// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu.Tests;

internal sealed class FakeGpuSurface : IGpuSurface
{
    private readonly List<string> _log;

    public FakeGpuSurface(List<string> log) => _log = log;

    public void Configure(int width, int height, GpuTextureFormat format)
        => _log.Add($"surface.configure:{width}x{height}");

    public IGpuTexture AcquireNextTexture()
    {
        _log.Add("surface.acquire");
        return new FakeGpuTexture(_log, 0, 0, GpuTextureFormat.Bgra8Unorm, GpuTextureUsage.RenderAttachment);
    }

    public void Present() => _log.Add("surface.present");

    public void Dispose() => _log.Add("disposeSurface");
}
