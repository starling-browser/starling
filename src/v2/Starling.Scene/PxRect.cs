// SPDX-License-Identifier: Apache-2.0
using System.Numerics;

namespace Starling.Scene;

/// <summary>
/// An axis-aligned rectangle in device-independent pixels. The scene IR works in
/// float pixels because that is the precision the GPU renderer consumes. Layout
/// (which runs in doubles) lowers into this when it builds a render scene.
/// </summary>
public readonly struct PxRect
{
    public PxRect(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public float X { get; }
    public float Y { get; }
    public float Width { get; }
    public float Height { get; }

    public float Right => X + Width;
    public float Bottom => Y + Height;
    public bool IsEmpty => Width <= 0f || Height <= 0f;

    /// <summary>Builds a rectangle from left/top/right/bottom edges.</summary>
    public static PxRect FromLtrb(float left, float top, float right, float bottom)
        => new(left, top, right - left, bottom - top);

    /// <summary>True when the point falls inside the rectangle, top-left inclusive.</summary>
    public bool Contains(Vector2 point)
        => point.X >= X && point.X < Right && point.Y >= Y && point.Y < Bottom;
}
