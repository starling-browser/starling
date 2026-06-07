// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// The path-first, renderer-neutral command set. Geometry is always a path, paint
/// is always a brush. CSS paint, generated UI, charts, and video all lower into
/// these. This is the contract every renderer backend (WebGPU now, Vello or
/// Blend2D later) must satisfy.
/// </summary>
public enum RenderCommandKind
{
    FillPath,
    StrokePath,
    DrawGlyphRun,
    DrawImage,
    PushClip,
    PopClip,
    PushTransform,
    PopTransform,
    PushLayer,
    PopLayer,
    SetBlendMode,
}
