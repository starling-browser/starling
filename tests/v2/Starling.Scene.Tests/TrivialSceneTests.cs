// SPDX-License-Identifier: Apache-2.0
using System.Numerics;
using AwesomeAssertions;

namespace Starling.Scene.Tests;

/// <summary>
/// The Phase 1 trivial scene: build a small generated-UI surface that uses every
/// primitive the path-first v2 IR ships with (filled path, image, glyph run, clip)
/// plus a brush, a hit region, an accessibility node, and a typed action. This is
/// the shape Phase 2's WebGPU renderer will consume.
/// </summary>
[TestClass]
public class TrivialSceneTests
{
    [TestMethod]
    public void Build_a_card_surface_with_every_primitive()
    {
        var graph = new SurfaceGraph(new PxSize(320, 200), scale: 2f);
        var layer = graph.AddRenderSceneLayer();
        layer.Provenance = new ProvenanceTag(
            "agent.cards",
            new ActionRef("doc.open", requiresConfirmation: false, "Open document"),
            PermissionScope.Interactive);

        var scene = layer.Scene!;
        var commands = scene.Commands;
        var resources = scene.Resources;

        // Geometry and paint are handles in the resource table.
        var cardRect = new PxRect(8, 8, 304, 184);
        var card = resources.AddPath(Path.RoundedRect(cardRect, radius: 12f));
        var surfaceBrush = resources.AddBrush(Brush.Solid(RgbaColor.White));
        var divider = resources.AddPath(Path.Rect(new PxRect(16, 64, 288, 1)));
        var dividerBrush = resources.AddBrush(Brush.Solid(RgbaColor.FromRgba(0xE0E0E0FF)));

        var avatar = resources.AddImage(new ImageResource(1, 1, contentHash: 0x5151, rgba: new byte[] { 255, 0, 0, 255 }));
        var font = resources.AddFont(new FontResource("Inter", 600, italic: false, key: 0x1010));
        var textBrush = resources.AddBrush(Brush.Solid(RgbaColor.Black));
        var title = resources.AddGlyphRun(new GlyphRun(font, 16f, new[]
        {
            new PositionedGlyph(10, new Vector2(0, 0)),
            new PositionedGlyph(11, new Vector2(9, 0)),
        }));

        commands.PushClip(card);
        commands.FillPath(card, surfaceBrush);
        commands.DrawImage(new PxRect(16, 16, 40, 40), avatar);
        commands.DrawGlyphRun(new Vector2(64, 24), title, textBrush);
        commands.FillPath(divider, dividerBrush);
        commands.PopClip();

        // The layer routes a click on the card body to a typed action, and exposes it to assistive tech.
        layer.HitRegions.Add(cardRect, hitId: 42);
        var root = new AccessibilityNode(AccessibilityRole.Group, name: "Apartment card", bounds: cardRect) { HitId = 42 };
        root.AddChild(new AccessibilityNode(AccessibilityRole.Button, name: "Open document"));
        layer.Accessibility = root;

        commands.Count.Should().Be(6);
        resources.PathCount.Should().Be(2);
        resources.BrushCount.Should().Be(3);
        resources.ImageCount.Should().Be(1);
        resources.GlyphRunCount.Should().Be(1);
        resources.GetGlyphRun(title).Glyphs.Should().HaveCount(2);
        layer.HitRegions.HitTest(new Vector2(160, 100)).Should().Be(42);
        layer.Accessibility!.Children.Should().HaveCount(1);
        graph.SurfaceSize.Width.Should().Be(320);
        graph.Scale.Should().Be(2f);
    }
}
