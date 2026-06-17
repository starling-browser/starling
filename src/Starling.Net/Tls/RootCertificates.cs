using Org.BouncyCastle.X509;

namespace Starling.Net.Tls;

public sealed class RootCertificates
{
    private const string ResourceSuffix = ".Resources.Roots.ccadb.pem";
    private readonly IReadOnlyList<X509Certificate> _certificates;

    private RootCertificates(IReadOnlyList<X509Certificate> certificates)
    {
        _certificates = certificates;
    }

    /// <summary>
    /// The embedded CCADB bundle only. Deterministic across machines — use this
    /// for tests and anywhere reproducible trust decisions matter.
    /// </summary>
    public static RootCertificates Default { get; } = LoadDefault();

    /// <summary>
    /// The embedded bundle plus any roots the operating system trusts (corporate
    /// internal CAs, mkcert, debugging proxies). The embedded bundle is the floor,
    /// so missing or stale OS roots can never make a publicly-valid site fail.
    /// This is what live connections use.
    /// </summary>
    public static RootCertificates SystemTrust { get; } = BuildSystemTrust();

    public IReadOnlyList<X509Certificate> Certificates => _certificates;

    public static RootCertificates FromPem(Stream pemStream)
    {
        if (pemStream is null)
        {
            throw new ArgumentNullException(nameof(pemStream));
        }

        var parser = new X509CertificateParser();
        var certificates = parser.ReadCertificates(pemStream).ToArray();
        if (certificates.Length == 0)
        {
            throw new InvalidDataException("root certificate bundle is empty");
        }

        return new RootCertificates(certificates);
    }

    private static RootCertificates BuildSystemTrust()
    {
        var combined = new List<X509Certificate>(Default._certificates);
        // Dedup by encoded bytes so a CA present in both the bundle and the OS
        // store becomes a single trust anchor.
        var seen = new HashSet<string>(Default._certificates.Count);
        foreach (var certificate in Default._certificates)
        {
            seen.Add(Fingerprint(certificate));
        }

        foreach (var certificate in SystemRootCertificates.Load())
        {
            if (seen.Add(Fingerprint(certificate)))
            {
                combined.Add(certificate);
            }
        }

        return new RootCertificates(combined);
    }

    private static string Fingerprint(X509Certificate certificate) =>
        Convert.ToHexString(certificate.GetEncoded());

    private static RootCertificates LoadDefault()
    {
        var assembly = typeof(RootCertificates).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(ResourceSuffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Missing embedded root store resource: {resourceName}");
        return FromPem(stream);
    }
}
