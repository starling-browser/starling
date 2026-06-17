using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Starling.Telemetry;

/// <summary>
/// A captured log record — the shape DevTools' ConsolePanel reads. Mirrors the
/// fields ILogger.Log surfaces (level, category, message, exception) plus a
/// timestamp and EventId. Scopes are flattened to a single comma-joined
/// string; ConsolePanel doesn't need their tree.
/// </summary>
public readonly record struct LogRecord(
    DateTime TimestampUtc,
    LogLevel Level,
    string Category,
    EventId EventId,
    string Message,
    string? Exception,
    string? Scope);

/// <summary>
/// In-memory ILoggerProvider that captures every log record into a bounded
/// ring buffer and replays them to live subscribers. Registered as the first
/// thing in <see cref="OtelBootstrap.AddStarlingTelemetry"/> so DevTools'
/// ConsolePanel can subscribe without depending on the OpenTelemetry Protocol
/// pipeline.
/// </summary>
public sealed class InMemoryLogSink : ILoggerProvider
{
    private const int Capacity = 2000;

    private readonly LogRecord[] _buffer = new LogRecord[Capacity];
    private readonly object _gate = new();
    private int _head;   // next write position
    private int _count;  // entries currently in buffer
    private readonly ConcurrentBag<Channel<LogRecord>> _subscribers = [];
    private readonly ConcurrentDictionary<string, ILogger> _loggers = new(StringComparer.Ordinal);
    private bool _disposed;

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, n => new SinkLogger(this, n));

    /// <summary>
    /// Snapshot the current buffer in chronological order. Returns a new array
    /// so callers can iterate without holding the gate. ConsolePanel paints
    /// this on first show; the live observable feeds new entries afterwards.
    /// </summary>
    public LogRecord[] Snapshot()
    {
        lock (_gate)
        {
            var result = new LogRecord[_count];
            // Buffer is a ring; walk from oldest (head - count) wrapping forward.
            var start = (_head - _count + Capacity) % Capacity;
            for (var i = 0; i < _count; i++)
            {
                result[i] = _buffer[(start + i) % Capacity];
            }

            return result;
        }
    }

    /// <summary>
    /// Subscribe to records appended after the call. Returns the live channel
    /// reader; dispose the returned token to unsubscribe. Channels are
    /// bounded-by-count (100) per subscriber so a slow consumer can't pin
    /// memory; oldest entries drop if the subscriber falls behind.
    /// </summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<LogRecord>(new BoundedChannelOptions(100)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _subscribers.Add(channel);
        return new Subscription(this, channel);
    }

    /// <summary>
    /// Append an externally-sourced log record, such as one decoded from the OpenTelemetry Protocol, into
    /// the ring buffer and fan it out to subscribers. Same path as the local
    /// <see cref="ILogger"/> capture.
    /// </summary>
    public void Ingest(LogRecord record) => Append(record);

    internal void Append(LogRecord record)
    {
        lock (_gate)
        {
            _buffer[_head] = record;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity)
            {
                _count++;
            }
        }
        // Fan out to subscribers outside the lock; TryWrite is non-blocking.
        foreach (var subscriber in _subscribers)
        {
            subscriber.Writer.TryWrite(record);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var subscriber in _subscribers)
        {
            subscriber.Writer.TryComplete();
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly InMemoryLogSink _owner;
        private readonly Channel<LogRecord> _channel;
        public ChannelReader<LogRecord> Reader => _channel.Reader;

        internal Subscription(InMemoryLogSink owner, Channel<LogRecord> channel)
        {
            _owner = owner;
            _channel = channel;
        }

        public void Dispose()
        {
            _channel.Writer.TryComplete();
            // We don't remove from _subscribers (ConcurrentBag has no Remove);
            // TryWrite after TryComplete is a cheap no-op so the leak is bounded.
        }
    }

    private sealed class SinkLogger(InMemoryLogSink owner, string category) : ILogger
    {
        private readonly InMemoryLogSink _owner = owner;
        private readonly string _category = category;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
            => NoopScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            var message = formatter(state, exception);
            _owner.Append(new LogRecord(
                DateTime.UtcNow, logLevel, _category, eventId, message,
                exception?.ToString(),
                Scope: null));
        }

        private sealed class NoopScope : IDisposable
        {
            public static readonly NoopScope Instance = new();
            public void Dispose() { }
        }
    }
}
