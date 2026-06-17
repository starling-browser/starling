using System.Globalization;
using System.Numerics;

namespace Starling.Js.Runtime;

/// <summary>
/// The seven JS primitive value types plus Object. Backing for
/// <see cref="JsValue"/>.
/// </summary>
public enum JsValueKind : byte
{
    Undefined = 0,
    Null,
    Boolean,
    Number,
    String,
    Object,
    BigInt,
    Symbol,
}

/// <summary>
/// Tagged JS value. Struct-backed to avoid boxing for primitives. NaN-boxing
/// is a v2 optimization (wp:M7-01); for v1 the simple struct layout is fine.
/// </summary>
/// <remarks>
/// Spec abstract operations (ToNumber, ToString, ToBoolean, abstract+strict
/// equality) live on this type as static methods so the VM dispatch loop can
/// inline them. BigInt values box a <see cref="BigInteger"/> into the
/// <c>_ref</c> slot; BigIntegers are immutable so reference sharing across
/// struct copies is safe.
/// </remarks>
public readonly struct JsValue : IEquatable<JsValue>
{
    public readonly JsValueKind Kind;
    private readonly double _num;
    private readonly object? _ref;

    private JsValue(JsValueKind kind, double num = 0, object? @ref = null)
    {
        Kind = kind;
        _num = num;
        _ref = @ref;
    }

    public static readonly JsValue Undefined = default;
    public static readonly JsValue Null = new(JsValueKind.Null);
    public static readonly JsValue True = new(JsValueKind.Boolean, 1);
    public static readonly JsValue False = new(JsValueKind.Boolean, 0);
    public static readonly JsValue NaN = new(JsValueKind.Number, double.NaN);
    public static readonly JsValue Zero = new(JsValueKind.Number, 0);

    public static JsValue Number(double d) => new(JsValueKind.Number, d);
    public static JsValue Boolean(bool b) => b ? True : False;
    public static JsValue String(string s) => new(JsValueKind.String, 0, s);
    public static JsValue Object(JsObject o) => new(JsValueKind.Object, 0, o);
    /// <summary>Build a BigInt from a <see cref="BigInteger"/>. The integer
    /// is boxed once into the <c>_ref</c> slot; struct copies share it
    /// (BigInteger is immutable).</summary>
    public static JsValue BigInt(BigInteger v) => new(JsValueKind.BigInt, 0, (object)v);
    /// <summary>Legacy overload — parse decimal digits into a BigInteger.</summary>
    public static JsValue BigInt(string digits) => new(JsValueKind.BigInt, 0,
        (object)BigInteger.Parse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture));
    public static JsValue Symbol(JsSymbol symbol) => new(JsValueKind.Symbol, 0, symbol);

    public bool IsUndefined => Kind == JsValueKind.Undefined;
    public bool IsNull => Kind == JsValueKind.Null;
    public bool IsNullish => Kind is JsValueKind.Undefined or JsValueKind.Null;
    public bool IsBoolean => Kind == JsValueKind.Boolean;
    public bool IsNumber => Kind == JsValueKind.Number;
    public bool IsString => Kind == JsValueKind.String;
    public bool IsObject => Kind == JsValueKind.Object;
    public bool IsSymbol => Kind == JsValueKind.Symbol;
    public bool IsBigInt => Kind == JsValueKind.BigInt;

    public double AsNumber => Kind == JsValueKind.Number
        ? _num
        : throw new InvalidOperationException($"value is {Kind}, not Number");
    public bool AsBool => Kind == JsValueKind.Boolean
        ? _num != 0
        : throw new InvalidOperationException($"value is {Kind}, not Boolean");
    public string AsString => Kind == JsValueKind.String
        ? (string)_ref!
        : throw new InvalidOperationException($"value is {Kind}, not String");
    public JsObject AsObject => Kind == JsValueKind.Object
        ? (JsObject)_ref!
        : throw new InvalidOperationException($"value is {Kind}, not Object");
    public JsSymbol AsSymbol => Kind == JsValueKind.Symbol
        ? (JsSymbol)_ref!
        : throw new InvalidOperationException($"value is {Kind}, not Symbol");
    public BigInteger AsBigInt => Kind == JsValueKind.BigInt
        ? (BigInteger)_ref!
        : throw new InvalidOperationException($"value is {Kind}, not BigInt");

    // -----------------------------------------------------------------------
    // ES2024 abstract operations
    // -----------------------------------------------------------------------

    /// <summary>ToBoolean per §7.1.2.</summary>
    public static bool ToBoolean(JsValue v) => v.Kind switch
    {
        JsValueKind.Undefined => false,
        JsValueKind.Null => false,
        JsValueKind.Boolean => v._num != 0,
        JsValueKind.Number => v._num != 0 && !double.IsNaN(v._num),
        JsValueKind.String => ((string)v._ref!).Length > 0,
        JsValueKind.Object => true,
        JsValueKind.BigInt => !((BigInteger)v._ref!).IsZero,
        JsValueKind.Symbol => true,
        _ => false,
    };

    /// <summary>ToNumber per §7.1.4. BigInt → Number throws TypeError per
    /// spec; surfaced here as InvalidOperationException for host callers,
    /// lifted to a JS TypeError by the VM at the operator boundary.</summary>
    public static double ToNumber(JsValue v) => v.Kind switch
    {
        JsValueKind.Undefined => double.NaN,
        JsValueKind.Null => 0,
        JsValueKind.Boolean => v._num,
        JsValueKind.Number => v._num,
        JsValueKind.String => ParseNumber((string)v._ref!),
        JsValueKind.Object => double.NaN, // simplified: real spec calls ToPrimitive then ToNumber
        JsValueKind.BigInt => throw new InvalidOperationException("can't convert BigInt to Number"),
        JsValueKind.Symbol => throw new InvalidOperationException("can't convert Symbol to Number"),
        _ => double.NaN,
    };

    private static double ParseNumber(string s)
    {
        s = s.Trim();
        if (s.Length == 0)
        {
            return 0;
        }

        if (TryParsePrefixedInteger(s, out var integer))
        {
            return (double)integer;
        }

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        {
            return d;
        }

        return double.NaN;
    }

    private static bool TryParsePrefixedInteger(string s, out BigInteger value)
    {
        value = default;
        if (s.Length < 3 || s[0] != '0')
        {
            return false;
        }

        var radix = s[1] switch
        {
            'x' or 'X' => 16,
            'o' or 'O' => 8,
            'b' or 'B' => 2,
            _ => 0,
        };
        if (radix == 0)
        {
            return false;
        }

        return TryParseIntegerDigits(s.AsSpan(2), radix, out value);
    }

    internal static bool TryStringToBigInt(string s, out BigInteger value)
    {
        s = s.Trim();
        if (s.Length == 0)
        {
            value = BigInteger.Zero;
            return true;
        }

        if (TryParsePrefixedInteger(s, out value))
        {
            return true;
        }

        return BigInteger.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseIntegerDigits(ReadOnlySpan<char> digits, int radix, out BigInteger value)
    {
        value = BigInteger.Zero;
        if (digits.Length == 0)
        {
            return false;
        }

        foreach (var ch in digits)
        {
            int digit;
            if (ch is >= '0' and <= '9')
            {
                digit = ch - '0';
            }
            else if (ch is >= 'a' and <= 'f')
            {
                digit = ch - 'a' + 10;
            }
            else if (ch is >= 'A' and <= 'F')
            {
                digit = ch - 'A' + 10;
            }
            else
            {
                return false;
            }

            if (digit >= radix)
            {
                return false;
            }

            value = value * radix + digit;
        }

        return true;
    }

    /// <summary>ToString per §7.1.17.</summary>
    public static string ToStringValue(JsValue v) => v.Kind switch
    {
        JsValueKind.Undefined => "undefined",
        JsValueKind.Null => "null",
        JsValueKind.Boolean => v._num != 0 ? "true" : "false",
        JsValueKind.Number => NumberToString(v._num),
        JsValueKind.String => (string)v._ref!,
        JsValueKind.Object => "[object Object]", // simplified
        JsValueKind.BigInt => ((BigInteger)v._ref!).ToString(CultureInfo.InvariantCulture),
        JsValueKind.Symbol => ((JsSymbol)v._ref!).DescriptiveString,
        _ => "",
    };

    private static string NumberToString(double d)
    {
        if (double.IsNaN(d))
        {
            return "NaN";
        }

        if (double.IsPositiveInfinity(d))
        {
            return "Infinity";
        }

        if (double.IsNegativeInfinity(d))
        {
            return "-Infinity";
        }

        if (d == 0)
        {
            return "0";
        }

        var sign = string.Empty;
        if (d < 0)
        {
            sign = "-";
            d = -d;
        }

        // Fast path for small integers that fit without exponent rewriting.
        if (d == Math.Truncate(d) && d <= long.MaxValue)
        {
            return sign + ((long)d).ToString(CultureInfo.InvariantCulture);
        }

        var raw = d.ToString("R", CultureInfo.InvariantCulture);
        var exponentPos = raw.IndexOf('E');
        if (exponentPos < 0)
        {
            exponentPos = raw.IndexOf('e');
        }

        if (exponentPos < 0)
        {
            return sign + raw;
        }

        return sign + FormatEcmaScientific(raw, exponentPos);
    }

    private static string FormatEcmaScientific(string raw, int exponentPos)
    {
        var mantissa = raw[..exponentPos];
        var exponent = int.Parse(raw[(exponentPos + 1)..],
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture);
        var dot = mantissa.IndexOf('.');
        var digits = dot < 0
            ? mantissa
            : mantissa[..dot] + mantissa[(dot + 1)..];
        var decimalPoint = exponent + 1;

        if (decimalPoint is > 0 and <= 21)
        {
            if (digits.Length <= decimalPoint)
            {
                return digits + new string('0', decimalPoint - digits.Length);
            }

            return digits[..decimalPoint] + "." + digits[decimalPoint..];
        }

        if (decimalPoint is <= 0 and > -6)
        {
            return "0." + new string('0', -decimalPoint) + digits;
        }

        var exponentText = exponent >= 0
            ? "+" + exponent.ToString(CultureInfo.InvariantCulture)
            : exponent.ToString(CultureInfo.InvariantCulture);
        return digits.Length == 1
            ? digits + "e" + exponentText
            : digits[0] + "." + digits[1..] + "e" + exponentText;
    }

    /// <summary>Strict equality per §7.2.16 (no coercion).</summary>
    public static bool StrictEquals(JsValue a, JsValue b)
    {
        if (a.Kind != b.Kind)
        {
            return false;
        }

        return a.Kind switch
        {
            JsValueKind.Undefined => true,
            JsValueKind.Null => true,
            JsValueKind.Boolean => a._num == b._num,
            JsValueKind.Number =>
                double.IsNaN(a._num) || double.IsNaN(b._num)
                    ? false
                    : a._num == b._num,
            JsValueKind.String => string.Equals((string)a._ref!, (string)b._ref!, StringComparison.Ordinal),
            JsValueKind.Object => ReferenceEquals(a._ref, b._ref),
            JsValueKind.BigInt => ((BigInteger)a._ref!).Equals((BigInteger)b._ref!),
            JsValueKind.Symbol => ReferenceEquals(a._ref, b._ref),
            _ => false,
        };
    }

    /// <summary>Abstract (loose) equality per §7.2.15. Performs type coercion.</summary>
    public static bool AbstractEquals(JsValue a, JsValue b)
    {
        if (a.Kind == b.Kind)
        {
            return StrictEquals(a, b);
        }
        // null == undefined.
        if (a.IsNullish && b.IsNullish)
        {
            return true;
        }
        // Number == String → coerce string to number.
        if (a.IsNumber && b.IsString)
        {
            return !double.IsNaN(b._num /* unused */) && a._num == ParseNumber((string)b._ref!);
        }

        if (a.IsString && b.IsNumber)
        {
            return ParseNumber((string)a._ref!) == b._num;
        }
        // §7.2.15 — BigInt cross-type loose equality (Number / String).
        if (a.IsBigInt && b.IsNumber)
        {
            return BigIntEqualsNumber((BigInteger)a._ref!, b._num);
        }

        if (a.IsNumber && b.IsBigInt)
        {
            return BigIntEqualsNumber((BigInteger)b._ref!, a._num);
        }

        if (a.IsBigInt && b.IsString)
        {
            return BigIntEqualsString((BigInteger)a._ref!, (string)b._ref!);
        }

        if (a.IsString && b.IsBigInt)
        {
            return BigIntEqualsString((BigInteger)b._ref!, (string)a._ref!);
        }
        // Boolean → Number (either side).
        if (a.IsBoolean)
        {
            return AbstractEquals(Number(a._num), b);
        }

        if (b.IsBoolean)
        {
            return AbstractEquals(a, Number(b._num));
        }
        // Number-or-String == Object → coerce object to primitive (simplified: false).
        return false;
    }

    /// <summary>§7.2.15 BigInt == Number step: equal only for a finite,
    /// integer-valued Number with the same value.</summary>
    private static bool BigIntEqualsNumber(BigInteger b, double n)
    {
        if (double.IsNaN(n) || double.IsInfinity(n))
        {
            return false;
        }

        if (n != Math.Truncate(n))
        {
            return false;
        }

        return b == new BigInteger(n);
    }

    /// <summary>§7.2.15 BigInt == String step: equal when StringToBigInt
    /// successfully parses the string to the same value.</summary>
    private static bool BigIntEqualsString(BigInteger b, string s)
    {
        if (!TryStringToBigInt(s, out var parsed))
        {
            return false;
        }

        return b == parsed;
    }

    public bool Equals(JsValue other) => StrictEquals(this, other);
    public override bool Equals(object? obj) => obj is JsValue v && Equals(v);
    public override int GetHashCode() => HashCode.Combine(Kind, _num, _ref);

    public override string ToString() => ToStringValue(this);
}
