using AwesomeAssertions;
using SixLabors.ImageSharp;
namespace Starling.Engine.Tests;

/// <summary>
/// End-to-end coverage for the M5 animation frame loop: lay a page out once
/// and ask the engine to repaint at multiple timestamps. The compositor
/// inside <see cref="StarlingEngine.RenderFrame"/> seeds and ticks the
/// AnimationEngine + TransitionEngine, so the pixels we get back at
/// different <c>nowMs</c> values must differ while a CSS animation is in
/// flight. This is the structural counterpart to a golden-PNG check —
/// cheaper to maintain than a recorded bitmap but sufficient to prove the
/// rAF → cascade → paint plumbing is wired end-to-end.
/// </summary>
[TestClass]
public class AnimationFrameRenderTests
{
    [TestMethod]
    public async Task RenderFrame_samples_keyframe_animation_at_different_times()
    {
        // A 1s linear opacity fade from 0 → 1 driven by @keyframes. At t=0
        // the div is fully transparent; at t=999 it is fully opaque. The
        // rendered bitmaps for those two frames must differ in the div's
        // pixel area — if they don't, the animation isn't being sampled.
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-anim-{Guid.NewGuid():N}.html");
        try
        {
            File.WriteAllText(fixture,
                "<!doctype html><html><head><style>" +
                "@keyframes fade { 0% { background-color: rgb(255,0,0) } 100% { background-color: rgb(0,0,255) } }" +
                ".target { width: 200px; height: 200px; background-color: red;" +
                "          animation: fade 1000ms linear; }" +
                "</style></head><body><div class=\"target\"></div></body></html>");

            var engine = new StarlingEngine();
            var laid = await engine.LayoutPageAsync(
                "file://" + fixture.Replace('\\', '/'),
                new RenderOptions(new Size(300, 300), FontSize: 16f),
                CancellationToken.None);

            laid.IsOk.Should().BeTrue(laid.IsErr ? laid.Error.Message : "");
            using var page = laid.Value;

            using var f0 = engine.RenderFrame(page, nowMs: 0);
            using var f500 = engine.RenderFrame(page, nowMs: 500);
            using var f999 = engine.RenderFrame(page, nowMs: 999);

            f0.Width.Should().Be(f500.Width).And.Be(f999.Width);
            f0.Height.Should().Be(f500.Height).And.Be(f999.Height);

            // The three frames must not be byte-identical — the animation is
            // either changing pixels (sampling worked) or it isn't (broken).
            f0.Rgba.SequenceEqual(f500.Rgba).Should().BeFalse(
                "the keyframe animation should change background color between t=0 and t=500ms");
            f500.Rgba.SequenceEqual(f999.Rgba).Should().BeFalse(
                "the keyframe animation should change background color between t=500 and t=999ms");
        }
        finally
        {
            if (File.Exists(fixture))
            {
                File.Delete(fixture);
            }
        }
    }

    [TestMethod]
    public async Task RenderFrame_with_no_animation_is_deterministic()
    {
        // Sanity check the other side: a static page repainted at two
        // timestamps must produce byte-identical output. If this fails the
        // frame loop is non-deterministic for static content — a regression
        // that would silently invalidate any future golden-PNG suite.
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-static-{Guid.NewGuid():N}.html");
        try
        {
            File.WriteAllText(fixture,
                "<!doctype html><html><body><div style=\"width:100px;height:100px;background:blue\"></div></body></html>");

            var engine = new StarlingEngine();
            var laid = await engine.LayoutPageAsync(
                "file://" + fixture.Replace('\\', '/'),
                new RenderOptions(new Size(200, 200), FontSize: 16f),
                CancellationToken.None);

            laid.IsOk.Should().BeTrue(laid.IsErr ? laid.Error.Message : "");
            using var page = laid.Value;

            using var a = engine.RenderFrame(page, nowMs: 0);
            using var b = engine.RenderFrame(page, nowMs: 5000);

            a.Rgba.SequenceEqual(b.Rgba).Should().BeTrue(
                "a static page must rasterize the same bytes at every frame");
        }
        finally
        {
            if (File.Exists(fixture))
            {
                File.Delete(fixture);
            }
        }
    }
}
