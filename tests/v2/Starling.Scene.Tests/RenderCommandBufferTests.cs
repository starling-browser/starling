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
        buffer.FillRect(new PxRect(0, 0, 10, 10), RgbaColor.White);
        buffer.FillRoundedRect(new PxRect(1, 1, 8, 8), RgbaColor.Black, cornerRadius: 4f);

        buffer.Count.Should().Be(2);
        buffer.Commands[0].Kind.Should().Be(RenderCommandKind.FillRect);
        buffer.Commands[1].Kind.Should().Be(RenderCommandKind.FillRoundedRect);
        buffer.Commands[1].Param.Should().Be(4f);
    }

    [TestMethod]
    public void FillRect_carries_color_and_bounds()
    {
        var buffer = new RenderCommandBuffer();
        var rect = new PxRect(2, 3, 4, 5);
        buffer.FillRect(rect, RgbaColor.FromRgba(0x11223344));

        var command = buffer.Commands[0];
        command.Rect.X.Should().Be(2);
        command.Rect.Bottom.Should().Be(8);
        command.Color.ToRgba().Should().Be(0x11223344);
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
        buffer.GetTransform(push.Index).Should().Be(transform);
        buffer.Commands[1].Kind.Should().Be(RenderCommandKind.PopTransform);
    }

    [TestMethod]
    public void DrawGlyphRun_packs_origin_into_rect()
    {
        var buffer = new RenderCommandBuffer();
        buffer.DrawGlyphRun(new Vector2(12f, 20f), new ResourceId(3));

        var command = buffer.Commands[0];
        command.Kind.Should().Be(RenderCommandKind.DrawGlyphRun);
        command.Rect.X.Should().Be(12f);
        command.Rect.Y.Should().Be(20f);
        command.Index.Should().Be(3);
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
}
