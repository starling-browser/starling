// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>
/// The GPU device: the root object that allocates resources and records work.
/// This is the whole seam the v2 renderer and compositor are allowed to see. Only
/// Starling.Gpu.WgpuNative implements it against wgpu-native. Swapping in Dawn
/// later means a second implementation, not a change to any renderer code.
/// </summary>
public interface IGpuDevice : IDisposable
{
    IGpuQueue Queue { get; }

    GpuLimits Limits { get; }

    IGpuBuffer CreateBuffer(in BufferDescriptor descriptor);

    IGpuTexture CreateTexture(in TextureDescriptor descriptor);

    IGpuCommandEncoder CreateCommandEncoder();
}
