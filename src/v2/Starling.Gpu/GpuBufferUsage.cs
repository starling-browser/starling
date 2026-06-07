// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>How a buffer may be used. Maps to WebGPU's GPUBufferUsage flags.</summary>
[Flags]
public enum GpuBufferUsage
{
    None = 0,
    Vertex = 1 << 0,
    Index = 1 << 1,
    Uniform = 1 << 2,
    Storage = 1 << 3,
    CopySource = 1 << 4,
    CopyDestination = 1 << 5,
}
