// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu.Tests;

internal sealed class FakeGpuQueue : IGpuQueue
{
    private readonly List<string> _log;

    public FakeGpuQueue(List<string> log) => _log = log;

    public void WriteBuffer(IGpuBuffer buffer, ReadOnlySpan<byte> data, int offsetBytes = 0)
        => _log.Add($"queue.writeBuffer:{data.Length}");

    public void WriteTexture(IGpuTexture texture, ReadOnlySpan<byte> data, in TextureWrite layout)
        => _log.Add($"queue.writeTexture:{layout.Width}x{layout.Height}");

    public void Submit(IGpuCommandBuffer commandBuffer) => _log.Add("queue.submit");
}
