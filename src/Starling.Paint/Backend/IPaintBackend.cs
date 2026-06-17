using Starling.Common.Image;
using LayoutRect = Starling.Layout.Rect;
using LayoutSize = Starling.Layout.Size;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint.Backend;

/// <summary>Renderer-neutral seam consumed by <see cref="Painter"/> via <see cref="PaintBackendSelector"/>.</summary>
internal interface IPaintBackend : IDisposable
{
    string Name { get; }

    /// <summary>
    /// Render <paramref name="list"/> into a bitmap sized to
    /// <paramref name="viewport"/>.Width × .Height. <paramref name="viewport"/>.X/Y
    /// is the page-coordinate scroll offset of the visible region: paint at
    /// page-coord (viewport.X, viewport.Y) lands at device-coord (0, 0). This is
    /// the viewport-clipped path used by interactive scroll.
    /// </summary>
    RenderedBitmap Render(PaintList list, LayoutRect viewport, float scale = 1.0f);

    /// <summary>
    /// Render <paramref name="list"/> into a <paramref name="viewport"/>-sized
    /// bitmap, optionally over a transparent canvas
    /// (<paramref name="opaqueBackground"/> = false). The compositor uses the
    /// transparent path to rasterize a layer's slice so unpainted regions stay
    /// see-through for alpha-over compositing. Defaults to the opaque-white
    /// behavior of the primary overload.
    /// </summary>
    RenderedBitmap Render(PaintList list, LayoutRect viewport, float scale, bool opaqueBackground)
        => Render(list, viewport, scale);

    /// <summary>
    /// "Render everything" convenience: a <see cref="LayoutSize"/> with no
    /// offset (X=Y=0). Used by headless full-page screenshots and tests that
    /// pass an explicit surface size. Delegates to the rect overload.
    /// </summary>
    RenderedBitmap Render(PaintList list, LayoutSize viewport, float scale = 1.0f)
        => Render(list, new LayoutRect(0, 0, viewport.Width, viewport.Height), scale);

    /// <summary>
    /// Render <paramref name="list"/> over a transparent canvas like the rect
    /// overload, then run the resolved CSS <paramref name="filters"/> chain over
    /// the result (Filter Effects 1 §10.1, in order). Used by the compositor to
    /// filter a promoted layer ONCE at layer granularity instead of re-running
    /// the chain inside every tile raster. The default replays through the
    /// inline PushFilter bracket every backend already understands, so a
    /// delegating wrapper that doesn't forward this member stays correct.
    /// </summary>
    RenderedBitmap RenderFiltered(PaintList list, LayoutRect viewport, float scale,
        IReadOnlyList<DisplayList.FilterFunction> filters)
    {
        var wrapped = new PaintList();
        wrapped.Add(new DisplayList.PushFilter(viewport, filters));
        var items = list.Items;
        for (var i = 0; i < items.Count; i++)
        {
            wrapped.Add(items[i]);
        }

        wrapped.Add(DisplayList.PopFilter.Instance);
        return Render(wrapped, viewport, scale, opaqueBackground: false);
    }

    /// <summary>
    /// Rasterize to a plain bitmap, preferring the cheapest path for SMALL
    /// surfaces. A GPU-canvas backend overrides this to use its CPU
    /// rasterizer: the WebGPU canvas flush pays a synchronous scheduling
    /// readback per texture (a full GPU round-trip) regardless of size, so a
    /// tiny animating layer slice is far cheaper to raster on the CPU and
    /// upload. The default just renders normally.
    /// </summary>
    RenderedBitmap RenderSmallBitmap(PaintList list, LayoutRect viewport, float scale, bool opaqueBackground)
        => Render(list, viewport, scale, opaqueBackground);

    /// <summary>
    /// Runs a resolved CSS filter chain over already-rendered pixels (Filter
    /// Effects 1 §10.1, in order). Used by the compositor's CPU blend path to
    /// filter a backdrop snapshot. The default is a no-op pass-through so a
    /// backend without a pixel filter chain stays correct-but-unfiltered.
    /// </summary>
    RenderedBitmap FilterBitmap(RenderedBitmap source, IReadOnlyList<DisplayList.FilterFunction> filters, float scale)
        => source;
}
