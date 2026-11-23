// Service worker: keeps exactly one "engine" document alive and routes messages.
//
// The Translator API is not available in workers, so translation happens in an
// engine document (engine.js). Preferred host: an offscreen document. If the API
// is missing there, the engine is hosted in a pinned, inactive tab instead.
//
// Engine mode (chrome.storage.local "engineMode"):
//   "offscreen" (default) | "tab" | "none" (Translator API missing everywhere)

const OFFSCREEN_PATH = 'offscreen.html';
const ENGINE_TAB_URL = chrome.runtime.getURL('engine.html');
const KEEPALIVE_ALARM = 'engine-keepalive';

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function getMode() {
  const { engineMode } = await chrome.storage.local.get('engineMode');
  return engineMode === 'tab' || engineMode === 'none' ? engineMode : 'offscreen';
}

function setMode(mode) {
  return chrome.storage.local.set({ engineMode: mode });
}

// ---------------------------------------------------------------- engine host

let ensuring = null;

/** Makes sure one engine host exists. Resolves to false if none can exist. */
function ensureEngine() {
  if (!ensuring) {
    ensuring = doEnsureEngine()
      .catch(() => false)
      .finally(() => { ensuring = null; });
  }
  return ensuring;
}

async function doEnsureEngine() {
  const mode = await getMode();
  if (mode === 'none') return false;
  if (mode === 'offscreen') {
    await closeEngineTabs();
    if (await chrome.offscreen.hasDocument()) return true;
    try {
      await chrome.offscreen.createDocument({
        url: OFFSCREEN_PATH,
        reasons: ['WORKERS'],
        justification:
          'Hosts the on-device Translator API (not available in service workers) ' +
          'and the offline text recognition for images (WebAssembly).',
      });
      return true;
    } catch (e) {
      if (await chrome.offscreen.hasDocument()) return true;
      // Offscreen documents unusable: fall back to the tab host.
      await setMode('tab');
      return ensureEngineTab();
    }
  }
  return ensureEngineTab();
}

async function findEngineTabs() {
  const tabs = await chrome.tabs.query({});
  return tabs.filter((t) => (t.url || t.pendingUrl || '').startsWith(ENGINE_TAB_URL));
}

async function closeEngineTabs() {
  const tabs = await findEngineTabs();
  if (tabs.length) await chrome.tabs.remove(tabs.map((t) => t.id)).catch(() => {});
}

async function ensureEngineTab() {
  const tabs = await findEngineTabs();
  if (tabs.length > 0) {
    // Keep one, close duplicates (e.g. restored pinned tabs).
    const extra = tabs.slice(1).map((t) => t.id);
    if (extra.length) await chrome.tabs.remove(extra).catch(() => {});
    return true;
  }
  const windows = await chrome.windows.getAll({ windowTypes: ['normal'] });
  if (windows.length === 0) return false; // never open a window on our own
  const tab = await chrome.tabs.create({ url: ENGINE_TAB_URL, pinned: true, active: false, index: 0 });
  // Memory Saver must not discard the engine.
  await chrome.tabs.update(tab.id, { autoDiscardable: false }).catch(() => {});
  return true;
}

async function closeEngine() {
  if (await chrome.offscreen.hasDocument()) await chrome.offscreen.closeDocument().catch(() => {});
  await closeEngineTabs();
}

async function restartEngine() {
  if (ensuring) await ensuring;
  await closeEngine();
  await ensureEngine();
}

/** Sends a message to the engine document, starting it first if necessary. */
async function sendToEngine(message) {
  for (let attempt = 0; attempt < 15; attempt++) {
    if (!(await ensureEngine())) throw new Error('no-engine');
    try {
      const response = await chrome.runtime.sendMessage({ ...message, target: 'engine' });
      if (response !== undefined) return response;
    } catch (e) {
      // Engine not loaded yet ("Receiving end does not exist"): retry.
    }
    await sleep(200);
  }
  throw new Error('engine-unreachable');
}

// ------------------------------------------------------------ message routing

async function handleEngineHello(message, sender) {
  const mode = await getMode();
  if (message.host === 'tab' && mode !== 'tab') {
    // Stale engine tab (e.g. restored from a previous session).
    if (sender.tab) chrome.tabs.remove(sender.tab.id).catch(() => {});
    return { shutdown: true };
  }
  if (message.apiPresent) return { shutdown: false };

  if (message.host === 'offscreen') {
    // Translator API not exposed in offscreen documents: use a tab instead.
    await setMode('tab');
    if (ensuring) await ensuring;
    await chrome.offscreen.closeDocument().catch(() => {});
    ensureEngine();
  } else {
    // Not available in a normal extension page either: Chrome cannot do it.
    await setMode('none');
    await chrome.storage.local.set({
      engineState: sanitizeEngineState({ host: 'none' }),
    });
    if (sender.tab) chrome.tabs.remove(sender.tab.id).catch(() => {});
  }
  return { shutdown: true };
}

// ----------------------------------------------------------------- validation
//
// Content scripts run inside web page processes. Their messages carry our
// extension id too, so everything that is not plain page translation is only
// accepted from the extension's own documents, and page batches are checked.

const EXTENSION_BASE = chrome.runtime.getURL('');
const ENGINE_URLS = [chrome.runtime.getURL(OFFSCREEN_PATH), ENGINE_TAB_URL];
const DEBUG_FAKE_VALUES = new Set(['all', 'tab', 'offscreen', 'none', 'missing']);
const MAX_REQUEST_TEXTS = 200;
const MAX_REQUEST_CHARS = 100000;
// Language pairs content scripts may ask for: page text (Chinese -> English) and the
// writing helper (English -> Chinese).
const ALLOWED_PAIRS = new Set(['zh>en', 'zh-Hant>en', 'en>zh']);

// Request statistics for the automated tests (only while the test translator is switched on).
let debugMode = false;
globalThis.autoTranslatorStats = { requests: 0, texts: [], batches: [] };
chrome.storage.local.get('debugFakeTranslator').then((r) => { debugMode = DEBUG_FAKE_VALUES.has(r.debugFakeTranslator); });

function recordStats(texts) {
  if (!debugMode) return;
  const stats = globalThis.autoTranslatorStats;
  stats.requests++;
  stats.batches.push([texts.length, texts.join('').length]);
  stats.texts.push(...texts);
  if (stats.texts.length > 5000) stats.texts.splice(0, stats.texts.length - 5000);
  if (stats.batches.length > 500) stats.batches.splice(0, stats.batches.length - 500);
}

const MAX_IMAGE_URL = 8192;
const MENU_IMAGE = 'translate-image';

/** http(s) image URL a content script may ask the engine to read. */
function validImageUrl(url) {
  if (typeof url !== 'string' || url.length > MAX_IMAGE_URL) return false;
  try {
    const u = new URL(url);
    return u.protocol === 'https:' || u.protocol === 'http:';
  } catch (e) {
    return false;
  }
}

/** A rectangle in the tab's visible area (CSS pixels), for the screenshot fallback. */
function validRect(r) {
  return !!r && ['x', 'y', 'w', 'h'].every((k) => Number.isFinite(r[k]))
    && r.w >= 1 && r.h >= 1 && r.w <= 20000 && r.h <= 20000;
}

/**
 * Screenshot fallback for images the engine cannot download (hotlink protection, blob: URLs):
 * crop the image's visible rectangle out of a capture of the tab. Top frame only, because only
 * there are the content script's coordinates the same as the tab's.
 */
async function captureImage(message, sender) {
  if (!sender.tab || sender.frameId !== 0 || !validRect(message.rect)) throw new Error('no-capture');
  const dpr = Number.isFinite(message.dpr) && message.dpr > 0 && message.dpr <= 8 ? message.dpr : 1;
  const dataUrl = await chrome.tabs.captureVisibleTab(sender.tab.windowId, { format: 'png' });
  const r = message.rect;
  return sendToEngine({
    type: 'engine.ocrImage',
    dataUrl,
    crop: { x: r.x * dpr, y: r.y * dpr, w: r.w * dpr, h: r.h * dpr },
  });
}

function fromExtensionPage(sender) {
  return typeof sender.url === 'string' && sender.url.startsWith(EXTENSION_BASE);
}

function fromEngine(sender) {
  return typeof sender.url === 'string' && ENGINE_URLS.some((u) => sender.url.split(/[?#]/)[0] === u);
}

function validTexts(texts) {
  if (!Array.isArray(texts) || texts.length === 0 || texts.length > MAX_REQUEST_TEXTS) return false;
  let chars = 0;
  for (const text of texts) {
    if (typeof text !== 'string') return false;
    chars += text.length;
  }
  return chars <= MAX_REQUEST_CHARS;
}

function sanitizeEngineState(state) {
  const s = state && typeof state === 'object' ? state : {};
  const text = (v, fallback) => (typeof v === 'string' && v.length <= 40 ? v : fallback);
  return {
    apiPresent: s.apiPresent === true,
    availability: text(s.availability, 'unavailable'),
    ready: s.ready === true,
    host: text(s.host, 'none'),
    pagesReady: s.pagesReady === true,
    pageEngine: text(s.pageEngine, 'chrome'),
    writeReady: s.writeReady === true,
    detail: typeof s.detail === 'string' && s.detail.length <= 120 ? s.detail : '',
  };
}

async function handleMessage(message, sender) {
  switch (message.type) {
    case 'engine.getConfig': {
      const cfg = await chrome.storage.local.get('debugFakeTranslator');
      return { debugFake: DEBUG_FAKE_VALUES.has(cfg.debugFakeTranslator) ? cfg.debugFakeTranslator : null };
    }
    case 'engine.hello':
      return handleEngineHello(message, sender);
    case 'engine.state':
      await chrome.storage.local.set({ engineState: sanitizeEngineState(message.state) });
      return { ok: true };
    case 'translate': {
      // From content scripts: page text (zh -> en) or the writing helper (en -> zh).
      if (!validTexts(message.texts)) return { ok: false, error: 'bad-request' };
      const source = message.source === undefined ? 'zh' : message.source;
      const target = message.target === undefined ? 'en' : message.target;
      if (!ALLOWED_PAIRS.has(source + '>' + target)) return { ok: false, error: 'bad-request' };
      if (source !== 'en') recordStats(message.texts);
      try {
        return await sendToEngine({
          type: 'engine.translate',
          sourceLanguage: source,
          targetLanguage: target,
          texts: message.texts,
        });
      } catch (e) {
        const noApi = (await getMode()) === 'none';
        return { ok: false, notReady: true, error: noApi ? 'no-api' : String(e && e.message || e) };
      }
    }
    case 'ocrImage': {
      // From content scripts: recognise and translate the text in one image.
      if (!validImageUrl(message.url)) return { ok: false, error: 'bad-request' };
      let result;
      try {
        result = await sendToEngine({ type: 'engine.ocrImage', url: message.url });
      } catch (e) {
        const noApi = (await getMode()) === 'none';
        return { ok: false, notReady: true, error: noApi ? 'no-api' : String(e && e.message || e) };
      }
      if (result && !result.ok && /^image-|Failed to fetch|decode/i.test(String(result.error)) && message.rect) {
        try {
          const captured = await captureImage(message, sender);
          if (captured && captured.ok) return { ...captured, space: 'css' };
        } catch (e) { /* keep the original error */ }
      }
      return result && result.ok ? { ...result, space: 'image' } : result;
    }
    case 'recheck': {
      // From popup / welcome page: language pack state may have changed.
      if (message.apiPresent && (await getMode()) === 'none') await setMode('offscreen');
      try {
        return await sendToEngine({ type: 'engine.recheck' });
      } catch (e) {
        return { ok: false, error: String(e && e.message || e) };
      }
    }
    default:
      return undefined;
  }
}

// Who may send what. 'translate' is the only message accepted from content scripts.
const HANDLED = new Map([
  ['engine.getConfig', fromEngine],
  ['engine.hello', fromEngine],
  ['engine.state', fromEngine],
  ['recheck', fromExtensionPage],
  ['translate', () => true],
  ['ocrImage', () => true],
]);

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || typeof message !== 'object' || sender.id !== chrome.runtime.id) return false;
  const allowed = HANDLED.get(message.type);
  if (!allowed || !allowed(sender)) return false;
  handleMessage(message, sender).then(sendResponse, (e) => sendResponse({ ok: false, error: String(e && e.message || e) }));
  return true; // async response
});

// ------------------------------------------------------------------ lifecycle

function scheduleKeepalive() {
  chrome.alarms.create(KEEPALIVE_ALARM, { periodInMinutes: 1 });
}

// Right-click on an image: "Translate image" translates it at once, whatever its size.
function createImageMenu() {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({ id: MENU_IMAGE, title: 'Translate image', contexts: ['image'] }, () => void chrome.runtime.lastError);
  });
}

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId !== MENU_IMAGE || !tab || !validImageUrl(info.srcUrl)) return;
  chrome.tabs.sendMessage(tab.id, { type: 'images.translateNow', srcUrl: info.srcUrl }, { frameId: info.frameId || 0 })
    .catch(() => {});
});

chrome.runtime.onInstalled.addListener(async (details) => {
  createImageMenu();
  // Also fires on Chrome updates: probe the offscreen document again.
  await setMode('offscreen');
  await chrome.storage.local.remove('engineState');
  scheduleKeepalive();
  ensureEngine();
  if (details.reason === 'install') {
    chrome.tabs.create({ url: chrome.runtime.getURL('welcome.html') });
  }
});

chrome.runtime.onStartup.addListener(async () => {
  if ((await getMode()) === 'none') await setMode('offscreen');
  await chrome.storage.local.remove('engineState');
  scheduleKeepalive();
  ensureEngine();
});

chrome.alarms.onAlarm.addListener((alarm) => {
  if (alarm.name === KEEPALIVE_ALARM) ensureEngine();
});

// Re-create the engine tab when the user closes it.
chrome.tabs.onRemoved.addListener(async () => {
  if ((await getMode()) !== 'tab') return;
  await sleep(500);
  ensureEngine();
});

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === 'local' && changes.debugFakeTranslator) {
    debugMode = DEBUG_FAKE_VALUES.has(changes.debugFakeTranslator.newValue);
    restartEngine();
  }
});
