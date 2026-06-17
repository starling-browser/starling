using Starling.Net.Tcp;
using Starling.Net.Tls;

namespace Starling.Net.Http;

/// <summary>
/// Concrete <see cref="IHttpTransport"/> wrapping a TCP connection optionally
/// upgraded to TLS. The instance owns its underlying TCP socket and (if
/// present) <see cref="BcTlsTransport"/>; <see cref="DisposeAsync"/> tears
/// everything down so the same wrapper can never be used twice after a close.
/// </summary>
/// <remarks>
/// The <see cref="Stream"/> exposed to the HTTP layer is the TLS-wrapped
/// stream for HTTPS or a thin <see cref="TcpConnectionStream"/> adapter for
/// plain HTTP. Either way, reads/writes go through one byte-oriented stream
/// so the request writer and response parser remain transport-agnostic.
/// </remarks>
internal sealed class PooledHttpTransport : IHttpTransport
{
    private readonly ITcpConnection _tcp;
    private readonly BcTlsTransport? _tls;
    private readonly TcpConnectionStream? _plainStream;
    private bool _disposed;

    public OriginKey Origin { get; }
    public Stream Stream { get; }
    public string? Alpn { get; }
    public CertificateSummary? PeerCertificate { get; }

    public bool IsOpen => !_disposed && _tcp.IsOpen;

    private PooledHttpTransport(
        OriginKey origin,
        ITcpConnection tcp,
        BcTlsTransport? tls,
        TcpConnectionStream? plainStream,
        Stream stream,
        string? alpn,
        CertificateSummary? peerCertificate)
    {
        Origin = origin;
        _tcp = tcp;
        _tls = tls;
        _plainStream = plainStream;
        Stream = stream;
        Alpn = alpn;
        PeerCertificate = peerCertificate;
    }

    public static PooledHttpTransport ForPlainHttp(
        OriginKey origin, ITcpConnection tcp)
    {
        ArgumentNullException.ThrowIfNull(tcp);
        var stream = new TcpConnectionStream(tcp);
        return new PooledHttpTransport(
            origin, tcp, tls: null, plainStream: stream, stream, alpn: null, peerCertificate: null);
    }

    public static PooledHttpTransport ForTls(
        OriginKey origin, ITcpConnection tcp, BcTlsTransport tls)
    {
        ArgumentNullException.ThrowIfNull(tcp);
        ArgumentNullException.ThrowIfNull(tls);
        return new PooledHttpTransport(
            origin, tcp, tls, plainStream: null, tls.Stream,
            tls.NegotiatedApplicationProtocol, tls.PeerCertificate);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_tls is not null)
        {
            // Disposing the TLS transport also disposes the wrapped TCP stream
            // (which in turn disposes the connection).
            _tls.Dispose();
        }
        else if (_plainStream is not null)
        {
            // Plain stream owns the connection via its Dispose override.
            _plainStream.Dispose();
        }
        else
        {
            await _tcp.DisposeAsync().ConfigureAwait(false);
        }
    }
}
