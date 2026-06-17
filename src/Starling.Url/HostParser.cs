using System.Text;

namespace Starling.Url;

/// <summary>
/// Host parser per WHATWG URL §4.5
/// (<see href="https://url.spec.whatwg.org/#host-parsing"/>).
/// </summary>
/// <remarks>
/// Implements the basic decision tree (opaque host vs domain vs IPv4 numeric)
/// without IDNA Punycode and without IPv6 bracket parsing. Hostnames containing
/// non-ASCII pass through as-is, which matches what an IDNA-to-ASCII
/// implementation would do for already-ASCII inputs.
/// </remarks>
internal static class HostParser
{
    public enum Error
    {
        Empty,
        IPv4NumberInvalid,
        IPv4TooBig,
        IPv6NotSupported,
        InvalidCharacter,
    }

    public readonly record struct Result(string? Host, Error? Err)
    {
        public bool IsOk => Err is null;
        public static Result Ok(string host) => new(host, null);
        public static Result Fail(Error e) => new(null, e);
    }

    /// <summary>
    /// Parse a hostname for the given scheme. <paramref name="isSpecial"/>
    /// affects what's considered a forbidden character.
    /// </summary>
    public static Result Parse(string input, bool isSpecial)
    {
        if (input.Length == 0)
        {
            return Result.Fail(Error.Empty);
        }

        // IPv6 literals are not implemented yet.
        if (input[0] == '[')
        {
            // Return an explicit error so the caller can decide.
            return Result.Fail(Error.IPv6NotSupported);
        }

        if (!isSpecial)
        {
            // Opaque host — percent-encode anything in the C0 control set
            // (plus space, tab, etc.) but otherwise verbatim.
            return Result.Ok(OpaqueHostString(input));
        }

        // Special host:
        //   - percent-decode
        //   - lowercase
        //   - validate no forbidden chars
        //   - try IPv4-numeric form
        var decoded = Percent.Decode(input).ToLowerInvariant();
        foreach (var ch in decoded)
        {
            if (IsForbiddenDomainCodePoint(ch))
            {
                return Result.Fail(Error.InvalidCharacter);
            }
        }

        // Attempt IPv4 numeric parse — spec is in §4.6 IPv4 parser. If the
        // input matches the IPv4 grammar, the result is canonicalized
        // (e.g. "0x7F.1" → "127.0.0.1"). For inputs that don't match, the
        // domain string is returned as-is.
        if (TryParseIPv4(decoded, out var ipv4))
        {
            return Result.Ok(ipv4);
        }

        return Result.Ok(decoded);
    }

    private static string OpaqueHostString(string input)
    {
        var sb = new StringBuilder();
        foreach (var ch in input)
        {
            Percent.AppendEncoded(sb, ch, Percent.Set.C0Control);
        }
        return sb.ToString();
    }

    private static bool IsForbiddenDomainCodePoint(char c)
    {
        // §4.5: forbidden host code points = U+0000, tab, LF, CR, space,
        //   #, /, :, <, >, ?, @, [, \, ], ^, |
        // For domain hosts, also: %.
        return c is '\0' or '\t' or '\n' or '\r' or ' '
            or '#' or '/' or ':' or '<' or '>' or '?' or '@'
            or '[' or '\\' or ']' or '^' or '|' or '%';
    }

    // -----------------------------------------------------------------------
    // §4.6 IPv4 parser — simplified. Spec accepts up to 4 dot-separated
    // "numbers" each of which may be decimal/octal/hex. Returns the
    // dotted-quad string on success.
    // -----------------------------------------------------------------------
    private static bool TryParseIPv4(string input, out string canonical)
    {
        canonical = "";
        var parts = input.TrimEnd('.').Split('.');
        if (parts.Length is < 1 or > 4)
        {
            return false;
        }
        // Every part must be a valid number per the IPv4 number grammar.
        // First quick check: at least the last part is purely digits.
        var numbers = new long[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!TryParseIPv4Number(parts[i], out numbers[i]))
            {
                return false;
            }
        }
        // Spec: if any part > 255 (except the last, which may be > 255 only
        // when there are fewer than 4 parts), it's an error.
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (numbers[i] > 255)
            {
                return false;
            }
        }
        var maxLast = parts.Length switch
        {
            1 => 0xFFFFFFFFL,
            2 => 0xFFFFFFL,
            3 => 0xFFFFL,
            _ => 255L,
        };
        if (numbers[^1] > maxLast)
        {
            return false;
        }

        long addr;
        switch (parts.Length)
        {
            case 1: addr = numbers[0]; break;
            case 2: addr = (numbers[0] << 24) | numbers[1]; break;
            case 3: addr = (numbers[0] << 24) | (numbers[1] << 16) | numbers[2]; break;
            default: addr = (numbers[0] << 24) | (numbers[1] << 16) | (numbers[2] << 8) | numbers[3]; break;
        }
        canonical =
            $"{(addr >> 24) & 0xFF}.{(addr >> 16) & 0xFF}.{(addr >> 8) & 0xFF}.{addr & 0xFF}";
        return true;
    }

    private static bool TryParseIPv4Number(string s, out long value)
    {
        value = 0;
        if (s.Length == 0)
        {
            return false;
        }

        int radix = 10;
        if (s.Length >= 2 && (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)))
        {
            radix = 16;
            s = s[2..];
        }
        else if (s.Length >= 2 && s[0] == '0')
        {
            radix = 8;
            s = s[1..];
        }
        if (s.Length == 0)
        {
            return true; // "0" or "0x" → 0
        }

        try
        {
            value = Convert.ToInt64(s, radix);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
