using AwesomeAssertions;
using Starling.Common.Image;
using Starling.Css.Animations;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Dom;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Box;
using Starling.Layout.Compositor;
using Starling.Layout.Text;
using Starling.Paint.DisplayList;
using Starling.Paint.Backend;
using Starling.Paint.Compositor;
using CompositorEngine = Starling.Paint.Compositor.Compositor;
using LayoutRect = Starling.Layout.Rect;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint.Tests;

/// <summary>
/// LTF-01: a per-frame predicate promotes an actively-animating element to its
/// own compositor layer even with no static <see cref="LayerHint"/>. A
/// composite-time transform is applied at composite (slice stays upright); any
/// other animated paint property re-rasters the element's own slice. Promotion
/// must not change pixels — the layer-tree render stays byte/SSIM-identical to
/// the flat render at a fixed clock.
/// </summary>
[TestClass]
public sealed class LayerPromotionTests
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
    public void Predicate_promotes_a_hintless_box_to_its_own_layer()
    {
        var doc = HtmlParser.Parse(
            "<body style=\"margin:0\"><div id=x style=\"position:absolute;left:10px;top:10px;" +
            "width:40px;height:40px;background-color:#ff0000\"></div></body>");
        var root = new LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance)
            .LayoutDocument(doc, new Size(200, 200));
        var el = doc.GetElementById("x")!;

        // The div has no static hint, so without the predicate it stays in the root layer.
        Find(root, el)!.Hints.Should().Be(LayerHint.None);
        new LayerTreeBuilder().Build(root).Children.Should().BeEmpty();

        // With the predicate promoting it, the div becomes a child layer.
        var promoted = new LayerTreeBuilder(isAnimatingLayerRoot: box => ReferenceEquals(box.Element, el)).Build(root);
        promoted.Children.Should().HaveCount(1, "the animating div is promoted to its own layer");
    }

    [TestMethod]
    public void Promotion_is_a_no_op_without_the_predicate()
    {
        var doc = HtmlParser.Parse(
            "<body style=\"margin:0\"><div style=\"width:40px;height:40px;background-color:#00ff00\"></div></body>");
        var root = new LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance)
            .LayoutDocument(doc, new Size(200, 200));

        new LayerTreeBuilder().Build(root).Children.Should().BeEmpty(
            "a plain non-animating box is never promoted");
    }

    [TestMethod]
    public void Animated_transform_promoted_by_predicate_matches_the_flat_render()
    {
        const int W = 200, H = 200;
        const float scale = 1f;
        const double clock = 500; // mid-way through a 0->60deg, 1000ms linear spin

        var style = new StyleEngine();
        style.AddStyleSheet(CssParser.ParseStyleSheet(
            "@keyframes spin { from { transform: rotate(0deg); } to { transform: rotate(60deg); } } " +
            "#x { animation: spin 1000ms linear both; position:absolute; left:50px; top:50px; " +
            "width:100px; height:60px; background-color:#cc2222; }"));
        var doc = HtmlParser.Parse("<body style=\"margin:0\"><div id=x>Spin</div></body>");
        var engine = new LayoutEngine(style, DefaultTextMeasurer.Instance);
        var root = engine.LayoutDocument(doc, new Size(W, H));
        var el = doc.GetElementById("x")!;

        // Prime the declarative animation onto the engine the way the live loop does,
        // then advance its clock.
        PrimeAnimations(style, root);
        style.AnimationEngine.Tick(clock);

        // The transform lives only in @keyframes, so the static box carries no hint.
        Find(root, el)!.Hints.Should().Be(LayerHint.None, "the transform is animated, not static — no static hint");
        style.AnimationEngine.ActiveProperties(el).Should().Contain(
            Starling.Css.Properties.PropertyId.Transform, "the spin animation targets transform");

        Func<Box, ComputedStyle?> styleOverride = box =>
            box.Element is { } e && style.AnimationEngine.ActiveProperties(e).Any()
                ? style.ComputeWithAnimations(e, clock)
                : null;
        Func<Box, bool> promote = box =>
            box.Element is { } e && style.AnimationEngine.ActiveProperties(e).Any();

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);

        // Flat path bakes the sampled rotation into the display list.
        PaintList flatList = new DisplayListBuilder().Build(root, null, styleOverride);
        using var flat = backend.Render(flatList, new LayoutRect(0, 0, W, H), scale);

        // Layer path: the predicate promotes the spinning div; its slice is upright
        // and the composite applies the rotation.
        var tree = new LayerTreeBuilder(styleOverride, isAnimatingLayerRoot: promote).Build(root);
        tree.Children.Should().HaveCount(1, "the predicate promotes the spinning div");
        tree.Children[0].Transform.IsIdentity.Should().BeFalse("the sampled rotation rides on the layer transform");
        using var layered = new CompositorEngine(backend).Render(tree, new LayoutRect(0, 0, W, H), scale);

        layered.Width.Should().Be(flat.Width);
        layered.Height.Should().Be(flat.Height);
        var ssim = Ssim.ComputeRgba(layered.Rgba, flat.Rgba, layered.Width, layered.Height);
        ssim.Should().BeGreaterThanOrEqualTo(0.99,
            "promoting an animating transform must not change pixels vs the flat path");
    }

    private static void PrimeAnimations(StyleEngine style, Box box)
    {
        if (box.Element is { } el && box.Style is { } cs)
        {
            var decls = AnimationCompositor.BuildDeclarations(cs);
            if (decls.Count > 0)
            {
                style.AnimationEngine.OnAnimationsCascaded(el, decls);
            }
        }
        foreach (var c in box.Children)
        {
            PrimeAnimations(style, c);
        }
    }
}
