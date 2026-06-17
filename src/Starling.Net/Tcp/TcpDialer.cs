using System.Net;
using System.Net.Sockets;
using Starling.Common;
using Starling.Net.Dns;

namespace Starling.Net.Tcp;

/// <summary>
/// Opens TCP connections by hostname, going through the Starling DNS
/// resolver. Returns an <see cref="ITcpConnection"/> on success.
/// </summary>
/// <remarks>
/// Tries each resolved address in order until one connects or all fail
/// (Happy Eyeballs sequencing is not implemented yet). The connect attempt
/// itself is bounded by <see cref="ConnectTimeout"/>. Cancellation tokens
/// passed in by the caller compose with that timeout.
/// </remarks>
public sealed class TcpDialer
{
    private readonly DnsResolver _dns;

    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public TcpDialer(DnsResolver dnsResolver)
    {
        _dns = dnsResolver ?? throw new ArgumentNullException(nameof(dnsResolver));
    }

    public async Task<Result<ITcpConnection, TcpError>> DialAsync(
        TcpEndpoint endpoint, CancellationToken ct = default)
    {
        var dnsResult = await _dns.ResolveAsync(endpoint.Hostname, ct).ConfigureAwait(false);
        if (dnsResult.IsErr)
        {
            return Result<ITcpConnection, TcpError>.Err(TcpError.DnsFailed);
        }

        Exception? last = null;
        foreach (var ip in dnsResult.Value.Addresses)
        {
            var family = ip.AddressFamily;
            var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ConnectTimeout);
                await socket.ConnectAsync(new IPEndPoint(ip, endpoint.Port), cts.Token)
                    .ConfigureAwait(false);
                return Result<ITcpConnection, TcpError>.Ok(
                    new SocketTcpConnection(socket, endpoint));
            }
            catch (Exception ex)
            {
                last = ex;
                socket.Dispose();
            }
        }
        _ = last;
        return Result<ITcpConnection, TcpError>.Err(TcpError.ConnectFailed);
    }

    /// <summary>
    /// Connect directly to an already-resolved <see cref="IPEndPoint"/>.
    /// Bypasses DNS — useful for tests against a local listener.
    /// </summary>
    public async Task<Result<ITcpConnection, TcpError>> DialDirectAsync(
        IPEndPoint endpoint, TcpEndpoint label, CancellationToken ct = default)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(endpoint, cts.Token).ConfigureAwait(false);
            return Result<ITcpConnection, TcpError>.Ok(
                new SocketTcpConnection(socket, label));
        }
        catch
        {
            socket.Dispose();
            return Result<ITcpConnection, TcpError>.Err(TcpError.ConnectFailed);
        }
    }
}

public enum TcpError
{
    DnsFailed,
    ConnectFailed,
    Timeout,
}
