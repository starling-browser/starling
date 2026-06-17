using AwesomeAssertions;
using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Box;
using Starling.Layout.Text;
using Starling.Paint.Backend;
using Starling.Paint.Compositor;
using Starling.Paint.DisplayList;
using CompositorEngine = Starling.Paint.Compositor.Compositor;
using LayoutRect = Starling.Layout.Rect;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint.Tests;

/// <summary>
/// Acceptance tests for the M12-04 layer compositor: transform parity vs. the
/// flat M5 path, opacity correctness, and per-layer cache isolation.
/// </summary>
[TestClass]
public sealed class CompositorTests
{
    private static BlockBox Layout(string html, int w, int h)
    {
        var document = HtmlParser.Parse(html);
        var engine = new LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance);
        return engine.LayoutDocument(document, new Size(w, h));
    }

    private static RenderedBitmap FlatRender(ImageSharpBackend backend, BlockBox root, int w, int h, float scale)
    {
        PaintList list = new DisplayListBuilder().Build(root);
        return backend.Render(list, new LayoutRect(0, 0, w, h), scale);
    }

    [TestMethod]
    public void Promoted_rotation_matches_flat_path_within_ssim_tolerance()
    {
        const int W = 200, H = 200;
        const float scale = 1f;
        var html =
            "<body style=\"margin:0\">" +
            "<div style=\"position:absolute;left:50px;top:50px;width:100px;height:60px;" +
            "background-color:#cc2222;transform:rotate(45deg)\">Rotate</div>" +
            "</body>";

        var root = Layout(html, W, H);
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);

        // Flat M5 path bakes the rotation into the display list.
        using var flat = FlatRender(backend, root, W, H, scale);

        // New path: the div's layer holds the UPRIGHT content; the composite
        // applies the rotation.
        var tree = new LayerTreeBuilder().Build(root);
        using var layered = new CompositorEngine(backend).Render(tree, new LayoutRect(0, 0, W, H), scale);

        layered.Width.Should().Be(flat.Width);
        layered.Height.Should().Be(flat.Height);

        var ssim = Ssim.ComputeRgba(layered.Rgba, flat.Rgba, layered.Width, layered.Height);
        ssim.Should().BeGreaterThanOrEqualTo(0.99,
            "the composited rotation must match the flat pre-baked rotation");
    }

    [TestMethod]
    public void Opacity_layer_alpha_blends_over_the_background()
    {
        const int W = 120, H = 120;
        const float scale = 1f;
        // Solid blue page background; a 50%-opacity solid-red div on top. The
        // div is painted at FULL opacity into its cache, and the composite
        // applies 0.5 — the result over blue is (127.5, 0, 127.5)-ish.
        var html =
            "<body style=\"margin:0;background-color:#0000ff\">" +
            "<div style=\"opacity:0.5;width:120px;height:120px;background-color:#ff0000\"></div>" +
            "</body>";

        var root = Layout(html, W, H);
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);

        var tree = new LayerTreeBuilder().Build(root);
        using var output = new CompositorEngine(backend).Render(tree, new LayoutRect(0, 0, W, H), scale);

        // Sample the centre — well inside both the blue page and the red div.
        var (r, g, b, a) = output.GetPixel(W / 2, H / 2);
        a.Should().Be(255);
        // 0.5*red over blue → R≈128, B≈128, G≈0 (straight-alpha blend).
        ((int)r).Should().BeInRange(118, 138, "half-opacity red contributes ~128 R over blue");
        ((int)b).Should().BeInRange(118, 138, "the blue background shows through the 50% red");
        ((int)g).Should().BeLessThan(20, "neither layer contributes green");
    }

    [TestMethod]
    public void Untouched_sibling_layer_serves_from_cache_on_reblit()
    {
        const int W = 200, H = 200;
        const float scale = 1f;
        // Two promoted opacity divs (siblings). Each layer's cache is keyed by its
        // slice CONTENT hash (LTF-02), so an unchanged re-render serves from a HIT
        // and is never re-rastered — the per-layer isolation invariant.
        var html =
            "<body style=\"margin:0\">" +
            "<div style=\"opacity:0.5;position:absolute;left:0;top:0;width:50px;height:50px;background-color:#ff0000\"></div>" +
            "<div style=\"opacity:0.5;position:absolute;left:100px;top:0;width:50px;height:50px;background-color:#0000ff\"></div>" +
            "</body>";

        var root = Layout(html, W, H);
        using var backend = new CountingBackend();

        // Build the tree ONCE so the layers persist across renders; the session tile
        // grid (and stable per-layer ids) is what carries reuse across frames now.
        var tiles = new TileGrid();
        var tree = new LayerTreeBuilder(styleOverride: null, images: null,
            layerIdFor: tiles.LayerIdFor).Build(root);
        var compositor = new CompositorEngine(backend, tileGrid: tiles);

        // First render seeds each layer's tiles.
        using (compositor.Render(tree, new LayoutRect(0, 0, W, H), scale)) { }
        var rendersAfterFirst = backend.RenderCalls;
        rendersAfterFirst.Should().BeGreaterThan(0, "the first render must raster the layer tiles");

        // Second render of the SAME (unchanged) content: each promoted layer's tile
        // serves from cache instead of re-rasterizing.
        using (compositor.Render(tree, new LayoutRect(0, 0, W, H), scale)) { }
        backend.RenderCalls.Should().Be(rendersAfterFirst,
            "untouched promoted layers re-blit their tiles from cache on the unchanged second render");

        // A third unchanged render keeps hitting — the tile cache persists across frames.
        using (compositor.Render(tree, new LayoutRect(0, 0, W, H), scale)) { }
        backend.RenderCalls.Should().Be(rendersAfterFirst,
            "the tile cache keeps serving unchanged layers across frames");
    }

    [TestMethod]
    public void Transform_only_change_reblits_layer_content_from_persistent_cache()
    {
        // Phase 5 for transform (the opacity test's sibling): a promoted, rotated
        // element's transform is applied at composite time, not baked into the
        // slice, so changing only the rotation re-blits the cached upright content
        // and re-composites — no re-raster — while the output changes. (This is the
        // deterministic core; driving it from the animation clock end to end is the
        // live-GUI integration tracked in PHASES_1_5_STATUS.)
        const int W = 160, H = 160;
        const float scale = 1f;
        const string baseStyle =
            "position:absolute;left:40px;top:40px;width:60px;height:60px;" +
            "background-color:#cc2222;will-change:transform;";

        var doc = HtmlParser.Parse(
            "<body style=\"margin:0;height:160px;background-color:#eef0ff\">" +
            $"<div id=box style=\"{baseStyle}transform:rotate(10deg)\"></div></body>");
        var style = new StyleEngine();
        var engine = new LayoutEngine(style, DefaultTextMeasurer.Instance);
        using var backend = new CountingBackend();
        var tiles = new TileGrid();
        var compositor = new CompositorEngine(backend, tileGrid: tiles);

        var root1 = engine.LayoutDocument(doc, new Size(W, H));
        FindByElement(root1, doc.GetElementById("box")!)!.Hints
            .Should().NotBe(Starling.Layout.Compositor.LayerHint.None, "a transformed div is its own layer");
        var tree1 = new LayerTreeBuilder(null, null, layerIdFor: tiles.LayerIdFor).Build(root1);
        byte[] first;
        using (var r1 = compositor.Render(tree1, new LayoutRect(0, 0, W, H), scale))
        {
            first = (byte[])r1.Rgba.Clone();
        }

        var rendersAfterFirst = backend.RenderCalls;
        rendersAfterFirst.Should().BeGreaterThan(0, "the first render must raster the layer tiles");

        // Change only the rotation — a composite-time property. Re-lay-out so the
        // new transform reaches the box style, then rebuild against the SAME tile grid.
        doc.GetElementById("box")!.SetAttribute("style", baseStyle + "transform:rotate(70deg)");
        var root2 = engine.LayoutDocument(doc, new Size(W, H));
        var tree2 = new LayerTreeBuilder(null, null, layerIdFor: tiles.LayerIdFor).Build(root2);
        byte[] second;
        using (var r2 = compositor.Render(tree2, new LayoutRect(0, 0, W, H), scale))
        {
            second = (byte[])r2.Rgba.Clone();
        }

        backend.RenderCalls.Should().Be(rendersAfterFirst,
            "the rotating layer content re-blits from cache; only the composite transform changed");
        second.SequenceEqual(first).Should().BeFalse("the new rotation must change the composited output");
    }

    [TestMethod]
    public void Opacity_only_change_reblits_layer_content_from_persistent_cache()
    {
        // Phase 5: across frames the layer tree is rebuilt, but persistent
        // element-keyed caches let a layer whose only change is opacity (applied
        // at composite, not baked into the slice) serve its pixels from cache
        // rather than re-rasterize. We change opacity, rebuild the tree against
        // the SAME store at the SAME page version, and assert the content is a
        // cache HIT yet the composited output differs.
        const int W = 160, H = 160;
        const float scale = 1f;
        var html =
            "<body style=\"margin:0;height:160px;background-color:#eef0ff\">" +
            "<div id=fade style=\"opacity:0.9;position:absolute;left:10px;top:10px;" +
            "width:80px;height:80px;background-color:#cc2222\"></div>" +
            "</body>";

        var doc = HtmlParser.Parse(html);
        var engine = new LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance);
        using var backend = new CountingBackend();
        var tiles = new TileGrid();
        var compositor = new CompositorEngine(backend, tileGrid: tiles);

        var root1 = engine.LayoutDocument(doc, new Size(W, H));
        var tree1 = new LayerTreeBuilder(null, null, layerIdFor: tiles.LayerIdFor).Build(root1);
        byte[] first;
        using (var r1 = compositor.Render(tree1, new LayoutRect(0, 0, W, H), scale))
        {
            first = (byte[])r1.Rgba.Clone();
        }

        var rendersAfterFirst = backend.RenderCalls;
        rendersAfterFirst.Should().BeGreaterThan(0, "the first render must raster the layer tiles");

        // Animate opacity only — no layout-affecting change. Re-lay-out so the new
        // opacity reaches the box style, then rebuild the tree against the SAME tile grid.
        doc.GetElementById("fade")!.SetAttribute(
            "style", "opacity:0.4;position:absolute;left:10px;top:10px;width:80px;height:80px;background-color:#cc2222");
        var root2 = engine.LayoutDocument(doc, new Size(W, H));
        var tree2 = new LayerTreeBuilder(null, null, layerIdFor: tiles.LayerIdFor).Build(root2);
        byte[] second;
        using (var r2 = compositor.Render(tree2, new LayoutRect(0, 0, W, H), scale))
        {
            second = (byte[])r2.Rgba.Clone();
        }

        backend.RenderCalls.Should().Be(rendersAfterFirst,
            "the layer content is reused from cache; only the composite-time opacity changed");
        // ...and the composited result actually changed (the div is more transparent).
        second.SequenceEqual(first).Should().BeFalse("the new opacity must change the composited output");
    }

    [TestMethod]
    public void Tall_layer_rasters_only_viewport_tiles_not_full_height()
    {
        // wp:M12-05 acceptance: a very tall layer rasters a number of tiles bounded by
        // the VIEWPORT, not the layer's full height — and no tile exceeds the tile size.
        const float scale = 1f;
        var html = "<body style=\"margin:0\"><div style=\"width:240px;height:50000px;background-color:#3366cc\"></div></body>";
        var root = Layout(html, 240, 800);
        using var backend = new CountingBackend();
        var tiles = new TileGrid();
        var tree = new LayerTreeBuilder(layerIdFor: tiles.LayerIdFor).Build(root);
        var compositor = new CompositorEngine(backend, tileGrid: tiles);

        using (compositor.Render(tree, new LayoutRect(0, 0, 240, 800), scale)) { }

        var bound = ((240 / TileGrid.TileWidthDevice) + 2) * ((800 / TileGrid.TileHeightDevice) + 2);
        backend.RenderCalls.Should().BeLessThanOrEqualTo(bound,
            "tiles painted are bounded by the viewport + a one-tile ring");
        backend.RenderCalls.Should().BeLessThan(20,
            "NOT proportional to the ~98-tile full layer height (50000px / 512)");
    }

    [TestMethod]
    public void Scrolling_one_tile_row_reblits_overlap_and_paints_one_new_row()
    {
        // wp:M12-05 acceptance: scrolling by one tile row re-blits the overlapping
        // rows from cache and rasters only the newly-exposed row.
        const float scale = 1f;
        var html = "<body style=\"margin:0\"><div style=\"width:240px;height:50000px;background-color:#3366cc\"></div></body>";
        var root = Layout(html, 240, 800);
        using var backend = new CountingBackend();
        var tiles = new TileGrid();
        var tree = new LayerTreeBuilder(layerIdFor: tiles.LayerIdFor).Build(root);
        var compositor = new CompositorEngine(backend, tileGrid: tiles);

        using (compositor.Render(tree, new LayoutRect(0, 0, 240, 800), scale)) { }
        var rendersFirst = backend.RenderCalls;

        // Scroll down exactly one tile row (TileHeightDevice px at scale 1).
        using (compositor.Render(tree, new LayoutRect(0, TileGrid.TileHeightDevice, 240, 800), scale)) { }
        var newRenders = backend.RenderCalls - rendersFirst;

        newRenders.Should().BeLessThan(rendersFirst,
            "the overlapping rows re-blit from cache after a one-row scroll");
        newRenders.Should().BeLessThanOrEqualTo(3,
            "only the newly-exposed row (one column + ring) re-rasters");
    }

    private static Box? FindByElement(Box box, Starling.Dom.Element el)
    {
        if (ReferenceEquals(box.Element, el))
        {
            return box;
        }

        foreach (var c in box.Children)
        {
            if (FindByElement(c, el) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private sealed class CountingBackend : IPaintBackend
    {
        private readonly ImageSharpBackend _inner = new(FontResolver.Default, webFonts: null);

        public int RenderCalls { get; private set; }
        public string Name => _inner.Name;

        public RenderedBitmap Render(PaintList list, LayoutRect viewport, float scale = 1.0f)
        {
            RenderCalls++;
            return _inner.Render(list, viewport, scale);
        }

        public RenderedBitmap Render(PaintList list, LayoutRect viewport, float scale, bool opaqueBackground)
        {
            RenderCalls++;
            return _inner.Render(list, viewport, scale, opaqueBackground);
        }

        public void Dispose() => _inner.Dispose();
    }
}
