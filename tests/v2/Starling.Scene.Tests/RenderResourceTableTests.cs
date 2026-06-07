// SPDX-License-Identifier: Apache-2.0
using AwesomeAssertions;

namespace Starling.Scene.Tests;

[TestClass]
public class RenderResourceTableTests
{
    private static ImageResource Image(long hash)
        => new(width: 4, height: 4, contentHash: hash, rgba: ReadOnlyMemory<byte>.Empty);

    [TestMethod]
    public void Same_image_content_returns_same_id_and_uploads_once()
    {
        var table = new RenderResourceTable();
        var first = table.AddImage(Image(0xABCD));
        var second = table.AddImage(Image(0xABCD));

        first.Should().Be(second);
        table.ImageCount.Should().Be(1);
    }

    [TestMethod]
    public void Different_image_content_returns_different_ids()
    {
        var table = new RenderResourceTable();
        var a = table.AddImage(Image(1));
        var b = table.AddImage(Image(2));

        a.Should().NotBe(b);
        table.ImageCount.Should().Be(2);
        table.GetImage(a).ContentHash.Should().Be(1);
        table.GetImage(b).ContentHash.Should().Be(2);
    }

    [TestMethod]
    public void Same_font_key_dedups()
    {
        var table = new RenderResourceTable();
        var a = table.AddFont(new FontResource("Inter", 400, italic: false, key: 99));
        var b = table.AddFont(new FontResource("Inter", 400, italic: false, key: 99));

        a.Should().Be(b);
        table.FontCount.Should().Be(1);
    }

    [TestMethod]
    public void Glyph_runs_are_not_deduped()
    {
        var table = new RenderResourceTable();
        var font = table.AddFont(new FontResource("Inter", 400, italic: false, key: 1));
        var runA = table.AddGlyphRun(new GlyphRun(font, 14f, []));
        var runB = table.AddGlyphRun(new GlyphRun(font, 14f, []));

        runA.Should().NotBe(runB);
        table.GlyphRunCount.Should().Be(2);
    }
}
