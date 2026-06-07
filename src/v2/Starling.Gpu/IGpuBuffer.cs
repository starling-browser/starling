// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>A GPU buffer. Created by <see cref="IGpuDevice.CreateBuffer"/>.</summary>
public interface IGpuBuffer : IDisposable
{
    int SizeBytes { get; }
    GpuBufferUsage Usage { get; }
}
