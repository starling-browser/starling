// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>The device's submission queue. Uploads data and submits command buffers. Owned by the device, so not disposable on its own.</summary>
public sealed class GpuQueue
{
    private readonly IGpuBackend _backend;
    private readonly object _native;

    internal GpuQueue(IGpuBackend backend, object native)
    {
        _backend = backend;
        _native = native;
    }

    public void WriteBuffer(GpuBuffer buffer, ReadOnlySpan<byte> data, int offsetBytes = 0)
        => _backend.WriteBuffer(_native, buffer.Native, data, offsetBytes);

    public void WriteTexture(GpuTexture texture, ReadOnlySpan<byte> data, in TextureWrite layout)
        => _backend.WriteTexture(_native, texture.Native, data, layout);

    public void Submit(GpuCommandBuffer commandBuffer)
    {
        // A command buffer is single-use: it is consumed by submission. Release
        // its backend token here so submitting each frame does not leak.
        _backend.Submit(_native, commandBuffer.Native);
        _backend.Release(commandBuffer.Native);
    }
}
