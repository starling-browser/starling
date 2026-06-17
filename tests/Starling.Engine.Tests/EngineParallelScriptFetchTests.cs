using AwesomeAssertions;
using SixLabors.ImageSharp;

namespace Starling.Engine.Tests;

/// <summary>
/// External scripts are fetched in parallel (HTML §4.12.1 constrains execution
/// order, not fetch order), but classic scripts still execute in document
/// order. Before this the fetcher awaited each script's download in turn, so a
/// page with N external scripts paid N sequential round-trips.
/// </summary>
[TestClass]
public sealed class EngineParallelScriptFetchTests
{
    [TestMethod]
    public async Task Two_external_scripts_are_fetched_concurrently()
    {
        // Each script response is delayed, so if the fetches overlap the server
        // sees two requests in flight at once; if they were sequential it would
        // never exceed one.
        var routes = new Dictionary<string, Route>
        {
            ["/page.html"] = new("text/html",
                "<!doctype html><html><body><p>hi</p>" +
                "<script src=\"/a.js\"></script><script src=\"/b.js\"></script>" +
                "</body></html>", DelayMs: 0),
            ["/a.js"] = new("text/javascript", "var a = 1;", DelayMs: 150),
            ["/b.js"] = new("text/javascript", "var b = 2;", DelayMs: 150),
        };

        using var server = new ConcurrencyTrackingServer(routes);
        var outcome = await RenderAsync(server, "/page.html");

        outcome.IsOk.Should().BeTrue(outcome.IsErr ? outcome.Error.Message : "");
        server.RequestsServed.Should().Be(3, "document + two scripts");
        server.MaxConcurrency.Should().BeGreaterThanOrEqualTo(2,
            "the two external scripts must be fetched in parallel");
    }

    [TestMethod]
    public async Task Classic_scripts_execute_in_document_order_even_when_the_later_one_downloads_first()
    {
        // b.js returns immediately; a.js is slow. Parallel fetch means b is
        // available first, but document order (a before b) must still win at
        // execution time, yielding "ab".
        var routes = new Dictionary<string, Route>
        {
            ["/page.html"] = new("text/html",
                "<!doctype html><html><body><p id=\"out\"></p>" +
                "<script src=\"/a.js\"></script><script src=\"/b.js\"></script>" +
                "</body></html>", DelayMs: 0),
            ["/a.js"] = new("text/javascript",
                "var o=document.getElementById('out'); o.textContent = o.textContent + 'a';", DelayMs: 150),
            ["/b.js"] = new("text/javascript",
                "var o=document.getElementById('out'); o.textContent = o.textContent + 'b';", DelayMs: 0),
        };

        using var server = new ConcurrencyTrackingServer(routes);
        var outcome = await RenderAsync(server, "/page.html");

        outcome.IsOk.Should().BeTrue(outcome.IsErr ? outcome.Error.Message : "");
        outcome.Value.DisplayText.Should().Contain("ab",
            "classic scripts run in document order regardless of which finished downloading first");
    }

    private static async Task<Starling.Common.Result<RenderOutcome, RenderError>> RenderAsync(
        ConcurrencyTrackingServer server, string path)
    {
        var tempPng = Path.Combine(Path.GetTempPath(), $"starling-parscript-{Guid.NewGuid():N}.png");
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
