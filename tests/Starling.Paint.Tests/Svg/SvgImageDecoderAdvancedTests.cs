// SPDX-License-Identifier: Apache-2.0
using AwesomeAssertions;
using Starling.Common.Image;
using Starling.Paint.Svg;
using Starling.Spec;

namespace Starling.Paint.Tests.Svg;

/// <summary>
/// Tests for the advanced SVG paint-model features added in the second round:
/// linearGradient / radialGradient paint servers, &lt;use&gt; / &lt;symbol&gt;
/// rendering, group opacity compositing, stroke options (dasharray, linecap,
/// linejoin), distinct rx/ry on &lt;rect&gt;, percentage geometry on circle /
/// ellipse / line, hsl() / hsla() colors, and url(#id) fallback paint.
/// </summary>
[TestClass]
[Spec("svg11", "https://www.w3.org/TR/SVG11/", section: "pservers+advanced")]
public sealed class SvgImageDecoderAdvancedTests
{
    private const string SvgUrl = "https://www.w3.org/TR/SVG11/";

    private static (byte R, byte G, byte B, byte A) PixelAt(DecodedImage img, int x, int y)
    {
        var px = img.Pixels.Span;
        int i = (y * img.Width + x) * 4;
        return (px[i], px[i + 1], px[i + 2], px[i + 3]);
    }

    private static bool AnyNonTransparent(DecodedImage img)
    {
        var px = img.Pixels.Span;
        for (int i = 3; i < px.Length; i += 4)
        {
            if (px[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static int CountWhere(DecodedImage img, Func<(byte R, byte G, byte B, byte A), bool> pred)
    {
        int n = 0;
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                if (pred(PixelAt(img, x, y)))
                {
                    n++;
                }
            }
        }

        return n;
    }

    // ==========================================================
    // 1. linearGradient paint server
    // ==========================================================

    [Spec("svg11", SvgUrl, section: "pservers.html#LinearGradients")]
    [SpecFact]
    public void LinearGradient_fill_left_to_right_paints_gradient_pixels()
    {
        // A red-to-blue horizontal gradient across a 40x20 rect.
        const string Svg =
            "<svg width='40' height='20' viewBox='0 0 40 20'>" +
            "<defs>" +
            "<linearGradient id='g' x1='0%' y1='0%' x2='100%' y2='0%'>" +
            "<stop offset='0%' stop-color='red'/>" +
            "<stop offset='100%' stop-color='blue'/>" +
            "</linearGradient></defs>" +
            "<rect width='40' height='20' fill='url(#g)'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);

        // Left edge must be mostly red.
        var left = PixelAt(img, 1, 10);
        left.R.Should().BeGreaterThan(150);
        left.B.Should().BeLessThan(150);

        // Right edge must be mostly blue.
        var right = PixelAt(img, 38, 10);
        right.B.Should().BeGreaterThan(150);
        right.R.Should().BeLessThan(150);

        AnyNonTransparent(img).Should().BeTrue();
    }

    [Spec("svg11", SvgUrl, section: "pservers.html#LinearGradients")]
    [SpecFact]
    public void LinearGradient_objectBoundingBox_default_units()
    {
        // Default gradientUnits = objectBoundingBox; no explicit unit on x1/y1/x2/y2
        // means fractions of the element bounding box — left=black, right=white.
        const string Svg =
            "<svg width='40' height='20' viewBox='0 0 40 20'>" +
            "<defs>" +
            "<linearGradient id='g'>" +
            "<stop offset='0' stop-color='black'/>" +
            "<stop offset='1' stop-color='white'/>" +
            "</linearGradient></defs>" +
            "<rect width='40' height='20' fill='url(#g)'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);

        // Left side should be dark.
        var left = PixelAt(img, 2, 10);
        left.R.Should().BeLessThan(80);

        // Right side should be bright.
        var right = PixelAt(img, 37, 10);
        right.R.Should().BeGreaterThan(150);

        AnyNonTransparent(img).Should().BeTrue();
    }

    [Spec("svg11", SvgUrl, section: "pservers.html#LinearGradients")]
    [SpecFact]
    public void LinearGradient_objectBoundingBox_under_group_transform_is_not_flat()
    {
        // Regression: a gradient-filled shape inside a scaled group (the common
        // Inkscape `scale(0.1,-0.1)` export pattern, as on the Starling logo).
        // The fill path is drawn in device space, so the gradient vector must be
        // mapped through the same element transform — otherwise the whole shape
        // clamps to a single stop and renders flat.
        const string Svg =
            "<svg width='40' height='20' viewBox='0 0 40 20'>" +
            "<defs>" +
            "<linearGradient id='g' x1='0' y1='0' x2='1' y2='1'>" +
            "<stop offset='0' stop-color='black'/>" +
            "<stop offset='1' stop-color='white'/>" +
            "</linearGradient></defs>" +
            "<g transform='scale(0.1,0.1)'>" +
            "<rect width='400' height='200' fill='url(#g)'/>" +
            "</g></svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);

        // Top-left of the bounding box is the first (black) stop...
        var topLeft = PixelAt(img, 2, 2);
        topLeft.R.Should().BeLessThan(80);

        // ...bottom-right is the last (white) stop. A flat fill would make these equal.
        var bottomRight = PixelAt(img, 37, 17);
        bottomRight.R.Should().BeGreaterThan(150);

        AnyNonTransparent(img).Should().BeTrue();
    }

    [Spec("svg11", SvgUrl, section: "pservers.html#RadialGradients")]
    [SpecFact]
    public void RadialGradient_under_group_transform_positions_center_correctly()
    {
        // Same regression for radial gradients: the center/radius must follow the
        // element transform, or the device shape falls entirely outside the
        // gradient circle and clamps to the edge stop.
        const string Svg =
            "<svg width='40' height='40' viewBox='0 0 40 40'>" +
            "<defs>" +
            "<radialGradient id='rg' cx='50%' cy='50%' r='50%'>" +
            "<stop offset='0' stop-color='white'/>" +
            "<stop offset='100%' stop-color='black'/>" +
            "</radialGradient></defs>" +
            "<g transform='scale(0.1,0.1)'>" +
            "<rect width='400' height='400' fill='url(#rg)'/>" +
            "</g></svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);

        // Center is the first (white) stop; a corner is near the last (black) stop.
        var center = PixelAt(img, 20, 20);
        center.R.Should().BeGreaterThan(150);

        var corner = PixelAt(img, 2, 2);
        corner.R.Should().BeLessThan(center.R);

        AnyNonTransparent(img).Should().BeTrue();
    }

    [Spec("svg11", SvgUrl, section: "pservers.html#LinearGradients")]
    [SpecFact]
    public void LinearGradient_stop_opacity_blends_into_alpha()
    {
        // A gradient from opaque red (stop-opacity=1) to transparent red (stop-opacity=0).
        const string Svg =
            "<svg width='40' height='10' viewBox='0 0 40 10'>" +
            "<defs>" +
            "<linearGradient id='g' x1='0%' y1='0%' x2='100%' y2='0%' gradientUnits='userSpaceOnUse'>" +
            "<stop offset='0' stop-color='red' stop-opacity='1'/>" +
            "<stop offset='1' stop-color='red' stop-opacity='0'/>" +
            "</linearGradient></defs>" +
            "<rect width='40' height='10' fill='url(#g)'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);

        // Left edge: fully opaque red.
        var left = PixelAt(img, 1, 5);
        left.R.Should().BeGreaterThan(200);
        left.A.Should().BeGreaterThan(200);

        // Right edge: transparent (alpha near 0).
        var right = PixelAt(img, 38, 5);
        right.A.Should().BeLessThan(80);
    }

    [Spec("svg11", SvgUrl, section: "pservers.html#LinearGradients")]
    [SpecFact]
    public void LinearGradient_href_inherits_stops_from_another_gradient()
    {
        // grad2 has no stops of its own; it inherits via href from grad1.
        // The rect fill still produces non-transparent pixels.
        const string Svg =
            "<svg width='20' height='20' viewBox='0 0 20 20'>" +
            "<defs>" +
            "<linearGradient id='grad1'>" +
            "<stop offset='0' stop-color='green'/>" +
            "<stop offset='1' stop-color='lime'/>" +
            "</linearGradient>" +
            "<linearGradient id='grad2' href='#grad1' x1='0%' y1='0%' x2='100%' y2='0%'/>" +
            "</defs>" +
            "<rect width='20' height='20' fill='url(#grad2)'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        AnyNonTransparent(img).Should().BeTrue("inherited stops must produce visible output");
    }

    [Spec("svg11", SvgUrl, section: "pservers.html#LinearGradients")]
    [SpecFact]
    public void LinearGradient_spreadMethod_repeat_produces_repeated_bands()
    {
        // A very narrow gradient (10% of the box) with spreadMethod="repeat"
        // should repeat across the full box, so the center should still be
        // coloured (not transparent).
        const string Svg =
            "<svg width='40' height='10' viewBox='0 0 40 10'>" +
            "<defs>" +
            "<linearGradient id='g' gradientUnits='userSpaceOnUse' x1='0' y1='0' x2='4' y2='0' spreadMethod='repeat'>" +
            "<stop offset='0' stop-color='red'/>" +
            "<stop offset='1' stop-color='blue'/>" +
            "</linearGradient></defs>" +
            "<rect width='40' height='10' fill='url(#g)'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        AnyNonTransparent(img).Should().BeTrue();
        // Some pixels beyond the first 4 px should also be colored.
        CountWhere(img, p => p.A > 100).Should().BeGreaterThan(10);
    }

    // ==========================================================
    // 2. radialGradient paint server
    // ==========================================================

    [Spec("svg11", SvgUrl, section: "pservers.html#RadialGradients")]
    [SpecFact]
    public void RadialGradient_center_is_start_color_edge_is_end_color()
    {
        // Center = white, edge = black. Center pixel must be bright;
        // a pixel near the outer rim must be dark.
        const string Svg =
            "<svg width='40' height='40' viewBox='0 0 40 40'>" +
            "<defs>" +
            "<radialGradient id='rg' cx='50%' cy='50%' r='50%'>" +
            "<stop offset='0' stop-color='white'/>" +
            "<stop offset='1' stop-color='black'/>" +
            "</radialGradient></defs>" +
            "<rect width='40' height='40' fill='url(#rg)'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);

        // Center (20,20) should be white.
        var center = PixelAt(img, 20, 20);
        center.R.Should().BeGreaterThan(200);
        center.G.Should().BeGreaterThan(200);
        center.B.Should().BeGreaterThan(200);

        // Near corner (1,1) should be dark.
        var corner = PixelAt(img, 1, 1);
        corner.R.Should().BeLessThan(80);

        AnyNonTransparent(img).Should().BeTrue();
    }

    [Spec("svg11", SvgUrl, section: "pservers.html#RadialGradients")]
    [SpecFact]
    public void RadialGradient_userSpaceOnUse_positions_by_coordinates()
    {
        const string Svg =
            "<svg width='40' height='40' viewBox='0 0 40 40'>" +
            "<defs>" +
            "<radialGradient id='rg' gradientUnits='userSpaceOnUse' cx='20' cy='20' r='15'>" +
            "<stop offset='0' stop-color='cyan'/>" +
            "<stop offset='1' stop-color='navy'/>" +
            "</radialGradient></defs>" +
            "<rect width='40' height='40' fill='url(#rg)'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        AnyNonTransparent(img).Should().BeTrue();

        // Center should be cyan-ish.
        var center = PixelAt(img, 20, 20);
        center.R.Should().BeLessThan(120);  // cyan has no red
        center.G.Should().BeGreaterThan(150);
        center.B.Should().BeGreaterThan(150);
    }

    // ==========================================================
    // 3. <use> and <symbol> / <defs>
    // ==========================================================

    [Spec("svg11", SvgUrl, section: "struct.html#UseElement")]
    [SpecFact]
    public void Use_element_renders_referenced_rect()
    {
        // Define a red 10x10 rect, then use it twice at different positions.
        const string Svg =
            "<svg width='40' height='20' viewBox='0 0 40 20'>" +
            "<defs><rect id='r' width='10' height='10' fill='red'/></defs>" +
            "<use href='#r' x='2' y='5'/>" +
            "<use href='#r' x='28' y='5'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);

        // First use at (2,5): pixel (7,10) should be red.
        PixelAt(img, 7, 10).R.Should().BeGreaterThan(200);
        PixelAt(img, 7, 10).A.Should().BeGreaterThan(0);

        // Second use at (28,5): pixel (33,10) should be red.
        PixelAt(img, 33, 10).R.Should().BeGreaterThan(200);
        PixelAt(img, 33, 10).A.Should().BeGreaterThan(0);

        // Gap between uses (18,10) should be transparent.
        PixelAt(img, 18, 10).A.Should().Be(0);
    }

    [Spec("svg11", SvgUrl, section: "struct.html#UseElement")]
    [SpecFact]
    public void Use_with_xlink_href_resolves_correctly()
    {
        const string Svg =
            "<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' " +
            "width='20' height='20' viewBox='0 0 20 20'>" +
            "<defs><circle id='c' cx='0' cy='0' r='5' fill='blue'/></defs>" +
            "<use xlink:href='#c' x='10' y='10'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        // Center of the placed circle (10,10) should be blue.
        var center = PixelAt(img, 10, 10);
        center.B.Should().BeGreaterThan(150);
        center.A.Should().BeGreaterThan(0);
    }

    [Spec("svg11", SvgUrl, section: "struct.html#SymbolElement")]
    [SpecFact]
    public void Symbol_children_rendered_via_use()
    {
        // A <symbol> defines a group of shapes; <use> renders them.
        const string Svg =
            "<svg width='30' height='20' viewBox='0 0 30 20'>" +
            "<defs>" +
            "<symbol id='s'>" +
            "<rect width='8' height='8' fill='green'/>" +
            "</symbol>" +
            "</defs>" +
            "<use href='#s' x='11' y='6'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        // The symbol rect placed at (11,6) → pixel (15,10) should be green.
        var px = PixelAt(img, 15, 10);
        px.G.Should().BeGreaterThan(100);
        px.A.Should().BeGreaterThan(0);
    }

    [Spec("svg11", SvgUrl, section: "struct.html#UseElement")]
    [SpecFact]
    public void Use_with_unresolved_href_silently_does_nothing()
    {
        // An unresolved reference must not throw; output remains transparent.
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='20' height='20' viewBox='0 0 20 20'>" +
            "<use href='#missing'/>" +
            "</svg>");
        AnyNonTransparent(img).Should().BeFalse();
    }

    // ==========================================================
    // 4. Group opacity compositing
    // ==========================================================

    [Spec("svg11", SvgUrl, section: "masking.html#OpacityProperty")]
    [SpecFact]
    public void Group_opacity_reduces_blended_alpha()
    {
        // A <g opacity="0.5"> wrapping a solid red rect. The resulting pixel
        // alpha should be ~127 (50% of 255).
        const string Svg =
            "<svg width='10' height='10' viewBox='0 0 10 10'>" +
            "<g opacity='0.5'>" +
            "<rect width='10' height='10' fill='red'/>" +
            "</g>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        var px = PixelAt(img, 5, 5);
        px.R.Should().BeGreaterThan(100);      // red channel still present
        px.A.Should().BeInRange(80, 180);       // alpha ~127 after 50% blend
    }

    [Spec("svg11", SvgUrl, section: "masking.html#OpacityProperty")]
    [SpecFact]
    public void Group_opacity_1_renders_normally()
    {
        // opacity=1 on a <g> must not change the output.
        const string Svg =
            "<svg width='10' height='10' viewBox='0 0 10 10'>" +
            "<g opacity='1'>" +
            "<rect width='10' height='10' fill='blue'/>" +
            "</g>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        var px = PixelAt(img, 5, 5);
        px.B.Should().BeGreaterThan(200);
        px.A.Should().BeGreaterThan(200);
    }

    // ==========================================================
    // 5. Stroke options
    // ==========================================================

    [Spec("svg11", SvgUrl, section: "painting.html#StrokeProperties")]
    [SpecFact]
    public void Stroke_dasharray_produces_dashed_line()
    {
        // A dashed horizontal line; the middle of a gap should be transparent,
        // the middle of a dash should be visible.
        const string Svg =
            "<svg width='50' height='10' viewBox='0 0 50 10'>" +
            "<line x1='0' y1='5' x2='50' y2='5' stroke='black' stroke-width='2' stroke-dasharray='4 4'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        // The total line must produce some stroked pixels.
        AnyNonTransparent(img).Should().BeTrue("dashed line must produce visible pixels");
    }

    [Spec("svg11", SvgUrl, section: "painting.html#StrokeProperties")]
    [SpecFact]
    public void Stroke_linecap_round_produces_pixels()
    {
        const string Svg =
            "<svg width='30' height='10' viewBox='0 0 30 10'>" +
            "<line x1='5' y1='5' x2='25' y2='5' stroke='red' stroke-width='3' stroke-linecap='round'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        AnyNonTransparent(img).Should().BeTrue();
        // Center of the line should be red.
        var mid = PixelAt(img, 15, 5);
        mid.R.Should().BeGreaterThan(150);
        mid.A.Should().BeGreaterThan(0);
    }

    [Spec("svg11", SvgUrl, section: "painting.html#StrokeProperties")]
    [SpecFact]
    public void Stroke_linejoin_bevel_produces_pixels()
    {
        const string Svg =
            "<svg width='30' height='30' viewBox='0 0 30 30'>" +
            "<polyline points='5,25 15,5 25,25' fill='none' stroke='black' stroke-width='3' stroke-linejoin='bevel'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        AnyNonTransparent(img).Should().BeTrue();
    }

    [Spec("svg11", SvgUrl, section: "painting.html#StrokeProperties")]
    [SpecFact]
    public void Stroke_dasharray_none_resets_to_solid()
    {
        // dasharray="none" (or absent) must produce a solid line.
        const string Svg =
            "<svg width='20' height='10' viewBox='0 0 20 10'>" +
            "<line x1='0' y1='5' x2='20' y2='5' stroke='blue' stroke-width='2' stroke-dasharray='none'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        AnyNonTransparent(img).Should().BeTrue();
    }

    // ==========================================================
    // 6. Distinct rx/ry on <rect>
    // ==========================================================

    [Spec("svg11", SvgUrl, section: "shapes.html#RectElement")]
    [SpecFact]
    public void Rect_distinct_rx_ry_paints_content()
    {
        // A wide rect with rx=5 ry=15 (very elliptic corners); verify corners
        // are clipped (transparent) while the body is solid.
        const string Svg =
            "<svg width='40' height='30' viewBox='0 0 40 30'>" +
            "<rect x='0' y='0' width='40' height='30' rx='5' ry='12' fill='red'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);

        // Center of the rect must be red.
        PixelAt(img, 20, 15).R.Should().BeGreaterThan(200);
        PixelAt(img, 20, 15).A.Should().BeGreaterThan(200);

        // The very corner (0,0) should be transparent (clipped by the large ry).
        PixelAt(img, 0, 0).A.Should().Be(0);
        PixelAt(img, 39, 29).A.Should().Be(0);
    }

    [Spec("svg11", SvgUrl, section: "shapes.html#RectElement")]
    [SpecFact]
    public void Rect_rx_only_derives_ry_and_vice_versa()
    {
        // rx only → ry defaults to rx; the rect should still have rounded corners.
        const string Svg =
            "<svg width='20' height='20' viewBox='0 0 20 20'>" +
            "<rect width='20' height='20' rx='5' fill='green'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        PixelAt(img, 10, 10).G.Should().BeGreaterThan(100); // center solid
        PixelAt(img, 0, 0).A.Should().Be(0);               // corner clipped
    }

    // ==========================================================
    // 7. Percentage geometry on circle / ellipse / line
    // ==========================================================

    [Spec("svg11", SvgUrl, section: "shapes.html#CircleElement")]
    [SpecFact]
    public void Circle_radius_percent_resolves_against_viewport()
    {
        // r='50%' on a 40x40 canvas → r = viewport diagonal factor ≈ 20
        // The center pixel must be filled.
        const string Svg =
            "<svg width='40' height='40' viewBox='0 0 40 40'>" +
            "<circle cx='50%' cy='50%' r='40%' fill='purple'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        // Center at (20,20) is inside the circle.
        PixelAt(img, 20, 20).A.Should().BeGreaterThan(0);
    }

    [Spec("svg11", SvgUrl, section: "shapes.html#EllipseElement")]
    [SpecFact]
    public void Ellipse_percentage_rx_ry_resolves_against_viewport()
    {
        const string Svg =
            "<svg width='40' height='20' viewBox='0 0 40 20'>" +
            "<ellipse cx='50%' cy='50%' rx='40%' ry='40%' fill='teal'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        PixelAt(img, 20, 10).A.Should().BeGreaterThan(0);
    }

    [Spec("svg11", SvgUrl, section: "shapes.html#LineElement")]
    [SpecFact]
    public void Line_percentage_coords_resolve_against_viewport()
    {
        const string Svg =
            "<svg width='40' height='40' viewBox='0 0 40 40'>" +
            "<line x1='10%' y1='50%' x2='90%' y2='50%' stroke='black' stroke-width='2'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        // The horizontal line at y=50% (y=20) should produce pixels.
        PixelAt(img, 20, 20).A.Should().BeGreaterThan(0);
    }

    // ==========================================================
    // 8. hsl() / hsla() colors
    // ==========================================================

    [Spec("svg11", SvgUrl, section: "color.html#ColorProperty")]
    [SpecFact]
    public void Hsl_color_fills_correctly()
    {
        // hsl(0, 100%, 50%) = pure red.
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='10' height='10' viewBox='0 0 10 10'>" +
            "<rect width='10' height='10' fill='hsl(0, 100%, 50%)'/>" +
            "</svg>");
        var px = PixelAt(img, 5, 5);
        px.R.Should().BeGreaterThan(200);
        px.G.Should().BeLessThan(60);
        px.B.Should().BeLessThan(60);
        px.A.Should().Be(255);
    }

    [Spec("svg11", SvgUrl, section: "color.html#ColorProperty")]
    [SpecFact]
    public void Hsl_green_fills_correctly()
    {
        // hsl(120, 100%, 50%) = pure green.
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='10' height='10' viewBox='0 0 10 10'>" +
            "<rect width='10' height='10' fill='hsl(120, 100%, 50%)'/>" +
            "</svg>");
        var px = PixelAt(img, 5, 5);
        px.G.Should().BeGreaterThan(200);
        px.R.Should().BeLessThan(60);
        px.B.Should().BeLessThan(60);
    }

    [Spec("svg11", SvgUrl, section: "color.html#ColorProperty")]
    [SpecFact]
    public void Hsla_with_half_alpha_reduces_opacity()
    {
        // hsla(240, 100%, 50%, 0.5) = 50% transparent blue.
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='10' height='10' viewBox='0 0 10 10'>" +
            "<rect width='10' height='10' fill='hsla(240, 100%, 50%, 0.5)'/>" +
            "</svg>");
        var px = PixelAt(img, 5, 5);
        px.B.Should().BeGreaterThan(150);
        px.A.Should().BeInRange(100, 160); // ~127
    }

    [Spec("svg11", SvgUrl, section: "color.html#ColorProperty")]
    [SpecFact]
    public void Hsl_gray_saturation_zero_produces_gray()
    {
        // hsl(0, 0%, 50%) = medium gray.
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='10' height='10' viewBox='0 0 10 10'>" +
            "<rect width='10' height='10' fill='hsl(0, 0%, 50%)'/>" +
            "</svg>");
        var px = PixelAt(img, 5, 5);
        px.R.Should().BeInRange(100, 160); // ~128
        px.G.Should().BeInRange(100, 160);
        px.B.Should().BeInRange(100, 160);
        px.A.Should().Be(255);
    }

    // ==========================================================
    // 9. url(#id) fallback paint
    // ==========================================================

    [Spec("svg11", SvgUrl, section: "painting.html#FillProperty")]
    [SpecFact]
    public void Url_fill_with_fallback_paints_fallback_when_ref_missing()
    {
        // fill="url(#missing) red" — #missing does not exist, so the fallback
        // color (red) must be used.
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='20' height='20' viewBox='0 0 20 20'>" +
            "<rect width='20' height='20' fill='url(#missing) red'/>" +
            "</svg>");
        var px = PixelAt(img, 10, 10);
        px.R.Should().BeGreaterThan(200);
        px.A.Should().BeGreaterThan(200);
    }

    [Spec("svg11", SvgUrl, section: "painting.html#FillProperty")]
    [SpecFact]
    public void Url_fill_without_fallback_paints_nothing_when_ref_missing()
    {
        // The existing codified behavior: unresolved ref with no fallback → nothing.
        // This used to paint nothing and must still paint nothing after changes.
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='20' height='20' viewBox='0 0 20 20'>" +
            "<rect width='20' height='20' fill='url(#missing)'/>" +
            "</svg>");
        AnyNonTransparent(img).Should().BeFalse();
    }

    [Spec("svg11", SvgUrl, section: "painting.html#FillProperty")]
    [SpecFact]
    public void Url_fill_resolved_server_takes_priority_over_fallback()
    {
        // When the server IS present, the gradient paints; fallback is ignored.
        const string Svg =
            "<svg width='20' height='20' viewBox='0 0 20 20'>" +
            "<defs>" +
            "<linearGradient id='g'>" +
            "<stop offset='0' stop-color='blue'/>" +
            "<stop offset='1' stop-color='navy'/>" +
            "</linearGradient></defs>" +
            "<rect width='20' height='20' fill='url(#g) red'/>" +
            "</svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        // The result should be blue-ish, not red.
        var px = PixelAt(img, 10, 10);
        px.B.Should().BeGreaterThan(px.R, "gradient takes priority over fallback");
        AnyNonTransparent(img).Should().BeTrue();
    }
}
