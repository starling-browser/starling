using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Starling.Engine.Tests;

/// <summary>
/// Local-loopback HTTP server that serves a directory tree captured by
/// <c>tools/snapshot-vendor/vendor-snapshot.sh</c>. Looks the request
/// path up under <see cref="RootDirectory"/>; returns 404 on miss. One
/// request per connection (Connection: close), single-threaded accept
/// loop — perfectly adequate for the snapshot render test, which fetches
/// a handful of assets.
/// </summary>
internal sealed class SnapshotHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;
    private readonly string _root;

    public int Port { get; }
    public string RootDirectory => _root;

    private SnapshotHttpServer(TcpListener listener, string root)
    {
        _listener = listener;
        _root = root;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _accept = Task.Run(AcceptLoop);
    }

    public static Task<SnapshotHttpServer> StartAsync(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException(rootDirectory);
        }

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return Task.FromResult(new SnapshotHttpServer(listener, rootDirectory));
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
                _ = Task.Run(() => ServeOneAsync(client));
            }
        }
        catch (ObjectDisposedException) { }
        catch (IOException) { }
    }

    private async Task ServeOneAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var stream = client.GetStream())
            {
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
                    if (ContainsCrLfCrLf(buffer.AsSpan(0, pos)))
                    {
                        break;
                    }
                }

                var request = Encoding.ASCII.GetString(buffer, 0, pos);
                var response = BuildResponse(request);
                await stream.WriteAsync(response, _cts.Token);
                await stream.FlushAsync(_cts.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
    }

    private byte[] BuildResponse(string request)
    {
        var path = ParseRequestPath(request);
        if (path is null)
        {
            return BuildStatus(400, "Bad Request");
        }

        // Map "/" -> "/index.html" and strip any query string.
        var qIdx = path.IndexOf('?', StringComparison.Ordinal);
        if (qIdx >= 0)
        {
            path = path[..qIdx];
        }

        if (path == "/" || path.Length == 0)
        {
            path = "/index.html";
        }

        // Canonicalise: forbid traversal, normalise separators.
        if (path.Contains("..", StringComparison.Ordinal))
        {
            return BuildStatus(400, "Bad Request");
        }

        var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var localPath = Path.Combine(_root, relative);
        if (!File.Exists(localPath))
        {
            return BuildStatus(404, "Not Found");
        }

        var body = File.ReadAllBytes(localPath);
        var contentType = GuessContentType(localPath);

        var header = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n");
        var combined = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, combined, 0, header.Length);
        Buffer.BlockCopy(body, 0, combined, header.Length, body.Length);
        return combined;
    }

    private static byte[] BuildStatus(int status, string reason)
        => Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Length: 0\r\n" +
            "Connection: close\r\n\r\n");

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

    private static string GuessContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".wasm" => "application/wasm",
            ".woff" => "font/woff",
            ".woff2" => "font/woff2",
            ".ttf" => "font/ttf",
            ".otf" => "font/otf",
            ".ico" => "image/x-icon",
            ".json" => "application/json",
            _ => "application/octet-stream",
        };
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
        try { _accept.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
