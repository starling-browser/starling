using Starling.Css.Parser;
using Starling.Css.Tokenizer;

namespace Starling.Css.FontFace;

/// <summary>
/// Extracts <see cref="FontFaceRule"/> values from parsed stylesheets. The
/// CSS parser already recognises <c>@font-face</c> as an at-rule with a
/// declaration list (see <see cref="Parser.CssParser"/>); this turns those
/// declarations into a strongly-typed font-face description that the engine
/// can hand to the font-face fetcher.
/// </summary>
/// <remarks>
/// Fail-soft: a rule missing the required <c>font-family</c> or <c>src</c>
/// descriptors is skipped silently (per CSS Fonts 3 §4.1, such a rule must
/// not be applied). Unsupported descriptors (<c>unicode-range</c>,
/// <c>font-variation-settings</c>, <c>font-feature-settings</c>, etc.) are
/// ignored — variable-font axes and Unicode-range coverage are later work.
/// </remarks>
public static class FontFaceParser
{
    public static IEnumerable<FontFaceRule> ParseAll(StyleSheet sheet)
    {
        ArgumentNullException.ThrowIfNull(sheet);
        foreach (var rule in sheet.Rules)
        {
            if (rule is AtRule { Name: "font-face" } atRule)
            {
                if (TryParse(atRule, out var fontFace))
                {
                    yield return fontFace!;
                }
            }
        }
    }

    public static bool TryParse(AtRule rule, out FontFaceRule? fontFace)
    {
        ArgumentNullException.ThrowIfNull(rule);
        fontFace = null;
        if (!string.Equals(rule.Name, "font-face", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string? family = null;
        var sources = new List<FontFaceSource>();
        var bold = false;
        var italic = false;
        UnicodeRangeSet? unicodeRange = null;

        foreach (var decl in rule.Declarations)
        {
            switch (decl.Name.ToLowerInvariant())
            {
                case "font-family":
                    family = ParseFamilyName(decl.Value);
                    break;
                case "src":
                    sources.AddRange(ParseSrc(decl.Value));
                    break;
                case "font-weight":
                    bold = ParseBold(decl.Value);
                    break;
                case "font-style":
                    italic = ParseItalic(decl.Value);
                    break;
                case "unicode-range":
                    unicodeRange = ParseUnicodeRange(decl.Value);
                    break;
            }
        }

        if (string.IsNullOrEmpty(family) || sources.Count == 0)
        {
            return false;
        }

        fontFace = new FontFaceRule(family, sources, bold, italic, unicodeRange);
        return true;
    }

    private static UnicodeRangeSet? ParseUnicodeRange(IReadOnlyList<CssComponentValue> value)
    {
        // The CSS tokenizer doesn't emit a unicode-range token, so the values
        // arrive as a sequence: an "U" ident, a "+" delim, then a mix of
        // numbers, idents (hex letters), and "?" / "-" delims forming one
        // entry per comma-separated chunk. We walk tokens, reassembling each
        // chunk into the canonical "U+xxxx[-yyyy]" / "U+x?x?" form for
        // TryParseSingleRange to consume.
        var sb = new System.Text.StringBuilder();
        var ranges = new List<(int, int)>();
        void Flush()
        {
            var entry = sb.ToString().Trim();
            sb.Clear();
            if (entry.Length == 0)
            {
                return;
            }

            if (TryParseSingleRange(entry, out var start, out var end))
            {
                ranges.Add((start, end));
            }
        }

        foreach (var v in value)
        {
            if (v is not CssTokenValue token)
            {
                continue;
            }

            switch (token.Token.Type)
            {
                case CssTokenType.Whitespace:
                    // Drop — adjacent tokens in unicode-range never need a
                    // separator inside a single entry.
                    break;
                case CssTokenType.Comma:
                    Flush();
                    break;
                case CssTokenType.Ident:
                    sb.Append(token.Token.Value);
                    break;
                case CssTokenType.Number:
                    // Numbers come in as doubles; for unicode-range the
                    // tokenizer may have eaten an integer prefix that we
                    // need to re-emit hex-clean. We emit the raw digits the
                    // tokenizer saw via integer ToString — fractional values
                    // aren't legal here.
                    AppendUnicodeRangeNumber(sb, token.Token.Number);
                    break;
                case CssTokenType.Dimension:
                    // The range separator '-' in "U+0000-00FF" gets swallowed
                    // into the second value when its hex digits include a letter:
                    // "-00FF" tokenizes as a Dimension (number -0, unit "FF"),
                    // and -0 stringifies as "0", dropping the '-'. That collapses
                    // the whole range to the single codepoint U+00FF. Recover the
                    // sign from the token (double.IsNegative is true for -0) and
                    // re-emit the '-' so the range survives.
                    AppendUnicodeRangeNumber(sb, token.Token.Number);
                    sb.Append(token.Token.Unit);
                    break;
                case CssTokenType.Delim:
                    sb.Append(token.Token.Delimiter);
                    break;
                case CssTokenType.Hash:
                    sb.Append('#').Append(token.Token.Value);
                    break;
            }
        }
        Flush();
        return ranges.Count == 0 ? null : new UnicodeRangeSet(ranges);
    }

    // Re-emit a unicode-range numeric token's digits, preserving a leading '-'
    // (the range separator) that the tokenizer folded into the value's sign.
    // double.IsNegative catches -0, which a plain comparison would miss.
    private static void AppendUnicodeRangeNumber(System.Text.StringBuilder sb, double number)
    {
        if (double.IsNegative(number))
        {
            sb.Append('-');
        }

        sb.Append(((long)Math.Abs(number)).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static bool TryParseSingleRange(string spec, out int start, out int end)
    {
        start = end = 0;
        // Format: "U+xxx" / "U+xxx-yyy" / "U+x?x?" (wildcards). The CSS
        // tokenizer eats the '+' as the sign of the following Number, so by
        // the time we reassemble we may see "U400" not "U+400" — accept both.
        var s = spec.AsSpan().Trim();
        if (s.Length < 2)
        {
            return false;
        }

        if (s[0] != 'U' && s[0] != 'u')
        {
            return false;
        }

        s = s[1..];
        if (s.Length > 0 && s[0] == '+')
        {
            s = s[1..];
        }

        // Wildcard form: every '?' marks a hex nibble that varies. Replace with
        // 0 for the low bound and F for the high bound.
        if (s.IndexOf('?') >= 0)
        {
            Span<char> low = stackalloc char[s.Length];
            Span<char> high = stackalloc char[s.Length];
            for (var i = 0; i < s.Length; i++)
            {
                low[i] = s[i] == '?' ? '0' : s[i];
                high[i] = s[i] == '?' ? 'F' : s[i];
            }
            return int.TryParse(low, System.Globalization.NumberStyles.HexNumber, null, out start)
                && int.TryParse(high, System.Globalization.NumberStyles.HexNumber, null, out end);
        }

        // Range form "xxx-yyy" or single "xxx".
        var dash = s.IndexOf('-');
        if (dash < 0)
        {
            if (!int.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out start))
            {
                return false;
            }

            end = start;
            return true;
        }
        return int.TryParse(s[..dash], System.Globalization.NumberStyles.HexNumber, null, out start)
            && int.TryParse(s[(dash + 1)..], System.Globalization.NumberStyles.HexNumber, null, out end);
    }

    private static string? ParseFamilyName(IReadOnlyList<CssComponentValue> value)
    {
        // The family name is either a quoted string ("Open Sans") or a
        // sequence of idents (Open Sans) — CSS Fonts 3 §4.2 allows both.
        string? quoted = null;
        var idents = new List<string>();
        foreach (var v in value)
        {
            if (v is CssTokenValue token)
            {
                switch (token.Token.Type)
                {
                    case CssTokenType.String:
                        quoted = token.Token.Value;
                        break;
                    case CssTokenType.Ident:
                        idents.Add(token.Token.Value);
                        break;
                }
            }
        }
        if (!string.IsNullOrEmpty(quoted))
        {
            return quoted;
        }

        return idents.Count == 0 ? null : string.Join(' ', idents);
    }

    private static IEnumerable<FontFaceSource> ParseSrc(IReadOnlyList<CssComponentValue> value)
    {
        // src is a comma-separated list of either local(name) or url(...) with
        // an optional format() hint. Split on top-level commas and parse each
        // entry.
        foreach (var entry in SplitOnComma(value))
        {
            var source = ParseSrcEntry(entry);
            if (source is not null)
            {
                yield return source;
            }
        }
    }

    private static FontFaceSource? ParseSrcEntry(IReadOnlyList<CssComponentValue> entry)
    {
        string? formatHint = null;
        FontFaceSource? primary = null;

        foreach (var v in entry)
        {
            switch (v)
            {
                case CssFunction { Name: var name } fn when string.Equals(name, "local", StringComparison.OrdinalIgnoreCase):
                    primary ??= new LocalFontSource(ExtractStringOrIdent(fn.Values) ?? string.Empty);
                    break;
                case CssFunction { Name: var name } fn when string.Equals(name, "url", StringComparison.OrdinalIgnoreCase):
                    var url = ExtractStringOrIdent(fn.Values);
                    if (!string.IsNullOrEmpty(url))
                    {
                        primary ??= new UrlFontSource(url, null);
                    }

                    break;
                case CssFunction { Name: var name } fn when string.Equals(name, "format", StringComparison.OrdinalIgnoreCase):
                    formatHint = ExtractStringOrIdent(fn.Values);
                    break;
                case CssTokenValue { Token: { Type: CssTokenType.Url } urlToken }:
                    primary ??= new UrlFontSource(urlToken.Value, null);
                    break;
            }
        }

        return primary switch
        {
            UrlFontSource u => new UrlFontSource(u.Url, formatHint),
            LocalFontSource l => l,
            _ => null,
        };
    }

    private static string? ExtractStringOrIdent(IReadOnlyList<CssComponentValue> values)
    {
        foreach (var v in values)
        {
            if (v is CssTokenValue token)
            {
                if (token.Token.Type == CssTokenType.String)
                {
                    return token.Token.Value;
                }

                if (token.Token.Type == CssTokenType.Url)
                {
                    return token.Token.Value;
                }

                if (token.Token.Type == CssTokenType.Ident)
                {
                    return token.Token.Value;
                }
            }
        }
        return null;
    }

    private static IEnumerable<IReadOnlyList<CssComponentValue>> SplitOnComma(IReadOnlyList<CssComponentValue> values)
    {
        var current = new List<CssComponentValue>();
        foreach (var v in values)
        {
            if (v is CssTokenValue { Token.Type: CssTokenType.Comma })
            {
                if (current.Count > 0) { yield return current; current = []; }
                continue;
            }
            // Skip leading/trailing whitespace so the per-entry list is clean.
            if (v is CssTokenValue { Token.Type: CssTokenType.Whitespace } && current.Count == 0)
            {
                continue;
            }

            current.Add(v);
        }
        if (current.Count > 0)
        {
            yield return current;
        }
    }

    private static bool ParseBold(IReadOnlyList<CssComponentValue> value)
    {
        // font-weight inside @font-face: bold | <number> | "<number> <number>".
        // Anything ≥ 600 (CSS bold threshold) marks the face as bold. A range
        // ("100 700") flips bold on if the *upper* bound clears the threshold,
        // matching what variable-font browsers do when the requested weight
        // falls inside the range.
        var top = 0d;
        foreach (var v in value)
        {
            if (v is CssTokenValue token)
            {
                if (token.Token.Type == CssTokenType.Ident &&
                    string.Equals(token.Token.Value, "bold", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (token.Token.Type == CssTokenType.Number && token.Token.Number > top)
                {
                    top = token.Token.Number;
                }
            }
        }
        return top >= 600;
    }

    private static bool ParseItalic(IReadOnlyList<CssComponentValue> value)
    {
        foreach (var v in value)
        {
            if (v is CssTokenValue token &&
                token.Token.Type == CssTokenType.Ident &&
                (string.Equals(token.Token.Value, "italic", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(token.Token.Value, "oblique", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        return false;
    }
}
