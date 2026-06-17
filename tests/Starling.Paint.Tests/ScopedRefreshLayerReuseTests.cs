using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Dom;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Box;
using Starling.Layout.Incremental;
using Starling.Layout.Text;
using Starling.Paint.Compositor;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint.Tests;

/// <summary>
/// Scoped-relayout layer reuse (issue #82 item 1). The live shell presents a
/// frame whose mutations were confined to an already-promoted subtree by
/// refreshing the cached layer tree (<see cref="LayerTreeBuilder.RefreshAnimating"/>)
/// instead of rebuilding it. That is sound because (a) incremental layout keeps
/// the SAME box objects for a text edit / child splice — pinned here — and
/// (b) the refresh rebuilds exactly the promoted layers from those live boxes
/// while reusing every static layer's slice object untouched. Also pins why
/// attribute mutations are rejected from the scoped path: they splice a NEW
/// box, which would leave the cached layer's source box detached.
/// </summary>
[TestClass]
public sealed class ScopedRefreshLayerReuseTests
{
    private const string PageHtml =
        "<body style=\"margin:0\">" +
        "<div id=base style=\"width:240px;height:200px;background-color:#dde3ff\">base content</div>" +
        "<div id=status style=\"position:absolute;left:0;top:170px;width:240px;height:20px\">running 16 ms</div>" +
        "</body>";

    private static (Document Doc, LayoutSession Session, BlockBox Root, Element Status) LayOut()
    {
        var doc = HtmlParser.Parse(PageHtml);
        doc.RecordLayoutMutations = true;
        var session = new LayoutSession(new StyleEngine());
        var root = session.Layout(doc, new Size(240, 400), DefaultTextMeasurer.Instance, nowMs: 0);
        return (doc, session, root, doc.GetElementById("status")!);
    }

    private static Box? FindBoxFor(Box box, Element el)
    {
        if (ReferenceEquals(box.Element, el))
        {
            return box;
        }

        foreach (var child in box.Children)
        {
            if (FindBoxFor(child, el) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static bool SliceMentions(PaintList slice, string needle)
    {
        foreach (var item in slice.Items)
        {
            if (item is Starling.Paint.DisplayList.DrawText t && t.Text.Contains(needle, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    [TestMethod]
    public void Text_mutation_keeps_the_promoted_box_object_and_its_frame()
    {
        // The invariant the scoped present's identity/geometry guard relies on:
        // a TextContent write (remove + insert of the text node) reconciles as a
        // child splice under the SAME element box, leaving its frame unchanged.
        var (doc, session, root, status) = LayOut();
        var before = FindBoxFor(root, status)!;
        var frame = before.Frame;

        status.TextContent = "running 32 ms";
        var root2 = session.Layout(doc, new Size(240, 400), DefaultTextMeasurer.Instance, nowMs: 16);

        ReferenceEquals(root2, root).Should().BeTrue("incremental reconcile retains the root box");
        var after = FindBoxFor(root2, status)!;
        ReferenceEquals(after, before).Should().BeTrue("a text-only change reuses the element's box object");
        after.Frame.Should().Be(frame, "the fixed-size status line keeps its page-space frame");
    }

    [TestMethod]
    public void Attribute_mutation_replaces_the_promoted_box_object()
    {
        // Why LayoutRelevantAttr is rejected from the scoped path: the
        // reconciler rebuilds and splices a NEW box for the element, so the
        // cached layer's SourceBox would be a detached stale subtree.
        var (doc, session, root, status) = LayOut();
        var before = FindBoxFor(root, status)!;

        status.SetAttribute("style", "position:absolute;left:0;top:170px;width:240px;height:20px;color:#333");
        var root2 = session.Layout(doc, new Size(240, 400), DefaultTextMeasurer.Instance, nowMs: 16);

        var after = FindBoxFor(root2, status)!;
        ReferenceEquals(after, before).Should().BeFalse(
            "an attribute change splices a rebuilt box — the scoped present must reject it");
    }

    [TestMethod]
    public void Peek_exposes_the_pending_batch_without_draining_it()
    {
        var (doc, _, _, status) = LayOut();

        status.TextContent = "running 32 ms";
        var peeked = doc.PeekLayoutMutations();
        peeked.Count.Should().BeGreaterThan(0, "the write recorded layout mutations");
        foreach (var m in peeked)
        {
            // textContent is a remove + insert of the text node — the kinds the
            // scoped present accepts (the element's own box survives the splice).
            m.Kind.Should().BeOneOf(LayoutChangeKind.ChildRemoved, LayoutChangeKind.ChildInserted);
        }

        doc.DrainLayoutMutations().Count.Should().BeGreaterThan(0, "peeking must not drain the batch");
        doc.PeekLayoutMutations().Count.Should().Be(0, "the drain empties what peek exposed");
    }

    [TestMethod]
    public void Refresh_rebuilds_only_the_promoted_layer_and_reuses_static_slices()
    {
        var (doc, session, root, status) = LayOut();

        bool Promote(Box box) => box.Element is { } el && ReferenceEquals(el, status);
        var tree1 = new LayerTreeBuilder(isAnimatingLayerRoot: Promote).Build(root);
        tree1.Children.Should().HaveCount(1, "the status line is promoted to its own layer");
        var statusLayer1 = tree1.Children[0];
        SliceMentions(statusLayer1.Items, "16").Should().BeTrue();

        // The scoped frame: text mutates inside the promoted subtree only.
        status.TextContent = "running 32 ms";
        var root2 = session.Layout(doc, new Size(240, 400), DefaultTextMeasurer.Instance, nowMs: 16);
        ReferenceEquals(root2, root).Should().BeTrue();

        var tree2 = new LayerTreeBuilder(isAnimatingLayerRoot: Promote).RefreshAnimating(tree1);

        // The promoted layer was rebuilt from its (in-place updated) box…
        var statusLayer2 = tree2.Children[0];
        ReferenceEquals(statusLayer2, statusLayer1).Should().BeFalse("the mutated layer must rebuild");
        SliceMentions(statusLayer2.Items, "32").Should().BeTrue("the rebuilt slice carries the new text");

        // …while the base layer's slice object — and therefore its content
        // hash and cached tiles — was reused without a rebuild.
        ReferenceEquals(tree2.Items, tree1.Items).Should().BeTrue(
            "a static layer's display-list slice is reused by reference, not rebuilt");
        tree2.ContentHash.Should().Be(tree1.ContentHash);
    }
}
