using Starling.Mcp;
using Starling.Mcp.Telemetry;
using Starling.Telemetry.Daemon;
using Starling.Telemetry.Daemon.Analysis;
using Starling.Telemetry.Daemon.Api;
using Starling.Telemetry.Daemon.Ingestion;

// ── Configuration ──────────────────────────────────────────────────────────
// Ports default to the OpenTelemetry Protocol standards (4317 gRPC, 4318
// HTTP/protobuf) so a host only needs
// OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317, the same variable on
// :4318 with OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf, or simply
// STARLING_TELEMETRY_DAEMON.
var grpcPort = EnvInt("STARLING_DAEMON_GRPC_PORT", 4317);
var httpPort = EnvInt("STARLING_DAEMON_HTTP_PORT", 4318);
var mcpUrl = Environment.GetEnvironmentVariable("STARLING_DAEMON_MCP_URL") ?? "http://127.0.0.1:4319/mcp";

var app = DaemonApp.Build(new DaemonOptions(grpcPort, httpPort));
var store = app.Services.GetRequiredService<TelemetryIngestStore>();
var analyzer = app.Services.GetRequiredService<TelemetryAnalyzer>();

// ── MCP query server -------------------------------
StarlingMcpServer? mcp = null;
try
{
    mcp = new StarlingMcpServer(
        endpoint: new Uri(mcpUrl),
        toolGroups: [new TelemetryTools(store.Stream), new TelemetryAnalysisTools(analyzer)],
        resourceProviders: [new TelemetryResources(store.Stream)],
        serverName: "starling-telemetry-daemon",
        serverTitle: "Starling Telemetry Daemon");
    await mcp.StartAsync();
}
catch (Exception ex)
{
    Console.Error.WriteLine(
        $"[daemon] MCP server failed to start ({ex.Message}); REST + OpenTelemetry Protocol still active.");
}

Console.WriteLine($"""
    ┌─ Starling Telemetry Daemon ───────────────────────────────────────────
    │ OpenTelemetry Protocol gRPC : http://localhost:{grpcPort}
    │ OpenTelemetry Protocol HTTP : http://localhost:{httpPort}/v1/*
    │ REST query  : http://localhost:{httpPort}/api/summary
    │ MCP query: {mcpUrl}
    │ Browser     : run with STARLING_TELEMETRY_DAEMON=http://localhost:{httpPort}
    └────────────────────────────────────────────────────────────────────────
    """);

using var heartbeat = new Timer(_ => PrintHeartbeat(analyzer, store), null,
    TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

app.Lifetime.ApplicationStopping.Register(() =>
{
    if (mcp is not null)
    {
        mcp.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
});

await app.RunAsync();
return;

// ── helpers ────────────────────────────────────────────────────────────────
static int EnvInt(string name, int fallback)
    => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

static void PrintHeartbeat(TelemetryAnalyzer analyzer, TelemetryIngestStore store)
{
    if (store.SpansIngested == 0 && store.MetricsIngested == 0 && store.LogsIngested == 0)
    {
        return; // stay quiet until a host connects
    }

    var o = analyzer.Overview(TimeSpan.FromSeconds(10), topLimit: 3);
    var f = o.Frames;
    var r = o.Resources;
    var hot = string.Join(", ", o.TopOffenders.Select(t => $"{t.Name} {t.AvgMs:F1}ms"));
    var cpu = double.IsNaN(r.CpuUtilization) ? "?" : $"{r.CpuUtilization * 100:F0}%";
    var rss = double.IsNaN(r.WorkingSetMb) ? "?" : $"{r.WorkingSetMb:F0}MB";
    var frame = f.Count > 0
        ? $"frame p99={f.P99Ms:F1}ms over={f.OverBudgetRate * 100:F0}% ~{f.EstimatedFps:F0}fps"
        : "frame n/a";
    Console.WriteLine(
        $"[{DateTime.Now:HH:mm:ss}] in spans={store.SpansIngested} metrics={store.MetricsIngested} " +
        $"logs={store.LogsIngested} | {frame} | cpu={cpu} rss={rss}" +
        (hot.Length > 0 ? $" | hot: {hot}" : ""));
}

/// <summary>Exposed so the integration tests can boot the host via a factory.</summary>
public partial class Program;
