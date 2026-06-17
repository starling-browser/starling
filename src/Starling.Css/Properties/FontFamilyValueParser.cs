using Starling.Css.Parser;
using Starling.Css.Tokenizer;
using Starling.Css.Values;

namespace Starling.Css.Properties;

/// <summary>
/// Family-name aware value parser for the <c>font-family</c> property.
/// Differs from <see cref="CssValueParser"/> in two ways:
/// <list type="bullet">
///   <item>Preserves the original case of unquoted ident family names (the
///   generic value parser lowercases all idents, which is wrong for family
///   identifiers like <c>Helvetica</c> or <c>Open</c> followed by <c>Sans</c>).</item>
///   <item>Splits the list on commas at the top level so each comma-separated
///   entry becomes its own <see cref="CssValue"/> (quoted string or
///   space-joined ident sequence).</item>
/// </list>
/// </summary>
internal static class FontFamilyValueParser
{
    public static List<CssValue> Parse(IReadOnlyList<CssComponentValue> values)
    {
        var result = new List<CssValue>();
        var current = new List<CssToken>();
        foreach (var component in values)
        {
            if (component is not CssTokenValue token)
            {
                // Non-token components (functions, blocks) are not valid in
                // font-family; skip them.
                continue;
            }

            switch (token.Token.Type)
            {
                case CssTokenType.Whitespace:
                    current.Add(token.Token);
                    break;
                case CssTokenType.Comma:
                    EmitEntry(current, result);
                    current.Clear();
                    break;
                default:
                    current.Add(token.Token);
                    break;
            }
        }
        EmitEntry(current, result);
        return result;
    }

    private static void EmitEntry(List<CssToken> tokens, List<CssValue> result)
    {
        // Trim surrounding whitespace.
        var start = 0;
        var end = tokens.Count;
        while (start < end && tokens[start].Type == CssTokenType.Whitespace)
        {
            start++;
        }

        while (end > start && tokens[end - 1].Type == CssTokenType.Whitespace)
        {
            end--;
        }

        if (start == end)
        {
            return;
        }

        // A quoted family name is a single CssString token. Anything else is
        // a sequence of idents (possibly separated by whitespace) that we
        // join with a single space — per CSS Fonts 3 §3.1, an unquoted
        // family name is a sequence of identifiers joined by whitespace.
        if (end - start == 1 && tokens[start].Type == CssTokenType.String)
        {
            result.Add(new CssString(tokens[start].Value));
            return;
        }

        var parts = new List<string>();
        for (var i = start; i < end; i++)
        {
            var tok = tokens[i];
            if (tok.Type == CssTokenType.Ident)
            {
                parts.Add(tok.Value);
            }
        }
        if (parts.Count == 0)
        {
            return;
        }

        // Generic family keywords ("serif", "sans-serif", "monospace",
        // "cursive", "fantasy", "system-ui", ...) are case-insensitive and
        // we normalise them so callers can match exactly. Quoted strings
        // bypass this — they're real family names.
        var joined = string.Join(' ', parts);
        var lower = joined.ToLowerInvariant();
        if (IsGenericFamilyKeyword(lower))
        {
            result.Add(new CssKeyword(lower));
        }
        else
        {
            result.Add(new CssKeyword(joined));
        }
    }

    private static bool IsGenericFamilyKeyword(string name) => name switch
    {
        "serif" or "sans-serif" or "monospace" or "cursive" or "fantasy"
            or "system-ui" or "ui-sans-serif" or "ui-serif" or "ui-monospace"
            or "ui-rounded" or "math" or "emoji" or "fangsong" => true,
        _ => false,
    };
}
