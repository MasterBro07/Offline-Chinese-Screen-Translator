using AutoTranslator.Core;

namespace AutoTranslator.Core.Tests;

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

public class AppSettingsLegacyTests
{
    [Fact]
    public void Load_SettingsFileFromTheClaudeVersion_StillLoads()
    {
        string dir = Path.Combine(Path.GetTempPath(), "at-legacy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "settings.json");
        File.WriteAllText(path,
            """{"Engine":"Claude","ClaudeModel":"claude-haiku-5-5","ClaudeDailyCharLimit":300000,"TrustedExtensionOrigin":"chrome-extension://abc","MergeLines":false,"Enabled":false}""");
        try
        {
            var loaded = AppSettings.Load(path);
            Assert.False(loaded.MergeLines);
            Assert.False(loaded.Enabled);
            Assert.Equal("zh", loaded.SourceLanguage);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
