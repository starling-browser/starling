using System.Globalization;
using System.Text;
using Starling.Css.Tokenizer;

namespace Starling.Css.Cssom;

/// <summary>
/// Re-serializes a CSS declaration value to its canonical form for the CSSOM
/// <c>setProperty</c>/<c>getPropertyValue</c> round-trip (CSS Syntax 3 §8 token
/// serialization, e.g. <c>1.0</c>→<c>1</c>, <c>.1</c>→<c>0.1</c>,
/// <c>1.0px</c>→<c>1px</c>). Returns null when the value is not a well-formed
/// component-value sequence (e.g. a trailing decimal point as in <c>1.</c> or
/// <c>1.px</c>), in which case <c>setProperty</c> leaves the property untouched.
/// </summary>
public static class CssValueSerializer
{
    /// <summary>Canonicalize a property value, or null if it is invalid.</summary>
    public static string? Canonicalize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var tokens = CssTokenizer.Tokenize(value);
        var sb = new StringBuilder();
        var any = false;
        var pendingWhitespace = false;

        foreach (var t in tokens)
        {
            switch (t.Type)
            {
                case CssTokenType.Eof:
                    break;
                case CssTokenType.Whitespace:
                    if (any)
                    {
                        pendingWhitespace = true;
                    }

                    continue;
                case CssTokenType.BadString:
                case CssTokenType.BadUrl:
                    return null;
                // A bare '.' delim never appears in a valid value — it is the
                // tell-tale of a malformed number such as "1." or "1.px".
                case CssTokenType.Delim when t.Delimiter == '.':
                    return null;
                case CssTokenType.Comma:
                    // A comma in a CSS value serializes with no space before it and
                    // exactly one space after (e.g. font-family: a, b). Drop any
                    // pending whitespace before the comma, then mark a pending space
                    // so the next token is separated by a single space (and any
                    // literal whitespace right after the comma collapses into it).
                    sb.Append(',');
                    any = true;
                    pendingWhitespace = true;
                    break;
                default:
                    if (pendingWhitespace)
                    {
                        sb.Append(' ');
                        pendingWhitespace = false;
                    }
                    sb.Append(SerializeToken(t));
                    any = true;
                    break;
            }
        }

        if (!any)
        {
            return null;
        }

        return sb.ToString();
    }

    private static string SerializeToken(CssToken t) => t.Type switch
    {
        CssTokenType.Number => SerializeNumber(t.Number),
        CssTokenType.Percentage => SerializeNumber(t.Number) + "%",
        CssTokenType.Dimension => SerializeNumber(t.Number) + t.Unit,
        CssTokenType.Ident => t.Value,
        CssTokenType.String => "\"" + t.Value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"",
        CssTokenType.Hash => "#" + t.Value,
        CssTokenType.Function => t.Value + "(",
        CssTokenType.Url => "url(" + t.Value + ")",
        CssTokenType.Delim => t.Delimiter.ToString(),
        CssTokenType.Colon => ":",
        CssTokenType.Comma => ",",
        CssTokenType.Semicolon => ";",
        CssTokenType.LeftParen => "(",
        CssTokenType.RightParen => ")",
        CssTokenType.LeftSquare => "[",
        CssTokenType.RightSquare => "]",
        CssTokenType.LeftBrace => "{",
        CssTokenType.RightBrace => "}",
        _ => t.Value,
    };

    /// <summary>Serialize a number per CSS Syntax 3 §8.2 (shortest form, no
    /// trailing zeros, leading 0 for fractions, e.g. 1.0→"1", .1→"0.1").</summary>
    public static string SerializeNumber(double n)
    {
        if (n == 0)
        {
            return "0";
        }
        // "R" gives the shortest round-trippable form; for integers this is "5",
        // for fractions "0.1"/"-0.5" — which matches CSS number serialization.
        var s = n.ToString("R", CultureInfo.InvariantCulture);
        // Guard against exponent forms for typical CSS magnitudes.
        if (s.Contains('E') || s.Contains('e'))
        {
            s = n.ToString("0.################", CultureInfo.InvariantCulture);
        }

        return s;
    }
}
