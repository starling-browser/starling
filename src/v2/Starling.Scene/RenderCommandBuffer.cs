// SPDX-License-Identifier: Apache-2.0
using System.Numerics;

namespace Starling.Scene;

/// <summary>
/// An append-only list of <see cref="RenderCommand"/> plus a transform side
/// table. This is the renderer-facing contract that replaces v1's
/// <c>DisplayList</c>: a backend (WebGPU, later Vello or Blend2D) walks the
/// commands and draws. Producers append in paint order.
/// </summary>
public sealed class RenderCommandBuffer
{
    private readonly List<RenderCommand> _commands = [];
    private readonly List<Matrix3x2> _transforms = [];

    public IReadOnlyList<RenderCommand> Commands => _commands;
    public int Count => _commands.Count;

    /// <summary>Resolves the transform a <see cref="RenderCommandKind.PushTransform"/> command refers to.</summary>
    public Matrix3x2 GetTransform(int index) => _transforms[index];

    public void FillRect(PxRect rect, RgbaColor color)
        => _commands.Add(new RenderCommand(RenderCommandKind.FillRect, rect, color, 0f, -1));

    public void FillRoundedRect(PxRect rect, RgbaColor color, float cornerRadius)
        => _commands.Add(new RenderCommand(RenderCommandKind.FillRoundedRect, rect, color, cornerRadius, -1));

    public void DrawImage(PxRect destination, ResourceId image)
        => _commands.Add(new RenderCommand(RenderCommandKind.DrawImage, destination, RgbaColor.Transparent, 0f, image.Value));

    public void DrawGlyphRun(Vector2 origin, ResourceId glyphRun)
        => _commands.Add(new RenderCommand(
            RenderCommandKind.DrawGlyphRun,
            new PxRect(origin.X, origin.Y, 0f, 0f),
            RgbaColor.Transparent,
            0f,
            glyphRun.Value));

    public void PushClip(PxRect rect)
        => _commands.Add(new RenderCommand(RenderCommandKind.PushClip, rect, RgbaColor.Transparent, 0f, -1));

    public void PopClip()
        => _commands.Add(new RenderCommand(RenderCommandKind.PopClip, default, RgbaColor.Transparent, 0f, -1));

    public void PushTransform(Matrix3x2 transform)
    {
        int index = _transforms.Count;
        _transforms.Add(transform);
        _commands.Add(new RenderCommand(RenderCommandKind.PushTransform, default, RgbaColor.Transparent, 0f, index));
    }

    public void PopTransform()
        => _commands.Add(new RenderCommand(RenderCommandKind.PopTransform, default, RgbaColor.Transparent, 0f, -1));

    public void PushLayer(float opacity, PxRect bounds)
        => _commands.Add(new RenderCommand(RenderCommandKind.PushLayer, bounds, RgbaColor.Transparent, opacity, -1));

    public void PopLayer()
        => _commands.Add(new RenderCommand(RenderCommandKind.PopLayer, default, RgbaColor.Transparent, 0f, -1));
}
