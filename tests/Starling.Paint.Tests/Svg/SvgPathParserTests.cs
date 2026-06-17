using AwesomeAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using Starling.Paint.Svg;
using Starling.Spec;

namespace Starling.Paint.Tests.Svg;

/// <summary>
/// Unit tests for the SVG path-data (<c>d</c> attribute) parser. Asserts the
/// command set — absolute and relative — and the elliptical-arc conversion via
/// the geometry of the produced <see cref="IPath"/> (its bounds and flattened
/// vertices).
/// </summary>
[TestClass]
[Spec("svg11", "https://www.w3.org/TR/SVG11/", section: "paths.html#PathData")]
public sealed class SvgPathParserTests
{
    private const string SvgUrl = "https://www.w3.org/TR/SVG11/";

    private static RectangleF Bounds(string d)
    {
        var path = SvgPathParser.Parse(d);
        path.Should().NotBeNull();
        return path!.Bounds;
    }

    private static List<PointF> Points(string d)
    {
        var path = SvgPathParser.Parse(d);
        path.Should().NotBeNull();
        var pts = new List<PointF>();
        foreach (var seg in path!.Flatten())
        {
            pts.AddRange(seg.Points.ToArray());
        }

        return pts;
    }

    [Spec("svg11", SvgUrl, section: "paths.html#PathDataMovetoCommands")]
    [SpecFact]
    public void Absolute_moveto_lineto_traces_a_square()
    {
        var b = Bounds("M0 0 L10 0 L10 10 L0 10 Z");
        b.Left.Should().BeApproximately(0, 0.01f);
        b.Top.Should().BeApproximately(0, 0.01f);
        b.Right.Should().BeApproximately(10, 0.01f);
        b.Bottom.Should().BeApproximately(10, 0.01f);
    }

    [Spec("svg11", SvgUrl, section: "paths.html#PathDataLinetoCommands")]
    [SpecFact]
    public void Relative_commands_accumulate_from_current_point()
    {
        // m starts at (5,5); each l is relative → (5,5)->(15,5)->(15,15)->(5,15).
        var b = Bounds("m5 5 l10 0 l0 10 l-10 0 z");
        b.Left.Should().BeApproximately(5, 0.01f);
        b.Top.Should().BeApproximately(5, 0.01f);
        b.Right.Should().BeApproximately(15, 0.01f);
        b.Bottom.Should().BeApproximately(15, 0.01f);
    }

    [Spec("svg11", SvgUrl, section: "paths.html#PathDataLinetoCommands")]
    [SpecFact]
    public void Horizontal_and_vertical_shorthand()
    {
        // H/V absolute then h/v relative.
        var b = Bounds("M0 0 H10 V10 h-10 v-10 Z");
        b.Right.Should().BeApproximately(10, 0.01f);
        b.Bottom.Should().BeApproximately(10, 0.01f);
    }

    [Spec("svg11", SvgUrl, section: "paths.html#PathDataCubicBezierCommands")]
    [SpecFact]
    public void Cubic_bezier_absolute_and_smooth_continuation()
    {
        // A cubic then a smooth cubic (S) reflecting the previous control point.
        var pts = Points("M0 0 C0 10 10 10 10 0 S20 -10 20 0");
        // The path should span roughly x:[0,20].
        var xs = pts.ConvertAll(p => p.X);
        xs.Min().Should().BeLessThan(1f);
        xs.Max().Should().BeGreaterThan(19f);
    }

    [Spec("svg11", SvgUrl, section: "paths.html#PathDataQuadraticBezierCommands")]
    [SpecFact]
    public void Quadratic_bezier_and_smooth_continuation()
    {
        var pts = Points("M0 0 Q5 10 10 0 T20 0");
        var xs = pts.ConvertAll(p => p.X);
        xs.Max().Should().BeGreaterThan(19f);
    }

    [Spec("svg11", SvgUrl, section: "paths.html#PathDataEllipticalArcCommands")]
    [SpecFact]
    public void Arc_traces_a_semicircle_with_expected_bounds()
    {
        // Half-circle from (0,0) to (20,0), radius 10. Per the W3C
        // endpoint→center parameterization (F.6.5), sweep=1 (positive-angle
        // direction) places the apex at y≈-10 (above the chord) in SVG's
        // y-down space.
        var b = Bounds("M0 0 A10 10 0 0 1 20 0");
        b.Left.Should().BeApproximately(0, 0.5f);
        b.Right.Should().BeApproximately(20, 0.5f);
        b.Top.Should().BeApproximately(-10, 0.6f, "the sweep=1 semicircle apex is ~10 above the chord");
        b.Bottom.Should().BeApproximately(0, 0.5f);
    }

    [Spec("svg11", SvgUrl, section: "paths.html#PathDataEllipticalArcCommands")]
    [SpecFact]
    public void Arc_sweep_flag_flips_the_bulge_direction()
    {
        var up = Bounds("M0 0 A10 10 0 0 1 20 0");   // sweep=1 → apex above (y-)
        var down = Bounds("M0 0 A10 10 0 0 0 20 0"); // sweep=0 → apex below (y+)
        up.Top.Should().BeApproximately(-10, 0.6f);
        down.Bottom.Should().BeApproximately(10, 0.6f);
    }

    [Spec("svg11", SvgUrl, section: "paths.html#PathDataEllipticalArcCommands")]
    [SpecFact]
    public void Arc_with_zero_radius_degenerates_to_a_line()
    {
        var b = Bounds("M0 0 A0 0 0 0 1 10 10");
        b.Right.Should().BeApproximately(10, 0.01f);
        b.Bottom.Should().BeApproximately(10, 0.01f);
    }

    [Spec("svg11", SvgUrl, section: "paths.html#PathDataBNF")]
    [SpecFact]
    public void Numbers_may_run_together_without_separators()
    {
        // "1-2" → 1, -2 ; ".5.5" → 0.5, 0.5. Tests the implicit separators.
        var b = Bounds("M0 0L10-5L5.5.5Z");
        b.Top.Should().BeApproximately(-5, 0.01f);
        b.Right.Should().BeApproximately(10, 0.01f);
    }

    [Spec("svg11", SvgUrl, section: "paths.html#PathData")]
    [SpecFact]
    public void Empty_or_null_data_returns_null()
    {
        SvgPathParser.Parse(null).Should().BeNull();
        SvgPathParser.Parse("   ").Should().BeNull();
    }
}
