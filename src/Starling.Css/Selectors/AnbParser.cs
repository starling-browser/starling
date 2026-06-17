using System.Globalization;
using Starling.Css.Tokenizer;

namespace Starling.Css.Selectors;

/// <summary>
/// Parses the An+B microsyntax (CSS Syntax 3 §9) from a raw token stream.
/// Whitespace is significant in An+B (e.g. <c>+n</c> is valid but <c>+ n</c> is
/// a parse error), so this parser consumes the un-pre-filtered token list and
/// applies the grammar's exact whitespace rules. Returns null on a parse error.
/// </summary>
public static class AnbParser
{
    /// <summary>Parse An+B from raw text (e.g. the contents of <c>:nth-child(...)</c>).</summary>
    public static NthPattern? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = CssTokenizer.Tokenize(text);
        return Parse(tokens);
    }

    public static NthPattern? Parse(IReadOnlyList<CssToken> rawTokens)
    {
        ArgumentNullException.ThrowIfNull(rawTokens);
        // Drop the trailing EOF the tokenizer appends; keep whitespace tokens.
        var tokens = new List<CssToken>(rawTokens.Count);
        foreach (var t in rawTokens)
        {
            if (t.Type != CssTokenType.Eof)
            {
                tokens.Add(t);
            }
        }

        var p = new Cursor(tokens);
        p.SkipWhitespace();
        var result = ParseAnb(ref p);
        if (result is null)
        {
            return null;
        }

        p.SkipWhitespace();
        return p.AtEnd ? result : null;
    }

    private static NthPattern? ParseAnb(ref Cursor p)
    {
        var t = p.Current;

        // odd | even
        if (t.Type == CssTokenType.Ident)
        {
            var v = t.Value.ToLowerInvariant();
            if (v == "odd") { p.Advance(); return new NthPattern(2, 1); }
            if (v == "even") { p.Advance(); return new NthPattern(2, 0); }
        }

        // <integer>  (no n at all)
        if (t.Type == CssTokenType.Number && t.IsInteger)
        {
            p.Advance();
            return new NthPattern(0, (int)t.Number);
        }

        // <n-dimension> ...                          5n, 5n+5, 5n -5, 5n + 5
        // <ndashdigit-dimension>                     5n-5
        // <ndash-dimension> <signless-integer>       5n- 5
        if (t.Type == CssTokenType.Dimension && t.IsInteger)
        {
            var unit = t.Unit.ToLowerInvariant();
            var a = (int)t.Number;

            if (unit == "n")
            {
                p.Advance();
                return ParseBClause(ref p, a);
            }

            // n-<digits>  → A n, B = -<digits>
            if (TryParseNDashDigit(unit, out var b))
            {
                p.Advance();
                return new NthPattern(a, b);
            }

            // "n-"  → expects a following <signless-integer> (the '-' is the sign)
            if (unit == "n-")
            {
                p.Advance();
                var sb = ParseSignlessInteger(ref p);
                if (sb is null)
                {
                    return null;
                }

                return new NthPattern(a, -sb.Value);
            }

            return null;
        }

        // Sign-prefixed forms beginning with a Delim '+' (the '-' cases are folded
        // into idents/dimensions by the tokenizer): "+n", "+n+1", "+n-5", "+n-1", ...
        if (t.Type == CssTokenType.Delim && t.Delimiter == '+')
        {
            // '+' must be immediately followed (no whitespace) by an ident.
            if (p.PeekType(1) != CssTokenType.Ident)
            {
                return null;
            }

            p.Advance(); // consume '+'
            return ParseNIdent(ref p, sign: 1);
        }

        // n | -n | n-5 | -n-5 | n+1 | -n+1 ... (ident-led)
        if (t.Type == CssTokenType.Ident)
        {
            return ParseNIdent(ref p, sign: 1);
        }

        return null;
    }

    /// <summary>Parse an ident-led An+B tail: n, -n, n-5, -n-5, then an optional B clause.</summary>
    private static NthPattern? ParseNIdent(ref Cursor p, int sign)
    {
        var t = p.Current;
        if (t.Type != CssTokenType.Ident)
        {
            return null;
        }

        var v = t.Value.ToLowerInvariant();

        // n
        if (v == "n")
        {
            p.Advance();
            return ParseBClause(ref p, sign);
        }
        // -n
        if (v == "-n")
        {
            p.Advance();
            return ParseBClause(ref p, -sign);
        }
        // n-<digits>      → A = +1, B = -digits
        if (TryParseNDashDigit(v, out var b1))
        {
            p.Advance();
            return new NthPattern(sign, b1);
        }
        // -n-<digits>     → A = -1, B = -digits
        if (v.StartsWith("-n-", StringComparison.Ordinal) &&
            TryParseDigits(v[3..], out var digits))
        {
            p.Advance();
            return new NthPattern(-sign, -digits);
        }
        // "n-" → following <signless-integer>
        if (v == "n-")
        {
            p.Advance();
            var sb = ParseSignlessInteger(ref p);
            if (sb is null)
            {
                return null;
            }

            return new NthPattern(sign, -sb.Value);
        }
        // "-n-" → following <signless-integer>
        if (v == "-n-")
        {
            p.Advance();
            var sb = ParseSignlessInteger(ref p);
            if (sb is null)
            {
                return null;
            }

            return new NthPattern(-sign, -sb.Value);
        }

        return null;
    }

    /// <summary>After "An", parse the optional B clause:
    /// nothing | &lt;signed-integer&gt; | ['+' | '-'] &lt;signless-integer&gt;.</summary>
    private static NthPattern? ParseBClause(ref Cursor p, int a)
    {
        var save = p;
        p.SkipWhitespace();

        // End → B = 0
        if (p.AtEnd)
        {
            return new NthPattern(a, 0);
        }

        // <signed-integer>: a Number with an explicit sign (e.g. "+5", "-5").
        var t = p.Current;
        if (t.Type == CssTokenType.Number && t.IsInteger && t.HasSign)
        {
            p.Advance();
            return new NthPattern(a, (int)t.Number);
        }

        // ['+' | '-'] <signless-integer>
        if (t.Type == CssTokenType.Delim && (t.Delimiter == '+' || t.Delimiter == '-'))
        {
            var bsign = t.Delimiter == '-' ? -1 : 1;
            p.Advance();
            var sb = ParseSignlessInteger(ref p);
            if (sb is null)
            {
                return null;
            }

            return new NthPattern(a, bsign * sb.Value);
        }

        // Anything else after An is a parse error; but a bare A with no B and
        // trailing tokens is handled by the caller's end check. Restore and let
        // the top-level end-of-input check reject leftover tokens.
        p = save;
        return new NthPattern(a, 0);
    }

    /// <summary>Parse a &lt;signless-integer&gt;: optional whitespace then an
    /// unsigned integer Number token.</summary>
    private static int? ParseSignlessInteger(ref Cursor p)
    {
        p.SkipWhitespace();
        var t = p.Current;
        if (t.Type == CssTokenType.Number && t.IsInteger && !t.HasSign)
        {
            p.Advance();
            return (int)t.Number;
        }
        return null;
    }

    private static bool TryParseNDashDigit(string unitOrIdent, out int b)
    {
        b = 0;
        // n-<digits>  (e.g. "n-5")
        if (unitOrIdent.StartsWith("n-", StringComparison.Ordinal) &&
            TryParseDigits(unitOrIdent[2..], out var digits))
        {
            b = -digits;
            return true;
        }
        return false;
    }

    private static bool TryParseDigits(string s, out int value)
    {
        value = 0;
        if (s.Length == 0)
        {
            return false;
        }

        foreach (var c in s)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private struct Cursor
    {
        private readonly IReadOnlyList<CssToken> _tokens;
        private int _pos;

        public Cursor(IReadOnlyList<CssToken> tokens)
        {
            _tokens = tokens;
            _pos = 0;
        }

        public readonly bool AtEnd => _pos >= _tokens.Count;

        public readonly CssToken Current =>
            _pos < _tokens.Count ? _tokens[_pos] : new CssToken(CssTokenType.Eof);

        public readonly CssTokenType PeekType(int offset)
            => _pos + offset < _tokens.Count ? _tokens[_pos + offset].Type : CssTokenType.Eof;

        public void Advance() => _pos++;

        public void SkipWhitespace()
        {
            while (_pos < _tokens.Count && _tokens[_pos].Type == CssTokenType.Whitespace)
            {
                _pos++;
            }
        }
    }
}
