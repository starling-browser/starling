using Starling.Codecs.Linux;
using Starling.Codecs.Mac;
using Starling.Codecs.Windows;
using Starling.Common.Image;

namespace Starling.Codecs;

public static class NativeImageDecoder
{
    // Hard cap for decoded RGBA8888 surfaces to prevent memory-exhaustion
    // attacks from oversized/untrusted image dimensions.
    internal const int MaxDecodedImageBytes = 256 * 1024 * 1024; // 256 MiB

    // Decode-resolution cap: pages routinely ship multi-megapixel photos that
    // display at a few hundred CSS px, and the engine keeps every decoded
    // bitmap alive for the lifetime of the page (the ImageFetcher cache feeds
    // tile re-rasterization). Decoding full-resolution RGBA retains ~4 bytes
    // per source pixel — github.com alone held 215 MB across 24 images.
    // Clamp the longest side; the buffer is a high-quality downscale and the
    // DecodedImage carries the true intrinsic dimensions for layout. 2048 px
    // covers a 1280-CSS-px viewport edge to edge at 1.6x DPR; a full-bleed
    // image on a 2x display upscales 1.25x from the clamped bitmap, which is
    // visually negligible for photographic content (the same trade real
    // browsers make when they sub-sample huge images).
    internal const int MaxDecodeDimension = 2048;

    /// <summary>
    /// Decode a complete encoded image (PNG/JPEG/WebP) into a
    /// <see cref="DecodedImage"/>. The caller owns the result and must dispose
    /// it once the pixels have been consumed.
    /// </summary>
    /// <exception cref="ImageDecodeException">
    /// The bytes are empty, an unrecognised format, corrupt/truncated, or the
    /// host OS has no supported codec backend, or decoded dimensions exceed
    /// the safety cap.
    /// </exception>
    public static DecodedImage Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new ImageDecodeException("Cannot decode an empty image buffer.");
        }

        var format = ImageFormatSniffer.Detect(bytes);
        if (format == ImageFormat.Svg)
        {
            throw new ImageDecodeException(
                "SVG is a vector format and cannot be decoded by the OS-native raster " +
                "codecs. Route it to the managed SVG rasterizer (Starling.Paint); the " +
                "engine does this automatically via ImageFormatSniffer.LooksLikeSvg.");
        }

        if (format == ImageFormat.Unknown)
        {
            throw new ImageDecodeException(
                "Unrecognised image format: no PNG/JPEG/WebP/GIF/BMP signature in the leading bytes.");
        }

        IImageDecoder backend = SelectBackend();
        // The macOS backend clamps natively (it decodes straight into a
        // clamped CGBitmapContext); for backends that decode full resolution
        // this downscales the over-cap result before anything retains it.
        return ClampToDecodeCap(backend.Decode(bytes));
    }

    /// <summary>
    /// Target pixel-buffer dimensions for a decode: unchanged when the longest
    /// side is within <see cref="MaxDecodeDimension"/>, otherwise scaled down
    /// proportionally so the longest side equals the cap.
    /// </summary>
    internal static (int Width, int Height) ClampDecodeTarget(int width, int height)
    {
        var longest = Math.Max(width, height);
        if (longest <= MaxDecodeDimension)
        {
            return (width, height);
        }

        var scale = (double)MaxDecodeDimension / longest;
        return (
            Math.Max(1, (int)Math.Round(width * scale)),
            Math.Max(1, (int)Math.Round(height * scale)));
    }

    /// <summary>
    /// Portable fallback for backends that decode at full resolution: when the
    /// decoded bitmap exceeds <see cref="MaxDecodeDimension"/>, box-filter it
    /// down to the clamped size (preserving the intrinsic dimensions for
    /// layout) and dispose the full-resolution buffer. The full-size buffer is
    /// only a transient — it returns to the pool here instead of being
    /// retained for the lifetime of the page.
    /// </summary>
    internal static DecodedImage ClampToDecodeCap(DecodedImage decoded)
    {
        var (targetW, targetH) = ClampDecodeTarget(decoded.Width, decoded.Height);
        if (targetW == decoded.Width && targetH == decoded.Height)
        {
            return decoded;
        }

        var src = decoded;
        try
        {
            return DecodedImage.CreatePooled(
                targetW, targetH, src.IntrinsicWidth, src.IntrinsicHeight,
                dst => BoxDownscale(src.Pixels.Span, src.Width, src.Height, dst, targetW, targetH));
        }
        finally
        {
            src.Dispose();
        }
    }

    /// <summary>
    /// Area-averaging (box-filter) downscale for straight-alpha RGBA8888.
    /// Color channels are accumulated alpha-weighted (premultiplied) so fully
    /// transparent texels do not bleed their RGB into the average, then
    /// converted back to straight alpha. Box filtering is the right quality
    /// trade for large shrink ratios (every source texel contributes once).
    /// </summary>
    private static void BoxDownscale(ReadOnlySpan<byte> src, int srcW, int srcH, Span<byte> dst, int dstW, int dstH)
    {
        var xRatio = (double)srcW / dstW;
        var yRatio = (double)srcH / dstH;
        for (int dy = 0; dy < dstH; dy++)
        {
            int y0 = (int)(dy * yRatio);
            int y1 = Math.Min(srcH, Math.Max(y0 + 1, (int)Math.Ceiling((dy + 1) * yRatio)));
            for (int dx = 0; dx < dstW; dx++)
            {
                int x0 = (int)(dx * xRatio);
                int x1 = Math.Min(srcW, Math.Max(x0 + 1, (int)Math.Ceiling((dx + 1) * xRatio)));

                long rSum = 0, gSum = 0, bSum = 0, aSum = 0;
                int count = (y1 - y0) * (x1 - x0);
                for (int sy = y0; sy < y1; sy++)
                {
                    int i = (sy * srcW + x0) * 4;
                    for (int sx = x0; sx < x1; sx++, i += 4)
                    {
                        int a = src[i + 3];
                        rSum += src[i] * a;
                        gSum += src[i + 1] * a;
                        bSum += src[i + 2] * a;
                        aSum += a;
                    }
                }

                int o = (dy * dstW + dx) * 4;
                if (aSum == 0)
                {
                    dst[o] = dst[o + 1] = dst[o + 2] = dst[o + 3] = 0;
                }
                else
                {
                    dst[o] = (byte)((rSum + aSum / 2) / aSum);
                    dst[o + 1] = (byte)((gSum + aSum / 2) / aSum);
                    dst[o + 2] = (byte)((bSum + aSum / 2) / aSum);
                    dst[o + 3] = (byte)((aSum + count / 2) / count);
                }
            }
        }
    }

    /// <summary>
    /// Validates decoded bitmap dimensions and enforces a global safety cap on
    /// RGBA8888 output size.
    /// </summary>
    internal static (int Width, int Height, int ByteLength) ValidateDecodedDimensions(long width, long height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ImageDecodeException($"Decoded image has invalid dimensions {width}x{height}.");
        }

        if (width > int.MaxValue || height > int.MaxValue)
        {
            throw new ImageDecodeException($"Decoded image dimensions {width}x{height} exceed supported integer range.");
        }

        long bytes;
        try
        {
            bytes = checked(width * height * 4L);
        }
        catch (OverflowException)
        {
            throw new ImageDecodeException($"Decoded image dimensions {width}x{height} overflow RGBA byte size.");
        }

        if (bytes > MaxDecodedImageBytes)
        {
            throw new ImageDecodeException(
                $"Decoded image dimensions {width}x{height} require {bytes} bytes, " +
                $"exceeding the safety cap of {MaxDecodedImageBytes} bytes.");
        }

        return ((int)width, (int)height, (int)bytes);
    }

    /// <summary>
    /// True when <paramref name="bytes"/> sniff as an SVG document. The engine
    /// uses this to route SVG to the managed vector rasterizer
    /// (<c>Starling.Paint</c>) instead of this OS-native raster path, which can
    /// only decode bitmap containers. Exposed publicly because the
    /// <see cref="ImageFormat"/> enum and its sniffer are internal to this
    /// interop seam.
    /// </summary>
    public static bool IsSvg(ReadOnlySpan<byte> bytes) => ImageFormatSniffer.LooksLikeSvg(bytes);

    /// <summary>
    /// Pick the decoder backend for the current OS. Split out so tests can
    /// assert the dispatch decision.
    /// </summary>
    internal static IImageDecoder SelectBackend()
    {
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst())
        {
            return new ImageIODecoder();
        }

        if (OperatingSystem.IsWindows())
        {
            return new WicDecoder();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxImageDecoder();
        }

        throw new ImageDecodeException(
            $"No native image decoder backend for this platform ({Environment.OSVersion.Platform}).");
    }
}
