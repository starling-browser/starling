// SPDX-License-Identifier: Apache-2.0
using System.Numerics;
using AwesomeAssertions;

namespace Starling.Scene.Tests;

[TestClass]
public class RenderCommandBufferTests
{
    [TestMethod]
    public void Append_records_commands_in_paint_order()
    {
        var buffer = new RenderCommandBuffer();
        buffer.FillPath(new PathId(0), new BrushId(0));
        buffer.StrokePath(new PathId(0), new BrushId(1), new StrokeStyle(2f));

        buffer.Count.Should().Be(2);
        buffer.Commands[0].Kind.Should().Be(RenderCommandKind.FillPath);
        buffer.Commands[1].Kind.Should().Be(RenderCommandKind.StrokePath);
        buffer.Commands[1].Param.Should().Be(2f);
    }

    [TestMethod]
    public void FillPath_carries_path_and_brush_handles()
    {
        var buffer = new RenderCommandBuffer();
        buffer.FillPath(new PathId(3), new BrushId(7));

        var command = buffer.Commands[0];
        command.A.Should().Be(3);
        command.B.Should().Be(7);
    }

    [TestMethod]
    public void PushTransform_stores_matrix_in_side_table()
    {
        var buffer = new RenderCommandBuffer();
        var transform = Matrix3x2.CreateTranslation(7f, 9f);
        buffer.PushTransform(transform);
        buffer.PopTransform();

        var push = buffer.Commands[0];
        push.Kind.Should().Be(RenderCommandKind.PushTransform);
        buffer.GetTransform(push.A).Should().Be(transform);
        buffer.Commands[1].Kind.Should().Be(RenderCommandKind.PopTransform);
    }

    [TestMethod]
    public void DrawGlyphRun_packs_origin_into_rect_and_carries_brush()
    {
        var buffer = new RenderCommandBuffer();
        buffer.DrawGlyphRun(new Vector2(12f, 20f), new GlyphRunId(3), new BrushId(5));

        var command = buffer.Commands[0];
        command.Kind.Should().Be(RenderCommandKind.DrawGlyphRun);
        command.Rect.X.Should().Be(12f);
        command.Rect.Y.Should().Be(20f);
        command.A.Should().Be(3);
        command.B.Should().Be(5);
    }

    [TestMethod]
    public void PushLayer_carries_opacity_and_bounds()
    {
        var buffer = new RenderCommandBuffer();
        buffer.PushLayer(0.5f, new PxRect(0, 0, 100, 100));
        buffer.PopLayer();

        buffer.Commands[0].Kind.Should().Be(RenderCommandKind.PushLayer);
        buffer.Commands[0].Param.Should().Be(0.5f);
        buffer.Commands[0].Rect.Width.Should().Be(100);
        buffer.Commands[1].Kind.Should().Be(RenderCommandKind.PopLayer);
    }

    [TestMethod]
    public void SetBlendMode_carries_mode()
    {
        var buffer = new RenderCommandBuffer();
        buffer.SetBlendMode(BlendMode.Multiply);

        var command = buffer.Commands[0];
        command.Kind.Should().Be(RenderCommandKind.SetBlendMode);
        ((BlendMode)command.A).Should().Be(BlendMode.Multiply);
    }
}
