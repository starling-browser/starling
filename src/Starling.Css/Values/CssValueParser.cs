using Starling.Css.Parser;
using Starling.Css.Tokenizer;

namespace Starling.Css.Values;

public static class CssValueParser
{
    public static CssValue Parse(IReadOnlyList<CssComponentValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var parsed = values
            .Where(value => value is not CssTokenValue { Token.Type: CssTokenType.Whitespace })
            .Select(ParseComponentValue)
            .ToList();
        return parsed.Count == 1 ? parsed[0] : new CssValueList(parsed);
    }

    public static IReadOnlyList<CssValue> ParseList(IReadOnlyList<CssComponentValue> values)
        => Parse(values) is CssValueList list ? list.Values : [Parse(values)];

    private static CssValue ParseComponentValue(CssComponentValue value)
        => value switch
        {
            CssTokenValue token => ParseToken(token.Token),
            CssFunction function => ParseFunction(function),
            CssSimpleBlock block => new CssFunctionValue(
                block.StartToken.ToString(),
                block.Values.Select(ParseComponentValue).ToList()),
            _ => new CssKeyword("initial"),
        };

    private static CssValue ParseToken(CssToken token)
        => token.Type switch
        {
            CssTokenType.Ident => ParseIdent(token.Value),
            CssTokenType.Number => new CssNumber(token.Number),
            CssTokenType.Percentage => new CssPercentage(token.Number),
            CssTokenType.Dimension => ParseDimension(token.Number, token.Unit),
            CssTokenType.Hash when ColorParser.TryParseHex(token.Value, out var color) => color,
            CssTokenType.String => new CssString(token.Value),
            CssTokenType.Url => new CssUrl(token.Value),
            CssTokenType.Delim => new CssKeyword(token.Delimiter.ToString()),
            _ => new CssKeyword(token.Value),
        };

    private static CssValue ParseIdent(string value)
        => NamedColors.TryGet(value, out var color)
            ? color
            : new CssKeyword(value.ToLowerInvariant());

    private static CssValue ParseDimension(double value, string unit)
    {
        if (Enum.TryParse<CssLengthUnit>(unit, ignoreCase: true, out var lengthUnit))
        {
            return new CssLength(value, lengthUnit);
        }

        return unit.ToLowerInvariant() switch
        {
            "deg" => new CssAngle(value, CssAngleUnit.Degrees),
            "grad" => new CssAngle(value, CssAngleUnit.Gradians),
            "rad" => new CssAngle(value, CssAngleUnit.Radians),
            "turn" => new CssAngle(value, CssAngleUnit.Turns),
            "s" => new CssTime(value, CssTimeUnit.Seconds),
            "ms" => new CssTime(value, CssTimeUnit.Milliseconds),
            "hz" => new CssFrequency(value, CssFrequencyUnit.Hertz),
            "khz" => new CssFrequency(value, CssFrequencyUnit.Kilohertz),
            "dpi" => new CssResolution(value, CssResolutionUnit.Dpi),
            "dpcm" => new CssResolution(value, CssResolutionUnit.Dpcm),
            "dppx" or "x" => new CssResolution(value, CssResolutionUnit.Dppx),
            _ => new CssDimension(value, unit),
        };
    }

    private static CssValue ParseFunction(CssFunction function)
    {
        var name = function.Name.ToLowerInvariant();
        if (name == "var")
        {
            return ParseVar(function.Values);
        }

        if (name == "env")
        {
            return ParseEnv(function.Values);
        }

        if (name == "attr")
        {
            return ParseAttr(function.Values);
        }

        if (name == "url")
        {
            // `url("…")` tokenizes as a function token (`url(` + string +
            // `)`) rather than a `<url-token>`. Unify both shapes to CssUrl
            // so downstream property handling doesn't need to special-case
            // quoted vs. bare URLs.
            var args = SplitArguments(function.Values).ToList();
            if (args.Count > 0 && Parse(args[0]) is CssString s)
            {
                return new CssUrl(s.Value);
            }
        }

        if (IsMathFunction(name))
        {
            return CalcEvaluator.ParseFunction(name, function.Values);
        }

        if (IsColorFunction(name))
        {
            if (ColorParser.TryParseFunction(name, function.Values, out var color))
            {
                return color;
            }
        }

        return new CssFunctionValue(name, SplitArguments(function.Values).Select(Parse).ToList());
    }

    private static bool IsMathFunction(string name)
        => name is "calc" or "min" or "max" or "clamp" or "round" or "mod" or "rem"
            or "sin" or "cos" or "tan" or "asin" or "acos" or "atan" or "atan2"
            or "sqrt" or "pow" or "hypot" or "log" or "exp"
            or "abs" or "sign";

    private static bool IsColorFunction(string name)
        => name is "rgb" or "rgba" or "hsl" or "hsla" or "hwb"
            or "lab" or "lch" or "oklab" or "oklch" or "color" or "color-mix";

    private static CssVarReference ParseVar(IReadOnlyList<CssComponentValue> values)
    {
        var args = SplitArguments(values).ToList();
        var name = args.Count > 0
            ? args[0].OfType<CssTokenValue>().FirstOrDefault(v => v.Token.Type == CssTokenType.Ident)?.Token.Value
            : null;
        var fallback = args.Count > 1 ? Parse(args[1]) : null;
        return new CssVarReference(name ?? string.Empty, fallback);
    }

    private static CssEnvReference ParseEnv(IReadOnlyList<CssComponentValue> values)
    {
        var args = SplitArguments(values).ToList();
        var name = args.Count > 0
            ? args[0].OfType<CssTokenValue>().FirstOrDefault(v => v.Token.Type == CssTokenType.Ident)?.Token.Value
            : null;
        var fallback = args.Count > 1 ? Parse(args[1]) : null;
        return new CssEnvReference(name ?? string.Empty, fallback);
    }

    private static CssAttrReference ParseAttr(IReadOnlyList<CssComponentValue> values)
    {
        var args = SplitArguments(values).ToList();
        if (args.Count == 0)
        {
            return new CssAttrReference(string.Empty, null, null);
        }
        // First argument: name [type-or-unit]
        var firstTokens = args[0]
            .Where(v => v is not CssTokenValue { Token.Type: CssTokenType.Whitespace })
            .OfType<CssTokenValue>()
            .Select(v => v.Token)
            .ToList();
        var attrName = firstTokens.FirstOrDefault(t => t.Type == CssTokenType.Ident).Value ?? string.Empty;
        var typeOrUnit = firstTokens
            .Skip(1)
            .FirstOrDefault(t => t.Type is CssTokenType.Ident or CssTokenType.Percentage or CssTokenType.Dimension)
            .Value;
        var fallback = args.Count > 1 ? Parse(args[1]) : null;
        return new CssAttrReference(attrName, string.IsNullOrEmpty(typeOrUnit) ? null : typeOrUnit, fallback);
    }

    private static IEnumerable<IReadOnlyList<CssComponentValue>> SplitArguments(IReadOnlyList<CssComponentValue> values)
    {
        var current = new List<CssComponentValue>();
        foreach (var value in values)
        {
            if (value is CssTokenValue { Token.Type: CssTokenType.Comma })
            {
                yield return current;
                current = [];
                continue;
            }

            current.Add(value);
        }

        yield return current;
    }
}
