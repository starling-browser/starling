namespace Starling.Css.Values;

/// <summary>
/// Parses the CSS Images 3 <c>&lt;gradient&gt;</c> functions from the generic
/// <see cref="CssFunctionValue"/> produced by <see cref="CssValueParser"/> into
/// a typed <see cref="CssGradient"/>. Mirrors <see cref="CssTransformParser"/>:
/// fail-soft — any unrecognised syntax returns <c>null</c> so the caller leaves
/// the property unset (CSS Images 3 §3, invalid gradient → not painted).
/// <para>
/// <see cref="CssValueParser"/> splits gradient arguments on top-level commas,
/// so each entry is either a single value (e.g. <c>red</c>, <c>90deg</c>) or a
/// <see cref="CssValueList"/> of whitespace-separated values (e.g.
/// <c>red 10%</c>, <c>to top right</c>, <c>circle at center</c>).
/// </para>
/// </summary>
public static class CssGradientParser
{
    public static bool TryParse(CssValue value, out CssGradient gradient)
    {
        // Idempotent: the animation interpolator emits typed CssGradient
        // intermediates; pass them straight through.
        if (value is CssGradient typed)
        {
            gradient = typed;
            return true;
        }
        gradient = null!;
        if (value is not CssFunctionValue fn)
        {
            return false;
        }

        return TryParseFunction(fn, out gradient);
    }

    public static bool TryParseFunction(CssFunctionValue fn, out CssGradient gradient)
    {
        gradient = null!;
        var name = fn.Name.ToLowerInvariant();
        var repeating = name.StartsWith("repeating-", StringComparison.Ordinal);
        var bare = repeating ? name["repeating-".Length..] : name;

        return bare switch
        {
            "linear-gradient" => TryParseLinear(fn.Arguments, repeating, out gradient),
            "radial-gradient" => TryParseRadial(fn.Arguments, repeating, out gradient),
            "conic-gradient" => TryParseConic(fn.Arguments, repeating, out gradient),
            _ => false,
        };
    }

    private static bool TryParseLinear(IReadOnlyList<CssValue> args, bool repeating, out CssGradient gradient)
    {
        gradient = null!;
        if (args.Count == 0)
        {
            return false;
        }

        var idx = 0;

        // CSS Color 4: optional `in <colorspace> [<hue-method> hue]` prelude before
        // the gradient line. Check the first token for the `in` keyword.
        GradientInterpolationMethod? interp = null;
        if (TryParseInterpolationPrelude(args, ref idx, out interp))
        {
            // consumed from idx
        }

        CssGradientLine? line = null;

        // Next argument may be the gradient line: an <angle> or `to <side>`.
        if (idx < args.Count && TryParseLine(args[idx], out var parsedLine))
        {
            line = parsedLine;
            idx++;
        }

        if (!TryParseStops(args, idx, out var stops))
        {
            return false;
        }

        gradient = new CssGradient(CssGradientKind.Linear, repeating, stops, Line: line, Interpolation: interp);
        return true;
    }

    private static bool TryParseRadial(IReadOnlyList<CssValue> args, bool repeating, out CssGradient gradient)
    {
        gradient = null!;
        if (args.Count == 0)
        {
            return false;
        }

        var idx = 0;

        // CSS Color 4: optional `in <colorspace>` prelude.
        GradientInterpolationMethod? interp = null;
        TryParseInterpolationPrelude(args, ref idx, out interp);

        var shape = CssRadialShape.Ellipse;
        var size = CssRadialSize.FarthestCorner;
        CssGradientPosition? position = null;

        // Next argument may describe the ending shape: `[<shape> || <size>] [at <position>]`.
        if (idx < args.Count && TryParseRadialPrelude(args[idx], ref shape, ref size, ref position))
        {
            idx++;
        }

        if (!TryParseStops(args, idx, out var stops))
        {
            return false;
        }

        gradient = new CssGradient(CssGradientKind.Radial, repeating, stops, Shape: shape, Size: size, Position: position, Interpolation: interp);
        return true;
    }

    private static bool TryParseConic(IReadOnlyList<CssValue> args, bool repeating, out CssGradient gradient)
    {
        gradient = null!;
        if (args.Count == 0)
        {
            return false;
        }

        var idx = 0;

        // CSS Color 4: optional `in <colorspace>` prelude (appears before `from`).
        GradientInterpolationMethod? interp = null;
        TryParseInterpolationPrelude(args, ref idx, out interp);

        // First remaining argument may be the conic prelude: `[from <angle>] [at <position>]`.
        // The `from` angle is stored on Line (clockwise from straight up) and the
        // `at` position on Position, mirroring the linear/radial value shape.
        CssGradientLine? line = null;
        CssGradientPosition? position = null;
        if (idx < args.Count && TryParseConicPrelude(args[idx], out line, out position))
        {
            idx++;
        }

        if (!TryParseStops(args, idx, out var stops))
        {
            return false;
        }

        gradient = new CssGradient(CssGradientKind.Conic, repeating, stops, Line: line, Position: position, Interpolation: interp);
        return true;
    }

    // --- CSS Color 4 §12.3 interpolation prelude: `in <colorspace> [<hue-method> hue]` ---

    /// <summary>
    /// Tries to parse a CSS Color 4 <c>in &lt;colorspace&gt; [&lt;hue-method&gt; hue]</c>
    /// prelude from <paramref name="args"/> starting at <paramref name="idx"/>.
    /// Advances <paramref name="idx"/> past the consumed arg(s) on success.
    /// The prelude is a single arg that starts with the keyword <c>in</c>.
    /// CSS Images 4 §3.1 — the interpolation method is the first optional part
    /// of a gradient function before the direction/stops.
    /// </summary>
    private static bool TryParseInterpolationPrelude(IReadOnlyList<CssValue> args, ref int idx, out GradientInterpolationMethod? method)
    {
        method = null;
        if (idx >= args.Count)
        {
            return false;
        }

        // The interp prelude is a whitespace-separated token list starting with `in`
        // inside a single comma-arg. It is either:
        //   CssKeyword("in") → single keyword arg (the colorspace must be next arg, unusual)
        //   CssValueList([in, colorspace, ...]) → most common form
        // CssValueParser splits on top-level commas, so `in oklch` arrives as one
        // CssValueList (or two separate scalar args if authored `in,oklch` — rare, skip).

        var arg = args[idx];

        IReadOnlyList<CssValue> tokens = arg switch
        {
            CssValueList list => list.Values,
            CssKeyword { Name: "in" } => [arg],
            _ => [],
        };

        if (tokens.Count == 0)
        {
            return false;
        }

        if (tokens[0] is not CssKeyword { Name: "in" })
        {
            return false;
        }

        if (tokens.Count < 2)
        {
            return false;
        }

        if (tokens[1] is not CssKeyword csKw)
        {
            return false;
        }

        if (!TryParseColorSpace(csKw.Name, out var cs))
        {
            return false;
        }

        // Optional hue interpolation method: `<method> hue` (two tokens: keyword + "hue").
        var hue = HueInterpolationMethod.Shorter;
        if (tokens.Count >= 4
            && tokens[2] is CssKeyword { Name: var hueName }
            && tokens[3] is CssKeyword { Name: "hue" }
            && TryParseHueMethod(hueName, out var hm))
        {
            hue = hm;
        }

        method = new GradientInterpolationMethod(cs, hue);
        idx++;
        return true;
    }

    private static bool TryParseColorSpace(string name, out GradientColorSpace cs)
    {
        cs = name switch
        {
            "srgb" => GradientColorSpace.Srgb,
            "srgb-linear" => GradientColorSpace.SrgbLinear,
            "oklab" => GradientColorSpace.Oklab,
            "oklch" => GradientColorSpace.Oklch,
            "hsl" => GradientColorSpace.Hsl,
            "hwb" => GradientColorSpace.Hwb,
            "lab" => GradientColorSpace.Lab,
            "lch" => GradientColorSpace.Lch,
            "display-p3" => GradientColorSpace.DisplayP3,
            "a98-rgb" => GradientColorSpace.A98Rgb,
            "prophoto-rgb" => GradientColorSpace.ProphotoRgb,
            "rec2020" => GradientColorSpace.Rec2020,
            "xyz-d50" => GradientColorSpace.XyzD50,
            "xyz-d65" or "xyz" => GradientColorSpace.XyzD65,
            _ => default,
        };
        return name is "srgb" or "srgb-linear" or "oklab" or "oklch" or "hsl" or "hwb"
            or "lab" or "lch" or "display-p3" or "a98-rgb" or "prophoto-rgb"
            or "rec2020" or "xyz-d50" or "xyz-d65" or "xyz";
    }

    private static bool TryParseHueMethod(string name, out HueInterpolationMethod hm)
    {
        hm = name switch
        {
            "shorter" => HueInterpolationMethod.Shorter,
            "longer" => HueInterpolationMethod.Longer,
            "increasing" => HueInterpolationMethod.Increasing,
            "decreasing" => HueInterpolationMethod.Decreasing,
            _ => default,
        };
        return name is "shorter" or "longer" or "increasing" or "decreasing";
    }

    private static bool TryParseConicPrelude(CssValue value, out CssGradientLine? line, out CssGradientPosition? position)
    {
        line = null;
        position = null;

        // A bare color (stop) is not a prelude.
        if (LooksLikeColorStop(value))
        {
            return false;
        }

        var tokens = value switch
        {
            CssValueList list => list.Values,
            _ => new[] { value },
        };

        var i = 0;
        var matched = false;

        if (i < tokens.Count && tokens[i] is CssKeyword { Name: "from" })
        {
            i++;
            if (i >= tokens.Count || !TryConicAngle(tokens[i], out var deg))
            {
                return false; // `from` with no angle is invalid syntax.
            }

            line = CssGradientLine.FromAngle(deg);
            i++;
            matched = true;
        }

        if (i < tokens.Count && tokens[i] is CssKeyword { Name: "at" })
        {
            if (!TryParsePosition(tokens, i + 1, out var pos))
            {
                return false;
            }

            position = pos;
            i = tokens.Count;
            matched = true;
        }

        // Every prelude token must be consumed — a leftover token means this
        // wasn't a prelude we understand, so fail soft.
        return matched && i >= tokens.Count;
    }

    private static bool TryConicAngle(CssValue value, out double degrees)
    {
        switch (value)
        {
            case CssAngle a:
                degrees = a.InDegrees;
                return true;
            case CssNumber { Value: 0 }:
                degrees = 0;
                return true;
            default:
                degrees = 0;
                return false;
        }
    }

    // --- gradient line ---

    private static bool TryParseLine(CssValue value, out CssGradientLine line)
    {
        line = null!;
        switch (value)
        {
            case CssAngle angle:
                line = CssGradientLine.FromAngle(angle.InDegrees);
                return true;
            case CssNumber n when n.Value == 0:
                line = CssGradientLine.FromAngle(0);
                return true;
            case CssValueList list when IsToSide(list.Values, out var sx, out var sy):
                line = CssGradientLine.FromSide(sx, sy);
                return true;
            default:
                return false;
        }
    }

    private static bool IsToSide(IReadOnlyList<CssValue> values, out CssGradientSideX sx, out CssGradientSideY sy)
    {
        sx = CssGradientSideX.None;
        sy = CssGradientSideY.None;
        if (values.Count < 2)
        {
            return false;
        }

        if (values[0] is not CssKeyword { Name: "to" })
        {
            return false;
        }

        for (var i = 1; i < values.Count; i++)
        {
            if (values[i] is not CssKeyword kw)
            {
                return false;
            }

            switch (kw.Name)
            {
                case "left": sx = CssGradientSideX.Left; break;
                case "right": sx = CssGradientSideX.Right; break;
                case "top": sy = CssGradientSideY.Top; break;
                case "bottom": sy = CssGradientSideY.Bottom; break;
                default: return false;
            }
        }
        return sx != CssGradientSideX.None || sy != CssGradientSideY.None;
    }

    // --- radial prelude: shape / size / position ---

    private static bool TryParseRadialPrelude(CssValue value, ref CssRadialShape shape, ref CssRadialSize size, ref CssGradientPosition? position)
    {
        var tokens = value switch
        {
            CssValueList list => list.Values,
            _ => new[] { value },
        };

        // If the first token already looks like a color stop, this isn't a prelude.
        if (LooksLikeColorStop(value))
        {
            return false;
        }

        var matchedAny = false;
        var i = 0;
        while (i < tokens.Count)
        {
            var t = tokens[i];
            if (t is CssKeyword kw)
            {
                switch (kw.Name)
                {
                    case "circle": shape = CssRadialShape.Circle; matchedAny = true; i++; continue;
                    case "ellipse": shape = CssRadialShape.Ellipse; matchedAny = true; i++; continue;
                    case "closest-side": size = CssRadialSize.ClosestSide; matchedAny = true; i++; continue;
                    case "closest-corner": size = CssRadialSize.ClosestCorner; matchedAny = true; i++; continue;
                    case "farthest-side": size = CssRadialSize.FarthestSide; matchedAny = true; i++; continue;
                    case "farthest-corner": size = CssRadialSize.FarthestCorner; matchedAny = true; i++; continue;
                    case "at":
                        // Remaining tokens form the position.
                        if (TryParsePosition(tokens, i + 1, out var pos))
                        {
                            position = pos;
                            matchedAny = true;
                        }
                        return matchedAny;
                    default:
                        // Unknown keyword in prelude position — treat as not-a-prelude.
                        return matchedAny;
                }
            }

            // Explicit radius lengths (e.g. `100px 50px`) are accepted as a
            // prelude marker but not yet honored for sizing; treat as ellipse.
            if (t is CssLength or CssPercentage)
            {
                matchedAny = true;
                i++;
                continue;
            }

            return matchedAny;
        }
        return matchedAny;
    }

    private static bool TryParsePosition(IReadOnlyList<CssValue> tokens, int start, out CssGradientPosition position)
    {
        position = CssGradientPosition.Center;
        double? x = null, y = null;
        for (var i = start; i < tokens.Count; i++)
        {
            switch (tokens[i])
            {
                case CssKeyword { Name: "left" }: x = 0; break;
                case CssKeyword { Name: "right" }: x = 1; break;
                case CssKeyword { Name: "top" }: y = 0; break;
                case CssKeyword { Name: "bottom" }: y = 1; break;
                case CssKeyword { Name: "center" }:
                    if (x is null)
                    {
                        x = 0.5;
                    }
                    else
                    {
                        y = 0.5;
                    }

                    break;
                case CssPercentage pct:
                    if (x is null)
                    {
                        x = pct.Value / 100.0;
                    }
                    else
                    {
                        y = pct.Value / 100.0;
                    }

                    break;
                default:
                    return false;
            }
        }
        position = new CssGradientPosition(x ?? 0.5, y ?? 0.5);
        return true;
    }

    // --- color stops ---

    private static bool TryParseStops(IReadOnlyList<CssValue> args, int start, out IReadOnlyList<CssColorStop> stops)
    {
        var result = new List<CssColorStop>(Math.Max(0, args.Count - start));
        for (var i = start; i < args.Count; i++)
        {
            if (!TryParseStop(args[i], result))
            {
                return Fail(out stops);
            }
        }

        // CSS Images 4 §3.4 — transition hints are not color stops and do not count
        // toward the two-stop minimum; count only real stops.
        var realStopCount = result.Count(s => !s.IsHint);
        if (realStopCount < 2)
        {
            return Fail(out stops);
        }

        stops = result;
        return true;

        static bool Fail(out IReadOnlyList<CssColorStop> s)
        {
            s = [];
            return false;
        }
    }

    private static bool TryParseStop(CssValue arg, List<CssColorStop> into)
    {
        switch (arg)
        {
            case CssColor color:
                into.Add(new CssColorStop(color));
                return true;
            case CssValueList list:
                {
                    CssColor? color = null;
                    CssGradientStopPosition? firstPos = null;
                    CssGradientStopPosition? secondPos = null;
                    foreach (var item in list.Values)
                    {
                        switch (item)
                        {
                            case CssColor c when color is null:
                                color = c;
                                break;
                            case CssLength or CssPercentage or CssAngle when TryStopPosition(item, out var p):
                                if (firstPos is null)
                                {
                                    firstPos = p;
                                }
                                else
                                {
                                    secondPos = p;
                                }

                                break;
                            case CssLength:
                                // Relative-unit position we can't resolve at parse
                                // time — keep the stop but drop the position (auto).
                                break;
                            default:
                                return false;
                        }
                    }
                    if (color is null)
                    {
                        return false;
                    }
                    // CSS Images 4 two-position color-stop shorthand: `red 10% 40%`
                    // expands to two stops with the same color.
                    into.Add(new CssColorStop(color, firstPos));
                    if (secondPos is not null)
                    {
                        into.Add(new CssColorStop(color, secondPos));
                    }

                    return true;
                }
            // CSS Images 4 §3.4 — transition hint: a bare <percentage> or absolute
            // <length> between two color stops. The hint shifts the interpolation
            // midpoint toward or away from one of the surrounding stops.
            case CssPercentage pct:
                {
                    var pos = new CssGradientStopPosition(pct.Value, IsPercent: true);
                    into.Add(CssTransitionHint.Create(pos));
                    return true;
                }
            case CssAngle ang:
                {
                    // Conic angle hints: treat angle as fraction of turn.
                    var pos = new CssGradientStopPosition(ang.InDegrees / 360.0 * 100.0, IsPercent: true);
                    into.Add(CssTransitionHint.Create(pos));
                    return true;
                }
            case CssLength len when len.Unit.IsAbsolute():
                {
                    var pos = new CssGradientStopPosition(len.Unit.AbsoluteToPx(len.Value), IsPercent: false);
                    into.Add(CssTransitionHint.Create(pos));
                    return true;
                }
            default:
                return false;
        }
    }

    private static bool TryStopPosition(CssValue value, out CssGradientStopPosition position)
    {
        switch (value)
        {
            case CssPercentage pct:
                position = new CssGradientStopPosition(pct.Value, IsPercent: true);
                return true;
            case CssAngle ang:
                // Conic color-stop angle: a full turn (360deg) is 100% of the
                // gradient, so store it as the equivalent percentage.
                position = new CssGradientStopPosition(ang.InDegrees / 360.0 * 100.0, IsPercent: true);
                return true;
            case CssLength len when len.Unit.IsAbsolute():
                position = new CssGradientStopPosition(len.Unit.AbsoluteToPx(len.Value), IsPercent: false);
                return true;
            case CssLength:
                // Relative units need a resolution context we don't have here;
                // treat as unset (auto) rather than rejecting the whole gradient.
                position = default;
                return false;
            default:
                position = default;
                return false;
        }
    }

    private static bool LooksLikeColorStop(CssValue value)
        => value switch
        {
            CssColor => true,
            CssValueList list => list.Values.Count > 0 && list.Values[0] is CssColor,
            _ => false,
        };
}
