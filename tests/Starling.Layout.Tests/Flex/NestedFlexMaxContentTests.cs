using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Spec;
namespace Starling.Layout.Tests.Flex;

/// <summary>
/// Max-content sizing of a row flex container whose own ancestor is also a row
/// flex container. Per CSS Sizing 4 §5 / Flexbox 1 §9.9, the max-content size
/// of a flex container is the sum of its items' max-content contributions
/// (plus main gaps) — not whatever the flex algorithm produces when run at a
/// very large measurement width. When the inner container has a
/// <c>flex-grow</c> child, that child fills the measurement width; reading the
/// post-grow frames back as a "natural" size makes the inner container report
/// itself as wide as the outer container's main size and starves any
/// <c>flex: 1</c> sibling (the user-visible bug: google.com's search pill
/// renders with a zero-width textarea because the right-side icon cluster is
/// itself flex-with-grow and claims all the pill's width during max-content
/// measurement).
///
/// All tests use empty boxes with explicit widths so the expected pixel values
/// are deterministic — no text shaping, no font metrics.
/// </summary>
[TestClass]
[Spec("css-sizing-3", "https://drafts.csswg.org/css-sizing-3/#intrinsic-sizes")]
[Spec("css-flexbox-1", "https://drafts.csswg.org/css-flexbox-1/#intrinsic-sizes", section: "9.9")]
public sealed class NestedFlexMaxContentTests
{
    private static LayoutEngine NewEngine() => new(new StyleEngine());

    private static BlockBox Layout(string html, Size viewport)
        => NewEngine().LayoutDocument(HtmlParser.Parse(html), viewport);

    // ---------------------------------------------------------------------
    // Sanity: the single-level case works today. These guard against
    // regressions in the fix path.
    // ---------------------------------------------------------------------

    [TestMethod]
    public void Single_level_flex_grow_takes_free_space_with_static_sibling()
    {
        // Sanity: not a nested case. Outer flex with a `flex:1` middle and a
        // plain (non-flex) right child. Middle grows to fill the leftovers.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px">
              <div id="l" style="width:24px; height:40px"></div>
              <div id="m" style="flex:1 1 auto; height:40px"></div>
              <div id="r" style="width:80px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        ById(root, "l").Frame.Width.Should().BeApproximately(24, 0.5);
        ById(root, "m").Frame.Width.Should().BeApproximately(496, 0.5);
        ById(root, "r").Frame.Width.Should().BeApproximately(80, 0.5);
    }

    // ---------------------------------------------------------------------
    // The Google-search-pill shape, reduced to flex primitives.
    //   outer (display:flex, width:600)
    //     left  width:24
    //     mid   flex:1 1 auto         <- the textarea slot
    //     right display:flex          <- the right icon cluster
    //       right-grow flex:1 1 auto, display:flex
    //         icon width:24
    //         icon width:24
    //       btn   width:60
    // The right cluster's max-content = sum of its items' max-content =
    //   (24 + 24) + 60 = 108. So mid should grow to 600 - 24 - 108 = 468.
    // ---------------------------------------------------------------------

    [TestMethod]
    public void Nested_flex_with_inner_grow_does_not_starve_outer_flex_grow_sibling()
    {
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px">
              <div id="l" style="width:24px; height:40px"></div>
              <div id="m" style="flex:1 1 auto; height:40px"></div>
              <div id="r" style="display:flex; height:40px">
                <div id="rg" style="flex:1 1 auto; display:flex; height:40px">
                  <div style="width:24px; height:24px"></div>
                  <div style="width:24px; height:24px"></div>
                </div>
                <div id="btn" style="width:60px; height:40px"></div>
              </div>
            </div></body>
            """, new Size(800, 600));

        // The outer container is 600px. Right cluster's natural max-content
        // is the sum of its items' max-content contributions (48 + 60 = 108);
        // the inner grow child should NOT inflate this to ~600.
        ById(root, "l").Frame.Width.Should().BeApproximately(24, 0.5);
        ById(root, "r").Frame.Width.Should().BeApproximately(108, 0.5);
        ById(root, "m").Frame.Width.Should().BeApproximately(468, 0.5);

        // And the inner grow + button still pack correctly inside `r` (the
        // grow child fills the slack left by the button).
        ById(root, "rg").Frame.Width.Should().BeApproximately(48, 0.5);
        ById(root, "btn").Frame.Width.Should().BeApproximately(60, 0.5);
    }

    [TestMethod]
    public void Three_level_nesting_with_innermost_grow_does_not_inflate_outer_max_content()
    {
        // Outer flex (600) → mid (flex:1) + right (flex container).
        // Right (flex) → wrap (flex container).
        // Wrap (flex) → growchild (flex:1) + leaf (width:50).
        // The innermost grow must not inflate "right"'s max-content past 50px
        // (the wrap's only intrinsic content is the 50px leaf).
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px">
              <div id="m" style="flex:1 1 auto; height:40px"></div>
              <div id="r" style="display:flex; height:40px">
                <div id="wrap" style="display:flex; height:40px">
                  <div style="flex:1 1 auto; height:40px"></div>
                  <div id="leaf" style="width:50px; height:40px"></div>
                </div>
              </div>
            </div></body>
            """, new Size(800, 600));

        ById(root, "r").Frame.Width.Should().BeApproximately(50, 0.5);
        ById(root, "m").Frame.Width.Should().BeApproximately(550, 0.5);
    }

    [TestMethod]
    public void Column_inside_row_propagates_max_of_items_not_grown_width()
    {
        // Inner column: max-content cross-size = max(item cross sizes) = 30.
        // (For a column container, the row-direction outer asks for the
        // inner's cross-axis = width, which is the max of its items' widths.)
        var root = Layout("""
            <body><div id="c" style="display:flex; width:400px; height:120px">
              <div id="m" style="flex:1 1 auto; height:40px"></div>
              <div id="r" style="display:flex; flex-direction:column; height:120px">
                <div style="width:30px; height:20px"></div>
                <div style="width:20px; height:20px"></div>
                <div style="flex:1 1 auto; width:auto; height:20px"></div>
              </div>
            </div></body>
            """, new Size(800, 600));

        ById(root, "r").Frame.Width.Should().BeApproximately(30, 0.5);
        ById(root, "m").Frame.Width.Should().BeApproximately(370, 0.5);
    }

    [TestMethod]
    public void Inner_flex_with_only_explicit_widths_already_sizes_correctly()
    {
        // Control case: the inner flex has NO flex-grow children, only
        // explicit widths. This already produces the right answer today; the
        // bug only fires when an inner flex-grow inflates the measurement.
        // Acts as a regression guard for the fix path.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px">
              <div id="m" style="flex:1 1 auto; height:40px"></div>
              <div id="r" style="display:flex; height:40px">
                <div style="width:24px; height:40px"></div>
                <div style="width:24px; height:40px"></div>
                <div style="width:60px; height:40px"></div>
              </div>
            </div></body>
            """, new Size(800, 600));

        ById(root, "r").Frame.Width.Should().BeApproximately(108, 0.5);
        ById(root, "m").Frame.Width.Should().BeApproximately(492, 0.5);
    }

    [TestMethod]
    public void Explicit_width_on_nested_flex_container_overrides_intrinsic_measurement()
    {
        // When the nested flex container has an explicit width, the
        // intrinsic-size path is bypassed entirely — the explicit value wins
        // even if the inner children would have measured larger or smaller.
        // Regression guard for any fix that touches the basis-resolution code.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px">
              <div id="m" style="flex:1 1 auto; height:40px"></div>
              <div id="r" style="display:flex; width:120px; height:40px">
                <div style="flex:1 1 auto; height:40px"></div>
                <div style="width:60px; height:40px"></div>
              </div>
            </div></body>
            """, new Size(800, 600));

        ById(root, "r").Frame.Width.Should().BeApproximately(120, 0.5);
        ById(root, "m").Frame.Width.Should().BeApproximately(480, 0.5);
    }

    [TestMethod]
    public void Inner_flex_with_multiple_grow_children_still_reports_finite_max_content()
    {
        // Two flex-grow children inside the nested container, no explicit
        // widths anywhere. Their combined max-content contribution is zero
        // (empty content), so the nested container's max-content is just the
        // remaining static child (40).
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px">
              <div id="m" style="flex:1 1 auto; height:40px"></div>
              <div id="r" style="display:flex; height:40px">
                <div style="flex:1 1 auto; height:40px"></div>
                <div style="flex:1 1 auto; height:40px"></div>
                <div style="width:40px; height:40px"></div>
              </div>
            </div></body>
            """, new Size(800, 600));

        ById(root, "r").Frame.Width.Should().BeApproximately(40, 0.5);
        ById(root, "m").Frame.Width.Should().BeApproximately(560, 0.5);
    }

    [TestMethod]
    public void Inner_flex_gap_is_included_in_max_content()
    {
        // Inner container: gap:8, three 20-wide static children + one flex:1.
        // Expected max-content: 3 * 20 + 3 * 8 (gaps between four items) = 84.
        // The flex:1 child contributes 0 (no content) but the three gaps
        // around it still count.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px">
              <div id="m" style="flex:1 1 auto; height:40px"></div>
              <div id="r" style="display:flex; gap:8px; height:40px">
                <div style="width:20px; height:40px"></div>
                <div style="flex:1 1 auto; height:40px"></div>
                <div style="width:20px; height:40px"></div>
                <div style="width:20px; height:40px"></div>
              </div>
            </div></body>
            """, new Size(800, 600));

        ById(root, "r").Frame.Width.Should().BeApproximately(84, 0.5);
        ById(root, "m").Frame.Width.Should().BeApproximately(516, 0.5);
    }

    [TestMethod]
    public void Outer_basis_zero_does_not_change_inner_intrinsic_calculation()
    {
        // Same shape as the headline test but the middle uses `flex-basis:0`
        // explicitly rather than the `flex:1 1 auto` shorthand. The bug is in
        // the right-sibling's basis resolution, so the middle's basis form
        // shouldn't matter — middle still ends up at 468.
        var root = Layout("""
            <body><div id="c" style="display:flex; width:600px; height:40px">
              <div id="l" style="width:24px; height:40px"></div>
              <div id="m" style="flex-grow:1; flex-shrink:1; flex-basis:0; height:40px"></div>
              <div id="r" style="display:flex; height:40px">
                <div style="flex:1 1 auto; display:flex; height:40px">
                  <div style="width:24px; height:24px"></div>
                  <div style="width:24px; height:24px"></div>
                </div>
                <div style="width:60px; height:40px"></div>
              </div>
            </div></body>
            """, new Size(800, 600));

        ById(root, "l").Frame.Width.Should().BeApproximately(24, 0.5);
        ById(root, "r").Frame.Width.Should().BeApproximately(108, 0.5);
        ById(root, "m").Frame.Width.Should().BeApproximately(468, 0.5);
    }

    [TestMethod]
    public void Google_search_pill_shape_does_not_collapse_textarea_to_zero()
    {
        // Reduction of the live Google layout (div.SDkEP > UMOYhd, a4bIc,
        // fM33ce.dRYYxd > ywK6Rd[flex:1] + button). The dimensions are
        // representative of a 1280×800 render: the pill is ~686 wide.
        var root = Layout("""
            <body><div id="pill" style="display:flex; width:686px; height:50px">
              <div id="leadicon" style="width:24px; height:50px"></div>
              <div id="textarea-slot" style="flex:1 1 auto; height:50px"></div>
              <div id="right" style="display:flex; height:50px">
                <div id="iconrow" style="flex:1 1 auto; display:flex; height:50px">
                  <div style="width:24px; height:24px"></div>
                  <div style="width:24px; height:24px"></div>
                </div>
                <div id="aimode" style="width:68px; height:36px"></div>
              </div>
            </div></body>
            """, new Size(1280, 800));

        // Right cluster's intrinsic width: 24 + 24 + 68 = 116. The textarea
        // slot should take the rest: 686 - 24 - 116 = 546. The exact textarea
        // value matters less than "much greater than zero" — the user-facing
        // symptom was width:0, so the assertion guards against that class of
        // regression rather than the exact integer.
        ById(root, "textarea-slot").Frame.Width.Should().BeGreaterThan(400);
        ById(root, "right").Frame.Width.Should().BeLessThan(200);
        ById(root, "right").Frame.Width.Should().BeGreaterThan(100);
    }

    // ---------------------------------------------------------------------
    // Helpers — identical shape to FlexLayoutTests' helpers; duplicated here
    // so this file stands alone if someone moves it.
    // ---------------------------------------------------------------------

    private static Box.Box ById(Box.Box root, string id)
    {
        var hit = FindBoxById(root, id);
        hit.Should().NotBeNull($"box with id='{id}' should exist in the layout tree");
        return hit!;
    }

    private static Box.Box? FindBoxById(Box.Box root, string id)
    {
        if (root.Element?.GetAttribute("id") == id)
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var hit = FindBoxById(child, id);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }
}
