using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using SixLabors.Fonts;
using SixLabors.Fonts.Unicode;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.Drawing.Processing.Backends;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using Starling.Common.Diagnostics;
using Starling.Common.Image;
using Starling.Css.Values;
using Starling.Paint.Gradients;
using Starling.Layout.Text;
using Starling.Paint.DisplayList;
using Starling.Paint.Interop;
using LayoutRect = Starling.Layout.Rect;
using LayoutSize = Starling.Layout.Size;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint.Backend;

/// <summary>
/// Cross-platform paint backend that replays a <see cref="DisplayList"/> through
/// ImageSharp.Drawing's canvas API. Supports two destinations: the CPU
/// rasterizer (<c>Image&lt;Rgba32&gt;</c>) and the WebGPU render target in
/// <see cref="WebGPURenderTarget"/>, selected via
/// <c>STARLING_PAINT_BACKEND=imagesharp-webgpu</c>. Both destinations consume
/// the same display-list replay because each exposes a <see cref="DrawingCanvas"/>.
/// </summary>
/// <remarks>
/// <para>
/// The starting canvas is filled opaque white so a fresh render lands on a
/// predictable background regardless of viewport size.
/// </para>
/// <para>
/// Text renders through <see cref="TextBlock"/> so SixLabors owns glyph
/// painting, including layered and color glyphs. When layout supplied an
/// ImageSharp-shaped run, the backend reuses its prepared block; otherwise it
/// creates a block from the resolved font at paint time.
/// </para>
/// <para>
/// Fonts: the backend snapshots a private <see cref="FontCollection"/> at
/// construction time. It contains the bundled fonts, any registered web fonts,
/// and system fonts when the host exposes them. Family lookup walks
/// <see cref="FontSpec.Families"/> against that collection and falls back to
/// the first registered family when nothing matches.
/// </para>
/// </remarks>
internal sealed partial class ImageSharpBackend : IPaintBackend, IGpuTexturePaintBackend
{
    private readonly IResampler _resampler = KnownResamplers.Bicubic;
    private readonly FontResolver _fonts;
    private readonly FontFaceRegistry? _webFonts;
    private readonly bool _useWebGpu;
    private readonly FontCollection _fontCollection;
    private readonly ConcurrentDictionary<FontCacheKey, Font> _fontCache = new();
    private readonly BoxShadowRasterCache _boxShadowCache;
    private readonly ConicLayerCache _conicCache = new();
    private static readonly Lazy<WebGPUEnvironmentError> _webGpuAvailability = new(WebGPUEnvironment.ProbeAvailability);

    public ImageSharpBackend(FontResolver fonts, FontFaceRegistry? webFonts, bool useWebGpu = false)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        _fonts = fonts;
        _webFonts = webFonts;
        _useWebGpu = useWebGpu;
        _fontCollection = ImageSharpFontLookup.LoadCollection(webFonts);
        _boxShadowCache = new BoxShadowRasterCache();
    }

    public string Name => _useWebGpu ? "imagesharp-webgpu" : "imagesharp";

    public RenderedBitmap Render(PaintList list, LayoutSize viewport, float scale = 1.0f)
        => Render(list, new LayoutRect(0, 0, viewport.Width, viewport.Height), scale);

    public RenderedBitmap Render(PaintList list, LayoutRect viewport, float scale = 1.0f)
        => Render(list, viewport, scale, opaqueBackground: true);

    /// <summary>
    /// Renders <paramref name="list"/> over a transparent canvas when
    /// <paramref name="opaqueBackground"/> is false — used by the compositor to
    /// rasterize a layer's slice into its own bitmap so unpainted regions stay
    /// see-through for alpha-over compositing. The default (true) clears to
    /// opaque white, the behavior every other caller relies on.
    /// </summary>
    public RenderedBitmap Render(PaintList list, LayoutRect viewport, float scale, bool opaqueBackground)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            throw new ArgumentException("Viewport must have positive dimensions.", nameof(viewport));
        }

        if (!(scale > 0.0f))
        {
            throw new ArgumentException("Scale must be positive.", nameof(scale));
        }

        var width = (int)Math.Ceiling(viewport.Width * scale);
        var height = (int)Math.Ceiling(viewport.Height * scale);

        // viewport.X/Y is the page-coordinate scroll offset of the visible
        // region: a page-coord item at (viewport.X, viewport.Y) must land at
        // device (0,0). Express it as the outermost (ancestor) CSS-space
        // transform so it composes with per-item transforms and is folded into
        // the canvas matrix alongside the device scale by ToCanvasMatrix.
        var viewportTransform = Matrix2D.Translate(-viewport.X, -viewport.Y);

        Activity.Current?.SetTag("raster.width", width);
        Activity.Current?.SetTag("raster.height", height);
        Activity.Current?.SetTag("raster.scale", scale);
        Activity.Current?.SetTag("raster.items", list.Items.Count);

        if (_useWebGpu && (width > MaxWebGpuTextureDimension || height > MaxWebGpuTextureDimension))
        {
            ThrowWebGpuTargetOversized(width, height);
        }

        return _useWebGpu
            ? RenderWebGpu(list, width, height, scale, viewportTransform, opaqueBackground)
            : RenderCpu(list, width, height, scale, viewportTransform, opaqueBackground);
    }

    public RenderedBitmap RenderSmallBitmap(PaintList list, LayoutRect viewport, float scale, bool opaqueBackground)
    {
        ArgumentNullException.ThrowIfNull(list);
        var width = (int)Math.Ceiling(viewport.Width * scale);
        var height = (int)Math.Ceiling(viewport.Height * scale);
        var viewportTransform = Matrix2D.Translate(-viewport.X, -viewport.Y);
        // Always the CPU rasterizer: for the small surfaces the compositor
        // routes here, the WebGPU canvas's per-flush scheduling readback
        // costs more than rastering the pixels.
        return RenderCpu(list, width, height, scale, viewportTransform, opaqueBackground);
    }

    public RenderedBitmap RenderFiltered(PaintList list, LayoutRect viewport, float scale,
        IReadOnlyList<FilterFunction> filters)
    {
        var unfiltered = Render(list, viewport, scale, opaqueBackground: false);
        if (filters.Count == 0)
        {
            return unfiltered;
        }

        // Wrap the straight-RGBA pixels (no copy), run the chain at device
        // resolution, then read the result back out. ApplyFilterChain may return
        // a rebuilt image (drop-shadow) — the wrapper stays owned by the using,
        // the rebuilt result is disposed after readback.
        using var layer = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(unfiltered.Rgba, unfiltered.Width, unfiltered.Height);
        var filtered = ApplyFilterChain(layer, filters, scale);
        try
        {
            var rgba = new byte[filtered.Width * filtered.Height * 4];
            filtered.CopyPixelDataTo(rgba);
            return new RenderedBitmap(filtered.Width, filtered.Height, rgba);
        }
        finally
        {
            if (!ReferenceEquals(filtered, layer))
            {
                filtered.Dispose();
            }
        }
    }

    public RenderedBitmap FilterBitmap(RenderedBitmap source, IReadOnlyList<FilterFunction> filters, float scale)
    {
        if (filters.Count == 0)
        {
            return source;
        }

        // Same shape as RenderFiltered's tail: wrap the straight-RGBA pixels
        // (no copy), run the chain at device resolution, read the result out.
        using var layer = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(source.Rgba, source.Width, source.Height);
        var filtered = ApplyFilterChain(layer, filters, scale);
        try
        {
            var rgba = new byte[filtered.Width * filtered.Height * 4];
            filtered.CopyPixelDataTo(rgba);
            return new RenderedBitmap(filtered.Width, filtered.Height, rgba);
        }
        finally
        {
            if (!ReferenceEquals(filtered, layer))
            {
                filtered.Dispose();
            }
        }
    }

    public GpuPaintTexture RenderTexture(
        PaintList list,
        LayoutRect viewport,
        float scale,
        bool opaqueBackground,
        GpuPaintDevice device)
    {
        ArgumentNullException.ThrowIfNull(list);

        if (!_useWebGpu)
        {
            throw new InvalidOperationException("GPU texture rendering requires the ImageSharp WebGPU paint backend.");
        }

        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            throw new ArgumentException("Viewport must have positive dimensions.", nameof(viewport));
        }

        if (!(scale > 0.0f))
        {
            throw new ArgumentException("Scale must be positive.", nameof(scale));
        }

        var width = (int)Math.Ceiling(viewport.Width * scale);
        var height = (int)Math.Ceiling(viewport.Height * scale);
        if (width > MaxWebGpuTextureDimension || height > MaxWebGpuTextureDimension)
        {
            ThrowWebGpuTargetOversized(width, height);
        }

        var viewportTransform = Matrix2D.Translate(-viewport.X, -viewport.Y);
        Activity.Current?.SetTag("raster.width", width);
        Activity.Current?.SetTag("raster.height", height);
        Activity.Current?.SetTag("raster.scale", scale);
        Activity.Current?.SetTag("raster.items", list.Items.Count);

        EnsureWebGpuAvailable();

        WebGPURenderTarget? target = null;
        try
        {
            using (StarlingTelemetry.Span("paint", "raster.context_init"))
            {
                ImageSharpGpuContext.GetOrCreate(device).ThrowIfDisposed();
            }

            using (StarlingTelemetry.Span("paint", "raster.surface_alloc"))
            {
                target = ImageSharpGpuContext.GetOrCreate(device).CreateRenderTarget(WebGPUTextureFormat.Rgba8Unorm, width, height);
            }

            using var pendingImageSources = new DisposableBag();

            DrawingCanvas canvas;
            using (StarlingTelemetry.Span("paint", "raster.command_record"))
            {
                canvas = target.CreateCanvas();
                using (canvas)
                {
                    ReplayList(canvas, list, width, height, scale, viewportTransform,
                        clearWhite: opaqueBackground, pendingImageSources, flush: true);
                }
            }

            var owned = new ImageSharpGpuTexture(target);
            target = null;
            return new GpuPaintTexture(
                owned.TextureHandle,
                owned.TextureViewHandle,
                owned.Width,
                owned.Height,
                PaintTextureFormat.Rgba8Unorm,
                owned);
        }
        finally
        {
            target?.Dispose();
        }
    }

    // 8192 is WebGPU's guaranteed minimum maxTextureDimension2D. Larger
    // surfaces can fail texture creation on constrained adapters, and wgpu's
    // default uncaptured-error handler aborts the process before C# can catch
    // anything, so detect the overflow before allocating a texture.
    private const int MaxWebGpuTextureDimension = 8192;

    private static void ThrowWebGpuTargetOversized(int width, int height)
    {
        // wgpu's default uncaptured-error handler aborts the process when
        // texture creation exceeds maxTextureDimension2D. GPU sessions are
        // strict: an oversize texture is a render failure, not a CPU fallback.
        Activity.Current?.SetTag("raster.webgpu.failure_reason", "exceeds_max_texture_dimension");
        throw new InvalidOperationException(
            $"WebGPU paint target {width}x{height} exceeds the supported " +
            $"{MaxWebGpuTextureDimension}x{MaxWebGpuTextureDimension} texture limit.");
    }

    private static void EnsureWebGpuAvailable()
    {
        // Probe the WebGPU environment before constructing a render target so a
        // missing or blocked wgpu-native binary reports a clear error.
        var probe = _webGpuAvailability.Value;
        if (probe != WebGPUEnvironmentError.Success)
        {
            throw new InvalidOperationException(
                $"WebGPU paint backend requested via STARLING_PAINT_BACKEND=imagesharp-webgpu, " +
                $"but WebGPUEnvironment.ProbeAvailability returned {probe}. " +
                Environment.NewLine + Environment.NewLine +
                "Native loader trail (Starling.Paint.Interop.WgpuNativeLoader):" + Environment.NewLine +
                "  " + WgpuNativeLoader.Diagnose() +
                Environment.NewLine + Environment.NewLine +
                "Common causes: the wgpu-native library was not copied into the runtime layout " +
                "(check runtimes/<rid>/native/libwgpu_native.{dylib,so} in the app bundle), " +
                "no compatible GPU adapter is visible to the process, or a sandbox blocks " +
                "Metal/Vulkan/D3D12 access. Fall back to STARLING_PAINT_BACKEND=imagesharp " +
                "to use the pure-CPU rasterizer.");
        }
    }

    private RenderedBitmap RenderCpu(PaintList list, int width, int height, float scale, Matrix2D viewportTransform, bool opaqueBackground = true)
    {
        using (StarlingTelemetry.Span("paint", "raster.context_init"))
        {
            // ImageSharp has no persistent CPU context; the span is kept so
            // the diagnostics trace shape stays stable for tooling.
        }

        Image<Rgba32> image;
        using (StarlingTelemetry.Span("paint", "raster.surface_alloc"))
        {
            image = new Image<Rgba32>(width, height, opaqueBackground ? new Rgba32(255, 255, 255, 255) : new Rgba32(0, 0, 0, 0));
        }

        using (image)
        {
            // Image sources used by DrawImage outlive the canvas closure: the
            // DrawingCanvas records commands and rasterizes them when the
            // Mutate/Paint scope unwinds, so disposing image sources inside
            // the closure throws ObjectDisposedException from ImageBrushRenderer.
            // Stage them in a bag that is disposed after rasterization/readback.
            using var pendingImageSources = new DisposableBag();

            using (StarlingTelemetry.Span("paint", "raster.command_record"))
            {
                if (ContainsTopLevelBackdropFilter(list.Items))
                {
                    // backdrop-filter needs the pixels already painted under the
                    // element, which the lazily-rasterizing canvas cannot give
                    // mid-replay — replay in segments and snapshot between them.
                    ReplayListCpuSegmented(image, list, width, height, scale, viewportTransform, pendingImageSources);
                }
                else
                {
                    // The CPU image is allocated pre-filled with the background color, so
                    // the replay must not clear it again (clearWhite: false). ImageSharp
                    // rasterizes lazily when the Paint scope unwinds — no explicit flush.
                    image.Mutate(x => x.Paint(canvas =>
                        ReplayList(canvas, list, width, height, scale, viewportTransform,
                            clearWhite: false, pendingImageSources, flush: false)));
                }
            }

            byte[] pixels;
            using (StarlingTelemetry.Span("paint", "raster.readback"))
            {
                pixels = new byte[checked(width * height * 4)];
                image.CopyPixelDataTo(pixels);
            }

            return new RenderedBitmap(width, height, pixels);
        }
    }

    // GPU path: WebGPURenderTarget allocates an offscreen surface and exposes
    // the same DrawingCanvas API as the CPU path, so display-list replay stays
    // shared. The target starts transparent, so clear it to opaque white to
    // match the CPU image allocation (skipped when opaqueBackground is false so
    // compositor layer slices keep a see-through background).
    private RenderedBitmap RenderWebGpu(PaintList list, int width, int height, float scale, Matrix2D viewportTransform, bool opaqueBackground = true)
    {
        EnsureWebGpuAvailable();

        WebGPURenderTarget target;
        using (StarlingTelemetry.Span("paint", "raster.context_init"))
        {
            target = new WebGPURenderTarget(width, height);
        }

        using (target)
        {
            using var pendingImageSources = new DisposableBag();

            DrawingCanvas canvas;
            using (StarlingTelemetry.Span("paint", "raster.surface_alloc"))
            {
                canvas = target.CreateCanvas();
            }

            using (canvas)
            using (StarlingTelemetry.Span("paint", "raster.command_record"))
            {
                // Flush seals queued commands into the canvas timeline so the GPU
                // pipeline executes before ReadbackImage samples the texture.
                // Without it, readback races the (un)submitted command buffer and
                // returns the initial clear color.
                ReplayList(canvas, list, width, height, scale, viewportTransform,
                    clearWhite: opaqueBackground, pendingImageSources, flush: true);
            }

            byte[] pixels;
            using (StarlingTelemetry.Span("paint", "raster.readback"))
            {
                using var image = target.ReadbackImage<Rgba32>();
                pixels = new byte[checked(width * height * 4)];
                image.CopyPixelDataTo(pixels);
            }

            return new RenderedBitmap(width, height, pixels);
        }
    }

    /// <summary>
    /// Shared display-list replay: optionally clears to opaque white, applies the
    /// viewport (scroll) transform, walks every <see cref="DisplayItem"/> through
    /// <see cref="Apply"/>, then optionally flushes the GPU timeline. The offscreen
    /// GPU and CPU renders both funnel through here so the exact same geometry
    /// reaches every destination. <paramref name="pendingImageSources"/> is owned by
    /// the caller and must outlive rasterization (DrawImage records the source and
    /// samples it when the canvas timeline executes).
    /// </summary>
    private void ReplayList(DrawingCanvas canvas, PaintList list, int width, int height, float scale, Matrix2D viewportTransform, bool clearWhite, DisposableBag pendingImageSources, bool flush)
    {
        if (clearWhite)
        {
            canvas.Clear(Brushes.Solid(Color.White), new Rectangle(0, 0, width, height));
        }

        var transforms = new Stack<Matrix2D>();
        transforms.Push(viewportTransform);
        canvas.Save(new DrawingOptions { Transform = ToCanvasMatrix(viewportTransform, scale) });

        var target = new LayoutRect(0, 0, width / scale, height / scale);
        var items = list.Items;
        // Use index-based loop so PushMask can jump past its matching PopMask.
        var idx = 0;
        while (idx < items.Count)
        {
            idx = ApplyAt(canvas, items, idx, scale, pendingImageSources, transforms, target);
        }

        canvas.Restore();

        if (flush)
        {
            using (StarlingTelemetry.Span("paint", "raster.flush"))
            {
                canvas.Flush();
            }
        }
    }

    /// <summary>
    /// Applies item at <paramref name="idx"/> to <paramref name="canvas"/> and
    /// returns the next index to process. For most items this is idx+1; for
    /// <see cref="PushMask"/> it is the index past the matching
    /// <see cref="PopMask"/> (the bracketed items are composited internally).
    /// </summary>
    private int ApplyAt(DrawingCanvas canvas, IReadOnlyList<DisplayItem> items, int idx, float scale,
        DisposableBag pendingImageSources, Stack<Matrix2D> transforms, LayoutRect target)
    {
        var item = items[idx];

        if (item is PushMask pushMask)
        {
            // Find the matching PopMask (balanced nesting handles nested masks).
            var depth = 1;
            var end = idx + 1;
            while (end < items.Count && depth > 0)
            {
                if (items[end] is PushMask)
                {
                    depth++;
                }
                else if (items[end] is PopMask)
                {
                    depth--;
                }

                end++;
            }
            // end now points past the PopMask (or at items.Count if unbalanced).
            // Render the bracketed slice into an offscreen layer, apply mask, blit.
            ApplyMaskGroup(canvas, items, idx + 1, end - 1, pushMask, scale, pendingImageSources, transforms, target);
            return end;
        }

        if (item is PushFilter pushFilter)
        {
            // Find the matching PopFilter (balanced nesting handles nested filters).
            var depth = 1;
            var end = idx + 1;
            while (end < items.Count && depth > 0)
            {
                if (items[end] is PushFilter)
                {
                    depth++;
                }
                else if (items[end] is PopFilter)
                {
                    depth--;
                }

                end++;
            }
            // Render the bracketed slice into an offscreen layer, run the
            // filter chain, blit the result (Filter Effects 1 §10).
            ApplyFilterGroup(canvas, items, idx + 1, end - 1, pushFilter, scale, pendingImageSources, transforms, target);
            return end;
        }

        Apply(canvas, item, scale, pendingImageSources, transforms, target);
        return idx + 1;
    }

    /// <summary>
    /// Renders display-list items from <paramref name="start"/> to
    /// <paramref name="end"/> (exclusive) into a transparent offscreen layer at
    /// the current viewport resolution, applies the mask from
    /// <paramref name="pushMask"/>, and blits the result through the current
    /// canvas transform. This implements CSS Masking 1 §5 whole-element
    /// compositing.
    /// </summary>
    private void ApplyMaskGroup(DrawingCanvas canvas, IReadOnlyList<DisplayItem> items, int start, int end,
        PushMask pushMask, float scale, DisposableBag pendingImageSources, Stack<Matrix2D> transforms, LayoutRect target)
    {
        var bounds = pushMask.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var px = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        var py = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        if ((long)px * py > 64L * 1024 * 1024)
        {
            return;
        }

        // Render the bracketed items into a transparent offscreen layer.
        // The layer uses a local coordinate system where (bounds.X, bounds.Y) maps
        // to device (0, 0); we achieve this by translating the existing viewport
        // transform to shift the box origin to (0, 0) within the layer.
        var layerOriginTranslate = Matrix2D.Translate(-bounds.X, -bounds.Y);
        var layerViewportTransform = layerOriginTranslate.Multiply(transforms.Peek());

        var contentLayer = new Image<Rgba32>(px, py, new Rgba32(0, 0, 0, 0));
        pendingImageSources.Add(contentLayer);

        contentLayer.Mutate(ctx => ctx.Paint(c =>
        {
            var layerTransforms = new Stack<Matrix2D>();
            layerTransforms.Push(layerViewportTransform);
            c.Save(new DrawingOptions { Transform = ToCanvasMatrix(layerViewportTransform, scale) });

            var i = start;
            while (i < end)
            {
                i = ApplyAt(c, items, i, scale, pendingImageSources, layerTransforms, target);
            }

            c.Restore();
        }));

        // Build the mask layer at the same device resolution.
        var tileW = pushMask.MaskRenderWidth * scale;
        var tileH = pushMask.MaskRenderHeight * scale;

        if (tileW > 0 && tileH > 0 && (pushMask.Mask is not null || pushMask.MaskGradient is not null))
        {
            var device = new RectangleF(0, 0, px, py);
            var maskLayer = new Image<Rgba32>(px, py, new Rgba32(0, 0, 0, 0));
            pendingImageSources.Add(maskLayer);

            if (pushMask.MaskGradient is { } maskGrad)
            {
                if (maskGrad.Kind == CssGradientKind.Conic)
                {
                    var cacheKey = new ConicCacheKey(maskGrad, px, py);
                    if (_conicCache.TryGet(cacheKey, out var conicCached))
                    {
                        maskLayer.Mutate(ctx => ctx.DrawImage(conicCached, new Point(0, 0), 1f));
                    }
                    else
                    {
                        FillConicPixels(maskLayer, maskGrad);
                        // Not added to the cache here because maskLayer is used as a
                        // mask (will be read but not mutated after this point, so it is
                        // safe to cache). However, the Put signature requires
                        // pendingImageSources which we have.
                        var masklayerCopy = new Image<Rgba32>(px, py, new Rgba32(0, 0, 0, 0));
                        pendingImageSources.Add(masklayerCopy);
                        masklayerCopy.Mutate(ctx => ctx.DrawImage(maskLayer, new Point(0, 0), 1f));
                        _conicCache.Put(cacheKey, masklayerCopy, pendingImageSources);
                    }
                }
                else
                {
                    maskLayer.Mutate(ctx => ctx.Paint(c =>
                    {
                        var brush = BuildGradientBrush(maskGrad, device);
                        if (brush is not null)
                        {
                            c.Fill(brush, new RectanglePolygon(device));
                        }
                    }));
                }
            }
            else
            {
                var mask = pushMask.Mask!;
                var maskSrc = Image.LoadPixelData<Rgba32>(mask.Pixels.Span, mask.Width, mask.Height);
                pendingImageSources.Add(maskSrc);
                var maskSrcRect = new Rectangle(0, 0, mask.Width, mask.Height);
                maskLayer.Mutate(ctx => ctx.Paint(c =>
                    TileMaskSource(c, maskSrc, maskSrcRect, tileW, tileH, px, py,
                        pushMask.MaskOffsetX * scale, pushMask.MaskOffsetY * scale,
                        pushMask.MaskRepeat)));
            }

            if (pushMask.Mode == MaskModeKind.Luminance)
            {
                MultiplyAlphaByLuminanceMask(contentLayer, maskLayer);
            }
            else
            {
                MultiplyAlphaByMask(contentLayer, maskLayer);
            }
        }

        // Corner-radius clipping.
        if (!pushMask.Radii.IsZero)
        {
            var deviceBounds = new LayoutRect(0, 0, px, py);
            var scaledRadii = ScaleRadii(pushMask.Radii, scale);
            var clipPath = BuildClipPath(deviceBounds, scaledRadii);
            ApplyClipMask(contentLayer, clipPath);
        }

        // Blit the composited layer at the box origin through the canvas transform.
        var destRect = new RectangleF((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height);
        canvas.DrawImage(contentLayer, new Rectangle(0, 0, px, py), destRect, _resampler);
    }

    /// <summary>
    /// Applies the specified <paramref name="item"/> to the given <paramref name="canvas"/>
    /// at the provided <paramref name="scale"/> using the transformation stack <paramref name="transforms"/>.
    /// Uses <paramref name="pendingImageSources"/> for managing temporary image assets.
    /// The operation is confined to the designated <paramref name="target"/> layout bounds.
    /// </summary>
    /// <param name="canvas">The drawing surface on which the display item is applied.</param>
    /// <param name="item">The visual element that will be rendered on the canvas.</param>
    /// <param name="scale">The scaling factor to adjust for rendering resolution.</param>
    /// <param name="pendingImageSources">A collection of disposable image resources used during the rendering process.</param>
    /// <param name="transforms">A stack of transformation matrices applied to the display item.</param>
    /// <param name="target">The layout bounds restricting the rendering area on the canvas.</param>
    private void Apply(DrawingCanvas canvas, DisplayItem item, float scale, DisposableBag pendingImageSources,
        Stack<Matrix2D> transforms, LayoutRect target)
    {
        switch (item)
        {
            case PushTransform push:
                var matrix = transforms.Peek().Multiply(push.Matrix);
                transforms.Push(matrix);
                canvas.Save(new DrawingOptions
                {
                    Transform = ToCanvasMatrix(matrix, scale),
                });

                break;
            case DisplayList.PopTransform:
                canvas.Restore();
                transforms.Pop();
                break;
            case PushClip pushClip:
                ApplyPushClip(canvas, pushClip, transforms.Peek(), scale);
                break;
            case DisplayList.PopClip:
                canvas.Restore();
                break;
            case PushClipPath pushClipPath:
                ApplyPushClipPath(canvas, pushClipPath, transforms.Peek(), scale);
                break;
            case DisplayList.PopClipPath:
                canvas.Restore();
                break;
            case FillRect fill:
                if (fill.Bounds.Width <= 0 || fill.Bounds.Height <= 0)
                {
                    return;
                }

                var rectPath = fill.PixelAlignment == FillRectPixelAlignment.SnapToDevicePixels
                    ? ToSnappedLayoutRectPath(fill.Bounds, scale)
                    : ToRectPath(fill.Bounds);

                canvas.Fill(Brushes.Solid(ToColor(fill.Color)), rectPath);
                break;
            case StrokeRect stroke:
                {
                    var penWidth = (float)stroke.Width;
                    var pen = Pens.Solid(ToColor(stroke.Color), penWidth);
                    var r = ToRectPath(stroke.Bounds);
                    canvas.Draw(pen, r);
                }
                break;
            case StrokeSegments segments:
                if (segments.Width <= 0 || segments.Color.A == 0)
                {
                    return;
                }
                {
                    // Form-control glyph stroke (checkbox check / select
                    // chevron): a three-point open polyline drawn with a solid
                    // pen. PathBuilder keeps it a single figure so the joint
                    // renders as one continuous stroke.
                    var pb = new PathBuilder();
                    pb.AddLine(new PointF((float)segments.X0, (float)segments.Y0), new PointF((float)segments.X1, (float)segments.Y1));
                    pb.AddLine(new PointF((float)segments.X1, (float)segments.Y1), new PointF((float)segments.X2, (float)segments.Y2));
                    canvas.Draw(Pens.Solid(ToColor(segments.Color), (float)segments.Width), pb.Build());
                }
                break;
            case FillRoundedRect roundFill:
                if (roundFill.Bounds.Width <= 0 || roundFill.Bounds.Height <= 0)
                {
                    return;
                }
                {
                    var path = BuildRoundedRectPath(roundFill.Bounds, roundFill.Radii);
                    canvas.Fill(Brushes.Solid(ToColor(roundFill.Color)), path);
                }
                break;
            case StrokeRoundedRect roundStroke:
                if (roundStroke.Bounds.Width <= 0 || roundStroke.Bounds.Height <= 0 || roundStroke.Width <= 0)
                {
                    return;
                }
                {
                    var path = BuildRoundedRectPath(roundStroke.Bounds, roundStroke.Radii);
                    canvas.Draw(Pens.Solid(ToColor(roundStroke.Color), (float)roundStroke.Width), path);
                }
                break;
            case DrawBorderSides borderSides:
                DrawBorderSides(canvas, borderSides);
                break;
            case DrawBoxShadow shadow:
                if (!BoxShadowIntersectsTarget(shadow, transforms.Peek(), target))
                {
                    return;
                }

                DrawBoxShadow(canvas, shadow, scale, transforms.Peek(), pendingImageSources);
                break;
            case DrawText text:
                DrawText(canvas, text);
                break;
            case DrawTextShadow shadow:
                DrawTextShadow(canvas, shadow, pendingImageSources);
                break;
            case DrawTextDecoration decoration:
                DrawTextDecoration(canvas, decoration);
                break;
            case DrawImage img:
                DrawImage(canvas, img, pendingImageSources);
                break;
            case FillGradient grad:
                FillGradient(canvas, grad, scale, pendingImageSources);
                break;
            case FillBackgroundTextClip clip:
                FillBackgroundTextClip(canvas, clip, scale, pendingImageSources);
                break;
            case FillMaskedBackground masked:
                FillMaskedBackground(canvas, masked, scale, pendingImageSources);
                break;
            // PushMask / PopMask are processed by ApplyAt / ApplyMaskGroup.
            // They should never arrive in the Apply dispatch (Apply is only called
            // for non-mask items), but guard defensively.
            case PushMask:
            case PopMask:
                break;
            // PushFilter / PopFilter are processed by ApplyAt / ApplyFilterGroup;
            // guard like the mask brackets.
            case PushFilter:
            case PopFilter:
                break;
            // backdrop-filter needs the pixels already painted UNDER the item,
            // which a recording DrawingCanvas cannot read back mid-replay. The
            // flat CPU path handles it via the segmented replay in
            // ReplayListCpuSegmented; here (offscreen groups and the WebGPU
            // canvas path) it is skipped — on the compositor paths the effect
            // rides the layer instead (CompositorLayer.BackdropFilters): the
            // blend stage snapshots the composited backdrop and filters it, so
            // a tile raster correctly paints nothing for this item.
            case DrawBackdropFilter:
                break;
        }
    }

    /// <summary>
    /// Pushes a (possibly rounded) rect clip onto the canvas state stack.
    /// <paramref name="composed"/> is the CSS-space transform in effect when
    /// the bracket opened — passed explicitly so the segmented backdrop replay
    /// can re-open a still-open bracket in a later Paint scope with the same
    /// matrix it was recorded with.
    /// </summary>
    private static void ApplyPushClip(DrawingCanvas canvas, PushClip pushClip, Matrix2D composed, float scale)
    {
        // The ImageSharp batcher's PrepareCommand does:
        //   path = path.Transform(drawingOptions.Transform)   // draw path → device px
        //   path = path.Clip(drawingOptions.ShapeOptions, clipPaths) // boolean op
        // So:
        // 1. The clip paths must be in device-pixel space (pre-transformed).
        // 2. ShapeOptions.BooleanOperation must be Intersection (not the default
        //    Difference) so the clip path RESTRICTS the draw area, not subtracts it.
        var currentMatrix = ToCanvasMatrix(composed, scale);
        // BooleanOperation.Intersection: keep only the part of the draw path that
        // falls inside the clip region.
        var clippingOptions = new DrawingOptions
        {
            Transform = currentMatrix,
            ShapeOptions = new ShapeOptions { BooleanOperation = BooleanOperation.Intersection },
        };
        if (pushClip.Bounds.Width > 0 && pushClip.Bounds.Height > 0)
        {
            var clipPath = BuildClipPath(pushClip.Bounds, pushClip.Radii)
                .Transform(currentMatrix);
            canvas.Save(clippingOptions, clipPath);
        }
        else
        {
            // Zero-size clip: nothing can paint through. Save a balanced state.
            canvas.Save(clippingOptions);
        }
    }

    /// <summary>
    /// CSS Masking 1 §7 — clip-path basic shapes. Resolve the shape against
    /// the element's reference box, build the IPath, transform it to device
    /// pixel space, and push an Intersection clip onto the canvas stack. Like
    /// <see cref="ApplyPushClip"/>, <paramref name="composed"/> is explicit so
    /// the segmented backdrop replay can re-open the bracket.
    /// </summary>
    private static void ApplyPushClipPath(DrawingCanvas canvas, PushClipPath pushClipPath, Matrix2D composed, float scale)
    {
        var currentMatrix = ToCanvasMatrix(composed, scale);
        var clippingOptions = new DrawingOptions
        {
            Transform = currentMatrix,
            ShapeOptions = new ShapeOptions { BooleanOperation = BooleanOperation.Intersection },
        };
        var shapePath = BuildBasicShapePath(pushClipPath.ClipPath, pushClipPath.ReferenceBox);
        if (shapePath is not null)
        {
            canvas.Save(clippingOptions, shapePath.Transform(currentMatrix));
        }
        else
        {
            // Degenerate or unsupported shape — save a balanced no-clip state.
            canvas.Save(clippingOptions);
        }
    }

    /// <summary>
    /// CSS Masking 1 §7 — resolve a <see cref="CssClipPath"/> to an ImageSharp
    /// <see cref="IPath"/> in page-coordinate CSS px. The path is then transformed
    /// to device pixels by the caller. Returns null when the shape is degenerate
    /// (zero radius, no vertices, …) or the clip-path is none/url (url is
    /// deferred; callers should not pass those cases here).
    /// <para>
    /// Reference box selection follows CSS Masking 1 §7 / CSS Shapes 1 §5:
    /// <list type="bullet">
    ///   <item>A geometry-box keyword present overrides the default.</item>
    ///   <item>Default for a basic shape with no box keyword is <c>border-box</c>.</item>
    ///   <item>A geometry-box keyword alone (no shape) clips to that box — built
    ///         as a plain rectangle (rounded-rect not yet supported for box-only).</item>
    /// </list>
    /// The paint layer only has the element's border-box (<paramref name="refBox"/>),
    /// so every geometry-box variant is mapped to border-box (padding-box / content-box
    /// distinctions require the padding/border metrics which are not carried on the
    /// display item — this is a known simplification for the initial implementation).
    /// </para>
    /// </summary>
    private static IPath? BuildBasicShapePath(CssClipPath clip, LayoutRect refBox)
    {
        if (clip.IsNone || clip.IsUrl)
        {
            return null;
        }

        var bx = (float)refBox.X;
        var by = (float)refBox.Y;
        var bw = (float)refBox.Width;
        var bh = (float)refBox.Height;

        // Geometry-box only (no explicit shape) — clip to the (currently simplified
        // to border-box) rectangle. Rounded corners per geometry-box are not yet
        // resolved here (requires carrying the radii alongside the clip-path value).
        if (clip.Shape is null)
        {
            if (bw <= 0 || bh <= 0)
            {
                return null;
            }

            return new RectanglePolygon(bx, by, bw, bh);
        }

        return clip.Shape switch
        {
            CssCircleShape circle => BuildCirclePath(circle, bx, by, bw, bh),
            CssEllipseShape ellipse => BuildEllipsePath(ellipse, bx, by, bw, bh),
            CssInsetShape inset => BuildInsetPath(inset, bx, by, bw, bh),
            CssPolygonShape polygon => BuildPolygonPath(polygon, bx, by, bw, bh),
            _ => null,
        };
    }

    /// <summary>
    /// Resolve a <see cref="CssLengthPercentage"/> against <paramref name="basis"/>
    /// (the relevant dimension of the reference box in CSS px). Calc values are
    /// approximated as zero (full calc evaluation is out of scope for the initial
    /// implementation; a zero fallback means the clip degrades gracefully).
    /// </summary>
    private static float ResolveLengthPct(CssLengthPercentage lp, float basis)
    {
        if (lp.IsPercentage)
        {
            return basis * (float)lp.Percentage / 100f;
        }

        if (lp.Length is { } l)
        {
            return (float)Starling.Layout.Block.BlockLayout.ToPx(l);
        }

        return 0f; // calc() fallback
    }

    /// <summary>
    /// circle( [radius]? [at position]? ) — CSS Shapes 1 §4.2.
    /// Radius keywords: closest-side (default when omitted), farthest-side.
    /// Position defaults to 50% 50%.
    /// </summary>
    private static EllipsePolygon? BuildCirclePath(CssCircleShape circle, float bx, float by, float bw, float bh)
    {
        if (bw <= 0 || bh <= 0)
        {
            return null;
        }

        var cx = bx + ResolveLengthPct(circle.Position.X, bw);
        var cy = by + ResolveLengthPct(circle.Position.Y, bh);

        float radius;
        if (circle.Radius is not null)
        {
            // Explicit length or percentage; percentage is relative to
            // sqrt((w²+h²)/2) per CSS Shapes 1 §4.2, but we use the simpler
            // "closest side from center" approximation when the reference is
            // not a percentage — use the dimension as the basis when it is.
            radius = ResolveLengthPct(circle.Radius, MathF.Sqrt((bw * bw + bh * bh) / 2f));
        }
        else
        {
            // Keyword or default: closest-side (default) or farthest-side.
            var keyword = circle.RadiusKeyword ?? "closest-side";
            radius = keyword.Equals("farthest-side", StringComparison.OrdinalIgnoreCase)
                ? Math.Max(Math.Max(cx - bx, bx + bw - cx), Math.Max(cy - by, by + bh - cy))
                : Math.Min(Math.Min(cx - bx, bx + bw - cx), Math.Min(cy - by, by + bh - cy));
        }

        if (radius <= 0)
        {
            return null;
        }
        // ImageSharp EllipsePolygon is centered at (cx, cy) with half-axes rx, ry.
        return new EllipsePolygon(cx, cy, radius, radius);
    }

    /// <summary>
    /// ellipse( [rx ry]? [at position]? ) — CSS Shapes 1 §4.3.
    /// Radius keywords: closest-side (default), farthest-side.
    /// </summary>
    private static EllipsePolygon? BuildEllipsePath(CssEllipseShape ellipse, float bx, float by, float bw, float bh)
    {
        if (bw <= 0 || bh <= 0)
        {
            return null;
        }

        var cx = bx + ResolveLengthPct(ellipse.Position.X, bw);
        var cy = by + ResolveLengthPct(ellipse.Position.Y, bh);

        float rx = ResolveShapeRadius(ellipse.RadiusX, ellipse.RadiusXKeyword, cx - bx, bx + bw - cx);
        float ry = ResolveShapeRadius(ellipse.RadiusY, ellipse.RadiusYKeyword, cy - by, by + bh - cy);

        if (rx <= 0 || ry <= 0)
        {
            return null;
        }

        return new EllipsePolygon(cx, cy, rx, ry);
    }

    /// <summary>
    /// Resolve a single ellipse/circle axis radius from an explicit
    /// <see cref="CssLengthPercentage"/> or a keyword, defaulting to closest-side.
    /// </summary>
    private static float ResolveShapeRadius(
        CssLengthPercentage? lp,
        string? keyword,
        float distToNearEdge,
        float distToFarEdge)
    {
        if (lp is not null)
        {
            return ResolveLengthPct(lp, distToNearEdge + distToFarEdge);
        }

        var kw = keyword ?? "closest-side";
        return kw.Equals("farthest-side", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(distToNearEdge, distToFarEdge)
            : Math.Min(distToNearEdge, distToFarEdge);
    }

    /// <summary>
    /// inset( top right bottom left [round border-radius]? ) — CSS Shapes 1 §4.1.
    /// Offsets are from the reference-box edges inward. Optional round clause
    /// adds per-corner radii.
    /// </summary>
    private static IPath? BuildInsetPath(CssInsetShape inset, float bx, float by, float bw, float bh)
    {
        if (bw <= 0 || bh <= 0)
        {
            return null;
        }

        var top = ResolveLengthPct(inset.Top, bh);
        var right = ResolveLengthPct(inset.Right, bw);
        var bottom = ResolveLengthPct(inset.Bottom, bh);
        var left = ResolveLengthPct(inset.Left, bw);

        var x = bx + left;
        var y = by + top;
        var w = bw - left - right;
        var h = bh - top - bottom;
        if (w <= 0 || h <= 0)
        {
            return null;
        }

        if (inset.Radii is null || inset.Radii.Count == 0)
        {
            return new RectanglePolygon(x, y, w, h);
        }

        // Map the parsed CssRadiusPair list (TL, TR, BR, BL) to CornerRadii.
        // Per §4.1 the radii are also clamped to the inset box dimensions.
        var pairs = inset.Radii;
        var tlH = ResolveLengthPct(pairs[0].H, w); var tlV = ResolveLengthPct(pairs[0].V, h);
        var trH = ResolveLengthPct(pairs[1].H, w); var trV = ResolveLengthPct(pairs[1].V, h);
        var brH = ResolveLengthPct(pairs[2].H, w); var brV = ResolveLengthPct(pairs[2].V, h);
        var blH = ResolveLengthPct(pairs[3].H, w); var blV = ResolveLengthPct(pairs[3].V, h);

        var radii = new CornerRadii(tlH, tlV, trH, trV, brH, brV, blH, blV);
        var insetBounds = new LayoutRect(x, y, w, h);
        return radii.IsZero ? new RectanglePolygon(x, y, w, h) : BuildRoundedRectPath(insetBounds, radii);
    }

    /// <summary>
    /// polygon( [fill-rule,]? vertex# ) — CSS Shapes 1 §4.4.
    /// Vertices are resolved as percentages / lengths against the reference box.
    /// ImageSharp's <see cref="Polygon"/> uses nonzero fill by default; for evenodd
    /// we fall back to the same path (a known limitation — ImageSharp Drawing 3
    /// does not expose per-fill-rule IPath construction; the fill rule is only
    /// observable for self-intersecting polygons).
    /// </summary>
    private static Polygon? BuildPolygonPath(CssPolygonShape polygon, float bx, float by, float bw, float bh)
    {
        if (polygon.Vertices.Count < 3)
        {
            return null;
        }

        var pts = new PointF[polygon.Vertices.Count];
        for (var i = 0; i < polygon.Vertices.Count; i++)
        {
            var v = polygon.Vertices[i];
            pts[i] = new PointF(bx + ResolveLengthPct(v.X, bw), by + ResolveLengthPct(v.Y, bh));
        }
        // Polygon closes the path automatically.
        return new Polygon(pts);
    }

    /// <summary>
    /// Rasterize a CSS Images 3/4 gradient sized to the fill box. Linear and
    /// radial gradients (and their <c>repeating-</c> variants) map onto an
    /// ImageSharp.Drawing gradient brush. ImageSharp has no conic/sweep brush,
    /// so conic gradients are painted per-pixel into an offscreen layer and
    /// blitted (see <see cref="FillConicGradient"/>). When the item carries
    /// non-zero <see cref="FillGradient.Radii"/> the gradient is clipped to the
    /// rounded rectangle (CSS Backgrounds 3 §5 border-radius).
    /// </summary>
    private void FillGradient(DrawingCanvas canvas, FillGradient item, float scale, DisposableBag pendingImageSources)
    {
        var bounds = item.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        if (item.Gradient.Kind == CssGradientKind.Conic)
        {
            FillConicGradient(canvas, item, scale, pendingImageSources);
            return;
        }

        // Linear/radial gradient: use an ImageSharp brush for the fill, then clip to
        // rounded corners via an offscreen layer when border-radius is non-zero.
        if (!item.Radii.IsZero)
        {
            FillGradientRounded(canvas, item, scale, pendingImageSources);
            return;
        }

        var brush = BuildGradientBrush(item.Gradient, ToRectF(bounds));
        if (brush is null)
        {
            return;
        }

        canvas.Fill(brush, ToRectPath(bounds));
    }

    /// <summary>
    /// Paint a linear/radial gradient clipped to a rounded rectangle. Renders the
    /// gradient into an offscreen layer, applies the rounded-rect clip mask
    /// via <see cref="ApplyClipMask"/>, then blits the result at the box origin.
    /// Used when <see cref="FillGradient.Radii"/> is non-zero.
    /// </summary>
    private void FillGradientRounded(DrawingCanvas canvas, FillGradient item, float scale, DisposableBag pendingImageSources)
    {
        var bounds = item.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        // Fill the gradient brush straight into a rounded-rect path on the canvas
        // — the same direct-fill path the solid FillRoundedRect uses. The brush
        // supplies the colour, the rounded path the shape, so the corners are
        // clipped without an offscreen layer. Filling on the canvas honours the
        // active clip stack (e.g. an `overflow: hidden` ancestor); the old
        // offscreen rasterize + DrawImage blit did NOT compose with that clip,
        // so a rounded gradient inside a clipped box (the classic pill-shaped
        // progress-bar fill) vanished.
        var brush = BuildGradientBrush(item.Gradient, ToRectF(bounds));
        if (brush is null)
        {
            return;
        }

        canvas.Fill(brush, BuildRoundedRectPath(bounds, item.Radii));
    }

    /// <summary>
    /// Paint a CSS Images 4 conic (sweep) gradient. ImageSharp.Drawing has no
    /// conic brush, so the gradient is rasterized per-pixel into a transparent
    /// offscreen layer at device resolution, then blitted to the box through the
    /// canvas transform (the same offscreen pattern <see cref="DrawBoxShadow"/>
    /// and <see cref="FillBackgroundTextClip"/> use). The hue sweeps clockwise
    /// from <c>from &lt;angle&gt;</c> (0deg = straight up) about the
    /// <c>at &lt;position&gt;</c> center. The rasterized layer is cached by
    /// (gradient identity, device width, height) so repeated frames reuse it
    /// without re-rasterizing. When the item carries non-zero
    /// <see cref="FillGradient.Radii"/> the layer is clipped to rounded corners.
    /// </summary>
    private void FillConicGradient(DrawingCanvas canvas, FillGradient item, float scale, DisposableBag pendingImageSources)
    {
        var gradient = item.Gradient;
        if (gradient.Stops.Count(s => !s.IsHint) < 2)
        {
            return;
        }

        var bounds = item.Bounds;
        var pw = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        var ph = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        // Guard against pathological sizes that would allocate an enormous layer.
        if ((long)pw * ph > 64L * 1024 * 1024)
        {
            return;
        }

        // Use the cache for the raw (unclipped) conic layer.
        var cacheKey = new ConicCacheKey(gradient, pw, ph);
        if (!_conicCache.TryGet(cacheKey, out var layer))
        {
            layer = new Image<Rgba32>(pw, ph, new Rgba32(0, 0, 0, 0));
            FillConicPixels(layer, gradient);
            _conicCache.Put(cacheKey, layer, pendingImageSources);
        }

        // When there are rounded corners, we must not mutate the cached layer —
        // blit it into a fresh layer, apply the clip, and blit that.
        if (!item.Radii.IsZero)
        {
            var clipped = new Image<Rgba32>(pw, ph, new Rgba32(0, 0, 0, 0));
            pendingImageSources.Add(clipped);
            clipped.Mutate(ctx => ctx.DrawImage(layer, new Point(0, 0), 1f));
            var deviceBounds = new LayoutRect(0, 0, pw, ph);
            var scaledRadii = ScaleRadii(item.Radii, scale);
            ApplyClipMask(clipped, BuildClipPath(deviceBounds, scaledRadii));
            var destRect2 = new RectangleF((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height);
            canvas.DrawImage(clipped, new Rectangle(0, 0, pw, ph), destRect2, _resampler);
            return;
        }

        // DrawImage records the blit and samples the layer when the canvas
        // timeline executes, so the layer must outlive this call — it is kept
        // alive by the cache.
        var destRect = new RectangleF((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height);
        canvas.DrawImage(layer, new Rectangle(0, 0, pw, ph), destRect, _resampler);
    }

    /// <summary>
    /// Rasterize a conic gradient across the whole of <paramref name="layer"/>
    /// (which represents the fill box at device resolution). Shared by the plain
    /// conic background fill and the masked-background path so both sweep the hue
    /// identically.
    /// <para>
    /// Antialiasing: 2×2 jittered supersample per pixel. Each pixel takes four
    /// quarter-pixel samples at offsets (±0.25, ±0.25) and averages them in
    /// premultiplied space. This eliminates aliased hard edges at stop boundaries
    /// and the seam at the origin angle. The cost is 4× the per-pixel work, which
    /// is still negligible vs the per-frame layer allocation.
    /// </para>
    /// <para>
    /// Color interpolation: honors <c>CssGradient.Interpolation</c> for the
    /// conic per-pixel path. Oklab, OKLCh, HSL, HWB, Lab, LCH and all other
    /// CSS Color 4 spaces are supported. Linear/radial gradients use the
    /// ImageSharp brush and are documented as an sRGB fallback there.
    /// </para>
    /// <para>
    /// Transition hints: <see cref="CssColorStop.IsHint"/> entries between two
    /// real stops shift the interpolation midpoint by applying a power-curve per
    /// CSS Images 4 §3.4.
    /// </para>
    /// </summary>
    private static void FillConicPixels(Image<Rgba32> layer, CssGradient gradient)
    {
        var pw = layer.Width;
        var ph = layer.Height;

        // Stop ratios are fractions of one full turn; percentages and angle
        // stops both resolve through ResolveStopRatios (line length is unused
        // for percent/angle, so pass the nominal 360deg turn).
        var ratios = ResolveStopRatios(gradient, 360.0);
        var n = ratios.Length;

        // Choose the color space to interpolate in.
        var cs = gradient.Interpolation?.ColorSpace ?? GradientColorSpace.Srgb;
        var hueMethod = gradient.Interpolation?.HueMethod ?? HueInterpolationMethod.Shorter;

        // Precompute stop channels in the chosen interpolation space, stored as
        // 4-component (ch0,ch1,ch2,alpha) doubles. For premultiplied spaces the
        // color channels are pre-multiplied by alpha before interpolation.
        var ch0 = new double[n];
        var ch1 = new double[n];
        var ch2 = new double[n];
        var al = new double[n];

        for (var i = 0; i < n; i++)
        {
            if (gradient.Stops[i].IsHint)
            {
                // Transition hint — channels are dummy; only the ratio matters.
                continue;
            }
            var c = gradient.Stops[i].Color.ToSrgb();
            var a = c.A / 255.0;
            var r = c.R / 255.0;
            var g = c.G / 255.0;
            var b = c.B / 255.0;
            al[i] = a;
            (ch0[i], ch1[i], ch2[i]) = GradientInterpolation.SrgbToSpace(r, g, b, cs);
            // Pre-multiply color channels by alpha for perceptually-correct blending.
            // Hue channels in polar spaces are NOT premultiplied (they are angles).
            if (!GradientInterpolation.IsPolarSpace(cs))
            {
                ch0[i] *= a;
                ch1[i] *= a;
                ch2[i] *= a;
            }
        }

        // Build the hint adjacency table: for each stop index that is a hint,
        // record its position ratio (already in ratios[]).
        var hasHints = gradient.Stops.Any(s => s.IsHint);

        var pos = gradient.Position ?? CssGradientPosition.Center;
        var cx = pos.FractionX * pw;
        var cy = pos.FractionY * ph;
        var fromRad = (gradient.Line?.AngleDegrees ?? 0.0) * Math.PI / 180.0;
        var repeating = gradient.Repeating;
        var firstRatio = ratios[0];
        var lastRatio = ratios[n - 1];
        var period = lastRatio - firstRatio;

        // 2×2 supersample offsets (quarter-pixel jitter grid).
        const double A0 = -0.25, A1 = 0.25;

        layer.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < ph; y++)
            {
                var row = accessor.GetRowSpan(y);
                var baseY = y + 0.5 - cy;

                for (var x = 0; x < pw; x++)
                {
                    var baseX = x + 0.5 - cx;

                    // 2×2 supersample: accumulate 4 samples.
                    double sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                    for (var sy = 0; sy < 2; sy++)
                    {
                        var dy = baseY + (sy == 0 ? A0 : A1);
                        for (var sx = 0; sx < 2; sx++)
                        {
                            var dx = baseX + (sx == 0 ? A0 : A1);
                            var t = ComputeConicT(dx, dy, fromRad, repeating, firstRatio, lastRatio, period);
                            var (sr, sg, sb, sa) = SampleStopsColor(t, ratios, ch0, ch1, ch2, al, cs, hueMethod, hasHints, gradient.Stops);
                            sumR += sr; sumG += sg; sumB += sb; sumA += sa;
                        }
                    }
                    // Average the 4 samples.
                    row[x] = ToStraightAlpha(sumR * 0.25, sumG * 0.25, sumB * 0.25, sumA * 0.25, cs);
                }
            }
        });
    }

    /// <summary>
    /// Compute the conic gradient turn fraction for position (dx, dy) relative
    /// to the center. Applies the repeating/clamping logic from the spec.
    /// </summary>
    private static double ComputeConicT(double dx, double dy, double fromRad, bool repeating, double firstRatio, double lastRatio, double period)
    {
        var ang = Math.Atan2(dx, -dy) - fromRad;
        var t = ang / (2.0 * Math.PI);
        t -= Math.Floor(t); // normalize to [0,1)

        if (repeating && period > 0)
        {
            t = firstRatio + (t - firstRatio - Math.Floor((t - firstRatio) / period) * period);
        }
        else if (t <= firstRatio)
        {
            t = firstRatio;
        }
        else if (t >= lastRatio)
        {
            t = lastRatio;
        }

        return t;
    }

    /// <summary>
    /// Sample the gradient stops at fraction <paramref name="t"/>, interpolating
    /// in the requested color space and applying transition hints. Returns
    /// (ch0, ch1, ch2, alpha) in the working space (premultiplied for
    /// non-polar spaces).
    /// </summary>
    private static (double ch0, double ch1, double ch2, double a) SampleStopsColor(
        double t,
        double[] ratios,
        double[] ch0, double[] ch1, double[] ch2, double[] al,
        GradientColorSpace cs,
        HueInterpolationMethod hueMethod,
        bool hasHints,
        IReadOnlyList<CssColorStop> stops)
    {
        var n = ratios.Length;

        // Find the high-side real stop index (skip hints for bracketing).
        var hi = 1;
        while (hi < n - 1 && ratios[hi] < t)
        {
            hi++;
        }
        // Step hi past any hint to find the next real stop.
        while (hi < n - 1 && stops[hi].IsHint)
        {
            hi++;
        }

        var lo = hi - 1;
        // Step lo back past any hint to the previous real stop.
        while (lo > 0 && stops[lo].IsHint)
        {
            lo--;
        }

        var span = ratios[hi] - ratios[lo];
        var f = span > 0 ? (t - ratios[lo]) / span : 0.0;
        f = Math.Clamp(f, 0.0, 1.0);

        // CSS Images 4 §3.4 — apply transition hint curve if there is a hint
        // between lo and hi.
        if (hasHints)
        {
            for (var k = lo + 1; k < hi; k++)
            {
                if (!stops[k].IsHint)
                {
                    continue;
                }
                // The hint's normalized position within the [lo..hi] span.
                var hintSpan = span > 0 ? (ratios[k] - ratios[lo]) / span : 0.5;
                hintSpan = Math.Clamp(hintSpan, 0.0001, 0.9999);
                // Apply the log-based power curve: f' = f^(log(0.5)/log(H)).
                // This passes through (0,0), (H,0.5), (1,1).
                var logH = Math.Log(hintSpan);
                if (Math.Abs(logH) > 1e-10)
                {
                    f = Math.Pow(f, Math.Log(0.5) / logH);
                }

                break; // only one hint per interval
            }
        }

        // Interpolate in the working space.
        if (GradientInterpolation.IsPolarSpace(cs))
        {
            // Polar: interpolate the hue angle accounting for hue-interpolation-method,
            // interpolate chroma/lightness linearly, average alpha.
            var hLo = ch2[lo]; // hue in degrees
            var hHi = ch2[hi];
            var hInterp = GradientInterpolation.InterpolateHue(hLo, hHi, f, hueMethod);
            var c0 = ch0[lo] + (ch0[hi] - ch0[lo]) * f; // L or similar
            var c1 = ch1[lo] + (ch1[hi] - ch1[lo]) * f; // C or S
            var a = al[lo] + (al[hi] - al[lo]) * f;
            return (c0, c1, hInterp, a);
        }
        else
        {
            // Non-polar: premultiplied linear interpolation.
            var c0 = ch0[lo] + (ch0[hi] - ch0[lo]) * f;
            var c1 = ch1[lo] + (ch1[hi] - ch1[lo]) * f;
            var c2 = ch2[lo] + (ch2[hi] - ch2[lo]) * f;
            var a = al[lo] + (al[hi] - al[lo]) * f;
            return (c0, c1, c2, a);
        }
    }

    /// <summary>
    /// Convert a straight-RGBA sRGB value (0..1 each) to the requested
    /// interpolation color space. Returns (ch0, ch1, ch2) in that space.
    /// </summary>
    /// <summary>
    /// Convert working-space (ch0, ch1, ch2, alpha) back to straight-alpha
    /// <see cref="Rgba32"/>. The color channels are premultiplied going in
    /// (for non-polar spaces), so un-premultiply before output.
    /// </summary>
    private static Rgba32 ToStraightAlpha(double c0, double c1, double c2, double a, GradientColorSpace cs)
    {
        double r, g, b;
        if (GradientInterpolation.IsPolarSpace(cs))
        {
            // Polar: channels are (L, C/S, H) — not premultiplied.
            (r, g, b) = GradientInterpolation.SpaceToSrgb(c0, c1, c2, cs);
        }
        else
        {
            // Un-premultiply.
            if (a > 1e-6)
            {
                c0 /= a;
                c1 /= a;
                c2 /= a;
            }
            (r, g, b) = GradientInterpolation.SpaceToSrgb(c0, c1, c2, cs);
        }
        static byte Clamp255(double v) => (byte)Math.Clamp(Math.Round(v * 255.0), 0, 255);
        return new Rgba32(Clamp255(r), Clamp255(g), Clamp255(b), (byte)Math.Clamp(Math.Round(a * 255.0), 0, 255));
    }

    /// <summary>
    /// Sample a premultiplied color at turn fraction <paramref name="t"/> across
    /// the resolved conic stops, returning a straight-alpha <see cref="Rgba32"/>.
    /// Ratios are sorted non-decreasing; equal-position stops produce a hard
    /// transition. Legacy path: only used when the calling code explicitly wants
    /// sRGB premultiplied interpolation without the 2×2 AA path.
    /// </summary>
    private static Rgba32 SampleStops(double t, double[] ratios, double[] rp, double[] gp, double[] bp, double[] ap)
    {
        var n = ratios.Length;
        var hi = 1;
        while (hi < n - 1 && ratios[hi] < t)
        {
            hi++;
        }

        var lo = hi - 1;

        var span = ratios[hi] - ratios[lo];
        var f = span > 0 ? (t - ratios[lo]) / span : 0.0;
        f = Math.Clamp(f, 0.0, 1.0);

        var a = ap[lo] + (ap[hi] - ap[lo]) * f;
        var rPre = rp[lo] + (rp[hi] - rp[lo]) * f;
        var gPre = gp[lo] + (gp[hi] - gp[lo]) * f;
        var bPre = bp[lo] + (bp[hi] - bp[lo]) * f;

        // Un-premultiply: rPre = R*A/255, so R = rPre*255/A.
        byte r = 0, g = 0, b = 0;
        if (a > 0)
        {
            r = ToByte(rPre * 255.0 / a);
            g = ToByte(gPre * 255.0 / a);
            b = ToByte(bPre * 255.0 / a);
        }
        return new Rgba32(r, g, b, ToByte(a));

        static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
    }

    /// <summary>
    /// Build the ImageSharp gradient brush for <paramref name="gradient"/> sized
    /// to <paramref name="bounds"/> (CSS Images 3 §3). Returns null when the
    /// gradient is degenerate (fewer than two stops, zero radius). Shared by the
    /// plain <see cref="FillGradient"/> fill and the
    /// <c>background-clip: text</c> path.
    /// </summary>
    private static Brush? BuildGradientBrush(CssGradient gradient, RectangleF bounds)
    {
        if (gradient.Stops.Count < 2)
        {
            return null;
        }
        // Conic gradients have no ImageSharp brush; they are rasterized directly
        // in FillConicGradient. Callers that can only use a brush (e.g.
        // background-clip: text) get null and fall back to the solid color.
        if (gradient.Kind == CssGradientKind.Conic)
        {
            return null;
        }

        var stops = ResolveColorStops(gradient, Math.Max(bounds.Width, bounds.Height));
        if (stops.Length < 2)
        {
            return null;
        }

        var repetition = gradient.Repeating
            ? GradientRepetitionMode.Repeat
            : GradientRepetitionMode.None;

        var x = bounds.X;
        var y = bounds.Y;
        var w = bounds.Width;
        var h = bounds.Height;

        if (gradient.Kind == CssGradientKind.Radial)
        {
            var pos = gradient.Position ?? CssGradientPosition.Center;
            var cx = x + (float)(pos.FractionX * w);
            var cy = y + (float)(pos.FractionY * h);
            var radius = RadialRadius(gradient, pos, w, h);
            if (radius <= 0)
            {
                return null;
            }

            return new RadialGradientBrush(new PointF(cx, cy), radius, repetition, stops);
        }

        // Linear: map the CSS gradient angle to two endpoints on the box.
        // CSS measures angle clockwise from "to top" (0deg = up). The
        // gradient line passes through the box center; endpoints sit where
        // it meets the box edge, with the start offset so the full color
        // range covers the projected box extent (CSS Images 3 §3.1).
        var angleDeg = gradient.Line?.ToDegrees(w, h) ?? 180.0;
        var rad = angleDeg * Math.PI / 180.0;
        // Direction the gradient progresses (toward the end color).
        var dx = Math.Sin(rad);
        var dy = -Math.Cos(rad);
        // Length of the projected gradient line: |w·sin| + |h·cos| keeps the
        // 0%/100% stops anchored to the box corners for the cardinal cases.
        var lineLen = Math.Abs(w * dx) + Math.Abs(h * dy);
        var ccx = x + w / 2.0;
        var ccy = y + h / 2.0;
        var p0 = new PointF((float)(ccx - dx * lineLen / 2.0), (float)(ccy - dy * lineLen / 2.0));
        var p1 = new PointF((float)(ccx + dx * lineLen / 2.0), (float)(ccy + dy * lineLen / 2.0));
        return new LinearGradientBrush(p0, p1, repetition, stops);
    }

    /// <summary>
    /// CSS Backgrounds 3 §3.8 — paint a box background clipped to its text
    /// glyphs (<c>background-clip: text</c>). Renders the background (gradient or
    /// solid color) into one offscreen layer and the element's glyphs (solid
    /// white) into a second, then multiplies the background's alpha by the glyph
    /// alpha so the background survives only inside the glyph shapes, and
    /// composites the masked result onto the canvas. Follows the offscreen
    /// pattern in <see cref="DrawTextShadow"/>.
    /// <para>
    /// Conic gradients: previously <see cref="BuildGradientBrush"/> returned null
    /// for conic (no ImageSharp brush), causing a fallback to the solid text color.
    /// The fix rasterizes the conic directly via <see cref="FillConicPixels"/> into
    /// the fill layer (the same technique <see cref="FillMaskedBackground"/> uses).
    /// </para>
    /// </summary>
    private void FillBackgroundTextClip(DrawingCanvas canvas, FillBackgroundTextClip clip, float scale, DisposableBag pendingImageSources)
    {
        var bounds = clip.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0 || clip.Glyphs.Count == 0)
        {
            return;
        }

        // Render the offscreen layers at device resolution for crisp glyph
        // edges; DrawImage blits them back through the canvas transform.
        var px = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        var py = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        // Guard against pathological sizes that would allocate an enormous layer.
        if ((long)px * py > 64L * 1024 * 1024)
        {
            return;
        }

        var device = new RectangleF(0, 0, px, py);
        var originX = (float)bounds.X;
        var originY = (float)bounds.Y;
        var scaleMatrix = Matrix4x4.CreateScale(scale, scale, 1f);

        // Fill layer: paint the gradient or solid color at device resolution.
        var fillLayer = new Image<Rgba32>(px, py, new Rgba32(0, 0, 0, 0));
        pendingImageSources.Add(fillLayer);

        if (clip.Gradient is { } gradient)
        {
            if (gradient.Kind == CssGradientKind.Conic)
            {
                // Conic has no ImageSharp brush; rasterize per-pixel into the fill
                // layer (the same path FillMaskedBackground uses). The fill layer
                // is already device-size, so FillConicPixels works directly.
                var cacheKey = new ConicCacheKey(gradient, px, py);
                if (_conicCache.TryGet(cacheKey, out var cached))
                {
                    // Blit cached layer into the fill layer.
                    fillLayer.Mutate(ctx => ctx.DrawImage(cached, new Point(0, 0), 1f));
                }
                else
                {
                    FillConicPixels(fillLayer, gradient);
                    // No cache store here — the fill layer will be mutated by the
                    // text mask below, making it unsuitable for caching.
                }
            }
            else
            {
                var layerBounds = new RectangleF(0, 0, (float)bounds.Width, (float)bounds.Height);
                var brush = BuildGradientBrush(gradient, layerBounds);
                if (brush is not null)
                {
                    fillLayer.Mutate(ctx => ctx.Paint(c =>
                    {
                        c.Save(new DrawingOptions { Transform = scaleMatrix });
                        c.Fill(brush, new RectanglePolygon(layerBounds));
                        c.Restore();
                    }));
                }
                else if (clip.Color.A > 0)
                {
                    fillLayer.Mutate(ctx => ctx.Paint(c =>
                        c.Fill(Brushes.Solid(ToColor(clip.Color)), new RectanglePolygon(device))));
                }
            }
        }
        else
        {
            if (clip.Color.A == 0)
            {
                return;
            }

            fillLayer.Mutate(ctx => ctx.Paint(c =>
                c.Fill(Brushes.Solid(ToColor(clip.Color)), new RectanglePolygon(device))));
        }

        // Glyph mask layer: render each text run in solid white at device resolution.
        var maskLayer = new Image<Rgba32>(px, py, new Rgba32(0, 0, 0, 0));
        pendingImageSources.Add(maskLayer);

        var glyphBrush = Brushes.Solid(Color.White);
        maskLayer.Mutate(ctx => ctx.Paint(c =>
        {
            c.Save(new DrawingOptions { Transform = scaleMatrix });
            foreach (var run in clip.Glyphs)
            {
                if (string.IsNullOrEmpty(run.Text))
                {
                    continue;
                }

                var spec = new FontSpec(run.FontFamilies, run.Bold, run.Italic);
                var size = (float)run.FontSize;
                var probe = FirstCodepoint(run.Text);
                var font = ResolveFont(spec, probe, size);
                var glyphX = (float)(run.X - bounds.X);
                var glyphY = (float)(run.Y - bounds.Y);
                var textBlock = run.Shaped is ImageSharpShapedRun shaped && shaped.CanReuseAtSize(size)
                    ? shaped.TextBlock
                    : new TextBlock(run.Text, new TextOptions(font));
                c.DrawText(textBlock, new PointF(glyphX, glyphY), -1, glyphBrush, null);
            }
            c.Restore();
        }));

        MultiplyAlphaByMask(fillLayer, maskLayer);

        // Composite the masked layer at the box origin. DrawImage applies the
        // canvas transform (which includes `scale`), so map the device-pixel
        // layer back to the CSS-px box rectangle.
        var destRect = new RectangleF(originX, originY, (float)bounds.Width, (float)bounds.Height);
        canvas.DrawImage(fillLayer, new Rectangle(0, 0, px, py), destRect, _resampler);
    }

    /// <summary>
    /// CSS Masking 1 §6 — paint a box background masked by a mask source
    /// (<c>mask-image</c>). The background (conic/linear/radial gradient,
    /// background image, or solid color) is rendered into one offscreen layer;
    /// the mask source (raster image or CSS gradient) is tiled/sized/positioned
    /// into a second; the background's alpha is multiplied by the resolved mask
    /// value (alpha channel for <c>alpha</c>/<c>match-source</c>; linearised
    /// luminance for <c>luminance</c>); the result is clipped to the box's
    /// corner radii and blitted back through the canvas transform. Mirrors the
    /// offscreen pattern in <see cref="FillBackgroundTextClip"/>.
    /// </summary>
    private void FillMaskedBackground(DrawingCanvas canvas, FillMaskedBackground item, float scale, DisposableBag pendingImageSources)
    {
        var bounds = item.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }
        // Must have exactly one mask source.
        if (item.Mask is null && item.MaskGradient is null)
        {
            return;
        }

        var px = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
        var py = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        if ((long)px * py > 64L * 1024 * 1024)
        {
            return;
        }

        var device = new RectangleF(0, 0, px, py);

        // Background layer (device resolution). Paint the solid color first, then
        // a background image, then a gradient on top — the same bottom-to-top
        // order as `background`. The builder sets at most one of gradient/image,
        // so this is layering-correct.
        var fillLayer = new Image<Rgba32>(px, py, new Rgba32(0, 0, 0, 0));
        pendingImageSources.Add(fillLayer);
        fillLayer.Mutate(ctx => ctx.Paint(c =>
        {
            if (item.Color.A > 0)
            {
                c.Fill(Brushes.Solid(ToColor(item.Color)), new RectanglePolygon(device));
            }

            if (item.BackgroundImage is { Width: > 0, Height: > 0 } bg)
            {
                var bgSrc = Image.LoadPixelData<Rgba32>(bg.Pixels.Span, bg.Width, bg.Height);
                pendingImageSources.Add(bgSrc);
                c.DrawImage(bgSrc, new Rectangle(0, 0, bg.Width, bg.Height), device, _resampler);
            }

            if (item.Gradient is { } gradient)
            {
                if (gradient.Kind == CssGradientKind.Conic)
                {
                    // Conic has no ImageSharp brush; reuse the cache if available,
                    // otherwise rasterize into a temp layer and composite it
                    // (preserves transparent stops over the color/image beneath).
                    var cacheKey = new ConicCacheKey(gradient, px, py);
                    if (_conicCache.TryGet(cacheKey, out var conicCached))
                    {
                        c.DrawImage(conicCached, new Rectangle(0, 0, px, py), device, _resampler);
                    }
                    else
                    {
                        var conic = new Image<Rgba32>(px, py, new Rgba32(0, 0, 0, 0));
                        pendingImageSources.Add(conic);
                        FillConicPixels(conic, gradient);
                        _conicCache.Put(cacheKey, conic, pendingImageSources);
                        c.DrawImage(conic, new Rectangle(0, 0, px, py), device, _resampler);
                    }
                }
                else
                {
                    var brush = BuildGradientBrush(gradient, device);
                    if (brush is not null)
                    {
                        c.Fill(brush, new RectanglePolygon(device));
                    }
                }
            }
        }));

        // Mask layer (device resolution): render the mask source into it.
        var tileW = item.MaskRenderWidth * scale;
        var tileH = item.MaskRenderHeight * scale;
        if (tileW <= 0 || tileH <= 0)
        {
            return;
        }

        var maskLayer = new Image<Rgba32>(px, py, new Rgba32(0, 0, 0, 0));
        pendingImageSources.Add(maskLayer);

        if (item.MaskGradient is { } maskGrad)
        {
            // Gradient mask source — rasterize directly into maskLayer (no tiling;
            // gradients are always sized to the mask area per CSS Masking 1 §6.5).
            if (maskGrad.Kind == CssGradientKind.Conic)
            {
                FillConicPixels(maskLayer, maskGrad);
            }
            else
            {
                maskLayer.Mutate(ctx => ctx.Paint(c =>
                {
                    var brush = BuildGradientBrush(maskGrad, device);
                    if (brush is not null)
                    {
                        c.Fill(brush, new RectanglePolygon(device));
                    }
                }));
            }
        }
        else
        {
            // Raster mask source — tile/position into maskLayer.
            var mask = item.Mask!;
            var maskSrc = Image.LoadPixelData<Rgba32>(mask.Pixels.Span, mask.Width, mask.Height);
            pendingImageSources.Add(maskSrc);
            var maskSrcRect = new Rectangle(0, 0, mask.Width, mask.Height);

            maskLayer.Mutate(ctx => ctx.Paint(c =>
                TileMaskSource(c, maskSrc, maskSrcRect, tileW, tileH, px, py,
                    item.MaskOffsetX * scale, item.MaskOffsetY * scale, item.MaskRepeat)));
        }

        // Apply mask: multiply fill alpha by the resolved mask channel.
        // CSS Masking 1 §6.1: alpha uses alpha directly; luminance converts
        // each pixel's linear luminance to the mask value (match-source == alpha
        // for decoded raster images).
        if (item.Mode == MaskModeKind.Luminance)
        {
            MultiplyAlphaByLuminanceMask(fillLayer, maskLayer);
        }
        else
        {
            MultiplyAlphaByMask(fillLayer, maskLayer);
        }

        // CSS Backgrounds 3 §5 — clip the masked result to the box's corner
        // radii so descendants respect border-radius clipping.
        if (!item.Radii.IsZero)
        {
            // The fill layer is in device-pixel space with origin at (0,0)
            // representing the box top-left. Scale the bounds to device px.
            var deviceBounds = new LayoutRect(0, 0, px, py);
            var scaledRadii = ScaleRadii(item.Radii, scale);
            var clipPath = BuildClipPath(deviceBounds, scaledRadii);

            // Mask out pixels outside the rounded rect by zeroing alpha where
            // the clip path does not contain the pixel centre.
            ApplyClipMask(fillLayer, clipPath);
        }

        var destRect = new RectangleF((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height);
        canvas.DrawImage(fillLayer, new Rectangle(0, 0, px, py), destRect, _resampler);
    }

    /// <summary>
    /// Tiles a mask source image (<paramref name="src"/>) into the active
    /// <paramref name="canvas"/> according to <paramref name="mode"/>. The source
    /// rectangle is <paramref name="srcRect"/>; the rendered tile size is
    /// (<paramref name="tileW"/>, <paramref name="tileH"/>); the canvas is
    /// (<paramref name="canvasW"/> × <paramref name="canvasH"/>) device pixels.
    /// Offsets are already in device pixels. Shared by
    /// <see cref="FillMaskedBackground"/> and <see cref="ApplyMaskGroup"/>.
    /// </summary>
    private void TileMaskSource(DrawingCanvas canvas, Image<Rgba32> src, Rectangle srcRect,
        double tileW, double tileH, int canvasW, int canvasH,
        double offsetX, double offsetY, MaskRepeatMode mode)
    {
        switch (mode)
        {
            case MaskRepeatMode.NoRepeat:
                {
                    canvas.DrawImage(src, srcRect, new RectangleF((float)offsetX, (float)offsetY, (float)tileW, (float)tileH), _resampler);
                    break;
                }
            case MaskRepeatMode.Space:
                {
                    // CSS Masking 1 §6.5 space: fit whole tiles, spread the leftover
                    // evenly as gaps. If only 1 tile fits, paint it at offset = 0.
                    var countX = Math.Max(1, (int)Math.Floor(canvasW / tileW));
                    var countY = Math.Max(1, (int)Math.Floor(canvasH / tileH));
                    var gapX = countX > 1 ? (canvasW - countX * tileW) / (countX - 1) : 0;
                    var gapY = countY > 1 ? (canvasH - countY * tileH) / (countY - 1) : 0;
                    for (var iy = 0; iy < countY; iy++)
                    {
                        for (var ix = 0; ix < countX; ix++)
                        {
                            var tx = (float)(ix * (tileW + gapX));
                            var ty = (float)(iy * (tileH + gapY));
                            canvas.DrawImage(src, srcRect, new RectangleF(tx, ty, (float)tileW, (float)tileH), _resampler);
                        }
                    }

                    break;
                }
            case MaskRepeatMode.Round:
                {
                    // CSS Masking 1 §6.5 round: stretch tiles so an integer count
                    // fills the box exactly; no gaps.
                    var countX = Math.Max(1, (int)Math.Round(canvasW / tileW));
                    var countY = Math.Max(1, (int)Math.Round(canvasH / tileH));
                    var stretchW = (float)(canvasW / (double)countX);
                    var stretchH = (float)(canvasH / (double)countY);
                    for (var iy = 0; iy < countY; iy++)
                    {
                        for (var ix = 0; ix < countX; ix++)
                        {
                            canvas.DrawImage(src, srcRect, new RectangleF(ix * stretchW, iy * stretchH, stretchW, stretchH), _resampler);
                        }
                    }

                    break;
                }
            case MaskRepeatMode.RepeatX:
                {
                    var dy = (float)offsetY;
                    var tilesX = (long)Math.Ceiling(canvasW / tileW) + 2;
                    if (tilesX > 1_000_000)
                    {
                        break;
                    }

                    var startX = offsetX % tileW;
                    if (startX > 0)
                    {
                        startX -= tileW;
                    }

                    for (var tx = startX; tx < canvasW; tx += tileW)
                    {
                        canvas.DrawImage(src, srcRect, new RectangleF((float)tx, dy, (float)tileW, (float)tileH), _resampler);
                    }

                    break;
                }
            case MaskRepeatMode.RepeatY:
                {
                    var dx = (float)offsetX;
                    var tilesY = (long)Math.Ceiling(canvasH / tileH) + 2;
                    if (tilesY > 1_000_000)
                    {
                        break;
                    }

                    var startY = offsetY % tileH;
                    if (startY > 0)
                    {
                        startY -= tileH;
                    }

                    for (var ty = startY; ty < canvasH; ty += tileH)
                    {
                        canvas.DrawImage(src, srcRect, new RectangleF(dx, (float)ty, (float)tileW, (float)tileH), _resampler);
                    }

                    break;
                }
            default: // Repeat
                {
                    var tilesX = (long)Math.Ceiling(canvasW / tileW) + 2;
                    var tilesY = (long)Math.Ceiling(canvasH / tileH) + 2;
                    if (tilesX * tilesY > 1_000_000)
                    {
                        break;
                    }

                    var startX = offsetX % tileW;
                    if (startX > 0)
                    {
                        startX -= tileW;
                    }

                    var startY = offsetY % tileH;
                    if (startY > 0)
                    {
                        startY -= tileH;
                    }

                    for (var ty = startY; ty < canvasH; ty += tileH)
                    {
                        for (var tx = startX; tx < canvasW; tx += tileW)
                        {
                            canvas.DrawImage(src, srcRect, new RectangleF((float)tx, (float)ty, (float)tileW, (float)tileH), _resampler);
                        }
                    }

                    break;
                }
        }
    }

    /// <summary>
    /// Multiply each pixel's alpha in <paramref name="target"/> by the alpha of
    /// the matching pixel in <paramref name="mask"/> (both straight-alpha
    /// Rgba32, identical dimensions). Used by <c>background-clip: text</c> to
    /// keep the background paint only where a glyph was drawn, and by the default
    /// (<c>alpha</c>/<c>match-source</c>) mask-mode path.
    /// </summary>
    private static void MultiplyAlphaByMask(Image<Rgba32> target, Image<Rgba32> mask)
    {
        var height = Math.Min(target.Height, mask.Height);
        target.ProcessPixelRows(mask, (targetAccessor, maskAccessor) =>
        {
            for (var y = 0; y < height; y++)
            {
                var targetRow = targetAccessor.GetRowSpan(y);
                var maskRow = maskAccessor.GetRowSpan(y);
                var width = Math.Min(targetRow.Length, maskRow.Length);
                for (var x = 0; x < width; x++)
                {
                    // mask alpha 0..255 scales the fill alpha; (a*m + 127)/255
                    // rounds to nearest.
                    var a = (targetRow[x].A * maskRow[x].A + 127) / 255;
                    targetRow[x].A = (byte)a;
                }
            }
        });
    }

    /// <summary>
    /// CSS Masking 1 §6.1 luminance masking: multiply each pixel's alpha in
    /// <paramref name="target"/> by the linearised luminance of the matching
    /// pixel in <paramref name="mask"/>. Luminance is computed as:
    /// <c>L = 0.2126·R + 0.7152·G + 0.0722·B</c> (Rec 709 coefficients) after
    /// gamma-decoding each sRGB channel with the standard piecewise expansion.
    /// The alpha of the mask pixel is applied too (opaque white → 100% mask;
    /// transparent → 0% mask), matching the CSS spec's "luminance × alpha"
    /// product (CSS Masking 1 §6.1 step 3).
    /// </summary>
    private static void MultiplyAlphaByLuminanceMask(Image<Rgba32> target, Image<Rgba32> mask)
    {
        var height = Math.Min(target.Height, mask.Height);
        target.ProcessPixelRows(mask, (targetAccessor, maskAccessor) =>
        {
            for (var y = 0; y < height; y++)
            {
                var targetRow = targetAccessor.GetRowSpan(y);
                var maskRow = maskAccessor.GetRowSpan(y);
                var width = Math.Min(targetRow.Length, maskRow.Length);
                for (var x = 0; x < width; x++)
                {
                    var mp = maskRow[x];
                    // CSS Masking 1 §6.1: luminanceMask = Luma(mask) × maskAlpha/255.
                    // Luma uses Rec 709; sRGB gamma-decode with the standard piecewise.
                    var mr = SrgbToLinear(mp.R);
                    var mg = SrgbToLinear(mp.G);
                    var mb = SrgbToLinear(mp.B);
                    var luma = 0.2126 * mr + 0.7152 * mg + 0.0722 * mb; // 0..1
                    // Combine luma with mask alpha.
                    var maskVal = luma * mp.A / 255.0; // 0..1
                    var a = (int)Math.Round(targetRow[x].A * maskVal);
                    targetRow[x].A = (byte)Math.Clamp(a, 0, 255);
                }
            }
        });
    }

    /// <summary>
    /// sRGB gamma-decode a byte channel value (0..255) to linear light (0..1)
    /// using the IEC 61966-2-1 piecewise function. Used by luminance masking.
    /// </summary>
    private static double SrgbToLinear(byte v)
    {
        var c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    /// <summary>
    /// Clip the alpha channel of <paramref name="layer"/> to the shape described
    /// by <paramref name="clipPath"/> by rasterising the path into a temporary
    /// mask image and multiplying alphas. Used to apply border-radius clipping to
    /// a device-resolution offscreen layer before blitting it to the canvas.
    /// </summary>
    private static void ApplyClipMask(Image<Rgba32> layer, IPath clipPath)
    {
        // Rasterize the clip path into a temporary mask at the same resolution:
        // fill the clip shape with solid white so alpha=255 inside, alpha=0 outside.
        var w = layer.Width;
        var h = layer.Height;
        using var clipMask = new Image<Rgba32>(w, h, new Rgba32(0, 0, 0, 0));
        clipMask.Mutate(ctx => ctx.Paint(c =>
            c.Fill(Brushes.Solid(Color.White), clipPath)));

        // Multiply the layer's alpha by the clip mask's alpha channel.
        MultiplyAlphaByMask(layer, clipMask);
    }

    /// <summary>
    /// Scale a <see cref="CornerRadii"/> by the device pixel ratio
    /// <paramref name="scale"/> so the radii match the offscreen layer
    /// which is sized in device pixels, not CSS px.
    /// </summary>
    private static CornerRadii ScaleRadii(CornerRadii r, float scale)
        => new(
            r.TopLeftX * scale, r.TopLeftY * scale,
            r.TopRightX * scale, r.TopRightY * scale,
            r.BottomRightX * scale, r.BottomRightY * scale,
            r.BottomLeftX * scale, r.BottomLeftY * scale);

    /// <summary>
    /// Resolve a gradient's color stops into ImageSharp <see cref="ColorStop"/>s
    /// with monotonically increasing ratios in 0..1. Stops without an explicit
    /// position are evenly distributed between their neighbors per CSS Images 3
    /// §3.4.3. Transition hint entries (<see cref="CssColorStop.IsHint"/>) are
    /// skipped — they carry no color and are handled by the conic per-pixel path.
    /// For the linear/radial brush path the hint skew is not applied (sRGB fallback
    /// for hint midpoint curve; stop positions are still correct).
    /// </summary>
    private static ColorStop[] ResolveColorStops(CssGradient gradient, double lineLengthPx)
    {
        var ratios = ResolveStopRatios(gradient, lineLengthPx);
        var src = gradient.Stops;
        var realCount = src.Count(s => !s.IsHint);
        var result = new ColorStop[realCount];
        var ri = 0;
        for (var i = 0; i < src.Count; i++)
        {
            if (src[i].IsHint)
            {
                continue;
            }

            var c = src[i].Color.ToSrgb();
            result[ri++] = new ColorStop((float)ratios[i], Color.FromPixel(new Rgba32(c.R, c.G, c.B, c.A)));
        }
        return result;
    }

    /// <summary>
    /// Resolve a gradient's color-stop positions into monotonically
    /// non-decreasing ratios in 0..1. Stops without an explicit position are
    /// evenly distributed between their neighbors per CSS Images 3 §3.4.3. The
    /// conic rasterizer reuses this so its stops follow the same distribution
    /// rules as the linear/radial brushes. Transition hints (which always carry a
    /// position) are resolved alongside real stops — the SampleStopsColor path
    /// uses the hint index to apply the curve, not skip the entry.
    /// </summary>
    private static double[] ResolveStopRatios(CssGradient gradient, double lineLengthPx)
    {
        var src = gradient.Stops;
        var ratios = new double?[src.Count];
        for (var i = 0; i < src.Count; i++)
        {
            if (src[i].Position is { } p)
            {
                ratios[i] = Math.Clamp(p.ResolveFraction(lineLengthPx), 0.0, 1.0);
            }
        }

        // For first/last defaults, skip hints (first/last real stops default to 0/1).
        var firstReal = -1;
        var lastReal = -1;
        for (var i = 0; i < src.Count; i++)
        {
            if (!src[i].IsHint) { if (firstReal < 0) { firstReal = i; } lastReal = i; }
        }
        if (firstReal >= 0)
        {
            ratios[firstReal] ??= 0.0;
        }

        if (lastReal >= 0)
        {
            ratios[lastReal] ??= 1.0;
        }

        // Enforce non-decreasing positions (CSS Images 3: a stop's position is
        // clamped to be >= the previous stop's).
        var lastKnown = 0.0;
        for (var i = 0; i < ratios.Length; i++)
        {
            if (ratios[i] is { } r)
            {
                if (r < lastKnown)
                {
                    r = lastKnown;
                }

                ratios[i] = r;
                lastKnown = r;
            }
        }

        // Interpolate runs of null positions evenly between their bracketing
        // known stops (handles unpositioned real stops; hints always have positions).
        var idx = 0;
        while (idx < ratios.Length)
        {
            if (ratios[idx] is not null) { idx++; continue; }
            var startKnown = idx - 1;            // always >= 0 because [firstReal] is set
            var end = idx;
            while (end < ratios.Length && ratios[end] is null)
            {
                end++;
            }

            var endKnown = end;                  // ratios[end] is set (lastReal is set)
            var startVal = ratios[startKnown]!.Value;
            var endVal = ratios[endKnown]!.Value;
            var gap = endKnown - startKnown;
            for (var k = idx; k < end; k++)
            {
                ratios[k] = startVal + (endVal - startVal) * (k - startKnown) / gap;
            }

            idx = end;
        }

        var result = new double[src.Count];
        for (var i = 0; i < src.Count; i++)
        {
            result[i] = ratios[i]!.Value;
        }

        return result;
    }

    private static float RadialRadius(CssGradient gradient, CssGradientPosition pos, float w, float h)
    {
        // Distances from the gradient center to each box edge.
        var left = pos.FractionX * w;
        var right = w - left;
        var top = pos.FractionY * h;
        var bottom = h - top;

        float closestSide = (float)Math.Min(Math.Min(left, right), Math.Min(top, bottom));
        float farthestSide = (float)Math.Max(Math.Max(left, right), Math.Max(top, bottom));

        double Corner(Func<double, double, double> pick)
        {
            var hx = pick(left, right);
            var hy = pick(top, bottom);
            return Math.Sqrt(hx * hx + hy * hy);
        }
        float closestCorner = (float)Corner(Math.Min);
        float farthestCorner = (float)Corner(Math.Max);

        return gradient.Size switch
        {
            CssRadialSize.ClosestSide => closestSide,
            CssRadialSize.ClosestCorner => closestCorner,
            CssRadialSize.FarthestSide => farthestSide,
            CssRadialSize.FarthestCorner => farthestCorner,
            _ => farthestCorner,
        };
    }

    private void DrawText(DrawingCanvas canvas, DrawText text)
    {
        if (string.IsNullOrEmpty(text.Text))
        {
            return;
        }

        var spec = FontSpecFromDrawText(text);
        var size = (float)text.FontSize;
        var probe = FirstCodepoint(text.Text);
        var font = ResolveFont(spec, probe, size);
        var color = ToColor(text.Color);

        var originX = (float)text.X;
        var originY = (float)text.Y;
        var brush = Brushes.Solid(color);

        // Reusing the layout-time shaped run skips a paint-time reshape; a miss
        // (no ImageSharp run, or a font-size mismatch) rebuilds the TextBlock
        // here — re-shaping the run, the cost the postmortem suspected. Track
        // both so the trace shows the reuse ratio for text-heavy pages.
        TextBlock textBlock;
        if (text.Shaped is ImageSharpShapedRun shaped && shaped.CanReuseAtSize(size))
        {
            textBlock = shaped.TextBlock;
        }
        else
        {
            textBlock = new TextBlock(text.Text, new TextOptions(font));
        }

        canvas.DrawText(textBlock, new PointF(originX, originY), -1, brush, null);
    }

    // ---- CSS Text Decoration 3 (wp:M5-css-15) ----

    private void DrawTextDecoration(DrawingCanvas canvas, DrawTextDecoration d)
    {
        if (d.Width <= 0 || d.Lines == TextDecorationLines.None)
        {
            return;
        }

        var spec = new FontSpec(d.FontFamilies, d.Bold, d.Italic);
        var size = (float)d.FontSize;
        var font = ResolveFont(spec, probeCodepoint: 'x', size);
        var fm = font.FontMetrics;
        var unitsScale = fm.UnitsPerEm > 0 ? size / (float)fm.UnitsPerEm : size / 1000f;

        // Real font metrics (design units → CSS px). OpenType underline/strikeout
        // positions are measured from the baseline, positive = up.
        var ascender = Math.Abs(fm.HorizontalMetrics.Ascender) * unitsScale;
        var underlinePosFromBaseline = fm.UnderlinePosition * unitsScale; // typically negative (below)
        var underlineThickness = fm.UnderlineThickness * unitsScale;
        var strikeoutPosFromBaseline = fm.StrikeoutPosition * unitsScale; // positive (above baseline)

        // Resolved thickness: 0 sentinel means `auto`/`from-font` — prefer the
        // font's own underline thickness, falling back to ~1px-at-16px.
        var thickness = d.Thickness > 0
            ? d.Thickness
            : (underlineThickness > 0 ? underlineThickness : Math.Max(1f, size / 16f));

        var baseline = (float)d.BaselineY;
        var color = ToColor(d.Color);
        var x0 = (float)d.X;
        var x1 = (float)(d.X + d.Width);
        var offset = (float)d.UnderlineOffset;

        if ((d.Lines & TextDecorationLines.Underline) != 0)
        {
            // Underline sits just below the baseline. Honor text-underline-offset
            // (positive moves the line further from the text) on top of the
            // font's underline position.
            var yPos = underlinePosFromBaseline != 0
                ? baseline - (float)underlinePosFromBaseline + offset
                : baseline + Math.Max(1f, (float)thickness) + offset;
            DrawDecorationLine(canvas, x0, x1, yPos, (float)thickness, color, d.Style);
        }

        if ((d.Lines & TextDecorationLines.Overline) != 0)
        {
            // Overline rides above the text, at roughly the ascender / cap line.
            var yPos = baseline - (float)ascender;
            DrawDecorationLine(canvas, x0, x1, yPos, (float)thickness, color, d.Style);
        }

        if ((d.Lines & TextDecorationLines.LineThrough) != 0)
        {
            // Line-through crosses near the middle of the x-height. Prefer the
            // font's strikeout position; fall back to ~1/3 of the ascender.
            var yPos = strikeoutPosFromBaseline != 0
                ? baseline - (float)strikeoutPosFromBaseline
                : baseline - (float)ascender / 3f;
            DrawDecorationLine(canvas, x0, x1, yPos, (float)thickness, color, d.Style);
        }
    }

    private static void DrawDecorationLine(DrawingCanvas canvas, float x0, float x1, float y, float thickness, Color color, TextDecorationStyleKind style)
    {
        var w = Math.Max(0.5f, thickness);
        switch (style)
        {
            case TextDecorationStyleKind.Wavy:
                canvas.Draw(Pens.Solid(color, w), BuildWavyPath(x0, x1, y, w));
                break;
            case TextDecorationStyleKind.Double:
                // Two parallel lines separated by a gap of ~thickness.
                var gap = w + Math.Max(1f, w);
                canvas.Draw(Pens.Solid(color, w), HorizontalLine(x0, x1, y));
                canvas.Draw(Pens.Solid(color, w), HorizontalLine(x0, x1, y + gap));
                break;
            case TextDecorationStyleKind.Dotted:
                canvas.Draw(Pens.Dot(color, w), HorizontalLine(x0, x1, y));
                break;
            case TextDecorationStyleKind.Dashed:
                canvas.Draw(Pens.Dash(color, w), HorizontalLine(x0, x1, y));
                break;
            default:
                canvas.Draw(Pens.Solid(color, w), HorizontalLine(x0, x1, y));
                break;
        }
    }

    private static IPath HorizontalLine(float x0, float x1, float y)
    {
        var pb = new PathBuilder();
        pb.AddLine(new PointF(x0, y), new PointF(x1, y));
        return pb.Build();
    }

    private static IPath BuildWavyPath(float x0, float x1, float y, float thickness)
    {
        // Approximate the wavy underline as a sequence of cubic Bézier humps.
        // Wavelength scales with thickness so heavier underlines wave wider.
        var wavelength = Math.Max(4f, thickness * 4f);
        var amplitude = Math.Max(1f, thickness);
        var pb = new PathBuilder();
        pb.StartFigure();
        var x = x0;
        pb.MoveTo(new PointF(x, y));
        var up = true;
        while (x < x1)
        {
            var next = Math.Min(x1, x + wavelength / 2f);
            var dir = up ? -1f : 1f; // screen y grows downward; alternate humps
            var c1 = new PointF(x + (next - x) / 2f, y + dir * amplitude);
            var c2 = new PointF(x + (next - x) / 2f, y + dir * amplitude);
            pb.AddCubicBezier(new PointF(x, y), c1, c2, new PointF(next, y));
            x = next;
            up = !up;
        }
        return pb.Build();
    }

    private void DrawTextShadow(DrawingCanvas canvas, DrawTextShadow s, DisposableBag pendingImageSources)
    {
        if (string.IsNullOrEmpty(s.Text))
        {
            return;
        }

        var spec = new FontSpec(s.FontFamilies, s.Bold, s.Italic);
        var size = (float)s.FontSize;
        var probe = FirstCodepoint(s.Text);
        var font = ResolveFont(spec, probe, size);
        var color = ToColor(s.Color);
        var brush = Brushes.Solid(color);

        var originX = (float)(s.X + s.OffsetX);
        var originY = (float)(s.Y + s.OffsetY);

        TextBlock textBlock;
        if (s.Shaped is ImageSharpShapedRun shaped && shaped.CanReuseAtSize(size))
        {
            textBlock = shaped.TextBlock;
        }
        else
        {
            textBlock = new TextBlock(s.Text, new TextOptions(font));
        }

        if (s.Blur <= 0)
        {
            // Sharp shadow: just an offset copy of the text in the shadow color.
            canvas.DrawText(textBlock, new PointF(originX, originY), -1, brush, null);
            return;
        }

        // Blurred shadow: render the text into an off-screen transparent buffer,
        // Gaussian-blur it, then composite the result onto the canvas at the
        // shadow offset. The buffer is sized in CSS px (1:1) with padding for
        // the blur spread; the canvas transform handles device scaling when the
        // blurred bitmap is blitted back.
        var em = font.FontMetrics.UnitsPerEm > 0 ? size / (float)font.FontMetrics.UnitsPerEm : size / 1000f;
        var ascent = Math.Abs(font.FontMetrics.HorizontalMetrics.Ascender) * em;
        var descent = Math.Abs(font.FontMetrics.HorizontalMetrics.Descender) * em;
        var advance = TextMeasurer.MeasureAdvance(s.Text, new TextOptions(font));
        var pad = (int)Math.Ceiling(s.Blur * 3) + 2;
        var width = (int)Math.Ceiling(advance.Width) + 2 * pad;
        var height = (int)Math.Ceiling(ascent + descent) + 2 * pad;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var glyphLayer = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 0));
        // canvas.DrawImage records a command and rasterizes lazily when the
        // outer Mutate/Paint scope unwinds, so the layer must outlive this call.
        // Stage it in the bag that is disposed after readback (mirrors DrawImage).
        pendingImageSources.Add(glyphLayer);

        // This offscreen glyph render + Gaussian blur is a full nested
        // rasterization that the lazy outer command_record span never sees;
        // its own span makes shadow-heavy pages attributable.
        // The display-list DrawText origin (s.X, s.Y) is the top of the line box;
        // render the glyph run at (pad, pad) inside the layer so it matches.
        glyphLayer.Mutate(ctx => ctx.Paint(c =>
            c.DrawText(new TextBlock(s.Text, new TextOptions(font)), new PointF(pad, pad), -1, brush, null)));
        glyphLayer.Mutate(ctx => ctx.GaussianBlur((float)s.Blur));

        // Composite at the offset origin minus the padding we added.
        var destRect = new RectangleF(originX - pad, originY - pad, width, height);
        canvas.DrawImage(glyphLayer, new Rectangle(0, 0, width, height), destRect, _resampler);
    }

    private void DrawImage(DrawingCanvas canvas, DrawImage item, DisposableBag pendingImageSources)
    {
        if (item.Bounds.Width <= 0 || item.Bounds.Height <= 0)
        {
            return;
        }

        var decoded = item.Source;
        if (decoded is null || decoded.Width <= 0 || decoded.Height <= 0)
        {
            return;
        }

        // ImageSharp's LoadPixelData<TPixel> wants a ReadOnlySpan<byte> of the
        // exact byte length; DecodedImage's buffer is straight-alpha RGBA8888
        // which matches Rgba32's layout precisely.
        var src = Image.LoadPixelData<Rgba32>(decoded.Pixels.Span, decoded.Width, decoded.Height);
        pendingImageSources.Add(src);
        // DrawingCanvas.DrawImage signature is (image, sourceRect, destinationRect, resampler):
        // sourceRect is the integer crop inside the source image and
        // destinationRect is the float-precision target on the canvas.
        Rectangle sourceRect;
        if (item.SourceRect is { } sr)
        {
            // Clamp to the source dimensions so a slice that overruns the
            // image (e.g. a sprite-sheet edge with sub-pixel rounding) still
            // produces a valid integer rect.
            var x = Math.Clamp((int)Math.Round(sr.X), 0, decoded.Width);
            var y = Math.Clamp((int)Math.Round(sr.Y), 0, decoded.Height);
            var w = Math.Clamp((int)Math.Round(sr.Width), 0, decoded.Width - x);
            var h = Math.Clamp((int)Math.Round(sr.Height), 0, decoded.Height - y);
            if (w <= 0 || h <= 0)
            {
                return;
            }

            sourceRect = new Rectangle(x, y, w, h);
        }
        else
        {
            sourceRect = new Rectangle(0, 0, decoded.Width, decoded.Height);
        }
        var destRect = ToRectF(item.Bounds);

        canvas.DrawImage(src, sourceRect, destRect, _resampler);
    }

    private readonly record struct FontCacheKey(FontSpec Spec, float Size, int Probe);

    private static FontSpec FontSpecFromDrawText(DrawText text)
        => new(text.FontFamilies, text.Bold, text.Italic);

    private static int FirstCodepoint(string text)
    {
        foreach (var codePoint in text.EnumerateCodePoints())
        {
            return codePoint.Value;
        }

        return 0;
    }

    private Font ResolveFont(FontSpec spec, int probeCodepoint, float size)
    {
        var key = new FontCacheKey(spec, size, probeCodepoint);
        if (_fontCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var font = _fontCache.GetOrAdd(key, k => CreateFont(k.Spec, k.Size));
        return font;
    }

    private Font CreateFont(FontSpec spec, float size)
        => ImageSharpFontLookup.CreateFont(_fontCollection, spec, size);

    private static Matrix4x4 ToCanvasMatrix(Matrix2D m, float scale)
    {
        var s = scale > 0 ? scale : 1f;
        // Display-list commands stay in CSS pixels. The canvas transform maps
        // the composed CSS transform into device pixels for the output surface.
        return new Matrix4x4(
            (float)(m.A * s), (float)(m.B * s), 0f, 0f,
            (float)(m.C * s), (float)(m.D * s), 0f, 0f,
            0f, 0f, 1f, 0f,
            (float)(m.E * s), (float)(m.F * s), 0f, 1f);
    }

    private static RectangleF ToRectF(LayoutRect r)
        => new((float)r.X, (float)r.Y, (float)r.Width, (float)r.Height);

    private static RectangleF ToSnappedLayoutRectF(LayoutRect r, float scale)
    {
        var s = scale > 0 ? scale : 1f;
        var x0 = (float)(Math.Round(r.X * s) / s);
        var y0 = (float)(Math.Round(r.Y * s) / s);
        var x1 = (float)(Math.Round((r.X + r.Width) * s) / s);
        var y1 = (float)(Math.Round((r.Y + r.Height) * s) / s);
        return new RectangleF(x0, y0, x1 - x0, y1 - y0);
    }

    private static RectanglePolygon ToRectPath(LayoutRect r)
        => new(ToRectF(r));

    private static RectanglePolygon ToSnappedLayoutRectPath(LayoutRect r, float scale)
        => new(ToSnappedLayoutRectF(r, scale));

    private static Color ToColor(CssColor c)
        => Color.FromPixel(new Rgba32(c.R, c.G, c.B, c.A));

    // --- wp:M5-css-14 — rounded rects + box-shadow ---------------------------

    // Magic constant for approximating a 90° elliptical arc with a cubic Bézier.
    private const float ArcBezierK = 0.5522847498f;

    /// <summary>
    /// Builds the clip path for a CSS overflow clip. When <paramref name="radii"/>
    /// is non-zero, returns a rounded-rectangle path (CSS Overflow 3 §2.4 —
    /// border-radius clips descendants). When the radii are all zero, returns a
    /// plain axis-aligned rectangle. This helper is the shared entry point for
    /// masking, gradient, and clipping agents — use it instead of calling
    /// <see cref="BuildRoundedRectPath"/> directly when the caller wants the
    /// correct plain-rect shortcut for the zero-radius case.
    /// </summary>
    /// <param name="bounds">The clip region in CSS px (page coordinates).</param>
    /// <param name="radii">The corner radii; pass <see cref="CornerRadii.None"/> for a plain rect.</param>
    internal static IPath BuildClipPath(LayoutRect bounds, CornerRadii radii)
        => radii.IsZero ? ToRectPath(bounds) : BuildRoundedRectPath(bounds, radii);

    /// <summary>
    /// Builds a closed rounded-rectangle path for <paramref name="bounds"/> with
    /// independent per-corner elliptical radii. Each corner's radii are clamped
    /// to half the box extent so they never cross. A zero-radius corner degrades
    /// to a sharp right angle. Traced clockwise from the top edge.
    /// </summary>
    private static IPath BuildRoundedRectPath(LayoutRect bounds, CornerRadii r)
    {
        var x = (float)bounds.X;
        var y = (float)bounds.Y;
        var w = (float)bounds.Width;
        var h = (float)bounds.Height;
        var right = x + w;
        var bottom = y + h;

        // Clamp each radius to the half-extents of the box.
        var maxX = w / 2f;
        var maxY = h / 2f;
        float Clamp(double v, float max) => Math.Clamp((float)v, 0f, max);
        var tlx = Clamp(r.TopLeftX, maxX); var tly = Clamp(r.TopLeftY, maxY);
        var trx = Clamp(r.TopRightX, maxX); var tryy = Clamp(r.TopRightY, maxY);
        var brx = Clamp(r.BottomRightX, maxX); var bry = Clamp(r.BottomRightY, maxY);
        var blx = Clamp(r.BottomLeftX, maxX); var bly = Clamp(r.BottomLeftY, maxY);

        var pb = new PathBuilder();
        // Top edge, left→right, starting just after the top-left corner.
        pb.MoveTo(new PointF(x + tlx, y));
        pb.LineTo(new PointF(right - trx, y));
        // Top-right corner.
        if (trx > 0 || tryy > 0)
        {
            pb.AddCubicBezier(
                new PointF(right - trx, y),
                new PointF(right - trx + trx * ArcBezierK, y),
                new PointF(right, y + tryy - tryy * ArcBezierK),
                new PointF(right, y + tryy));
        }
        // Right edge.
        pb.LineTo(new PointF(right, bottom - bry));
        // Bottom-right corner.
        if (brx > 0 || bry > 0)
        {
            pb.AddCubicBezier(
                new PointF(right, bottom - bry),
                new PointF(right, bottom - bry + bry * ArcBezierK),
                new PointF(right - brx + brx * ArcBezierK, bottom),
                new PointF(right - brx, bottom));
        }
        // Bottom edge.
        pb.LineTo(new PointF(x + blx, bottom));
        // Bottom-left corner.
        if (blx > 0 || bly > 0)
        {
            pb.AddCubicBezier(
                new PointF(x + blx, bottom),
                new PointF(x + blx - blx * ArcBezierK, bottom),
                new PointF(x, bottom - bly + bly * ArcBezierK),
                new PointF(x, bottom - bly));
        }
        // Left edge.
        pb.LineTo(new PointF(x, y + tly));
        // Top-left corner.
        if (tlx > 0 || tly > 0)
        {
            pb.AddCubicBezier(
                new PointF(x, y + tly),
                new PointF(x, y + tly - tly * ArcBezierK),
                new PointF(x + tlx - tlx * ArcBezierK, y),
                new PointF(x + tlx, y));
        }

        pb.CloseFigure();
        return pb.Build();
    }

    /// <summary>
    /// Paints a single outer drop shadow (CSS Backgrounds 3 §6). The shadow
    /// silhouette — the box grown by <c>Spread</c>, with its corner radii grown
    /// to match — is rasterized into a transparent offscreen image at CSS-pixel
    /// resolution, blurred by a Gaussian of σ = blur/2, then blitted at the
    /// shadow offset through the same canvas transform every other primitive
    /// uses (so the device <paramref name="scale"/> composes correctly). Inset
    /// layers branch to <see cref="DrawInsetBoxShadow"/>.
    /// </summary>
    private void DrawBoxShadow(DrawingCanvas canvas, DrawBoxShadow shadow, float scale, Matrix2D current, DisposableBag pendingImageSources)
    {
        if (shadow.Inset)
        {
            DrawInsetBoxShadow(canvas, shadow, scale, current, pendingImageSources);
            return;
        }

        // Device-scale fast path: when the composed CSS transform is a pure
        // translation, the shadow is rasterized AT DEVICE RESOLUTION (the blur
        // sigma scales with the device scale, mirroring ApplyFilterChain), cached
        // under a scale-aware key, and blitted 1:1 through an identity canvas
        // transform. Without this every draw of a cached CSS-resolution shadow
        // ran a CloningImageProcessor resample to device scale — 4.7 s of a 16 s
        // hover-toggle trace on the animations demo. A non-translation transform
        // falls back to the CSS-resolution raster the composed matrix resamples.
        var fast = IsTranslationOnly(current);
        var rasterScale = fast && scale > 0 ? scale : 1f;
        var scaled = rasterScale == 1f ? shadow : ScaleShadow(shadow, rasterScale);
        if (!TryGetBoxShadowRasterGeometry(scaled, out var geometry))
        {
            return;
        }

        var key = BoxShadowCacheKey.From(scaled, geometry, rasterScale);
        if (!_boxShadowCache.TryGet(key, out var shadowImage))
        {
            shadowImage = RasterizeBoxShadow(scaled, geometry);
            Interlocked.Increment(ref _boxShadowRasterizations);
            _boxShadowCache.Put(key, shadowImage, pendingImageSources);
        }

        var sourceRect = new Rectangle(0, 0, geometry.ImageWidth, geometry.ImageHeight);
        if (fast)
        {
            DrawDeviceScaledShadow(canvas, shadowImage, sourceRect, new RectangleF(
                (float)(geometry.Destination.X + current.E * rasterScale),
                (float)(geometry.Destination.Y + current.F * rasterScale),
                geometry.ImageWidth,
                geometry.ImageHeight));
            return;
        }

        canvas.DrawImage(shadowImage, sourceRect, ToRectF(geometry.Destination), _resampler);
    }

    /// <summary>True when <paramref name="m"/> moves content without scaling,
    /// rotating, or shearing it — the only transforms under which a
    /// device-resolution shadow raster can be blitted 1:1.</summary>
    private static bool IsTranslationOnly(Matrix2D m)
        => m.A == 1 && m.B == 0 && m.C == 0 && m.D == 1;

    /// <summary>Returns a copy of <paramref name="shadow"/> with every CSS
    /// length scaled into device pixels, so the raster helpers produce a
    /// device-resolution image without learning about scale themselves.</summary>
    private static DrawBoxShadow ScaleShadow(DrawBoxShadow shadow, float scale) => shadow with
    {
        Bounds = new LayoutRect(
            shadow.Bounds.X * scale, shadow.Bounds.Y * scale,
            shadow.Bounds.Width * scale, shadow.Bounds.Height * scale),
        Radii = ScaleRadii(shadow.Radii, scale),
        OffsetX = shadow.OffsetX * scale,
        OffsetY = shadow.OffsetY * scale,
        Blur = shadow.Blur * scale,
        Spread = shadow.Spread * scale,
    };

    /// <summary>Blits a device-resolution shadow raster at a device-pixel
    /// destination under an identity transform. Source and destination are the
    /// same size, so the draw is a plain blit — no resampling pass runs.</summary>
    private static void DrawDeviceScaledShadow(DrawingCanvas canvas, Image<Rgba32> shadowImage, Rectangle sourceRect, RectangleF deviceDest)
    {
        canvas.Save(new DrawingOptions());
        try
        {
            canvas.DrawImage(shadowImage, sourceRect, deviceDest);
        }
        finally
        {
            canvas.Restore();
        }
    }

    // Cache-miss rasterizations since backend construction (outer + inset).
    // Read by tests to prove a steady-state frame serves shadows from cache.
    private int _boxShadowRasterizations;
    internal int BoxShadowRasterizationsForTest => _boxShadowRasterizations;

    // Above this sigma the shadow rasters at reduced resolution and upscales
    // once at the end — the Gaussian's cost is O(pixels x kernel), so both
    // shrink with the factor. A big card shadow (blur 40 px at a 2x display)
    // cost ~200 ms at full resolution and single-digit ms downsampled; the
    // upscale of an inherently low-passed image is visually exact.
    private const float MaxShadowSigmaPerRaster = 4f;

    private Image<Rgba32> RasterizeBoxShadow(DrawBoxShadow shadow, BoxShadowRasterGeometry geometry)
    {
        // Grow each corner radius by the spread so the silhouette stays a
        // rounded rect that hugs the box. A negative spread shrinks it.
        var grown = GrowRadii(shadow.Radii, shadow.Spread);
        var sigma = (float)(Math.Max(0, shadow.Blur) / 2d);
        var down = (int)Math.Clamp(MathF.Floor(sigma / MaxShadowSigmaPerRaster), 1, 8);

        if (down > 1)
        {
            // Proportional small raster: every coordinate divides by the
            // factor exactly (no per-scale rounding constants), so the single
            // upscale lands the silhouette where the full-resolution raster
            // would have.
            var w = Math.Max(1, (geometry.ImageWidth + down - 1) / down);
            var h = Math.Max(1, (geometry.ImageHeight + down - 1) / down);
            var silhouetteSmall = new LayoutRect(
                geometry.Margin / (double)down, geometry.Margin / (double)down,
                geometry.SilhouetteWidth / (double)down, geometry.SilhouetteHeight / (double)down);
            var shapeSmall = BuildRoundedRectPath(silhouetteSmall, ScaleRadii(grown, 1f / down));
            var small = new Image<Rgba32>(w, h, new Rgba32(0, 0, 0, 0));
            small.Mutate(ctx => ctx.Paint(c => c.Fill(Brushes.Solid(ToColor(shadow.Color)), shapeSmall)));
            small.Mutate(ctx => ctx.GaussianBlur(sigma / down));
            small.Mutate(ctx => ctx.Resize(geometry.ImageWidth, geometry.ImageHeight));
            return small;
        }

        var silhouette = new LayoutRect(geometry.Margin, geometry.Margin, geometry.SilhouetteWidth, geometry.SilhouetteHeight);
        var shape = BuildRoundedRectPath(silhouette, grown);

        var shadowImage = new Image<Rgba32>(geometry.ImageWidth, geometry.ImageHeight, new Rgba32(0, 0, 0, 0));
        shadowImage.Mutate(ctx => ctx.Paint(c => c.Fill(Brushes.Solid(ToColor(shadow.Color)), shape)));
        if (shadow.Blur > 0)
        {
            shadowImage.Mutate(ctx => ctx.GaussianBlur(sigma));
        }

        return shadowImage;
    }

    /// <summary>
    /// Paints a single inset box-shadow layer (CSS Backgrounds 3 §7.1.1). The
    /// display item's <c>Bounds</c> is the element's PADDING box and its
    /// <c>Radii</c> the padding-box inner radii — the builder resolves both.
    /// The shadow is the region between the padding edge and the inner
    /// silhouette (the padding box translated by (OffsetX, OffsetY) and shrunk
    /// by Spread on every side), rasterized offscreen as an opaque plane with
    /// the silhouette knocked out, blurred by a Gaussian of σ = Blur/2, then
    /// clipped to the rounded padding box and blitted through the canvas
    /// transform. A positive OffsetX shifts the silhouette right and thickens
    /// the LEFT band, matching the spec's offset direction for inner shadows
    /// (verified against Chromium).
    /// </summary>
    private void DrawInsetBoxShadow(DrawingCanvas canvas, DrawBoxShadow shadow, float scale, Matrix2D current, DisposableBag pendingImageSources)
    {
        if (shadow.Color.A == 0)
        {
            return;
        }

        // Same device-scale fast path as the outer shadow: under a pure
        // translation the ring is rasterized at device resolution and blitted
        // 1:1, so no per-draw resample runs.
        var fast = IsTranslationOnly(current);
        var rasterScale = fast && scale > 0 ? scale : 1f;
        var scaled = rasterScale == 1f ? shadow : ScaleShadow(shadow, rasterScale);

        var padW = scaled.Bounds.Width;
        var padH = scaled.Bounds.Height;
        if (padW <= 0 || padH <= 0)
        {
            return;
        }

        var blur = Math.Max(0, scaled.Blur);
        var margin = (int)Math.Ceiling(blur * 1.5) + 2;
        var imgW = (int)Math.Ceiling(padW) + 2 * margin;
        var imgH = (int)Math.Ceiling(padH) + 2 * margin;
        if ((long)imgW * imgH > 64_000_000L)
        {
            return;
        }

        var key = BoxShadowCacheKey.FromInset(scaled, imgW, imgH, margin, rasterScale);
        if (!_boxShadowCache.TryGet(key, out var shadowImage))
        {
            shadowImage = RasterizeInsetBoxShadow(scaled, imgW, imgH, margin);
            Interlocked.Increment(ref _boxShadowRasterizations);
            _boxShadowCache.Put(key, shadowImage, pendingImageSources);
        }

        var sourceRect = new Rectangle(0, 0, imgW, imgH);
        if (fast)
        {
            DrawDeviceScaledShadow(canvas, shadowImage, sourceRect, new RectangleF(
                (float)(scaled.Bounds.X + current.E * rasterScale - margin),
                (float)(scaled.Bounds.Y + current.F * rasterScale - margin),
                imgW,
                imgH));
            return;
        }

        var dest = new LayoutRect(shadow.Bounds.X - margin, shadow.Bounds.Y - margin, imgW, imgH);
        canvas.DrawImage(shadowImage, sourceRect, ToRectF(dest), _resampler);
    }

    private static Image<Rgba32> RasterizeInsetBoxShadow(DrawBoxShadow shadow, int imgW, int imgH, int margin)
    {
        // "As if everything outside the padding edge were opaque": start from
        // a fully filled plane (margins included, so the blur at the padding
        // edge samples opaque neighbours), then knock the silhouette out.
        var shadowImage = new Image<Rgba32>(imgW, imgH, new Rgba32(shadow.Color.R, shadow.Color.G, shadow.Color.B, shadow.Color.A));

        // Inner silhouette: the padding box translated by the offset, shrunk
        // by the spread (positive spread thickens the shadow ring; negative
        // grows the silhouette and thins it). Its radii shrink/grow with it.
        var holeW = shadow.Bounds.Width - 2 * shadow.Spread;
        var holeH = shadow.Bounds.Height - 2 * shadow.Spread;
        if (holeW > 0 && holeH > 0)
        {
            var hole = new LayoutRect(
                margin + shadow.OffsetX + shadow.Spread,
                margin + shadow.OffsetY + shadow.Spread,
                holeW,
                holeH);
            KnockOutAlphaByPath(shadowImage, BuildClipPath(hole, GrowRadii(shadow.Radii, -shadow.Spread)));
        }

        if (shadow.Blur > 0)
        {
            shadowImage.Mutate(ctx => ctx.GaussianBlur((float)(Math.Max(0, shadow.Blur) / 2d)));
        }

        // Clip to the (rounded) padding box — this also clears the bleed
        // margins, so the blit never paints outside the padding edge.
        ApplyClipMask(shadowImage, BuildClipPath(new LayoutRect(margin, margin, shadow.Bounds.Width, shadow.Bounds.Height), shadow.Radii));
        return shadowImage;
    }

    /// <summary>
    /// Zero the alpha of <paramref name="layer"/> inside
    /// <paramref name="holePath"/> (anti-aliased: partially covered edge
    /// pixels keep partial alpha). The inverse of <see cref="ApplyClipMask"/>
    /// — used by the inset box-shadow rasterizer to cut the inner silhouette
    /// out of the opaque shadow plane before blurring.
    /// </summary>
    private static void KnockOutAlphaByPath(Image<Rgba32> layer, IPath holePath)
    {
        using var holeMask = new Image<Rgba32>(layer.Width, layer.Height, new Rgba32(0, 0, 0, 0));
        holeMask.Mutate(ctx => ctx.Paint(c => c.Fill(Brushes.Solid(Color.White), holePath)));

        var height = Math.Min(layer.Height, holeMask.Height);
        layer.ProcessPixelRows(holeMask, (targetAccessor, maskAccessor) =>
        {
            for (var y = 0; y < height; y++)
            {
                var targetRow = targetAccessor.GetRowSpan(y);
                var maskRow = maskAccessor.GetRowSpan(y);
                var width = Math.Min(targetRow.Length, maskRow.Length);
                for (var x = 0; x < width; x++)
                {
                    // Multiply by the inverse mask coverage: full coverage
                    // erases, edge coverage feathers, none keeps the fill.
                    var a = (targetRow[x].A * (255 - maskRow[x].A) + 127) / 255;
                    targetRow[x].A = (byte)a;
                }
            }
        });
    }

    private static bool BoxShadowIntersectsTarget(DrawBoxShadow shadow, Matrix2D current, LayoutRect target)
        => shadow.Inset
            // Inner shadows are clipped to the padding box the item carries.
            ? Intersects(TransformedAabb(shadow.Bounds, current), target)
            : TryGetBoxShadowRasterGeometry(shadow, out var geometry)
              && Intersects(TransformedAabb(geometry.Destination, current), target);

    private static bool TryGetBoxShadowRasterGeometry(DrawBoxShadow shadow, out BoxShadowRasterGeometry geometry)
    {
        geometry = default;
        if (shadow.Inset)
        {
            return false; // inner shadows deferred
        }

        if (shadow.Color.A == 0)
        {
            return false;
        }

        var spread = shadow.Spread;
        var silhouetteW = shadow.Bounds.Width + 2 * spread;
        var silhouetteH = shadow.Bounds.Height + 2 * spread;
        if (silhouetteW <= 0 || silhouetteH <= 0)
        {
            return false;
        }

        var blur = Math.Max(0, shadow.Blur);
        var margin = (int)Math.Ceiling(blur * 1.5) + 2;
        var imgW = (int)Math.Ceiling(silhouetteW) + 2 * margin;
        var imgH = (int)Math.Ceiling(silhouetteH) + 2 * margin;
        if (imgW <= 0 || imgH <= 0 || (long)imgW * imgH > 64_000_000L)
        {
            return false;
        }

        var destX = shadow.Bounds.X - spread + shadow.OffsetX - margin;
        var destY = shadow.Bounds.Y - spread + shadow.OffsetY - margin;
        geometry = new BoxShadowRasterGeometry(
            silhouetteW,
            silhouetteH,
            margin,
            imgW,
            imgH,
            new LayoutRect(destX, destY, imgW, imgH));
        return true;
    }

    private readonly record struct BoxShadowRasterGeometry(
        double SilhouetteWidth,
        double SilhouetteHeight,
        int Margin,
        int ImageWidth,
        int ImageHeight,
        LayoutRect Destination);

    private static LayoutRect TransformedAabb(LayoutRect r, Matrix2D m)
    {
        if (m.IsIdentity)
        {
            return r;
        }

        var (x0, y0) = m.Transform(r.X, r.Y);
        var (x1, y1) = m.Transform(r.X + r.Width, r.Y);
        var (x2, y2) = m.Transform(r.X + r.Width, r.Y + r.Height);
        var (x3, y3) = m.Transform(r.X, r.Y + r.Height);
        var minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
        var minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
        var maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
        var maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
        return new LayoutRect(minX, minY, maxX - minX, maxY - minY);
    }

    private static bool Intersects(LayoutRect a, LayoutRect b)
        => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private static CornerRadii GrowRadii(CornerRadii r, double by)
    {
        static double G(double v, double by) => v <= 0 ? 0 : Math.Max(0, v + by);
        return new CornerRadii(
            G(r.TopLeftX, by), G(r.TopLeftY, by),
            G(r.TopRightX, by), G(r.TopRightY, by),
            G(r.BottomRightX, by), G(r.BottomRightY, by),
            G(r.BottomLeftX, by), G(r.BottomLeftY, by));
    }

    // --- Tier 4 items 11+12 — outline + dashed/dotted/double border sides ----

    /// <summary>
    /// Rasterizes a <see cref="DisplayList.DrawBorderSides"/>: each border side
    /// is drawn separately between its corner boundaries. For a square corner
    /// the horizontal sides own the corner pixels and the vertical sides start
    /// inside the adjacent band; for a rounded corner every side’s straight
    /// run stops at the radius tangent and the corner arc is stroked SOLID in
    /// the adjacent side’s color (v1 simplification — dashes do not follow
    /// the curve). One path is built per side, so a dashed side costs a single
    /// fill regardless of dash count.
    /// </summary>
    private static void DrawBorderSides(DrawingCanvas canvas, DrawBorderSides b)
    {
        var x = (float)b.Bounds.X;
        var y = (float)b.Bounds.Y;
        var w = (float)b.Bounds.Width;
        var h = (float)b.Bounds.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var right = x + w;
        var bottom = y + h;

        // Corner radii clamped to the half extents, like BuildRoundedRectPath.
        var maxX = w / 2f;
        var maxY = h / 2f;
        float Clamp(double v, float max) => Math.Clamp((float)v, 0f, max);
        var tlx = Clamp(b.Radii.TopLeftX, maxX); var tly = Clamp(b.Radii.TopLeftY, maxY);
        var trx = Clamp(b.Radii.TopRightX, maxX); var tryy = Clamp(b.Radii.TopRightY, maxY);
        var brx = Clamp(b.Radii.BottomRightX, maxX); var bry = Clamp(b.Radii.BottomRightY, maxY);
        var blx = Clamp(b.Radii.BottomLeftX, maxX); var bly = Clamp(b.Radii.BottomLeftY, maxY);

        var topW = (float)b.TopWidth;
        var rightW = (float)b.RightWidth;
        var bottomW = (float)b.BottomWidth;
        var leftW = (float)b.LeftWidth;

        var paintTop = topW > 0 && b.TopStyle != BorderSideStyle.None && b.TopColor.A > 0;
        var paintRight = rightW > 0 && b.RightStyle != BorderSideStyle.None && b.RightColor.A > 0;
        var paintBottom = bottomW > 0 && b.BottomStyle != BorderSideStyle.None && b.BottomColor.A > 0;
        var paintLeft = leftW > 0 && b.LeftStyle != BorderSideStyle.None && b.LeftColor.A > 0;

        if (paintTop)
        {
            var x0 = tlx > 0 ? x + tlx : x;
            var x1 = trx > 0 ? right - trx : right;
            DrawBorderSideRun(canvas, horizontal: true, x0, x1, edge: y, inward: 1f, topW, ToColor(b.TopColor), b.TopStyle);
        }
        if (paintBottom)
        {
            var x0 = blx > 0 ? x + blx : x;
            var x1 = brx > 0 ? right - brx : right;
            DrawBorderSideRun(canvas, horizontal: true, x0, x1, edge: bottom, inward: -1f, bottomW, ToColor(b.BottomColor), b.BottomStyle);
        }
        if (paintLeft)
        {
            var y0 = tly > 0 ? y + tly : y + (paintTop ? topW : 0f);
            var y1 = bly > 0 ? bottom - bly : bottom - (paintBottom ? bottomW : 0f);
            DrawBorderSideRun(canvas, horizontal: false, y0, y1, edge: x, inward: 1f, leftW, ToColor(b.LeftColor), b.LeftStyle);
        }
        if (paintRight)
        {
            var y0 = tryy > 0 ? y + tryy : y + (paintTop ? topW : 0f);
            var y1 = bry > 0 ? bottom - bry : bottom - (paintBottom ? bottomW : 0f);
            DrawBorderSideRun(canvas, horizontal: false, y0, y1, edge: right, inward: -1f, rightW, ToColor(b.RightColor), b.RightStyle);
        }

        // Rounded corners: stroke each quarter arc solid along the band’s
        // centre line (v1 — the dash pattern does not continue around the
        // curve). The arc takes the adjacent horizontal side’s color when
        // that side paints, else the vertical side’s.
        if (tlx > 0 || tly > 0)
        {
            DrawSolidCornerArc(canvas, x + tlx, y + tly, tlx, tly, sx: -1f, sy: -1f, paintTop, paintLeft, topW, leftW, b.TopColor, b.LeftColor);
        }

        if (trx > 0 || tryy > 0)
        {
            DrawSolidCornerArc(canvas, right - trx, y + tryy, trx, tryy, sx: 1f, sy: -1f, paintTop, paintRight, topW, rightW, b.TopColor, b.RightColor);
        }

        if (brx > 0 || bry > 0)
        {
            DrawSolidCornerArc(canvas, right - brx, bottom - bry, brx, bry, sx: 1f, sy: 1f, paintBottom, paintRight, bottomW, rightW, b.BottomColor, b.RightColor);
        }

        if (blx > 0 || bly > 0)
        {
            DrawSolidCornerArc(canvas, x + blx, bottom - bly, blx, bly, sx: -1f, sy: 1f, paintBottom, paintLeft, bottomW, leftW, b.BottomColor, b.LeftColor);
        }
    }

    /// <summary>
    /// Draws one border side’s straight run. <paramref name="a0"/> /
    /// <paramref name="a1"/> bound the run along the side’s axis
    /// (<paramref name="horizontal"/> picks the axis), <paramref name="edge"/>
    /// is the box edge coordinate on the cross axis, and
    /// <paramref name="inward"/> is +1 when the band grows toward larger
    /// cross-axis values (top / left) or −1 (bottom / right). Dash geometry:
    /// dashed → rectangular dashes, 2:1 dash:gap, a dash anchored at each end;
    /// dotted → round dots of diameter <paramref name="thickness"/> spaced
    /// about twice the width, inset one radius from the run ends; double →
    /// two strokes each one third of the width with the middle third empty.
    /// All segments of a side land in one path, so the side costs one fill.
    /// </summary>
    private static void DrawBorderSideRun(DrawingCanvas canvas, bool horizontal, float a0, float a1, float edge, float inward, float thickness, Color color, BorderSideStyle style)
    {
        var run = a1 - a0;
        if (run <= 0 || thickness <= 0)
        {
            return;
        }

        var near = inward > 0 ? edge : edge - thickness;
        var pb = new PathBuilder();
        switch (style)
        {
            case BorderSideStyle.Dashed:
                {
                    // run = n·dash + (n−1)·gap with dash = 2·gap, n picked so the
                    // gap stays close to one border width.
                    var n = Math.Max(1, (int)Math.Round((run / thickness + 1d) / 3d));
                    var gap = run / (3f * n - 1f);
                    var dash = 2f * gap;
                    for (var i = 0; i < n; i++)
                    {
                        var s = a0 + i * (dash + gap);
                        AddBandRect(pb, horizontal, s, s + dash, near, thickness);
                    }
                    break;
                }
            case BorderSideStyle.Dotted:
                {
                    var r = thickness / 2f;
                    var cross = edge + inward * r;
                    var c0 = a0 + r;
                    var c1 = a1 - r;
                    var span = c1 - c0;
                    if (span <= 0)
                    {
                        var mid = (a0 + a1) / 2f;
                        AddDotFigure(pb, horizontal ? mid : cross, horizontal ? cross : mid, r);
                    }
                    else
                    {
                        var intervals = Math.Max(1, (int)Math.Round(span / (2f * thickness)));
                        var step = span / intervals;
                        for (var i = 0; i <= intervals; i++)
                        {
                            var c = c0 + i * step;
                            AddDotFigure(pb, horizontal ? c : cross, horizontal ? cross : c, r);
                        }
                    }
                    break;
                }
            case BorderSideStyle.Double:
                {
                    var third = thickness / 3f;
                    var nearOuter = inward > 0 ? edge : edge - third;
                    var nearInner = inward > 0 ? edge + 2f * third : edge - thickness;
                    AddBandRect(pb, horizontal, a0, a1, nearOuter, third);
                    AddBandRect(pb, horizontal, a0, a1, nearInner, third);
                    break;
                }
            default:
                AddBandRect(pb, horizontal, a0, a1, near, thickness);
                break;
        }
        canvas.Fill(Brushes.Solid(color), pb.Build());
    }

    /// <summary>Adds one axis-aligned band rectangle figure to <paramref name="pb"/>.</summary>
    private static void AddBandRect(PathBuilder pb, bool horizontal, float s0, float s1, float near, float t)
    {
        pb.StartFigure();
        if (horizontal)
        {
            pb.MoveTo(new PointF(s0, near));
            pb.LineTo(new PointF(s1, near));
            pb.LineTo(new PointF(s1, near + t));
            pb.LineTo(new PointF(s0, near + t));
        }
        else
        {
            pb.MoveTo(new PointF(near, s0));
            pb.LineTo(new PointF(near + t, s0));
            pb.LineTo(new PointF(near + t, s1));
            pb.LineTo(new PointF(near, s1));
        }
        pb.CloseFigure();
    }

    /// <summary>
    /// Adds a circular dot figure (four cubic Bézier quadrants) to
    /// <paramref name="pb"/> so a whole dotted side stays one path.
    /// </summary>
    private static void AddDotFigure(PathBuilder pb, float cx, float cy, float r)
    {
        var k = ArcBezierK * r;
        pb.StartFigure();
        pb.MoveTo(new PointF(cx + r, cy));
        pb.AddCubicBezier(new PointF(cx + r, cy), new PointF(cx + r, cy + k), new PointF(cx + k, cy + r), new PointF(cx, cy + r));
        pb.AddCubicBezier(new PointF(cx, cy + r), new PointF(cx - k, cy + r), new PointF(cx - r, cy + k), new PointF(cx - r, cy));
        pb.AddCubicBezier(new PointF(cx - r, cy), new PointF(cx - r, cy - k), new PointF(cx - k, cy - r), new PointF(cx, cy - r));
        pb.AddCubicBezier(new PointF(cx, cy - r), new PointF(cx + k, cy - r), new PointF(cx + r, cy - k), new PointF(cx + r, cy));
        pb.CloseFigure();
    }

    /// <summary>
    /// Strokes one rounded-border quarter arc solid along the band centre
    /// line. (<paramref name="cx"/>, <paramref name="cy"/>) is the corner
    /// ellipse centre, <paramref name="rx"/>/<paramref name="ry"/> the outer
    /// corner radii, and <paramref name="sx"/>/<paramref name="sy"/> point
    /// from the centre toward the box corner (−1,−1 = top-left). The pen is
    /// as wide as the wider adjacent painted side.
    /// </summary>
    private static void DrawSolidCornerArc(DrawingCanvas canvas, float cx, float cy, float rx, float ry, float sx, float sy, bool hPainted, bool vPainted, float hW, float vW, CssColor hColor, CssColor vColor)
    {
        if (!hPainted && !vPainted)
        {
            return;
        }

        var penW = Math.Max(hPainted ? hW : 0f, vPainted ? vW : 0f);
        if (penW <= 0)
        {
            return;
        }

        var color = hPainted ? hColor : vColor;

        // Centre-line radii: the outer radius pulled in by half the adjacent
        // side’s width on each axis.
        var rcx = Math.Max(0f, rx - (vPainted ? vW : penW) / 2f);
        var rcy = Math.Max(0f, ry - (hPainted ? hW : penW) / 2f);
        if (rcx <= 0 && rcy <= 0)
        {
            return;
        }

        // S sits on the horizontal side’s centre line, E on the vertical’s.
        var s = new PointF(cx, cy + sy * rcy);
        var e = new PointF(cx + sx * rcx, cy);
        var c1 = new PointF(cx + sx * ArcBezierK * rcx, s.Y);
        var c2 = new PointF(e.X, cy + sy * ArcBezierK * rcy);
        var pb = new PathBuilder();
        pb.MoveTo(s);
        pb.AddCubicBezier(s, c1, c2, e);
        canvas.Draw(Pens.Solid(ToColor(color), penW), pb.Build());
    }

    // --- Tier 4 item 18 — Filter Effects 1 `filter` + `backdrop-filter` ------

    /// <summary>
    /// Renders display-list items from <paramref name="start"/> to
    /// <paramref name="end"/> (exclusive) into a transparent offscreen layer
    /// (Filter Effects 1 §10 group rendering), runs the filter chain over it IN
    /// ORDER, and blits the result through the current canvas transform. The
    /// offscreen covers the union of the border box and the bracketed items'
    /// painted bounds, padded by the chain's blur/offset halo
    /// (<see cref="FilterFunction.HaloPadding"/>) so the Gaussian never samples
    /// the layer edge — without the padding edges go dark and the blur is
    /// cropped.
    /// </summary>
    private void ApplyFilterGroup(DrawingCanvas canvas, IReadOnlyList<DisplayItem> items, int start, int end,
        PushFilter pushFilter, float scale, DisposableBag pendingImageSources, Stack<Matrix2D> transforms, LayoutRect target)
    {
        var filters = pushFilter.Filters;
        var degenerate = filters.Count == 0
            || pushFilter.Bounds.Width <= 0 || pushFilter.Bounds.Height <= 0;

        LayoutRect bounds = default;
        int px = 0, py = 0;
        if (!degenerate)
        {
            // Union the bracket's painted extents with the border box
            // (descendants may overflow it), then add the halo padding.
            bounds = UnionGroupBounds(items, start, end, pushFilter.Bounds);
            var pad = FilterFunction.HaloPadding(filters);
            bounds = new LayoutRect(bounds.X - pad, bounds.Y - pad, bounds.Width + 2 * pad, bounds.Height + 2 * pad);
            px = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale));
            py = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
            // Same offscreen size guard as the mask group path.
            degenerate = (long)px * py > 64L * 1024 * 1024;
        }

        if (degenerate)
        {
            // Nothing filterable (or an absurdly large surface) — replay the
            // bracketed items inline so the content still paints.
            var j = start;
            while (j < end)
            {
                j = ApplyAt(canvas, items, j, scale, pendingImageSources, transforms, target);
            }

            return;
        }

        // Render the bracketed items into a transparent offscreen layer whose
        // local origin is the padded bounds' top-left. The current canvas
        // transform (viewport/tile translate + any enclosing CSS transform) is
        // deliberately NOT composed in: canvas.DrawImage maps the blit's
        // destRect through that transform already, so baking it into the layer
        // would apply it twice — visible as a shifted/cropped group on the
        // tiled compositor path, whose per-tile viewport origin is non-zero.
        // (Rendering the group upright also matches the spec's order: filter
        // in local space, then the ancestor transform maps the result.)
        var layerViewportTransform = Matrix2D.Translate(-bounds.X, -bounds.Y);
        var contentLayer = new Image<Rgba32>(px, py, new Rgba32(0, 0, 0, 0));
        pendingImageSources.Add(contentLayer);
        contentLayer.Mutate(ctx => ctx.Paint(c =>
        {
            var layerTransforms = new Stack<Matrix2D>();
            layerTransforms.Push(layerViewportTransform);
            c.Save(new DrawingOptions { Transform = ToCanvasMatrix(layerViewportTransform, scale) });
            var i = start;
            while (i < end)
            {
                i = ApplyAt(c, items, i, scale, pendingImageSources, layerTransforms, target);
            }

            c.Restore();
        }));

        var filtered = ApplyFilterChain(contentLayer, filters, scale);
        if (!ReferenceEquals(filtered, contentLayer))
        {
            pendingImageSources.Add(filtered); // canvas.DrawImage rasterizes lazily
        }

        canvas.DrawImage(filtered, new Rectangle(0, 0, px, py),
            new RectangleF((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height),
            _resampler);
    }

    /// <summary>
    /// Page-space AABB union of the items in [start, end) seeded with the
    /// filter group's border box, tracking nested transform brackets the same
    /// way the layer-bounds union does.
    /// </summary>
    private static LayoutRect UnionGroupBounds(IReadOnlyList<DisplayItem> items, int start, int end, LayoutRect seed)
    {
        var minX = seed.X;
        var minY = seed.Y;
        var maxX = seed.Right;
        var maxY = seed.Bottom;
        var transform = Matrix2D.Identity;
        Stack<Matrix2D>? stack = null;
        for (var i = start; i < end; i++)
        {
            var item = items[i];
            switch (item)
            {
                case PushTransform p:
                    stack ??= new Stack<Matrix2D>();
                    stack.Push(transform);
                    transform = transform.Multiply(p.Matrix);
                    continue;
                case DisplayList.PopTransform:
                    if (stack is { Count: > 0 })
                    {
                        transform = stack.Pop();
                    }

                    continue;
            }
            if (!DisplayItemBounds.TryGet(item, out var local))
            {
                continue;
            }

            var aabb = TransformedAabb(local, transform);
            minX = Math.Min(minX, aabb.X);
            minY = Math.Min(minY, aabb.Y);
            maxX = Math.Max(maxX, aabb.Right);
            maxY = Math.Max(maxY, aabb.Bottom);
        }
        return new LayoutRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>
    /// Runs a resolved filter chain over <paramref name="layer"/> IN ORDER
    /// (Filter Effects 1 §10.1 — order is observable: brightness→invert differs
    /// from invert→brightness). The color functions map onto ImageSharp's
    /// color-matrix processors, which use the same Rec.709 luminance weights as
    /// the spec's matrices; blur runs the Gaussian at σ = radius/2 · scale (the
    /// repo's box-shadow σ convention, at device resolution). Drop-shadow
    /// rebuilds the layer (silhouette → blur → offset → source-over), so the
    /// returned image may differ from the input; intermediates are disposed
    /// here, the caller owns the input and the returned image.
    /// </summary>
    private static Image<Rgba32> ApplyFilterChain(Image<Rgba32> layer, IReadOnlyList<FilterFunction> filters, float scale)
    {
        var original = layer;
        for (var i = 0; i < filters.Count; i++)
        {
            var f = filters[i];
            switch (f.Kind)
            {
                case FilterFunctionKind.Blur:
                    {
                        var sigma = (float)(FilterFunction.BlurSigma(f.Amount) * scale);
                        if (sigma > 0)
                        {
                            layer.Mutate(ctx => ctx.GaussianBlur(sigma));
                        }

                        break;
                    }
                case FilterFunctionKind.Brightness:
                    {
                        var amount = (float)Math.Max(0, f.Amount);
                        layer.Mutate(ctx => ctx.Brightness(amount));
                        break;
                    }
                case FilterFunctionKind.Contrast:
                    {
                        var amount = (float)Math.Max(0, f.Amount);
                        layer.Mutate(ctx => ctx.Contrast(amount));
                        break;
                    }
                case FilterFunctionKind.Saturate:
                    {
                        var amount = (float)Math.Max(0, f.Amount);
                        layer.Mutate(ctx => ctx.Saturate(amount));
                        break;
                    }
                case FilterFunctionKind.Grayscale:
                    {
                        var amount = (float)Math.Clamp(f.Amount, 0, 1);
                        if (amount > 0)
                        {
                            layer.Mutate(ctx => ctx.Grayscale(amount));
                        }

                        break;
                    }
                case FilterFunctionKind.Sepia:
                    {
                        var amount = (float)Math.Clamp(f.Amount, 0, 1);
                        if (amount > 0)
                        {
                            layer.Mutate(ctx => ctx.Sepia(amount));
                        }

                        break;
                    }
                case FilterFunctionKind.HueRotate:
                    {
                        var degrees = (float)f.Amount;
                        if (degrees != 0)
                        {
                            layer.Mutate(ctx => ctx.Hue(degrees));
                        }

                        break;
                    }
                case FilterFunctionKind.Invert:
                    {
                        // §10.1: c' = a·(1−c) + (1−a)·c = (1−2a)·c + a — an affine
                        // color matrix (full channel flip at a = 1).
                        var a = (float)Math.Clamp(f.Amount, 0, 1);
                        if (a > 0)
                        {
                            var m = new ColorMatrix
                            {
                                M11 = 1 - 2 * a,
                                M22 = 1 - 2 * a,
                                M33 = 1 - 2 * a,
                                M44 = 1,
                                M51 = a,
                                M52 = a,
                                M53 = a,
                            };
                            layer.Mutate(ctx => ctx.Filter(m));
                        }
                        break;
                    }
                case FilterFunctionKind.Opacity:
                    {
                        var a = (float)Math.Clamp(f.Amount, 0, 1);
                        if (a < 1)
                        {
                            layer.Mutate(ctx => ctx.Opacity(a));
                        }

                        break;
                    }
                case FilterFunctionKind.DropShadow:
                    {
                        var next = ApplyDropShadowFilter(layer, f, scale);
                        if (!ReferenceEquals(layer, original))
                        {
                            layer.Dispose(); // intermediate from a previous drop-shadow
                        }

                        layer = next;
                        break;
                    }
            }
        }
        return layer;
    }

    /// <summary>
    /// drop-shadow(ox oy blur color) — Filter Effects 1 §10.1: a blurred,
    /// offset, tinted copy of the layer's alpha silhouette composited UNDER the
    /// layer (like the text-shadow / box-shadow rasterizers: silhouette → blur
    /// → offset → source over).
    /// </summary>
    private static Image<Rgba32> ApplyDropShadowFilter(Image<Rgba32> source, FilterFunction f, float scale)
    {
        var color = f.Color ?? CssColor.Black;

        // Tint the source's alpha with the shadow color.
        var shadow = new Image<Rgba32>(source.Width, source.Height, new Rgba32(0, 0, 0, 0));
        shadow.ProcessPixelRows(source, (shadowAccessor, sourceAccessor) =>
        {
            var height = Math.Min(shadowAccessor.Height, sourceAccessor.Height);
            for (var y = 0; y < height; y++)
            {
                var shadowRow = shadowAccessor.GetRowSpan(y);
                var sourceRow = sourceAccessor.GetRowSpan(y);
                var width = Math.Min(shadowRow.Length, sourceRow.Length);
                for (var x = 0; x < width; x++)
                {
                    var a = (sourceRow[x].A * color.A + 127) / 255;
                    shadowRow[x] = new Rgba32(color.R, color.G, color.B, (byte)a);
                }
            }
        });

        var sigma = (float)(FilterFunction.ShadowSigma(f.Amount) * scale);
        if (sigma > 0)
        {
            shadow.Mutate(ctx => ctx.GaussianBlur(sigma));
        }

        var dx = (int)Math.Round(f.OffsetX * scale);
        var dy = (int)Math.Round(f.OffsetY * scale);
        var result = new Image<Rgba32>(source.Width, source.Height, new Rgba32(0, 0, 0, 0));
        result.Mutate(ctx =>
        {
            ctx.DrawImage(shadow, new Point(dx, dy), 1f);
            ctx.DrawImage(source, new Point(0, 0), 1f);
        });
        shadow.Dispose(); // image.Mutate rasterizes immediately, unlike the canvas
        return result;
    }

    /// <summary>
    /// True when the list carries a <see cref="DrawBackdropFilter"/> outside
    /// any mask/filter offscreen group — only those can read the real canvas
    /// (a nested one would snapshot the group's transparent layer; skipped).
    /// </summary>
    private static bool ContainsTopLevelBackdropFilter(IReadOnlyList<DisplayItem> items)
    {
        var groupDepth = 0;
        for (var i = 0; i < items.Count; i++)
        {
            switch (items[i])
            {
                case PushMask or PushFilter:
                    groupDepth++;
                    break;
                case PopMask or PopFilter:
                    if (groupDepth > 0)
                    {
                        groupDepth--;
                    }

                    break;
                case DrawBackdropFilter when groupDepth == 0:
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// CPU replay that supports `backdrop-filter` (Filter Effects 2 §6). The
    /// DrawingCanvas records lazily inside one Mutate/Paint scope, so canvas
    /// pixels under an element cannot be read mid-replay; instead the list is
    /// replayed in segments. Everything before a top-level
    /// <see cref="DrawBackdropFilter"/> is rasterized into the image, the
    /// region under the element's border box (plus blur halo) is snapshotted,
    /// filtered, and drawn back clipped to the rounded border box, then replay
    /// continues — the element's own background/content paints over the
    /// filtered patch in the next segment. Transform/clip brackets still open
    /// at a segment boundary are re-opened at the start of the next segment
    /// with the matrices they were recorded with.
    /// <para>
    /// v1 simplifications: the snapshot reads the FLATTENED canvas painted so
    /// far (no isolation/backdrop-root grouping for overlapping promoted
    /// layers — which matches what this CPU path can see); ancestor clips do
    /// not crop the patch (it is clipped to the element's own border box
    /// only); the WebGPU canvas path skips backdrop-filter entirely.
    /// </para>
    /// </summary>
    private void ReplayListCpuSegmented(Image<Rgba32> image, PaintList list, int width, int height, float scale,
        Matrix2D viewportTransform, DisposableBag pendingImageSources)
    {
        var items = list.Items;
        var transforms = new Stack<Matrix2D>();
        transforms.Push(viewportTransform);
        var openBrackets = new List<(DisplayItem Item, Matrix2D Matrix)>();
        var target = new LayoutRect(0, 0, width / scale, height / scale);

        var segmentStart = 0;
        var groupDepth = 0;
        var i = 0;
        while (i < items.Count)
        {
            if (groupDepth == 0 && items[i] is DrawBackdropFilter backdrop)
            {
                ReplaySegment(image, items, segmentStart, i, scale, viewportTransform, pendingImageSources, transforms, openBrackets, target);
                ApplyBackdropFilterCpu(image, backdrop, transforms.Peek(), scale);
                segmentStart = i + 1;
                i++;
                continue;
            }
            switch (items[i])
            {
                case PushMask or PushFilter:
                    groupDepth++;
                    break;
                case PopMask or PopFilter:
                    if (groupDepth > 0)
                    {
                        groupDepth--;
                    }

                    break;
            }
            i++;
        }
        ReplaySegment(image, items, segmentStart, items.Count, scale, viewportTransform, pendingImageSources, transforms, openBrackets, target);
    }

    /// <summary>
    /// Rasterizes items [start, end) into <paramref name="image"/> through one
    /// Paint scope, re-opening the brackets left open by the previous segment
    /// and recording the brackets still open at the end (with the composed
    /// matrix each was opened under) so the next segment can re-open them.
    /// </summary>
    private void ReplaySegment(Image<Rgba32> image, IReadOnlyList<DisplayItem> items, int start, int end,
        float scale, Matrix2D viewportTransform, DisposableBag pendingImageSources,
        Stack<Matrix2D> transforms, List<(DisplayItem Item, Matrix2D Matrix)> openBrackets, LayoutRect target)
    {
        if (start >= end)
        {
            return;
        }

        image.Mutate(x => x.Paint(canvas =>
        {
            canvas.Save(new DrawingOptions { Transform = ToCanvasMatrix(viewportTransform, scale) });
            for (var b = 0; b < openBrackets.Count; b++)
            {
                ReopenBracket(canvas, openBrackets[b].Item, openBrackets[b].Matrix, scale);
            }

            var i = start;
            while (i < end)
            {
                var item = items[i];
                var next = ApplyAt(canvas, items, i, scale, pendingImageSources, transforms, target);
                switch (item)
                {
                    // Bracket bookkeeping AFTER Apply so PushTransform records
                    // the composed matrix (transforms.Peek() now includes it).
                    case PushTransform or PushClip or PushClipPath:
                        openBrackets.Add((item, transforms.Peek()));
                        break;
                    case DisplayList.PopTransform or DisplayList.PopClip or DisplayList.PopClipPath:
                        if (openBrackets.Count > 0)
                        {
                            openBrackets.RemoveAt(openBrackets.Count - 1);
                        }

                        break;
                }
                i = next;
            }

            // Balance this Paint scope's Save stack: one Restore per bracket
            // still open, plus the viewport Save.
            for (var b = 0; b < openBrackets.Count; b++)
            {
                canvas.Restore();
            }

            canvas.Restore();
        }));
    }

    /// <summary>Re-opens a bracket recorded by <see cref="ReplaySegment"/> in a
    /// fresh Paint scope, using the composed matrix it was recorded with.</summary>
    private static void ReopenBracket(DrawingCanvas canvas, DisplayItem item, Matrix2D matrix, float scale)
    {
        switch (item)
        {
            case PushTransform:
                // The recorded matrix is already the fully composed transform.
                canvas.Save(new DrawingOptions { Transform = ToCanvasMatrix(matrix, scale) });
                break;
            case PushClip pushClip:
                ApplyPushClip(canvas, pushClip, matrix, scale);
                break;
            case PushClipPath pushClipPath:
                ApplyPushClipPath(canvas, pushClipPath, matrix, scale);
                break;
        }
    }

    /// <summary>
    /// Snapshots the canvas region under the backdrop item's border box
    /// (expanded by the chain's blur halo so the Gaussian has real neighbours
    /// at the edge), runs the filter chain over the patch, and draws it back
    /// clipped to the rounded border box. The element's own background /
    /// content items follow in the next segment and paint over the patch.
    /// </summary>
    private static void ApplyBackdropFilterCpu(Image<Rgba32> image, DrawBackdropFilter backdrop, Matrix2D current, float scale)
    {
        if (backdrop.Filters.Count == 0)
        {
            return;
        }

        if (backdrop.Bounds.Width <= 0 || backdrop.Bounds.Height <= 0)
        {
            return;
        }

        // Device-space AABB of the border box under the current transform,
        // padded by the blur halo, clamped to the canvas.
        var aabb = TransformedAabb(backdrop.Bounds, current);
        var pad = FilterFunction.HaloPadding(backdrop.Filters);
        var x0 = Math.Max(0, (int)Math.Floor((aabb.X - pad) * scale));
        var y0 = Math.Max(0, (int)Math.Floor((aabb.Y - pad) * scale));
        var x1 = Math.Min(image.Width, (int)Math.Ceiling((aabb.Right + pad) * scale));
        var y1 = Math.Min(image.Height, (int)Math.Ceiling((aabb.Bottom + pad) * scale));
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        var region = new Rectangle(x0, y0, x1 - x0, y1 - y0);
        var patch = image.Clone(ctx => ctx.Crop(region));
        var filtered = ApplyFilterChain(patch, backdrop.Filters, scale);
        if (!ReferenceEquals(filtered, patch))
        {
            patch.Dispose();
        }

        // Keep only the pixels inside the (rounded) border box: transform the
        // CSS-px path to device pixels, shift it into patch-local coords, and
        // zero the alpha outside it. The padded margin thus feeds the Gaussian
        // but never paints back.
        var clipPath = BuildClipPath(backdrop.Bounds, backdrop.Radii)
            .Transform(ToCanvasMatrix(current, scale))
            .Translate(-x0, -y0);
        ApplyClipMask(filtered, clipPath);

        image.Mutate(ctx => ctx.DrawImage(filtered, new Point(x0, y0), 1f));
        filtered.Dispose(); // image.Mutate rasterizes immediately
    }

    public void Dispose()
    {
        // FontCollection has no Dispose. Clear cached Font entries so a
        // long-lived host releases per-spec lookup results.
        _boxShadowCache.Dispose();
        _conicCache.Dispose();
        _fontCache.Clear();
        _ = _fonts;
        _ = _webFonts;
    }

}
