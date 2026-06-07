// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>Records GPU commands. Begin a render pass, then finish into a command buffer. Transient, so not disposable.</summary>
public sealed class GpuCommandEncoder
{
    private readonly IGpuBackend _backend;
    private readonly object _native;

    internal GpuCommandEncoder(IGpuBackend backend, object native)
    {
        _backend = backend;
        _native = native;
    }

    public GpuRenderPass BeginRenderPass(in RenderPassDescriptor descriptor)
        => new(_backend, _backend.BeginRenderPass(_native, descriptor.ColorTarget.Native, descriptor.Clear, descriptor.ClearColor));

    /// <summary>Closes the encoder and returns the recorded commands. The encoder is consumed and released here.</summary>
    public GpuCommandBuffer Finish()
    {
        var commandBuffer = _backend.FinishEncoder(_native);
        _backend.Release(_native);
        return new GpuCommandBuffer(_backend, commandBuffer);
    }
}
