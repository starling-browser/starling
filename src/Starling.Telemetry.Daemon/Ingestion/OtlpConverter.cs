using System.Diagnostics;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using OpenTelemetry.Proto.Common.V1;
using OpenTelemetry.Proto.Logs.V1;
using OpenTelemetry.Proto.Metrics.V1;
using OtlpStatusCode = OpenTelemetry.Proto.Trace.V1.Status.Types.StatusCode;

namespace Starling.Telemetry.Daemon.Ingestion;

/// <summary>
/// Converts decoded OpenTelemetry Protocol export requests into the in-memory
/// record types the rest of Starling already speaks (<see cref="ActivityRecord"/>,
/// <see cref="MeterRecord"/>, <see cref="LogRecord"/>). Keeping the daemon on
/// the same record shapes means the existing MCP telemetry
/// tools and the analyzer read daemon-ingested data with zero special-casing.
/// </summary>
internal static class OtlpConverter
{
    public static List<ActivityRecord> ToActivityRecords(ExportTraceServiceRequest request)
    {
        var records = new List<ActivityRecord>();
        foreach (var rs in request.ResourceSpans)
        {
            var resourceTags = AttributeTags(rs.Resource?.Attributes);
            foreach (var ss in rs.ScopeSpans)
            {
                var source = ss.Scope?.Name ?? string.Empty;
                foreach (var span in ss.Spans)
                {
                    var tags = new List<KeyValuePair<string, object?>>(
                        resourceTags.Count + span.Attributes.Count);
                    tags.AddRange(resourceTags);
                    foreach (var kv in span.Attributes)
                    {
                        tags.Add(new KeyValuePair<string, object?>(kv.Key, AnyValueToObject(kv.Value)));
                    }

                    var start = UnixNanosToUtc(span.StartTimeUnixNano);
                    var duration = span.EndTimeUnixNano > span.StartTimeUnixNano
                        ? TimeSpan.FromTicks((long)((span.EndTimeUnixNano - span.StartTimeUnixNano) / 100))
                        : TimeSpan.Zero;

                    records.Add(new ActivityRecord(
                        start,
                        duration,
                        source,
                        span.Name,
                        HexOrEmpty(span.TraceId),
                        HexOrEmpty(span.SpanId),
                        span.ParentSpanId.IsEmpty ? null : HexOrEmpty(span.ParentSpanId),
                        MapStatus(span.Status?.Code),
                        tags));
                }
            }
        }
        return records;
    }

    public static List<MeterRecord> ToMeterRecords(ExportMetricsServiceRequest request)
    {
        var records = new List<MeterRecord>();
        foreach (var rm in request.ResourceMetrics)
        {
            foreach (var sm in rm.ScopeMetrics)
            {
                var meterName = sm.Scope?.Name ?? string.Empty;
                foreach (var metric in sm.Metrics)
                {
                    // Gauges and sums (counters/up-down-counters) cover the
                    // signals the lag analysis needs: frame time, tile ratios,
                    // and the process CPU/memory gauges. Histograms are skipped.
                    var points = metric.DataCase switch
                    {
                        Metric.DataOneofCase.Gauge => metric.Gauge.DataPoints,
                        Metric.DataOneofCase.Sum => metric.Sum.DataPoints,
                        _ => null,
                    };
                    if (points is null)
                    {
                        continue;
                    }

                    foreach (var p in points)
                    {
                        var value = p.ValueCase switch
                        {
                            NumberDataPoint.ValueOneofCase.AsDouble => p.AsDouble,
                            NumberDataPoint.ValueOneofCase.AsInt => p.AsInt,
                            _ => 0d,
                        };
                        records.Add(new MeterRecord(
                            UnixNanosToUtc(p.TimeUnixNano),
                            meterName,
                            metric.Name,
                            metric.Unit ?? string.Empty,
                            value,
                            AttributeTags(p.Attributes)));
                    }
                }
            }
        }
        return records;
    }

    public static List<LogRecord> ToLogRecords(ExportLogsServiceRequest request)
    {
        var records = new List<LogRecord>();
        foreach (var rl in request.ResourceLogs)
        {
            foreach (var sl in rl.ScopeLogs)
            {
                var category = sl.Scope?.Name ?? string.Empty;
                foreach (var lr in sl.LogRecords)
                {
                    var ts = lr.TimeUnixNano != 0 ? lr.TimeUnixNano : lr.ObservedTimeUnixNano;
                    string? exception = null;
                    foreach (var kv in lr.Attributes)
                    {
                        if (kv.Key is "exception.stacktrace" or "exception.message")
                        {
                            exception = AnyValueToObject(kv.Value)?.ToString();
                            if (kv.Key == "exception.stacktrace")
                            {
                                break; // prefer full stack
                            }
                        }
                    }

                    records.Add(new LogRecord(
                        UnixNanosToUtc(ts),
                        MapSeverity(lr.SeverityNumber, lr.SeverityText),
                        string.IsNullOrEmpty(category) ? "otlp" : category,
                        default,
                        lr.Body is null ? string.Empty : AnyValueToObject(lr.Body)?.ToString() ?? string.Empty,
                        exception,
                        Scope: null));
                }
            }
        }
        return records;
    }

    private static List<KeyValuePair<string, object?>> AttributeTags(
        IEnumerable<KeyValue>? attributes)
    {
        var tags = new List<KeyValuePair<string, object?>>();
        if (attributes is null)
        {
            return tags;
        }

        foreach (var kv in attributes)
        {
            tags.Add(new KeyValuePair<string, object?>(kv.Key, AnyValueToObject(kv.Value)));
        }

        return tags;
    }

    private static object? AnyValueToObject(AnyValue? value)
    {
        if (value is null)
        {
            return null;
        }

        return value.ValueCase switch
        {
            AnyValue.ValueOneofCase.StringValue => value.StringValue,
            AnyValue.ValueOneofCase.BoolValue => value.BoolValue,
            AnyValue.ValueOneofCase.IntValue => value.IntValue,
            AnyValue.ValueOneofCase.DoubleValue => value.DoubleValue,
            AnyValue.ValueOneofCase.BytesValue => HexOrEmpty(value.BytesValue),
            AnyValue.ValueOneofCase.ArrayValue =>
                string.Join(",", value.ArrayValue.Values.Select(AnyValueToObject)),
            AnyValue.ValueOneofCase.KvlistValue =>
                string.Join(",", value.KvlistValue.Values.Select(kv => $"{kv.Key}={AnyValueToObject(kv.Value)}")),
            _ => null,
        };
    }

    private static string HexOrEmpty(Google.Protobuf.ByteString bytes)
        => bytes.IsEmpty ? string.Empty : Convert.ToHexStringLower(bytes.Span);

    // OpenTelemetry Protocol timestamps are nanoseconds since the Unix epoch;
    // one tick is 100 ns.
    private static DateTime UnixNanosToUtc(ulong nanos)
        => nanos == 0 ? DateTime.UtcNow : DateTime.UnixEpoch.AddTicks((long)(nanos / 100));

    private static ActivityStatusCode MapStatus(OtlpStatusCode? code) => code switch
    {
        OtlpStatusCode.Ok => ActivityStatusCode.Ok,
        OtlpStatusCode.Error => ActivityStatusCode.Error,
        _ => ActivityStatusCode.Unset,
    };

    private static LogLevel MapSeverity(SeverityNumber severity, string? severityText)
    {
        // SEVERITY_NUMBER_UNSPECIFIED (0) means "unknown" per the OpenTelemetry
        // Protocol logs spec. Never assume Information. Honor a textual severity
        // if the producer set one, else treat the record as the lowest level
        // (Trace).
        if (severity == SeverityNumber.Unspecified)
        {
            return ParseSeverityText(severityText) ?? LogLevel.Trace;
        }

        return (int)severity switch
        {
            >= 21 => LogLevel.Critical,
            >= 17 => LogLevel.Error,
            >= 13 => LogLevel.Warning,
            >= 9 => LogLevel.Information,
            >= 5 => LogLevel.Debug,
            _ => LogLevel.Trace, // 1..4
        };
    }

    private static LogLevel? ParseSeverityText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        if (t.Equals("TRACE", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Trace;
        }

        if (t.Equals("DEBUG", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Debug;
        }

        if (t.Equals("INFO", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("INFORMATION", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Information;
        }

        if (t.Equals("WARN", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("WARNING", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Warning;
        }

        if (t.Equals("ERROR", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Error;
        }

        if (t.Equals("FATAL", StringComparison.OrdinalIgnoreCase) ||
            t.Equals("CRITICAL", StringComparison.OrdinalIgnoreCase))
        {
            return LogLevel.Critical;
        }

        return null;
    }
}
