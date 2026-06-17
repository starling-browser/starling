using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Common.Diagnostics;
using Starling.Css.Cascade;
using Starling.Dom;
using Starling.Layout.Block;
using Starling.Layout.Box;
using Starling.Layout.Position;
using Starling.Layout.Text;
using Starling.Layout.Tree;

namespace Starling.Layout.Incremental;

/// <summary>
/// A persistent layout context that lays a document out once and then, on each
/// later frame, recomputes only the subtrees a mutation touched — the core of
/// incremental layout (see the incremental-layout plan §3, §5–6).
/// </summary>
/// <remarks>
/// <para>The session retains the box tree and an element/text-node → box map
/// across frames. Each frame it drains the document's
/// <see cref="Document.DrainLayoutMutations">layout-mutation batch</see>,
/// marks the changed subtrees (and any element with an in-flight
/// animation/transition, whose style changes off the clock) dirty along the
/// root-to-change path, then re-runs the block pass. Clean subtrees with an
/// unchanged constraint space are reused in place — repositioned in O(1)
/// because every <see cref="Box.Box.Frame"/> is parent-relative, never
/// recomputed or re-shaped.</para>
/// <para>Soundness comes from the reuse key: a box is reused only when it is
/// not on a dirty path <em>and</em> its constraint space is unchanged. Anything
/// the reconciler cannot prove safe — a structural change, a display-level flip,
/// a viewport resize, an unmapped target — falls back to a full rebuild, which
/// is always correct. The dual-run verifier (<c>LayoutVerifier</c>)
/// guards the whole thing: incremental output is checked against a full rebuild.</para>
/// </remarks>
public sealed class LayoutSession
{
    private readonly StyleEngine _style;
    private readonly IImageResolver _images;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;

    // Persistent across frames: the retained tree and its lookup maps.
    private readonly Dictionary<Element, Box.Box> _elementMap = new();
    private readonly Dictionary<Starling.Dom.Text, TextBox> _textMap = new();
    private BlockBox? _root;
    private Size _viewport;

    // Scroll-measurement scoping (browser-plan/scroll-model.md WP1 follow-up).
    // The sink collects the scroll containers a relayout actually re-laid (via
    // BlockLayout.NoteRelaid); the scratch list audits store entries against
    // the live tree. Both are reused across frames — no per-frame allocation.
    private readonly List<Box.Box> _relaidScrollers = [];
    private readonly List<Element> _scrollEntryScratch = [];

    // True while the per-box scroll-extent caches mirror the retained tree:
    // set after any pass that measured with a store attached, cleared by a
    // pass that relaid without one (its invalidation hooks were off). While
    // false, the next measured pass runs the full, cache-distrusting measure.
    private bool _scrollCachesCoherent;

    public LayoutSession(StyleEngine style, IImageResolver? images = null, ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        _style = style;
        _images = images ?? NullImageResolver.Instance;
        _loggerFactory = loggerFactory ?? NullLoggerFactory.Instance;
        _log = _loggerFactory.CreateLogger<LayoutSession>();
    }

    /// <summary>The retained box tree's root, or null before the first layout.</summary>
    public BlockBox? Root => _root;

    /// <summary>
    /// When set, every incremental relayout is checked against a full rebuild of
    /// the same document and the first geometry divergence is logged — the plan's
    /// incremental-vs-full harness flip (§2g). Doubles layout cost, so it is a
    /// debug/CI safety net; defaults to the <c>STARLING_LAYOUT_VERIFY</c> switch.
    /// </summary>
    public bool VerifyAgainstFullRebuild { get; init; } = Verification.LayoutVerifier.Enabled;

    /// <summary>
    /// Optional per-document scroll store (browser-plan/scroll-model.md WP1).
    /// Same contract as <see cref="LayoutEngine.ScrollState"/>: each
    /// <see cref="Layout"/> call runs under the store's layout gate, then
    /// measures scrollports + scrollable overflow and re-clamps stored
    /// offsets. The offset itself never enters <see cref="ConstraintSpace"/>
    /// or any other reuse key — scrolling must not dirty layout.
    /// </summary>
    public Scroll.ScrollStateStore? ScrollState { get; set; }

    /// <summary>
    /// Lay <paramref name="document"/> out at <paramref name="viewport"/>,
    /// reusing the retained tree where the mutation batch lets us. The first
    /// call (and any call the reconciler can't handle incrementally) does a full
    /// rebuild. Returns the laid-out root.
    /// </summary>
    public BlockBox Layout(
        Document document, Size viewport, ITextMeasurer measurer, double? nowMs, CancellationToken abort = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(measurer);

        var batch = document.DrainLayoutMutations();

        var scroll = ScrollState;
        scroll?.BeginLayoutPass();
        try
        {
            ResetScrollQueue(); // an aborted earlier pass must not leak queue flags
            // Pessimistically mark the caches incoherent for the duration of
            // the pass: if it throws (navigation supersede, Stop abort) after
            // stamping some scrollers, their queue entries are gone and a
            // later scoped pass would never re-measure them. The success
            // paths below re-assert coherence.
            var cachesWereCoherent = _scrollCachesCoherent;
            _scrollCachesCoherent = false;

            if (_root is not null && viewport == _viewport && TryReconcile(batch, nowMs))
            {
                // Scoped scroll measurement needs the extent caches to mirror
                // the tree, which holds only if every relayout since the last
                // full measure ran with the invalidation hooks (sink) on.
                var scoped = scroll is not null && cachesWereCoherent;
                using (StarlingTelemetry.Span("layout", "incremental.relayout"))
                {
                    RunLayout(measurer, viewport, abort, incremental: true,
                        scrollSink: scoped ? _relaidScrollers : null);
                }

                StarlingTelemetry.Counter("layout.incremental.relayout", 1);
                if (VerifyAgainstFullRebuild)
                {
                    Verify(document, viewport, measurer, nowMs, abort);
                }

                if (scoped)
                {
                    MeasureScrollScoped(scroll!, viewport);
                }
                else
                {
                    MeasureScroll(scroll, viewport);
                }

                _scrollCachesCoherent = scroll is not null;
                return _root;
            }

            using (StarlingTelemetry.Span("layout", "incremental.full_rebuild"))
            {
                FullBuild(document, viewport, measurer, nowMs, abort);
            }

            StarlingTelemetry.Counter("layout.incremental.full_rebuild", 1);
            MeasureScroll(scroll, viewport);
            _scrollCachesCoherent = scroll is not null;
            return _root!;
        }
        finally
        {
            scroll?.EndLayoutPass();
        }
    }

    /// <summary>Full post-layout scroll measurement + offset re-clamp — see
    /// <see cref="ScrollState"/>. Distrusts and rewrites the per-box extent
    /// caches, so it is the path that (re)establishes cache coherence. No-op
    /// without a store.</summary>
    private void MeasureScroll(Scroll.ScrollStateStore? scroll, Size viewport)
    {
        if (scroll is null)
        {
            return;
        }

        Scroll.ScrollOverflowMeasurer.Measure(_root!, viewport, scroll);
        scroll.ReconcileAfterLayout();
    }

    /// <summary>
    /// Scoped post-layout scroll measurement for an incremental relayout: the
    /// document extent reconciles from the per-box extent caches (only relaid
    /// chains recompute), and only the scroll containers the pass actually
    /// re-laid — the sink <c>BlockLayout.NoteRelaid</c> filled — re-record
    /// their geometry. An untouched scroller's entry is left exactly as the
    /// last pass measured it, which is still exact: a subtree the pass neither
    /// laid nor moved measures the same. This is what keeps a per-frame
    /// animation tick from paying the full O(n) measure walk every frame.
    /// </summary>
    private void MeasureScrollScoped(Scroll.ScrollStateStore scroll, Size viewport)
    {
        Scroll.ScrollOverflowMeasurer.MeasureScoped(_root!, viewport, scroll, _relaidScrollers);
        _relaidScrollers.Clear(); // queue flags were cleared box-by-box above

        // The relaid queue cannot vouch for every entry. Audit each one
        // against the live tree: drop entries whose element left the tree or
        // stopped producing a scroll container (the scoped equivalent of the
        // full path's generation drop), and re-measure scrollers the stamp
        // seams cannot see (inline-formatting content re-lays without
        // stamping). Entry counts are small — the page's scroller count.
        scroll.CollectEntryElements(_scrollEntryScratch);
        foreach (var el in _scrollEntryScratch)
        {
            if (!_elementMap.TryGetValue(el, out var box)
                || !IsAttached(box)
                || (Scroll.ScrollOverflowMeasurer.Classify(box) & Scroll.ScrollBoxFlags.ScrollContainer) == 0)
            {
                scroll.RemoveEntry(el);
            }
            else if (!Scroll.ScrollOverflowMeasurer.IsStampReachable(box))
            {
                Scroll.ScrollOverflowMeasurer.MeasureContainerUncached(box, scroll);
            }
        }

        scroll.ClampAllEntries();
    }

    /// <summary>True when <paramref name="box"/> still hangs off the retained
    /// root. Detached subtrees keep their parent pointers into the old slot,
    /// so the splice points null the replaced box's parent (see
    /// <see cref="RebuildAndSplice"/>) to make this check sound.</summary>
    private bool IsAttached(Box.Box box)
    {
        for (Box.Box? b = box; b is not null; b = b.Parent)
        {
            if (ReferenceEquals(b, _root))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Clear the relaid-scroller queue and its per-box flags. Runs at
    /// the top of every pass so an aborted pass cannot leave a box marked
    /// queued forever (which would silently skip its future re-measures).</summary>
    private void ResetScrollQueue()
    {
        foreach (var b in _relaidScrollers)
        {
            b.ScrollMeasureQueued = false;
        }

        _relaidScrollers.Clear();
    }

    /// <summary>
    /// Dual-run check (plan §2g): rebuild <paramref name="document"/> from
    /// scratch the always-correct way and compare it to the just-produced
    /// incremental tree, logging the first geometry divergence. This is how a
    /// stale-but-plausible reuse — the worst incremental-layout failure — is
    /// caught before it reaches a user.
    /// </summary>
    private void Verify(Document document, Size viewport, ITextMeasurer measurer, double? nowMs, CancellationToken abort)
    {
        using var _ = StarlingTelemetry.Span("layout", "incremental.verify");
        var reference = new BoxTreeBuilder(_style, _images, nowMs).Build(document);
        var block = new BlockLayout(measurer, viewport, abort, incremental: false);
        block.Layout(reference);
        // Same sticky mode as the produced tree: with a store attached, sticky
        // frames stay natural on both sides (the reference pass re-records
        // identical constraints, a harmless overwrite); without one, both
        // sides apply the clamped-relative fallback.
        new PositionLayout(block, viewport, ScrollState).LayoutPositioned(reference);

        var divergence = Verification.LayoutVerifier.FindFirstDivergence(_root!, reference);
        if (divergence is { } d)
        {
            StarlingTelemetry.Counter("layout.incremental.divergent", 1);
            LayoutSessionLog.IncrementalDivergence(_log, d.ToString());
        }
        else
        {
            StarlingTelemetry.Counter("layout.incremental.verify_ok", 1);
        }
    }

    private void FullBuild(Document document, Size viewport, ITextMeasurer measurer, double? nowMs, CancellationToken abort)
    {
        _elementMap.Clear();
        _textMap.Clear();
        _viewport = viewport;

        var builder = new BoxTreeBuilder(_style, _images, nowMs, _elementMap, _textMap);
        _root = builder.Build(document);
        RunLayout(measurer, viewport, abort, incremental: true);
    }

    private void RunLayout(
        ITextMeasurer measurer, Size viewport, CancellationToken abort, bool incremental,
        List<Box.Box>? scrollSink = null)
    {
        var root = _root!;
        var block = new BlockLayout(measurer, viewport, abort, incremental) { RelaidScrollerSink = scrollSink };
        block.Layout(root);
        var positioning = new PositionLayout(block, viewport, ScrollState);
        positioning.LayoutPositioned(root);
    }

    /// <summary>
    /// Apply <paramref name="batch"/> to the retained tree, marking changed
    /// subtrees dirty. Returns false — asking the caller to fall back to a full
    /// rebuild — for any change the reconciler can't apply safely in place
    /// (structural change, display-level flip, unmapped target).
    /// </summary>
    private bool TryReconcile(IReadOnlyList<LayoutMutation> batch, double? nowMs)
    {
        foreach (var m in batch)
        {
            switch (m.Kind)
            {
                case LayoutChangeKind.TextChanged:
                    if (m.Target is not Starling.Dom.Text text
                        || !_textMap.TryGetValue(text, out var textBox))
                    {
                        return false;
                    }

                    textBox.Text = text.Data;
                    textBox.Fragments.Clear(); // force the inline pass to re-shape
                    MarkDirtyPath(textBox);
                    break;

                case LayoutChangeKind.LayoutRelevantAttr:
                    if (m.Target is not Element attrEl || !RebuildAndSplice(attrEl, nowMs))
                    {
                        return false;
                    }

                    break;

                // A child was inserted/removed under the target. Splice the one
                // changed child into the parent's box children, reusing the
                // unchanged siblings' laid-out subtrees, and re-bucket the parent's
                // inline run (plan §3a + §3b). Falls back to a full rebuild when a
                // :has()/:empty selector means the change could restyle outside the
                // parent, or when the parent isn't a mapped element.
                case LayoutChangeKind.ChildInserted:
                case LayoutChangeKind.ChildRemoved:
                    if (_style.StructuralChangeNeedsFullRebuild)
                    {
                        return false;
                    }

                    if (m.Target is not Element parent || !SpliceChildren(parent, nowMs))
                    {
                        return false;
                    }

                    break;
            }
        }

        // An animation or transition changes its target's computed style off the
        // clock, with no DOM mutation to record — so mark every active target
        // dirty and re-cascade it at the current frame time. (Both engines key on
        // Element; ones that produce no box, e.g. display:none, are skipped.)
        //
        // BUT only when the animated property can actually move geometry. A
        // transform / opacity / color / shadow animation is paint- or
        // composite-time: the painter samples it off the clock at paint time, so
        // the box tree is unchanged and a relayout would be pure waste. Skipping
        // it matters a lot on real pages: an animated element usually sits inside
        // a flex / grid container, and marking it dirty forces that whole
        // formatting context to re-lay (and re-measure all its text) every frame
        // — the animations demo, whose cards are flex items, was doing ~46 text
        // measures and ~9 ms of layout per frame for transform-only spins.
        if (nowMs is not null)
        {
            foreach (var el in _style.AnimationEngine.ActiveElements)
            {
                if (_style.AnimationEngine.HasLayoutAffectingProperty(el) && !MarkAnimated(el, nowMs))
                {
                    return false;
                }
            }

            foreach (var el in _style.TransitionEngine.ActiveElements)
            {
                if (_style.TransitionEngine.HasLayoutAffectingProperty(el) && !MarkAnimated(el, nowMs))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private bool SpliceChildren(Element parent, double? nowMs)
    {
        if (!_elementMap.TryGetValue(parent, out var parentBox))
        {
            return false;
        }

        var builder = new BoxTreeBuilder(_style, _images, nowMs, _elementMap, _textMap);
        if (!builder.SpliceChildren(parent, parentBox))
        {
            return false;
        }

        MarkDirtyPath(parentBox);
        return true;
    }

    private bool MarkAnimated(Element el, double? nowMs)
    {
        if (!_elementMap.TryGetValue(el, out _))
        {
            return true; // no box (display:none) — nothing to do
        }

        return RebuildAndSplice(el, nowMs);
    }

    /// <summary>
    /// Rebuild <paramref name="element"/>'s box subtree (re-cascading it at
    /// <paramref name="nowMs"/>) and splice it into the retained tree in place of
    /// its old box, then mark the path dirty. Returns false (forcing a full
    /// rebuild) if the element isn't mapped, is the root, changed box level, or
    /// no longer produces a single box — cases where the parent's structure
    /// would shift.
    /// </summary>
    private bool RebuildAndSplice(Element element, double? nowMs)
    {
        if (!_elementMap.TryGetValue(element, out var oldBox))
        {
            return false;
        }

        var slot = oldBox.Parent;
        if (slot is null)
        {
            return false; // the root element — full rebuild is simpler
        }

        var index = slot.Children.IndexOf(oldBox);
        if (index < 0)
        {
            return false;
        }

        // Reproduce the old box's block/inline level so the parent's anonymous
        // wrapping is unchanged; a level flip would re-bucket siblings.
        var blockifyParent = oldBox.Kind != BoxKind.Inline;
        var builder = new BoxTreeBuilder(_style, _images, nowMs, _elementMap, _textMap);
        var newBox = builder.RebuildElementSubtree(element, slot.Style, blockifyParent);
        if (newBox is null || newBox.Kind != oldBox.Kind)
        {
            return false;
        }

        newBox.Parent = slot;
        slot.Children[index] = newBox;
        oldBox.Parent = null; // detach, so IsAttached sees replaced subtrees as dead
        MarkDirtyPath(newBox);
        return true;
    }

    /// <summary>Mark <paramref name="box"/> and every ancestor up to the root as
    /// subtree-dirty, so the block pass recomputes the whole root-to-change path
    /// while reusing the clean subtrees that hang off it.</summary>
    private static void MarkDirtyPath(Box.Box box)
    {
        for (Box.Box? b = box; b is not null; b = b.Parent)
        {
            b.SubtreeDirty = true;
        }
    }
}

internal static partial class LayoutSessionLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "incremental layout diverged from full rebuild: {Divergence}")]
    public static partial void IncrementalDivergence(ILogger logger, string divergence);
}
