using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
namespace Starling.Layout.Tests;

/// <summary>
/// Regression coverage for <c>calc()</c> widths/heights that mix a percentage
/// with a length (e.g. <c>calc(100% - 7rem)</c>). These survive parsing as a
/// symbolic CssCalc and must be resolved at layout time against the containing
/// block. Before the fix they fell through ResolveLength/ResolveHeight and were
/// dropped: a normal block fell back to auto width, and an absolutely-positioned
/// box collapsed to 0 wide — the exact reason angular.dev's hero glow
/// (<c>.pattern { position:absolute; width:calc(100% - 7rem) }</c>) was invisible.
/// </summary>
[TestClass]
public sealed class CalcLengthLayoutTests
{
    private static LayoutEngine NewEngine() => new(new StyleEngine());

    private static BlockBox Layout(string html, Size viewport)
        => NewEngine().LayoutDocument(HtmlParser.Parse(html), viewport);

    [TestMethod]
    public void Calc_percent_minus_px_width_resolves_against_container()
    {
        // Container is 400px wide; calc(100% - 50px) = 350px.
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"width:400px\">" +
            "<div id=\"t\" style=\"width:calc(100% - 50px)\">x</div></div></body>",
            new Size(800, 600));

        FindBox(root, "t")!.Frame.Width.Should().Be(350,
            "calc(100% - 50px) of a 400px container is 350px");
    }

    [TestMethod]
    public void Calc_percent_minus_rem_width_resolves_against_container()
    {
        // 7rem at the default 16px/rem = 112px; calc(100% - 7rem) of 400 = 288.
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"width:400px\">" +
            "<div id=\"t\" style=\"width:calc(100% - 7rem)\">x</div></div></body>",
            new Size(800, 600));

        FindBox(root, "t")!.Frame.Width.Should().Be(288,
            "calc(100% - 7rem) of a 400px container is 400 - 112 = 288px");
    }

    [TestMethod]
    public void Calc_width_on_absolutely_positioned_box_is_not_zero()
    {
        // The angular.dev hero-glow shape: an absolutely positioned box whose
        // width is calc(100% - 7rem). It must size to 288px, not collapse to 0.
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"position:relative; width:400px; height:200px\">" +
            "<div id=\"t\" style=\"position:absolute; height:100%; width:calc(100% - 7rem)\"></div>" +
            "</div></body>",
            new Size(800, 600));

        FindBox(root, "t")!.Frame.Width.Should().Be(288,
            "an abspos box with calc(100% - 7rem) width must size to 288px, not 0");
    }

    [TestMethod]
    public void Calc_percent_plus_px_height_resolves_against_container_height()
    {
        // calc(50% + 10px) of a 200px-tall container = 110px.
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"height:200px\">" +
            "<div id=\"t\" style=\"height:calc(50% + 10px)\">x</div></div></body>",
            new Size(800, 600));

        FindBox(root, "t")!.Frame.Height.Should().Be(110,
            "calc(50% + 10px) of a 200px container height is 110px");
    }

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
