using System.Globalization;
using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>ECMA-262 §25.1 ArrayBuffer Objects.</summary>
public static class ArrayBufferCtor
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.ArrayBufferPrototype;

        JsNativeFunction? ctor = null;
        ctor = new JsNativeFunction("ArrayBuffer", (newTarget, args) =>
        {
            // §25.1.4.1 step 1: requires `new`. OrdinaryCreateFromConstructor
            // picks the prototype from new.target for `class B extends ArrayBuffer {}`.
            if (!IntrinsicHelpers.IsConstructInvocation(newTarget))
            {
                throw new JsThrow(realm.NewTypeError("ArrayBuffer constructor requires 'new'"));
            }

            var length = ToIndex(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
            var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
            return JsValue.Object(new JsArrayBuffer(instProto, length));
        }, isConstructor: true);
        ctor.SetPrototypeOf(realm.FunctionPrototype);
        DefineData(ctor, "prototype", JsValue.Object(proto), false, false, false);
        DefineData(ctor, "name", JsValue.String("ArrayBuffer"), false, false, true);
        DefineData(ctor, "length", JsValue.Number(1), false, false, true);
        ctor.DefineOwnProperty(SymbolCtor.Species,
            PropertyDescriptor.Accessor(new JsNativeFunction("get [Symbol.species]", (thisV, _) => thisV), null));
        DefineData(proto, "constructor", JsValue.Object(ctor), true, false, true);
        proto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("ArrayBuffer"), writable: false, enumerable: false, configurable: true));

        DefineMethod(proto, "slice", (thisV, args) => Slice(realm, thisV, args), 2);
        DefineMethod(ctor, "isView", (_, args) => JsValue.Boolean(args.Length > 0 && IsView(args[0])), 1);

        realm.GlobalObject.DefineOwnProperty("ArrayBuffer", PropertyDescriptor.Data(JsValue.Object(ctor), true, false, true));
    }

    private static JsValue Slice(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var buffer = thisV.IsObject && thisV.AsObject is JsArrayBuffer b
            ? b
            : throw new JsThrow(realm.NewTypeError("ArrayBuffer.prototype.slice called on incompatible receiver"));
        var len = buffer.ByteLength;
        var relativeStart = ToIntegerOrInfinity(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var first = relativeStart < 0 ? Math.Max(len + relativeStart, 0) : Math.Min(relativeStart, len);
        var relativeEnd = args.Length > 1 && !args[1].IsUndefined ? ToIntegerOrInfinity(realm, args[1]) : len;
        var final = relativeEnd < 0 ? Math.Max(len + relativeEnd, 0) : Math.Min(relativeEnd, len);
        return JsValue.Object(buffer.Slice(realm.ArrayBufferPrototype, (int)first, (int)final));
    }

    private static bool IsView(JsValue value)
        => value.IsObject && (value.AsObject is JsTypedArray || value.AsObject.Get("__DataView").Equals(JsValue.True));

    internal static int ToIndex(JsRealm realm, JsValue value)
    {
        var n = Number(realm, value);
        if (double.IsNaN(n) || n == 0)
        {
            return 0;
        }

        n = Math.Truncate(n);
        if (n < 0)
        {
            throw new JsThrow(realm.NewRangeError("ArrayBuffer length is out of range"));
        }

        if (!double.IsFinite(n) || n > int.MaxValue)
        {
            throw new JsThrow(realm.NewRangeError("ArrayBuffer length is out of range"));
        }

        return (int)n;
    }

    internal static double ToIntegerOrInfinity(JsValue value)
    {
        var n = Number(value);
        return ToIntegerOrInfinityCore(n);
    }

    internal static double ToIntegerOrInfinity(JsRealm realm, JsValue value)
    {
        var n = Number(realm, value);
        return ToIntegerOrInfinityCore(n);
    }

    private static double ToIntegerOrInfinityCore(double n)
    {
        if (double.IsNaN(n) || n == 0)
        {
            return 0;
        }

        if (double.IsInfinity(n))
        {
            return n;
        }

        return Math.Truncate(n);
    }

    internal static double Number(JsValue value)
        => value.IsObject ? JsValue.ToNumber(AbstractOperations.ToPrimitive(value, "number")) : JsValue.ToNumber(value);

    internal static double Number(JsRealm realm, JsValue value)
    {
        try
        {
            return Number(value);
        }
        catch (InvalidOperationException ex)
        {
            throw new JsThrow(realm.NewTypeError(ex.Message));
        }
    }

    internal static string IndexKey(int i) => i.ToString(CultureInfo.InvariantCulture);

    internal static void DefineMethod(JsObject target, string name, Func<JsValue, JsValue[], JsValue> body, int length)
    {
        var fn = new JsNativeFunction(name, body, isConstructor: false);
        DefineData(fn, "name", JsValue.String(name), false, false, true);
        DefineData(fn, "length", JsValue.Number(length), false, false, true);
        target.DefineOwnProperty(name, PropertyDescriptor.BuiltinMethod(JsValue.Object(fn)));
    }

    internal static void DefineData(JsObject target, string name, JsValue value, bool writable, bool enumerable, bool configurable)
        => target.DefineOwnProperty(name, PropertyDescriptor.Data(value, writable, enumerable, configurable));
}
