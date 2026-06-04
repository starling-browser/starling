namespace Starling.Js.Lex;

/// <summary>
/// A lexer token. <see cref="Lexeme"/> is the raw source slice carried as a
/// <see cref="ReadOnlySpan{T}"/>: for plain identifiers, keywords, numbers,
/// strings, punctuators, etc. it points straight into the lexer's source
/// buffer, so producing a token allocates nothing. <see cref="Value"/> carries
/// the decoded literal value (a <c>double</c> for numeric, a <c>string</c> for
/// string literals, <c>null</c> otherwise). For BigInt literals,
/// <see cref="Value"/> is the raw digits string (BigInteger conversion lives in
/// the JS runtime, not the lexer).
/// </summary>
/// <remarks>
/// A <c>ref struct</c> precisely because it holds a span: a token never
/// outlives the source buffer it slices, and the parser consumes it the moment
/// it is produced. For an escaped identifier / private name the lexeme can't be
/// a source slice (the escapes are decoded into fresh text); there the span
/// points at that decoded string, which the span keeps alive for the token's
/// lifetime.
/// </remarks>
public readonly ref struct JsToken
{
    public JsToken(
        JsTokenKind kind,
        ReadOnlySpan<char> lexeme,
        JsPosition start,
        JsPosition end,
        object? value = null)
    {
        Kind = kind;
        Lexeme = lexeme;
        Start = start;
        End = end;
        Value = value;
    }

    public JsTokenKind Kind { get; }
    public ReadOnlySpan<char> Lexeme { get; }
    public JsPosition Start { get; }
    public JsPosition End { get; }
    public object? Value { get; }

    /// <summary>True if this token was preceded by a line terminator in the
    /// source — needed by the parser's automatic-semicolon-insertion rules.</summary>
    public bool PrecededByLineTerminator { get; init; }

    /// <summary>True when this token uses a legacy syntactic form that is a
    /// strict-mode SyntaxError (ES §12.9.3 / B.1.2): a legacy octal integer
    /// literal (<c>0123</c>), a NonOctalDecimalInteger (<c>08</c>/<c>09</c>),
    /// or a string literal containing a legacy octal / <c>\8</c> / <c>\9</c>
    /// escape sequence. The lexer cannot know whether the surrounding scope is
    /// strict, so it merely tags the token; the parser raises the error when
    /// the token appears in a strict scope.</summary>
    public bool LegacyOctal { get; init; }

    /// <summary>True when an identifier / keyword token contained at least one
    /// <c>\u</c> Unicode escape in its source (§12.7.2). An escaped reserved
    /// word keeps its keyword <see cref="Kind"/> so it can still serve as an
    /// IdentifierName (property / member name), but the parser must reject it
    /// wherever a literal reserved word would itself be illegal — e.g. as a
    /// BindingIdentifier or as an IdentifierReference in an assignment pattern
    /// (<c>{ if } = …</c> is a SyntaxError).</summary>
    public bool ContainsEscape { get; init; }

    /// <summary>True when a template segment token (NoSubstitution / Head /
    /// Middle / Tail) contained a syntactically invalid escape sequence
    /// (§12.9.6 NotEscapeSequence — e.g. <c>\unicode</c>, <c>\xg</c>, a legacy
    /// octal <c>\07</c>, or <c>\8</c>/<c>\9</c>). Such a segment has NO cooked
    /// value (<see cref="Value"/> is <c>null</c>) but keeps its raw lexeme. It
    /// is a SyntaxError in an untagged template literal but legal in a tagged
    /// template, where the cooked element becomes <c>undefined</c>; the parser
    /// enforces that distinction.</summary>
    public bool InvalidEscape { get; init; }

    public override string ToString() =>
        $"{Kind} \"{Lexeme}\" at {Start}";
}
