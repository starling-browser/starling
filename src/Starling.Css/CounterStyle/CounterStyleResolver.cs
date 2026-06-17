using System.Globalization;
using System.Text;

namespace Starling.Css.CounterStyle;

/// <summary>
/// CSS Counter Styles 3 §2/§6 — generates the marker representation of an
/// integer for a counter style, whether predefined (§7) or defined via
/// <c>@counter-style</c> (§3). Honors <c>prefix</c>/<c>suffix</c>,
/// <c>negative</c>, <c>pad</c>, <c>range</c>, <c>fallback</c>, and
/// <c>extends</c>. Predefined numeric/alphabetic algorithms (decimal, roman,
/// alpha, greek) live here so list markers and counter() share one source of
/// truth.
/// </summary>
public sealed class CounterStyleResolver
{
    private readonly Dictionary<string, CounterStyleRule> _userStyles;

    public CounterStyleResolver(IEnumerable<CounterStyleRule>? userStyles = null)
    {
        _userStyles = new Dictionary<string, CounterStyleRule>(StringComparer.OrdinalIgnoreCase);
        if (userStyles is null)
        {
            return;
        }
        // Last definition of a given name wins, matching the cascade.
        foreach (var style in userStyles)
        {
            _userStyles[style.Name] = style;
        }
    }

    /// <summary>A resolver holding only the predefined styles (§7).</summary>
    public static CounterStyleResolver Default { get; } = new();

    /// <summary>
    /// The full marker text for <paramref name="styleName"/> at
    /// <paramref name="value"/>, including prefix/suffix/negative sign per
    /// §6 "Generating Counter Representations".
    /// </summary>
    public string Render(string styleName, int value)
        => Render(styleName, value, depth: 0);

    /// <summary>
    /// The counter representation only (no prefix/suffix/negative wrapping) —
    /// the body that §6 wraps. Out-of-range or unrepresentable values fall back.
    /// </summary>
    public string RenderCore(string styleName, int value)
        => RenderCore(styleName, value, depth: 0);

    private string Render(string styleName, int value, int depth)
    {
        var (prefix, suffix, negPre, negPost) = Affixes(styleName);
        var core = RenderCore(styleName, value, depth);
        var sign = value < 0 ? negPre : "";
        var signEnd = value < 0 ? negPost : "";
        return prefix + sign + core + signEnd + suffix;
    }

    private (string Prefix, string Suffix, string NegPre, string NegPost) Affixes(string styleName)
    {
        if (_userStyles.TryGetValue(styleName, out var rule))
        {
            // extends inherits its base's affixes unless overridden. We resolve
            // by walking the chain and taking the first explicitly-set value;
            // for simplicity the parsed rule already carries defaults, and
            // extends rules copy descriptors at parse time only for system —
            // so fall through to the base for affixes when this is an extends.
            if (rule.System == CounterSystem.Extends && rule.ExtendsName is { } baseName)
            {
                var baseAffix = Affixes(baseName);
                return (
                    rule.Prefix.Length > 0 ? rule.Prefix : baseAffix.Prefix,
                    rule.Suffix != ". " ? rule.Suffix : baseAffix.Suffix,
                    rule.NegativePrefix != "-" ? rule.NegativePrefix : baseAffix.NegPre,
                    rule.NegativeSuffix.Length > 0 ? rule.NegativeSuffix : baseAffix.NegPost);
            }
            return (rule.Prefix, rule.Suffix, rule.NegativePrefix, rule.NegativeSuffix);
        }

        // Predefined: glyph bullets have an empty suffix; numeric/alphabetic
        // use ". " (§7 lists each predefined style's suffix).
        return IsGlyph(styleName)
            ? ("", "", "-", "")
            : ("", PredefinedSuffix(styleName), "-", "");
    }

    private string RenderCore(string styleName, int value, int depth)
    {
        // Guard against extends/fallback cycles.
        if (depth > 32)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        if (_userStyles.TryGetValue(styleName, out var rule))
        {
            return RenderUserStyle(rule, value, depth);
        }

        return RenderPredefined(styleName, value, depth);
    }

    private string RenderUserStyle(CounterStyleRule rule, int value, int depth)
    {
        // Resolve extends to its base system + descriptors (§3.1 extends).
        if (rule.System == CounterSystem.Extends && rule.ExtendsName is { } baseName)
        {
            var effective = MergeExtends(rule, baseName, depth);
            return RenderUserStyleResolved(effective, value, depth);
        }
        return RenderUserStyleResolved(rule, value, depth);
    }

    private CounterStyleRule MergeExtends(CounterStyleRule rule, string baseName, int depth)
    {
        // An extends rule takes its base's algorithm + any descriptors it does
        // not itself specify. If the base is itself a user style, recurse.
        if (_userStyles.TryGetValue(baseName, out var baseRule))
        {
            if (baseRule.System == CounterSystem.Extends && baseRule.ExtendsName is { } grand && depth < 32)
            {
                baseRule = MergeExtends(baseRule, grand, depth + 1);
            }

            return rule with
            {
                System = baseRule.System,
                FixedFirstValue = baseRule.FixedFirstValue,
                Symbols = rule.Symbols.Count > 0 ? rule.Symbols : baseRule.Symbols,
                AdditiveSymbols = rule.AdditiveSymbols.Count > 0 ? rule.AdditiveSymbols : baseRule.AdditiveSymbols,
                RangeLow = rule.HasExplicitRange ? rule.RangeLow : baseRule.RangeLow,
                RangeHigh = rule.HasExplicitRange ? rule.RangeHigh : baseRule.RangeHigh,
                RangeSegments = rule.HasExplicitRange ? rule.RangeSegments : baseRule.RangeSegments,
                HasExplicitRange = rule.HasExplicitRange || baseRule.HasExplicitRange,
                Fallback = rule.Fallback != "decimal" ? rule.Fallback : baseRule.Fallback,
            };
        }
        // Extends a predefined style: render directly via the predefined path,
        // but the extends rule may override affixes/range. We synthesize an
        // additive/numeric stand-in is impossible here, so mark it as a special
        // "predefined-backed" extends by leaving System=Extends and letting
        // RenderUserStyleResolved delegate to the predefined renderer.
        return rule;
    }

    private string RenderUserStyleResolved(CounterStyleRule rule, int value, int depth)
    {
        // An extends rule still pointing at a predefined base: delegate the
        // core generation to the predefined renderer, then apply this rule's
        // range/pad on top.
        if (rule.System == CounterSystem.Extends && rule.ExtendsName is { } baseName)
        {
            if (OutOfRange(rule, value))
            {
                return RenderCore(rule.Fallback, value, depth + 1);
            }

            var basic = RenderPredefined(baseName, value, depth + 1);
            return Pad(rule, basic, value);
        }

        // Range check (§3.4 / §6 step "If value is outside style's range").
        if (OutOfRange(rule, value))
        {
            return RenderCore(rule.Fallback, value, depth + 1);
        }

        // Negative values: the sign is applied by Render(); RenderCore works on
        // the absolute value for systems that accept negatives.
        var magnitude = AcceptsNegative(rule.System) ? Math.Abs(value) : value;

        var core = rule.System switch
        {
            CounterSystem.Cyclic => Cyclic(rule.Symbols, value),
            CounterSystem.Fixed => Fixed(rule, value, depth),
            CounterSystem.Symbolic => Symbolic(rule.Symbols, magnitude, rule, value, depth),
            CounterSystem.Alphabetic => Alphabetic(rule.Symbols, magnitude, rule, value, depth),
            CounterSystem.Numeric => Numeric(rule.Symbols, magnitude),
            CounterSystem.Additive => Additive(rule, magnitude, depth),
            _ => value.ToString(CultureInfo.InvariantCulture),
        };
        return Pad(rule, core, value);
    }

    private static bool AcceptsNegative(CounterSystem system)
        => system is CounterSystem.Numeric or CounterSystem.Additive;

    private bool OutOfRange(CounterStyleRule rule, int value)
    {
        // Negative values are out of range for systems that can't represent
        // them (cyclic accepts any; symbolic/alphabetic need value ≥ 1).
        if (rule.HasExplicitRange)
        {
            // §3.4: with multiple comma-separated segments, the value is in
            // range if it falls within ANY segment (open-ended on a null bound).
            if (rule.RangeSegments.Count > 0)
            {
                foreach (var (lo, hi) in rule.RangeSegments)
                {
                    var aboveLow = lo is not { } l || value >= l;
                    var belowHigh = hi is not { } h || value <= h;
                    if (aboveLow && belowHigh)
                    {
                        return false;
                    }
                }
                return true;
            }
            if (rule.RangeLow is { } low && value < low)
            {
                return true;
            }

            if (rule.RangeHigh is { } high && value > high)
            {
                return true;
            }

            return false;
        }
        // Auto range (§3.4): cyclic/numeric/fixed accept all integers;
        // alphabetic/symbolic/additive need value ≥ 1.
        return rule.System switch
        {
            CounterSystem.Alphabetic or CounterSystem.Symbolic => value < 1,
            CounterSystem.Additive => value < 0,
            _ => false,
        };
    }

    private string Pad(CounterStyleRule rule, string core, int value)
    {
        if (rule.PadLength <= 0)
        {
            return core;
        }
        // §3.5: pad counts the negative sign toward the length.
        var signLen = value < 0 ? rule.NegativePrefix.Length + rule.NegativeSuffix.Length : 0;
        var deficit = rule.PadLength - (core.Length + signLen);
        if (deficit <= 0)
        {
            return core;
        }

        var pad = rule.PadSymbol.Length == 0 ? "" : string.Concat(Enumerable.Repeat(rule.PadSymbol, deficit));
        return pad + core;
    }

    // --- System algorithms (§2) ---

    private static string Cyclic(IReadOnlyList<string> symbols, int value)
    {
        // §2.1: repeatedly cycle through the symbols. Index is (value-1) mod n.
        if (symbols.Count == 0)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        var i = ((value - 1) % symbols.Count + symbols.Count) % symbols.Count;
        return symbols[i];
    }

    private string Fixed(CounterStyleRule rule, int value, int depth)
    {
        // §2.2: the first symbol represents FixedFirstValue, the next +1, etc.
        // Values outside the symbol run fall back.
        var index = value - rule.FixedFirstValue;
        if (index < 0 || index >= rule.Symbols.Count)
        {
            return RenderCore(rule.Fallback, value, depth + 1);
        }

        return rule.Symbols[index];
    }

    private string Symbolic(IReadOnlyList<string> symbols, int magnitude, CounterStyleRule rule, int value, int depth)
    {
        // §2.3: symbol = symbols[(value-1) mod n], repeated ceil(value/n) times.
        if (symbols.Count == 0 || value < 1)
        {
            return RenderCore(rule.Fallback, value, depth + 1);
        }

        var n = symbols.Count;
        var reps = (magnitude - 1) / n + 1;
        var symbol = symbols[(magnitude - 1) % n];
        return string.Concat(Enumerable.Repeat(symbol, reps));
    }

    private string Alphabetic(IReadOnlyList<string> symbols, int magnitude, CounterStyleRule rule, int value, int depth)
    {
        // §2.4: bijective base-N over the symbol set.
        if (symbols.Count < 2 || value < 1)
        {
            return RenderCore(rule.Fallback, value, depth + 1);
        }

        var n = symbols.Count;
        var parts = new List<string>();
        var v = magnitude;
        while (v > 0)
        {
            v--;
            parts.Add(symbols[v % n]);
            v /= n;
        }
        parts.Reverse();
        return string.Concat(parts);
    }

    private static string Numeric(IReadOnlyList<string> symbols, int value)
    {
        // §2.5: positional base-N. symbols[0] is the digit for zero.
        if (symbols.Count < 2)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        var n = symbols.Count;
        var v = Math.Abs(value);
        if (v == 0)
        {
            return symbols[0];
        }

        var parts = new List<string>();
        while (v > 0)
        {
            parts.Add(symbols[v % n]);
            v /= n;
        }
        parts.Reverse();
        return string.Concat(parts);
    }

    private string Additive(CounterStyleRule rule, int magnitude, int depth)
    {
        // §2.6: greedily subtract the largest weights, emitting their symbols.
        if (rule.AdditiveSymbols.Count == 0)
        {
            return RenderCore(rule.Fallback, magnitude, depth + 1);
        }

        var v = magnitude;
        if (v == 0)
        {
            // Zero is representable only if a weight-0 symbol exists.
            foreach (var a in rule.AdditiveSymbols)
            {
                if (a.Weight == 0)
                {
                    return a.Symbol;
                }
            }

            return RenderCore(rule.Fallback, magnitude, depth + 1);
        }
        var sb = new StringBuilder();
        foreach (var a in rule.AdditiveSymbols)
        {
            if (a.Weight <= 0)
            {
                continue;
            }

            if (v <= 0)
            {
                break;
            }

            var count = v / a.Weight;
            if (count == 0)
            {
                continue;
            }

            for (var i = 0; i < count; i++)
            {
                sb.Append(a.Symbol);
            }

            v -= count * a.Weight;
        }
        // If we couldn't represent the value exactly, fall back.
        if (v != 0)
        {
            return RenderCore(rule.Fallback, magnitude, depth + 1);
        }

        return sb.ToString();
    }

    // --- Predefined styles (§7) ---

    private string RenderPredefined(string styleName, int value, int depth)
    {
        var name = styleName.Trim().ToLowerInvariant();
        switch (name)
        {
            case "disc":
                return Disc;
            case "circle":
                return Circle;
            case "square":
                return Square;
            case "decimal":
                return value.ToString(CultureInfo.InvariantCulture);
            case "decimal-leading-zero":
                return DecimalLeadingZero(value);
            case "lower-roman":
                return Roman(value, upper: false, depth);
            case "upper-roman":
                return Roman(value, upper: true, depth);
            case "lower-alpha" or "lower-latin":
                return AlphaLetters(value, 'a', depth);
            case "upper-alpha" or "upper-latin":
                return AlphaLetters(value, 'A', depth);
            case "lower-greek":
                return Greek(value, depth);
            default:
                // Unknown predefined name: fall back to decimal (§6).
                return value.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static bool IsGlyph(string styleName) => styleName.Trim().ToLowerInvariant() is "disc" or "circle" or "square";

    private static string PredefinedSuffix(string styleName) => styleName.Trim().ToLowerInvariant() switch
    {
        // §7.2/§7.3: numeric and alphabetic predefined styles all use ". ".
        _ => ". ",
    };

    /// <summary>Unicode bullet for <c>disc</c> (U+2022 BULLET).</summary>
    public const string Disc = "•";

    /// <summary>Unicode bullet for <c>circle</c> (U+25E6 WHITE BULLET).</summary>
    public const string Circle = "◦";

    /// <summary>Unicode bullet for <c>square</c> (U+25AA BLACK SMALL SQUARE).</summary>
    public const string Square = "▪";

    private static string DecimalLeadingZero(int n)
    {
        // §7 decimal-leading-zero: an additive-ish system padded to ≥ 2 digits.
        if (n < 0)
        {
            return n.ToString(CultureInfo.InvariantCulture);
        }

        var s = n.ToString(CultureInfo.InvariantCulture);
        return s.Length < 2 ? "0" + s : s;
    }

    private static readonly (int Value, string Upper, string Lower)[] RomanTable =
    [
        (1000, "M", "m"),
        (900, "CM", "cm"),
        (500, "D", "d"),
        (400, "CD", "cd"),
        (100, "C", "c"),
        (90, "XC", "xc"),
        (50, "L", "l"),
        (40, "XL", "xl"),
        (10, "X", "x"),
        (9, "IX", "ix"),
        (5, "V", "v"),
        (4, "IV", "iv"),
        (1, "I", "i"),
    ];

    private string Roman(int n, bool upper, int depth)
    {
        // §7.2: roman covers 1..3999; outside that range fall back to decimal.
        if (n is < 1 or > 3999)
        {
            return RenderCore("decimal", n, depth + 1);
        }

        var sb = new StringBuilder();
        foreach (var (value, up, low) in RomanTable)
        {
            while (n >= value)
            {
                sb.Append(upper ? up : low);
                n -= value;
            }
        }
        return sb.ToString();
    }

    private string AlphaLetters(int n, char first, int depth)
    {
        // §7.2: bijective base-26. Out of range (n < 1) falls back to decimal.
        if (n < 1)
        {
            return RenderCore("decimal", n, depth + 1);
        }

        var sb = new StringBuilder();
        while (n > 0)
        {
            n--;
            sb.Insert(0, (char)(first + n % 26));
            n /= 26;
        }
        return sb.ToString();
    }

    private const string GreekLetters = "αβγδεζηθικλμνξοπρστυφχψω";

    private string Greek(int n, int depth)
    {
        if (n < 1)
        {
            return RenderCore("decimal", n, depth + 1);
        }

        var sb = new StringBuilder();
        var count = GreekLetters.Length;
        while (n > 0)
        {
            n--;
            sb.Insert(0, GreekLetters[n % count]);
            n /= count;
        }
        return sb.ToString();
    }
}
