using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using Starling.Common.Diagnostics;
using Starling.Common.Encoding;
using Starling.Css;
using Starling.Css.Parser;
using Starling.Dom;
using Starling.Net;
using Starling.Url;
using StarlingUrl = global::Starling.Url.Url;

namespace Starling.Engine;

/// <summary>
/// Fetches every <c>&lt;link rel="stylesheet" href="..."&gt;</c> referenced by
/// a <see cref="Document"/>, parses each response as CSS, and exposes the
/// result keyed by <c>&lt;link&gt;</c> element so the painter can drop the
/// parsed sheet into the cascade at the correct document-order position.
/// </summary>
/// <remarks>
/// Fail-soft like <see cref="ImageFetcher"/>: a network/parse failure leaves
/// the <c>&lt;link&gt;</c> with no associated sheet — the cascade proceeds
/// without it. A by-URL cache de-duplicates fetches: a repeated href resolves
/// to the already-parsed sheet rather than re-downloading. (Two <em>identical</em>
/// hrefs discovered in the same pass both miss the cache probe and each fetch,
/// since the parallel kick-off starts before either completes — matching
/// <see cref="ScriptFetcher"/>; the cache still prevents a re-fetch on the
/// engine's later post-script pass.)
///
/// Cross-origin / CSP are not enforced yet (M5+ work). The HTTP charset, BOM,
/// and <c>@charset</c> rules are all best-effort: we honour the
/// <c>Content-Type</c> charset, then a UTF-16 BOM, then the leading
/// <c>@charset</c> rule, else UTF-8 per [CSS Syntax 3 §3.2].
/// </remarks>
internal sealed class StylesheetFetcher : IDisposable
{
    private readonly Dictionary<Element, StyleSheet> _byElement = [];
    // Concurrent because FetchAllAsync starts multiple external fetches in
    // parallel; their cache writes (on the network continuation) can race.
    private readonly ConcurrentDictionary<string, (StyleSheet Sheet, StarlingUrl Url)> _byUrl = new(StringComparer.Ordinal);
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private readonly Func<StarlingHttpClient> _httpFactory;
    private StarlingHttpClient? _sharedHttp;
    private readonly bool _ownsHttp;

    public StylesheetFetcher(ILoggerFactory loggerFactory, Func<StarlingHttpClient> httpFactory)
    {
        _loggerFactory = loggerFactory;
        _log = _loggerFactory.CreateLogger<StylesheetFetcher>();
        _httpFactory = httpFactory;
        _ownsHttp = true;
    }

    /// <summary>
    /// Use a caller-owned <see cref="StarlingHttpClient"/> so resource fetches
    /// share one connection pool — same-origin requests reuse the keep-alive
    /// transport instead of paying a fresh DNS+TCP+TLS handshake each time. The
    /// shared client is owned by the caller and is not disposed by this fetcher.
    /// </summary>
    public StylesheetFetcher(ILoggerFactory loggerFactory, StarlingHttpClient sharedHttp)
    {
        _loggerFactory = loggerFactory;
        _log = _loggerFactory.CreateLogger<StylesheetFetcher>();
        _sharedHttp = sharedHttp;
        _httpFactory = () => sharedHttp;
        _ownsHttp = false;
    }

    /// <summary>
    /// Number of stylesheets fetched + parsed so far. The engine snapshots this
    /// before and after the post-script resource fetch: a newly loaded sheet
    /// changes the cascade (and therefore layout), so a growth here forces a
    /// full re-layout instead of reusing the pre-script box tree.
    /// </summary>
    public int LoadedCount => _byUrl.Count;

    public StyleSheet? Resolve(Element element)
        => _byElement.TryGetValue(element, out var sheet) ? sheet : null;

    /// <summary>
    /// Enumerates every loaded sheet paired with the absolute URL it was
    /// fetched from. Subresource fetchers (currently <see cref="FontFaceFetcher"/>)
    /// use this so relative <c>url()</c> values resolve against the sheet's
    /// origin, not the document's.
    /// </summary>
    public IEnumerable<(StyleSheet Sheet, StarlingUrl BaseUrl)> EnumerateLoaded()
    {
        foreach (var entry in _byUrl.Values)
        {
            yield return (entry.Sheet, entry.Url);
        }
    }

    public async Task FetchAllAsync(Document document, StarlingUrl? baseUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Two-pass fetch so external stylesheets download in parallel while
        // still being applied in document order. The cascade orders sheets by
        // the position of their <link> element (CSS Cascade "Order of
        // Appearance"), not by which response arrives first — so fetch order is
        // free to overlap. Pass 1 walks in document order and kicks off each
        // fetch without awaiting (the synchronous prefix — attribute reads, URL
        // resolve, cache probe — still runs sequentially here; only the network
        // I/O overlaps). Pass 2 awaits them all, then records each sheet against
        // its element in document order.
        var pending = new List<(Element Link, Task<StyleSheet?> Task)>();
        foreach (var link in document.GetElementsByTagName("link"))
        {
            ct.ThrowIfCancellationRequested();

            if (!IsStylesheetLink(link))
            {
                continue;
            }

            var href = link.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var absolute = ResolveAbsolute(href, baseUrl);
            if (absolute is null)
            {
                StylesheetFetcherLog.CannotResolveStylesheetHref(_log, href);
                continue;
            }

            pending.Add((link, FetchAndParseAsync(absolute, ct)));
        }

        if (pending.Count == 0)
        {
            return;
        }

        // Await all fetches before recording. Task.WhenAll observes every task,
        // so a cancellation can't leave an unobserved faulted task behind; it
        // also surfaces cancellation the same way the old sequential loop did.
        await Task.WhenAll(pending.Select(static p => p.Task)).ConfigureAwait(false);

        foreach (var (link, task) in pending)
        {
            var sheet = task.Result; // completed; null = fetch/parse failed
            if (sheet is null)
            {
                continue;
            }

            _byElement[link] = sheet;
        }
    }

    private static bool IsStylesheetLink(Element link)
    {
        var rel = link.GetAttribute("rel");
        if (string.IsNullOrWhiteSpace(rel))
        {
            return false;
        }

        // rel is a space-separated set of tokens per HTML spec; "stylesheet"
        // anywhere in the set counts.
        foreach (var token in rel.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (token.Equals("stylesheet", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private async Task<StyleSheet?> FetchAndParseAsync(StarlingUrl url, CancellationToken ct)
    {
        var key = url.ToString();
        if (_byUrl.TryGetValue(key, out var cached))
        {
            return cached.Sheet;
        }

        using var _ = StarlingTelemetry.Span("engine", "fetch_stylesheet");
        Activity.Current?.SetTag("url", key);

        try
        {
            byte[] bytes;
            string? contentType = null;
            if (url.IsFile)
            {
                var path = url.ToFileSystemPath();
                if (!File.Exists(path))
                {
                    StylesheetFetcherLog.MissingLocalStylesheet(_log, path);
                    StarlingTelemetry.Counter("engine.fetch.stylesheet.failed", 1);
                    return null;
                }
                bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            else if (url.IsHttp || url.IsHttps)
            {
                _sharedHttp ??= _httpFactory();
                var response = await _sharedHttp.GetAsync(url, ct).ConfigureAwait(false);
                if (response.IsErr)
                {
                    StylesheetFetcherLog.StylesheetFetchFailed(_log, url.ToString(), response.Error.ToString());
                    StarlingTelemetry.Counter("engine.fetch.stylesheet.failed", 1);
                    return null;
                }
                Activity.Current?.SetTag("http.status_code", response.Value.StatusCode);
                if (response.Value.StatusCode is < 200 or >= 400)
                {
                    StylesheetFetcherLog.StylesheetFetchHttpError(_log, response.Value.StatusCode, url.ToString());
                    StarlingTelemetry.Counter("engine.fetch.stylesheet.failed", 1);
                    return null;
                }
                bytes = response.Value.Body.ToArray();
                contentType = response.Value.Headers.GetFirst("Content-Type");
            }
            else
            {
                StylesheetFetcherLog.UnsupportedStylesheetScheme(_log, url.Scheme, url.ToString());
                StarlingTelemetry.Counter("engine.fetch.stylesheet.failed", 1);
                return null;
            }

            Activity.Current?.SetTag("bytes", bytes.Length);
            var text = DecodeCss(contentType, bytes);
            var sheet = CssParser.ParseStyleSheet(text, StyleOrigin.Author);
            _byUrl[key] = (sheet, url);
            StarlingTelemetry.Counter("engine.fetch.stylesheet", 1);
            return sheet;
        }
        catch (IOException ex)
        {
            StylesheetFetcherLog.StylesheetReadFailed(_log, ex, url.ToString());
            StarlingTelemetry.Counter("engine.fetch.stylesheet.failed", 1);
            return null;
        }
    }

    private static string DecodeCss(string? contentType, byte[] bytes)
    {
        // Honour BOM first (CSS Syntax 3 §3.2), then HTTP charset, else UTF-8.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (contentType is { Length: > 0 })
        {
            var charset = ExtractCharset(contentType);
            if (charset is not null && TryResolveEncoding(charset) is { } encoding)
            {
                return encoding.GetString(bytes);
            }
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static string? ExtractCharset(string headerValue)
    {
        foreach (var raw in headerValue.Split(';'))
        {
            var part = raw.Trim();
            const string prefix = "charset=";
            if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return part[prefix.Length..].Trim().Trim('"', '\'');
            }
        }
        return null;
    }

    private static Encoding? TryResolveEncoding(string name)
    {
        var canonical = WhatwgEncodingLabels.TryGetCanonicalName(name);
        if (canonical is null)
        {
            return null;
        }

        return canonical switch
        {
            "UTF-8" => Encoding.UTF8,
            "UTF-16LE" => Encoding.Unicode,
            "UTF-16BE" => Encoding.BigEndianUnicode,
            _ => TryGetEncodingByName(canonical),
        };
    }

    private static Encoding? TryGetEncodingByName(string name)
    {
        try { return Encoding.GetEncoding(name); }
        catch (ArgumentException) { return null; }
    }

    private static StarlingUrl? ResolveAbsolute(string href, StarlingUrl? baseUrl)
    {
        var parsed = baseUrl is null
            ? UrlParser.Parse(href)
            : UrlParser.Parse(href, baseUrl);
        return parsed.IsOk ? parsed.Value : null;
    }

    public void Dispose()
    {
        _byUrl.Clear();
        _byElement.Clear();
        if (_ownsHttp)
        {
            _sharedHttp?.Dispose();
        }

        _sharedHttp = null;
    }
}

internal static partial class StylesheetFetcherLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not resolve <link href='{Href}'>")]
    public static partial void CannotResolveStylesheetHref(ILogger logger, string href);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Missing local stylesheet: {Path}")]
    public static partial void MissingLocalStylesheet(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stylesheet fetch failed {Url}: {Error}")]
    public static partial void StylesheetFetchFailed(ILogger logger, string url, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stylesheet fetch HTTP {StatusCode} from {Url}")]
    public static partial void StylesheetFetchHttpError(ILogger logger, int statusCode, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unsupported stylesheet scheme '{Scheme}' for {Url}")]
    public static partial void UnsupportedStylesheetScheme(ILogger logger, string scheme, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Stylesheet read failed {Url}")]
    public static partial void StylesheetReadFailed(ILogger logger, Exception ex, string url);
}
