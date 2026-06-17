using Starling.Common.Diagnostics;
using Starling.Common.Image;
using LayoutRect = Starling.Layout.Rect;

namespace Starling.Paint.Cache;

/// <summary>
/// A WebRender-style single-bitmap picture cache. Holds the most recently
/// rasterized viewport as a tightly-packed RGBA8888 buffer plus the device-pixel
/// rectangle it covers, keyed by <c>(pageVersion, scale)</c>. A scroll whose new
/// viewport falls entirely inside the cached rectangle is served by blitting the
/// cached pixels at an offset (no backend call). A scroll that exposes a fresh
/// edge paints only the newly-visible strips and then <em>slides</em> the cache
/// window onto the new viewport (<see cref="SlideTo"/>): the overlap with the
/// previous window is retained, the freshly-painted strips fill the exposed
/// region, and the rows that scrolled off-screen are dropped. The cache buffer is
/// therefore always exactly the requested viewport — it never grows with scroll
/// distance, so a long scroll never accumulates the whole scrolled-through
/// bounding box and never triggers a full-viewport reseed mid-scroll.
/// </summary>
/// <remarks>
/// Coordinate spaces: the cache works entirely in <em>device pixels</em>
/// (page CSS px × <c>scale</c>, rounded to whole integers). Page-coord viewports
/// are converted on the boundary via <see cref="ToDeviceRect"/>. Keeping the
/// internal math integer-only is what guarantees a HIT/PARTIAL serve is
/// byte-identical to a from-scratch render: the backend lands a page-coord item
/// at device <c>item.X*scale - viewport.X*scale</c>, so as long as every viewport
/// origin is at a whole device pixel, two aligned viewports differ only by an
/// integer device offset and row copies reproduce pixels exactly. This is also
/// why strips are rounded to whole device pixels (WP note: "rounds to whole
/// device pixels to avoid aliasing").
/// <para>
/// This is intentionally a single viewport-sized bitmap; an LRU tile grid (which
/// would let off-screen scrolled-through content be retained across a long scroll
/// instead of dropped) is <c>wp:M12-05-tile-grid</c>. Any display-list change is signalled by a bumped
/// <c>pageVersion</c> and invalidates the whole cache — smarter partial
/// invalidation is <c>wp:M12-06-invalidation</c>.
/// </para>
/// </remarks>
internal sealed class PictureCache
{
    // Backing pixels for the cached region, RGBA8888, row stride _bounds.Width*4.
    private byte[]? _pixels;
    private DeviceRect _bounds;
    private float _scale;
    // 64-bit so the layer compositor can key by a slice content hash (LTF-02);
    // the flat scroll path passes its int DisplayListVersion, which widens.
    private long _pageVersion;

    /// <summary>Creates an empty cache.</summary>
    public PictureCache()
    {
    }

    /// <summary>True once a frame has seeded the cache for the current key.</summary>
    public bool HasContent => _pixels is not null;

    /// <summary>
    /// Attempts to serve <paramref name="viewport"/> entirely from cache. Returns
    /// <c>true</c> (HIT) when <paramref name="pageVersion"/> and
    /// <paramref name="scale"/> match the cached key and the requested device rect
    /// is fully contained in the cached rect; <paramref name="blit"/> then
    /// describes the sub-rect of the cache buffer to copy and where it lands in
    /// the (0,0)-origin output. Bumps <c>paint.cache.hit</c> or
    /// <c>paint.cache.miss</c>.
    /// </summary>
    public bool TryServe(LayoutRect viewport, float scale, long pageVersion, out CacheBlit blit)
    {
        if (!TryServeRaw(viewport, scale, pageVersion, out blit))
        {
            return false;
        }

        StarlingTelemetry.Counter("paint.cache.hit", 1);
        return true;
    }

    /// <summary>
    /// Same containment check + blit computation as <see cref="TryServe"/> but
    /// without bumping <c>paint.cache.hit</c>. Used to assemble the output after a
    /// seed/stitch on a MISS/PARTIAL frame, where the frame's outcome counter has
    /// already been recorded and the serve must not also count as a hit.
    /// </summary>
    public bool TryServeRaw(LayoutRect viewport, float scale, long pageVersion, out CacheBlit blit)
    {
        blit = default;
        var want = ToDeviceRect(viewport, scale);

        if (_pixels is null || pageVersion != _pageVersion || scale != _scale || !_bounds.Contains(want))
        {
            return false;
        }

        blit = new CacheBlit(
            SourcePixels: _pixels,
            SourceStride: _bounds.Width * 4,
            SourceX: want.X - _bounds.X,
            SourceY: want.Y - _bounds.Y,
            DestX: 0,
            DestY: 0,
            Width: want.Width,
            Height: want.Height);
        return true;
    }

    /// <summary>
    /// Returns the list of device-pixel strip rectangles of <paramref name="viewport"/>
    /// that are NOT already covered by the cache for the matching key — the regions
    /// the host must paint and then pass to <see cref="SlideTo"/>. Returns the whole
    /// requested rect (and bumps <c>paint.cache.miss</c>) when the key differs;
    /// returns the uncovered edges (and bumps <c>paint.cache.partial</c>) when the
    /// key matches but the rect spills past the cached bounds; returns empty when
    /// fully covered (a HIT — caller should have used <see cref="TryServe"/>).
    /// </summary>
    public IReadOnlyList<DeviceRect> ComputeUncachedStrips(LayoutRect viewport, float scale, long pageVersion)
    {
        var want = ToDeviceRect(viewport, scale);

        if (_pixels is null || pageVersion != _pageVersion || scale != _scale || !_bounds.Intersects(want))
        {
            StarlingTelemetry.Counter("paint.cache.miss", 1);
            return new[] { want };
        }

        if (_bounds.Contains(want))
        {
            return Array.Empty<DeviceRect>();
        }

        StarlingTelemetry.Counter("paint.cache.partial", 1);
        return Subtract(want, _bounds);
    }

    /// <summary>
    /// Slides the cache window onto <paramref name="window"/> (the new viewport in
    /// device px): allocates a fresh buffer sized to exactly that window, copies the
    /// overlap with the current bounds into it (the part of the previous frame still
    /// on screen), then fills the freshly-exposed regions from <paramref name="strips"/>
    /// (each the raster of one uncovered edge band from
    /// <see cref="ComputeUncachedStrips"/>). Rows that scrolled off-screen are
    /// dropped, so the buffer never grows past one viewport. Emits each strip's area
    /// as <c>paint.cache.strip_area</c>. Returns <c>false</c> (caller must reseed via
    /// <see cref="Invalidate"/> + a full raster) only when the key is stale; the
    /// retained overlap plus the strips otherwise tile the window completely.
    /// </summary>
    public bool SlideTo(DeviceRect window, float scale, long pageVersion, IReadOnlyList<(DeviceRect Rect, RenderedBitmap Pixels)> strips)
    {
        ArgumentNullException.ThrowIfNull(strips);

        // A version/scale mismatch slipping through here means the caller is
        // sliding against a stale key — refuse so it reseeds wholesale.
        if (_pixels is null || pageVersion != _pageVersion || scale != _scale)
        {
            return false;
        }

        var dest = new byte[checked(window.Width * window.Height * 4)];

        // Retain the part of the old window still visible in the new one.
        var overlap = _bounds.Intersect(window);
        if (overlap.Width > 0 && overlap.Height > 0)
        {
            CopyRegion(_pixels, _bounds, dest, window, overlap);
        }

        // Overlay each freshly-painted strip, clamped to the window in case a
        // fractional-scale ceil sized the raster a pixel past the edge.
        foreach (var (rect, pixels) in strips)
        {
            ArgumentNullException.ThrowIfNull(pixels);
            StarlingTelemetry.Counter("paint.cache.strip_area", (double)rect.Width * rect.Height);
            var region = rect.Intersect(window);
            if (region.Width > 0 && region.Height > 0)
            {
                CopyRegion(pixels.Rgba, rect, dest, window, region);
            }
        }

        _pixels = dest;
        _bounds = window;
        return true;
    }

    /// <summary>Drops all cached content. The next render is a full MISS.</summary>
    public void Invalidate()
    {
        _pixels = null;
        _bounds = default;
        _scale = 0f;
        _pageVersion = 0;
    }

    /// <summary>Device-pixel rectangle of the cached region (for diagnostics/tests).</summary>
    public DeviceRect Bounds => _bounds;

    /// <summary>
    /// Replaces all cached content with <paramref name="pixels"/> placed at the
    /// device rect <paramref name="rect"/>. Used to seed on a MISS and to reseed
    /// on eviction; the caller owns matching <paramref name="rect"/> to the
    /// raster's real dimensions.
    /// </summary>
    public void Reset(DeviceRect rect, float scale, long pageVersion, RenderedBitmap pixels)
    {
        var expected = checked(rect.Width * rect.Height * 4);
        if (pixels.Rgba.Length != expected || pixels.Width != rect.Width || pixels.Height != rect.Height)
        {
            throw new ArgumentException(
                $"Seed/strip pixels {pixels.Width}x{pixels.Height} do not match device rect {rect.Width}x{rect.Height}.");
        }
        // Copy so the cache owns an independent buffer (callers may reuse/dispose).
        var owned = new byte[expected];
        Array.Copy(pixels.Rgba, owned, expected);
        _pixels = owned;
        _bounds = rect;
        _scale = scale;
        _pageVersion = pageVersion;
    }

    /// <summary>
    /// Copies the device-pixel sub-rectangle <paramref name="region"/> from
    /// <paramref name="src"/> (a tight buffer covering <paramref name="srcBounds"/>)
    /// into <paramref name="dest"/> (a tight buffer covering
    /// <paramref name="destBounds"/>), one row at a time respecting both strides.
    /// <paramref name="region"/> must be contained in both bounds.
    /// </summary>
    private static void CopyRegion(byte[] src, DeviceRect srcBounds, byte[] dest, DeviceRect destBounds, DeviceRect region)
    {
        var srcStride = srcBounds.Width * 4;
        var destStride = destBounds.Width * 4;
        var rowBytes = region.Width * 4;
        var srcX = (region.X - srcBounds.X) * 4;
        var destX = (region.X - destBounds.X) * 4;
        for (var row = 0; row < region.Height; row++)
        {
            var srcOffset = ((region.Y - srcBounds.Y + row) * srcStride) + srcX;
            var destOffset = ((region.Y - destBounds.Y + row) * destStride) + destX;
            Array.Copy(src, srcOffset, dest, destOffset, rowBytes);
        }
    }

    /// <summary>
    /// Page-coord rect → device-pixel rect. Origin rounds to the nearest whole
    /// device pixel; size uses <see cref="Math.Ceiling(double)"/> to match
    /// <c>ImageSharpBackend.Render</c>'s output dimensions exactly.
    /// </summary>
    public static DeviceRect ToDeviceRect(LayoutRect r, float scale)
    {
        var x = (int)Math.Round(r.X * scale);
        var y = (int)Math.Round(r.Y * scale);
        var w = (int)Math.Ceiling(r.Width * scale);
        var h = (int)Math.Ceiling(r.Height * scale);
        return new DeviceRect(x, y, w, h);
    }

    /// <summary>
    /// The parts of <paramref name="want"/> not covered by <paramref name="have"/>,
    /// as up to four axis-aligned strips (top, bottom, left, right). The split
    /// favors full-width top/bottom bands then the remaining left/right columns so
    /// adjacent scroll deltas produce one band, not a cross.
    /// </summary>
    private static List<DeviceRect> Subtract(DeviceRect want, DeviceRect have)
    {
        var strips = new List<DeviceRect>(4);

        // Top band: full width of `want` above `have`.
        if (want.Y < have.Y)
        {
            var h = Math.Min(have.Y, want.Bottom) - want.Y;
            strips.Add(new DeviceRect(want.X, want.Y, want.Width, h));
        }
        // Bottom band: full width of `want` below `have`.
        if (want.Bottom > have.Bottom)
        {
            var top = Math.Max(have.Bottom, want.Y);
            strips.Add(new DeviceRect(want.X, top, want.Width, want.Bottom - top));
        }

        // Middle vertical span shared with `have` — left/right columns there.
        var midTop = Math.Max(want.Y, have.Y);
        var midBottom = Math.Min(want.Bottom, have.Bottom);
        if (midBottom > midTop)
        {
            if (want.X < have.X)
            {
                var w = Math.Min(have.X, want.Right) - want.X;
                strips.Add(new DeviceRect(want.X, midTop, w, midBottom - midTop));
            }
            if (want.Right > have.Right)
            {
                var left = Math.Max(have.Right, want.X);
                strips.Add(new DeviceRect(left, midTop, want.Right - left, midBottom - midTop));
            }
        }

        return strips;
    }
}

/// <summary>
/// An integer device-pixel rectangle. Distinct from the page-coord
/// <see cref="Starling.Layout.Rect"/> (which is in CSS px, doubles); all
/// <see cref="PictureCache"/> pixel math is in this space.
/// </summary>
internal readonly record struct DeviceRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool Contains(DeviceRect o)
        => o.X >= X && o.Y >= Y && o.Right <= Right && o.Bottom <= Bottom;

    public bool Intersects(DeviceRect o)
        => X < o.Right && Right > o.X && Y < o.Bottom && Bottom > o.Y;

    /// <summary>
    /// The overlapping rectangle of <c>this</c> and <paramref name="o"/>. A
    /// non-overlapping pair yields a rect with zero or negative extent; callers
    /// guard on <c>Width &gt; 0 &amp;&amp; Height &gt; 0</c> before using it.
    /// </summary>
    public DeviceRect Intersect(DeviceRect o)
    {
        var x = Math.Max(X, o.X);
        var y = Math.Max(Y, o.Y);
        var right = Math.Min(Right, o.Right);
        var bottom = Math.Min(Bottom, o.Bottom);
        return new DeviceRect(x, y, right - x, bottom - y);
    }
}

/// <summary>
/// Describes a single row-by-row copy from a cached buffer into the output
/// bitmap: which sub-rect of the source to read and where in the destination it
/// lands. The destination is always the (0,0)-origin output for the requested
/// viewport.
/// </summary>
internal readonly record struct CacheBlit(
    byte[] SourcePixels,
    int SourceStride,
    int SourceX,
    int SourceY,
    int DestX,
    int DestY,
    int Width,
    int Height);
