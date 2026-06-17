using Org.BouncyCastle.Tls;

namespace Starling.Net.Tls;

internal sealed class StarlingTlsAuthentication : TlsAuthentication
{
    private readonly TlsClientOptions _options;
    private readonly RootCertificates _roots;
    private readonly RevocationSet _revocations;
    private readonly Action<CertificateSummary?> _onVerified;

    public StarlingTlsAuthentication(
        TlsClientOptions options,
        RootCertificates roots,
        RevocationSet revocations,
        Action<CertificateSummary?> onVerified)
    {
        _options = options;
        _roots = roots;
        _revocations = revocations;
        _onVerified = onVerified;
    }

    public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
    {
        if (!CertificateVerifier.Verify(
                serverCertificate.Certificate, _options.ServerName, _roots, _options.ValidationTime, _revocations))
        {
            throw new TlsFatalAlert(AlertDescription.bad_certificate, "server certificate validation failed");
        }

        // Capture the verified leaf for the UI lock popover. Only reached once
        // the chain has validated against the bundled root store.
        _onVerified(CertificateVerifier.Summarize(serverCertificate.Certificate));
    }

    public TlsCredentials? GetClientCredentials(CertificateRequest certificateRequest) => null;
}
