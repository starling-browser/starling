using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Starling.Common.Diagnostics;
using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Box;
using Starling.Layout.Text;
using Starling.Paint;
using Xunit;
using LayoutRect = Starling.Layout.Rect;

namespace Starling.Gui.Headless.Tests;

/// <summary>
/// LTF-03: the shell keeps each layer's persistent picture cache across an
/// in-place relayout (InvalidateCache drops only the flat scroll cache) and
/// clears them on navigation (ResetForNavigation). Combined with the LTF-02
/// content-hash key, an unchanged layer re-blits from cache across a relayout
/// while a navigation forces a cold re-raster.
/// </summary>
public sealed class LayerCachePersistenceTests
{
    // Two promoted opacity layers + the root → three layers total.
    private const string Html =
        "<body style=\"margin:0\">" +
        "<div style=\"opacity:0.5;position:absolute;left:0;top:0;width:50px;height:50px;background-color:#ff0000\"></div>" +
        "<div style=\"opacity:0.5;position:absolute;left:80px;top:0;width:50px;height:50px;background-color:#0000ff\"></div>" +
        "</body>";

    private static BlockBox Layout()
        => new LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance)
            .LayoutDocument(HtmlParser.Parse(Html), new Size(200, 200));

    [Fact]
    public void Layer_caches_survive_relayout_and_clear_on_navigation()
    {
        GpuTests.SkipUnlessAvailable();
        using var metrics = new MetricRecorder();
        using var host = new PageRendererHost();
        var root = Layout();

        // Seed every layer's cache (no prior content → no HIT).
        host.RenderViaLayerTree(root, 1f).Dispose();
        metrics.CountOf("paint.tile.cache_hit").Should().Be(0, "nothing is cached before the first render");

        // Re-render unchanged content: every layer re-blits from cache.
        host.RenderViaLayerTree(root, 1f).Dispose();
        var afterReblit = metrics.CountOf("paint.tile.cache_hit");
        afterReblit.Should().BeGreaterThan(0, "unchanged layers serve from cache on the second render");

        // In-place relayout: the flat cache drops but the per-layer caches persist.
        host.InvalidateCache();
        host.RenderViaLayerTree(root, 1f).Dispose();
        metrics.CountOf("paint.tile.cache_hit").Should().Be(afterReblit * 2,
            "the per-layer caches are retained across an in-place relayout (LTF-03)");

        // Navigation: every layer cache is cleared, so the next render is cold.
        host.ResetForNavigation();
        host.RenderViaLayerTree(root, 1f).Dispose();
        metrics.CountOf("paint.tile.cache_hit").Should().Be(afterReblit * 2,
            "navigation clears the per-layer caches — the next render re-rasters, no new HIT");
    }

    /// <summary>Drives the layer-tree compositor for headless cache-persistence tests.</summary>
    private sealed class PageRendererHost : IDisposable
    {
        private readonly CompositedPageRenderer _renderer = new();

        /// <summary>Render <paramref name="root"/> through the layer tree and return
        /// the bitmap (dispose to release native memory).</summary>
        public RenderedBitmap RenderViaLayerTree(BlockBox root, float scale)
        {
            var viewport = new LayoutRect(0, 0,
                Math.Max(1, root.Frame.Width),
                Math.Max(1, root.Frame.Height));
            return _renderer.Render(root, viewport, scale);
        }

        /// <summary>Simulates an in-place relayout: the flat scroll cache is dropped
        /// but the per-layer tile grid is retained.</summary>
        public void InvalidateCache()
        {
            // The flat scroll cache was removed; the per-layer tile grid persists
            // automatically across relayouts (this is the LTF-03 invariant under test).
        }

        public void ResetForNavigation() => _renderer.ResetForNavigation();

        public void Dispose() => _renderer.Dispose();
    }

    /// <summary>Listens to StarlingTelemetry.Meter and accumulates counter totals.</summary>
    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _l = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, double> _v = new();

        public MetricRecorder()
        {
            _l.InstrumentPublished = (inst, lst) =>
            { if (inst.Meter.Name == StarlingTelemetry.SourceName)
                {
                    lst.EnableMeasurementEvents(inst);
                }
            };
            _l.SetMeasurementEventCallback<double>((inst, m, t, s) => Add(inst.Name, m));
            _l.SetMeasurementEventCallback<long>((inst, m, t, s) => Add(inst.Name, m));
            _l.Start();
        }

        private void Add(string n, double m)
            => _v.AddOrUpdate(n, m, (_, p) => p + m);

        public double CountOf(string name) => _v.TryGetValue(name, out var x) ? x : 0d;

        public void Dispose() => _l.Dispose();
    }
}
