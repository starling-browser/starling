using Starling.Css.Properties;
using Starling.Layout.Block;

namespace Starling.Layout.Position;

/// <summary>
/// Second-pass positioning for elements with <c>position: absolute</c> or
/// <c>position: fixed</c>. Runs after normal in-flow layout has set every
/// box's <c>Frame</c>. For each out-of-flow box, this pass:
/// <list type="number">
///   <item>Finds the containing block — the nearest positioned ancestor's
///   padding box, or the initial containing block (the viewport) when no
///   positioned ancestor exists, or always the viewport for <c>fixed</c>.</item>
///   <item>Resolves <c>top</c>/<c>right</c>/<c>bottom</c>/<c>left</c>
///   (with percentages against the containing block's content-box size).</item>
///   <item>Determines the box's used width/height per CSS 2.1 §10.3.7 +
///   §10.6.4 (simplified — see <see cref="ResolveAxis"/>).</item>
///   <item>Lays out the box's subtree at the resolved width (so descendants
///   stay scoped inside the positioned box, establishing a BFC).</item>
///   <item>Translates the resulting document-space rect back into the
///   element's parent's content-box coordinates and writes
///   <see cref="Box.Box.Frame"/>.</item>
/// </list>
/// Simplifications relative to spec:
/// <list type="bullet">
///   <item>Static-position fallback when both insets of an axis are
///   <c>auto</c> uses the hypothetical position the flow pass recorded on
///   the box (<see cref="Box.Box.StaticX"/>/<c>StaticY</c> — CSS 2.1
///   §10.3.7/§10.6.4), approximated as the stack cursor at the parent's
///   left content edge; sibling margin collapse and the flex sole-item
///   alignment offsets are not replayed.</item>
///   <item>Margin auto on positioned elements: not yet centered against the
///   containing block; margins resolve to their computed value (zero by
///   default).</item>
///   <item>z-index is parsed into <see cref="PositionedProps.ZIndex"/> but
///   does not affect paint order — the painter still walks tree order.</item>
/// </list>
/// </summary>
internal sealed class PositionLayout
{
    private readonly BlockLayout _block;
    private readonly Size _viewport;
    private readonly Rect _initialContainingBlock;

    /// <summary>The document's scroll store, when a scroll model is attached
    /// (browser-plan/scroll-model.md WP5). With a store, every sticky element
    /// keeps its natural in-flow frame and the pass records
    /// <see cref="Scroll.StickyConstraints"/> instead — the shift is applied
    /// at paint/hit-test/rect time from the bound scroller's live offset.
    /// Without a store (no scroller exists), sticky stays on the
    /// clamped-relative layout shift, keeping static renders pixel-identical.</summary>
    private readonly Scroll.ScrollStateStore? _scroll;

    public PositionLayout(BlockLayout block, Size viewport, Scroll.ScrollStateStore? scrollStore = null)
    {
        _block = block;
        _viewport = viewport;
        _scroll = scrollStore;
        _initialContainingBlock = new Rect(0, 0, viewport.Width, viewport.Height);
    }

    /// <summary>
    /// Walk the box tree rooted at <paramref name="root"/>, applying the
    /// positioning pass to every out-of-flow descendant. The root itself is
    /// always treated as in-flow (it's the layout document root).
    /// </summary>
    public void LayoutPositioned(Box.Box root)
    {
        // First, apply the cheap "relative" post-translation everywhere so
        // that any positioned ancestor's padding-box rect we read later
        // reflects the shifted position.
        ApplyRelativeOffsets(root, parentOriginX: 0, parentOriginY: 0);

        // Then place out-of-flow descendants. We do this as a second pass
        // because resolving a containing block needs ancestors with final
        // frames already written.
        PositionOutOfFlow(root);
    }

    // ---------------------------------------------------------------------
    // Pass 1: position: relative — shift the element's frame by (left, top).
    // ---------------------------------------------------------------------

    /// <summary>
    /// Apply <c>position: relative</c> offsets in-place. Siblings are not
    /// re-flowed; only this element's painted box shifts.
    /// </summary>
    private void ApplyRelativeOffsets(Box.Box box, double parentOriginX, double parentOriginY)
    {
        var props = PositionParser.Parse(box.Style);
        if (props.Kind is PositionKind.Relative)
        {
            // Percentage basis for top/bottom is the containing block's
            // *height*, and for left/right is its *width*. The containing
            // block for an in-flow element is its parent's content box.
            var (cbWidth, cbHeight) = ContainingBlockContentSizeOf(box.Parent);

            var leftPx = props.Left.Resolve(cbWidth);
            var rightPx = props.Right.Resolve(cbWidth);
            var topPx = props.Top.Resolve(cbHeight);
            var bottomPx = props.Bottom.Resolve(cbHeight);

            // CSS 2.1 §9.4.3: if both left & right are non-auto, `right`
            // is ignored for LTR. Same for top/bottom: `bottom` is ignored
            // when both set. So `left`/`top` win; otherwise either side can
            // shift (right = -right offset, bottom = -bottom offset).
            double dx = 0, dy = 0;
            if (leftPx is { } lx)
            {
                dx = lx;
            }
            else if (rightPx is { } rx)
            {
                dx = -rx;
            }

            if (topPx is { } ty)
            {
                dy = ty;
            }
            else if (bottomPx is { } by)
            {
                dy = -by;
            }

            var natural = NaturalFrameOf(box);
            CommitShift(box, natural, natural.Translate(dx, dy));
        }
        else if (props.Kind is PositionKind.Sticky && _scroll is null)
        {
            // No scroll model attached: sticky degrades to clamped-relative —
            // the natural frame only shifts if an inset is violated relative
            // to the containing block's content rect. The CB-local space
            // is `(0, 0, cbWidth, cbHeight)` because `box.Frame` is in
            // parent-content-box coordinates already. With a store, the box
            // keeps its natural frame here and the positioning walk records
            // StickyConstraints instead; the shift is paint-time arithmetic
            // (scroll-model.md: layout frames never move, so the incremental
            // reuse keys stay clean).
            var (cbWidth, cbHeight) = ContainingBlockContentSizeOf(box.Parent);
            var cbLocal = new Rect(0, 0, cbWidth, cbHeight);
            var natural = NaturalFrameOf(box);
            CommitShift(box, natural, StickyLayout.ResolveOffset(natural, cbLocal, props));
        }

        // Recurse into children with the (possibly shifted) origin baked in.
        // Origin tracking isn't strictly required for the relative pass —
        // frames are stored in parent-relative coords — but we keep it here
        // in case we want to add coverage later.
        foreach (var child in box.Children)
        {
            ApplyRelativeOffsets(child, parentOriginX + box.Frame.X, parentOriginY + box.Frame.Y);
        }
    }

    /// <summary>
    /// The box's natural (pre-shift) frame. Normally that is the current
    /// <see cref="Box.Box.Frame"/> — every layout seam that places a box
    /// writes its natural position. But a box deeper than one level inside a
    /// clean reused subtree is NOT re-placed by an incremental pass: its frame
    /// still carries the shift this pass applied last time, and translating it
    /// again would compound (the y=100 -> 150 -> 200 drift). The position pass
    /// records the exact frame it writes (<see cref="Box.Box.RelShiftedFrame"/>).
    /// Every re-lay seam clears <see cref="Box.Box.RelShiftValid"/>
    /// (<c>BlockLayout.NoteRelaid</c>), so a valid flag means no seam
    /// re-placed the box and the recorded natural origin is the basis. The
    /// frame-equality check stays as defense for frame writes outside the
    /// instrumented seams (e.g. flex container repositioning of clean items):
    /// a moved frame resets the basis to the live frame.
    /// </summary>
    private static Rect NaturalFrameOf(Box.Box box)
        => box.RelShiftValid && box.Frame == box.RelShiftedFrame
            ? new Rect(box.RelNaturalX, box.RelNaturalY, box.Frame.Width, box.Frame.Height)
            : box.Frame;

    /// <summary>Write the shifted frame and the idempotency bookkeeping for
    /// <see cref="NaturalFrameOf"/>. Frames that did not move still record,
    /// so a later pass can tell "unshifted" from "never visited".</summary>
    private void CommitShift(Box.Box box, Rect natural, Rect shifted)
    {
        // Frame write + scroll bookkeeping first, idempotency bookkeeping
        // last — re-lay seams clear RelShiftValid, so the valid flag must be
        // the final write of the commit.
        if (shifted != box.Frame)
        {
            box.Frame = shifted;
            _block.NoteFrameMoved(box); // ancestors' cached scroll extents now include a moved rect
        }
        box.RelNaturalX = natural.X;
        box.RelNaturalY = natural.Y;
        box.RelShiftedFrame = shifted;
        box.RelShiftValid = true;
    }

    // ---------------------------------------------------------------------
    // Pass 2: position: absolute / fixed.
    // ---------------------------------------------------------------------

    private void PositionOutOfFlow(Box.Box root)
    {
        // We need to know, for each box, the document-space offset of its
        // padding-box top-left so it can serve as a containing block for
        // any absolutely-positioned descendant. Walk depth-first and keep a
        // stack of (positioned ancestor → padding-box document rect).
        var ancestorStack = new System.Collections.Generic.Stack<Rect>();
        Walk(root, parentContentOriginX: 0, parentContentOriginY: 0, ancestorStack,
            scroller: null, scrollerPadX: 0, scrollerPadY: 0);
    }

    // scroller / scrollerPadX / scrollerPadY: the nearest scrolling-ancestor
    // element for sticky descendants of this subtree (null = the root
    // document scroller) and the document-space origin of its padding box —
    // the scrollport origin every sticky constraint is expressed against.
    // Only meaningful when a scroll store is attached.
    private void Walk(Box.Box box, double parentContentOriginX, double parentContentOriginY, System.Collections.Generic.Stack<Rect> ancestorStack,
        Starling.Dom.Element? scroller, double scrollerPadX, double scrollerPadY)
    {
        // box.Frame is the *border-box* rect in parent's content-box coords.
        var borderBoxDocX = parentContentOriginX + box.Frame.X;
        var borderBoxDocY = parentContentOriginY + box.Frame.Y;

        // Padding-box rect (= border-box inset by border edges).
        var paddingBoxRect = new Rect(
            borderBoxDocX + box.Border.Left,
            borderBoxDocY + box.Border.Top,
            box.Frame.Width - box.Border.Horizontal,
            box.Frame.Height - box.Border.Vertical);

        // Content-box origin in document space (for descendants' frames).
        var contentOriginX = paddingBoxRect.X + box.Padding.Left;
        var contentOriginY = paddingBoxRect.Y + box.Padding.Top;

        var props = PositionParser.Parse(box.Style);

        // Sticky constraints at layout time (scroll-model.md WP5): natural
        // frame, containing block, resolved insets, and the one scroller this
        // element binds to — everything scroll-time arithmetic needs, all in
        // the scroller's padding-box (scrollport-origin) space. Recorded every
        // pass; the store sweeps entries the pass did not re-record.
        if (_scroll is { } scrollStore && props.Kind == PositionKind.Sticky && box.Element is { } stickyEl)
        {
            var (cbW, cbH) = ContainingBlockContentSizeOf(box.Parent);
            var cbRight = parentContentOriginX + cbW - scrollerPadX;
            var cbBottom = parentContentOriginY + cbH - scrollerPadY;
            // When the containing block IS the bound scroller, the constraint
            // rectangle is the scroller's scrollable area, not its (fixed)
            // content box — otherwise the canonical "sticky header as direct
            // child of the scroller" pins for only contentHeight−natural px
            // and then stops dead mid-scroll. v1 models that as unbounded end
            // edges, exact whenever element+inset fit inside the scrollport.
            if (box.Parent is { } cbBox && ReferenceEquals(cbBox.Element, scroller) && scroller is not null)
            {
                cbRight = double.PositiveInfinity;
                cbBottom = double.PositiveInfinity;
            }
            scrollStore.RecordSticky(stickyEl, new Scroll.StickyConstraints(
                NaturalX: borderBoxDocX - scrollerPadX,
                NaturalY: borderBoxDocY - scrollerPadY,
                Width: box.Frame.Width,
                Height: box.Frame.Height,
                CbX: parentContentOriginX - scrollerPadX,
                CbY: parentContentOriginY - scrollerPadY,
                CbRight: cbRight,
                CbBottom: cbBottom,
                Top: props.Top.Resolve(cbH),
                Right: props.Right.Resolve(cbW),
                Bottom: props.Bottom.Resolve(cbH),
                Left: props.Left.Resolve(cbW),
                Scroller: scroller));
        }

        // If this box is out-of-flow, position it now. The containing block
        // is the top-of-stack ancestor for absolute; or the viewport for
        // fixed.
        if (props.IsOutOfFlow)
        {
            Rect cb;
            if (props.Kind == PositionKind.Fixed)
            {
                cb = _initialContainingBlock;
            }
            else // Absolute
            {
                cb = ancestorStack.Count > 0 ? ancestorStack.Peek() : _initialContainingBlock;
            }

            PlaceAbsoluteOrFixed(box, props, cb, parentContentOriginX, parentContentOriginY,
                staticDocX: parentContentOriginX + box.StaticX,
                staticDocY: parentContentOriginY + box.StaticY);

            // The placement re-laid this box wholesale (out-of-flow boxes have
            // no reuse key), so its scroll-extent chain is stale and an
            // out-of-flow scroller owes a scoped re-measure. Conservative on
            // purpose: when nothing changed, the re-measure overwrites
            // identical geometry.
            _block.NoteRelaid(box);

            // After placing, the box's frame is in *parent content* coords,
            // and we want to recurse into its children using its NEW
            // padding-box as their containing block. Recompute.
            borderBoxDocX = parentContentOriginX + box.Frame.X;
            borderBoxDocY = parentContentOriginY + box.Frame.Y;
            paddingBoxRect = new Rect(
                borderBoxDocX + box.Border.Left,
                borderBoxDocY + box.Border.Top,
                box.Frame.Width - box.Border.Horizontal,
                box.Frame.Height - box.Border.Vertical);
            contentOriginX = paddingBoxRect.X + box.Padding.Left;
            contentOriginY = paddingBoxRect.Y + box.Padding.Top;
        }

        // Push the padding-box rect for *this* box if it establishes a
        // containing block for absolute descendants.
        var pushed = props.IsContainingBlockForAbsolute;
        if (pushed)
        {
            ancestorStack.Push(paddingBoxRect);
        }

        // A scroll container rebinds sticky descendants to itself: they stick
        // against the FIRST scroller up the chain, and outer scrollers move
        // this whole subtree (constraints included) for free.
        if (_scroll is not null && box.Element is not null
            && (Scroll.ScrollOverflowMeasurer.Classify(box) & Scroll.ScrollBoxFlags.ScrollContainer) != 0)
        {
            foreach (var child in box.Children)
            {
                Walk(child, contentOriginX, contentOriginY, ancestorStack,
                    box.Element, paddingBoxRect.X, paddingBoxRect.Y);
            }
        }
        else
        {
            foreach (var child in box.Children)
            {
                Walk(child, contentOriginX, contentOriginY, ancestorStack,
                    scroller, scrollerPadX, scrollerPadY);
            }
        }

        if (pushed)
        {
            ancestorStack.Pop();
        }
    }

    /// <summary>
    /// Compute the used frame for a <c>position: absolute</c> or <c>fixed</c>
    /// element, lay out its descendants at the resolved width, and write the
    /// final <see cref="Box.Box.Frame"/> in parent-content-box coordinates.
    /// </summary>
    private void PlaceAbsoluteOrFixed(
        Box.Box box,
        PositionedProps props,
        Rect cbDocRect,
        double parentContentOriginX,
        double parentContentOriginY,
        double staticDocX,
        double staticDocY)
    {
        // Resolve box-model values against the containing block (CSS spec:
        // percentage margins/padding on absolutely-positioned boxes resolve
        // against the containing block's *width*, just like normal).
        ResolveBoxModel(box, cbDocRect.Width);

        // Resolve explicit width/height (auto → null). ResolveAxis consumes
        // BORDER-BOX sizes, so a resolved content-box width/height gets the
        // box's own padding + border added back — without that, a padded
        // positioned box lost its padding from the frame (and the content
        // width subtraction below removed it a second time).
        var widthResolved = BlockLayout.ResolveLength(box.Style, PropertyId.Width, cbDocRect.Width, _viewport, allowAuto: true);
        var heightResolved = BlockLayout.ResolveLength(box.Style, PropertyId.Height, cbDocRect.Height, _viewport, allowAuto: true);
        if (widthResolved is { } wr)
        {
            widthResolved = wr + box.Padding.Horizontal + box.Border.Horizontal;
        }

        if (heightResolved is { } hr)
        {
            heightResolved = hr + box.Padding.Vertical + box.Border.Vertical;
        }

        // Resolve insets against the containing block's content-box
        // dimensions.
        var (left, right, usedWidth) = ResolveAxis(
            startInset: props.Left,
            endInset: props.Right,
            cbStart: cbDocRect.X,
            cbExtent: cbDocRect.Width,
            explicitSize: widthResolved,
            startMargin: box.Margin.Left,
            endMargin: box.Margin.Right,
            outerInsets: box.Padding.Horizontal + box.Border.Horizontal,
            staticStart: staticDocX);

        // Default height: lay out children at the resolved width and use
        // their consumed height (block formatting context for descendants).
        // For now, lay out children at the chosen content width to get a
        // natural height even when `height: auto` and no bottom inset.
        var contentWidth = System.Math.Max(0, usedWidth - box.Padding.Horizontal - box.Border.Horizontal);
        var naturalHeight = _block.LayoutChildren(box, contentWidth);

        // CSS 2.1 §10.6.5 — a replaced element (e.g. <img>) with height:auto takes
        // its height from the intrinsic ratio at the used width, NOT from the
        // content height or the insets. Without this, an absolutely-positioned
        // image (e.g. the hero's decorative "echo" birds) collapsed to h=0.
        double? replacedHeight = null;
        if (box is Box.ImageBox img && heightResolved is null)
        {
            var iw = img.IntrinsicWidth > 0 ? img.IntrinsicWidth : 1;
            var ih = img.IntrinsicHeight > 0 ? img.IntrinsicHeight : 1;
            var replacedContentW = System.Math.Max(0, usedWidth - box.Padding.Horizontal - box.Border.Horizontal);
            replacedHeight = replacedContentW * (ih / iw) + box.Padding.Vertical + box.Border.Vertical;
        }

        // CSS 2.1 §10.6.4 — when height is `auto` but BOTH top and bottom are
        // specified, the used height is derived from the insets (cb height minus
        // top/bottom), exactly like the width axis derives width from left/right.
        // Only when at most one vertical inset is set does auto height shrink-wrap
        // to the content. Without this, an inset-sized overlay (e.g. a glow with
        // `inset: -56px`) collapsed to its zero content height and never painted.
        var bothVerticalInsets =
            props.Top.Resolve(cbDocRect.Height) is not null &&
            props.Bottom.Resolve(cbDocRect.Height) is not null;
        var heightExplicit = heightResolved ?? replacedHeight ?? (bothVerticalInsets
            ? (double?)null
            : naturalHeight + box.Padding.Vertical + box.Border.Vertical);

        var (top, bottom, usedHeight) = ResolveAxis(
            startInset: props.Top,
            endInset: props.Bottom,
            cbStart: cbDocRect.Y,
            cbExtent: cbDocRect.Height,
            explicitSize: heightExplicit,
            startMargin: box.Margin.Top,
            endMargin: box.Margin.Bottom,
            outerInsets: box.Padding.Vertical + box.Border.Vertical,
            staticStart: staticDocY);

        // Document-space top-left of the element's *border box*.
        var docX = left + box.Margin.Left;
        var docY = top + box.Margin.Top;

        // Translate into parent-content-box coordinates.
        box.Frame = new Rect(
            docX - parentContentOriginX,
            docY - parentContentOriginY,
            usedWidth,
            usedHeight);
    }

    /// <summary>
    /// Resolve one axis of a positioned box. Returns:
    /// <list type="bullet">
    ///   <item><c>start</c>: the document-space start coordinate of the
    ///   element's outer-edge (margin-box) along this axis.</item>
    ///   <item><c>end</c>: the document-space end coordinate of the
    ///   element's margin-box.</item>
    ///   <item><c>usedSize</c>: the element's border-box size along this
    ///   axis.</item>
    /// </list>
    /// Implements CSS 2.1 §10.3.7 (horizontal) / §10.6.4 (vertical),
    /// simplified — auto margins resolve to 0 rather than absorbing slack.
    /// <paramref name="staticStart"/> is the document-space hypothetical
    /// static position for this axis, used when BOTH insets are auto.
    /// </summary>
    private static (double Start, double End, double UsedSize) ResolveAxis(
        Inset startInset,
        Inset endInset,
        double cbStart,
        double cbExtent,
        double? explicitSize,
        double startMargin,
        double endMargin,
        double outerInsets,
        double staticStart)
    {
        var s = startInset.Resolve(cbExtent);
        var e = endInset.Resolve(cbExtent);

        double startCoord;
        double usedSize;

        if (s is null && e is null)
        {
            // Both auto → the hypothetical static position recorded by the
            // flow pass (CSS 2.1 §10.3.7 rule 1 / §10.6.4 rule 1): the box
            // stays where in-flow layout would have put it. x.com's fixed
            // sidebar card has no insets at all — it must paint inside the
            // sidebar (x≈837), not at the viewport origin.
            startCoord = staticStart;
            usedSize = explicitSize ?? 0;
        }
        else if (s is { } sv && e is null)
        {
            startCoord = cbStart + sv;
            usedSize = explicitSize ?? 0;
        }
        else if (s is null && e is { } ev)
        {
            // End edge anchored: usedSize must come from explicitSize, then
            // place start = cbEnd - end - usedSize - margins.
            usedSize = explicitSize ?? 0;
            startCoord = cbStart + cbExtent - ev - usedSize - startMargin - endMargin;
        }
        else
        {
            // Both set. If explicitSize is given, `end` is over-constrained
            // (spec: ignore `right` / `bottom` in LTR/TTB writing modes).
            // If explicit is null, width = remaining space.
            var startVal = s!.Value;
            var endVal = e!.Value;
            if (explicitSize is { } sz)
            {
                startCoord = cbStart + startVal;
                usedSize = sz;
            }
            else
            {
                startCoord = cbStart + startVal;
                var available = cbExtent - startVal - endVal - startMargin - endMargin;
                usedSize = System.Math.Max(0, available);
            }
        }

        usedSize = System.Math.Max(0, usedSize);
        return (startCoord, startCoord + startMargin + endMargin + usedSize, usedSize);
    }

    private static (double Width, double Height) ContainingBlockContentSizeOf(Box.Box? parent)
    {
        if (parent is null)
        {
            return (0, 0);
        }

        var contentW = System.Math.Max(0, parent.Frame.Width - parent.Padding.Horizontal - parent.Border.Horizontal);
        var contentH = System.Math.Max(0, parent.Frame.Height - parent.Padding.Vertical - parent.Border.Vertical);
        return (contentW, contentH);
    }

    private void ResolveBoxModel(Box.Box box, double containerWidth)
    {
        // CSS 2.1 §8.3/§8.4 — percentage margins/padding resolve against the
        // containing block's width on all four sides (vertical included).
        box.Margin = new Edges(
            BlockLayout.ResolveLength(box.Style, PropertyId.MarginTop, containerWidth, _viewport) ?? 0,
            BlockLayout.ResolveLength(box.Style, PropertyId.MarginRight, containerWidth, _viewport) ?? 0,
            BlockLayout.ResolveLength(box.Style, PropertyId.MarginBottom, containerWidth, _viewport) ?? 0,
            BlockLayout.ResolveLength(box.Style, PropertyId.MarginLeft, containerWidth, _viewport) ?? 0);

        box.Padding = new Edges(
            BlockLayout.ResolveLength(box.Style, PropertyId.PaddingTop, containerWidth, _viewport) ?? 0,
            BlockLayout.ResolveLength(box.Style, PropertyId.PaddingRight, containerWidth, _viewport) ?? 0,
            BlockLayout.ResolveLength(box.Style, PropertyId.PaddingBottom, containerWidth, _viewport) ?? 0,
            BlockLayout.ResolveLength(box.Style, PropertyId.PaddingLeft, containerWidth, _viewport) ?? 0);

        // Border resolution is left at zero for the positioned scope — the
        // flex path uses the same simplification. Borders on positioned
        // elements can land alongside flex border support.
        box.Border = Edges.Zero;
    }
}
