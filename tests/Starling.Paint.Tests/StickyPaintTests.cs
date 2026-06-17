// SPDX-License-Identifier: Apache-2.0
using AwesomeAssertions;
using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Scroll;
using Starling.Layout.Text;
using Starling.Paint.Backend;
using Starling.Paint.DisplayList;
using LayoutSize = Starling.Layout.Size;

namespace Starling.Paint.Tests;

/// <summary>
/// WP5 pixel test (browser-plan/scroll-model.md): a sticky header stays
/// pinned at its inset while the scroller's content moves under it. Layout
/// keeps the header's frame natural; the display-list builder reads the same
/// store the scroll offsets come from and applies the shift at paint time.
/// </summary>
[TestClass]
public sealed class StickyPaintTests
{
    private const string Page = """
        <body style='margin:0'>
          <div id=sc style='overflow:auto;width:200px;height:100px'>
            <div id=wrap>
              <div style='height:40px;background:#0000ff'></div>
              <div id=st style='position:sticky;top:0;height:20px;background:#ff0000'></div>
              <div style='height:340px;background:#008000'></div>
            </div>
          </div>
        </body>
        """;

    private static RenderedBitmap Render(string html, double scrollY, ScrollStateStore store)
    {
        var document = HtmlParser.Parse(html);
        var engine = new LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance) { ScrollState = store };
        var root = engine.LayoutDocument(document, new LayoutSize(200, 150));
        if (scrollY != 0)
        {
            store.Write(document.GetElementById("sc")!, 0, scrollY);
        }

        var dl = new DisplayListBuilder().Build(
            root, viewport: null, styleOverride: null, images: null,
            scrollOffsets: store.GetOffset, canvasRect: null, stickyShifts: store.GetStickyShift);
        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        return backend.Render(dl, new LayoutSize(200, 150));
    }

    private static (byte R, byte G, byte B) PixelAt(RenderedBitmap bmp, int x, int y)
    {
        var i = ((y * bmp.Width) + x) * 4;
        return (bmp.Rgba[i], bmp.Rgba[i + 1], bmp.Rgba[i + 2]);
    }

    [TestMethod]
    public void Unscrolled_header_paints_at_its_natural_position()
    {
        var store = new ScrollStateStore();
        using var bmp = Render(Page, scrollY: 0, store);

        PixelAt(bmp, 100, 20).Should().Be(((byte)0, (byte)0, (byte)255), "rows 0-39 are the lead-in block");
        PixelAt(bmp, 100, 50).Should().Be(((byte)255, (byte)0, (byte)0), "the header sits naturally at rows 40-59");
        PixelAt(bmp, 100, 80).Should().Be(((byte)0, (byte)128, (byte)0), "content follows the header");
    }

    [TestMethod]
    public void Mid_scroll_header_pins_to_the_scrollport_top_and_vacates_its_natural_slot()
    {
        var store = new ScrollStateStore();
        using var bmp = Render(Page, scrollY: 100, store);

        // Pinned position: top:0 holds the header at the scrollport's first
        // rows while the content underneath has scrolled by 100.
        PixelAt(bmp, 100, 5).Should().Be(((byte)255, (byte)0, (byte)0), "the stuck header pins at the scrollport top");
        PixelAt(bmp, 100, 15).Should().Be(((byte)255, (byte)0, (byte)0));

        // Vacated natural position: unshifted, the header would paint at
        // scrollport rows -60..-40 — i.e. NOT in the port. Every row below
        // the pinned band must be the green content that scrolled in over
        // the header's natural slot.
        PixelAt(bmp, 100, 25).Should().Be(((byte)0, (byte)128, (byte)0), "content paints in the vacated slot");
        PixelAt(bmp, 100, 60).Should().Be(((byte)0, (byte)128, (byte)0));
        PixelAt(bmp, 100, 95).Should().Be(((byte)0, (byte)128, (byte)0));

        // Exactly one 200x20 band of header red — pinned once, not painted a
        // second time at any translated natural position.
        BitmapPixels.CountExact(bmp, 255, 0, 0).Should().Be(200 * 20);
    }

    [TestMethod]
    public void Stuck_header_composes_with_a_transformed_ancestor()
    {
        // Doc point 5: a transformed ancestor needs no special case — the
        // shift offsets the paint origin inside the ancestor's transform
        // bracket, so the pinned band lands at the translated position.
        const string page = """
            <body style='margin:0'>
              <div style='transform:translate(20px, 10px);width:200px'>
                <div id=sc style='overflow:auto;width:200px;height:100px'>
                  <div id=wrap>
                    <div style='height:40px;background:#0000ff'></div>
                    <div id=st style='position:sticky;top:0;height:20px;background:#ff0000'></div>
                    <div style='height:340px;background:#008000'></div>
                  </div>
                </div>
              </div>
            </body>
            """;
        var store = new ScrollStateStore();
        using var bmp = Render(page, scrollY: 100, store);

        PixelAt(bmp, 100, 15).Should().Be(((byte)255, (byte)0, (byte)0),
            "the pinned band sits at the scroller's translated top (page rows 10-29)");
        PixelAt(bmp, 100, 5).Should().NotBe(((byte)255, (byte)0, (byte)0),
            "nothing paints above the translated scroller");
        PixelAt(bmp, 100, 40).Should().Be(((byte)0, (byte)128, (byte)0));
    }

    [TestMethod]
    public void Partially_stuck_header_shifts_by_the_deficit_only()
    {
        var store = new ScrollStateStore();
        using var bmp = Render(Page, scrollY: 50, store);

        // Natural port position is 40-50 = -10 → shift 10 → pinned at rows
        // 0..19; the blue lead-in's last 30px occupy nothing visible (rows
        // -10..-? are above the port), content fills the rest.
        PixelAt(bmp, 100, 10).Should().Be(((byte)255, (byte)0, (byte)0), "the header is pinned at the port top");
        PixelAt(bmp, 100, 30).Should().Be(((byte)0, (byte)128, (byte)0), "scrolled-in content follows immediately");
        BitmapPixels.CountExact(bmp, 255, 0, 0).Should().Be(200 * 20);
    }
}
