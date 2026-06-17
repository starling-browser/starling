using System.Globalization;
using Starling.Css.Parser;
using Starling.Css.Tokenizer;

namespace Starling.Css.Container;

/// <summary>
/// Extracts <see cref="ContainerRule"/> values from parsed stylesheets. The CSS
/// parser recognises <c>@container</c> as an at-rule whose prelude is an optional
/// container name followed by a container condition, and whose body holds the
/// conditionally-applied rules (CSS Containment 3 §5). This separates the
/// optional leading name from the condition text.
/// </summary>
public static class ContainerQueryParser
{
    public static IEnumerable<ContainerRule> ParseAll(StyleSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        foreach (var rule in sheet.Rules)
        {
            if (rule is AtRule { Name: "container" } atRule)
            {
                yield return Parse(atRule);
            }
        }
    }

    public static ContainerRule Parse(AtRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        // The prelude is `<container-name>? <container-condition>`. A leading
        // bare ident that is not a known query keyword is the container name;
        // everything from the first parenthesized block (or `not`/`(`) onward is
        // the condition.
        string? name = null;
        var conditionStart = 0;
        var prelude = rule.Prelude;

        for (var i = 0; i < prelude.Count; i++)
        {
            var c = prelude[i];
            if (c is CssTokenValue { Token.Type: CssTokenType.Whitespace })
            {
                conditionStart = i + 1;
                continue;
            }
            if (c is CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var ident } }
                && !ident.Equals("not", StringComparison.OrdinalIgnoreCase))
            {
                name = ident;
                conditionStart = i + 1;
            }
            break;
        }

        var condition = string.Concat(prelude.Skip(conditionStart).Select(ComponentText)).Trim();
        return new ContainerRule(name, condition, rule.Rules);
    }

    private static string ComponentText(CssComponentValue value) => value switch
    {
        CssTokenValue token => TokenText(token.Token),
        CssFunction function => $"{function.Name}({string.Concat(function.Values.Select(ComponentText))})",
        CssSimpleBlock { StartToken: CssTokenType.LeftParen } block =>
            $"({string.Concat(block.Values.Select(ComponentText))})",
        CssSimpleBlock block => string.Concat(block.Values.Select(ComponentText)),
        _ => string.Empty,
    };

    private static string TokenText(CssToken token) => token.Type switch
    {
        CssTokenType.Ident => token.Value,
        CssTokenType.Colon => ":",
        CssTokenType.Delim => token.Delimiter.ToString(),
        CssTokenType.Whitespace => " ",
        CssTokenType.Number => token.Number.ToString("0.################", CultureInfo.InvariantCulture),
        CssTokenType.Percentage => token.Number.ToString("0.################", CultureInfo.InvariantCulture) + "%",
        CssTokenType.Dimension => token.Number.ToString("0.################", CultureInfo.InvariantCulture) + token.Unit,
        _ => token.Value ?? string.Empty,
    };
}
