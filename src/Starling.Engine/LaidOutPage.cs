using Microsoft.Extensions.Logging;
using Starling.Css.Cascade;
using Starling.Dom;
using Starling.Layout;
using Starling.Layout.Box;
using Starling.Layout.Incremental;
using Starling.Layout.Tree;
using Starling.Net;
using Starling.Net.Http;
using Starling.Paint;

namespace Starling.Engine;

/// <summary>
/// A fully laid-out page exposed to interactive shells. Owns the box tree
/// (positions in document-space CSS px), the source DOM, and the resolver
/// objects backing image/stylesheet lookups — disposing the page releases
/// those resources.
/// </summary>
/// <remarks>
/// Interactive callers walk <see cref="Root"/> to render native views, hit-test
/// taps, drive Cmd-F, etc. The headless render path stays on
/// <see cref="StarlingEngine.RenderAsync"/> and goes through the rasterizer.
/// </remarks>
public sealed class LaidOutPage : IDisposable
{
    // Process-wide monotonic source for DisplayListVersion. Each fresh layout
    // produces a new LaidOutPage and therefore a new version, so a re-layout /
    // re-style invalidates the picture cache wholesale (wp:M12-02). A scroll
    // reuses the same page object — same version — so its render can hit the
    // cache. Smarter partial invalidation is wp:M12-06.
    private static int _nextVersion;

    private readonly ImageFetcher _images;
    private readonly StylesheetFetcher _stylesheets;
    private readonly FontFaceRegistry _webFonts;
    // Shared HTTP client backing the fetchers above (one pool / H2 manager / DNS
    // cache for the whole page). The fetchers don't own it, so the page disposes
    // it here — keeping it alive lets late/lazy resource fetches (e.g. images
    // pulled in by a paint host) reuse the page's warm connections. Null for
    // pages whose resources were transferred to a relayout successor.
    private readonly StarlingHttpClient? _http;
    private PageScripting? _scripting;
    private LayoutSession? _layoutSession;
    private bool _disposed;
    private bool _resourcesTransferred;

    internal LaidOutPage(
        BlockBox root,
        Document document,
        StyleEngine style,
        Size viewport,
        string url,
        string? title,
        ImageFetcher images,
        StylesheetFetcher stylesheets,
        FontFaceRegistry webFonts,
        float? defaultFontSize,
        ConnectionSecurity? security = null,
        StarlingHttpClient? http = null,
        PageScripting? scripting = null,
        Starling.Layout.Scroll.ScrollStateStore? scrollState = null)
    {
        ScrollState = scrollState ?? new Starling.Layout.Scroll.ScrollStateStore();
        ScrollOffsetLookup = ScrollState.GetOffset;
        StickyShiftLookup = ScrollState.GetStickyShift;
        Root = root;
        Document = document;
        Style = style;
        Viewport = viewport;
        Url = url;
        Title = title;
        Security = security;
        _images = images;
        _stylesheets = stylesheets;
        _webFonts = webFonts;
        _http = http;
        _scripting = scripting;
        DefaultFontSize = defaultFontSize;
        DisplayListVersion = System.Threading.Interlocked.Increment(ref _nextVersion);
    }

    public BlockBox Root { get; }
    public Document Document { get; }

    /// <summary>
    /// The one shared scroll store for this document
    /// (browser-plan/scroll-model.md): per-element clamped offsets,
    /// scrollports, and scrollable overflow, refreshed by every layout pass
    /// this page runs. The shells route wheel input through it (WP2) and the
    /// bindings read it via <see cref="Starling.Bindings.ILayoutHost"/> (WP3).
    /// Owned by the engine session: created with the page and transferred to
    /// relayout successors so offsets survive reflows.
    /// </summary>
    public Starling.Layout.Scroll.ScrollStateStore ScrollState { get; }

    /// <summary>
    /// <see cref="ScrollState"/>'s per-element offset lookup, pre-bound once at
    /// construction in the shape the paint and hit-test paths take
    /// (<c>DisplayListBuilder</c>'s <c>scrollOffsets</c> func,
    /// <c>BoxHitTester.HitTest</c>). Cached so per-frame renders do not
    /// allocate a fresh delegate per present.
    /// </summary>
    public Func<Element, (double X, double Y)> ScrollOffsetLookup { get; }

    /// <summary>
    /// <see cref="ScrollState"/>'s per-element <c>position: sticky</c> paint
    /// shift (<c>GetStickyShift</c>), pre-bound like
    /// <see cref="ScrollOffsetLookup"/> so per-frame renders and hit tests do
    /// not allocate a delegate per present (scroll-model.md WP5).
    /// </summary>
    public Func<Element, (double X, double Y)> StickyShiftLookup { get; }

    /// <summary>
    /// The live JS context for this page, or null for pages without scripts (or
    /// non-interactive renders). The shell uses it to dispatch DOM events into
    /// page listeners and to pump timers/rAF/fetch after first paint. Owned by
    /// the page: disposed in <see cref="Dispose"/> and transferred to a relayout
    /// successor so the realm survives reflows. Attached once, post-load, by the
    /// engine's interactive path.
    /// </summary>
    public PageScripting? Scripting => _scripting;

    /// <summary>
    /// The persistent incremental-layout session for this page, created lazily on
    /// the first incremental relayout and transferred to relayout successors (so
    /// the retained box tree and its dirty-tracking survive across frames). Null
    /// until incremental layout first runs, or when the feature is off.
    /// </summary>
    internal LayoutSession GetOrCreateLayoutSession(ILoggerFactory loggerFactory)
        => _layoutSession ??= new LayoutSession(Style, _images, loggerFactory) { ScrollState = ScrollState };

    /// <summary>Attach the live JS context after construction — used by the
    /// interactive path when the first-paint page is returned unchanged (no
    /// deferred-script DOM mutation), so the already-built page goes live.</summary>
    internal void AttachScripting(PageScripting scripting) => _scripting = scripting;

    /// <summary>
    /// Monotonic version of this page's display list, assigned once at
    /// construction. The picture cache (wp:M12-02) keys on it: a render against a
    /// version different from the cached one is a full miss. Because every fresh
    /// layout is a new <see cref="LaidOutPage"/>, re-layout / re-style bumps the
    /// version automatically while a scroll (same page) leaves it stable.
    /// </summary>
    public int DisplayListVersion { get; }

    /// <summary>
    /// The style engine that produced <see cref="Root"/>. Interactive shells
    /// call <c>Style.Compute(element, context)</c> with a hover/focus context
    /// to get an updated <c>ComputedStyle</c> and push the diff onto the
    /// corresponding view — no re-layout needed for cosmetic state changes.
    /// </summary>
    public StyleEngine Style { get; }

    public Size Viewport { get; }
    public string Url { get; }
    public string? Title { get; }

    /// <summary>
    /// Security context of the main-document fetch (HTTP version, encryption,
    /// certificate) — backs the shell's lock affordance. Null for non-network
    /// documents (file://) or when the fetch didn't record it.
    /// </summary>
    public ConnectionSecurity? Security { get; }

    /// <summary>
    /// The full laid-out page height in CSS px — typically taller than the
    /// viewport. The shell sizes its scroll surface to this value.
    /// </summary>
    public double DocumentHeight => Root.Frame.Height;

    /// <summary>Default font size used when the page was first laid out.
    /// Re-paints (e.g. via <see cref="StarlingEngine.RenderFrame"/>) reuse
    /// it so frame-to-frame rasters stay consistent.</summary>
    internal float? DefaultFontSize { get; }

    internal ImageFetcher Images => _images;

    /// <summary>
    /// The image resolver behind this page, exposed for paint hosts that
    /// want to re-render at a different scale or with a different style
    /// override and still resolve <c>&lt;img&gt;</c> / CSS-<c>url()</c>
    /// references.
    /// </summary>
    public IImageResolver ImageResolver => _images;
    internal StylesheetFetcher Stylesheets => _stylesheets;
    internal FontFaceRegistry WebFonts => _webFonts;

    /// <summary>
    /// Produces a successor page from a fresh box tree / style engine laid out
    /// over the <em>same</em> <see cref="Document"/> and resource resolvers —
    /// the relayout path used when the viewport changes (window resize) without
    /// a navigation. The already-fetched images, stylesheets, and web fonts are
    /// handed to the successor, so disposing this page afterward leaves them
    /// alone (the successor owns them now). The caller must show the returned
    /// page and dispose this one.
    /// </summary>
    internal LaidOutPage Relayout(BlockBox root, StyleEngine style, Size viewport)
    {
        _resourcesTransferred = true;
        var successor = new LaidOutPage(
            root, Document, style, viewport, Url, Title,
            _images, _stylesheets, _webFonts, DefaultFontSize, Security, _http, _scripting,
            ScrollState);
        // The incremental session owns the retained tree across reflows, so it
        // rides along to the successor (which now exposes that tree as its Root).
        successor._layoutSession = _layoutSession;
        return successor;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // A page whose resources were handed to a relayout successor must not
        // release them — they are still in use by that successor. (The live JS
        // context rides along under the same transfer flag.)
        if (_resourcesTransferred)
        {
            return;
        }

        _scripting?.Dispose();
        _images.Dispose();
        _stylesheets.Dispose();
        _webFonts.Dispose();
        // Disposed last: the fetchers may flush in-flight work on dispose, and
        // they share this client. A relayout successor took ownership instead
        // (transferred above), so this only fires for the terminal page.
        _http?.Dispose();
    }
}
