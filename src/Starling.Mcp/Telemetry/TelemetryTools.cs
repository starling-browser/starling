using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Starling.Telemetry;

namespace Starling.Mcp.Telemetry;

/// <summary>
/// MCP tool group that delivers Starling's own OpenTelemetry
/// signal (spans, logs, metrics) to driving agents. Reads from the in-memory ring buffers
/// behind <see cref="TelemetryStream"/> — the same buffers the GUI DevTools
/// panels render — so calls are pure snapshots with no exporter dependency.
/// </summary>
public sealed class TelemetryTools : IMcpToolGroup
{
    public const string TracesToolName = "browser_telemetry_traces";
    public const string LogsToolName = "browser_telemetry_logs";
    public const string MetricsToolName = "browser_telemetry_metrics";
    public const string DescribeToolName = "browser_telemetry_describe";

    private const int DefaultLimit = 200;
    private const int MetricsDefaultLimit = 500;

    private readonly TelemetryStream _telemetry;

    public TelemetryTools(TelemetryStream telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        _telemetry = telemetry;
    }

    public string GetToolDescriptorsJson() => ToolDescriptorsJson;

    public bool HasTool(string name) => name switch
    {
        TracesToolName or LogsToolName or MetricsToolName or DescribeToolName => true,
        _ => false,
    };

    public Task<McpToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct)
        => name switch
        {
            TracesToolName => Task.FromResult(new McpToolResult(BuildTracesPayload(arguments))),
            LogsToolName => Task.FromResult(new McpToolResult(BuildLogsPayload(arguments))),
            MetricsToolName => Task.FromResult(new McpToolResult(BuildMetricsPayload(arguments))),
            DescribeToolName => Task.FromResult(new McpToolResult(BuildDescribePayload())),
            _ => throw new ArgumentException($"Unknown telemetry tool: {name}", nameof(name)),
        };

    internal JsonObject BuildTracesPayload(JsonElement arguments, int? limitOverride = null)
    {
        var sinceMs = McpArgumentReader.ReadOptionalLong(arguments, "sinceUnixMs");
        var sourceFilter = McpArgumentReader.ReadOptionalString(arguments, "source");
        var minDurationMs = McpArgumentReader.ReadOptionalDouble(arguments, "minDurationMs");
        var statusError = McpArgumentReader.ReadBool(arguments, "statusError");
        var limit = ClampLimit(limitOverride ?? McpArgumentReader.ReadIntOr(arguments, "limit", DefaultLimit));

        var snapshot = _telemetry.Activities.Snapshot();
        var traces = new JsonArray();
        var count = 0;
        // Walk newest-first so a small limit returns the most recent activity.
        // Then reverse the assembled array so the response is start-time
        // ascending — the natural shape for trace timelines.
        var picked = new List<JsonObject>();
        for (var i = snapshot.Length - 1; i >= 0 && count < limit; i--)
        {
            var record = snapshot[i];
            if (sinceMs is { } since && record.StartUtc < FromUnixMs(since))
            {
                continue;
            }

            if (sourceFilter is { Length: > 0 } source && record.Source != source)
            {
                continue;
            }

            if (minDurationMs is { } md && record.Duration.TotalMilliseconds < md)
            {
                continue;
            }

            if (statusError && record.Status != ActivityStatusCode.Error)
            {
                continue;
            }

            picked.Add(ActivityRecordToJson(record));
            count++;
        }
        picked.Reverse();
        foreach (var entry in picked)
        {
            traces.Add(entry);
        }

        return new JsonObject
        {
            ["traces"] = traces,
            ["count"] = traces.Count,
            ["truncated"] = traces.Count >= limit,
        };
    }

    internal JsonObject BuildLogsPayload(JsonElement arguments, int? limitOverride = null)
    {
        var sinceMs = McpArgumentReader.ReadOptionalLong(arguments, "sinceUnixMs");
        var categoryFilter = McpArgumentReader.ReadOptionalString(arguments, "category");
        var minLevelText = McpArgumentReader.ReadOptionalString(arguments, "minLevel");
        var minLevel = ParseLogLevel(minLevelText);
        var limit = ClampLimit(limitOverride ?? McpArgumentReader.ReadIntOr(arguments, "limit", DefaultLimit));

        var snapshot = _telemetry.Logs.Snapshot();
        var picked = new List<JsonObject>();
        var count = 0;
        for (var i = snapshot.Length - 1; i >= 0 && count < limit; i--)
        {
            var record = snapshot[i];
            if (sinceMs is { } since && record.TimestampUtc < FromUnixMs(since))
            {
                continue;
            }

            if (categoryFilter is { Length: > 0 } cat && record.Category != cat)
            {
                continue;
            }

            if (minLevel is { } floor && record.Level < floor)
            {
                continue;
            }

            picked.Add(LogRecordToJson(record));
            count++;
        }
        picked.Reverse();

        var logs = new JsonArray();
        foreach (var entry in picked)
        {
            logs.Add(entry);
        }

        return new JsonObject
        {
            ["logs"] = logs,
            ["count"] = logs.Count,
            ["truncated"] = logs.Count >= limit,
        };
    }

    internal JsonObject BuildMetricsPayload(JsonElement arguments, int? limitOverride = null)
    {
        var sinceMs = McpArgumentReader.ReadOptionalLong(arguments, "sinceUnixMs");
        var instrumentFilter = McpArgumentReader.ReadOptionalString(arguments, "instrument");
        var limit = ClampLimit(limitOverride ?? McpArgumentReader.ReadIntOr(arguments, "limit", MetricsDefaultLimit));

        var snapshot = _telemetry.Metrics.Snapshot();
        var picked = new List<JsonObject>();
        var count = 0;
        for (var i = snapshot.Length - 1; i >= 0 && count < limit; i--)
        {
            var record = snapshot[i];
            if (sinceMs is { } since && record.TimestampUtc < FromUnixMs(since))
            {
                continue;
            }

            if (instrumentFilter is { Length: > 0 } inst && record.InstrumentName != inst)
            {
                continue;
            }

            picked.Add(MeterRecordToJson(record));
            count++;
        }
        picked.Reverse();

        var measurements = new JsonArray();
        foreach (var entry in picked)
        {
            measurements.Add(entry);
        }

        return new JsonObject
        {
            ["measurements"] = measurements,
            ["count"] = measurements.Count,
            ["truncated"] = measurements.Count >= limit,
        };
    }

    internal JsonObject BuildDescribePayload()
    {
        var activitySources = new HashSet<string>(StringComparer.Ordinal);
        var spanNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in _telemetry.Activities.Snapshot())
        {
            activitySources.Add(record.Source);
            spanNames.Add(record.OperationName);
        }

        var logCategories = new HashSet<string>(StringComparer.Ordinal);
        var minLogLevel = LogLevel.None;
        var maxLogLevel = LogLevel.Trace;
        foreach (var record in _telemetry.Logs.Snapshot())
        {
            logCategories.Add(record.Category);
            if (record.Level < minLogLevel)
            {
                minLogLevel = record.Level;
            }

            if (record.Level > maxLogLevel)
            {
                maxLogLevel = record.Level;
            }
        }

        var meterNames = new HashSet<string>(StringComparer.Ordinal);
        var instruments = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in _telemetry.Metrics.Snapshot())
        {
            meterNames.Add(record.MeterName);
            instruments.Add($"{record.MeterName}:{record.InstrumentName}");
        }

        return new JsonObject
        {
            ["activitySources"] = ToSortedJsonArray(activitySources),
            ["spanNames"] = ToSortedJsonArray(spanNames),
            ["logCategories"] = ToSortedJsonArray(logCategories),
            ["meters"] = ToSortedJsonArray(meterNames),
            ["instruments"] = ToSortedJsonArray(instruments),
            ["logLevelObserved"] = new JsonObject
            {
                ["min"] = minLogLevel == LogLevel.None ? null : minLogLevel.ToString(),
                ["max"] = maxLogLevel.ToString(),
            },
            ["bufferCapacity"] = 2000,
        };
    }

    private static JsonArray ToSortedJsonArray(HashSet<string> values)
    {
        var array = new JsonArray();
        foreach (var v in values.OrderBy(s => s, StringComparer.Ordinal))
        {
            // Cast to JsonNode? so the non-generic JsonArray.Add(JsonNode?)
            // overload wins. The Add<T> overload routes through
            // JsonValue.Create<T> → JsonSerializer reflection, which is
            // disabled in the AOT-published GUI (PublishAot=true also flips
            // JsonSerializer.IsReflectionEnabledByDefault to false at runtime).
            array.Add((JsonNode?)v);
        }
        return array;
    }

    private static JsonObject ActivityRecordToJson(ActivityRecord record)
    {
        var tags = new JsonObject();
        foreach (var kv in record.Tags)
        {
            tags[kv.Key] = TagValue(kv.Value);
        }

        return new JsonObject
        {
            ["traceId"] = record.TraceId,
            ["spanId"] = record.SpanId,
            ["parentSpanId"] = record.ParentSpanId,
            ["source"] = record.Source,
            ["name"] = record.OperationName,
            ["startUnixMs"] = ToUnixMs(record.StartUtc),
            ["durationMs"] = record.Duration.TotalMilliseconds,
            ["status"] = record.Status.ToString(),
            ["tags"] = tags,
        };
    }

    private static JsonObject LogRecordToJson(LogRecord record) => new()
    {
        ["timestampUnixMs"] = ToUnixMs(record.TimestampUtc),
        ["level"] = record.Level.ToString(),
        ["category"] = record.Category,
        ["eventId"] = record.EventId.Id,
        ["eventName"] = record.EventId.Name,
        ["message"] = record.Message,
        ["exception"] = record.Exception,
        ["scope"] = record.Scope,
    };

    private static JsonObject MeterRecordToJson(MeterRecord record)
    {
        var tags = new JsonObject();
        foreach (var kv in record.Tags)
        {
            tags[kv.Key] = TagValue(kv.Value);
        }

        return new JsonObject
        {
            ["timestampUnixMs"] = ToUnixMs(record.TimestampUtc),
            ["meter"] = record.MeterName,
            ["instrument"] = record.InstrumentName,
            ["unit"] = record.Unit,
            ["value"] = record.Value,
            ["tags"] = tags,
        };
    }

    private static JsonNode? TagValue(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b,
        int i => i,
        long l => l,
        double d => d,
        float f => (double)f,
        _ => value.ToString(),
    };

    private static long ToUnixMs(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static DateTime FromUnixMs(long ms)
        => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private static LogLevel? ParseLogLevel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return Enum.TryParse<LogLevel>(text, ignoreCase: true, out var level) ? level : null;
    }

    private static int ClampLimit(int requested)
    {
        if (requested <= 0)
        {
            return DefaultLimit;
        }

        if (requested > 2000)
        {
            return 2000;
        }

        return requested;
    }

    private const string ToolDescriptorsJson = """
        [
          {
            "name": "browser_telemetry_traces",
            "description": "Return recent Starling engine spans (browser activity records) from the in-memory ring buffer. Use to inspect a page load timeline: render, fetch, parse, layout, paint, JS execute. Results sorted by start time ascending; newest entries returned first when results are truncated by `limit`.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "sinceUnixMs": { "type": "integer", "description": "Only return spans whose start time is at or after this epoch milliseconds." },
                "source": { "type": "string", "description": "Filter by ActivitySource name, e.g. `Starling.Engine`." },
                "minDurationMs": { "type": "number", "description": "Only return spans whose duration is at least this many milliseconds." },
                "statusError": { "type": "boolean", "description": "If true, only return spans with status=Error." },
                "limit": { "type": "integer", "description": "Maximum number of spans to return. Defaults to 200, capped at 2000." }
              }
            }
          },
          {
            "name": "browser_telemetry_logs",
            "description": "Return recent Starling engine log records from the in-memory ring buffer. Includes engine traces, JS console output (under `Starling.engine.js`), and exception detail.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "sinceUnixMs": { "type": "integer", "description": "Only return logs whose timestamp is at or after this epoch milliseconds." },
                "category": { "type": "string", "description": "Filter by exact logger category, e.g. `Starling.engine.js`." },
                "minLevel": { "type": "string", "description": "Minimum log level (Trace, Debug, Information, Warning, Error, Critical)." },
                "limit": { "type": "integer", "description": "Maximum number of logs to return. Defaults to 200, capped at 2000." }
              }
            }
          },
          {
            "name": "browser_telemetry_metrics",
            "description": "Return recent metric measurements captured from Starling's Meter instruments (counters, histograms) in the in-memory ring buffer.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "sinceUnixMs": { "type": "integer", "description": "Only return measurements at or after this epoch milliseconds." },
                "instrument": { "type": "string", "description": "Filter by exact instrument name, e.g. `page_load`." },
                "limit": { "type": "integer", "description": "Maximum number of measurements to return. Defaults to 500, capped at 2000." }
              }
            }
          },
          {
            "name": "browser_telemetry_describe",
            "description": "Summarise the telemetry currently captured: distinct ActivitySource names, span names, log categories, meters, and instruments. Use this first to discover what filter values the other telemetry tools accept.",
            "inputSchema": { "type": "object", "properties": {} }
          }
        ]
        """;
}
