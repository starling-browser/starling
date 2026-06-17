using AwesomeAssertions;
using SixLabors.ImageSharp;

namespace Starling.Engine.Tests;

[TestClass]
public class EngineRozTests
{
    [TestMethod]
    public async Task Render_returns_err_when_roz_dom_depth_limit_is_exceeded()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-roz-{Guid.NewGuid():N}.html");
        var output = Path.Combine(Path.GetTempPath(), $"starling-roz-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllText(fixture, BuildDeepDomHtml(depth: 120));
            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                "file://" + fixture.Replace('\\', '/'),
                new RenderOptions(new Size(400, 200), 16f)
                {
                    Roz = RenderRozOptions.Default with
                    {
                        MaxDomDepth = 40,
                        MaxDomNodes = null,
                        MaxRenderWallTimeMs = null,
                        MaxManagedHeapBytes = null,
                        MaxWorkingSetBytes = null,
                    },
                },
                output);

            result.IsErr.Should().BeTrue();
            result.Error.Message.Should().Contain("Roz limit exceeded");
            result.Error.Message.Should().Contain("DOM depth");
        }
        finally
        {
            if (File.Exists(fixture))
            {
                File.Delete(fixture);
            }

            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    [TestMethod]
    public async Task Render_returns_err_when_roz_dom_node_limit_is_exceeded()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-roz-{Guid.NewGuid():N}.html");
        var output = Path.Combine(Path.GetTempPath(), $"starling-roz-{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllText(fixture, BuildWideDomHtml(nodeCount: 600));
            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                "file://" + fixture.Replace('\\', '/'),
                new RenderOptions(new Size(400, 200), 16f)
                {
                    Roz = RenderRozOptions.Default with
                    {
                        MaxDomNodes = 120,
                        MaxDomDepth = null,
                        MaxRenderWallTimeMs = null,
                        MaxManagedHeapBytes = null,
                        MaxWorkingSetBytes = null,
                    },
                },
                output);

            result.IsErr.Should().BeTrue();
            result.Error.Message.Should().Contain("Roz limit exceeded");
            result.Error.Message.Should().Contain("DOM nodes");
        }
        finally
        {
            if (File.Exists(fixture))
            {
                File.Delete(fixture);
            }

            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    private static string BuildDeepDomHtml(int depth)
    {
        var sb = new System.Text.StringBuilder("<!doctype html><body>");
        for (var i = 0; i < depth; i++)
        {
            sb.Append("<div>");
        }

        sb.Append("x");
        for (var i = 0; i < depth; i++)
        {
            sb.Append("</div>");
        }

        sb.Append("</body>");
        return sb.ToString();
    }

    private static string BuildWideDomHtml(int nodeCount)
    {
        var sb = new System.Text.StringBuilder("<!doctype html><body>");
        for (var i = 0; i < nodeCount; i++)
        {
            sb.Append("<span>x</span>");
        }

        sb.Append("</body>");
        return sb.ToString();
    }
}
