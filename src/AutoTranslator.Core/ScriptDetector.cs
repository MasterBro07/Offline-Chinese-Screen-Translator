using System.Text;

namespace AutoTranslator.Core;

/// <summary>Detects Han (CJK ideograph) characters, including the supplementary-plane extensions.</summary>
public static class ScriptDetector
{
    /// <summary>True when the text contains at least one CJK ideograph.</summary>
    public static bool ContainsHan(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsHan(rune.Value)) return true;
        }
        return false;
    }

    /// <summary>Han characters divided by all non-whitespace characters (0 for empty text).</summary>
    public static double HanRatio(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int han = 0, total = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) continue;
            total++;
            if (IsHan(rune.Value)) han++;
        }
        return total == 0 ? 0 : (double)han / total;
    }

    /// <summary>True for code points in the CJK ideograph blocks (not kana, Hangul or CJK punctuation).</summary>
    public static bool IsHan(int codePoint) => codePoint switch
    {
        0x3007 => true,                        // ideographic number zero
        >= 0x3400 and <= 0x4DBF => true,       // Extension A
        >= 0x4E00 and <= 0x9FFF => true,       // Unified Ideographs
        >= 0xF900 and <= 0xFAFF => true,       // Compatibility Ideographs
        >= 0x20000 and <= 0x2A6DF => true,     // Extension B
        >= 0x2A700 and <= 0x2EE5F => true,     // Extensions C-F, I
        >= 0x2F800 and <= 0x2FA1F => true,     // Compatibility Supplement
        >= 0x30000 and <= 0x323AF => true,     // Extensions G, H
        _ => false,
    };
}
