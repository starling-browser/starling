using Starling.Common.Image;

namespace Starling.Paint.Tests;

/// <summary>
/// Pixel-reader helpers for the backend-neutral <see cref="RenderedBitmap"/>.
/// The golden tests used to index an ImageSharp <c>Image&lt;Rgba32&gt;</c>;
/// since wp:M3-06i <c>Painter.RenderDocument</c> returns a
/// <see cref="RenderedBitmap"/>, so the readers walk its raw RGBA buffer.
/// </summary>
internal static class BitmapPixels
{
    /// <summary>Counts pixels that are not (near-)white.</summary>
    public static int CountNonWhite(RenderedBitmap image)
        => Count(image, (r, g, b, _) => r < 250 || g < 250 || b < 250);

    /// <summary>Counts pixels exactly matching the given straight-RGBA color.</summary>
    public static int CountExact(RenderedBitmap image, byte r, byte g, byte b, byte a = 255)
        => Count(image, (pr, pg, pb, pa) => pr == r && pg == g && pb == b && pa == a);

    /// <summary>Counts pixels where blue dominates by a wide margin (JPEG-bleed tolerant).</summary>
    public static int CountBluish(RenderedBitmap image)
        => Count(image, (r, g, b, _) => b > 150 && b > r + 50 && b > g + 50);

    /// <summary>True iff both bitmaps have the same size and identical RGBA bytes.</summary>
    public static bool PixelsEqual(RenderedBitmap a, RenderedBitmap b)
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return false;
        }

        return a.Rgba.SequenceEqual(b.Rgba);
    }

    public static int Count(RenderedBitmap image, Func<byte, byte, byte, byte, bool> predicate)
    {
        var count = 0;
        var rgba = image.Rgba;
        for (var i = 0; i < rgba.Length; i += 4)
        {
            if (predicate(rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]))
            {
                count++;
            }
        }
        return count;
    }
}
