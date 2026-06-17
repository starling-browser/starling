using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Box;
using LayoutBox = Starling.Layout.Box.Box;

namespace Starling.Gui.Tests;

/// <summary>
/// Regression coverage for the inter-word whitespace rendering bug: clicks
/// on the gap between two words inside a link must still resolve to the
/// anchor, and the selection painter must receive fragments that cover the
/// gaps so the highlight does not appear striped.
/// </summary>
[TestClass]
public sealed class BoxHitTesterTests
{
    private static BlockBox Layout(string html, Size viewport)
        => new LayoutEngine(new StyleEngine()).LayoutDocument(HtmlParser.Parse(html), viewport);

    /// <summary>
    /// The inline formatter emits a fragment for the space between two
    /// words on the same line; this is the precondition the hit-tester and
    /// selection painter rely on.
    /// </summary>
    [TestMethod]
    public void Inter_word_space_is_emitted_as_a_text_fragment()
    {
        var root = Layout("<body><p>hello world</p></body>", new Size(800, 600));
        var fragments = FlattenTextBoxes(root).SelectMany(tb => tb.Fragments).ToList();

        fragments.Select(f => f.Text).Should().Contain(" ");
        var space = fragments.First(f => f.Text == " ");
        space.Width.Should().BeGreaterThan(0, "the space fragment must have a positive advance");
    }

    [TestMethod]
    public void CollectFragments_includes_inter_word_whitespace()
    {
        var root = Layout("<body><p>hello world</p></body>", new Size(800, 600));
        var placed = BoxHitTester.CollectFragments(root);

        placed.Should().Contain(f => f.Text == "hello");
        placed.Should().Contain(f => f.Text == "world");
        placed.Should().Contain(f => f.Text == " ",
            "the selection painter needs the space fragment so its highlight rectangle is contiguous between words");
    }

    [TestMethod]
    public void CollectFragments_emits_space_fragment_between_neighbouring_words()
    {
        // Wide viewport keeps "hello world" on a single line; the gap
        // between the two word fragments must be filled by a third
        // whitespace fragment, not left as bare canvas.
        var root = Layout("<body><p>hello world</p></body>", new Size(800, 600));
        var all = BoxHitTester.CollectFragments(root);
        var hello = all.First(f => f.Text == "hello");
        var world = all.First(f => f.Text == "world");
        var space = all.First(f => f.Text == " " && f.Y == hello.Y);

        // The space fragment should sit between the two word fragments
        // with no gap on either side (within a small fp tolerance).
        space.X.Should().BeApproximately(hello.X + hello.Width, 0.5);
        (space.X + space.Width).Should().BeApproximately(world.X, 0.5);
    }

    [TestMethod]
    public void HitTest_inside_word_inside_link_returns_anchor()
    {
        // Baseline: clicking on a word of a link has always worked. Asserts
        // the hit-test wiring (the test scaffolding) is correct before we
        // start probing the inter-word gap.
        var root = Layout("<body><a href=\"https://example.org\">hello world</a></body>", new Size(800, 600));
        var placed = BoxHitTester.CollectFragments(root);
        var hello = placed.First(f => f.Text == "hello");

        var hit = BoxHitTester.HitTest(root, hello.X + (hello.Width / 2), hello.Y + (hello.Height / 2));

        hit.IsHit.Should().BeTrue();
        hit.LinkAnchor.Should().NotBeNull();
        hit.LinkAnchor!.LocalName.Should().Be("a");
    }

    [TestMethod]
    public void HitTest_on_inter_word_space_inside_link_returns_anchor()
    {
        // Regression: previously the hit-tester skipped whitespace
        // fragments, so the gap between "hello" and "world" inside an
        // <a> reported no hit and the click missed the link.
        var root = Layout("<body><a href=\"https://example.org\">hello world</a></body>", new Size(800, 600));
        var placed = BoxHitTester.CollectFragments(root);
        var space = placed.First(f => f.Text == " ");

        var hit = BoxHitTester.HitTest(root, space.X + (space.Width / 2), space.Y + (space.Height / 2));

        hit.IsHit.Should().BeTrue("a click inside the link should hit even on the inter-word gap");
        hit.LinkAnchor.Should().NotBeNull();
        hit.LinkAnchor!.LocalName.Should().Be("a");
    }

    [TestMethod]
    public void ResolveCursor_on_inter_word_space_inside_link_is_pointer()
    {
        // Regression: with the whitespace skip in place, hovering the gap
        // produced no hit and the cursor fell back to default — visually
        // the cursor flickered to an arrow between every word of a link.
        var root = Layout("<body><a href=\"https://example.org\">hello world</a></body>", new Size(800, 600));
        var placed = BoxHitTester.CollectFragments(root);
        var space = placed.First(f => f.Text == " ");

        var hit = BoxHitTester.HitTest(root, space.X + (space.Width / 2), space.Y + (space.Height / 2));

        BoxHitTester.ResolveCursor(hit).Should().Be("pointer");
    }

    [TestMethod]
    public void HitTest_on_inter_word_space_outside_link_still_hits_text()
    {
        // Outside a link, clicking the gap should still register as a
        // text-content hit (so the I-beam cursor and drag-select origin
        // work uniformly across the line).
        var root = Layout("<body><p>hello world</p></body>", new Size(800, 600));
        var placed = BoxHitTester.CollectFragments(root);
        var space = placed.First(f => f.Text == " ");

        var hit = BoxHitTester.HitTest(root, space.X + (space.Width / 2), space.Y + (space.Height / 2));

        hit.IsHit.Should().BeTrue();
        hit.LinkAnchor.Should().BeNull();
        BoxHitTester.ResolveCursor(hit).Should().Be("text");
    }

    private static IEnumerable<TextBox> FlattenTextBoxes(LayoutBox box)
    {
        if (box is TextBox tb) { yield return tb; yield break; }
        foreach (var child in box.Children)
        {
            foreach (var inner in FlattenTextBoxes(child))
            {
                yield return inner;
            }
        }
    }
}
