using System.Numerics;

namespace Starling.Js.Runtime;

internal static class BigIntOps
{
    public static JsThrow MixedTypeError(JsRealm realm, string op)
        => new(realm.NewTypeError($"Cannot mix BigInt and other types, use explicit conversions ({op})"));

    public static JsValue Add(BigInteger a, BigInteger b) => JsValue.BigInt(a + b);
    public static JsValue Subtract(BigInteger a, BigInteger b) => JsValue.BigInt(a - b);
    public static JsValue Multiply(BigInteger a, BigInteger b) => JsValue.BigInt(a * b);

    public static JsValue Divide(JsRealm realm, BigInteger a, BigInteger b)
    {
        if (b.IsZero)
        {
            throw new JsThrow(realm.NewRangeError("Division by zero"));
        }

        return JsValue.BigInt(BigInteger.Divide(a, b));
    }

    public static JsValue Remainder(JsRealm realm, BigInteger a, BigInteger b)
    {
        if (b.IsZero)
        {
            throw new JsThrow(realm.NewRangeError("Division by zero"));
        }

        return JsValue.BigInt(BigInteger.Remainder(a, b));
    }

    public static JsValue Pow(JsRealm realm, BigInteger a, BigInteger b)
    {
        if (b.Sign < 0)
        {
            throw new JsThrow(realm.NewRangeError("Exponent must be positive"));
        }

        if (b > int.MaxValue)
        {
            throw new JsThrow(realm.NewRangeError("Exponent too large"));
        }

        return JsValue.BigInt(BigInteger.Pow(a, (int)b));
    }

    public static JsValue Negate(BigInteger a) => JsValue.BigInt(-a);
    public static JsValue BitwiseNot(BigInteger a) => JsValue.BigInt(~a);
    public static JsValue BitwiseAnd(BigInteger a, BigInteger b) => JsValue.BigInt(a & b);
    public static JsValue BitwiseOr(BigInteger a, BigInteger b) => JsValue.BigInt(a | b);
    public static JsValue BitwiseXor(BigInteger a, BigInteger b) => JsValue.BigInt(a ^ b);

    public static JsValue ShiftLeft(JsRealm realm, BigInteger a, BigInteger b)
    {
        if (b > int.MaxValue || b < int.MinValue)
        {
            throw new JsThrow(realm.NewRangeError("BigInt shift count out of range"));
        }

        return JsValue.BigInt(a << (int)b);
    }

    public static JsValue ShiftRight(JsRealm realm, BigInteger a, BigInteger b)
    {
        if (b > int.MaxValue || b < int.MinValue)
        {
            throw new JsThrow(realm.NewRangeError("BigInt shift count out of range"));
        }

        return JsValue.BigInt(a >> (int)b);
    }

    public static bool LessThan(BigInteger a, BigInteger b) => a < b;

    public static BigInteger ToBigInt(JsRealm realm, JsValue value)
    {
        ArgumentNullException.ThrowIfNull(realm);
        value = AbstractOperations.ToPrimitive(value, "number");
        return value.Kind switch
        {
            JsValueKind.BigInt => value.AsBigInt,
            JsValueKind.Boolean => value.AsBool ? BigInteger.One : BigInteger.Zero,
            JsValueKind.String => ParseStringToBigInt(realm, value.AsString),
            JsValueKind.Number => NumberToBigInt(realm, value.AsNumber),
            JsValueKind.Undefined => throw new JsThrow(realm.NewTypeError("Cannot convert undefined to a BigInt")),
            JsValueKind.Null => throw new JsThrow(realm.NewTypeError("Cannot convert null to a BigInt")),
            JsValueKind.Symbol => throw new JsThrow(realm.NewTypeError("Cannot convert a Symbol value to a BigInt")),
            JsValueKind.Object => throw new JsThrow(realm.NewTypeError("Cannot convert Object to a BigInt")),
            _ => throw new JsThrow(realm.NewTypeError("Cannot convert value to a BigInt")),
        };
    }

    public static BigInteger NumberToBigInt(JsRealm realm, double n)
    {
        if (double.IsNaN(n) || double.IsInfinity(n) || n != Math.Truncate(n))
        {
            throw new JsThrow(realm.NewRangeError($"The number {n} cannot be converted to a BigInt because it is not an integer"));
        }

        return new BigInteger(n);
    }

    public static BigInteger ParseStringToBigInt(JsRealm realm, string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0)
        {
            return BigInteger.Zero;
        }

        var negative = false;
        var i = 0;
        if (s[0] == '+' || s[0] == '-')
        {
            if (s.Length > 2 && s[1] == '0' && (s[2] is 'x' or 'X' or 'b' or 'B' or 'o' or 'O'))
            {
                throw new JsThrow(realm.NewSyntaxError($"Cannot convert {raw} to a BigInt"));
            }

            negative = s[0] == '-';
            i = 1;
        }
        if (i + 1 < s.Length && s[i] == '0')
        {
            var prefix = s[i + 1];
            if (prefix == 'x' || prefix == 'X')
            {
                return ParseRadix(realm, raw, s[(i + 2)..], 16, negative);
            }

            if (prefix == 'b' || prefix == 'B')
            {
                return ParseRadix(realm, raw, s[(i + 2)..], 2, negative);
            }

            if (prefix == 'o' || prefix == 'O')
            {
                return ParseRadix(realm, raw, s[(i + 2)..], 8, negative);
            }
        }
        var digits = s[i..];
        if (digits.Length == 0)
        {
            throw new JsThrow(realm.NewSyntaxError($"Cannot convert {raw} to a BigInt"));
        }

        foreach (var c in digits)
        {
            if (c < '0' || c > '9')
            {
                throw new JsThrow(realm.NewSyntaxError($"Cannot convert {raw} to a BigInt"));
            }
        }

        var v = BigInteger.Parse(digits,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture);
        return negative ? -v : v;
    }

    private static BigInteger ParseRadix(JsRealm realm, string raw, string digits, int radix, bool negative)
    {
        if (digits.Length == 0)
        {
            throw new JsThrow(realm.NewSyntaxError($"Cannot convert {raw} to a BigInt"));
        }

        var v = BigInteger.Zero;
        foreach (var c in digits)
        {
            var d = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
            if (d < 0 || d >= radix)
            {
                throw new JsThrow(realm.NewSyntaxError($"Cannot convert {raw} to a BigInt"));
            }

            v = v * radix + d;
        }
        return negative ? -v : v;
    }

    public static BigInteger AsIntN(JsRealm realm, int bits, BigInteger value)
    {
        if (bits < 0)
        {
            throw new JsThrow(realm.NewRangeError("Invalid bit count"));
        }

        if (bits == 0)
        {
            return BigInteger.Zero;
        }

        var mod = BigInteger.One << bits;
        var rem = ((value % mod) + mod) % mod;
        var signBit = BigInteger.One << (bits - 1);
        return rem >= signBit ? rem - mod : rem;
    }

    public static BigInteger AsUintN(JsRealm realm, int bits, BigInteger value)
    {
        if (bits < 0)
        {
            throw new JsThrow(realm.NewRangeError("Invalid bit count"));
        }

        if (bits == 0)
        {
            return BigInteger.Zero;
        }

        var mod = BigInteger.One << bits;
        var rem = value % mod;
        if (rem.Sign < 0)
        {
            rem += mod;
        }

        return rem;
    }

    public static string ToRadixString(BigInteger value, int radix)
    {
        if (radix == 10)
        {
            return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (value.IsZero)
        {
            return "0";
        }

        var negative = value.Sign < 0;
        var v = negative ? -value : value;
        const string Digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var sb = new System.Text.StringBuilder();
        while (!v.IsZero)
        {
            v = BigInteger.DivRem(v, radix, out var rem);
            sb.Append(Digits[(int)rem]);
        }
        if (negative)
        {
            sb.Append('-');
        }

        var chars = new char[sb.Length];
        for (var i = 0; i < sb.Length; i++)
        {
            chars[i] = sb[sb.Length - 1 - i];
        }

        return new string(chars);
    }
}
