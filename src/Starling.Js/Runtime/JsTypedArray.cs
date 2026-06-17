using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;

namespace Starling.Js.Runtime;

public enum JsTypedArrayKind
{
    Int8,
    Uint8,
    Uint8Clamped,
    Int16,
    Uint16,
    Int32,
    Uint32,
    Float32,
    Float64,
    BigInt64,
    BigUint64,
}

/// <summary>ECMA-262 §25.2 TypedArray exotic object backed by an ArrayBuffer.</summary>
public sealed class JsTypedArray : JsObject
{
    public JsTypedArray(JsObject? prototype, JsTypedArrayKind kind, JsArrayBuffer buffer, int byteOffset, int length) : base(prototype)
    {
        DisableInlineCache();
        Kind = kind;
        Buffer = buffer;
        ByteOffset = byteOffset;
        Length = length;
        DefineOwnProperty("buffer", PropertyDescriptor.Data(JsValue.Object(buffer), false, false, true));
        DefineOwnProperty("byteOffset", PropertyDescriptor.Data(JsValue.Number(byteOffset), false, false, true));
        DefineOwnProperty("byteLength", PropertyDescriptor.Data(JsValue.Number(length * BytesPerElement), false, false, true));
        DefineOwnProperty("length", PropertyDescriptor.Data(JsValue.Number(length), false, false, true));
        DefineOwnProperty("BYTES_PER_ELEMENT", PropertyDescriptor.Data(JsValue.Number(BytesPerElement), false, false, true));
    }

    public JsTypedArrayKind Kind { get; }
    public JsArrayBuffer Buffer { get; }
    public int ByteOffset { get; }
    public int Length { get; }
    public int BytesPerElement => BytesPerElementOf(Kind);
    public int ByteLength => Length * BytesPerElement;

    public static int BytesPerElementOf(JsTypedArrayKind kind) => kind switch
    {
        JsTypedArrayKind.Int8 or JsTypedArrayKind.Uint8 or JsTypedArrayKind.Uint8Clamped => 1,
        JsTypedArrayKind.Int16 or JsTypedArrayKind.Uint16 => 2,
        JsTypedArrayKind.Int32 or JsTypedArrayKind.Uint32 or JsTypedArrayKind.Float32 => 4,
        _ => 8,
    };

    /// <summary>§23.2.3.34 [[TypedArrayName]] — concrete constructor name
    /// (e.g. "Uint8Array", "Float32Array"). Read by the
    /// <c>%TypedArray%.prototype[@@toStringTag]</c> accessor.</summary>
    public string ConstructorName => Kind switch
    {
        JsTypedArrayKind.Int8 => "Int8Array",
        JsTypedArrayKind.Uint8 => "Uint8Array",
        JsTypedArrayKind.Uint8Clamped => "Uint8ClampedArray",
        JsTypedArrayKind.Int16 => "Int16Array",
        JsTypedArrayKind.Uint16 => "Uint16Array",
        JsTypedArrayKind.Int32 => "Int32Array",
        JsTypedArrayKind.Uint32 => "Uint32Array",
        JsTypedArrayKind.Float32 => "Float32Array",
        JsTypedArrayKind.Float64 => "Float64Array",
        JsTypedArrayKind.BigInt64 => "BigInt64Array",
        JsTypedArrayKind.BigUint64 => "BigUint64Array",
        _ => throw new InvalidOperationException(),
    };

    public override JsValue Get(string name)
    {
        if (TryIndex(name, out var index))
        {
            return index >= 0 && index < Length ? GetElement(index) : JsValue.Undefined;
        }

        return base.Get(name);
    }

    public override void Set(string name, JsValue value)
    {
        if (TryIndex(name, out var index))
        {
            if (index >= 0 && index < Length)
            {
                SetElement(index, value);
            }

            return;
        }
        base.Set(name, value);
    }

    public JsValue GetElement(int index)
    {
        var offset = CheckedOffset(index);
        var bytes = Buffer.GetSpan();
        var span = bytes[offset..];
        return Kind switch
        {
            JsTypedArrayKind.Int8 => JsValue.Number(unchecked((sbyte)bytes[offset])),
            JsTypedArrayKind.Uint8 or JsTypedArrayKind.Uint8Clamped => JsValue.Number(bytes[offset]),
            JsTypedArrayKind.Int16 => JsValue.Number(BinaryPrimitives.ReadInt16LittleEndian(span)),
            JsTypedArrayKind.Uint16 => JsValue.Number(BinaryPrimitives.ReadUInt16LittleEndian(span)),
            JsTypedArrayKind.Int32 => JsValue.Number(BinaryPrimitives.ReadInt32LittleEndian(span)),
            JsTypedArrayKind.Uint32 => JsValue.Number(BinaryPrimitives.ReadUInt32LittleEndian(span)),
            JsTypedArrayKind.Float32 => JsValue.Number(BinaryPrimitives.ReadSingleLittleEndian(span)),
            JsTypedArrayKind.Float64 => JsValue.Number(BinaryPrimitives.ReadDoubleLittleEndian(span)),
            JsTypedArrayKind.BigInt64 => JsValue.BigInt(new BigInteger(BinaryPrimitives.ReadInt64LittleEndian(span))),
            JsTypedArrayKind.BigUint64 => JsValue.BigInt(new BigInteger(BinaryPrimitives.ReadUInt64LittleEndian(span))),
            _ => JsValue.Undefined,
        };
    }

    public void SetElement(int index, JsValue value, JsRealm? realm = null)
    {
        var offset = CheckedOffset(index);
        var bytes = Buffer.GetSpan();
        var span = bytes[offset..];
        var n = Kind is JsTypedArrayKind.BigInt64 or JsTypedArrayKind.BigUint64 ? 0 : ToNumber(value, realm);
        switch (Kind)
        {
            case JsTypedArrayKind.Int8:
                bytes[offset] = unchecked((byte)(sbyte)ToInt32(n));
                break;
            case JsTypedArrayKind.Uint8:
                bytes[offset] = unchecked((byte)ToUint32(n));
                break;
            case JsTypedArrayKind.Uint8Clamped:
                bytes[offset] = ToUint8Clamp(n);
                break;
            case JsTypedArrayKind.Int16:
                BinaryPrimitives.WriteInt16LittleEndian(span, unchecked((short)ToInt32(n)));
                break;
            case JsTypedArrayKind.Uint16:
                BinaryPrimitives.WriteUInt16LittleEndian(span, unchecked((ushort)ToUint32(n)));
                break;
            case JsTypedArrayKind.Int32:
                BinaryPrimitives.WriteInt32LittleEndian(span, ToInt32(n));
                break;
            case JsTypedArrayKind.Uint32:
                BinaryPrimitives.WriteUInt32LittleEndian(span, ToUint32(n));
                break;
            case JsTypedArrayKind.Float32:
                BinaryPrimitives.WriteSingleLittleEndian(span, (float)n);
                break;
            case JsTypedArrayKind.Float64:
                BinaryPrimitives.WriteDoubleLittleEndian(span, n);
                break;
            case JsTypedArrayKind.BigInt64:
                BinaryPrimitives.WriteInt64LittleEndian(span, (long)AsIntN(64, ToBigInt(value, realm)));
                break;
            case JsTypedArrayKind.BigUint64:
                BinaryPrimitives.WriteUInt64LittleEndian(span, (ulong)AsUintN(64, ToBigInt(value, realm)));
                break;
        }
    }

    public JsTypedArray CreateSameKind(JsObject? prototype, JsArrayBuffer buffer, int byteOffset, int length)
        => new(prototype, Kind, buffer, byteOffset, length);

    private int CheckedOffset(int index)
    {
        if ((uint)index >= (uint)Length)
        {
            throw new ArgumentOutOfRangeException(nameof(index));
        }

        return ByteOffset + index * BytesPerElement;
    }

    private static bool TryIndex(string name, out int index)
    {
        if (name.Length == 0 || name[0] == '-' || name.Contains('.', StringComparison.Ordinal))
        {
            index = -1;
            return false;
        }
        return int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out index);
    }

    private static double ToNumber(JsValue value, JsRealm? realm)
    {
        try
        {
            if (!value.IsObject)
            {
                return JsValue.ToNumber(value);
            }

            return JsValue.ToNumber(AbstractOperations.ToPrimitive(value, "number"));
        }
        catch (InvalidOperationException ex) when (realm is not null)
        {
            throw new JsThrow(realm.NewTypeError(ex.Message));
        }
    }

    private static BigInteger ToBigInt(JsValue value, JsRealm? realm)
    {
        if (value.IsObject)
        {
            value = AbstractOperations.ToPrimitive(value, "number");
        }

        return value.Kind switch
        {
            JsValueKind.BigInt => value.AsBigInt,
            JsValueKind.Boolean => value.AsBool ? BigInteger.One : BigInteger.Zero,
            JsValueKind.String when realm is not null => BigIntOps.ParseStringToBigInt(realm, value.AsString),
            JsValueKind.String => BigInteger.Parse(value.AsString, CultureInfo.InvariantCulture),
            _ when realm is not null => throw new JsThrow(realm.NewTypeError("Cannot convert value to BigInt")),
            _ => throw new InvalidOperationException("Cannot convert value to BigInt"),
        };
    }

    private static BigInteger AsIntN(int bits, BigInteger value)
    {
        var mod = BigInteger.One << bits;
        var rem = ((value % mod) + mod) % mod;
        var signBit = BigInteger.One << (bits - 1);
        return rem >= signBit ? rem - mod : rem;
    }

    private static BigInteger AsUintN(int bits, BigInteger value)
    {
        var mod = BigInteger.One << bits;
        var rem = value % mod;
        return rem.Sign < 0 ? rem + mod : rem;
    }

    // ECMA-262 §7.1.6 ToInt32: truncate, modulo 2^32, reinterpret signed.
    private static int ToInt32(double n)
    {
        if (double.IsNaN(n) || double.IsInfinity(n) || n == 0)
        {
            return 0;
        }

        var int32bit = n < 0
            ? 4294967296d - (Math.Abs(Math.Truncate(n)) % 4294967296d)
            : Math.Truncate(n) % 4294967296d;
        return unchecked((int)(uint)int32bit);
    }

    // ECMA-262 §7.1.7 ToUint32: truncate and wrap modulo 2^32.
    private static uint ToUint32(double n)
    {
        if (double.IsNaN(n) || double.IsInfinity(n) || n == 0)
        {
            return 0;
        }

        var int32bit = n < 0
            ? 4294967296d - (Math.Abs(Math.Truncate(n)) % 4294967296d)
            : Math.Truncate(n) % 4294967296d;
        return (uint)int32bit;
    }

    // ECMA-262 §7.1.12 ToUint8Clamp: clamp, then round half to even.
    private static byte ToUint8Clamp(double n)
    {
        if (double.IsNaN(n) || n <= 0)
        {
            return 0;
        }

        if (n >= 255)
        {
            return 255;
        }

        var f = Math.Floor(n);
        if (n < f + 0.5)
        {
            return (byte)f;
        }

        if (n > f + 0.5)
        {
            return (byte)(f + 1);
        }

        return (byte)(((int)f % 2) == 0 ? f : f + 1);
    }

    public override string ToString() => $"[object {Kind}Array]";
}
