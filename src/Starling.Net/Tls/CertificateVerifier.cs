using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Pkix;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Utilities.Collections;
using Org.BouncyCastle.X509;
using Org.BouncyCastle.X509.Store;

namespace Starling.Net.Tls;

public static class CertificateVerifier
{
    public static bool Verify(
        Certificate certificate,
        string hostname,
        RootCertificates roots,
        DateTimeOffset? validationTime = null,
        RevocationSet? revocations = null)
    {
        if (certificate is null)
        {
            throw new ArgumentNullException(nameof(certificate));
        }

        if (roots is null)
        {
            throw new ArgumentNullException(nameof(roots));
        }

        if (string.IsNullOrWhiteSpace(hostname))
        {
            return false;
        }

        var chain = DecodeChain(certificate);
        if (chain.Count == 0)
        {
            return false;
        }

        // PKIX path validation does not match the hostname, so that stays ours.
        // Everything else — path building to a trusted anchor, signatures,
        // validity windows, basic constraints, key usage, name constraints — is
        // delegated to BouncyCastle's RFC 5280 validator below.
        if (!CertificateHostNameMatcher.Matches(chain[0], hostname))
        {
            return false;
        }

        var path = BuildTrustedPath(chain, roots.Certificates, validationTime);
        if (path is null)
        {
            return false;
        }

        // Local CRLSet-style blocklist check over the validated path. Empty by
        // default, so this is a no-op until a revocation feed is loaded.
        var revocationSet = revocations ?? RevocationSet.Empty;
        if (!revocationSet.IsEmpty && PathContainsRevokedCert(path, revocationSet))
        {
            return false;
        }

        return true;
    }

    // Build and validate a path from the leaf (chain[0]) to any trusted root,
    // using the presented certs as the pool of candidate intermediates. The
    // builder picks the shortest valid path, so extra cross-sign certs the
    // server appends above the real anchor (e.g. Google's GTS Root R1 trailed by
    // a legacy GlobalSign root we don't bundle) no longer cause a rejection.
    // Returns null when no valid path exists.
    private static PkixCertPathBuilderResult? BuildTrustedPath(
        List<X509Certificate> chain,
        IReadOnlyList<X509Certificate> roots,
        DateTimeOffset? validationTime)
    {
        var anchors = new HashSet<TrustAnchor>();
        foreach (var root in roots)
        {
            anchors.Add(new TrustAnchor(root, null));
        }

        var target = new X509CertStoreSelector { Certificate = chain[0] };
        var parameters = new PkixBuilderParameters(anchors, target)
        {
            // Live OCSP/CRL would add blocking network I/O on the handshake path.
            // Revocation is handled out of band by the local blocklist below.
            IsRevocationEnabled = false,
            Date = (validationTime ?? DateTimeOffset.UtcNow).UtcDateTime,
        };
        parameters.AddStoreCert(CollectionUtilities.CreateStore(chain));

        try
        {
            return new PkixCertPathBuilder().Build(parameters);
        }
        catch (PkixCertPathBuilderException)
        {
            return null;
        }
    }

    // Walk the built path from leaf up to the trust anchor, checking each cert
    // against the blocklist. The issuer of each cert is the next one up; the
    // anchor is self-issued.
    private static bool PathContainsRevokedCert(PkixCertPathBuilderResult path, RevocationSet revocations)
    {
        var ordered = new List<X509Certificate>(path.CertPath.Certificates) { path.TrustAnchor.TrustedCert };
        for (var i = 0; i < ordered.Count; i++)
        {
            var issuer = i + 1 < ordered.Count ? ordered[i + 1] : ordered[i];
            if (revocations.IsRevoked(ordered[i], issuer))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Build a display summary of the leaf (end-entity) certificate. Returns
    /// null when the chain is empty. Intended for UI surfaces (the lock popover)
    /// after <see cref="Verify"/> has already accepted the chain.
    /// </summary>
    public static CertificateSummary? Summarize(Certificate certificate)
    {
        if (certificate is null)
        {
            throw new ArgumentNullException(nameof(certificate));
        }

        var chain = DecodeChain(certificate);
        if (chain.Count == 0)
        {
            return null;
        }

        var leaf = chain[0];
        return new CertificateSummary(
            FriendlyName(leaf.SubjectDN),
            FriendlyName(leaf.IssuerDN),
            new DateTimeOffset(leaf.NotBefore.ToUniversalTime(), TimeSpan.Zero),
            new DateTimeOffset(leaf.NotAfter.ToUniversalTime(), TimeSpan.Zero));
    }

    // Prefer the common name; fall back to the organisation, then the full DN.
    private static string FriendlyName(X509Name dn)
    {
        foreach (var oid in new[] { X509Name.CN, X509Name.O })
        {
            var values = dn.GetValueList(oid);
            if (values.Count > 0 && values[0] is string s && !string.IsNullOrWhiteSpace(s))
            {
                return s;
            }
        }
        return dn.ToString();
    }

    private static List<X509Certificate> DecodeChain(Certificate certificate)
    {
        var parser = new X509CertificateParser();
        return certificate.GetCertificateList()
            .Select(tlsCertificate => parser.ReadCertificate(tlsCertificate.GetEncoded()))
            .ToList();
    }
}

public static class CertificateHostNameMatcher
{
    public static bool Matches(X509Certificate certificate, string hostname)
    {
        if (certificate is null)
        {
            throw new ArgumentNullException(nameof(certificate));
        }

        if (string.IsNullOrWhiteSpace(hostname))
        {
            return false;
        }

        var normalizedHost = hostname.Trim().TrimEnd('.').ToLowerInvariant();
        var names = certificate.GetSubjectAlternativeNames();
        if (names is null || names.Count == 0)
        {
            return false;
        }

        foreach (var name in names)
        {
            if (name.Count < 2 || name[0] is not int { } type || type != GeneralName.DnsName)
            {
                continue;
            }

            if (name[1] is string dnsName && MatchDnsName(dnsName, normalizedHost))
            {
                return true;
            }
        }

        return false;
    }

    public static bool MatchDnsName(string pattern, string hostname)
    {
        var normalizedPattern = pattern.Trim().TrimEnd('.').ToLowerInvariant();
        var normalizedHost = hostname.Trim().TrimEnd('.').ToLowerInvariant();
        if (normalizedPattern.Length == 0 || normalizedHost.Length == 0)
        {
            return false;
        }

        if (!normalizedPattern.Contains('*', StringComparison.Ordinal))
        {
            return normalizedPattern == normalizedHost;
        }

        if (!normalizedPattern.StartsWith("*.", StringComparison.Ordinal)
            || normalizedPattern.IndexOf('*', 1) >= 0)
        {
            return false;
        }

        var suffix = normalizedPattern[1..];
        if (!normalizedHost.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var unmatched = normalizedHost[..^suffix.Length];
        return unmatched.Length > 0 && !unmatched.Contains('.', StringComparison.Ordinal);
    }
}
