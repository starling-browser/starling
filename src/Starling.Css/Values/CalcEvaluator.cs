using Starling.Css.Parser;
using Starling.Css.Tokenizer;

namespace Starling.Css.Values;

/// <summary>
/// Parser + evaluator for CSS Values 4 §10 math functions:
/// <c>calc()</c>, <c>min()</c>, <c>max()</c>, <c>clamp()</c>, <c>round()</c>,
/// <c>mod()</c>, <c>rem()</c>, trig (<c>sin/cos/tan/asin/acos/atan/atan2</c>),
/// exponential (<c>pow/sqrt/hypot/log/exp</c>), and sign-related
/// (<c>abs/sign</c>).
/// </summary>
/// <remarks>
/// Strategy: build a symbolic <see cref="CalcNode"/> tree, then attempt a
/// best-effort reduction. If every leaf is a <see cref="CalcNumber"/> or
/// uses an absolute length unit, we collapse to a single literal at parse
/// time. Anything involving font-relative, viewport, container or
/// percentage units is preserved as a tree to be resolved at used-value time
/// by the style engine.
/// </remarks>
public static class CalcEvaluator
{
    /// <summary>Parse a calc()/min()/max()/etc. function call into a CssValue.
    /// Returns a literal <see cref="CssLength"/>/<see cref="CssNumber"/>/etc.
    /// when fully reducible, otherwise a <see cref="CssCalc"/> wrapping the tree.</summary>
    public static CssValue ParseFunction(string name, IReadOnlyList<CssComponentValue> values)
    {
        var node = ParseFunctionNode(name, values);
        return ToCssValue(node);
    }

    public static CalcNode ParseFunctionNode(string name, IReadOnlyList<CssComponentValue> values)
    {
        var lower = name.ToLowerInvariant();
        var args = SplitArguments(values).Select(arg => ParseExpression(arg)).ToList();
        return lower switch
        {
            "calc" => args.Count == 1 ? Reduce(args[0]) : new CalcFunction("calc", args, args[0].Type),
            "min" or "max" => MinMax(lower, args),
            "clamp" => ClampNode(args),
            "round" => RoundNode(values),
            "mod" or "rem" => ModRem(lower, args),
            "abs" or "sign" => UnaryFunc(lower, args),
            "sin" or "cos" or "tan" or "asin" or "acos" or "atan" or "atan2"
                or "sqrt" or "exp" or "log" or "pow" or "hypot" => MathFunc(lower, args),
            _ => new CalcFunction(lower, args, args.Count > 0 ? args[0].Type : NumericType.Unknown),
        };
    }

    /// <summary>Convert a calc tree into the most specific CssValue we can.</summary>
    public static CssValue ToCssValue(CalcNode node)
    {
        var reduced = Reduce(node);
        return reduced switch
        {
            CalcNumber n => new CssNumber(n.Value),
            CalcLength l => new CssLength(l.Value, l.Unit),
            CalcPercentage p => new CssPercentage(p.Value),
            CalcAngle a => new CssAngle(a.Value, a.Unit),
            CalcTime t => new CssTime(t.Value, t.Unit),
            CalcFrequency f => new CssFrequency(f.Value, f.Unit),
            CalcResolution r => new CssResolution(r.Value, r.Unit),
            _ => new CssCalc(reduced),
        };
    }

    private static CalcNode MinMax(string name, List<CalcNode> args)
    {
        if (args.Count == 0)
        {
            throw new FormatException($"{name}() requires at least one argument");
        }

        if (args.All(IsAbsoluteLiteral))
        {
            var values = args.Select(GetAbsoluteValue).ToArray();
            var unit = GetPreferredUnit(args);
            var result = name == "min" ? values.Min() : values.Max();
            return MakeLiteral(result, unit, args[0].Type);
        }
        return new CalcFunction(name, args, args[0].Type);
    }

    private static CalcNode ClampNode(List<CalcNode> args)
    {
        if (args.Count != 3)
        {
            throw new FormatException("clamp() requires 3 arguments");
        }

        if (args.All(IsAbsoluteLiteral))
        {
            var lo = GetAbsoluteValue(args[0]);
            var v = GetAbsoluteValue(args[1]);
            var hi = GetAbsoluteValue(args[2]);
            var clamped = Math.Min(Math.Max(v, lo), hi);
            return MakeLiteral(clamped, GetPreferredUnit(args), args[1].Type);
        }
        return new CalcFunction("clamp", args, args[1].Type);
    }

    private static CalcNode RoundNode(IReadOnlyList<CssComponentValue> values)
    {
        // round() may take an optional first ident argument for strategy.
        var rawArgs = SplitArguments(values).ToList();
        var strategy = "nearest";
        if (rawArgs.Count > 0 && rawArgs[0].Count > 0 &&
            rawArgs[0][0] is CssTokenValue tv && tv.Token.Type == CssTokenType.Ident &&
            tv.Token.Value is "nearest" or "up" or "down" or "to-zero")
        {
            strategy = tv.Token.Value;
            rawArgs.RemoveAt(0);
        }

        var args = rawArgs.Select(arg => ParseExpression(arg)).ToList();
        if (args.Count != 2)
        {
            throw new FormatException("round() requires 2 numeric arguments");
        }

        if (args.All(IsAbsoluteLiteral))
        {
            var a = GetAbsoluteValue(args[0]);
            var b = GetAbsoluteValue(args[1]);
            if (b == 0)
            {
                return MakeLiteral(double.NaN, GetPreferredUnit(args), args[0].Type);
            }

            var q = a / b;
            var rounded = strategy switch
            {
                "up" => Math.Ceiling(q),
                "down" => Math.Floor(q),
                "to-zero" => Math.Truncate(q),
                _ => Math.Round(q, MidpointRounding.ToEven),
            };
            return MakeLiteral(rounded * b, GetPreferredUnit(args), args[0].Type);
        }
        return new CalcFunction("round-" + strategy, args, args[0].Type);
    }

    private static CalcNode ModRem(string name, List<CalcNode> args)
    {
        if (args.Count != 2)
        {
            throw new FormatException(name + "() requires 2 arguments");
        }

        if (args.All(IsAbsoluteLiteral))
        {
            var a = GetAbsoluteValue(args[0]);
            var b = GetAbsoluteValue(args[1]);
            if (b == 0)
            {
                return MakeLiteral(double.NaN, GetPreferredUnit(args), args[0].Type);
            }

            double v;
            if (name == "mod")
            {
                v = a - Math.Floor(a / b) * b; // mathematical mod (sign of B)
            }
            else
            {
                v = a - Math.Truncate(a / b) * b; // rem (sign of A)
            }
            return MakeLiteral(v, GetPreferredUnit(args), args[0].Type);
        }
        return new CalcFunction(name, args, args[0].Type);
    }

    private static CalcNode UnaryFunc(string name, List<CalcNode> args)
    {
        if (args.Count != 1)
        {
            throw new FormatException(name + "() requires 1 argument");
        }

        if (args[0] is CalcNumber n)
        {
            return name switch
            {
                "abs" => new CalcNumber(Math.Abs(n.Value)),
                "sign" => new CalcNumber(Math.Sign(n.Value)),
                _ => new CalcFunction(name, args, args[0].Type),
            };
        }
        if (IsAbsoluteLiteral(args[0]))
        {
            var v = GetAbsoluteValue(args[0]);
            var rv = name == "abs" ? Math.Abs(v) : Math.Sign(v);
            return MakeLiteral(rv, GetPreferredUnit(args), args[0].Type);
        }
        return new CalcFunction(name, args, args[0].Type);
    }

    private static CalcNode MathFunc(string name, List<CalcNode> args)
    {
        if (args.All(a => a is CalcNumber))
        {
            var nums = args.Select(a => ((CalcNumber)a).Value).ToArray();
            double v = name switch
            {
                "sin" => Math.Sin(nums[0]),
                "cos" => Math.Cos(nums[0]),
                "tan" => Math.Tan(nums[0]),
                "asin" => Math.Asin(nums[0]),
                "acos" => Math.Acos(nums[0]),
                "atan" => Math.Atan(nums[0]),
                "atan2" => Math.Atan2(nums[0], nums[1]),
                "sqrt" => Math.Sqrt(nums[0]),
                "exp" => Math.Exp(nums[0]),
                "log" => nums.Length > 1 ? Math.Log(nums[0]) / Math.Log(nums[1]) : Math.Log(nums[0]),
                "pow" => Math.Pow(nums[0], nums[1]),
                "hypot" => Math.Sqrt(nums.Select(n => n * n).Sum()),
                _ => double.NaN,
            };
            // asin/acos/atan/atan2 produce angles in radians per spec.
            if (name is "asin" or "acos" or "atan" or "atan2")
            {
                return new CalcAngle(v * 180.0 / Math.PI, CssAngleUnit.Degrees);
            }

            return new CalcNumber(v);
        }
        // sin/cos/tan accept angles too — fold if all-angle.
        if (name is "sin" or "cos" or "tan" && args.Count == 1 && args[0] is CalcAngle ang)
        {
            var rad = ang.Value * Math.PI / 180.0 * UnitScale(ang.Unit);
            double v = name switch { "sin" => Math.Sin(rad), "cos" => Math.Cos(rad), _ => Math.Tan(rad) };
            return new CalcNumber(v);
        }
        return new CalcFunction(name, args, NumericType.Number);
    }

    private static double UnitScale(CssAngleUnit unit) => unit switch
    {
        CssAngleUnit.Degrees => 1.0,
        CssAngleUnit.Gradians => 0.9,
        CssAngleUnit.Radians => 180.0 / Math.PI,
        CssAngleUnit.Turns => 360.0,
        _ => 1.0,
    };

    // ----------- Expression parsing -----------

    /// <summary>Parse a single expression (additive chain) from a component value list.</summary>
    public static CalcNode ParseExpression(IReadOnlyList<CssComponentValue> values)
    {
        var stream = new Stream(values);
        var node = ParseAdditive(stream);
        return Reduce(node);
    }

    private sealed class Stream
    {
        private readonly IReadOnlyList<CssComponentValue> _items;
        private int _pos;
        public Stream(IReadOnlyList<CssComponentValue> items) { _items = items; }
        public CssComponentValue? Peek()
        {
            SkipWs();
            return _pos < _items.Count ? _items[_pos] : null;
        }
        public CssComponentValue? Read()
        {
            SkipWs();
            return _pos < _items.Count ? _items[_pos++] : null;
        }
        public bool HasMore { get { SkipWs(); return _pos < _items.Count; } }
        public bool PeekWhitespaceBefore()
            => _pos > 0 && _items[_pos - 1] is CssTokenValue { Token.Type: CssTokenType.Whitespace };
        public bool HasWhitespaceAt(int offset)
            => _pos + offset >= 0 && _pos + offset < _items.Count
               && _items[_pos + offset] is CssTokenValue { Token.Type: CssTokenType.Whitespace };
        private void SkipWs()
        {
            while (_pos < _items.Count && _items[_pos] is CssTokenValue { Token.Type: CssTokenType.Whitespace })
            {
                _pos++;
            }
        }
    }

    private static CalcNode ParseAdditive(Stream s)
    {
        var left = ParseMultiplicative(s);
        while (true)
        {
            var peek = s.Peek();
            if (peek is not CssTokenValue tv)
            {
                break;
            }

            if (tv.Token.Type != CssTokenType.Delim || (tv.Token.Delimiter != '+' && tv.Token.Delimiter != '-'))
            {
                break;
            }
            // Spec requires whitespace around + / - in calc.
            s.Read();
            var right = ParseMultiplicative(s);
            var op = tv.Token.Delimiter == '+' ? CalcOperator.Add : CalcOperator.Subtract;
            var resultType = CombineAdditiveType(left.Type, right.Type, op);
            left = new CalcBinary(op, left, right, resultType);
        }
        return left;
    }

    private static CalcNode ParseMultiplicative(Stream s)
    {
        var left = ParseUnary(s);
        while (true)
        {
            var peek = s.Peek();
            if (peek is not CssTokenValue tv)
            {
                break;
            }

            if (tv.Token.Type != CssTokenType.Delim || (tv.Token.Delimiter != '*' && tv.Token.Delimiter != '/'))
            {
                break;
            }

            s.Read();
            var right = ParseUnary(s);
            var op = tv.Token.Delimiter == '*' ? CalcOperator.Multiply : CalcOperator.Divide;
            var resultType = CombineMultiplicativeType(left.Type, right.Type, op);
            left = new CalcBinary(op, left, right, resultType);
        }
        return left;
    }

    private static CalcNode ParseUnary(Stream s)
    {
        var peek = s.Peek();
        if (peek is CssTokenValue { Token: { Type: CssTokenType.Delim, Delimiter: '-' } })
        {
            s.Read();
            var operand = ParseUnary(s);
            return new CalcNegate(operand);
        }
        if (peek is CssTokenValue { Token: { Type: CssTokenType.Delim, Delimiter: '+' } })
        {
            s.Read();
            return ParseUnary(s);
        }
        return ParsePrimary(s);
    }

    private static CalcNode ParsePrimary(Stream s)
    {
        var item = s.Read();
        return item switch
        {
            CssTokenValue tv => TokenToNode(tv.Token),
            CssFunction f => CallFunctionNode(f),
            CssSimpleBlock b when b.StartToken == CssTokenType.LeftParen
                => ParseExpression(b.Values),
            _ => new CalcNumber(0),
        };
    }

    private static CalcNode TokenToNode(CssToken token)
        => token.Type switch
        {
            CssTokenType.Number => new CalcNumber(token.Number),
            CssTokenType.Percentage => new CalcPercentage(token.Number),
            CssTokenType.Dimension => DimensionToNode(token.Number, token.Unit),
            CssTokenType.Ident => IdentToNode(token.Value),
            _ => new CalcNumber(0),
        };

    private static CalcNumber IdentToNode(string ident)
        => ident.ToLowerInvariant() switch
        {
            "pi" => new CalcNumber(Math.PI),
            "e" => new CalcNumber(Math.E),
            "infinity" => new CalcNumber(double.PositiveInfinity),
            "-infinity" => new CalcNumber(double.NegativeInfinity),
            "nan" => new CalcNumber(double.NaN),
            _ => new CalcNumber(0),
        };

    private static CalcNode DimensionToNode(double value, string unit)
    {
        if (Enum.TryParse<CssLengthUnit>(unit, ignoreCase: true, out var lengthUnit))
        {
            return new CalcLength(value, lengthUnit);
        }

        return unit.ToLowerInvariant() switch
        {
            "deg" => new CalcAngle(value, CssAngleUnit.Degrees),
            "grad" => new CalcAngle(value, CssAngleUnit.Gradians),
            "rad" => new CalcAngle(value, CssAngleUnit.Radians),
            "turn" => new CalcAngle(value, CssAngleUnit.Turns),
            "s" => new CalcTime(value, CssTimeUnit.Seconds),
            "ms" => new CalcTime(value, CssTimeUnit.Milliseconds),
            "hz" => new CalcFrequency(value, CssFrequencyUnit.Hertz),
            "khz" => new CalcFrequency(value, CssFrequencyUnit.Kilohertz),
            "dpi" => new CalcResolution(value, CssResolutionUnit.Dpi),
            "dpcm" => new CalcResolution(value, CssResolutionUnit.Dpcm),
            "dppx" or "x" => new CalcResolution(value, CssResolutionUnit.Dppx),
            _ => new CalcNumber(value),
        };
    }

    private static CalcNode CallFunctionNode(CssFunction f)
        => ParseFunctionNode(f.Name, f.Values);

    // ----------- Reduction -----------

    /// <summary>Best-effort constant folding of the tree.</summary>
    public static CalcNode Reduce(CalcNode node)
        => node switch
        {
            CalcNegate(var op) => ReduceNegate(Reduce(op)),
            CalcBinary b => ReduceBinary(b.Op, Reduce(b.Left), Reduce(b.Right), b.ResultType),
            _ => node,
        };

    private static CalcNode ReduceNegate(CalcNode op)
        => op switch
        {
            CalcNumber n => new CalcNumber(-n.Value),
            CalcLength l => new CalcLength(-l.Value, l.Unit),
            CalcPercentage p => new CalcPercentage(-p.Value),
            CalcAngle a => new CalcAngle(-a.Value, a.Unit),
            CalcTime t => new CalcTime(-t.Value, t.Unit),
            CalcFrequency f => new CalcFrequency(-f.Value, f.Unit),
            CalcResolution r => new CalcResolution(-r.Value, r.Unit),
            CalcNegate n => n.Operand,
            _ => new CalcNegate(op),
        };

    private static CalcNode ReduceBinary(CalcOperator op, CalcNode left, CalcNode right, NumericType resultType)
    {
        // number op number
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

        // multiplication / division of any-typed by number → preserves type & unit.
        if (op is CalcOperator.Multiply or CalcOperator.Divide)
        {
            if (right is CalcNumber n2)
            {
                return ScaleNode(left, op == CalcOperator.Multiply ? n2.Value : (n2.Value == 0 ? double.NaN : 1.0 / n2.Value));
            }

            if (left is CalcNumber n1 && op == CalcOperator.Multiply)
            {
                return ScaleNode(right, n1.Value);
            }
        }

        // length + length (absolute units): fold to px-equivalent in the larger unit if both absolute.
        if (op is CalcOperator.Add or CalcOperator.Subtract
            && left is CalcLength lL && right is CalcLength rL
            && lL.Unit.IsAbsolute() && rL.Unit.IsAbsolute())
        {
            var sum = lL.Unit.AbsoluteToPx(lL.Value) +
                      (op == CalcOperator.Add ? 1 : -1) * rL.Unit.AbsoluteToPx(rL.Value);
            return new CalcLength(sum, CssLengthUnit.Px);
        }
        if (op is CalcOperator.Add or CalcOperator.Subtract
            && left is CalcLength lL2 && right is CalcLength rL2 && lL2.Unit == rL2.Unit)
        {
            var sum = op == CalcOperator.Add ? lL2.Value + rL2.Value : lL2.Value - rL2.Value;
            return new CalcLength(sum, lL2.Unit);
        }
        if (op is CalcOperator.Add or CalcOperator.Subtract
            && left is CalcPercentage lP && right is CalcPercentage rP)
        {
            var sum = op == CalcOperator.Add ? lP.Value + rP.Value : lP.Value - rP.Value;
            return new CalcPercentage(sum);
        }
        if (op is CalcOperator.Add or CalcOperator.Subtract
            && left is CalcAngle lA && right is CalcAngle rA)
        {
            var a = lA.InDegrees() + (op == CalcOperator.Add ? 1 : -1) * rA.InDegrees();
            return new CalcAngle(a, CssAngleUnit.Degrees);
        }
        // time + time: preserve the unit when both match, else fold to seconds.
        if (op is CalcOperator.Add or CalcOperator.Subtract
            && left is CalcTime lT && right is CalcTime rT)
        {
            if (lT.Unit == rT.Unit)
            {
                return new CalcTime(op == CalcOperator.Add ? lT.Value + rT.Value : lT.Value - rT.Value, lT.Unit);
            }

            var t = lT.InSeconds() + (op == CalcOperator.Add ? 1 : -1) * rT.InSeconds();
            return new CalcTime(t, CssTimeUnit.Seconds);
        }
        // frequency + frequency: preserve the unit when both match, else fold to hertz.
        if (op is CalcOperator.Add or CalcOperator.Subtract
            && left is CalcFrequency lF && right is CalcFrequency rF)
        {
            if (lF.Unit == rF.Unit)
            {
                return new CalcFrequency(op == CalcOperator.Add ? lF.Value + rF.Value : lF.Value - rF.Value, lF.Unit);
            }

            var f = lF.InHertz() + (op == CalcOperator.Add ? 1 : -1) * rF.InHertz();
            return new CalcFrequency(f, CssFrequencyUnit.Hertz);
        }

        return new CalcBinary(op, left, right, resultType);
    }

    private static CalcNode ScaleNode(CalcNode n, double s)
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

    // ----------- Type system (Values 4 §10.2) -----------

    private static NumericType CombineAdditiveType(NumericType a, NumericType b, CalcOperator op)
    {
        if (a == b)
        {
            return a;
        }

        if ((a == NumericType.Length && b == NumericType.Percentage) ||
            (a == NumericType.Percentage && b == NumericType.Length) ||
            a == NumericType.LengthPercentage || b == NumericType.LengthPercentage)
        {
            return NumericType.LengthPercentage;
        }

        if (a == NumericType.Number)
        {
            return b;
        }

        if (b == NumericType.Number)
        {
            return a;
        }

        return NumericType.Unknown;
    }

    private static NumericType CombineMultiplicativeType(NumericType a, NumericType b, CalcOperator op)
    {
        if (op == CalcOperator.Multiply)
        {
            if (a == NumericType.Number)
            {
                return b;
            }

            if (b == NumericType.Number)
            {
                return a;
            }

            return NumericType.Unknown; // length*length etc. is a type error
        }
        // Divide:
        if (b == NumericType.Number)
        {
            return a;
        }

        if (a == b)
        {
            return NumericType.Number;
        }

        return NumericType.Unknown;
    }

    // ----------- Helpers -----------

    private static bool IsAbsoluteLiteral(CalcNode n) => n switch
    {
        CalcNumber => true,
        CalcLength l => l.Unit.IsAbsolute(),
        CalcPercentage => true,
        CalcAngle => true,
        CalcTime => true,
        CalcFrequency => true,
        CalcResolution => true,
        _ => false,
    };

    private static double GetAbsoluteValue(CalcNode n) => n switch
    {
        CalcNumber x => x.Value,
        CalcLength l => l.Unit.AbsoluteToPx(l.Value),
        CalcPercentage p => p.Value,
        CalcAngle a => a.InDegrees(),
        CalcTime t => t.Unit == CssTimeUnit.Seconds ? t.Value : t.Value / 1000.0,
        CalcFrequency f => f.Unit == CssFrequencyUnit.Hertz ? f.Value : f.Value * 1000.0,
        CalcResolution r => r.Unit switch
        {
            CssResolutionUnit.Dppx => r.Value,
            CssResolutionUnit.Dpi => r.Value / 96.0,
            CssResolutionUnit.Dpcm => r.Value * 2.54 / 96.0,
            _ => r.Value,
        },
        _ => 0,
    };

    private static (string, CssLengthUnit?, CssAngleUnit?, CssTimeUnit?, CssFrequencyUnit?, CssResolutionUnit?) GetPreferredUnit(IReadOnlyList<CalcNode> args)
    {
        foreach (var a in args)
        {
            switch (a)
            {
                case CalcLength l: return ("length", l.Unit, null, null, null, null);
                case CalcAngle ang: return ("angle", null, ang.Unit, null, null, null);
                case CalcTime t: return ("time", null, null, t.Unit, null, null);
                case CalcFrequency f: return ("freq", null, null, null, f.Unit, null);
                case CalcResolution r: return ("res", null, null, null, null, r.Unit);
                case CalcPercentage: return ("pct", null, null, null, null, null);
                case CalcNumber: continue;
            }
        }
        return ("num", null, null, null, null, null);
    }

    private static CalcNode MakeLiteral(double v,
        (string, CssLengthUnit?, CssAngleUnit?, CssTimeUnit?, CssFrequencyUnit?, CssResolutionUnit?) preferred,
        NumericType _)
        => preferred.Item1 switch
        {
            "length" => new CalcLength(preferred.Item2 == CssLengthUnit.Px ? v : preferred.Item2!.Value.IsAbsolute()
                ? FromPx(v, preferred.Item2!.Value)
                : v, preferred.Item2!.Value),
            "angle" => new CalcAngle(v, preferred.Item3!.Value),
            "time" => new CalcTime(v, preferred.Item4!.Value),
            "freq" => new CalcFrequency(v, preferred.Item5!.Value),
            "res" => new CalcResolution(v, preferred.Item6!.Value),
            "pct" => new CalcPercentage(v),
            _ => new CalcNumber(v),
        };

    private static double FromPx(double px, CssLengthUnit unit) => unit switch
    {
        CssLengthUnit.Px => px,
        CssLengthUnit.Pt => px * 3d / 4d,
        CssLengthUnit.Pc => px / 16d,
        CssLengthUnit.In => px / 96d,
        CssLengthUnit.Cm => px * 2.54d / 96d,
        CssLengthUnit.Mm => px * 25.4d / 96d,
        CssLengthUnit.Q => px * 101.6d / 96d,
        _ => px,
    };

    private static IEnumerable<IReadOnlyList<CssComponentValue>> SplitArguments(IReadOnlyList<CssComponentValue> values)
    {
        var current = new List<CssComponentValue>();
        foreach (var v in values)
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
}

internal static class CalcAngleExtensions
{
    public static double InDegrees(this CalcAngle a) => a.Unit switch
    {
        CssAngleUnit.Degrees => a.Value,
        CssAngleUnit.Gradians => a.Value * 0.9,
        CssAngleUnit.Radians => a.Value * 180.0 / Math.PI,
        CssAngleUnit.Turns => a.Value * 360.0,
        _ => a.Value,
    };
}
