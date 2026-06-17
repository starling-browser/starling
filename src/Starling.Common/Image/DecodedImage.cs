using System.Buffers;

namespace Starling.Common.Image;

/// <summary>
/// A backend-neutral decoded raster image. Carries intrinsic dimensions plus a
/// tightly-packed, top-down, straight (non-premultiplied) RGBA8888 pixel
/// buffer — four bytes per pixel, row stride exactly <c>Width * 4</c>, no
/// padding.
/// </summary>
/// <remarks>
/// This type is the seam between decode and paint. It lets the engine pass
/// pixels from the decoder to the paint backend without either side naming the
/// other's concrete bitmap type.
/// <para>
/// The backing buffer may be rented from <see cref="ArrayPool{T}"/>. Callers
/// must <see cref="Dispose"/> the image once they finish reading
/// <see cref="Pixels"/> so the buffer can go back to the pool. After disposal
/// the <see cref="Pixels"/> span must not be read.
/// </para>
/// </remarks>
public sealed class DecodedImage : IDisposable
{
    private byte[]? _buffer;
    private readonly bool _pooled;
    private readonly int _length;

    private DecodedImage(int width, int height, int intrinsicWidth, int intrinsicHeight, byte[] buffer, int length, bool pooled)
    {
        Width = width;
        Height = height;
        IntrinsicWidth = intrinsicWidth;
        IntrinsicHeight = intrinsicHeight;
        _buffer = buffer;
        _length = length;
        _pooled = pooled;
    }

    /// <summary>Pixel-buffer width in pixels (the decoded bitmap's width).</summary>
    public int Width { get; }

    /// <summary>Pixel-buffer height in pixels (the decoded bitmap's height).</summary>
    public int Height { get; }

    /// <summary>
    /// The source image's true intrinsic width in pixels. Equal to
    /// <see cref="Width"/> unless the decoder clamped the decode resolution of
    /// a very large image. When clamped, the pixel buffer is a high-quality
    /// downscale and this carries the original width. Layout and
    /// background-size math must use the intrinsic dimensions. Pixel access
    /// (strides, source rects) must use <see cref="Width"/>/<see cref="Height"/>.
    /// </summary>
    public int IntrinsicWidth { get; }

    /// <summary>
    /// The source image's true intrinsic height in pixels. See
    /// <see cref="IntrinsicWidth"/>.
    /// </summary>
    public int IntrinsicHeight { get; }

    /// <summary>
    /// Tightly-packed, top-down, straight-alpha RGBA8888 pixels:
    /// <c>Width * Height * 4</c> bytes, row stride <c>Width * 4</c>.
    /// </summary>
    public ReadOnlyMemory<byte> Pixels
    {
        get
        {
            var buffer = _buffer ?? throw new ObjectDisposedException(nameof(DecodedImage));
            return new ReadOnlyMemory<byte>(buffer, 0, _length);
        }
    }

    /// <summary>
    /// Allocates a <see cref="DecodedImage"/> with a pooled backing buffer and
    /// hands the writable span to <paramref name="fill"/> so the decoder can
    /// copy pixels straight in. The span passed to <paramref name="fill"/> is
    /// exactly <c>width * height * 4</c> bytes long.
    /// </summary>
    public static DecodedImage CreatePooled(int width, int height, Action<Span<byte>> fill)
        => CreatePooled(width, height, width, height, fill);

    /// <summary>
    /// Like <see cref="CreatePooled(int,int,Action{Span{byte}})"/> but for a
    /// resolution-clamped decode: the pixel buffer is
    /// <paramref name="width"/>×<paramref name="height"/> while
    /// <paramref name="intrinsicWidth"/>/<paramref name="intrinsicHeight"/>
    /// carry the source image's true dimensions for layout.
    /// </summary>
    public static DecodedImage CreatePooled(int width, int height, int intrinsicWidth, int intrinsicHeight, Action<Span<byte>> fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        if (intrinsicWidth <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intrinsicWidth));
        }

        if (intrinsicHeight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(intrinsicHeight));
        }

        var length = CheckedLength(width, height);
        var buffer = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            fill(buffer.AsSpan(0, length));
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
        return new DecodedImage(width, height, intrinsicWidth, intrinsicHeight, buffer, length, pooled: true);
    }

    /// <summary>
    /// Wraps an already-packed RGBA8888 buffer without copying. The buffer must
    /// be at least <c>width * height * 4</c> bytes. Ownership transfers to the
    /// returned image, but the buffer counts as non-pooled and is not returned
    /// on <see cref="Dispose"/>.
    /// </summary>
    public static DecodedImage FromBuffer(int width, int height, byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var length = CheckedLength(width, height);
        if (buffer.Length < length)
        {
            throw new ArgumentException(
                $"Buffer is {buffer.Length} bytes; need at least {length} for {width}x{height} RGBA8888.",
                nameof(buffer));
        }

        return new DecodedImage(width, height, width, height, buffer, length, pooled: false);
    }

    private static int CheckedLength(int width, int height)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        var length = checked(width * height * 4);
        return length;
    }

    /// <summary>Returns the pooled backing buffer to the pool, if there is one.
    /// Safe to call more than once.</summary>
    public void Dispose()
    {
        var buffer = _buffer;
        _buffer = null;
        if (buffer is not null && _pooled)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
