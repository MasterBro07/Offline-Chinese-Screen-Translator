using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using AutoTranslator.Core;
using AutoTranslator.Desktop;

namespace AutoTranslator.SmokeTests;

internal sealed partial class Suite
{
    // ------------------------------------------------------------------ 5 end-to-end pipeline

    private async Task CheckPipelineAsync()
    {
        const string Name = "5 pipeline";
        if (!_ocrUsable)
        {
            Report.Skip(Name, _ocrSkipReason);
            return;
        }

        if (_fallback)
        {
            Report.Skip(Name + ": with Chinese text", _ocrSkipReason
                + "; the pipeline checks below run with Latin text and a marker character");
        }

        TextWindow text = _text!;
        TextLayout layout = await text.SetAsync(MainScene());
        if (!await EnsureForegroundAsync(Name))
        {
            return;
        }

        await Task.Delay(500);
        ScreenRect client = layout.Client;
        Shot baseline = Shots.Grab(client);

        var overlay = new OverlayWindow();
        overlay.Initialize();
        IntPtr overlayHwnd = new WindowInteropHelper(overlay).Handle;
        var recorder = new RecordingPresenter(overlay);
        var translator = new FakeTranslator();
        var ocr = new OcrTextSource(() => ScreenCapture.ScopeForegroundWindow);
        ITextSource source = _fallback ? new HanTaggingSource(ocr) : ocr;
        var options = new PipelineOptions { Interval = TimeSpan.FromMilliseconds(700) };
        var pipeline = new TranslationPipeline(source, translator, recorder, options);

        try
        {
            // ---- phase 1: single cycles every 700 ms on a static screen (spans one forced 10 s refresh)
            const int Cycles = 18;
            var cycleMs = new List<double>();
            var counts = new List<int>();
            var showsPerCycle = new List<int>();
            for (int i = 0; i < Cycles; i++)
            {
                int before = recorder.Snapshot().Count;
                Stopwatch watch = Stopwatch.StartNew();
                int shown = await pipeline.RunOnceAsync(CancellationToken.None);
                cycleMs.Add(watch.Elapsed.TotalMilliseconds);
                counts.Add(shown);
                showsPerCycle.Add(recorder.Snapshot().Count - before);
                await Task.Delay(Math.Max(1, 700 - (int)watch.ElapsedMilliseconds));
            }

            Report.Info("cycle times in ms (cycle 1 does the OCR, later ones only compare the screen): "
                        + string.Join(" ", cycleMs.Select(Report.F)));
            Report.Info("regions on the overlay per cycle: " + string.Join(" ", counts)
                        + "; overlay updates per cycle: " + string.Join(" ", showsPerCycle)
                        + "; pipeline status: " + pipeline.StatusText);

            List<PresenterEvent> events = recorder.Snapshot();
            IReadOnlyList<TranslatedRegion>? first = events.Count > 0 ? events[0].Regions : null;
            int placed = 0;
            int wellFormed = 0;
            int maxOffset = 0;
            if (first != null)
            {
                foreach (PlacedText item in layout.Items)
                {
                    TranslatedRegion? region = BestMatch(first, r => r.Bounds, item.Rect);
                    if (region == null)
                    {
                        Report.Info("  no translation shown over \"" + item.Text + "\"");
                        continue;
                    }

                    bool textOk = Accuracy(item.Text, region.Original) >= 0.8;
                    bool inside = Shots.Inside(region.Bounds, Shots.Inflate(item.Rect, 6));
                    if (textOk && inside)
                    {
                        placed++;
                    }

                    if (region.Translated == "EN[" + region.Original + "]")
                    {
                        wellFormed++;
                    }

                    ScreenRect? ink = InkBounds(baseline, item.Rect);
                    if (ink != null)
                    {
                        maxOffset = Math.Max(maxOffset, EdgeError(region.Bounds, ink.Value));
                    }

                    Report.Info("  \"" + item.Text + "\" -> \"" + region.Translated + "\" at " + Shots.Describe(region.Bounds));
                }
            }

            Report.Check(first != null && placed == layout.Items.Count && wellFormed == layout.Items.Count,
                Name + ": translations shown over the original text" + Tag,
                placed + "/" + layout.Items.Count + " texts have their translation on top (largest edge offset to the glyphs "
                + maxOffset + " px); " + (first == null ? "the overlay was never shown" : first.Count + " region(s) shown"));

            int laterUpdates = showsPerCycle.Skip(1).Sum();
            bool stable = first != null && laterUpdates == 0 && counts.Distinct().Count() == 1 && counts[0] > 0;
            Report.Check(stable, Name + ": stable on a static screen (no feedback loop, no flicker)" + Tag,
                "overlay updates after the first cycle: " + laterUpdates + " in " + (Cycles - 1) + " cycles over "
                + Report.F(Cycles * 0.7) + " s (expected 0); translator calls: " + translator.Calls
                + "; region counts: " + string.Join(",", counts.Distinct()));

            await Task.Delay(300);
            Shot withOverlay = Shots.Grab(client);
            ShotDiff selfCapture = Shots.Diff(baseline, withOverlay);
            Report.Check(Native.IsWindowVisible(overlayHwnd) && selfCapture.ChangedPixels == 0,
                Name + ": the app does not capture its own overlay" + Tag,
                "overlay window visible: " + Native.IsWindowVisible(overlayHwnd)
                + "; screen capture with overlay vs. without: " + selfCapture);

            // What the user sees (capture exclusion switched off for one screenshot only).
            Native.SetWindowDisplayAffinity(overlayHwnd, Native.WDA_NONE);
            await Task.Delay(400);
            Shots.Save(Shots.Grab(layout.Window), "05-pipeline-what-the-user-sees");
            Native.SetWindowDisplayAffinity(overlayHwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            await Task.Delay(400);

            // ---- timings
            await MeasureTimingsAsync(Name, cycleMs);

            // ---- phase 2: the real background loop reacts to changes
            int eventsBefore = recorder.Snapshot().Count;
            pipeline.Start();
            await Task.Delay(1600);
            int idleUpdates = recorder.Snapshot().Count - eventsBefore;

            TextLayout changed = await text.SetAsync(ChangedScene());
            DateTime changedUtc = changed.ReportedUtc;
            PresenterEvent? update = null;
            Stopwatch wait = Stopwatch.StartNew();
            while (wait.Elapsed < TimeSpan.FromSeconds(6) && update == null)
            {
                update = recorder.Snapshot().FirstOrDefault(e =>
                    e.Utc >= changedUtc.AddMilliseconds(-200) && e.Regions != null
                    && changed.Items.All(item => e.Regions.Any(r =>
                        Accuracy(item.Text, r.Original) >= 0.8 && IntersectionArea(r.Bounds, item.Rect) > 0))
                    && (_fallback || e.Regions.Count == changed.Items.Count)); // fallback also reads the title bar
                if (update == null)
                {
                    await Task.Delay(40);
                }
            }

            double latency = update != null ? (update.Utc - changedUtc).TotalMilliseconds : -1;
            Report.Check(update != null && latency <= 2000, Name + ": changed text updates the overlay within 2 s" + Tag,
                update != null
                    ? "new translations shown " + Report.F(latency) + " ms after the window changed its text (loop interval 700 ms)"
                    : "no overlay update with the new texts within 6 s; events: " + DescribeEvents(recorder.Snapshot().Skip(eventsBefore)));
            Report.Info("overlay updates while the loop idled on a static screen for 1.6 s: " + idleUpdates);

            await Task.Delay(500);
            Native.SetWindowDisplayAffinity(overlayHwnd, Native.WDA_NONE);
            await Task.Delay(400);
            Shots.Save(Shots.Grab(layout.Window), "05-pipeline-after-text-change");
            Native.SetWindowDisplayAffinity(overlayHwnd, Native.WDA_EXCLUDEFROMCAPTURE);

            // ---- text disappears: overlay must go away
            TextLayout empty = await text.SetAsync();
            DateTime emptyUtc = empty.ReportedUtc;
            PresenterEvent? clear = null;
            wait.Restart();
            while (wait.Elapsed < TimeSpan.FromSeconds(6) && clear == null)
            {
                clear = recorder.Snapshot().FirstOrDefault(e => e.Utc >= emptyUtc.AddMilliseconds(-200) && e.Regions == null);
                if (clear == null)
                {
                    await Task.Delay(40);
                }
            }

            await Task.Delay(300);
            double clearLatency = clear != null ? (clear.Utc - emptyUtc).TotalMilliseconds : -1;
            Report.Check(clear != null && clearLatency <= 2000 && !Native.IsWindowVisible(overlayHwnd),
                Name + ": overlay disappears when the text is gone" + Tag,
                clear != null
                    ? "cleared " + Report.F(clearLatency) + " ms after the text was removed; overlay window visible: "
                      + Native.IsWindowVisible(overlayHwnd)
                    : "overlay was not cleared within 6 s");

            await pipeline.StopAsync();
        }
        finally
        {
            await pipeline.DisposeAsync();
            overlay.Shutdown();
        }
    }

    private static string DescribeEvents(IEnumerable<PresenterEvent> events)
    {
        return string.Join(" | ", events.Select(e => e.Regions == null
            ? "Clear"
            : "Show(" + string.Join(", ", e.Regions.Select(r => r.Original)) + ")"));
    }

    private async Task MeasureTimingsAsync(string name, List<double> cycleMs)
    {
        const int Runs = 12;
        string scope = ScreenCapture.ScopeForegroundWindow;

        // Screen grab + copy + hash: what every cycle costs, also when nothing changed.
        var captureMs = new List<double>();
        byte[]? buffer = null;
        ScreenRect size = default;
        for (int i = 0; i < Runs; i++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            await Task.Run(() =>
            {
                ScreenRect? rect = ScreenCapture.GetCaptureRect(scope);
                if (rect == null)
                {
                    return;
                }

                size = rect.Value;
                using (System.Drawing.Bitmap bitmap = ScreenCapture.Capture(rect.Value))
                {
                    int length = ScreenCapture.CopyPixels(bitmap, ref buffer);
                    ScreenCapture.Hash(buffer!, length);
                }
            });
            captureMs.Add(watch.Elapsed.TotalMilliseconds);
        }

        // Full forced capture: grab + 1.5x scaling + OCR + region building.
        var fullMs = new List<double>();
        var source = new OcrTextSource(() => scope);
        for (int i = 0; i < Runs + 1; i++)
        {
            source.Invalidate();
            Stopwatch watch = Stopwatch.StartNew();
            await source.CaptureAsync(CancellationToken.None);
            if (i > 0)
            {
                fullMs.Add(watch.Elapsed.TotalMilliseconds); // the first run creates the engine
            }
        }

        // Same for the whole primary monitor (the "Ganzer Hauptbildschirm" setting).
        var monitorMs = new List<double>();
        var monitorSource = new OcrTextSource(() => ScreenCapture.ScopePrimaryMonitor);
        for (int i = 0; i < 6; i++)
        {
            monitorSource.Invalidate();
            Stopwatch watch = Stopwatch.StartNew();
            await monitorSource.CaptureAsync(CancellationToken.None);
            if (i > 0)
            {
                monitorMs.Add(watch.Elapsed.TotalMilliseconds);
            }
        }

        double capture = Median(captureMs);
        double full = Median(fullMs);
        double unchanged = Median(cycleMs.Skip(1));
        int screenWidth = Native.GetSystemMetrics(Native.SM_CXSCREEN);
        int screenHeight = Native.GetSystemMetrics(Native.SM_CYSCREEN);
        Report.Info("timing, window " + size.Width + "x" + size.Height + " px (median of " + Runs + "):");
        Report.Info("  capture (grab+copy+hash): " + Report.F(capture) + " ms (max " + Report.F(captureMs.Max()) + ")");
        Report.Info("  OCR incl. 1.5x scaling:   " + Report.F(Math.Max(0, full - capture)) + " ms");
        Report.Info("  full changed-screen read: " + Report.F(full) + " ms (max " + Report.F(fullMs.Max()) + ")");
        Report.Info("  first pipeline cycle:     " + Report.F(cycleMs[0]) + " ms (OCR + translation + overlay, includes engine start)");
        Report.Info("  unchanged-screen cycle:   " + Report.F(unchanged) + " ms");
        Report.Info("  whole monitor " + screenWidth + "x" + screenHeight + ": " + Report.F(Median(monitorMs)) + " ms per changed-screen read");

        Report.Check(full <= 1000 && capture <= 300, name + ": cycle time" + Tag,
            "capture " + Report.F(capture) + " ms + OCR " + Report.F(Math.Max(0, full - capture)) + " ms = "
            + Report.F(full) + " ms per changed screen (" + size.Width + "x" + size.Height + " px window; limit 1000 ms); "
            + "unchanged screen " + Report.F(unchanged) + " ms; whole " + screenWidth + "x" + screenHeight + " monitor "
            + Report.F(Median(monitorMs)) + " ms. Worst-case delay = 700 ms loop interval + this + translation.");
    }

    // ------------------------------------------------------------------ 7 UI Automation

    private async Task CheckUiAutomationAsync()
    {
        const string Name = "7 ui-automation";
        TextWindow text = _text!;

        // UI Automation needs no OCR pack, so this always uses the Chinese texts.
        TextLayout layout = await text.SetAsync(BuildScene(ChineseRows, ChineseColumns));
        if (!await EnsureForegroundAsync(Name))
        {
            return;
        }

        await Task.Delay(400);
        var source = new UiAutomationTextSource();
        source.Invalidate();
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyList<TextRegion>? regions = await source.CaptureAsync(CancellationToken.None);
        double firstMs = watch.Elapsed.TotalMilliseconds;
        watch.Restart();
        IReadOnlyList<TextRegion>? second = await source.CaptureAsync(CancellationToken.None);
        double secondMs = watch.Elapsed.TotalMilliseconds;

        if (regions == null)
        {
            Report.Fail(Name, "CaptureAsync returned null on the first call");
            return;
        }

        foreach (TextRegion region in regions)
        {
            Report.Info("  \"" + region.Text + "\" at " + Shots.Describe(region.Bounds));
        }

        int found = 0;
        int maxError = 0;
        foreach (PlacedText item in layout.Items)
        {
            TextRegion? region = regions.FirstOrDefault(r => r.Text == item.Text);
            if (region == null)
            {
                Report.Info("  missing: \"" + item.Text + "\"");
                continue;
            }

            found++;
            maxError = Math.Max(maxError, EdgeError(region.Bounds, item.Rect));
        }

        Report.Check(found == layout.Items.Count && regions.Count == layout.Items.Count && maxError <= 2, Name + ": texts and bounds",
            found + "/" + layout.Items.Count + " Chinese strings read exactly, " + regions.Count
            + " region(s) in total, largest bounds error " + maxError + " px (limit 2); first read "
            + Report.F(firstMs) + " ms, second " + Report.F(secondMs) + " ms");
        Report.Check(second == null, Name + ": static window reported as unchanged",
            "2nd capture: " + (second == null ? "unchanged" : second.Count + " regions"));
    }

    // ------------------------------------------------------------------ 8 resources

    private async Task CheckResourcesAsync()
    {
        const string Name = "8 resources";
        if (!_ocrUsable)
        {
            Report.Skip(Name, _ocrSkipReason);
            return;
        }

        TextWindow text = _text!;
        await text.SetAsync(MainScene());
        if (!await EnsureForegroundAsync(Name))
        {
            return;
        }

        var overlay = new OverlayWindow();
        overlay.Initialize();
        var source = new OcrTextSource(() => ScreenCapture.ScopeForegroundWindow);
        try
        {
            Func<int, Task> cycle = async i =>
            {
                source.Invalidate();
                IReadOnlyList<TextRegion>? regions = await source.CaptureAsync(CancellationToken.None);
                if (regions != null)
                {
                    // A new overlay every cycle is the worst case (scrolling text).
                    overlay.Show(regions.Select(r => new TranslatedRegion(r.Text, "EN " + i + " " + r.Text, r.Bounds)).ToList());
                }

                await Task.Delay(15);
            };

            for (int i = 0; i < 15; i++)
            {
                await cycle(i);
            }

            ResourceSample start = ResourceSample.Take();
            const int Cycles = 100;
            Stopwatch watch = Stopwatch.StartNew();
            for (int i = 0; i < Cycles; i++)
            {
                await cycle(i);
            }

            double perCycle = watch.Elapsed.TotalMilliseconds / Cycles;
            ResourceSample end = ResourceSample.Take();

            Report.Info("start: " + start);
            Report.Info("end:   " + end);
            long privateGrowth = end.PrivateBytes - start.PrivateBytes;
            long gdiGrowth = (long)end.Gdi - start.Gdi;
            long userGrowth = (long)end.User - start.User;
            long handleGrowth = (long)end.Handles - start.Handles;
            bool ok = gdiGrowth <= 20 && userGrowth <= 20 && handleGrowth <= 150 && privateGrowth <= 80L * 1024 * 1024;
            Report.Check(ok, Name + ": no leak over " + Cycles + " capture+OCR+overlay cycles" + Tag,
                "GDI handles " + start.Gdi + " -> " + end.Gdi + ", USER handles " + start.User + " -> " + end.User
                + ", kernel handles " + start.Handles + " -> " + end.Handles
                + ", private memory " + Mb(start.PrivateBytes) + " -> " + Mb(end.PrivateBytes) + " MB"
                + ", working set " + Mb(start.WorkingSet) + " -> " + Mb(end.WorkingSet) + " MB"
                + ", managed heap " + Mb(start.Managed) + " -> " + Mb(end.Managed) + " MB; "
                + Report.F(perCycle) + " ms per cycle (limits: +20 GDI, +20 USER, +150 handles, +80 MB)");
        }
        finally
        {
            overlay.Shutdown();
        }
    }

    private static string Mb(long bytes)
    {
        return Report.F(bytes / (1024.0 * 1024.0));
    }

    private sealed class ResourceSample
    {
        public long WorkingSet;
        public long PrivateBytes;
        public long Managed;
        public uint Gdi;
        public uint User;
        public int Handles;

        public static ResourceSample Take()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            using (Process process = Process.GetCurrentProcess())
            {
                return new ResourceSample
                {
                    WorkingSet = process.WorkingSet64,
                    PrivateBytes = process.PrivateMemorySize64,
                    Managed = GC.GetTotalMemory(false),
                    Gdi = Native.GetGuiResources(process.Handle, Native.GR_GDIOBJECTS),
                    User = Native.GetGuiResources(process.Handle, Native.GR_USEROBJECTS),
                    Handles = process.HandleCount,
                };
            }
        }

        public override string ToString()
        {
            return "working set " + Mb(WorkingSet) + " MB, private " + Mb(PrivateBytes) + " MB, managed " + Mb(Managed)
                   + " MB, GDI " + Gdi + ", USER " + User + ", handles " + Handles;
        }
    }

    // ------------------------------------------------------------------ app log of the in-process checks

    private Task CheckInProcessLogAsync()
    {
        // OcrTextSource, OverlayWindow and UiAutomationTextSource log their problems here.
        string path = Log.FilePath;
        string content = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        foreach (string line in content.Split('\n'))
        {
            if (line.Trim().Length > 0)
            {
                Report.Info("  log: " + line.TrimEnd());
            }
        }

        int errors = content.Split('\n').Count(l => l.Contains("[ERROR]"));
        Report.Check(errors == 0, "in-process app log: no errors",
            errors + " [ERROR] line(s) written by the app classes during the in-process checks");
        return Task.CompletedTask;
    }
}
