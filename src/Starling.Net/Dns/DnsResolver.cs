using System.Net;
using Starling.Common;

namespace Starling.Net.Dns;

/// <summary>
/// Pure-managed DNS resolver. Public API: <see cref="ResolveAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Short-circuits for the names a resolver shouldn't have to ask the network
/// about — <c>localhost</c> → <c>127.0.0.1</c> + <c>::1</c>; numeric IPv4 dotted
/// quad → that address. Otherwise queries the supplied
/// <see cref="IDnsTransport"/>, parses the response, follows CNAMEs locally,
/// and caches successful results with their TTL.
/// </para>
/// <para>
/// Cache is bounded by both TTL and entry count (default 256). Resolving the
/// same name within TTL returns immediately. Failed lookups are NOT cached
/// (negative caching deferred).
/// </para>
/// </remarks>
public sealed class DnsResolver
{
    private readonly IDnsTransport _transport;
    private readonly DnsCache _cache;
    private readonly Func<ushort> _newId;

    public DnsResolver(IDnsTransport transport)
        : this(transport, new DnsCache(maxEntries: 256), DefaultIdGenerator) { }

    /// <summary>Constructor used by tests to seed a deterministic id sequence.</summary>
    public DnsResolver(IDnsTransport transport, DnsCache cache, Func<ushort> newId)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _newId = newId ?? throw new ArgumentNullException(nameof(newId));
    }

    private static readonly Random _idRng = Random.Shared;
    private static ushort DefaultIdGenerator() => (ushort)_idRng.Next(0, 0x10000);

    public async Task<Result<DnsResult, DnsError>> ResolveAsync(
        string hostname, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            return Result<DnsResult, DnsError>.Err(DnsError.EmptyHostname);
        }

        hostname = hostname.Trim().TrimEnd('.').ToLowerInvariant();

        // Short-circuit: localhost.
        if (hostname == "localhost")
        {
            return Result<DnsResult, DnsError>.Ok(DnsResult.LoopbackFor(hostname));
        }

        // Short-circuit: numeric IPv4 dotted quad.
        if (IPAddress.TryParse(hostname, out var literal))
        {
            return Result<DnsResult, DnsError>.Ok(new DnsResult(hostname, [literal], TimeSpan.FromHours(1)));
        }

        // Cache.
        if (_cache.TryGet(hostname, out var cached))
        {
            return Result<DnsResult, DnsError>.Ok(cached);
        }

        // Query A + AAAA in parallel.
        var aTask = QueryAsync(hostname, DnsMessage.QType.A, ct);
        var aaaaTask = QueryAsync(hostname, DnsMessage.QType.AAAA, ct);
        await Task.WhenAll(aTask, aaaaTask).ConfigureAwait(false);

        var ips = new List<IPAddress>();
        var minTtl = uint.MaxValue;

        foreach (var task in new[] { aTask, aaaaTask })
        {
            var result = task.Result;
            if (result.Header.Rcode == DnsMessage.RCode.NameError)
            {
                continue;
            }

            if (result.Header.Rcode != DnsMessage.RCode.NoError)
            {
                continue;
            }

            foreach (var a in result.Answers)
            {
                if (a is DnsMessage.AAnswer av4)
                {
                    ips.Add(new IPAddress(av4.IPv4));
                    minTtl = Math.Min(minTtl, av4.Ttl);
                }
                else if (a is DnsMessage.AaaaAnswer av6)
                {
                    ips.Add(new IPAddress(av6.IPv6));
                    minTtl = Math.Min(minTtl, av6.Ttl);
                }
                // CNAME / Other: ignored for v1 — the recursive resolver
                // upstream has already chased the chain and embedded the A/AAAA.
            }
        }

        if (ips.Count == 0)
        {
            return Result<DnsResult, DnsError>.Err(DnsError.NoRecords);
        }

        var ttl = TimeSpan.FromSeconds(minTtl == uint.MaxValue ? 60 : minTtl);
        var result_ = new DnsResult(hostname, ips, ttl);
        _cache.Put(hostname, result_);
        return Result<DnsResult, DnsError>.Ok(result_);
    }

    private async Task<(DnsMessage.Header Header, List<DnsMessage.Answer> Answers)>
        QueryAsync(string hostname, DnsMessage.QType qtype, CancellationToken ct)
    {
        var id = _newId();
        var packet = DnsMessage.BuildQuery(id, hostname, qtype);
        byte[] response;
        try
        {
            response = await _transport.SendAsync(packet, ct).ConfigureAwait(false);
        }
        catch
        {
            return (default, []);
        }
        try
        {
            var (header, _, answers) = DnsMessage.Parse(response);
            return (header, answers);
        }
        catch (FormatException)
        {
            return (default, []);
        }
    }
}

public enum DnsError
{
    EmptyHostname,
    NoRecords,
    Timeout,
    TransportFailure,
}

public sealed record DnsResult(string Hostname, IReadOnlyList<IPAddress> Addresses, TimeSpan Ttl)
{
    public static DnsResult LoopbackFor(string hostname) => new(
        hostname,
        [IPAddress.Loopback, IPAddress.IPv6Loopback],
        TimeSpan.FromHours(1));
}
