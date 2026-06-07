// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>
/// The GPU device: allocates resources and records work. This is the bulk of the
/// seam the renderer and compositor see. Shader modules, render pipelines, bind
/// groups, and samplers (the draw API) are added here in Phase 2.
/// </summary>
public sealed class GpuDevice : IDisposable
{
    private readonly IGpuBackend _backend;

    internal GpuDevice(IGpuBackend backend, object native)
    {
        _backend = backend;
        Native = native;
        Queue = new GpuQueue(backend, backend.GetQueue(native));
    }

    internal object Native { get; }

    public GpuQueue Queue { get; }

    public GpuBuffer CreateBuffer(in BufferDescriptor descriptor)
        => new(_backend, _backend.CreateBuffer(Native, descriptor), descriptor.SizeBytes, descriptor.Usage);

    public GpuTexture CreateTexture(in TextureDescriptor descriptor)
        => new(_backend, _backend.CreateTexture(Native, descriptor), descriptor.Width, descriptor.Height, descriptor.Format, descriptor.Usage);

    public GpuCommandEncoder CreateCommandEncoder()
        => new(_backend, _backend.CreateCommandEncoder(Native));

    public void Dispose()
    {
        _backend.Release(Native);
        GC.SuppressFinalize(this);
    }
}
