// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu.Tests;

internal sealed class FakeGpuCommandEncoder : IGpuCommandEncoder
{
    private readonly List<string> _log;

    public FakeGpuCommandEncoder(List<string> log) => _log = log;

    public IGpuRenderPass BeginRenderPass(in RenderPassDescriptor descriptor)
    {
        _log.Add(descriptor.Clear ? "renderPass.begin:clear" : "renderPass.begin");
        return new FakeGpuRenderPass(_log);
    }

    public IGpuCommandBuffer Finish()
    {
        _log.Add("encoder.finish");
        return new FakeGpuCommandBuffer();
    }
}
