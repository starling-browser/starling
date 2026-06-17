using System.Globalization;
using System.Text;

namespace Starling.Gui;

/// <summary>
/// Sub-fragment selection. A caret is a (fragment, character-offset) pair —
/// <c>CharOffset == 0</c> is before the first char, <c>CharOffset == Text.Length</c>
/// is after the last. The model maps pointer coordinates onto carets, produces
/// the per-fragment highlight rectangles for the painter, and extracts the
/// selected substring for the clipboard. Pure logic so it is unit-testable
/// without an Avalonia control.
/// <para>
/// Carets produced by this model always land on an extended grapheme cluster
/// boundary (UAX #29, via <see cref="StringInfo.GetTextElementEnumerator(string)"/>),
/// so selection can't split a surrogate pair, combining mark, emoji ZWJ
/// sequence or regional indicator pair. Downstream consumers slice
/// <c>Text</c> by these UTF-16 offsets safely.
/// </para>
/// </summary>
public static class SelectionModel
{
    /// <summary>
    /// A caret position inside the placed-fragment list. <c>CharOffset</c> is
    /// a UTF-16 code-unit index into the fragment's <c>Text</c>; the producers
    /// in this class guarantee it sits on a grapheme cluster boundary.
    /// </summary>
    public readonly record struct Caret(int FragmentIndex, int CharOffset)
    {
        public static readonly Caret None = new(-1, 0);
        public bool IsValid => FragmentIndex >= 0;
    }

    /// <summary>An ordered (start ≤ end) selection range.</summary>
    public readonly record struct Range(Caret Start, Caret End)
    {
        public bool IsEmpty => Start == End;
    }

    /// <summary>A rectangle to paint as part of the selection highlight.</summary>
    public readonly record struct HighlightRect(double X, double Y, double Width, double Height);

    /// <summary>
    /// Returns the caret nearest to (<paramref name="x"/>, <paramref name="y"/>).
    /// Fragments on the same line as the point are preferred; among those, the
    /// fragment under the pointer wins, otherwise the closest by horizontal
    /// distance. Once a fragment is chosen, the character offset is the one
    /// whose mid-glyph X is nearest <paramref name="x"/> (browser caret rule).
    /// </summary>
    public static Caret CaretFromPoint(
        IReadOnlyList<BoxHitTester.PlacedFragment> fragments, double x, double y)
    {
        if (fragments.Count == 0)
        {
            return Caret.None;
        }

        var best = -1;
        var bestPenalty = double.MaxValue;
        for (var i = 0; i < fragments.Count; i++)
        {
            var f = fragments[i];
            var dy = 0.0;
            if (y < f.Y)
            {
                dy = f.Y - y;
            }
            else if (y >= f.Y + f.Height)
            {
                dy = y - (f.Y + f.Height) + 1;
            }

            var dx = 0.0;
            if (x < f.X)
            {
                dx = f.X - x;
            }
            else if (x > f.X + f.Width)
            {
                dx = x - (f.X + f.Width);
            }

            // A vertical mismatch outweighs any horizontal distance so the
            // caret stays on whatever line the user is dragging across.
            var penalty = (dy * 10000) + dx;
            if (penalty < bestPenalty) { bestPenalty = penalty; best = i; }
        }

        var frag = fragments[best];
        return new Caret(best, CharOffsetAtX(frag, x - frag.X));
    }

    /// <summary>Returns the canonical (start ≤ end) ordering of two carets.</summary>
    public static Range Order(Caret a, Caret b)
    {
        if (!a.IsValid || !b.IsValid)
        {
            return new Range(Caret.None, Caret.None);
        }

        if (a.FragmentIndex < b.FragmentIndex)
        {
            return new Range(a, b);
        }

        if (a.FragmentIndex > b.FragmentIndex)
        {
            return new Range(b, a);
        }

        return a.CharOffset <= b.CharOffset ? new Range(a, b) : new Range(b, a);
    }

    /// <summary>
    /// Produces the highlight rectangles for <paramref name="range"/>: a
    /// possibly-sliced rect at the start fragment, full-width rects for any
    /// intermediate fragments, and a possibly-sliced rect at the end fragment.
    /// </summary>
    public static List<HighlightRect> RectsFor(
        IReadOnlyList<BoxHitTester.PlacedFragment> fragments, Range range)
    {
        var rects = new List<HighlightRect>();
        if (range.IsEmpty || !range.Start.IsValid)
        {
            return rects;
        }

        for (var i = range.Start.FragmentIndex; i <= range.End.FragmentIndex; i++)
        {
            var f = fragments[i];
            var startChar = i == range.Start.FragmentIndex ? range.Start.CharOffset : 0;
            var endChar = i == range.End.FragmentIndex ? range.End.CharOffset : f.Text.Length;
            if (startChar >= endChar)
            {
                continue;
            }

            var (left, right) = SubFragmentBounds(f, startChar, endChar);
            rects.Add(new HighlightRect(f.X + left, f.Y, right - left, f.Height));
        }
        return rects;
    }

    /// <summary>
    /// Extracts the selected substring. A soft space is inserted between two
    /// non-whitespace fragments that fall on different lines but have no
    /// explicit whitespace fragment between them — that happens when the
    /// inline formatter drops a space at a line wrap.
    /// </summary>
    public static string TextFor(
        IReadOnlyList<BoxHitTester.PlacedFragment> fragments, Range range)
    {
        if (range.IsEmpty || !range.Start.IsValid)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        var prevY = double.NaN;
        var prevWasWord = false;

        for (var i = range.Start.FragmentIndex; i <= range.End.FragmentIndex; i++)
        {
            var f = fragments[i];
            var startChar = i == range.Start.FragmentIndex ? range.Start.CharOffset : 0;
            var endChar = i == range.End.FragmentIndex ? range.End.CharOffset : f.Text.Length;
            if (startChar >= endChar)
            {
                continue;
            }

            var thisIsWord = !string.IsNullOrWhiteSpace(f.Text);
            if (sb.Length > 0 && !double.IsNaN(prevY)
                && f.Y != prevY && prevWasWord && thisIsWord)
            {
                // Wrap boundary with no preserved whitespace fragment.
                sb.Append(' ');
            }

            sb.Append(f.Text, startChar, endChar - startChar);
            prevY = f.Y;
            prevWasWord = thisIsWord;
        }
        return sb.ToString();
    }

    private static int CharOffsetAtX(BoxHitTester.PlacedFragment f, double localX)
    {
        if (f.Text.Length == 0 || f.Width <= 0)
        {
            return 0;
        }

        if (localX <= 0)
        {
            return 0;
        }

        if (localX >= f.Width)
        {
            return f.Text.Length;
        }

        var boundaries = GraphemeBoundaries(f.Text);
        var clusterCount = boundaries.Length - 1;
        var shaped = f.Shaped;
        var glyphsAlignToChars = shaped is not null && shaped.Glyphs.Length == f.Text.Length;

        // Walk cluster midpoints. Snap to the boundary whose midpoint is
        // closest left of localX — this is the standard browser caret rule
        // and keeps the model from ever producing an offset inside a
        // surrogate pair or combining sequence.
        for (var i = 0; i < clusterCount; i++)
        {
            var left = XForBoundary(f, boundaries, i, glyphsAlignToChars);
            var right = XForBoundary(f, boundaries, i + 1, glyphsAlignToChars);
            var mid = (left + right) / 2.0;
            if (localX < mid)
            {
                return boundaries[i];
            }
        }
        return f.Text.Length;
    }

    private static (double Left, double Right) SubFragmentBounds(
        BoxHitTester.PlacedFragment f, int startChar, int endChar)
    {
        if (f.Text.Length == 0 || f.Width <= 0)
        {
            return (0, 0);
        }

        var shaped = f.Shaped;
        if (shaped is not null && shaped.Glyphs.Length == f.Text.Length)
        {
            // Under the 1:1 glyph/char assumption a UTF-16 offset maps
            // directly to a glyph index, so the existing arithmetic works
            // for any code-unit offset — including the grapheme-aligned
            // ones produced by CharOffsetAtX.
            var glyphs = shaped.Glyphs;
            var left = startChar < glyphs.Length ? (double)glyphs[startChar].X : f.Width;
            var right = endChar < glyphs.Length ? (double)glyphs[endChar].X : f.Width;
            return (left, right);
        }

        // Uniform fallback: distribute width by cluster, not by code unit,
        // so mixed ASCII/emoji text gets a visually proportional slice.
        var boundaries = GraphemeBoundaries(f.Text);
        var clusterCount = boundaries.Length - 1;
        if (clusterCount == 0)
        {
            return (0, 0);
        }

        var per = f.Width / clusterCount;
        return (ClusterIndexOf(boundaries, startChar) * per,
                ClusterIndexOf(boundaries, endChar) * per);
    }

    /// <summary>
    /// Returns the UTF-16 offsets of each grapheme cluster start, with
    /// <c>text.Length</c> appended as a trailing sentinel. So <c>""</c> →
    /// <c>[0]</c>, <c>"a"</c> → <c>[0,1]</c>, <c>"😀"</c> → <c>[0,2]</c>.
    /// Built per call — small fragment text, not a hot enough path to memo.
    /// </summary>
    private static int[] GraphemeBoundaries(string text)
    {
        if (text.Length == 0)
        {
            return [0];
        }

        var bounds = new List<int>(text.Length + 1);
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            bounds.Add(e.ElementIndex);
        }

        bounds.Add(text.Length);
        return [.. bounds];
    }

    /// <summary>
    /// Pen-X (relative to the fragment origin) for the boundary at index
    /// <paramref name="boundaryIndex"/> in <paramref name="boundaries"/>.
    /// </summary>
    private static double XForBoundary(
        BoxHitTester.PlacedFragment f, int[] boundaries, int boundaryIndex, bool glyphsAlignToChars)
    {
        var codeUnit = boundaries[boundaryIndex];
        if (glyphsAlignToChars)
        {
            var glyphs = f.Shaped!.Glyphs;
            return codeUnit < glyphs.Length ? glyphs[codeUnit].X : f.Width;
        }
        var clusterCount = boundaries.Length - 1;
        return clusterCount == 0 ? 0 : (boundaryIndex / (double)clusterCount) * f.Width;
    }

    /// <summary>
    /// Index of <paramref name="codeUnit"/> in <paramref name="boundaries"/>.
    /// Caller has guaranteed grapheme alignment, so we expect an exact hit;
    /// if a stray offset slipped through (e.g. a defensively-constructed
    /// <c>Caret</c>) we fall back to the nearest preceding boundary so
    /// painting still produces something coherent.
    /// </summary>
    private static int ClusterIndexOf(int[] boundaries, int codeUnit)
    {
        for (var i = 0; i < boundaries.Length; i++)
        {
            if (boundaries[i] == codeUnit)
            {
                return i;
            }

            if (boundaries[i] > codeUnit)
            {
                return Math.Max(0, i - 1);
            }
        }
        return boundaries.Length - 1;
    }
}
