// SPDX-License-Identifier: Apache-2.0
using AwesomeAssertions;
using Starling.Spec;

namespace Starling.Paint.Tests.Svg;

/// <summary>
/// SVG 1.1 chapter 9 — Basic Shapes (+ chapter 8 Paths), modelled on the
/// resvg test-suite <c>tests/shapes/</c> cases. Each case authors an SVG and
/// asserts on the rasterized geometry.
/// </summary>
[TestClass]
[Spec("svg11", SvgRaster.Spec11Url, section: "shapes.html")]
public sealed class SvgConformanceShapesTests
{
    private const string U = SvgRaster.Spec11Url;

    // --- rect ---------------------------------------------------------------

    [Spec("svg11", U, section: "shapes.html#RectElement")]
    [SpecFact]
    public void Rect_fills_its_box()
    {
        using var img = SvgRaster.Decode(
            "<svg width='20' height='20' viewBox='0 0 20 20'><rect x='2' y='2' width='16' height='16' fill='red'/></svg>");
        SvgRaster.IsRed(SvgRaster.At(img, 10, 10)).Should().BeTrue();
        SvgRaster.At(img, 0, 0).A.Should().Be(0);
    }

    [Spec("svg11", U, section: "shapes.html#RectElementRXAttribute")]
    [SpecFact]
    public void Rect_with_rx_ry_rounds_its_corners()
    {
        // A 20x20 rounded rect with r=6 clips the corners → corner transparent,
        // centre filled.
        using var img = SvgRaster.Decode(
            "<svg width='20' height='20' viewBox='0 0 20 20'><rect width='20' height='20' rx='6' ry='6' fill='black'/></svg>");
        SvgRaster.At(img, 0, 0).A.Should().Be(0, "the rounded corner is clipped away");
        SvgRaster.At(img, 10, 10).A.Should().BeGreaterThan(200);
    }

    [Spec("svg11", U, section: "shapes.html#RectElementRXAttribute")]
    [SpecFact]
    public void Rect_rx_only_mirrors_to_ry()
    {
        // Only rx given → ry defaults to rx (SVG 1.1 §9.2), so both axes round.
        using var img = SvgRaster.Decode(
            "<svg width='20' height='20' viewBox='0 0 20 20'><rect width='20' height='20' rx='8' fill='black'/></svg>");
        SvgRaster.At(img, 0, 0).A.Should().Be(0);
        SvgRaster.At(img, 10, 10).A.Should().BeGreaterThan(200);
    }

    [Spec("svg11", U, section: "shapes.html#RectElementRXAttribute")]
    [SpecFact]
    public void Rect_rx_is_clamped_to_half_width()
    {
        // rx far larger than half the width must clamp to w/2 (here 10), not
        // overflow. The shape stays inside the box and the centre fills.
        using var img = SvgRaster.Decode(
            "<svg width='20' height='20' viewBox='0 0 20 20'><rect width='20' height='20' rx='1000' fill='black'/></svg>");
        SvgRaster.At(img, 10, 10).A.Should().BeGreaterThan(200);
        SvgRaster.At(img, 0, 0).A.Should().Be(0, "clamped radius still rounds the corner");
    }

    [Spec("svg11", U, section: "shapes.html#RectElementWidthAttribute")]
    [SpecFact]
    public void Rect_with_zero_or_negative_size_paints_nothing()
    {
        using var zero = SvgRaster.Decode(
            "<svg width='20' height='20' viewBox='0 0 20 20'><rect width='0' height='10' fill='red'/></svg>");
        SvgRaster.AnyOpaque(zero).Should().BeFalse("zero width disables rendering (SVG 1.1 §9.2)");

        using var neg = SvgRaster.Decode(
            "<svg width='20' height='20' viewBox='0 0 20 20'><rect width='-5' height='10' fill='red'/></svg>");
        SvgRaster.AnyOpaque(neg).Should().BeFalse("negative width is an error → not rendered");
    }

    [Spec("svg11", U, section: "shapes.html#RectElementRXAttribute")]
    [SpecFact]
    public void Rect_with_negative_rx_renders_sharp_corners()
    {
        // Negative rx is invalid → treated as unrounded, so the corner fills.
        using var img = SvgRaster.Decode(
            "<svg width='20' height='20' viewBox='0 0 20 20'><rect width='20' height='20' rx='-5' fill='red'/></svg>");
        SvgRaster.IsRed(SvgRaster.At(img, 0, 0)).Should().BeTrue("a negative radius means no rounding");
    }

    [Spec("svg11", U, section: "coords.html#Units")]
    [SpecFact]
    public void Rect_percentage_size_resolves_against_viewport()
    {
        using var img = SvgRaster.Decode(
            "<svg width='40' height='40' viewBox='0 0 40 40'><rect width='50%' height='50%' fill='red'/></svg>");
        SvgRaster.IsRed(SvgRaster.At(img, 5, 5)).Should().BeTrue();
        SvgRaster.At(img, 30, 30).A.Should().Be(0, "50% of 40 is 20 → outside the rect");
    }

    // --- circle / ellipse ---------------------------------------------------

    [Spec("svg11", U, section: "shapes.html#CircleElement")]
    [SpecFact]
    public void Circle_fills_a_disc()
    {
        using var img = SvgRaster.Decode(
            "<svg width='40' height='40' viewBox='0 0 40 40'><circle cx='20' cy='20' r='15' fill='blue'/></svg>");
        SvgRaster.IsBlue(SvgRaster.At(img, 20, 20)).Should().BeTrue();
        SvgRaster.At(img, 0, 0).A.Should().Be(0);
    }

    [Spec("svg11", U, section: "shapes.html#CircleElementRAttribute")]
    [SpecFact]
    public void Circle_with_zero_or_negative_radius_paints_nothing()
    {
        using var zero = SvgRaster.Decode(
            "<svg width='20' height='20' viewBox='0 0 20 20'><circle cx='10' cy='10' r='0' fill='red'/></svg>");
        SvgRaster.AnyOpaque(zero).Should().BeFalse();

        using var neg = SvgRaster.Decode(
            "<svg width='20' height='20' viewBox='0 0 20 20'><circle cx='10' cy='10' r='-4' fill='red'/></svg>");
        SvgRaster.AnyOpaque(neg).Should().BeFalse("a negative radius disables rendering");
    }

    [Spec("svg11", U, section: "shapes.html#EllipseElement")]
    [SpecFact]
    public void Ellipse_is_wider_than_tall_when_rx_exceeds_ry()
    {
        using var img = SvgRaster.Decode(
            "<svg width='40' height='40' viewBox='0 0 40 40'><ellipse cx='20' cy='20' rx='18' ry='6' fill='black'/></svg>");
        int width = SvgRaster.OpaqueInRow(img, 20);
        int height = 0;
        for (int y = 0; y < img.Height; y++)
        {
            if (SvgRaster.At(img, 20, y).A > 40)
            {
                height++;
            }
        }

        width.Should().BeGreaterThan(height + 6, "rx (18) far exceeds ry (6), so the ellipse is wider than tall");
    }

    [Spec("svg11", U, section: "shapes.html#CircleElementRAttribute")]
    [SpecFact]
    public void Circle_radius_matches_the_r_attribute()
    {
        // A circle of r=10 must span ~20px across its centre row. It currently
        // spans ~10px (radius ~5), so this documents the size defect.
        using var img = SvgRaster.Decode(
            "<svg width='40' height='40' viewBox='0 0 40 40'><circle cx='20' cy='20' r='10' fill='black'/></svg>");
        SvgRaster.OpaqueInRow(img, 20).Should().BeInRange(18, 22, "diameter of an r=10 circle is ~20px");
    }

    [Spec("svg11", U, section: "shapes.html#EllipseElement")]
    [SpecFact]
    public void Ellipse_with_zero_radius_paints_nothing()
    {
        using var img = SvgRaster.Decode(
            "<svg width='20' height='20' viewBox='0 0 20 20'><ellipse cx='10' cy='10' rx='8' ry='0' fill='red'/></svg>");
        SvgRaster.AnyOpaque(img).Should().BeFalse();
    }

    // --- line / polyline / polygon ------------------------------------------

    [Spec("svg11", U, section: "shapes.html#LineElement")]
    [SpecFact]
    public void Line_paints_only_when_stroked()
    {
        using var noStroke = SvgRaster.Decode(
            "<svg width='20' height='20' viewBox='0 0 20 20'><line x1='2' y1='2' x2='18' y2='18'/></svg>");
        SvgRaster.AnyOpaque(noStroke).Should().BeFalse("a line has no fill and no default stroke");

        using var stroked = SvgRaster.Decode(
            "<svg width='20' height='20' viewBox='0 0 20 20'><line x1='2' y1='2' x2='18' y2='18' stroke='black' stroke-width='2'/></svg>");
        SvgRaster.At(stroked, 10, 10).A.Should().BeGreaterThan(0);
    }

    [Spec("svg11", U, section: "shapes.html#PolygonElement")]
    [SpecFact]
    public void Polygon_closes_its_outline_and_fills()
    {
        // A triangle: filled inside, closed back to the start point.
        using var img = SvgRaster.Decode(
            "<svg width='40' height='40' viewBox='0 0 40 40'><polygon points='20,4 36,36 4,36' fill='black'/></svg>");
        SvgRaster.At(img, 20, 28).A.Should().BeGreaterThan(0, "inside the triangle");
        SvgRaster.At(img, 2, 2).A.Should().Be(0, "outside the triangle");
    }

    [Spec("svg11", U, section: "shapes.html#PolylineElement")]
    [SpecFact]
    public void Polyline_does_not_auto_close()
    {
        // An open polyline with fill='none' and a stroke: the open side has no
        // edge, unlike a polygon which would draw the closing segment.
        using var img = SvgRaster.Decode(
            "<svg width='40' height='40' viewBox='0 0 40 40'>" +
            "<polyline points='6,6 34,6 34,34' fill='none' stroke='black' stroke-width='2'/></svg>");
        SvgRaster.At(img, 6, 20).A.Should().Be(0, "the closing edge from (34,34) back to (6,6) is not drawn");
        SvgRaster.At(img, 20, 6).A.Should().BeGreaterThan(0, "the top edge is drawn");
    }

    [Spec("svg11", U, section: "shapes.html#PointsBNF")]
    [SpecFact]
    public void Polygon_with_fewer_than_two_points_paints_nothing()
    {
        using var img = SvgRaster.Decode(
            "<svg width='20' height='20' viewBox='0 0 20 20'><polygon points='10,10' fill='red'/></svg>");
        SvgRaster.AnyOpaque(img).Should().BeFalse();
    }

    // --- path (chapter 8) ---------------------------------------------------

    [Spec("svg11", U, section: "paths.html#PathDataClosePathCommand")]
    [SpecFact]
    public void Path_M_L_L_Z_fills_a_triangle()
    {
        using var img = SvgRaster.Decode(
            "<svg width='40' height='40' viewBox='0 0 40 40'><path d='M20 4 L36 36 L4 36 Z' fill='black'/></svg>");
        SvgRaster.At(img, 20, 28).A.Should().BeGreaterThan(0);
        SvgRaster.At(img, 2, 2).A.Should().Be(0);
    }

    [Spec("svg11", U, section: "paths.html#PathDataLinetoCommands")]
    [SpecFact]
    public void Path_implicit_lineto_after_moveto()
    {
        // "M x y x y" — the second coord pair is an implicit lineto.
        using var img = SvgRaster.Decode(
            "<svg width='40' height='40' viewBox='0 0 40 40'><path d='M4 4 36 36 4 36 Z' fill='black'/></svg>");
        SvgRaster.At(img, 12, 30).A.Should().BeGreaterThan(0);
    }

    [Spec("svg11", U, section: "paths.html#PathDataLinetoCommands")]
    [SpecFact]
    public void Path_relative_and_HV_commands_trace_a_box()
    {
        // m + h/v relative commands close a 20x20 box at (10,10).
        using var img = SvgRaster.Decode(
            "<svg width='40' height='40' viewBox='0 0 40 40'><path d='m10 10 h20 v20 h-20 z' fill='black'/></svg>");
        SvgRaster.At(img, 20, 20).A.Should().BeGreaterThan(0, "inside the box");
        SvgRaster.At(img, 2, 2).A.Should().Be(0, "outside the box");
    }

    [Spec("svg11", U, section: "paths.html#PathDataCubicBezierCommands")]
    [SpecFact]
    public void Path_cubic_bezier_renders_filled_pixels()
    {
        using var img = SvgRaster.Decode(
            "<svg width='40' height='40' viewBox='0 0 40 40'><path d='M4 32 C4 4 36 4 36 32 Z' fill='black'/></svg>");
        SvgRaster.AnyOpaque(img).Should().BeTrue();
        SvgRaster.At(img, 20, 24).A.Should().BeGreaterThan(0);
    }

    [Spec("svg11", U, section: "paths.html#PathDataQuadraticBezierCommands")]
    [SpecFact]
    public void Path_quadratic_bezier_renders_filled_pixels()
    {
        using var img = SvgRaster.Decode(
            "<svg width='40' height='40' viewBox='0 0 40 40'><path d='M4 32 Q20 0 36 32 Z' fill='black'/></svg>");
        SvgRaster.AnyOpaque(img).Should().BeTrue();
    }

    [Spec("svg11", U, section: "paths.html#PathDataEllipticalArcCommands")]
    [SpecFact]
    public void Path_arc_renders_a_filled_region()
    {
        using var img = SvgRaster.Decode(
            "<svg width='40' height='40' viewBox='0 0 40 40'><path d='M5 20 A15 15 0 0 1 35 20 Z' fill='black'/></svg>");
        SvgRaster.At(img, 20, 14).A.Should().BeGreaterThan(0);
    }

    [Spec("svg11", U, section: "paths.html#PathDataMovetoCommands")]
    [SpecFact]
    public void Path_two_subpaths_both_render()
    {
        using var img = SvgRaster.Decode(
            "<svg width='40' height='40' viewBox='0 0 40 40'>" +
            "<path d='M2 2 H14 V14 H2 Z M26 26 H38 V38 H26 Z' fill='black'/></svg>");
        SvgRaster.At(img, 8, 8).A.Should().BeGreaterThan(0, "first subpath");
        SvgRaster.At(img, 32, 32).A.Should().BeGreaterThan(0, "second subpath");
    }
}
