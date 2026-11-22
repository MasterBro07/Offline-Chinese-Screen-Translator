// Popup: status, language pack download, on/off switches.

(() => {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const enabledBox = $('enabled');
  const siteRow = $('site-row');
  const siteBox = $('site-off');
  const siteName = $('site-name');
  const pageEngine = $('page-engine');
  const engineHint = $('engine-hint');
  const composeStatus = $('compose-status');
  const composeBox = $('compose');
  const imagesBox = $('images');
  let hostname = '';

  // ------------------------------------------------------------ language pack

  LanguagePack.bind({
    status: $('pack-status'),
    button: $('download'),
    error: $('download-error'),
    hint: $('api-hint'),
  });

  // ------------------------------------------------------------------ engine

  function renderApp(state) {
    const ready = !!(state && state.ready);
    pageEngine.textContent = ready ? 'bereit' : 'Sprachpaket fehlt';
    pageEngine.dataset.state = ready ? 'available' : 'downloadable';
    engineHint.textContent = 'Chinesisch → Englisch, kostenlos und direkt auf diesem Gerät.';

    // Writing helper (English -> Chinese).
    const writeReady = !!(state && state.writeReady);
    const composeOn = composeBox.checked;
    composeStatus.textContent = !composeOn ? 'aus' : writeReady ? 'bereit' : 'Sprachpaket fehlt';
    composeStatus.dataset.state = !composeOn ? 'off' : writeReady ? 'available' : 'downloadable';
  }

  let lastState = null;

  chrome.storage.local.get('engineState').then((r) => { lastState = r.engineState; renderApp(lastState); });
  chrome.storage.onChanged.addListener((changes, area) => {
    if (area === 'local' && changes.engineState) { lastState = changes.engineState.newValue; renderApp(lastState); }
    if (area === 'sync' && (changes.enabled || changes.disabledHosts || changes.compose || changes.images)) loadSettings();
  });
  // Wakes the engine if necessary and refreshes its state.
  chrome.runtime.sendMessage({ type: 'recheck', apiPresent: 'Translator' in self }).catch(() => {});

  // ----------------------------------------------------------------- switches

  async function loadSettings() {
    const settings = await chrome.storage.sync.get(['enabled', 'disabledHosts', 'compose', 'images']);
    const disabledHosts = Array.isArray(settings.disabledHosts) ? settings.disabledHosts : [];
    enabledBox.checked = settings.enabled !== false;
    composeBox.checked = settings.compose !== false;
    imagesBox.checked = settings.images !== false;
    imagesBox.disabled = !enabledBox.checked;
    renderApp(lastState);
    siteBox.checked = !!hostname && disabledHosts.includes(hostname);
    siteBox.disabled = !enabledBox.checked;
  }

  enabledBox.addEventListener('change', () => {
    chrome.storage.sync.set({ enabled: enabledBox.checked });
  });

  imagesBox.addEventListener('change', () => {
    chrome.storage.sync.set({ images: imagesBox.checked });
  });

  composeBox.addEventListener('change', () => {
    chrome.storage.sync.set({ compose: composeBox.checked });
    renderApp(lastState);
  });

  siteBox.addEventListener('change', async () => {
    if (!hostname) return;
    const settings = await chrome.storage.sync.get('disabledHosts');
    const hosts = new Set(Array.isArray(settings.disabledHosts) ? settings.disabledHosts : []);
    if (siteBox.checked) hosts.add(hostname);
    else hosts.delete(hostname);
    await chrome.storage.sync.set({ disabledHosts: [...hosts] });
  });

  async function init() {
    try {
      const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
      const url = new URL((tab && tab.url) || '');
      if (url.protocol === 'http:' || url.protocol === 'https:' || url.protocol === 'file:') {
        hostname = url.hostname;
      }
    } catch (e) { /* no usable tab */ }
    if (hostname) {
      siteRow.hidden = false;
      siteName.textContent = hostname;
    }
    await loadSettings();
  }

  init();
})();
