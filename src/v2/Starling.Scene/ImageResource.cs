// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// A decoded image referenced by a render scene. The pixel bytes are optional:
/// an image that already lives on the GPU carries an empty span and is keyed by
/// <see cref="ContentHash"/> alone, so the renderer can reuse a resident texture.
/// </summary>
public sealed class ImageResource
{
    public ImageResource(int width, int height, long contentHash, ReadOnlyMemory<byte> rgba)
    {
        Width = width;
        Height = height;
        ContentHash = contentHash;
        Rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>Stable identity used to dedup uploads and reuse resident textures.</summary>
    public long ContentHash { get; }

    /// <summary>Top-down, tightly packed RGBA8888 pixels, or empty if GPU-resident.</summary>
    public ReadOnlyMemory<byte> Rgba { get; }
}
