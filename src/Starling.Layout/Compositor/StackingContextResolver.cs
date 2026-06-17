using Starling.Css.Cascade;
using Starling.Css.Properties;
using Starling.Css.Values;

namespace Starling.Layout.Compositor;

/// <summary>
/// Single place that maps a box's computed style onto the set of
/// <see cref="LayerHint"/> bits describing why (if at all) it establishes a CSS
/// stacking context / forces its own compositor layer. Encodes the spec rules
/// verbatim (see the citations below) so reviewers can audit the mapping.
/// </summary>
public static class StackingContextResolver
{
    // CSS-Position-3 §9 "Painting Order and Stacking Contexts"
    // <https://www.w3.org/TR/css-position-3/#stacking-context>
    //   "A stacking context is formed, anywhere in the document, by any element
    //    in the following scenarios:
    //      - the root element.
    //      - an element with a position value absolute or relative and a
    //        z-index value other than auto.
    //      - an element with a position value fixed or sticky.
    //      - an element with an opacity value less than 1.
    //      - an element with a will-change value specifying any property that
    //        would create a stacking context on non-initial value.
    //      - an element with a filter value other than none.
    //      - an element with an isolation value isolate."
    //
    // CSS-Transforms-1 §5 "The Transform Rendering Model"
    // <https://www.w3.org/TR/css-transforms-1/#transform-rendering>
    //   "For elements whose layout is governed by the CSS box model, any value
    //    other than none for the transform property results in the creation of a
    //    stacking context."

    /// <summary>
    /// Resolve the layer hints for <paramref name="box"/> using its
    /// <paramref name="style"/>. <paramref name="isRoot"/> tags the document
    /// root element box. Anonymous boxes (null style) carry no hints.
    /// </summary>
    public static LayerHint Resolve(Box.Box box, ComputedStyle? style, bool isRoot = false)
    {
        ArgumentNullException.ThrowIfNull(box);

        var hints = LayerHint.None;

        // §9: "the root element."
        if (isRoot)
        {
            hints |= LayerHint.Root;
        }

        if (style is null)
        {
            return hints;
        }

        var position = PositionKeyword(style);

        // §9: "an element with a position value absolute or relative and a
        // z-index value other than auto." (We also include fixed/sticky here:
        // any positioned box with an explicit z-index is promoted.)
        if (position is "relative" or "absolute" or "fixed" or "sticky"
            && ZIndexIsNotAuto(style))
        {
            hints |= LayerHint.Promoted;
        }

        // §9: "an element with a position value fixed or sticky." Tagged with
        // dedicated bits so the compositor can treat scroll-affected boxes
        // specially. Sticky is tagged unconditionally — its effective position
        // depends on scroll, so the compositor decides promotion at frame time.
        if (position == "fixed")
        {
            hints |= LayerHint.Fixed;
        }

        if (position == "sticky")
        {
            hints |= LayerHint.Sticky;
        }

        // §9: "an element with an opacity value less than 1."
        if (OpacityLessThanOne(style))
        {
            hints |= LayerHint.OpacityLessThanOne;
        }

        // CSS-Transforms-1 §5: "any value other than none for the transform
        // property results in the creation of a stacking context."
        if (HasTransform(style))
        {
            hints |= LayerHint.Transform3D;
        }

        // §9: "an element with a will-change value specifying any property that
        // would create a stacking context on non-initial value."
        if (WillChangePromotes(style))
        {
            hints |= LayerHint.WillChange;
        }

        // §9: "an element with a filter value other than none."
        if (HasFilter(style))
        {
            hints |= LayerHint.Filter;
        }

        // §9: "an element with an isolation value isolate."
        if (IsIsolated(style))
        {
            hints |= LayerHint.Isolation;
        }

        // Filter Effects 2 §6: "A computed value of other than none results in
        // the creation of a stacking context". The compositor also needs the
        // box on its own layer so the backdrop snapshot can be taken before
        // the element's own content paints.
        if (HasBackdropFilter(style))
        {
            hints |= LayerHint.BackdropFilter;
        }

        return hints;
    }

    private static string PositionKeyword(ComputedStyle style)
        => style.Get(PropertyId.Position) is CssKeyword k
            ? k.Name.ToLowerInvariant()
            : "static";

    private static bool ZIndexIsNotAuto(ComputedStyle style)
        => style.Get(PropertyId.ZIndex) switch
        {
            CssKeyword k => !k.Name.Equals("auto", StringComparison.OrdinalIgnoreCase),
            CssNumber => true,
            CssLength => true,
            _ => false,
        };

    private static bool OpacityLessThanOne(ComputedStyle style)
        => style.Get(PropertyId.Opacity) switch
        {
            CssNumber n => n.Value < 1.0,
            CssPercentage p => p.Value < 100.0,
            _ => false,
        };

    private static bool HasTransform(ComputedStyle style)
    {
        var raw = style.Get(PropertyId.Transform);
        if (raw is null or CssKeyword { Name: "none" })
        {
            return false;
        }
        // A declaration that parses to an empty/identity list is treated as none.
        return !CssTransformParser.Parse(raw).IsNone;
    }

    private static bool HasFilter(ComputedStyle style)
        => style.Get(PropertyId.Filter) switch
        {
            null => false,
            CssKeyword k => !k.Name.Equals("none", StringComparison.OrdinalIgnoreCase),
            // A filter function (blur(), drop-shadow(), …) or a list of them.
            _ => true,
        };

    private static bool HasBackdropFilter(ComputedStyle style)
        => style.Get(PropertyId.BackdropFilter) switch
        {
            null => false,
            CssKeyword k => !k.Name.Equals("none", StringComparison.OrdinalIgnoreCase),
            // A filter function (blur(), saturate(), …) or a list of them.
            _ => true,
        };

    private static bool IsIsolated(ComputedStyle style)
        => style.Get(PropertyId.Isolation) is CssKeyword { Name: var name }
            && name.Equals("isolate", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True if <c>will-change</c> names a property whose non-initial value would
    /// establish a stacking context (transform, opacity, filter, isolation) or
    /// the special compositing keywords. Tolerant of both a single keyword and
    /// a comma-separated list of keywords.
    /// </summary>
    private static bool WillChangePromotes(ComputedStyle style)
    {
        var value = style.Get(PropertyId.WillChange);
        return value switch
        {
            CssKeyword k => IsCompositingProperty(k.Name),
            CssValueList list => list.Values.Any(v => v is CssKeyword k && IsCompositingProperty(k.Name)),
            _ => false,
        };
    }

    private static bool IsCompositingProperty(string name)
        => name.ToLowerInvariant() is "transform" or "opacity" or "filter"
            or "isolation" or "perspective" or "scroll-position" or "contents"
            or "translate" or "scale" or "rotate";
}
