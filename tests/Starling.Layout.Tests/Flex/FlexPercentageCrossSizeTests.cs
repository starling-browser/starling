using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Spec;
namespace Starling.Layout.Tests.Flex;

/// <summary>
/// A flex item's percentage cross size (height in a row container) must resolve
/// to <c>auto</c> when the container's cross size is indefinite — CSS 2.1 §10.5:
/// a percentage height against an indefinite containing block computes to auto.
///
/// The user-visible bug: angular.dev's search field is
/// <c>&lt;input style="height: 100%"&gt;</c> inside an auto-height
/// <c>display: flex</c> wrapper. Starling resolved that <c>100%</c> against the
/// viewport height (~900px), so the input grew to ~900px tall, inflated the
/// banner row, and dragged the hero down until the next section overlapped it.
/// The fix treats a percentage cross size against an indefinite container cross
/// as auto, so the input sizes to its content (~one text line) instead.
///
/// A percentage cross size against a <em>definite</em> container height must
/// still resolve normally — that case is covered too.
/// </summary>
[TestClass]
[Spec("css2", "https://www.w3.org/TR/CSS21/visudet.html#the-height-property", section: "10.5")]
[Spec("css-flexbox-1", "https://drafts.csswg.org/css-flexbox-1/#cross-sizing", section: "9.4")]
public sealed class FlexPercentageCrossSizeTests
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

    private static Box.Box ById(Box.Box root, string id)
        => FindBox(root, b => b.Element?.GetAttribute("id") == id)!;

    [TestMethod]
    public void Percentage_height_against_indefinite_row_container_resolves_to_auto()
    {
        // The flex row has `height: auto` (indefinite cross), so the child's
        // `height: 100%` must NOT resolve against the viewport. The child sizes
        // to its content (one short line), staying well under the 600px viewport.
        var root = Layout("""
            <body><div id="c" style="display:flex; align-items:center; width:300px">
              <div id="item" style="height:100%; width:100px">hi</div>
            </div></body>
            """, new Size(800, 600));

        var item = ById(root, "item");
        // Content is a single text line (~16-20px), nowhere near the viewport.
        item.Frame.Height.Should().BeLessThan(60);
    }

    [TestMethod]
    public void Percentage_height_against_definite_row_container_still_resolves()
    {
        // The container has an explicit 100px height, so the child's `height:50%`
        // resolves to a definite 50px content height.
        var root = Layout("""
            <body><div id="c" style="display:flex; align-items:center; width:300px; height:100px">
              <div id="item" style="height:50%; width:100px"></div>
            </div></body>
            """, new Size(800, 600));

        var item = ById(root, "item");
        item.Frame.Height.Should().BeApproximately(50, 0.5);
    }

    [TestMethod]
    public void Input_with_percentage_height_in_auto_flex_does_not_inflate_the_row()
    {
        // Mirrors angular.dev's search field: an auto-height flex wrapper holding
        // an `<input>` styled `height: 100%`. The input (and therefore the
        // wrapper) must stay near a single line's height, not balloon toward the
        // viewport.
        var root = Layout("""
            <body><div id="c" style="display:flex; align-items:center; padding:8px; width:300px">
              <input id="field" type="text" value="search" style="flex:1; height:100%">
            </div></body>
            """, new Size(1280, 900));

        var field = ById(root, "field");
        field.Frame.Height.Should().BeLessThan(60);

        // The wrapper grows to contain the input plus its 8px padding — it must
        // also stay small, not inherit a viewport-sized child.
        var wrapper = ById(root, "c");
        wrapper.Frame.Height.Should().BeLessThan(80);
    }
}
