using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using StarlingUrlParser = global::Starling.Url.UrlParser;

namespace Starling.Net.Tests.Http;

[TestClass]
public class StarlingHttpClientTests
{
    [TestMethod]
    public async Task End_to_end_GET_against_a_local_HTTP_server_returns_200()
    {
        var body = Encoding.UTF8.GetBytes("<!doctype html><html><body>hello starling</body></html>");
        using var server = await StubHttpServer.StartAsync(req =>
        {
            req.Should().StartWith("GET /test?x=1 HTTP/1.1\r\n");
            req.Should().Contain("Host: localhost:");
            req.Should().Contain("Accept-Encoding: gzip, br, deflate");
            return BuildResponse(body, "text/html; charset=utf-8");
        });

        using var client = new StarlingHttpClient();
        var url = StarlingUrlParser.Parse($"http://localhost:{server.Port}/test?x=1").Value;
        var result = await client.GetAsync(url, CancellationToken.None);

        result.IsOk.Should().BeTrue($"got {(result.IsOk ? "Ok" : result.Error.ToString())}");
        var resp = result.Value;
        resp.StatusCode.Should().Be(200);
        resp.Headers.GetFirst("Content-Type").Should().Be("text/html; charset=utf-8");
        Encoding.UTF8.GetString(resp.Body.Span).Should().Be(Encoding.UTF8.GetString(body));
    }

    [TestMethod]
    public async Task End_to_end_GET_decodes_gzip_response()
    {
        var payload = Encoding.UTF8.GetBytes("<!doctype html><body>compressed body content</body>");
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        {
            gz.Write(payload);
        }

        var compressed = ms.ToArray();

        using var server = await StubHttpServer.StartAsync(_ =>
        {
            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/html\r\n" +
                "Content-Encoding: gzip\r\n" +
                $"Content-Length: {compressed.Length}\r\n" +
                "Connection: close\r\n\r\n");
            var combined = new byte[head.Length + compressed.Length];
            Buffer.BlockCopy(head, 0, combined, 0, head.Length);
            Buffer.BlockCopy(compressed, 0, combined, head.Length, compressed.Length);
            return combined;
        });

        using var client = new StarlingHttpClient();
        var result = await client.GetAsync(
            $"http://localhost:{server.Port}/", CancellationToken.None);

        result.IsOk.Should().BeTrue();
        result.Value.Body.ToArray().Should().Equal(payload);
    }

    [TestMethod]
    public async Task End_to_end_GET_decodes_chunked_response()
    {
        using var server = await StubHttpServer.StartAsync(_ =>
        {
            var raw =
                "HTTP/1.1 200 OK\r\n" +
                "Content-Type: text/plain\r\n" +
                "Transfer-Encoding: chunked\r\n" +
                "Connection: close\r\n\r\n" +
                "5\r\nhello\r\n" +
                "6\r\n world\r\n" +
                "1\r\n!\r\n" +
                "0\r\n\r\n";
            return Encoding.ASCII.GetBytes(raw);
        });

        using var client = new StarlingHttpClient();
        var result = await client.GetAsync(
            $"http://localhost:{server.Port}/", CancellationToken.None);
        result.IsOk.Should().BeTrue();
        Encoding.UTF8.GetString(result.Value.Body.Span).Should().Be("hello world!");
    }

    [TestMethod]
    public async Task Returns_UnsupportedScheme_for_file_url()
    {
        using var client = new StarlingHttpClient();
        var url = StarlingUrlParser.Parse("file:///tmp/foo.html").Value;
        var result = await client.GetAsync(url, CancellationToken.None);
        result.IsErr.Should().BeTrue();
        result.Error.Should().Be(NetworkError.UnsupportedScheme);
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
}

/// <summary>
/// Single-shot HTTP/1.1 server. Accepts one connection, reads the request
/// header block, hands the request text to <c>handler</c>, writes the
/// returned bytes back, then closes.
/// </summary>
internal sealed class StubHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;

    public int Port { get; }

    private StubHttpServer(TcpListener listener, Func<string, byte[]> handler)
    {
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _accept = Task.Run(() => AcceptLoop(handler));
    }

    public static Task<StubHttpServer> StartAsync(Func<string, byte[]> handler)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new StubHttpServer(listener, handler));
    }

    private async Task AcceptLoop(Func<string, byte[]> handler)
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                using var stream = client.GetStream();

                var buffer = new byte[8192];
                var pos = 0;
                while (pos < buffer.Length)
                {
                    var n = await stream.ReadAsync(buffer.AsMemory(pos), _cts.Token);
                    if (n == 0)
                    {
                        break;
                    }

                    pos += n;
                    var slice = buffer.AsSpan(0, pos);
                    if (ContainsCrLfCrLf(slice))
                    {
                        break;
                    }
                }

                var req = Encoding.ASCII.GetString(buffer, 0, pos);
                var response = handler(req);
                await stream.WriteAsync(response, _cts.Token);
                await stream.FlushAsync(_cts.Token);
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
