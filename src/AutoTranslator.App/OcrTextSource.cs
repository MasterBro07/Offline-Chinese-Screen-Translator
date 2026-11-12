using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslator.Core;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace AutoTranslator.Desktop;

/// <summary>Reads Chinese text from a screen capture with the OCR engine built into Windows.</summary>
internal sealed class OcrTextSource : ITextSource, IRefreshableTextSource
{
    public const string MissingLanguageMessage = "Chinesisches OCR-Sprachpaket fehlt (siehe Einrichtung / Hilfe)";

    private static readonly string[] PreferredLanguageTags = { "zh-Hans-CN", "zh-Hans", "zh-CN" };
    private static readonly IReadOnlyList<TextRegion> Empty = Array.Empty<TextRegion>();

    // Small captures are enlarged before OCR: Chinese glyphs at 100 % scaling are close to
    // the size limit of the recognizer.
    private const double UpscaleFactor = 1.5;
    private const long UpscaleMaxPixels = 4_000_000;

    private static readonly TimeSpan ForcedRefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan EngineRetryInterval = TimeSpan.FromSeconds(15);

    private static readonly object AvailabilityGate = new object();
    private static DateTime _availabilityCheckedUtc = DateTime.MinValue;
    private static bool _availabilityCached;

    private readonly Func<string> _scopeProvider;

    private OcrEngine? _engine;
    private DateTime _nextEngineAttemptUtc = DateTime.MinValue;
    private volatile string? _warning;

    private byte[]? _captureBuffer;
    private byte[]? _scaledBuffer;

    private bool _hasLast;
    private ScreenRect _lastRect;
    private ulong _lastHash;
    private IReadOnlyList<TextRegion>? _lastResult;
    private DateTime _lastFullResultUtc = DateTime.MinValue;
    private volatile bool _invalidated;
    private int _busy;

    public OcrTextSource(Func<string> scopeProvider)
    {
        _scopeProvider = scopeProvider;
    }

    public string Name
    {
        get { return "OCR"; }
    }

    public string? Warning
    {
        get { return _warning; }
    }

    public void Invalidate()
    {
        _invalidated = true;
    }

    /// <summary>True when Windows has an OCR recognizer for Chinese installed.</summary>
    public static bool IsChineseOcrAvailable()
    {
        // Called by the help window once per second; creating an engine is not free.
        lock (AvailabilityGate)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _availabilityCheckedUtc < TimeSpan.FromSeconds(5))
            {
                return _availabilityCached;
            }

            bool available;
            try
            {
                available = CreateEngine() != null;
            }
            catch
            {
                available = false;
            }

            _availabilityCached = available;
            _availabilityCheckedUtc = now;
            return available;
        }
    }

    public async Task<IReadOnlyList<TextRegion>?> CaptureAsync(CancellationToken ct)
    {
        // The pipeline calls sequentially; this only protects the shared buffers.
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return null;
        }

        try
        {
            return await CaptureCoreAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // E.g. secure desktop (UAC prompt, lock screen): nothing can be captured.
            Log.Error("OCR capture failed", ex);
            ResetChangeDetection();
            return Empty;
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task<IReadOnlyList<TextRegion>?> CaptureCoreAsync(CancellationToken ct)
    {
        OcrEngine? engine = GetEngine();
        if (engine == null)
        {
            return Empty;
        }

        Frame? frame = await Task.Run(() => GrabFrame(), ct).ConfigureAwait(false);
        if (frame == null)
        {
            // Nothing to capture (own window in front, minimized, ...): clear the overlay.
            ResetChangeDetection();
            return Empty;
        }

        if (frame.Unchanged)
        {
            return null;
        }

        IReadOnlyList<TextRegion> regions;
        using (SoftwareBitmap bitmap = frame.Bitmap!)
        {
            ct.ThrowIfCancellationRequested();
            OcrResult result = await engine.RecognizeAsync(bitmap).AsTask(ct).ConfigureAwait(false);
            regions = BuildRegions(result, frame.Rect, frame.Scale);
        }

        bool forced = frame.Forced;
        IReadOnlyList<TextRegion>? previous = _lastResult;
        _hasLast = true;
        _lastRect = frame.Rect;
        _lastHash = frame.Hash;
        _lastResult = regions;

        if (!forced && previous != null && previous.SequenceEqual(regions))
        {
            // Pixels changed (blinking caret, animation) but the text did not.
            return null;
        }

        _lastFullResultUtc = DateTime.UtcNow;
        return regions;
    }

    private void ResetChangeDetection()
    {
        _hasLast = false;
        _lastResult = null;
    }

    /// <summary>Captures the screen and prepares the OCR input. Runs on a thread-pool thread.</summary>
    private Frame? GrabFrame()
    {
        ScreenRect? captureRect = ScreenCapture.GetCaptureRect(_scopeProvider());
        if (captureRect == null)
        {
            return null;
        }

        ScreenRect rect = captureRect.Value;
        bool forced = _invalidated || DateTime.UtcNow - _lastFullResultUtc > ForcedRefreshInterval;
        _invalidated = false;

        using (System.Drawing.Bitmap capture = ScreenCapture.Capture(rect))
        {
            int length = ScreenCapture.CopyPixels(capture, ref _captureBuffer);
            ulong hash = ScreenCapture.Hash(_captureBuffer!, length);

            if (!forced && _hasLast && _lastRect == rect && _lastHash == hash)
            {
                return new Frame { Unchanged = true, Rect = rect, Hash = hash };
            }

            int width = rect.Width;
            int height = rect.Height;
            double scale = (long)width * height <= UpscaleMaxPixels ? UpscaleFactor : 1.0;
            double maxDimension = OcrEngine.MaxImageDimension;
            double longest = Math.Max(width, height);
            if (longest * scale > maxDimension)
            {
                scale = maxDimension / longest;
            }

            byte[] pixels = _captureBuffer!;
            int ocrWidth = width;
            int ocrHeight = height;
            if (Math.Abs(scale - 1.0) > 0.01)
            {
                ocrWidth = Math.Max(1, (int)Math.Floor(width * scale));
                ocrHeight = Math.Max(1, (int)Math.Floor(height * scale));
                using (System.Drawing.Bitmap scaled = ScreenCapture.Resize(capture, ocrWidth, ocrHeight))
                {
                    length = ScreenCapture.CopyPixels(scaled, ref _scaledBuffer);
                }

                pixels = _scaledBuffer!;
            }

            // CreateCopyFromBuffer copies the bytes, so the buffers can be reused next time.
            SoftwareBitmap bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                pixels.AsBuffer(0, length),
                BitmapPixelFormat.Bgra8,
                ocrWidth,
                ocrHeight,
                BitmapAlphaMode.Premultiplied);

            return new Frame
            {
                Bitmap = bitmap,
                Rect = rect,
                Hash = hash,
                Forced = forced,
                ScaleX = (double)ocrWidth / width,
                ScaleY = (double)ocrHeight / height,
            };
        }
    }

    private static IReadOnlyList<TextRegion> BuildRegions(OcrResult result, ScreenRect origin, FrameScale scale)
    {
        var regions = new List<TextRegion>();
        var text = new StringBuilder();

        foreach (OcrLine line in result.Lines)
        {
            text.Clear();
            double left = 0, top = 0, right = 0, bottom = 0;
            bool hasWords = false;

            foreach (OcrWord word in line.Words)
            {
                string wordText = word.Text;
                if (string.IsNullOrEmpty(wordText))
                {
                    continue;
                }

                var box = word.BoundingRect;
                double wordLeft = box.X;
                double wordTop = box.Y;
                double wordRight = box.X + box.Width;
                double wordBottom = box.Y + box.Height;

                if (hasWords)
                {
                    // A wide horizontal gap means a different column / control, not the same sentence.
                    double lineHeight = Math.Max(bottom - top, box.Height);
                    if (wordLeft - right > lineHeight * 2.0)
                    {
                        AddRegion(regions, text, left, top, right, bottom, origin, scale);
                        text.Clear();
                        hasWords = false;
                    }
                }

                if (!hasWords)
                {
                    left = wordLeft;
                    top = wordTop;
                    right = wordRight;
                    bottom = wordBottom;
                    hasWords = true;
                }
                else
                {
                    // Windows OCR returns every Chinese character as its own "word":
                    // join without spaces, except between two Latin words.
                    if (NeedsSpace(text[text.Length - 1], wordText[0]))
                    {
                        text.Append(' ');
                    }

                    left = Math.Min(left, wordLeft);
                    top = Math.Min(top, wordTop);
                    right = Math.Max(right, wordRight);
                    bottom = Math.Max(bottom, wordBottom);
                }

                text.Append(wordText);
            }

            if (hasWords)
            {
                AddRegion(regions, text, left, top, right, bottom, origin, scale);
            }
        }

        return regions;
    }

    private static bool NeedsSpace(char previous, char next)
    {
        // Everything below the CJK blocks (Latin, digits, Cyrillic, ...) is separated by spaces.
        return previous < 0x2E80 && next < 0x2E80;
    }

    private static void AddRegion(
        List<TextRegion> regions, StringBuilder text,
        double left, double top, double right, double bottom,
        ScreenRect origin, FrameScale scale)
    {
        string value = text.ToString().Trim();
        if (value.Length == 0 || !ScriptDetector.ContainsHan(value))
        {
            return;
        }

        int x = origin.X + (int)Math.Floor(left / scale.X);
        int y = origin.Y + (int)Math.Floor(top / scale.Y);
        int x2 = origin.X + (int)Math.Ceiling(right / scale.X);
        int y2 = origin.Y + (int)Math.Ceiling(bottom / scale.Y);
        if (x2 - x < 2 || y2 - y < 2)
        {
            return;
        }

        regions.Add(new TextRegion(value, new ScreenRect(x, y, x2 - x, y2 - y)));
    }

    private OcrEngine? GetEngine()
    {
        if (_engine != null)
        {
            return _engine;
        }

        DateTime now = DateTime.UtcNow;
        if (now < _nextEngineAttemptUtc)
        {
            return null;
        }

        _nextEngineAttemptUtc = now + EngineRetryInterval;
        try
        {
            _engine = CreateEngine();
        }
        catch (Exception ex)
        {
            Log.Error("Creating the OCR engine failed", ex);
            _engine = null;
        }

        _warning = _engine == null ? MissingLanguageMessage : null;
        return _engine;
    }

    private static OcrEngine? CreateEngine()
    {
        foreach (string tag in PreferredLanguageTags)
        {
            OcrEngine? engine = TryCreate(tag);
            if (engine != null)
            {
                return engine;
            }
        }

        // Any other installed Chinese recognizer (e.g. zh-Hant-TW) is better than nothing.
        foreach (Language language in OcrEngine.AvailableRecognizerLanguages)
        {
            string tag = language.LanguageTag ?? string.Empty;
            if (tag.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                OcrEngine? engine = OcrEngine.TryCreateFromLanguage(language);
                if (engine != null)
                {
                    return engine;
                }
            }
        }

        return null;
    }

    private static OcrEngine? TryCreate(string languageTag)
    {
        try
        {
            var language = new Language(languageTag);
            if (!OcrEngine.IsLanguageSupported(language))
            {
                return null;
            }

            return OcrEngine.TryCreateFromLanguage(language);
        }
        catch
        {
            // Invalid or unknown language tag on this Windows version.
            return null;
        }
    }

    /// <summary>OCR image pixels per screen pixel.</summary>
    private readonly struct FrameScale
    {
        public FrameScale(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }

        public double Y { get; }
    }

    private sealed class Frame
    {
        public bool Unchanged { get; set; }

        public bool Forced { get; set; }

        public SoftwareBitmap? Bitmap { get; set; }

        public ScreenRect Rect { get; set; }

        public ulong Hash { get; set; }

        public double ScaleX { get; set; } = 1.0;

        public double ScaleY { get; set; } = 1.0;

        public FrameScale Scale
        {
            get { return new FrameScale(ScaleX, ScaleY); }
        }
    }
}
