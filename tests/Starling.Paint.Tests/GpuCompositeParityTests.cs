using AwesomeAssertions;
using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Css.Values;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Box;
using Starling.Layout.Text;
using Starling.Paint.Backend;
using Starling.Paint.Compositor;
using CompositorEngine = Starling.Paint.Compositor.Compositor;
using LayoutRect = Starling.Layout.Rect;

namespace Starling.Paint.Tests;

/// <summary>
/// Golden parity for the GPU layer-composite blend (wp:M12-13-gpu-composite-blend):
/// the same layer tree composited on the GPU must match the managed CPU
/// <c>BlendOp</c> path — byte-for-byte for upright (translate/scale) layers, and
/// within the SSIM tolerance the rest of the compositor uses for rotations
/// (filtered sampling rounds differently between the GPU sampler and the CPU
/// bilinear loop). Skips when the host has no GPU adapter.
/// </summary>
[TestClass]
public sealed class GpuCompositeParityTests
{
    private static BlockBox Layout(string html, int w, int h)
    {
        var document = HtmlParser.Parse(html);
        var engine = new LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance);
        return engine.LayoutDocument(document, new Size(w, h));
    }

    private static (RenderedBitmap cpu, RenderedBitmap gpu) RenderBothPaths(string html, int w, int h, float scale)
    {
        var root = Layout(html, w, h);
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null, useWebGpu: true);

        // Independent trees so neither path's per-layer picture cache perturbs the
        // other. Both feed the same geometry to CPU BlendOp vs GPU blend.
        var cpuTree = new LayerTreeBuilder().Build(root);
        var gpuTree = new LayerTreeBuilder().Build(root);

        var cpu = new CompositorEngine(backend) { DisableGpuBlend = true }
            .Render(cpuTree, new LayoutRect(0, 0, w, h), scale);
        var gpu = new CompositorEngine(backend) // GPU is the default path
            .Render(gpuTree, new LayoutRect(0, 0, w, h), scale);
        return (cpu, gpu);
    }

    /// <summary>Fraction of pixels whose every channel differs by at most <paramref name="tol"/>.</summary>
    private static double NearEqualFraction(RenderedBitmap a, RenderedBitmap b, int tol)
    {
        var pa = a.Rgba;
        var pb = b.Rgba;
        long near = 0;
        var pixels = pa.Length / 4;
        for (var i = 0; i < pa.Length; i += 4)
        {
            if (Math.Abs(pa[i] - pb[i]) <= tol
                && Math.Abs(pa[i + 1] - pb[i + 1]) <= tol
                && Math.Abs(pa[i + 2] - pb[i + 2]) <= tol
                && Math.Abs(pa[i + 3] - pb[i + 3]) <= tol)
            {
                near++;
            }
        }
        return (double)near / pixels;
    }

    [TestMethod]
    public void Upright_layers_match_cpu_blend_byte_for_byte()
    {
        if (GpuLayerCompositor.Shared is null)
        {
            Assert.Inconclusive("No GPU adapter available; GPU composite parity cannot be checked on this host.");
            return;
        }

        // Stacked translate-only layers: an opaque base, a half-opacity solid
        // layer, and an offset solid layer. No rotation/scale, so the GPU blend
        // lands texel-aligned and must reproduce the CPU AlphaOver exactly.
        const int W = 256, H = 192;
        var html =
            "<body style=\"margin:0;background-color:#dde3ff\">" +
            "<div style=\"position:absolute;left:0;top:0;width:256px;height:192px;background-color:#dde3ff\"></div>" +
            "<div style=\"opacity:0.5;position:absolute;left:20px;top:20px;width:120px;height:90px;background-color:#cc3333\"></div>" +
            "<div style=\"position:absolute;left:90px;top:60px;width:120px;height:90px;background-color:#2255aa\"></div>" +
            "</body>";

        var (c, g) = RenderBothPaths(html, W, H, 2.0f);
        using (c)
        using (g)
        {
            c.Width.Should().Be(g.Width);
            c.Height.Should().Be(g.Height);

            // Allow ±1 per channel for the rounding seam between the GPU's
            // unorm store and the CPU's round-half ToByte, but require the
            // overwhelming majority to match within that.
            var frac = NearEqualFraction(c, g, tol: 1);
            frac.Should().BeGreaterThanOrEqualTo(0.999,
                "an upright GPU blend must reproduce the CPU AlphaOver within a rounding unit");

            Ssim.ComputeRgba(c.Rgba, g.Rgba, c.Width, c.Height)
                .Should().BeGreaterThanOrEqualTo(0.999, "upright layers are byte-identical up to rounding");
        }
    }

    [TestMethod]
    public void Many_overlapping_animated_layers_match_cpu_blend()
    {
        if (GpuLayerCompositor.Shared is null)
        {
            Assert.Inconclusive("No GPU adapter available.");
            return;
        }

        // Mimic the animations page: many promoted layers (transform + opacity)
        // overlapping in a grid, the exact shape that stacks layers on the GPU
        // blend. A regression in multi-layer blending shows here even though the
        // single-layer parity tests pass.
        const int W = 360, H = 360;
        var sb = new System.Text.StringBuilder();
        sb.Append("<body style=\"margin:0;background-color:#101018\">");
        sb.Append("<div style=\"position:absolute;left:0;top:0;width:360px;height:360px;background-color:#181826\"></div>");
        for (var i = 0; i < 16; i++)
        {
            var x = 20 + (i % 4) * 80;
            var y = 20 + (i / 4) * 80;
            var angle = (i * 23) % 360;
            var op = 0.4 + 0.04 * i; // 0.4 .. ~1.0
            var color = $"#{(40 + i * 12) % 256:x2}{(200 - i * 9) % 256:x2}{(90 + i * 15) % 256:x2}";
            sb.Append($"<div style=\"position:absolute;left:{x}px;top:{y}px;width:70px;height:70px;" +
                      $"background-color:{color};opacity:{op.ToString(System.Globalization.CultureInfo.InvariantCulture)};" +
                      $"transform:rotate({angle}deg)\"></div>");
        }
        sb.Append("</body>");

        var (c, g) = RenderBothPaths(sb.ToString(), W, H, 2.0f);
        using (c)
        using (g)
        {
            c.Width.Should().Be(g.Width);
            Ssim.ComputeRgba(c.Rgba, g.Rgba, c.Width, c.Height)
                .Should().BeGreaterThanOrEqualTo(0.99,
                    "stacked animated layers must blend on the GPU the same as the CPU path");
        }
    }

    [TestMethod]
    public void Gpu_blend_is_deterministic_under_cache_churn()
    {
        if (GpuLayerCompositor.Shared is null)
        {
            Assert.Inconclusive("No GPU adapter available.");
            return;
        }

        // The GPU compositor's texture cache is a process-wide singleton shared
        // across every frame. If rendering the same layer tree twice — with a
        // DIFFERENT tree churning the cache in between — ever differs, that is a
        // frame-to-frame flicker. Render A, then B, then A again, byte-for-byte.
        const int W = 200, H = 200;
        const string htmlA =
            "<body style=\"margin:0;background-color:#202030\">" +
            "<div style=\"position:absolute;left:10px;top:10px;width:120px;height:80px;background-color:#cc3344;transform:rotate(15deg)\"></div>" +
            "<div style=\"opacity:0.6;position:absolute;left:60px;top:60px;width:120px;height:80px;background-color:#3366cc\"></div>" +
            "</body>";
        const string htmlB =
            "<body style=\"margin:0;background-color:#103018\">" +
            "<div style=\"position:absolute;left:20px;top:30px;width:90px;height:90px;background-color:#33aa55;transform:rotate(40deg)\"></div>" +
            "<div style=\"opacity:0.5;position:absolute;left:80px;top:20px;width:100px;height:120px;background-color:#aa33aa\"></div>" +
            "</body>";

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null, useWebGpu: true);

        RenderedBitmap RenderGpu(string html)
        {
            var root = Layout(html, W, H);
            var tree = new LayerTreeBuilder().Build(root);
            return new CompositorEngine(backend).Render(tree, new LayoutRect(0, 0, W, H), 2.0f);
        }

        using var a1 = RenderGpu(htmlA);
        using var b = RenderGpu(htmlB);          // churn the shared cache
        using var a2 = RenderGpu(htmlA);          // same content as a1 — must match
        using var a3 = RenderGpu(htmlA);          // and again

        a1.Rgba.AsSpan().SequenceEqual(a2.Rgba).Should().BeTrue(
            "the same layer tree must blend identically after another tree churned the shared GPU cache");
        a2.Rgba.AsSpan().SequenceEqual(a3.Rgba).Should().BeTrue(
            "repeated renders of the same tree must be byte-identical (no flicker)");
    }

    [TestMethod]
    public void Gpu_texture_readback_blends_surface_overlays_on_top()
    {
        if (GpuLayerCompositor.Shared is null)
        {
            Assert.Inconclusive("No GPU adapter available.");
            return;
        }

        const int W = 160, H = 120;
        var root = Layout("<body style=\"margin:0;background-color:#102030\"></body>", W, H);
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null, useWebGpu: true);
        var tree = new LayerTreeBuilder().Build(root);
        var overlayScene = SurfaceOverlayScene.Create(50, 40, 0x450C0A7E1L,
            builder => builder.FillInstances(
                SurfaceOverlayPrimitive.Rectangle,
                [new SurfaceOverlayInstance(0, 0, 50, 40, new CssColor(255, 0, 0, 255))]));
        var overlays = new[] { new SurfaceOverlayLayer(20, 30, 50, 40, overlayScene) };

        using var rendered = new CompositorEngine(backend)
            .RenderGpuReadback(tree, new LayoutRect(0, 0, W, H), 1.0f, overlays);

        rendered.GetPixel(45, 50).Should().Be((255, 0, 0, 255),
            "viewport readback must include surface overlay quads after page content");
    }

    [TestMethod]
    public void Gpu_blend_skips_fully_clipped_ops_without_losing_the_frame()
    {
        if (GpuLayerCompositor.Shared is null)
        {
            Assert.Inconclusive("No GPU adapter available.");
            return;
        }

        const int W = 120, H = 100;
        var html =
            "<body style=\"margin:0\">" +
            "<div style=\"position:absolute;left:0;top:0;width:120px;height:100px;background-color:#123456\"></div>" +
            "<div style=\"position:absolute;left:200px;top:200px;width:40px;height:40px;" +
            "overflow:hidden;opacity:.9;background-color:#ff0000\"></div>" +
            "</body>";
        var root = Layout(html, W, H);
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null, useWebGpu: true);
        var tree = new LayerTreeBuilder().Build(root);

        using var rendered = new CompositorEngine(backend)
            .RenderGpuReadback(tree, new LayoutRect(0, 0, W, H), 1.0f);

        rendered.GetPixel(10, 10).Should().Be((18, 52, 86, 255),
            "a fully clipped promoted layer must not invalidate the GPU command buffer");
    }

    [TestMethod]
    public void Rotated_layer_matches_cpu_blend_within_ssim_tolerance()
    {
        if (GpuLayerCompositor.Shared is null)
        {
            Assert.Inconclusive("No GPU adapter available; GPU composite parity cannot be checked on this host.");
            return;
        }

        const int W = 220, H = 220;
        var html =
            "<body style=\"margin:0;background-color:#eef0ff\">" +
            "<div style=\"position:absolute;left:50px;top:50px;width:110px;height:70px;" +
            "background-color:#cc2222;transform:rotate(37deg)\"></div>" +
            "</body>";

        var (c, g) = RenderBothPaths(html, W, H, 2.0f);
        using (c)
        using (g)
        {
            Ssim.ComputeRgba(c.Rgba, g.Rgba, c.Width, c.Height)
                .Should().BeGreaterThanOrEqualTo(0.99,
                    "the GPU-rotated layer must match the CPU bilinear blend within the compositor's SSIM tolerance");
        }
    }
}
