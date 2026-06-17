using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Starling.Gui.Mcp;
using Starling.Mcp;
using Starling.Mcp.Telemetry;
using Starling.Telemetry;

namespace Starling.Gui;

public sealed class App : Application
{
    private const string DefaultMcpUrl = "http://127.0.0.1:3077/mcp";

    private StarlingMcpServer? _mcpServer;
    private BrowserControlBridge? _mcpBridge;
    private MainWindow? _mainWindow;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        MacDockIcon.Apply();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _mainWindow = new MainWindow();
            desktop.MainWindow = _mainWindow;
            desktop.Exit += (_, _) => _ = ShutdownMcpAsync();

            StartMcpServer(_mainWindow);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void StartMcpServer(MainWindow window)
    {
        var log = Program.Services.GetService(typeof(ILoggerFactory)) is ILoggerFactory lf
            ? lf.CreateLogger("Starling.Gui.Mcp")
            : null;
        try
        {
            _mcpBridge = new BrowserControlBridge();
            _mcpBridge.Attach(window);

            var telemetry = Program.Services.GetRequiredService<TelemetryStream>();
            var browserTools = new BrowserTools(_mcpBridge);
            var telemetryTools = new TelemetryTools(telemetry);
            var telemetryResources = new TelemetryResources(telemetry);

            _mcpServer = new StarlingMcpServer(
                endpoint: ResolveEndpoint(),
                toolGroups: [browserTools, telemetryTools],
                resourceProviders: [telemetryResources],
                serverName: "starling-gui",
                serverTitle: "Starling GUI");
            // StartAsync returns the listener-spawned task — fire-and-forget;
            // the accept loop runs until disposal.
            _ = _mcpServer.StartAsync();
            log?.LogInformation("MCP server listening on {Endpoint}", _mcpServer.Endpoint);
        }
        catch (Exception ex)
        {
            // Port collision (e.g. another Starling.Gui already bound 3077)
            // shows up here; log + swallow so the window still opens.
            log?.LogWarning(ex, "MCP server failed to start; browser_* tools unavailable");
        }
    }

    private static Uri ResolveEndpoint()
    {
        var configured = Environment.GetEnvironmentVariable("STARLING_MCP_URL");
        if (string.IsNullOrWhiteSpace(configured))
        {
            return new Uri(DefaultMcpUrl);
        }

        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException(
                "STARLING_MCP_URL must be an absolute loopback HTTP URL with a path, for example http://127.0.0.1:3077/mcp.");
        }

        return uri;
    }

    private async Task ShutdownMcpAsync()
    {
        if (_mainWindow is not null && _mcpBridge is not null)
        {
            _mcpBridge.Detach(_mainWindow);
        }

        if (_mcpServer is not null)
        {
            await _mcpServer.DisposeAsync().ConfigureAwait(false);
        }
    }
}
