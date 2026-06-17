using System.Globalization;
using System.Text;
using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>§21.1 Number Objects. Installs Number as callable/constructible,
/// its constants/statics, and Number.prototype formatting/value methods.</summary>
public static class NumberCtor
{
    private const double MaxSafeInteger = 9007199254740991d;
    private const double MinSafeInteger = -9007199254740991d;

    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.NumberPrototype;

        JsNativeFunction? ctor = null;
        ctor = new JsNativeFunction(realm, "Number", length: 1, (newTarget, args) =>
        {
            var n = args.Length == 0 ? 0 : ToNumber(args[0]);
            // §21.1.1.1: constructed → wrapper prototyped from new.target.
            if (IntrinsicHelpers.IsConstructInvocation(newTarget))
            {
                var box = realm.BoxNumber(JsValue.Number(n));
                var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
                if (!ReferenceEquals(instProto, proto))
                {
                    box.SetPrototypeOf(instProto);
                }

                return JsValue.Object(box);
            }
            return JsValue.Number(n);
        }, isConstructor: true);
        DefineData(ctor, "prototype", JsValue.Object(proto), false, false, false);

        DefineConst(ctor, "EPSILON", Math.Pow(2, -52));
        DefineConst(ctor, "MAX_SAFE_INTEGER", MaxSafeInteger);
        DefineConst(ctor, "MIN_SAFE_INTEGER", MinSafeInteger);
        DefineConst(ctor, "MAX_VALUE", double.MaxValue);
        DefineConst(ctor, "MIN_VALUE", double.Epsilon);
        DefineConst(ctor, "POSITIVE_INFINITY", double.PositiveInfinity);
        DefineConst(ctor, "NEGATIVE_INFINITY", double.NegativeInfinity);
        DefineConst(ctor, "NaN", double.NaN);

        IntrinsicHelpers.DefineMethod(realm, ctor, "isFinite", 1, (_, args) => JsValue.Boolean(args.Length > 0 && args[0].IsNumber && double.IsFinite(args[0].AsNumber)));
        IntrinsicHelpers.DefineMethod(realm, ctor, "isInteger", 1, (_, args) => JsValue.Boolean(args.Length > 0 && IsInteger(args[0])));
        IntrinsicHelpers.DefineMethod(realm, ctor, "isNaN", 1, (_, args) => JsValue.Boolean(args.Length > 0 && args[0].IsNumber && double.IsNaN(args[0].AsNumber)));
        IntrinsicHelpers.DefineMethod(realm, ctor, "isSafeInteger", 1, (_, args) => JsValue.Boolean(args.Length > 0 && IsSafeInteger(args[0])));
        IntrinsicHelpers.DefineMethod(realm, ctor, "parseFloat", 1, (_, args) => ParseFloat(args));
        IntrinsicHelpers.DefineMethod(realm, ctor, "parseInt", 2, (_, args) => ParseInt(args));

        // Bulk-install constructor + the six prototype methods by adopting one
        // precomputed shape. Same creation order as the prior sequential install
        // (constructor was previously defined right after the prototype slot, then
        // the methods), so getOwnPropertyNames order is unchanged and the result
        // is byte-identical.
        IntrinsicHelpers.BulkInstallBuiltins(realm, proto, new[]
        {
            new IntrinsicHelpers.BulkMember("constructor", 0, null, JsValue.Object(ctor)),
            new IntrinsicHelpers.BulkMember("toString", 1, (thisV, args) => JsValue.String(NumberToString(ThisNumber(realm, thisV), args.Length > 0 ? args[0] : JsValue.Undefined, realm))),
            new IntrinsicHelpers.BulkMember("toFixed", 1, (thisV, args) => JsValue.String(ToFixed(ThisNumber(realm, thisV), ToDigits(args, 0), realm))),
            new IntrinsicHelpers.BulkMember("toPrecision", 1, (thisV, args) => ToPrecision(ThisNumber(realm, thisV), args.Length > 0 ? args[0] : JsValue.Undefined, realm)),
            new IntrinsicHelpers.BulkMember("toExponential", 1, (thisV, args) => JsValue.String(ToExponential(ThisNumber(realm, thisV), args.Length > 0 ? args[0] : JsValue.Undefined, realm))),
            new IntrinsicHelpers.BulkMember("valueOf", 0, (thisV, _) => JsValue.Number(ThisNumber(realm, thisV))),
            new IntrinsicHelpers.BulkMember("toLocaleString", 0, (thisV, _) => JsValue.String(JsValue.ToStringValue(JsValue.Number(ThisNumber(realm, thisV))))),
        });

        realm.NumberConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("Number", PropertyDescriptor.Data(JsValue.Object(ctor), true, false, true));
    }

    internal static JsValue ParseInt(JsValue[] args)
    {
        var input = JsValue.ToStringValue(args.Length > 0 ? args[0] : JsValue.Undefined).TrimStart();
        var radixArg = args.Length > 1 ? ToNumber(args[1]) : double.NaN;
        return JsValue.Number(ParseIntString(input, radixArg));
    }

    internal static JsValue ParseFloat(JsValue[] args)
    {
        var input = JsValue.ToStringValue(args.Length > 0 ? args[0] : JsValue.Undefined).TrimStart();
        return JsValue.Number(ParseFloatString(input));
    }

    internal static double ToNumber(JsValue value)
    {
        if (!value.IsObject)
        {
            return value.IsString ? StringToNumber(value.AsString) : JsValue.ToNumber(value);
        }

        var prim = AbstractOperations.ToPrimitive(value, "number");
        return prim.IsString ? StringToNumber(prim.AsString) : JsValue.ToNumber(prim);
    }

    private static bool IsInteger(JsValue value)
    {
        if (!value.IsNumber)
        {
            return false;
        }

        var n = value.AsNumber;
        return double.IsFinite(n) && n == Math.Truncate(n);
    }

    private static bool IsSafeInteger(JsValue value) => IsInteger(value) && Math.Abs(value.AsNumber) <= MaxSafeInteger;

    private static double ThisNumber(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsNumber)
        {
            return thisV.AsNumber;
        }

        if (thisV.IsObject)
        {
            var slot = thisV.AsObject.GetOwnPropertyDescriptor("__primitiveValue");
            if (slot is { } d && d.Value.IsNumber)
            {
                return d.Value.AsNumber;
            }
        }
        throw new JsThrow(realm.NewTypeError("Number.prototype method called on incompatible receiver"));
    }

    private static double StringToNumber(string s)
    {
        var t = s.Trim();
        if (t.Length == 0)
        {
            return 0;
        }

        var sign = 1;
        if (t[0] == '+' || t[0] == '-') { if (t[0] == '-') { sign = -1; } t = t[1..]; }
        if (string.Equals(t, "Infinity", StringComparison.Ordinal))
        {
            return sign > 0 ? double.PositiveInfinity : double.NegativeInfinity;
        }

        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var acc = 0d;
            for (var i = 2; i < t.Length; i++)
            {
                var digit = DigitValue(t[i]);
                if (digit < 0 || digit >= 16)
                {
                    return double.NaN;
                }

                acc = acc * 16 + digit;
            }
            return sign * acc;
        }
        return double.TryParse((sign < 0 ? "-" : "") + t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }

    /// <summary>§19.2.5 parseInt — radix inference, optional sign, and longest valid digit prefix.</summary>
    private static double ParseIntString(string s, double radixNumber)
    {
        var pos = 0;
        var sign = 1;
        if (pos < s.Length && (s[pos] == '+' || s[pos] == '-')) { if (s[pos] == '-') { sign = -1; } pos++; }
        var stripPrefix = true;
        var radix = double.IsNaN(radixNumber) || radixNumber == 0 ? 0 : (int)radixNumber;
        if (radix != 0)
        {
            if (radix < 2 || radix > 36)
            {
                return double.NaN;
            }

            stripPrefix = radix == 16;
        }
        if (stripPrefix && pos + 1 < s.Length && s[pos] == '0' && (s[pos + 1] == 'x' || s[pos + 1] == 'X'))
        {
            radix = 16;
            pos += 2;
        }
        if (radix == 0)
        {
            radix = 10;
        }

        var any = false;
        var value = 0d;
        for (; pos < s.Length; pos++)
        {
            var d = DigitValue(s[pos]);
            if (d < 0 || d >= radix)
            {
                break;
            }

            any = true;
            value = value * radix + d;
        }
        return any ? sign * value : double.NaN;
    }

    private static double ParseFloatString(string s)
    {
        if (s.Length == 0)
        {
            return double.NaN;
        }

        var pos = 0;
        if (s[pos] == '+' || s[pos] == '-')
        {
            pos++;
        }

        if (pos + 8 <= s.Length && string.Equals(s.Substring(pos, 8), "Infinity", StringComparison.Ordinal))
        {
            return s[0] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        }

        var startDigits = pos;
        while (pos < s.Length && char.IsAsciiDigit(s[pos]))
        {
            pos++;
        }

        var before = pos > startDigits;
        if (pos < s.Length && s[pos] == '.')
        {
            pos++;
            var fracStart = pos;
            while (pos < s.Length && char.IsAsciiDigit(s[pos]))
            {
                pos++;
            }

            before = before || pos > fracStart;
        }
        if (!before)
        {
            return double.NaN;
        }

        if (pos < s.Length && (s[pos] == 'e' || s[pos] == 'E'))
        {
            var expMark = pos++;
            if (pos < s.Length && (s[pos] == '+' || s[pos] == '-'))
            {
                pos++;
            }

            var expStart = pos;
            while (pos < s.Length && char.IsAsciiDigit(s[pos]))
            {
                pos++;
            }

            if (pos == expStart)
            {
                pos = expMark;
            }
        }
        return double.TryParse(s[..pos], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
    }

    private static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'z' => c - 'a' + 10,
        >= 'A' and <= 'Z' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>§21.1.3.6 Number.prototype.toString — base conversion for radix 2..36.</summary>
    private static string NumberToString(double n, JsValue radixValue, JsRealm realm)
    {
        var radix = radixValue.IsUndefined ? 10 : (int)ToNumber(radixValue);
        if (radix < 2 || radix > 36)
        {
            throw new JsThrow(realm.NewRangeError("radix must be between 2 and 36"));
        }

        if (radix == 10)
        {
            return JsValue.ToStringValue(JsValue.Number(n));
        }

        if (double.IsNaN(n))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(n))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(n))
        {
            return "-Infinity";
        }

        if (n == 0)
        {
            return "0";
        }

        var sign = n < 0 ? "-" : "";
        n = Math.Abs(n);
        var integer = Math.Truncate(n);
        var sb = new StringBuilder();
        do
        {
            var d = (int)(integer % radix);
            sb.Insert(0, "0123456789abcdefghijklmnopqrstuvwxyz"[d]);
            integer = Math.Floor(integer / radix);
        } while (integer > 0);
        var frac = n - Math.Truncate(n);
        if (frac > 0)
        {
            sb.Append('.');
            for (var i = 0; i < 32 && frac > 0; i++)
            {
                frac *= radix;
                var d = (int)Math.Floor(frac);
                sb.Append("0123456789abcdefghijklmnopqrstuvwxyz"[d]);
                frac -= d;
            }
        }
        return sign + sb;
    }

    /// <summary>§21.1.3.3 Number.prototype.toFixed — fixed decimal formatting, invariant culture.</summary>
    private static string ToFixed(double n, int digits, JsRealm realm)
    {
        if (digits < 0 || digits > 100)
        {
            throw new JsThrow(realm.NewRangeError("toFixed digits out of range"));
        }

        if (double.IsNaN(n))
        {
            return "NaN";
        }

        if (double.IsInfinity(n))
        {
            return JsValue.ToStringValue(JsValue.Number(n));
        }

        if (Math.Abs(n) >= 1e21)
        {
            return JsValue.ToStringValue(JsValue.Number(n));
        }

        return n.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static JsValue ToPrecision(double n, JsValue precisionValue, JsRealm realm)
    {
        if (precisionValue.IsUndefined)
        {
            return JsValue.String(JsValue.ToStringValue(JsValue.Number(n)));
        }

        var p = ToDigits(new[] { precisionValue }, 0);
        if (p < 1 || p > 100)
        {
            throw new JsThrow(realm.NewRangeError("toPrecision precision out of range"));
        }

        if (double.IsNaN(n) || double.IsInfinity(n))
        {
            return JsValue.String(JsValue.ToStringValue(JsValue.Number(n)));
        }

        if (n == 0)
        {
            return JsValue.String(p == 1 ? "0" : "0." + new string('0', p - 1));
        }

        var exponent = (int)Math.Floor(Math.Log10(Math.Abs(n)));
        if (exponent >= p || exponent < -6)
        {
            return JsValue.String(ToExponential(n, JsValue.Number(p - 1), realm));
        }

        var fractionDigits = Math.Max(0, p - exponent - 1);
        return JsValue.String(n.ToString("F" + fractionDigits.ToString(CultureInfo.InvariantCulture),
            CultureInfo.InvariantCulture));
    }

    private static string ToExponential(double n, JsValue digitsValue, JsRealm realm)
    {
        if (double.IsNaN(n) || double.IsInfinity(n))
        {
            return JsValue.ToStringValue(JsValue.Number(n));
        }

        string fmt;
        if (digitsValue.IsUndefined)
        {
            fmt = "E15";
        }
        else
        {
            var f = (int)ToNumber(digitsValue);
            if (f < 0 || f > 100)
            {
                throw new JsThrow(realm.NewRangeError("toExponential digits out of range"));
            }

            fmt = "E" + f.ToString(CultureInfo.InvariantCulture);
        }
        var s = NormalizeExponent(n.ToString(fmt, CultureInfo.InvariantCulture));
        if (digitsValue.IsUndefined)
        {
            s = s.TrimEnd('0').Replace(".e", "e", StringComparison.Ordinal);
        }

        return s;
    }

    private static int ToDigits(JsValue[] args, int index) => args.Length <= index || args[index].IsUndefined ? 0 : (int)ToNumber(args[index]);

    private static string NormalizeExponent(string s)
    {
        var e = s.IndexOf('E');
        if (e < 0)
        {
            return s;
        }

        var mant = s[..e];
        var exp = int.Parse(s[(e + 1)..], CultureInfo.InvariantCulture);
        return mant + "e" + (exp >= 0 ? "+" : "") + exp.ToString(CultureInfo.InvariantCulture);
    }

    private static void DefineConst(JsObject target, string name, double value) => DefineData(target, name, JsValue.Number(value), false, false, false);

    private static void DefineData(JsObject target, string name, JsValue value, bool writable, bool enumerable, bool configurable)
        => target.DefineOwnProperty(name, PropertyDescriptor.Data(value, writable, enumerable, configurable));
}
