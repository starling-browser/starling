using System.Text.RegularExpressions;
using Starling.RegExp;
using NetRegex = System.Text.RegularExpressions.Regex;

namespace Starling.Js.Runtime.Regex;

/// <summary>
/// The .NET backend: delegates matching to
/// <see cref="System.Text.RegularExpressions"/> for the translatable subset of
/// JS patterns, falling back to the Pike VM per pattern for anything outside
/// that subset.
/// </summary>
/// <remarks>
/// Correctness contract: the Pike VM form is the source of truth. The caller
/// always compiles it first (so JS early-error <c>SyntaxError</c>s and capture
/// metadata are identical), then hands it here. We translate only patterns we
/// can prove behave the same under <c>RegexOptions.ECMAScript</c>; if
/// construction throws or the pattern is not translatable we return
/// <c>null</c> and the caller keeps the Pike VM. The .NET path must never throw
/// a different JS-visible error than the Pike VM would.
/// </remarks>
public sealed partial class DotNetRegexMatcher : IRegexMatcher
{
    private readonly NetRegex _regex;
    private readonly bool _sticky;

    // JS group index (0-based array over groups 0..CaptureCount) → resolver.
    // .NET numbers named groups after unnamed; JS numbers strictly
    // left-to-right. We resolve each JS index by .NET group NAME for named
    // groups and by .NET positional number for unnamed groups, so
    // Group(jsIndex) always returns the JS-correct capture.
    private readonly string[] _groupKey;

    // Metadata mirrors the Pike VM form exactly (the source of truth).
    private readonly string _source;
    private readonly RegexFlags _flags;
    private readonly int _captureCount;
    private readonly IReadOnlyDictionary<string, int> _namedCaptures;

    public string Source => _source;
    public RegexFlags Flags => _flags;
    public int CaptureCount => _captureCount;
    public IReadOnlyDictionary<string, int> NamedCaptures => _namedCaptures;

    /// <summary>Delegate a literal whole-string replace to System.Text's
    /// single-pass <see cref="NetRegex.Replace(string,string)"/> — the same
    /// optimized path Jint uses, avoiding a per-match <c>Match</c> allocation.
    /// Disabled for sticky regexes (Replace scans forward rather than anchoring
    /// at lastIndex). The replacement is literal (no <c>$</c>), so .NET performs
    /// no substitution and matches JS semantics exactly.</summary>
    public string? TryReplaceLiteral(string input, string literalReplacement, bool global)
    {
        if (_sticky)
        {
            return null;
        }

        return global
            ? _regex.Replace(input, literalReplacement)
            : _regex.Replace(input, literalReplacement, 1);
    }

    private DotNetRegexMatcher(NetRegex regex, bool sticky, string[] groupKey, CompiledRegex pikeForm)
    {
        _regex = regex;
        _sticky = sticky;
        _groupKey = groupKey;
        _source = pikeForm.Source;
        _flags = pikeForm.Flags;
        _captureCount = pikeForm.CaptureCount;
        _namedCaptures = pikeForm.NamedCaptures;
    }

    /// <summary>Build a .NET-backed matcher from the already-compiled Pike VM
    /// form, or return <c>null</c> when the pattern is not translatable (caller
    /// falls back to the Pike VM). Never throws a JS-visible error.</summary>
    internal static IRegexMatcher? TryCreate(CompiledRegex pikeForm)
    {
        var source = pikeForm.Source;
        var flags = pikeForm.Flags;

        if (!IsTranslatable(source, flags))
        {
            return null;
        }

        var options = RegexOptions.ECMAScript | RegexOptions.CultureInvariant;
        if ((flags & RegexFlags.IgnoreCase) != 0)
        {
            options |= RegexOptions.IgnoreCase;
        }

        if ((flags & RegexFlags.Multiline) != 0)
        {
            options |= RegexOptions.Multiline;
        }

        if ((flags & RegexFlags.DotAll) != 0)
        {
            options |= RegexOptions.Singleline;
        }

        NetRegex regex;
        try
        {
            regex = new NetRegex(source, options);
        }
        catch (ArgumentException)
        {
            // RegexOptions.ECMAScript rejects some constructs (e.g. it cannot be
            // combined with certain features) and bad-for-.NET patterns throw
            // here (RegexParseException derives from ArgumentException). Fall back
            // to the Pike VM rather than surfacing a non-JS error.
            return null;
        }

        var groupKey = BuildGroupKey(pikeForm);
        var sticky = (flags & RegexFlags.Sticky) != 0;
        return new DotNetRegexMatcher(regex, sticky, groupKey, pikeForm);
    }

    // Map JS group index (0..CaptureCount) to the key used to fetch the .NET
    // Match.Groups entry. JS numbers ALL groups strictly left-to-right; .NET
    // numbers UNNAMED groups first (1,2,...) and named groups after, but exposes
    // named groups by NAME. So:
    //   * named JS group  → its name (Match.Groups[name] works regardless of
    //                       .NET's internal numbering)
    //   * unnamed JS group → its 1-based position AMONG THE UNNAMED GROUPS, which
    //                        is exactly its .NET positional number
    // Index 0 is always the whole match ("0").
    private static string[] BuildGroupKey(CompiledRegex pikeForm)
    {
        int count = pikeForm.CaptureCount;
        var key = new string[count + 1];
        key[0] = "0";
        // Invert NamedCaptures (name → JS index) into JS index → name.
        var nameByIndex = new Dictionary<int, string>();
        foreach (var (name, idx) in pikeForm.NamedCaptures)
        {
            nameByIndex[idx] = name;
        }

        int unnamedSeq = 0; // .NET assigns unnamed groups numbers 1,2,... in order.
        for (int i = 1; i <= count; i++)
        {
            if (nameByIndex.TryGetValue(i, out var name))
            {
                key[i] = name;
            }
            else
            {
                unnamedSeq++;
                key[i] = unnamedSeq.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return key;
    }

    // ------------------------------------------------------------------
    //                       Translatability check
    // ------------------------------------------------------------------
    // Fall back to the Pike VM whenever any of these is present:
    //   * the u (Unicode) or v (UnicodeSets) flag
    //   * \p{ or \P{ property escapes
    //   * the i flag together with any non-ASCII character or a \u/\x escape
    //     above 0x7F in the source
    // These are the spots where .NET's ECMAScript mode and JS diverge.
    private static bool IsTranslatable(string source, RegexFlags flags)
    {
        if ((flags & (RegexFlags.Unicode | RegexFlags.UnicodeSets)) != 0)
        {
            return false;
        }

        bool ignoreCase = (flags & RegexFlags.IgnoreCase) != 0;

        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];

            if (c == '\\' && i + 1 < source.Length)
            {
                char n = source[i + 1];
                // \p{...} / \P{...} Unicode property escapes — not in ECMAScript mode.
                if (n == 'p' || n == 'P')
                {
                    return false;
                }

                if (ignoreCase && (n == 'u' || n == 'x'))
                {
                    int val = ReadHexEscapeValue(source, i + 1);
                    if (val > 0x7F)
                    {
                        return false;
                    }
                }
                // Skip the escaped char so a literal "\\" or "\p" inside text
                // is not double-scanned.
                i++;
                continue;
            }

            if (ignoreCase && c > 0x7F)
            {
                return false;
            }
        }

        return true;
    }

    // Parse the numeric value of a \xHH, \uHHHH, or \u{...} escape starting at
    // escapeCharPos (the position of 'x' or 'u'). Returns -1 when malformed,
    // which keeps the pattern translatable (the Pike VM would reject a truly
    // bad escape; a malformed one here just means "not > 0x7F").
    private static int ReadHexEscapeValue(string s, int escapeCharPos)
    {
        char kind = s[escapeCharPos];
        int p = escapeCharPos + 1;
        if (kind == 'x')
        {
            if (p + 1 >= s.Length)
            {
                return -1;
            }

            return TryHex2(s, p);
        }
        // kind == 'u'
        if (p < s.Length && s[p] == '{')
        {
            int close = s.IndexOf('}', p + 1);
            if (close < 0)
            {
                return -1;
            }

            return TryHexN(s, p + 1, close);
        }
        if (p + 3 >= s.Length)
        {
            return -1;
        }

        return TryHexN(s, p, p + 4);
    }

    private static int TryHex2(string s, int p)
    {
        int hi = HexVal(s[p]);
        int lo = HexVal(s[p + 1]);
        if (hi < 0 || lo < 0)
        {
            return -1;
        }

        return (hi << 4) | lo;
    }

    private static int TryHexN(string s, int start, int end)
    {
        int val = 0;
        for (int i = start; i < end; i++)
        {
            int h = HexVal(s[i]);
            if (h < 0)
            {
                return -1;
            }

            val = (val << 4) | h;
        }
        return val;
    }

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    // ------------------------------------------------------------------
    //                       Exec
    // ------------------------------------------------------------------
    public IRegexMatch? Exec(string input, int start)
    {
        // Guard the same way the intrinsic already does before calling us.
        if (start < 0)
        {
            start = 0;
        }

        if (start > input.Length)
        {
            return null;
        }

        // Non-sticky: Match(input, start) scans forward from start, matching the
        // Pike VM contract. Sticky: scan, then reject unless anchored at start.
        var m = _regex.Match(input, start);
        if (!m.Success)
        {
            return null;
        }

        if (_sticky && m.Index != start)
        {
            return null;
        }

        return new DotNetRegexMatch(m, input, _groupKey);
    }

    public bool ExecSpans(string input, int start, int[] spanBuffer, out int matchStart, out int matchEnd)
    {
        if (start < 0)
        {
            start = 0;
        }

        if (start > input.Length)
        {
            matchStart = -1;
            matchEnd = -1;
            return false;
        }

        var m = _regex.Match(input, start);
        if (!m.Success || (_sticky && m.Index != start))
        {
            matchStart = -1;
            matchEnd = -1;
            return false;
        }

        // Group 0 is the whole match.
        spanBuffer[0] = m.Index;
        spanBuffer[1] = m.Index + m.Length;
        for (int i = 1; i <= _captureCount; i++)
        {
            // _groupKey routes JS index → .NET group key (name for named,
            // positional number for unnamed), the same resolution Group/
            // GroupSpan use, so the JS numbering stays correct.
            var g = i < _groupKey.Length ? m.Groups[_groupKey[i]] : null;
            int si = i * 2;
            if (g is { Success: true })
            {
                spanBuffer[si] = g.Index;
                spanBuffer[si + 1] = g.Index + g.Length;
            }
            else
            {
                spanBuffer[si] = -1;
                spanBuffer[si + 1] = -1;
            }
        }
        matchStart = m.Index;
        matchEnd = m.Index + m.Length;
        return true;
    }
}

/// <summary>Maps a .NET <see cref="Match"/> onto the JS match shape, using the
/// JS-index → .NET-group key built at compile time.</summary>
public sealed class DotNetRegexMatch : IRegexMatch
{
    private readonly Match _match;
    private readonly string _input;
    private readonly string[] _groupKey;

    internal DotNetRegexMatch(Match match, string input, string[] groupKey)
    {
        _match = match;
        _input = input;
        _groupKey = groupKey;
    }

    public int Start => _match.Index;
    public int End => _match.Index + _match.Length;
    public string Input => _input;

    public string? Group(int i)
    {
        if (i == 0)
        {
            return _match.Value;
        }

        var g = ResolveGroup(i);
        return g is { Success: true } ? g.Value : null;
    }

    public (int Start, int End)? GroupSpan(int i)
    {
        if (i == 0)
        {
            return (_match.Index, _match.Index + _match.Length);
        }

        var g = ResolveGroup(i);
        return g is { Success: true } ? (g.Index, g.Index + g.Length) : null;
    }

    private Group? ResolveGroup(int jsIndex)
    {
        if (jsIndex < 0 || jsIndex >= _groupKey.Length)
        {
            return null;
        }
        // Group key is the .NET group name (named) or positional number (unnamed).
        // Match.Groups[string] resolves both names and numeric strings.
        return _match.Groups[_groupKey[jsIndex]];
    }
}
