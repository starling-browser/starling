using Starling.Css.Cascade;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Layout;
using Starling.Layout.Box;
using Starling.Layout.Compositor;
using Starling.Layout.Tree;
using Starling.Paint.DisplayList;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint.Compositor;

/// <summary>
/// Converts a laid-out box tree into a tree of <see cref="CompositorLayer"/>s.
/// A new layer opens at the implicit document root and at every box whose
/// <see cref="Box.Hints"/> is non-empty (a CSS stacking context / promotion
/// candidate tagged by <c>StackingContextResolver</c> in M12-03). Each layer
/// owns the display-list slice its subtree paints, with the items that belong to
/// descendant layers routed into those descendants instead.
/// </summary>
/// <remarks>
/// The implicit root layer is always present, even with zero promotions, so the
/// call site is uniform (WP note). Child layers are stored in PAINT ORDER — the
/// z-index sort (CSS-Position-3 §9) happens here, not at composite time.
/// </remarks>
internal sealed class LayerTreeBuilder
{
    private readonly DisplayListBuilder _builder = new();
    private readonly Func<Box, ComputedStyle?>? _styleOverride;
    private readonly IImageResolver? _images;
    // Per-frame promotion predicate (LTF-01): an element that is actively
    // animating (or, via LTF-06, was just mutated) becomes a layer root even
    // with no static LayerHint. Evaluated every frame because animation
    // start/stop changes faster than layout runs, so it cannot be baked into the
    // layout-time Hints. Null for one-shot renders / tests with no live loop.
    private readonly Func<Box, bool>? _isAnimatingLayerRoot;
    // Stable cross-frame id per layer (from its element), used as the tile cache key's
    // layer component. Null for one-shot renders / tests (id 0 → no reuse).
    private readonly Func<Box, long>? _layerIdFor;
    // Per-container scroll offsets (overflow:scroll|auto), keyed by element. Threaded
    // into each layer's slice so the zero-copy surface path renders inner-scrolled
    // content correctly — the same offsets the readback path's DisplayListBuilder
    // applies. Null when the page has no scrolled containers.
    private readonly Func<Starling.Dom.Element, (double X, double Y)>? _scrollOffsets;
    // Per-element position:sticky paint shift (scroll-model.md WP5), threaded
    // into every layer slice exactly like the scroll offsets — both read the
    // same store. Null when the page has no scroll model.
    private readonly Func<Starling.Dom.Element, (double X, double Y)>? _stickyShifts;

    public LayerTreeBuilder(
        Func<Box, ComputedStyle?>? styleOverride = null,
        IImageResolver? images = null,
        Func<Box, bool>? isAnimatingLayerRoot = null,
        Func<Box, long>? layerIdFor = null,
        Func<Starling.Dom.Element, (double X, double Y)>? scrollOffsets = null,
        Func<Starling.Dom.Element, (double X, double Y)>? stickyShifts = null)
    {
        _styleOverride = styleOverride;
        _images = images;
        _isAnimatingLayerRoot = isAnimatingLayerRoot;
        _layerIdFor = layerIdFor;
        _scrollOffsets = scrollOffsets;
        _stickyShifts = stickyShifts;
    }

    /// <summary>
    /// A box opens its own layer iff M12-03 tagged it with any static hint, or
    /// the per-frame predicate promotes it (an actively-animating or just-mutated
    /// element — LTF-01 / LTF-06). A composite-time transform/opacity on the
    /// promoted box is applied at composite (its slice stays upright); any other
    /// animated paint property simply re-rasters this box's own small slice.
    /// </summary>
    private bool IsLayerRoot(Box box)
        => box.Hints != LayerHint.None || (_isAnimatingLayerRoot?.Invoke(box) ?? false);

    /// <summary>
    /// Builds the layer tree. The returned layer is the implicit document root
    /// (always present); its <see cref="CompositorLayer.Children"/> are the
    /// next-level promoted boxes in paint order, recursively.
    /// </summary>
    public CompositorLayer Build(BlockBox root)
    {
        ArgumentNullException.ThrowIfNull(root);
        // The root's parent content origin is the document origin (0,0). The
        // box's own Frame.X/Y is folded in by BuildLayerSlice.
        return BuildLayer(root, parentOriginX: 0, parentOriginY: 0, inheritedClip: null);
    }

    /// <summary>
    /// Builds the layer rooted at <paramref name="layerBox"/>. <paramref name="parentOriginX"/>/
    /// <paramref name="parentOriginY"/> are the page-coord content origin of the
    /// box that contains <paramref name="layerBox"/> — the same origin the flat
    /// builder's <c>Visit</c> would have received for it.
    /// </summary>
    private CompositorLayer BuildLayer(Box layerBox, double parentOriginX, double parentOriginY, Rect? inheritedClip)
    {
        var frameX = parentOriginX + layerBox.Frame.X;
        var frameY = parentOriginY + layerBox.Frame.Y;

        // A layer carries its own CSS transform; the slice must be painted in
        // untransformed local space (the composite applies the matrix), so
        // suppress the slice root's transform bracket. Non-root transformed
        // descendants inside the slice keep their normal brackets.
        var transform = DisplayListBuilder.TryGetTransformMatrix(layerBox, _styleOverride, frameX, frameY);
        var hasTransform = transform is not null;

        PaintList slice = _builder.BuildLayerSlice(
            layerBox,
            parentOriginX,
            parentOriginY,
            IsLayerRoot,
            suppressRootTransform: hasTransform,
            out var rootFilters,
            _styleOverride,
            _images,
            _scrollOffsets,
            _stickyShifts);

        var bounds = UnionBounds(slice);

        var opacity = EffectiveOpacity(layerBox);
        // This layer's own overflow clip, combined with the overflow clips of any
        // non-layer boxes between it and its parent layer (inheritedClip). A
        // promoted descendant of an `overflow:hidden` card must stay clipped to the
        // card even though the card itself is not a layer — otherwise the promoted
        // slice escapes the card on composite (and on hover refresh).
        var clip = IntersectClip(inheritedClip, EffectiveClip(layerBox, frameX, frameY));

        // Descendant layer roots whose nearest enclosing layer is THIS one
        // become our child layers. They are found by descending into our
        // children's content boxes and stopping the descent at each layer root.
        // Their inherited clip starts empty: this layer's own clip is propagated to
        // them at composite time (CollectOps intersects ancestorClip with layer.Clip).
        var children = new List<(CompositorLayer Layer, int? ZIndex, int Order)>();
        var contentOriginX = frameX + layerBox.Border.Left + layerBox.Padding.Left;
        var contentOriginY = frameY + layerBox.Border.Top + layerBox.Padding.Top;
        foreach (var child in layerBox.Children)
        {
            CollectChildLayers(child, contentOriginX, contentOriginY, children, inheritedClip: null);
        }

        // CSS-Position-3 §9 painting order: negative z-index below, then
        // auto/0 in tree order, then positive z-index — a stable sort by the
        // (effective z, tree order) key. `auto` is treated as 0 for ordering
        // among siblings within this stacking context.
        var ordered = children
            .OrderBy(c => c.ZIndex ?? 0)
            .ThenBy(c => c.Order)
            .Select(c => c.Layer)
            .ToList();

        // Content hash of the slice (LTF-02). The slice already excludes this
        // layer's own transform/opacity (suppressed above / applied at composite),
        // so a transform/opacity-only frame produces an identical hash and the
        // layer re-blits from cache; only a real content change re-rasters it.
        // The root's filter chain is excluded from the slice the same way, so it
        // is folded back into the hash here — an animating blur radius or a
        // hover brightness must still re-raster the layer.
        var contentHash = DisplayListContentHash.Compute(slice);
        if (rootFilters is not null)
        {
            contentHash = DisplayListContentHash.Combine(contentHash, DisplayListContentHash.ComputeFilters(rootFilters));
        }

        return new CompositorLayer(slice, bounds, transform ?? Matrix2D.Identity, opacity, clip, ordered,
            contentHash: contentHash, layerId: _layerIdFor?.Invoke(layerBox) ?? 0,
            sourceBox: layerBox, originParentX: parentOriginX, originParentY: parentOriginY,
            zIndex: ZIndexOf(layerBox) ?? 0, inheritedClip: inheritedClip, filters: rootFilters,
            backdropFilters: TryGetBackdropFilters(layerBox),
            backdropBounds: new Rect(frameX, frameY, layerBox.Frame.Width, layerBox.Frame.Height));
    }

    /// <summary>
    /// Resolves the layer-root box's <c>backdrop-filter</c> chain (Filter
    /// Effects 2 §6), or null when none applies. Carried on the layer (like
    /// <see cref="CompositorLayer.Filters"/>) so the compositor can snapshot
    /// and filter the already-composited backdrop before this layer's content
    /// paints — the slice's own <c>DrawBackdropFilter</c> item only ever sees
    /// the layer's transparent local canvas.
    /// </summary>
    private IReadOnlyList<FilterFunction>? TryGetBackdropFilters(Box box)
    {
        if (box.Frame.Width <= 0 || box.Frame.Height <= 0)
        {
            return null;
        }

        var style = _styleOverride?.Invoke(box) ?? box.Style;
        var raw = style?.Get(PropertyId.BackdropFilter);
        if (raw is null or CssKeyword)
        {
            return null; // none — the initial value
        }

        return DisplayListBuilder.ResolveFilterFunctions(raw, style!.GetColor(PropertyId.Color));
    }

    /// <summary>
    /// Produces a refreshed copy of a previously-built layer tree for an animation
    /// frame, rebuilding only the nodes whose box is actively animating (per
    /// <see cref="_isAnimatingLayerRoot"/>) and reusing every static node as-is.
    /// </summary>
    /// <remarks>
    /// The caller guarantees the frame is a pure animation tick: the DOM, layout,
    /// viewport, scale, and the set of animating layer roots are all unchanged since
    /// <paramref name="cached"/> was built (only the animation clock advanced). Under
    /// that guarantee every static layer's slice — and therefore its content hash —
    /// is byte-identical to last frame, so reusing it is exact. An animating node is
    /// rebuilt in full so a transform / opacity / colour change is sampled fresh; its
    /// slice still hashes identically for a transform/opacity-only change, so the tile
    /// cache re-blits rather than re-rasters. This skips the per-frame rebuild + re-hash
    /// of the whole document, which is the allocation churn that otherwise pins the GC.
    /// </remarks>
    public CompositorLayer RefreshAnimating(CompositorLayer cached)
    {
        ArgumentNullException.ThrowIfNull(cached);
        return Refresh(cached);
    }

    private CompositorLayer Refresh(CompositorLayer node)
    {
        // An animating layer root is rebuilt in full (slice, transform, opacity,
        // hash, and its own subtree) so the fresh animation sample takes effect.
        if (node.SourceBox is { } box && (_isAnimatingLayerRoot?.Invoke(box) ?? false))
        {
            return BuildLayer(box, node.OriginParentX, node.OriginParentY, node.InheritedClip);
        }

        // Static node: reuse its slice/hash unchanged, but recurse so an animating
        // descendant layer still refreshes.
        var children = node.Children;
        List<CompositorLayer>? refreshed = null;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            var r = Refresh(child);
            if (!ReferenceEquals(r, child))
            {
                if (refreshed is null)
                {
                    refreshed = new List<CompositorLayer>(children.Count);
                    for (var j = 0; j < i; j++)
                    {
                        refreshed.Add(children[j]);
                    }
                }
            }
            refreshed?.Add(r);
        }

        return refreshed is null ? node : node.WithChildren(refreshed);
    }

    /// <summary>
    /// Descends from <paramref name="box"/> looking for the next layer roots.
    /// When a layer root is found it is built (and its own subtree handled by the
    /// recursive <see cref="BuildLayer"/>); otherwise we keep descending into its
    /// children, so a layer root nested several plain boxes deep still attaches
    /// to the correct ancestor layer.
    /// </summary>
    private void CollectChildLayers(Box box, double parentOriginX, double parentOriginY,
        List<(CompositorLayer, int?, int)> sink, Rect? inheritedClip)
    {
        var frameX = parentOriginX + box.Frame.X;
        var frameY = parentOriginY + box.Frame.Y;

        if (IsLayerRoot(box))
        {
            var layer = BuildLayer(box, parentOriginX, parentOriginY, inheritedClip);
            sink.Add((layer, ZIndexOf(box), sink.Count));
            return; // The nested layer owns everything below it.
        }

        // A non-layer box with `overflow:hidden` clips its subtree. Carry that clip
        // down so a promoted descendant layer inherits it (the clip lives only in
        // this slice's PushClip/PopClip, which a separate child layer never sees).
        var childInheritedClip = IntersectClip(inheritedClip, EffectiveClip(box, frameX, frameY));
        var contentOriginX = frameX + box.Border.Left + box.Padding.Left;
        var contentOriginY = frameY + box.Border.Top + box.Padding.Top;
        foreach (var child in box.Children)
        {
            CollectChildLayers(child, contentOriginX, contentOriginY, sink, childInheritedClip);
        }
    }

    /// <summary>Intersects two page-space clip rects; null means "no clip".</summary>
    private static Rect? IntersectClip(Rect? a, Rect? b)
    {
        if (a is null)
        {
            return b;
        }

        if (b is null)
        {
            return a;
        }

        var x = Math.Max(a.Value.X, b.Value.X);
        var y = Math.Max(a.Value.Y, b.Value.Y);
        var right = Math.Min(a.Value.X + a.Value.Width, b.Value.X + b.Value.Width);
        var bottom = Math.Min(a.Value.Y + a.Value.Height, b.Value.Y + b.Value.Height);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    private static Rect UnionBounds(PaintList slice)
    {
        var any = false;
        double minX = 0, minY = 0, maxX = 0, maxY = 0;
        var transform = Matrix2D.Identity;
        var stack = new Stack<Matrix2D>();
        stack.Push(Matrix2D.Identity);

        foreach (var item in slice.Items)
        {
            switch (item)
            {
                case PushTransform push:
                    transform = transform.Multiply(push.Matrix);
                    stack.Push(transform);
                    continue;
                case PopTransform:
                    if (stack.Count > 1)
                    {
                        stack.Pop();
                    }

                    transform = stack.Peek();
                    continue;
            }

            if (!DisplayItemBounds.TryGet(item, out var local))
            {
                continue;
            }

            var aabb = TransformedAabb(local, transform);
            if (!any)
            {
                minX = aabb.X; minY = aabb.Y; maxX = aabb.Right; maxY = aabb.Bottom;
                any = true;
            }
            else
            {
                minX = Math.Min(minX, aabb.X);
                minY = Math.Min(minY, aabb.Y);
                maxX = Math.Max(maxX, aabb.Right);
                maxY = Math.Max(maxY, aabb.Bottom);
            }
        }

        return any ? new Rect(minX, minY, maxX - minX, maxY - minY) : Rect.Empty;
    }

    private static Rect TransformedAabb(Rect r, Matrix2D m)
    {
        if (m.IsIdentity)
        {
            return r;
        }

        var (x0, y0) = m.Transform(r.X, r.Y);
        var (x1, y1) = m.Transform(r.X + r.Width, r.Y);
        var (x2, y2) = m.Transform(r.X + r.Width, r.Y + r.Height);
        var (x3, y3) = m.Transform(r.X, r.Y + r.Height);
        var minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
        var minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
        var maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
        var maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private float EffectiveOpacity(Box box)
    {
        var style = _styleOverride?.Invoke(box) ?? box.Style;
        return style?.Get(PropertyId.Opacity) switch
        {
            CssNumber n => (float)Math.Clamp(n.Value, 0d, 1d),
            CssPercentage p => (float)Math.Clamp(p.Value / 100d, 0d, 1d),
            _ => 1f,
        };
    }

    private Rect? EffectiveClip(Box box, double frameX, double frameY)
    {
        var style = _styleOverride?.Invoke(box) ?? box.Style;
        if (style is null)
        {
            return null;
        }
        // overflow:hidden clips the subtree to the box's frame (border box).
        // The `overflow` shorthand expands to overflow-x / overflow-y, so the
        // longhands are the carriers. Border-radius clipping is deferred (WP note).
        if (!IsOverflowHidden(style.Get(PropertyId.OverflowX))
            && !IsOverflowHidden(style.Get(PropertyId.OverflowY)))
        {
            return null;
        }

        return new Rect(frameX, frameY, box.Frame.Width, box.Frame.Height);
    }

    private static bool IsOverflowHidden(CssValue? value) => value switch
    {
        CssKeyword { Name: var n } => n.Equals("hidden", StringComparison.OrdinalIgnoreCase)
            || n.Equals("clip", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private int? ZIndexOf(Box box)
    {
        var style = _styleOverride?.Invoke(box) ?? box.Style;
        return style?.Get(PropertyId.ZIndex) switch
        {
            CssNumber n => (int)n.Value,
            CssLength len => (int)len.Value,
            _ => null, // auto → ordered as 0 among siblings (tree order tiebreak)
        };
    }
}
