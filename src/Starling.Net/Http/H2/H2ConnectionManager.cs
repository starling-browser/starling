using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Starling.Net.Http.H2;

internal static partial class H2ConnectionManagerLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "H2 connection dispose threw during teardown")]
    public static partial void ConnectionDisposeFailed(ILogger logger, Exception ex);
}

/// <summary>
/// Holds at most one live <see cref="H2Connection"/> per origin. Unlike the
/// HTTP/1.1 <see cref="ConnectionPool"/> (a queue of idle single-use sockets),
/// an HTTP/2 connection is long-lived and multiplexed, so the "pool" is just a
/// per-origin map of shared connections.
/// </summary>
internal sealed class H2ConnectionManager : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<OriginKey, H2Connection> _byOrigin = [];
    private readonly ILogger _log;
    private bool _disposed;

    public H2ConnectionManager(ILogger<H2ConnectionManager>? log = null)
    {
        _log = log ?? NullLogger<H2ConnectionManager>.Instance;
    }

    /// <summary>Return the live, usable connection for an origin, or null.</summary>
    public H2Connection? TryGet(OriginKey origin)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (!_byOrigin.TryGetValue(origin, out var conn))
            {
                return null;
            }

            if (conn.IsUsable)
            {
                return conn;
            }
            // Stale (GOAWAY/closed) — drop the reference; it disposes itself.
            _byOrigin.Remove(origin);
            return null;
        }
    }

    /// <summary>
    /// Register <paramref name="candidate"/> as the connection for its origin,
    /// unless another usable connection won a concurrent race — in which case
    /// the winner is returned and the caller must dispose the candidate.
    /// </summary>
    public H2Connection Adopt(OriginKey origin, H2Connection candidate)
    {
        lock (_gate)
        {
            if (!_disposed
                && _byOrigin.TryGetValue(origin, out var existing)
                && existing.IsUsable)
            {
                return existing;
            }
            _byOrigin[origin] = candidate;
            return candidate;
        }
    }

    /// <summary>Drop a connection (called from its close callback) if still mapped.</summary>
    public void Remove(OriginKey origin, H2Connection conn)
    {
        lock (_gate)
        {
            if (_byOrigin.TryGetValue(origin, out var current) && ReferenceEquals(current, conn))
            {
                _byOrigin.Remove(origin);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<H2Connection> all;
        lock (_gate)
        {
            _disposed = true;
            all = [.. _byOrigin.Values];
            _byOrigin.Clear();
        }
        foreach (var c in all)
        {
            try { await c.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { H2ConnectionManagerLog.ConnectionDisposeFailed(_log, ex); /* teardown of a dying connection */ }
        }
    }
}
