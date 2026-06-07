// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>A GPU texture. Created by the device, or acquired from a surface as the frame target.</summary>
public sealed class GpuTexture : IDisposable
{
    private readonly IGpuBackend _backend;

    internal GpuTexture(IGpuBackend backend, object native, int width, int height, GpuTextureFormat format, GpuTextureUsage usage)
    {
        _backend = backend;
        Native = native;
        Width = width;
        Height = height;
        Format = format;
        Usage = usage;
    }

    internal object Native { get; }

    public int Width { get; }
    public int Height { get; }
    public GpuTextureFormat Format { get; }
    public GpuTextureUsage Usage { get; }

    /// <summary>Creates a default view over the whole texture.</summary>
    public GpuTextureView CreateView()
        => new(_backend, _backend.CreateTextureView(Native));

    public void Dispose()
    {
        _backend.Release(Native);
        GC.SuppressFinalize(this);
    }
}
