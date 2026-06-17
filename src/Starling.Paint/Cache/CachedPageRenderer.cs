using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Css.Properties;
using Starling.Dom;
using Starling.Layout.Box;
using Starling.Layout.Compositor;
using Starling.Layout.Tree;
using Starling.Paint.Backend;
using Starling.Paint.DisplayList;
using LayoutRect = Starling.Layout.Rect;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint.Cache;

/// <summary>
/// Wraps an <see cref="IPaintBackend"/> with a <see cref="PictureCache"/> so
/// scrolling re-uses the previous frame's pixels instead of re-painting the whole
/// viewport. On a cache HIT the cached pixels are blitted into the output with no
/// backend call; on a PARTIAL the newly-exposed strips are painted (each via the
/// viewport-clipped <see cref="DisplayListBuilder"/> + backend path) and stitched
/// in; on a MISS the full viewport is painted and seeds the cache. The full-page
/// (no-viewport) path bypasses the cache entirely so headless screenshots stay
/// byte-for-byte identical.
/// </summary>
internal sealed class CachedPageRenderer
{
    private readonly IPaintBackend _backend;
    private readonly PictureCache _cache;
    // Memoized "this layout has at least one position: fixed subtree" answer.
    // Tree walks for the check are O(boxes), so re-using the result across
    // every scroll frame of the same laid-out page is worth the int+bool.
    private int _fixedScanVersion = -1;
    private bool _hasFixed;

    public CachedPageRenderer(IPaintBackend backend, PictureCache? cache = null)
    {
        ArgumentNullException.ThrowIfNull(backend);
        _backend = backend;
        _cache = cache ?? new PictureCache();
    }

    /// <summary>Drops cached pixels — call on navigation / tab switch / re-layout.</summary>
    public void Invalidate() => _cache.Invalidate();

    /// <summary>
    /// Renders <paramref name="root"/> for <paramref name="viewport"/> at
    /// <paramref name="scale"/>, consulting the cache keyed by
    /// <paramref name="pageVersion"/>. The output is a <see cref="RenderedBitmap"/>
    /// for the requested viewport, identical to a from-scratch render of the same
    /// region.
    /// </summary>
    public RenderedBitmap Render(
        BlockBox root,
        LayoutRect viewport,
        float scale,
        int pageVersion,
        Func<Box, ComputedStyle?>? styleOverride = null,
        IImageResolver? images = null,
        Func<Element, (double X, double Y)>? scrollOffsets = null,
        Func<Element, (double X, double Y)>? stickyShifts = null)
    {
        ArgumentNullException.ThrowIfNull(root);

        var device = PictureCache.ToDeviceRect(viewport, scale);

        // Pages with `position: fixed` subtrees, or with `overflow: scroll |
        // auto` containers whose offsets the user can change at any moment,
        // can't reuse cached strips: the cached pixels bake one scroll/offset
        // configuration in, but those subtrees need to repaint at the current
        // configuration every frame. The painter handles the per-frame
        // positioning (viewport translation for fixed, scroll-offset
        // translation for overflow containers); we just need to bypass the
        // strip-reuse path so each frame is a fresh seed-and-serve. Detect
        // once per laid-out page (pageVersion-keyed) since the box tree only
        // changes on relayout.
        if (HasFixedOrScrollSubtree(root, pageVersion))
        {
            _cache.Invalidate();
            return SeedAndServe(root, viewport, scale, pageVersion, device, styleOverride, images, scrollOffsets, stickyShifts);
        }

        if (_cache.TryServe(viewport, scale, pageVersion, out var hit))
        {
            return Compose(hit, device.Width, device.Height);
        }

        var strips = _cache.ComputeUncachedStrips(viewport, scale, pageVersion);

        // Full miss: one strip equal to the whole device rect. Paint it, seed,
        // and serve from the now-complete cache.
        if (strips.Count == 1 && strips[0].Equals(device))
        {
            return SeedAndServe(root, viewport, scale, pageVersion, device, styleOverride, images, scrollOffsets, stickyShifts);
        }

        // Partial: paint each newly-exposed strip, then slide the cache window onto
        // the new viewport — retaining the still-visible overlap and dropping the
        // rows that scrolled off-screen. The cache stays viewport-sized, so a long
        // scroll never grows it without bound and never pays for a full-viewport
        // reseed; only the thin exposed strips are ever repainted.
        var painted = new List<(DeviceRect Rect, RenderedBitmap Pixels)>(strips.Count);
        try
        {
            foreach (var strip in strips)
            {
                var bmp = PaintStrip(root, strip, scale, styleOverride, images, scrollOffsets, stickyShifts);
                // Track the raster's real device rect (origin + actual size); a
                // fractional scale's ceil can differ from the requested strip.
                painted.Add((new DeviceRect(strip.X, strip.Y, bmp.Width, bmp.Height), bmp));
            }

            if (!_cache.SlideTo(device, scale, pageVersion, painted))
            {
                // Stale key slipped past the strip computation — reseed wholesale.
                _cache.Invalidate();
                return SeedAndServe(root, viewport, scale, pageVersion, device, styleOverride, images, scrollOffsets, stickyShifts);
            }
        }
        finally
        {
            foreach (var (_, bmp) in painted)
            {
                bmp.Dispose();
            }
        }
        return ServeAfterFill(viewport, scale, pageVersion, device);
    }

    private RenderedBitmap SeedAndServe(
        BlockBox root,
        LayoutRect viewport,
        float scale,
        int pageVersion,
        DeviceRect device,
        Func<Box, ComputedStyle?>? styleOverride,
        IImageResolver? images,
        Func<Element, (double X, double Y)>? scrollOffsets,
        Func<Element, (double X, double Y)>? stickyShifts)
    {
        using var full = PaintStrip(root, device, scale, styleOverride, images, scrollOffsets, stickyShifts);
        // Reset against the raster's real device rect (origin + actual size); a
        // fractional scale's ceil can differ from the request by a pixel.
        var seedRect = new DeviceRect(device.X, device.Y, full.Width, full.Height);
        _cache.Reset(seedRect, scale, pageVersion, full);
        return ServeAfterFill(viewport, scale, pageVersion, device);
    }

    private RenderedBitmap ServeAfterFill(LayoutRect viewport, float scale, int pageVersion, DeviceRect device)
    {
        // TryServeRaw (not TryServe): the frame's outcome was already counted as a
        // miss/partial; assembling the output must not also bump the hit counter.
        if (!_cache.TryServeRaw(viewport, scale, pageVersion, out var blit))
        {
            throw new InvalidOperationException("Cache should fully cover the viewport after seed/stitch.");
        }

        return Compose(blit, device.Width, device.Height);
    }

    /// <summary>
    /// Paints a single device-pixel strip through the viewport-clipped builder +
    /// backend. The strip's device rect is converted back to a page-coord viewport
    /// (device / scale) so the backend translates content to the strip's top-left;
    /// the result is therefore directly stitchable at the strip's cache position.
    /// </summary>
    private RenderedBitmap PaintStrip(
        BlockBox root,
        DeviceRect strip,
        float scale,
        Func<Box, ComputedStyle?>? styleOverride,
        IImageResolver? images,
        Func<Element, (double X, double Y)>? scrollOffsets,
        Func<Element, (double X, double Y)>? stickyShifts)
    {
        var pageViewport = new LayoutRect(strip.X / scale, strip.Y / scale, strip.Width / scale, strip.Height / scale);
        PaintList list = new DisplayListBuilder().Build(root, pageViewport, styleOverride, images, scrollOffsets, canvasRect: null, stickyShifts: stickyShifts);
        return _backend.Render(list, pageViewport, scale);
    }

    private bool HasFixedOrScrollSubtree(BlockBox root, int pageVersion)
    {
        if (_fixedScanVersion == pageVersion)
        {
            return _hasFixed;
        }

        _hasFixed = ContainsFixedOrScrollHint(root);
        _fixedScanVersion = pageVersion;
        return _hasFixed;
    }

    private static bool ContainsFixedOrScrollHint(Box box)
    {
        if ((box.Hints & LayerHint.Fixed) != LayerHint.None)
        {
            return true;
        }

        if (box.Style is { } style && (IsScrollContainer(style) || IsSticky(style)))
        {
            return true;
        }

        foreach (var child in box.Children)
        {
            if (ContainsFixedOrScrollHint(child))
            {
                return true;
            }
        }

        return false;
    }

    // position:sticky repaints at a scroll-dependent position (scroll-model.md
    // WP5), so cached strips would bake one scroll configuration in — same
    // bypass as fixed subtrees.
    private static bool IsSticky(ComputedStyle style)
        => style.Get(PropertyId.Position) is Starling.Css.Values.CssKeyword k
           && k.Name.Equals("sticky", StringComparison.OrdinalIgnoreCase);

    private static bool IsScrollContainer(ComputedStyle style)
        => IsScrollKeyword(style.Get(PropertyId.OverflowX))
        || IsScrollKeyword(style.Get(PropertyId.OverflowY));

    private static bool IsScrollKeyword(Starling.Css.Values.CssValue? value)
        => value is Starling.Css.Values.CssKeyword { Name: var n }
            && (n.Equals("scroll", StringComparison.OrdinalIgnoreCase)
             || n.Equals("auto", StringComparison.OrdinalIgnoreCase));

    private static RenderedBitmap Compose(CacheBlit blit, int outWidth, int outHeight)
    {
        var outBuf = new byte[checked(outWidth * outHeight * 4)];
        var destStride = outWidth * 4;
        var rowBytes = blit.Width * 4;
        for (var row = 0; row < blit.Height; row++)
        {
            var srcOffset = ((blit.SourceY + row) * blit.SourceStride) + (blit.SourceX * 4);
            var destOffset = ((blit.DestY + row) * destStride) + (blit.DestX * 4);
            Array.Copy(blit.SourcePixels, srcOffset, outBuf, destOffset, rowBytes);
        }
        return new RenderedBitmap(outWidth, outHeight, outBuf);
    }
}
