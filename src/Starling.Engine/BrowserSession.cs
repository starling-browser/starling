using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Common;
using Starling.Common.Diagnostics;
using Starling.Net;
using Starling.Net.Http.Cookies;

namespace Starling.Engine;

/// <summary>
/// Small stateful browsing session for headless and shell hosts. It keeps
/// navigation history and a shared cookie jar across page loads.
/// </summary>
public sealed class BrowserSession : IDisposable
{
    private readonly StarlingEngine _engine;
    private readonly ILoggerFactory _loggerFactory;
    private readonly StarlingHttpClient _http;

    public BrowserSession(ILoggerFactory? loggerFactory = null, CookieJar? cookieJar = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        Cookies = cookieJar ?? new CookieJar();
        StarlingHttpClient NewClient() => new(new StarlingHttpClientOptions
        {
            CookieJar = Cookies,
            LoggerFactory = _loggerFactory,
        });
        _http = NewClient();
        _engine = new StarlingEngine(_loggerFactory, httpFactory: NewClient);
    }

    public NavigationHistory History { get; } = new();
    public CookieJar Cookies { get; }

    public async Task<Result<RenderOutcome, RenderError>> NavigateAsync(
        string url,
        RenderOptions options,
        string outputPath,
        CancellationToken ct = default)
    {
        return await TrackAsync("navigate", url, async () =>
        {
            var result = await _engine.RenderAsync(url, options, outputPath, ct).ConfigureAwait(false);
            if (result.IsOk)
            {
                History.Navigate(url);
            }

            return result;
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Same flow as <see cref="NavigateAsync"/> but returns a laid-out page
    /// the caller can walk for interactive rendering, instead of saving a PNG.
    /// </summary>
    /// <remarks>
    /// History commits at navigation-commit time — first paint on the
    /// progressive (scripted) path, or successful settle on the snapshot
    /// (script-free) path — not after the deferred phase finishes. That way
    /// a navigation the user already saw is recorded in history even if a
    /// later click cancels its deferred phase, matching browser behaviour
    /// and keeping Back/Forward from "skipping" a page the user visited.
    /// </remarks>
    public async Task<Result<LaidOutPage, RenderError>> NavigateInteractiveAsync(
        string url,
        RenderOptions options,
        CancellationToken ct = default,
        Action<LaidOutPage>? onFirstPaint = null)
    {
        return await TrackAsync("navigate", url, async () =>
        {
            var committed = false;
            void Commit()
            {
                if (committed)
                {
                    return;
                }

                committed = true;
                History.Navigate(url);
            }
            void OnCommit(LaidOutPage page)
            {
                Commit();
                onFirstPaint?.Invoke(page);
            }
            var result = await _engine.LayoutPageAsync(url, options, ct, sharedHttp: _http, onFirstPaint: OnCommit).ConfigureAwait(false);
            // Script-free pages skip the progressive path: OnCommit never
            // fires, so commit on a successful settle instead. Cancelled or
            // failed loads that never first-painted stay out of history.
            if (result.IsOk)
            {
                Commit();
            }

            return result;
        }).ConfigureAwait(false);
    }

    public Task<Result<LaidOutPage, RenderError>> BackInteractiveAsync(
        RenderOptions options,
        CancellationToken ct = default,
        Action<LaidOutPage>? onFirstPaint = null)
    {
        var url = History.Back();
        return TrackAsync("back", url, () => _engine.LayoutPageAsync(url, options, ct, sharedHttp: _http, onFirstPaint: onFirstPaint));
    }

    public Task<Result<LaidOutPage, RenderError>> ForwardInteractiveAsync(
        RenderOptions options,
        CancellationToken ct = default,
        Action<LaidOutPage>? onFirstPaint = null)
    {
        var url = History.Forward();
        return TrackAsync("forward", url, () => _engine.LayoutPageAsync(url, options, ct, sharedHttp: _http, onFirstPaint: onFirstPaint));
    }

    public Task<Result<LaidOutPage, RenderError>> ReloadInteractiveAsync(
        RenderOptions options,
        CancellationToken ct = default,
        Action<LaidOutPage>? onFirstPaint = null)
    {
        var url = History.Reload();
        return TrackAsync("reload", url, () => _engine.LayoutPageAsync(url, options, ct, sharedHttp: _http, onFirstPaint: onFirstPaint));
    }

    /// <summary>
    /// Reflows <paramref name="page"/> at a new viewport size without touching
    /// the network or history — used when the shell window resizes. Returns a
    /// fresh page reusing the existing document/resources; the caller shows it
    /// and disposes the old one. See <see cref="StarlingEngine.RelayoutPage"/>.
    /// </summary>
    public LaidOutPage RelayoutCurrent(LaidOutPage page, RenderOptions options)
        => _engine.RelayoutPage(page, options);

    /// <summary>Advance the page's animation/transition clocks to
    /// <paramref name="nowMs"/> (and import script animations) so the next
    /// render samples the animated values. Drives the live GUI animation loop.</summary>
    public void PrepareAnimationFrame(LaidOutPage page, long nowMs)
        => _engine.PrepareAnimationFrame(page, nowMs);

    /// <summary>True while the page has an in-flight animation or transition.</summary>
    public bool HasActiveAnimations(LaidOutPage page)
        => _engine.HasActiveAnimations(page);

    /// <summary>
    /// Capture <paramref name="page"/> to a PNG at <paramref name="outputPath"/>.
    /// Pass-through to <see cref="StarlingEngine.CaptureToPng"/> — used by the
    /// shell's MCP screenshot tool. Synchronous: callers
    /// marshal to the UI thread.
    /// </summary>
    public RenderOutcome CaptureToPng(LaidOutPage page, string outputPath, long nowMs = 0, bool fullPage = true)
        => _engine.CaptureToPng(page, outputPath, nowMs, fullPage);

    public Task<Result<RenderOutcome, RenderError>> BackAsync(
        RenderOptions options,
        string outputPath,
        CancellationToken ct = default)
    {
        var url = History.Back();
        return TrackAsync("back", url, () => _engine.RenderAsync(url, options, outputPath, ct));
    }

    public Task<Result<RenderOutcome, RenderError>> ForwardAsync(
        RenderOptions options,
        string outputPath,
        CancellationToken ct = default)
    {
        var url = History.Forward();
        return TrackAsync("forward", url, () => _engine.RenderAsync(url, options, outputPath, ct));
    }

    public Task<Result<RenderOutcome, RenderError>> ReloadAsync(
        RenderOptions options,
        string outputPath,
        CancellationToken ct = default)
    {
        var url = History.Reload();
        return TrackAsync("reload", url, () => _engine.RenderAsync(url, options, outputPath, ct));
    }

    public void Dispose()
    {
        // Tear down the session-scoped client and its live connections. Pages
        // returned by interactive navigations do not own this client (they were
        // handed null), so disposing here is the single owning release.
        _http.Dispose();
    }

    /// <summary>
    /// Wraps a navigation operation in a "session" span, tags the URL,
    /// increments per-op counters, and surfaces error status on the active
    /// activity. Returns the underlying result unchanged.
    /// </summary>
    private async Task<Result<T, RenderError>> TrackAsync<T>(
        string op,
        string? url,
        Func<Task<Result<T, RenderError>>> work)
    {
        using var span = StarlingTelemetry.Span("session", op);
        if (!string.IsNullOrEmpty(url))
        {
            Activity.Current?.SetTag("http.url", url);
        }

        StarlingTelemetry.Counter("session.navigations", 1);

        var result = await work().ConfigureAwait(false);
        if (result.IsErr)
        {
            StarlingTelemetry.Counter("session.navigation_failures", 1);
            BrowserSessionLog.NavigationFailed(_log, op, result.Error.Message);
            Activity.Current?.SetStatus(ActivityStatusCode.Error, result.Error.Message);
        }
        return result;
    }

    private ILogger _log => _loggerFactory.CreateLogger<BrowserSession>();
}

internal static partial class BrowserSessionLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "{Op} failed: {Message}")]
    public static partial void NavigationFailed(ILogger logger, string op, string message);
}
