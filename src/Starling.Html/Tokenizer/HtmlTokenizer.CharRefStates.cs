namespace Starling.Html.Tokenizer;

/// <summary>
/// Character-reference states from WHATWG HTML §13.2.5.72–.80. Resolves
/// <c>&amp;name;</c>, <c>&amp;#NNN;</c>, and
/// <c>&amp;#xHHHH;</c> forms; falls back to ambiguous-ampersand for
/// unrecognized inputs (emits the literal chars, matching browser behavior
/// on typo'd entities).
/// </summary>
/// <remarks>
/// A character reference can be entered from any of: Data, Rcdata,
/// AttributeValueDoubleQuoted, AttributeValueSingleQuoted, AttributeValueUnquoted.
/// The entry state stores itself in <c>_returnState</c>; the helpers
/// <see cref="FlushBufferedAsCharacters"/> and <see cref="IsReturnStateAttribute"/>
/// route emit-vs-append based on it.
/// </remarks>
public sealed partial class HtmlTokenizer
{
    private void DispatchCharRefState(TokenizerState state, int c)
    {
        switch (state)
        {
            case TokenizerState.CharacterReference: StepCharRef(c); break;
            case TokenizerState.NamedCharacterReference: StepNamedCharRef(c); break;
            case TokenizerState.AmbiguousAmpersand: StepAmbiguousAmp(c); break;
            case TokenizerState.NumericCharacterReference: StepNumCharRef(c); break;
            case TokenizerState.HexadecimalCharacterReferenceStart: StepHexStart(c); break;
            case TokenizerState.DecimalCharacterReferenceStart: StepDecStart(c); break;
            case TokenizerState.HexadecimalCharacterReference: StepHex(c); break;
            case TokenizerState.DecimalCharacterReference: StepDec(c); break;
            case TokenizerState.NumericCharacterReferenceEnd: StepNumEnd(c); break;
            default:
                throw new InvalidOperationException(
                $"DispatchCharRefState invoked for unrelated state '{state}'.");
        }
    }

    private void StepCharRefEof()
    {
        switch (_state)
        {
            case TokenizerState.NumericCharacterReference:
            case TokenizerState.HexadecimalCharacterReferenceStart:
            case TokenizerState.DecimalCharacterReferenceStart:
                _errors.Report(HtmlParseError.AbsenceOfDigitsInNumericCharacterReference,
                    _line, _column);
                FlushBufferedAsCharacters();
                break;
            case TokenizerState.HexadecimalCharacterReference:
            case TokenizerState.DecimalCharacterReference:
                _errors.Report(HtmlParseError.MissingSemicolonAfterCharacterReference,
                    _line, _column);
                FinishNumericRef(reconsume: -1);
                break;
            default:
                FlushBufferedAsCharacters();
                break;
        }

        _emitted.Enqueue(EndOfFileToken.Instance);
    }

    private bool IsReturnStateAttribute()
        => _returnState == TokenizerState.AttributeValueDoubleQuoted
        || _returnState == TokenizerState.AttributeValueSingleQuoted
        || _returnState == TokenizerState.AttributeValueUnquoted;

    /// <summary>Flush <c>_tempBuffer</c> contents either as character tokens
    /// or appended to the current attribute value, depending on the return
    /// state.</summary>
    private void FlushBufferedAsCharacters()
    {
        if (IsReturnStateAttribute())
        {
            _attrValue.Append(_tempBuffer);
        }
        else
        {
            for (var i = 0; i < _tempBuffer.Length; i++)
            {
                _emitted.Enqueue(new CharacterToken(_tempBuffer[i]));
            }
        }

        _tempBuffer.Clear();
    }

    private void EmitDecodedCodePoint(int cp)
    {
        if (IsReturnStateAttribute())
        {
            if (cp <= 0xFFFF)
            {
                _attrValue.Append((char)cp);
            }
            else
            {
                _attrValue.Append(char.ConvertFromUtf32(cp));
            }
        }
        else
        {
            _emitted.Enqueue(new CharacterToken(cp));
        }
    }

    // -----------------------------------------------------------------------
    // 13.2.5.72 Character reference state
    // -----------------------------------------------------------------------
    private void StepCharRef(int c)
    {
        // _tempBuffer already starts with '&' (the entry states append it).
        if (IsAsciiAlphanumeric(c))
        {
            Reconsume(c, TokenizerState.NamedCharacterReference);
            return;
        }
        if (c == '#')
        {
            _tempBuffer.Append('#');
            _state = TokenizerState.NumericCharacterReference;
            return;
        }
        FlushBufferedAsCharacters();
        Reconsume(c, _returnState);
    }

    // -----------------------------------------------------------------------
    // 13.2.5.73 Named character reference state
    //
    // Spec: "Consume the maximum number of characters possible, where the
    // consumed characters are one of the identifiers". Implementation uses
    // peek-only against the stream and advances by exactly the matched
    // length, leaving the rest in the stream for subsequent states.
    // -----------------------------------------------------------------------
    private void StepNamedCharRef(int c)
    {
        // c is the first alphanumeric char after '&', already consumed.
        // _tempBuffer currently holds "&". Build a candidate "c + peeks".
        var candidate = new System.Text.StringBuilder();
        candidate.Append((char)c);
        for (var i = 0; i < 31; i++)
        {
            var pk = _stream.PeekAt(i);
            if (pk == -1)
            {
                break;
            }

            if (!IsAsciiAlphanumeric(pk) && pk != ';')
            {
                break;
            }

            candidate.Append((char)pk);
            if (pk == ';')
            {
                break;
            }
        }

        var span = candidate.ToString();
        NamedCharacterReferences.Match? best = null;
        for (var len = span.Length; len > 0; len--)
        {
            var m = NamedCharacterReferences.FindLongest(span.AsSpan(0, len));
            if (m is not null) { best = m; break; }
        }

        if (best is null)
        {
            // No match. Per spec §13.2.5.73: "Flush code points consumed as
            // a character reference. Switch to the ambiguous ampersand state."
            // The temp buffer has '&' plus c; flush both, then let
            // AmbiguousAmpersand continue from the next input char.
            _tempBuffer.Append((char)c);
            FlushBufferedAsCharacters();
            _state = TokenizerState.AmbiguousAmpersand;
            return;
        }

        // best.Length includes c (the first matched char). Advance the
        // stream past the remaining matched chars.
        var matchLen = best.Value.Length;
        _stream.Advance(matchLen - 1);
        for (var i = 0; i < matchLen - 1; i++)
        {
            TrackPosition(span[i + 1]);
        }

        var matched = span[..matchLen];
        var lastIsSemicolon = matched.EndsWith(';');

        // Historical attribute-value quirk (§13.2.5.73): if the return state
        // is an attribute value AND the match doesn't end in ';' AND the
        // next input char is '=' or alphanumeric, treat the whole thing
        // as literal text. Append '&' + matched to attribute, return.
        if (IsReturnStateAttribute() && !lastIsSemicolon)
        {
            var next = _stream.PeekAt(0);
            if (next == '=' || IsAsciiAlphanumeric(next))
            {
                _tempBuffer.Append(matched);
                FlushBufferedAsCharacters();
                _state = _returnState;
                return;
            }
        }

        if (!lastIsSemicolon)
        {
            _errors.Report(HtmlParseError.MissingSemicolonAfterCharacterReference,
                _line, _column);
        }

        _tempBuffer.Clear();
        EmitDecodedCodePoint(best.Value.CodePoint1);
        if (best.Value.CodePoint2 is int cp2)
        {
            EmitDecodedCodePoint(cp2);
        }

        _state = _returnState;
    }

    // -----------------------------------------------------------------------
    // 13.2.5.74 Ambiguous ampersand state
    // -----------------------------------------------------------------------
    private void StepAmbiguousAmp(int c)
    {
        if (IsAsciiAlphanumeric(c))
        {
            if (IsReturnStateAttribute())
            {
                _attrValue.Append((char)c);
            }
            else
            {
                _emitted.Enqueue(new CharacterToken(c));
            }

            return;
        }
        if (c == ';')
        {
            _errors.Report(HtmlParseError.UnknownNamedCharacterReference, _line, _column);
            Reconsume(c, _returnState);
            return;
        }
        Reconsume(c, _returnState);
    }

    // -----------------------------------------------------------------------
    // 13.2.5.75 Numeric character reference state
    // -----------------------------------------------------------------------
    private void StepNumCharRef(int c)
    {
        _charRefCode = 0;
        _charRefOverflowed = false;
        if (c == 'x' || c == 'X')
        {
            _tempBuffer.Append((char)c);
            _state = TokenizerState.HexadecimalCharacterReferenceStart;
            return;
        }
        Reconsume(c, TokenizerState.DecimalCharacterReferenceStart);
    }

    // 13.2.5.76 Hex char ref start state
    private void StepHexStart(int c)
    {
        if (IsAsciiHexDigit(c))
        {
            Reconsume(c, TokenizerState.HexadecimalCharacterReference);
            return;
        }
        _errors.Report(HtmlParseError.AbsenceOfDigitsInNumericCharacterReference,
            _line, _column);
        FlushBufferedAsCharacters();
        Reconsume(c, _returnState);
    }

    // 13.2.5.77 Decimal char ref start state
    private void StepDecStart(int c)
    {
        if (c >= '0' && c <= '9')
        {
            Reconsume(c, TokenizerState.DecimalCharacterReference);
            return;
        }
        _errors.Report(HtmlParseError.AbsenceOfDigitsInNumericCharacterReference,
            _line, _column);
        FlushBufferedAsCharacters();
        Reconsume(c, _returnState);
    }

    // 13.2.5.78 Hex char ref state
    private void StepHex(int c)
    {
        if (c >= '0' && c <= '9') { Accum(c - '0', 16); return; }
        if (c >= 'A' && c <= 'F') { Accum(c - 'A' + 10, 16); return; }
        if (c >= 'a' && c <= 'f') { Accum(c - 'a' + 10, 16); return; }
        if (c == ';')
        {
            FinishNumericRef(reconsume: -1);
            return;
        }
        _errors.Report(HtmlParseError.MissingSemicolonAfterCharacterReference,
            _line, _column);
        FinishNumericRef(reconsume: c);
    }

    // 13.2.5.79 Decimal char ref state
    private void StepDec(int c)
    {
        if (c >= '0' && c <= '9') { Accum(c - '0', 10); return; }
        if (c == ';')
        {
            FinishNumericRef(reconsume: -1);
            return;
        }
        _errors.Report(HtmlParseError.MissingSemicolonAfterCharacterReference,
            _line, _column);
        FinishNumericRef(reconsume: c);
    }

    private void Accum(int digit, int radix)
    {
        // Saturate at 0x110000 to detect overflow without unbounded growth.
        if (_charRefOverflowed)
        {
            return;
        }

        _charRefCode = (uint)(_charRefCode * radix + digit);
        if (_charRefCode > 0x10FFFF)
        {
            _charRefOverflowed = true;
            _charRefCode = 0x10FFFF + 1; // sentinel; classified below
        }
    }

    /// <summary>
    /// 13.2.5.80 Numeric character reference end — the state runs without
    /// consuming a character. Called directly from <c>StepHex</c>/<c>StepDec</c>
    /// on the transition ';' or on the parse-error fall-through. If
    /// <paramref name="reconsume"/> is &gt;= 0, that char is reconsumed in
    /// the return state after the decoded code point is emitted.
    /// </summary>
    private void FinishNumericRef(int reconsume)
    {
        var code = (int)_charRefCode;
        if (code == 0)
        {
            _errors.Report(HtmlParseError.NullCharacterReference, _line, _column);
            code = 0xFFFD;
        }
        else if (code > 0x10FFFF || _charRefOverflowed)
        {
            _errors.Report(HtmlParseError.CharacterReferenceOutsideUnicodeRange,
                _line, _column);
            code = 0xFFFD;
        }
        else if (code >= 0xD800 && code <= 0xDFFF)
        {
            _errors.Report(HtmlParseError.SurrogateCharacterReference, _line, _column);
            code = 0xFFFD;
        }
        else if (IsNonCharacter(code))
        {
            _errors.Report(HtmlParseError.NoncharacterCharacterReference, _line, _column);
            // No substitution — parse error reported, code point passes through.
        }
        else if (code == 0x0D
            || (code <= 0x1F && code != 0x09 && code != 0x0A && code != 0x0C && code != 0x20)
            || (code >= 0x7F && code <= 0x9F))
        {
            _errors.Report(HtmlParseError.ControlCharacterReference, _line, _column);
            // Apply the spec's C1 → Unicode substitution table.
            code = C1Replacement(code);
        }

        _tempBuffer.Clear();
        EmitDecodedCodePoint(code);
        _state = _returnState;
        if (reconsume >= 0)
        {
            _reconsume = reconsume;
        }
    }

    /// <summary>Dispatcher hook — unreachable in practice since
    /// <see cref="FinishNumericRef"/> handles the work directly.</summary>
    private void StepNumEnd(int c) => FinishNumericRef(reconsume: c);

    // Spec §13.2.5.80 has a fixed table mapping the C1 controls + 0x80 etc.
    // to Windows-1252-style replacements. Implemented inline.
    private static int C1Replacement(int code) => code switch
    {
        0x80 => 0x20AC,
        0x82 => 0x201A,
        0x83 => 0x0192,
        0x84 => 0x201E,
        0x85 => 0x2026,
        0x86 => 0x2020,
        0x87 => 0x2021,
        0x88 => 0x02C6,
        0x89 => 0x2030,
        0x8A => 0x0160,
        0x8B => 0x2039,
        0x8C => 0x0152,
        0x8E => 0x017D,
        0x91 => 0x2018,
        0x92 => 0x2019,
        0x93 => 0x201C,
        0x94 => 0x201D,
        0x95 => 0x2022,
        0x96 => 0x2013,
        0x97 => 0x2014,
        0x98 => 0x02DC,
        0x99 => 0x2122,
        0x9A => 0x0161,
        0x9B => 0x203A,
        0x9C => 0x0153,
        0x9E => 0x017E,
        0x9F => 0x0178,
        _ => code,
    };

    private static bool IsNonCharacter(int cp)
    {
        // U+FDD0..U+FDEF and any code point ending in FFFE/FFFF
        if (cp >= 0xFDD0 && cp <= 0xFDEF)
        {
            return true;
        }

        var low = cp & 0xFFFF;
        return low == 0xFFFE || low == 0xFFFF;
    }

    private static bool IsAsciiAlphanumeric(int c)
        => (c >= '0' && c <= '9') || IsAsciiAlpha(c);

    private static bool IsAsciiHexDigit(int c)
        => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');
}
