using AutoTranslator.Core;

namespace AutoTranslator.Core.Tests;

public class ScriptDetectorTests
{
    [Theory]
    [InlineData("你好", true)]
    [InlineData("Hello 世界!", true)]
    [InlineData("㐀", true)]                 // Extension A
    [InlineData("\U00020000", true)]         // Extension B (surrogate pair)
    [InlineData("\U0002B740", true)]         // Extension D
    [InlineData("\U00030000", true)]         // Extension G
    [InlineData("豈", true)]                 // compatibility ideograph
    [InlineData("Hello world", false)]
    [InlineData("こんにちは", false)]         // hiragana
    [InlineData("カタカナ", false)]           // katakana
    [InlineData("한국어", false)]             // Hangul
    [InlineData("。、「」", false)]           // CJK punctuation
    [InlineData("😀", false)]                // other surrogate pair
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ContainsHan(string? text, bool expected) =>
        Assert.Equal(expected, ScriptDetector.ContainsHan(text));

    [Fact]
    public void HanRatio_CountsCodePointsAndIgnoresWhitespace()
    {
        Assert.Equal(0, ScriptDetector.HanRatio(null));
        Assert.Equal(0, ScriptDetector.HanRatio("   "));
        Assert.Equal(1, ScriptDetector.HanRatio("你 好\n"));
        Assert.Equal(0.5, ScriptDetector.HanRatio("你好 ab"));
        Assert.Equal(0.5, ScriptDetector.HanRatio("\U00020000a")); // one supplementary ideograph + one letter
        Assert.Equal(0, ScriptDetector.HanRatio("abc"));
    }
}
