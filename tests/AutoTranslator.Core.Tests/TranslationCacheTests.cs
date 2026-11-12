using AutoTranslator.Core;

namespace AutoTranslator.Core.Tests;

public class TranslationCacheTests
{
    [Fact]
    public void SetAndGet_KeyIncludesLanguages()
    {
        var cache = new TranslationCache();
        cache.Set("zh", "en", "你好", "Hello");

        Assert.True(cache.TryGet("zh", "en", "你好", out var t));
        Assert.Equal("Hello", t);
        Assert.False(cache.TryGet("zh", "de", "你好", out _));
        Assert.False(cache.TryGet("ja", "en", "你好", out _));
        Assert.Equal(1, cache.Count);

        cache.Set("zh", "en", "你好", "Hi");
        Assert.True(cache.TryGet("zh", "en", "你好", out t));
        Assert.Equal("Hi", t);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void EvictsLeastRecentlyUsed()
    {
        var cache = new TranslationCache(capacity: 2);
        cache.Set("zh", "en", "a", "A");
        cache.Set("zh", "en", "b", "B");
        Assert.True(cache.TryGet("zh", "en", "a", out _)); // "b" is now the oldest
        cache.Set("zh", "en", "c", "C");

        Assert.Equal(2, cache.Count);
        Assert.True(cache.TryGet("zh", "en", "a", out _));
        Assert.False(cache.TryGet("zh", "en", "b", out _));
        Assert.True(cache.TryGet("zh", "en", "c", out _));
    }

    [Fact]
    public void Persistence_RoundTripKeepsEntriesAndLruOrder()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "sub", "cache.json");
        var cache = new TranslationCache(10, path);
        cache.Set("zh", "en", "一", "one");
        cache.Set("zh", "en", "二", "two");
        cache.Set("zh", "en", "三", "three");
        Assert.True(cache.TryGet("zh", "en", "一", out _)); // oldest is now "二"
        cache.Save();

        Assert.True(File.Exists(path));
        Assert.False(File.Exists(path + ".tmp"));

        var loaded = new TranslationCache(3, path);
        loaded.Load();
        Assert.Equal(3, loaded.Count);
        loaded.Set("zh", "en", "四", "four");
        Assert.False(loaded.TryGet("zh", "en", "二", out _));
        Assert.True(loaded.TryGet("zh", "en", "一", out var one));
        Assert.Equal("one", one);
    }

    [Fact]
    public void Load_CorruptOrMissingFile_DoesNotThrow()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        var path = dir.File("cache.json");

        var cache = new TranslationCache(10, path);
        cache.Load(); // missing
        Assert.Equal(0, cache.Count);

        File.WriteAllText(path, "[{\"s\":\"zh\",");
        cache.Load();
        Assert.Equal(0, cache.Count);

        File.WriteAllText(path, "[{\"s\":\"zh\",\"t\":\"en\",\"x\":\"好\",\"v\":\"good\"},{\"s\":null},null]");
        cache.Load();
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void WithoutFilePath_LoadAndSaveAreNoOps()
    {
        var cache = new TranslationCache();
        cache.Set("zh", "en", "a", "A");
        cache.Load();
        cache.Save();
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Save_ToUnwritablePath_DoesNotThrow()
    {
        using var dir = new TempDir();
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(dir.File("file"), "x");
        var cache = new TranslationCache(10, Path.Combine(dir.File("file"), "cache.json")); // parent is a file
        cache.Set("zh", "en", "a", "A");
        cache.Save();
    }

    [Fact]
    public void ConcurrentUse_StaysWithinCapacity()
    {
        var cache = new TranslationCache(100);
        Parallel.For(0, 2000, i =>
        {
            cache.Set("zh", "en", "k" + (i % 300), "v");
            cache.TryGet("zh", "en", "k" + (i % 50), out _);
        });
        Assert.InRange(cache.Count, 1, 100);
    }
}
