// Content script: translates Chinese text on the page in place, automatically.
// Runs in every frame. Never reads or sends what the user types.

(() => {
  'use strict';

  if (globalThis.__autoTranslatorContent) return;
  globalThis.__autoTranslatorContent = true;

  const HAN = /\p{Script=Han}/u;
  const ATTRS = ['placeholder', 'title', 'alt', 'aria-label'];
  const OBSERVED_ATTRS = [...ATTRS, 'value'];
  const BUTTON_INPUT_TYPES = new Set(['button', 'submit', 'reset']);
  const EDITABLE = '[contenteditable=""],[contenteditable="true"],[contenteditable="plaintext-only"]';
  const NO_TRANSLATE = '[translate="no"],.notranslate';
  // Text inside these elements is never translated.
  const TEXT_SKIP = 'script,style,noscript,textarea,code,pre,' + EDITABLE + ',' + NO_TRANSLATE;
  // Attributes of these elements (or their descendants) are never translated.
  const ATTR_SKIP = 'script,style,noscript,' + EDITABLE + ',' + NO_TRANSLATE;

  const DEBOUNCE_MS = 150;
  const MAX_BATCH_TEXTS = 60;
  const MAX_BATCH_CHARS = 4000;
  const MAX_SCORED = 1000;
  const LOCAL_CACHE_CAPACITY = 3000;
  const BACKOFF_START_MS = 1000;
  const BACKOFF_MAX_MS = 15000;
  const BACKOFF_NO_API_MS = 30000;

  // Per-node memory: what the page had and what we wrote.
  const textState = new WeakMap(); // Text -> { original, translated }
  const attrState = new WeakMap(); // Element -> { [attr]: { original, translated, tooltip? } }

  const pendingText = new Set(); // Text nodes waiting for translation
  const pendingAttrs = new Map(); // Element -> Set<attr>
  const localCache = new Map(); // core text -> translation

  let active = false;
  let generation = 0; // bumped on every stop, invalidates in-flight answers
  let inflight = false;
  let timer = null;
  let backoff = BACKOFF_START_MS;
  let observer = null;
  let observedRoots = new WeakSet();
  let dead = false; // extension context invalidated (extension reloaded)

  // ------------------------------------------------------------------ helpers

  function splitWhitespace(value) {
    const m = /^(\s*)([\s\S]*?)(\s*)$/.exec(value);
    return { lead: m[1], core: m[2], trail: m[3] };
  }

  /** closest() that also climbs out of shadow roots. */
  function closestDeep(el, selector) {
    while (el) {
      if (el.nodeType === 1) {
        const hit = el.closest(selector);
        if (hit) return hit;
      }
      const root = el.getRootNode();
      el = root && root.host ? root.host : null;
    }
    return null;
  }

  function isPasswordField(el) {
    return el.localName === 'input' && String(el.type).toLowerCase() === 'password';
  }

  function attrEligible(el, attr) {
    if (isPasswordField(el)) return false;
    if (attr === 'value') {
      return el.localName === 'input' && BUTTON_INPUT_TYPES.has(String(el.type).toLowerCase());
    }
    return el.hasAttribute(attr);
  }

  function getAttr(el, attr) {
    return attr === 'value' ? el.value : el.getAttribute(attr);
  }

  function setAttr(el, attr, value) {
    if (attr === 'value') el.value = value;
    else el.setAttribute(attr, value);
  }

  function cachePut(core, translation) {
    localCache.delete(core);
    localCache.set(core, translation);
    if (localCache.size > LOCAL_CACHE_CAPACITY) localCache.delete(localCache.keys().next().value);
  }

  // ------------------------------------------------------------------ writing

  function applyText(node, translation) {
    const value = node.nodeValue;
    const { lead, core, trail } = splitWhitespace(value);
    // An empty or identical answer is remembered too, so it is not asked again.
    const next = translation && translation.trim() ? lead + translation + trail : value;
    textState.set(node, { original: value, translated: next });
    if (next === value) return;
    node.nodeValue = next;
    setTooltip(node.parentElement, core);
  }

  /** Shows the original text as tooltip unless the element has its own title. */
  function setTooltip(el, original) {
    if (!el || el.nodeType !== 1 || isPasswordField(el)) return;
    let states = attrState.get(el);
    const own = states && states.title;
    if (el.hasAttribute('title') && !(own && own.tooltip)) return;
    if (!states) attrState.set(el, (states = {}));
    states.title = { original: null, translated: original, tooltip: true };
    el.setAttribute('title', original);
  }

  function applyAttr(el, attr, translation) {
    const value = getAttr(el, attr);
    const { lead, trail } = splitWhitespace(value);
    const next = translation && translation.trim() ? lead + translation + trail : value;
    let states = attrState.get(el);
    if (!states) attrState.set(el, (states = {}));
    states[attr] = { original: value, translated: next };
    if (next !== value) setAttr(el, attr, next);
  }

  // ----------------------------------------------------------------- scanning

  function considerText(node) {
    const value = node.nodeValue;
    const state = textState.get(node);
    if (state) {
      if (value === state.translated) return; // our own write
      textState.delete(node); // the page changed it again
    }
    if (!value || !HAN.test(value)) { pendingText.delete(node); return; }
    if (closestDeep(node.parentNode, TEXT_SKIP)) return;
    const cached = localCache.get(splitWhitespace(value).core);
    if (cached !== undefined) applyText(node, cached);
    else pendingText.add(node);
  }

  function considerAttr(el, attr) {
    if (!attrEligible(el, attr)) return;
    const value = getAttr(el, attr);
    const states = attrState.get(el);
    const state = states && states[attr];
    if (state) {
      if (value === state.translated) return; // our own write (or our tooltip)
      delete states[attr];
    }
    if (!value || !HAN.test(value)) return;
    const cached = localCache.get(splitWhitespace(value).core);
    if (cached !== undefined) {
      applyAttr(el, attr, cached);
    } else {
      let set = pendingAttrs.get(el);
      if (!set) pendingAttrs.set(el, (set = new Set()));
      set.add(attr);
    }
  }

  function visitElement(el) {
    for (const attr of ATTRS) if (el.hasAttribute(attr)) considerAttr(el, attr);
    if (el.localName === 'input') considerAttr(el, 'value');
    if (el.shadowRoot) scan(el.shadowRoot);
  }

  function observeRoot(root) {
    if (!observer || observedRoots.has(root)) return;
    observedRoots.add(root);
    observer.observe(root, {
      childList: true,
      subtree: true,
      characterData: true,
      attributes: true,
      attributeFilter: OBSERVED_ATTRS,
    });
  }

  /** Collects everything translatable below root (inclusive). */
  function scan(root) {
    const type = root.nodeType;
    if (type === 3) { considerText(root); return; }
    if (type !== 1 && type !== 9 && type !== 11) return;
    if (type === 9 || type === 11) observeRoot(root);
    if (type === 1) {
      if (closestDeep(root, ATTR_SKIP)) return;
      visitElement(root);
      if (closestDeep(root, TEXT_SKIP)) return;
    }
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT, {
      acceptNode(node) {
        if (node.nodeType !== 1) return NodeFilter.FILTER_ACCEPT;
        if (node.matches(ATTR_SKIP)) return NodeFilter.FILTER_REJECT;
        visitElement(node); // attributes (e.g. textarea placeholder) and shadow roots
        return node.matches(TEXT_SKIP) ? NodeFilter.FILTER_REJECT : NodeFilter.FILTER_SKIP;
      },
    });
    let node;
    while ((node = walker.nextNode())) considerText(node);
  }

  function onMutations(records) {
    if (!active) return;
    for (const record of records) {
      if (record.type === 'characterData') {
        if (record.target.nodeType === 3) considerText(record.target);
      } else if (record.type === 'childList') {
        for (const node of record.addedNodes) if (node.isConnected) scan(node);
      } else if (record.type === 'attributes') {
        const el = record.target;
        if (!closestDeep(el, ATTR_SKIP)) considerAttr(el, record.attributeName);
      }
    }
    if (pendingText.size || pendingAttrs.size) schedule(DEBOUNCE_MS);
  }

  // ----------------------------------------------------------------- batching

  function schedule(delay) {
    clearTimeout(timer);
    timer = setTimeout(flush, delay);
  }

  /** Distance of an element from the viewport in px (0 = visible). */
  function viewportDistance(el) {
    if (!el || !el.getBoundingClientRect) return 3000;
    const r = el.getBoundingClientRect();
    if (r.width === 0 && r.height === 0) return 3000; // not rendered
    const dy = r.bottom < 0 ? -r.bottom : r.top > innerHeight ? r.top - innerHeight : 0;
    const dx = r.right < 0 ? -r.right : r.left > innerWidth ? r.left - innerWidth : 0;
    return dx + dy;
  }

  /** Validates the pending sets and returns the still relevant candidates. */
  function collectCandidates() {
    const out = [];
    for (const node of pendingText) {
      const value = node.nodeValue;
      const state = textState.get(node);
      if (!node.isConnected || !value || !HAN.test(value) || (state && state.translated === value)) {
        pendingText.delete(node);
        continue;
      }
      out.push({ node, el: node.parentElement, core: splitWhitespace(value).core });
    }
    for (const [el, attrs] of pendingAttrs) {
      for (const attr of attrs) {
        const value = el.isConnected && attrEligible(el, attr) ? getAttr(el, attr) : null;
        const state = attrState.get(el) && attrState.get(el)[attr];
        if (!value || !HAN.test(value) || (state && state.translated === value)) {
          attrs.delete(attr);
          continue;
        }
        out.push({ el, attr, core: splitWhitespace(value).core });
      }
      if (attrs.size === 0) pendingAttrs.delete(el);
    }
    return out;
  }

  function isInvalidated(error) {
    return !chrome.runtime || !chrome.runtime.id || /context invalidated/i.test(String(error && error.message));
  }

  async function flush() {
    if (!active || inflight || dead) return;
    const candidates = collectCandidates();
    if (candidates.length === 0) return;

    // Visible things first (only worth the layout cost when not everything fits).
    if (candidates.length > MAX_BATCH_TEXTS) {
      const scores = new Map();
      candidates.forEach((c, i) => scores.set(c, i < MAX_SCORED ? viewportDistance(c.el) : 1e9));
      candidates.sort((a, b) => scores.get(a) - scores.get(b));
    }

    // Unique strings, limited per batch.
    const groups = new Map(); // core -> candidates
    let chars = 0;
    for (const c of candidates) {
      const group = groups.get(c.core);
      if (group) { group.push(c); continue; }
      if (groups.size >= MAX_BATCH_TEXTS) continue;
      if (groups.size > 0 && chars + c.core.length > MAX_BATCH_CHARS) continue;
      groups.set(c.core, [c]);
      chars += c.core.length;
    }
    const texts = [...groups.keys()];

    const myGeneration = generation;
    inflight = true;
    let response = null;
    try {
      response = await chrome.runtime.sendMessage({ type: 'translate', source: 'zh', target: 'en', texts });
    } catch (e) {
      if (isInvalidated(e)) { shutdown(); return; }
    } finally {
      inflight = false;
    }
    if (!active) return;
    if (myGeneration !== generation) {
      // Switched off and on again while waiting: the answer is stale.
      if (pendingText.size || pendingAttrs.size) schedule(0);
      return;
    }

    if (response && response.ok && Array.isArray(response.translations) && response.translations.length === texts.length) {
      backoff = BACKOFF_START_MS;
      texts.forEach((core, i) => {
        const translation = String(response.translations[i] == null ? '' : response.translations[i]);
        cachePut(core, translation);
        for (const c of groups.get(core)) {
          if (c.node) {
            pendingText.delete(c.node);
            // Only write if the page did not change the text meanwhile.
            if (c.node.isConnected && splitWhitespace(c.node.nodeValue).core === core) applyText(c.node, translation);
            else if (c.node.isConnected) considerText(c.node);
          } else {
            const attrs = pendingAttrs.get(c.el);
            if (attrs) { attrs.delete(c.attr); if (attrs.size === 0) pendingAttrs.delete(c.el); }
            if (c.el.isConnected && attrEligible(c.el, c.attr)) {
              if (splitWhitespace(getAttr(c.el, c.attr) || '').core === core) applyAttr(c.el, c.attr, translation);
              else considerAttr(c.el, c.attr);
            }
          }
        }
      });
      if (pendingText.size || pendingAttrs.size) schedule(0);
    } else {
      // Engine not ready (language pack missing, starting up, ...): keep the
      // queue and try again later. A "ready" broadcast resumes immediately.
      const delay = response && response.error === 'no-api' ? BACKOFF_NO_API_MS : backoff;
      backoff = Math.min(backoff * 2, BACKOFF_MAX_MS);
      schedule(delay);
    }
  }

  // ------------------------------------------------------------ start / stop

  function restoreAll(root) {
    const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT | NodeFilter.SHOW_TEXT);
    let node = root;
    while (node) {
      if (node.nodeType === 3) {
        const state = textState.get(node);
        if (state) {
          if (node.nodeValue === state.translated) node.nodeValue = state.original;
          textState.delete(node);
        }
      } else if (node.nodeType === 1) {
        const states = attrState.get(node);
        if (states) {
          for (const attr of Object.keys(states)) {
            const state = states[attr];
            if (getAttr(node, attr) !== state.translated) continue;
            if (state.tooltip) node.removeAttribute('title');
            else setAttr(node, attr, state.original);
          }
          attrState.delete(node);
        }
        if (node.shadowRoot) restoreAll(node.shadowRoot);
      }
      node = walker.nextNode();
    }
  }

  function start() {
    if (active || dead) return;
    active = true;
    backoff = BACKOFF_START_MS;
    observer = new MutationObserver(onMutations);
    observedRoots = new WeakSet();
    scan(document);
    if (pendingText.size || pendingAttrs.size) schedule(0);
  }

  function stop() {
    if (!active) return;
    active = false;
    generation++;
    clearTimeout(timer);
    if (observer) { observer.disconnect(); observer = null; }
    pendingText.clear();
    pendingAttrs.clear();
    restoreAll(document);
  }

  /** The extension was reloaded or removed: this script instance is orphaned. */
  function shutdown() {
    dead = true;
    active = false;
    generation++;
    clearTimeout(timer);
    if (observer) { observer.disconnect(); observer = null; }
    pendingText.clear();
    pendingAttrs.clear();
  }

  // ----------------------------------------------------------------- settings

  function hostnames() {
    const names = new Set();
    if (location.hostname) names.add(location.hostname);
    try {
      const origins = location.ancestorOrigins;
      if (origins && origins.length) names.add(new URL(origins[origins.length - 1]).hostname);
    } catch (e) { /* opaque origin */ }
    try {
      if (window.top.location.hostname) names.add(window.top.location.hostname);
    } catch (e) { /* cross-origin top frame */ }
    return names;
  }

  function applySettings(settings) {
    const enabled = settings.enabled !== false;
    const disabledHosts = Array.isArray(settings.disabledHosts) ? settings.disabledHosts : [];
    const names = hostnames();
    const siteOff = disabledHosts.some((h) => names.has(h));
    if (enabled && !siteOff) start();
    else stop();
  }

  async function loadSettings() {
    try {
      applySettings(await chrome.storage.sync.get(['enabled', 'disabledHosts']));
    } catch (e) {
      if (isInvalidated(e)) shutdown();
      else applySettings({});
    }
  }

  function init() {
    try {
      chrome.storage.onChanged.addListener((changes, area) => {
        if (dead) return;
        if (area === 'sync' && (changes.enabled || changes.disabledHosts)) {
          loadSettings();
        } else if (area === 'local' && changes.engineState) {
          // "Engine ready" broadcast from the service worker.
          const now = changes.engineState.newValue;
          const before = changes.engineState.oldValue;
          if (active && now && now.ready && !(before && before.ready)) {
            backoff = BACKOFF_START_MS;
            if (pendingText.size || pendingAttrs.size) schedule(0);
          }
        }
      });
    } catch (e) {
      return; // extension context already gone
    }
    loadSettings();
  }

  if (document.documentElement) init();
  else document.addEventListener('DOMContentLoaded', init, { once: true });
})();
