using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Starling.Engine.Tests;

/// <summary>One canned HTTP response: its content type, body, and an artificial
/// per-request delay used to widen the window in which concurrent fetches
/// overlap.</summary>
internal readonly record struct Route(string ContentType, string Body, int DelayMs);

/// <summary>
/// Keep-alive loopback HTTP/1.1 server that delays each response and tracks the
/// peak number of simultaneously in-flight requests, so a test can prove the
/// client fetched things in parallel. Shared by the parallel script / resource
/// fetch tests.
/// </summary>
internal sealed class ConcurrencyTrackingServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<string, Route> _routes;
    private int _inFlight;
    private int _maxConcurrency;
    private int _requestsServed;

    public int Port { get; }
    public int MaxConcurrency => Volatile.Read(ref _maxConcurrency);
    public int RequestsServed => Volatile.Read(ref _requestsServed);

    public ConcurrencyTrackingServer(Dictionary<string, Route> routes)
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
                    return;
                }

                Interlocked.Increment(ref _requestsServed);
                var inFlight = Interlocked.Increment(ref _inFlight);
                UpdateMax(inFlight);
                try
                {
                    var route = _routes.TryGetValue(path, out var r)
                        ? r
                        : new Route("text/plain", "not found", 0);
                    if (route.DelayMs > 0)
                    {
                        await Task.Delay(route.DelayMs, _cts.Token).ConfigureAwait(false);
                    }

                    var status = _routes.ContainsKey(path) ? "200 OK" : "404 Not Found";
                    var bodyBytes = Encoding.UTF8.GetBytes(route.Body);
                    var header =
                        $"HTTP/1.1 {status}\r\n" +
                        $"Content-Type: {route.ContentType}\r\n" +
                        $"Content-Length: {bodyBytes.Length}\r\n" +
                        "Connection: keep-alive\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _cts.Token).ConfigureAwait(false);
                    await stream.WriteAsync(bodyBytes, _cts.Token).ConfigureAwait(false);
                    await stream.FlushAsync(_cts.Token).ConfigureAwait(false);
                }
                catch { return; }
                finally { Interlocked.Decrement(ref _inFlight); }
            }
        }
    }

    private void UpdateMax(int observed)
    {
        int prev;
        while ((prev = Volatile.Read(ref _maxConcurrency)) < observed
               && Interlocked.CompareExchange(ref _maxConcurrency, observed, prev) != prev)
        {
            // retry
        }
    }

    private static async Task<string?> ReadRequestPathAsync(NetworkStream stream, CancellationToken ct)
    {
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
