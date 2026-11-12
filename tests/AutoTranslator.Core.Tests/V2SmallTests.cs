using AutoTranslator.Core;

namespace AutoTranslator.Core.Tests;

public class AppSettingsV2Tests
{
    [Fact]
    public void Defaults()
    {
        var s = new AppSettings();
        Assert.Equal("Chrome", s.Engine);
        Assert.Equal("claude-haiku-5-5", s.ClaudeModel);
        Assert.Equal(300000, s.ClaudeDailyCharLimit);
        Assert.True(s.MergeLines);
        Assert.Null(s.TrustedExtensionOrigin);
    }

    [Fact]
    public void RoundTrip()
    {
        using var dir = new TempDir();
        var path = dir.File("settings.json");
        new AppSettings
        {
            Engine = "Claude",
            ClaudeModel = "claude-sonnet-5-5",
            ClaudeDailyCharLimit = 0,
            MergeLines = false,
            TrustedExtensionOrigin = "chrome-extension://abc",
        }.Save(path);

        var loaded = AppSettings.Load(path);

        Assert.Equal("Claude", loaded.Engine);
        Assert.Equal("claude-sonnet-5-5", loaded.ClaudeModel);
        Assert.Equal(0, loaded.ClaudeDailyCharLimit);
        Assert.False(loaded.MergeLines);
        Assert.Equal("chrome-extension://abc", loaded.TrustedExtensionOrigin);
    }

    [Fact]
    public void Load_SanitisesNonsense()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = dir.File("settings.json");
        File.WriteAllText(path,
            """{"Engine":"Gemini","ClaudeModel":"  ","ClaudeDailyCharLimit":-5,"TrustedExtensionOrigin":" "}""");

        var loaded = AppSettings.Load(path);

        Assert.Equal("Chrome", loaded.Engine);
        Assert.Equal("claude-haiku-5-5", loaded.ClaudeModel);
        Assert.Equal(300000, loaded.ClaudeDailyCharLimit);
        Assert.Null(loaded.TrustedExtensionOrigin);
    }

    [Theory]
    [InlineData("claude", "Claude")]
    [InlineData(" CLAUDE ", "Claude")]
    [InlineData("chrome", "Chrome")]
    [InlineData("", "Chrome")]
    public void Load_NormalisesEngine(string stored, string expected)
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = dir.File("settings.json");
        File.WriteAllText(path, $$"""{"Engine":"{{stored}}","ClaudeModel":null}""");

        var loaded = AppSettings.Load(path);

        Assert.Equal(expected, loaded.Engine);
        Assert.Equal("claude-haiku-5-5", loaded.ClaudeModel);
    }

    [Fact]
    public void Load_OldFileWithoutNewProperties_KeepsDefaults()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = dir.File("settings.json");
        File.WriteAllText(path, """{"BridgePort":5000}""");

        var loaded = AppSettings.Load(path);

        Assert.Equal(5000, loaded.BridgePort);
        Assert.Equal("Chrome", loaded.Engine);
        Assert.True(loaded.MergeLines);
        Assert.Equal(300000, loaded.ClaudeDailyCharLimit);
    }
}

public class UsageTrackerTests
{
    [Fact]
    public void Unlimited_CountsEverything()
    {
        var tracker = new UsageTracker();
        Assert.Equal(0, tracker.DailyCharLimit);
        Assert.True(tracker.TryConsume(1_000_000));
        Assert.True(tracker.TryConsume(5));
        Assert.Equal(1_000_005, tracker.CharsToday);
    }

    [Fact]
    public void OverLimit_ConsumesNothing()
    {
        var tracker = new UsageTracker(null, 10);
        Assert.True(tracker.TryConsume(6));
        Assert.False(tracker.TryConsume(5));
        Assert.Equal(6, tracker.CharsToday);
        Assert.True(tracker.TryConsume(4)); // exactly the limit is allowed
        Assert.False(tracker.TryConsume(1));
        Assert.Equal(10, tracker.CharsToday);

        tracker.DailyCharLimit = 0;
        Assert.True(tracker.TryConsume(100));
    }

    [Fact]
    public void ZeroOrNegative_IsANoOp()
    {
        var tracker = new UsageTracker(null, 1);
        var changes = 0;
        tracker.Changed += (_, _) => changes++;
        Assert.True(tracker.TryConsume(0));
        Assert.True(tracker.TryConsume(-5));
        Assert.Equal(0, tracker.CharsToday);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void Changed_IsRaisedOnConsumeRefundAndLimitChange()
    {
        var tracker = new UsageTracker(null, 10);
        var changes = 0;
        tracker.Changed += (_, _) => changes++;

        tracker.TryConsume(3);
        Assert.Equal(1, changes);
        tracker.TryConsume(100); // rejected
        Assert.Equal(1, changes);
        tracker.Refund(2);
        Assert.Equal(2, changes);
        Assert.Equal(1, tracker.CharsToday);
        tracker.DailyCharLimit = 20;
        Assert.Equal(3, changes);
        tracker.DailyCharLimit = 20;
        Assert.Equal(3, changes);

        tracker.Refund(50);
        Assert.Equal(0, tracker.CharsToday);
    }

    [Fact]
    public void ResetsWhenTheLocalDateChanges()
    {
        var now = new DateTime(2026, 11, 12, 23, 59, 0);
        var tracker = new UsageTracker(null, 10, () => now);
        Assert.True(tracker.TryConsume(10));
        Assert.False(tracker.TryConsume(1));

        now = now.AddMinutes(2);

        Assert.Equal(0, tracker.CharsToday);
        Assert.True(tracker.TryConsume(7));
        Assert.Equal(7, tracker.CharsToday);
    }

    [Fact]
    public void PersistsAcrossInstances_OnTheSameDay()
    {
        using var dir = new TempDir();
        var path = dir.File("sub/usage.json");
        var now = new DateTime(2026, 11, 12, 10, 0, 0);

        var first = new UsageTracker(path, 100, () => now);
        first.TryConsume(40);
        first.TryConsume(2);
        first.Save();
        Assert.Contains("\"date\":\"2026-11-12\"", File.ReadAllText(path));
        Assert.Contains("\"chars\":42", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));

        Assert.Equal(42, new UsageTracker(path, 100, () => now).CharsToday);
        Assert.Equal(0, new UsageTracker(path, 100, () => now.AddDays(1)).CharsToday);
    }

    [Fact]
    public void FirstConsume_WritesTheFileWithoutExplicitSave()
    {
        using var dir = new TempDir();
        var path = dir.File("usage.json");
        new UsageTracker(path, 0).TryConsume(9);
        Assert.Equal(9, new UsageTracker(path, 0).CharsToday);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"date\":\"yesterday\",\"chars\":5}")]
    [InlineData("{\"date\":null,\"chars\":5}")]
    [InlineData("[1,2,3]")]
    [InlineData("")]
    public void CorruptFile_StartsAtZero(string content)
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = dir.File("usage.json");
        File.WriteAllText(path, content);

        var tracker = new UsageTracker(path, 10);

        Assert.Equal(0, tracker.CharsToday);
        Assert.True(tracker.TryConsume(3));
        tracker.Save();
        Assert.Equal(3, new UsageTracker(path, 10).CharsToday);
    }

    [Fact]
    public void Save_NeverThrows()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(dir.File("blocker"), "a file, not a directory");
        var tracker = new UsageTracker(dir.File("blocker/usage.json"), 10);

        Assert.True(tracker.TryConsume(1));
        tracker.Save();
        new UsageTracker().Save();

        Assert.Equal(1, tracker.CharsToday);
    }

    [Fact]
    public async Task TryConsume_IsAtomicUnderContention()
    {
        var tracker = new UsageTracker(null, 1000);
        var granted = 0;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 500; i++)
            {
                if (tracker.TryConsume(1)) Interlocked.Increment(ref granted);
            }
        })));

        Assert.Equal(1000, granted);
        Assert.Equal(1000, tracker.CharsToday);
    }
}

public class FallbackTranslatorTests
{
    private static (FallbackTranslator, FakeTranslator, FakeTranslator) Create()
    {
        var primary = new FakeTranslator { StatusText = "Claude bereit (Haiku)", Map = t => "P:" + t };
        var fallback = new FakeTranslator { StatusText = "Verbunden – bereit", Map = t => "F:" + t };
        return (new FallbackTranslator(primary, fallback), primary, fallback);
    }

    [Fact]
    public void IsReady_WhenEitherIsReady()
    {
        var (translator, primary, fallback) = Create();
        Assert.True(translator.IsReady);
        primary.IsReady = false;
        Assert.True(translator.IsReady);
        fallback.IsReady = false;
        Assert.False(translator.IsReady);
        primary.IsReady = true;
        Assert.True(translator.IsReady);
    }

    [Fact]
    public void StatusText_CombinesBoth()
    {
        var (translator, _, fallback) = Create();
        Assert.Equal("Claude bereit (Haiku) · Chrome: Verbunden – bereit", translator.StatusText);

        fallback.StatusText = "Chrome-Erweiterung nicht verbunden"; // already names the engine
        Assert.Equal("Claude bereit (Haiku) · Chrome-Erweiterung nicht verbunden", translator.StatusText);
    }

    [Fact]
    public void StatusChanged_IsForwardedFromBoth()
    {
        var (translator, primary, fallback) = Create();
        var changes = 0;
        translator.StatusChanged += (sender, _) =>
        {
            Assert.Same(translator, sender);
            changes++;
        };

        primary.RaiseStatusChanged();
        fallback.RaiseStatusChanged();

        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task UsesPrimary_WhenReady()
    {
        var (translator, _, fallback) = Create();
        Assert.Equal(new[] { "P:你好" }, await translator.TranslateAsync(new[] { "你好" }, "zh", "en", default));
        Assert.Empty(fallback.Calls);
    }

    [Fact]
    public async Task UsesFallback_WhenPrimaryIsNotReady()
    {
        var (translator, primary, _) = Create();
        primary.IsReady = false;
        Assert.Equal(new[] { "F:你好" }, await translator.TranslateAsync(new[] { "你好" }, "zh", "en", default));
        Assert.Empty(primary.Calls);
    }

    [Fact]
    public async Task UsesFallback_WhenPrimaryThrows()
    {
        var (translator, primary, fallback) = Create();
        primary.Throw = new InvalidOperationException("Tageslimit für Claude erreicht");

        Assert.Equal(new[] { "F:你好" }, await translator.TranslateAsync(new[] { "你好" }, "zh", "en", default));
        Assert.Single(primary.Calls);
        Assert.Single(fallback.Calls);
    }

    [Fact]
    public async Task Rethrows_WhenPrimaryThrowsAndFallbackIsNotReady()
    {
        var (translator, primary, fallback) = Create();
        primary.Throw = new TimeoutException("Zeitüberschreitung bei Claude");
        fallback.IsReady = false;

        var ex = await Assert.ThrowsAsync<TimeoutException>(
            () => translator.TranslateAsync(new[] { "你好" }, "zh", "en", default));
        Assert.Equal("Zeitüberschreitung bei Claude", ex.Message);
        Assert.Empty(fallback.Calls);
    }

    [Fact]
    public async Task Cancellation_DoesNotFallBack()
    {
        var (translator, primary, fallback) = Create();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        primary.Throw = new OperationCanceledException(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => translator.TranslateAsync(new[] { "你好" }, "zh", "en", cts.Token));
        Assert.Empty(fallback.Calls);
    }

    [Fact]
    public async Task InternalTimeoutOfPrimary_FallsBack()
    {
        // An OperationCanceledException that is not the caller's cancellation is just an error.
        var (translator, primary, _) = Create();
        primary.Throw = new TaskCanceledException("inner timeout");

        Assert.Equal(new[] { "F:你好" }, await translator.TranslateAsync(new[] { "你好" }, "zh", "en", default));
    }

    [Fact]
    public async Task NeitherReady_Throws()
    {
        var (translator, primary, fallback) = Create();
        primary.IsReady = false;
        fallback.IsReady = false;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => translator.TranslateAsync(new[] { "你好" }, "zh", "en", default));
        Assert.Empty(primary.Calls);
        Assert.Empty(fallback.Calls);
    }
}

public class CachingTranslatorEngineTagTests
{
    [Fact]
    public async Task EngineTag_KeepsEnginesApart()
    {
        var cache = new TranslationCache();
        var chromeInner = new FakeTranslator { Map = t => "chrome:" + t };
        var claudeInner = new FakeTranslator { Map = t => "claude:" + t };
        var chrome = new CachingTranslator(chromeInner, cache);
        var claude = new CachingTranslator(claudeInner, cache, "claude");

        Assert.Equal(new[] { "chrome:你好" }, await chrome.TranslateAsync(new[] { "你好" }, "zh", "en", default));
        Assert.Equal(new[] { "claude:你好" }, await claude.TranslateAsync(new[] { "你好" }, "zh", "en", default));
        Assert.Equal(new[] { "claude:你好" }, await claude.TranslateAsync(new[] { "你好" }, "zh", "en", default));
        Assert.Equal(new[] { "chrome:你好" }, await chrome.TranslateAsync(new[] { "你好" }, "zh", "en", default));

        Assert.Single(chromeInner.Calls);
        Assert.Single(claudeInner.Calls);
        Assert.True(cache.TryGet("zh", "en", "你好", out var plain));
        Assert.Equal("chrome:你好", plain);
        Assert.True(cache.TryGet("zh", "en|claude", "你好", out var tagged));
        Assert.Equal("claude:你好", tagged);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task NoEngineTag_UsesThePlainTargetKey(string? tag)
    {
        var cache = new TranslationCache();
        var translator = new CachingTranslator(new FakeTranslator(), cache, tag);

        await translator.TranslateAsync(new[] { "你好" }, "zh", "en", default);

        Assert.True(cache.TryGet("zh", "en", "你好", out _));
    }
}
