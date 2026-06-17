using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Starling.Common.Diagnostics;

using Starling.Css.FontFace;
using Starling.Css.Parser;
using Starling.Net;
using Starling.Paint;
using Starling.Url;
using StarlingUrl = global::Starling.Url.Url;

namespace Starling.Engine;

/// <summary>
/// Walks the document's parsed stylesheets, extracts every
/// <c>@font-face</c> rule, fetches each rule's <c>url()</c> sources, and
/// loads them as typefaces into a <see cref="FontFaceRegistry"/>. The
/// registry is then passed to the painter so the cascade's font-family
/// declarations can resolve to web fonts before falling back to system
/// faces. Fail-soft: a missing or unreadable font drops the source and
/// tries the next entry in the same <c>@font-face</c> block.
/// </summary>
internal sealed class FontFaceFetcher : IDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private readonly Func<StarlingHttpClient> _httpFactory;
    // Concurrent because FetchAllAsync warms the url() fetches in parallel;
    // their cache writes (on the network continuation) can race.
    private readonly ConcurrentDictionary<string, byte[]> _byUrl = new(StringComparer.Ordinal);
    private StarlingHttpClient? _sharedHttp;
    private readonly bool _ownsHttp;

    public FontFaceFetcher(ILoggerFactory loggerFactory, Func<StarlingHttpClient> httpFactory)
    {
        _loggerFactory = loggerFactory;
        _log = _loggerFactory.CreateLogger<FontFaceFetcher>();
        _httpFactory = httpFactory;
        _ownsHttp = true;
    }

    /// <summary>
    /// Use a caller-owned <see cref="StarlingHttpClient"/> so resource fetches
    /// share one connection pool — same-origin requests reuse the keep-alive
    /// transport instead of paying a fresh DNS+TCP+TLS handshake each time. The
    /// shared client is owned by the caller and is not disposed by this fetcher.
    /// </summary>
    public FontFaceFetcher(ILoggerFactory loggerFactory, StarlingHttpClient sharedHttp)
    {
        _loggerFactory = loggerFactory;
        _log = _loggerFactory.CreateLogger<FontFaceFetcher>();
        _sharedHttp = sharedHttp;
        _httpFactory = () => sharedHttp;
        _ownsHttp = false;
    }

    /// <summary>
    /// Resolves every <c>@font-face</c> in <paramref name="sheets"/> and registers
    /// the loaded typefaces under <paramref name="registry"/>. Each sheet entry
    /// carries its own base URL so relative <c>url()</c> values resolve against
    /// the sheet that declared them, not the document (per CSS Cascade §4).
    /// </summary>
    public async Task FetchAllAsync(
        IEnumerable<(StyleSheet Sheet, StarlingUrl? BaseUrl)> sheets,
        FontFaceRegistry registry,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(sheets);
        ArgumentNullException.ThrowIfNull(registry);

        // Flatten every @font-face rule with the base URL of the sheet that
        // declared it, enumerating the (possibly lazily-parsed) sheet sequence
        // exactly once.
        var rules = new List<(FontFaceRule Rule, StarlingUrl? BaseUrl)>();
        foreach (var (sheet, baseUrl) in sheets)
        {
            foreach (var rule in FontFaceParser.ParseAll(sheet))
            {
                rules.Add((rule, baseUrl));
            }
        }

        if (rules.Count == 0)
        {
            return;
        }

        // Pass 1: kick off the network fetch for each rule's first usable url()
        // source in parallel, so a page with N web fonts pays one wave of
        // round-trips instead of N sequential ones. A @font-face `src` is a
        // fallback chain (first entry that loads wins), and authors put the
        // preferred format first, so warming the first readable url() covers the
        // common single-round-trip case; the byte cache (keyed by absolute URL)
        // then lets pass 2 resolve from memory. A rare validation miss in pass 2
        // falls back to fetching the next entry on demand.
        var warm = new List<Task>();
        foreach (var (rule, baseUrl) in rules)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var source in rule.Sources)
            {
                if (source is not UrlFontSource url)
                {
                    continue;
                }

                if (!IsLikelyReadableFormat(url.Format))
                {
                    continue;
                }

                warm.Add(FetchBytesAsync(url.Url, baseUrl, ct));
                break; // the remaining url() entries are fallbacks
            }
        }
        if (warm.Count > 0)
        {
            await Task.WhenAll(warm).ConfigureAwait(false);
        }

        // Pass 2: resolve + register each rule in document order. Registration
        // mutates the (non-thread-safe) FontFaceRegistry, so it stays
        // single-threaded; the warm-up above has populated the byte cache, so
        // this reads bytes and registers without re-hitting the network.
        foreach (var (rule, baseUrl) in rules)
        {
            ct.ThrowIfCancellationRequested();
            await RegisterAsync(rule, baseUrl, registry, ct).ConfigureAwait(false);
        }
    }

    private async Task RegisterAsync(
        FontFaceRule rule,
        StarlingUrl? baseUrl,
        FontFaceRegistry registry,
        CancellationToken ct)
    {
        // CSS Fonts 3 §4.3: the user agent tries each entry in `src` in order
        // and uses the first that successfully loads. local() sources are
        // resolved by the system font manager; url() sources go through the
        // document loader.
        foreach (var source in rule.Sources)
        {
            switch (source)
            {
                case LocalFontSource local:
                    if (TryRegisterLocal(rule, local.Name, registry))
                    {
                        return;
                    }

                    break;
                case UrlFontSource url:
                    if (!IsLikelyReadableFormat(url.Format))
                    {
                        continue;
                    }

                    var bytes = await FetchBytesAsync(url.Url, baseUrl, ct).ConfigureAwait(false);
                    if (bytes is null)
                    {
                        continue;
                    }

                    if (registry.TryAdd(rule.FamilyName, rule.Bold, rule.Italic, bytes, rule.UnicodeRange))
                    {
                        StarlingTelemetry.Counter("engine.fetch.font", 1);
                        return;
                    }
                    break;
            }
        }

        FontFaceFetcherLog.FontFaceNoUsableSource(_log, rule.FamilyName);
    }

    private static bool TryRegisterLocal(FontFaceRule rule, string name, FontFaceRegistry registry)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }
        // local() lookups are best handled by the system font manager at
        // resolve time, not by us loading bytes. So we leave them for the
        // FontResolver fallback chain: any system family named in the
        // cascade's font-family list will be found there anyway. Returning
        // false here lets the next src entry try — typically a url().
        return false;
    }

    private static bool IsLikelyReadableFormat(string? format)
    {
        // Keep this as a coarse fetch filter only; FontFaceRegistry.TryAdd
        // asks SixLabors.Fonts to validate the actual bytes and fall through
        // to the next src entry when the advertised format is misleading.
        if (string.IsNullOrEmpty(format))
        {
            return true;
        }

        var f = format.Trim().ToLowerInvariant();
        return f is "truetype" or "opentype" or "ttf" or "otf" or "woff" or "woff2" or "svg";
    }

    private async Task<byte[]?> FetchBytesAsync(string href, StarlingUrl? baseUrl, CancellationToken ct)
    {
        var absolute = ResolveAbsolute(href, baseUrl);
        if (absolute is null)
        {
            FontFaceFetcherLog.CannotResolveFontUrl(_log, href);
            return null;
        }

        var key = absolute.ToString();
        if (_byUrl.TryGetValue(key, out var cached))
        {
            return cached;
        }

        using var _ = StarlingTelemetry.Span("engine", "fetch_font");
        Activity.Current?.SetTag("url", key);

        try
        {
            byte[] bytes;
            if (absolute.IsFile)
            {
                var path = absolute.ToFileSystemPath();
                if (!File.Exists(path))
                {
                    FontFaceFetcherLog.MissingLocalFont(_log, path);
                    StarlingTelemetry.Counter("engine.fetch.font.failed", 1);
                    return null;
                }
                bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            }
            else if (absolute.IsHttp || absolute.IsHttps)
            {
                _sharedHttp ??= _httpFactory();
                var response = await _sharedHttp.GetAsync(absolute, ct).ConfigureAwait(false);
                if (response.IsErr)
                {
                    FontFaceFetcherLog.FontFetchFailed(_log, absolute.ToString(), response.Error.ToString());
                    StarlingTelemetry.Counter("engine.fetch.font.failed", 1);
                    return null;
                }
                if (response.Value.StatusCode is < 200 or >= 400)
                {
                    FontFaceFetcherLog.FontFetchHttpError(_log, response.Value.StatusCode, absolute.ToString());
                    StarlingTelemetry.Counter("engine.fetch.font.failed", 1);
                    return null;
                }
                bytes = response.Value.Body.ToArray();
            }
            else
            {
                FontFaceFetcherLog.UnsupportedFontScheme(_log, absolute.Scheme);
                StarlingTelemetry.Counter("engine.fetch.font.failed", 1);
                return null;
            }

            Activity.Current?.SetTag("bytes", bytes.Length);
            _byUrl[key] = bytes;
            return bytes;
        }
        catch (IOException ex)
        {
            FontFaceFetcherLog.FontReadFailed(_log, ex, absolute.ToString());
            StarlingTelemetry.Counter("engine.fetch.font.failed", 1);
            return null;
        }
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
        if (_ownsHttp)
        {
            _sharedHttp?.Dispose();
        }

        _sharedHttp = null;
    }
}

internal static partial class FontFaceFetcherLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "@font-face '{FamilyName}' did not resolve to a usable source.")]
    public static partial void FontFaceNoUsableSource(ILogger logger, string familyName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not resolve @font-face url('{Href}')")]
    public static partial void CannotResolveFontUrl(ILogger logger, string href);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Missing local font: {Path}")]
    public static partial void MissingLocalFont(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Font fetch failed {Url}: {Error}")]
    public static partial void FontFetchFailed(ILogger logger, string url, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Font fetch HTTP {StatusCode} from {Url}")]
    public static partial void FontFetchHttpError(ILogger logger, int statusCode, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unsupported font scheme '{Scheme}'")]
    public static partial void UnsupportedFontScheme(ILogger logger, string scheme);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Font read failed {Url}")]
    public static partial void FontReadFailed(ILogger logger, Exception ex, string url);
}
