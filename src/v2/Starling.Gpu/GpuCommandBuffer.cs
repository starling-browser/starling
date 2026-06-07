// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>A finished, immutable list of GPU commands ready to submit to a queue.</summary>
public sealed class GpuCommandBuffer
{
    internal GpuCommandBuffer(IGpuBackend backend, object native) => Native = native;

    internal object Native { get; }
}
