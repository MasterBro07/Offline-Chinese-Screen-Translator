// Translation engine. Runs in exactly one extension document at a time:
// offscreen.html (preferred) or engine.html (pinned tab fallback).
//
// Owns the Translator instances (Chrome built-in, on-device) and the offline text
// recognition for images (ocr/ocr.js). Only chrome.runtime is used here: offscreen
// documents have no other extension APIs.
//
// Work arrives from content scripts via the service worker:
//   - page batches (Chinese -> English) and writing-helper text (English -> Chinese)
//   - images: recognise the Chinese text, then translate it

(() => {
  'use strict';

  const HOST = location.pathname.endsWith('/offscreen.html') ? 'offscreen' : 'tab';
  const POLL_NOT_READY_MS = 5000;
  const POLL_READY_MS = 60000;
  const CACHE_CAPACITY = 5000;
  const READY_PAIR = { source: 'zh', target: 'en' };
  const WRITE_PAIR = { source: 'en', target: 'zh' }; // writing helper: English in, Chinese out
  const ON_DEVICE = 'chrome'; // engine tag of the on-device translator

  let translatorApi = null; // the global Translator (or the test fake)
  let availability = 'unavailable';
  let ready = false;
  let writeReady = false; // en -> zh language pack present (writing helper)
  let pollTimer = null;
  let stopped = false;

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
        // A few real words, so tests can check spacing between translated inline pieces.
        const words = o.targetLanguage === 'en' ? { 免费: 'Free', 送货: 'shipping', 包邮: 'Free', 退货: 'returns' } : {};
        return {
          translate: async (text) => words[text] || tag + '[' + text + ']',
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

  // --------------------------------------------------------- text in images

  const OCR_CACHE_CAPACITY = 300;
  const OCR_MAX_BYTES = 25 * 1024 * 1024;
  const ocrCache = new Map(); // image key -> { width, height, lines: [{ box, zh }] }
  let ocrQueue = Promise.resolve();

  /** Loads an image the page shows. Host permissions let the engine fetch it without CORS limits. */
  async function loadImage(message) {
    let blob;
    if (typeof message.dataUrl === 'string') {
      blob = await (await fetch(message.dataUrl)).blob();
    } else {
      const res = await fetch(message.url, { credentials: 'omit', referrerPolicy: 'no-referrer' });
      if (!res.ok) throw new Error('image-http-' + res.status);
      blob = await res.blob();
    }
    if (blob.size > OCR_MAX_BYTES) throw new Error('image-too-large');
    const crop = message.crop;
    if (crop && [crop.x, crop.y, crop.w, crop.h].every((v) => Number.isFinite(v)) && crop.w > 0 && crop.h > 0) {
      return createImageBitmap(blob, Math.round(crop.x), Math.round(crop.y), Math.round(crop.w), Math.round(crop.h));
    }
    return createImageBitmap(blob);
  }

  /**
   * OCR + translation of one image. Images are processed one at a time; recognised text is cached
   * per image, translations come from the normal translation cache.
   */
  function ocrImage(message) {
    const key = typeof message.dataUrl === 'string' ? null : String(message.url);
    const run = ocrQueue.then(async () => {
      let result = key ? ocrCache.get(key) : undefined;
      if (!result) {
        const bitmap = await loadImage(message);
        try {
          const lines = await AutoTranslatorOcr.recognize(bitmap);
          result = { width: bitmap.width, height: bitmap.height, lines: lines.map((l) => ({ box: l.box, zh: l.text })) };
        } finally {
          bitmap.close();
        }
        if (key) {
          ocrCache.set(key, result);
          if (ocrCache.size > OCR_CACHE_CAPACITY) ocrCache.delete(ocrCache.keys().next().value);
        }
      }
      const en = result.lines.length ? await enqueueBatch(result.lines.map((l) => l.zh), 'zh', 'en') : [];
      return { width: result.width, height: result.height, lines: result.lines.map((l, i) => ({ box: l.box, zh: l.zh, en: en[i] })) };
    });
    ocrQueue = run.catch(() => {});
    return run;
  }

  /** Page batches and writing-helper text: always the on-device translator. */
  async function translateForPages(texts, source, target) {
    if (!Array.isArray(texts)) throw new Error('texts must be an array');
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

  /**
   * Published to chrome.storage.local "engineState" by the service worker.
   *   ready        on-device translator usable (zh -> en language pack present)
   *   pagesReady   pages can be translated right now (same as ready)
   *   pageEngine   always "chrome" (the on-device translator)
   *   writeReady   writing helper usable (en -> zh language pack present)
   *   detail       short German explanation when not ready
   */
  function currentState() {
    return {
      apiPresent: !!translatorApi,
      availability,
      ready,
      host: HOST,
      pagesReady: ready,
      pageEngine: ON_DEVICE,
      writeReady,
      detail: translatorApi ? detailFor(availability) : detailFor('no-api'),
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
    availability = next;
    ready = nextReady;
    if (changed) pushState();
    schedulePoll();
  }

  function schedulePoll() {
    clearTimeout(pollTimer);
    if (stopped) return;
    pollTimer = setTimeout(recheck, ready && writeReady ? POLL_READY_MS : POLL_NOT_READY_MS);
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
    if (message.type === 'engine.ocrImage') {
      ocrImage(message).then(
        (result) => sendResponse({ ok: true, ...result }),
        (e) => sendResponse({ ok: false, notReady: !ready, error: String((e && e.message) || e) }),
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
  }

  init();
})();
