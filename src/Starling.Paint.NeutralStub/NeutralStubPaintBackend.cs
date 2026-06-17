// SPDX-License-Identifier: Apache-2.0
using Starling.Common.Image;
using Starling.Css.Values;
using Starling.Layout;
using Starling.Paint.Backend;
using Starling.Paint.DisplayList;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint.NeutralStub;

/// <summary>
/// A minimal paint backend written entirely against the renderer-neutral seam —
/// no SixLabors type appears here. It walks the neutral <see cref="DisplayList"/>
/// and fills solid rectangles into a raw RGBA bitmap, proving the display-list
/// contract is consumable without any drawing library. The GPU path returns a
/// neutral <see cref="GpuPaintTexture"/> (null handles — proof only, not a real
/// compositor texture).
/// </summary>
internal sealed class NeutralStubPaintBackend : IGpuTexturePaintBackend
{
    public string Name => "neutral-stub";

    public RenderedBitmap Render(PaintList list, Rect viewport, float scale = 1f)
    {
        ArgumentNullException.ThrowIfNull(list);
        int w = Math.Max(1, (int)Math.Ceiling(viewport.Width * scale));
        int h = Math.Max(1, (int)Math.Ceiling(viewport.Height * scale));
        var rgba = new byte[w * h * 4];

        foreach (var item in list.Items)
        {
            if (item is FillRect fill)
            {
                FillSolid(rgba, w, h, fill.Bounds, viewport, scale, fill.Color);
            }
        }

        return new RenderedBitmap(w, h, rgba);
    }

    public GpuPaintTexture RenderTexture(
        PaintList list, Rect viewport, float scale, bool opaqueBackground, GpuPaintDevice device)
    {
        int w = Math.Max(1, (int)Math.Ceiling(viewport.Width * scale));
        int h = Math.Max(1, (int)Math.Ceiling(viewport.Height * scale));
        // A real GPU backend would upload to device.Device / device.Queue; the
        // stub returns a neutral texture with null handles to demonstrate the
        // GpuPaintDevice -> GpuPaintTexture contract carries no backend type.
        return new GpuPaintTexture(0, 0, w, h, PaintTextureFormat.Rgba8Unorm, NoopDisposable.Instance);
    }

    private static void FillSolid(byte[] rgba, int w, int h, Rect bounds, Rect viewport, float scale, CssColor color)
    {
        var s = color.ToSrgb();
        int x0 = Math.Clamp((int)((bounds.X - viewport.X) * scale), 0, w);
        int y0 = Math.Clamp((int)((bounds.Y - viewport.Y) * scale), 0, h);
        int x1 = Math.Clamp((int)Math.Ceiling((bounds.X - viewport.X + bounds.Width) * scale), 0, w);
        int y1 = Math.Clamp((int)Math.Ceiling((bounds.Y - viewport.Y + bounds.Height) * scale), 0, h);
        for (int y = y0; y < y1; y++)
        {
            for (int x = x0; x < x1; x++)
            {
                int i = (y * w + x) * 4;
                rgba[i] = s.R; rgba[i + 1] = s.G; rgba[i + 2] = s.B; rgba[i + 3] = s.A;
            }
        }
    }

    public void Dispose() { }
}
