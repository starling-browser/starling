// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>How a path is stroked. Phase 1 carries width only; caps and joins are added with the renderer in Phase 2.</summary>
public readonly struct StrokeStyle
{
    public StrokeStyle(float width) => Width = width;

    public float Width { get; }
}
