namespace Starling.Common.Image;

/// <summary>
/// A backend-neutral raster result: the final pixels a paint backend produced
/// for a viewport. Carries dimensions plus a tightly-packed, top-down, straight
/// (non-premultiplied) RGBA8888 buffer — four bytes per pixel, row stride
/// exactly <c>Width * 4</c>, no padding.
/// </summary>
/// <remarks>
/// This is the paint/output contract seam: it lets callers (the engine, the
/// headless CLI, golden tests) consume a render without naming the backend's
/// concrete bitmap type. PNG encoding happens via ImageSharp from
/// <see cref="Rgba"/>.
/// <para>
/// Implements <see cref="IDisposable"/> as a no-op so callers can use a
/// uniform <c>using</c> shape; the buffer is a plain managed array and needs
/// no explicit release.
/// </para>
/// </remarks>
public sealed class RenderedBitmap : IDisposable
{
    /// <summary>Creates a bitmap that takes ownership of <paramref name="rgba"/>.</summary>
    public RenderedBitmap(int width, int height, byte[] rgba)
    {
        ArgumentNullException.ThrowIfNull(rgba);
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        var expected = checked(width * height * 4);
        if (rgba.Length != expected)
        {
            throw new ArgumentException(
                $"RGBA8888 buffer length {rgba.Length} != width*height*4 ({expected}).",
                nameof(rgba));
        }

        Width = width;
        Height = height;
        Rgba = rgba;
    }

    /// <summary>Bitmap width in pixels.</summary>
    public int Width { get; }

    /// <summary>Bitmap height in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// Tightly-packed, top-down, straight-alpha RGBA8888 pixels:
    /// <c>Width * Height * 4</c> bytes, row stride <c>Width * 4</c>.
    /// </summary>
    public byte[] Rgba { get; }

    /// <summary>Returns the straight-alpha RGBA components of pixel (<paramref name="x"/>, <paramref name="y"/>).</summary>
    public (byte R, byte G, byte B, byte A) GetPixel(int x, int y)
    {
        if ((uint)x >= (uint)Width)
        {
            throw new ArgumentOutOfRangeException(nameof(x));
        }

        if ((uint)y >= (uint)Height)
        {
            throw new ArgumentOutOfRangeException(nameof(y));
        }

        var i = ((y * Width) + x) * 4;
        return (Rgba[i], Rgba[i + 1], Rgba[i + 2], Rgba[i + 3]);
    }

    /// <summary>No-op: the backing buffer is a plain managed array.</summary>
    public void Dispose()
    {
    }
}
