using System.Text;

namespace Starling.IdlGen.Parsing;

public enum IdlTokenKind { Identifier, Integer, Decimal, String, Punct, Eof }

public readonly record struct IdlToken(IdlTokenKind Kind, string Text, int Line, int Col)
{
    public override string ToString() => $"{Kind}:{Text} ({Line}:{Col})";
}

// Turns Web IDL source into a flat token list. Skips whitespace and both comment
// forms. Identifiers keep a single leading underscore stripped per the spec's
// escape rule. Strings keep their content without the quotes.
public sealed class IdlLexer
{
    private readonly string _src;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    public IdlLexer(string source) => _src = source;

    public List<IdlToken> Tokenize()
    {
        var tokens = new List<IdlToken>();
        while (true)
        {
            var tok = Next();
            tokens.Add(tok);
            if (tok.Kind == IdlTokenKind.Eof)
            {
                break;
            }
        }
        return tokens;
    }

    private char Cur => _pos < _src.Length ? _src[_pos] : '\0';
    private char Peek(int n = 1) => _pos + n < _src.Length ? _src[_pos + n] : '\0';

    private void Advance()
    {
        if (Cur == '\n') { _line++; _col = 1; }
        else { _col++; }
        _pos++;
    }

    private IdlToken Next()
    {
        SkipTrivia();
        if (_pos >= _src.Length)
        {
            return new IdlToken(IdlTokenKind.Eof, "", _line, _col);
        }

        int startLine = _line, startCol = _col;
        char c = Cur;

        // String literal.
        if (c == '"')
        {
            Advance();
            var sb = new StringBuilder();
            while (Cur is not '"' and not '\0') { sb.Append(Cur); Advance(); }
            Advance(); // closing quote
            return new IdlToken(IdlTokenKind.String, sb.ToString(), startLine, startCol);
        }

        // Identifier (optionally one leading underscore to escape a keyword).
        if (c == '_' || char.IsAsciiLetter(c))
        {
            int start = _pos;
            if (c == '_')
            {
                Advance();   // strip a single leading underscore
            }

            int nameStart = _pos;
            while (Cur is '_' or '-' || char.IsAsciiLetterOrDigit(Cur))
            {
                Advance();
            }
            // If the only char was '_' treat the underscore as part of the name.
            string text = nameStart < _pos ? _src[nameStart.._pos] : _src[start.._pos];
            return new IdlToken(IdlTokenKind.Identifier, text, startLine, startCol);
        }

        // Number (integer or decimal), with optional leading minus.
        if (char.IsAsciiDigit(c) || (c == '-' && char.IsAsciiDigit(Peek())))
        {
            return LexNumber(startLine, startCol);
        }

        // Ellipsis.
        if (c == '.' && Peek() == '.' && Peek(2) == '.')
        {
            Advance(); Advance(); Advance();
            return new IdlToken(IdlTokenKind.Punct, "...", startLine, startCol);
        }

        // Single-character punctuation.
        Advance();
        return new IdlToken(IdlTokenKind.Punct, c.ToString(), startLine, startCol);
    }

    private IdlToken LexNumber(int line, int col)
    {
        int start = _pos;
        bool isDecimal = false;
        if (Cur == '-')
        {
            Advance();
        }

        // Hex.
        if (Cur == '0' && (Peek() is 'x' or 'X'))
        {
            Advance(); Advance();
            while (Uri.IsHexDigit(Cur))
            {
                Advance();
            }

            return new IdlToken(IdlTokenKind.Integer, _src[start.._pos], line, col);
        }

        while (char.IsAsciiDigit(Cur))
        {
            Advance();
        }

        if (Cur == '.') { isDecimal = true; Advance(); while (char.IsAsciiDigit(Cur))
            {
                Advance();
            }
        }
        if (Cur is 'e' or 'E')
        {
            isDecimal = true; Advance();
            if (Cur is '+' or '-')
            {
                Advance();
            }

            while (char.IsAsciiDigit(Cur))
            {
                Advance();
            }
        }
        return new IdlToken(isDecimal ? IdlTokenKind.Decimal : IdlTokenKind.Integer, _src[start.._pos], line, col);
    }

    private void SkipTrivia()
    {
        while (_pos < _src.Length)
        {
            char c = Cur;
            if (c is ' ' or '\t' or '\r' or '\n') { Advance(); continue; }
            if (c == '/' && Peek() == '/')
            {
                while (Cur is not '\n' and not '\0')
                {
                    Advance();
                }

                continue;
            }
            if (c == '/' && Peek() == '*')
            {
                Advance(); Advance();
                while (!(Cur == '*' && Peek() == '/') && Cur != '\0')
                {
                    Advance();
                }

                Advance(); Advance();
                continue;
            }
            break;
        }
    }
}
