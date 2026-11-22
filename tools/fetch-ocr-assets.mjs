#!/usr/bin/env node
// Downloads the offline OCR files for the Chrome extension from the npm registry:
//   - PaddleOCR PP-OCRv4 Chinese models (@gutenye/ocr-models)
//   - ONNX Runtime Web, WebAssembly backend only (onnxruntime-web)
// Versions are pinned and every tarball is checked against the SHA-512 integrity the
// registry publishes. Only the needed files are written, to extension/ocr/models and
// extension/ocr/ort. No dependencies; Node 18+.
//
//   node tools/fetch-ocr-assets.mjs            (skips files that already exist)
//   node tools/fetch-ocr-assets.mjs --force    (downloads again)
//
// Behind an HTTP proxy, run Node with NODE_USE_ENV_PROXY=1 (Node 22.21+).

import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gunzipSync } from 'node:zlib';

const ROOT = join(dirname(fileURLToPath(import.meta.url)), '..');
const OCR_DIR = join(ROOT, 'extension', 'ocr');
const FORCE = process.argv.includes('--force');

const PACKAGES = [
  {
    name: '@gutenye/ocr-models',
    version: '1.4.2',
    files: {
      'package/assets/ch_PP-OCRv4_det_infer.onnx': 'models/det.onnx',
      'package/assets/ch_PP-OCRv4_rec_infer.onnx': 'models/rec.onnx',
      'package/assets/ppocr_keys_v1.txt': 'models/keys.txt',
    },
  },
  {
    name: 'onnxruntime-web',
    version: '1.31.1',
    files: {
      'package/dist/ort.wasm.min.js': 'ort/ort.wasm.min.js',
      'package/dist/ort-wasm-simd-threaded.mjs': 'ort/ort-wasm-simd-threaded.mjs',
      'package/dist/ort-wasm-simd-threaded.wasm': 'ort/ort-wasm-simd-threaded.wasm',
    },
  },
];

async function getBuffer(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${url}: HTTP ${res.status}`);
  return Buffer.from(await res.arrayBuffer());
}

/** Minimal tar reader: returns Map(path -> Buffer) for regular files. */
function readTar(buf) {
  const out = new Map();
  let off = 0;
  while (off + 512 <= buf.length) {
    const header = buf.subarray(off, off + 512);
    if (header.every((b) => b === 0)) break;
    const str = (start, len) => header.subarray(start, start + len).toString('utf8').replace(/\0.*$/s, '');
    const name = str(0, 100);
    const prefix = str(345, 155);
    const size = parseInt(str(124, 12).trim() || '0', 8);
    const type = str(156, 1);
    const path = prefix ? `${prefix}/${name}` : name;
    const start = off + 512;
    if (type === '0' || type === '') out.set(path, buf.subarray(start, start + size));
    off = start + Math.ceil(size / 512) * 512;
  }
  return out;
}

async function fetchPackage(pkg) {
  const targets = Object.values(pkg.files).map((rel) => join(OCR_DIR, rel));
  if (!FORCE && targets.every((t) => existsSync(t))) {
    console.log(`${pkg.name}@${pkg.version}: already present`);
    return;
  }
  const meta = JSON.parse((await getBuffer(`https://registry.npmjs.org/${pkg.name}/${pkg.version}`)).toString('utf8'));
  const { tarball, integrity } = meta.dist;
  if (!integrity || !integrity.startsWith('sha512-')) throw new Error(`${pkg.name}: no sha512 integrity in registry metadata`);
  const tgz = await getBuffer(tarball);
  const digest = 'sha512-' + createHash('sha512').update(tgz).digest('base64');
  if (digest !== integrity) throw new Error(`${pkg.name}@${pkg.version}: integrity mismatch`);
  const entries = readTar(gunzipSync(tgz));
  for (const [from, rel] of Object.entries(pkg.files)) {
    const data = entries.get(from);
    if (!data) throw new Error(`${pkg.name}@${pkg.version}: ${from} not found in tarball`);
    const to = join(OCR_DIR, rel);
    mkdirSync(dirname(to), { recursive: true });
    writeFileSync(to, data);
    console.log(`${pkg.name}@${pkg.version}: ${rel} (${(data.length / 1048576).toFixed(1)} MB)`);
  }
}

try {
  for (const pkg of PACKAGES) await fetchPackage(pkg);
  writeFileSync(join(OCR_DIR, 'ort', 'NOTICE.txt'),
    'ONNX Runtime Web 1.31.1, Copyright (c) Microsoft Corporation, MIT License\n'
    + '(https://github.com/microsoft/onnxruntime/blob/main/LICENSE).\n');
  writeFileSync(join(OCR_DIR, 'models', 'NOTICE.txt'),
    'PaddleOCR PP-OCRv4 Chinese models (Apache License 2.0, https://github.com/PaddlePaddle/PaddleOCR),\n'
    + 'converted to ONNX and distributed in the npm package @gutenye/ocr-models (MIT).\n');
} catch (e) {
  console.error('fetch-ocr-assets failed:', e.message);
  process.exit(1);
}
