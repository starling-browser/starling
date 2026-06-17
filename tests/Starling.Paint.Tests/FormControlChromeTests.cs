using AwesomeAssertions;
using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Text;
using Starling.Paint.DisplayList;
using LayoutSize = Starling.Layout.Size;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint.Tests;

/// <summary>
/// Tier 4 item 17 — form-control chrome. Pixel probes drive real HTML through
/// the full <see cref="Painter"/> pipeline (UA stylesheet included), so the
/// probes cover the UA sizing rules, the display-list emission, and the
/// ImageSharp <c>StrokeSegments</c>/<c>FillRoundedRect</c> rasterization
/// together. Glyphs are stroked in the element's <c>color</c>, so each test
/// tints the control red and probes for strongly red pixels — the UA border
/// gray (#767676) and white background can never satisfy the predicate.
/// </summary>
[TestClass]
public sealed class FormControlChromeTests
{
    private static RenderedBitmap Render(string html, int w = 400, int h = 120)
    {
        var painter = new Painter();
        var document = HtmlParser.Parse(html);
        return painter.RenderDocument(document, new LayoutSize(w, h));
    }

    private static bool IsRed(byte r, byte g, byte b) => r > 150 && g < 90 && b < 90;

    private static int CountRed(RenderedBitmap bmp)
        => BitmapPixels.Count(bmp, (r, g, b, _) => IsRed(r, g, b));

    /// <summary>Bounding box of all strongly-red pixels, or null when none.</summary>
    private static (int MinX, int MinY, int MaxX, int MaxY)? RedExtent(RenderedBitmap bmp)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        var rgba = bmp.Rgba;
        for (var yPos = 0; yPos < bmp.Height; yPos++)
        {
            for (var xPos = 0; xPos < bmp.Width; xPos++)
            {
                var i = (yPos * bmp.Width + xPos) * 4;
                if (!IsRed(rgba[i], rgba[i + 1], rgba[i + 2]))
                {
                    continue;
                }

                if (xPos < minX)
                {
                    minX = xPos;
                }

                if (yPos < minY)
                {
                    minY = yPos;
                }

                if (xPos > maxX)
                {
                    maxX = xPos;
                }

                if (yPos > maxY)
                {
                    maxY = yPos;
                }
            }
        }
        return maxX < 0 ? null : (minX, minY, maxX, maxY);
    }

    // ----- checkbox ----------------------------------------------------------

    [TestMethod]
    public void Checked_checkbox_paints_check_glyph_pixels()
    {
        using var bmp = Render(
            "<body><input type=\"checkbox\" checked style=\"position:absolute;left:20px;top:20px;color:#ff0000\"></body>");
        CountRed(bmp).Should().BeGreaterThan(3, "the check mark strokes in currentColor");
    }

    [TestMethod]
    public void Unchecked_checkbox_paints_no_glyph_pixels()
    {
        using var bmp = Render(
            "<body><input type=\"checkbox\" style=\"position:absolute;left:20px;top:20px;color:#ff0000\"></body>");
        CountRed(bmp).Should().Be(0, "an unchecked box is an empty bordered square");
    }

    // ----- radio -------------------------------------------------------------

    [TestMethod]
    public void Checked_radio_paints_a_centered_dot()
    {
        // UA sheet: 13px content box + 1px border each side = 15px border box
        // at (20,20), so the control's centre is (27.5, 27.5).
        using var bmp = Render(
            "<body><input type=\"radio\" checked style=\"position:absolute;left:20px;top:20px;color:#ff0000\"></body>");

        var extent = RedExtent(bmp);
        extent.Should().NotBeNull("the checked radio paints a currentColor dot");
        var (minX, minY, maxX, maxY) = extent!.Value;
        var cx = (minX + maxX) / 2d;
        var cy = (minY + maxY) / 2d;
        cx.Should().BeApproximately(27.5, 2.5, "the dot is horizontally centred");
        cy.Should().BeApproximately(27.5, 2.5, "the dot is vertically centred");
        (maxX - minX).Should().BeLessThan(13, "the dot is smaller than the control");
    }

    [TestMethod]
    public void Unchecked_radio_paints_no_dot()
    {
        using var bmp = Render(
            "<body><input type=\"radio\" style=\"position:absolute;left:20px;top:20px;color:#ff0000\"></body>");
        CountRed(bmp).Should().Be(0);
    }

    // ----- select ------------------------------------------------------------

    [TestMethod]
    public void Select_paints_chevron_right_of_center()
    {
        // Border box: 120px content + 20px padding + 2px border = 142px wide
        // starting at x=10 — the horizontal midpoint is x=81. The chevron is
        // right-aligned (5px in from the right border edge).
        using var bmp = Render(
            "<body><select style=\"position:absolute;left:10px;top:10px;width:120px;height:24px;color:#ff0000\"></select></body>");

        var extent = RedExtent(bmp);
        extent.Should().NotBeNull("the select paints a currentColor chevron");
        var (minX, _, maxX, _) = extent!.Value;
        minX.Should().BeGreaterThan(81, "the chevron sits right of the control's centre");
        maxX.Should().BeGreaterThan(130, "the chevron hugs the right edge");
    }

    // ----- placeholder -------------------------------------------------------

    [TestMethod]
    public void Placeholder_paints_muted_gray_text_when_value_is_empty()
    {
        // border:none leaves the placeholder glyphs as the only ink, so every
        // non-white pixel must be the muted gray (and antialiased blends of it
        // toward white) — never the element's near-black color.
        using var bmp = Render(
            "<body><input type=\"text\" placeholder=\"Search here\"" +
            " style=\"position:absolute;left:10px;top:10px;width:200px;border:none\"></body>");

        BitmapPixels.CountNonWhite(bmp).Should().BeGreaterThan(0, "the placeholder text paints");
        BitmapPixels.Count(bmp, static (r, g, b, _) => r < 80 && g < 80 && b < 80)
            .Should().Be(0, "muted placeholder text has no near-black pixels");
        BitmapPixels.Count(bmp, static (r, g, b, _) =>
                Math.Abs(r - g) <= 8 && Math.Abs(g - b) <= 8 && r >= 90 && r <= 200)
            .Should().BeGreaterThan(0, "the glyph cores land on the placeholder gray");
    }

    [TestMethod]
    public void Placeholder_paints_nothing_when_a_value_is_present()
    {
        const string withPlaceholder =
            "<body><input type=\"text\" value=\"Filled\" placeholder=\"Search here\"" +
            " style=\"position:absolute;left:10px;top:10px;width:200px;border:none\"></body>";
        const string withoutPlaceholder =
            "<body><input type=\"text\" value=\"Filled\"" +
            " style=\"position:absolute;left:10px;top:10px;width:200px;border:none\"></body>";

        using var a = Render(withPlaceholder);
        using var b = Render(withoutPlaceholder);

        BitmapPixels.Count(a, static (r, g, b2, _) => r < 80 && g < 80 && b2 < 80)
            .Should().BeGreaterThan(0, "the value text paints in the element's (black) color");
        BitmapPixels.PixelsEqual(a, b).Should().BeTrue(
            "a non-empty value hides the placeholder entirely");
    }

    // ----- appearance: none --------------------------------------------------

    [TestMethod]
    public void Appearance_none_suppresses_the_checkbox_glyph()
    {
        using var bmp = Render(
            "<body><input type=\"checkbox\" checked" +
            " style=\"position:absolute;left:20px;top:20px;color:#ff0000;appearance:none\"></body>");
        CountRed(bmp).Should().Be(0, "appearance:none strips the widget chrome");
    }

    [TestMethod]
    public void Appearance_none_suppresses_the_radio_dot()
    {
        using var bmp = Render(
            "<body><input type=\"radio\" checked" +
            " style=\"position:absolute;left:20px;top:20px;color:#ff0000;appearance:none\"></body>");
        CountRed(bmp).Should().Be(0);
    }

    [TestMethod]
    public void Appearance_none_suppresses_the_select_chevron()
    {
        using var bmp = Render(
            "<body><select style=\"position:absolute;left:10px;top:10px;width:120px;height:24px;" +
            "color:#ff0000;appearance:none\"></select></body>");
        CountRed(bmp).Should().Be(0);
    }

    // ----- display-list emission ----------------------------------------------

    private static PaintList BuildList(string html, LayoutSize viewport)
    {
        var document = HtmlParser.Parse(html);
        var style = new StyleEngine();
        var engine = new LayoutEngine(style, DefaultTextMeasurer.Instance);
        var root = engine.LayoutDocument(document, viewport);
        return new DisplayListBuilder().Build(root);
    }

    [TestMethod]
    public void Checked_checkbox_emits_a_StrokeSegments_item_in_the_element_color()
    {
        var list = BuildList(
            "<body><input type=\"checkbox\" checked style=\"color:#008000\"></body>",
            new LayoutSize(200, 100));

        var glyph = list.Items.OfType<StrokeSegments>().ToList();
        glyph.Should().HaveCount(1);
        glyph[0].Color.Should().Be(new Starling.Css.Values.CssColor(0, 128, 0, 255),
            "the glyph stroke follows currentColor");
        glyph[0].Width.Should().BeGreaterThanOrEqualTo(1.5);
    }

    [TestMethod]
    public void Unchecked_checkbox_emits_no_StrokeSegments_item()
    {
        var list = BuildList(
            "<body><input type=\"checkbox\"></body>", new LayoutSize(200, 100));
        list.Items.OfType<StrokeSegments>().Should().BeEmpty();
    }
}
