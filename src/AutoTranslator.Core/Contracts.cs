namespace AutoTranslator.Core;

/// <summary>Rectangle in physical screen pixels (virtual-screen coordinates).</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height);

/// <summary>A piece of text found on screen and where it is.</summary>
public sealed record TextRegion(string Text, ScreenRect Bounds);

/// <summary>A text region together with its translation, ready to be drawn.</summary>
public sealed record TranslatedRegion(string Original, string Translated, ScreenRect Bounds);

/// <summary>Translates batches of text. Implementations must be thread-safe.</summary>
public interface ITranslator
{
    /// <summary>True when the engine can translate right now.</summary>
    bool IsReady { get; }

    /// <summary>Human-readable engine state for the tray UI (German).</summary>
    string StatusText { get; }

    event EventHandler? StatusChanged;

    /// <summary>Returns one translation per input text, in the same order.</summary>
    Task<IReadOnlyList<string>> TranslateAsync(
        IReadOnlyList<string> texts, string sourceLanguage, string targetLanguage, CancellationToken ct);
}

/// <summary>Reads the text currently visible on screen (OCR, UI Automation, ...).</summary>
public interface ITextSource
{
    string Name { get; }

    /// <summary>
    /// Returns the visible text regions, or null when the screen has not changed
    /// since the previous call (the pipeline then keeps the current overlay).
    /// </summary>
    Task<IReadOnlyList<TextRegion>?> CaptureAsync(CancellationToken ct);
}

/// <summary>Draws translations over the original text. Called from a background thread.</summary>
public interface IOverlayPresenter
{
    void Show(IReadOnlyList<TranslatedRegion> regions);
    void Clear();
}
