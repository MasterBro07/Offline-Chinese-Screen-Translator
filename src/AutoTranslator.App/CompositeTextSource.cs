using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutoTranslator.Core;

namespace AutoTranslator.Desktop;

/// <summary>
/// Mode "Both": UI Automation text where a program exposes it, OCR for everything else.
/// OCR regions that overlap a UI Automation region are dropped.
/// </summary>
internal sealed class CompositeTextSource : ITextSource, IRefreshableTextSource
{
    private static readonly IReadOnlyList<TextRegion> Empty = Array.Empty<TextRegion>();

    private readonly UiAutomationTextSource _uia;
    private readonly OcrTextSource _ocr;
    private IReadOnlyList<TextRegion>? _lastUia;
    private IReadOnlyList<TextRegion>? _lastOcr;

    public CompositeTextSource(UiAutomationTextSource uia, OcrTextSource ocr)
    {
        _uia = uia;
        _ocr = ocr;
    }

    public string Name
    {
        get { return "UI-Automation + OCR"; }
    }

    public string? Warning
    {
        get { return _ocr.Warning; }
    }

    public void Invalidate()
    {
        _uia.Invalidate();
        _ocr.Invalidate();
    }

    public async Task<IReadOnlyList<TextRegion>?> CaptureAsync(CancellationToken ct)
    {
        Task<IReadOnlyList<TextRegion>?> uiaTask = _uia.CaptureAsync(ct);
        Task<IReadOnlyList<TextRegion>?> ocrTask = _ocr.CaptureAsync(ct);
        await Task.WhenAll(uiaTask, ocrTask).ConfigureAwait(false);

        IReadOnlyList<TextRegion>? uia = await uiaTask.ConfigureAwait(false);
        IReadOnlyList<TextRegion>? ocr = await ocrTask.ConfigureAwait(false);

        if (uia == null && ocr == null)
        {
            return null;
        }

        // null means "unchanged": reuse the last known result of that source.
        if (uia != null)
        {
            _lastUia = uia;
        }

        if (ocr != null)
        {
            _lastOcr = ocr;
        }

        return Merge(_lastUia ?? Empty, _lastOcr ?? Empty);
    }

    private static IReadOnlyList<TextRegion> Merge(IReadOnlyList<TextRegion> uia, IReadOnlyList<TextRegion> ocr)
    {
        if (uia.Count == 0)
        {
            return ocr;
        }

        var result = new List<TextRegion>(uia.Count + ocr.Count);
        result.AddRange(uia);

        foreach (TextRegion candidate in ocr)
        {
            bool covered = false;
            foreach (TextRegion existing in uia)
            {
                if (OverlapsSubstantially(candidate.Bounds, existing.Bounds))
                {
                    covered = true;
                    break;
                }
            }

            if (!covered)
            {
                result.Add(candidate);
            }
        }

        return result;
    }

    /// <summary>True when the intersection covers more than half of the smaller rectangle.</summary>
    private static bool OverlapsSubstantially(ScreenRect a, ScreenRect b)
    {
        ScreenRect? intersection = ScreenCapture.Intersect(a, b);
        if (intersection == null)
        {
            return false;
        }

        long intersectionArea = (long)intersection.Value.Width * intersection.Value.Height;
        long smaller = Math.Min((long)a.Width * a.Height, (long)b.Width * b.Height);
        return smaller > 0 && intersectionArea * 2 > smaller;
    }
}
