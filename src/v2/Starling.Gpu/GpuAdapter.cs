// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>A chosen GPU adapter. Hands out the device and reports its limits.</summary>
public sealed class GpuAdapter : IDisposable
{
    private readonly IGpuBackend _backend;

    internal GpuAdapter(IGpuBackend backend, object native)
    {
        _backend = backend;
        Native = native;
    }

    internal object Native { get; }

    public GpuLimits Limits => _backend.GetLimits(Native);

    public GpuDevice RequestDevice(GpuDeviceDescriptor descriptor = default)
        => new(_backend, _backend.RequestDevice(Native, descriptor));

    public void Dispose()
    {
        _backend.Release(Native);
        GC.SuppressFinalize(this);
    }
}
