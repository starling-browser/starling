using AwesomeAssertions;
using Starling.Css.Values;
using Starling.Common.Image;
using Starling.Paint.Svg;
using Starling.Spec;

namespace Starling.Paint.Tests.Svg;

/// <summary>
/// Conformance + behavioural tests for the managed SVG rasterizer
/// (<see cref="SvgImageDecoder"/>). Covers intrinsic sizing, the shape set
/// real-world icons use, presentation/style/class cascade, and golden colour
/// spot-checks on synthetic documents. Real McMaster fixtures (the failing
/// case that motivated this WP) are decoded end-to-end.
/// </summary>
[TestClass]
[Spec("svg11", "https://www.w3.org/TR/SVG11/", section: "rasterization")]
public sealed class SvgImageDecoderTests
{
    private const string SvgUrl = "https://www.w3.org/TR/SVG11/";

    private static readonly string FixtureDir =
        Path.Combine(AppContext.BaseDirectory, "testdata", "images", "svg");

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

    // --- intrinsic sizing ---------------------------------------------------

    [Spec("svg11", SvgUrl, section: "coords.html#IntrinsicSizing")]
    [SpecFact]
    public void Intrinsic_size_from_width_and_height()
    {
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='40' height='25' viewBox='0 0 40 25'><rect width='40' height='25' fill='red'/></svg>");
        img.Width.Should().Be(40);
        img.Height.Should().Be(25);
    }

    [Spec("svg11", SvgUrl, section: "coords.html#ViewBoxAttribute")]
    [SpecFact]
    public void Intrinsic_size_falls_back_to_viewBox_when_no_width_height()
    {
        using var img = SvgImageDecoder.DecodeText(
            "<svg viewBox='0 0 21 21.9'><circle cx='8.7' cy='8.7' r='8.1' fill='black'/></svg>");
        img.Width.Should().Be(21);
        img.Height.Should().Be(22); // 21.9 ceils to 22
    }

    [Spec("svg11", SvgUrl, section: "coords.html#IntrinsicSizing")]
    [SpecFact]
    public void Intrinsic_size_defaults_to_150_when_no_size_info()
    {
        using var img = SvgImageDecoder.DecodeText("<svg><rect width='10' height='10' fill='red'/></svg>");
        img.Width.Should().Be(150);
        img.Height.Should().Be(150);
    }

    // --- root-element presentation attribute inheritance --------------------

    [Spec("svg11", SvgUrl, section: "painting.html#FillProperty")]
    [SpecFact]
    public void Root_fill_none_and_stroke_inherit_to_children()
    {
        // `fill="none" stroke=…` on the <svg> root must inherit to the circle,
        // so it paints as a ring — not a solid disc from the default black fill.
        // This is the shape almost every "line" icon uses (the netclaw search
        // magnifying glass, Heroicons/Lucide outline icons, …).
        using var img = SvgImageDecoder.DecodeText(
            "<svg xmlns='http://www.w3.org/2000/svg' width='24' height='24' viewBox='0 0 24 24' " +
            "fill='none' stroke='#ff0000' stroke-width='2'><circle cx='12' cy='12' r='8'/></svg>");

        // Center is inside the ring → no fill → transparent.
        PixelAt(img, 12, 12).A.Should().Be(0);
        // A red stroke ring is present.
        CountWhere(img, p => p.A > 0 && p.R > 150 && p.G < 80 && p.B < 80).Should().BeGreaterThan(0);
        // Nothing painted as the default opaque black fill.
        CountWhere(img, p => p.A > 200 && p.R < 40 && p.G < 40 && p.B < 40).Should().Be(0);
    }

    [Spec("svg11", SvgUrl, section: "color.html#ColorProperty")]
    [SpecFact]
    public void Root_stroke_currentColor_uses_supplied_color()
    {
        // stroke="currentColor" on the root resolves against the caller-supplied
        // currentColor (the element's computed CSS color for inline <svg>).
        using var img = SvgImageDecoder.DecodeText(
            "<svg xmlns='http://www.w3.org/2000/svg' width='24' height='24' viewBox='0 0 24 24' " +
            "fill='none' stroke='currentColor' stroke-width='2'><circle cx='12' cy='12' r='8'/></svg>",
            currentColor: CssColor.FromSrgb(0 / 255.0, 128 / 255.0, 255 / 255.0, 1.0));

        // The ring is drawn in the supplied blue, not the default black.
        CountWhere(img, p => p.A > 0 && p.B > 150 && p.R < 80).Should().BeGreaterThan(0);
        CountWhere(img, p => p.A > 200 && p.R < 40 && p.G < 40 && p.B < 40).Should().Be(0);
    }

    // --- golden colour spot-checks on synthetic SVGs ------------------------

    [Spec("svg11", SvgUrl, section: "shapes.html#RectElement")]
    [SpecFact]
    public void Filled_red_rect_produces_red_pixels()
    {
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='20' height='20' viewBox='0 0 20 20'><rect x='0' y='0' width='20' height='20' fill='#ff0000'/></svg>");

        AnyNonTransparent(img).Should().BeTrue();
        var center = PixelAt(img, 10, 10);
        center.R.Should().BeGreaterThan(200);
        center.G.Should().BeLessThan(60);
        center.B.Should().BeLessThan(60);
        center.A.Should().Be(255);
    }

    [Spec("svg11", SvgUrl, section: "shapes.html#CircleElement")]
    [SpecFact]
    public void Filled_blue_circle_is_blue_at_center_and_transparent_at_corner()
    {
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='40' height='40' viewBox='0 0 40 40'><circle cx='20' cy='20' r='15' fill='rgb(0,0,255)'/></svg>");

        var center = PixelAt(img, 20, 20);
        center.B.Should().BeGreaterThan(200);
        center.R.Should().BeLessThan(60);
        center.A.Should().Be(255);

        // The corner is outside the r=15 circle → transparent.
        PixelAt(img, 0, 0).A.Should().Be(0);
    }

    [Spec("svg11", SvgUrl, section: "painting.html#FillProperty")]
    [SpecFact]
    public void Named_color_and_currentColor_resolve()
    {
        using var named = SvgImageDecoder.DecodeText(
            "<svg width='10' height='10' viewBox='0 0 10 10'><rect width='10' height='10' fill='green'/></svg>");
        PixelAt(named, 5, 5).G.Should().BeGreaterThan(100);

        // currentColor resolves to the supplied color (cyan-ish).
        using var cur = SvgImageDecoder.DecodeText(
            "<svg width='10' height='10' viewBox='0 0 10 10'><rect width='10' height='10' fill='currentColor'/></svg>",
            currentColor: CssColor.FromSrgb(0 / 255.0, 200 / 255.0, 200 / 255.0, 1.0));
        var px = PixelAt(cur, 5, 5);
        px.G.Should().BeGreaterThan(150);
        px.B.Should().BeGreaterThan(150);
    }

    [Spec("svg11", SvgUrl, section: "painting.html#FillProperty")]
    [SpecFact]
    public void Fill_none_produces_no_fill()
    {
        // fill:none with no stroke → fully transparent output.
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='20' height='20' viewBox='0 0 20 20'><rect width='20' height='20' fill='none'/></svg>");
        AnyNonTransparent(img).Should().BeFalse();
    }

    [Spec("svg11", SvgUrl, section: "painting.html#StrokeProperty")]
    [SpecFact]
    public void Stroked_line_produces_pixels()
    {
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='20' height='20' viewBox='0 0 20 20'><line x1='2' y1='2' x2='18' y2='18' stroke='#000000' stroke-width='2'/></svg>");
        AnyNonTransparent(img).Should().BeTrue();
        // A point on the diagonal should be dark.
        PixelAt(img, 10, 10).A.Should().BeGreaterThan(0);
    }

    [Spec("svg11", SvgUrl, section: "painting.html#FillRuleProperty")]
    [SpecFact]
    public void Fill_rule_evenodd_leaves_inner_hole_transparent()
    {
        // Outer 20x20 square with an inner 8x12 hole; even-odd leaves the hole
        // empty, nonzero (same winding) would fill it. Both subpaths are wound
        // clockwise so even-odd is the discriminator.
        const string Svg =
            "<svg width='20' height='20' viewBox='0 0 20 20'>" +
            "<path fill-rule='evenodd' fill='#000000' " +
            "d='M0 0 H20 V20 H0 Z M6 4 H14 V16 H6 Z'/></svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);

        PixelAt(img, 2, 10).A.Should().BeGreaterThan(0);   // in the outer ring
        PixelAt(img, 10, 10).A.Should().Be(0);             // inside the hole
    }

    [Spec("svg11", SvgUrl, section: "coords.html#TransformAttribute")]
    [SpecFact]
    public void Group_transform_translates_geometry()
    {
        // A 4x4 red rect at origin, translated to (10,10) by the group.
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='20' height='20' viewBox='0 0 20 20'>" +
            "<g transform='translate(10,10)'><rect width='4' height='4' fill='red'/></g></svg>");

        PixelAt(img, 12, 12).R.Should().BeGreaterThan(200); // moved here
        PixelAt(img, 2, 2).A.Should().Be(0);                // empty at origin
    }

    [Spec("svg11", SvgUrl, section: "painting.html#FillOpacityProperty")]
    [SpecFact]
    public void Fill_opacity_reduces_alpha()
    {
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='10' height='10' viewBox='0 0 10 10'><rect width='10' height='10' fill='#000000' fill-opacity='0.5'/></svg>");
        var a = PixelAt(img, 5, 5).A;
        a.Should().BeInRange(100, 160); // ~127
    }

    [Spec("svg11", SvgUrl, section: "styling.html#StyleAttribute")]
    [SpecFact]
    public void Style_attribute_overrides_presentation_attribute()
    {
        // presentation fill=red, but style="" sets blue → blue wins.
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='10' height='10' viewBox='0 0 10 10'><rect width='10' height='10' fill='red' style='fill:#0000ff'/></svg>");
        var px = PixelAt(img, 5, 5);
        px.B.Should().BeGreaterThan(200);
        px.R.Should().BeLessThan(60);
    }

    [Spec("svg11", SvgUrl, section: "styling.html#StyleElement")]
    [SpecFact]
    public void Style_element_class_rules_apply()
    {
        // The geometry carries no inline paint — colour lives in a class rule,
        // exactly like the McMaster fixtures.
        const string Svg =
            "<svg width='10' height='10' viewBox='0 0 10 10'>" +
            "<style>.fillme{fill:#00ff00;}</style>" +
            "<rect class='fillme' width='10' height='10'/></svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        PixelAt(img, 5, 5).G.Should().BeGreaterThan(200);
    }

    [Spec("svg11", SvgUrl, section: "paths.html#PathDataEllipticalArcCommands")]
    [SpecFact]
    public void Arc_command_renders_a_filled_region()
    {
        // A half-disc traced with an arc; just assert it produces filled pixels
        // (the path parser arc test asserts the geometry precisely).
        const string Svg =
            "<svg width='40' height='40' viewBox='0 0 40 40'>" +
            "<path fill='#000000' d='M5 20 A15 15 0 0 1 35 20 Z'/></svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        AnyNonTransparent(img).Should().BeTrue();
        PixelAt(img, 20, 14).A.Should().BeGreaterThan(0); // inside the arc cap
    }

    // --- malformed / edge cases ---------------------------------------------

    [Spec("svg11", SvgUrl, section: "rasterization")]
    [SpecFact]
    public void Malformed_xml_throws_SvgDecodeException()
    {
        var act = () => SvgImageDecoder.DecodeText("<svg><rect></svg>");
        act.Should().Throw<SvgDecodeException>();
    }

    [Spec("svg11", SvgUrl, section: "rasterization")]
    [SpecFact]
    public void Non_svg_root_throws()
    {
        var act = () => SvgImageDecoder.DecodeText("<html><body/></html>");
        act.Should().Throw<SvgDecodeException>();
    }

    [Spec("svg11", SvgUrl, section: "rasterization")]
    [SpecFact]
    public void Unknown_elements_are_ignored_gracefully()
    {
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='10' height='10' viewBox='0 0 10 10'>" +
            "<foreignObject/><unknownThing x='1'/>" +
            "<rect width='10' height='10' fill='red'/></svg>");
        PixelAt(img, 5, 5).R.Should().BeGreaterThan(200);
    }

    // --- real McMaster fixtures (the motivating failure) --------------------

    [TestMethod]
    [DataRow("MastheadLogo.svg", 457, 66)]              // viewBox 0 0 456.5 65.7
    [DataRow("searchIconLightGray.svg", 21, 22)]        // viewBox 0 0 21 21.9
    [DataRow("searchBoxVerticalSeparatorDefault.svg", 1, 13)]
    [DataRow("searchBoxClearButtonLightGray.svg", 12, 12)]
    [DataRow("searchIconRefreshedLightGray.svg", 20, 20)]
    [Spec("svg11", SvgUrl, section: "rasterization")]
    public void McMaster_fixture_decodes_to_expected_dimensions(string file, int w, int h)
    {
        var path = Path.Combine(FixtureDir, file);
        File.Exists(path).Should().BeTrue($"fixture '{file}' should be copied to test output ({path})");
        using var img = SvgImageDecoder.Decode(File.ReadAllBytes(path));
        img.Width.Should().Be(w);
        img.Height.Should().Be(h);
        AnyNonTransparent(img).Should().BeTrue($"'{file}' should rasterize to non-empty pixels");
    }

    [Spec("svg11", SvgUrl, section: "rasterization")]
    [SpecFact]
    public void McMaster_separator_fills_with_its_class_color()
    {
        // searchBoxVerticalSeparatorDefault: a single grey (#cbcbcb) rect.
        var path = Path.Combine(FixtureDir, "searchBoxVerticalSeparatorDefault.svg");
        using var img = SvgImageDecoder.Decode(File.ReadAllBytes(path));
        // 1x13 image; the middle pixel should be the grey fill.
        var px = PixelAt(img, 0, 6);
        px.A.Should().BeGreaterThan(0);
        px.R.Should().BeInRange(180, 220); // ~0xcb
    }

    [Spec("svg11", SvgUrl, section: "rasterization")]
    [SpecFact]
    public void DecodedImage_buffer_is_tightly_packed_rgba8888_top_down()
    {
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='8' height='5' viewBox='0 0 8 5'><rect width='8' height='5' fill='red'/></svg>");
        // Invariant: length == width*height*4, no row padding.
        img.Pixels.Length.Should().Be(8 * 5 * 4);
    }

    // --- <pattern> paint servers + percentage geometry ----------------------

    [Spec("svg11", SvgUrl, section: "pservers.html#Patterns")]
    [SpecFact]
    public void Rect_width_height_percent_resolves_against_viewport()
    {
        // `width='100%'` must fill the 40x40 viewport, not collapse to 0.
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='40' height='40' viewBox='0 0 40 40'>" +
            "<rect width='100%' height='100%' fill='red'/></svg>");
        PixelAt(img, 2, 2).R.Should().BeGreaterThan(200);
        PixelAt(img, 38, 38).R.Should().BeGreaterThan(200);
    }

    [Spec("svg11", SvgUrl, section: "pservers.html#Patterns")]
    [SpecFact]
    public void Pattern_fill_tiles_across_the_shape()
    {
        // A 20x20 tile with a red square in its top-left 10x10 quadrant, tiled
        // (userSpaceOnUse) across a 40x40 rect → red in each tile's top-left
        // quadrant, transparent in the rest. Proves both reference resolution
        // and tiling beyond the first tile.
        const string Svg =
            "<svg width='40' height='40' viewBox='0 0 40 40'>" +
            "<defs><pattern id='p' patternUnits='userSpaceOnUse' width='20' height='20'>" +
            "<rect x='0' y='0' width='10' height='10' fill='red'/></pattern></defs>" +
            "<rect width='100%' height='100%' fill='url(#p)'/></svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);

        // First tile (0..20): red in 0..10, clear in 10..20.
        PixelAt(img, 5, 5).R.Should().BeGreaterThan(200);
        PixelAt(img, 5, 5).A.Should().BeGreaterThan(200);
        PixelAt(img, 15, 15).A.Should().Be(0);
        // Second tile (20..40): the pattern repeated → red again at 25,25.
        PixelAt(img, 25, 25).R.Should().BeGreaterThan(200);
        PixelAt(img, 35, 35).A.Should().Be(0);
    }

    [Spec("svg11", SvgUrl, section: "pservers.html#Patterns")]
    [SpecFact]
    public void Pattern_with_unresolved_reference_paints_nothing()
    {
        // fill="url(#missing)" with no such paint server → no fill (not black).
        using var img = SvgImageDecoder.DecodeText(
            "<svg width='20' height='20' viewBox='0 0 20 20'>" +
            "<rect width='100%' height='100%' fill='url(#missing)'/></svg>");
        AnyNonTransparent(img).Should().BeFalse();
    }

    [Spec("svg11", SvgUrl, section: "pservers.html#Patterns")]
    [SpecFact]
    public void Pattern_renders_stroked_content()
    {
        // The angular.dev-style mask: a rotated stroked line inside the tile.
        // It must produce visible (non-transparent) pixels.
        const string Svg =
            "<svg width='72' height='72' viewBox='0 0 72 72'>" +
            "<defs><pattern id='p' patternUnits='userSpaceOnUse' width='72' height='72'>" +
            "<g transform='translate(36 36) rotate(-60)'>" +
            "<line x1='-10' y1='0' x2='10' y2='0' stroke='black' stroke-width='3' stroke-linecap='round'/>" +
            "</g></pattern></defs>" +
            "<rect width='100%' height='100%' fill='url(#p)'/></svg>";
        using var img = SvgImageDecoder.DecodeText(Svg);
        AnyNonTransparent(img).Should().BeTrue("the rotated line in the pattern tile must paint");
    }
}
