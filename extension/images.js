// Text in images: on pages with Chinese text, larger images in view are sent (one at a time) to the
// engine, which recognises Chinese text offline (PaddleOCR) and translates it on-device. The English
// lines are drawn as dark boxes exactly over the Chinese ones. A small "Original" button on the image shows
// the original again. Right-click → "Translate image" translates any single image right away.
//
// Nothing from the page is parsed as HTML; the overlays live in their own shadow root, marked
// as the extension's own UI, so the page translation (content.js) skips them.

(() => {
  'use strict';

  if (globalThis.__autoTranslatorImages) return;
  globalThis.__autoTranslatorImages = true;

  const HAN = /\p{Script=Han}/u;
  const MIN_NATURAL_W = 150;
  const MIN_NATURAL_H = 80;
  const MIN_SHOWN_W = 120;
  const MIN_SHOWN_H = 60;
  const MIN_FONT_PX = 9;
  const RETRY_MS = 15000;

  let pageOn = true; // "Translate web pages automatically"
  let imagesOn = true; // "Translate images automatically"
  let siteOff = false;
  let running = false;
  let dead = false;

  let chinesePage = false;
  let chineseCheckedAt = 0;

  /**
   * Per image: { src, status: 'queued'|'busy'|'done'|'none'|'waiting'|'error', forced, lines,
   * space, layer, inner, boxes, toggle, showOriginal }
   */
  const states = new WeakMap();
  const tracked = new Set(); // images with an overlay
  const visible = new Set();
  const queue = []; // images waiting for OCR
  let busy = false;

  let io = null;
  let mo = null;
  let ro = null;
  let host = null;
  let root = null;
  let tick = 0;
  let frame = 0;

  // ------------------------------------------------------------------ helpers

  function isInvalidated(e) {
    return /context invalidated/i.test(String(e && e.message || e));
  }

  function active() {
    return pageOn && imagesOn && !siteOff;
  }

  function pageLooksChinese() {
    if (chinesePage || globalThis.__autoTranslatorSawChinese) return (chinesePage = true);
    const lang = (document.documentElement && document.documentElement.lang) || '';
    if (/^zh\b/i.test(lang)) return (chinesePage = true);
    const now = Date.now();
    if (now - chineseCheckedAt < 3000) return false;
    chineseCheckedAt = now;
    const sample = document.body ? (document.body.textContent || '').slice(0, 20000) : '';
    if (HAN.test(sample) || HAN.test(document.title || '')) chinesePage = true;
    return chinesePage;
  }

  function imageSrc(img) {
    const src = img.currentSrc || img.src || '';
    return /^https?:/i.test(src) ? src : '';
  }

  function isSvg(src) {
    try {
      return /\.svgz?$/i.test(new URL(src).pathname);
    } catch (e) {
      return false;
    }
  }

  function eligible(img, forced) {
    if (!img.isConnected || !img.complete) return false;
    const src = imageSrc(img);
    if (!src || isSvg(src)) return false;
    if (forced) return img.naturalWidth > 0 && img.naturalHeight > 0;
    if (img.naturalWidth < MIN_NATURAL_W || img.naturalHeight < MIN_NATURAL_H) return false;
    const r = img.getBoundingClientRect();
    return r.width >= MIN_SHOWN_W && r.height >= MIN_SHOWN_H;
  }

  /** Visible part of the image in viewport coordinates (for the screenshot fallback). */
  function visibleRect(img) {
    const r = img.getBoundingClientRect();
    const x = Math.max(0, r.left);
    const y = Math.max(0, r.top);
    const w = Math.min(innerWidth, r.right) - x;
    const h = Math.min(innerHeight, r.bottom) - y;
    return w >= 1 && h >= 1 ? { x, y, w, h, offX: x - r.left, offY: y - r.top, boxW: r.width, boxH: r.height } : null;
  }

  // ------------------------------------------------------------------- queue

  function enqueue(img, forced) {
    let st = states.get(img);
    const src = imageSrc(img);
    if (st && st.src !== src) { removeOverlay(img); st = null; }
    if (st && !forced && st.status !== 'waiting') return;
    if (st && (st.status === 'queued' || st.status === 'busy')) {
      if (forced) { st.forced = true; queue.splice(queue.indexOf(img), 1); queue.unshift(img); }
      return;
    }
    if (st && st.status === 'done' && st.lines && st.lines.length) { render(img); return; }
    st = { src, status: 'queued', forced: !!forced };
    states.set(img, st);
    if (forced) queue.unshift(img); else queue.push(img);
    pump();
  }

  function consider(img) {
    if (!running || !active() || !visible.has(img) || !eligible(img, false)) return;
    if (!pageLooksChinese()) return;
    const st = states.get(img);
    if (st && st.src === imageSrc(img) && st.status !== 'waiting') {
      if (st.status === 'done') render(img);
      return;
    }
    enqueue(img, false);
  }

  /** Next image: forced ones first, then the one closest to the middle of the viewport. */
  function nextImage() {
    for (let i = queue.length - 1; i >= 0; i--) {
      const img = queue[i];
      const st = states.get(img);
      if (!img.isConnected || !st || st.status !== 'queued' || st.src !== imageSrc(img)) queue.splice(i, 1);
    }
    const forced = queue.findIndex((img) => states.get(img).forced);
    if (forced >= 0) return queue.splice(forced, 1)[0];
    let best = -1;
    let bestDist = Infinity;
    queue.forEach((img, i) => {
      if (!visible.has(img)) return;
      const r = img.getBoundingClientRect();
      const d = Math.abs(r.top + r.height / 2 - innerHeight / 2);
      if (d < bestDist) { bestDist = d; best = i; }
    });
    return best >= 0 ? queue.splice(best, 1)[0] : null;
  }

  async function pump() {
    if (busy || dead) return;
    const img = nextImage();
    if (!img) return;
    const st = states.get(img);
    if (!st.forced && (!active() || !running)) { states.delete(img); pump(); return; }
    busy = true;
    st.status = 'busy';
    const rect = visibleRect(img);
    let response;
    try {
      response = await chrome.runtime.sendMessage({
        type: 'ocrImage',
        url: st.src,
        rect: rect ? { x: rect.x, y: rect.y, w: rect.w, h: rect.h } : undefined,
        dpr: devicePixelRatio,
      });
    } catch (e) {
      busy = false;
      if (isInvalidated(e)) { shutdown(); return; }
      st.status = 'error';
      pump();
      return;
    }
    busy = false;
    if (states.get(img) !== st) { pump(); return; } // replaced meanwhile
    if (response && response.ok && Array.isArray(response.lines)) {
      st.space = response.space === 'css' ? 'css' : 'image';
      st.lines = normalise(response, rect);
      st.status = st.lines.length ? 'done' : 'none';
      if (st.lines.length && (running || st.forced)) render(img);
    } else if (response && response.notReady) {
      st.status = 'waiting'; // engine or language pack not ready yet: try again later
      setTimeout(() => { if (states.get(img) === st && st.status === 'waiting') enqueue(img, st.forced); }, RETRY_MS);
    } else {
      st.status = 'error';
    }
    pump();
  }

  /** Converts engine boxes into fractions of the image ('image' space) or of the element ('css'). */
  function normalise(response, rect) {
    const lines = [];
    const W = response.width;
    const H = response.height;
    if (!(W > 0 && H > 0)) return lines;
    for (const line of response.lines) {
      if (!line || typeof line.en !== 'string' || !line.box) continue;
      const b = line.box;
      let fx, fy, fw, fh;
      if (response.space === 'css' && rect) {
        // Screenshot of the visible part: device pixels of the crop -> fractions of the element.
        const sx = rect.w / W;
        const sy = rect.h / H;
        fx = (rect.offX + b.x * sx) / rect.boxW;
        fy = (rect.offY + b.y * sy) / rect.boxH;
        fw = (b.w * sx) / rect.boxW;
        fh = (b.h * sy) / rect.boxH;
      } else {
        fx = b.x / W; fy = b.y / H; fw = b.w / W; fh = b.h / H;
      }
      lines.push({ fx, fy, fw, fh, en: line.en.trim(), zh: String(line.zh || '') });
    }
    return lines.filter((l) => l.en);
  }

  // ----------------------------------------------------------------- overlay

  function ensureHost() {
    if (host && host.isConnected) return;
    if (!host) {
      host = document.createElement('auto-translator-images');
      host.setAttribute('translate', 'no');
      host.style.cssText = 'all: initial; position: fixed; top: 0; left: 0; width: 0; height: 0; z-index: 2147483646; pointer-events: none;';
      root = host.attachShadow({ mode: 'open' });
      const style = document.createElement('style');
      style.textContent = `
        .layer { position: fixed; overflow: hidden; pointer-events: none; }
        .inner { position: absolute; }
        .inner.off { visibility: hidden; }
        .box { position: absolute; box-sizing: border-box; display: flex; align-items: center;
          justify-content: center; text-align: center; padding: 0 2px; border-radius: 3px;
          background: rgba(24, 26, 30, .9); color: #fff; line-height: 1.12; overflow: hidden;
          font-family: system-ui, -apple-system, "Segoe UI", Roboto, sans-serif; font-weight: 600;
          white-space: nowrap; }
        .box.wrap { white-space: normal; overflow-wrap: anywhere; }
        .toggle { position: absolute; top: 4px; right: 4px; pointer-events: auto; cursor: pointer;
          border: 0; border-radius: 10px; padding: 2px 8px; font: 600 11px/1.4 system-ui, sans-serif;
          background: rgba(24, 26, 30, .75); color: #fff; }
        .toggle:hover { background: rgba(47, 129, 247, .95); }
      `;
      root.append(style);
    }
    (document.body || document.documentElement).append(host);
  }

  function render(img) {
    const st = states.get(img);
    if (!st || !st.lines || !st.lines.length) return;
    ensureHost();
    if (!st.layer) {
      const layer = document.createElement('div');
      layer.className = 'layer';
      const inner = document.createElement('div');
      inner.className = 'inner';
      st.boxes = st.lines.map((line) => {
        const box = document.createElement('div');
        box.className = 'box';
        box.lang = 'en';
        box.textContent = line.en;
        inner.append(box);
        return box;
      });
      const toggle = document.createElement('button');
      toggle.className = 'toggle';
      toggle.type = 'button';
      toggle.textContent = 'Original';
      toggle.title = 'Show the original';
      toggle.addEventListener('click', (e) => {
        e.preventDefault();
        e.stopPropagation();
        st.showOriginal = !st.showOriginal;
        inner.classList.toggle('off', st.showOriginal);
        toggle.textContent = st.showOriginal ? 'English' : 'Original';
        toggle.title = st.showOriginal ? 'Show the translation' : 'Show the original';
      });
      layer.append(inner, toggle);
      st.layer = layer;
      st.inner = inner;
      st.toggle = toggle;
      st.fontKey = '';
    }
    if (!st.layer.isConnected) root.append(st.layer);
    tracked.add(img);
    if (ro) ro.observe(img);
    position(img);
  }

  function removeOverlay(img) {
    const st = states.get(img);
    if (st && st.layer) st.layer.remove();
    tracked.delete(img);
    states.delete(img);
  }

  let measureCtx = null;
  function textWidth(text, px) {
    if (!measureCtx) measureCtx = new OffscreenCanvas(1, 1).getContext('2d');
    measureCtx.font = `600 ${px}px system-ui, -apple-system, "Segoe UI", Roboto, sans-serif`;
    return measureCtx.measureText(text).width;
  }

  /** The area the picture occupies inside the element (object-fit), relative to the element. */
  function contentBox(img, W, H, space) {
    if (space === 'css') return { x: 0, y: 0, w: W, h: H };
    const nw = img.naturalWidth;
    const nh = img.naturalHeight;
    const fit = getComputedStyle(img).objectFit;
    if (!nw || !nh || fit === 'fill' || !fit) return { x: 0, y: 0, w: W, h: H };
    let s;
    if (fit === 'contain') s = Math.min(W / nw, H / nh);
    else if (fit === 'cover') s = Math.max(W / nw, H / nh);
    else if (fit === 'scale-down') s = Math.min(1, W / nw, H / nh);
    else s = 1; // none
    const w = nw * s;
    const h = nh * s;
    return { x: (W - w) / 2, y: (H - h) / 2, w, h };
  }

  function position(img) {
    const st = states.get(img);
    if (!st || !st.layer) return;
    if (!img.isConnected || imageSrc(img) !== st.src) {
      removeOverlay(img);
      if (img.isConnected && running) consider(img);
      return;
    }
    const r = img.getBoundingClientRect();
    const hidden = r.width < 2 || r.height < 2 || r.bottom < 0 || r.top > innerHeight
      || r.right < 0 || r.left > innerWidth || getComputedStyle(img).visibility === 'hidden';
    st.layer.style.display = hidden ? 'none' : '';
    if (hidden) return;
    Object.assign(st.layer.style, { left: r.left + 'px', top: r.top + 'px', width: r.width + 'px', height: r.height + 'px' });
    const c = contentBox(img, r.width, r.height, st.space);
    Object.assign(st.inner.style, { left: c.x + 'px', top: c.y + 'px', width: c.w + 'px', height: c.h + 'px' });
    st.toggle.style.display = r.width >= 90 && r.height >= 40 ? '' : 'none';
    // Font sizes only change with the displayed size.
    const key = Math.round(c.w) + 'x' + Math.round(c.h);
    if (key === st.fontKey) return;
    st.fontKey = key;
    st.lines.forEach((line, i) => {
      const box = st.boxes[i];
      // Grow each box a little beyond the detected line so the Chinese glyphs are fully covered.
      const pad = Math.max(2, line.fh * c.h * 0.16);
      const left = line.fx * c.w - pad;
      const top = line.fy * c.h - pad;
      const width = line.fw * c.w + 2 * pad;
      const height = line.fh * c.h + 2 * pad;
      Object.assign(box.style, { left: left + 'px', top: top + 'px', width: width + 'px', minHeight: height + 'px' });
      const bw = Math.max(1, width - 6);
      const bh = Math.max(1, line.fh * c.h);
      let px = Math.max(MIN_FONT_PX, bh * 0.72);
      const textW = textWidth(line.en, px);
      if (textW > bw) px = Math.max(MIN_FONT_PX, (px * bw) / textW * 0.97);
      box.style.fontSize = px.toFixed(1) + 'px';
      box.classList.toggle('wrap', textWidth(line.en, px) > bw);
    });
  }

  function positionAll() {
    frame = 0;
    for (const img of [...tracked]) position(img);
  }

  function schedulePosition() {
    if (!frame && tracked.size) frame = requestAnimationFrame(positionAll);
  }

  // ------------------------------------------------------------ observation

  function eachImage(rootNode, fn) {
    if (rootNode.localName === 'img') fn(rootNode);
    if (rootNode.querySelectorAll) rootNode.querySelectorAll('img').forEach(fn);
  }

  function observeImage(img) {
    if (io) io.observe(img);
  }

  function scanShadowRoots(node) {
    const walker = document.createTreeWalker(node, NodeFilter.SHOW_ELEMENT);
    for (let el = walker.currentNode; el; el = walker.nextNode()) {
      if (el.shadowRoot && el !== host) {
        eachImage(el.shadowRoot, observeImage);
        scanShadowRoots(el.shadowRoot);
      }
    }
  }

  const onLoad = (e) => {
    const img = e.target;
    if (img && img.localName === 'img') consider(img);
  };

  function start() {
    if (running || dead) return;
    running = true;
    io = new IntersectionObserver((entries) => {
      for (const entry of entries) {
        const img = entry.target;
        if (entry.isIntersecting) { visible.add(img); consider(img); } else visible.delete(img);
      }
    }, { rootMargin: '300px 0px' });
    ro = new ResizeObserver(schedulePosition);
    eachImage(document, observeImage);
    if (document.body) scanShadowRoots(document.body);
    mo = new MutationObserver((records) => {
      for (const rec of records) {
        if (rec.type === 'attributes') {
          if (rec.target.localName === 'img') consider(rec.target);
          continue;
        }
        for (const node of rec.addedNodes) {
          if (node.nodeType === 1 && node !== host) eachImage(node, observeImage);
        }
      }
    });
    mo.observe(document.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['src', 'srcset'] });
    document.addEventListener('load', onLoad, true);
    addEventListener('scroll', schedulePosition, { capture: true, passive: true });
    addEventListener('resize', schedulePosition);
    // Carousels and animations move images without scroll events.
    tick = setInterval(() => { if (tracked.size) positionAll(); }, 500);
    for (const img of tracked) if (states.get(img)) render(img);
  }

  function stop() {
    running = false;
    if (io) { io.disconnect(); io = null; }
    if (mo) { mo.disconnect(); mo = null; }
    if (ro) { ro.disconnect(); ro = null; }
    clearInterval(tick);
    document.removeEventListener('load', onLoad, true);
    removeEventListener('scroll', schedulePosition, true);
    removeEventListener('resize', schedulePosition);
    visible.clear();
    queue.length = 0;
    for (const img of [...tracked]) {
      const st = states.get(img);
      if (st && st.layer) st.layer.remove();
    }
    tracked.clear();
    if (host) host.remove();
  }

  function shutdown() {
    if (dead) return;
    stop();
    dead = true;
  }

  // -------------------------------------------------------------- right-click

  function findImage(rootNode, srcUrl) {
    for (const img of rootNode.querySelectorAll('img')) {
      if (img.currentSrc === srcUrl || img.src === srcUrl) return img;
    }
    const walker = document.createTreeWalker(rootNode, NodeFilter.SHOW_ELEMENT);
    for (let el = walker.currentNode; el; el = walker.nextNode()) {
      if (el.shadowRoot && el !== host) {
        const hit = findImage(el.shadowRoot, srcUrl);
        if (hit) return hit;
      }
    }
    return null;
  }

  chrome.runtime.onMessage.addListener((message, sender) => {
    if (dead || !message || sender.id !== chrome.runtime.id || message.type !== 'images.translateNow') return false;
    const img = typeof message.srcUrl === 'string' ? findImage(document, message.srcUrl) : null;
    if (img && eligible(img, true)) {
      const st = states.get(img);
      if (st && st.status === 'done' && st.lines && st.lines.length) render(img);
      else {
        if (st && st.status !== 'queued' && st.status !== 'busy') states.delete(img);
        enqueue(img, true);
      }
    }
    return false;
  });

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
    pageOn = settings.enabled !== false;
    imagesOn = settings.images !== false;
    const disabledHosts = Array.isArray(settings.disabledHosts) ? settings.disabledHosts : [];
    const names = hostnames();
    siteOff = disabledHosts.some((h) => names.has(h));
    if (active()) start(); else stop();
  }

  function onEngineState(now, before) {
    const ready = (s) => !!s && (s.pagesReady === true || s.ready === true);
    if (!ready(now) || ready(before)) return;
    // Translation became possible: retry images that were waiting.
    document.querySelectorAll('img').forEach((img) => {
      const st = states.get(img);
      if (st && st.status === 'waiting') enqueue(img, st.forced);
    });
  }

  async function init() {
    try {
      chrome.storage.onChanged.addListener((changes, area) => {
        if (dead) return;
        if (area === 'sync' && (changes.enabled || changes.images || changes.disabledHosts)) {
          chrome.storage.sync.get(['enabled', 'images', 'disabledHosts']).then(applySettings, () => {});
        } else if (area === 'local' && changes.engineState) {
          onEngineState(changes.engineState.newValue, changes.engineState.oldValue);
        }
      });
      applySettings(await chrome.storage.sync.get(['enabled', 'images', 'disabledHosts']));
    } catch (e) {
      if (isInvalidated(e)) shutdown();
    }
  }

  if (document.documentElement) init();
  else document.addEventListener('DOMContentLoaded', init, { once: true });
})();
