using System.Globalization;
using Starling.Css.Parser;
using Starling.Css.Properties;
using Starling.Css.Values;

namespace Starling.Css.Cascade;

public sealed class ComputedStyle
{
    private readonly IReadOnlyDictionary<PropertyId, CssValue> _values;

    internal ComputedStyle(
        IReadOnlyDictionary<PropertyId, CssValue> values,
        IReadOnlyDictionary<string, IReadOnlyList<CssComponentValue>> customProperties)
    {
        _values = values;
        CustomProperties = customProperties;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<CssComponentValue>> CustomProperties { get; }

    public CssValue Get(PropertyId property) => _values[property];

    /// <summary>
    /// Value-equality over the resolved property map — true when every property
    /// resolves to an equal <see cref="CssValue"/> (records compare structurally).
    /// Incremental layout uses this to decide whether a re-cascaded element is
    /// unchanged and its laid-out subtree can be reused. Custom properties feed
    /// <c>var()</c> which is already resolved into the property map at compute
    /// time, so they need not be compared separately for layout/paint equality.
    /// </summary>
    public bool ValuesEqual(ComputedStyle? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        if (other is null || _values.Count != other._values.Count)
        {
            return false;
        }

        foreach (var (id, value) in _values)
        {
            if (!other._values.TryGetValue(id, out var v) || !ValueEquals(value, v))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Deep value equality for <see cref="CssValue"/>. Record equality is value-
    /// based for scalar values, but a record's synthesized <c>Equals</c> compares
    /// collection members (e.g. <see cref="CssValueList"/>'s <c>Values</c>) by
    /// reference — so two equal <c>font-family: a, b</c> values would compare
    /// unequal. We recurse through the collection-bearing value kinds. Anything
    /// not handled here falls back to record equality, which is conservative
    /// (a false "unequal" only forces a rebuild, never a stale reuse).
    /// </summary>
    private static bool ValueEquals(CssValue a, CssValue b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        switch (a, b)
        {
            case (CssValueList la, CssValueList lb):
                if (la.Values.Count != lb.Values.Count)
                {
                    return false;
                }

                for (var i = 0; i < la.Values.Count; i++)
                {
                    if (!ValueEquals(la.Values[i], lb.Values[i]))
                    {
                        return false;
                    }
                }

                return true;
            case (CssFunctionValue fa, CssFunctionValue fb):
                if (!string.Equals(fa.Name, fb.Name, StringComparison.Ordinal)
                    || fa.Arguments.Count != fb.Arguments.Count)
                {
                    return false;
                }

                for (var i = 0; i < fa.Arguments.Count; i++)
                {
                    if (!ValueEquals(fa.Arguments[i], fb.Arguments[i]))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return a.Equals(b);
        }
    }

    public bool TryGet(PropertyId property, out CssValue value)
    {
        if (_values.TryGetValue(property, out var v))
        {
            value = v;
            return true;
        }
        value = default!;
        return false;
    }

    /// <summary>
    /// Returns a new <see cref="ComputedStyle"/> with the given property values
    /// overlaid on top of this one. Used by the animation compositor
    /// (CSS Animations 1 §3.2) to layer in-flight animation + transition
    /// samples over the static cascade. Custom properties are unchanged.
    /// </summary>
    internal ComputedStyle WithOverrides(IReadOnlyDictionary<PropertyId, CssValue> overrides)
    {
        if (overrides.Count == 0)
        {
            return this;
        }

        var merged = new Dictionary<PropertyId, CssValue>(_values.Count);
        foreach (var kv in _values)
        {
            merged[kv.Key] = kv.Value;
        }

        foreach (var kv in overrides)
        {
            merged[kv.Key] = kv.Value;
        }

        return new ComputedStyle(merged, CustomProperties);
    }

    /// <summary>
    /// Builds the computed style for an anonymous box generated inside the
    /// element this style belongs to. Per CSS 2.1 §9.2.1.1 (and CSS Flexbox 1
    /// §4): an anonymous box inherits the <em>inherited</em> properties from its
    /// parent and takes the <em>initial</em> value for every non-inherited
    /// property. Copying the parent style wholesale instead would leak the
    /// parent's <c>width</c>/<c>flex-*</c>/<c>background</c>/box-model onto the
    /// wrapper — e.g. an anonymous flex item picking up a container's
    /// <c>width:100%</c> as its flex-basis and ballooning, shoving its siblings
    /// aside. Text-affecting properties (font, color, white-space, …) survive so
    /// the wrapped inline run still renders with the parent's typography.
    /// </summary>
    public ComputedStyle ForAnonymousChild()
    {
        var values = new Dictionary<PropertyId, CssValue>(_values.Count);
        foreach (var kv in _values)
        {
            values[kv.Key] = PropertyRegistry.Inherits(kv.Key)
                ? kv.Value
                : PropertyRegistry.InitialValue(kv.Key);
        }

        return new ComputedStyle(values, CustomProperties);
    }

    /// <summary>Layout-time used-value resolution. Resolves any remaining
    /// percentages or symbolic units (e.g. percentages, container units when
    /// a container basis is supplied) using <paramref name="ctx"/>.</summary>
    public CssValue UsedValue(PropertyId property, CssResolutionContext ctx)
        => CssCalcResolver.Resolve(Get(property), ctx);

    /// <summary>Resolve a property's value to a px length given a containing-block
    /// basis in pixels. Returns 0 if the value is not length-typed.</summary>
    public double UsedLengthPx(PropertyId property, double containingBlockPx, CssResolutionContext baseCtx)
    {
        var ctx = baseCtx with { PercentageBasisPx = containingBlockPx };
        return UsedValue(property, ctx) switch
        {
            CssLength { Unit: CssLengthUnit.Px } len => len.Value,
            CssNumber n => n.Value,
            _ => 0,
        };
    }

    public CssLength GetLength(PropertyId property)
        => Get(property) as CssLength ?? CssLength.Zero;

    public CssColor GetColor(PropertyId property)
        => Get(property) as CssColor ?? CssColor.Black;

    public string GetPropertyValue(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        // Custom properties (--foo) are stored in a parallel dictionary;
        // serialize their component-value list back to text per CSSOM §6.7.4.
        if (name.StartsWith("--", StringComparison.Ordinal))
        {
            if (!CustomProperties.TryGetValue(name, out var tokens))
            {
                return string.Empty;
            }
            // Substitute any var() references, then serialize with comment-insertion
            // for consecutive tokens that would otherwise re-tokenize differently.
            var flat = SubstituteVars(tokens, CustomProperties, depth: 0);
            return SerializeWithComments(flat).Trim();
        }
        return PropertyRegistry.TryGetPropertyId(name, out var property)
            ? ToCssText(Get(property))
            : string.Empty;
    }

    // -----------------------------------------------------------------
    // Custom-property var() substitution + serialization
    // CSS Custom Properties §3.3 + CSS Syntax §8.1
    // -----------------------------------------------------------------

    /// <summary>Flatten a component-value list by substituting var() references.
    /// Returns a flat list of leaf tokens (no functions / blocks for now;
    /// blocks are preserved as block tokens). Depth-limits at 8 to avoid cycles.</summary>
    private static IReadOnlyList<Starling.Css.Tokenizer.CssToken> SubstituteVars(
        IReadOnlyList<CssComponentValue> values,
        IReadOnlyDictionary<string, IReadOnlyList<CssComponentValue>> customProps,
        int depth)
    {
        if (depth > 8)
        {
            return Array.Empty<Starling.Css.Tokenizer.CssToken>();
        }

        var result = new List<Starling.Css.Tokenizer.CssToken>();
        foreach (var cv in values)
        {
            switch (cv)
            {
                case CssTokenValue tv:
                    result.Add(tv.Token);
                    break;
                case CssFunction func when func.Name.Equals("var", StringComparison.OrdinalIgnoreCase):
                    {
                        // Resolve the var(): first positional argument is the custom property name.
                        var varName = ExtractVarName(func.Values);
                        if (varName is not null && customProps.TryGetValue(varName, out var replacement))
                        {
                            result.AddRange(SubstituteVars(replacement, customProps, depth + 1));
                        }
                        else
                        {
                            // Try the fallback (everything after the first comma).
                            var fallback = ExtractVarFallback(func.Values);
                            if (fallback is not null)
                            {
                                result.AddRange(SubstituteVars(fallback, customProps, depth + 1));
                            }
                            // If neither, substitute with nothing (guaranteed-invalid token).
                        }
                        break;
                    }
                case CssFunction func:
                    {
                        // Non-var function: serialize as function token + inner content + ')'.
                        result.Add(new Starling.Css.Tokenizer.CssToken(Starling.Css.Tokenizer.CssTokenType.Function, func.Name));
                        result.AddRange(SubstituteVars(func.Values, customProps, depth + 1));
                        result.Add(new Starling.Css.Tokenizer.CssToken(Starling.Css.Tokenizer.CssTokenType.RightParen));
                        break;
                    }
                case CssSimpleBlock block:
                    {
                        result.Add(new Starling.Css.Tokenizer.CssToken(block.StartToken));
                        result.AddRange(SubstituteVars(block.Values, customProps, depth + 1));
                        result.Add(new Starling.Css.Tokenizer.CssToken(
                            block.StartToken == Starling.Css.Tokenizer.CssTokenType.LeftParen ? Starling.Css.Tokenizer.CssTokenType.RightParen :
                            block.StartToken == Starling.Css.Tokenizer.CssTokenType.LeftSquare ? Starling.Css.Tokenizer.CssTokenType.RightSquare :
                            Starling.Css.Tokenizer.CssTokenType.RightBrace));
                        break;
                    }
            }
        }
        return result;
    }

    private static string? ExtractVarName(IReadOnlyList<CssComponentValue> args)
    {
        // First non-whitespace token should be the custom property ident.
        foreach (var cv in args)
        {
            if (cv is CssTokenValue { Token.Type: Starling.Css.Tokenizer.CssTokenType.Whitespace })
            {
                continue;
            }

            if (cv is CssTokenValue tv && tv.Token.Type == Starling.Css.Tokenizer.CssTokenType.Ident
                && tv.Token.Value.StartsWith("--", StringComparison.Ordinal))
            {
                return tv.Token.Value;
            }

            return null;
        }
        return null;
    }

    private static List<CssComponentValue>? ExtractVarFallback(IReadOnlyList<CssComponentValue> args)
    {
        // Everything after the first comma is the fallback.
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] is CssTokenValue { Token.Type: Starling.Css.Tokenizer.CssTokenType.Comma })
            {
                return args.Skip(i + 1).ToList();
            }
        }
        return null;
    }

    /// <summary>Serialize a flat token list, inserting <c>/**/</c> between
    /// consecutive tokens that would re-tokenize differently (CSS Syntax §8.1
    /// serialization table). Skips whitespace tokens from the flat list since
    /// the surrounding context manages spacing.</summary>
    private static string SerializeWithComments(IReadOnlyList<Starling.Css.Tokenizer.CssToken> tokens)
    {
        var sb = new System.Text.StringBuilder();
        Starling.Css.Tokenizer.CssToken? prev = null;
        foreach (var t in tokens)
        {
            // Skip whitespace — we re-insert via comment or space.
            if (t.Type == Starling.Css.Tokenizer.CssTokenType.Whitespace)
            {
                // Mark that a space was seen so we can insert it if no comment needed.
                if (prev.HasValue) { sb.Append(' '); prev = null; }
                continue;
            }
            if (prev.HasValue && NeedsComment(prev.Value, t))
            {
                sb.Append("/**/");
            }

            sb.Append(TokenToText(t));
            prev = t;
        }
        return sb.ToString();
    }

    /// <summary>CSS Syntax §8.1 serialization table: returns true if a comment
    /// must be inserted between <paramref name="a"/> and <paramref name="b"/>
    /// to prevent them from re-tokenizing as a single token.</summary>
    private static bool NeedsComment(
        Starling.Css.Tokenizer.CssToken a,
        Starling.Css.Tokenizer.CssToken b)
    {
        var aType = a.Type;
        var bType = b.Type;

        // ident / at-keyword / hash / dimension start with a name code point.
        var aIsIdent = aType is
            Starling.Css.Tokenizer.CssTokenType.Ident or
            Starling.Css.Tokenizer.CssTokenType.AtKeyword or
            Starling.Css.Tokenizer.CssTokenType.Hash or
            Starling.Css.Tokenizer.CssTokenType.Dimension;
        var bIsIdentStart = bType is
            Starling.Css.Tokenizer.CssTokenType.Ident or
            Starling.Css.Tokenizer.CssTokenType.Function or
            Starling.Css.Tokenizer.CssTokenType.Url or
            Starling.Css.Tokenizer.CssTokenType.Dimension;

        // ident followed by: ident, function, url, dimension, number, percentage,
        // Cdc (-->), or '(' or '-'.
        if (aIsIdent && bIsIdentStart)
        {
            return true;
        }

        if (aIsIdent && bType is
            Starling.Css.Tokenizer.CssTokenType.Number or
            Starling.Css.Tokenizer.CssTokenType.Percentage or
            Starling.Css.Tokenizer.CssTokenType.Cdc)
        {
            return true;
        }

        if (aIsIdent && bType == Starling.Css.Tokenizer.CssTokenType.Delim && (b.Delimiter == '-' || b.Delimiter == '('))
        {
            return true;
        }

        if (aIsIdent && bType == Starling.Css.Tokenizer.CssTokenType.LeftParen)
        {
            return true;
        }

        // number / percentage followed by: ident, function, url, %, dimension,
        // number, '('.
        var aIsNum = aType is
            Starling.Css.Tokenizer.CssTokenType.Number or
            Starling.Css.Tokenizer.CssTokenType.Percentage or
            Starling.Css.Tokenizer.CssTokenType.Dimension;
        if (aIsNum && bIsIdentStart)
        {
            return true;
        }

        if (aIsNum && bType is
            Starling.Css.Tokenizer.CssTokenType.Number or
            Starling.Css.Tokenizer.CssTokenType.Percentage)
        {
            return true;
        }

        if (aIsNum && bType == Starling.Css.Tokenizer.CssTokenType.Delim && b.Delimiter == '%')
        {
            return true;
        }

        if (aIsNum && bType == Starling.Css.Tokenizer.CssTokenType.LeftParen)
        {
            return true;
        }

        // hash / delim('#') followed by ident-start tokens.
        if (aType == Starling.Css.Tokenizer.CssTokenType.Hash && bIsIdentStart)
        {
            return true;
        }

        if (aType == Starling.Css.Tokenizer.CssTokenType.Hash && bType is
            Starling.Css.Tokenizer.CssTokenType.Number or
            Starling.Css.Tokenizer.CssTokenType.Percentage or
            Starling.Css.Tokenizer.CssTokenType.Dimension or
            Starling.Css.Tokenizer.CssTokenType.Cdc)
        {
            return true;
        }

        if (aType == Starling.Css.Tokenizer.CssTokenType.Hash && bType == Starling.Css.Tokenizer.CssTokenType.Delim && b.Delimiter == '-')
        {
            return true;
        }

        // '#' delim followed by ident.
        if (aType == Starling.Css.Tokenizer.CssTokenType.Delim && a.Delimiter == '#' && bIsIdentStart)
        {
            return true;
        }

        if (aType == Starling.Css.Tokenizer.CssTokenType.Delim && a.Delimiter == '#' && bType is
            Starling.Css.Tokenizer.CssTokenType.Number or
            Starling.Css.Tokenizer.CssTokenType.Percentage or
            Starling.Css.Tokenizer.CssTokenType.Dimension or
            Starling.Css.Tokenizer.CssTokenType.Cdc)
        {
            return true;
        }

        if (aType == Starling.Css.Tokenizer.CssTokenType.Delim && a.Delimiter == '#' && bType == Starling.Css.Tokenizer.CssTokenType.Delim && b.Delimiter == '-')
        {
            return true;
        }

        // '-' delim followed by: ident, function, url, number, percentage, dimension.
        if (aType == Starling.Css.Tokenizer.CssTokenType.Delim && a.Delimiter == '-')
        {
            if (bIsIdentStart || bType is
                Starling.Css.Tokenizer.CssTokenType.Number or
                Starling.Css.Tokenizer.CssTokenType.Percentage or
                Starling.Css.Tokenizer.CssTokenType.Dimension)
            {
                return true;
            }

            if (bType == Starling.Css.Tokenizer.CssTokenType.Delim && b.Delimiter == '-')
            {
                return true;
            }
        }

        // '@' delim followed by ident.
        if (aType == Starling.Css.Tokenizer.CssTokenType.Delim && a.Delimiter == '@')
        {
            if (bIsIdentStart || bType == Starling.Css.Tokenizer.CssTokenType.Delim && b.Delimiter == '-')
            {
                return true;
            }
        }

        // '.' delim followed by number/percentage/dimension.
        if (aType == Starling.Css.Tokenizer.CssTokenType.Delim && a.Delimiter == '.')
        {
            if (bType is Starling.Css.Tokenizer.CssTokenType.Number or
                Starling.Css.Tokenizer.CssTokenType.Percentage or
                Starling.Css.Tokenizer.CssTokenType.Dimension)
            {
                return true;
            }
        }

        // '+' delim followed by number/percentage/dimension — "+123" is a signed number.
        if (aType == Starling.Css.Tokenizer.CssTokenType.Delim && a.Delimiter == '+')
        {
            if (bType is Starling.Css.Tokenizer.CssTokenType.Number or
                Starling.Css.Tokenizer.CssTokenType.Percentage or
                Starling.Css.Tokenizer.CssTokenType.Dimension)
            {
                return true;
            }
        }

        // '/' delim followed by '*' delim — "/*" starts a CSS block comment.
        if (aType == Starling.Css.Tokenizer.CssTokenType.Delim && a.Delimiter == '/' &&
            bType == Starling.Css.Tokenizer.CssTokenType.Delim && b.Delimiter == '*')
        {
            return true;
        }

        return false;
    }

    private static string ComponentValueToText(CssComponentValue value) => value switch
    {
        CssTokenValue token => TokenToText(token.Token),
        CssFunction func => func.Name + "(" + string.Concat(func.Values.Select(ComponentValueToText)) + ")",
        CssSimpleBlock block => BlockStart(block.StartToken) + string.Concat(block.Values.Select(ComponentValueToText)) + BlockEnd(block.StartToken),
        _ => string.Empty,
    };

    private static string TokenToText(Starling.Css.Tokenizer.CssToken t) => t.Type switch
    {
        Starling.Css.Tokenizer.CssTokenType.Ident => t.Value,
        Starling.Css.Tokenizer.CssTokenType.AtKeyword => "@" + t.Value,
        // Prefer single-quote serialization when the string contains a double
        // quote (avoids backslash escaping and matches what the author likely wrote).
        Starling.Css.Tokenizer.CssTokenType.String when t.Value.Contains('"') =>
            "'" + t.Value.Replace("\\", "\\\\").Replace("'", "\\'") + "'",
        Starling.Css.Tokenizer.CssTokenType.String => "\"" + t.Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        Starling.Css.Tokenizer.CssTokenType.Hash => "#" + t.Value,
        Starling.Css.Tokenizer.CssTokenType.Number => SerializeNum(t.Number),
        Starling.Css.Tokenizer.CssTokenType.Percentage => SerializeNum(t.Number) + "%",
        Starling.Css.Tokenizer.CssTokenType.Dimension => SerializeNum(t.Number) + t.Unit,
        Starling.Css.Tokenizer.CssTokenType.Delim => t.Delimiter.ToString(),
        Starling.Css.Tokenizer.CssTokenType.Whitespace => " ",
        Starling.Css.Tokenizer.CssTokenType.Colon => ":",
        Starling.Css.Tokenizer.CssTokenType.Comma => ",",
        Starling.Css.Tokenizer.CssTokenType.Semicolon => ";",
        Starling.Css.Tokenizer.CssTokenType.LeftParen => "(",
        Starling.Css.Tokenizer.CssTokenType.RightParen => ")",
        Starling.Css.Tokenizer.CssTokenType.LeftSquare => "[",
        Starling.Css.Tokenizer.CssTokenType.RightSquare => "]",
        Starling.Css.Tokenizer.CssTokenType.LeftBrace => "{",
        Starling.Css.Tokenizer.CssTokenType.RightBrace => "}",
        Starling.Css.Tokenizer.CssTokenType.Url => "url(" + t.Value + ")",
        Starling.Css.Tokenizer.CssTokenType.Function => t.Value + "(",
        Starling.Css.Tokenizer.CssTokenType.Cdc => "-->",
        Starling.Css.Tokenizer.CssTokenType.Cdo => "<!--",
        _ => t.Value,
    };

    // Number serialization per CSS Syntax 3 §8.2 (shortest form, no trailing
    // zeros, leading 0 for fractions). Mirrors CssValueSerializer.SerializeNumber
    // without creating a cross-assembly dependency.
    private static string SerializeNum(double n)
    {
        if (n == 0)
        {
            return "0";
        }

        var s = n.ToString("R", CultureInfo.InvariantCulture);
        if (s.Contains('E') || s.Contains('e'))
        {
            s = n.ToString("0.################", CultureInfo.InvariantCulture);
        }

        return s;
    }

    private static string BlockStart(Starling.Css.Tokenizer.CssTokenType type) => type switch
    {
        Starling.Css.Tokenizer.CssTokenType.LeftParen => "(",
        Starling.Css.Tokenizer.CssTokenType.LeftSquare => "[",
        Starling.Css.Tokenizer.CssTokenType.LeftBrace => "{",
        _ => string.Empty,
    };

    private static string BlockEnd(Starling.Css.Tokenizer.CssTokenType type) => type switch
    {
        Starling.Css.Tokenizer.CssTokenType.LeftParen => ")",
        Starling.Css.Tokenizer.CssTokenType.LeftSquare => "]",
        Starling.Css.Tokenizer.CssTokenType.LeftBrace => "}",
        _ => string.Empty,
    };

    public static string ToCssText(CssValue value)
        => value switch
        {
            CssKeyword keyword => keyword.Name,
            CssNumber number => number.Value.ToString(CultureInfo.InvariantCulture),
            CssPercentage percentage => percentage.Value.ToString(CultureInfo.InvariantCulture) + "%",
            CssLength length => length.ToString(),
            CssDimension dimension => dimension.Value.ToString(CultureInfo.InvariantCulture) + dimension.Unit,
            CssColor color => color.ToString(),
            CssString text => text.Value,
            CssUrl url => $"url({url.Value})",
            CssValueList list => string.Join(" ", list.Values.Select(ToCssText)),
            CssFunctionValue function => $"{function.Name}({string.Join(", ", function.Arguments.Select(ToCssText))})",
            CssVarReference var => var.Fallback is null
                ? $"var({var.Name})"
                : $"var({var.Name}, {ToCssText(var.Fallback)})",
            CssPendingSubstitution pending =>
                $"{pending.Shorthand}({string.Join(" ", pending.Values.Select(ToCssText))})",
            _ => value.ToString() ?? string.Empty,
        };
}
