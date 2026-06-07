// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// A straight-alpha 8-bit-per-channel color. The renderer premultiplies at
/// upload time, so the scene IR keeps colors in straight alpha for clarity.
/// </summary>
public readonly struct RgbaColor
{
    public RgbaColor(byte r, byte g, byte b, byte a)
    {
        R = r;
        G = g;
        B = b;
        A = a;
    }

    public byte R { get; }
    public byte G { get; }
    public byte B { get; }
    public byte A { get; }

    /// <summary>Builds a color from a packed 0xRRGGBBAA value.</summary>
    public static RgbaColor FromRgba(uint rgba)
        => new((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);

    /// <summary>Returns the color packed as 0xRRGGBBAA.</summary>
    public uint ToRgba()
        => ((uint)R << 24) | ((uint)G << 16) | ((uint)B << 8) | A;

    public static RgbaColor Transparent => new(0, 0, 0, 0);
    public static RgbaColor Black => new(0, 0, 0, 255);
    public static RgbaColor White => new(255, 255, 255, 255);
}
