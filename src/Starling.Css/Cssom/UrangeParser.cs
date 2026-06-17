namespace Starling.Css.Cssom;

/// <summary>
/// Parses and canonicalizes CSS <c>&lt;urange&gt;</c> values per CSS Syntax Level 3
/// §4.3.10. Returns null when the value does not conform to the grammar.
///
/// Canonical form (CSSOM §6.7.3):
///   - Single code point: <c>U+XXXX</c>
///   - Range:             <c>U+XXXX-YYYY</c>
/// where hex digits are uppercase and there are no leading zeros beyond what is
/// needed for the value itself.
/// </summary>
public static class UrangeParser
{
    // The maximum valid Unicode code point.
    private const int MaxCodePoint = 0x10FFFF;

    /// <summary>
    /// Attempt to parse <paramref name="raw"/> as a single <c>&lt;urange&gt;</c>
    /// value (possibly a comma-separated list). Returns the canonicalized string,
    /// or null if the input is not a valid <c>&lt;urange&gt;</c>.
    /// </summary>
    public static string? Canonicalize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        // Strip comments then trim outer whitespace.
        var stripped = StripComments(raw).Trim();
        if (stripped.Length == 0)
        {
            return null;
        }

        // Support comma-separated lists (e.g. "U+0-FF, U+200-2FF").
        var parts = stripped.Split(',');
        var results = new string[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var canon = ParseSingle(parts[i].Trim());
            if (canon is null)
            {
                return null;
            }

            results[i] = canon;
        }
        return string.Join(", ", results);
    }

    // -----------------------------------------------------------------
    // §4.3.10 grammar:
    //
    //   <urange> =
    //     u [WS] '+' [WS] <hex>+ '?'*    -- ident+delim+ident or ident+dim form
    //     u [WS] '+' [WS] <hex>* '?'+    -- wildcard-only form (u+?)
    //     u [WS] '+' [WS] <hex>+ '-' <hex>+ -- explicit range
    //
    // After stripping comments, we parse the remaining characters directly.
    //   A "hex segment" is 1–6 ASCII hex digits.
    //   A "wildcard segment" is '?' repeated 1–6 times.
    //   The start segment may mix hex + wildcards: hex* '?'+
    //   A range uses only hex on both sides (no wildcards).
    //
    // Validity constraints (§4.3.10 step 10):
    //   total length (hex + ?) ≤ 6
    //   no ? after hex in a range form
    //   start ≤ end ≤ 0x10FFFF
    // -----------------------------------------------------------------
    private static string? ParseSingle(string s)
    {
        var i = 0;

        // Expect 'u' or 'U'
        if (i >= s.Length || (s[i] != 'u' && s[i] != 'U'))
        {
            return null;
        }

        i++;
        // No whitespace allowed between 'u' and '+' for the token form
        // (the only whitespace allowed is inside comments which we stripped).
        // CSS Syntax §4.3.10 says whitespace CAN appear between tokens in
        // the tokenizer-based definition; after comment-stripping, those show up
        // as space chars.  BUT: "u + a" (with spaces) is NOT a valid <urange> —
        // the space form only applies to the comment-separated form "u/**/+/**/a".
        // After stripping comments, whitespace between u and + means invalid.
        // Exception: the spec's token-stream reading skips whitespace BETWEEN
        // tokens, so "u + a" with real spaces is invalid, but the comment form
        // "u/**/+/**/a" collapses to "u+a" after stripping.
        // Therefore after comment-stripping, no whitespace between u and + is valid.
        if (i < s.Length && s[i] == ' ')
        {
            return null;
        }

        // Expect '+'
        if (i >= s.Length || s[i] != '+')
        {
            return null;
        }

        i++;

        // No whitespace after '+' either.
        if (i < s.Length && s[i] == ' ')
        {
            return null;
        }

        // Collect the start segment: hex digits followed by optional '?'
        var hexStart = i;
        var hexCount = 0;
        while (i < s.Length && IsHex(s[i]) && hexCount < 7)
        {
            hexCount++;
            i++;
        }
        var wildcardCount = 0;
        while (i < s.Length && s[i] == '?' && wildcardCount < 7)
        {
            wildcardCount++;
            i++;
        }

        // After the start segment, nothing else (for the start-only or wildcard
        // form), or a '-' followed by the end segment.
        if (i < s.Length && s[i] == '-')
        {
            // Range form: start segment must be pure hex (no wildcards).
            if (wildcardCount > 0)
            {
                return null;
            }

            i++;

            // No whitespace after '-'.
            if (i < s.Length && s[i] == ' ')
            {
                return null;
            }

            var hexEnd = i;
            var hexCount2 = 0;
            while (i < s.Length && IsHex(s[i]) && hexCount2 < 7)
            {
                hexCount2++;
                i++;
            }

            // End segment must be pure hex, no wildcards.
            if (i < s.Length && s[i] == '?')
            {
                return null;
            }
            // Must reach end of string.
            if (i != s.Length)
            {
                return null;
            }

            return ValidateRange(s[hexStart..(hexStart + hexCount)],
                                  s[hexEnd..(hexEnd + hexCount2)]);
        }

        // Must reach end of string.
        if (i != s.Length)
        {
            return null;
        }

        // Pure hex or hex+wildcards form.
        return ValidateStartOnly(s[hexStart..(hexStart + hexCount)], wildcardCount);
    }

    /// <summary>
    /// Validate and canonicalize a start-only urange (with optional wildcards).
    /// </summary>
    private static string? ValidateStartOnly(string hexPart, int wildcardCount)
    {
        var total = hexPart.Length + wildcardCount;
        if (total == 0)
        {
            return null;        // nothing after '+'
        }

        if (total > 6)
        {
            return null;         // too many characters
        }

        if (hexPart.Length > 0 && wildcardCount > 0 && hexPart.Length + wildcardCount > 6)
        {
            return null;
        }

        if (wildcardCount == 0)
        {
            // Single code point.
            if (!TryParseHex(hexPart, out var cp))
            {
                return null;
            }

            if (cp > MaxCodePoint)
            {
                return null;
            }

            return "U+" + cp.ToString("X");
        }
        else
        {
            // Wildcard form: hex? e.g. "A?" → U+A0-AF, "?" → U+0-F.
            // Build start (replace ? with 0) and end (replace ? with F).
            var startHex = hexPart + new string('0', wildcardCount);
            var endHex = hexPart + new string('F', wildcardCount);
            if (!TryParseHex(startHex, out var start))
            {
                return null;
            }

            if (!TryParseHex(endHex, out var end))
            {
                return null;
            }

            if (end > MaxCodePoint)
            {
                return null;
            }

            return "U+" + start.ToString("X") + "-" + end.ToString("X");
        }
    }

    /// <summary>
    /// Validate and canonicalize an explicit range (start-end).
    /// </summary>
    private static string? ValidateRange(string startHex, string endHex)
    {
        if (startHex.Length == 0 || startHex.Length > 6)
        {
            return null;
        }

        if (endHex.Length == 0 || endHex.Length > 6)
        {
            return null;
        }

        if (!TryParseHex(startHex, out var start))
        {
            return null;
        }

        if (!TryParseHex(endHex, out var end))
        {
            return null;
        }

        if (end > MaxCodePoint)
        {
            return null;
        }

        if (start > end)
        {
            return null;
        }

        if (start == end)
        {
            return "U+" + start.ToString("X");
        }

        return "U+" + start.ToString("X") + "-" + end.ToString("X");
    }

    private static bool TryParseHex(string hex, out int value)
    {
        value = 0;
        if (hex.Length == 0 || hex.Length > 6)
        {
            return false;
        }

        foreach (var c in hex)
        {
            if (!IsHex(c))
            {
                return false;
            }

            value = (value << 4) | HexVal(c);
        }
        return true;
    }

    private static bool IsHex(char c)
        => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static int HexVal(char c)
        => c >= '0' && c <= '9' ? c - '0'
         : c >= 'a' && c <= 'f' ? c - 'a' + 10
         : c - 'A' + 10;

    /// <summary>
    /// Strip CSS block comments (<c>/* … */</c>) from the input. This is
    /// required because the CSS Syntax §4.3.10 urange algorithm is defined
    /// over the <em>token</em> stream (not the raw string), and comments are
    /// consumed between tokens. After stripping, the result may contain
    /// whitespace that was adjacent to comments, which is then handled by the
    /// surrounding parse logic.
    /// </summary>
    private static string StripComments(string s)
    {
        if (!s.Contains("/*"))
        {
            return s;
        }

        var sb = new System.Text.StringBuilder(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (i + 1 < s.Length && s[i] == '/' && s[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < s.Length && !(s[i] == '*' && s[i + 1] == '/'))
                {
                    i++;
                }

                i += 2; // skip */
            }
            else
            {
                sb.Append(s[i]);
                i++;
            }
        }
        return sb.ToString();
    }
}
