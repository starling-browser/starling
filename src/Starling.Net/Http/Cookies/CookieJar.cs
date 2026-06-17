using System.Text;
using StarlingUrl = global::Starling.Url.Url;

namespace Starling.Net.Http.Cookies;

/// <summary>
/// In-memory cookie store. Implements the storage and retrieval algorithms
/// from RFC 6265bis §5.4-5.6 — domain matching, path matching, public-suffix
/// blocking, prefix rules (<c>__Host-</c> / <c>__Secure-</c>), Max-Age /
/// Expires evaluation.
/// </summary>
/// <remarks>
/// SameSite filtering is partial in v1: <c>Strict</c>/<c>Lax</c> cookies are
/// included on every request the engine makes (the headless renderer only
/// issues top-level navigations, which are same-site by definition).
/// Cross-origin SameSite filtering is not implemented yet.
/// </remarks>
public sealed class CookieJar
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Cookie>> _byDomain =
        new(StringComparer.Ordinal);
    private readonly PublicSuffixList _psl;
    private readonly Func<DateTimeOffset> _now;

    public CookieJar() : this(PublicSuffixList.Default, () => DateTimeOffset.UtcNow) { }

    public CookieJar(PublicSuffixList publicSuffixList, Func<DateTimeOffset> now)
    {
        _psl = publicSuffixList ?? throw new ArgumentNullException(nameof(publicSuffixList));
        _now = now ?? throw new ArgumentNullException(nameof(now));
    }

    /// <summary>Total live (non-expired) cookies across all domains.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                var n = 0;
                var now = _now();
                foreach (var list in _byDomain.Values)
                {
                    foreach (var c in list)
                    {
                        if (!c.IsExpired(now))
                        {
                            n++;
                        }
                    }
                }
                return n;
            }
        }
    }

    /// <summary>Drop every stored cookie.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _byDomain.Clear();
        }
    }

    /// <summary>
    /// Process the <c>Set-Cookie</c> header values from a single response.
    /// Each header is one cookie. Malformed and disallowed cookies are
    /// silently dropped per RFC 6265bis §5.5 — we surface a count of
    /// accepted cookies for telemetry only.
    /// </summary>
    public int StoreFromHeaders(StarlingUrl requestUrl, IReadOnlyList<string> setCookieHeaders)
    {
        ArgumentNullException.ThrowIfNull(requestUrl);
        ArgumentNullException.ThrowIfNull(setCookieHeaders);
        if (setCookieHeaders.Count == 0)
        {
            return 0;
        }

        var host = requestUrl.Host;
        if (string.IsNullOrEmpty(host))
        {
            return 0;
        }

        var canonicalHost = CanonicalHost(host);
        var requestPath = DefaultPath(requestUrl);
        var isSecureScheme = requestUrl.IsHttps;

        var accepted = 0;
        var now = _now();

        lock (_gate)
        {
            foreach (var header in setCookieHeaders)
            {
                var parsed = CookieParser.Parse(header);
                if (parsed is null)
                {
                    continue;
                }

                var cookie = AdoptCookie(parsed, canonicalHost, requestPath, isSecureScheme, now);
                if (cookie is null)
                {
                    continue;
                }

                StoreCookie(cookie, now);
                accepted++;
            }
        }
        return accepted;
    }

    /// <summary>
    /// Build the <c>Cookie</c> header value for an outgoing request.
    /// Returns an empty string when no cookies apply.
    /// </summary>
    public string BuildCookieHeader(StarlingUrl requestUrl)
    {
        ArgumentNullException.ThrowIfNull(requestUrl);
        var host = requestUrl.Host;
        if (string.IsNullOrEmpty(host))
        {
            return string.Empty;
        }

        var canonicalHost = CanonicalHost(host);
        var requestPath = string.IsNullOrEmpty(requestUrl.Path) ? "/" : requestUrl.Path;
        var isSecureScheme = requestUrl.IsHttps;
        var now = _now();

        List<Cookie> matching = [];

        lock (_gate)
        {
            foreach (var (_, list) in _byDomain)
            {
                for (var i = list.Count - 1; i >= 0; i--)
                {
                    var c = list[i];
                    if (c.IsExpired(now))
                    {
                        list.RemoveAt(i);
                        continue;
                    }
                    if (!DomainMatches(c, canonicalHost))
                    {
                        continue;
                    }

                    if (!PathMatches(c.Path, requestPath))
                    {
                        continue;
                    }

                    if (c.Secure && !isSecureScheme)
                    {
                        continue;
                    }

                    matching.Add(c);
                }
            }

            // §5.6.4 sort: longer Path first, earliest creation first.
            matching.Sort(static (a, b) =>
            {
                var byPath = b.Path.Length.CompareTo(a.Path.Length);
                if (byPath != 0)
                {
                    return byPath;
                }

                return a.CreationUtc.CompareTo(b.CreationUtc);
            });

            // Update last-access while we still hold the lock.
            foreach (var c in matching)
            {
                c.LastAccessUtc = now;
            }
        }

        if (matching.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(matching.Count * 32);
        for (var i = 0; i < matching.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("; ");
            }

            sb.Append(matching[i].Name).Append('=').Append(matching[i].Value);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Apply the storage-side rules from §5.5 (domain matching against the
    /// request host, public-suffix rejection, prefix rules, SameSite=None
    /// requires Secure) and return the cookie to insert, or null to reject.
    /// </summary>
    private Cookie? AdoptCookie(
        ParsedSetCookie parsed,
        string requestHost,
        string requestPath,
        bool isSecureScheme,
        DateTimeOffset now)
    {
        // §5.5 step 7: compute domain.
        string domain;
        bool hostOnly;
        if (parsed.Domain is { Length: > 0 } d)
        {
            // §5.5 step 7.4: reject cookies whose Domain is a public suffix.
            if (_psl.IsPublicSuffix(d) && !string.Equals(d, requestHost, StringComparison.Ordinal))
            {
                return null;
            }

            // §5.5 step 7.5: domain must be the request host or a parent of it.
            if (!IsHostOrParent(d, requestHost))
            {
                return null;
            }

            domain = d;
            hostOnly = false;
        }
        else
        {
            domain = requestHost;
            hostOnly = true;
        }

        // §5.5 step 8: determine path.
        var path = parsed.Path ?? requestPath;

        // §5.5 step 12: SameSite=None requires Secure.
        if (parsed.SameSite == SameSiteMode.None && !parsed.Secure)
        {
            return null;
        }

        // §5.5 step 13: Secure-only attribute on insecure scheme.
        if (parsed.Secure && !isSecureScheme)
        {
            return null;
        }

        // §4.1.3 prefix rules.
        if (parsed.Name.StartsWith("__Secure-", StringComparison.Ordinal)
            && (!parsed.Secure || !isSecureScheme))
        {
            return null;
        }
        if (parsed.Name.StartsWith("__Host-", StringComparison.Ordinal))
        {
            if (!parsed.Secure)
            {
                return null;
            }

            if (!isSecureScheme)
            {
                return null;
            }

            if (!hostOnly)
            {
                return null;
            }

            if (path != "/")
            {
                return null;
            }
        }

        // Compute expiry — Max-Age beats Expires per §5.5 step 4.
        DateTimeOffset? expires = null;
        var persistent = false;
        if (parsed.MaxAge is long ma)
        {
            persistent = true;
            expires = ma <= 0 ? now.AddSeconds(-1) : now.AddSeconds(ma);
        }
        else if (parsed.Expires is { } ex)
        {
            persistent = true;
            expires = ex;
        }

        return new Cookie
        {
            Name = parsed.Name,
            Value = parsed.Value,
            Domain = domain,
            Path = path,
            CreationUtc = now,
            LastAccessUtc = now,
            ExpiresUtc = expires,
            Persistent = persistent,
            HostOnly = hostOnly,
            Secure = parsed.Secure,
            HttpOnly = parsed.HttpOnly,
            SameSite = parsed.SameSite,
        };
    }

    private void StoreCookie(Cookie cookie, DateTimeOffset now)
    {
        if (!_byDomain.TryGetValue(cookie.Domain, out var list))
        {
            _byDomain[cookie.Domain] = list = new List<Cookie>(4);
        }

        // Replace any existing cookie matching (Name, Path) — §5.5 step 11.
        for (var i = 0; i < list.Count; i++)
        {
            var existing = list[i];
            if (string.Equals(existing.Name, cookie.Name, StringComparison.Ordinal)
                && string.Equals(existing.Path, cookie.Path, StringComparison.Ordinal))
            {
                // Per §5.5 step 11.3, preserve original creation time.
                list[i] = new Cookie
                {
                    Name = cookie.Name,
                    Value = cookie.Value,
                    Domain = cookie.Domain,
                    Path = cookie.Path,
                    CreationUtc = existing.CreationUtc,
                    LastAccessUtc = now,
                    ExpiresUtc = cookie.ExpiresUtc,
                    Persistent = cookie.Persistent,
                    HostOnly = cookie.HostOnly,
                    Secure = cookie.Secure,
                    HttpOnly = cookie.HttpOnly,
                    SameSite = cookie.SameSite,
                };
                return;
            }
        }

        // Don't store an already-expired cookie (deletes a non-existent one).
        if (cookie.IsExpired(now))
        {
            return;
        }

        list.Add(cookie);
    }

    /// <summary>§5.1.4 default-path: the request URL's path with the last slash truncation.</summary>
    private static string DefaultPath(StarlingUrl url)
    {
        var p = string.IsNullOrEmpty(url.Path) ? "/" : url.Path;
        if (p[0] != '/')
        {
            return "/";
        }

        if (p == "/")
        {
            return "/";
        }

        var lastSlash = p.LastIndexOf('/');
        if (lastSlash <= 0)
        {
            return "/";
        }

        return p[..lastSlash];
    }

    /// <summary>§5.1.3 domain matching.</summary>
    private static bool DomainMatches(Cookie cookie, string requestHost)
    {
        if (cookie.HostOnly)
        {
            return string.Equals(cookie.Domain, requestHost, StringComparison.Ordinal);
        }

        if (string.Equals(cookie.Domain, requestHost, StringComparison.Ordinal))
        {
            return true;
        }

        if (requestHost.EndsWith(cookie.Domain, StringComparison.Ordinal)
            && requestHost[requestHost.Length - cookie.Domain.Length - 1] == '.')
        {
            // Reject IP-literal hosts from being suffix-matched.
            return !IsIpLiteral(requestHost);
        }
        return false;
    }

    /// <summary>§5.1.4 path matching.</summary>
    private static bool PathMatches(string cookiePath, string requestPath)
    {
        if (string.Equals(cookiePath, requestPath, StringComparison.Ordinal))
        {
            return true;
        }

        if (!requestPath.StartsWith(cookiePath, StringComparison.Ordinal))
        {
            return false;
        }

        return cookiePath[^1] == '/'
            || (requestPath.Length > cookiePath.Length && requestPath[cookiePath.Length] == '/');
    }

    private static bool IsHostOrParent(string parent, string host)
    {
        if (string.Equals(parent, host, StringComparison.Ordinal))
        {
            return true;
        }

        if (host.Length <= parent.Length)
        {
            return false;
        }

        return host.EndsWith(parent, StringComparison.Ordinal)
            && host[host.Length - parent.Length - 1] == '.';
    }

    private static string CanonicalHost(string host)
    {
        var s = host.Trim().TrimEnd('.').ToLowerInvariant();
        return s;
    }

    private static bool IsIpLiteral(string host)
    {
        if (host.Length == 0)
        {
            return false;
        }

        if (host[0] == '[')
        {
            return true; // IPv6 literal in brackets
        }
        // Quick IPv4 check — all-numeric labels separated by dots.
        var labels = host.Split('.');
        foreach (var label in labels)
        {
            if (label.Length == 0)
            {
                return false;
            }

            foreach (var ch in label)
            {
                if (!char.IsAsciiDigit(ch))
                {
                    return false;
                }
            }
        }
        return labels.Length == 4;
    }

    // Reserved for diagnostic dumps; not part of v1 surface.
    internal IReadOnlyList<string> EnumerateDomains()
    {
        lock (_gate)
        {
            return _byDomain.Keys.ToArray();
        }
    }
}
