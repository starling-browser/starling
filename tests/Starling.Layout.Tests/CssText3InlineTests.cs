using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;

namespace Starling.Layout.Tests;

/// <summary>
/// Behavioral coverage for CSS Text Module Level 3 inline properties applied by
/// <see cref="Starling.Layout.Inline.InlineLayout"/>: <c>white-space</c> (plus
/// the <c>white-space-collapse</c> + <c>text-wrap</c> longhands),
/// <c>text-transform</c>, <c>letter-spacing</c>, <c>word-spacing</c>,
/// <c>text-indent</c>, <c>tab-size</c>, <c>overflow-wrap</c>, and
/// <c>word-break</c>.
/// </summary>
[TestClass]
public sealed class CssText3InlineTests
{
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

    // ---- white-space ------------------------------------------------------

    [TestMethod]
    public void WhiteSpace_normal_collapses_runs_and_newlines_onto_one_line()
    {
        var root = Layout(
            "<body style=\"margin:0\"><p style=\"white-space:normal\">a  b\nc</p></body>",
            new Size(2000, 600));

        var frags = Fragments(root);
        LineCount(frags).Should().Be(1, "white-space:normal collapses the newline and the double space");
        // No fragment should carry two consecutive spaces — they collapsed.
        frags.Should().NotContain(f => f.Text.Contains("  "));
    }

    [TestMethod]
    public void WhiteSpace_pre_preserves_double_space_and_breaks_at_newline()
    {
        var root = Layout(
            "<body style=\"margin:0\"><p style=\"white-space:pre\">a  b\nc</p></body>",
            new Size(2000, 600));

        var frags = Fragments(root);
        // Forced break at \n → two lines.
        LineCount(frags).Should().Be(2, "white-space:pre keeps the forced newline");
        // A run of two preserved spaces survives as a space token.
        frags.Should().Contain(f => f.Text == "  ", "the double space is preserved");
    }

    [TestMethod]
    public void WhiteSpace_pre_does_not_soft_wrap()
    {
        // A long line under a tiny viewport must stay on a single line (pre
        // disables soft wrapping) — only the forced break would split it.
        var root = Layout(
            "<body style=\"margin:0\"><p style=\"white-space:pre\">aaaa bbbb cccc dddd eeee ffff</p></body>",
            new Size(40, 600));

        LineCount(Fragments(root)).Should().Be(1);
    }

    [TestMethod]
    public void WhiteSpace_pre_line_collapses_spaces_but_keeps_newlines()
    {
        var root = Layout(
            "<body style=\"margin:0\"><p style=\"white-space:pre-line\">a  b\nc</p></body>",
            new Size(2000, 600));

        var frags = Fragments(root);
        LineCount(frags).Should().Be(2, "pre-line keeps the newline");
        frags.Should().NotContain(f => f.Text.Contains("  "), "pre-line still collapses runs of spaces");
    }

    [TestMethod]
    public void WhiteSpace_nowrap_keeps_text_on_one_line()
    {
        var root = Layout(
            "<body style=\"margin:0\"><p style=\"white-space:nowrap\">the quick brown fox jumps over</p></body>",
            new Size(40, 600));

        LineCount(Fragments(root)).Should().Be(1, "nowrap suppresses soft wrapping");
    }

    [TestMethod]
    public void WhiteSpaceCollapse_preserve_longhand_preserves_double_space()
    {
        // Modern longhands without the legacy shorthand.
        var root = Layout(
            "<body style=\"margin:0\"><p style=\"white-space-collapse:preserve; text-wrap:nowrap\">a  b</p></body>",
            new Size(2000, 600));

        Fragments(root).Should().Contain(f => f.Text == "  ");
    }

    [TestMethod]
    public void TextWrap_nowrap_longhand_suppresses_soft_wrap()
    {
        var root = Layout(
            "<body style=\"margin:0\"><p style=\"text-wrap:nowrap\">the quick brown fox jumps over</p></body>",
            new Size(40, 600));

        LineCount(Fragments(root)).Should().Be(1);
    }

    [TestMethod]
    public void Pre_element_from_ua_sheet_preserves_whitespace()
    {
        // <pre> gets `white-space: pre` from the UA stylesheet; that must now
        // take effect end-to-end.
        var root = Layout(
            "<body style=\"margin:0\"><pre>a  b\nc</pre></body>",
            new Size(2000, 600));

        var frags = Fragments(root);
        LineCount(frags).Should().Be(2);
        frags.Should().Contain(f => f.Text == "  ");
    }

    // ---- text-transform ---------------------------------------------------

    [TestMethod]
    public void TextTransform_uppercase_uppercases_fragments()
    {
        var root = Layout(
            "<body style=\"margin:0\"><p style=\"text-transform:uppercase\">hello world</p></body>",
            new Size(2000, 600));

        var words = Fragments(root).Where(f => !string.IsNullOrWhiteSpace(f.Text)).Select(f => f.Text).ToList();
        words.Should().Contain("HELLO");
        words.Should().Contain("WORLD");
    }

    [TestMethod]
    public void TextTransform_lowercase_lowercases_fragments()
    {
        var root = Layout(
            "<body style=\"margin:0\"><p style=\"text-transform:lowercase\">HELLO World</p></body>",
            new Size(2000, 600));

        var words = Fragments(root).Where(f => !string.IsNullOrWhiteSpace(f.Text)).Select(f => f.Text).ToList();
        words.Should().Contain("hello");
        words.Should().Contain("world");
    }

    [TestMethod]
    public void TextTransform_capitalize_uppercases_word_starts()
    {
        var root = Layout(
            "<body style=\"margin:0\"><p style=\"text-transform:capitalize\">hello world</p></body>",
            new Size(2000, 600));

        var words = Fragments(root).Where(f => !string.IsNullOrWhiteSpace(f.Text)).Select(f => f.Text).ToList();
        words.Should().Contain("Hello");
        words.Should().Contain("World");
    }

    // ---- letter-spacing / word-spacing ------------------------------------

    [TestMethod]
    public void LetterSpacing_increases_run_width()
    {
        var baseRoot = Layout(
            "<body style=\"margin:0\"><p>aaaaaaaaaa</p></body>", new Size(2000, 600));
        var spacedRoot = Layout(
            "<body style=\"margin:0\"><p style=\"letter-spacing:5px\">aaaaaaaaaa</p></body>", new Size(2000, 600));

        var baseWidth = Fragments(baseRoot).Single(f => !string.IsNullOrWhiteSpace(f.Text)).Width;
        var spacedWidth = Fragments(spacedRoot).Single(f => !string.IsNullOrWhiteSpace(f.Text)).Width;

        // 10 letters × 5px extra = +50px.
        spacedWidth.Should().BeApproximately(baseWidth + 50, 0.5);
    }

    [TestMethod]
    public void WordSpacing_pushes_following_word_right()
    {
        var baseRoot = Layout(
            "<body style=\"margin:0\"><p>aa bb</p></body>", new Size(2000, 600));
        var spacedRoot = Layout(
            "<body style=\"margin:0\"><p style=\"word-spacing:30px\">aa bb</p></body>", new Size(2000, 600));

        double SecondWordX(BlockBox r) => Fragments(r)
            .Where(f => !string.IsNullOrWhiteSpace(f.Text))
            .OrderBy(f => f.X)
            .Last().X;

        SecondWordX(spacedRoot).Should().BeApproximately(SecondWordX(baseRoot) + 30, 0.5);
    }

    // ---- text-indent ------------------------------------------------------

    [TestMethod]
    public void TextIndent_offsets_first_line_start()
    {
        var root = Layout(
            "<body style=\"margin:0\"><p style=\"font-size:16px; text-indent:2em\">hello</p></body>",
            new Size(2000, 600));

        var first = Fragments(root).Where(f => !string.IsNullOrWhiteSpace(f.Text)).OrderBy(f => f.X).First();
        first.X.Should().BeApproximately(32, 0.5, "2em at 16px font-size = 32px");
    }

    [TestMethod]
    public void TextIndent_only_indents_the_first_line()
    {
        var root = Layout(
            "<body style=\"margin:0\"><p style=\"font-size:16px; text-indent:40px\">alpha beta gamma delta epsilon zeta</p></body>",
            new Size(120, 600));

        var frags = Fragments(root).Where(f => !string.IsNullOrWhiteSpace(f.Text)).ToList();
        LineCount(frags).Should().BeGreaterThan(1);
        var firstLineY = frags.Min(f => f.Y);
        var firstLineMinX = frags.Where(f => Math.Abs(f.Y - firstLineY) < 0.01).Min(f => f.X);
        var laterMinX = frags.Where(f => f.Y > firstLineY + 0.01).Min(f => f.X);
        firstLineMinX.Should().BeApproximately(40, 0.5);
        laterMinX.Should().BeLessThan(firstLineMinX);
    }

    // ---- tab-size ---------------------------------------------------------

    [TestMethod]
    public void TabSize_expands_tab_in_preserved_whitespace()
    {
        // Smaller tab-size → the word after the tab starts further left.
        var small = Layout(
            "<body style=\"margin:0\"><pre style=\"tab-size:2\">a\tb</pre></body>", new Size(2000, 600));
        var large = Layout(
            "<body style=\"margin:0\"><pre style=\"tab-size:16\">a\tb</pre></body>", new Size(2000, 600));

        double LastX(BlockBox r) => Fragments(r)
            .Where(f => !string.IsNullOrWhiteSpace(f.Text)).OrderBy(f => f.X).Last().X;

        LastX(large).Should().BeGreaterThan(LastX(small),
            "a larger tab-size pushes the post-tab content further right");
    }

    // ---- overflow-wrap / word-break --------------------------------------

    [TestMethod]
    public void OverflowWrap_anywhere_breaks_a_long_token()
    {
        var root = Layout(
            "<body style=\"margin:0\"><p style=\"overflow-wrap:anywhere\">aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</p></body>",
            new Size(40, 600));

        LineCount(Fragments(root)).Should().BeGreaterThan(1, "an unbreakable token must break under overflow-wrap:anywhere");
    }

    [TestMethod]
    public void WordBreak_break_all_breaks_a_long_token()
    {
        var root = Layout(
            "<body style=\"margin:0\"><p style=\"word-break:break-all\">aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</p></body>",
            new Size(40, 600));

        LineCount(Fragments(root)).Should().BeGreaterThan(1);
    }

    [TestMethod]
    public void Default_does_not_break_a_long_token()
    {
        // Initial overflow-wrap:normal / word-break:normal keeps the token whole
        // even when it overflows (it overflows the line rather than breaking).
        var root = Layout(
            "<body style=\"margin:0\"><p>aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</p></body>",
            new Size(40, 600));

        LineCount(Fragments(root)).Should().Be(1);
    }
}
