// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu.Tests;

internal sealed class FakeGpuRenderPass : IGpuRenderPass
{
    private readonly List<string> _log;

    public FakeGpuRenderPass(List<string> log) => _log = log;

    public void End() => _log.Add("renderPass.end");
}
