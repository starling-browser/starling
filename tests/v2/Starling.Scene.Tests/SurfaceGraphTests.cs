// SPDX-License-Identifier: Apache-2.0
using AwesomeAssertions;

namespace Starling.Scene.Tests;

[TestClass]
public class SurfaceGraphTests
{
    [TestMethod]
    public void Render_scene_layer_has_a_scene_and_default_compositing_params()
    {
        var graph = new SurfaceGraph(new PxSize(800, 600), scale: 2f);
        var layer = graph.AddRenderSceneLayer();

        graph.Layers.Should().HaveCount(1);
        (layer.Content is RenderSceneContent).Should().BeTrue();
        layer.Scene.Should().NotBeNull();
        layer.Opacity.Should().Be(1f);
        layer.Transform.IsIdentity.Should().BeTrue();
        layer.Clip.Should().BeNull();
        layer.Provenance.Should().BeNull();
        layer.Accessibility.Should().BeNull();
    }

    [TestMethod]
    public void Layers_get_distinct_ids_in_insertion_order()
    {
        var graph = new SurfaceGraph(new PxSize(100, 100), scale: 1f);
        var a = graph.AddRenderSceneLayer();
        var b = graph.AddLayer(new VideoContent(new VideoHandle(7)));
        var c = graph.AddLayer(new NativeGuestContent(new NativeSurfaceHandle(42)));

        a.Id.Should().NotBe(b.Id);
        b.Id.Should().NotBe(c.Id);
        graph.Layers.Select(static l => l.Id).Should().ContainInOrder(a.Id, b.Id, c.Id);
    }

    [TestMethod]
    public void Non_scene_layers_have_no_scene()
    {
        var graph = new SurfaceGraph(new PxSize(100, 100), scale: 1f);
        var video = graph.AddLayer(new VideoContent(new VideoHandle(1)));

        (video.Content is VideoContent).Should().BeTrue();
        video.Scene.Should().BeNull();
    }

    [TestMethod]
    public void Generated_ui_layer_carries_typed_action_and_scope()
    {
        var graph = new SurfaceGraph(new PxSize(100, 100), scale: 1f);
        var layer = graph.AddRenderSceneLayer();
        layer.Provenance = new ProvenanceTag(
            "agent.cards",
            new ActionRef("calendar.createEvent", requiresConfirmation: true, "Schedule viewing"),
            PermissionScope.Interactive);

        layer.Provenance!.Value.Scope.Should().Be(PermissionScope.Interactive);
        layer.Provenance!.Value.Action!.Value.Tool.Should().Be("calendar.createEvent");
        layer.Provenance!.Value.Action!.Value.RequiresConfirmation.Should().BeTrue();
    }
}
