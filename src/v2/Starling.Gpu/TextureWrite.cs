// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>Layout of the CPU pixel data handed to <see cref="IGpuQueue.WriteTexture"/>.</summary>
public readonly struct TextureWrite
{
    public TextureWrite(int width, int height, int bytesPerRow)
    {
        Width = width;
        Height = height;
        BytesPerRow = bytesPerRow;
    }

    public int Width { get; }
    public int Height { get; }
    public int BytesPerRow { get; }
}
