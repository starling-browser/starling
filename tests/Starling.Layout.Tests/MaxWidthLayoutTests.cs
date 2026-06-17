using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
namespace Starling.Layout.Tests;

/// <summary>
/// Locks down the <c>max-width</c> / <c>min-width</c> clamp that was lost in a
/// previous refactor. The canonical regression page is justinjackson.ca/words.html,
/// which uses <c>body { max-width: 35em; margin: 2em auto; }</c>; without the
/// clamp it renders full-viewport-wide instead of in a narrow centered column.
/// </summary>
[TestClass]
public sealed class MaxWidthLayoutTests
{
    private static LayoutEngine NewEngine() => new(new StyleEngine());

    private static BlockBox Layout(string html, Size viewport)
        => NewEngine().LayoutDocument(HtmlParser.Parse(html), viewport);

    [TestMethod]
    public void Max_width_clamps_a_block_below_the_available_width()
    {
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"max-width:200px\">x</div></body>",
            new Size(800, 600));

        var div = FindBox(root, "div")!;
        div.Frame.Width.Should().Be(200,
            "max-width clamps the auto-width down to 200px even though 800px is available");
    }

    [TestMethod]
    public void Max_width_none_lets_the_block_fill_its_container()
    {
        // `none` is the initial value; ResolveLength maps `none` to 0 and would
        // collapse the box if the clamp didn't intercept it.
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"max-width:none\">x</div></body>",
            new Size(800, 600));

        var div = FindBox(root, "div")!;
        div.Frame.Width.Should().Be(800,
            "max-width: none must be a no-op, not a collapse-to-zero");
    }

    [TestMethod]
    public void Min_width_expands_a_block_narrower_than_the_minimum()
    {
        // Both width and max-width clamp this below the min-width floor; the
        // floor must win.
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"width:100px; min-width:300px\">x</div></body>",
            new Size(800, 600));

        var div = FindBox(root, "div")!;
        div.Frame.Width.Should().Be(300,
            "min-width is the lower bound and overrides a smaller width");
    }

    [TestMethod]
    public void Max_width_in_em_resolves_to_pixels()
    {
        // 35em with the default 16px font-size = 560px. This is the
        // words.html case verbatim.
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"max-width:35em\">x</div></body>",
            new Size(1200, 600));

        var div = FindBox(root, "div")!;
        div.Frame.Width.Should().Be(560,
            "35em at default 16px/em resolves to 560px");
    }

    [TestMethod]
    public void Auto_margins_center_a_max_width_clamped_block()
    {
        // The words.html / MDN article-body pattern: width is auto, max-width
        // clamps below the container, margin: auto centers the leftover slack.
        // CSS 2.1 §10.4 extends §10.3.3's auto-margin distribution to this
        // clamp case.
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"max-width:200px; margin-left:auto; margin-right:auto\">x</div></body>",
            new Size(800, 600));

        var div = FindBox(root, "div")!;
        div.Frame.Width.Should().Be(200);
        // Slack = 800 - 200 = 600, split evenly into 300px on each side.
        div.Frame.X.Should().Be(300,
            "auto margins must distribute the 600px slack so the box centers at x=300");
    }

    [TestMethod]
    public void Single_auto_margin_absorbs_all_slack()
    {
        var root = Layout(
            "<body style=\"margin:0\"><div style=\"max-width:200px; margin-left:auto\">x</div></body>",
            new Size(800, 600));

        var div = FindBox(root, "div")!;
        div.Frame.Width.Should().Be(200);
        // One auto margin → it takes all the slack (600px), pinning the box
        // to the right edge.
        div.Frame.X.Should().Be(600,
            "a single auto margin absorbs all horizontal slack");
    }

    private static Box.Box? FindBox(Box.Box root, string localName)
    {
        if (root.Element?.LocalName == localName)
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var hit = FindBox(child, localName);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }
}
