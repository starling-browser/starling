using System.Net;
using System.Net.Sockets;
using System.Text;
using AwesomeAssertions;
using Starling.Net.Http;
using Starling.Net.Http.H2;
using Starling.Net.Http.H2.Hpack;
using UrlParser = global::Starling.Url.UrlParser;

namespace Starling.Net.Tests.Http.H2;

/// <summary>
/// End-to-end <see cref="H2Connection"/> tests over a loopback socket driven by
/// a scripted minimal HTTP/2 server. Exercises the full path: preface, SETTINGS
/// exchange, HPACK-encoded request, and HEADERS+DATA response assembly,
/// including concurrent multiplexed streams.
/// </summary>
[TestClass]
public class H2ConnectionTests
{
    private static readonly OriginKey Origin = OriginKey.Create("https", "example.com", 443);

    [TestMethod]
    public async Task Single_get_returns_status_and_body()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (clientStream, serverStream) = await ConnectLoopbackAsync();

        var serverTask = RunServerAsync(serverStream, cts.Token);
        await using var conn = await H2Connection.StartAsync(
            new FakeH2Transport(clientStream, Origin), Origin, null, null, cts.Token);

        var url = UrlParser.Parse("https://example.com/hello").Value;
        var result = await conn.SendAsync(HttpRequest.Get(url), url, cts.Token);

        result.IsOk.Should().BeTrue(result.IsOk ? "" : result.Error.ToString());
        result.Value.StatusCode.Should().Be(200);
        result.Value.HttpVersion.Should().Be("HTTP/2");
        result.Value.Headers.GetFirst("content-type").Should().Be("text/plain");
        Encoding.ASCII.GetString(result.Value.Body.Span).Should().Be("path=/hello");

        await serverStream.DisposeAsync();
        await serverTask;
    }

    [TestMethod]
    public async Task Concurrent_streams_are_multiplexed_and_demultiplexed()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var (clientStream, serverStream) = await ConnectLoopbackAsync();

        var serverTask = RunServerAsync(serverStream, cts.Token);
        await using var conn = await H2Connection.StartAsync(
            new FakeH2Transport(clientStream, Origin), Origin, null, null, cts.Token);

        // Fire several requests without awaiting; the connection must keep each
        // response matched to its own stream.
        var tasks = Enumerable.Range(0, 8).Select(i =>
        {
            var url = UrlParser.Parse($"https://example.com/r{i}").Value;
            return conn.SendAsync(HttpRequest.Get(url), url, cts.Token);
        }).ToArray();

        var results = await Task.WhenAll(tasks);

        for (var i = 0; i < results.Length; i++)
        {
            results[i].IsOk.Should().BeTrue();
            results[i].Value.StatusCode.Should().Be(200);
            Encoding.ASCII.GetString(results[i].Value.Body.Span).Should().Be($"path=/r{i}");
        }

        await serverStream.DisposeAsync();
        await serverTask;
    }

    [TestMethod]
    public async Task Server_goaway_fails_outstanding_requests_retryably()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var (clientStream, serverStream) = await ConnectLoopbackAsync();

        // Server that reads the preface, sends SETTINGS, then GOAWAY(lastId=0).
        var serverTask = Task.Run(async () =>
        {
            await ReadPrefaceAsync(serverStream, cts.Token);
            await WriteEmptySettingsAsync(serverStream, cts.Token);
            var writer = new H2FrameWriter(serverStream);
            // Drain a couple of client frames, then refuse everything.
            var reader = new H2FrameReader(serverStream, H2Protocol.DefaultMaxFrameSize);
            await reader.ReadFrameAsync(cts.Token);
            await writer.WriteGoAwayAsync(0, H2ErrorCode.NoError, cts.Token);
        }, cts.Token);

        await using var conn = await H2Connection.StartAsync(
            new FakeH2Transport(clientStream, Origin), Origin, null, null, cts.Token);

        var url = UrlParser.Parse("https://example.com/x").Value;
        var result = await conn.SendAsync(HttpRequest.Get(url), url, cts.Token);

        result.IsErr.Should().BeTrue();
        result.Error.Should().Be(NetworkError.TransportFailure); // retryable
        conn.IsUsable.Should().BeFalse();

        await serverStream.DisposeAsync();
        await serverTask;
    }

    // ---- Loopback plumbing -------------------------------------------------

    private static async Task<(NetworkStream Client, NetworkStream Server)> ConnectLoopbackAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var client = new TcpClient();
            var acceptTask = listener.AcceptTcpClientAsync();
            await client.ConnectAsync(IPAddress.Loopback, port);
            var server = await acceptTask;
            client.NoDelay = true;
            server.NoDelay = true;
            return (client.GetStream(), server.GetStream());
        }
        finally
        {
            listener.Stop();
        }
    }

    // ---- Scripted server ---------------------------------------------------

    /// <summary>
    /// Minimal HTTP/2 server: completes the handshake, then for each request
    /// HEADERS replies 200 with a "path=&lt;:path&gt;" body so callers can verify
    /// stream demultiplexing.
    /// </summary>
    private static async Task RunServerAsync(NetworkStream serverStream, CancellationToken ct)
    {
        try
        {
            await ReadPrefaceAsync(serverStream, ct);
            await WriteEmptySettingsAsync(serverStream, ct);

            var reader = new H2FrameReader(serverStream, H2Protocol.DefaultMaxFrameSize);
            var writer = new H2FrameWriter(serverStream);
            var decoder = new HpackDecoder(H2Protocol.DefaultHeaderTableSize);
            var encoder = new HpackEncoder();

            while (true)
            {
                var maybe = await reader.ReadFrameAsync(ct).ConfigureAwait(false);
                if (maybe is not { } frame)
                {
                    break;
                }

                switch (frame.Type)
                {
                    case H2FrameType.Settings when !frame.HasFlag(H2Flags.Ack):
                        await writer.WriteSettingsAckAsync(ct);
                        break;

                    case H2FrameType.Headers:
                        decoder.TryDecode(frame.Payload, out var fields).Should().BeTrue();
                        var path = fields.First(f => f.Name == ":path").Value;
                        var body = Encoding.ASCII.GetBytes($"path={path}");
                        var block = encoder.Encode([(":status", "200"), ("content-type", "text/plain")]);
                        await writer.WriteHeadersAsync(
                            frame.StreamId, block, endStream: false, H2Protocol.DefaultMaxFrameSize, ct);
                        await writer.WriteDataAsync(frame.StreamId, body, endStream: true, ct);
                        break;

                    default:
                        break; // ignore WINDOW_UPDATE / PING / etc.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Client closed the connection — expected at end of test.
        }
    }

    private static async Task ReadPrefaceAsync(Stream stream, CancellationToken ct)
    {
        var preface = new byte[24];
        await stream.ReadExactlyAsync(preface, ct);
    }

    private static Task WriteEmptySettingsAsync(Stream stream, CancellationToken ct)
    {
        // SETTINGS frame, length 0, flags 0, stream 0.
        var frame = new byte[] { 0, 0, 0, (byte)H2FrameType.Settings, 0, 0, 0, 0, 0 };
        return stream.WriteAsync(frame, ct).AsTask();
    }

    private sealed class FakeH2Transport(Stream stream, OriginKey origin) : IHttpTransport
    {
        public OriginKey Origin { get; } = origin;
        public Stream Stream { get; } = stream;
        public string? Alpn => "h2";
        public Starling.Net.Tls.CertificateSummary? PeerCertificate => null;
        public bool IsOpen { get; private set; } = true;

        public ValueTask DisposeAsync()
        {
            IsOpen = false;
            return Stream.DisposeAsync();
        }
    }
}
