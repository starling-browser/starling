// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu.Tests;

internal sealed class FakeGpuTextureView : IGpuTextureView
{
    private readonly List<string> _log;

    public FakeGpuTextureView(List<string> log) => _log = log;

    public void Dispose() => _log.Add("disposeView");
}
