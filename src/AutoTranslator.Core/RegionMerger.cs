namespace AutoTranslator.Core;

/// <summary>
/// Joins OCR line regions that belong to one paragraph, so the translator sees whole sentences
/// instead of fragments cut at the line wrap. Deliberately conservative: stacked short labels
/// (menus, lists) stay separate.
/// </summary>
public static class RegionMerger
{
    private const double MaxHeightDifference = 0.30;   // relative to the taller line
    private const double MaxGap = 0.70;                // vertical gap, in line heights
    private const double MaxOverlap = 0.30;            // lines may overlap vertically by this much
    private const double FullLineRatio = 0.70;         // upper line vs. widest line of the paragraph
    private const double MinFullLineAspect = 6.0;      // a wrapped line is at least ~6 glyphs wide
    private const int MinFullLineChars = 6;
    private const double StrongOverlap = 0.80;         // share of the narrower line
    private const double EqualEdgeTolerance = 0.30;    // in line heights

    private sealed class Group
    {
        public Group(TextRegion first)
        {
            Lines.Add(first);
            MaxWidth = first.Bounds.Width;
        }

        public List<TextRegion> Lines { get; } = new();
        public int MaxWidth { get; set; }
        public TextRegion Last => Lines[^1];
    }

    /// <summary>
    /// Returns the regions ordered top-to-bottom, left-to-right, with consecutive lines of one
    /// paragraph merged into a single region (text joined, bounds = union).
    /// </summary>
    public static IReadOnlyList<TextRegion> MergeLines(IReadOnlyList<TextRegion> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0) return Array.Empty<TextRegion>();

        // OrderBy is stable, so equal positions keep their input order.
        var ordered = lines
            .Where(l => l is not null)
            .OrderBy(l => l.Bounds.Y)
            .ThenBy(l => l.Bounds.X)
            .ToList();

        var groups = new List<Group>();
        foreach (var line in ordered)
        {
            Group? target = null;
            foreach (var group in groups)
            {
                if (!Continues(group, line)) continue;
                if (target is not null)
                {
                    // Two paragraphs claim the line (columns): ambiguous, so leave it alone.
                    target = null;
                    break;
                }
                target = group;
            }

            if (target is null)
            {
                groups.Add(new Group(line));
            }
            else
            {
                target.Lines.Add(line);
                target.MaxWidth = Math.Max(target.MaxWidth, line.Bounds.Width);
            }
        }

        var result = new List<TextRegion>(groups.Count);
        foreach (var group in groups)
        {
            result.Add(group.Lines.Count == 1 ? group.Lines[0] : Merge(group.Lines));
        }
        return result;
    }

    /// <summary>True when <paramref name="lower"/> is the next line of the paragraph in <paramref name="group"/>.</summary>
    private static bool Continues(Group group, TextRegion lower)
    {
        var upper = group.Last;
        var u = upper.Bounds;
        var l = lower.Bounds;
        if (u.Height <= 0 || l.Height <= 0 || u.Width <= 0 || l.Width <= 0) return false;

        var upperText = upper.Text?.Trim() ?? "";
        var lowerText = lower.Text?.Trim() ?? "";
        if (upperText.Length == 0 || lowerText.Length == 0) return false;

        // Same font size.
        double taller = Math.Max(u.Height, l.Height);
        if (Math.Abs(u.Height - l.Height) > MaxHeightDifference * taller) return false;

        // Directly below, at normal line spacing.
        double lineHeight = (u.Height + l.Height) / 2.0;
        double gap = l.Y - (u.Y + u.Height);
        if (gap >= MaxGap * lineHeight || gap < -MaxOverlap * lineHeight) return false;

        // Same column: left-aligned, or one line (almost) covering the other, e.g. centred text.
        double leftDelta = Math.Abs(u.X - l.X);
        double overlap = Math.Min(u.X + u.Width, l.X + l.Width) - Math.Max(u.X, l.X);
        bool leftAligned = leftDelta <= lineHeight;
        bool stronglyOverlapping = overlap >= StrongOverlap * Math.Min(u.Width, l.Width);
        if (!leftAligned && !stronglyOverlapping) return false;

        var last = upperText[^1];
        if (IsTerminalPunctuation(last)) return false; // the sentence ended; no need to join

        // A line ending in a comma obviously continues below, however short it is.
        if (IsContinuationPunctuation(last) && leftDelta <= EqualEdgeTolerance * lineHeight) return true;

        // Otherwise the upper line must look wrapped: long, and about as wide as the paragraph.
        double widest = Math.Max(group.MaxWidth, l.Width);
        if (u.Width < FullLineRatio * widest) return false;
        if (u.Width < MinFullLineAspect * u.Height) return false;
        return CountNonWhitespace(upperText) >= MinFullLineChars;
    }

    private static TextRegion Merge(List<TextRegion> lines)
    {
        var text = new System.Text.StringBuilder();
        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
        foreach (var line in lines)
        {
            var part = line.Text.Trim();
            if (text.Length > 0 && !(IsCjk(text[^1]) && IsCjk(part[0]))) text.Append(' ');
            text.Append(part);

            var b = line.Bounds;
            left = Math.Min(left, b.X);
            top = Math.Min(top, b.Y);
            right = Math.Max(right, b.X + b.Width);
            bottom = Math.Max(bottom, b.Y + b.Height);
        }
        return new TextRegion(text.ToString(), new ScreenRect(left, top, right - left, bottom - top));
    }

    private static int CountNonWhitespace(string text)
    {
        int count = 0;
        foreach (var c in text)
        {
            if (!char.IsWhiteSpace(c)) count++;
        }
        return count;
    }

    private static bool IsTerminalPunctuation(char c) =>
        c is '。' or '！' or '？' or '.' or '!' or '?' or '…' or '；' or ';';

    private static bool IsContinuationPunctuation(char c) =>
        c is '，' or '、' or ',' or '：' or ':';

    /// <summary>Scripts written without spaces between words, including their punctuation.</summary>
    private static bool IsCjk(char c) =>
        c is >= '⺀' and <= '鿿'      // radicals, CJK punctuation, kana, ideographs
            or >= '豈' and <= '﫿'    // compatibility ideographs
            or >= '︰' and <= '﹏'    // compatibility forms
            or >= '＀' and <= '￯'    // full-width forms
        || char.IsSurrogate(c);               // supplementary ideographs
}
