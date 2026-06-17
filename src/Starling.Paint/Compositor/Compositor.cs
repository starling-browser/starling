using Starling.Common.Diagnostics;
using Starling.Common.Image;
using Starling.Css.Values;
using Starling.Layout;
using Starling.Paint.Backend;
using LayoutRect = Starling.Layout.Rect;

namespace Starling.Paint.Compositor;

internal sealed class Compositor
{
    private const int MaxUnculledTiles = 4;

    private readonly IPaintBackend _backend;

    // Session-scoped per-layer tile cache
    private readonly TileGrid _tileGrid;

    private int _frameTileHits;
    private int _frameTileMisses;

    public Compositor(IPaintBackend backend, TileGrid? tileGrid = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _tileGrid = tileGrid ?? new TileGrid();
    }

    public RenderedBitmap RenderGpuReadback(
        CompositorLayer root,
        LayoutRect viewport,
        float scale,
        IReadOnlyList<SurfaceOverlayLayer>? drawingOverlays = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            throw new ArgumentException("Viewport must have positive dimensions.", nameof(viewport));
        }

        if (!(scale > 0f))
        {
            throw new ArgumentException("Scale must be positive.", nameof(scale));
        }

        var gpu = GpuLayerCompositor.Shared
            ?? throw new InvalidOperationException("WebGPU composite blend is unavailable for the selected paint backend.");

        var width = (int)Math.Ceiling(viewport.Width * scale);
        var height = (int)Math.Ceiling(viewport.Height * scale);

        using var composite = StarlingTelemetry.Span(RenderMetrics.PaintArea, RenderMetrics.CompositeOp);
        StarlingTelemetry.Gauge(RenderMetrics.CompositeOutputAllocBytes, (double)width * height * 4);
        var output = new byte[checked(width * height * 4)];
        FillWhite(output);

        var ops = new List<LayerBlend>();
        CollectOps(root, ops, viewport, scale,
            ancestorTransform: Matrix2D.Identity, ancestorOpacity: 1f, ancestorClip: null,
            textureCache: gpu);
        var gpuOverlayLayers = BuildGpuOverlayLayers(viewport, scale, drawingOverlays);

        if (ops.Count > 0 || gpuOverlayLayers is { Count: > 0 })
        {
            gpu.Composite(output, width, height, ops, gpuOverlayLayers);
        }

        EmitTileFrameMetrics();
        return new RenderedBitmap(width, height, output);
    }

    public bool RenderToSurface(
        CompositorLayer root,
        LayoutRect viewport,
        float scale,
        GpuSurfacePresenter presenter,
        IReadOnlyList<SurfaceOverlayLayer>? drawingOverlays = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(presenter);
        if (viewport.Width <= 0 || viewport.Height <= 0)
        {
            throw new ArgumentException("Viewport must have positive dimensions.", nameof(viewport));
        }

        if (!(scale > 0f))
        {
            throw new ArgumentException("Scale must be positive.", nameof(scale));
        }

        var width = (int)Math.Ceiling(viewport.Width * scale);
        var height = (int)Math.Ceiling(viewport.Height * scale);

        var ops = new List<LayerBlend>();
        var textureCache = new SurfaceTextureCache(presenter);
        CollectOps(root, ops, viewport, scale,
            ancestorTransform: Matrix2D.Identity, ancestorOpacity: 1f, ancestorClip: null,
            textureCache: textureCache);

        var gpuOverlayLayers = BuildGpuOverlayLayers(viewport, scale, drawingOverlays);

        var presented = presenter.PresentOps(width, height, ops, gpuOverlayLayers);
        if (!presented)
        {
            throw new InvalidOperationException("GPU surface presenter did not present the frame.");
        }

        EmitTileFrameMetrics();
        return presented;
    }


    private static List<GpuOverlayLayer>? BuildGpuOverlayLayers(
        LayoutRect viewport,
        float scale,
        IReadOnlyList<SurfaceOverlayLayer>? overlays)
    {
        if (overlays is not { Count: > 0 })
        {
            return null;
        }

        var s = (double)scale;
        var pageToDevice = Matrix2D.Translate(-viewport.X * s, -viewport.Y * s).Multiply(Matrix2D.Scale(s, s));
        List<GpuOverlayLayer>? layers = null;
        foreach (var overlay in overlays)
        {
            if (overlay.Scene.Width <= 0 ||
                overlay.Scene.Height <= 0 ||
                overlay.Opacity <= 0 ||
                overlay.Scene.Commands.Count == 0)
            {
                continue;
            }

            var sceneToDevice = pageToDevice.Multiply(overlay.SceneToPage);
            var clipDevice = overlay.ClipPage is { } clip
                ? new Rect(
                    (clip.X - viewport.X) * s,
                    (clip.Y - viewport.Y) * s,
                    clip.Width * s,
                    clip.Height * s)
                : (Rect?)null;
            (layers ??= []).Add(new GpuOverlayLayer(
                overlay.Scene,
                sceneToDevice,
                Math.Clamp(overlay.Opacity, 0f, 1f),
                clipDevice));
        }

        return layers;
    }

    /// <summary>
    /// Appends one layer tree's blend ops into a shared list for a surface frame
    /// that composites several documents (e.g. engine-rendered chrome above the
    /// page). Each op's device mapping is post-translated by
    /// (<paramref name="destOriginXDevice"/>, <paramref name="destOriginYDevice"/>)
    /// so the tree lands in a sub-region of the swapchain, and clipped to
    /// <paramref name="regionClipDevice"/> so it can't draw outside that region.
    /// The presenter blends the combined list in one render pass, one present.
    /// </summary>
    internal void AppendSurfaceOps(
        CompositorLayer root,
        LayoutRect viewport,
        float scale,
        double destOriginXDevice,
        double destOriginYDevice,
        Rect? regionClipDevice,
        List<LayerBlend> ops,
        GpuSurfacePresenter presenter)
    {
        var local = new List<LayerBlend>();
        var textureCache = new SurfaceTextureCache(presenter);
        CollectOps(root, local, viewport, scale,
            ancestorTransform: Matrix2D.Identity, ancestorOpacity: 1f, ancestorClip: null,
            textureCache: textureCache);

        var offset = Matrix2D.Translate(destOriginXDevice, destOriginYDevice);
        foreach (var op in local)
        {
            var mapped = offset.Multiply(op.LocalToDevice);
            // The op's own clip is already in device space; shift it into the
            // region and intersect with the region bounds.
            Rect? clip = op.ClipDevice is { } c
                ? IntersectDeviceRect(c.Translate(destOriginXDevice, destOriginYDevice), regionClipDevice)
                : regionClipDevice;
            ops.Add(op.WithGeometry(mapped, clip));
        }
    }

    private static Rect? IntersectDeviceRect(Rect a, Rect? b)
    {
        if (b is not { } r)
        {
            return a;
        }

        var x = Math.Max(a.X, r.X);
        var y = Math.Max(a.Y, r.Y);
        var right = Math.Min(a.Right, r.Right);
        var bottom = Math.Min(a.Bottom, r.Bottom);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private void CollectOps(
        CompositorLayer layer,
        List<LayerBlend> ops,
        LayoutRect viewport,
        float scale,
        Matrix2D ancestorTransform,
        float ancestorOpacity,
        Rect? ancestorClip,
        IGpuLayerTextureCache? textureCache)
    {
        // Effective transform = ancestor × this layer's transform (post-multiply
        // so the ancestor's frame wraps the descendant, matching CSS Transforms
        // 1 §6.1 and the flat builder's nesting).
        var effectiveTransform = ancestorTransform.Multiply(layer.Transform);
        var effectiveOpacity = ancestorOpacity * layer.Opacity;

        // Clip is intersected in page space. The layer's own clip rect is in
        // page coords; ancestor clips were transformed into the same space as
        // they were intersected, so combine by plain rect intersection.
        var effectiveClip = IntersectClip(ancestorClip, layer.Clip);

        // Filter Effects 2 §6 — backdrop-filter. The op goes into the stream
        // BEFORE everything in this stacking context (including negative-z
        // child layers — they paint above the filtered backdrop), so the blend
        // path snapshots everything composited under the element, filters it,
        // and the element's content then paints over it and stays sharp.
        if (layer.BackdropFilters is { Count: > 0 } backdropFilters && effectiveOpacity > 0f)
        {
            EmitBackdropFilterOp(layer, backdropFilters, ops, viewport, scale,
                effectiveTransform, effectiveClip, textureCache);
        }

        // Children are already in paint order (sorted by z-index, then tree order,
        // at build time), so negative-z layers are the prefix. CSS-Position-3 §9:
        // they paint BEHIND this layer's own slice; non-negative layers paint over
        // it. Without this split a negative-z layer (e.g. a full-viewport `z-index:-1`
        // background overlay) would draw over the parent's in-flow text.
        var childIndex = 0;
        for (; childIndex < layer.Children.Count && layer.Children[childIndex].ZIndex < 0; childIndex++)
        {
            CollectOps(layer.Children[childIndex], ops, viewport, scale,
                effectiveTransform, effectiveOpacity, effectiveClip, textureCache);
        }

        if (layer.Bounds.Width > 0 && layer.Bounds.Height > 0 && effectiveOpacity > 0f)
        {
            // A layer-root `filter` chain rides the layer (LTF; see
            // CompositorLayer.Filters) and is applied ONCE to the whole rastered
            // layer here — not re-run inside every tile raster it overlaps.
            if (layer.Filters is { Count: > 0 } filters)
            {
                EmitFilteredLayer(layer, filters, ops, viewport, scale,
                    effectiveTransform, effectiveOpacity, effectiveClip, textureCache);
            }
            else
            {
                EmitLayerTiles(layer, ops, viewport, scale,
                    effectiveTransform, effectiveOpacity, effectiveClip, textureCache);
            }
        }

        for (; childIndex < layer.Children.Count; childIndex++)
        {
            CollectOps(layer.Children[childIndex], ops, viewport, scale,
                effectiveTransform, effectiveOpacity, effectiveClip, textureCache);
        }
    }

    private void EmitLayerTiles(
        CompositorLayer layer, List<LayerBlend> ops,
        LayoutRect viewport, float scale, Matrix2D effectiveTransform,
        float effectiveOpacity, Rect? effectiveClip, IGpuLayerTextureCache? textureCache)
    {
        var s = (double)scale;
        var bounds = layer.Bounds;
        // The layer's full extent in device px — the size the single-bitmap path used.
        var layerDevW = Math.Max(1, (int)Math.Ceiling(bounds.Width * s));
        var layerDevH = Math.Max(1, (int)Math.Ceiling(bounds.Height * s));

        var outW = (int)Math.Ceiling(viewport.Width * s);
        var outH = (int)Math.Ceiling(viewport.Height * s);

        var pageToDevice = Matrix2D.Translate(-viewport.X * s, -viewport.Y * s).Multiply(Matrix2D.Scale(s, s));
        // layer-local device px (0..layerDevW, 0..layerDevH) -> output device px.
        var layerLocalToDevice = pageToDevice.Multiply(effectiveTransform)
            .Multiply(Matrix2D.Translate(bounds.X, bounds.Y).Multiply(Matrix2D.Scale(1d / s, 1d / s)));

        // Device AABB of the page-space clip — applied to every tile (same as the
        // single-bitmap path applied one clip to the whole layer).
        Rect? clipDev = effectiveClip is { } cp
            ? TransformedAabb(new Rect(cp.X, cp.Y, cp.Width, cp.Height), pageToDevice)
            : null;

        const int TW = TileGrid.TileWidthDevice;
        const int TH = TileGrid.TileHeightDevice;
        var maxCol = (layerDevW - 1) / TW;
        var maxRow = (layerDevH - 1) / TH;

        // Default to the whole layer; viewport-cull only a LARGE, untransformed layer
        // (the page root is the case that matters — a long page must not raster its
        // full height). The inverse-transform cull is exact for an identity transform;
        // a transformed layer (an animating element) is small and cheap, so render all
        // its tiles rather than risk the fragile rotated-AABB cull dropping it.
        int col0 = 0, col1 = maxCol, row0 = 0, row1 = maxRow;
        var manyTiles = (long)(maxCol + 1) * (maxRow + 1) > MaxUnculledTiles;
        if (manyTiles && effectiveTransform.IsIdentity
            && !TryVisibleTileRange(layerLocalToDevice, outW, outH, layerDevW, layerDevH,
                out col0, out col1, out row0, out row1))
        {
            return; // layer entirely outside the viewport
        }

        // Pre-walk the slice once so each tile can be keyed by the content that
        // actually paints into IT (not the whole layer). This is the fix for the
        // whole-layer-invalidation lag: a localized change (one text node, a hover
        // colour) only changes the hash of the tiles it overlaps, so every other
        // visible tile still serves from cache.
        var prepared = _tileGrid.GetOrPrepare(layer.ContentHash, layer.Items);

        for (var row = row0; row <= row1; row++)
        {
            var th = Math.Min(TH, layerDevH - row * TH);
            if (th <= 0)
            {
                continue;
            }

            for (var col = col0; col <= col1; col++)
            {
                var tw = Math.Min(TW, layerDevW - col * TW);
                if (tw <= 0)
                {
                    continue;
                }

                // Tile page origin is the layer origin plus an integer device offset
                // (col*TW, row*TH) converted to CSS — keeps the tile on the layer's
                // device-pixel grid for seam-free, byte-identical abutment.
                var tilePageX = bounds.X + col * TW / s;
                var tilePageY = bounds.Y + row * TH / s;

                var tileRectPage = new LayoutRect(tilePageX, tilePageY, tw / s, th / s);
                // Freshness key is this tile's own content hash — only the items
                // that paint into this tile (plus bracket items) — so an unchanged
                // tile stays cached even when another tile's content changed.
                var tileHash = prepared.HashForTile(tileRectPage);

                var key = new TileKey(layer.LayerId, col, row, scale);
                var opHash = TileOpHash(tileHash, col, row);
                var tileLocalToDevice = pageToDevice.Multiply(effectiveTransform)
                    .Multiply(Matrix2D.Translate(tilePageX, tilePageY).Multiply(Matrix2D.Scale(1d / s, 1d / s)));

                if (textureCache is not null)
                {

                    EmitGpuTextureTile(ops, layer.Items, tileRectPage, scale, key, tileHash, opHash,
                        tileLocalToDevice, effectiveOpacity, clipDev, textureCache);
                    continue;
                }

                if (_tileGrid.TryGetTile(key, tileHash, out var tileBmp))
                {
                    _frameTileHits++;
                }
                else
                {
                    _frameTileMisses++;
                    tileBmp = _backend.Render(layer.Items, tileRectPage, scale, opaqueBackground: false);
                    _tileGrid.PutTile(key, tileHash, tileBmp);
                }

                // GPU texture key stays position-mixed so two distinct tiles never
                // alias one texture; seeded by the per-tile hash so a tile's texture
                // re-uploads only when ITS content changes.
                ops.Add(LayerBlend.Bitmap(tileBmp, opHash,
                    tileLocalToDevice, effectiveOpacity, clipDev));
            }
        }
    }

    private void EmitGpuTextureTile(
        List<LayerBlend> ops,
        DisplayList.DisplayList items,
        LayoutRect tileRectPage,
        float scale,
        TileKey key,
        long tileHash,
        long opHash,
        Matrix2D tileLocalToDevice,
        float effectiveOpacity,
        Rect? clipDev,
        IGpuLayerTextureCache textureCache)
    {
        if (_tileGrid.TryGetResidentTile(key, tileHash, out var resident)
            && textureCache.HasResidentTexture(opHash, resident.Width, resident.Height))
        {
            _frameTileHits++;
            ops.Add(LayerBlend.ResidentTexture(
                resident.Width,
                resident.Height,
                opHash,
                tileLocalToDevice,
                effectiveOpacity,
                clipDev));
            return;
        }

        _frameTileMisses++;
        if (_backend is not IGpuTexturePaintBackend gpuBackend)
        {
            throw new InvalidOperationException("GPU surface rendering requires a GPU texture paint backend.");
        }

        GpuPaintTexture? texture = null;
        try
        {
            texture = gpuBackend.RenderTexture(
                items,
                tileRectPage,
                scale,
                opaqueBackground: false,
                textureCache.GpuDevice);

            textureCache.AdoptTexture(opHash, texture);
            _tileGrid.PutResidentTile(key, tileHash, texture.Width, texture.Height);
            ops.Add(LayerBlend.ResidentTexture(
                texture.Width,
                texture.Height,
                opHash,
                tileLocalToDevice,
                effectiveOpacity,
                clipDev));
            texture = null;
        }
        finally
        {
            texture?.Dispose();
        }
    }

    private void EmitFilteredLayer(
        CompositorLayer layer, IReadOnlyList<DisplayList.FilterFunction> filters, List<LayerBlend> ops,
        LayoutRect viewport, float scale, Matrix2D effectiveTransform,
        float effectiveOpacity, Rect? effectiveClip, IGpuLayerTextureCache? textureCache)
    {
        var s = (double)scale;
        var pad = DisplayList.FilterFunction.HaloPadding(filters);
        var bounds = layer.Bounds;
        var padded = new LayoutRect(bounds.X - pad, bounds.Y - pad, bounds.Width + 2 * pad, bounds.Height + 2 * pad);

        var pageToDevice = Matrix2D.Translate(-viewport.X * s, -viewport.Y * s).Multiply(Matrix2D.Scale(s, s));
        var pageTransform = pageToDevice.Multiply(effectiveTransform);
        var devAabb = TransformedAabb(new Rect(padded.X, padded.Y, padded.Width, padded.Height), pageTransform);
        var outW = (int)Math.Ceiling(viewport.Width * s);
        var outH = (int)Math.Ceiling(viewport.Height * s);
        if (devAabb.Right < 0 || devAabb.Bottom < 0 || devAabb.X > outW || devAabb.Y > outH)
        {
            return;
        }

        Rect? clipDev = effectiveClip is { } cp
            ? TransformedAabb(new Rect(cp.X, cp.Y, cp.Width, cp.Height), pageToDevice)
            : null;

        var key = new TileKey(layer.LayerId != 0 ? layer.LayerId : layer.ContentHash, -1, -1, scale);
        var devW = Math.Max(1, (int)Math.Ceiling(padded.Width * s));
        var devH = Math.Max(1, (int)Math.Ceiling(padded.Height * s));
        var opHash = FilteredOpHash(layer.ContentHash, devW, devH);

        if (textureCache is not null)
        {
            if (_tileGrid.TryGetResidentTile(key, opHash, out var resident)
                && textureCache.HasResidentTexture(opHash, resident.Width, resident.Height))
            {
                _frameTileHits++;
                ops.Add(LayerBlend.ResidentTexture(resident.Width, resident.Height, opHash,
                    FilteredLocalToDevice(pageTransform, padded, resident.Width, resident.Height),
                    effectiveOpacity, clipDev));
                return;
            }

            _frameTileMisses++;
            if (_backend is not IGpuTexturePaintBackend gpuBackend)
            {
                throw new InvalidOperationException("GPU surface rendering requires a GPU texture paint backend.");
            }

            var source = gpuBackend.RenderTexture(layer.Items, padded, scale, opaqueBackground: false, textureCache.GpuDevice);
            var filtered = textureCache.ApplyLayerFilters(source, filters, scale);
            if (filtered is null)
            {
                EmitLayerTiles(WrapInFilterBracket(layer, filters, padded), ops, viewport, scale,
                    effectiveTransform, effectiveOpacity, effectiveClip, textureCache);
                return;
            }

            textureCache.AdoptTexture(opHash, filtered);
            _tileGrid.PutResidentTile(key, opHash, filtered.Width, filtered.Height);
            ops.Add(LayerBlend.ResidentTexture(filtered.Width, filtered.Height, opHash,
                FilteredLocalToDevice(pageTransform, padded, filtered.Width, filtered.Height),
                effectiveOpacity, clipDev));
            return;
        }

        if (_tileGrid.TryGetTile(key, opHash, out var bmp))
        {
            _frameTileHits++;
        }
        else
        {
            _frameTileMisses++;
            bmp = _backend.RenderFiltered(layer.Items, padded, scale, filters);
            _tileGrid.PutTile(key, opHash, bmp);
        }

        ops.Add(LayerBlend.Bitmap(bmp, opHash,
            FilteredLocalToDevice(pageTransform, padded, bmp.Width, bmp.Height),
            effectiveOpacity, clipDev));
    }

    private static void EmitBackdropFilterOp(
        CompositorLayer layer, IReadOnlyList<DisplayList.FilterFunction> filters, List<LayerBlend> ops,
        LayoutRect viewport, float scale, Matrix2D effectiveTransform, Rect? effectiveClip,
        IGpuLayerTextureCache? textureCache)
    {
        if (textureCache?.SupportsLayerFilters(filters) == false)
        {
            return;
        }

        var bounds = layer.BackdropBounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var s = (double)scale;
        var pageToDevice = Matrix2D.Translate(-viewport.X * s, -viewport.Y * s).Multiply(Matrix2D.Scale(s, s));
        var pageTransform = pageToDevice.Multiply(effectiveTransform);
        // Device AABB of the element's border box. A rotated element filters
        // the AABB of its box — a v1 approximation, same as the clip handling.
        var devRect = TransformedAabb(new Rect(bounds.X, bounds.Y, bounds.Width, bounds.Height), pageTransform);

        var outW = (int)Math.Ceiling(viewport.Width * s);
        var outH = (int)Math.Ceiling(viewport.Height * s);
        var pad = DisplayList.FilterFunction.HaloPadding(filters) * s;
        var x0 = Math.Max(0, (int)Math.Floor(devRect.X - pad));
        var y0 = Math.Max(0, (int)Math.Floor(devRect.Y - pad));
        var x1 = Math.Min(outW, (int)Math.Ceiling(devRect.Right + pad));
        var y1 = Math.Min(outH, (int)Math.Ceiling(devRect.Bottom + pad));
        if (x1 <= x0 || y1 <= y0)
        {
            return;
        }

        Rect? clipDev = devRect;
        if (effectiveClip is { } cp)
        {
            clipDev = IntersectDeviceRect(devRect, TransformedAabb(new Rect(cp.X, cp.Y, cp.Width, cp.Height), pageToDevice));
        }

        if (clipDev is { Width: <= 0 } or { Height: <= 0 })
        {
            return;
        }

        var layerKey = layer.LayerId != 0 ? layer.LayerId : layer.ContentHash;
        ops.Add(LayerBlend.BackdropFilter(x1 - x0, y1 - y0,
            BackdropOpHash(layerKey, x0, y0),
            Matrix2D.Translate(x0, y0), clipDev, filters, scale));
    }

    private static long BackdropOpHash(long layerKey, int x, int y)
        => unchecked(((layerKey ^ 0x4241434B44524F50L) * 1099511628211L) ^ (((long)x << 21) | (uint)y));

    /// <summary>
    /// Maps a filtered surface's texel space (0..texW, 0..texH) onto output
    /// device px. Unlike a tile (always 1 texel = 1/scale page px), the filtered
    /// result may be smaller than its padded page rect (large blurs render at
    /// reduced resolution), so the scale is derived from the rect/texture ratio
    /// and the blend quad's linear sampling upscales.
    /// </summary>
    private static Matrix2D FilteredLocalToDevice(Matrix2D pageTransform, LayoutRect padded, int texW, int texH)
        => pageTransform.Multiply(
            Matrix2D.Translate(padded.X, padded.Y)
                .Multiply(Matrix2D.Scale(padded.Width / Math.Max(1, texW), padded.Height / Math.Max(1, texH))));

    // Whole-surface op key for a filtered layer. The tag keeps it out of every
    // TileOpHash (which mixes non-negative col/row) and the overlay-swatch range.
    private static long FilteredOpHash(long layerContentHash, int devW, int devH)
        => unchecked(((layerContentHash ^ (long)0x46494C5445520000) * 1099511628211L)
                     ^ (((long)devW << 21) | (uint)devH));

    /// <summary>
    /// Legacy fallback: re-wraps a filtered layer's slice in its PushFilter
    /// bracket — the form the per-tile raster path applies inline — with bounds
    /// padded for the blur halo so the tiles cover the bleed. Keeps oversized
    /// surfaces and GPU-unsupported chains rendering exactly as before.
    /// </summary>
    private static CompositorLayer WrapInFilterBracket(
        CompositorLayer layer, IReadOnlyList<DisplayList.FilterFunction> filters, LayoutRect paddedBounds)
    {
        var items = new DisplayList.DisplayList();
        // The bracket seeds the group-bounds union with the element's border box
        // when the source box is known (what Visit used to emit), else the
        // painted union — either is a valid seed, the union only grows.
        var bracketBounds = layer.SourceBox is { } src
            ? new Rect(layer.OriginParentX + src.Frame.X, layer.OriginParentY + src.Frame.Y, src.Frame.Width, src.Frame.Height)
            : layer.Bounds;
        items.Add(new DisplayList.PushFilter(bracketBounds, filters));
        var slice = layer.Items.Items;
        for (var i = 0; i < slice.Count; i++)
        {
            items.Add(slice[i]);
        }

        items.Add(DisplayList.PopFilter.Instance);
        return new CompositorLayer(items, paddedBounds, layer.Transform, layer.Opacity, layer.Clip,
            [], layer.ContentHash, layer.LayerId,
            layer.SourceBox, layer.OriginParentX, layer.OriginParentY, layer.ZIndex, layer.InheritedClip);
    }

    /// <summary>
    /// Flushes the per-frame tile metrics for the multi-document surface path
    /// (<see cref="AppendSurfaceOps"/> ×N then a single present), which has no
    /// single Render/RenderToSurface exit to hook. The host calls this once after
    /// the last AppendSurfaceOps; a Compositor is one frame, so the accumulated
    /// hit/miss tally is exactly this frame's.
    /// </summary>
    internal void FlushTileFrameMetrics() => EmitTileFrameMetrics();

    // Emits the per-frame tile cache signals once the frame's ops are collected:
    // the miss ratio (≈1.0 while only a small region changed is the smoking gun for
    // whole-layer invalidation) and how many tiles this frame had to raster.
    private void EmitTileFrameMetrics()
    {
        var total = _frameTileHits + _frameTileMisses;
        if (total == 0)
        {
            return;
        }

        StarlingTelemetry.Gauge(RenderMetrics.TileMissRatio, (double)_frameTileMisses / total);
        StarlingTelemetry.Counter(RenderMetrics.TileRastersPerFrame, _frameTileMisses);
    }

    // Per-tile op key for the GPU texture cache: unique per (tile position, content),
    // so each tile uploads one texture and re-uploads when the layer content changes.
    // FNV-style mix; stays out of the 0xC0FFEE… overlay-swatch range.
    private static long TileOpHash(long layerContentHash, int col, int row)
        => unchecked((layerContentHash * 1099511628211L) ^ (((long)col << 21) | (uint)row));

    /// <summary>
    /// Computes the inclusive tile column/row range of <paramref name="layerLocalToDevice"/>
    /// whose tiles intersect the output rect (<paramref name="outW"/>×<paramref name="outH"/>),
    /// expanded by a 1-tile overdraw ring and clamped to the layer's grid. Returns false
    /// when the transform is degenerate or the layer is wholly outside the viewport.
    /// </summary>
    private static bool TryVisibleTileRange(
        Matrix2D layerLocalToDevice, int outW, int outH, int layerDevW, int layerDevH,
        out int col0, out int col1, out int row0, out int row1)
    {
        col0 = col1 = row0 = row1 = 0;
        if (!TryInvert(layerLocalToDevice, out var inv))
        {
            return false;
        }

        // Inverse-map the output rect corners into layer-local device space, take AABB.
        var (ax, ay) = inv.Transform(0, 0);
        var (bx, by) = inv.Transform(outW, 0);
        var (cx, cy) = inv.Transform(outW, outH);
        var (dx, dy) = inv.Transform(0, outH);
        var minX = Math.Max(0d, Math.Min(Math.Min(ax, bx), Math.Min(cx, dx)));
        var minY = Math.Max(0d, Math.Min(Math.Min(ay, by), Math.Min(cy, dy)));
        var maxX = Math.Min((double)layerDevW, Math.Max(Math.Max(ax, bx), Math.Max(cx, dx)));
        var maxY = Math.Min((double)layerDevH, Math.Max(Math.Max(ay, by), Math.Max(cy, dy)));
        if (maxX <= minX || maxY <= minY)
        {
            return false;
        }

        const int TW = TileGrid.TileWidthDevice;
        const int TH = TileGrid.TileHeightDevice;
        var maxCol = (layerDevW - 1) / TW;
        var maxRow = (layerDevH - 1) / TH;
        col0 = Math.Clamp((int)Math.Floor(minX / TW) - 1, 0, maxCol);
        col1 = Math.Clamp((int)Math.Floor((maxX - 1e-3) / TW) + 1, 0, maxCol);
        row0 = Math.Clamp((int)Math.Floor(minY / TH) - 1, 0, maxRow);
        row1 = Math.Clamp((int)Math.Floor((maxY - 1e-3) / TH) + 1, 0, maxRow);
        return col1 >= col0 && row1 >= row0;
    }

    private static void FillWhite(byte[] buf)
    {
        for (var i = 0; i < buf.Length; i++)
        {
            buf[i] = 255;
        }
    }

    private static Rect? IntersectClip(Rect? a, Rect? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        var x = Math.Max(a.Value.X, b.Value.X);
        var y = Math.Max(a.Value.Y, b.Value.Y);
        var right = Math.Min(a.Value.Right, b.Value.Right);
        var bottom = Math.Min(a.Value.Bottom, b.Value.Bottom);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static Rect TransformedAabb(Rect r, Matrix2D m)
    {
        var (x0, y0) = m.Transform(r.X, r.Y);
        var (x1, y1) = m.Transform(r.X + r.Width, r.Y);
        var (x2, y2) = m.Transform(r.X + r.Width, r.Y + r.Height);
        var (x3, y3) = m.Transform(r.X, r.Y + r.Height);
        var minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
        var minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
        var maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
        var maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>Inverts a 2D affine <see cref="Matrix2D"/>; false if singular.</summary>
    private static bool TryInvert(Matrix2D m, out Matrix2D inverse)
    {
        var det = m.A * m.D - m.B * m.C;
        if (Math.Abs(det) < 1e-12)
        {
            inverse = Matrix2D.Identity;
            return false;
        }
        var invDet = 1d / det;
        var a = m.D * invDet;
        var b = -m.B * invDet;
        var c = -m.C * invDet;
        var d = m.A * invDet;
        var e = -(a * m.E + c * m.F);
        var f = -(b * m.E + d * m.F);
        inverse = new Matrix2D(a, b, c, d, e, f);
        return true;
    }

    private readonly struct SurfaceTextureCache(GpuSurfacePresenter presenter) : IGpuLayerTextureCache
    {
        public bool HasResidentTexture(long contentHash, int width, int height)
            => presenter.HasResidentTexture(contentHash, width, height);

        public GpuPaintDevice GpuDevice => presenter.GpuDevice;

        public void AdoptTexture(long contentHash, GpuPaintTexture texture)
            => presenter.AdoptTexture(contentHash, texture);

        public bool SupportsLayerFilters(IReadOnlyList<DisplayList.FilterFunction> filters)
            => GpuSurfacePresenter.SupportsLayerFilters(filters);

        public GpuPaintTexture? ApplyLayerFilters(GpuPaintTexture source,
            IReadOnlyList<DisplayList.FilterFunction> filters, float scale)
            => presenter.ApplyLayerFilters(source, filters, scale);
    }
}
