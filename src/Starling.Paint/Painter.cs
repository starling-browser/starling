using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Common.Diagnostics;
using Starling.Common.Image;
using Starling.Css;
using Starling.Css.Animations;
using Starling.Css.Cascade;
using Starling.Css.Media;
using Starling.Css.Parser;
using Starling.Dom;
using Starling.Layout.Tree;
using Starling.Paint.Backend;
using Starling.Paint.DisplayList;
using LayoutEngineImpl = Starling.Layout.LayoutEngine;
using LayoutRect = Starling.Layout.Rect;
using LayoutSize = Starling.Layout.Size;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint;

/// <summary>
/// Paint façade for the full pipeline: parse → style → layout → display list →
/// raster. ImageSharp.Drawing 3.0 is the sole paint backend after the
/// Skia/Graphite native shim was removed; the engine is once again pure
/// managed end-to-end.
/// </summary>
public sealed class Painter
{
    private readonly FontResolver _fonts;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;

    // One animation timeline per document, kept alive across the per-layout
    // StyleEngine rebuilds. Every StyleEngine this painter builds for a given
    // document shares its timeline, so animation/transition playback survives
    // relayouts instead of restarting each pass. Weak-keyed so the timeline GCs
    // with its document.
    private readonly ConditionalWeakTable<Document, AnimationTimeline> _timelines = new();

    public Painter(FontResolver? fonts = null, ILoggerFactory? loggerFactory = null)
    {
        _fonts = fonts ?? FontResolver.Default;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = _loggerFactory.CreateLogger<Painter>();
    }

    /// <summary>The persistent <see cref="AnimationTimeline"/> for
    /// <paramref name="document"/>, created on first use. The engine grabs this
    /// to register Web Animations API (<c>element.animate</c>) animations into
    /// the same long-lived <see cref="AnimationEngine"/> the cascade samples,
    /// without going through a per-layout re-import.</summary>
    public AnimationTimeline GetAnimationTimeline(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return _timelines.GetValue(document, static _ => new AnimationTimeline());
    }

    public CompositedPageRenderer CreateCompositedRenderer(FontFaceRegistry? webFonts = null)
        => new(_fonts, webFonts, _loggerFactory);

    /// <summary>
    /// Run the full pipeline: build a box tree, lay it out, build a paint
    /// display list, and rasterize it with ImageSharp.Drawing 3. The caller
    /// supplies a parsed <see cref="Document"/> and the viewport size in CSS
    /// px. Pass an <paramref name="images"/> resolver to render
    /// <c>&lt;img&gt;</c> elements; without one, every <c>&lt;img&gt;</c>
    /// degrades to its <c>alt</c> text.
    /// <paramref name="externalStylesheet"/> supplies parsed CSS for each
    /// <c>&lt;link rel="stylesheet"&gt;</c> element — the painter inserts the
    /// returned sheet at that element's position in document order so the
    /// cascade matches what a browser sees. Without it, only inline
    /// <c>&lt;style&gt;</c> blocks contribute.
    /// </summary>
    public RenderedBitmap RenderDocument(
        Document document,
        LayoutSize viewport,
        float? defaultFontSize = null,
        IImageResolver? images = null,
        Func<Element, StyleSheet?>? externalStylesheet = null,
        FontFaceRegistry? webFonts = null,
        ColorScheme colorScheme = ColorScheme.Light)
        => RenderDocument(document, viewport, defaultFontSize, images, externalStylesheet, webFonts, nowMs: null, colorScheme);

    /// <summary>
    /// Render at a specific frame timestamp — drives CSS animations and
    /// transitions through the cascade so the produced bitmap reflects the
    /// engine state at <paramref name="nowMs"/>. Callers must Tick the
    /// underlying <see cref="StyleEngine.AnimationEngine"/> and
    /// <see cref="StyleEngine.TransitionEngine"/> to <paramref name="nowMs"/>
    /// before calling this — the painter does not own the engines and so
    /// cannot advance them itself.
    /// </summary>
    public RenderedBitmap RenderDocument(
        Document document,
        LayoutSize viewport,
        float? defaultFontSize,
        IImageResolver? images,
        Func<Element, StyleSheet?>? externalStylesheet,
        FontFaceRegistry? webFonts,
        double? nowMs,
        ColorScheme colorScheme = ColorScheme.Light,
        LayoutRect? clipViewport = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        var root = LayoutDocument(document, viewport, defaultFontSize, images, externalStylesheet, webFonts, nowMs, colorScheme);

        PaintList displayList;
        using (StarlingTelemetry.Span("paint", "display_list"))
        {
            // The rendered bitmap covers the clip viewport when one is given,
            // otherwise the layout viewport — that area is the CSS canvas the
            // root (or body) background propagates onto.
            var canvasRect = clipViewport ?? new LayoutRect(0, 0, viewport.Width, viewport.Height);
            displayList = new DisplayListBuilder().Build(root, clipViewport, styleOverride: null, images: images, scrollOffsets: null, canvasRect);
        }

        using (StarlingTelemetry.Span("paint", $"raster:{PaintBackendSelector.Selected.ToString().ToLowerInvariant()}"))
        {
            // DisplayList is the renderer-neutral seam. ImageSharp.Drawing 3
            // is the only backend after the Skia shim removal. When a clip
            // viewport is supplied the bitmap is sized to it (and translated by
            // its offset); otherwise the full layout viewport is rendered.
            try
            {
                // Backend construction (font collection load especially) runs
                // inside the raster span but outside any child, so on a cold
                // process it silently widens the raster→command_record gap.
                // Span it so that cost is attributable.
                IPaintBackend backend;
                using (StarlingTelemetry.Span("paint", "raster.backend_init"))
                {
                    backend = PaintBackendSelector.Create(_fonts, webFonts);
                }

                using (backend)
                {
                    return clipViewport is { } clip
                        ? backend.Render(displayList, clip)
                        : backend.Render(displayList, viewport);
                }
            }
            catch (Exception ex)
            {
                // Surface failures (backend construction, WebGPU init, native
                // shim missing, etc.) through diagnostics before unwinding so
                // Aspire shows the full exception on the raster span instead
                // of just a silently failed activity.
                PainterLog.RasterBackendFailed(_log, ex, PaintBackendSelector.Selected.ToString());
                throw;
            }
        }
    }

    /// <summary>
    /// Paint an <em>already-laid-out</em> box tree: build the display list and
    /// rasterize it, skipping cascade + layout entirely. The caller owns the
    /// <paramref name="root"/> (typically produced by an earlier layout that is
    /// still valid). Used by the engine to reuse the pre-script layout for the
    /// final render when nothing layout-affecting changed after scripts ran —
    /// collapsing two full layouts into one. Emits the same <c>display_list</c>
    /// and <c>raster:*</c> spans as <see cref="RenderDocument(Document, LayoutSize, float?, IImageResolver?, Func{Element, StyleSheet?}?, FontFaceRegistry?, double?, ColorScheme, LayoutRect?)"/>
    /// (but no <c>layout</c> / <c>style_cascade</c> span), so telemetry stays
    /// consistent and a span-counting harness can verify exactly one layout ran.
    /// </summary>
    public RenderedBitmap PaintLaidOut(
        Starling.Layout.Box.BlockBox root,
        LayoutSize viewport,
        IImageResolver? images = null,
        FontFaceRegistry? webFonts = null,
        LayoutRect? clipViewport = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        PaintList displayList;
        using (StarlingTelemetry.Span("paint", "display_list"))
        {
            // The rendered bitmap covers the clip viewport when one is given,
            // otherwise the layout viewport — that area is the CSS canvas the
            // root (or body) background propagates onto.
            var canvasRect = clipViewport ?? new LayoutRect(0, 0, viewport.Width, viewport.Height);
            displayList = new DisplayListBuilder().Build(root, clipViewport, styleOverride: null, images: images, scrollOffsets: null, canvasRect);
        }

        using (StarlingTelemetry.Span("paint", $"raster:{PaintBackendSelector.Selected.ToString().ToLowerInvariant()}"))
        {
            try
            {
                IPaintBackend backend;
                using (StarlingTelemetry.Span("paint", "raster.backend_init"))
                {
                    backend = PaintBackendSelector.Create(_fonts, webFonts);
                }

                using (backend)
                {
                    return clipViewport is { } clip
                        ? backend.Render(displayList, clip)
                        : backend.Render(displayList, viewport);
                }
            }
            catch (Exception ex)
            {
                PainterLog.RasterBackendFailed(_log, ex, PaintBackendSelector.Selected.ToString());
                throw;
            }
        }
    }

    /// <summary>
    /// Run cascade + layout without rasterizing. Returns the laid-out box tree
    /// for callers that want to walk it themselves — interactive shells (a real
    /// browser frame) consume this so taps, selection, and Cmd-F can resolve
    /// against structure instead of pixels.
    /// </summary>
    public Starling.Layout.Box.BlockBox LayoutDocument(
        Document document,
        LayoutSize viewport,
        float? defaultFontSize = null,
        IImageResolver? images = null,
        Func<Element, StyleSheet?>? externalStylesheet = null,
        FontFaceRegistry? webFonts = null,
        ColorScheme colorScheme = ColorScheme.Light)
        => LayoutDocument(document, viewport, defaultFontSize, images, externalStylesheet, webFonts, nowMs: null, colorScheme);

    /// <summary>Layout at a frame timestamp — see the matching RenderDocument
    /// overload for semantics.</summary>
    public Starling.Layout.Box.BlockBox LayoutDocument(
        Document document,
        LayoutSize viewport,
        float? defaultFontSize,
        IImageResolver? images,
        Func<Element, StyleSheet?>? externalStylesheet,
        FontFaceRegistry? webFonts,
        double? nowMs,
        ColorScheme colorScheme = ColorScheme.Light)
    {
        var (root, _) = LayoutDocumentWithStyle(document, viewport, defaultFontSize, images, externalStylesheet, webFonts, nowMs, colorScheme);
        return root;
    }

    /// <summary>
    /// Same as <see cref="LayoutDocument(Document, LayoutSize, float?, IImageResolver?, Func{Element, StyleSheet?}?, FontFaceRegistry?, ColorScheme)"/> but also returns the
    /// <see cref="StyleEngine"/> used for the cascade, so interactive callers
    /// can recompute styles for individual elements when state changes
    /// (<c>:hover</c>, <c>:focus</c>, <c>:active</c>) without re-running layout.
    /// </summary>
    public (Starling.Layout.Box.BlockBox Root, StyleEngine Style) LayoutDocumentWithStyle(
        Document document,
        LayoutSize viewport,
        float? defaultFontSize = null,
        IImageResolver? images = null,
        Func<Element, StyleSheet?>? externalStylesheet = null,
        FontFaceRegistry? webFonts = null,
        ColorScheme colorScheme = ColorScheme.Light,
        CancellationToken ct = default,
        Starling.Layout.Scroll.ScrollStateStore? scrollState = null)
        => LayoutDocumentWithStyle(document, viewport, defaultFontSize, images, externalStylesheet, webFonts, nowMs: null, colorScheme, ct, scrollState);

    /// <summary>Layout overload that threads a frame timestamp through the
    /// cascade. See the matching RenderDocument overload for semantics. The
    /// optional <paramref name="ct"/> is observed by the block-layout pass so a
    /// host's Stop signal interrupts a heavy reflow between sibling boxes.</summary>
    public (Starling.Layout.Box.BlockBox Root, StyleEngine Style) LayoutDocumentWithStyle(
        Document document,
        LayoutSize viewport,
        float? defaultFontSize,
        IImageResolver? images,
        Func<Element, StyleSheet?>? externalStylesheet,
        FontFaceRegistry? webFonts,
        double? nowMs,
        ColorScheme colorScheme = ColorScheme.Light,
        CancellationToken ct = default,
        Starling.Layout.Scroll.ScrollStateStore? scrollState = null)
    {
        ArgumentNullException.ThrowIfNull(document);

        StyleEngine style;
        using (StarlingTelemetry.Span("paint", "style_cascade"))
        {
            style = CreateStyleEngine(document, viewport, defaultFontSize, externalStylesheet, _loggerFactory, colorScheme);
        }

        var measurer = PaintBackendSelector.CreateMeasurer(_fonts, webFonts);
        try
        {
            // The engine session's per-document scroll store rides along so
            // this pass refreshes scrollports + scrollable overflow and
            // re-clamps stored offsets (scroll-model.md WP1). Null for
            // callers without a session (one-shot raster paths).
            var layoutEngine = new LayoutEngineImpl(style, measurer, images, _loggerFactory, ct) { ScrollState = scrollState };
            Starling.Layout.Box.BlockBox root;
            using (StarlingTelemetry.Span("paint", "layout"))
            {
                root = layoutEngine.LayoutDocument(document, viewport, nowMs);
            }

            return (root, style);
        }
        finally
        {
            (measurer as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Re-layout and paint <paramref name="document"/> using the caller's
    /// pre-built <see cref="StyleEngine"/> at frame timestamp
    /// <paramref name="nowMs"/>. Used by the frame loop so the
    /// <see cref="StyleEngine.AnimationEngine"/> / <see cref="StyleEngine.TransitionEngine"/>
    /// state seeded on a retained engine survives across paints (the
    /// fresh-engine path of <see cref="RenderDocument(Document, LayoutSize, float?, IImageResolver?, Func{Element, StyleSheet?}?, FontFaceRegistry?, double?, ColorScheme, LayoutRect?)"/>
    /// would reseed every call, restarting animations on each frame).
    /// Callers tick the engines before invoking this.
    /// </summary>
    public RenderedBitmap RenderWithStyle(
        Document document,
        StyleEngine style,
        LayoutSize viewport,
        IImageResolver? images,
        FontFaceRegistry? webFonts,
        double nowMs,
        LayoutRect? clipViewport = null,
        Starling.Layout.Scroll.ScrollStateStore? scrollState = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(style);

        var measurer = PaintBackendSelector.CreateMeasurer(_fonts, webFonts);
        try
        {
            // The page's scroll store rides through the per-frame relayout so
            // scrollports + scrollable overflow stay fresh during pure
            // animation frames (scroll-model.md WP2), and the display list
            // paints each scroll container's subtree at its stored offset.
            var layoutEngine = new LayoutEngineImpl(style, measurer, images, _loggerFactory) { ScrollState = scrollState };
            Starling.Layout.Box.BlockBox root;
            using (StarlingTelemetry.Span("paint", "layout"))
            {
                root = layoutEngine.LayoutDocument(document, viewport, nowMs);
            }

            PaintList displayList;
            using (StarlingTelemetry.Span("paint", "display_list"))
            {
                var canvasRect = clipViewport ?? new LayoutRect(0, 0, viewport.Width, viewport.Height);
                displayList = new DisplayListBuilder().Build(
                    root, clipViewport, styleOverride: null, images: images,
                    scrollOffsets: scrollState is null ? null : scrollState.GetOffset, canvasRect,
                    stickyShifts: scrollState is null ? null : scrollState.GetStickyShift);
            }

            using (StarlingTelemetry.Span("paint", $"raster:{PaintBackendSelector.Selected.ToString().ToLowerInvariant()}"))
            {
                IPaintBackend backend;
                using (StarlingTelemetry.Span("paint", "raster.backend_init"))
                {
                    backend = PaintBackendSelector.Create(_fonts, webFonts);
                }

                using (backend)
                {
                    return clipViewport is { } clip
                        ? backend.Render(displayList, clip)
                        : backend.Render(displayList, viewport);
                }
            }
        }
        finally
        {
            (measurer as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Lay <paramref name="document"/> out through a persistent
    /// <see cref="Starling.Layout.Incremental.LayoutSession"/>, reusing the
    /// session's retained box tree where the per-frame mutation batch allows and
    /// rebuilding only the changed subtrees. The session owns the tree across
    /// frames. Returns the laid-out root. Used by the engine's incremental
    /// relayout path; falls back internally to a full rebuild when reuse isn't
    /// safe.
    /// </summary>
    public Starling.Layout.Box.BlockBox LayoutDocumentIncremental(
        Starling.Layout.Incremental.LayoutSession session,
        Document document,
        LayoutSize viewport,
        FontFaceRegistry? webFonts,
        double? nowMs = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(document);

        var measurer = GetOrCreateIncrementalMeasurer(webFonts);
        using (StarlingTelemetry.Span("paint", "layout.incremental"))
        {
            return session.Layout(document, viewport, measurer, nowMs, ct);
        }
    }

    // Measurer cache for the incremental relayout path. Building an
    // ImageSharp measurer parses the whole font collection (bundled + system
    // + web fonts) — about 4 ms, which the live loop paid on EVERY animation
    // tick when each relayout created a fresh measurer. The fonts only change
    // when the page registers a new @font-face, so the cache keys on the
    // registry and its version.
    private Starling.Layout.Text.ITextMeasurer? _incrementalMeasurer;
    private FontFaceRegistry? _incrementalMeasurerFonts;
    private int _incrementalMeasurerVersion;
    private readonly object _measurerGate = new();

    private Starling.Layout.Text.ITextMeasurer GetOrCreateIncrementalMeasurer(FontFaceRegistry? webFonts)
    {
        var version = webFonts?.Version ?? 0;
        lock (_measurerGate)
        {
            if (_incrementalMeasurer is null
                || !ReferenceEquals(_incrementalMeasurerFonts, webFonts)
                || _incrementalMeasurerVersion != version)
            {
                (_incrementalMeasurer as IDisposable)?.Dispose();
                _incrementalMeasurer = PaintBackendSelector.CreateMeasurer(_fonts, webFonts);
                _incrementalMeasurerFonts = webFonts;
                _incrementalMeasurerVersion = version;
            }

            return _incrementalMeasurer;
        }
    }

    /// <summary>Build only the cascade — no layout, no display list. Used by
    /// <c>BoxLayoutHost</c> (in <c>Starling.Engine</c>) to answer
    /// <c>getComputedStyle</c> reads for purely-cascaded properties
    /// (<c>visibility</c>, <c>opacity</c>, <c>display</c>, …) without paying the
    /// full layout pass. On google.com this saves ~210 ms — the trace shows
    /// the page's startup script reads <c>getComputedStyle(el).visibility</c>
    /// before any geometry is needed, and the engine previously forced layout
    /// to answer that.</summary>
    public StyleEngine BuildStyleEngine(
        Document document,
        LayoutSize viewport,
        float? defaultFontSize,
        Func<Element, StyleSheet?>? externalStylesheet,
        ColorScheme colorScheme = ColorScheme.Light)
    {
        ArgumentNullException.ThrowIfNull(document);
        using (StarlingTelemetry.Span("paint", "style_cascade.standalone"))
        {
            return CreateStyleEngine(document, viewport, defaultFontSize, externalStylesheet, _loggerFactory, colorScheme);
        }
    }

    private StyleEngine CreateStyleEngine(
        Document document,
        LayoutSize viewport,
        float? defaultFontSize,
        Func<Element, StyleSheet?>? externalStylesheet,
        ILoggerFactory loggerFactory,
        ColorScheme colorScheme)
    {
        // Reuse the document's persistent timeline so animation/transition state
        // outlives this (per-layout) StyleEngine.
        var style = new StyleEngine(
            includeUserAgentStyleSheet: true, loggerFactory: loggerFactory, timeline: GetAnimationTimeline(document));
        // Expose the UA's preferred color scheme through @media
        // (prefers-color-scheme: …) and the real viewport size through both
        // @media width/height/orientation queries and the vw/vh/sv*/lv*/dv*
        // length units. Without the viewport here the cascade falls back to
        // MediaContext.Default (1024×768) and every page lays out for a fixed
        // screen regardless of the actual window size. Evaluated on every
        // Compute call.
        style.MediaContext = style.MediaContext with
        {
            ColorScheme = colorScheme,
            ViewportWidthPx = viewport.Width,
            ViewportHeightPx = viewport.Height,
        };

        if (defaultFontSize is > 0)
        {
            // The user-stylesheet override is content-addressable by font size.
            // Parse once per distinct size and reuse across layouts so repeated
            // re-layouts during script execution don't re-parse a constant sheet.
            var sheet = s_userFontSizeSheetCache.GetOrAdd(
                defaultFontSize.Value,
                size => CssParser.ParseStyleSheet(
                    FormattableString.Invariant($"body {{ font-size: {size}px; }}"),
                    StyleOrigin.User));
            style.AddStyleSheet(sheet);
        }

        // Walk the tree in document order so `<style>` and `<link rel=stylesheet>`
        // contribute to the cascade in source order — required by [CSS Cascade
        // 4 §6.3]: tree order is the tiebreaker after origin/importance/specificity.
        AddAuthorStylesheets(document, externalStylesheet, style, loggerFactory);

        return style;
    }

    // Inline-<style> parse cache. Layout runs every time a script reads
    // geometry/computed style after a DOM mutation; each layout currently
    // walks the document and re-parses every <style> block. Google's
    // homepage has ~50 inline <style>s, so without the cache each layout
    // pays for ~50 ParseStyleSheet + selector-index builds. Weak-keyed on
    // the Element so the entry GCs with the document.
    private sealed class CachedSheet
    {
        public string Source { get; init; } = string.Empty;
        public StyleSheet Sheet { get; init; } = null!;
    }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Element, CachedSheet> s_inlineSheetCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<float, StyleSheet> s_userFontSizeSheetCache = new();

    private static void AddAuthorStylesheets(
        Node node,
        Func<Element, StyleSheet?>? externalStylesheet,
        StyleEngine style,
        ILoggerFactory loggerFactory)
    {
        if (node is Element element)
        {
            // HTML §4.12.2 — with scripting enabled <noscript> content is inert
            // fallback. The engine always has scripting, so a <style>/<link>
            // inside <noscript> must not join the cascade: x.com's no-JS block
            // is `#react-root{display:none!important}` + a white body — applying
            // it blanks the whole app.
            if (element.LocalName == "noscript")
            {
                return;
            }

            if (element.LocalName == "style")
            {
                var source = element.TextContent;
                if (!string.IsNullOrWhiteSpace(source))
                {
                    StyleSheet sheet;
                    // `Element.TextContent` allocates a fresh string each
                    // access; ReferenceEquals would always miss. Value compare
                    // — bounded by total CSS bytes per layout (small).
                    if (s_inlineSheetCache.TryGetValue(element, out var cached)
                        && string.Equals(cached.Source, source, StringComparison.Ordinal))
                    {
                        sheet = cached.Sheet;
                    }
                    else
                    {
                        sheet = CssParser.ParseStyleSheet(source, StyleOrigin.Author);
                        s_inlineSheetCache.Remove(element);
                        s_inlineSheetCache.Add(element, new CachedSheet { Source = source, Sheet = sheet });
                    }
                    style.AddStyleSheet(sheet);
                }
            }
            else if (element.LocalName == "link" && externalStylesheet is not null)
            {
                var sheet = externalStylesheet(element);
                if (sheet is not null)
                {
                    style.AddStyleSheet(sheet);
                }
            }
        }

        for (var child = node.FirstChild; child is not null; child = child.NextSibling)
        {
            AddAuthorStylesheets(child, externalStylesheet, style, loggerFactory);
        }
    }
}

internal static partial class PainterLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "raster backend '{Backend}' failed")]
    public static partial void RasterBackendFailed(ILogger logger, Exception ex, string backend);
}
