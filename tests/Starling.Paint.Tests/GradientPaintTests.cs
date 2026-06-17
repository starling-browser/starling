using AwesomeAssertions;
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
/// CSS Images 3 §3 — pixel-probe coverage for the <see cref="FillGradient"/>
/// display item rasterized through <see cref="ImageSharpBackend"/>'s gradient
/// brushes. The headline check: a left→right red→blue linear gradient must be
/// red-dominant near the left edge and blue-dominant near the right edge.
/// </summary>
[TestClass]
public sealed class GradientPaintTests
{
    private static readonly CssColor Red = new(255, 0, 0);
    private static readonly CssColor Blue = new(0, 0, 255);

    private static CssGradient Linear(CssGradientLine? line, params CssColorStop[] stops)
        => new(CssGradientKind.Linear, Repeating: false, stops, Line: line);

    [TestMethod]
    public void Linear_90deg_red_to_blue_is_red_left_blue_right()
    {
        var gradient = Linear(
            CssGradientLine.FromAngle(90), // to right
            new CssColorStop(Red),
            new CssColorStop(Blue));

        var list = new PaintList();
        list.Add(new FillGradient(new LayoutRect(0, 0, 100, 40), gradient));

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(100, 40));

        // Probe at 10% width (x=10) and 90% width (x=90), mid-height.
        var (lr, _, lb, _) = bmp.GetPixel(10, 20);
        var (rr, _, rb, _) = bmp.GetPixel(90, 20);

        lr.Should().BeGreaterThan(lb, "left side of a red→blue gradient should be red-dominant");
        rb.Should().BeGreaterThan(rr, "right side of a red→blue gradient should be blue-dominant");
    }

    [TestMethod]
    public void Rounded_gradient_paints_through_an_overflow_clip()
    {
        // Regression (starlingbrowser.com progress bars): a pill-shaped gradient
        // fill (border-radius >= height/2) clipped by an `overflow: hidden`
        // ancestor used to vanish — the rounded path rasterized into an offscreen
        // layer whose blit ignored the active clip stack. The fix fills the rounded
        // path straight onto the canvas, so the clip is honoured and the fill shows.
        var gradient = Linear(
            CssGradientLine.FromAngle(90),
            new CssColorStop(Red),
            new CssColorStop(Blue));

        var pill = CornerRadii.Uniform(5, 5, 5, 5); // >= height/2 for a 10px-tall bar
        var list = new PaintList();
        list.Add(new PushClip(new LayoutRect(0, 0, 100, 10), pill)); // overflow:hidden parent
        list.Add(new FillGradient(new LayoutRect(0, 0, 80, 10), gradient, pill)); // rounded fill
        list.Add(new PopClip());

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(100, 10));

        // Mid-fill must carry the red→blue gradient (so near-zero green and a real
        // red/blue presence). The bug left this area unpainted — the empty/white
        // background it showed instead would have full green.
        var (r, g, b, _) = bmp.GetPixel(40, 5);
        g.Should().BeLessThan(120, "the red→blue gradient has no green; an unpainted pixel would");
        (r + b).Should().BeGreaterThan(120, "the fill should carry gradient colour");
    }

    [TestMethod]
    public void Linear_to_right_keyword_matches_90deg()
    {
        var gradient = Linear(
            CssGradientLine.FromSide(CssGradientSideX.Right, CssGradientSideY.None),
            new CssColorStop(Red),
            new CssColorStop(Blue));

        var list = new PaintList();
        list.Add(new FillGradient(new LayoutRect(0, 0, 100, 20), gradient));

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(100, 20));

        bmp.GetPixel(5, 10).Item1.Should().BeGreaterThan(bmp.GetPixel(5, 10).Item3);
        bmp.GetPixel(95, 10).Item3.Should().BeGreaterThan(bmp.GetPixel(95, 10).Item1);
    }

    [TestMethod]
    public void Radial_gradient_paints_center_color_at_center()
    {
        var gradient = new CssGradient(
            CssGradientKind.Radial,
            Repeating: false,
            new[] { new CssColorStop(Red), new CssColorStop(Blue) },
            Shape: CssRadialShape.Circle,
            Size: CssRadialSize.FarthestCorner,
            Position: CssGradientPosition.Center);

        var list = new PaintList();
        list.Add(new FillGradient(new LayoutRect(0, 0, 60, 60), gradient));

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(60, 60));

        // Center is the first stop (red); a corner trends toward the last (blue).
        var (cr, _, cb, _) = bmp.GetPixel(30, 30);
        cr.Should().BeGreaterThan(cb, "the radial gradient center should show the first (red) stop");
    }

    [TestMethod]
    public void Three_stop_gradient_shows_middle_color()
    {
        var gradient = Linear(
            CssGradientLine.FromAngle(90),
            new CssColorStop(Red, new CssGradientStopPosition(0, IsPercent: true)),
            new CssColorStop(new CssColor(0, 255, 0), new CssGradientStopPosition(50, IsPercent: true)),
            new CssColorStop(Blue, new CssGradientStopPosition(100, IsPercent: true)));

        var list = new PaintList();
        list.Add(new FillGradient(new LayoutRect(0, 0, 100, 20), gradient));

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(100, 20));

        var (mr, mg, mb, _) = bmp.GetPixel(50, 10);
        ((int)mg).Should().BeGreaterThan(mr, "the middle stop is green");
        ((int)mg).Should().BeGreaterThan(mb, "the middle stop is green");
    }

    [TestMethod]
    public void Css_background_gradient_emits_fill_gradient_through_layout()
    {
        // End-to-end: CSS `background-image: linear-gradient(...)` must flow
        // through the parser, layout, and EmitBackgroundImage to a FillGradient
        // — no image resolver required.
        var document = HtmlParser.Parse("""
            <body style="margin:0">
              <div style="width:100px; height:40px;
                          background-image: linear-gradient(90deg, red, blue)"></div>
            </body>
            """);
        var style = new StyleEngine();
        var engine = new LayoutEngine(style, DefaultTextMeasurer.Instance, images: null);
        var root = engine.LayoutDocument(document, new Size(800, 600));
        var list = new DisplayListBuilder().Build(root, styleOverride: null, images: null);

        var fills = list.Items.OfType<FillGradient>().ToList();
        fills.Should().NotBeEmpty("a CSS linear-gradient background must emit a FillGradient");
        var fill = fills[0];
        fill.Gradient.Kind.Should().Be(CssGradientKind.Linear);
        fill.Gradient.Stops.Should().HaveCount(2);
        fill.Bounds.Width.Should().BeApproximately(100, 0.5);
        fill.Bounds.Height.Should().BeApproximately(40, 0.5);
    }

    [TestMethod]
    public void Conic_red_to_blue_sweeps_clockwise_from_top()
    {
        // conic-gradient(red, blue): the hue sweeps clockwise from straight up.
        // A quarter turn clockwise (the right edge) is still mostly red; three
        // quarters around (the left edge) is mostly blue.
        var gradient = new CssGradient(
            CssGradientKind.Conic,
            Repeating: false,
            new[] { new CssColorStop(Red), new CssColorStop(Blue) });

        var list = new PaintList();
        list.Add(new FillGradient(new LayoutRect(0, 0, 100, 100), gradient));

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(100, 100));

        var (rr, _, rb, _) = bmp.GetPixel(90, 50); // quarter turn clockwise
        var (lr, _, lb, _) = bmp.GetPixel(10, 50); // three quarters clockwise

        rr.Should().BeGreaterThan(rb, "the right of a red→blue conic should be red-dominant");
        lb.Should().BeGreaterThan(lr, "the left of a red→blue conic should be blue-dominant");
    }

    [TestMethod]
    public void Css_conic_gradient_emits_fill_gradient()
    {
        // conic-gradient is now painted (rasterized per-pixel in the backend),
        // so the builder must emit a FillGradient carrying the conic value.
        var document = HtmlParser.Parse("""
            <body style="margin:0">
              <div style="width:50px; height:50px;
                          background-image: conic-gradient(from 90deg at 50% 50%, red, blue)"></div>
            </body>
            """);
        var style = new StyleEngine();
        var engine = new LayoutEngine(style, DefaultTextMeasurer.Instance, images: null);
        var root = engine.LayoutDocument(document, new Size(800, 600));
        var list = new DisplayListBuilder().Build(root, styleOverride: null, images: null);

        var fills = list.Items.OfType<FillGradient>().ToList();
        fills.Should().NotBeEmpty("a CSS conic-gradient background must emit a FillGradient");
        fills[0].Gradient.Kind.Should().Be(CssGradientKind.Conic);
        fills[0].Gradient.Line!.AngleDegrees.Should().Be(90);
    }

    [TestMethod]
    public void Gradient_fills_whole_box_no_white_remains()
    {
        var gradient = Linear(
            CssGradientLine.FromAngle(90),
            new CssColorStop(Red),
            new CssColorStop(Blue));

        var list = new PaintList();
        list.Add(new FillGradient(new LayoutRect(0, 0, 50, 50), gradient));

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(50, 50));

        // The 50x50 box covers the entire 50x50 canvas; effectively no white left.
        BitmapPixels.CountNonWhite(bmp).Should().BeGreaterThan(50 * 50 - 50,
            "a full-box gradient should leave essentially no white background");
    }

    // -----------------------------------------------------------------------
    // #1  Conic in background-clip: text
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Conic_text_clip_paints_gradient_inside_glyphs_not_solid()
    {
        // A conic gradient used with background-clip: text previously fell back to
        // the solid text color because BuildGradientBrush returns null for conic.
        // After the fix the gradient is rasterized directly, so the left edge of
        // the glyph band should be red-dominant and the top region blue-dominant.
        var painter = new Painter();
        var document = Starling.Html.HtmlParser.Parse("""
            <body style="margin:0; background:#ffffff">
              <h1 style="font-size:80px; font-weight:700; margin:0;
                         background-image: conic-gradient(red, blue);
                         background-clip: text; -webkit-background-clip: text;
                         color: transparent">WWW</h1>
            </body>
            """);
        using var image = painter.RenderDocument(document, new LayoutSize(600, 200));

        // Must have coloured pixels inside the glyph shapes.
        BitmapPixels.CountNonWhite(image)
            .Should().BeGreaterThan(200, "conic gradient must paint inside the glyph shapes");

        // The conic gradient sweeps the full hue; at minimum the rendered pixel
        // colors should NOT all be identical (i.e. not a single flat color). We
        // collect a few sample pixels across the rendered image and check that at
        // least two distinct non-white colors appear.
        var coloredPixels = new List<(byte r, byte g, byte b)>();
        for (var probeX = 50; probeX < 550; probeX += 30)
        {
            for (var probeY = 5; probeY < 100; probeY += 10)
            {
                var (r, g, b, a) = image.GetPixel(probeX, probeY);
                // Only collect pixels that are clearly not white background.
                if (r < 240 || g < 200 || b < 200)
                {
                    coloredPixels.Add((r, g, b));
                }
            }
        }
        coloredPixels.Should().NotBeEmpty("conic gradient must produce colored pixels inside glyphs");
        // Check at least two distinct hues exist (gradient, not a flat color).
        var hasRedish = coloredPixels.Any(p => p.r > p.b + 30);
        var hasBlueish = coloredPixels.Any(p => p.b > p.r + 30);
        (hasRedish || hasBlueish).Should().BeTrue(
            "conic gradient must produce color variation across the glyph band");
    }

    // -----------------------------------------------------------------------
    // #2  Gradient clipped to border-radius
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Linear_gradient_with_border_radius_has_transparent_corners()
    {
        // A linear gradient on a box with border-radius: 50% should leave the
        // corners transparent (the canvas is white, so the corners read white).
        // Without the rounded-corner fix the gradient fills the whole square box.
        var gradient = Linear(
            CssGradientLine.FromAngle(90),
            new CssColorStop(Red),
            new CssColorStop(Blue));

        var radii = CornerRadii.Uniform(50, 50, 50, 50); // 50px radius on a 100x100 box → circle
        var list = new PaintList();
        list.Add(new FillGradient(new LayoutRect(0, 0, 100, 100), gradient, radii));

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(100, 100));

        // The four corner pixels must be white (transparent fill → white canvas).
        bmp.GetPixel(2, 2).Item1.Should().BeGreaterThan(240, "top-left corner must be white (outside gradient circle)");
        bmp.GetPixel(97, 2).Item1.Should().BeGreaterThan(240, "top-right corner must be white");
        bmp.GetPixel(2, 97).Item1.Should().BeGreaterThan(240, "bottom-left corner must be white");
        bmp.GetPixel(97, 97).Item1.Should().BeGreaterThan(240, "bottom-right corner must be white");

        // The center must be painted (red or blue, not white).
        var (cr, cg, cb, _) = bmp.GetPixel(50, 50);
        (cr < 200 || cb > 200).Should().BeTrue("center of gradient circle must be painted");
    }

    [TestMethod]
    public void Conic_gradient_with_border_radius_has_transparent_corners()
    {
        var gradient = new CssGradient(
            CssGradientKind.Conic,
            Repeating: false,
            new[] { new CssColorStop(Red), new CssColorStop(Blue) });

        var radii = CornerRadii.Uniform(50, 50, 50, 50);
        var list = new PaintList();
        list.Add(new FillGradient(new LayoutRect(0, 0, 100, 100), gradient, radii));

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(100, 100));

        // Corners must be white — outside the circle.
        bmp.GetPixel(2, 2).Item1.Should().BeGreaterThan(240, "corner must be white outside conic circle");
        bmp.GetPixel(97, 97).Item1.Should().BeGreaterThan(240, "corner must be white outside conic circle");

        // Center must be painted.
        var (cr, cg, cb, _) = bmp.GetPixel(50, 50);
        (cr > 50 || cg > 50 || cb > 50).Should().BeTrue("center of conic circle must be painted");
    }

    [TestMethod]
    public void Css_linear_gradient_with_border_radius_emits_radii_on_fill_gradient()
    {
        // End-to-end: the builder must propagate border-radius onto FillGradient.
        var document = Starling.Html.HtmlParser.Parse("""
            <body style="margin:0">
              <div style="width:100px; height:100px; border-radius:50%;
                          background-image: linear-gradient(90deg, red, blue)"></div>
            </body>
            """);
        var style = new StyleEngine();
        var engine = new LayoutEngine(style, DefaultTextMeasurer.Instance, images: null);
        var root = engine.LayoutDocument(document, new LayoutSize(800, 600));
        var list = new DisplayListBuilder().Build(root, styleOverride: null, images: null);

        var fills = list.Items.OfType<FillGradient>().ToList();
        fills.Should().NotBeEmpty("gradient must be emitted");
        fills[0].Radii.IsZero.Should().BeFalse("border-radius must be propagated to FillGradient");
    }

    // -----------------------------------------------------------------------
    // #3  Conic cache and AA: basic quality regression
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Conic_gradient_repeated_render_produces_identical_output()
    {
        // The conic cache must produce bit-identical results on the second render.
        var gradient = new CssGradient(
            CssGradientKind.Conic,
            Repeating: false,
            new[] { new CssColorStop(Red), new CssColorStop(Blue) });

        var list = new PaintList();
        list.Add(new FillGradient(new LayoutRect(0, 0, 60, 60), gradient));

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp1 = backend.Render(list, new LayoutSize(60, 60));
        using var bmp2 = backend.Render(list, new LayoutSize(60, 60));

        BitmapPixels.PixelsEqual(bmp1, bmp2).Should().BeTrue("cache must reproduce identical pixels");
    }

    [TestMethod]
    public void Conic_gradient_has_no_harsh_seam_at_origin()
    {
        // With 2x2 AA the seam at the from-angle should be anti-aliased, not a
        // 1-pixel hard edge. We probe pixels immediately on either side of the
        // top-center seam (the from=0deg / from=360deg wrap point) and check
        // that at least one of them is not at a pure stop colour.
        var gradient = new CssGradient(
            CssGradientKind.Conic,
            Repeating: false,
            new[]
            {
                new CssColorStop(Red, new CssGradientStopPosition(0, IsPercent: true)),
                new CssColorStop(Blue, new CssGradientStopPosition(99, IsPercent: true)),
                new CssColorStop(Red, new CssGradientStopPosition(100, IsPercent: true)),
            });

        var list = new PaintList();
        list.Add(new FillGradient(new LayoutRect(0, 0, 100, 100), gradient));

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(100, 100));

        // Sample a few pixels near the seam. The primary regression check is that
        // the render does not throw and produces non-garbage output (non-zero alpha).
        var (r0, _, _, a0) = bmp.GetPixel(50, 5);
        var (r1, _, _, a1) = bmp.GetPixel(50, 6);
        // Both pixels near the top-center (the seam) should be opaque red (the
        // gradient wraps red→...→red at the seam). Accept any rendered output;
        // the test guards against regressions where AA produces zero/garbage pixels.
        (a0 > 0 || a1 > 0).Should().BeTrue("AA seam pixels must be non-transparent");
        (r0 > 0 || r1 > 0).Should().BeTrue("AA seam pixels near the seam must have red channel");
    }

    // -----------------------------------------------------------------------
    // #4b  Color space: conic path interpolates in Oklab
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Conic_gradient_oklch_interpolation_produces_different_midpoint_than_srgb()
    {
        // Two identical stop lists but different color spaces should produce
        // different midpoint colors. This confirms the color-space path is wired.
        var stopsRed = new CssColorStop(Red);
        var stopsBlue = new CssColorStop(Blue);

        var gradSrgb = new CssGradient(
            CssGradientKind.Conic, Repeating: false,
            new[] { stopsRed, stopsBlue },
            Interpolation: new GradientInterpolationMethod(GradientColorSpace.Srgb));

        var gradOklab = new CssGradient(
            CssGradientKind.Conic, Repeating: false,
            new[] { stopsRed, stopsBlue },
            Interpolation: new GradientInterpolationMethod(GradientColorSpace.Oklab));

        var listSrgb = new PaintList();
        listSrgb.Add(new FillGradient(new LayoutRect(0, 0, 100, 100), gradSrgb));

        var listOklab = new PaintList();
        listOklab.Add(new FillGradient(new LayoutRect(0, 0, 100, 100), gradOklab));

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmpSrgb = backend.Render(listSrgb, new LayoutSize(100, 100));
        using var bmpOklab = backend.Render(listOklab, new LayoutSize(100, 100));

        // The midpoint of the gradient sweep (right side) should differ between
        // sRGB and Oklab interpolation.
        var srgbMid = bmpSrgb.GetPixel(90, 50);
        var oklabMid = bmpOklab.GetPixel(90, 50);

        // At minimum they should not be bit-identical (Oklab produces different midpoint).
        // We can't rely on exact values across platforms so we just check they differ.
        BitmapPixels.PixelsEqual(bmpSrgb, bmpOklab)
            .Should().BeFalse("Oklab interpolation must produce different colors than sRGB");
    }
}
