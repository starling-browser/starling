// SPDX-License-Identifier: Apache-2.0
using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Dom;
using Starling.Html;
using Starling.Layout.Incremental;
using Starling.Layout.Scroll;
using Starling.Layout.Text;

namespace Starling.Layout.Tests.Position;

/// <summary>
/// Regression: the position pass re-translates every relative/sticky box on
/// every pass. A box deeper than one level inside a clean reused subtree keeps
/// the previous pass's already-shifted frame, so the shift compounded — a
/// <c>position:relative; top:550px</c> box drifted y=550 → 1100 → 1650 across
/// incremental passes that touched only an unrelated sibling, dragging the
/// root scroll extent with it. The shift must be idempotent (browser-plan/
/// scroll-model.md WP5, part 1: the scoped scroll measurement leans on frame
/// stability).
/// </summary>
[TestClass]
public sealed class RelativeStickyDriftTests
{
    private static readonly Size Viewport = new(800, 600);

    private static (Document Doc, LayoutSession Session, ScrollStateStore Store) StartSession(string html)
    {
        var doc = HtmlParser.Parse(html);
        doc.RecordLayoutMutations = true;
        var store = new ScrollStateStore();
        var session = new LayoutSession(new StyleEngine()) { ScrollState = store };
        session.Layout(doc, Viewport, DefaultTextMeasurer.Instance, nowMs: null);
        return (doc, session, store);
    }

    /// <summary>Store-less session: sticky stays on the clamped-relative
    /// fallback path (no scroll model), which is the seam under test.</summary>
    private static (Document Doc, LayoutSession Session) StartSessionNoStore(string html)
    {
        var doc = HtmlParser.Parse(html);
        doc.RecordLayoutMutations = true;
        var session = new LayoutSession(new StyleEngine());
        session.Layout(doc, Viewport, DefaultTextMeasurer.Instance, nowMs: null);
        return (doc, session);
    }

    private static Box.Box FrameOwner(LayoutSession session, Document doc, string id)
    {
        var el = doc.GetElementById(id);
        el.Should().NotBeNull();
        var box = Find(session.Root!, el!);
        box.Should().NotBeNull($"#{id} should produce a box");
        return box!;
    }

    private static Box.Box? Find(Box.Box box, Element el)
    {
        if (ReferenceEquals(box.Element, el))
        {
            return box;
        }

        foreach (var child in box.Children)
        {
            if (Find(child, el) is { } hit)
            {
                return hit;
            }
        }

        return null;
    }

    [TestMethod]
    public void Relative_box_inside_reused_subtree_does_not_drift_across_incremental_passes()
    {
        // The mutated sibling sits BELOW the reused subtree so the passes
        // change nothing about #outer's document position — any movement of
        // #rel (or of the root scroll extent) is the compounding shift.
        var (doc, session, store) = StartSession("""
            <body style="margin:0">
              <div id=outer>
                <div id=mid>
                  <div id=rel style="position:relative;top:550px;height:30px"></div>
                </div>
              </div>
              <div id=mut style="height:50px"></div>
            </body>
            """);

        var rel = FrameOwner(session, doc, "rel");
        var firstFrame = rel.Frame;
        firstFrame.Y.Should().Be(550, "the relative shift applies once on the first pass");
        var firstExtent = store.Root.OverflowHeight;

        // Three incremental passes that each touch ONLY the unrelated sibling.
        // #outer's clean subtree is reused in place, so #rel (two levels deep)
        // keeps its already-shifted frame — the pass must not shift it again.
        for (var height = 60; height <= 80; height += 10)
        {
            doc.GetElementById("mut")!.SetAttribute("style", $"height:{height}px");
            session.Layout(doc, Viewport, DefaultTextMeasurer.Instance, nowMs: null);

            FrameOwner(session, doc, "rel").Frame.Should().Be(firstFrame,
                "an incremental pass touching an unrelated sibling must leave the relative box's frame byte-stable");
            store.Root.OverflowHeight.Should().Be(firstExtent,
                "a compounding shift drags the root scroll extent with it");
        }
    }

    [TestMethod]
    public void Sticky_fallback_box_inside_reused_subtree_does_not_drift()
    {
        // The clamped-relative sticky fallback (no scrolling ancestor model in
        // a one-shot layout, here: violated `top` inset relative to the CB)
        // goes through the same shift seam, so it must be idempotent too.
        var (doc, session) = StartSessionNoStore("""
            <body style="margin:0">
              <div id=mut style="height:50px"></div>
              <div id=outer style="padding-top:0px">
                <div id=mid>
                  <div style="height:0px"></div>
                  <div id=st style="position:sticky;top:40px;height:30px"></div>
                  <div style="height:200px"></div>
                </div>
              </div>
            </body>
            """);

        var initial = FrameOwner(session, doc, "st").Frame;

        for (var height = 60; height <= 80; height += 10)
        {
            doc.GetElementById("mut")!.SetAttribute("style", $"height:{height}px");
            session.Layout(doc, Viewport, DefaultTextMeasurer.Instance, nowMs: null);

            FrameOwner(session, doc, "st").Frame.Should().Be(initial,
                "the sticky fallback shift must not compound across incremental passes");
        }
    }

    [TestMethod]
    public void Relayout_that_moves_the_relative_box_rebases_the_shift_on_the_fresh_frame()
    {
        // Idempotency must not freeze the box: when the pass actually re-lays
        // the relative box's subtree (content above it grows), the shift
        // recomputes from the NEW natural frame, not the stale recorded one.
        var (doc, session, _) = StartSession("""
            <body style="margin:0">
              <div id=outer>
                <div id=mid>
                  <div id=grow style="height:10px"></div>
                  <div id=rel style="position:relative;top:50px;height:30px"></div>
                </div>
              </div>
            </body>
            """);

        FrameOwner(session, doc, "rel").Frame.Y.Should().Be(60, "10 natural + 50 shift");

        doc.GetElementById("grow")!.SetAttribute("style", "height:25px");
        session.Layout(doc, Viewport, DefaultTextMeasurer.Instance, nowMs: null);

        FrameOwner(session, doc, "rel").Frame.Y.Should().Be(75,
            "25 natural + 50 shift — the fresh natural frame is the new basis");
    }

    [TestMethod]
    public void Growth_equal_to_the_shift_amount_does_not_collide_with_the_stale_basis()
    {
        // The sentinel-collision case: content above a top:50px relative box
        // grows by EXACTLY the shift amount, so the fresh natural frame
        // (y=60) equals the previous pass's shifted frame (10 + 50). The
        // seam-cleared bookkeeping must treat it as a fresh basis: y must be
        // 110 (60 natural + 50 shift), not 60 re-based on the stale origin.
        var (doc, session, _) = StartSession("""
            <body style="margin:0"><div id=mid>
              <div id=grow style="height:10px"></div>
              <div id=rel style="position:relative;top:50px;height:30px"></div>
            </div></body>
            """);
        FrameOwner(session, doc, "rel").Frame.Y.Should().Be(60, "10 natural + 50 shift");

        doc.GetElementById("grow")!.SetAttribute("style", "height:60px");
        session.Layout(doc, Viewport, DefaultTextMeasurer.Instance, nowMs: null);

        FrameOwner(session, doc, "rel").Frame.Y.Should().Be(110,
            "60 natural + 50 shift — a natural frame that coincides with the " +
            "old shifted frame must still be recognized as freshly placed");
    }
}
