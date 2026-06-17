using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Layout.Block;
using Starling.Layout.Box;

namespace Starling.Layout.Flex;

/// <summary>
/// Flex layout (CSS Flex Box Layout Module Level 1, simplified). Handles
/// <c>flex-direction</c>, <c>flex-wrap</c> (multi-line rows, and columns with
/// a definite height), <c>order</c>, <c>justify-content</c>,
/// <c>align-items</c>/<c>align-self</c> (including first-baseline alignment
/// in row containers), <c>align-content</c> (multi-line cross distribution +
/// line stretching), the <c>flex</c> shorthand + individual
/// <c>flex-grow</c>/<c>shrink</c>/<c>basis</c>,
/// <c>min-width</c>/<c>min-height</c> (and the automatic content minimum),
/// <c>max-width</c>/<c>max-height</c> clamping with violation freezing +
/// free-space redistribution (CSS Flexbox §9.7), and
/// <c>gap</c>/<c>row-gap</c>/<c>column-gap</c>.
/// Out of scope (deferred):
/// <list type="bullet">
///   <item><c>wrap-reverse</c> cross-axis line reversal — wraps like <c>wrap</c>.</item>
///   <item>Baseline alignment in column containers — falls back to <c>flex-start</c>.</item>
///   <item>A line's cross size doesn't grow for baseline-shifted items
///   (max ascent + max descent), so a shifted item can overflow its line.</item>
/// </list>
/// </summary>
/// <remarks>
/// Coordinate convention matches <see cref="BlockLayout"/>: each child's
/// <see cref="Box.Box.Frame"/> is in the flex container's content-box
/// coordinate space (so x = 0 is the left edge of padding, not the parent's
/// content edge). The container's own frame is set by the calling block
/// layout pass.
/// </remarks>
internal sealed class FlexLayout
{
    private readonly BlockLayout _block;
    private readonly Size _viewport;

    public FlexLayout(BlockLayout block, Size viewport)
    {
        _block = block;
        _viewport = viewport;
    }

    /// <summary>
    /// Lay out <paramref name="container"/>'s children as flex items inside a
    /// content box of <paramref name="containerWidth"/> wide. Returns the
    /// consumed content height (cross size for row, main size for column) so
    /// the caller can size the container's box.
    /// </summary>
    /// <param name="container">The flex container box. Its own
    /// <c>Frame</c>/<c>Margin</c>/<c>Padding</c>/<c>Border</c> are not
    /// touched — the caller composes them.</param>
    /// <param name="containerWidth">Content-box width of the container.</param>
    /// <param name="explicitHeight">Explicit content-box height of the
    /// container, or null when <c>height: auto</c>. Used as the main basis
    /// for column direction and as the cross basis for row direction.</param>
    public double Layout(Box.Box container, double containerWidth, double? explicitHeight)
    {
        var props = FlexParser.ParseContainer(
            container.Style,
            mainAxisBasisPx: containerWidth,
            crossAxisBasisPx: explicitHeight ?? _viewport.Height,
            _viewport);

        // Main / cross sizes of the container's content box.
        //
        // The main size is DEFINITE for a row container (its width is given) and
        // for a column container with an explicit height. A column container with
        // `height: auto` has an INDEFINITE main size — it sizes to its content.
        // We must not fall back to the viewport height there: that phantom main
        // size makes `justify-content` (center/end/space-*) distribute hundreds of
        // px of non-existent free space, shoving the items far below the box
        // (angular.dev's `flex-direction: column` nav buttons landing at y≈422 in
        // a 96px box). When indefinite, the main size used for free-space math is
        // the items' own used main extent, so free space is zero.
        var mainIsDefinite = props.IsRow || explicitHeight.HasValue;
        var mainSize = props.IsRow ? containerWidth : (explicitHeight ?? _viewport.Height);
        var crossSize = props.IsRow ? (explicitHeight ?? double.NaN) : containerWidth;

        // A column container's `min-height` raises a definite floor under the
        // main size even when `height` is auto (CSS Sizing 3 §5). Resolve it so
        // justify-content can distribute any real slack between the content and
        // the floor, while still treating a purely content-sized column as
        // indefinite (no free space).
        double? minMainFloor = null;
        if (!props.IsRow && container.Style is not null)
        {
            minMainFloor = BlockLayout.ResolveLength(
                container.Style, PropertyId.MinHeight, _viewport.Height, _viewport);
        }

        // CSS Flexbox §4: an absolutely/fixed-positioned child is NOT a flex
        // item — it takes no part in flex sizing or spacing and is placed later
        // by PositionLayout. Skipping it here keeps a hidden `position: fixed`
        // overlay (e.g. Google's slide-out menu, height: 100vh) from consuming
        // a flex line and shoving the real content off-screen.
        var children = new List<Box.Box>(container.Children.Count);
        foreach (var c in container.Children)
        {
            if (!BlockLayout.IsOutOfFlow(c.Style))
            {
                children.Add(c);
                continue;
            }
            // CSS Flexbox §4.1: the static position of an absolutely
            // positioned flex child is as if it were the sole flex item —
            // approximated as the container's content-box origin
            // (flex-start / flex-start; align/justify offsets are skipped).
            c.StaticX = 0;
            c.StaticY = 0;
        }
        if (children.Count == 0)
        {
            return explicitHeight ?? 0;
        }

        // ---- step 1: resolve each item's flex base size + box model ----
        // MainSize / CrossSize are tracked as *content-box* sizes throughout;
        // the per-axis padding+border (MainPad / CrossPad) is added back only
        // when distributing free space and sizing frames. Keeping content-box
        // internally lets the final block pass over each item subtract its own
        // padding exactly once — without this a padded item (e.g. a button with
        // `padding: 16px 32px`) had its padding subtracted twice and collapsed
        // to a sliver, wrapping its label.
        var items = new Item[children.Count];
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            ResolveBoxModel(child, containerWidth);
            var itemProps = FlexParser.ParseItem(child.Style);
            var basis = ResolveBasis(child, itemProps.Basis, props, containerWidth, explicitHeight);
            var minMain = ResolveMinMainSize(child, props, containerWidth, explicitHeight, basis);
            var maxMain = ResolveMaxMainSize(child, props, containerWidth, explicitHeight);
            items[i] = new Item
            {
                Box = child,
                Props = itemProps,
                Basis = basis,
                // Hypothetical main size = flex base size clamped by the used
                // min/max main sizes (CSS Flexbox §9.7 step 1; min wins over
                // max, CSS 2.1 §10.4). Line breaking and free-space
                // distribution work from this, so an item with `min-width`
                // larger than its content doesn't get over-grown neighbours,
                // and a `max-width` item (x.com's 600px-max primary column in
                // a 920px slot) hands its slack to siblings instead of
                // overflowing the row.
                MainSize = Math.Max(minMain, Math.Min(basis, maxMain)),
                MinMain = minMain,
                MaxMain = maxMain,
                MaxCross = ResolveMaxCrossSize(child, props, containerWidth, explicitHeight),
                MainPad = props.IsRow
                    ? child.Padding.Horizontal + child.Border.Horizontal
                    : child.Padding.Vertical + child.Border.Vertical,
                CrossPad = props.IsRow
                    ? child.Padding.Vertical + child.Border.Vertical
                    : child.Padding.Horizontal + child.Border.Horizontal,
            };
        }

        // ---- step 2: order — lay out (and paint) items in `order`-modified
        // document order (CSS Flexbox §5.4). Stable so equal orders keep
        // document order. We reorder the array itself; nothing downstream relies
        // on the original index.
        StableOrderSort(items);

        // ---- step 3: collect items into flex lines (CSS Flexbox §9.3) ----
        // Wrapping needs a definite main size to break against: rows always
        // have one (the container width); a column wraps only when its height
        // is definite — with `height: auto` the container grows to fit, so
        // nothing ever overflows onto a second line. Lines stack along the
        // cross axis (downward for rows, rightward for columns).
        var lines = BreakIntoLines(items, props, mainSize, mainIsDefinite);

        // ---- step 4: measure each line — main-axis distribution plus each
        // item's hypothetical cross size; the line's natural cross size is
        // its largest item cross extent.
        var lineCross = new double[lines.Count];
        var lineJustifyMain = new double[lines.Count];
        var maxUsedMain = 0d;
        for (var li = 0; li < lines.Count; li++)
        {
            var (start, count) = lines[li];
            var (cross, usedMain, justifyMain) = MeasureLine(
                items, start, count, props, mainSize, mainIsDefinite, minMainFloor,
                containerWidth, crossSize);
            lineCross[li] = cross;
            lineJustifyMain[li] = justifyMain;
            if (usedMain > maxUsedMain)
            {
                maxUsedMain = usedMain;
            }
        }

        // ---- step 5: align-content — size and place the lines in the
        // container's cross space (CSS Flexbox §8.4 / §9.4 step 8). The cross
        // size is definite for a column (its width) and for a row with an
        // explicit height; an indefinite cross has no free space, so lines
        // stack at their natural sizes separated by the cross gap.
        var containerCross = props.IsRow ? crossSize : containerWidth;
        var crossDefinite = !double.IsNaN(containerCross) && containerCross > 0;
        double leadingCross = 0, betweenCross = props.CrossGap;
        if (crossDefinite && lines.Count == 1)
        {
            // §9.4 step 8: a single-line container with a definite cross size
            // uses it as the line's cross size; align-content has no effect
            // on single-line containers.
            lineCross[0] = containerCross;
        }
        else if (crossDefinite)
        {
            var linesTotal = props.CrossGap * (lines.Count - 1);
            for (var li = 0; li < lineCross.Length; li++)
            {
                linesTotal += lineCross[li];
            }

            var free = containerCross - linesTotal;
            if (props.ContentAlign == AlignContent.Stretch && free > 0)
            {
                // §8.4 `stretch`: split the positive free space equally among
                // the lines, growing each line's cross size.
                var grow = free / lines.Count;
                for (var li = 0; li < lineCross.Length; li++)
                {
                    lineCross[li] += grow;
                }

                free = 0;
            }
            (leadingCross, betweenCross) = ResolveCrossAxisSpacing(
                props.ContentAlign, free, lines.Count, props.CrossGap);
        }

        // ---- step 6: position each line's items at the line's final cross
        // size and offset.
        var crossCursor = leadingCross;
        var totalCross = props.CrossGap * Math.Max(0, lines.Count - 1);
        for (var li = 0; li < lines.Count; li++)
        {
            var (start, count) = lines[li];
            PositionLine(items, start, count, props, lineCross[li], crossCursor, lineJustifyMain[li], crossSize);
            crossCursor += lineCross[li] + betweenCross;
            totalCross += lineCross[li];
        }

        // Row: consumed content height is the stacked line cross sizes,
        // raised to the explicit container cross when the lines fit inside
        // it. Column: the tallest line's main extent, raised to a
        // `min-height` floor when one is set so the box never collapses
        // below it.
        if (props.IsRow)
        {
            return crossDefinite ? Math.Max(containerCross, totalCross) : totalCross;
        }

        return Math.Max(maxUsedMain, minMainFloor ?? 0);
    }

    /// <summary>
    /// Partition items into flex lines. A wrapping container with a definite
    /// main size (rows always; columns only with an explicit height) breaks
    /// to a new line when the next item's outer (border-box + gap) main size
    /// would overflow the container's main size; the first item on a line is
    /// always kept even if it alone overflows. Everything else (nowrap, or a
    /// column sized by its content) is a single line. <c>wrap-reverse</c>
    /// wraps like <c>wrap</c> — cross-axis line reversal is deferred.
    /// </summary>
    private static List<(int Start, int Count)> BreakIntoLines(Item[] items, FlexContainerProps props, double mainSize, bool mainIsDefinite)
    {
        var lines = new List<(int, int)>();
        if (!props.IsWrap || !mainIsDefinite)
        {
            lines.Add((0, items.Length));
            return lines;
        }

        const double epsilon = 0.5;
        var i = 0;
        while (i < items.Length)
        {
            var start = i;
            var lineMain = items[i].MainSize + items[i].MainPad;
            i++;
            while (i < items.Length)
            {
                var next = props.MainGap + items[i].MainSize + items[i].MainPad;
                if (lineMain + next > mainSize + epsilon)
                {
                    break;
                }

                lineMain += next;
                i++;
            }
            lines.Add((start, i - start));
        }
        return lines;
    }

    /// <summary>
    /// Measure one flex line (items[start..start+count]): distribute main
    /// free space (grow/shrink, min/max-clamped) and resolve each item's
    /// hypothetical cross size. Returns the line's natural cross size (its
    /// largest item cross extent), its used main extent, and the main size
    /// the free-space math ran against (consumed again when positioning).
    /// </summary>
    private (double LineCross, double UsedMain, double JustifyMain) MeasureLine(
        Item[] items, int start, int count, FlexContainerProps props,
        double mainSize, bool mainIsDefinite, double? minMainFloor,
        double containerWidth, double crossSize)
    {
        var end = start + count;

        // Free space: container main size vs items' outer main sizes + gaps.
        var gapTotal = props.MainGap * Math.Max(0, count - 1);
        var outerSum = 0d;
        for (var i = start; i < end; i++)
        {
            outerSum += items[i].MainSize + items[i].MainPad;
        }

        // When the main size is indefinite (a column with `height: auto`) the
        // container sizes to its content, so the main size used for free-space
        // math is the items' own extent — there is no slack to distribute and
        // grow/justify-content become no-ops. A `min-height` floor still grants
        // real slack between the content and the floor.
        var contentMain = outerSum + gapTotal;
        var justifyMain = mainIsDefinite
            ? mainSize
            : Math.Max(contentMain, minMainFloor ?? 0);

        ResolveFlexibleLengths(items, start, end, justifyMain, gapTotal);

        // Cross size of each item, then the line's natural cross extent.
        var maxCrossOuter = 0d;
        for (var i = start; i < end; i++)
        {
            var crossSelf = MeasureCrossSize(items[i].Box, props, items[i].MainSize, containerWidth, crossSize);
            // CSS Flexbox §4.5: the used cross size is clamped by the item's
            // cross min/max (max-height for row, max-width for column). Clamp
            // before the line cross size is taken so a capped item doesn't
            // inflate the line.
            if (crossSelf > items[i].MaxCross)
            {
                crossSelf = items[i].MaxCross;
            }

            items[i].CrossSize = crossSelf;
            maxCrossOuter = Math.Max(maxCrossOuter, crossSelf + items[i].CrossPad);
        }

        var usedMain = gapTotal;
        for (var i = start; i < end; i++)
        {
            usedMain += items[i].MainSize + items[i].MainPad;
        }

        return (maxCrossOuter, usedMain, justifyMain);
    }

    /// <summary>
    /// Position one measured flex line at its final cross size
    /// (<paramref name="lineCrossSize"/>, possibly grown by align-content
    /// stretch) and cross offset: per-item align-self/align-items stretching,
    /// final content layout, first-baseline alignment (row containers), then
    /// main + cross frame placement.
    /// </summary>
    private void PositionLine(
        Item[] items, int start, int count, FlexContainerProps props,
        double lineCrossSize, double crossOffset, double justifyMain, double crossSize)
    {
        var end = start + count;

        // align-self: auto (the initial) resolves to the container's
        // align-items; anything else overrides it per item (CSS Flexbox
        // §8.3). Stretch grows auto-cross items to the line. A percentage
        // cross size that resolved to `auto` (indefinite container cross, see
        // MeasureCrossSize) is auto for stretch too.
        var crossIndefinite = props.IsRow && double.IsNaN(crossSize);
        for (var i = start; i < end; i++)
        {
            if ((items[i].Props.AlignSelf ?? props.Align) == AlignItems.Stretch
                && ChildCrossSizeIsAuto(items[i].Box, props, crossIndefinite))
            {
                // Stretch is clamped by the item's cross max size (CSS Flexbox
                // §9.4.11): a row item with `max-height` (or a column item with
                // `max-width`) never stretches past its cap.
                items[i].CrossSize = Math.Min(items[i].MaxCross, Math.Max(0, lineCrossSize - items[i].CrossPad));
            }
        }

        for (var i = start; i < end; i++)
        {
            LayoutItemContents(items[i], props);
        }

        // First-baseline alignment (CSS Flexbox §8.3, row containers): the
        // line's shared baseline sits maxAscent below its cross-start edge,
        // and every participating item shifts down so its own first baseline
        // lands there. Runs after the final content layout above so the text
        // fragments it reads carry their final positions.
        var maxAscent = 0d;
        if (props.IsRow)
        {
            for (var i = start; i < end; i++)
            {
                if ((items[i].Props.AlignSelf ?? props.Align) != AlignItems.Baseline)
                {
                    continue;
                }

                items[i].Ascent = ItemFirstBaseline(ref items[i]);
                if (items[i].Ascent > maxAscent)
                {
                    maxAscent = items[i].Ascent;
                }
            }
        }

        // Position along main + cross.
        var usedMain = props.MainGap * Math.Max(0, count - 1);
        for (var i = start; i < end; i++)
        {
            usedMain += items[i].MainSize + items[i].MainPad;
        }

        var (leadingMain, betweenMain) = ResolveMainAxisSpacing(props.Justify, justifyMain, usedMain, count, props.MainGap);

        var cursor = leadingMain;
        for (var i = start; i < end; i++)
        {
            var item = items[i];
            var mainExtent = item.MainSize + item.MainPad;
            var crossExtent = item.CrossSize + item.CrossPad;

            // For *-reverse the main-start edge is on the far end; mirror the
            // logical position while keeping paint order.
            var mainPos = props.IsReverse ? justifyMain - cursor - mainExtent : cursor;
            var align = item.Props.AlignSelf ?? props.Align;
            var cross = crossOffset + (props.IsRow && align == AlignItems.Baseline
                ? maxAscent - item.Ascent
                : ResolveCrossOffset(align, lineCrossSize, crossExtent));

            item.Box.Frame = props.IsRow
                ? new Rect(mainPos, cross, mainExtent, crossExtent)
                : new Rect(cross, mainPos, crossExtent, mainExtent);

            cursor += mainExtent + betweenMain;
        }
    }

    /// <summary>
    /// Stable insertion sort of <paramref name="items"/> by <c>order</c>
    /// (CSS Flexbox §5.4). Item counts in a flex container are small, so an
    /// in-place stable insertion sort is both simplest and cheapest.
    /// </summary>
    private static void StableOrderSort(Item[] items)
    {
        for (var i = 1; i < items.Length; i++)
        {
            var key = items[i];
            var j = i - 1;
            while (j >= 0 && items[j].Props.Order > key.Props.Order)
            {
                items[j + 1] = items[j];
                j--;
            }
            items[j + 1] = key;
        }
    }

    /// <summary>
    /// CSS Flexbox §9.7 — resolve flexible lengths for one line. Items start
    /// at their flex base size, free space is distributed by flex factor, each
    /// item is clamped by its used min/max main size, and clamp violators are
    /// FROZEN at the clamp; the loop then redistributes the remaining free
    /// space among the unfrozen items until no item violates. A single clamp
    /// pass without redistribution under-fills the line: x.com's 600px-max
    /// primary column kept 920px of slot reserved, shoving the sidebar
    /// off-screen.
    /// Simplifications kept from the previous pass (both documented spec
    /// deviations, not regressions): shrink is weighted by flex-shrink alone
    /// (the spec scales it by the base size), and a flex-factor sum below 1
    /// still distributes all the free space.
    /// </summary>
    private static void ResolveFlexibleLengths(Item[] items, int start, int end, double mainSize, double gapTotal)
    {
        // §9.7 step 1 — grow when the hypothetical outer sizes underfill the
        // line, shrink when they overflow it. MainSize holds the hypothetical
        // (min/max-clamped base) size on entry.
        var hypoOuter = gapTotal;
        for (var i = start; i < end; i++)
        {
            hypoOuter += items[i].MainSize + items[i].MainPad;
        }

        var growing = hypoOuter < mainSize;

        // §9.7 step 2 — freeze inflexible items at their hypothetical size:
        // zero flex factor, or the clamp already moved them against the flex
        // direction (growing an item whose base exceeds its hypothetical size
        // would re-violate max immediately; same for shrink vs min).
        var unfrozen = 0;
        for (var i = start; i < end; i++)
        {
            var factor = growing ? items[i].Props.Grow : items[i].Props.Shrink;
            items[i].Frozen = factor <= 0
                || (growing && items[i].Basis > items[i].MainSize)
                || (!growing && items[i].Basis < items[i].MainSize);
            if (!items[i].Frozen)
            {
                unfrozen++;
            }
        }

        // §9.7 step 4 — distribute, clamp, freeze violators, repeat. Every
        // round either freezes all items (zero total violation) or at least
        // one violator, so the loop terminates in <= item count rounds.
        const double epsilon = 0.0001;
        while (unfrozen > 0)
        {
            // Remaining free space from frozen targets + unfrozen base sizes.
            var used = gapTotal;
            var totalFactor = 0d;
            for (var i = start; i < end; i++)
            {
                used += items[i].MainPad + (items[i].Frozen ? items[i].MainSize : items[i].Basis);
                if (!items[i].Frozen)
                {
                    totalFactor += growing ? items[i].Props.Grow : items[i].Props.Shrink;
                }
            }
            var remaining = mainSize - used;
            var distribute = totalFactor > 0
                && ((growing && remaining > 0) || (!growing && remaining < 0));

            var totalViolation = 0d;
            for (var i = start; i < end; i++)
            {
                if (items[i].Frozen)
                {
                    continue;
                }

                var factor = growing ? items[i].Props.Grow : items[i].Props.Shrink;
                var target = items[i].Basis;
                if (distribute)
                {
                    target += remaining * (factor / totalFactor);
                }

                var clamped = Math.Max(items[i].MinMain, Math.Min(target, items[i].MaxMain));
                if (clamped < 0)
                {
                    clamped = 0;
                }

                items[i].MainSize = clamped;
                items[i].Violation = clamped - target;
                totalViolation += items[i].Violation;
            }

            if (totalViolation > epsilon)
            {
                // Min violations dominate: freeze the min violators.
                for (var i = start; i < end; i++)
                {
                    if (!items[i].Frozen && items[i].Violation > 0)
                    {
                        items[i].Frozen = true;
                        unfrozen--;
                    }
                }
            }
            else if (totalViolation < -epsilon)
            {
                // Max violations dominate: freeze the max violators.
                for (var i = start; i < end; i++)
                {
                    if (!items[i].Frozen && items[i].Violation < 0)
                    {
                        items[i].Frozen = true;
                        unfrozen--;
                    }
                }
            }
            else
            {
                break; // zero total violation — every target is final
            }
        }
    }

    /// <summary>
    /// The item's used maximum main size (max-width for row, max-height for
    /// column), or <see cref="double.PositiveInfinity"/> when `none`.
    /// Percentages resolve against the same basis as the minimum
    /// (<see cref="ResolveMinMainSize"/>).
    /// </summary>
    private double ResolveMaxMainSize(Box.Box child, FlexContainerProps props, double containerWidth, double? containerHeight)
    {
        if (child.Kind == BoxKind.AnonymousBlock || child.Style is null)
        {
            return double.PositiveInfinity;
        }

        var maxProp = props.IsRow ? PropertyId.MaxWidth : PropertyId.MaxHeight;
        var basisPx = props.IsRow ? containerWidth : (containerHeight ?? _viewport.Height);
        var max = BlockLayout.ResolveMaxLength(child.Style, maxProp, basisPx, _viewport);
        return max is { } m ? Math.Max(0, m) : double.PositiveInfinity;
    }

    /// <summary>
    /// The item's used maximum cross size (max-height for row, max-width for
    /// column), or <see cref="double.PositiveInfinity"/> when `none`. A
    /// percentage against an indefinite cross basis (row container with auto
    /// height) behaves as `none` (CSS 2.1 §10.7).
    /// </summary>
    private double ResolveMaxCrossSize(Box.Box child, FlexContainerProps props, double containerWidth, double? containerHeight)
    {
        if (child.Kind == BoxKind.AnonymousBlock || child.Style is null)
        {
            return double.PositiveInfinity;
        }

        var maxProp = props.IsRow ? PropertyId.MaxHeight : PropertyId.MaxWidth;
        if (props.IsRow && containerHeight is null && child.Style.Get(maxProp) is CssPercentage)
        {
            return double.PositiveInfinity;
        }

        var basisPx = props.IsRow ? (containerHeight ?? _viewport.Height) : containerWidth;
        var max = BlockLayout.ResolveMaxLength(child.Style, maxProp, basisPx, _viewport);
        return max is { } m ? Math.Max(0, m) : double.PositiveInfinity;
    }

    private struct Item
    {
        public Box.Box Box;
        public FlexItemProps Props;
        public double Basis;
        public double MainSize;
        /// <summary>Used minimum main size: explicit min-width/height, else the
        /// automatic content-based minimum. The item never shrinks below this.</summary>
        public double MinMain;
        /// <summary>Used maximum main size (max-width for row, max-height for
        /// column); <see cref="double.PositiveInfinity"/> when `none`. The item
        /// never grows past this.</summary>
        public double MaxMain;
        /// <summary>Used maximum cross size (max-height for row, max-width for
        /// column); <see cref="double.PositiveInfinity"/> when `none`.</summary>
        public double MaxCross;
        /// <summary>Scratch state for <see cref="ResolveFlexibleLengths"/>:
        /// item is fixed at its current <see cref="MainSize"/> and takes no
        /// further part in free-space distribution.</summary>
        public bool Frozen;
        /// <summary>Scratch state for <see cref="ResolveFlexibleLengths"/>:
        /// clamped target minus unclamped target from the latest round
        /// (positive = min violation, negative = max violation).</summary>
        public double Violation;
        public double CrossSize;
        /// <summary>Scratch state for the baseline pass in
        /// <see cref="PositionLine"/>: distance from the item's border-box
        /// top to its first text baseline (or to the synthesized margin-box
        /// bottom edge when it has no text).</summary>
        public double Ascent;
        /// <summary>Main-axis padding + border (both edges), in px.</summary>
        public double MainPad;
        /// <summary>Cross-axis padding + border (both edges), in px.</summary>
        public double CrossPad;
    }

    /// <summary>
    /// Resolve a child's flex-basis to pixels. <c>flex-basis: auto</c> falls
    /// back to the child's main-axis size property (width for row, height for
    /// column); when that is also <c>auto</c>, we approximate the content size
    /// at "infinite" width using a block measurement pass — the same
    /// max-content trick the inline-block sub-pass uses.
    /// </summary>
    private double ResolveBasis(Box.Box child, double? parsedBasis, FlexContainerProps props, double containerWidth, double? containerHeight)
    {
        if (parsedBasis is { } basis)
        {
            return Math.Max(0, basis);
        }

        var mainProperty = props.IsRow ? PropertyId.Width : PropertyId.Height;
        var mainBasis = props.IsRow ? containerWidth : (containerHeight ?? _viewport.Height);
        var explicitMain = BlockLayout.ResolveLength(child.Style, mainProperty, mainBasis, _viewport, allowAuto: true);
        if (explicitMain is { } main)
        {
            return Math.Max(0, main);
        }

        // auto + no explicit width/height: use content size.
        return MeasureContentMainSize(child, props, containerWidth);
    }

    /// <summary>
    /// The item's used minimum main size (CSS Flexbox §4.5). An explicit
    /// <c>min-width</c>/<c>min-height</c> (length or percentage) wins; when it is
    /// <c>auto</c> the automatic minimum applies — the content-based minimum,
    /// capped at the flex base size so it never forces an item larger than its
    /// preferred size. Anonymous items (bare text runs) get no minimum.
    /// </summary>
    private double ResolveMinMainSize(Box.Box child, FlexContainerProps props, double containerWidth, double? containerHeight, double basis)
    {
        if (child.Kind == BoxKind.AnonymousBlock || child.Style is null)
        {
            return 0;
        }

        var minProp = props.IsRow ? PropertyId.MinWidth : PropertyId.MinHeight;
        if (child.Style.Get(minProp) is not CssKeyword { Name: "auto" })
        {
            var basisPx = props.IsRow ? containerWidth : (containerHeight ?? _viewport.Height);
            var explicitMin = BlockLayout.ResolveLength(child.Style, minProp, basisPx, _viewport);
            if (explicitMin is { } m)
            {
                return Math.Max(0, m);
            }
        }

        // Flexbox §4.5: the automatic minimum applies only when the item's
        // main-axis overflow is visible. A clipping item (the classic
        // nowrap + hidden + text-overflow:ellipsis label) shrinks freely.
        var overflowProp = props.IsRow ? PropertyId.OverflowX : PropertyId.OverflowY;
        if (child.Style.Get(overflowProp) is CssKeyword { Name: not "visible" })
        {
            return 0;
        }

        // min-*: auto → automatic (content-based) minimum, capped at the base
        // size (min-content is always <= max-content/basis, so this is mostly a
        // guard against pathological measurements).
        return Math.Min(basis, MeasureContentMinSize(child, props, containerWidth));
    }

    /// <summary>
    /// Approximate the child's min-content main size: for row direction, lay it
    /// out at zero available width so soft-wrap opportunities all break, leaving
    /// the widest unbreakable run (or the full run under <c>white-space: nowrap</c>);
    /// for column, the natural content height at the container width.
    /// </summary>
    private double MeasureContentMinSize(Box.Box child, FlexContainerProps props, double containerWidth)
    {
        if (props.IsRow)
        {
            // Min-content width is width-independent and stable while the subtree
            // is unchanged, so a clean item serves it from cache instead of
            // re-laying + re-measuring every descendant. (Column direction below
            // returns a height that depends on containerWidth, so it is not
            // cached here.)
            if (!child.SubtreeDirty && child.CachedMinContentWidth is { } cached)
            {
                return cached;
            }

            _block.LayoutItem(child, 0d, null, measure: true);
            var min = UsedMainWidth(child);
            child.CachedMinContentWidth = min;
            return min;
        }
        // Column: the caller consumes only the returned content height, so a
        // clean subtree may replay it (reuseHeight) instead of re-laying.
        return _block.LayoutItem(child, containerWidth, null, measure: true, reuseHeight: true);
    }

    /// <summary>
    /// Approximate the child's max-content main size. For row direction we
    /// run a block measurement pass at very wide width and take the consumed
    /// width; for column we just run block layout at the available width and
    /// return its consumed height. This matches what
    /// <see cref="Inline.InlineLayout"/> does for inline-block shrink-to-fit.
    /// </summary>
    private double MeasureContentMainSize(Box.Box child, FlexContainerProps props, double containerWidth)
    {
        if (props.IsRow)
        {
            return Math.Min(containerWidth, NaturalWidth(child, containerWidth));
        }
        // Column direction: measure the child's natural height at the
        // container width. Return-value-only consumer → replayable.
        return _block.LayoutItem(child, containerWidth, null, measure: true, reuseHeight: true);
    }

    /// <summary>
    /// The child's natural (max-content) outer width. For a non-flex child this
    /// is the standard "lay it out at a huge width and read the consumed
    /// content extent" trick. For a child that is itself a flex container the
    /// huge-width layout would let any inner <c>flex-grow</c> descendant fill
    /// the measurement width and propagate that ~1M back as the natural size
    /// (google.com search-pill bug: a row flex with a flex-grown right cluster
    /// reported itself as ~600 wide, starving the sibling textarea slot).
    /// Per CSS Flexbox 1 §9.9 / CSS Sizing 4, the max-content of a flex
    /// container is structural: sum of items' max-content contributions plus
    /// main gaps (row), or max of items' cross-content sizes (column).
    /// Recursive so it survives arbitrary nesting.
    /// </summary>
    internal double NaturalWidth(Box.Box box, double containerWidth)
    {
        if (box.Kind == BoxKind.AnonymousBlock || !BlockLayout.IsFlexContainer(box.Style))
        {
            // Max-content width is measured at a fixed huge width, so it too is
            // stable while the subtree is unchanged — cache it for clean items.
            if (!box.SubtreeDirty && box.CachedMaxContentWidth is { } cached)
            {
                return cached;
            }

            const double measureWidth = 1_000_000d;
            _block.LayoutItem(box, measureWidth, null, measure: true);
            var natural = UsedMainWidth(box);
            box.CachedMaxContentWidth = natural;
            return natural;
        }

        // Nested flex container — compute structurally.
        var props = FlexParser.ParseContainer(
            box.Style,
            mainAxisBasisPx: containerWidth,
            crossAxisBasisPx: _viewport.Height,
            _viewport);

        // Out-of-flow items don't participate in flex sizing (Flexbox §4); they
        // are placed later by PositionLayout and must not contribute to the
        // intrinsic size.
        var items = new List<Box.Box>(box.Children.Count);
        foreach (var c in box.Children)
        {
            if (!BlockLayout.IsOutOfFlow(c.Style))
            {
                items.Add(c);
            }
        }

        if (items.Count == 0)
        {
            return 0;
        }

        if (props.IsRow)
        {
            // Row flex: sum of items' outer natural widths + (n-1) * MainGap.
            double sum = 0;
            foreach (var item in items)
            {
                ResolveBoxModel(item, containerWidth);
                sum += ItemNaturalWidth(item, containerWidth)
                       + item.Padding.Horizontal + item.Border.Horizontal
                       + item.Margin.Horizontal;
            }
            sum += props.MainGap * (items.Count - 1);
            return sum;
        }

        // Column flex: cross axis is horizontal — natural width is the max of
        // items' outer natural widths.
        double max = 0;
        foreach (var item in items)
        {
            ResolveBoxModel(item, containerWidth);
            var outer = ItemNaturalWidth(item, containerWidth)
                        + item.Padding.Horizontal + item.Border.Horizontal
                        + item.Margin.Horizontal;
            if (outer > max)
            {
                max = outer;
            }
        }
        return max;
    }

    /// <summary>
    /// A single flex item's natural (max-content) inner width. Honours an
    /// explicit <c>flex-basis</c> length, then an explicit <c>width</c>, then
    /// falls back to <see cref="NaturalWidth"/> for the content size.
    /// </summary>
    private double ItemNaturalWidth(Box.Box item, double containerWidth)
    {
        if (item.Style is not null)
        {
            var itemProps = FlexParser.ParseItem(item.Style);
            if (itemProps.Basis is { } b)
            {
                return Math.Max(0, b);
            }

            var explicitWidth = BlockLayout.ResolveLength(
                item.Style, PropertyId.Width, containerWidth, _viewport, allowAuto: true);
            if (explicitWidth is { } w)
            {
                return Math.Max(0, w);
            }
        }
        return NaturalWidth(item, containerWidth);
    }

    /// <summary>
    /// Rightmost content edge of a measured item, in its own content-box space.
    /// For a nested flex container the item's content lives on its flex
    /// children's frames (positioned along the main axis), so the extent is the
    /// max child right edge — <see cref="MeasureUsedWidth"/>'s text-fragment
    /// walk would miss those per-item offsets and under-measure the row.
    /// </summary>
    private static double UsedMainWidth(Box.Box box)
    {
        // An AnonymousBlockBox inherits its parent's ComputedStyle (for text
        // inheritance), so its Display can read "flex"/"inline-flex" even though
        // it is really an inline-run wrapper — never treat it as a flex
        // container, or this would read its (frame-less) text children as items
        // and report width 0.
        if (box.Kind == BoxKind.AnonymousBlock || !BlockLayout.IsFlexContainer(box.Style))
        {
            return MeasureUsedWidth(box);
        }

        double max = 0;
        foreach (var item in box.Children)
        {
            max = Math.Max(max, item.Frame.X + item.Frame.Width);
        }

        return max;
    }

    private static double MeasureUsedWidth(Box.Box box)
    {
        double max = 0;
        Walk(box);
        return max;

        // Mirror Inline.InlineLayout.MeasureUsedWidth: take the rightmost edge
        // of *content* (text fragments, replaced boxes, atomic inlines) only.
        // Block/anonymous container frames are sized to the measurement width
        // (1,000,000px) during the max-content pass, so counting their
        // Frame.Width here would report the measurement width as the item's
        // content size — the bug that made every flex item balloon to the
        // container width. We descend through containers but never read their
        // own frame.
        void Walk(Box.Box node)
        {
            switch (node)
            {
                case TextBox tb:
                    foreach (var frag in tb.Fragments)
                    {
                        max = Math.Max(max, frag.X + frag.Width);
                    }

                    return;
                case ImageBox img:
                    max = Math.Max(max, img.Frame.X + img.Frame.Width);
                    return;
                case InlineBox ib when ib != box
                    && ib.Style?.Get(PropertyId.Display) is CssKeyword { Name: "inline-block" }:
                    max = Math.Max(max, ib.Frame.X + ib.Frame.Width);
                    return;
            }
            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }
    }

    /// <summary>Measure the cross-axis natural size of an item that hasn't
    /// been told to stretch.</summary>
    private double MeasureCrossSize(Box.Box child, FlexContainerProps props, double itemMainSize, double containerWidth, double containerCross)
    {
        var crossProperty = props.IsRow ? PropertyId.Height : PropertyId.Width;
        // CSS 2.1 §10.5: a child's percentage cross size resolves to `auto` when
        // the container's cross size is indefinite. For a row container the cross
        // axis is height, and an auto-height container has an indefinite cross
        // (containerCross is NaN). Resolving `height: 100%` against the viewport
        // there would inflate the item to a near-viewport height — angular.dev's
        // search `<input>` (`height: 100%` inside an auto-height flex row) blew up
        // to ~900px, dragging the whole banner past its container. When the cross
        // basis is indefinite we treat a percentage cross size as auto and fall
        // through to the content/stretch path below.
        var crossIsIndefinite = props.IsRow && double.IsNaN(containerCross);
        if (!(crossIsIndefinite && child.Style?.Get(crossProperty) is CssPercentage))
        {
            var crossBasis = props.IsRow ? (double.IsNaN(containerCross) ? _viewport.Height : containerCross) : containerWidth;
            var explicitCross = BlockLayout.ResolveLength(child.Style, crossProperty, crossBasis, _viewport, allowAuto: true);
            if (explicitCross is { } c)
            {
                return Math.Max(0, c);
            }
        }

        // Auto cross size: lay out the child at the chosen main size and let
        // its natural content height settle. For row direction the child's
        // content width is itemMainSize; the consumed block height becomes
        // the natural cross size.
        if (props.IsRow)
        {
            // Height-only measurement: the caller takes the consumed height and
            // never reads fragments, so a clean subtree may replay its cached
            // measured height (reuseHeight) instead of re-laying every line.
            return _block.LayoutItem(child, itemMainSize, null, measure: true, reuseHeight: true);
        }
        else
        {
            const double measureWidth = 1_000_000d;
            _block.LayoutItem(child, measureWidth, null, measure: true);
            return Math.Min(containerWidth, UsedMainWidth(child));
        }
    }

    private static bool ChildCrossSizeIsAuto(Box.Box child, FlexContainerProps props, bool crossIndefinite)
    {
        var crossProperty = props.IsRow ? PropertyId.Height : PropertyId.Width;
        if (child.Style is null)
        {
            return true;
        }

        var value = child.Style.Get(crossProperty);
        if (value is CssKeyword { Name: "auto" })
        {
            return true;
        }
        // A percentage cross size against an indefinite container cross resolves
        // to auto (CSS 2.1 §10.5), so it stretches like an auto item would.
        return crossIndefinite && value is CssPercentage;
    }

    /// <summary>
    /// Re-lay the item's box at its final main+cross sizes. After main-axis
    /// distribution the child's content width is decided, so a final block
    /// pass at that width settles its descendants' frames.
    /// </summary>
    private void LayoutItemContents(Item item, FlexContainerProps props)
    {
        var child = item.Box;
        // MainSize / CrossSize are already content-box sizes (see step 1), so
        // they map straight to the block pass's content width/height — no
        // padding subtraction here (that's what double-counted before).
        var (contentWidth, contentHeight) = props.IsRow
            ? (item.MainSize, item.CrossSize)
            : (item.CrossSize, item.MainSize);
        // The item's used cross/main sizes are definite by the time we get
        // here (resolved by main-axis distribution + align-items: stretch),
        // so descendants with `height: 100%` should see the item's content
        // height as their containing block — not collapse to 0. Without this
        // a flex chain like  navbar(height:60) > brand(height:100%) >
        // logo(height:100%, background-image)  renders the logo as zero.
        _block.LayoutItem(child, Math.Max(0, contentWidth), Math.Max(0, contentHeight));
    }

    /// <summary>
    /// Resolve the leading offset + between-item gap for <c>justify-content</c>.
    /// The <c>gap</c> property is honoured as a minimum between items: the
    /// space-* distributions add on top.
    /// </summary>
    private static (double Leading, double Between) ResolveMainAxisSpacing(JustifyContent justify, double mainSize, double usedMain, int itemCount, double gap)
    {
        var free = Math.Max(0, mainSize - usedMain);
        return justify switch
        {
            JustifyContent.FlexEnd => (free, gap),
            JustifyContent.Center => (free / 2d, gap),
            JustifyContent.SpaceBetween when itemCount > 1
                => (0, gap + free / (itemCount - 1)),
            JustifyContent.SpaceAround when itemCount > 0
                => (free / (itemCount * 2d), gap + free / itemCount),
            JustifyContent.SpaceEvenly when itemCount > 0
                => (free / (itemCount + 1d), gap + free / (itemCount + 1d)),
            _ => (0, gap), // flex-start (and space-* with degenerate item counts)
        };
    }

    private static double ResolveCrossOffset(AlignItems align, double lineCross, double itemCross)
    {
        var free = lineCross - itemCross;
        return align switch
        {
            AlignItems.FlexEnd => free,
            AlignItems.Center => free / 2d,
            AlignItems.Stretch => 0, // already resized to lineCross above
            // Row baseline items are positioned by the baseline pass in
            // PositionLine before this is consulted; reaching here means a
            // column container, where baseline falls back to flex-start.
            AlignItems.Baseline => 0,
            _ => 0,
        };
    }

    /// <summary>
    /// Resolve the leading offset + between-line spacing for
    /// <c>align-content</c> (CSS Flexbox §8.4). Mirrors
    /// <see cref="ResolveMainAxisSpacing"/>: the cross gap is a minimum and
    /// the space-* distributions add on top. <c>stretch</c> consumed its free
    /// space growing the lines, so any remainder packs like flex-start.
    /// </summary>
    private static (double Leading, double Between) ResolveCrossAxisSpacing(AlignContent align, double free, int lineCount, double gap)
    {
        if (free < 0)
        {
            free = 0;
        }

        return align switch
        {
            AlignContent.FlexEnd => (free, gap),
            AlignContent.Center => (free / 2d, gap),
            AlignContent.SpaceBetween when lineCount > 1
                => (0, gap + free / (lineCount - 1)),
            AlignContent.SpaceAround when lineCount > 0
                => (free / (lineCount * 2d), gap + free / lineCount),
            AlignContent.SpaceEvenly when lineCount > 0
                => (free / (lineCount + 1d), gap + free / (lineCount + 1d)),
            _ => (0, gap), // flex-start, stretch, degenerate space-*
        };
    }

    /// <summary>
    /// The item's first-baseline ascent: distance from its border-box top to
    /// the baseline of the first text fragment in its subtree, accumulating
    /// frame + border/padding offsets exactly like the painter does. An item
    /// with no text synthesizes its baseline from the margin-box bottom edge
    /// (CSS Flexbox §8.5 / CSS 2.1 §10.8.1 fallback).
    /// </summary>
    private static double ItemFirstBaseline(ref readonly Item item)
    {
        var box = item.Box;
        if (TryFindFirstBaseline(box, box.Border.Top + box.Padding.Top, out var ascent))
        {
            return ascent;
        }

        return item.CrossSize + item.CrossPad + box.Margin.Bottom;
    }

    /// <summary>
    /// Depth-first search for the first in-flow text fragment under
    /// <paramref name="box"/>. <paramref name="contentTop"/> is the offset of
    /// the box's content area from the flex item's border-box top; fragments
    /// are stored in their enclosing block's content space, so the first
    /// fragment's Y + Baseline lands on the alphabetic baseline.
    /// </summary>
    private static bool TryFindFirstBaseline(Box.Box box, double contentTop, out double baseline)
    {
        foreach (var child in box.Children)
        {
            if (child is TextBox tb)
            {
                if (tb.Fragments.Count > 0)
                {
                    var frag = tb.Fragments[0];
                    baseline = contentTop + child.Frame.Y + frag.Y + frag.Baseline;
                    return true;
                }
                continue;
            }
            // Out-of-flow descendants take no part in the in-flow line boxes,
            // so they don't contribute a baseline.
            if (BlockLayout.IsOutOfFlow(child.Style))
            {
                continue;
            }

            if (TryFindFirstBaseline(child, contentTop + child.Frame.Y + child.Border.Top + child.Padding.Top, out baseline))
            {
                return true;
            }
        }
        baseline = 0;
        return false;
    }

    private void ResolveBoxModel(Box.Box box, double containerWidth)
    {
        // An anonymous flex item (a bare-text run wrapper) inherits the
        // container's ComputedStyle for text properties, but box-model
        // properties are not inherited — they take their initial value of 0.
        // Without this it would pick up the container's own padding/border (e.g.
        // a padded button), double-applying it and over-sizing the item.
        if (box.Kind == BoxKind.AnonymousBlock)
        {
            box.Margin = Edges.Zero;
            box.Padding = Edges.Zero;
            box.Border = Edges.Zero;
            return;
        }

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

        box.Border = new Edges(
            BlockLayout.ResolveBorderWidth(box.Style, PropertyId.BorderTopWidth, PropertyId.BorderTopStyle, _viewport),
            BlockLayout.ResolveBorderWidth(box.Style, PropertyId.BorderRightWidth, PropertyId.BorderRightStyle, _viewport),
            BlockLayout.ResolveBorderWidth(box.Style, PropertyId.BorderBottomWidth, PropertyId.BorderBottomStyle, _viewport),
            BlockLayout.ResolveBorderWidth(box.Style, PropertyId.BorderLeftWidth, PropertyId.BorderLeftStyle, _viewport));
    }
}
