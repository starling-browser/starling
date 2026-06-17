using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Starling.Codecs;
using Starling.Common.Diagnostics;
using Starling.Common.Image;
using Starling.Dom;
using Starling.Layout.Tree;
using Starling.Net;
using Starling.Paint.Svg;
using Starling.Url;
using StarlingUrl = global::Starling.Url.Url;

namespace Starling.Engine;

/// <summary>
/// Resolves every <c>&lt;img src&gt;</c> in a <see cref="Document"/> to a
/// backend-neutral <see cref="DecodedImage"/> and exposes the results as an
/// <see cref="IImageResolver"/> for layout to query. Owns the decoded
/// images and disposes them when the fetcher itself is disposed.
/// </summary>
/// <remarks>
/// The actual decode goes through <see cref="NativeImageDecoder"/> — the
/// OS-native codec seam (ImageIO / WIC / libpng+libjpeg+libwebp) — which hands
/// back a backend-neutral <see cref="DecodedImage"/> so nothing downstream
/// names a concrete decoder type.
/// <para>
/// Caches per absolute URL so an image referenced N times decodes once. The
/// fetcher is fail-soft: a network or decode failure leaves the element
/// without a resolved image, which BoxTreeBuilder degrades to its
/// <c>alt</c> text.
/// </para>
/// </remarks>
internal sealed class ImageFetcher : IImageResolver, IDisposable
{
    private readonly Dictionary<Element, ResolvedImage> _byElement = [];
    // Concurrent because FetchAllAsync / FetchBackgroundsAsync start multiple
    // decodes in parallel; their cache writes (on the network/decode
    // continuation) can race.
    private readonly ConcurrentDictionary<string, DecodedImage> _byUrl = new(StringComparer.Ordinal);
    // Inline-<svg> rasters are decoded on demand (during layout) and aren't
    // keyed by URL, so track them separately for disposal.
    private readonly List<DecodedImage> _inlineSvg = [];
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private readonly Func<StarlingHttpClient> _httpFactory;
    private StarlingHttpClient? _sharedHttp;
    private readonly bool _ownsHttp;

    public ImageFetcher(ILoggerFactory loggerFactory, Func<StarlingHttpClient> httpFactory)
    {
        _loggerFactory = loggerFactory;
        _log = _loggerFactory.CreateLogger<ImageFetcher>();
        _httpFactory = httpFactory;
        _ownsHttp = true;
    }

    /// <summary>
    /// Use a caller-owned <see cref="StarlingHttpClient"/> so resource fetches
    /// share one connection pool — same-origin requests reuse the keep-alive
    /// transport instead of paying a fresh DNS+TCP+TLS handshake each time. The
    /// shared client is owned by the caller and is not disposed by this fetcher.
    /// </summary>
    public ImageFetcher(ILoggerFactory loggerFactory, StarlingHttpClient sharedHttp)
    {
        _loggerFactory = loggerFactory;
        _log = _loggerFactory.CreateLogger<ImageFetcher>();
        _sharedHttp = sharedHttp;
        _httpFactory = () => sharedHttp;
        _ownsHttp = false;
    }

    /// <summary>
    /// Total number of resolved images currently cached — both element-keyed
    /// (<c>&lt;img&gt;</c> intrinsic sizes) and URL-keyed (CSS
    /// <c>background-image</c> / prefetched url()). The engine snapshots this
    /// before and after the post-script resource fetch to decide whether a new
    /// layout-affecting image arrived (a late intrinsic size changes layout); if
    /// the count is unchanged it can safely reuse the pre-script layout.
    /// </summary>
    public int LoadedCount => _byElement.Count + _byUrl.Count;

    public bool TryResolve(Element element, out ResolvedImage image)
        => _byElement.TryGetValue(element, out image);

    /// <summary>
    /// Rasterize an inline <c>&lt;svg&gt;</c> on demand: serialize the subtree
    /// to an SVG document and run the managed rasterizer, resolving
    /// <c>currentColor</c> against the element's computed color. Cached per
    /// element so repeated layout passes decode once. Fail-soft — a malformed or
    /// empty SVG returns false and layout falls back to the accessible name.
    /// </summary>
    public bool TryResolveInlineSvg(Element svg, Starling.Css.Values.CssColor currentColor, out ResolvedImage image)
    {
        if (_byElement.TryGetValue(svg, out image))
        {
            return true;
        }

        try
        {
            var doc = InlineSvgSerializer.Serialize(svg);
            var decoded = SvgImageDecoder.DecodeText(doc, currentColor);
            image = new ResolvedImage(decoded.Width, decoded.Height, decoded);
            _byElement[svg] = image;
            _inlineSvg.Add(decoded);
            StarlingTelemetry.Counter("engine.inline_svg", 1);
            return true;
        }
        catch (Exception ex) when (ex is SvgDecodeException or System.Xml.XmlException)
        {
            ImageFetcherLog.InlineSvgDecodeFailed(_log, ex.Message);
            StarlingTelemetry.Counter("engine.inline_svg.failed", 1);
            image = default;
            return false;
        }
    }

    public bool TryResolveUrl(string url, out DecodedImage image)
    {
        // The fetcher's prefetch pass keys the cache by absolute URL string;
        // the paint pipeline calls this with the verbatim CSS url() value, so
        // we look up by the same key the resolver code stored.
        if (_byUrl.TryGetValue(url, out var cached))
        {
            image = cached;
            return true;
        }
        image = null!;
        return false;
    }

    public async Task FetchAllAsync(Document document, StarlingUrl? baseUrl, CancellationToken ct)
        => await FetchAllAsync(document, baseUrl, viewportWidthCssPx: 0, fontSizeCssPx: 16, ct).ConfigureAwait(false);

    /// <summary>
    /// Fetch every <c>&lt;img&gt;</c> in the document. When an element has
    /// <c>srcset</c> (and optionally <c>sizes</c>), pick the best candidate
    /// for the supplied viewport and record the
    /// <see href="https://html.spec.whatwg.org/multipage/images.html#density-corrected-intrinsic-width-and-height">
    /// density-corrected intrinsic dimensions</see> so layout sizes the image
    /// against the sizes-derived CSS width instead of the source bitmap's
    /// pixel dimensions.
    /// </summary>
    public async Task FetchAllAsync(
        Document document, StarlingUrl? baseUrl,
        double viewportWidthCssPx, double fontSizeCssPx,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Two-pass fetch so the images download in parallel instead of one
        // round-trip after another. Pass 1 walks the document and kicks off each
        // fetch+decode without awaiting (the synchronous prefix — srcset
        // selection, URL resolve, cache probe — still runs sequentially here;
        // only the network I/O + decode overlap). Pass 2 awaits them all, then
        // records the resolved intrinsic sizes against each element.
        var pending = new List<(Element Img, double CorrectedW, double CorrectedH, Task<DecodedImage?> Task)>();
        foreach (var img in document.GetElementsByTagName("img"))
        {
            ct.ThrowIfCancellationRequested();

            var src = img.GetAttribute("src");
            var srcset = img.GetAttribute("srcset");
            var sizes = img.GetAttribute("sizes");

            var (selectedUrl, correctedW, correctedH) = Srcset.Select(
                srcset, sizes, src, viewportWidthCssPx, fontSizeCssPx);

            if (string.IsNullOrWhiteSpace(selectedUrl))
            {
                continue;
            }

            var absolute = ResolveAbsolute(selectedUrl, baseUrl);
            if (absolute is null)
            {
                ImageFetcherLog.CannotResolveImgSrc(_log, selectedUrl);
                continue;
            }

            pending.Add((img, correctedW, correctedH, FetchAndDecodeAsync(absolute, ct)));
        }

        if (pending.Count == 0)
        {
            return;
        }

        // Task.WhenAll observes every task so a cancellation can't leave an
        // unobserved faulted task behind, matching the old sequential loop.
        await Task.WhenAll(pending.Select(static p => p.Task)).ConfigureAwait(false);

        foreach (var (img, correctedW, correctedH, task) in pending)
        {
            var decoded = task.Result; // completed; null = fetch/decode failed
            if (decoded is null)
            {
                continue;
            }

            // density-corrected intrinsic: if sizes gave us a CSS-px width,
            // scale height proportionally to preserve the source aspect
            // ratio. Otherwise fall back to source intrinsic dims — the true
            // image dimensions, even when the decode was resolution-clamped
            // and the pixel buffer is smaller.
            double intrinsicW, intrinsicH;
            if (correctedW > 0 && decoded.IntrinsicWidth > 0 && decoded.IntrinsicHeight > 0)
            {
                intrinsicW = correctedW;
                intrinsicH = correctedH > 0
                    ? correctedH
                    : correctedW * decoded.IntrinsicHeight / decoded.IntrinsicWidth;
            }
            else
            {
                intrinsicW = decoded.IntrinsicWidth;
                intrinsicH = decoded.IntrinsicHeight;
            }
            _byElement[img] = new ResolvedImage(intrinsicW, intrinsicH, decoded);
        }
    }

    /// <summary>
    /// Walk a parsed stylesheet for <c>url(...)</c> references in
    /// background-image declarations and prefetch each one so paint can resolve
    /// the URL synchronously when emitting display items. The CSS url() value
    /// is also indexed verbatim so the paint pipeline's lookup matches without
    /// the caller needing to re-resolve URLs.
    /// </summary>
    public async Task FetchBackgroundsAsync(IEnumerable<(Starling.Css.Parser.StyleSheet Sheet, StarlingUrl? BaseUrl)> stylesheets, StarlingUrl? documentBaseUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stylesheets);

        // (raw url, base url used to resolve relative references). External
        // stylesheets carry their own base URL so relative paths inside them
        // resolve against the sheet, not the document.
        var rawByBase = new Dictionary<string, StarlingUrl?>(StringComparer.Ordinal);
        foreach (var (sheet, sheetBase) in stylesheets)
        {
            var perSheet = new HashSet<string>(StringComparer.Ordinal);
            CollectBackgroundUrls(sheet, perSheet);
            foreach (var raw in perSheet)
            {
                rawByBase.TryAdd(raw, sheetBase ?? documentBaseUrl);
            }
        }

        // Two-pass like FetchAllAsync: kick off every background fetch+decode in
        // parallel (pass 1), then index the results under the raw CSS url()
        // string (pass 2) so the paint-time lookup — which only sees the CSS
        // value — finds the prefetched bitmap.
        var pending = new List<(string Raw, Task<DecodedImage?> Task)>();
        foreach (var (raw, baseUrl) in rawByBase)
        {
            ct.ThrowIfCancellationRequested();
            if (_byUrl.ContainsKey(raw))
            {
                continue;
            }

            var absolute = ResolveAbsolute(raw, baseUrl);
            if (absolute is null)
            {
                continue;
            }

            pending.Add((raw, FetchAndDecodeAsync(absolute, ct)));
        }

        if (pending.Count == 0)
        {
            return;
        }

        await Task.WhenAll(pending.Select(static p => p.Task)).ConfigureAwait(false);

        foreach (var (raw, task) in pending)
        {
            var decoded = task.Result;
            if (decoded is null)
            {
                continue;
            }

            _byUrl[raw] = decoded;
        }
    }

    private static void CollectBackgroundUrls(Starling.Css.Parser.StyleSheet sheet, HashSet<string> urls)
    {
        foreach (var rule in sheet.Rules)
        {
            CollectFromRule(rule, urls);
        }
    }

    private static void CollectFromRule(Starling.Css.Parser.CssRule rule, HashSet<string> urls)
    {
        if (rule is Starling.Css.Parser.StyleRule sr)
        {
            foreach (var decl in sr.Declarations)
            {
                CollectFromDeclaration(decl, urls);
            }
        }
        else if (rule is Starling.Css.Parser.AtRule at)
        {
            // Walk inner rules (e.g. @media wraps StyleRule children) and any
            // declarations carried directly on the at-rule (rare, but harmless).
            foreach (var inner in at.Rules)
            {
                CollectFromRule(inner, urls);
            }

            foreach (var decl in at.Declarations)
            {
                CollectFromDeclaration(decl, urls);
            }
        }
    }

    private static void CollectFromDeclaration(Starling.Css.Parser.CssDeclaration decl, HashSet<string> urls)
    {
        // Prefetch url() images referenced by background-image, mask-image (and
        // the -webkit-mask-* aliases / mask shorthand), and custom properties.
        // Custom properties are included because a mask-image: var(--x) reads its
        // url() from the --x declaration, not from the mask-image declaration
        // itself, so the raw url() only appears on the custom property.
        if (!WantsImagePrefetch(decl.Name))
        {
            return;
        }

        foreach (var v in Starling.Css.Values.CssValueParser.ParseList(decl.Value))
        {
            if (v is Starling.Css.Values.CssUrl u && !string.IsNullOrEmpty(u.Value))
            {
                urls.Add(u.Value);
            }
        }
    }

    private static bool WantsImagePrefetch(string name)
        => name.Contains("background", StringComparison.OrdinalIgnoreCase)
            || name.Contains("mask", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("--", StringComparison.Ordinal);

    private async Task<DecodedImage?> FetchAndDecodeAsync(StarlingUrl url, CancellationToken ct)
    {
        var key = url.ToString();
        if (_byUrl.TryGetValue(key, out var cached))
        {
            return cached;
        }

        using var _ = StarlingTelemetry.Span("engine", "fetch_image");
        Activity.Current?.SetTag("url", key);

        try
        {
            byte[] bytes;
            if (url.IsFile)
            {
                var path = url.ToFileSystemPath();
                if (!File.Exists(path))
                {
                    ImageFetcherLog.MissingLocalImage(_log, path);
                    StarlingTelemetry.Counter("engine.fetch.image.failed", 1);
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
                    ImageFetcherLog.ImageFetchFailed(_log, url.ToString(), response.Error.ToString());
                    StarlingTelemetry.Counter("engine.fetch.image.failed", 1);
                    return null;
                }
                Activity.Current?.SetTag("http.status_code", response.Value.StatusCode);
                if (response.Value.StatusCode is < 200 or >= 400)
                {
                    ImageFetcherLog.ImageFetchHttpError(_log, response.Value.StatusCode, url.ToString());
                    StarlingTelemetry.Counter("engine.fetch.image.failed", 1);
                    return null;
                }
                bytes = response.Value.Body.ToArray();
            }
            else if (url.IsData)
            {
                // RFC 2397 data: URLs are decoded locally — never hit the
                // network. Image-bearing data URIs are very common (Google,
                // GitHub, many SPAs inline icons), so without this branch
                // every such <img> degrades to its alt-text fallback.
                if (!DataUrl.TryDecode(url, out var payload))
                {
                    ImageFetcherLog.MalformedDataUrl(_log);
                    StarlingTelemetry.Counter("engine.fetch.image.failed", 1);
                    return null;
                }
                bytes = payload.Bytes;
            }
            else
            {
                ImageFetcherLog.UnsupportedImageScheme(_log, url.Scheme, url.ToString());
                StarlingTelemetry.Counter("engine.fetch.image.failed", 1);
                return null;
            }

            Activity.Current?.SetTag("bytes", bytes.Length);
            // SVG is a vector (XML) format the OS-native raster codecs cannot
            // touch. Sniff it first and route to the pure-managed rasterizer in
            // Starling.Paint (ImageSharp.Drawing); everything else goes through
            // NativeImageDecoder, which sniffs PNG/JPEG/WebP/GIF/BMP and decodes
            // via the OS-native codec. Both return a backend-neutral DecodedImage
            // (straight RGBA8888, top-down, tightly packed) so nothing
            // downstream names a concrete decoder type.
            DecodedImage decoded = NativeImageDecoder.IsSvg(bytes)
                ? SvgImageDecoder.Decode(bytes)
                : NativeImageDecoder.Decode(bytes);
            Activity.Current?.SetTag("image.w", decoded.Width);
            Activity.Current?.SetTag("image.h", decoded.Height);
            // Two identical URLs discovered in the same parallel pass both miss
            // the cache probe above and each decode; keep the first to land and
            // dispose the loser so the cache (and disposal) own exactly one
            // bitmap per URL.
            var winner = _byUrl.GetOrAdd(key, decoded);
            if (!ReferenceEquals(winner, decoded))
            {
                decoded.Dispose();
                return winner;
            }
            StarlingTelemetry.Counter("engine.fetch.image", 1);
            return decoded;
        }
        catch (Exception ex) when (ex is IOException or ImageDecodeException or SvgDecodeException)
        {
            ImageFetcherLog.ImageDecodeFailed(_log, ex, url.ToString());
            StarlingTelemetry.Counter("engine.fetch.image.failed", 1);
            return null;
        }
    }

    private static StarlingUrl? ResolveAbsolute(string src, StarlingUrl? baseUrl)
    {
        var parsed = baseUrl is null
            ? UrlParser.Parse(src)
            : UrlParser.Parse(src, baseUrl);
        return parsed.IsOk ? parsed.Value : null;
    }

    public void Dispose()
    {
        foreach (var decoded in _byUrl.Values)
        {
            decoded.Dispose();
        }

        _byUrl.Clear();
        foreach (var decoded in _inlineSvg)
        {
            decoded.Dispose();
        }

        _inlineSvg.Clear();
        _byElement.Clear();
        if (_ownsHttp)
        {
            _sharedHttp?.Dispose();
        }

        _sharedHttp = null;
    }
}

internal static partial class ImageFetcherLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Inline <svg> decode failed: {Message}")]
    public static partial void InlineSvgDecodeFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not resolve <img src='{Src}'>")]
    public static partial void CannotResolveImgSrc(ILogger logger, string src);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Missing local image: {Path}")]
    public static partial void MissingLocalImage(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Image fetch failed {Url}: {Error}")]
    public static partial void ImageFetchFailed(ILogger logger, string url, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Image fetch HTTP {StatusCode} from {Url}")]
    public static partial void ImageFetchHttpError(ILogger logger, int statusCode, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Malformed data: URL for image")]
    public static partial void MalformedDataUrl(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Unsupported image scheme '{Scheme}' for {Url}")]
    public static partial void UnsupportedImageScheme(ILogger logger, string scheme, string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Image decode failed {Url}")]
    public static partial void ImageDecodeFailed(ILogger logger, Exception ex, string url);
}
