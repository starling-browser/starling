using Silk.NET.Maths;
using Silk.NET.Windowing;
using Starling.Css.Cascade;
using Starling.Gui.Core.Rendering;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Box;
using Starling.Layout.Text;

namespace Starling.Shell.Native;

/// <summary>
/// Phase-2 proof: load a page, lay it out, and present the GPU compositor's
/// output straight to the window's wgpu swapchain — zero readback, zero
/// re-upload. Throwaway chrome (none yet); the point is the present path.
/// Pass <c>--frames N</c> to auto-close after N frames (for automated runs);
/// otherwise it runs until the window closes.
/// </summary>
internal static class NativePresentDemo
{
    private const string Html =
        "<body style=\"margin:0;background-color:#eef1ff;font-family:sans-serif\">" +
        "<div style=\"position:absolute;left:0;top:0;width:1000px;height:700px;background-color:#dde3ff\"></div>" +
        "<div style=\"position:absolute;left:60px;top:60px;width:280px;height:160px;background-color:#cc3344\"></div>" +
        "<div style=\"position:absolute;left:200px;top:160px;width:280px;height:160px;background-color:#3366cc;opacity:0.6\"></div>" +
        "<div style=\"position:absolute;left:120px;top:300px;width:320px;height:90px;background-color:#22aa66;transform:rotate(8deg)\"></div>" +
        "<div style=\"position:absolute;left:40px;top:430px;width:600px;height:40px;color:#222;font-size:28px\">Starling — native wgpu present (zero readback)</div>" +
        "</body>";

    public static int Run(int maxFrames)
    {
        Silk.NET.Windowing.Glfw.GlfwWindowing.Use();
        var opts = WindowOptions.Default with
        {
            Title = "Starling (native present)",
            Size = new Vector2D<int>(800, 600),
            API = GraphicsAPI.None,
            VSync = false,
            ShouldSwapAutomatically = false,
        };

        using var window = Window.Create(opts);
        window.Initialize();

        var dpr = window.Size.X > 0 ? (float)window.FramebufferSize.X / window.Size.X : 1f;
        Console.WriteLine($"present: fb={window.FramebufferSize} logical={window.Size} dpr={dpr}");

        using var renderer = RenderSessionFactory.Create();
        if (!renderer.SupportsSurfaceTargets)
        {
            Console.Error.WriteLine("present: selected render backend does not support surface targets.");
            return 1;
        }
        var target = WindowSurfaceFrameTarget.TryCreate(window);
        if (target is null)
        {
            Console.Error.WriteLine("present: no GPU adapter / surface; cannot create surface target.");
            return 1;
        }
        using var _target = target;

        // Lay out the page once at the current logical size; re-layout on resize.
        var doc = HtmlParser.Parse(Html);
        var style = new StyleEngine();
        BlockBox root = LayoutAt(doc, style, window.FramebufferSize, dpr);
        var lastFb = window.FramebufferSize;

        var presented = 0;
        var failures = 0;

        window.FramebufferResize += sz =>
        {
            target.Configure(Math.Max(1, sz.X), Math.Max(1, sz.Y));
        };

        window.Render += _ =>
        {
            var fb = window.FramebufferSize;
            if (fb.X <= 0 || fb.Y <= 0)
            {
                return;
            }

            if (fb != lastFb)
            {
                dpr = window.Size.X > 0 ? (float)fb.X / window.Size.X : 1f;
                root = LayoutAt(doc, style, fb, dpr);
                lastFb = fb;
            }

            var logicalW = fb.X / dpr;
            var logicalH = fb.Y / dpr;
            using var frame = renderer.Render(new PageFrameRequest
            {
                Root = root,
                Scale = dpr,
                Viewport = new Rect(0, 0, logicalW, logicalH),
            }, target);
            var ok = frame.Presented;
            if (ok)
            {
                presented++;
            }
            else
            {
                failures++;
            }

            if (maxFrames > 0 && presented >= maxFrames)
            {
                window.Close();
            }
        };

        window.Run();
        Console.WriteLine($"PRESENT OK: {presented} frames presented zero-copy ({failures} surface-reconfig frames)");
        return presented > 0 ? 0 : 1;
    }

    private static BlockBox LayoutAt(Starling.Dom.Document doc, StyleEngine style, Vector2D<int> fb, float dpr)
    {
        var logicalW = Math.Max(1, fb.X / dpr);
        var logicalH = Math.Max(1, fb.Y / dpr);
        return new LayoutEngine(style, DefaultTextMeasurer.Instance)
            .LayoutDocument(doc, new Size(logicalW, logicalH));
    }
}
