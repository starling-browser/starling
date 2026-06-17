using AwesomeAssertions;
using SixLabors.ImageSharp;
namespace Starling.Engine.Tests;

/// <summary>
/// Live-network acceptance for the M2 exit demo:
/// <c>starling render https://example.com -o out.png</c>. Skipped by
/// default; the dedicated <c>network-tests</c> CI job exports
/// <c>STARLING_ALLOW_NETWORK=1</c> to opt in.
///
/// The test renders <c>https://example.com</c> through the full engine
/// (DNS, TLS 1.3, HTTP/1.1, parse, layout, paint, PNG encode) and
/// compares the result to a checked-in golden via SSIM. The threshold
/// (0.99) tracks the M2 milestone spec; if a future platform's font
/// rasterisation pushes the score below it, narrow the golden by
/// platform rather than relaxing the bar — the assertion is the point.
/// </summary>
[TestClass]
[TestCategory("NetworkLive")]
public class EngineLiveHttpsTests
{
    private const string LiveUrl = "https://example.com/";
    private const int ViewportWidth = 1024;
    private const int ViewportHeight = 768;
    private const float DefaultFontSize = 16f;
    private const double SsimFloor = 0.99;

    [TestMethod]
    public async Task Render_example_com_matches_golden_via_ssim()
    {
        if (Environment.GetEnvironmentVariable("STARLING_ALLOW_NETWORK") != "1")
        {
            return;
        }

        var repoRoot = LocateRepoRoot();
        var goldenPath = Path.Combine(repoRoot, "testdata", "golden", "live", "example.com.png");

        var output = Path.Combine(Path.GetTempPath(), $"starling-live-{Guid.NewGuid():N}.png");
        try
        {
            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                LiveUrl,
                new RenderOptions(new Size(ViewportWidth, ViewportHeight), DefaultFontSize),
                output,
                CancellationToken.None);

            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            File.Exists(output).Should().BeTrue();
            result.Value.DisplayText.Should().Contain("Example Domain");

            if (Environment.GetEnvironmentVariable("STARLING_UPDATE_GOLDENS") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
                File.Copy(output, goldenPath, overwrite: true);
                return;
            }

            File.Exists(goldenPath).Should().BeTrue(
                $"golden missing: {goldenPath}. Run with STARLING_UPDATE_GOLDENS=1 to regenerate.");

            var ssim = PngComparison.Ssim(output, goldenPath);
            ssim.Should().BeGreaterThanOrEqualTo(SsimFloor,
                $"live render drifted from golden; SSIM={ssim:F4}. If the upstream " +
                "page changed, re-render with STARLING_UPDATE_GOLDENS=1 after " +
                "confirming the new render is correct.");
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Starling.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate Starling.slnx walking up from the test binary.");
        }

        return dir.FullName;
    }
}
