using AwesomeAssertions;
using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Css.Values;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Text;
using Starling.Paint.Backend;
using Starling.Paint.DisplayList;
using LayoutRect = Starling.Layout.Rect;
using LayoutSize = Starling.Layout.Size;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint.Tests;

/// <summary>
/// Tier 4 items 11+12 — CSS UI 4 §3 outline painting and css-backgrounds-3
/// §4.2 dashed / dotted / double border styles. Backend pixel probes follow
/// the <see cref="RoundedRectAndShadowTests"/> pattern (hand-built display
/// lists through <see cref="ImageSharpBackend"/>); builder tests drive real
/// HTML through layout so emission geometry and bracket order are covered.
/// </summary>
[TestClass]
public sealed class OutlineAndBorderStyleTests
{
    private static readonly CssColor Red = new(255, 0, 0, 255);
    private static readonly CssColor Blue = new(0, 0, 255, 255);
    private static readonly CssColor Green = new(0, 128, 0, 255);

    private static PaintList BuildList(string html, LayoutSize viewport)
    {
        var document = HtmlParser.Parse(html);
        var style = new StyleEngine();
        var engine = new LayoutEngine(style, DefaultTextMeasurer.Instance);
        var root = engine.LayoutDocument(document, viewport);
        return new DisplayListBuilder().Build(root);
    }

    private static RenderedBitmap Render(PaintList list, int w, int h)
    {
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        return backend.Render(list, new LayoutSize(w, h));
    }

    // ----- backend pixel probes: border styles -------------------------------

    [TestMethod]
    public void Dashed_top_border_paints_dashes_and_leaves_gaps()
    {
        // Box (10,10,104,60), 4px dashed red top border, square corners: the
        // top side owns the full edge, so the run is 104px. The backend picks
        // n = round((104/4 + 1)/3) = 9 dashes, gap = 104/26 = 4, dash = 8.
        // First dash spans x [10,18], first gap x [18,22].
        var list = new PaintList();
        list.Add(new DrawBorderSides(
            new LayoutRect(10, 10, 104, 60), CornerRadii.None,
            TopWidth: 4, RightWidth: 0, BottomWidth: 0, LeftWidth: 0,
            Red, Red, Red, Red,
            BorderSideStyle.Dashed, BorderSideStyle.None, BorderSideStyle.None, BorderSideStyle.None));
        using var bmp = Render(list, 160, 120);

        bmp.GetPixel(14, 12).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255),
            "the centre of the first dash is solid red");
        bmp.GetPixel(20, 12).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255),
            "the centre of the first gap stays white");
        bmp.GetPixel(60, 30).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255),
            "the box interior is untouched by border painting");
    }

    [TestMethod]
    public void Dotted_left_border_paints_round_dots_with_clear_space_between()
    {
        // Box (10,10,104,54), 6px dotted blue left border only. The run spans
        // the full left edge (no top/bottom bands), inset one radius (3px) at
        // each end: dot centres run y = 13 .. 61, nominal spacing 12 →
        // intervals = round(48/12) = 4, step 12 → centres at y = 13,25,37,49,61
        // on the x = 13 centre line, radius 3.
        var list = new PaintList();
        list.Add(new DrawBorderSides(
            new LayoutRect(10, 10, 104, 54), CornerRadii.None,
            TopWidth: 0, RightWidth: 0, BottomWidth: 0, LeftWidth: 6,
            Blue, Blue, Blue, Blue,
            BorderSideStyle.None, BorderSideStyle.None, BorderSideStyle.None, BorderSideStyle.Dotted));
        using var bmp = Render(list, 160, 120);

        bmp.GetPixel(13, 25).Should().Be(((byte)0, (byte)0, (byte)255, (byte)255),
            "a dot centre is solid blue");
        bmp.GetPixel(13, 31).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255),
            "the midpoint between two dots stays white");
        // A dot is round: the band corner diagonal from the dot centre is empty.
        bmp.GetPixel(10, 28).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255),
            "between dots even the outer band edge is unpainted");
    }

    [TestMethod]
    public void Double_border_paints_two_strokes_with_an_empty_middle_third()
    {
        // Box (10,10,100,60), 9px double green top border: bands y [10,13]
        // and [16,19], middle third [13,16] empty.
        var list = new PaintList();
        list.Add(new DrawBorderSides(
            new LayoutRect(10, 10, 100, 60), CornerRadii.None,
            TopWidth: 9, RightWidth: 0, BottomWidth: 0, LeftWidth: 0,
            Green, Green, Green, Green,
            BorderSideStyle.Double, BorderSideStyle.None, BorderSideStyle.None, BorderSideStyle.None));
        using var bmp = Render(list, 160, 120);

        bmp.GetPixel(60, 11).Should().Be(((byte)0, (byte)128, (byte)0, (byte)255),
            "the outer stroke of a double border is painted");
        bmp.GetPixel(60, 17).Should().Be(((byte)0, (byte)128, (byte)0, (byte)255),
            "the inner stroke of a double border is painted");
        bmp.GetPixel(60, 14).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255),
            "the middle third of a double border stays empty");
    }

    [TestMethod]
    public void Mixed_per_side_styles_paint_each_side_independently()
    {
        // Top: solid red 4. Right: dashed blue 4. Bottom: dotted green 6.
        // Left: double red 9. Each side must show its own pattern.
        var box = new LayoutRect(20, 20, 120, 80);
        var list = new PaintList();
        list.Add(new DrawBorderSides(
            box, CornerRadii.None,
            TopWidth: 4, RightWidth: 4, BottomWidth: 6, LeftWidth: 9,
            Red, Blue, Green, Red,
            BorderSideStyle.Solid, BorderSideStyle.Dashed, BorderSideStyle.Dotted, BorderSideStyle.Double));
        using var bmp = Render(list, 200, 140);

        // Top side: solid — three probes along the band are all red.
        foreach (var px in new[] { 40, 80, 120 })
        {
            bmp.GetPixel(px, 22).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255),
                "a solid top side has no gaps");
        }

        // Right side: dashed — the band column has blue AND white pixels
        // strictly between the corner bands.
        int bluePixels = 0, whiteOnRight = 0;
        for (var py = 26; py < 92; py++)
        {
            var (r, g, b, _) = bmp.GetPixel(138, py);
            if (b == 255 && r == 0)
            {
                bluePixels++;
            }

            if (r == 255 && g == 255 && b == 255)
            {
                whiteOnRight++;
            }
        }
        bluePixels.Should().BeGreaterThan(0, "a dashed side paints dashes");
        whiteOnRight.Should().BeGreaterThan(0, "a dashed side leaves gaps");

        // Bottom side: dotted — green and white pixels along the row through
        // the dot centres (y = 100 - 3 = 97).
        int greenPixels = 0, whiteOnBottom = 0;
        for (var px = 32; px < 128; px++)
        {
            var (r, g, b, _) = bmp.GetPixel(px, 97);
            if (g == 128 && r == 0)
            {
                greenPixels++;
            }

            if (r == 255 && g == 255 && b == 255)
            {
                whiteOnBottom++;
            }
        }
        greenPixels.Should().BeGreaterThan(0, "a dotted side paints dots");
        whiteOnBottom.Should().BeGreaterThan(0, "a dotted side leaves space between dots");

        // Left side: double — outer stroke at x 21, hollow middle at x 24,
        // inner stroke at x 27 (bands [20,23] and [26,29]).
        bmp.GetPixel(21, 60).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
        bmp.GetPixel(24, 60).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255));
        bmp.GetPixel(27, 60).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255));
    }

    // ----- backend pixel probes: outline ring --------------------------------

    [TestMethod]
    public void Outline_ring_paints_outside_the_border_edge_at_the_offset()
    {
        // End to end: a 100x50 box at (20,20) with `outline: 4px solid red;
        // outline-offset: 6px`. Ring inner edge = box expanded by 6 →
        // x [14,126]; ring band x [10,14] on the left. The gap between ring
        // and border edge ([14,20]) stays white.
        var dl = BuildList(
            "<body style=\"margin:0\"><div style=\"margin:20px;width:100px;height:50px;" +
            "background-color:#0000ff;outline:4px solid #ff0000;outline-offset:6px\"></div></body>",
            new LayoutSize(200, 120));
        using var bmp = Render(dl, 200, 120);

        bmp.GetPixel(12, 45).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255),
            "the outline ring sits outline-offset + width outside the border edge");
        bmp.GetPixel(17, 45).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255),
            "the offset gap between border edge and ring stays unpainted");
        bmp.GetPixel(70, 45).Should().Be(((byte)0, (byte)0, (byte)255, (byte)255),
            "the box itself still paints its background");
        bmp.GetPixel(6, 45).Should().Be(((byte)255, (byte)255, (byte)255, (byte)255),
            "nothing paints outside the ring's outer edge");
    }

    [TestMethod]
    public void Negative_outline_offset_pulls_the_ring_inside_the_border_box()
    {
        // `outline-offset: -12px` on the same 100x50 box at (20,20): ring
        // inner edge = box shrunk by 12 → x [32,108]; ring band x [28,32].
        var dl = BuildList(
            "<body style=\"margin:0\"><div style=\"margin:20px;width:100px;height:50px;" +
            "background-color:#eeeeee;outline:4px solid #ff0000;outline-offset:-12px\"></div></body>",
            new LayoutSize(200, 120));
        using var bmp = Render(dl, 200, 120);

        bmp.GetPixel(30, 45).Should().Be(((byte)255, (byte)0, (byte)0, (byte)255),
            "a negative offset pulls the ring inside the border box");
        bmp.GetPixel(24, 45).Should().Be(((byte)238, (byte)238, (byte)238, (byte)255),
            "between the border edge and the inset ring the background shows");
        bmp.GetPixel(70, 45).Should().Be(((byte)238, (byte)238, (byte)238, (byte)255),
            "the ring interior shows the background");
    }

    // ----- builder emission ---------------------------------------------------

    [TestMethod]
    public void Solid_outline_emits_a_centre_line_stroke_ring()
    {
        var dl = BuildList(
            "<body style=\"margin:0\"><div style=\"width:100px;height:50px;" +
            "outline:4px solid #ff0000;outline-offset:6px\"></div></body>",
            new LayoutSize(400, 300));

        var ring = dl.Items.OfType<StrokeRoundedRect>().Single();
        ring.Width.Should().Be(4);
        ring.Color.Should().Be(Red);
        // Centre rect = border box expanded by offset + width/2 = 8.
        ring.Bounds.Width.Should().Be(100 + 2 * 8);
        ring.Bounds.Height.Should().Be(50 + 2 * 8);
        ring.Bounds.X.Should().Be(-8);
        ring.Bounds.Y.Should().Be(-8);
    }

    [TestMethod]
    public void Huge_negative_outline_offset_is_clamped_against_inversion()
    {
        // min(100,50)/2 = 25 caps the negative offset: centre rect width =
        // 100 + 2*(-25) + 4 = 54, height = 50 + 2*(-25) + 4 = 4.
        var dl = BuildList(
            "<body style=\"margin:0\"><div style=\"width:100px;height:50px;" +
            "outline:4px solid #ff0000;outline-offset:-200px\"></div></body>",
            new LayoutSize(400, 300));

        var ring = dl.Items.OfType<StrokeRoundedRect>().Single();
        ring.Bounds.Width.Should().Be(54);
        ring.Bounds.Height.Should().Be(4);
        ring.Bounds.Width.Should().BeGreaterThan(0, "the clamped ring must never invert");
    }

    [TestMethod]
    public void Outline_auto_draws_a_solid_focus_ring_in_the_text_color()
    {
        var dl = BuildList(
            "<body style=\"margin:0\"><div style=\"width:100px;height:50px;color:#0000ff;" +
            "outline-style:auto;outline-width:2px\"></div></body>",
            new LayoutSize(400, 300));

        var ring = dl.Items.OfType<StrokeRoundedRect>().Single();
        ring.Width.Should().Be(2);
        ring.Color.Should().Be(Blue, "outline-color:auto falls back to the element's color");
    }

    [TestMethod]
    public void Dashed_outline_reuses_the_border_side_machinery_on_the_expanded_box()
    {
        var dl = BuildList(
            "<body style=\"margin:0\"><div style=\"width:100px;height:50px;" +
            "outline:4px dashed #ff0000;outline-offset:2px\"></div></body>",
            new LayoutSize(400, 300));

        var ring = dl.Items.OfType<DrawBorderSides>().Single();
        ring.TopStyle.Should().Be(BorderSideStyle.Dashed);
        ring.TopWidth.Should().Be(4);
        // Outer box = border box expanded by offset + width = 6 per side.
        ring.Bounds.Width.Should().Be(100 + 2 * 6);
        ring.Bounds.Height.Should().Be(50 + 2 * 6);
    }

    [TestMethod]
    public void Outline_is_emitted_before_the_elements_own_overflow_clip_bracket()
    {
        // The element's own overflow clip wraps only its children; the outline
        // must sit OUTSIDE that bracket so the element cannot clip its own ring.
        var dl = BuildList(
            "<body style=\"margin:0\"><div style=\"width:100px;height:50px;overflow:hidden;" +
            "outline:4px solid #ff0000\"><span>content</span></div></body>",
            new LayoutSize(400, 300));

        var items = dl.Items;
        var ringIndex = -1;
        var clipIndex = -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (ringIndex < 0 && items[i] is StrokeRoundedRect)
            {
                ringIndex = i;
            }

            if (clipIndex < 0 && items[i] is PushClip)
            {
                clipIndex = i;
            }
        }
        ringIndex.Should().BeGreaterThanOrEqualTo(0, "the outline ring is emitted");
        clipIndex.Should().BeGreaterThanOrEqualTo(0, "overflow:hidden emits a clip bracket");
        ringIndex.Should().BeLessThan(clipIndex,
            "the outline must not be clipped by the element's own overflow clip");
    }

    [TestMethod]
    public void Dashed_border_routes_through_the_per_side_primitive()
    {
        var dl = BuildList(
            "<body style=\"margin:0\"><div style=\"width:100px;height:50px;" +
            "border:4px dashed #ff0000\"></div></body>",
            new LayoutSize(400, 300));

        var sides = dl.Items.OfType<DrawBorderSides>().Single();
        sides.TopStyle.Should().Be(BorderSideStyle.Dashed);
        sides.RightStyle.Should().Be(BorderSideStyle.Dashed);
        sides.BottomStyle.Should().Be(BorderSideStyle.Dashed);
        sides.LeftStyle.Should().Be(BorderSideStyle.Dashed);
        sides.TopWidth.Should().Be(4);
        sides.Bounds.Width.Should().Be(108, "the border box includes the borders");
    }

    [TestMethod]
    public void Mixed_side_styles_route_through_the_per_side_primitive()
    {
        var dl = BuildList(
            "<body style=\"margin:0\"><div style=\"width:100px;height:50px;" +
            "border-top:4px solid #ff0000;border-bottom:6px dotted #0000ff\"></div></body>",
            new LayoutSize(400, 300));

        var sides = dl.Items.OfType<DrawBorderSides>().Single();
        sides.TopStyle.Should().Be(BorderSideStyle.Solid);
        sides.BottomStyle.Should().Be(BorderSideStyle.Dotted);
        sides.LeftWidth.Should().Be(0, "no left border was declared");
        sides.RightWidth.Should().Be(0, "no right border was declared");
        sides.BottomColor.Should().Be(Blue);
    }

    [TestMethod]
    public void All_solid_borders_keep_the_existing_fill_rect_path()
    {
        var dl = BuildList(
            "<body style=\"margin:0\"><div style=\"width:100px;height:50px;" +
            "border:4px solid #ff0000\"></div></body>",
            new LayoutSize(400, 300));

        dl.Items.OfType<DrawBorderSides>().Should().BeEmpty(
            "solid borders stay on the existing fast path");
        dl.Items.OfType<FillRect>().Should().Contain(r => r.Color == Red);
    }
}
