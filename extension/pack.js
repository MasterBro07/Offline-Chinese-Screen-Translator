// Shared by popup.html and welcome.html: language pack status and download.
// These are normal extension documents, so a click here is a real user gesture,
// which Chrome requires for downloading a language pack. One click downloads every
// pack the extension uses: Chinese -> English (pages) and English -> Chinese (writing helper).

const LanguagePack = (() => {
  'use strict';

  // The first pair is the main one: without it nothing works. The others add features.
  const PAIRS = [
    { sourceLanguage: 'zh', targetLanguage: 'en' },
    { sourceLanguage: 'en', targetLanguage: 'zh' },
  ];
  let downloading = false;

  function apiPresent() {
    return 'Translator' in self;
  }

  /** "no-api" | "unavailable" | "downloadable" | "downloading" | "available" */
  async function status() {
    if (!apiPresent()) return 'no-api';
    const states = await Promise.all(PAIRS.map(async (pair) => {
      try {
        return await Translator.availability(pair);
      } catch (e) {
        return 'unavailable';
      }
    }));
    const main = states[0];
    if (main === 'unavailable') return main;
    // A pair this Chrome does not support at all is skipped; otherwise the least ready one counts.
    const usable = states.filter((s) => s !== 'unavailable');
    if (usable.includes('downloadable')) return 'downloadable';
    if (usable.includes('downloading')) return 'downloading';
    return 'available';
  }

  function statusText(state, percent) {
    switch (state) {
      case 'available': return 'bereit';
      case 'downloading':
        return typeof percent === 'number' ? 'werden geladen ' + percent + ' %' : 'werden geladen …';
      case 'downloadable': return 'fehlen';
      case 'no-api': return 'nicht verfügbar';
      default: return 'wird nicht unterstützt';
    }
  }

  /**
   * Starts the download. Must be called directly from a click handler.
   * onProgress receives 0..100. Resolves to the final status.
   */
  async function download(onProgress) {
    if (!apiPresent()) return 'no-api';
    if (downloading) return 'downloading';
    downloading = true;
    try {
      // All create() calls start synchronously, still inside the click's user gesture.
      // A pack that is already on disk finishes at once.
      const fractions = PAIRS.map(() => 0);
      const report = () => {
        const avg = fractions.reduce((a, b) => a + b, 0) / fractions.length;
        if (onProgress) onProgress(Math.max(0, Math.min(100, Math.round(avg * 100))));
      };
      const jobs = PAIRS.map((pair, i) => Translator.create({
        ...pair,
        monitor(m) {
          m.addEventListener('downloadprogress', (e) => {
            // e.loaded is a fraction 0..1 (older builds: bytes with e.total).
            fractions[i] = e.total && e.total > 1 ? e.loaded / e.total : e.loaded;
            report();
          });
        },
      }));
      const results = await Promise.allSettled(jobs);
      for (const r of results) {
        if (r.status === 'fulfilled' && r.value && typeof r.value.destroy === 'function') r.value.destroy();
      }
      if (results[0].status === 'rejected') throw results[0].reason;
    } finally {
      downloading = false;
      // Tell the engine document to look again.
      chrome.runtime.sendMessage({ type: 'recheck', apiPresent: 'Translator' in self }).catch(() => {});
    }
    return status();
  }

  /**
   * Wires a status element and a download button. Returns a refresh function.
   * elements: { status, button, error?, hint? }
   */
  function bind(elements) {
    let percent;

    function render(state) {
      elements.status.textContent = statusText(state, percent);
      elements.status.dataset.state = state;
      const showButton = state === 'downloadable' || state === 'downloading';
      elements.button.hidden = !showButton;
      elements.button.disabled = state === 'downloading';
      elements.button.textContent = state === 'downloading' ? 'Sprachpakete werden geladen …' : 'Sprachpakete laden';
      if (elements.hint) {
        elements.hint.hidden = state !== 'no-api' && state !== 'unavailable';
        elements.hint.textContent = state === 'no-api'
          ? 'Dieser Browser hat keine eingebaute Übersetzung. Benötigt wird Chrome 138 oder neuer auf einem Computer (Windows, Mac, Linux).'
          : 'Dieses Chrome kann Chinesisch → Englisch nicht übersetzen. Bitte Chrome aktualisieren.';
      }
    }

    async function refresh() {
      const state = await status();
      if (downloading && state !== 'available') render('downloading');
      else render(state);
      return state;
    }

    elements.button.addEventListener('click', () => {
      if (elements.error) elements.error.hidden = true;
      percent = undefined;
      render('downloading');
      // Called synchronously inside the click handler: keeps the user gesture.
      download((p) => { percent = p; render('downloading'); }).then(
        (state) => { percent = undefined; render(state); },
        (e) => {
          percent = undefined;
          if (elements.error) {
            elements.error.hidden = false;
            elements.error.textContent = 'Die Sprachpakete konnten nicht geladen werden. Bitte Internetverbindung prüfen und erneut versuchen.';
          }
          refresh();
        },
      );
    });

    refresh();
    // A download may also be running from another page: keep the line fresh.
    const interval = setInterval(refresh, 2000);
    addEventListener('pagehide', () => clearInterval(interval));
    return refresh;
  }

  return { apiPresent, status, statusText, download, bind };
})();
