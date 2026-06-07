// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>
/// A finished, immutable list of GPU commands ready to submit to a queue. It is
/// single-use: <see cref="GpuQueue.Submit"/> consumes it and releases its backend
/// token, so it is not separately disposable.
/// </summary>
public sealed class GpuCommandBuffer
{
    internal GpuCommandBuffer(IGpuBackend backend, object native) => Native = native;

    internal object Native { get; }
}
