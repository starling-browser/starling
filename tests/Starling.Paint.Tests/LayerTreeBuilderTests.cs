using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Box;
using Starling.Layout.Text;
using Starling.Paint.Compositor;
using Starling.Paint.DisplayList;

namespace Starling.Paint.Tests;

/// <summary>
/// Structure-level tests for <see cref="LayerTreeBuilder"/> (M12-04): the layer
/// tree topology and z-index paint ordering, independent of rasterization.
/// </summary>
[TestClass]
public sealed class LayerTreeBuilderTests
{
    private static BlockBox Layout(string html, int w = 400, int h = 400)
    {
        var document = HtmlParser.Parse(html);
        var engine = new LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance);
        return engine.LayoutDocument(document, new Size(w, h));
    }

    [TestMethod]
    public void Unpromoted_page_yields_a_single_root_layer_with_no_children()
    {
        var root = Layout("<body><div style=\"width:100px;height:100px;background-color:#ff0000\">x</div></body>");

        var tree = new LayerTreeBuilder().Build(root);

        tree.Should().NotBeNull();
        tree.Children.Should().BeEmpty("a page with no promoted boxes is a single-node layer tree");
        tree.Transform.IsIdentity.Should().BeTrue();
        tree.Opacity.Should().Be(1f);
        tree.Clip.Should().BeNull();
        // The whole document's paint lives in the one root slice.
        tree.Items.Items.Should().NotBeEmpty();
    }

    [TestMethod]
    public void Two_promoted_divs_become_child_layers_of_the_root()
    {
        var root = Layout(
            "<body>" +
            "<div style=\"opacity:0.5;width:50px;height:50px;background-color:#ff0000\">a</div>" +
            "<div style=\"opacity:0.5;width:50px;height:50px;background-color:#0000ff\">b</div>" +
            "</body>");

        var tree = new LayerTreeBuilder().Build(root);

        tree.Children.Should().HaveCount(2, "each opacity<1 div is promoted onto its own layer");
        tree.Children.Should().AllSatisfy(c => c.Opacity.Should().Be(0.5f));
    }

    [TestMethod]
    public void Child_layers_are_stored_in_zindex_paint_order()
    {
        // Three positioned, promoted divs declared in tree order C(z=1) A(z=-1)
        // B(z=0). A positioned box is only promoted with an explicit z-index
        // (z-index:auto does not establish a stacking context), so all three
        // carry one. CSS-Position-3 §9 paint order: negative below, then 0/auto
        // in tree order, then positive. Expected painted order: A(-1), B(0),
        // C(1).
        var root = Layout(
            "<body>" +
            "<div id=c style=\"position:absolute;z-index:1;width:10px;height:10px;background-color:#ff0000\"></div>" +
            "<div id=a style=\"position:absolute;z-index:-1;width:10px;height:10px;background-color:#00ff00\"></div>" +
            "<div id=b style=\"position:absolute;z-index:0;width:10px;height:10px;background-color:#0000ff\"></div>" +
            "</body>");

        var tree = new LayerTreeBuilder().Build(root);

        tree.Children.Should().HaveCount(3);
        // Identify each child layer by the unique background colour in its slice.
        Color(tree.Children[0]).Should().Be(0x00ff00u, "z=-1 paints first (below)");
        Color(tree.Children[1]).Should().Be(0x0000ffu, "z=0 paints in tree order after negatives");
        Color(tree.Children[2]).Should().Be(0xff0000u, "z=1 paints last (on top)");
    }

    [TestMethod]
    public void Promoted_subtree_items_are_excluded_from_the_root_slice()
    {
        var root = Layout(
            "<body>" +
            "<div style=\"opacity:0.5;width:40px;height:40px;background-color:#123456\">a</div>" +
            "</body>");

        var tree = new LayerTreeBuilder().Build(root);

        // The promoted div's fill is in the child slice, not the root slice.
        RootSliceHasColor(tree, 0x123456u).Should().BeFalse(
            "the promoted box paints into its own layer, not the root layer slice");
        tree.Children.Should().ContainSingle();
        Color(tree.Children[0]).Should().Be(0x123456u);
    }

    private static uint? Color(CompositorLayer layer)
    {
        foreach (var item in layer.Items.Items)
        {
            if (item is FillRect f)
            {
                return ((uint)f.Color.R << 16) | ((uint)f.Color.G << 8) | f.Color.B;
            }
        }

        return null;
    }

    private static bool RootSliceHasColor(CompositorLayer root, uint rgb)
    {
        foreach (var item in root.Items.Items)
        {
            if (item is FillRect f && ((((uint)f.Color.R << 16) | ((uint)f.Color.G << 8) | f.Color.B) == rgb))
            {
                return true;
            }
        }

        return false;
    }
}
