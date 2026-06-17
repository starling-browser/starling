using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Layout.Box;

namespace Starling.Layout.Scroll;

/// <summary>
/// Post-layout measurement pass: records every scroll container's scrollport
/// (padding box) and scrollable overflow into the <see cref="ScrollStateStore"/>,
/// plus the root entry for the document scroller.
/// </summary>
/// <remarks>
/// <para>Scrollable overflow follows CSS Overflow 3 §2.2: the union of the
/// box's own padding box with the border boxes of in-flow and positioned
/// descendants, in the container's padding-box space — deep descendants and
/// positioned boxes included, not the direct-children guess the old panel
/// code used. Two scope cuts, both per the spec's shape:</para>
/// <list type="bullet">
///   <item>The walk does not descend into a descendant that establishes its
///   own scroll container or otherwise clips (<c>hidden</c>/<c>clip</c>) —
///   that box's inner overflow belongs to it, only its border box leaks
///   out.</item>
///   <item>Top/left overhang (negative margins) is dropped: the scroll
///   origin is the padding-box corner, so content above/left of it is
///   unreachable in LTR and must not widen the scrolling area (CSSOM View
///   scrolling-area definition).</item>
/// </list>
/// <para>Block-end padding joins the scrollable overflow once content
/// overflows past the content box's block end (Chromium and Firefox:
/// <c>padding:10px</c> plus a 300px child in a 100px-tall scroller gives
/// <c>scrollHeight</c> 320). The inline axis stays content-only, matching
/// Chromium's block-container behavior.</para>
/// <para>v1 deviations, called out in browser-plan/scroll-model.md: only
/// <c>auto</c>/<c>scroll</c> boxes get store entries — an
/// <c>overflow: hidden</c> box is programmatically scrollable per spec but
/// is out of v1 scope (it still clips, and still bounds this walk).
/// <c>position: fixed</c> descendants are viewport-anchored and contribute
/// to no scrolling area. An absolutely positioned descendant whose
/// containing block sits outside the scroller is still counted (its frame is
/// stored parent-relative); the spec excludes it, but the case is rare and
/// the error is only ever a too-large scroll range.</para>
/// <para>Runs after the position pass — positioned boxes get their frames
/// there, and the doc's overflow definition includes them, so measuring when
/// the block pass finishes a box would under-report.</para>
/// <para><b>Cost model.</b> Per-box classification is memoized on the box
/// (<see cref="Classify"/>), and each box caches its subtree extent relative
/// to its own frame origin (<see cref="Box.Box.ScrollExtentRight"/>). The
/// layout seams clear those caches chain-to-root for every box they actually
/// re-lay, so <see cref="MeasureScoped"/> — the incremental-relayout path —
/// recomputes only relaid subtrees and re-records only relaid scroll
/// containers. An animation tick that moves nothing scroll-relevant costs
/// O(1) here instead of the full O(n) style-resolving walk it used to. The
/// full <see cref="Measure"/> distrusts and rewrites every cache, so a full
/// layout stays the always-correct reference.</para>
/// <para>Caching is restricted to block-level containers reached by the
/// stamped layout seams (<c>BlockLayout.Stamp</c>/<c>LayoutItem</c>, the
/// float and positioned-box hooks). Inline formatting content — including
/// atomic inline-blocks and everything under them — is re-laid by throwaway
/// sub-passes that never stamp, so it is never cached and is recomputed
/// whenever its (always-stamped) enclosing block is.</para>
/// </remarks>
internal static class ScrollOverflowMeasurer
{
    // ---- Entry points --------------------------------------------------------

    /// <summary>Full measure: record <paramref name="root"/>'s laid-out tree
    /// into <paramref name="store"/>. Distrusts and rewrites every extent
    /// cache, so it is correct regardless of what earlier passes did. Used by
    /// full rebuilds and the one-shot <c>LayoutEngine</c> path.</summary>
    internal static void Measure(Box.Box root, Size viewport, ScrollStateStore store)
    {
        RecordRoot(root, viewport, store, trustCaches: false);
        Visit(root, store, cacheable: true);
    }

    /// <summary>
    /// Scoped measure for an incremental relayout: re-record the document
    /// extent (from the extent caches, which the relayout invalidated exactly
    /// where it re-laid) and re-measure only <paramref name="relaidScrollers"/>
    /// — the scroll containers the pass actually laid out, collected by the
    /// layout seams. Untouched scrollers keep their last-recorded geometry,
    /// which is still exact because a subtree the pass did not lay out (and
    /// did not move internally) measures the same.
    /// </summary>
    internal static void MeasureScoped(
        Box.Box root, Size viewport, ScrollStateStore store, List<Box.Box> relaidScrollers)
    {
        RecordRoot(root, viewport, store, trustCaches: true);
        foreach (var box in relaidScrollers)
        {
            box.ScrollMeasureQueued = false;
            MeasureContainer(box, store, trustCaches: true,
                cacheableInterior: box.Kind == BoxKind.BlockContainer);
        }
    }

    /// <summary>Re-measure one scroll container without touching the extent
    /// caches — for containers the stamp seams cannot see
    /// (<see cref="IsStampReachable"/> false), re-measured every scoped pass.</summary>
    internal static void MeasureContainerUncached(Box.Box box, ScrollStateStore store)
        => MeasureContainer(box, store, trustCaches: true, cacheableInterior: false);

    // ---- Classification (memoized per box) -----------------------------------

    /// <summary>This box's overflow/position classification, computed from its
    /// style once and memoized on the box (the style is immutable for the
    /// box's lifetime — style changes rebuild the box).</summary>
    internal static ScrollBoxFlags Classify(Box.Box box)
    {
        var f = box.ScrollFlags;
        if ((f & ScrollBoxFlags.Computed) != 0)
        {
            return f;
        }

        f = ScrollBoxFlags.Computed;
        if (box.Style is { } style)
        {
            var ox = style.Get(PropertyId.OverflowX);
            var oy = style.Get(PropertyId.OverflowY);
            if (IsScrollKeyword(ox) || IsScrollKeyword(oy))
            {
                f |= ScrollBoxFlags.ScrollContainer | ScrollBoxFlags.ClipsOverflow;
            }
            else if (IsClipKeyword(ox) || IsClipKeyword(oy))
            {
                f |= ScrollBoxFlags.ClipsOverflow;
            }

            if (style.Get(PropertyId.Position) is CssKeyword { Name: var p }
                && p.Equals("fixed", StringComparison.OrdinalIgnoreCase))
            {
                f |= ScrollBoxFlags.FixedPosition;
            }
        }
        box.ScrollFlags = f;
        return f;
    }

    /// <summary>
    /// True when every re-lay of <paramref name="box"/>'s subtree is visible
    /// to the stamped layout seams — i.e. its scoped re-measurement can rely
    /// on the relaid-scroller queue. False for scroll containers living in
    /// inline formatting content (an inline-block scroller, or a block
    /// scroller nested inside one): those are re-laid by throwaway
    /// non-incremental sub-passes that never stamp, so the session re-measures
    /// them on every scoped pass instead.
    /// </summary>
    internal static bool IsStampReachable(Box.Box box)
    {
        if (box.Kind != BoxKind.BlockContainer)
        {
            return false;
        }

        for (var b = box.Parent; b is not null; b = b.Parent)
        {
            if (b.Kind == BoxKind.Inline)
            {
                return false;
            }
        }

        return true;
    }

    // ---- Cache invalidation (called from the layout seams) -------------------

    /// <summary>
    /// Clear the cached subtree extents from <paramref name="box"/> up to the
    /// root, so the next measure recomputes exactly the chain this re-lay
    /// dirtied. The climb stops early at an already-invalid ancestor: within a
    /// pass every invalidation runs through here, so an invalid ancestor's own
    /// ancestors are already invalid. (The one at-rest exception — a box that
    /// was never measured because it sits under a non-scroller clip root —
    /// only ever causes a harmless extra climb to the root, never a missed
    /// invalidation, because a measured box's ancestors are always measured.)
    /// </summary>
    internal static void InvalidateExtentsToRoot(Box.Box box)
    {
        for (var b = box; b is not null; b = b.Parent)
        {
            if (!b.ScrollExtentValid && !ReferenceEquals(b, box))
            {
                break;
            }

            b.ScrollExtentValid = false;
        }
    }

    // ---- Internals -----------------------------------------------------------

    /// <summary>Full-measure dispatch walk: re-record every scroll container.
    /// <paramref name="cacheable"/> turns false for inline subtrees and stays
    /// false below them (their layout is invisible to the stamp seams).</summary>
    private static void Visit(Box.Box box, ScrollStateStore store, bool cacheable)
    {
        cacheable = cacheable && box.Kind is BoxKind.BlockContainer or BoxKind.AnonymousBlock;

        if (box.Element is not null && (Classify(box) & ScrollBoxFlags.ScrollContainer) != 0)
        {
            MeasureContainer(box, store, trustCaches: false,
                cacheableInterior: cacheable && box.Kind == BoxKind.BlockContainer);
        }

        foreach (var child in box.Children)
        {
            Visit(child, store, cacheable);
        }
    }

    /// <summary>Document scroller: scrollport = viewport, scrollable overflow
    /// = the page extent (the root border box plus anything poking past it).</summary>
    private static void RecordRoot(Box.Box root, Size viewport, ScrollStateStore store, bool trustCaches)
    {
        double right = 0, bottom = 0;
        if ((Classify(root) & ScrollBoxFlags.FixedPosition) == 0)
        {
            var (r, b) = SubtreeExtent(root, cacheable: true, trustCaches);
            right = root.Frame.X + r;
            bottom = root.Frame.Y + b;
        }
        store.RecordRootGeometry(
            viewport.Width, viewport.Height,
            Math.Max(viewport.Width, right), Math.Max(viewport.Height, bottom));
    }

    /// <summary>Record one scroll container's scrollport + scrollable overflow.
    /// Scrollport = padding box = border box minus borders; overlay scrollbars
    /// by decision (zero thickness), so no scrollbar inset.</summary>
    private static void MeasureContainer(Box.Box box, ScrollStateStore store, bool trustCaches, bool cacheableInterior)
    {
        var portW = Math.Max(0, box.Frame.Width - box.Border.Horizontal);
        var portH = Math.Max(0, box.Frame.Height - box.Border.Vertical);

        // Children frames are in the container's content-box space; the
        // overflow rect is measured from the padding-box origin, which is
        // (Padding.Left, Padding.Top) above/left of the content origin.
        double right = 0, bottom = 0;
        var cdx = box.Padding.Left;
        var cdy = box.Padding.Top;
        foreach (var child in box.Children)
        {
            if ((Classify(child) & ScrollBoxFlags.FixedPosition) != 0)
            {
                continue;
            }

            var (cr, cb) = SubtreeExtent(child, cacheableInterior, trustCaches);
            var r = cdx + child.Frame.X + cr;
            var b = cdy + child.Frame.Y + cb;
            if (r > right)
            {
                right = r;
            }

            if (b > bottom)
            {
                bottom = b;
            }
        }

        // Block-end padding joins the scrollable overflow once content
        // overflows past the content box's block end (Chromium: padding:10px
        // + a 300px child in a 100px-tall scroller => scrollHeight 320). The
        // inline axis stays content-only, matching Chromium block containers.
        if (bottom > portH - box.Padding.Bottom)
        {
            bottom += box.Padding.Bottom;
        }

        // CSS Overflow 3 §2.2: the scrollable overflow rectangle always
        // contains the padding box, so it never measures under the scrollport.
        store.RecordGeometry(box.Element!, portW, portH,
            Math.Max(portW, right), Math.Max(portH, bottom));
    }

    /// <summary>
    /// The scrollable extent of <paramref name="box"/>'s subtree — its border
    /// box (or line fragments, for a text run) unioned with every non-clipped,
    /// non-fixed descendant's — relative to the box's own frame origin. Served
    /// from the per-box cache when <paramref name="trustCaches"/> and the box
    /// is cache-eligible; recomputed (and re-cached) otherwise. Text runs can
    /// have fragments past every box frame (long unbreakable text); the
    /// painter draws them at frame + fragment offset, so measure the same way.
    /// A fragmentless text run contributes negative infinity, i.e. nothing.
    /// </summary>
    private static (double Right, double Bottom) SubtreeExtent(Box.Box box, bool cacheable, bool trustCaches)
    {
        cacheable = cacheable && box.Kind is BoxKind.BlockContainer or BoxKind.AnonymousBlock;
        if (cacheable && trustCaches && box.ScrollExtentValid)
        {
            return (box.ScrollExtentRight, box.ScrollExtentBottom);
        }

        double right, bottom;
        if (box is TextBox text)
        {
            right = double.NegativeInfinity;
            bottom = double.NegativeInfinity;
            foreach (var f in text.Fragments)
            {
                var fr = f.X + f.Width;
                var fb = f.Y + f.Height;
                if (fr > right)
                {
                    right = fr;
                }

                if (fb > bottom)
                {
                    bottom = fb;
                }
            }
        }
        else
        {
            right = box.Frame.Width;
            bottom = box.Frame.Height;

            // A descendant that clips (its own scroll container, or
            // hidden/clip) owns its inner overflow — only its border box
            // contributes here, so the walk is bounded at scroller boundaries.
            if ((Classify(box) & ScrollBoxFlags.ClipsOverflow) == 0)
            {
                var cdx = box.Border.Left + box.Padding.Left;
                var cdy = box.Border.Top + box.Padding.Top;
                foreach (var child in box.Children)
                {
                    // Fixed-position boxes anchor to the viewport; they are in
                    // no ancestor's scrolling area (CSS Overflow 3 §2.2).
                    if ((Classify(child) & ScrollBoxFlags.FixedPosition) != 0)
                    {
                        continue;
                    }

                    var (cr, cb) = SubtreeExtent(child, cacheable, trustCaches);
                    var r = cdx + child.Frame.X + cr;
                    var b = cdy + child.Frame.Y + cb;
                    if (r > right)
                    {
                        right = r;
                    }

                    if (b > bottom)
                    {
                        bottom = b;
                    }
                }
            }
        }

        if (cacheable)
        {
            box.ScrollExtentRight = right;
            box.ScrollExtentBottom = bottom;
            box.ScrollExtentValid = true;
        }
        return (right, bottom);
    }

    private static bool IsScrollKeyword(CssValue? value)
        => value is CssKeyword { Name: var n }
           && (n.Equals("auto", StringComparison.OrdinalIgnoreCase)
               || n.Equals("scroll", StringComparison.OrdinalIgnoreCase));

    private static bool IsClipKeyword(CssValue? value)
        => value is CssKeyword { Name: var n }
           && (n.Equals("auto", StringComparison.OrdinalIgnoreCase)
               || n.Equals("scroll", StringComparison.OrdinalIgnoreCase)
               || n.Equals("hidden", StringComparison.OrdinalIgnoreCase)
               || n.Equals("clip", StringComparison.OrdinalIgnoreCase));
}
