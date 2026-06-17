using System.Text;
using Starling.Css.Parser;
using Starling.Css.Selectors;

namespace Starling.Css.Cssom;

/// <summary>
/// A live, mutable CSSOM declaration block (CSSOM §6.4 CSSStyleDeclaration),
/// backing both a style rule's <c>.style</c> and (via the binding layer) inline
/// styles. Declarations are an ordered list; later duplicates win on read but the
/// CSSOM keeps a single entry per property name.
/// </summary>
public sealed class CssomDeclarationBlock
{
    private readonly List<Entry> _entries = new();

    public sealed record Entry(string Name, string Value, bool Important);

    public CssomDeclarationBlock() { }

    public CssomDeclarationBlock(IEnumerable<CssDeclaration> declarations)
    {
        foreach (var d in declarations)
        {
            var value = SerializeComponentValues(d.Value);
            SetRaw(d.Name.ToLowerInvariant(), value, d.Important);
        }
    }

    public int Count => _entries.Count;

    public string ItemName(int index) =>
        index >= 0 && index < _entries.Count ? _entries[index].Name : string.Empty;

    public string GetPropertyValue(string name)
    {
        name = name.Trim();
        var key = name.StartsWith("--", StringComparison.Ordinal) ? name : name.ToLowerInvariant();
        foreach (var e in _entries)
        {
            if (e.Name == key)
            {
                return e.Value;
            }
        }

        return string.Empty;
    }

    public string GetPropertyPriority(string name)
    {
        name = name.Trim();
        var key = name.StartsWith("--", StringComparison.Ordinal) ? name : name.ToLowerInvariant();
        foreach (var e in _entries)
        {
            if (e.Name == key)
            {
                return e.Important ? "important" : string.Empty;
            }
        }

        return string.Empty;
    }

    /// <summary>CSSOM §6.4.2 setProperty: parse + canonicalize the value, and
    /// only store it when it is well-formed; otherwise leave the block unchanged.</summary>
    public void SetProperty(string name, string value, string? priority)
    {
        name = name.Trim();
        var key = name.StartsWith("--", StringComparison.Ordinal) ? name : name.ToLowerInvariant();
        var important = string.Equals(priority?.Trim(), "important", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(value))
        {
            RemoveProperty(key);
            return;
        }

        // Custom properties take the value verbatim (trimmed); known properties
        // are canonicalized and validated.
        string stored;
        if (key.StartsWith("--", StringComparison.Ordinal))
        {
            stored = value.Trim();
        }
        else if (key.Equals("unicode-range", StringComparison.OrdinalIgnoreCase))
        {
            // CSS Syntax §4.3.10 <urange> — special canonicalization.
            var canonical = UrangeParser.Canonicalize(value);
            if (canonical is null)
            {
                return; // invalid urange — leave property unchanged
            }

            stored = canonical;
        }
        else
        {
            var canonical = CssValueSerializer.Canonicalize(value);
            if (canonical is null)
            {
                return; // invalid value — do nothing (spec step)
            }

            stored = canonical;
        }
        SetRaw(key, stored, important);
    }

    private void SetRaw(string key, string value, bool important)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Name == key)
            {
                _entries[i] = new Entry(key, value, important);
                return;
            }
        }
        _entries.Add(new Entry(key, value, important));
    }

    public string RemoveProperty(string name)
    {
        name = name.Trim();
        var key = name.StartsWith("--", StringComparison.Ordinal) ? name : name.ToLowerInvariant();
        for (var i = 0; i < _entries.Count; i++)
        {
            if (_entries[i].Name == key)
            {
                var old = _entries[i].Value;
                _entries.RemoveAt(i);
                return old;
            }
        }
        return string.Empty;
    }

    /// <summary>CSSOM §6.4.1 cssText getter — "serialize a CSS declaration block".</summary>
    public string CssText
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var e in _entries)
            {
                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(e.Name).Append(": ").Append(e.Value);
                if (e.Important)
                {
                    sb.Append(" !important");
                }

                sb.Append(';');
            }
            return sb.ToString();
        }
        set
        {
            _entries.Clear();
            var decls = new CssParser(value ?? string.Empty).ParseDeclarationList();
            foreach (var d in decls)
            {
                SetRaw(d.Name.ToLowerInvariant(), SerializeComponentValues(d.Value), d.Important);
            }
        }
    }

    private static string SerializeComponentValues(IReadOnlyList<CssComponentValue> values)
    {
        // Reuse the canonical value serializer by reconstructing raw text and
        // re-tokenizing; falls back to the raw concatenation if canonicalization
        // rejects the value (e.g. stylesheet declarations we still want to expose).
        var raw = ComponentValuesToText(values);
        return CssValueSerializer.Canonicalize(raw) ?? raw.Trim();
    }

    private static string ComponentValuesToText(IReadOnlyList<CssComponentValue> values)
    {
        // Top-level declaration values lose interior whitespace during parsing
        // (the parser collapses whitespace for non-custom properties). Re-insert a
        // single space between adjacent top-level component values so multi-token
        // values such as `1px solid black` round-trip; Canonicalize then normalizes
        // comma spacing. Whitespace inside functions/blocks is preserved verbatim.
        var sb = new StringBuilder();
        for (var i = 0; i < values.Count; i++)
        {
            var v = values[i];
            if (v is CssTokenValue { Token.Type: Tokenizer.CssTokenType.Whitespace })
            {
                sb.Append(' ');
                continue;
            }
            if (sb.Length > 0 && sb[^1] != ' ')
            {
                sb.Append(' ');
            }

            sb.Append(ComponentValueText(v));
        }
        return sb.ToString();
    }

    private static string ComponentValueText(CssComponentValue value) => value switch
    {
        CssTokenValue token => TokenText(token.Token),
        CssFunction function => $"{function.Name}({ComponentValuesToText(function.Values)})",
        CssSimpleBlock block => $"{BlockStart(block.StartToken)}{ComponentValuesToText(block.Values)}{BlockEnd(block.StartToken)}",
        _ => string.Empty,
    };

    private static string TokenText(Tokenizer.CssToken token) => token.Type switch
    {
        Tokenizer.CssTokenType.Ident or Tokenizer.CssTokenType.String => token.Value,
        Tokenizer.CssTokenType.Hash => "#" + token.Value,
        Tokenizer.CssTokenType.Number => CssValueSerializer.SerializeNumber(token.Number),
        Tokenizer.CssTokenType.Percentage => CssValueSerializer.SerializeNumber(token.Number) + "%",
        Tokenizer.CssTokenType.Dimension => CssValueSerializer.SerializeNumber(token.Number) + token.Unit,
        Tokenizer.CssTokenType.Delim => token.Delimiter.ToString(),
        Tokenizer.CssTokenType.Whitespace => " ",
        Tokenizer.CssTokenType.Colon => ":",
        Tokenizer.CssTokenType.Comma => ",",
        Tokenizer.CssTokenType.Url => "url(" + token.Value + ")",
        _ => token.Value,
    };

    private static string BlockStart(Tokenizer.CssTokenType type) => type switch
    {
        Tokenizer.CssTokenType.LeftParen => "(",
        Tokenizer.CssTokenType.LeftSquare => "[",
        Tokenizer.CssTokenType.LeftBrace => "{",
        _ => string.Empty,
    };

    private static string BlockEnd(Tokenizer.CssTokenType type) => type switch
    {
        Tokenizer.CssTokenType.LeftParen => ")",
        Tokenizer.CssTokenType.LeftSquare => "]",
        Tokenizer.CssTokenType.LeftBrace => "}",
        _ => string.Empty,
    };
}

/// <summary>A live CSSOM style rule (CSSOM §6.4 CSSStyleRule) with a mutable
/// selector text and declaration block.</summary>
public sealed class CssomStyleRule
{
    public CssomStyleRule(string selectorText, CssomDeclarationBlock style)
    {
        SelectorTextRaw = selectorText;
        Style = style;
    }

    /// <summary>The current selector text. Setting goes through
    /// <see cref="TrySetSelectorText"/>; this holds the canonical serialization.</summary>
    public string SelectorTextRaw { get; private set; }

    public CssomDeclarationBlock Style { get; }

    /// <summary>CSSOM §6.4.3 selectorText setter: parse the new selector; on a
    /// parse error, leave it unchanged (return false). On success, store the
    /// canonical serialization.</summary>
    public bool TrySetSelectorText(string value)
    {
        var parsed = TryParseSelectorList(value);
        if (parsed is null || parsed.Selectors.Count == 0)
        {
            return false;
        }

        SelectorTextRaw = SelectorSerializer.Serialize(parsed);
        return true;
    }

    private static SelectorList? TryParseSelectorList(string source)
    {
        try
        {
            var sheet = CssParser.ParseStyleSheet($"{source} {{ }}");
            var rule = sheet.Rules.OfType<StyleRule>().FirstOrDefault();
            if (rule is null)
            {
                return null;
            }

            var list = SelectorParser.ParseSelectorList(rule.Prelude);
            if (list.Selectors.Count == 0)
            {
                return null;
            }
            // Reject selectors that failed to fully parse (e.g. an invalid An+B
            // microsyntax inside :nth-child()).
            if (!SelectorListIsValid(list))
            {
                return null;
            }

            return list;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Validity gate for selector parsing: every complex selector must
    /// have produced at least one compound, and any nth-style pseudo argument must
    /// have parsed cleanly via the An+B grammar.</summary>
    private static bool SelectorListIsValid(SelectorList list)
    {
        foreach (var complex in list.Selectors)
        {
            if (complex.Parts.Count == 0)
            {
                return false;
            }

            foreach (var part in complex.Parts)
            {
                if (part.Compound.SimpleSelectors.Count == 0)
                {
                    return false;
                }

                foreach (var simple in part.Compound.SimpleSelectors)
                {
                    if (simple is PseudoClassSelector { Argument: NthArgument { IsValid: false } })
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }
}

/// <summary>A live CSSOM stylesheet (CSSOM §6.2 CSSStyleSheet) over the rules of
/// one <c>&lt;style&gt;</c>/<c>&lt;link&gt;</c>. Only style rules are exposed as
/// CSSStyleRule objects; other rules (at-rules) are preserved as opaque entries
/// so indices remain stable.</summary>
public sealed class CssomStyleSheet
{
    private readonly List<CssomStyleRule?> _rules = new();
    private readonly List<string?> _atRuleNames = new();

    public CssomStyleSheet(StyleSheet parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        foreach (var rule in parsed.Rules)
        {
            if (rule is StyleRule sr)
            {
                var selectorText = SerializeSelectorPrelude(sr.Prelude);
                var block = new CssomDeclarationBlock(sr.Declarations);
                _rules.Add(new CssomStyleRule(selectorText, block));
                _atRuleNames.Add(null);
            }
            else
            {
                // At-rule etc. — kept as a placeholder so cssRules indices match
                // the parsed rule order. Not exposed as a CSSStyleRule, but we keep
                // the at-rule keyword (e.g. "media", "page") so the binding can
                // report the correct CSSRule.type (CSSOM §6.4 the CSSRule interface).
                _rules.Add(null);
                _atRuleNames.Add(rule is AtRule at ? at.Name?.ToLowerInvariant() : null);
            }
        }
    }

    /// <summary>The style rules, including null placeholders for non-style rules.</summary>
    public IReadOnlyList<CssomStyleRule?> Rules => _rules;

    /// <summary>The lowercased at-rule keyword at <paramref name="index"/> when the
    /// rule there is a non-style at-rule (e.g. "media", "page", "font-face",
    /// "namespace", "import", "keyframes", "supports"); otherwise null.</summary>
    public string? AtRuleNameAt(int index) =>
        index >= 0 && index < _atRuleNames.Count ? _atRuleNames[index] : null;

    private static string SerializeSelectorPrelude(IReadOnlyList<CssComponentValue> prelude)
    {
        try
        {
            var list = SelectorParser.ParseSelectorList(prelude);
            return list.Selectors.Count == 0 ? string.Empty : SelectorSerializer.Serialize(list);
        }
        catch
        {
            return string.Empty;
        }
    }
}
