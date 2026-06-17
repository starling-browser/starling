using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
namespace Starling.Layout.Tests;

/// <summary>
/// An <c>inline-block</c> establishes a new block formatting context (CSS 2.1
/// §10.1). When its children mix block and inline content, the block children
/// must lay out vertically as in any block container; the inline runs around
/// them are wrapped in anonymous blocks. Without this, legacy pages whose
/// table-cell-wrapped forms (Google's homepage) contain block children render
/// nothing for those children.
/// </summary>
[TestClass]
public sealed class InlineBlockBlockChildrenTests
{
    private static LayoutEngine NewEngine() => new(new StyleEngine());

    private static BlockBox Layout(string html, Size viewport)
        => NewEngine().LayoutDocument(HtmlParser.Parse(html), viewport);

    [TestMethod]
    public void Block_child_inside_inline_block_lays_out_and_inline_block_has_height()
    {
        // The inline-block <span> wraps a block <div> and an inline <span>.
        // Per §10.1, the inline-block forms a BFC, so the inner div stacks
        // above the inline anonymous block that holds "inline sibling".
        const string html = """
            <body><span style="display:inline-block">
              <div>block child</div>
              <span>inline sibling</span>
            </span></body>
            """;
        var root = Layout(html, new Size(800, 600));

        var inlineBlock = FindBox(root, "span");
        inlineBlock.Should().NotBeNull();
        inlineBlock!.Frame.Height.Should().BeGreaterThan(0,
            "the inline-block must size to contain the inner block + inline line");

        var innerDiv = FindBox(inlineBlock, "div");
        innerDiv.Should().NotBeNull("the block child must appear in the box tree");
        innerDiv!.Frame.Height.Should().BeGreaterThan(0);

        var allText = FlattenTextBoxes(inlineBlock).Select(tb => tb.Text).ToList();
        allText.Should().Contain(s => s.Contains("block child"));
        allText.Should().Contain(s => s.Contains("inline sibling"));
    }

    [TestMethod]
    public void Google_table_cell_lays_out_input_above_submit_button()
    {
        // Mirrors Google's homepage cell shape: a <td> (UA: inline-block)
        // containing a <div> wrapping a text input, followed by a submit
        // button. The text input must sit visually above the button.
        const string html = """
            <body><table><tr>
              <td>
                <div><input type="text" size="57" name="q"></div>
                <input type="submit" value="Search">
              </td>
            </tr></table></body>
            """;
        var root = Layout(html, new Size(1024, 768));

        var textInput = FindInputByType(root, "text");
        var submit = FindInputByType(root, "submit");

        textInput.Should().NotBeNull("the search box must be in the box tree");
        submit.Should().NotBeNull("the submit button must be in the box tree");

        textInput!.Frame.Width.Should().BeGreaterThan(0);
        textInput.Frame.Height.Should().BeGreaterThan(0);
        submit!.Frame.Width.Should().BeGreaterThan(0);
        submit.Frame.Height.Should().BeGreaterThan(0);

        // Vertical stacking: the input lives in a block <div>, the submit
        // sits in the anonymous block below. Document-space Y comparison.
        AbsoluteY(textInput).Should().BeLessThan(AbsoluteY(submit),
            "the text input should sit above the submit button inside the cell");
    }

    [TestMethod]
    public void Inline_block_with_only_text_still_uses_text_only_path()
    {
        // Regression guard: a pure-text inline-block must still size by its
        // measured text content, not by the BFC sub-pass.
        const string html = """
            <body><span style="display:inline-block">hello world</span></body>
            """;
        var root = Layout(html, new Size(800, 600));

        var inlineBlock = FindBox(root, "span")!;
        inlineBlock.Frame.Width.Should().BeGreaterThan(0);
        inlineBlock.Frame.Height.Should().BeGreaterThan(0);

        var text = FlattenTextBoxes(inlineBlock).Select(tb => tb.Text).ToList();
        text.Should().Contain(s => s.Contains("hello"));
    }

    [TestMethod]
    public void Inline_block_with_inline_block_child_renders_the_child()
    {
        // No block children — but an inline-block child. The text-only path
        // would silently drop the inner span; the inline-formatting-context
        // sub-pass must place it with a non-zero frame.
        const string html = """
            <body><span style="display:inline-block">
              <span style="display:inline-block;width:50px;height:20px;background:red">x</span>
            </span></body>
            """;
        var root = Layout(html, new Size(800, 600));

        var spans = FindAll(root, "span").ToList();
        spans.Should().HaveCountGreaterThanOrEqualTo(2);
        var outer = spans[0];
        var inner = spans[1];

        outer.Frame.Width.Should().BeGreaterThan(0,
            "the outer inline-block must size to include its child");
        outer.Frame.Height.Should().BeGreaterThan(0);
        inner.Frame.Width.Should().BeGreaterThan(0,
            "the inner inline-block must be placed inside the outer span");
        inner.Frame.Height.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void Inline_block_shrinks_to_max_content_width_not_available_width()
    {
        // The outer inline-block contains an inline-block sized to 100px.
        // Without shrink-to-fit, the outer would expand to fill the 1000px
        // div and push "SIBLING" onto a second line. With shrink-to-fit,
        // SIBLING sits next to the outer span on the same line.
        const string html = """
            <body><div style="width:1000px">
              <span style="display:inline-block"><span style="display:inline-block;width:100px;height:20px">a</span></span>SIBLING
            </div></body>
            """;
        var root = Layout(html, new Size(1200, 600));

        var outer = FindBox(root, "span")!;
        // The outer's content width should be roughly the inner's 100px
        // (plus a small whitespace allowance from text nodes around the
        // inner span). Crucially: it must NOT eat the whole 1000px row.
        outer.Frame.Width.Should().BeLessThan(500,
            "outer inline-block must shrink-to-fit, not consume the row");

        // Find the SIBLING text fragment and verify it landed on the same
        // visual line (same Y in the anonymous-block coordinate space) as
        // the outer inline-block. If it wrapped, its Y would be larger.
        var siblingFrag = FindTextFragment(root, "SIBLING");
        siblingFrag.Should().NotBeNull("the SIBLING text run must be placed");
        var outerLineY = outer.Frame.Y;
        siblingFrag!.Value.Y.Should().BeLessThan(outerLineY + outer.Frame.Height,
            "SIBLING must sit on the same line as the outer inline-block");
    }

    [TestMethod]
    public void Google_form_shape_keeps_cells_horizontal()
    {
        // Two <td>s: the first wraps an inline-block <div> with an <input>;
        // the second has a link. Both cells should sit side-by-side, not
        // stacked. The inline-block <div> must shrink-to-fit so the first
        // cell doesn't consume the entire row.
        const string html = """
            <body><table><tr>
              <td><div style="display:inline-block"><input type="text" size="57" name="q"></div></td>
              <td><a href="#">Advanced</a></td>
            </tr></table></body>
            """;
        var root = Layout(html, new Size(1280, 720));

        var cells = FindAll(root, "td").ToList();
        cells.Should().HaveCount(2);

        // Same row → same Y (within a tolerance for row-alignment slack).
        Math.Abs(cells[0].Frame.Y - cells[1].Frame.Y).Should().BeLessThan(2,
            "both cells should share a Y on the same row");

        // Second cell must sit to the right of the first.
        cells[1].Frame.X.Should().BeGreaterThan(cells[0].Frame.X,
            "the second cell should follow horizontally, not wrap below");

        // The input must actually be in the layout tree with a frame.
        var input = FindInputByType(root, "text");
        input.Should().NotBeNull();
        input!.Frame.Width.Should().BeGreaterThan(0);
        input.Frame.Height.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void Inline_block_with_block_child_shrinks_to_block_childs_content_max_content()
    {
        // The outer inline-block holds a single block child <div> with the
        // narrow text "short". Without a real max-content pass the block
        // child would size to the 1000px containing block and its frame
        // (background, borders) would paint across the row. After
        // shrink-to-fit the outer inline-block — and importantly its block
        // child's frame — should be ~text("short")-wide. SIBLING then sits
        // on the same line.
        const string html = """
            <body style="width:1000px">
              <span style="display:inline-block"><div>short</div></span>
              <span>SIBLING</span>
            </body>
            """;
        var root = Layout(html, new Size(1200, 600));

        var outer = FindBox(root, "span")!;
        outer.Frame.Width.Should().BeLessThan(200,
            "the outer inline-block must shrink to the block child's max-content width");
        outer.Frame.Width.Should().BeGreaterThan(0);

        // The inner <div>'s own frame should also be narrow — that's the
        // whole point of the second-pass re-layout at the shrunk width.
        var innerDiv = FindBox(outer, "div")!;
        innerDiv.Frame.Width.Should().BeLessThanOrEqualTo(outer.Frame.Width + 0.5,
            "the inner block child's frame must not exceed the shrunk inline-block width");

        // SIBLING must sit on the same line as the outer inline-block.
        var siblingFrag = FindTextFragment(root, "SIBLING");
        siblingFrag.Should().NotBeNull("the SIBLING text run must be placed");
        var outerLineBottom = outer.Frame.Y + outer.Frame.Height;
        siblingFrag!.Value.Y.Should().BeLessThan(outerLineBottom,
            "SIBLING must not wrap to a second line below the inline-block");
    }

    [TestMethod]
    public void Two_inline_blocks_each_with_block_child_fit_on_one_line_when_narrow()
    {
        // Two adjacent inline-blocks, each holding a block child with three
        // characters of text. Both should shrink-to-fit and sit on the same
        // line, with the second's X strictly greater than the first's.
        const string html = """
            <body style="width:1000px">
              <span style="display:inline-block"><div>aaa</div></span><span style="display:inline-block"><div>bbb</div></span>
            </body>
            """;
        var root = Layout(html, new Size(1200, 600));

        var spans = FindAll(root, "span").ToList();
        spans.Should().HaveCountGreaterThanOrEqualTo(2);
        var first = spans[0];
        var second = spans[1];

        Math.Abs(first.Frame.Y - second.Frame.Y).Should().BeLessThan(1,
            "both inline-blocks should share a Y on the same line");
        second.Frame.X.Should().BeGreaterThan(first.Frame.X,
            "the second inline-block must sit to the right of the first");
    }

    // ---------------------------------------------------------------- helpers

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

    private static TextFragment? FindTextFragment(Box.Box root, string contains)
    {
        if (root is TextBox tb)
        {
            foreach (var frag in tb.Fragments)
            {
                if (frag.Text.Contains(contains, StringComparison.Ordinal))
                {
                    return frag;
                }
            }
        }
        foreach (var child in root.Children)
        {
            var hit = FindTextFragment(child, contains);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
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

    private static Box.Box? FindInputByType(Box.Box root, string type)
    {
        if (root.Element?.LocalName == "input" &&
            string.Equals(root.Element.GetAttribute("type"), type, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var hit = FindInputByType(child, type);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }

    private static IEnumerable<TextBox> FlattenTextBoxes(Box.Box box)
    {
        if (box is TextBox tb) { yield return tb; yield break; }
        foreach (var child in box.Children)
        {
            foreach (var inner in FlattenTextBoxes(child))
            {
                yield return inner;
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
