namespace Starling.Css.Values;

/// <summary>
/// Used-value-time resolution for symbolic <see cref="CssCalc"/> trees and
/// individual <see cref="CssLength"/> values. Converts em/rem/lh/rlh/v*/sv*/lv*/dv*/cq*
/// and percentages into absolute pixel lengths using a
/// <see cref="CssResolutionContext"/>.
/// </summary>
public static class CssCalcResolver
{
    /// <summary>Resolve any <see cref="CssValue"/> into a fully-evaluated form.
    /// Returns the input unchanged when no resolution is needed. When
    /// <see cref="CssResolutionContext.HasPercentageBasis"/> is false,
    /// percentages are preserved symbolically.</summary>
    public static CssValue Resolve(CssValue value, CssResolutionContext ctx)
        => value switch
        {
            CssCalc calc => NodeToValue(ResolveNode(calc.Expression, ctx)),
            CssLength len => new CssLength(ToPx(len.Value, len.Unit, ctx), CssLengthUnit.Px),
            CssPercentage pct => ctx.HasPercentageBasis
                ? new CssLength(pct.Value / 100.0 * ctx.PercentageBasisPx, CssLengthUnit.Px)
                : pct,
            CssValueList list => new CssValueList(list.Values.Select(v => Resolve(v, ctx)).ToList()),
            CssFunctionValue fn => new CssFunctionValue(fn.Name, fn.Arguments.Select(a => Resolve(a, ctx)).ToList()),
            _ => value,
        };

    /// <summary>Resolve a <see cref="CssLength"/> directly to pixels.</summary>
    public static double ResolveToPx(CssLength length, CssResolutionContext ctx)
        => ToPx(length.Value, length.Unit, ctx);

    /// <summary>Walk a calc tree, resolving every relative leaf to its absolute
    /// counterpart and folding constants. Returns the simplified node.</summary>
    public static CalcNode ResolveNode(CalcNode node, CssResolutionContext ctx)
        => node switch
        {
            CalcLength l => new CalcLength(ToPx(l.Value, l.Unit, ctx), CssLengthUnit.Px),
            CalcPercentage p when ctx.HasPercentageBasis
                => new CalcLength(p.Value / 100.0 * ctx.PercentageBasisPx, CssLengthUnit.Px),
            CalcPercentage p => p,
            CalcAngle a => new CalcAngle(a.InDegrees(), CssAngleUnit.Degrees),
            CalcTime t => new CalcTime(t.InSeconds(), CssTimeUnit.Seconds),
            CalcFrequency f => new CalcFrequency(f.InHertz(), CssFrequencyUnit.Hertz),
            CalcResolution r => new CalcResolution(r.InDppx(), CssResolutionUnit.Dppx),
            CalcNegate n => Negate(ResolveNode(n.Operand, ctx)),
            CalcBinary b => Combine(b.Op, ResolveNode(b.Left, ctx), ResolveNode(b.Right, ctx), b.ResultType),
            CalcFunction f => ResolveFunction(f, ctx),
            _ => node,
        };

    private static CalcNode ResolveFunction(CalcFunction f, CssResolutionContext ctx)
    {
        var args = f.Arguments.Select(a => ResolveNode(a, ctx)).ToList();
        var name = f.Name.StartsWith("round-") ? "round" : f.Name;
        var strategy = f.Name.StartsWith("round-") ? f.Name["round-".Length..] : "nearest";

        if (!args.All(IsLiteralOrFolded))
        {
            return f with { Arguments = args };
        }

        return name switch
        {
            "min" => Literal(args.Min(GetValue), PreferredUnit(args), args[0].Type),
            "max" => Literal(args.Max(GetValue), PreferredUnit(args), args[0].Type),
            "clamp" => Literal(Math.Clamp(GetValue(args[1]), GetValue(args[0]), GetValue(args[2])), PreferredUnit(args), args[1].Type),
            "mod" => DoMod(GetValue(args[0]), GetValue(args[1]), PreferredUnit(args), args[0].Type, true),
            "rem" => DoMod(GetValue(args[0]), GetValue(args[1]), PreferredUnit(args), args[0].Type, false),
            "abs" => Literal(Math.Abs(GetValue(args[0])), PreferredUnit(args), args[0].Type),
            "sign" => Literal(Math.Sign(GetValue(args[0])), default, NumericType.Number),
            "round" => DoRound(GetValue(args[0]), GetValue(args[1]), strategy, PreferredUnit(args), args[0].Type),
            _ => f with { Arguments = args },
        };
    }

    private static CalcNode DoMod(double a, double b, UnitKind unit, NumericType type, bool isMod)
    {
        if (b == 0)
        {
            return Literal(double.NaN, unit, type);
        }

        var v = isMod ? a - Math.Floor(a / b) * b : a - Math.Truncate(a / b) * b;
        return Literal(v, unit, type);
    }

    private static CalcNode DoRound(double a, double b, string strategy, UnitKind unit, NumericType type)
    {
        if (b == 0)
        {
            return Literal(double.NaN, unit, type);
        }

        var q = a / b;
        var rounded = strategy switch
        {
            "up" => Math.Ceiling(q),
            "down" => Math.Floor(q),
            "to-zero" => Math.Truncate(q),
            _ => Math.Round(q, MidpointRounding.ToEven),
        };
        return Literal(rounded * b, unit, type);
    }

    private static CalcNode Combine(CalcOperator op, CalcNode left, CalcNode right, NumericType resultType)
    {
        if (left is CalcNumber ln && right is CalcNumber rn)
        {
            return op switch
            {
                CalcOperator.Add => new CalcNumber(ln.Value + rn.Value),
                CalcOperator.Subtract => new CalcNumber(ln.Value - rn.Value),
                CalcOperator.Multiply => new CalcNumber(ln.Value * rn.Value),
                CalcOperator.Divide => new CalcNumber(rn.Value == 0 ? double.NaN : ln.Value / rn.Value),
                _ => new CalcBinary(op, left, right, resultType),
            };
        }
        if (op is CalcOperator.Multiply or CalcOperator.Divide)
        {
            if (right is CalcNumber n2)
            {
                return Scale(left, op == CalcOperator.Multiply ? n2.Value : (n2.Value == 0 ? double.NaN : 1.0 / n2.Value));
            }

            if (left is CalcNumber n1 && op == CalcOperator.Multiply)
            {
                return Scale(right, n1.Value);
            }
        }
        if (op is CalcOperator.Add or CalcOperator.Subtract && left is CalcLength lL && right is CalcLength rL)
        {
            var sum = lL.Value + (op == CalcOperator.Add ? 1 : -1) * rL.Value;
            return new CalcLength(sum, CssLengthUnit.Px);
        }
        if (op is CalcOperator.Add or CalcOperator.Subtract && left is CalcPercentage lP && right is CalcPercentage rP)
        {
            var sum = lP.Value + (op == CalcOperator.Add ? 1 : -1) * rP.Value;
            return new CalcPercentage(sum);
        }
        if (op is CalcOperator.Add or CalcOperator.Subtract && left is CalcAngle lA && right is CalcAngle rA)
        {
            var sum = lA.Value + (op == CalcOperator.Add ? 1 : -1) * rA.Value;
            return new CalcAngle(sum, CssAngleUnit.Degrees);
        }
        return new CalcBinary(op, left, right, resultType);
    }

    private static CalcNode Negate(CalcNode op)
        => op switch
        {
            CalcNumber n => new CalcNumber(-n.Value),
            CalcLength l => new CalcLength(-l.Value, l.Unit),
            CalcPercentage p => new CalcPercentage(-p.Value),
            CalcAngle a => new CalcAngle(-a.Value, a.Unit),
            CalcTime t => new CalcTime(-t.Value, t.Unit),
            CalcFrequency f => new CalcFrequency(-f.Value, f.Unit),
            CalcResolution r => new CalcResolution(-r.Value, r.Unit),
            _ => new CalcNegate(op),
        };

    private static CalcNode Scale(CalcNode n, double s)
        => n switch
        {
            CalcNumber x => new CalcNumber(x.Value * s),
            CalcLength x => new CalcLength(x.Value * s, x.Unit),
            CalcPercentage x => new CalcPercentage(x.Value * s),
            CalcAngle x => new CalcAngle(x.Value * s, x.Unit),
            CalcTime x => new CalcTime(x.Value * s, x.Unit),
            CalcFrequency x => new CalcFrequency(x.Value * s, x.Unit),
            CalcResolution x => new CalcResolution(x.Value * s, x.Unit),
            _ => new CalcBinary(CalcOperator.Multiply, n, new CalcNumber(s), n.Type),
        };

    private static CssValue NodeToValue(CalcNode node)
        => node switch
        {
            CalcNumber n => new CssNumber(n.Value),
            CalcLength l => new CssLength(l.Value, l.Unit),
            CalcPercentage p => new CssPercentage(p.Value),
            CalcAngle a => new CssAngle(a.Value, a.Unit),
            CalcTime t => new CssTime(t.Value, t.Unit),
            CalcFrequency f => new CssFrequency(f.Value, f.Unit),
            CalcResolution r => new CssResolution(r.Value, r.Unit),
            _ => new CssCalc(node),
        };

    private static double ToPx(double value, CssLengthUnit unit, CssResolutionContext ctx)
        => unit switch
        {
            CssLengthUnit.Px => value,
            CssLengthUnit.Pt => value * 4d / 3d,
            CssLengthUnit.Pc => value * 16d,
            CssLengthUnit.In => value * 96d,
            CssLengthUnit.Cm => value * 96d / 2.54d,
            CssLengthUnit.Mm => value * 96d / 25.4d,
            CssLengthUnit.Q => value * 96d / 101.6d,
            CssLengthUnit.Em => value * ctx.FontSizePx,
            CssLengthUnit.Rem => value * ctx.RootFontSizePx,
            CssLengthUnit.Ex => value * ctx.XHeightPx,
            CssLengthUnit.Cap => value * ctx.CapHeightPx,
            CssLengthUnit.Ch => value * ctx.ZeroAdvancePx,
            CssLengthUnit.Ic => value * ctx.IcAdvancePx,
            CssLengthUnit.Lh => value * ctx.LineHeightPx,
            CssLengthUnit.Rlh => value * ctx.RootLineHeightPx,
            CssLengthUnit.Vw => value * ctx.ViewportWidthPx / 100.0,
            CssLengthUnit.Vh => value * ctx.ViewportHeightPx / 100.0,
            CssLengthUnit.Vmin => value * Math.Min(ctx.ViewportWidthPx, ctx.ViewportHeightPx) / 100.0,
            CssLengthUnit.Vmax => value * Math.Max(ctx.ViewportWidthPx, ctx.ViewportHeightPx) / 100.0,
            CssLengthUnit.Vi => value * ctx.ViewportInlinePx / 100.0,
            CssLengthUnit.Vb => value * ctx.ViewportBlockPx / 100.0,
            CssLengthUnit.Svw => value * ctx.SmallViewportWidthPx / 100.0,
            CssLengthUnit.Svh => value * ctx.SmallViewportHeightPx / 100.0,
            CssLengthUnit.Svmin => value * Math.Min(ctx.SmallViewportWidthPx, ctx.SmallViewportHeightPx) / 100.0,
            CssLengthUnit.Svmax => value * Math.Max(ctx.SmallViewportWidthPx, ctx.SmallViewportHeightPx) / 100.0,
            CssLengthUnit.Lvw => value * ctx.LargeViewportWidthPx / 100.0,
            CssLengthUnit.Lvh => value * ctx.LargeViewportHeightPx / 100.0,
            CssLengthUnit.Lvmin => value * Math.Min(ctx.LargeViewportWidthPx, ctx.LargeViewportHeightPx) / 100.0,
            CssLengthUnit.Lvmax => value * Math.Max(ctx.LargeViewportWidthPx, ctx.LargeViewportHeightPx) / 100.0,
            CssLengthUnit.Dvw => value * ctx.DynamicViewportWidthPx / 100.0,
            CssLengthUnit.Dvh => value * ctx.DynamicViewportHeightPx / 100.0,
            CssLengthUnit.Dvmin => value * Math.Min(ctx.DynamicViewportWidthPx, ctx.DynamicViewportHeightPx) / 100.0,
            CssLengthUnit.Dvmax => value * Math.Max(ctx.DynamicViewportWidthPx, ctx.DynamicViewportHeightPx) / 100.0,
            CssLengthUnit.Cqw => value * ctx.ContainerWidthPx / 100.0,
            CssLengthUnit.Cqh => value * ctx.ContainerHeightPx / 100.0,
            CssLengthUnit.Cqi => value * ctx.ContainerInlinePx / 100.0,
            CssLengthUnit.Cqb => value * ctx.ContainerBlockPx / 100.0,
            CssLengthUnit.Cqmin => value * Math.Min(ctx.ContainerWidthPx, ctx.ContainerHeightPx) / 100.0,
            CssLengthUnit.Cqmax => value * Math.Max(ctx.ContainerWidthPx, ctx.ContainerHeightPx) / 100.0,
            _ => value,
        };

    private static bool IsLiteralOrFolded(CalcNode n) => n is CalcNumber or CalcLength or CalcPercentage
        or CalcAngle or CalcTime or CalcFrequency or CalcResolution;

    private static double GetValue(CalcNode n) => n switch
    {
        CalcNumber x => x.Value,
        CalcLength x => x.Value,
        CalcPercentage x => x.Value,
        CalcAngle x => x.InDegrees(),
        CalcTime x => x.InSeconds(),
        CalcFrequency x => x.InHertz(),
        CalcResolution x => x.InDppx(),
        _ => 0,
    };

    private enum UnitKind { Number, LengthPx, Percentage, AngleDeg, TimeSec, FreqHz, ResDppx }

    private static UnitKind PreferredUnit(IReadOnlyList<CalcNode> args)
    {
        foreach (var a in args)
        {
            switch (a)
            {
                case CalcLength: return UnitKind.LengthPx;
                case CalcAngle: return UnitKind.AngleDeg;
                case CalcTime: return UnitKind.TimeSec;
                case CalcFrequency: return UnitKind.FreqHz;
                case CalcResolution: return UnitKind.ResDppx;
                case CalcPercentage: return UnitKind.Percentage;
            }
        }

        return UnitKind.Number;
    }

    private static CalcNode Literal(double v, UnitKind unit, NumericType _) => unit switch
    {
        UnitKind.LengthPx => new CalcLength(v, CssLengthUnit.Px),
        UnitKind.AngleDeg => new CalcAngle(v, CssAngleUnit.Degrees),
        UnitKind.TimeSec => new CalcTime(v, CssTimeUnit.Seconds),
        UnitKind.FreqHz => new CalcFrequency(v, CssFrequencyUnit.Hertz),
        UnitKind.ResDppx => new CalcResolution(v, CssResolutionUnit.Dppx),
        UnitKind.Percentage => new CalcPercentage(v),
        _ => new CalcNumber(v),
    };
}

internal static class CalcResolverExtensions
{
    public static double InSeconds(this CalcTime t) => t.Unit == CssTimeUnit.Seconds ? t.Value : t.Value / 1000.0;

    public static double InHertz(this CalcFrequency f) => f.Unit == CssFrequencyUnit.Hertz ? f.Value : f.Value * 1000.0;

    public static double InDppx(this CalcResolution r) => r.Unit switch
    {
        CssResolutionUnit.Dppx => r.Value,
        CssResolutionUnit.Dpi => r.Value / 96.0,
        CssResolutionUnit.Dpcm => r.Value * 2.54 / 96.0,
        _ => r.Value,
    };
}
