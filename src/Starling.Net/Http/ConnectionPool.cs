using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Starling.Net.Http;

internal static partial class ConnectionPoolLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "transport dispose threw during pool discard")]
    public static partial void DiscardFailed(ILogger logger, Exception ex);
}

/// <summary>
/// Per-origin pool of idle, kept-alive HTTP/1.1 transports. Keyed on
/// <see cref="OriginKey"/> (scheme/host/port). LRU-bounded, with an idle
/// timeout that prunes long-quiet connections before they would be killed
/// by upstream NATs or server-side reapers.
/// </summary>
/// <remarks>
/// Sizing rationale: 6 idle connections per origin matches the classic
/// HTTP/1.1 browser concurrency cap (Chrome / Firefox 6, Safari 6).
/// Idle timeout default of 60s is the value Chromium has used since the
/// early Blink era; most production HTTP/1.1 servers (nginx, apache, IIS)
/// have a server-side keep-alive timeout in the 5–120s range, and 60s sits
/// comfortably under the upper bound while letting bursty page loads reuse
/// connections.
/// <para>
/// Eviction policy: oldest by last-used time. When the pool is full and a
/// new release arrives, the oldest is dequeued and disposed before the new
/// one is pushed in.
/// </para>
/// <para>
/// Thread safety: <see cref="TryAcquire"/>, <see cref="ReleaseAsync"/>,
///  and <see cref="DisposeAllAsync"/> are all safe
/// to call concurrently. Each origin owns its own queue under a per-origin
/// lock so requests against different origins don't contend.
/// </para>
/// </remarks>
public sealed class ConnectionPool : IAsyncDisposable
{
    /// <summary>Default per-origin idle capacity (HTTP/1.1 browser cap).</summary>
    public const int DefaultMaxPerOrigin = 6;

    /// <summary>Default idle timeout matching Chromium's historical default.</summary>
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromSeconds(60);

    private readonly Dictionary<OriginKey, LinkedList<Entry>> _byOrigin = [];
    private readonly object _gate = new();
    private readonly ILogger _log;
    private bool _disposed;

    public int MaxPerOrigin { get; }
    public TimeSpan IdleTimeout { get; }

    public ConnectionPool() : this(DefaultMaxPerOrigin, DefaultIdleTimeout) { }

    public ConnectionPool(int maxPerOrigin, TimeSpan idleTimeout, ILogger<ConnectionPool>? log = null)
    {
        if (maxPerOrigin < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxPerOrigin), "Pool capacity must be at least 1.");
        }

        if (idleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(idleTimeout), "Idle timeout must be positive.");
        }

        MaxPerOrigin = maxPerOrigin;
        IdleTimeout = idleTimeout;
        _log = log ?? NullLogger<ConnectionPool>.Instance;
    }

    /// <summary>
    /// Snapshot of the idle-transport count across all origins. Mainly for
    /// tests; not used on the request path.
    /// </summary>
    public int IdleCount
    {
        get
        {
            lock (_gate)
            {
                var n = 0;
                foreach (var q in _byOrigin.Values)
                {
                    n += q.Count;
                }

                return n;
            }
        }
    }

    /// <summary>
    /// Idle count for one specific origin. Useful for tests asserting on
    /// per-host pool occupancy.
    /// </summary>
    public int IdleCountFor(OriginKey origin)
    {
        lock (_gate)
        {
            return _byOrigin.TryGetValue(origin, out var q) ? q.Count : 0;
        }
    }

    /// <summary>
    /// Attempt to acquire an idle transport for the given origin. Returns the
    /// most-recently-used (MRU) transport — newer connections are likely to
    /// still be open at the server.
    /// </summary>
    public IHttpTransport? TryAcquire(OriginKey origin)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return null;
            }

            if (!_byOrigin.TryGetValue(origin, out var q) || q.Count == 0)
            {
                return null;
            }

            // MRU: take from the tail. Discard any that have since closed
            // (e.g., peer FIN we haven't noticed yet) and try the next.
            while (q.Count > 0)
            {
                var node = q.Last!;
                q.RemoveLast();
                if (node.Value.Transport.IsOpen)
                {
                    return node.Value.Transport;
                }

                // Stale: dispose and keep looking.
                _ = DiscardAsync(node.Value.Transport, _log);
            }
            return null;
        }
    }

    /// <summary>
    /// Return a kept-alive transport to the pool. Caller must ensure the
    /// response body was fully consumed and both sides agreed to keep the
    /// connection alive. If the pool is at capacity for the origin, the
    /// oldest entry is evicted and disposed (LRU).
    /// </summary>
    public async ValueTask ReleaseAsync(IHttpTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);

        IHttpTransport? evicted = null;
        lock (_gate)
        {
            if (_disposed || !transport.IsOpen)
            {
                // Caller asked to release a broken transport, or the pool was
                // disposed while we were holding the connection. Just close it.
            }
            else
            {
                var q = GetQueue(transport.Origin);
                if (q.Count >= MaxPerOrigin)
                {
                    // LRU eviction: drop the oldest entry to make room.
                    evicted = q.First!.Value.Transport;
                    q.RemoveFirst();
                }
                q.AddLast(new Entry(transport, DateTimeOffset.UtcNow));
                transport = null!; // consumed by the pool; do not dispose below
            }
        }

        if (transport is not null)
        {
            await DiscardAsync(transport, _log).ConfigureAwait(false);
        }

        if (evicted is not null)
        {
            await DiscardAsync(evicted, _log).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drop and dispose any pooled transport whose last-used timestamp is
    /// older than <paramref name="now"/> minus <see cref="IdleTimeout"/>. When
    /// <paramref name="now"/> is null, <see cref="DateTimeOffset.UtcNow"/> is
    /// used.
    /// </summary>
    /// <returns>Count of transports drained.</returns>
    public async ValueTask<int> DrainExpiredAsync(DateTimeOffset? now = null)
    {
        var threshold = (now ?? DateTimeOffset.UtcNow) - IdleTimeout;
        List<IHttpTransport>? expired = null;

        lock (_gate)
        {
            foreach (var (_, q) in _byOrigin)
            {
                // Entries are appended in arrival order, which is also
                // last-used order — keep popping from the front while stale.
                while (q.First is { } first && first.Value.LastUsed <= threshold)
                {
                    (expired ??= []).Add(first.Value.Transport);
                    q.RemoveFirst();
                }
            }
        }

        if (expired is null)
        {
            return 0;
        }

        foreach (var t in expired)
        {
            await DiscardAsync(t, _log).ConfigureAwait(false);
        }

        return expired.Count;
    }

    /// <summary>
    /// Drop and dispose every pooled transport. The pool itself stays usable
    /// (e.g. for test scenarios that drain between assertions); call
    /// <see cref="DisposeAsync"/> to additionally mark it as no longer
    /// accepting new releases.
    /// </summary>
    public async ValueTask DisposeAllAsync()
    {
        List<IHttpTransport>? toClose = null;
        lock (_gate)
        {
            foreach (var (_, q) in _byOrigin)
            {
                foreach (var entry in q)
                {
                    (toClose ??= []).Add(entry.Transport);
                }

                q.Clear();
            }
            _byOrigin.Clear();
        }
        if (toClose is null)
        {
            return;
        }

        foreach (var t in toClose)
        {
            await DiscardAsync(t, _log).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeAllAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _disposed = true;
        }
    }

    private LinkedList<Entry> GetQueue(OriginKey origin)
    {
        if (!_byOrigin.TryGetValue(origin, out var q))
        {
            q = new LinkedList<Entry>();
            _byOrigin[origin] = q;
        }
        return q;
    }

    private static async ValueTask DiscardAsync(IHttpTransport transport, ILogger log)
    {
        try { await transport.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { ConnectionPoolLog.DiscardFailed(log, ex); /* a stale socket may throw on shutdown; pooling doesn't care */ }
    }

    private readonly record struct Entry(IHttpTransport Transport, DateTimeOffset LastUsed);
}
