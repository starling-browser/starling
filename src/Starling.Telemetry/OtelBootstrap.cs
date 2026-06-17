using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Starling.Common.Diagnostics;

namespace Starling.Telemetry;

/// <summary>
/// One-stop OpenTelemetry wiring for Starling host processes. Both flavours
/// export traces, metrics, and logs over the OpenTelemetry Protocol when
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is set. Without the env var, only the
/// console logger and DevTools in-memory sinks are registered.
///
/// <para>Engine libraries log through <c>ILogger</c> and record spans/metrics
/// through <c>StarlingTelemetry</c>; this class just registers the providers and
/// listeners that consume them. There is no longer a diagnostics facade to
/// resolve from DI — code takes an <see cref="ILoggerFactory"/> instead.</para>
/// </summary>
public static class OtelBootstrap
{
    /// <summary>
    /// Wire OpenTelemetry into a <see cref="IHostApplicationBuilder"/>-shaped
    /// host, such as <c>HostApplicationBuilder</c> or
    /// <c>WebApplicationBuilder</c>. Uses the framework's logging/metrics
    /// builders so anything the app already logs through
    /// <see cref="ILogger"/> flows out as OpenTelemetry log records. Engine code
    /// resolves <see cref="ILoggerFactory"/> from DI for logging and uses the
    /// static <c>StarlingTelemetry</c> for spans/metrics.
    /// </summary>
    public static TBuilder AddStarlingTelemetry<TBuilder>(this TBuilder builder, string serviceName)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var hasOtlp = HasOtlpEndpoint();

        // The in-memory log sink is its own ILoggerProvider, registered
        // alongside OpenTelemetry's so DevTools' ConsolePanel can read recent
        // entries even when no OpenTelemetry Protocol endpoint is configured.
        var logSink = new InMemoryLogSink();
        builder.Logging.AddProvider(logSink);
        // DevTools' ConsolePanel shows page console.* output (routed via the
        // engine under "Starling.engine.js"). Open that category down to Debug
        // for the in-memory sink only so console.debug survives the default
        // Information floor; other providers/categories keep their defaults.
        builder.Logging.AddFilter<InMemoryLogSink>("Starling.engine.js", LogLevel.Debug);

        if (hasOtlp)
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                logging.IncludeFormattedMessage = true;
                logging.IncludeScopes = true;
            });

            builder.Services.AddOpenTelemetry()
                .ConfigureResource(rb => rb.AddService(serviceName))
                .WithTracing(t => t
                    .AddSource(serviceName)
                    .AddSource(StarlingTelemetry.SourceName)
                    .AddHttpClientInstrumentation())
                .WithMetrics(m => m
                    .AddMeter(StarlingTelemetry.SourceName)
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation());
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        // In-memory sinks feeding DevTools' ConsolePanel / PerformancePanel /
        // InternalsPanel. The activity and meter listeners self-register at
        // construction. It coexists with the OpenTelemetry SDK's own listeners
        // (multiple ActivityListeners per source are allowed). One TelemetryStream
        // facade aggregates the three for DevTools to subscribe to.
        builder.Services.AddSingleton(logSink);
        builder.Services.AddSingleton(_ =>
            new InMemoryActivitySink(DiagnosticsMode.TelemetrySinks, serviceName, StarlingTelemetry.SourceName));
        builder.Services.AddSingleton(_ =>
            new InMemoryMeterSink(DiagnosticsMode.TelemetrySinks, StarlingTelemetry.SourceName));
        builder.Services.AddSingleton<TelemetryStream>();

        return builder;
    }

    /// <summary>
    /// Wire OpenTelemetry for a plain <c>Main</c>-style console app that
    /// doesn't go through <see cref="IHostApplicationBuilder"/>. Returns a
    /// disposable handle that flushes and shuts down the providers and
    /// exposes the <see cref="ILoggerFactory"/> — store it in a <c>using</c> at
    /// the top of <c>Main</c> so traces/metrics aren't dropped on exit.
    ///
    /// Pass <paramref name="withInMemorySinks"/> = true to additionally
    /// build the same three ring-buffer sinks <see cref="AddStarlingTelemetry"/>
    /// registers via DI — needed when the host wants to read its own
    /// telemetry back, for example to serve it through MCP.
    /// </summary>
    public static OtelHandle Initialize(string serviceName, bool withInMemorySinks = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var resource = ResourceBuilder.CreateDefault().AddService(serviceName);
        var hasOtlp = HasOtlpEndpoint();

        InMemoryActivitySink? activitySink = null;
        InMemoryMeterSink? meterSink = null;
        InMemoryLogSink? logSink = null;
        var attachInMemoryListeners = withInMemorySinks || DiagnosticsMode.TelemetrySinks;
        if (withInMemorySinks)
        {
            // Construct the sinks before the providers. ActivitySink and
            // MeterSink self-register their listeners at construction; the
            // log sink is wired as an ILoggerProvider on the factory below.
            activitySink = new InMemoryActivitySink(attachInMemoryListeners, serviceName, StarlingTelemetry.SourceName);
            meterSink = new InMemoryMeterSink(attachInMemoryListeners, StarlingTelemetry.SourceName);
            logSink = new InMemoryLogSink();
        }

        TracerProvider? tracerProvider = null;
        MeterProvider? meterProvider = null;
        if (hasOtlp)
        {
            tracerProvider = Sdk.CreateTracerProviderBuilder()
                .SetResourceBuilder(resource)
                .AddSource(serviceName)
                .AddSource(StarlingTelemetry.SourceName)
                .AddHttpClientInstrumentation()
                .AddOtlpExporter()
                .Build();

            meterProvider = Sdk.CreateMeterProviderBuilder()
                .SetResourceBuilder(resource)
                .AddMeter(StarlingTelemetry.SourceName)
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter()
                .Build();
        }

        var loggerFactory = LoggerFactory.Create(builder =>
        {
            // Console → stderr, so plain `dotnet run` still shows engine logs the
            // way the old ConsoleDiagnostics sink did. STARLING_DIAG_TRACE lowers
            // the floor to Trace (paint span timings etc.); default stays Info to
            // keep normal CLI runs quiet.
            builder.SetMinimumLevel(DiagnosticsMode.TraceConsole ? LogLevel.Trace : LogLevel.Information);
            builder.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
            if (logSink is not null)
            {
                builder.AddProvider(logSink);
                // Mirror AddStarlingTelemetry: open Starling.engine.js (page
                // console output) down to Debug for the in-memory sink only.
                builder.AddFilter<InMemoryLogSink>("Starling.engine.js", LogLevel.Debug);
            }
            if (hasOtlp)
            {
                builder.AddOpenTelemetry(logging =>
                {
                    logging.SetResourceBuilder(resource);
                    logging.IncludeFormattedMessage = true;
                    logging.IncludeScopes = true;
                    logging.AddOtlpExporter();
                });
            }
        });

        TelemetryStream? telemetryStream = null;
        if (withInMemorySinks)
        {
            // Non-null when withInMemorySinks is true (assigned above).
            telemetryStream = new TelemetryStream(logSink!, activitySink!, meterSink!);
        }

        return new OtelHandle(tracerProvider, meterProvider, loggerFactory, telemetryStream);
    }

    /// <summary>
    /// Convenience bridge for the standalone telemetry daemon. When
    /// <c>STARLING_TELEMETRY_DAEMON</c> is set (e.g. <c>http://localhost:4318</c>)
    /// this points the standard OpenTelemetry Protocol exporter at it over
    /// HTTP/protobuf, so a host only needs that one env var to stream its
    /// spans/metrics/logs to the daemon. No Aspire AppHost is required. Existing explicit
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>/<c>_PROTOCOL</c> values win (so Aspire
    /// runs are untouched). Call this once at the very top of <c>Main</c>, before
    /// <see cref="AddStarlingTelemetry"/> / <see cref="Initialize"/>.
    /// </summary>
    public static void ConfigureDaemonExportFromEnv()
    {
        var daemon = Environment.GetEnvironmentVariable("STARLING_TELEMETRY_DAEMON");
        if (string.IsNullOrWhiteSpace(daemon))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT")))
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", daemon);
        }

        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL")))
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");
        }
    }

    private static bool HasOtlpEndpoint()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));

    public sealed class OtelHandle : IDisposable
    {
        private readonly TracerProvider? _tracer;
        private readonly MeterProvider? _meter;
        private readonly TelemetryStream? _telemetryStream;

        internal OtelHandle(
            TracerProvider? tracer,
            MeterProvider? meter,
            ILoggerFactory loggerFactory,
            TelemetryStream? telemetryStream)
        {
            _tracer = tracer;
            _meter = meter;
            _telemetryStream = telemetryStream;
            LoggerFactory = loggerFactory;
        }

        public ILoggerFactory LoggerFactory { get; }

        /// <summary>
        /// Non-null when <see cref="Initialize"/> was called with
        /// <c>withInMemorySinks: true</c>. Hosts an in-memory ring-buffer
        /// snapshot of recent spans/logs/metrics. Useful for serving the
        /// process's own telemetry through MCP without a
        /// separate exporter.
        /// </summary>
        public TelemetryStream? TelemetryStream => _telemetryStream;

        public void Dispose()
        {
            // Disposal order matters: flush exporters via the providers'
            // disposal hooks before tearing the logger factory down.
            _tracer?.Dispose();
            _meter?.Dispose();
            _telemetryStream?.Dispose();
            LoggerFactory.Dispose();
        }
    }
}
