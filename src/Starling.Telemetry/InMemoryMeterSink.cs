using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;

namespace Starling.Telemetry;

/// <summary>
/// One measurement from any instrument the sink subscribed to. Tag values
/// are captured as their object form (the Meter API surfaces them as
/// <c>ReadOnlySpan&lt;KeyValuePair&lt;string, object?&gt;&gt;</c>) so DevTools
/// can group/filter without knowing the instrument's signature.
/// </summary>
public readonly record struct MeterRecord(
    DateTime TimestampUtc,
    string MeterName,
    string InstrumentName,
    string Unit,
    double Value,
    IReadOnlyList<KeyValuePair<string, object?>> Tags);

/// <summary>
/// Captures metric measurements into a bounded ring buffer + observable.
/// Wraps <see cref="MeterListener"/>, filtered by meter name. Boxes
/// non-double values (int/long/float) via <c>Convert.ToDouble</c> so callers
/// see one record type.
/// </summary>
public sealed class InMemoryMeterSink : IDisposable
{
    private const int Capacity = 2000;

    private readonly MeterRecord[] _buffer = new MeterRecord[Capacity];
    private readonly object _gate = new();
    private int _head;
    private int _count;
    private readonly MeterListener? _listener;
    private readonly HashSet<string> _meters;
    private readonly ConcurrentBag<Channel<MeterRecord>> _subscribers = [];
    private bool _disposed;

    public InMemoryMeterSink(params string[] meters) : this(attachListener: true, meters) { }

    /// <summary>
    /// Construct the sink, optionally skipping the in-process
    /// <see cref="MeterListener"/>. Pass <paramref name="attachListener"/> =
    /// false when measurements are fed exclusively via <see cref="Ingest"/>
    /// (for example, the telemetry daemon receiving metrics over the OpenTelemetry Protocol).
    /// </summary>
    public InMemoryMeterSink(bool attachListener, params string[] meters)
    {
        _meters = new HashSet<string>(meters, StringComparer.Ordinal);
        if (!attachListener)
        {
            return;
        }

        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (_meters.Contains(instrument.Meter.Name))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        // One callback per primitive numeric type; route them all through
        // RecordMeasurement which casts to double. MeterListener requires
        // each type to be wired explicitly — there is no generic overload.
        _listener.SetMeasurementEventCallback<double>(RecordMeasurement);
        _listener.SetMeasurementEventCallback<float>((i, v, t, s) => RecordMeasurement(i, v, t, s));
        _listener.SetMeasurementEventCallback<long>((i, v, t, s) => RecordMeasurement(i, v, t, s));
        _listener.SetMeasurementEventCallback<int>((i, v, t, s) => RecordMeasurement(i, v, t, s));
        _listener.SetMeasurementEventCallback<short>((i, v, t, s) => RecordMeasurement(i, v, t, s));
        _listener.SetMeasurementEventCallback<byte>((i, v, t, s) => RecordMeasurement(i, v, t, s));
        _listener.Start();
    }

    public MeterRecord[] Snapshot()
    {
        lock (_gate)
        {
            var result = new MeterRecord[_count];
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
        var channel = Channel.CreateBounded<MeterRecord>(new BoundedChannelOptions(200)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _subscribers.Add(channel);
        return new Subscription(channel);
    }

    private void RecordMeasurement<T>(
        Instrument instrument,
        T measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
        where T : struct
    {
        var value = Convert.ToDouble(measurement, System.Globalization.CultureInfo.InvariantCulture);
        var tagList = tags.Length == 0
            ? Array.Empty<KeyValuePair<string, object?>>()
            : tags.ToArray();

        var record = new MeterRecord(
            DateTime.UtcNow,
            instrument.Meter.Name,
            instrument.Name,
            instrument.Unit ?? string.Empty,
            value,
            tagList);

        Store(record);
    }

    /// <summary>
    /// Append an externally-sourced measurement, such as one decoded from the OpenTelemetry Protocol, into
    /// the ring buffer and fan it out to subscribers.
    /// </summary>
    public void Ingest(MeterRecord record) => Store(record);

    private void Store(MeterRecord record)
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
        private readonly Channel<MeterRecord> _channel;
        public ChannelReader<MeterRecord> Reader => _channel.Reader;

        internal Subscription(Channel<MeterRecord> channel) => _channel = channel;

        public void Dispose() => _channel.Writer.TryComplete();
    }
}
