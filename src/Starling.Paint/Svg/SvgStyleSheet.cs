using System.Text.RegularExpressions;

namespace Starling.Paint.Svg;

/// <summary>
/// A first-cut CSS resolver for the rules Adobe Illustrator / Figma emit inside
/// an SVG <c>&lt;style&gt;</c> block. It understands flat
/// <c>.class { prop: value; … }</c> and <c>element { … }</c> rules — which is
/// what real-world icon exports use — and ignores anything more advanced
/// (combinators, attribute selectors, media queries, <c>!important</c>).
/// </summary>
/// <remarks>
/// Without this, the McMaster fixtures (and most Illustrator exports) render
/// blank: their geometry carries no inline <c>fill</c>/<c>stroke</c>; the paint
/// lives entirely in <c>.st0 { fill:none; stroke:#58595B; }</c> class rules.
/// </remarks>
internal sealed partial class SvgStyleSheet
{
    private readonly Dictionary<string, List<(string Prop, string Value)>> _byClass = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string Prop, string Value)>> _byElement = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    /// <summary>Parse and accumulate the rules in one <c>&lt;style&gt;</c> body.</summary>
    public void AddCss(string? css)
    {
        if (string.IsNullOrWhiteSpace(css))
        {
            return;
        }

        css = CommentRegex().Replace(css, " ");

        int i = 0;
        while (i < css.Length)
        {
            int brace = css.IndexOf('{', i);
            if (brace < 0)
            {
                break;
            }

            int end = css.IndexOf('}', brace + 1);
            if (end < 0)
            {
                break;
            }

            var selectors = css[i..brace];
            var body = css[(brace + 1)..end];
            var decls = ParseDecls(body);

            foreach (var sel in selectors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // Only handle a single simple selector token: ".class" or "tag".
                // A compound like ".a .b" is reduced to its last simple token,
                // which is a deliberate, documented approximation.
                var token = LastSimpleToken(sel);
                if (token.StartsWith('.'))
                {
                    Append(_byClass, token[1..], decls);
                }
                else if (token.Length > 0 && (char.IsLetter(token[0])))
                {
                    Append(_byElement, token, decls);
                }
            }

            i = end + 1;
        }
    }

    /// <summary>
    /// Apply the rules matching <paramref name="elementName"/> and any of its
    /// classes to <paramref name="style"/>, element rules first then class rules
    /// (class wins, matching CSS specificity for this simple subset).
    /// </summary>
    public void Apply(SvgStyle style, string elementName, string? classAttr)
    {
        if (_byElement.TryGetValue(elementName, out var elemDecls))
        {
            foreach (var (p, v) in elemDecls)
            {
                style.ApplyDeclaration(p, v);
            }
        }

        if (!string.IsNullOrWhiteSpace(classAttr))
        {
            foreach (var cls in classAttr.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (_byClass.TryGetValue(cls, out var clsDecls))
                {
                    foreach (var (p, v) in clsDecls)
                    {
                        style.ApplyDeclaration(p, v);
                    }
                }
            }
        }
    }

    public bool IsEmpty => _byClass.Count == 0 && _byElement.Count == 0;

    private static string LastSimpleToken(string selector)
    {
        var parts = selector.Split([' ', '\t', '\r', '\n', '>'], StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 0 ? selector.Trim() : parts[^1];
    }

    private static List<(string, string)> ParseDecls(string body)
    {
        var list = new List<(string, string)>();
        foreach (var decl in body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = decl.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            list.Add((decl[..colon].Trim(), decl[(colon + 1)..].Trim()));
        }
        return list;
    }

    private static void Append(
        Dictionary<string, List<(string, string)>> map, string key, List<(string, string)> decls)
    {
        if (!map.TryGetValue(key, out var list))
        {
            map[key] = list = [];
        }

        list.AddRange(decls);
    }
}
