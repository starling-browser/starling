// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>A width and height in device-independent pixels.</summary>
public readonly struct PxSize
{
    public PxSize(float width, float height)
    {
        Width = width;
        Height = height;
    }

    public float Width { get; }
    public float Height { get; }
}
