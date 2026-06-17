using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
namespace Starling.Layout.Tests.Flex;

/// <summary>
/// Single-line flex tests. The flex container is always inside <c>&lt;body&gt;</c>
/// (which has the UA's 8px margin), so child frames are inspected in the
/// flex container's content-box coordinates — <c>Frame.X = 0</c> is the
/// container's left content edge.
/// </summary>
[TestClass]
public sealed class FlexLayoutTests
{
    private static LayoutEngine NewEngine() => new(new StyleEngine());

    private static BlockBox Layout(string html, Size viewport)
        => NewEngine().LayoutDocument(HtmlParser.Parse(html), viewport);

    [TestMethod]
    public void Row_with_no_flex_grow_places_children_at_their_explicit_widths_left_aligned()
    {
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px">
              <div style="width:100px; height:40px"></div>
              <div style="width:80px;  height:40px"></div>
              <div style="width:120px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items.Should().HaveCount(3);
        items[0].Frame.X.Should().BeApproximately(0, 0.5);
        items[0].Frame.Width.Should().BeApproximately(100, 0.5);
        items[1].Frame.X.Should().BeApproximately(100, 0.5);
        items[1].Frame.Width.Should().BeApproximately(80, 0.5);
        items[2].Frame.X.Should().BeApproximately(180, 0.5);
        items[2].Frame.Width.Should().BeApproximately(120, 0.5);
    }

    [TestMethod]
    public void Justify_content_center_centers_the_row()
    {
        // 600px container, three 100px items → used 300, free 300, leading 150.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px; justify-content:center">
              <div style="width:100px; height:40px"></div>
              <div style="width:100px; height:40px"></div>
              <div style="width:100px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items[0].Frame.X.Should().BeApproximately(150, 0.5);
        items[1].Frame.X.Should().BeApproximately(250, 0.5);
        items[2].Frame.X.Should().BeApproximately(350, 0.5);
    }

    [TestMethod]
    public void Justify_content_flex_end_packs_at_end()
    {
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px; justify-content:flex-end">
              <div style="width:100px; height:40px"></div>
              <div style="width:100px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items[0].Frame.X.Should().BeApproximately(400, 0.5);
        items[1].Frame.X.Should().BeApproximately(500, 0.5);
    }

    [TestMethod]
    public void Justify_content_space_between_puts_no_gap_at_edges()
    {
        // 600px - 3 * 100px = 300 free; split into 2 between-gaps = 150 each.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px; justify-content:space-between">
              <div style="width:100px; height:40px"></div>
              <div style="width:100px; height:40px"></div>
              <div style="width:100px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items[0].Frame.X.Should().BeApproximately(0, 0.5);
        items[1].Frame.X.Should().BeApproximately(250, 0.5);
        items[2].Frame.X.Should().BeApproximately(500, 0.5);
    }

    [TestMethod]
    public void Justify_content_space_around_puts_half_gap_at_edges()
    {
        // 600 - 300 = 300 free; per-item slot = 100; half = 50 leading.
        // Positions: 50, 50+100+100 = 250, 250+100+100 = 450.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px; justify-content:space-around">
              <div style="width:100px; height:40px"></div>
              <div style="width:100px; height:40px"></div>
              <div style="width:100px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items[0].Frame.X.Should().BeApproximately(50, 0.5);
        items[1].Frame.X.Should().BeApproximately(250, 0.5);
        items[2].Frame.X.Should().BeApproximately(450, 0.5);
    }

    [TestMethod]
    public void Three_flex_one_children_split_container_equally()
    {
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px">
              <div style="flex:1; height:40px"></div>
              <div style="flex:1; height:40px"></div>
              <div style="flex:1; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items[0].Frame.Width.Should().BeApproximately(200, 0.5);
        items[1].Frame.Width.Should().BeApproximately(200, 0.5);
        items[2].Frame.Width.Should().BeApproximately(200, 0.5);
        items[0].Frame.X.Should().BeApproximately(0, 0.5);
        items[1].Frame.X.Should().BeApproximately(200, 0.5);
        items[2].Frame.X.Should().BeApproximately(400, 0.5);
    }

    [TestMethod]
    public void Mixed_flex_grow_distributes_free_space_proportionally()
    {
        // flex:2 + flex:1 + flex:1 (all basis 0) → 50/25/25 split of 800px.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:800px; height:40px">
              <div style="flex:2; height:40px"></div>
              <div style="flex:1; height:40px"></div>
              <div style="flex:1; height:40px"></div>
            </div></body>
            """, new Size(900, 600));

        var items = FlexChildren(root);
        items[0].Frame.Width.Should().BeApproximately(400, 0.5);
        items[1].Frame.Width.Should().BeApproximately(200, 0.5);
        items[2].Frame.Width.Should().BeApproximately(200, 0.5);
    }

    [TestMethod]
    public void Align_items_center_centers_short_child_cross_axis()
    {
        // Container 100px tall, child 40px tall → cross offset = 30.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:100px; align-items:center">
              <div style="width:100px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items[0].Frame.Y.Should().BeApproximately(30, 0.5);
        items[0].Frame.Height.Should().BeApproximately(40, 0.5);
    }

    [TestMethod]
    public void Align_items_stretch_fills_container_cross_size_when_height_auto()
    {
        // No explicit child height → stretch to container's 100px.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:100px">
              <div style="width:100px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items[0].Frame.Height.Should().BeApproximately(100, 0.5);
    }

    [TestMethod]
    public void Flex_direction_column_stacks_main_axis_vertically()
    {
        var root = Layout("""
            <body><div id="c" style="display:flex; flex-direction:column; width:400px; height:300px">
              <div style="width:200px; height:50px"></div>
              <div style="width:200px; height:60px"></div>
              <div style="width:200px; height:70px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items[0].Frame.Y.Should().BeApproximately(0, 0.5);
        items[1].Frame.Y.Should().BeApproximately(50, 0.5);
        items[2].Frame.Y.Should().BeApproximately(110, 0.5);
    }

    [TestMethod]
    public void Column_with_auto_height_and_justify_center_packs_items_from_the_top()
    {
        // Regression: a `flex-direction: column` container with `height: auto`
        // has an INDEFINITE main size. It must size to its content, not fall back
        // to the viewport height. Before the fix the viewport height (600) became
        // the column's main size, so `justify-content: center` distributed ~270px
        // of phantom free space and shoved the items far below the box
        // (angular.dev's nav-button icon/label landing at y≈422 in a 96px box).
        var root = Layout("""
            <body><div id="c" style="display:flex; flex-direction:column;
                        align-items:center; justify-content:center; width:120px">
              <div style="width:16px; height:16px"></div>
              <div style="width:40px; height:14px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        // Items pack from the main-start edge: there is no free space to
        // distribute, so justify-content is a no-op.
        items[0].Frame.Y.Should().BeApproximately(0, 0.5);
        items[1].Frame.Y.Should().BeApproximately(16, 0.5);

        // The container sizes its content box to the stacked item heights.
        var container = FindBox(root, b => b.Element?.GetAttribute("id") == "c")!;
        container.Frame.Height.Should().BeApproximately(30, 0.5);
    }

    [TestMethod]
    public void Column_with_auto_height_and_min_height_distributes_only_the_real_slack()
    {
        // A `min-height` floor gives a column a definite minimum: justify-content
        // distributes the slack between the content (30px) and the floor (100px),
        // i.e. 70px, centering it (35px leading) — but never the viewport height.
        var root = Layout("""
            <body><div id="c" style="display:flex; flex-direction:column;
                        justify-content:center; min-height:100px; width:120px">
              <div style="width:16px; height:16px"></div>
              <div style="width:40px; height:14px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items[0].Frame.Y.Should().BeApproximately(35, 0.5);
        items[1].Frame.Y.Should().BeApproximately(51, 0.5);

        var container = FindBox(root, b => b.Element?.GetAttribute("id") == "c")!;
        container.Frame.Height.Should().BeApproximately(100, 0.5);
    }

    [TestMethod]
    public void Flex_direction_row_reverse_reverses_visual_order_but_keeps_paint_order()
    {
        // Reverse direction: the items are positioned right-to-left visually,
        // but the box-tree order (children[0..n]) stays paint-order.
        var root = Layout("""
            <body><div id="c" style="display:flex; flex-direction:row-reverse; width:600px; height:40px">
              <div id="a" style="width:100px; height:40px"></div>
              <div id="b" style="width:100px; height:40px"></div>
              <div id="d" style="width:100px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        // children order in the box tree is a, b, d (paint order preserved).
        var container = FindBox(root, b => b.Element?.GetAttribute("id") == "c")!;
        var children = container.Children.OfType<BlockBox>().ToList();
        children[0].Element!.GetAttribute("id").Should().Be("a");
        children[1].Element!.GetAttribute("id").Should().Be("b");
        children[2].Element!.GetAttribute("id").Should().Be("d");

        // Visually: a at the rightmost slot, d at the leftmost.
        var aBox = children[0];
        var bBox = children[1];
        var dBox = children[2];
        aBox.Frame.X.Should().BeApproximately(500, 0.5);
        bBox.Frame.X.Should().BeApproximately(400, 0.5);
        dBox.Frame.X.Should().BeApproximately(300, 0.5);
    }

    [TestMethod]
    public void Gap_adds_space_between_items_but_not_at_ends()
    {
        // 600px container, 3 * 100px items + 2 * 10px gaps = 320; left-aligned.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px; gap:10px">
              <div style="width:100px; height:40px"></div>
              <div style="width:100px; height:40px"></div>
              <div style="width:100px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items[0].Frame.X.Should().BeApproximately(0, 0.5);
        items[1].Frame.X.Should().BeApproximately(110, 0.5);
        items[2].Frame.X.Should().BeApproximately(220, 0.5);
    }

    [TestMethod]
    public void Gap_combined_with_space_between_respects_gap_as_minimum()
    {
        // 3 * 100 + 2 * 10 = 320 consumed, 280 free, split into 2 between
        // distributions = 140 added on top of the 10px gap → between = 150.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px; gap:10px; justify-content:space-between">
              <div style="width:100px; height:40px"></div>
              <div style="width:100px; height:40px"></div>
              <div style="width:100px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items[0].Frame.X.Should().BeApproximately(0, 0.5);
        items[1].Frame.X.Should().BeApproximately(250, 0.5);
        items[2].Frame.X.Should().BeApproximately(500, 0.5);

        // gap as minimum: distance between adjacent items >= 10px.
        (items[1].Frame.X - (items[0].Frame.X + items[0].Frame.Width))
            .Should().BeGreaterThanOrEqualTo(10);
    }

    [TestMethod]
    public void Anonymous_flex_item_wrapping_inline_run_does_not_inherit_container_width()
    {
        // Regression: mcmaster's .category-tile is `display:flex; width:100%`
        // holding an inline <img>/text run + a sibling. The anonymous box that
        // wraps the inline run used to inherit the container's ComputedStyle —
        // including its `width` — so flex read that as the wrapper's flex-basis,
        // ballooning it to (nearly) the whole row and shoving the sibling to the
        // far right edge. The wrapper must instead shrink to its content width.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:300px; height:40px">
              x<div style="width:100px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var container = FindBox(root, b => b.Element?.GetAttribute("id") == "c")!;
        var anon = container.Children.First(c => c.Kind == BoxKind.AnonymousBlock);
        var sized = container.Children.OfType<BlockBox>().First();

        // The wrapper hugs its tiny text content, nowhere near the 300px row.
        anon.Frame.Width.Should().BeLessThan(100);
        // The sized sibling therefore sits right after it, not pushed rightward.
        sized.Frame.X.Should().BeLessThan(100);
        sized.Frame.Width.Should().BeApproximately(100, 0.5);
    }

    [TestMethod]
    public void Flex_wrap_breaks_overflowing_items_onto_multiple_lines()
    {
        // 300px row with flex-wrap: two 200px items can't share a line, so the
        // second drops below the first line (whose cross size is 40px).
        var root = Layout("""
            <body><div id="c" style="display:flex; flex-wrap:wrap; width:300px">
              <div style="width:200px; height:40px"></div>
              <div style="width:200px; height:50px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items[0].Frame.X.Should().BeApproximately(0, 0.5);
        items[0].Frame.Y.Should().BeApproximately(0, 0.5);
        items[1].Frame.X.Should().BeApproximately(0, 0.5);
        items[1].Frame.Y.Should().BeApproximately(40, 0.5);

        // Container height grows to enclose both lines (40 + 50).
        var container = FindBox(root, b => b.Element?.GetAttribute("id") == "c")!;
        container.Frame.Height.Should().BeApproximately(90, 0.5);
    }

    [TestMethod]
    public void Nowrap_keeps_overflowing_items_on_a_single_line()
    {
        // Without flex-wrap (and with shrink disabled) the items overflow on one
        // line rather than wrapping.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:300px; height:40px">
              <div style="width:200px; height:40px; flex-shrink:0"></div>
              <div style="width:200px; height:40px; flex-shrink:0"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items[0].Frame.Y.Should().BeApproximately(0, 0.5);
        items[1].Frame.Y.Should().BeApproximately(0, 0.5);
        items[1].Frame.X.Should().BeApproximately(200, 0.5);
    }

    [TestMethod]
    public void Order_reorders_items_for_layout_but_keeps_paint_order()
    {
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px">
              <div id="a" style="width:100px; height:40px; order:2"></div>
              <div id="b" style="width:100px; height:40px; order:1"></div>
            </div></body>
            """, new Size(800, 600));

        var container = FindBox(root, b => b.Element?.GetAttribute("id") == "c")!;
        var children = container.Children.OfType<BlockBox>().ToList();

        // Paint order (box-tree order) stays document order: a, b.
        children[0].Element!.GetAttribute("id").Should().Be("a");
        children[1].Element!.GetAttribute("id").Should().Be("b");

        // Layout order follows `order`: b (1) at the start, a (2) after it.
        children.First(c => c.Element!.GetAttribute("id") == "b").Frame.X.Should().BeApproximately(0, 0.5);
        children.First(c => c.Element!.GetAttribute("id") == "a").Frame.X.Should().BeApproximately(100, 0.5);
    }

    [TestMethod]
    public void Flex_item_min_width_is_honored_over_a_smaller_basis()
    {
        // min-width:200 wins over the 50px width and isn't over-grown by its
        // neighbour (regression for collapsing footer link groups).
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px">
              <div id="m" style="width:50px; min-width:200px; height:40px"></div>
              <div id="n" style="width:100px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var items = FlexChildren(root);
        items.First(b => b.Element?.GetAttribute("id") == "m").Frame.Width.Should().BeApproximately(200, 0.5);
        items.First(b => b.Element?.GetAttribute("id") == "n").Frame.X.Should().BeApproximately(200, 0.5);
    }

    // ---------- helpers ----------

    private static List<Box.Box> FlexChildren(Box.Box root)
    {
        var container = FindBox(root, b => b.Element?.GetAttribute("id") == "c")!;
        // The flex container's children are laid out in flex order; for
        // non-reverse cases visual order == paint order, so we sort by X (or
        // Y for column) when callers want geometric order. Tests below either
        // index by paint order or sort explicitly.
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
}
