// SPDX-License-Identifier: Apache-2.0
using System.Numerics;

namespace Starling.Gpu;

/// <summary>
/// Describes a render pass: the color target to draw into and whether to clear it
/// first. Clear color is RGBA in the 0..1 range. The pipeline and draw API land in
/// Phase 2; Phase 1 covers begin, clear, and end so the present path can be
/// exercised end to end.
/// </summary>
public readonly struct RenderPassDescriptor
{
    public RenderPassDescriptor(GpuTextureView colorTarget, bool clear, Vector4 clearColor)
    {
        ColorTarget = colorTarget;
        Clear = clear;
        ClearColor = clearColor;
    }

    public GpuTextureView ColorTarget { get; }
    public bool Clear { get; }
    public Vector4 ClearColor { get; }
}
