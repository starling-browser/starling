// SPDX-License-Identifier: Apache-2.0
using System.Numerics;

namespace Starling.Scene;

/// <summary>
/// One surface in the <see cref="SurfaceGraph"/>. It owns its content (a render
/// scene, an external texture, a video, or a native guest), the compositing
/// parameters applied when the compositor blends it (transform, opacity, clip),
/// its hit regions, its accessibility tree, and optional provenance. Separating
/// content from compositing parameters lets a transform or opacity change
/// recomposite without re-rendering the content. The <see cref="Id"/> and
/// <see cref="ContentHash"/> are the keys the compositor caches resident textures
/// and computes damage with.
/// </summary>
public sealed class SurfaceLayer
{
    public SurfaceLayer(LayerId id, LayerContent content)
    {
        Id = id;
        Content = content;
    }

    public LayerId Id { get; }
    public LayerContent Content { get; }

    /// <summary>A backend-neutral hash of the content. The compositor reuses a resident texture when this is unchanged.</summary>
    public long ContentHash { get; set; }

    public Matrix3x2 Transform { get; set; } = Matrix3x2.Identity;
    public float Opacity { get; set; } = 1f;
    public PxRect? Clip { get; set; }
    public ProvenanceTag? Provenance { get; set; }
    public AccessibilityNode? Accessibility { get; set; }

    public HitRegionSet HitRegions { get; } = new();

    /// <summary>The render scene, when the content is a render scene. Null for texture, video, or guest layers.</summary>
    public RenderScene? Scene => (Content as RenderSceneContent)?.Scene;
}
