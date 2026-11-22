// Translation engine. Runs in exactly one extension document at a time:
// offscreen.html (preferred) or engine.html (pinned tab fallback).
//
// Owns the Translator instances (Chrome built-in, on-device) and the WebSocket
// to the desktop app (bridge protocol version 2, see docs/ARCHITECTURE.md).
// Only chrome.runtime is used here: offscreen documents have no other APIs.
//
// Two kinds of work arrive here:
//   - `translate` requests from the desktop app: always answered with the
//     on-device translator (never forwarded back to the app, so no loops).
//   - page batches from content scripts (via the service worker): sent to the
//     app as `page-translate` while the app offers a premium engine, with a
//     per-batch fallback to the on-device translator.

(() => {
  'use strict';

  const HOST = location.pathname.endsWith('/offscreen.html') ? 'offscreen' : 'tab';
  const PROTOCOL_VERSION = 2;
  const RECONNECT_DELAY_MS = 2500;
  const POLL_NOT_READY_MS = 5000;
  const POLL_READY_MS = 60000;
  const CACHE_CAPACITY = 5000;
  const READY_PAIR = { source: 'zh', target: 'en' };
  const WRITE_PAIR = { source: 'en', target: 'zh' }; // writing helper: English in, Chinese out
  const PAGE_TIMEOUT_MS = 10000; // the app must answer a page batch within this time
  const PREMIUM_COOLDOWN_MS = 30000; // after a timeout the app is not asked for a while
  const ON_DEVICE = 'chrome'; // engine tag of the on-device translator

  let translatorApi = null; // the global Translator (or the test fake)
  let availability = 'unavailable';
  let ready = false;
  let writeReady = false; // en -> zh language pack present (writing helper)
  let bridgeConnected = false;
  let socket = null;
  let bridgePort = 47613;
  let pollTimer = null;
  let stopped = false;

  // Premium engine offered by the desktop app (bridge protocol version 2).
  let appPremium = false; // last `app-state.premium` of the current connection
  let appEngine = 'app'; // last `app-state.engine`, sanitised
  let usePremium = true; // user setting (popup)
  let premiumPausedUntil = 0; // cooldown after a timeout
  let cooldownTimer = null;
  let pageSeq = 0;
  const pagePending = new Map(); // id -> { resolve, reject, timer }

  // ------------------------------------------------------------- backend hook

  // Test hooks only (chrome.storage.local "debugFakeTranslator", handed over by
  // the service worker, which accepts nothing but these values). Web pages have
  // no access to extension storage, so page content cannot switch this on.
  //   "all"      fake translator in every host
  //   "tab"      fake translator in the tab host, API reported missing in offscreen
  //   "none"     API reported missing everywhere
  //   "missing"  API present, but the language pack is reported as not downloaded
  /** Fake backend for automated tests. */
  function createFakeTranslatorApi(packMissing) {
    // Like the real API, the fake only knows real language codes.
    const known = (o) => !!o && ((/^zh(-Hant)?$/.test(o.sourceLanguage) && o.targetLanguage === 'en')
      || (o.sourceLanguage === 'en' && o.targetLanguage === 'zh'));
    return {
      availability: async (o) => (!known(o) ? 'unavailable' : packMissing ? 'downloadable' : 'available'),
      create: async (o) => {
        if (packMissing || !known(o)) throw new Error('language pack missing');
        const tag = o.targetLanguage === 'zh' ? 'ZH' : 'EN';
        return {
          translate: async (text) => tag + '[' + text + ']',
          destroy() {},
        };
      },
    };
  }

  /** Single place that decides which translator backend is used. */
  function getTranslatorApi(debugFake) {
    if (debugFake === 'missing') return createFakeTranslatorApi(true);
    if (debugFake === 'all' || debugFake === HOST) return createFakeTranslatorApi(false);
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

  function splitWhitespace(text) {
    const m = /^(\s*)([\s\S]*?)(\s*)$/.exec(text);
    return { lead: m[1], core: m[2], trail: m[3] };
  }

  // ---------------------------------------------------------- translator pool

  const translators = new Map(); // "src|tgt" -> Promise<Translator>
  // LRU: "engine|src|tgt|text" -> translation. Keyed per engine, so switching
  // engines never serves one engine's wording as the other's.
  const cache = new Map();

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
    const { lead, core, trail } = splitWhitespace(line);
    if (!core) return line;
    const key = ON_DEVICE + '|' + source + '|' + target + '|' + core;
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
    return lead + translated + trail;
  }

  async function translateText(text, source, target) {
    if (typeof text !== 'string') text = text == null ? '' : String(text);
    if (!text.trim()) return text;
    if (!text.includes('\n')) return translateLine(text, source, target);
    const lines = text.split('\n');
    for (let i = 0; i < lines.length; i++) lines[i] = await translateLine(lines[i], source, target);
    return lines.join('\n');
  }

  /** Translates a batch on-device; the result has the same order and length. */
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

  // ------------------------------------------------- premium engine (the app)

  /** True while page batches should go to the desktop app. */
  function premiumActive() {
    return bridgeConnected && appPremium && usePremium && Date.now() >= premiumPausedUntil;
  }

  function sanitizeEngineName(name) {
    const s = typeof name === 'string' ? name.trim().toLowerCase() : '';
    return /^[a-z0-9._-]{1,32}$/.test(s) && s !== ON_DEVICE ? s : 'app';
  }

  function rejectPagePending(reason) {
    for (const [id, entry] of pagePending) {
      clearTimeout(entry.timer);
      pagePending.delete(id);
      entry.reject(new Error(reason));
    }
  }

  /** The app did not answer in time: do not make every batch wait for it. */
  function pausePremium() {
    premiumPausedUntil = Date.now() + PREMIUM_COOLDOWN_MS;
    clearTimeout(cooldownTimer);
    cooldownTimer = setTimeout(pushState, PREMIUM_COOLDOWN_MS + 50);
    pushState();
  }

  /** One `page-translate` round trip. Rejects on page-error, timeout or disconnect. */
  function requestPageTranslation(texts, source, target) {
    return new Promise((resolve, reject) => {
      const ws = socket;
      if (!ws || ws.readyState !== WebSocket.OPEN) { reject(new Error('not connected')); return; }
      const id = 'p' + (++pageSeq);
      const timer = setTimeout(() => {
        if (!pagePending.delete(id)) return;
        pausePremium();
        reject(new Error('timeout'));
      }, PAGE_TIMEOUT_MS);
      pagePending.set(id, { resolve, reject, timer });
      try {
        ws.send(JSON.stringify({ type: 'page-translate', id, source, target, texts }));
      } catch (e) {
        clearTimeout(timer);
        pagePending.delete(id);
        reject(e);
      }
    });
  }

  function settlePageRequest(message) {
    const entry = pagePending.get(message.id);
    if (!entry) return; // unknown id, or an answer that arrived after the timeout
    clearTimeout(entry.timer);
    pagePending.delete(message.id);
    if (message.type === 'page-result') entry.resolve(message.translations);
    else entry.reject(new Error(typeof message.message === 'string' ? message.message : 'page-error'));
  }

  /**
   * Translates a page batch through the app. Order is kept (the app's engine
   * benefits from context); duplicates and cached texts are not sent again.
   */
  async function translateViaApp(texts, source, target) {
    const engine = appEngine;
    const prefix = engine + '|' + source + '|' + target + '|';
    const parts = [];
    const index = new Map(); // core -> position in `wanted`
    const wanted = [];
    for (const raw of texts) {
      const part = splitWhitespace(typeof raw === 'string' ? raw : raw == null ? '' : String(raw));
      parts.push(part);
      if (!part.core) { part.value = ''; continue; }
      part.value = cacheGet(prefix + part.core);
      if (part.value === undefined && !index.has(part.core)) {
        index.set(part.core, wanted.length);
        wanted.push(part.core);
      }
    }
    if (wanted.length > 0) {
      const answer = await requestPageTranslation(wanted, source, target);
      if (!Array.isArray(answer) || answer.length !== wanted.length || !answer.every((t) => typeof t === 'string')) {
        throw new Error('malformed page-result');
      }
      wanted.forEach((core, i) => cacheSet(prefix + core, answer[i]));
      for (const part of parts) if (part.value === undefined) part.value = answer[index.get(part.core)];
    }
    return { engine, translations: parts.map((p) => p.lead + p.value + p.trail) };
  }

  /** Page batches: premium engine while offered; on-device otherwise or on failure. */
  async function translateForPages(texts, source, target) {
    if (!Array.isArray(texts)) throw new Error('texts must be an array');
    // Only Chinese page text goes to the app; the writing helper (en -> zh) always stays on-device.
    const fromChinese = /^zh/i.test(String(source || 'zh')) && String(target || 'en').toLowerCase() === 'en';
    if (fromChinese && premiumActive()) {
      try {
        return await translateViaApp(texts, source || 'zh', target || 'en');
      } catch (e) {
        // page-error, timeout, disconnect or malformed answer:
        // on-device fallback for this batch.
      }
    }
    return { engine: ON_DEVICE, translations: await enqueueBatch(texts, source, target) };
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

  /**
   * Published to chrome.storage.local "engineState" by the service worker.
   *   ready        on-device translator usable (language pack present)
   *   premium      the connected app offers a better engine for pages
   *   usePremium   user setting
   *   pagesReady   pages can be translated right now (on-device OR via the app)
   *   pageEngine   engine for new page text: "chrome" or the app's engine name
   *   writeReady   writing helper usable (en -> zh language pack present)
   */
  function currentState() {
    const viaApp = premiumActive();
    return {
      apiPresent: !!translatorApi,
      availability,
      ready,
      bridgeConnected,
      host: HOST,
      premium: bridgeConnected && appPremium,
      usePremium,
      pagesReady: ready || viaApp,
      pageEngine: viaApp ? appEngine : ON_DEVICE,
      writeReady,
    };
  }

  function pushState() {
    if (stopped) return;
    chrome.runtime.sendMessage({ type: 'engine.state', state: currentState() }).catch(() => {});
  }

  async function recheck() {
    if (!translatorApi || stopped) return;
    const [next, nextWrite] = await Promise.all([
      queryAvailability(READY_PAIR.source, READY_PAIR.target),
      queryAvailability(WRITE_PAIR.source, WRITE_PAIR.target),
    ]);
    const nextReady = next === 'available';
    const nextWriteReady = nextWrite === 'available';
    const changed = next !== availability || nextReady !== ready || nextWriteReady !== writeReady;
    writeReady = nextWriteReady;
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
    pollTimer = setTimeout(recheck, ready && writeReady ? POLL_READY_MS : POLL_NOT_READY_MS);
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
      appPremium = false; // until the app says otherwise (`app-state`)
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
      if (socket === ws) {
        socket = null;
        // Everything learned from the app belongs to that connection.
        const hadState = bridgeConnected || appPremium;
        bridgeConnected = false;
        appPremium = false;
        appEngine = 'app';
        premiumPausedUntil = 0;
        clearTimeout(cooldownTimer);
        rejectPagePending('disconnected');
        if (hadState) pushState();
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
    } else if (message.type === 'app-state') {
      if (ws !== socket) return;
      const premium = message.premium === true;
      const engine = sanitizeEngineName(message.engine);
      if (premium !== appPremium || engine !== appEngine) {
        appPremium = premium;
        appEngine = engine;
        if (premium) { premiumPausedUntil = 0; clearTimeout(cooldownTimer); }
        pushState();
      }
    } else if (message.type === 'page-result' || message.type === 'page-error') {
      if (ws === socket && typeof message.id === 'string') settlePageRequest(message);
    } else if (message.type === 'translate') {
      // The desktop app asks: always the on-device translator, never the app itself.
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
    if (stopped) return false;
    if (message.type === 'engine.translate') {
      // Page batch from a content script.
      translateForPages(message.texts, message.sourceLanguage, message.targetLanguage).then(
        (result) => sendResponse({ ok: true, translations: result.translations, engine: result.engine }),
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
        sendResponse({ ok: true, ...currentState() });
      });
      return true;
    }
    if (message.type === 'engine.config') {
      const next = message.usePremium !== false;
      if (next !== usePremium) { usePremium = next; pushState(); }
      sendResponse({ ok: true });
      return false;
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
    usePremium = config.usePremium !== false;
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
