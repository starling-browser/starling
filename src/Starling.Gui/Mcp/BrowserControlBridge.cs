using Avalonia.Threading;

namespace Starling.Gui.Mcp;

/// <summary>
/// Bridges MCP browser tools to the active Avalonia window.
/// Marshals tool calls onto <see cref="Dispatcher.UIThread"/>. A semaphore
/// serializes concurrent tool calls so the browser state machine only sees one
/// mutation at a time.
/// </summary>
public sealed class BrowserControlBridge : IBrowserControlDispatcher
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IBrowserController? _controller;

    public void Attach(IBrowserController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        Volatile.Write(ref _controller, controller);
    }

    public void Detach(IBrowserController controller)
    {
        if (ReferenceEquals(Volatile.Read(ref _controller), controller))
        {
            Volatile.Write(ref _controller, null);
        }
    }

    public Task<BrowserControlResult> NavigateAsync(string url, CancellationToken ct)
        => InvokeAsync(controller => controller.NavigateFromToolAsync(url, ct), ct);

    public Task<BrowserControlResult> BackAsync(CancellationToken ct)
        => InvokeAsync(controller => controller.BackFromToolAsync(ct), ct);

    public Task<BrowserControlResult> ForwardAsync(CancellationToken ct)
        => InvokeAsync(controller => controller.ForwardFromToolAsync(ct), ct);

    public Task<BrowserControlResult> ReloadAsync(CancellationToken ct)
        => InvokeAsync(controller => controller.ReloadFromToolAsync(ct), ct);

    public Task<BrowserControlResult> ScreenshotAsync(string path, CancellationToken ct)
        => InvokeAsync(controller => controller.ScreenshotFromToolAsync(path, ct), ct);

    public Task<BrowserControlResult> ScreenshotViewportAsync(string path, CancellationToken ct)
        => InvokeAsync(controller => controller.ScreenshotViewportFromToolAsync(path, ct), ct);

    public Task<BrowserControlResult> InspectAsync(bool includeHtml, string? logPath, CancellationToken ct)
        => InvokeAsync(controller => controller.InspectFromToolAsync(includeHtml, logPath, ct), ct);

    public Task<BrowserControlResult> ConsoleAsync(string? minLevel, int limit, CancellationToken ct)
        => InvokeAsync(controller => controller.ConsoleFromToolAsync(minLevel, limit, ct), ct);

    public Task<BrowserControlResult> NetworkAsync(int limit, CancellationToken ct)
        => InvokeAsync(controller => controller.NetworkFromToolAsync(limit, ct), ct);

    public Task<BrowserControlResult> ClickAsync(double x, double y, CancellationToken ct)
        => InvokeAsync(controller => controller.ClickFromToolAsync(x, y, ct), ct);

    public Task<BrowserControlResult> ClickSelectorAsync(string selector, CancellationToken ct)
        => InvokeAsync(controller => controller.ClickSelectorFromToolAsync(selector, ct), ct);

    public Task<BrowserControlResult> MoveMouseAsync(double x, double y, CancellationToken ct)
        => InvokeAsync(controller => controller.MoveMouseFromToolAsync(x, y, ct), ct);

    public Task<BrowserControlResult> ScrollAsync(double deltaX, double deltaY, CancellationToken ct)
        => InvokeAsync(controller => controller.ScrollFromToolAsync(deltaX, deltaY, ct), ct);

    public Task<BrowserControlResult> ScrollToAsync(double? x, double? y, string? selector, string? position, CancellationToken ct)
        => InvokeAsync(controller => controller.ScrollToFromToolAsync(x, y, selector, position, ct), ct);

    public Task<BrowserControlResult> PressKeyAsync(string key, bool shift, bool ctrl, bool alt, bool meta, CancellationToken ct)
        => InvokeAsync(controller => controller.PressKeyFromToolAsync(key, shift, ctrl, alt, meta, ct), ct);

    public Task<BrowserControlResult> TypeTextAsync(string text, bool submit, CancellationToken ct)
        => InvokeAsync(controller => controller.TypeTextFromToolAsync(text, submit, ct), ct);

    public Task<BrowserControlResult> ResizeAsync(double width, double height, CancellationToken ct)
        => InvokeAsync(controller => controller.ResizeFromToolAsync(width, height, ct), ct);

    public Task<BrowserControlResult> WaitAsync(string state, string? value, int timeoutMs, CancellationToken ct)
        => InvokeAsync(controller => controller.WaitFromToolAsync(state, value, timeoutMs, ct), ct);

    public Task<BrowserControlResult> QueryAsync(string selector, bool includeText, bool includeHtml, int limit, CancellationToken ct)
        => InvokeAsync(controller => controller.QueryFromToolAsync(selector, includeText, includeHtml, limit, ct), ct);

    public Task<BrowserControlResult> HighlightAsync(string selector, string? color, CancellationToken ct)
        => InvokeAsync(controller => controller.HighlightFromToolAsync(selector, color, ct), ct);

    public Task<BrowserControlResult> SelectElementAsync(string selector, CancellationToken ct)
        => InvokeAsync(controller => controller.SelectElementFromToolAsync(selector, ct), ct);

    public Task<BrowserControlResult> FocusElementAsync(string selector, CancellationToken ct)
        => InvokeAsync(controller => controller.FocusElementFromToolAsync(selector, ct), ct);

    public Task<BrowserControlResult> FindAsync(string query, string direction, CancellationToken ct)
        => InvokeAsync(controller => controller.FindFromToolAsync(query, direction, ct), ct);

    public Task<BrowserControlResult> ClipboardAsync(string action, string? text, CancellationToken ct)
        => InvokeAsync(controller => controller.ClipboardFromToolAsync(action, text, ct), ct);

    public Task<BrowserControlResult> BookmarksAsync(string? id, CancellationToken ct)
        => InvokeAsync(controller => controller.BookmarksFromToolAsync(id, ct), ct);

    public Task<BrowserControlResult> ComputedStyleAsync(string selector, CancellationToken ct)
        => InvokeAsync(controller => controller.ComputedStyleFromToolAsync(selector, ct), ct);

    private async Task<BrowserControlResult> InvokeAsync(
        Func<IBrowserController, Task<BrowserControlResult>> action,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var controller = Volatile.Read(ref _controller);
            if (controller is null)
            {
                return BrowserControlResult.Failure(
                    "No Starling browser window is available.",
                    url: null,
                    title: null,
                    canGoBack: false,
                    canGoForward: false,
                    isBusy: false);
            }

            // Dispatcher.UIThread.InvokeAsync<T>(Func<Task<T>>) unwraps the
            // returned task, so the caller observes the tool action's result directly.
            return await Dispatcher.UIThread.InvokeAsync(() => action(controller));
        }
        finally
        {
            _gate.Release();
        }
    }
}
