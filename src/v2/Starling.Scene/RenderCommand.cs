// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// One entry in a <see cref="RenderCommandBuffer"/>. Commands are compact value
/// types so a buffer of thousands costs one array, not thousands of objects.
/// Variable-size payloads (transforms, glyph runs, images) live in side tables
/// and are reached through <see cref="Index"/>; the meaning of each field depends
/// on <see cref="Kind"/>.
/// </summary>
public readonly struct RenderCommand
{
    internal RenderCommand(RenderCommandKind kind, PxRect rect, RgbaColor color, float param, int index)
    {
        Kind = kind;
        Rect = rect;
        Color = color;
        Param = param;
        Index = index;
    }

    public RenderCommandKind Kind { get; }

    /// <summary>
    /// Geometry. Fill/rounded/image bounds, the clip rectangle, or the layer
    /// bounds. For <see cref="RenderCommandKind.DrawGlyphRun"/> the run origin is
    /// in <see cref="PxRect.X"/> and <see cref="PxRect.Y"/>.
    /// </summary>
    public PxRect Rect { get; }

    /// <summary>Fill color for the rect commands; unused otherwise.</summary>
    public RgbaColor Color { get; }

    /// <summary>Corner radius for a rounded rect, or layer opacity for a push layer.</summary>
    public float Param { get; }

    /// <summary>
    /// A resource id value (image or glyph run) or a transform side-table index
    /// for a push transform. -1 when the command needs no payload.
    /// </summary>
    public int Index { get; }
}
