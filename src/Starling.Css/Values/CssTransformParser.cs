namespace Starling.Css.Values;

/// <summary>
/// Parses the <c>transform</c> property value into a strongly-typed
/// <see cref="CssTransform"/>. Input is the generic <see cref="CssValue"/>
/// produced by <see cref="CssValueParser"/>: either the <c>none</c> keyword,
/// a single <see cref="CssFunctionValue"/>, or a <see cref="CssValueList"/>
/// of function values.
/// <para>
/// Fail-soft: any unrecognised function, malformed argument, or out-of-range
/// 3D variant causes the whole declaration to resolve to <see cref="CssTransform.None"/>,
/// matching CSS Transforms 1 §6.1 (invalid grammar → property unset).
/// </para>
/// </summary>
public static class CssTransformParser
{
    public static CssTransform Parse(CssValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Idempotent: an already-parsed transform (e.g. the output of animation
        // interpolation, which the cascade stores back as the property value)
        // passes straight through. Without this, re-parsing it would fall through
        // the function-value path below and yield None — dropping the transform.
        if (value is CssTransform already)
        {
            return already;
        }

        if (value is CssKeyword { Name: var kw } && kw.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return CssTransform.None;
        }

        var functions = value switch
        {
            CssValueList list => list.Values,
            _ => new[] { value },
        };

        var result = new List<CssTransformFunction>(functions.Count);
        foreach (var item in functions)
        {
            if (item is not CssFunctionValue fn)
            {
                return CssTransform.None;
            }

            if (!TryParseFunction(fn, out var transformFn))
            {
                return CssTransform.None;
            }

            result.Add(transformFn);
        }
        return new CssTransform(result);
    }

    public static bool TryParseFunction(CssFunctionValue fn, out CssTransformFunction result)
    {
        result = null!;
        var args = fn.Arguments;
        switch (fn.Name.ToLowerInvariant())
        {
            case "translate":
                if (args.Count is < 1 or > 2)
                {
                    return false;
                }

                if (!TryLengthOrPercent(args[0], out var tx))
                {
                    return false;
                }

                var ty = new CssLengthOrPercent(0, false);
                if (args.Count == 2 && !TryLengthOrPercent(args[1], out ty))
                {
                    return false;
                }

                result = new CssTranslate(tx, ty);
                return true;
            case "translatex":
                if (args.Count != 1 || !TryLengthOrPercent(args[0], out var tlx))
                {
                    return false;
                }

                result = new CssTranslate(tlx, new CssLengthOrPercent(0, false));
                return true;
            case "translatey":
                if (args.Count != 1 || !TryLengthOrPercent(args[0], out var tly))
                {
                    return false;
                }

                result = new CssTranslate(new CssLengthOrPercent(0, false), tly);
                return true;
            case "scale":
                if (args.Count is < 1 or > 2)
                {
                    return false;
                }

                if (!TryNumber(args[0], out var sx))
                {
                    return false;
                }

                var sy = sx;
                if (args.Count == 2 && !TryNumber(args[1], out sy))
                {
                    return false;
                }

                result = new CssScale(sx, sy);
                return true;
            case "scalex":
                if (args.Count != 1 || !TryNumber(args[0], out var sxOnly))
                {
                    return false;
                }

                result = new CssScale(sxOnly, 1);
                return true;
            case "scaley":
                if (args.Count != 1 || !TryNumber(args[0], out var syOnly))
                {
                    return false;
                }

                result = new CssScale(1, syOnly);
                return true;
            case "rotate":
            case "rotatez":
                if (args.Count != 1 || !TryAngleRadians(args[0], out var rad))
                {
                    return false;
                }

                result = new CssRotate(rad);
                return true;
            case "skew":
                if (args.Count is < 1 or > 2)
                {
                    return false;
                }

                if (!TryAngleRadians(args[0], out var ax))
                {
                    return false;
                }

                var ay = 0d;
                if (args.Count == 2 && !TryAngleRadians(args[1], out ay))
                {
                    return false;
                }

                result = new CssSkew(ax, ay);
                return true;
            case "skewx":
                if (args.Count != 1 || !TryAngleRadians(args[0], out var skx))
                {
                    return false;
                }

                result = new CssSkew(skx, 0);
                return true;
            case "skewy":
                if (args.Count != 1 || !TryAngleRadians(args[0], out var sky))
                {
                    return false;
                }

                result = new CssSkew(0, sky);
                return true;
            case "matrix":
                if (args.Count != 6)
                {
                    return false;
                }

                Span<double> mvals = stackalloc double[6];
                for (var i = 0; i < 6; i++)
                {
                    if (!TryNumber(args[i], out var v))
                    {
                        return false;
                    }

                    mvals[i] = v;
                }
                result = new CssMatrix(new Matrix2D(mvals[0], mvals[1], mvals[2], mvals[3], mvals[4], mvals[5]));
                return true;
            // 3D variants are explicitly rejected here — they need 4x4 math + a stacking flatten.
            // Listed so the catch-all `default` is reserved for truly unknown names.
            case "translate3d":
            case "translatez":
            case "scale3d":
            case "scalez":
            case "rotate3d":
            case "rotatex":
            case "rotatey":
            case "matrix3d":
            case "perspective":
                return false;
            default:
                return false;
        }
    }

    private static bool TryNumber(CssValue value, out double number)
    {
        switch (value)
        {
            case CssNumber n: number = n.Value; return true;
            case CssPercentage p: number = p.Value / 100d; return true;
            default: number = 0; return false;
        }
    }

    private static bool TryLengthOrPercent(CssValue value, out CssLengthOrPercent result)
    {
        switch (value)
        {
            case CssLength len:
                if (!len.Unit.IsAbsolute())
                {
                    // Relative units (em/rem/vh/...) require a resolution context we don't
                    // have at parse time; conservatively treat them as 0 for now and let a
                    // future caller resolve. Returning true keeps the rest of the function list valid.
                    result = new CssLengthOrPercent(0, false);
                    return true;
                }
                result = new CssLengthOrPercent(len.Unit.AbsoluteToPx(len.Value), false);
                return true;
            case CssPercentage pct:
                result = new CssLengthOrPercent(pct.Value, true);
                return true;
            case CssNumber n when n.Value == 0:
                result = new CssLengthOrPercent(0, false);
                return true;
            default:
                result = default;
                return false;
        }
    }

    private static bool TryAngleRadians(CssValue value, out double radians)
    {
        switch (value)
        {
            case CssAngle angle: radians = angle.InRadians; return true;
            case CssNumber n when n.Value == 0: radians = 0; return true;
            default: radians = 0; return false;
        }
    }
}
