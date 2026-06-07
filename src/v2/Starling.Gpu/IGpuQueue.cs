// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>The device's submission queue. Uploads data and submits command buffers.</summary>
public interface IGpuQueue
{
    void WriteBuffer(IGpuBuffer buffer, ReadOnlySpan<byte> data, int offsetBytes = 0);

    void WriteTexture(IGpuTexture texture, ReadOnlySpan<byte> data, in TextureWrite layout);

    void Submit(IGpuCommandBuffer commandBuffer);
}
