using Starling.Common;
using Starling.Net.Tcp;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace Starling.Net.Tls;

/// <summary>
/// Pure-managed TLS transport built on BouncyCastle's TLS 1.3 implementation.
/// Drives the protocol in non-blocking mode behind <see cref="BcDuplexTlsStream"/>
/// so reads and writes can proceed concurrently — a hard requirement for HTTP/2,
/// which multiplexes a single connection between a reader loop and request
/// writers.
/// </summary>
public sealed class BcTlsTransport : ITlsTransport
{
    private readonly TlsClientProtocol _protocol;
    private readonly BcDuplexTlsStream _stream;
    private bool _disposed;

    private BcTlsTransport(
        TlsClientProtocol protocol,
        BcDuplexTlsStream stream,
        string? negotiatedApplicationProtocol,
        CertificateSummary? peerCertificate)
    {
        _protocol = protocol;
        _stream = stream;
        NegotiatedApplicationProtocol = negotiatedApplicationProtocol;
        PeerCertificate = peerCertificate;
    }

    public Stream Stream => _stream;
    public string? NegotiatedApplicationProtocol { get; }
    public CertificateSummary? PeerCertificate { get; }

    public static async Task<Result<BcTlsTransport, TlsError>> ConnectAsync(
        ITcpConnection tcpConnection,
        TlsClientOptions options,
        CancellationToken ct = default)
    {
        if (tcpConnection is null)
        {
            throw new ArgumentNullException(nameof(tcpConnection));
        }

        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.ServerName) || options.ApplicationProtocols.Count == 0)
        {
            return Result<BcTlsTransport, TlsError>.Err(TlsError.InvalidOptions);
        }

        var tcpStream = new TcpConnectionStream(tcpConnection);
        var client = new StarlingTlsClient(
            new BcTlsCrypto(new SecureRandom()),
            options,
            RootCertificates.SystemTrust);
        var protocol = new TlsClientProtocol(); // non-blocking mode

        try
        {
            var stream = await BcDuplexTlsStream.HandshakeAsync(protocol, client, tcpStream, ct)
                .ConfigureAwait(false);
            return Result<BcTlsTransport, TlsError>.Ok(
                new BcTlsTransport(protocol, stream, client.NegotiatedApplicationProtocol, client.PeerCertificate));
        }
        catch (TlsFatalAlert alert) when (alert.AlertDescription is AlertDescription.bad_certificate
            or AlertDescription.certificate_expired
            or AlertDescription.certificate_revoked
            or AlertDescription.certificate_unknown
            or AlertDescription.unknown_ca)
        {
            protocol.Close();
            return Result<BcTlsTransport, TlsError>.Err(TlsError.CertificateRejected);
        }
        catch
        {
            protocol.Close();
            return Result<BcTlsTransport, TlsError>.Err(TlsError.HandshakeFailed);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Disposing the duplex stream sends close_notify (best-effort) and tears
        // down the wrapped TCP connection.
        _stream.Dispose();
    }
}
