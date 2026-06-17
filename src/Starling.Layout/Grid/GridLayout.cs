using Starling.Css.Cascade;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Layout.Block;
using Starling.Layout.Box;

namespace Starling.Layout.Grid;

/// <summary>
/// CSS Grid layout (CSS Grid Layout Module Level 1/2, simplified).
/// Supports explicit <c>grid-template-columns</c> and <c>grid-template-rows</c>
/// track lists (<c>&lt;length&gt;</c>, <c>&lt;percentage&gt;</c>, <c>calc()</c>,
/// <c>fr</c>, <c>auto</c>, <c>min-content</c>/<c>max-content</c>,
/// <c>minmax()</c>, <c>fit-content()</c>, <c>repeat()</c> with integer and
/// <c>auto-fill</c>/<c>auto-fit</c> counts); explicit item placement via
/// <c>grid-row</c>/<c>grid-column</c> line numbers (negative lines count from
/// the end of the explicit grid) and <c>span N</c>; <c>grid-template-areas</c>
/// with <c>grid-area</c> named placement (non-rectangular templates are
/// dropped, per spec invalidity); sparse row-major auto-placement that flows
/// around explicitly placed items (css-grid-1 §8.5); <c>gap</c>; and box
/// alignment via <c>justify-items/self</c> and <c>align-items/self</c>.
/// Track sizing follows a simplified §11: fixed and content-sized tracks get
/// their base sizes, tracks then grow toward growth limits (maximize), free
/// space is split across <c>fr</c> tracks proportionally with minimum floors
/// (§11.7), and leftover space stretches <c>auto</c> tracks when
/// <c>justify-content</c>/<c>align-content</c> is <c>normal</c>/<c>stretch</c>.
/// </summary>
/// <remarks>
/// Out of scope (degrade gracefully): <c>grid-auto-flow: column</c> (treated
/// as row), <c>dense</c> packing, named <c>[line-name]</c> placement (bracketed
/// names are skipped in track lists; only area names resolve), <c>baseline</c>
/// alignment (falls back to <c>stretch</c>), <c>auto</c> margins, implicit
/// tracks before line 1 (negative resolved indices clamp to 0), and
/// <c>subgrid</c>. <c>fit-content(l)</c> is approximated as
/// <c>min(max-content, l)</c> (no separate min-content floor). The min-content
/// contribution of an item is approximated by its max-content size.
/// Coordinate convention matches <see cref="BlockLayout"/> / flex: each item's
/// <see cref="Box.Box.Frame"/> is in the container's content-box space.
/// </remarks>
internal sealed class GridLayout
{
    private readonly BlockLayout _block;
    private readonly Size _viewport;

    public GridLayout(BlockLayout block, Size viewport)
    {
        _block = block;
        _viewport = viewport;
    }

    public double Layout(Box.Box container, double containerWidth, double? explicitHeight)
    {
        // CSS Grid §9: absolutely/fixed-positioned children are not grid items
        // — they're excluded from placement/sizing and positioned later by
        // PositionLayout. Mirror FlexLayout so a hidden `position: fixed`
        // overlay doesn't occupy a grid cell.
        var items = new List<Box.Box>(container.Children.Count);
        foreach (var c in container.Children)
        {
            if (!BlockLayout.IsOutOfFlow(c.Style))
            {
                items.Add(c);
            }
        }

        if (items.Count == 0)
        {
            return explicitHeight ?? 0;
        }

        var n = items.Count;

        var style = container.Style;
        var columnGap = ResolveGap(style, PropertyId.ColumnGap, containerWidth);
        var rowGap = ResolveGap(style, PropertyId.RowGap, _viewport.Height);

        // --- Explicit grid: track lists + named areas -----------------------
        var areas = ParseAreas(style?.Get(PropertyId.GridTemplateAreas));
        var colDefs = ParseTrackDefs(style?.Get(PropertyId.GridTemplateColumns), containerWidth, containerWidth, columnGap);
        var rowDefs = ParseTrackDefs(style?.Get(PropertyId.GridTemplateRows), explicitHeight, explicitHeight, rowGap);

        // grid-template-areas implies that many explicit (auto-sized) tracks.
        if (areas is not null)
        {
            while (colDefs.Count < areas.Cols)
            {
                colDefs.Add(AutoDef());
            }

            while (rowDefs.Count < areas.Rows)
            {
                rowDefs.Add(AutoDef());
            }
        }
        var explicitCols = colDefs.Count;
        var explicitRows = rowDefs.Count;

        // --- Per-item placement specs (§8.3/§8.5) ---------------------------
        var colSpec = new AxisSpec[n];
        var rowSpec = new AxisSpec[n];
        for (var i = 0; i < n; i++)
        {
            colSpec[i] = ResolveAxisSpec(items[i].Style, PropertyId.GridColumnStart, PropertyId.GridColumnEnd, explicitCols, areas, isRow: false);
            rowSpec[i] = ResolveAxisSpec(items[i].Style, PropertyId.GridRowStart, PropertyId.GridRowEnd, explicitRows, areas, isRow: true);
        }

        // Final column count: explicit tracks, definite column placements, and
        // the widest auto-placed span (row flow never adds implicit columns
        // beyond that).
        var numCols = Math.Max(1, explicitCols);
        for (var i = 0; i < n; i++)
        {
            var end = colSpec[i].Start >= 0 ? colSpec[i].Start + colSpec[i].Span : colSpec[i].Span;
            if (end > numCols)
            {
                numCols = end;
            }
        }

        var placements = PlaceItems(n, colSpec, rowSpec, numCols, out var numRows);
        if (numRows < explicitRows)
        {
            numRows = explicitRows;
        }

        if (numRows < 1)
        {
            numRows = 1;
        }

        // Full per-axis track arrays, implicit tracks taking grid-auto-* sizes.
        var colTracks = BuildTrackArray(colDefs, numCols, ImplicitDef(style, PropertyId.GridAutoColumns, containerWidth));
        var rowTracks = BuildTrackArray(rowDefs, numRows, ImplicitDef(style, PropertyId.GridAutoRows, explicitHeight));

        // repeat(auto-fit): tracks with no items collapse to zero (and their
        // gap goes with them).
        CollapseEmptyAutoFitTracks(colTracks, placements, isColumn: true);
        CollapseEmptyAutoFitTracks(rowTracks, placements, isColumn: false);

        // --- Box model, alignment, inline-axis contributions -----------------
        var pad = new (double H, double V)[n];
        var jAlign = new Align[n];
        var aAlign = new Align[n];
        var maxContentW = new double[n];
        var colContrib = new double[numCols];
        for (var i = 0; i < n; i++)
        {
            var item = items[i];
            ResolveBoxModel(item, containerWidth);
            pad[i] = (item.Padding.Horizontal + item.Border.Horizontal,
                      item.Padding.Vertical + item.Border.Vertical);
            jAlign[i] = ResolveInlineAlign(style, item.Style);
            aAlign[i] = ResolveBlockAlign(style, item.Style);

            // Max-content contribution: an explicit fixed width wins; percent
            // and calc widths (cyclic vs. the track) measure like auto.
            var fixedW = FixedWidth(item.Style);
            maxContentW[i] = fixedW ?? MeasureMaxContent(item);
            var outerW = maxContentW[i] + pad[i].H;
            DistributeContribution(colContrib, colTracks, placements[i].Col, placements[i].ColSpan, outerW, columnGap);
        }

        var stretchCols = IsStretchContent(style, PropertyId.JustifyContent);
        var colSizes = SizeTracks(colTracks, colContrib, containerWidth, columnGap, stretchCols);
        var colX = TrackPositions(colSizes, colTracks, columnGap, out _);

        // --- Item inline sizes, block-axis contributions ---------------------
        var inline = new double[n];
        var blockContent = new double[n];
        var effJ = new Align[n];
        var effA = new Align[n];
        var rowContrib = new double[numRows];
        for (var i = 0; i < n; i++)
        {
            var item = items[i];
            var p = placements[i];
            var cellW = SpanExtent(colX, colSizes, p.Col, p.ColSpan);
            var availInline = Math.Max(0, cellW - pad[i].H);

            // §6.6: stretch applies only when the item's width is auto; a
            // sized item start-aligns instead (no auto-margin support).
            var explicitW = BlockLayout.ResolveLength(item.Style, PropertyId.Width, cellW, _viewport, allowAuto: true);
            if (explicitW is { } w)
            {
                inline[i] = w;
                effJ[i] = jAlign[i] == Align.Stretch ? Align.Start : jAlign[i];
            }
            else if (jAlign[i] == Align.Stretch)
            {
                inline[i] = availInline;
                effJ[i] = Align.Stretch;
            }
            else
            {
                inline[i] = Math.Min(maxContentW[i], availInline);
                effJ[i] = jAlign[i];
            }

            // Block (row-axis) natural content size, measured at the chosen
            // inline width. An explicit height wins; a percentage height has
            // no definite grid-area basis here so it collapses to auto.
            var explicitH = BlockLayout.ResolveHeight(item.Style, PropertyId.Height, null, _viewport, allowAuto: true);
            blockContent[i] = explicitH ?? _block.LayoutItem(item, inline[i], null, measure: true);
            effA[i] = explicitH.HasValue && aAlign[i] == Align.Stretch ? Align.Start : aAlign[i];

            var outerH = blockContent[i] + pad[i].V;
            DistributeContribution(rowContrib, rowTracks, p.Row, p.RowSpan, outerH, rowGap);
        }

        var stretchRows = IsStretchContent(style, PropertyId.AlignContent);
        var rowSizes = SizeTracks(rowTracks, rowContrib, explicitHeight, rowGap, stretchRows);
        var rowY = TrackPositions(rowSizes, rowTracks, rowGap, out var totalHeight);

        // --- Final placement --------------------------------------------------
        for (var i = 0; i < n; i++)
        {
            var item = items[i];
            var p = placements[i];
            var cellW = SpanExtent(colX, colSizes, p.Col, p.ColSpan);
            var cellH = SpanExtent(rowY, rowSizes, p.Row, p.RowSpan);

            var contentW = effJ[i] == Align.Stretch ? Math.Max(0, cellW - pad[i].H) : inline[i];
            var contentH = effA[i] == Align.Stretch ? Math.Max(0, cellH - pad[i].V) : blockContent[i];
            _block.LayoutItem(item, contentW, contentH);

            var outerW = contentW + pad[i].H;
            var outerH = contentH + pad[i].V;
            var xOff = AlignOffset(effJ[i], cellW, outerW);
            var yOff = AlignOffset(effA[i], cellH, outerH);
            item.Frame = new Rect(colX[p.Col] + xOff, rowY[p.Row] + yOff, outerW, outerH);
        }

        return explicitHeight ?? totalHeight;
    }

    // ====================================================================
    // Placement (css-grid-1 §8)
    // ====================================================================

    /// <summary>One axis of an item's placement: 0-based start line (-1 = auto) and span.</summary>
    private struct AxisSpec
    {
        public int Start;
        public int Span;
    }

    private struct ItemPlacement
    {
        public int Col;
        public int ColSpan;
        public int Row;
        public int RowSpan;
    }

    private enum LineKind : byte { Auto, Line, Span }

    /// <summary>
    /// Resolve one axis of grid-row/grid-column into a (start, span) pair.
    /// Handles line numbers (negative lines count back from the end of the
    /// explicit grid), <c>span N</c>, <c>auto</c>, and area names from
    /// grid-template-areas (start longhand → area start line, end longhand →
    /// area end line, per the *-start/*-end implicit line names).
    /// </summary>
    private static AxisSpec ResolveAxisSpec(ComputedStyle? style, PropertyId startId, PropertyId endId, int explicitTracks, AreaMap? areas, bool isRow)
    {
        Classify(style?.Get(startId), areas, isRow, isStart: true, out var sk, out var sn);
        Classify(style?.Get(endId), areas, isRow, isStart: false, out var ek, out var en);

        // Resolve a 1-based (possibly negative) line number to a 0-based index;
        // negative lines count from the end of the explicit grid. Indices
        // before line 1 clamp to 0 (no leading implicit tracks).
        int L(int lineNumber)
        {
            var idx = lineNumber > 0 ? lineNumber - 1 : explicitTracks + 1 + lineNumber;
            return idx < 0 ? 0 : idx;
        }

        if (sk == LineKind.Line && ek == LineKind.Line)
        {
            var a = L(sn);
            var b = L(en);
            if (b <= a)
            {
                b = a + 1; // §8.3.1: end before start → span 1 from start
            }

            return new AxisSpec { Start = a, Span = b - a };
        }
        if (sk == LineKind.Line && ek == LineKind.Span)
        {
            return new AxisSpec { Start = L(sn), Span = Math.Max(1, en) };
        }

        if (sk == LineKind.Line)
        {
            return new AxisSpec { Start = L(sn), Span = 1 };
        }

        if (sk == LineKind.Span && ek == LineKind.Line)
        {
            var b = L(en);
            var a = b - Math.Max(1, sn);
            if (a < 0)
            {
                a = 0;
            }

            return new AxisSpec { Start = a, Span = Math.Max(1, b - a) };
        }
        if (ek == LineKind.Line)
        {
            var b = L(en);
            var a = b > 0 ? b - 1 : 0;
            return new AxisSpec { Start = a, Span = 1 };
        }
        if (sk == LineKind.Span)
        {
            return new AxisSpec { Start = -1, Span = Math.Max(1, sn) };
        }

        if (ek == LineKind.Span)
        {
            return new AxisSpec { Start = -1, Span = Math.Max(1, en) };
        }

        return new AxisSpec { Start = -1, Span = 1 };
    }

    private static void Classify(CssValue? value, AreaMap? areas, bool isRow, bool isStart, out LineKind kind, out int number)
    {
        kind = LineKind.Auto;
        number = 0;
        switch (value)
        {
            case CssNumber num when (int)num.Value != 0:
                kind = LineKind.Line;
                number = (int)num.Value;
                return;
            case CssValueList list:
                {
                    var sawSpan = false;
                    var spanCount = 0;
                    foreach (var v in list.Values)
                    {
                        if (v is CssKeyword { Name: "span" })
                        {
                            sawSpan = true;
                        }
                        else if (v is CssNumber m)
                        {
                            spanCount = (int)m.Value;
                        }
                    }
                    if (sawSpan && spanCount > 0)
                    {
                        kind = LineKind.Span;
                        number = spanCount;
                    }
                    // `span <custom-ident>` and other shapes degrade to auto.
                    return;
                }
            case CssKeyword { Name: "auto" }:
                return;
            case CssKeyword k:
                {
                    // An area name places against the area's implicit
                    // name-start/name-end lines. Unknown idents degrade to auto.
                    if (areas is not null && areas.Areas.TryGetValue(k.Name, out var rect))
                    {
                        kind = LineKind.Line;
                        number = isRow
                            ? (isStart ? rect.R0 + 1 : rect.R1 + 2)
                            : (isStart ? rect.C0 + 1 : rect.C1 + 2);
                    }
                    return;
                }
        }
    }

    /// <summary>
    /// Sparse row-major auto-placement (css-grid-1 §8.5): definite items are
    /// placed first, items locked to a row pack after previous items in that
    /// row, then fully-auto items flow from a forward-only cursor around
    /// everything already placed. `dense` is not supported.
    /// </summary>
    private static ItemPlacement[] PlaceItems(int n, AxisSpec[] colSpec, AxisSpec[] rowSpec, int numCols, out int numRows)
    {
        var placements = new ItemPlacement[n];
        var placed = new bool[n];
        var occ = new List<bool[]>(8); // row-major occupancy, one bool[] per row

        void EnsureRows(int rowEnd)
        {
            while (occ.Count < rowEnd)
            {
                occ.Add(new bool[numCols]);
            }
        }

        bool Fits(int r, int rs, int c, int cs)
        {
            EnsureRows(r + rs);
            for (var rr = r; rr < r + rs; rr++)
            {
                var row = occ[rr];
                for (var cc = c; cc < c + cs; cc++)
                {
                    if (row[cc])
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        void Mark(int i, int r, int rs, int c, int cs)
        {
            EnsureRows(r + rs);
            for (var rr = r; rr < r + rs; rr++)
            {
                var row = occ[rr];
                for (var cc = c; cc < c + cs; cc++)
                {
                    row[cc] = true;
                }
            }
            placements[i] = new ItemPlacement { Col = c, ColSpan = cs, Row = r, RowSpan = rs };
            placed[i] = true;
        }

        // Pass 1: both axes definite.
        for (var i = 0; i < n; i++)
        {
            if (colSpec[i].Start < 0 || rowSpec[i].Start < 0)
            {
                continue;
            }

            Mark(i, rowSpec[i].Start, Math.Max(1, rowSpec[i].Span), colSpec[i].Start, Math.Max(1, colSpec[i].Span));
        }

        // Pass 2: definite row, auto column — pack after previous items in
        // that row (sparse).
        Dictionary<int, int>? rowCursor = null;
        for (var i = 0; i < n; i++)
        {
            if (placed[i] || rowSpec[i].Start < 0)
            {
                continue;
            }

            rowCursor ??= new Dictionary<int, int>();
            var r = rowSpec[i].Start;
            var rs = Math.Max(1, rowSpec[i].Span);
            var cs = Math.Min(Math.Max(1, colSpec[i].Span), numCols);
            rowCursor.TryGetValue(r, out var from);
            var found = -1;
            for (var c = from; c + cs <= numCols; c++)
            {
                if (Fits(r, rs, c, cs)) { found = c; break; }
            }
            if (found < 0)
            {
                found = Math.Max(0, numCols - cs); // overflow: overlap at the end
            }

            Mark(i, r, rs, found, cs);
            rowCursor[r] = found + cs;
        }

        // Pass 3: auto-row items, forward-only cursor.
        var cursorRow = 0;
        var cursorCol = 0;
        for (var i = 0; i < n; i++)
        {
            if (placed[i])
            {
                continue;
            }

            var rs = Math.Max(1, rowSpec[i].Span);
            var cs = Math.Min(Math.Max(1, colSpec[i].Span), numCols);
            if (colSpec[i].Start >= 0)
            {
                // Definite column: advance to the next row whose cells are free
                // at that column (moving down if the cursor already passed it).
                var c = Math.Min(colSpec[i].Start, numCols - cs);
                if (c < 0)
                {
                    c = 0;
                }

                if (c < cursorCol) { cursorRow++; cursorCol = 0; }
                while (!Fits(cursorRow, rs, c, cs))
                {
                    cursorRow++;
                }

                Mark(i, cursorRow, rs, c, cs);
                cursorCol = c + cs;
            }
            else
            {
                for (var r = cursorRow; ; r++)
                {
                    var startC = r == cursorRow ? cursorCol : 0;
                    var found = -1;
                    for (var c = startC; c + cs <= numCols; c++)
                    {
                        if (Fits(r, rs, c, cs)) { found = c; break; }
                    }
                    if (found < 0)
                    {
                        continue; // next row (fresh rows are empty, so this terminates)
                    }

                    Mark(i, r, rs, found, cs);
                    cursorRow = r;
                    cursorCol = found + cs;
                    break;
                }
            }
        }

        numRows = occ.Count;
        return placements;
    }

    // ====================================================================
    // grid-template-areas (css-grid-1 §7.3)
    // ====================================================================

    private readonly record struct AreaRect(int R0, int R1, int C0, int C1);

    private sealed class AreaMap
    {
        public readonly Dictionary<string, AreaRect> Areas = new(StringComparer.Ordinal);
        public int Rows;
        public int Cols;
    }

    /// <summary>
    /// Parse grid-template-areas strings into named rectangles. If any name's
    /// cells do not form a filled rectangle the whole declaration is invalid
    /// (§7.3) and null is returned (no named areas).
    /// </summary>
    private static AreaMap? ParseAreas(CssValue? value)
    {
        List<string>? rows = null;
        switch (value)
        {
            case CssString s:
                rows = [s.Value];
                break;
            case CssValueList list:
                foreach (var v in list.Values)
                {
                    if (v is CssString rs)
                    {
                        (rows ??= new List<string>(list.Values.Count)).Add(rs.Value);
                    }
                }

                break;
        }
        if (rows is null || rows.Count == 0)
        {
            return null;
        }

        var tokens = new string[rows.Count][];
        var cols = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            tokens[r] = rows[r].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens[r].Length > cols)
            {
                cols = tokens[r].Length;
            }
        }
        if (cols == 0)
        {
            return null;
        }

        var map = new AreaMap { Rows = rows.Count, Cols = cols };
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var r = 0; r < tokens.Length; r++)
        {
            var rowTokens = tokens[r];
            for (var c = 0; c < rowTokens.Length; c++)
            {
                var name = rowTokens[c];
                if (name.Length == 0 || name[0] == '.')
                {
                    continue; // null cell token
                }

                if (map.Areas.TryGetValue(name, out var rect))
                {
                    map.Areas[name] = new AreaRect(
                        Math.Min(rect.R0, r), Math.Max(rect.R1, r),
                        Math.Min(rect.C0, c), Math.Max(rect.C1, c));
                    counts[name]++;
                }
                else
                {
                    map.Areas[name] = new AreaRect(r, r, c, c);
                    counts[name] = 1;
                }
            }
        }

        // Rectangle validation: a name's cell count must fill its bounding box.
        foreach (var (name, rect) in map.Areas)
        {
            var area = (rect.R1 - rect.R0 + 1) * (rect.C1 - rect.C0 + 1);
            if (counts[name] != area)
            {
                return null;
            }
        }
        return map;
    }

    // ====================================================================
    // Track-list parsing
    // ====================================================================

    private enum SizeKind : byte { Px, Fr, Auto, MinContent, MaxContent, FitContent }

    private struct TrackDef
    {
        public SizeKind MinKind;
        public double Min;
        public SizeKind MaxKind;
        public double Max;
        public bool AutoFit;   // produced by repeat(auto-fit, ...)
        public bool Collapsed; // auto-fit track with no items
    }

    private static TrackDef AutoDef() => new() { MinKind = SizeKind.Auto, MaxKind = SizeKind.Auto };

    /// <summary>
    /// Parse a grid-template-rows/columns value into track definitions. The
    /// value arrives after var()/calc() substitution, so percentages and
    /// symbolic calc() resolve here against <paramref name="percentBasis"/>.
    /// An auto-fill/auto-fit repeat() expands to a count derived from the
    /// definite <paramref name="autoRepeatAvailable"/> size including gaps.
    /// </summary>
    private List<TrackDef> ParseTrackDefs(CssValue? value, double? percentBasis, double? autoRepeatAvailable, double gap)
    {
        var defs = new List<TrackDef>();
        if (value is null || value is CssKeyword { Name: "none" })
        {
            return defs;
        }

        var repeatAt = -1;
        List<TrackDef>? repeatBody = null;
        var autoFit = false;
        AppendTrackDefs(value, defs, percentBasis, ref repeatAt, ref repeatBody, ref autoFit);

        if (repeatBody is { Count: > 0 })
        {
            var count = AutoRepeatCount(defs, repeatBody, autoRepeatAvailable, gap);
            var expanded = new List<TrackDef>(defs.Count + count * repeatBody.Count);
            for (var i = 0; i < repeatAt; i++)
            {
                expanded.Add(defs[i]);
            }

            for (var r = 0; r < count; r++)
            {
                for (var b = 0; b < repeatBody.Count; b++)
                {
                    var d = repeatBody[b];
                    d.AutoFit = autoFit;
                    expanded.Add(d);
                }
            }
            for (var i = repeatAt; i < defs.Count; i++)
            {
                expanded.Add(defs[i]);
            }

            return expanded;
        }
        return defs;
    }

    private void AppendTrackDefs(CssValue value, List<TrackDef> defs, double? basis, ref int repeatAt, ref List<TrackDef>? repeatBody, ref bool autoFit)
    {
        switch (value)
        {
            case CssValueList list:
                foreach (var v in list.Values)
                {
                    AppendTrackDefs(v, defs, basis, ref repeatAt, ref repeatBody, ref autoFit);
                }

                break;
            case CssFunctionValue { Name: "repeat" } f when f.Arguments.Count >= 2:
                if (f.Arguments[0] is CssNumber { Value: var cnt } && cnt >= 1)
                {
                    var body = new List<TrackDef>();
                    var bAt = -1;
                    List<TrackDef>? bBody = null;
                    var bFit = false;
                    for (var a = 1; a < f.Arguments.Count; a++)
                    {
                        AppendTrackDefs(f.Arguments[a], body, basis, ref bAt, ref bBody, ref bFit);
                    }

                    for (var r = 0; r < (int)cnt; r++)
                    {
                        for (var b = 0; b < body.Count; b++)
                        {
                            defs.Add(body[b]);
                        }
                    }
                }
                else if (f.Arguments[0] is CssKeyword { Name: "auto-fill" or "auto-fit" } k && repeatBody is null)
                {
                    // Only one auto-repeat is valid per track list (§7.2.3.1).
                    repeatBody = new List<TrackDef>();
                    var bAt = -1;
                    List<TrackDef>? bBody = null;
                    var bFit = false;
                    for (var a = 1; a < f.Arguments.Count; a++)
                    {
                        AppendTrackDefs(f.Arguments[a], repeatBody, basis, ref bAt, ref bBody, ref bFit);
                    }

                    repeatAt = defs.Count;
                    autoFit = k.Name == "auto-fit";
                }
                break;
            default:
                if (TryParseTrackDef(value, basis, out var def))
                {
                    defs.Add(def);
                }

                break;
        }
    }

    private bool TryParseTrackDef(CssValue value, double? basis, out TrackDef def)
    {
        def = default;
        switch (value)
        {
            case CssFunctionValue { Name: "minmax" } f when f.Arguments.Count == 2:
                {
                    var (minK, minV) = ParseSizePart(f.Arguments[0], basis, isMin: true);
                    var (maxK, maxV) = ParseSizePart(f.Arguments[1], basis, isMin: false);
                    // §7.2.5: if max < min, max is treated as min.
                    if (minK == SizeKind.Px && maxK == SizeKind.Px && maxV < minV)
                    {
                        maxV = minV;
                    }

                    def = new TrackDef { MinKind = minK, Min = minV, MaxKind = maxK, Max = maxV };
                    return true;
                }
            case CssFunctionValue { Name: "fit-content" } f when f.Arguments.Count == 1:
                {
                    var limit = ResolvePx(f.Arguments[0], basis);
                    def = limit is { } px
                        ? new TrackDef { MinKind = SizeKind.Auto, MaxKind = SizeKind.FitContent, Max = px }
                        : AutoDef();
                    return true;
                }
            case CssDimension { Unit: "fr" } d:
                def = new TrackDef { MinKind = SizeKind.Auto, MaxKind = SizeKind.Fr, Max = Math.Max(0, d.Value) };
                return true;
            case CssKeyword { Name: "auto" }:
                def = AutoDef();
                return true;
            case CssKeyword { Name: "min-content" }:
                def = new TrackDef { MinKind = SizeKind.MinContent, MaxKind = SizeKind.MinContent };
                return true;
            case CssKeyword { Name: "max-content" }:
                def = new TrackDef { MinKind = SizeKind.MaxContent, MaxKind = SizeKind.MaxContent };
                return true;
            default:
                {
                    // Lengths, percentages, numbers, symbolic calc(). Anything else
                    // (line-name blocks, unknown keywords) contributes no track.
                    var px = ResolvePx(value, basis);
                    if (px is { } v)
                    {
                        def = new TrackDef { MinKind = SizeKind.Px, Min = v, MaxKind = SizeKind.Px, Max = v };
                        return true;
                    }
                    // A percentage (or calc with %) against an indefinite size
                    // behaves as auto (css-grid-1 §7.2.1) — the track must still
                    // occupy its slot, or later tracks shift and negative line
                    // numbers resolve against the wrong count.
                    if (value is CssPercentage or CssCalc)
                    {
                        def = AutoDef();
                        return true;
                    }
                    return false;
                }
        }
    }

    private (SizeKind Kind, double Value) ParseSizePart(CssValue value, double? basis, bool isMin)
    {
        switch (value)
        {
            case CssDimension { Unit: "fr" } d:
                // fr is invalid as a minimum; degrade to auto.
                return isMin ? (SizeKind.Auto, 0) : (SizeKind.Fr, Math.Max(0, d.Value));
            case CssKeyword { Name: "auto" }:
                return (SizeKind.Auto, 0);
            case CssKeyword { Name: "min-content" }:
                return (SizeKind.MinContent, 0);
            case CssKeyword { Name: "max-content" }:
                return (SizeKind.MaxContent, 0);
            default:
                return ResolvePx(value, basis) is { } px ? (SizeKind.Px, px) : (SizeKind.Auto, 0);
        }
    }

    /// <summary>Resolve a definite track size to pixels (percentages against the basis; symbolic calc() too).</summary>
    private double? ResolvePx(CssValue value, double? basis) => value switch
    {
        CssLength len => BlockLayout.ToPx(len, _viewport),
        CssPercentage pct => basis is { } b ? b * pct.Value / 100d : null,
        CssNumber n => n.Value,
        CssCalc calc => BlockLayout.ResolveCalcPx(calc, basis, _viewport),
        _ => null,
    };

    /// <summary>
    /// repeat(auto-fill|auto-fit) count (§7.2.3.2): the largest number of body
    /// copies that fits the definite available size, gaps included; minimum 1.
    /// </summary>
    private static int AutoRepeatCount(List<TrackDef> others, List<TrackDef> body, double? available, double gap)
    {
        if (available is not { } avail)
        {
            return 1;
        }

        double bodySum = 0;
        for (var i = 0; i < body.Count; i++)
        {
            var d = DefiniteSize(body[i]);
            if (d < 0)
            {
                return 1; // auto-repeat tracks must have definite sizes
            }

            bodySum += d;
        }
        var k = body.Count;
        var denom = bodySum + gap * k;
        if (denom <= 0)
        {
            return 1;
        }

        double otherSum = 0;
        for (var i = 0; i < others.Count; i++)
        {
            var d = DefiniteSize(others[i]);
            if (d > 0)
            {
                otherSum += d;
            }
        }
        var numer = avail - otherSum - gap * (others.Count - 1);
        var count = (int)Math.Floor(numer / denom + 1e-9);
        return count < 1 ? 1 : count;
    }

    private static double DefiniteSize(TrackDef def)
    {
        if (def.MaxKind is SizeKind.Px or SizeKind.FitContent)
        {
            return def.Max;
        }

        if (def.MinKind == SizeKind.Px)
        {
            return def.Min;
        }

        return -1;
    }

    private static TrackDef[] BuildTrackArray(List<TrackDef> explicitDefs, int count, TrackDef implicitDef)
    {
        var tracks = new TrackDef[count];
        var e = Math.Min(explicitDefs.Count, count);
        for (var i = 0; i < e; i++)
        {
            tracks[i] = explicitDefs[i];
        }

        for (var i = e; i < count; i++)
        {
            tracks[i] = implicitDef;
        }

        return tracks;
    }

    private TrackDef ImplicitDef(ComputedStyle? style, PropertyId id, double? basis)
    {
        var value = style?.Get(id);
        if (value is CssValueList list && list.Values.Count > 0)
        {
            value = list.Values[0]; // grid-auto-* lists cycle; use the first size
        }

        return value is not null && TryParseTrackDef(value, basis, out var def) ? def : AutoDef();
    }

    private static void CollapseEmptyAutoFitTracks(TrackDef[] tracks, ItemPlacement[] placements, bool isColumn)
    {
        var any = false;
        for (var t = 0; t < tracks.Length; t++)
        {
            if (tracks[t].AutoFit) { any = true; break; }
        }

        if (!any)
        {
            return;
        }

        var used = new bool[tracks.Length];
        for (var i = 0; i < placements.Length; i++)
        {
            var start = isColumn ? placements[i].Col : placements[i].Row;
            var end = start + (isColumn ? placements[i].ColSpan : placements[i].RowSpan);
            if (end > tracks.Length)
            {
                end = tracks.Length;
            }

            for (var t = start; t < end; t++)
            {
                used[t] = true;
            }
        }
        for (var t = 0; t < tracks.Length; t++)
        {
            if (tracks[t].AutoFit && !used[t])
            {
                tracks[t].Collapsed = true;
            }
        }
    }

    // ====================================================================
    // Track sizing (css-grid-1 §11, simplified)
    // ====================================================================

    /// <summary>
    /// Size one axis's tracks. Base sizes come from fixed parts and content
    /// contributions; tracks then grow toward their growth limits (§11.5
    /// maximize), free space is distributed to fr tracks proportionally with
    /// their minimum floors (§11.7), and remaining space stretches auto tracks
    /// (§11.8) when the content-distribution keyword is normal/stretch. With an
    /// indefinite available size each track resolves to its growth limit
    /// (content size for intrinsic and fr tracks).
    /// </summary>
    private static double[] SizeTracks(TrackDef[] defs, double[] contrib, double? available, double gap, bool stretchAuto)
    {
        var n = defs.Length;
        var sizes = new double[n];
        var limits = new double[n];
        var active = 0;
        double totalFr = 0;
        for (var i = 0; i < n; i++)
        {
            ref readonly var d = ref defs[i];
            if (d.Collapsed)
            {
                continue;
            }

            active++;
            double baseSize, limit;
            if (d.MaxKind == SizeKind.Fr)
            {
                // The fr track's floor: a definite minimum from minmax();
                // bare fr floors at 0 (auto-min approximated as 0).
                baseSize = d.MinKind == SizeKind.Px ? d.Min : 0;
                limit = double.PositiveInfinity;
                totalFr += d.Max;
            }
            else if (d.MaxKind == SizeKind.FitContent)
            {
                // fit-content(l) ≈ min(max-content, l).
                baseSize = Math.Min(contrib[i], d.Max);
                limit = baseSize;
            }
            else
            {
                baseSize = d.MinKind == SizeKind.Px ? d.Min : contrib[i];
                limit = d.MaxKind == SizeKind.Px ? d.Max : contrib[i];
                if (limit < baseSize)
                {
                    limit = baseSize;
                }
            }
            sizes[i] = baseSize;
            limits[i] = limit;
        }

        if (available is not { } avail)
        {
            // Indefinite available size: every track resolves to its growth
            // limit; fr and intrinsic tracks take their content size.
            for (var i = 0; i < n; i++)
            {
                if (defs[i].Collapsed)
                {
                    continue;
                }

                sizes[i] = double.IsPositiveInfinity(limits[i])
                    ? Math.Max(sizes[i], contrib[i])
                    : limits[i];
            }
            return sizes;
        }

        var free = avail - gap * Math.Max(0, active - 1);
        for (var i = 0; i < n; i++)
        {
            if (!defs[i].Collapsed)
            {
                free -= sizes[i];
            }
        }

        // §11.5 maximize: grow non-fr tracks toward their growth limits,
        // distributing free space equally (re-distributing as tracks freeze).
        while (free > 1e-9)
        {
            var growable = 0;
            for (var i = 0; i < n; i++)
            {
                if (!defs[i].Collapsed && defs[i].MaxKind != SizeKind.Fr && sizes[i] < limits[i] - 1e-9)
                {
                    growable++;
                }
            }

            if (growable == 0)
            {
                break;
            }

            var share = free / growable;
            double distributed = 0;
            for (var i = 0; i < n; i++)
            {
                if (defs[i].Collapsed || defs[i].MaxKind == SizeKind.Fr || sizes[i] >= limits[i] - 1e-9)
                {
                    continue;
                }

                var grow = Math.Min(share, limits[i] - sizes[i]);
                sizes[i] += grow;
                distributed += grow;
            }
            free -= distributed;
            if (distributed <= 1e-9)
            {
                break;
            }
        }

        // §11.7 expand flexible tracks: split the leftover across fr tracks in
        // proportion to their factors, never below each track's floor.
        if (totalFr > 0 && free > 1e-9)
        {
            var frSpace = free;
            for (var i = 0; i < n; i++)
            {
                if (!defs[i].Collapsed && defs[i].MaxKind == SizeKind.Fr)
                {
                    frSpace += sizes[i];
                }
            }

            var frozen = new bool[n];
            while (true)
            {
                double factors = 0, reserved = 0;
                for (var i = 0; i < n; i++)
                {
                    if (defs[i].Collapsed || defs[i].MaxKind != SizeKind.Fr)
                    {
                        continue;
                    }

                    if (frozen[i])
                    {
                        reserved += sizes[i];
                    }
                    else
                    {
                        factors += defs[i].Max;
                    }
                }
                if (factors <= 0)
                {
                    break;
                }

                var unit = (frSpace - reserved) / factors;
                if (unit < 0)
                {
                    unit = 0;
                }

                var froze = false;
                for (var i = 0; i < n; i++)
                {
                    if (defs[i].Collapsed || defs[i].MaxKind != SizeKind.Fr || frozen[i])
                    {
                        continue;
                    }

                    if (sizes[i] > defs[i].Max * unit + 1e-9) { frozen[i] = true; froze = true; }
                }
                if (froze)
                {
                    continue;
                }

                for (var i = 0; i < n; i++)
                {
                    if (!defs[i].Collapsed && defs[i].MaxKind == SizeKind.Fr && !frozen[i])
                    {
                        sizes[i] = defs[i].Max * unit;
                    }
                }

                break;
            }
            free = 0;
        }

        // §11.8 stretch auto tracks with whatever is left.
        if (stretchAuto && free > 1e-9)
        {
            var autoCount = 0;
            for (var i = 0; i < n; i++)
            {
                if (!defs[i].Collapsed && defs[i].MaxKind == SizeKind.Auto)
                {
                    autoCount++;
                }
            }

            if (autoCount > 0)
            {
                var per = free / autoCount;
                for (var i = 0; i < n; i++)
                {
                    if (!defs[i].Collapsed && defs[i].MaxKind == SizeKind.Auto)
                    {
                        sizes[i] += per;
                    }
                }
            }
        }

        return sizes;
    }

    /// <summary>
    /// Spread an item's outer contribution across the tracks it spans: fixed
    /// tracks absorb their definite part, the remainder splits equally among
    /// the intrinsic (content-sized / flexible) tracks in the span.
    /// </summary>
    private static void DistributeContribution(double[] contrib, TrackDef[] tracks, int start, int span, double outer, double gap)
    {
        var end = start + span;
        if (end > tracks.Length)
        {
            end = tracks.Length;
        }

        if (start >= end)
        {
            return;
        }

        if (end - start == 1)
        {
            if (outer > contrib[start])
            {
                contrib[start] = outer;
            }

            return;
        }

        var remaining = outer - gap * (end - start - 1);
        var intrinsic = 0;
        for (var t = start; t < end; t++)
        {
            ref readonly var d = ref tracks[t];
            if (d.Collapsed)
            {
                continue;
            }

            if (d.MinKind == SizeKind.Px && d.MaxKind == SizeKind.Px)
            {
                remaining -= d.Min;
            }
            else
            {
                intrinsic++;
            }
        }
        if (intrinsic == 0 || remaining <= 0)
        {
            return;
        }

        var share = remaining / intrinsic;
        for (var t = start; t < end; t++)
        {
            ref readonly var d = ref tracks[t];
            if (d.Collapsed || (d.MinKind == SizeKind.Px && d.MaxKind == SizeKind.Px))
            {
                continue;
            }

            if (share > contrib[t])
            {
                contrib[t] = share;
            }
        }
    }

    /// <summary>Track start offsets; collapsed tracks take no size and no gap.</summary>
    private static double[] TrackPositions(double[] sizes, TrackDef[] defs, double gap, out double total)
    {
        var pos = new double[sizes.Length];
        var cursor = 0d;
        var anyVisible = false;
        for (var i = 0; i < sizes.Length; i++)
        {
            pos[i] = cursor;
            if (defs[i].Collapsed)
            {
                continue;
            }

            anyVisible = true;
            cursor += sizes[i] + gap;
        }
        total = anyVisible ? cursor - gap : 0;
        return pos;
    }

    /// <summary>Extent of a grid area along one axis, internal gaps included.</summary>
    private static double SpanExtent(double[] pos, double[] sizes, int start, int span)
    {
        var last = start + span - 1;
        if (last >= pos.Length)
        {
            last = pos.Length - 1;
        }

        if (start > last)
        {
            start = last;
        }

        return pos[last] + sizes[last] - pos[start];
    }

    // ====================================================================
    // Alignment, measurement, box model (unchanged behavior)
    // ====================================================================

    /// <summary>Self-alignment of a grid item along one axis.</summary>
    private enum Align { Stretch, Start, Center, End }

    /// <summary>
    /// Inline-axis (column) alignment: <c>justify-self</c> falls back to the
    /// container's <c>justify-items</c> when <c>auto</c>. The grid initial
    /// <c>legacy</c>/<c>normal</c> behaves as <c>stretch</c>.
    /// </summary>
    private static Align ResolveInlineAlign(ComputedStyle? container, ComputedStyle? item)
    {
        var self = Keyword(item, PropertyId.JustifySelf);
        if (self is null or "auto")
        {
            self = Keyword(container, PropertyId.JustifyItems);
        }

        return MapAlign(self);
    }

    /// <summary>
    /// Block-axis (row) alignment: <c>align-self</c> falls back to the
    /// container's <c>align-items</c> when <c>auto</c>. The grid initial
    /// <c>normal</c> behaves as <c>stretch</c>.
    /// </summary>
    private static Align ResolveBlockAlign(ComputedStyle? container, ComputedStyle? item)
    {
        var self = Keyword(item, PropertyId.AlignSelf);
        if (self is null or "auto")
        {
            self = Keyword(container, PropertyId.AlignItems);
        }

        return MapAlign(self);
    }

    private static string? Keyword(ComputedStyle? style, PropertyId id)
        => (style?.Get(id) as CssKeyword)?.Name.ToLowerInvariant();

    /// <summary>align-content/justify-content normal|stretch (the initial) stretches auto tracks.</summary>
    private static bool IsStretchContent(ComputedStyle? style, PropertyId id)
        => Keyword(style, id) is null or "normal" or "stretch";

    private static Align MapAlign(string? keyword) => keyword switch
    {
        "center" => Align.Center,
        "start" or "flex-start" or "self-start" or "left" => Align.Start,
        "end" or "flex-end" or "self-end" or "right" => Align.End,
        // "stretch", "normal", "legacy", "baseline" (no baseline support), null.
        _ => Align.Stretch,
    };

    private static double AlignOffset(Align align, double cell, double outer) => align switch
    {
        Align.Center => (cell - outer) / 2d,
        Align.End => cell - outer,
        _ => 0, // Start and Stretch both anchor at the cell start.
    };

    /// <summary>An item's definite fixed width (lengths/numbers only; percent and calc measure like auto).</summary>
    private double? FixedWidth(ComputedStyle? style) => style?.Get(PropertyId.Width) switch
    {
        CssLength len => BlockLayout.ToPx(len, _viewport),
        CssNumber n => n.Value,
        _ => null,
    };

    /// <summary>
    /// Max-content inline size of an item: lay it out at an effectively
    /// infinite width and take its rightmost content edge (mirrors
    /// <see cref="Flex.FlexLayout"/>'s shrink-to-fit measurement).
    /// </summary>
    private double MeasureMaxContent(Box.Box item)
    {
        const double measureWidth = 1_000_000d;
        _block.LayoutItem(item, measureWidth, null, measure: true);
        return UsedContentWidth(item);
    }

    /// <summary>
    /// Rightmost content edge of a measured item, in its own content-box space.
    /// A flex/grid item carries its content on its children's positioned frames,
    /// so read those directly; otherwise walk for text/replaced/inline-block
    /// fragments (block/anonymous container frames are sized to the measurement
    /// width and must not be counted). Mirrors FlexLayout.UsedMainWidth.
    /// </summary>
    private static double UsedContentWidth(Box.Box box)
    {
        if (box.Kind != BoxKind.AnonymousBlock
            && (BlockLayout.IsFlexContainer(box.Style) || BlockLayout.IsGridContainer(box.Style)))
        {
            double max = 0;
            foreach (var child in box.Children)
            {
                max = Math.Max(max, child.Frame.X + child.Frame.Width);
            }

            return max;
        }

        double edge = 0;
        Walk(box);
        return edge;

        void Walk(Box.Box node)
        {
            switch (node)
            {
                case TextBox tb:
                    foreach (var frag in tb.Fragments)
                    {
                        edge = Math.Max(edge, frag.X + frag.Width);
                    }

                    return;
                case ImageBox img:
                    edge = Math.Max(edge, img.Frame.X + img.Frame.Width);
                    return;
                case InlineBox ib when ib != box
                    && ib.Style?.Get(PropertyId.Display) is CssKeyword { Name: "inline-block" }:
                    edge = Math.Max(edge, ib.Frame.X + ib.Frame.Width);
                    return;
            }
            foreach (var child in node.Children)
            {
                Walk(child);
            }
        }
    }

    private double ResolveGap(ComputedStyle? style, PropertyId id, double basis)
    {
        if (style is null)
        {
            return 0;
        }

        return style.Get(id) switch
        {
            CssKeyword { Name: "normal" } => 0,
            CssLength len => BlockLayout.ToPx(len, _viewport),
            CssPercentage pct => basis * pct.Value / 100d,
            CssNumber n => n.Value,
            _ => 0,
        };
    }

    private void ResolveBoxModel(Box.Box box, double containerWidth)
    {
        // Anonymous items (bare-text wrappers) take initial box-model values;
        // they must not inherit the container's padding/border via style.
        if (box.Kind == BoxKind.AnonymousBlock)
        {
            box.Margin = Edges.Zero;
            box.Padding = Edges.Zero;
            box.Border = Edges.Zero;
            return;
        }

        box.Padding = new Edges(
            BlockLayout.ResolveLength(box.Style, PropertyId.PaddingTop, _viewport.Height, _viewport) ?? 0,
            BlockLayout.ResolveLength(box.Style, PropertyId.PaddingRight, containerWidth, _viewport) ?? 0,
            BlockLayout.ResolveLength(box.Style, PropertyId.PaddingBottom, _viewport.Height, _viewport) ?? 0,
            BlockLayout.ResolveLength(box.Style, PropertyId.PaddingLeft, containerWidth, _viewport) ?? 0);

        box.Border = new Edges(
            BlockLayout.ResolveBorderWidth(box.Style, PropertyId.BorderTopWidth, PropertyId.BorderTopStyle, _viewport),
            BlockLayout.ResolveBorderWidth(box.Style, PropertyId.BorderRightWidth, PropertyId.BorderRightStyle, _viewport),
            BlockLayout.ResolveBorderWidth(box.Style, PropertyId.BorderBottomWidth, PropertyId.BorderBottomStyle, _viewport),
            BlockLayout.ResolveBorderWidth(box.Style, PropertyId.BorderLeftWidth, PropertyId.BorderLeftStyle, _viewport));

        box.Margin = Edges.Zero; // grid item margins are rare on these shells; ignore for now.
    }
}
