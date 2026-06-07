// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// One entry in a <see cref="RenderCommandBuffer"/>. Commands are compact value
/// types so a buffer of thousands costs one array, not thousands of objects.
/// Geometry, brushes, and glyph runs live in the resource table and are reached
/// through <see cref="A"/> and <see cref="B"/>; the meaning of each field depends
/// on <see cref="Kind"/>.
/// </summary>
public readonly struct RenderCommand
{
    internal RenderCommand(RenderCommandKind kind, PxRect rect, float param, int a, int b)
    {
        Kind = kind;
        Rect = rect;
        Param = param;
        A = a;
        B = b;
    }

    public RenderCommandKind Kind { get; }

    /// <summary>
    /// Geometry for the commands that carry it inline: the DrawImage destination,
    /// the layer bounds for PushLayer, and the glyph-run origin in
    /// <see cref="PxRect.X"/> and <see cref="PxRect.Y"/> for DrawGlyphRun.
    /// </summary>
    public PxRect Rect { get; }

    /// <summary>Layer opacity for PushLayer, or stroke width for StrokePath.</summary>
    public float Param { get; }

    /// <summary>
    /// Primary handle or index: a PathId value (FillPath, StrokePath, PushClip),
    /// an ImageId value (DrawImage), a GlyphRunId value (DrawGlyphRun), a transform
    /// side-table index (PushTransform), or a BlendMode (SetBlendMode). -1 if unused.
    /// </summary>
    public int A { get; }

    /// <summary>Secondary handle: a BrushId value for FillPath, StrokePath, and DrawGlyphRun. -1 otherwise.</summary>
    public int B { get; }
}
