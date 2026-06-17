using AwesomeAssertions;
using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Dom;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Layout.Tree;

namespace Starling.Layout.Tests;

/// <summary>
/// CSS 2.1 §10.3.2 / §10.4 — used dimensions of a replaced element (<c>&lt;img&gt;</c>)
/// honour computed <c>width</c>, <c>height</c>, <c>min-*</c>, and <c>max-*</c>.
///
/// Regression: https://docs.htmlcsstoimage.com renders the just-the-docs theme
/// which ships <c>img { max-width: 100%; height: auto; }</c>. Prior to this
/// fix Starling ignored author CSS on <c>&lt;img&gt;</c> entirely and painted
/// at intrinsic pixel size, so the in-page hero image blew out the column.
/// </summary>
[TestClass]
public sealed class ImageReplacedSizingTests
{
    private static DecodedImage MakeImage(int w = 1, int h = 1)
        => DecodedImage.CreatePooled(w, h, span => span.Fill(0xff));

    private sealed class FixedImageResolver : IImageResolver
    {
        private readonly double _w;
        private readonly double _h;
        private readonly DecodedImage _image;

        public FixedImageResolver(double w, double h)
        {
            _w = w;
            _h = h;
            _image = MakeImage();
        }

        public bool TryResolve(Element element, out ResolvedImage image)
        {
            image = new ResolvedImage(_w, _h, _image);
            return true;
        }
    }

    private static BlockBox Layout(string html, double intrinsicW, double intrinsicH, Size viewport)
    {
        var resolver = new FixedImageResolver(intrinsicW, intrinsicH);
        var engine = new LayoutEngine(new StyleEngine(), images: resolver);
        return engine.LayoutDocument(HtmlParser.Parse(html), viewport);
    }

    private static ImageBox FindImage(Box.Box root)
    {
        if (root is ImageBox img)
        {
            return img;
        }

        foreach (var child in root.Children)
        {
            var hit = TryFind(child);
            if (hit is not null)
            {
                return hit;
            }
        }
        throw new InvalidOperationException("No ImageBox in tree");

        static ImageBox? TryFind(Box.Box b)
        {
            if (b is ImageBox i)
            {
                return i;
            }

            foreach (var c in b.Children)
            {
                var h = TryFind(c);
                if (h is not null)
                {
                    return h;
                }
            }
            return null;
        }
    }

    [TestMethod]
    public void Img_without_css_uses_intrinsic_dimensions()
    {
        // Sanity: no author CSS, the box keeps the intrinsic 200×100.
        var root = Layout(
            """<body style="margin:0"><img src="x.png"></body>""",
            intrinsicW: 200, intrinsicH: 100, new Size(800, 600));

        var img = FindImage(root);
        img.Frame.Width.Should().Be(200);
        img.Frame.Height.Should().Be(100);
    }

    [TestMethod]
    public void Img_max_width_100_percent_scales_oversized_image_down_to_container()
    {
        // The docs.htmlcsstoimage.com regression: a 1200×600 image inside a
        // 400-px column with `max-width: 100%; height: auto;` must shrink to
        // 400×200 (preserving aspect ratio), not paint at 1200×600.
        var root = Layout(
            """
            <body style="margin:0">
              <div style="width:400px">
                <img src="big.png" style="max-width:100%; height:auto">
              </div>
            </body>
            """,
            intrinsicW: 1200, intrinsicH: 600, new Size(800, 600));

        var img = FindImage(root);
        img.Frame.Width.Should().BeApproximately(400, 0.5);
        img.Frame.Height.Should().BeApproximately(200, 0.5,
            "height:auto with max-width:100% must preserve the 2:1 aspect ratio");
    }

    [TestMethod]
    public void Img_max_width_100_percent_leaves_smaller_image_alone()
    {
        // max-width is an *upper* bound: a naturally-narrow image keeps its
        // intrinsic size when it already fits.
        var root = Layout(
            """
            <body style="margin:0">
              <div style="width:400px">
                <img src="small.png" style="max-width:100%; height:auto">
              </div>
            </body>
            """,
            intrinsicW: 120, intrinsicH: 60, new Size(800, 600));

        var img = FindImage(root);
        img.Frame.Width.Should().BeApproximately(120, 0.5);
        img.Frame.Height.Should().BeApproximately(60, 0.5);
    }

    [TestMethod]
    public void Img_width_percentage_resolves_against_container()
    {
        // 50% of a 600-px column = 300 px wide; height auto preserves ratio.
        var root = Layout(
            """
            <body style="margin:0">
              <div style="width:600px">
                <img src="x.png" style="width:50%; height:auto">
              </div>
            </body>
            """,
            intrinsicW: 1000, intrinsicH: 500, new Size(800, 600));

        var img = FindImage(root);
        img.Frame.Width.Should().BeApproximately(300, 0.5);
        img.Frame.Height.Should().BeApproximately(150, 0.5);
    }

    [TestMethod]
    public void Img_explicit_width_and_height_are_used_verbatim()
    {
        // Both axes specified: no aspect-ratio preservation, no clamping.
        var root = Layout(
            """<body style="margin:0"><img src="x.png" style="width:50px; height:80px"></body>""",
            intrinsicW: 1000, intrinsicH: 1000, new Size(800, 600));

        var img = FindImage(root);
        img.Frame.Width.Should().BeApproximately(50, 0.5);
        img.Frame.Height.Should().BeApproximately(80, 0.5);
    }

    [TestMethod]
    public void Img_only_width_specified_preserves_aspect_ratio()
    {
        var root = Layout(
            """<body style="margin:0"><img src="x.png" style="width:100px"></body>""",
            intrinsicW: 400, intrinsicH: 200, new Size(800, 600));

        var img = FindImage(root);
        img.Frame.Width.Should().BeApproximately(100, 0.5);
        img.Frame.Height.Should().BeApproximately(50, 0.5);
    }

    [TestMethod]
    public void Img_only_height_specified_preserves_aspect_ratio()
    {
        var root = Layout(
            """<body style="margin:0"><img src="x.png" style="height:25px"></body>""",
            intrinsicW: 400, intrinsicH: 200, new Size(800, 600));

        var img = FindImage(root);
        img.Frame.Height.Should().BeApproximately(25, 0.5);
        img.Frame.Width.Should().BeApproximately(50, 0.5);
    }

    [TestMethod]
    public void Img_max_height_clamps_and_preserves_aspect_ratio_when_width_auto()
    {
        // Intrinsic 400×200, max-height 50 ⇒ scaled to 100×50.
        var root = Layout(
            """<body style="margin:0"><img src="x.png" style="max-height:50px"></body>""",
            intrinsicW: 400, intrinsicH: 200, new Size(800, 600));

        var img = FindImage(root);
        img.Frame.Height.Should().BeApproximately(50, 0.5);
        img.Frame.Width.Should().BeApproximately(100, 0.5);
    }

    [TestMethod]
    public void Img_min_width_floor_expands_smaller_image()
    {
        // Intrinsic 40×20, min-width 100 ⇒ expanded to 100×50 (ratio kept
        // because height is auto/intrinsic).
        var root = Layout(
            """<body style="margin:0"><img src="x.png" style="min-width:100px"></body>""",
            intrinsicW: 40, intrinsicH: 20, new Size(800, 600));

        var img = FindImage(root);
        img.Frame.Width.Should().BeApproximately(100, 0.5);
        img.Frame.Height.Should().BeApproximately(50, 0.5);
    }

    [TestMethod]
    public void Img_max_width_none_disables_the_clamp()
    {
        // `none` is the initial value: an explicit `max-width: none` must
        // not collapse the image to zero (the same regression class the
        // block-level max-width tests guard against).
        var root = Layout(
            """<body style="margin:0"><img src="x.png" style="max-width:none"></body>""",
            intrinsicW: 1200, intrinsicH: 600, new Size(400, 600));

        var img = FindImage(root);
        img.Frame.Width.Should().BeApproximately(1200, 0.5);
        img.Frame.Height.Should().BeApproximately(600, 0.5);
    }

    [TestMethod]
    public void Img_explicit_width_overrides_html_width_attribute()
    {
        // The HTML `width` attribute is a presentational hint; author CSS
        // outranks it. Intrinsic 1200×600 with HTML width=800 would land at
        // 800×400 in BoxTreeBuilder, but the inline CSS `width:200px` must
        // win at layout time.
        var root = Layout(
            """<body style="margin:0"><img src="x.png" width="800" style="width:200px; height:auto"></body>""",
            intrinsicW: 1200, intrinsicH: 600, new Size(900, 600));

        var img = FindImage(root);
        img.Frame.Width.Should().BeApproximately(200, 0.5);
        // Height tracks the *box-tree* aspect ratio (which folded in the
        // 800×400 HTML hint), so 200 / 800 * 400 = 100.
        img.Frame.Height.Should().BeApproximately(100, 0.5);
    }

    [TestMethod]
    public void Absolutely_positioned_image_height_auto_follows_intrinsic_ratio()
    {
        // CSS 2.1 §10.6.5 — an absolutely-positioned <img> with width set and
        // height auto takes its height from the intrinsic aspect ratio, not the
        // (zero) content height. Regression: the hero's decorative "echo" birds
        // collapsed to h=0 and never painted.
        var root = Layout(
            """<body><div style="position:relative; width:400px; height:300px">"""
            + """<img src="x.png" style="position:absolute; top:10px; left:10px; width:100px"></div></body>""",
            intrinsicW: 200, intrinsicH: 100, new Size(800, 600));

        var img = FindImage(root);
        img.Frame.Width.Should().BeApproximately(100, 0.5);
        img.Frame.Height.Should().BeApproximately(50, 0.5, "100px width at a 200:100 intrinsic ratio");
    }

    [TestMethod]
    public void Image_with_opacity_establishes_a_compositor_layer()
    {
        // Replaced elements must establish a stacking-context layer for opacity /
        // transform / filter, or the compositor never applies them and the image
        // paints at full strength. Regression: <img opacity:.1> rendered opaque.
        var root = Layout(
            """<body><img src="x.png" style="opacity:0.5"></body>""",
            intrinsicW: 80, intrinsicH: 80, new Size(800, 600));

        var img = FindImage(root);
        img.Hints.HasFlag(Compositor.LayerHint.OpacityLessThanOne).Should().BeTrue(
            "opacity < 1 on an <img> must establish a compositor layer");
    }
}
