using AwesomeAssertions;
using SixLabors.ImageSharp;

namespace Starling.Engine.Tests;

/// <summary>
/// Images and web fonts are fetched in parallel, the same way external scripts
/// and stylesheets already are. Before this the image and @font-face fetchers
/// awaited each download in turn, so a page with N images (or N fonts) paid N
/// sequential round-trips; fonts additionally waited for the entire image wave
/// to finish even though they only depend on the stylesheets.
/// </summary>
[TestClass]
public sealed class EngineParallelResourceFetchTests
{
    [TestMethod]
    public async Task Two_images_are_fetched_concurrently()
    {
        // Each image response is delayed, so overlapping fetches put two requests
        // in flight at once; a sequential loop would never exceed one. The bytes
        // need not decode — concurrency is observed during the in-flight GET.
        var routes = new Dictionary<string, Route>
        {
            ["/page.html"] = new("text/html",
                "<!doctype html><html><body>" +
                "<img src=\"/a.png\"><img src=\"/b.png\">" +
                "</body></html>", DelayMs: 0),
            ["/a.png"] = new("image/png", "not-a-real-png-a", DelayMs: 150),
            ["/b.png"] = new("image/png", "not-a-real-png-b", DelayMs: 150),
        };

        using var server = new ConcurrencyTrackingServer(routes);
        var outcome = await RenderAsync(server, "/page.html");

        outcome.IsOk.Should().BeTrue(outcome.IsErr ? outcome.Error.Message : "");
        server.RequestsServed.Should().Be(3, "document + two images");
        server.MaxConcurrency.Should().BeGreaterThanOrEqualTo(2,
            "the two <img> resources must be fetched in parallel");
    }

    [TestMethod]
    public async Task Two_web_fonts_are_fetched_concurrently()
    {
        // Two @font-face rules in an inline <style> (no stylesheet fetch needed),
        // each pointing at a delayed font URL. Overlapping fetches put both font
        // requests in flight at once.
        var routes = new Dictionary<string, Route>
        {
            ["/page.html"] = new("text/html",
                "<!doctype html><html><head><style>" +
                "@font-face{font-family:A;src:url(/a.woff2) format('woff2');}" +
                "@font-face{font-family:B;src:url(/b.woff2) format('woff2');}" +
                "</style></head><body><p style=\"font-family:A\">hi</p></body></html>", DelayMs: 0),
            ["/a.woff2"] = new("font/woff2", "not-a-real-font-a", DelayMs: 150),
            ["/b.woff2"] = new("font/woff2", "not-a-real-font-b", DelayMs: 150),
        };

        using var server = new ConcurrencyTrackingServer(routes);
        var outcome = await RenderAsync(server, "/page.html");

        outcome.IsOk.Should().BeTrue(outcome.IsErr ? outcome.Error.Message : "");
        server.RequestsServed.Should().Be(3, "document + two fonts");
        server.MaxConcurrency.Should().BeGreaterThanOrEqualTo(2,
            "the two @font-face url() sources must be fetched in parallel");
    }

    [TestMethod]
    public async Task Fonts_begin_before_the_image_wave_finishes()
    {
        // Fonts depend only on the stylesheets (here inline, so available
        // immediately), not on the images. With the images deliberately slow, the
        // font fetches must overlap them rather than wait for the image wave to
        // drain — so the server sees both slow images AND a font in flight at the
        // same time (peak >= 3).
        var routes = new Dictionary<string, Route>
        {
            ["/page.html"] = new("text/html",
                "<!doctype html><html><head><style>" +
                "@font-face{font-family:A;src:url(/a.woff2) format('woff2');}" +
                "@font-face{font-family:B;src:url(/b.woff2) format('woff2');}" +
                "</style></head><body>" +
                "<img src=\"/x.png\"><img src=\"/y.png\">" +
                "<p style=\"font-family:A\">hi</p></body></html>", DelayMs: 0),
            ["/x.png"] = new("image/png", "not-a-real-png-x", DelayMs: 400),
            ["/y.png"] = new("image/png", "not-a-real-png-y", DelayMs: 400),
            ["/a.woff2"] = new("font/woff2", "not-a-real-font-a", DelayMs: 100),
            ["/b.woff2"] = new("font/woff2", "not-a-real-font-b", DelayMs: 100),
        };

        using var server = new ConcurrencyTrackingServer(routes);
        var outcome = await RenderAsync(server, "/page.html");

        outcome.IsOk.Should().BeTrue(outcome.IsErr ? outcome.Error.Message : "");
        server.MaxConcurrency.Should().BeGreaterThanOrEqualTo(3,
            "fonts must start as soon as the (inline) stylesheets are parsed, " +
            "overlapping the still-in-flight slow images instead of waiting them out");
    }

    private static async Task<Starling.Common.Result<RenderOutcome, RenderError>> RenderAsync(
        ConcurrencyTrackingServer server, string path)
    {
        var tempPng = Path.Combine(Path.GetTempPath(), $"starling-parres-{Guid.NewGuid():N}.png");
        try
        {
            var engine = new StarlingEngine();
            return await engine.RenderAsync(
                $"http://127.0.0.1:{server.Port}{path}",
                new RenderOptions(new Size(800, 600), 16f),
                tempPng,
                CancellationToken.None);
        }
        finally
        {
            try { if (File.Exists(tempPng)) { File.Delete(tempPng); } } catch { /* best-effort */ }
        }
    }
}
