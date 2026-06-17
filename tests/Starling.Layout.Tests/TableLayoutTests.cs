using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
namespace Starling.Layout.Tests;

/// <summary>
/// Tests for the placeholder table layout (UA stylesheet maps cells to
/// inline-block so a single row's cells flow horizontally). This is a
/// stop-gap until a real CSS table formatting context lands; once that
/// exists, these tests should be replaced with proper table-layout tests.
/// </summary>
[TestClass]
public sealed class TableLayoutTests
{
    private static LayoutEngine NewEngine() => new(new StyleEngine());

    private static BlockBox Layout(string html, Size viewport)
        => NewEngine().LayoutDocument(HtmlParser.Parse(html), viewport);

    [TestMethod]
    public void Cells_in_a_single_row_flow_horizontally()
    {
        var root = Layout(
            "<body><table><tr><td>A</td><td>B</td><td>C</td></tr></table></body>",
            new Size(800, 600));

        var cells = FindAll(root, "td").ToList();
        cells.Should().HaveCount(3);

        // All cells should share the same Y (single row, top-aligned).
        var firstY = cells[0].Frame.Y;
        cells.All(c => Math.Abs(c.Frame.Y - firstY) < 0.5).Should().BeTrue(
            "all cells on the same row should share a Y position");

        // X should monotonically increase: A is left of B is left of C.
        cells[0].Frame.X.Should().BeLessThan(cells[1].Frame.X);
        cells[1].Frame.X.Should().BeLessThan(cells[2].Frame.X);
    }

    [TestMethod]
    public void Rows_stack_vertically()
    {
        var root = Layout(
            "<body><table><tr><td>X</td></tr><tr><td>Y</td></tr></table></body>",
            new Size(800, 600));

        var rows = FindAll(root, "tr").ToList();
        rows.Should().HaveCount(2);

        // Second row should sit below the first (Y monotonically increases).
        rows[1].Frame.Y.Should().BeGreaterThan(rows[0].Frame.Y);

        // The single cell in each row should land on a distinct Y (in the
        // document-space sense — cell.Frame.Y is in its row's content-box,
        // but combined with the row's distinct Y origins, the two cells are
        // on different visual lines).
        var cells = FindAll(root, "td").ToList();
        cells.Should().HaveCount(2);

        var firstAbs = AbsoluteY(cells[0]);
        var secondAbs = AbsoluteY(cells[1]);
        secondAbs.Should().BeGreaterThan(firstAbs);
    }

    [TestMethod]
    public void Table_is_block_level_and_stacks_after_siblings()
    {
        // A <div> followed by a <table> followed by a <div>: the table should
        // sit between them as a block-level element. This guards the
        // table/thead/tbody/tfoot/tr `display: block` part of the UA rules.
        var root = Layout(
            "<body><div>before</div><table><tr><td>x</td></tr></table><div>after</div></body>",
            new Size(800, 600));

        var body = FindBox(root, "body")!;
        var topLevel = body.Children
            .Where(c => c.Element is not null &&
                        (c.Element.LocalName == "div" || c.Element.LocalName == "table"))
            .ToList();

        topLevel.Should().HaveCount(3);
        topLevel[0].Element!.LocalName.Should().Be("div");
        topLevel[1].Element!.LocalName.Should().Be("table");
        topLevel[2].Element!.LocalName.Should().Be("div");

        // Y positions monotonically increase: stacked vertically.
        topLevel[0].Frame.Y.Should().BeLessThan(topLevel[1].Frame.Y);
        topLevel[1].Frame.Y.Should().BeLessThan(topLevel[2].Frame.Y);
    }

    // ---------------------------------------------------------------- helpers

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

    private static IEnumerable<Box.Box> FindAll(Box.Box root, string localName)
    {
        if (root.Element?.LocalName == localName)
        {
            yield return root;
        }

        foreach (var child in root.Children)
        {
            foreach (var hit in FindAll(child, localName))
            {
                yield return hit;
            }
        }
    }

    /// <summary>Sum each ancestor's <see cref="Box.Box.Frame"/> Y to get a
    /// document-space Y coordinate.</summary>
    private static double AbsoluteY(Box.Box box)
    {
        var y = 0d;
        for (var b = box; b is not null; b = b.Parent)
        {
            y += b.Frame.Y;
        }

        return y;
    }
}
