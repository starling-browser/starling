using System.Globalization;

namespace Starling.Css.Values;

/// <summary>
/// CSS Values 4 §11.2 — <c>attr()</c> type coercion. Given an attribute lookup,
/// reads the raw attribute string and produces a typed <see cref="CssValue"/>
/// matching the requested type-or-unit. Returns the fallback (or null if none)
/// on coercion failure.
/// </summary>
public static class AttrResolver
{
    /// <summary>Resolve a single <see cref="CssAttrReference"/> against a lookup
    /// that returns the raw attribute string, or null if the attribute is absent.</summary>
    public static CssValue? Resolve(CssAttrReference attr, Func<string, string?> lookup)
    {
        var raw = lookup(attr.AttrName);
        if (raw is null)
        {
            return attr.Fallback;
        }

        var coerced = Coerce(raw, attr.TypeOrUnit);
        return coerced ?? attr.Fallback;
    }

    /// <summary>Coerce a raw attribute string into the requested CSS type. Returns
    /// null if the value cannot be parsed under the requested type.</summary>
    public static CssValue? Coerce(string raw, string? typeOrUnit)
    {
        var t = (typeOrUnit ?? "string").Trim().ToLowerInvariant();
        var trimmed = raw.Trim();

        switch (t)
        {
            case "string":
                return new CssString(raw);
            case "url":
                return new CssUrl(trimmed);
            case "ident":
                return string.IsNullOrEmpty(trimmed) ? null : new CssKeyword(trimmed);
            case "color":
                return TryParseColor(trimmed);
            case "number":
                return TryParseDouble(trimmed, out var n) ? new CssNumber(n) : null;
            case "integer":
                return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
                    ? new CssNumber(i)
                    : null;
            case "%" or "percentage":
                return TryParseDouble(trimmed, out var pct) ? new CssPercentage(pct) : null;
            case "length":
                return ParseLength(trimmed, CssLengthUnit.Px);
        }

        // Unit-keyed coercion: px, em, rem, %, deg, ms, s, hz, dpi, etc.
        if (TryUnitKeyword(t, out CssLengthUnit lenUnit))
        {
            return TryParseDouble(trimmed, out var v) ? new CssLength(v, lenUnit) : null;
        }

        if (TryAngleUnit(t, out CssAngleUnit angUnit))
        {
            return TryParseDouble(trimmed, out var av) ? new CssAngle(av, angUnit) : null;
        }

        if (TryTimeUnit(t, out CssTimeUnit timeUnit))
        {
            return TryParseDouble(trimmed, out var tv) ? new CssTime(tv, timeUnit) : null;
        }

        if (TryFrequencyUnit(t, out CssFrequencyUnit freqUnit))
        {
            return TryParseDouble(trimmed, out var fv) ? new CssFrequency(fv, freqUnit) : null;
        }

        if (TryResolutionUnit(t, out CssResolutionUnit resUnit))
        {
            return TryParseDouble(trimmed, out var rv) ? new CssResolution(rv, resUnit) : null;
        }

        // Unknown type identifier — fall back to string.
        return new CssString(raw);
    }

    private static CssValue? ParseLength(string raw, CssLengthUnit defaultUnit)
    {
        // Strip a trailing unit, then parse the number.
        var end = raw.Length;
        while (end > 0 && (char.IsLetter(raw[end - 1]) || raw[end - 1] == '%'))
        {
            end--;
        }

        var numberPart = raw[..end];
        var unitPart = raw[end..];
        if (!TryParseDouble(numberPart, out var v))
        {
            return null;
        }

        if (string.IsNullOrEmpty(unitPart))
        {
            return new CssLength(v, defaultUnit);
        }

        if (unitPart == "%")
        {
            return new CssPercentage(v);
        }

        if (TryUnitKeyword(unitPart.ToLowerInvariant(), out var lu))
        {
            return new CssLength(v, lu);
        }

        return null;
    }

    private static CssColor? TryParseColor(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        if (raw.StartsWith('#') && ColorParser.TryParseHex(raw[1..], out var hex))
        {
            return hex;
        }

        if (NamedColors.TryGet(raw, out var named))
        {
            return named;
        }

        return null;
    }

    private static bool TryParseDouble(string s, out double v)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    private static bool TryUnitKeyword(string s, out CssLengthUnit unit)
    {
        if (Enum.TryParse(s, ignoreCase: true, out unit))
        {
            return true;
        }

        unit = CssLengthUnit.Px;
        return false;
    }

    private static bool TryAngleUnit(string s, out CssAngleUnit unit)
    {
        switch (s)
        {
            case "deg": unit = CssAngleUnit.Degrees; return true;
            case "grad": unit = CssAngleUnit.Gradians; return true;
            case "rad": unit = CssAngleUnit.Radians; return true;
            case "turn": unit = CssAngleUnit.Turns; return true;
            default: unit = CssAngleUnit.Degrees; return false;
        }
    }

    private static bool TryTimeUnit(string s, out CssTimeUnit unit)
    {
        switch (s)
        {
            case "s": unit = CssTimeUnit.Seconds; return true;
            case "ms": unit = CssTimeUnit.Milliseconds; return true;
            default: unit = CssTimeUnit.Seconds; return false;
        }
    }

    private static bool TryFrequencyUnit(string s, out CssFrequencyUnit unit)
    {
        switch (s)
        {
            case "hz": unit = CssFrequencyUnit.Hertz; return true;
            case "khz": unit = CssFrequencyUnit.Kilohertz; return true;
            default: unit = CssFrequencyUnit.Hertz; return false;
        }
    }

    private static bool TryResolutionUnit(string s, out CssResolutionUnit unit)
    {
        switch (s)
        {
            case "dpi": unit = CssResolutionUnit.Dpi; return true;
            case "dpcm": unit = CssResolutionUnit.Dpcm; return true;
            case "dppx" or "x": unit = CssResolutionUnit.Dppx; return true;
            default: unit = CssResolutionUnit.Dppx; return false;
        }
    }
}
