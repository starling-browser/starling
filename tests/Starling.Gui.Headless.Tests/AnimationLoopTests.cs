using System.Diagnostics.Metrics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Common.Diagnostics;
using Starling.Engine;
using Starling.Gui.Controls;
using Starling.Gui.Theme;
using EngineSize = SixLabors.ImageSharp.Size;

namespace Starling.Gui.Headless.Tests;

/// <summary>
/// Verifies the live GUI animation loop: a page that calls
/// <c>element.animate()</c> drives the WebviewPanel's own renderer (via the
/// engine's PrepareAnimationFrame + the animated-style override) so the painted
/// pixels of the animated element change across frame timestamps. This is the
/// GUI-side counterpart to the engine's RenderFrame pixel-diff test.
/// </summary>
public class AnimationLoopTests
{
    [AvaloniaFact]
    public async Task WebviewPanel_repaints_a_waapi_animation_across_frames()
    {
        GpuTests.SkipUnlessAvailable();
        const string html = """
            <!doctype html><html><body>
              <div id='t' style='width:200px;height:200px;background:rgb(255,0,0)'></div>
              <script>
                document.getElementById('t').animate(
                  [{backgroundColor:'rgb(255,0,0)'},{backgroundColor:'rgb(0,0,255)'}],
                  {duration:1000, easing:'linear'});
              </script>
            </body></html>
            """;
        var (engine, page) = await LoadInteractiveAsync(html);

        using var panel = new WebviewPanel(
            new ThemeManager(), NullLoggerFactory.Instance, _ => { }, (_, _) => { },
            (p, vp) => engine.RelayoutPage(p, new RenderOptions(vp, FontSize: 16f)),
            prepareAnimationFrame: engine.PrepareAnimationFrame,
            hasActiveAnimations: engine.HasActiveAnimations);

        var window = new Window { Content = panel, Width = 400, Height = 400 };
        window.Show();
        panel.ShowPage(page);
        window.CaptureRenderedFrame(); // force measure/arrange so the page bitmap materializes

        // The animation should be in flight (script anim imported into the page).
        engine.HasActiveAnimations(page).Should().BeTrue("element.animate registered a script animation");

        var c0 = PixelAtFrame(engine, panel, page, nowMs: 0, x: 20, y: 20);
        var c500 = PixelAtFrame(engine, panel, page, nowMs: 500, x: 20, y: 20);
        var c999 = PixelAtFrame(engine, panel, page, nowMs: 999, x: 20, y: 20);

        // Linear red→blue fade: red falls, blue rises monotonically.
        c0.R.Should().BeGreaterThan(c500.R, "the red channel fades out over the animation");
        c500.R.Should().BeGreaterThan(c999.R);
        c0.B.Should().BeLessThan(c500.B, "the blue channel fades in over the animation");
        c500.B.Should().BeLessThan(c999.B);
    }

    [AvaloniaFact]
    public async Task WebviewPanel_repaints_a_declarative_keyframes_animation()
    {
        GpuTests.SkipUnlessAvailable();
        // No script — a pure @keyframes animation. The engine primes it into the
        // live AnimationEngine from the element's static animation-* cascade, so
        // the GUI loop renders it moving.
        const string html = """
            <!doctype html><html><head><style>
              @keyframes fade { 0% { background-color: rgb(255,0,0) } 100% { background-color: rgb(0,0,255) } }
              #t { width:200px; height:200px; background:rgb(255,0,0); animation: fade 1000ms linear; }
            </style></head><body><div id='t'></div></body></html>
            """;
        var (engine, page) = await LoadInteractiveAsync(html);

        using var panel = new WebviewPanel(
            new ThemeManager(), NullLoggerFactory.Instance, _ => { }, (_, _) => { },
            (p, vp) => engine.RelayoutPage(p, new RenderOptions(vp, FontSize: 16f)),
            prepareAnimationFrame: engine.PrepareAnimationFrame,
            hasActiveAnimations: engine.HasActiveAnimations);

        var window = new Window { Content = panel, Width = 400, Height = 400 };
        window.Show();
        panel.ShowPage(page);
        window.CaptureRenderedFrame();

        engine.HasActiveAnimations(page).Should().BeTrue("a declarative @keyframes animation should be primed and in flight");

        var c0 = PixelAtFrame(engine, panel, page, nowMs: 0, x: 20, y: 20);
        var c500 = PixelAtFrame(engine, panel, page, nowMs: 500, x: 20, y: 20);
        var c999 = PixelAtFrame(engine, panel, page, nowMs: 999, x: 20, y: 20);

        c0.R.Should().BeGreaterThan(c500.R, "the red channel fades out");
        c500.R.Should().BeGreaterThan(c999.R);
        c0.B.Should().BeLessThan(c500.B, "the blue channel fades in");
        c500.B.Should().BeLessThan(c999.B);
    }

    [AvaloniaFact]
    public async Task Transform_animation_on_a_promoted_layer_reblits_content_from_cache()
    {
        GpuTests.SkipUnlessAvailable();
        // A `will-change: transform` div promotes its own compositor layer; the
        // WAAPI animation drives only `transform`, a composite-time property. So
        // across frames the layer's CONTENT is unchanged — only the matrix moves.
        // Phase 5 routes the live repaint through RenderViaLayerTree with a
        // clock-free page version, so the second frame re-blits the layer pixels
        // from cache (paint.tile.cache_hit > 0) instead of re-rasterizing, while the
        // composited output still moves.
        const string html = """
            <!doctype html><html><body>
              <div id='t' style='width:200px;height:200px;background:rgb(255,0,0);will-change:transform'></div>
              <script>
                document.getElementById('t').animate(
                  [{transform:'translateX(0px)'},{transform:'translateX(150px)'}],
                  {duration:1000, easing:'linear'});
              </script>
            </body></html>
            """;
        var (engine, page) = await LoadInteractiveAsync(html);

        using var metrics = new MetricRecorder();
        using var panel = new WebviewPanel(
            new ThemeManager(), NullLoggerFactory.Instance, _ => { }, (_, _) => { },
            (p, vp) => engine.RelayoutPage(p, new RenderOptions(vp, FontSize: 16f)),
            prepareAnimationFrame: engine.PrepareAnimationFrame,
            hasActiveAnimations: engine.HasActiveAnimations);

        var window = new Window { Content = panel, Width = 400, Height = 400 };
        window.Show();
        panel.ShowPage(page);
        window.CaptureRenderedFrame();

        // Frame 0 seeds the per-layer caches; frame 600 (a different clock) must
        // serve their content from cache — proof the clock-free layer-tree path
        // ran (the flat path's clock-keyed version would miss every frame).
        var near = PixelAtFrame(engine, panel, page, nowMs: 0, x: 40, y: 40);
        var hitsAfterSeed = metrics.CountOf("paint.tile.cache_hit");
        _ = PixelAtFrame(engine, panel, page, nowMs: 600, x: 40, y: 40);
        var farLeft = PixelAtFrame(engine, panel, page, nowMs: 999, x: 40, y: 40);

        metrics.CountOf("paint.tile.cache_hit").Should().BeGreaterThan(hitsAfterSeed,
            "the transform-only frame re-blits each layer's content from cache");

        // x=40 starts inside the red box (translateX≈0). Red is rgb(255,0,0), so
        // the green/blue channels are near zero there — the R channel alone can't
        // tell red from the white page background.
        near.G.Should().BeLessThan(80, "x=40 is inside the red box at translateX(0)");
        near.B.Should().BeLessThan(80);

        // Composite-time transform slides the box right; by translateX≈150 the
        // box has cleared x=40, exposing the lighter page background.
        var moved = Math.Abs(near.R - farLeft.R) + Math.Abs(near.G - farLeft.G) + Math.Abs(near.B - farLeft.B);
        moved.Should().BeGreaterThan(60, "the box slid right, changing the pixel at x=40");
    }

    // ---------------------------------------------------------------- helpers

    private static (byte R, byte G, byte B, byte A) PixelAtFrame(
        StarlingEngine engine, WebviewPanel panel, LaidOutPage page, long nowMs, int x, int y)
    {
        // Advance the page's animation clock, then drive the panel's own render
        // path the way the live timer would (without depending on the timer).
        engine.PrepareAnimationFrame(page, nowMs);
        SetField(panel, "_animClockMs", nowMs);
        SetField(panel, "_animating", true);
        // The compositor layer-tree path is gated on _animating alone now (LTF-04):
        // an animating frame composites whether or not it relayouted.
        typeof(WebviewPanel).GetMethod("RenderViewportRegion", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(panel, null);

        var image = (Image)typeof(WebviewPanel)
            .GetField("_pageImage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(panel)!;
        var bmp = (WriteableBitmap)image.Source!;
        return ReadPixel(bmp, x, y);
    }

    private static void SetField(object obj, string name, object value)
        => typeof(WebviewPanel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(obj, value);

    private static (byte R, byte G, byte B, byte A) ReadPixel(WriteableBitmap bmp, int x, int y)
    {
        using var fb = bmp.Lock();
        var bytesPerPixel = 4;
        var offset = y * fb.RowBytes + x * bytesPerPixel;
        var px = new byte[4];
        Marshal.Copy(fb.Address + offset, px, 0, 4);
        // Rgba8888 channel order.
        return fb.Format == PixelFormat.Bgra8888
            ? (px[2], px[1], px[0], px[3])
            : (px[0], px[1], px[2], px[3]);
    }

    private static async Task<(StarlingEngine Engine, LaidOutPage Page)> LoadInteractiveAsync(string html)
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-animloop-{Guid.NewGuid():N}.html");
        File.WriteAllText(fixture, html);
        var engine = new StarlingEngine();
        var result = await engine.LayoutPageAsync(
            "file://" + fixture.Replace('\\', '/'),
            new RenderOptions(new EngineSize(400, 400), FontSize: 16f),
            CancellationToken.None, onFirstPaint: _ => { });
        result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
        return (engine, result.Value);
    }

    /// <summary>Listens to StarlingTelemetry.Meter and accumulates counter values
    /// so a test can assert cache-hit deltas.</summary>
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
