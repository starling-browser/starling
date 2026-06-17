using System.Globalization;
using System.Text;

namespace Starling.Css.Tokenizer;

/// <summary>
/// CSS Syntax Module Level 3 §4 tokenizer.
/// </summary>
/// <remarks>
/// The tokenizer follows the spec's "consume a token" algorithm. Input is run
/// through the §3.3 preprocessing step first (see <see cref="Preprocess"/>):
/// newlines are normalized and NULL/lone surrogates become U+FFFD. The output
/// stream is materialized eagerly via <see cref="Tokenize"/>; the parser
/// consumes the list.
/// </remarks>
public sealed class CssTokenizer
{
    private readonly string _source;
    private int _position;

    public CssTokenizer(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _source = Preprocess(source);
    }

    // CSS Syntax 3 §3.3 input preprocessing: normalize newlines (CR, CRLF, FF
    // → LF), replace U+0000 NULL and lone surrogates with U+FFFD. We do this up
    // front so downstream consume steps see a clean stream.
    private static string Preprocess(string source)
    {
        var needsWork = false;
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c is '\r' or '\f' or '\0' || char.IsSurrogate(c))
            {
                needsWork = true;
                break;
            }
        }
        if (!needsWork)
        {
            return source;
        }

        var builder = new StringBuilder(source.Length);
        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            switch (c)
            {
                case '\r':
                    builder.Append('\n');
                    if (i + 1 < source.Length && source[i + 1] == '\n')
                    {
                        i++;
                    }

                    break;
                case '\f':
                    builder.Append('\n');
                    break;
                case '\0':
                    builder.Append('�');
                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]))
                    {
                        // A valid surrogate pair encodes a non-BMP code point; keep it.
                        builder.Append(c);
                        builder.Append(source[i + 1]);
                        i++;
                    }
                    else if (char.IsSurrogate(c))
                    {
                        builder.Append('�');
                    }
                    else
                    {
                        builder.Append(c);
                    }
                    break;
            }
        }
        return builder.ToString();
    }

    // CSS Syntax 3 §3: whitespace is exactly U+0009 TAB, U+000A LF, U+000C FF,
    // U+000D CR, and U+0020 SPACE. After preprocessing FF and CR become LF, but
    // we still accept them here for robustness. Note: char.IsWhiteSpace also
    // matches Unicode spaces (NBSP, U+2000…) which CSS does NOT treat as
    // whitespace, so we must not use it.
    private static bool IsCssWhitespace(char c)
        => c is '\t' or '\n' or '\f' or '\r' or ' ';

    public static IReadOnlyList<CssToken> Tokenize(string source)
    {
        var tokenizer = new CssTokenizer(source);
        var tokens = new List<CssToken>();
        CssToken token;
        do
        {
            token = tokenizer.NextToken();
            tokens.Add(token);
        }
        while (token.Type != CssTokenType.Eof);

        return tokens;
    }

    public CssToken NextToken()
    {
        while (StartsWith("/*"))
        {
            ConsumeComment();
        }

        if (IsEnd)
        {
            return new CssToken(CssTokenType.Eof);
        }

        var c = Peek();
        if (IsCssWhitespace(c))
        {
            return ConsumeWhitespace();
        }

        if (StartsWith("<!--"))
        {
            _position += 4;
            return new CssToken(CssTokenType.Cdo);
        }
        if (StartsWith("-->"))
        {
            _position += 3;
            return new CssToken(CssTokenType.Cdc);
        }
        if (c is '"' or '\'')
        {
            return ConsumeString(Read());
        }

        if (WouldStartNumber())
        {
            return ConsumeNumeric();
        }

        if (c == '@')
        {
            Read();
            if (WouldStartIdentifier())
            {
                return new CssToken(CssTokenType.AtKeyword, ConsumeName());
            }

            return new CssToken(CssTokenType.Delim, Delimiter: '@');
        }
        if (c == '#')
        {
            Read();
            if (IsName(Peek()) || StartsValidEscape())
            {
                // §4.3.6: type flag is "id" iff the value would start an ident
                // sequence (decided before consuming the name).
                var hashIsId = WouldStartIdentifier();
                return new CssToken(CssTokenType.Hash, ConsumeName(), HashIsId: hashIsId);
            }
            return new CssToken(CssTokenType.Delim, Delimiter: '#');
        }
        if (c == '\\' && StartsValidEscape())
        {
            return ConsumeIdentLike();
        }

        if (WouldStartIdentifier())
        {
            return ConsumeIdentLike();
        }

        return Read() switch
        {
            ':' => new CssToken(CssTokenType.Colon),
            ';' => new CssToken(CssTokenType.Semicolon),
            ',' => new CssToken(CssTokenType.Comma),
            '[' => new CssToken(CssTokenType.LeftSquare),
            ']' => new CssToken(CssTokenType.RightSquare),
            '(' => new CssToken(CssTokenType.LeftParen),
            ')' => new CssToken(CssTokenType.RightParen),
            '{' => new CssToken(CssTokenType.LeftBrace),
            '}' => new CssToken(CssTokenType.RightBrace),
            var delimiter => new CssToken(CssTokenType.Delim, Delimiter: delimiter),
        };
    }

    private CssToken ConsumeWhitespace()
    {
        var start = _position;
        while (!IsEnd && IsCssWhitespace(Peek()))
        {
            _position++;
        }

        return new CssToken(CssTokenType.Whitespace, _source[start.._position]);
    }

    private CssToken ConsumeString(char ending)
    {
        var value = new StringBuilder();
        while (!IsEnd)
        {
            var c = Peek();
            if (c == ending)
            {
                _position++;
                return new CssToken(CssTokenType.String, value.ToString());
            }
            if (c is '\n' or '\r' or '\f')
            {
                return new CssToken(CssTokenType.BadString, value.ToString());
            }

            if (c == '\\')
            {
                if (_position + 1 >= _source.Length)
                {
                    _position++;
                    continue;
                }
                var next = Peek(1);
                if (next is '\n' or '\r' or '\f')
                {
                    // \<newline> in a string is a line continuation per spec §4.3.5.
                    _position++; // backslash
                    if (Peek() == '\r' && Peek(1) == '\n')
                    {
                        _position++;
                    }

                    _position++;
                    continue;
                }
                _position++; // backslash
                value.Append(ConsumeEscape());
                continue;
            }

            _position++;
            value.Append(c);
        }

        return new CssToken(CssTokenType.String, value.ToString());
    }

    private CssToken ConsumeNumeric()
    {
        var start = _position;
        // CSS Syntax 3 §4.3.3: a number records its sign and integer/number type
        // flag. We carry these so An+B parsing (§9) can distinguish signed from
        // signless integers and value serialization can canonicalize numbers.
        var hasSign = Peek() is '+' or '-';
        if (hasSign)
        {
            _position++;
        }

        var isInteger = true;
        ConsumeDigits();
        if (Peek() == '.' && char.IsAsciiDigit(Peek(1)))
        {
            isInteger = false;
            _position++;
            ConsumeDigits();
        }

        if (Peek() is 'e' or 'E' && (char.IsAsciiDigit(Peek(1)) ||
            ((Peek(1) is '+' or '-') && char.IsAsciiDigit(Peek(2)))))
        {
            isInteger = false;
            _position++;
            if (Peek() is '+' or '-')
            {
                _position++;
            }

            ConsumeDigits();
        }

        var numberText = _source[start.._position];
        var number = double.Parse(numberText, CultureInfo.InvariantCulture);
        if (Peek() == '%')
        {
            _position++;
            return new CssToken(CssTokenType.Percentage, Number: number, HasSign: hasSign, IsInteger: isInteger);
        }

        if (WouldStartIdentifier())
        {
            var unit = ConsumeName();
            return new CssToken(CssTokenType.Dimension, Number: number, Unit: unit, HasSign: hasSign, IsInteger: isInteger);
        }

        return new CssToken(CssTokenType.Number, Number: number, HasSign: hasSign, IsInteger: isInteger);
    }

    private CssToken ConsumeIdentLike()
    {
        var name = ConsumeName();
        if (Peek() != '(')
        {
            return new CssToken(CssTokenType.Ident, name);
        }

        _position++;
        if (name.Equals("url", StringComparison.OrdinalIgnoreCase))
        {
            return ConsumeUrl();
        }

        return new CssToken(CssTokenType.Function, name);
    }

    private CssToken ConsumeUrl()
    {
        while (IsCssWhitespace(Peek()))
        {
            _position++;
        }

        if (Peek() is '"' or '\'')
        {
            return new CssToken(CssTokenType.Function, "url");
        }

        var value = new StringBuilder();
        while (!IsEnd)
        {
            var c = Peek();
            if (c == ')')
            {
                _position++;
                return new CssToken(CssTokenType.Url, value.ToString());
            }

            if (IsCssWhitespace(c))
            {
                while (!IsEnd && IsCssWhitespace(Peek()))
                {
                    _position++;
                }

                if (IsEnd || Peek() == ')')
                {
                    if (!IsEnd)
                    {
                        _position++;
                    }

                    return new CssToken(CssTokenType.Url, value.ToString());
                }
                return ConsumeBadUrlRemnants();
            }

            if (c is '"' or '\'' or '(' || IsNonPrintable(c))
            {
                return ConsumeBadUrlRemnants();
            }

            if (c == '\\')
            {
                if (StartsValidEscape())
                {
                    _position++;
                    value.Append(ConsumeEscape());
                    continue;
                }
                return ConsumeBadUrlRemnants();
            }

            _position++;
            value.Append(c);
        }

        return new CssToken(CssTokenType.Url, value.ToString());
    }

    private CssToken ConsumeBadUrlRemnants()
    {
        while (!IsEnd)
        {
            var c = Read();
            if (c == ')')
            {
                break;
            }

            if (c == '\\' && _position < _source.Length && _source[_position] is not ('\n' or '\r' or '\f'))
            {
                ConsumeEscape();
            }
        }
        return new CssToken(CssTokenType.BadUrl);
    }

    private string ConsumeName()
    {
        var builder = new StringBuilder();
        while (true)
        {
            var c = Peek();
            if (IsName(c))
            {
                _position++;
                builder.Append(c);
                continue;
            }
            if (c == '\\' && StartsValidEscape())
            {
                _position++;
                builder.Append(ConsumeEscape());
                continue;
            }
            break;
        }
        return builder.ToString();
    }

    private string ConsumeEscape()
    {
        if (IsEnd)
        {
            return "�";
        }

        var c = Read();
        if (IsHex(c))
        {
            Span<char> hex = stackalloc char[6];
            hex[0] = c;
            var count = 1;
            while (count < 6 && IsHex(Peek()))
            {
                hex[count++] = _source[_position++];
            }

            if (IsCssWhitespace(Peek()))
            {
                // Spec: a single trailing whitespace after a hex escape is consumed (CRLF as one).
                if (Peek() == '\r' && Peek(1) == '\n')
                {
                    _position++;
                }

                _position++;
            }

            var code = uint.Parse(hex[..count], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (code == 0 || code > 0x10FFFF || (code >= 0xD800 && code <= 0xDFFF))
            {
                return "�";
            }

            return char.ConvertFromUtf32((int)code);
        }

        return c.ToString();
    }

    private bool StartsValidEscape(int offset = 0)
    {
        var first = Peek(offset);
        var second = Peek(offset + 1);
        return first == '\\' && second is not ('\n' or '\r' or '\f');
    }

    private static bool IsHex(char c)
        => char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    // CSS Syntax 3 §4.2: U+0000..U+0008, U+000B, U+000E..U+001F, U+007F.
    private static bool IsNonPrintable(char c)
        => c <= 0x08 || c == 0x0B || (c >= 0x0E && c <= 0x1F) || c == 0x7F;

    private void ConsumeDigits()
    {
        while (char.IsAsciiDigit(Peek()))
        {
            _position++;
        }
    }

    private void ConsumeComment()
    {
        _position += 2;
        var end = _source.IndexOf("*/", _position, StringComparison.Ordinal);
        _position = end < 0 ? _source.Length : end + 2;
    }

    private bool WouldStartIdentifier()
    {
        var c1 = Peek();
        var c2 = Peek(1);
        if (c1 == '-')
        {
            return IsNameStart(c2) || c2 == '-' || (c2 == '\\' && StartsValidEscape(1));
        }

        if (c1 == '\\')
        {
            return StartsValidEscape();
        }

        return IsNameStart(c1);
    }

    private bool WouldStartNumber()
    {
        var c1 = Peek();
        var c2 = Peek(1);
        var c3 = Peek(2);
        return c1 switch
        {
            '+' or '-' => char.IsAsciiDigit(c2) || (c2 == '.' && char.IsAsciiDigit(c3)),
            '.' => char.IsAsciiDigit(c2),
            _ => char.IsAsciiDigit(c1),
        };
    }

    private bool StartsWith(string value)
        => _position + value.Length <= _source.Length &&
           string.CompareOrdinal(_source, _position, value, 0, value.Length) == 0;

    private bool IsEnd => _position >= _source.Length;

    private char Peek(int offset = 0)
        => _position + offset < _source.Length ? _source[_position + offset] : '\0';

    private char Read() => _source[_position++];

    private static bool IsNameStart(char c)
        => char.IsAsciiLetter(c) || c == '_' || c >= 0x80;

    private static bool IsName(char c)
        => IsNameStart(c) || char.IsAsciiDigit(c) || c == '-';
}
