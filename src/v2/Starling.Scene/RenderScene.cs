// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// The renderer-facing scene for one surface layer: a command buffer plus the
/// resource table its commands reference. A document, a generated UI panel, a
/// chart, or a video frame each produce a render scene, and every renderer
/// backend consumes the same shape.
/// </summary>
public sealed class RenderScene
{
    public RenderCommandBuffer Commands { get; } = new();
    public RenderResourceTable Resources { get; } = new();
}
