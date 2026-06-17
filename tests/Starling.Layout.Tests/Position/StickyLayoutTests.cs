using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Position;
namespace Starling.Layout.Tests.Position;

/// <summary>
/// Tests for <c>position: sticky</c>. Without a scroll model, sticky
/// behaves as <em>clamped-relative</em>: shift only when an inset is
/// violated relative to the containing block's content rect, then clamp
/// the result to stay inside that rect. The scroll-driven shift (the
/// defining behaviour of sticky in real browsers) is documented as
/// future work and not exercised here.
/// </summary>
[TestClass]
public sealed class StickyLayoutTests
{
    private static PositionedProps StickyWith(
        Inset? top = null,
        Inset? right = null,
        Inset? bottom = null,
        Inset? left = null)
        => new(
            PositionKind.Sticky,
            top ?? Inset.Auto,
            right ?? Inset.Auto,
            bottom ?? Inset.Auto,
            left ?? Inset.Auto,
            null);

    // ---- 1. top:10 with natural y=0 → shifts down to y=10. -----------
    [TestMethod]
    public void Sticky_with_top_inset_shifts_element_down_when_natural_y_is_above_threshold()
    {
        var cb = new Rect(0, 0, 200, 200);
        var natural = new Rect(0, 0, 50, 30);
        var result = StickyLayout.ResolveOffset(natural, cb, StickyWith(top: Inset.Pixels(10)));
        result.Y.Should().BeApproximately(10, 0.5);
        result.X.Should().BeApproximately(0, 0.5);
        result.Width.Should().BeApproximately(50, 0.5);
        result.Height.Should().BeApproximately(30, 0.5);
    }

    // ---- 2. top:10 with natural y already > 10 → no shift. ----------
    [TestMethod]
    public void Sticky_with_top_inset_does_not_shift_when_natural_position_already_satisfies_it()
    {
        var cb = new Rect(0, 0, 200, 200);
        var natural = new Rect(0, 50, 50, 30);
        var result = StickyLayout.ResolveOffset(natural, cb, StickyWith(top: Inset.Pixels(10)));
        result.Y.Should().BeApproximately(50, 0.5);
    }

    // ---- 3. top:100, CB (0,0,100,80), 20px-tall, natural y=0 → ------
    //         wants y=100 but CB bottom is 80 → clamp: bottom=80, top=60.
    [TestMethod]
    public void Sticky_with_top_exceeding_containing_block_clamps_so_box_stays_inside_cb()
    {
        var cb = new Rect(0, 0, 100, 80);
        var natural = new Rect(0, 0, 50, 20);
        var result = StickyLayout.ResolveOffset(natural, cb, StickyWith(top: Inset.Pixels(100)));
        // Without clamp the shift would put y=100; clamp pulls bottom (=80)
        // up to cb.Bottom and so top = 80 - 20 = 60.
        result.Y.Should().BeApproximately(60, 0.5);
        result.Bottom.Should().BeApproximately(80, 0.5);
    }

    // ---- 4. bottom:10 with element near CB bottom → bottom can't ----
    //         exceed cb.Bottom - 10.
    [TestMethod]
    public void Sticky_with_bottom_inset_pulls_element_up_when_its_bottom_would_exceed_threshold()
    {
        var cb = new Rect(0, 0, 200, 100);
        // Natural position: bottom edge at y=100 = cb.Bottom — exceeds the
        // cb.Bottom - 10 = 90 threshold by 10px. Expect shift up by 10.
        var natural = new Rect(0, 70, 50, 30);
        var result = StickyLayout.ResolveOffset(natural, cb, StickyWith(bottom: Inset.Pixels(10)));
        result.Y.Should().BeApproximately(60, 0.5);
        result.Bottom.Should().BeApproximately(90, 0.5);
    }

    // ---- 5. Combined top:5, bottom:5, box fits in band → natural ----
    //         position respected.
    [TestMethod]
    public void Sticky_with_top_and_bottom_insets_respects_natural_position_when_inside_band()
    {
        var cb = new Rect(0, 0, 200, 200);
        var natural = new Rect(0, 50, 50, 30);
        var result = StickyLayout.ResolveOffset(
            natural, cb,
            StickyWith(top: Inset.Pixels(5), bottom: Inset.Pixels(5)));
        result.Y.Should().BeApproximately(50, 0.5);
        result.Height.Should().BeApproximately(30, 0.5);
    }

    // ---- 6. Sticky doesn't take element out of flow → siblings ------
    //         position as if it were `relative`. (Integration test.)
    [TestMethod]
    public void Sticky_element_stays_in_flow_so_next_sibling_follows_its_natural_position()
    {
        var engine = new LayoutEngine(new StyleEngine());
        var root = engine.LayoutDocument(HtmlParser.Parse("""
            <body><div id="parent" style="width:400px; height:400px">
              <div id="a" style="width:100px; height:50px; position:sticky; top:200px"></div>
              <div id="b" style="width:100px; height:50px"></div>
            </div></body>
            """), new Size(800, 600));

        var a = FindBox(root, "a")!;
        var b = FindBox(root, "b")!;

        // 'a' is sticky and gets shifted (natural y=0, top:200 → y=200).
        a.Frame.Y.Should().BeApproximately(200, 0.5);
        // 'b' sits where it would have without the shift — sticky doesn't
        // advance the cursor for subsequent siblings, just like `relative`.
        b.Frame.Y.Should().BeApproximately(50, 0.5);
    }

    // ---- 7. position:sticky with no insets set → natural position. --
    [TestMethod]
    public void Sticky_with_no_insets_set_returns_natural_frame_unchanged()
    {
        var cb = new Rect(0, 0, 200, 200);
        var natural = new Rect(15, 25, 50, 30);
        var result = StickyLayout.ResolveOffset(natural, cb, StickyWith());
        result.X.Should().BeApproximately(15, 0.5);
        result.Y.Should().BeApproximately(25, 0.5);
        result.Width.Should().BeApproximately(50, 0.5);
        result.Height.Should().BeApproximately(30, 0.5);
    }

    // ---- 8. left:20 with natural x=0 → shifts to x=20. --------------
    [TestMethod]
    public void Sticky_with_left_inset_shifts_element_right_when_natural_x_is_left_of_threshold()
    {
        var cb = new Rect(0, 0, 200, 200);
        var natural = new Rect(0, 0, 50, 30);
        var result = StickyLayout.ResolveOffset(natural, cb, StickyWith(left: Inset.Pixels(20)));
        result.X.Should().BeApproximately(20, 0.5);
        result.Y.Should().BeApproximately(0, 0.5);
    }

    // ---- 9. right:10 with element near CB right → right edge -------
    //         = cb.Right - 10.
    [TestMethod]
    public void Sticky_with_right_inset_pulls_element_left_when_its_right_would_exceed_threshold()
    {
        var cb = new Rect(0, 0, 200, 100);
        // Natural: right edge at x=200 = cb.Right. Threshold cb.Right - 10
        // = 190. Expect shift left by 10.
        var natural = new Rect(150, 0, 50, 30);
        var result = StickyLayout.ResolveOffset(natural, cb, StickyWith(right: Inset.Pixels(10)));
        result.X.Should().BeApproximately(140, 0.5);
        result.Right.Should().BeApproximately(190, 0.5);
    }

    // ---- helpers -----------------------------------------------------

    private static Box.Box? FindBox(Box.Box root, string id)
    {
        if (root.Element?.GetAttribute("id") == id)
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var hit = FindBox(child, id);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }
}
