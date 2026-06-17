using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using SixLabors.ImageSharp;

namespace Starling.Engine.Tests;

/// <summary>
/// A single page load shares one HTTP connection pool across the document fetch
/// and every resource fetch, so multiple same-origin requests reuse one
/// keep-alive transport instead of re-doing DNS+TCP+TLS each time. Before this,
/// each fetcher (and the HTML fetch) built its own client/pool, so every
/// request to the same origin opened a fresh connection.
/// </summary>
[TestClass]
public sealed class EngineConnectionReuseTests
{
    [TestMethod]
    public async Task Same_origin_document_and_resources_reuse_one_connection()
    {
        // index.html pulls a same-origin stylesheet and script: 3 GETs to the
        // same origin (document, css, js). The document is fetched first and
        // its connection returns to the pool, then the stylesheet and script
        // fetches run in parallel (Engine.cs intentionally overlaps them to
        // shorten the critical path). With one shared keep-alive pool at
        // least one of those parallel fetches reuses the document's
        // connection — so fewer connections are accepted than requests are
        // served. Before the shared-pool fix every fetcher built its own pool
        // and accepted = served = 3.
        var routes = new Dictionary<string, (string ContentType, string Body)>
        {
            ["/index.html"] = ("text/html",
                "<!doctype html><html><head>" +
                "<link rel=\"stylesheet\" href=\"/style.css\">" +
                "</head><body><p>hi</p><script src=\"/app.js\"></script></body></html>"),
            ["/style.css"] = ("text/css", "p { color: red; }"),
            ["/app.js"] = ("text/javascript", "var x = 1;"),
        };

        using var server = new KeepAliveHttpServer(routes);
        var tempPng = Path.Combine(Path.GetTempPath(), $"starling-reuse-{Guid.NewGuid():N}.png");
        try
        {
            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                $"http://127.0.0.1:{server.Port}/index.html",
                new RenderOptions(new Size(800, 600), 16f),
                tempPng,
                CancellationToken.None);

            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            server.RequestsServed.Should().Be(3, "document + stylesheet + script were fetched");
            server.ConnectionsAccepted.Should().BeLessThan(server.RequestsServed,
                "the shared keep-alive pool must let at least one same-origin " +
                "request reuse an existing connection instead of dialing fresh");
        }
        finally
        {
            try { if (File.Exists(tempPng)) { File.Delete(tempPng); } } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Minimal HTTP/1.1 loopback server that keeps connections alive (serves
    /// many requests per socket) and counts how many TCP connections it
    /// accepted, so a test can prove client-side connection reuse.
    /// </summary>
    private sealed class KeepAliveHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Dictionary<string, (string ContentType, string Body)> _routes;
        private int _connectionsAccepted;
        private int _requestsServed;

        public int Port { get; }
        public int ConnectionsAccepted => Volatile.Read(ref _connectionsAccepted);
        public int RequestsServed => Volatile.Read(ref _requestsServed);

        public KeepAliveHttpServer(Dictionary<string, (string ContentType, string Body)> routes)
        {
            _routes = routes;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync();
        }

        private async Task AcceptLoopAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
                catch { break; }
                Interlocked.Increment(ref _connectionsAccepted);
                _ = HandleConnectionAsync(client);
            }
        }

        private async Task HandleConnectionAsync(TcpClient client)
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                while (!_cts.IsCancellationRequested)
                {
                    var path = await ReadRequestPathAsync(stream, _cts.Token).ConfigureAwait(false);
                    if (path is null)
                    {
                        return; // peer closed the connection
                    }

                    Interlocked.Increment(ref _requestsServed);
                    var (status, contentType, body) = _routes.TryGetValue(path, out var route)
                        ? ("200 OK", route.ContentType, route.Body)
                        : ("404 Not Found", "text/plain", "not found");

                    var bodyBytes = Encoding.UTF8.GetBytes(body);
                    // Keep-alive + Content-Length so the client's pool can safely
                    // reuse the transport (definite framing, no Connection: close).
                    var header =
                        $"HTTP/1.1 {status}\r\n" +
                        $"Content-Type: {contentType}\r\n" +
                        $"Content-Length: {bodyBytes.Length}\r\n" +
                        "Connection: keep-alive\r\n\r\n";
                    try
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token).ConfigureAwait(false);
                        await stream.WriteAsync(bodyBytes, _cts.Token).ConfigureAwait(false);
                        await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                    }
                    catch { return; }
                }
            }
        }

        private static async Task<string?> ReadRequestPathAsync(NetworkStream stream, CancellationToken ct)
        {
            // GET requests have no body; read until the header terminator.
            var sb = new StringBuilder();
            var buf = new byte[1024];
            while (!sb.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                int n;
                try { n = await stream.ReadAsync(buf, ct).ConfigureAwait(false); }
                catch { return null; }
                if (n == 0)
                {
                    return null;
                }

                sb.Append(Encoding.ASCII.GetString(buf, 0, n));
            }
            var firstLine = sb.ToString().Split("\r\n", StringSplitOptions.None)[0];
            var parts = firstLine.Split(' ');
            return parts.Length >= 2 ? parts[1] : "/";
        }

        public void Dispose()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { /* best-effort */ }
            _cts.Dispose();
        }
    }
}
