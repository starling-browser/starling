using AwesomeAssertions;
using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Dom;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Layout.Text;
using Starling.Layout.Tree;
using Starling.Spec;

namespace Starling.Layout.Tests;

/// <summary>
/// css-sizing-4 §5 (aspect-ratio: transferred sizes) and css-sizing-3 §4-5
/// (intrinsic sizing keywords min-content / max-content / fit-content on the
/// inline axis) resolved in block layout.
/// </summary>
[TestClass]
[Spec("css-sizing-4", "https://drafts.csswg.org/css-sizing-4/")]
[Spec("css-sizing-3", "https://drafts.csswg.org/css-sizing-3/")]
public sealed class SizingKeywordsTests
{
    private static BlockBox Layout(string html, Size viewport)
        => new LayoutEngine(new StyleEngine()).LayoutDocument(HtmlParser.Parse(html), viewport);

    private static BlockBox LayoutWithImage(string html, double intrinsicW, double intrinsicH, Size viewport)
    {
        var engine = new LayoutEngine(new StyleEngine(), images: new FixedImageResolver(intrinsicW, intrinsicH));
        return engine.LayoutDocument(HtmlParser.Parse(html), viewport);
    }

    /// <summary>Advance width the test measurer assigns to <paramref name="text"/>
    /// at the default 16px font, so expectations track the heuristic measurer
    /// instead of hard-coding magic pixel numbers.</summary>
    private static double TextWidth(string text)
        => DefaultTextMeasurer.Instance.MeasureWidth(text, 16, FontSpec.Default);

    // ---- aspect-ratio (css-sizing-4 §5) ------------------------------------

    [Spec("css-sizing-4", "https://drafts.csswg.org/css-sizing-4/#aspect-ratio", section: "5")]
    [TestMethod]
    public void Img_with_explicit_width_derives_height_from_natural_ratio()
    {
        var root = LayoutWithImage(
            """<body style="margin:0"><img src="x.png" style="width:400px"></body>""",
            intrinsicW: 200, intrinsicH: 100, new Size(800, 600));

        var img = FindImage(root);
        img.Frame.Width.Should().Be(400);
        img.Frame.Height.Should().Be(200,
            "the 200x100 natural ratio transfers the 400px width into a 200px height");
    }

    [Spec("css-sizing-4", "https://drafts.csswg.org/css-sizing-4/#aspect-ratio", section: "5")]
    [TestMethod]
    public void Div_with_ratio_and_definite_width_derives_height()
    {
        var root = Layout(
            """<body style="margin:0"><div style="width:300px; aspect-ratio: 2 / 1"></div></body>""",
            new Size(800, 600));

        var div = FindBox(root, "div")!;
        div.Frame.Width.Should().Be(300);
        div.Frame.Height.Should().Be(150,
            "aspect-ratio: 2/1 transfers the definite 300px width into a 150px height");
    }

    [Spec("css-sizing-4", "https://drafts.csswg.org/css-sizing-4/#aspect-ratio-minimum", section: "5.2")]
    [TestMethod]
    public void Min_height_overrides_the_transferred_height()
    {
        var root = Layout(
            """<body style="margin:0"><div style="width:300px; aspect-ratio: 2 / 1; min-height:200px"></div></body>""",
            new Size(800, 600));

        var div = FindBox(root, "div")!;
        div.Frame.Height.Should().Be(200,
            "the transferred size respects the derived axis's min-height (150 -> 200)");
    }

    [Spec("css-sizing-4", "https://drafts.csswg.org/css-sizing-4/#aspect-ratio-minimum", section: "5.2")]
    [TestMethod]
    public void Max_height_caps_the_transferred_height()
    {
        var root = Layout(
            """<body style="margin:0"><div style="width:300px; aspect-ratio: 2 / 1; max-height:100px"></div></body>""",
            new Size(800, 600));

        var div = FindBox(root, "div")!;
        div.Frame.Height.Should().Be(100,
            "the transferred size respects the derived axis's max-height (150 -> 100)");
    }

    [Spec("css-sizing-4", "https://drafts.csswg.org/css-sizing-4/#aspect-ratio", section: "5.1")]
    [TestMethod]
    public void Aspect_ratio_property_overrides_natural_ratio_on_replaced()
    {
        var root = LayoutWithImage(
            """<body style="margin:0"><img src="x.png" style="width:400px; aspect-ratio: 1 / 1"></body>""",
            intrinsicW: 200, intrinsicH: 100, new Size(800, 600));

        var img = FindImage(root);
        img.Frame.Height.Should().Be(400,
            "a bare <ratio> beats the replaced element's natural 2:1 ratio");
    }

    [Spec("css-sizing-4", "https://drafts.csswg.org/css-sizing-4/#aspect-ratio", section: "5.1")]
    [TestMethod]
    public void Auto_with_ratio_prefers_the_natural_ratio_on_replaced()
    {
        var root = LayoutWithImage(
            """<body style="margin:0"><img src="x.png" style="width:400px; aspect-ratio: auto 1 / 1"></body>""",
            intrinsicW: 200, intrinsicH: 100, new Size(800, 600));

        var img = FindImage(root);
        img.Frame.Height.Should().Be(200,
            "`auto && <ratio>` uses the natural ratio when the replaced element has one");
    }

    [Spec("css-sizing-4", "https://drafts.csswg.org/css-sizing-4/#aspect-ratio", section: "5")]
    [TestMethod]
    public void Definite_height_transfers_into_the_auto_width()
    {
        var root = Layout(
            """<body style="margin:0"><div style="height:100px; aspect-ratio: 3 / 1"></div></body>""",
            new Size(800, 600));

        var div = FindBox(root, "div")!;
        div.Frame.Height.Should().Be(100);
        div.Frame.Width.Should().Be(300,
            "with width:auto and a definite height the ratio transfers the other way");
    }

    // ---- intrinsic sizing keywords (css-sizing-3 §4-5) ---------------------

    [Spec("css-sizing-3", "https://drafts.csswg.org/css-sizing-3/#max-content", section: "4.1")]
    [TestMethod]
    public void Width_max_content_is_the_no_wrap_width()
    {
        var root = Layout(
            """<body style="margin:0"><div style="width:max-content">aaaa bbbb cccc</div></body>""",
            new Size(800, 600));

        var div = FindBox(root, "div")!;
        div.Frame.Width.Should().BeApproximately(TextWidth("aaaa bbbb cccc"), 0.01,
            "max-content lays the text on a single line and takes that width");
    }

    [Spec("css-sizing-3", "https://drafts.csswg.org/css-sizing-3/#max-content", section: "4.1")]
    [TestMethod]
    public void Width_max_content_overflows_a_narrow_parent()
    {
        var root = Layout(
            """<body style="margin:0"><div style="width:100px"><div id="t" style="width:max-content">aaaa bbbb cccc dddd</div></div></body>""",
            new Size(800, 600));

        var inner = FindBoxById(root, "t")!;
        inner.Frame.Width.Should().BeApproximately(TextWidth("aaaa bbbb cccc dddd"), 0.01,
            "max-content does not clamp to the 100px available space — it overflows");
        inner.Frame.Width.Should().BeGreaterThan(100);
    }

    [Spec("css-sizing-3", "https://drafts.csswg.org/css-sizing-3/#min-content", section: "4.2")]
    [TestMethod]
    public void Width_min_content_wraps_at_the_longest_word()
    {
        var root = Layout(
            """<body style="margin:0"><div style="width:min-content">aa bbbbbb cc</div></body>""",
            new Size(800, 600));

        var div = FindBox(root, "div")!;
        div.Frame.Width.Should().BeApproximately(TextWidth("bbbbbb"), 0.01,
            "min-content takes every wrap opportunity, leaving the longest word");
    }

    [Spec("css-sizing-3", "https://drafts.csswg.org/css-sizing-3/#fit-content-size", section: "5.3")]
    [TestMethod]
    public void Fit_content_uses_max_content_when_space_allows()
    {
        var root = Layout(
            """<body style="margin:0"><div style="width:fit-content">aaaa aaaa</div></body>""",
            new Size(800, 600));

        var div = FindBox(root, "div")!;
        div.Frame.Width.Should().BeApproximately(TextWidth("aaaa aaaa"), 0.01,
            "fit-content = min(max-content, max(min-content, stretch)) -> max-content here");
    }

    [Spec("css-sizing-3", "https://drafts.csswg.org/css-sizing-3/#fit-content-size", section: "5.3")]
    [TestMethod]
    public void Fit_content_clamps_to_the_available_space()
    {
        var root = Layout(
            """<body style="margin:0"><div style="width:100px"><div id="t" style="width:fit-content">aaaa aaaa aaaa aaaa aaaa</div></div></body>""",
            new Size(800, 600));

        var inner = FindBoxById(root, "t")!;
        inner.Frame.Width.Should().Be(100,
            "min-content < 100px stretch < max-content, so fit-content takes the stretch size");
    }

    [Spec("css-sizing-3", "https://drafts.csswg.org/css-sizing-3/#fit-content-size", section: "5.3")]
    [TestMethod]
    public void Fit_content_never_drops_below_min_content()
    {
        var root = Layout(
            """<body style="margin:0"><div style="width:20px"><div id="t" style="width:fit-content">aaaa aaaa aaaa</div></div></body>""",
            new Size(800, 600));

        var inner = FindBoxById(root, "t")!;
        inner.Frame.Width.Should().BeApproximately(TextWidth("aaaa"), 0.01,
            "the 20px stretch is below min-content, so fit-content floors at min-content");
    }

    [Spec("css-sizing-3", "https://drafts.csswg.org/css-sizing-3/#sizing-values", section: "4")]
    [TestMethod]
    public void Max_width_max_content_caps_a_stretched_block()
    {
        var root = Layout(
            """<body style="margin:0"><div style="max-width:max-content">aaaa aaaa</div></body>""",
            new Size(800, 600));

        var div = FindBox(root, "div")!;
        div.Frame.Width.Should().BeApproximately(TextWidth("aaaa aaaa"), 0.01,
            "the auto width stretches to 800px, then max-width:max-content clamps it down");
    }

    [Spec("css-sizing-3", "https://drafts.csswg.org/css-sizing-3/#sizing-values", section: "4")]
    [TestMethod]
    public void Height_intrinsic_keyword_behaves_as_auto_in_horizontal_writing_mode()
    {
        var root = Layout(
            """<body style="margin:0"><div id="kw" style="height:min-content">aaaa</div><div id="auto">aaaa</div></body>""",
            new Size(800, 600));

        var keyword = FindBoxById(root, "kw")!;
        var auto = FindBoxById(root, "auto")!;
        keyword.Frame.Height.Should().Be(auto.Frame.Height,
            "block-axis intrinsic keywords behave as auto in a horizontal writing mode");
    }

    // ---- helpers ------------------------------------------------------------

    private static DecodedImage MakeImage()
        => DecodedImage.CreatePooled(1, 1, span => span.Fill(0xff));

    private sealed class FixedImageResolver : IImageResolver
    {
        private readonly double _w;
        private readonly double _h;
        private readonly DecodedImage _image = MakeImage();

        public FixedImageResolver(double w, double h)
        {
            _w = w;
            _h = h;
        }

        public bool TryResolve(Element element, out ResolvedImage image)
        {
            image = new ResolvedImage(_w, _h, _image);
            return true;
        }
    }

    private static ImageBox FindImage(Box.Box root)
    {
        var hit = TryFind(root);
        return hit ?? throw new InvalidOperationException("No ImageBox in tree");

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

    private static Box.Box? FindBox(Box.Box root, string localName)
    {
        if (root.Element?.LocalName == localName)
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var hit = FindBox(child, localName);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }

    private static Box.Box? FindBoxById(Box.Box root, string id)
    {
        if (root.Element?.GetAttribute("id") == id)
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var hit = FindBoxById(child, id);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }
}
