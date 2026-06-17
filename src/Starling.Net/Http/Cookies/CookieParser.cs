using System.Globalization;

namespace Starling.Net.Http.Cookies;

/// <summary>
/// Parser for a single <c>Set-Cookie</c> header value per RFC 6265bis §5.2.
/// Returns a name/value pair plus a bag of attributes; storage rules
/// (domain matching, public-suffix rejection, prefix enforcement) are
/// applied higher up by <see cref="CookieJar"/>.
/// </summary>
internal static class CookieParser
{
    /// <summary>
    /// Returns a parsed cookie or null if the header is malformed (missing
    /// '=', empty name).
    /// </summary>
    public static ParsedSetCookie? Parse(string header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        // §5.2 step 1-3: split off the first ';' to separate the name=value pair
        // from the attribute list. The name=value itself uses the *first* '='.
        var firstSemi = header.IndexOf(';', StringComparison.Ordinal);
        var pair = (firstSemi < 0 ? header : header[..firstSemi]).Trim();
        var attrSection = firstSemi < 0 ? string.Empty : header[(firstSemi + 1)..];

        var eq = pair.IndexOf('=', StringComparison.Ordinal);
        if (eq < 0)
        {
            return null;
        }

        var name = pair[..eq].Trim();
        var value = pair[(eq + 1)..].Trim();
        if (name.Length == 0)
        {
            return null;
        }
        // Strip a single pair of surrounding double-quotes from the value
        // — common in older Set-Cookie outputs.
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
        }

        var result = new ParsedSetCookie { Name = name, Value = value };

        foreach (var rawAttr in attrSection.Split(';'))
        {
            var attr = rawAttr.Trim();
            if (attr.Length == 0)
            {
                continue;
            }

            string attrName, attrValue;
            var aeq = attr.IndexOf('=', StringComparison.Ordinal);
            if (aeq < 0) { attrName = attr; attrValue = string.Empty; }
            else
            {
                attrName = attr[..aeq].Trim();
                attrValue = attr[(aeq + 1)..].Trim();
            }

            ApplyAttribute(result, attrName, attrValue);
        }

        return result;
    }

    private static void ApplyAttribute(ParsedSetCookie c, string name, string value)
    {
        if (string.Equals(name, "Expires", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseHttpDate(value, out var date))
            {
                c.Expires = date;
            }
        }
        else if (string.Equals(name, "Max-Age", StringComparison.OrdinalIgnoreCase))
        {
            // §5.2.2 — leading '-' = expire immediately. 0 = expire immediately.
            // Non-numeric → ignore. Max-Age beats Expires.
            if (long.TryParse(value, NumberStyles.Integer | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out var seconds))
            {
                c.MaxAge = seconds;
            }
        }
        else if (string.Equals(name, "Domain", StringComparison.OrdinalIgnoreCase))
        {
            var d = value.TrimStart('.').ToLowerInvariant();
            if (d.Length > 0)
            {
                c.Domain = d;
            }
        }
        else if (string.Equals(name, "Path", StringComparison.OrdinalIgnoreCase))
        {
            c.Path = value.Length > 0 && value[0] == '/' ? value : null;
        }
        else if (string.Equals(name, "Secure", StringComparison.OrdinalIgnoreCase))
        {
            c.Secure = true;
        }
        else if (string.Equals(name, "HttpOnly", StringComparison.OrdinalIgnoreCase))
        {
            c.HttpOnly = true;
        }
        else if (string.Equals(name, "SameSite", StringComparison.OrdinalIgnoreCase))
        {
            c.SameSite = value.ToLowerInvariant() switch
            {
                "strict" => SameSiteMode.Strict,
                "lax" => SameSiteMode.Lax,
                "none" => SameSiteMode.None,
                _ => SameSiteMode.Lax,
            };
        }
        // Unknown attributes are ignored per §5.2.
    }

    /// <summary>
    /// Parse the date formats RFC 6265bis §5.1.1 admits. The full algorithm
    /// is forgiving — we accept the common HTTP date formats only and fall
    /// back to <see cref="DateTimeOffset.TryParse(string, out DateTimeOffset)"/>
    /// for variants like asctime.
    /// </summary>
    internal static bool TryParseHttpDate(string raw, out DateTimeOffset date)
    {
        var formats = new[]
        {
            "ddd, dd MMM yyyy HH:mm:ss 'GMT'",     // RFC 1123
            "dddd, dd-MMM-yy HH:mm:ss 'GMT'",       // RFC 850
            "ddd MMM  d HH:mm:ss yyyy",             // asctime
            "ddd MMM d HH:mm:ss yyyy",
            "ddd, dd MMM yyyy HH:mm:ss zzz",
        };
        if (DateTimeOffset.TryParseExact(raw, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date))
        {
            return true;
        }
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out date);
    }
}

internal sealed class ParsedSetCookie
{
    public required string Name { get; init; }
    public required string Value { get; init; }

    public DateTimeOffset? Expires { get; set; }
    public long? MaxAge { get; set; }
    public string? Domain { get; set; }
    public string? Path { get; set; }
    public bool Secure { get; set; }
    public bool HttpOnly { get; set; }
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Lax;
}
