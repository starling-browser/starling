using System.Globalization;
using Starling.Css.Parser;
using Starling.Css.Tokenizer;

namespace Starling.Css.Selectors;

public sealed class SelectorParser
{
    private readonly IReadOnlyList<CssComponentValue> _values;
    private int _position;

    public SelectorParser(IReadOnlyList<CssComponentValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _values = values;
    }

    public static SelectorList ParseSelectorList(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sheet = CssParser.ParseStyleSheet($"{source} {{ }}");
        var rule = sheet.Rules.OfType<StyleRule>().FirstOrDefault();
        return rule is null ? SelectorList.Empty : ParseSelectorList(rule.Prelude);
    }

    public static SelectorList ParseSelectorList(IReadOnlyList<CssComponentValue> values)
        => new SelectorParser(values).ParseSelectorList();

    public SelectorList ParseSelectorList()
    {
        var selectors = new List<ComplexSelector>();
        while (!IsEnd)
        {
            SkipWhitespace();
            if (ConsumeToken(CssTokenType.Comma))
            {
                continue;
            }

            if (IsEnd)
            {
                break;
            }

            var selector = ParseComplexSelector();
            if (selector.Parts.Count > 0)
            {
                selectors.Add(selector);
            }

            SkipWhitespace();
            if (!ConsumeToken(CssTokenType.Comma) && !IsEnd)
            {
                break;
            }
        }

        return new SelectorList(selectors);
    }

    private ComplexSelector ParseComplexSelector()
    {
        var parts = new List<ComplexSelectorPart>();
        var pendingCombinator = SelectorCombinator.None;
        var sawExplicitLeadingCombinator = false;

        while (!IsEnd && !IsToken(CssTokenType.Comma))
        {
            var hadWhitespace = SkipWhitespace();
            if (hadWhitespace && parts.Count > 0 && pendingCombinator == SelectorCombinator.None)
            {
                pendingCombinator = SelectorCombinator.Descendant;
            }

            if (TryConsumeCombinator(out var explicitCombinator))
            {
                pendingCombinator = explicitCombinator;
                if (parts.Count == 0)
                {
                    sawExplicitLeadingCombinator = true;
                }

                SkipWhitespace();
                continue;
            }

            var compound = ParseCompoundSelector();
            if (compound.SimpleSelectors.Count == 0)
            {
                break;
            }

            EnforcePseudoElementPosition(compound);

            // Preserve an explicit leading combinator (e.g. `> a` inside `:has()`) on the first part.
            var combinator = parts.Count == 0
                ? (sawExplicitLeadingCombinator ? pendingCombinator : SelectorCombinator.None)
                : pendingCombinator;
            parts.Add(new ComplexSelectorPart(compound, combinator));
            pendingCombinator = SelectorCombinator.None;
        }

        return new ComplexSelector(parts);
    }

    private CompoundSelector ParseCompoundSelector()
    {
        var simples = new List<SimpleSelector>();
        if (TryParseTypeSelector(out var typeSelector))
        {
            simples.Add(typeSelector);
        }

        while (!IsEnd)
        {
            if (Current is CssSimpleBlock { StartToken: CssTokenType.LeftSquare } block)
            {
                _position++;
                if (TryParseAttributeSelector(block, out var attribute))
                {
                    simples.Add(attribute);
                }

                continue;
            }

            if (TryConsumeHash(out var id))
            {
                simples.Add(new IdSelector(id));
                continue;
            }

            if (TryConsumeClassSelector(out var className))
            {
                simples.Add(new ClassSelector(className));
                continue;
            }

            if (TryConsumePseudoSelector(out var pseudo))
            {
                simples.Add(pseudo);
                continue;
            }

            break;
        }

        return new CompoundSelector(simples);
    }

    private static void EnforcePseudoElementPosition(CompoundSelector compound)
    {
        // Selectors 4 §3.6 / CSS Pseudo-Elements 4 §3: once a pseudo-element appears, only further
        // pseudo-classes (e.g. ::-webkit-scrollbar-thumb:window-inactive, ::before:hover) or chained
        // pseudo-elements may follow. Type/class/id/attribute selectors after a pseudo-element are invalid.
        var seenPseudoElement = false;
        foreach (var simple in compound.SimpleSelectors)
        {
            if (simple is PseudoElementSelector)
            {
                seenPseudoElement = true;
                continue;
            }
            if (seenPseudoElement && simple is not PseudoClassSelector)
            {
                throw new FormatException(
                    "Only pseudo-classes or further pseudo-elements may follow a pseudo-element in a compound selector.");
            }
        }
    }

    private bool TryParseTypeSelector(out SimpleSelector selector)
    {
        selector = null!;
        // Namespace-prefixed selectors: ns|tag, ns|*, *|tag, *|*, |tag, |*
        if (TryParseNamespacedTypeOrUniversal(out selector))
        {
            return true;
        }

        if (TryConsumeIdent(out var name))
        {
            selector = new TypeSelector(name.ToLowerInvariant());
            return true;
        }

        if (TryConsumeDelimiter('*'))
        {
            selector = new UniversalSelector();
            return true;
        }

        return false;
    }

    private bool TryParseNamespacedTypeOrUniversal(out SimpleSelector selector)
    {
        selector = null!;
        // Look ahead for: (ident|*|empty) '|' (ident|*) where '|' is a Delim and the next char is NOT '='
        // and the bar is not the column combinator '||'.
        var save = _position;
        string? ns = null;
        var consumedPrefix = false;

        if (IsToken(CssTokenType.Ident) && IsDelimiter('|', 1) && !IsDelimiter('=', 2) && !IsDelimiter('|', 2))
        {
            ns = TokenAt(0).Value.ToLowerInvariant();
            _position += 2;
            consumedPrefix = true;
        }
        else if (IsDelimiter('*') && IsDelimiter('|', 1) && !IsDelimiter('=', 2) && !IsDelimiter('|', 2))
        {
            ns = "*";
            _position += 2;
            consumedPrefix = true;
        }
        else if (IsDelimiter('|') && !IsDelimiter('=', 1) && !IsDelimiter('|', 1))
        {
            ns = string.Empty;
            _position += 1;
            consumedPrefix = true;
        }

        if (!consumedPrefix)
        {
            return false;
        }

        if (TryConsumeIdent(out var localName))
        {
            selector = new TypeSelector(localName.ToLowerInvariant(), ns);
            return true;
        }
        if (TryConsumeDelimiter('*'))
        {
            selector = new UniversalSelector(ns);
            return true;
        }

        // Rollback if no local name follows.
        _position = save;
        return false;
    }

    private bool TryConsumeClassSelector(out string className)
    {
        className = string.Empty;
        if (!IsDelimiter('.') || !IsToken(CssTokenType.Ident, offset: 1))
        {
            return false;
        }

        _position++;
        className = TokenAt(0).Value;
        _position++;
        return true;
    }

    private bool TryConsumePseudoSelector(out SimpleSelector selector)
    {
        selector = null!;
        if (!ConsumeToken(CssTokenType.Colon))
        {
            return false;
        }

        var pseudoElement = ConsumeToken(CssTokenType.Colon);
        if (Current is CssFunction function)
        {
            _position++;
            var name = function.Name.ToLowerInvariant();
            if (pseudoElement)
            {
                // Functional pseudo-elements (e.g. ::part(), ::slotted()) — store name only for now.
                selector = new PseudoElementSelector(name);
            }
            else
            {
                selector = new PseudoClassSelector(name, ParsePseudoArgument(name, function.Values));
            }
            return true;
        }

        if (TryConsumeIdent(out var ident))
        {
            var name = ident.ToLowerInvariant();
            // Selectors 4 §3.4: ::before/::after/::first-line/::first-letter may also be written with a single colon (legacy CSS2).
            if (!pseudoElement && IsLegacyPseudoElementName(name))
            {
                selector = new PseudoElementSelector(name);
            }
            else
            {
                selector = pseudoElement
                    ? new PseudoElementSelector(name)
                    : new PseudoClassSelector(name);
            }
            return true;
        }

        return false;
    }

    private static bool IsLegacyPseudoElementName(string name)
        => name is "before" or "after" or "first-line" or "first-letter";

    private static object? ParsePseudoArgument(string name, IReadOnlyList<CssComponentValue> values)
        => name switch
        {
            "is" or "where" or "not" or "has" => ParseSelectorList(values),
            "lang" => FirstValueText(values)?.ToLowerInvariant(),
            "dir" => FirstValueText(values)?.ToLowerInvariant(),
            "nth-child" or "nth-last-child" => ParseNthArgument(values),
            "nth-of-type" or "nth-last-of-type" => ParseNthArgument(values).Pattern,
            "heading" => ParseHeadingArgument(values),
            _ => ComponentValuesText(values),
        };

    /// <summary>Parse the <c>:heading()</c> argument: a comma-separated list of plain integers
    /// (Selectors 5 §heading). Any non-integer value, An+B form, calc, or empty list is invalid.</summary>
    private static HeadingArgument ParseHeadingArgument(IReadOnlyList<CssComponentValue> values)
    {
        var levels = new List<int>();
        var expectNumber = true;
        var sawAny = false;

        foreach (var value in values)
        {
            if (value is CssTokenValue { Token.Type: CssTokenType.Whitespace })
            {
                continue;
            }

            if (value is not CssTokenValue token)
            {
                return new HeadingArgument([], IsValid: false);
            }

            if (expectNumber)
            {
                if (token.Token.Type != CssTokenType.Number || !token.Token.IsInteger)
                {
                    return new HeadingArgument([], IsValid: false);
                }

                levels.Add((int)token.Token.Number);
                expectNumber = false;
                sawAny = true;
            }
            else
            {
                if (token.Token.Type != CssTokenType.Comma)
                {
                    return new HeadingArgument([], IsValid: false);
                }

                expectNumber = true;
            }
        }

        // Reject an empty list and a trailing comma (expectNumber still true after a comma).
        if (!sawAny || expectNumber)
        {
            return new HeadingArgument([], IsValid: false);
        }

        return new HeadingArgument(levels);
    }

    /// <summary>Parses An+B with optional `of S` tail (Selectors 4 §15.3).</summary>
    private static NthArgument ParseNthArgument(IReadOnlyList<CssComponentValue> values)
    {
        // Split values at the standalone "of" ident.
        int? ofIndex = null;
        for (var i = 0; i < values.Count; i++)
        {
            if (values[i] is CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var v } } &&
                v.Equals("of", StringComparison.OrdinalIgnoreCase))
            {
                ofIndex = i;
                break;
            }
        }

        if (ofIndex is null)
        {
            var pattern = ParseNthPattern(values, out var valid);
            return new NthArgument(pattern, null, valid);
        }

        var nthValues = values.Take(ofIndex.Value).ToList();
        var ofValues = values.Skip(ofIndex.Value + 1).ToList();
        var pat = ParseNthPattern(nthValues, out var ofValid);
        return new NthArgument(pat, ParseSelectorList(ofValues), ofValid);
    }

    /// <summary>Parse An+B (CSS Syntax 3 §9) from component values, preserving the
    /// whitespace and sign information the An+B grammar depends on. Falls back to
    /// the identity pattern (0n+0) on a parse error so existing selector matching
    /// degrades gracefully; <paramref name="valid"/> reports whether the An+B
    /// microsyntax actually parsed (used by CSSOM selectorText).</summary>
    private static NthPattern ParseNthPattern(IReadOnlyList<CssComponentValue> values, out bool valid)
    {
        var tokens = FlattenTokens(values);
        var parsed = AnbParser.Parse(tokens);
        valid = parsed is not null;
        return parsed ?? new NthPattern(0, 0);
    }

    private static List<CssToken> FlattenTokens(IReadOnlyList<CssComponentValue> values)
    {
        var tokens = new List<CssToken>();
        foreach (var v in values)
        {
            FlattenInto(v, tokens);
        }

        return tokens;
    }

    private static void FlattenInto(CssComponentValue value, List<CssToken> tokens)
    {
        switch (value)
        {
            case CssTokenValue t:
                tokens.Add(t.Token);
                break;
            case CssFunction f:
                tokens.Add(new CssToken(CssTokenType.Function, f.Name));
                foreach (var inner in f.Values)
                {
                    FlattenInto(inner, tokens);
                }

                tokens.Add(new CssToken(CssTokenType.RightParen));
                break;
            case CssSimpleBlock b:
                tokens.Add(new CssToken(b.StartToken));
                foreach (var inner in b.Values)
                {
                    FlattenInto(inner, tokens);
                }

                tokens.Add(new CssToken(MatchingEnd(b.StartToken)));
                break;
        }
    }

    private static CssTokenType MatchingEnd(CssTokenType start) => start switch
    {
        CssTokenType.LeftParen => CssTokenType.RightParen,
        CssTokenType.LeftSquare => CssTokenType.RightSquare,
        CssTokenType.LeftBrace => CssTokenType.RightBrace,
        _ => CssTokenType.Eof,
    };

    private bool TryParseAttributeSelector(CssSimpleBlock block, out AttributeSelector selector)
    {
        selector = null!;
        var tokens = block.Values.OfType<CssTokenValue>()
            .Where(value => value.Token.Type != CssTokenType.Whitespace)
            .Select(value => value.Token)
            .ToList();
        if (tokens.Count == 0 || tokens[0].Type != CssTokenType.Ident)
        {
            return false;
        }

        var name = tokens[0].Value.ToLowerInvariant();
        if (tokens.Count == 1)
        {
            selector = new AttributeSelector(name, AttributeOperator.Exists, null, false);
            return true;
        }

        var index = 1;
        var op = AttributeOperator.Equals;
        if (tokens[index].Type == CssTokenType.Delim && tokens[index].Delimiter != '=')
        {
            op = tokens[index].Delimiter switch
            {
                '~' => AttributeOperator.Includes,
                '|' => AttributeOperator.DashMatch,
                '^' => AttributeOperator.Prefix,
                '$' => AttributeOperator.Suffix,
                '*' => AttributeOperator.Substring,
                _ => AttributeOperator.Equals,
            };
            index++;
        }

        if (index >= tokens.Count || tokens[index].Type != CssTokenType.Delim || tokens[index].Delimiter != '=')
        {
            return false;
        }

        index++;
        if (index >= tokens.Count || tokens[index].Type is not (CssTokenType.Ident or CssTokenType.String))
        {
            return false;
        }

        var value = tokens[index].Value;
        index++;
        var caseInsensitive = index < tokens.Count &&
            tokens[index].Type == CssTokenType.Ident &&
            tokens[index].Value.Equals("i", StringComparison.OrdinalIgnoreCase);

        selector = new AttributeSelector(name, op, value, caseInsensitive);
        return true;
    }

    private bool TryConsumeCombinator(out SelectorCombinator combinator)
    {
        combinator = SelectorCombinator.None;
        if (TryConsumeDelimiter('>'))
        {
            combinator = SelectorCombinator.Child;
            return true;
        }
        if (TryConsumeDelimiter('+'))
        {
            combinator = SelectorCombinator.NextSibling;
            return true;
        }
        if (TryConsumeDelimiter('~'))
        {
            combinator = SelectorCombinator.SubsequentSibling;
            return true;
        }
        // Column combinator '||' — parsed but only meaningful in table contexts.
        if (IsDelimiter('|') && IsDelimiter('|', 1))
        {
            _position += 2;
            combinator = SelectorCombinator.Column;
            return true;
        }

        return false;
    }

    private bool SkipWhitespace()
    {
        var skipped = false;
        while (IsToken(CssTokenType.Whitespace))
        {
            _position++;
            skipped = true;
        }

        return skipped;
    }

    private bool TryConsumeHash(out string value)
    {
        value = string.Empty;
        if (!IsToken(CssTokenType.Hash))
        {
            return false;
        }

        value = TokenAt(0).Value;
        _position++;
        return true;
    }

    private bool TryConsumeIdent(out string value)
    {
        value = string.Empty;
        if (!IsToken(CssTokenType.Ident))
        {
            return false;
        }

        value = TokenAt(0).Value;
        _position++;
        return true;
    }

    private bool ConsumeToken(CssTokenType type)
    {
        if (!IsToken(type))
        {
            return false;
        }

        _position++;
        return true;
    }

    private bool TryConsumeDelimiter(char delimiter)
    {
        if (!IsDelimiter(delimiter))
        {
            return false;
        }

        _position++;
        return true;
    }

    private bool IsDelimiter(char delimiter, int offset = 0)
        => IsToken(CssTokenType.Delim, offset) && TokenAt(offset).Delimiter == delimiter;

    private bool IsToken(CssTokenType type, int offset = 0)
        => CurrentAt(offset) is CssTokenValue { Token.Type: var tokenType } && tokenType == type;

    private CssToken TokenAt(int offset)
        => ((CssTokenValue)CurrentAt(offset)).Token;

    private CssComponentValue Current => CurrentAt(0);

    private CssComponentValue CurrentAt(int offset)
        => _position + offset < _values.Count
            ? _values[_position + offset]
            : new CssTokenValue(new CssToken(CssTokenType.Eof));

    private bool IsEnd => _position >= _values.Count;

    private static string? FirstValueText(IReadOnlyList<CssComponentValue> values)
        => values.OfType<CssTokenValue>()
            .FirstOrDefault(v => v.Token.Type != CssTokenType.Whitespace)
            ?.Token.Value;

    private static string ComponentValuesText(IReadOnlyList<CssComponentValue> values)
        => string.Concat(values.Select(ComponentValueText));

    private static string ComponentValueText(CssComponentValue value)
        => value switch
        {
            CssTokenValue token => TokenText(token.Token),
            CssFunction function => $"{function.Name}({ComponentValuesText(function.Values)})",
            CssSimpleBlock block => $"{BlockStart(block.StartToken)}{ComponentValuesText(block.Values)}{BlockEnd(block.StartToken)}",
            _ => string.Empty,
        };

    private static string TokenText(CssToken token)
        => token.Type switch
        {
            CssTokenType.Ident or CssTokenType.String or CssTokenType.Hash => token.Value,
            CssTokenType.Number => token.Number.ToString(CultureInfo.InvariantCulture),
            CssTokenType.Percentage => token.Number.ToString(CultureInfo.InvariantCulture) + "%",
            CssTokenType.Dimension => token.Number.ToString(CultureInfo.InvariantCulture) + token.Unit,
            CssTokenType.Delim => token.Delimiter.ToString(),
            CssTokenType.Whitespace => " ",
            _ => token.Value,
        };

    private static string BlockStart(CssTokenType type) => type switch
    {
        CssTokenType.LeftParen => "(",
        CssTokenType.LeftSquare => "[",
        CssTokenType.LeftBrace => "{",
        _ => string.Empty,
    };

    private static string BlockEnd(CssTokenType type) => type switch
    {
        CssTokenType.LeftParen => ")",
        CssTokenType.LeftSquare => "]",
        CssTokenType.LeftBrace => "}",
        _ => string.Empty,
    };
}
