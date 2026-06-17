using AwesomeAssertions;
using Starling.Html;
using Starling.Spec;
using LayoutSize = Starling.Layout.Size;

namespace Starling.Paint.Tests;

/// <summary>
/// End-to-end pixel probes for the x.com inline-style dark theme (Tier 2
/// item 6, tasks/SITE_STYLING_PLAN.md). The snippet mirrors the markup of
/// <c>testdata/sites/xcom-nasa/index.html</c>: a black page background on
/// <c>&lt;body&gt;</c>, a pill button whose radius/width/style come from
/// atomic classes while every color arrives via inline longhands, the 2px
/// one-side tab underline, and a colored text run.
/// </summary>
[TestClass]
public sealed class InlineThemePaintTests
{
    [TestMethod]
    public void Dark_theme_snippet_paints_inline_longhand_colors()
    {
        var painter = new Painter();
        var document = HtmlParser.Parse("""
            <body style="margin: 0; background-color: #000000">
            <style>
            .r-sdzlij{border-bottom-left-radius:9999px;border-bottom-right-radius:9999px;border-top-left-radius:9999px;border-top-right-radius:9999px;}
            .r-1phboty{border-bottom-style:solid;border-left-style:solid;border-right-style:solid;border-top-style:solid;}
            .r-rs99b7{border-bottom-width:1px;border-left-width:1px;border-right-width:1px;border-top-width:1px;}
            </style>
            <div class="r-sdzlij r-1phboty r-rs99b7" style="margin: 40px 0 0 40px; width: 200px; height: 60px; background-color: rgba(239,243,244,1.00); border-top-color: rgba(83,100,113,1.00); border-right-color: rgba(83,100,113,1.00); border-bottom-color: rgba(83,100,113,1.00); border-left-color: rgba(83,100,113,1.00)"></div>
            <div style="margin: 38px 0 0 40px; width: 200px; height: 30px; border-bottom: 2px solid #EFF3F4"></div>
            <div style="margin: 28px 0 0 40px; font-size: 24px; color: rgba(29,155,240,1.00)">NASA</div>
            </body>
            """);

        using var bmp = painter.RenderDocument(document, new LayoutSize(320, 260));

        // 1. The inline body background-color propagates to the whole canvas
        // (CSS 2.1 §14.2) — x.com's dark page is exactly this pattern.
        bmp.GetPixel(10, 10).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255),
            "the inline body background-color must fill the canvas");

        // 2. Pill button interior — inline background-color longhand. The
        // border box spans (40,40)-(242,102); the centre is plain fill.
        bmp.GetPixel(141, 71).Should().Be(((byte)239, (byte)243, (byte)244, (byte)255),
            "the button fill comes from the inline background-color");

        // 3. The 9999px per-corner radius longhands (from the atomic class)
        // round the pill, so the bounding-box corner stays page-black.
        bmp.GetPixel(42, 42).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255),
            "the class radius longhands must clip the pill corner");

        // 4. Top border row at the horizontal centre — the straight stretch
        // of the 1px ring painted with the inline border-top-color. The
        // stroke straddles the border-box edge, so accept any mix of at
        // least 25% border ink over the fill: channel ∈ [border, border*t +
        // fill*(1-t)] for t = 0.25. Pure fill (239,243,244) and pure canvas
        // (0,0,0) both fail these bands.
        var (tr, tg, tb, _) = bmp.GetPixel(141, 40);
        ((int)tr).Should().BeInRange(83, 200,
            "the top border row must carry inline border-top-color ink");
        ((int)tg).Should().BeInRange(100, 207);
        ((int)tb).Should().BeInRange(113, 211);

        // 5. The one-side tab underline: 2px solid #EFF3F4 along the bottom
        // edge only (border box 140..172, border rows 170/171). The content
        // row above it stays canvas-black because no other side paints.
        bmp.GetPixel(140, 171).Should().Be(((byte)239, (byte)243, (byte)244, (byte)255),
            "the 2px bottom border paints with the underline color");
        bmp.GetPixel(140, 160).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255),
            "only the bottom side of the underline div has a border");

        // 6. The colored text run is present: rgba(29,155,240,1.00) link
        // blue dominates in the text band even after glyph anti-aliasing.
        var blueText = 0;
        for (var y = 190; y < 250; y++)
        {
            for (var x = 30; x < 240; x++)
            {
                var (r, g, b, _) = bmp.GetPixel(x, y);
                if (b > 100 && b > r + 40 && b > g + 20)
                {
                    blueText++;
                }
            }
        }
        blueText.Should().BeGreaterThan(20,
            "the inline color longhand must tint the rendered text run blue");
    }

    [TestMethod]
    public void Body_background_color_propagates_past_its_content_height()
    {
        // CSS 2.1 §14.2 — an <html> with no background borrows <body>'s
        // background for the canvas. x.com's body is 'background-color:
        // #000000' while most of the viewport is covered only by positioned
        // descendants, so without propagation the page edges flash white.
        var painter = new Painter();
        var document = HtmlParser.Parse(
            "<body style=\"margin: 0; background-color: #000000\">" +
            "<div style=\"height: 50px\"></div></body>");

        using var bmp = painter.RenderDocument(document, new LayoutSize(100, 200));

        bmp.GetPixel(10, 10).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255));
        bmp.GetPixel(10, 150).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255),
            "the body background must cover the canvas below the body's content");
    }

    [TestMethod]
    public void Root_background_color_propagates_to_the_canvas()
    {
        var painter = new Painter();
        var document = HtmlParser.Parse(
            "<html style=\"background-color: #000000\"><body style=\"margin: 0\">" +
            "<div style=\"height: 50px\"></div></body></html>");

        using var bmp = painter.RenderDocument(document, new LayoutSize(100, 200));

        bmp.GetPixel(10, 150).Should().Be(((byte)0, (byte)0, (byte)0, (byte)255),
            "the root element background is the canvas background");
    }

    [PendingFact(
        "PositionLayout zeroes Border edges for absolutely positioned boxes " +
        "(documented simplification next to its padding resolution), so borders " +
        "on position:absolute/fixed elements never paint. x.com draws bordered " +
        "pills inside positioned layers (e.g. the bottom login bar).",
        trackingWp: "wp:site-styling-tier2-theming")]
    public void Absolutely_positioned_box_paints_its_border()
    {
        var painter = new Painter();
        var document = HtmlParser.Parse(
            "<body style=\"margin: 0\">" +
            "<div style=\"position: absolute; top: 40px; left: 40px; width: 200px; " +
            "height: 60px; background-color: rgba(239,243,244,1.00); " +
            "border: 1px solid rgba(83,100,113,1.00)\"></div></body>");

        using var bmp = painter.RenderDocument(document, new LayoutSize(320, 200));

        bmp.GetPixel(141, 40).Should().Be(((byte)83, (byte)100, (byte)113, (byte)255),
            "the 1px top border of an absolutely positioned box must paint");
    }
}
