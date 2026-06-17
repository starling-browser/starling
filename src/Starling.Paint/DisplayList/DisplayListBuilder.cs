using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Dom;
using Starling.Layout;
using Starling.Layout.Box;
using Starling.Layout.Text;
using Starling.Layout.Tree;

namespace Starling.Paint.DisplayList;

/// <summary>
/// Walks a laid-out box tree and emits a display list in paint order.
/// </summary>
/// <remarks>
/// Emits paint items for backgrounds, borders, images, text, shadows,
/// transforms, scroll clips, and compositor layer slices. The compositor can
/// split the same display list into cached layers without changing the paint
/// commands.
/// </remarks>
public sealed class DisplayListBuilder
{
    /// <summary>
    /// Overdraw margin (CSS px) added around the viewport before culling.
    /// Items whose AABB falls within this margin of the visible region are
    /// still emitted so partial-pixel scroll and sub-pixel rounding don't pop
    /// content at the edges.
    /// </summary>
    private const double OverdrawMargin = 64d;

    /// <summary>UA default ::placeholder gray — what Chromium uses
    /// (#757575) on a light scheme; distinct from the #767676 control border
    /// only by happenstance, both read as "muted".</summary>
    private static readonly CssColor PlaceholderColor = new(117, 117, 117, 255);

    /// <summary>Host abort signal — see Painter.LayoutDocumentWithStyle.
    /// Observed by the box-tree walk so a Stop arriving during paint of a very
    /// large page unwinds between sibling boxes instead of running to completion.</summary>
    private readonly CancellationToken _abort;

    /// <summary>
    /// Page-coordinate origin of the current viewport (the scroll offset), or
    /// null when no viewport is set (full-page paint). Used to anchor
    /// <c>position: fixed</c> subtrees to the viewport instead of the page —
    /// the layout pass writes their frame in viewport-relative coordinates
    /// against the initial containing block, and the painter translates that
    /// by (viewport.X, viewport.Y) so the box paints at the right page coord
    /// for the current scroll position.
    /// </summary>
    private double _viewportX;
    private double _viewportY;
    private bool _hasViewport;

    /// <summary>
    /// Lookup that, for a given Element, returns its current local scroll
    /// offset inside an <c>overflow: scroll | auto</c> container. The painter
    /// emits a translation transform of <c>(-X, -Y)</c> around the container's
    /// children so the scrolled-to portion lands inside the container's
    /// (cull-clipped) frame. Null when the host doesn't track per-element
    /// scrolling (tests / headless renders), in which case every container is
    /// painted at offset (0, 0).
    /// </summary>
    private Func<Element, (double X, double Y)>? _scrollOffsets;

    /// <summary>
    /// Per-element <c>position: sticky</c> paint shift, read from the same
    /// scroll store the offsets come from (<c>ScrollStateStore.GetStickyShift</c>
    /// shape — browser-plan/scroll-model.md WP5). Layout keeps sticky frames
    /// natural; this builder offsets the box's paint origin by the shift, so
    /// the whole subtree (and any CSS transform bracket, whose origin derives
    /// from the shifted frame) composes with it. Null when the host has no
    /// scroll model — sticky then arrived pre-shifted by the layout fallback.
    /// </summary>
    private Func<Element, (double X, double Y)>? _stickyShifts;

    // CSS 2.1 §14.2 / CSS Backgrounds 3 §2.11.2 — the box whose
    // background-color was promoted to the canvas for this Build pass. Its own
    // background-color paint is suppressed so translucent colors are not
    // blended twice. Null when no canvas rect was supplied or no box donates.
    private Box? _canvasDonor;

    public DisplayListBuilder() : this(CancellationToken.None) { }

    public DisplayListBuilder(CancellationToken abort)
    {
        _abort = abort;
    }

    /// <summary>
    /// Builds a display list from a laid-out box tree (no culling — every item
    /// is emitted). <paramref name="styleOverride"/> is an optional per-box
    /// hook used to swap in fresh styles without re-laying out — interactive
    /// shells call it with hover/focus-recascaded styles so
    /// <c>a:hover { color: red }</c> repaints in red at the same glyph
    /// positions. Returning <c>null</c> for a box keeps the layout-time
    /// <see cref="Box.Style"/>.
    /// </summary>
    public DisplayList Build(BlockBox root, Func<Box, ComputedStyle?>? styleOverride = null, IImageResolver? images = null)
        => Build(root, viewport: null, styleOverride, images, scrollOffsets: null);

    /// <summary>
    /// Builds a display list, optionally culling to a page-coordinate
    /// <paramref name="viewport"/>. When <paramref name="viewport"/> is
    /// non-null only items whose (post-transform) page-coordinate AABB
    /// intersects the viewport (expanded by <see cref="OverdrawMargin"/>) are
    /// emitted, and transformed subtrees with no surviving items skip their
    /// <see cref="PushTransform"/>/<see cref="PopTransform"/> bracket entirely
    /// so the cost stays O(items on screen). When null, every item is emitted
    /// (the full-page behavior the headless screenshot path relies on).
    /// </summary>
    public DisplayList Build(BlockBox root, Rect? viewport, Func<Box, ComputedStyle?>? styleOverride = null, IImageResolver? images = null, Func<Element, (double X, double Y)>? scrollOffsets = null, Rect? canvasRect = null, Func<Element, (double X, double Y)>? stickyShifts = null)
    {
        _stickyShifts = stickyShifts;
        ArgumentNullException.ThrowIfNull(root);
        var list = new DisplayList();
        _canvasDonor = null;
        if (canvasRect is { Width: > 0, Height: > 0 } canvas)
        {
            // CSS 2.1 §14.2 — the root element's background paints the whole
            // canvas; an <html> root with no background borrows the <body>'s,
            // and the donor must not paint that background again on its own
            // box. Color-only at this fidelity (background-image stays on the
            // donor box).
            var donor = FindCanvasBackgroundDonor(root, styleOverride);
            if (donor is not null)
            {
                var canvasColor = EffectiveStyle(donor, styleOverride)!.GetColor(PropertyId.BackgroundColor);
                list.Add(new FillRect(canvas, canvasColor, FillRectPixelAlignment.Preserve));
                _canvasDonor = donor;
            }
        }
        Rect? cull = viewport is { } v
            ? new Rect(v.X - OverdrawMargin, v.Y - OverdrawMargin, v.Width + 2 * OverdrawMargin, v.Height + 2 * OverdrawMargin)
            : null;
        if (viewport is { } vp)
        {
            _viewportX = vp.X;
            _viewportY = vp.Y;
            _hasViewport = true;
        }
        else
        {
            _hasViewport = false;
        }
        _scrollOffsets = scrollOffsets;
        Visit(box: root, list, originX: 0, originY: 0, current: Matrix2D.Identity, cull, styleOverride, images, slice: null);
        return list;
    }

    /// <summary>
    /// Builds the display-list <em>slice</em> for a single compositor layer
    /// rooted at <paramref name="sliceRoot"/>: the items <paramref name="sliceRoot"/>
    /// and its subtree paint, EXCLUDING any box (other than the slice root
    /// itself) for which <paramref name="isLayerBoundary"/> returns true — those
    /// boxes are descendant layers and the <see cref="Compositor.LayerTreeBuilder"/>
    /// emits them into their own slices. <paramref name="originX"/> /
    /// <paramref name="originY"/> are the page-coord content origin of the slice
    /// root's PARENT (i.e. the same origin its enclosing <see cref="Visit"/> call
    /// would have used), so a slice is painted in the SAME page coordinate space
    /// as the flat build — the layer's transform/opacity are applied later, at
    /// composite time, not baked into the slice.
    /// <para>
    /// When <paramref name="suppressRootTransform"/> is true the slice root's own
    /// CSS <c>transform</c> bracket is skipped: the layer carries that transform
    /// (<see cref="Compositor.CompositorLayer.Transform"/>) and applies it at
    /// composite time, so the slice holds the untransformed (upright) content.
    /// Nested non-promoted transformed descendants inside the slice still get
    /// their normal push/pop brackets.
    /// </para>
    /// </summary>
    internal DisplayList BuildLayerSlice(
        Box sliceRoot,
        double originX,
        double originY,
        Func<Box, bool> isLayerBoundary,
        bool suppressRootTransform,
        out IReadOnlyList<FilterFunction>? rootFilters,
        Func<Box, ComputedStyle?>? styleOverride = null,
        IImageResolver? images = null,
        Func<Element, (double X, double Y)>? scrollOffsets = null,
        Func<Element, (double X, double Y)>? stickyShifts = null)
    {
        ArgumentNullException.ThrowIfNull(sliceRoot);
        ArgumentNullException.ThrowIfNull(isLayerBoundary);
        var list = new DisplayList();
        // Per-container scroll offsets so a slice that contains an overflow:scroll
        // subtree paints it at the user-scrolled position (the Visit recursion
        // brackets the scrolled children in a -offset transform). Lets the zero-copy
        // surface path render inner-scrolled pages instead of declining to readback.
        _scrollOffsets = scrollOffsets;
        _stickyShifts = stickyShifts;
        // Layer slices never own the canvas; clear any donor left by a prior
        // Build call on a reused builder so no slice drops its background.
        _canvasDonor = null;
        var slice = new LayerSlice(sliceRoot, isLayerBoundary);
        // Like the root transform, the slice root's `filter` chain is owned by its
        // layer (CompositorLayer.Filters) and applied once at composite time, so
        // the bracket Visit would emit for it is suppressed: the slice holds the
        // unfiltered content. Nested non-promoted filtered descendants inside the
        // slice still get their normal brackets.
        rootFilters = TryGetFilterFunctions(sliceRoot, styleOverride);
        _suppressFilterBracketRoot = rootFilters is not null ? sliceRoot : null;
        try
        {
            if (suppressRootTransform)
            {
                // The slice root's transform is owned by its layer, so paint its
                // own box + children directly (no transform bracket) in local space.
                var frameX = originX + sliceRoot.Frame.X;
                var frameY = originY + sliceRoot.Frame.Y;
                PaintBoxAndChildren(sliceRoot, list, frameX, frameY, Matrix2D.Identity, cull: null, styleOverride, images, slice);
            }
            else
            {
                Visit(sliceRoot, list, originX, originY, Matrix2D.Identity, cull: null, styleOverride, images, slice);
            }
        }
        finally
        {
            _suppressFilterBracketRoot = null;
        }
        return list;
    }

    // The slice root whose `filter` bracket Visit must NOT emit during the
    // current BuildLayerSlice call — the chain is carried on the CompositorLayer
    // instead. Null outside slice builds and for filterless roots.
    private Box? _suppressFilterBracketRoot;

    /// <summary>
    /// Threaded through the recursion during a layer-slice build. Carries the
    /// slice root (which is always emitted even though it is itself a layer
    /// boundary) and the predicate identifying descendant layer boundaries to
    /// exclude. Null on the flat full-document build, where every box is emitted.
    /// </summary>
    private sealed record LayerSlice(Box Root, Func<Box, bool> IsBoundary);

    /// <summary>
    /// CSS 2.1 §14.2 — picks the box whose background-color becomes the
    /// canvas background: the root element when it has one, otherwise the
    /// root's <c>&lt;body&gt;</c> child when the root is <c>&lt;html&gt;</c>.
    /// Returns null when neither donates a visible color.
    /// </summary>
    private static Box? FindCanvasBackgroundDonor(Box root, Func<Box, ComputedStyle?>? styleOverride)
    {
        if (EffectiveStyle(root, styleOverride) is { } rootStyle
            && rootStyle.GetColor(PropertyId.BackgroundColor).A > 0)
        {
            return root;
        }

        if (root.Element is not { } rootEl
            || !string.Equals(rootEl.TagName, "html", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var children = root.Children;
        for (var i = 0; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Element is not { } childEl
                || !string.Equals(childEl.TagName, "body", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            return EffectiveStyle(child, styleOverride) is { } bodyStyle
                && bodyStyle.GetColor(PropertyId.BackgroundColor).A > 0
                    ? child
                    : null;
        }
        return null;
    }

    private static ComputedStyle? EffectiveStyle(Box box, Func<Box, ComputedStyle?>? styleOverride)
        => styleOverride?.Invoke(box) ?? box.Style;

    /// <summary>
    /// Whether this box paints its own content (CSS 2.2 §11.2). `visibility:
    /// hidden`/`collapse` suppresses the box's own background, border, text and
    /// replaced content; children are unaffected (they may flip back to
    /// `visible`). Boxes with no style (rare) are treated as visible.
    /// </summary>
    private static bool IsSelfVisible(Box box, Func<Box, ComputedStyle?>? styleOverride)
        => EffectiveStyle(box, styleOverride) is not { } style
           || style.Get(PropertyId.Visibility) is not CssKeyword k
           || k.Name.Equals("visible", StringComparison.OrdinalIgnoreCase);

    private static bool IsFixedPositioned(Box box, Func<Box, ComputedStyle?>? styleOverride)
        => EffectiveStyle(box, styleOverride) is { } style
           && style.Get(PropertyId.Position) is CssKeyword k
           && k.Name.Equals("fixed", StringComparison.OrdinalIgnoreCase);

    private static bool IsStickyPositioned(Box box, Func<Box, ComputedStyle?>? styleOverride)
        => EffectiveStyle(box, styleOverride) is { } style
           && style.Get(PropertyId.Position) is CssKeyword k
           && k.Name.Equals("sticky", StringComparison.OrdinalIgnoreCase);

    private void Visit(Box box, DisplayList list, double originX, double originY, Matrix2D current, Rect? cull, Func<Box, ComputedStyle?>? styleOverride, IImageResolver? images, LayerSlice? slice)
    {
        // Host abort (Stop button, navigation supersede). One check per visited
        // box keeps the cancellation latency bounded across a deep DOM without
        // touching the inner-emit hot loops below.
        _abort.ThrowIfCancellationRequested();

        // Layer-slice mode: a descendant that is itself a layer root paints into
        // its OWN slice, not this one. The slice root is exempt — it is the box
        // this slice is rooted at, so it must paint here.
        if (slice is not null && !ReferenceEquals(box, slice.Root) && slice.IsBoundary(box))
        {
            return;
        }

        var frameX = originX + box.Frame.X;
        var frameY = originY + box.Frame.Y;

        // `position: fixed` anchors the box to the initial containing block (the
        // viewport). Layout writes the resolved frame in viewport-relative
        // coordinates — e.g. `inset-block-start: 0` becomes Y=0 in the page —
        // but when the user scrolls the painter is asked for a viewport whose
        // page-Y is non-zero. Adding the viewport origin here shifts the entire
        // fixed subtree onto the page coords the painter is sampling, so the
        // box stays glued to the viewport edge regardless of scroll position.
        // Skipped when no viewport is set (full-page screenshots) so headless
        // captures keep their pre-existing "fixed boxes at Y=0" output.
        if (_hasViewport && IsFixedPositioned(box, styleOverride))
        {
            frameX += _viewportX;
            frameY += _viewportY;
        }

        // `position: sticky` (scroll-model.md WP5): layout left the frame at
        // its natural position; the bound scroller's live offset turns into a
        // pure paint-time translation here, exactly like the scroll offset.
        // Offsetting the paint origin shifts the box AND its whole subtree
        // (children inherit the content origin), and composes with any
        // enclosing transform bracket because items inside it are emitted in
        // these coordinates. One keyword test per element box, one store read
        // per sticky box — nothing on the sticky-free path.
        if (_stickyShifts is not null && box.Element is { } stickyElement
            && IsStickyPositioned(box, styleOverride))
        {
            var stickyShift = _stickyShifts(stickyElement);
            frameX += stickyShift.X;
            frameY += stickyShift.Y;
        }

        // CSS `transform` is applied around the box's transform-origin (default
        // 50% 50% 0). Layout is unaffected: the box keeps its frame coordinates
        // and paint composes T(+origin) × M × T(-origin) on top of every
        // primitive in this subtree (paint of this box and every descendant).
        var transformMatrix = TryGetTransformMatrix(box, styleOverride, frameX, frameY);
        var transformed = transformMatrix is not null;

        // Filter Effects 1 §10 — `filter` renders the element AND its descendants
        // as a group: the subtree is collected into a scratch list and wrapped in
        // a PushFilter/PopFilter bracket so the backend can composite it
        // offscreen, run the chain, and blit the result. The filter bracket sits
        // INSIDE the transform bracket: the spec filters the element's local
        // rendering, then the transform maps the filtered result. A slice root's
        // own chain is suppressed — its CompositorLayer carries it instead.
        var filterFns = ReferenceEquals(box, _suppressFilterBracketRoot)
            ? null
            : TryGetFilterFunctions(box, styleOverride);

        if (transformed || filterFns is not null)
        {
            // Items inside the transform must be culled using their
            // post-transform AABB, so compose the matrix onto the active one
            // and paint the subtree into a scratch list. Only if it produced
            // visible items do we emit the bracket — an entirely off-screen
            // transformed subtree contributes nothing and stays O(on-screen).
            var composed = transformed ? current.Multiply(transformMatrix!.Value) : current;
            var scratch = new DisplayList();
            PaintBoxAndChildren(box, scratch, frameX, frameY, composed, cull, styleOverride, images, slice);
            if (scratch.Items.Count > 0)
            {
                if (transformed)
                {
                    list.Add(new PushTransform(transformMatrix!.Value));
                }

                if (filterFns is not null)
                {
                    list.Add(new PushFilter(new Rect(frameX, frameY, box.Frame.Width, box.Frame.Height), filterFns));
                }

                foreach (var item in scratch.Items)
                {
                    list.Add(item);
                }

                if (filterFns is not null)
                {
                    list.Add(PopFilter.Instance);
                }

                if (transformed)
                {
                    list.Add(PopTransform.Instance);
                }
            }
            return;
        }

        PaintBoxAndChildren(box, list, frameX, frameY, current, cull, styleOverride, images, slice);
    }

    /// <summary>
    /// Paints <paramref name="box"/>'s children in paint order. With a scroll
    /// model attached (<see cref="_stickyShifts"/> non-null), sticky children
    /// are hoisted to paint AFTER their in-flow siblings — CSS 2.1 Appendix E:
    /// positioned descendants with <c>z-index: auto</c> paint above the
    /// in-flow layer, which is what keeps a stuck header visible over the
    /// content that scrolls under it. Without a scroll model the loop is the
    /// plain tree-order walk, byte-identical to the pre-WP5 painter.
    /// </summary>
    private void VisitChildren(Box box, DisplayList list, double contentOriginX, double contentOriginY, Matrix2D current, Rect? cull, Func<Box, ComputedStyle?>? styleOverride, IImageResolver? images, LayerSlice? slice)
    {
        if (_stickyShifts is null)
        {
            foreach (var child in box.Children)
            {
                Visit(child, list, contentOriginX, contentOriginY, current, cull, styleOverride, images, slice);
            }

            return;
        }

        var anySticky = false;
        foreach (var child in box.Children)
        {
            if (child.Element is not null && IsStickyPositioned(child, styleOverride))
            {
                anySticky = true;
                continue;
            }
            Visit(child, list, contentOriginX, contentOriginY, current, cull, styleOverride, images, slice);
        }
        if (!anySticky)
        {
            return;
        }

        foreach (var child in box.Children)
        {
            if (child.Element is not null && IsStickyPositioned(child, styleOverride))
            {
                Visit(child, list, contentOriginX, contentOriginY, current, cull, styleOverride, images, slice);
            }
        }
    }

    /// <summary>
    /// Adds <paramref name="item"/> to <paramref name="list"/> unless culling is
    /// active and the item's page-coordinate AABB (the local <paramref name="bounds"/>
    /// transformed by <paramref name="current"/>) does not intersect the
    /// viewport.
    /// </summary>
    private static void Emit(DisplayList list, DisplayItem item, Rect bounds, Matrix2D current, Rect? cull)
    {
        if (cull is { } c && !Intersects(TransformedAabb(bounds, current), c))
        {
            return;
        }

        list.Add(item);
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

    private static bool Intersects(Rect a, Rect b)
        => a.X < b.Right && a.Right > b.X && a.Y < b.Bottom && a.Bottom > b.Y;

    private void PaintBoxAndChildren(Box box, DisplayList list, double frameX, double frameY, Matrix2D current, Rect? cull, Func<Box, ComputedStyle?>? styleOverride, IImageResolver? images, LayerSlice? slice)
    {
        // Backgrounds and borders for box-bearing boxes. Only BlockContainer
        // qualifies unconditionally; InlineBox qualifies only when it is an
        // atomic inline (display:inline-block) — flattened spans have a zero
        // frame and would otherwise emit a phantom rect at the origin.
        //
        // AnonymousBlock must NOT paint decorations: per CSS 2.2 §9.2.2.1 an
        // anonymous box inherits only inheritable properties from its enclosing
        // box; non-inherited ones (background, border, box-shadow, …) take their
        // initial values. Our AnonymousBlockBox carries the *parent's* full
        // ComputedStyle (for text/font inheritance), so painting its background
        // here re-painted the parent's — visible as a brighter rectangle behind
        // the text whenever that background was semi-transparent (e.g. a pill or
        // search field with rgba()/#rrggbbaa fill).
        // CSS 2.2 §11.2 — `visibility: hidden`/`collapse` makes a box invisible
        // (no background, border, text, or replaced content) while it still
        // takes part in layout. It is inherited, but a descendant may flip back
        // to `visible`, so we suppress only THIS box's own painting and always
        // recurse into children.
        var selfVisible = IsSelfVisible(box, styleOverride);

        var hasFrame = box.Frame.Width > 0 && box.Frame.Height > 0;
        var paintsBox = selfVisible
            && (box.Kind is BoxKind.BlockContainer
                || (box.Kind == BoxKind.Inline && hasFrame));

        // CSS Masking 1 §5 — when the box carries a paintable mask-image the
        // entire element (background + border + content + descendants) is
        // composited into an offscreen surface and then masked. Per the spec,
        // box-shadows paint OUTSIDE the masked group (they are not affected by
        // the element's own mask), so they are emitted first on the main list
        // and the PushMask bracket only wraps the box-interior paint.
        //
        // To wire this up: detect the mask here; emit outer shadows on the main
        // list; collect box-interior + children into `innerList`; wrap with
        // PushMask / PopMask and flush to the main list at the end.
        MaskGeometry? maskGeometry = null;
        if (paintsBox && EffectiveStyle(box, styleOverride) is { } maskCheckStyle)
        {
            maskGeometry = TryResolveMaskGeometry(box, frameX, frameY, maskCheckStyle, images);
        }

        // CSS Masking 1 §7 — clip-path clips the ENTIRE element rendering
        // (background + border + content + descendants), unlike overflow which
        // clips only descendants. Detect a non-none, non-url clip-path here and,
        // when present, wrap all interior paint in PushClipPath/PopClipPath.
        // box-shadows are NOT clipped by clip-path (they are outside the element).
        CssClipPath? clipPathValue = null;
        if (paintsBox && EffectiveStyle(box, styleOverride) is { } clipCheckStyle)
        {
            clipPathValue = TryResolveClipPath(clipCheckStyle);
        }

        // When there is a mask, innerList is a scratch list that collects all
        // box-interior paint (background + border + content + descendants).
        // When there is no mask, innerList == list — no extra allocation.
        var innerList = maskGeometry is not null ? new DisplayList() : list;

        // When clip-path applies, we need yet another scratch layer to collect
        // the items that go inside the clip bracket. The layering from outermost
        // to innermost is: list → [PushMask → innerList → [PushClipPath → clipList]].
        // When there is no mask, innerList == list, so the bracket collapses cleanly.
        DisplayList? clipList = null;
        if (clipPathValue is not null)
        {
            clipList = new DisplayList();
        }

        // activeList is the target for all box-interior paint (backgrounds, borders,
        // content, children). When clip-path is active it is the clipList scratch;
        // otherwise it is innerList (which may itself be a mask scratch or == list).
        var activeList = clipList ?? innerList;

        if (paintsBox && EffectiveStyle(box, styleOverride) is { } style)
        {
            var bounds = new Rect(frameX, frameY, box.Frame.Width, box.Frame.Height);

            // CSS Backgrounds 3 §5 — the four corner radii (clamped to the box
            // per §5.1 so overlapping radii never exceed the box dimensions).
            var radii = ReadCornerRadii(style, box.Frame.Width, box.Frame.Height);

            // Filter Effects 2 §6 — backdrop-filter. The filtered backdrop patch
            // paints first, beneath everything the element itself paints; the
            // backend snapshots the canvas under the border box, filters it, and
            // draws it back clipped to the (rounded) border box. Emitted on the
            // OUTER list: inside a mask/clip-path scratch layer the "canvas"
            // would be the transparent offscreen, not the real backdrop.
            EmitBackdropFilter(bounds, radii, list, current, cull, style);

            // CSS Backgrounds 3 §6 — box-shadow. Outer shadows paint BEHIND the
            // box (before background + border), in reverse list order so the
            // first listed layer ends up on top. Inset shadows are parsed but
            // painted best-effort (the outer drop shadow is the supported path).
            // When the box is masked, shadows still paint on the MAIN list (they
            // are not part of the masked group per CSS Masking 1 §5).
            // Per CSS Masking 1 §7.2 box-shadow also paints OUTSIDE clip-path.
            EmitBoxShadows(box, bounds, radii, list, current, cull, style);

            // CSS Backgrounds 3 §3.8 — `background-clip: text`. The background
            // (gradient or solid color) is clipped to the union of the
            // element's text glyphs instead of filling the box. Detect it here
            // and, when present, emit a single glyph-clipped fill in place of
            // the normal background fill + background-image gradient.
            if (IsTextClip(style) && TryEmitBackgroundTextClip(box, frameX, frameY, activeList, current, cull, style, styleOverride))
            {
                // Inset shadows still paint above the (glyph-clipped)
                // background and below the borders, which paint normally; only
                // the background fill was diverted to the glyph-clipped item.
                EmitInsetBoxShadows(box, bounds, radii, activeList, current, cull, style);
                EmitBorders(box, frameX, frameY, activeList, current, cull, style, radii);
            }
            else
            {
                var bg = style.GetColor(PropertyId.BackgroundColor);
                if (bg is { A: > 0 } && !ReferenceEquals(box, _canvasDonor))
                {
                    // CSS Backgrounds 3 §2.4 — background-color is clipped by the
                    // bottom (last) layer's background-clip box, with the corner
                    // radii corrected to that box's inner edge.
                    var (colorRect, colorRadii) = ResolveBackgroundPaintBox(
                        box, bounds, radii, LastLayerValue(style.Get(PropertyId.BackgroundClip)), "border-box");
                    if (colorRect.Width > 0 && colorRect.Height > 0)
                    {
                        if (colorRadii.IsZero)
                        {
                            Emit(activeList, new FillRect(colorRect, bg, FillRectPixelAlignment.Preserve), colorRect, current, cull);
                        }
                        else
                        {
                            Emit(activeList, new FillRoundedRect(colorRect, colorRadii, bg), colorRect, current, cull);
                        }
                    }
                }

                // CSS Backgrounds 3 §3 — background-image paints inside the
                // box's padding box (border-box is the default origin per spec,
                // but at this layout fidelity the padding+border distinction
                // is unobservable and using the frame is correct).
                EmitBackgroundImage(box, frameX, frameY, activeList, current, cull, style, images, radii);

                // CSS Backgrounds 3 §6 — inset box-shadow layers paint ABOVE
                // the background (color + images) and BELOW the border stroke.
                EmitInsetBoxShadows(box, bounds, radii, activeList, current, cull, style);

                // Borders. Painter renders one stroke per side that has a non-zero width.
                EmitBorders(box, frameX, frameY, activeList, current, cull, style, radii);
            }

            // CSS UI 4 §3 — outline: a ring painted OUTSIDE the border edge,
            // taking no layout space. Emitted here, before any descendant
            // overflow PushClip bracket opens, so the element's own overflow
            // clip never crops its outline (ancestor clips still apply).
            EmitOutline(box, frameX, frameY, activeList, current, cull, style, radii);

            // HTML form-control chrome — checkbox check mark, radio dot,
            // select chevron. Painted above background/border and below
            // children (the select's option label paints later, to the left
            // of the right-aligned chevron).
            EmitFormControlChrome(box, frameX, frameY, activeList, current, cull, style);
        }

        // Inline content: text fragments live on TextBoxes, positioned in their
        // anonymous-block parent's content box.
        if (box is TextBox textBox)
        {
            if (selfVisible)
            {
                EmitTextFragments(textBox, frameX, frameY, activeList, current, cull, styleOverride);
            }

            FlushClipPathBracket(innerList, activeList, clipPathValue, new Rect(frameX, frameY, box.Frame.Width, box.Frame.Height));
            if (maskGeometry is not null)
            {
                FlushMaskBracket(list, innerList, maskGeometry);
            }

            return; // Text boxes have no children.
        }

        // Replaced inline content (currently <img>): its Frame was set by the
        // inline formatting context relative to the anonymous-block container,
        // so frameX/frameY (which already includes that translation) is its
        // document-space top-left.
        if (box is ImageBox imageBox)
        {
            if (selfVisible)
            {
                EmitReplacedImage(imageBox, frameX, frameY, activeList, current, cull, styleOverride);
            }

            FlushClipPathBracket(innerList, activeList, clipPathValue, new Rect(frameX, frameY, box.Frame.Width, box.Frame.Height));
            if (maskGeometry is not null)
            {
                FlushMaskBracket(list, innerList, maskGeometry);
            }

            return; // ImageBox has no children.
        }

        // Children paint after backgrounds. Their frames are in our content-box
        // coords, so push our padding+border origin.
        var contentOriginX = frameX + box.Border.Left + box.Padding.Left;
        var contentOriginY = frameY + box.Border.Top + box.Padding.Top;

        // A scroll container (`overflow: hidden | clip | scroll | auto` on
        // either axis) clips its descendants to its border box. We tighten
        // the cull rect (a binary optimisation that drops wholly off-screen
        // items before they reach the rasterizer) AND emit a real PushClip /
        // PopClip pair that the backend converts to a proper scissor/path
        // clip so items straddling the box edge are cropped, not painted past
        // the border. When the box also has rounded corners the clip path is
        // the rounded rectangle, implementing CSS Overflow 3 §2.4.
        var childCull = cull;
        ComputedStyle? overflowStyle = box.Kind != BoxKind.AnonymousBlock ? EffectiveStyle(box, styleOverride) : null;
        var clipsOverflow = overflowStyle is not null && ClipsOverflow(overflowStyle);

        // Per CSS Overflow 3 §2.4 a box with a rounded border also clips its
        // children to the rounded shape when overflow != visible — read the
        // radii unconditionally for the clipsOverflow path.
        CornerRadii clipRadii = CornerRadii.None;
        if (clipsOverflow)
        {
            var clipRect = new Rect(frameX, frameY, box.Frame.Width, box.Frame.Height);
            childCull = cull is { } c ? IntersectRect(c, clipRect) : clipRect;

            // Include border-radius in the clip so rounded overflow boxes
            // crop their children to the rounded inner edge.
            if (overflowStyle is not null && box.Frame.Width > 0 && box.Frame.Height > 0)
            {
                clipRadii = ReadCornerRadii(overflowStyle, box.Frame.Width, box.Frame.Height);
            }
        }

        // `overflow: scroll | auto` containers carry a per-element scroll
        // offset maintained by the shell (wheel handler in WebviewPanel). The
        // painter translates this subtree by `(-X, -Y)` so the user-scrolled
        // position lands inside the (already clip-tightened) container frame.
        // The childCull stays in unscrolled page coords; the cull check
        // applies the composed matrix to each item before comparing, so the
        // visible band picks itself out automatically.
        (double X, double Y) scrollOffset = default;
        if (overflowStyle is not null
            && box.Element is { } scrollElement
            && _scrollOffsets is { } offsets
            && ScrollsOverflow(overflowStyle))
        {
            scrollOffset = offsets(scrollElement);
        }

        var refBox = new Rect(frameX, frameY, box.Frame.Width, box.Frame.Height);

        if (scrollOffset.X != 0 || scrollOffset.Y != 0)
        {
            var scrollMatrix = Matrix2D.Translate(-scrollOffset.X, -scrollOffset.Y);
            var composed = current.Multiply(scrollMatrix);
            var scratch = new DisplayList();
            VisitChildren(box, scratch, contentOriginX, contentOriginY, composed, childCull, styleOverride, images, slice);
            if (scratch.Items.Count > 0)
            {
                // Emit the clip bracket around the scrolled children so they
                // are cropped to the scroll container's border box.
                if (clipsOverflow && box.Frame.Width > 0 && box.Frame.Height > 0)
                {
                    var clipBounds = new Rect(frameX, frameY, box.Frame.Width, box.Frame.Height);
                    activeList.Add(new PushClip(clipBounds, clipRadii));
                }
                activeList.Add(new PushTransform(scrollMatrix));
                foreach (var item in scratch.Items)
                {
                    activeList.Add(item);
                }

                activeList.Add(PopTransform.Instance);
                if (clipsOverflow && box.Frame.Width > 0 && box.Frame.Height > 0)
                {
                    activeList.Add(PopClip.Instance);
                }
            }
            FlushClipPathBracket(innerList, activeList, clipPathValue, refBox);
            if (maskGeometry is not null)
            {
                FlushMaskBracket(list, innerList, maskGeometry);
            }

            return;
        }

        // Emit PushClip / children / PopClip for non-scrolling overflow clips
        // so descendants are rasterizer-cropped to this box, not just cull-dropped.
        if (clipsOverflow && box.Frame.Width > 0 && box.Frame.Height > 0)
        {
            var clipBounds = new Rect(frameX, frameY, box.Frame.Width, box.Frame.Height);
            activeList.Add(new PushClip(clipBounds, clipRadii));
            VisitChildren(box, activeList, contentOriginX, contentOriginY, current, childCull, styleOverride, images, slice);
            activeList.Add(PopClip.Instance);
            FlushClipPathBracket(innerList, activeList, clipPathValue, refBox);
            if (maskGeometry is not null)
            {
                FlushMaskBracket(list, innerList, maskGeometry);
            }

            return;
        }

        VisitChildren(box, activeList, contentOriginX, contentOriginY, current, childCull, styleOverride, images, slice);

        FlushClipPathBracket(innerList, activeList, clipPathValue, refBox);
        if (maskGeometry is not null)
        {
            FlushMaskBracket(list, innerList, maskGeometry);
        }
    }

    // ---- CSS Masking 1 §7 — clip-path helpers --------------------------------

    /// <summary>
    /// Returns the <see cref="CssClipPath"/> for the box when it is a non-none,
    /// non-url basic shape (or geometry-box-only) value. Returns null when
    /// clip-path is none, unset, or a url() reference (which is deferred).
    /// </summary>
    private static CssClipPath? TryResolveClipPath(ComputedStyle style)
    {
        var v = style.Get(PropertyId.ClipPath);
        if (v is not CssClipPath clip)
        {
            return null;
        }

        if (clip.IsNone)
        {
            return null;
        }

        if (clip.IsUrl)
        {
            return null; // url(#id) requires SVG DOM resolution — deferred
        }

        return clip;
    }

    /// <summary>
    /// When <paramref name="clipPath"/> is non-null, wraps the items accumulated
    /// in <paramref name="activeList"/> (the interior paint) with a
    /// <see cref="PushClipPath"/> / <see cref="PopClipPath"/> bracket and appends
    /// to <paramref name="target"/>. When <paramref name="clipPath"/> is null,
    /// this is a no-op (interior paint was already written directly to target).
    /// </summary>
    private static void FlushClipPathBracket(
        DisplayList target,
        DisplayList activeList,
        CssClipPath? clipPath,
        Rect referenceBox)
    {
        if (clipPath is null || ReferenceEquals(target, activeList))
        {
            return;
        }

        if (activeList.Items.Count == 0)
        {
            return; // nothing to clip
        }

        target.Add(new PushClipPath(referenceBox, clipPath));
        foreach (var item in activeList.Items)
        {
            target.Add(item);
        }

        target.Add(PopClipPath.Instance);
    }

    private static bool ScrollsOverflow(ComputedStyle style)
        => OverflowKeywordScrolls(style.Get(PropertyId.OverflowX))
        || OverflowKeywordScrolls(style.Get(PropertyId.OverflowY));

    private static bool OverflowKeywordScrolls(CssValue? value) => value switch
    {
        CssKeyword { Name: var n } =>
            n.Equals("scroll", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("auto", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool ClipsOverflow(ComputedStyle style)
        => OverflowKeywordClips(style.Get(PropertyId.OverflowX))
        || OverflowKeywordClips(style.Get(PropertyId.OverflowY));

    private static bool OverflowKeywordClips(CssValue? value) => value switch
    {
        CssKeyword { Name: var n } =>
            n.Equals("hidden", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("clip", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("scroll", StringComparison.OrdinalIgnoreCase) ||
            n.Equals("auto", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static Rect IntersectRect(Rect a, Rect b)
    {
        var x = Math.Max(a.X, b.X);
        var y = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return new Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    /// <summary>
    /// CSS Backgrounds 3 §3.8 — true when this box's <c>background-clip</c>
    /// resolves to the <c>text</c> keyword (set directly or via the
    /// <c>-webkit-background-clip: text</c> alias, both of which parse to the
    /// same keyword value).
    /// </summary>
    private static bool IsTextClip(ComputedStyle style)
        => style.Get(PropertyId.BackgroundClip) is CssKeyword k
           && k.Name.Equals("text", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// CSS Backgrounds 3 §3.8 — paint the box background clipped to its text
    /// glyphs. Resolves the background paint (a gradient when
    /// <c>background-image</c> is a paintable gradient, otherwise the solid
    /// <c>background-color</c>), gathers every descendant glyph run, and emits a
    /// single <see cref="FillBackgroundTextClip"/>. Returns false (so the caller
    /// falls back to the normal background path) when there is no paintable
    /// background or no text to clip to.
    /// </summary>
    private bool TryEmitBackgroundTextClip(Box box, double frameX, double frameY, DisplayList list, Matrix2D current, Rect? cull, ComputedStyle style, Func<Box, ComputedStyle?>? styleOverride)
    {
        if (box.Frame.Width <= 0 || box.Frame.Height <= 0)
        {
            return false;
        }

        // The background paint: a paintable gradient takes priority over the
        // solid color (matching the layering order of `background`).
        CssGradient? gradient = null;
        if (style.Get(PropertyId.BackgroundImage) is { } bgImageValue
            && CssGradientParser.TryParse(bgImageValue, out var g)
            && g.IsPaintable)
        {
            gradient = g;
        }

        var color = style.GetColor(PropertyId.BackgroundColor);
        if (gradient is null && color.A == 0)
        {
            return false;
        }

        // Gather descendant glyph runs in document space. The clip element's own
        // text lives in descendant text boxes whose frames are relative to this
        // box's content origin.
        var glyphs = new List<ClipGlyphRun>();
        var contentX = frameX + box.Border.Left + box.Padding.Left;
        var contentY = frameY + box.Border.Top + box.Padding.Top;
        foreach (var child in box.Children)
        {
            CollectClipGlyphRuns(child, contentX, contentY, styleOverride, glyphs);
        }

        if (glyphs.Count == 0)
        {
            return false;
        }

        var bounds = new Rect(frameX, frameY, box.Frame.Width, box.Frame.Height);
        Emit(list, new FillBackgroundTextClip(bounds, gradient, color, glyphs), bounds, current, cull);
        return true;
    }

    /// <summary>
    /// Walk a subtree collecting every text fragment as a
    /// <see cref="ClipGlyphRun"/> in document-space coordinates. <paramref name="originX"/>
    /// / <paramref name="originY"/> are the document-space top-left of
    /// <paramref name="box"/>'s parent content box, matching how the painter
    /// pushes the content origin when it descends.
    /// </summary>
    private void CollectClipGlyphRuns(Box box, double originX, double originY, Func<Box, ComputedStyle?>? styleOverride, List<ClipGlyphRun> glyphs)
    {
        var frameX = originX + box.Frame.X;
        var frameY = originY + box.Frame.Y;

        if (box is TextBox textBox)
        {
            if (textBox.Fragments.Count == 0)
            {
                return;
            }

            var style = EffectiveStyle(textBox, styleOverride);
            var fontSize = style?.Get(PropertyId.FontSize) switch
            {
                CssLength len => Starling.Layout.Block.BlockLayout.ToPx(len),
                _ => 16d,
            };
            var spec = FontSpec.FromStyle(style);
            foreach (var frag in textBox.Fragments)
            {
                if (frag.Text.Length == 0 || string.IsNullOrWhiteSpace(frag.Text))
                {
                    continue;
                }

                glyphs.Add(new ClipGlyphRun(
                    frag.Text,
                    frameX + frag.X,
                    frameY + frag.Y,
                    fontSize,
                    spec.Families,
                    spec.Bold,
                    spec.Italic,
                    frag.Shaped));
            }
            return; // Text boxes have no children.
        }

        if (box is ImageBox)
        {
            return; // Replaced content contributes no glyphs.
        }

        var contentX = frameX + box.Border.Left + box.Padding.Left;
        var contentY = frameY + box.Border.Top + box.Padding.Top;
        foreach (var child in box.Children)
        {
            CollectClipGlyphRuns(child, contentX, contentY, styleOverride, glyphs);
        }
    }

    /// <summary>
    /// Resolve <c>background-image</c> via <paramref name="images"/> and emit
    /// a sliced <see cref="DrawImage"/> using <c>background-position</c> +
    /// <c>background-size</c> to compute the source rectangle. This is the
    /// path that makes CSS sprite sheets render — the box's frame defines the
    /// visible window, the sprite PNG is the source, and bg-position picks
    /// which slice maps to the window.
    /// </summary>
    private static void EmitBackgroundImage(Box box, double frameX, double frameY, DisplayList list, Matrix2D current, Rect? cull, ComputedStyle style, IImageResolver? images, CornerRadii radii = default)
    {
        var bgImage = style.Get(PropertyId.BackgroundImage);

        // CSS Backgrounds 3 §2.6/§2.4 — per-layer positioning area (origin)
        // and painting area (clip), indexed per layer like position/size.
        var originList = style.Get(PropertyId.BackgroundOrigin);
        var clipList = style.Get(PropertyId.BackgroundClip);

        // CSS Backgrounds 3 §3.4 — `background-image` may be a comma-separated
        // list of layers. The first layer is the topmost, so paint the list
        // back-to-front (later layers sit behind earlier ones). Per-layer
        // position/size are read from the matching index of those longhands.
        if (bgImage is CssValueList layers && layers.Values.Count > 0)
        {
            var posList = style.Get(PropertyId.BackgroundPosition);
            var sizeList = style.Get(PropertyId.BackgroundSize);
            for (var i = layers.Values.Count - 1; i >= 0; i--)
            {
                EmitOneBackgroundLayer(box, frameX, frameY, list, current, cull, images, radii,
                    layers.Values[i], LayerValueAt(sizeList, i), LayerValueAt(posList, i),
                    LayerValueAt(originList, i), LayerValueAt(clipList, i));
            }

            return;
        }

        EmitOneBackgroundLayer(box, frameX, frameY, list, current, cull, images, radii,
            bgImage, style.Get(PropertyId.BackgroundSize), style.Get(PropertyId.BackgroundPosition),
            LayerValueAt(originList, 0), LayerValueAt(clipList, 0));
    }

    /// <summary>Index into a layered background longhand: returns the i-th
    /// layer's value when <paramref name="value"/> is a list (clamping to the
    /// last entry when short), or the value itself for a single, shared value.</summary>
    private static CssValue? LayerValueAt(CssValue? value, int i)
        => value is CssValueList l
            ? (l.Values.Count == 0 ? null : l.Values[Math.Min(i, l.Values.Count - 1)])
            : value;

    private static void EmitOneBackgroundLayer(Box box, double frameX, double frameY, DisplayList list, Matrix2D current, Rect? cull, IImageResolver? images, CornerRadii radii, CssValue? layerImage, CssValue? layerSize, CssValue? layerPosition, CssValue? layerOrigin = null, CssValue? layerClip = null)
    {
        // CSS Images 3 §3 — `background-image: <gradient>`. Gradients paint
        // directly from the typed value and don't need an image resolver; map
        // the recognised gradient functions (linear, radial, conic, and their
        // repeating- variants) to a FillGradient over the box. Anything that
        // doesn't parse (malformed syntax) fails soft, matching the
        // unresolved-image path below. Pass the box's corner radii so the
        // backend clips the gradient fill to the rounded rectangle (CSS
        // Backgrounds 3 §5 border-radius).
        if (layerImage is not null
            && CssGradientParser.TryParse(layerImage, out var gradient)
            && gradient.IsPaintable
            && box.Frame.Width > 0 && box.Frame.Height > 0)
        {
            // CSS Backgrounds 3 §2.4 — the gradient paints over this layer's
            // background-clip box with that box's corrected inner radii. (The
            // gradient's positioning area is approximated by the same box;
            // origin-only differences are not observable without tiling.)
            var gborder = new Rect(frameX, frameY, box.Frame.Width, box.Frame.Height);
            var (gbounds, gradRadii) = ResolveBackgroundPaintBox(box, gborder, radii, layerClip, "border-box");
            if (gbounds.Width <= 0 || gbounds.Height <= 0)
            {
                return;
            }

            Emit(list, new FillGradient(gbounds, gradient, gradRadii), gbounds, current, cull);
            return;
        }

        if (images is null)
        {
            return;
        }

        var url = layerImage switch
        {
            CssUrl u => u.Value,
            _ => null,
        };
        if (url is null || !images.TryResolveUrl(url, out var decoded) || decoded is null)
        {
            return;
        }

        if (box.Frame.Width <= 0 || box.Frame.Height <= 0)
        {
            return;
        }

        if (decoded.Width <= 0 || decoded.Height <= 0)
        {
            return;
        }

        // CSS Backgrounds 3 §2.6 — background-origin picks the positioning
        // area (border/padding/content box) that position and size resolve
        // against. §2.4 — background-clip picks the painting area the
        // rendered image is cropped to.
        var bbounds = new Rect(frameX, frameY, box.Frame.Width, box.Frame.Height);
        var (originRect, _) = ResolveBackgroundPaintBox(box, bbounds, radii, layerOrigin, "padding-box");
        var (clipRect, clipRadii) = ResolveBackgroundPaintBox(box, bbounds, radii, layerClip, "border-box");
        if (originRect.Width <= 0 || originRect.Height <= 0)
        {
            return;
        }

        if (clipRect.Width <= 0 || clipRect.Height <= 0)
        {
            return;
        }

        var areaW = originRect.Width;
        var areaH = originRect.Height;

        // background-size — default `auto auto` keeps the image at native
        // dimensions. A single length applies to width with height = auto.
        // Native size is the *intrinsic* size in CSS px, which can exceed the
        // pixel buffer when the decode was resolution-clamped.
        var (renderW, renderH) = ResolveBackgroundSize(layerSize, areaW, areaH, decoded.IntrinsicWidth, decoded.IntrinsicHeight);
        if (renderW <= 0 || renderH <= 0)
        {
            return;
        }

        // background-position — where the rendered image's top-left lands
        // inside the positioning area. A single value is the X offset (Y
        // defaults to centred per the spec, but the sprite use case keys on
        // X only, so we default Y to 0 for the single-value case to match
        // common sprite-sheet authoring).
        var (offsetX, offsetY) = ResolveBackgroundPosition(layerPosition, areaW, areaH, renderW, renderH);

        // The rendered image rectangle in document coordinates.
        var imgX = originRect.X + offsetX;
        var imgY = originRect.Y + offsetY;

        // Clip the rendered image to the painting area — the visible part is
        // the intersection. Anything outside is discarded.
        var destX = Math.Max(clipRect.X, imgX);
        var destY = Math.Max(clipRect.Y, imgY);
        var destRight = Math.Min(clipRect.Right, imgX + renderW);
        var destBottom = Math.Min(clipRect.Bottom, imgY + renderH);
        var destW = destRight - destX;
        var destH = destBottom - destY;
        if (destW <= 0 || destH <= 0)
        {
            return;
        }

        // Map the clipped destination rect back to source pixel coords —
        // pixel-buffer dims here, because SourceRect addresses the buffer.
        var scaleX = decoded.Width / renderW;
        var scaleY = decoded.Height / renderH;
        var srcX = (destX - imgX) * scaleX;
        var srcY = (destY - imgY) * scaleY;
        var srcW = destW * scaleX;
        var srcH = destH * scaleY;

        var dest = new Rect(destX, destY, destW, destH);
        if (clipRadii.IsZero)
        {
            Emit(list, new DrawImage(dest, decoded, new Rect(srcX, srcY, srcW, srcH)), dest, current, cull);
            return;
        }

        // Rounded painting area: crop the blit with a real clip bracket so
        // the clip box's rounded corners stay transparent.
        list.Add(new PushClip(clipRect, clipRadii));
        Emit(list, new DrawImage(dest, decoded, new Rect(srcX, srcY, srcW, srcH)), dest, current, cull);
        list.Add(PopClip.Instance);
    }

    private static (double Width, double Height) ResolveBackgroundSize(CssValue? value, double boxW, double boxH, double nativeW, double nativeH)
    {
        if (value is CssKeyword { Name: "contain" })
        {
            var scale = Math.Min(boxW / nativeW, boxH / nativeH);
            return (nativeW * scale, nativeH * scale);
        }
        if (value is CssKeyword { Name: "cover" })
        {
            var scale = Math.Max(boxW / nativeW, boxH / nativeH);
            return (nativeW * scale, nativeH * scale);
        }

        double? w = null, h = null;
        if (value is CssValueList list)
        {
            if (list.Values.Count > 0)
            {
                w = ResolveBgSizeDim(list.Values[0], boxW);
            }

            if (list.Values.Count > 1)
            {
                h = ResolveBgSizeDim(list.Values[^1], boxH);
            }
        }
        else
        {
            w = ResolveBgSizeDim(value, boxW);
        }

        // `auto` on one axis preserves aspect ratio against the other.
        if (w is null && h is null)
        {
            return (nativeW, nativeH);
        }

        if (w is null)
        {
            return (nativeW * (h!.Value / nativeH), h.Value);
        }

        if (h is null)
        {
            return (w.Value, nativeH * (w.Value / nativeW));
        }

        return (w.Value, h.Value);
    }

    private static double? ResolveBgSizeDim(CssValue? value, double basis)
        => value switch
        {
            CssLength len => Starling.Layout.Block.BlockLayout.ToPx(len),
            CssPercentage pct => basis * pct.Value / 100d,
            CssNumber n => n.Value,
            _ => null,
        };

    private static (double X, double Y) ResolveBackgroundPosition(CssValue? value, double boxW, double boxH, double imgW, double imgH)
    {
        CssValue? xv = null, yv = null;
        if (value is CssValueList list)
        {
            if (list.Values.Count > 0)
            {
                xv = list.Values[0];
            }

            if (list.Values.Count > 1)
            {
                yv = list.Values[^1];
            }
        }
        else
        {
            xv = value;
        }
        var x = ResolveBgPositionAxis(xv, boxW, imgW, axisX: true);
        var y = ResolveBgPositionAxis(yv, boxH, imgH, axisX: false);
        return (x, y);
    }

    private static double ResolveBgPositionAxis(CssValue? value, double boxSize, double imgSize, bool axisX)
        => value switch
        {
            CssLength len => Starling.Layout.Block.BlockLayout.ToPx(len),
            CssPercentage pct => (boxSize - imgSize) * pct.Value / 100d,
            CssNumber n => n.Value,
            CssKeyword { Name: "left" } => 0,
            CssKeyword { Name: "top" } => 0,
            CssKeyword { Name: "right" } => boxSize - imgSize,
            CssKeyword { Name: "bottom" } => boxSize - imgSize,
            CssKeyword { Name: "center" } => (boxSize - imgSize) / 2d,
            // Default missing-axis behaviour: x defaults to 0, y to 0 — the
            // sprite-sheet use case is the dominant consumer and authors
            // typically pass a single horizontal offset.
            _ => 0,
        };

    // -----------------------------------------------------------------------
    // CSS Images 3 §4.5/§4.6 — object-fit + object-position for replaced
    // boxes, and CSS Transforms 1 §6.2 — transform-origin resolution.
    // Additive arms per the shared-paint-file etiquette.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Emits the replaced content of an <see cref="ImageBox"/> honouring
    /// <c>object-fit</c> and <c>object-position</c> (CSS Images 3 §4.5/§4.6).
    /// Layout already sized the box; this only changes how the source pixels
    /// map onto the CONTENT box. The fitted image rectangle is computed from
    /// the fit keyword, positioned by <c>object-position</c>, and any part
    /// outside the content box (possible under <c>cover</c>/<c>none</c> or an
    /// edge position) is cropped by slicing the source rectangle, so the blit
    /// never paints past the content edge.
    /// </summary>
    private static void EmitReplacedImage(ImageBox imageBox, double frameX, double frameY, DisplayList list, Matrix2D current, Rect? cull, Func<Box, ComputedStyle?>? styleOverride)
    {
        var decoded = imageBox.Source;
        if (decoded.Width <= 0 || decoded.Height <= 0)
        {
            return;
        }

        // object-fit positions the replaced content inside the content box
        // (frame minus border and padding).
        var contentX = frameX + imageBox.Border.Left + imageBox.Padding.Left;
        var contentY = frameY + imageBox.Border.Top + imageBox.Padding.Top;
        var contentW = imageBox.Frame.Width - imageBox.Border.Left - imageBox.Border.Right - imageBox.Padding.Left - imageBox.Padding.Right;
        var contentH = imageBox.Frame.Height - imageBox.Border.Top - imageBox.Border.Bottom - imageBox.Padding.Top - imageBox.Padding.Bottom;
        if (contentW <= 0 || contentH <= 0)
        {
            return;
        }

        var style = EffectiveStyle(imageBox, styleOverride);
        var fitName = style?.Get(PropertyId.ObjectFit) is CssKeyword fitKeyword ? fitKeyword.Name : "fill";

        // Natural size in CSS px. A ratio-only intrinsic size (e.g. an SVG
        // with only a viewBox) has no definite natural size: contain/cover
        // still work off the ratio, while `none` degrades to fill and
        // `scale-down` to contain (§4.5 — the default object size is the
        // content box, and contain never exceeds it).
        var naturalW = imageBox.IntrinsicWidth;
        var naturalH = imageBox.IntrinsicHeight;
        var hasRatio = naturalW > 0 && naturalH > 0;
        var hasNatural = hasRatio && !imageBox.IntrinsicSizeIsRatioOnly;

        var fitW = contentW;
        var fitH = contentH;
        switch (fitName)
        {
            case "contain" when hasRatio:
                {
                    var scale = Math.Min(contentW / naturalW, contentH / naturalH);
                    fitW = naturalW * scale;
                    fitH = naturalH * scale;
                    break;
                }
            case "cover" when hasRatio:
                {
                    var scale = Math.Max(contentW / naturalW, contentH / naturalH);
                    fitW = naturalW * scale;
                    fitH = naturalH * scale;
                    break;
                }
            case "none" when hasNatural:
                fitW = naturalW;
                fitH = naturalH;
                break;
            case "scale-down" when hasRatio:
                {
                    // §4.5 — the smaller of `none` and `contain`.
                    var scale = Math.Min(contentW / naturalW, contentH / naturalH);
                    if (hasNatural && scale > 1d)
                    {
                        scale = 1d;
                    }

                    fitW = naturalW * scale;
                    fitH = naturalH * scale;
                    break;
                }
        }

        var (offsetX, offsetY) = ResolveObjectPosition(
            style?.Get(PropertyId.ObjectPosition), contentW, contentH, fitW, fitH);

        var imgX = contentX + offsetX;
        var imgY = contentY + offsetY;

        // Fast path — the fitted image covers the content box exactly (the
        // `fill` default): one whole-source blit, the same item this arm
        // emitted before object-fit existed.
        if (imgX == contentX && imgY == contentY && fitW == contentW && fitH == contentH)
        {
            var bounds = new Rect(contentX, contentY, contentW, contentH);
            Emit(list, new DrawImage(bounds, decoded), bounds, current, cull);
            return;
        }

        // The visible part is the fitted rectangle clipped to the content box.
        var destX = Math.Max(contentX, imgX);
        var destY = Math.Max(contentY, imgY);
        var destRight = Math.Min(contentX + contentW, imgX + fitW);
        var destBottom = Math.Min(contentY + contentH, imgY + fitH);
        var destW = destRight - destX;
        var destH = destBottom - destY;
        if (destW <= 0 || destH <= 0)
        {
            return;
        }

        // Map the cropped destination back to source pixel coords — the pixel
        // BUFFER dims, not the intrinsic dims, because SourceRect addresses
        // the buffer (which may be a resolution-clamped decode).
        var scaleX = decoded.Width / fitW;
        var scaleY = decoded.Height / fitH;
        var srcX = (destX - imgX) * scaleX;
        var srcY = (destY - imgY) * scaleY;
        var srcW = destW * scaleX;
        var srcH = destH * scaleY;

        var dest = new Rect(destX, destY, destW, destH);
        Emit(list, new DrawImage(dest, decoded, new Rect(srcX, srcY, srcW, srcH)), dest, current, cull);
    }

    /// <summary>
    /// Resolves <c>object-position</c> (CSS Images 3 §4.6, a CSS
    /// <c>&lt;position&gt;</c>) to the fitted image's top-left offset inside
    /// the content box. Percentages resolve against the leftover space
    /// (content − fitted, so 50% centres and 100% flush-ends); lengths offset
    /// from the top/left edge; keyword pairs accept either axis order. The
    /// four-value edge-offset form (<c>right 10px bottom 5px</c>) offsets
    /// from the named edge. The initial value (stored as the raw keyword
    /// "50% 50%") and anything unrecognised resolve to the centre.
    /// </summary>
    private static (double X, double Y) ResolveObjectPosition(CssValue? value, double boxW, double boxH, double imgW, double imgH)
    {
        if (value is CssValueList list)
        {
            var vs = list.Values;
            if (vs.Count >= 4)
            {
                // <edge> <offset> <edge> <offset> — e.g. "right 10px bottom 5px".
                var ex = ResolvePositionEdgeOffset(vs[0], vs[1], boxW, imgW);
                var ey = ResolvePositionEdgeOffset(vs[2], vs[3], boxH, imgH);
                return (ex, ey);
            }
            CssValue? xv = vs.Count > 0 ? vs[0] : null;
            CssValue? yv = vs.Count > 1 ? vs[1] : null;
            // Keyword pairs may come axis-swapped ("top left"): when the first
            // names the vertical axis or the second the horizontal one, swap.
            if (IsVerticalPositionKeyword(xv) || IsHorizontalPositionKeyword(yv))
            {
                (xv, yv) = (yv, xv);
            }

            return (ResolvePositionAxis(xv, boxW, imgW), ResolvePositionAxis(yv, boxH, imgH));
        }

        // Single value: that axis is explicit, the other centres (the missing
        // axis pattern in ResolvePositionAxis handles the null).
        if (IsVerticalPositionKeyword(value))
        {
            return (ResolvePositionAxis(null, boxW, imgW), ResolvePositionAxis(value, boxH, imgH));
        }

        return (ResolvePositionAxis(value, boxW, imgW), ResolvePositionAxis(null, boxH, imgH));
    }

    private static bool IsVerticalPositionKeyword(CssValue? value)
        => value is CssKeyword { Name: "top" or "bottom" };

    private static bool IsHorizontalPositionKeyword(CssValue? value)
        => value is CssKeyword { Name: "left" or "right" };

    private static double ResolvePositionAxis(CssValue? value, double boxSize, double imgSize)
        => value switch
        {
            CssLength len => Starling.Layout.Block.BlockLayout.ToPx(len),
            CssPercentage pct => (boxSize - imgSize) * pct.Value / 100d,
            CssNumber n => n.Value,
            CssKeyword { Name: "left" or "top" } => 0,
            CssKeyword { Name: "right" or "bottom" } => boxSize - imgSize,
            // center / missing axis / the raw "50% 50%" initial keyword.
            _ => (boxSize - imgSize) * 0.5,
        };

    /// <summary>Edge-offset form of one <c>&lt;position&gt;</c> axis:
    /// <c>left|top &lt;offset&gt;</c> offsets from the start edge,
    /// <c>right|bottom &lt;offset&gt;</c> from the end edge (so
    /// <c>right 20%</c> equals <c>80%</c>).</summary>
    private static double ResolvePositionEdgeOffset(CssValue edge, CssValue offset, double boxSize, double imgSize)
    {
        var off = offset switch
        {
            CssLength len => Starling.Layout.Block.BlockLayout.ToPx(len),
            CssPercentage pct => (boxSize - imgSize) * pct.Value / 100d,
            CssNumber n => n.Value,
            _ => 0d,
        };
        return edge is CssKeyword { Name: "right" or "bottom" }
            ? boxSize - imgSize - off
            : off;
    }

    /// <summary>
    /// Resolves <c>transform-origin</c> (CSS Transforms 1 §6.2) to an offset
    /// from the border box's top-left. Keywords left/center/right/top/bottom,
    /// lengths (px from the top-left), and percentages of the border-box
    /// dimensions; keyword pairs accept either axis order ("top left" ==
    /// "left top"); a single value sets that axis with the other centred. A
    /// third (Z) component is ignored — paint is 2D. The initial value
    /// (stored as the raw keyword "50% 50% 0") and anything unrecognised
    /// resolve to the centre, matching the previous hardcoded behaviour.
    /// </summary>
    internal static (double X, double Y) ResolveTransformOrigin(CssValue? value, double width, double height)
    {
        if (value is CssValueList list && list.Values.Count > 0)
        {
            CssValue? xv = list.Values[0];
            CssValue? yv = list.Values.Count > 1 ? list.Values[1] : null;
            if (IsVerticalPositionKeyword(xv) || IsHorizontalPositionKeyword(yv))
            {
                (xv, yv) = (yv, xv);
            }

            return (ResolveOriginAxis(xv, width), ResolveOriginAxis(yv, height));
        }

        if (IsVerticalPositionKeyword(value))
        {
            return (width * 0.5, ResolveOriginAxis(value, height));
        }

        return (ResolveOriginAxis(value, width), height * 0.5);
    }

    private static double ResolveOriginAxis(CssValue? value, double size)
        => value switch
        {
            CssLength len => Starling.Layout.Block.BlockLayout.ToPx(len),
            CssPercentage pct => size * pct.Value / 100d,
            CssNumber n => n.Value,
            CssKeyword { Name: "left" or "top" } => 0,
            CssKeyword { Name: "right" or "bottom" } => size,
            // center / missing axis / the raw "50% 50% 0" initial keyword.
            _ => size * 0.5,
        };

    /// <summary>
    /// Holds the resolved mask source and geometry for a whole-element mask group.
    /// Created by <see cref="TryResolveMaskGeometry"/> and consumed by
    /// <see cref="FlushMaskBracket"/>.
    /// </summary>
    private sealed record MaskGeometry(
        Rect Bounds,
        CornerRadii Radii,
        DecodedImage? Mask,
        CssGradient? MaskGradient,
        double RenderW,
        double RenderH,
        double OffsetX,
        double OffsetY,
        MaskRepeatMode Repeat,
        MaskModeKind Mode);

    /// <summary>
    /// Resolves the mask source and geometry for a box that has a paintable
    /// <c>mask-image</c>. Returns null when the box has no resolvable mask so
    /// the caller can bail out cheaply. The returned <see cref="MaskGeometry"/>
    /// is passed to <see cref="FlushMaskBracket"/> after the inner content is
    /// collected.
    /// </summary>
    private static MaskGeometry? TryResolveMaskGeometry(Box box, double frameX, double frameY, ComputedStyle style, IImageResolver? images)
    {
        if (box.Frame.Width <= 0 || box.Frame.Height <= 0)
        {
            return null;
        }

        var maskValue = style.Get(PropertyId.MaskImage);

        DecodedImage? maskImage = null;
        CssGradient? maskGradient = null;

        if (maskValue is CssUrl maskUrl)
        {
            if (images is null)
            {
                return null;
            }

            if (!images.TryResolveUrl(maskUrl.Value, out maskImage) || maskImage is null)
            {
                return null;
            }

            if (maskImage.Width <= 0 || maskImage.Height <= 0)
            {
                return null;
            }
        }
        else if (maskValue is CssFunctionValue maskGradientFn
                 && CssGradientParser.TryParseFunction(maskGradientFn, out var mg)
                 && mg.IsPaintable)
        {
            maskGradient = mg;
        }
        else
        {
            return null; // No mask source we can handle.
        }

        var boxW = box.Frame.Width;
        var boxH = box.Frame.Height;
        // Intrinsic dims: the mask's native CSS-px size, independent of any
        // decode-resolution clamp on the pixel buffer.
        var nativeMaskW = maskImage?.IntrinsicWidth ?? boxW;
        var nativeMaskH = maskImage?.IntrinsicHeight ?? boxH;
        var (renderW, renderH) = ResolveBackgroundSize(style.Get(PropertyId.MaskSize), boxW, boxH, nativeMaskW, nativeMaskH);
        if (renderW <= 0 || renderH <= 0)
        {
            return null;
        }

        var (offsetX, offsetY) = ResolveBackgroundPosition(style.Get(PropertyId.MaskPosition), boxW, boxH, renderW, renderH);
        var repeat = ResolveMaskRepeat(style.Get(PropertyId.MaskRepeat));
        var mode = ResolveMaskMode(style.Get(PropertyId.MaskMode));
        var radii = ReadCornerRadii(style, boxW, boxH);
        var bounds = new Rect(frameX, frameY, boxW, boxH);

        return new MaskGeometry(bounds, radii, maskImage, maskGradient, renderW, renderH, offsetX, offsetY, repeat, mode);
    }

    /// <summary>
    /// Wraps the accumulated <paramref name="innerList"/> items in a
    /// <see cref="PushMask"/> / <see cref="PopMask"/> bracket and appends them
    /// to <paramref name="list"/>. Called after all box-interior and child items
    /// have been collected.
    /// </summary>
    private static void FlushMaskBracket(DisplayList list, DisplayList innerList, MaskGeometry? geo)
    {
        if (geo is null)
        {
            return;
        }

        list.Add(new PushMask(
            geo.Bounds, geo.Radii,
            geo.Mask, geo.MaskGradient,
            geo.RenderW, geo.RenderH,
            geo.OffsetX, geo.OffsetY,
            geo.Repeat, geo.Mode));
        foreach (var item in innerList.Items)
        {
            list.Add(item);
        }

        list.Add(PopMask.Instance);
    }

    /// <summary>
    /// Resolves <c>mask-mode</c> (CSS Masking 1 §6.1). Defaults to
    /// <see cref="MaskModeKind.MatchSource"/> when unset or unrecognised.
    /// </summary>
    private static MaskModeKind ResolveMaskMode(CssValue? value)
        => value switch
        {
            CssKeyword { Name: "luminance" } => MaskModeKind.Luminance,
            CssKeyword { Name: "alpha" } => MaskModeKind.Alpha,
            _ => MaskModeKind.MatchSource,
        };

    /// <summary>
    /// Resolves <c>mask-repeat</c> (CSS Masking 1 §6.5). Handles the single-keyword
    /// and two-keyword forms; the two-keyword form uses the X-axis keyword to set
    /// the dominant mode (space/round on X side wins).
    /// </summary>
    private static MaskRepeatMode ResolveMaskRepeat(CssValue? value)
    {
        // Extract the first (x-axis) keyword from a two-value list.
        CssKeyword? xKey = value switch
        {
            CssValueList { Values: var vs } => vs.Count > 0 ? vs[0] as CssKeyword : null,
            CssKeyword k => k,
            _ => null,
        };
        // Also check if the single keyword or x-keyword is "no-repeat" – but a
        // list value can have a no-repeat on the y-axis only; honour x-axis first.
        CssKeyword? yKey = value switch
        {
            CssValueList { Values: var vs } => vs.Count > 1 ? vs[1] as CssKeyword : null,
            _ => null,
        };
        var xName = xKey?.Name ?? "";
        var yName = yKey?.Name ?? "";

        return xName switch
        {
            "no-repeat" => MaskRepeatMode.NoRepeat,
            "space" => MaskRepeatMode.Space,
            "round" => MaskRepeatMode.Round,
            "repeat-x" => MaskRepeatMode.RepeatX,
            "repeat-y" => MaskRepeatMode.RepeatY,
            "repeat" when yName is "no-repeat" => MaskRepeatMode.RepeatX,
            _ when yName is "no-repeat" => MaskRepeatMode.RepeatY,
            _ => MaskRepeatMode.Repeat,
        };
    }

    /// <summary>
    /// Reads <c>PropertyId.Transform</c> off the box's effective style and
    /// composes it with the box's resolved <c>transform-origin</c>
    /// (CSS Transforms 1 §6.2) into a single document-space matrix
    /// <c>T(+origin) × M × T(-origin)</c>. Returns <c>null</c> when no
    /// transform applies (<c>none</c>, identity, or unparseable).
    /// </summary>
    internal static Matrix2D? TryGetTransformMatrix(Box box, Func<Box, ComputedStyle?>? styleOverride, double frameX, double frameY)
    {
        if (box.Frame.Width <= 0 && box.Frame.Height <= 0)
        {
            return null;
        }

        var style = EffectiveStyle(box, styleOverride);
        if (style is null)
        {
            return null;
        }

        var raw = style.Get(PropertyId.Transform);
        if (raw is null or CssKeyword { Name: "none" })
        {
            return null;
        }

        var transform = CssTransformParser.Parse(raw);
        if (transform.IsNone)
        {
            return null;
        }

        var local = transform.ToMatrix(box.Frame.Width, box.Frame.Height);
        if (local.IsIdentity)
        {
            return null;
        }

        var (originOffsetX, originOffsetY) = ResolveTransformOrigin(
            style.Get(PropertyId.TransformOrigin), box.Frame.Width, box.Frame.Height);
        var originX = frameX + originOffsetX;
        var originY = frameY + originOffsetY;

        // final = T(+origin) × local × T(-origin)
        var preOrigin = Matrix2D.Translate(-originX, -originY);
        var postOrigin = Matrix2D.Translate(originX, originY);
        return postOrigin.Multiply(local).Multiply(preOrigin);
    }

    /// <summary>
    /// Reads the four <c>border-*-radius</c> longhands off the style, resolves
    /// each to CSS px against the box dimensions, and clamps them so adjacent
    /// radii never overlap (CSS Backgrounds 3 §5.1: if the sum of two radii on a
    /// side exceeds that side's length, all radii are scaled down by the same
    /// factor). Returns <see cref="CornerRadii.None"/> when every corner is
    /// square.
    /// </summary>
    private static CornerRadii ReadCornerRadii(ComputedStyle style, double width, double height)
    {
        var tl = ResolveRadius(style.Get(PropertyId.BorderTopLeftRadius), width, height);
        var tr = ResolveRadius(style.Get(PropertyId.BorderTopRightRadius), width, height);
        var br = ResolveRadius(style.Get(PropertyId.BorderBottomRightRadius), width, height);
        var bl = ResolveRadius(style.Get(PropertyId.BorderBottomLeftRadius), width, height);
        if (tl <= 0 && tr <= 0 && br <= 0 && bl <= 0)
        {
            return CornerRadii.None;
        }

        // §5.1 overlap clamp — scale every corner by the smallest side ratio.
        var scale = 1d;
        if (tl + tr > 0)
        {
            scale = Math.Min(scale, SafeRatio(width, tl + tr));
        }

        if (bl + br > 0)
        {
            scale = Math.Min(scale, SafeRatio(width, bl + br));
        }

        if (tl + bl > 0)
        {
            scale = Math.Min(scale, SafeRatio(height, tl + bl));
        }

        if (tr + br > 0)
        {
            scale = Math.Min(scale, SafeRatio(height, tr + br));
        }

        if (scale < 1d)
        {
            tl *= scale; tr *= scale; br *= scale; bl *= scale;
        }

        return CornerRadii.Uniform(tl, tr, br, bl);
    }

    private static double SafeRatio(double available, double requested)
        => requested <= 0 ? 1d : Math.Min(1d, available / requested);

    private static double ResolveRadius(CssValue? value, double width, double height)
        => value switch
        {
            CssLength len => Math.Max(0, Starling.Layout.Block.BlockLayout.ToPx(len)),
            // §5: a percentage radius is relative to the box's width for the
            // horizontal component; we use the smaller dimension as a single
            // circular approximation (rx == ry) which matches the common case.
            CssPercentage pct => Math.Max(0, Math.Min(width, height) * pct.Value / 100d),
            CssNumber n => Math.Max(0, n.Value),
            CssValueList list when list.Values.Count > 0 => ResolveRadius(list.Values[0], width, height),
            _ => 0,
        };

    private static CornerRadii ShrinkRadii(CornerRadii r, double by)
    {
        static double S(double v, double by) => Math.Max(0, v - by);
        return new CornerRadii(
            S(r.TopLeftX, by), S(r.TopLeftY, by),
            S(r.TopRightX, by), S(r.TopRightY, by),
            S(r.BottomRightX, by), S(r.BottomRightY, by),
            S(r.BottomLeftX, by), S(r.BottomLeftY, by));
    }

    /// <summary>
    /// Shrinks each corner radius component by the adjacent box edges (CSS
    /// Backgrounds 3 §5.1 — the inner radius at a corner is the outer radius
    /// minus the edge thickness on the side that component runs along).
    /// Negative results clamp to zero (a square inner corner); a corner that
    /// is already square stays square.
    /// </summary>
    private static CornerRadii ShrinkRadiiPerSide(CornerRadii r, double top, double right, double bottom, double left)
    {
        static double S(double v, double by) => v <= 0 ? 0 : Math.Max(0, v - by);
        return new CornerRadii(
            S(r.TopLeftX, left), S(r.TopLeftY, top),
            S(r.TopRightX, right), S(r.TopRightY, top),
            S(r.BottomRightX, right), S(r.BottomRightY, bottom),
            S(r.BottomLeftX, left), S(r.BottomLeftY, bottom));
    }

    /// <summary>Returns the LAST layer's value of a layered background
    /// longhand (the bottom layer — the one background-color is clipped by
    /// per CSS Backgrounds 3 §2.4), or the value itself when it is a single
    /// shared value.</summary>
    private static CssValue? LastLayerValue(CssValue? value)
        => value is CssValueList { Values.Count: > 0 } l ? l.Values[^1] : value;

    /// <summary>
    /// Resolves a background box keyword (<c>border-box | padding-box |
    /// content-box</c>, CSS Backgrounds 3 §2.4/§2.6) to its document-space
    /// rectangle and corrected corner radii. <paramref name="borderBounds"/> /
    /// <paramref name="radii"/> are the border box and its radii;
    /// <paramref name="fallback"/> supplies the per-property initial keyword
    /// (<c>padding-box</c> for origin, <c>border-box</c> for clip) when the
    /// style carries no keyword. Unrecognised keywords (e.g. <c>text</c>,
    /// which is handled by the dedicated glyph-clip path) resolve to the
    /// border box.
    /// </summary>
    private static (Rect Rect, CornerRadii Radii) ResolveBackgroundPaintBox(Box box, Rect borderBounds, CornerRadii radii, CssValue? keyword, string fallback)
    {
        var name = keyword is CssKeyword k ? k.Name : fallback;
        double top, right, bottom, left;
        if (name.Equals("content-box", StringComparison.OrdinalIgnoreCase))
        {
            top = box.Border.Top + box.Padding.Top;
            right = box.Border.Right + box.Padding.Right;
            bottom = box.Border.Bottom + box.Padding.Bottom;
            left = box.Border.Left + box.Padding.Left;
        }
        else if (name.Equals("padding-box", StringComparison.OrdinalIgnoreCase))
        {
            top = box.Border.Top;
            right = box.Border.Right;
            bottom = box.Border.Bottom;
            left = box.Border.Left;
        }
        else
        {
            return (borderBounds, radii);
        }

        if (top == 0 && right == 0 && bottom == 0 && left == 0)
        {
            return (borderBounds, radii);
        }

        var rect = new Rect(
            borderBounds.X + left,
            borderBounds.Y + top,
            Math.Max(0, borderBounds.Width - left - right),
            Math.Max(0, borderBounds.Height - top - bottom));
        return (rect, radii.IsZero ? radii : ShrinkRadiiPerSide(radii, top, right, bottom, left));
    }

    /// <summary>
    /// Emits the outer <c>box-shadow</c> drop shadows behind the box. Per CSS
    /// Backgrounds 3 §6 the first listed layer is on top, so the layers are
    /// emitted back-to-front (last → first). Inset layers are skipped here;
    /// they paint later, above the background, via
    /// <see cref="EmitInsetBoxShadows"/>.
    /// </summary>
    private static void EmitBoxShadows(Box box, Rect bounds, CornerRadii radii, DisplayList list, Matrix2D current, Rect? cull, ComputedStyle style)
    {
        var raw = style.Get(PropertyId.BoxShadow);
        if (raw is null or CssKeyword { Name: "none" })
        {
            return;
        }

        var shadow = CssBoxShadowParser.Parse(raw);
        if (shadow.IsNone)
        {
            return;
        }

        var textColor = style.GetColor(PropertyId.Color);

        for (var i = shadow.Layers.Count - 1; i >= 0; i--)
        {
            var layer = shadow.Layers[i];
            if (layer.Inset)
            {
                continue; // inner layers paint via EmitInsetBoxShadows
            }

            var color = layer.Color ?? textColor;
            if (color.A == 0)
            {
                continue;
            }

            var offsetX = Starling.Layout.Block.BlockLayout.ToPx(layer.OffsetX);
            var offsetY = Starling.Layout.Block.BlockLayout.ToPx(layer.OffsetY);
            var blur = Math.Max(0, Starling.Layout.Block.BlockLayout.ToPx(layer.Blur));
            var spread = Starling.Layout.Block.BlockLayout.ToPx(layer.Spread);

            // Shadow AABB for culling: box grown by spread + blur, offset.
            var pad = spread + blur;
            var shadowAabb = new Rect(
                bounds.X + offsetX - pad,
                bounds.Y + offsetY - pad,
                bounds.Width + 2 * pad,
                bounds.Height + 2 * pad);

            Emit(list, new DrawBoxShadow(bounds, radii, offsetX, offsetY, blur, spread, color, layer.Inset), shadowAabb, current, cull);
        }
    }

    /// <summary>
    /// Emits the inset <c>box-shadow</c> layers (CSS Backgrounds 3 §6/§7.1.1).
    /// Inner shadows paint ABOVE the background and BELOW the border stroke,
    /// clipped to the padding box, so the caller invokes this between the
    /// background and the border emission. The emitted
    /// <see cref="DrawBoxShadow"/> carries the PADDING box as <c>Bounds</c>
    /// and the padding-box (inner) radii as <c>Radii</c>; the backend
    /// rasterizes the ring between the inner silhouette (the padding box
    /// offset by the shadow offset and shrunk by spread) and the padding
    /// edge. Layers are emitted back-to-front so the first listed layer
    /// paints on top, matching the outer-shadow order. Parsing happens twice
    /// for boxes that actually have a box-shadow (once here, once for the
    /// outer pass) — both passes early-out before parsing for the common
    /// shadow-less box.
    /// </summary>
    private static void EmitInsetBoxShadows(Box box, Rect bounds, CornerRadii radii, DisplayList list, Matrix2D current, Rect? cull, ComputedStyle style)
    {
        var raw = style.Get(PropertyId.BoxShadow);
        if (raw is null or CssKeyword { Name: "none" })
        {
            return;
        }

        var shadow = CssBoxShadowParser.Parse(raw);
        if (shadow.IsNone)
        {
            return;
        }

        // Padding box: the border box inset by the used border widths. The
        // inner radii shrink per-side by the adjacent border widths (§5.1).
        var bt = box.Border.Top;
        var brd = box.Border.Right;
        var bb = box.Border.Bottom;
        var bl = box.Border.Left;
        var padW = bounds.Width - bl - brd;
        var padH = bounds.Height - bt - bb;
        if (padW <= 0 || padH <= 0)
        {
            return;
        }

        var padding = new Rect(bounds.X + bl, bounds.Y + bt, padW, padH);
        var innerRadii = radii.IsZero ? radii : ShrinkRadiiPerSide(radii, bt, brd, bb, bl);

        var textColor = style.GetColor(PropertyId.Color);

        for (var i = shadow.Layers.Count - 1; i >= 0; i--)
        {
            var layer = shadow.Layers[i];
            if (!layer.Inset)
            {
                continue;
            }

            var color = layer.Color ?? textColor;
            if (color.A == 0)
            {
                continue;
            }

            var offsetX = Starling.Layout.Block.BlockLayout.ToPx(layer.OffsetX);
            var offsetY = Starling.Layout.Block.BlockLayout.ToPx(layer.OffsetY);
            var blur = Math.Max(0, Starling.Layout.Block.BlockLayout.ToPx(layer.Blur));
            var spread = Starling.Layout.Block.BlockLayout.ToPx(layer.Spread);

            // No offset, no blur, and no positive spread → the inner
            // silhouette covers the whole padding box; the ring is empty.
            if (offsetX == 0 && offsetY == 0 && blur <= 0 && spread <= 0)
            {
                continue;
            }

            // Inner shadows never escape the padding box, so its rect is the AABB.
            Emit(list, new DrawBoxShadow(padding, innerRadii, offsetX, offsetY, blur, spread, color, Inset: true), padding, current, cull);
        }
    }

    private static void EmitBorders(Box box, double x, double y, DisplayList list, Matrix2D current, Rect? cull, ComputedStyle style, CornerRadii radii)
    {
        var top = box.Border.Top;
        var right = box.Border.Right;
        var bottom = box.Border.Bottom;
        var left = box.Border.Left;
        if (top + right + bottom + left == 0)
        {
            return;
        }

        var topColor = style.GetColor(PropertyId.BorderTopColor);
        var rightColor = style.GetColor(PropertyId.BorderRightColor);
        var bottomColor = style.GetColor(PropertyId.BorderBottomColor);
        var leftColor = style.GetColor(PropertyId.BorderLeftColor);

        // css-backgrounds-3 §4.2 — per-side border styles. Any painted side
        // with a dashed / dotted / double style routes the whole border
        // through the per-side primitive (the backend draws each side between
        // its corner boundaries); the all-solid fast paths below stay as-is.
        var topStyle = ReadBorderSideStyle(style, PropertyId.BorderTopStyle);
        var rightStyle = ReadBorderSideStyle(style, PropertyId.BorderRightStyle);
        var bottomStyle = ReadBorderSideStyle(style, PropertyId.BorderBottomStyle);
        var leftStyle = ReadBorderSideStyle(style, PropertyId.BorderLeftStyle);
        if ((top > 0 && topStyle is not (BorderSideStyle.Solid or BorderSideStyle.None))
            || (right > 0 && rightStyle is not (BorderSideStyle.Solid or BorderSideStyle.None))
            || (bottom > 0 && bottomStyle is not (BorderSideStyle.Solid or BorderSideStyle.None))
            || (left > 0 && leftStyle is not (BorderSideStyle.Solid or BorderSideStyle.None)))
        {
            var borderBox = new Rect(x, y, box.Frame.Width, box.Frame.Height);
            Emit(list, new DrawBorderSides(
                borderBox, radii,
                topStyle == BorderSideStyle.None ? 0 : top,
                rightStyle == BorderSideStyle.None ? 0 : right,
                bottomStyle == BorderSideStyle.None ? 0 : bottom,
                leftStyle == BorderSideStyle.None ? 0 : left,
                topColor, rightColor, bottomColor, leftColor,
                topStyle, rightStyle, bottomStyle, leftStyle), borderBox, current, cull);
            return;
        }

        // Rounded uniform border: when every side shares the same width and
        // color (the overwhelmingly common authoring case) and the box has
        // rounded corners, paint the border as a single rounded ring — fill the
        // outer rounded border-box, then knock out the inner (padding-box)
        // rounded rect by filling it back to the background-less hole. We can't
        // composite a hole here, so instead paint the ring as the difference of
        // two rounded fills layered with the background already drawn beneath:
        // fill the outer border-box rounded rect with the border color BEFORE
        // the background would be insufficient. Simpler + correct for the solid
        // case: stroke the centre-line rounded rect with a pen of the border
        // width. Per-side mixed (different widths/colors) rounded borders fall
        // back to the square strokes below — a documented first-cut gap.
        if (!radii.IsZero
            && top > 0 && top == right && right == bottom && bottom == left
            && topColor.A > 0
            && topColor == rightColor && rightColor == bottomColor && bottomColor == leftColor)
        {
            // Centre-line rounded rect: inset by half the border width so a
            // pen of `top` width straddles the border-box edge symmetrically.
            var half = top / 2d;
            var inner = new Rect(x + half, y + half, box.Frame.Width - top, box.Frame.Height - top);
            var innerRadii = ShrinkRadii(radii, half);
            var ringBounds = new Rect(x, y, box.Frame.Width, box.Frame.Height);
            Emit(list, new StrokeRoundedRect(inner, innerRadii, topColor, top), ringBounds, current, cull);
            return;
        }

        {
            var b = new Rect(x, y, box.Frame.Width, top);
            Emit(list, new FillRect(b, topColor, FillRectPixelAlignment.Preserve), b, current, cull);
        }
        if (right > 0 && rightColor.A > 0)
        {
            var b = new Rect(x + box.Frame.Width - right, y, right, box.Frame.Height);
            Emit(list, new FillRect(b, rightColor, FillRectPixelAlignment.Preserve), b, current, cull);
        }
        if (bottom > 0 && bottomColor.A > 0)
        {
            var b = new Rect(x, y + box.Frame.Height - bottom, box.Frame.Width, bottom);
            Emit(list, new FillRect(b, bottomColor, FillRectPixelAlignment.Preserve), b, current, cull);
        }
        if (left > 0 && leftColor.A > 0)
        {
            var b = new Rect(x, y, left, box.Frame.Height);
            Emit(list, new FillRect(b, leftColor, FillRectPixelAlignment.Preserve), b, current, cull);
        }
    }

    // ---- css-backgrounds-3 §4.2 border styles + CSS UI 4 §3 outline ---------

    /// <summary>
    /// Maps a <c>border-*-style</c> keyword to the painted
    /// <see cref="BorderSideStyle"/> subset. <c>none</c>/<c>hidden</c> suppress
    /// the side; <c>groove</c>/<c>ridge</c>/<c>inset</c>/<c>outset</c> paint as
    /// solid at this fidelity (documented simplification).
    /// </summary>
    private static BorderSideStyle ReadBorderSideStyle(ComputedStyle style, PropertyId id)
        => style.Get(id) is CssKeyword k
            ? k.Name switch
            {
                "none" or "hidden" => BorderSideStyle.None,
                "dashed" => BorderSideStyle.Dashed,
                "dotted" => BorderSideStyle.Dotted,
                "double" => BorderSideStyle.Double,
                _ => BorderSideStyle.Solid,
            }
            : BorderSideStyle.Solid;

    /// <summary>
    /// Emits the CSS UI 4 §3 outline ring: <c>outline-width</c> thick, in
    /// <c>outline-color</c>, drawn OUTSIDE the border edge expanded by
    /// <c>outline-offset</c>. A negative offset pulls the ring inside the
    /// border box; the offset is clamped at minus half the smaller box
    /// dimension so the ring rectangle never inverts. Outlines take no layout
    /// space and are emitted before the element's own overflow PushClip
    /// bracket opens, so they are never cropped by the element's own clip.
    /// <c>auto</c> draws as a solid focus ring; dashed / dotted / double reuse
    /// the per-side border machinery on the expanded ring box. The ring
    /// follows the element's border-radius expanded by the offset (a square
    /// corner stays square).
    /// </summary>
    private static void EmitOutline(Box box, double x, double y, DisplayList list, Matrix2D current, Rect? cull, ComputedStyle style, CornerRadii radii)
    {
        if (style.Get(PropertyId.OutlineStyle) is not CssKeyword styleKeyword)
        {
            return;
        }

        BorderSideStyle ringStyle;
        switch (styleKeyword.Name)
        {
            case "none" or "hidden": return;
            case "dashed": ringStyle = BorderSideStyle.Dashed; break;
            case "dotted": ringStyle = BorderSideStyle.Dotted; break;
            case "double": ringStyle = BorderSideStyle.Double; break;
            // `auto` is the UA focus ring — drawn as a solid ring here.
            // groove / ridge / inset / outset also paint solid at this fidelity.
            default: ringStyle = BorderSideStyle.Solid; break;
        }

        var width = ResolveOutlineWidth(style.Get(PropertyId.OutlineWidth));
        if (width <= 0)
        {
            return;
        }

        // `outline-color: auto` (the initial value) and `currentcolor` both
        // resolve to the element's text color here (the UA accent color is not
        // modelled). A concrete color is used verbatim.
        var color = style.Get(PropertyId.OutlineColor) is CssColor c
            ? c
            : style.GetColor(PropertyId.Color);
        if (color.A == 0)
        {
            return;
        }

        var w = box.Frame.Width;
        var h = box.Frame.Height;
        var offset = style.Get(PropertyId.OutlineOffset) is CssLength len
            ? Starling.Layout.Block.BlockLayout.ToPx(len)
            : 0d;

        // CSS UI 4 §3.5 — clamp a large negative offset so the ring's inner
        // edge rectangle never inverts.
        var minHalf = Math.Min(w, h) / 2d;
        if (offset < -minHalf)
        {
            offset = -minHalf;
        }

        // Ring inner edge = border box expanded by `offset` on every side; the
        // ring is `width` thick OUTSIDE that edge.
        var grow = offset + width;
        var outer = new Rect(x - grow, y - grow, w + 2 * grow, h + 2 * grow);

        if (ringStyle == BorderSideStyle.Solid)
        {
            // Centre-line stroke: a pen of `width` straddles the centre rect
            // symmetrically, landing exactly between the inner and outer edges.
            var centre = new Rect(
                x - offset - width / 2d,
                y - offset - width / 2d,
                w + 2 * offset + width,
                h + 2 * offset + width);
            var centreRadii = ExpandRadii(radii, offset + width / 2d);
            Emit(list, new StrokeRoundedRect(centre, centreRadii, color, width), outer, current, cull);
            return;
        }

        // Non-solid ring: ride the per-side border machinery on the expanded
        // outer box, all four sides sharing the outline width / color / style.
        var outerRadii = ExpandRadii(radii, grow);
        Emit(list, new DrawBorderSides(
            outer, outerRadii,
            width, width, width, width,
            color, color, color, color,
            ringStyle, ringStyle, ringStyle, ringStyle), outer, current, cull);
    }

    /// <summary>
    /// Resolves <c>outline-width</c> to CSS px. The line-width keywords use
    /// the UA conventions (thin 1px, medium 3px, thick 5px).
    /// </summary>
    private static double ResolveOutlineWidth(CssValue? value)
        => value switch
        {
            CssLength len => Math.Max(0, Starling.Layout.Block.BlockLayout.ToPx(len)),
            CssNumber n => Math.Max(0, n.Value),
            CssKeyword { Name: "thin" } => 1,
            CssKeyword { Name: "medium" } => 3,
            CssKeyword { Name: "thick" } => 5,
            _ => 0,
        };

    /// <summary>
    /// Grows every non-zero corner radius by <paramref name="by"/> (clamped at
    /// zero) so an outline ring keeps following the border curvature at its
    /// offset distance. Square corners stay square, matching CSS UI 4 §3.
    /// </summary>
    private static CornerRadii ExpandRadii(CornerRadii r, double by)
    {
        if (r.IsZero)
        {
            return CornerRadii.None;
        }

        static double G(double v, double by) => v <= 0 ? 0 : Math.Max(0, v + by);
        return new CornerRadii(
            G(r.TopLeftX, by), G(r.TopLeftY, by),
            G(r.TopRightX, by), G(r.TopRightY, by),
            G(r.BottomRightX, by), G(r.BottomRightY, by),
            G(r.BottomLeftX, by), G(r.BottomLeftY, by));
    }

    /// <summary>
    /// Minimum-credible native-widget chrome for HTML form controls, drawn as
    /// resolution-independent vector items (HTML §4.10 rendering / CSS Basic
    /// UI 4 §7): a check-mark polyline for a checked checkbox, a filled
    /// centred dot for a checked radio (the outer circle comes from the UA
    /// sheet's border-radius:50% border), and a right-aligned chevron for
    /// select. The UA sheet opts controls in with `appearance: auto`; any
    /// other computed value — including the property's initial `none` — keeps
    /// the element a plain styled box, so author `appearance: none` strips
    /// all of this chrome. Glyph strokes follow the element's resolved
    /// `color` (currentColor).
    /// </summary>
    private static void EmitFormControlChrome(Box box, double x, double y, DisplayList list, Matrix2D current, Rect? cull, ComputedStyle style)
    {
        if (box.Element is not { } element)
        {
            return;
        }

        var name = element.LocalName;
        if (name is not ("input" or "select"))
        {
            return;
        }

        if (style.Get(PropertyId.Appearance) is not CssKeyword appearance
            || !string.Equals(appearance.Name, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var w = box.Frame.Width;
        var h = box.Frame.Height;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        var color = style.GetColor(PropertyId.Color);
        if (color.A == 0)
        {
            return;
        }

        if (name == "select")
        {
            // Chevron-down, right-aligned in the border box and vertically
            // centred: inset 5px from the right border edge, ~8px wide. Skip
            // degenerate boxes where the chevron would not fit.
            const double chevronWidth = 8d;
            const double chevronInset = 5d;
            if (w < chevronWidth + 2 * chevronInset)
            {
                return;
            }

            var chevronHeight = Math.Min(4.5d, h * 0.4d);
            if (chevronHeight <= 0)
            {
                return;
            }

            var rightX = x + w - chevronInset;
            var midY = y + h / 2d;
            var topY = midY - chevronHeight / 2d;
            var glyph = new StrokeSegments(
                rightX - chevronWidth, topY,
                rightX - chevronWidth / 2d, topY + chevronHeight,
                rightX, topY,
                color, 1.5d);
            var chevronBounds = new Rect(rightX - chevronWidth - 1, topY - 1, chevronWidth + 2, chevronHeight + 2);
            Emit(list, glyph, chevronBounds, current, cull);
            return;
        }

        // <input>: only checkbox/radio carry glyph chrome, and only when
        // checked. Checkedness is attribute-backed in Starling.Dom — the IDL
        // `checked` setter writes the content attribute (HtmlFormControls),
        // so attribute presence IS the live state.
        var type = element.GetAttribute("type");
        if (string.Equals(type, "checkbox", StringComparison.OrdinalIgnoreCase))
        {
            if (!HtmlFormControls.Checked(element))
            {
                return;
            }
            // Check mark: two stroked segments through three points placed at
            // fractions of the border box, so the glyph scales with any
            // author-resized control.
            var stroke = Math.Max(1.5d, Math.Min(w, h) * 0.15d);
            var glyph = new StrokeSegments(
                x + w * 0.22d, y + h * 0.55d,
                x + w * 0.42d, y + h * 0.75d,
                x + w * 0.78d, y + h * 0.28d,
                color, stroke);
            var bounds = new Rect(x, y, w, h);
            Emit(list, glyph, bounds, current, cull);
        }
        else if (string.Equals(type, "radio", StringComparison.OrdinalIgnoreCase))
        {
            if (!HtmlFormControls.Checked(element))
            {
                return;
            }
            // Inner dot: a filled circle at half the control's diameter,
            // centred in the border box.
            var d = Math.Min(w, h) * 0.5d;
            if (d <= 0)
            {
                return;
            }

            var dotRect = new Rect(x + (w - d) / 2d, y + (h - d) / 2d, d, d);
            var r = d / 2d;
            Emit(list, new FillRoundedRect(dotRect, CornerRadii.Uniform(r, r, r, r), color), dotRect, current, cull);
        }
    }

    private static void EmitTextFragments(TextBox text, double x, double y, DisplayList list, Matrix2D current, Rect? cull, Func<Box, ComputedStyle?>? styleOverride)
    {
        if (text.Fragments.Count == 0)
        {
            return;
        }

        var style = EffectiveStyle(text, styleOverride);
        var color = style?.GetColor(PropertyId.Color) ?? CssColor.Black;
        // HTML placeholder text renders muted (the UA ::placeholder default —
        // a mid gray) rather than in the element's own color. The box-tree
        // builder marks the synthesized placeholder TextBox.
        if (text.IsPlaceholder)
        {
            color = PlaceholderColor;
        }

        var fontSize = style?.Get(PropertyId.FontSize) switch
        {
            CssLength len => Starling.Layout.Block.BlockLayout.ToPx(len),
            _ => 16d,
        };
        var spec = FontSpec.FromStyle(style);

        // CSS Text Decoration 3 — resolve the decoration + shadow style once per
        // text box; both apply uniformly to every fragment in the run.
        var lines = ResolveDecorationLines(style);
        var decorationStyle = ResolveDecorationStyle(style);
        var decorationColor = ResolveDecorationColor(style, color);
        var thickness = ResolveDecorationThickness(style, fontSize);
        var underlineOffset = ResolveUnderlineOffset(style, fontSize);
        var shadow = ResolveTextShadow(style);

        foreach (var frag in text.Fragments)
        {
            if (frag.Text.Length == 0 || string.IsNullOrWhiteSpace(frag.Text))
            {
                continue;
            }

            var baselineY = y + frag.Y + frag.Baseline;
            // Cull bounds: the glyph run sits on the baseline. Cover ascent
            // (≈ font size above the baseline) and a small descent below so the
            // AABB safely encloses the rasterized glyphs.
            var glyphBounds = new Rect(
                x + frag.X,
                baselineY - fontSize,
                frag.Width,
                fontSize * 1.3d);

            // text-shadow (CSS Text Decoration 3 §5): paint each layer beneath
            // the foreground glyphs, back-to-front. Per spec the first listed
            // layer renders on top, so emit the list in reverse.
            for (var i = shadow.Layers.Count - 1; i >= 0; i--)
            {
                var layer = shadow.Layers[i];
                var layerColor = layer.Color ?? color;
                if (layerColor.A == 0)
                {
                    continue;
                }

                var shadowBounds = new Rect(
                    glyphBounds.X + layer.OffsetX - layer.Blur,
                    glyphBounds.Y + layer.OffsetY - layer.Blur,
                    glyphBounds.Width + 2 * layer.Blur,
                    glyphBounds.Height + 2 * layer.Blur);
                Emit(list, new DrawTextShadow(
                    frag.Text,
                    x + frag.X,
                    y + frag.Y,
                    fontSize,
                    layerColor,
                    layer.OffsetX,
                    layer.OffsetY,
                    layer.Blur,
                    spec.Families,
                    spec.Bold,
                    spec.Italic,
                    frag.Shaped), shadowBounds, current, cull);
            }

            Emit(list, new DrawText(
                frag.Text,
                x + frag.X,
                y + frag.Y,
                fontSize,
                color,
                spec.Families,
                spec.Bold,
                spec.Italic,
                frag.Shaped), glyphBounds, current, cull);

            if (lines != TextDecorationLines.None && frag.Width > 0)
            {
                // Decoration position/thickness depend on real font metrics, so
                // emit a typed primitive carrying the run geometry + style and
                // let the backend resolve exact y-offsets from the resolved
                // face (CSS Text Decoration 3 §2). Cull bounds cover the full
                // glyph box, which encloses all three line positions.
                Emit(list, new DrawTextDecoration(
                    x + frag.X,
                    frag.Width,
                    baselineY,
                    fontSize,
                    decorationColor,
                    lines,
                    decorationStyle,
                    thickness,
                    underlineOffset,
                    spec.Families,
                    spec.Bold,
                    spec.Italic), glyphBounds, current, cull);
            }
        }
    }

    // ---- CSS Text Decoration 3 helpers (wp:M5-css-15) ----

    private static TextDecorationLines ResolveDecorationLines(ComputedStyle? style)
    {
        if (style is null)
        {
            return TextDecorationLines.None;
        }
        // The `text-decoration` shorthand expands to text-decoration-line; keep
        // the legacy `text-decoration` carrier as a fallback for authors that
        // set the longhand-less shorthand value directly.
        var lines = LinesFromValue(style.Get(PropertyId.TextDecorationLine));
        if (lines == TextDecorationLines.None)
        {
            lines = LinesFromValue(style.Get(PropertyId.TextDecoration));
        }

        return lines;
    }

    private static TextDecorationLines LinesFromValue(CssValue? value)
    {
        var result = TextDecorationLines.None;
        switch (value)
        {
            case CssKeyword k:
                result |= LineFromKeyword(k.Name);
                break;
            case CssValueList list:
                foreach (var item in list.Values.OfType<CssKeyword>())
                {
                    result |= LineFromKeyword(item.Name);
                }

                break;
        }
        return result;
    }

    private static TextDecorationLines LineFromKeyword(string name) => name.ToLowerInvariant() switch
    {
        "underline" => TextDecorationLines.Underline,
        "overline" => TextDecorationLines.Overline,
        "line-through" => TextDecorationLines.LineThrough,
        _ => TextDecorationLines.None,
    };

    private static TextDecorationStyleKind ResolveDecorationStyle(ComputedStyle? style)
        => (style?.Get(PropertyId.TextDecorationStyle) as CssKeyword)?.Name.ToLowerInvariant() switch
        {
            "double" => TextDecorationStyleKind.Double,
            "dotted" => TextDecorationStyleKind.Dotted,
            "dashed" => TextDecorationStyleKind.Dashed,
            "wavy" => TextDecorationStyleKind.Wavy,
            _ => TextDecorationStyleKind.Solid,
        };

    private static CssColor ResolveDecorationColor(ComputedStyle? style, CssColor currentColor)
    {
        // text-decoration-color defaults to currentColor; only an explicit typed
        // color overrides it (the keyword `currentColor` stays unresolved).
        if (style?.Get(PropertyId.TextDecorationColor) is CssColor c)
        {
            return c;
        }

        return currentColor;
    }

    private static double ResolveDecorationThickness(ComputedStyle? style, double fontSize)
    {
        switch (style?.Get(PropertyId.TextDecorationThickness))
        {
            case CssLength { Unit: CssLengthUnit.Px } px:
                return Math.Max(0.5, px.Value);
            case CssLength len:
                return Math.Max(0.5, Starling.Layout.Block.BlockLayout.ToPx(len));
            case CssPercentage pct:
                return Math.Max(0.5, fontSize * pct.Value / 100d);
            default:
                // `auto` / `from-font`: ~1px at 16px, scaling with font size; the
                // backend may refine this from the font's underline thickness.
                return 0d; // sentinel: backend derives from font metrics.
        }
    }

    private static double ResolveUnderlineOffset(ComputedStyle? style, double fontSize)
        => style?.Get(PropertyId.TextUnderlineOffset) switch
        {
            CssLength { Unit: CssLengthUnit.Px } px => px.Value,
            CssLength len => Starling.Layout.Block.BlockLayout.ToPx(len),
            CssPercentage pct => fontSize * pct.Value / 100d,
            _ => 0d, // auto
        };

    private static CssTextShadow ResolveTextShadow(ComputedStyle? style)
        => style?.Get(PropertyId.TextShadow) as CssTextShadow ?? CssTextShadow.None;

    // ---- Filter Effects 1 (Tier 4 item 18) — filter / backdrop-filter --------

    /// <summary>
    /// Resolves the box's `filter` chain to the typed paint-side list, or null
    /// when no paintable filter applies (the overwhelmingly common case — the
    /// initial `none` keyword exits on the type switch with no allocation).
    /// </summary>
    private static IReadOnlyList<FilterFunction>? TryGetFilterFunctions(Box box, Func<Box, ComputedStyle?>? styleOverride)
    {
        if (box.Frame.Width <= 0 && box.Frame.Height <= 0)
        {
            return null;
        }

        var style = EffectiveStyle(box, styleOverride);
        if (style is null)
        {
            return null;
        }

        return ResolveFilterFunctions(style.Get(PropertyId.Filter), style.GetColor(PropertyId.Color));
    }

    /// <summary>
    /// Emits the <see cref="DrawBackdropFilter"/> item for a box whose
    /// `backdrop-filter` resolves to a paintable chain (Filter Effects 2 §6).
    /// The item carries the border box and its corner radii so the backend can
    /// clip the filtered patch to the rounded shape.
    /// </summary>
    private static void EmitBackdropFilter(Rect bounds, CornerRadii radii, DisplayList list, Matrix2D current, Rect? cull, ComputedStyle style)
    {
        var raw = style.Get(PropertyId.BackdropFilter);
        if (raw is null or CssKeyword)
        {
            return; // none — the initial value
        }

        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return;
        }

        var filters = ResolveFilterFunctions(raw, style.GetColor(PropertyId.Color));
        if (filters is null)
        {
            return;
        }

        Emit(list, new DrawBackdropFilter(bounds, radii, filters), bounds, current, cull);
    }

    /// <summary>
    /// Resolves a parsed `filter` / `backdrop-filter` value — a single
    /// <see cref="CssFunctionValue"/> or a <see cref="CssValueList"/> of them
    /// (the generic value path's shape, see the CssFilterEffects1 spec tests) —
    /// into the typed <see cref="FilterFunction"/> chain, in order. Returns
    /// null for `none`, for url() references (SVG filter resolution is
    /// deferred), and — per Filter Effects 1 §10.1, where one invalid function
    /// invalidates the whole list — when any entry fails to resolve.
    /// <paramref name="currentColor"/> supplies the drop-shadow default color.
    /// </summary>
    internal static IReadOnlyList<FilterFunction>? ResolveFilterFunctions(CssValue? value, CssColor currentColor)
    {
        switch (value)
        {
            case CssFunctionValue fn:
                return ResolveFilterFunction(fn, currentColor) is { } single ? new[] { single } : null;
            case CssValueList { Values.Count: > 0 } chain:
                {
                    var resolved = new FilterFunction[chain.Values.Count];
                    for (var i = 0; i < chain.Values.Count; i++)
                    {
                        if (chain.Values[i] is not CssFunctionValue fn)
                        {
                            return null;
                        }

                        if (ResolveFilterFunction(fn, currentColor) is not { } one)
                        {
                            return null;
                        }

                        resolved[i] = one;
                    }
                    return resolved;
                }
            default:
                return null; // none / unset / url(#id)
        }
    }

    private static FilterFunction? ResolveFilterFunction(CssFunctionValue fn, CssColor currentColor)
    {
        var arg = fn.Arguments.Count > 0 ? fn.Arguments[0] : null;
        switch (fn.Name)
        {
            case "blur":
                // blur(<length>?) — the radius; omitted means 0 (§10.1).
                return arg switch
                {
                    null => new FilterFunction(FilterFunctionKind.Blur, 0),
                    CssLength len => new FilterFunction(FilterFunctionKind.Blur, Math.Max(0, Starling.Layout.Block.BlockLayout.ToPx(len))),
                    CssNumber { Value: 0 } => new FilterFunction(FilterFunctionKind.Blur, 0),
                    _ => null,
                };
            case "brightness":
                return AmountFunction(FilterFunctionKind.Brightness, arg, clampToOne: false);
            case "contrast":
                return AmountFunction(FilterFunctionKind.Contrast, arg, clampToOne: false);
            case "saturate":
                return AmountFunction(FilterFunctionKind.Saturate, arg, clampToOne: false);
            case "grayscale":
                return AmountFunction(FilterFunctionKind.Grayscale, arg, clampToOne: true);
            case "sepia":
                return AmountFunction(FilterFunctionKind.Sepia, arg, clampToOne: true);
            case "invert":
                return AmountFunction(FilterFunctionKind.Invert, arg, clampToOne: true);
            case "opacity":
                return AmountFunction(FilterFunctionKind.Opacity, arg, clampToOne: true);
            case "hue-rotate":
                // hue-rotate(<angle>?) — omitted and unitless 0 both mean 0deg.
                return arg switch
                {
                    null => new FilterFunction(FilterFunctionKind.HueRotate, 0),
                    CssAngle angle => new FilterFunction(FilterFunctionKind.HueRotate, angle.InDegrees),
                    CssNumber { Value: 0 } => new FilterFunction(FilterFunctionKind.HueRotate, 0),
                    _ => null,
                };
            case "drop-shadow":
                return ResolveDropShadowFunction(fn, currentColor);
            default:
                return null; // unknown function ⇒ whole list invalid (§10.1)
        }
    }

    /// <summary>
    /// Resolves the amount-style functions. An omitted amount means 1 (§10.1);
    /// negative amounts are invalid; grayscale/sepia/invert/opacity clamp
    /// values over 100% down to 1, the rest pass through (brightness(3) is
    /// meaningful).
    /// </summary>
    private static FilterFunction? AmountFunction(FilterFunctionKind kind, CssValue? arg, bool clampToOne)
    {
        double amount;
        switch (arg)
        {
            case null: amount = 1; break;
            case CssNumber n when n.Value >= 0: amount = n.Value; break;
            case CssPercentage p when p.Value >= 0: amount = p.Value / 100d; break;
            default: return null;
        }
        if (clampToOne && amount > 1)
        {
            amount = 1;
        }

        return new FilterFunction(kind, amount);
    }

    /// <summary>
    /// drop-shadow( [length length length?]? color? ) — the comma-split
    /// tokenizer collapses the space-separated parts into one
    /// <see cref="CssValueList"/> argument; the lengths are gathered in order
    /// (offset-x, offset-y, blur) and the color wherever it appears. A missing
    /// color falls back to <paramref name="currentColor"/> per the spec.
    /// </summary>
    private static FilterFunction? ResolveDropShadowFunction(CssFunctionValue fn, CssColor currentColor)
    {
        double? ox = null, oy = null;
        double blur = 0;
        CssColor color = currentColor;

        for (var a = 0; a < fn.Arguments.Count; a++)
        {
            var values = fn.Arguments[a] is CssValueList inner
                ? inner.Values
                : new[] { fn.Arguments[a] };
            for (var i = 0; i < values.Count; i++)
            {
                switch (values[i])
                {
                    case CssLength len when ox is null:
                        ox = Starling.Layout.Block.BlockLayout.ToPx(len);
                        break;
                    case CssLength len when oy is null:
                        oy = Starling.Layout.Block.BlockLayout.ToPx(len);
                        break;
                    case CssLength len:
                        blur = Math.Max(0, Starling.Layout.Block.BlockLayout.ToPx(len));
                        break;
                    case CssColor c:
                        color = c;
                        break;
                    default:
                        return null;
                }
            }
        }

        if (ox is null || oy is null)
        {
            return null; // both offsets are required
        }

        return new FilterFunction(FilterFunctionKind.DropShadow, blur, ox.Value, oy.Value, color);
    }
}
