// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>
/// A presentable surface bound to a native window. This is the zero-readback
/// present seam carried over from v1: the compositor blends resident textures
/// straight into the acquired frame texture and presents, with no copy back to the
/// processor. A surface is created from an instance, since it needs platform window
/// handles, not from the device.
/// </summary>
public sealed class GpuSurface : IDisposable
{
    private readonly IGpuBackend _backend;
    private int _width;
    private int _height;
    private GpuTextureFormat _format;

    internal GpuSurface(IGpuBackend backend, object native)
    {
        _backend = backend;
        Native = native;
    }

    internal object Native { get; }

    /// <summary>Configures the swapchain for a device-pixel size and format. Call again on resize.</summary>
    public void Configure(int width, int height, GpuTextureFormat format)
    {
        _width = width;
        _height = height;
        _format = format;
        _backend.ConfigureSurface(Native, width, height, format);
    }

    /// <summary>Acquires the next frame's texture to render into.</summary>
    public GpuTexture AcquireNextTexture()
        => new(_backend, _backend.AcquireSurfaceTexture(Native), _width, _height, _format, GpuTextureUsage.RenderAttachment);

    /// <summary>Presents the acquired frame.</summary>
    public void Present() => _backend.Present(Native);

    public void Dispose()
    {
        _backend.Release(Native);
        GC.SuppressFinalize(this);
    }
}
