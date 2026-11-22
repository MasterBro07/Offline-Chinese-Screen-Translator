# Offline Chinese Screen Translator

*The app inside is called **AutoTranslator**.*

[![Build](https://github.com/MasterBro07/Offline-Chinese-Screen-Translator/actions/workflows/build.yml/badge.svg?branch=main)](https://github.com/MasterBro07/Offline-Chinese-Screen-Translator/actions/workflows/build.yml?query=branch%3Amain)
![Extension](https://img.shields.io/badge/extension-Windows%20%7C%20macOS%20%7C%20Linux-0078D6)
![App](https://img.shields.io/badge/app-Windows%2010%20%7C%2011-0078D6)
![Offline](https://img.shields.io/badge/translation-100%25%20offline-1a7f37)
![Price](https://img.shields.io/badge/price-free-1a7f37)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue)](LICENSE)
![Vibe-coded](https://img.shields.io/badge/vibe--coded-with%20Claude%20Code-d97757)

**AutoTranslator translates Chinese text on your screen into English, automatically and without a single click.**
It works in Chrome and in ordinary Windows programs (shop apps, chat apps, games, installers) by drawing the
English translation directly on top of the Chinese original.

**Everything runs on your own computer.** Translation uses the translator built into Google Chrome,
text recognition uses the one built into Windows, and the two parts talk to each other only inside your PC.
No account, no API key, no subscription, and no screen text is ever uploaded.

> **🤖 Vibe-coded.** This whole project was built with AI: the author described what they wanted in plain
> language, and [Claude Code](https://claude.com/claude-code) planned, wrote, tested and documented the code.
> No line was written by hand. The app was built and tested automatically on Windows (see
> [Known limits](#known-limits)) and works well for the author, but treat it as a hobby project.

*Deutsche Anleitung: [README.de.md](README.de.md)*

![How AutoTranslator works: screen capture, Windows OCR, filter and cache, translation by Chrome's on-device translator, English overlay. Everything runs offline on your PC; the internet is used only once to download the language pack.](docs/workflow.svg)

---

## Contents

- [Why offline matters](#why-offline-matters)
- [What you get](#what-you-get)
- [Which part should I use?](#which-part-should-i-use)
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

Shop apps, chats and order pages are full of personal data: names, addresses, messages, order numbers.
Online translators send all of that to a server. AutoTranslator does not.

| Part | Where it runs | Uses the internet? |
|---|---|---|
| Reading text on screen (OCR) | Windows' built-in text recognition | **No** |
| Translating Chinese ↔ English | Chrome's built-in on-device translator | **No**, after a one-time language pack download |
| Reading text in images | Text recognition model built into the extension (PaddleOCR) | **No**, the model ships with the extension |
| App ↔ Chrome extension | WebSocket on `127.0.0.1` (your own PC) | **No**, only accepts the Chrome extension |
| Settings and translation cache | Files in `%AppData%\AutoTranslator` | **No** |
| Telemetry, accounts, API keys | none | — |

The only download is the language packs (Chinese → English, and English → Chinese for the writing
helper), which Chrome fetches once from Google when you click **"Sprachpakete laden"**. After that you can unplug the network cable and translation keeps working.

## What you get

- **No clicks.** Chinese text is translated the moment it appears and keeps being translated while you
  scroll, chat or browse.
- **Websites:** the Chrome extension replaces Chinese text on the page itself, including text loaded later
  (infinite scroll, pop-ups, chats), iframes, drop-down menus and image captions. Input fields and passwords
  are never touched.
- **Text in images:** Chinese text in product photos, banners and long detail pictures is recognised and
  translated automatically; the English is drawn right over it. See [Text in images](#text-in-images).
- **Writing in Chinese:** type English into a text field on a Chinese page and the Chinese translation
  appears right below it; one click inserts it. See [Writing in Chinese](#writing-in-chinese).
- **Windows programs:** the desktop app reads the active window and draws the English text on a transparent
  layer over the original. Clicks go straight through to the program underneath.
- **Fast:** in tests, new text was translated and shown about 0.5 s after it appeared.
- **Light:** a small tray icon next to the clock; when the screen does not change, it does almost nothing.
- **Free** and open source.

> The user interface of the app and the extension is in German. Pages are translated Chinese → English;
> the writing helper translates English → Chinese.

## Which part should I use?

**For websites, use the Chrome extension. It is the better choice for everyday things like online shopping
or ordering food on Uber Eats.** It reads the real text of the page instead of a screenshot, so it is
faster and more accurate: the English appears directly in the page, stays in place while you scroll, and
small print, prices and menus are translated without recognition errors.

| You want to translate … | Use | Why |
|---|---|---|
| Websites in Chrome: shops, food delivery, news, social media | **Chrome extension** ⭐ | Instant, exact, translated right inside the page |
| Programs outside the browser: desktop apps, games, installers | **Desktop app** (`AutoTranslator.exe`) | Reads the screen with OCR and draws English on top |

The extension also works on its own: if you only need websites, steps 1 and 2 of the tutorial are enough.
Turn the desktop app off (Ctrl + Alt + T) while browsing so it does not add a second overlay on top of
already translated pages.

## Requirements

| Part | Works on |
|---|---|
| **Chrome extension** (websites) | **Windows, macOS and Linux**: Google Chrome 138 or newer on a desktop computer |
| **Desktop app** (other programs) | Windows 10 (version 2004 or newer) or Windows 11, 64-bit |

- About 10 minutes for the one-time setup (2 minutes if you only need the extension)
- Not available on phones: Chrome on Android and iPhone has no extensions and no on-device translator.

**On a Mac or Linux PC** you only need the extension: do steps 1 and 2 of the tutorial below
(download only `AutoTranslator-chrome-extension.zip`). Everything else works exactly as on Windows,
including the offline translation.

## Installation tutorial

### Step 1 – Download

1. Open the [**latest release**](https://github.com/MasterBro07/Offline-Chinese-Screen-Translator/releases/latest)
   and download both ZIP files:
   - `AutoTranslator-windows-x64.zip`: the Windows app
   - `AutoTranslator-chrome-extension.zip`: the Chrome extension

   *No release yet?* Use the newest green run of the
   [**Build** workflow](https://github.com/MasterBro07/Offline-Chinese-Screen-Translator/actions/workflows/build.yml?query=branch%3Amain)
   instead and download the two **Artifacts** at the bottom (requires a GitHub sign-in).
2. Unzip both (right-click → **Extract All…**) to a permanent place, for example
   `C:\AutoTranslator\app` and `C:\AutoTranslator\extension`. Do not delete or move these folders later.

### Step 2 – Install the Chrome extension

1. In Chrome, type `chrome://extensions` into the address bar and press Enter.
2. Turn on **Developer mode** (switch in the top right corner).
3. Click **Load unpacked** and select the `extension` folder.
4. A welcome page opens. Click **"Sprachpakete laden"** (download language packs) and wait until the status
   says **"bereit"** (ready). One click downloads both packs: Chinese → English for pages and
   English → Chinese for the writing helper. This is the only time anything is downloaded.

Websites in Chrome are now translated automatically. Tabs that were already open need one reload.

> **Only need websites (shopping, Uber Eats, news …)? You are done here.** Steps 3 and 4 are only for
> programs outside the browser.

### Step 3 – Add Chinese text recognition to Windows

Windows needs its Chinese OCR component to read Chinese characters on screen. Choose one way:

- **Settings:** Settings → Time & language → Language & region → **Add a language** →
  **Chinese (Simplified)**. You do not need to change your Windows display language.
- **Command:** right-click the Start button → **Terminal (Admin)** and run

  ```powershell
  Add-WindowsCapability -Online -Name "Language.OCR~~~zh-CN~0.0.1.0"
  ```

### Step 4 – Start the app

1. Double-click `AutoTranslator.exe` in the `app` folder.
2. If Windows shows **"Windows protected your PC"**, click **More info** → **Run anyway**. The warning
   appears because the app is not code-signed.
3. The app now sits as a round blue **A** icon in the notification area next to the clock (maybe hidden
   behind the `^` arrow). On the first start a help window shows whether the extension and text recognition
   are ready.

**Done.** Open any program or website with Chinese text: the English translation appears on top of it.

> **Tip:** to start AutoTranslator with Windows, press `Win + R`, type `shell:startup`, press Enter and put a
> shortcut to `AutoTranslator.exe` into that folder.

## Text in images

Shops put a lot of their text into pictures: prices and offers in banners, sizes and materials in long
detail images. The extension translates those too, automatically and offline.

- Larger images near the visible part of a Chinese page are read one after another, about 0.5 s per
  picture (a long detail image takes a few seconds). The English appears as dark boxes over the Chinese.
- The small **原文** button on an image shows the original; click again for the translation.
- **Right-click an image → "Bild übersetzen"** translates a single image at once, also small ones.
- Turn it off in the extension popup (*Bilder automatisch übersetzen*).

The text recognition (PaddleOCR PP-OCRv4, run with ONNX Runtime) is built into the extension, which is
why the extension download is about 30 MB. Images are read on your PC and never uploaded. Decorative fonts,
very small or curved text and text on busy backgrounds can be misread.

## Writing in Chinese

Need to message a seller, search a Chinese shop or fill in a form? Just type in English.

1. Click into a text field on a page with Chinese text (chat box, search, comment, message …).
2. Type in English. After a short pause a dark bar below the field shows the Chinese translation.
3. Click **Einfügen** (insert) or press **Ctrl + Shift + Enter**: the Chinese text replaces what you typed.
   **Esc** or **×** closes the bar for that field.

The helper stays out of the way: it only appears on pages with Chinese text, only for English input, and
never on password, e-mail, phone, number or card-number fields. What you type is translated on your PC,
is not stored, and is only written into the field when you click *Einfügen*. Turn it off in the
extension popup (*Schreibhilfe in Eingabefeldern*).

## Everyday use

| Action | How |
|---|---|
| Turn translation on / off | **Ctrl + Alt + T**, or right-click the tray icon → *Übersetzung aktiv* |
| Choose how text is read | Tray menu → *Texterkennung*: **OCR** (reads everything visible, default), **UI-Automation** (reads text straight from the program, very exact but not supported everywhere), or **Beides** (both) |
| Choose what is read | Tray menu → *Bereich*: **Aktives Fenster** (active window, faster) or **Ganzer Hauptbildschirm** (whole primary screen) |
| Translate paragraphs as a whole | Tray menu → *Zeilen zu Absätzen zusammenfassen* (on by default; turn off for lists with many short entries) |
| Show the original text of an image | Click **原文** on the image (again for the translation) |
| Translate one particular image | Right-click it → **Bild übersetzen** |
| Write Chinese in a text field | Type English, then **Einfügen** or **Ctrl + Shift + Enter** (see [Writing in Chinese](#writing-in-chinese)) |
| Pause websites in Chrome | Click the extension icon → untick *Webseiten automatisch übersetzen*, or disable it for one site only |
| See status and setup help | Tray menu → *Einrichtung / Hilfe…* (or double-click the tray icon) |
| Quit | Tray menu → *Beenden* |

Chrome has to be open while you translate, because the app uses Chrome's translator. It can stay minimized.

## Troubleshooting

| Problem | Fix |
|---|---|
| Status says **"Chrome-Erweiterung nicht verbunden"** | Open Chrome and check that the extension is enabled on `chrome://extensions`. |
| Extension popup says **"Sprachpakete fehlen"** | Click **"Sprachpakete laden"** in the popup and wait until it says *bereit*. |
| The writing helper says a language pack is missing | Same fix: **"Sprachpakete laden"** in the popup. |
| Help window says the **Chinese OCR is missing** | Do step 3, then quit and restart AutoTranslator. |
| **"AutoTranslator läuft bereits"** | The app is already running; look for the icon next to the clock. |
| Small text is not translated | Very small fonts (about 12 px) are hard to read for OCR. Zoom in (Ctrl + Plus) or try *UI-Automation*. |
| Ctrl + Alt + T does nothing | Another program uses that shortcut; use the tray menu instead. The help window shows this too. |
| Something else | Send the file `%AppData%\AutoTranslator\log.txt` (paste that path into the Explorer address bar). |

## Known limits

- Translation direction is Chinese → English only.
- OCR is not perfect: very small text, decorative fonts and busy backgrounds can cause mistakes.
- Exclusive full-screen games and copy-protected video cannot be captured by any screen reader.
- The translation shows up about half a second after the text; while scrolling it briefly lags behind.
- Tested automatically on Windows Server 2022 and 2025 (1 monitor, 100 % scaling). Several monitors with
  different scaling were not tested.

## How it works

1. **Capture:** every 0.7 s the app grabs the active window. If nothing changed, the cycle stops here.
2. **Recognise:** Windows OCR finds Chinese lines and their exact positions.
3. **Filter & cache:** only lines with Chinese characters are kept, wrapped lines are joined into paragraphs,
   and anything translated before comes straight from the local cache.
4. **Translate:** new text goes over a local WebSocket (`127.0.0.1:47613`) to the Chrome extension, which
   translates it with Chrome's built-in on-device Translator API.
5. **Overlay:** the app draws the English text over the original in a transparent, click-through window that
   is hidden from its own screen capture, so it never re-reads its own translations.

On websites the extension does the same job inside the page: it finds Chinese text, translates it on-device
and replaces it in place, and a watcher picks up anything added later.

The local connection only accepts Chrome extensions (`chrome-extension://` origin); websites cannot connect.

## For developers

| Path | Contents |
|---|---|
| `src/AutoTranslator.Core` | Platform-independent logic: bridge server, pipeline, cache, line merging (.NET 8) |
| `src/AutoTranslator.App` | WPF tray app: screen capture, Windows OCR, UI Automation, overlay |
| `extension/` | Chrome MV3 extension, plain JavaScript, no build step |
| `tests/` | xUnit tests for Core and Windows smoke tests for the real app |
| `extension-tests/` | Playwright tests for the extension |

- Build: GitHub Actions ([`build.yml`](.github/workflows/build.yml)) runs the tests and publishes a
  self-contained `AutoTranslator.exe` plus the extension as artifacts.
- Release: run the [`Release`](.github/workflows/release.yml) workflow by hand (Actions → Release →
  *Run workflow*); it builds both parts and publishes them as ZIP files on a new GitHub Release.
- Core tests run on any OS: `dotnet test tests/AutoTranslator.Core.Tests`
- Before loading the extension from a git checkout, fetch the offline OCR files (about 29 MB, not in git):
  `node tools/fetch-ocr-assets.mjs`. The workflows do this automatically for builds and releases.
- Extension tests: `npm i playwright ws && node extension-tests/run.js` (includes real offline OCR checks)
- Architecture and the bridge protocol: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)

## License

[MIT](LICENSE): free to use, change and share, also commercially, as long as the copyright notice stays in.
