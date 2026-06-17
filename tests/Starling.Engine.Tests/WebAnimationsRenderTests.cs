using AwesomeAssertions;
using SixLabors.ImageSharp;
namespace Starling.Engine.Tests;

/// <summary>
/// End-to-end coverage for the Web Animations API (<c>element.animate</c>) on
/// the default Starling JS engine: a page script registers a programmatic
/// animation, which must flow through the engine's per-document script-animation
/// store into the live AnimationEngine and be sampled by <see cref="StarlingEngine.RenderFrame"/>
/// — exactly the path declarative <c>@keyframes</c> use. Frames at different
/// timestamps must differ while the animation is in flight.
/// </summary>
[TestClass]
public class WebAnimationsRenderTests
{
    [TestMethod]
    public async Task ElementAnimate_renders_a_moving_animation_across_frames()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-waapi-{Guid.NewGuid():N}.html");
        try
        {
            File.WriteAllText(fixture,
                "<!doctype html><html><body>" +
                "<div id=\"t\" style=\"width:200px;height:200px;background:rgb(255,0,0)\"></div>" +
                "<script>document.getElementById('t').animate(" +
                "[{backgroundColor:'rgb(255,0,0)'},{backgroundColor:'rgb(0,0,255)'}]," +
                "{duration:1000, easing:'linear'});</script>" +
                "</body></html>");

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

            // If element.animate() reached the compositor, the div's background
            // colour changes over time — the frames must not be byte-identical.
            f0.Rgba.SequenceEqual(f500.Rgba).Should().BeFalse(
                "element.animate() should change the background between t=0 and t=500ms");
            f500.Rgba.SequenceEqual(f999.Rgba).Should().BeFalse(
                "element.animate() should change the background between t=500 and t=999ms");
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
