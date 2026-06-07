// SPDX-License-Identifier: Apache-2.0
using System.Numerics;

namespace Starling.Scene;

/// <summary>One shaped glyph: a face-local glyph id and its pen offset from the run origin, in pixels.</summary>
public readonly struct PositionedGlyph
{
    public PositionedGlyph(ushort glyphId, Vector2 offset)
    {
        GlyphId = glyphId;
        Offset = offset;
    }

    public ushort GlyphId { get; }
    public Vector2 Offset { get; }
}
