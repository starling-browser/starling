// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>
/// An in-progress render pass. Phase 1 supports begin/clear/end so the present
/// path is testable. Pipeline binding, vertex buffers, bind groups, and draw
/// calls are added in Phase 2 when the WebGPU renderer lands.
/// </summary>
public interface IGpuRenderPass
{
    /// <summary>Ends the pass. After this the encoder can be finished.</summary>
    void End();
}
