using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using SixLabors.ImageSharp;
using Starling.Bindings;
using Starling.Dom;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;
using Starling.Net;
namespace Starling.Engine.Tests;

/// <summary>
/// B7 — the end-to-end google.com smoke test. Demonstrates that the M3 → M7
/// engine stack (URL → HTTP → HTML parse → style → layout → paint → PNG)
/// lights up against a "shaped-like-google" home page and search-results
/// page served from a local <see cref="HttpListener"/>.
///
/// <para>
/// <see cref="StarlingEngine.RenderAsync"/> now executes page JavaScript
/// between HTML parse and layout (see <c>RunScriptsAsync</c> in
/// <c>Engine.cs</c>): inline + classic external scripts run in document
/// order, DOMContentLoaded and load fire, and fetch/XHR completions are
/// pumped to quiescence before display text is extracted. The offline
/// fixtures still inline their visible result text statically — the engine
/// would also surface JS-populated results, but the fixture stays static
/// so the test isolates the layout/paint path. <see cref="Search_fetches_results_via_js_fetch_and_populates_dom"/>
/// covers the JS-driven shape directly against <c>JsRuntime</c> +
/// <c>FetchBinding</c>; <c>EngineJsExecutionTests</c> covers the
/// engine-level fetch→DOM-mutation→DisplayText round trip.
/// </para>
///
/// <para>Known follow-ups:</para>
/// <list type="bullet">
///   <item><c>setTimeout</c> / <c>setInterval</c> are not installed in the
///   engine's JS environment (no host event loop to advance). Pages that
///   bootstrap via a 0ms timer will hang silently rather than render.</item>
///   <item>Pre-existing failures (<c>Snapshot_nginx_org_renders_match_golden</c>,
///   <c>Underlined_link_emits_text_and_underline_fill</c>) are not regressions
///   for this task — see the handoff doc.</item>
/// </list>
/// </summary>
[TestClass]
public sealed class GoogleSearchTests
{
    private const int ViewportWidth = 800;
    private const int ViewportHeight = 600;
    private const float DefaultFontSize = 16f;

    // -----------------------------------------------------------------
    // Offline-fixture-mode tests (always run)
    // -----------------------------------------------------------------

    [TestMethod]
    public async Task Google_home_renders_brand_and_nav()
    {
        var (homeHtml, _) = LoadFixtures();
        using var server = await FixtureServer.StartAsync(homeHtml, searchResultsHtml: "");

        var output = Path.Combine(Path.GetTempPath(), $"starling-b7-home-{Guid.NewGuid():N}.png");
        try
        {
            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                $"http://127.0.0.1:{server.Port}/",
                new RenderOptions(new Size(ViewportWidth, ViewportHeight), DefaultFontSize),
                output,
                CancellationToken.None);

            result.IsOk.Should().BeTrue(
                result.IsErr ? $"engine render failed: {result.Error.Message}" : "");

            var outcome = result.Value;
            outcome.Width.Should().Be(ViewportWidth);
            outcome.Height.Should().Be(ViewportHeight);

            File.Exists(output).Should().BeTrue();
            new FileInfo(output).Length.Should().BeGreaterThan(500,
                "real PNG output should be substantially larger than just the header bytes");

            // Brand + a few top-nav links the user would expect to see on the
            // real google.com home (and which our fixture renders statically).
            outcome.DisplayText.Should().Contain("Google");
            outcome.DisplayText.Should().Contain("Gmail");
            outcome.DisplayText.Should().Contain("Images");
            outcome.DisplayText.Should().Contain("Sign in");
        }
        finally
        {
            TryDelete(output);
        }
    }

    [TestMethod]
    public async Task Google_search_renders_result_list()
    {
        var (_, searchHtml) = LoadFixtures();
        using var server = await FixtureServer.StartAsync(homeHtml: "", searchHtml);

        var output = Path.Combine(Path.GetTempPath(), $"starling-b7-search-{Guid.NewGuid():N}.png");
        try
        {
            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                $"http://127.0.0.1:{server.Port}/search?q=hello",
                new RenderOptions(new Size(ViewportWidth, ViewportHeight), DefaultFontSize),
                output,
                CancellationToken.None);

            // If anything in the pipeline blew up, surface the precise error
            // (which is the whole point of B7 as a diagnostic).
            result.IsOk.Should().BeTrue(
                result.IsErr ? $"engine render failed for /search?q=hello: {result.Error.Message}" : "");

            var outcome = result.Value;
            outcome.Width.Should().Be(ViewportWidth);
            outcome.Height.Should().Be(ViewportHeight);

            File.Exists(output).Should().BeTrue();
            new FileInfo(output).Length.Should().BeGreaterThan(500);

            var text = outcome.DisplayText;
            // Page-level chrome.
            text.Should().Contain("Google");
            // Stats line — a result-page signature string.
            text.Should().Contain("results");
            // Specific search-result hits the fixture inlines. These are the
            // strings a user would scan for on a real "hello" SERP.
            text.Should().Contain("Hello - Wikipedia");
            text.Should().Contain("hellomagazine.com");
            text.Should().Contain("hello, world program");
        }
        finally
        {
            TryDelete(output);
        }
    }

    /// <summary>
    /// Drives the JS-engine + DOM + fetch path end-to-end against an in-process
    /// HTTP listener — the piece that the static engine render does not yet
    /// exercise. Asserts that a script which calls <c>fetch('/api/results')</c>,
    /// awaits the JSON, and appends nodes to <c>document.getElementById</c>
    /// ends up with the result text in <c>document.body</c>.
    ///
    /// This is the "would the JS pipeline produce a populated DOM if the engine
    /// drove it" rehearsal: if RenderAsync ever gains a JS-execution hook, the
    /// same script ought to surface this same content in <c>DisplayText</c>.
    /// </summary>
    [TestMethod]
    public async Task Search_fetches_results_via_js_fetch_and_populates_dom()
    {
        await using var server = await JsFixtureServer.StartAsync();

        var doc = new Document();
        var html = doc.AppendChild(doc.CreateElement("html"));
        var body = doc.CreateElement("body");
        html.AppendChild(body);
        var resultsHost = doc.CreateElement("div");
        resultsHost.SetAttribute("id", "results");
        body.AppendChild(resultsHost);

        var runtime = new JsRuntime();
        var errors = new List<string>();
        runtime.ConsoleSink = (level, message) =>
        {
            if (level == "error")
            {
                errors.Add(message);
            }
        };

        using var http = new StarlingHttpClient();
        WindowBinding.Install(runtime, doc, new WindowInstallOptions(
            DocumentUrl: server.BaseUrl + "/search?q=hello",
            HttpClient: http));

        // Mirror the JS that a "real" search-results bootstrap would run:
        // fetch a JSON array of result strings, build a <div> per result,
        // append them under #results. Uses `.forEach` rather than a `for`
        // loop because the bytecode compiler does not yet lower
        // `ForStatement` (wp:M3-03 / B3-2-followup-a); the gap is tracked
        // in the handoff doc and surfaces here as a real diagnostic if it
        // ever changes shape.
        Eval(runtime, $@"
            globalThis.__done = false;
            fetch('{server.BaseUrl}/api/results?q=hello')
              .then(function(r) {{ return r.json(); }})
              .then(function(data) {{
                  var host = document.getElementById('results');
                  data.forEach(function(item) {{
                      var d = document.createElement('div');
                      d.className = 'result';
                      d.textContent = item;
                      host.appendChild(d);
                  }});
                  globalThis.__done = true;
              }})
              .catch(function(e) {{
                  globalThis.__err = (e && e.message) ? e.message : String(e);
              }});
        ");

        await PumpUntil(runtime,
            () => runtime.GetGlobal("__done").AsBool || runtime.GetGlobal("__err").IsString,
            timeoutMs: 15000);

        if (runtime.GetGlobal("__err").IsString)
        {
            throw new AssertFailedException(
                "JS error during fetch-driven search-result population: " +
                runtime.GetGlobal("__err").AsString +
                (errors.Count > 0 ? "\nconsole.error: " + string.Join(" | ", errors) : ""));
        }

        runtime.GetGlobal("__done").AsBool.Should().BeTrue(
            "JS fetch chain should resolve and populate the DOM");

        // The engine's display-text extractor reads from doc.Body — same
        // function the real render path uses on the post-parse Document.
        var displayText = StarlingEngine.ExtractDisplayText(doc);
        displayText.Should().Contain("Result one");
        displayText.Should().Contain("Result two");
        displayText.Should().Contain("Result three");
    }

    // -----------------------------------------------------------------
    // Live-gated test (STARLING_ALLOW_NETWORK=1 to opt in)
    // -----------------------------------------------------------------

    [TestMethod]
    [TestCategory("NetworkLive")]
    public async Task Live_google_home_renders_offline_baseline_strings()
    {
        if (Environment.GetEnvironmentVariable("STARLING_ALLOW_NETWORK") != "1")
        {
            return;
        }

        var repoRoot = LocateRepoRoot();
        var homeFixture = Path.Combine(repoRoot, "testdata", "sites", "google-home.html");
        var searchFixture = Path.Combine(repoRoot, "testdata", "sites", "google-search-q-hello.html");

        var output = Path.Combine(Path.GetTempPath(), $"starling-b7-live-home-{Guid.NewGuid():N}.png");
        try
        {
            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                "https://www.google.com/",
                new RenderOptions(new Size(ViewportWidth, ViewportHeight), DefaultFontSize),
                output,
                CancellationToken.None);

            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");

            // Same minimum bar as the offline fixture (brand + top nav).
            // If google.com ever renders without these visible in the
            // static HTML, the test will fail loudly so we know to
            // re-vendor the fixture.
            var text = result.Value.DisplayText;
            text.Should().Contain("Google");
            // "Gmail" / "Images" appear in the static HTML even when JS is
            // gated — they're hard-coded nav links.
            (text.Contains("Gmail") || text.Contains("Images")).Should().BeTrue(
                "expected at least one of Gmail/Images in the top nav from the live HTML");

            if (Environment.GetEnvironmentVariable("STARLING_UPDATE_GOLDENS") == "1")
            {
                // Snapshot fresh HTML for the offline fixtures so future
                // offline runs track the upstream shape.
                using var client = new StarlingHttpClient();
                var url = global::Starling.Url.UrlParser.Parse("https://www.google.com/").Value;
                var resp = await client.GetAsync(url, CancellationToken.None);
                if (resp.IsOk)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(homeFixture)!);
                    await File.WriteAllBytesAsync(homeFixture, resp.Value.Body.ToArray(), CancellationToken.None);
                }

                var searchUrl = global::Starling.Url.UrlParser.Parse("https://www.google.com/search?q=hello").Value;
                var searchResp = await client.GetAsync(searchUrl, CancellationToken.None);
                if (searchResp.IsOk)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(searchFixture)!);
                    await File.WriteAllBytesAsync(searchFixture, searchResp.Value.Body.ToArray(), CancellationToken.None);
                }
            }
        }
        finally
        {
            TryDelete(output);
        }
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static (string HomeHtml, string SearchHtml) LoadFixtures()
    {
        var repoRoot = LocateRepoRoot();
        var home = Path.Combine(repoRoot, "testdata", "sites", "google-home.html");
        var search = Path.Combine(repoRoot, "testdata", "sites", "google-search-q-hello.html");
        File.Exists(home).Should().BeTrue($"fixture missing: {home}");
        File.Exists(search).Should().BeTrue($"fixture missing: {search}");
        return (File.ReadAllText(home), File.ReadAllText(search));
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

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch { /* best-effort cleanup */ }
    }

    private static JsValue Eval(JsRuntime runtime, string source)
    {
        var program = new JsParser(source).ParseProgram();
        var chunk = JsCompiler.Compile(program);
        return new JsVm(runtime).Run(chunk);
    }

    private static async Task PumpUntil(JsRuntime runtime, Func<bool> predicate, int timeoutMs = 10000)
    {
        // Same trick FetchTests.PumpUntil uses: re-enter the VM on an empty
        // chunk so microtasks drain under a valid ActiveVm.
        var drainChunk = JsCompiler.Compile(new JsParser("").ParseProgram());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && sw.ElapsedMilliseconds < timeoutMs)
        {
            new JsVm(runtime).Run(drainChunk);
            if (predicate())
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
        new JsVm(runtime).Run(drainChunk);
    }

    // -----------------------------------------------------------------
    // Local fixture HTTP server
    // -----------------------------------------------------------------

    /// <summary>
    /// Minimal HTTP fixture server that returns the home or search HTML
    /// based on the request path. 404s anything else (the engine will
    /// surface a network error for sub-resources, which we tolerate —
    /// the smoke test asserts on the parent page's DisplayText, not on
    /// every asset hitting 200).
    /// </summary>
    private sealed class FixtureServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _accept;
        private readonly string _homeHtml;
        private readonly string _searchHtml;

        public int Port { get; }

        private FixtureServer(TcpListener listener, string homeHtml, string searchHtml)
        {
            _listener = listener;
            _homeHtml = homeHtml;
            _searchHtml = searchHtml;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _accept = Task.Run(AcceptLoop);
        }

        public static Task<FixtureServer> StartAsync(string homeHtml, string searchResultsHtml)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new FixtureServer(listener, homeHtml, searchResultsHtml));
        }

        private async Task AcceptLoop()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                    catch (OperationCanceledException) { return; }
                    catch (ObjectDisposedException) { return; }
                    _ = Task.Run(() => ServeOneAsync(client));
                }
            }
            catch (IOException) { }
        }

        private async Task ServeOneAsync(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var buf = new byte[8192];
                    var pos = 0;
                    while (pos < buf.Length)
                    {
                        var n = await stream.ReadAsync(buf.AsMemory(pos), _cts.Token);
                        if (n == 0)
                        {
                            break;
                        }

                        pos += n;
                        if (ContainsCrLfCrLf(buf.AsSpan(0, pos)))
                        {
                            break;
                        }
                    }

                    var req = Encoding.ASCII.GetString(buf, 0, pos);
                    var path = ParseRequestPath(req) ?? "/";

                    // Strip query string for routing match (we keep the raw
                    // string only to decide which page to serve).
                    var routeKey = path;
                    var qIdx = routeKey.IndexOf('?', StringComparison.Ordinal);
                    if (qIdx >= 0)
                    {
                        routeKey = routeKey[..qIdx];
                    }

                    string body;
                    int status;
                    if (routeKey == "/" || routeKey == "/index.html")
                    {
                        body = _homeHtml;
                        status = body.Length > 0 ? 200 : 404;
                    }
                    else if (routeKey == "/search")
                    {
                        body = _searchHtml;
                        status = body.Length > 0 ? 200 : 404;
                    }
                    else
                    {
                        body = "";
                        status = 404;
                    }

                    var bodyBytes = Encoding.UTF8.GetBytes(body);
                    var head = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\n" +
                        "Content-Type: text/html; charset=utf-8\r\n" +
                        $"Content-Length: {bodyBytes.Length}\r\n" +
                        "Connection: close\r\n\r\n");
                    await stream.WriteAsync(head, _cts.Token);
                    if (bodyBytes.Length > 0)
                    {
                        await stream.WriteAsync(bodyBytes, _cts.Token);
                    }

                    await stream.FlushAsync(_cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }

        private static string? ParseRequestPath(string request)
        {
            var sp1 = request.IndexOf(' ', StringComparison.Ordinal);
            if (sp1 < 0)
            {
                return null;
            }

            var sp2 = request.IndexOf(' ', sp1 + 1);
            if (sp2 < 0)
            {
                return null;
            }

            return request.Substring(sp1 + 1, sp2 - sp1 - 1);
        }

        private static bool ContainsCrLfCrLf(ReadOnlySpan<byte> data)
        {
            for (var i = 0; i + 3 < data.Length; i++)
            {
                if (data[i] == 0x0D && data[i + 1] == 0x0A && data[i + 2] == 0x0D && data[i + 3] == 0x0A)
                {
                    return true;
                }
            }
            return false;
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            try { _accept.Wait(TimeSpan.FromSeconds(2)); } catch { }
            _cts.Dispose();
        }
    }

    /// <summary>
    /// HttpListener-backed server used by the JS fetch test. Serves
    /// <c>/api/results?q=hello</c> as a JSON array; everything else 404s.
    /// </summary>
    private sealed class JsFixtureServer : IAsyncDisposable
    {
        private readonly HttpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;
        public string BaseUrl { get; }

        private JsFixtureServer(HttpListener listener, string baseUrl)
        {
            _listener = listener;
            BaseUrl = baseUrl;
            _loop = Task.Run(LoopAsync);
        }

        public static Task<JsFixtureServer> StartAsync()
        {
            var port = FindFreePort();
            var prefix = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            listener.Start();
            return Task.FromResult(new JsFixtureServer(listener, prefix.TrimEnd('/')));
        }

        private async Task LoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { return; }
                _ = Task.Run(() =>
                {
                    try
                    {
                        var path = ctx.Request.Url!.AbsolutePath;
                        if (path == "/api/results")
                        {
                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/json";
                            using var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
                            w.Write("[\"Result one\",\"Result two\",\"Result three\"]");
                        }
                        else
                        {
                            ctx.Response.StatusCode = 404;
                        }
                    }
                    catch { /* swallow */ }
                    finally
                    {
                        try { ctx.Response.OutputStream.Close(); } catch { }
                        try { ctx.Response.Close(); } catch { }
                    }
                });
            }
        }

        private static int FindFreePort()
        {
            var sock = new TcpListener(IPAddress.Loopback, 0);
            sock.Start();
            var port = ((IPEndPoint)sock.LocalEndpoint).Port;
            sock.Stop();
            return port;
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { await _loop.ConfigureAwait(false); } catch { }
        }
    }
}
