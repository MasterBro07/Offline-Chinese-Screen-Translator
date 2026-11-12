using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslator.Core;
using AutoTranslator.Desktop;

namespace AutoTranslator.SmokeTests;

internal sealed partial class Suite
{
    // ------------------------------------------------------------------ 3 capture + OCR

    private async Task CheckCaptureOcrAsync()
    {
        const string Name = "3 capture+ocr";
        if (!_ocrUsable)
        {
            Report.Skip(Name, _ocrSkipReason);
            return;
        }

        TextWindow text = _text!;
        TextLayout layout = await text.SetAsync(MainScene());
        if (!await EnsureForegroundAsync(Name))
        {
            return;
        }

        await Task.Delay(500);

        // The rectangle the app decides to read.
        ScreenRect? captureRect = ScreenCapture.GetCaptureRect(ScreenCapture.ScopeForegroundWindow);
        if (captureRect == null)
        {
            Report.Fail(Name + ": capture rectangle", "ScreenCapture.GetCaptureRect returned null for the foreground text window");
            return;
        }

        ScreenRect rect = captureRect.Value;
        Report.Check(
            Shots.Inside(layout.Client, rect) && Shots.Inside(rect, Shots.Inflate(layout.Window, 1)),
            Name + ": capture rectangle",
            "app captures " + Shots.Describe(rect) + "; window " + Shots.Describe(layout.Window)
            + ", client area " + Shots.Describe(layout.Client));

        var source = new OcrTextSource(() => ScreenCapture.ScopeForegroundWindow);
        source.Invalidate();
        Stopwatch watch = Stopwatch.StartNew();
        IReadOnlyList<TextRegion>? regions = await source.CaptureAsync(CancellationToken.None);
        double firstMs = watch.Elapsed.TotalMilliseconds;
        Shot shot = Shots.Grab(rect);
        Shots.Save(shot, "03-capture-ocr-input");

        if (regions == null || source.Warning != null)
        {
            Report.Fail(Name + ": OCR ran", "CaptureAsync returned " + (regions == null ? "null" : regions.Count + " regions")
                + ", warning: " + (source.Warning ?? "(none)"));
            return;
        }

        Report.Info("first capture+OCR took " + Report.F(firstMs) + " ms (includes creating the OCR engine); "
                    + regions.Count + " region(s):");
        foreach (TextRegion region in regions)
        {
            Report.Info("  \"" + region.Text + "\" at " + Shots.Describe(region.Bounds));
        }

        // Text and position of every expected string.
        int expectedChars = 0;
        double matchedChars = 0;
        int exact = 0;
        int missing = 0;
        int maxEdgeError = 0;
        int outside = 0;
        var matches = new List<TextRegion?>();
        foreach (PlacedText item in layout.Items)
        {
            TextRegion? region = BestMatch(regions, r => r.Bounds, item.Rect);
            matches.Add(region);
            int length = item.Text.Count(c => !char.IsWhiteSpace(c));
            expectedChars += length;
            if (region == null)
            {
                missing++;
                Report.Info("  expected \"" + item.Text + "\" at " + Shots.Describe(item.Rect) + ": NO region there");
                continue;
            }

            double accuracy = Accuracy(item.Text, region.Text);
            matchedChars += accuracy * length;
            if (region.Text == item.Text)
            {
                exact++;
            }

            ScreenRect? ink = InkBounds(shot, item.Rect);
            int error = ink != null ? EdgeError(region.Bounds, ink.Value) : -1;
            maxEdgeError = Math.Max(maxEdgeError, error < 0 ? 999 : error);
            if (!Shots.Inside(region.Bounds, layout.Client))
            {
                outside++;
            }

            Report.Info("  expected \"" + item.Text + "\" -> \"" + region.Text + "\" (" + Report.F(accuracy * 100) + " %), region "
                        + Shots.Describe(region.Bounds) + ", glyph ink " + (ink != null ? Shots.Describe(ink.Value) : "?")
                        + ", edge error " + error + " px");
        }

        double overall = expectedChars == 0 ? 0 : matchedChars / expectedChars;
        string textDetails = exact + "/" + layout.Items.Count + " strings exact, "
                             + Report.F(overall * 100) + " % of " + expectedChars + " characters, " + missing + " string(s) not found";
        bool textOk = missing == 0 && overall >= 0.85;
        if (_fallback)
        {
            Report.Skip(Name + ": Chinese text recognised", _ocrSkipReason);
            Report.Check(textOk, Name + ": text recognised" + Tag, textDetails);
        }
        else
        {
            Report.Check(textOk, Name + ": Chinese text recognised", textDetails);
        }

        Report.Check(missing == 0 && outside == 0 && maxEdgeError <= 8,
            Name + ": region bounds" + Tag,
            "largest distance between an OCR region edge and the real glyph edge: " + maxEdgeError
            + " px (limit 8); regions outside the window: " + outside
            + " (capture is scaled 1.5x for OCR, so this covers the coordinate math)");

        // The two texts on the last line must stay separate regions.
        TextRegion? left = matches[matches.Count - 2];
        TextRegion? right = matches[matches.Count - 1];
        Report.Check(left != null && right != null && !ReferenceEquals(left, right),
            Name + ": columns stay separate" + Tag,
            "two texts on one line, " + (layout.Items[layout.Items.Count - 1].Rect.X
                - layout.Items[layout.Items.Count - 2].Rect.X - layout.Items[layout.Items.Count - 2].Rect.Width)
            + " px apart: " + (left == null || right == null ? "one is missing"
                : ReferenceEquals(left, right) ? "merged into one region \"" + left.Text + "\"" : "two regions"));

        // A static screen must be reported as "unchanged" (null), otherwise the overlay would be
        // rebuilt every 700 ms.
        IReadOnlyList<TextRegion>? second = await source.CaptureAsync(CancellationToken.None);
        IReadOnlyList<TextRegion>? third = await source.CaptureAsync(CancellationToken.None);
        Report.Check(second == null && third == null, Name + ": static screen reported as unchanged" + Tag,
            "2nd capture: " + (second == null ? "unchanged" : second.Count + " regions")
            + ", 3rd capture: " + (third == null ? "unchanged" : third.Count + " regions"));
    }

    // ------------------------------------------------------------------ 3b small text

    /// <summary>
    /// Real programs use 12-16 px text, not 44 px. Measures what the recognizer still reads
    /// (the app enlarges the capture 1.5x for exactly this reason).
    /// </summary>
    private async Task CheckSmallTextAsync()
    {
        const string Name = "3b small-text ocr";
        if (!_ocrUsable)
        {
            Report.Skip(Name, _ocrSkipReason);
            return;
        }

        string sentence = _fallback ? "Welcome to the automatic translator" : "欢迎使用自动翻译软件";
        double[] sizes = { 12, 14, 16, 20, 24, 32 };
        var items = new List<TextItem>();
        double y = 20;
        foreach (double size in sizes)
        {
            items.Add(new TextItem(40, y, size, sentence));
            y += size * 1.4 + 30;
        }

        TextLayout layout = await _text!.SetAsync(items.ToArray());
        if (!await EnsureForegroundAsync(Name))
        {
            return;
        }

        await Task.Delay(500);
        var source = new OcrTextSource(() => ScreenCapture.ScopeForegroundWindow);
        source.Invalidate();
        IReadOnlyList<TextRegion>? regions = await source.CaptureAsync(CancellationToken.None);
        ScreenRect? rect = ScreenCapture.GetCaptureRect(ScreenCapture.ScopeForegroundWindow);
        if (rect != null)
        {
            Shots.Save(Shots.Grab(rect.Value), "03b-small-text-input");
        }

        if (regions == null)
        {
            Report.Fail(Name, "CaptureAsync returned null");
            return;
        }

        var parts = new List<string>();
        double at16 = 0;
        for (int i = 0; i < sizes.Length; i++)
        {
            TextRegion? region = BestMatch(regions, r => r.Bounds, layout.Items[i].Rect);
            double accuracy = region == null ? 0 : Accuracy(sentence, region.Text);
            if (sizes[i] == 16)
            {
                at16 = accuracy;
            }

            parts.Add(sizes[i] + " px: " + Report.F(accuracy * 100) + " %");
            Report.Info("  " + sizes[i] + " px -> " + (region == null ? "(nothing)" : "\"" + region.Text + "\""));
        }

        string details = "characters read correctly per font size: " + string.Join(", ", parts);
        if (_fallback)
        {
            Report.Skip(Name + ": Chinese", _ocrSkipReason + ". Measured with Latin text: " + string.Join(", ", parts));
        }
        else
        {
            Report.Check(at16 >= 0.8, Name + ": 16 px Chinese text readable", details);
        }
    }
}
