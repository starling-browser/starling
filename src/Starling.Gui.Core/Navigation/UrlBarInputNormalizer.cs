using System.Text.RegularExpressions;

namespace Starling.Gui;

/// <summary>
/// Normalizes what the user types into the URL bar into a fully-qualified
/// URL ready for navigation. Mirrors the behavior of Chrome's omnibox and
/// Firefox's URIFixup: schemeless input like <c>google.com</c> becomes
/// <c>https://google.com</c>, <c>localhost</c> becomes <c>http://localhost</c>,
/// and a single word with no dot returns null. Starling has no search provider
/// yet, so the shell can surface an error instead of attempting a navigation.
/// </summary>
public static class UrlBarInputNormalizer
{
    // Bare IPv4 literal (loose; the URL parser will tighten).
    private static readonly Regex Ipv4Regex =
        new("^[0-9]{1,3}(\\.[0-9]{1,3}){3}$", RegexOptions.Compiled);

    /// <summary>
    /// Returns a fully-qualified URL for <paramref name="raw"/>, or null if
    /// the input doesn't look like a URL at all (empty, or a single word
    /// with no dot, port, or scheme). A search-backed browser would route it to
    /// a search provider.
    /// </summary>
    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var input = raw.Trim();

        if (ContainsWhitespace(input))
        {
            return null;
        }

        // Protocol-relative — promote to https.
        if (input.StartsWith("//"))
        {
            return "https:" + input;
        }

        // Path-only input has no host to navigate to.
        if (input.StartsWith('/'))
        {
            return null;
        }

        // Detect an explicit URL scheme. The tricky case is "localhost:8080"
        // or "example.com:8443" — both technically match the scheme grammar
        // (alpha *(alpha/digit/+/./-) ":"), but the user means host:port.
        // Disambiguate by peeking at what follows the colon: if it's all
        // digits (until end / path / query / fragment), it's a port number,
        // not a scheme opaque part. This matches Chrome's omnibox heuristic.
        var colon = input.IndexOf(':');
        if (colon > 0 && IsSchemeChars(input.AsSpan(0, colon)) &&
            !IsPortAfterColon(input, colon))
        {
            return input;
        }

        var hostCandidate = ExtractHostCandidate(input);
        if (hostCandidate.Length == 0)
        {
            return null;
        }

        var bareHost = StripPort(hostCandidate);

        // localhost and IPv4 literals default to http, matching Chrome and
        // Firefox: developers expect plain http for these.
        if (IsLocalhost(bareHost) || Ipv4Regex.IsMatch(bareHost))
        {
            return "http://" + input;
        }

        // Anything that looks like a hostname (has a dot) or carries an
        // explicit port defaults to https — the modern-browser default.
        if (bareHost.Contains('.') || HasExplicitPort(hostCandidate))
        {
            return "https://" + input;
        }

        // Single bare word: search query, not a URL.
        return null;
    }

    private static bool IsSchemeChars(System.ReadOnlySpan<char> s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        var first = s[0];
        if (!((first >= 'a' && first <= 'z') || (first >= 'A' && first <= 'Z')))
        {
            return false;
        }

        for (var i = 1; i < s.Length; i++)
        {
            var c = s[i];
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
                     (c >= '0' && c <= '9') || c == '+' || c == '-' || c == '.';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }

    private static bool IsPortAfterColon(string input, int colon)
    {
        // After the colon, walk until end / path / query / fragment. If
        // every character on the way is a digit (and there is at least
        // one), what we have is a port, not a scheme.
        var i = colon + 1;
        var sawDigit = false;
        for (; i < input.Length; i++)
        {
            var c = input[i];
            if (c is '/' or '?' or '#')
            {
                break;
            }

            if (c < '0' || c > '9')
            {
                return false;
            }

            sawDigit = true;
        }
        return sawDigit;
    }

    private static string ExtractHostCandidate(string input)
    {
        var end = input.Length;
        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c is '/' or '?' or '#') { end = i; break; }
        }
        return input[..end];
    }

    private static string StripPort(string hostCandidate)
    {
        var colon = hostCandidate.IndexOf(':');
        return colon < 0 ? hostCandidate : hostCandidate[..colon];
    }

    private static bool HasExplicitPort(string hostCandidate)
    {
        var colon = hostCandidate.IndexOf(':');
        if (colon < 0 || colon == hostCandidate.Length - 1)
        {
            return false;
        }

        for (var i = colon + 1; i < hostCandidate.Length; i++)
        {
            if (hostCandidate[i] < '0' || hostCandidate[i] > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLocalhost(string bareHost)
        => bareHost.Equals("localhost", System.StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWhitespace(string s)
    {
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                return true;
            }
        }

        return false;
    }
}
