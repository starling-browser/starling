// SPDX-License-Identifier: Apache-2.0
using System.Numerics;
using AwesomeAssertions;

namespace Starling.Scene.Tests;

/// <summary>
/// The Phase 1 trivial scene: build a small generated-UI surface that uses every
/// primitive the v2 IR ships with (solid rect, rounded rect, image, glyph run,
/// clip) and prove the graph, command buffer, and resource table hold together.
/// This is the shape Phase 2's WebGPU renderer will consume.
/// </summary>
[TestClass]
public class TrivialSceneTests
{
    [TestMethod]
    public void Build_a_card_surface_with_every_primitive()
    {
        var graph = new SurfaceGraph(new PxSize(320, 200), scale: 2f);
        var layer = graph.AddLayer(SurfaceLayerKind.GeneratedUi);
        layer.Provenance = new ProvenanceTag("agent.cards", actionId: null, PermissionScope.ReadOnly);

        var scene = layer.Scene;
        var commands = scene.Commands;
        var resources = scene.Resources;

        // A 1x1 opaque image standing in for an avatar thumbnail.
        var avatar = resources.AddImage(new ImageResource(1, 1, contentHash: 0x5151, rgba: new byte[] { 255, 0, 0, 255 }));
        var font = resources.AddFont(new FontResource("Inter", 600, italic: false, key: 0x1010));
        var title = resources.AddGlyphRun(new GlyphRun(font, 16f, new[]
        {
            new PositionedGlyph(10, new Vector2(0, 0)),
            new PositionedGlyph(11, new Vector2(9, 0)),
        }));

        var card = new PxRect(8, 8, 304, 184);
        commands.PushClip(card);
        commands.FillRoundedRect(card, RgbaColor.White, cornerRadius: 12f);
        commands.DrawImage(new PxRect(16, 16, 40, 40), avatar);
        commands.DrawGlyphRun(new Vector2(64, 24), title);
        commands.FillRect(new PxRect(16, 64, 288, 1), RgbaColor.FromRgba(0xE0E0E0FF));
        commands.PopClip();

        // The layer routes a click on the card body to a typed action.
        layer.HitRegions.Add(card, hitId: 42);

        commands.Count.Should().Be(6);
        resources.ImageCount.Should().Be(1);
        resources.GlyphRunCount.Should().Be(1);
        resources.GetGlyphRun(title).Glyphs.Should().HaveCount(2);
        layer.HitRegions.HitTest(new Vector2(160, 100)).Should().Be(42);
        graph.SurfaceSize.Width.Should().Be(320);
        graph.Scale.Should().Be(2f);
    }
}
