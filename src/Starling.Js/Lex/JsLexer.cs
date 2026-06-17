using System.Globalization;
using System.Text;

namespace Starling.Js.Lex;

/// <summary>
/// ECMAScript lexer. Pull-based: <see cref="Next"/> returns the next token
/// (or sticky <see cref="JsTokenKind.EndOfFile"/>); <see cref="Peek"/> for
/// one-token lookahead.
/// </summary>
/// <remarks>
/// <para>
/// Covers the ES2024 lexical forms used by the parser, including strings,
/// numbers, template segments, private identifiers, and regular expression
/// literals. The parser drives context-sensitive scans such as regex-vs-division
/// and template continuation.
/// </para>
/// <list type="bullet">
///   <item>Identifier scanning accepts ASCII identifiers, Unicode escapes,
///   surrogate-pair astral identifiers, and a broad non-ASCII letter subset.</item>
///   <item>Full Unicode IdentifierStart and IdentifierPart tables are not
///   generated from the spec data yet.</item>
/// </list>
/// <para>
/// Reserved-word categories are first-class <see cref="JsTokenKind"/> values
/// so the parser doesn't have to re-match identifier text. Contextual
/// keywords (<c>let</c>, <c>async</c>, <c>await</c>, <c>get</c>, <c>set</c>,
/// <c>of</c>, <c>from</c>, <c>as</c>, <c>static</c>, <c>target</c>,
/// <c>meta</c>) come out as <see cref="JsTokenKind.Identifier"/>; the parser
/// disambiguates based on position.
/// </para>
/// </remarks>
public ref struct JsLexer
{
    private readonly ReadOnlySpan<char> _src;
    private readonly ReadOnlyMemory<char> _srcMemory;
    private readonly IJsLexErrorSink _errors;
    private int _i;
    private int _line = 1;
    private int _col = 1;
    private JsToken? _peeked;
    private bool _precedingLineTerm;
    /// <summary>§B.1.2 — set by <see cref="ScanEscape"/> when the escape it
    /// just consumed was a legacy octal escape (<c>\1</c>…<c>\377</c>) or a
    /// <c>\8</c>/<c>\9</c> NonOctalDecimalEscapeSequence. <see cref="ScanString"/>
    /// reads it to tag the resulting string token as a strict-mode error.</summary>
    private bool _lastEscapeWasLegacyOctal;

    /// <summary>§12.9.6 — while scanning a template segment body, escape errors
    /// are NOT fatal: an invalid escape (<c>\unicode</c>, <c>\xg</c>, legacy
    /// octal, <c>\8</c>/<c>\9</c>) is a NotEscapeSequence that yields no cooked
    /// value rather than a lexer error (the parser later rejects it only when
    /// the template is untagged). When this flag is set, <see cref="ScanEscape"/>
    /// and its helpers suppress error reporting and set
    /// <see cref="_lastEscapeWasInvalid"/> instead.</summary>
    private bool _inTemplateBody;

    /// <summary>Set by <see cref="ScanEscape"/> (when <see cref="_inTemplateBody"/>
    /// is true) if the escape it just consumed was syntactically invalid.</summary>
    private bool _lastEscapeWasInvalid;

    public JsLexer(string source, IJsLexErrorSink? errors = null)
        : this(source.AsMemory(), errors)
    {
    }

    public JsLexer(ReadOnlyMemory<char> source, IJsLexErrorSink? errors = null)
    {
        _srcMemory = source;
        _src = source.Span;
        _errors = errors ?? IJsLexErrorSink.Null;
    }

    public JsLexer(ReadOnlySpan<char> source, IJsLexErrorSink? errors = null)
    {
        _srcMemory = default;
        _src = source;
        _errors = errors ?? IJsLexErrorSink.Null;
    }

    internal ReadOnlySpan<char> Source => _src;

    /// <summary>Return the next token, advancing the stream. EOF is sticky.</summary>
    public JsToken Next()
    {
        if (_peeked is { } p) { _peeked = null; return p; }
        return Scan();
    }

    /// <summary>One-token lookahead.</summary>
    public JsToken Peek()
    {
        _peeked ??= Scan();
        return _peeked.Value;
    }

    /// <summary>Push a previously-consumed token back into the lookahead slot.
    /// Used by the parser when reclassifying a <c>/</c> token as the head of a
    /// regex literal (then immediately calls <see cref="ScanRegExp"/>, whose
    /// peeked-slash rollback fixes the underlying byte position).</summary>
    public void PushBack(JsToken token)
    {
        if (_peeked is not null)
        {
            throw new InvalidOperationException("PushBack called with a token already peeked");
        }

        _peeked = token;
    }

    /// <summary>B1b-2c — disambiguate <c>async ( ... )</c>. Look ahead at the
    /// next <c>(</c> ... <c>)</c> chunk and see whether it is followed by
    /// <c>=&gt;</c>. Returns true if so. Does not mutate parser-visible state
    /// (snapshots position + peeked token).</summary>
    /// <remarks>
    /// Called only after the parser has confirmed the current token is the
    /// <c>async</c> identifier and the peeked token is <c>LParen</c>. The
    /// scan starts at the current position, walking forward over balanced
    /// parens. Strings/templates inside are not lexed precisely — we treat
    /// them as inert characters since balancing only cares about parens.
    /// This is approximate but adequate for disambiguation.
    /// </remarks>
    public bool LookaheadIsAsyncArrow()
    {
        // Called after the parser has confirmed _current == "async" identifier
        // and _peeked == LParen. The lexer's _i pointer therefore sits just
        // past the peeked '(' character — the precondition for the shared
        // balanced-paren scan below.
        return LookaheadIsArrowFromParen(_i);
    }

    /// <summary>Disambiguate a parenthesized arrow head from an ordinary
    /// grouping/sequence expression. Given the source offset just past an
    /// opening <c>(</c>, walk balanced parens to the matching <c>)</c> and
    /// report whether <c>=&gt;</c> follows. Pure: reads <c>_src</c> only, no
    /// state mutation, so the parser can probe before committing to the
    /// arrow-parameter parse path.</summary>
    /// <remarks>
    /// Shares the approximate scan of <see cref="LookaheadIsAsyncArrow"/>:
    /// strings, templates, and comments are skipped so their inner parens
    /// don't unbalance the count. Regex literals are not lexed precisely —
    /// adequate for arrow disambiguation, matching the async variant.
    /// </remarks>
    public bool LookaheadIsArrowFromParen(int afterOpenParenOffset)
    {
        var i = afterOpenParenOffset;
        var depth = 1; // caller is positioned just inside the opening (
        for (; i < _src.Length; i++)
        {
            var c = _src[i];
            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < _src.Length && _src[i] != quote)
                {
                    if (_src[i] == '\\')
                    {
                        i++;
                    }

                    i++;
                }
                continue;
            }
            if (c == '`')
            {
                i++;
                while (i < _src.Length && _src[i] != '`')
                {
                    i++;
                }

                continue;
            }
            if (c == '/' && i + 1 < _src.Length && _src[i + 1] == '/')
            {
                while (i < _src.Length && _src[i] != '\n')
                {
                    i++;
                }

                continue;
            }
            if (c == '/' && i + 1 < _src.Length && _src[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < _src.Length && !(_src[i] == '*' && _src[i + 1] == '/'))
                {
                    i++;
                }

                i++; // skip the '/'
                continue;
            }
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0) { i++; break; }
            }
        }
        // Skip whitespace between ')' and a possible '=>'.
        while (i < _src.Length)
        {
            var c = _src[i];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { i++; continue; }
            break;
        }
        return i + 1 < _src.Length && _src[i] == '=' && _src[i + 1] == '>';
    }

    /// <summary>Drain to a list. Useful for tests; not for production parsing.</summary>
    public List<JsToken> Drain()
    {
        var tokens = new List<JsToken>();
        while (true)
        {
            var t = Next();
            tokens.Add(t);
            if (t.Kind == JsTokenKind.EndOfFile)
            {
                return tokens;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Core scan loop
    // -----------------------------------------------------------------------
    private JsToken Scan()
    {
        // §12.5 Hashbang comment — only at the very first byte of the script,
        // mirrors Node/V8/JSC. Treat as a line comment.
        if (_i == 0 && _src.Length >= 2 && _src[0] == '#' && _src[1] == '!')
        {
            while (_i < _src.Length && !IsLineTerminator(_src[_i]))
            {
                Advance();
            }
        }

        SkipWhitespaceAndComments();
        var start = CurrentPos();
        var precededByLT = _precedingLineTerm;
        _precedingLineTerm = false;

        if (_i >= _src.Length)
        {
            return MakeToken(JsTokenKind.EndOfFile, ReadOnlyMemory<char>.Empty, start, start, precededByLT);
        }

        var c = _src[_i];

        // Identifier / keyword — an identifier may begin with a raw
        // IdentifierStart char, an astral IdentifierStart written as a
        // surrogate pair, or a \u escape that decodes to one (§12.6).
        if (IsIdStart(c) || (c == '\\' && StartsIdentifierEscape(_i))
            || TryAstralIdChar(_i, first: true, out _))
        {
            return ScanIdentifier(start, precededByLT);
        }

        // Private identifier — #name, only valid in class bodies (parser
        // enforces). The name part likewise allows a leading \u escape or an
        // astral IdentifierStart surrogate pair.
        if (c == '#' && _i + 1 < _src.Length
            && (IsIdStart(_src[_i + 1]) || (_src[_i + 1] == '\\' && StartsIdentifierEscape(_i + 1))
                || TryAstralIdChar(_i + 1, first: true, out _)))
        {
            return ScanPrivateIdentifier(start, precededByLT);
        }

        // Numeric literal
        if (c >= '0' && c <= '9')
        {
            return ScanNumber(start, precededByLT);
        }

        // String literal
        if (c == '"' || c == '\'')
        {
            return ScanString(c, start, precededByLT);
        }

        // Template literal — parser-driven thereafter for substitutions.
        if (c == '`')
        {
            return ScanTemplateBody(start, precededByLT, head: true);
        }

        // Punctuator
        return ScanPunctuator(start, precededByLT);
    }

    /// <summary>Parser entry point for the start of a regex literal — called
    /// when the previous token's grammatical position permits a regex. Assumes
    /// the next character is <c>/</c>. Emits a single
    /// <see cref="JsTokenKind.RegExpLiteral"/> token whose <c>Value</c> is the
    /// tuple <c>(pattern: string, flags: string)</c>.</summary>
    public JsToken ScanRegExp()
    {
        if (_peeked is { } pq && (pq.Kind == JsTokenKind.Slash || pq.Kind == JsTokenKind.SlashEq))
        {
            // Roll back the peeked `/` (or `/=`) — we're going to re-lex it
            // as a regex; the lexeme that follows the leading slash is part
            // of the regex pattern.
            _i -= pq.Length;
            _col -= pq.Length;
            _peeked = null;
        }
        SkipWhitespaceAndComments();
        var start = CurrentPos();
        var precededByLT = _precedingLineTerm;
        _precedingLineTerm = false;

        if (_i >= _src.Length || _src[_i] != '/')
        {
            throw new InvalidOperationException("ScanRegExp called at non-slash position");
        }

        Advance(); // opening /
        var patternStart = _i;
        var inClass = false;
        while (_i < _src.Length)
        {
            var c = _src[_i];
            if (IsLineTerminator(c))
            {
                _errors.Report(JsLexError.UnterminatedRegExp, start, "unterminated regular expression");
                return MakeSourceToken(JsTokenKind.Invalid, start.Offset, _i - start.Offset, start, CurrentPos(), precededByLT);
            }
            if (c == '\\')
            {
                Advance();
                if (_i < _src.Length)
                {
                    Advance();
                }

                continue;
            }
            if (c == '[') { inClass = true; Advance(); continue; }
            if (c == ']') { inClass = false; Advance(); continue; }
            if (c == '/' && !inClass)
            {
                break;
            }

            Advance();
        }
        if (_i >= _src.Length || _src[_i] != '/')
        {
            _errors.Report(JsLexError.UnterminatedRegExp, start, "unterminated regular expression");
            return MakeSourceToken(JsTokenKind.Invalid, start.Offset, _i - start.Offset, start, CurrentPos(), precededByLT);
        }
        var patternEnd = _i;
        Advance(); // closing /
        var flagsStart = _i;
        while (_i < _src.Length && IsIdPart(_src[_i]))
        {
            Advance();
        }

        return JsToken.RegExpLiteral(
            _srcMemory,
            start.Offset,
            _i - start.Offset,
            patternStart,
            patternEnd - patternStart,
            flagsStart,
            _i - flagsStart,
            start,
            CurrentPos(),
            precededByLT);
    }

    /// <summary>Parser entry point after a <c>}</c> closes a substitution in a
    /// template literal. Continues scanning the template body — emits either
    /// <see cref="JsTokenKind.TemplateMiddle"/> (another <c>${</c> follows) or
    /// <see cref="JsTokenKind.TemplateTail"/> (closing backtick).</summary>
    public JsToken ScanTemplateContinuation()
    {
        _peeked = null;
        var start = CurrentPos();
        return ScanTemplateBody(start, precededByLT: false, head: false);
    }

    private JsToken ScanTemplateBody(JsPosition start, bool precededByLT, bool head)
    {
        // Caller positioned us at either the opening backtick (head=true) or
        // the character immediately after the `}` of a ${…} substitution.
        if (head)
        {
            if (_i >= _src.Length || _src[_i] != '`')
            {
                throw new InvalidOperationException("template head called at non-backtick");
            }

            Advance();
        }
        var begin = _i;
        var sb = new StringBuilder();
        // §12.9.6 — within a template body an invalid escape is a NotEscapeSequence:
        // it produces no cooked value (legal only in a tagged template) rather than
        // a lexer error. Track that here so the token can be flagged with no cooked
        // value while its raw lexeme is preserved.
        var prevInTemplate = _inTemplateBody;
        _inTemplateBody = true;
        var segmentInvalid = false;
        try
        {
            while (_i < _src.Length)
            {
                var c = _src[_i];
                if (c == '`')
                {
                    Advance();
                    var kind = head ? JsTokenKind.TemplateNoSubstitution : JsTokenKind.TemplateTail;
                    return MakeSourceToken(kind, begin, _i - begin, start, CurrentPos(), precededByLT,
                        segmentInvalid ? null : sb.ToString(), invalidEscape: segmentInvalid);
                }
                if (c == '$' && _i + 1 < _src.Length && _src[_i + 1] == '{')
                {
                    Advance(); Advance();
                    var kind = head ? JsTokenKind.TemplateHead : JsTokenKind.TemplateMiddle;
                    return MakeSourceToken(kind, begin, _i - begin, start, CurrentPos(), precededByLT,
                        segmentInvalid ? null : sb.ToString(), invalidEscape: segmentInvalid);
                }
                if (c == '\\')
                {
                    Advance();
                    if (_i >= _src.Length)
                    {
                        break;
                    }
                    // Line continuation \<LineTerminator> is dropped.
                    if (IsLineTerminator(_src[_i]))
                    {
                        if (_src[_i] == '\r' && _i + 1 < _src.Length && _src[_i + 1] == '\n')
                        {
                            AdvanceRaw();
                        }

                        _i++; _line++; _col = 1; _precedingLineTerm = true;
                        continue;
                    }
                    _lastEscapeWasInvalid = false;
                    sb.Append(ScanEscape(start));
                    if (_lastEscapeWasInvalid)
                    {
                        segmentInvalid = true;
                    }

                    continue;
                }
                if (IsLineTerminator(c))
                {
                    // Raw newlines are legal inside templates; track them for ASI.
                    _precedingLineTerm = true;
                    if (c == '\r' && _i + 1 < _src.Length && _src[_i + 1] == '\n')
                    {
                        AdvanceRaw();
                    }

                    sb.Append('\n');
                    _i++; _line++; _col = 1;
                    continue;
                }
                sb.Append(c);
                Advance();
            }
            _errors.Report(JsLexError.UnterminatedTemplate, start, "unterminated template literal");
            return MakeSourceToken(JsTokenKind.Invalid, begin, _i - begin, start, CurrentPos(), precededByLT, sb.ToString());
        }
        finally { _inTemplateBody = prevInTemplate; }
    }

    private JsToken ScanPrivateIdentifier(JsPosition start, bool precededByLT)
    {
        var begin = _i;
        Advance(); // '#'
        var sb = new StringBuilder("#");
        // The #-name allows the same \u escapes as a plain identifier (§12.6).
        ScanIdentifierChars(sb, start);
        var lex = sb.ToString();
        return MakeSourceToken(JsTokenKind.PrivateIdentifier, begin, _i - begin, start, CurrentPos(), precededByLT, lex);
    }

    // -----------------------------------------------------------------------
    // Whitespace, line terminators, comments — §12.2 / §12.3 / §12.4
    // -----------------------------------------------------------------------
    private void SkipWhitespaceAndComments()
    {
        while (_i < _src.Length)
        {
            var c = _src[_i];
            if (IsWhitespace(c)) { Advance(); continue; }
            if (IsLineTerminator(c))
            {
                _precedingLineTerm = true;
                // CRLF counts as one line break.
                if (c == '\r' && _i + 1 < _src.Length && _src[_i + 1] == '\n')
                {
                    AdvanceRaw();
                }

                _i++;
                _line++;
                _col = 1;
                continue;
            }
            if (c == '/' && _i + 1 < _src.Length)
            {
                var next = _src[_i + 1];
                if (next == '/') { SkipLineComment(); continue; }
                if (next == '*') { SkipBlockComment(); continue; }
            }
            break;
        }
    }

    private void SkipLineComment()
    {
        // Already at "//".
        Advance(); Advance();
        while (_i < _src.Length && !IsLineTerminator(_src[_i]))
        {
            Advance();
        }
    }

    private void SkipBlockComment()
    {
        var start = CurrentPos();
        Advance(); Advance(); // skip "/*"
        while (_i < _src.Length)
        {
            if (_src[_i] == '*' && _i + 1 < _src.Length && _src[_i + 1] == '/')
            {
                Advance(); Advance();
                return;
            }
            if (IsLineTerminator(_src[_i]))
            {
                _precedingLineTerm = true;
                if (_src[_i] == '\r' && _i + 1 < _src.Length && _src[_i + 1] == '\n')
                {
                    AdvanceRaw();
                }

                _i++; _line++; _col = 1;
            }
            else
            {
                Advance();
            }
        }
        _errors.Report(JsLexError.UnterminatedComment, start, "block comment without */");
    }

    // -----------------------------------------------------------------------
    // Identifier / keyword
    // -----------------------------------------------------------------------
    private JsToken ScanIdentifier(JsPosition start, bool precededByLT)
    {
        var begin = _i;
        var containsEscape = false;
        StringBuilder? sb = null;
        var first = true;
        while (_i < _src.Length)
        {
            var c = _src[_i];
            if (c == '\\')
            {
                var cp = PeekUnicodeEscape(_i, out var len);
                if (cp < 0 || (first ? !IsIdStartCp(cp) : !IsIdPartCp(cp)))
                {
                    _errors.Report(JsLexError.InvalidUnicodeEscape, start,
                        "invalid unicode escape in identifier");
                    break;
                }
                sb ??= new StringBuilder(_src.Slice(begin, _i - begin).ToString());
                sb.Append(char.ConvertFromUtf32(cp));
                for (var k = 0; k < len; k++)
                {
                    Advance();
                }

                containsEscape = true;
            }
            else if (first ? IsIdStart(c) : IsIdPart(c))
            {
                if (sb is not null)
                {
                    sb.Append(c);
                }

                Advance();
            }
            else if (TryAstralIdChar(_i, first, out _))
            {
                if (sb is not null)
                {
                    sb.Append(c);
                    sb.Append(_src[_i + 1]);
                }
                Advance();
                Advance();
            }
            else
            {
                break;
            }

            first = false;
        }
        var end = CurrentPos();
        if (!containsEscape)
        {
            var span = _src.Slice(begin, _i - begin);
            var sourceKind = KeywordLookup(span);
            return MakeSourceToken(sourceKind, begin, _i - begin, start, end, precededByLT,
                sourceKind == JsTokenKind.BooleanLiteral ? span.SequenceEqual("true")
                    : sourceKind == JsTokenKind.NullLiteral ? (object?)null
                    : null);
        }

        var lex = sb is not null ? sb.ToString().AsMemory() : _srcMemory.Slice(begin, _i - begin);
        // The token keeps its keyword kind even when written with a \u escape, so
        // an escaped reserved word stays usable as an IdentifierName (property /
        // member name — `a.if`, `{ if: 1 }`) while the parser still
        // rejects a keyword-kind token where a BindingIdentifier / reference is
        // required (`var if` → SyntaxError), per §12.7.2. A non-reserved
        // escaped name resolves to a plain Identifier.
        var kind = KeywordLookup(lex.Span);
        return MakeToken(kind, lex, start, end, precededByLT,
            kind == JsTokenKind.BooleanLiteral ? lex.Span.SequenceEqual("true")
                : kind == JsTokenKind.NullLiteral ? (object?)null
                : null,
            containsEscape: containsEscape);
    }

    /// <summary>Consume IdentifierStart followed by IdentifierPart* into
    /// <paramref name="sb"/>, decoding any <c>\u</c> escapes (§12.6). Returns
    /// true if at least one escape was used. Stops at the first char that is
    /// neither a raw IdentifierPart nor a valid identifier escape.</summary>
    private bool ScanIdentifierChars(StringBuilder sb, JsPosition start)
    {
        var hasEscape = false;
        var first = true;
        while (_i < _src.Length)
        {
            var c = _src[_i];
            if (c == '\\')
            {
                var cp = PeekUnicodeEscape(_i, out var len);
                if (cp < 0 || (first ? !IsIdStartCp(cp) : !IsIdPartCp(cp)))
                {
                    _errors.Report(JsLexError.InvalidUnicodeEscape, start,
                        "invalid unicode escape in identifier");
                    break;
                }
                sb.Append(char.ConvertFromUtf32(cp));
                for (var k = 0; k < len; k++)
                {
                    Advance();
                }

                hasEscape = true;
            }
            else if (first ? IsIdStart(c) : IsIdPart(c))
            {
                sb.Append(c);
                Advance();
            }
            else if (TryAstralIdChar(_i, first, out _))
            {
                // Astral IdentifierStart/Part written as a surrogate pair —
                // copy both UTF-16 units.
                sb.Append(c);
                sb.Append(_src[_i + 1]);
                Advance();
                Advance();
            }
            else
            {
                break;
            }

            first = false;
        }
        return hasEscape;
    }

    private static JsTokenKind KeywordLookup(string s) => s switch
    {
        "break" => JsTokenKind.Break,
        "case" => JsTokenKind.Case,
        "catch" => JsTokenKind.Catch,
        "class" => JsTokenKind.Class,
        "const" => JsTokenKind.Const,
        "continue" => JsTokenKind.Continue,
        "debugger" => JsTokenKind.Debugger,
        "default" => JsTokenKind.Default,
        "delete" => JsTokenKind.Delete,
        "do" => JsTokenKind.Do,
        "else" => JsTokenKind.Else,
        "enum" => JsTokenKind.Enum,
        "export" => JsTokenKind.Export,
        "extends" => JsTokenKind.Extends,
        "false" => JsTokenKind.BooleanLiteral,
        "finally" => JsTokenKind.Finally,
        "for" => JsTokenKind.For,
        "function" => JsTokenKind.Function,
        "if" => JsTokenKind.If,
        "import" => JsTokenKind.Import,
        "in" => JsTokenKind.In,
        "instanceof" => JsTokenKind.Instanceof,
        "new" => JsTokenKind.New,
        "null" => JsTokenKind.NullLiteral,
        "return" => JsTokenKind.Return,
        "super" => JsTokenKind.Super,
        "switch" => JsTokenKind.Switch,
        "this" => JsTokenKind.This,
        "throw" => JsTokenKind.Throw,
        "true" => JsTokenKind.BooleanLiteral,
        "try" => JsTokenKind.Try,
        "typeof" => JsTokenKind.Typeof,
        "var" => JsTokenKind.Var,
        "void" => JsTokenKind.Void,
        "while" => JsTokenKind.While,
        "with" => JsTokenKind.With,
        "yield" => JsTokenKind.Yield,
        _ => JsTokenKind.Identifier,
    };

    private static JsTokenKind KeywordLookup(ReadOnlySpan<char> s)
        => s.Length switch
        {
            2 when s.SequenceEqual("do") => JsTokenKind.Do,
            2 when s.SequenceEqual("if") => JsTokenKind.If,
            2 when s.SequenceEqual("in") => JsTokenKind.In,
            3 when s.SequenceEqual("for") => JsTokenKind.For,
            3 when s.SequenceEqual("new") => JsTokenKind.New,
            3 when s.SequenceEqual("try") => JsTokenKind.Try,
            3 when s.SequenceEqual("var") => JsTokenKind.Var,
            4 when s.SequenceEqual("case") => JsTokenKind.Case,
            4 when s.SequenceEqual("else") => JsTokenKind.Else,
            4 when s.SequenceEqual("enum") => JsTokenKind.Enum,
            4 when s.SequenceEqual("null") => JsTokenKind.NullLiteral,
            4 when s.SequenceEqual("this") => JsTokenKind.This,
            4 when s.SequenceEqual("true") => JsTokenKind.BooleanLiteral,
            4 when s.SequenceEqual("void") => JsTokenKind.Void,
            4 when s.SequenceEqual("with") => JsTokenKind.With,
            5 when s.SequenceEqual("break") => JsTokenKind.Break,
            5 when s.SequenceEqual("catch") => JsTokenKind.Catch,
            5 when s.SequenceEqual("class") => JsTokenKind.Class,
            5 when s.SequenceEqual("const") => JsTokenKind.Const,
            5 when s.SequenceEqual("false") => JsTokenKind.BooleanLiteral,
            5 when s.SequenceEqual("super") => JsTokenKind.Super,
            5 when s.SequenceEqual("throw") => JsTokenKind.Throw,
            5 when s.SequenceEqual("while") => JsTokenKind.While,
            5 when s.SequenceEqual("yield") => JsTokenKind.Yield,
            6 when s.SequenceEqual("delete") => JsTokenKind.Delete,
            6 when s.SequenceEqual("export") => JsTokenKind.Export,
            6 when s.SequenceEqual("import") => JsTokenKind.Import,
            6 when s.SequenceEqual("return") => JsTokenKind.Return,
            6 when s.SequenceEqual("switch") => JsTokenKind.Switch,
            6 when s.SequenceEqual("typeof") => JsTokenKind.Typeof,
            7 when s.SequenceEqual("default") => JsTokenKind.Default,
            7 when s.SequenceEqual("extends") => JsTokenKind.Extends,
            7 when s.SequenceEqual("finally") => JsTokenKind.Finally,
            8 when s.SequenceEqual("continue") => JsTokenKind.Continue,
            8 when s.SequenceEqual("debugger") => JsTokenKind.Debugger,
            8 when s.SequenceEqual("function") => JsTokenKind.Function,
            10 when s.SequenceEqual("instanceof") => JsTokenKind.Instanceof,
            _ => JsTokenKind.Identifier,
        };

    // -----------------------------------------------------------------------
    // Numeric literal — §12.9.3
    // -----------------------------------------------------------------------
    private JsToken ScanNumber(JsPosition start, bool precededByLT)
    {
        var begin = _i;
        var c = _src[_i];

        // Detect hex, binary, octal prefixes.
        if (c == '0' && _i + 1 < _src.Length)
        {
            var p = _src[_i + 1];
            if (p == 'x' || p == 'X')
            {
                return ScanRadixNumber(start, precededByLT, begin, radix: 16);
            }

            if (p == 'b' || p == 'B')
            {
                return ScanRadixNumber(start, precededByLT, begin, radix: 2);
            }

            if (p == 'o' || p == 'O')
            {
                return ScanRadixNumber(start, precededByLT, begin, radix: 8);
            }
        }

        // Decimal: digits [. digits] [eE [+-]? digits] [n]?
        // §12.9.3 NumericLiteralSeparator — `_` is allowed between two decimal
        // digits; the same rules apply to every digit-run in this literal.
        ScanDecimalDigits(start, allowSeparator: true);
        var isInteger = true;
        if (_i < _src.Length && _src[_i] == '.')
        {
            isInteger = false;
            Advance();
            // `_` immediately after `.` is a SyntaxError (no leading separator).
            if (_i < _src.Length && _src[_i] == '_')
            {
                _errors.Report(JsLexError.InvalidNumericLiteral, start,
                    "numeric separator cannot appear immediately after decimal point");
                Advance(); // consume to avoid treating it as a valid inter-digit sep
            }
            ScanDecimalDigits(start, allowSeparator: true);
        }
        if (_i < _src.Length && (_src[_i] == 'e' || _src[_i] == 'E'))
        {
            isInteger = false;
            // `_` immediately before the exponent letter is rejected by the
            // fact that ScanDecimalDigits already banned a trailing separator.
            Advance();
            if (_i < _src.Length && (_src[_i] == '+' || _src[_i] == '-'))
            {
                Advance();
            }

            if (_i >= _src.Length || !IsAsciiDigit(_src[_i]))
            {
                _errors.Report(JsLexError.InvalidNumericLiteral, start, "exponent has no digits");
            }
            // `_` immediately after exponent sign / letter is a SyntaxError:
            // no leading separator in the exponent digit-run.
            if (_i < _src.Length && _src[_i] == '_')
            {
                _errors.Report(JsLexError.InvalidNumericLiteral, start,
                    "numeric separator cannot appear immediately after exponent marker");
                Advance(); // consume to avoid treating it as a valid inter-digit sep
            }
            ScanDecimalDigits(start, allowSeparator: true);
        }

        // BigInt suffix `n` only legal on pure integers.
        if (isInteger && _i < _src.Length && _src[_i] == 'n')
        {
            var rawBi = _src.Slice(begin, _i - begin);
            // ScanDecimalDigits already reports a trailing `_` as a separator
            // error; no second report needed here.
            var digitsBi = rawBi.IndexOf('_') >= 0 ? RemoveSeparators(rawBi) : null;
            var digitsSpan = digitsBi is null ? rawBi : digitsBi.AsSpan();
            // §12.9.3 — `LegacyOctalIntegerLiteral` / `NonOctalDecimalIntegerLiteral`
            // (a leading `0` immediately followed by a decimal digit) cannot carry
            // a BigInt suffix: `00n`, `01n`, `08n` are SyntaxErrors.
            if (digitsSpan.Length >= 2 && digitsSpan[0] == '0' && IsAsciiDigit(digitsSpan[1]))
            {
                _errors.Report(JsLexError.InvalidNumericLiteral, start,
                    "legacy octal / non-octal-decimal literal cannot have a BigInt suffix");
            }

            Advance(); // consume n
            CheckNoIdentifierAfterNumber(start);
            return JsToken.BigIntLiteral(_srcMemory, begin, _i - begin, begin, _i - begin - 1,
                start, CurrentPos(), precededByLT);
        }

        var lex = _src.Slice(begin, _i - begin);
        // Strip separators before numeric conversion so `1_000` parses as 1000.
        var lexNoSep = lex.IndexOf('_') >= 0 ? RemoveSeparators(lex) : null;
        var parseSpan = lexNoSep is null ? lex : lexNoSep.AsSpan();
        if (!double.TryParse(parseSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            _errors.Report(JsLexError.InvalidNumericLiteral, start, lex.ToString());
            value = double.NaN;
        }
        // §12.9.3 / B.1.2 — a literal that starts with `0` immediately followed
        // by a decimal digit is either a LegacyOctalIntegerLiteral (`0123`) or a
        // NonOctalDecimalIntegerLiteral (`08`, `09`). Both are strict-mode
        // SyntaxErrors. Tag the token so the parser can raise the error when the
        // surrounding scope is strict. A leading-zero literal with a `.` or `e`
        // (e.g. `0.5`, `0e3`) is an ordinary DecimalLiteral and not tagged.
        var legacyOctal = isInteger && parseSpan.Length >= 2 && parseSpan[0] == '0' && IsAsciiDigit(parseSpan[1]);
        // §12.9.3 — LegacyOctalIntegerLiteral / NonOctalDecimalIntegerLiteral do
        // not permit numeric separators. If the raw lexeme contains `_` and the
        // stripped form qualifies as a legacy-leading-zero literal, report it.
        if (legacyOctal && lex.IndexOf('_') >= 0)
        {
            _errors.Report(JsLexError.InvalidNumericLiteral, start,
                "numeric separator is not allowed in legacy octal / non-octal decimal literals");
        }
        // Legacy octal literals (`010`) denote a base-8 value; .NET parses the
        // lexeme as decimal above (`010` → 10). Recompute octal-style when every
        // digit is 0-7 so the runtime sees the spec value in sloppy mode.
        if (legacyOctal && AllOctalDigits(parseSpan))
        {
            value = 0;
            for (var k = 0; k < parseSpan.Length; k++)
            {
                value = value * 8 + (parseSpan[k] - '0');
            }
        }
        CheckNoIdentifierAfterNumber(start);
        return MakeSourceToken(JsTokenKind.NumericLiteral, begin, _i - begin,
            start, CurrentPos(), precededByLT, value, legacyOctal);
    }

    /// <summary>Consume a run of decimal digits, allowing a single <c>_</c>
    /// separator between any two consecutive digits (§12.9.3
    /// NumericLiteralSeparator). Reports a lexical error for a leading or
    /// doubled separator; a <em>trailing</em> separator (digit <c>_</c> not
    /// followed by a digit) is also rejected because the characters after the
    /// run (decimal point, exponent letter, BigInt <c>n</c>, or end of token)
    /// are not digits.
    /// <para>Precondition: the caller has already verified the <em>first</em>
    /// character is a decimal digit (so this method never sees a leading
    /// separator on entry).</para></summary>
    private void ScanDecimalDigits(JsPosition start, bool allowSeparator)
    {
        var prevWasSep = false;
        while (_i < _src.Length)
        {
            var c = _src[_i];
            if (IsAsciiDigit(c)) { prevWasSep = false; Advance(); continue; }
            if (allowSeparator && c == '_')
            {
                // `_` is only valid between two digits: the previous char must
                // have been a digit (not another `_`) and the next char must
                // be a digit too.
                var nextIsDigit = _i + 1 < _src.Length && IsAsciiDigit(_src[_i + 1]);
                if (prevWasSep || !nextIsDigit)
                {
                    // Doubled (`1__0`) or trailing (`1_`) separator.
                    _errors.Report(JsLexError.InvalidNumericLiteral, start,
                        "numeric separator must be between two digits");
                    Advance(); // consume the bad `_` so lexing can continue
                    prevWasSep = true;
                    continue;
                }
                prevWasSep = true;
                Advance(); // consume `_`
                continue;
            }
            break;
        }
    }

    /// <summary>§12.9.3 — the SourceCharacter immediately following a
    /// NumericLiteral must not be an IdentifierStart or a DecimalDigit, so
    /// `3in`, `0x1g`, `1n2` etc. are SyntaxErrors. (A trailing `.` belongs to a
    /// fraction and is consumed before this runs.)</summary>
    private void CheckNoIdentifierAfterNumber(JsPosition start)
    {
        if (_i >= _src.Length)
        {
            return;
        }

        var c = _src[_i];
        if (IsAsciiDigit(c) || IsIdStart(c) || TryAstralIdChar(_i, first: true, out _))
        {
            _errors.Report(JsLexError.InvalidNumericLiteral, start,
                "identifier or digit immediately after numeric literal");
        }
    }

    private static bool AllOctalDigits(ReadOnlySpan<char> s)
    {
        foreach (var ch in s)
        {
            if (ch < '0' || ch > '7')
            {
                return false;
            }
        }

        return true;
    }

    private JsToken ScanRadixNumber(JsPosition start, bool precededByLT, int begin, int radix)
    {
        Advance(); Advance(); // 0x / 0b / 0o
        var digitStart = _i;
        // §12.9.3 NumericLiteralSeparator — `_` between two radix digits is
        // allowed, but NOT immediately after the radix prefix (`0x_1` is an
        // early error). Consume and report, then continue so the remaining
        // digits are still scanned and the token boundary is clean.
        if (_i < _src.Length && _src[_i] == '_')
        {
            _errors.Report(JsLexError.InvalidNumericLiteral, start,
                "numeric separator cannot appear immediately after radix prefix");
            Advance(); // consume the bad leading `_`
        }
        ScanRadixDigits(start, radix);
        if (_i == digitStart)
        {
            _errors.Report(JsLexError.InvalidNumericLiteral, start, "radix literal has no digits");
        }

        var isInteger = true; // always for these prefixes
        _ = isInteger; // silence unused
        // BigInt suffix permitted on integer radix forms too.
        if (_i < _src.Length && _src[_i] == 'n')
        {
            var rawBi = _src.Slice(digitStart, _i - digitStart);
            // ScanRadixDigits already reports a trailing `_`; no second report.
            Advance();
            CheckNoIdentifierAfterNumber(start);
            _ = rawBi;
            return JsToken.BigIntLiteral(_srcMemory, begin, _i - begin, digitStart, _i - digitStart - 1,
                start, CurrentPos(), precededByLT);
        }
        var rawDigits = _src.Slice(digitStart, _i - digitStart);
        var digitsNoSep = rawDigits.IndexOf('_') >= 0 ? RemoveSeparators(rawDigits) : null;
        var digits = digitsNoSep is null ? rawDigits : digitsNoSep.AsSpan();
        double value;
        try
        {
            value = ParseRadixDouble(digits, radix);
        }
        catch
        {
            _errors.Report(JsLexError.InvalidNumericLiteral, start, _src.Slice(begin, _i - begin).ToString());
            value = double.NaN;
        }
        CheckNoIdentifierAfterNumber(start);
        return MakeSourceToken(JsTokenKind.NumericLiteral, begin, _i - begin,
            start, CurrentPos(), precededByLT, value);
    }

    /// <summary>Consume radix digits for <paramref name="radix"/> (2, 8, or 16),
    /// allowing a single <c>_</c> separator between two valid digits. A leading,
    /// trailing, or doubled separator is flagged as a lexical error.
    /// <para>Precondition: the caller has already verified that if the very first
    /// character is <c>_</c> it is a leading-separator error (so this method is
    /// called only when the cursor is either at a valid radix digit or at a
    /// known-bad leading <c>_</c> that was already reported).</para></summary>
    private void ScanRadixDigits(JsPosition start, int radix)
    {
        var prevWasSep = false;
        while (_i < _src.Length)
        {
            var c = _src[_i];
            if (IsDigitInRadix(c, radix)) { prevWasSep = false; Advance(); continue; }
            if (c == '_')
            {
                var nextIsDigit = _i + 1 < _src.Length && IsDigitInRadix(_src[_i + 1], radix);
                if (prevWasSep || !nextIsDigit)
                {
                    _errors.Report(JsLexError.InvalidNumericLiteral, start,
                        "numeric separator must be between two digits");
                    Advance();
                    prevWasSep = true;
                    continue;
                }
                prevWasSep = true;
                Advance(); // consume `_`
                continue;
            }
            break;
        }
    }

    /// <summary>
    /// Scans a numeric literal that begins with a decimal point, e.g. <c>.5</c>,
    /// <c>.25e3</c>, <c>.0</c>.  ECMAScript §12.9.3 <c>DecimalLiteral</c>
    /// production: <c>. DecimalDigits ExponentPart?</c>.
    /// Called only when the current character is <c>.</c> and the lookahead is
    /// an ASCII digit — the caller (ScanPunctuator) has already verified this.
    /// </summary>
    private JsToken ScanLeadingDotNumber(JsPosition start, bool precededByLT)
    {
        var begin = _i;
        Advance(); // consume the '.'
        // Fractional digits (guaranteed at least one by caller's lookahead
        // check) — allow `_` separators between digits (§12.9.3), same as
        // ScanNumber's fraction.
        ScanDecimalDigits(start, allowSeparator: true);
        // Optional exponent part: [eE] [+-]? Digits
        if (_i < _src.Length && (_src[_i] == 'e' || _src[_i] == 'E'))
        {
            Advance();
            if (_i < _src.Length && (_src[_i] == '+' || _src[_i] == '-'))
            {
                Advance();
            }

            if (_i >= _src.Length || !IsAsciiDigit(_src[_i]))
            {
                _errors.Report(JsLexError.InvalidNumericLiteral, start, "exponent has no digits");
            }
            // `_` immediately after exponent sign/marker is a SyntaxError.
            if (_i < _src.Length && _src[_i] == '_')
            {
                _errors.Report(JsLexError.InvalidNumericLiteral, start,
                    "numeric separator cannot appear immediately after exponent marker");
                Advance();
            }
            ScanDecimalDigits(start, allowSeparator: true);
        }
        var lex = _src.Slice(begin, _i - begin);
        // Strip separators before numeric conversion (`.0_1e2` -> `.01e2`).
        var lexNoSep = lex.IndexOf('_') >= 0 ? RemoveSeparators(lex) : null;
        var parseSpan = lexNoSep is null ? lex : lexNoSep.AsSpan();
        if (!double.TryParse(parseSpan, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            _errors.Report(JsLexError.InvalidNumericLiteral, start, lex.ToString());
            value = double.NaN;
        }
        return MakeSourceToken(JsTokenKind.NumericLiteral, begin, _i - begin,
            start, CurrentPos(), precededByLT, value);
    }

    private static double ParseRadixDouble(ReadOnlySpan<char> digits, int radix)
    {
        double value = 0;
        foreach (var ch in digits)
        {
            var digit = ch switch
            {
                >= '0' and <= '9' => ch - '0',
                >= 'A' and <= 'F' => ch - 'A' + 10,
                >= 'a' and <= 'f' => ch - 'a' + 10,
                _ => throw new FormatException(),
            };
            if (digit >= radix)
            {
                throw new FormatException();
            }

            value = value * radix + digit;
        }
        return value;
    }

    private static bool IsDigitInRadix(char c, int radix) => radix switch
    {
        2 => c == '0' || c == '1',
        8 => c >= '0' && c <= '7',
        16 => IsAsciiDigit(c) || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f'),
        _ => false,
    };

    // -----------------------------------------------------------------------
    // String literal — §12.9.4
    // -----------------------------------------------------------------------
    private JsToken ScanString(char quote, JsPosition start, bool precededByLT)
    {
        var begin = _i;
        Advance(); // skip opening quote
        var valueBegin = _i;
        StringBuilder? sb = null;
        var legacyOctal = false; // §B.1.2 — a legacy octal / \8 / \9 escape was seen
        while (_i < _src.Length)
        {
            var c = _src[_i];
            if (c == quote)
            {
                Advance();
                if (sb is null && !legacyOctal)
                {
                    return JsToken.StringLiteralNoEscapes(
                        _srcMemory,
                        begin,
                        _i - begin,
                        valueBegin,
                        _i - valueBegin - 1,
                        start,
                        CurrentPos(),
                        precededByLT);
                }
                return MakeSourceToken(JsTokenKind.StringLiteral, begin, _i - begin,
                    start, CurrentPos(), precededByLT, sb?.ToString() ?? string.Empty, legacyOctal);
            }
            if (IsLineTerminator(c))
            {
                sb ??= new StringBuilder(_src.Slice(valueBegin, _i - valueBegin).ToString());
                _errors.Report(JsLexError.UnterminatedString, start,
                    "string literal contains unescaped line terminator");
                return MakeSourceToken(JsTokenKind.Invalid, begin, _i - begin,
                    start, CurrentPos(), precededByLT, sb.ToString());
            }
            if (c == '\\')
            {
                sb ??= new StringBuilder(_src.Slice(valueBegin, _i - valueBegin).ToString());
                Advance();
                if (_i >= _src.Length)
                {
                    _errors.Report(JsLexError.UnterminatedString, start, "string ends in backslash");
                    break;
                }
                sb.Append(ScanEscape(start));
                if (_lastEscapeWasLegacyOctal)
                {
                    legacyOctal = true;
                }

                continue;
            }
            if (sb is not null)
            {
                sb.Append(c);
            }

            Advance();
        }
        sb ??= new StringBuilder(_src.Slice(valueBegin, _i - valueBegin).ToString());
        _errors.Report(JsLexError.UnterminatedString, start, "closing quote not found");
        return MakeSourceToken(JsTokenKind.Invalid, begin, _i - begin,
            start, CurrentPos(), precededByLT, sb.ToString());
    }

    /// <summary>Report an escape-sequence error, or — inside a template body —
    /// merely record that an invalid escape occurred (a NotEscapeSequence is
    /// only fatal in an untagged template, which the parser decides). Returns
    /// the placeholder cooked text the caller should fall back to.</summary>
    private string ReportEscapeError(JsLexError code, JsPosition pos, string message, string placeholder)
    {
        if (_inTemplateBody)
        {
            _lastEscapeWasInvalid = true;
            return placeholder;
        }
        _errors.Report(code, pos, message);
        return placeholder;
    }

    private string ScanEscape(JsPosition start)
    {
        _lastEscapeWasLegacyOctal = false;
        var e = _src[_i];
        Advance();
        switch (e)
        {
            case 'n': return "\n";
            case 'r': return "\r";
            case 't': return "\t";
            case 'b': return "\b";
            case 'f': return "\f";
            case 'v': return "\v";
            case '0' when _i >= _src.Length || !IsAsciiDigit(_src[_i]): return "\0";
            // §B.1.2 LegacyOctalEscapeSequence — `\` followed by an octal digit
            // (incl. `\0` followed by another digit). Decode the octal value
            // (sloppy semantics) and tag it as a strict-mode error. In a template
            // body this form is itself a NotEscapeSequence (no cooked value).
            case >= '0' and <= '7':
                {
                    var v = ScanLegacyOctalEscape(e);
                    if (_inTemplateBody) { _lastEscapeWasInvalid = true; _lastEscapeWasLegacyOctal = false; }
                    return v;
                }
            // §B.1.2 NonOctalDecimalEscapeSequence — `\8` / `\9`. The value is
            // just the digit, but it is a strict-mode error (and a template
            // NotEscapeSequence).
            case '8':
            case '9':
                if (_inTemplateBody) { _lastEscapeWasInvalid = true; return e.ToString(); }
                _lastEscapeWasLegacyOctal = true;
                return e.ToString();
            case '\'': return "'";
            case '"': return "\"";
            case '\\': return "\\";
            case '\n': return "";     // line continuation
            case '\r':
                if (_i < _src.Length && _src[_i] == '\n')
                {
                    Advance();
                }

                return "";
            case 'x':
                return ScanHexEscape(start, 2);
            case 'u':
                if (_i < _src.Length && _src[_i] == '{')
                {
                    Advance();
                    var sb = new StringBuilder();
                    var sawBadHex = false;
                    while (_i < _src.Length && _src[_i] != '}')
                    {
                        if (!IsHex(_src[_i]))
                        {
                            sawBadHex = true;
                            ReportEscapeError(JsLexError.InvalidUnicodeEscape, start, "expected hex digit", "");
                            break;
                        }
                        sb.Append(_src[_i]);
                        Advance();
                    }
                    if (_i < _src.Length && _src[_i] == '}')
                    {
                        Advance();
                    }

                    if (sawBadHex)
                    {
                        return "�";
                    }

                    if (sb.Length == 0)
                    {
                        if (_inTemplateBody)
                        {
                            _lastEscapeWasInvalid = true;
                        }

                        return "";
                    }
                    var code = Convert.ToInt32(sb.ToString(), 16);
                    if (code > 0x10FFFF)
                    {
                        return ReportEscapeError(JsLexError.InvalidUnicodeEscape, start, "code point out of range", "�");
                    }

                    return char.ConvertFromUtf32(code);
                }
                return ScanHexEscape(start, 4);
            default:
                return e.ToString();
        }
    }

    /// <summary>§B.1.2 LegacyOctalEscapeSequence — decode <c>\NNN</c> where the
    /// first octal digit <paramref name="first"/> has already been consumed.
    /// One to three octal digits: if the first digit is 0-3 up to three digits
    /// follow; if 4-7 only one more. Sets <see cref="_lastEscapeWasLegacyOctal"/>
    /// so the surrounding string is flagged a strict-mode error.</summary>
    private string ScanLegacyOctalEscape(char first)
    {
        _lastEscapeWasLegacyOctal = true;
        var value = first - '0';
        var maxMore = first <= '3' ? 2 : 1;
        for (var k = 0; k < maxMore; k++)
        {
            if (_i >= _src.Length || _src[_i] < '0' || _src[_i] > '7')
            {
                break;
            }

            value = value * 8 + (_src[_i] - '0');
            Advance();
        }
        return ((char)value).ToString();
    }

    private string ScanHexEscape(JsPosition start, int digits)
    {
        if (_i + digits > _src.Length)
        {
            return ReportEscapeError(JsLexError.InvalidEscape, start, "truncated hex escape", "�");
        }

        var slice = _src.Slice(_i, digits);
        foreach (var ch in slice)
        {
            if (!IsHex(ch))
            {
                return ReportEscapeError(JsLexError.InvalidEscape, start, "bad hex digit", "�");
            }
        }
        var value = 0;
        foreach (var ch in slice)
        {
            value = (value * 16) + HexDigit(ch);
        }

        for (var k = 0; k < digits; k++)
        {
            Advance();
        }

        return ((char)value).ToString();
    }

    // -----------------------------------------------------------------------
    // Punctuators — §12.8.1
    // -----------------------------------------------------------------------
    private JsToken ScanPunctuator(JsPosition start, bool precededByLT)
    {
        var c = _src[_i];
        char p1 = _i + 1 < _src.Length ? _src[_i + 1] : '\0';
        char p2 = _i + 2 < _src.Length ? _src[_i + 2] : '\0';

        // §12.9.3 DecimalLiteral — ". DecimalDigits ExponentPart?" — a numeric
        // literal may start with a decimal point when followed by an ASCII digit.
        // Must be checked BEFORE the '...' three-char check so that the three-dot
        // case (p1 == '.' && p2 == '.') still falls through to Ellipsis below.
        if (c == '.' && p1 >= '0' && p1 <= '9')
        {
            return ScanLeadingDotNumber(start, precededByLT);
        }

        // 3-char punctuators
        if (c == '=' && p1 == '=' && p2 == '=')
        {
            return Punct(JsTokenKind.EqEqEq, 3, start, precededByLT);
        }

        if (c == '!' && p1 == '=' && p2 == '=')
        {
            return Punct(JsTokenKind.BangEqEq, 3, start, precededByLT);
        }

        if (c == '<' && p1 == '<' && p2 == '=')
        {
            return Punct(JsTokenKind.LtLtEq, 3, start, precededByLT);
        }

        if (c == '>' && p1 == '>' && p2 == '>')
        {
            char p3 = _i + 3 < _src.Length ? _src[_i + 3] : '\0';
            if (p3 == '=')
            {
                return Punct(JsTokenKind.GtGtGtEq, 4, start, precededByLT);
            }

            return Punct(JsTokenKind.GtGtGt, 3, start, precededByLT);
        }
        if (c == '>' && p1 == '>' && p2 == '=')
        {
            return Punct(JsTokenKind.GtGtEq, 3, start, precededByLT);
        }

        if (c == '*' && p1 == '*' && p2 == '=')
        {
            return Punct(JsTokenKind.StarStarEq, 3, start, precededByLT);
        }

        if (c == '&' && p1 == '&' && p2 == '=')
        {
            return Punct(JsTokenKind.AmpAmpEq, 3, start, precededByLT);
        }

        if (c == '|' && p1 == '|' && p2 == '=')
        {
            return Punct(JsTokenKind.PipePipeEq, 3, start, precededByLT);
        }

        if (c == '?' && p1 == '?' && p2 == '=')
        {
            return Punct(JsTokenKind.QuestionQuestionEq, 3, start, precededByLT);
        }

        if (c == '.' && p1 == '.' && p2 == '.')
        {
            return Punct(JsTokenKind.Ellipsis, 3, start, precededByLT);
        }

        // 2-char punctuators
        if (c == '=' && p1 == '=')
        {
            return Punct(JsTokenKind.EqEq, 2, start, precededByLT);
        }

        if (c == '!' && p1 == '=')
        {
            return Punct(JsTokenKind.BangEq, 2, start, precededByLT);
        }

        if (c == '<' && p1 == '=')
        {
            return Punct(JsTokenKind.LtEq, 2, start, precededByLT);
        }

        if (c == '>' && p1 == '=')
        {
            return Punct(JsTokenKind.GtEq, 2, start, precededByLT);
        }

        if (c == '<' && p1 == '<')
        {
            return Punct(JsTokenKind.LtLt, 2, start, precededByLT);
        }

        if (c == '>' && p1 == '>')
        {
            return Punct(JsTokenKind.GtGt, 2, start, precededByLT);
        }

        if (c == '+' && p1 == '+')
        {
            return Punct(JsTokenKind.PlusPlus, 2, start, precededByLT);
        }

        if (c == '-' && p1 == '-')
        {
            return Punct(JsTokenKind.MinusMinus, 2, start, precededByLT);
        }

        if (c == '*' && p1 == '*')
        {
            return Punct(JsTokenKind.StarStar, 2, start, precededByLT);
        }

        if (c == '&' && p1 == '&')
        {
            return Punct(JsTokenKind.AmpAmp, 2, start, precededByLT);
        }

        if (c == '|' && p1 == '|')
        {
            return Punct(JsTokenKind.PipePipe, 2, start, precededByLT);
        }

        if (c == '?' && p1 == '?')
        {
            return Punct(JsTokenKind.QuestionQuestion, 2, start, precededByLT);
        }
        // §12.10 OptionalChainingPunctuator: `?.` is optional-chaining ONLY when
        // not immediately followed by a decimal digit, so `x ? .5 : y` stays a
        // conditional with a leading-dot number, and `a?.b` / `a?.[i]` / `a?.(x)`
        // still chain. `?.` + digit falls through to a `?` Question token.
        if (c == '?' && p1 == '.' && !(p2 >= '0' && p2 <= '9'))
        {
            return Punct(JsTokenKind.QuestionDot, 2, start, precededByLT);
        }

        if (c == '=' && p1 == '>')
        {
            return Punct(JsTokenKind.Arrow, 2, start, precededByLT);
        }

        if (c == '+' && p1 == '=')
        {
            return Punct(JsTokenKind.PlusEq, 2, start, precededByLT);
        }

        if (c == '-' && p1 == '=')
        {
            return Punct(JsTokenKind.MinusEq, 2, start, precededByLT);
        }

        if (c == '*' && p1 == '=')
        {
            return Punct(JsTokenKind.StarEq, 2, start, precededByLT);
        }

        if (c == '/' && p1 == '=')
        {
            return Punct(JsTokenKind.SlashEq, 2, start, precededByLT);
        }

        if (c == '%' && p1 == '=')
        {
            return Punct(JsTokenKind.PercentEq, 2, start, precededByLT);
        }

        if (c == '&' && p1 == '=')
        {
            return Punct(JsTokenKind.AmpEq, 2, start, precededByLT);
        }

        if (c == '|' && p1 == '=')
        {
            return Punct(JsTokenKind.PipeEq, 2, start, precededByLT);
        }

        if (c == '^' && p1 == '=')
        {
            return Punct(JsTokenKind.CaretEq, 2, start, precededByLT);
        }

        // 1-char punctuators
        var k = c switch
        {
            '{' => JsTokenKind.LBrace,
            '}' => JsTokenKind.RBrace,
            '(' => JsTokenKind.LParen,
            ')' => JsTokenKind.RParen,
            '[' => JsTokenKind.LBracket,
            ']' => JsTokenKind.RBracket,
            '.' => JsTokenKind.Dot,
            ';' => JsTokenKind.Semicolon,
            ',' => JsTokenKind.Comma,
            '<' => JsTokenKind.Lt,
            '>' => JsTokenKind.Gt,
            '+' => JsTokenKind.Plus,
            '-' => JsTokenKind.Minus,
            '*' => JsTokenKind.Star,
            '/' => JsTokenKind.Slash,
            '%' => JsTokenKind.Percent,
            '&' => JsTokenKind.Amp,
            '|' => JsTokenKind.Pipe,
            '^' => JsTokenKind.Caret,
            '~' => JsTokenKind.Tilde,
            '!' => JsTokenKind.Bang,
            '?' => JsTokenKind.Question,
            ':' => JsTokenKind.Colon,
            '=' => JsTokenKind.Eq,
            _ => JsTokenKind.Invalid,
        };
        if (k == JsTokenKind.Invalid)
        {
            _errors.Report(JsLexError.InvalidCharacter, start, $"unexpected character '{c}' (U+{(int)c:X4})");
        }

        return Punct(k, 1, start, precededByLT);
    }

    private JsToken Punct(JsTokenKind kind, int len, JsPosition start, bool precededByLT)
    {
        var begin = _i;
        for (var k = 0; k < len; k++)
        {
            Advance();
        }

        return MakeSourceToken(kind, begin, len, start, CurrentPos(), precededByLT);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    private JsPosition CurrentPos() => new(_line, _col, _i);

    private void Advance()
    {
        // For single-char positions (no line terminators here — those are
        // handled separately in SkipWhitespaceAndComments / SkipBlockComment).
        _col++;
        _i++;
    }

    /// <summary>Advance without bumping column — used when a CR is the first
    /// half of a CRLF and the LF half will run the normal advance path.</summary>
    private void AdvanceRaw() { _i++; }

    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

    private static bool IsHex(char c)
        => IsAsciiDigit(c) || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

    private static bool IsWhitespace(char c)
    {
        // ECMAScript WhiteSpace (spec section 12.2): TAB, VT, FF, ZWNBSP, and
        // every code point in Unicode general category "Space_Separator" (Zs):
        // NBSP (U+00A0), the en/em/thin spaces (U+2000 to U+200A), NNBSP
        // (U+202F), medium mathematical space (U+205F), ideographic space
        // (U+3000), and Ogham space mark (U+1680). ASCII fast path first to
        // keep the lexer loop cheap for the common cases.
        if (c == ' ' || c == '\t' || c == '\v' || c == '\f')
        {
            return true;
        }

        if (c < 0x80)
        {
            return false;
        }

        if (c == '\uFEFF')
        {
            return true; // ZWNBSP is not in Zs
        }

        return CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator;
    }

    private static bool IsLineTerminator(char c)
        => c == '\n'
        || c == '\r'
        || c == '\u2028'   // LINE SEPARATOR
        || c == '\u2029';


    /// <summary>ASCII identifier start + non-ASCII letters per UnicodeCategory.
    /// Full IdentifierStart per spec §12.6 is more permissive; this is enough
    /// for the cases this slice covers.</summary>
    private static bool IsIdStart(char c)
    {
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
        {
            return true;
        }

        if (c == '_' || c == '$')
        {
            return true;
        }

        if (c < 0x80)
        {
            return false;
        }

        if (IsOtherIdStart(c))
        {
            return true; // \u00A712.6 Other_ID_Start
        }
        // U+2E2F VERTICAL TILDE is a ModifierLetter but is in Unicode
        // Pattern_Syntax, so it is excluded from ID_Start/ID_Continue (\u00A712.6).
        if (c == '\u2E2F')
        {
            return false;
        }

        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat is UnicodeCategory.UppercaseLetter
            or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter
            or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter
            or UnicodeCategory.LetterNumber;
    }

    private static bool IsIdPart(char c)
    {
        if (IsIdStart(c))
        {
            return true;
        }

        if (c >= '0' && c <= '9')
        {
            return true;
        }

        if (c == '\u200C' || c == '\u200D')
        {
            return true; // ZWNJ/ZWJ
        }

        if (IsOtherIdContinue(c))
        {
            return true; // \u00A712.6 Other_ID_Continue
        }

        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.ConnectorPunctuation;
    }

    /// <summary>ECMAScript \u00A712.6 IdentifierStartChar includes the Unicode
    /// <c>Other_ID_Start</c> property code points, which are NOT in the letter
    /// categories tested above. Enumerate them explicitly.</summary>
    private static bool IsOtherIdStart(char c)
        => c is '\u1885'   // MONGOLIAN LETTER ALI GALI BALUDA
            or '\u1886'    // MONGOLIAN LETTER ALI GALI THREE BALUDA
            or '\u2118'    // SCRIPT CAPITAL P (U+2118 wp / weierstrass p)
            or '\u212E'    // ESTIMATED SYMBOL
            or '\u309B'    // KATAKANA-HIRAGANA VOICED SOUND MARK
            or '\u309C';   // KATAKANA-HIRAGANA SEMI-VOICED SOUND MARK

    /// <summary>ECMAScript \u00A712.6 IdentifierPartChar includes the Unicode
    /// <c>Other_ID_Continue</c> property code points (plus the
    /// Other_ID_Start set), none of which are in the mark/number/punctuation
    /// categories tested above. Enumerate the Other_ID_Continue set here.</summary>
    private static bool IsOtherIdContinue(char c)
        => c is '\u00B7'   // MIDDLE DOT (\u00B7)
            or '\u0387'    // GREEK ANO TELEIA
            or '\u1369'    // ETHIOPIC DIGIT ONE
            or '\u136A'
            or '\u136B'
            or '\u136C'
            or '\u136D'
            or '\u136E'
            or '\u136F'
            or '\u1370'
            or '\u1371'    // ETHIOPIC DIGIT NINE
            or '\u19DA'    // NEW TAI LUE THAM DIGIT ONE
            or '\u30FB'    // KATAKANA MIDDLE DOT (U+30FB)
            or '\uFF65';   // HALFWIDTH KATAKANA MIDDLE DOT (U+FF65)

    // ----- §12.6 IdentifierName UnicodeEscapeSequence support -----------------

    private static int HexDigit(char c) =>
        c >= '0' && c <= '9' ? c - '0'
        : c >= 'a' && c <= 'f' ? c - 'a' + 10
        : c >= 'A' && c <= 'F' ? c - 'A' + 10
        : -1;

    /// <summary>Code-point-aware IdentifierStart test (handles astral code
    /// points produced by a <c>\u{...}</c> escape).</summary>
    private static bool IsIdStartCp(int cp)
    {
        if (cp <= 0xFFFF)
        {
            return IsIdStart((char)cp);
        }

        var cat = CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(cp), 0);
        return cat is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
    }

    /// <summary>Code-point-aware IdentifierPart test.</summary>
    private static bool IsIdPartCp(int cp)
    {
        if (cp <= 0xFFFF)
        {
            return IsIdPart((char)cp);
        }

        var cat = CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(cp), 0);
        return IsIdStartCp(cp) || cat is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.DecimalDigitNumber
            or UnicodeCategory.ConnectorPunctuation;
    }

    /// <summary>When the two source units at <paramref name="pos"/> form a
    /// well-formed UTF-16 surrogate pair, decode it and report whether the
    /// astral code point is a valid IdentifierStart/Part (per <paramref name="first"/>).
    /// Returns true and sets <paramref name="cp"/> on a match; otherwise false.
    /// Astral identifier characters are written literally as surrogate pairs in
    /// the source, so the single-<c>char</c> tests above never see them.</summary>
    private bool TryAstralIdChar(int pos, bool first, out int cp)
    {
        cp = -1;
        if (pos + 1 >= _src.Length)
        {
            return false;
        }

        var hi = _src[pos];
        var lo = _src[pos + 1];
        if (!char.IsHighSurrogate(hi) || !char.IsLowSurrogate(lo))
        {
            return false;
        }

        cp = char.ConvertToUtf32(hi, lo);
        return first ? IsIdStartCp(cp) : IsIdPartCp(cp);
    }

    /// <summary>Peek a <c>\uXXXX</c> or <c>\u{...}</c> escape starting at
    /// <paramref name="pos"/> (which must point at the backslash) WITHOUT
    /// consuming. Returns the decoded code point and sets <paramref name="len"/>
    /// to the number of source chars it spans; returns -1 when the slice is not
    /// a well-formed unicode escape.</summary>
    private int PeekUnicodeEscape(int pos, out int len)
    {
        len = 0;
        if (pos + 1 >= _src.Length || _src[pos] != '\\' || _src[pos + 1] != 'u')
        {
            return -1;
        }

        var p = pos + 2;
        if (p < _src.Length && _src[p] == '{')
        {
            p++;
            int val = 0; var any = false;
            while (p < _src.Length && _src[p] != '}')
            {
                var d = HexDigit(_src[p]); if (d < 0)
                {
                    return -1;
                }

                val = val * 16 + d; if (val > 0x10FFFF)
                {
                    return -1;
                }

                any = true; p++;
            }
            if (!any || p >= _src.Length || _src[p] != '}')
            {
                return -1;
            }

            len = (p - pos) + 1;
            return val;
        }
        if (p + 4 > _src.Length)
        {
            return -1;
        }

        int v = 0;
        for (var k = 0; k < 4; k++) { var d = HexDigit(_src[p + k]); if (d < 0) { return -1; } v = v * 16 + d; }
        len = (p + 4) - pos;
        return v;
    }

    /// <summary>True when the source at <paramref name="pos"/> begins a
    /// <c>\u</c> escape whose code point is a valid IdentifierStart.</summary>
    private bool StartsIdentifierEscape(int pos)
    {
        var cp = PeekUnicodeEscape(pos, out _);
        return cp >= 0 && IsIdStartCp(cp);
    }

    private JsToken MakeSourceToken(
        JsTokenKind kind, int offset, int length, JsPosition start, JsPosition end,
        bool precededByLT, object? value = null, bool legacyOctal = false,
        bool containsEscape = false, bool invalidEscape = false)
        => JsToken.FromSource(kind, _srcMemory, offset, length, start, end, precededByLT,
            value, legacyOctal, containsEscape, invalidEscape);

    private static JsToken MakeToken(
        JsTokenKind kind, ReadOnlyMemory<char> lexeme, JsPosition start, JsPosition end,
        bool precededByLT, object? value = null, bool legacyOctal = false,
        bool containsEscape = false, bool invalidEscape = false)
        => JsToken.FromDecodedText(kind, lexeme, start, end, precededByLT,
            value, legacyOctal, containsEscape, invalidEscape);

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
}
