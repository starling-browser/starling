using AwesomeAssertions;
using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Dom;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Box;
using Starling.Layout.Text;
using Starling.Paint.Backend;
using Starling.Paint.Compositor;
using CompositorEngine = Starling.Paint.Compositor.Compositor;
using LayoutRect = Starling.Layout.Rect;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint.Tests;

/// <summary>
/// LTF-06: a subtree a script mutates each frame is promoted to its own small
/// layer, so the base layer's slice content hash stays stable and serves from
/// cache — only the mutated layer re-rasters. Also covers the Document-side
/// recently-mutated tracker + its hysteresis decay.
/// </summary>
[TestClass]
public sealed class LayerMutationIsolationTests
{
    [TestMethod]
    public void Document_tracks_recently_mutated_element_and_decays_after_window()
    {
        var doc = HtmlParser.Parse("<body><span id=s>a</span></body>");
        doc.RecordLayoutMutations = true;
        var status = doc.GetElementById("s")!;
        var text = FirstText(status)!;

        text.Data = "b"; // a connected text mutation marks the parent element
        doc.WasRecentlyMutated(status).Should().BeTrue("a fresh text mutation promotes the element");

        // Hysteresis window is a few frames (RecentMutationFrames = 3).
        doc.DecayRecentMutations();
        doc.WasRecentlyMutated(status).Should().BeTrue("still inside the hysteresis window");
        doc.DecayRecentMutations();
        doc.WasRecentlyMutated(status).Should().BeTrue();
        doc.DecayRecentMutations();
        doc.WasRecentlyMutated(status).Should().BeFalse("the promotion window has elapsed");
    }

    [TestMethod]
    public void Mutated_subtree_isolates_so_the_base_layer_serves_from_cache()
    {
        const int W = 240, H = 400;
        const float scale = 1f;
        // An absolutely-positioned status line over a static base. Promoting it
        // keeps its text out of the base slice, so a text-only change leaves the
        // base hash stable. The viewport is tall enough to contain both layers so
        // the tile path rasters them (it culls tiles outside the viewport, LTF/M12-05).
        var html =
            "<body style=\"margin:0\">" +
            "<div style=\"width:240px;height:200px;background-color:#dde3ff\">base content</div>" +
            "<div id=status style=\"position:absolute;left:0;top:170px;width:240px;height:20px\">running 16 ms</div>" +
            "</body>";
        var doc = HtmlParser.Parse(html);
        var style = new StyleEngine();
        var engine = new LayoutEngine(style, DefaultTextMeasurer.Instance);
        var size = new Size(W, H);
        var status = doc.GetElementById("status")!;

        using var inner = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var counting = new CountingBackend(inner);
        var tiles = new TileGrid();
        var compositor = new CompositorEngine(counting, tiles);

        // The status element is the promoted (recently-mutated) layer.
        bool Promote(Box box) => ReferenceEquals(box.Element, status);

        for (var f = 0; f < 4; f++)
        {
            FirstText(status)!.Data = f % 2 == 0 ? "running 16 ms" : "running 32 ms";
            var root = engine.LayoutDocument(doc, size);
            var tree = new LayerTreeBuilder(isAnimatingLayerRoot: Promote,
                layerIdFor: tiles.LayerIdFor).Build(root);
            tree.Children.Should().HaveCount(1, "the status line is promoted to its own layer");

            var before = counting.RenderCount;
            using (compositor.Render(tree, new LayoutRect(0, 0, W, H), scale)) { }
            var rastered = counting.RenderCount - before;

            if (f == 0)
            {
                rastered.Should().Be(2, "the first frame rasters the base layer and the status layer");
            }
            else
            {
                rastered.Should().Be(1,
                    "only the mutated status layer re-rasters; the base layer serves from cache (LTF-06)");
            }
        }
    }

    private static Text? FirstText(Element element)
    {
        for (var child = element.FirstChild; child is not null; child = child.NextSibling)
        {
            if (child is Text t)
            {
                return t;
            }
        }

        return null;
    }

    private sealed class CountingBackend(IPaintBackend inner) : IPaintBackend
    {
        public int RenderCount { get; private set; }
        public string Name => inner.Name;
        public RenderedBitmap Render(PaintList list, LayoutRect viewport, float scale = 1.0f)
        {
            RenderCount++;
            return inner.Render(list, viewport, scale);
        }
        public RenderedBitmap Render(PaintList list, LayoutRect viewport, float scale, bool opaqueBackground)
        {
            RenderCount++;
            return inner.Render(list, viewport, scale, opaqueBackground);
        }
        public void Dispose() => inner.Dispose();
    }
}
