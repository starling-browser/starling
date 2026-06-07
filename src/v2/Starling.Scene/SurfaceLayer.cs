// SPDX-License-Identifier: Apache-2.0
using System.Numerics;

namespace Starling.Scene;

/// <summary>
/// One surface in the <see cref="SurfaceGraph"/>. It owns a render scene, the
/// compositing parameters applied when the compositor blends it (transform,
/// opacity, clip), its hit regions, and optional provenance. Separating content
/// (the scene) from compositing parameters lets a transform or opacity change
/// recomposite without re-rendering the scene.
/// </summary>
public sealed class SurfaceLayer
{
    public SurfaceLayer(SurfaceLayerKind kind, RenderScene scene)
    {
        Kind = kind;
        Scene = scene;
    }

    public SurfaceLayerKind Kind { get; }
    public RenderScene Scene { get; }

    public Matrix3x2 Transform { get; set; } = Matrix3x2.Identity;
    public float Opacity { get; set; } = 1f;
    public PxRect? Clip { get; set; }
    public ProvenanceTag? Provenance { get; set; }

    public HitRegionSet HitRegions { get; } = new();
}
