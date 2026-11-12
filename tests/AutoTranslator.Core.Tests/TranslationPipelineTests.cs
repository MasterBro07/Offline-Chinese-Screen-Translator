using AutoTranslator.Core;

namespace AutoTranslator.Core.Tests;

public class TranslationPipelineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static TextRegion Region(string text, int x = 0) => new(text, new ScreenRect(x, 0, 100, 20));

    private static (TranslationPipeline, FakeTextSource, FakeTranslator, FakePresenter) Create(int intervalMs = 5)
    {
        var source = new FakeTextSource();
        var translator = new FakeTranslator();
        var presenter = new FakePresenter();
        var pipeline = new TranslationPipeline(source, translator, presenter,
            new PipelineOptions { Interval = TimeSpan.FromMilliseconds(intervalMs) });
        return (pipeline, source, translator, presenter);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "condition not reached in time");
            await Task.Delay(5);
        }
    }

    [Fact]
    public async Task TranslatesOnlyHanRegions_InOneBatch()
    {
        var (pipeline, source, translator, presenter) = Create();
        source.Enqueue(Region("你好", 1), Region("Hello", 2), Region("世界", 3));

        Assert.Equal(2, await pipeline.RunOnceAsync(default));

        Assert.Equal(new[] { "你好", "世界" }, Assert.Single(translator.Calls));
        var shown = Assert.Single(presenter.Shown);
        Assert.Equal(new TranslatedRegion("你好", "EN:你好", new ScreenRect(1, 0, 100, 20)), shown[0]);
        Assert.Equal(new TranslatedRegion("世界", "EN:世界", new ScreenRect(3, 0, 100, 20)), shown[1]);
        Assert.Equal("Aktiv – 2 Texte übersetzt", pipeline.StatusText);
    }

    [Fact]
    public async Task UnchangedCapture_KeepsOverlayWithoutNewShow()
    {
        var (pipeline, source, translator, presenter) = Create();
        source.Enqueue(Region("你好"));
        source.EnqueueUnchanged();
        source.Enqueue(Region("你好")); // changed capture, identical result

        Assert.Equal(1, await pipeline.RunOnceAsync(default));
        Assert.Equal(1, await pipeline.RunOnceAsync(default));
        Assert.Equal(1, await pipeline.RunOnceAsync(default));

        Assert.Single(presenter.Shown);
        Assert.Equal(0, presenter.Clears);
        Assert.Equal(2, translator.Calls.Count); // the null capture did not translate
    }

    [Fact]
    public async Task NoHanText_ClearsOverlay()
    {
        var (pipeline, source, translator, presenter) = Create();
        source.Enqueue(Region("你好"));
        source.Enqueue(Region("Hello"));
        source.Enqueue();

        Assert.Equal(1, await pipeline.RunOnceAsync(default));
        Assert.Equal(0, await pipeline.RunOnceAsync(default));
        Assert.Equal(1, presenter.Clears);
        Assert.Equal(0, await pipeline.RunOnceAsync(default));
        Assert.Equal(1, presenter.Clears); // already clear
        Assert.Single(translator.Calls);
    }

    [Fact]
    public async Task TranslatorThrows_ClearsSetsStatus_AndRecoversOnUnchangedScreen()
    {
        var (pipeline, source, translator, presenter) = Create();
        translator.Throw = new InvalidOperationException("kaputt");
        source.Enqueue(Region("你好"));

        Assert.Equal(0, await pipeline.RunOnceAsync(default));
        Assert.Equal(1, presenter.Clears);
        Assert.Equal("Fehler: kaputt", pipeline.StatusText);

        // Screen unchanged, translator works again: the pending capture is retried.
        translator.Throw = null;
        Assert.Equal(1, await pipeline.RunOnceAsync(default));
        Assert.Single(presenter.Shown);
    }

    [Fact]
    public async Task TranslatorNotReady_ClearsAndWaits_ThenShowsWhenReady()
    {
        var (pipeline, source, translator, presenter) = Create();
        translator.IsReady = false;
        source.Enqueue(Region("你好"));

        Assert.Equal(0, await pipeline.RunOnceAsync(default));
        Assert.Empty(translator.Calls);
        Assert.Equal(1, presenter.Clears);
        Assert.Equal("Warte auf Chrome-Erweiterung", pipeline.StatusText);

        translator.IsReady = true;
        Assert.Equal(1, await pipeline.RunOnceAsync(default));
        Assert.Single(presenter.Shown);
    }

    [Fact]
    public async Task SourceOrPresenterThrowing_DoesNotThrow()
    {
        var (pipeline, source, _, presenter) = Create();
        source.Throw = new IOException("capture failed");
        Assert.Equal(0, await pipeline.RunOnceAsync(default));
        Assert.StartsWith("Fehler: ", pipeline.StatusText);

        source.Throw = null;
        presenter.ThrowOnShow = true;
        source.Enqueue(Region("你好"));
        Assert.Equal(0, await pipeline.RunOnceAsync(default));

        presenter.ThrowOnShow = false;
        Assert.Equal(1, await pipeline.RunOnceAsync(default));
    }

    [Fact]
    public async Task Loop_SurvivesErrors_AndStopClearsOverlay()
    {
        var (pipeline, source, translator, presenter) = Create(intervalMs: 1);
        var statusChanges = 0;
        pipeline.StatusChanged += (_, _) => Interlocked.Increment(ref statusChanges);
        Assert.Equal("Pausiert", pipeline.StatusText);
        Assert.False(pipeline.IsRunning);

        translator.Throw = new InvalidOperationException("boom");
        source.Enqueue(Region("你好"));
        pipeline.Start();
        pipeline.Start(); // idempotent
        Assert.True(pipeline.IsRunning);

        await WaitUntil(() => translator.Calls.Count >= 3); // kept looping through failures
        translator.Throw = null;
        await WaitUntil(() => presenter.Shown.Count == 1);

        var clearsBefore = presenter.Clears;
        await pipeline.StopAsync().WaitAsync(Timeout);
        await pipeline.StopAsync().WaitAsync(Timeout);
        Assert.False(pipeline.IsRunning);
        Assert.True(presenter.Clears > clearsBefore);
        Assert.Equal("Pausiert", pipeline.StatusText);
        Assert.True(statusChanges >= 2);

        var captures = source.Captures;
        await Task.Delay(30);
        Assert.Equal(captures, source.Captures); // really stopped

        // Restart: the source says "unchanged", but the overlay was cleared, so it is redrawn.
        pipeline.Start();
        await WaitUntil(() => presenter.Shown.Count == 2);
        await pipeline.DisposeAsync();
    }
}
