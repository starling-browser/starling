namespace Starling.Css.FontFace;

/// <summary>
/// A set of Unicode codepoint ranges declared by a <c>@font-face</c>'s
/// <c>unicode-range</c> descriptor. Ranges are stored sorted and merged so
/// <see cref="Contains(int)"/> is O(log n). For example, the descriptor
/// <c>unicode-range: U+00-FF, U+2000-20FF, U+2200-22FF</c> becomes three
/// disjoint intervals.
/// </summary>
public sealed class UnicodeRangeSet
{
    private readonly (int Start, int End)[] _ranges;

    /// <summary>Range set that covers every assignable codepoint.</summary>
    public static readonly UnicodeRangeSet Full = new(new[] { (0, 0x10FFFF) });

    public UnicodeRangeSet(IReadOnlyList<(int Start, int End)> ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        var sorted = ranges.OrderBy(r => r.Start).ToArray();

        // Merge overlapping or adjacent ranges so Contains() can binary-search
        // without worrying about duplicates.
        var merged = new List<(int Start, int End)>();
        foreach (var r in sorted)
        {
            var start = Math.Max(0, r.Start);
            var end = Math.Min(0x10FFFF, r.End);
            if (start > end)
            {
                continue;
            }

            if (merged.Count > 0 && start <= merged[^1].End + 1)
            {
                merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
            }
            else
            {
                merged.Add((start, end));
            }
        }
        _ranges = merged.ToArray();
    }

    private UnicodeRangeSet((int, int)[] ranges) { _ranges = ranges; }

    public bool Contains(int codepoint)
    {
        // Binary search over disjoint ranges sorted by Start.
        var lo = 0;
        var hi = _ranges.Length - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var r = _ranges[mid];
            if (codepoint < r.Start)
            {
                hi = mid - 1;
            }
            else if (codepoint > r.End)
            {
                lo = mid + 1;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// True if every codepoint scalar in <paramref name="text"/> falls inside
    /// the set. Used by the font resolver to skip subset web fonts whose
    /// declared range doesn't include the codepoints being rendered.
    /// </summary>
    public bool CoversAll(ReadOnlySpan<char> text)
    {
        var i = 0;
        while (i < text.Length)
        {
            int cp;
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                i += 2;
            }
            else
            {
                cp = text[i];
                i++;
            }
            if (!Contains(cp))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Exposes the merged disjoint intervals (test/debug surface).</summary>
    public IReadOnlyList<(int Start, int End)> Ranges => _ranges;
}
