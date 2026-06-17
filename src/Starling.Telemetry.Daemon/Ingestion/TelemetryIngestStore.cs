namespace Starling.Telemetry.Daemon.Ingestion;

/// <summary>
/// The daemon's in-memory home for everything it receives over the
/// OpenTelemetry Protocol. Holds the three ring-buffer sinks in ingestion-only
/// mode (no local listeners) and the <see cref="TelemetryStream"/> facade over
/// them, so the existing MCP telemetry tools and the analyzer
/// read received data exactly as they read a host's own telemetry. Also tracks
/// lightweight liveness/throughput counters for the console heartbeat and the
/// /api/summary endpoint.
/// </summary>
internal sealed class TelemetryIngestStore : IDisposable
{
    public InMemoryActivitySink Activities { get; }
    public InMemoryMeterSink Metrics { get; }
    public InMemoryLogSink Logs { get; }
    public TelemetryStream Stream { get; }

    private long _spansIngested;
    private long _metricsIngested;
    private long _logsIngested;
    private long _firstReceivedTicks;
    private long _lastReceivedTicks;

    public TelemetryIngestStore()
    {
        // attachListener: false. These buffers are fed only by OpenTelemetry
        // Protocol ingest, so the daemon's own ASP.NET/gRPC activities never
        // pollute the data.
        Activities = new InMemoryActivitySink(attachListener: false);
        Metrics = new InMemoryMeterSink(attachListener: false);
        Logs = new InMemoryLogSink();
        Stream = new TelemetryStream(Logs, Activities, Metrics);
    }

    public long SpansIngested => Interlocked.Read(ref _spansIngested);
    public long MetricsIngested => Interlocked.Read(ref _metricsIngested);
    public long LogsIngested => Interlocked.Read(ref _logsIngested);

    public DateTime? FirstReceivedUtc => TicksToUtc(Interlocked.Read(ref _firstReceivedTicks));
    public DateTime? LastReceivedUtc => TicksToUtc(Interlocked.Read(ref _lastReceivedTicks));

    public void IngestSpans(IReadOnlyList<ActivityRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        foreach (var r in records)
        {
            Activities.Ingest(r);
        }

        Interlocked.Add(ref _spansIngested, records.Count);
        Touch();
    }

    public void IngestMetrics(IReadOnlyList<MeterRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        foreach (var r in records)
        {
            Metrics.Ingest(r);
        }

        Interlocked.Add(ref _metricsIngested, records.Count);
        Touch();
    }

    public void IngestLogs(IReadOnlyList<LogRecord> records)
    {
        if (records.Count == 0)
        {
            return;
        }

        foreach (var r in records)
        {
            Logs.Ingest(r);
        }

        Interlocked.Add(ref _logsIngested, records.Count);
        Touch();
    }

    private void Touch()
    {
        var now = DateTime.UtcNow.Ticks;
        Interlocked.CompareExchange(ref _firstReceivedTicks, now, 0);
        Interlocked.Exchange(ref _lastReceivedTicks, now);
    }

    private static DateTime? TicksToUtc(long ticks)
        => ticks == 0 ? null : new DateTime(ticks, DateTimeKind.Utc);

    public void Dispose()
    {
        Stream.Dispose();
    }
}
