# Architecture

Auto Translator is a Chrome MV3 extension (plain JavaScript, no build step). Everything runs on the
user's computer: Chrome's on-device Translator API for translation, and PaddleOCR (ONNX Runtime Web,
WebAssembly) bundled with the extension for text in images. The only network access is Chrome downloading
the language packs once, and the engine loading the images the page already shows.

```
web page ─┬─ content.js   page text, attributes, shadow DOM, frames  ──┐
          ├─ images.js    images in view → OCR request, overlays      ─┤  chrome.runtime messages
          └─ compose.js   writing helper in text fields (en → zh)     ─┤
                                                                       ▼
                         background.js (service worker): validation, routing, context menu
                                                                       │
                         engine.js in offscreen.html (or pinned engine.html tab as fallback)
                           ├─ Translator pool zh→en / en→zh, LRU cache, readiness → engineState
                           └─ ocr/ocr.js: PP-OCRv4 detection + recognition, tiling for tall images
```

## Files

| File | Role |
|---|---|
| `manifest.json` | MV3; permissions `storage`, `offscreen`, `tabs`, `alarms`, `contextMenus`, `scripting`; host access `<all_urls>`; CSP allows `wasm-unsafe-eval` for the OCR runtime |
| `background.js` | Keeps exactly one engine host alive (offscreen document, else a pinned tab), validates every message from content scripts, routes `translate` and `ocrImage`, right-click menu "Bild übersetzen", screenshot fallback for images the engine cannot download |
| `engine.js` | Translator instances per language pair, translation cache, readiness polling, publishes `engineState`; runs the image OCR queue (one image at a time, results cached per URL) |
| `ocr/ocr.js` | Offline OCR: detector (DB post-processing, connected components, unclip), recogniser (CTC greedy decoding), tall images in overlapping tiles |
| `content.js` | Finds and replaces Chinese text in place, batching with rate limits, MutationObserver, restores originals when switched off |
| `images.js` | Picks larger images near the viewport on Chinese pages, requests OCR, draws English boxes over the image in a shadow root (object-fit aware, follows scrolling), "原文" toggle |
| `compose.js` | Writing helper: preview bar below text fields, inserts the Chinese translation on request |
| `pack.js`, `popup.*`, `welcome.*` | Language pack status and one-click download (needs a user gesture), switches |

## Messages

Content scripts may only send:

| Message | Answer |
|---|---|
| `{type:'translate', source, target, texts}` with pair `zh→en`, `zh-Hant→en` or `en→zh` | `{ok, translations}` (same order and length) or `{ok:false, notReady?, error}` |
| `{type:'ocrImage', url, rect?, dpr?}` (http/https URL) | `{ok, width, height, space:'image'\|'css', lines:[{box:{x,y,w,h}, zh, en}]}` |

`engine.*` messages are accepted only from the engine documents, `recheck` only from extension pages.

`chrome.storage.local.engineState` (written by the service worker from the engine's reports):
`{apiPresent, availability, ready, host, pagesReady, pageEngine:'chrome', writeReady, detail}`.

## Settings (`chrome.storage.sync`)

`enabled` (page translation), `disabledHosts` (sites switched off), `images` (text in images), `compose`
(writing helper). All default to on.

## OCR assets

`extension/ocr/models/` (det/rec ONNX + dictionary) and `extension/ocr/ort/` (ONNX Runtime Web, WASM
backend) are not in git. `tools/fetch-ocr-assets.mjs` downloads pinned npm versions
(`@gutenye/ocr-models@1.4.2`, `onnxruntime-web@1.31.1`), checks their SHA-512 integrity and extracts only the
needed files; the build, release and test workflows run it.

## Tests

`extension-tests/run.js` (Playwright) loads the extension with a test translator (`debugFakeTranslator` in
`chrome.storage.local`; answers `EN[...]` / `ZH[...]`) and runs real offline OCR on generated images.
The workflow `extension.yml` runs it on Windows, macOS and Linux.
