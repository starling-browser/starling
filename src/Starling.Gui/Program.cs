using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Starling.Common.Diagnostics;
using Starling.Engine;
using Starling.Gui.Theme;
using Starling.Telemetry;

namespace Starling.Gui;

internal static class Program
{
    public static IServiceProvider Services { get; private set; } = null!;

    // Held for the process lifetime so it keeps sampling (and isn't GC'd).
    private static ProcessResourceSampler? s_resourceSampler;

    [STAThread]
    public static int Main(string[] args)
    {
        // Default the HTML parser to the Starling parser. HtmlBackendSelector
        // reads STARLING_HTML_PARSER lazily on first page load; an explicit value
        // (Aspire, CI, shell) still wins. AngleSharp is opt-in via "anglesharp".
        // (The JS engine no longer needs seeding here — Starling is the default
        // for a blank STARLING_JS_ENGINE.)
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("STARLING_HTML_PARSER")))
        {
            Environment.SetEnvironmentVariable("STARLING_HTML_PARSER", "starling");
        }

        // If STARLING_TELEMETRY_DAEMON is set, point the OpenTelemetry Protocol
        // exporter at the standalone daemon before telemetry is wired.
        OtelBootstrap.ConfigureDaemonExportFromEnv();

        Services = BuildServices();

        var otlp = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        // Sample this process's CPU/memory only when telemetry is being exported
        // or detailed local diagnostics are enabled.
        if (DiagnosticsMode.ProcessSampler || !string.IsNullOrWhiteSpace(otlp))
        {
            s_resourceSampler = new ProcessResourceSampler(Services.GetRequiredService<ILogger<ProcessResourceSampler>>());
        }

        var log = Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("Starling.Gui.Startup");
        if (!string.IsNullOrWhiteSpace(otlp))
        {
            log.LogInformation("OTLP exporter live → {Endpoint}", otlp);
        }
        else
        {
            log.LogInformation("OTEL_EXPORTER_OTLP_ENDPOINT not set; telemetry exporter is a no-op.");
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    private static IServiceProvider BuildServices()
    {
        // AddStarlingTelemetry's TBuilder constraint is IHostApplicationBuilder
        // (so the call site can chain on `WebApplication.CreateBuilder()` or
        // `Host.CreateApplicationBuilder()`). Use a HostApplicationBuilder to
        // satisfy the constraint, mutate its Services collection, then build a
        // plain ServiceProvider — we don't actually want the host's lifetime.
        var host = Host.CreateApplicationBuilder();
        host.Services.AddSingleton<ThemeManager>();
        host.Services.AddStarlingEngine();
        host.AddStarlingTelemetry("starling-gui");

        var provider = host.Services.BuildServiceProvider();

        // TracerProvider and MeterProvider register as hosted services and only
        // attach listeners inside StartAsync. We do not run the host here, so
        // resolve them by hand to force the providers to build.
        _ = provider.GetService<TracerProvider>();
        _ = provider.GetService<MeterProvider>();
        return provider;
    }
}
