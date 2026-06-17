using AwesomeAssertions;
using SixLabors.ImageSharp;
namespace Starling.Engine.Tests;

/// <summary>
/// Offline snapshot-fixture render test. Serves the vendored
/// <c>testdata/snapshots/nginx.org/</c> tree out of a local loopback HTTP
/// stub, points the engine at it, and asserts the rendered PNG matches a
/// committed golden under <c>testdata/golden/snapshots/nginx.org.png</c>.
///
/// The comparison prefers a byte-exact PNG match (the cheapest signal:
/// if the bytes are identical we are done). When AA / font-hinting noise
/// makes that infeasible we fall back to SSIM ≥ 0.99 over the pixel
/// buffers — the same threshold as the live test, since the snapshot
/// pipeline is the offline twin of the live one.
///
/// To regenerate the golden after an intentional rendering change, run
/// with <c>STARLING_UPDATE_GOLDENS=1</c>; the test will write the produced
/// PNG into the golden path and pass.
/// </summary>
[TestClass]
[TestCategory("GoldenImage")]
public class EngineSnapshotRenderTests
{
    private const string Host = "nginx.org";
    private const int ViewportWidth = 1024;
    private const int ViewportHeight = 768;
    private const float DefaultFontSize = 16f;
    private const double SsimFloor = 0.99;

    [TestMethod]
    public async Task Snapshot_nginx_org_renders_match_golden()
    {
        var repoRoot = LocateRepoRoot();
        var snapshotDir = Path.Combine(repoRoot, "testdata", "snapshots", Host);
        Directory.Exists(snapshotDir).Should().BeTrue($"snapshot directory missing: {snapshotDir}");
        File.Exists(Path.Combine(snapshotDir, "manifest.json")).Should().BeTrue("manifest.json must accompany the snapshot");

        var goldenPath = Path.Combine(repoRoot, "testdata", "golden", "snapshots", $"{Host}.png");

        using var server = await SnapshotHttpServer.StartAsync(snapshotDir);

        var output = Path.Combine(Path.GetTempPath(), $"starling-snapshot-{Guid.NewGuid():N}.png");
        try
        {
            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                $"http://localhost:{server.Port}/",
                new RenderOptions(new Size(ViewportWidth, ViewportHeight), DefaultFontSize),
                output,
                CancellationToken.None);

            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            File.Exists(output).Should().BeTrue();

            // The page title is "nginx"; <h2> says "nginx"; just sanity-check
            // some real text from the body so a busted text pipeline fails
            // loudly before the SSIM assert.
            result.Value.DisplayText.Should().Contain("nginx");

            if (Environment.GetEnvironmentVariable("STARLING_UPDATE_GOLDENS") == "1")
            {
                Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
                File.Copy(output, goldenPath, overwrite: true);
                return;
            }

            File.Exists(goldenPath).Should().BeTrue(
                $"golden missing: {goldenPath}. Run with STARLING_UPDATE_GOLDENS=1 to regenerate.");

            if (PngComparison.BytesEqual(output, goldenPath))
            {
                return;
            }

            var ssim = PngComparison.Ssim(output, goldenPath);
            ssim.Should().BeGreaterThanOrEqualTo(SsimFloor,
                $"snapshot drift for {Host}; bytes differ and SSIM dropped to {ssim:F4}. " +
                "If the change is intentional, re-vendor the snapshot and " +
                "regenerate the golden with STARLING_UPDATE_GOLDENS=1.");
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
