using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Spec;
namespace Starling.Layout.Tests.Flex;

/// <summary>
/// CSS Flexbox 1 flex polish: per-item <c>align-self</c> (§8.3),
/// <c>align-content</c> line distribution + line stretching (§8.4), column
/// wrap (§9.3), and first-baseline alignment in row containers (§8.5).
/// Item frames are asserted in the flex container's content-box coordinates
/// (the container sits inside <c>&lt;body&gt;</c> with the UA's 8px margin).
/// </summary>
[TestClass]
[Spec("css-flexbox-1", "https://drafts.csswg.org/css-flexbox-1/#alignment", section: "8")]
public sealed class FlexAlignmentTests
{
    private static LayoutEngine NewEngine() => new(new StyleEngine());

    private static BlockBox Layout(string html, Size viewport)
        => NewEngine().LayoutDocument(HtmlParser.Parse(html), viewport);

    // ---------- align-self ----------

    [TestMethod]
    public void Align_self_overrides_container_align_items_per_item()
    {
        // The container packs items at flex-start; each item picks its own
        // alignment, including a stretch override on an auto-height item.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:100px; align-items:flex-start">
              <div id="a" style="width:50px; height:40px"></div>
              <div id="b" style="width:50px; height:40px; align-self:center"></div>
              <div id="d" style="width:50px; height:40px; align-self:flex-end"></div>
              <div id="e" style="width:50px; align-self:stretch"></div>
            </div></body>
            """, new Size(800, 600));

        ById(root, "a").Frame.Y.Should().BeApproximately(0, 0.5);
        ById(root, "b").Frame.Y.Should().BeApproximately(30, 0.5);
        ById(root, "d").Frame.Y.Should().BeApproximately(60, 0.5);
        ById(root, "e").Frame.Y.Should().BeApproximately(0, 0.5);
        ById(root, "e").Frame.Height.Should().BeApproximately(100, 0.5);
    }

    [TestMethod]
    public void Align_self_center_overrides_the_containers_default_stretch()
    {
        // align-items is the initial `stretch`: the plain auto-height item
        // fills the 100px line, while align-self:center keeps the second
        // item's 40px height and centers it instead.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:100px">
              <div id="a" style="width:50px"></div>
              <div id="b" style="width:50px; height:40px; align-self:center"></div>
            </div></body>
            """, new Size(800, 600));

        ById(root, "a").Frame.Height.Should().BeApproximately(100, 0.5);
        ById(root, "b").Frame.Y.Should().BeApproximately(30, 0.5);
        ById(root, "b").Frame.Height.Should().BeApproximately(40, 0.5);
    }

    // ---------- align-content ----------

    [TestMethod]
    public void Align_content_space_between_separates_three_lines()
    {
        // 300px-wide container, 150px items → 2 per line, 3 lines of 40px.
        // 300px height - 120px of lines = 180px free → 90px between adjacent
        // lines, none at the edges.
        var root = Layout("""
            <body><div id="c" style="display:flex; flex-wrap:wrap; width:300px; height:300px; align-content:space-between">
              <div style="width:150px; height:40px"></div><div style="width:150px; height:40px"></div>
              <div style="width:150px; height:40px"></div><div style="width:150px; height:40px"></div>
              <div style="width:150px; height:40px"></div><div style="width:150px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items[0].Frame.Y.Should().BeApproximately(0, 0.5);
        items[1].Frame.Y.Should().BeApproximately(0, 0.5);
        items[2].Frame.Y.Should().BeApproximately(130, 0.5);
        items[4].Frame.Y.Should().BeApproximately(260, 0.5);
    }

    [TestMethod]
    public void Align_content_stretch_grows_each_lines_cross_size()
    {
        // Two natural-40px lines in a 300px-tall container: stretch (the
        // initial `normal`) splits the 220px free space equally, so each line
        // becomes 150px tall and line 2 starts at y=150. The auto-height item
        // stretches to its grown line.
        var root = Layout("""
            <body><div id="c" style="display:flex; flex-wrap:wrap; width:300px; height:300px">
              <div id="a" style="width:150px; height:40px"></div><div id="b" style="width:150px"></div>
              <div id="d" style="width:150px; height:40px"></div><div id="e" style="width:150px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        ById(root, "a").Frame.Y.Should().BeApproximately(0, 0.5);
        ById(root, "b").Frame.Height.Should().BeApproximately(150, 0.5);
        ById(root, "d").Frame.Y.Should().BeApproximately(150, 0.5);
        ById(root, "e").Frame.Y.Should().BeApproximately(150, 0.5);
    }

    [TestMethod]
    public void Align_content_center_centers_the_line_block()
    {
        // Two 40px lines = 80; free = 300 - 80 = 220 → 110px leading.
        var root = Layout("""
            <body><div id="c" style="display:flex; flex-wrap:wrap; width:300px; height:300px; align-content:center">
              <div style="width:150px; height:40px"></div><div style="width:150px; height:40px"></div>
              <div style="width:150px; height:40px"></div><div style="width:150px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items[0].Frame.Y.Should().BeApproximately(110, 0.5);
        items[2].Frame.Y.Should().BeApproximately(150, 0.5);
    }

    // ---------- column wrap ----------

    [TestMethod]
    public void Column_wrap_starts_a_second_column_at_the_first_columns_right_edge()
    {
        // 100px-tall column: two 40px items fit, the third wraps into a new
        // column. Lines stack horizontally; with align-content:flex-start the
        // first column keeps its natural 80px width, so column 2 is at x=80.
        var root = Layout("""
            <body><div id="c" style="display:flex; flex-direction:column; flex-wrap:wrap; width:300px; height:100px; align-content:flex-start">
              <div id="a" style="width:80px; height:40px"></div>
              <div id="b" style="width:80px; height:40px"></div>
              <div id="d" style="width:80px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        ById(root, "a").Frame.X.Should().BeApproximately(0, 0.5);
        ById(root, "a").Frame.Y.Should().BeApproximately(0, 0.5);
        ById(root, "b").Frame.X.Should().BeApproximately(0, 0.5);
        ById(root, "b").Frame.Y.Should().BeApproximately(40, 0.5);
        ById(root, "d").Frame.X.Should().BeApproximately(80, 0.5);
        ById(root, "d").Frame.Y.Should().BeApproximately(0, 0.5);
    }

    [TestMethod]
    public void Column_with_auto_height_never_wraps()
    {
        // With `height: auto` the column's main size is indefinite — the
        // container grows to fit, so wrap never triggers.
        var root = Layout("""
            <body><div id="c" style="display:flex; flex-direction:column; flex-wrap:wrap; width:300px">
              <div id="a" style="width:80px; height:40px"></div>
              <div id="b" style="width:80px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        ById(root, "a").Frame.Y.Should().BeApproximately(0, 0.5);
        ById(root, "b").Frame.X.Should().BeApproximately(0, 0.5);
        ById(root, "b").Frame.Y.Should().BeApproximately(40, 0.5);
    }

    // ---------- baseline ----------

    [TestMethod]
    public void Baseline_alignment_lines_up_mixed_font_sizes_on_a_shared_baseline()
    {
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; align-items:baseline">
              <div id="small" style="font-size:16px">small</div>
              <div id="big" style="font-size:32px">big</div>
            </div></body>
            """, new Size(800, 600));

        var small = ById(root, "small");
        var big = ById(root, "big");

        // The larger font has the deeper first baseline, so its box pins the
        // line's shared baseline and the smaller item shifts down to meet it.
        big.Frame.Y.Should().BeApproximately(0, 0.5);
        small.Frame.Y.Should().BeGreaterThan(1);

        // Both items' first text baselines land on the same container-space y.
        var smallBaseline = small.Frame.Y + FirstFragmentBaseline(small);
        var bigBaseline = big.Frame.Y + FirstFragmentBaseline(big);
        smallBaseline.Should().BeApproximately(bigBaseline, 0.5);
    }

    [TestMethod]
    public void Baseline_falls_back_to_the_margin_box_bottom_for_an_item_with_no_text()
    {
        // The empty 30px item synthesizes its baseline at its bottom edge
        // (§8.5 fallback). That is deeper than the text item's baseline, so
        // the empty box sits at the line top and the text item shifts down
        // until its baseline hits y=30. align-self:baseline covers the
        // per-item opt-in path too.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px">
              <div id="t" style="font-size:16px; align-self:baseline">text</div>
              <div id="e" style="width:50px; height:30px; align-self:baseline"></div>
            </div></body>
            """, new Size(800, 600));

        var text = ById(root, "t");
        var empty = ById(root, "e");

        empty.Frame.Y.Should().BeApproximately(0, 0.5);
        var textAscent = FirstFragmentBaseline(text);
        textAscent.Should().BeLessThan(30); // sanity: 16px text baseline < 30
        text.Frame.Y.Should().BeApproximately(30 - textAscent, 0.5);
        (text.Frame.Y + textAscent).Should().BeApproximately(30, 0.5);
    }

    // ---------- helpers ----------

    private static Box.Box ById(Box.Box root, string id)
        => FindBox(root, b => b.Element?.GetAttribute("id") == id)!;

    private static List<Box.Box> FlexChildren(Box.Box root)
    {
        var container = FindBox(root, b => b.Element?.GetAttribute("id") == "c")!;
        return container.Children.OfType<BlockBox>().Cast<Box.Box>().ToList();
    }

    private static Box.Box? FindBox(Box.Box root, Func<Box.Box, bool> pred)
    {
        if (pred(root))
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var hit = FindBox(child, pred);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }

    /// <summary>First text fragment's baseline offset from the item's
    /// border-box top (the items here carry no border or padding).</summary>
    private static double FirstFragmentBaseline(Box.Box item)
    {
        var tb = FindTextBox(item)!;
        var frag = tb.Fragments[0];
        return tb.Frame.Y + frag.Y + frag.Baseline;
    }

    private static TextBox? FindTextBox(Box.Box box)
    {
        foreach (var child in box.Children)
        {
            if (child is TextBox tb && tb.Fragments.Count > 0)
            {
                return tb;
            }

            var hit = FindTextBox(child);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }
}
