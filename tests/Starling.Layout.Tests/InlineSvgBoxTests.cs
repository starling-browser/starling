using AwesomeAssertions;
using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Css.Values;
using Starling.Dom;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Layout.Text;
using Starling.Layout.Tree;
namespace Starling.Layout.Tests;

/// <summary>
/// An inline <c>&lt;svg&gt;</c> is built as a replaced <see cref="ImageBox"/>
/// when the image resolver can rasterize it (so it is sized by the same CSS
/// width/height path as <c>&lt;img&gt;</c>), and degrades to the element's
/// accessible name when it cannot.
/// </summary>
[TestClass]
public sealed class InlineSvgBoxTests
{
    /// <summary>Resolver that hands back a fixed 24×24 raster for any inline svg.</summary>
    private sealed class FakeSvgResolver : IImageResolver
    {
        public CssColor? LastCurrentColor;
        public bool TryResolve(Element element, out ResolvedImage image) { image = default; return false; }
        public bool TryResolveInlineSvg(Element svg, CssColor currentColor, out ResolvedImage image)
        {
            LastCurrentColor = currentColor;
            var decoded = DecodedImage.FromBuffer(24, 24, new byte[24 * 24 * 4]);
            image = new ResolvedImage(24, 24, decoded);
            return true;
        }
    }

    private static BlockBox Layout(string html, IImageResolver images, Size viewport)
        => new LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance, images)
            .LayoutDocument(HtmlParser.Parse(html), viewport);

    private static T? Find<T>(Box.Box box) where T : Box.Box
    {
        if (box is T t)
        {
            return t;
        }

        foreach (var c in box.Children)
        {
            if (Find<T>(c) is { } hit)
            {
                return hit;
            }
        }

        return null;
    }

    [TestMethod]
    public void Inline_svg_becomes_an_image_box_sized_by_css()
    {
        var resolver = new FakeSvgResolver();
        var root = Layout(
            """<body><a style="color:#8a8fa3"><svg viewBox="0 0 24 24" style="width:16px;height:16px"></svg>x</a></body>""",
            resolver, new Size(400, 300));

        var img = Find<ImageBox>(root);
        img.Should().NotBeNull();
        // CSS width/height win over the 24×24 intrinsic raster.
        img!.Frame.Width.Should().BeApproximately(16, 0.5);
        img.Frame.Height.Should().BeApproximately(16, 0.5);
        // currentColor was taken from the (inherited) computed color.
        resolver.LastCurrentColor.Should().NotBeNull();
        (resolver.LastCurrentColor!.R, resolver.LastCurrentColor.G, resolver.LastCurrentColor.B)
            .Should().Be(((byte)0x8a, (byte)0x8f, (byte)0xa3));
    }

    [TestMethod]
    public void Inline_svg_falls_back_to_accessible_name_when_unrenderable()
    {
        // NullImageResolver never resolves → degrade to aria-label text.
        var root = Layout(
            """<body><span><svg aria-label="Menu" viewBox="0 0 24 24"></svg></span></body>""",
            NullImageResolver.Instance, new Size(400, 300));

        Find<ImageBox>(root).Should().BeNull();
        var text = Find<TextBox>(root);
        text.Should().NotBeNull();
        text!.Text.Should().Be("Menu");
    }
}
