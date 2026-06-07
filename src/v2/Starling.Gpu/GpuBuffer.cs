// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>A GPU buffer. Created by <see cref="GpuDevice.CreateBuffer"/>.</summary>
public sealed class GpuBuffer : IDisposable
{
    private readonly IGpuBackend _backend;

    internal GpuBuffer(IGpuBackend backend, object native, int sizeBytes, GpuBufferUsage usage)
    {
        _backend = backend;
        Native = native;
        SizeBytes = sizeBytes;
        Usage = usage;
    }

    internal object Native { get; }

    public int SizeBytes { get; }
    public GpuBufferUsage Usage { get; }

    public void Dispose()
    {
        _backend.Release(Native);
        GC.SuppressFinalize(this);
    }
}
