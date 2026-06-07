// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>
/// The root GPU object, following the WebGPU init chain instance to adapter to
/// device. Create one with <see cref="Create"/> over any <see cref="IGpuBackend"/>.
/// </summary>
public sealed class GpuInstance : IDisposable
{
    private readonly IGpuBackend _backend;

    internal GpuInstance(IGpuBackend backend, object native)
    {
        _backend = backend;
        Native = native;
    }

    internal object Native { get; }

    /// <summary>Creates an instance over the given backend.</summary>
    public static GpuInstance Create(IGpuBackend backend)
        => new(backend, backend.CreateInstance());

    public GpuAdapter RequestAdapter(GpuAdapterOptions options = default)
        => new(_backend, _backend.RequestAdapter(Native, options));

    public GpuSurface CreateSurface(NativeSurfaceDescriptor descriptor)
        => new(_backend, _backend.CreateSurface(Native, descriptor));

    public void Dispose()
    {
        _backend.Release(Native);
        GC.SuppressFinalize(this);
    }
}
