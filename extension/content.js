// Content script: translates Chinese text on the page in place, automatically.
// Runs in every frame. Never reads or sends what the user types.
//
// Page content is only ever read as text and written back through
// Text.nodeValue / setAttribute: nothing from the page (or from a translation)
// is evaluated or parsed as HTML.

(() => {
  'use strict';

  if (globalThis.__autoTranslatorContent) return;
  globalThis.__autoTranslatorContent = true;

  const HAN = /\p{Script=Han}/u;
  const WORD = /[\p{L}\p{N}]/u;
  const ATTRS = ['placeholder', 'title', 'alt', 'aria-label'];
  const OBSERVED_ATTRS = [...ATTRS, 'value', 'label'];
  const BUTTON_INPUT_TYPES = new Set(['button', 'submit', 'reset']);
  const EDITABLE = '[contenteditable=""],[contenteditable="true"],[contenteditable="plaintext-only"]';
  const NO_TRANSLATE = '[translate="no"],.notranslate';
  // Text inside these elements is never translated.
  const TEXT_SKIP = 'script,style,noscript,textarea,code,pre,' + EDITABLE + ',' + NO_TRANSLATE;
  // Attributes of these elements (or their descendants) are never translated.
  const ATTR_SKIP = 'script,style,noscript,' + EDITABLE + ',' + NO_TRANSLATE;
  const XHTML = 'http://www.w3.org/1999/xhtml';
  const ON_DEVICE = 'chrome';

  const DEBOUNCE_MS = 150;
  const MAX_BATCH_TEXTS = 60;
  const MAX_BATCH_CHARS = 4000; // on-device translator
  const MAX_TEXT_CHARS = 20000; // longer single texts are not natural-language content
  const MAX_SCORED = 1000;
  const LOCAL_CACHE_CAPACITY = 3000;
  const TEMPLATE_CAPACITY = 300;
  const BACKOFF_START_MS = 1000;
  const BACKOFF_MAX_MS = 15000;
  const BACKOFF_NO_API_MS = 30000;
  // Request budget per frame: a burst large enough for very long pages, then a
  // slow refill, so a page that never stops changing cannot cause a request storm.
  const BUDGET_CAPACITY = 100;
  const BUDGET_REFILL_MS = 2000;
  // A node / container whose text is replaced this often is "hot" (countdown,
  // ticker, carousel): its texts are filled from number templates when possible.
  const HOT_WINDOW_MS = 10000;
  const HOT_THRESHOLD = 3;
  // Shadow roots attached after their host was scanned produce no mutation
  // record, so open hosts are looked for again a few times and after activity.
  const SWEEP_DELAYS_MS = [1000, 3000, 8000];
  const SWEEP_ACTIVITY_MS = 5000;

  // Per-node memory: what the page had and what we wrote.
  const textState = new WeakMap(); // Text -> { original, translated }
  const attrState = new WeakMap(); // Element -> { [attr]: { original, translated, tooltip? } }
  const pinnedOptions = new WeakMap(); // <option> -> value attribute we added
  const churn = new WeakMap(); // Node -> { count, last }

  const pendingText = new Set(); // Text nodes waiting for translation
  const pendingAttrs = new Map(); // Element -> Set<attr>
  const localCache = new Map(); // engine + "\n" + core text -> translation
  const templates = new Map(); // engine + "\n" + text with numbers blanked -> translation template

  let active = false;
  let generation = 0; // bumped on every stop, invalidates in-flight answers
  let inflight = false;
  let timer = null;
  let timerAt = 0; // when `timer` fires
  let notBefore = 0; // no request before this time (failure backoff)
  let backoff = BACKOFF_START_MS;
  let budget = BUDGET_CAPACITY;
  let budgetAt = Date.now();
  let observer = null;
  let observedRoots = new WeakSet();
  let sweepTimer = null;
  let sweepsDone = 0;
  let hotScan = false; // scanning nodes added to a hot container
  let pageEngine = ON_DEVICE; // engine that currently translates new page text
  let dead = false; // extension context invalidated (extension reloaded)

  // ------------------------------------------------------------------ helpers

  function splitWhitespace(value) {
    const m = /^(\s*)([\s\S]*?)(\s*)$/.exec(value);
    return { lead: m[1], core: m[2], trail: m[3] };
  }

  /** Shadow root of an element: open ones always, closed ones of custom elements too. */
  function shadowOf(el) {
    if (el.shadowRoot) return el.shadowRoot;
    if (el.namespaceURI === XHTML && el.localName.includes('-')) {
      try {
        return chrome.dom.openOrClosedShadowRoot(el) || null;
      } catch (e) { /* API missing or extension context gone */ }
    }
    return null;
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
    if (attr === 'label') {
      // Visible list labels only (<track label>, custom elements etc. are left alone).
      return (el.localName === 'optgroup' || el.localName === 'option') && el.hasAttribute('label');
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

  function hasPending() {
    return pendingText.size > 0 || pendingAttrs.size > 0;
  }

  // -------------------------------------------------------------------- cache

  function lruPut(map, capacity, key, value) {
    map.delete(key);
    map.set(key, value);
    if (map.size > capacity) map.delete(map.keys().next().value);
  }

  function cacheGet(core) {
    return localCache.get(pageEngine + '\n' + core);
  }

  // Number templates. "剩余 12 件" and "剩余 11 件" differ only in numbers; once
  // one of them is translated and every number shows up unchanged, exactly
  // once, in the translation, the other one can be filled in without a request.
  // Only used for hot nodes, where the alternative is a request per tick.
  const NUMBER = /\d+(?:[.,:]\d+)*/g;
  const SLOT = '\u0001';

  function learnTemplate(engine, core, translation) {
    const numbers = core.match(NUMBER);
    if (!numbers || new Set(numbers).size !== numbers.length) return;
    const found = translation.match(NUMBER) || [];
    if (found.length !== numbers.length || !found.every((n) => numbers.includes(n))) return;
    if (new Set(found).size !== found.length) return;
    if (core.includes(SLOT) || translation.includes(SLOT)) return;
    const template = translation.replace(NUMBER, (n) => SLOT + numbers.indexOf(n) + SLOT);
    lruPut(templates, TEMPLATE_CAPACITY, engine + '\n' + core.replace(NUMBER, SLOT), template);
  }

  function fillTemplate(core) {
    const numbers = core.match(NUMBER);
    if (!numbers) return undefined;
    const template = templates.get(pageEngine + '\n' + core.replace(NUMBER, SLOT));
    if (template === undefined) return undefined;
    return template.replace(/\u0001(\d+)\u0001/g, (all, i) => (numbers[i] === undefined ? '' : numbers[i]));
  }

  function bumpChurn(node) {
    const now = Date.now();
    const entry = churn.get(node);
    // Stays hot for as long as the changes keep coming; cools down after a quiet window.
    if (!entry || now - entry.last > HOT_WINDOW_MS) churn.set(node, { count: 1, last: now });
    else { entry.count++; entry.last = now; }
  }

  function isHot(node) {
    const entry = churn.get(node);
    return !!entry && entry.count >= HOT_THRESHOLD && Date.now() - entry.last <= HOT_WINDOW_MS;
  }

  /** Translation available without a request, or undefined. */
  function lookup(core, hot) {
    const cached = cacheGet(core);
    if (cached !== undefined || !hot) return cached;
    return fillTemplate(core);
  }

  // ------------------------------------------------------------------ writing

  function isInline(el) {
    try {
      return getComputedStyle(el).display === 'inline';
    } catch (e) {
      return false;
    }
  }

  /**
   * The character right before (or after) a text node on the same line, looking
   * through inline elements only; '' at a block boundary or when unknown.
   */
  function neighbourChar(node, forward) {
    let current = node;
    for (let depth = 0; depth < 4; depth++) {
      let sibling = forward ? current.nextSibling : current.previousSibling;
      while (sibling && (sibling.nodeType === 8 || (sibling.nodeType === 3 && !sibling.nodeValue))) {
        sibling = forward ? sibling.nextSibling : sibling.previousSibling;
      }
      if (sibling) {
        let text;
        if (sibling.nodeType === 3) text = sibling.nodeValue;
        else if (sibling.nodeType === 1 && isInline(sibling)) text = sibling.textContent;
        if (!text) return '';
        return forward ? text[0] : text[text.length - 1];
      }
      const parent = current.parentNode;
      if (!parent || parent.nodeType !== 1 || !isInline(parent)) return '';
      current = parent;
    }
    return '';
  }

  function needsGap(ch) {
    return !!ch && WORD.test(ch) && !HAN.test(ch);
  }

  /**
   * Chinese needs no spaces between words, so shops write
   * "<b>满</b>300<b>减</b>50" or "<span>免费</span><span>送货</span>". After
   * translation the pieces would run together ("Free" + "shipping", "Over300"),
   * so a space is added where a translated piece touches a letter or digit.
   */
  function withGaps(node, lead, translation, trail) {
    let text = translation;
    if (!lead && WORD.test(text[0]) && needsGap(neighbourChar(node, false))) text = ' ' + text;
    if (!trail && WORD.test(text[text.length - 1]) && needsGap(neighbourChar(node, true))) text += ' ';
    return lead + text + trail;
  }

  /** <option> without a value attribute submits its text: keep the original. */
  function pinOptionValue(node) {
    const option = node.parentNode;
    if (!option || option.localName !== 'option' || option.hasAttribute('value')) return;
    const value = option.value;
    pinnedOptions.set(option, value);
    option.setAttribute('value', value);
  }

  function unpinOptionValue(node) {
    const option = node.parentNode;
    if (!option || option.nodeType !== 1 || !pinnedOptions.has(option)) return;
    if (option.getAttribute('value') === pinnedOptions.get(option)) option.removeAttribute('value');
    pinnedOptions.delete(option);
  }

  function applyText(node, translation) {
    const value = node.nodeValue;
    const { lead, core, trail } = splitWhitespace(value);
    // An empty or identical answer is remembered too, so it is not asked again.
    const next = translation && translation.trim() ? withGaps(node, lead, translation, trail) : value;
    textState.set(node, { original: value, translated: next });
    if (next === value) return;
    pinOptionValue(node);
    node.nodeValue = next;
    setTooltip(node.parentElement, core);
  }

  /** Shows the original text as tooltip unless the element has its own title. */
  function setTooltip(el, original) {
    // HTML only: a title attribute means nothing on SVG elements.
    if (!el || el.nodeType !== 1 || el.namespaceURI !== XHTML || isPasswordField(el)) return;
    let states = attrState.get(el);
    const own = states && states.title;
    if (el.hasAttribute('title') && !(own && own.tooltip)) return;
    if (!states) attrState.set(el, (states = {}));
    states.title = { original: null, translated: original, tooltip: true };
    if (el.getAttribute('title') !== original) el.setAttribute('title', original);
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
      unpinOptionValue(node);
    }
    if (!value || value.length > MAX_TEXT_CHARS || !HAN.test(value)) { pendingText.delete(node); return; }
    if (closestDeep(node.parentNode, TEXT_SKIP)) return;
    // Shared with compose.js (same isolated world): this frame shows Chinese text.
    globalThis.__autoTranslatorSawChinese = true;
    const known = lookup(splitWhitespace(value).core, hotScan || isHot(node));
    if (known !== undefined) { pendingText.delete(node); applyText(node, known); }
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
    if (!value || value.length > MAX_TEXT_CHARS || !HAN.test(value)) return;
    const cached = cacheGet(splitWhitespace(value).core);
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
    const name = el.localName;
    if (name === 'input') considerAttr(el, 'value');
    else if (name === 'optgroup' || name === 'option') considerAttr(el, 'label');
    const shadow = shadowOf(el);
    if (shadow) scan(shadow);
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
    if (type === 11 && root.host && closestDeep(root.host, ATTR_SKIP)) return;
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
        const node = record.target;
        if (node.nodeType !== 3) continue;
        const state = textState.get(node);
        if (!state || state.translated !== node.nodeValue) bumpChurn(node); // not our own write
        considerText(node);
      } else if (record.type === 'childList') {
        if (record.addedNodes.length === 0) continue;
        // Content that is replaced over and over (innerHTML tickers, carousels).
        if (record.removedNodes.length > 0) bumpChurn(record.target);
        hotScan = isHot(record.target);
        try {
          for (const node of record.addedNodes) if (node.isConnected) scan(node);
        } finally {
          hotScan = false;
        }
      } else if (record.type === 'attributes') {
        const el = record.target;
        if (!closestDeep(el, ATTR_SKIP)) considerAttr(el, record.attributeName);
      }
    }
    if (hasPending()) schedule(DEBOUNCE_MS);
    scheduleSweep(SWEEP_ACTIVITY_MS);
  }

  // ------------------------------------------------- late shadow roots (sweep)

  function scheduleSweep(delay) {
    if (sweepTimer !== null || !active) return;
    sweepTimer = setTimeout(() => {
      const run = () => { sweepTimer = null; sweepShadowRoots(); };
      if (typeof requestIdleCallback === 'function') requestIdleCallback(run, { timeout: 2000 });
      else run();
    }, delay);
  }

  /** Finds shadow roots that appeared after their host had been scanned. */
  function sweepShadowRoots() {
    if (!active) return;
    const visit = (root) => {
      const walker = document.createTreeWalker(root, NodeFilter.SHOW_ELEMENT);
      let el;
      while ((el = walker.nextNode())) {
        const shadow = shadowOf(el);
        if (!shadow) continue;
        if (observedRoots.has(shadow)) visit(shadow);
        else scan(shadow);
      }
    };
    visit(document);
    if (hasPending()) schedule(0);
    if (++sweepsDone < SWEEP_DELAYS_MS.length) scheduleSweep(SWEEP_DELAYS_MS[sweepsDone]);
  }

  // ----------------------------------------------------------------- batching

  /**
   * Runs flush after `delay` ms. An already planned, earlier flush is kept, so
   * a page that mutates all the time cannot postpone translation forever, and
   * nothing runs before `notBefore` (backoff after a failed request).
   */
  function schedule(delay) {
    const now = Date.now();
    const at = Math.max(now + delay, notBefore);
    if (timer !== null && timerAt <= at) return;
    clearTimeout(timer);
    timerAt = at;
    timer = setTimeout(() => { timer = null; flush(); }, at - now);
  }

  function cancelTimers() {
    clearTimeout(timer);
    timer = null;
    clearTimeout(sweepTimer);
    sweepTimer = null;
  }

  /** Takes one request from the budget; returns the wait in ms when it is empty. */
  function takeBudget() {
    const now = Date.now();
    const refill = Math.floor((now - budgetAt) / BUDGET_REFILL_MS);
    if (refill > 0) {
      budget = Math.min(BUDGET_CAPACITY, budget + refill);
      budgetAt += refill * BUDGET_REFILL_MS;
    }
    if (budget > 0) { budget--; return 0; }
    return Math.max(50, budgetAt + BUDGET_REFILL_MS - now);
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
      const core = splitWhitespace(value).core;
      // Translated meanwhile through another node, or the engine changed.
      const known = lookup(core, isHot(node));
      if (known !== undefined) {
        pendingText.delete(node);
        applyText(node, known);
        continue;
      }
      out.push({ node, el: node.parentElement, core });
    }
    for (const [el, attrs] of pendingAttrs) {
      for (const attr of attrs) {
        const value = el.isConnected && attrEligible(el, attr) ? getAttr(el, attr) : null;
        const state = attrState.get(el) && attrState.get(el)[attr];
        if (!value || !HAN.test(value) || (state && state.translated === value)) {
          attrs.delete(attr);
          continue;
        }
        const core = splitWhitespace(value).core;
        const known = cacheGet(core);
        if (known !== undefined) {
          attrs.delete(attr);
          applyAttr(el, attr, known);
          continue;
        }
        out.push({ el, attr, core });
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
    if (Date.now() < notBefore) { schedule(0); return; }
    const candidates = collectCandidates();
    if (candidates.length === 0) return;

    const wait = takeBudget();
    if (wait > 0) { schedule(wait); return; }

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
      if (hasPending()) schedule(0);
      return;
    }

    if (response && response.ok && Array.isArray(response.translations) && response.translations.length === texts.length) {
      backoff = BACKOFF_START_MS;
      notBefore = 0;
      // Cached under the engine that really produced it (a batch may have
      // fallen back to the on-device translator).
      const engine = typeof response.engine === 'string' ? response.engine : ON_DEVICE;
      texts.forEach((core, i) => {
        const translation = String(response.translations[i] == null ? '' : response.translations[i]);
        lruPut(localCache, LOCAL_CACHE_CAPACITY, engine + '\n' + core, translation);
        learnTemplate(engine, core, translation);
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
      if (hasPending()) schedule(0);
    } else {
      // Engine not ready (language pack missing, starting up, ...): keep the
      // queue and try again later. A "ready" broadcast resumes immediately.
      const delay = response && response.error === 'no-api' ? BACKOFF_NO_API_MS : backoff;
      backoff = Math.min(backoff * 2, BACKOFF_MAX_MS);
      notBefore = Date.now() + delay;
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
          unpinOptionValue(node);
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
        const shadow = shadowOf(node);
        if (shadow) restoreAll(shadow);
      }
      node = walker.nextNode();
    }
  }

  function start() {
    if (active || dead) return;
    active = true;
    backoff = BACKOFF_START_MS;
    notBefore = 0;
    observer = new MutationObserver(onMutations);
    observedRoots = new WeakSet();
    scan(document);
    if (hasPending()) schedule(0);
    sweepsDone = 0;
    scheduleSweep(SWEEP_DELAYS_MS[0]);
  }

  function stop() {
    if (!active) return;
    active = false;
    generation++;
    cancelTimers();
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
    cancelTimers();
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

  /** Pages are translatable when the on-device pack is there OR the app offers its engine. */
  function pagesReady(state) {
    return !!state && (state.pagesReady === true || state.ready === true);
  }

  function engineOf(state) {
    return state && typeof state.pageEngine === 'string' && state.pageEngine ? state.pageEngine : ON_DEVICE;
  }

  function onEngineState(now, before) {
    const engineChanged = engineOf(now) !== pageEngine;
    // Text that is already translated stays as it is; only new text uses the new engine.
    pageEngine = engineOf(now);
    if (!active) return;
    if (engineChanged || (pagesReady(now) && !pagesReady(before))) {
      // "Engine ready" broadcast: resume a waiting queue immediately.
      backoff = BACKOFF_START_MS;
      notBefore = 0;
      if (hasPending()) schedule(0);
    }
  }

  async function init() {
    try {
      chrome.storage.onChanged.addListener((changes, area) => {
        if (dead) return;
        if (area === 'sync' && (changes.enabled || changes.disabledHosts)) {
          loadSettings();
        } else if (area === 'local' && changes.engineState) {
          onEngineState(changes.engineState.newValue, changes.engineState.oldValue);
        }
      });
    } catch (e) {
      return; // extension context already gone
    }
    try {
      pageEngine = engineOf((await chrome.storage.local.get('engineState')).engineState);
    } catch (e) { /* default engine */ }
    loadSettings();
  }

  if (document.documentElement) init();
  else document.addEventListener('DOMContentLoaded', init, { once: true });
})();
