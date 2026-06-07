// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>Records GPU commands. Begin a render pass, then finish into a command buffer.</summary>
public interface IGpuCommandEncoder
{
    IGpuRenderPass BeginRenderPass(in RenderPassDescriptor descriptor);

    /// <summary>Closes the encoder and returns the recorded commands.</summary>
    IGpuCommandBuffer Finish();
}
