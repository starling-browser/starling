using AwesomeAssertions;
using Starling.Css;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Spec;

namespace Starling.Layout.Tests;

/// <summary>
/// CSS Grid track sizing: grid-template-rows, minmax(), fr distribution with
/// minimum floors (§11.7), repeat(auto-fill|auto-fit) (§7.2.3), fit-content(),
/// and track lists that arrive through calc()/var() substitution. Assertions
/// are item frames in the grid container's content-box space.
/// </summary>
[TestClass]
public sealed class GridTrackSizingTests
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
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#track-sizing", section: "7.2")]
    public void Grid_template_rows_sizes_fixed_rows()
    {
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:400px; grid-template-rows:50px 100px">
              <div id="a"></div><div id="b"></div>
            </div></body>
            """, new Size(1000, 600));

        var a = ById(root, "a")!;
        var b = ById(root, "b")!;
        a.Frame.Y.Should().Be(0);
        a.Frame.Height.Should().Be(50, "first explicit row is 50px and the item stretches");
        b.Frame.Y.Should().Be(50);
        b.Frame.Height.Should().Be(100);
        a.Frame.Width.Should().Be(400, "the single implicit auto column stretches to the container");
        ById(root, "g")!.Frame.Height.Should().Be(150, "auto container height is the sum of the rows");
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#track-sizing", section: "7.2.1")]
    public void Percentage_row_against_indefinite_height_behaves_as_auto_and_keeps_its_slot()
    {
        // 50% of an indefinite container height behaves as auto — the track
        // must still occupy its slot. Dropping it would shift the 100px size
        // onto row 1 and push item b into an implicit zero-height row.
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:400px; grid-template-rows:50% 100px">
              <div id="a"></div><div id="b"></div>
            </div></body>
            """, new Size(1000, 600));

        var a = ById(root, "a")!;
        var b = ById(root, "b")!;
        a.Frame.Height.Should().Be(0, "an auto row with empty content collapses");
        b.Frame.Y.Should().Be(0);
        b.Frame.Height.Should().Be(100, "the 100px definition stays on the second track");
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#track-sizing", section: "7.2.1")]
    public void Percent_calc_row_against_indefinite_height_behaves_as_auto_and_keeps_its_slot()
    {
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:400px; grid-template-rows:calc(50% - 10px) 100px">
              <div id="a"></div><div id="b"></div>
            </div></body>
            """, new Size(1000, 600));

        ById(root, "a")!.Frame.Height.Should().Be(0);
        var b = ById(root, "b")!;
        b.Frame.Y.Should().Be(0);
        b.Frame.Height.Should().Be(100);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#fr-unit", section: "11.7")]
    public void Fr_rows_split_a_definite_height_proportionally()
    {
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:400px; height:300px; grid-template-rows:1fr 2fr">
              <div id="a"></div><div id="b"></div>
            </div></body>
            """, new Size(1000, 600));

        ById(root, "a")!.Frame.Height.Should().Be(100);
        var b = ById(root, "b")!;
        b.Frame.Y.Should().Be(100);
        b.Frame.Height.Should().Be(200);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#fr-unit", section: "11.7")]
    public void Fr_columns_share_space_left_by_fixed_tracks_and_gaps()
    {
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:720px; gap:10px; grid-template-columns:100px 1fr 2fr">
              <div id="a"></div><div id="b"></div><div id="c"></div>
            </div></body>
            """, new Size(1000, 600));

        // free = 720 - 2*10 - 100 = 600; 1fr = 200.
        var b = ById(root, "b")!;
        var c = ById(root, "c")!;
        b.Frame.X.Should().Be(110);
        b.Frame.Width.Should().Be(200);
        c.Frame.X.Should().Be(320);
        c.Frame.Width.Should().Be(400);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#valdef-grid-template-columns-minmax", section: "7.2.5")]
    public void Minmax_with_min_greater_than_max_uses_min()
    {
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:500px; grid-template-columns:minmax(200px, 100px) 1fr">
              <div id="a"></div><div id="b"></div>
            </div></body>
            """, new Size(1000, 600));

        ById(root, "a")!.Frame.Width.Should().Be(200, "max < min means max is treated as min");
        var b = ById(root, "b")!;
        b.Frame.X.Should().Be(200);
        b.Frame.Width.Should().Be(300);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#fr-unit", section: "11.7")]
    public void Fr_distribution_respects_minmax_minimum_floors()
    {
        // 200px total: minmax(150px,1fr) floors at 150, so the second 1fr
        // track only gets the remaining 50 instead of an equal 100/100 split.
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:200px; grid-template-columns:minmax(150px, 1fr) 1fr">
              <div id="a"></div><div id="b"></div>
            </div></body>
            """, new Size(1000, 600));

        ById(root, "a")!.Frame.Width.Should().Be(150);
        var b = ById(root, "b")!;
        b.Frame.X.Should().Be(150);
        b.Frame.Width.Should().Be(50);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#algo-track-sizing", section: "11.5")]
    public void Github_page_shell_columns_auto_zero_minmax_calc()
    {
        // The GitHub .Layout shell: an auto sidebar column, a 0 divider track,
        // and minmax(0, calc(100% - 296px - 24px)) for the content. The calc
        // resolves against the container width; the content track then grows
        // from 0 toward that limit with the free space that is actually left.
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:1000px; column-gap:24px; grid-template-columns:auto 0 minmax(0, calc(100% - 296px - 24px))">
              <div id="side" style="width:296px"></div><div id="zero"></div><div id="main"></div>
            </div></body>
            """, new Size(1200, 800));

        var side = ById(root, "side")!;
        var main = ById(root, "main")!;
        side.Frame.X.Should().Be(0);
        side.Frame.Width.Should().Be(296, "the auto track sizes to the sidebar's max-content width");
        // free = 1000 - 2*24 - 296 = 656; limit = calc(...) = 680, so 656 wins.
        main.Frame.X.Should().Be(344, "296 + 24 + 0 + 24");
        main.Frame.Width.Should().Be(656);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#track-sizing", section: "7.2")]
    public void Track_list_routed_through_a_custom_property_is_honored()
    {
        var root = Layout(
            """<body style="margin:0"><div id="g" class="g"><div id="a"></div><div id="b"></div></div></body>""",
            new Size(1000, 600),
            ".g { display:grid; --cols: 120px 1fr; grid-template-columns: var(--cols); width:400px }");

        ById(root, "a")!.Frame.Width.Should().Be(120, "the track list arrives after var() substitution");
        var b = ById(root, "b")!;
        b.Frame.X.Should().Be(120);
        b.Frame.Width.Should().Be(280);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#auto-repeat", section: "7.2.3.2")]
    public void Repeat_auto_fill_derives_count_from_definite_width_and_gaps()
    {
        // floor((430 + 10) / (100 + 10)) = 4 columns.
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:430px; gap:10px; grid-template-columns:repeat(auto-fill, 100px)">
              <div id="i1" style="height:20px"></div><div id="i2" style="height:20px"></div>
              <div id="i3" style="height:20px"></div><div id="i4" style="height:20px"></div>
              <div id="i5" style="height:20px"></div>
            </div></body>
            """, new Size(1000, 600));

        ById(root, "i2")!.Frame.X.Should().Be(110);
        ById(root, "i4")!.Frame.X.Should().Be(330, "the fourth item still fits the first row");
        var i5 = ById(root, "i5")!;
        i5.Frame.X.Should().Be(0, "a fifth item wraps because only four tracks fit");
        i5.Frame.Y.Should().Be(30, "row height 20 + row gap 10");
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#auto-repeat", section: "7.2.3.2")]
    public void Repeat_auto_fill_keeps_empty_tracks()
    {
        // Four minmax(100px,1fr) tracks fit; with auto-fill the two empty
        // tracks stay, so each track keeps its equal 1fr share of 100px.
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:430px; gap:10px; grid-template-columns:repeat(auto-fill, minmax(100px, 1fr))">
              <div id="a"></div><div id="b"></div>
            </div></body>
            """, new Size(1000, 600));

        ById(root, "a")!.Frame.Width.Should().Be(100);
        ById(root, "b")!.Frame.X.Should().Be(110);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#auto-repeat", section: "7.2.3.2")]
    public void Repeat_auto_fit_collapses_empty_tracks()
    {
        // Same grid as the auto-fill case, but auto-fit collapses the two
        // empty tracks (and their gaps), so the two occupied 1fr tracks split
        // the whole 430px minus one gap: 210 each.
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:430px; gap:10px; grid-template-columns:repeat(auto-fit, minmax(100px, 1fr))">
              <div id="a"></div><div id="b"></div>
            </div></body>
            """, new Size(1000, 600));

        ById(root, "a")!.Frame.Width.Should().Be(210);
        var b = ById(root, "b")!;
        b.Frame.X.Should().Be(220);
        b.Frame.Width.Should().Be(210);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#fit-content", section: "7.2.5")]
    public void Fit_content_clamps_a_content_sized_track()
    {
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:400px; grid-template-columns:fit-content(150px) 1fr">
              <div id="a" style="width:300px"></div><div id="b"></div>
            </div></body>
            """, new Size(1000, 600));

        // The 300px item's contribution is clamped to fit-content's 150px.
        var b = ById(root, "b")!;
        b.Frame.X.Should().Be(150);
        b.Frame.Width.Should().Be(250);
    }

    [TestMethod]
    [Spec("css-grid-2", "https://www.w3.org/TR/css-grid-2/#algo-track-sizing", section: "11")]
    public void Auto_column_sizes_to_content_next_to_fr()
    {
        var root = Layout("""
            <body style="margin:0"><div id="g" style="display:grid; width:400px; grid-template-columns:auto 1fr">
              <div id="a" style="width:100px"></div><div id="b"></div>
            </div></body>
            """, new Size(1000, 600));

        var b = ById(root, "b")!;
        b.Frame.X.Should().Be(100, "the auto track takes its item's width, not an equal fr share");
        b.Frame.Width.Should().Be(300);
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
