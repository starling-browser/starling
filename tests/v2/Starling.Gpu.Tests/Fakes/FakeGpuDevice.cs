// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu.Tests;

/// <summary>
/// A recording implementation of <see cref="IGpuDevice"/>. It proves the GPU
/// abstraction is implementable without any native backend and lets tests assert
/// the exact sequence of GPU operations a present takes.
/// </summary>
internal sealed class FakeGpuDevice : IGpuDevice
{
    private readonly List<string> _log;

    public FakeGpuDevice(List<string> log)
    {
        _log = log;
        Queue = new FakeGpuQueue(log);
    }

    public IGpuQueue Queue { get; }

    public GpuLimits Limits => new(maxTextureDimension2D: 8192);

    public IGpuBuffer CreateBuffer(in BufferDescriptor descriptor)
    {
        _log.Add($"createBuffer:{descriptor.Label}:{descriptor.SizeBytes}");
        return new FakeGpuBuffer(_log, descriptor);
    }

    public IGpuTexture CreateTexture(in TextureDescriptor descriptor)
    {
        _log.Add($"createTexture:{descriptor.Width}x{descriptor.Height}");
        return new FakeGpuTexture(_log, descriptor.Width, descriptor.Height, descriptor.Format, descriptor.Usage);
    }

    public IGpuCommandEncoder CreateCommandEncoder()
    {
        _log.Add("createEncoder");
        return new FakeGpuCommandEncoder(_log);
    }

    public void Dispose() => _log.Add("disposeDevice");
}
