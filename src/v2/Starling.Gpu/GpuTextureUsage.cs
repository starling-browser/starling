// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>How a texture may be used. Maps to WebGPU's GPUTextureUsage flags.</summary>
[Flags]
public enum GpuTextureUsage
{
    None = 0,
    CopySource = 1 << 0,
    CopyDestination = 1 << 1,
    TextureBinding = 1 << 2,
    RenderAttachment = 1 << 3,
}
