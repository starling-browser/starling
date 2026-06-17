using System.Runtime.InteropServices;
using Starling.Common.Image;

namespace Starling.Codecs.Mac;

/// <summary>
/// macOS / Mac Catalyst image decoder built on the ImageIO + Core Graphics
/// frameworks. Decodes any format ImageIO understands (PNG, JPEG, WebP, GIF,
/// HEIC, …) by handing the bytes to a <c>CGImageSource</c>, then drawing the
/// first frame into a freshly-allocated <c>CGBitmapContext</c> with an explicit
/// RGBA8888 layout so the output matches the <see cref="DecodedImage"/>
/// contract exactly — straight (non-premultiplied) alpha, top-down rows,
/// stride == width*4.
/// </summary>
/// <remarks>
/// All Core Foundation objects created here (<c>CFData</c>, <c>CGImageSource</c>,
/// <c>CGImage</c>, <c>CGColorSpace</c>, <c>CGContext</c>) are released in a
/// <c>finally</c> so a decode failure cannot leak. This lives inside the
/// designated image-decode interop project. See AGENTS.md.
/// </remarks>
internal sealed partial class ImageIODecoder : IImageDecoder
{
    public unsafe DecodedImage Decode(ReadOnlySpan<byte> bytes)
    {
        nint data = 0;
        nint source = 0;
        nint cgImage = 0;
        nint colorSpace = 0;
        nint context = 0;
        try
        {
            fixed (byte* p = bytes)
            {
                data = CFDataCreate(0, (nint)p, bytes.Length);
            }
            if (data == 0)
            {
                throw new ImageDecodeException("ImageIO: CFDataCreate returned null.");
            }

            source = CGImageSourceCreateWithData(data, 0);
            if (source == 0)
            {
                throw new ImageDecodeException("ImageIO: CGImageSourceCreateWithData failed (not a recognised image).");
            }

            cgImage = CGImageSourceCreateImageAtIndex(source, 0, 0);
            if (cgImage == 0)
            {
                throw new ImageDecodeException("ImageIO: CGImageSourceCreateImageAtIndex failed (corrupt or unsupported image).");
            }

            nint width = CGImageGetWidth(cgImage);
            nint height = CGImageGetHeight(cgImage);
            var (w, h, _) = NativeImageDecoder.ValidateDecodedDimensions(width, height);

            // Clamp the decode resolution: very large photos draw into a
            // proportionally smaller context and CoreGraphics performs the
            // high-quality downscale during CGContextDrawImage, so the
            // full-resolution RGBA never touches the managed heap. The
            // DecodedImage still reports the true intrinsic dimensions.
            var (cw, ch) = NativeImageDecoder.ClampDecodeTarget(w, h);

            colorSpace = CGColorSpaceCreateDeviceRGB();
            if (colorSpace == 0)
            {
                throw new ImageDecodeException("ImageIO: CGColorSpaceCreateDeviceRGB failed.");
            }

            nint stride = (nint)cw * 4;

            // CoreGraphics bitmap contexts only support *premultiplied* (or
            // none/skip) alpha for 8-bit RGBA — straight alpha
            // (kCGImageAlphaLast) is not a valid context format. So draw into a
            // premultiplied-RGBA context pointed straight at the destination
            // buffer, then un-premultiply in place to satisfy the
            // straight-alpha DecodedImage contract.
            return DecodedImage.CreatePooled(cw, ch, w, h, span =>
            {
                // ArrayPool.Rent returns an uninitialised buffer. CGContextDrawImage
                // defaults to sourceOver, so any non-zero alpha left in the rented
                // memory would composite into semi-transparent output pixels
                // (opaque source pixels coincidentally land right because the dst
                // contribution drops out). Zero first so the draw is effectively a
                // clean blit.
                span.Clear();

                fixed (byte* dst = span)
                {
                    nint ctx = CGBitmapContextCreate(
                        (nint)dst, cw, ch,
                        bitsPerComponent: 8,
                        bytesPerRow: stride,
                        space: colorSpace,
                        bitmapInfo: kCGImageAlphaPremultipliedLast | kCGBitmapByteOrder32Big);
                    if (ctx == 0)
                    {
                        throw new ImageDecodeException("ImageIO: CGBitmapContextCreate failed.");
                    }

                    context = ctx;

                    // When the decode is resolution-clamped the draw below is a
                    // downscale; ask CG for its best resampling filter.
                    if (cw != w || ch != h)
                    {
                        CGContextSetInterpolationQuality(ctx, kCGInterpolationHigh);
                    }

                    // A CGBitmapContext stores row 0 of its backing buffer at
                    // the *top* of the image, and CGContextDrawImage fills the
                    // given rect in that same orientation — so drawing the
                    // image at the origin already lands top-down in memory. No
                    // CTM flip is needed (a flip would invert it).
                    var rect = new CGRect { X = 0, Y = 0, Width = cw, Height = ch };
                    CGContextDrawImage(ctx, rect, cgImage);
                    CGContextFlush(ctx);

                    UnpremultiplyInPlace(span);
                }
            });
        }
        catch (ImageDecodeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ImageDecodeException("ImageIO: native image decode failed.", ex);
        }
        finally
        {
            if (context != 0)
            {
                CGContextRelease(context);
            }

            if (colorSpace != 0)
            {
                CGColorSpaceRelease(colorSpace);
            }

            if (cgImage != 0)
            {
                CGImageRelease(cgImage);
            }

            if (source != 0)
            {
                CFRelease(source);
            }

            if (data != 0)
            {
                CFRelease(data);
            }
        }
    }

    // CGInterpolationQuality.kCGInterpolationHigh == 3 — CG's best resampler
    // (Lanczos-class) for the clamped-decode downscale draw.
    private const int kCGInterpolationHigh = 3;
    // CGImageAlphaInfo.kCGImageAlphaPremultipliedLast == 1 (premultiplied,
    // alpha in the last byte).
    private const uint kCGImageAlphaPremultipliedLast = 1;
    // CGBitmapInfo.kCGBitmapByteOrder32Big == 4 << 12 — pins the in-memory
    // channel order to R,G,B,A regardless of host endianness.
    private const uint kCGBitmapByteOrder32Big = 4u << 12;

    /// <summary>
    /// Convert the premultiplied-alpha RGBA8888 buffer a CoreGraphics bitmap
    /// context produced into the straight (non-premultiplied) alpha the
    /// <see cref="DecodedImage"/> contract requires. With
    /// <c>kCGBitmapByteOrder32Big</c> the channel order is already R,G,B,A;
    /// only the premultiplication needs undoing — <c>c = c' * 255 / a</c>,
    /// clamped. Fully-opaque and fully-transparent pixels need no division.
    /// </summary>
    private static void UnpremultiplyInPlace(Span<byte> rgba)
    {
        for (int i = 0; i + 3 < rgba.Length; i += 4)
        {
            byte a = rgba[i + 3];
            if (a == 0 || a == 255)
            {
                continue;
            }

            rgba[i] = (byte)Math.Min(255, rgba[i] * 255 / a);
            rgba[i + 1] = (byte)Math.Min(255, rgba[i + 1] * 255 / a);
            rgba[i + 2] = (byte)Math.Min(255, rgba[i + 2] * 255 / a);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;
    }

    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CoreGraphics =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string ImageIO =
        "/System/Library/Frameworks/ImageIO.framework/ImageIO";

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDataCreate(nint allocator, nint bytes, nint length);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint cf);

    [LibraryImport(ImageIO)]
    private static partial nint CGImageSourceCreateWithData(nint data, nint options);

    [LibraryImport(ImageIO)]
    private static partial nint CGImageSourceCreateImageAtIndex(nint source, nint index, nint options);

    [LibraryImport(CoreGraphics)]
    private static partial nint CGImageGetWidth(nint image);

    [LibraryImport(CoreGraphics)]
    private static partial nint CGImageGetHeight(nint image);

    [LibraryImport(CoreGraphics)]
    private static partial void CGImageRelease(nint image);

    [LibraryImport(CoreGraphics)]
    private static partial nint CGColorSpaceCreateDeviceRGB();

    [LibraryImport(CoreGraphics)]
    private static partial void CGColorSpaceRelease(nint space);

    [LibraryImport(CoreGraphics)]
    private static partial nint CGBitmapContextCreate(
        nint data, nint width, nint height, nint bitsPerComponent,
        nint bytesPerRow, nint space, uint bitmapInfo);

    [LibraryImport(CoreGraphics)]
    private static partial void CGContextRelease(nint context);

    [LibraryImport(CoreGraphics)]
    private static partial void CGContextSetInterpolationQuality(nint context, int quality);

    [LibraryImport(CoreGraphics)]
    private static partial void CGContextDrawImage(nint context, CGRect rect, nint image);

    [LibraryImport(CoreGraphics)]
    private static partial void CGContextFlush(nint context);
}
