using System.Diagnostics;

namespace AutoTranslator.Core;

/// <summary>Capture → filter Han text → translate → overlay, repeated on a background loop.</summary>
public sealed class TranslationPipeline : IAsyncDisposable
{
    private readonly ITextSource _source;
    private readonly ITranslator _translator;
    private readonly IOverlayPresenter _presenter;
    private readonly PipelineOptions _options;

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _cycleGate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private string _statusText = "Pausiert";

    // Cycle state, only touched while holding _cycleGate.
    private IReadOnlyList<TextRegion>? _lastHanRegions;   // Han regions of the last real capture
    private bool _retryLastCapture;                       // last capture was not (successfully) shown
    private IReadOnlyList<TranslatedRegion>? _lastShown;  // null = overlay state unknown
    private bool _overlayCleared;

    public TranslationPipeline(ITextSource source, ITranslator translator,
                               IOverlayPresenter presenter, PipelineOptions options)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _translator = translator ?? throw new ArgumentNullException(nameof(translator));
        _presenter = presenter ?? throw new ArgumentNullException(nameof(presenter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public bool IsRunning
    {
        get { lock (_stateLock) return _loop is not null; }
    }

    /// <summary>German status line for the tray tooltip.</summary>
    public string StatusText
    {
        get { lock (_stateLock) return _statusText; }
    }

    public event EventHandler? StatusChanged;

    public void Start()
    {
        lock (_stateLock)
        {
            if (_loop is not null) return;
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _loop = Task.Run(() => LoopAsync(token));
        }
    }

    /// <summary>Stops the loop, waits for it and clears the overlay. Safe to call repeatedly.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? loop;
        lock (_stateLock)
        {
            cts = _cts;
            loop = _loop;
            _cts = null;
            _loop = null;
        }

        if (cts is not null)
        {
            cts.Cancel();
            if (loop is not null)
            {
                try { await loop.ConfigureAwait(false); } catch { /* the loop swallows its own errors */ }
            }
            cts.Dispose();
        }

        await _cycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            ClearOverlay(force: true);
            // The source may keep reporting "unchanged" after a restart, so remember to redraw.
            _retryLastCapture = _lastHanRegions is { Count: > 0 };
        }
        finally
        {
            _cycleGate.Release();
        }
        SetStatus("Pausiert");
    }

    /// <summary>Runs one cycle and returns the number of regions on the overlay. Only throws on cancellation.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await _cycleGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunCycleAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _retryLastCapture = true;
            ClearOverlay(force: false);
            SetStatus("Fehler: " + ex.Message);
            return 0;
        }
        finally
        {
            _cycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private async Task<int> RunCycleAsync(CancellationToken ct)
    {
        var captured = await _source.CaptureAsync(ct).ConfigureAwait(false);
        if (captured is not null)
        {
            _lastHanRegions = captured
                .Where(r => r is not null && ScriptDetector.ContainsHan(r.Text))
                .ToList();
            _retryLastCapture = true;
        }
        else if (!_retryLastCapture || _lastHanRegions is null)
        {
            return _lastShown?.Count ?? 0; // unchanged: keep the overlay
        }

        var regions = _lastHanRegions!;
        if (regions.Count == 0)
        {
            _retryLastCapture = false;
            ClearOverlay(force: false);
            SetStatus("Aktiv – kein Text zu übersetzen");
            return 0;
        }

        if (!_translator.IsReady)
        {
            ClearOverlay(force: false);
            SetStatus("Warte auf Chrome-Erweiterung");
            return 0;
        }

        var texts = regions.Select(r => r.Text).ToList();
        var translations = await _translator
            .TranslateAsync(texts, _options.SourceLanguage, _options.TargetLanguage, ct)
            .ConfigureAwait(false);
        if (translations is null || translations.Count != regions.Count)
        {
            throw new InvalidOperationException("Übersetzer lieferte eine unerwartete Anzahl von Ergebnissen.");
        }

        var result = new List<TranslatedRegion>(regions.Count);
        for (int i = 0; i < regions.Count; i++)
        {
            var translated = translations[i];
            if (string.IsNullOrWhiteSpace(translated)) continue;
            result.Add(new TranslatedRegion(regions[i].Text, translated, regions[i].Bounds));
        }

        if (result.Count == 0)
        {
            _retryLastCapture = false;
            ClearOverlay(force: false);
            SetStatus("Aktiv – kein Text zu übersetzen");
            return 0;
        }

        if (_lastShown is null || !_lastShown.SequenceEqual(result))
        {
            _lastShown = null; // unknown if Show throws
            _presenter.Show(result);
            _lastShown = result;
            _overlayCleared = false;
        }
        _retryLastCapture = false;
        SetStatus(result.Count == 1 ? "Aktiv – 1 Text übersetzt" : $"Aktiv – {result.Count} Texte übersetzt");
        return result.Count;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        SetStatus("Aktiv");
        var stopwatch = new Stopwatch();
        while (!ct.IsCancellationRequested)
        {
            stopwatch.Restart();
            try
            {
                await RunOnceAsync(ct).ConfigureAwait(false);
                // Wait only for the rest of the interval so slow cycles do not pile up.
                var remaining = _options.Interval - stopwatch.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, ct).ConfigureAwait(false);
                else await Task.Yield();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Nothing may kill the loop; back off briefly and try again.
                try { await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void ClearOverlay(bool force)
    {
        if (_overlayCleared && !force) return;
        _lastShown = null;
        try
        {
            _presenter.Clear();
            _overlayCleared = true;
        }
        catch
        {
            _overlayCleared = false;
        }
    }

    private void SetStatus(string text)
    {
        lock (_stateLock)
        {
            if (_statusText == text) return;
            _statusText = text;
        }
        try { StatusChanged?.Invoke(this, EventArgs.Empty); } catch { /* listener errors are not ours */ }
    }
}
