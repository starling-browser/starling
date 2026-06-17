// SPDX-License-Identifier: Apache-2.0
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Starling.Css.Values;
using Starling.Paint.DisplayList;

namespace Starling.Paint.Backend;

/// <summary>
/// Adapter-internal raster caches and deferred-disposal bag for the ImageSharp
/// paint backend. Split out of <c>ImageSharpBackend.cs</c> (same partial class)
/// so the LRU box-shadow and conic-gradient layer caches read as one clearly
/// ImageSharp-only concern. None of these types cross the renderer-neutral seam.
/// </summary>
internal sealed partial class ImageSharpBackend
{
    private readonly record struct BoxShadowCacheKey(
        int ImageWidth,
        int ImageHeight,
        double SilhouetteWidth,
        double SilhouetteHeight,
        int Margin,
        double Blur,
        double Spread,
        CornerRadii Radii,
        byte R,
        byte G,
        byte B,
        byte A,
        bool Inset = false,
        double OffsetX = 0,
        double OffsetY = 0,
        double RasterScale = 1)
    {
        public static BoxShadowCacheKey From(DrawBoxShadow shadow, BoxShadowRasterGeometry geometry, double rasterScale = 1)
            => new(
                geometry.ImageWidth,
                geometry.ImageHeight,
                geometry.SilhouetteWidth,
                geometry.SilhouetteHeight,
                geometry.Margin,
                Math.Max(0, shadow.Blur),
                shadow.Spread,
                shadow.Radii,
                shadow.Color.R,
                shadow.Color.G,
                shadow.Color.B,
                shadow.Color.A,
                RasterScale: rasterScale);

        /// <summary>
        /// Key for an inset layer raster. Unlike outer shadows (where the
        /// offset only moves the blit destination), the offset changes the
        /// rasterized ring itself, so it joins the key. The silhouette fields
        /// carry the padding-box dimensions.
        /// </summary>
        public static BoxShadowCacheKey FromInset(DrawBoxShadow shadow, int imageWidth, int imageHeight, int margin, double rasterScale = 1)
            => new(
                imageWidth,
                imageHeight,
                shadow.Bounds.Width,
                shadow.Bounds.Height,
                margin,
                Math.Max(0, shadow.Blur),
                shadow.Spread,
                shadow.Radii,
                shadow.Color.R,
                shadow.Color.G,
                shadow.Color.B,
                shadow.Color.A,
                Inset: true,
                shadow.OffsetX,
                shadow.OffsetY,
                RasterScale: rasterScale);
    }

    private sealed class DisposableBag : IDisposable
    {
        private readonly List<IDisposable> _items = [];

        public void Add(IDisposable item)
            => _items.Add(item);

        public void Dispose()
        {
            foreach (var item in _items)
            {
                item.Dispose();
            }
        }
    }

    private sealed class BoxShadowRasterCache : IDisposable
    {
        private const long DefaultBudgetBytes = 64L * 1024 * 1024;

        private readonly object _gate = new();
        private readonly long _maxBytes = ReadBudgetEnv();
        private readonly Dictionary<BoxShadowCacheKey, LinkedListNode<Entry>> _map = new();
        private readonly LinkedList<Entry> _lru = new();
        private long _bytes;

        private static long ReadBudgetEnv()
        {
            var raw = Environment.GetEnvironmentVariable("STARLING_BOX_SHADOW_CACHE_BYTES");
            return long.TryParse(raw, out var value) && value > 0 ? value : DefaultBudgetBytes;
        }

        public bool TryGet(BoxShadowCacheKey key, out Image<Rgba32> image)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    image = node.Value.Image;
                    return true;
                }
            }

            image = null!;
            return false;
        }

        public bool Put(BoxShadowCacheKey key, Image<Rgba32> image, DisposableBag deferredDisposals)
        {
            var bytes = checked((long)image.Width * image.Height * 4);
            if (bytes > _maxBytes)
            {
                deferredDisposals.Add(image);
                return false;
            }

            lock (_gate)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    Remove(existing, deferredDisposals);
                }

                var node = _lru.AddFirst(new Entry(key, image, bytes));
                _map[key] = node;
                _bytes += bytes;
                EvictToBudget(deferredDisposals);
                return true;
            }
        }

        private void EvictToBudget(DisposableBag deferredDisposals)
        {
            while (_bytes > _maxBytes && _lru.Last is { } last)
            {
                Remove(last, deferredDisposals);
            }
        }

        private void Remove(LinkedListNode<Entry> node, DisposableBag deferredDisposals)
        {
            _lru.Remove(node);
            _map.Remove(node.Value.Key);
            _bytes -= node.Value.Bytes;
            deferredDisposals.Add(node.Value.Image);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var entry in _lru)
                {
                    entry.Image.Dispose();
                }

                _lru.Clear();
                _map.Clear();
                _bytes = 0;
            }
        }

        private sealed record Entry(BoxShadowCacheKey Key, Image<Rgba32> Image, long Bytes);
    }

    /// <summary>
    /// Cache key for a rasterized conic gradient layer. Keyed by the gradient's
    /// stop list (color + position identity), the from-angle, the at-position, and
    /// the device dimensions. The gradient record equality comes from C# record
    /// structural equality, which recursively compares stops.
    /// </summary>
    private readonly record struct ConicCacheKey(CssGradient Gradient, int Width, int Height);

    /// <summary>
    /// LRU cache for rasterized conic gradient layers. Capped at 32 MB by
    /// default (configurable via <c>STARLING_CONIC_CACHE_BYTES</c>) so repeated
    /// frames reuse previously-painted layers without re-running the per-pixel
    /// rasterizer. The 64 M-pixel per-layer guard in <see cref="FillConicGradient"/>
    /// is separate and still applies.
    /// </summary>
    private sealed class ConicLayerCache : IDisposable
    {
        private const long DefaultBudgetBytes = 32L * 1024 * 1024; // 32 MB

        private readonly object _gate = new();
        private readonly long _maxBytes = ReadBudgetEnv();
        private readonly Dictionary<ConicCacheKey, LinkedListNode<Entry>> _map = new();
        private readonly LinkedList<Entry> _lru = new();
        private long _bytes;

        private static long ReadBudgetEnv()
        {
            var raw = Environment.GetEnvironmentVariable("STARLING_CONIC_CACHE_BYTES");
            return long.TryParse(raw, out var value) && value > 0 ? value : DefaultBudgetBytes;
        }

        public bool TryGet(ConicCacheKey key, out Image<Rgba32> image)
        {
            lock (_gate)
            {
                if (_map.TryGetValue(key, out var node))
                {
                    _lru.Remove(node);
                    _lru.AddFirst(node);
                    image = node.Value.Image;
                    return true;
                }
            }
            image = null!;
            return false;
        }

        /// <summary>
        /// Add <paramref name="image"/> to the cache under <paramref name="key"/>.
        /// If the image is too large to fit within the budget, it is added to
        /// <paramref name="deferredDisposals"/> and NOT cached. Returns true when
        /// the image was accepted into the cache.
        /// </summary>
        public bool Put(ConicCacheKey key, Image<Rgba32> image, DisposableBag deferredDisposals)
        {
            var bytes = checked((long)image.Width * image.Height * 4);
            if (bytes > _maxBytes)
            {
                // Too big for the cache — caller is responsible for lifecycle.
                return false;
            }

            lock (_gate)
            {
                if (_map.TryGetValue(key, out var existing))
                {
                    Remove(existing, deferredDisposals);
                }
                var node = _lru.AddFirst(new Entry(key, image, bytes));
                _map[key] = node;
                _bytes += bytes;
                EvictToBudget(deferredDisposals);
                return true;
            }
        }

        private void EvictToBudget(DisposableBag deferredDisposals)
        {
            while (_bytes > _maxBytes && _lru.Last is { } last)
            {
                Remove(last, deferredDisposals);
            }
        }

        private void Remove(LinkedListNode<Entry> node, DisposableBag deferredDisposals)
        {
            _lru.Remove(node);
            _map.Remove(node.Value.Key);
            _bytes -= node.Value.Bytes;
            deferredDisposals.Add(node.Value.Image);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var entry in _lru)
                {
                    entry.Image.Dispose();
                }

                _lru.Clear();
                _map.Clear();
                _bytes = 0;
            }
        }

        private sealed record Entry(ConicCacheKey Key, Image<Rgba32> Image, long Bytes);
    }
}
