using Starling.Bindings;
using Starling.Css.Cascade;
using Starling.Dom;
using Starling.Layout.Box;

namespace Starling.Engine;

/// <summary>
/// <see cref="ILayoutHost"/> implementation backed by
/// <c>Painter.LayoutDocumentWithStyle</c>. The engine builds the host against
/// the parsed (pre-script) DOM, hands it to <c>WindowBinding</c>, and the
/// bindings answer rect / offset / computed-style queries from it for the
/// duration of script execution.
/// </summary>
/// <remarks>
/// <para><b>Incremental re-layout:</b> the host tracks the document's
/// <see cref="Document.LayoutInvalidationVersion"/>. Each readback first checks whether a
/// DOM mutation has bumped the version since the last layout; if so it lazily
/// re-runs the cascade + layout via the recompute delegate and rebuilds its
/// element→box index before answering. This makes mutate-then-measure idioms
/// (append sized content, then read <c>getBoundingClientRect</c> /
/// <c>offsetTop</c> on the new node or a following sibling) reflect the
/// post-mutation layout rather than the pre-script snapshot. Layout runs at
/// most once per quiescent batch of mutations because the version only advances
/// on the next mutation.</para>
/// </remarks>
internal sealed class BoxLayoutHost : ILayoutHost
{
    private readonly Dictionary<Element, Box> _boxByElement = new(ReferenceEqualityComparer.Instance);
    private readonly Document? _document;
    /// <summary>Recompute delegate. The string argument is the trigger reason
    /// — the binding entry that forced layout (e.g. <c>"offsetWidth"</c>,
    /// <c>"getBoundingClientRect"</c>, <c>"getComputedStyle:visibility"</c>).
    /// Engine.cs passes this through to <c>Activity.Current?.SetTag</c> on the
    /// <c>engine.prelayout_for_js</c> span so the trace identifies the
    /// forced-reflow culprit without needing to dump the script source.</summary>
    private readonly Func<string?, (BlockBox Root, StyleEngine Style)>? _relayout;

    /// <summary>Optional lightweight delegate that builds only the cascade
    /// (no layout). When set, <see cref="GetComputedProperty"/> uses it for
    /// purely-cascaded properties (visibility/opacity/display/…) instead of
    /// forcing a full layout via <see cref="_relayout"/>. Cheap: ~21 ms on
    /// google.com vs ~400 ms for the layout pass that the visibility read
    /// used to drag in.</summary>
    private readonly Func<StyleEngine>? _cascadeOnlyBuilder;

    /// <summary>The engine session's per-document scroll store
    /// (browser-plan/scroll-model.md). Layout passes triggered through this
    /// host refresh it (the recompute delegate threads it into
    /// <c>Painter.LayoutDocumentWithStyle</c>), and the scroll-metric reads
    /// below answer from it. Null when the owner has no store (legacy static
    /// snapshots) — reads then report "not a scroll container".</summary>
    private readonly Starling.Layout.Scroll.ScrollStateStore? _scrollState;

    private BlockBox? _root;
    private StyleEngine? _style;
    private int _laidOutVersion;
    private bool _laidOut;
    /// <summary>Document mutation version the current <see cref="_style"/>
    /// reflects. May equal <see cref="_laidOutVersion"/> (cascade built as a
    /// side effect of a layout) or be a newer version when only a cascade-only
    /// rebuild happened since the last layout. -1 means no cascade has been
    /// built yet. Tracked so repeat cascade-only reads inside one script
    /// hit the cache instead of re-running the cascade.</summary>
    private int _cascadeReadyVersion = -1;

    /// <summary>Shared cascade cache that lives alongside the held layout.
    /// Without this, every <see cref="GetComputedProperty"/> call walks the
    /// full ancestor cascade from scratch — on Google's homepage each
    /// <c>getComputedStyle(el).visibility</c> read costs ~200&#160;ms because
    /// it re-cascades a deep ancestor chain. Reset by <see cref="EnsureFresh"/>
    /// when a DOM mutation forces a re-layout.</summary>
    private CascadeCache _cascadeCache = new();

    /// <summary>Diagnostic counters — number of times <see cref="EnsureFresh"/>
    /// actually re-ran layout vs short-circuited via the
    /// <see cref="Document.LayoutInvalidationVersion"/> check. Useful for confirming the
    /// cache is doing its job on a real page: each layout run is ~200 ms on a
    /// medium page, so a count &gt; 1 inside one script means
    /// <see cref="Document.LayoutInvalidationVersion"/> is being bumped mid-execution.</summary>
    public long RelayoutCount;
    /// <summary>Reads that hit the cached layout (no re-run).</summary>
    public long FreshHits;

    /// <summary>
    /// Build a host over an already-computed layout. Without a
    /// <paramref name="document"/>/<paramref name="relayout"/> pair the host is
    /// a static snapshot — DOM mutations are not reflected.
    /// </summary>
    public BoxLayoutHost(BlockBox root, StyleEngine style,
        Document? document = null, Func<string?, (BlockBox Root, StyleEngine Style)>? relayout = null,
        Starling.Layout.Scroll.ScrollStateStore? scrollState = null)
    {
        _scrollState = scrollState;
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(style);
        _root = root;
        _style = style;
        _document = document;
        _relayout = relayout;
        _laidOutVersion = document?.LayoutInvalidationVersion ?? 0;
        Index(root);
        _laidOut = true;
    }

    /// <summary>
    /// Build a host that <b>defers</b> its first layout until a script actually
    /// reads geometry or computed style. Pages whose scripts never touch layout
    /// (analytics beacons, feature-detection, etc.) skip the pre-script layout
    /// pass entirely — the only layout that runs is the final render pass. The
    /// first geometry/computed-style read triggers the <paramref name="relayout"/>
    /// delegate; subsequent reads reuse it until a DOM mutation bumps the
    /// document's <see cref="Document.LayoutInvalidationVersion"/>.
    /// </summary>
    public BoxLayoutHost(Document document, Func<string?, (BlockBox Root, StyleEngine Style)> relayout,
        Func<StyleEngine>? cascadeOnlyBuilder = null,
        Starling.Layout.Scroll.ScrollStateStore? scrollState = null)
    {
        _scrollState = scrollState;
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(relayout);
        _document = document;
        _relayout = relayout;
        _cascadeOnlyBuilder = cascadeOnlyBuilder;
        _laidOutVersion = document.LayoutInvalidationVersion;
        _laidOut = false;
    }

    /// <summary>Resolved-value properties whose getComputedStyle return is
    /// determined by the cascade alone (no used-value layout step). Reading
    /// these via <see cref="GetComputedProperty"/> skips the forced layout
    /// pass when a <see cref="_cascadeOnlyBuilder"/> was supplied. Conservative
    /// allowlist — anything not here forces layout, matching the previous
    /// behavior.</summary>
    private static readonly HashSet<string> s_cascadeOnlyProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "visibility",
        "opacity",
        "color",
        "background-color",
        "background-image",
        "cursor",
        "display",
        "position",
        "z-index",
        "font-family",
        "font-style",
        "font-weight",
        "font-variant",
        "font-size",
        "line-height",
        "text-align",
        "text-decoration",
        "text-decoration-line",
        "text-decoration-color",
        "text-transform",
        "white-space",
        "word-break",
        "overflow-wrap",
        "direction",
        "pointer-events",
        "user-select",
        "list-style-type",
        "list-style-image",
        "list-style-position",
        "vertical-align",
        "box-sizing",
        "float",
        "clear",
    };

    /// <summary>Diagnostic counter — number of <see cref="GetComputedProperty"/>
    /// reads answered via the cascade-only fast path. Reads tied to a layout
    /// pass count toward <see cref="RelayoutCount"/> / <see cref="FreshHits"/>
    /// instead.</summary>
    public long CascadeOnlyHits;

    /// <summary>
    /// Whether a layout has actually been materialized yet. False for a deferred
    /// host whose geometry was never read — in that case there is no box tree to
    /// reuse and the engine must run its own layout for the final paint.
    /// </summary>
    public bool HasLayout => _laidOut;

    /// <summary>The document mutation version the held layout reflects.</summary>
    public int LaidOutVersion => _laidOutVersion;

    /// <summary>
    /// The layout the host currently holds, paired with the style engine it was
    /// computed with. Only valid once <see cref="HasLayout"/> is true. The engine
    /// reads this after script execution to reuse the already-materialized box
    /// tree for the final paint — skipping a redundant second cascade + layout
    /// (Win B) — when nothing layout-affecting has changed since
    /// (<see cref="LaidOutVersion"/> still equals the document's
    /// <see cref="Document.LayoutInvalidationVersion"/> and no late resources arrived).
    /// </summary>
    public (BlockBox Root, StyleEngine Style) Materialized => (_root!, _style!);

    /// <summary>
    /// If a DOM mutation has advanced the document's mutation version since the
    /// last layout, re-run layout and rebuild the element→box index. No-op for a
    /// static snapshot (no recompute delegate) or when nothing has mutated.
    /// <paramref name="trigger"/> identifies which binding entry forced this
    /// layout (e.g. <c>"offsetWidth"</c>); the engine forwards it to a tag on
    /// the emitted span so the trace pinpoints the forced-reflow culprit.
    /// </summary>
    private void EnsureFresh(string trigger)
    {
        if (_relayout is null)
        {
            return; // static snapshot — indexed at construction
        }

        if (_laidOut && (_document is null || _document.LayoutInvalidationVersion == _laidOutVersion))
        {
            FreshHits++;
            return;
        }
        RelayoutCount++;

        var (root, style) = _relayout(trigger);
        _root = root;
        _style = style;
        _boxByElement.Clear();
        // A fresh StyleEngine means every ancestor cascade we previously
        // cached is stale — entries are tied to the old engine's selector
        // index / sheet ordering. Drop the cache so it rebuilds against the
        // new engine on the next computed-style read.
        _cascadeCache = new CascadeCache();
        Index(root);
        _laidOutVersion = _document?.LayoutInvalidationVersion ?? 0;
        _laidOut = true;
        _cascadeReadyVersion = _laidOutVersion;
    }

    /// <summary>Ensure a <see cref="StyleEngine"/> is current for the document's
    /// present mutation version, WITHOUT running layout. Falls back to
    /// <see cref="EnsureFresh"/> when no cheap cascade builder was supplied.
    /// </summary>
    private void EnsureCascadeFresh(string trigger)
    {
        if (_relayout is null)
        {
            return; // static snapshot — indexed at construction
        }

        var current = _document?.LayoutInvalidationVersion ?? 0;
        // Already have a fresh cascade for this mutation version (either from
        // a previous layout or a previous cascade-only build).
        if (_style is not null && _cascadeReadyVersion == current)
        {
            FreshHits++;
            return;
        }
        if (_cascadeOnlyBuilder is not null)
        {
            _style = _cascadeOnlyBuilder();
            _cascadeCache = new CascadeCache();
            _cascadeReadyVersion = current;
            CascadeOnlyHits++;
            return;
        }
        // No cascade-only path available — fall back to the full layout so the
        // read can still be answered.
        EnsureFresh(trigger);
    }

    private void Index(Box box)
    {
        if (box.Element is { } e)
        {
            _boxByElement[e] = box;
        }

        foreach (var child in box.Children)
        {
            Index(child);
        }
    }

    public bool TryGetBoundingClientRect(Element element, out LayoutRect rect)
    {
        EnsureFresh("getBoundingClientRect");
        if (_boxByElement.TryGetValue(element, out var box))
        {
            // box.Frame.X/Y is relative to the containing block's content origin;
            // getBoundingClientRect is document-relative (viewport-relative at
            // scroll 0). Accumulate each ancestor's frame offset plus its
            // border+padding (the step from an ancestor's border-box origin to the
            // content origin its children are measured from) — the same top-down
            // origin walk DisplayListBuilder does, run bottom-up here.
            var x = box.Frame.X;
            var y = box.Frame.Y;
            // Decision 2 (scroll-model.md): a stuck element reports the STUCK
            // position, like Chromium. Layout keeps sticky frames natural, so
            // the same store-computed paint shift is added here — for the box
            // itself and for any stuck ancestor it rides on. Gated on the
            // store actually holding sticky entries so the overwhelmingly
            // common sticky-free read pays one count check, no lookups.
            var sticky = _scrollState is { HasStickyEntries: true } ? _scrollState : null;
            if (sticky is not null && box.Element is { } selfEl)
            {
                var (sx, sy) = sticky.GetStickyShift(selfEl);
                x += sx;
                y += sy;
            }
            for (var p = box.Parent; p is not null; p = p.Parent)
            {
                x += p.Frame.X + p.Border.Left + p.Padding.Left;
                y += p.Frame.Y + p.Border.Top + p.Padding.Top;
                if (sticky is not null && p.Element is { } ancestorEl)
                {
                    var (sx, sy) = sticky.GetStickyShift(ancestorEl);
                    x += sx;
                    y += sy;
                }
            }
            rect = new LayoutRect(x, y, box.Frame.Width, box.Frame.Height);
            return true;
        }
        rect = default;
        return false;
    }

    public bool TryGetOffsetMetrics(Element element, out OffsetMetrics metrics)
    {
        EnsureFresh("offset-metrics");
        if (_boxByElement.TryGetValue(element, out var box))
        {
            var frame = box.Frame;
            var padding = box.Padding;
            // offsetWidth/Height = border box (frame). clientWidth/Height
            // = padding box (frame minus borders). Without borders the
            // numbers coincide, which is fine for our snapshot.
            var border = box.Border;
            metrics = new OffsetMetrics(
                OffsetWidth: frame.Width,
                OffsetHeight: frame.Height,
                OffsetTop: frame.Y,
                OffsetLeft: frame.X,
                ClientWidth: Math.Max(0, frame.Width - border.Horizontal),
                ClientHeight: Math.Max(0, frame.Height - border.Vertical));
            _ = padding; // reserved for scrollWidth/Height once content overflow is wired.
            return true;
        }
        metrics = default;
        return false;
    }

    public bool TryGetScrollMetrics(Element element, out ScrollMetrics metrics)
    {
        if (_scrollState is null)
        {
            metrics = default;
            return false;
        }
        // Same up-to-date rule as the offset metrics: a DOM mutation since the
        // last layout forces a re-layout, which re-measures the store before
        // we read it.
        EnsureFresh("scroll-metrics");
        if (_scrollState.TryGet(element, out var s))
        {
            // clientLeft/clientTop = border widths (CSSOM §7; overlay
            // scrollbars, so no scrollbar term). The store keeps only sizes,
            // so the per-side insets come from the box itself.
            double clientLeft = 0, clientTop = 0;
            if (_boxByElement.TryGetValue(element, out var box))
            {
                clientLeft = box.Border.Left;
                clientTop = box.Border.Top;
            }
            metrics = new ScrollMetrics(
                ScrollLeft: s.OffsetX,
                ScrollTop: s.OffsetY,
                ScrollWidth: s.OverflowWidth,
                ScrollHeight: s.OverflowHeight,
                ClientWidth: s.ScrollportWidth,
                ClientHeight: s.ScrollportHeight,
                ClientLeft: clientLeft,
                ClientTop: clientTop);
            return true;
        }
        metrics = default;
        return false;
    }

    public ScrollMetrics GetRootScrollMetrics()
    {
        if (_scrollState is null)
        {
            return default;
        }

        EnsureFresh("scroll-metrics");
        var s = _scrollState.Root;
        return new ScrollMetrics(
            ScrollLeft: s.OffsetX,
            ScrollTop: s.OffsetY,
            ScrollWidth: s.OverflowWidth,
            ScrollHeight: s.OverflowHeight,
            ClientWidth: s.ScrollportWidth,
            ClientHeight: s.ScrollportHeight);
    }

    /// <summary>Write side of the JS scroll surface
    /// (browser-plan/scroll-model.md §JavaScript surface): flush layout if
    /// dirty — the same up-to-date rule the offset metrics use, so the clamp
    /// bound reflects the current DOM — then let the store clamp, store, and
    /// flag the pending scroll event. Never triggers a relayout by itself:
    /// the offset is paint/hit-test state, and the repaint rides the store's
    /// pending-event flag (drained by the shells' frame pump, WP2/WP4).</summary>
    public void SetScrollOffset(Element element, double x, double y)
    {
        if (_scrollState is null)
        {
            return;
        }

        EnsureFresh("scroll-write");
        _scrollState.Write(element, x, y);
    }

    /// <summary>Document-scroller variant of <see cref="SetScrollOffset"/>
    /// (window.scrollTo / scrollBy, root-element scrollTop/scrollLeft).</summary>
    public void SetRootScrollOffset(double x, double y)
    {
        if (_scrollState is null)
        {
            return;
        }

        EnsureFresh("scroll-write");
        _scrollState.WriteRoot(x, y);
    }

    public bool MatchMedia(string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return false;
        }

        EnsureFresh("matchMedia");
        if (_style is null)
        {
            return false;
        }

        try { return _style.MatchMedia(query); }
        catch { return false; }
    }

    public string GetComputedProperty(Element element, string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return string.Empty;
        }
        // Include the requested property in the trigger so the tag distinguishes
        // a getComputedStyle(...).visibility read (cascade-only, cheap) from a
        // .width read (layout-forcing, the one we care about).
        var trigger = "getComputedStyle:" + propertyName;
        if (s_cascadeOnlyProperties.Contains(propertyName))
        {
            EnsureCascadeFresh(trigger);
        }
        else
        {
            EnsureFresh(trigger);
        }

        if (_style is null)
        {
            return string.Empty;
        }

        try
        {
            // Pass the host's CascadeCache so repeated reads on the same
            // element (or descendants sharing an ancestor chain) reuse the
            // ancestor cascades. Without the cache, getComputedStyle in a
            // hot JS loop re-walks the chain per call.
            var computed = _style.Compute(element, context: null, _cascadeCache);
            return computed?.GetPropertyValue(propertyName) ?? string.Empty;
        }
        catch
        {
            // The cascade engine can throw for elements detached from the
            // document or for properties it doesn't recognise. The binding
            // surface is required not to throw, so fall back to the
            // unknown-property empty-string return.
            return string.Empty;
        }
    }
}
