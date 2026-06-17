using AwesomeAssertions;
using Starling.Common.Image;
using Starling.Html;
using LayoutSize = Starling.Layout.Size;

namespace Starling.Paint.Tests;

/// <summary>
/// Pixel proof for CSS UI 4 §7 <c>text-overflow: ellipsis</c>: the truncation
/// happens at layout time (fragment surgery), so the painted raster shows the
/// U+2026 glyph inside the content box and no glyph pixels right of the
/// content edge.
/// </summary>
[TestClass]
public sealed class TextOverflowEllipsisPaintTests
{
    private const string LongText = "mmmm mmmm mmmm mmmm mmmm mmmm mmmm mmmm";

    [TestMethod]
    public void No_glyph_pixels_paint_right_of_the_content_edge()
    {
        var painter = new Painter();

        // Control: same text with overflow left visible. Glyphs must paint
        // past the 80px content edge, proving the probe region catches
        // overflow when nothing suppresses it.
        var visible = HtmlParser.Parse(
            "<body style=\"margin:0; background:#fff\">" +
            "<div style=\"width:80px; white-space:nowrap; font-size:16px; color:#000\">" +
            LongText + "</div></body>");

        // Ellipsis: layout truncates the line inside 80px and appends U+2026.
        var ellipsized = HtmlParser.Parse(
            "<body style=\"margin:0; background:#fff\">" +
            "<div style=\"width:80px; overflow:hidden; white-space:nowrap; " +
            "text-overflow:ellipsis; font-size:16px; color:#000\">" +
            LongText + "</div></body>");

        using var visibleImg = painter.RenderDocument(visible, new LayoutSize(300, 60));
        using var ellipsisImg = painter.RenderDocument(ellipsized, new LayoutSize(300, 60));

        CountDark(visibleImg, 90, 300).Should().BeGreaterThan(0,
            "control: without ellipsis the text paints past the 80px content edge");
        CountDark(ellipsisImg, 84, 300).Should().Be(0,
            "no glyph pixels may paint right of the content edge (84px allows glyph antialias slack)");
        CountDark(ellipsisImg, 0, 84).Should().BeGreaterThan(0,
            "the kept prefix plus the ellipsis glyph paint inside the box");
    }

    [TestMethod]
    public void Ellipsis_glyph_paints_when_no_text_fits()
    {
        // A box too narrow for even one 'm': layout drops every text fragment
        // and keeps only the U+2026 fragment at the line origin — so any dark
        // pixel inside the box is the ellipsis glyph itself.
        var painter = new Painter();
        var doc = HtmlParser.Parse(
            "<body style=\"margin:0; background:#fff\">" +
            "<div style=\"width:14px; overflow:hidden; white-space:nowrap; " +
            "text-overflow:ellipsis; font-size:16px; color:#000\">mmmm mmmm</div></body>");

        using var image = painter.RenderDocument(doc, new LayoutSize(200, 60));

        CountDark(image, 0, 16).Should().BeGreaterThan(0, "the U+2026 glyph paints inside the box");
        CountDark(image, 20, 200).Should().Be(0, "the dropped text fragments paint nothing");
    }

    /// <summary>Counts visibly dark pixels in the column band [x0, x1).</summary>
    private static int CountDark(RenderedBitmap image, int x0, int x1)
    {
        var count = 0;
        var rgba = image.Rgba;
        var width = image.Width;
        var xEnd = Math.Min(x1, width);
        for (var y = 0; y < image.Height; y++)
        {
            var row = y * width * 4;
            for (var x = x0; x < xEnd; x++)
            {
                var i = row + x * 4;
                if (rgba[i] < 128 && rgba[i + 1] < 128 && rgba[i + 2] < 128 && rgba[i + 3] > 0)
                {
                    count++;
                }
            }
        }
        return count;
    }
}
