// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>A GPU texture. Created by the device, or acquired from a surface as the frame target.</summary>
public interface IGpuTexture : IDisposable
{
    int Width { get; }
    int Height { get; }
    GpuTextureFormat Format { get; }
    GpuTextureUsage Usage { get; }

    /// <summary>Creates a default view over the whole texture.</summary>
    IGpuTextureView CreateView();
}
