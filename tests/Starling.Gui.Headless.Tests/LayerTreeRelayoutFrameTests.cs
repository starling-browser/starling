using System.Diagnostics.Metrics;
using AwesomeAssertions;
using Starling.Common.Diagnostics;
using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Dom;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Box;
using Starling.Layout.Text;
using Starling.Paint;
using Xunit;
using LayoutRect = Starling.Layout.Rect;

namespace Starling.Gui.Headless.Tests;

/// <summary>
/// LTF-04: the compositor layer-tree path runs on a live frame even when that
/// frame also relayouted. The pre-LTF-04 gate stayed on the flat path whenever a
/// frame relayouted (which the demo does every frame, from a status-text write),
/// so the layer caches never helped. Now a relayout no longer wipes the per-layer
/// caches (LTF-03) and each layer is content-keyed (LTF-02), so a transform-only
/// layer re-blits from cache across a relayout frame while the relaid region
/// re-rasters.
/// </summary>
public sealed class LayerTreeRelayoutFrameTests
{
    private const string SpinBase =
        "position:absolute;left:50px;top:50px;width:80px;height:50px;background-color:#cc2222;";

    [Fact]
    public void Spinning_layer_reblits_from_cache_across_relayout_frames()
    {
        GpuTests.SkipUnlessAvailable();
        using var metrics = new MetricRecorder();
        using var host = new PageRendererHost();

        var doc = HtmlParser.Parse(
            "<body style=\"margin:0\">" +
            $"<div id=spin style=\"{SpinBase}transform:rotate(15deg)\"></div>" +
            "<div id=label style=\"position:absolute;left:0;top:150px\">frame 0</div>" +
            "</body>");
        var style = new StyleEngine();
        var engine = new LayoutEngine(style, DefaultTextMeasurer.Instance);
        var size = new Size(240, 240);
        var spin = doc.GetElementById("spin")!;
        var label = doc.GetElementById("label")!;

        double prevHits = 0;
        for (var f = 0; f < 4; f++)
        {
            // A layout-relevant mutation (the status-text write the demo makes) —
            // this is what forced the old gate onto the flat path every frame.
            if (label.FirstChild is Text t)
            {
                t.Data = "frame " + f;
            }
            // A composite-time, transform-only change on the promoted layer. Start
            // non-zero so the layer is promoted (a stacking context) from the first
            // frame — rotate(0deg) is the identity and would only promote later.
            spin.SetAttribute("style", SpinBase + $"transform:rotate({(f + 1) * 15}deg)");

            var root = engine.LayoutDocument(doc, size);
            host.RenderViaLayerTree(root, 1f).Dispose();

            var hits = metrics.CountOf("paint.tile.cache_hit");
            if (f == 0)
            {
                hits.Should().Be(0, "the first frame seeds every layer's cache");
            }
            else
            {
                (hits - prevHits).Should().BeGreaterThan(0,
                    "the spinning layer re-blits its content from cache on a relayout frame (LTF-04)");
            }
            prevHits = hits;
        }
    }

    /// <summary>Drives the layer-tree compositor for headless cache tests.</summary>
    private sealed class PageRendererHost : IDisposable
    {
        private readonly CompositedPageRenderer _renderer = new();

        public RenderedBitmap RenderViaLayerTree(BlockBox root, float scale)
        {
            var viewport = new LayoutRect(0, 0,
                Math.Max(1, root.Frame.Width),
                Math.Max(1, root.Frame.Height));
            return _renderer.Render(root, viewport, scale);
        }

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
