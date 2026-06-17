using System.Diagnostics;
using Starling.Dom;

namespace Starling.Layout.Scroll;

/// <summary>
/// Read-side snapshot of one scroll container's state. Offsets are CSS px,
/// already clamped to the legal range. The scrollport is the padding-box size
/// from the last layout (the <c>clientWidth</c>/<c>clientHeight</c> source);
/// the overflow size is the scrollable overflow from the last layout (the
/// <c>scrollWidth</c>/<c>scrollHeight</c> source and the clamp bound).
/// </summary>
public readonly record struct ScrollState(
    double OffsetX,
    double OffsetY,
    double ScrollportWidth,
    double ScrollportHeight,
    double OverflowWidth,
    double OverflowHeight,
    bool PendingEvent)
{
    /// <summary>Largest legal <see cref="OffsetX"/> for the recorded geometry.</summary>
    public double MaxOffsetX => Math.Max(0, OverflowWidth - ScrollportWidth);

    /// <summary>Largest legal <see cref="OffsetY"/> for the recorded geometry.</summary>
    public double MaxOffsetY => Math.Max(0, OverflowHeight - ScrollportHeight);
}

/// <summary>
/// The one shared scroll store for a document (see
/// browser-plan/scroll-model.md). Keyed by <see cref="Element"/> because boxes
/// are rebuilt on every relayout while elements are stable — the same pattern
/// the script-animation store uses. Holds, per scroll container: the clamped
/// offset, the scrollport (padding box) and scrollable overflow from the last
/// layout, and a pending-event flag set on any offset change (drained once per
/// frame by the frame pump, WP4). One extra root entry models the document
/// scroller.
/// </summary>
/// <remarks>
/// <para><b>Scrolling never dirties layout.</b> The offset is paint-time and
/// hit-test-time state only. <see cref="Write"/> clamps, stores, and flags —
/// it flushes nothing. Geometry flows the other way: the layout engine
/// measures scrollport + overflow into the store after each pass, then
/// <see cref="ReconcileAfterLayout"/> re-clamps every entry against the fresh
/// geometry (a clamp that moves an offset flags the event like any other
/// scroll).</para>
/// <para><b>Layout gate.</b> The layout engine brackets each pass with
/// <see cref="BeginLayoutPass"/>/<see cref="EndLayoutPass"/>; offset writes
/// during a pass throw in DEBUG builds. The offset must never feed a layout
/// input (e.g. <c>ConstraintSpace</c>) — a write mid-pass is the loudest
/// symptom of that bug.</para>
/// <para>Not thread-safe; same single-threaded use as the layout tree it
/// mirrors.</para>
/// </remarks>
public sealed class ScrollStateStore
{
    private struct Entry
    {
        public double X, Y;             // clamped offset
        public double PortW, PortH;     // scrollport = padding box
        public double OverW, OverH;     // scrollable overflow
        public bool Pending;            // scroll event owed to this target
        public int Generation;          // last layout pass that measured this entry
    }

    private struct StickyEntry
    {
        public StickyConstraints C;
        public int Generation;      // last layout pass that recorded this entry
    }

    private readonly Dictionary<Element, Entry> _entries = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Element, StickyEntry> _sticky = new(ReferenceEqualityComparer.Instance);
    private readonly List<Element> _scratch = [];
    private Entry _root;
    private int _generation;
    private int _layoutPassDepth;
    private long _geometryRecords;

    // ---- Reads (bindings, paint, hit testing) -------------------------------

    /// <summary>Snapshot for <paramref name="element"/>'s scroll container, or
    /// false when the element is not a scroll container in the current layout
    /// (and holds no offset written since).</summary>
    public bool TryGet(Element element, out ScrollState state)
    {
        if (_entries.TryGetValue(element, out var e))
        {
            state = Snapshot(in e);
            return true;
        }
        state = default;
        return false;
    }

    /// <summary>The document scroller's entry. Scrollport = viewport,
    /// overflow = page extent, offset = root scroll position (synced from the
    /// shell's root scroller in v1).</summary>
    public ScrollState Root => Snapshot(in _root);

    /// <summary>Offset lookup shaped for the paint/hit-test scroll-offset
    /// funcs (<c>DisplayListBuilder.Build</c> et al.). (0,0) for non-scrollers.</summary>
    public (double X, double Y) GetOffset(Element element)
        => _entries.TryGetValue(element, out var e) ? (e.X, e.Y) : (0, 0);

    /// <summary>True when any sticky element recorded constraints in the last
    /// layout. Lets hot read paths (client rects) skip per-element shift
    /// lookups entirely on the overwhelmingly common sticky-free page.</summary>
    public bool HasStickyEntries => _sticky.Count > 0;

    /// <summary>The layout-time sticky constraints for <paramref name="element"/>,
    /// or false when the last layout recorded none (the element is not sticky,
    /// or no scroll model was attached to its layout).</summary>
    public bool TryGetStickyConstraints(Element element, out StickyConstraints constraints)
    {
        if (_sticky.TryGetValue(element, out var e))
        {
            constraints = e.C;
            return true;
        }
        constraints = default;
        return false;
    }

    /// <summary>
    /// The paint-time sticky shift for <paramref name="element"/> at the
    /// current scroll offsets — pure arithmetic over the recorded
    /// <see cref="StickyConstraints"/> and the bound scroller's offset, no
    /// layout (browser-plan/scroll-model.md). (0,0) for non-sticky elements.
    /// Shaped like <see cref="GetOffset"/> so paint/hit-test/rect paths can
    /// pre-bind it once. Runs per sticky box per scroll tick: two dictionary
    /// reads plus branches, nothing allocated.
    /// </summary>
    public (double X, double Y) GetStickyShift(Element element)
    {
        if (_sticky.Count == 0 || !_sticky.TryGetValue(element, out var e))
        {
            return (0, 0);
        }

        ref readonly var c = ref e.C;
        double offX, offY, portW, portH;
        if (c.Scroller is null)
        {
            offX = _root.X; offY = _root.Y; portW = _root.PortW; portH = _root.PortH;
        }
        else if (_entries.TryGetValue(c.Scroller, out var sc))
        {
            offX = sc.X; offY = sc.Y; portW = sc.PortW; portH = sc.PortH;
        }
        else
        {
            // The scroller's entry vanished mid-frame (it will reconcile on
            // the next layout). Treat as unscrolled rather than guessing.
            offX = 0; offY = 0; portW = 0; portH = 0;
        }

        return (ComputeAxisShift(c.NaturalX - offX, c.Width, c.Left, c.Right, portW,
                    c.NaturalX - c.CbX, c.CbRight - (c.NaturalX + c.Width)),
                ComputeAxisShift(c.NaturalY - offY, c.Height, c.Top, c.Bottom, portH,
                    c.NaturalY - c.CbY, c.CbBottom - (c.NaturalY + c.Height)));
    }

    /// <summary>
    /// One axis of the sticky shift. <paramref name="position"/> is the
    /// element's natural start edge in scrollport space (natural − offset).
    /// The doc's formula for <c>top: T</c>: when that position falls below
    /// <c>T</c>, shift down by the difference; mirrored for the end inset
    /// against the scrollport extent; and the result is clamped so the box
    /// never leaves its containing block — the available slack toward the end
    /// is <c>containingBlock.end − natural.end</c>, toward the start
    /// <c>natural.start − containingBlock.start</c>. When both insets are set
    /// and the scrollport is too small to honor both, the start inset is
    /// applied last and wins — the CSS over-constrained precedence
    /// (top/left beat bottom/right).
    /// </summary>
    private static double ComputeAxisShift(
        double position, double size, double? startInset, double? endInset,
        double portExtent, double slackToStart, double slackToEnd)
    {
        var target = position;
        if (endInset is { } e && target + size > portExtent - e)
        {
            target = portExtent - e - size;
        }

        if (startInset is { } st && target < st)
        {
            target = st;
        }

        var shift = target - position;
        if (shift == 0)
        {
            return 0;
        }

        // Containing-block clamp; a box larger than its CB has no slack.
        var lo = -Math.Max(0, slackToStart);
        var hi = Math.Max(0, slackToEnd);
        return Math.Clamp(shift, lo, hi);
    }

    /// <summary>True when any entry (or the root) owes a scroll event.</summary>
    public bool HasPendingEvents
    {
        get
        {
            if (_root.Pending)
            {
                return true;
            }

            foreach (var kv in _entries)
            {
                if (kv.Value.Pending)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Collect every element owing a scroll event into
    /// <paramref name="targets"/> (cleared first) and clear the flags;
    /// <paramref name="rootPending"/> reports (and clears) the document
    /// scroller's flag. The frame pump (WP4) drains this once per frame,
    /// before <c>requestAnimationFrame</c> callbacks.
    /// </summary>
    public void DrainPendingEventTargets(List<Element> targets, out bool rootPending)
    {
        ArgumentNullException.ThrowIfNull(targets);
        targets.Clear();
        rootPending = _root.Pending;
        _root.Pending = false;
        foreach (var kv in _entries)
        {
            if (kv.Value.Pending)
            {
                targets.Add(kv.Key);
            }
        }

        foreach (var el in targets)
        {
            var e = _entries[el];
            e.Pending = false;
            _entries[el] = e;
        }
    }

    // ---- Writes (input routing, bindings) -----------------------------------

    /// <summary>
    /// Set <paramref name="element"/>'s scroll offset. Clamps to the legal
    /// range for the last-measured geometry, stores, and sets the
    /// pending-event flag when the offset actually moved. Flushes nothing —
    /// callers that need fresh geometry first (the <c>scrollTop</c> setter)
    /// flush layout before writing. Writes to a box that is not a scroll
    /// container clamp to (0,0), matching CSSOM's no-op semantics.
    /// </summary>
    public void Write(Element element, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(element);
        AssertNotInLayoutPass();
        var existed = _entries.TryGetValue(element, out var e); // default entry: zero geometry
        // A box with no measured geometry clamps to (0,0); skip materializing
        // an all-zero entry for it (reconcile would drop it anyway).
        var moved = WriteEntry(ref e, x, y);
        if (moved)
        {
            OffsetVersion++;
        }

        if (moved || existed)
        {
            _entries[element] = e;
        }
    }

    /// <summary>Root-scroller variant of <see cref="Write"/>. The shells sync
    /// their root scroll position through here in v1.</summary>
    public void WriteRoot(double x, double y)
    {
        AssertNotInLayoutPass();
        if (WriteEntry(ref _root, x, y))
        {
            OffsetVersion++;
        }
    }

    /// <summary>
    /// Bumps every time any stored offset actually moves — script/input writes,
    /// post-layout re-clamps, and entry drops alike. The live compositor's
    /// scoped-refresh path compares this against the version captured when the
    /// cached layer tree was last built in full: layer slices bake the offsets
    /// in, so reusing a slice is only sound while no offset has moved.
    /// </summary>
    public int OffsetVersion { get; private set; }

    // ---- Layout-side surface (internal: the layout engine only) -------------

    /// <summary>Marks a layout pass in progress: external offset writes are
    /// illegal until the matching <see cref="EndLayoutPass"/> (DEBUG throw).
    /// Also opens a new measurement generation so <see cref="ReconcileAfterLayout"/>
    /// can drop entries the new layout no longer measures. The gate is a
    /// counter, so a layout pass nested inside another (e.g. a flush forced
    /// mid-pass) cannot reopen the outer pass's gate early.</summary>
    internal void BeginLayoutPass()
    {
        _layoutPassDepth++;
        _generation++;
    }

    /// <summary>Closes one nesting level of the layout gate; offset writes are
    /// legal again when the outermost pass ends. Always runs (finally) so an
    /// aborted layout cannot wedge the gate shut; an unbalanced call is
    /// clamped rather than letting the gate go negative.</summary>
    internal void EndLayoutPass()
    {
        if (_layoutPassDepth > 0)
        {
            _layoutPassDepth--;
        }
    }

    /// <summary>Record a scroll container's fresh geometry. Offsets and the
    /// pending flag are preserved; clamping waits for
    /// <see cref="ReconcileAfterLayout"/> so one pass clamps everything once.</summary>
    internal void RecordGeometry(Element element, double scrollportWidth, double scrollportHeight, double overflowWidth, double overflowHeight)
    {
        _geometryRecords++;
        _entries.TryGetValue(element, out var e);
        e.PortW = scrollportWidth;
        e.PortH = scrollportHeight;
        e.OverW = overflowWidth;
        e.OverH = overflowHeight;
        e.Generation = _generation;
        _entries[element] = e;
    }

    /// <summary>Record the document scroller's geometry (scrollport = viewport,
    /// overflow = page extent).</summary>
    internal void RecordRootGeometry(double scrollportWidth, double scrollportHeight, double overflowWidth, double overflowHeight)
    {
        _root.PortW = scrollportWidth;
        _root.PortH = scrollportHeight;
        _root.OverW = overflowWidth;
        _root.OverH = overflowHeight;
        _root.Generation = _generation;
    }

    /// <summary>Record one sticky element's layout-time constraints. Runs from
    /// the position pass, which walks the full tree every pass (full and
    /// incremental alike), so every live sticky element re-records each pass
    /// and stale entries are swept by generation in the reconcile steps.</summary>
    internal void RecordSticky(Element element, in StickyConstraints constraints)
    {
        _sticky[element] = new StickyEntry { C = constraints, Generation = _generation };
    }

    /// <summary>Drop sticky entries the pass that just ended did not re-record
    /// (the element left the tree or stopped being sticky). Shared by the full
    /// and scoped reconciles — unlike scroll geometry, sticky constraints are
    /// re-recorded by EVERY pass, so the generation test is exact for both.</summary>
    private void SweepStickyEntries()
    {
        if (_sticky.Count == 0)
        {
            return;
        }

        _scratch.Clear();
        foreach (var kv in _sticky)
        {
            if (kv.Value.Generation != _generation)
            {
                _scratch.Add(kv.Key);
            }
        }

        foreach (var el in _scratch)
        {
            _sticky.Remove(el);
        }

        _scratch.Clear();
    }

    /// <summary>
    /// Post-layout reconcile: re-clamp every entry against the geometry the
    /// pass just measured. When content shrank or a scrollport grew, the
    /// stored offset can exceed the new range — the clamp pulls it in and
    /// sets the pending-event flag like any other scroll. Entries the pass
    /// did not measure (the element stopped being a scroll container, or left
    /// the tree) are dropped; their next appearance starts at offset (0,0),
    /// matching engine behavior for display:none round-trips.
    /// </summary>
    internal void ReconcileAfterLayout()
    {
        SweepStickyEntries();
        if (ClampEntry(ref _root))
        {
            OffsetVersion++;
        }

        if (_entries.Count == 0)
        {
            return;
        }

        _scratch.Clear();
        foreach (var kv in _entries)
        {
            _scratch.Add(kv.Key);
        }

        foreach (var el in _scratch)
        {
            var e = _entries[el];
            if (e.Generation != _generation)
            {
                _entries.Remove(el);
                if (e.X != 0 || e.Y != 0)
                {
                    OffsetVersion++;
                }

                continue;
            }
            if (ClampEntry(ref e))
            {
                _entries[el] = e;
                OffsetVersion++;
            }
        }
        _scratch.Clear();
    }

    /// <summary>
    /// Scoped-pass reconcile (incremental relayout): re-clamp every entry and
    /// the root against their current geometry, with no generation-based
    /// dropping — a scoped pass deliberately re-measures only relaid scroll
    /// containers, so an unmeasured entry is fresh, not stale. The layout
    /// session drops dead entries itself (element detached or no longer a
    /// scroll container) before calling this.
    /// </summary>
    internal void ClampAllEntries()
    {
        SweepStickyEntries();
        if (ClampEntry(ref _root))
        {
            OffsetVersion++;
        }

        if (_entries.Count == 0)
        {
            return;
        }

        _scratch.Clear();
        foreach (var kv in _entries)
        {
            _scratch.Add(kv.Key);
        }

        foreach (var el in _scratch)
        {
            var e = _entries[el];
            if (ClampEntry(ref e))
            {
                _entries[el] = e;
                OffsetVersion++;
            }
        }
        _scratch.Clear();
    }

    /// <summary>Copy every entry's element into <paramref name="into"/>
    /// (cleared first) so the layout session can audit entries against the
    /// live box tree without iterating the dictionary it mutates.</summary>
    internal void CollectEntryElements(List<Element> into)
    {
        into.Clear();
        foreach (var kv in _entries)
        {
            into.Add(kv.Key);
        }
    }

    /// <summary>Drop one entry — the scoped-pass equivalent of the
    /// generation-mismatch drop in <see cref="ReconcileAfterLayout"/>.</summary>
    internal void RemoveEntry(Element element)
    {
        if (_entries.Remove(element, out var e) && (e.X != 0 || e.Y != 0))
        {
            OffsetVersion++; // a dropped entry reads as offset (0,0) from now on
        }
    }

    /// <summary>Total <see cref="RecordGeometry"/> calls over this store's
    /// lifetime — the observable that pins re-measurement scoping in tests
    /// (an untouched scroll container's entry is never re-recorded by an
    /// incremental relayout of a sibling).</summary>
    internal long GeometryRecords => _geometryRecords;

    // ---- Internals -----------------------------------------------------------

    private static ScrollState Snapshot(ref readonly Entry e)
        => new(e.X, e.Y, e.PortW, e.PortH, e.OverW, e.OverH, e.Pending);

    /// <summary>Clamp-and-store an offset write into <paramref name="e"/>.
    /// Returns true when the offset moved (flag set).</summary>
    private static bool WriteEntry(ref Entry e, double x, double y)
    {
        if (double.IsNaN(x))
        {
            x = 0;
        }

        if (double.IsNaN(y))
        {
            y = 0;
        }

        var nx = Math.Clamp(x, 0, Math.Max(0, e.OverW - e.PortW));
        var ny = Math.Clamp(y, 0, Math.Max(0, e.OverH - e.PortH));
        if (nx == e.X && ny == e.Y)
        {
            return false;
        }

        e.X = nx;
        e.Y = ny;
        e.Pending = true;
        return true;
    }

    /// <summary>Re-clamp <paramref name="e"/>'s offset against its own
    /// geometry. Returns true when the offset moved (flag set).</summary>
    private static bool ClampEntry(ref Entry e)
    {
        var nx = Math.Clamp(e.X, 0, Math.Max(0, e.OverW - e.PortW));
        var ny = Math.Clamp(e.Y, 0, Math.Max(0, e.OverH - e.PortH));
        if (nx == e.X && ny == e.Y)
        {
            return false;
        }

        e.X = nx;
        e.Y = ny;
        e.Pending = true;
        return true;
    }

    /// <summary>The scroll-model invariant, loud: nothing may write a scroll
    /// offset while layout is running — the offset is not a layout input, and
    /// a write mid-pass means some layout code is reading it back in (see the
    /// Risks section of browser-plan/scroll-model.md). Compiled out of release
    /// builds.</summary>
    [Conditional("DEBUG")]
    private void AssertNotInLayoutPass()
    {
        if (_layoutPassDepth > 0)
        {
            throw new InvalidOperationException(
                "ScrollStateStore offset write during a layout pass. Scroll offsets are paint/hit-test state; layout must never read or write them (browser-plan/scroll-model.md).");
        }
    }
}
