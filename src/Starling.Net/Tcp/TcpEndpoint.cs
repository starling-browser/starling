namespace Starling.Net.Tcp;

/// <summary>
/// Identifies a TCP destination by its DNS-level <see cref="Hostname"/> and
/// <see cref="Port"/>. The hostname is what we'll resolve; the resulting
/// IP+port goes into the kernel.
/// </summary>
public readonly record struct TcpEndpoint(string Hostname, int Port)
{
    public override string ToString() => $"{Hostname}:{Port}";

    public static TcpEndpoint For(string hostname, int port)
    {
        if (string.IsNullOrWhiteSpace(hostname))
        {
            throw new ArgumentException("Hostname required.", nameof(hostname));
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), port, "Port must be 1..65535.");
        }

        return new TcpEndpoint(hostname, port);
    }
}
