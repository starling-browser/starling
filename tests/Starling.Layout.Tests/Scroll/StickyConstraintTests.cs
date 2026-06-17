// SPDX-License-Identifier: Apache-2.0
using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Dom;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Layout.Scroll;

namespace Starling.Layout.Tests.Scroll;

/// <summary>
/// WP5 (browser-plan/scroll-model.md, "Sticky: constraints at layout, offset
/// at scroll"): the position pass records per-sticky-element
/// <see cref="StickyConstraints"/> — natural frame, containing block, resolved
/// insets, and the ONE scrolling ancestor it binds to — and the scroll-time
/// shift is pure arithmetic in <see cref="ScrollStateStore.GetStickyShift"/>.
/// Layout frames never move when a store is attached.
/// </summary>
[TestClass]
public sealed class StickyConstraintTests
{
    private static readonly Size Viewport = new(800, 600);

    private static (Document Doc, BlockBox Root, ScrollStateStore Store) Layout(string html)
    {
        var doc = HtmlParser.Parse(html);
        var store = new ScrollStateStore();
        var root = new LayoutEngine(new StyleEngine()) { ScrollState = store }
            .LayoutDocument(doc, Viewport);
        return (doc, root, store);
    }

    private static Element El(Document doc, string id)
    {
        var el = doc.GetElementById(id);
        el.Should().NotBeNull();
        return el!;
    }

    private static Box.Box FindBox(Box.Box box, Element el)
    {
        if (ReferenceEquals(box.Element, el))
        {
            return box;
        }

        foreach (var child in box.Children)
        {
            if (FindBox(child, el) is { } hit)
            {
                return hit;
            }
        }

        return null!;
    }

    // ---- Constraint recording -------------------------------------------------

    [TestMethod]
    public void Constraints_record_natural_frame_insets_and_nearest_scroller()
    {
        var (doc, _, store) = Layout("""
            <body style="margin:0">
              <div id=sc style="overflow:auto;width:200px;height:100px">
                <div id=wrap>
                  <div style="height:40px"></div>
                  <div id=st style="position:sticky;top:10px;height:20px"></div>
                  <div style="height:340px"></div>
                </div>
              </div>
            </body>
            """);

        store.TryGetStickyConstraints(El(doc, "st"), out var c).Should().BeTrue();
        c.Scroller.Should().BeSameAs(El(doc, "sc"),
            "the sticky binds to the first scroll container up the containing-block chain");
        c.NaturalY.Should().Be(40, "natural frame is in the scroller's padding-box space");
        c.NaturalX.Should().Be(0);
        c.Width.Should().Be(200);
        c.Height.Should().Be(20);
        c.Top.Should().Be(10, "only the specified insets constrain");
        c.Bottom.Should().BeNull();
        c.Left.Should().BeNull();
        c.Right.Should().BeNull();
        c.CbY.Should().Be(0);
        c.CbBottom.Should().Be(400, "the wrapper's content box bounds the shift");
    }

    [TestMethod]
    public void Nested_scrollers_bind_to_the_nearest_one_only()
    {
        var (doc, _, store) = Layout("""
            <body style="margin:0">
              <div id=outer style="overflow:auto;width:300px;height:200px">
                <div id=inner style="overflow:auto;width:250px;height:150px">
                  <div id=st style="position:sticky;top:5px;height:10px"></div>
                  <div style="height:400px"></div>
                </div>
                <div style="height:600px"></div>
              </div>
            </body>
            """);

        store.TryGetStickyConstraints(El(doc, "st"), out var c).Should().BeTrue();
        c.Scroller.Should().BeSameAs(El(doc, "inner"));

        // Natural y=0 sits above the 5px line even unscrolled.
        store.GetStickyShift(El(doc, "st")).Should().Be((0d, 5d));

        // The outer scroller moves the whole inner scroller, constraints
        // included — it must not feed this element's shift arithmetic.
        store.Write(El(doc, "outer"), 0, 120);
        store.GetStickyShift(El(doc, "st")).Should().Be((0d, 5d));

        store.Write(El(doc, "inner"), 0, 30);
        store.GetStickyShift(El(doc, "st")).Should().Be((0d, 35d),
            "5 − (0 − 30) — only the bound scroller's offset enters the arithmetic");
    }

    [TestMethod]
    public void Sticky_without_element_scroller_binds_to_the_root_scroller()
    {
        var (doc, _, store) = Layout("""
            <body style="margin:0">
              <div style="height:50px"></div>
              <div id=st style="position:sticky;top:0;height:20px"></div>
              <div style="height:2000px"></div>
            </body>
            """);

        store.TryGetStickyConstraints(El(doc, "st"), out var c).Should().BeTrue();
        c.Scroller.Should().BeNull("no element scroller exists, so the document scroller is the binding");
        c.NaturalY.Should().Be(50, "root-bound constraints are in document space");

        store.GetStickyShift(El(doc, "st")).Should().Be((0d, 0d));
        store.WriteRoot(0, 100);
        store.GetStickyShift(El(doc, "st")).Should().Be((0d, 50d), "0 − (50 − 100)");
    }

    // ---- Scroll-time arithmetic ----------------------------------------------

    [TestMethod]
    public void Shift_is_zero_until_the_inset_is_violated_then_tracks_the_scroll()
    {
        var (doc, _, store) = Layout("""
            <body style="margin:0">
              <div id=sc style="overflow:auto;width:200px;height:100px">
                <div id=wrap>
                  <div style="height:40px"></div>
                  <div id=st style="position:sticky;top:10px;height:20px"></div>
                  <div style="height:340px"></div>
                </div>
              </div>
            </body>
            """);
        var st = El(doc, "st");
        var sc = El(doc, "sc");

        store.GetStickyShift(st).Should().Be((0d, 0d), "natural position 40 is below the 10px line");

        store.Write(sc, 0, 30);
        store.GetStickyShift(st).Should().Be((0d, 0d), "position 10 is exactly at the line");

        store.Write(sc, 0, 50);
        store.GetStickyShift(st).Should().Be((0d, 20d), "10 − (40 − 50) = 20");

        store.Write(sc, 0, 0);
        store.GetStickyShift(st).Should().Be((0d, 0d), "scrolling back unsticks");
    }

    [TestMethod]
    public void Shift_clamps_to_the_containing_block_slack()
    {
        // Wrapper CB bottom = 400; natural bottom = 60 → slack 340. A deep
        // scroll wants a 345px shift; the box must never leave its CB.
        var (doc, _, store) = Layout("""
            <body style="margin:0">
              <div id=sc style="overflow:auto;width:200px;height:25px">
                <div id=wrap>
                  <div style="height:40px"></div>
                  <div id=st style="position:sticky;top:10px;height:20px"></div>
                  <div style="height:340px"></div>
                </div>
              </div>
            </body>
            """);

        store.Write(El(doc, "sc"), 0, 375); // max offset: 400 − 25
        store.GetStickyShift(El(doc, "st")).Should().Be((0d, 340d),
            "slack = containingBlock.bottom (400) − natural.bottom (60)");
    }

    [TestMethod]
    public void Direct_child_of_the_scroller_is_constrained_by_the_scrollable_area()
    {
        // The canonical sticky header: a direct child of its scroller. Its
        // containing block is the scrollable area, not the scroller's fixed
        // 100px content box — the header must pin through the whole range.
        var (doc, _, store) = Layout("""
            <body style="margin:0">
              <div id=sc style="overflow:auto;width:200px;height:100px">
                <div id=st style="position:sticky;top:0;height:20px"></div>
                <div style="height:380px"></div>
              </div>
            </body>
            """);

        store.Write(El(doc, "sc"), 0, 300); // max offset: 400 − 100
        store.GetStickyShift(El(doc, "st")).Should().Be((0d, 300d),
            "a 100px content-box clamp would have stopped the header at 80px");
    }

    [TestMethod]
    public void Bottom_inset_mirrors_pulling_the_box_up_into_the_scrollport()
    {
        // Natural position below the scrollport: bottom:10 holds the box 10px
        // above the port's bottom edge until the scroll catches up.
        var (doc, _, store) = Layout("""
            <body style="margin:0">
              <div id=sc style="overflow:auto;width:200px;height:100px">
                <div id=wrap>
                  <div style="height:200px"></div>
                  <div id=st style="position:sticky;bottom:10px;height:20px"></div>
                  <div style="height:180px"></div>
                </div>
              </div>
            </body>
            """);
        var st = El(doc, "st");

        // Unscrolled: natural bottom 220 sits 130px past the 90px line →
        // wants −130, clamped to the start slack (natural.y − cb.y = 200).
        store.GetStickyShift(st).Should().Be((0d, -130d), "(220 − 0) − (100 − 10)");

        store.Write(El(doc, "sc"), 0, 130);
        store.GetStickyShift(st).Should().Be((0d, 0d),
            "once the natural position scrolls to the line the box travels normally");
    }

    // ---- Layout frames + lifecycle --------------------------------------------

    [TestMethod]
    public void With_a_store_the_layout_frame_stays_natural_even_when_the_inset_is_violated()
    {
        const string html = """
            <body style="margin:0">
              <div id=sc style="overflow:auto;width:200px;height:100px">
                <div id=wrap>
                  <div id=st style="position:sticky;top:50px;height:20px"></div>
                  <div style="height:380px"></div>
                </div>
              </div>
            </body>
            """;

        // With a store: natural frame, shift lives in the store.
        var (doc, root, store) = Layout(html);
        FindBox(root, El(doc, "st")).Frame.Y.Should().Be(0,
            "scroll-model.md: with a scroller, layout frames never move — the shift is paint-time");
        store.GetStickyShift(El(doc, "st")).Should().Be((0d, 50d));

        // Without a store: the clamped-relative fallback shifts at layout,
        // keeping static renders pixel-identical to the pre-scroll-model engine.
        var doc2 = HtmlParser.Parse(html);
        var root2 = new LayoutEngine(new StyleEngine()).LayoutDocument(doc2, Viewport);
        FindBox(root2, El(doc2, "st")).Frame.Y.Should().Be(50);
    }

    [TestMethod]
    public void Constraints_are_swept_when_the_element_stops_being_sticky()
    {
        var doc = HtmlParser.Parse("""
            <body style="margin:0">
              <div id=st style="position:sticky;top:0;height:20px"></div>
              <div style="height:2000px"></div>
            </body>
            """);
        var store = new ScrollStateStore();
        var engine = new LayoutEngine(new StyleEngine()) { ScrollState = store };
        engine.LayoutDocument(doc, Viewport);
        store.TryGetStickyConstraints(El(doc, "st"), out _).Should().BeTrue();

        El(doc, "st").SetAttribute("style", "position:static;height:20px");
        engine.LayoutDocument(doc, Viewport);
        store.TryGetStickyConstraints(El(doc, "st"), out _).Should().BeFalse(
            "a pass that no longer records the element must drop its constraints");
        store.GetStickyShift(El(doc, "st")).Should().Be((0d, 0d));
    }
}
