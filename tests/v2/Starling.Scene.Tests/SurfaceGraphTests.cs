// SPDX-License-Identifier: Apache-2.0
using AwesomeAssertions;

namespace Starling.Scene.Tests;

[TestClass]
public class SurfaceGraphTests
{
    [TestMethod]
    public void New_layer_has_identity_transform_and_full_opacity()
    {
        var graph = new SurfaceGraph(new PxSize(800, 600), scale: 2f);
        var layer = graph.AddLayer(SurfaceLayerKind.Document);

        graph.Layers.Should().HaveCount(1);
        layer.Kind.Should().Be(SurfaceLayerKind.Document);
        layer.Opacity.Should().Be(1f);
        layer.Transform.IsIdentity.Should().BeTrue();
        layer.Clip.Should().BeNull();
        layer.Provenance.Should().BeNull();
    }

    [TestMethod]
    public void Layers_keep_insertion_paint_order()
    {
        var graph = new SurfaceGraph(new PxSize(100, 100), scale: 1f);
        graph.AddLayer(SurfaceLayerKind.Document);
        graph.AddLayer(SurfaceLayerKind.GeneratedUi);
        graph.AddLayer(SurfaceLayerKind.Overlay);

        graph.Layers.Select(static l => l.Kind).Should().ContainInOrder(
            SurfaceLayerKind.Document,
            SurfaceLayerKind.GeneratedUi,
            SurfaceLayerKind.Overlay);
    }

    [TestMethod]
    public void Generated_ui_layer_carries_provenance_and_scope()
    {
        var graph = new SurfaceGraph(new PxSize(100, 100), scale: 1f);
        var layer = graph.AddLayer(SurfaceLayerKind.GeneratedUi);
        layer.Provenance = new ProvenanceTag("agent.cards", "open-doc", PermissionScope.Interactive);

        layer.Provenance.Should().NotBeNull();
        layer.Provenance!.Value.Scope.Should().Be(PermissionScope.Interactive);
        layer.Provenance!.Value.ActionId.Should().Be("open-doc");
    }
}
