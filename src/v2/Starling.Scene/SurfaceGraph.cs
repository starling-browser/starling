// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// The top of the v2 scene model. A surface graph is an ordered set of surface
/// layers plus the target surface size and scale. The compositor consumes one
/// graph per frame. This is the non-replaceable core: documents, generated UI,
/// video, overlays, and external guests are all surface producers that feed the
/// same graph, regardless of which renderer backend drew them.
/// </summary>
public sealed class SurfaceGraph
{
    private readonly List<SurfaceLayer> _layers = [];

    public SurfaceGraph(PxSize surfaceSize, float scale)
    {
        SurfaceSize = surfaceSize;
        Scale = scale;
    }

    /// <summary>Logical surface size in device-independent pixels.</summary>
    public PxSize SurfaceSize { get; }

    /// <summary>Device pixel ratio. Device size is the surface size times this scale.</summary>
    public float Scale { get; }

    /// <summary>Layers in bottom-to-top paint order.</summary>
    public IReadOnlyList<SurfaceLayer> Layers => _layers;

    /// <summary>Creates a layer with an empty render scene, appends it on top, and returns it.</summary>
    public SurfaceLayer AddLayer(SurfaceLayerKind kind)
    {
        var layer = new SurfaceLayer(kind, new RenderScene());
        _layers.Add(layer);
        return layer;
    }

    /// <summary>Appends an existing layer on top.</summary>
    public void AddLayer(SurfaceLayer layer) => _layers.Add(layer);
}
