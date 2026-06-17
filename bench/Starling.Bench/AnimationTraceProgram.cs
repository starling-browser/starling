using System.Diagnostics;
using System.Globalization;
using Starling.Common.Diagnostics;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Html;
using Starling.Layout;
using Starling.Paint;
using Starling.Paint.Backend;
using Starling.Paint.DisplayList;

namespace Starling.Bench;

/// <summary>
/// The <c>animtrace</c> run-mode: drive the live <c>testdata/sites/animations</c>
/// page through N synthetic animation frames and read the per-frame
/// <c>raster.text.shaped_reused</c> / <c>raster.text.shaped_rebuilt</c> counters
/// straight off the <c>paint.raster.command_record</c> span (ImageSharpBackend.cs).
///
/// This is the faithful headless stand-in for one live trace of the Animations
/// tab: it lays the real page out with the same <see cref="ImageSharpTextMeasurer"/>
/// the GUI uses (so layout produces <c>ImageSharpShapedRun</c>s, the only kind the
/// backend can reuse at paint time), rebuilds the whole frame each tick (the live
/// full-relayout path the postmortem named), and rasters through the shipped
/// WebGPU backend. If <c>shaped_rebuilt</c> dominates, paint-time reshaping is
/// real and the page is on the heavy path; if <c>shaped_reused</c> dominates,
/// the reshape suspicion is wrong and the cost lives upstream (relayout/raster).
/// </summary>
internal static class AnimationTraceProgram
{
    public static int Run(string[] args)
    {
        var frames = 120;
        var warmup = 30;
        var scale = 2.0f; // the Retina device scale the live app actually runs at

        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--frames": frames = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--warmup": warmup = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--scale": scale = float.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    default:
                        Console.Error.WriteLine($"unknown option: {args[i]}");
                        return 2;
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine("invalid option value.");
            return 2;
        }

        var siteDir = Path.Combine(Fixtures.RepoRoot, "testdata", "sites", "animations");
        var html = File.ReadAllText(Path.Combine(siteDir, "index.html"));
        // The page links style.css; the bench has no resource loader, so inline
        // the linked sheet exactly as the engine would after fetching it.
        var css = File.ReadAllText(Path.Combine(siteDir, "style.css"));

        var doc = HtmlParser.Parse(html);
        var style = new StyleEngine();
        style.AddStyleSheet(CssParser.ParseStyleSheet(css));
        var viewport = new Size(1024, 768);
        using var measurer = new ImageSharpTextMeasurer(FontResolver.Default);

        var frameData = new FrameCounters();
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null, useWebGpu: true);

        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == StarlingTelemetry.SourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a =>
            {
                if (a.OperationName != "paint.raster.command_record")
                {
                    return;
                }

                frameData.LastReused = TagInt(a, "raster.text.shaped_reused");
                frameData.LastRebuilt = TagInt(a, "raster.text.shaped_rebuilt");
                frameData.LastChars = TagInt(a, "raster.text.chars");
                frameData.LastFontMiss = TagInt(a, "raster.font.cache_miss");
                frameData.LastDrawTextMs = TagDouble(a, "raster.time.draw_text_ms");
                frameData.LastFontCreateMs = TagDouble(a, "raster.time.font_create_ms");
            },
        };
        ActivitySource.AddActivityListener(listener);

        var clock = 0.0;
        var delta = 1000.0 / 60.0;
        var total = warmup + frames;

        long sumReused = 0, sumRebuilt = 0, sumChars = 0, sumMiss = 0;
        double sumDrawMs = 0, sumFontMs = 0;
        var steadyReused = 0;
        var steadyRebuilt = 0;

        Console.WriteLine($"animtrace: testdata/sites/animations  scale {scale.ToString(CultureInfo.InvariantCulture)}  backend {backend.Name}");
        Console.WriteLine($"frames {frames} ({warmup} warmup)  measurer ImageSharpTextMeasurer  path full-relayout/frame + flat render");
        Console.WriteLine();

        for (var f = 0; f < total; f++)
        {
            clock += delta;
            style.AnimationEngine.Tick(clock);
            var root = new LayoutEngine(style, measurer).LayoutDocument(doc, viewport, clock);
            var list = new DisplayListBuilder().Build(root);
            frameData.Reset();
            using (backend.Render(list, viewport, scale)) { }

            if (f < warmup)
            {
                continue;
            }

            sumReused += frameData.LastReused;
            sumRebuilt += frameData.LastRebuilt;
            sumChars += frameData.LastChars;
            sumMiss += frameData.LastFontMiss;
            sumDrawMs += frameData.LastDrawTextMs;
            sumFontMs += frameData.LastFontCreateMs;
            if (f == total - 1)
            {
                steadyReused = frameData.LastReused;
                steadyRebuilt = frameData.LastRebuilt;
            }
        }

        var n = frames;
        var meanReused = (double)sumReused / n;
        var meanRebuilt = (double)sumRebuilt / n;
        var totalRuns = meanReused + meanRebuilt;
        var reusePct = totalRuns > 0 ? meanReused / totalRuns : 0;

        Console.WriteLine($"{"counter",-26}{"mean/frame",14}{"steady frame",16}");
        Console.WriteLine($"{"raster.text.shaped_reused",-26}{meanReused,14:F1}{steadyReused,16}");
        Console.WriteLine($"{"raster.text.shaped_rebuilt",-26}{meanRebuilt,14:F1}{steadyRebuilt,16}");
        Console.WriteLine($"{"raster.text.chars",-26}{(double)sumChars / n,14:F0}{"",16}");
        Console.WriteLine($"{"raster.font.cache_miss",-26}{(double)sumMiss / n,14:F1}{"",16}");
        Console.WriteLine();
        Console.WriteLine($"reuse ratio: {reusePct.ToString("P1", CultureInfo.InvariantCulture)}  ({meanReused:F0} reused vs {meanRebuilt:F0} rebuilt per frame)");
        Console.WriteLine($"raster.time.draw_text_ms mean/frame: {sumDrawMs / n:F3}   font_create_ms mean/frame: {sumFontMs / n:F3}");
        Console.WriteLine();
        if (meanRebuilt > meanReused)
        {
            Console.WriteLine("VERDICT: shaped_rebuilt dominates — text IS re-shaped at paint time every frame (heavy path confirmed).");
        }
        else
        {
            Console.WriteLine("VERDICT: shaped_reused dominates — paint-time shaping is cached; the per-frame cost lives in relayout/raster, not reshape.");
        }

        return 0;
    }

    private static int TagInt(Activity a, string key)
        => a.GetTagItem(key) is { } v ? Convert.ToInt32(v, CultureInfo.InvariantCulture) : 0;

    private static double TagDouble(Activity a, string key)
        => a.GetTagItem(key) is { } v ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : 0;

    /// <summary>
    /// Holds per-frame counters captured from Activity tags set by the backend
    /// on the <c>paint.raster.command_record</c> span.
    /// </summary>
    private sealed class FrameCounters
    {
        public int LastReused, LastRebuilt, LastChars, LastFontMiss;
        public double LastDrawTextMs, LastFontCreateMs;

        public void Reset()
        {
            LastReused = LastRebuilt = LastChars = LastFontMiss = 0;
            LastDrawTextMs = LastFontCreateMs = 0;
        }
    }
}
