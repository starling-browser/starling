// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>A view onto a texture, used as a render-pass color target or a bind-group entry.</summary>
public sealed class GpuTextureView : IDisposable
{
    private readonly IGpuBackend _backend;

    internal GpuTextureView(IGpuBackend backend, object native)
    {
        _backend = backend;
        Native = native;
    }

    internal object Native { get; }

    public void Dispose()
    {
        _backend.Release(Native);
        GC.SuppressFinalize(this);
    }
}
