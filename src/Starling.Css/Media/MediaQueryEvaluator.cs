using System.Globalization;

namespace Starling.Css.Media;

// MQ5 §3-§17 evaluator. Pure function over (query, context).
public static class MediaQueryEvaluator
{
    public static bool Evaluate(MediaQueryList list, MediaContext ctx)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(ctx);
        if (list.Queries.Count == 0)
        {
            return true;
        }
        // Comma list = OR.
        foreach (var q in list.Queries)
        {
            if (Evaluate(q, ctx))
            {
                return true;
            }
        }

        return false;
    }

    public static bool Evaluate(MediaQuery query, MediaContext ctx)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(ctx);
        var typeMatch = MatchType(query.MediaType, ctx);
        var condMatch = query.Condition is null || EvaluateCondition(query.Condition, ctx);
        var raw = typeMatch && condMatch;
        return query.Modifier == MediaQueryModifier.Not ? !raw : raw;
    }

    private static bool MatchType(string? mediaType, MediaContext ctx)
    {
        if (mediaType is null or "all")
        {
            return true;
        }

        return mediaType.Equals(ctx.MediaType, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EvaluateCondition(MediaCondition cond, MediaContext ctx)
        => cond switch
        {
            MediaConditionFeature feat => EvaluateFeature(feat.Feature, ctx),
            MediaConditionNot not => !EvaluateCondition(not.Inner, ctx),
            MediaConditionAnd and => and.Parts.All(p => EvaluateCondition(p, ctx)),
            MediaConditionOr or => or.Parts.Any(p => EvaluateCondition(p, ctx)),
            _ => false,
        };

    private static bool EvaluateFeature(MediaFeature feat, MediaContext ctx)
        => feat switch
        {
            MediaFeatureBoolean b => EvaluateBoolean(b.Name, ctx),
            MediaFeaturePlain p => EvaluatePlain(p.Name, p.Value, ctx),
            MediaFeatureRange r => EvaluateRange(r, ctx),
            _ => false,
        };

    private static bool EvaluateBoolean(string name, MediaContext ctx)
        => name switch
        {
            "width" => ctx.ViewportWidthPx > 0,
            "height" => ctx.ViewportHeightPx > 0,
            "color" => ctx.Color > 0,
            "monochrome" => ctx.Monochrome > 0,
            "hover" => ctx.Hover != Hover.None,
            "any-hover" => ctx.AnyHover != Hover.None,
            "pointer" => ctx.Pointer != Pointer.None,
            "any-pointer" => ctx.AnyPointer != Pointer.None,
            "grid" => false, // we render to a bitmap surface
            "scripting" => ctx.Scripting != Scripting.None,
            "update" => ctx.Update != UpdateFrequency.None,
            "prefers-reduced-motion" => ctx.ReducedMotion == ReducedMotion.Reduce,
            "prefers-reduced-transparency" => ctx.ReducedTransparency == ReducedTransparency.Reduce,
            "prefers-reduced-data" => ctx.ReducedData == ReducedData.Reduce,
            "prefers-contrast" => ctx.Contrast != Contrast.NoPreference,
            "prefers-color-scheme" => true,
            "forced-colors" => ctx.ForcedColors == ForcedColors.Active,
            "inverted-colors" => ctx.InvertedColors == InvertedColors.Inverted,
            _ => false,
        };

    private static bool EvaluatePlain(string name, MediaFeatureValue? value, MediaContext ctx)
    {
        if (value is null)
        {
            return EvaluateBoolean(name, ctx);
        }

        // min-/max- prefixes are sugar for >= / <=.
        if (name.StartsWith("min-", StringComparison.OrdinalIgnoreCase))
        {
            return CompareNumeric(name[4..], value, ctx, RangeOp.GreaterOrEqual);
        }

        if (name.StartsWith("max-", StringComparison.OrdinalIgnoreCase))
        {
            return CompareNumeric(name[4..], value, ctx, RangeOp.LessOrEqual);
        }

        // discrete-keyword features
        if (TryEvaluateKeyword(name, value, ctx, out var kw))
        {
            return kw;
        }

        return CompareNumeric(name, value, ctx, RangeOp.Equal);
    }

    private static bool EvaluateRange(MediaFeatureRange r, MediaContext ctx)
    {
        // double form: `v1 op1 name op2 v2` — treat as (name op1-rev v1) AND (name op2 v2)
        var name = r.Name.ToLowerInvariant();
        if (r.Op1 != RangeOp.None && r.V1 is not null)
        {
            // For the LHS form, the comparison is `value op name`, equivalent to `name reverse(op) value`.
            // The parser stores the value in V1 with the operator as written. We re-flip when V2 is also present.
            var op = r.V2 is null ? r.Op1 : ReverseOp(r.Op1);
            if (!CompareNumeric(name, r.V1, ctx, op))
            {
                return false;
            }
        }
        if (r.Op2 != RangeOp.None && r.V2 is not null)
        {
            if (!CompareNumeric(name, r.V2, ctx, r.Op2))
            {
                return false;
            }
        }
        return true;
    }

    private static RangeOp ReverseOp(RangeOp op) => op switch
    {
        RangeOp.Less => RangeOp.Greater,
        RangeOp.LessOrEqual => RangeOp.GreaterOrEqual,
        RangeOp.Greater => RangeOp.Less,
        RangeOp.GreaterOrEqual => RangeOp.LessOrEqual,
        _ => op,
    };

    private static bool TryEvaluateKeyword(string name, MediaFeatureValue value, MediaContext ctx, out bool result)
    {
        result = false;
        if (value is not MediaFeatureIdent ident)
        {
            return false;
        }

        var k = ident.Name;
        switch (name)
        {
            case "orientation":
                result = (k == "portrait" && ctx.Orientation == Orientation.Portrait)
                       || (k == "landscape" && ctx.Orientation == Orientation.Landscape);
                return true;
            case "prefers-color-scheme":
                result = (k == "dark" && ctx.ColorScheme == ColorScheme.Dark)
                       || (k == "light" && ctx.ColorScheme == ColorScheme.Light);
                return true;
            case "prefers-reduced-motion":
                result = (k == "reduce" && ctx.ReducedMotion == ReducedMotion.Reduce)
                       || (k == "no-preference" && ctx.ReducedMotion == ReducedMotion.NoPreference);
                return true;
            case "prefers-contrast":
                result = k switch
                {
                    "more" => ctx.Contrast == Contrast.More,
                    "less" => ctx.Contrast == Contrast.Less,
                    "custom" => ctx.Contrast == Contrast.Custom,
                    "no-preference" => ctx.Contrast == Contrast.NoPreference,
                    _ => false,
                };
                return true;
            case "prefers-reduced-transparency":
                result = (k == "reduce" && ctx.ReducedTransparency == ReducedTransparency.Reduce)
                       || (k == "no-preference" && ctx.ReducedTransparency == ReducedTransparency.NoPreference);
                return true;
            case "prefers-reduced-data":
                result = (k == "reduce" && ctx.ReducedData == ReducedData.Reduce)
                       || (k == "no-preference" && ctx.ReducedData == ReducedData.NoPreference);
                return true;
            case "pointer":
                result = MatchPointer(ctx.Pointer, k);
                return true;
            case "any-pointer":
                result = MatchPointer(ctx.AnyPointer, k);
                return true;
            case "hover":
                result = MatchHover(ctx.Hover, k);
                return true;
            case "any-hover":
                result = MatchHover(ctx.AnyHover, k);
                return true;
            case "scripting":
                result = k switch
                {
                    "none" => ctx.Scripting == Scripting.None,
                    "initial-only" => ctx.Scripting == Scripting.InitialOnly,
                    "enabled" => ctx.Scripting == Scripting.Enabled,
                    _ => false,
                };
                return true;
            case "update":
                result = k switch
                {
                    "none" => ctx.Update == UpdateFrequency.None,
                    "slow" => ctx.Update == UpdateFrequency.Slow,
                    "fast" => ctx.Update == UpdateFrequency.Fast,
                    _ => false,
                };
                return true;
            case "color-gamut":
                result = k switch
                {
                    "srgb" => ctx.ColorGamut is ColorGamut.Srgb or ColorGamut.P3 or ColorGamut.Rec2020,
                    "p3" => ctx.ColorGamut is ColorGamut.P3 or ColorGamut.Rec2020,
                    "rec2020" => ctx.ColorGamut == ColorGamut.Rec2020,
                    _ => false,
                };
                return true;
            case "display-mode":
                result = k switch
                {
                    "browser" => ctx.DisplayMode == DisplayMode.Browser,
                    "standalone" => ctx.DisplayMode == DisplayMode.Standalone,
                    "minimal-ui" => ctx.DisplayMode == DisplayMode.MinimalUi,
                    "fullscreen" => ctx.DisplayMode == DisplayMode.Fullscreen,
                    "picture-in-picture" => ctx.DisplayMode == DisplayMode.PictureInPicture,
                    _ => false,
                };
                return true;
            case "forced-colors":
                result = (k == "active" && ctx.ForcedColors == ForcedColors.Active)
                       || (k == "none" && ctx.ForcedColors == ForcedColors.None);
                return true;
            case "inverted-colors":
                result = (k == "inverted" && ctx.InvertedColors == InvertedColors.Inverted)
                       || (k == "none" && ctx.InvertedColors == InvertedColors.None);
                return true;
        }
        return false;
    }

    private static bool MatchPointer(Pointer current, string keyword) => keyword switch
    {
        "none" => current == Pointer.None,
        "coarse" => current == Pointer.Coarse,
        "fine" => current == Pointer.Fine,
        _ => false,
    };

    private static bool MatchHover(Hover current, string keyword) => keyword switch
    {
        "none" => current == Hover.None,
        "hover" => current == Hover.Hover,
        _ => false,
    };

    private static bool CompareNumeric(string name, MediaFeatureValue value, MediaContext ctx, RangeOp op)
    {
        if (!TryGetNumericContext(name, ctx, out var contextValue))
        {
            // Fall back to keyword match if the value is an ident.
            if (TryEvaluateKeyword(name, value, ctx, out var kw))
            {
                return kw;
            }

            return false;
        }

        var rhs = ResolveValue(name, value);
        if (double.IsNaN(rhs))
        {
            return false;
        }

        return op switch
        {
            RangeOp.Less => contextValue < rhs,
            RangeOp.LessOrEqual => contextValue <= rhs,
            RangeOp.Greater => contextValue > rhs,
            RangeOp.GreaterOrEqual => contextValue >= rhs,
            RangeOp.Equal => ApproxEquals(contextValue, rhs),
            _ => false,
        };
    }

    private static bool ApproxEquals(double a, double b)
        => Math.Abs(a - b) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));

    private static bool TryGetNumericContext(string name, MediaContext ctx, out double value)
    {
        switch (name)
        {
            case "width": value = ctx.ViewportWidthPx; return true;
            case "height": value = ctx.ViewportHeightPx; return true;
            case "aspect-ratio": value = ctx.AspectRatio; return true;
            case "device-aspect-ratio": value = ctx.AspectRatio; return true;
            case "device-width": value = ctx.ViewportWidthPx; return true;
            case "device-height": value = ctx.ViewportHeightPx; return true;
            case "resolution": value = ctx.Resolution; return true;
            case "color": value = ctx.Color; return true;
            case "color-index": value = 0; return true;
            case "monochrome": value = ctx.Monochrome; return true;
            default: value = 0; return false;
        }
    }

    private static double ResolveValue(string featureName, MediaFeatureValue value)
        => value switch
        {
            MediaFeatureNumber n => n.Value,
            MediaFeatureRatio r => r.AsDouble,
            MediaFeatureDimension d => NormalizeDimension(featureName, d),
            MediaFeatureIdent => double.NaN,
            _ => double.NaN,
        };

    // Length features need px; resolution feature needs dppx.
    private static double NormalizeDimension(string feature, MediaFeatureDimension d)
    {
        if (feature == "resolution")
        {
            return d.Unit switch
            {
                "dppx" or "x" => d.Value,
                "dpi" => d.Value / 96d,
                "dpcm" => d.Value * 2.54d / 96d,
                _ => double.NaN,
            };
        }
        // length → px
        return d.Unit switch
        {
            "px" => d.Value,
            "pt" => d.Value * 4d / 3d,
            "pc" => d.Value * 16d,
            "in" => d.Value * 96d,
            "cm" => d.Value * 96d / 2.54d,
            "mm" => d.Value * 96d / 25.4d,
            "q" => d.Value * 96d / 101.6d,
            "em" or "rem" => d.Value * 16d,
            _ => double.TryParse(d.Value.ToString(CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN,
        };
    }
}
