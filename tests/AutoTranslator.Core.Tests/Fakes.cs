using AutoTranslator.Core;

namespace AutoTranslator.Core.Tests;

internal sealed class FakeTranslator : ITranslator
{
    public bool IsReady { get; set; } = true;
    public string StatusText { get; set; } = "fake";
    public event EventHandler? StatusChanged;
    public List<IReadOnlyList<string>> Calls { get; } = new();
    public Exception? Throw { get; set; }
    public Func<string, string> Map { get; set; } = t => "EN:" + t;

    public void RaiseStatusChanged() => StatusChanged?.Invoke(this, EventArgs.Empty);

    public Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct)
    {
        lock (Calls) Calls.Add(texts.ToList());
        if (Throw is not null) throw Throw;
        return Task.FromResult<IReadOnlyList<string>>(texts.Select(Map).ToList());
    }
}

internal sealed class FakeTextSource : ITextSource
{
    private readonly Queue<IReadOnlyList<TextRegion>?> _queue = new();
    public string Name => "fake";
    public int Captures;
    public Exception? Throw { get; set; }

    public void Enqueue(params TextRegion[]? regions) => _queue.Enqueue(regions);
    public void EnqueueUnchanged() => _queue.Enqueue(null);

    /// <summary>Returns queued captures, then "unchanged" forever.</summary>
    public Task<IReadOnlyList<TextRegion>?> CaptureAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref Captures);
        if (Throw is not null) throw Throw;
        lock (_queue) return Task.FromResult(_queue.Count > 0 ? _queue.Dequeue() : null);
    }
}

internal sealed class FakePresenter : IOverlayPresenter
{
    public List<IReadOnlyList<TranslatedRegion>> Shown { get; } = new();
    public int Clears;
    public bool ThrowOnShow { get; set; }

    public void Show(IReadOnlyList<TranslatedRegion> regions)
    {
        if (ThrowOnShow) throw new InvalidOperationException("overlay broken");
        lock (Shown) Shown.Add(regions);
    }

    public void Clear() => Interlocked.Increment(ref Clears);
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "at-tests-" + Guid.NewGuid().ToString("N"));
    public string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}
