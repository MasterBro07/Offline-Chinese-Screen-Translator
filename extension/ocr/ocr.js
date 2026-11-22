// Offline text recognition for Chinese text in images (PaddleOCR PP-OCRv4, run with ONNX Runtime
// Web on WebAssembly). Loaded by the engine document (offscreen.html / engine.html) after
// ocr/ort/ort.wasm.min.js. Models and runtime ship with the extension (tools/fetch-ocr-assets.mjs);
// nothing is downloaded at run time.
//
//   const lines = await AutoTranslatorOcr.recognize(imageBitmap);
//   // -> [{ text, score, box: { x, y, w, h } }] in image pixels, top to bottom

const AutoTranslatorOcr = (() => {
  'use strict';

  const DET_MAX_SIDE = 960; // longest side fed to the detector
  const DET_MAX_UPSCALE = 2; // small images are enlarged at most this much
  const DET_THRESHOLD = 0.3; // probability that counts as "text pixel"
  const DET_BOX_SCORE = 0.6; // mean probability a region needs to count as text
  const DET_UNCLIP = 1.5; // DB "unclip" ratio: the model predicts shrunken text regions
  const TILE_ASPECT = 1.3; // tall images are cut into tiles of height = width * TILE_ASPECT
  const TILE_OVERLAP = 0.15;
  const REC_HEIGHT = 48;
  const REC_MAX_WIDTH = 1600;
  const REC_MIN_SCORE = 0.5;
  const HAN = /\p{Script=Han}/u;

  let loading = null;

  function load() {
    if (!loading) {
      loading = (async () => {
        if (typeof ort === 'undefined') throw new Error('ocr-runtime-missing');
        ort.env.wasm.wasmPaths = chrome.runtime.getURL('ocr/ort/');
        ort.env.wasm.numThreads = 1; // no SharedArrayBuffer / cross-origin isolation needed
        const url = (f) => chrome.runtime.getURL('ocr/models/' + f);
        const [det, rec, keyText] = await Promise.all([
          ort.InferenceSession.create(url('det.onnx')),
          ort.InferenceSession.create(url('rec.onnx')),
          fetch(url('keys.txt')).then((r) => { if (!r.ok) throw new Error('ocr-keys-missing'); return r.text(); }),
        ]);
        const lines = keyText.split(/\r?\n/);
        if (lines[lines.length - 1] === '') lines.pop();
        // Output classes: 0 = CTC blank, 1..n = dictionary, n + 1 = space.
        const keys = ['', ...lines, ' '];
        return { det, rec, keys };
      })();
      loading.catch(() => { loading = null; });
    }
    return loading;
  }

  /** True when the model files are part of this installation. */
  async function available() {
    try {
      const r = await fetch(chrome.runtime.getURL('ocr/models/det.onnx'), { method: 'HEAD' });
      return r.ok && typeof ort !== 'undefined';
    } catch (e) {
      return false;
    }
  }

  // ------------------------------------------------------------------ pixels

  function context(w, h) {
    const canvas = new OffscreenCanvas(w, h);
    return canvas.getContext('2d', { willReadFrequently: true });
  }

  /** CHW float tensor data of an RGBA image, normalised per channel. */
  function toChw(data, w, h, mean, std) {
    const plane = w * h;
    const out = new Float32Array(3 * plane);
    for (let i = 0, p = 0; p < plane; p++, i += 4) {
      out[p] = (data[i] / 255 - mean[0]) / std[0];
      out[plane + p] = (data[i + 1] / 255 - mean[1]) / std[1];
      out[2 * plane + p] = (data[i + 2] / 255 - mean[2]) / std[2];
    }
    return out;
  }

  // --------------------------------------------------------------- detection

  /** Text boxes in one region (sx, sy, sw, sh) of the image, in image pixels. */
  async function detectRegion(models, image, sx, sy, sw, sh) {
    const scale = Math.min(DET_MAX_SIDE / Math.max(sw, sh), DET_MAX_UPSCALE);
    const w = Math.max(32, Math.round((sw * scale) / 32) * 32);
    const h = Math.max(32, Math.round((sh * scale) / 32) * 32);
    const ctx = context(w, h);
    ctx.fillStyle = '#fff';
    ctx.fillRect(0, 0, w, h);
    ctx.drawImage(image, sx, sy, sw, sh, 0, 0, w, h);
    const pixels = ctx.getImageData(0, 0, w, h).data;
    const input = new ort.Tensor('float32', toChw(pixels, w, h, [0.485, 0.456, 0.406], [0.229, 0.224, 0.225]), [1, 3, h, w]);
    const out = await models.det.run({ [models.det.inputNames[0]]: input });
    const prob = out[models.det.outputNames[0]].data;

    // Connected regions of text pixels (4-neighbourhood).
    const label = new Int32Array(w * h);
    const stack = new Int32Array(w * h);
    const boxes = [];
    let next = 0;
    for (let start = 0; start < w * h; start++) {
      if (label[start] || prob[start] < DET_THRESHOLD) continue;
      next++;
      let top = 0;
      stack[top++] = start;
      label[start] = next;
      let minX = w, minY = h, maxX = 0, maxY = 0, sum = 0, count = 0;
      while (top) {
        const p = stack[--top];
        const x = p % w;
        const y = (p - x) / w;
        sum += prob[p];
        count++;
        if (x < minX) minX = x;
        if (x > maxX) maxX = x;
        if (y < minY) minY = y;
        if (y > maxY) maxY = y;
        if (x > 0 && !label[p - 1] && prob[p - 1] >= DET_THRESHOLD) { label[p - 1] = next; stack[top++] = p - 1; }
        if (x < w - 1 && !label[p + 1] && prob[p + 1] >= DET_THRESHOLD) { label[p + 1] = next; stack[top++] = p + 1; }
        if (y > 0 && !label[p - w] && prob[p - w] >= DET_THRESHOLD) { label[p - w] = next; stack[top++] = p - w; }
        if (y < h - 1 && !label[p + w] && prob[p + w] >= DET_THRESHOLD) { label[p + w] = next; stack[top++] = p + w; }
      }
      const bw = maxX - minX + 1;
      const bh = maxY - minY + 1;
      if (bw < 3 || bh < 3 || sum / count < DET_BOX_SCORE) continue;
      // DB unclip: grow by area * ratio / perimeter on every side.
      const d = (bw * bh * DET_UNCLIP) / (2 * (bw + bh));
      const x0 = Math.max(0, minX - d);
      const y0 = Math.max(0, minY - d);
      const x1 = Math.min(w, maxX + 1 + d);
      const y1 = Math.min(h, maxY + 1 + d);
      boxes.push({
        x: sx + (x0 / w) * sw,
        y: sy + (y0 / h) * sh,
        w: ((x1 - x0) / w) * sw,
        h: ((y1 - y0) / h) * sh,
      });
    }
    return boxes;
  }

  function overlap(a, b) {
    const ix = Math.max(0, Math.min(a.x + a.w, b.x + b.w) - Math.max(a.x, b.x));
    const iy = Math.max(0, Math.min(a.y + a.h, b.y + b.h) - Math.max(a.y, b.y));
    return (ix * iy) / Math.min(a.w * a.h, b.w * b.h);
  }

  /** All text boxes; tall images (shop detail pictures) are processed in overlapping tiles. */
  async function detect(models, image) {
    const W = image.width;
    const H = image.height;
    if (H <= W * 2) return detectRegion(models, image, 0, 0, W, H);
    const tileH = Math.round(W * TILE_ASPECT);
    const step = Math.round(tileH * (1 - TILE_OVERLAP));
    const all = [];
    for (let y = 0; y < H; y += step) {
      const sh = Math.min(tileH, H - y);
      const first = y === 0;
      const last = y + sh >= H;
      for (const box of await detectRegion(models, image, 0, y, W, sh)) {
        // A box touching an inner tile edge is cut; the neighbouring tile has it whole.
        const margin = 2;
        if (!first && box.y <= y + margin) continue;
        if (!last && box.y + box.h >= y + sh - margin) continue;
        if (!all.some((b) => overlap(b, box) > 0.5)) all.push(box);
      }
      if (last) break;
    }
    return all;
  }

  // ------------------------------------------------------------- recognition

  async function recognizeBox(models, image, box) {
    const vertical = box.h >= box.w * 1.5;
    const srcW = vertical ? box.h : box.w;
    const srcH = vertical ? box.w : box.h;
    const w = Math.max(16, Math.min(REC_MAX_WIDTH, Math.round((REC_HEIGHT * srcW) / srcH)));
    const ctx = context(w, REC_HEIGHT);
    ctx.fillStyle = '#fff';
    ctx.fillRect(0, 0, w, REC_HEIGHT);
    if (vertical) {
      // Vertical text: rotate so that the characters run left to right.
      ctx.translate(0, REC_HEIGHT);
      ctx.rotate(-Math.PI / 2);
      ctx.drawImage(image, box.x, box.y, box.w, box.h, 0, 0, REC_HEIGHT, w);
      ctx.setTransform(1, 0, 0, 1, 0, 0);
    } else {
      ctx.drawImage(image, box.x, box.y, box.w, box.h, 0, 0, w, REC_HEIGHT);
    }
    const pixels = ctx.getImageData(0, 0, w, REC_HEIGHT).data;
    const input = new ort.Tensor('float32', toChw(pixels, w, REC_HEIGHT, [0.5, 0.5, 0.5], [0.5, 0.5, 0.5]), [1, 3, REC_HEIGHT, w]);
    const out = await models.rec.run({ [models.rec.inputNames[0]]: input });
    const tensor = out[models.rec.outputNames[0]];
    const [, steps, classes] = tensor.dims;
    const probs = tensor.data;
    // CTC greedy decoding.
    let text = '';
    let sum = 0;
    let count = 0;
    let prev = 0;
    for (let t = 0; t < steps; t++) {
      let best = 0;
      let bestP = -Infinity;
      const base = t * classes;
      for (let c = 0; c < classes; c++) {
        const p = probs[base + c];
        if (p > bestP) { bestP = p; best = c; }
      }
      if (best !== 0 && best !== prev) {
        text += models.keys[best] || '';
        sum += bestP;
        count++;
      }
      prev = best;
    }
    return { text: text.trim(), score: count ? sum / count : 0 };
  }

  /** Recognises Chinese text lines in an ImageBitmap (or any CanvasImageSource with width/height). */
  async function recognize(image) {
    const models = await load();
    const boxes = await detect(models, image);
    boxes.sort((a, b) => (Math.abs(a.y - b.y) < Math.min(a.h, b.h) / 2 ? a.x - b.x : a.y - b.y));
    const lines = [];
    for (const box of boxes) {
      const { text, score } = await recognizeBox(models, image, box);
      if (text && score >= REC_MIN_SCORE && HAN.test(text)) {
        lines.push({
          text,
          score: Math.round(score * 1000) / 1000,
          box: { x: Math.round(box.x), y: Math.round(box.y), w: Math.round(box.w), h: Math.round(box.h) },
        });
      }
    }
    return lines;
  }

  return { load, available, recognize };
})();
