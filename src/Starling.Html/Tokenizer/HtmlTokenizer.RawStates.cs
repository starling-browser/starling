namespace Starling.Html.Tokenizer;

/// <summary>
/// RCDATA / RAWTEXT / PLAINTEXT states from WHATWG HTML §13.2.5.2, .3, .5,
/// .9–.11 (RCDATA), and .12–.14 (RAWTEXT).
/// </summary>
/// <remarks>
/// <para>
/// These states are entered explicitly by the tree builder via
/// <see cref="HtmlTokenizer.SetState"/> after parsing certain elements:
/// </para>
/// <list type="bullet">
///   <item>RCDATA — <c>&lt;textarea&gt;</c>, <c>&lt;title&gt;</c></item>
///   <item>RAWTEXT — <c>&lt;style&gt;</c>, <c>&lt;xmp&gt;</c>,
///         <c>&lt;iframe&gt;</c>, <c>&lt;noembed&gt;</c>, <c>&lt;noframes&gt;</c>,
///         <c>&lt;noscript&gt;</c> (when scripting enabled)</item>
///   <item>PLAINTEXT — <c>&lt;plaintext&gt;</c> (legacy; everything to EOF)</item>
/// </list>
/// <para>
/// "Appropriate end tag token" (§13.2.5.11 / §13.2.5.14) means: the token
/// being built has the same tag name as the most-recently-emitted start
/// tag. We track that in <c>_lastStartTagName</c>.
/// </para>
/// </remarks>
public sealed partial class HtmlTokenizer
{
    private void DispatchRawState(TokenizerState state, int c)
    {
        switch (state)
        {
            case TokenizerState.Rcdata: StepRcdata(c); break;
            case TokenizerState.RcdataLessThanSign: StepRcdataLt(c); break;
            case TokenizerState.RcdataEndTagOpen: StepRcdataEndTagOpen(c); break;
            case TokenizerState.RcdataEndTagName: StepRcdataEndTagName(c); break;
            case TokenizerState.Rawtext: StepRawtext(c); break;
            case TokenizerState.RawtextLessThanSign: StepRawtextLt(c); break;
            case TokenizerState.RawtextEndTagOpen: StepRawtextEndTagOpen(c); break;
            case TokenizerState.RawtextEndTagName: StepRawtextEndTagName(c); break;
            case TokenizerState.Plaintext: StepPlaintext(c); break;
            default:
                throw new InvalidOperationException(
                $"DispatchRawState invoked for unrelated state '{state}'.");
        }
    }

    private void StepRawEof()
    {
        // All seven RAW states share the same EOF behavior: emit pending
        // chars from the temp buffer (if any), then EOF. Spec doesn't fire
        // a parse error for EOF in Rcdata/Rawtext/Plaintext — they're
        // tail-driven states.
        switch (_state)
        {
            case TokenizerState.RcdataLessThanSign:
            case TokenizerState.RawtextLessThanSign:
                _emitted.Enqueue(new CharacterToken('<'));
                break;
            case TokenizerState.RcdataEndTagOpen:
            case TokenizerState.RawtextEndTagOpen:
                _emitted.Enqueue(new CharacterToken('<'));
                _emitted.Enqueue(new CharacterToken('/'));
                break;
            case TokenizerState.RcdataEndTagName:
            case TokenizerState.RawtextEndTagName:
                // No "appropriate" close was confirmed → emit '<', '/',
                // and the buffered name chars as character tokens.
                FlushEndTagAttemptAsCharacters();
                break;
        }
        _emitted.Enqueue(EndOfFileToken.Instance);
    }

    private void FlushEndTagAttemptAsCharacters()
    {
        _emitted.Enqueue(new CharacterToken('<'));
        _emitted.Enqueue(new CharacterToken('/'));
        for (var i = 0; i < _tempBuffer.Length; i++)
        {
            _emitted.Enqueue(new CharacterToken(_tempBuffer[i]));
        }

        _tempBuffer.Clear();
        _tagName.Clear();
        _tagAttrs.Clear();
    }

    private bool IsAppropriateEndTag()
        => _lastStartTagName is not null
           && _tagName.Length == _lastStartTagName.Length
           && _tagName.ToString() == _lastStartTagName;

    // -----------------------------------------------------------------------
    // 13.2.5.2 RCDATA state
    // -----------------------------------------------------------------------
    private void StepRcdata(int c)
    {
        switch (c)
        {
            case '&':
                _returnState = TokenizerState.Rcdata;
                _tempBuffer.Clear();
                _tempBuffer.Append('&');
                _state = TokenizerState.CharacterReference;
                break;
            case '<':
                _state = TokenizerState.RcdataLessThanSign;
                break;
            case 0:
                _errors.Report(HtmlParseError.UnexpectedNullCharacter, _line, _column);
                _emitted.Enqueue(new CharacterToken(0xFFFD));
                break;
            default:
                _emitted.Enqueue(new CharacterToken(c));
                break;
        }
    }

    // 13.2.5.9 RCDATA less-than sign state
    private void StepRcdataLt(int c)
    {
        if (c == '/')
        {
            _tempBuffer.Clear();
            _state = TokenizerState.RcdataEndTagOpen;
            return;
        }
        _emitted.Enqueue(new CharacterToken('<'));
        Reconsume(c, TokenizerState.Rcdata);
    }

    // 13.2.5.10 RCDATA end tag open state
    private void StepRcdataEndTagOpen(int c)
    {
        if (IsAsciiAlpha(c))
        {
            StartNewTag(isEnd: true);
            Reconsume(c, TokenizerState.RcdataEndTagName);
            return;
        }
        _emitted.Enqueue(new CharacterToken('<'));
        _emitted.Enqueue(new CharacterToken('/'));
        Reconsume(c, TokenizerState.Rcdata);
    }

    // 13.2.5.11 RCDATA end tag name state
    private void StepRcdataEndTagName(int c) =>
        StepEndTagNameCommon(c, returnState: TokenizerState.Rcdata);

    // -----------------------------------------------------------------------
    // 13.2.5.3 RAWTEXT state
    // -----------------------------------------------------------------------
    private void StepRawtext(int c)
    {
        switch (c)
        {
            case '<':
                _state = TokenizerState.RawtextLessThanSign;
                break;
            case 0:
                _errors.Report(HtmlParseError.UnexpectedNullCharacter, _line, _column);
                _emitted.Enqueue(new CharacterToken(0xFFFD));
                break;
            default:
                _emitted.Enqueue(new CharacterToken(c));
                break;
        }
    }

    // 13.2.5.12 RAWTEXT less-than sign state
    private void StepRawtextLt(int c)
    {
        if (c == '/')
        {
            _tempBuffer.Clear();
            _state = TokenizerState.RawtextEndTagOpen;
            return;
        }
        _emitted.Enqueue(new CharacterToken('<'));
        Reconsume(c, TokenizerState.Rawtext);
    }

    // 13.2.5.13 RAWTEXT end tag open state
    private void StepRawtextEndTagOpen(int c)
    {
        if (IsAsciiAlpha(c))
        {
            StartNewTag(isEnd: true);
            Reconsume(c, TokenizerState.RawtextEndTagName);
            return;
        }
        _emitted.Enqueue(new CharacterToken('<'));
        _emitted.Enqueue(new CharacterToken('/'));
        Reconsume(c, TokenizerState.Rawtext);
    }

    // 13.2.5.14 RAWTEXT end tag name state
    private void StepRawtextEndTagName(int c) =>
        StepEndTagNameCommon(c, returnState: TokenizerState.Rawtext);

    // -----------------------------------------------------------------------
    // 13.2.5.5 PLAINTEXT state
    // -----------------------------------------------------------------------
    private void StepPlaintext(int c)
    {
        if (c == 0)
        {
            _errors.Report(HtmlParseError.UnexpectedNullCharacter, _line, _column);
            _emitted.Enqueue(new CharacterToken(0xFFFD));
            return;
        }
        _emitted.Enqueue(new CharacterToken(c));
    }

    // -----------------------------------------------------------------------
    // Shared body for §13.2.5.11 (RCDATA end tag name) and §13.2.5.14
    // (RAWTEXT end tag name). The state shapes are identical except for
    // the fall-through return state. Same shape also applies to
    // ScriptDataEndTagName.
    // -----------------------------------------------------------------------
    private void StepEndTagNameCommon(int c, TokenizerState returnState)
    {
        if (IsAsciiWhitespace(c) && IsAppropriateEndTag())
        {
            _state = TokenizerState.BeforeAttributeName;
            return;
        }
        if (c == '/' && IsAppropriateEndTag())
        {
            _state = TokenizerState.SelfClosingStartTag;
            return;
        }
        if (c == '>' && IsAppropriateEndTag())
        {
            _state = TokenizerState.Data;
            EmitCurrentTag();
            return;
        }
        if (IsAsciiUpper(c))
        {
            _tagName.Append((char)(c + 0x20));
            _tempBuffer.Append((char)c);
            return;
        }
        if (c >= 'a' && c <= 'z')
        {
            _tagName.Append((char)c);
            _tempBuffer.Append((char)c);
            return;
        }
        // Anything else → not an appropriate end tag. Emit '<', '/', and the
        // accumulated temp buffer as character tokens, then reconsume in the
        // host state (RCDATA / RAWTEXT / ScriptData).
        FlushEndTagAttemptAsCharacters();
        Reconsume(c, returnState);
    }
}
