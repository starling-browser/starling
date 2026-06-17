using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using AwesomeAssertions;
using Starling.Net.Dns;
using Starling.Net.Tcp;
namespace Starling.Net.Tests.Tcp;

[TestClass]
public class TcpDialerTests
{
    [TestMethod]
    public void TcpEndpoint_validates_port_range()
    {
        var act1 = () => TcpEndpoint.For("a", 0);
        var act2 = () => TcpEndpoint.For("a", 65536);
        act1.Should().Throw<ArgumentOutOfRangeException>();
        act2.Should().Throw<ArgumentOutOfRangeException>();
    }

    [TestMethod]
    public void TcpEndpoint_rejects_empty_hostname()
    {
        var act = () => TcpEndpoint.For("  ", 80);
        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public async Task Direct_dial_round_trips_bytes_through_a_local_listener()
    {
        using var listener = new EchoListener();
        var resolver = new DnsResolver(new NoopTransport());
        var dialer = new TcpDialer(resolver) { ConnectTimeout = TimeSpan.FromSeconds(2) };

        var ct = CancellationToken.None;
        var dialResult = await dialer.DialDirectAsync(
            listener.LocalEndpoint,
            TcpEndpoint.For("localhost", listener.LocalEndpoint.Port),
            ct);
        dialResult.IsOk.Should().BeTrue();

        await using var conn = dialResult.Value;
        conn.IsOpen.Should().BeTrue();
        conn.Endpoint.Hostname.Should().Be("localhost");

        var payload = Encoding.UTF8.GetBytes("hello, starling tcp\n");
        await conn.WriteAsync(payload, ct);

        var buf = new byte[payload.Length];
        var total = 0;
        while (total < buf.Length)
        {
            var n = await conn.ReadAsync(buf.AsMemory(total), ct);
            if (n == 0)
            {
                break;
            }

            total += n;
        }
        total.Should().Be(payload.Length);
        Encoding.UTF8.GetString(buf, 0, total).Should().Be("hello, starling tcp\n");
    }

    [TestMethod]
    public async Task Connect_to_unbound_port_returns_ConnectFailed()
    {
        // 127.0.0.1 with a very-likely-unbound port; if a system listener
        // happens to be there we tolerate the spurious pass by checking only
        // that the result type is well-formed.
        var resolver = new DnsResolver(new NoopTransport());
        var dialer = new TcpDialer(resolver) { ConnectTimeout = TimeSpan.FromMilliseconds(500) };
        var ct = CancellationToken.None;
        var r = await dialer.DialDirectAsync(
            new IPEndPoint(IPAddress.Loopback, 1),
            TcpEndpoint.For("localhost", 1), ct);
        r.IsErr.Should().BeTrue();
        r.Error.Should().Be(TcpError.ConnectFailed);
    }

    [TestMethod]
    public async Task Connection_disposes_cleanly()
    {
        using var listener = new EchoListener();
        var dialer = new TcpDialer(new DnsResolver(new NoopTransport()));
        var ct = CancellationToken.None;
        var dial = await dialer.DialDirectAsync(
            listener.LocalEndpoint, TcpEndpoint.For("localhost", listener.LocalEndpoint.Port), ct);

        var conn = dial.Value;
        await conn.ShutdownAsync(ct);
        conn.IsOpen.Should().BeFalse();
        await conn.DisposeAsync();
    }

    [TestMethod]
    public async Task DisposeAsync_completes_synchronously_when_called_from_synchronization_context()
    {
        using var listener = new EchoListener();
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(listener.LocalEndpoint, CancellationToken.None);

        var conn = new SocketTcpConnection(
            client,
            TcpEndpoint.For("localhost", listener.LocalEndpoint.Port));
        var originalContext = SynchronizationContext.Current;
        var context = new QueuingSynchronizationContext();
        Task disposeTask;
        bool completedSynchronously;

        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            disposeTask = conn.DisposeAsync().AsTask();
            completedSynchronously = disposeTask.IsCompletedSuccessfully;
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(originalContext);
        }

        if (!completedSynchronously)
        {
            await context.DrainAsync();
            await disposeTask.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        }

        completedSynchronously.Should().BeTrue(
            "synchronous dispose paths must not post continuations back to UI synchronization contexts");
    }

    [TestMethod]
    public async Task Read_returns_zero_when_peer_closes()
    {
        using var listener = new EchoListener(closeAfterFirstByte: true);
        var dialer = new TcpDialer(new DnsResolver(new NoopTransport()));
        var ct = CancellationToken.None;
        var dial = await dialer.DialDirectAsync(
            listener.LocalEndpoint, TcpEndpoint.For("localhost", listener.LocalEndpoint.Port), ct);
        await using var conn = dial.Value;

        await conn.WriteAsync(new byte[] { (byte)'x' }, ct);
        var buf = new byte[16];
        var first = await conn.ReadAsync(buf.AsMemory(0, 1), ct);
        first.Should().Be(1);

        // Listener closes after echoing one byte → next read should return 0.
        var next = await conn.ReadAsync(buf, ct);
        next.Should().Be(0);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Minimal echo TCP server bound to an ephemeral loopback port.</summary>
    private sealed class EchoListener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public IPEndPoint LocalEndpoint { get; }

        public EchoListener(bool closeAfterFirstByte = false)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            LocalEndpoint = (IPEndPoint)_listener.LocalEndpoint;
            _ = Task.Run(() => AcceptLoop(closeAfterFirstByte));
        }

        private async Task AcceptLoop(bool closeAfterFirstByte)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => Echo(client, closeAfterFirstByte));
                }
            }
            catch { /* shutting down */ }
        }

        private static async Task Echo(TcpClient client, bool closeAfterFirstByte)
        {
            try
            {
                using (client)
                using (var s = client.GetStream())
                {
                    var buf = new byte[4096];
                    while (true)
                    {
                        var n = await s.ReadAsync(buf);
                        if (n == 0)
                        {
                            break;
                        }

                        await s.WriteAsync(buf.AsMemory(0, n));
                        if (closeAfterFirstByte)
                        {
                            break;
                        }
                    }
                }
            }
            catch { /* drop */ }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _listener.Stop();
            _cts.Dispose();
        }
    }

    private sealed class NoopTransport : IDnsTransport
    {
        public Task<byte[]> SendAsync(byte[] queryPacket, CancellationToken ct)
            => throw new InvalidOperationException("DNS not exercised in this test");
    }

    private sealed class QueuingSynchronizationContext : SynchronizationContext
    {
        private readonly Channel<(SendOrPostCallback Callback, object? State)> _callbacks =
            Channel.CreateUnbounded<(SendOrPostCallback, object?)>();

        public override void Post(SendOrPostCallback d, object? state)
            => _callbacks.Writer.TryWrite((d, state));

        public async Task DrainAsync()
        {
            _callbacks.Writer.Complete();
            await foreach (var (callback, state) in _callbacks.Reader.ReadAllAsync())
            {
                callback(state);
            }
        }
    }
}
