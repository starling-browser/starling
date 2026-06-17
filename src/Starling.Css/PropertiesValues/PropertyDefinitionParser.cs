using System.Globalization;
using System.Text;
using Starling.Css.Parser;
using Starling.Css.Tokenizer;

namespace Starling.Css.PropertiesValues;

/// <summary>
/// Extracts <see cref="RegisteredProperty"/> values from parsed stylesheets. The
/// CSS parser recognises <c>@property</c> as an at-rule with a declaration list
/// (its descriptors); this turns those descriptors into a strongly-typed model.
/// Per CSS Properties and Values API 1 §2: a rule is valid only when it has a
/// <c>--</c>-prefixed name, a <c>syntax</c> descriptor, an <c>inherits</c>
/// descriptor, and — unless the syntax is the universal <c>*</c> — an
/// <c>initial-value</c>. Invalid rules are dropped (fail-soft).
/// </summary>
public static class PropertyDefinitionParser
{
    public static IEnumerable<RegisteredProperty> ParseAll(StyleSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        foreach (var rule in sheet.Rules)
        {
            if (rule is AtRule { Name: "property" } atRule && TryParse(atRule, out var registered))
            {
                yield return registered!;
            }
        }
    }

    public static bool TryParse(AtRule rule, out RegisteredProperty? registered)
    {
        ArgumentNullException.ThrowIfNull(rule);
        registered = null;
        if (!string.Equals(rule.Name, "property", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var name = ExtractName(rule.Prelude);
        if (name is null)
        {
            return false;
        }

        string? syntax = null;
        bool? inherits = null;
        string? initialValue = null;

        foreach (var decl in rule.Declarations)
        {
            switch (decl.Name.ToLowerInvariant())
            {
                case "syntax":
                    syntax = ExtractString(decl.Value);
                    break;
                case "inherits":
                    inherits = ExtractBool(decl.Value);
                    break;
                case "initial-value":
                    initialValue = Serialize(decl.Value);
                    break;
            }
        }

        // syntax and inherits are required descriptors.
        if (syntax is null || inherits is null)
        {
            return false;
        }

        var isUniversal = syntax.Trim() == "*";
        // initial-value is required for any non-universal syntax.
        if (!isUniversal && string.IsNullOrEmpty(initialValue))
        {
            return false;
        }

        registered = new RegisteredProperty(name, syntax, inherits.Value, initialValue);
        return true;
    }

    private static string? ExtractName(IReadOnlyList<CssComponentValue> prelude)
    {
        foreach (var component in prelude)
        {
            if (component is CssTokenValue { Token.Type: CssTokenType.Whitespace })
            {
                continue;
            }

            if (component is CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var ident } }
                && ident.StartsWith("--", StringComparison.Ordinal))
            {
                return ident;
            }

            return null;
        }
        return null;
    }

    private static string? ExtractString(IReadOnlyList<CssComponentValue> value)
    {
        foreach (var v in value)
        {
            if (v is CssTokenValue { Token: { Type: CssTokenType.String, Value: var s } })
            {
                return s;
            }
        }

        return null;
    }

    private static bool? ExtractBool(IReadOnlyList<CssComponentValue> value)
    {
        foreach (var v in value)
        {
            if (v is CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var ident } })
            {
                if (ident.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (ident.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
        }

        return null;
    }

    private static string Serialize(IReadOnlyList<CssComponentValue> value)
    {
        var sb = new StringBuilder();
        foreach (var v in value)
        {
            sb.Append(ComponentText(v));
        }

        return sb.ToString().Trim();
    }

    private static string ComponentText(CssComponentValue value) => value switch
    {
        CssTokenValue token => TokenText(token.Token),
        CssFunction function => $"{function.Name}({string.Concat(function.Values.Select(ComponentText))})",
        _ => string.Empty,
    };

    private static string TokenText(CssToken token) => token.Type switch
    {
        CssTokenType.Ident => token.Value,
        CssTokenType.Number => Num(token.Number),
        CssTokenType.Percentage => Num(token.Number) + "%",
        CssTokenType.Dimension => Num(token.Number) + token.Unit,
        CssTokenType.String => "\"" + token.Value + "\"",
        CssTokenType.Hash => "#" + token.Value,
        CssTokenType.Whitespace => " ",
        CssTokenType.Comma => ",",
        _ => token.Value ?? string.Empty,
    };

    private static string Num(double n)
        => n.ToString("0.################", CultureInfo.InvariantCulture);
}
