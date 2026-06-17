namespace Starling.Gui.Mcp;

/// <summary>
/// Runs browser-control calls for native hosts that have no UI dispatcher of
/// their own. Model Context Protocol tools enqueue work from Kestrel threads.
/// The host drains the queue on its own window thread.
/// </summary>
public sealed class BrowserControlWorkQueue : IBrowserControlDispatcher
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Queue<QueuedCall> _calls = new();
    private IBrowserController? _controller;

    /// <summary>Sets the controller that drained calls run against.</summary>
    public void Attach(IBrowserController controller)
    {
        ArgumentNullException.ThrowIfNull(controller);
        Volatile.Write(ref _controller, controller);
    }

    /// <summary>Clears the controller, but only if it is still the one passed in.</summary>
    public void Detach(IBrowserController controller)
    {
        if (ReferenceEquals(Volatile.Read(ref _controller), controller))
        {
            Volatile.Write(ref _controller, null);
        }
    }

    /// <summary>
    /// Runs every queued call on the calling thread. The host calls this on its
    /// window thread. Calls with no controller attached fail with an error result.
    /// </summary>
    public void Drain()
    {
        while (TryDequeue(out var call))
        {
            Execute(call);
        }
    }

    public Task<BrowserControlResult> NavigateAsync(string url, CancellationToken ct)
        => EnqueueAsync(controller => controller.NavigateFromToolAsync(url, ct), ct);

    public Task<BrowserControlResult> BackAsync(CancellationToken ct)
        => EnqueueAsync(controller => controller.BackFromToolAsync(ct), ct);

    public Task<BrowserControlResult> ForwardAsync(CancellationToken ct)
        => EnqueueAsync(controller => controller.ForwardFromToolAsync(ct), ct);

    public Task<BrowserControlResult> ReloadAsync(CancellationToken ct)
        => EnqueueAsync(controller => controller.ReloadFromToolAsync(ct), ct);

    public Task<BrowserControlResult> ScreenshotAsync(string path, CancellationToken ct)
        => EnqueueAsync(controller => controller.ScreenshotFromToolAsync(path, ct), ct);

    public Task<BrowserControlResult> ScreenshotViewportAsync(string path, CancellationToken ct)
        => EnqueueAsync(controller => controller.ScreenshotViewportFromToolAsync(path, ct), ct);

    public Task<BrowserControlResult> InspectAsync(bool includeHtml, string? logPath, CancellationToken ct)
        => EnqueueAsync(controller => controller.InspectFromToolAsync(includeHtml, logPath, ct), ct);

    public Task<BrowserControlResult> ConsoleAsync(string? minLevel, int limit, CancellationToken ct)
        => EnqueueAsync(controller => controller.ConsoleFromToolAsync(minLevel, limit, ct), ct);

    public Task<BrowserControlResult> NetworkAsync(int limit, CancellationToken ct)
        => EnqueueAsync(controller => controller.NetworkFromToolAsync(limit, ct), ct);

    public Task<BrowserControlResult> ClickAsync(double x, double y, CancellationToken ct)
        => EnqueueAsync(controller => controller.ClickFromToolAsync(x, y, ct), ct);

    public Task<BrowserControlResult> ClickSelectorAsync(string selector, CancellationToken ct)
        => EnqueueAsync(controller => controller.ClickSelectorFromToolAsync(selector, ct), ct);

    public Task<BrowserControlResult> MoveMouseAsync(double x, double y, CancellationToken ct)
        => EnqueueAsync(controller => controller.MoveMouseFromToolAsync(x, y, ct), ct);

    public Task<BrowserControlResult> ScrollAsync(double deltaX, double deltaY, CancellationToken ct)
        => EnqueueAsync(controller => controller.ScrollFromToolAsync(deltaX, deltaY, ct), ct);

    public Task<BrowserControlResult> ScrollToAsync(double? x, double? y, string? selector, string? position, CancellationToken ct)
        => EnqueueAsync(controller => controller.ScrollToFromToolAsync(x, y, selector, position, ct), ct);

    public Task<BrowserControlResult> PressKeyAsync(string key, bool shift, bool ctrl, bool alt, bool meta, CancellationToken ct)
        => EnqueueAsync(controller => controller.PressKeyFromToolAsync(key, shift, ctrl, alt, meta, ct), ct);

    public Task<BrowserControlResult> TypeTextAsync(string text, bool submit, CancellationToken ct)
        => EnqueueAsync(controller => controller.TypeTextFromToolAsync(text, submit, ct), ct);

    public Task<BrowserControlResult> ResizeAsync(double width, double height, CancellationToken ct)
        => EnqueueAsync(controller => controller.ResizeFromToolAsync(width, height, ct), ct);

    public Task<BrowserControlResult> WaitAsync(string state, string? value, int timeoutMs, CancellationToken ct)
        => EnqueueAsync(controller => controller.WaitFromToolAsync(state, value, timeoutMs, ct), ct);

    public Task<BrowserControlResult> QueryAsync(string selector, bool includeText, bool includeHtml, int limit, CancellationToken ct)
        => EnqueueAsync(controller => controller.QueryFromToolAsync(selector, includeText, includeHtml, limit, ct), ct);

    public Task<BrowserControlResult> HighlightAsync(string selector, string? color, CancellationToken ct)
        => EnqueueAsync(controller => controller.HighlightFromToolAsync(selector, color, ct), ct);

    public Task<BrowserControlResult> SelectElementAsync(string selector, CancellationToken ct)
        => EnqueueAsync(controller => controller.SelectElementFromToolAsync(selector, ct), ct);

    public Task<BrowserControlResult> FocusElementAsync(string selector, CancellationToken ct)
        => EnqueueAsync(controller => controller.FocusElementFromToolAsync(selector, ct), ct);

    public Task<BrowserControlResult> FindAsync(string query, string direction, CancellationToken ct)
        => EnqueueAsync(controller => controller.FindFromToolAsync(query, direction, ct), ct);

    public Task<BrowserControlResult> ClipboardAsync(string action, string? text, CancellationToken ct)
        => EnqueueAsync(controller => controller.ClipboardFromToolAsync(action, text, ct), ct);

    public Task<BrowserControlResult> BookmarksAsync(string? id, CancellationToken ct)
        => EnqueueAsync(controller => controller.BookmarksFromToolAsync(id, ct), ct);

    public Task<BrowserControlResult> ComputedStyleAsync(string selector, CancellationToken ct)
        => EnqueueAsync(controller => controller.ComputedStyleFromToolAsync(selector, ct), ct);

    private async Task<BrowserControlResult> EnqueueAsync(
        Func<IBrowserController, Task<BrowserControlResult>> action,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<BrowserControlResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            CancellationTokenRegistration registration = default;
            if (ct.CanBeCanceled)
            {
                registration = ct.Register(() => completion.TrySetCanceled(ct));
            }

            lock (_calls)
            {
                _calls.Enqueue(new QueuedCall(action, completion, registration));
            }

            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryDequeue(out QueuedCall call)
    {
        lock (_calls)
        {
            if (_calls.Count == 0)
            {
                call = default!;
                return false;
            }

            call = _calls.Dequeue();
            return true;
        }
    }

    private void Execute(QueuedCall call)
    {
        try
        {
            if (call.Completion.Task.IsCompleted)
            {
                return;
            }

            var controller = Volatile.Read(ref _controller);
            var result = controller is null
                ? BrowserControlResult.Failure(
                    "No Starling browser window is available.",
                    url: null,
                    title: null,
                    canGoBack: false,
                    canGoForward: false,
                    isBusy: false)
                : call.Action(controller).GetAwaiter().GetResult();
            call.Completion.TrySetResult(result);
        }
        catch (OperationCanceledException ex)
        {
            call.Completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            call.Completion.TrySetResult(BrowserControlResult.Failure(
                ex.Message,
                url: null,
                title: null,
                canGoBack: false,
                canGoForward: false,
                isBusy: false));
        }
        finally
        {
            call.Cancellation.Dispose();
        }
    }

    private readonly record struct QueuedCall(
        Func<IBrowserController, Task<BrowserControlResult>> Action,
        TaskCompletionSource<BrowserControlResult> Completion,
        CancellationTokenRegistration Cancellation);
}
