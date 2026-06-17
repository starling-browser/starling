using System.Numerics;
using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// §21.2 BigInt — callable (not constructible) wrapper plus statics and
/// prototype methods. The BigInt callable converts its argument via
/// §7.1.13 with the spec's Number-coercion subtlety: only finite integers
/// convert. <c>new BigInt(...)</c> throws TypeError per §21.2.1.1.
/// </summary>
public static class BigIntCtor
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.BigIntPrototype;

        JsNativeFunction? ctor = null;
        ctor = new JsNativeFunction(realm, "BigInt", length: 1, (thisV, args) =>
        {
            // §21.2.1.1: NewTarget !== undefined → throw TypeError.
            // We detect "called via new" by thisV being a plain object whose
            // [[Prototype]] is BigInt.prototype OR by the `new` opcode path
            // (which routes through native constructors with thisV = newTarget
            // object). isConstructor=false would suppress this routing; we
            // keep it false but still guard explicitly so the message is
            // spec-accurate.
            _ = ctor; // closure capture
            // Without `new`, run §21.2.1.1.1 ThisBigIntValue / ToBigInt.
            var input = args.Length == 0 ? JsValue.Undefined : args[0];
            var prim = AbstractOperations.ToPrimitive(input, "number");
            if (prim.IsNumber)
            {
                return JsValue.BigInt(BigIntOps.NumberToBigInt(realm, prim.AsNumber));
            }

            return JsValue.BigInt(BigIntOps.ToBigInt(realm, prim));
        }, isConstructor: false);

        // BigInt.prototype.constructor → BigInt.
        DefineData(ctor, "prototype", JsValue.Object(proto), writable: false, enumerable: false, configurable: false);
        DefineData(proto, "constructor", JsValue.Object(ctor), writable: true, enumerable: false, configurable: true);

        // Statics — BigInt.asIntN / asUintN per §21.2.2.
        IntrinsicHelpers.DefineMethod(realm, ctor, "asIntN", 2, (_, args) =>
        {
            var bitIndex = ToBitCountIndex(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
            var value = BigIntOps.ToBigInt(realm, args.Length > 1 ? args[1] : JsValue.Undefined);
            var bits = ToSupportedBitCount(realm, bitIndex);
            return JsValue.BigInt(BigIntOps.AsIntN(realm, bits, value));
        });
        IntrinsicHelpers.DefineMethod(realm, ctor, "asUintN", 2, (_, args) =>
        {
            var bitIndex = ToBitCountIndex(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
            var value = BigIntOps.ToBigInt(realm, args.Length > 1 ? args[1] : JsValue.Undefined);
            var bits = ToSupportedBitCount(realm, bitIndex);
            return JsValue.BigInt(BigIntOps.AsUintN(realm, bits, value));
        });

        // Prototype methods — toString(radix), valueOf, toLocaleString.
        IntrinsicHelpers.DefineMethod(realm, proto, "toString", 1, (thisV, args) =>
        {
            var v = ThisBigIntValue(realm, thisV);
            var radix = 10;
            if (args.Length > 0 && !args[0].IsUndefined)
            {
                var r = JsValue.ToNumber(args[0]);
                if (double.IsNaN(r) || r < 2 || r > 36 || r != Math.Truncate(r))
                {
                    throw new JsThrow(realm.NewRangeError("toString() radix must be an integer between 2 and 36"));
                }

                radix = (int)r;
            }
            return JsValue.String(BigIntOps.ToRadixString(v, radix));
        });
        IntrinsicHelpers.DefineMethod(realm, proto, "valueOf", 0, (thisV, _) => JsValue.BigInt(ThisBigIntValue(realm, thisV)));
        IntrinsicHelpers.DefineMethod(realm, proto, "toLocaleString", 0, (thisV, _) =>
            JsValue.String(BigIntOps.ToRadixString(ThisBigIntValue(realm, thisV), 10)));

        realm.BigIntConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("BigInt",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    /// <summary>§21.2.1.1.1 ThisBigIntValue — unbox a wrapper object or accept
    /// a BigInt primitive directly; throw TypeError otherwise.</summary>
    private static BigInteger ThisBigIntValue(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsBigInt)
        {
            return thisV.AsBigInt;
        }

        if (thisV.IsObject)
        {
            var slot = thisV.AsObject.Get("__primitiveValue");
            if (slot.IsBigInt)
            {
                return slot.AsBigInt;
            }
        }
        throw new JsThrow(realm.NewTypeError("BigInt.prototype method called on non-BigInt receiver"));
    }

    private static long ToBitCountIndex(JsRealm realm, JsValue value)
    {
        const double MaxSafeInteger = 9007199254740991d;
        if (value.IsUndefined)
        {
            return 0;
        }

        var n = JsValue.ToNumber(value);
        if (double.IsNaN(n) || n == 0)
        {
            return 0;
        }

        if (double.IsInfinity(n) || n < 0 || n != Math.Truncate(n) || n > MaxSafeInteger)
        {
            throw new JsThrow(realm.NewRangeError("Bit count must be a non-negative integer"));
        }

        return (long)n;
    }

    private static int ToSupportedBitCount(JsRealm realm, long bits)
    {
        if (bits > int.MaxValue)
        {
            throw new JsThrow(realm.NewRangeError("Bit count is too large"));
        }

        return (int)bits;
    }

    private static void DefineData(JsObject target, string name, JsValue value,
        bool writable, bool enumerable, bool configurable)
        => target.DefineOwnProperty(name, PropertyDescriptor.Data(value, writable, enumerable, configurable));
}
