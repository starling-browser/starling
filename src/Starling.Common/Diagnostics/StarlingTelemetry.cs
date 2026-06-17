// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Starling.Common.Diagnostics;

/// <summary>
/// Process-wide tracing and metrics facade for engine code. This is the
/// non-logging half of what the old <c>IDiagnostics</c> sink carried: spans
/// (<see cref="Span"/>), counters/gauges (<see cref="Counter"/>/<see cref="Gauge"/>),
/// and snapshots. Logging now goes straight through <c>ILogger</c> (source-generated
/// <c>[LoggerMessage]</c> methods) — see the host wiring in Starling.Telemetry.
///
/// <para>The <see cref="ActivitySource"/> and <see cref="Meter"/> are static and
/// shared so any library can record without threading a sink through its
/// constructors. The OpenTelemetry SDK and the DevTools in-memory sinks attach
/// listeners to <see cref="SourceName"/>; nothing is exported until a listener is
/// present, so the calls are cheap no-ops in tests and plain runs.</para>
/// </summary>
public static class StarlingTelemetry
{
    /// <summary>Single source name for engine/layout/paint/css spans + metrics.
    /// The host's OpenTelemetry tracer/meter registers this via AddSource/AddMeter,
    /// and the DevTools sinks listen on it.</summary>
    public const string SourceName = "Starling.Engine";

    public static readonly ActivitySource Source = new(SourceName);
    public static readonly Meter Meter = new(SourceName);

    private static readonly ConcurrentDictionary<string, Counter<double>> s_counters = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Gauge<double>> s_gauges = new(StringComparer.Ordinal);

    /// <summary>
    /// Opens a trace span named <c>{area}.{operation}</c>. The returned value is
    /// the <see cref="Activity"/> itself (disposing it ends the span); when no
    /// listener is attached <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
    /// returns null, so we hand back a no-op disposable to keep the call site simple.
    /// </summary>
    public static IDisposable Span(string area, string operation)
    {
        var activity = Source.StartActivity($"{area}.{operation}", ActivityKind.Internal);
        return activity ?? DiagnosticScope.Noop;
    }

    /// <summary>Adds <paramref name="value"/> to a monotonic counter instrument.</summary>
    public static void Counter(string name, double value)
    {
        var counter = s_counters.GetOrAdd(name, static n => Meter.CreateCounter<double>(n));
        counter.Add(value);
    }

    /// <summary>
    /// Records the latest value of a gauge — a sampled level that rises and falls
    /// (FPS, queue depth, memory), unlike <see cref="Counter"/> which only
    /// accumulates. Maps to a synchronous <see cref="Gauge{T}"/> (.NET 9+) so the
    /// dashboard graphs the value itself, not a running sum.
    /// </summary>
    public static void Gauge(string name, double value)
    {
        var gauge = s_gauges.GetOrAdd(name, static n => Meter.CreateGauge<double>(n));
        gauge.Record(value);
    }

    /// <summary>
    /// Pins a labelled byte payload (e.g. a rendered frame) onto the active span
    /// as an event so it shows inline in the trace timeline. No-op when no span is
    /// active.
    /// </summary>
    public static void Snapshot(string label, ReadOnlySpan<byte> bytes)
    {
        var current = Activity.Current;
        if (current is null)
        {
            return;
        }

        var tags = new ActivityTagsCollection { { "bytes", bytes.Length } };
        current.AddEvent(new ActivityEvent($"snapshot:{label}", tags: tags));
    }

    /// <summary>
    /// Marks the active span as failed and attaches the exception in the
    /// OpenTelemetry shape (type/message/stacktrace). Call alongside logging a
    /// caught exception when it should also flip the span red in the trace view.
    /// No-op when no span is active.
    /// </summary>
    public static void RecordException(string area, Exception exception, string? message = null)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var current = Activity.Current;
        if (current is null)
        {
            return;
        }

        var tags = new ActivityTagsCollection
        {
            { "exception.type", exception.GetType().FullName },
            { "exception.message", exception.Message },
            { "exception.stacktrace", exception.ToString() },
            { "area", area },
        };
        current.AddEvent(new ActivityEvent("exception", tags: tags));
        current.SetStatus(ActivityStatusCode.Error, message ?? exception.Message);
    }
}
