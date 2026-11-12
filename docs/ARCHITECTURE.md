# Architecture and contracts

Goal: everything Chinese on screen is translated to English automatically, with no click —
in Chrome pages and in ordinary Windows programs. Translation is free and on-device,
using Chrome's built-in Translator API.

```
┌──────────────── Windows desktop app (WPF, .NET 8) ────────────────┐
│ ITextSource (OCR / UI Automation) → TranslationPipeline → Overlay │
│                                   │                               │
│                    CachingTranslator → ChromeBridgeServer         │
└───────────────────────────────────────────┬───────────────────────┘
                                            │ ws://127.0.0.1:47613/bridge
┌──────────────── Chrome extension (MV3) ───┴───────────────────────┐
│ engine document (Translator API)  ←  service worker  ← content.js │
│   - answers desktop app requests        (page auto-translation)   │
└───────────────────────────────────────────────────────────────────┘
```

The desktop app cannot call Chrome's Translator API itself, so the extension connects
to the app over a localhost WebSocket and acts as its translation engine. The same
extension also translates web pages in place.

## Repository layout

| Path | What | Builds on |
|---|---|---|
| `src/AutoTranslator.Core` | Platform-independent logic (net8.0) | Linux + Windows |
| `tests/AutoTranslator.Core.Tests` | xUnit tests for Core | Linux + Windows |
| `src/AutoTranslator.App` | WPF tray app: capture, OCR, UIA, overlay (net8.0-windows) | Windows only (CI) |
| `extension/` | Chrome MV3 extension, plain JS, no build step | — |
| `.github/workflows/build.yml` | Builds, tests, publishes the exe and the extension zip | — |

## Bridge protocol (version 1)

WebSocket, JSON text frames, one JSON object per frame.
Server: desktop app, `ws://127.0.0.1:47613/bridge`. Client: the extension.
The server rejects connections whose `Origin` header does not start with `chrome-extension://`.
Only one client is active; a new connection replaces the old one.

| Direction | Message |
|---|---|
| ext → app | `{"type":"hello","version":1,"engine":"chrome-translator","ready":true,"detail":""}` |
| ext → app | `{"type":"status","ready":false,"detail":"Sprachpaket fehlt"}` (any time readiness changes) |
| app → ext | `{"type":"translate","id":"42","source":"zh","target":"en","texts":["你好","世界"]}` |
| ext → app | `{"type":"result","id":"42","translations":["Hello","World"]}` (same order and length) |
| ext → app | `{"type":"error","id":"42","message":"..."}` |
| app → ext | `{"type":"ping"}` → ext answers `{"type":"pong"}` |

`ready` means: a Translator for zh→en can be created without a user gesture
(`Translator.availability()` is `"available"`).

## Core public API (namespace `AutoTranslator.Core`)

`Contracts.cs` (already written, do not change): `ScreenRect`, `TextRegion`,
`TranslatedRegion`, `ITranslator`, `ITextSource`, `IOverlayPresenter`.

To be implemented in Core, with exactly these public shapes (the App is written against them):

```csharp
public static class ScriptDetector {
    public static bool ContainsHan(string? text);        // any CJK ideograph
    public static double HanRatio(string? text);         // Han chars / non-whitespace chars
}

public sealed class AppSettings {
    public string SourceLanguage { get; set; } = "zh";
    public string TargetLanguage { get; set; } = "en";
    public int BridgePort { get; set; } = 47613;
    public int CaptureIntervalMs { get; set; } = 700;
    public string TextSourceMode { get; set; } = "Ocr";  // "Ocr" | "UiAutomation" | "Both"
    public string CaptureScope { get; set; } = "ForegroundWindow"; // or "PrimaryMonitor"
    public bool Enabled { get; set; } = true;
    public static string DefaultDirectory { get; }       // %AppData%/AutoTranslator
    public static AppSettings Load(string? path = null); // never throws; defaults on error
    public void Save(string? path = null);
}

public sealed class TranslationCache {                   // thread-safe LRU, optional JSON file
    public TranslationCache(int capacity = 20000, string? filePath = null);
    public bool TryGet(string source, string target, string text, out string translation);
    public void Set(string source, string target, string text, string translation);
    public int Count { get; }
    public void Load();                                  // no-op without filePath; never throws
    public void Save();
}

public sealed class CachingTranslator : ITranslator {    // dedupes + caches, forwards misses
    public CachingTranslator(ITranslator inner, TranslationCache cache);
}

public sealed class ChromeBridgeServer : ITranslator, IAsyncDisposable {
    public ChromeBridgeServer(int port = 47613);
    public int Port { get; }                             // actual port (0 → ephemeral, for tests)
    public Task StartAsync(CancellationToken ct = default);
    public Task StopAsync();
    // TranslateAsync throws InvalidOperationException when no extension is connected,
    // TimeoutException after 15 s without an answer.
}

public sealed class PipelineOptions {
    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(700);
    public string SourceLanguage { get; set; } = "zh";
    public string TargetLanguage { get; set; } = "en";
}

public sealed class TranslationPipeline : IAsyncDisposable {
    public TranslationPipeline(ITextSource source, ITranslator translator,
                               IOverlayPresenter presenter, PipelineOptions options);
    public bool IsRunning { get; }
    public string StatusText { get; }                    // German, for the tray tooltip
    public event EventHandler? StatusChanged;
    public void Start();
    public Task StopAsync();                             // also clears the overlay
    public Task<int> RunOnceAsync(CancellationToken ct); // one cycle; returns regions shown
}
```

Pipeline cycle: `CaptureAsync` → `null` means unchanged, keep overlay → keep regions where
`ScriptDetector.ContainsHan` → translate (one batch) → `presenter.Show(...)`. No Han text →
`presenter.Clear()`. Translator not ready or throwing → `Clear()`, set status, keep looping.
A cycle never throws out of the loop.

## User-facing language

UI strings are German (the user is German). Code, comments and docs are English.
