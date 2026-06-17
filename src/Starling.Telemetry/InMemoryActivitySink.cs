using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

namespace Starling.Telemetry;

/// <summary>
/// A captured activity (span). Holds just enough for the Performance / Internals
/// DevTools panels to render flame rows, web-vitals markers, and module-chip
/// counts without referencing System.Diagnostics types in the UI layer.
/// </summary>
public readonly record struct ActivityRecord(
    DateTime StartUtc,
    TimeSpan Duration,
    string Source,
    string OperationName,
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    ActivityStatusCode Status,
    IReadOnlyList<KeyValuePair<string, object?>> Tags);

/// <summary>
/// Captures completed activities into a bounded ring buffer + observable.
/// Registers an <see cref="ActivityListener"/> at construction so it's
/// always-on. One listener can coexist with OpenTelemetry's own, and both receive the
/// stop event). Subscribers see activities only after Stopped fires, which
/// matches Aspire's trace timeline.
/// </summary>
public sealed class InMemoryActivitySink : IDisposable
{
    private const int Capacity = 2000;

    private readonly ActivityRecord[] _buffer = new ActivityRecord[Capacity];
    private readonly object _gate = new();
    private int _head;
    private int _count;
    private readonly ActivityListener? _listener;
    private readonly HashSet<string> _sources;
    private readonly ConcurrentBag<Channel<ActivityRecord>> _subscribers = [];
    private bool _disposed;

    public InMemoryActivitySink(params string[] sources) : this(attachListener: true, sources) { }

    /// <summary>
    /// Construct the sink, optionally skipping the in-process
    /// <see cref="ActivityListener"/>. Pass <paramref name="attachListener"/> =
    /// false when the buffer is fed exclusively via <see cref="Ingest"/> (e.g.
    /// the telemetry daemon receiving spans over the OpenTelemetry Protocol from another process) so
    /// the daemon's own activities don't leak into the ring buffer.
    /// </summary>
    public InMemoryActivitySink(bool attachListener, params string[] sources)
    {
        _sources = new HashSet<string>(sources, StringComparer.Ordinal);
        if (!attachListener)
        {
            return;
        }

        _listener = new ActivityListener
        {
            ShouldListenTo = src => _sources.Contains(src.Name),
            // Sample == AllData so tags survive. AllDataAndRecorded would also
            // mark the activity Recorded which can affect downstream exporters;
            // AllData is safer for a passive observer.
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData,
            ActivityStopped = OnStopped,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>
    /// Append an externally-sourced span record, such as one decoded from the OpenTelemetry Protocol, into
    /// the ring buffer and fan it out to subscribers — the same path the local
    /// <see cref="ActivityListener"/> uses, so daemon-ingested spans read back
    /// identically through <see cref="Snapshot"/>.
    /// </summary>
    public void Ingest(ActivityRecord record) => Store(record);

    public ActivityRecord[] Snapshot()
    {
        lock (_gate)
        {
            var result = new ActivityRecord[_count];
            var start = (_head - _count + Capacity) % Capacity;
            for (var i = 0; i < _count; i++)
            {
                result[i] = _buffer[(start + i) % Capacity];
            }

            return result;
        }
    }

    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<ActivityRecord>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _subscribers.Add(channel);
        return new Subscription(channel);
    }

    private void OnStopped(Activity activity)
    {
        // Snapshot the tag list once — Activity.TagObjects is a struct enumerator
        // that re-walks the linked list every call.
        var tags = new List<KeyValuePair<string, object?>>();
        foreach (var t in activity.TagObjects)
        {
            tags.Add(t);
        }

        var record = new ActivityRecord(
            activity.StartTimeUtc,
            activity.Duration,
            activity.Source.Name,
            activity.OperationName,
            activity.TraceId.ToString(),
            activity.SpanId.ToString(),
            activity.ParentSpanId == default ? null : activity.ParentSpanId.ToString(),
            activity.Status,
            tags);

        Store(record);
    }

    private void Store(ActivityRecord record)
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
        _listener?.Dispose();
        foreach (var subscriber in _subscribers)
        {
            subscriber.Writer.TryComplete();
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly Channel<ActivityRecord> _channel;
        public ChannelReader<ActivityRecord> Reader => _channel.Reader;

        internal Subscription(Channel<ActivityRecord> channel) => _channel = channel;

        public void Dispose() => _channel.Writer.TryComplete();
    }
}
