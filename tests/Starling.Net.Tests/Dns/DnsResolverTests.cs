using System.Net;
using AwesomeAssertions;
using Starling.Net.Dns;
namespace Starling.Net.Tests.Dns;

[TestClass]
public class DnsResolverTests
{
    [TestMethod]
    public async Task Localhost_short_circuits_to_loopback_addresses()
    {
        var resolver = new DnsResolver(new FailingTransport());
        var ct = CancellationToken.None;
        var r = await resolver.ResolveAsync("localhost", ct);
        r.IsOk.Should().BeTrue();
        r.Value.Addresses.Should().Contain(IPAddress.Loopback);
    }

    [TestMethod]
    public async Task Numeric_dotted_quad_passes_through_without_query()
    {
        var resolver = new DnsResolver(new FailingTransport());
        var ct = CancellationToken.None;
        var r = await resolver.ResolveAsync("8.8.8.8", ct);
        r.IsOk.Should().BeTrue();
        r.Value.Addresses.Should().ContainSingle()
            .Which.Should().Be(IPAddress.Parse("8.8.8.8"));
    }

    [TestMethod]
    public async Task Empty_hostname_is_an_error()
    {
        var resolver = new DnsResolver(new FailingTransport());
        var ct = CancellationToken.None;
        var r = await resolver.ResolveAsync("  ", ct);
        r.IsErr.Should().BeTrue();
        r.Error.Should().Be(DnsError.EmptyHostname);
    }

    [TestMethod]
    public async Task Query_against_fake_transport_returns_parsed_address()
    {
        // FakeDnsTransport returns a canned response for example.com → 93.184.216.34.
        var transport = new FakeDnsTransport();
        var resolver = new DnsResolver(transport, new DnsCache(), () => 0x1234);
        var ct = CancellationToken.None;
        var r = await resolver.ResolveAsync("example.com", ct);
        r.IsOk.Should().BeTrue();
        r.Value.Addresses.Should().Contain(IPAddress.Parse("93.184.216.34"));
    }

    [TestMethod]
    public async Task Cached_result_skips_transport()
    {
        var transport = new FakeDnsTransport();
        var cache = new DnsCache();
        var resolver = new DnsResolver(transport, cache, () => 0x1234);
        var ct = CancellationToken.None;

        await resolver.ResolveAsync("example.com", ct);
        var firstCalls = transport.CallCount;

        await resolver.ResolveAsync("example.com", ct);
        transport.CallCount.Should().Be(firstCalls,
            because: "cache should serve the second lookup");
    }

    [TestMethod]
    public async Task NoRecords_when_response_has_zero_answers()
    {
        // A response with NOERROR but ANCOUNT=0.
        var transport = new EmptyDnsTransport();
        var resolver = new DnsResolver(transport, new DnsCache(), () => 0xABCD);
        var ct = CancellationToken.None;
        var r = await resolver.ResolveAsync("nowhere.example", ct);
        r.IsErr.Should().BeTrue();
        r.Error.Should().Be(DnsError.NoRecords);
    }

    // -----------------------------------------------------------------------
    // Cache unit tests
    // -----------------------------------------------------------------------

    [TestMethod]
    public void Cache_returns_cached_within_ttl_and_evicts_after()
    {
        var fakeNow = new MutableClock(DateTimeOffset.UtcNow);
        var cache = new DnsCache(now: () => fakeNow.Now);

        var r = new DnsResult("example.com", [IPAddress.Loopback], TimeSpan.FromSeconds(10));
        cache.Put("example.com", r);

        cache.TryGet("example.com", out var hit).Should().BeTrue();
        hit.Hostname.Should().Be("example.com");

        fakeNow.Now += TimeSpan.FromSeconds(11);
        cache.TryGet("example.com", out _).Should().BeFalse();
    }

    [TestMethod]
    public void Cache_evicts_oldest_when_over_capacity()
    {
        var cache = new DnsCache(maxEntries: 2);
        cache.Put("a", new DnsResult("a", [IPAddress.Loopback], TimeSpan.FromSeconds(60)));
        cache.Put("b", new DnsResult("b", [IPAddress.Loopback], TimeSpan.FromSeconds(60)));
        cache.Put("c", new DnsResult("c", [IPAddress.Loopback], TimeSpan.FromSeconds(60)));

        cache.Count.Should().Be(2);
        cache.TryGet("a", out _).Should().BeFalse();
        cache.TryGet("b", out _).Should().BeTrue();
        cache.TryGet("c", out _).Should().BeTrue();
    }

    [TestMethod]
    public void Cache_recent_access_bumps_LRU_order()
    {
        var cache = new DnsCache(maxEntries: 2);
        cache.Put("a", new DnsResult("a", [IPAddress.Loopback], TimeSpan.FromSeconds(60)));
        cache.Put("b", new DnsResult("b", [IPAddress.Loopback], TimeSpan.FromSeconds(60)));
        // Touching 'a' makes 'b' the LRU candidate for eviction.
        cache.TryGet("a", out _);
        cache.Put("c", new DnsResult("c", [IPAddress.Loopback], TimeSpan.FromSeconds(60)));

        cache.TryGet("a", out _).Should().BeTrue();
        cache.TryGet("b", out _).Should().BeFalse();
        cache.TryGet("c", out _).Should().BeTrue();
    }

    // -----------------------------------------------------------------------
    // Test doubles
    // -----------------------------------------------------------------------

    private sealed class FailingTransport : IDnsTransport
    {
        public Task<byte[]> SendAsync(byte[] queryPacket, CancellationToken ct)
            => throw new InvalidOperationException("test transport must not be called");
    }

    private sealed class EmptyDnsTransport : IDnsTransport
    {
        public Task<byte[]> SendAsync(byte[] queryPacket, CancellationToken ct)
        {
            // Echo the question section with QR=1, ANCOUNT=0.
            var resp = new byte[queryPacket.Length];
            Array.Copy(queryPacket, resp, queryPacket.Length);
            resp[2] = 0x81; // QR=1, RD=1
            resp[3] = 0x80; // RA=1
            // Leave ANCOUNT at 0.
            return Task.FromResult(resp);
        }
    }

    private sealed class FakeDnsTransport : IDnsTransport
    {
        public int CallCount { get; private set; }

        public Task<byte[]> SendAsync(byte[] queryPacket, CancellationToken ct)
        {
            CallCount++;

            // Parse the question to discover the qtype + name.
            var (h, qs, _) = DnsMessage.Parse(queryPacket);
            if (qs.Count == 0)
            {
                throw new InvalidOperationException();
            }

            var q = qs[0];

            // Build a response only for the A query; for AAAA return NOERROR
            // with zero answers (simulates v4-only host).
            if (q.Type != DnsMessage.QType.A)
            {
                var empty = new byte[queryPacket.Length];
                Array.Copy(queryPacket, empty, queryPacket.Length);
                empty[2] = 0x81; empty[3] = 0x80;
                empty[7] = 0;
                return Task.FromResult(empty);
            }

            // Manually assemble: header(12) + question + answer.
            var name = DnsMessage.EncodeName(q.Name);
            var resp = new byte[12 + name.Length + 4 + name.Length + 10 + 4];
            // Header
            resp[0] = queryPacket[0]; resp[1] = queryPacket[1];
            resp[2] = 0x81; resp[3] = 0x80;
            resp[5] = 1;
            resp[7] = 1;
            // Question
            Array.Copy(name, 0, resp, 12, name.Length);
            var qoff = 12 + name.Length;
            resp[qoff + 1] = (byte)DnsMessage.QType.A;
            resp[qoff + 3] = (byte)DnsMessage.QClass.IN;
            // Answer
            var aoff = qoff + 4;
            Array.Copy(name, 0, resp, aoff, name.Length);
            var roff = aoff + name.Length;
            resp[roff + 1] = (byte)DnsMessage.QType.A;
            resp[roff + 3] = (byte)DnsMessage.QClass.IN;
            resp[roff + 6] = 0x01; resp[roff + 7] = 0x2C;  // TTL = 300
            resp[roff + 9] = 4;
            resp[roff + 10] = 93; resp[roff + 11] = 184;
            resp[roff + 12] = 216; resp[roff + 13] = 34;
            return Task.FromResult(resp);
        }
    }

    private sealed class MutableClock
    {
        public DateTimeOffset Now;
        public MutableClock(DateTimeOffset start) { Now = start; }
    }
}
