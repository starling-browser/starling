using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Starling.Net.Http;
namespace Starling.Net.Tests.Http;

/// <summary>
/// End-to-end tests that exercise <see cref="StarlingHttpClient"/>'s use of the
/// connection pool. Drives a real loopback HTTP/1.1 server that holds the TCP
/// connection open for keep-alive responses, then asserts the second request
/// reused the same socket (no new accept) or didn't (new accept).
/// </summary>
[TestClass]
public class ConnectionPoolIntegrationTests
{
    [TestMethod]
    public async Task Second_same_origin_request_reuses_pooled_connection()
    {
        using var server = await KeepAliveStubServer.StartAsync(req =>
            ResponseBuilder.KeepAlive("hi", "text/plain"));

        using var client = new StarlingHttpClient();
        var url = $"http://localhost:{server.Port}/";

        var r1 = await client.GetAsync(url, CancellationToken.None);
        var r2 = await client.GetAsync(url, CancellationToken.None);

        r1.IsOk.Should().BeTrue();
        r2.IsOk.Should().BeTrue();

        // Same TCP connection serviced both requests: the server should only
        // have accepted once.
        server.AcceptCount.Should().Be(1, "the second request must reuse the pooled connection");
        server.RequestCount.Should().Be(2);

        // After the second request completes and is released, the pool should
        // still hold one idle entry for this origin.
        var origin = OriginKey.Create("http", "localhost", server.Port);
        client.ConnectionPool.IdleCountFor(origin).Should().Be(1);
    }

    [TestMethod]
    public async Task Different_origin_does_not_reuse_pooled_connection()
    {
        using var serverA = await KeepAliveStubServer.StartAsync(_ =>
            ResponseBuilder.KeepAlive("a", "text/plain"));
        using var serverB = await KeepAliveStubServer.StartAsync(_ =>
            ResponseBuilder.KeepAlive("b", "text/plain"));

        using var client = new StarlingHttpClient();

        var r1 = await client.GetAsync(
            $"http://localhost:{serverA.Port}/", CancellationToken.None);
        var r2 = await client.GetAsync(
            $"http://localhost:{serverB.Port}/", CancellationToken.None);

        r1.IsOk.Should().BeTrue();
        r2.IsOk.Should().BeTrue();

        serverA.AcceptCount.Should().Be(1);
        serverB.AcceptCount.Should().Be(1, "different origin must not pull from the other origin's pool");

        var originA = OriginKey.Create("http", "localhost", serverA.Port);
        var originB = OriginKey.Create("http", "localhost", serverB.Port);
        client.ConnectionPool.IdleCountFor(originA).Should().Be(1);
        client.ConnectionPool.IdleCountFor(originB).Should().Be(1);
    }

    [TestMethod]
    public async Task Connection_close_response_is_not_pooled()
    {
        using var server = await KeepAliveStubServer.StartAsync(_ =>
            ResponseBuilder.Close("bye", "text/plain"));

        using var client = new StarlingHttpClient();
        var origin = OriginKey.Create("http", "localhost", server.Port);

        var r1 = await client.GetAsync(
            $"http://localhost:{server.Port}/", CancellationToken.None);
        r1.IsOk.Should().BeTrue();

        client.ConnectionPool.IdleCountFor(origin).Should().Be(0,
            "Connection: close responses are never returned to the pool");

        var r2 = await client.GetAsync(
            $"http://localhost:{server.Port}/", CancellationToken.None);
        r2.IsOk.Should().BeTrue();

        server.AcceptCount.Should().Be(2, "the second request must open a new connection");
    }

    [TestMethod]
    public async Task Idle_timeout_evicts_pooled_connection_on_next_acquire()
    {
        using var server = await KeepAliveStubServer.StartAsync(_ =>
            ResponseBuilder.KeepAlive("ok", "text/plain"));

        // Tiny idle timeout so we can age the entry out without waiting.
        var pool = new ConnectionPool(maxPerOrigin: 6, idleTimeout: TimeSpan.FromMilliseconds(50));
        using var client = new StarlingHttpClient(
            new StarlingHttpClientOptions { ConnectionPool = pool });
        var origin = OriginKey.Create("http", "localhost", server.Port);

        var r1 = await client.GetAsync(
            $"http://localhost:{server.Port}/", CancellationToken.None);
        r1.IsOk.Should().BeTrue();
        pool.IdleCountFor(origin).Should().Be(1);

        // Age the idle entry past the timeout, then trigger drain. After that
        // a subsequent request must open a fresh socket.
        await Task.Delay(150, CancellationToken.None);
        var drained = await pool.DrainExpiredAsync();
        drained.Should().Be(1, "the idle entry exceeded the configured idle timeout");
        pool.IdleCountFor(origin).Should().Be(0);

        var r2 = await client.GetAsync(
            $"http://localhost:{server.Port}/", CancellationToken.None);
        r2.IsOk.Should().BeTrue();
        server.AcceptCount.Should().Be(2, "expired connection must not be reused");
    }

    [TestMethod]
    public async Task Disposing_client_disposes_pooled_connections()
    {
        var server = await KeepAliveStubServer.StartAsync(_ =>
            ResponseBuilder.KeepAlive("ok", "text/plain"));

        var client = new StarlingHttpClient();
        var origin = OriginKey.Create("http", "localhost", server.Port);

        try
        {
            var r1 = await client.GetAsync(
                $"http://localhost:{server.Port}/", CancellationToken.None);
            r1.IsOk.Should().BeTrue();
            client.ConnectionPool.IdleCountFor(origin).Should().Be(1);
        }
        finally
        {
            client.Dispose();
            server.Dispose();
        }

        // After disposal the pool's idle queues should be empty.
        client.ConnectionPool.IdleCount.Should().Be(0);
    }

    [TestMethod]
    public async Task Concurrent_same_origin_requests_open_separate_connections_then_pool_both()
    {
        // Two requests fired off in parallel against an empty pool must each
        // open their own socket (no serialization). When both complete and the
        // server kept them alive, both should land in the pool.
        using var server = await KeepAliveStubServer.StartAsync(_ =>
            ResponseBuilder.KeepAlive("ok", "text/plain"));

        using var client = new StarlingHttpClient();
        var url = $"http://localhost:{server.Port}/";

        var t1 = client.GetAsync(url, CancellationToken.None);
        var t2 = client.GetAsync(url, CancellationToken.None);

        var results = await Task.WhenAll(t1, t2);
        results[0].IsOk.Should().BeTrue();
        results[1].IsOk.Should().BeTrue();

        server.AcceptCount.Should().Be(2,
            "parallel requests on an empty pool must open separate connections");

        var origin = OriginKey.Create("http", "localhost", server.Port);
        client.ConnectionPool.IdleCountFor(origin).Should().Be(2,
            "both kept-alive connections return to the pool when finished");
    }

    private static class ResponseBuilder
    {
        public static byte[] KeepAlive(string body, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {bytes.Length}\r\n" +
                "Connection: keep-alive\r\n\r\n");
            return Concat(head, bytes);
        }

        public static byte[] Close(string body, string contentType)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                $"Content-Type: {contentType}\r\n" +
                $"Content-Length: {bytes.Length}\r\n" +
                "Connection: close\r\n\r\n");
            return Concat(head, bytes);
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            var c = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, c, 0, a.Length);
            Buffer.BlockCopy(b, 0, c, a.Length, b.Length);
            return c;
        }
    }
}

/// <summary>
/// Multi-request HTTP/1.1 stub. Unlike <see cref="StubHttpServer"/> this one
/// keeps each accepted socket open after writing a response so it can service
/// further requests over the same TCP connection (true HTTP/1.1 keep-alive).
/// A connection terminates when the handler returns a response that asks for
/// <c>Connection: close</c>, the peer closes, or the server is disposed.
/// </summary>
internal sealed class KeepAliveStubServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;
    private readonly Func<string, byte[]> _handler;
    private int _accepts;
    private int _requests;

    public int Port { get; }
    public int AcceptCount => Volatile.Read(ref _accepts);
    public int RequestCount => Volatile.Read(ref _requests);

    private KeepAliveStubServer(TcpListener listener, Func<string, byte[]> handler)
    {
        _listener = listener;
        _handler = handler;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _accept = Task.Run(AcceptLoop);
    }

    public static Task<KeepAliveStubServer> StartAsync(Func<string, byte[]> handler)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new KeepAliveStubServer(listener, handler));
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                Interlocked.Increment(ref _accepts);
                _ = Task.Run(() => ServeAsync(client));
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            using var stream = client.GetStream();
            var buffer = new byte[8192];

            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var pos = 0;
                    while (pos < buffer.Length)
                    {
                        var n = await stream.ReadAsync(buffer.AsMemory(pos), _cts.Token);
                        if (n == 0)
                        {
                            // Peer closed between requests — normal end of life.
                            return;
                        }
                        pos += n;
                        if (ContainsCrLfCrLf(buffer.AsSpan(0, pos)))
                        {
                            break;
                        }
                    }
                    if (pos == 0)
                    {
                        return;
                    }

                    var req = Encoding.ASCII.GetString(buffer, 0, pos);
                    Interlocked.Increment(ref _requests);

                    var response = _handler(req);
                    await stream.WriteAsync(response, _cts.Token);
                    await stream.FlushAsync(_cts.Token);

                    if (ResponseClosesConnection(response))
                    {
                        return; // honor server-side close
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }
    }

    private static bool ContainsCrLfCrLf(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i + 3 < data.Length; i++)
        {
            if (data[i] == 0x0D && data[i + 1] == 0x0A &&
                data[i + 2] == 0x0D && data[i + 3] == 0x0A)
            {
                return true;
            }
        }
        return false;
    }

    private static bool ResponseClosesConnection(byte[] response)
    {
        var text = Encoding.ASCII.GetString(response);
        var headEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headEnd < 0)
        {
            return true;
        }

        var head = text[..headEnd];
        foreach (var line in head.Split("\r\n"))
        {
            if (!line.StartsWith("Connection:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return line.Contains("close", StringComparison.OrdinalIgnoreCase);
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
