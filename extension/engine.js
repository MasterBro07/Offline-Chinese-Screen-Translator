// Translation engine. Runs in exactly one extension document at a time:
// offscreen.html (preferred) or engine.html (pinned tab fallback).
//
// Owns the Translator instances (Chrome built-in, on-device) and the WebSocket
// to the desktop app (bridge protocol version 1, see docs/ARCHITECTURE.md).
// Only chrome.runtime is used here: offscreen documents have no other APIs.

(() => {
  'use strict';

  const HOST = location.pathname.endsWith('/offscreen.html') ? 'offscreen' : 'tab';
  const PROTOCOL_VERSION = 1;
  const RECONNECT_DELAY_MS = 2500;
  const POLL_NOT_READY_MS = 5000;
  const POLL_READY_MS = 60000;
  const CACHE_CAPACITY = 5000;
  const READY_PAIR = { source: 'zh', target: 'en' };

  let translatorApi = null; // the global Translator (or the test fake)
  let availability = 'unavailable';
  let ready = false;
  let bridgeConnected = false;
  let socket = null;
  let bridgePort = 47613;
  let pollTimer = null;
  let stopped = false;

  // ------------------------------------------------------------- backend hook

  // Test hooks only (chrome.storage.local "debugFakeTranslator"):
  //   "all"  fake translator in every host
  //   "tab"  fake translator in the tab host, API reported missing in offscreen
  //   "none" API reported missing everywhere
  /** Fake backend for automated tests. */
  function createFakeTranslatorApi() {
    return {
      availability: async () => 'available',
      create: async () => ({
        translate: async (text) => 'EN[' + text + ']',
        destroy() {},
      }),
    };
  }

  /** Single place that decides which translator backend is used. */
  function getTranslatorApi(debugFake) {
    if (globalThis.__fakeTranslator) return globalThis.__fakeTranslator;
    if (debugFake === 'all' || debugFake === HOST) return createFakeTranslatorApi();
    if (debugFake === 'tab' || debugFake === 'none') return null;
    return 'Translator' in self ? self.Translator : null;
  }

  // ----------------------------------------------------------------- language

  /** Maps protocol language codes to Translator API codes. */
  function mapLanguage(code) {
    const c = String(code || '').trim().toLowerCase();
    if (c === 'zh' || c === 'zh-cn' || c === 'zh-sg' || c === 'zh-hans' || c === 'zh-hans-cn') return 'zh';
    if (c === 'zh-hant' || c === 'zh-tw' || c === 'zh-hk' || c === 'zh-hant-tw') return 'zh-Hant';
    return String(code || '').trim();
  }

  // ---------------------------------------------------------- translator pool

  const translators = new Map(); // "src|tgt" -> Promise<Translator>
  const cache = new Map(); // LRU: "src|tgt|text" -> translation

  function cacheGet(key) {
    if (!cache.has(key)) return undefined;
    const value = cache.get(key);
    cache.delete(key);
    cache.set(key, value);
    return value;
  }

  function cacheSet(key, value) {
    cache.delete(key);
    cache.set(key, value);
    if (cache.size > CACHE_CAPACITY) cache.delete(cache.keys().next().value);
  }

  async function queryAvailability(source, target) {
    try {
      return await translatorApi.availability({ sourceLanguage: source, targetLanguage: target });
    } catch (e) {
      return 'unavailable';
    }
  }

  function getTranslator(source, target) {
    const key = source + '|' + target;
    let promise = translators.get(key);
    if (!promise) {
      promise = (async () => {
        // Without a downloaded pack, create() would need a user gesture.
        const state = await queryAvailability(source, target);
        if (state !== 'available') throw new Error(detailFor(state));
        return translatorApi.create({ sourceLanguage: source, targetLanguage: target });
      })();
      translators.set(key, promise);
      promise.catch(() => translators.delete(key));
    }
    return promise;
  }

  async function translateLine(line, source, target) {
    const match = /^(\s*)([\s\S]*?)(\s*)$/.exec(line);
    const core = match[2];
    if (!core) return line;
    const key = source + '|' + target + '|' + core;
    let translated = cacheGet(key);
    if (translated === undefined) {
      let translator = await getTranslator(source, target);
      try {
        translated = await translator.translate(core);
      } catch (e) {
        // The instance may have been destroyed: recreate once.
        translators.delete(source + '|' + target);
        translator = await getTranslator(source, target);
        translated = await translator.translate(core);
      }
      translated = String(translated == null ? '' : translated);
      cacheSet(key, translated);
    }
    return match[1] + translated + match[3];
  }

  async function translateText(text, source, target) {
    if (typeof text !== 'string') text = text == null ? '' : String(text);
    if (!text.trim()) return text;
    if (!text.includes('\n')) return translateLine(text, source, target);
    const lines = text.split('\n');
    for (let i = 0; i < lines.length; i++) lines[i] = await translateLine(lines[i], source, target);
    return lines.join('\n');
  }

  /** Translates a batch; the result has the same order and length. */
  async function translateBatch(texts, source, target) {
    if (!translatorApi) throw new Error(detailFor('no-api'));
    if (!Array.isArray(texts)) throw new Error('texts must be an array');
    const src = mapLanguage(source || 'zh');
    const tgt = mapLanguage(target || 'en');
    const out = new Array(texts.length);
    for (let i = 0; i < texts.length; i++) out[i] = await translateText(texts[i], src, tgt);
    return out;
  }

  // Batches run one after another so the model is never flooded.
  let queue = Promise.resolve();
  function enqueueBatch(texts, source, target) {
    const run = queue.then(() => translateBatch(texts, source, target));
    queue = run.catch(() => {});
    return run;
  }

  // ---------------------------------------------------------------- readiness

  function detailFor(state) {
    switch (state) {
      case 'available': return '';
      case 'downloadable': return 'Sprachpaket fehlt';
      case 'downloading': return 'Sprachpaket wird geladen';
      case 'no-api': return 'Translator-API nicht verfügbar (Chrome 138 oder neuer nötig)';
      default: return 'Übersetzung Chinesisch → Englisch wird von diesem Chrome nicht unterstützt';
    }
  }

  function currentDetail() {
    return translatorApi ? detailFor(availability) : detailFor('no-api');
  }

  function pushState() {
    const state = { apiPresent: !!translatorApi, availability, ready, bridgeConnected, host: HOST };
    chrome.runtime.sendMessage({ type: 'engine.state', state }).catch(() => {});
  }

  async function recheck() {
    if (!translatorApi || stopped) return;
    const next = await queryAvailability(READY_PAIR.source, READY_PAIR.target);
    const nextReady = next === 'available';
    const changed = next !== availability || nextReady !== ready;
    const readyChanged = nextReady !== ready;
    availability = next;
    ready = nextReady;
    if (changed) pushState();
    if (readyChanged) bridgeSend({ type: 'status', ready, detail: currentDetail() });
    schedulePoll();
  }

  function schedulePoll() {
    clearTimeout(pollTimer);
    if (stopped) return;
    pollTimer = setTimeout(recheck, ready ? POLL_READY_MS : POLL_NOT_READY_MS);
  }

  // ------------------------------------------------------------ bridge client

  function bridgeSend(message) {
    if (socket && socket.readyState === WebSocket.OPEN) {
      try { socket.send(JSON.stringify(message)); } catch (e) { /* closed meanwhile */ }
    }
  }

  function connectBridge() {
    if (stopped) return;
    let ws;
    try {
      ws = new WebSocket('ws://127.0.0.1:' + bridgePort + '/bridge');
    } catch (e) {
      setTimeout(connectBridge, RECONNECT_DELAY_MS);
      return;
    }
    socket = ws;
    ws.onopen = () => {
      bridgeConnected = true;
      bridgeSend({
        type: 'hello',
        version: PROTOCOL_VERSION,
        engine: 'chrome-translator',
        ready,
        detail: currentDetail(),
      });
      pushState();
    };
    ws.onmessage = (event) => { handleBridgeMessage(ws, event.data); };
    ws.onerror = () => { /* the desktop app is simply not running; stay quiet */ };
    ws.onclose = () => {
      if (socket === ws) socket = null;
      if (bridgeConnected) {
        bridgeConnected = false;
        pushState();
      }
      setTimeout(connectBridge, RECONNECT_DELAY_MS);
    };
  }

  async function handleBridgeMessage(ws, data) {
    let message;
    try { message = JSON.parse(data); } catch (e) { return; }
    if (!message || typeof message !== 'object') return;
    const reply = (obj) => {
      if (ws.readyState === WebSocket.OPEN) {
        try { ws.send(JSON.stringify(obj)); } catch (e) { /* closed meanwhile */ }
      }
    };
    if (message.type === 'ping') {
      reply({ type: 'pong' });
    } else if (message.type === 'translate') {
      const id = message.id;
      try {
        const translations = await enqueueBatch(message.texts, message.source, message.target);
        reply({ type: 'result', id, translations });
      } catch (e) {
        reply({ type: 'error', id, message: String((e && e.message) || e) });
        recheck();
      }
    }
  }

  // --------------------------------------------- messages from service worker

  chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (!message || message.target !== 'engine' || sender.id !== chrome.runtime.id) return false;
    if (message.type === 'engine.translate') {
      enqueueBatch(message.texts, message.source, message.target).then(
        (translations) => sendResponse({ ok: true, translations }),
        (e) => {
          sendResponse({ ok: false, notReady: !ready, error: String((e && e.message) || e) });
          recheck();
        },
      );
      return true;
    }
    if (message.type === 'engine.recheck') {
      recheck().then(() => {
        pushState();
        sendResponse({ ok: true, apiPresent: !!translatorApi, availability, ready, bridgeConnected });
      });
      return true;
    }
    return false;
  });

  // --------------------------------------------------------------------- init

  async function askServiceWorker(message) {
    for (let attempt = 0; attempt < 20; attempt++) {
      try {
        const response = await chrome.runtime.sendMessage(message);
        if (response !== undefined) return response;
      } catch (e) { /* service worker starting up */ }
      await new Promise((resolve) => setTimeout(resolve, 250));
    }
    return null;
  }

  function showNote(text) {
    const el = document.getElementById('note');
    if (el) el.textContent = text;
  }

  async function init() {
    const config = (await askServiceWorker({ type: 'engine.getConfig' })) || {};
    if (config.port) bridgePort = config.port;
    translatorApi = getTranslatorApi(config.debugFake);

    const hello = await askServiceWorker({ type: 'engine.hello', host: HOST, apiPresent: !!translatorApi });
    if (!translatorApi || (hello && hello.shutdown)) {
      // The service worker closes this document and picks another host.
      stopped = true;
      showNote('Die Übersetzungsfunktion von Chrome ist hier nicht verfügbar. Dieser Tab kann geschlossen werden.');
      return;
    }
    await recheck();
    pushState();
    connectBridge();
  }

  init();
})();
