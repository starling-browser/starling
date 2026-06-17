// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace Starling.RegExp;

/// <summary>
/// ES2024/ES2025 §22.2.1 regex pattern parser. Source string → AST. Throws
/// <see cref="RegexSyntaxException"/> with a message suitable for surfacing as
/// a JS SyntaxError on any grammar violation.
///
/// Early errors enforced here (§22.2.1.1 Static Semantics: Early Errors):
///   • Duplicate GroupSpecifier names that can both participate (i.e. within the
///     same Disjunction alternative). ES2025 allows the same name across
///     separate alternatives — <c>/(?&lt;a&gt;x)|(?&lt;a&gt;y)/</c> — but not
///     within one — <c>/(?&lt;a&gt;x)(?&lt;a&gt;y)/</c>.
///   • Malformed GroupSpecifier / GroupName (must be a RegExpIdentifierName:
///     empty, numeric-leading, punctuator, non-ID code points, lone surrogate,
///     <c>\u</c>-escapes that decode to non-ID code points, etc.).
///   • <c>\k&lt;name&gt;</c> referencing an undefined group (when the pattern
///     contains any named group, or in Unicode mode). In non-Unicode mode with
///     no named groups, <c>\k</c> is an IdentityEscape (Annex B).
///   • Invalid braced quantifier range (<c>min &gt; max</c>) and
///     InvalidBracedQuantifier in Atom position (<c>/{2}/</c>).
///   • Lookbehind is never quantifiable; lookahead is not quantifiable under the
///     <c>u</c>/<c>v</c> flags.
/// </summary>
public sealed class RegexParser
{
    private readonly string _src;
    private readonly RegexFlags _flags;
    private readonly bool _unicode;
    private int _i;
    private int _captureCount;
    private bool _hasNamedGroups;
    // \k<name> references seen during the parse; validated against the full set
    // of defined GroupNames once parsing completes (so forward references work).
    private readonly List<string> _namedRefs = new();
    // Names that the *enclosing* alternatives have already bound and that can
    // still participate alongside any name we bind from here on. Reset per
    // alternative branch by ParseAlternation.
    private readonly HashSet<string> _activeNames = new();
    public int CaptureCount => _captureCount;
    public Dictionary<string, int> NamedCaptures { get; } = new();

    public RegexParser(string src, RegexFlags flags)
    {
        _src = src ?? string.Empty;
        _flags = flags;
        _unicode = (flags & (RegexFlags.Unicode | RegexFlags.UnicodeSets)) != 0;
    }

    public RegexNode Parse()
    {
        // First pass — count capture groups so backreferences can validate, and
        // pre-scan for the existence of any named group (drives the Annex B
        // \k IdentityEscape rule).
        _captureCount = CountCaptures(_src, out _hasNamedGroups);
        var node = ParseAlternation();
        if (_i != _src.Length)
        {
            throw new RegexSyntaxException($"Unexpected character at index {_i}");
        }
        // §22.2.1.1 — every \k<name> must reference a GroupName defined somewhere
        // in the enclosing Pattern (forward references are permitted, so this is
        // validated only after the whole pattern — and all its GroupSpecifiers —
        // has been parsed).
        foreach (var r in _namedRefs)
        {
            if (!NamedCaptures.ContainsKey(r))
            {
                throw new RegexSyntaxException($"Invalid named backreference: {r}");
            }
        }

        return node;
    }

    private static int CountCaptures(string src, out bool hasNamed)
    {
        var n = 0;
        hasNamed = false;
        for (var i = 0; i < src.Length; i++)
        {
            var c = src[i];
            if (c == '\\') { i++; continue; }
            if (c == '[')
            {
                while (++i < src.Length && src[i] != ']')
                {
                    if (src[i] == '\\')
                    {
                        i++;
                    }
                }
                continue;
            }
            if (c == '(')
            {
                if (i + 1 < src.Length && src[i + 1] == '?')
                {
                    if (i + 2 < src.Length && src[i + 2] == '<')
                    {
                        if (i + 3 < src.Length && (src[i + 3] == '=' || src[i + 3] == '!'))
                        {
                            continue; // lookbehind, not a capture
                        }
                        // named capture
                        n++;
                        hasNamed = true;
                    }
                    continue;
                }
                n++;
            }
        }
        return n;
    }

    private RegexNode ParseAlternation()
    {
        // Each alternative gets a fresh view of which named groups are "active"
        // and could collide with a duplicate. Group names bound in one branch
        // do NOT conflict with the same name in a sibling branch (ES2025), so we
        // snapshot/restore _activeNames around each alternative.
        var outer = new HashSet<string>(_activeNames);
        var alts = new List<RegexNode>();

        _activeNames.Clear();
        _activeNames.UnionWith(outer);
        alts.Add(ParseSequence());

        while (_i < _src.Length && _src[_i] == '|')
        {
            _i++;
            _activeNames.Clear();
            _activeNames.UnionWith(outer);
            alts.Add(ParseSequence());
        }

        // After the disjunction, restore the enclosing scope: the names bound in
        // any branch remain reachable for sequential siblings of this whole
        // disjunction, so propagate their union outward.
        _activeNames.UnionWith(outer);
        return alts.Count == 1 ? alts[0] : new AlternationNode(alts);
    }

    private RegexNode ParseSequence()
    {
        var items = new List<RegexNode>();
        while (_i < _src.Length)
        {
            var c = _src[_i];
            if (c == '|' || c == ')')
            {
                break;
            }

            var atom = ParseAtom();
            atom = MaybeQuantify(atom);
            items.Add(atom);
        }
        if (items.Count == 0)
        {
            return new EmptyNode();
        }

        if (items.Count == 1)
        {
            return items[0];
        }

        return new SequenceNode(items);
    }

    private RegexNode MaybeQuantify(RegexNode atom)
    {
        if (_i >= _src.Length)
        {
            return atom;
        }

        var c = _src[_i];
        int min, max;
        switch (c)
        {
            case '*': _i++; min = 0; max = -1; break;
            case '+': _i++; min = 1; max = -1; break;
            case '?': _i++; min = 0; max = 1; break;
            case '{':
                {
                    var save = _i;
                    if (!TryParseBrace(out min, out max))
                    {
                        _i = save;
                        return atom;
                    }
                    break;
                }
            default: return atom;
        }
        // §22.2.1: a Term of the form Assertion Quantifier is only allowed for
        // QuantifiableAssertion (the Annex-B lookahead form), and never under
        // the u/v flag. Lookbehind is never quantifiable.
        if (atom is LookaroundNode look)
        {
            if (look.Behind)
            {
                throw new RegexSyntaxException("Lookbehind assertion is not quantifiable");
            }

            if (_unicode)
            {
                throw new RegexSyntaxException("Assertion is not quantifiable under the u/v flag");
            }
        }
        bool greedy = true;
        if (_i < _src.Length && _src[_i] == '?')
        {
            greedy = false;
            _i++;
        }
        return new QuantifierNode(atom, min, max, greedy);
    }

    private bool TryParseBrace(out int min, out int max)
    {
        min = 0; max = 0;
        var i = _i;
        if (i >= _src.Length || _src[i] != '{')
        {
            return false;
        }

        i++;
        var start = i;
        while (i < _src.Length && _src[i] >= '0' && _src[i] <= '9')
        {
            i++;
        }

        if (i == start)
        {
            return false;
        }

        if (!int.TryParse(_src[start..i], out min))
        {
            min = int.MaxValue;
        }

        if (i < _src.Length && _src[i] == '}')
        {
            max = min;
            _i = i + 1;
            return true;
        }
        if (i < _src.Length && _src[i] == ',')
        {
            i++;
            var maxStart = i;
            while (i < _src.Length && _src[i] >= '0' && _src[i] <= '9')
            {
                i++;
            }

            if (i < _src.Length && _src[i] == '}')
            {
                if (maxStart == i)
                {
                    max = -1;
                }
                else if (!int.TryParse(_src[maxStart..i], out max))
                {
                    max = int.MaxValue;
                }

                if (max != -1 && max < min)
                {
                    throw new RegexSyntaxException("Numbers out of order in {} quantifier");
                }

                _i = i + 1;
                return true;
            }
        }
        return false;
    }

    private RegexNode ParseAtom()
    {
        var c = _src[_i];
        switch (c)
        {
            case '.':
                _i++;
                return new AnyNode((_flags & RegexFlags.DotAll) != 0);
            case '^':
                _i++;
                return new AnchorNode(AnchorKind.StartOfInput);
            case '$':
                _i++;
                return new AnchorNode(AnchorKind.EndOfInput);
            case '(':
                return ParseGroup();
            case '[':
                return ParseClass();
            case '\\':
                return ParseEscape();
            case '*':
            case '+':
            case '?':
            case '|':
                throw new RegexSyntaxException($"Unexpected '{c}' at index {_i}");
            case '{':
                {
                    // §22.2.1 (Annex B B.1.2): a `{` that *would* form a valid
                    // BracedQuantifier here has nothing to quantify, which is an
                    // InvalidBracedQuantifier — a SyntaxError in BOTH Annex-B and
                    // non-Annex-B environments (e.g. `/{2}/`, `/{2,}/`,
                    // `/{2,3}/`). A `{` that does NOT form a quantifier (e.g.
                    // `a{`, `{x}`) is an ExtendedPatternChar literal in non-u
                    // mode; under u/v the strict grammar rejects it.
                    var save = _i;
                    if (TryParseBrace(out _, out _))
                    {
                        _i = save;
                        throw new RegexSyntaxException($"Nothing to repeat at index {_i}");
                    }
                    _i = save;
                    if (_unicode)
                    {
                        throw new RegexSyntaxException($"Lone quantifier brace '{{' at index {_i}");
                    }

                    goto default;
                }
            case '}':
            case ']':
                // Annex B §B.1.2: with neither the `u` nor `v` flag, `}` `]` that
                // don't close a class are ExtendedPatternCharacters — ordinary
                // literals. Real-world bundles depend on this. Under
                // Unicode/UnicodeSets the strict grammar applies and they remain
                // a SyntaxError.
                if (_unicode)
                {
                    throw new RegexSyntaxException($"Unexpected '{c}' at index {_i}");
                }

                goto default;
            default:
                {
                    _i++;
                    int cp = c;
                    if (char.IsHighSurrogate(c) && _i < _src.Length && char.IsLowSurrogate(_src[_i])
                        && _unicode)
                    {
                        cp = char.ConvertToUtf32(c, _src[_i]);
                        _i++;
                    }
                    return new LiteralNode(cp);
                }
        }
    }

    private RegexNode ParseGroup()
    {
        // We're at '('. Consume it.
        _i++;
        int? capture = null;
        string? name = null;
        bool isLookaround = false;
        bool lookBehind = false;
        bool negative = false;
        if (_i < _src.Length && _src[_i] == '?')
        {
            _i++;
            if (_i >= _src.Length)
            {
                throw new RegexSyntaxException("Unterminated group");
            }

            var ch = _src[_i];
            switch (ch)
            {
                case ':': _i++; break;
                case '=': _i++; isLookaround = true; lookBehind = false; negative = false; break;
                case '!': _i++; isLookaround = true; lookBehind = false; negative = true; break;
                case '<':
                    {
                        _i++;
                        if (_i < _src.Length && (_src[_i] == '=' || _src[_i] == '!'))
                        {
                            isLookaround = true;
                            lookBehind = true;
                            negative = _src[_i] == '!';
                            _i++;
                        }
                        else
                        {
                            // Named capture: GroupSpecifier = `?<` RegExpIdentifierName `>`.
                            name = ParseGroupName();
                            // §22.2.1.1 — two GroupSpecifiers with the same name
                            // that can both participate are an early error. Names
                            // active in the current alternative chain conflict.
                            if (!_activeNames.Add(name))
                            {
                                throw new RegexSyntaxException($"Duplicate capture group name: {name}");
                            }

                            capture = ++_currentCapture;
                            // Keep first index for a name; duplicates across
                            // alternatives share the name but get distinct slots.
                            NamedCaptures.TryAdd(name, capture.Value);
                        }
                        break;
                    }
                default:
                    throw new RegexSyntaxException($"Invalid group qualifier (?{ch} at index {_i})");
            }
        }
        else
        {
            capture = ++_currentCapture;
        }
        var inner = ParseAlternation();
        if (_i >= _src.Length || _src[_i] != ')')
        {
            throw new RegexSyntaxException("Unterminated group");
        }

        _i++;
        if (isLookaround)
        {
            return new LookaroundNode(lookBehind, negative, inner);
        }

        return new GroupNode(capture, name, inner);
    }

    /// <summary>Parses a GroupName body: a RegExpIdentifierName followed by
    /// <c>&gt;</c>. <c>_i</c> must point just past the opening <c>&lt;</c>.
    /// Throws on any malformed name (empty, bad start/continue code point, lone
    /// surrogate, unterminated, etc.).</summary>
    private string ParseGroupName()
    {
        var name = ParseRegExpIdentifierName();
        if (_i >= _src.Length || _src[_i] != '>')
        {
            throw new RegexSyntaxException("Invalid or unterminated capture group name");
        }

        _i++; // consume '>'
        return name;
    }

    /// <summary>§22.2.1 RegExpIdentifierName. Consumes a single identifier-like
    /// name and returns its StringValue (with <c>\u</c> escapes resolved).
    /// Throws on an empty name or any code point that is not a valid
    /// IdentifierStart / IdentifierPart.</summary>
    private string ParseRegExpIdentifierName()
    {
        var sb = new System.Text.StringBuilder();
        var first = true;
        while (_i < _src.Length)
        {
            var c = _src[_i];
            if (c == '>')
            {
                break; // GroupName terminator handled by caller
            }

            int cp;
            if (c == '\\')
            {
                // RegExpIdentifierStart/Part allows a `\u` UnicodeEscapeSequence.
                if (_i + 1 >= _src.Length || _src[_i + 1] != 'u')
                {
                    throw new RegexSyntaxException("Invalid escape in capture group name");
                }

                _i += 2; // past "\u"
                cp = ParseUnicodeEscapeSequence();
            }
            else
            {
                cp = c;
                if (char.IsHighSurrogate(c) && _i + 1 < _src.Length && char.IsLowSurrogate(_src[_i + 1]))
                {
                    cp = char.ConvertToUtf32(c, _src[_i + 1]);
                    _i += 2;
                }
                else
                {
                    // A lone surrogate (or any other char) is taken as-is and
                    // validated below; lone surrogates are not ID code points.
                    _i++;
                }
            }

            if (first)
            {
                if (cp != '$' && cp != '_' && !IsIdStartCp(cp))
                {
                    throw new RegexSyntaxException("Invalid capture group name: bad start character");
                }

                first = false;
            }
            else
            {
                if (cp != '$' && cp != '‌' && cp != '‍' && !IsIdPartCp(cp))
                {
                    throw new RegexSyntaxException("Invalid capture group name: bad continuation character");
                }
            }
            sb.Append(char.ConvertFromUtf32(cp));
        }
        if (sb.Length == 0)
        {
            throw new RegexSyntaxException("Empty capture group name");
        }

        return sb.ToString();
    }

    private static bool IsIdStartCp(int cp)
    {
        if (cp < 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
        {
            return false;
        }

        var cat = CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(cp), 0);
        return cat is UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter
            or UnicodeCategory.TitlecaseLetter or UnicodeCategory.ModifierLetter
            or UnicodeCategory.OtherLetter or UnicodeCategory.LetterNumber;
    }

    private static bool IsIdPartCp(int cp)
    {
        if (cp < 0 || cp > 0x10FFFF || (cp >= 0xD800 && cp <= 0xDFFF))
        {
            return false;
        }

        if (IsIdStartCp(cp))
        {
            return true;
        }

        var cat = CharUnicodeInfo.GetUnicodeCategory(char.ConvertFromUtf32(cp), 0);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ConnectorPunctuation;
    }

    private int _currentCapture;

    private CharClassNode ParseClass()
    {
        _i++; // '['
        bool negated = false;
        if (_i < _src.Length && _src[_i] == '^')
        {
            negated = true; _i++;
        }
        // v-flag set ops not implemented; reject syntactically.
        var ranges = new List<(int, int)>();
        var nestedKlasses = new List<RegexCharClass>();
        while (_i < _src.Length && _src[_i] != ']')
        {
            if ((_flags & RegexFlags.UnicodeSets) != 0 && _src[_i] == '[')
            {
                throw new RegexSyntaxException("v-flag character class set operations are not supported");
            }

            if (_i + 1 < _src.Length && _src[_i] == '&' && _src[_i + 1] == '&')
            {
                throw new RegexSyntaxException("v-flag character class set operations are not supported");
            }

            // §22.2.1 (u/v): in a NonemptyClassRanges, a ClassAtom that is a
            // CharacterClassEscape (\d \w \s …) cannot be an endpoint of a `-`
            // range. Track whether the just-parsed atom was such an escape.
            var loWasClassEscape = NextClassAtomIsClassEscape();
            var lo = ParseClassAtom(ranges, nestedKlasses);
            if (_i + 1 < _src.Length && _src[_i] == '-' && _src[_i + 1] != ']')
            {
                // Lookahead: is the next atom a class escape too?
                var dashAt = _i;
                _i++; // '-'
                var hiWasClassEscape = NextClassAtomIsClassEscape();
                var hi = ParseClassAtom(ranges, nestedKlasses);
                if (_unicode && (loWasClassEscape || hiWasClassEscape))
                {
                    throw new RegexSyntaxException("Invalid character class range with a class escape endpoint");
                }

                if (lo.HasValue && hi.HasValue)
                {
                    if (hi.Value < lo.Value)
                    {
                        throw new RegexSyntaxException("Range out of order in character class");
                    }

                    ranges.Add((lo.Value, hi.Value));
                }
                else
                {
                    // Treat as literal '-' between escapes that produced ranges already
                    if (lo.HasValue)
                    {
                        ranges.Add((lo.Value, lo.Value));
                    }

                    ranges.Add(('-', '-'));
                    if (hi.HasValue)
                    {
                        ranges.Add((hi.Value, hi.Value));
                    }

                    _ = dashAt;
                }
            }
            else if (lo.HasValue)
            {
                ranges.Add((lo.Value, lo.Value));
            }
        }
        if (_i >= _src.Length)
        {
            throw new RegexSyntaxException("Unterminated character class");
        }

        _i++; // ']'
        // Merge nested classes (predefined) into ranges. If any of them is
        // negated independently, we expand it inversely. Simpler approach:
        // merge their ranges only if not negated; if negated, expand to the
        // complement up to U+FFFF.
        foreach (var nested in nestedKlasses)
        {
            for (var cp = 0; cp <= 0xFFFF; cp++)
            {
                if (nested.Contains(cp))
                {
                    ranges.Add((cp, cp));
                }
            }
        }
        var caseInsensitive = (_flags & RegexFlags.IgnoreCase) != 0;
        return new CharClassNode(new RegexCharClass(ranges, negated, caseInsensitive));
    }

    /// <summary>Non-consuming peek: does the class atom at the current position
    /// begin a CharacterClassEscape (<c>\d \D \w \W \s \S \p{…} \P{…}</c>)?</summary>
    private bool NextClassAtomIsClassEscape()
    {
        if (_i + 1 >= _src.Length || _src[_i] != '\\')
        {
            return false;
        }

        return _src[_i + 1] is 'd' or 'D' or 'w' or 'W' or 's' or 'S' or 'p' or 'P';
    }

    private int? ParseClassAtom(List<(int, int)> ranges, List<RegexCharClass> nested)
    {
        if (_src[_i] == '\\')
        {
            // Escape inside class
            _i++;
            if (_i >= _src.Length)
            {
                throw new RegexSyntaxException("Trailing backslash in class");
            }

            var esc = _src[_i];
            switch (esc)
            {
                case 'd': _i++; foreach (var r in RegexCharClass.Digits()) { ranges.Add(r); } return null;
                case 'D': _i++; AddNegatedRanges(ranges, RegexCharClass.Digits()); return null;
                case 'w': _i++; foreach (var r in RegexCharClass.Word()) { ranges.Add(r); } return null;
                case 'W': _i++; AddNegatedRanges(ranges, RegexCharClass.Word()); return null;
                case 's': _i++; foreach (var r in RegexCharClass.Whitespace()) { ranges.Add(r); } return null;
                case 'S': _i++; AddNegatedRanges(ranges, RegexCharClass.Whitespace()); return null;
                case 'p':
                case 'P':
                    _i++;
                    nested.Add(ParsePropertyEscape(esc == 'P'));
                    return null;
                case 'n': _i++; return '\n';
                case 'r': _i++; return '\r';
                case 't': _i++; return '\t';
                case 'v': _i++; return '\v';
                case 'f': _i++; return '\f';
                case '0': _i++; return 0;
                case 'b': _i++; return 0x08; // backspace in class
                case 'x': _i++; return ParseHex(2);
                case 'u': _i++; return ParseUnicodeEscapeSequence();
                case 'c': _i++; return ParseControlChar();
                default:
                    // §22.2.1 (u/v): only IdentityEscape characters
                    // (SyntaxCharacter or '/') may be escaped. A letter/digit
                    // identity escape is a SyntaxError under u/v.
                    if (_unicode && !IsClassIdentityEscape(esc))
                    {
                        throw new RegexSyntaxException($"Invalid escape '\\{esc}' in character class under u/v flag");
                    }

                    _i++;
                    return esc;
            }
        }
        else
        {
            int cp = _src[_i];
            _i++;
            if (_unicode && char.IsHighSurrogate((char)cp) && _i < _src.Length && char.IsLowSurrogate(_src[_i]))
            {
                cp = char.ConvertToUtf32((char)cp, _src[_i]);
                _i++;
            }
            return cp;
        }
    }

    private static bool IsClassIdentityEscape(char c)
        // IdentityEscape :: SyntaxCharacter | '/' (the chars that must be
        // escaped). Also '-' is a valid ClassEscape under u/v.
        => c is '^' or '$' or '\\' or '.' or '*' or '+' or '?' or '(' or ')'
            or '[' or ']' or '{' or '}' or '|' or '/' or '-';

    private static void AddNegatedRanges(List<(int, int)> dest, List<(int, int)> src)
    {
        // Add the complement of src ranges to dest. Limit to BMP.
        src.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        int cursor = 0;
        foreach (var (lo, hi) in src)
        {
            if (lo > cursor)
            {
                dest.Add((cursor, lo - 1));
            }

            cursor = hi + 1;
        }
        if (cursor <= 0xFFFF)
        {
            dest.Add((cursor, 0xFFFF));
        }
    }

    /// <summary>Caller must have already consumed the leading 'p'/'P'; we
    /// expect <c>_i</c> to point at the opening brace.</summary>
    private RegexCharClass ParsePropertyEscape(bool negated)
    {
        if (_i >= _src.Length || _src[_i] != '{')
        {
            throw new RegexSyntaxException("Invalid \\p escape: missing '{'");
        }

        _i++;
        var start = _i;
        while (_i < _src.Length && _src[_i] != '}')
        {
            _i++;
        }

        if (_i >= _src.Length)
        {
            throw new RegexSyntaxException("Unterminated \\p{ escape");
        }

        var raw = _src[start.._i];
        _i++;
        // Accept "Property=Value" or just "Value"
        var name = raw;
        var eq = raw.IndexOf('=');
        if (eq >= 0)
        {
            name = raw[(eq + 1)..];
        }

        if (!RegexCharClass.SupportedProperties.Contains(name))
        {
            throw new RegexSyntaxException($"Unsupported Unicode property: {name}");
        }
        // Use precomputed (cached at type init) ranges to avoid per-\p scan + allocs.
        var baseRanges = RegexCharClass.GetPropertyRanges(name);
        var caseInsensitive = (_flags & RegexFlags.IgnoreCase) != 0;
        return new RegexCharClass(baseRanges, negated, caseInsensitive);
    }

    private RegexNode ParseEscape()
    {
        _i++; // consume '\'
        if (_i >= _src.Length)
        {
            throw new RegexSyntaxException("Trailing backslash");
        }

        var c = _src[_i];
        switch (c)
        {
            case 'b': _i++; return new AnchorNode(AnchorKind.WordBoundary);
            case 'B': _i++; return new AnchorNode(AnchorKind.NonWordBoundary);
            case 'd': _i++; return new CharClassNode(new RegexCharClass(RegexCharClass.Digits(), false, false));
            case 'D': _i++; return new CharClassNode(new RegexCharClass(RegexCharClass.Digits(), true, false));
            case 'w': _i++; return new CharClassNode(new RegexCharClass(RegexCharClass.Word(), false, false));
            case 'W': _i++; return new CharClassNode(new RegexCharClass(RegexCharClass.Word(), true, false));
            case 's': _i++; return new CharClassNode(new RegexCharClass(RegexCharClass.Whitespace(), false, false));
            case 'S': _i++; return new CharClassNode(new RegexCharClass(RegexCharClass.Whitespace(), true, false));
            case 'p':
            case 'P':
                {
                    var negated = c == 'P';
                    _i++;
                    var klass = ParsePropertyEscape(negated);
                    return new CharClassNode(klass);
                }
            case 'n': _i++; return new LiteralNode('\n');
            case 'r': _i++; return new LiteralNode('\r');
            case 't': _i++; return new LiteralNode('\t');
            case 'v': _i++; return new LiteralNode('\v');
            case 'f': _i++; return new LiteralNode('\f');
            case '0':
                _i++;
                if (_i < _src.Length && _src[_i] >= '0' && _src[_i] <= '9')
                {
                    // \0 followed by a digit is a LegacyOctalEscape — allowed in
                    // Annex B (non-u) but a SyntaxError under u/v.
                    if (_unicode)
                    {
                        throw new RegexSyntaxException("\\0 may not be followed by another digit under u/v flag");
                    }
                }
                return new LiteralNode(0);
            case 'x': _i++; return new LiteralNode(ParseHex(2));
            case 'u': _i++; return new LiteralNode(ParseUnicodeEscapeSequence());
            case 'c': _i++; return new LiteralNode(ParseControlChar());
            case 'k':
                {
                    // Annex B (non-u): \k is only a named backreference when the
                    // pattern actually contains a named group; otherwise it is an
                    // IdentityEscape for the literal 'k'. Under u/v it is always a
                    // named backreference (so a malformed/dangling \k is an error).
                    if (!_unicode && !_hasNamedGroups)
                    {
                        _i++;
                        return new LiteralNode('k');
                    }
                    _i++;
                    if (_i >= _src.Length || _src[_i] != '<')
                    {
                        throw new RegexSyntaxException("\\k must be followed by <name>");
                    }

                    _i++;
                    var name = ParseRegExpIdentifierName();
                    if (_i >= _src.Length || _src[_i] != '>')
                    {
                        throw new RegexSyntaxException("Unterminated \\k<name>");
                    }

                    _i++; // '>'
                    // Defer the "is defined" check to end-of-parse so forward
                    // references like /\k<a>(?<a>x)/ are accepted.
                    _namedRefs.Add(name);
                    return new NamedBackrefNode(name);
                }
            default:
                if (c >= '1' && c <= '9')
                {
                    var start = _i;
                    while (_i < _src.Length && _src[_i] >= '0' && _src[_i] <= '9')
                    {
                        _i++;
                    }

                    var n = int.Parse(_src[start.._i]);
                    // §22.2.1.1 — under u/v a DecimalEscape must reference an
                    // existing capture group (no octal fallback / out-of-bounds).
                    if (_unicode && n > _captureCount)
                    {
                        throw new RegexSyntaxException($"Invalid backreference \\{n}: only {_captureCount} group(s)");
                    }

                    return new BackrefNode(n);
                }
                // IdentityEscape: under u/v only SyntaxCharacters and '/' may be
                // escaped; an alphanumeric identity escape is a SyntaxError.
                if (_unicode && !IsIdentityEscape(c))
                {
                    throw new RegexSyntaxException($"Invalid escape '\\{c}' under u/v flag");
                }

                _i++;
                return new LiteralNode(c);
        }
    }

    private static bool IsIdentityEscape(char c)
        => c is '^' or '$' or '\\' or '.' or '*' or '+' or '?' or '(' or ')'
            or '[' or ']' or '{' or '}' or '|' or '/';

    private int ParseHex(int digits)
    {
        if (_i + digits > _src.Length)
        {
            throw new RegexSyntaxException("Invalid hex escape");
        }

        var hex = _src.Substring(_i, digits);
        if (!int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v))
        {
            throw new RegexSyntaxException("Invalid hex escape");
        }

        _i += digits;
        return v;
    }

    private int ParseUnicodeEscapeSequence()
    {
        var cp = ParseUnicodeEscape(out var braced);
        if (_unicode && !braced && IsLeadSurrogate(cp) && TryConsumeTrailingSurrogateEscape(out var trail))
        {
            return char.ConvertToUtf32((char)cp, (char)trail);
        }

        return cp;
    }

    private int ParseUnicodeEscape(out bool braced)
    {
        // \u{HHHH...} (u flag) or \uHHHH
        braced = false;
        if (_unicode && _i < _src.Length && _src[_i] == '{')
        {
            braced = true;
            _i++;
            var start = _i;
            while (_i < _src.Length && _src[_i] != '}')
            {
                _i++;
            }

            if (_i >= _src.Length)
            {
                throw new RegexSyntaxException("Unterminated \\u{ escape");
            }

            var hex = _src[start.._i];
            if (hex.Length == 0
                || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                throw new RegexSyntaxException("Invalid \\u{ escape");
            }
            // §22.2.1 — CodePoint must be <= 0x10FFFF.
            if (v > 0x10FFFF)
            {
                throw new RegexSyntaxException("\\u{} code point out of range");
            }

            _i++; // '}'
            return v;
        }
        return ParseHex(4);
    }

    private bool TryConsumeTrailingSurrogateEscape(out int trail)
    {
        trail = 0;
        if (_i + 5 >= _src.Length || _src[_i] != '\\' || _src[_i + 1] != 'u')
        {
            return false;
        }

        if (!TryParseFixedHex(_i + 2, out var value) || !IsTrailSurrogate(value))
        {
            return false;
        }

        _i += 6;
        trail = value;
        return true;
    }

    private bool TryParseFixedHex(int start, out int value)
    {
        value = 0;
        if (start + 4 > _src.Length)
        {
            return false;
        }

        for (var j = 0; j < 4; j++)
        {
            var digit = HexValue(_src[start + j]);
            if (digit < 0)
            {
                return false;
            }

            value = (value << 4) | digit;
        }
        return true;
    }

    private static bool IsLeadSurrogate(int cp) => cp >= 0xD800 && cp <= 0xDBFF;

    private static bool IsTrailSurrogate(int cp) => cp >= 0xDC00 && cp <= 0xDFFF;

    private static int HexValue(char c)
    {
        if (c >= '0' && c <= '9')
        {
            return c - '0';
        }

        if (c >= 'a' && c <= 'f')
        {
            return c - 'a' + 10;
        }

        if (c >= 'A' && c <= 'F')
        {
            return c - 'A' + 10;
        }

        return -1;
    }

    private int ParseControlChar()
    {
        if (_i >= _src.Length)
        {
            throw new RegexSyntaxException("Invalid \\c escape");
        }

        var c = _src[_i];
        if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
        {
            _i++;
            return c % 32;
        }
        // \c not followed by an ASCII letter is not a valid ControlEscape.
        // (Under u/v this is strictly a SyntaxError; preserved as an error in
        // non-u mode too, matching the prior behavior.)
        throw new RegexSyntaxException("Invalid \\c escape");
    }
}

public sealed class RegexSyntaxException : System.Exception
{
    public RegexSyntaxException() { }
    public RegexSyntaxException(string message) : base(message) { }
    public RegexSyntaxException(string message, System.Exception innerException) : base(message, innerException) { }
}
