using Starling.Css.CounterStyle;

namespace Starling.Layout.Tree;

/// <summary>
/// CSS Lists 3 §3 — list-item marker text generation. Maps a
/// <c>list-style-type</c> keyword + ordinal to the string a marker box renders
/// (e.g. <c>decimal</c> ordinal 3 → <c>"3."</c>, <c>upper-roman</c> ordinal 4 →
/// <c>"IV."</c>). Glyph markers (<c>disc</c>/<c>circle</c>/<c>square</c>) map to
/// the Unicode bullet characters browsers use. The integer→string algorithms
/// (roman/alpha/decimal/greek) live in <see cref="CounterStyleResolver"/> so
/// list markers and CSS Counter Styles 3 share one source of truth.
/// </summary>
internal static class ListMarker
{
    /// <summary>Unicode bullet for <c>disc</c> (U+2022 BULLET).</summary>
    public const string Disc = CounterStyleResolver.Disc;

    /// <summary>Unicode bullet for <c>circle</c> (U+25E6 WHITE BULLET).</summary>
    public const string Circle = CounterStyleResolver.Circle;

    /// <summary>Unicode bullet for <c>square</c> (U+25AA BLACK SMALL SQUARE).</summary>
    public const string Square = CounterStyleResolver.Square;

    private static readonly HashSet<string> KnownCounterStyles =
    [
        "decimal", "decimal-leading-zero",
        "lower-alpha", "lower-latin", "upper-alpha", "upper-latin",
        "lower-roman", "upper-roman", "lower-greek",
    ];

    /// <summary>
    /// Returns the rendered marker text for <paramref name="listStyleType"/> at
    /// the 1-based <paramref name="ordinal"/>, or null when the type is
    /// <c>none</c> (no marker) or unrecognised.
    /// </summary>
    public static string? Render(string listStyleType, int ordinal)
    {
        var type = listStyleType.Trim().ToLowerInvariant();
        switch (type)
        {
            case "none":
                return null;
            case "disc":
                return Disc;
            case "circle":
                return Circle;
            case "square":
                return Square;
        }

        if (!KnownCounterStyles.Contains(type))
        {
            return null;
        }

        // Numeric / alphabetic counter styles render the ordinal followed by a
        // "." suffix per the UA's list-item marker formatting (CSS Lists 3 §3.2).
        // The core representation comes from the shared counter-style resolver.
        return CounterStyleResolver.Default.RenderCore(type, ordinal) + ".";
    }
}
