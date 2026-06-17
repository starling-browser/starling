using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
namespace Starling.Layout.Tests.Position;

/// <summary>
/// Tests for <c>position: relative / absolute / fixed</c>. Frames are read
/// in document-space by walking up the box tree and summing parent
/// content-edge offsets — same trick the painter uses, since each
/// box's <c>Frame</c> is in its parent's content-box coordinate space.
/// </summary>
[TestClass]
public sealed class PositionLayoutTests
{
    private static LayoutEngine NewEngine() => new(new StyleEngine());

    private static BlockBox Layout(string html, Size viewport)
        => NewEngine().LayoutDocument(HtmlParser.Parse(html), viewport);

    [TestMethod]
    public void Position_relative_shifts_painted_box_by_left_and_top()
    {
        var root = Layout("""
            <body><div id="parent" style="width:400px; height:200px">
              <div id="a" style="width:100px; height:50px; position:relative; left:5px; top:10px"></div>
              <div id="b" style="width:100px; height:50px"></div>
            </div></body>
            """, new Size(800, 600));

        var a = ById(root, "a")!;
        var b = ById(root, "b")!;

        // Sibling 'b' is unaffected — it sits where 'a' would have been
        // before the shift (i.e. flow continues from a's pre-shift position).
        var aDoc = DocumentRectOf(a);
        var bDoc = DocumentRectOf(b);

        // a's pre-shift Y in document = body content top (which is body's
        // padding-box top, == 8 by default UA margin? Actually body sits at
        // (8,8) — the test only cares about the *delta* introduced by the
        // relative offset.
        // The relative shift adds (5, 10) to a's frame relative to parent.
        // So a.x.relativeToParent should be ~5, a.y.relativeToParent ~10.
        a.Frame.X.Should().BeApproximately(5, 0.5);
        a.Frame.Y.Should().BeApproximately(10, 0.5);

        // b sits where it would have without the shift — directly under
        // where 'a' was in normal flow, which is parent-content (0, 50).
        b.Frame.X.Should().BeApproximately(0, 0.5);
        b.Frame.Y.Should().BeApproximately(50, 0.5);
    }

    [TestMethod]
    public void Position_absolute_with_top_left_positions_against_nearest_positioned_ancestor()
    {
        // The parent is position:relative so it becomes the containing block.
        var root = Layout("""
            <body><div id="parent" style="position:relative; width:400px; height:200px; padding:0">
              <div id="a" style="position:absolute; top:20px; left:30px; width:50px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var a = ById(root, "a")!;
        var parent = ById(root, "parent")!;

        // a's frame is in parent's content-box coords; parent has no padding
        // or border, so absolute coords inside parent are simply (left, top).
        a.Frame.X.Should().BeApproximately(30, 0.5);
        a.Frame.Y.Should().BeApproximately(20, 0.5);
        a.Frame.Width.Should().BeApproximately(50, 0.5);
        a.Frame.Height.Should().BeApproximately(40, 0.5);

        _ = parent;
    }

    [TestMethod]
    public void Position_absolute_with_no_positioned_ancestor_uses_initial_containing_block()
    {
        // Wrapper is position:static; the absolute element should resolve
        // against the viewport (the ICB), not the wrapper.
        var root = Layout("""
            <body><div id="wrapper" style="margin:50px; padding:20px; width:200px; height:100px">
              <div id="a" style="position:absolute; top:7px; left:11px; width:50px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var a = ById(root, "a")!;
        // Document-space top-left should be (11, 7) regardless of nesting.
        var doc = DocumentRectOf(a);
        doc.X.Should().BeApproximately(11, 0.5);
        doc.Y.Should().BeApproximately(7, 0.5);
    }

    [TestMethod]
    public void Position_absolute_with_only_right_inset_aligns_right_edge_against_container()
    {
        var root = Layout("""
            <body><div id="parent" style="position:relative; width:400px; height:200px">
              <div id="a" style="position:absolute; right:30px; width:50px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var a = ById(root, "a")!;
        // parent content width = 400, right inset = 30, width = 50
        // → element's right edge = 400 - 30 = 370 → left = 320.
        a.Frame.X.Should().BeApproximately(320, 0.5);
        a.Frame.Width.Should().BeApproximately(50, 0.5);
    }

    [TestMethod]
    public void Position_absolute_with_both_left_and_right_and_auto_width_fills_the_gap()
    {
        var root = Layout("""
            <body><div id="parent" style="position:relative; width:400px; height:200px">
              <div id="a" style="position:absolute; left:30px; right:70px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var a = ById(root, "a")!;
        // width = 400 - 30 - 70 = 300.
        a.Frame.X.Should().BeApproximately(30, 0.5);
        a.Frame.Width.Should().BeApproximately(300, 0.5);
    }

    [TestMethod]
    public void Position_absolute_percentage_top_resolves_against_containing_block_height()
    {
        var root = Layout("""
            <body><div id="parent" style="position:relative; width:400px; height:200px">
              <div id="a" style="position:absolute; top:50%; left:0; width:50px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var a = ById(root, "a")!;
        // 50% of parent's 200px content height = 100.
        a.Frame.Y.Should().BeApproximately(100, 0.5);
    }

    [TestMethod]
    public void Position_fixed_positions_against_viewport_regardless_of_nesting()
    {
        var root = Layout("""
            <body><div id="outer" style="position:relative; margin:50px; padding:20px; width:300px; height:200px">
              <div id="middle" style="position:relative; padding:15px; width:200px; height:150px">
                <div id="a" style="position:fixed; top:0; left:0; width:80px; height:30px"></div>
              </div>
            </div></body>
            """, new Size(1000, 800));

        var a = ById(root, "a")!;
        var doc = DocumentRectOf(a);
        // Fixed → viewport (0,0).
        doc.X.Should().BeApproximately(0, 0.5);
        doc.Y.Should().BeApproximately(0, 0.5);
        a.Frame.Width.Should().BeApproximately(80, 0.5);
        a.Frame.Height.Should().BeApproximately(30, 0.5);
    }

    [TestMethod]
    public void Absolutely_positioned_sibling_is_removed_from_flow_so_subsequent_sibling_stacks_normally()
    {
        // Three children: in-flow 'a' (50 tall), absolute 'pos' (should not
        // advance the cursor), in-flow 'b' (should sit at y=50, not y=100).
        var root = Layout("""
            <body><div id="parent" style="position:relative; width:400px; height:300px">
              <div id="a" style="width:100px; height:50px"></div>
              <div id="pos" style="position:absolute; top:200px; left:200px; width:50px; height:40px"></div>
              <div id="b" style="width:100px; height:50px"></div>
            </div></body>
            """, new Size(800, 600));

        var aBox = ById(root, "a")!;
        var bBox = ById(root, "b")!;
        var pos = ById(root, "pos")!;

        aBox.Frame.Y.Should().BeApproximately(0, 0.5);
        bBox.Frame.Y.Should().BeApproximately(50, 0.5); // 'pos' didn't advance the cursor.
        pos.Frame.X.Should().BeApproximately(200, 0.5);
        pos.Frame.Y.Should().BeApproximately(200, 0.5);
    }

    [TestMethod]
    public void Two_absolute_siblings_position_independently_against_the_same_containing_block()
    {
        var root = Layout("""
            <body><div id="parent" style="position:relative; width:400px; height:300px">
              <div id="a" style="position:absolute; top:10px; left:10px; width:50px; height:40px"></div>
              <div id="b" style="position:absolute; top:80px; left:120px; width:60px; height:30px"></div>
            </div></body>
            """, new Size(800, 600));

        var a = ById(root, "a")!;
        var b = ById(root, "b")!;
        a.Frame.X.Should().BeApproximately(10, 0.5);
        a.Frame.Y.Should().BeApproximately(10, 0.5);
        b.Frame.X.Should().BeApproximately(120, 0.5);
        b.Frame.Y.Should().BeApproximately(80, 0.5);
    }

    [TestMethod]
    public void Z_index_is_read_into_positioned_props_even_though_paint_order_unchanged()
    {
        // The point of this test is to exercise the parser path so a future
        // regression that drops z-index parsing trips a test. We don't try
        // to assert paint order here — that lands in a follow-up.
        var root = Layout("""
            <body><div id="parent" style="position:relative; width:200px; height:100px">
              <div id="a" style="position:absolute; top:0; left:0; z-index:5; width:50px; height:40px"></div>
            </div></body>
            """, new Size(800, 600));

        var a = ById(root, "a")!;
        var props = Starling.Layout.Position.PositionParser.Parse(a.Style);
        props.ZIndex.Should().Be(5);
        props.Kind.Should().Be(Starling.Layout.Position.PositionKind.Absolute);
    }

    [TestMethod]
    public void Position_absolute_resolves_against_padding_box_of_positioned_ancestor()
    {
        // Parent has padding:20px; the abs child's (top:0; left:0) should
        // map to the parent's padding-box top-left, i.e. inside the border
        // edge of the parent — not inside the parent's content box.
        var root = Layout("""
            <body><div id="parent" style="position:relative; padding:20px; width:200px; height:100px">
              <div id="a" style="position:absolute; top:0; left:0; width:30px; height:20px"></div>
            </div></body>
            """, new Size(800, 600));

        var parent = ById(root, "parent")!;
        var a = ById(root, "a")!;
        var parentDoc = DocumentRectOf(parent);
        var aDoc = DocumentRectOf(a);

        // Padding-box top-left coincides with the parent's frame top-left
        // here (no border). So a's doc top-left = parent's doc top-left.
        aDoc.X.Should().BeApproximately(parentDoc.X, 0.5);
        aDoc.Y.Should().BeApproximately(parentDoc.Y, 0.5);
    }

    [TestMethod]
    public void Position_absolute_height_auto_resolves_from_top_and_bottom()
    {
        // CSS 2.1 §10.6.4 — with height:auto and BOTH top and bottom specified,
        // the used height fills the gap between the insets (mirroring how width
        // derives from left+right). Regression: an inset-sized overlay (e.g. a
        // glow with `inset: ...`) collapsed to its zero content height and never
        // painted.
        var root = Layout("""
            <body><div id="parent" style="position:relative; width:400px; height:300px; padding:0">
              <div id="a" style="position:absolute; top:40px; bottom:60px; left:0; width:100px"></div>
            </div></body>
            """, new Size(800, 600));

        var a = ById(root, "a")!;
        a.Frame.Height.Should().BeApproximately(200, 0.5, "300 - top(40) - bottom(60)");
        a.Frame.Y.Should().BeApproximately(40, 0.5);
    }

    [TestMethod]
    public void Position_absolute_explicit_height_ignores_bottom_inset()
    {
        // When height is explicit, bottom is over-constrained and ignored (the
        // box keeps its explicit height and is anchored by top).
        var root = Layout("""
            <body><div id="parent" style="position:relative; width:400px; height:300px; padding:0">
              <div id="a" style="position:absolute; top:40px; bottom:60px; left:0; width:100px; height:70px"></div>
            </div></body>
            """, new Size(800, 600));

        var a = ById(root, "a")!;
        a.Frame.Height.Should().BeApproximately(70, 0.5);
        a.Frame.Y.Should().BeApproximately(40, 0.5);
    }

    // ----- helpers ------------------------------------------------------

    private static Box.Box? ById(Box.Box root, string id)
        => FindBox(root, b => b.Element?.GetAttribute("id") == id);

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

    /// <summary>
    /// Resolve a box's <em>border-box</em> rect to document-space by walking
    /// up the box tree and summing each ancestor's content-box origin.
    /// Each box's <c>Frame</c> is in its parent's content-box
    /// coordinate space, so the content-box origin of a parent in document
    /// space is its own border-box origin + border-left + padding-left (and
    /// similarly for the y-axis).
    /// </summary>
    private static Rect DocumentRectOf(Box.Box box)
    {
        double x = box.Frame.X;
        double y = box.Frame.Y;
        var cur = box.Parent;
        while (cur is not null)
        {
            x += cur.Frame.X + cur.Border.Left + cur.Padding.Left;
            y += cur.Frame.Y + cur.Border.Top + cur.Padding.Top;
            cur = cur.Parent;
        }
        return new Rect(x, y, box.Frame.Width, box.Frame.Height);
    }
}
