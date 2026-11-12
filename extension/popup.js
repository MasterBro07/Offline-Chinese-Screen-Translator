// Popup: status, language pack download, on/off switches.

(() => {
  'use strict';

  const $ = (id) => document.getElementById(id);
  const appStatus = $('app-status');
  const enabledBox = $('enabled');
  const siteRow = $('site-row');
  const siteBox = $('site-off');
  const siteName = $('site-name');
  let hostname = '';

  // ------------------------------------------------------------ language pack

  LanguagePack.bind({
    status: $('pack-status'),
    button: $('download'),
    error: $('download-error'),
    hint: $('api-hint'),
  });

  // -------------------------------------------------------------- desktop app

  function renderApp(state) {
    const connected = !!(state && state.bridgeConnected);
    appStatus.textContent = connected ? 'verbunden' : 'nicht verbunden';
    appStatus.dataset.state = connected ? 'available' : 'off';
  }

  chrome.storage.local.get('engineState').then((r) => renderApp(r.engineState));
  chrome.storage.onChanged.addListener((changes, area) => {
    if (area === 'local' && changes.engineState) renderApp(changes.engineState.newValue);
    if (area === 'sync' && (changes.enabled || changes.disabledHosts)) loadSettings();
  });
  // Wakes the engine if necessary and refreshes its state.
  chrome.runtime.sendMessage({ type: 'recheck', apiPresent: 'Translator' in self }).catch(() => {});

  // ----------------------------------------------------------------- switches

  async function loadSettings() {
    const settings = await chrome.storage.sync.get(['enabled', 'disabledHosts']);
    const disabledHosts = Array.isArray(settings.disabledHosts) ? settings.disabledHosts : [];
    enabledBox.checked = settings.enabled !== false;
    siteBox.checked = !!hostname && disabledHosts.includes(hostname);
    siteBox.disabled = !enabledBox.checked;
  }

  enabledBox.addEventListener('change', () => {
    chrome.storage.sync.set({ enabled: enabledBox.checked });
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
