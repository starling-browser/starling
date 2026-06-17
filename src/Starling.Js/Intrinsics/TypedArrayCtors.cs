using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>ECMA-262 §25.2 TypedArray constructors and shared prototype.</summary>
public static class TypedArrayCtors
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var shared = realm.TypedArrayPrototype;
        InstallSharedPrototype(realm, shared);

        InstallType(realm, "Int8Array", JsTypedArrayKind.Int8);
        InstallType(realm, "Uint8Array", JsTypedArrayKind.Uint8);
        InstallType(realm, "Uint8ClampedArray", JsTypedArrayKind.Uint8Clamped);
        InstallType(realm, "Int16Array", JsTypedArrayKind.Int16);
        InstallType(realm, "Uint16Array", JsTypedArrayKind.Uint16);
        InstallType(realm, "Int32Array", JsTypedArrayKind.Int32);
        InstallType(realm, "Uint32Array", JsTypedArrayKind.Uint32);
        InstallType(realm, "Float32Array", JsTypedArrayKind.Float32);
        InstallType(realm, "Float64Array", JsTypedArrayKind.Float64);
        InstallType(realm, "BigInt64Array", JsTypedArrayKind.BigInt64);
        InstallType(realm, "BigUint64Array", JsTypedArrayKind.BigUint64);
    }

    private static void InstallType(JsRealm realm, string name, JsTypedArrayKind kind)
    {
        var proto = new JsObject(realm.TypedArrayPrototype);

        JsNativeFunction? ctor = null;
        ctor = new JsNativeFunction(name, (newTarget, args) =>
        {
            // §23.2.5.1 step 1: a TypedArray constructor requires `new` (or a
            // derived super()). Resolve the instance prototype from new.target
            // so `class X extends Uint8Array {}` yields an X-prototyped view.
            if (!IntrinsicHelpers.IsConstructInvocation(newTarget))
            {
                throw new JsThrow(realm.NewTypeError(name + " constructor requires 'new'"));
            }

            var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
            return JsValue.Object(Construct(realm, instProto, kind, args));
        }, isConstructor: true);
        ctor.SetPrototypeOf(realm.FunctionPrototype);
        ArrayBufferCtor.DefineData(ctor, "prototype", JsValue.Object(proto), false, false, false);
        ArrayBufferCtor.DefineData(ctor, "name", JsValue.String(name), false, false, true);
        ArrayBufferCtor.DefineData(ctor, "length", JsValue.Number(3), false, false, true);
        ArrayBufferCtor.DefineData(ctor, "BYTES_PER_ELEMENT", JsValue.Number(JsTypedArray.BytesPerElementOf(kind)), false, false, false);
        ctor.DefineOwnProperty(SymbolCtor.Species,
            PropertyDescriptor.Accessor(new JsNativeFunction("get [Symbol.species]", (thisV, _) => thisV), null));
        ArrayBufferCtor.DefineData(proto, "constructor", JsValue.Object(ctor), true, false, true);
        ArrayBufferCtor.DefineData(proto, "BYTES_PER_ELEMENT", JsValue.Number(JsTypedArray.BytesPerElementOf(kind)), false, false, false);
        // §23.2.3.34: @@toStringTag lives on %TypedArray%.prototype as an
        // accessor, not on each concrete prototype — installed once in
        // InstallSharedPrototype below.
        ArrayBufferCtor.DefineMethod(ctor, "from", (thisV, args) => From(realm, proto, kind, args), 1);
        ArrayBufferCtor.DefineMethod(ctor, "of", (thisV, args) => Of(realm, proto, kind, args), 0);

        realm.GlobalObject.DefineOwnProperty(name, PropertyDescriptor.Data(JsValue.Object(ctor), true, false, true));
    }

    private static JsTypedArray Construct(JsRealm realm, JsObject proto, JsTypedArrayKind kind, JsValue[] args)
    {
        var bpe = JsTypedArray.BytesPerElementOf(kind);
        var first = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (first.IsObject && first.AsObject is JsArrayBuffer buffer)
        {
            var offset = args.Length > 1 ? ArrayBufferCtor.ToIndex(realm, args[1]) : 0;
            if (offset % bpe != 0)
            {
                throw new JsThrow(realm.NewRangeError("TypedArray byteOffset must align to element size"));
            }

            if (offset > buffer.ByteLength)
            {
                throw new JsThrow(realm.NewRangeError("TypedArray byteOffset out of range"));
            }

            var remaining = buffer.ByteLength - offset;
            int viewLength;
            if (args.Length > 2 && !args[2].IsUndefined)
            {
                viewLength = ArrayBufferCtor.ToIndex(realm, args[2]);
                if (viewLength > remaining / bpe)
                {
                    throw new JsThrow(realm.NewRangeError("TypedArray length out of range"));
                }
            }
            else
            {
                if (remaining % bpe != 0)
                {
                    throw new JsThrow(realm.NewRangeError("TypedArray byteLength must align to element size"));
                }

                viewLength = remaining / bpe;
            }
            return new JsTypedArray(proto, kind, buffer, offset, viewLength);
        }

        if (first.IsObject && first.AsObject is JsTypedArray source)
        {
            var target = Allocate(realm, proto, kind, source.Length);
            for (var i = 0; i < source.Length; i++)
            {
                target.SetElement(i, source.GetElement(i), realm);
            }

            return target;
        }

        if (first.IsObject)
        {
            var src = first.AsObject;
            var lenV = src.Get("length");
            if (lenV.IsNumber)
            {
                var len = ArrayBufferCtor.ToIndex(realm, lenV);
                var target = Allocate(realm, proto, kind, len);
                for (var i = 0; i < len; i++)
                {
                    target.SetElement(i, src.Get(ArrayBufferCtor.IndexKey(i)), realm);
                }

                return target;
            }
        }

        var length = ArrayBufferCtor.ToIndex(realm, first);
        return Allocate(realm, proto, kind, length);
    }

    private static JsTypedArray Allocate(JsRealm realm, JsObject proto, JsTypedArrayKind kind, int length)
    {
        var bpe = JsTypedArray.BytesPerElementOf(kind);
        if (length > int.MaxValue / bpe)
        {
            throw new JsThrow(realm.NewRangeError("TypedArray length out of range"));
        }

        return new JsTypedArray(proto, kind, new JsArrayBuffer(realm.ArrayBufferPrototype, length * bpe), 0, length);
    }

    private static JsValue From(JsRealm realm, JsObject proto, JsTypedArrayKind kind, JsValue[] args)
    {
        if (args.Length == 0 || !args[0].IsObject)
        {
            throw new JsThrow(realm.NewTypeError("TypedArray.from source must be array-like"));
        }

        var src = args[0].AsObject;
        var len = ArrayBufferCtor.ToIndex(realm, src.Get("length"));
        var target = Allocate(realm, proto, kind, len);
        var mapFn = args.Length > 1 ? args[1] : JsValue.Undefined;
        var thisArg = args.Length > 2 ? args[2] : JsValue.Undefined;
        if (!mapFn.IsUndefined && !AbstractOperations.IsCallable(mapFn))
        {
            throw new JsThrow(realm.NewTypeError("TypedArray.from mapFn must be callable"));
        }

        for (var i = 0; i < len; i++)
        {
            var v = src.Get(ArrayBufferCtor.IndexKey(i));
            if (!mapFn.IsUndefined)
            {
                v = AbstractOperations.Call(realm.ActiveVm, mapFn, thisArg, new[] { v, JsValue.Number(i) });
            }

            target.SetElement(i, v, realm);
        }
        return JsValue.Object(target);
    }

    private static JsValue Of(JsRealm realm, JsObject proto, JsTypedArrayKind kind, JsValue[] args)
    {
        var target = Allocate(realm, proto, kind, args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            target.SetElement(i, args[i], realm);
        }

        return JsValue.Object(target);
    }

    private static void InstallSharedPrototype(JsRealm realm, JsObject proto)
    {
        ArrayBufferCtor.DefineMethod(proto, "at", (thisV, args) => At(realm, thisV, args), 1);
        ArrayBufferCtor.DefineMethod(proto, "copyWithin", (thisV, args) => CopyWithin(realm, thisV, args), 2);
        ArrayBufferCtor.DefineMethod(proto, "entries", (thisV, args) => Entries(realm, thisV), 0);
        ArrayBufferCtor.DefineMethod(proto, "every", (thisV, args) => Every(realm, thisV, args), 1);
        ArrayBufferCtor.DefineMethod(proto, "fill", (thisV, args) => Fill(realm, thisV, args), 1);
        ArrayBufferCtor.DefineMethod(proto, "filter", (thisV, args) => Filter(realm, thisV, args), 1);
        ArrayBufferCtor.DefineMethod(proto, "find", (thisV, args) => Find(realm, thisV, args, last: false, indexOnly: false), 1);
        ArrayBufferCtor.DefineMethod(proto, "findIndex", (thisV, args) => Find(realm, thisV, args, last: false, indexOnly: true), 1);
        ArrayBufferCtor.DefineMethod(proto, "findLast", (thisV, args) => Find(realm, thisV, args, last: true, indexOnly: false), 1);
        ArrayBufferCtor.DefineMethod(proto, "findLastIndex", (thisV, args) => Find(realm, thisV, args, last: true, indexOnly: true), 1);
        ArrayBufferCtor.DefineMethod(proto, "forEach", (thisV, args) => ForEach(realm, thisV, args), 1);
        ArrayBufferCtor.DefineMethod(proto, "includes", (thisV, args) => Includes(realm, thisV, args), 1);
        ArrayBufferCtor.DefineMethod(proto, "indexOf", (thisV, args) => IndexOf(realm, thisV, args, last: false), 1);
        ArrayBufferCtor.DefineMethod(proto, "join", (thisV, args) => Join(realm, thisV, args), 1);
        ArrayBufferCtor.DefineMethod(proto, "keys", (thisV, args) => Keys(realm, thisV), 0);
        ArrayBufferCtor.DefineMethod(proto, "lastIndexOf", (thisV, args) => IndexOf(realm, thisV, args, last: true), 1);
        ArrayBufferCtor.DefineMethod(proto, "map", (thisV, args) => Map(realm, thisV, args), 1);
        ArrayBufferCtor.DefineMethod(proto, "reduce", (thisV, args) => Reduce(realm, thisV, args, right: false), 1);
        ArrayBufferCtor.DefineMethod(proto, "reduceRight", (thisV, args) => Reduce(realm, thisV, args, right: true), 1);
        ArrayBufferCtor.DefineMethod(proto, "reverse", (thisV, args) => Reverse(realm, thisV), 0);
        ArrayBufferCtor.DefineMethod(proto, "set", (thisV, args) => Set(realm, thisV, args), 1);
        ArrayBufferCtor.DefineMethod(proto, "slice", (thisV, args) => Slice(realm, thisV, args), 2);
        ArrayBufferCtor.DefineMethod(proto, "some", (thisV, args) => Some(realm, thisV, args), 1);
        ArrayBufferCtor.DefineMethod(proto, "sort", (thisV, args) => Sort(realm, thisV, args), 1);
        ArrayBufferCtor.DefineMethod(proto, "subarray", (thisV, args) => Subarray(realm, thisV, args), 2);
        ArrayBufferCtor.DefineMethod(proto, "toLocaleString", (thisV, args) => Join(realm, thisV, args), 0);
        ArrayBufferCtor.DefineMethod(proto, "toReversed", (thisV, args) => ToReversed(realm, thisV), 0);
        ArrayBufferCtor.DefineMethod(proto, "toSorted", (thisV, args) => ToSorted(realm, thisV, args), 1);
        ArrayBufferCtor.DefineMethod(proto, "toString", (thisV, args) => Join(realm, thisV, args), 0);
        var valuesFn = IntrinsicHelpers.DefineMethod(realm, proto, "values", 0,
            (thisV, args) => Values(realm, thisV));
        ArrayBufferCtor.DefineMethod(proto, "with", (thisV, args) => With(realm, thisV, args), 2);

        // §23.2.3.36 %TypedArray%.prototype[@@iterator] is the same function
        // object as %TypedArray%.prototype.values per spec.
        proto.DefineOwnProperty(SymbolCtor.Iterator,
            PropertyDescriptor.BuiltinMethod(JsValue.Object(valuesFn)));

        // §23.2.3.34 get %TypedArray%.prototype[@@toStringTag] — accessor
        // returning the receiver's [[TypedArrayName]] (Kind-derived name) or
        // undefined when the receiver isn't a TypedArray.
        var tagGetter = new JsNativeFunction(realm, "get [Symbol.toStringTag]", 0, (thisV, _) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsTypedArray ta)
            {
                return JsValue.String(ta.ConstructorName);
            }

            return JsValue.Undefined;
        }, isConstructor: false);
        proto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Accessor(tagGetter, setter: null, enumerable: false, configurable: true));
    }

    private static JsTypedArray ThisTA(JsRealm realm, JsValue thisV)
        => thisV.IsObject && thisV.AsObject is JsTypedArray ta
            ? ta
            : throw new JsThrow(realm.NewTypeError("TypedArray method called on incompatible receiver"));

    private static JsObject TypePrototype(JsRealm realm, JsTypedArray ta)
        => ta.Prototype ?? realm.TypedArrayPrototype;

    private static int RelativeIndex(JsRealm realm, JsValue v, int len, bool defaultEnd = false)
    {
        if (v.IsUndefined)
        {
            return defaultEnd ? len : 0;
        }

        var n = ArrayBufferCtor.ToIntegerOrInfinity(realm, v);
        if (n < 0)
        {
            return (int)Math.Max(len + n, 0);
        }

        return (int)Math.Min(n, len);
    }

    private static JsValue At(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var ta = ThisTA(realm, thisV);
        var k = (int)ArrayBufferCtor.ToIntegerOrInfinity(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        if (k < 0)
        {
            k += ta.Length;
        }

        return k < 0 || k >= ta.Length ? JsValue.Undefined : ta.GetElement(k);
    }

    private static JsValue Fill(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var ta = ThisTA(realm, thisV);
        var value = args.Length > 0 ? args[0] : JsValue.Undefined;
        var start = RelativeIndex(realm, args.Length > 1 ? args[1] : JsValue.Undefined, ta.Length);
        var end = RelativeIndex(realm, args.Length > 2 ? args[2] : JsValue.Undefined, ta.Length, defaultEnd: true);
        for (var i = start; i < end; i++)
        {
            ta.SetElement(i, value, realm);
        }

        return thisV;
    }

    private static JsValue CopyWithin(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var ta = ThisTA(realm, thisV);
        var to = RelativeIndex(realm, args.Length > 0 ? args[0] : JsValue.Undefined, ta.Length);
        var from = RelativeIndex(realm, args.Length > 1 ? args[1] : JsValue.Undefined, ta.Length);
        var end = RelativeIndex(realm, args.Length > 2 ? args[2] : JsValue.Undefined, ta.Length, defaultEnd: true);
        var count = Math.Min(end - from, ta.Length - to);
        if (count <= 0)
        {
            return thisV;
        }

        var tmp = new JsValue[count];
        for (var i = 0; i < count; i++)
        {
            tmp[i] = ta.GetElement(from + i);
        }

        for (var i = 0; i < count; i++)
        {
            ta.SetElement(to + i, tmp[i], realm);
        }

        return thisV;
    }

    private static JsValue Set(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var ta = ThisTA(realm, thisV);
        if (args.Length == 0 || !args[0].IsObject)
        {
            throw new JsThrow(realm.NewTypeError("TypedArray.prototype.set source must be array-like"));
        }

        var offset = args.Length > 1 ? ArrayBufferCtor.ToIndex(realm, args[1]) : 0;
        var values = ToList(realm, args[0].AsObject);
        if (values.Count > ta.Length - offset)
        {
            throw new JsThrow(realm.NewRangeError("TypedArray.prototype.set out of range"));
        }

        for (var i = 0; i < values.Count; i++)
        {
            ta.SetElement(offset + i, values[i], realm);
        }

        return JsValue.Undefined;
    }

    private static List<JsValue> ToList(JsRealm realm, JsObject src)
    {
        if (src is JsTypedArray sta)
        {
            var values = new List<JsValue>(sta.Length);
            for (var i = 0; i < sta.Length; i++)
            {
                values.Add(sta.GetElement(i));
            }

            return values;
        }
        var len = ArrayBufferCtor.ToIndex(realm, src.Get("length"));
        var list = new List<JsValue>(len);
        for (var i = 0; i < len; i++)
        {
            list.Add(src.Get(ArrayBufferCtor.IndexKey(i)));
        }

        return list;
    }

    private static JsValue Slice(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var ta = ThisTA(realm, thisV);
        var start = RelativeIndex(realm, args.Length > 0 ? args[0] : JsValue.Undefined, ta.Length);
        var end = RelativeIndex(realm, args.Length > 1 ? args[1] : JsValue.Undefined, ta.Length, defaultEnd: true);
        var len = Math.Max(end - start, 0);
        var result = Allocate(realm, TypePrototype(realm, ta), ta.Kind, len);
        for (var i = 0; i < len; i++)
        {
            result.SetElement(i, ta.GetElement(start + i), realm);
        }

        return JsValue.Object(result);
    }

    private static JsValue Subarray(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var ta = ThisTA(realm, thisV);
        var start = RelativeIndex(realm, args.Length > 0 ? args[0] : JsValue.Undefined, ta.Length);
        var end = RelativeIndex(realm, args.Length > 1 ? args[1] : JsValue.Undefined, ta.Length, defaultEnd: true);
        var len = Math.Max(end - start, 0);
        return JsValue.Object(new JsTypedArray(TypePrototype(realm, ta), ta.Kind, ta.Buffer, ta.ByteOffset + start * ta.BytesPerElement, len));
    }

    private static JsValue Reverse(JsRealm realm, JsValue thisV)
    {
        var ta = ThisTA(realm, thisV);
        for (var i = 0; i < ta.Length / 2; i++)
        {
            var j = ta.Length - 1 - i;
            var a = ta.GetElement(i);
            ta.SetElement(i, ta.GetElement(j), realm);
            ta.SetElement(j, a, realm);
        }
        return thisV;
    }

    private static JsValue ToReversed(JsRealm realm, JsValue thisV)
    {
        var ta = ThisTA(realm, thisV);
        var result = Allocate(realm, TypePrototype(realm, ta), ta.Kind, ta.Length);
        for (var i = 0; i < ta.Length; i++)
        {
            result.SetElement(i, ta.GetElement(ta.Length - 1 - i), realm);
        }

        return JsValue.Object(result);
    }

    private static JsValue ToSorted(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var copy = Slice(realm, thisV, Array.Empty<JsValue>()).AsObject;
        return Sort(realm, JsValue.Object(copy), args);
    }

    private static JsValue Sort(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var ta = ThisTA(realm, thisV);
        var values = ToList(realm, ta);
        var compare = args.Length > 0 ? args[0] : JsValue.Undefined;
        values.Sort((a, b) =>
        {
            if (AbstractOperations.IsCallable(compare))
            {
                return Math.Sign(ArrayBufferCtor.Number(AbstractOperations.Call(realm.ActiveVm, compare, JsValue.Undefined, new[] { a, b })));
            }

            return ArrayBufferCtor.Number(a).CompareTo(ArrayBufferCtor.Number(b));
        });
        for (var i = 0; i < values.Count; i++)
        {
            ta.SetElement(i, values[i], realm);
        }

        return thisV;
    }

    private static JsValue Map(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var ta = ThisTA(realm, thisV);
        var cb = RequireCallback(realm, args);
        var thisArg = args.Length > 1 ? args[1] : JsValue.Undefined;
        var result = Allocate(realm, TypePrototype(realm, ta), ta.Kind, ta.Length);
        for (var i = 0; i < ta.Length; i++)
        {
            result.SetElement(i, AbstractOperations.Call(realm.ActiveVm, cb, thisArg, new[] { ta.GetElement(i), JsValue.Number(i), thisV }), realm);
        }

        return JsValue.Object(result);
    }

    private static JsValue Filter(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var ta = ThisTA(realm, thisV);
        var cb = RequireCallback(realm, args);
        var thisArg = args.Length > 1 ? args[1] : JsValue.Undefined;
        var kept = new List<JsValue>();
        for (var i = 0; i < ta.Length; i++)
        {
            var v = ta.GetElement(i);
            if (JsValue.ToBoolean(AbstractOperations.Call(realm.ActiveVm, cb, thisArg, new[] { v, JsValue.Number(i), thisV })))
            {
                kept.Add(v);
            }
        }
        var result = Allocate(realm, TypePrototype(realm, ta), ta.Kind, kept.Count);
        for (var i = 0; i < kept.Count; i++)
        {
            result.SetElement(i, kept[i], realm);
        }

        return JsValue.Object(result);
    }

    private static JsValue ForEach(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var ta = ThisTA(realm, thisV);
        var cb = RequireCallback(realm, args);
        var thisArg = args.Length > 1 ? args[1] : JsValue.Undefined;
        for (var i = 0; i < ta.Length; i++)
        {
            AbstractOperations.Call(realm.ActiveVm, cb, thisArg, new[] { ta.GetElement(i), JsValue.Number(i), thisV });
        }

        return JsValue.Undefined;
    }

    private static JsValue Every(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var ta = ThisTA(realm, thisV);
        var cb = RequireCallback(realm, args);
        var thisArg = args.Length > 1 ? args[1] : JsValue.Undefined;
        for (var i = 0; i < ta.Length; i++)
        {
            if (!JsValue.ToBoolean(AbstractOperations.Call(realm.ActiveVm, cb, thisArg, new[] { ta.GetElement(i), JsValue.Number(i), thisV })))
            {
                return JsValue.False;
            }
        }

        return JsValue.True;
    }

    private static JsValue Some(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var ta = ThisTA(realm, thisV);
        var cb = RequireCallback(realm, args);
        var thisArg = args.Length > 1 ? args[1] : JsValue.Undefined;
        for (var i = 0; i < ta.Length; i++)
        {
            if (JsValue.ToBoolean(AbstractOperations.Call(realm.ActiveVm, cb, thisArg, new[] { ta.GetElement(i), JsValue.Number(i), thisV })))
            {
                return JsValue.True;
            }
        }

        return JsValue.False;
    }

    private static JsValue Find(JsRealm realm, JsValue thisV, JsValue[] args, bool last, bool indexOnly)
    {
        var ta = ThisTA(realm, thisV);
        var cb = RequireCallback(realm, args);
        var thisArg = args.Length > 1 ? args[1] : JsValue.Undefined;
        for (var step = 0; step < ta.Length; step++)
        {
            var i = last ? ta.Length - 1 - step : step;
            var v = ta.GetElement(i);
            if (JsValue.ToBoolean(AbstractOperations.Call(realm.ActiveVm, cb, thisArg, new[] { v, JsValue.Number(i), thisV })))
            {
                return indexOnly ? JsValue.Number(i) : v;
            }
        }
        return indexOnly ? JsValue.Number(-1) : JsValue.Undefined;
    }

    private static JsValue Reduce(JsRealm realm, JsValue thisV, JsValue[] args, bool right)
    {
        var ta = ThisTA(realm, thisV);
        var cb = RequireCallback(realm, args);
        if (ta.Length == 0 && args.Length < 2)
        {
            throw new JsThrow(realm.NewTypeError("Reduce of empty TypedArray with no initial value"));
        }

        var i = right ? ta.Length - 1 : 0;
        var acc = args.Length > 1 ? args[1] : ta.GetElement(i);
        if (args.Length <= 1)
        {
            i += right ? -1 : 1;
        }

        for (; right ? i >= 0 : i < ta.Length; i += right ? -1 : 1)
        {
            acc = AbstractOperations.Call(realm.ActiveVm, cb, JsValue.Undefined, new[] { acc, ta.GetElement(i), JsValue.Number(i), thisV });
        }

        return acc;
    }

    private static JsValue Includes(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var ta = ThisTA(realm, thisV);
        var search = args.Length > 0 ? args[0] : JsValue.Undefined;
        var start = RelativeIndex(realm, args.Length > 1 ? args[1] : JsValue.Undefined, ta.Length);
        for (var i = start; i < ta.Length; i++)
        {
            if (AbstractOperations.SameValueZero(ta.GetElement(i), search))
            {
                return JsValue.True;
            }
        }

        return JsValue.False;
    }

    private static JsValue IndexOf(JsRealm realm, JsValue thisV, JsValue[] args, bool last)
    {
        var ta = ThisTA(realm, thisV);
        var search = args.Length > 0 ? args[0] : JsValue.Undefined;
        var start = args.Length > 1 ? RelativeIndex(realm, args[1], ta.Length, defaultEnd: last) : (last ? ta.Length - 1 : 0);
        if (last) { for (var i = Math.Min(start, ta.Length - 1); i >= 0; i--)
            {
                if (JsValue.StrictEquals(ta.GetElement(i), search))
                {
                    return JsValue.Number(i);
                }
            }
        }
        else { for (var i = start; i < ta.Length; i++)
            {
                if (JsValue.StrictEquals(ta.GetElement(i), search))
                {
                    return JsValue.Number(i);
                }
            }
        }
        return JsValue.Number(-1);
    }

    private static JsValue Join(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var ta = ThisTA(realm, thisV);
        var sep = args.Length > 0 && !args[0].IsUndefined ? JsValue.ToStringValue(args[0]) : ",";
        var parts = new string[ta.Length];
        for (var i = 0; i < ta.Length; i++)
        {
            parts[i] = JsValue.ToStringValue(ta.GetElement(i));
        }

        return JsValue.String(string.Join(sep, parts));
    }

    private static JsValue With(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var ta = ThisTA(realm, thisV);
        var index = (int)ArrayBufferCtor.ToIntegerOrInfinity(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        if (index < 0)
        {
            index += ta.Length;
        }

        if (index < 0 || index >= ta.Length)
        {
            throw new JsThrow(realm.NewRangeError("TypedArray.prototype.with index out of range"));
        }

        var copy = Slice(realm, thisV, Array.Empty<JsValue>()).AsObject as JsTypedArray ?? throw new InvalidOperationException();
        copy.SetElement(index, args.Length > 1 ? args[1] : JsValue.Undefined, realm);
        return JsValue.Object(copy);
    }

    // §23.2.3.{19,36,7} keys / values / entries return a real Array Iterator
    // (%ArrayIteratorPrototype%) over the typed array, so the result has a
    // working `next` and is itself iterable — required for `for…of`, spread,
    // and destructuring to consume a typed array (the @@iterator alias of
    // `values`). ThisTA validates the receiver is a TypedArray first.
    private static JsValue Keys(JsRealm realm, JsValue thisV)
    {
        var ta = ThisTA(realm, thisV);
        return IteratorIntrinsics.CreateArrayIterator(realm, JsValue.Object(ta), ArrayIteratorKind.Key);
    }

    private static JsValue Values(JsRealm realm, JsValue thisV)
    {
        var ta = ThisTA(realm, thisV);
        return IteratorIntrinsics.CreateArrayIterator(realm, JsValue.Object(ta), ArrayIteratorKind.Value);
    }

    private static JsValue Entries(JsRealm realm, JsValue thisV)
    {
        var ta = ThisTA(realm, thisV);
        return IteratorIntrinsics.CreateArrayIterator(realm, JsValue.Object(ta), ArrayIteratorKind.KeyAndValue);
    }

    private static JsValue RequireCallback(JsRealm realm, JsValue[] args)
    {
        var cb = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (!AbstractOperations.IsCallable(cb))
        {
            throw new JsThrow(realm.NewTypeError("callback must be callable"));
        }

        return cb;
    }
}
