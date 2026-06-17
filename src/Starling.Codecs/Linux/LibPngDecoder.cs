using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Starling.Common.Image;

namespace Starling.Codecs.Linux;

/// <summary>
/// PNG decode via <c>libpng16.so.16</c> using the libpng 1.6+ "simplified API"
/// (<c>png_image</c> + <c>png_image_begin_read_from_memory</c> +
/// <c>png_image_finish_read</c>). The simplified API hides the read-struct /
/// info-struct / row-callback machinery of classic libpng and lets us ask for
/// a fixed output format — <c>PNG_FORMAT_RGBA</c> gives straight-alpha,
/// top-down, tightly-packed RGBA8888, exactly the <see cref="DecodedImage"/>
/// contract.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class LibPngDecoder
{
    private const string LibPng = "libpng16.so.16";

    // libpng simplified-API constants (png.h).
    private const uint PNG_IMAGE_VERSION = 1;
    private const uint PNG_FORMAT_RGBA = 0x03; // PNG_FORMAT_FLAG_ALPHA | PNG_FORMAT_FLAG_COLOR

    /// <summary>
    /// Mirror of libpng's <c>png_image</c> struct (png.h). Layout must match the
    /// native struct exactly; the <c>opaque</c> pointer and the trailing
    /// reserved fields are libpng-internal.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct PngImage
    {
        public nint Opaque;       // png_controlp
        public uint Version;
        public uint Width;
        public uint Height;
        public uint Format;
        public uint Flags;
        public uint Colormap_entries;
        public uint Warning_or_error;
        // char message[64]
        public unsafe fixed byte Message[64];
    }

    public static unsafe DecodedImage Decode(ReadOnlySpan<byte> bytes)
    {
        var image = default(PngImage);
        image.Version = PNG_IMAGE_VERSION;

        byte[] srcCopy = bytes.ToArray();
        fixed (byte* src = srcCopy)
        {
            if (png_image_begin_read_from_memory(ref image, (nint)src, (nuint)srcCopy.Length) == 0)
            {
                throw new ImageDecodeException($"libpng: begin_read_from_memory failed ({ReadMessage(ref image)}).");
            }
        }

        image.Format = PNG_FORMAT_RGBA;

        var (w, h, _) = NativeImageDecoder.ValidateDecodedDimensions(image.Width, image.Height);

        nint stride = (nint)w * 4;
        try
        {
            return DecodedImage.CreatePooled(w, h, span =>
            {
                fixed (byte* dst = span)
                {
                    // background = null (no compositing), row_stride in bytes,
                    // colormap = null. Returns non-zero on success.
                    if (png_image_finish_read(ref image, 0, (nint)dst, (int)stride, 0) == 0)
                    {
                        throw new ImageDecodeException($"libpng: finish_read failed ({ReadMessage(ref image)}).");
                    }
                }
            });
        }
        finally
        {
            png_image_free(ref image);
        }
    }

    private static unsafe string ReadMessage(ref PngImage image)
    {
        fixed (byte* m = image.Message)
        {
            return Marshal.PtrToStringUTF8((nint)m) ?? "unknown libpng error";
        }
    }

    [LibraryImport(LibPng)]
    private static partial int png_image_begin_read_from_memory(
        ref PngImage image, nint memory, nuint size);

    [LibraryImport(LibPng)]
    private static partial int png_image_finish_read(
        ref PngImage image, nint background, nint buffer, int rowStride, nint colormap);

    [LibraryImport(LibPng)]
    private static partial void png_image_free(ref PngImage image);
}
