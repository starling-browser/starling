using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Starling.Common.Image;

namespace Starling.Codecs.Linux;

/// <summary>
/// JPEG decode via libjpeg-turbo's TurboJPEG API (<c>libturbojpeg.so.0</c>).
/// The classic libjpeg API (<c>libjpeg.so.62</c>) is a setjmp/longjmp,
/// struct-layout-versioned C API that is hostile to P/Invoke; TurboJPEG is the
/// stable, flat, handle-based API libjpeg-turbo ships specifically for
/// bindings. It decodes straight to an interleaved RGBA8888 buffer
/// (<c>TJPF_RGBA</c>), top-down, which is exactly the
/// <see cref="DecodedImage"/> contract.
/// </summary>
/// <remarks>
/// libjpeg-turbo is the default JPEG library on every mainstream Linux distro,
/// and <c>libturbojpeg.so.0</c> ships in the same package
/// (<c>libturbojpeg0</c> / <c>libjpeg-turbo</c>). If only the classic
/// <c>libjpeg.so.62</c> is present this throws a clear
/// <see cref="ImageDecodeException"/> rather than attempting the fragile
/// classic-API bind.
/// </remarks>
[SupportedOSPlatform("linux")]
internal static partial class LibJpegDecoder
{
    private const string LibTurboJpeg = "libturbojpeg.so.0";

    // TurboJPEG pixel format: TJPF_RGBA == 7 (turbojpeg.h).
    private const int TJPF_RGBA = 7;
    // tjDecompress2 flags: TJFLAG_BOTTOMUP is *off* → top-down rows. 0 is fine.
    private const int TJFLAG_NONE = 0;

    public static unsafe DecodedImage Decode(ReadOnlySpan<byte> bytes)
    {
        nint handle = tjInitDecompress();
        if (handle == 0)
        {
            throw new ImageDecodeException("libturbojpeg: tjInitDecompress returned null.");
        }

        byte[] srcCopy = bytes.ToArray();
        try
        {
            int width, height, jpegSubsamp, jpegColorspace;
            fixed (byte* src = srcCopy)
            {
                int hr = tjDecompressHeader3(
                    handle, (nint)src, (nuint)srcCopy.Length,
                    out width, out height, out jpegSubsamp, out jpegColorspace);
                if (hr != 0)
                {
                    throw new ImageDecodeException($"libturbojpeg: tjDecompressHeader3 failed ({ReadError(handle)}).");
                }
            }
            var (w, h, _) = NativeImageDecoder.ValidateDecodedDimensions(width, height);
            int stride = w * 4;

            return DecodedImage.CreatePooled(w, h, span =>
            {
                fixed (byte* src = srcCopy)
                fixed (byte* dst = span)
                {
                    int hr = tjDecompress2(
                        handle, (nint)src, (nuint)srcCopy.Length,
                        (nint)dst, w, stride, h, TJPF_RGBA, TJFLAG_NONE);
                    if (hr != 0)
                    {
                        throw new ImageDecodeException($"libturbojpeg: tjDecompress2 failed ({ReadError(handle)}).");
                    }
                }
            });
        }
        finally
        {
            tjDestroy(handle);
        }
    }

    private static string ReadError(nint handle)
    {
        nint msg = tjGetErrorStr2(handle);
        return msg == 0 ? "unknown error" : (Marshal.PtrToStringUTF8(msg) ?? "unknown error");
    }

    [LibraryImport(LibTurboJpeg)]
    private static partial nint tjInitDecompress();

    [LibraryImport(LibTurboJpeg)]
    private static partial int tjDestroy(nint handle);

    [LibraryImport(LibTurboJpeg)]
    private static partial int tjDecompressHeader3(
        nint handle, nint jpegBuf, nuint jpegSize,
        out int width, out int height, out int jpegSubsamp, out int jpegColorspace);

    [LibraryImport(LibTurboJpeg)]
    private static partial int tjDecompress2(
        nint handle, nint jpegBuf, nuint jpegSize,
        nint dstBuf, int width, int pitch, int height, int pixelFormat, int flags);

    [LibraryImport(LibTurboJpeg)]
    private static partial nint tjGetErrorStr2(nint handle);
}
