using Starling.Css.Cascade;
using Starling.Css.Properties;
using Starling.Css.Selectors;
using Starling.Css.Values;
using Starling.Dom;
using Starling.Layout.Box;
using Starling.Layout.Compositor;

namespace Starling.Layout.Tree;

/// <summary>
/// Builds the layout box tree from a DOM tree + a <see cref="StyleEngine"/>.
/// </summary>
internal sealed class BoxTreeBuilder
{
    private readonly StyleEngine _style;
    private readonly IImageResolver _images;
    private readonly double? _nowMs;
    // Element/text-node → box maps for the incremental path. Owned by the
    // LayoutSession (so they persist across frames) and passed in; null on the
    // full-rebuild path, which records nothing.
    private readonly Dictionary<Element, Box.Box>? _elementMap;
    private readonly Dictionary<Starling.Dom.Text, TextBox>? _textMap;

    public BoxTreeBuilder(
        StyleEngine style,
        IImageResolver? images = null,
        double? nowMs = null,
        Dictionary<Element, Box.Box>? elementMap = null,
        Dictionary<Starling.Dom.Text, TextBox>? textMap = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        _style = style;
        _images = images ?? NullImageResolver.Instance;
        _nowMs = nowMs;
        _elementMap = elementMap;
        _textMap = textMap;
    }

    private ComputedStyle Compute(Element el, CascadeCache cache)
        => _nowMs is { } t
            ? _style.ComputeWithAnimations(el, t, context: null, cache)
            : _style.Compute(el, context: null, cache);

    public BlockBox Build(Document document)
    {
        ArgumentNullException.ThrowIfNull(document);

        // Selector-aware invalidation (plan §7): tell the document which
        // attributes its stylesheets select on, so a later script write to a
        // data-*/aria-* attribute that author CSS targets invalidates layout.
        document.StyleReferencedAttributes = _style.ReferencedAttributeNames;

        var root = document.DocumentElement;
        if (root is null)
        {
            return new BlockBox(style: null, element: null);
        }

        // One cache per box-tree-build pass: the cascade for a given element
        // is identical across every visit during this traversal, so we can
        // memoize and skip the ancestor recursion on the hot path.
        var cache = new CascadeCache();
        // Pre-cascade the whole DOM in parallel by depth. After this, every
        // _style.Compute(element, ..., cache) call below is a cache hit and
        // the box-tree walk is pure CPU-bound traversal. Falls through to
        // sequential for tiny trees (Parallel.ForEach has fixed overhead
        // around ~50μs per partition that beats serial only past ~12 items
        // per depth).
        _style.PrecomputeTree(root, cache);
        var rootStyle = Compute(root, cache);
        var rootBox = new BlockBox(rootStyle, root);
        if (_elementMap is not null)
        {
            _elementMap[root] = rootBox;
        }

        rootBox.Hints = StackingContextResolver.Resolve(rootBox, rootStyle, isRoot: true);
        BuildChildren(root, rootStyle, rootBox, cache);
        WrapInlinesInAnonymousBlocks(rootBox);
        return rootBox;
    }

    private void BuildChildren(Node parentNode, ComputedStyle parentStyle, Box.Box parentBox, CascadeCache cache)
    {
        // CSS Flexbox §4 / Grid §6: the in-flow children of a flex or grid
        // container are *blockified* — an inline-level child computes its
        // display to the equivalent block-level box and becomes its own flex/
        // grid item. Without this an inline child (e.g. a nav's <a>) would be
        // merged with its siblings into a single anonymous block by
        // WrapInlinesInAnonymousBlocks, collapsing the whole row into one item.
        var blockifyChildren = EstablishesFlexOrGridItems(parentStyle);

        for (var child = parentNode.FirstChild; child is not null; child = child.NextSibling)
        {
            switch (child)
            {
                case Element element:
                    BuildElementInto(element, parentStyle, parentBox, blockifyChildren, cache);
                    break;
                case Starling.Dom.Text text:
                    var data = text.Data;
                    if (data.Length == 0)
                    {
                        continue;
                    }

                    var textBox = new TextBox(data, parentStyle);
                    if (_textMap is not null)
                    {
                        _textMap[text] = textBox;
                    }

                    parentBox.AppendChild(textBox);
                    break;
            }
        }
    }

    /// <summary>
    /// Build the box(es) for one element child and append them to
    /// <paramref name="parentBox"/>. Factored out of <see cref="BuildChildren"/>
    /// so the incremental path can rebuild a single element's subtree in
    /// isolation (<see cref="RebuildElementSubtree"/>).
    /// </summary>
    private void BuildElementInto(Element element, ComputedStyle? parentStyle, Box.Box parentBox, bool blockifyChildren, CascadeCache cache)
    {
        var elementStyle = Compute(element, cache);
        var display = DisplayKeyword(elementStyle);
        if (display == "none")
        {
            return;
        }

        if (display == "contents")
        {
            BuildChildren(element, elementStyle, parentBox, cache);
            return;
        }
        if (string.Equals(element.LocalName, "img", StringComparison.OrdinalIgnoreCase))
        {
            BuildImage(element, elementStyle, parentBox);
            return;
        }
        if (string.Equals(element.LocalName, "svg", StringComparison.OrdinalIgnoreCase))
        {
            BuildSvg(element, elementStyle, parentBox);
            return;
        }
        // inline-flex / inline-grid are inline-LEVEL but establish a flex/grid
        // formatting context internally: they place as atomic inlines in their
        // parent's line box (shrink-to-fit), so they're InlineBoxes here. When
        // the parent itself blockifies its children (a flex/grid container), the
        // child becomes a block-level item regardless.
        var isInlineLevel = display is "inline" or "inline-block" or "inline-flex" or "inline-grid";
        Box.Box box = isInlineLevel && !blockifyChildren
            ? new InlineBox(elementStyle, element)
            : new BlockBox(elementStyle, element);
        box.Hints = StackingContextResolver.Resolve(box, elementStyle);
        if (_elementMap is not null)
        {
            _elementMap[element] = box;
        }

        parentBox.AppendChild(box);

        // CSS Lists 3 §3 — a list-item synthesizes a marker box as its first
        // in-flow child. Markers paint through the normal text path (no dedicated
        // display item), so we prepend a TextBox carrying the marker string.
        if (display == "list-item")
        {
            AppendListMarker(element, elementStyle, box);
        }

        // CSS Content 3 §2 — synthesize ::before before children.
        AppendPseudoElement(element, elementStyle, box, PseudoElement.Before, cache);
        if (element.LocalName is "select" or "textarea")
        {
            AppendFormControlLabel(element, elementStyle, box);
        }
        else
        {
            BuildChildren(element, elementStyle, box, cache);
        }
        // ::after after children.
        AppendPseudoElement(element, elementStyle, box, PseudoElement.After, cache);
        // <input> is a void element with no DOM children — synthesize a TextBox
        // from its value/placeholder so the search box and submit button labels
        // actually show up.
        if (string.Equals(element.LocalName, "input", StringComparison.OrdinalIgnoreCase))
        {
            AppendInputLabel(element, elementStyle, box);
        }
    }

    /// <summary>
    /// Incremental reconciliation: rebuild the box subtree for a single
    /// <paramref name="element"/> whose style or content changed, re-cascading
    /// it and its descendants against <paramref name="parentStyle"/> (the
    /// element's parent's computed style). Returns the freshly built box, or
    /// null when the element produces no single box (<c>display:none</c> or
    /// <c>display:contents</c>) — the caller falls back to a full rebuild in that
    /// case, since the parent's box structure would change. Anonymous-block
    /// wrapping is applied to the rebuilt subtree so it matches a full build.
    /// </summary>
    public Box.Box? RebuildElementSubtree(Element element, ComputedStyle? parentStyle, bool blockifyParent)
    {
        var cache = new CascadeCache();
        _style.PrecomputeTree(element, cache);
        // A scratch parent collects the build output; we only keep the single
        // child box (if exactly one was produced).
        var scratch = new BlockBox(parentStyle, element: null);
        BuildElementInto(element, parentStyle, scratch, blockifyParent, cache);
        if (scratch.Children.Count != 1)
        {
            return null; // none / contents / multi
        }

        var box = scratch.Children[0];
        WrapInlinesInAnonymousBlocks(box);
        box.Parent = null;
        return box;
    }

    /// <summary>
    /// Incremental reconciliation of a structural change (plan §3a + §3b): rebuild
    /// <paramref name="parentBox"/>'s child list from <paramref name="parentElement"/>'s
    /// current DOM children, reusing each unchanged child's already-laid-out box
    /// subtree and building only the inserted/changed one, then re-bucketing the
    /// parent's direct child run (localized anonymous re-wrap). The parent's
    /// subtree is re-cascaded first, so a child whose computed style shifted — e.g.
    /// a positional <c>:nth-child</c> selector firing as a sibling moved — is
    /// detected and rebuilt; only style-unchanged subtrees are reused, which keeps
    /// it sound for sibling/positional selectors. Returns false (asking the caller
    /// to fall back to a full rebuild) for a <c>display:contents</c> child, whose
    /// hoisting this localized path doesn't handle.
    /// </summary>
    public bool SpliceChildren(Element parentElement, Box.Box parentBox)
    {
        var cache = new CascadeCache();
        _style.PrecomputeTree(parentElement, cache);
        var blockify = EstablishesFlexOrGridItems(parentBox.Style);

        // The element boxes currently contributed by this parent's DOM children,
        // for pruning the ones whose nodes were removed.
        var oldElementBoxes = CollectChildElementBoxes(parentBox);

        var newChildren = new List<Box.Box>();
        var kept = new HashSet<Element>();

        for (var dom = parentElement.FirstChild; dom is not null; dom = dom.NextSibling)
        {
            switch (dom)
            {
                case Element el:
                    var style = Compute(el, cache);
                    var display = DisplayKeyword(style);
                    if (display == "none")
                    {
                        continue;
                    }

                    if (display == "contents")
                    {
                        return false; // hoisting — fall back
                    }

                    kept.Add(el);
                    if (_elementMap is not null
                        && _elementMap.TryGetValue(el, out var existing)
                        && existing.Parent is not null
                        && SubtreeStylesUnchanged(existing, cache))
                    {
                        newChildren.Add(existing); // reuse the laid-out subtree
                    }
                    else
                    {
                        var boxes = BuildElementBoxes(el, parentBox.Style, blockify, cache);
                        if (boxes.Count != 1)
                        {
                            return false; // unexpected shape — fall back
                        }

                        newChildren.Add(boxes[0]);
                    }
                    break;
                case Starling.Dom.Text t:
                    if (t.Data.Length == 0)
                    {
                        continue;
                    }

                    if (_textMap is not null && _textMap.TryGetValue(t, out var tb) && tb.Text == t.Data)
                    {
                        newChildren.Add(tb);
                    }
                    else
                    {
                        var fresh = new TextBox(t.Data, parentBox.Style);
                        if (_textMap is not null)
                        {
                            _textMap[t] = fresh;
                        }

                        newChildren.Add(fresh);
                    }
                    break;
            }
        }

        // Prune the maps of removed subtrees so a later re-insert rebuilds fresh
        // (a detached node may have been mutated without a recorded batch entry).
        foreach (var old in oldElementBoxes)
        {
            if (old.Element is { } e && !kept.Contains(e))
            {
                PruneElementEntries(old);
            }
        }

        parentBox.Children.Clear();
        foreach (var c in newChildren)
        {
            c.Parent = parentBox;
            parentBox.Children.Add(c);
        }
        WrapDirectChildrenForReconcile(parentBox);
        return true;
    }

    /// <summary>Build the box(es) one element child contributes, each with its
    /// subtree wrapped: 0 = <c>display:none</c>, 1 = a normal element box,
    /// &gt;1 = <c>display:contents</c> expansion.</summary>
    private List<Box.Box> BuildElementBoxes(Element el, ComputedStyle? parentStyle, bool blockify, CascadeCache cache)
    {
        var scratch = new BlockBox(parentStyle, element: null);
        BuildElementInto(el, parentStyle, scratch, blockify, cache);
        var result = new List<Box.Box>(scratch.Children.Count);
        foreach (var box in scratch.Children)
        {
            WrapInlinesInAnonymousBlocks(box);
            box.Parent = null;
            result.Add(box);
        }
        return result;
    }

    /// <summary>True when re-cascading every element in <paramref name="box"/>'s
    /// subtree yields styles equal to the boxes' current styles — i.e. nothing
    /// the structural change touched restyled this subtree, so it can be reused
    /// wholesale. The subtree's DOM is unchanged (only a sibling moved), so equal
    /// styles imply an equal subtree.</summary>
    private bool SubtreeStylesUnchanged(Box.Box box, CascadeCache cache)
    {
        if (box.Element is { } e && (box.Style is null || !box.Style.ValuesEqual(Compute(e, cache))))
        {
            return false;
        }

        foreach (var child in box.Children)
        {
            if (!SubtreeStylesUnchanged(child, cache))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The element boxes contributed by a parent's DOM children — its
    /// direct element-box children plus the element boxes nested one level inside
    /// its anonymous blocks (inline runs).</summary>
    private static List<Box.Box> CollectChildElementBoxes(Box.Box parentBox)
    {
        var list = new List<Box.Box>();
        foreach (var c in parentBox.Children)
        {
            if (c.Element is not null)
            {
                list.Add(c);
            }
            else if (c.Kind == BoxKind.AnonymousBlock)
            {
                foreach (var gc in c.Children)
                {
                    if (gc.Element is not null)
                    {
                        list.Add(gc);
                    }
                }
            }
        }
        return list;
    }

    private void PruneElementEntries(Box.Box box)
    {
        if (_elementMap is null)
        {
            return;
        }

        if (box.Element is { } e)
        {
            _elementMap.Remove(e);
        }

        foreach (var child in box.Children)
        {
            PruneElementEntries(child);
        }
    }

    /// <summary>
    /// CSS 2.2 §9.2.1.1: when a block container has both block and inline
    /// children, runs of consecutive inline children are wrapped in anonymous
    /// block boxes so block layout sees a uniform list of blocks.
    /// </summary>
    /// <remarks>
    /// An <c>inline-block</c> establishes its own block formatting context
    /// (CSS 2.1 §10.1). When an inline-block has mixed block + inline
    /// children, we apply the same anonymous-block wrapping so the BFC sub-pass
    /// sees a uniform list of blocks. Inline boxes that contain only inline
    /// content are left alone — they continue to flatten into the parent IFC.
    /// </remarks>
    private static void WrapInlinesInAnonymousBlocks(Box.Box parent)
    {
        // Text and Replaced boxes have no children worth recursing into.
        if (parent.Kind == BoxKind.Text || parent.Kind == BoxKind.Replaced)
        {
            return;
        }

        // Bucket this level's inline runs (unless the node flattens into the
        // enclosing IFC), then descend. An anonymous block never re-buckets its
        // own children but is still descended through, because it may host an
        // inline-block with mixed children that needs its own wrapping.
        if (ShouldWrapDirectChildren(parent))
        {
            WrapDirectChildren(parent);
        }

        foreach (var child in parent.Children)
        {
            WrapInlinesInAnonymousBlocks(child);
        }
    }

    /// <summary>
    /// Whether <paramref name="parent"/>'s direct children need anonymous-block
    /// bucketing. Anonymous blocks, and inline boxes with no block-level child
    /// (and not establishing a flex/grid context), flatten into the enclosing
    /// inline formatting context and are left as-is.
    /// </summary>
    private static bool ShouldWrapDirectChildren(Box.Box parent)
    {
        if (parent.Kind is BoxKind.Text or BoxKind.Replaced or BoxKind.AnonymousBlock)
        {
            return false;
        }
        // An inline-flex / inline-grid box establishes a flex/grid context, so
        // its raw text/inline runs must still be wrapped into anonymous items —
        // otherwise a bare TextBox becomes a flex item the formatting context
        // can't lay out (the text silently vanishes).
        if (parent.Kind == BoxKind.Inline && !HasBlockLevelChild(parent)
            && !EstablishesFlexOrGridItems(parent.Style))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Bucket <paramref name="parent"/>'s direct inline runs (inline / text /
    /// replaced boxes) into anonymous block boxes so the block formatting
    /// context sees a uniform list of block-level children (CSS 2.2 §9.2.1.1).
    /// Does <em>not</em> recurse — shared by the full wrap pass and the
    /// incremental localized re-wrap (§3b), where the children are already
    /// wrapped internally.
    /// </summary>
    private static void WrapDirectChildren(Box.Box parent)
    {
        var newChildren = new List<Box.Box>();
        AnonymousBlockBox? bucket = null;
        foreach (var child in parent.Children)
        {
            var isInline = child.Kind is BoxKind.Inline or BoxKind.Text or BoxKind.Replaced;
            if (isInline)
            {
                // An anonymous wrapper inherits only inherited (text) properties
                // from its parent; non-inherited props reset to initial. Passing
                // the parent style verbatim would leak width/flex/box-model onto
                // the wrapper — e.g. a flex container's width:100% becoming the
                // anonymous item's flex-basis, ballooning it (CSS 2.1 §9.2.1.1).
                bucket ??= new AnonymousBlockBox(parent.Style?.ForAnonymousChild());
                child.Parent = bucket;
                bucket.Children.Add(child);
            }
            else
            {
                FlushBucket(bucket, newChildren, parent);
                bucket = null;
                newChildren.Add(child);
                child.Parent = parent;
            }
        }

        FlushBucket(bucket, newChildren, parent);

        parent.Children.Clear();
        parent.Children.AddRange(newChildren);
    }

    /// <summary>Localized anonymous re-wrap (plan §3b): re-bucket only
    /// <paramref name="parent"/>'s direct child run, without recursing into the
    /// children (which the incremental splice has already wrapped internally,
    /// whether freshly built or reused).</summary>
    private static void WrapDirectChildrenForReconcile(Box.Box parent)
    {
        if (ShouldWrapDirectChildren(parent))
        {
            WrapDirectChildren(parent);
        }
    }

    private static bool HasBlockLevelChild(Box.Box parent)
    {
        foreach (var child in parent.Children)
        {
            if (child.Kind is BoxKind.BlockContainer or BoxKind.AnonymousBlock)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Append a finished anonymous-block bucket to <paramref name="newChildren"/>,
    /// unless it holds only collapsible whitespace. CSS 2.2 §9.2.2.1: an anonymous
    /// block that would contain only whitespace which subsequently collapses away
    /// is not generated — so the newlines/indentation between block-level siblings
    /// (e.g. between stacked <c>&lt;p&gt;</c>s) don't each become a line-height-tall
    /// box that bloats vertical spacing and breaks adjacent-margin collapse.
    /// </summary>
    private static void FlushBucket(AnonymousBlockBox? bucket, List<Box.Box> newChildren, Box.Box parent)
    {
        if (bucket is null || bucket.Children.Count == 0)
        {
            return;
        }

        if (IsCollapsibleWhitespaceOnly(bucket))
        {
            return;
        }

        bucket.Parent = parent;
        newChildren.Add(bucket);
    }

    private static bool IsCollapsibleWhitespaceOnly(AnonymousBlockBox bucket)
    {
        foreach (var child in bucket.Children)
        {
            if (child is not TextBox text || !string.IsNullOrWhiteSpace(text.Text))
            {
                return false;
            }
            // `white-space: pre*` keeps whitespace significant — don't drop it.
            if (text.Style?.Get(PropertyId.WhiteSpace) is CssKeyword { Name: "pre" or "pre-wrap" or "pre-line" })
            {
                return false;
            }
        }
        return true;
    }

    private static string DisplayKeyword(ComputedStyle style)
        => style.Get(PropertyId.Display) is CssKeyword k ? k.Name.ToLowerInvariant() : "inline";

    /// <summary>
    /// True when <paramref name="style"/> establishes a flex or grid formatting
    /// context, whose in-flow children become blockified flex/grid items.
    /// </summary>
    private static bool EstablishesFlexOrGridItems(ComputedStyle? style)
        => style?.Get(PropertyId.Display) is CssKeyword k
            && (k.Name.Equals("flex", StringComparison.OrdinalIgnoreCase)
                || k.Name.Equals("inline-flex", StringComparison.OrdinalIgnoreCase)
                || k.Name.Equals("grid", StringComparison.OrdinalIgnoreCase)
                || k.Name.Equals("inline-grid", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Build an <c>&lt;img&gt;</c> as an <see cref="ImageBox"/> when the
    /// resolver has decoded bytes for it; otherwise degrade to a
    /// <see cref="TextBox"/> carrying the <c>alt</c> (or <c>aria-label</c> /
    /// <c>title</c>) attribute when present. The HTML spec calls this the
    /// "missing image" rendering: nothing if alt="" was explicitly authored,
    /// otherwise show the alternative text.
    /// </summary>
    private void BuildImage(Element img, ComputedStyle style, Box.Box parentBox)
    {
        if (_images.TryResolve(img, out var resolved))
        {
            var (width, height) = ResolveImageSize(img, resolved);
            var box = new ImageBox(style, img, width, height, resolved.Source);
            // Replaced elements establish a stacking-context layer just like any
            // other element — without this, opacity / transform / filter on an
            // <img> never reach the compositor and paint at full strength.
            box.Hints = StackingContextResolver.Resolve(box, style);
            if (_elementMap is not null)
            {
                _elementMap[img] = box;
            }

            parentBox.AppendChild(box);
            return;
        }

        var label = AccessibleName(img);
        if (string.IsNullOrEmpty(label))
        {
            return;
        }

        parentBox.AppendChild(new TextBox(label, style));
    }

    /// <summary>
    /// Build an inline <c>&lt;svg&gt;</c>. We rasterize it through the image
    /// resolver (which serializes the subtree and runs the managed SVG
    /// rasterizer) and place the result as a replaced <see cref="ImageBox"/>,
    /// so the box is sized by the same CSS width/height path as <c>&lt;img&gt;</c>
    /// (e.g. a <c>width:16px</c> icon). <c>currentColor</c> resolves against the
    /// element's computed <c>color</c> so a <c>stroke="currentColor"</c> icon
    /// picks up the surrounding text color.
    /// <para>
    /// When the resolver can't render the SVG (no renderer wired, or no drawable
    /// geometry) we keep accessibility-driven content visible by degrading to the
    /// element's accessible name — so a labelled icon like Google's logo
    /// (<c>&lt;svg aria-label="Google"&gt;</c>) still surfaces the word "Google"
    /// rather than vanishing.
    /// </para>
    /// </summary>
    private void BuildSvg(Element svg, ComputedStyle style, Box.Box parentBox)
    {
        var currentColor = style.GetColor(PropertyId.Color);
        if (_images.TryResolveInlineSvg(svg, currentColor, out var resolved))
        {
            var (width, height) = ResolveImageSize(svg, resolved);
            // A `viewBox`-only svg (no width/height attribute) has an intrinsic
            // ratio but no intrinsic size: its used size comes from CSS, falling
            // back to the available inline size — not the viewBox px (which would
            // blow a `viewBox="0 -960 960 960"` Material Symbols icon up to 960px
            // when its CSS size is `auto`). The viewBox dims still feed the ratio.
            var ratioOnly = string.IsNullOrEmpty(svg.GetAttribute("width"))
                && string.IsNullOrEmpty(svg.GetAttribute("height"));
            var svgBox = new ImageBox(style, svg, width, height, resolved.Source, ratioOnly);
            svgBox.Hints = StackingContextResolver.Resolve(svgBox, style);
            if (_elementMap is not null)
            {
                _elementMap[svg] = svgBox;
            }

            parentBox.AppendChild(svgBox);
            return;
        }

        var label = AccessibleName(svg);
        if (string.IsNullOrEmpty(label))
        {
            return;
        }
        // Inline the label; the surrounding context controls block/inline
        // flow on the parent.
        parentBox.AppendChild(new TextBox(label, style));
    }

    /// <summary>
    /// The element's accessible name, in priority order: <c>aria-label</c>,
    /// then <c>alt</c> (for <c>&lt;img&gt;</c>), then <c>title</c>. Returns
    /// the empty string when no candidate is set.
    /// </summary>
    private static string AccessibleName(Element element)
    {
        var aria = element.GetAttribute("aria-label");
        if (!string.IsNullOrEmpty(aria))
        {
            return aria;
        }

        var alt = element.GetAttribute("alt");
        if (!string.IsNullOrEmpty(alt))
        {
            return alt;
        }

        var title = element.GetAttribute("title");
        if (!string.IsNullOrEmpty(title))
        {
            return title;
        }

        return string.Empty;
    }

    /// <summary>
    /// HTML <c>width</c>/<c>height</c> attributes take precedence over the
    /// intrinsic dimensions; missing axes scale to preserve aspect ratio.
    /// CSS <c>width</c>/<c>height</c> support is deferred to a follow-up.
    /// </summary>
    private static (double Width, double Height) ResolveImageSize(Element img, ResolvedImage resolved)
    {
        var attrW = ParseDimensionAttribute(img.GetAttribute("width"));
        var attrH = ParseDimensionAttribute(img.GetAttribute("height"));
        var iw = resolved.Width > 0 ? resolved.Width : 1;
        var ih = resolved.Height > 0 ? resolved.Height : 1;

        if (attrW is { } w && attrH is { } h)
        {
            return (w, h);
        }

        if (attrW is { } onlyW)
        {
            return (onlyW, onlyW * ih / iw);
        }

        if (attrH is { } onlyH)
        {
            return (onlyH * iw / ih, onlyH);
        }

        return (iw, ih);
    }

    private static void AppendInputLabel(Element input, ComputedStyle style, Box.Box box)
    {
        var type = (input.GetAttribute("type") ?? "text").Trim().ToLowerInvariant();

        // Controls whose label isn't text content (checkbox/radio glyph, file
        // picker, image button, hidden) get no synthetic text.
        if (type is "checkbox" or "radio" or "file" or "image" or "hidden" or "color" or "range")
        {
            return;
        }

        // The live IDL value (typed text / scripted assignment) shadows the
        // `value` content attribute once the field has been edited; until then
        // the attribute supplies the initial value.
        var value = input.InputValue ?? input.GetAttribute("value");
        if (!string.IsNullOrEmpty(value))
        {
            box.AppendChild(new TextBox(value, style));
            return;
        }

        // Default labels for submit/reset buttons match what browsers show
        // when `value` is omitted — per HTML spec localised defaults. For text
        // fields the placeholder stays visible until the user actually types
        // (HTML, the placeholder attribute) — focusing the field does not clear
        // it, so we key on the value alone, not on focus.
        var fallback = type switch
        {
            "submit" => "Submit",
            "reset" => "Reset",
            "button" => "",
            _ => null,
        };
        if (fallback is null)
        {
            // Text-entry inputs: the placeholder attribute supplies the muted
            // hint text. Flag the run so the painter draws it in the UA
            // placeholder gray instead of the element's color.
            if (input.GetAttribute("placeholder") is { Length: > 0 } placeholder)
            {
                box.AppendChild(new TextBox(placeholder, style) { IsPlaceholder = true });
            }

            return;
        }
        if (!string.IsNullOrEmpty(fallback))
        {
            box.AppendChild(new TextBox(fallback, style));
        }
    }

    private static void AppendFormControlLabel(Element element, ComputedStyle style, Box.Box box)
    {
        if (element.LocalName == "select")
        {
            var label = SelectedOptionLabel(element);
            if (!string.IsNullOrEmpty(label))
            {
                box.AppendChild(new TextBox(label, style));
            }

            return;
        }

        var value = HtmlFormControls.Value(element);
        if (!string.IsNullOrEmpty(value))
        {
            box.AppendChild(new TextBox(value, style));
            return;
        }

        // Like text inputs, the placeholder shows until the user types — focus
        // alone does not clear it. Flagged so the painter mutes it.
        if (element.GetAttribute("placeholder") is { Length: > 0 } placeholder)
        {
            box.AppendChild(new TextBox(placeholder, style) { IsPlaceholder = true });
        }
    }

    private static string SelectedOptionLabel(Element select)
    {
        Element? first = null;
        foreach (var option in select.DescendantElements())
        {
            if (option.LocalName != "option")
            {
                continue;
            }

            first ??= option;
            if (option.HasAttribute("selected"))
            {
                return option.TextContent.Trim();
            }
        }
        return first?.TextContent.Trim() ?? string.Empty;
    }

    /// <summary>
    /// CSS Content 3 §2 — synthesize a <c>::before</c>/<c>::after</c> box for
    /// <paramref name="element"/> when an author rule sets a renderable
    /// <c>content</c>. The pseudo-element box is inline and carries the generated
    /// text as a child <see cref="TextBox"/> styled by the pseudo's own cascade
    /// (so e.g. <c>::before { color: red }</c> tints the generated glyph).
    /// </summary>
    private void AppendPseudoElement(
        Element element,
        ComputedStyle elementStyle,
        Box.Box box,
        PseudoElement pseudo,
        CascadeCache cache)
    {
        var pseudoStyle = _style.ComputeGeneratedPseudoElement(element, pseudo, elementStyle);
        if (pseudoStyle is null)
        {
            return;
        }

        // `display: none` on the pseudo suppresses it entirely.
        if (DisplayKeyword(pseudoStyle) == "none")
        {
            return;
        }

        var text = ContentText(pseudoStyle.Get(PropertyId.Content));
        if (text is null)
        {
            return; // none/normal/unrenderable (e.g. counter()).
        }

        // The generated box is an inline box; its text inherits the pseudo's
        // computed style. Empty strings still generate a (zero-width) box per
        // spec, but contribute no fragment, so we keep the TextBox.
        var inline = new InlineBox(pseudoStyle, element: null);
        // Pseudo boxes promote like element boxes (M12-03): a `filter`/
        // `transform`/z-index ::before (the decorative-glow pattern) must become
        // its own compositor layer, or its filter re-runs inside every tile of
        // the parent layer's raster.
        inline.Hints = StackingContextResolver.Resolve(inline, pseudoStyle);
        inline.AppendChild(new TextBox(text, pseudoStyle));
        box.AppendChild(inline);
    }

    /// <summary>
    /// Extract the rendered string for a computed <c>content</c> value.
    /// <c>none</c>/<c>normal</c> suppress (return null). A <see cref="CssString"/>
    /// (including a resolved <c>attr()</c>) renders verbatim; a list concatenates
    /// its string parts. <c>counter()</c>/<c>counters()</c> and <c>open-quote</c>
    /// are deferred — they yield null so no box is generated.
    /// </summary>
    private static string? ContentText(CssValue content)
    {
        switch (content)
        {
            case CssKeyword { Name: "none" or "normal" }:
                return null;
            case CssString s:
                return s.Value;
            case CssValueList list:
                {
                    var sb = new System.Text.StringBuilder();
                    var any = false;
                    foreach (var part in list.Values)
                    {
                        switch (part)
                        {
                            case CssString ps:
                                sb.Append(ps.Value);
                                any = true;
                                break;
                            case CssKeyword { Name: "normal" or "none" }:
                                break;
                            // counter()/counters()/attr()-already-resolved-string handled above;
                            // unresolved attr() (absent attribute, no fallback) and
                            // counter()/quotes are deferred → contribute nothing.
                            default:
                                break;
                        }
                    }
                    return any ? sb.ToString() : null;
                }
            // Bare attr() that resolved to nothing, or counter()/url() image
            // content: deferred / unsupported → no generated text.
            default:
                return null;
        }
    }

    /// <summary>
    /// CSS Lists 3 §3 — prepend a marker box to a <c>display: list-item</c> box.
    /// The marker text comes from the inherited <c>list-style-type</c> and the
    /// item's ordinal (its 1-based index among list-item siblings, honoring
    /// <c>&lt;ol start&gt;</c> and <c>&lt;li value&gt;</c>). <c>list-style-type:
    /// none</c> suppresses the marker.
    /// </summary>
    private static void AppendListMarker(Element element, ComputedStyle style, Box.Box box)
    {
        var listType = style.Get(PropertyId.ListStyleType) switch
        {
            CssKeyword k => k.Name,
            CssString => "disc", // custom string symbol → fall back to a bullet for now
            _ => "disc",
        };

        var ordinal = ListItemOrdinal(element);
        var marker = ListMarker.Render(listType, ordinal);
        if (marker is null)
        {
            return;
        }

        // The marker is rendered through the normal text path. A trailing space
        // separates the marker from the item's content for `inside` markers and
        // mirrors the visual gap browsers leave for `outside`.
        var markerBox = new TextBox(marker + " ", style);
        // Prepend so the marker leads the item's content.
        box.Children.Insert(0, markerBox);
        markerBox.Parent = box;
    }

    /// <summary>
    /// The 1-based ordinal for a list item: its index among preceding
    /// <c>display: list-item</c> siblings, offset by an <c>&lt;ol start&gt;</c>
    /// base, and overridden outright by an <c>&lt;li value&gt;</c> attribute.
    /// </summary>
    private static int ListItemOrdinal(Element element)
    {
        if (TryParseInt(element.GetAttribute("value"), out var explicitValue))
        {
            return explicitValue;
        }

        var start = 1;
        if (element.ParentNode is Element parent &&
            string.Equals(parent.LocalName, "ol", StringComparison.OrdinalIgnoreCase) &&
            TryParseInt(parent.GetAttribute("start"), out var s))
        {
            start = s;
        }

        var index = 0;
        for (var sibling = element.ParentNode?.FirstChild; sibling is not null; sibling = sibling.NextSibling)
        {
            if (sibling is not Element sib)
            {
                continue;
            }

            if (!IsListItem(sib))
            {
                continue;
            }
            // A preceding sibling with its own value attribute resets the count
            // is a refinement we skip; per the WP, ordinal = sibling index.
            if (sib == element)
            {
                return start + index;
            }

            index++;
        }
        return start + index;
    }

    private static bool IsListItem(Element element)
        => string.Equals(element.LocalName, "li", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseInt(string? raw, out int value)
        => int.TryParse((raw ?? string.Empty).Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out value);

    private static double? ParseDimensionAttribute(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        // HTML allows trailing "px"; we accept that and bare integers/decimals.
        var trimmed = raw.Trim().TrimEnd('p', 'P', 'x', 'X');
        return double.TryParse(trimmed, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value >= 0
            ? value
            : null;
    }
}
