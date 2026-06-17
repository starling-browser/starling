using Starling.Css.Values;

namespace Starling.Css.Animations;

/// <summary>
/// CSS Easing 1 timing functions. <see cref="Evaluate"/> maps a linear
/// progress value <c>t ∈ [0, 1]</c> to an eased progress, which the
/// transition / animation engine then feeds back into per-property
/// interpolation. The five named curves (`ease`, `linear`,
/// `ease-in`/`out`/`in-out`) plus `cubic-bezier(x1, y1, x2, y2)` and
/// `steps(n, jumpterm)` are supported.
/// </summary>
public abstract record TimingFunction
{
    public static TimingFunction Linear { get; } = new LinearTimingFunction();

    // Spec defaults: CSS Easing 1 §3.1 — keyword to cubic-bezier mapping.
    public static TimingFunction Ease { get; } = new CubicBezierTimingFunction(0.25, 0.1, 0.25, 1.0);
    public static TimingFunction EaseIn { get; } = new CubicBezierTimingFunction(0.42, 0.0, 1.0, 1.0);
    public static TimingFunction EaseOut { get; } = new CubicBezierTimingFunction(0.0, 0.0, 0.58, 1.0);
    public static TimingFunction EaseInOut { get; } = new CubicBezierTimingFunction(0.42, 0.0, 0.58, 1.0);

    /// <summary>Evaluate eased progress for linear input <paramref name="t"/> in [0, 1].</summary>
    public abstract double Evaluate(double t);

    /// <summary>
    /// Parse a <see cref="CssValue"/> in the position of
    /// <c>transition-timing-function</c>. Returns <see cref="Ease"/> if the
    /// value cannot be interpreted — matches the spec's "compute the initial
    /// value" fallback for invalid declarations.
    /// </summary>
    public static TimingFunction FromCss(CssValue? value)
    {
        switch (value)
        {
            case CssKeyword k:
                return k.Name.ToLowerInvariant() switch
                {
                    "linear" => Linear,
                    "ease" => Ease,
                    "ease-in" => EaseIn,
                    "ease-out" => EaseOut,
                    "ease-in-out" => EaseInOut,
                    // step-start / step-end are shorthand for steps(1, jump-start|jump-end)
                    "step-start" => new StepsTimingFunction(1, StepPosition.JumpStart),
                    "step-end" => new StepsTimingFunction(1, StepPosition.JumpEnd),
                    _ => Ease,
                };

            case CssFunctionValue f when string.Equals(f.Name, "cubic-bezier", StringComparison.OrdinalIgnoreCase):
                if (f.Arguments.Count == 4
                    && TryAsNumber(f.Arguments[0], out var x1)
                    && TryAsNumber(f.Arguments[1], out var y1)
                    && TryAsNumber(f.Arguments[2], out var x2)
                    && TryAsNumber(f.Arguments[3], out var y2))
                {
                    // Spec §2.1: x1 and x2 must be in [0, 1] (else the curve
                    // is not a function); clamp rather than fail-hard so a
                    // typo in a stylesheet still animates.
                    x1 = Math.Clamp(x1, 0.0, 1.0);
                    x2 = Math.Clamp(x2, 0.0, 1.0);
                    return new CubicBezierTimingFunction(x1, y1, x2, y2);
                }
                return Ease;

            case CssFunctionValue f when string.Equals(f.Name, "steps", StringComparison.OrdinalIgnoreCase):
                if (f.Arguments.Count >= 1 && TryAsNumber(f.Arguments[0], out var n))
                {
                    var count = Math.Max(1, (int)n);
                    var pos = StepPosition.JumpEnd;
                    if (f.Arguments.Count >= 2 && f.Arguments[1] is CssKeyword posKw)
                    {
                        pos = posKw.Name.ToLowerInvariant() switch
                        {
                            "jump-start" or "start" => StepPosition.JumpStart,
                            "jump-end" or "end" => StepPosition.JumpEnd,
                            "jump-none" => StepPosition.JumpNone,
                            "jump-both" => StepPosition.JumpBoth,
                            _ => StepPosition.JumpEnd,
                        };
                    }
                    return new StepsTimingFunction(count, pos);
                }
                return Ease;

            case CssFunctionValue f when string.Equals(f.Name, "linear", StringComparison.OrdinalIgnoreCase):
                {
                    var points = ParseLinearStops(f.Arguments);
                    return points is { Count: >= 2 } ? new LinearEasingFunction(points) : Linear;
                }
        }
        return Ease;
    }

    // CSS Easing 1 §4: parse `linear()` stops into (input, output) control
    // points. Each argument is an output number, optionally followed by one or
    // two input-position percentages. Missing first/last inputs default to
    // 0/1; missing interior inputs are evenly distributed; inputs are made
    // monotonically non-decreasing.
    private static List<(double Input, double Output)>? ParseLinearStops(IReadOnlyList<CssValue> args)
    {
        var outputs = new List<double>();
        var inputs = new List<double?>();
        foreach (var arg in args)
        {
            switch (arg)
            {
                case CssNumber num:
                    outputs.Add(num.Value);
                    inputs.Add(null);
                    break;
                case CssValueList list when list.Values.Count >= 1 && list.Values[0] is CssNumber outNum:
                    // A stop with two input positions expands to two control points.
                    var positions = list.Values.Skip(1).OfType<CssPercentage>().Select(p => p.Value / 100.0).ToList();
                    if (positions.Count == 0)
                    {
                        outputs.Add(outNum.Value);
                        inputs.Add(null);
                    }
                    else
                    {
                        foreach (var pos in positions)
                        {
                            outputs.Add(outNum.Value);
                            inputs.Add(pos);
                        }
                    }
                    break;
                default:
                    return null; // malformed stop
            }
        }

        var n = outputs.Count;
        if (n == 0)
        {
            return null;
        }

        if (inputs[0] is null)
        {
            inputs[0] = 0.0;
        }

        if (inputs[n - 1] is null)
        {
            inputs[n - 1] = 1.0;
        }

        // Evenly distribute runs of missing interior inputs between defined anchors.
        var i = 0;
        while (i < n)
        {
            if (inputs[i] is not null) { i++; continue; }
            var j = i;
            while (j < n && inputs[j] is null)
            {
                j++;
            }

            var lo = inputs[i - 1]!.Value;
            var hi = inputs[j]!.Value;
            for (var k = i; k < j; k++)
            {
                inputs[k] = lo + (hi - lo) * (k - (i - 1)) / (j - (i - 1));
            }

            i = j;
        }
        // Monotonic non-decreasing (§4 step 3).
        for (var k = 1; k < n; k++)
        {
            if (inputs[k]!.Value < inputs[k - 1]!.Value)
            {
                inputs[k] = inputs[k - 1];
            }
        }

        var points = new List<(double Input, double Output)>(n);
        for (var k = 0; k < n; k++)
        {
            points.Add((inputs[k]!.Value, outputs[k]));
        }

        return points;
    }

    private static bool TryAsNumber(CssValue v, out double n)
    {
        switch (v)
        {
            case CssNumber num: n = num.Value; return true;
            case CssPercentage pct: n = pct.Value / 100.0; return true;
            default: n = 0; return false;
        }
    }
}

internal sealed record LinearTimingFunction : TimingFunction
{
    public override double Evaluate(double t) => t;
}

/// <summary>
/// CSS Easing 1 §4 <c>linear()</c> — a piecewise-linear easing defined by
/// (input, output) control points. <see cref="Evaluate"/> finds the segment
/// bracketing the input progress and linearly interpolates the output (a
/// zero-width input segment is a discontinuity that jumps to the later output).
/// </summary>
public sealed record LinearEasingFunction : TimingFunction
{
    private readonly IReadOnlyList<(double Input, double Output)> _points;

    public LinearEasingFunction(IReadOnlyList<(double Input, double Output)> points) => _points = points;

    public override double Evaluate(double t)
    {
        var count = _points.Count;
        if (count == 0)
        {
            return t;
        }

        if (count == 1)
        {
            return _points[0].Output;
        }

        if (t <= _points[0].Input)
        {
            return _points[0].Output;
        }

        if (t >= _points[count - 1].Input)
        {
            return _points[count - 1].Output;
        }

        for (var k = 0; k < count - 1; k++)
        {
            var (inK, outK) = _points[k];
            var (inNext, outNext) = _points[k + 1];
            if (t > inNext)
            {
                continue;
            }

            if (inNext <= inK)
            {
                return outNext; // zero-width segment → jump
            }

            var frac = (t - inK) / (inNext - inK);
            return outK + frac * (outNext - outK);
        }
        return _points[count - 1].Output;
    }

    // Records compare by value; compare the control points element-wise.
    public bool Equals(LinearEasingFunction? other)
        => other is not null && _points.SequenceEqual(other._points);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var p in _points)
        {
            hash.Add(p);
        }

        return hash.ToHashCode();
    }
}

/// <summary>
/// CSS Easing 1 §2 cubic-bezier. The parametric curve <c>B(s)</c> traces
/// <c>(x(s), y(s))</c> with control points <c>(0,0), (x1,y1), (x2,y2), (1,1)</c>.
/// We need <c>y</c> as a function of <c>x</c>; solve <c>x(s) = t</c> for
/// parameter <c>s</c> by Newton-Raphson with a bisection fallback when the
/// derivative is too small (matches Blink/WebKit's <c>UnitBezier</c>).
/// </summary>
public sealed record CubicBezierTimingFunction(double X1, double Y1, double X2, double Y2) : TimingFunction
{
    private const double NewtonEpsilon = 1e-6;
    private const int NewtonIterations = 8;

    public override double Evaluate(double t)
    {
        if (t <= 0)
        {
            return 0;
        }

        if (t >= 1)
        {
            return 1;
        }

        var s = SolveCurveX(t);
        return SampleCurveY(s);
    }

    private double SolveCurveX(double x)
    {
        // Newton-Raphson — converges in 2-4 iterations for typical bezier
        // shapes used by `ease-*`. We allow up to 8; if it diverges (zero
        // derivative or numerical noise) we fall through to bisection.
        var s = x;
        for (var i = 0; i < NewtonIterations; i++)
        {
            var fx = SampleCurveX(s) - x;
            if (Math.Abs(fx) < NewtonEpsilon)
            {
                return s;
            }

            var dx = SampleCurveDerivativeX(s);
            if (Math.Abs(dx) < NewtonEpsilon)
            {
                break;
            }

            s -= fx / dx;
        }

        // Fallback: bisect over [0, 1]. Guaranteed to converge since x(s)
        // is monotonically increasing on [0, 1] for valid bezier curves.
        double lo = 0, hi = 1;
        s = x;
        while (lo < hi)
        {
            var fx = SampleCurveX(s);
            if (Math.Abs(fx - x) < NewtonEpsilon)
            {
                return s;
            }

            if (x > fx)
            {
                lo = s;
            }
            else
            {
                hi = s;
            }

            s = (lo + hi) * 0.5;
            if (hi - lo < NewtonEpsilon)
            {
                break;
            }
        }
        return s;
    }

    // For the cubic Bezier with endpoints (0,0) and (1,1), the polynomial
    // coefficients simplify to: cx = 3*X1, bx = 3*(X2-X1) - cx, ax = 1 - cx - bx.
    // Returning B(s) = ((ax*s + bx)*s + cx)*s is Horner's form.
    private double SampleCurveX(double s)
    {
        var cx = 3 * X1;
        var bx = 3 * (X2 - X1) - cx;
        var ax = 1 - cx - bx;
        return ((ax * s + bx) * s + cx) * s;
    }

    private double SampleCurveY(double s)
    {
        var cy = 3 * Y1;
        var by = 3 * (Y2 - Y1) - cy;
        var ay = 1 - cy - by;
        return ((ay * s + by) * s + cy) * s;
    }

    private double SampleCurveDerivativeX(double s)
    {
        var cx = 3 * X1;
        var bx = 3 * (X2 - X1) - cx;
        var ax = 1 - cx - bx;
        return (3 * ax * s + 2 * bx) * s + cx;
    }
}

/// <summary>
/// CSS Easing 1 §3.3 step-position values. Names match the spec; the legacy
/// keywords <c>start</c>/<c>end</c> are aliased to <c>JumpStart</c>/<c>JumpEnd</c>
/// at parse time.
/// </summary>
public enum StepPosition { JumpStart, JumpEnd, JumpNone, JumpBoth }

/// <summary>
/// <c>steps(n, position)</c> — quantises progress to <paramref name="StepCount"/>
/// discrete output levels per the spec table in §3.3. <c>jump-start</c>
/// outputs 1/n at t=0; <c>jump-end</c> outputs 0; <c>jump-none</c> only emits
/// n-1 levels with the final level reached just before t=1; <c>jump-both</c>
/// emits n+1 levels including both 0 and 1.
/// </summary>
public sealed record StepsTimingFunction(int StepCount, StepPosition Position) : TimingFunction
{
    public override double Evaluate(double t)
    {
        // Clamp first; the spec defines values outside [0, 1] to clip to the
        // endpoint output (no extrapolation beyond steps).
        t = Math.Clamp(t, 0.0, 1.0);
        var n = Math.Max(1, StepCount);

        var step = (int)Math.Floor(t * n);
        // jump-start shifts every interval up by one — at t=0 the output is
        // already 1/n. jump-both shifts up by one AND uses n+1 levels.
        if (Position is StepPosition.JumpStart or StepPosition.JumpBoth)
        {
            step++;
        }
        // Edge case at t=1 with jump-end: floor(1.0 * n) == n which would
        // produce step/n == 1.0 — that's actually correct. With jump-none we
        // need to cap at (n-1)/(n-1) == 1.0 (divisor is n-1).
        var divisor = Position == StepPosition.JumpBoth ? n + 1
            : Position == StepPosition.JumpNone ? Math.Max(1, n - 1)
            : n;
        if (Position == StepPosition.JumpNone && t >= 1.0)
        {
            step = divisor;
        }

        if (step < 0)
        {
            step = 0;
        }

        if (step > divisor)
        {
            step = divisor;
        }

        return (double)step / divisor;
    }
}
