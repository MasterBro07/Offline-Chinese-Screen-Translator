using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslator.Core;
using AutoTranslator.Desktop;

namespace AutoTranslator.SmokeTests;

/// <summary>
/// The smoke checks, in order. Everything runs on one STA dispatcher thread (see Program);
/// the checks are split over the Suite.*.cs files.
/// </summary>
internal sealed partial class Suite : IDisposable
{
    private static readonly string[] ChineseRows = { "你好世界", "加入购物车", "立即购买", "商品详情" };
    private static readonly string[] ChineseColumns = { "用户评价", "联系客服" };
    private static readonly string[] ChineseChanged = { "新品上市", "免费配送" };

    private static readonly string[] LatinRows = { "HELLO WORLD", "ADD TO CART", "BUY NOW", "PRODUCT DETAILS" };
    private static readonly string[] LatinColumns = { "REVIEWS", "SUPPORT" };
    private static readonly string[] LatinChanged = { "NEW ARRIVALS", "FREE SHIPPING" };

    private readonly string _outDir;
    private readonly string? _exePath;

    private TextWindow? _text;
    private bool _interactive;
    private bool _chineseOcr;
    private bool _ocrUsable;
    private bool _fallback;
    private string _ocrSkipReason = string.Empty;

    public Suite(string outDir, string? exePath)
    {
        _outDir = outDir;
        _exePath = exePath;
    }

    private string Tag
    {
        get { return _fallback ? " [en-US fallback]" : string.Empty; }
    }

    public async Task RunAsync()
    {
        // The in-process checks use the app's own Log class; start with an empty log file.
        TryDelete(Log.FilePath);

        await RunCheckAsync("1 environment", CheckEnvironmentAsync);
        await RunCheckAsync("2 chinese-ocr-pack", CheckOcrPackAsync);

        if (!_interactive)
        {
            Report.Skip("3-8 all desktop checks", "the session has no interactive desktop, no window can be shown");
            return;
        }

        _text = await TextWindow.StartAsync(60, 40, 780, 560);
        Report.Info("text window: " + Native.Describe(_text.Layout.Hwnd)
                    + " window=" + Shots.Describe(_text.Layout.Window)
                    + " client=" + Shots.Describe(_text.Layout.Client)
                    + " foreground=" + _text.Layout.Foreground);

        await RunCheckAsync("3 capture+ocr", CheckCaptureOcrAsync);
        await RunCheckAsync("3b small-text ocr", CheckSmallTextAsync);
        await RunCheckAsync("4 overlay", CheckOverlayAsync);
        await RunCheckAsync("5 pipeline", CheckPipelineAsync);
        await RunCheckAsync("7 ui-automation", CheckUiAutomationAsync);
        await RunCheckAsync("8 resources", CheckResourcesAsync);
        await RunCheckAsync("in-process app log", CheckInProcessLogAsync);
        await RunCheckAsync("6 real executable", CheckExecutableAsync);
    }

    public void Dispose()
    {
        if (_text != null)
        {
            _text.Dispose();
            _text = null;
        }
    }

    private static async Task RunCheckAsync(string name, Func<Task> body)
    {
        Report.Section(name);
        try
        {
            await body();
        }
        catch (Exception ex)
        {
            Report.Fail(name, "unexpected exception: " + ex);
        }
    }

    // ------------------------------------------------------------------ scenes

    private TextItem[] MainScene()
    {
        return BuildScene(_fallback ? LatinRows : ChineseRows, _fallback ? LatinColumns : ChineseColumns);
    }

    private TextItem[] ChangedScene()
    {
        string[] texts = _fallback ? LatinChanged : ChineseChanged;
        return new[]
        {
            new TextItem(40, 60, 44, texts[0]),
            new TextItem(40, 220, 44, texts[1]),
        };
    }

    private static TextItem[] BuildScene(string[] rows, string[] columns)
    {
        var items = new List<TextItem>();
        for (int i = 0; i < rows.Length; i++)
        {
            items.Add(new TextItem(40, 24 + i * 86, 44, rows[i]));
        }

        // Two texts on one line, far apart: must become two regions, not one sentence.
        items.Add(new TextItem(40, 384, 32, columns[0]));
        items.Add(new TextItem(470, 384, 32, columns[1]));
        return items.ToArray();
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Makes the text window the foreground window; reports a FAIL and returns false otherwise.</summary>
    private async Task<bool> EnsureForegroundAsync(string checkName)
    {
        TextWindow text = _text!;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (Native.GetForegroundWindow() == text.Layout.Hwnd)
            {
                return true;
            }

            await text.ActivateAsync();
            await Task.Delay(200);
        }

        if (Native.GetForegroundWindow() == text.Layout.Hwnd)
        {
            return true;
        }

        Report.Fail(checkName + ": foreground",
            "the text window could not be made the foreground window; foreground is "
            + Native.Describe(Native.GetForegroundWindow()));
        return false;
    }

    /// <summary>Share of the expected characters found in order in the actual text (0..1), ignoring spaces.</summary>
    private static double Accuracy(string expected, string? actual)
    {
        string a = new string(expected.Where(c => !char.IsWhiteSpace(c)).ToArray());
        string b = new string((actual ?? string.Empty).Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (a.Length == 0)
        {
            return 1;
        }

        var table = new int[a.Length + 1, b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                table[i, j] = a[i - 1] == b[j - 1]
                    ? table[i - 1, j - 1] + 1
                    : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        return (double)table[a.Length, b.Length] / a.Length;
    }

    private static long IntersectionArea(ScreenRect a, ScreenRect b)
    {
        ScreenRect? intersection = ScreenCapture.Intersect(a, b);
        return intersection == null ? 0 : (long)intersection.Value.Width * intersection.Value.Height;
    }

    /// <summary>The region overlapping the placed text most, or null.</summary>
    private static T? BestMatch<T>(IEnumerable<T> candidates, Func<T, ScreenRect> bounds, ScreenRect target)
        where T : class
    {
        T? best = null;
        long bestArea = 0;
        foreach (T candidate in candidates)
        {
            long area = IntersectionArea(bounds(candidate), target);
            if (area > bestArea)
            {
                best = candidate;
                bestArea = area;
            }
        }

        return best;
    }

    /// <summary>Bounding box of the dark pixels (the glyph ink) inside a rectangle of a screenshot.</summary>
    private static ScreenRect? InkBounds(Shot shot, ScreenRect within)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int y = within.Y; y < within.Y + within.Height; y++)
        {
            for (int x = within.X; x < within.X + within.Width; x++)
            {
                if (!shot.Contains(x, y) || shot.Luma(x, y) > 140)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX)
        {
            return null;
        }

        return new ScreenRect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Largest distance in pixels between corresponding edges of two rectangles.</summary>
    private static int EdgeError(ScreenRect a, ScreenRect b)
    {
        return Math.Max(
            Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)),
            Math.Max(Math.Abs(a.X + a.Width - b.X - b.Width), Math.Abs(a.Y + a.Height - b.Y - b.Height)));
    }

    private static double Median(IEnumerable<double> values)
    {
        List<double> sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0)
        {
            return 0;
        }

        return sorted.Count % 2 == 1
            ? sorted[sorted.Count / 2]
            : (sorted[sorted.Count / 2 - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best effort
        }
    }

    // ------------------------------------------------------------------ test doubles

    /// <summary>Stands in for the Chrome extension: "你好" becomes "EN[你好]".</summary>
    private sealed class FakeTranslator : ITranslator
    {
        public int Calls;

        public bool IsReady
        {
            get { return true; }
        }

        public string StatusText
        {
            get { return "fake"; }
        }

        public event EventHandler? StatusChanged
        {
            add { }
            remove { }
        }

        public Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(t => "EN[" + t + "]").ToList());
        }
    }

    private sealed class PresenterEvent
    {
        public PresenterEvent(DateTime utc, IReadOnlyList<TranslatedRegion>? regions)
        {
            Utc = utc;
            Regions = regions;
        }

        public DateTime Utc { get; }

        /// <summary>null for Clear().</summary>
        public IReadOnlyList<TranslatedRegion>? Regions { get; }
    }

    /// <summary>Forwards to the real overlay and remembers every call.</summary>
    private sealed class RecordingPresenter : IOverlayPresenter
    {
        private readonly IOverlayPresenter _inner;
        private readonly List<PresenterEvent> _events = new List<PresenterEvent>();

        public RecordingPresenter(IOverlayPresenter inner)
        {
            _inner = inner;
        }

        public List<PresenterEvent> Snapshot()
        {
            lock (_events)
            {
                return _events.ToList();
            }
        }

        public void Show(IReadOnlyList<TranslatedRegion> regions)
        {
            lock (_events)
            {
                _events.Add(new PresenterEvent(DateTime.UtcNow, regions));
            }

            _inner.Show(regions);
        }

        public void Clear()
        {
            lock (_events)
            {
                _events.Add(new PresenterEvent(DateTime.UtcNow, null));
            }

            _inner.Clear();
        }
    }

    /// <summary>
    /// Fallback mode only (no Chinese OCR pack): the Core pipeline drops text without Han characters,
    /// so a marker character is appended to let the Latin test text through the same pipeline.
    /// </summary>
    private sealed class HanTaggingSource : ITextSource
    {
        private readonly ITextSource _inner;

        public HanTaggingSource(ITextSource inner)
        {
            _inner = inner;
        }

        public string Name
        {
            get { return _inner.Name; }
        }

        public async Task<IReadOnlyList<TextRegion>?> CaptureAsync(CancellationToken ct)
        {
            IReadOnlyList<TextRegion>? regions = await _inner.CaptureAsync(ct).ConfigureAwait(false);
            if (regions == null)
            {
                return null;
            }

            return regions.Select(r => new TextRegion(r.Text + " 文", r.Bounds)).ToList();
        }
    }
}
