using Starling.Css.Cascade;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Layout.Box;
using Starling.Layout.Incremental;
using Starling.Layout.Inline;
using Starling.Layout.Text;

namespace Starling.Layout.Block;

/// <summary>
/// Block formatting context layout. Children stack vertically; inline children
/// are folded into anonymous blocks before this pass so we only ever see block
/// children here.
/// </summary>
/// <remarks>
/// Coordinate convention: every box's <see cref="Box.Box.Frame"/> is in its
/// <em>parent's content-box</em> coordinate space. To get a document-space
/// position, the painter walks the tree adding parent content origins.
/// </remarks>
internal sealed class BlockLayout
{
    private readonly ITextMeasurer _measurer;
    private readonly Size _viewport;
    private readonly InlineLayout _inline;
    private readonly CancellationToken _abort;

    // When true, the block pass records each box's constraint space and reuses a
    // clean subtree (not dirty + same constraint) by repositioning it in O(1)
    // instead of recomputing it. Off by default, so the full-rebuild path is
    // byte-for-byte unchanged. See Starling.Layout.Incremental.
    private readonly bool _incremental;

    /// <summary>
    /// When set (the layout session's incremental-relayout path with a scroll
    /// store attached), every box this pass actually (re)lays out has its
    /// cached scroll extents invalidated chain-to-root, and every relaid
    /// scroll container is queued here for the post-pass scoped re-measure
    /// (browser-plan/scroll-model.md WP1). Null everywhere else, making the
    /// whole mechanism one null check per laid box.
    /// </summary>
    internal List<Box.Box>? RelaidScrollerSink { get; set; }

    public BlockLayout(ITextMeasurer measurer, Size viewport, CancellationToken abort = default, bool incremental = false)
    {
        _measurer = measurer;
        _viewport = viewport;
        _inline = new InlineLayout(measurer, viewport);
        _abort = abort;
        _incremental = incremental;
    }

    public void Layout(Box.Box root)
    {
        ResolveBoxModel(root, _viewport.Width);
        var contentWidth = _viewport.Width - root.Margin.Horizontal - root.Border.Horizontal - root.Padding.Horizontal;
        // The root's containing block is the viewport (CSS 2.1 §10.1). When the
        // root has `height: auto` we still hand the viewport height down as the
        // percentage basis for descendants — that matches the long-standing
        // html/body special case browsers use so `body { height: 100% }` reaches
        // the viewport instead of collapsing to 0.
        var explicitHeight = ResolveLength(root.Style, PropertyId.Height, _viewport.Height, _viewport, allowAuto: true);
        var consumedHeight = LayoutChildren(root, contentWidth, explicitHeight ?? _viewport.Height);
        var contentHeight = explicitHeight ?? consumedHeight;
        root.Frame = new Rect(
            root.Margin.Left,
            root.Margin.Top,
            contentWidth + root.Padding.Horizontal + root.Border.Horizontal,
            contentHeight + root.Padding.Vertical + root.Border.Vertical);
    }

    /// <summary>
    /// Lay out <paramref name="parent"/>'s children as a block formatting
    /// context using <paramref name="containerWidth"/> as the available
    /// content width. Returns the total consumed height. The parent's own
    /// frame is not touched — callers (e.g. <see cref="Inline.InlineLayout"/>
    /// for an inline-block) compose the result into their own box model.
    /// </summary>
    internal double LayoutChildren(Box.Box parent, double containerWidth)
        => LayoutChildren(parent, containerWidth, containerHeight: null, measure: false);

    /// <summary>
    /// Lay out a flex/grid item's contents. Identical to <c>LayoutChildren</c>
    /// for a normal block-level item, but a bare-text "anonymous flex item"
    /// (CSS Flexbox §4) is itself an <see cref="BoxKind.AnonymousBlock"/> whose
    /// children are raw inline boxes with no enclosing anonymous block — so it
    /// must establish the inline formatting context directly instead of trying
    /// to block-stack its text (which silently drops it).
    /// </summary>
    internal double LayoutItem(Box.Box item, double containerWidth, double? containerHeight, bool measure = false, bool reuseHeight = false)
    {
        if (item.Kind == BoxKind.AnonymousBlock)
        {
            // The early return must still invalidate the scroll-extent cache
            // chain: a re-wrapped bare-text flex/grid item otherwise keeps a
            // valid-but-stale extent and the scoped scroll measure diverges
            // from a full layout. NoteRelaid never queues an anonymous box
            // (it has no element); it only clears the chain to the root.
            NoteRelaid(item);
            return _inline.Layout(item, containerWidth, measure);
        }

        // Item-level reuse — the flex/grid measure→final choreography lays the
        // same item several times per pass (basis measure, min measure, final
        // contents), and each nested flex level multiplies that. A clean
        // subtree relaid under identical constraints replays its content
        // extent: measurement replay is return-value-only (callers that read
        // frames don't pass reuseHeight), final replay leaves the
        // parent-relative descendant frames exactly as the previous identical
        // pass wrote them.
        // Unlike the other reuse keys this one is NOT gated on _incremental:
        // the one-shot path builds a fresh box tree per layout run, so a stamp
        // can only ever be replayed within the same pass — where determinism
        // makes it exact — and the within-pass replay is precisely what breaks
        // the nested-flex exponential.
        var cs = new ConstraintSpace(containerWidth, containerHeight, _viewport.Width, _viewport.Height);
        if (!item.SubtreeDirty)
        {
            if (measure && reuseHeight && item.ItemMeasureConstraint == cs)
            {
                return item.ItemMeasuredContent;
            }

            if (!measure && item.ItemLaidConstraint == cs)
            {
                return item.ItemLaidContent;
            }
        }

        double content;
        // A flex/grid item can itself be a flex container (nested flex — e.g. a
        // navbar's <ul> that is both an item of the nav row and a flex row of
        // its own <li>s). LayoutChildren would block-stack the inner items;
        // route back through the flex formatting context so the inner row lays
        // out as a row. (Normal block flow reaches this via LayoutBlock; items
        // bypass that path, so the dispatch must be repeated here.)
        if (IsFlexContainer(item.Style))
        {
            var flex = new Starling.Layout.Flex.FlexLayout(this, _viewport);
            content = flex.Layout(item, containerWidth, containerHeight);
        }
        else if (IsGridContainer(item.Style))
        {
            var grid = new Starling.Layout.Grid.GridLayout(this, _viewport);
            content = grid.Layout(item, containerWidth, containerHeight);
        }
        else
        {
            content = LayoutChildren(item, containerWidth, containerHeight, measure, reuseHeight);
        }

        if (measure)
        {
            item.ItemMeasureConstraint = cs;
            item.ItemMeasuredContent = content;
            // This measurement REWROTE the subtree's frames (e.g. the
            // 1,000,000px max-content probe). Any earlier final-pass frames
            // are gone, so the final-pass reuse key must not match until a
            // real final pass runs again.
            item.ItemLaidConstraint = null;
        }
        else
        {
            item.ItemLaidConstraint = cs;
            item.ItemLaidContent = content;
            // A final pass rewrites frames too — a prior measurement's stamp
            // no longer describes them, only its returned extent (which is
            // all measure replay hands out, so that stamp stays valid).
            // The final pass is authoritative for frames — descendants are
            // clean from here on.
            item.SubtreeDirty = false;
        }
        NoteRelaid(item);
        return content;
    }

    /// <summary>
    /// Lay out children with the option of running in measurement mode, used
    /// by the inline-block shrink-to-fit pass. In measurement mode the inline
    /// formatting context skips its post-layout alignment shifts so the
    /// caller's <c>MeasureUsedWidth</c> walk sees natural pre-shift positions.
    /// </summary>
    internal double LayoutChildren(Box.Box parent, double containerWidth, bool measure)
        => LayoutChildren(parent, containerWidth, containerHeight: null, measure);

    internal double LayoutChildren(Box.Box parent, double containerWidth, double? containerHeight)
        => LayoutChildren(parent, containerWidth, containerHeight, measure: false);

    /// <summary>
    /// Lay out children with an explicit containing-block height for resolving
    /// percentage heights on descendants (CSS 2.1 §10.5). Pass <c>null</c> when
    /// the containing block has indefinite height — percentage heights on
    /// direct children then collapse to <c>auto</c> per spec.
    /// <para><paramref name="reuseHeight"/>, set by a flex/grid item's auto
    /// cross-size (height) measurement, lets a clean child subtree replay its
    /// cached measured height instead of re-laying. Only valid when the caller
    /// consumes the returned height and never reads descendant fragments — the
    /// width (min/max-content) measure passes leave it false.</para>
    /// </summary>
    internal double LayoutChildren(Box.Box parent, double containerWidth, double? containerHeight, bool measure, bool reuseHeight = false)
    {
        var cursorY = 0d;
        var prevBottomMargin = 0d;
        var first = true;
        // Every LayoutChildren call gets its own float context — every block
        // is treated as a new BFC for floats, which is a simplification of
        // CSS 2.1 §9.4.1 (BFCs are actually only established by floats,
        // overflow!=visible, position:absolute|fixed, display:inline-block,
        // and the root). The simplification is fine while inline floats and
        // float-escape-across-non-BFC layouts aren't exercised.
        var floats = new FloatContext(containerWidth);
        foreach (var child in parent.Children)
        {
            // Host abort (Stop button, navigation supersede). One check per
            // child keeps the cancellation latency bounded — a deeply nested
            // subtree pays at every level — without polluting the per-property
            // inner work below.
            _abort.ThrowIfCancellationRequested();

            // CSS 2.1 §9.3.1: `position: absolute` and `position: fixed`
            // remove the element from normal flow. We skip them here so the
            // cursor doesn't advance — they're placed in a second pass by
            // PositionLayout. The child's Frame is left at default (zero)
            // until that pass writes it.
            // Record the hypothetical static position (§10.3.7/§10.6.4) the
            // box would have had in this flow — the current stack cursor at
            // the parent's left content edge — so the positioning pass can
            // fall back to it when both insets of an axis are auto, instead
            // of snapping to the containing block's origin. Margin collapse
            // with the would-be previous sibling is approximated away.
            if (IsOutOfFlow(child.Style))
            {
                child.StaticX = 0;
                child.StaticY = cursorY;
                continue;
            }

            var floatSide = GetFloatSide(child.Style);
            if (floatSide is not null)
            {
                LayoutFloat(child, containerWidth, containerHeight, floats, floatSide, cursorY);
                continue;
            }

            // CSS 2.1 §9.5.2 — clear on a non-float pushes its top edge past
            // every active float of the requested side(s) before normal flow
            // resumes. Apply before margin-collapse so the cleared block
            // starts in the right band.
            var clearSide = GetClearSide(child.Style);
            if (clearSide is not null)
            {
                var clearY = floats.ClearY(clearSide);
                if (clearY > cursorY)
                {
                    cursorY = clearY;
                    prevBottomMargin = 0;
                }
            }

            LayoutBlock(child, containerWidth, containerHeight, ref cursorY, ref prevBottomMargin, ref first, measure, reuseHeight);
        }
        // Grow the consumed height to enclose any floats that stick past the
        // last in-flow block (§10.6.7). Without this, a float-only container
        // would report zero height even though its floats are visible.
        var floatBottom = floats.MaxFloatBottom();
        if (floatBottom > cursorY)
        {
            cursorY = floatBottom;
        }

        return cursorY;
    }

    private static string? GetFloatSide(ComputedStyle? style)
    {
        if (style is null)
        {
            return null;
        }

        if (style.Get(PropertyId.Float) is not CssKeyword k)
        {
            return null;
        }

        return k.Name.Equals("left", StringComparison.OrdinalIgnoreCase) ? "left"
            : k.Name.Equals("right", StringComparison.OrdinalIgnoreCase) ? "right"
            : null;
    }

    private static string? GetClearSide(ComputedStyle? style)
    {
        if (style is null)
        {
            return null;
        }

        if (style.Get(PropertyId.Clear) is not CssKeyword k)
        {
            return null;
        }

        var n = k.Name;
        return n.Equals("left", StringComparison.OrdinalIgnoreCase) ? "left"
            : n.Equals("right", StringComparison.OrdinalIgnoreCase) ? "right"
            : n.Equals("both", StringComparison.OrdinalIgnoreCase) ? "both"
            : null;
    }

    private void LayoutFloat(Box.Box child, double containerWidth, double? containerHeight, FloatContext floats, string side, double startY)
    {
        // Floats use the regular block sizing path (specified width, intrinsic
        // height from laid-out children) but are placed by the float context
        // instead of advancing the cursor. They establish their own BFC so the
        // recursive LayoutChildren call gets its own FloatContext.
        if (child.Kind == BoxKind.AnonymousBlock)
        {
            return;
        }

        ResolveBoxModel(child, containerWidth);
        var explicitHeight = ResolveHeight(child.Style, PropertyId.Height, containerHeight, _viewport, allowAuto: true);
        var width = ContentWidth(child, containerWidth, TransferredWidth(child, explicitHeight));

        double childContentHeight;
        if (IsFlexContainer(child.Style))
        {
            var flex = new Starling.Layout.Flex.FlexLayout(this, _viewport);
            childContentHeight = flex.Layout(child, width, explicitHeight);
        }
        else if (IsGridContainer(child.Style))
        {
            var grid = new Starling.Layout.Grid.GridLayout(this, _viewport);
            childContentHeight = grid.Layout(child, width, explicitHeight);
        }
        else
        {
            childContentHeight = LayoutChildren(child, width, explicitHeight, measure: false);
        }

        var resolvedHeight = explicitHeight
            ?? TransferredHeight(child, width, containerHeight, childContentHeight)
            ?? childContentHeight;
        var fullHeight = resolvedHeight + child.Padding.Vertical + child.Border.Vertical;
        var fullWidth = width + child.Padding.Horizontal + child.Border.Horizontal;
        var outerWidth = fullWidth + child.Margin.Horizontal;
        var outerHeight = fullHeight + child.Margin.Vertical;

        var placement = side == "left"
            ? floats.PlaceLeft(startY, outerWidth, outerHeight)
            : floats.PlaceRight(startY, outerWidth, outerHeight);

        child.Frame = new Rect(
            placement.X + child.Margin.Left,
            placement.Y + child.Margin.Top,
            fullWidth,
            fullHeight);

        // Floats have no reuse key — this re-laid the box wholesale, so the
        // scroll-extent caches up the chain are stale (and a float scroller
        // owes a re-measure). Children laid above go through the stamped
        // seams and report themselves.
        NoteRelaid(child);
    }

    /// <summary>
    /// True for <c>position: absolute</c> / <c>fixed</c> boxes, which are
    /// removed from normal flow (CSS 2.1 §9.3.1) and placed in a later pass by
    /// <see cref="Position.PositionLayout"/>. Flex (CSS Flexbox §4) and Grid
    /// (CSS Grid §9) likewise exclude such children from item layout, so this
    /// is shared with <see cref="Flex.FlexLayout"/> / <see cref="Grid.GridLayout"/>.
    /// </summary>
    internal static bool IsOutOfFlow(ComputedStyle? style)
    {
        if (style is null)
        {
            return false;
        }

        if (style.Get(PropertyId.Position) is not CssKeyword k)
        {
            return false;
        }

        var name = k.Name;
        return string.Equals(name, "absolute", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "fixed", StringComparison.OrdinalIgnoreCase);
    }

    private void LayoutBlock(Box.Box child, double containerWidth, double? containerHeight, ref double cursorY, ref double prevBottomMargin, ref bool first, bool measure = false, bool reuseHeight = false)
    {
        // Incremental reuse: a clean subtree laid out under the same constraint
        // space keeps its size and its descendants' (parent-relative) geometry,
        // so we only re-place its root in the parent's block flow. The placement
        // math below mirrors the full path exactly, so the dual-run harness sees
        // no divergence. Measurement mode (inline-block shrink-to-fit) skips
        // reuse — its alignment behaviour differs from the normal pass.
        var cs = new ConstraintSpace(containerWidth, containerHeight, _viewport.Width, _viewport.Height);

        // Measurement-mode reuse: an intrinsic-sizing pass over a clean subtree
        // (e.g. an auto-height flex item measuring its content height) only
        // consumes the subtree's height — it never reads the descendants' text
        // fragments. So a clean box measured before under the same measurement
        // constraint replays its cached height without re-shaping a single line.
        // This is what stops one deep DOM change inside a flex root from
        // re-measuring the whole page every frame: the dirty item re-measures,
        // its clean siblings/descendants replay. Width (min/max-content) measures
        // do read fragments, but those short-circuit at the flex layer before
        // reaching here, so this height-only replay is sound.
        if (_incremental && measure && reuseHeight && !child.SubtreeDirty
            && child.LaidConstraintMeasure == cs && child.Kind != BoxKind.AnonymousBlock)
        {
            var mTop = child.Margin.Top;
            var mCollapse = first ? mTop : Math.Max(0, Math.Max(mTop, prevBottomMargin) - prevBottomMargin);
            cursorY += mCollapse;
            first = false;
            child.Frame = new Rect(child.Margin.Left, cursorY, child.Frame.Width, child.MeasuredHeight);
            child.RelShiftValid = false; // fresh natural frame — see NoteRelaid
            cursorY += child.MeasuredHeight + child.Margin.Bottom;
            prevBottomMargin = child.Margin.Bottom;
            return;
        }

        if (_incremental && !measure && !child.SubtreeDirty && child.LaidConstraint == cs)
        {
            if (child.Kind == BoxKind.AnonymousBlock)
            {
                child.Frame = new Rect(0, cursorY, containerWidth, child.Frame.Height);
                cursorY = child.Frame.Bottom;
                prevBottomMargin = 0;
                first = false;
                return;
            }

            var reuseTop = child.Margin.Top;
            var reuseCollapse = first ? reuseTop : Math.Max(0, Math.Max(reuseTop, prevBottomMargin) - prevBottomMargin);
            cursorY += reuseCollapse;
            first = false;
            child.Frame = new Rect(child.Margin.Left, cursorY, child.Frame.Width, child.Frame.Height);
            // This is a NATURAL stack position: a relative/sticky child reused
            // in place must re-derive its shift from it, even when it lands
            // exactly on the previous pass's shifted frame (see NoteRelaid).
            child.RelShiftValid = false;
            cursorY += child.Frame.Height + child.Margin.Bottom;
            prevBottomMargin = child.Margin.Bottom;
            return;
        }

        if (child.Kind == BoxKind.AnonymousBlock)
        {
            // Anonymous blocks take the initial value for box-model properties
            // (margin/padding/border = 0); they only inherit text-formatting for
            // the inline pass.
            child.Margin = Edges.Zero;
            child.Padding = Edges.Zero;
            child.Border = Edges.Zero;

            var inlineHeight = _inline.Layout(child, containerWidth, measure);
            child.Frame = new Rect(0, cursorY, containerWidth, inlineHeight);
            cursorY = child.Frame.Bottom;
            prevBottomMargin = 0;
            first = false;
            Stamp(child, cs, measure);
            return;
        }

        ResolveBoxModel(child, containerWidth);

        // Margin-collapse against previous sibling.
        var topMargin = child.Margin.Top;
        var collapseTop = first ? topMargin : Math.Max(0, Math.Max(topMargin, prevBottomMargin) - prevBottomMargin);
        cursorY += collapseTop;
        first = false;

        // Height first: a definite height plus a preferred aspect ratio can
        // transfer into an auto width (css-sizing-4 §5), so the width
        // resolution below needs it.
        var explicitHeight = ResolveHeight(child.Style, PropertyId.Height, containerHeight, _viewport, allowAuto: true);
        var width = ContentWidth(child, containerWidth, TransferredWidth(child, explicitHeight));

        // CSS 2.1 §10.3.3 — resolve `auto` horizontal margins for non-replaced
        // block-level elements in normal flow. Only when `width` is not `auto`
        // do auto margins absorb slack; otherwise they resolve to 0 (already
        // handled by ResolveBoxModel).
        ResolveAutoHorizontalMargins(child, containerWidth, width);

        // Flex container: hand off to the flex formatting context. Flex sizes
        // its items inside the container's content box; the result replaces
        // what block stacking would have produced.
        double childContentHeight;
        if (IsFlexContainer(child.Style))
        {
            var flex = new Starling.Layout.Flex.FlexLayout(this, _viewport);
            childContentHeight = flex.Layout(child, width, explicitHeight);
        }
        else if (IsGridContainer(child.Style))
        {
            var grid = new Starling.Layout.Grid.GridLayout(this, _viewport);
            childContentHeight = grid.Layout(child, width, explicitHeight);
        }
        else
        {
            childContentHeight = LayoutChildren(child, width, explicitHeight, measure, reuseHeight);
        }

        var resolvedHeight = explicitHeight
            ?? TransferredHeight(child, width, containerHeight, childContentHeight)
            ?? childContentHeight;
        var fullHeight = resolvedHeight + child.Padding.Vertical + child.Border.Vertical;

        child.Frame = new Rect(
            child.Margin.Left,
            cursorY,
            width + child.Padding.Horizontal + child.Border.Horizontal,
            fullHeight);

        cursorY += fullHeight + child.Margin.Bottom;
        prevBottomMargin = child.Margin.Bottom;
        Stamp(child, cs, measure);
    }

    /// <summary>
    /// Scroll-measure bookkeeping for a box this pass actually (re)laid out:
    /// its cached subtree extent (and every ancestor's) no longer describes
    /// the tree, and if it is a scroll container it owes a scoped re-measure.
    /// Queuing on a re-lay is deliberately conservative — re-recording
    /// unchanged geometry is a harmless overwrite, while missing a changed
    /// scroller would serve stale scroll ranges. No-op without a sink.
    /// </summary>
    internal void NoteRelaid(Box.Box box)
    {
        // A seam that (re)lays a box writes a fresh NATURAL frame, so the
        // relative/sticky idempotency bookkeeping must die with it: keeping
        // it would let a fresh natural frame that happens to equal the
        // previous shifted frame re-base on a stale origin. Unconditional —
        // the scroll sink gate below only governs measurement bookkeeping.
        box.RelShiftValid = false;
        var sink = RelaidScrollerSink;
        if (sink is null)
        {
            return;
        }

        Scroll.ScrollOverflowMeasurer.InvalidateExtentsToRoot(box);
        if (box.Element is not null
            && !box.ScrollMeasureQueued
            && (Scroll.ScrollOverflowMeasurer.Classify(box) & Scroll.ScrollBoxFlags.ScrollContainer) != 0)
        {
            box.ScrollMeasureQueued = true;
            sink.Add(box);
        }
    }

    /// <summary>Scroll-measure bookkeeping for a frame moved in place by the
    /// position pass (relative/sticky shift): the box's own cached extent is
    /// frame-origin-relative and stays exact, but every ancestor's union now
    /// includes a moved rect. No-op without a sink.</summary>
    internal void NoteFrameMoved(Box.Box box)
    {
        if (RelaidScrollerSink is null)
        {
            return;
        }

        Scroll.ScrollOverflowMeasurer.InvalidateExtentsToRoot(box);
    }

    /// <summary>Record the constraint space a freshly laid-out box was computed
    /// under so a later frame can reuse it. The normal pass also clears the dirty
    /// mark; the measurement pass records only its own (height-only) reuse key and
    /// leaves the dirty mark for the normal pass to clear. No-op off the
    /// incremental path.</summary>
    private void Stamp(Box.Box box, ConstraintSpace cs, bool measure)
    {
        if (!_incremental)
        {
            return;
        }

        NoteRelaid(box);
        if (measure)
        {
            box.LaidConstraintMeasure = cs;
            box.MeasuredHeight = box.Frame.Height;
            return;
        }
        box.LaidConstraint = cs;
        box.SubtreeDirty = false;
    }

    /// <summary>
    /// CSS 2.1 §10.3.3 — for a block-level non-replaced element in normal flow,
    /// `auto` values for `margin-left` / `margin-right` absorb the slack between
    /// the used width and the containing block's content width:
    /// <list type="bullet">
    ///   <item>Both auto → center the box (each margin gets half the slack; if
    ///   negative, both go to 0).</item>
    ///   <item>One auto → that one absorbs all slack.</item>
    ///   <item>Neither auto → leave margins as computed (over-constrained;
    ///   spec says ignore margin-right in LTR but we keep the user's values).</item>
    /// </list>
    /// This is only meaningful when `width` is not `auto`; if width filled the
    /// available space, slack is zero (or negative) and there is nothing to
    /// distribute.
    /// </summary>
    private static void ResolveAutoHorizontalMargins(Box.Box child, double containerWidth, double usedWidth)
    {
        var leftAuto = IsAutoMargin(child.Style, PropertyId.MarginLeft);
        var rightAuto = IsAutoMargin(child.Style, PropertyId.MarginRight);
        if (!leftAuto && !rightAuto)
        {
            return;
        }

        // CSS 2.1 §10.3.3 says auto margins absorb slack when width is not
        // auto. §10.4 extends this to the max-width / min-width clamp: after
        // the tentative width is constrained, the leftover horizontal space
        // is redistributed to auto margins, even though the *computed* width
        // value is `auto`. Without this, `body { max-width: 35em; margin: auto }`
        // (the words.html / Justin Jackson layout, MDN article body, etc.)
        // pins to the left instead of centering. Compute the slack from the
        // used width — if it's zero (true auto width, no clamp) the autos
        // collapse to 0 the same way the §10.3.3 path produces.
        var outerWidth = usedWidth + child.Padding.Horizontal + child.Border.Horizontal;
        var slack = containerWidth - outerWidth - child.Margin.Left - child.Margin.Right;
        if (slack <= 0)
        {
            return;
        }

        double newLeft = child.Margin.Left;
        double newRight = child.Margin.Right;
        if (leftAuto && rightAuto)
        {
            if (slack < 0)
            {
                // Negative slack: both autos go to 0 (left edge).
                newLeft = 0;
                newRight = 0;
            }
            else
            {
                newLeft = slack / 2d;
                newRight = slack - newLeft;
            }
        }
        else if (leftAuto)
        {
            newLeft = Math.Max(0, slack);
        }
        else // rightAuto
        {
            newRight = Math.Max(0, slack);
        }

        child.Margin = new Edges(child.Margin.Top, newRight, child.Margin.Bottom, newLeft);
    }

    private static bool IsAutoMargin(ComputedStyle? style, PropertyId property)
        => style is not null && style.Get(property) is CssKeyword k && k.Name == "auto";

    internal static bool IsFlexContainer(ComputedStyle? style)
    {
        if (style is null)
        {
            return false;
        }

        if (style.Get(PropertyId.Display) is not CssKeyword k)
        {
            return false;
        }

        return k.Name.Equals("flex", StringComparison.OrdinalIgnoreCase)
            || k.Name.Equals("inline-flex", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsGridContainer(ComputedStyle? style)
    {
        if (style is null)
        {
            return false;
        }

        if (style.Get(PropertyId.Display) is not CssKeyword k)
        {
            return false;
        }

        return k.Name.Equals("grid", StringComparison.OrdinalIgnoreCase)
            || k.Name.Equals("inline-grid", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasExplicitWidth(ComputedStyle? style)
    {
        if (style is null)
        {
            return false;
        }

        var value = style.Get(PropertyId.Width);
        return value is not CssKeyword k || k.Name != "auto";
    }

    private void ResolveBoxModel(Box.Box box, double containerWidth)
    {
        // CSS 2.1 §8.3/§8.4: percentage margins and padding on ALL FOUR sides
        // resolve against the containing block's WIDTH — including the
        // vertical ones. (That's what makes the `padding-bottom: %`
        // aspect-ratio trick work: x.com's profile banner is
        // `padding-bottom: 33.33%` of the column width, not of the viewport
        // height.)
        box.Margin = new Edges(
            ResolveLength(box.Style, PropertyId.MarginTop, containerWidth, _viewport) ?? 0,
            ResolveLength(box.Style, PropertyId.MarginRight, containerWidth, _viewport) ?? 0,
            ResolveLength(box.Style, PropertyId.MarginBottom, containerWidth, _viewport) ?? 0,
            ResolveLength(box.Style, PropertyId.MarginLeft, containerWidth, _viewport) ?? 0);

        box.Padding = new Edges(
            ResolveLength(box.Style, PropertyId.PaddingTop, containerWidth, _viewport) ?? 0,
            ResolveLength(box.Style, PropertyId.PaddingRight, containerWidth, _viewport) ?? 0,
            ResolveLength(box.Style, PropertyId.PaddingBottom, containerWidth, _viewport) ?? 0,
            ResolveLength(box.Style, PropertyId.PaddingLeft, containerWidth, _viewport) ?? 0);

        box.Border = new Edges(
            ResolveBorderWidth(box.Style, PropertyId.BorderTopWidth, PropertyId.BorderTopStyle, _viewport),
            ResolveBorderWidth(box.Style, PropertyId.BorderRightWidth, PropertyId.BorderRightStyle, _viewport),
            ResolveBorderWidth(box.Style, PropertyId.BorderBottomWidth, PropertyId.BorderBottomStyle, _viewport),
            ResolveBorderWidth(box.Style, PropertyId.BorderLeftWidth, PropertyId.BorderLeftStyle, _viewport));
    }

    private double ContentWidth(Box.Box box, double containerWidth, double? transferredWidth = null)
    {
        // CSS 2.1 §10.4 + css-sizing-3 §5: tentative width is `width` when it
        // resolves — a length/percentage or an intrinsic sizing keyword — else
        // the aspect-ratio transferred size (css-sizing-4 §5), else the
        // available space within the parent. The tentative width is then
        // clamped by `max-width` (upper bound) and `min-width` (lower bound),
        // which accept the same intrinsic keywords on the inline axis.
        // Honouring max-width is what makes the classic "max-width: 35em"
        // narrow-column layout work (justinjackson.ca/words.html, MDN, etc).
        var available = Math.Max(0, containerWidth - box.Margin.Horizontal - box.Border.Horizontal - box.Padding.Horizontal);

        double tentative;
        if (TryResolveIntrinsicWidth(box.Style?.Get(PropertyId.Width), box, available, containerWidth, out var intrinsic))
        {
            tentative = intrinsic;
        }
        else if (ResolveLength(box.Style, PropertyId.Width, containerWidth, _viewport, allowAuto: true) is { } w)
        {
            tentative = w;
        }
        else if (transferredWidth is { } tw)
        {
            tentative = tw;
        }
        else
        {
            tentative = available;
        }

        // max-width: `none` (the initial value) is a no-op; any concrete length,
        // percentage, or intrinsic keyword clamps the tentative width down.
        double? maxWidth;
        if (TryResolveIntrinsicWidth(box.Style?.Get(PropertyId.MaxWidth), box, available, containerWidth, out var maxIntrinsic))
        {
            maxWidth = maxIntrinsic;
        }
        else
        {
            maxWidth = ResolveMaxLength(box.Style, PropertyId.MaxWidth, containerWidth, _viewport);
        }

        if (maxWidth is { } mx && tentative > mx)
        {
            tentative = mx;
        }

        // min-width: initial value `0` is a no-op; a concrete length/percentage
        // (or intrinsic keyword) expands the box back up when it's narrower
        // than the floor.
        double minWidth;
        if (TryResolveIntrinsicWidth(box.Style?.Get(PropertyId.MinWidth), box, available, containerWidth, out var minIntrinsic))
        {
            minWidth = minIntrinsic;
        }
        else
        {
            minWidth = ResolveLength(box.Style, PropertyId.MinWidth, containerWidth, _viewport) ?? 0;
        }

        if (tentative < minWidth)
        {
            tentative = minWidth;
        }

        return Math.Max(0, tentative);
    }

    /// <summary>
    /// css-sizing-3 §4-5 — resolve an intrinsic sizing keyword on the inline
    /// axis. <c>min-content</c> is the narrowest wrap (probe at zero width),
    /// <c>max-content</c> the no-wrap width (probe at a huge width),
    /// <c>fit-content</c> clamps the stretch size between the two, and
    /// <c>fit-content(&lt;length-percentage&gt;)</c> substitutes its argument
    /// for the stretch size. Probes are cached per box per pass. Returns false
    /// for every other value. (The block axis never reaches here — in a
    /// horizontal writing mode the keywords behave as <c>auto</c> there, see
    /// <see cref="ResolveHeight"/>.)
    /// </summary>
    private bool TryResolveIntrinsicWidth(CssValue? value, Box.Box box, double available, double containerWidth, out double resolved)
    {
        switch (value)
        {
            case CssKeyword { Name: "min-content" }:
                resolved = MinContentWidth(box);
                return true;
            case CssKeyword { Name: "max-content" }:
                resolved = MaxContentWidth(box, containerWidth);
                return true;
            case CssKeyword { Name: "fit-content" }:
                resolved = FitContentWidth(box, available, containerWidth);
                return true;
            case CssFunctionValue { Name: "fit-content" } f when f.Arguments.Count == 1:
                {
                    // fit-content(size) = min(max-content, max(min-content, size)).
                    double? size = f.Arguments[0] switch
                    {
                        CssLength len => ToPx(len, _viewport),
                        CssPercentage pct => containerWidth * pct.Value / 100d,
                        _ => null,
                    };
                    if (size is { } s)
                    {
                        resolved = FitContentWidth(box, s, containerWidth);
                        return true;
                    }
                    resolved = 0;
                    return false;
                }
            default:
                resolved = 0;
                return false;
        }
    }

    /// <summary>
    /// Min-content inline size (css-sizing-3 §4.2): probe at zero available
    /// width so every soft-wrap opportunity breaks; the widest unbreakable run
    /// (longest word, widest replaced box) remains. Cached per box per pass.
    /// </summary>
    internal double MinContentWidth(Box.Box box)
    {
        if (!box.SubtreeDirty && box.CachedMinContentWidth is { } cached)
        {
            return cached;
        }

        LayoutItem(box, 0d, null, measure: true);
        var min = IntrinsicSizes.ContentExtent(box);
        box.CachedMinContentWidth = min;
        return min;
    }

    /// <summary>
    /// Max-content (no-wrap) inline size (css-sizing-3 §4.1): probe at a huge
    /// width so nothing soft-wraps and read the used content extent. Cached
    /// per box per pass.
    /// </summary>
    internal double MaxContentWidth(Box.Box box, double containerWidth)
    {
        if (box.Kind != BoxKind.AnonymousBlock && IsFlexContainer(box.Style))
        {
            // A flex container's max-content size is structural (Flexbox §9.9):
            // probing it at a huge width would let any flex-grow item balloon
            // to the probe width. FlexLayout owns that computation.
            var flex = new Starling.Layout.Flex.FlexLayout(this, _viewport);
            return flex.NaturalWidth(box, containerWidth);
        }
        if (!box.SubtreeDirty && box.CachedMaxContentWidth is { } cached)
        {
            return cached;
        }

        LayoutItem(box, IntrinsicSizes.ProbeWidth, null, measure: true);
        var max = IntrinsicSizes.ContentExtent(box);
        box.CachedMaxContentWidth = max;
        return max;
    }

    /// <summary>
    /// css-sizing-3 §5.3 fit-content: clamp the stretch size between
    /// min-content and max-content — shrink-to-fit with the caller's stretch
    /// size in place of the available space.
    /// </summary>
    private double FitContentWidth(Box.Box box, double stretch, double containerWidth)
    {
        var min = MinContentWidth(box);
        var max = MaxContentWidth(box, containerWidth);
        return Math.Min(max, Math.Max(min, stretch));
    }

    /// <summary>
    /// css-sizing-4 §5 transferred inline size: when `width` is auto, `height`
    /// is definite, and the box has a preferred aspect ratio, the tentative
    /// width is height × ratio (min/max-width then clamp it as usual).
    /// </summary>
    private static double? TransferredWidth(Box.Box child, double? explicitHeight)
    {
        if (explicitHeight is not { } h || HasExplicitWidth(child.Style))
        {
            return null;
        }

        if (!IntrinsicSizes.TryGetPreferredRatio(child.Style, out var ratio))
        {
            return null;
        }

        return h * ratio;
    }

    /// <summary>
    /// css-sizing-4 §5 transferred block size: when `height` is auto and the
    /// box has a preferred aspect ratio, the used height is width / ratio,
    /// clamped by the derived axis's own min/max (§5.2.1). With min-height at
    /// its initial value the automatic content-based minimum (§5.2.2) still
    /// lets overflowing in-flow content grow the box past the transferred size
    /// (capped by max-height); an explicit positive min-height replaces that
    /// automatic minimum. Returns null when the box has no preferred ratio.
    /// </summary>
    private double? TransferredHeight(Box.Box child, double usedWidth, double? containerHeight, double contentHeight)
    {
        if (!IntrinsicSizes.TryGetPreferredRatio(child.Style, out var ratio))
        {
            return null;
        }

        var transferred = usedWidth / ratio;

        double? maxH = child.Style?.Get(PropertyId.MaxHeight) is CssKeyword { Name: "none" }
            ? null
            : ResolveHeight(child.Style, PropertyId.MaxHeight, containerHeight, _viewport, allowAuto: true);
        var minH = ResolveHeight(child.Style, PropertyId.MinHeight, containerHeight, _viewport, allowAuto: true);

        if (maxH is { } mx && transferred > mx)
        {
            transferred = mx;
        }

        if (minH is { } mn && transferred < mn)
        {
            transferred = mn;
        }

        if (minH is not { } floor || floor <= 0)
        {
            var contentFloor = contentHeight;
            if (maxH is { } cap && contentFloor > cap)
            {
                contentFloor = cap;
            }

            if (transferred < contentFloor)
            {
                transferred = contentFloor;
            }
        }
        return transferred;
    }

    // max-* properties accept the keyword `none` (the initial value) to mean
    // "no upper bound". ResolveLength maps `none` to 0, which would collapse
    // the box; intercept it here so callers can skip the clamp.
    internal static double? ResolveMaxLength(ComputedStyle? style, PropertyId property, double percentageBasis, Size? viewport)
    {
        if (style is null)
        {
            return null;
        }

        var value = style.Get(property);
        if (value is CssKeyword k && k.Name == "none")
        {
            return null;
        }

        return ResolveLength(style, property, percentageBasis, viewport);
    }

    internal static double ResolveBorderWidth(ComputedStyle? style, PropertyId widthId, PropertyId styleId, Size? viewport = null)
    {
        if (style is null)
        {
            return 0;
        }

        var styleValue = style.Get(styleId);
        if (styleValue is CssKeyword k && k.Name == "none")
        {
            return 0;
        }

        return style.Get(widthId) is CssLength len ? ToPx(len, viewport) : 0;
    }

    /// <summary>
    /// Height-axis variant of <see cref="ResolveLength(ComputedStyle?, PropertyId, double, Size?, bool)"/>.
    /// CSS 2.1 §10.5: if the containing block's height is not specified
    /// explicitly (the basis here is <c>null</c>), a percentage height
    /// resolves as <c>auto</c> — returned as <c>null</c> when
    /// <paramref name="allowAuto"/> is true, otherwise 0.
    /// </summary>
    internal static double? ResolveHeight(ComputedStyle? style, PropertyId property, double? containerHeight, Size? viewport, bool allowAuto = false)
    {
        if (style is null)
        {
            return null;
        }

        var value = style.Get(property);
        return value switch
        {
            CssLength len => ToPx(len, viewport),
            CssPercentage when !containerHeight.HasValue => allowAuto ? null : 0,
            CssPercentage pct => containerHeight!.Value * pct.Value / 100d,
            CssNumber n => n.Value,
            // A calc() that mixes a percentage with a length (e.g.
            // `calc(100% - 2rem)`) survives parsing as a symbolic CssCalc; resolve
            // it against the containing block's height. With no explicit height
            // basis a contained percentage stays symbolic and ResolveCalcPx
            // returns null, which behaves as auto (CSS 2.1 §10.5).
            CssCalc calc => ResolveCalcPx(calc, containerHeight, viewport) ?? (allowAuto ? null : 0),
            CssKeyword k when k.Name == "auto" => allowAuto ? null : 0,
            CssKeyword k when k.Name == "none" => 0,
            // css-sizing-3 §5 — the intrinsic sizing keywords refer to the
            // inline axis; on the block axis in a horizontal writing mode
            // (the only mode this engine lays out) they behave as `auto`.
            CssKeyword k when k.Name is "min-content" or "max-content" or "fit-content" => allowAuto ? null : 0,
            _ => null,
        };
    }

    internal static double? ResolveLength(ComputedStyle? style, PropertyId property, double percentageBasis, bool allowAuto = false)
        => ResolveLength(style, property, percentageBasis, viewport: null, allowAuto);

    internal static double? ResolveLength(ComputedStyle? style, PropertyId property, double percentageBasis, Size? viewport, bool allowAuto = false)
    {
        if (style is null)
        {
            return null;
        }

        var value = style.Get(property);
        return value switch
        {
            CssLength len => ToPx(len, viewport),
            CssPercentage pct => percentageBasis * pct.Value / 100d,
            CssNumber n => n.Value,
            // A calc() that mixes a percentage with a length (e.g.
            // `calc(100% - 7rem)`) cannot fold at parse time, so it reaches layout
            // as a symbolic CssCalc. Resolve it here against the containing block.
            CssCalc calc => ResolveCalcPx(calc, percentageBasis, viewport),
            CssKeyword k when k.Name == "auto" => allowAuto ? null : 0,
            CssKeyword k when k.Name == "none" => 0,
            _ => null,
        };
    }

    /// <summary>
    /// Resolve a symbolic <see cref="CssCalc"/> length to pixels against a
    /// containing-block <paramref name="percentageBasis"/>. Returns null when the
    /// result can't be reduced to a length (e.g. a contained percentage with no
    /// basis), letting callers fall back to auto. The resolution context mirrors
    /// <see cref="ToPx(CssLength, Size?)"/>: font-relative units use a 16px base
    /// and viewport units use <paramref name="viewport"/> (100px when unknown).
    /// </summary>
    internal static double? ResolveCalcPx(CssCalc calc, double? percentageBasis, Size? viewport)
    {
        var vw = viewport?.Width ?? 100d;
        var vh = viewport?.Height ?? 100d;
        var ctx = CssResolutionContext.Default with
        {
            ViewportWidthPx = vw,
            ViewportHeightPx = vh,
            SmallViewportWidthPx = vw,
            SmallViewportHeightPx = vh,
            LargeViewportWidthPx = vw,
            LargeViewportHeightPx = vh,
            DynamicViewportWidthPx = vw,
            DynamicViewportHeightPx = vh,
            ContainerWidthPx = vw,
            ContainerHeightPx = vh,
            PercentageBasisPx = percentageBasis ?? double.NaN,
        };

        return CssCalcResolver.Resolve(calc, ctx) switch
        {
            CssLength len => ToPx(len, viewport),
            CssPercentage pct when percentageBasis is { } basis => basis * pct.Value / 100d,
            _ => null,
        };
    }

    internal static double ToPx(CssLength length) => ToPx(length, viewport: null);

    internal static double ToPx(CssLength length, Size? viewport) => length.Unit switch
    {
        CssLengthUnit.Px => length.Value,
        CssLengthUnit.Pt => length.Value * 4d / 3d,
        CssLengthUnit.Pc => length.Value * 16d,
        CssLengthUnit.In => length.Value * 96d,
        CssLengthUnit.Cm => length.Value * 96d / 2.54d,
        CssLengthUnit.Mm => length.Value * 96d / 25.4d,
        CssLengthUnit.Q => length.Value * 96d / 101.6d,
        CssLengthUnit.Em => length.Value * 16d,
        CssLengthUnit.Rem => length.Value * 16d,
        CssLengthUnit.Vh => length.Value * (viewport?.Height ?? 100d) / 100d,
        CssLengthUnit.Vw => length.Value * (viewport?.Width ?? 100d) / 100d,
        _ => length.Value,
    };
}
