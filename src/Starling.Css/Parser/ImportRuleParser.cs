using Starling.Css.Media;
using Starling.Css.Tokenizer;

namespace Starling.Css.Parser;

// CSS Cascade 5 §4: `@import <url> [layer(<name>)?] [supports(<condition>)?] <media-query-list>?;`
public sealed record ImportRule(
    string Url,
    string? LayerName,
    IReadOnlyList<CssComponentValue>? SupportsCondition,
    MediaQueryList MediaQueryList);

public static class ImportRuleParser
{
    public static bool TryParse(AtRule atRule, out ImportRule importRule)
    {
        importRule = null!;
        if (!atRule.Name.Equals("import", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prelude = atRule.Prelude;
        var pos = 0;
        SkipWs(prelude, ref pos);
        if (pos >= prelude.Count)
        {
            return false;
        }

        string? url = null;
        if (prelude[pos] is CssTokenValue { Token.Type: CssTokenType.Url } urlTok)
        {
            url = urlTok.Token.Value;
            pos++;
        }
        else if (prelude[pos] is CssTokenValue { Token.Type: CssTokenType.String } strTok)
        {
            url = strTok.Token.Value;
            pos++;
        }
        else if (prelude[pos] is CssFunction { Name: var fname, Values: var fvals } && fname.Equals("url", StringComparison.OrdinalIgnoreCase))
        {
            url = ExtractFirstString(fvals);
            pos++;
        }
        if (url is null)
        {
            return false;
        }

        SkipWs(prelude, ref pos);
        string? layerName = null;
        // `layer` as ident → anonymous layer; `layer(name)` as function → named layer.
        if (pos < prelude.Count && prelude[pos] is CssTokenValue { Token.Type: CssTokenType.Ident } layerIdent &&
            layerIdent.Token.Value.Equals("layer", StringComparison.OrdinalIgnoreCase))
        {
            layerName = string.Empty; // anonymous
            pos++;
        }
        else if (pos < prelude.Count && prelude[pos] is CssFunction { Name: var lfname } layerFn &&
            lfname.Equals("layer", StringComparison.OrdinalIgnoreCase))
        {
            layerName = ExtractDottedIdent(layerFn.Values);
            pos++;
        }
        SkipWs(prelude, ref pos);

        IReadOnlyList<CssComponentValue>? supports = null;
        if (pos < prelude.Count && prelude[pos] is CssFunction { Name: var sname } sfn &&
            sname.Equals("supports", StringComparison.OrdinalIgnoreCase))
        {
            supports = sfn.Values;
            pos++;
        }
        SkipWs(prelude, ref pos);

        var remainder = new List<CssComponentValue>();
        for (var i = pos; i < prelude.Count; i++)
        {
            remainder.Add(prelude[i]);
        }

        var mqList = remainder.Count == 0
            ? MediaQueryList.All
            : MediaQueryParser.ParseList(remainder);

        importRule = new ImportRule(url, layerName, supports, mqList);
        return true;
    }

    private static void SkipWs(IReadOnlyList<CssComponentValue> values, ref int pos)
    {
        while (pos < values.Count && values[pos] is CssTokenValue { Token.Type: CssTokenType.Whitespace })
        {
            pos++;
        }
    }

    private static string? ExtractFirstString(IReadOnlyList<CssComponentValue> values)
    {
        foreach (var v in values)
        {
            if (v is CssTokenValue { Token.Type: CssTokenType.String } s)
            {
                return s.Token.Value;
            }
        }

        return null;
    }

    private static string? ExtractDottedIdent(IReadOnlyList<CssComponentValue> values)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var v in values)
        {
            if (v is CssTokenValue tv)
            {
                if (tv.Token.Type == CssTokenType.Ident)
                {
                    sb.Append(tv.Token.Value);
                }
                else if (tv.Token.Type == CssTokenType.Delim && tv.Token.Delimiter == '.')
                {
                    sb.Append('.');
                }
            }
        }
        return sb.Length == 0 ? null : sb.ToString();
    }
}
