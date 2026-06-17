using Starling.Css.Parser;
using Starling.Css.Tokenizer;

namespace Starling.Css.Values;

/// <summary>
/// Parses CSS Color 4/5 color functions: rgb()/rgba(), hsl()/hsla(), hwb(),
/// lab(), lch(), oklab(), oklch(), color(&lt;space&gt; ...), color-mix().
/// Accepts both legacy comma-separated and modern whitespace-separated syntaxes.
/// </summary>
public static class ColorParser
{
    public static bool TryParseFunction(string name, IReadOnlyList<CssComponentValue> raw, out CssColor color)
    {
        color = CssColor.Transparent;
        var lower = name.ToLowerInvariant();

        // CSS Color 5 §4: relative color syntax — `rgb(from <color> r g b)` etc.
        // When the first argument is the `from` keyword, resolve the origin color,
        // bind the target space's channel keywords, and rewrite the channel
        // expressions into ordinary numeric components before the regular parse.
        if (StartsWithFrom(raw, out var afterFrom) &&
            lower is "rgb" or "rgba" or "hsl" or "hsla" or "hwb"
                  or "lab" or "lch" or "oklab" or "oklch")
        {
            return TryParseRelative(lower, afterFrom, out color);
        }

        return lower switch
        {
            "rgb" or "rgba" => TryParseRgb(raw, out color),
            "hsl" or "hsla" => TryParseHsl(raw, out color),
            "hwb" => TryParseHwb(raw, out color),
            "lab" => TryParseLab(raw, out color),
            "lch" => TryParseLch(raw, out color),
            "oklab" => TryParseOklab(raw, out color),
            "oklch" => TryParseOklch(raw, out color),
            "color" => TryParseColorFn(raw, out color),
            "color-mix" => TryParseColorMix(raw, out color),
            _ => false,
        };
    }

    // ---------- CSS Color 5 §4: relative color syntax ----------

    /// <summary>True when the first non-whitespace argument is the <c>from</c>
    /// keyword. Returns the component values that follow it (the origin color
    /// plus channel expressions).</summary>
    private static bool StartsWithFrom(IReadOnlyList<CssComponentValue> raw, out IReadOnlyList<CssComponentValue> rest)
    {
        rest = raw;
        var noWs = FilterWhitespace(raw).ToList();
        if (noWs.Count == 0)
        {
            return false;
        }

        if (noWs[0] is not CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var kw } } ||
            !kw.Equals("from", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        rest = noWs.Skip(1).ToList();
        return true;
    }

    /// <summary>
    /// Resolve a relative color: parse the origin <c>&lt;color&gt;</c>, decompose
    /// it into the target space's channels, bind the channel keywords (e.g.
    /// <c>r g b</c>, <c>l c h</c>), and evaluate each channel expression — a bare
    /// number/percentage/angle, the <c>none</c> keyword, a channel keyword, or a
    /// <c>calc()</c> referencing channel keywords. CSS Color 5 §4.
    /// </summary>
    private static bool TryParseRelative(string fn, IReadOnlyList<CssComponentValue> rest, out CssColor color)
    {
        color = CssColor.Transparent;
        if (rest.Count == 0)
        {
            return false;
        }

        // The first component value is the origin color. It may be a function
        // (rgb(...), oklch(...), color(...)) or a single token (named / hex).
        var origin = rest[0];
        CssColor originColor;
        if (origin is CssFunction of)
        {
            if (!TryParseFunction(of.Name, of.Values, out originColor))
            {
                return false;
            }
        }
        else if (origin is CssTokenValue tv && TryParseInline(new List<CssComponentValue> { tv }, out var inline))
        {
            originColor = inline;
        }
        else
        {
            return false;
        }

        // Decompose the origin into the target space's channel symbol table.
        var symbols = DecomposeForFunction(fn, originColor);
        if (symbols is null)
        {
            return false;
        }

        // The remaining components are the channel expressions. Rewrite every
        // channel-keyword ident into a literal number token, then hand the
        // result to the normal per-function parser.
        var channelArgs = rest.Skip(1).Select(v => SubstituteChannels(v, symbols)).ToList();

        return fn switch
        {
            "rgb" or "rgba" => TryParseRgb(channelArgs, out color),
            "hsl" or "hsla" => TryParseHsl(channelArgs, out color),
            "hwb" => TryParseHwb(channelArgs, out color),
            "lab" => TryParseLab(channelArgs, out color),
            "lch" => TryParseLch(channelArgs, out color),
            "oklab" => TryParseOklab(channelArgs, out color),
            "oklch" => TryParseOklch(channelArgs, out color),
            _ => false,
        };
    }

    /// <summary>Decompose <paramref name="c"/> into the channel keywords used by
    /// the target color function, returning a case-insensitive symbol table
    /// (channel name → numeric value in the units the function's own parser
    /// expects). Returns null for unsupported functions.</summary>
    private static Dictionary<string, double>? DecomposeForFunction(string fn, CssColor c)
    {
        var alpha = double.IsNaN(c.AlphaF) ? 1.0 : c.AlphaF;
        var t = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["alpha"] = alpha };
        switch (fn)
        {
            case "rgb" or "rgba":
                {
                    // rgb() channel keywords are numbers in the 0..255 range.
                    var (r, g, b) = SrgbOf(c);
                    t["r"] = r * 255.0; t["g"] = g * 255.0; t["b"] = b * 255.0;
                    return t;
                }
            case "hsl" or "hsla":
                {
                    var (r, g, b) = SrgbOf(c);
                    var (h, s, l) = ColorConversion.SrgbToHsl(Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
                    // s/l keywords carry the 0..100 (percentage) magnitude.
                    t["h"] = h; t["s"] = s * 100.0; t["l"] = l * 100.0;
                    return t;
                }
            case "hwb":
                {
                    var (r, g, b) = SrgbOf(c);
                    var rr = Math.Clamp(r, 0, 1); var gg = Math.Clamp(g, 0, 1); var bb = Math.Clamp(b, 0, 1);
                    var (h, _, _) = ColorConversion.SrgbToHsl(rr, gg, bb);
                    var w = Math.Min(rr, Math.Min(gg, bb));
                    var bk = 1 - Math.Max(rr, Math.Max(gg, bb));
                    t["h"] = h; t["w"] = w * 100.0; t["b"] = bk * 100.0;
                    return t;
                }
            case "lab":
                {
                    var (L, a, b) = ComponentsIn(ColorSpace.Lab, c);
                    t["l"] = L; t["a"] = a; t["b"] = b;
                    return t;
                }
            case "lch":
                {
                    var (L, ch, h) = ComponentsIn(ColorSpace.Lch, c);
                    t["l"] = L; t["c"] = ch; t["h"] = h;
                    return t;
                }
            case "oklab":
                {
                    var (L, a, b) = ComponentsIn(ColorSpace.Oklab, c);
                    t["l"] = L; t["a"] = a; t["b"] = b;
                    return t;
                }
            case "oklch":
                {
                    var (L, ch, h) = ComponentsIn(ColorSpace.Oklch, c);
                    t["l"] = L; t["c"] = ch; t["h"] = h;
                    return t;
                }
            default:
                return null;
        }
    }

    /// <summary>sRGB components (0..1) of any color.</summary>
    private static (double R, double G, double B) SrgbOf(CssColor c)
    {
        if (!c.HasWideGamutData)
        {
            return (c.R / 255.0, c.G / 255.0, c.B / 255.0);
        }

        var s1 = double.IsNaN(c.C1) ? 0 : c.C1;
        var s2 = double.IsNaN(c.C2) ? 0 : c.C2;
        var s3 = double.IsNaN(c.C3) ? 0 : c.C3;
        return ColorConversion.ToSrgb(c.Space, s1, s2, s3);
    }

    /// <summary>Native components of <paramref name="c"/> expressed in
    /// <paramref name="target"/>, routing through XYZ-D65 when needed.</summary>
    private static (double, double, double) ComponentsIn(ColorSpace target, CssColor c)
    {
        if (c.HasWideGamutData && c.Space == target)
        {
            return (double.IsNaN(c.C1) ? 0 : c.C1, double.IsNaN(c.C2) ? 0 : c.C2, double.IsNaN(c.C3) ? 0 : c.C3);
        }

        double x, y, z;
        if (c.HasWideGamutData)
        {
            var s1 = double.IsNaN(c.C1) ? 0 : c.C1;
            var s2 = double.IsNaN(c.C2) ? 0 : c.C2;
            var s3 = double.IsNaN(c.C3) ? 0 : c.C3;
            (x, y, z) = ColorConversion.ToXyzD65(c.Space, s1, s2, s3);
        }
        else
        {
            (x, y, z) = ColorConversion.ToXyzD65(ColorSpace.Srgb, c.R / 255.0, c.G / 255.0, c.B / 255.0);
        }
        return XyzToSpace(target, x, y, z);
    }

    /// <summary>Recursively rewrite channel-keyword idents into literal numbers so
    /// the existing numeric component readers (and calc() evaluator) can consume
    /// the channel expressions unchanged.</summary>
    private static CssComponentValue SubstituteChannels(CssComponentValue v, IReadOnlyDictionary<string, double> symbols)
    {
        switch (v)
        {
            case CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var name } } when symbols.TryGetValue(name, out var val):
                return new CssTokenValue(new CssToken(CssTokenType.Number, Number: val, IsInteger: false));
            case CssFunction f:
                return new CssFunction(f.Name, f.Values.Select(c => SubstituteChannels(c, symbols)).ToList());
            case CssSimpleBlock b:
                return new CssSimpleBlock(b.StartToken, b.Values.Select(c => SubstituteChannels(c, symbols)).ToList());
            default:
                return v;
        }
    }

    // ---------- rgb / rgba ----------

    private static bool TryParseRgb(IReadOnlyList<CssComponentValue> raw, out CssColor color)
    {
        color = CssColor.Transparent;
        if (!TryReadThreePlusAlpha(raw, out var c1, out var c2, out var c3, out var alpha, out _))
        {
            return false;
        }

        // rgb accepts numbers 0..255 OR percentages 0..100. We treat percentages
        // by scaling to 0..1 before clamping.
        double r = NumToSrgbChannel(c1);
        double g = NumToSrgbChannel(c2);
        double b = NumToSrgbChannel(c3);
        color = CssColor.FromSrgb(r, g, b, alpha);
        return true;
    }

    private static double NumToSrgbChannel(Component c)
    {
        if (c.IsNone)
        {
            return double.NaN;
        }

        if (c.IsPercentage)
        {
            return Math.Clamp(c.Value / 100.0, 0.0, 1.0);
        }

        return Math.Clamp(c.Value / 255.0, 0.0, 1.0);
    }

    // ---------- hsl / hsla ----------

    private static bool TryParseHsl(IReadOnlyList<CssComponentValue> raw, out CssColor color)
    {
        color = CssColor.Transparent;
        if (!TryReadThreePlusAlpha(raw, out var c1, out var c2, out var c3, out var alpha, out _))
        {
            return false;
        }

        var h = ReadAngle(c1);
        var s = c2.IsNone ? 0 : (c2.IsPercentage ? c2.Value / 100.0 : c2.Value);
        var l = c3.IsNone ? 0 : (c3.IsPercentage ? c3.Value / 100.0 : c3.Value);
        var (r, g, b) = ColorConversion.HslToSrgb(h, s, l);
        color = CssColor.FromSrgb(r, g, b, alpha) with
        {
            Space = ColorSpace.Hsl,
            C1 = c1.IsNone ? double.NaN : h,
            C2 = c2.IsNone ? double.NaN : s,
            C3 = c3.IsNone ? double.NaN : l,
            AlphaF = alpha,
        };
        return true;
    }

    // ---------- hwb ----------

    private static bool TryParseHwb(IReadOnlyList<CssComponentValue> raw, out CssColor color)
    {
        color = CssColor.Transparent;
        if (!TryReadThreePlusAlpha(raw, out var c1, out var c2, out var c3, out var alpha, out _))
        {
            return false;
        }

        var h = ReadAngle(c1);
        var w = c2.IsNone ? 0 : (c2.IsPercentage ? c2.Value / 100.0 : c2.Value);
        var bk = c3.IsNone ? 0 : (c3.IsPercentage ? c3.Value / 100.0 : c3.Value);
        var (r, g, b) = ColorConversion.HwbToSrgb(h, w, bk);
        color = CssColor.FromSrgb(r, g, b, alpha) with
        {
            Space = ColorSpace.Hwb,
            C1 = c1.IsNone ? double.NaN : h,
            C2 = c2.IsNone ? double.NaN : w,
            C3 = c3.IsNone ? double.NaN : bk,
            AlphaF = alpha,
        };
        return true;
    }

    // ---------- lab / lch / oklab / oklch ----------

    private static bool TryParseLab(IReadOnlyList<CssComponentValue> raw, out CssColor color)
    {
        color = CssColor.Transparent;
        if (!TryReadThreePlusAlpha(raw, out var c1, out var c2, out var c3, out var alpha, out _))
        {
            return false;
        }
        // L is percentage 0..100 (modern), or raw 0..100.
        var L = c1.IsPercentage ? c1.Value : c1.Value;
        var a = c2.IsPercentage ? c2.Value * 1.25 : c2.Value; // 100% = 125
        var b = c3.IsPercentage ? c3.Value * 1.25 : c3.Value;
        color = CssColor.FromComponents(ColorSpace.Lab,
            c1.IsNone ? double.NaN : L,
            c2.IsNone ? double.NaN : a,
            c3.IsNone ? double.NaN : b,
            alpha);
        return true;
    }

    private static bool TryParseLch(IReadOnlyList<CssComponentValue> raw, out CssColor color)
    {
        color = CssColor.Transparent;
        if (!TryReadThreePlusAlpha(raw, out var c1, out var c2, out var c3, out var alpha, out _))
        {
            return false;
        }

        var L = c1.IsPercentage ? c1.Value : c1.Value;
        var c = c2.IsPercentage ? c2.Value * 1.5 : c2.Value; // 100% = 150
        var h = ReadAngle(c3);
        color = CssColor.FromComponents(ColorSpace.Lch,
            c1.IsNone ? double.NaN : L,
            c2.IsNone ? double.NaN : c,
            c3.IsNone ? double.NaN : h,
            alpha);
        return true;
    }

    private static bool TryParseOklab(IReadOnlyList<CssComponentValue> raw, out CssColor color)
    {
        color = CssColor.Transparent;
        if (!TryReadThreePlusAlpha(raw, out var c1, out var c2, out var c3, out var alpha, out _))
        {
            return false;
        }

        var L = c1.IsPercentage ? c1.Value / 100.0 : c1.Value;
        var a = c2.IsPercentage ? c2.Value * 0.004 : c2.Value;
        var b = c3.IsPercentage ? c3.Value * 0.004 : c3.Value;
        color = CssColor.FromComponents(ColorSpace.Oklab,
            c1.IsNone ? double.NaN : L,
            c2.IsNone ? double.NaN : a,
            c3.IsNone ? double.NaN : b,
            alpha);
        return true;
    }

    private static bool TryParseOklch(IReadOnlyList<CssComponentValue> raw, out CssColor color)
    {
        color = CssColor.Transparent;
        if (!TryReadThreePlusAlpha(raw, out var c1, out var c2, out var c3, out var alpha, out _))
        {
            return false;
        }

        var L = c1.IsPercentage ? c1.Value / 100.0 : c1.Value;
        var c = c2.IsPercentage ? c2.Value * 0.004 : c2.Value;
        var h = ReadAngle(c3);
        color = CssColor.FromComponents(ColorSpace.Oklch,
            c1.IsNone ? double.NaN : L,
            c2.IsNone ? double.NaN : c,
            c3.IsNone ? double.NaN : h,
            alpha);
        return true;
    }

    // ---------- color(<space> c1 c2 c3 [/ alpha]) ----------

    private static bool TryParseColorFn(IReadOnlyList<CssComponentValue> raw, out CssColor color)
    {
        color = CssColor.Transparent;
        var tokens = FilterWhitespace(raw).ToList();
        if (tokens.Count == 0)
        {
            return false;
        }

        if (tokens[0] is not CssTokenValue { Token: { Type: CssTokenType.Ident } } id)
        {
            return false;
        }

        if (!TryParseSpace(id.Token.Value, out var space))
        {
            return false;
        }

        var rest = tokens.Skip(1).ToList();
        // Split on slash for alpha (modern only).
        var (channelTokens, alphaTokens) = SplitOnSlash(rest);
        var ch = channelTokens.Select(TokenToComponent).ToList();
        if (ch.Count != 3)
        {
            return false;
        }

        var alpha = alphaTokens is null ? 1.0 : ComponentToFloat(TokenToComponent(alphaTokens[0]), 1.0);
        color = CssColor.FromComponents(space,
            ch[0].IsNone ? double.NaN : NormalizeColorChannel(ch[0], space, 0),
            ch[1].IsNone ? double.NaN : NormalizeColorChannel(ch[1], space, 1),
            ch[2].IsNone ? double.NaN : NormalizeColorChannel(ch[2], space, 2),
            alpha);
        return true;
    }

    private static double NormalizeColorChannel(Component c, ColorSpace space, int index)
        => c.IsPercentage ? c.Value / 100.0 : c.Value;

    private static bool TryParseSpace(string name, out ColorSpace space)
    {
        switch (name.ToLowerInvariant())
        {
            case "srgb": space = ColorSpace.Srgb; return true;
            case "srgb-linear": space = ColorSpace.SrgbLinear; return true;
            case "display-p3": space = ColorSpace.DisplayP3; return true;
            case "a98-rgb": space = ColorSpace.A98Rgb; return true;
            case "rec2020": space = ColorSpace.Rec2020; return true;
            case "prophoto-rgb": space = ColorSpace.ProphotoRgb; return true;
            case "xyz" or "xyz-d65": space = ColorSpace.XyzD65; return true;
            case "xyz-d50": space = ColorSpace.XyzD50; return true;
            default: space = ColorSpace.Srgb; return false;
        }
    }

    // ---------- color-mix(in <space>, <c1> [pct], <c2> [pct]) ----------

    private static bool TryParseColorMix(IReadOnlyList<CssComponentValue> raw, out CssColor color)
    {
        color = CssColor.Transparent;
        // Split args on commas at top level.
        var args = SplitTopLevelCommas(raw).ToList();
        if (args.Count != 3)
        {
            return false;
        }

        // arg0: "in <space>" optionally followed by "<hint> hue" per Color 5 §6.
        var ws0 = FilterWhitespace(args[0]).ToList();
        if (ws0.Count < 2)
        {
            return false;
        }

        if (ws0[0] is not CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var inKw } } || !inKw.Equals("in", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ws0[1] is not CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var spaceName } })
        {
            return false;
        }

        if (!TryParseInterpolationSpace(spaceName, out var space))
        {
            return false;
        }

        var hueStrategy = HueInterpolation.Shorter;
        if (ws0.Count >= 4)
        {
            if (ws0[2] is not CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var hintName } })
            {
                return false;
            }

            if (ws0[3] is not CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var hueKw } } || !hueKw.Equals("hue", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            switch (hintName.ToLowerInvariant())
            {
                case "shorter": hueStrategy = HueInterpolation.Shorter; break;
                case "longer": hueStrategy = HueInterpolation.Longer; break;
                case "increasing": hueStrategy = HueInterpolation.Increasing; break;
                case "decreasing": hueStrategy = HueInterpolation.Decreasing; break;
                default: return false;
            }
            var polar = space is ColorSpace.Hsl or ColorSpace.Hwb or ColorSpace.Lch or ColorSpace.Oklch;
            if (!polar)
            {
                return false;
            }
        }

        // arg1, arg2: color [percentage]?
        if (!TryParseColorAndPercentage(args[1], out var ca, out var pa))
        {
            return false;
        }

        if (!TryParseColorAndPercentage(args[2], out var cb, out var pb))
        {
            return false;
        }

        // Normalize percentages per Color 5 §6: if both omitted, 50/50; if one
        // omitted, infer from the other; if both >100 normalize.
        if (pa is null && pb is null) { pa = 50; pb = 50; }
        else if (pa is null) { pa = 100 - pb!.Value; }
        else if (pb is null) { pb = 100 - pa.Value; }
        var sum = pa.Value + pb.Value;
        if (sum <= 0)
        {
            return false;
        }

        var wA = pa.Value / sum;
        var wB = pb.Value / sum;

        color = Mix(space, ca, wA, cb, wB, hueStrategy);
        return true;
    }

    private enum HueInterpolation { Shorter, Longer, Increasing, Decreasing }

    private static bool TryParseInterpolationSpace(string name, out ColorSpace space)
    {
        switch (name.ToLowerInvariant())
        {
            case "srgb": space = ColorSpace.Srgb; return true;
            case "srgb-linear": space = ColorSpace.SrgbLinear; return true;
            case "lab": space = ColorSpace.Lab; return true;
            case "oklab": space = ColorSpace.Oklab; return true;
            case "lch": space = ColorSpace.Lch; return true;
            case "oklch": space = ColorSpace.Oklch; return true;
            case "hsl": space = ColorSpace.Hsl; return true;
            case "hwb": space = ColorSpace.Hwb; return true;
            default: space = ColorSpace.Srgb; return false;
        }
    }

    private static bool TryParseColorAndPercentage(IReadOnlyList<CssComponentValue> arg, out CssColor c, out double? percentage)
    {
        c = CssColor.Transparent;
        percentage = null;
        var ws = FilterWhitespace(arg).ToList();
        if (ws.Count == 0)
        {
            return false;
        }

        // Trailing percentage?
        var lastIdx = ws.Count - 1;
        if (ws[lastIdx] is CssTokenValue { Token: { Type: CssTokenType.Percentage } } pct)
        {
            percentage = pct.Token.Number;
            ws.RemoveAt(lastIdx);
        }
        // Color may be a function or a single token.
        if (ws.Count == 0)
        {
            return false;
        }

        return TryParseInline(ws, out c);
    }

    private static bool TryParseInline(List<CssComponentValue> tokens, out CssColor color)
    {
        color = CssColor.Transparent;
        if (tokens.Count == 1)
        {
            if (tokens[0] is CssTokenValue { Token: { Type: CssTokenType.Ident, Value: var ident } } &&
                NamedColors.TryGet(ident, out var named))
            {
                color = named;
                return true;
            }
            if (tokens[0] is CssTokenValue { Token: { Type: CssTokenType.Hash, Value: var hex } } &&
                TryParseHex(hex, out var hexColor))
            {
                color = hexColor;
                return true;
            }
            if (tokens[0] is CssFunction f)
            {
                return TryParseFunction(f.Name, f.Values, out color);
            }
        }
        return false;
    }

    // ---------- mixing math ----------

    private static CssColor Mix(ColorSpace space, CssColor a, double wA, CssColor b, double wB, HueInterpolation hue = HueInterpolation.Shorter)
    {
        // Convert both to interpolation space, lerp components, convert back to sRGB for storage.
        var (a1, a2, a3) = ToInterpolation(space, a);
        var (b1, b2, b3) = ToInterpolation(space, b);
        var aAlpha = double.IsNaN(a.AlphaF) ? 0 : a.AlphaF;
        var bAlpha = double.IsNaN(b.AlphaF) ? 0 : b.AlphaF;

        var alpha = aAlpha * wA + bAlpha * wB;

        bool polar = space is ColorSpace.Hsl or ColorSpace.Hwb or ColorSpace.Lch or ColorSpace.Oklch;
        double c1, c2, c3;
        if (polar)
        {
            // Hue is the first component for hsl/hwb, third for lch/oklch.
            if (space is ColorSpace.Hsl or ColorSpace.Hwb)
            {
                c1 = LerpHue(a1, b1, wA, hue);
                c2 = a2 * wA + b2 * wB;
                c3 = a3 * wA + b3 * wB;
            }
            else
            {
                c1 = a1 * wA + b1 * wB;
                c2 = a2 * wA + b2 * wB;
                c3 = LerpHue(a3, b3, wA, hue);
            }
        }
        else
        {
            c1 = a1 * wA + b1 * wB;
            c2 = a2 * wA + b2 * wB;
            c3 = a3 * wA + b3 * wB;
        }

        return CssColor.FromComponents(space, c1, c2, c3, alpha);
    }

    private static double LerpHue(double aH, double bH, double wA, HueInterpolation strategy)
    {
        // Normalize hues to [0, 360).
        aH = ((aH % 360.0) + 360.0) % 360.0;
        bH = ((bH % 360.0) + 360.0) % 360.0;
        double diff;
        switch (strategy)
        {
            case HueInterpolation.Shorter:
                diff = bH - aH;
                while (diff > 180)
                {
                    diff -= 360;
                }

                while (diff < -180)
                {
                    diff += 360;
                }

                break;
            case HueInterpolation.Longer:
                diff = bH - aH;
                while (diff > 180)
                {
                    diff -= 360;
                }

                while (diff < -180)
                {
                    diff += 360;
                }
                // Take the long way around: flip 0->360 or 0->-360.
                if (diff == 0)
                {
                    diff = 360;
                }
                else if (diff > 0)
                {
                    diff -= 360;
                }
                else
                {
                    diff += 360;
                }

                break;
            case HueInterpolation.Increasing:
                diff = bH - aH;
                if (diff < 0)
                {
                    diff += 360;
                }

                break;
            case HueInterpolation.Decreasing:
                diff = bH - aH;
                if (diff > 0)
                {
                    diff -= 360;
                }

                break;
            default:
                diff = bH - aH;
                break;
        }
        var result = aH + diff * (1 - wA);
        return ((result % 360.0) + 360.0) % 360.0;
    }

    private static (double, double, double) ToInterpolation(ColorSpace space, CssColor c)
    {
        // Determine the source components in their native space.
        double src1, src2, src3;
        ColorSpace srcSpace;
        if (c.HasWideGamutData)
        {
            srcSpace = c.Space;
            src1 = double.IsNaN(c.C1) ? 0 : c.C1;
            src2 = double.IsNaN(c.C2) ? 0 : c.C2;
            src3 = double.IsNaN(c.C3) ? 0 : c.C3;
        }
        else
        {
            // Legacy / named / hex colors: only byte view is reliable.
            srcSpace = ColorSpace.Srgb;
            src1 = c.R / 255.0; src2 = c.G / 255.0; src3 = c.B / 255.0;
        }
        if (srcSpace == space)
        {
            return (src1, src2, src3);
        }
        // Otherwise go through XYZ-D65 → target.
        var (x, y, z) = ColorConversion.ToXyzD65(srcSpace, src1, src2, src3);
        return XyzToSpace(space, x, y, z);
    }

    private static (double, double, double) XyzToSpace(ColorSpace space, double x, double y, double z)
    {
        switch (space)
        {
            case ColorSpace.Srgb:
                var (r, g, b) = ColorConversion.XyzD65ToSrgb(x, y, z);
                return (r, g, b);
            case ColorSpace.SrgbLinear:
                var (lr, lg, lb) = ColorConversion.XyzD65ToLinearSrgb(x, y, z);
                return (lr, lg, lb);
            case ColorSpace.Oklab:
                var (oL, oa, ob) = ColorConversion.XyzD65ToOklab(x, y, z);
                return (oL, oa, ob);
            case ColorSpace.Oklch:
                var (oL2, oa2, ob2) = ColorConversion.XyzD65ToOklab(x, y, z);
                var oc = Math.Sqrt(oa2 * oa2 + ob2 * ob2);
                var oh = Math.Atan2(ob2, oa2) * 180.0 / Math.PI;
                if (oh < 0)
                {
                    oh += 360;
                }

                return (oL2, oc, oh);
            case ColorSpace.Lab:
                var (x50, y50, z50) = ColorConversion.XyzD65ToXyzD50(x, y, z);
                var (lL, la, lb2) = ColorConversion.XyzD50ToLab(x50, y50, z50);
                return (lL, la, lb2);
            case ColorSpace.Lch:
                var (x51, y51, z51) = ColorConversion.XyzD65ToXyzD50(x, y, z);
                var (lL2, la2, lb3) = ColorConversion.XyzD50ToLab(x51, y51, z51);
                var lc = Math.Sqrt(la2 * la2 + lb3 * lb3);
                var lh = Math.Atan2(lb3, la2) * 180.0 / Math.PI;
                if (lh < 0)
                {
                    lh += 360;
                }

                return (lL2, lc, lh);
            case ColorSpace.Hsl:
                var (sr, sg, sb) = ColorConversion.XyzD65ToSrgb(x, y, z);
                return ColorConversion.SrgbToHsl(Math.Clamp(sr, 0, 1), Math.Clamp(sg, 0, 1), Math.Clamp(sb, 0, 1));
            case ColorSpace.Hwb:
                var (wr, wg, wb) = ColorConversion.XyzD65ToSrgb(x, y, z);
                var rr = Math.Clamp(wr, 0, 1); var gg = Math.Clamp(wg, 0, 1); var bb = Math.Clamp(wb, 0, 1);
                var (hh, _, _) = ColorConversion.SrgbToHsl(rr, gg, bb);
                var W = Math.Min(rr, Math.Min(gg, bb));
                var B = 1 - Math.Max(rr, Math.Max(gg, bb));
                return (hh, W, B);
            default:
                return (x, y, z);
        }
    }

    // ---------- hex parsing ----------

    public static bool TryParseHex(string text, out CssColor color)
    {
        color = CssColor.Transparent;
        if (text.Length is not (3 or 4 or 6 or 8) || text.Any(c => !Uri.IsHexDigit(c)))
        {
            return false;
        }

        var expanded = text.Length switch
        {
            3 => string.Concat(text.Select(c => $"{c}{c}")) + "ff",
            4 => string.Concat(text.Select(c => $"{c}{c}")),
            6 => text + "ff",
            _ => text,
        };
        color = new CssColor(
            Convert.ToByte(expanded[..2], 16),
            Convert.ToByte(expanded[2..4], 16),
            Convert.ToByte(expanded[4..6], 16),
            Convert.ToByte(expanded[6..8], 16));
        return true;
    }

    // ---------- helpers: component reading ----------

    /// <summary>Parsed numeric component or "none" placeholder.</summary>
    private readonly record struct Component(double Value, bool IsPercentage, bool IsNone, bool IsAngle, CssAngleUnit AngleUnit);

    private static Component TokenToComponent(CssComponentValue v)
    {
        if (v is CssTokenValue { Token: { Type: CssTokenType.Ident, Value: "none" } })
        {
            return new Component(0, false, true, false, CssAngleUnit.Degrees);
        }

        if (v is CssTokenValue tv)
        {
            return tv.Token.Type switch
            {
                CssTokenType.Number => new Component(tv.Token.Number, false, false, false, CssAngleUnit.Degrees),
                CssTokenType.Percentage => new Component(tv.Token.Number, true, false, false, CssAngleUnit.Degrees),
                CssTokenType.Dimension => DimensionToComponent(tv.Token.Number, tv.Token.Unit),
                _ => new Component(0, false, false, false, CssAngleUnit.Degrees),
            };
        }
        if (v is CssFunction f)
        {
            // calc() etc. produce a numeric — reduce.
            var node = CalcEvaluator.ParseFunctionNode(f.Name, f.Values);
            if (node is CalcNumber n)
            {
                return new Component(n.Value, false, false, false, CssAngleUnit.Degrees);
            }

            if (node is CalcPercentage p)
            {
                return new Component(p.Value, true, false, false, CssAngleUnit.Degrees);
            }

            if (node is CalcAngle a)
            {
                return new Component(a.Value, false, false, true, a.Unit);
            }
        }
        return new Component(0, false, false, false, CssAngleUnit.Degrees);
    }

    private static Component DimensionToComponent(double value, string unit)
        => unit.ToLowerInvariant() switch
        {
            "deg" => new Component(value, false, false, true, CssAngleUnit.Degrees),
            "grad" => new Component(value, false, false, true, CssAngleUnit.Gradians),
            "rad" => new Component(value, false, false, true, CssAngleUnit.Radians),
            "turn" => new Component(value, false, false, true, CssAngleUnit.Turns),
            _ => new Component(value, false, false, false, CssAngleUnit.Degrees),
        };

    private static double ReadAngle(Component c)
    {
        if (c.IsNone)
        {
            return 0;
        }

        if (!c.IsAngle)
        {
            return c.Value;
        }

        return c.AngleUnit switch
        {
            CssAngleUnit.Degrees => c.Value,
            CssAngleUnit.Gradians => c.Value * 0.9,
            CssAngleUnit.Radians => c.Value * 180.0 / Math.PI,
            CssAngleUnit.Turns => c.Value * 360.0,
            _ => c.Value,
        };
    }

    private static double ComponentToFloat(Component c, double scale)
    {
        if (c.IsNone)
        {
            return double.NaN;
        }

        return c.IsPercentage ? c.Value / 100.0 * scale : c.Value;
    }

    /// <summary>
    /// Accept either legacy comma-separated or modern whitespace+slash syntax.
    /// Returns three channel components plus alpha (defaulting to 1.0).
    /// </summary>
    private static bool TryReadThreePlusAlpha(
        IReadOnlyList<CssComponentValue> raw,
        out Component c1, out Component c2, out Component c3, out double alpha, out bool legacy)
    {
        c1 = c2 = c3 = default;
        alpha = 1.0;
        legacy = false;

        var noWs = FilterWhitespace(raw).ToList();
        // Detect commas: if any commas exist at top level, treat as legacy syntax.
        var hasComma = noWs.Any(v => v is CssTokenValue { Token: { Type: CssTokenType.Comma } });
        if (hasComma)
        {
            legacy = true;
            var parts = SplitTopLevelCommas(raw)
                .Select(p => FilterWhitespace(p).ToList())
                .Where(p => p.Count > 0)
                .ToList();
            if (parts.Count is < 3 or > 4)
            {
                return false;
            }

            c1 = TokenToComponent(parts[0][0]);
            c2 = TokenToComponent(parts[1][0]);
            c3 = TokenToComponent(parts[2][0]);
            if (parts.Count == 4)
            {
                alpha = ComponentToFloat(TokenToComponent(parts[3][0]), 1.0);
            }

            return true;
        }

        // Modern: split on / for alpha.
        var (chTokens, alphaTokens) = SplitOnSlash(noWs);
        if (chTokens.Count != 3)
        {
            return false;
        }

        c1 = TokenToComponent(chTokens[0]);
        c2 = TokenToComponent(chTokens[1]);
        c3 = TokenToComponent(chTokens[2]);
        if (alphaTokens is not null && alphaTokens.Count > 0)
        {
            alpha = ComponentToFloat(TokenToComponent(alphaTokens[0]), 1.0);
        }

        return true;
    }

    private static IEnumerable<CssComponentValue> FilterWhitespace(IEnumerable<CssComponentValue> raw)
        => raw.Where(v => v is not CssTokenValue { Token.Type: CssTokenType.Whitespace });

    private static IEnumerable<IReadOnlyList<CssComponentValue>> SplitTopLevelCommas(IReadOnlyList<CssComponentValue> raw)
    {
        var current = new List<CssComponentValue>();
        foreach (var v in raw)
        {
            if (v is CssTokenValue { Token.Type: CssTokenType.Comma })
            {
                yield return current;
                current = new List<CssComponentValue>();
                continue;
            }
            current.Add(v);
        }
        yield return current;
    }

    private static (List<CssComponentValue> Channels, List<CssComponentValue>? Alpha) SplitOnSlash(List<CssComponentValue> raw)
    {
        var ch = new List<CssComponentValue>();
        List<CssComponentValue>? alpha = null;
        var seenSlash = false;
        foreach (var v in raw)
        {
            if (!seenSlash && v is CssTokenValue { Token: { Type: CssTokenType.Delim, Delimiter: '/' } })
            {
                seenSlash = true;
                alpha = new List<CssComponentValue>();
                continue;
            }
            if (seenSlash)
            {
                alpha!.Add(v);
            }
            else
            {
                ch.Add(v);
            }
        }
        return (ch, alpha);
    }
}
