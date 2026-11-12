namespace AutoTranslator.Core;

public sealed class PipelineOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(700);
    public string SourceLanguage { get; set; } = "zh";
    public string TargetLanguage { get; set; } = "en";
}
