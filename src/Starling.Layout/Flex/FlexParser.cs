using Starling.Css.Cascade;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Layout.Block;

namespace Starling.Layout.Flex;

/// <summary>
/// Translates the relevant flex keywords + lengths off a
/// <see cref="ComputedStyle"/> into the strongly-typed records flex layout
/// consumes. Mirrors <see cref="BlockLayout"/>'s pattern of reading
/// computed-style values directly off the style record — there's no separate
/// "used-value" pass for flex properties since they don't take percentages
/// outside of <c>flex-basis</c> (which is resolved at layout time).
/// </summary>
internal static class FlexParser
{
    public static FlexContainerProps ParseContainer(ComputedStyle? style, double mainAxisBasisPx, double crossAxisBasisPx, Size? viewport)
    {
        var direction = Keyword(style, PropertyId.FlexDirection, "row") switch
        {
            "row-reverse" => FlexDirection.RowReverse,
            "column" => FlexDirection.Column,
            "column-reverse" => FlexDirection.ColumnReverse,
            _ => FlexDirection.Row,
        };

        var wrap = Keyword(style, PropertyId.FlexWrap, "nowrap") switch
        {
            "wrap" => FlexWrap.Wrap,
            "wrap-reverse" => FlexWrap.WrapReverse,
            _ => FlexWrap.NoWrap,
        };

        // `justify-content: normal` is the initial value; in a flex container
        // its used value is `flex-start` (CSS Box Alignment §6.2).
        var justify = Keyword(style, PropertyId.JustifyContent, "flex-start") switch
        {
            "flex-end" or "end" or "right" => JustifyContent.FlexEnd,
            "center" => JustifyContent.Center,
            "space-between" => JustifyContent.SpaceBetween,
            "space-around" => JustifyContent.SpaceAround,
            "space-evenly" => JustifyContent.SpaceEvenly,
            _ => JustifyContent.FlexStart,
        };

        // `align-items: normal` resolves to `stretch` in flex containers
        // (CSS Box Alignment §5.3).
        var align = MapAlignItems(Keyword(style, PropertyId.AlignItems, "stretch"));

        // `align-content: normal` behaves as `stretch` in flex containers
        // (CSS Box Alignment §6.2). Baseline content-distribution takes its
        // start/end fallback (CSS Box Alignment §9.3).
        var contentAlign = Keyword(style, PropertyId.AlignContent, "normal") switch
        {
            "flex-start" or "start" or "baseline" or "first baseline" => AlignContent.FlexStart,
            "flex-end" or "end" or "last baseline" => AlignContent.FlexEnd,
            "center" => AlignContent.Center,
            "space-between" => AlignContent.SpaceBetween,
            "space-around" => AlignContent.SpaceAround,
            "space-evenly" => AlignContent.SpaceEvenly,
            _ => AlignContent.Stretch, // normal | stretch
        };

        // `gap`'s initial computed value is the `normal` keyword, which for
        // flex resolves to 0 (CSS Box Alignment §8.1).
        var rowGap = ResolveGap(style, PropertyId.RowGap, crossAxisBasisPx, viewport);
        var columnGap = ResolveGap(style, PropertyId.ColumnGap, mainAxisBasisPx, viewport);

        return new FlexContainerProps(direction, wrap, justify, align, contentAlign, rowGap, columnGap);
    }

    public static FlexItemProps ParseItem(ComputedStyle? style)
    {
        var grow = NumberOrZero(style, PropertyId.FlexGrow);
        // CSS Flex §7.2: flex-shrink's initial value is 1.
        var shrink = NumberOr(style, PropertyId.FlexShrink, fallback: 1);
        var basis = ParseBasis(style);
        var order = (int)Math.Round(NumberOrZero(style, PropertyId.Order));
        // `align-self: auto` (the initial) defers to the container's
        // align-items at layout time (CSS Flexbox §8.3).
        AlignItems? alignSelf = null;
        if (style?.Get(PropertyId.AlignSelf) is CssKeyword self
            && !self.Name.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            alignSelf = MapAlignItems(self.Name.ToLowerInvariant());
        }
        return new FlexItemProps(grow, Math.Max(0, shrink), basis, order, alignSelf);
    }

    /// <summary>Map an align-items/align-self keyword to its enum value;
    /// <c>normal</c>, <c>stretch</c>, and anything unrecognised land on
    /// stretch (CSS Box Alignment §5.3).</summary>
    private static AlignItems MapAlignItems(string keyword) => keyword switch
    {
        "flex-start" or "start" or "self-start" => AlignItems.FlexStart,
        "flex-end" or "end" or "self-end" => AlignItems.FlexEnd,
        "center" => AlignItems.Center,
        "baseline" or "first baseline" or "last baseline" => AlignItems.Baseline,
        _ => AlignItems.Stretch,
    };

    /// <summary>
    /// Resolve a child's <c>flex-basis</c> to a fixed pixel length, or
    /// <c>null</c> to mean "fall back to the child's main-size property,
    /// then to its content size" (CSS Flex §9.2 step 3).
    /// </summary>
    private static double? ParseBasis(ComputedStyle? style)
    {
        if (style is null)
        {
            return null;
        }

        var value = style.Get(PropertyId.FlexBasis);
        return value switch
        {
            CssKeyword { Name: "auto" } => null,
            CssKeyword { Name: "content" } => null,
            CssLength len => BlockLayout.ToPx(len),
            CssNumber n => n.Value,
            // Percentage flex-basis is resolved at layout time against the
            // container's main size, not parsed up front; surface a sentinel
            // (negative => percentage handled separately) is overkill here —
            // route through layout-time ResolveBasis for percentage support.
            _ => null,
        };
    }

    private static double ResolveGap(ComputedStyle? style, PropertyId id, double basis, Size? viewport)
    {
        if (style is null)
        {
            return 0;
        }

        var value = style.Get(id);
        return value switch
        {
            CssKeyword { Name: "normal" } => 0,
            CssLength len => BlockLayout.ToPx(len, viewport),
            CssPercentage pct => basis * pct.Value / 100d,
            CssNumber n => n.Value,
            _ => 0,
        };
    }

    private static string Keyword(ComputedStyle? style, PropertyId id, string fallback)
    {
        if (style is null)
        {
            return fallback;
        }

        return style.Get(id) is CssKeyword k ? k.Name.ToLowerInvariant() : fallback;
    }

    private static double NumberOrZero(ComputedStyle? style, PropertyId id)
        => NumberOr(style, id, 0);

    private static double NumberOr(ComputedStyle? style, PropertyId id, double fallback)
    {
        if (style is null)
        {
            return fallback;
        }

        return style.Get(id) switch
        {
            CssNumber n => Math.Max(0, n.Value),
            _ => fallback,
        };
    }
}
