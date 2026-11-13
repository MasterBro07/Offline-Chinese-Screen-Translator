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

---

# Version 2 additions: Claude engine, page translation through the app

The user chose Claude Haiku (`claude-haiku-5-5`, Anthropic API, own API key) as an optional
higher-quality engine. The key lives only in the desktop app (DPAPI-protected). Web pages
use it through the bridge, so the key is entered once and never stored in the extension.

Engines: `"Chrome"` (default, free, on-device) and `"Claude"` (API). With `"Claude"`, the
Chrome engine remains the fallback whenever Claude fails (no key, network, limit reached).

## Bridge protocol version 2 (superset of version 1)

`hello` now carries `"version":2`. The app accepts version 1 clients (they just never send
the new messages). New messages:

| Direction | Message |
|---|---|
| app → ext | `{"type":"app-state","premium":true,"engine":"claude"}` — sent right after the client's `hello` and whenever it changes. `premium:true` means the app can translate page text with a better engine than the on-device one. |
| ext → app | `{"type":"page-translate","id":"p7","source":"zh","target":"en","texts":["..."]}` — only while `premium` is true |
| app → ext | `{"type":"page-result","id":"p7","translations":["..."]}` (same order and length) |
| app → ext | `{"type":"page-error","id":"p7","message":"..."}` → the extension falls back to the on-device translator for that batch |

Trust: any `chrome-extension://` origin may connect, but `page-translate` spends the user's
API credit. The app pins the first origin that connects (trust on first use, stored in
settings as `TrustedExtensionOrigin`); `page-translate` from any other origin is answered
with `page-error`. The tray menu can reset the pinned origin.

## Core public API additions

```csharp
public sealed class AppSettings {            // new properties (old ones unchanged)
    public string Engine { get; set; } = "Chrome";            // "Chrome" | "Claude"
    public string ClaudeModel { get; set; } = "claude-haiku-5-5";
    public long ClaudeDailyCharLimit { get; set; } = 300000;  // source chars per day; 0 = unlimited
    public bool MergeLines { get; set; } = true;
    public string? TrustedExtensionOrigin { get; set; }
}

public sealed class UsageTracker {            // thread-safe, per-day counter persisted as JSON
    public UsageTracker(string? filePath = null, long dailyCharLimit = 0);
    public long DailyCharLimit { get; set; }
    public long CharsToday { get; }           // resets when the local date changes
    public bool TryConsume(long chars);       // false (and nothing consumed) when over the limit
    public event EventHandler? Changed;
    public void Save();                       // never throws
}

public sealed class ClaudeTranslatorOptions {
    public string Model { get; set; } = "claude-haiku-5-5";
    public Uri? BaseUrl { get; set; }         // tests point this at a local fake server
    public int MaxBatchItems { get; set; } = 60;
    public int MaxBatchChars { get; set; } = 6000;
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}

public sealed class ClaudeTranslator : ITranslator {
    // apiKeyProvider returns null/empty when no key is configured → IsReady false.
    public ClaudeTranslator(Func<string?> apiKeyProvider, ClaudeTranslatorOptions? options = null,
                            UsageTracker? usage = null);
    public void NotifyKeyChanged();           // re-evaluates IsReady, raises StatusChanged
    public Task<string?> TestAsync(CancellationToken ct); // null = OK, else a German error text
}

public sealed class FallbackTranslator : ITranslator {  // primary when ready, else/on error fallback
    public FallbackTranslator(ITranslator primary, ITranslator fallback);
}

public sealed class CachingTranslator {       // new optional parameter
    public CachingTranslator(ITranslator inner, TranslationCache cache, string? engineTag = null);
    // engineTag is appended to the cache's target key ("en|claude") so engines do not share entries
}

public static class RegionMerger {            // merges OCR lines of one paragraph into one region
    public static IReadOnlyList<TextRegion> MergeLines(IReadOnlyList<TextRegion> lines);
}

public sealed class PipelineOptions { public bool MergeLines { get; set; } = true; }

public sealed class ChromeBridgeServer {      // additions
    public bool OffersPremiumEngine { get; set; }      // drives app-state.premium
    public string? TrustedOrigin { get; set; }         // null → pin the first origin that connects
    public event Action<string>? OriginPinned;         // raised once when an origin gets pinned
    public Func<IReadOnlyList<string>, string, string, CancellationToken,
                Task<IReadOnlyList<string>>>? PageTranslateHandler { get; set; }
}
```

`ClaudeTranslator` sends one request per batch with all texts as a JSON array and asks for a
JSON array of the same length (structured output), telling the model the lines come from one
screen/page, may contain OCR errors, and must be translated to natural, concise English.

## Overlay window and screen capture

The overlay must be click-through, visible to the user and invisible to the app's own screen
capture (otherwise OCR reads the translations and the overlay flickers in a feedback loop).
`OverlayWindow.CreateInitialized()` picks the mechanism at start-up:

| Windows | Window | Hidden from capture by |
|---|---|---|
| 11 / Server 2025 (build ≥ 22000) | WPF layered window (`AllowsTransparency`) | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` |
| 10 2004+ / Server 2022 | DWM-transparent window (`DwmExtendFrameIntoClientArea`, transparent composition target, `WS_EX_LAYERED` kept by a window-procedure guard) | `SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)` |
| exclusion refused for the layered window | falls back to the DWM-transparent window | as above |
| exclusion refused altogether (e.g. Windows 10 before 2004) | DWM-transparent window | `ICaptureShield`: the overlay is DWM-cloaked for the duration of each `ScreenCapture.Capture` (about two frames, slight flicker) |

On Windows 10 `SetWindowDisplayAffinity` fails with error 8 for windows drawn with
`UpdateLayeredWindow`, which is what WPF does for `AllowsTransparency`. All four rows are
exercised by `tests/AutoTranslator.App.SmokeTests` (workflow `smoke.yml`, Windows Server 2022
and 2025 runners; the last two rows through test seams).
