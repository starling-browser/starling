// SPDX-License-Identifier: Apache-2.0
using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Scroll;
using DomDocument = Starling.Dom.Document;
using LayoutBox = Starling.Layout.Box.Box;

namespace Starling.Gui.Tests;

/// <summary>
/// WP2 of browser-plan/scroll-model.md: the shared <see cref="ScrollController"/>
/// both shells route wheel input through. Real layouts feed the store (the same
/// path the engine session runs), then the controller walks the laid-out box
/// tree — so these cover the ancestor walk, the per-axis latch, rail
/// fall-through, and the precision-versus-line delta conversion without any
/// GPU or Avalonia surface.
/// </summary>
[TestClass]
public sealed class ScrollControllerTests
{
    private static readonly Size Viewport = new(800, 600);

    private sealed record Fixture(DomDocument Document, Starling.Layout.Box.BlockBox Root, ScrollStateStore Store);

    private static Fixture Layout(string html)
    {
        var store = new ScrollStateStore();
        var doc = HtmlParser.Parse(html);
        var root = new LayoutEngine(new StyleEngine()) { ScrollState = store }
            .LayoutDocument(doc, Viewport);
        return new Fixture(doc, root, store);
    }

    /// <summary>The deepest box whose element carries <paramref name="id"/> —
    /// the stand-in for a wheel hit inside that element's subtree.</summary>
    private static LayoutBox BoxOf(Fixture f, string id)
    {
        var el = f.Document.GetElementById(id);
        el.Should().NotBeNull($"#{id} should exist");
        var box = Find(f.Root, el!);
        box.Should().NotBeNull($"#{id} should have a laid-out box");
        return box!;

        static LayoutBox? Find(LayoutBox box, Starling.Dom.Element el)
        {
            if (ReferenceEquals(box.Element, el))
            {
                return box;
            }

            foreach (var child in box.Children)
            {
                if (Find(child, el) is { } hit)
                {
                    return hit;
                }
            }

            return null;
        }
    }

    private static ScrollState StateOf(Fixture f, string id)
    {
        var el = f.Document.GetElementById(id)!;
        f.Store.TryGet(el, out var state).Should().BeTrue($"#{id} should have a store entry");
        return state;
    }

    // ---- Ancestor walk -------------------------------------------------------

    [TestMethod]
    public void Picks_the_deepest_scroller_with_room()
    {
        var f = Layout("""
            <body><div id=outer style="overflow:auto;width:300px;height:300px">
              <div id=inner style="overflow:auto;width:200px;height:100px">
                <div id=content style="height:500px"></div>
              </div>
              <div style="height:2000px"></div>
            </div></body>
            """);

        ScrollController.TryScroll(f.Store, BoxOf(f, "content"), 0, 1, precise: false)
            .Should().BeTrue();

        StateOf(f, "inner").OffsetY.Should().Be(40, "one line = 40 css px lands on the inner scroller");
        StateOf(f, "outer").OffsetY.Should().Be(0, "the outer scroller must not move while the inner has room");
    }

    [TestMethod]
    public void Chains_to_the_ancestor_when_the_inner_scroller_hits_its_rail()
    {
        var f = Layout("""
            <body><div id=outer style="overflow:auto;width:300px;height:300px">
              <div id=inner style="overflow:auto;width:200px;height:100px">
                <div id=content style="height:120px"></div>
              </div>
              <div style="height:2000px"></div>
            </div></body>
            """);

        // Inner range is only 20px; the first tick pins it to the rail.
        ScrollController.TryScroll(f.Store, BoxOf(f, "content"), 0, 1, precise: false)
            .Should().BeTrue();
        StateOf(f, "inner").OffsetY.Should().Be(20);
        StateOf(f, "outer").OffsetY.Should().Be(0);

        // Pinned inner: the next tick walks past it to the outer scroller.
        ScrollController.TryScroll(f.Store, BoxOf(f, "content"), 0, 1, precise: false)
            .Should().BeTrue();
        StateOf(f, "inner").OffsetY.Should().Be(20, "the pinned inner scroller stays at its rail");
        StateOf(f, "outer").OffsetY.Should().Be(40, "the delta chains to the ancestor with room");
    }

    [TestMethod]
    public void Scrolling_back_up_moves_the_inner_scroller_again()
    {
        var f = Layout("""
            <body><div id=outer style="overflow:auto;width:300px;height:300px">
              <div id=inner style="overflow:auto;width:200px;height:100px">
                <div id=content style="height:120px"></div>
              </div>
              <div style="height:2000px"></div>
            </div></body>
            """);

        var content = BoxOf(f, "content");
        ScrollController.TryScroll(f.Store, content, 0, 1, precise: false);   // inner -> rail (20)
        ScrollController.TryScroll(f.Store, content, 0, 1, precise: false);   // outer -> 40

        // Upward delta: the inner scroller has room toward 0 again, so it
        // (not the outer) takes the tick.
        ScrollController.TryScroll(f.Store, content, 0, -0.25, precise: false)
            .Should().BeTrue();
        StateOf(f, "inner").OffsetY.Should().Be(10);
        StateOf(f, "outer").OffsetY.Should().Be(40);
    }

    // ---- Per-axis latch ------------------------------------------------------

    [TestMethod]
    public void Axes_latch_to_different_scrollers()
    {
        // The inner scroller only has vertical room; the outer one only has
        // horizontal room. A diagonal delta must split: dy to the inner, dx to
        // the outer.
        var f = Layout("""
            <body><div id=outer style="overflow:auto;width:300px;height:300px">
              <div style="width:900px">
                <div id=inner style="overflow:auto;width:200px;height:100px">
                  <div id=content style="height:500px"></div>
                </div>
              </div>
            </div></body>
            """);

        ScrollController.TryScroll(f.Store, BoxOf(f, "content"), 1, 1, precise: false)
            .Should().BeTrue();

        var inner = StateOf(f, "inner");
        inner.OffsetY.Should().Be(40, "the vertical delta latches to the deepest scroller with vertical room");
        inner.OffsetX.Should().Be(0, "the inner scroller has no horizontal overflow");
        var outer = StateOf(f, "outer");
        outer.OffsetX.Should().Be(40, "the horizontal delta latches to the ancestor with horizontal room");
        outer.OffsetY.Should().Be(0);
    }

    [TestMethod]
    public void Style_gates_the_axis_even_when_overflow_geometry_allows_it()
    {
        // overflow-y:hidden clips vertically but is not user-scrollable on
        // that axis; the measured overflow alone must not let a wheel pan it.
        var f = Layout("""
            <body><div id=s style="overflow-x:auto;overflow-y:hidden;width:200px;height:100px">
              <div id=content style="width:600px;height:500px"></div>
            </div></body>
            """);

        ScrollController.TryScroll(f.Store, BoxOf(f, "content"), 0, 1, precise: false)
            .Should().BeFalse("no ancestor scrolls vertically");
        StateOf(f, "s").OffsetY.Should().Be(0);

        ScrollController.TryScroll(f.Store, BoxOf(f, "content"), 1, 0, precise: false)
            .Should().BeTrue();
        StateOf(f, "s").OffsetX.Should().Be(40);
    }

    // ---- Fall-through --------------------------------------------------------

    [TestMethod]
    public void Reports_unconsumed_when_no_ancestor_scrolls()
    {
        var f = Layout("""
            <body><div id=plain style="width:200px;height:100px">
              <p id=text>hello</p>
            </div></body>
            """);

        ScrollController.TryScroll(f.Store, BoxOf(f, "text"), 0, 1, precise: false)
            .Should().BeFalse("the shell's root scroller must get the delta");
    }

    [TestMethod]
    public void Reports_unconsumed_at_the_rail_in_the_delta_direction()
    {
        var f = Layout("""
            <body><div id=s style="overflow:auto;width:200px;height:100px">
              <div id=content style="height:500px"></div>
            </div></body>
            """);

        // At offset 0, an upward wheel has nowhere to go: fall through.
        ScrollController.TryScroll(f.Store, BoxOf(f, "content"), 0, -1, precise: false)
            .Should().BeFalse();
        StateOf(f, "s").OffsetY.Should().Be(0);
    }

    [TestMethod]
    public void Zero_delta_consumes_nothing()
    {
        var f = Layout("""
            <body><div id=s style="overflow:auto;width:200px;height:100px">
              <div id=content style="height:500px"></div>
            </div></body>
            """);

        ScrollController.TryScroll(f.Store, BoxOf(f, "content"), 0, 0, precise: false)
            .Should().BeFalse();
        ScrollController.TryScroll(f.Store, BoxOf(f, "content"), 0, 0, precise: true)
            .Should().BeFalse();
    }

    // ---- Delta units (Decision 3) -------------------------------------------

    [TestMethod]
    public void Line_deltas_convert_at_forty_css_px_per_line()
    {
        var f = Layout("""
            <body><div id=s style="overflow:auto;width:200px;height:100px">
              <div id=content style="height:500px"></div>
            </div></body>
            """);

        ScrollController.TryScroll(f.Store, BoxOf(f, "content"), 0, 1.5, precise: false)
            .Should().BeTrue();
        StateOf(f, "s").OffsetY.Should().Be(60, "1.5 lines x 40 css px");
    }

    [TestMethod]
    public void Precision_deltas_pass_through_unscaled()
    {
        var f = Layout("""
            <body><div id=s style="overflow:auto;width:200px;height:100px">
              <div id=content style="height:500px"></div>
            </div></body>
            """);

        ScrollController.TryScroll(f.Store, BoxOf(f, "content"), 0, 7, precise: true)
            .Should().BeTrue();
        StateOf(f, "s").OffsetY.Should().Be(7, "precision deltas are already css px");
    }

    [TestMethod]
    public void Writes_clamp_to_the_measured_range()
    {
        var f = Layout("""
            <body><div id=s style="overflow:auto;width:200px;height:100px">
              <div id=content style="height:500px"></div>
            </div></body>
            """);

        ScrollController.TryScroll(f.Store, BoxOf(f, "content"), 0, 9999, precise: true)
            .Should().BeTrue();
        var s = StateOf(f, "s");
        s.OffsetY.Should().Be(s.MaxOffsetY, "the store clamps the write to the legal range");
    }

    [TestMethod]
    public void Consumed_writes_set_the_pending_event_flag_only()
    {
        // WP2 contract: the wheel path sets store flags and never dispatches —
        // the frame pump (WP4) drains them.
        var f = Layout("""
            <body><div id=s style="overflow:auto;width:200px;height:100px">
              <div id=content style="height:500px"></div>
            </div></body>
            """);

        f.Store.HasPendingEvents.Should().BeFalse("a fresh layout owes no scroll events");
        ScrollController.TryScroll(f.Store, BoxOf(f, "content"), 0, 1, precise: false);
        f.Store.HasPendingEvents.Should().BeTrue();
        StateOf(f, "s").PendingEvent.Should().BeTrue();
    }
}
