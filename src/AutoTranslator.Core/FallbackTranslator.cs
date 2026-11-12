namespace AutoTranslator.Core;

/// <summary>
/// Uses the primary engine while it is ready and switches to the fallback when the primary is
/// not ready or fails.
/// </summary>
public sealed class FallbackTranslator : ITranslator
{
    private readonly ITranslator _primary;
    private readonly ITranslator _fallback;

    public FallbackTranslator(ITranslator primary, ITranslator fallback)
    {
        _primary = primary ?? throw new ArgumentNullException(nameof(primary));
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _primary.StatusChanged += OnInnerStatusChanged;
        _fallback.StatusChanged += OnInnerStatusChanged;
    }

    /// <summary>Name put in front of the fallback's status in <see cref="StatusText"/>.</summary>
    public string FallbackLabel { get; set; } = "Chrome";

    public bool IsReady => _primary.IsReady || _fallback.IsReady;

    /// <summary>Both engines' states, e.g. "Claude bereit (Haiku) · Chrome: Verbunden – bereit".</summary>
    public string StatusText
    {
        get
        {
            var fallback = _fallback.StatusText;
            var label = FallbackLabel;
            if (!string.IsNullOrEmpty(label) && !fallback.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                fallback = label + ": " + fallback;
            }
            return _primary.StatusText + " · " + fallback;
        }
    }

    public event EventHandler? StatusChanged;

    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        if (_primary.IsReady)
        {
            try
            {
                return await _primary.TranslateAsync(texts, sourceLanguage, targetLanguage, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested)
                                       && _fallback.IsReady)
            {
                // The primary's own status text reports the error; the fallback keeps things going.
            }
            ct.ThrowIfCancellationRequested();
        }
        else if (!_fallback.IsReady)
        {
            throw new InvalidOperationException(StatusText);
        }

        return await _fallback.TranslateAsync(texts, sourceLanguage, targetLanguage, ct).ConfigureAwait(false);
    }

    private void OnInnerStatusChanged(object? sender, EventArgs e)
    {
        try { StatusChanged?.Invoke(this, EventArgs.Empty); } catch { /* listener errors are not ours */ }
    }
}
