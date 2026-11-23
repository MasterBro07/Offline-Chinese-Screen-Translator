// Writing helper: on pages with Chinese text, type English into a text field and get the
// Chinese translation shown below it; "Insert" (or Ctrl+Shift+Enter) puts it into the field.
//
// Privacy: the typed text is translated on this device (Chrome's Translator API, en -> zh),
// never stored, and only written into the field when the user asks for it.
// Nothing from the page is parsed as HTML; the bar lives in its own shadow root, so page CSS
// cannot change it, and the page translation (content.js) skips it.

(() => {
  'use strict';

  if (globalThis.__autoTranslatorCompose) return;
  globalThis.__autoTranslatorCompose = true;

  const HAN = /\p{Script=Han}/u;
  const LATIN = /[A-Za-z]/;
  const DEBOUNCE_MS = 400;
  const MAX_CHARS = 2000;
  const TEXT_INPUT_TYPES = new Set(['', 'text', 'search']);
  // autocomplete tokens of fields that must never be touched.
  const SECRET_AUTOCOMPLETE = /\b(cc-[a-z-]+|one-time-code|current-password|new-password)\b/i;

  let enabled = true; // user setting "compose"
  let siteOff = false;
  let writeReady = false;
  let chinesePage = false;

  let field = null; // the field the bar belongs to
  let host = null; // bar host element (in the page)
  let ui = null; // { bar, text, insert, close }
  let translation = ''; // latest translation for the field's current text
  let translatedFor = '';
  let timer = 0;
  let seq = 0;
  const dismissed = new WeakSet(); // fields closed with "×" until reload

  // ------------------------------------------------------------------ helpers

  function isInvalidated(e) {
    return /context invalidated/i.test(String(e && e.message || e));
  }

  function pageLooksChinese() {
    if (chinesePage || globalThis.__autoTranslatorSawChinese) return true;
    const lang = (document.documentElement && document.documentElement.lang) || '';
    if (/^zh\b/i.test(lang)) return true;
    const sample = document.body ? (document.body.textContent || '').slice(0, 20000) : '';
    return HAN.test(sample) || HAN.test(document.title || '');
  }

  /** The editable element a focus/input event belongs to, or null if it is not a candidate. */
  function editableFrom(target) {
    let el = target;
    if (!el || el.nodeType !== 1) return null;
    if (el.localName === 'textarea') return el.readOnly || el.disabled ? null : el;
    if (el.localName === 'input') {
      const type = String(el.getAttribute('type') || '').toLowerCase();
      if (!TEXT_INPUT_TYPES.has(type) || el.readOnly || el.disabled) return null;
      return el;
    }
    if (el.isContentEditable) {
      // Use the editing host (the outermost contenteditable element).
      while (el.parentElement && el.parentElement.isContentEditable) el = el.parentElement;
      return el;
    }
    return null;
  }

  function isSecret(el) {
    const auto = el.getAttribute && el.getAttribute('autocomplete');
    return !!auto && SECRET_AUTOCOMPLETE.test(auto);
  }

  function fieldText(el) {
    return el.isContentEditable ? (el.innerText || '') : String(el.value || '');
  }

  // ---------------------------------------------------------------------- bar

  function buildUi() {
    host = document.createElement('auto-translator-compose');
    host.setAttribute('translate', 'no'); // content.js must not translate the preview back
    host.style.cssText = 'all: initial; position: fixed; z-index: 2147483647; top: 0; left: 0;';
    const root = host.attachShadow({ mode: 'open' });
    const style = document.createElement('style');
    style.textContent = `
      .bar { box-sizing: border-box; display: flex; align-items: center; gap: 8px; max-width: 520px;
        padding: 6px 8px; border-radius: 8px; background: #1f2328; color: #fff;
        font: 14px/1.35 system-ui, -apple-system, "Segoe UI", sans-serif;
        box-shadow: 0 4px 14px rgba(0,0,0,.25); }
      .text { flex: 1; min-width: 0; white-space: pre-wrap; overflow-wrap: anywhere;
        max-height: 8.5em; overflow: auto; }
      .text.muted { color: #c9d1d9; font-size: 13px; }
      button { font: inherit; border: 0; border-radius: 6px; cursor: pointer; }
      .insert { padding: 4px 10px; background: #2f81f7; color: #fff; font-weight: 600; white-space: nowrap; }
      .insert:disabled { opacity: .5; cursor: default; }
      .hint { color: #8b949e; font-size: 11px; white-space: nowrap; }
      .close { padding: 0 6px; background: transparent; color: #8b949e; font-size: 16px; }
      .close:hover { color: #fff; }
      @media (max-width: 480px) { .hint { display: none; } }
    `;
    const bar = document.createElement('div');
    bar.className = 'bar';
    bar.setAttribute('role', 'status');
    const text = document.createElement('span');
    text.className = 'text';
    text.lang = 'zh';
    const insert = document.createElement('button');
    insert.className = 'insert';
    insert.type = 'button';
    insert.textContent = 'Insert';
    const hint = document.createElement('span');
    hint.className = 'hint';
    hint.textContent = 'Ctrl+Shift+Enter';
    const close = document.createElement('button');
    close.className = 'close';
    close.type = 'button';
    close.title = 'Hide the writing helper for this field';
    close.textContent = '×';
    bar.append(text, insert, hint, close);
    root.append(style, bar);

    // Keep the focus in the field when the bar is clicked.
    bar.addEventListener('mousedown', (e) => e.preventDefault());
    insert.addEventListener('click', () => insertTranslation());
    close.addEventListener('click', () => {
      if (field) dismissed.add(field);
      hide();
    });
    ui = { bar, text, insert, close };
  }

  function position() {
    if (!host || !field) return;
    const r = field.getBoundingClientRect();
    if (r.width === 0 && r.height === 0) { hide(); return; }
    const barHeight = ui.bar.offsetHeight || 36;
    const below = r.bottom + 4;
    const top = below + barHeight > innerHeight && r.top - barHeight - 4 > 0 ? r.top - barHeight - 4 : below;
    const barWidth = ui.bar.offsetWidth || Math.min(520, innerWidth - 8);
    const left = Math.max(4, Math.min(r.left, innerWidth - barWidth - 4));
    host.style.top = Math.round(top) + 'px';
    host.style.left = Math.round(left) + 'px';
  }

  function show(message, isTranslation) {
    if (!ui) buildUi();
    if (!host.isConnected) (document.body || document.documentElement).append(host);
    ui.text.textContent = message;
    ui.text.classList.toggle('muted', !isTranslation);
    ui.text.lang = isTranslation ? 'zh' : 'en';
    ui.insert.disabled = !isTranslation;
    position();
  }

  function hide() {
    clearTimeout(timer);
    seq++;
    translation = '';
    translatedFor = '';
    if (host && host.isConnected) host.remove();
  }

  // ---------------------------------------------------------------- translate

  function active() {
    return enabled && !siteOff;
  }

  function update() {
    clearTimeout(timer);
    if (!field || !active() || dismissed.has(field) || !field.isConnected) { hide(); return; }
    const text = fieldText(field).trim();
    // Only English (Latin) input without Chinese characters is translated.
    if (!text || text.length > MAX_CHARS || !LATIN.test(text) || HAN.test(text)) { hide(); return; }
    if (!writeReady) {
      show('The writing helper needs a language pack: click the extension icon → “Download language packs”.', false);
      return;
    }
    if (text === translatedFor && translation) { show(translation, true); return; }
    if (host && host.isConnected) ui.insert.disabled = true;
    const mine = ++seq;
    timer = setTimeout(() => translate(text, mine), DEBOUNCE_MS);
  }

  async function translate(text, mine) {
    let response;
    try {
      response = await chrome.runtime.sendMessage({ type: 'translate', source: 'en', target: 'zh', texts: [text] });
    } catch (e) {
      if (isInvalidated(e)) shutdown();
      return;
    }
    if (mine !== seq || !field) return; // the user kept typing
    if (response && response.ok && Array.isArray(response.translations) && typeof response.translations[0] === 'string') {
      translation = response.translations[0].trim();
      translatedFor = text;
      show(translation, true);
    } else {
      show('Translation not possible right now.', false);
    }
  }

  // ------------------------------------------------------------------- insert

  function selectAll(el) {
    if (el.isContentEditable) {
      const range = document.createRange();
      range.selectNodeContents(el);
      const sel = getSelection();
      sel.removeAllRanges();
      sel.addRange(range);
    } else {
      el.select();
    }
  }

  function insertTranslation() {
    const el = field;
    const zh = translation;
    if (!el || !zh || fieldText(el).trim() !== translatedFor) return;
    el.focus();
    selectAll(el);
    // insertText fires real input events, so frameworks (React, Vue …) see the change.
    let done = false;
    try { done = document.execCommand('insertText', false, zh); } catch (e) { done = false; }
    if (!done || fieldText(el).trim() !== zh) {
      if (el.isContentEditable) {
        el.textContent = zh;
      } else {
        const proto = el.localName === 'textarea' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
        const setter = Object.getOwnPropertyDescriptor(proto, 'value').set;
        setter.call(el, zh);
      }
      el.dispatchEvent(new InputEvent('input', { bubbles: true, composed: true, inputType: 'insertText', data: zh }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
    }
    hide();
  }

  // ------------------------------------------------------------------- events

  function onFocusIn(e) {
    const target = e.composedPath ? e.composedPath()[0] : e.target;
    const el = editableFrom(target);
    // Always track the focused field (also while switched off), so that switching the helper
    // on again or typing later refers to the right field.
    if (!el || isSecret(el) || !pageLooksChinese()) {
      if (field && el !== field) { field = null; hide(); }
      return;
    }
    chinesePage = true;
    if (field !== el) hide();
    field = el;
    update();
  }

  function onFocusOut(e) {
    if (!field) return;
    const target = e.composedPath ? e.composedPath()[0] : e.target;
    if (editableFrom(target) !== field) return;
    // Let a click on the bar finish first (the bar keeps the focus anyway).
    setTimeout(() => {
      const activeEl = document.activeElement && document.activeElement.shadowRoot
        ? document.activeElement.shadowRoot.activeElement || document.activeElement
        : document.activeElement;
      if (field && editableFrom(activeEl) !== field && !(field.contains && field.contains(activeEl))) {
        field = null;
        hide();
      }
    }, 0);
  }

  function onInput(e) {
    if (!field) return;
    const target = e.composedPath ? e.composedPath()[0] : e.target;
    if (target === field || (field.contains && field.contains(target))) update();
  }

  function onKeyDown(e) {
    if (!field || !host || !host.isConnected) return;
    if (e.key === 'Enter' && e.ctrlKey && e.shiftKey && translation) {
      e.preventDefault();
      e.stopPropagation();
      insertTranslation();
    } else if (e.key === 'Escape') {
      dismissed.add(field);
      hide();
    }
  }

  const onViewportChange = () => position();

  // ----------------------------------------------------------------- settings

  function hostnames() {
    const names = new Set();
    if (location.hostname) names.add(location.hostname);
    try {
      const origins = location.ancestorOrigins;
      if (origins && origins.length) names.add(new URL(origins[origins.length - 1]).hostname);
    } catch (e) { /* opaque origin */ }
    return names;
  }

  function applySettings(settings) {
    enabled = settings.compose !== false;
    const disabledHosts = Array.isArray(settings.disabledHosts) ? settings.disabledHosts : [];
    const names = hostnames();
    siteOff = disabledHosts.some((h) => names.has(h));
    if (!active()) hide();
    else if (field) update();
  }

  function applyState(state) {
    const next = !!(state && state.writeReady);
    if (next === writeReady) return;
    writeReady = next;
    if (field) { translation = ''; translatedFor = ''; update(); }
  }

  let dead = false;
  function shutdown() {
    if (dead) return;
    dead = true;
    hide();
    document.removeEventListener('focusin', onFocusIn, true);
    document.removeEventListener('focusout', onFocusOut, true);
    document.removeEventListener('input', onInput, true);
    document.removeEventListener('keydown', onKeyDown, true);
    removeEventListener('scroll', onViewportChange, true);
    removeEventListener('resize', onViewportChange);
  }

  async function init() {
    try {
      chrome.storage.onChanged.addListener((changes, area) => {
        if (dead) return;
        if (area === 'sync' && (changes.compose || changes.disabledHosts)) {
          chrome.storage.sync.get(['compose', 'disabledHosts']).then(applySettings, () => {});
        } else if (area === 'local' && changes.engineState) {
          applyState(changes.engineState.newValue);
        }
      });
      applySettings(await chrome.storage.sync.get(['compose', 'disabledHosts']));
      applyState((await chrome.storage.local.get('engineState')).engineState);
    } catch (e) {
      if (isInvalidated(e)) { shutdown(); return; }
    }
    chinesePage = pageLooksChinese();
    document.addEventListener('focusin', onFocusIn, true);
    document.addEventListener('focusout', onFocusOut, true);
    document.addEventListener('input', onInput, true);
    document.addEventListener('keydown', onKeyDown, true);
    addEventListener('scroll', onViewportChange, true);
    addEventListener('resize', onViewportChange);
    // A field that already has the focus (autofocus) gets the helper too.
    const focused = document.activeElement;
    if (focused && focused !== document.body) onFocusIn({ target: focused, composedPath: () => [focused] });
  }

  if (document.documentElement) init();
  else document.addEventListener('DOMContentLoaded', init, { once: true });
})();
