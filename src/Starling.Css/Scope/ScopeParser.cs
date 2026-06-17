using System.Globalization;
using Starling.Css.Parser;
using Starling.Css.Tokenizer;

namespace Starling.Css.Scope;

/// <summary>
/// Extracts <see cref="ScopeRule"/> values from parsed stylesheets. The CSS
/// parser recognises <c>@scope</c> as an at-rule whose prelude is
/// <c>(&lt;scope-start&gt;) [ to (&lt;scope-end&gt;) ]?</c> and whose body holds
/// scoped style rules (CSS Cascade 6 §3). This turns the prelude into the two
/// selector bounds.
/// </summary>
public static class ScopeParser
{
    public static IEnumerable<ScopeRule> ParseAll(StyleSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        foreach (var rule in sheet.Rules)
        {
            if (rule is AtRule { Name: "scope" } atRule)
            {
                yield return Parse(atRule);
            }
        }
    }

    public static ScopeRule Parse(AtRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        // The prelude is a sequence: optional (start) block, optional `to`
        // ident, optional (end) block. Collect the parenthesized blocks in
        // order; the first is scope-start, a second (after `to`) is scope-end.
        string? start = null;
        string? end = null;
        var blocksSeen = 0;
        foreach (var component in rule.Prelude)
        {
            if (component is CssSimpleBlock { StartToken: CssTokenType.LeftParen } block)
            {
                var text = SelectorText(block.Values);
                if (blocksSeen == 0)
                {
                    start = text;
                }
                else
                {
                    end = text;
                }

                blocksSeen++;
            }
        }

        return new ScopeRule(start, end, rule.Rules);
    }

    private static string SelectorText(IReadOnlyList<CssComponentValue> values)
        => string.Concat(values.Select(ComponentText)).Trim();

    private static string ComponentText(CssComponentValue value) => value switch
    {
        CssTokenValue token => TokenText(token.Token),
        CssFunction function => $"{function.Name}({string.Concat(function.Values.Select(ComponentText))})",
        CssSimpleBlock block => $"[{string.Concat(block.Values.Select(ComponentText))}]",
        _ => string.Empty,
    };

    private static string TokenText(CssToken token) => token.Type switch
    {
        CssTokenType.Ident => token.Value,
        CssTokenType.Hash => "#" + token.Value,
        CssTokenType.Delim => token.Delimiter.ToString(),
        CssTokenType.Colon => ":",
        CssTokenType.Comma => ",",
        CssTokenType.Whitespace => " ",
        CssTokenType.String => "\"" + token.Value + "\"",
        CssTokenType.Number => token.Number.ToString("0.################", CultureInfo.InvariantCulture),
        _ => token.Value ?? string.Empty,
    };
}
