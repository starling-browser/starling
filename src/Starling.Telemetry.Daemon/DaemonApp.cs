using System.IO.Compression;
using Google.Protobuf;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;
using Starling.Telemetry.Daemon.Analysis;
using Starling.Telemetry.Daemon.Api;
using Starling.Telemetry.Daemon.Ingestion;

namespace Starling.Telemetry.Daemon;

internal sealed record DaemonOptions(int GrpcPort, int HttpPort);

/// <summary>
/// Builds the daemon's web host: the OpenTelemetry Protocol receiver (gRPC +
/// HTTP/protobuf) and the REST query API, with the ingest store and analyzer in
/// dependency injection. Factored out of <c>Program</c> so the integration tests
/// can boot the same host on ephemeral ports and drive it over the wire.
/// </summary>
internal static class DaemonApp
{
    public static WebApplication Build(DaemonOptions opts)
    {
        var builder = WebApplication.CreateBuilder();

        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);

        builder.WebHost.ConfigureKestrel(k =>
        {
            // gRPC needs HTTP/2. The OpenTelemetry Protocol exporter speaks h2c
            // to an http:// endpoint.
            k.ListenLocalhost(opts.GrpcPort, o => o.Protocols = HttpProtocols.Http2);
            // HTTP port carries OpenTelemetry Protocol HTTP/protobuf (/v1/*) and
            // the REST query API, both HTTP/1.1. gRPC has its own port, so no
            // h2c-without-TLS warning.
            k.ListenLocalhost(opts.HttpPort, o => o.Protocols = HttpProtocols.Http1);
        });

        builder.Services.AddGrpc();
        builder.Services.AddSingleton<TelemetryIngestStore>();
        builder.Services.AddSingleton<TelemetryAnalyzer>();

        var app = builder.Build();
        var store = app.Services.GetRequiredService<TelemetryIngestStore>();
        var analyzer = app.Services.GetRequiredService<TelemetryAnalyzer>();

        MapOtlpReceiver(app, store);
        MapQueryApi(app, analyzer, store, opts);
        return app;
    }

    private static void MapOtlpReceiver(WebApplication app, TelemetryIngestStore store)
    {
        // OpenTelemetry Protocol over gRPC, the default exporter protocol.
        app.MapGrpcService<TraceIngestService>();
        app.MapGrpcService<MetricsIngestService>();
        app.MapGrpcService<LogsIngestService>();

        // OpenTelemetry Protocol over HTTP/protobuf. A malformed body returns
        // 400 per the protocol instead of bubbling to a 500 with a stack trace.
        app.MapPost("/v1/traces", async (HttpRequest req, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(req, ct);
            if (!TryParse(ExportTraceServiceRequest.Parser, body, out var request))
            {
                return Results.BadRequest();
            }

            store.IngestSpans(OtlpConverter.ToActivityRecords(request));
            return Results.Bytes(new ExportTraceServiceResponse().ToByteArray(), "application/x-protobuf");
        });
        app.MapPost("/v1/metrics", async (HttpRequest req, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(req, ct);
            if (!TryParse(ExportMetricsServiceRequest.Parser, body, out var request))
            {
                return Results.BadRequest();
            }

            store.IngestMetrics(OtlpConverter.ToMeterRecords(request));
            return Results.Bytes(new ExportMetricsServiceResponse().ToByteArray(), "application/x-protobuf");
        });
        app.MapPost("/v1/logs", async (HttpRequest req, CancellationToken ct) =>
        {
            var body = await ReadBodyAsync(req, ct);
            if (!TryParse(ExportLogsServiceRequest.Parser, body, out var request))
            {
                return Results.BadRequest();
            }

            store.IngestLogs(OtlpConverter.ToLogRecords(request));
            return Results.Bytes(new ExportLogsServiceResponse().ToByteArray(), "application/x-protobuf");
        });
    }

    private static bool TryParse<T>(MessageParser<T> parser, byte[] body, out T message)
        where T : IMessage<T>
    {
        try { message = parser.ParseFrom(body); return true; }
        catch (InvalidProtocolBufferException) { message = default!; return false; }
    }

    private static void MapQueryApi(
        WebApplication app, TelemetryAnalyzer analyzer, TelemetryIngestStore store, DaemonOptions opts)
    {
        app.MapGet("/", () => Results.Text(
            $"Starling telemetry daemon. OpenTelemetry Protocol in on gRPC :{opts.GrpcPort} and HTTP :{opts.HttpPort}/v1/*.\n" +
            "Query: /api/summary  /api/top-offenders  /api/frames  /api/resources  /api/correlate?span=NAME\n"));

        app.MapGet("/healthz", () => Results.Json(new
        {
            status = "ok",
            spansIngested = store.SpansIngested,
            metricsIngested = store.MetricsIngested,
            logsIngested = store.LogsIngested,
            lastReceivedUtc = store.LastReceivedUtc,
        }, DaemonJson.Options));

        app.MapGet("/api/summary", (int? window, int? limit) =>
            Results.Json(analyzer.Overview(Window(window, 30), limit ?? 12), DaemonJson.Options));
        app.MapGet("/api/top-offenders", (int? window, int? limit) =>
            Results.Json(analyzer.TopOffenders(Window(window, 30), limit ?? 15), DaemonJson.Options));
        app.MapGet("/api/frames", (int? window) =>
            Results.Json(analyzer.Frames(Window(window, 30)), DaemonJson.Options));
        app.MapGet("/api/resources", (int? window) =>
            Results.Json(analyzer.Resources(Window(window, 60)), DaemonJson.Options));
        app.MapGet("/api/correlate", (string? span, int? window) =>
            string.IsNullOrWhiteSpace(span)
                ? Results.BadRequest("query param 'span' is required, e.g. /api/correlate?span=paint.gpu.readback")
                : Results.Json(analyzer.Correlate(span, Window(window, 30)), DaemonJson.Options));
    }

    private static TimeSpan Window(int? seconds, int fallback)
        => TimeSpan.FromSeconds(Math.Clamp(seconds ?? fallback, 1, 3600));

    private static async Task<byte[]> ReadBodyAsync(HttpRequest req, CancellationToken ct)
    {
        // leaveOpen: true so disposing the decompressor doesn't close Kestrel's
        // framework-owned request body; the wrapper is disposed deterministically
        // in the finally to release its native inflater state.
        GZipStream? gzip = null;
        Stream src = req.Body;
        foreach (var enc in req.Headers.ContentEncoding)
        {
            if (enc is not null && enc.Contains("gzip", StringComparison.OrdinalIgnoreCase))
            {
                gzip = new GZipStream(req.Body, CompressionMode.Decompress, leaveOpen: true);
                src = gzip;
                break;
            }
        }
        try
        {
            using var ms = new MemoryStream();
            await src.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        finally
        {
            if (gzip is not null)
            {
                await gzip.DisposeAsync();
            }
        }
    }
}
