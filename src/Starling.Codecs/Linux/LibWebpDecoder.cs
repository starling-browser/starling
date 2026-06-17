using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Starling.Common.Image;

namespace Starling.Codecs.Linux;

/// <summary>
/// WebP decode via <c>libwebp.so.7</c>. libwebp's simple decode API
/// (<c>WebPGetInfo</c> + <c>WebPDecodeRGBAInto</c>) produces exactly the
/// straight-alpha, top-down, tightly-packed RGBA8888 layout the
/// <see cref="DecodedImage"/> contract wants, so the buffer goes through
/// untouched.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class LibWebpDecoder
{
    private const string LibWebp = "libwebp.so.7";

    public static unsafe DecodedImage Decode(ReadOnlySpan<byte> bytes)
    {
        int width, height;
        fixed (byte* src = bytes)
        {
            if (WebPGetInfo((nint)src, (nuint)bytes.Length, out width, out height) == 0)
            {
                throw new ImageDecodeException("libwebp: WebPGetInfo rejected the data (not a valid WebP).");
            }

            var (w, h, byteLength) = NativeImageDecoder.ValidateDecodedDimensions(width, height);
            nint stride = (nint)w * 4;

            byte[] srcCopy = bytes.ToArray();
            return DecodedImage.CreatePooled(w, h, span =>
            {
                fixed (byte* s = srcCopy)
                fixed (byte* dst = span)
                {
                    nint result = WebPDecodeRGBAInto(
                        (nint)s, (nuint)srcCopy.Length, (nint)dst, (nuint)byteLength, (int)stride);
                    if (result == 0)
                    {
                        throw new ImageDecodeException("libwebp: WebPDecodeRGBAInto failed (corrupt WebP).");
                    }
                }
            });
        }
    }

    [LibraryImport(LibWebp)]
    private static partial int WebPGetInfo(nint data, nuint dataSize, out int width, out int height);

    /// <summary>
    /// Decodes into a caller-supplied buffer; returns <paramref name="output"/>
    /// on success or 0 on failure.
    /// </summary>
    [LibraryImport(LibWebp)]
    private static partial nint WebPDecodeRGBAInto(
        nint data, nuint dataSize, nint output, nuint outputSize, int outputStride);
}
