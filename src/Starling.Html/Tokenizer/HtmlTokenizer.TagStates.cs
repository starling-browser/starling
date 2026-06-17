namespace Starling.Html.Tokenizer;

/// <summary>
/// Tag + attribute states from WHATWG HTML §13.2.5.6 through §13.2.5.40
/// (the contiguous range of tag-construction states).
/// </summary>
/// <remarks>
/// State summary:
/// <list type="number">
///   <item>§13.2.5.6  Tag open</item>
///   <item>§13.2.5.7  End tag open</item>
///   <item>§13.2.5.8  Tag name</item>
///   <item>§13.2.5.32 Before attribute name</item>
///   <item>§13.2.5.33 Attribute name</item>
///   <item>§13.2.5.34 After attribute name</item>
///   <item>§13.2.5.35 Before attribute value</item>
///   <item>§13.2.5.36 Attribute value (double-quoted)</item>
///   <item>§13.2.5.37 Attribute value (single-quoted)</item>
///   <item>§13.2.5.38 Attribute value (unquoted)</item>
///   <item>§13.2.5.39 After attribute value (quoted)</item>
///   <item>§13.2.5.40 Self-closing start tag</item>
/// </list>
/// The states share the <c>_tagName</c>/<c>_tagAttrs</c>/<c>_attrName</c>/
/// <c>_attrValue</c> builders declared in <c>HtmlTokenizer.cs</c>.
/// </remarks>
public sealed partial class HtmlTokenizer
{
    // ----- builder helpers --------------------------------------------------

    private void StartNewTag(bool isEnd)
    {
        _tagIsEnd = isEnd;
        _tagSelfClosing = false;
        _tagName.Clear();
        _tagAttrs.Clear();
        _attrName.Clear();
        _attrValue.Clear();
    }

    private void StartNewAttribute()
    {
        // Commit any in-progress attribute first (e.g. transition from
        // AttributeName → AfterAttributeName without an `=`).
        CommitPendingAttribute();
        _attrName.Clear();
        _attrValue.Clear();
    }

    /// <summary>
    /// Flush the current attribute name+value into <c>_tagAttrs</c>, applying
    /// the duplicate-name rule from §13.2.5.33: "if there is already an
    /// attribute with the same name on the token, this is a
    /// duplicate-attribute parse error and the new attribute must be removed
    /// from the token".
    /// </summary>
    private void CommitPendingAttribute()
    {
        if (_attrName.Length == 0)
        {
            _attrValue.Clear();
            return;
        }

        var name = _attrName.ToString();
        foreach (var existing in _tagAttrs)
        {
            if (existing.Name == name)
            {
                _errors.Report(HtmlParseError.DuplicateAttribute, _line, _column);
                _attrName.Clear();
                _attrValue.Clear();
                return;
            }
        }

        _tagAttrs.Add(new HtmlAttribute(name, _attrValue.ToString()));
        _attrName.Clear();
        _attrValue.Clear();
    }

    private void EmitCurrentTag()
    {
        CommitPendingAttribute();
        var name = _tagName.ToString();
        HtmlToken token = _tagIsEnd
            ? new EndTagToken(name, [.. _tagAttrs], _tagSelfClosing)
            : new StartTagToken(name, [.. _tagAttrs], _tagSelfClosing);
        if (!_tagIsEnd)
        {
            _lastStartTagName = name;
        }

        _emitted.Enqueue(token);
        _tagName.Clear();
        _tagAttrs.Clear();
    }

    private static bool IsAsciiWhitespace(int c)
        => c == '\t' || c == '\n' || c == '\f' || c == ' ';

    private static bool IsAsciiUpper(int c)
        => c >= 'A' && c <= 'Z';

    private static bool IsAsciiAlpha(int c)
        => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    // ----- dispatch from HtmlTokenizer.cs -----------------------------------

    private void DispatchTagState(TokenizerState state, int c)
    {
        switch (state)
        {
            case TokenizerState.TagOpen: StepTagOpen(c); break;
            case TokenizerState.EndTagOpen: StepEndTagOpen(c); break;
            case TokenizerState.TagName: StepTagName(c); break;
            case TokenizerState.BeforeAttributeName: StepBeforeAttrName(c); break;
            case TokenizerState.AttributeName: StepAttrName(c); break;
            case TokenizerState.AfterAttributeName: StepAfterAttrName(c); break;
            case TokenizerState.BeforeAttributeValue: StepBeforeAttrValue(c); break;
            case TokenizerState.AttributeValueDoubleQuoted: StepAttrValueDQ(c); break;
            case TokenizerState.AttributeValueSingleQuoted: StepAttrValueSQ(c); break;
            case TokenizerState.AttributeValueUnquoted: StepAttrValueUnq(c); break;
            case TokenizerState.AfterAttributeValueQuoted: StepAfterAttrValueQ(c); break;
            case TokenizerState.SelfClosingStartTag: StepSelfClosing(c); break;
            default:
                throw new InvalidOperationException(
                $"DispatchTagState invoked for unrelated state '{state}'.");
        }
    }

    private void StepTagEof()
    {
        // Common pattern for EOF in this cluster — see spec §13.2.5.6–40.
        switch (_state)
        {
            case TokenizerState.TagOpen:
                // §13.2.5.6: eof-before-tag-name. Emit '<' then EOF.
                _errors.Report(HtmlParseError.EofBeforeTagName, _line, _column);
                _emitted.Enqueue(new CharacterToken('<'));
                _emitted.Enqueue(EndOfFileToken.Instance);
                break;

            case TokenizerState.EndTagOpen:
                // §13.2.5.7: eof-before-tag-name. Emit '<', '/', EOF.
                _errors.Report(HtmlParseError.EofBeforeTagName, _line, _column);
                _emitted.Enqueue(new CharacterToken('<'));
                _emitted.Enqueue(new CharacterToken('/'));
                _emitted.Enqueue(EndOfFileToken.Instance);
                break;

            default:
                // §13.2.5.8 and the remaining tag/attr states all use the
                // same shape: eof-in-tag parse error, emit EOF (the
                // in-progress tag is discarded).
                _errors.Report(HtmlParseError.EofInTag, _line, _column);
                _emitted.Enqueue(EndOfFileToken.Instance);
                break;
        }
    }

    // -----------------------------------------------------------------------
    // 13.2.5.6 Tag open state
    // -----------------------------------------------------------------------
    private void StepTagOpen(int c)
    {
        switch (c)
        {
            case '!':
                _state = TokenizerState.MarkupDeclarationOpen;
                _tempBuffer.Clear();
                break;

            case '/':
                _state = TokenizerState.EndTagOpen;
                break;

            case '?':
                // §13.2.5.6: parse error; create empty comment; reconsume in
                // bogus comment state.
                _errors.Report(
                    HtmlParseError.UnexpectedQuestionMarkInsteadOfTagName,
                    _line, _column);
                _commentData.Clear();
                Reconsume(c, TokenizerState.BogusComment);
                break;

            default:
                if (IsAsciiAlpha(c))
                {
                    StartNewTag(isEnd: false);
                    Reconsume(c, TokenizerState.TagName);
                }
                else
                {
                    _errors.Report(
                        HtmlParseError.InvalidFirstCharacterOfTagName,
                        _line, _column);
                    _emitted.Enqueue(new CharacterToken('<'));
                    Reconsume(c, TokenizerState.Data);
                }
                break;
        }
    }

    // -----------------------------------------------------------------------
    // 13.2.5.7 End tag open state
    // -----------------------------------------------------------------------
    private void StepEndTagOpen(int c)
    {
        if (c == '>')
        {
            _errors.Report(HtmlParseError.MissingEndTagName, _line, _column);
            _state = TokenizerState.Data;
            return;
        }
        if (IsAsciiAlpha(c))
        {
            StartNewTag(isEnd: true);
            Reconsume(c, TokenizerState.TagName);
            return;
        }
        _errors.Report(
            HtmlParseError.InvalidFirstCharacterOfTagName, _line, _column);
        _commentData.Clear();
        Reconsume(c, TokenizerState.BogusComment);
    }

    // -----------------------------------------------------------------------
    // 13.2.5.8 Tag name state
    // -----------------------------------------------------------------------
    private void StepTagName(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            _state = TokenizerState.BeforeAttributeName;
            return;
        }
        switch (c)
        {
            case '/':
                _state = TokenizerState.SelfClosingStartTag;
                return;
            case '>':
                _state = TokenizerState.Data;
                EmitCurrentTag();
                return;
            case 0:
                _errors.Report(HtmlParseError.UnexpectedNullCharacter, _line, _column);
                _tagName.Append((char)0xFFFD);
                return;
        }
        if (IsAsciiUpper(c))
        {
            _tagName.Append((char)(c + 0x20));
            return;
        }
        _tagName.Append((char)c);
    }

    // -----------------------------------------------------------------------
    // 13.2.5.32 Before attribute name state
    // -----------------------------------------------------------------------
    private void StepBeforeAttrName(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            return;
        }

        if (c == '/' || c == '>' || c == -1)
        {
            Reconsume(c, TokenizerState.AfterAttributeName);
            return;
        }
        if (c == '=')
        {
            _errors.Report(
                HtmlParseError.UnexpectedEqualsSignBeforeAttributeName,
                _line, _column);
            StartNewAttribute();
            _attrName.Append('=');
            _state = TokenizerState.AttributeName;
            return;
        }
        StartNewAttribute();
        Reconsume(c, TokenizerState.AttributeName);
    }

    // -----------------------------------------------------------------------
    // 13.2.5.33 Attribute name state
    // -----------------------------------------------------------------------
    private void StepAttrName(int c)
    {
        if (IsAsciiWhitespace(c) || c == '/' || c == '>')
        {
            Reconsume(c, TokenizerState.AfterAttributeName);
            return;
        }
        if (c == '=')
        {
            _state = TokenizerState.BeforeAttributeValue;
            return;
        }
        if (IsAsciiUpper(c))
        {
            _attrName.Append((char)(c + 0x20));
            return;
        }
        if (c == 0)
        {
            _errors.Report(HtmlParseError.UnexpectedNullCharacter, _line, _column);
            _attrName.Append((char)0xFFFD);
            return;
        }
        if (c == '"' || c == '\'' || c == '<')
        {
            _errors.Report(
                HtmlParseError.UnexpectedCharacterInAttributeName,
                _line, _column);
            _attrName.Append((char)c);
            return;
        }
        _attrName.Append((char)c);
    }

    // -----------------------------------------------------------------------
    // 13.2.5.34 After attribute name state
    // -----------------------------------------------------------------------
    private void StepAfterAttrName(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            return;
        }

        switch (c)
        {
            case '/':
                _state = TokenizerState.SelfClosingStartTag;
                return;
            case '=':
                _state = TokenizerState.BeforeAttributeValue;
                return;
            case '>':
                _state = TokenizerState.Data;
                EmitCurrentTag();
                return;
        }
        StartNewAttribute();
        Reconsume(c, TokenizerState.AttributeName);
    }

    // -----------------------------------------------------------------------
    // 13.2.5.35 Before attribute value state
    // -----------------------------------------------------------------------
    private void StepBeforeAttrValue(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            return;
        }

        switch (c)
        {
            case '"':
                _state = TokenizerState.AttributeValueDoubleQuoted;
                return;
            case '\'':
                _state = TokenizerState.AttributeValueSingleQuoted;
                return;
            case '>':
                _errors.Report(
                    HtmlParseError.MissingAttributeValue, _line, _column);
                _state = TokenizerState.Data;
                EmitCurrentTag();
                return;
        }
        Reconsume(c, TokenizerState.AttributeValueUnquoted);
    }

    // -----------------------------------------------------------------------
    // 13.2.5.36 Attribute value (double-quoted) state
    // -----------------------------------------------------------------------
    private void StepAttrValueDQ(int c)
    {
        switch (c)
        {
            case '"':
                _state = TokenizerState.AfterAttributeValueQuoted;
                return;
            case '&':
                _returnState = TokenizerState.AttributeValueDoubleQuoted;
                _tempBuffer.Clear();
                _tempBuffer.Append('&');
                _state = TokenizerState.CharacterReference;
                return;
            case 0:
                _errors.Report(HtmlParseError.UnexpectedNullCharacter, _line, _column);
                _attrValue.Append((char)0xFFFD);
                return;
        }
        _attrValue.Append((char)c);
    }

    // -----------------------------------------------------------------------
    // 13.2.5.37 Attribute value (single-quoted) state
    // -----------------------------------------------------------------------
    private void StepAttrValueSQ(int c)
    {
        switch (c)
        {
            case '\'':
                _state = TokenizerState.AfterAttributeValueQuoted;
                return;
            case '&':
                _returnState = TokenizerState.AttributeValueSingleQuoted;
                _tempBuffer.Clear();
                _tempBuffer.Append('&');
                _state = TokenizerState.CharacterReference;
                return;
            case 0:
                _errors.Report(HtmlParseError.UnexpectedNullCharacter, _line, _column);
                _attrValue.Append((char)0xFFFD);
                return;
        }
        _attrValue.Append((char)c);
    }

    // -----------------------------------------------------------------------
    // 13.2.5.38 Attribute value (unquoted) state
    // -----------------------------------------------------------------------
    private void StepAttrValueUnq(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            _state = TokenizerState.BeforeAttributeName;
            return;
        }
        switch (c)
        {
            case '&':
                _returnState = TokenizerState.AttributeValueUnquoted;
                _tempBuffer.Clear();
                _tempBuffer.Append('&');
                _state = TokenizerState.CharacterReference;
                return;
            case '>':
                _state = TokenizerState.Data;
                EmitCurrentTag();
                return;
            case 0:
                _errors.Report(HtmlParseError.UnexpectedNullCharacter, _line, _column);
                _attrValue.Append((char)0xFFFD);
                return;
            case '"':
            case '\'':
            case '<':
            case '=':
            case '`':
                _errors.Report(
                    HtmlParseError.UnexpectedCharacterInUnquotedAttributeValue,
                    _line, _column);
                _attrValue.Append((char)c);
                return;
        }
        _attrValue.Append((char)c);
    }

    // -----------------------------------------------------------------------
    // 13.2.5.39 After attribute value (quoted) state
    // -----------------------------------------------------------------------
    private void StepAfterAttrValueQ(int c)
    {
        if (IsAsciiWhitespace(c))
        {
            _state = TokenizerState.BeforeAttributeName;
            return;
        }
        switch (c)
        {
            case '/':
                _state = TokenizerState.SelfClosingStartTag;
                return;
            case '>':
                _state = TokenizerState.Data;
                EmitCurrentTag();
                return;
        }
        _errors.Report(
            HtmlParseError.MissingWhitespaceBetweenAttributes, _line, _column);
        Reconsume(c, TokenizerState.BeforeAttributeName);
    }

    // -----------------------------------------------------------------------
    // 13.2.5.40 Self-closing start tag state
    // -----------------------------------------------------------------------
    private void StepSelfClosing(int c)
    {
        if (c == '>')
        {
            _tagSelfClosing = true;
            _state = TokenizerState.Data;
            EmitCurrentTag();
            return;
        }
        _errors.Report(HtmlParseError.UnexpectedSolidusInTag, _line, _column);
        Reconsume(c, TokenizerState.BeforeAttributeName);
    }
}
