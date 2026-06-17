using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Spec;
namespace Starling.Layout.Tests;

[TestClass]
public sealed class LayoutEngineTests
{
    private static LayoutEngine NewEngine() => new(new StyleEngine());

    private static BlockBox Layout(string html, Size viewport)
        => NewEngine().LayoutDocument(HtmlParser.Parse(html), viewport);

    [TestMethod]
    public void Document_root_has_viewport_width()
    {
        var root = Layout("<body><p>x</p></body>", new Size(800, 600));
        root.Frame.Width.Should().Be(800);
    }

    [TestMethod]
    public void Block_children_stack_vertically()
    {
        var root = Layout("""
            <body><div>a</div><div>b</div><div>c</div></body>
            """, new Size(400, 600));

        // root -> html(BlockBox) -> body(BlockBox) -> 3 divs
        var body = FindBox(root, "body");
        body.Should().NotBeNull();
        var divs = body!.Children.Where(b => b.Element?.LocalName == "div").ToList();
        divs.Should().HaveCount(3);

        // Each div's frame should advance Y monotonically.
        for (var i = 1; i < divs.Count; i++)
        {
            divs[i].Frame.Y.Should().BeGreaterThan(divs[i - 1].Frame.Y);
        }
    }

    [TestMethod]
    public void Inline_text_runs_get_wrapped_into_anonymous_block()
    {
        var root = Layout("<body><p>some words here</p></body>", new Size(800, 600));
        var p = FindBox(root, "p");
        p.Should().NotBeNull();
        // The <p>'s children should be wrapped in an anonymous block hosting the text.
        p!.Children.Should().NotBeEmpty();
        var textBox = FlattenTextBoxes(p).First();
        textBox.Fragments.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Text_wraps_when_a_line_overflows_the_container()
    {
        // Skinny viewport forces wrap.
        var root = Layout(
            "<body><p>the quick brown fox jumps over the lazy dog the quick brown fox</p></body>",
            new Size(120, 600));

        var fragments = AllFragments(root);
        fragments.Should().NotBeEmpty();
        // At least one fragment should be on a line other than y=0.
        fragments.Select(f => f.Y).Distinct().Count().Should().BeGreaterThan(1);
    }

    [TestMethod]
    public void Wide_viewport_keeps_text_on_a_single_line()
    {
        var root = Layout("<body><p>tiny line</p></body>", new Size(2000, 600));
        var fragments = AllFragments(root);
        fragments.Should().NotBeEmpty();
        fragments.Select(f => f.Y).Distinct().Should().ContainSingle();
    }

    [TestMethod]
    public void Display_none_excludes_subtree_from_box_tree()
    {
        var root = Layout(
            "<body><div style=\"display:none\">hidden</div><div>visible</div></body>",
            new Size(400, 600));

        var body = FindBox(root, "body")!;
        var divs = body.Children.Where(b => b.Element?.LocalName == "div").ToList();
        divs.Should().HaveCount(1);
        divs[0].Element!.GetAttribute("style").Should().NotContain("none");
    }

    [TestMethod]
    public void Display_contents_drops_the_box_but_keeps_descendants()
    {
        var root = Layout(
            "<body><div style=\"display:contents\"><p>inner</p></div></body>",
            new Size(400, 600));

        // The wrapping <div> should be elided; <p> should be a direct child of body.
        var body = FindBox(root, "body")!;
        body.Children.OfType<BlockBox>().Should().Contain(b => b.Element != null && b.Element.LocalName == "p");
        body.Children.OfType<BlockBox>().Should().NotContain(b => b.Element != null && b.Element.LocalName == "div");
    }

    [TestMethod]
    public void Body_height_grows_to_contain_children()
    {
        var root = Layout(
            "<body><div>a</div><div>b</div></body>",
            new Size(400, 600));

        var body = FindBox(root, "body")!;
        body.Frame.Height.Should().BeGreaterThan(0);
    }

    [TestMethod]
    [Spec("css-sizing-3", "https://drafts.csswg.org/css-sizing-3/#percentage-sizing", section: "5.1")]
    [Spec("css2", "https://www.w3.org/TR/CSS21/visudet.html#the-height-property", section: "10.5")]
    public void Percentage_height_inherits_parent_explicit_height()
    {
        // Parent has an explicit 64px height; the child's `height: 100%`
        // should resolve against that, not the viewport.
        var root = Layout(
            """<body><div style="height:64px"><div id="t" style="height:100%">x</div></div></body>""",
            new Size(400, 600));

        var inner = FindBoxById(root, "t")!;
        inner.Frame.Height.Should().BeApproximately(64, 0.5);
    }

    [TestMethod]
    [Spec("css-sizing-3", "https://drafts.csswg.org/css-sizing-3/#percentage-sizing", section: "5.1")]
    [Spec("css2", "https://www.w3.org/TR/CSS21/visudet.html#the-height-property", section: "10.5")]
    public void Percentage_height_collapses_when_parent_height_indefinite()
    {
        // The parent has no explicit height (height: auto), so per CSS 2.1
        // §10.5 the child's `height: 100%` must resolve to `auto` (content
        // height) — here, 0. Previously we used the root viewport height as
        // the basis, blowing the box out to ~600px. This is the bug that
        // mis-sized .site-logo on docs.htmlcsstoimage.com.
        var root = Layout(
            """<body><div><div id="t" style="height:100%"></div></div></body>""",
            new Size(400, 600));

        var inner = FindBoxById(root, "t")!;
        inner.Frame.Height.Should().BeApproximately(0, 0.5);
    }

    [TestMethod]
    [Spec("css-flexbox-1", "https://drafts.csswg.org/css-flexbox-1/#definite-sizes", section: "9.8")]
    public void Percentage_height_threads_through_flex_item_cross_size()
    {
        // The docs.htmlcsstoimage.com .site-logo scenario: a row-direction
        // flex container with a definite height (the navbar). Items'
        // cross size = the container's height; that cross size is a
        // definite basis, so descendants with `height: 100%` should resolve
        // against it instead of collapsing to 0.
        //
        //   navbar (display:flex, height:60px)
        //     └─ brand (height:100%)
        //         └─ logo  (height:100%, background-image)
        var root = Layout(
            """
            <body>
              <div style="display:flex; height:60px">
                <div>
                  <div id="t" style="height:100%"></div>
                </div>
              </div>
            </body>
            """,
            new Size(800, 600));

        var inner = FindBoxById(root, "t")!;
        inner.Frame.Height.Should().BeApproximately(60, 0.5);
    }

    [TestMethod]
    // Not normative — html/body height-100% is a long-standing browser quirk
    // (see CSSWG issue csswg-drafts#1108). Tagged under css2 §10.1 (initial
    // containing block) as the closest formal anchor.
    [Spec("compat-quirks", "https://github.com/w3c/csswg-drafts/issues/1108")]
    [Spec("css2", "https://www.w3.org/TR/CSS21/visudet.html#containing-block-details", section: "10.1")]
    public void Body_height_100_percent_resolves_against_viewport()
    {
        // Long-standing html/body special case: even though <html> has
        // auto height, browsers thread the viewport height down to body so
        // `body { height: 100% }` reaches the viewport.
        var root = Layout(
            """<body style="height:100%"><div></div></body>""",
            new Size(400, 600));

        var body = FindBox(root, "body")!;
        body.Frame.Height.Should().BeApproximately(600, 0.5);
    }

    [TestMethod]
    public void Margin_auto_centers_block_with_explicit_width()
    {
        // body has 8px UA margin → body content width = 800 - 16 = 784
        // div is 200px wide → slack = 584, each side margin = 292
        var root = Layout(
            "<body><div style=\"width: 200px; margin: 0 auto\">x</div></body>",
            new Size(800, 600));

        var div = FindBox(root, "div")!;
        div.Frame.X.Should().BeApproximately(292, 0.5);
        div.Frame.Width.Should().Be(200);
    }

    [TestMethod]
    public void Margin_left_auto_right_aligns_block()
    {
        // body content width = 400 - 16 = 384; div is 100px wide
        // margin-left: auto, margin-right: 0 → margin-left absorbs all slack = 284
        var root = Layout(
            "<body><div style=\"margin-left: auto; margin-right: 0; width: 100px\">x</div></body>",
            new Size(400, 600));

        var div = FindBox(root, "div")!;
        div.Frame.X.Should().BeApproximately(284, 0.5);
        div.Frame.Width.Should().Be(100);
    }

    [TestMethod]
    public void Margin_right_auto_left_aligns_block()
    {
        // margin-left: 0, margin-right: auto → div sticks to left edge of body
        var root = Layout(
            "<body><div style=\"margin-left: 0; margin-right: auto; width: 100px\">x</div></body>",
            new Size(400, 600));

        var div = FindBox(root, "div")!;
        div.Frame.X.Should().BeApproximately(0, 0.5);
        div.Frame.Width.Should().Be(100);
    }

    [TestMethod]
    public void Margin_auto_with_auto_width_resolves_to_zero()
    {
        // When width is auto, auto margins resolve to 0, so the div should
        // fill the body's content width and sit at X = 0 (no left margin).
        var root = Layout(
            "<body><div style=\"margin: 0 auto\">x</div></body>",
            new Size(400, 600));

        var div = FindBox(root, "div")!;
        div.Frame.X.Should().BeApproximately(0, 0.5);
        // div should fill body content width (400 - 16 = 384)
        div.Frame.Width.Should().BeApproximately(384, 0.5);
    }

    [TestMethod]
    public void Margin_auto_with_overflowing_width_clamps_to_left_edge()
    {
        // div is wider than container → slack is negative → both margins become 0.
        var root = Layout(
            "<body><div style=\"width: 2000px; margin: 0 auto\">x</div></body>",
            new Size(400, 600));

        var div = FindBox(root, "div")!;
        div.Frame.X.Should().BeApproximately(0, 0.5);
    }

    // -------------------------------------------------- img/svg fallback
    //
    // When an <img> can't be resolved (network failure, unsupported format,
    // data: scheme stripped) we degrade to its accessible name so the page
    // doesn't lose user-visible content. The <svg> branch is the same story
    // for inline SVG, which Starling does not yet render — google.com's logo
    // is an inline <svg aria-label="Google">, so without this fallback the
    // user sees a vanishing inline.

    [TestMethod]
    public void Unresolved_img_with_alt_renders_alt_text()
    {
        var root = Layout(
            """<body><img src="missing.png" alt="example image"></body>""",
            new Size(400, 600));
        AllText(root).Should().Contain("example image");
    }

    [TestMethod]
    public void Unresolved_img_with_no_alt_falls_back_to_aria_label()
    {
        var root = Layout(
            """<body><img src="missing.png" aria-label="logo"></body>""",
            new Size(400, 600));
        AllText(root).Should().Contain("logo");
    }

    [TestMethod]
    public void Unresolved_img_with_empty_alt_renders_nothing()
    {
        var root = Layout(
            """<body><img src="missing.png" alt=""></body>""",
            new Size(400, 600));
        AllText(root).Should().NotContain("missing");
    }

    [TestMethod]
    public void Inline_svg_with_aria_label_renders_label_as_text()
    {
        var root = Layout(
            """<body><svg aria-label="Google" width="272" height="92"><path fill="#EA4335"></path></svg></body>""",
            new Size(800, 600));
        AllText(root).Should().Contain("Google");
    }

    [TestMethod]
    public void Inline_svg_with_no_aria_label_is_empty()
    {
        var root = Layout(
            """<body><svg width="24" height="24"><rect width="24" height="24"></rect></svg></body>""",
            new Size(400, 600));
        // No text content because nothing identifies the element.
        AllText(root).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- helpers

    private static Box.Box? FindBox(Box.Box root, string localName)
    {
        if (root.Element?.LocalName == localName)
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var hit = FindBox(child, localName);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }

    private static Box.Box? FindBoxById(Box.Box root, string id)
    {
        if (root.Element?.GetAttribute("id") == id)
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var hit = FindBoxById(child, id);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }

    private static IEnumerable<TextBox> FlattenTextBoxes(Box.Box box)
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

    private static string AllText(Box.Box root)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var tb in FlattenTextBoxes(root))
        {
            sb.Append(tb.Text);
        }

        return sb.ToString();
    }

    private static List<TextFragment> AllFragments(Box.Box root)
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
}
