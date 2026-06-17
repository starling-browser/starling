using System.Security.Cryptography;
using Org.BouncyCastle.X509;

namespace Starling.Net.Tls;

/// <summary>
/// A local, CRLSet-style revocation blocklist consulted during certificate
/// validation. It holds two kinds of entries, both matched in memory with no
/// network on the handshake path:
///
/// <list type="bullet">
/// <item><b>Blocked SPKIs</b> — the SHA-256 of a Subject Public Key Info. Any
/// cert carrying that key is rejected wherever it appears in the path. Used to
/// distrust a whole compromised or misbehaving CA key.</item>
/// <item><b>Revoked serials</b> — an (issuer SPKI SHA-256, serial) pair, the
/// standard way to revoke one leaf or intermediate without distrusting its
/// issuer.</item>
/// </list>
///
/// The set is empty by default, so it changes no behaviour until a data source
/// populates it. Refreshing that data from an out-of-band feed is a separate,
/// later piece — this type is only the consumer.
/// </summary>
public sealed class RevocationSet
{
    private readonly HashSet<string> _blockedSpki;
    private readonly HashSet<(string IssuerSpki, string Serial)> _revokedSerials;

    private RevocationSet(
        HashSet<string> blockedSpki,
        HashSet<(string, string)> revokedSerials)
    {
        _blockedSpki = blockedSpki;
        _revokedSerials = revokedSerials;
    }

    /// <summary>A set with no entries — revokes nothing.</summary>
    public static RevocationSet Empty { get; } =
        new(new HashSet<string>(), new HashSet<(string, string)>());

    /// <summary>
    /// The blocklist live connections consult. Loaded from an embedded resource
    /// if one is present, otherwise <see cref="Empty"/>.
    /// </summary>
    public static RevocationSet Default { get; } = LoadDefault();

    public bool IsEmpty => _blockedSpki.Count == 0 && _revokedSerials.Count == 0;

    public static RevocationSet Create(
        IEnumerable<string> blockedSpki,
        IEnumerable<(string IssuerSpki, string Serial)> revokedSerials)
    {
        if (blockedSpki is null)
        {
            throw new ArgumentNullException(nameof(blockedSpki));
        }

        if (revokedSerials is null)
        {
            throw new ArgumentNullException(nameof(revokedSerials));
        }

        return new RevocationSet(
            blockedSpki.Select(Normalize).ToHashSet(),
            revokedSerials.Select(e => (Normalize(e.IssuerSpki), Normalize(e.Serial))).ToHashSet());
    }

    /// <summary>
    /// True when <paramref name="certificate"/> is revoked — either its key is
    /// blocked outright, or its serial is revoked under <paramref name="issuer"/>.
    /// </summary>
    public bool IsRevoked(X509Certificate certificate, X509Certificate issuer)
    {
        if (certificate is null)
        {
            throw new ArgumentNullException(nameof(certificate));
        }

        if (issuer is null)
        {
            throw new ArgumentNullException(nameof(issuer));
        }

        if (IsEmpty)
        {
            return false;
        }

        if (_blockedSpki.Count > 0 && _blockedSpki.Contains(SpkiHash(certificate)))
        {
            return true;
        }

        return _revokedSerials.Count > 0
            && _revokedSerials.Contains((SpkiHash(issuer), SerialHex(certificate)));
    }

    /// <summary>SHA-256 of the cert's Subject Public Key Info, as upper-hex.</summary>
    public static string SpkiHash(X509Certificate certificate)
    {
        if (certificate is null)
        {
            throw new ArgumentNullException(nameof(certificate));
        }

        var spki = Org.BouncyCastle.X509.SubjectPublicKeyInfoFactory
            .CreateSubjectPublicKeyInfo(certificate.GetPublicKey())
            .GetDerEncoded();
        return Convert.ToHexString(SHA256.HashData(spki));
    }

    /// <summary>The cert's serial number as upper-hex of its unsigned big-endian bytes.</summary>
    public static string SerialHex(X509Certificate certificate)
    {
        if (certificate is null)
        {
            throw new ArgumentNullException(nameof(certificate));
        }

        return Convert.ToHexString(certificate.SerialNumber.ToByteArrayUnsigned());
    }

    /// <summary>
    /// Parse the blocklist text format. One directive per line, '#' starts a
    /// comment, blank lines ignored:
    /// <code>
    /// spki   &lt;sha256-hex-of-SPKI&gt;
    /// serial &lt;sha256-hex-of-issuer-SPKI&gt; &lt;serial-hex&gt;
    /// </code>
    /// </summary>
    public static RevocationSet FromText(Stream textStream)
    {
        if (textStream is null)
        {
            throw new ArgumentNullException(nameof(textStream));
        }

        var blockedSpki = new HashSet<string>();
        var revokedSerials = new HashSet<(string, string)>();

        using var reader = new StreamReader(textStream);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
            {
                continue;
            }

            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (parts[0].ToLowerInvariant())
            {
                case "spki" when parts.Length == 2:
                    blockedSpki.Add(Normalize(parts[1]));
                    break;
                case "serial" when parts.Length == 3:
                    revokedSerials.Add((Normalize(parts[1]), Normalize(parts[2])));
                    break;
                default:
                    throw new InvalidDataException($"malformed revocation entry: {trimmed}");
            }
        }

        return new RevocationSet(blockedSpki, revokedSerials);
    }

    private static string Normalize(string hex) =>
        (hex ?? throw new ArgumentNullException(nameof(hex)))
        .Trim()
        .Replace(":", "", StringComparison.Ordinal)
        .ToUpperInvariant();

    private static RevocationSet LoadDefault()
    {
        const string resourceSuffix = ".Resources.Revocations.blocklist.txt";
        var assembly = typeof(RevocationSet).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(resourceSuffix, StringComparison.Ordinal));
        if (resourceName is null)
        {
            return Empty;
        }

        using var stream = assembly.GetManifestResourceStream(resourceName);
        return stream is null ? Empty : FromText(stream);
    }
}
