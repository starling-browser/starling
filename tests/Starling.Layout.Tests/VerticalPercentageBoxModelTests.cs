using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Spec;
namespace Starling.Layout.Tests;

/// <summary>
/// CSS 2.1 §8.3 (margin) / §8.4 (padding): a percentage on ANY of the four
/// margin/padding sides — vertical included — resolves against the containing
/// block's WIDTH, never its height.
///
/// The user-visible bug: vertical percentages resolved against the viewport
/// height (900px), so x.com's `padding-bottom: 33.33%` banner ratio box came
/// out 300px tall regardless of column width, and every `padding-bottom:100%`
/// avatar circle blew up to 900px, shoving the profile content ~1000px down.
/// </summary>
[TestClass]
[Spec("css2", "https://www.w3.org/TR/CSS21/box.html#margin-properties", section: "8.3")]
[Spec("css2", "https://www.w3.org/TR/CSS21/box.html#padding-properties", section: "8.4")]
public sealed class VerticalPercentageBoxModelTests
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
    public void Padding_bottom_percentage_resolves_against_container_width()
    {
        // The aspect-ratio trick: a 600px-wide column with height:0 and
        // padding-bottom:33.3333% is a 600 x 200 ratio box.
        var root = Layout("""
            <body style="margin:0"><div style="width:600px">
              <div id="ratio" style="height:0; padding-bottom:33.3333%"></div>
            </div></body>
            """, new Size(1280, 900));

        var ratio = FindBox(root, b => b.Element?.GetAttribute("id") == "ratio")!;
        ratio.Frame.Height.Should().BeApproximately(200, 0.5);
    }

    [SpecFact]
    public void Padding_bottom_100_percent_of_a_zero_width_container_is_zero()
    {
        var root = Layout("""
            <body style="margin:0"><div style="width:0">
              <div id="p" style="height:0; padding-bottom:100%"></div>
            </div></body>
            """, new Size(1280, 900));

        var p = FindBox(root, b => b.Element?.GetAttribute("id") == "p")!;
        p.Frame.Height.Should().BeApproximately(0, 0.01);
    }

    [SpecFact]
    public void Margin_top_percentage_resolves_against_container_width()
    {
        // 10% of the 400px containing block width = 40, NOT 10% of the 900px
        // viewport height (90).
        var root = Layout("""
            <body style="margin:0"><div style="width:400px">
              <div id="m" style="margin-top:10%; height:10px"></div>
            </div></body>
            """, new Size(1280, 900));

        var m = FindBox(root, b => b.Element?.GetAttribute("id") == "m")!;
        m.Frame.Y.Should().BeApproximately(40, 0.5);
    }

    [SpecFact]
    public void Flex_item_vertical_percentage_padding_resolves_against_container_width()
    {
        // Same rule inside a flex formatting context: the avatar-style ratio
        // box (width 100px via basis, padding-bottom 100%) is 100px tall.
        var root = Layout("""
            <body style="margin:0"><div style="display:flex; width:500px">
              <div id="avatar" style="width:100px; height:0; padding-bottom:20%"></div>
            </div></body>
            """, new Size(1280, 900));

        var avatar = FindBox(root, b => b.Element?.GetAttribute("id") == "avatar")!;
        // 20% of the flex container's 500px content width = 100.
        avatar.Frame.Height.Should().BeApproximately(100, 0.5);
    }

    [SpecFact]
    public void Absolutely_positioned_vertical_percentage_padding_resolves_against_cb_width()
    {
        // Containing block = the positioned ancestor's padding box (400px
        // wide); padding-top:25% = 100, not 225 (25% of 900px viewport).
        var root = Layout("""
            <body style="margin:0"><div style="position:relative; width:400px; height:300px">
              <div id="abs" style="position:absolute; left:0; top:0; width:50px; height:0; padding-top:25%"></div>
            </div></body>
            """, new Size(1280, 900));

        var abs = FindBox(root, b => b.Element?.GetAttribute("id") == "abs")!;
        abs.Frame.Height.Should().BeApproximately(100, 0.5);
    }
}
