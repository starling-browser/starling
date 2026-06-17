using BenchmarkDotNet.Attributes;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Html;
using Starling.Layout;
using Starling.Paint;
using Starling.Paint.Backend;
using Starling.Paint.DisplayList;

namespace Starling.Bench;

// Raster cost — the actual renderer number, separate from layout and from
// display-list build. Uses the WebGPU ImageSharp backend (useWebGpu: true) so
// the timing reflects the GPU paint path Starling actually ships. Each display
// list is built once in Setup; the benchmark times only backend.Render. Scope:
// raster-only. The host must expose a working WebGPU adapter (see
// WgpuNativeLoader); the backend throws on Render if none is available.
//
// PaintBench is the matching display-list-build benchmark; the two together
// separate "build the paint list" from "draw it to pixels".
[MemoryDiagnoser]
public class RasterBench
{
    private static readonly Size Viewport = new(1024, 768);
    private ImageSharpBackend _backend = null!;
    private DisplayList _solid = null!;
    private DisplayList _borders = null!;
    private DisplayList _text = null!;

    [GlobalSetup]
    public void Setup()
    {
        _backend = new ImageSharpBackend(FontResolver.Default, webFonts: null, useWebGpu: true);
        _solid = BuildList(Fixtures.SolidBackgrounds(150), Fixtures.SolidBackgroundsCss);
        _borders = BuildList(Fixtures.ManyBorders(150), Fixtures.ManyBordersCss);
        _text = BuildList(Fixtures.TextHeavyParagraphs(80), css: null);
    }

    [GlobalCleanup]
    public void Cleanup() => _backend.Dispose();

    [Benchmark]
    public int Raster_SolidBackgrounds() => Render(_solid);

    [Benchmark]
    public int Raster_ManyBorders() => Render(_borders);

    [Benchmark]
    public int Raster_TextHeavy() => Render(_text);

    private int Render(DisplayList list)
    {
        using var bmp = _backend.Render(list, Viewport, 1.0f);
        return bmp.Width;
    }

    private static DisplayList BuildList(string html, string? css)
    {
        var doc = HtmlParser.Parse(html);
        var style = new StyleEngine();
        if (css is not null)
        {
            style.AddStyleSheet(CssParser.ParseStyleSheet(css));
        }

        var root = new LayoutEngine(style).LayoutDocument(doc, Viewport);
        return new DisplayListBuilder().Build(root);
    }
}
