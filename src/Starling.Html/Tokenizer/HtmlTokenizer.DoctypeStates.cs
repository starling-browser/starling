namespace Starling.Html.Tokenizer;

/// <summary>
/// DOCTYPE states from WHATWG HTML §13.2.5.53–.68.
/// </summary>
/// <remarks>
/// The tokenizer builds a doctype incrementally via:
/// <list type="bullet">
///   <item><c>_doctypeName</c> — appended in <c>DoctypeName</c>.</item>
///   <item><c>_doctypePublicId</c> — appended in the two
///         <c>DoctypePublicIdentifier*Quoted</c> states.</item>
///   <item><c>_doctypeSystemId</c> — appended in the two
///         <c>DoctypeSystemIdentifier*Quoted</c> states.</item>
///   <item><c>_doctypeForceQuirks</c> — set on every "set the doctype token's
///         force-quirks flag to on" branch in the spec, including all
///         EOF-in-doctype paths and various parse-error fallthroughs.</item>
/// </list>
/// Each <c>*Set</c> flag distinguishes "an empty string was seen" from
/// "the field is absent" so the emitted token preserves the spec's
/// distinction (e.g. <c>&lt;!DOCTYPE html PUBLIC ""&gt;</c> has PublicId
/// equal to <c>""</c>, not <c>null</c>).
/// </remarks>
public sealed partial class HtmlTokenizer
{
    private void DispatchDoctypeState(TokenizerState state, int c)
    {
        switch (state)
        {
            case TokenizerState.Doctype: StepDoctype(c); break;
            case TokenizerState.BeforeDoctypeName: StepBeforeDoctypeName(c); break;
            case TokenizerState.DoctypeName: StepDoctypeName(c); break;
            case TokenizerState.AfterDoctypeName: StepAfterDoctypeName(c); break;
            case TokenizerState.AfterDoctypePublicKeyword: StepAfterDoctypePublicKw(c); break;
            case TokenizerState.BeforeDoctypePublicIdentifier: StepBeforeDoctypePublicId(c); break;
            case TokenizerState.DoctypePublicIdentifierDoubleQuoted: StepDoctypePublicIdDQ(c); break;
            case TokenizerState.DoctypePublicIdentifierSingleQuoted: StepDoctypePublicIdSQ(c); break;
            case TokenizerState.AfterDoctypePublicIdentifier: StepAfterDoctypePublicId(c); break;
            case TokenizerState.BetweenDoctypePublicAndSystemIdentifiers: StepBetweenDoctypePublicSystem(c); break;
            case TokenizerState.AfterDoctypeSystemKeyword: StepAfterDoctypeSystemKw(c); break;
            case TokenizerState.BeforeDoctypeSystemIdentifier: StepBeforeDoctypeSystemId(c); break;
            case TokenizerState.DoctypeSystemIdentifierDoubleQuoted: StepDoctypeSystemIdDQ(c); break;
            case TokenizerState.DoctypeSystemIdentifierSingleQuoted: StepDoctypeSystemIdSQ(c); break;
            case TokenizerState.AfterDoctypeSystemIdentifier: StepAfterDoctypeSystemId(c); break;
            case TokenizerState.BogusDoctype: StepBogusDoctype(c); break;
            default:
                throw new InvalidOperationException(
                $"DispatchDoctypeState invoked for unrelated state '{state}'.");
        }
    }

    private void StartDoctype()
    {
        _doctypeName.Clear();
        _doctypeNameSet = false;
        _doctypePublicId.Clear();
        _doctypePublicIdSet = false;
        _doctypeSystemId.Clear();
        _doctypeSystemIdSet = false;
        _doctypeForceQuirks = false;
    }

    private void EmitDoctype()
    {
        _emitted.Enqueue(new DoctypeToken(
            Name: _doctypeNameSet ? _doctypeName.ToString() : null,
            PublicId: _doctypePublicIdSet ? _doctypePublicId.ToString() : null,
            SystemId: _doctypeSystemIdSet ? _doctypeSystemId.ToString() : null,
            ForceQuirks: _doctypeForceQuirks));
        _doctypeName.Clear();
        _doctypePublicId.Clear();
        _doctypeSystemId.Clear();
        _doctypeNameSet = _doctypePublicIdSet = _doctypeSystemIdSet = false;
    }

    private void StepDoctypeEof()
    {
        if (_state == TokenizerState.BogusDoctype)
        {
            EmitDoctype();
            _emitted.Enqueue(EndOfFileToken.Instance);
            return;
        }
        // All non-bogus doctype states on EOF: parse error, force quirks, emit.
        _errors.Report(HtmlParseError.EofInDoctype, _line, _column);
        _doctypeForceQuirks = true;
        EmitDoctype();
        _emitted.Enqueue(EndOfFileToken.Instance);
    }

    // -----------------------------------------------------------------------
    // 13.2.5.53 DOCTYPE state
    // -----------------------------------------------------------------------
    private void StepDoctype(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            _state = TokenizerState.BeforeDoctypeName;
            return;
        }
        if (c == '>')
        {
            Reconsume(c, TokenizerState.BeforeDoctypeName);
            return;
        }
        _errors.Report(HtmlParseError.MissingWhitespaceBeforeDoctypeName, _line, _column);
        Reconsume(c, TokenizerState.BeforeDoctypeName);
    }

    // 13.2.5.54 Before DOCTYPE name state
    private void StepBeforeDoctypeName(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            return;
        }

        if (IsAsciiUpper(c))
        {
            StartDoctype();
            _doctypeName.Append((char)(c + 0x20));
            _doctypeNameSet = true;
            _state = TokenizerState.DoctypeName;
            return;
        }
        if (c == 0)
        {
            _errors.Report(HtmlParseError.UnexpectedNullCharacter, _line, _column);
            StartDoctype();
            _doctypeName.Append((char)0xFFFD);
            _doctypeNameSet = true;
            _state = TokenizerState.DoctypeName;
            return;
        }
        if (c == '>')
        {
            _errors.Report(HtmlParseError.MissingDoctypeName, _line, _column);
            StartDoctype();
            _doctypeForceQuirks = true;
            _state = TokenizerState.Data;
            EmitDoctype();
            return;
        }
        StartDoctype();
        _doctypeName.Append((char)c);
        _doctypeNameSet = true;
        _state = TokenizerState.DoctypeName;
    }

    // 13.2.5.55 DOCTYPE name state
    private void StepDoctypeName(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            _state = TokenizerState.AfterDoctypeName;
            return;
        }
        if (c == '>')
        {
            _state = TokenizerState.Data;
            EmitDoctype();
            return;
        }
        if (IsAsciiUpper(c))
        {
            _doctypeName.Append((char)(c + 0x20));
            return;
        }
        if (c == 0)
        {
            _errors.Report(HtmlParseError.UnexpectedNullCharacter, _line, _column);
            _doctypeName.Append((char)0xFFFD);
            return;
        }
        _doctypeName.Append((char)c);
    }

    // 13.2.5.56 After DOCTYPE name state
    private void StepAfterDoctypeName(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            return;
        }

        if (c == '>')
        {
            _state = TokenizerState.Data;
            EmitDoctype();
            return;
        }
        // Look for "PUBLIC" or "SYSTEM" via small lookahead. Each
        // upcoming char passes through this state until we see a non-letter
        // (or '>') which decides the branch. To minimize state additions
        // we use _tempBuffer as we did in MarkupDeclarationOpen.
        _tempBuffer.Append((char)c);
        var p = _tempBuffer.ToString();
        var publicViable = "PUBLIC".StartsWith(p, StringComparison.OrdinalIgnoreCase);
        var systemViable = "SYSTEM".StartsWith(p, StringComparison.OrdinalIgnoreCase);

        if (publicViable && p.Length == 6)
        {
            _tempBuffer.Clear();
            _state = TokenizerState.AfterDoctypePublicKeyword;
            return;
        }
        if (systemViable && p.Length == 6)
        {
            _tempBuffer.Clear();
            _state = TokenizerState.AfterDoctypeSystemKeyword;
            return;
        }
        if (publicViable || systemViable)
        {
            return; // need more
        }

        _errors.Report(HtmlParseError.InvalidCharacterSequenceAfterDoctypeName, _line, _column);
        _doctypeForceQuirks = true;
        _tempBuffer.Clear();
        Reconsume(c, TokenizerState.BogusDoctype);
    }

    // 13.2.5.57 After DOCTYPE public keyword state
    private void StepAfterDoctypePublicKw(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            _state = TokenizerState.BeforeDoctypePublicIdentifier;
            return;
        }
        switch (c)
        {
            case '"':
                _errors.Report(HtmlParseError.MissingWhitespaceAfterDoctypePublicKeyword, _line, _column);
                _doctypePublicId.Clear();
                _doctypePublicIdSet = true;
                _state = TokenizerState.DoctypePublicIdentifierDoubleQuoted;
                return;
            case '\'':
                _errors.Report(HtmlParseError.MissingWhitespaceAfterDoctypePublicKeyword, _line, _column);
                _doctypePublicId.Clear();
                _doctypePublicIdSet = true;
                _state = TokenizerState.DoctypePublicIdentifierSingleQuoted;
                return;
            case '>':
                _errors.Report(HtmlParseError.MissingDoctypePublicIdentifier, _line, _column);
                _doctypeForceQuirks = true;
                _state = TokenizerState.Data;
                EmitDoctype();
                return;
        }
        _errors.Report(HtmlParseError.MissingQuoteBeforeDoctypePublicIdentifier, _line, _column);
        _doctypeForceQuirks = true;
        Reconsume(c, TokenizerState.BogusDoctype);
    }

    // 13.2.5.58 Before DOCTYPE public identifier state
    private void StepBeforeDoctypePublicId(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            return;
        }

        switch (c)
        {
            case '"':
                _doctypePublicId.Clear();
                _doctypePublicIdSet = true;
                _state = TokenizerState.DoctypePublicIdentifierDoubleQuoted;
                return;
            case '\'':
                _doctypePublicId.Clear();
                _doctypePublicIdSet = true;
                _state = TokenizerState.DoctypePublicIdentifierSingleQuoted;
                return;
            case '>':
                _errors.Report(HtmlParseError.MissingDoctypePublicIdentifier, _line, _column);
                _doctypeForceQuirks = true;
                _state = TokenizerState.Data;
                EmitDoctype();
                return;
        }
        _errors.Report(HtmlParseError.MissingQuoteBeforeDoctypePublicIdentifier, _line, _column);
        _doctypeForceQuirks = true;
        Reconsume(c, TokenizerState.BogusDoctype);
    }

    // 13.2.5.59 DOCTYPE public identifier (double-quoted)
    private void StepDoctypePublicIdDQ(int c)
    {
        if (c == '"')
        {
            _state = TokenizerState.AfterDoctypePublicIdentifier;
            return;
        }
        if (c == 0)
        {
            _errors.Report(HtmlParseError.UnexpectedNullCharacter, _line, _column);
            _doctypePublicId.Append((char)0xFFFD);
            return;
        }
        if (c == '>')
        {
            _errors.Report(HtmlParseError.AbruptDoctypePublicIdentifier, _line, _column);
            _doctypeForceQuirks = true;
            _state = TokenizerState.Data;
            EmitDoctype();
            return;
        }
        _doctypePublicId.Append((char)c);
    }

    // 13.2.5.60 DOCTYPE public identifier (single-quoted)
    private void StepDoctypePublicIdSQ(int c)
    {
        if (c == '\'')
        {
            _state = TokenizerState.AfterDoctypePublicIdentifier;
            return;
        }
        if (c == 0)
        {
            _errors.Report(HtmlParseError.UnexpectedNullCharacter, _line, _column);
            _doctypePublicId.Append((char)0xFFFD);
            return;
        }
        if (c == '>')
        {
            _errors.Report(HtmlParseError.AbruptDoctypePublicIdentifier, _line, _column);
            _doctypeForceQuirks = true;
            _state = TokenizerState.Data;
            EmitDoctype();
            return;
        }
        _doctypePublicId.Append((char)c);
    }

    // 13.2.5.61 After DOCTYPE public identifier state
    private void StepAfterDoctypePublicId(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            _state = TokenizerState.BetweenDoctypePublicAndSystemIdentifiers;
            return;
        }
        switch (c)
        {
            case '>':
                _state = TokenizerState.Data;
                EmitDoctype();
                return;
            case '"':
                _errors.Report(HtmlParseError.MissingWhitespaceBetweenDoctypePublicAndSystemIdentifiers, _line, _column);
                _doctypeSystemId.Clear();
                _doctypeSystemIdSet = true;
                _state = TokenizerState.DoctypeSystemIdentifierDoubleQuoted;
                return;
            case '\'':
                _errors.Report(HtmlParseError.MissingWhitespaceBetweenDoctypePublicAndSystemIdentifiers, _line, _column);
                _doctypeSystemId.Clear();
                _doctypeSystemIdSet = true;
                _state = TokenizerState.DoctypeSystemIdentifierSingleQuoted;
                return;
        }
        _errors.Report(HtmlParseError.MissingQuoteBeforeDoctypeSystemIdentifier, _line, _column);
        _doctypeForceQuirks = true;
        Reconsume(c, TokenizerState.BogusDoctype);
    }

    // 13.2.5.62 Between DOCTYPE public and system identifiers state
    private void StepBetweenDoctypePublicSystem(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            return;
        }

        switch (c)
        {
            case '>':
                _state = TokenizerState.Data;
                EmitDoctype();
                return;
            case '"':
                _doctypeSystemId.Clear();
                _doctypeSystemIdSet = true;
                _state = TokenizerState.DoctypeSystemIdentifierDoubleQuoted;
                return;
            case '\'':
                _doctypeSystemId.Clear();
                _doctypeSystemIdSet = true;
                _state = TokenizerState.DoctypeSystemIdentifierSingleQuoted;
                return;
        }
        _errors.Report(HtmlParseError.MissingQuoteBeforeDoctypeSystemIdentifier, _line, _column);
        _doctypeForceQuirks = true;
        Reconsume(c, TokenizerState.BogusDoctype);
    }

    // 13.2.5.63 After DOCTYPE system keyword state
    private void StepAfterDoctypeSystemKw(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            _state = TokenizerState.BeforeDoctypeSystemIdentifier;
            return;
        }
        switch (c)
        {
            case '"':
                _errors.Report(HtmlParseError.MissingWhitespaceAfterDoctypeSystemKeyword, _line, _column);
                _doctypeSystemId.Clear();
                _doctypeSystemIdSet = true;
                _state = TokenizerState.DoctypeSystemIdentifierDoubleQuoted;
                return;
            case '\'':
                _errors.Report(HtmlParseError.MissingWhitespaceAfterDoctypeSystemKeyword, _line, _column);
                _doctypeSystemId.Clear();
                _doctypeSystemIdSet = true;
                _state = TokenizerState.DoctypeSystemIdentifierSingleQuoted;
                return;
            case '>':
                _errors.Report(HtmlParseError.MissingDoctypeSystemIdentifier, _line, _column);
                _doctypeForceQuirks = true;
                _state = TokenizerState.Data;
                EmitDoctype();
                return;
        }
        _errors.Report(HtmlParseError.MissingQuoteBeforeDoctypeSystemIdentifier, _line, _column);
        _doctypeForceQuirks = true;
        Reconsume(c, TokenizerState.BogusDoctype);
    }

    // 13.2.5.64 Before DOCTYPE system identifier state
    private void StepBeforeDoctypeSystemId(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            return;
        }

        switch (c)
        {
            case '"':
                _doctypeSystemId.Clear();
                _doctypeSystemIdSet = true;
                _state = TokenizerState.DoctypeSystemIdentifierDoubleQuoted;
                return;
            case '\'':
                _doctypeSystemId.Clear();
                _doctypeSystemIdSet = true;
                _state = TokenizerState.DoctypeSystemIdentifierSingleQuoted;
                return;
            case '>':
                _errors.Report(HtmlParseError.MissingDoctypeSystemIdentifier, _line, _column);
                _doctypeForceQuirks = true;
                _state = TokenizerState.Data;
                EmitDoctype();
                return;
        }
        _errors.Report(HtmlParseError.MissingQuoteBeforeDoctypeSystemIdentifier, _line, _column);
        _doctypeForceQuirks = true;
        Reconsume(c, TokenizerState.BogusDoctype);
    }

    // 13.2.5.65 DOCTYPE system identifier (double-quoted)
    private void StepDoctypeSystemIdDQ(int c)
    {
        if (c == '"')
        {
            _state = TokenizerState.AfterDoctypeSystemIdentifier;
            return;
        }
        if (c == 0)
        {
            _errors.Report(HtmlParseError.UnexpectedNullCharacter, _line, _column);
            _doctypeSystemId.Append((char)0xFFFD);
            return;
        }
        if (c == '>')
        {
            _errors.Report(HtmlParseError.AbruptDoctypeSystemIdentifier, _line, _column);
            _doctypeForceQuirks = true;
            _state = TokenizerState.Data;
            EmitDoctype();
            return;
        }
        _doctypeSystemId.Append((char)c);
    }

    // 13.2.5.66 DOCTYPE system identifier (single-quoted)
    private void StepDoctypeSystemIdSQ(int c)
    {
        if (c == '\'')
        {
            _state = TokenizerState.AfterDoctypeSystemIdentifier;
            return;
        }
        if (c == 0)
        {
            _errors.Report(HtmlParseError.UnexpectedNullCharacter, _line, _column);
            _doctypeSystemId.Append((char)0xFFFD);
            return;
        }
        if (c == '>')
        {
            _errors.Report(HtmlParseError.AbruptDoctypeSystemIdentifier, _line, _column);
            _doctypeForceQuirks = true;
            _state = TokenizerState.Data;
            EmitDoctype();
            return;
        }
        _doctypeSystemId.Append((char)c);
    }

    // 13.2.5.67 After DOCTYPE system identifier state
    private void StepAfterDoctypeSystemId(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            return;
        }

        if (c == '>')
        {
            _state = TokenizerState.Data;
            EmitDoctype();
            return;
        }
        _errors.Report(HtmlParseError.UnexpectedCharacterAfterDoctypeSystemIdentifier, _line, _column);
        // Spec: don't set force-quirks here, reconsume in bogus doctype.
        Reconsume(c, TokenizerState.BogusDoctype);
    }

    // 13.2.5.68 Bogus DOCTYPE state — swallow chars until '>' or EOF.
    private void StepBogusDoctype(int c)
    {
        if (c == '>')
        {
            _state = TokenizerState.Data;
            EmitDoctype();
            return;
        }
        if (c == 0)
        {
            _errors.Report(HtmlParseError.UnexpectedNullCharacter, _line, _column);
            // No state mutation — bogus doctype just absorbs.
        }
    }
}
