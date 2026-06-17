namespace Starling.Js.Lex;

/// <summary>
/// A lexer token. Source-backed tokens carry an offset and length into the
/// original source, so raw text is only allocated when <see cref="Lexeme"/> or
/// another text helper is used.
/// </summary>
public readonly struct JsToken
{
    private readonly ReadOnlyMemory<char> _source;
    private readonly ReadOnlyMemory<char> _lexeme;
    private readonly object? _value;
    private readonly JsTokenValueKind _valueKind;
    private readonly int _valueOffset;
    private readonly int _valueLength;
    private readonly int _extraOffset;
    private readonly int _extraLength;

    public JsToken(
        JsTokenKind kind,
        ReadOnlyMemory<char> lexeme,
        JsPosition start,
        JsPosition end,
        object? value = null)
        : this(kind, source: default, lexeme, offset: 0, length: lexeme.Length, start, end,
            value, JsTokenValueKind.Direct, 0, 0, 0, 0, false, false, false, false)
    {
    }

    public JsToken(
        JsTokenKind kind,
        string lexeme,
        JsPosition start,
        JsPosition end,
        object? value = null)
        : this(kind, lexeme.AsMemory(), start, end, value)
    {
    }

    private JsToken(
        JsTokenKind kind,
        ReadOnlyMemory<char> source,
        ReadOnlyMemory<char> lexeme,
        int offset,
        int length,
        JsPosition start,
        JsPosition end,
        object? value,
        JsTokenValueKind valueKind,
        int valueOffset,
        int valueLength,
        int extraOffset,
        int extraLength,
        bool precededByLineTerminator,
        bool legacyOctal,
        bool containsEscape,
        bool invalidEscape)
    {
        Kind = kind;
        _source = source;
        _lexeme = lexeme;
        Offset = offset;
        Length = length;
        Start = start;
        End = end;
        _value = value;
        _valueKind = valueKind;
        _valueOffset = valueOffset;
        _valueLength = valueLength;
        _extraOffset = extraOffset;
        _extraLength = extraLength;
        PrecededByLineTerminator = precededByLineTerminator;
        LegacyOctal = legacyOctal;
        ContainsEscape = containsEscape;
        InvalidEscape = invalidEscape;
    }

    public JsTokenKind Kind { get; }

    /// <summary>The raw token text, or the decoded identifier text for escaped identifiers.</summary>
    public string Lexeme => !_lexeme.IsEmpty ? _lexeme.ToString() : SourceSlice(Offset, Length);

    /// <summary>The same text as <see cref="Lexeme"/>, exposed as a span to avoid allocation.</summary>
    public ReadOnlySpan<char> LexemeSpan
        => !_lexeme.IsEmpty ? _lexeme.Span : SourceSpan(Offset, Length);

    public int Offset { get; }

    public int Length { get; }

    public JsPosition Start { get; }

    public JsPosition End { get; }

    public object? Value => _valueKind switch
    {
        JsTokenValueKind.StringLiteralNoEscapes => SourceSlice(_valueOffset, _valueLength),
        JsTokenValueKind.BigIntDigits => BigIntDigits,
        JsTokenValueKind.RegExpLiteral => RegExpPatternAndFlags(),
        _ => _value,
    };

    /// <summary>True if this token was preceded by a line terminator in the
    /// source — needed by the parser's automatic-semicolon-insertion rules.</summary>
    public bool PrecededByLineTerminator { get; }

    /// <summary>True when this token uses a legacy syntactic form that is a
    /// strict-mode SyntaxError (ES §12.9.3 / B.1.2).</summary>
    public bool LegacyOctal { get; }

    /// <summary>True when an identifier / keyword token contained at least one
    /// <c>\u</c> Unicode escape in its source (§12.7.2).</summary>
    public bool ContainsEscape { get; }

    /// <summary>True when a template segment token contained a syntactically
    /// invalid escape sequence (§12.9.6 NotEscapeSequence).</summary>
    public bool InvalidEscape { get; }

    public string BigIntDigits
    {
        get
        {
            var digits = SourceSpan(_valueOffset, _valueLength);
            if (digits.IndexOf('_') < 0)
            {
                return digits.ToString();
            }

            return RemoveSeparators(digits);
        }
    }

    public (string Pattern, string Flags) RegExpPatternAndFlags()
        => (SourceSlice(_valueOffset, _valueLength), SourceSlice(_extraOffset, _extraLength));

    public bool TextEquals(string text) => LexemeSpan.SequenceEqual(text.AsSpan());

    public override string ToString() => $"{Kind} \"{Lexeme}\" at {Start}";

    internal static JsToken FromSource(
        JsTokenKind kind,
        ReadOnlyMemory<char> source,
        int offset,
        int length,
        JsPosition start,
        JsPosition end,
        bool precededByLineTerminator,
        object? value = null,
        bool legacyOctal = false,
        bool containsEscape = false,
        bool invalidEscape = false)
        => new(kind, source, lexeme: default, offset, length, start, end,
            value, JsTokenValueKind.Direct, 0, 0, 0, 0,
            precededByLineTerminator, legacyOctal, containsEscape, invalidEscape);

    internal static JsToken FromDecodedText(
        JsTokenKind kind,
        ReadOnlyMemory<char> text,
        JsPosition start,
        JsPosition end,
        bool precededByLineTerminator,
        object? value = null,
        bool legacyOctal = false,
        bool containsEscape = false,
        bool invalidEscape = false)
        => new(kind, source: default, text, offset: 0, length: text.Length, start, end,
            value, JsTokenValueKind.Direct, 0, 0, 0, 0,
            precededByLineTerminator, legacyOctal, containsEscape, invalidEscape);

    internal static JsToken FromDecodedText(
        JsTokenKind kind,
        string text,
        JsPosition start,
        JsPosition end,
        bool precededByLineTerminator,
        object? value = null,
        bool legacyOctal = false,
        bool containsEscape = false,
        bool invalidEscape = false)
        => FromDecodedText(kind, text.AsMemory(), start, end, precededByLineTerminator,
            value, legacyOctal, containsEscape, invalidEscape);

    internal static JsToken StringLiteralNoEscapes(
        ReadOnlyMemory<char> source,
        int offset,
        int length,
        int valueOffset,
        int valueLength,
        JsPosition start,
        JsPosition end,
        bool precededByLineTerminator)
        => new(JsTokenKind.StringLiteral, source, lexeme: default, offset, length, start, end,
            value: null, JsTokenValueKind.StringLiteralNoEscapes, valueOffset, valueLength, 0, 0,
            precededByLineTerminator, legacyOctal: false, containsEscape: false, invalidEscape: false);

    internal static JsToken BigIntLiteral(
        ReadOnlyMemory<char> source,
        int offset,
        int length,
        int digitsOffset,
        int digitsLength,
        JsPosition start,
        JsPosition end,
        bool precededByLineTerminator)
        => new(JsTokenKind.BigIntLiteral, source, lexeme: default, offset, length, start, end,
            value: null, JsTokenValueKind.BigIntDigits, digitsOffset, digitsLength, 0, 0,
            precededByLineTerminator, legacyOctal: false, containsEscape: false, invalidEscape: false);

    internal static JsToken RegExpLiteral(
        ReadOnlyMemory<char> source,
        int offset,
        int length,
        int patternOffset,
        int patternLength,
        int flagsOffset,
        int flagsLength,
        JsPosition start,
        JsPosition end,
        bool precededByLineTerminator)
        => new(JsTokenKind.RegExpLiteral, source, lexeme: default, offset, length, start, end,
            value: null, JsTokenValueKind.RegExpLiteral,
            patternOffset, patternLength, flagsOffset, flagsLength,
            precededByLineTerminator, legacyOctal: false, containsEscape: false, invalidEscape: false);

    private ReadOnlySpan<char> SourceSpan(int offset, int length)
        => _source.IsEmpty ? ReadOnlySpan<char>.Empty : _source.Span.Slice(offset, length);

    private string SourceSlice(int offset, int length)
        => _source.IsEmpty ? string.Empty : _source.Span.Slice(offset, length).ToString();

    private static string RemoveSeparators(ReadOnlySpan<char> source)
    {
        var chars = new char[source.Length];
        var count = 0;
        foreach (var ch in source)
        {
            if (ch != '_')
            {
                chars[count++] = ch;
            }
        }
        return new string(chars, 0, count);
    }

    private enum JsTokenValueKind : byte
    {
        Direct,
        StringLiteralNoEscapes,
        BigIntDigits,
        RegExpLiteral,
    }
}
