// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>
/// An in-progress render pass. Phase 1 supports begin, clear, and end so the
/// present path is testable. Pipeline binding, vertex buffers, bind groups, and
/// draw calls are added in Phase 2 when the WebGPU renderer lands.
/// </summary>
public sealed class GpuRenderPass
{
    private readonly IGpuBackend _backend;
    private readonly object _native;

    internal GpuRenderPass(IGpuBackend backend, object native)
    {
        _backend = backend;
        _native = native;
    }

    /// <summary>Ends the pass and releases it. After this the encoder can be finished.</summary>
    public void End()
    {
        _backend.EndRenderPass(_native);
        _backend.Release(_native);
    }
}
