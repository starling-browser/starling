// SPDX-License-Identifier: Apache-2.0
using System.Numerics;

namespace Starling.Scene;

/// <summary>
/// An append-only list of path-first <see cref="RenderCommand"/> plus a transform
/// side table. This is the renderer-facing contract that replaces v1's
/// <c>DisplayList</c>. Producers append in paint order; geometry and brushes are
/// added to the scene's <see cref="RenderResourceTable"/> and referenced by id.
/// </summary>
public sealed class RenderCommandBuffer
{
    private readonly List<RenderCommand> _commands = [];
    private readonly List<Matrix3x2> _transforms = [];

    public IReadOnlyList<RenderCommand> Commands => _commands;
    public int Count => _commands.Count;

    /// <summary>Resolves the transform a <see cref="RenderCommandKind.PushTransform"/> command refers to.</summary>
    public Matrix3x2 GetTransform(int index) => _transforms[index];

    public void FillPath(PathId path, BrushId brush)
        => _commands.Add(new RenderCommand(RenderCommandKind.FillPath, default, 0f, path.Value, brush.Value));

    public void StrokePath(PathId path, BrushId brush, StrokeStyle style)
        => _commands.Add(new RenderCommand(RenderCommandKind.StrokePath, default, style.Width, path.Value, brush.Value));

    public void DrawImage(PxRect destination, ImageId image)
        => _commands.Add(new RenderCommand(RenderCommandKind.DrawImage, destination, 0f, image.Value, -1));

    public void DrawGlyphRun(Vector2 origin, GlyphRunId glyphRun, BrushId brush)
        => _commands.Add(new RenderCommand(
            RenderCommandKind.DrawGlyphRun,
            new PxRect(origin.X, origin.Y, 0f, 0f),
            0f,
            glyphRun.Value,
            brush.Value));

    public void PushClip(PathId path)
        => _commands.Add(new RenderCommand(RenderCommandKind.PushClip, default, 0f, path.Value, -1));

    public void PopClip()
        => _commands.Add(new RenderCommand(RenderCommandKind.PopClip, default, 0f, -1, -1));

    public void PushTransform(Matrix3x2 transform)
    {
        int index = _transforms.Count;
        _transforms.Add(transform);
        _commands.Add(new RenderCommand(RenderCommandKind.PushTransform, default, 0f, index, -1));
    }

    public void PopTransform()
        => _commands.Add(new RenderCommand(RenderCommandKind.PopTransform, default, 0f, -1, -1));

    public void PushLayer(float opacity, PxRect bounds)
        => _commands.Add(new RenderCommand(RenderCommandKind.PushLayer, bounds, opacity, -1, -1));

    public void PopLayer()
        => _commands.Add(new RenderCommand(RenderCommandKind.PopLayer, default, 0f, -1, -1));

    public void SetBlendMode(BlendMode mode)
        => _commands.Add(new RenderCommand(RenderCommandKind.SetBlendMode, default, 0f, (int)mode, -1));
}
