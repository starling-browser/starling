using Starling.Css.Parser;
using Starling.Css.Properties;
using Starling.Css.Selectors;
using Starling.Css.Tokenizer;
using Starling.Css.Values;

namespace Starling.Css.Cascade;

// CSS Conditional 5 §3: evaluates `@supports` preludes.
public static class SupportsEvaluator
{
    public static bool Evaluate(IReadOnlyList<CssComponentValue> prelude)
    {
        ArgumentNullException.ThrowIfNull(prelude);
        var tokens = StripWhitespace(prelude);
        var pos = 0;
        var result = ParseCondition(tokens, ref pos);
        return result;
    }

    private static List<CssComponentValue> StripWhitespace(IReadOnlyList<CssComponentValue> values)
    {
        var list = new List<CssComponentValue>();
        foreach (var v in values)
        {
            if (v is not CssTokenValue { Token.Type: CssTokenType.Whitespace })
            {
                list.Add(v);
            }
        }

        return list;
    }

    private static bool ParseCondition(List<CssComponentValue> tokens, ref int pos)
    {
        if (pos >= tokens.Count)
        {
            return false;
        }
        // not <in-parens>
        if (IsIdent(tokens[pos], "not"))
        {
            pos++;
            return !ParseInParens(tokens, ref pos);
        }
        var first = ParseInParens(tokens, ref pos);

        // and / or chain
        if (pos < tokens.Count && IsIdent(tokens[pos], "and"))
        {
            var all = first;
            while (pos < tokens.Count && IsIdent(tokens[pos], "and"))
            {
                pos++;
                all = all && ParseInParens(tokens, ref pos);
            }
            return all;
        }
        if (pos < tokens.Count && IsIdent(tokens[pos], "or"))
        {
            var any = first;
            while (pos < tokens.Count && IsIdent(tokens[pos], "or"))
            {
                pos++;
                any = any || ParseInParens(tokens, ref pos);
            }
            return any;
        }
        return first;
    }

    private static bool ParseInParens(List<CssComponentValue> tokens, ref int pos)
    {
        if (pos >= tokens.Count)
        {
            return false;
        }

        var node = tokens[pos];
        if (node is CssSimpleBlock { StartToken: CssTokenType.LeftParen } block)
        {
            pos++;
            return EvaluateParens(block.Values);
        }
        if (node is CssFunction fn)
        {
            pos++;
            return EvaluateFunction(fn);
        }
        // unrecognized — skip and return false
        pos++;
        return false;
    }

    private static bool EvaluateParens(IReadOnlyList<CssComponentValue> values)
    {
        // The interior may be (a) a nested condition starting with `not` / `(` / func, or (b) a declaration.
        var stripped = StripWhitespace(values);
        if (stripped.Count == 0)
        {
            return false;
        }

        if (IsIdent(stripped[0], "not") ||
            stripped[0] is CssSimpleBlock { StartToken: CssTokenType.LeftParen } ||
            stripped[0] is CssFunction)
        {
            var pos = 0;
            return ParseCondition(stripped, ref pos);
        }
        return EvaluateDeclaration(stripped);
    }

    private static bool EvaluateDeclaration(List<CssComponentValue> tokens)
    {
        // Expect ident ':' value+
        if (tokens.Count < 2)
        {
            return false;
        }

        if (tokens[0] is not CssTokenValue { Token.Type: CssTokenType.Ident } nameTok)
        {
            return false;
        }

        if (tokens[1] is not CssTokenValue { Token.Type: CssTokenType.Colon })
        {
            return false;
        }

        var name = nameTok.Token.Value;
        var valueTokens = new List<CssComponentValue>();
        for (var i = 2; i < tokens.Count; i++)
        {
            valueTokens.Add(tokens[i]);
        }

        // Custom properties are always supported.
        if (name.StartsWith("--", StringComparison.Ordinal))
        {
            return true;
        }

        // Try the property registry — yields longhands for shorthands like `margin`.
        // CSS Conditional 5 §2.2: the declaration is "supported" only when the
        // value is actually valid for the property, not merely tokenizable.
        try
        {
            var decl = new CssDeclaration(name, valueTokens, false);
            var parsed = PropertyRegistry.Parse(decl).ToList();
            return parsed.Count > 0 && parsed.All(IsSupportedValue);
        }
        catch
        {
            return false;
        }
    }

    // CSS Conditional 5 §2.2: a declaration is supported only when its value is
    // valid for the property. Permissive by default (via the registry's
    // longhand validator); `color` is additionally checked here — and only here,
    // not on the normal parse path — so that dynamic forms like attr()/color-mix()
    // still parse normally while `@supports (color: <garbage>)` reports false.
    private static bool IsSupportedValue(PropertyDeclaration d)
    {
        if (d.Id == PropertyId.Color)
        {
            return d.Value is CssColor
                || d.Value is CssKeyword
                {
                    Name: "currentcolor" or "transparent"
                        or "inherit" or "initial" or "unset" or "revert" or "revert-layer"
                };
        }

        return PropertyRegistry.IsValidLonghandValue(d.Id, d.Value);
    }

    private static bool EvaluateFunction(CssFunction fn)
    {
        var name = fn.Name.ToLowerInvariant();
        switch (name)
        {
            case "selector":
                return EvaluateSelectorTest(fn.Values);
            case "font-tech":
                // Recognize syntax; v1 returns false for any tech.
                return false;
            case "font-format":
                return false;
            default:
                return false;
        }
    }

    private static bool EvaluateSelectorTest(IReadOnlyList<CssComponentValue> values)
    {
        // The nesting selector `&` is only valid inside a style rule; in a
        // standalone `selector()` test it is not a valid selector, so reject it.
        if (values.Any(v => v is CssTokenValue { Token: { Type: CssTokenType.Delim, Delimiter: '&' } }))
        {
            return false;
        }

        try
        {
            var list = SelectorParser.ParseSelectorList(values);
            return list.Selectors.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsIdent(CssComponentValue value, string name)
        => value is CssTokenValue { Token.Type: CssTokenType.Ident } tok &&
           tok.Token.Value.Equals(name, StringComparison.OrdinalIgnoreCase);
}
