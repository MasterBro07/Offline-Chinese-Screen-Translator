using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Interop;
using AutoTranslator.Core;
using AutoTranslator.Desktop;

namespace AutoTranslator.SmokeTests;

internal sealed partial class Suite
{
    // ------------------------------------------------------------------ 4 overlay

    private async Task CheckOverlayAsync()
    {
        bool defaultWpfLayered = Environment.OSVersion.Version.Build >= 22000;
        Report.Info("Windows build " + Environment.OSVersion.Version.Build + ": the app uses the "
                    + ModeName(defaultWpfLayered) + " overlay window here");
        await CheckOverlayAsync("4 overlay");

        // The technique the app does NOT use on this Windows version, for the record.
        Report.Experiment = true;
        OverlayWindow.TestUseWpfLayeredWindow = !defaultWpfLayered;
        string logBefore = System.IO.File.Exists(Log.FilePath) ? System.IO.File.ReadAllText(Log.FilePath) : string.Empty;
        try
        {
            await CheckOverlayAsync("4x overlay (" + ModeName(!defaultWpfLayered) + ", not used on this Windows)");
        }
        finally
        {
            OverlayWindow.TestUseWpfLayeredWindow = null;
            Report.Experiment = false;

            // Errors the app logs during the experiment are not errors of the real configuration.
            try
            {
                System.IO.File.WriteAllText(Log.FilePath, logBefore);
            }
            catch (Exception ex)
            {
                Report.Info("restoring the app log after the experiment failed: " + ex.Message);
            }
        }
    }

    private static string ModeName(bool wpfLayered)
    {
        return wpfLayered ? "WPF-layered (AllowsTransparency)" : "DWM-transparent";
    }

    private async Task CheckOverlayAsync(string name)
    {
        string Name = name;
        TextWindow text = _text!;

        // Blank white window underneath, so every changed pixel belongs to the overlay.
        TextLayout layout = await text.SetAsync();
        if (!await EnsureForegroundAsync(Name))
        {
            return;
        }

        await Task.Delay(400);
        ScreenRect client = layout.Client;
        ScreenRect screen = new ScreenRect(0, 0, Native.GetSystemMetrics(Native.SM_CXSCREEN), Native.GetSystemMetrics(Native.SM_CYSCREEN));

        var regionA = new ScreenRect(client.X + 40, client.Y + 40, 280, 44);
        var regionB = new ScreenRect(client.X + 300, client.Y + 300, 320, 36);
        var regions = new List<TranslatedRegion>
        {
            new TranslatedRegion("你好世界", "Hello world", regionA),
            new TranslatedRegion("加入购物车", "Add to cart", regionB),
        };
        ScreenRect[] expected = { Shots.Inflate(regionA, 2), Shots.Inflate(regionB, 2) };

        Shot baseline = Shots.Grab(screen);
        Shots.Save(baseline, "04-overlay-0-baseline");

        var overlay = new OverlayWindow();
        try
        {
            overlay.Initialize();
            IntPtr hwnd = new WindowInteropHelper(overlay).Handle;

            // --- production setting: excluded from capture
            uint affinity;
            bool gotAffinity = Native.GetWindowDisplayAffinity(hwnd, out affinity);
            Report.Check(gotAffinity && affinity == Native.WDA_EXCLUDEFROMCAPTURE,
                Name + ": SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE) applied by the app",
                gotAffinity ? "window display affinity is 0x" + affinity.ToString("X") + " (expected 0x11)"
                            : "GetWindowDisplayAffinity failed");

            long exStyle = Native.GetExStyle(hwnd);
            long wanted = Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            Report.Check((exStyle & wanted) == wanted, Name + ": click-through window styles",
                "extended style 0x" + exStyle.ToString("X") + ": WS_EX_TRANSPARENT=" + ((exStyle & Native.WS_EX_TRANSPARENT) != 0)
                + " WS_EX_LAYERED=" + ((exStyle & Native.WS_EX_LAYERED) != 0)
                + " WS_EX_NOACTIVATE=" + ((exStyle & Native.WS_EX_NOACTIVATE) != 0)
                + " WS_EX_TOOLWINDOW=" + ((exStyle & Native.WS_EX_TOOLWINDOW) != 0));

            overlay.Show(regions);
            await Task.Delay(700);

            Native.RECT windowRect;
            Native.GetWindowRect(hwnd, out windowRect);
            ScreenRect virtualScreen = ScreenCapture.GetVirtualScreen();
            bool covers = windowRect.Left == virtualScreen.X && windowRect.Top == virtualScreen.Y
                          && windowRect.Right - windowRect.Left == virtualScreen.Width
                          && windowRect.Bottom - windowRect.Top == virtualScreen.Height;
            Report.Check(Native.IsWindowVisible(hwnd) && covers, Name + ": window shown over the whole screen",
                "visible=" + Native.IsWindowVisible(hwnd) + ", window rect (" + windowRect.Left + "," + windowRect.Top + " "
                + (windowRect.Right - windowRect.Left) + "x" + (windowRect.Bottom - windowRect.Top) + "), virtual screen "
                + Shots.Describe(virtualScreen));

            long shownStyle = Native.GetExStyle(hwnd);
            Report.Check((shownStyle & wanted) == wanted, Name + ": click-through window styles still set while shown",
                "extended style 0x" + shownStyle.ToString("X") + ": WS_EX_TRANSPARENT=" + ((shownStyle & Native.WS_EX_TRANSPARENT) != 0)
                + " WS_EX_LAYERED=" + ((shownStyle & Native.WS_EX_LAYERED) != 0));

            Report.Check(Native.GetForegroundWindow() == layout.Hwnd, Name + ": does not take the foreground",
                "foreground after showing the overlay: " + Native.Describe(Native.GetForegroundWindow()));

            Shot excluded = Shots.Grab(screen);
            Shots.Save(excluded, "04-overlay-1-shown-excluded-from-capture");
            ShotDiff excludedDiff = Shots.Diff(baseline, excluded, 24, null, client);
            Report.Check(excludedDiff.ChangedPixels == 0,
                Name + ": invisible to screen capture (production setting)",
                "CopyFromScreen with the overlay shown vs. before: " + excludedDiff + " in the window area");

            // --- test-only: switch the exclusion off to see what the user sees
            bool cleared = Native.SetWindowDisplayAffinity(hwnd, Native.WDA_NONE);
            await Task.Delay(500);
            Shot visible = Shots.Grab(screen);

            // A busy runner can present the first frame late; the delay is reported, a missing frame still fails.
            System.Diagnostics.Stopwatch drawWait = System.Diagnostics.Stopwatch.StartNew();
            while (drawWait.Elapsed < TimeSpan.FromSeconds(8)
                   && regions.Any(r => Shots.Diff(baseline, visible, 24, null, r.Bounds).ChangedPixels == 0))
            {
                await Task.Delay(250);
                visible = Shots.Grab(screen);
            }

            double drawnAfterMs = 500 + drawWait.Elapsed.TotalMilliseconds;
            Shots.Save(visible, "04-overlay-2-shown-capture-exclusion-off");

            ShotDiff elsewhere = Shots.Diff(baseline, visible, 24, expected, client);
            var details = new List<string>();
            bool boxesOk = cleared;
            int maxError = 0;
            foreach (TranslatedRegion region in regions)
            {
                ShotDiff diff = Shots.Diff(baseline, visible, 24, null, Shots.Inflate(region.Bounds, 30));
                double dark, light;
                Shots.Tones(visible, region.Bounds, out dark, out light);
                int error = diff.ChangedPixels == 0 ? 999 : EdgeError(diff.Bounds, region.Bounds);
                maxError = Math.Max(maxError, error);
                bool ok = diff.ChangedPixels > 0 && error <= 2 && dark >= 0.6 && light >= 0.01 && light <= 0.45;
                boxesOk &= ok;
                details.Add("\"" + region.Translated + "\": expected " + Shots.Describe(region.Bounds) + ", drawn "
                            + (diff.ChangedPixels == 0 ? "NOTHING" : Shots.Describe(diff.Bounds))
                            + ", " + Report.F(dark * 100) + " % dark / " + Report.F(light * 100) + " % light pixels");
            }

            Report.Check(boxesOk, Name + ": dark boxes with light text at the expected positions",
                "position error " + maxError + " px (limit 2); " + string.Join("; ", details)
                + (drawnAfterMs > 600 ? "; on screen only " + Report.F(drawnAfterMs) + " ms after capture exclusion was switched off" : string.Empty));
            Report.Check(elsewhere.ChangedPixels == 0, Name + ": nothing drawn anywhere else",
                "outside the two boxes: " + elsewhere);

            // --- click-through
            text.ClearClicks();
            int clickX = regionA.X + regionA.Width / 2;
            int clickY = regionA.Y + regionA.Height / 2;
            if (!Native.Click(clickX, clickY))
            {
                Report.Skip(Name + ": mouse click passes through", "SendInput/SetCursorPos is blocked in this session");
            }
            else
            {
                ScreenRect? click = await text.WaitForClickAsync(TimeSpan.FromSeconds(3));
                Report.Check(click != null, Name + ": mouse click passes through",
                    "clicked at (" + clickX + "," + clickY + ") in the middle of an overlay box; the window underneath "
                    + (click != null ? "received the click at (" + click.Value.X + "," + click.Value.Y + ")" : "did NOT receive it"));
            }

            // --- exclusion on again, on the visible window
            bool set = Native.SetWindowDisplayAffinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            int error2 = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            await Task.Delay(500);
            Shot hidden = Shots.Grab(screen);
            Shots.Save(hidden, "04-overlay-3-exclusion-on-again");
            ShotDiff hiddenDiff = Shots.Diff(baseline, hidden, 24, null, client);
            Report.Check(set && hiddenDiff.ChangedPixels == 0 && Native.IsWindowVisible(hwnd),
                Name + ": SetWindowDisplayAffinity hides the visible overlay from capture",
                "returned " + set + (set ? string.Empty : " (error " + error2 + ")") + "; overlay still shown: "
                + Native.IsWindowVisible(hwnd) + "; capture vs. baseline: " + hiddenDiff);

            // --- Clear() hides the window
            Native.SetWindowDisplayAffinity(hwnd, Native.WDA_NONE);
            overlay.Clear();
            await Task.Delay(500);
            Shot afterClear = Shots.Grab(screen);
            ShotDiff clearDiff = Shots.Diff(baseline, afterClear, 24, null, client);
            Report.Check(!Native.IsWindowVisible(hwnd) && clearDiff.ChangedPixels == 0, Name + ": Clear() removes everything",
                "window visible: " + Native.IsWindowVisible(hwnd) + "; capture vs. baseline: " + clearDiff);
        }
        finally
        {
            overlay.Shutdown();
        }
    }
}
