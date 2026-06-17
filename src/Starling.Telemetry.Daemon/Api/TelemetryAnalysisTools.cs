using System.Text.Json;
using System.Text.Json.Nodes;
using Starling.Mcp;
using Starling.Telemetry.Daemon.Analysis;

namespace Starling.Telemetry.Daemon.Api;

/// <summary>
/// MCP tool group exposing the daemon's lag analysis to
/// driving agents, alongside the reused raw-telemetry tools
/// (browser_telemetry_*). These are the "pinpoint the lag" surface: ranked span
/// offenders, the frame-time/budget report, the latest CPU/memory, and per-span
/// CPU/memory correlation.
/// </summary>
internal sealed class TelemetryAnalysisTools(TelemetryAnalyzer analyzer) : IMcpToolGroup
{
    public const string OverviewTool = "lag_overview";
    public const string OffendersTool = "lag_top_offenders";
    public const string FramesTool = "lag_frames";
    public const string ResourcesTool = "lag_resources";
    public const string CorrelateTool = "lag_correlate_span";

    private const int DefaultWindowSeconds = 30;

    public string GetToolDescriptorsJson() => ToolDescriptorsJson;

    public bool HasTool(string name) => name switch
    {
        OverviewTool or OffendersTool or FramesTool or ResourcesTool or CorrelateTool => true,
        _ => false,
    };

    public Task<McpToolResult> InvokeAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        var window = TimeSpan.FromSeconds(Math.Clamp(ReadInt(arguments, "window", DefaultWindowSeconds), 1, 3600));
        JsonNode payload = name switch
        {
            OverviewTool => ToNode(analyzer.Overview(window, ReadInt(arguments, "limit", 12))),
            OffendersTool => Wrap("offenders", analyzer.TopOffenders(window, ReadInt(arguments, "limit", 15))),
            FramesTool => ToNode(analyzer.Frames(window)),
            ResourcesTool => ToNode(analyzer.Resources(window)),
            CorrelateTool => ToNode(analyzer.Correlate(
                ReadString(arguments, "span") ?? throw new ArgumentException("lag_correlate_span requires 'span'."),
                window)),
            _ => throw new ArgumentException($"Unknown analysis tool: {name}", nameof(name)),
        };
        return Task.FromResult(new McpToolResult(payload));
    }

    private static JsonObject ToNode<T>(T value)
        => JsonSerializer.SerializeToNode(value, DaemonJson.Options) as JsonObject
           ?? new JsonObject();

    private static JsonObject Wrap<T>(string key, T value) => new()
    {
        [key] = JsonSerializer.SerializeToNode(value, DaemonJson.Options),
    };

    private static int ReadInt(JsonElement args, string name, int def)
    {
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v))
        {
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i))
            {
                return i;
            }

            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var j))
            {
                return j;
            }
        }
        return def;
    }

    private static string? ReadString(JsonElement args, string name)
        => args.ValueKind == JsonValueKind.Object &&
           args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private const string ToolDescriptorsJson = """
        [
          {
            "name": "lag_overview",
            "description": "One-call lag dashboard: ingest counts, the frame-time/budget report, the latest local CPU/memory sample, and the top span offenders ranked by total time. Start here when investigating why pages feel laggy.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "window": { "type": "integer", "description": "Look-back window in seconds (default 30, max 3600)." },
                "limit": { "type": "integer", "description": "How many top offenders to include (default 12)." }
              }
            }
          },
          {
            "name": "lag_top_offenders",
            "description": "Span operations ranked by total wall-clock time in the window, with count, avg/p50/p95/p99/max duration, error count, and the average CPU utilization + working set sampled while each was running. The spans eating the most time and CPU are the lag suspects.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "window": { "type": "integer", "description": "Look-back window in seconds (default 30)." },
                "limit": { "type": "integer", "description": "Max rows (default 15)." }
              }
            }
          },
          {
            "name": "lag_frames",
            "description": "Frame-time distribution from the gui.frame.time_ms gauge (or gui.frame spans): count, avg/p50/p95/p99/max ms, frames over the 16ms budget and the overrun rate, estimated fps, and the slowest frames with the CPU/RAM measured at each.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "window": { "type": "integer", "description": "Look-back window in seconds (default 30)." }
              }
            }
          },
          {
            "name": "lag_resources",
            "description": "Local-machine resource usage of the browser process: latest CPU utilization, working set, managed/GC heap, thread + GC-collection counts, plus avg/max CPU and working set across the window.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "window": { "type": "integer", "description": "Look-back window in seconds (default 60)." }
              }
            }
          },
          {
            "name": "lag_correlate_span",
            "description": "Correlate one span name with local CPU/memory: average + max duration, the average + max CPU utilization and working set sampled during those spans, and a plain-language interpretation (CPU-bound vs blocked/waiting). Use after lag_top_offenders to confirm whether a hot span is burning CPU or stalling on the GPU/IO/a lock.",
            "inputSchema": {
              "type": "object",
              "properties": {
                "span": { "type": "string", "description": "Exact span name, e.g. paint.gpu.readback or paint.layertree.build." },
                "window": { "type": "integer", "description": "Look-back window in seconds (default 30)." }
              },
              "required": ["span"]
            }
          }
        ]
        """;
}
