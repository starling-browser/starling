// SPDX-License-Identifier: Apache-2.0
using System.Numerics;

namespace Starling.Scene;

/// <summary>How a brush paints.</summary>
public enum BrushKind
{
    Solid,
    LinearGradient,
    Image,
}

/// <summary>One stop in a gradient: an offset in 0..1 and the color at that offset.</summary>
public readonly struct GradientStop
{
    public GradientStop(float offset, RgbaColor color)
    {
        Offset = offset;
        Color = color;
    }

    public float Offset { get; }
    public RgbaColor Color { get; }
}

/// <summary>
/// A paint source for a fill or stroke. A brush is solid, a linear gradient, or
/// an image. Making the paint a brush handle (not a separate FillRect/FillGradient
/// command) keeps the command set small and renderer-neutral.
/// </summary>
public sealed class Brush
{
    private static readonly GradientStop[] NoStops = [];

    private Brush(BrushKind kind, RgbaColor solidColor, Vector2 start, Vector2 end, IReadOnlyList<GradientStop> stops, ImageId image)
    {
        Kind = kind;
        SolidColor = solidColor;
        GradientStart = start;
        GradientEnd = end;
        GradientStops = stops;
        Image = image;
    }

    public BrushKind Kind { get; }

    /// <summary>The color, for a <see cref="BrushKind.Solid"/> brush.</summary>
    public RgbaColor SolidColor { get; }

    /// <summary>Gradient endpoints in path-local pixels, for a <see cref="BrushKind.LinearGradient"/> brush.</summary>
    public Vector2 GradientStart { get; }
    public Vector2 GradientEnd { get; }
    public IReadOnlyList<GradientStop> GradientStops { get; }

    /// <summary>The image, for a <see cref="BrushKind.Image"/> brush.</summary>
    public ImageId Image { get; }

    public static Brush Solid(RgbaColor color)
        => new(BrushKind.Solid, color, default, default, NoStops, ImageId.Invalid);

    public static Brush LinearGradient(Vector2 start, Vector2 end, IReadOnlyList<GradientStop> stops)
        => new(BrushKind.LinearGradient, default, start, end, stops, ImageId.Invalid);

    public static Brush Image(ImageId image)
        => new(BrushKind.Image, default, default, default, NoStops, image);
}
