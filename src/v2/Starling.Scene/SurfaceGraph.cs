// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// The top of the v2 scene model. A surface graph is an ordered set of surface
/// layers plus the target surface size and scale. The compositor consumes one
/// graph per frame. This is the non-replaceable core: documents, generated UI,
/// video, overlays, and external guests are all surface layers in the same graph,
/// regardless of which renderer backend drew them. The graph assigns each layer a
/// stable <see cref="LayerId"/>.
/// </summary>
public sealed class SurfaceGraph
{
    private readonly List<SurfaceLayer> _layers = [];
    private int _nextLayerId;

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

    /// <summary>Creates a layer backed by a fresh render scene, appends it on top, and returns it.</summary>
    public SurfaceLayer AddRenderSceneLayer()
        => AddLayer(new RenderSceneContent(new RenderScene()));

    /// <summary>Creates a layer with the given content, appends it on top, and returns it.</summary>
    public SurfaceLayer AddLayer(LayerContent content)
    {
        var layer = new SurfaceLayer(new LayerId(_nextLayerId++), content);
        _layers.Add(layer);
        return layer;
    }
}
