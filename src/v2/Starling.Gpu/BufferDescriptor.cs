// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>Describes a GPU buffer to create.</summary>
public readonly struct BufferDescriptor
{
    public BufferDescriptor(int sizeBytes, GpuBufferUsage usage, string? label = null)
    {
        SizeBytes = sizeBytes;
        Usage = usage;
        Label = label;
    }

    public int SizeBytes { get; }
    public GpuBufferUsage Usage { get; }

    /// <summary>Optional debug label, surfaced to GPU debug tooling.</summary>
    public string? Label { get; }
}
