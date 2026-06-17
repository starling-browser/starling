using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;

namespace Starling.Mcp;

/// <summary>
/// Loopback HTTP host for Starling's MCP surface. The transport is provided by
/// the official C# SDK; this class only adapts Starling tool/resource groups to
/// the SDK and owns the local endpoint lifetime.
/// </summary>
public sealed class StarlingMcpServer : IAsyncDisposable
{
    private const string DefaultProtocolVersion = "2025-11-25";

    private readonly IReadOnlyList<IMcpToolGroup> _toolGroups;
    private readonly IReadOnlyList<IMcpResourceProvider> _resourceProviders;
    private readonly IReadOnlyList<IMcpPromptProvider> _promptProviders;
    private readonly string _serverName;
    private readonly string _serverTitle;
    private readonly string _serverVersion;

    private WebApplication? _app;

    public StarlingMcpServer(
        Uri endpoint,
        IEnumerable<IMcpToolGroup> toolGroups,
        IEnumerable<IMcpResourceProvider>? resourceProviders = null,
        IEnumerable<IMcpPromptProvider>? promptProviders = null,
        string serverName = "starling",
        string serverTitle = "Starling",
        string serverVersion = "0.1.0")
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(toolGroups);

        Endpoint = endpoint;
        ValidateEndpoint(endpoint);

        _toolGroups = toolGroups.ToArray();
        _resourceProviders = (resourceProviders ?? []).ToArray();
        _promptProviders = (promptProviders ?? StarlingDefaultPrompts.ForToolGroups(_toolGroups)).ToArray();
        _serverName = serverName;
        _serverTitle = serverTitle;
        _serverVersion = serverVersion;
    }

    public Uri Endpoint { get; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_app is not null)
        {
            return;
        }

        var endpoint = Endpoint;
        var ip = ResolveEndpointAddress(endpoint);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ApplicationName = typeof(StarlingMcpServer).Assembly.GetName().Name,
        });

        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(ip, endpoint.Port, o => o.Protocols = HttpProtocols.Http1);
        });

        builder.Services
            .AddMcpServer(options =>
            {
                options.ProtocolVersion = DefaultProtocolVersion;
                options.ServerInfo = new Implementation
                {
                    Name = _serverName,
                    Title = _serverTitle,
                    Version = _serverVersion,
                    Description = "Starling browser automation and telemetry server.",
                };
                options.ServerInstructions =
                    "Use browser_* tools to drive the visible Starling window and browser_telemetry_* tools to inspect recent engine telemetry.";
            })
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
            })
            .WithStarlingToolGroups(_toolGroups)
            .WithStarlingResourceProviders(_resourceProviders)
            .WithStarlingPromptProviders(_promptProviders);

        var app = builder.Build();
        app.Use(RejectInvalidOrigins);
        app.MapMcp(endpoint.AbsolutePath);

        _app = app;
        try
        {
            await app.StartAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            _app = null;
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var app = _app;
        _app = null;
        if (app is null)
        {
            return;
        }

        await app.StopAsync().ConfigureAwait(false);
        await app.DisposeAsync().ConfigureAwait(false);
    }

    private static Task RejectInvalidOrigins(HttpContext context, Func<Task> next)
    {
        if (context.Request.Headers.TryGetValue("Origin", out var originValues))
        {
            foreach (var origin in originValues)
            {
                if (!IsAllowedOrigin(origin))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                }
            }
        }

        return next();
    }

    private static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.Scheme is "http" or "https" && IsLoopbackHost(uri.Host);
    }

    private static IPAddress ResolveEndpointAddress(Uri endpoint)
    {
        var ip = endpoint.Host switch
        {
            "localhost" => IPAddress.Loopback,
            _ when IPAddress.TryParse(endpoint.Host, out var parsed) => parsed,
            _ => throw new InvalidOperationException("The MCP endpoint must use a loopback host."),
        };
        if (!IPAddress.IsLoopback(ip))
        {
            throw new InvalidOperationException("The MCP endpoint must use a loopback host.");
        }

        return ip;
    }

    private static void ValidateEndpoint(Uri endpoint)
    {
        if (endpoint.Scheme != Uri.UriSchemeHttp)
        {
            throw new ArgumentException("MCP endpoint scheme must be http.", nameof(endpoint));
        }

        if (!IsLoopbackHost(endpoint.Host))
        {
            throw new ArgumentException("MCP endpoint must use a loopback host.", nameof(endpoint));
        }

        if (string.IsNullOrEmpty(endpoint.AbsolutePath) || endpoint.AbsolutePath == "/")
        {
            throw new ArgumentException("MCP endpoint must include a path, e.g. /mcp.", nameof(endpoint));
        }
    }

    private static bool IsLoopbackHost(string host)
        => host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
           IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip);
}
