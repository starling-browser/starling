using AwesomeAssertions;
using Starling.Css;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Spec;

namespace Starling.Layout.Tests;

/// <summary>
/// CSS Grid item placement: grid-row/grid-column line numbers (positive and
/// negative), span N, grid-template-areas + grid-area named placement with
/// rectangle validation (§7.3), and sparse auto-placement that flows around
/// explicitly placed items (css-grid-1 §8.5). Assertions are item frames in
/// the grid container's content-box space.
/// </summary>
[TestClass]
public sealed class GridPlacementTests
{
    private static BlockBox Layout(string html, Size viewport, string? css = null)
    {
        var engine = new StyleEngine();
        if (css is not null)
        {
            engine.AddStyleSheet(CssParser.ParseStyleSheet(css, StyleOrigin.Author));
        }

        return new LayoutEngine(engine).LayoutDocument(HtmlParser.Parse(html), viewport);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#line-placement", section: "8.3")]
    public void Grid_column_line_numbers_span_tracks()
    {
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:300px; grid-template-columns:100px 100px 100px">
              <div id="a" style="grid-column:1 / 3; height:20px"></div>
              <div id="b" style="height:20px"></div>
            </div></body>
            """, new Size(1000, 600));

        var a = ById(root, "a")!;
        a.Frame.X.Should().Be(0);
        a.Frame.Width.Should().Be(200, "lines 1 to 3 cover the first two 100px tracks");
        var b = ById(root, "b")!;
        b.Frame.X.Should().Be(200, "auto placement flows around the explicit item");
        b.Frame.Y.Should().Be(0);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#line-placement", section: "8.3")]
    public void Negative_line_numbers_count_from_the_explicit_grid_end()
    {
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:300px; grid-template-columns:100px 100px 100px">
              <div id="a" style="grid-column:-2 / -1; height:20px"></div>
              <div id="b" style="height:20px"></div>
            </div></body>
            """, new Size(1000, 600));

        var a = ById(root, "a")!;
        a.Frame.X.Should().Be(200, "-2 is the line before the last of three tracks");
        a.Frame.Width.Should().Be(100);
        ById(root, "b")!.Frame.X.Should().Be(0, "auto placement uses the free leading cell");
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#placement-shorthands", section: "8.4")]
    public void Grid_column_line_with_span_end()
    {
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:300px; grid-template-columns:100px 100px 100px">
              <div id="a" style="grid-column:2 / span 2; height:20px"></div>
              <div id="b" style="height:20px"></div>
            </div></body>
            """, new Size(1000, 600));

        var a = ById(root, "a")!;
        a.Frame.X.Should().Be(100);
        a.Frame.Width.Should().Be(200);
        ById(root, "b")!.Frame.X.Should().Be(0);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#auto-placement-algo", section: "8.5")]
    public void Auto_placement_flows_around_a_fully_definite_item()
    {
        // `a` is pinned to row 2 / column 2; auto items fill (0,0), (0,1) and
        // then skip the occupied cell to land at (1,0).
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:200px; grid-template-columns:100px 100px; grid-template-rows:50px 50px">
              <div id="a" style="grid-row:2; grid-column:2"></div>
              <div id="b"></div><div id="c"></div><div id="d"></div>
            </div></body>
            """, new Size(1000, 600));

        var a = ById(root, "a")!;
        a.Frame.X.Should().Be(100);
        a.Frame.Y.Should().Be(50);
        ById(root, "b")!.Frame.X.Should().Be(0);
        ById(root, "b")!.Frame.Y.Should().Be(0);
        ById(root, "c")!.Frame.X.Should().Be(100);
        ById(root, "c")!.Frame.Y.Should().Be(0);
        var d = ById(root, "d")!;
        d.Frame.X.Should().Be(0, "the cell next to it is taken by the explicit item");
        d.Frame.Y.Should().Be(50);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#auto-placement-algo", section: "8.5")]
    public void Sparse_auto_placement_does_not_backfill_holes()
    {
        // Two span-2 items leave a hole at (0,2); sparse packing places the
        // next single-cell item after the cursor, not back in the hole.
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:300px; grid-template-columns:100px 100px 100px">
              <div id="a" style="grid-column:span 2; height:20px"></div>
              <div id="b" style="grid-column:span 2; height:20px"></div>
              <div id="c" style="height:20px"></div>
            </div></body>
            """, new Size(1000, 600));

        ById(root, "a")!.Frame.Y.Should().Be(0);
        var b = ById(root, "b")!;
        b.Frame.Y.Should().Be(20, "a 2-track span no longer fits row 1 after the first item");
        b.Frame.X.Should().Be(0);
        var c = ById(root, "c")!;
        c.Frame.Y.Should().Be(20, "sparse flow never returns to the row-1 hole");
        c.Frame.X.Should().Be(200);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#grid-template-areas-property", section: "7.3")]
    public void Grid_template_areas_places_named_items()
    {
        var root = Layout(
            """
            <body style="margin:0"><div id="g" class="shell">
              <div id="h" style="grid-area:head"></div>
              <div id="n" style="grid-area:nav"></div>
              <div id="m" style="grid-area:main"></div>
              <div id="f" style="grid-area:foot"></div>
            </div></body>
            """,
            new Size(1000, 600),
            """
            .shell { display:grid; width:400px; height:300px;
                     grid-template-columns:100px 1fr; grid-template-rows:50px 1fr 30px;
                     grid-template-areas:"head head" "nav main" "foot foot"; }
            """);

        var h = ById(root, "h")!;
        h.Frame.Should().Be(new Rect(0, 0, 400, 50), "head spans both columns of row 1");
        var n = ById(root, "n")!;
        n.Frame.Should().Be(new Rect(0, 50, 100, 220));
        var m = ById(root, "m")!;
        m.Frame.Should().Be(new Rect(100, 50, 300, 220));
        var f = ById(root, "f")!;
        f.Frame.Should().Be(new Rect(0, 270, 400, 30));
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#grid-template-areas-property", section: "7.3")]
    public void Non_rectangular_template_areas_are_dropped()
    {
        // "a" forms an L shape, which makes the whole declaration invalid;
        // both items fall back to auto placement.
        var root = Layout(
            """
            <body style="margin:0"><div id="g" class="bad">
              <div id="p" style="grid-area:a; height:20px"></div>
              <div id="q" style="height:20px"></div>
            </div></body>
            """,
            new Size(1000, 600),
            """.bad { display:grid; width:200px; grid-template-columns:100px 100px; grid-template-areas:"a a" ". a"; }""");

        var p = ById(root, "p")!;
        p.Frame.X.Should().Be(0);
        p.Frame.Y.Should().Be(0);
        var q = ById(root, "q")!;
        q.Frame.X.Should().Be(100, "with the areas dropped both items auto-place in row 1");
        q.Frame.Y.Should().Be(0);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#placement", section: "8")]
    public void Definite_row_beyond_content_creates_empty_implicit_rows()
    {
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:200px; row-gap:10px">
              <div id="a" style="grid-row:3; height:30px"></div>
            </div></body>
            """, new Size(1000, 600));

        var a = ById(root, "a")!;
        a.Frame.Y.Should().Be(20, "two empty zero-height implicit rows still keep their gaps");
        ById(root, "g")!.Frame.Height.Should().Be(50);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#placement-shorthands", section: "8.4")]
    public void Grid_area_line_numbers_span_both_axes()
    {
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:200px; grid-template-columns:100px 100px; grid-template-rows:40px 40px">
              <div id="a" style="grid-area:1 / 1 / 3 / 3"></div>
              <div id="b" style="height:20px"></div>
            </div></body>
            """, new Size(1000, 600));

        var a = ById(root, "a")!;
        a.Frame.Width.Should().Be(200);
        a.Frame.Height.Should().Be(80);
        var b = ById(root, "b")!;
        b.Frame.Y.Should().Be(80, "the explicit area fills rows 1-2, so auto flow opens row 3");
        b.Frame.X.Should().Be(0);
    }

    private static Box.Box? ById(Box.Box root, string id)
        => FindBox(root, b => b.Element?.GetAttribute("id") == id);

    private static Box.Box? FindBox(Box.Box root, Func<Box.Box, bool> pred)
    {
        if (pred(root))
        {
            return root;
        }

        foreach (var c in root.Children)
        {
            var hit = FindBox(c, pred);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }
}
