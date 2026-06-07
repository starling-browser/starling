// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>Describes a GPU texture to create.</summary>
public readonly struct TextureDescriptor
{
    public TextureDescriptor(int width, int height, GpuTextureFormat format, GpuTextureUsage usage, string? label = null)
    {
        Width = width;
        Height = height;
        Format = format;
        Usage = usage;
        Label = label;
    }

    public int Width { get; }
    public int Height { get; }
    public GpuTextureFormat Format { get; }
    public GpuTextureUsage Usage { get; }

    /// <summary>Optional debug label, surfaced to GPU debug tooling.</summary>
    public string? Label { get; }
}
