using System.Runtime.CompilerServices;
using Starling.Css.Properties;
using Starling.Css.Values;

namespace Starling.Css.Animations;

/// <summary>
/// Per-property value interpolation. CSS Transitions / Animations sample
/// the from/to values at progress <c>p ∈ [0, 1]</c> per CSS Values 4 §5.
/// The interpolator dispatches on value type because the rules differ:
/// numeric and length values lerp directly, colors lerp in sRGB component
/// space, and transform lists either lerp pairwise (matching function
/// signatures) or decompose to matrices and lerp those (mismatched lists,
/// per CSS Transforms 1 §10.2).
/// </summary>
public static class Interpolator
{
    /// <summary>
    /// Returns the interpolated value of <paramref name="from"/> /
    /// <paramref name="to"/> at progress <paramref name="progress"/>. If
    /// the pair is not interpolable per the spec (mismatched value kinds,
    /// keyword endpoints other than identical), the function falls back to
    /// the discrete rule: return <paramref name="from"/> while
    /// <c>progress &lt; 0.5</c>, otherwise <paramref name="to"/>
    /// (CSS Transitions 1 §3.1).
    /// </summary>
    public static CssValue Interpolate(PropertyId property, CssValue from, CssValue to, double progress)
    {
        if (progress <= 0)
        {
            return from;
        }

        if (progress >= 1)
        {
            return to;
        }

        if (ReferenceEquals(from, to))
        {
            return from;
        }

        // `transform` is stored in the cascade as a function value / value list
        // (e.g. rotate(0deg)), not a CssTransform, so the type switch below would
        // miss it and the value would snap at 50%. Parse both endpoints to
        // CssTransform up front (Parse is idempotent + maps `none` to the empty
        // transform) so a rotate/translate/scale animation actually tweens.
        if (property == PropertyId.Transform)
        {
            var fromTransform = from as CssTransform ?? CssTransformParser.Parse(from);
            var toTransform = to as CssTransform ?? CssTransformParser.Parse(to);
            return InterpolateTransform(fromTransform, toTransform, progress);
        }

        // Shadow lists and gradient images are stored in the cascade as raw
        // value trees (CssValueList / CssFunctionValue), so the type switch
        // below would never pair them up. Dispatch on the property id and
        // parse to the typed form once per endpoint (cached — the transition
        // engines hold stable endpoint references and sample per frame).
        if (property == PropertyId.BoxShadow)
        {
            return InterpolateBoxShadowValue(from, to, progress);
        }

        if (property == PropertyId.TextShadow && from is CssTextShadow fromText && to is CssTextShadow toText)
        {
            return InterpolateTextShadow(fromText, toText, progress);
        }

        if (property == PropertyId.BackgroundImage)
        {
            return InterpolateBackgroundImage(from, to, progress);
        }

        if (from.GetType() != to.GetType())
        {
            return Discrete(from, to, progress);
        }

        switch (from)
        {
            case CssNumber f when to is CssNumber t:
                return new CssNumber(Lerp(f.Value, t.Value, progress));

            case CssPercentage f when to is CssPercentage t:
                return new CssPercentage(Lerp(f.Value, t.Value, progress));

            case CssLength f when to is CssLength t:
                // Same-unit fast path keeps the result in the source unit so
                // subsequent computed-value passes don't need a second
                // conversion. Different units fall through to the px
                // common-denominator branch handled below.
                if (f.Unit == t.Unit)
                {
                    return new CssLength(Lerp(f.Value, t.Value, progress), f.Unit);
                }

                if (TryToPx(f, out var fpx) && TryToPx(t, out var tpx))
                {
                    return new CssLength(Lerp(fpx, tpx, progress), CssLengthUnit.Px);
                }

                return Discrete(from, to, progress);

            case CssTime f when to is CssTime t:
                return new CssTime(Lerp(f.InSeconds, t.InSeconds, progress), CssTimeUnit.Seconds);

            case CssAngle f when to is CssAngle t:
                // Interpolate in degrees so different angle units (turn, rad)
                // can mix; convert the result back into degrees as a stable
                // canonical unit.
                return new CssAngle(Lerp(f.InDegrees, t.InDegrees, progress), CssAngleUnit.Degrees);

            case CssColor f when to is CssColor t:
                return InterpolateColor(f, t, progress);

            case CssTransform f when to is CssTransform t:
                return InterpolateTransform(f, t, progress);
        }

        return Discrete(from, to, progress);
    }

    /// <summary>
    /// Returns true when <paramref name="property"/> is animatable per the
    /// CSS property registry. The shortlist mirrors the properties we
    /// currently interpolate; anything else falls back to discrete switch.
    /// </summary>
    public static bool IsAnimatable(PropertyId property) => property switch
    {
        PropertyId.Opacity => true,
        PropertyId.Color => true,
        PropertyId.BackgroundColor => true,
        PropertyId.BorderTopColor or PropertyId.BorderRightColor
            or PropertyId.BorderBottomColor or PropertyId.BorderLeftColor => true,
        PropertyId.Width or PropertyId.Height => true,
        PropertyId.Top or PropertyId.Right or PropertyId.Bottom or PropertyId.Left => true,
        PropertyId.MarginTop or PropertyId.MarginRight or PropertyId.MarginBottom or PropertyId.MarginLeft => true,
        PropertyId.PaddingTop or PropertyId.PaddingRight or PropertyId.PaddingBottom or PropertyId.PaddingLeft => true,
        PropertyId.BorderTopWidth or PropertyId.BorderRightWidth
            or PropertyId.BorderBottomWidth or PropertyId.BorderLeftWidth => true,
        PropertyId.FontSize or PropertyId.LineHeight or PropertyId.LetterSpacing or PropertyId.WordSpacing => true,
        PropertyId.Transform => true,
        PropertyId.BoxShadow or PropertyId.TextShadow => true,
        PropertyId.BackgroundImage => true,
        _ => false,
    };

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;

    private static CssValue Discrete(CssValue from, CssValue to, double progress)
        => progress < 0.5 ? from : to;

    private static CssColor InterpolateColor(CssColor a, CssColor b, double p)
    {
        // Spec (CSS Color 4 §12): interpolation happens in the colour space
        // specified by `color-interpolation-method`, defaulting to sRGB.
        // Component lerp on premultiplied alpha avoids the bleed-through
        // artefact when fading to transparent.
        var aa = a.A / 255.0;
        var ba = b.A / 255.0;
        var ar = a.R * aa;
        var ag = a.G * aa;
        var ab = a.B * aa;
        var br = b.R * ba;
        var bg = b.G * ba;
        var bb = b.B * ba;
        var oa = Lerp(aa, ba, p);
        var or = Lerp(ar, br, p);
        var og = Lerp(ag, bg, p);
        var ob = Lerp(ab, bb, p);
        var outA = (byte)Math.Clamp(Math.Round(oa * 255), 0, 255);
        var outR = oa < 1e-6 ? (byte)0 : (byte)Math.Clamp(Math.Round(or / oa), 0, 255);
        var outG = oa < 1e-6 ? (byte)0 : (byte)Math.Clamp(Math.Round(og / oa), 0, 255);
        var outB = oa < 1e-6 ? (byte)0 : (byte)Math.Clamp(Math.Round(ob / oa), 0, 255);
        return new CssColor(outR, outG, outB, outA);
    }

    private static CssTransform InterpolateTransform(CssTransform a, CssTransform b, double p)
    {
        // Fast path — matching function signatures lerp pairwise per
        // CSS Transforms 1 §10.1. Bail to the matrix-decompose slow path
        // when counts or function types differ.
        var fa = a.Functions;
        var fb = b.Functions;
        if (fa.Count == fb.Count && CanLerpPairwise(fa, fb))
        {
            var result = new List<CssTransformFunction>(fa.Count);
            for (var i = 0; i < fa.Count; i++)
            {
                result.Add(LerpFunction(fa[i], fb[i], p));
            }

            return new CssTransform(result);
        }

        // Matrix decompose-and-lerp: compose the function lists into
        // Matrix2D and lerp each component independently. This is the
        // simplified form of CSS Transforms 1 §10.2 — full decompose into
        // translate/scale/rotate/skew components is a richer follow-up
        // (it produces nicer mid-frames for combined rotate+scale), but
        // matrix-component lerp matches Firefox's pre-2015 behaviour and is
        // visually acceptable for cross-function transitions.
        var ma = a.ToMatrix(0, 0);
        var mb = b.ToMatrix(0, 0);
        var lerped = new Matrix2D(
            Lerp(ma.A, mb.A, p),
            Lerp(ma.B, mb.B, p),
            Lerp(ma.C, mb.C, p),
            Lerp(ma.D, mb.D, p),
            Lerp(ma.E, mb.E, p),
            Lerp(ma.F, mb.F, p));
        return new CssTransform(new[] { (CssTransformFunction)new CssMatrix(lerped) });
    }

    private static bool CanLerpPairwise(IReadOnlyList<CssTransformFunction> a, IReadOnlyList<CssTransformFunction> b)
    {
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].GetType() != b[i].GetType())
            {
                return false;
            }
        }

        return true;
    }

    private static CssTransformFunction LerpFunction(CssTransformFunction a, CssTransformFunction b, double p)
    {
        switch (a)
        {
            case CssTranslate ta when b is CssTranslate tb:
                return new CssTranslate(
                    LerpLengthOrPct(ta.X, tb.X, p),
                    LerpLengthOrPct(ta.Y, tb.Y, p));
            case CssScale sa when b is CssScale sb:
                return new CssScale(Lerp(sa.X, sb.X, p), Lerp(sa.Y, sb.Y, p));
            case CssRotate ra when b is CssRotate rb:
                return new CssRotate(Lerp(ra.AngleRadians, rb.AngleRadians, p));
            case CssSkew ka when b is CssSkew kb:
                return new CssSkew(Lerp(ka.XRadians, kb.XRadians, p), Lerp(ka.YRadians, kb.YRadians, p));
            case CssMatrix ma when b is CssMatrix mb:
                return new CssMatrix(new Matrix2D(
                    Lerp(ma.Value.A, mb.Value.A, p),
                    Lerp(ma.Value.B, mb.Value.B, p),
                    Lerp(ma.Value.C, mb.Value.C, p),
                    Lerp(ma.Value.D, mb.Value.D, p),
                    Lerp(ma.Value.E, mb.Value.E, p),
                    Lerp(ma.Value.F, mb.Value.F, p)));
            default:
                // CanLerpPairwise should have ruled this out, but stay safe.
                return p < 0.5 ? a : b;
        }
    }

    private static CssLengthOrPercent LerpLengthOrPct(CssLengthOrPercent a, CssLengthOrPercent b, double p)
    {
        // Mixed length-vs-percent translates can't be lerped at compute time
        // without the resolution context (we'd need the reference size to
        // resolve the percent to px). Fall back to discrete switch — matches
        // the behaviour you'd get from a calc() that the engine can't yet
        // evaluate at sample time.
        if (a.IsPercent != b.IsPercent)
        {
            return p < 0.5 ? a : b;
        }

        return new CssLengthOrPercent(Lerp(a.Value, b.Value, p), a.IsPercent);
    }

    // ---- box-shadow / text-shadow (CSS Backgrounds 3 §6) -------------------

    // Parse-once cache for raw box-shadow endpoints. Transition/animation
    // endpoints are stable object references for the life of a transition, so
    // keying on the instance gives one parse per endpoint instead of one per
    // sampled frame; entries die with the endpoint values.
    private static readonly ConditionalWeakTable<CssValue, CssBoxShadow> ShadowCache = new();

    private static CssValue InterpolateBoxShadowValue(CssValue from, CssValue to, double p)
    {
        var a = from as CssBoxShadow ?? ShadowCache.GetValue(from, static v => CssBoxShadowParser.Parse(v));
        var b = to as CssBoxShadow ?? ShadowCache.GetValue(to, static v => CssBoxShadowParser.Parse(v));

        var count = Math.Max(a.Layers.Count, b.Layers.Count);
        if (count == 0)
        {
            return from; // none → none
        }

        var layers = new CssShadow[count];
        for (var i = 0; i < count; i++)
        {
            var la = i < a.Layers.Count ? a.Layers[i] : null;
            var lb = i < b.Layers.Count ? b.Layers[i] : null;
            // Shorter list pads with the neutral shadow: all-zero lengths,
            // transparent color, inset matching the paired layer (CSS
            // Backgrounds 3 §6 / web-animations-1 shadow list interpolation).
            la ??= NeutralShadow(lb!);
            lb ??= NeutralShadow(la);
            // A pair that disagrees on inset is not interpolable — the whole
            // property falls back to discrete.
            if (la.Inset != lb.Inset)
            {
                return Discrete(from, to, p);
            }

            if (!TryLerpShadow(la, lb, p, out layers[i]!))
            {
                return Discrete(from, to, p);
            }
        }

        return new CssBoxShadow(layers);
    }

    private static CssShadow NeutralShadow(CssShadow paired)
        => new(CssLength.Zero, CssLength.Zero, CssLength.Zero, CssLength.Zero,
            new CssColor(0, 0, 0, 0), paired.Inset);

    private static bool TryLerpShadow(CssShadow a, CssShadow b, double p, out CssShadow? result)
    {
        result = null;
        if (!TryLerpLength(a.OffsetX, b.OffsetX, p, out var ox))
        {
            return false;
        }

        if (!TryLerpLength(a.OffsetY, b.OffsetY, p, out var oy))
        {
            return false;
        }

        if (!TryLerpLength(a.Blur, b.Blur, p, out var blur))
        {
            return false;
        }

        if (!TryLerpLength(a.Spread, b.Spread, p, out var spread))
        {
            return false;
        }
        // Blur radius is non-negative per spec; the endpoints already are, but
        // clamp anyway so an overshooting easing can never paint a negative.
        if (blur!.Value < 0)
        {
            blur = new CssLength(0, blur.Unit);
        }

        CssColor? color;
        if (a.Color is null && b.Color is null)
        {
            color = null; // currentColor at both ends — keep the sentinel
        }
        else if (a.Color is null || b.Color is null)
        {
            // currentColor vs a concrete color can't be resolved at compute
            // time (we don't know the element's `color` here) — discrete.
            return false;
        }
        else
        {
            color = InterpolateColor(a.Color, b.Color, p);
        }

        result = new CssShadow(ox!, oy!, blur, spread!, color, a.Inset);
        return true;
    }

    private static bool TryLerpLength(CssLength a, CssLength b, double p, out CssLength? result)
    {
        if (a.Unit == b.Unit)
        {
            result = new CssLength(Lerp(a.Value, b.Value, p), a.Unit);
            return true;
        }
        if (TryToPx(a, out var apx) && TryToPx(b, out var bpx))
        {
            result = new CssLength(Lerp(apx, bpx, p), CssLengthUnit.Px);
            return true;
        }
        result = null;
        return false;
    }

    // text-shadow is stored typed (CssTextShadow, px-resolved doubles), so no
    // parse cache is needed; pad and lerp per layer like box-shadow but with
    // no inset/spread components.
    private static readonly CssTextShadowLayer NeutralTextShadowLayer = new(0, 0, 0, new CssColor(0, 0, 0, 0));

    private static CssValue InterpolateTextShadow(CssTextShadow a, CssTextShadow b, double p)
    {
        var count = Math.Max(a.Layers.Count, b.Layers.Count);
        if (count == 0)
        {
            return a;
        }

        var layers = new CssTextShadowLayer[count];
        for (var i = 0; i < count; i++)
        {
            var la = i < a.Layers.Count ? a.Layers[i] : NeutralTextShadowLayer;
            var lb = i < b.Layers.Count ? b.Layers[i] : NeutralTextShadowLayer;

            CssColor? color;
            if (la.Color is null && lb.Color is null)
            {
                color = null;
            }
            else if (la.Color is null || lb.Color is null)
            {
                return Discrete(a, b, p);
            }
            else
            {
                color = InterpolateColor(la.Color, lb.Color, p);
            }

            layers[i] = new CssTextShadowLayer(
                Lerp(la.OffsetX, lb.OffsetX, p),
                Lerp(la.OffsetY, lb.OffsetY, p),
                Math.Max(0, Lerp(la.Blur, lb.Blur, p)),
                color);
        }

        return new CssTextShadow(layers);
    }

    // ---- background-image gradients (CSS Images 3 §3.4.1) ------------------

    /// <summary>Typed view of a background-image endpoint: one entry per
    /// layer, null when the layer is not a gradient (url(), none, image-set).</summary>
    private sealed class ParsedImageLayers
    {
        public ParsedImageLayers(CssGradient?[] layers) => Layers = layers;

        public readonly CssGradient?[] Layers;
    }

    private static readonly ConditionalWeakTable<CssValue, ParsedImageLayers> ImageLayersCache = new();

    private static CssValue InterpolateBackgroundImage(CssValue from, CssValue to, double p)
    {
        // Fast path: both endpoints already typed (single-gradient transitions
        // land here every sampled frame) — lerp directly, no per-sample
        // wrapper allocation.
        if (from is CssGradient fg && to is CssGradient tg)
        {
            return TryLerpGradient(fg, tg, p, out var direct) ? direct! : Discrete(from, to, p);
        }

        var a = ImageLayersCache.GetValue(from, static v => ParseImageLayers(v));
        var b = ImageLayersCache.GetValue(to, static v => ParseImageLayers(v));

        var n = a.Layers.Length;
        if (n == 0 || n != b.Layers.Length)
        {
            return Discrete(from, to, p);
        }

        // Single layer stays a bare CssGradient; multi-layer becomes a clean
        // CssValueList of gradients (the same shape the `background` shorthand
        // produces, which the paint layer iterates per layer).
        if (n == 1)
        {
            var ga = a.Layers[0];
            var gb = b.Layers[0];
            if (ga is null || gb is null || !TryLerpGradient(ga, gb, p, out var lerped))
            {
                return Discrete(from, to, p);
            }

            return lerped!;
        }

        var values = new CssValue[n];
        for (var i = 0; i < n; i++)
        {
            var ga = a.Layers[i];
            var gb = b.Layers[i];
            if (ga is null || gb is null || !TryLerpGradient(ga, gb, p, out var lerped))
            {
                return Discrete(from, to, p);
            }

            values[i] = lerped!;
        }
        return new CssValueList(values);
    }

    private static ParsedImageLayers ParseImageLayers(CssValue value)
    {
        if (value is CssGradient g)
        {
            return new ParsedImageLayers([g]);
        }

        if (value is CssValueList list)
        {
            var layers = new List<CssGradient?>(list.Values.Count);
            foreach (var item in list.Values)
            {
                // Longhand lists keep top-level commas as empty/"," keywords
                // (see CssBoxShadowParser.IsCommaSeparator); shorthand-built
                // lists carry one value per layer with no separators.
                if (item is CssKeyword { Name: "" or "," })
                {
                    continue;
                }

                layers.Add(ParseImageLayer(item));
            }
            return new ParsedImageLayers(layers.ToArray());
        }
        return new ParsedImageLayers([ParseImageLayer(value)]);
    }

    private static CssGradient? ParseImageLayer(CssValue value)
        => value switch
        {
            CssGradient g => g,
            CssFunctionValue fn when CssGradientParser.TryParseFunction(fn, out var g) => g,
            _ => null,
        };

    private static bool TryLerpGradient(CssGradient a, CssGradient b, double p, out CssGradient? result)
    {
        result = null;
        // Interpolable only between the same gradient shape: same kind, same
        // repeating flag, same stop count (CSS Images 3 §3.4.1). Radial
        // keyword shape/size and the color-interpolation prelude must match —
        // none of those lerp without layout context.
        if (a.Kind != b.Kind || a.Repeating != b.Repeating)
        {
            return false;
        }

        if (a.Stops.Count != b.Stops.Count)
        {
            return false;
        }

        if (a.Kind == CssGradientKind.Radial && (a.Shape != b.Shape || a.Size != b.Size))
        {
            return false;
        }

        if (!Equals(a.Interpolation, b.Interpolation))
        {
            return false;
        }

        if (!TryLerpGradientLine(a, b, p, out var line))
        {
            return false;
        }

        // `at <position>` center (radial/conic): null means center, so both
        // endpoints always resolve; keep null when both ends used the default.
        CssGradientPosition? position;
        if (a.Position is null && b.Position is null)
        {
            position = null;
        }
        else
        {
            var pa = a.Position ?? CssGradientPosition.Center;
            var pb = b.Position ?? CssGradientPosition.Center;
            position = new CssGradientPosition(
                Lerp(pa.FractionX, pb.FractionX, p),
                Lerp(pa.FractionY, pb.FractionY, p));
        }

        var stops = new CssColorStop[a.Stops.Count];
        for (var i = 0; i < stops.Length; i++)
        {
            var sa = a.Stops[i];
            var sb = b.Stops[i];
            if (sa.IsHint != sb.IsHint)
            {
                return false; // hint must pair with hint
            }

            CssGradientStopPosition? pos;
            if (sa.Position is null && sb.Position is null)
            {
                pos = null; // both auto-distributed — stays auto
            }
            else if (sa.Position is { } qa && sb.Position is { } qb && qa.IsPercent == qb.IsPercent)
            {
                pos = new CssGradientStopPosition(Lerp(qa.Value, qb.Value, p), qa.IsPercent);
            }
            else
            {
                // auto vs fixed, or % vs px — needs the gradient line length
                // to resolve, which we don't have at compute time.
                return false;
            }

            // A hint's color is a sentinel (transparent black) — carry it
            // through unchanged instead of lerping the sentinel.
            var color = sa.IsHint ? sa.Color : InterpolateColor(sa.Color, sb.Color, p);
            stops[i] = new CssColorStop(color, pos, sa.IsHint);
        }

        result = new CssGradient(a.Kind, a.Repeating, stops, line, a.Shape, a.Size, position, a.Interpolation);
        return true;
    }

    private static bool TryLerpGradientLine(CssGradient a, CssGradient b, double p, out CssGradientLine? line)
    {
        line = null;
        var la = a.Line;
        var lb = b.Line;
        if (Equals(la, lb))
        {
            line = la; // identical (or both default) — keep as-is
            return true;
        }

        // Differing lines interpolate only when both resolve to pure angles.
        // `to <side-or-corner>` needs the box aspect ratio to become an angle,
        // so any mismatch involving sides stays discrete.
        var defaultDeg = a.Kind == CssGradientKind.Linear ? 180.0 : 0.0; // linear default `to bottom`; conic default 0deg
        if (!TryLineAngle(la, defaultDeg, out var da) || !TryLineAngle(lb, defaultDeg, out var db))
        {
            return false;
        }

        line = CssGradientLine.FromAngle(Lerp(da, db, p));
        return true;
    }

    private static bool TryLineAngle(CssGradientLine? l, double defaultDeg, out double degrees)
    {
        if (l is null)
        {
            degrees = defaultDeg;
            return true;
        }
        if (l.AngleDegrees is { } d)
        {
            degrees = d;
            return true;
        }
        degrees = 0;
        return false;
    }

    private static bool TryToPx(CssLength l, out double px)
    {
        // Absolute units only. Anything font-relative or viewport-relative
        // needs the resolution context to land in px; without it the
        // interpolator surrenders and the caller switches to discrete.
        switch (l.Unit)
        {
            case CssLengthUnit.Px: px = l.Value; return true;
            case CssLengthUnit.Pt: px = l.Value * 4.0 / 3.0; return true;
            case CssLengthUnit.Pc: px = l.Value * 16.0; return true;
            case CssLengthUnit.In: px = l.Value * 96.0; return true;
            case CssLengthUnit.Cm: px = l.Value * 96.0 / 2.54; return true;
            case CssLengthUnit.Mm: px = l.Value * 96.0 / 25.4; return true;
            case CssLengthUnit.Q: px = l.Value * 96.0 / 25.4 / 4.0; return true;
            default: px = 0; return false;
        }
    }
}
