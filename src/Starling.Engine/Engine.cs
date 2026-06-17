using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Common;
using Starling.Common.Diagnostics;
using Starling.Common.Encoding;
using Starling.Bindings;
using Starling.Css;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Dom;
using Starling.Js.Hosting;
using Starling.Net;
using Starling.Paint;
using Starling.Url;
using LayoutBox = Starling.Layout.Box.Box;
using LayoutRect = Starling.Layout.Rect;
using LayoutSize = Starling.Layout.Size;
using StarlingUrl = global::Starling.Url.Url;

namespace Starling.Engine;

/// <summary>
/// Engine façade. Loads a URL, parses HTML, runs scripts and the
/// style/layout/paint pipeline, and returns a rendered page or bitmap.
/// </summary>
/// <remarks>
/// <see cref="BrowserSession"/> layers navigation history, cookies, and
/// interactive page reuse on top of this engine.
/// </remarks>
public sealed class StarlingEngine
{
    private const int MaxRedirects = 10;

    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private readonly Painter _painter;
    private readonly Func<StarlingHttpClient> _httpFactory;

    // Per-box-tree marker: declarative @keyframes animations are primed
    // (registered from each element's static animation-* cascade) exactly once
    // per laid-out box tree, so the GUI animation loop can see them without
    // running a full RenderWithStyle cascade. Keyed on the box-tree root (rebuilt
    // every layout) so a relayout re-primes against the fresh cascade — picking
    // up any animation-* changes — while frame-to-frame ticks on the same tree
    // skip the walk. The AnimationEngine itself now persists per document (see
    // Painter.GetAnimationTimeline), so playback survives the re-prime via
    // OnAnimationsCascaded's match-by-name.
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Starling.Layout.Box.Box, object> _primedTrees = new();

    // Dedicated logger for JS console output — DevTools and the MCP query tool
    // filter on the exact category "Starling.engine.js".
    private readonly ILogger _jsConsoleLog;

    static StarlingEngine()
    {
        // Register the .NET base class library CodePages provider once so WHATWG legacy
        // single-byte (windows-1250…1258, ISO-8859-2…16, KOI8-*, mac*)
        // and CJK (Shift_JIS, GBK, gb18030, Big5, EUC-KR, …) labels
        // resolve. CodePages is a pure-managed NuGet package.
        System.Text.Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public StarlingEngine(ILoggerFactory? loggerFactory = null, Painter? painter = null,
        Func<StarlingHttpClient>? httpFactory = null)
    {
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = _loggerFactory.CreateLogger<StarlingEngine>();
        _jsConsoleLog = _loggerFactory.CreateLogger("Starling.engine.js");
        _painter = painter ?? new Painter(loggerFactory: _loggerFactory);
        // Default factory: share one CookieJar across every client this engine
        // creates (main document fetch + per-session XHR/fetch + images/fonts) so
        // Set-Cookie from one request is carried to the next. Real sites gate
        // content on session cookies set during a token/auth handshake (e.g.
        // McMaster's tokenauthorization.aspx → ProdPageWebPart.aspx), which 403s
        // without the cookie. Also forward logger so net spans/logs surface.
        var sharedCookies = new Starling.Net.Http.Cookies.CookieJar();
        _httpFactory = httpFactory ?? (() => new StarlingHttpClient(new StarlingHttpClientOptions
        {
            CookieJar = sharedCookies,
            LoggerFactory = _loggerFactory,
        }));
    }

    /// <summary>
    /// Render <paramref name="url"/> into a PNG written to <paramref name="outputPath"/>.
    /// Returns <c>true</c> on success.
    /// </summary>
    /// <remarks>
    /// Supports <c>file://</c>, <c>http://</c>, and <c>https://</c> URLs. The
    /// returned <see cref="RenderOutcome.DisplayText"/> is a diagnostic text
    /// summary; the PNG is produced from the full parsed document.
    /// </remarks>
    public async Task<Result<RenderOutcome, RenderError>> RenderAsync(
        string url, RenderOptions options, string outputPath, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(outputPath);

        StarlingTelemetry.Counter("engine.page_load", 1);
        using var _ = StarlingTelemetry.Span("engine", $"render {url} -> {outputPath}");
        Activity.Current?.SetTag("http.url", url);
        Activity.Current?.SetTag("viewport.w", options.Viewport.Width);
        Activity.Current?.SetTag("viewport.h", options.Viewport.Height);
        Activity.Current?.SetTag("font_size", options.FontSize);
        Activity.Current?.SetTag("output.path", outputPath);
        var roz = new RozRuntime(options.Roz, _loggerFactory);
        if (roz.Checkpoint("start") is { } startErr)
        {
            return Fail(startErr);
        }

        var parsed = UrlParser.Parse(url);
        if (parsed.IsErr)
        {
            return Fail($"URL parse failed: {parsed.Error}");
        }

        var u = parsed.Value;
        // One HTTP client (one connection pool) for the whole page load: the
        // document fetch and every resource fetch (css/js/fonts/images) share
        // it, so same-origin requests reuse a keep-alive transport instead of
        // re-doing DNS+TCP+TLS per request. Cheap to construct (no socket until
        // first use), so it is created even for file:// renders.
        using var http = _httpFactory();
        string html;
        try
        {
            if (u.IsFile)
            {
                var path = u.ToFileSystemPath();
                if (!File.Exists(path))
                {
                    return Fail($"File not found: {path}");
                }

                using (StarlingTelemetry.Span("engine", "read_file"))
                {
                    html = File.ReadAllText(path);
                    Activity.Current?.SetTag("file.path", path);
                    Activity.Current?.SetTag("html.bytes", html.Length);
                }
            }
            else if (u.IsHttp || u.IsHttps)
            {
                Result<(string Html, StarlingUrl FinalUrl, Starling.Net.Http.ConnectionSecurity? Security), RenderError> fetched;
                using (StarlingTelemetry.Span("engine", "fetch_html"))
                {
                    fetched = await FetchHtmlAsync(u, http, ct).ConfigureAwait(false);
                }
                if (fetched.IsErr)
                {
                    return Fail(fetched.Error.Message);
                }

                html = fetched.Value.Html;
                // Resolve subsequent relative URLs (images, stylesheets, fonts)
                // against the post-redirect host. Typing `https://google.com`
                // 301s to `https://www.google.com/`, and the page's
                // `<img src="/images/..."` only resolves on the www host.
                u = fetched.Value.FinalUrl;
            }
            else
            {
                return Fail($"Unsupported scheme '{u.Scheme}'.");
            }
        }
        catch (IOException ex)
        {
            return Fail(ex.Message);
        }

        // Install the selected HTML parser backend (Starling by default, or
        // AngleSharp when STARLING_HTML_PARSER=anglesharp) before the first
        // parse. Idempotent, so the progressive-paint path below can repeat it.
        HtmlBackendSelector.EnsureInstalled();

        Document doc;
        using (StarlingTelemetry.Span("engine", "parse_html"))
        {
            Activity.Current?.SetTag("html.bytes", html.Length);
            // The engine runs page JavaScript, so HTML parsing uses the
            // scripting flag ENABLED (WHATWG HTML §13.2). This makes
            // <noscript> contents inert raw text instead of parsed elements.
            doc = Html.HtmlParser.Parse(html, scriptingEnabled: true);
        }
        if (roz.Checkpoint("post_parse_html") is { } parseErr)
        {
            return Fail(parseErr);
        }

        if (roz.CheckDomBudget(doc, "post_parse_html") is { } parseDomErr)
        {
            return Fail(parseDomErr);
        }

        using var images = new ImageFetcher(_loggerFactory, http);
        using var stylesheets = new StylesheetFetcher(_loggerFactory, http);
        using var scripts = new ScriptFetcher(_loggerFactory, http);

        // Start downloading external scripts now, concurrently with the
        // stylesheet/image fetch below (and the font fetch that follows).
        // Scripts have no data dependency on stylesheets or fonts being
        // *downloaded* — only script *execution* must wait for stylesheets to
        // apply, which is enforced where scripts run below — so serializing the
        // downloads only inflates the critical path. FetchAllAsync enumerates
        // the <script> elements synchronously before its first await, so this
        // reads the DOM before any concurrent fetch can touch it. The task is
        // awaited just before scripts execute.
        var scriptsFetch = scripts.FetchAllAsync(doc, baseUrl: u, ct);

        using var webFonts = new FontFaceRegistry();
        using var fontFaceFetcher = new FontFaceFetcher(_loggerFactory, http);

        // Fonts are declared by @font-face in the author stylesheets, so the font
        // fetch depends on the stylesheets being downloaded + parsed — but not on
        // the images. Chain the font fetch onto the stylesheet fetch and run that
        // chain alongside the image fetch, so web fonts start downloading the
        // moment the sheets land instead of waiting out the whole image wave.
        async Task FetchSheetsThenFontsAsync()
        {
            await stylesheets.FetchAllAsync(doc, baseUrl: u, ct).ConfigureAwait(false);
            using (StarlingTelemetry.Span("engine", "fetch_fonts"))
            {
                await fontFaceFetcher
                    .FetchAllAsync(EnumerateAuthorSheets(doc, u, stylesheets), webFonts, ct)
                    .ConfigureAwait(false);
            }
        }

        using (StarlingTelemetry.Span("engine", "fetch_resources"))
        {
            await Task.WhenAll(
                images.FetchAllAsync(doc, baseUrl: u, options.Viewport.Width, options.FontSize, ct),
                FetchSheetsThenFontsAsync()
            ).ConfigureAwait(false);
        }
        if (roz.Checkpoint("post_fetch_resources") is { } fetchErr)
        {
            return Fail(fetchErr);
        }

        // Run page JavaScript. Mutations to the DOM (text content, new
        // elements, <img src=…> writes) land on `doc` and feed into the
        // layout/paint pass below. Best-effort: a script that throws is
        // logged via the realm's console sink and the remaining scripts
        // still run.
        //
        // The host built for JS holds the box tree it last laid out. When a
        // script reads geometry (so a layout was materialized) and nothing
        // layout-affecting changes afterwards, the engine reuses that exact box
        // tree for the final paint instead of running a second full cascade +
        // layout (Win B). `jsLayoutHost` survives past the script block; it is
        // nulled when a late resource invalidates the tree, and reuse is
        // additionally gated on HasLayout + an unchanged mutation version below.
        BoxLayoutHost? jsLayoutHost = null;
        // Scroll store for this render's layout passes — lets script reads of
        // scrollWidth/scrollHeight (WP3) see real scrollable overflow even on
        // the headless path. No shell ever writes offsets here.
        var scrollState = new Starling.Layout.Scroll.ScrollStateStore();
        {
            // The script downloads were kicked off above and have been running
            // concurrently with the stylesheet/font fetch; await their
            // completion now, before any script executes.
            using (StarlingTelemetry.Span("engine", "fetch_scripts"))
            {
                await scriptsFetch.ConfigureAwait(false);
            }
            if (scripts.Scripts.Count > 0 || scripts.ModuleScripts.Count > 0)
            {
                // Lay out against the pre-script DOM so JS can call
                // getBoundingClientRect / offsetWidth / getComputedStyle and
                // receive real numbers. The host is handed a recompute
                // delegate keyed on the document's mutation version, so a read
                // issued after a DOM mutation in the same script run lazily
                // re-runs layout and reflects the post-mutation geometry.
                var viewport = new LayoutSize(options.Viewport.Width, options.Viewport.Height);
                // Lazy pre-script layout: only run the (expensive) full layout
                // if a script actually reads geometry / computed style. Pages
                // whose scripts never touch layout (analytics beacons, etc.)
                // skip the pass entirely — the render_document layout below is
                // then the only one that runs. The span lives inside the
                // delegate so it is recorded only when layout truly happens.
                (Starling.Layout.Box.BlockBox Root, Starling.Css.Cascade.StyleEngine Style) Relayout(string? trigger)
                {
                    using (StarlingTelemetry.Span("engine", "prelayout_for_js"))
                    {
                        // The trigger names the JS read that forced this
                        // layout (offsetWidth / getBoundingClientRect / etc.).
                        // Aspire shows it inline on the span, so an inline
                        // script's forced-reflow origin is one click away.
                        if (trigger is not null)
                        {
                            System.Diagnostics.Activity.Current?.SetTag("layout.trigger", trigger);
                            // Also emit as a log so the Aspire structured-logs
                            // API surfaces it — span attributes aren't returned
                            // by list_trace_structured_logs, and a forced
                            // prelayout is a notable event (~200 ms per
                            // occurrence on a real page) worth Info severity.
                            StarlingEngineLog.PrelayoutTrigger(_log, trigger);
                        }
                        var sw = System.Diagnostics.Stopwatch.StartNew();
                        var result = _painter.LayoutDocumentWithStyle(
                            doc, viewport, options.FontSize, images, stylesheets.Resolve, webFonts,
                            colorScheme: options.PreferredColorScheme, ct: ct, scrollState: scrollState);
                        sw.Stop();
                        // Surface wall time alongside the trigger so the
                        // structured-logs view shows where forced-reflow time
                        // is going without needing to click the trace.
                        StarlingEngineLog.PrelayoutDone(_log, trigger ?? "<no-trigger>", sw.ElapsedMilliseconds);
                        return result;
                    }
                }
                // Lightweight cascade-only builder for getComputedStyle reads
                // on purely-cascaded properties (visibility/opacity/display/…).
                // Avoids the full ~400 ms layout when google.com's startup
                // script touches `.visibility` before any geometry is read.
                Starling.Css.Cascade.StyleEngine BuildCascadeOnly()
                    => _painter.BuildStyleEngine(
                        doc, viewport, options.FontSize, stylesheets.Resolve,
                        options.PreferredColorScheme);
                jsLayoutHost = new BoxLayoutHost(doc, Relayout, BuildCascadeOnly, scrollState);

                using (StarlingTelemetry.Span("engine", "run_scripts"))
                {
                    await RunScriptsAsync(doc, u, scripts, jsLayoutHost, viewport, ct).ConfigureAwait(false);
                }
                StarlingEngineLog.LayoutHostStats(_log, jsLayoutHost.RelayoutCount, jsLayoutHost.FreshHits);
                // Surface which attribute names drove the mutation-version bumps
                // so we can decide where layout-irrelevance heuristics would pay.
                if (doc.AttributeMutationCounts.Count > 0)
                {
                    var top = doc.AttributeMutationCounts
                        .OrderByDescending(kv => kv.Value)
                        .Take(10);
                    var sb = new System.Text.StringBuilder();
                    foreach (var kv in top)
                    {
                        if (sb.Length > 0)
                        {
                            sb.Append(", ");
                        }

                        sb.Append($"{kv.Key}={kv.Value}");
                    }
                    StarlingEngineLog.AttrMutations(_log, sb.ToString());
                }

                // Snapshot how many images/stylesheets were loaded before the
                // post-script fetch. If that fetch pulls in anything new, a late
                // image intrinsic size or a new sheet's cascade can change
                // layout — so we must NOT reuse the pre-script box tree.
                var imagesBefore = images.LoadedCount;
                var sheetsBefore = stylesheets.LoadedCount;

                // Scripts may have added <img> or new stylesheet links;
                // re-run the resource fetch so the post-script DOM is
                // fully loaded before paint. URL dedupe makes the second
                // pass cheap for unchanged content.
                using (StarlingTelemetry.Span("engine", "fetch_resources_post_js"))
                {
                    await Task.WhenAll(
                        images.FetchAllAsync(doc, baseUrl: u, options.Viewport.Width, options.FontSize, ct),
                        stylesheets.FetchAllAsync(doc, baseUrl: u, ct)
                    ).ConfigureAwait(false);
                }

                if (images.LoadedCount != imagesBefore || stylesheets.LoadedCount != sheetsBefore)
                {
                    // A late resource arrived — its intrinsic size / cascade may
                    // shift layout. Discard the cached tree and re-layout.
                    jsLayoutHost = null;
                }
            }
        }
        if (roz.Checkpoint("post_scripts") is { } scriptsErr)
        {
            return Fail(scriptsErr);
        }

        if (roz.CheckDomBudget(doc, "post_scripts") is { } scriptsDomErr)
        {
            return Fail(scriptsDomErr);
        }

        // Prefetch CSS-referenced background-image url()s now so the paint
        // pipeline can resolve them synchronously when emitting display items.
        using (StarlingTelemetry.Span("engine", "fetch_backgrounds"))
        {
            await images
                .FetchBackgroundsAsync(EnumerateAuthorSheets(doc, u, stylesheets), u, ct)
                .ConfigureAwait(false);
        }
        if (roz.Checkpoint("post_fetch_backgrounds") is { } bgErr)
        {
            return Fail(bgErr);
        }

        // Extract display text from the post-script DOM so JS-driven
        // mutations (innerText/textContent writes, appended children, fetch
        // result rendering) land in RenderOutcome.DisplayText.
        var displayText = ExtractDisplayText(doc);

        var renderViewport = new LayoutSize(options.Viewport.Width, options.Viewport.Height);

        // Win B: reuse the layout JS already materialized when it is provably
        // still valid, painting straight from that box tree instead of running a
        // second full cascade + layout. ALL of these must hold:
        //   • a layout was actually materialized for JS — with the lazy host that
        //     means a script read geometry / computed style (HasLayout);
        //   • the DOM has not mutated since that layout
        //     (doc.LayoutInvalidationVersion == host.LaidOutVersion);
        //   • no layout-affecting resource arrived after it (jsLayoutHost is
        //     nulled above when fetch_resources_post_js loaded a new image/sheet);
        //   • viewport / font-size / color-scheme are unchanged within a single
        //     render (the host laid out with these exact options, so identical by
        //     construction).
        // Background images prefetched in fetch_backgrounds above are paint-only
        // (they never change layout) and resolve through the same images resolver
        // during the display-list build, so they do not block reuse.
        var canReuse = jsLayoutHost is { } host
            && host.HasLayout
            && doc.LayoutInvalidationVersion == host.LaidOutVersion;

        Starling.Common.Image.RenderedBitmap bitmap;
        using (StarlingTelemetry.Span("engine", "render_document"))
        {
            if (canReuse)
            {
                StarlingTelemetry.Counter("engine.render.reused_prelayout", 1);
                var (reuseRoot, _) = jsLayoutHost!.Materialized;
                bitmap = _painter.PaintLaidOut(reuseRoot, renderViewport, images, webFonts);
            }
            else
            {
                bitmap = _painter.RenderDocument(
                    doc,
                    renderViewport,
                    options.FontSize,
                    images,
                    stylesheets.Resolve,
                    webFonts,
                    colorScheme: options.PreferredColorScheme);
            }
            Activity.Current?.SetTag("image.w", bitmap.Width);
            Activity.Current?.SetTag("image.h", bitmap.Height);
        }
        if (roz.Checkpoint("post_render_document") is { } renderErr)
        {
            return Fail(renderErr);
        }

        try
        {
            try
            {
                using (StarlingTelemetry.Span("engine", "save_png"))
                {
                    EnsureOutputDirectory(outputPath);
                    // PNG encode stays via ImageSharp for now: wrap the
                    // backend-neutral RGBA8888 bytes back into an Image<Rgba32>
                    // purely for the encoder. LoadPixelData copies.
                    using var image = Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(
                        bitmap.Rgba, bitmap.Width, bitmap.Height);
                    image.SaveAsPng(outputPath);
                }
            }
            catch (IOException ex)
            {
                return Fail($"Save failed: {ex.Message}");
            }
            if (roz.Checkpoint("post_save_png") is { } saveErr)
            {
                return Fail(saveErr);
            }

            StarlingEngineLog.RenderComplete(_log, outputPath, bitmap.Width, bitmap.Height, displayText.Length);

            return Result<RenderOutcome, RenderError>.Ok(
                new RenderOutcome(outputPath, bitmap.Width, bitmap.Height, displayText));
        }
        finally
        {
            bitmap.Dispose();
        }

        Result<RenderOutcome, RenderError> Fail(string message)
        {
            StarlingTelemetry.Counter("engine.page_load.failed", 1);
            StarlingEngineLog.RenderFailed(_log, message);
            return Result<RenderOutcome, RenderError>.Err(new RenderError(message));
        }
    }

    /// <summary>
    /// Load <paramref name="url"/>, parse it, style it, and lay it out — but do
    /// not rasterize. Returns the box tree wrapped in a <see cref="LaidOutPage"/>
    /// the caller owns; disposing the page releases fetched image bitmaps and
    /// parsed stylesheets. Interactive shells use this to walk the structure
    /// and emit native views rather than displaying a flat bitmap.
    /// </summary>
    // sharedHttp: optional session-scoped HTTP client. When supplied (the
    // interactive browsing path), the document and all resource fetches reuse it,
    // so warm HTTP/2 connections, pooled keep-alive transports, and the DNS cache
    // survive *across navigations* — revisiting an origin skips the DNS+TCP+TLS
    // round-trips. The caller owns its lifetime; this method neither disposes it
    // nor hands it to the returned page. When null, a throwaway client is created
    // for this page load and bound to the returned LaidOutPage (disposed with it).
    //
    // onFirstPaint: optional progressive-render sink (interactive path). When
    // supplied and the page has scripts, only render-blocking scripts run before
    // first paint; the laid-out page is handed to this callback, then the async
    // (deferred) scripts settle and — only if they mutated the DOM — a reflowed
    // successor page is returned. When null, all scripts run before the single
    // returned page (snapshot semantics, e.g. the headless/PNG callers).
    public async Task<Result<LaidOutPage, RenderError>> LayoutPageAsync(
        string url, RenderOptions options, CancellationToken ct = default,
        StarlingHttpClient? sharedHttp = null,
        Action<LaidOutPage>? onFirstPaint = null)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(options);

        StarlingTelemetry.Counter("engine.page_layout", 1);
        using var _ = StarlingTelemetry.Span("engine", $"layout {url}");
        Activity.Current?.SetTag("http.url", url);
        Activity.Current?.SetTag("viewport.w", options.Viewport.Width);
        Activity.Current?.SetTag("viewport.h", options.Viewport.Height);

        // Wall-clock budget for the whole page-load critical path. Logged at
        // each phase boundary so the structured-logs view shows where time
        // goes between request start and onFirstPaint without needing the
        // trace tree. All elapsed values are ms since this stopwatch started.
        var pageSw = System.Diagnostics.Stopwatch.StartNew();

        var parsed = UrlParser.Parse(url);
        if (parsed.IsErr)
        {
            return Result<LaidOutPage, RenderError>.Err(new RenderError($"URL parse failed: {parsed.Error}"));
        }

        var u = parsed.Value;

        // One HTTP client (one connection pool / H2 manager / DNS cache) for the
        // whole page load: the document fetch and every resource fetch
        // (css/js/fonts/images) share it, so same-origin requests reuse a live
        // HTTP/2 connection (or keep-alive H1 transport) instead of re-doing
        // DNS+TCP+TLS per resource. A caller-supplied sharedHttp (interactive
        // session) is reused across navigations and is NOT owned here; otherwise
        // we mint a throwaway client, hand it to the returned LaidOutPage, and
        // the finally below disposes it on every path that doesn't hand it over.
        var http = sharedHttp ?? _httpFactory();
        var ownsHttp = sharedHttp is null;
        var httpHandedToPage = false;
        try
        {
            string html;
            Starling.Net.Http.ConnectionSecurity? security = null;
            try
            {
                if (u.IsFile)
                {
                    var path = u.ToFileSystemPath();
                    if (!File.Exists(path))
                    {
                        return Result<LaidOutPage, RenderError>.Err(new RenderError($"File not found: {path}"));
                    }

                    html = File.ReadAllText(path);
                }
                else if (u.IsHttp || u.IsHttps)
                {
                    Result<(string Html, StarlingUrl FinalUrl, Starling.Net.Http.ConnectionSecurity? Security), RenderError> fetched;
                    fetched = await FetchHtmlAsync(u, http, ct).ConfigureAwait(false);
                    if (fetched.IsErr)
                    {
                        return Result<LaidOutPage, RenderError>.Err(fetched.Error);
                    }

                    html = fetched.Value.Html;
                    security = fetched.Value.Security;
                    // Use the post-redirect URL as the base for relative resource
                    // resolution. See FetchHtmlAsync docstring.
                    u = fetched.Value.FinalUrl;
                    StarlingEngineLog.PhaseTimestamp(_log, "fetch_html", pageSw.ElapsedMilliseconds, html.Length);
                }
                else
                {
                    return Result<LaidOutPage, RenderError>.Err(new RenderError($"Unsupported scheme '{u.Scheme}'."));
                }
            }
            catch (IOException ex)
            {
                return Result<LaidOutPage, RenderError>.Err(new RenderError(ex.Message));
            }

            // Scripting flag ENABLED — the engine executes page JS, so <noscript>
            // contents must parse as inert raw text (WHATWG HTML §13.2.6.4.4).
            HtmlBackendSelector.EnsureInstalled();
            var doc = Html.HtmlParser.Parse(html, scriptingEnabled: true);
            StarlingEngineLog.PhaseDone(_log, "html_parsed", pageSw.ElapsedMilliseconds);

            // Page resources outlive this method — the caller's LaidOutPage owns
            // and disposes them (and, when minted here, the http client). On any
            // path that doesn't return Ok we dispose here so callers don't have
            // to. Once progressive first paint hands a page to the caller, the
            // displayed page owns these resources — the catch must not free them.
            var images = new ImageFetcher(_loggerFactory, http);
            var stylesheets = new StylesheetFetcher(_loggerFactory, http);
            var webFonts = new FontFaceRegistry();
            // The page's shared scroll store (browser-plan/scroll-model.md):
            // every layout pass for this document measures scrollports +
            // scrollable overflow into it, and the LaidOutPage hands it to
            // the shells / bindings as the single source of scroll truth.
            var scrollState = new Starling.Layout.Scroll.ScrollStateStore();
            var resourcesHandedToPage = false;

            // The ScriptFetcher must outlive the critical phase in progressive
            // mode (the deferred phase reads its scripts and the dynamic runner
            // fetches through it), so it can't be a single `using`. Dispose it
            // idempotently once the deferred phase finishes (or on the way out).
            var scripts = new ScriptFetcher(_loggerFactory, http);
            var scriptsDisposed = false;
            void DisposeScripts()
            {
                if (scriptsDisposed)
                {
                    return;
                }

                scriptsDisposed = true;
                scripts.Dispose();
            }

            try
            {
                // Snapshot callers keep the old all-resources-before-paint
                // semantics. Interactive callers split the critical path:
                // blocking scripts and CSS/fonts can delay first paint, but
                // images and defer/async/module scripts settle after the first
                // visible frame.
                var hasScripts = ScriptFetcher.HasRunnableScripts(doc);
                var interactive = onFirstPaint is not null && hasScripts;
                var scriptsFetch = interactive
                    ? scripts.FetchBlockingAsync(doc, baseUrl: u, ct)
                    : scripts.FetchAllAsync(doc, baseUrl: u, ct);

                // Fonts depend on the @font-face rules in the stylesheets, not on
                // the images — chain the font fetch onto the stylesheet fetch and
                // run it alongside the image fetch so web fonts start as soon as
                // the sheets land. (Mirrors RenderAsync.)
                using (var fontFaceFetcher = new FontFaceFetcher(_loggerFactory, http))
                {
                    async Task FetchSheetsThenFontsAsync()
                    {
                        await stylesheets.FetchAllAsync(doc, baseUrl: u, ct).ConfigureAwait(false);
                        await fontFaceFetcher
                            .FetchAllAsync(EnumerateAuthorSheets(doc, u, stylesheets), webFonts, ct)
                            .ConfigureAwait(false);
                    }

                    if (interactive)
                    {
                        await Task.WhenAll(
                            scriptsFetch,
                            FetchSheetsThenFontsAsync()
                        ).ConfigureAwait(false);
                        StarlingEngineLog.PhaseDone(_log, "render_blocking_resources", pageSw.ElapsedMilliseconds);
                    }
                    else
                    {
                        await Task.WhenAll(
                            scriptsFetch,
                            images.FetchAllAsync(doc, baseUrl: u, options.Viewport.Width, options.FontSize, ct),
                            FetchSheetsThenFontsAsync()
                        ).ConfigureAwait(false);
                        StarlingEngineLog.PhaseDone(_log, "subresources", pageSw.ElapsedMilliseconds);
                        StarlingEngineLog.PhaseDone(_log, "scripts_fetched", pageSw.ElapsedMilliseconds);
                    }

                    var viewport = new LayoutSize(options.Viewport.Width, options.Viewport.Height);

                    // Lazy pre-script layout — see RenderAsync for rationale. The
                    // trigger string identifies which JS read forced the layout
                    // (offsetWidth / getBoundingClientRect / getComputedStyle:foo)
                    // and is attached as a tag on the emitted span so the trace
                    // identifies the culprit script's read without needing a dump.
                    (Starling.Layout.Box.BlockBox Root, Starling.Css.Cascade.StyleEngine Style) Prelayout(string? trigger)
                    {
                        using (StarlingTelemetry.Span("engine", "prelayout_for_js"))
                        {
                            if (trigger is not null)
                            {
                                System.Diagnostics.Activity.Current?.SetTag("layout.trigger", trigger);
                                // Also emit as a log so the Aspire structured-logs
                                // API surfaces it — span attributes aren't returned
                                // by list_trace_structured_logs, and a forced
                                // prelayout is a notable event (~200 ms per
                                // occurrence on a real page) worth Info severity.
                                StarlingEngineLog.PrelayoutTrigger(_log, trigger);
                            }
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            var result = _painter.LayoutDocumentWithStyle(
                                doc, viewport, options.FontSize, images, stylesheets.Resolve, webFonts,
                                colorScheme: options.PreferredColorScheme, ct: ct, scrollState: scrollState);
                            sw.Stop();
                            // Surface wall time alongside the trigger so the
                            // structured-logs view shows where forced-reflow time
                            // is going without needing to click the trace.
                            StarlingEngineLog.PrelayoutDone(_log, trigger ?? "<no-trigger>", sw.ElapsedMilliseconds);
                            return result;
                        }
                    }

                    // Cheap cascade-only path for getComputedStyle reads on
                    // purely-cascaded properties — see RenderAsync.
                    Starling.Css.Cascade.StyleEngine BuildCascadeOnly()
                        => _painter.BuildStyleEngine(
                            doc, viewport, options.FontSize, stylesheets.Resolve,
                            options.PreferredColorScheme);

                    // Re-fetch resources a script run injected (new <img> / <link>),
                    // plus CSS background images, before laying out for paint.
                    async Task FetchInjectedAndBackgroundsAsync()
                    {
                        await Task.WhenAll(
                            images.FetchAllAsync(doc, baseUrl: u, options.Viewport.Width, options.FontSize, ct),
                            stylesheets.FetchAllAsync(doc, baseUrl: u, ct)
                        ).ConfigureAwait(false);
                        await images
                            .FetchBackgroundsAsync(EnumerateAuthorSheets(doc, u, stylesheets), u, ct)
                            .ConfigureAwait(false);
                    }

                    // Lay out the current DOM for paint and build the owning page.
                    // Only hand the client to the page when we own it; a shared
                    // session client outlives the page and is disposed by the caller.
                    // When <paramref name="reuseHost"/> already holds a layout that
                    // matches the current mutation version (script forced a
                    // prelayout AND nothing has mutated since), reuse it — saves
                    // ~200 ms on real pages by skipping the third full layout pass.
                    LaidOutPage BuildPage(BoxLayoutHost? reuseHost = null)
                    {
                        Starling.Layout.Box.BlockBox root;
                        Starling.Css.Cascade.StyleEngine style;
                        if (reuseHost is { HasLayout: true } h
                            && h.LaidOutVersion == doc.LayoutInvalidationVersion)
                        {
                            (root, style) = h.Materialized;
                        }
                        else
                        {
                            (root, style) = _painter.LayoutDocumentWithStyle(
                                doc, viewport, options.FontSize, images, stylesheets.Resolve, webFonts,
                                colorScheme: options.PreferredColorScheme, ct: ct, scrollState: scrollState);
                        }
                        return new LaidOutPage(
                            root, doc, style, viewport, url, ExtractTitle(doc), images, stylesheets, webFonts,
                            options.FontSize, security, ownsHttp ? http : null,
                            scrollState: scrollState);
                    }

                    // ---- Progressive path: run only render-blocking scripts, paint,
                    // then settle deferred (async) scripts and reflow only if they
                    // changed the DOM. Used by the interactive shell. ----
                    if (onFirstPaint is not null && hasScripts)
                    {
                        var progressiveHost = new BoxLayoutHost(doc, Prelayout, BuildCascadeOnly, scrollState);
                        var session = BeginScripts(doc, u, scripts, progressiveHost, viewport, ct);
                        var sessionEnded = false;
                        void EndSessionOnce() { if (!sessionEnded) { sessionEnded = true; EndScripts(session); } }
                        try
                        {
                            using (var critSpan = StarlingTelemetry.Span("engine", "run_scripts.critical"))
                            {
                                Activity.Current?.SetTag("script.count", scripts.Scripts.Count);
                                Activity.Current?.SetTag("script.module_count", scripts.ModuleScripts.Count);
                                var critSw = System.Diagnostics.Stopwatch.StartNew();
                                RunCriticalScripts(session, deferAsync: true, includeParserDeferred: false, ct);
                                critSw.Stop();
                                StarlingEngineLog.CriticalScriptsDone(_log, critSw.ElapsedMilliseconds,
                                    scripts.Scripts.Count, scripts.ModuleScripts.Count,
                                    progressiveHost.RelayoutCount, progressiveHost.FreshHits,
                                    progressiveHost.CascadeOnlyHits);
                            }

                            // First paint: lay out the post-critical DOM and hand it to
                            // the caller. From here the displayed page owns the shared
                            // resources + http client, so the catch must not free them.
                            var injSw = System.Diagnostics.Stopwatch.StartNew();
                            await FetchSheetsThenFontsAsync().ConfigureAwait(false);
                            injSw.Stop();
                            var paintSw = System.Diagnostics.Stopwatch.StartNew();
                            // Reuse the JS layout host's materialized layout if its
                            // mutation version still matches — avoids a third full
                            // layout pass when scripts already triggered one.
                            var page1 = BuildPage(progressiveHost);
                            paintSw.Stop();
                            StarlingEngineLog.PostCriticalTiming(_log, injSw.ElapsedMilliseconds,
                                paintSw.ElapsedMilliseconds,
                                progressiveHost.HasLayout && progressiveHost.LaidOutVersion == doc.LayoutInvalidationVersion);
                            httpHandedToPage = ownsHttp;
                            resourcesHandedToPage = true;

                            var versionAtPaint = doc.MutationVersion;
                            var imagesAtPaint = images.LoadedCount;
                            var sheetsAtPaint = stylesheets.LoadedCount;
                            onFirstPaint(page1);

                            var deferredFetchSw = System.Diagnostics.Stopwatch.StartNew();
                            await Task.WhenAll(
                                scripts.FetchDeferredAsync(doc, baseUrl: u, ct),
                                images.FetchAllAsync(doc, baseUrl: u, options.Viewport.Width, options.FontSize, ct)
                            ).ConfigureAwait(false);
                            await images
                                .FetchBackgroundsAsync(EnumerateAuthorSheets(doc, u, stylesheets), u, ct)
                                .ConfigureAwait(false);
                            StarlingEngineLog.PhaseElapsed(_log, "deferred_resources", pageSw.ElapsedMilliseconds, deferredFetchSw.ElapsedMilliseconds);

                            // Deferred scripts settle here, after first paint.
                            using (StarlingTelemetry.Span("engine", "run_scripts.deferred"))
                            {
                                await RunDeferredScriptsAsync(session, deferAsync: true, runParserDeferred: true, ct).ConfigureAwait(false);
                            }

                            // Keep the realm LIVE past load: instead of tearing the
                            // session down, hand it to the returned page as a
                            // PageScripting so the shell can dispatch DOM events and
                            // pump timers/rAF/fetch interactively. The page owns it
                            // now (disposes the JS http client + fetcher + DOM hooks),
                            // so we do NOT EndScripts/DisposeScripts on this path. All
                            // fallible work runs first; the hand-off (which flips the
                            // teardown guards) happens last so any throw before it
                            // still tears the session down via the catch.
                            LaidOutPage HandOff(LaidOutPage page)
                            {
                                var live = new PageScripting(
                                    session.Session, session.Http, session.Fetcher, doc, scrollState);
                                sessionEnded = true;  // page.Dispose() tears the session down now
                                scriptsDisposed = true;
                                page.AttachScripting(live);
                                return page;
                            }

                            // Common case (analytics/beacons): deferred work touched no
                            // DOM and no late layout-affecting resources arrived, so
                            // page1 is still correct — return it (now live).
                            if (doc.MutationVersion == versionAtPaint
                                && images.LoadedCount == imagesAtPaint
                                && stylesheets.LoadedCount == sheetsAtPaint)
                            {
                                return Result<LaidOutPage, RenderError>.Ok(HandOff(page1));
                            }

                            // Deferred scripts mutated the DOM or late resources
                            // arrived: re-fetch what they injected and reflow into a
                            // successor. Relayout transfers page1's shared resources
                            // to it and marks page1 inert, so the caller can dispose
                            // page1 safely.
                            await FetchInjectedAndBackgroundsAsync().ConfigureAwait(false);
                            var (root2, style2) = _painter.LayoutDocumentWithStyle(
                                doc, viewport, options.FontSize, images, stylesheets.Resolve, webFonts,
                                colorScheme: options.PreferredColorScheme, ct: ct, scrollState: scrollState);
                            return Result<LaidOutPage, RenderError>.Ok(HandOff(page1.Relayout(root2, style2, viewport)));
                        }
                        catch
                        {
                            // After first paint the caller owns the resources; only
                            // tear the session/fetcher down and propagate (the outer
                            // catch skips resource disposal via resourcesHandedToPage).
                            EndSessionOnce();
                            DisposeScripts();
                            throw;
                        }
                    }

                    // ---- Non-progressive path: run everything before painting the
                    // single returned page (snapshot semantics / no callback). ----
                    if (hasScripts)
                    {
                        await RunScriptsAsync(doc, u, scripts, new BoxLayoutHost(doc, Prelayout, BuildCascadeOnly, scrollState), viewport, ct).ConfigureAwait(false);
                        DisposeScripts();
                        await FetchInjectedAndBackgroundsAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        DisposeScripts();
                        await images
                            .FetchBackgroundsAsync(EnumerateAuthorSheets(doc, u, stylesheets), u, ct)
                            .ConfigureAwait(false);
                    }

                    var page = BuildPage();
                    httpHandedToPage = ownsHttp;
                    resourcesHandedToPage = true;
                    return Result<LaidOutPage, RenderError>.Ok(page);
                }
            }
            catch
            {
                // The ScriptFetcher is never owned by the page; always dispose it.
                DisposeScripts();
                // Resources become the displayed page's once first paint hands
                // them over — don't free them out from under it.
                if (!resourcesHandedToPage)
                {
                    images.Dispose();
                    stylesheets.Dispose();
                    webFonts.Dispose();
                }
                throw;
            }
        }
        finally
        {
            // Dispose only a client we minted here and didn't hand to the page.
            // A caller-supplied shared client is never disposed here.
            if (ownsHttp && !httpHandedToPage)
            {
                http.Dispose();
            }
        }
    }

    /// <summary>
    /// Re-lays-out an existing <paramref name="page"/> at a new viewport size
    /// (e.g. after a window resize) <em>without</em> re-fetching: the page's
    /// already-parsed <see cref="LaidOutPage.Document"/> and resource resolvers
    /// are reused, only the box tree and cascade are rebuilt against the new
    /// <paramref name="options"/> viewport. Returns a fresh <see cref="LaidOutPage"/>
    /// that owns the shared resources; the caller must show it and dispose the
    /// old one. Synchronous — runs on the caller's thread.
    /// </summary>
    /// <remarks>
    /// This reflows the post-script DOM as it currently stands; it does not
    /// re-run page scripts, fire <c>resize</c> events, or re-evaluate
    /// <c>srcset</c> against the new width. Those need a full navigation.
    /// </remarks>
    public LaidOutPage RelayoutPage(LaidOutPage page, RenderOptions options)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(options);

        StarlingTelemetry.Counter("engine.page_relayout", 1);
        using var _ = StarlingTelemetry.Span("engine", $"relayout {page.Url}");
        Activity.Current?.SetTag("viewport.w", options.Viewport.Width);
        Activity.Current?.SetTag("viewport.h", options.Viewport.Height);

        var viewport = new LayoutSize(options.Viewport.Width, options.Viewport.Height);

        // Reuse the page's persistent StyleEngine and a retained box tree,
        // recomputing only the subtrees this frame's mutations touched. The
        // session detects a viewport change and falls back to a full rebuild, so
        // resize stays correct — but the cascade reads the viewport off the
        // MediaContext, so refresh it first.
        page.Document.RecordLayoutMutations = true;
        page.Style.MediaContext = page.Style.MediaContext with
        {
            ColorScheme = options.PreferredColorScheme,
            ViewportWidthPx = viewport.Width,
            ViewportHeightPx = viewport.Height,
        };
        var session = page.GetOrCreateLayoutSession(_loggerFactory);
        var incRoot = _painter.LayoutDocumentIncremental(session, page.Document, viewport, page.WebFonts);
        return page.Relayout(incRoot, page.Style, viewport);
    }

    /// <summary>
    /// Re-paint an already-laid-out page at frame timestamp <paramref name="nowMs"/>.
    /// Ticks the page's animation and transition engines forward, then runs the
    /// painter with the timestamp threaded through the cascade so any in-flight
    /// CSS animations and transitions sample their current value into the
    /// returned bitmap. The page's box tree is rebuilt from scratch each call —
    /// a fresh box-tree builder with a new per-build cascade cache — so the same
    /// <see cref="LaidOutPage"/> can drive an arbitrary frame sequence at the
    /// cost of a full relayout per frame. (Incremental fragment reuse is the
    /// planned replacement.) Callers typically loop calling this once per rAF
    /// tick.
    /// </summary>
    public Starling.Common.Image.RenderedBitmap RenderFrame(LaidOutPage page, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(page);

        PrepareAnimationFrame(page, nowMs);

        return _painter.RenderWithStyle(
            page.Document,
            page.Style,
            new LayoutSize(page.Viewport.Width, page.Viewport.Height),
            page.Images,
            page.WebFonts,
            nowMs: (double)nowMs,
            scrollState: page.ScrollState);
    }

    public CompositedPageRenderer CreateCompositedRenderer(LaidOutPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        var renderer = _painter.CreateCompositedRenderer(page.WebFonts);
        renderer.ResetForNavigation();
        return renderer;
    }

    public Starling.Common.Image.RenderedBitmap RenderFrameGpu(
        LaidOutPage page,
        long nowMs,
        CompositedPageRenderer? renderer = null,
        bool fullPage = false)
    {
        ArgumentNullException.ThrowIfNull(page);

        var ownsRenderer = renderer is null;
        renderer ??= CreateCompositedRenderer(page);
        try
        {
            return RenderFrameGpuCore(page, nowMs, renderer, fullPage);
        }
        finally
        {
            if (ownsRenderer)
            {
                renderer.Dispose();
            }
        }
    }

    /// <summary>
    /// Advance the page's animation/transition clocks to <paramref name="nowMs"/>,
    /// priming declarative animations if this box tree has not been primed yet,
    /// so a subsequent render samples the animated values. Public so the live GUI
    /// animation loop can drive the same path used by <see cref="RenderFrame"/>.
    /// Script (WAAPI) animations already live in the document's persistent
    /// <see cref="Css.Animations.AnimationEngine"/>, so there is nothing to
    /// re-import here.
    /// </summary>
    public void PrepareAnimationFrame(LaidOutPage page, long nowMs)
    {
        ArgumentNullException.ThrowIfNull(page);
        PrimeDeclarativeAnimations(page);
        page.Style.AnimationEngine.Tick(nowMs);
        page.Style.TransitionEngine.Tick(nowMs);
        // Dispatch the animation/transition DOM events the engines queued —
        // boundary facts from the ticks above plus transitionrun facts the
        // previous frame's style pass recorded. This runs before this frame's
        // style/paint work, matching the HTML event-loop's "update animations
        // and send events" step, and never from inside Compose (listeners
        // mutate the DOM). JS listeners are bridged onto the host EventTarget,
        // so they run on this thread — the same one that pumps rAF callbacks.
        Css.Animations.AnimationEventDispatcher.DispatchPending(
            page.Style.AnimationEngine, page.Style.TransitionEngine);
    }

    /// <summary>
    /// Register the page's declarative <c>@keyframes</c> animations into the
    /// document's <see cref="Css.Animations.AnimationEngine"/> from each element's
    /// static <c>animation-*</c> cascade (read off the laid-out box tree).
    /// Without this the engine only learns about declarative animations during a
    /// full <c>RenderWithStyle</c> cascade — which the GUI's box-tree renderer
    /// never runs — so they would never start in the live window. Primed once
    /// per laid-out box tree (rebuilt each layout); a relayout re-primes the new
    /// tree, and <see cref="Css.Animations.AnimationEngine.OnAnimationsCascaded"/>
    /// matches instances by name so playback is preserved.
    /// </summary>
    private void PrimeDeclarativeAnimations(LaidOutPage page)
    {
        var root = page.Root;
        if (_primedTrees.TryGetValue(root, out _))
        {
            return;
        }

        _primedTrees.Add(root, this);
        PrimeBox(root, page.Style.AnimationEngine);

        static void PrimeBox(Starling.Layout.Box.Box box, Css.Animations.AnimationEngine engine)
        {
            if (box.Element is { } el && box.Style is { } style)
            {
                var decls = Css.Animations.AnimationCompositor.BuildDeclarations(style);
                if (decls.Count > 0)
                {
                    engine.OnAnimationsCascaded(el, decls);
                }
            }
            foreach (var child in box.Children)
            {
                PrimeBox(child, engine);
            }
        }
    }

    /// <summary>True when the page has any in-flight animation or transition
    /// (declarative or script). The live GUI loop uses this to decide whether to
    /// keep repainting frames.</summary>
    public bool HasActiveAnimations(LaidOutPage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        PrimeDeclarativeAnimations(page);
        return page.Style.AnimationEngine.HasInFlight || page.Style.TransitionEngine.ActiveCount > 0;
    }

    /// <summary>
    /// Repaint <paramref name="page"/> to a PNG at <paramref name="outputPath"/>
    /// for out-of-band capture (the GUI's <c>browser_screenshot</c> tool). Like
    /// <see cref="RenderFrame"/> it ticks the page's animation/transition clocks
    /// to <paramref name="nowMs"/> and repaints the current live DOM, but when
    /// <paramref name="fullPage"/> is set it renders at the full laid-out document
    /// height (clamped) rather than the window viewport, so the whole scroll
    /// extent lands in the image. The PNG encode reuses the same ImageSharp wrap
    /// as <see cref="RenderAsync"/>.
    /// </summary>
    public RenderOutcome CaptureToPng(
        LaidOutPage page, string outputPath, long nowMs = 0, bool fullPage = true)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(outputPath);

        using var _ = StarlingTelemetry.Span("engine", $"capture_png {page.Url} -> {outputPath}");

        var height = fullPage
            ? Math.Clamp(Math.Ceiling(page.DocumentHeight), page.Viewport.Height, 30000)
            : page.Viewport.Height;
        var viewport = new LayoutSize(page.Viewport.Width, height);

        PrepareAnimationFrame(page, nowMs);

        // The store rides along only when the capture viewport matches the
        // page layout viewport: a full-page capture lays out at the document
        // height, and letting that pass re-record root geometry would clamp
        // the live root offset against the wrong scrollport.
        using var bitmap = _painter.RenderWithStyle(
            page.Document, page.Style, viewport, page.Images, page.WebFonts, nowMs: (double)nowMs,
            scrollState: fullPage ? null : page.ScrollState);

        EnsureOutputDirectory(outputPath);
        using var image = Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(
            bitmap.Rgba, bitmap.Width, bitmap.Height);
        image.SaveAsPng(outputPath);

        return new RenderOutcome(outputPath, bitmap.Width, bitmap.Height, DisplayText: string.Empty);
    }

    public RenderOutcome CaptureToPngGpu(
        LaidOutPage page, string outputPath, long nowMs = 0, bool fullPage = false)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(outputPath);

        using var _ = StarlingTelemetry.Span("engine", $"capture_png.gpu {page.Url} -> {outputPath}");
        using var renderer = CreateCompositedRenderer(page);
        using var bitmap = RenderFrameGpu(page, nowMs, renderer, fullPage);

        EnsureOutputDirectory(outputPath);
        using var image = Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(
            bitmap.Rgba, bitmap.Width, bitmap.Height);
        image.SaveAsPng(outputPath);

        return new RenderOutcome(outputPath, bitmap.Width, bitmap.Height, DisplayText: string.Empty);
    }

    private Starling.Common.Image.RenderedBitmap RenderFrameGpuCore(
        LaidOutPage page,
        long nowMs,
        CompositedPageRenderer renderer,
        bool fullPage)
    {
        PrepareAnimationFrame(page, nowMs);

        var height = fullPage
            ? Math.Clamp(Math.Ceiling(page.DocumentHeight), page.Viewport.Height, 30000)
            : page.Viewport.Height;
        var viewport = new LayoutRect(0, 0, page.Viewport.Width, height);
        var animating = HasActiveAnimations(page);
        Func<LayoutBox, ComputedStyle?>? styleOverride = animating
            ? box => AnimatedStyle(page, box, nowMs)
            : null;

        return renderer.Render(
            page.Root,
            viewport,
            scale: 1.0f,
            styleOverride,
            page.ImageResolver,
            box => IsElementAnimatingLayerRoot(page, box),
            scrollOffsets: page.ScrollOffsetLookup,
            stickyShifts: page.StickyShiftLookup);
    }

    private static bool IsElementAnimatingLayerRoot(LaidOutPage page, LayoutBox box)
    {
        if (box.Element is not { } el)
        {
            return false;
        }

        if (page.Document.WasRecentlyMutated(el))
        {
            return true;
        }

        foreach (var _ in page.Style.AnimationEngine.ActiveProperties(el))
        {
            return true;
        }

        foreach (var _ in page.Style.TransitionEngine.ActiveProperties(el))
        {
            return true;
        }

        return false;
    }

    private static ComputedStyle? AnimatedStyle(LaidOutPage page, LayoutBox box, long nowMs)
    {
        if (box.Element is not { } el)
        {
            return null;
        }

        foreach (var _ in page.Style.AnimationEngine.ActiveProperties(el))
        {
            return page.Style.ComputeWithAnimations(el, nowMs);
        }

        foreach (var _ in page.Style.TransitionEngine.ActiveProperties(el))
        {
            return page.Style.ComputeWithAnimations(el, nowMs);
        }

        return null;
    }

    /// <summary>
    /// Stand up the active JS backend session, run every collected script
    /// against the shared <paramref name="document"/>, then drain microtasks and
    /// fire <c>DOMContentLoaded</c> + <c>load</c>. Script errors are logged
    /// through the console sink rather than bubbled — one bad bundle should not
    /// abort the render.
    /// </summary>
    /// <remarks>
    /// The engine keeps script ordering/selection/dedup and the pump-loop shape;
    /// all JS-touching work goes through <see cref="IScriptSession"/>, so the
    /// active engine is swappable via <c>STARLING_JS_ENGINE</c>. Timers and
    /// <c>requestAnimationFrame</c> ride a backend-owned simulated clock that
    /// <see cref="PumpToQuiescenceAsync"/> advances one
    /// <see cref="IScriptSession.PumpOnce"/> at a time, so chained
    /// <c>setTimeout</c> / rAF bootstrappers settle within a wall-clock budget.
    /// </remarks>
    private async Task RunScriptsAsync(
        Document document, StarlingUrl baseUrl, ScriptFetcher scriptFetcher,
        ILayoutHost? layoutHost, LayoutSize viewport, CancellationToken ct)
    {
        // One span over the whole execution phase — compile + run, the
        // DOMContentLoaded/load events, and the microtask/timer/dynamic-script
        // pumping that follows. On script-heavy pages this is usually the
        // dominant *non-network* cost, and without this span it shows up in
        // traces only as an unattributed gap between fetch_scripts and layout.
        //
        // The PNG/headless path runs both phases back-to-back (deferAsync:false
        // preserves the historical ordering — async classic scripts run before
        // DOMContentLoaded). The interactive path splits the phases around first
        // paint via the Begin/RunCritical/RunDeferred/End primitives below.
        using var span = StarlingTelemetry.Span("engine", "run_scripts");
        Activity.Current?.SetTag("script.count", scriptFetcher.Scripts.Count);
        Activity.Current?.SetTag("script.module_count", scriptFetcher.ModuleScripts.Count);

        var session = BeginScripts(document, baseUrl, scriptFetcher, layoutHost, viewport, ct);
        try
        {
            RunCriticalScripts(session, deferAsync: false, includeParserDeferred: true, ct);
            await RunDeferredScriptsAsync(session, deferAsync: false, runParserDeferred: false, ct).ConfigureAwait(false);
        }
        finally
        {
            EndScripts(session);
        }
    }

    /// <summary>
    /// Holds the live JS state shared across the critical and deferred script
    /// phases: the realm/VM, the simulated event loop, the dynamic-script runner,
    /// the dedup set, and the JS-owned HTTP client. Created by
    /// <see cref="BeginScripts"/>, torn down by <see cref="EndScripts"/>.
    /// </summary>
    private sealed class ScriptSession
    {
        // The JS-engine-neutral session (Starling.Js or Jint). Owns the realm,
        // the simulated loop, the dynamic-script runner, and the src/inject
        // hooks — all JS-touching work goes through it.
        public required IScriptSession Session { get; init; }
        public required HashSet<Element> Executed { get; init; }
        public required StarlingHttpClient Http { get; init; }
        public required Document Document { get; init; }
        public required StarlingUrl BaseUrl { get; init; }
        public required ScriptFetcher Fetcher { get; init; }
        public int ConsoleErrors;
    }

    /// <summary>
    /// Stand up the JS realm, window/timer/rAF bindings, the tree-mutation and
    /// <c>src</c>-set hooks, and the dynamic-script runner — everything needed to
    /// run a page's scripts. The returned session must be torn down with
    /// <see cref="EndScripts"/> (it owns an HTTP client and installed hooks).
    /// </summary>
    private ScriptSession BeginScripts(
        Document document, StarlingUrl baseUrl, ScriptFetcher scriptFetcher,
        ILayoutHost? layoutHost, LayoutSize viewport, CancellationToken ct)
    {
        var http = _httpFactory();

        // Stand up the active JS backend (Starling.Js by default, or Jint when
        // STARLING_JS_ENGINE=jint). The session owns the realm, the simulated
        // loop, the dynamic-script runner, and the src/inject hooks; the engine
        // keeps only script ordering/selection/dedup and the pump-loop shape.
        var inner = JsEngineSelector.Factory.CreateSession(new ScriptSessionOptions(
            Document: document,
            BaseUrl: baseUrl,
            // The dynamic-script runner + ES module loader inside the backend
            // fetch through the same ScriptFetcher cache + scheme handling the
            // parser batch used, so a bundle requested both ways is fetched once.
            Fetcher: (url, token) => scriptFetcher.FetchSourceAsync(url, token),
            Http: http,
            LayoutHost: layoutHost,
            LoggerFactory: _loggerFactory)
        {
            // Register element.animate() animations straight into the document's
            // persistent AnimationEngine (held by its timeline), so they outlive
            // the per-layout StyleEngine without a per-frame re-import.
            AnimationHost = new EngineAnimationHost(
                _painter.GetAnimationTimeline(document).Animations),
            ViewportWidth = (int)viewport.Width,
            ViewportHeight = (int)viewport.Height,
            // Same navigation ct that drives the HTTP/pump path: the backend
            // observes it from inside the interpreter so Stop interrupts a
            // synchronous script (e.g. a tight `while(true)` or heavy reflow
            // driven from JS) instead of waiting for it to return.
            AbortToken = ct,
        });

        var session = new ScriptSession
        {
            Session = inner,
            Document = document,
            BaseUrl = baseUrl,
            Fetcher = scriptFetcher,
            Executed = new HashSet<Element>(ReferenceEqualityComparer.Instance),
            Http = http,
        };

        // Route the backend console through the dedicated JS console logger,
        // preserving the page's console level so DevTools' ConsolePanel can
        // colour errors red and its level-filter pills match. The in-memory
        // sink is opened down to Debug for this category in AddStarlingTelemetry
        // so console.debug isn't dropped by the default Information floor;
        // console.trace folds into Debug (no trace pill).
        inner.ConsoleSink = (level, message) =>
        {
            var logLevel = level switch
            {
                ConsoleLevel.Error => LogLevel.Error,
                ConsoleLevel.Warn => LogLevel.Warning,
                ConsoleLevel.Debug or ConsoleLevel.Trace => LogLevel.Debug,
                _ => LogLevel.Information,
            };
            _jsConsoleLog.Log(logLevel, "[{Level}] {Message}", level, message);
            if (level == ConsoleLevel.Error)
            {
                session.ConsoleErrors++;
            }
        };

        // Wire the runtime-injection hook: when JS appends a freshly created
        // <script> to the connected DOM, the backend runs it (or, for
        // script-inserted async / external scripts, defers it to the
        // dynamic-script pump) through the same compile+run path.
        document.NodeConnected = node => inner.OnScriptElementConnected(node);

        return session;
    }

    /// <summary>
    /// Render-blocking script phase: the ordered (non-async) classic scripts,
    /// the module scripts, then <c>DOMContentLoaded</c> — everything that must
    /// run before a correct first paint. With <paramref name="deferAsync"/> the
    /// <c>async</c> classic scripts are held back for <see cref="RunDeferredScriptsAsync"/>
    /// (so they no longer block first paint); otherwise they run here, preserving
    /// the historical pre-DOMContentLoaded ordering.
    /// </summary>
    private void RunCriticalScripts(
        ScriptSession s,
        bool deferAsync,
        bool includeParserDeferred,
        CancellationToken ct)
    {
        // Ordered scripts (neither async nor defer, then defer) run in document
        // order (HTML §4.12.1).
        RunOrderedScripts(s, includeParserDeferred, ct);
        if (!deferAsync)
        {
            RunAsyncScripts(s, ct);
        }

        // Module scripts run after the classic scripts, deferred and in document
        // order, before DOMContentLoaded.
        if (includeParserDeferred)
        {
            RunModuleScripts(s, ct);
        }

        // Mark every parser-batch script "already started" so a deferred loader's
        // later `src` write never re-runs a script that already executed. The
        // backend owns the started flag (HTML §4.12.1); the engine notifies it
        // per element since the run entry points are element-agnostic.
        MarkParserBatchScriptsStarted(s);

        // DOMContentLoaded — synchronous handlers see the parsed DOM.
        s.Session.FireDomContentLoaded();
    }

    /// <summary>
    /// Deferred script phase, run after first paint on the interactive path: the
    /// <c>async</c> classic scripts (when <paramref name="deferAsync"/>), then the
    /// async-work pump (fetch/XHR completions, microtasks, simulated timers/rAF,
    /// and src-triggered dynamic scripts), the <c>load</c> event, and a final
    /// pump. This is where analytics/beacon work and lazily-injected scripts
    /// settle.
    /// </summary>
    private async Task RunDeferredScriptsAsync(
        ScriptSession s,
        bool deferAsync,
        bool runParserDeferred,
        CancellationToken ct)
    {
        // DIAG (open-time investigation): time each sub-step of the deferred phase.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (runParserDeferred)
        {
            RunDeferredClassicScripts(s, ct);
            RunModuleScripts(s, ct);
            MarkParserBatchScriptsStarted(s);
        }

        var tParserDeferred = sw.ElapsedMilliseconds;
        if (deferAsync)
        {
            RunAsyncScripts(s, ct);
        }

        var tAsync = sw.ElapsedMilliseconds;

        // Pump in-flight async work AND src-triggered dynamic script fetches,
        // re-pumping after each settles. Sequential bundle loaders chain off
        // `load`, so this loop is what lets bundle #2..N run after #1.
        await PumpToQuiescenceAsync(s, ct).ConfigureAwait(false);
        var tPump1 = sw.ElapsedMilliseconds;

        // load event after subresources have settled, then one more drain pass
        // for listeners that schedule further work.
        s.Session.FireLoad();
        var tFireLoad = sw.ElapsedMilliseconds;
        await PumpToQuiescenceAsync(s, ct).ConfigureAwait(false);

        StarlingEngineLog.DeferredSummary(_log, tParserDeferred, tAsync - tParserDeferred,
            tPump1 - tAsync, tFireLoad - tPump1, sw.ElapsedMilliseconds - tFireLoad, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Detach the tree-mutation / <c>src</c>-set hooks so the document is inert
    /// again, dispose the JS-owned HTTP client, and surface any console-error
    /// count. Safe to call exactly once per <see cref="BeginScripts"/>.
    /// </summary>
    private void EndScripts(ScriptSession s)
    {
        s.Document.NodeConnected = null;
        // Disposing the session detaches the src-set hook so the document is
        // inert again (and releases any backend-held realm state).
        s.Session.Dispose();
        s.Http.Dispose();

        if (s.ConsoleErrors > 0)
        {
            StarlingTelemetry.Counter("engine.script.console_errors", s.ConsoleErrors);
            Activity.Current?.SetTag("script.console_errors", s.ConsoleErrors);
        }
    }

    /// <summary>Run the non-<c>async</c> classic scripts in document order: the
    /// scripts with neither attribute first, then the <c>defer</c> scripts, both
    /// in source order (HTML §4.12.1). Inline scripts are always
    /// <see cref="ScriptDisposition.None"/>.</summary>
    private void RunOrderedScripts(ScriptSession s, bool includeDefer, CancellationToken ct)
    {
        foreach (var script in s.Fetcher.Scripts)
        {
            if (script.Disposition == ScriptDisposition.None)
            {
                ExecuteScript(s, script, ct);
            }
        }
        if (!includeDefer)
        {
            return;
        }

        RunDeferredClassicScripts(s, ct);
    }

    private void RunDeferredClassicScripts(ScriptSession s, CancellationToken ct)
    {
        foreach (var script in s.Fetcher.Scripts)
        {
            if (script.Disposition == ScriptDisposition.Defer)
            {
                ExecuteScript(s, script, ct);
            }
        }
    }

    private static void MarkParserBatchScriptsStarted(ScriptSession s)
    {
        foreach (var sc in s.Fetcher.Scripts)
        {
            s.Session.MarkScriptStarted(sc.Element);
        }

        foreach (var sc in s.Fetcher.ModuleScripts)
        {
            s.Session.MarkScriptStarted(sc.Element);
        }
    }

    /// <summary>Run the <c>async</c> classic scripts. Order is unspecified
    /// (HTML §4.12.1 "as soon as it is available"); the headless engine has
    /// already fetched them, so it runs them in source order, which is one
    /// permitted ordering.</summary>
    private void RunAsyncScripts(ScriptSession s, CancellationToken ct)
    {
        foreach (var script in s.Fetcher.Scripts)
        {
            if (script.Disposition == ScriptDisposition.Async)
            {
                ExecuteScript(s, script, ct);
            }
        }
    }

    /// <summary>Run a single classic script through the active backend session,
    /// deduping on the source <see cref="Element"/> so a script can never execute
    /// twice. Failures are fail-soft (logged via diagnostics), matching the
    /// document-order batch behaviour.</summary>
    private void ExecuteScript(ScriptSession s, LoadedScript script, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!s.Executed.Add(script.Element))
        {
            return;
        }

        var label = script.IsInline ? "<inline>" : (script.BaseUrl?.ToString() ?? "<unknown>");
        try
        {
            s.Session.RunClassicScript(script.Source, label);
            StarlingTelemetry.Counter("engine.script.ok", 1);
        }
        catch (ScriptThrow ex)
        {
            StarlingTelemetry.Counter("engine.script.failed", 1);
            // The JS-side stack (when the backend produced one) rides in its own
            // structured field so it stays queryable instead of being mashed into
            // the message. "(no JS stack)" makes a missing stack explicit.
            StarlingEngineLog.UncaughtScriptError(_jsConsoleLog, label, ex.Message,
                string.IsNullOrEmpty(ex.JsStack) ? "(no JS stack)" : ex.JsStack);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            StarlingTelemetry.Counter("engine.script.failed", 1);
            StarlingEngineLog.ScriptRunFailure(_jsConsoleLog, label, ex.Message);
        }
    }

    /// <summary>
    /// Evaluate each <c>&lt;script type="module"&gt;</c> as the entry of its own
    /// ES module graph through the active backend session. The backend's module
    /// loader shares one module map across the page so a module imported by two
    /// entry scripts is fetched, linked and evaluated once. Errors are logged
    /// through the console sink (one bad module must not abort the render).
    /// </summary>
    private void RunModuleScripts(ScriptSession s, CancellationToken ct)
    {
        var moduleScripts = s.Fetcher.ModuleScripts;
        if (moduleScripts.Count == 0)
        {
            return;
        }

        // Module scripts run deferred, after the classic scripts, in document
        // order, before DOMContentLoaded. The backend owns the module loader;
        // the engine drives evaluation order and the dedup set. Each module is
        // evaluated synchronously here (block on the session's async entry) to
        // preserve the historical pre-DOMContentLoaded ordering.
        foreach (var script in moduleScripts)
        {
            ct.ThrowIfCancellationRequested();
            if (!s.Executed.Add(script.Element))
            {
                continue;
            }

            var label = script.IsInline ? "<inline module>" : (script.BaseUrl?.ToString() ?? "<unknown>");
            // Inline modules have no URL of their own; pass the document base so
            // the backend registers a synthetic inline entry (its imports resolve
            // against the document URL). External modules carry their src URL.
            var moduleUrl = script.IsInline ? s.BaseUrl : script.BaseUrl!;
            try
            {
                s.Session.RunModuleScriptAsync(moduleUrl, script.Source, ct).GetAwaiter().GetResult();
                StarlingTelemetry.Counter("engine.module.ok", 1);
            }
            catch (ScriptThrow ex)
            {
                StarlingTelemetry.Counter("engine.module.failed", 1);
                StarlingEngineLog.UncaughtModuleError(_jsConsoleLog, label, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                StarlingTelemetry.Counter("engine.module.failed", 1);
                StarlingEngineLog.ModuleRunFailure(_jsConsoleLog, label, ex.Message);
            }
        }
    }

    /// <summary>
    /// Drive the active <see cref="IScriptSession"/> to quiescence on every
    /// front the backend manages — microtask/promise jobs, simulated
    /// timers/rAF, and src-triggered dynamic scripts — by repeatedly calling
    /// <see cref="IScriptSession.PumpOnce"/>. Each call advances one front and
    /// reports whether any work remains; when it reports idle we still wait out
    /// a short wall-clock window, because off-thread fetch / XHR completions
    /// enqueue their resolve jobs asynchronously and need a slot to land before
    /// we declare the page settled. A generous wall-clock cap accommodates
    /// sequential network bundle chains without hanging a stuck page.
    /// A self-perpetuating <c>requestAnimationFrame</c> loop is the exception: it
    /// never reports idle, so it gets a small frame budget and is then handed to
    /// the live phase rather than holding navigation open for the full cap.
    /// </summary>
    private async Task PumpToQuiescenceAsync(ScriptSession s, CancellationToken ct)
    {
        // Hard wall-clock cap for the whole settle. Overridable via
        // STARLING_PUMP_MAX_MS for content-heavy SPAs whose data XHRs need longer than the default.
        var MaxMs = 8000;
        if (int.TryParse(Environment.GetEnvironmentVariable("STARLING_PUMP_MAX_MS"), out var capOverride) && capOverride > 0)
        {
            MaxMs = capOverride;
        }

        const int IdleMs = 60;    // consecutive idle window before declaring done

        // A self-rescheduling requestAnimationFrame loop never reports idle, so
        // without a bound the pump burns the entire wall-clock cap on every
        // animated page. We give rAF a frame budget, then stop waiting on it.
        //
        // A static/headless render has no live phase (PumpFrame) to continue the
        // animation, so the budget must be generous enough that typical entrance
        // animations — counters easing on performance.now() deltas, width/opacity
        // transitions of ~1–2s — reach their final state before the snapshot;
        // otherwise the page is captured mid-animation. At SimulatedStepMs per
        // frame this still bounds how long a truly endless rAF loop (game loop,
        // spinner) holds the navigation settle. Override with
        // STARLING_RAF_FRAME_BUDGET. Microtasks, timers, and dynamic-script
        // fetches always pump to true quiescence, independent of this budget.
        var RafFrameBudget = 40;
        if (int.TryParse(Environment.GetEnvironmentVariable("STARLING_RAF_FRAME_BUDGET"), out var rafBudgetOverride) && rafBudgetOverride > 0)
        {
            RafFrameBudget = rafBudgetOverride;
        }

        var wall = System.Diagnostics.Stopwatch.StartNew();
        var idle = System.Diagnostics.Stopwatch.StartNew();
        var rafFrames = 0;
        var iters = 0;   // DIAG (open-time investigation)

        while (wall.ElapsedMilliseconds < MaxMs)
        {
            ct.ThrowIfCancellationRequested();
            iters++;

            // When the only work left is a steady rAF loop that has already had
            // its frame budget, declare the page settled and let the live loop
            // drive the ongoing animation instead of blocking here.
            var rafOnly = s.Session.OnlyAnimationFramePending;
            if (rafOnly && rafFrames >= RafFrameBudget)
            {
                StarlingEngineLog.PumpSettled(_log, wall.ElapsedMilliseconds, iters, rafFrames, "rafBudget");
                return;
            }
            if (rafOnly)
            {
                rafFrames++;   // this PumpOnce advances one animation frame
            }

            if (s.Session.PumpOnce())
            {
                // Some front had pending work this iteration; reset the idle
                // window and keep pumping synchronously.
                idle.Restart();
                continue;
            }

            if (s.Session.HasPendingHostAsyncWork)
            {
                idle.Restart();
                await Task.Delay(20, ct).ConfigureAwait(false);
                continue;
            }

            // Nothing pending in-process. Give off-thread fetch/XHR completions
            // a slot to enqueue resolve jobs; exit once the idle window elapses
            // with no new work observed.
            if (idle.ElapsedMilliseconds >= IdleMs)
            {
                StarlingEngineLog.PumpSettled(_log, wall.ElapsedMilliseconds, iters, rafFrames, "quiescent");
                return;
            }
            await Task.Delay(20, ct).ConfigureAwait(false);
        }

        StarlingEngineLog.PumpCapExceeded(_log, wall.ElapsedMilliseconds, iters, rafFrames);
    }

    /// <summary>
    /// Yields every author stylesheet visible on the document — inline
    /// <c>&lt;style&gt;</c> blocks (whose base URL is the document URL) and
    /// every fetched <c>&lt;link rel="stylesheet"&gt;</c> (whose base URL is
    /// the absolute URL it was fetched from). The font-face fetcher needs the
    /// per-sheet base URL so a <c>url("foo.woff2")</c> declared in a remote
    /// CSS resolves against that CSS's origin, not the document's.
    /// </summary>
    private IEnumerable<(StyleSheet Sheet, StarlingUrl? BaseUrl)> EnumerateAuthorSheets(
        Document doc, StarlingUrl? docUrl, StylesheetFetcher stylesheets)
    {
        foreach (var styleElement in doc.GetElementsByTagName("style"))
        {
            // HTML §4.12.2 — <noscript> content is inert when scripting is
            // enabled (always, in this engine); its fallback CSS must not
            // contribute fonts/backgrounds any more than it contributes rules.
            if (HasNoscriptAncestor(styleElement))
            {
                continue;
            }

            var source = styleElement.TextContent;
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            yield return (CssParser.ParseStyleSheet(source, StyleOrigin.Author), docUrl);
        }
        foreach (var entry in stylesheets.EnumerateLoaded())
        {
            yield return (entry.Sheet, entry.BaseUrl);
        }
    }

    private static bool HasNoscriptAncestor(Element element)
    {
        for (var p = element.ParentNode; p is not null; p = p.ParentNode)
        {
            if (p is Element { LocalName: "noscript" })
            {
                return true;
            }
        }

        return false;
    }

    private static string? ExtractTitle(Document doc)
    {
        foreach (var el in doc.GetElementsByTagName("title"))
        {
            var text = el.TextContent.Trim();
            if (text.Length > 0)
            {
                return text;
            }
        }
        return null;
    }

    private static void EnsureOutputDirectory(string outputPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private sealed class RozRuntime(RenderRozOptions options, ILoggerFactory loggerFactory)
    {
        private readonly ILogger _log = loggerFactory.CreateLogger("Starling.engine.roz");
        private readonly Stopwatch _wall = Stopwatch.StartNew();

        public string? Checkpoint(string stage)
        {
            if (options.MaxRenderWallTimeMs is int maxWallMs && maxWallMs > 0 &&
                _wall.ElapsedMilliseconds > maxWallMs)
            {
                var msg = $"Roz limit exceeded ({stage}): render time {_wall.ElapsedMilliseconds}ms > {maxWallMs}ms.";
                StarlingTelemetry.Counter("engine.roz.time_limit", 1);
                RozRuntimeLog.LimitExceeded(_log, msg);
                return msg;
            }

            var managed = GC.GetTotalMemory(forceFullCollection: false);
            Activity.Current?.SetTag("roz.managed_bytes", managed);
            if (options.MaxManagedHeapBytes is long maxManagedBytes && maxManagedBytes > 0 &&
                managed > maxManagedBytes)
            {
                var msg = $"Roz limit exceeded ({stage}): managed heap {managed} bytes > {maxManagedBytes} bytes.";
                StarlingTelemetry.Counter("engine.roz.managed_limit", 1);
                RozRuntimeLog.LimitExceeded(_log, msg);
                return msg;
            }

            var workingSet = Process.GetCurrentProcess().WorkingSet64;
            Activity.Current?.SetTag("roz.working_set_bytes", workingSet);
            if (options.MaxWorkingSetBytes is long maxWorkingBytes && maxWorkingBytes > 0 &&
                workingSet > maxWorkingBytes)
            {
                var msg = $"Roz limit exceeded ({stage}): working set {workingSet} bytes > {maxWorkingBytes} bytes.";
                StarlingTelemetry.Counter("engine.roz.working_set_limit", 1);
                RozRuntimeLog.LimitExceeded(_log, msg);
                return msg;
            }

            return null;
        }

        public string? CheckDomBudget(Document document, string stage)
        {
            var maxDepth = options.MaxDomDepth;
            var maxNodes = options.MaxDomNodes;
            if ((maxDepth is null || maxDepth <= 0) && (maxNodes is null || maxNodes <= 0))
            {
                return null;
            }

            var visited = 0;
            var stack = new Stack<(Node Node, int Depth)>();
            stack.Push((document, 0));
            while (stack.Count > 0)
            {
                var (node, depth) = stack.Pop();
                visited++;
                if (maxDepth is int depthLimit && depthLimit > 0 && depth > depthLimit)
                {
                    var msg = $"Roz limit exceeded ({stage}): DOM depth {depth} > {depthLimit}.";
                    StarlingTelemetry.Counter("engine.roz.dom_depth_limit", 1);
                    RozRuntimeLog.LimitExceeded(_log, msg);
                    return msg;
                }

                if (maxNodes is int nodeLimit && nodeLimit > 0 && visited > nodeLimit)
                {
                    var msg = $"Roz limit exceeded ({stage}): DOM nodes {visited} > {nodeLimit}.";
                    StarlingTelemetry.Counter("engine.roz.dom_nodes_limit", 1);
                    RozRuntimeLog.LimitExceeded(_log, msg);
                    return msg;
                }

                for (var child = node.LastChild; child is not null; child = child.PreviousSibling)
                {
                    stack.Push((child, depth + 1));
                }
            }

            Activity.Current?.SetTag("roz.dom_nodes", visited);
            return null;
        }
    }

    /// <summary>
    /// Fetch an HTML page, following redirects. Returns both the body and the
    /// final post-redirect URL — callers need the latter as the base for
    /// resolving relative resource URLs (images, stylesheets, fonts). When the
    /// user types `https://google.com`, the 301 lands on `https://www.google.com/`
    /// and the page's `&lt;img src="/images/..."&gt;` must resolve against
    /// the www host; the original (pre-redirect) URL would 404.
    /// </summary>
    private async Task<Result<(string Html, StarlingUrl FinalUrl, Starling.Net.Http.ConnectionSecurity? Security), RenderError>> FetchHtmlAsync(
        StarlingUrl url, StarlingHttpClient http, CancellationToken ct)
    {
        var current = url;

        for (var redirects = 0; redirects <= MaxRedirects; redirects++)
        {
            var response = await http.GetAsync(current, ct).ConfigureAwait(false);
            if (response.IsErr)
            {
                return Result<(string, StarlingUrl, Starling.Net.Http.ConnectionSecurity?), RenderError>.Err(new RenderError(
                    $"Network error fetching {current}: {response.Error}"));
            }

            var resp = response.Value;
            if (IsRedirect(resp.StatusCode))
            {
                if (redirects == MaxRedirects)
                {
                    return Result<(string, StarlingUrl, Starling.Net.Http.ConnectionSecurity?), RenderError>.Err(new RenderError(
                        $"Too many redirects fetching {url}"));
                }

                var redirected = ResolveRedirect(current, resp);
                if (redirected.IsErr)
                {
                    return Result<(string, StarlingUrl, Starling.Net.Http.ConnectionSecurity?), RenderError>.Err(redirected.Error);
                }

                current = redirected.Value;
                continue;
            }

            if (resp.StatusCode is < 200 or >= 400)
            {
                return Result<(string, StarlingUrl, Starling.Net.Http.ConnectionSecurity?), RenderError>.Err(new RenderError(
                    $"HTTP {resp.StatusCode} {resp.ReasonPhrase} from {current}"));
            }

            var contentType = resp.Headers.GetFirst("Content-Type");
            var encoding = ResolveEncoding(contentType, resp.Body.Span);
            return Result<(string, StarlingUrl, Starling.Net.Http.ConnectionSecurity?), RenderError>.Ok(
                (encoding.GetString(resp.Body.Span), current, resp.Security));
        }

        return Result<(string, StarlingUrl, Starling.Net.Http.ConnectionSecurity?), RenderError>.Err(new RenderError(
            $"Too many redirects fetching {url}"));
    }

    private static bool IsRedirect(int statusCode)
        => statusCode is 301 or 302 or 303 or 307 or 308;

    private static Result<StarlingUrl, RenderError> ResolveRedirect(StarlingUrl current, Net.Http.HttpResponse response)
    {
        var location = response.Headers.GetFirst("Location");
        if (string.IsNullOrWhiteSpace(location))
        {
            return Result<StarlingUrl, RenderError>.Err(new RenderError(
                $"HTTP {response.StatusCode} redirect from {current} did not include a Location header"));
        }

        var redirectUrl = ExpandRedirectLocation(location, current);
        var parsed = UrlParser.Parse(redirectUrl, current);
        if (parsed.IsErr)
        {
            return Result<StarlingUrl, RenderError>.Err(new RenderError(
                $"Redirect Location parse failed from {current}: {parsed.Error}"));
        }

        var next = parsed.Value;
        if (!next.IsHttp && !next.IsHttps)
        {
            return Result<StarlingUrl, RenderError>.Err(new RenderError(
                $"Unsupported redirect scheme '{next.Scheme}' from {current}"));
        }

        return Result<StarlingUrl, RenderError>.Ok(next);
    }

    private static string ExpandRedirectLocation(string location, StarlingUrl current)
    {
        var trimmed = location.Trim();
        if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return trimmed;
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return current.Scheme + ":" + trimmed;
        }

        var authority = current.Host is null
            ? ""
            : current.Port is int port
                ? $"{current.Host}:{port}"
                : current.Host;
        var prefix = $"{current.Scheme}://{authority}";

        if (trimmed.StartsWith("/", StringComparison.Ordinal))
        {
            return prefix + trimmed;
        }

        if (trimmed.StartsWith("?", StringComparison.Ordinal))
        {
            return prefix + current.Path + trimmed;
        }

        if (trimmed.StartsWith("#", StringComparison.Ordinal))
        {
            var query = current.Query is null ? "" : "?" + current.Query;
            return prefix + current.Path + query + trimmed;
        }

        var basePath = current.Path;
        var slash = basePath.LastIndexOf('/');
        basePath = slash >= 0 ? basePath[..(slash + 1)] : "/";
        return prefix + basePath + trimmed;
    }

    /// <summary>
    /// Charset sniff: prefer a recognised BOM, then the HTTP
    /// <c>Content-Type</c>'s <c>charset=</c> parameter, then common HTML
    /// <c>&lt;meta charset&gt;</c> / pragma forms in the first bytes, else UTF-8.
    /// </summary>
    internal static Encoding ResolveEncoding(string? contentType, ReadOnlySpan<byte> body)
    {
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
        {
            return Encoding.UTF8;
        }

        if (body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE)
        {
            return Encoding.Unicode;
        }

        if (body.Length >= 2 && body[0] == 0xFE && body[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode;
        }

        if (contentType is { Length: > 0 })
        {
            var charset = ExtractCharset(contentType);
            if (charset is not null && TryResolveEncoding(charset) is { } httpEncoding)
            {
                return httpEncoding;
            }
        }

        var metaCharset = SniffMetaCharset(body);
        if (metaCharset is not null && TryResolveEncoding(metaCharset) is { } metaEncoding)
        {
            return metaEncoding;
        }

        return Encoding.UTF8;
    }

    private static string? SniffMetaCharset(ReadOnlySpan<byte> body)
    {
        var length = Math.Min(body.Length, 4096);
        if (length == 0)
        {
            return null;
        }

        var prefix = Encoding.Latin1.GetString(body[..length]);
        var direct = Regex.Match(
            prefix,
            @"<meta\s+[^>]*charset\s*=\s*[""']?\s*([A-Za-z0-9._:-]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (direct.Success)
        {
            return direct.Groups[1].Value;
        }

        var pragma = Regex.Match(
            prefix,
            @"<meta\s+[^>]*http-equiv\s*=\s*[""']?\s*content-type[^>]*content\s*=\s*[""'][^""']*charset\s*=\s*([A-Za-z0-9._:-]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return pragma.Success ? pragma.Groups[1].Value : null;
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
        // Strip surrounding quotes (we already split charset=... but the
        // header value may itself be quoted).
        var trimmed = name.Trim().Trim('"', '\'');
        if (trimmed.Length == 0)
        {
            return null;
        }

        // WHATWG Encoding Standard "names and labels" lookup is the single
        // source of truth (src/Starling.Common/Encoding/WhatwgEncodingLabels.cs).
        // The WHATWG canonical "windows-1252" maps the entire ISO-8859-1 /
        // US-ASCII family, matching real-world browser behaviour: bytes
        // 0x80..0x9F are mapped to their windows-1252 glyphs (e.g. 0x92 →
        // U+2019 right single quote) rather than C1 controls.
        var canonical = WhatwgEncodingLabels.TryGetCanonicalName(trimmed);
        if (canonical is null)
        {
            return null;
        }

        // Hot-path the .NET base class library singletons; fall back to GetEncoding(name) for
        // CodePages-backed encodings (registered in the static ctor).
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

    internal static string ExtractDisplayText(Document doc)
    {
        // Prefer the body; fall back to the whole document so single-line input
        // fragments still render.
        var source = (Node?)doc.Body ?? doc;
        var raw = new StringBuilder();
        AppendDisplayText(source, raw);
        return NormalizeDisplayText(raw.ToString());
    }

    private static void AppendDisplayText(Node node, StringBuilder buffer)
    {
        switch (node)
        {
            case Text text:
                buffer.Append(text.Data);
                return;
            case CData cdata:
                buffer.Append(cdata.Data);
                return;
            // These elements are `display: none` in the UA stylesheet so they
            // contribute no rendered text. `noscript` is hidden when scripting
            // is enabled (WHATWG HTML §15.3.1) — the engine always runs JS, so
            // its contents (parsed as inert raw text) must not surface here.
            case Element { LocalName: "script" or "style" or "head" or "noscript" }:
                return;
        }

        var isBlock = node is Element element && IsTextBoundaryElement(element.LocalName);
        if (isBlock && buffer.Length > 0)
        {
            buffer.Append(' ');
        }

        for (var child = node.FirstChild; child is not null; child = child.NextSibling)
        {
            AppendDisplayText(child, buffer);
        }

        if (isBlock && buffer.Length > 0)
        {
            buffer.Append(' ');
        }
    }

    private static bool IsTextBoundaryElement(string localName) => localName.ToLowerInvariant() switch
    {
        "address" or "article" or "aside" or "blockquote" or "body" or "br"
            or "dd" or "details" or "dialog" or "div" or "dl" or "dt"
            or "figcaption" or "figure" or "footer" or "form" or "h1"
            or "h2" or "h3" or "h4" or "h5" or "h6" or "header"
            or "hr" or "li" or "main" or "nav" or "ol" or "p"
            or "pre" or "section" or "summary" or "table" or "tbody"
            or "td" or "tfoot" or "th" or "thead" or "tr" or "ul" => true,
        _ => false,
    };

    private static string NormalizeDisplayText(string raw)
    {
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        var buf = new StringBuilder(raw.Length);
        var prevWs = false;
        foreach (var ch in raw)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevWs && buf.Length > 0)
                {
                    buf.Append(' ');
                }

                prevWs = true;
            }
            else
            {
                buf.Append(ch);
                prevWs = false;
            }
        }
        return buf.ToString().TrimEnd();
    }
}

/// <summary>Options for a single engine render: viewport and default font size.</summary>
/// <param name="Viewport">The render viewport size in CSS px.</param>
/// <param name="FontSize">The document's default font size in CSS px.</param>
public sealed record RenderOptions(Size Viewport, float FontSize = 32f)
{
    public static RenderOptions Default { get; } = new(new Size(800, 600));

    /// <summary>
    /// The UA's preferred <c>color-scheme</c>, surfaced to the page through
    /// <c>@media (prefers-color-scheme: …)</c>. The interactive shell binds
    /// this to its light/dark theme toggle so sites with dark-mode rules
    /// re-cascade when the user flips the theme.
    /// </summary>
    public Starling.Css.Media.ColorScheme PreferredColorScheme { get; init; } = Starling.Css.Media.ColorScheme.Light;

    /// <summary>
    /// Roz safety budgets for render-time, memory, and DOM growth limits.
    /// </summary>
    public RenderRozOptions Roz { get; init; } = RenderRozOptions.Default;
}

/// <summary>
/// Configurable safety budgets enforced by the Roz runtime checks.
/// Null means "no limit" for that dimension.
/// </summary>
public sealed record RenderRozOptions
{
    public static RenderRozOptions Default { get; } = new();

    public int? MaxRenderWallTimeMs { get; init; } = 30_000;
    public long? MaxManagedHeapBytes { get; init; } = 1024L * 1024L * 1024L;
    public long? MaxWorkingSetBytes { get; init; } = 2L * 1024L * 1024L * 1024L;
    public int? MaxDomDepth { get; init; } = 4096;
    public int? MaxDomNodes { get; init; } = 2_000_000;
}

public sealed record RenderOutcome(string OutputPath, int Width, int Height, string DisplayText);

public sealed record RenderError(string Message);

internal static partial class StarlingEngineLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "prelayout trigger: {Trigger}")]
    public static partial void PrelayoutTrigger(ILogger logger, string trigger);

    [LoggerMessage(Level = LogLevel.Information, Message = "prelayout done ({Trigger}): {ElapsedMs} ms")]
    public static partial void PrelayoutDone(ILogger logger, string trigger, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Trace, Message = "  layout host: relayouts={Relayouts}, cached reads={FreshHits}")]
    public static partial void LayoutHostStats(ILogger logger, long relayouts, long freshHits);

    [LoggerMessage(Level = LogLevel.Trace, Message = "  attr mutations: {Mutations}")]
    public static partial void AttrMutations(ILogger logger, string mutations);

    [LoggerMessage(Level = LogLevel.Information, Message = "Wrote {OutputPath} ({Width}x{Height}, text length={TextLength}).")]
    public static partial void RenderComplete(ILogger logger, string outputPath, int width, int height, int textLength);

    [LoggerMessage(Level = LogLevel.Error, Message = "{Message}")]
    public static partial void RenderFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "phase: {Phase}@{ElapsedMs}ms ({Bytes} bytes)")]
    public static partial void PhaseTimestamp(ILogger logger, string phase, long elapsedMs, int bytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "phase: {Phase}@{ElapsedMs}ms")]
    public static partial void PhaseDone(ILogger logger, string phase, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "phase: {Phase}@{PageElapsedMs}ms ({PhaseElapsedMs}ms)")]
    public static partial void PhaseElapsed(ILogger logger, string phase, long pageElapsedMs, long phaseElapsedMs);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "run_scripts.critical: {ElapsedMs} ms ({ClassicCount} classic + {ModuleCount} module); " +
                  "layout-host: relayouts={Relayouts}, cached-reads={FreshHits}, cascade-only={CascadeOnlyHits}")]
    public static partial void CriticalScriptsDone(ILogger logger, long elapsedMs, int classicCount, int moduleCount,
        long relayouts, long freshHits, long cascadeOnlyHits);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "post-critical: fetch_styles_fonts={FetchMs}ms, build_page={BuildMs}ms (reused_host_layout={ReusedHost})")]
    public static partial void PostCriticalTiming(ILogger logger, long fetchMs, long buildMs, bool reusedHost);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "deferred.summary: parser={ParserMs}ms async={AsyncMs}ms pump1={Pump1Ms}ms fireLoad={FireLoadMs}ms pump2={Pump2Ms}ms total={TotalMs}ms")]
    public static partial void DeferredSummary(ILogger logger, long parserMs, long asyncMs, long pump1Ms, long fireLoadMs, long pump2Ms, long totalMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Uncaught script error ({Label}): {Message}\n{JsStack}")]
    public static partial void UncaughtScriptError(ILogger logger, string label, string message, string jsStack);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Script compile/run failure ({Label}): {Message}")]
    public static partial void ScriptRunFailure(ILogger logger, string label, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Uncaught module error ({Label}): {Message}")]
    public static partial void UncaughtModuleError(ILogger logger, string label, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Module compile/run failure ({Label}): {Message}")]
    public static partial void ModuleRunFailure(ILogger logger, string label, string message);

    [LoggerMessage(Level = LogLevel.Information,
        Message = "pump.settle: {WallMs}ms wall, {Iters} iters, {RafFrames} rAF frames, exit={Exit}")]
    public static partial void PumpSettled(ILogger logger, long wallMs, int iters, int rafFrames, string exit);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "pump.settle: {WallMs}ms wall, {Iters} iters, {RafFrames} rAF frames, exit=cap (rendering current DOM)")]
    public static partial void PumpCapExceeded(ILogger logger, long wallMs, int iters, int rafFrames);
}

internal static partial class RozRuntimeLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "{Message}")]
    public static partial void LimitExceeded(ILogger logger, string message);
}
