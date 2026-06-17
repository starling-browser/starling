using SysMath = System.Math;
using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// §21.3 The Math Object. Installs the global <c>Math</c> namespace object on
/// the realm's global object as a non-callable, non-constructible plain object
/// with the full set of constants and static methods.
/// </summary>
/// <remarks>
/// Constants are sealed (non-writable + non-enumerable + non-configurable) per
/// spec; static methods follow the typical builtin descriptor
/// (writable + non-enumerable + configurable). Argument coercion routes through
/// <see cref="JsValue.ToNumber(JsValue)"/> — the standard §7.1.4 path — so
/// callers passing strings, booleans, etc. behave per spec. Several methods
/// deviate from the .NET <c>System.Math</c> behavior and are noted inline
/// (notably <c>round</c>, <c>sign</c>, <c>pow(NaN, 0)</c>, and variadic
/// <c>max</c>/<c>min</c>).
/// </remarks>
public static class MathObj
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var math = new JsObject(realm.ObjectPrototype);

        // ---------- Constants (sealed) ---------------------------------------
        DefineConst(math, "E", SysMath.E);
        DefineConst(math, "LN10", SysMath.Log(10));
        DefineConst(math, "LN2", SysMath.Log(2));
        DefineConst(math, "LOG10E", 1.0 / SysMath.Log(10));
        DefineConst(math, "LOG2E", 1.0 / SysMath.Log(2));
        DefineConst(math, "PI", SysMath.PI);
        DefineConst(math, "SQRT1_2", SysMath.Sqrt(0.5));
        DefineConst(math, "SQRT2", SysMath.Sqrt(2));

        // ---------- Single-argument methods ----------------------------------
        DefineUnary(realm, math, "abs", SysMath.Abs);
        DefineUnary(realm, math, "acos", SysMath.Acos);
        DefineUnary(realm, math, "acosh", SysMath.Acosh);
        DefineUnary(realm, math, "asin", SysMath.Asin);
        DefineUnary(realm, math, "asinh", SysMath.Asinh);
        DefineUnary(realm, math, "atan", SysMath.Atan);
        DefineUnary(realm, math, "atanh", SysMath.Atanh);
        DefineUnary(realm, math, "cbrt", SysMath.Cbrt);
        DefineUnary(realm, math, "ceil", SysMath.Ceiling);
        DefineUnary(realm, math, "cos", SysMath.Cos);
        DefineUnary(realm, math, "cosh", SysMath.Cosh);
        DefineUnary(realm, math, "exp", SysMath.Exp);
        DefineUnary(realm, math, "expm1", x => SysMath.Exp(x) - 1.0); // .NET has no Expm1
        DefineUnary(realm, math, "floor", SysMath.Floor);
        DefineUnary(realm, math, "fround", x => (double)(float)x);
        DefineUnary(realm, math, "log", SysMath.Log);
        DefineUnary(realm, math, "log10", SysMath.Log10);
        DefineUnary(realm, math, "log1p", x => SysMath.Log(1.0 + x)); // .NET has no Log1p
        DefineUnary(realm, math, "log2", SysMath.Log2);
        DefineUnary(realm, math, "sin", SysMath.Sin);
        DefineUnary(realm, math, "sinh", SysMath.Sinh);
        DefineUnary(realm, math, "sqrt", SysMath.Sqrt);
        DefineUnary(realm, math, "tan", SysMath.Tan);
        DefineUnary(realm, math, "tanh", SysMath.Tanh);
        DefineUnary(realm, math, "trunc", SysMath.Truncate);

        // Math.round — JS rounds half toward +∞ (NOT to even, NOT away from zero).
        // .NET's Math.Round uses banker's rounding by default, so we hand-roll.
        DefineUnary(realm, math, "round", JsRound);

        // Math.sign — preserves signed zero (Math.sign(-0) === -0).
        DefineUnary(realm, math, "sign", JsSign);

        // Math.clz32 — count leading zeroes of ToUint32(x). 32 when x is 0.
        DefineUnary(realm, math, "clz32", x => Clz32(x));

        // Math.random — pseudo-random number in [0, 1). §21.3.2.27 leaves the
        // algorithm implementation-defined and explicitly non-cryptographic;
        // Random.Shared is thread-safe (.NET 6+) which matters because the
        // engine may pump callbacks across threads.
        IntrinsicHelpers.DefineMethod(realm, math, "random", 0,
            (_, _) => JsValue.Number(System.Random.Shared.NextDouble()));

        // ---------- Two-argument methods -------------------------------------
        IntrinsicHelpers.DefineMethod(realm, math, "atan2", 2, (_, args) =>
        {
            var y = args.Length > 0 ? JsValue.ToNumber(args[0]) : double.NaN;
            var x = args.Length > 1 ? JsValue.ToNumber(args[1]) : double.NaN;
            return JsValue.Number(SysMath.Atan2(y, x));
        });

        IntrinsicHelpers.DefineMethod(realm, math, "pow", 2, (_, args) =>
        {
            var b = args.Length > 0 ? JsValue.ToNumber(args[0]) : double.NaN;
            var e = args.Length > 1 ? JsValue.ToNumber(args[1]) : double.NaN;
            // Math.pow(NaN, 0) === 1 per ES spec (overrides usual NaN-poisoning).
            if (e == 0)
            {
                return JsValue.Number(1);
            }

            return JsValue.Number(SysMath.Pow(b, e));
        });

        IntrinsicHelpers.DefineMethod(realm, math, "imul", 2, (_, args) =>
        {
            var a = ToUint32(args.Length > 0 ? args[0] : JsValue.Undefined);
            var b = ToUint32(args.Length > 1 ? args[1] : JsValue.Undefined);
            // Truncate to Int32 after multiplication mod 2^32.
            unchecked
            {
                uint product = a * b;
                return JsValue.Number((int)product);
            }
        });

        // ---------- Variadic methods -----------------------------------------
        // Math.max / Math.min: spec length === 2 (declared as (value1, value2, ...values)).
        IntrinsicHelpers.DefineMethod(realm, math, "max", 2, (_, args) =>
        {
            // Math.max() === -Infinity per spec.
            double best = double.NegativeInfinity;
            foreach (var arg in args)
            {
                var n = JsValue.ToNumber(arg);
                if (double.IsNaN(n))
                {
                    return JsValue.NaN;
                }
                // +0 should beat -0 for max.
                if (n > best || (n == 0 && best == 0 && !double.IsNegative(n)))
                {
                    best = n;
                }
            }
            return JsValue.Number(best);
        });

        IntrinsicHelpers.DefineMethod(realm, math, "min", 2, (_, args) =>
        {
            // Math.min() === +Infinity per spec.
            double best = double.PositiveInfinity;
            foreach (var arg in args)
            {
                var n = JsValue.ToNumber(arg);
                if (double.IsNaN(n))
                {
                    return JsValue.NaN;
                }

                if (n < best || (n == 0 && best == 0 && double.IsNegative(n)))
                {
                    best = n;
                }
            }
            return JsValue.Number(best);
        });

        // Math.hypot: spec length === 2 (declared as (value1, value2, ...values)).
        IntrinsicHelpers.DefineMethod(realm, math, "hypot", 2, (_, args) =>
        {
            if (args.Length == 0)
            {
                return JsValue.Number(0);
            }
            // General variadic case: sum-of-squares with infinity/NaN guards.
            bool anyInf = false; bool anyNan = false;
            double sum = 0;
            foreach (var arg in args)
            {
                var n = JsValue.ToNumber(arg);
                if (double.IsInfinity(n))
                {
                    anyInf = true;
                }
                else if (double.IsNaN(n))
                {
                    anyNan = true;
                }
                else
                {
                    sum += n * n;
                }
            }
            if (anyInf)
            {
                return JsValue.Number(double.PositiveInfinity);
            }

            if (anyNan)
            {
                return JsValue.NaN;
            }

            return JsValue.Number(SysMath.Sqrt(sum));
        });

        // §21.3.1 Math[@@toStringTag] = "Math".
        math.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("Math"), writable: false, enumerable: false, configurable: true));

        // Stamp Math on the global object as writable + configurable + non-enumerable.
        realm.GlobalObject.DefineOwnProperty("Math",
            PropertyDescriptor.Data(JsValue.Object(math),
                writable: true, enumerable: false, configurable: true));
    }

    // ----------------------------------------------------------- helpers

    private static void DefineConst(JsObject math, string name, double value)
    {
        math.DefineOwnProperty(name,
            PropertyDescriptor.Data(JsValue.Number(value),
                writable: false, enumerable: false, configurable: false));
    }

    private static void DefineUnary(JsRealm realm, JsObject math, string name, Func<double, double> fn)
    {
        // All single-argument Math.* statics declare a spec length of 1.
        IntrinsicHelpers.DefineMethod(realm, math, name, 1, (_, args) =>
        {
            var x = args.Length > 0 ? JsValue.ToNumber(args[0]) : double.NaN;
            return JsValue.Number(fn(x));
        });
    }

    /// <summary>JS-style Math.round: rounds half toward +∞, with NaN / ±Infinity
    /// / signed-zero passthrough. Implemented as <c>floor(x + 0.5)</c> with the
    /// usual edge-case handling.</summary>
    private static double JsRound(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x))
        {
            return x;
        }

        if (x == 0)
        {
            return x; // preserves -0 / +0
        }
        // Math.round(-0.5) === -0, Math.round(-0.4) === -0, … negative values
        // in [-0.5, 0] round to -0. Math.round(0.5) === 1.
        if (x < 0 && x >= -0.5)
        {
            return -0.0;
        }

        return SysMath.Floor(x + 0.5);
    }

    /// <summary>JS-style Math.sign: returns -0 for -0, +0 for +0, ±1 otherwise.</summary>
    private static double JsSign(double x)
    {
        if (double.IsNaN(x))
        {
            return double.NaN;
        }

        if (x > 0)
        {
            return 1.0;
        }

        if (x < 0)
        {
            return -1.0;
        }

        return x; // preserves signed zero
    }

    /// <summary>ToUint32 per §7.1.7 — used by Math.clz32 / Math.imul.</summary>
    private static uint ToUint32(JsValue value)
    {
        var n = JsValue.ToNumber(value);
        if (double.IsNaN(n) || double.IsInfinity(n) || n == 0)
        {
            return 0;
        }

        var i = SysMath.Truncate(n);
        // Modulo 2^32, then map to unsigned.
        var mod = i - SysMath.Floor(i / 4294967296.0) * 4294967296.0;
        return (uint)mod;
    }

    private static double Clz32(double x)
    {
        var u = ToUint32(JsValue.Number(x));
        if (u == 0)
        {
            return 32;
        }

        int c = 0;
        while ((u & 0x80000000u) == 0) { c++; u <<= 1; }
        return c;
    }
}
