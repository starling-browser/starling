using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Dom;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Box;
using Starling.Layout.Compositor;
using Starling.Layout.Text;
using Starling.Paint.Backend;
using Starling.Paint.Compositor;
using Starling.Paint.DisplayList;
using CompositorEngine = Starling.Paint.Compositor.Compositor;
using LayoutRect = Starling.Layout.Rect;

namespace Starling.Paint.Tests;

/// <summary>
/// Filter Effects 2 §6 — `backdrop-filter` on the compositor paths. A box with
/// a backdrop-filter chain is promoted to its own layer (new
/// <see cref="LayerHint.BackdropFilter"/>), the chain rides the layer
/// (<see cref="CompositorLayer.BackdropFilters"/>), and the blend stage
/// snapshots the pixels already composited under the element, filters them,
/// and draws them back before the element's own content paints.
/// </summary>
[TestClass]
public sealed class BackdropFilterLayerTests
{
    private static Box? Find(Box box, Element el)
    {
        if (ReferenceEquals(box.Element, el))
        {
            return box;
        }

        foreach (var c in box.Children)
        {
            if (Find(c, el) is { } f)
            {
                return f;
            }
        }

        return null;
    }

    [TestMethod]
    public void Backdrop_filter_promotes_the_box_to_its_own_layer_and_rides_it()
    {
        var doc = HtmlParser.Parse(
            "<body style=\"margin:0\"><div id=frost style=\"position:absolute;left:20px;top:20px;" +
            "width:80px;height:80px;backdrop-filter:blur(8px)\"></div></body>");
        var root = new LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance)
            .LayoutDocument(doc, new Size(200, 200));
        var el = doc.GetElementById("frost")!;

        Find(root, el)!.Hints.Should().HaveFlag(LayerHint.BackdropFilter,
            "backdrop-filter creates a stacking context (Filter Effects 2 §6)");

        var tree = new LayerTreeBuilder().Build(root);
        tree.Children.Should().HaveCount(1, "the backdrop-filter box becomes its own compositor layer");

        var layer = tree.Children[0];
        layer.BackdropFilters.Should().NotBeNull("the resolved chain rides the layer");
        layer.BackdropFilters.Should().ContainSingle()
            .Which.Should().Be(new FilterFunction(FilterFunctionKind.Blur, 8));
        layer.BackdropBounds.Should().Be(new LayoutRect(20, 20, 80, 80),
            "the chain applies under the element's border box in page coords");
    }

    [TestMethod]
    public void Backdrop_blur_mixes_the_pixels_composited_under_the_element()
    {
        // A red|blue split background with a frosted panel straddling the seam.
        var doc = HtmlParser.Parse(
            "<body style=\"margin:0\">" +
            "<div style=\"position:absolute;left:0;top:0;width:100px;height:200px;background-color:#ff0000\"></div>" +
            "<div style=\"position:absolute;left:100px;top:0;width:100px;height:200px;background-color:#0000ff\"></div>" +
            "<div id=frost style=\"position:absolute;left:50px;top:50px;width:100px;height:100px;" +
            "backdrop-filter:blur(10px)\"></div>" +
            "</body>");
        var root = new LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance)
            .LayoutDocument(doc, new Size(200, 200));

        var tree = new LayerTreeBuilder().Build(root);
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        var compositor = new CompositorEngine(backend) { DisableGpuBlend = true };
        using var bmp = compositor.Render(tree, new LayoutRect(0, 0, 200, 200), 1f);

        // Outside the panel the seam stays razor sharp.
        var sharp = bmp.GetPixel(95, 10);
        sharp.R.Should().Be(255, "outside the panel the red half is unfiltered");
        sharp.B.Should().Be(0, "outside the panel no blue bleeds across the seam");

        // Inside the panel, 5px from the seam, the blur mixes red and blue.
        var frosted = bmp.GetPixel(95, 100);
        frosted.B.Should().BeGreaterThan(30, "the backdrop blur pulls blue across the seam");
        frosted.R.Should().BeLessThan(240, "the backdrop blur dilutes the red side");
    }
}
