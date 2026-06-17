using Starling.Dom;
using Starling.Dom.Events;
using Starling.Js.Hosting;
using Starling.Net;

namespace Starling.Engine;

/// <summary>
/// The live JavaScript context retained for an interactively-loaded page so the
/// shell can keep driving it <em>after</em> first paint. It lets the shell:
/// <list type="bullet">
/// <item>dispatch DOM events (input/change/focus/keydown/…) into page listeners
/// via <see cref="DispatchEvent"/>, so forms and search-as-you-type handlers
/// run; and</item>
/// <item>pump the page's event loop — microtasks, <c>setTimeout</c>/<c>setInterval</c>,
/// <c>requestAnimationFrame</c>, and <c>fetch</c>/XHR completions — against real
/// wall-clock time via <see cref="PumpFrame"/>.</item>
/// </list>
/// Owned by the <see cref="LaidOutPage"/> and disposed with it (tearing down the
/// JS HTTP client, the script fetcher, and the realm's DOM hooks). On a relayout
/// (resize / form edit) the owning page transfers this object to its successor,
/// so the same live realm survives reflows.
/// </summary>
/// <remarks>
/// Engine-neutral: the live realm is reached only through the
/// <see cref="IScriptSession"/> seam, so the same retained-context behaviour
/// works whichever JS backend (<c>STARLING_JS_ENGINE</c>) ran the page. The
/// backend owns the realm, the simulated loop, and the dynamic-script runner;
/// this host just routes the shell's post-load events and pump ticks into it.
/// </remarks>
/// <remarks>
/// Single-threaded: every method must run on the shell's UI thread. The realm is
/// not thread-safe; the load-time async phase has fully settled before the page
/// (and this host) is handed to the shell, after which only the UI thread touches
/// the realm. Off-thread <c>fetch</c>/XHR completions enqueue resolve jobs onto
/// the realm's (thread-safe) microtask queue, which <see cref="PumpFrame"/> drains.
/// </remarks>
public sealed class PageScripting : IDisposable
{
    private readonly IScriptSession _session;
    private readonly StarlingHttpClient _http;
    private readonly ScriptFetcher _fetcher;
    private readonly Document _document;
    // The page's shared scroll store (browser-plan/scroll-model.md). Offset
    // writes only flag it; this host drains the flagged set once per
    // PumpFrame and dispatches the coalesced scroll events. Null for callers
    // that predate the store (bare unit hosts).
    private readonly Starling.Layout.Scroll.ScrollStateStore? _scrollState;
    // Reused per frame so the steady-state no-scroll tick allocates nothing.
    private readonly List<Element> _scrollEventTargets = [];
    private bool _inScrollSteps;
    private bool _disposed;

    internal PageScripting(
        IScriptSession session, StarlingHttpClient http, ScriptFetcher fetcher, Document document,
        Starling.Layout.Scroll.ScrollStateStore? scrollState = null)
    {
        _session = session;
        _http = http;
        _fetcher = fetcher;
        _document = document;
        _scrollState = scrollState;
    }

    /// <summary>The document whose realm this drives — equals the owning page's.</summary>
    public Document Document => _document;

    /// <summary>
    /// Dispatch <paramref name="evt"/> to <paramref name="target"/> with the
    /// realm's VM active so JS listeners run, then drain the microtasks they
    /// queue. Returns true when the dispatch mutated the DOM (the shell should
    /// re-render). Exceptions thrown by page listeners are swallowed by the
    /// engine's console pipe, never surfacing to the shell.
    /// </summary>
    public bool DispatchEvent(EventTarget target, Event evt)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(evt);
        if (_disposed)
        {
            return false;
        }

        return _session.DispatchEvent(target, evt);
    }

    /// <summary>
    /// Advance the page's event loop to <paramref name="elapsedMs"/> (real ms
    /// elapsed since the shell began driving this host), firing any due timers
    /// and one <c>requestAnimationFrame</c> frame, and draining microtasks plus
    /// any <c>fetch</c>/XHR completions that landed since the last pump. Returns
    /// true when the DOM changed and the shell should re-render.
    /// </summary>
    public bool PumpFrame(long elapsedMs)
    {
        if (_disposed)
        {
            return false;
        }
        // HTML event loop ordering: the scroll steps run before this frame's
        // requestAnimationFrame callbacks (which fire inside the session's
        // pump below). A rAF scheduled by a scroll listener still lands in
        // this same frame's rAF phase, matching the spec.
        var mutated = RunScrollSteps();
        mutated |= _session.PumpFrame(elapsedMs);
        return mutated;
    }

    /// <summary>
    /// "Run the scroll steps" (browser-plan/scroll-model.md WP4): drain every
    /// scroll target the store flagged since the last frame and dispatch their
    /// coalesced <c>scroll</c> events through the session. Runs from the frame
    /// pump only — never from the wheel handler, never inside a paint. The
    /// flags are cleared before any listener runs, so a listener that writes
    /// an offset re-flags its target for the NEXT frame's drain instead of
    /// recursing this one (the latch guards hosts that re-enter the pump from
    /// a listener).
    /// </summary>
    private bool RunScrollSteps()
    {
        var store = _scrollState;
        if (store is null || _inScrollSteps || !store.HasPendingEvents)
        {
            return false;
        }

        _inScrollSteps = true;
        try
        {
            store.DrainPendingEventTargets(_scrollEventTargets, out var documentScrolled);
            var mutated = _session.DispatchScrollEvents(_scrollEventTargets, documentScrolled);
            _scrollEventTargets.Clear();
            return mutated;
        }
        finally
        {
            _inScrollSteps = false;
        }
    }

    /// <summary>
    /// Runs the IntersectionObserver "update intersection observations" step
    /// against the given viewport (the visible region in document CSS px: scroll
    /// offset as origin, viewport size as extent). The shell calls this on
    /// scroll/resize so reveal-on-scroll content fires as it enters the viewport.
    /// Returns true when an observer callback mutated the DOM (re-render).
    /// </summary>
    public bool UpdateIntersectionObservations(double viewportX, double viewportY, double viewportWidth, double viewportHeight)
    {
        if (_disposed)
        {
            return false;
        }

        return _session.UpdateIntersectionObservations(viewportX, viewportY, viewportWidth, viewportHeight);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.FireBeforeUnload();
        _session.FireUnload();
        // Make the document inert again and release the JS-owned resources. The
        // session's own Dispose unregisters the realm's src/inject hooks.
        _document.NodeConnected = null;
        _session.Dispose();
        _http.Dispose();
        _fetcher.Dispose();
    }
}
