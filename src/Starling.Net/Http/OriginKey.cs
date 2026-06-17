namespace Starling.Net.Http;

/// <summary>
/// (scheme, host, port) tuple identifying an HTTP origin for connection-pooling
/// purposes. Matches RFC 6454 §4's "origin" projection but without the URL's
/// path or query — those don't influence which TCP connection a request can
/// reuse.
/// </summary>
/// <remarks>
/// Scheme and host are case-insensitive per the URL spec; <see cref="Create"/>
/// normalises them to lowercase ASCII so equality lookups work without
/// per-call fold-case overhead.
/// </remarks>
public readonly record struct OriginKey(string Scheme, string Host, int Port)
{
    public static OriginKey Create(string scheme, string host, int port)
    {
        if (string.IsNullOrEmpty(scheme))
        {
            throw new ArgumentException("scheme must be non-empty.", nameof(scheme));
        }

        if (string.IsNullOrEmpty(host))
        {
            throw new ArgumentException("host must be non-empty.", nameof(host));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        return new OriginKey(scheme.ToLowerInvariant(), host.ToLowerInvariant(), port);
    }

    public override string ToString() => $"{Scheme}://{Host}:{Port}";
}
