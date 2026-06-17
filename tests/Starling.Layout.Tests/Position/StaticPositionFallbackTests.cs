using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Spec;
namespace Starling.Layout.Tests.Position;

/// <summary>
/// CSS 2.1 §10.3.7 (rule 1) / §10.6.4 (rule 1): when BOTH insets of an axis
/// are <c>auto</c> on an absolutely/fixed-positioned box, the used position is
/// the hypothetical STATIC position — where the in-flow pass would have placed
/// the box — not the containing block's origin.
///
/// The user-visible bug: x.com's fixed-position sidebar card carries no insets
/// at all, so it must paint where it sits in the markup (inside the right
/// sidebar, x≈837 at 1280px), but it snapped to the viewport origin (0,0) and
/// covered the nav rail.
/// </summary>
[TestClass]
[Spec("css2", "https://www.w3.org/TR/CSS21/visudet.html#abs-non-replaced-width", section: "10.3.7")]
[Spec("css2", "https://www.w3.org/TR/CSS21/visudet.html#abs-non-replaced-height", section: "10.6.4")]
public sealed class StaticPositionFallbackTests
{
    private static LayoutEngine NewEngine() => new(new StyleEngine());

    private static BlockBox Layout(string html, Size viewport)
        => NewEngine().LayoutDocument(HtmlParser.Parse(html), viewport);

    private static Box.Box? FindBox(Box.Box root, Func<Box.Box, bool> pred)
    {
        if (pred(root))
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var hit = FindBox(child, pred);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }

    [SpecFact]
    public void Absolute_box_with_no_insets_keeps_its_hypothetical_flow_position()
    {
        // The abs box follows a 120px sibling, so its static position is
        // (0, 120) in the parent's content box. The old behavior snapped it
        // to the initial containing block's origin (0, 0).
        var root = Layout("""
            <body style="margin:0"><div style="width:400px">
              <div style="height:120px"></div>
              <div id="abs" style="position:absolute; width:50px; height:50px"></div>
            </div></body>
            """, new Size(1280, 900));

        var abs = FindBox(root, b => b.Element?.GetAttribute("id") == "abs")!;
        abs.Frame.X.Should().BeApproximately(0, 0.5);
        abs.Frame.Y.Should().BeApproximately(120, 0.5);
    }

    [SpecFact]
    public void Fixed_box_with_no_insets_paints_inside_its_flow_parent()
    {
        // The x.com sidebar shape: a fixed card with no insets inside the
        // right column of a flex row. It stays at the sidebar's content
        // origin (frame 0,0 relative to the sidebar), instead of jumping to
        // the viewport origin (which would be frame.X = -600 here).
        var root = Layout("""
            <body style="margin:0"><div style="display:flex; width:920px">
              <div style="width:600px; height:200px"></div>
              <div id="sidebar" style="width:290px; height:200px">
                <div id="card" style="position:fixed; width:290px; height:100px"></div>
              </div>
            </div></body>
            """, new Size(1280, 900));

        var sidebar = FindBox(root, b => b.Element?.GetAttribute("id") == "sidebar")!;
        sidebar.Frame.X.Should().BeApproximately(600, 0.5);
        var card = FindBox(root, b => b.Element?.GetAttribute("id") == "card")!;
        // Frame is in the sidebar's content-box space.
        card.Frame.X.Should().BeApproximately(0, 0.5);
        card.Frame.Y.Should().BeApproximately(0, 0.5);
    }

    [SpecFact]
    public void Static_position_applies_per_axis_when_only_one_axis_has_an_inset()
    {
        // left:25 wins on the horizontal axis (resolved against the
        // containing block); the vertical axis has both insets auto, so the
        // static position (after the 80px sibling) is used.
        var root = Layout("""
            <body style="margin:0"><div style="position:relative; width:400px; height:300px">
              <div style="height:80px"></div>
              <div id="abs" style="position:absolute; left:25px; width:50px; height:50px"></div>
            </div></body>
            """, new Size(1280, 900));

        var abs = FindBox(root, b => b.Element?.GetAttribute("id") == "abs")!;
        abs.Frame.X.Should().BeApproximately(25, 0.5);
        abs.Frame.Y.Should().BeApproximately(80, 0.5);
    }

    [SpecFact]
    public void Absolute_child_of_a_flex_container_falls_back_to_the_container_origin()
    {
        // CSS Flexbox §4.1 sole-item approximation: the static position of an
        // out-of-flow flex child is the container's content-box origin, not
        // the viewport's.
        var root = Layout("""
            <body style="margin:0"><div style="margin-left:300px; display:flex; width:400px; height:120px">
              <div style="width:100px; height:120px"></div>
              <div id="abs" style="position:absolute; width:50px; height:50px"></div>
            </div></body>
            """, new Size(1280, 900));

        var abs = FindBox(root, b => b.Element?.GetAttribute("id") == "abs")!;
        // Frame is relative to the flex container's content box; the old
        // behavior put the box at the viewport origin (frame.X = -300).
        abs.Frame.X.Should().BeApproximately(0, 0.5);
        abs.Frame.Y.Should().BeApproximately(0, 0.5);
    }
}
