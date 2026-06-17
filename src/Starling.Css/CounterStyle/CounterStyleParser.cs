using Starling.Css.Parser;
using Starling.Css.Tokenizer;

namespace Starling.Css.CounterStyle;

/// <summary>
/// Extracts <see cref="CounterStyleRule"/> values from parsed stylesheets. The
/// CSS parser recognises <c>@counter-style</c> as an at-rule with a declaration
/// list (see <see cref="Parser.CssParser"/>); this turns those descriptors into
/// a strongly-typed model the <see cref="CounterStyleResolver"/> samples.
/// </summary>
/// <remarks>
/// Fail-soft (CSS Counter Styles 3 §3): a rule with no name is skipped. A bad
/// descriptor value is ignored and the descriptor keeps its initial value. The
/// reserved names <c>decimal</c>/<c>disc</c> can't be overridden, but we accept
/// any other name verbatim.
/// </remarks>
public static class CounterStyleParser
{
    public static IEnumerable<CounterStyleRule> ParseAll(StyleSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        foreach (var rule in sheet.Rules)
        {
            if (rule is AtRule atRule &&
                string.Equals(atRule.Name, "counter-style", StringComparison.OrdinalIgnoreCase) &&
                TryParse(atRule, out var counterStyle))
            {
                yield return counterStyle!;
            }
        }
    }

    public static bool TryParse(AtRule rule, out CounterStyleRule? counterStyle)
    {
        ArgumentNullException.ThrowIfNull(rule);
        counterStyle = null;
        if (!string.Equals(rule.Name, "counter-style", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = ExtractName(rule.Prelude);
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        var system = CounterSystem.Symbolic;
        var fixedFirst = 1;
        string? extendsName = null;
        IReadOnlyList<string> symbols = [];
        IReadOnlyList<AdditiveSymbol> additive = [];
        var negPrefix = "-";
        var negSuffix = "";
        var prefix = "";
        // §3.3: the suffix initial value is ". " (full stop + space).
        var suffix = ". ";
        int? rangeLow = null;
        int? rangeHigh = null;
        var hasRange = false;
        IReadOnlyList<(int? Low, int? High)> rangeSegments = [];
        var padLength = 0;
        var padSymbol = "";
        var fallback = "decimal";

        foreach (var decl in rule.Declarations)
        {
            switch (decl.Name.ToLowerInvariant())
            {
                case "system":
                    (system, fixedFirst, extendsName) = ParseSystem(decl.Value, system, fixedFirst);
                    break;
                case "symbols":
                    symbols = ParseSymbols(decl.Value);
                    break;
                case "additive-symbols":
                    additive = ParseAdditiveSymbols(decl.Value);
                    break;
                case "negative":
                    (negPrefix, negSuffix) = ParseNegative(decl.Value, negPrefix, negSuffix);
                    break;
                case "prefix":
                    prefix = ParseSingleSymbol(decl.Value) ?? prefix;
                    break;
                case "suffix":
                    suffix = ParseSingleSymbol(decl.Value) ?? suffix;
                    break;
                case "range":
                    (rangeLow, rangeHigh, hasRange, rangeSegments) = ParseRange(decl.Value);
                    break;
                case "pad":
                    (padLength, padSymbol) = ParsePad(decl.Value, padLength, padSymbol);
                    break;
                case "fallback":
                    fallback = ParseIdent(decl.Value) ?? fallback;
                    break;
            }
        }

        counterStyle = new CounterStyleRule
        {
            Name = name,
            System = system,
            FixedFirstValue = fixedFirst,
            ExtendsName = extendsName,
            Symbols = symbols,
            AdditiveSymbols = additive,
            NegativePrefix = negPrefix,
            NegativeSuffix = negSuffix,
            Prefix = prefix,
            Suffix = suffix,
            RangeLow = rangeLow,
            RangeHigh = rangeHigh,
            HasExplicitRange = hasRange,
            RangeSegments = rangeSegments,
            PadLength = padLength,
            PadSymbol = padSymbol,
            Fallback = fallback,
        };
        return true;
    }

    private static string ExtractName(IReadOnlyList<CssComponentValue> prelude)
    {
        foreach (var component in prelude)
        {
            if (component is CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var ident } })
            {
                return ident.ToLowerInvariant();
            }
        }
        return string.Empty;
    }

    private static (CounterSystem System, int FixedFirst, string? Extends) ParseSystem(
        IReadOnlyList<CssComponentValue> value, CounterSystem current, int fixedFirst)
    {
        // system: cyclic | numeric | alphabetic | symbolic | additive
        //       | [fixed <integer>?] | [extends <counter-style-name>]
        string? keyword = null;
        int? firstValue = null;
        string? extends = null;
        var sawExtendsKeyword = false;
        foreach (var v in value)
        {
            if (v is not CssTokenValue token)
            {
                continue;
            }

            switch (token.Token.Type)
            {
                case CssTokenType.Ident:
                    if (sawExtendsKeyword)
                    {
                        extends ??= token.Token.Value.ToLowerInvariant();
                    }
                    else if (token.Token.Value.Equals("extends", StringComparison.OrdinalIgnoreCase))
                    {
                        sawExtendsKeyword = true;
                    }
                    else
                    {
                        keyword ??= token.Token.Value.ToLowerInvariant();
                    }

                    break;
                case CssTokenType.Number when token.Token.IsInteger:
                    firstValue ??= (int)token.Token.Number;
                    break;
            }
        }

        if (sawExtendsKeyword)
        {
            return (CounterSystem.Extends, fixedFirst, extends);
        }

        return keyword switch
        {
            "cyclic" => (CounterSystem.Cyclic, fixedFirst, null),
            "fixed" => (CounterSystem.Fixed, firstValue ?? 1, null),
            "symbolic" => (CounterSystem.Symbolic, fixedFirst, null),
            "alphabetic" => (CounterSystem.Alphabetic, fixedFirst, null),
            "numeric" => (CounterSystem.Numeric, fixedFirst, null),
            "additive" => (CounterSystem.Additive, fixedFirst, null),
            _ => (current, fixedFirst, null),
        };
    }

    private static List<string> ParseSymbols(IReadOnlyList<CssComponentValue> value)
    {
        // symbols: <symbol>+ where <symbol> is <string> | <image> | <ident>.
        // We support strings and idents (images are out of scope here).
        var symbols = new List<string>();
        foreach (var v in value)
        {
            if (v is not CssTokenValue token)
            {
                continue;
            }

            if (token.Token.Type is CssTokenType.String or CssTokenType.Ident)
            {
                symbols.Add(token.Token.Value);
            }
        }
        return symbols;
    }

    private static List<AdditiveSymbol> ParseAdditiveSymbols(IReadOnlyList<CssComponentValue> value)
    {
        // additive-symbols: [<integer> && <symbol>]#  — comma-separated
        // "weight symbol" pairs. Sort descending by weight per §2.6.
        var result = new List<AdditiveSymbol>();
        int? weight = null;
        string? symbol = null;
        void Flush()
        {
            if (weight is { } w && symbol is { } s)
            {
                result.Add(new AdditiveSymbol(w, s));
            }

            weight = null;
            symbol = null;
        }
        foreach (var v in value)
        {
            if (v is not CssTokenValue token)
            {
                continue;
            }

            switch (token.Token.Type)
            {
                case CssTokenType.Comma:
                    Flush();
                    break;
                case CssTokenType.Number when token.Token.IsInteger:
                    weight ??= (int)token.Token.Number;
                    break;
                case CssTokenType.String or CssTokenType.Ident:
                    symbol ??= token.Token.Value;
                    break;
            }
        }
        Flush();
        result.Sort((a, b) => b.Weight.CompareTo(a.Weight));
        return result;
    }

    private static (string Prefix, string Suffix) ParseNegative(
        IReadOnlyList<CssComponentValue> value, string curPrefix, string curSuffix)
    {
        // negative: <symbol> <symbol>?  — first is prepended, optional second
        // appended to negative counters (§3.2).
        var symbols = new List<string>();
        foreach (var v in value)
        {
            if (v is CssTokenValue token &&
                token.Token.Type is CssTokenType.String or CssTokenType.Ident or CssTokenType.Delim)
            {
                symbols.Add(token.Token.Type == CssTokenType.Delim
                    ? token.Token.Delimiter.ToString()
                    : token.Token.Value);
            }
        }
        return symbols.Count switch
        {
            >= 2 => (symbols[0], symbols[1]),
            1 => (symbols[0], ""),
            _ => (curPrefix, curSuffix),
        };
    }

    private static string? ParseSingleSymbol(IReadOnlyList<CssComponentValue> value)
    {
        foreach (var v in value)
        {
            if (v is CssTokenValue token)
            {
                if (token.Token.Type is CssTokenType.String or CssTokenType.Ident)
                {
                    return token.Token.Value;
                }

                if (token.Token.Type == CssTokenType.Delim)
                {
                    return token.Token.Delimiter.ToString();
                }
            }
        }
        return null;
    }

    private static string? ParseIdent(IReadOnlyList<CssComponentValue> value)
    {
        foreach (var v in value)
        {
            if (v is CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var ident } })
            {
                return ident.ToLowerInvariant();
            }
        }
        return null;
    }

    private static (int? Low, int? High, bool HasRange, IReadOnlyList<(int? Low, int? High)> Segments) ParseRange(
        IReadOnlyList<CssComponentValue> value)
    {
        // range: [[<integer> | infinite]{2}]# | auto (§3.4). Comma-separated
        // segments; each is a [low high] pair. "auto" leaves the range
        // unbounded. "infinite" maps to null on that side (open-ended).
        var hasAuto = false;
        var segments = new List<(int? Low, int? High)>();
        var bounds = new List<int?>();

        void FlushSegment()
        {
            if (bounds.Count >= 2)
            {
                segments.Add((bounds[0], bounds[1]));
            }

            bounds.Clear();
        }

        foreach (var v in value)
        {
            if (v is not CssTokenValue token)
            {
                continue;
            }

            switch (token.Token.Type)
            {
                case CssTokenType.Ident when token.Token.Value.Equals("auto", StringComparison.OrdinalIgnoreCase):
                    hasAuto = true;
                    break;
                case CssTokenType.Ident when token.Token.Value.Equals("infinite", StringComparison.OrdinalIgnoreCase):
                    bounds.Add(null);
                    break;
                case CssTokenType.Number when token.Token.IsInteger:
                    bounds.Add((int)token.Token.Number);
                    break;
                case CssTokenType.Comma:
                    FlushSegment();
                    break;
            }
        }
        FlushSegment();

        if (hasAuto || segments.Count == 0)
        {
            return (null, null, false, []);
        }
        // RangeLow/RangeHigh mirror the first segment for callers that only
        // read the single-pair form.
        return (segments[0].Low, segments[0].High, true, segments);
    }

    private static (int Length, string Symbol) ParsePad(
        IReadOnlyList<CssComponentValue> value, int curLength, string curSymbol)
    {
        // pad: <integer [0,∞]> && <symbol> (§3.5).
        int? length = null;
        string? symbol = null;
        foreach (var v in value)
        {
            if (v is not CssTokenValue token)
            {
                continue;
            }

            switch (token.Token.Type)
            {
                case CssTokenType.Number when token.Token.IsInteger:
                    length ??= (int)token.Token.Number;
                    break;
                case CssTokenType.String or CssTokenType.Ident:
                    symbol ??= token.Token.Value;
                    break;
            }
        }
        return (length ?? curLength, symbol ?? curSymbol);
    }
}
