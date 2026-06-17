using Starling.Css.Parser;
using Starling.Css.Tokenizer;

namespace Starling.Css.Media;

// MQ5 §2/§4: parse a `<media-query-list>` from a stream of CSS component values.
public sealed class MediaQueryParser
{
    private readonly IReadOnlyList<CssComponentValue> _values;
    private int _position;

    public MediaQueryParser(IReadOnlyList<CssComponentValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values;
    }

    public static MediaQueryList ParseList(IReadOnlyList<CssComponentValue> values)
        => new MediaQueryParser(values).ParseList();

    public MediaQueryList ParseList()
    {
        var queries = new List<MediaQuery>();
        SkipWs();
        if (IsEnd)
        {
            return MediaQueryList.All;
        }

        while (!IsEnd)
        {
            var query = ParseQuery();
            queries.Add(query);
            SkipWs();
            if (!ConsumeComma())
            {
                break;
            }

            SkipWs();
        }

        return queries.Count == 0 ? MediaQueryList.All : new MediaQueryList(queries);
    }

    private MediaQuery ParseQuery()
    {
        SkipWs();
        // Starts with `(` → condition-only (no media type).
        if (Current is CssSimpleBlock || Current is CssFunction)
        {
            var cond = ParseConditionAll();
            return new MediaQuery(MediaQueryModifier.None, null, cond);
        }

        var modifier = MediaQueryModifier.None;
        if (PeekIdentEquals("not"))
        {
            _position++;
            SkipWs();
            // `not` followed by `(` is condition-form
            if (Current is CssSimpleBlock or CssFunction)
            {
                var inner = ParseConditionInParens();
                // collect optional `and (...)` following.
                var parts = new List<MediaCondition> { inner };
                while (true)
                {
                    SkipWs();
                    if (!PeekIdentEquals("and"))
                    {
                        break;
                    }

                    _position++;
                    SkipWs();
                    parts.Add(ParseConditionInParens());
                }
                var aggregate = parts.Count == 1 ? parts[0] : new MediaConditionAnd(parts);
                return new MediaQuery(MediaQueryModifier.None, null, new MediaConditionNot(aggregate));
            }
            modifier = MediaQueryModifier.Not;
        }
        else if (PeekIdentEquals("only"))
        {
            _position++;
            SkipWs();
            modifier = MediaQueryModifier.Only;
        }

        string? mediaType = null;
        if (Current is CssTokenValue { Token.Type: CssTokenType.Ident } typeTok)
        {
            mediaType = typeTok.Token.Value.ToLowerInvariant();
            _position++;
        }

        MediaCondition? condition = null;
        SkipWs();
        if (PeekIdentEquals("and"))
        {
            _position++;
            SkipWs();
            var parts = new List<MediaCondition> { ParseConditionInParens() };
            while (true)
            {
                SkipWs();
                if (!PeekIdentEquals("and"))
                {
                    break;
                }

                _position++;
                SkipWs();
                parts.Add(ParseConditionInParens());
            }
            condition = parts.Count == 1 ? parts[0] : new MediaConditionAnd(parts);
        }

        return new MediaQuery(modifier, mediaType, condition);
    }

    // <media-condition> = <media-not> | <media-in-parens> [ <media-and>* | <media-or>* ]
    private MediaCondition ParseConditionAll()
    {
        SkipWs();
        if (PeekIdentEquals("not"))
        {
            _position++;
            SkipWs();
            return new MediaConditionNot(ParseConditionInParens());
        }

        var first = ParseConditionInParens();
        SkipWs();
        if (PeekIdentEquals("and"))
        {
            var parts = new List<MediaCondition> { first };
            while (PeekIdentEquals("and"))
            {
                _position++;
                SkipWs();
                parts.Add(ParseConditionInParens());
                SkipWs();
            }
            return new MediaConditionAnd(parts);
        }
        if (PeekIdentEquals("or"))
        {
            var parts = new List<MediaCondition> { first };
            while (PeekIdentEquals("or"))
            {
                _position++;
                SkipWs();
                parts.Add(ParseConditionInParens());
                SkipWs();
            }
            return new MediaConditionOr(parts);
        }
        return first;
    }

    private MediaCondition ParseConditionInParens()
    {
        SkipWs();
        if (Current is CssSimpleBlock { StartToken: CssTokenType.LeftParen } block)
        {
            _position++;
            // It can be either a nested condition or a feature.
            // If the inner content starts with `not`/`(` → nested condition.
            // Else → feature.
            return ParseInsideParens(block.Values);
        }

        if (Current is CssFunction fn)
        {
            // general_enclosed — unsupported function form, treat as unknown.
            _position++;
            return new MediaConditionFeature(new MediaFeatureBoolean(fn.Name.ToLowerInvariant()));
        }

        // Malformed — consume one token and yield an always-false feature.
        if (!IsEnd)
        {
            _position++;
        }

        return new MediaConditionFeature(new MediaFeatureBoolean("__unknown__"));
    }

    private static MediaCondition ParseInsideParens(IReadOnlyList<CssComponentValue> values)
    {
        // Filter is unwieldy here; use a sub-parser.
        var sub = new MediaQueryParser(values);
        sub.SkipWs();
        // Nested: `( not ... )` or `( (...) and ... )`
        if (sub.PeekIdentEquals("not") ||
            sub.Current is CssSimpleBlock { StartToken: CssTokenType.LeftParen })
        {
            return sub.ParseConditionAll();
        }

        // Otherwise it must be a feature.
        return new MediaConditionFeature(sub.ParseFeature());
    }

    private MediaFeature ParseFeature()
    {
        SkipWs();
        // Possibilities (MQ5 §4):
        //   ident                                  → boolean feature
        //   ident : value                          → plain (legacy) feature
        //   ident op value                         → range short form
        //   value op ident                         → range short form (reversed)
        //   value op ident op value                → range double form
        if (Current is CssTokenValue { Token.Type: CssTokenType.Ident } firstIdent)
        {
            var name = firstIdent.Token.Value.ToLowerInvariant();
            _position++;
            SkipWs();
            if (ConsumeColon())
            {
                SkipWs();
                var value = ParseFeatureValue();
                return new MediaFeaturePlain(name, value);
            }
            if (TryParseRangeOp(out var op))
            {
                SkipWs();
                var rhs = ParseFeatureValue();
                return new MediaFeatureRange(name, ToReversed(op), rhs, RangeOp.None, null);
            }
            return new MediaFeatureBoolean(name);
        }

        // value op ident [ op value ]?
        var lhs = ParseFeatureValue();
        SkipWs();
        if (!TryParseRangeOp(out var op1))
        {
            return new MediaFeatureBoolean("__unknown__");
        }

        SkipWs();
        if (Current is not CssTokenValue { Token.Type: CssTokenType.Ident } midIdent)
        {
            return new MediaFeatureBoolean("__unknown__");
        }

        var featureName = midIdent.Token.Value.ToLowerInvariant();
        _position++;
        SkipWs();
        if (!TryParseRangeOp(out var op2))
        {
            return new MediaFeatureRange(featureName, op1, lhs, RangeOp.None, null);
        }

        SkipWs();
        var rhs2 = ParseFeatureValue();
        return new MediaFeatureRange(featureName, op1, lhs, op2, rhs2);
    }

    private static RangeOp ToReversed(RangeOp op) => op switch
    {
        // (width >= 400px) means `width >= 400px`; from feature-name perspective op stays as-is.
        _ => op,
    };

    // True only for range-op delimiters (the ident branch handles `:` itself).
    private bool LooksLikeRangeContinuation(int offset)
    {
        var i = _position + offset;
        while (i < _values.Count && _values[i] is CssTokenValue { Token.Type: CssTokenType.Whitespace })
        {
            i++;
        }

        if (i >= _values.Count)
        {
            return false;
        }

        return _values[i] is CssTokenValue { Token.Type: CssTokenType.Delim, Token.Delimiter: '<' or '>' or '=' };
    }

    private bool TryParseRangeOp(out RangeOp op)
    {
        op = RangeOp.None;
        if (Current is not CssTokenValue { Token.Type: CssTokenType.Delim } tok)
        {
            return false;
        }

        var d = tok.Token.Delimiter;
        if (d is not ('<' or '>' or '='))
        {
            return false;
        }

        _position++;
        // check for `<=` or `>=`
        if (d is '<' or '>' && Current is CssTokenValue { Token.Type: CssTokenType.Delim, Token.Delimiter: '=' })
        {
            _position++;
            op = d == '<' ? RangeOp.LessOrEqual : RangeOp.GreaterOrEqual;
            return true;
        }
        op = d switch
        {
            '<' => RangeOp.Less,
            '>' => RangeOp.Greater,
            '=' => RangeOp.Equal,
            _ => RangeOp.None,
        };
        return true;
    }

    private MediaFeatureValue ParseFeatureValue()
    {
        if (Current is CssTokenValue tv)
        {
            var t = tv.Token;
            if (t.Type == CssTokenType.Dimension)
            {
                _position++;
                // ratio like `16/9` not natively a token — handled via Number / Number below.
                return new MediaFeatureDimension(t.Number, t.Unit.ToLowerInvariant());
            }
            if (t.Type == CssTokenType.Number)
            {
                _position++;
                // check for ratio `n / m`
                SkipWs();
                if (Current is CssTokenValue { Token.Type: CssTokenType.Delim, Token.Delimiter: '/' })
                {
                    _position++;
                    SkipWs();
                    if (Current is CssTokenValue { Token.Type: CssTokenType.Number } denomTok)
                    {
                        _position++;
                        return new MediaFeatureRatio(t.Number, denomTok.Token.Number);
                    }
                }
                return new MediaFeatureNumber(t.Number);
            }
            if (t.Type == CssTokenType.Percentage)
            {
                _position++;
                return new MediaFeatureNumber(t.Number / 100d);
            }
            if (t.Type == CssTokenType.Ident)
            {
                _position++;
                return new MediaFeatureIdent(t.Value.ToLowerInvariant());
            }
        }
        // unknown — yield a sentinel.
        if (!IsEnd)
        {
            _position++;
        }

        return new MediaFeatureIdent("__unknown__");
    }

    private bool ConsumeColon()
    {
        if (Current is CssTokenValue { Token.Type: CssTokenType.Colon })
        {
            _position++;
            return true;
        }
        return false;
    }

    private bool ConsumeComma()
    {
        if (Current is CssTokenValue { Token.Type: CssTokenType.Comma })
        {
            _position++;
            return true;
        }
        return false;
    }

    private bool PeekIdentEquals(string name)
        => Current is CssTokenValue { Token.Type: CssTokenType.Ident } tok &&
           tok.Token.Value.Equals(name, StringComparison.OrdinalIgnoreCase);

    private void SkipWs()
    {
        while (Current is CssTokenValue { Token.Type: CssTokenType.Whitespace })
        {
            _position++;
        }
    }

    private CssComponentValue Current
        => _position < _values.Count ? _values[_position] : new CssTokenValue(new CssToken(CssTokenType.Eof));

    private bool IsEnd => _position >= _values.Count;
}
