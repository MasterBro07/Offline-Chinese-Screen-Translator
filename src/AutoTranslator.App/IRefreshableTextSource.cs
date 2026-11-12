namespace AutoTranslator.Desktop;

/// <summary>Extras the app needs from its text sources beyond <c>ITextSource</c>.</summary>
internal interface IRefreshableTextSource
{
    /// <summary>
    /// Forget the change-detection state so that the next capture returns a full result
    /// even when the screen is unchanged (used when the translator becomes ready).
    /// </summary>
    void Invalidate();

    /// <summary>German warning for the tray (e.g. missing OCR language pack), or null.</summary>
    string? Warning { get; }
}
