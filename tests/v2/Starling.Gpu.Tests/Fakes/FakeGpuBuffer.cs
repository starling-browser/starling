// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu.Tests;

internal sealed class FakeGpuBuffer : IGpuBuffer
{
    private readonly List<string> _log;

    public FakeGpuBuffer(List<string> log, BufferDescriptor descriptor)
    {
        _log = log;
        SizeBytes = descriptor.SizeBytes;
        Usage = descriptor.Usage;
    }

    public int SizeBytes { get; }
    public GpuBufferUsage Usage { get; }

    public void Dispose() => _log.Add("disposeBuffer");
}
