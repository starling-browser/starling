using System.Collections.Frozen;
using Starling.Css.Parser;
using Starling.Css.Values;

namespace Starling.Css.Properties;


public static class PropertyRegistry
{
    // Build-once / read-many name→id table, read on every declaration parse:
    // FrozenDictionary per the repo C# performance policy (hot lookup path).
    private static readonly FrozenDictionary<string, PropertyId> Names =
        Enum.GetValues<PropertyId>().ToFrozenDictionary(ToCssName, id => id, StringComparer.OrdinalIgnoreCase);

    // Build-once -webkit- prefix alias table. CSS Masking 1 / CSS Backgrounds 3:
    // most real-world sites use prefixed forms (-webkit-mask-image,
    // -webkit-background-clip, etc.). One FrozenDictionary lookup instead of
    // a linear scan of Names.
    private const string WebkitPrefix = "-webkit-";
    private static readonly FrozenDictionary<string, PropertyId> WebkitAliases =
        Enum.GetValues<PropertyId>()
            .ToFrozenDictionary(
                id => WebkitPrefix + ToCssName(id),
                id => id,
                StringComparer.OrdinalIgnoreCase);

    // Build-once / read-many inheritance set, read on every cascade: FrozenSet.
    private static readonly FrozenSet<PropertyId> Inherited = FrozenSet.ToFrozenSet<PropertyId>(
    [
        PropertyId.Color,
        PropertyId.FontFamily,
        PropertyId.FontSize,
        PropertyId.FontStretch,
        PropertyId.FontStyle,
        PropertyId.FontVariationSettings,
        PropertyId.FontFeatureSettings,
        PropertyId.FontWeight,
        PropertyId.LineHeight,
        PropertyId.TextAlign,
        PropertyId.TextAlignLast,
        PropertyId.TextDecoration,
        PropertyId.TextShadow,
        PropertyId.TextTransform,
        PropertyId.TextIndent,
        PropertyId.TextOrientation,
        PropertyId.WhiteSpace,
        PropertyId.WhiteSpaceCollapse,
        PropertyId.TextWrap,
        PropertyId.WordBreak,
        PropertyId.OverflowWrap,
        PropertyId.Hyphens,
        PropertyId.LineBreak,
        PropertyId.TabSize,
        PropertyId.WordSpacing,
        PropertyId.LetterSpacing,
        PropertyId.Direction,
        PropertyId.WritingMode,
        PropertyId.Visibility,
        PropertyId.Cursor,
        PropertyId.PointerEvents,
        PropertyId.CaretColor,
        PropertyId.ColorScheme,
        // CSS Scrollbars 1 §3/§4 — scrollbar-width and scrollbar-color are inherited.
        PropertyId.ScrollbarWidth,
        PropertyId.ScrollbarColor,
        // CSS Lists 3 §2: list-style-* are inherited so a marker type set on
        // the list container applies to every list item descendant. `quotes`
        // is inherited per CSS Content 3 §5.
        PropertyId.ListStyleType,
        PropertyId.ListStylePosition,
        PropertyId.ListStyleImage,
        PropertyId.Quotes,
    ]);

    // Properties that, when they change, never alter any box's geometry — they
    // only repaint or recomposite. Read on every animation/transition frame by
    // incremental layout (to decide whether an in-flight animation needs a
    // relayout at all), so it is a FrozenSet for a fast membership test. This is
    // a denylist: a property NOT in here is conservatively treated as
    // layout-affecting, so an omission only costs an unneeded relayout, never a
    // missed one. Everything here is paint- or composite-time per the CSS specs:
    // the transform family is applied at composite time, color/shadow/filter/
    // background/mask paint into the box's existing geometry, and outline is
    // drawn outside the box so it never takes layout space.
    private static readonly FrozenSet<PropertyId> PaintOnly = FrozenSet.ToFrozenSet<PropertyId>(
    [
        // Transform family — composite-time, no layout effect.
        PropertyId.Transform, PropertyId.TransformOrigin, PropertyId.TransformBox,
        PropertyId.Translate, PropertyId.Scale, PropertyId.Rotate,
        PropertyId.Perspective, PropertyId.PerspectiveOrigin, PropertyId.TransformStyle,
        PropertyId.BackfaceVisibility,
        // Opacity / blending / isolation — composite-time.
        PropertyId.Opacity, PropertyId.MixBlendMode, PropertyId.BackgroundBlendMode,
        PropertyId.Isolation,
        // Colors — paint-time.
        PropertyId.Color, PropertyId.BackgroundColor,
        PropertyId.BorderTopColor, PropertyId.BorderRightColor,
        PropertyId.BorderBottomColor, PropertyId.BorderLeftColor,
        PropertyId.BorderInlineStartColor, PropertyId.BorderInlineEndColor,
        PropertyId.BorderBlockStartColor, PropertyId.BorderBlockEndColor,
        PropertyId.OutlineColor, PropertyId.ColumnRuleColor,
        PropertyId.TextDecorationColor, PropertyId.AccentColor, PropertyId.CaretColor,
        // Shadows / filters — paint-time.
        PropertyId.BoxShadow, PropertyId.TextShadow,
        PropertyId.Filter, PropertyId.BackdropFilter,
        // Clip / mask — paint-time.
        PropertyId.ClipPath,
        PropertyId.MaskImage, PropertyId.MaskPosition, PropertyId.MaskSize,
        PropertyId.MaskRepeat, PropertyId.MaskClip, PropertyId.MaskOrigin,
        PropertyId.MaskComposite, PropertyId.MaskMode,
        // Background painting — paint-time (geometry of the box is unchanged).
        PropertyId.BackgroundImage, PropertyId.BackgroundPosition,
        PropertyId.BackgroundPositionX, PropertyId.BackgroundPositionY,
        PropertyId.BackgroundSize, PropertyId.BackgroundRepeat,
        PropertyId.BackgroundAttachment, PropertyId.BackgroundOrigin, PropertyId.BackgroundClip,
        // Outline — drawn outside the box, never takes layout space.
        PropertyId.OutlineWidth, PropertyId.OutlineStyle, PropertyId.OutlineOffset,
    ]);

    public static IReadOnlyList<PropertyId> All { get; } = Enum.GetValues<PropertyId>();

    public static bool TryGetPropertyId(string name, out PropertyId id)
    {
        if (Names.TryGetValue(name, out id))
        {
            return true;
        }

        // CSS Masking 1 / CSS Backgrounds 3: browsers ship mask-* and
        // background-clip:text behind the -webkit- prefix. The WebkitAliases
        // FrozenDictionary maps each "-webkit-<canonical>" to its PropertyId in
        // a single O(1) lookup.
        if (name.StartsWith(WebkitPrefix, StringComparison.Ordinal)
            && WebkitAliases.TryGetValue(name, out id))
        {
            return true;
        }

        return false;
    }

    public static string Name(PropertyId id) => ToCssName(id);

    public static bool Inherits(PropertyId id) => Inherited.Contains(id);

    /// <summary>
    /// True when a change to <paramref name="id"/> can alter box geometry and so
    /// requires a relayout. False for paint- and composite-only properties
    /// (transform, opacity, color, shadow, filter, background, mask, outline),
    /// which only need a repaint. Conservative: any property not known to be
    /// paint-only is reported as layout-affecting.
    /// </summary>
    public static bool AffectsLayout(PropertyId id) => !PaintOnly.Contains(id);

    public static IEnumerable<PropertyDeclaration> Parse(CssDeclaration declaration)
    {
        ArgumentNullException.ThrowIfNull(declaration);
        if (declaration.Name.StartsWith("--", StringComparison.Ordinal))
        {
            yield break;
        }

        var name = declaration.Name.ToLowerInvariant();
        // font-family idents are family names, not CSS keywords — the value
        // parser lowercases idents which would mangle "Helvetica Neue" to
        // "helvetica neue". Family matching is case-insensitive at lookup
        // time, but we keep the authored case here so the cascaded value
        // round-trips and DevTools-style inspection sees the original text.
        var values = name == "font-family"
            ? FontFamilyValueParser.Parse(declaration.Value)
            : CssValueParser.ParseList(declaration.Value).ToList();
        foreach (var parsed in Expand(name, values, declaration.Important))
        {
            yield return parsed;
        }
    }

    public static CssValue InitialValue(PropertyId id)
        => id switch
        {
            PropertyId.Display => new CssKeyword("inline"),
            PropertyId.Position => new CssKeyword("static"),
            PropertyId.Top or PropertyId.Right or PropertyId.Bottom or PropertyId.Left => new CssKeyword("auto"),
            PropertyId.ZIndex => new CssKeyword("auto"),
            PropertyId.Float or PropertyId.Clear => new CssKeyword("none"),
            PropertyId.Width or PropertyId.Height => new CssKeyword("auto"),
            PropertyId.MinWidth or PropertyId.MinHeight => CssLength.Zero,
            PropertyId.MaxWidth or PropertyId.MaxHeight => new CssKeyword("none"),
            PropertyId.MarginTop or PropertyId.MarginRight or PropertyId.MarginBottom or PropertyId.MarginLeft => CssLength.Zero,
            PropertyId.PaddingTop or PropertyId.PaddingRight or PropertyId.PaddingBottom or PropertyId.PaddingLeft => CssLength.Zero,
            PropertyId.BoxSizing => new CssKeyword("content-box"),
            PropertyId.OverflowX or PropertyId.OverflowY => new CssKeyword("visible"),
            PropertyId.OverflowClipMargin => CssLength.Zero,
            PropertyId.BorderTopWidth or PropertyId.BorderRightWidth or PropertyId.BorderBottomWidth or PropertyId.BorderLeftWidth => new CssLength(3, CssLengthUnit.Px),
            PropertyId.BorderTopStyle or PropertyId.BorderRightStyle or PropertyId.BorderBottomStyle or PropertyId.BorderLeftStyle => new CssKeyword("none"),
            PropertyId.BorderTopColor or PropertyId.BorderRightColor or PropertyId.BorderBottomColor or PropertyId.BorderLeftColor => new CssKeyword("currentColor"),
            PropertyId.BorderTopLeftRadius or PropertyId.BorderTopRightRadius or PropertyId.BorderBottomRightRadius or PropertyId.BorderBottomLeftRadius => CssLength.Zero,
            PropertyId.Color => CssColor.Black,
            PropertyId.BackgroundColor => CssColor.Transparent,
            PropertyId.BackgroundImage => new CssKeyword("none"),
            PropertyId.BackgroundPosition => new CssKeyword("0% 0%"),
            // CSS Backgrounds 4 §3 — background-position-x/y longhands, initial 0%.
            PropertyId.BackgroundPositionX or PropertyId.BackgroundPositionY => new CssPercentage(0),
            PropertyId.BackgroundSize => new CssKeyword("auto"),
            PropertyId.BackgroundRepeat => new CssKeyword("repeat"),
            // CSS Backgrounds 3 §3 — initial values for attachment/origin/clip.
            PropertyId.BackgroundAttachment => new CssKeyword("scroll"),
            PropertyId.BackgroundOrigin => new CssKeyword("padding-box"),
            PropertyId.BackgroundClip => new CssKeyword("border-box"),
            PropertyId.Opacity => new CssNumber(1),
            PropertyId.Visibility => new CssKeyword("visible"),
            PropertyId.FontFamily => new CssKeyword("serif"),
            PropertyId.FontSize => new CssLength(16, CssLengthUnit.Px),
            PropertyId.FontStretch => new CssKeyword("normal"),
            PropertyId.FontStyle => new CssKeyword("normal"),
            PropertyId.FontVariationSettings => new CssKeyword("normal"),
            // CSS Fonts 4 §6.11 (font-feature-settings) / §9.5 (size-adjust).
            PropertyId.FontFeatureSettings => new CssKeyword("normal"),
            PropertyId.SizeAdjust => new CssPercentage(100),
            PropertyId.FontWeight => new CssNumber(400),
            PropertyId.LineHeight => new CssKeyword("normal"),
            PropertyId.TextAlign => new CssKeyword("start"),
            PropertyId.TextAlignLast => new CssKeyword("auto"),
            PropertyId.TextDecoration => new CssKeyword("none"),
            PropertyId.TextDecorationLine => new CssKeyword("none"),
            PropertyId.TextDecorationStyle => new CssKeyword("solid"),
            PropertyId.TextDecorationColor => new CssKeyword("currentColor"),
            PropertyId.TextDecorationThickness => new CssKeyword("auto"),
            PropertyId.TextUnderlineOffset => new CssKeyword("auto"),
            PropertyId.TextUnderlinePosition => new CssKeyword("auto"),
            PropertyId.TextShadow => CssTextShadow.None,
            PropertyId.TextTransform => new CssKeyword("none"),
            PropertyId.TextIndent => CssLength.Zero,
            PropertyId.WhiteSpace => new CssKeyword("normal"),
            PropertyId.WhiteSpaceCollapse => new CssKeyword("collapse"),
            PropertyId.TextWrap => new CssKeyword("wrap"),
            PropertyId.WordBreak => new CssKeyword("normal"),
            PropertyId.OverflowWrap => new CssKeyword("normal"),
            PropertyId.Hyphens => new CssKeyword("manual"),
            PropertyId.TabSize => new CssNumber(8),
            PropertyId.LineBreak => new CssKeyword("auto"),
            PropertyId.WordSpacing => new CssKeyword("normal"),
            PropertyId.LetterSpacing => new CssKeyword("normal"),
            PropertyId.Direction => new CssKeyword("ltr"),
            PropertyId.WritingMode => new CssKeyword("horizontal-tb"),
            PropertyId.TextOrientation => new CssKeyword("mixed"),
            PropertyId.UnicodeBidi => new CssKeyword("normal"),
            // CSS Inline 3 §3/§4 — vertical-align (initial baseline) + baseline-source.
            PropertyId.VerticalAlign => new CssKeyword("baseline"),
            PropertyId.BaselineSource => new CssKeyword("auto"),

            // Flexbox
            PropertyId.FlexDirection => new CssKeyword("row"),
            PropertyId.FlexWrap => new CssKeyword("nowrap"),
            PropertyId.FlexGrow => new CssNumber(0),
            PropertyId.FlexShrink => new CssNumber(1),
            PropertyId.FlexBasis => new CssKeyword("auto"),
            PropertyId.Order => new CssNumber(0),
            PropertyId.JustifyContent => new CssKeyword("normal"),
            PropertyId.AlignItems => new CssKeyword("normal"),
            PropertyId.AlignSelf => new CssKeyword("auto"),
            PropertyId.AlignContent => new CssKeyword("normal"),
            PropertyId.JustifyItems => new CssKeyword("legacy"),
            PropertyId.JustifySelf => new CssKeyword("auto"),

            // Grid
            PropertyId.GridTemplateColumns or PropertyId.GridTemplateRows or PropertyId.GridTemplateAreas => new CssKeyword("none"),
            PropertyId.GridAutoColumns or PropertyId.GridAutoRows => new CssKeyword("auto"),
            PropertyId.GridAutoFlow => new CssKeyword("row"),
            PropertyId.GridColumnStart or PropertyId.GridColumnEnd or PropertyId.GridRowStart or PropertyId.GridRowEnd => new CssKeyword("auto"),

            // Gap
            PropertyId.RowGap or PropertyId.ColumnGap => new CssKeyword("normal"),

            // CSS Multicol 1 §3-§5 initial values.
            PropertyId.ColumnCount or PropertyId.ColumnWidth => new CssKeyword("auto"),
            PropertyId.ColumnRuleWidth => new CssKeyword("medium"),
            PropertyId.ColumnRuleStyle => new CssKeyword("none"),
            PropertyId.ColumnRuleColor => new CssKeyword("currentColor"),
            PropertyId.ColumnSpan => new CssKeyword("none"),
            PropertyId.ColumnFill => new CssKeyword("balance"),

            // Sizing
            PropertyId.AspectRatio => new CssKeyword("auto"),
            PropertyId.ObjectFit => new CssKeyword("fill"),
            PropertyId.ObjectPosition => new CssKeyword("50% 50%"),
            // CSS Sizing 4 §4 — contain-intrinsic-size longhands, initial `none`.
            PropertyId.ContainIntrinsicWidth or PropertyId.ContainIntrinsicHeight
                or PropertyId.ContainIntrinsicInlineSize or PropertyId.ContainIntrinsicBlockSize => new CssKeyword("none"),

            // Visual effects
            PropertyId.Transform or PropertyId.Translate or PropertyId.Scale or PropertyId.Rotate => new CssKeyword("none"),
            PropertyId.TransformOrigin => new CssKeyword("50% 50% 0"),
            PropertyId.TransformBox => new CssKeyword("view-box"),
            PropertyId.Perspective => new CssKeyword("none"),
            PropertyId.PerspectiveOrigin => new CssKeyword("50% 50%"),
            // CSS Transforms 2 §5.
            PropertyId.TransformStyle => new CssKeyword("flat"),
            PropertyId.BackfaceVisibility => new CssKeyword("visible"),
            PropertyId.Filter or PropertyId.BackdropFilter => new CssKeyword("none"),
            PropertyId.MixBlendMode or PropertyId.BackgroundBlendMode => new CssKeyword("normal"),
            PropertyId.ClipPath => new CssKeyword("none"),
            // CSS Shapes 1 §2-§3.
            PropertyId.ShapeOutside => new CssKeyword("none"),
            PropertyId.ShapeMargin => CssLength.Zero,
            PropertyId.ShapeImageThreshold => new CssNumber(0),
            PropertyId.BoxShadow => new CssKeyword("none"),
            PropertyId.MaskImage => new CssKeyword("none"),
            PropertyId.MaskPosition => new CssKeyword("0% 0%"),
            PropertyId.MaskSize => new CssKeyword("auto"),
            PropertyId.MaskRepeat => new CssKeyword("repeat"),
            PropertyId.MaskClip or PropertyId.MaskOrigin => new CssKeyword("border-box"),
            PropertyId.MaskComposite => new CssKeyword("add"),
            PropertyId.MaskMode => new CssKeyword("match-source"),

            // Containment / rendering
            PropertyId.Contain => new CssKeyword("none"),
            PropertyId.ContentVisibility => new CssKeyword("visible"),
            PropertyId.WillChange => new CssKeyword("auto"),
            PropertyId.Isolation => new CssKeyword("auto"),
            PropertyId.Container => new CssKeyword("none"),
            PropertyId.ContainerType => new CssKeyword("normal"),
            PropertyId.ContainerName => new CssKeyword("none"),

            // Scrolling
            PropertyId.ScrollBehavior => new CssKeyword("auto"),
            PropertyId.ScrollSnapType => new CssKeyword("none"),
            PropertyId.ScrollSnapAlign => new CssKeyword("none"),
            PropertyId.ScrollSnapStop => new CssKeyword("normal"),
            PropertyId.ScrollMarginTop or PropertyId.ScrollMarginRight or PropertyId.ScrollMarginBottom or PropertyId.ScrollMarginLeft => CssLength.Zero,
            PropertyId.ScrollPaddingTop or PropertyId.ScrollPaddingRight or PropertyId.ScrollPaddingBottom or PropertyId.ScrollPaddingLeft => new CssKeyword("auto"),
            PropertyId.OverscrollBehaviorX or PropertyId.OverscrollBehaviorY => new CssKeyword("auto"),
            // CSS Scrollbars 1 §3/§4 — both default to `auto`.
            PropertyId.ScrollbarWidth or PropertyId.ScrollbarColor => new CssKeyword("auto"),
            // CSS Overflow 3 §3.2 — scrollbar-gutter initial `auto`.
            PropertyId.ScrollbarGutter => new CssKeyword("auto"),

            // Forms / UI
            PropertyId.AccentColor or PropertyId.CaretColor => new CssKeyword("auto"),
            PropertyId.ColorScheme => new CssKeyword("normal"),
            PropertyId.Appearance => new CssKeyword("none"),
            PropertyId.PointerEvents => new CssKeyword("auto"),
            PropertyId.UserSelect => new CssKeyword("auto"),
            PropertyId.Cursor => new CssKeyword("auto"),
            // CSS Basic UI 4 §3 (outline), §6 (resize), §7 (text-overflow).
            PropertyId.OutlineWidth => new CssKeyword("medium"),
            PropertyId.OutlineStyle => new CssKeyword("none"),
            PropertyId.OutlineColor => new CssKeyword("auto"),
            PropertyId.OutlineOffset => CssLength.Zero,
            PropertyId.Resize => new CssKeyword("none"),
            PropertyId.TextOverflow => new CssKeyword("clip"),

            // Logical longhands — default to physical equivalents.
            PropertyId.MarginInlineStart or PropertyId.MarginInlineEnd or PropertyId.MarginBlockStart or PropertyId.MarginBlockEnd => CssLength.Zero,
            PropertyId.PaddingInlineStart or PropertyId.PaddingInlineEnd or PropertyId.PaddingBlockStart or PropertyId.PaddingBlockEnd => CssLength.Zero,
            PropertyId.BorderInlineStartWidth or PropertyId.BorderInlineEndWidth or PropertyId.BorderBlockStartWidth or PropertyId.BorderBlockEndWidth => new CssLength(3, CssLengthUnit.Px),
            PropertyId.BorderInlineStartStyle or PropertyId.BorderInlineEndStyle or PropertyId.BorderBlockStartStyle or PropertyId.BorderBlockEndStyle => new CssKeyword("none"),
            PropertyId.BorderInlineStartColor or PropertyId.BorderInlineEndColor or PropertyId.BorderBlockStartColor or PropertyId.BorderBlockEndColor => new CssKeyword("currentColor"),
            PropertyId.BorderStartStartRadius or PropertyId.BorderStartEndRadius or PropertyId.BorderEndStartRadius or PropertyId.BorderEndEndRadius => CssLength.Zero,
            PropertyId.InsetInlineStart or PropertyId.InsetInlineEnd or PropertyId.InsetBlockStart or PropertyId.InsetBlockEnd => new CssKeyword("auto"),
            PropertyId.InlineSize or PropertyId.BlockSize => new CssKeyword("auto"),
            PropertyId.MinInlineSize or PropertyId.MinBlockSize => CssLength.Zero,
            PropertyId.MaxInlineSize or PropertyId.MaxBlockSize => new CssKeyword("none"),

            // Transitions
            PropertyId.TransitionProperty => new CssKeyword("all"),
            PropertyId.TransitionDuration or PropertyId.TransitionDelay => new CssDimension(0, "s"),
            PropertyId.TransitionTimingFunction => new CssKeyword("ease"),
            PropertyId.TransitionBehavior => new CssKeyword("normal"),

            // Animations
            PropertyId.AnimationName => new CssKeyword("none"),
            PropertyId.AnimationDuration or PropertyId.AnimationDelay => new CssDimension(0, "s"),
            PropertyId.AnimationTimingFunction => new CssKeyword("ease"),
            PropertyId.AnimationIterationCount => new CssNumber(1),
            PropertyId.AnimationDirection => new CssKeyword("normal"),
            PropertyId.AnimationFillMode => new CssKeyword("none"),
            PropertyId.AnimationPlayState => new CssKeyword("running"),
            PropertyId.AnimationComposition => new CssKeyword("replace"),
            // CSS Animations 2 §3 — scroll/view timeline + range.
            PropertyId.AnimationTimeline => new CssKeyword("auto"),
            PropertyId.AnimationRangeStart or PropertyId.AnimationRangeEnd => new CssKeyword("normal"),
            // CSS Scroll-Driven Animations 1 §2-§4.
            PropertyId.ScrollTimelineName or PropertyId.ViewTimelineName or PropertyId.TimelineScope => new CssKeyword("none"),
            PropertyId.ScrollTimelineAxis or PropertyId.ViewTimelineAxis => new CssKeyword("block"),
            PropertyId.ViewTimelineInset => new CssKeyword("auto"),
            // CSS Anchor Positioning 1 §2-§5.
            PropertyId.AnchorName or PropertyId.PositionArea or PropertyId.AnchorScope => new CssKeyword("none"),
            PropertyId.PositionAnchor => new CssKeyword("auto"),

            // Generated content + lists
            // CSS Content 3 §2.1 — `content` initial is `normal` (computes to
            // `none` on ::before/::after, to the element's contents otherwise).
            PropertyId.Content => new CssKeyword("normal"),
            PropertyId.ListStyleType => new CssKeyword("disc"),
            PropertyId.ListStylePosition => new CssKeyword("outside"),
            PropertyId.ListStyleImage => new CssKeyword("none"),
            PropertyId.Quotes => new CssKeyword("auto"),

            // CSS Overflow 4 §5 — line-clamp initial `none`; the legacy
            // -webkit-box-orient companion defaults to `inline-axis`.
            PropertyId.LineClamp => new CssKeyword("none"),
            PropertyId.BoxOrient => new CssKeyword("inline-axis"),

            _ => new CssKeyword("initial"),
        };

    private static IEnumerable<PropertyDeclaration> Expand(
        string name,
        List<CssValue> values,
        bool important)
    {
        if (values.Count == 0)
        {
            yield break;
        }

        // CSS Variables L1 §3.7 — when a shorthand contains var(), every longhand
        // it maps to is set to a pending-substitution value; the shorthand cannot
        // be expanded until the var() references resolve at computed-value time.
        if (ShorthandLonghands.TryGetValue(name, out var longhands)
            && values.Any(ContainsVarReference))
        {
            foreach (var longhand in longhands)
            {
                yield return new PropertyDeclaration(
                    longhand,
                    new CssPendingSubstitution(name, values, longhand),
                    important);
            }

            yield break;
        }

        foreach (var item in ExpandResolved(name, values, important))
        {
            yield return item;
        }
    }

    /// <summary>
    /// Re-runs the shorthand expander after var() substitution. Used by the
    /// cascade to resolve <see cref="CssPendingSubstitution"/> values. Callers
    /// must have already substituted all var() references in <paramref name="values"/>;
    /// any remaining <see cref="CssVarReference"/>s are treated as invalid components.
    /// </summary>
    internal static IEnumerable<PropertyDeclaration> ExpandResolved(
        string name,
        IReadOnlyList<CssValue> values,
        bool important)
    {
        if (values.Count == 0)
        {
            yield break;
        }

        var list = values as List<CssValue> ?? values.ToList();
        foreach (var item in ExpandSwitch(name, list, important))
        {
            yield return item;
        }
    }

    private static IEnumerable<PropertyDeclaration> ExpandSwitch(
        string name,
        List<CssValue> values,
        bool important)
    {
        switch (name)
        {
            case "margin":
                foreach (var item in Box(PropertyId.MarginTop, PropertyId.MarginRight, PropertyId.MarginBottom, PropertyId.MarginLeft, values, important))
                {
                    yield return item;
                }

                break;
            case "padding":
                // CSS Box Model 3 §4: padding must be non-negative — a negative
                // component makes the whole shorthand invalid (dropped).
                if (values.Any(IsNegativeLengthValue))
                {
                    break;
                }

                foreach (var item in Box(PropertyId.PaddingTop, PropertyId.PaddingRight, PropertyId.PaddingBottom, PropertyId.PaddingLeft, values, important))
                {
                    yield return item;
                }

                break;
            case "border-width":
                foreach (var item in Box(PropertyId.BorderTopWidth, PropertyId.BorderRightWidth, PropertyId.BorderBottomWidth, PropertyId.BorderLeftWidth, values, important))
                {
                    yield return item;
                }

                break;
            case "border-style":
                foreach (var item in Box(PropertyId.BorderTopStyle, PropertyId.BorderRightStyle, PropertyId.BorderBottomStyle, PropertyId.BorderLeftStyle, values, important))
                {
                    yield return item;
                }

                break;
            case "border-color":
                foreach (var item in Box(PropertyId.BorderTopColor, PropertyId.BorderRightColor, PropertyId.BorderBottomColor, PropertyId.BorderLeftColor, values, important))
                {
                    yield return item;
                }

                break;
            case "border-radius":
                foreach (var item in Box(PropertyId.BorderTopLeftRadius, PropertyId.BorderTopRightRadius, PropertyId.BorderBottomRightRadius, PropertyId.BorderBottomLeftRadius, values, important))
                {
                    yield return item;
                }

                break;
            case "overflow":
                yield return new PropertyDeclaration(PropertyId.OverflowX, values[0], important);
                yield return new PropertyDeclaration(PropertyId.OverflowY, values.Count > 1 ? values[1] : values[0], important);
                break;
            // CSS Fonts 4 §4.8 — `font` shorthand:
            // [ <style> || <variant> || <weight> || <stretch> ]? <size> [ / <line-height> ]? <family>
            // (system font keywords set all longhands from UA metrics).
            case "font":
                foreach (var item in ExpandFont(values, important))
                {
                    yield return item;
                }

                break;
            case "background":
                foreach (var item in ExpandBackground(values, important))
                {
                    yield return item;
                }

                break;
            case "mask":
            case "-webkit-mask":
                foreach (var item in ExpandMask(values, important))
                {
                    yield return item;
                }

                break;
            case "border":
                foreach (var value in values)
                {
                    if (IsBorderStyle(value))
                    {
                        foreach (var item in Box(PropertyId.BorderTopStyle, PropertyId.BorderRightStyle, PropertyId.BorderBottomStyle, PropertyId.BorderLeftStyle, [value], important))
                        {
                            yield return item;
                        }
                    }
                    else if (IsColorLike(value))
                    {
                        foreach (var item in Box(PropertyId.BorderTopColor, PropertyId.BorderRightColor, PropertyId.BorderBottomColor, PropertyId.BorderLeftColor, [value], important))
                        {
                            yield return item;
                        }
                    }
                    else
                    {
                        foreach (var item in Box(PropertyId.BorderTopWidth, PropertyId.BorderRightWidth, PropertyId.BorderBottomWidth, PropertyId.BorderLeftWidth, [value], important))
                        {
                            yield return item;
                        }
                    }
                }
                break;
            // CSS Scroll-Driven Animations 1 §3.1 — `scroll-timeline: <name> <axis>?`.
            case "scroll-timeline":
                yield return new PropertyDeclaration(PropertyId.ScrollTimelineName, values[0], important);
                yield return new PropertyDeclaration(PropertyId.ScrollTimelineAxis, values.Count > 1 ? values[1] : new CssKeyword("block"), important);
                break;
            // CSS Scroll-Driven Animations 1 §4.1 — `view-timeline: <name> <axis>?`.
            case "view-timeline":
                yield return new PropertyDeclaration(PropertyId.ViewTimelineName, values[0], important);
                yield return new PropertyDeclaration(PropertyId.ViewTimelineAxis, values.Count > 1 ? values[1] : new CssKeyword("block"), important);
                break;
            // CSS Sizing 4 §4 — `contain-intrinsic-size: [ none | <length> ]{1,2}`
            // sets width then height (one value applies to both).
            case "contain-intrinsic-size":
                yield return new PropertyDeclaration(PropertyId.ContainIntrinsicWidth, values[0], important);
                yield return new PropertyDeclaration(PropertyId.ContainIntrinsicHeight, values.Count > 1 ? values[1] : values[0], important);
                break;
            // CSS Multicol 1 §7.1 — `columns: <'column-width'> || <'column-count'>`.
            // Each component is `auto` or a length (width) / integer (count).
            case "columns":
                {
                    CssValue? colWidth = null, colCount = null;
                    foreach (var value in values)
                    {
                        if (value is CssNumber)
                        {
                            colCount = value;
                        }
                        else if (value is CssLength)
                        {
                            colWidth = value;
                        }
                        else if (value is CssKeyword { Name: "auto" }) { /* applies to whichever is unset */ }
                        else
                        {
                            colWidth ??= value;
                        }
                    }
                    yield return new PropertyDeclaration(PropertyId.ColumnWidth, colWidth ?? new CssKeyword("auto"), important);
                    yield return new PropertyDeclaration(PropertyId.ColumnCount, colCount ?? new CssKeyword("auto"), important);
                }
                break;
            // CSS Multicol 1 §6 — `column-rule: <'column-rule-width'> || <'column-rule-style'> || <'column-rule-color'>`.
            case "column-rule":
                {
                    CssValue? rColor = null, rStyle = null, rWidth = null;
                    foreach (var value in values)
                    {
                        if (IsBorderStyle(value))
                        {
                            rStyle = value;
                        }
                        else if (IsColorLike(value))
                        {
                            rColor = value;
                        }
                        else
                        {
                            rWidth = value;
                        }
                    }
                    yield return new PropertyDeclaration(PropertyId.ColumnRuleColor, rColor ?? new CssKeyword("currentColor"), important);
                    yield return new PropertyDeclaration(PropertyId.ColumnRuleStyle, rStyle ?? new CssKeyword("none"), important);
                    yield return new PropertyDeclaration(PropertyId.ColumnRuleWidth, rWidth ?? new CssKeyword("medium"), important);
                }
                break;
            // CSS Basic UI 4 §3.4 — `outline: <'outline-color'> || <'outline-style'> || <'outline-width'>`.
            // Omitted longhands reset to their initial value.
            case "outline":
                {
                    CssValue? oColor = null, oStyle = null, oWidth = null;
                    foreach (var value in values)
                    {
                        if (IsBorderStyle(value))
                        {
                            oStyle = value;
                        }
                        else if (IsColorLike(value))
                        {
                            oColor = value;
                        }
                        else
                        {
                            oWidth = value;
                        }
                    }
                    yield return new PropertyDeclaration(PropertyId.OutlineColor, oColor ?? new CssKeyword("auto"), important);
                    yield return new PropertyDeclaration(PropertyId.OutlineStyle, oStyle ?? new CssKeyword("none"), important);
                    yield return new PropertyDeclaration(PropertyId.OutlineWidth, oWidth ?? new CssKeyword("medium"), important);
                }
                break;

            // ---- Flexbox shorthands ----
            case "flex":
                foreach (var item in ExpandFlex(values, important))
                {
                    yield return item;
                }

                break;
            case "flex-flow":
                foreach (var item in ExpandFlexFlow(values, important))
                {
                    yield return item;
                }

                break;

            // ---- Gap shorthands ----
            case "gap":
                yield return new PropertyDeclaration(PropertyId.RowGap, values[0], important);
                yield return new PropertyDeclaration(PropertyId.ColumnGap, values.Count > 1 ? values[1] : values[0], important);
                break;
            case "grid-gap":
                yield return new PropertyDeclaration(PropertyId.RowGap, values[0], important);
                yield return new PropertyDeclaration(PropertyId.ColumnGap, values.Count > 1 ? values[1] : values[0], important);
                break;
            case "grid-row-gap":
                yield return new PropertyDeclaration(PropertyId.RowGap, values[0], important);
                break;
            case "grid-column-gap":
                yield return new PropertyDeclaration(PropertyId.ColumnGap, values[0], important);
                break;

            // ---- Grid shorthands ----
            // TODO(lane-B): Refine grid-template / grid shorthand once track-list value parser exists.
            case "grid-template":
                foreach (var item in ExpandGridTemplate(values, important))
                {
                    yield return item;
                }

                break;
            case "grid":
                foreach (var item in ExpandGrid(values, important))
                {
                    yield return item;
                }

                break;
            case "grid-column":
                foreach (var item in ExpandSlashPair(PropertyId.GridColumnStart, PropertyId.GridColumnEnd, values, important))
                {
                    yield return item;
                }

                break;
            case "grid-row":
                foreach (var item in ExpandSlashPair(PropertyId.GridRowStart, PropertyId.GridRowEnd, values, important))
                {
                    yield return item;
                }

                break;
            case "grid-area":
                foreach (var item in ExpandGridArea(values, important))
                {
                    yield return item;
                }

                break;

            // ---- Place-* shorthands ----
            case "place-items":
                yield return new PropertyDeclaration(PropertyId.AlignItems, values[0], important);
                yield return new PropertyDeclaration(PropertyId.JustifyItems, values.Count > 1 ? values[1] : values[0], important);
                break;
            case "place-content":
                yield return new PropertyDeclaration(PropertyId.AlignContent, values[0], important);
                yield return new PropertyDeclaration(PropertyId.JustifyContent, values.Count > 1 ? values[1] : values[0], important);
                break;
            case "place-self":
                yield return new PropertyDeclaration(PropertyId.AlignSelf, values[0], important);
                yield return new PropertyDeclaration(PropertyId.JustifySelf, values.Count > 1 ? values[1] : values[0], important);
                break;

            // ---- Logical: margin/padding 2-value shorthands ----
            case "margin-inline":
                foreach (var item in TwoValue(PropertyId.MarginInlineStart, PropertyId.MarginInlineEnd, values, important))
                {
                    yield return item;
                }

                break;
            case "margin-block":
                foreach (var item in TwoValue(PropertyId.MarginBlockStart, PropertyId.MarginBlockEnd, values, important))
                {
                    yield return item;
                }

                break;
            case "padding-inline":
                foreach (var item in TwoValue(PropertyId.PaddingInlineStart, PropertyId.PaddingInlineEnd, values, important))
                {
                    yield return item;
                }

                break;
            case "padding-block":
                foreach (var item in TwoValue(PropertyId.PaddingBlockStart, PropertyId.PaddingBlockEnd, values, important))
                {
                    yield return item;
                }

                break;
            case "inset":
                foreach (var item in Box(PropertyId.Top, PropertyId.Right, PropertyId.Bottom, PropertyId.Left, values, important))
                {
                    yield return item;
                }

                break;
            case "inset-inline":
                foreach (var item in TwoValue(PropertyId.InsetInlineStart, PropertyId.InsetInlineEnd, values, important))
                {
                    yield return item;
                }

                break;
            case "inset-block":
                foreach (var item in TwoValue(PropertyId.InsetBlockStart, PropertyId.InsetBlockEnd, values, important))
                {
                    yield return item;
                }

                break;

            // ---- Logical: border shorthands ----
            case "border-inline-width":
                foreach (var item in TwoValue(PropertyId.BorderInlineStartWidth, PropertyId.BorderInlineEndWidth, values, important))
                {
                    yield return item;
                }

                break;
            case "border-inline-style":
                foreach (var item in TwoValue(PropertyId.BorderInlineStartStyle, PropertyId.BorderInlineEndStyle, values, important))
                {
                    yield return item;
                }

                break;
            case "border-inline-color":
                foreach (var item in TwoValue(PropertyId.BorderInlineStartColor, PropertyId.BorderInlineEndColor, values, important))
                {
                    yield return item;
                }

                break;
            case "border-block-width":
                foreach (var item in TwoValue(PropertyId.BorderBlockStartWidth, PropertyId.BorderBlockEndWidth, values, important))
                {
                    yield return item;
                }

                break;
            case "border-block-style":
                foreach (var item in TwoValue(PropertyId.BorderBlockStartStyle, PropertyId.BorderBlockEndStyle, values, important))
                {
                    yield return item;
                }

                break;
            case "border-block-color":
                foreach (var item in TwoValue(PropertyId.BorderBlockStartColor, PropertyId.BorderBlockEndColor, values, important))
                {
                    yield return item;
                }

                break;
            case "border-inline-start":
                foreach (var item in ExpandBorderSide(PropertyId.BorderInlineStartWidth, PropertyId.BorderInlineStartStyle, PropertyId.BorderInlineStartColor, values, important))
                {
                    yield return item;
                }

                break;
            case "border-inline-end":
                foreach (var item in ExpandBorderSide(PropertyId.BorderInlineEndWidth, PropertyId.BorderInlineEndStyle, PropertyId.BorderInlineEndColor, values, important))
                {
                    yield return item;
                }

                break;
            case "border-block-start":
                foreach (var item in ExpandBorderSide(PropertyId.BorderBlockStartWidth, PropertyId.BorderBlockStartStyle, PropertyId.BorderBlockStartColor, values, important))
                {
                    yield return item;
                }

                break;
            case "border-block-end":
                foreach (var item in ExpandBorderSide(PropertyId.BorderBlockEndWidth, PropertyId.BorderBlockEndStyle, PropertyId.BorderBlockEndColor, values, important))
                {
                    yield return item;
                }

                break;
            case "border-inline":
                foreach (var item in ExpandBorderSide(PropertyId.BorderInlineStartWidth, PropertyId.BorderInlineStartStyle, PropertyId.BorderInlineStartColor, values, important))
                {
                    yield return item;
                }

                foreach (var item in ExpandBorderSide(PropertyId.BorderInlineEndWidth, PropertyId.BorderInlineEndStyle, PropertyId.BorderInlineEndColor, values, important))
                {
                    yield return item;
                }

                break;
            case "border-block":
                foreach (var item in ExpandBorderSide(PropertyId.BorderBlockStartWidth, PropertyId.BorderBlockStartStyle, PropertyId.BorderBlockStartColor, values, important))
                {
                    yield return item;
                }

                foreach (var item in ExpandBorderSide(PropertyId.BorderBlockEndWidth, PropertyId.BorderBlockEndStyle, PropertyId.BorderBlockEndColor, values, important))
                {
                    yield return item;
                }

                break;

            // ---- Physical border-side shorthands (border-top, border-right, border-bottom, border-left) ----
            case "border-top":
                foreach (var item in ExpandBorderSide(PropertyId.BorderTopWidth, PropertyId.BorderTopStyle, PropertyId.BorderTopColor, values, important))
                {
                    yield return item;
                }

                break;
            case "border-right":
                foreach (var item in ExpandBorderSide(PropertyId.BorderRightWidth, PropertyId.BorderRightStyle, PropertyId.BorderRightColor, values, important))
                {
                    yield return item;
                }

                break;
            case "border-bottom":
                foreach (var item in ExpandBorderSide(PropertyId.BorderBottomWidth, PropertyId.BorderBottomStyle, PropertyId.BorderBottomColor, values, important))
                {
                    yield return item;
                }

                break;
            case "border-left":
                foreach (var item in ExpandBorderSide(PropertyId.BorderLeftWidth, PropertyId.BorderLeftStyle, PropertyId.BorderLeftColor, values, important))
                {
                    yield return item;
                }

                break;

            // ---- Scroll-margin / scroll-padding 4-value shorthands ----
            case "scroll-margin":
                foreach (var item in Box(PropertyId.ScrollMarginTop, PropertyId.ScrollMarginRight, PropertyId.ScrollMarginBottom, PropertyId.ScrollMarginLeft, values, important))
                {
                    yield return item;
                }

                break;
            case "scroll-padding":
                foreach (var item in Box(PropertyId.ScrollPaddingTop, PropertyId.ScrollPaddingRight, PropertyId.ScrollPaddingBottom, PropertyId.ScrollPaddingLeft, values, important))
                {
                    yield return item;
                }

                break;
            case "overscroll-behavior":
                yield return new PropertyDeclaration(PropertyId.OverscrollBehaviorX, values[0], important);
                yield return new PropertyDeclaration(PropertyId.OverscrollBehaviorY, values.Count > 1 ? values[1] : values[0], important);
                break;

            // ---- Text decoration shorthand ----
            case "text-decoration":
                foreach (var item in ExpandTextDecoration(values, important))
                {
                    yield return item;
                }

                break;

            // ---- Text shadow (CSS Text Decoration 3 §5) ----
            case "text-shadow":
                yield return new PropertyDeclaration(PropertyId.TextShadow, ParseTextShadow(values), important);
                break;

            // ---- white-space shorthand (CSS Text 4 §3) ----
            // Sets the white-space-collapse + text-wrap longhands from the legacy
            // single keyword, while preserving the `white-space` value itself so
            // existing consumers (and serialization) still see it.
            case "white-space":
                foreach (var item in ExpandWhiteSpace(values, important))
                {
                    yield return item;
                }

                break;

            // ---- List shorthand ----
            case "list-style":
                foreach (var item in ExpandListStyle(values, important))
                {
                    yield return item;
                }

                break;

            // ---- Transition / Animation shorthands (simplified: single layer only) ----
            // TODO(lane-B): Multi-layer comma-separated transition/animation requires CssValueList splitting on top-level commas.
            case "transition":
                foreach (var item in ExpandTransition(values, important))
                {
                    yield return item;
                }

                break;
            case "animation":
                foreach (var item in ExpandAnimation(values, important))
                {
                    yield return item;
                }

                break;

            // CSS Display 3 §2.1: the two-value `display` syntax
            // (<display-outside> <display-inside>) is normalized to its
            // single-keyword legacy equivalent.
            case "display" when values.Count == 2
                && values[0] is CssKeyword o && values[1] is CssKeyword inr:
                {
                    var (a, b) = (o.Name, inr.Name);
                    // Accept either order of outer/inner.
                    var outer = a is "block" or "inline" or "run-in" ? a : b;
                    var inner = outer == a ? b : a;
                    var normalized = (outer, inner) switch
                    {
                        ("block", "flow") => "block",
                        ("block", "flow-root") => "flow-root",
                        ("inline", "flow") => "inline",
                        ("inline", "flow-root") => "inline-block",
                        ("block", "flex") => "flex",
                        ("inline", "flex") => "inline-flex",
                        ("block", "grid") => "grid",
                        ("inline", "grid") => "inline-grid",
                        ("block", "table") => "table",
                        ("inline", "table") => "inline-table",
                        _ => null,
                    };
                    if (normalized is not null)
                    {
                        yield return new PropertyDeclaration(PropertyId.Display, new CssKeyword(normalized), important);
                    }
                    else
                    {
                        yield return new PropertyDeclaration(PropertyId.Display, new CssValueList(values), important);
                    }
                }
                break;
            // CSS Compositing 1 §5.2: `background-blend-mode` is a comma-separated
            // list of blend keywords (one per background layer). Split on the
            // top-level comma artifacts so the value is one clean entry per layer.
            case "background-blend-mode":
                {
                    var modes = values.Where(v => v is not CssKeyword { Name: "" or "," }).ToList();
                    yield return new PropertyDeclaration(PropertyId.BackgroundBlendMode,
                        modes.Count == 1 ? modes[0] : new CssValueList(modes), important);
                }
                break;

            // CSS Masking 1 §7 — clip-path: none | <basic-shape> || <geometry-box> | url()
            case "clip-path":
                {
                    var clipPath = CssBasicShapeParser.TryParseClipPath(values);
                    if (clipPath is not null)
                    {
                        yield return new PropertyDeclaration(PropertyId.ClipPath, clipPath, important);
                    }
                    else if (values.Count == 1)
                    {
                        yield return new PropertyDeclaration(PropertyId.ClipPath, values[0], important);
                    }
                }
                break;

            default:
                if (TryGetPropertyId(name, out var id))
                {
                    var value = values.Count == 1 ? values[0] : new CssValueList(values);
                    // Drop declarations whose value is invalid for the property
                    // (CSS Syntax 3 §8.2). Validation is permissive by default —
                    // see IsValidLonghandValue — so only properties with an
                    // explicit rule reject anything.
                    if (values.Count == 1 && !IsValidLonghandValue(id, value))
                    {
                        break;
                    }

                    yield return new PropertyDeclaration(id, value, important);
                }
                break;
        }
    }

    // Per-property value validation. Permissive by default (returns true): only
    // properties with an explicit rule reject values, so adding a rule never
    // changes behavior for any other property. CSS-wide keywords always pass.
    internal static bool IsValidLonghandValue(PropertyId id, CssValue value)
    {
        if (value is CssKeyword { Name: "inherit" or "initial" or "unset" or "revert" or "revert-layer" })
        {
            return true;
        }

        return id switch
        {
            // CSS Overscroll Behavior 1 §2: auto | contain | none.
            PropertyId.OverscrollBehaviorX or PropertyId.OverscrollBehaviorY
                => value is CssKeyword { Name: "auto" or "contain" or "none" },
            // CSS Box Model 3 §4: padding longhands must be non-negative.
            PropertyId.PaddingTop or PropertyId.PaddingRight or PropertyId.PaddingBottom or PropertyId.PaddingLeft
                or PropertyId.PaddingInlineStart or PropertyId.PaddingInlineEnd
                or PropertyId.PaddingBlockStart or PropertyId.PaddingBlockEnd
                => !IsNegativeLengthValue(value),
            _ => true,
        };
    }

    // True when the value is a statically-negative length / number / percentage.
    // calc() and keywords are not statically negative, so they pass.
    private static bool IsNegativeLengthValue(CssValue value) => value switch
    {
        CssLength l => l.Value < 0,
        CssNumber n => n.Value < 0,
        CssPercentage p => p.Value < 0,
        CssDimension d => d.Value < 0,
        _ => false,
    };

    private static IEnumerable<PropertyDeclaration> Box(
        PropertyId top,
        PropertyId right,
        PropertyId bottom,
        PropertyId left,
        List<CssValue> values,
        bool important)
    {
        CssValue[] actual = values.Count switch
        {
            1 => [values[0], values[0], values[0], values[0]],
            2 => [values[0], values[1], values[0], values[1]],
            3 => [values[0], values[1], values[2], values[1]],
            _ => [values[0], values[1], values[2], values[3]],
        };

        yield return new PropertyDeclaration(top, actual[0], important);
        yield return new PropertyDeclaration(right, actual[1], important);
        yield return new PropertyDeclaration(bottom, actual[2], important);
        yield return new PropertyDeclaration(left, actual[3], important);
    }

    private static IEnumerable<PropertyDeclaration> TwoValue(
        PropertyId start,
        PropertyId end,
        List<CssValue> values,
        bool important)
    {
        yield return new PropertyDeclaration(start, values[0], important);
        yield return new PropertyDeclaration(end, values.Count > 1 ? values[1] : values[0], important);
    }

    private static IEnumerable<PropertyDeclaration> ExpandFont(List<CssValue> values, bool important)
    {
        // System font keywords (§4.8.1): set all longhands from UA metrics. We
        // don't have per-system metrics, so emit a sensible default so the
        // shorthand isn't a no-op.
        if (values.Count == 1 && values[0] is CssKeyword sys && IsSystemFontKeyword(sys.Name))
        {
            yield return new PropertyDeclaration(PropertyId.FontStyle, new CssKeyword("normal"), important);
            yield return new PropertyDeclaration(PropertyId.FontWeight, new CssKeyword("normal"), important);
            yield return new PropertyDeclaration(PropertyId.FontSize, new CssKeyword("medium"), important);
            yield return new PropertyDeclaration(PropertyId.LineHeight, new CssKeyword("normal"), important);
            yield return new PropertyDeclaration(PropertyId.FontFamily, new CssKeyword("sans-serif"), important);
            yield break;
        }

        // font-size is required and is the pivot: everything before it is the
        // optional style/variant/weight/stretch run; everything after is the
        // optional `/ line-height` then the required font-family.
        var sizeIdx = values.FindIndex(IsFontSizeValue);
        if (sizeIdx < 0)
        {
            yield break; // no font-size → invalid shorthand, dropped.
        }

        CssValue? style = null, weight = null, stretch = null;
        for (var i = 0; i < sizeIdx; i++)
        {
            switch (values[i])
            {
                case CssKeyword { Name: "italic" or "oblique" } sv: style = sv; break;
                case CssKeyword { Name: "bold" or "bolder" or "lighter" } wv: weight = wv; break;
                case CssNumber wn: weight = wn; break; // numeric font-weight
                case CssKeyword k when IsFontStretchKeyword(k.Name): stretch = k; break;
                    // `normal`, `small-caps` (variant) etc. fall through (defaults).
            }
        }

        var idx = sizeIdx + 1;
        CssValue? lineHeight = null;
        if (idx < values.Count && values[idx] is CssKeyword { Name: "/" })
        {
            idx++;
            if (idx < values.Count) { lineHeight = values[idx]; idx++; }
        }

        var familyValues = values.Skip(idx).ToList();
        if (familyValues.Count == 0)
        {
            yield break; // font-family is required.
        }

        yield return new PropertyDeclaration(PropertyId.FontStyle, style ?? new CssKeyword("normal"), important);
        yield return new PropertyDeclaration(PropertyId.FontWeight, weight ?? new CssKeyword("normal"), important);
        if (stretch is not null)
        {
            yield return new PropertyDeclaration(PropertyId.FontStretch, stretch, important);
        }

        yield return new PropertyDeclaration(PropertyId.FontSize, values[sizeIdx], important);
        yield return new PropertyDeclaration(PropertyId.LineHeight, lineHeight ?? new CssKeyword("normal"), important);
        yield return new PropertyDeclaration(PropertyId.FontFamily,
            familyValues.Count == 1 ? familyValues[0] : new CssValueList(familyValues), important);
    }

    private static bool IsFontSizeValue(CssValue v)
        => v is CssLength or CssDimension or CssPercentage
            || (v is CssKeyword k && (k.Name is "xx-small" or "x-small" or "small" or "medium"
                or "large" or "x-large" or "xx-large" or "xxx-large" or "smaller" or "larger"));

    private static bool IsFontStretchKeyword(string name)
        => name is "ultra-condensed" or "extra-condensed" or "condensed" or "semi-condensed"
            or "semi-expanded" or "expanded" or "extra-expanded" or "ultra-expanded";

    private static bool IsSystemFontKeyword(string name)
        => name is "caption" or "icon" or "menu" or "message-box" or "small-caption" or "status-bar";

    private static IEnumerable<PropertyDeclaration> ExpandBackground(List<CssValue> values, bool important)
    {
        // CSS Backgrounds 3 §3.4 — the `background` shorthand sets the eight
        // background-* longhands. The value is a comma-separated list of layers
        // ( [ <bg-layer> , ]* <final-bg-layer> ). `background-color` may only
        // appear in the final layer; every layer that omits a given longhand
        // resets it to its initial value.
        var layers = SplitTopLevelCommas(values);
        if (layers.Count == 0)
        {
            yield break;
        }

        // Per-layer accumulators for each layered longhand. Background-color is
        // a single value taken from the final layer only.
        var images = new List<CssValue>();
        var positions = new List<CssValue>();
        var sizes = new List<CssValue>();
        var repeats = new List<CssValue>();
        var attachments = new List<CssValue>();
        var origins = new List<CssValue>();
        var clips = new List<CssValue>();
        CssValue color = CssColor.Transparent;

        for (var i = 0; i < layers.Count; i++)
        {
            var isFinal = i == layers.Count - 1;
            var (parsed, layerColor) = ParseBackgroundLayer(layers[i], isFinal);
            if (isFinal && layerColor is not null)
            {
                color = layerColor;
            }

            images.Add(parsed.Image);
            positions.Add(parsed.Position);
            sizes.Add(parsed.Size);
            repeats.Add(parsed.Repeat);
            attachments.Add(parsed.Attachment);
            origins.Add(parsed.Origin);
            clips.Add(parsed.Clip);
        }

        // CSS Backgrounds 3 §3.4 — background-color is not layered; it takes the
        // final layer's color (initial transparent otherwise).
        yield return new PropertyDeclaration(PropertyId.BackgroundColor, color, important);
        yield return Layered(PropertyId.BackgroundImage, images, important);
        yield return Layered(PropertyId.BackgroundPosition, positions, important);
        yield return Layered(PropertyId.BackgroundSize, sizes, important);
        yield return Layered(PropertyId.BackgroundRepeat, repeats, important);
        yield return Layered(PropertyId.BackgroundAttachment, attachments, important);
        yield return Layered(PropertyId.BackgroundOrigin, origins, important);
        yield return Layered(PropertyId.BackgroundClip, clips, important);

        static PropertyDeclaration Layered(PropertyId id, List<CssValue> layerValues, bool important)
            => new(id, layerValues.Count == 1 ? layerValues[0] : new CssValueList(layerValues), important);
    }

    private readonly record struct BackgroundLayer(
        CssValue Image,
        CssValue Position,
        CssValue Size,
        CssValue Repeat,
        CssValue Attachment,
        CssValue Origin,
        CssValue Clip);

    /// <summary>
    /// Parse a single <c>&lt;bg-layer&gt;</c> per CSS Backgrounds 3 §3.4. Any
    /// omitted component resets to its initial value. The optional
    /// <c>&lt;position&gt; [ / &lt;size&gt; ]?</c> is slash-separated. When two
    /// box keywords (border-box / padding-box / content-box) are present, the
    /// first sets <c>background-origin</c> and the second <c>background-clip</c>;
    /// a single box keyword sets both. <paramref name="allowColor"/> is true only
    /// for the final layer, where a <c>background-color</c> is permitted.
    /// </summary>
    private static (BackgroundLayer Layer, CssValue? Color) ParseBackgroundLayer(
        List<CssValue> values,
        bool allowColor)
    {
        CssValue? image = null;
        CssValue? repeat = null;
        CssValue? attachment = null;
        CssValue? color = null;
        var boxes = new List<CssValue>();
        var positionValues = new List<CssValue>();
        var sizeValues = new List<CssValue>();

        // The optional `<position> [ / <size> ]?` puts the size right after the
        // slash. Only the size component(s) immediately after the slash belong
        // to background-size; any following keywords (attachment, box) are
        // parsed normally once the size run ends.
        var afterSlash = false;
        foreach (var v in values)
        {
            if (v is CssKeyword { Name: "/" })
            {
                afterSlash = true;
                continue;
            }

            if (afterSlash)
            {
                if (IsBackgroundSizeComponent(v))
                {
                    sizeValues.Add(v);
                    continue;
                }
                afterSlash = false; // size run ended; fall through to normal routing.
            }

            if (image is null && IsBackgroundImage(v))
            {
                image = v;
            }
            else if (repeat is null && v is CssKeyword rk && IsBackgroundRepeatKeyword(rk.Name))
            {
                repeat = v;
            }
            else if (attachment is null && v is CssKeyword ak && IsBackgroundAttachmentKeyword(ak.Name))
            {
                attachment = v;
            }
            else if (v is CssKeyword bk && IsBackgroundBoxKeyword(bk.Name))
            {
                boxes.Add(v);
            }
            else if (v is CssLength or CssPercentage or CssNumber
                || (v is CssKeyword pk && IsBackgroundPositionKeyword(pk.Name)))
            {
                positionValues.Add(v);
            }
            else if (allowColor && color is null && IsColorLike(v))
            {
                color = v;
            }
        }

        var size = sizeValues.Count switch
        {
            0 => new CssKeyword("auto"),
            1 => sizeValues[0],
            _ => new CssValueList(sizeValues),
        };

        CssValue position = positionValues.Count switch
        {
            0 => new CssKeyword("0% 0%"),
            1 => positionValues[0],
            _ => new CssValueList(positionValues),
        };

        // First box → origin, second → clip; one box sets both (§3.4).
        var origin = boxes.Count > 0 ? boxes[0] : new CssKeyword("padding-box");
        var clip = boxes.Count > 1 ? boxes[1] : (boxes.Count == 1 ? boxes[0] : new CssKeyword("border-box"));

        var layer = new BackgroundLayer(
            image ?? new CssKeyword("none"),
            position,
            size,
            repeat ?? new CssKeyword("repeat"),
            attachment ?? new CssKeyword("scroll"),
            origin,
            clip);
        return (layer, color);
    }

    /// <summary>
    /// CSS Masking 1 §7.5 — the <c>mask</c> shorthand sets the eight mask-* longhands.
    /// Single-layer support: classifies each component and resets omitted longhands to
    /// their initial value. The optional <c>&lt;position&gt; [ / &lt;size&gt; ]?</c> is
    /// slash-separated; two geometry-box keywords set mask-origin then mask-clip.
    /// </summary>
    private static IEnumerable<PropertyDeclaration> ExpandMask(List<CssValue> values, bool important)
    {
        CssValue? image = null, mode = null, repeat = null, composite = null;
        var boxes = new List<CssValue>();
        var positionValues = new List<CssValue>();
        var sizeValues = new List<CssValue>();
        var afterSlash = false;

        foreach (var v in values)
        {
            if (v is CssKeyword { Name: "/" }) { afterSlash = true; continue; }
            if (afterSlash)
            {
                if (IsBackgroundSizeComponent(v)) { sizeValues.Add(v); continue; }
                afterSlash = false;
            }

            if (image is null && IsBackgroundImage(v))
            {
                image = v;
            }
            else if (mode is null && v is CssKeyword mk && IsMaskModeKeyword(mk.Name))
            {
                mode = v;
            }
            else if (repeat is null && v is CssKeyword rk && IsBackgroundRepeatKeyword(rk.Name))
            {
                repeat = v;
            }
            else if (composite is null && v is CssKeyword ck && IsMaskCompositeKeyword(ck.Name))
            {
                composite = v;
            }
            else if (v is CssKeyword bk && IsMaskGeometryBoxKeyword(bk.Name))
            {
                boxes.Add(v);
            }
            else if (v is CssLength or CssPercentage or CssNumber
                || (v is CssKeyword pk && IsBackgroundPositionKeyword(pk.Name)))
            {
                positionValues.Add(v);
            }
        }

        var size = sizeValues.Count switch { 0 => new CssKeyword("auto"), 1 => sizeValues[0], _ => new CssValueList(sizeValues) };
        CssValue position = positionValues.Count switch { 0 => new CssKeyword("0% 0%"), 1 => positionValues[0], _ => new CssValueList(positionValues) };
        var origin = boxes.Count > 0 ? boxes[0] : new CssKeyword("border-box");
        var clip = boxes.Count > 1 ? boxes[1] : (boxes.Count == 1 ? boxes[0] : new CssKeyword("border-box"));

        yield return new PropertyDeclaration(PropertyId.MaskImage, image ?? new CssKeyword("none"), important);
        yield return new PropertyDeclaration(PropertyId.MaskMode, mode ?? new CssKeyword("match-source"), important);
        yield return new PropertyDeclaration(PropertyId.MaskPosition, position, important);
        yield return new PropertyDeclaration(PropertyId.MaskSize, size, important);
        yield return new PropertyDeclaration(PropertyId.MaskRepeat, repeat ?? new CssKeyword("repeat"), important);
        yield return new PropertyDeclaration(PropertyId.MaskOrigin, origin, important);
        yield return new PropertyDeclaration(PropertyId.MaskClip, clip, important);
        yield return new PropertyDeclaration(PropertyId.MaskComposite, composite ?? new CssKeyword("add"), important);
    }

    private static bool IsMaskModeKeyword(string name)
        => name is "alpha" or "luminance" or "match-source";

    private static bool IsMaskCompositeKeyword(string name)
        => name is "add" or "subtract" or "intersect" or "exclude";

    private static bool IsMaskGeometryBoxKeyword(string name)
        => name is "border-box" or "padding-box" or "content-box"
            or "fill-box" or "stroke-box" or "view-box" or "no-clip";

    private static bool IsBackgroundImage(CssValue v)
        => v is CssUrl
            || v is CssGradient
            || v is CssKeyword { Name: "none" }
            || v is CssFunctionValue { Name: "linear-gradient" or "radial-gradient" or "conic-gradient" or "repeating-linear-gradient" or "repeating-radial-gradient" or "repeating-conic-gradient" or "image-set" or "url" };

    private static bool IsBackgroundRepeatKeyword(string name)
        => name is "repeat" or "no-repeat" or "repeat-x" or "repeat-y" or "space" or "round";

    private static bool IsBackgroundSizeComponent(CssValue v)
        => v is CssLength or CssPercentage or CssNumber
            or CssKeyword { Name: "auto" or "cover" or "contain" }
            or CssFunctionValue { Name: "calc" };

    private static bool IsBackgroundAttachmentKeyword(string name)
        => name is "scroll" or "fixed" or "local";

    private static bool IsBackgroundBoxKeyword(string name)
        => name is "border-box" or "padding-box" or "content-box";

    private static bool IsBackgroundPositionKeyword(string name)
        => name is "left" or "right" or "top" or "bottom" or "center";

    private static IEnumerable<PropertyDeclaration> ExpandFlex(List<CssValue> values, bool important)
    {
        // flex: none | <flex-grow> [<flex-shrink> || <flex-basis>] | auto
        if (values.Count == 1 && values[0] is CssKeyword { Name: "none" })
        {
            yield return new PropertyDeclaration(PropertyId.FlexGrow, new CssNumber(0), important);
            yield return new PropertyDeclaration(PropertyId.FlexShrink, new CssNumber(0), important);
            yield return new PropertyDeclaration(PropertyId.FlexBasis, new CssKeyword("auto"), important);
            yield break;
        }
        if (values.Count == 1 && values[0] is CssKeyword { Name: "auto" })
        {
            yield return new PropertyDeclaration(PropertyId.FlexGrow, new CssNumber(1), important);
            yield return new PropertyDeclaration(PropertyId.FlexShrink, new CssNumber(1), important);
            yield return new PropertyDeclaration(PropertyId.FlexBasis, new CssKeyword("auto"), important);
            yield break;
        }
        if (values.Count == 1 && values[0] is CssKeyword { Name: "initial" })
        {
            yield return new PropertyDeclaration(PropertyId.FlexGrow, new CssNumber(0), important);
            yield return new PropertyDeclaration(PropertyId.FlexShrink, new CssNumber(1), important);
            yield return new PropertyDeclaration(PropertyId.FlexBasis, new CssKeyword("auto"), important);
            yield break;
        }

        CssValue grow = new CssNumber(1);
        CssValue shrink = new CssNumber(1);
        CssValue basis = new CssLength(0, CssLengthUnit.Px);
        var seenGrow = false;
        var seenShrink = false;
        var seenBasis = false;

        foreach (var v in values)
        {
            if (!seenGrow && v is CssNumber gNum)
            {
                grow = gNum;
                seenGrow = true;
            }
            else if (seenGrow && !seenShrink && v is CssNumber sNum)
            {
                shrink = sNum;
                seenShrink = true;
            }
            else if (!seenBasis && IsBasisValue(v))
            {
                basis = v;
                seenBasis = true;
            }
        }

        // single number means flex-grow only; basis becomes 0.
        if (values.Count == 1 && seenGrow && !seenBasis)
        {
            basis = new CssLength(0, CssLengthUnit.Px);
        }

        yield return new PropertyDeclaration(PropertyId.FlexGrow, grow, important);
        yield return new PropertyDeclaration(PropertyId.FlexShrink, shrink, important);
        yield return new PropertyDeclaration(PropertyId.FlexBasis, basis, important);
    }

    private static bool IsBasisValue(CssValue value)
        => value is CssLength or CssPercentage or CssKeyword { Name: "auto" or "content" or "min-content" or "max-content" or "fit-content" } or CssFunctionValue { Name: "fit-content" or "calc" };

    private static IEnumerable<PropertyDeclaration> ExpandFlexFlow(List<CssValue> values, bool important)
    {
        foreach (var v in values)
        {
            if (v is CssKeyword k)
            {
                if (k.Name is "row" or "row-reverse" or "column" or "column-reverse")
                {
                    yield return new PropertyDeclaration(PropertyId.FlexDirection, v, important);
                }
                else if (k.Name is "nowrap" or "wrap" or "wrap-reverse")
                {
                    yield return new PropertyDeclaration(PropertyId.FlexWrap, v, important);
                }
            }
        }
    }

    private static IEnumerable<PropertyDeclaration> ExpandSlashPair(
        PropertyId start,
        PropertyId end,
        List<CssValue> values,
        bool important)
    {
        var (before, after) = SplitOnSlash(values);
        var startValue = before.Count == 1 ? before[0] : new CssValueList(before);
        yield return new PropertyDeclaration(start, startValue, important);
        if (after is null)
        {
            yield return new PropertyDeclaration(end, startValue, important);
        }
        else
        {
            var endValue = after.Count == 1 ? after[0] : new CssValueList(after);
            yield return new PropertyDeclaration(end, endValue, important);
        }
    }

    private static (List<CssValue> Before, List<CssValue>? After) SplitOnSlash(List<CssValue> values)
    {
        var idx = values.FindIndex(v => v is CssKeyword { Name: "/" });
        if (idx < 0)
        {
            return (values, null);
        }

        return (values.Take(idx).ToList(), values.Skip(idx + 1).ToList());
    }

    private static IEnumerable<PropertyDeclaration> ExpandGridArea(List<CssValue> values, bool important)
    {
        // grid-area: <row-start> [/ <column-start> [/ <row-end> [/ <column-end>]]]
        var parts = new List<List<CssValue>>();
        var current = new List<CssValue>();
        foreach (var v in values)
        {
            if (v is CssKeyword { Name: "/" })
            {
                parts.Add(current);
                current = [];
            }
            else
            {
                current.Add(v);
            }
        }
        parts.Add(current);

        CssValue Pick(int i) => parts.Count > i && parts[i].Count > 0
            ? (parts[i].Count == 1 ? parts[i][0] : new CssValueList(parts[i]))
            : new CssKeyword("auto");

        var rowStart = Pick(0);
        var colStart = parts.Count > 1 ? Pick(1) : rowStart;
        var rowEnd = parts.Count > 2 ? Pick(2) : rowStart;
        var colEnd = parts.Count > 3 ? Pick(3) : colStart;

        yield return new PropertyDeclaration(PropertyId.GridRowStart, rowStart, important);
        yield return new PropertyDeclaration(PropertyId.GridColumnStart, colStart, important);
        yield return new PropertyDeclaration(PropertyId.GridRowEnd, rowEnd, important);
        yield return new PropertyDeclaration(PropertyId.GridColumnEnd, colEnd, important);
    }

    private static IEnumerable<PropertyDeclaration> ExpandGridTemplate(List<CssValue> values, bool important)
    {
        // Minimal: grid-template: none | <rows> / <columns>
        if (values.Count == 1 && values[0] is CssKeyword { Name: "none" })
        {
            yield return new PropertyDeclaration(PropertyId.GridTemplateRows, new CssKeyword("none"), important);
            yield return new PropertyDeclaration(PropertyId.GridTemplateColumns, new CssKeyword("none"), important);
            yield return new PropertyDeclaration(PropertyId.GridTemplateAreas, new CssKeyword("none"), important);
            yield break;
        }

        var (before, after) = SplitOnSlash(values);
        if (after is not null)
        {
            yield return new PropertyDeclaration(PropertyId.GridTemplateRows,
                before.Count == 1 ? before[0] : new CssValueList(before), important);
            yield return new PropertyDeclaration(PropertyId.GridTemplateColumns,
                after.Count == 1 ? after[0] : new CssValueList(after), important);
        }
        else
        {
            yield return new PropertyDeclaration(PropertyId.GridTemplateRows,
                before.Count == 1 ? before[0] : new CssValueList(before), important);
        }
    }

    private static IEnumerable<PropertyDeclaration> ExpandGrid(List<CssValue> values, bool important)
    {
        // TODO(lane-B): Full grid shorthand parsing — for now route as grid-template.
        foreach (var item in ExpandGridTemplate(values, important))
        {
            yield return item;
        }
    }

    private static IEnumerable<PropertyDeclaration> ExpandBorderSide(
        PropertyId widthId,
        PropertyId styleId,
        PropertyId colorId,
        List<CssValue> values,
        bool important)
    {
        foreach (var value in values)
        {
            if (IsBorderStyle(value))
            {
                yield return new PropertyDeclaration(styleId, value, important);
            }
            else if (IsColorLike(value))
            {
                yield return new PropertyDeclaration(colorId, value, important);
            }
            else
            {
                yield return new PropertyDeclaration(widthId, value, important);
            }
        }
    }

    private static IEnumerable<PropertyDeclaration> ExpandTextDecoration(List<CssValue> values, bool important)
    {
        foreach (var v in values)
        {
            if (v is CssKeyword k)
            {
                if (k.Name is "none" or "underline" or "overline" or "line-through" or "blink")
                {
                    yield return new PropertyDeclaration(PropertyId.TextDecorationLine, v, important);
                }
                else if (k.Name is "solid" or "double" or "dotted" or "dashed" or "wavy")
                {
                    yield return new PropertyDeclaration(PropertyId.TextDecorationStyle, v, important);
                }
                else if (IsColorLike(v))
                {
                    yield return new PropertyDeclaration(PropertyId.TextDecorationColor, v, important);
                }
                else
                {
                    yield return new PropertyDeclaration(PropertyId.TextDecorationLine, v, important);
                }
            }
            else if (IsColorLike(v))
            {
                yield return new PropertyDeclaration(PropertyId.TextDecorationColor, v, important);
            }
            else if (v is CssLength or CssPercentage)
            {
                yield return new PropertyDeclaration(PropertyId.TextDecorationThickness, v, important);
            }
        }
    }

    /// <summary>
    /// Parse the <c>text-shadow</c> value into a typed <see cref="CssTextShadow"/>
    /// (CSS Text Decoration 3 §5). Grammar per layer:
    /// <c>none | [ &lt;color&gt;? &amp;&amp; &lt;length&gt;{2,3} ]#</c>. Layers are
    /// comma separated; within a layer the optional color may lead or trail the
    /// two/three lengths (offset-x, offset-y, [blur]). Absolute-unit lengths are
    /// resolved to px; relative units keep their face value (best-effort, no
    /// computed-style context is available at parse time). Malformed layers are
    /// dropped.
    /// </summary>
    private static CssTextShadow ParseTextShadow(List<CssValue> values)
    {
        if (values.Count == 1 && values[0] is CssKeyword { Name: "none" })
        {
            return CssTextShadow.None;
        }

        var layers = new List<CssTextShadowLayer>();
        foreach (var layer in SplitTopLevelCommas(values))
        {
            CssColor? color = null;
            var lengths = new List<double>(3);
            var malformed = false;
            foreach (var v in layer)
            {
                switch (v)
                {
                    case CssLength len:
                        lengths.Add(len.Unit.IsAbsolute() ? len.Unit.AbsoluteToPx(len.Value) : len.Value);
                        break;
                    case CssNumber num:
                        // Tolerate unit-less zero (and other bare numbers) as px.
                        lengths.Add(num.Value);
                        break;
                    case CssColor c:
                        color = c;
                        break;
                    case CssKeyword { Name: "currentColor" or "currentcolor" }:
                        color = null; // currentColor is the default; the painter substitutes.
                        break;
                    default:
                        malformed = true;
                        break;
                }
            }

            if (malformed || lengths.Count is < 2 or > 3)
            {
                continue;
            }

            var offsetX = lengths[0];
            var offsetY = lengths[1];
            var blur = lengths.Count == 3 ? Math.Max(0, lengths[2]) : 0d;
            layers.Add(new CssTextShadowLayer(offsetX, offsetY, blur, color));
        }

        return layers.Count == 0 ? CssTextShadow.None : new CssTextShadow(layers);
    }

    private static IEnumerable<PropertyDeclaration> ExpandWhiteSpace(List<CssValue> values, bool important)
    {
        // CSS Text 4 §3 maps the legacy `white-space` keyword onto the
        // white-space-collapse + text-wrap longhands. We emit those longhands
        // *and* keep the `white-space` longhand itself so layout's legacy
        // fast-path and DevTools-style serialization both keep working.
        var keyword = values[0] is CssKeyword k ? k.Name.ToLowerInvariant() : "normal";

        // Pass values like `normal` / global keywords (inherit/initial/unset)
        // through to the white-space longhand untouched.
        yield return new PropertyDeclaration(PropertyId.WhiteSpace, values[0], important);

        (string collapse, string wrap)? mapped = keyword switch
        {
            "normal" => ("collapse", "wrap"),
            "nowrap" => ("collapse", "nowrap"),
            "pre" => ("preserve", "nowrap"),
            "pre-wrap" => ("preserve", "wrap"),
            "pre-line" => ("preserve-breaks", "wrap"),
            _ => null,
        };
        if (mapped is { } m)
        {
            yield return new PropertyDeclaration(PropertyId.WhiteSpaceCollapse, new CssKeyword(m.collapse), important);
            yield return new PropertyDeclaration(PropertyId.TextWrap, new CssKeyword(m.wrap), important);
        }
    }
    private static IEnumerable<PropertyDeclaration> ExpandListStyle(List<CssValue> values, bool important)
    {
        // CSS Lists 3 §2.5 — list-style: <list-style-type> || <list-style-position> || <list-style-image>
        // `none` is ambiguous (it can satisfy list-style-type OR list-style-image);
        // per spec the first `none` sets list-style-type and a second sets the image,
        // but the common case is a single `none` meaning "no marker", which we map to
        // the type. Author CSS like `list-style: square inside` sets two longhands;
        // an unspecified longhand is reset to its initial value.
        CssValue? type = null;
        CssValue? position = null;
        CssValue? image = null;
        var sawNone = false;

        foreach (var v in values)
        {
            if (v is CssUrl || v is CssFunctionValue { Name: "linear-gradient" or "radial-gradient" or "conic-gradient" or "image-set" or "url" })
            {
                image ??= v;
            }
            else if (v is CssKeyword k)
            {
                if (k.Name is "inside" or "outside")
                {
                    position ??= v;
                }
                else if (k.Name == "none")
                {
                    // First none → type; a second none → image (per §2.5).
                    if (!sawNone) { type ??= new CssKeyword("none"); sawNone = true; }
                    else
                    {
                        image ??= new CssKeyword("none");
                    }
                }
                else if (IsListStyleTypeKeyword(k.Name))
                {
                    type ??= v;
                }
                else
                {
                    type ??= v; // custom @counter-style names etc.
                }
            }
            else if (v is CssString)
            {
                // A <string> in list-style is a custom marker counter symbol;
                // route it to list-style-type so the marker can render it.
                type ??= v;
            }
        }

        yield return new PropertyDeclaration(PropertyId.ListStyleType, type ?? new CssKeyword("disc"), important);
        yield return new PropertyDeclaration(PropertyId.ListStylePosition, position ?? new CssKeyword("outside"), important);
        yield return new PropertyDeclaration(PropertyId.ListStyleImage, image ?? new CssKeyword("none"), important);
    }

    private static bool IsListStyleTypeKeyword(string name)
        => name is "disc" or "circle" or "square" or "decimal" or "decimal-leading-zero"
            or "lower-alpha" or "upper-alpha" or "lower-latin" or "upper-latin"
            or "lower-roman" or "upper-roman" or "lower-greek"
            or "armenian" or "georgian" or "none";

    private static IEnumerable<PropertyDeclaration> ExpandTransition(List<CssValue> values, bool important)
    {
        // Simplified single-layer transition shorthand.
        var sawDuration = false;
        foreach (var v in values)
        {
            if (v is CssTime || v is CssDimension { Unit: "s" or "ms" })
            {
                if (!sawDuration)
                {
                    yield return new PropertyDeclaration(PropertyId.TransitionDuration, v, important);
                    sawDuration = true;
                }
                else
                {
                    yield return new PropertyDeclaration(PropertyId.TransitionDelay, v, important);
                }
            }
            else if (v is CssKeyword k && IsTimingFunctionKeyword(k.Name))
            {
                yield return new PropertyDeclaration(PropertyId.TransitionTimingFunction, v, important);
            }
            else if (v is CssFunctionValue f && IsTimingFunctionName(f.Name))
            {
                yield return new PropertyDeclaration(PropertyId.TransitionTimingFunction, v, important);
            }
            else if (v is CssKeyword propKw)
            {
                yield return new PropertyDeclaration(PropertyId.TransitionProperty, propKw, important);
            }
        }
    }

    private static IEnumerable<PropertyDeclaration> ExpandAnimation(List<CssValue> values, bool important)
    {
        // Split on top-level commas. The value parser emits empty-name keywords
        // for separator commas (verified by probe). Each segment is one layer
        // per CSS Animations 1 §4.1.
        var layers = SplitTopLevelCommas(values);

        // Collect per-layer values for each Animation* longhand.
        var names = new List<CssValue>();
        var durations = new List<CssValue>();
        var delays = new List<CssValue>();
        var timings = new List<CssValue>();
        var iterations = new List<CssValue>();
        var directions = new List<CssValue>();
        var fills = new List<CssValue>();
        var playStates = new List<CssValue>();

        foreach (var layer in layers)
        {
            string? name = null;
            CssValue? duration = null, delay = null, timing = null, iteration = null;
            CssValue? direction = null, fill = null, playState = null;

            foreach (var v in layer)
            {
                if (v is CssTime || v is CssDimension { Unit: "s" or "ms" })
                {
                    if (duration is null)
                    {
                        duration = v;
                    }
                    else
                    {
                        delay ??= v;
                    }
                }
                else if (v is CssNumber)
                {
                    iteration ??= v;
                }
                else if (v is CssKeyword k)
                {
                    if (k.Name is "infinite")
                    {
                        iteration ??= v;
                    }
                    else if (IsTimingFunctionKeyword(k.Name))
                    {
                        timing ??= v;
                    }
                    else if (k.Name is "normal" or "reverse" or "alternate" or "alternate-reverse")
                    {
                        direction ??= v;
                    }
                    else if (k.Name is "forwards" or "backwards" or "both")
                    {
                        fill ??= v;
                    }
                    else if (k.Name is "none")
                    {
                        // Ambiguous: name "none" or fill-mode "none". Per spec
                        // §4.1 the first encountered keyword that fits an
                        // un-set slot wins; prefer fill-mode if not yet set,
                        // else name.
                        if (fill is null)
                        {
                            fill = v;
                        }
                        else
                        {
                            name ??= k.Name;
                        }
                    }
                    else if (k.Name is "running" or "paused")
                    {
                        playState ??= v;
                    }
                    else
                    {
                        name ??= k.Name;
                    }
                }
                else if (v is CssFunctionValue f && IsTimingFunctionName(f.Name))
                {
                    timing ??= v;
                }
            }

            names.Add(new CssKeyword(name ?? "none"));
            durations.Add(duration ?? new CssTime(0, CssTimeUnit.Seconds));
            delays.Add(delay ?? new CssTime(0, CssTimeUnit.Seconds));
            timings.Add(timing ?? new CssKeyword("ease"));
            iterations.Add(iteration ?? new CssNumber(1));
            directions.Add(direction ?? new CssKeyword("normal"));
            fills.Add(fill ?? new CssKeyword("none"));
            playStates.Add(playState ?? new CssKeyword("running"));
        }

        if (names.Count == 0)
        {
            yield break;
        }

        yield return Emit(PropertyId.AnimationName, names, important);
        yield return Emit(PropertyId.AnimationDuration, durations, important);
        yield return Emit(PropertyId.AnimationDelay, delays, important);
        yield return Emit(PropertyId.AnimationTimingFunction, timings, important);
        yield return Emit(PropertyId.AnimationIterationCount, iterations, important);
        yield return Emit(PropertyId.AnimationDirection, directions, important);
        yield return Emit(PropertyId.AnimationFillMode, fills, important);
        yield return Emit(PropertyId.AnimationPlayState, playStates, important);

        static PropertyDeclaration Emit(PropertyId id, List<CssValue> layers, bool important)
            => new(id, layers.Count == 1 ? layers[0] : new CssValueList(layers), important);
    }

    private static List<List<CssValue>> SplitTopLevelCommas(List<CssValue> values)
    {
        var layers = new List<List<CssValue>>();
        var current = new List<CssValue>();
        foreach (var v in values)
        {
            if (v is CssKeyword { Name: "" })
            {
                layers.Add(current);
                current = new List<CssValue>();
            }
            else
            {
                current.Add(v);
            }
        }
        layers.Add(current);
        // Drop fully empty trailing layers (e.g. trailing comma with no
        // values after it) but keep empty intermediates so the layer index
        // matches author intent.
        if (layers.Count > 0 && layers[^1].Count == 0)
        {
            layers.RemoveAt(layers.Count - 1);
        }

        return layers;
    }

    private static bool IsTimingFunctionKeyword(string name)
        => name is "linear" or "ease" or "ease-in" or "ease-out" or "ease-in-out" or "step-start" or "step-end";

    private static bool IsTimingFunctionName(string name)
        => name is "cubic-bezier" or "steps" or "linear";

    /// <summary>
    /// Map from shorthand property name to the set of longhand properties it
    /// resets. Used to seed pending-substitution values when a shorthand
    /// contains a <c>var()</c> reference (CSS Variables L1 §3.7). Per spec the
    /// shorthand resets every mapped longhand; longhands that the resolved
    /// shorthand doesn't explicitly populate fall back to their initial value
    /// at computed time.
    /// </summary>
    private static readonly Dictionary<string, PropertyId[]> ShorthandLonghands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["margin"] = [PropertyId.MarginTop, PropertyId.MarginRight, PropertyId.MarginBottom, PropertyId.MarginLeft],
        ["padding"] = [PropertyId.PaddingTop, PropertyId.PaddingRight, PropertyId.PaddingBottom, PropertyId.PaddingLeft],
        ["border-width"] = [PropertyId.BorderTopWidth, PropertyId.BorderRightWidth, PropertyId.BorderBottomWidth, PropertyId.BorderLeftWidth],
        ["border-style"] = [PropertyId.BorderTopStyle, PropertyId.BorderRightStyle, PropertyId.BorderBottomStyle, PropertyId.BorderLeftStyle],
        ["border-color"] = [PropertyId.BorderTopColor, PropertyId.BorderRightColor, PropertyId.BorderBottomColor, PropertyId.BorderLeftColor],
        ["border-radius"] = [PropertyId.BorderTopLeftRadius, PropertyId.BorderTopRightRadius, PropertyId.BorderBottomRightRadius, PropertyId.BorderBottomLeftRadius],
        ["overflow"] = [PropertyId.OverflowX, PropertyId.OverflowY],
        ["background"] =
        [
            PropertyId.BackgroundColor, PropertyId.BackgroundImage, PropertyId.BackgroundPosition,
            PropertyId.BackgroundSize, PropertyId.BackgroundRepeat, PropertyId.BackgroundAttachment,
            PropertyId.BackgroundOrigin, PropertyId.BackgroundClip,
        ],
        ["border"] =
        [
            PropertyId.BorderTopWidth, PropertyId.BorderRightWidth, PropertyId.BorderBottomWidth, PropertyId.BorderLeftWidth,
            PropertyId.BorderTopStyle, PropertyId.BorderRightStyle, PropertyId.BorderBottomStyle, PropertyId.BorderLeftStyle,
            PropertyId.BorderTopColor, PropertyId.BorderRightColor, PropertyId.BorderBottomColor, PropertyId.BorderLeftColor,
        ],
        ["flex"] = [PropertyId.FlexGrow, PropertyId.FlexShrink, PropertyId.FlexBasis],
        ["flex-flow"] = [PropertyId.FlexDirection, PropertyId.FlexWrap],
        ["gap"] = [PropertyId.RowGap, PropertyId.ColumnGap],
        ["grid-gap"] = [PropertyId.RowGap, PropertyId.ColumnGap],
        ["grid-row-gap"] = [PropertyId.RowGap],
        ["grid-column-gap"] = [PropertyId.ColumnGap],
        ["grid-template"] = [PropertyId.GridTemplateRows, PropertyId.GridTemplateColumns, PropertyId.GridTemplateAreas],
        ["grid"] = [PropertyId.GridTemplateRows, PropertyId.GridTemplateColumns, PropertyId.GridTemplateAreas],
        ["grid-column"] = [PropertyId.GridColumnStart, PropertyId.GridColumnEnd],
        ["grid-row"] = [PropertyId.GridRowStart, PropertyId.GridRowEnd],
        ["grid-area"] = [PropertyId.GridRowStart, PropertyId.GridColumnStart, PropertyId.GridRowEnd, PropertyId.GridColumnEnd],
        ["place-items"] = [PropertyId.AlignItems, PropertyId.JustifyItems],
        ["place-content"] = [PropertyId.AlignContent, PropertyId.JustifyContent],
        ["place-self"] = [PropertyId.AlignSelf, PropertyId.JustifySelf],
        ["margin-inline"] = [PropertyId.MarginInlineStart, PropertyId.MarginInlineEnd],
        ["margin-block"] = [PropertyId.MarginBlockStart, PropertyId.MarginBlockEnd],
        ["padding-inline"] = [PropertyId.PaddingInlineStart, PropertyId.PaddingInlineEnd],
        ["padding-block"] = [PropertyId.PaddingBlockStart, PropertyId.PaddingBlockEnd],
        ["inset"] = [PropertyId.Top, PropertyId.Right, PropertyId.Bottom, PropertyId.Left],
        ["inset-inline"] = [PropertyId.InsetInlineStart, PropertyId.InsetInlineEnd],
        ["inset-block"] = [PropertyId.InsetBlockStart, PropertyId.InsetBlockEnd],
        ["border-inline-width"] = [PropertyId.BorderInlineStartWidth, PropertyId.BorderInlineEndWidth],
        ["border-inline-style"] = [PropertyId.BorderInlineStartStyle, PropertyId.BorderInlineEndStyle],
        ["border-inline-color"] = [PropertyId.BorderInlineStartColor, PropertyId.BorderInlineEndColor],
        ["border-block-width"] = [PropertyId.BorderBlockStartWidth, PropertyId.BorderBlockEndWidth],
        ["border-block-style"] = [PropertyId.BorderBlockStartStyle, PropertyId.BorderBlockEndStyle],
        ["border-block-color"] = [PropertyId.BorderBlockStartColor, PropertyId.BorderBlockEndColor],
        ["border-inline-start"] = [PropertyId.BorderInlineStartWidth, PropertyId.BorderInlineStartStyle, PropertyId.BorderInlineStartColor],
        ["border-inline-end"] = [PropertyId.BorderInlineEndWidth, PropertyId.BorderInlineEndStyle, PropertyId.BorderInlineEndColor],
        ["border-block-start"] = [PropertyId.BorderBlockStartWidth, PropertyId.BorderBlockStartStyle, PropertyId.BorderBlockStartColor],
        ["border-block-end"] = [PropertyId.BorderBlockEndWidth, PropertyId.BorderBlockEndStyle, PropertyId.BorderBlockEndColor],
        ["border-inline"] =
        [
            PropertyId.BorderInlineStartWidth, PropertyId.BorderInlineStartStyle, PropertyId.BorderInlineStartColor,
            PropertyId.BorderInlineEndWidth, PropertyId.BorderInlineEndStyle, PropertyId.BorderInlineEndColor,
        ],
        ["border-block"] =
        [
            PropertyId.BorderBlockStartWidth, PropertyId.BorderBlockStartStyle, PropertyId.BorderBlockStartColor,
            PropertyId.BorderBlockEndWidth, PropertyId.BorderBlockEndStyle, PropertyId.BorderBlockEndColor,
        ],
        ["border-top"] = [PropertyId.BorderTopWidth, PropertyId.BorderTopStyle, PropertyId.BorderTopColor],
        ["border-right"] = [PropertyId.BorderRightWidth, PropertyId.BorderRightStyle, PropertyId.BorderRightColor],
        ["border-bottom"] = [PropertyId.BorderBottomWidth, PropertyId.BorderBottomStyle, PropertyId.BorderBottomColor],
        ["border-left"] = [PropertyId.BorderLeftWidth, PropertyId.BorderLeftStyle, PropertyId.BorderLeftColor],
        ["scroll-margin"] = [PropertyId.ScrollMarginTop, PropertyId.ScrollMarginRight, PropertyId.ScrollMarginBottom, PropertyId.ScrollMarginLeft],
        ["scroll-padding"] = [PropertyId.ScrollPaddingTop, PropertyId.ScrollPaddingRight, PropertyId.ScrollPaddingBottom, PropertyId.ScrollPaddingLeft],
        ["overscroll-behavior"] = [PropertyId.OverscrollBehaviorX, PropertyId.OverscrollBehaviorY],
        ["outline"] = [PropertyId.OutlineColor, PropertyId.OutlineStyle, PropertyId.OutlineWidth],
        ["mask"] = [PropertyId.MaskImage, PropertyId.MaskMode, PropertyId.MaskPosition, PropertyId.MaskSize, PropertyId.MaskRepeat, PropertyId.MaskOrigin, PropertyId.MaskClip, PropertyId.MaskComposite],
        // CSS Masking 1: -webkit-mask is an alias for mask.
        ["-webkit-mask"] = [PropertyId.MaskImage, PropertyId.MaskMode, PropertyId.MaskPosition, PropertyId.MaskSize, PropertyId.MaskRepeat, PropertyId.MaskOrigin, PropertyId.MaskClip, PropertyId.MaskComposite],
        ["font"] = [PropertyId.FontStyle, PropertyId.FontWeight, PropertyId.FontStretch, PropertyId.FontSize, PropertyId.LineHeight, PropertyId.FontFamily],
        ["scroll-timeline"] = [PropertyId.ScrollTimelineName, PropertyId.ScrollTimelineAxis],
        ["view-timeline"] = [PropertyId.ViewTimelineName, PropertyId.ViewTimelineAxis],
        ["columns"] = [PropertyId.ColumnWidth, PropertyId.ColumnCount],
        ["column-rule"] = [PropertyId.ColumnRuleColor, PropertyId.ColumnRuleStyle, PropertyId.ColumnRuleWidth],
        ["text-decoration"] = [PropertyId.TextDecorationLine, PropertyId.TextDecorationStyle, PropertyId.TextDecorationColor, PropertyId.TextDecorationThickness],
        ["list-style"] = [PropertyId.ListStyleType, PropertyId.ListStylePosition, PropertyId.ListStyleImage],
        ["transition"] = [PropertyId.TransitionProperty, PropertyId.TransitionDuration, PropertyId.TransitionTimingFunction, PropertyId.TransitionDelay],
        ["animation"] =
        [
            PropertyId.AnimationName, PropertyId.AnimationDuration, PropertyId.AnimationTimingFunction,
            PropertyId.AnimationDelay, PropertyId.AnimationIterationCount, PropertyId.AnimationDirection,
            PropertyId.AnimationFillMode, PropertyId.AnimationPlayState,
        ],
    };

    private static bool ContainsVarReference(CssValue value)
        => value switch
        {
            CssVarReference => true,
            CssValueList list => list.Values.Any(ContainsVarReference),
            CssFunctionValue fn => fn.Arguments.Any(ContainsVarReference),
            _ => false,
        };

    private static bool IsColorLike(CssValue value)
        => value is CssColor or CssKeyword { Name: "currentColor" or "transparent" } or CssFunctionValue { Name: "rgb" or "rgba" or "hsl" or "hsla" or "hwb" or "lab" or "lch" or "oklab" or "oklch" or "color" };

    private static bool IsBorderStyle(CssValue value)
        => value is CssKeyword { Name: "none" or "hidden" or "dotted" or "dashed" or "solid" or "double" or "groove" or "ridge" or "inset" or "outset" };

    private static string ToCssName(PropertyId id)
    {
        var name = id.ToString();
        var chars = new List<char>(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
            {
                chars.Add('-');
            }

            chars.Add(char.ToLowerInvariant(name[i]));
        }

        return new string(chars.ToArray());
    }
}

public sealed record PropertyDeclaration(PropertyId Id, CssValue Value, bool Important);
