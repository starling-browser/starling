// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>
/// A presentable surface bound to a native window. This is the zero-readback
/// present seam carried over from v1: the compositor blends resident textures
/// straight into the acquired frame texture and presents, with no copy back to
/// the CPU. A surface is created by the native backend, not by the device, since
/// it needs platform window handles.
/// </summary>
public interface IGpuSurface : IDisposable
{
    /// <summary>Configures the swapchain for a device-pixel size and format. Call again on resize.</summary>
    void Configure(int width, int height, GpuTextureFormat format);

    /// <summary>Acquires the next frame's texture to render into.</summary>
    IGpuTexture AcquireNextTexture();

    /// <summary>Presents the acquired frame.</summary>
    void Present();
}
