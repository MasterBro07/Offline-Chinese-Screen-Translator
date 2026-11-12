using System.Text.Json;

namespace AutoTranslator.Core;

/// <summary>User settings, stored as settings.json in <see cref="DefaultDirectory"/>.</summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string SourceLanguage { get; set; } = "zh";
    public string TargetLanguage { get; set; } = "en";
    public int BridgePort { get; set; } = 47613;
    public int CaptureIntervalMs { get; set; } = 700;
    /// <summary>"Ocr" | "UiAutomation" | "Both"</summary>
    public string TextSourceMode { get; set; } = "Ocr";
    /// <summary>"ForegroundWindow" | "PrimaryMonitor"</summary>
    public string CaptureScope { get; set; } = "ForegroundWindow";
    public bool Enabled { get; set; } = true;

    /// <summary>"Chrome" (on-device, default) | "Claude" (Anthropic API, Chrome stays the fallback)</summary>
    public string Engine { get; set; } = "Chrome";
    public string ClaudeModel { get; set; } = "claude-haiku-5-5";
    /// <summary>Source characters per day sent to Claude; 0 = unlimited.</summary>
    public long ClaudeDailyCharLimit { get; set; } = 300000;
    /// <summary>Merge OCR lines of one paragraph before translating.</summary>
    public bool MergeLines { get; set; } = true;
    /// <summary>Extension origin allowed to spend API credit through the bridge; null = pin the first one.</summary>
    public string? TrustedExtensionOrigin { get; set; }

    /// <summary>%AppData%/AutoTranslator</summary>
    public static string DefaultDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoTranslator");

    /// <summary>Full path of the default settings file.</summary>
    public static string DefaultPath => Path.Combine(DefaultDirectory, "settings.json");

    /// <summary>Loads the settings; returns defaults when the file is missing or invalid. Never throws.</summary>
    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            if (!File.Exists(path)) return new AppSettings();
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOptions);
            return Sanitize(settings ?? new AppSettings());
        }
        catch
        {
            return new AppSettings();
        }
    }

    /// <summary>Writes the settings, creating the directory when needed.</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    // A hand-edited file may contain nulls or nonsense; fall back to defaults per field.
    private static AppSettings Sanitize(AppSettings s)
    {
        var d = new AppSettings();
        if (string.IsNullOrWhiteSpace(s.SourceLanguage)) s.SourceLanguage = d.SourceLanguage;
        if (string.IsNullOrWhiteSpace(s.TargetLanguage)) s.TargetLanguage = d.TargetLanguage;
        if (string.IsNullOrWhiteSpace(s.TextSourceMode)) s.TextSourceMode = d.TextSourceMode;
        if (string.IsNullOrWhiteSpace(s.CaptureScope)) s.CaptureScope = d.CaptureScope;
        if (s.BridgePort is < 1 or > 65535) s.BridgePort = d.BridgePort;
        if (s.CaptureIntervalMs < 50) s.CaptureIntervalMs = d.CaptureIntervalMs;
        s.Engine = string.Equals(s.Engine?.Trim(), "Claude", StringComparison.OrdinalIgnoreCase) ? "Claude" : "Chrome";
        s.ClaudeModel = string.IsNullOrWhiteSpace(s.ClaudeModel) ? d.ClaudeModel : s.ClaudeModel.Trim();
        if (s.ClaudeDailyCharLimit < 0) s.ClaudeDailyCharLimit = d.ClaudeDailyCharLimit;
        s.TrustedExtensionOrigin = string.IsNullOrWhiteSpace(s.TrustedExtensionOrigin) ? null : s.TrustedExtensionOrigin.Trim();
        return s;
    }
}
