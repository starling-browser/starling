using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;

namespace Starling.Layout.Tests;

/// <summary>
/// CSS UI 4 §7 <c>text-overflow: ellipsis</c> and the legacy
/// <c>-webkit-line-clamp</c> pattern. Both are layout-time fragment surgery in
/// <see cref="Starling.Layout.Inline.InlineLayout"/>: the truncated line keeps
/// only fragments inside the content box plus a single U+2026 fragment that
/// fits, and a clamped box keeps exactly N line boxes.
/// </summary>
[TestClass]
public sealed class TextOverflowEllipsisTests
{
    private const string Ellipsis = "…";

    private static BlockBox Layout(string html, Size viewport)
        => new LayoutEngine(new StyleEngine()).LayoutDocument(HtmlParser.Parse(html), viewport);

    private static List<TextFragment> Fragments(Box.Box root)
    {
        var result = new List<TextFragment>();
        Recurse(root);
        return result;

        void Recurse(Box.Box b)
        {
            if (b is TextBox tb)
            {
                result.AddRange(tb.Fragments);
            }

            foreach (var c in b.Children)
            {
                Recurse(c);
            }
        }
    }

    private static int LineCount(IEnumerable<TextFragment> frags)
        => frags.Select(f => Math.Round(f.Y, 2)).Distinct().Count();

    private static Box.Box FindElement(Box.Box root, string localName)
    {
        if (root.Element is { } e && string.Equals(e.LocalName, localName, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            if (FindElement(child, localName) is { } found)
            {
                return found;
            }
        }
        return null!;
    }

    // ---- text-overflow: ellipsis (nowrap + hidden) -------------------------

    [TestMethod]
    public void Nowrap_hidden_ellipsis_truncates_and_appends_ellipsis_inside_content_box()
    {
        const double width = 120;
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"width:120px; overflow:hidden; " +
            "white-space:nowrap; text-overflow:ellipsis\">" +
            "aaaa bbbb cccc dddd eeee ffff gggg hhhh</div></body>",
            new Size(800, 600));

        var frags = Fragments(root);
        frags.Should().ContainSingle(f => f.Text == Ellipsis, "the overflowing line gains one U+2026 fragment");

        var ellipsis = frags.Single(f => f.Text == Ellipsis);
        (ellipsis.X + ellipsis.Width).Should().BeLessThanOrEqualTo(width + 0.06,
            "the ellipsis glyph must fit inside the content box");

        // Every kept fragment ends at or before the ellipsis start; nothing
        // overflows the content edge.
        foreach (var f in frags)
        {
            (f.X + f.Width).Should().BeLessThanOrEqualTo(width + 0.06,
                $"fragment '{f.Text}' must not overflow the content box");
        }

        LineCount(frags).Should().Be(1, "nowrap keeps everything on one line");
    }

    [TestMethod]
    public void Fitting_text_is_not_ellipsized()
    {
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"width:400px; overflow:hidden; " +
            "white-space:nowrap; text-overflow:ellipsis\">short</div></body>",
            new Size(800, 600));

        var frags = Fragments(root);
        frags.Should().NotContain(f => f.Text == Ellipsis, "content that fits is never ellipsized");
        frags.Should().Contain(f => f.Text == "short", "the original fragment is untouched");
    }

    [TestMethod]
    public void Overflow_visible_does_not_ellipsize()
    {
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"width:120px; " +
            "white-space:nowrap; text-overflow:ellipsis\">" +
            "aaaa bbbb cccc dddd eeee ffff gggg hhhh</div></body>",
            new Size(800, 600));

        Fragments(root).Should().NotContain(f => f.Text == Ellipsis,
            "text-overflow only applies when overflow-x is not visible");
    }

    [TestMethod]
    public void Wrapping_white_space_does_not_ellipsize()
    {
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"width:120px; overflow:hidden; " +
            "text-overflow:ellipsis\">aaaa bbbb cccc dddd eeee ffff gggg hhhh</div></body>",
            new Size(800, 600));

        Fragments(root).Should().NotContain(f => f.Text == Ellipsis,
            "wrapping white-space lays the text out on multiple lines instead");
    }

    [TestMethod]
    public void Ellipsis_recomputes_when_width_changes()
    {
        const string html =
            "<body style=\"margin:0\"><div style=\"width:{0}px; overflow:hidden; " +
            "white-space:nowrap; text-overflow:ellipsis\">" +
            "aaaa bbbb cccc dddd eeee ffff gggg hhhh</div></body>";

        var narrow = Fragments(Layout(string.Format(html, 100), new Size(800, 600)));
        var wide = Fragments(Layout(string.Format(html, 220), new Size(800, 600)));

        var narrowEllipsis = narrow.Single(f => f.Text == Ellipsis);
        var wideEllipsis = wide.Single(f => f.Text == Ellipsis);

        (narrowEllipsis.X + narrowEllipsis.Width).Should().BeLessThanOrEqualTo(100 + 0.06);
        (wideEllipsis.X + wideEllipsis.Width).Should().BeLessThanOrEqualTo(220 + 0.06);
        wideEllipsis.X.Should().BeGreaterThan(narrowEllipsis.X,
            "a wider box keeps more text before the ellipsis");

        // The wider layout keeps strictly more text.
        var narrowKept = narrow.Where(f => f.Text != Ellipsis).Sum(f => f.Text.Length);
        var wideKept = wide.Where(f => f.Text != Ellipsis).Sum(f => f.Text.Length);
        wideKept.Should().BeGreaterThan(narrowKept);
    }

    [TestMethod]
    public void Ellipsis_does_not_follow_a_trailing_space()
    {
        // Cut lands inside the space between words: the kept content must not
        // end with a whitespace fragment before the ellipsis.
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"width:104px; overflow:hidden; " +
            "white-space:nowrap; text-overflow:ellipsis; font-size:16px\">" +
            "aaaaaaaaaaaa bbbbbbbbbbbb</div></body>",
            new Size(800, 600));

        var frags = Fragments(root);
        var ellipsis = frags.Single(f => f.Text == Ellipsis);
        var lastBefore = frags
            .Where(f => f.Text != Ellipsis && f.X < ellipsis.X)
            .OrderBy(f => f.X)
            .LastOrDefault();
        if (lastBefore.Text is { Length: > 0 })
        {
            lastBefore.Text.Trim().Should().NotBeEmpty("the ellipsis hugs text, not trailing spaces");
        }
    }

    // ---- -webkit-line-clamp -------------------------------------------------

    [TestMethod]
    public void Line_clamp_two_keeps_two_lines_and_ellipsizes_the_second()
    {
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"display:-webkit-box; " +
            "-webkit-box-orient:vertical; -webkit-line-clamp:2; overflow:hidden; " +
            "width:120px; font-size:16px; line-height:20px\">" +
            "aaaa bbbb cccc dddd eeee ffff gggg hhhh iiii jjjj kkkk llll</div></body>",
            new Size(800, 600));

        var frags = Fragments(root);
        LineCount(frags).Should().Be(2, "-webkit-line-clamp:2 caps the box at two line boxes");

        var lastLineY = frags.Max(f => f.Y);
        frags.Should().ContainSingle(f => f.Text == Ellipsis, "the clamped line gains the ellipsis");
        frags.Single(f => f.Text == Ellipsis).Y.Should().Be(lastLineY, "the ellipsis sits on line 2");

        var div = FindElement(root, "div");
        div.Should().NotBeNull();
        div.Frame.Height.Should().BeApproximately(40, 0.1, "box height is exactly 2 × line-height");
    }

    [TestMethod]
    public void Line_clamp_one_keeps_a_single_line()
    {
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"display:-webkit-box; " +
            "-webkit-box-orient:vertical; -webkit-line-clamp:1; overflow:hidden; " +
            "width:120px; font-size:16px; line-height:20px\">" +
            "aaaa bbbb cccc dddd eeee ffff gggg hhhh</div></body>",
            new Size(800, 600));

        var frags = Fragments(root);
        LineCount(frags).Should().Be(1);
        frags.Should().ContainSingle(f => f.Text == Ellipsis);

        var div = FindElement(root, "div");
        div.Frame.Height.Should().BeApproximately(20, 0.1, "box height is exactly 1 × line-height");
    }

    [TestMethod]
    public void Line_clamp_larger_than_line_count_changes_nothing()
    {
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"display:-webkit-box; " +
            "-webkit-box-orient:vertical; -webkit-line-clamp:5; overflow:hidden; " +
            "width:400px; font-size:16px; line-height:20px\">aa bb</div></body>",
            new Size(800, 600));

        var frags = Fragments(root);
        frags.Should().NotContain(f => f.Text == Ellipsis, "content under the clamp limit is untouched");
        LineCount(frags).Should().Be(1);
    }

    [TestMethod]
    public void Line_clamp_without_vertical_orient_does_not_clamp()
    {
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"display:-webkit-box; " +
            "-webkit-line-clamp:1; overflow:hidden; width:120px\">" +
            "aaaa bbbb cccc dddd eeee ffff gggg hhhh</div></body>",
            new Size(800, 600));

        var frags = Fragments(root);
        frags.Should().NotContain(f => f.Text == Ellipsis,
            "the clamp pattern requires -webkit-box-orient:vertical");
        LineCount(frags).Should().BeGreaterThan(1);
    }
}
