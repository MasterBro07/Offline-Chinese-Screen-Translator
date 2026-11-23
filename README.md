# Offline Chinese Screen Translator

*The Chrome extension is called **Auto Translator**.*

[![Build](https://github.com/MasterBro07/Offline-Chinese-Screen-Translator/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/MasterBro07/Offline-Chinese-Screen-Translator/actions/workflows/build.yml?query=branch%3Amain)
![Chrome](https://img.shields.io/badge/Chrome-Windows%20%7C%20macOS%20%7C%20Linux-0078D6)
![Offline](https://img.shields.io/badge/translation-100%25%20offline-1a7f37)
![Price](https://img.shields.io/badge/price-free-1a7f37)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![Vibe-coded](https://img.shields.io/badge/vibe--coded-with%20Claude%20Code-d97757)

**Auto Translator is a Chrome extension that translates Chinese web pages into English, automatically and
without a single click**: the page text, the text in images, and, the other way round, what you type.

**Everything runs on your own computer.** Translation uses the translator built into Google Chrome, and
the text recognition for images is built into the extension. No account, no API key, no subscription,
and no page text or image is ever uploaded.

> **🤖 Vibe-coded.** This whole project was built with AI: the author described what they wanted in plain
> language, and [Claude Code](https://claude.com/claude-code) planned, wrote, tested and documented the code.
> No line was written by hand. It is tested automatically on Windows, macOS and Linux and works well for
> the author, but treat it as a hobby project.

*Deutsche Anleitung: [README.de.md](README.de.md)*

![How Auto Translator works: three pipelines. Page text: find Chinese text, convert traditional to simplified if needed, translate with Chrome's on-device translator, replace in place. Images: read the text with the built-in OCR, convert if needed, translate, draw English boxes over the image. Text fields: the writing helper translates English to Chinese and inserts it. Everything runs offline in Chrome; the internet is used once to download the language packs.](docs/workflow.svg)

---

## Contents

- [Why offline matters](#why-offline-matters)
- [What you get](#what-you-get)
- [Text in images](#text-in-images)
- [Writing in Chinese](#writing-in-chinese)
- [Requirements](#requirements)
- [Installation tutorial](#installation-tutorial)
- [Everyday use](#everyday-use)
- [Troubleshooting](#troubleshooting)
- [Known limits](#known-limits)
- [How it works](#how-it-works)
- [For developers](#for-developers)

---

## Why offline matters

Shops, chats and order pages are full of personal data: names, addresses, messages, order numbers.
Online translators send all of that to a server. Auto Translator does not.

| Part | Where it runs | Uses the internet? |
|---|---|---|
| Translating Chinese ↔ English | Chrome's built-in on-device translator | **No**, after a one-time language pack download |
| Reading text in images | Text recognition model built into the extension (PaddleOCR) | **No**, the model ships with the extension |
| Settings and caches | Inside Chrome on your PC | **No** |
| Telemetry, accounts, API keys | none | — |

The only download is the language packs (Chinese → English, and English → Chinese for the writing
helper), which Chrome fetches once from Google when you click **"Download language packs"**. After that you can
unplug the network cable and translation keeps working.

## What you get

- **No clicks.** Chinese text is translated the moment it appears and keeps being translated while you
  scroll, chat or browse: infinite scroll, pop-ups, chats, iframes, drop-down menus, image captions.
- **In place.** The English replaces the Chinese right in the page; hover over a text to see the original.
  Input fields and passwords are never touched.
- **Every page.** Also pages that mark themselves "do not translate" (common on booking and shop sites),
  because the extension only swaps text and never rebuilds the page.
- **Text in images:** Chinese text in product photos, banners and long detail pictures is recognised and
  translated automatically; the English is drawn right over it. See [Text in images](#text-in-images).
- **Writing in Chinese:** type English into a text field on a Chinese page and the Chinese translation
  appears right below it; one click inserts it. See [Writing in Chinese](#writing-in-chinese).
- **Simplified and traditional Chinese** (mainland, Taiwan, Hong Kong): traditional text is converted to
  simplified characters on your PC (OpenCC table) and then translated, so no extra pack is needed.
- **Free** and open source.

> Pages are translated Chinese → English; the writing helper translates English → Chinese.

## Text in images

Shops put a lot of their text into pictures: prices and offers in banners, sizes and materials in long
detail images. The extension translates those too, automatically and offline.

- Larger images near the visible part of a Chinese page are read one after another, about 0.5 s per
  picture (a long detail image takes a few seconds). The English appears as dark boxes over the Chinese.
- The small **Original** button on an image shows the original; click again for the translation.
- **Right-click an image → "Translate image"** translates a single image at once, also small ones.
- Turn it off in the extension popup (*Translate images automatically*).

The text recognition (PaddleOCR PP-OCRv4, run with ONNX Runtime) is built into the extension, which is
why the download is about 17 MB zipped (about 30 MB unpacked). Images are read on your PC and never
uploaded. Decorative fonts, very small or curved text and text on busy backgrounds can be misread.

## Writing in Chinese

Need to message a seller, search a Chinese shop or fill in a form? Just type in English.

1. Click into a text field on a page with Chinese text (chat box, search, comment, message …).
2. Type in English. After a short pause a dark bar below the field shows the Chinese translation.
3. Click **Insert** or press **Ctrl + Shift + Enter**: the Chinese text replaces what you typed.
   **Esc** or **×** closes the bar for that field.

The helper stays out of the way: it only appears on pages with Chinese text, only for English input, and
never on password, e-mail, phone, number or card-number fields. What you type is translated on your PC,
is not stored, and is only written into the field when you click *Insert*. Turn it off in the
extension popup (*Writing helper in text fields*).

## Requirements

- **Google Chrome 138 or newer** on a desktop computer: **Windows, macOS or Linux**
- About 2 minutes for the one-time setup
- Not available on phones: Chrome on Android and iPhone has no extensions and no on-device translator.

## Installation tutorial

### Step 1 – Download

1. Open the [**latest release**](https://github.com/MasterBro07/Offline-Chinese-Screen-Translator/releases/latest)
   and download `AutoTranslator-chrome-extension.zip`.

   *No release yet?* Use the newest green run of the
   [**Build** workflow](https://github.com/MasterBro07/Offline-Chinese-Screen-Translator/actions/workflows/build.yml?query=branch%3Amain)
   instead and download the **Artifact** at the bottom (requires a GitHub sign-in).
2. Unzip it (right-click → **Extract All…**) to a permanent place, for example `Documents\AutoTranslator`.
   Do not delete or move this folder later.

### Step 2 – Install the extension

1. In Chrome, type `chrome://extensions` into the address bar and press Enter.
2. Turn on **Developer mode** (switch in the top right corner).
3. Click **Load unpacked** and select the unzipped folder.
4. A welcome page opens. Click **"Download language packs"** and wait until the status
   says **"ready"**. One click downloads both packs: Chinese → English for pages and
   English → Chinese for the writing helper. This is the only time anything is downloaded.

**Done.** Chinese websites are now translated automatically. Tabs that were already open need one reload.

## Everyday use

| Action | How |
|---|---|
| Pause translation | Click the extension icon → untick *Translate web pages automatically* |
| Switch off for one site | Click the extension icon → *Turn off on this site* |
| See the original of a text | Hover over it |
| Show the original text of an image | Click **Original** on the image (again for the translation) |
| Translate one particular image | Right-click it → **Translate image** |
| Write Chinese in a text field | Type English, then **Insert** or **Ctrl + Shift + Enter** (see [Writing in Chinese](#writing-in-chinese)) |
| Switch features on / off | Extension popup: *Translate images automatically*, *Writing helper in text fields* |

## Troubleshooting

| Problem | Fix |
|---|---|
| Nothing is translated after updating or reinstalling the extension | Reload the open tabs once (**F5**). Chrome only adds the new version to pages opened after the update. |
| Extension popup says language packs **"missing"** | Click **"Download language packs"** in the popup and wait until it says *ready*. |
| The writing helper says a language pack is missing | Same fix: **"Download language packs"** in the popup. |
| The popup says the translator is not available | Update Chrome (version 138 or newer is needed) and use it on a desktop computer. |
| An image is not translated | Very small images are skipped; right-click → **Translate image**. Text in decorative fonts may not be recognised. |

## Known limits

- Translation direction is Chinese → English (and English → Chinese for the writing helper).
- Only in Google Chrome on desktop computers; not in other browsers or on phones.
- Chrome's on-device translator is a small model: its English is sometimes less natural than an online
  translator's.
- Text recognition in images is not perfect: decorative fonts, very small or curved text and busy
  backgrounds can cause mistakes.
- Images inside embedded frames whose server blocks direct loading cannot be read.

## How it works

The diagram above shows the three pipelines; step by step:

1. **Find:** content scripts look for Chinese text in the page (also in iframes, shadow DOM, attributes
   like placeholders and image captions) and watch for new text.
2. **Traditional → simplified:** text in traditional characters (Taiwan, Hong Kong) is converted with the
   built-in OpenCC table; simplified text passes unchanged.
3. **Translate:** the text goes to the extension's engine, which runs in a hidden extension page and uses
   Chrome's built-in on-device Translator API. Translations are cached, so repeated text is instant.
4. **Replace:** the English is written back into the page in place; the original is kept for the tooltip
   and for switching off.
5. **Images:** larger images are loaded by the engine and read with the built-in text recognition
   (PaddleOCR PP-OCRv4 on WebAssembly). The recognised lines go through steps 2 and 3 and are drawn as
   boxes over the image.
6. **Writing:** in text fields, English input is translated English → Chinese and inserted on request.

## For developers

| Path | Contents |
|---|---|
| `extension/` | Chrome MV3 extension, plain JavaScript, no build step |
| `extension/ocr/` | Offline text recognition (`ocr.js`); models and runtime are downloaded by the tool below |
| `extension-tests/` | Playwright end-to-end tests (fake translator, real offline OCR) |
| `extension/t2s.js` | Traditional → simplified character table (from OpenCC, Apache-2.0) |
| `tools/fetch-ocr-assets.mjs` | Downloads the OCR models and ONNX Runtime from npm (pinned, integrity-checked) |

- Before loading the extension from a git checkout, fetch the offline OCR files (about 29 MB, not in git):
  `node tools/fetch-ocr-assets.mjs`.
- Tests: `npm i playwright && npx playwright install chromium && node extension-tests/run.js`.
- Build: GitHub Actions ([`build.yml`](.github/workflows/build.yml)) packs the extension with the OCR files
  as an artifact. [`extension.yml`](.github/workflows/extension.yml) runs the tests on Windows, macOS and
  Linux (started by hand).
- Release: run the [`Release`](.github/workflows/release.yml) workflow by hand (Actions → Release →
  *Run workflow*); it publishes the extension as a ZIP file on a new GitHub Release.
- Architecture: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)

## License

[MIT](LICENSE): free to use, change and share, also commercially, as long as the copyright notice stays in.
