// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// A run of shaped glyphs that share one font and size. The renderer turns each
/// glyph into a textured quad sampled from the face's glyph atlas. Positions are
/// already resolved, so the renderer never reshapes text.
/// </summary>
public sealed class GlyphRun
{
    public GlyphRun(ResourceId font, float fontSize, IReadOnlyList<PositionedGlyph> glyphs)
    {
        Font = font;
        FontSize = fontSize;
        Glyphs = glyphs;
    }

    public ResourceId Font { get; }
    public float FontSize { get; }
    public IReadOnlyList<PositionedGlyph> Glyphs { get; }
}
