namespace AutoTranslator.Core;

public sealed class PipelineOptions
{
    public TimeSpan Interval { get; set; } = TimeSpan.FromMilliseconds(700);
    public string SourceLanguage { get; set; } = "zh";
    public string TargetLanguage { get; set; } = "en";
    /// <summary>Merge OCR lines that belong to one paragraph before translating.</summary>
    public bool MergeLines { get; set; } = true;
}
