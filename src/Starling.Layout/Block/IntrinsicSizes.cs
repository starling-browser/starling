using Starling.Css.Cascade;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Layout.Box;

namespace Starling.Layout.Block;

/// <summary>
/// Shared helpers for css-sizing-3 §4-5 intrinsic inline sizes and the
/// css-sizing-4 §5 preferred aspect ratio. The measurements themselves run
/// through <see cref="BlockLayout"/>'s measure-mode probes; this class owns
/// the pure pieces: reading the computed <c>aspect-ratio</c> value and walking
/// a probed subtree for its used content extent.
/// </summary>
internal static class IntrinsicSizes
{
    /// <summary>Probe width for max-content measurement — wide enough that
    /// nothing soft-wraps. Matches the inline-block shrink-to-fit and flex
    /// basis probes so cached extents agree across callers.</summary>
    internal const double ProbeWidth = 1_000_000d;

    /// <summary>
    /// The preferred aspect ratio (width / height) of a non-replaced box: any
    /// valid <c>&lt;ratio&gt;</c> component applies whether or not <c>auto</c>
    /// accompanies it (css-sizing-4 §5.1 — the <c>auto</c> branch only changes
    /// behaviour for replaced elements with a natural ratio).
    /// </summary>
    internal static bool TryGetPreferredRatio(ComputedStyle? style, out double ratio)
        => TryGetAspectRatio(style, out ratio, out _);

    /// <summary>
    /// Read the computed <c>aspect-ratio</c> (css-sizing-4 §5.1,
    /// <c>auto || &lt;ratio&gt;</c>). Returns true when a usable
    /// <c>&lt;ratio&gt;</c> component is present; <paramref name="hasAuto"/>
    /// reports an accompanying <c>auto</c> so replaced elements can prefer
    /// their natural ratio. Degenerate ratios (zero, infinite) behave as
    /// <c>auto</c> per spec and return false.
    /// </summary>
    internal static bool TryGetAspectRatio(ComputedStyle? style, out double ratio, out bool hasAuto)
    {
        ratio = 0;
        hasAuto = false;
        if (style is null)
        {
            return false;
        }

        switch (style.Get(PropertyId.AspectRatio))
        {
            case CssKeyword k:
                hasAuto = k.Name == "auto";
                return false;
            case CssNumber n:
                return Validate(n.Value, ref ratio);
            case CssValueList list:
                {
                    // `16 / 9` parses as [number, "/" keyword, number]; an
                    // accompanying `auto` adds a keyword entry on either side.
                    double? first = null;
                    double? second = null;
                    for (var i = 0; i < list.Values.Count; i++)
                    {
                        switch (list.Values[i])
                        {
                            case CssKeyword { Name: "auto" }:
                                hasAuto = true;
                                break;
                            case CssNumber num when first is null:
                                first = num.Value;
                                break;
                            case CssNumber num when second is null:
                                second = num.Value;
                                break;
                        }
                    }
                    if (first is not { } w)
                    {
                        return false;
                    }

                    return Validate(second is { } h ? w / h : w, ref ratio);
                }
            default:
                return false;
        }

        static bool Validate(double candidate, ref double ratio)
        {
            if (!(candidate > 0) || !double.IsFinite(candidate))
            {
                return false;
            }

            ratio = candidate;
            return true;
        }
    }

    /// <summary>
    /// Rightmost content edge reached inside <paramref name="box"/> after a
    /// measure-mode probe, in the box's content-box space. Mirrors the flex
    /// shrink-to-fit walk: only real content counts — text fragments, replaced
    /// boxes, atomic inline-blocks, and fixed-width block children — because
    /// auto-width block frames are sized to the probe width and would report
    /// the probe width back as content. A flex container's content lives on
    /// its item frames (per-item main-axis offsets the text walk can't see),
    /// so it reads those directly.
    /// </summary>
    internal static double ContentExtent(Box.Box box)
    {
        if (box.Kind != BoxKind.AnonymousBlock && BlockLayout.IsFlexContainer(box.Style))
        {
            double itemMax = 0;
            foreach (var item in box.Children)
            {
                itemMax = Math.Max(itemMax, item.Frame.X + item.Frame.Width);
            }

            return itemMax;
        }

        return WalkContent(box);

        // Extent of the content INSIDE node, in node's content-box space.
        // Text fragments carry coordinates rooted at their inline formatting
        // context (the nearest block container), so descending into a block
        // child must add that child's content origin (frame X + left border +
        // left padding) and, on the way out, its right edges — otherwise a
        // padded descendant under-reports (Chromium counts the padding in
        // max-content). Auto-width block FRAMES stay excluded: they are sized
        // to the probe width and would report the probe width back.
        static double WalkContent(Box.Box node)
        {
            double max = 0;
            foreach (var child in node.Children)
            {
                switch (child)
                {
                    case TextBox tb:
                        foreach (var frag in tb.Fragments)
                        {
                            // A collapsible space that trails a line hangs in
                            // this engine instead of being removed (CSS Text 3
                            // §4.1.1 line-end trimming); it is not content, so
                            // it must not widen the measured extent (a
                            // zero-width min-content probe would otherwise
                            // report longest-word + space).
                            if (string.IsNullOrWhiteSpace(frag.Text))
                            {
                                continue;
                            }

                            max = Math.Max(max, frag.X + frag.Width);
                        }
                        break;
                    case ImageBox img:
                        max = Math.Max(max, img.Frame.X + img.Frame.Width);
                        break;
                    case InlineBox ib when
                        ib.Style?.Get(PropertyId.Display) is CssKeyword { Name: "inline-block" }:
                        max = Math.Max(max, ib.Frame.X + ib.Frame.Width);
                        break;
                    case BlockBox bb when bb.Style?.Get(PropertyId.Width) is CssLength:
                        // A block child with a fixed (length) width keeps that
                        // width under any probe, so its border-box frame is
                        // real content extent.
                        max = Math.Max(max, bb.Frame.X + bb.Frame.Width);
                        break;
                    case BlockBox bb:
                        max = Math.Max(
                            max,
                            bb.Frame.X + bb.Border.Left + bb.Padding.Left
                                + WalkContent(bb)
                                + bb.Padding.Right + bb.Border.Right);
                        break;
                    default:
                        // Non-atomic inline boxes: their text fragments are
                        // already rooted at this formatting context's origin.
                        max = Math.Max(max, WalkContent(child));
                        break;
                }
            }
            return max;
        }
    }
}
