using System.Diagnostics.Metrics;
using System.Text;
using AwesomeAssertions;
using Starling.Common.Diagnostics;
using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Box;
using Starling.Layout.Text;
using Starling.Paint.Backend;
using Starling.Paint.Cache;
using Starling.Paint.DisplayList;
using LayoutRect = Starling.Layout.Rect;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint.Tests;

/// <summary>
/// Acceptance tests for wp:M12-02 picture cache: scrolling reuses cached pixels
/// (HIT / PARTIAL), a display-list version bump forces a full MISS, served pixels
/// are byte-identical to a from-scratch render, and the cache evicts when it would
/// exceed its area budget. Drives the real <see cref="CachedPageRenderer"/> +
/// <see cref="ImageSharpBackend"/> so the integration is what's under test.
/// </summary>
[TestClass]
public sealed class PictureCacheTests
{
    private const int ViewW = 800;
    private const int ViewH = 600;

    private static BlockBox LayoutTallPage(int blocks, int blockHeightPx, double viewportWidth = ViewW)
    {
        var sb = new StringBuilder("<body style=\"margin:0\">");
        for (var i = 0; i < blocks; i++)
        {
            var g = (byte)(i % 200 + 30);
            sb.Append($"<div style=\"margin:0;height:{blockHeightPx}px;background-color:rgb(10,{g},20)\"></div>");
        }
        sb.Append("</body>");

        var document = HtmlParser.Parse(sb.ToString());
        var style = new StyleEngine();
        var engine = new LayoutEngine(style, DefaultTextMeasurer.Instance);
        return engine.LayoutDocument(document, new Size(viewportWidth, ViewH));
    }

    /// <summary>From-scratch render of a viewport — the ground truth a cache serve must match.</summary>
    private static RenderedBitmap RenderFromScratch(ImageSharpBackend backend, BlockBox root, LayoutRect viewport, float scale = 1f)
    {
        PaintList list = new DisplayListBuilder().Build(root, viewport);
        return backend.Render(list, viewport, scale);
    }

    [TestMethod]
    public void Scrolling_records_partial_hits_and_at_most_one_miss_with_bounded_strip_area()
    {
        // ~200000 px tall page.
        var root = LayoutTallPage(blocks: 1000, blockHeightPx: 200);
        root.Frame.Height.Should().BeGreaterThan(190000);

        using var metrics = new MetricRecorder();
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        var renderer = new CachedPageRenderer(backend);

        const int scrolls = 10;
        const int delta = 50;
        const int version = 1;

        // Initial render at y=0 (the only expected full miss).
        using (renderer.Render(root, new LayoutRect(0, 0, ViewW, ViewH), 1f, version)) { }

        for (var i = 1; i <= scrolls; i++)
        {
            using (renderer.Render(root, new LayoutRect(0, i * delta, ViewW, ViewH), 1f, version)) { }
        }

        metrics.CountOf("paint.cache.miss").Should().BeLessThanOrEqualTo(1,
            "only the first render should be a full miss; every subsequent scroll overlaps the cache");
        metrics.CountOf("paint.cache.partial").Should().BeGreaterThanOrEqualTo(scrolls - 1,
            "each downward scroll exposes a fresh edge strip and must be served as a partial");

        // strip_area counts only stitched strips, not the seed frame (the seed
        // goes through Reset, not Stitch). Each 50px downward scroll paints one
        // 50px band across the viewport width — the backend sizes the strip raster
        // to the strip rect (not the 64px-overdraw cull rect), so no margin slack
        // is needed, but allow a band's worth of slack to be safe.
        var maxBandArea = (double)delta * ViewW;
        metrics.CountOf("paint.cache.strip_area").Should()
            .BeLessThanOrEqualTo((scrolls + 1) * maxBandArea,
                "each ≤50px scroll paints at most one 50px band across the viewport width");
    }

    [TestMethod]
    public void Version_bump_forces_full_miss_on_next_render()
    {
        var root = LayoutTallPage(blocks: 200, blockHeightPx: 200);
        using var metrics = new MetricRecorder();
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        var renderer = new CachedPageRenderer(backend);

        var viewport = new LayoutRect(0, 0, ViewW, ViewH);

        using (renderer.Render(root, viewport, 1f, pageVersion: 1)) { }
        metrics.CountOf("paint.cache.miss").Should().Be(1, "the seed render is a miss");

        // Same viewport, same scale, but a bumped version: must be a fresh miss,
        // not a hit/partial.
        using (renderer.Render(root, viewport, 1f, pageVersion: 2)) { }

        metrics.CountOf("paint.cache.miss").Should().Be(2, "a version bump invalidates the cache wholesale");
        metrics.CountOf("paint.cache.hit").Should().Be(0);
        metrics.CountOf("paint.cache.partial").Should().Be(0);
    }

    [TestMethod]
    public void Full_hit_serves_pixels_identical_to_from_scratch_render()
    {
        var root = LayoutTallPage(blocks: 300, blockHeightPx: 200);
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        var renderer = new CachedPageRenderer(backend);

        // Seed a large cache (1200px tall), then request a sub-viewport fully
        // inside it — a pure HIT, no backend call.
        using (renderer.Render(root, new LayoutRect(0, 0, ViewW, 1200), 1f, 1)) { }

        var sub = new LayoutRect(0, 300, ViewW, ViewH);
        using var served = renderer.Render(root, sub, 1f, 1);
        using var truth = RenderFromScratch(backend, root, sub);

        BitmapPixels.PixelsEqual(served, truth).Should().BeTrue(
            "a cache HIT must produce byte-identical pixels to a from-scratch render of the same viewport");
    }

    [TestMethod]
    public void Partial_stitch_serves_pixels_identical_to_from_scratch_render()
    {
        var root = LayoutTallPage(blocks: 300, blockHeightPx: 200);
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        var renderer = new CachedPageRenderer(backend);

        // Seed at y=0, then scroll down 137px so the new viewport overlaps the
        // cache but exposes a bottom strip — a PARTIAL that stitches.
        using (renderer.Render(root, new LayoutRect(0, 0, ViewW, ViewH), 1f, 1)) { }

        var scrolled = new LayoutRect(0, 137, ViewW, ViewH);
        using var served = renderer.Render(root, scrolled, 1f, 1);
        using var truth = RenderFromScratch(backend, root, scrolled);

        served.Width.Should().Be(truth.Width);
        served.Height.Should().Be(truth.Height);
        BitmapPixels.PixelsEqual(served, truth).Should().BeTrue(
            "a stitched PARTIAL serve must produce byte-identical pixels to a from-scratch render");
    }

    [TestMethod]
    public void Long_scroll_slides_window_without_growing_or_full_reseed()
    {
        var root = LayoutTallPage(blocks: 1000, blockHeightPx: 200);
        using var metrics = new MetricRecorder();
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);

        var cache = new PictureCache();
        var renderer = new CachedPageRenderer(backend, cache);

        using (renderer.Render(root, new LayoutRect(0, 0, ViewW, ViewH), 1f, 1)) { }

        // Scroll down by 400px: only [400,600) overlaps the seeded [0,600) window,
        // so a 400px bottom strip is painted and the window slides down. The old
        // design unioned to a 1000px-tall bitmap and (under budget) eventually
        // evicted + repainted the whole viewport; the sliding window must instead
        // stay viewport-sized and never count a second miss.
        var scrolled = new LayoutRect(0, 400, ViewW, ViewH);
        using var served = renderer.Render(root, scrolled, 1f, 1);

        metrics.CountOf("paint.cache.miss").Should().Be(1,
            "only the seed is a miss; a partial scroll slides the window instead of reseeding");
        metrics.CountOf("paint.cache.partial").Should().BeGreaterThanOrEqualTo(1);
        ((long)cache.Bounds.Width * cache.Bounds.Height).Should().Be(ViewW * ViewH,
            "the cache window slides onto the new viewport rather than growing to the scrolled-through bounds");

        using var truth = RenderFromScratch(backend, root, scrolled);
        BitmapPixels.PixelsEqual(served, truth).Should().BeTrue(
            "pixels served after a window slide must still match a from-scratch render");

        // The slid window now holds the new content; re-requesting it is a clean HIT.
        using var again = renderer.Render(root, scrolled, 1f, 1);
        BitmapPixels.PixelsEqual(again, truth).Should().BeTrue();
        metrics.CountOf("paint.cache.hit").Should().BeGreaterThanOrEqualTo(1);
    }

    [TestMethod]
    public void Scrolling_back_up_after_window_slid_repaints_only_the_exposed_strip()
    {
        var root = LayoutTallPage(blocks: 1000, blockHeightPx: 200);
        using var metrics = new MetricRecorder();
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        var renderer = new CachedPageRenderer(backend);

        // Seed, scroll down past the window, then scroll back up so the top edge is
        // freshly exposed again. Every step stays viewport-sized and correct.
        using (renderer.Render(root, new LayoutRect(0, 0, ViewW, ViewH), 1f, 1)) { }
        using (renderer.Render(root, new LayoutRect(0, 400, ViewW, ViewH), 1f, 1)) { }

        var backUp = new LayoutRect(0, 100, ViewW, ViewH);
        using var served = renderer.Render(root, backUp, 1f, 1);
        using var truth = RenderFromScratch(backend, root, backUp);

        BitmapPixels.PixelsEqual(served, truth).Should().BeTrue(
            "scrolling back up re-exposes a top strip; the slide must reassemble it correctly");
        metrics.CountOf("paint.cache.miss").Should().Be(1, "no step jumped clear of the window");
    }

    [TestMethod]
    public void Repeated_identical_viewport_is_a_pure_hit_after_seed()
    {
        var root = LayoutTallPage(blocks: 100, blockHeightPx: 200);
        using var metrics = new MetricRecorder();
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        var renderer = new CachedPageRenderer(backend);

        var viewport = new LayoutRect(0, 0, ViewW, ViewH);
        using (renderer.Render(root, viewport, 1f, 1)) { }
        using (renderer.Render(root, viewport, 1f, 1)) { }

        metrics.CountOf("paint.cache.miss").Should().Be(1);
        metrics.CountOf("paint.cache.hit").Should().Be(1, "the second identical render must be a pure HIT");
        metrics.CountOf("paint.cache.partial").Should().Be(0);
    }

    /// <summary>
    /// Captures metric deltas from <see cref="StarlingTelemetry.Meter"/> for the
    /// duration of the enclosing test. Because the meter is process-global, this
    /// recorder observes only measurements emitted after it is constructed.
    /// </summary>
    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _values = new();

        public MetricRecorder()
        {
            _listener.InstrumentPublished = (inst, lst) =>
            {
                if (inst.Meter.Name == StarlingTelemetry.SourceName)
                {
                    lst.EnableMeasurementEvents(inst);
                }
            };
            _listener.SetMeasurementEventCallback<double>((inst, m, _, _) => Add(inst.Name, m));
            _listener.SetMeasurementEventCallback<long>((inst, m, _, _) => Add(inst.Name, (double)m));
            _listener.Start();
        }

        private void Add(string name, double value)
            => _values.AddOrUpdate(name, value, (_, prev) => prev + value);

        public double CountOf(string name) => _values.TryGetValue(name, out var v) ? v : 0d;

        public void Dispose() => _listener.Dispose();
    }
}
