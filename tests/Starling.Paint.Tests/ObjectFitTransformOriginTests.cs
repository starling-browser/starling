using AwesomeAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Css.Values;
using Starling.Dom;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Text;
using Starling.Layout.Tree;
using Starling.Paint.DisplayList;
using LayoutSize = Starling.Layout.Size;

namespace Starling.Paint.Tests;

/// <summary>
/// CSS Images 3 §4.5/§4.6 (object-fit + object-position) and CSS Transforms 1
/// §6.2 (transform-origin) paint tests. The object-fit tests render a real
/// document and probe pixels relative to the image's content box, whose
/// origin is discovered from a reference render (object-fit: fill with a
/// solid magenta source) so the probes do not depend on inline-layout
/// baseline placement.
/// </summary>
[TestClass]
[TestCategory("GoldenImage")]
public sealed class ObjectFitTransformOriginTests
{
    private static readonly Rgba32 Red = new(255, 0, 0);
    private static readonly Rgba32 Blue = new(0, 0, 255);
    private static readonly Rgba32 Magenta = new(255, 0, 255);

    // The body carries a lime background so letterbox / uncovered regions
    // probe as lime (the painter does not paint backgrounds on replaced
    // boxes themselves).
    private static string ImgDoc(string fitStyle) =>
        "<body style=\"margin:0;background-color:#00ff00\">" +
        $"<img id=swatch style=\"width:40px;height:20px;{fitStyle}\">" +
        "</body>";

    // ------------------------------------------------------------------
    // object-fit / object-position
    // ------------------------------------------------------------------

    [TestMethod]
    public void Cover_crops_the_long_axis()
    {
        var (ox, oy) = ImageBoxOrigin();
        // 80x20 source whose LEFT QUARTER (x < 20) is red, rest blue, in a
        // 40x20 box. cover keeps scale 1 (max of 40/80 and 20/20) and crops
        // 20px off each horizontal side, so the red quarter is cropped away
        // entirely. An asymmetric swatch is load-bearing: with a half-split
        // the colour boundary lands at the box centre under BOTH fill and
        // cover, and the probe could not tell them apart.
        using var swatch = LeftQuarterSwatch(80, 20, Red, Blue);
        using var bmp = Render(ImgDoc("object-fit:cover"), swatch);

        IsBlue(bmp.GetPixel(ox + 2, oy + 10)).Should().BeTrue(
            "cover crops the red left quarter away; fill would squash it red into this pixel");
        IsBlue(bmp.GetPixel(ox + 5, oy + 1)).Should().BeTrue(
            "cover fills the full height; contain would letterbox this row in lime");
        IsBlue(bmp.GetPixel(ox + 38, oy + 10)).Should().BeTrue(
            "the right side of the centred crop is blue");
    }

    [TestMethod]
    public void Contain_letterboxes_and_shows_the_background()
    {
        var (ox, oy) = ImageBoxOrigin();
        // 40x40 source in a 40x20 box: contain scales to 20x20, centred at
        // x 10..30, with lime letterbox bands either side.
        using var swatch = TopBottomSwatch(40, 40, Red, Blue);
        using var bmp = Render(ImgDoc("object-fit:contain"), swatch);

        IsLime(bmp.GetPixel(ox + 2, oy + 10)).Should().BeTrue(
            "the left letterbox shows the background through the image box");
        IsLime(bmp.GetPixel(ox + 37, oy + 10)).Should().BeTrue(
            "the right letterbox shows the background through the image box");
        IsRed(bmp.GetPixel(ox + 20, oy + 4)).Should().BeTrue(
            "the fitted image's top half is red");
        IsBlue(bmp.GetPixel(ox + 20, oy + 15)).Should().BeTrue(
            "the fitted image's bottom half is blue");
    }

    [TestMethod]
    public void None_centers_the_image_unscaled()
    {
        var (ox, oy) = ImageBoxOrigin();
        // 10x10 source in a 40x20 box: none keeps 10x10, centred at
        // (15,5)..(25,15).
        using var swatch = SolidSwatch(10, 10, Red);
        using var bmp = Render(ImgDoc("object-fit:none"), swatch);

        IsRed(bmp.GetPixel(ox + 20, oy + 10)).Should().BeTrue(
            "the unscaled image is centred in the content box");
        IsLime(bmp.GetPixel(ox + 12, oy + 10)).Should().BeTrue(
            "none does not scale: fill would stretch red over this pixel");
        IsLime(bmp.GetPixel(ox + 2, oy + 2)).Should().BeTrue(
            "the corner outside the centred image stays background");
    }

    [TestMethod]
    public void Object_position_zero_zero_pins_top_left()
    {
        var (ox, oy) = ImageBoxOrigin();
        using var swatch = SolidSwatch(10, 10, Red);
        using var bmp = Render(ImgDoc("object-fit:none;object-position:0 0"), swatch);

        IsRed(bmp.GetPixel(ox + 2, oy + 2)).Should().BeTrue(
            "object-position 0 0 pins the image to the content box top-left");
        IsLime(bmp.GetPixel(ox + 20, oy + 10)).Should().BeTrue(
            "the box centre is empty once the image is pinned top-left");
        IsLime(bmp.GetPixel(ox + 12, oy + 12)).Should().BeTrue(
            "pixels past the 10x10 image stay background");
    }

    [TestMethod]
    public void Scale_down_picks_none_when_the_image_is_smaller()
    {
        var (ox, oy) = ImageBoxOrigin();
        // 10x10 source in 40x20: contain would UPSCALE to 20x20 (x 10..30);
        // scale-down must keep the smaller `none` size (10x10 at x 15..25).
        using var swatch = SolidSwatch(10, 10, Red);
        using var bmp = Render(ImgDoc("object-fit:scale-down"), swatch);

        IsRed(bmp.GetPixel(ox + 20, oy + 10)).Should().BeTrue(
            "scale-down keeps the centred natural-size image");
        IsLime(bmp.GetPixel(ox + 12, oy + 10)).Should().BeTrue(
            "contain would upscale to 20x20 and paint this pixel red");
    }

    [TestMethod]
    public void Scale_down_picks_contain_when_the_image_is_larger()
    {
        var (ox, oy) = ImageBoxOrigin();
        // 80x80 source in 40x20: none would cover the whole box; scale-down
        // must pick contain (20x20 at x 10..30).
        using var swatch = SolidSwatch(80, 80, Red);
        using var bmp = Render(ImgDoc("object-fit:scale-down"), swatch);

        IsRed(bmp.GetPixel(ox + 20, oy + 10)).Should().BeTrue(
            "the contained image paints in the box centre");
        IsLime(bmp.GetPixel(ox + 2, oy + 10)).Should().BeTrue(
            "none would crop-cover the whole box and paint this pixel red");
    }

    // ------------------------------------------------------------------
    // transform-origin — pixel probes
    // ------------------------------------------------------------------

    [TestMethod]
    public void Top_left_rotation_keeps_the_top_left_region_painted()
    {
        const string Template =
            "<body style=\"margin:0\"><div style=\"width:100px;height:100px;background-color:#ff0000;{0}\"></div></body>";

        // Discover the box origin from an untransformed reference render
        // (transform never affects layout).
        using var reference = Render(string.Format(Template, ""), swatch: null, 200, 200);
        var (bx, by) = FirstMatch(reference, IsRed);

        using var topLeft = Render(
            string.Format(Template, "transform:rotate(45deg);transform-origin:top left"), swatch: null, 200, 200);
        using var centre = Render(
            string.Format(Template, "transform:rotate(45deg)"), swatch: null, 200, 200);

        // (2,10) relative to the box corner lies inside the box rotated about
        // its top-left corner, but well outside the box rotated about its
        // centre (the centred diamond satisfies |dx-50|+|dy-50| <= 70.7).
        IsRed(topLeft.GetPixel(bx + 2, by + 10)).Should().BeTrue(
            "rotation about the top-left corner keeps the near-corner region painted");
        IsRed(topLeft.GetPixel(bx + 4, by + 20)).Should().BeTrue(
            "the pivot corner region stays fixed under a top-left rotation");
        IsRed(centre.GetPixel(bx + 2, by + 10)).Should().BeFalse(
            "rotation about the centre moves the top-left region away");
    }

    [TestMethod]
    public void Bottom_right_origin_scale_grows_leftward_and_upward()
    {
        const string Template =
            "<body style=\"margin:0\"><div style=\"padding:40px 0 0 40px\">" +
            "<div style=\"width:20px;height:20px;background-color:#ff0000;{0}\"></div></div></body>";

        using var reference = Render(string.Format(Template, ""), swatch: null, 200, 100);
        var (bx, by) = FirstMatch(reference, IsRed);

        using var corner = Render(
            string.Format(Template, "transform:scale(2);transform-origin:100% 100%"), swatch: null, 200, 100);
        using var centre = Render(
            string.Format(Template, "transform:scale(2)"), swatch: null, 200, 100);

        // scale(2) about the bottom-right corner covers (bx-20,by-20)..(bx+20,by+20);
        // about the centre it covers (bx-10,by-10)..(bx+30,by+30).
        IsRed(corner.GetPixel(bx - 15, by - 15)).Should().BeTrue(
            "scaling about 100% 100% grows the box leftward and upward");
        IsRed(corner.GetPixel(bx + 25, by + 25)).Should().BeFalse(
            "scaling about 100% 100% must not grow past the fixed bottom-right corner");
        IsRed(centre.GetPixel(bx - 15, by - 15)).Should().BeFalse(
            "centre-origin scaling does not reach this far left/up");
        IsRed(centre.GetPixel(bx + 25, by + 25)).Should().BeTrue(
            "centre-origin scaling grows past the original bottom-right corner");
    }

    // ------------------------------------------------------------------
    // transform-origin — matrix-level (frame-position independent deltas)
    // ------------------------------------------------------------------

    [TestMethod]
    public void Rotation_matrix_translation_shifts_by_origin_choice()
    {
        // For rotate(90deg) about pivot (px,py): E = px+py, F = py-px. Moving
        // the pivot from the top-left corner to the centre of a 100x100 box
        // adds (50,50), so E grows by 100 and F is unchanged — independent of
        // where layout put the box.
        var topLeft = FirstPush(BuildList(
            "<body style=\"margin:0\"><div style=\"width:100px;height:100px;background-color:#ff0000;transform:rotate(90deg);transform-origin:top left\">x</div></body>"));
        var centre = FirstPush(BuildList(
            "<body style=\"margin:0\"><div style=\"width:100px;height:100px;background-color:#ff0000;transform:rotate(90deg)\">x</div></body>"));

        (centre.Matrix.E - topLeft.Matrix.E).Should().BeApproximately(100, 0.001);
        (centre.Matrix.F - topLeft.Matrix.F).Should().BeApproximately(0, 0.001);
    }

    [TestMethod]
    public void Scale_matrix_translation_shifts_by_origin_choice()
    {
        // For scale(2) about pivot (px,py): E = -px, F = -py. Moving the
        // pivot from the centre (50,50) to 100% 100% (100,100) of a 100x100
        // box shifts E and F by -50 each.
        var corner = FirstPush(BuildList(
            "<body style=\"margin:0\"><div style=\"width:100px;height:100px;background-color:#ff0000;transform:scale(2);transform-origin:100% 100%\">x</div></body>"));
        var centre = FirstPush(BuildList(
            "<body style=\"margin:0\"><div style=\"width:100px;height:100px;background-color:#ff0000;transform:scale(2)\">x</div></body>"));

        (corner.Matrix.E - centre.Matrix.E).Should().BeApproximately(-50, 0.001);
        (corner.Matrix.F - centre.Matrix.F).Should().BeApproximately(-50, 0.001);
    }

    // ------------------------------------------------------------------
    // ResolveTransformOrigin — unit coverage of the value grammar
    // ------------------------------------------------------------------

    [TestMethod]
    public void Transform_origin_initial_value_resolves_to_centre()
    {
        DisplayListBuilder.ResolveTransformOrigin(new CssKeyword("50% 50% 0"), 200, 100)
            .Should().Be((100d, 50d));
        DisplayListBuilder.ResolveTransformOrigin(null, 200, 100)
            .Should().Be((100d, 50d));
    }

    [TestMethod]
    public void Transform_origin_keyword_pairs_accept_either_axis_order()
    {
        DisplayListBuilder.ResolveTransformOrigin(
                new CssValueList([new CssKeyword("top"), new CssKeyword("left")]), 200, 100)
            .Should().Be((0d, 0d));
        DisplayListBuilder.ResolveTransformOrigin(
                new CssValueList([new CssKeyword("left"), new CssKeyword("top")]), 200, 100)
            .Should().Be((0d, 0d));
        DisplayListBuilder.ResolveTransformOrigin(
                new CssValueList([new CssKeyword("right"), new CssKeyword("bottom")]), 200, 100)
            .Should().Be((200d, 100d));
    }

    [TestMethod]
    public void Transform_origin_lengths_and_percentages_resolve_against_the_border_box()
    {
        DisplayListBuilder.ResolveTransformOrigin(
                new CssValueList([new CssLength(10, CssLengthUnit.Px), new CssLength(20, CssLengthUnit.Px)]), 200, 100)
            .Should().Be((10d, 20d));
        DisplayListBuilder.ResolveTransformOrigin(
                new CssValueList([new CssPercentage(100), new CssPercentage(100)]), 200, 100)
            .Should().Be((200d, 100d));
        DisplayListBuilder.ResolveTransformOrigin(
                new CssValueList([new CssPercentage(25), new CssPercentage(75)]), 200, 100)
            .Should().Be((50d, 75d));
    }

    [TestMethod]
    public void Transform_origin_single_value_centres_the_other_axis()
    {
        DisplayListBuilder.ResolveTransformOrigin(new CssKeyword("left"), 200, 100)
            .Should().Be((0d, 50d));
        DisplayListBuilder.ResolveTransformOrigin(new CssKeyword("bottom"), 200, 100)
            .Should().Be((100d, 100d));
        DisplayListBuilder.ResolveTransformOrigin(new CssPercentage(100), 200, 100)
            .Should().Be((200d, 50d));
    }

    [TestMethod]
    public void Transform_origin_third_z_component_is_ignored()
    {
        DisplayListBuilder.ResolveTransformOrigin(
                new CssValueList([
                    new CssLength(10, CssLengthUnit.Px),
                    new CssLength(20, CssLengthUnit.Px),
                    new CssNumber(0)]), 200, 100)
            .Should().Be((10d, 20d));
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Starling.Paint.DisplayList.DisplayList BuildList(string html)
    {
        var document = HtmlParser.Parse(html);
        var style = new StyleEngine();
        var engine = new LayoutEngine(style, DefaultTextMeasurer.Instance);
        var root = engine.LayoutDocument(document, new LayoutSize(400, 400));
        return new DisplayListBuilder().Build(root);
    }

    private static PushTransform FirstPush(Starling.Paint.DisplayList.DisplayList dl)
    {
        foreach (var item in dl.Items)
        {
            if (item is PushTransform push)
            {
                return push;
            }
        }

        throw new InvalidOperationException("display list has no PushTransform");
    }

    /// <summary>
    /// Renders <see cref="ImgDoc"/> with <c>object-fit: fill</c> and a solid
    /// magenta source, and returns the first magenta pixel — the image's
    /// content-box origin in page coordinates. Layout is identical across
    /// fits (object-fit/-position are paint-only), so probes in the actual
    /// test render can be made relative to this origin.
    /// </summary>
    private static (int X, int Y) ImageBoxOrigin()
    {
        using var magenta = SolidSwatch(40, 20, Magenta);
        using var reference = Render(ImgDoc("object-fit:fill"), magenta);
        return FirstMatch(reference, p => p.R > 150 && p.B > 150 && p.G < 150);
    }

    private static RenderedBitmap Render(string html, Image<Rgba32>? swatch, int viewW = 200, int viewH = 100)
    {
        var document = HtmlParser.Parse(html);
        ManualImageResolver? resolver = null;
        if (swatch is not null)
        {
            resolver = new ManualImageResolver();
            resolver.Add(FindImg(document), swatch);
        }
        var painter = new Painter();
        return painter.RenderDocument(document, new LayoutSize(viewW, viewH), defaultFontSize: 16f, images: resolver);
    }

    private static (int X, int Y) FirstMatch(RenderedBitmap bmp, Func<(byte R, byte G, byte B, byte A), bool> match)
    {
        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                if (match(bmp.GetPixel(x, y)))
                {
                    return (x, y);
                }
            }
        }

        throw new InvalidOperationException("no matching pixel found");
    }

    private static bool IsRed((byte R, byte G, byte B, byte A) p) => p.R > 180 && p.G < 80 && p.B < 80;
    private static bool IsBlue((byte R, byte G, byte B, byte A) p) => p.B > 180 && p.R < 80 && p.G < 80;
    private static bool IsLime((byte R, byte G, byte B, byte A) p) => p.G > 180 && p.R < 80 && p.B < 80;

    private static Image<Rgba32> SolidSwatch(int w, int h, Rgba32 color)
    {
        var image = new Image<Rgba32>(w, h);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = color;
                }
            }
        });
        return image;
    }

    /// <summary>Left quarter <paramref name="left"/>, the rest <paramref name="rest"/>.</summary>
    private static Image<Rgba32> LeftQuarterSwatch(int w, int h, Rgba32 left, Rgba32 rest)
    {
        var image = new Image<Rgba32>(w, h);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = x < w / 4 ? left : rest;
                }
            }
        });
        return image;
    }

    /// <summary>Top half <paramref name="top"/>, bottom half <paramref name="bottom"/>.</summary>
    private static Image<Rgba32> TopBottomSwatch(int w, int h, Rgba32 top, Rgba32 bottom)
    {
        var image = new Image<Rgba32>(w, h);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                var color = y < h / 2 ? top : bottom;
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = color;
                }
            }
        });
        return image;
    }

    private static Element FindImg(Document document)
    {
        foreach (var img in document.GetElementsByTagName("img"))
        {
            return img;
        }

        throw new InvalidOperationException("no <img> in fixture");
    }

    private sealed class ManualImageResolver : IImageResolver
    {
        private readonly Dictionary<Element, ResolvedImage> _byElement = [];

        public void Add(Element element, Image<Rgba32> image)
        {
            var decoded = DecodedImage.CreatePooled(
                image.Width, image.Height, span => image.CopyPixelDataTo(span));
            _byElement[element] = new ResolvedImage(image.Width, image.Height, decoded);
        }

        public bool TryResolve(Element element, out ResolvedImage image)
            => _byElement.TryGetValue(element, out image);
    }
}
