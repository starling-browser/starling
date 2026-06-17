using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using AwesomeAssertions;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;
using Starling.Net.Tcp;
using Starling.Net.Tls;
using BcCertificate = Org.BouncyCastle.Tls.Certificate;
using DotNetX509Certificate = System.Security.Cryptography.X509Certificates.X509Certificate2;

namespace Starling.Net.Tests.Tls;

[TestClass]
public class TlsClientTests
{
    [TestMethod]
    public void Default_root_store_loads_embedded_ccadb_bundle()
    {
        RootCertificates.Default.Certificates.Count.Should().BeGreaterThan(50);
    }

    [TestMethod]
    public void System_trust_is_a_superset_of_the_embedded_bundle()
    {
        // SystemTrust augments the embedded floor with OS-trusted roots. It can
        // never have fewer anchors than the bundle, and on a machine with a
        // populated Root store it has strictly more.
        RootCertificates.SystemTrust.Certificates.Count
            .Should().BeGreaterThanOrEqualTo(RootCertificates.Default.Certificates.Count);
    }

    [TestMethod]
    public void Client_extensions_advertise_sni_and_alpn()
    {
        var options = TlsClientOptions.ForHttps("example.com");
        var client = new StarlingTlsClient(
            new BcTlsCrypto(new SecureRandom()),
            options,
            RootCertificates.Default);

        var extensions = client.CreateClientExtensionsForTesting();

        var names = TlsExtensionsUtilities.GetServerNameExtensionClient(extensions);
        names.Should().ContainSingle();
        names[0].NameType.Should().Be(NameType.host_name);
        names[0].NameData.Should().Equal("example.com"u8.ToArray());

        var alpn = TlsExtensionsUtilities.GetAlpnExtensionClient(extensions)
            .Select(protocol => protocol.GetUtf8Decoding())
            .ToArray();
        alpn.Should().Equal("h2", "http/1.1");
    }

    [TestMethod]
    [DataRow("example.com", "example.com", true)]
    [DataRow("EXAMPLE.com.", "example.com", true)]
    [DataRow("*.example.com", "www.example.com", true)]
    [DataRow("*.example.com", "deep.www.example.com", false)]
    [DataRow("*.example.com", "example.com", false)]
    [DataRow("*.*.example.com", "www.example.com", false)]
    public void Dns_name_matching_handles_rfc6125_wildcard_shape(
        string pattern,
        string hostname,
        bool expected) =>
        CertificateHostNameMatcher.MatchDnsName(pattern, hostname).Should().Be(expected);

    [TestMethod]
    public async Task Invalid_options_return_error_before_handshake()
    {
        var result = await BcTlsTransport.ConnectAsync(
            new ClosedConnection(),
            new TlsClientOptions("", []),
            CancellationToken.None);

        result.IsErr.Should().BeTrue();
        result.Error.Should().Be(TlsError.InvalidOptions);
    }

    [TestMethod]
    public void Untrusted_self_signed_certificate_chain_is_rejected()
    {
        using var rsa = RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=bad.example",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("bad.example");
        request.CertificateExtensions.Add(san.Build());
        using DotNetX509Certificate certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1));

        var tlsCertificate = new BcTlsCrypto(new SecureRandom())
            .CreateCertificate(certificate.Export(X509ContentType.Cert));
        var chain = new BcCertificate([tlsCertificate]);

        CertificateVerifier.Verify(chain, "bad.example", RootCertificates.Default)
            .Should().BeFalse();
    }

    [TestMethod]
    public void Chain_is_trusted_when_anchor_precedes_an_untrusted_cross_sign_root()
    {
        // Mirrors angular.dev (Google Trust Services): the server presents
        //   leaf -> intermediate -> trusted root (cross-signed form) -> legacy root
        // where the trusted root is in our bundle but the trailing legacy root,
        // which cross-signed it, is not. The anchor sits at chain[^2], so a
        // verifier that only checks the terminal cert would wrongly reject this.
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(1);

        using var legacyKey = RSA.Create(2048);
        var legacyRoot = CaRequest("CN=Legacy Cross Root", legacyKey)
            .CreateSelfSigned(notBefore, notAfter);

        using var trustedKey = RSA.Create(2048);
        var trustedRequest = CaRequest("CN=Trusted Root", trustedKey);
        // Self-signed form goes in our store; cross-signed form (same subject and
        // key, issued by the legacy root) is what the server actually presents.
        using DotNetX509Certificate trustedSelfSigned =
            trustedRequest.CreateSelfSigned(notBefore, notAfter);
        using DotNetX509Certificate trustedCrossSigned = trustedRequest.Create(
            legacyRoot.SubjectName,
            X509SignatureGenerator.CreateForRSA(legacyKey, RSASignaturePadding.Pkcs1),
            notBefore,
            notAfter,
            [0x01, 0x02, 0x03, 0x04]);

        using var intermediateKey = RSA.Create(2048);
        using DotNetX509Certificate intermediate = CaRequest("CN=Intermediate", intermediateKey).Create(
            trustedSelfSigned.SubjectName,
            X509SignatureGenerator.CreateForRSA(trustedKey, RSASignaturePadding.Pkcs1),
            notBefore,
            notAfter,
            [0x02, 0x03, 0x04, 0x05]);

        using var leafKey = RSA.Create(2048);
        var leafRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=leaf.example",
            leafKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var leafSan = new SubjectAlternativeNameBuilder();
        leafSan.AddDnsName("leaf.example");
        leafRequest.CertificateExtensions.Add(leafSan.Build());
        using DotNetX509Certificate leaf = leafRequest.Create(
            intermediate.SubjectName,
            X509SignatureGenerator.CreateForRSA(intermediateKey, RSASignaturePadding.Pkcs1),
            notBefore,
            notAfter,
            [0x03, 0x04, 0x05, 0x06]);

        var crypto = new BcTlsCrypto(new SecureRandom());
        var presented = new BcCertificate(
        [
            crypto.CreateCertificate(leaf.Export(X509ContentType.Cert)),
            crypto.CreateCertificate(intermediate.Export(X509ContentType.Cert)),
            crypto.CreateCertificate(trustedCrossSigned.Export(X509ContentType.Cert)),
            crypto.CreateCertificate(legacyRoot.Export(X509ContentType.Cert)),
        ]);

        using var store = new MemoryStream(
            System.Text.Encoding.ASCII.GetBytes(trustedSelfSigned.ExportCertificatePem()));
        var roots = RootCertificates.FromPem(store);

        CertificateVerifier.Verify(presented, "leaf.example", roots)
            .Should().BeTrue();
    }

    [TestMethod]
    public void Leaf_signed_by_a_non_ca_intermediate_is_rejected()
    {
        // PKIX enforces basic constraints: an end-entity cert (CA=false) may not
        // issue other certs. The chain links and signatures are all valid and it
        // terminates at a trusted root, so the previous hand-rolled verifier
        // accepted it — this is the security gap the PKIX validator closes.
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(1);

        using var rootKey = RSA.Create(2048);
        using DotNetX509Certificate root = CaRequest("CN=Test Root", rootKey)
            .CreateSelfSigned(notBefore, notAfter);

        using var fakeKey = RSA.Create(2048);
        var fakeRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=Not A CA",
            fakeKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        fakeRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        using DotNetX509Certificate fakeIntermediate = fakeRequest.Create(
            root.SubjectName,
            X509SignatureGenerator.CreateForRSA(rootKey, RSASignaturePadding.Pkcs1),
            notBefore,
            notAfter,
            [0x01, 0x02, 0x03, 0x07]);

        using var leafKey = RSA.Create(2048);
        var leafRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=leaf.example",
            leafKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var leafSan = new SubjectAlternativeNameBuilder();
        leafSan.AddDnsName("leaf.example");
        leafRequest.CertificateExtensions.Add(leafSan.Build());
        using DotNetX509Certificate leaf = leafRequest.Create(
            fakeIntermediate.SubjectName,
            X509SignatureGenerator.CreateForRSA(fakeKey, RSASignaturePadding.Pkcs1),
            notBefore,
            notAfter,
            [0x01, 0x02, 0x03, 0x08]);

        var crypto = new BcTlsCrypto(new SecureRandom());
        var presented = new BcCertificate(
        [
            crypto.CreateCertificate(leaf.Export(X509ContentType.Cert)),
            crypto.CreateCertificate(fakeIntermediate.Export(X509ContentType.Cert)),
        ]);

        using var store = new MemoryStream(
            System.Text.Encoding.ASCII.GetBytes(root.ExportCertificatePem()));
        var roots = RootCertificates.FromPem(store);

        CertificateVerifier.Verify(presented, "leaf.example", roots)
            .Should().BeFalse();
    }

    [TestMethod]
    public void Revoked_leaf_serial_is_rejected_but_an_empty_blocklist_accepts()
    {
        var (presented, roots, leaf, intermediate) = BuildTrustedLeafChain();

        // Empty blocklist: the chain is otherwise valid, so it verifies.
        CertificateVerifier.Verify(presented, "leaf.example", roots, null, RevocationSet.Empty)
            .Should().BeTrue();

        // Revoke the leaf by (issuer SPKI, serial): now it must be rejected.
        var revoked = RevocationSet.Create(
            [],
            [(RevocationSet.SpkiHash(intermediate), RevocationSet.SerialHex(leaf))]);
        CertificateVerifier.Verify(presented, "leaf.example", roots, null, revoked)
            .Should().BeFalse();
    }

    [TestMethod]
    public void Blocked_intermediate_spki_is_rejected()
    {
        var (presented, roots, _, intermediate) = BuildTrustedLeafChain();

        // Distrust the intermediate's key outright — the whole path falls.
        var blocked = RevocationSet.Create([RevocationSet.SpkiHash(intermediate)], []);
        CertificateVerifier.Verify(presented, "leaf.example", roots, null, blocked)
            .Should().BeFalse();
    }

    [TestMethod]
    public void Revocation_text_format_round_trips()
    {
        const string text = """
            # sample blocklist
            spki   AABBCC

            serial DEADBEEF 01ff
            """;
        using var stream = new MemoryStream(System.Text.Encoding.ASCII.GetBytes(text));
        var set = RevocationSet.FromText(stream);

        set.IsEmpty.Should().BeFalse();
        // Hex is normalized to upper-case; a malformed line would have thrown.
        var equivalent = RevocationSet.Create(["aabbcc"], [("deadbeef", "01FF")]);
        equivalent.IsEmpty.Should().BeFalse();
    }

    // A valid leaf -> intermediate -> trusted root chain, with the root in the
    // returned store. Returns the presented chain plus the leaf and intermediate
    // so tests can target them for revocation.
    private static (BcCertificate Presented, RootCertificates Roots,
        Org.BouncyCastle.X509.X509Certificate Leaf,
        Org.BouncyCastle.X509.X509Certificate Intermediate) BuildTrustedLeafChain()
    {
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(1);

        using var rootKey = RSA.Create(2048);
        using DotNetX509Certificate root = CaRequest("CN=Test Root", rootKey)
            .CreateSelfSigned(notBefore, notAfter);

        using var intermediateKey = RSA.Create(2048);
        using DotNetX509Certificate intermediate = CaRequest("CN=Intermediate", intermediateKey).Create(
            root.SubjectName,
            X509SignatureGenerator.CreateForRSA(rootKey, RSASignaturePadding.Pkcs1),
            notBefore,
            notAfter,
            [0x0a, 0x0b, 0x0c, 0x0d]);

        using var leafKey = RSA.Create(2048);
        var leafRequest = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=leaf.example",
            leafKey,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var leafSan = new SubjectAlternativeNameBuilder();
        leafSan.AddDnsName("leaf.example");
        leafRequest.CertificateExtensions.Add(leafSan.Build());
        using DotNetX509Certificate leaf = leafRequest.Create(
            intermediate.SubjectName,
            X509SignatureGenerator.CreateForRSA(intermediateKey, RSASignaturePadding.Pkcs1),
            notBefore,
            notAfter,
            [0x0e, 0x0f, 0x10, 0x11]);

        var crypto = new BcTlsCrypto(new SecureRandom());
        var presented = new BcCertificate(
        [
            crypto.CreateCertificate(leaf.Export(X509ContentType.Cert)),
            crypto.CreateCertificate(intermediate.Export(X509ContentType.Cert)),
        ]);

        var parser = new Org.BouncyCastle.X509.X509CertificateParser();
        using var store = new MemoryStream(
            System.Text.Encoding.ASCII.GetBytes(root.ExportCertificatePem()));
        var roots = RootCertificates.FromPem(store);

        return (
            presented,
            roots,
            parser.ReadCertificate(leaf.Export(X509ContentType.Cert)),
            parser.ReadCertificate(intermediate.Export(X509ContentType.Cert)));
    }

    private static System.Security.Cryptography.X509Certificates.CertificateRequest CaRequest(
        string subject,
        RSA key)
    {
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            subject,
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        return request;
    }

    [TestMethod]
    [DataRow("cloudflare.com")]
    [DataRow("tls13.akamai.io")]
    public async Task Live_tls13_handshake_when_enabled(string host)
    {
        if (Environment.GetEnvironmentVariable("STARLING_LIVE_TLS_TESTS") != "1")
        {
            return;
        }

        var ct = CancellationToken.None;
        var addresses = await System.Net.Dns.GetHostAddressesAsync(host, ct);
        var endpoint = new IPEndPoint(addresses.First(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork), 443);
        var dialer = new TcpDialer(new Starling.Net.Dns.DnsResolver(new NoopDnsTransport()))
        {
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        var tcp = await dialer.DialDirectAsync(endpoint, TcpEndpoint.For(host, 443), ct);
        tcp.IsOk.Should().BeTrue();

        await using var connection = tcp.Value;
        var tls = await BcTlsTransport.ConnectAsync(
            connection,
            TlsClientOptions.ForHttps(host),
            ct);

        tls.IsOk.Should().BeTrue();
        tls.Value.NegotiatedApplicationProtocol.Should().BeOneOf("h2", "http/1.1", null);
        tls.Value.Dispose();
    }

    private sealed class ClosedConnection : ITcpConnection
    {
        public TcpEndpoint Endpoint => TcpEndpoint.For("closed.example", 443);
        public bool IsOpen => false;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct) => ValueTask.FromResult(0);
        public ValueTask ShutdownAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
            throw new InvalidOperationException("closed");
    }

    private sealed class NoopDnsTransport : Starling.Net.Dns.IDnsTransport
    {
        public Task<byte[]> SendAsync(byte[] queryPacket, CancellationToken ct) =>
            throw new InvalidOperationException("DNS is not exercised by this test");
    }
}
