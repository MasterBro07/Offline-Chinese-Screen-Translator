using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using AutoTranslator.Core;
using AutoTranslator.Desktop;

namespace AutoTranslator.SmokeTests;

internal sealed partial class Suite
{
    // ------------------------------------------------------------------ 4b / 4c what the app does when capture exclusion fails

    /// <summary>
    /// OverlayWindow.CreateInitialized (the call the app makes at start-up) with capture
    /// exclusion failing: first for the preferred window kind only, then altogether.
    /// </summary>
    private async Task CheckOverlayFallbackAsync()
    {
        await CheckWindowKindFallbackAsync();
        await CheckCaptureShieldAsync();
    }

    private Task CheckWindowKindFallbackAsync()
    {
        const string Name = "4b overlay start-up: other window kind when capture exclusion fails";
        if (Environment.OSVersion.Version.Build >= 22000)
        {
            Report.Skip(Name, "Windows build " + Environment.OSVersion.Version.Build + " excludes the WPF-layered window from capture "
                + "(see 4 overlay), so the switch to the DWM-transparent window cannot be triggered here; it runs on the windows-2022 leg");
            return Task.CompletedTask;
        }

        OverlayWindow.TestUseWpfLayeredWindow = true; // what a Windows 11 build with the Windows 10 behaviour would do
        OverlayWindow? overlay = null;
        try
        {
            overlay = OverlayWindow.CreateInitialized();
            IntPtr hwnd = new WindowInteropHelper(overlay).Handle;
            uint affinity;
            bool gotAffinity = Native.GetWindowDisplayAffinity(hwnd, out affinity);
            Report.Check(!overlay.UsesWpfLayeredWindow && overlay.IsExcludedFromCapture && ScreenCapture.Shield == null
                         && gotAffinity && affinity == Native.WDA_EXCLUDEFROMCAPTURE,
                Name,
                "started with the WPF-layered window forced; result: " + ModeName(overlay.UsesWpfLayeredWindow)
                + " window, excluded from capture: " + overlay.IsExcludedFromCapture + ", display affinity 0x" + affinity.ToString("X")
                + ", capture shield in use: " + (ScreenCapture.Shield != null));
        }
        finally
        {
            OverlayWindow.TestUseWpfLayeredWindow = null;
            if (overlay != null)
            {
                overlay.Shutdown();
            }
        }

        return Task.CompletedTask;
    }

    private async Task CheckCaptureShieldAsync()
    {
        const string Name = "4c overlay without capture exclusion";
        TextWindow text = _text!;
        TextLayout layout = await text.SetAsync();
        if (!await EnsureForegroundAsync(Name))
        {
            return;
        }

        await Task.Delay(400);
        ScreenRect client = layout.Client;
        Shot baseline = Shots.Grab(client);
        ulong baselineHash = await Task.Run(() => AppCaptureHash(client));

        var regions = new List<TranslatedRegion>
        {
            new TranslatedRegion("你好世界", "Hello world", new ScreenRect(client.X + 40, client.Y + 40, 280, 44)),
            new TranslatedRegion("加入购物车", "Add to cart", new ScreenRect(client.X + 300, client.Y + 300, 320, 36)),
        };

        // As on a Windows that refuses SetWindowDisplayAffinity for every kind of window
        // (Windows 10 before version 2004).
        OverlayWindow.TestCaptureExclusionUnavailable = true;
        OverlayWindow? overlay = null;
        try
        {
            overlay = OverlayWindow.CreateInitialized();
            IntPtr hwnd = new WindowInteropHelper(overlay).Handle;
            uint affinity;
            Native.GetWindowDisplayAffinity(hwnd, out affinity);
            Report.Check(!overlay.IsExcludedFromCapture && ReferenceEquals(ScreenCapture.Shield, overlay) && affinity == Native.WDA_NONE,
                Name + ": the app switches to hiding the overlay during its captures",
                "excluded from capture: " + overlay.IsExcludedFromCapture + ", display affinity 0x" + affinity.ToString("X")
                + ", capture shield installed: " + ReferenceEquals(ScreenCapture.Shield, overlay)
                + ", window kind: " + ModeName(overlay.UsesWpfLayeredWindow));

            overlay.Show(regions);
            await Task.Delay(700);
            ShotDiff plain = Shots.Diff(baseline, Shots.Grab(client));
            Report.Check(plain.ChangedPixels > 0, Name + ": premise: an ordinary screen capture shows the overlay",
                "CopyFromScreen by the test (no shield): " + plain);

            // The app's capture call, on a thread-pool thread as in OcrTextSource.
            const int Captures = 20;
            int dirty = 0;
            var captureMs = new List<double>();
            for (int i = 0; i < Captures; i++)
            {
                Stopwatch watch = Stopwatch.StartNew();
                ulong hash = await Task.Run(() => AppCaptureHash(client));
                captureMs.Add(watch.Elapsed.TotalMilliseconds);
                if (hash != baselineHash)
                {
                    dirty++;
                }

                await Task.Delay(i % 2 == 0 ? 60 : 5);
            }

            Report.Check(dirty == 0, Name + ": the app's own captures never contain the overlay",
                (Captures - dirty) + "/" + Captures + " captures identical to the screen without overlay; "
                + Report.F(Median(captureMs)) + " ms per capture (max " + Report.F(captureMs.Max())
                + "), during which the overlay is off the screen");

            await Task.Delay(300);
            ShotDiff after = Shots.Diff(baseline, Shots.Grab(client));
            Report.Check(Native.IsWindowVisible(hwnd) && after.ChangedPixels == plain.ChangedPixels,
                Name + ": overlay is back on screen after the captures",
                "window visible: " + Native.IsWindowVisible(hwnd) + "; ordinary screen capture: " + after
                + " (before the captures: " + plain.ChangedPixels + " px)");

            overlay.Clear();
            await Task.Delay(400);

            // ---- the pipeline in this mode: must not read its own translations
            if (!_ocrUsable)
            {
                Report.Skip(Name + ": pipeline stays stable", _ocrSkipReason);
            }
            else
            {
                await text.SetAsync(MainScene());
                if (!await EnsureForegroundAsync(Name))
                {
                    return;
                }

                await Task.Delay(500);
                var recorder = new RecordingPresenter(overlay);
                var translator = new FakeTranslator();
                var ocr = new OcrTextSource(() => ScreenCapture.ScopeForegroundWindow);
                ITextSource source = _fallback ? new HanTaggingSource(ocr) : ocr;
                var pipeline = new TranslationPipeline(source, translator, recorder,
                    new PipelineOptions { Interval = TimeSpan.FromMilliseconds(700) });
                try
                {
                    const int Cycles = 17; // 11.9 s: spans one forced 10 s refresh
                    var counts = new List<int>();
                    var shows = new List<int>();
                    for (int i = 0; i < Cycles; i++)
                    {
                        int before = recorder.Snapshot().Count;
                        Stopwatch watch = Stopwatch.StartNew();
                        counts.Add(await pipeline.RunOnceAsync(CancellationToken.None));
                        shows.Add(recorder.Snapshot().Count - before);
                        await Task.Delay(Math.Max(1, 700 - (int)watch.ElapsedMilliseconds));
                    }

                    bool readOwn = recorder.Snapshot().Any(e => e.Regions != null && e.Regions.Any(r => (r.Original.Contains("EN[") || r.Original.Contains("EN【"))));
                    int laterUpdates = shows.Skip(1).Sum();
                    Report.Check(counts[0] > 0 && counts.Distinct().Count() == 1 && laterUpdates == 0 && !readOwn
                                 && Native.IsWindowVisible(hwnd),
                        Name + ": pipeline stays stable (no feedback loop)" + Tag,
                        "overlay updates after the first cycle: " + laterUpdates + " in " + (Cycles - 1) + " cycles over "
                        + Report.F(Cycles * 0.7) + " s (expected 0); region counts: " + string.Join(",", counts.Distinct())
                        + "; translator calls: " + translator.Calls + "; own translations read back: " + (readOwn ? "YES" : "none")
                        + "; overlay window visible: " + Native.IsWindowVisible(hwnd));
                }
                finally
                {
                    await pipeline.DisposeAsync();
                }
            }
        }
        finally
        {
            OverlayWindow.TestCaptureExclusionUnavailable = false;
            if (overlay != null)
            {
                overlay.Shutdown();
            }
        }

        Report.Check(ScreenCapture.Shield == null, Name + ": shield removed when the overlay closes",
            "capture shield after Shutdown: " + (ScreenCapture.Shield == null ? "none" : "still installed"));
    }

    /// <summary>ScreenCapture.Capture + hash, exactly what OcrTextSource uses to detect changes.</summary>
    private static ulong AppCaptureHash(ScreenRect rect)
    {
        byte[]? buffer = null;
        using (System.Drawing.Bitmap bitmap = ScreenCapture.Capture(rect))
        {
            int length = ScreenCapture.CopyPixels(bitmap, ref buffer);
            return ScreenCapture.Hash(buffer!, length);
        }
    }
}
