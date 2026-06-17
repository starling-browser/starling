using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
namespace Starling.Layout.Tests;

/// <summary>
/// CSS 2.1 §9.5 float / clear. McMaster.com's category grid is float-based
/// (no flex, no grid), so float support is the largest missing piece for that
/// page. These tests cover left/right floats, line wrap, clear, and the nested
/// float pattern McMaster uses (an inline-floated &lt;ul&gt; whose children are
/// also floats).
/// </summary>
[TestClass]
public sealed class FloatLayoutTests
{
    private static LayoutEngine NewEngine() => new(new StyleEngine());

    private static BlockBox Layout(string html, Size viewport)
        => NewEngine().LayoutDocument(HtmlParser.Parse(html), viewport);

    [TestMethod]
    public void Two_left_floats_sit_side_by_side_on_the_same_line()
    {
        // body has 8px UA margin → body content origin (8, 8). Two 60px floats
        // both pinned to the left edge: first at x=8, second at x=8+60=68.
        var root = Layout("""
            <body style="margin:0">
              <div id="a" style="float:left; width:60px; height:40px"></div>
              <div id="b" style="float:left; width:60px; height:40px"></div>
            </body>
            """, new Size(800, 600));

        var a = FindById(root, "a")!;
        var b = FindById(root, "b")!;

        // Both floats sit on the same horizontal band — same y, advancing x.
        a.Frame.Y.Should().BeApproximately(b.Frame.Y, 0.5);
        b.Frame.X.Should().BeApproximately(a.Frame.X + 60, 0.5);
    }

    [TestMethod]
    public void Right_floats_pin_to_right_edge()
    {
        // 200px container, two 50px floats on the right: rightmost at the edge,
        // second one immediately to its left.
        var root = Layout("""
            <body style="margin:0">
              <div style="width:200px">
                <div id="a" style="float:right; width:50px; height:30px"></div>
                <div id="b" style="float:right; width:50px; height:30px"></div>
              </div>
            </body>
            """, new Size(400, 600));

        var a = FindById(root, "a")!;
        var b = FindById(root, "b")!;

        // The first-in-source float (#a) sits at the rightmost slot, #b to its
        // left (CSS 2.1 §9.5.1 rule 6: right floats stack right-to-left).
        a.Frame.X.Should().BeApproximately(150, 0.5); // 200 - 50
        b.Frame.X.Should().BeApproximately(100, 0.5); // 200 - 100
    }

    [TestMethod]
    public void Floats_wrap_to_a_new_line_when_they_overflow_the_container()
    {
        // 200px container, three 80px floats: two fit on line one (160px),
        // the third drops to a new line.
        var root = Layout("""
            <body style="margin:0">
              <div style="width:200px">
                <div id="a" style="float:left; width:80px; height:30px"></div>
                <div id="b" style="float:left; width:80px; height:30px"></div>
                <div id="c" style="float:left; width:80px; height:30px"></div>
              </div>
            </body>
            """, new Size(400, 600));

        var a = FindById(root, "a")!;
        var b = FindById(root, "b")!;
        var c = FindById(root, "c")!;

        a.Frame.Y.Should().BeApproximately(b.Frame.Y, 0.5);
        c.Frame.Y.Should().BeGreaterThan(a.Frame.Y);
        c.Frame.X.Should().BeApproximately(a.Frame.X, 0.5);
    }

    [TestMethod]
    public void Clear_left_pushes_following_block_below_active_left_float()
    {
        // 200px tall float; the next block has clear:left so it must drop
        // below the float's bottom.
        var root = Layout("""
            <body style="margin:0">
              <div id="f" style="float:left; width:60px; height:200px"></div>
              <div id="next" style="clear:left; width:100px; height:40px"></div>
            </body>
            """, new Size(400, 600));

        var f = FindById(root, "f")!;
        var next = FindById(root, "next")!;

        next.Frame.Y.Should().BeGreaterThanOrEqualTo(f.Frame.Y + f.Frame.Height - 0.5);
    }

    [TestMethod]
    public void Block_without_clear_flows_alongside_an_active_float()
    {
        // No clear → the next block's top edge should align with the float's
        // top, not be pushed below it (the float reserves a column on the left
        // but does not push subsequent in-flow blocks downward).
        var root = Layout("""
            <body style="margin:0">
              <div id="f" style="float:left; width:60px; height:200px"></div>
              <div id="next" style="height:40px"></div>
            </body>
            """, new Size(400, 600));

        var f = FindById(root, "f")!;
        var next = FindById(root, "next")!;
        next.Frame.Y.Should().BeApproximately(f.Frame.Y, 0.5);
    }

    [TestMethod]
    public void Mcmaster_style_nested_float_grid_lays_out_two_columns_then_wraps()
    {
        // Approximates .subcat ul/li from mcmaster.com: an outer float:left
        // container with width=144 (room for 2 × 72px tiles) holds float:left
        // 72×114 tiles. Three tiles → two on row one, one on row two.
        var root = Layout("""
            <body style="margin:0">
              <div id="ul" style="float:left; width:144px">
                <div id="t1" style="float:left; width:72px; height:114px"></div>
                <div id="t2" style="float:left; width:72px; height:114px"></div>
                <div id="t3" style="float:left; width:72px; height:114px"></div>
              </div>
            </body>
            """, new Size(800, 600));

        var t1 = FindById(root, "t1")!;
        var t2 = FindById(root, "t2")!;
        var t3 = FindById(root, "t3")!;

        t1.Frame.Y.Should().BeApproximately(t2.Frame.Y, 0.5);
        t2.Frame.X.Should().BeApproximately(t1.Frame.X + 72, 0.5);
        t3.Frame.Y.Should().BeApproximately(t1.Frame.Y + 114, 0.5);
        t3.Frame.X.Should().BeApproximately(t1.Frame.X, 0.5);
    }

    // ---------------------------------------------------------------- helpers

    private static Box.Box? FindById(Box.Box root, string id)
    {
        if (root.Element?.GetAttribute("id") == id)
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var hit = FindById(child, id);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }
}
