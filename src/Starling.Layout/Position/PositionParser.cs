using Starling.Css.Cascade;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Layout.Block;

namespace Starling.Layout.Position;

/// <summary>
/// Reads <c>position</c>, the four inset properties (<c>top</c>/<c>right</c>/
/// <c>bottom</c>/<c>left</c>), and <c>z-index</c> off a
/// <see cref="ComputedStyle"/> into a typed <see cref="PositionedProps"/>.
/// Mirrors the convention used by <see cref="Starling.Layout.Flex.FlexParser"/>.
/// </summary>
internal static class PositionParser
{
    public static PositionedProps Parse(ComputedStyle? style)
    {
        if (style is null)
        {
            return PositionedProps.Static;
        }

        var kind = (style.Get(PropertyId.Position) is CssKeyword k ? k.Name : "static").ToLowerInvariant() switch
        {
            "relative" => PositionKind.Relative,
            "absolute" => PositionKind.Absolute,
            "fixed" => PositionKind.Fixed,
            "sticky" => PositionKind.Sticky,
            _ => PositionKind.Static,
        };

        var top = ParseInset(style.Get(PropertyId.Top));
        var right = ParseInset(style.Get(PropertyId.Right));
        var bottom = ParseInset(style.Get(PropertyId.Bottom));
        var left = ParseInset(style.Get(PropertyId.Left));
        var z = ParseZIndex(style.Get(PropertyId.ZIndex));

        return new PositionedProps(kind, top, right, bottom, left, z);
    }

    private static Inset ParseInset(CssValue value) => value switch
    {
        CssKeyword k when k.Name.Equals("auto", System.StringComparison.OrdinalIgnoreCase) => Inset.Auto,
        CssLength len => Inset.Pixels(BlockLayout.ToPx(len)),
        CssPercentage pct => Inset.Percent(pct.Value),
        CssNumber n => Inset.Pixels(n.Value),
        _ => Inset.Auto,
    };

    private static int? ParseZIndex(CssValue value) => value switch
    {
        CssKeyword k when k.Name.Equals("auto", System.StringComparison.OrdinalIgnoreCase) => null,
        CssNumber n => (int)n.Value,
        // z-index integers may arrive parsed as a length in degenerate cases;
        // be tolerant.
        CssLength len => (int)len.Value,
        _ => null,
    };

    /// <summary>Convenience helper for callers that just need the kind without
    /// the full record.</summary>
    public static PositionKind ParseKind(ComputedStyle? style)
    {
        if (style is null)
        {
            return PositionKind.Static;
        }

        return (style.Get(PropertyId.Position) is CssKeyword k ? k.Name : "static").ToLowerInvariant() switch
        {
            "relative" => PositionKind.Relative,
            "absolute" => PositionKind.Absolute,
            "fixed" => PositionKind.Fixed,
            "sticky" => PositionKind.Sticky,
            _ => PositionKind.Static,
        };
    }
}
