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
const DEFAULT_PORT = 47613;
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
          'and the localhost WebSocket to the Auto Translator desktop app.',
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
      engineState: { apiPresent: false, availability: 'unavailable', ready: false, bridgeConnected: false, host: 'none' },
    });
    if (sender.tab) chrome.tabs.remove(sender.tab.id).catch(() => {});
  }
  return { shutdown: true };
}

async function handleMessage(message, sender) {
  switch (message.type) {
    case 'engine.getConfig': {
      const cfg = await chrome.storage.local.get(['bridgePort', 'debugFakeTranslator']);
      const port = Number(cfg.bridgePort);
      return {
        port: Number.isInteger(port) && port > 0 && port < 65536 ? port : DEFAULT_PORT,
        debugFake: cfg.debugFakeTranslator || null,
      };
    }
    case 'engine.hello':
      return handleEngineHello(message, sender);
    case 'engine.state':
      await chrome.storage.local.set({ engineState: message.state });
      return { ok: true };
    case 'translate': {
      // From content scripts. Languages are fixed for now (zh -> en).
      try {
        return await sendToEngine({
          type: 'engine.translate',
          source: message.source || 'zh',
          target: message.target || 'en',
          texts: message.texts,
        });
      } catch (e) {
        const noApi = (await getMode()) === 'none';
        return { ok: false, notReady: true, error: noApi ? 'no-api' : String(e && e.message || e) };
      }
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

const HANDLED = new Set(['engine.getConfig', 'engine.hello', 'engine.state', 'translate', 'recheck']);

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || sender.id !== chrome.runtime.id || !HANDLED.has(message.type)) return false;
  handleMessage(message, sender).then(sendResponse, (e) => sendResponse({ ok: false, error: String(e && e.message || e) }));
  return true; // async response
});

// ------------------------------------------------------------------ lifecycle

function scheduleKeepalive() {
  chrome.alarms.create(KEEPALIVE_ALARM, { periodInMinutes: 1 });
}

chrome.runtime.onInstalled.addListener(async (details) => {
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
  if (area === 'local' && (changes.bridgePort || changes.debugFakeTranslator)) restartEngine();
});
