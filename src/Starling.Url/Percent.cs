using System.Text;

namespace Starling.Url;

/// <summary>
/// Percent-encoding / -decoding helpers per WHATWG URL §1.3
/// (<see href="https://url.spec.whatwg.org/#percent-encoded-bytes"/>).
/// </summary>
/// <remarks>
/// The spec defines several "percent-encode sets" — distinct categories of
/// characters that must be percent-encoded in different URL components.
/// Sets are listed at §1.3:
/// <list type="bullet">
///   <item><b>C0 controls</b>: U+0000–U+001F + U+007E and above.</item>
///   <item><b>Fragment</b>: C0 controls + space, <c>"</c>, <c>&lt;</c>, <c>&gt;</c>, <c>`</c>.</item>
///   <item><b>Query</b>: C0 controls + space, <c>"</c>, <c>#</c>, <c>&lt;</c>, <c>&gt;</c>.</item>
///   <item><b>Special-query</b>: Query + <c>'</c>.</item>
///   <item><b>Path</b>: Query + <c>?</c>, <c>`</c>, <c>{</c>, <c>}</c>.</item>
///   <item><b>Userinfo</b>: Path + <c>/</c>, <c>:</c>, <c>;</c>, <c>=</c>, <c>@</c>, <c>[</c>–<c>^</c>, <c>|</c>.</item>
///   <item><b>Component</b>: Userinfo + <c>$</c>, <c>%</c>, <c>&amp;</c>, <c>+</c>, <c>,</c>.</item>
/// </list>
/// </remarks>
internal static class Percent
{
    public enum Set
    {
        C0Control,
        Fragment,
        Query,
        SpecialQuery,
        Path,
        Userinfo,
    }

    public static bool IsInSet(int c, Set set)
    {
        if (c <= 0x1F || c >= 0x7F)
        {
            return true; // C0 controls + > tilde
        }

        return set switch
        {
            Set.C0Control => false, // covered by the leading check
            Set.Fragment => c is ' ' or '"' or '<' or '>' or '`',
            Set.Query => c is ' ' or '"' or '#' or '<' or '>',
            Set.SpecialQuery => c is ' ' or '"' or '#' or '<' or '>' or '\'',
            Set.Path => c is ' ' or '"' or '#' or '<' or '>' or '?' or '`' or '{' or '}',
            Set.Userinfo => c is ' ' or '"' or '#' or '<' or '>' or '?' or '`'
                or '{' or '}' or '/' or ':' or ';' or '=' or '@'
                or '[' or '\\' or ']' or '^' or '|',
            _ => false,
        };
    }

    /// <summary>
    /// Append percent-encoded <paramref name="cp"/> to the buffer if it's in
    /// <paramref name="set"/>; otherwise append it verbatim. UTF-8 encoded
    /// for code points &gt; U+007F.
    /// </summary>
    public static void AppendEncoded(StringBuilder sb, int cp, Set set)
    {
        if (cp < 0x80 && !IsInSet(cp, set))
        {
            sb.Append((char)cp);
            return;
        }

        // UTF-8 encode the code point, then percent-encode each byte.
        Span<byte> buf = stackalloc byte[4];
        var rune = new System.Text.Rune(cp);
        var n = rune.EncodeToUtf8(buf);
        for (var i = 0; i < n; i++)
        {
            sb.Append('%');
            sb.Append(HexUpper(buf[i] >> 4));
            sb.Append(HexUpper(buf[i] & 0xF));
        }
    }

    /// <summary>
    /// Percent-decode <paramref name="s"/> assuming a UTF-8 underlying byte
    /// stream. Malformed escapes pass through as-is per spec leniency.
    /// </summary>
    public static string Decode(string s)
    {
        if (s.IndexOf('%') < 0)
        {
            return s;
        }

        var bytes = new List<byte>(s.Length);
        Span<byte> rbuf = stackalloc byte[4];
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '%' && i + 2 < s.Length
                && IsHex(s[i + 1]) && IsHex(s[i + 2]))
            {
                bytes.Add((byte)((HexVal(s[i + 1]) << 4) | HexVal(s[i + 2])));
                i += 2;
            }
            else if (s[i] < 0x80)
            {
                bytes.Add((byte)s[i]);
            }
            else
            {
                // Non-ASCII chars get UTF-8 encoded into the byte buffer
                // (the per-iteration stackalloc above is hoisted out of the loop).
                var rune = new System.Text.Rune(s[i]);
                var n = rune.EncodeToUtf8(rbuf);
                for (var b = 0; b < n; b++)
                {
                    bytes.Add(rbuf[b]);
                }
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static bool IsHex(char c)
        => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'F') || (c >= 'a' && c <= 'f');

    private static int HexVal(char c)
        => c switch
        {
            >= '0' and <= '9' => c - '0',
            >= 'A' and <= 'F' => c - 'A' + 10,
            >= 'a' and <= 'f' => c - 'a' + 10,
            _ => 0,
        };

    private static char HexUpper(int v) => (char)(v < 10 ? '0' + v : 'A' + v - 10);
}
