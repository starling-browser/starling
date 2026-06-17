using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using Starling.Common.Image;

namespace Starling.Codecs.Windows;

/// <summary>
/// Windows image decoder built on the Windows Imaging Component (WIC,
/// <c>windowscodecs.dll</c>). WIC decodes every format the OS ships codecs for
/// (PNG, JPEG, WebP on Win10+, GIF, BMP, TIFF, …). The decode pipeline is:
/// create the factory → wrap the bytes in an <c>IWICStream</c> → create a
/// decoder → take frame 0 → run it through an <c>IWICFormatConverter</c> to
/// 32bpp RGBA → <c>CopyPixels</c> into the <see cref="DecodedImage"/> buffer.
/// </summary>
/// <remarks>
/// COM interop uses the <c>[GeneratedComInterface]</c> source generator
/// (<c>System.Runtime.InteropServices.Marshalling</c>) rather than the legacy
/// <c>ComImport</c> runtime marshaller — no IL emit, AOT-friendly, and the
/// allowed pattern for the Starling.Codecs interop seam. This backend is
/// compile-checked everywhere but only runtime-exercised on the Windows CI leg.
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WicDecoder : IImageDecoder
{
    public unsafe DecodedImage Decode(ReadOnlySpan<byte> bytes)
    {
        IWICImagingFactory? factory = null;
        IWICStream? stream = null;
        IWICBitmapDecoder? decoder = null;
        IWICBitmapFrameDecode? frame = null;
        IWICFormatConverter? converter = null;
        byte[]? pinned = bytes.ToArray();
        try
        {
            int hr = CoCreateInstance(
                in CLSID_WICImagingFactory, 0, CLSCTX_INPROC_SERVER,
                in IID_IWICImagingFactory, out object factoryObj);
            ThrowIfFailed(hr, "CoCreateInstance(WICImagingFactory)");
            factory = (IWICImagingFactory)factoryObj;

            stream = factory.CreateStream();
            fixed (byte* p = pinned)
            {
                stream.InitializeFromMemory((nint)p, (uint)pinned.Length);

                decoder = factory.CreateDecoderFromStream(
                    stream, in Guid.Empty, WICDecodeMetadataCacheOnDemand);

                frame = decoder.GetFrame(0);

                frame.GetSize(out uint width, out uint height);
                var (w, h, _) = NativeImageDecoder.ValidateDecodedDimensions(width, height);

                converter = factory.CreateFormatConverter();
                converter.Initialize(
                    frame, in GUID_WICPixelFormat32bppRGBA,
                    WICBitmapDitherTypeNone, 0, 0.0, WICBitmapPaletteTypeCustom);

                uint stride = (uint)w * 4;

                return DecodedImage.CreatePooled(w, h, span =>
                {
                    fixed (byte* dst = span)
                    {
                        converter.CopyPixels(0, stride, (uint)span.Length, (nint)dst);
                    }
                });
            }
        }
        catch (ImageDecodeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ImageDecodeException("WIC: native image decode failed.", ex);
        }
        finally
        {
            // The generated COM marshaller owns the underlying RCW lifetime;
            // dropping the references is enough. pinned is GC-managed.
            converter = null;
            frame = null;
            decoder = null;
            stream = null;
            factory = null;
            pinned = null;
        }
    }

    private static void ThrowIfFailed(int hr, string what)
    {
        if (hr < 0)
        {
            throw new ImageDecodeException($"WIC: {what} failed (HRESULT 0x{hr:X8}).");
        }
    }

    // --- WIC constants -----------------------------------------------------

    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint WICDecodeMetadataCacheOnDemand = 1;
    private const int WICBitmapDitherTypeNone = 0;
    private const int WICBitmapPaletteTypeCustom = 0;

    // CLSID_WICImagingFactory {CACAF262-9370-4615-A13B-9F5539DA4C0A}
    private static readonly Guid CLSID_WICImagingFactory =
        new(0xCACAF262, 0x9370, 0x4615, 0xA1, 0x3B, 0x9F, 0x55, 0x39, 0xDA, 0x4C, 0x0A);

    // IID_IWICImagingFactory {EC5EC8A9-C395-4314-9C77-54D7A935FF70}
    private static readonly Guid IID_IWICImagingFactory =
        new(0xEC5EC8A9, 0xC395, 0x4314, 0x9C, 0x77, 0x54, 0xD7, 0xA9, 0x35, 0xFF, 0x70);

    // GUID_WICPixelFormat32bppRGBA {F5C7AD2D-6A8D-43DD-A7A8-A29935261AE9}
    private static readonly Guid GUID_WICPixelFormat32bppRGBA =
        new(0xF5C7AD2D, 0x6A8D, 0x43DD, 0xA7, 0xA8, 0xA2, 0x99, 0x35, 0x26, 0x1A, 0xE9);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(
        in Guid rclsid, nint pUnkOuter, uint dwClsContext,
        in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);
}

/// <summary>WIC factory — creates streams, decoders and format converters.</summary>
[GeneratedComInterface]
[Guid("EC5EC8A9-C395-4314-9C77-54D7A935FF70")]
internal partial interface IWICImagingFactory
{
    [PreserveSig]
    int CreateDecoderFromFilename(
        [MarshalAs(UnmanagedType.LPWStr)] string filename, in Guid vendor,
        uint desiredAccess, uint metadataOptions, out IWICBitmapDecoder decoder);

    IWICBitmapDecoder CreateDecoderFromStream(
        IWICStream stream, in Guid vendor, uint metadataOptions);

    [PreserveSig]
    int CreateDecoderFromFileHandle(
        nint file, in Guid vendor, uint metadataOptions, out IWICBitmapDecoder decoder);

    [PreserveSig]
    int CreateComponentInfo(in Guid clsidComponent, out nint info);

    [PreserveSig]
    int CreateDecoder(in Guid containerFormat, in Guid vendor, out IWICBitmapDecoder decoder);

    [PreserveSig]
    int CreateEncoder(in Guid containerFormat, in Guid vendor, out nint encoder);

    [PreserveSig]
    int CreatePalette(out nint palette);

    IWICFormatConverter CreateFormatConverter();

    [PreserveSig]
    int CreateBitmapScaler(out nint scaler);

    [PreserveSig]
    int CreateBitmapClipper(out nint clipper);

    [PreserveSig]
    int CreateBitmapFlipRotator(out nint flipRotator);

    IWICStream CreateStream();
}

/// <summary>WIC stream — wraps an in-memory byte buffer for the decoder.</summary>
[GeneratedComInterface]
[Guid("135FF860-22B7-4DDF-B0F6-218F4F299A43")]
internal partial interface IWICStream
{
    // IWICStream inherits IStream. The source generator uses declaration order
    // as vtable order, so these inherited slots must appear before WIC's own
    // InitializeFrom* methods even though Starling never calls them directly.
    [PreserveSig]
    int Read(nint buffer, uint count, nint bytesRead);

    [PreserveSig]
    int Write(nint buffer, uint count, nint bytesWritten);

    [PreserveSig]
    int Seek(long move, uint origin, nint newPosition);

    [PreserveSig]
    int SetSize(ulong newSize);

    [PreserveSig]
    int CopyTo(nint stream, ulong count, nint bytesRead, nint bytesWritten);

    [PreserveSig]
    int Commit(uint flags);

    [PreserveSig]
    int Revert();

    [PreserveSig]
    int LockRegion(ulong offset, ulong count, uint lockType);

    [PreserveSig]
    int UnlockRegion(ulong offset, ulong count, uint lockType);

    [PreserveSig]
    int Stat(nint stat, uint flags);

    [PreserveSig]
    int Clone(out nint stream);

    void InitializeFromIStream(nint stream);

    void InitializeFromFilename(
        [MarshalAs(UnmanagedType.LPWStr)] string filename, uint desiredAccess);

    void InitializeFromMemory(nint buffer, uint size);

    void InitializeFromIStreamRegion(nint stream, ulong offset, ulong maxSize);
}

/// <summary>WIC decoder — yields one or more frames from an encoded image.</summary>
[GeneratedComInterface]
[Guid("9EDDE9E7-8DEE-47EA-99DF-E6FAF2ED44BF")]
internal partial interface IWICBitmapDecoder
{
    [PreserveSig]
    int QueryCapability(nint stream, out uint capability);

    [PreserveSig]
    int Initialize(nint stream, uint cacheOptions);

    [PreserveSig]
    int GetContainerFormat(out Guid containerFormat);

    [PreserveSig]
    int GetDecoderInfo(out nint decoderInfo);

    [PreserveSig]
    int CopyPalette(nint palette);

    [PreserveSig]
    int GetMetadataQueryReader(out nint reader);

    [PreserveSig]
    int GetPreview(out nint source);

    [PreserveSig]
    int GetColorContexts(uint count, nint contexts, out uint actualCount);

    [PreserveSig]
    int GetThumbnail(out nint thumbnail);

    [PreserveSig]
    int GetFrameCount(out uint count);

    IWICBitmapFrameDecode GetFrame(uint index);
}

/// <summary>A single decoded frame; exposes size and the source pixels.</summary>
[GeneratedComInterface]
[Guid("3B16811B-6A43-4EC9-A813-3D930C13B940")]
internal partial interface IWICBitmapFrameDecode
{
    // IWICBitmapSource members (inherited slots, declared inline because the
    // generated COM source generator does not chase interface inheritance).
    void GetSize(out uint width, out uint height);

    [PreserveSig]
    int GetPixelFormat(out Guid pixelFormat);

    [PreserveSig]
    int GetResolution(out double dpiX, out double dpiY);

    [PreserveSig]
    int CopyPalette(nint palette);

    [PreserveSig]
    int CopyPixels(nint rect, uint stride, uint bufferSize, nint buffer);

    // IWICBitmapFrameDecode-specific members.
    [PreserveSig]
    int GetMetadataQueryReader(out nint reader);

    [PreserveSig]
    int GetColorContexts(uint count, nint contexts, out uint actualCount);

    [PreserveSig]
    int GetThumbnail(out nint thumbnail);
}

/// <summary>Converts a source frame to a requested pixel format (here RGBA8888).</summary>
[GeneratedComInterface]
[Guid("00000301-A8F2-4877-BA0A-FD2B6645FB94")]
internal partial interface IWICFormatConverter
{
    // IWICBitmapSource slots first (see note on IWICBitmapFrameDecode).
    void GetSize(out uint width, out uint height);

    [PreserveSig]
    int GetPixelFormat(out Guid pixelFormat);

    [PreserveSig]
    int GetResolution(out double dpiX, out double dpiY);

    [PreserveSig]
    int CopyPalette(nint palette);

    void CopyPixels(nint rect, uint stride, uint bufferSize, nint buffer);

    // IWICFormatConverter-specific members.
    void Initialize(
        IWICBitmapFrameDecode source, in Guid dstFormat, int dither,
        nint palette, double alphaThreshold, int paletteType);

    [PreserveSig]
    int CanConvert(in Guid srcPixelFormat, in Guid dstPixelFormat, out int canConvert);
}
