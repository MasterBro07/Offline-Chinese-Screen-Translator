using AutoTranslator.Core;

namespace AutoTranslator.Core.Tests;

public class AppSettingsTests
{
    [Fact]
    public void Defaults()
    {
        var s = new AppSettings();
        Assert.Equal("zh", s.SourceLanguage);
        Assert.Equal("en", s.TargetLanguage);
        Assert.Equal(47613, s.BridgePort);
        Assert.Equal(700, s.CaptureIntervalMs);
        Assert.Equal("Ocr", s.TextSourceMode);
        Assert.Equal("ForegroundWindow", s.CaptureScope);
        Assert.True(s.Enabled);
        Assert.EndsWith("AutoTranslator", AppSettings.DefaultDirectory);
    }

    [Fact]
    public void SaveCreatesDirectory_AndRoundTrips()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "nested", "settings.json");
        var s = new AppSettings
        {
            SourceLanguage = "ja", TargetLanguage = "de", BridgePort = 5000, CaptureIntervalMs = 250,
            TextSourceMode = "Both", CaptureScope = "PrimaryMonitor", Enabled = false,
        };
        s.Save(path);

        var loaded = AppSettings.Load(path);
        Assert.Equal("ja", loaded.SourceLanguage);
        Assert.Equal("de", loaded.TargetLanguage);
        Assert.Equal(5000, loaded.BridgePort);
        Assert.Equal(250, loaded.CaptureIntervalMs);
        Assert.Equal("Both", loaded.TextSourceMode);
        Assert.Equal("PrimaryMonitor", loaded.CaptureScope);
        Assert.False(loaded.Enabled);
    }

    [Fact]
    public void Load_MissingOrCorruptFile_ReturnsDefaults()
    {
        using var dir = new TempDir();
        Assert.Equal(47613, AppSettings.Load(dir.File("missing.json")).BridgePort);

        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(dir.File("bad.json"), "{ not json");
        Assert.Equal("zh", AppSettings.Load(dir.File("bad.json")).SourceLanguage);

        File.WriteAllText(dir.File("partial.json"), "{\"TargetLanguage\":\"fr\",\"SourceLanguage\":null,\"BridgePort\":-5}");
        var partial = AppSettings.Load(dir.File("partial.json"));
        Assert.Equal("fr", partial.TargetLanguage);
        Assert.Equal("zh", partial.SourceLanguage);
        Assert.Equal(47613, partial.BridgePort);
    }
}
