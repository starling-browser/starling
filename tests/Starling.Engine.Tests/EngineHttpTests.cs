using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Starling.Net;
using Starling.Net.Http;
using StarlingUrlParser = global::Starling.Url.UrlParser;
namespace Starling.Engine.Tests;

[TestClass]
public class EngineHttpTests
{
    public static IEnumerable<object[]> SnapshotCases()
    {
        yield return [new HttpSnapshotCase("paragraph", "<body><p>Snapshot one.</p></body>", null, "Snapshot one.")];
        yield return [new HttpSnapshotCase("author style", "<head><style>.box{background-color:#008000;width:90px;height:35px}</style></head><body><div class=box>green</div></body>", new Rgba32(0, 128, 0), "green")];
        yield return [new HttpSnapshotCase("inline background", "<body><div style=\"background-color:#0000ff;width:80px;height:30px\">blue</div></body>", new Rgba32(0, 0, 255), "blue")];
        yield return [new HttpSnapshotCase("heading list", "<body><h1>Docs</h1><ul><li>Install</li><li>Run</li></ul></body>", null, "Docs Install Run")];
        yield return [new HttpSnapshotCase("centered text", "<body><p style=\"text-align:center;width:160px\">centered</p></body>", null, "centered")];
    }

    [TestMethod]
    public async Task RenderAsync_fetches_html_over_http_and_writes_png()
    {
        var bodyText = """
            <!doctype html>
            <head><style>.net { background-color: #008000; width: 100px; height: 40px; }</style></head>
            <body><div class="net">Hello over HTTP.</div></body>
            """;
        var bodyBytes = Encoding.UTF8.GetBytes(bodyText);
        using var server = await StubHttpServer.StartAsync(_ => Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Connection: close\r\n\r\n" +
            bodyText));

        var output = Path.Combine(Path.GetTempPath(), $"starling-{Guid.NewGuid():N}.png");
        try
        {
            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                $"http://localhost:{server.Port}/",
                new RenderOptions(new Size(400, 200), 28f),
                output,
                CancellationToken.None);

            result.IsOk.Should().BeTrue($"render failed: {(result.IsErr ? result.Error.Message : "")}");
            File.Exists(output).Should().BeTrue();
            new FileInfo(output).Length.Should().BeGreaterThan(100);
            result.Value.DisplayText.Should().Be("Hello over HTTP.");
            using var image = Image.Load<Rgba32>(output);
            CountExact(image, new Rgba32(0, 128, 0)).Should().BeGreaterThan(1_000);
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    [TestMethod]
    public async Task RenderAsync_follows_relative_redirect_and_renders_final_response()
    {
        using var server = await StubHttpServer.StartAsync(req =>
        {
            if (req.StartsWith("GET /start HTTP/1.1", StringComparison.Ordinal))
            {
                return Encoding.ASCII.GetBytes(
                    "HTTP/1.1 302 Found\r\n" +
                    "Location: /final\r\n" +
                    "Content-Length: 0\r\n" +
                    "Connection: close\r\n\r\n");
            }

            var body = Encoding.UTF8.GetBytes(
                "<head><style>.done{background-color:#008000;width:100px;height:40px}</style></head><body><div class=done>Redirected.</div></body>");
            return BuildResponse(body, "text/html; charset=utf-8");
        });

        var output = Path.Combine(Path.GetTempPath(), $"starling-{Guid.NewGuid():N}.png");
        try
        {
            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                $"http://localhost:{server.Port}/start",
                new RenderOptions(new Size(300, 160), 16f),
                output,
                CancellationToken.None);

            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            result.Value.DisplayText.Should().Be("Redirected.");
            using var image = Image.Load<Rgba32>(output);
            CountExact(image, new Rgba32(0, 128, 0)).Should().BeGreaterThan(1_000);
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    [TestMethod]
    public async Task RenderAsync_reports_redirect_loop_as_render_error()
    {
        using var server = await StubHttpServer.StartAsync(_ => Encoding.ASCII.GetBytes(
            "HTTP/1.1 302 Found\r\n" +
            "Location: /loop\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n"));

        var engine = new StarlingEngine();
        var output = Path.Combine(Path.GetTempPath(), $"starling-{Guid.NewGuid():N}.png");
        var result = await engine.RenderAsync(
            $"http://localhost:{server.Port}/loop",
            RenderOptions.Default,
            output,
            CancellationToken.None);

        result.IsErr.Should().BeTrue();
        result.Error.Message.Should().Contain("Too many redirects");
        File.Exists(output).Should().BeFalse();
    }

    [TestMethod]
    [DynamicData(nameof(SnapshotCases))]
    public async Task RenderAsync_renders_snapshot_http_fixtures(HttpSnapshotCase snapshot)
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html>" + snapshot.Html);
        using var server = await StubHttpServer.StartAsync(_ => BuildResponse(body, "text/html; charset=utf-8"));

        var output = Path.Combine(Path.GetTempPath(), $"starling-{Guid.NewGuid():N}.png");
        try
        {
            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                $"http://localhost:{server.Port}/{snapshot.Name}",
                new RenderOptions(new Size(320, 180), 16f),
                output,
                CancellationToken.None);

            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            result.Value.DisplayText.Should().Be(snapshot.ExpectedText);
            File.Exists(output).Should().BeTrue();
            new FileInfo(output).Length.Should().BeGreaterThan(100);

            if (snapshot.RequiredColor is { } color)
            {
                using var image = Image.Load<Rgba32>(output);
                CountExact(image, color).Should().BeGreaterThan(500);
            }
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    [TestMethod]
    public async Task RenderAsync_uses_html_meta_charset_when_http_header_omits_charset()
    {
        var body = Encoding.Latin1.GetBytes("""
            <!doctype html>
            <html><head><meta charset="iso-8859-1"></head><body><p>cafés</p></body></html>
            """);
        using var server = await StubHttpServer.StartAsync(_ => BuildResponse(body, "text/html"));

        var output = Path.Combine(Path.GetTempPath(), $"starling-{Guid.NewGuid():N}.png");
        try
        {
            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                $"http://localhost:{server.Port}/latin1",
                new RenderOptions(new Size(320, 180), 16f),
                output,
                CancellationToken.None);

            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            result.Value.DisplayText.Should().Be("cafés");
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    [TestMethod]
    public async Task BrowserSession_keeps_cookies_across_navigations()
    {
        var sawCookieOnSecondRequest = false;
        using var server = await StubHttpServer.StartAsync(req =>
        {
            if (req.StartsWith("GET /login HTTP/1.1", StringComparison.Ordinal))
            {
                var body = Encoding.UTF8.GetBytes("<body><p>logged in</p></body>");
                var head = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/html; charset=utf-8\r\n" +
                    "Set-Cookie: sid=abc; Path=/\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Connection: close\r\n\r\n");
                var combined = new byte[head.Length + body.Length];
                Buffer.BlockCopy(head, 0, combined, 0, head.Length);
                Buffer.BlockCopy(body, 0, combined, head.Length, body.Length);
                return combined;
            }

            sawCookieOnSecondRequest = req.Contains("Cookie: sid=abc\r\n", StringComparison.Ordinal);
            return BuildResponse(Encoding.UTF8.GetBytes("<body><p>account</p></body>"), "text/html; charset=utf-8");
        });

        var first = Path.Combine(Path.GetTempPath(), $"starling-{Guid.NewGuid():N}.png");
        var second = Path.Combine(Path.GetTempPath(), $"starling-{Guid.NewGuid():N}.png");
        try
        {
            using var session = new BrowserSession();
            var login = await session.NavigateAsync(
                $"http://localhost:{server.Port}/login",
                new RenderOptions(new Size(320, 180), 16f),
                first,
                CancellationToken.None);
            login.IsOk.Should().BeTrue(login.IsErr ? login.Error.Message : "");

            var account = await session.NavigateAsync(
                $"http://localhost:{server.Port}/account",
                new RenderOptions(new Size(320, 180), 16f),
                second,
                CancellationToken.None);
            account.IsOk.Should().BeTrue(account.IsErr ? account.Error.Message : "");

            session.Cookies.Count.Should().Be(1);
            sawCookieOnSecondRequest.Should().BeTrue();
            session.History.Entries.Should().HaveCount(2);
        }
        finally
        {
            if (File.Exists(first))
            {
                File.Delete(first);
            }

            if (File.Exists(second))
            {
                File.Delete(second);
            }
        }
    }

    [TestMethod]
    public async Task StarlingHttpClient_reuses_a_single_TCP_connection_across_two_sequential_GETs()
    {
        // wp:M2-07c — when the server keeps the connection alive after each
        // response, the client must pool the transport and reuse it for the
        // next request to the same origin. Asserting on AcceptCount==1 is the
        // observable signal that the pool actually fired.
        var body = Encoding.UTF8.GetBytes("ok");
        using var server = await StubHttpServer.StartAsync(
            _ => Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/plain\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: keep-alive\r\n\r\n" +
                "ok"),
            keepAlive: true);

        using var client = new StarlingHttpClient();
        var url = StarlingUrlParser.Parse($"http://localhost:{server.Port}/").Value;

        var first = await client.GetAsync(url, CancellationToken.None);
        first.IsOk.Should().BeTrue(first.IsErr ? first.Error.ToString() : "");
        first.Value.StatusCode.Should().Be(200);

        var second = await client.GetAsync(url, CancellationToken.None);
        second.IsOk.Should().BeTrue(second.IsErr ? second.Error.ToString() : "");
        second.Value.StatusCode.Should().Be(200);

        server.AcceptCount.Should().Be(1,
            "the pool must reuse the kept-alive TCP connection across sequential GETs");
        client.ConnectionPool.IdleCountFor(
            OriginKey.Create("http", "localhost", server.Port))
            .Should().Be(1, "after the second response the transport returns to the idle pool");
    }

    [TestMethod]
    public async Task StarlingHttpClient_redials_when_pooled_connection_was_closed_by_peer()
    {
        // The server keeps the first connection alive (so we pool it) but
        // closes it immediately after returning the response. When the next
        // request arrives the original connection is half-dead — the client
        // must transparently re-dial and the caller must see a clean 200.
        var body = Encoding.UTF8.GetBytes("ok");
        var responses = 0;
        using var server = await StubHttpServer.StartAsync(
            _ =>
            {
                Interlocked.Increment(ref responses);
                // First response says keep-alive (so we pool it), but the
                // server-side loop is single-shot so it'll close after.
                return Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\n" +
                    "Content-Type: text/plain\r\n" +
                    $"Content-Length: {body.Length}\r\n" +
                    "Connection: keep-alive\r\n\r\n" +
                    "ok");
            },
            keepAlive: false);

        using var client = new StarlingHttpClient();
        var url = StarlingUrlParser.Parse($"http://localhost:{server.Port}/").Value;

        var first = await client.GetAsync(url, CancellationToken.None);
        first.IsOk.Should().BeTrue();

        // Give the server a tick to notice the close on its end (helps make
        // the pooled-then-stale path deterministic on slow CI).
        await Task.Delay(20, CancellationToken.None);

        var second = await client.GetAsync(url, CancellationToken.None);
        second.IsOk.Should().BeTrue(second.IsErr ? second.Error.ToString() : "");

        server.AcceptCount.Should().Be(2,
            "the pooled socket is half-dead so the second send must re-dial");
        responses.Should().Be(2);
    }

    [TestMethod]
    public async Task StarlingHttpClient_does_not_reuse_when_server_closes_the_connection()
    {
        // Belt-and-braces: an explicit Connection: close response should drop
        // the socket and force a second dial on the next request.
        var body = Encoding.UTF8.GetBytes("ok");
        using var server = await StubHttpServer.StartAsync(
            _ => Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/plain\r\n" +
                $"Content-Length: {body.Length}\r\n" +
                "Connection: close\r\n\r\n" +
                "ok"));

        using var client = new StarlingHttpClient();
        var url = StarlingUrlParser.Parse($"http://localhost:{server.Port}/").Value;

        (await client.GetAsync(url, CancellationToken.None)).IsOk.Should().BeTrue();
        (await client.GetAsync(url, CancellationToken.None)).IsOk.Should().BeTrue();

        server.AcceptCount.Should().Be(2);
        client.ConnectionPool.IdleCount.Should().Be(0);
    }

    [TestMethod]
    public async Task RenderAsync_returns_render_error_on_http_failure_status()
    {
        using var server = await StubHttpServer.StartAsync(_ => Encoding.ASCII.GetBytes(
            "HTTP/1.1 404 Not Found\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n"));

        var engine = new StarlingEngine();
        var output = Path.Combine(Path.GetTempPath(), $"starling-{Guid.NewGuid():N}.png");
        var result = await engine.RenderAsync(
            $"http://localhost:{server.Port}/missing",
            RenderOptions.Default,
            output,
            CancellationToken.None);

        result.IsErr.Should().BeTrue();
        result.Error.Message.Should().Contain("404");
        File.Exists(output).Should().BeFalse();
    }

    [TestMethod]
    // UTF / ASCII / Latin-1 core.
    [DataRow("text/html; charset=utf-8", new byte[] { 0x68, 0x69 }, "hi")]
    [DataRow("text/html", new byte[] { 0xEF, 0xBB, 0xBF, 0x68, 0x69 }, "hi")]
    [DataRow("text/html; charset=\"utf-8\"", new byte[] { 0x68 }, "h")]
    [DataRow("text/html", new byte[] { 0x3C, 0x6D, 0x65, 0x74, 0x61, 0x20, 0x63, 0x68, 0x61, 0x72, 0x73, 0x65, 0x74, 0x3D, 0x69, 0x73, 0x6F, 0x2D, 0x38, 0x38, 0x35, 0x39, 0x2D, 0x31, 0x3E, 0x63, 0x61, 0x66, 0xE9 }, "<meta charset=iso-8859-1>café")]
    [DataRow(null, new byte[] { 0x61, 0x62 }, "ab")]
    // WHATWG encoding-label aliases that map onto BCL Encoding singletons.
    [DataRow("text/html; charset=ANSI_X3.4-1968", new byte[] { 0x68, 0x69 }, "hi")]
    [DataRow("text/html; charset=ISO_8859-1", new byte[] { 0xE9 }, "é")]
    [DataRow("text/html; charset=iso-ir-100", new byte[] { 0xE9 }, "é")]
    [DataRow("text/html; charset=unicode-1-1-utf-8", new byte[] { 0x68, 0x69 }, "hi")]
    // WHATWG "iso-8859-1" / "us-ascii" canonicalise to windows-1252, so
    // bytes 0x80..0x9F map to their windows-1252 glyphs (browser-compatible).
    [DataRow("text/html; charset=iso-8859-1", new byte[] { 0x92 }, "’")]
    [DataRow("text/html; charset=us-ascii", new byte[] { 0x80 }, "€")]
    [DataRow("text/html; charset=windows-1252", new byte[] { 0x80, 0x92, 0x97 }, "€’—")]
    [DataRow("text/html; charset=cp1252", new byte[] { 0x9C }, "œ")]
    // windows-1250..1258 family.
    [DataRow("text/html; charset=windows-1250", new byte[] { 0xA3 }, "Ł")]
    [DataRow("text/html; charset=cp1251", new byte[] { 0xC0 }, "А")]
    [DataRow("text/html; charset=windows-1253", new byte[] { 0xC1 }, "Α")]
    [DataRow("text/html; charset=windows-1254", new byte[] { 0xFD }, "ı")]
    [DataRow("text/html; charset=iso-8859-9", new byte[] { 0xFD }, "ı")]
    [DataRow("text/html; charset=windows-1255", new byte[] { 0xE0 }, "א")]
    [DataRow("text/html; charset=windows-1256", new byte[] { 0xC7 }, "ا")]
    [DataRow("text/html; charset=windows-1257", new byte[] { 0xC0 }, "Ą")]
    [DataRow("text/html; charset=windows-1258", new byte[] { 0xC0 }, "À")]
    // ISO-8859 family (2..16).
    [DataRow("text/html; charset=iso-8859-2", new byte[] { 0xA1 }, "Ą")]
    [DataRow("text/html; charset=latin2", new byte[] { 0xA3 }, "Ł")]
    [DataRow("text/html; charset=iso-8859-3", new byte[] { 0xA1 }, "Ħ")]
    [DataRow("text/html; charset=iso-8859-4", new byte[] { 0xA1 }, "Ą")]
    [DataRow("text/html; charset=iso-8859-5", new byte[] { 0xB0 }, "А")]
    [DataRow("text/html; charset=iso-8859-7", new byte[] { 0xC1 }, "Α")]
    [DataRow("text/html; charset=iso-8859-13", new byte[] { 0xC0 }, "Ą")]
    [DataRow("text/html; charset=iso-8859-15", new byte[] { 0xA4 }, "€")]
    // ISO-8859-10/-14/-16 are mapped by WHATWG but not shipped by the
    // .NET BCL CodePages provider; the engine falls back to UTF-8 for
    // those labels rather than mis-decoding. See WhatwgEncodingLabels.
    // Cyrillic + Mac.
    [DataRow("text/html; charset=koi8-r", new byte[] { 0xC1 }, "а")]
    [DataRow("text/html; charset=koi8-u", new byte[] { 0xA4 }, "є")]
    [DataRow("text/html; charset=x-mac-cyrillic", new byte[] { 0x80 }, "А")]
    [DataRow("text/html; charset=macintosh", new byte[] { 0xA9 }, "©")]
    // Thai / IBM866.
    [DataRow("text/html; charset=windows-874", new byte[] { 0xA1 }, "ก")]
    [DataRow("text/html; charset=tis-620", new byte[] { 0xA1 }, "ก")]
    [DataRow("text/html; charset=ibm866", new byte[] { 0x80 }, "А")]
    // CJK families.
    [DataRow("text/html; charset=shift_jis", new byte[] { 0x82, 0xA0 }, "あ")]
    [DataRow("text/html; charset=ms_kanji", new byte[] { 0x82, 0xA0 }, "あ")]
    [DataRow("text/html; charset=sjis", new byte[] { 0x82, 0xA0 }, "あ")]
    [DataRow("text/html; charset=gbk", new byte[] { 0xC4, 0xE3 }, "你")]
    [DataRow("text/html; charset=gb2312", new byte[] { 0xC4, 0xE3 }, "你")]
    [DataRow("text/html; charset=gb18030", new byte[] { 0xC4, 0xE3, 0xBA, 0xC3 }, "你好")]
    [DataRow("text/html; charset=big5", new byte[] { 0xA4, 0x40 }, "一")]
    [DataRow("text/html; charset=big5-hkscs", new byte[] { 0xA4, 0x40 }, "一")]
    [DataRow("text/html; charset=euc-kr", new byte[] { 0xBE, 0xC8 }, "안")]
    [DataRow("text/html; charset=korean", new byte[] { 0xBE, 0xC8 }, "안")]
    [DataRow("text/html; charset=euc-jp", new byte[] { 0xA4, 0xA2 }, "あ")]
    [DataRow("text/html; charset=iso-2022-jp", new byte[] { 0x1B, 0x24, 0x42, 0x24, 0x22, 0x1B, 0x28, 0x42 }, "あ")]
    public void ResolveEncoding_handles_common_inputs(string? contentType, byte[] body, string expectedDecoded)
    {
        var enc = StarlingEngine.ResolveEncoding(contentType, body);
        enc.GetString(body).TrimStart((char)0xFEFF).Should().Be(expectedDecoded);
    }

    [TestMethod]
    public void ResolveEncoding_falls_back_to_utf8_for_unknown_charset()
    {
        var enc = StarlingEngine.ResolveEncoding("text/html; charset=totally-fake", new byte[] { 0x61 });
        enc.WebName.Should().Be("utf-8");
    }

    private static byte[] BuildResponse(byte[] body, string contentType)
    {
        var head = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        var combined = new byte[head.Length + body.Length];
        Buffer.BlockCopy(head, 0, combined, 0, head.Length);
        Buffer.BlockCopy(body, 0, combined, head.Length, body.Length);
        return combined;
    }

    private static int CountExact(Image<Rgba32> image, Rgba32 color)
    {
        var count = 0;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                foreach (var px in row)
                {
                    if (px.Equals(color))
                    {
                        count++;
                    }
                }
            }
        });
        return count;
    }
}

public sealed record HttpSnapshotCase(
    string Name,
    string Html,
    Rgba32? RequiredColor,
    string ExpectedText)
{
    public override string ToString() => Name;
}

/// <summary>
/// Stub HTTP server used by engine integration tests. Serves one response per
/// request, by default closing after each. Pass <c>keepAlive: true</c> to
/// loop on the same TCP connection (used to exercise the wp:M2-07c keep-alive
/// path); the server then keeps reading further requests off the same socket
/// until the client closes or the response itself contains
/// <c>Connection: close</c>.
/// </summary>
internal sealed class StubHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;
    private int _acceptCount;

    public int Port { get; }

    /// <summary>Number of distinct TCP accepts this server has handled.</summary>
    public int AcceptCount => Volatile.Read(ref _acceptCount);

    private StubHttpServer(TcpListener listener, Func<string, byte[]> handler, bool keepAlive)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _accept = Task.Run(() => AcceptLoop(handler, keepAlive));
    }

    public static Task<StubHttpServer> StartAsync(Func<string, byte[]> handler)
        => StartAsync(handler, keepAlive: false);

    public static Task<StubHttpServer> StartAsync(Func<string, byte[]> handler, bool keepAlive)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new StubHttpServer(listener, handler, keepAlive));
    }

    private async Task AcceptLoop(Func<string, byte[]> handler, bool keepAlive)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                Interlocked.Increment(ref _acceptCount);
                _ = Task.Run(() => ServeConnectionAsync(client, handler, keepAlive));
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (ObjectDisposedException) { /* listener closed */ }
        catch (IOException) { /* peer disconnected */ }
    }

    private async Task ServeConnectionAsync(TcpClient client, Func<string, byte[]> handler, bool keepAlive)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
                while (!_cts.IsCancellationRequested)
                {
                    var buffer = new byte[8192];
                    var pos = 0;
                    var gotRequest = false;
                    while (pos < buffer.Length)
                    {
                        var n = await stream.ReadAsync(buffer.AsMemory(pos), _cts.Token);
                        if (n == 0)
                        {
                            break;
                        }

                        pos += n;
                        if (ContainsCrLfCrLf(buffer.AsSpan(0, pos)))
                        {
                            gotRequest = true;
                            break;
                        }
                    }
                    if (!gotRequest)
                    {
                        return;
                    }

                    var req = Encoding.ASCII.GetString(buffer, 0, pos);
                    var response = handler(req);
                    await stream.WriteAsync(response, _cts.Token);
                    await stream.FlushAsync(_cts.Token);

                    if (!keepAlive)
                    {
                        return;
                    }

                    // If the response itself signals close, stop reading further
                    // requests on this connection.
                    var responseText = Encoding.ASCII.GetString(response);
                    if (responseText.Contains("Connection: close", StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (ObjectDisposedException) { /* listener closed */ }
        catch (IOException) { /* peer disconnected */ }
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
