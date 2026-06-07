// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// The renderer-neutral command set. CSS paint, generated UI, charts, and video
/// frames all lower into these. This is deliberately small: it is the contract
/// every renderer backend must satisfy, not a mirror of CSS paint primitives.
/// </summary>
public enum RenderCommandKind
{
    FillRect,
    FillRoundedRect,
    DrawImage,
    DrawGlyphRun,
    PushClip,
    PopClip,
    PushTransform,
    PopTransform,
    PushLayer,
    PopLayer,
}
