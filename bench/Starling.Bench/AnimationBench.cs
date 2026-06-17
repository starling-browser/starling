using BenchmarkDotNet.Attributes;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Dom;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Incremental;
using Starling.Paint;
using Starling.Paint.Backend;
using Starling.Paint.DisplayList;

namespace Starling.Bench;

// Per-frame cost of driving a CSS @keyframes timeline — the work the live frame
// loop repeats every animation frame (Engine.PrepareAnimationFrame): advance
// the animation clock, re-sample the animated properties through the cascade,
// relayout whatever the samples dirtied, and rebuild the display list.
//
// Two property shapes, because they take very different paths:
//   * [Kind] = Transform — transform + opacity. Composite-only: the animated
//     value never affects box geometry, so incremental layout leaves the tree
//     alone and only the paint (display-list) stage re-runs.
//   * [Kind] = Layout — width + margin. Layout-affecting: each frame marks the
//     animated elements dirty and relays them out, so the box-tree pass re-runs.
//
// [Boxes] scales the number of simultaneously-animating elements. Each frame
// advances the clock by one 60fps step; the animation iterates forever, so the
// playback head keeps sweeping the keyframes and never settles.
//
// Three phases bracket the per-frame cost:
//   * SampleOnly — tick + read sampled values, no layout/paint. Interpolation only.
//   * Frame — tick + layout + display-list build, NO raster. The CPU-side frame.
//   * FrameWithRaster — Frame plus the shipped WebGPU backend render (the
//     dominant phase the earlier config omitted): the box tree is rasterized to
//     pixels every frame, the work the live loop actually does. The gap
//     FrameWithRaster − Frame is the raster cost the previous AnimationBench
//     never measured.
//
// Layout uses ImageSharpTextMeasurer (the measurer the live GUI uses), so text
// is shaped into ImageSharpShapedRuns the backend reuses at paint time — the
// faithful path, matching RasterBench/replay's GPU policy.
[MemoryDiagnoser]
public class AnimationBench
{
    public enum AnimKind { Transform, Layout }

    [Params(AnimKind.Transform, AnimKind.Layout)]
    public AnimKind Kind;

    [Params(10, 100)]
    public int Boxes;

    private const double FrameMs = 1000.0 / 60.0;
    private static readonly Size Viewport = new(1024, 768);
    private ImageSharpTextMeasurer _measurer = null!;
    private ImageSharpBackend _backend = null!;

    private Document _doc = null!;
    private StyleEngine _style = null!;
    private LayoutSession _session = null!;
    private double _clock;

    [GlobalSetup]
    public void Setup()
    {
        var css = Kind == AnimKind.Transform ? Fixtures.TransformAnimCss : Fixtures.LayoutAnimCss;
        _doc = HtmlParser.Parse(Fixtures.AnimatedBoxesHtml(Boxes));
        _doc.RecordLayoutMutations = true;
        _style = new StyleEngine();
        _style.AddStyleSheet(CssParser.ParseStyleSheet(css));
        _measurer = new ImageSharpTextMeasurer(FontResolver.Default);
        _backend = new ImageSharpBackend(FontResolver.Default, webFonts: null, useWebGpu: true);
        _session = new LayoutSession(_style) { VerifyAgainstFullRebuild = false };
        // Warm: full build + cascade. The cascade's AnimationCompositor.Compose
        // starts the animations (OnAnimationsCascaded), so later frames just tick
        // and re-sample rather than re-discovering the animation declarations.
        _session.Layout(_doc, Viewport, _measurer, nowMs: 0);
        _clock = 0;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _backend.Dispose();
        _measurer.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int Frame()
    {
        _clock += FrameMs;
        _style.AnimationEngine.Tick(_clock);
        var root = _session.Layout(_doc, Viewport, _measurer, _clock);
        return new DisplayListBuilder().Build(root).Items.Count;
    }

    [Benchmark]
    public int FrameWithRaster()
    {
        _clock += FrameMs;
        _style.AnimationEngine.Tick(_clock);
        var root = _session.Layout(_doc, Viewport, _measurer, _clock);
        var list = new DisplayListBuilder().Build(root);
        using var bmp = _backend.Render(list, Viewport, 1.0f);
        return bmp.Width;
    }

    [Benchmark]
    public int SampleOnly()
    {
        _clock += FrameMs;
        var engine = _style.AnimationEngine;
        engine.Tick(_clock);
        var samples = 0;
        foreach (var el in engine.ActiveElements)
        {
            foreach (var prop in engine.ActiveProperties(el))
            {
                if (engine.GetEffective(el, prop) is not null)
                {
                    samples++;
                }
            }
        }

        return samples;
    }
}
