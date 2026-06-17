using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;

namespace Starling.Layout.Tests;

/// <summary>
/// CSS Grid §11.6 — when a grid container has a definite block size larger than
/// its content rows, the default <c>align-content: normal/stretch</c> distributes
/// the surplus across the (auto) rows so they fill the container. A single auto
/// row then spans the full height, and <c>align-items: center</c> can centre an
/// item vertically. Regression: an item stayed pinned to the top because its row
/// was only as tall as the item.
/// </summary>
[TestClass]
public sealed class GridAlignContentTests
{
    private static BlockBox Layout(string html, Size viewport)
        => new LayoutEngine(new StyleEngine()).LayoutDocument(HtmlParser.Parse(html), viewport);

    [TestMethod]
    public void Place_items_center_centers_item_in_both_axes_of_a_definite_height_grid()
    {
        var root = Layout("""
            <body><div id="g" style="display:grid; place-items:center; width:300px; height:200px">
              <div id="a" style="width:40px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var a = ById(root, "a")!;
        a.Frame.X.Should().BeApproximately(130, 0.5, "(300 - 40) / 2");
        a.Frame.Y.Should().BeApproximately(80, 0.5, "(200 - 40) / 2 — only works once the auto row stretches");
    }

    [TestMethod]
    public void Align_content_start_keeps_rows_content_sized()
    {
        // With an explicit non-stretch align-content the auto row is NOT grown, so
        // a top-aligned item stays at the top of the (content-sized) row.
        var root = Layout("""
            <body><div id="g" style="display:grid; align-content:start; justify-items:center; width:300px; height:200px">
              <div id="a" style="width:40px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var a = ById(root, "a")!;
        a.Frame.Y.Should().BeApproximately(0, 0.5);
    }

    private static Box.Box? ById(Box.Box root, string id)
        => FindBox(root, b => b.Element?.GetAttribute("id") == id);

    private static Box.Box? FindBox(Box.Box root, Func<Box.Box, bool> pred)
    {
        if (pred(root))
        {
            return root;
        }

        foreach (var c in root.Children)
        {
            var hit = FindBox(c, pred);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }
}
