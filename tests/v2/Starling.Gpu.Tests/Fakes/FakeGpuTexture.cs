// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu.Tests;

internal sealed class FakeGpuTexture : IGpuTexture
{
    private readonly List<string> _log;

    public FakeGpuTexture(List<string> log, int width, int height, GpuTextureFormat format, GpuTextureUsage usage)
    {
        _log = log;
        Width = width;
        Height = height;
        Format = format;
        Usage = usage;
    }

    public int Width { get; }
    public int Height { get; }
    public GpuTextureFormat Format { get; }
    public GpuTextureUsage Usage { get; }

    public IGpuTextureView CreateView()
    {
        _log.Add("createView");
        return new FakeGpuTextureView(_log);
    }

    public void Dispose() => _log.Add("disposeTexture");
}
