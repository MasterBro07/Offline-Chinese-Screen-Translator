using AutoTranslator.Core;

namespace AutoTranslator.Core.Tests;

public class CachingTranslatorTests
{
    [Fact]
    public async Task DedupesWithinBatch_AndPreservesOrder()
    {
        var inner = new FakeTranslator();
        var sut = new CachingTranslator(inner, new TranslationCache());

        var result = await sut.TranslateAsync(new[] { "你好", "世界", "你好", "", "  ", "世界" }, "zh", "en", default);

        Assert.Equal(new[] { "EN:你好", "EN:世界", "EN:你好", "", "  ", "EN:世界" }, result);
        Assert.Equal(new[] { "你好", "世界" }, Assert.Single(inner.Calls));
    }

    [Fact]
    public async Task SendsOnlyMisses()
    {
        var inner = new FakeTranslator();
        var cache = new TranslationCache();
        cache.Set("zh", "en", "世界", "World");
        var sut = new CachingTranslator(inner, cache);

        var result = await sut.TranslateAsync(new[] { "你好", "世界", "朋友" }, "zh", "en", default);

        Assert.Equal(new[] { "EN:你好", "World", "EN:朋友" }, result);
        Assert.Equal(new[] { "你好", "朋友" }, Assert.Single(inner.Calls));

        // Everything is cached now.
        await sut.TranslateAsync(new[] { "朋友", "你好" }, "zh", "en", default);
        Assert.Single(inner.Calls);
    }

    [Fact]
    public async Task FullyCached_WorksWhenInnerIsNotReady()
    {
        var inner = new FakeTranslator { IsReady = false, Throw = new InvalidOperationException("not connected") };
        var cache = new TranslationCache();
        cache.Set("zh", "en", "你好", "Hello");
        var sut = new CachingTranslator(inner, cache);

        var result = await sut.TranslateAsync(new[] { "你好", "", "你好" }, "zh", "en", default);

        Assert.Equal(new[] { "Hello", "", "Hello" }, result);
        Assert.Empty(inner.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TranslateAsync(new[] { "新" }, "zh", "en", default));
    }

    [Fact]
    public async Task EmptyBatch_DoesNotCallInner()
    {
        var inner = new FakeTranslator();
        var sut = new CachingTranslator(inner, new TranslationCache());
        Assert.Empty(await sut.TranslateAsync(Array.Empty<string>(), "zh", "en", default));
        Assert.Empty(inner.Calls);
    }

    [Fact]
    public void ForwardsStateAndEvents()
    {
        var inner = new FakeTranslator { IsReady = false, StatusText = "status" };
        var sut = new CachingTranslator(inner, new TranslationCache());
        int raised = 0;
        EventHandler handler = (_, _) => raised++;
        sut.StatusChanged += handler;

        Assert.False(sut.IsReady);
        Assert.Equal("status", sut.StatusText);
        inner.IsReady = true;
        inner.RaiseStatusChanged();
        Assert.True(sut.IsReady);
        Assert.Equal(1, raised);

        sut.StatusChanged -= handler;
        inner.RaiseStatusChanged();
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task WrongResultCount_Throws()
    {
        var inner = new BrokenTranslator();
        var sut = new CachingTranslator(inner, new TranslationCache());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => sut.TranslateAsync(new[] { "一", "二" }, "zh", "en", default));
    }

    private sealed class BrokenTranslator : ITranslator
    {
        public bool IsReady => true;
        public string StatusText => "";
        public event EventHandler? StatusChanged { add { } remove { } }
        public Task<IReadOnlyList<string>> TranslateAsync(
            IReadOnlyList<string> texts, string s, string t, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "only one" });
    }
}
