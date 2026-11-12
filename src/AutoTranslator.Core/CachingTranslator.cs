namespace AutoTranslator.Core;

/// <summary>Answers from the cache where possible and forwards only the distinct misses to the inner translator.</summary>
public sealed class CachingTranslator : ITranslator
{
    private readonly ITranslator _inner;
    private readonly TranslationCache _cache;

    public CachingTranslator(ITranslator inner, TranslationCache cache)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public bool IsReady => _inner.IsReady;

    public string StatusText => _inner.StatusText;

    public event EventHandler? StatusChanged
    {
        add => _inner.StatusChanged += value;
        remove => _inner.StatusChanged -= value;
    }

    public async Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var results = new string[texts.Count];
        var missTexts = new List<string>();
        var missIndexes = new Dictionary<string, List<int>>(StringComparer.Ordinal);

        for (int i = 0; i < texts.Count; i++)
        {
            var text = texts[i];
            if (string.IsNullOrWhiteSpace(text))
            {
                results[i] = text ?? string.Empty;
            }
            else if (_cache.TryGet(sourceLanguage, targetLanguage, text, out var cached))
            {
                results[i] = cached;
            }
            else if (missIndexes.TryGetValue(text, out var indexes))
            {
                indexes.Add(i);
            }
            else
            {
                missIndexes[text] = new List<int> { i };
                missTexts.Add(text);
            }
        }

        if (missTexts.Count == 0) return results;

        var translated = await _inner.TranslateAsync(missTexts, sourceLanguage, targetLanguage, ct).ConfigureAwait(false);
        if (translated is null || translated.Count != missTexts.Count)
        {
            throw new InvalidOperationException(
                $"Übersetzer lieferte {translated?.Count ?? 0} statt {missTexts.Count} Ergebnisse.");
        }

        for (int m = 0; m < missTexts.Count; m++)
        {
            var translation = translated[m] ?? string.Empty;
            // Empty answers are not worth remembering; they are most likely engine hiccups.
            if (translation.Length > 0) _cache.Set(sourceLanguage, targetLanguage, missTexts[m], translation);
            foreach (var i in missIndexes[missTexts[m]]) results[i] = translation;
        }
        return results;
    }
}
