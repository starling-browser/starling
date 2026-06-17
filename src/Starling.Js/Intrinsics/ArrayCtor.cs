using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// §23.1 The Array constructor and §23.1.3 Array.prototype. Backed by the
/// dense <see cref="JsArray"/> exotic object.
/// </summary>
/// <remarks>
/// <para><b>Iterator protocol (B3-2):</b> <c>entries</c>/<c>keys</c>/<c>values</c>
/// return real <c>%ArrayIteratorPrototype%</c> instances and
/// <c>Array.prototype[@@iterator]</c> aliases <c>values</c>. <c>Array.from</c>
/// dispatches through <c>@@iterator</c> when present and falls back to
/// array-like length+index walking.</para>
/// <para><b>Sparse arrays:</b> our backing list materializes holes as
/// <see cref="JsValue.Undefined"/>. Spec-strict sparseness (separate "no slot"
/// state vs. "slot holds undefined") is deferred — the visible behavior matches
/// for every method we ship.</para>
/// </remarks>
public static class ArrayCtor
{
    [ThreadStatic] private static HashSet<JsObject>? s_joinStack;

    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.ArrayPrototype;

        // -------- Constructor (callable + constructible). §23.1.1.1. Array may
        // be called as a function or constructed; when derived via
        // `class X extends Array {}`, super() threads X as new.target so the
        // result is X-prototyped (a real exotic Array with the right chain).
        var ctor = new JsNativeFunction(realm, "Array", length: 1,
            (newTarget, args) =>
            {
                var arr = ConstructArray(realm, args);
                var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
                if (!ReferenceEquals(instProto, proto))
                {
                    arr.SetPrototypeOf(instProto);
                }

                return JsValue.Object(arr);
            },
            isConstructor: true);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));

        // -------- Statics
        IntrinsicHelpers.DefineMethod(realm, ctor, "isArray", 1, (_, args) =>
            JsValue.Boolean(args.Length > 0 && JsArray.IsArray(args[0], realm)));
        IntrinsicHelpers.DefineMethod(realm, ctor, "of", 0, (_, args) =>
        {
            var arr = new JsArray(realm);
            foreach (var v in args)
            {
                arr.Push(v);
            }

            return JsValue.Object(arr);
        });
        IntrinsicHelpers.DefineMethod(realm, ctor, "from", 1, (_, args) => From(realm, args));

        // Bulk-install constructor + every string-keyed prototype method by
        // adopting one precomputed shape. Order (and thus getOwnPropertyNames
        // order) is unchanged: constructor, mutating methods, non-mutating
        // methods, ES2023 immutable methods, then the iterator trio
        // (values/keys/entries). All are string-keyed builtin data properties, so
        // the result is byte-identical to the prior sequential DefineMethod chain.
        IntrinsicHelpers.BulkInstallBuiltins(realm, proto, new[]
        {
            new IntrinsicHelpers.BulkMember("constructor", 0, null, JsValue.Object(ctor)),
            new IntrinsicHelpers.BulkMember("push", 1, (thisV, args) => Push(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("pop", 0, (thisV, _) => Pop(realm, thisV)),
            new IntrinsicHelpers.BulkMember("shift", 0, (thisV, _) => Shift(realm, thisV)),
            new IntrinsicHelpers.BulkMember("unshift", 1, (thisV, args) => Unshift(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("splice", 2, (thisV, args) => Splice(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("reverse", 0, (thisV, _) => Reverse(realm, thisV)),
            new IntrinsicHelpers.BulkMember("sort", 1, (thisV, args) => Sort(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("fill", 1, (thisV, args) => Fill(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("copyWithin", 2, (thisV, args) => CopyWithin(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("concat", 1, (thisV, args) => Concat(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("slice", 2, (thisV, args) => Slice(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("join", 1, (thisV, args) => Join(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("toString", 0, (thisV, _) => ToString(realm, thisV)),
            new IntrinsicHelpers.BulkMember("toLocaleString", 0, (thisV, _) => Join(realm, thisV, Array.Empty<JsValue>())),
            new IntrinsicHelpers.BulkMember("indexOf", 1, (thisV, args) => IndexOf(realm, thisV, args, fromEnd: false)),
            new IntrinsicHelpers.BulkMember("lastIndexOf", 1, (thisV, args) => IndexOf(realm, thisV, args, fromEnd: true)),
            new IntrinsicHelpers.BulkMember("includes", 1, (thisV, args) => Includes(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("forEach", 1, (thisV, args) => ForEach(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("map", 1, (thisV, args) => Map(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("filter", 1, (thisV, args) => Filter(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("reduce", 1, (thisV, args) => Reduce(realm, thisV, args, fromRight: false)),
            new IntrinsicHelpers.BulkMember("reduceRight", 1, (thisV, args) => Reduce(realm, thisV, args, fromRight: true)),
            new IntrinsicHelpers.BulkMember("every", 1, (thisV, args) => Every(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("some", 1, (thisV, args) => Some(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("find", 1, (thisV, args) => Find(realm, thisV, args, fromEnd: false, indexOnly: false)),
            new IntrinsicHelpers.BulkMember("findIndex", 1, (thisV, args) => Find(realm, thisV, args, fromEnd: false, indexOnly: true)),
            new IntrinsicHelpers.BulkMember("findLast", 1, (thisV, args) => Find(realm, thisV, args, fromEnd: true, indexOnly: false)),
            new IntrinsicHelpers.BulkMember("findLastIndex", 1, (thisV, args) => Find(realm, thisV, args, fromEnd: true, indexOnly: true)),
            new IntrinsicHelpers.BulkMember("flat", 0, (thisV, args) => Flat(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("flatMap", 1, (thisV, args) => FlatMap(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("toReversed", 0, (thisV, _) => ToReversed(realm, thisV)),
            new IntrinsicHelpers.BulkMember("toSorted", 1, (thisV, args) => ToSorted(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("toSpliced", 2, (thisV, args) => ToSpliced(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("with", 2, (thisV, args) => With(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("values", 0, (thisV, _) => IteratorIntrinsics.CreateArrayIterator(realm, thisV, ArrayIteratorKind.Value)),
            new IntrinsicHelpers.BulkMember("keys", 0, (thisV, _) => IteratorIntrinsics.CreateArrayIterator(realm, thisV, ArrayIteratorKind.Key)),
            new IntrinsicHelpers.BulkMember("entries", 0, (thisV, _) => IteratorIntrinsics.CreateArrayIterator(realm, thisV, ArrayIteratorKind.KeyAndValue)),
        });
        // §23.1.3.36 Array.prototype[@@iterator] is the SAME function object as
        // Array.prototype.values per spec. Symbol-keyed — install via the
        // dictionary path AFTER the string-method shape is adopted.
        var values = proto.Get("values");
        proto.DefineOwnProperty(SymbolCtor.Iterator,
            PropertyDescriptor.BuiltinMethod(values));

        realm.ArrayConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("Array",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    // ============================================================
    //                   Constructor helper
    // ============================================================
    private static JsArray ConstructArray(JsRealm realm, JsValue[] args)
    {
        if (args.Length == 1 && args[0].IsNumber)
        {
            var n = args[0].AsNumber;
            var len = (uint)n;
            if (n != len || double.IsNaN(n) || double.IsInfinity(n))
            {
                throw new JsThrow(realm.NewRangeError("Invalid array length"));
            }

            var arr = new JsArray(realm);
            // Pre-grow via length so we have N holes.
            var lengthDesc = PropertyDescriptor.Data(JsValue.Number(len), writable: true, enumerable: false, configurable: false);
            arr.DefineOwnProperty("length", lengthDesc);
            return arr;
        }
        return new JsArray(realm, args);
    }

    // ============================================================
    //                       Helpers
    // ============================================================

    /// <summary>Coerce <c>this</c> into an array-like host object. Falls back
    /// to ToObject so generic prototype methods can be called on plain objects
    /// (the spec convention — every Array.prototype method works on any
    /// <c>{ length, 0, 1, ... }</c> shape).</summary>
    private static JsObject ThisObject(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsNullish)
        {
            throw new JsThrow(realm.NewTypeError("Array.prototype method called on null or undefined"));
        }

        return thisV.IsObject ? thisV.AsObject : AbstractOperations.ToObject(realm, thisV);
    }

    private static int ToLength(JsObject obj)
    {
        var v = obj.Get("length");
        var n = JsValue.ToNumber(v);
        if (double.IsNaN(n) || n <= 0)
        {
            return 0;
        }

        if (n > int.MaxValue)
        {
            return int.MaxValue;
        }

        return (int)Math.Min(Math.Truncate(n), (double)(1L << 53) - 1);
    }

    private static JsValue GetElement(JsObject obj, int i)
    {
        if (obj is JsArray ja)
        {
            return ja[i];
        }

        return AbstractOperations.Get((JsVm?)null, obj, JsArray.IndexToString((uint)i));
    }

    private static void SetElement(JsObject obj, int i, JsValue v)
    {
        if (obj is JsArray ja) { ja[i] = v; return; }
        AbstractOperations.Set(null, obj, JsArray.IndexToString((uint)i), v);
    }

    private static int ClampStart(int relative, int len)
    {
        if (relative < 0)
        {
            return Math.Max(len + relative, 0);
        }

        return Math.Min(relative, len);
    }

    private static int ToInteger(JsValue v)
    {
        var n = JsValue.ToNumber(v);
        if (double.IsNaN(n))
        {
            return 0;
        }

        if (double.IsPositiveInfinity(n))
        {
            return int.MaxValue;
        }

        if (double.IsNegativeInfinity(n))
        {
            return int.MinValue;
        }

        return (int)Math.Truncate(n);
    }

    // ============================================================
    //                       Statics: from / of
    // ============================================================

    private static JsValue From(JsRealm realm, JsValue[] args)
    {
        if (args.Length == 0 || args[0].IsNullish)
        {
            throw new JsThrow(realm.NewTypeError("Array.from requires an iterable or array-like"));
        }

        var src = args[0];
        var mapFn = args.Length > 1 ? args[1] : JsValue.Undefined;
        var thisArg = args.Length > 2 ? args[2] : JsValue.Undefined;
        var hasMap = !mapFn.IsUndefined;
        if (hasMap && !AbstractOperations.IsCallable(mapFn))
        {
            throw new JsThrow(realm.NewTypeError("Array.from: map fn must be callable"));
        }

        var arr = new JsArray(realm);

        // §23.1.2.1 step 5: check the iterator-protocol path first. Strings,
        // arrays, and user-defined iterables all hit this branch via
        // String.prototype[@@iterator] / Array.prototype[@@iterator].
        var usingIterator = HasIteratorMethod(realm, src);
        if (usingIterator)
        {
            var record = AbstractOperations.GetIterator(realm, realm.ActiveVm, src);
            var index = 0;
            while (true)
            {
                var step = AbstractOperations.IteratorStep(realm, realm.ActiveVm, ref record);
                if (step is null)
                {
                    break;
                }

                var value = AbstractOperations.IteratorValue(realm.ActiveVm, step.Value);
                var elem = hasMap
                    ? AbstractOperations.Call(realm.ActiveVm, mapFn, thisArg, new[] { value, JsValue.Number(index) })
                    : value;
                arr.Push(elem);
                index++;
            }
            return JsValue.Object(arr);
        }

        // Array-like fallback (length + indexed access).
        if (!src.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("Array.from: source must be an iterable or array-like"));
        }

        var obj = src.AsObject;
        var len = ToLength(obj);
        for (var i = 0; i < len; i++)
        {
            var elem = GetElement(obj, i);
            if (hasMap)
            {
                elem = AbstractOperations.Call(realm.ActiveVm, mapFn, thisArg, new[] { elem, JsValue.Number(i) });
            }

            arr.Push(elem);
        }
        return JsValue.Object(arr);
    }

    /// <summary>Lightweight check whether <paramref name="value"/> has an
    /// <c>@@iterator</c> method without invoking it. Used by
    /// <see cref="From(JsRealm, JsValue[])"/> to pick between the iterator-
    /// protocol path and the array-like fallback.</summary>
    internal static bool HasIteratorMethod(JsRealm realm, JsValue value)
    {
        if (value.IsString)
        {
            return true;
        }

        if (!value.IsObject)
        {
            return false;
        }

        var v = AbstractOperations.Get((JsVm?)null, value.AsObject, JsPropertyKey.Symbol(SymbolCtor.Iterator));
        return AbstractOperations.IsCallable(v);
    }

    // ============================================================
    //                       Mutators
    // ============================================================

    private static JsValue Push(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var len = ToLength(obj);
        foreach (var v in args)
        {
            SetElement(obj, len, v);
            len++;
        }
        AbstractOperations.Set(null, obj, "length", JsValue.Number(len));
        return JsValue.Number(len);
    }

    private static JsValue Pop(JsRealm realm, JsValue thisV)
    {
        var obj = ThisObject(realm, thisV);
        var len = ToLength(obj);
        if (len == 0)
        {
            AbstractOperations.Set(null, obj, "length", JsValue.Number(0));
            return JsValue.Undefined;
        }
        var idx = len - 1;
        var v = GetElement(obj, idx);
        AbstractOperations.Set(null, obj, "length", JsValue.Number(idx));
        return v;
    }

    private static JsValue Shift(JsRealm realm, JsValue thisV)
    {
        var obj = ThisObject(realm, thisV);
        var len = ToLength(obj);
        if (len == 0)
        {
            AbstractOperations.Set(null, obj, "length", JsValue.Number(0));
            return JsValue.Undefined;
        }
        var first = GetElement(obj, 0);
        for (var i = 1; i < len; i++)
        {
            SetElement(obj, i - 1, GetElement(obj, i));
        }

        AbstractOperations.Set(null, obj, "length", JsValue.Number(len - 1));
        return first;
    }

    private static JsValue Unshift(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var len = ToLength(obj);
        var count = args.Length;
        if (count > 0)
        {
            for (var i = len - 1; i >= 0; i--)
            {
                SetElement(obj, i + count, GetElement(obj, i));
            }

            for (var i = 0; i < count; i++)
            {
                SetElement(obj, i, args[i]);
            }
        }
        var newLen = len + count;
        AbstractOperations.Set(null, obj, "length", JsValue.Number(newLen));
        return JsValue.Number(newLen);
    }

    private static JsValue Splice(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var len = ToLength(obj);
        var start = args.Length > 0 ? ClampStart(ToInteger(args[0]), len) : 0;
        int deleteCount;
        if (args.Length < 1)
        {
            deleteCount = 0;
        }
        else if (args.Length < 2)
        {
            deleteCount = len - start;
        }
        else
        {
            deleteCount = Math.Max(0, Math.Min(ToInteger(args[1]), len - start));
        }

        var insertCount = Math.Max(0, args.Length - 2);
        var removed = new JsArray(realm);
        for (var i = 0; i < deleteCount; i++)
        {
            removed.Push(GetElement(obj, start + i));
        }

        var newLen = len - deleteCount + insertCount;
        if (insertCount < deleteCount)
        {
            for (var i = start; i < len - deleteCount; i++)
            {
                SetElement(obj, i + insertCount, GetElement(obj, i + deleteCount));
            }
            // Resulting length is shorter; rely on JsArray length truncation.
        }
        else if (insertCount > deleteCount)
        {
            for (var i = len - deleteCount - 1; i >= start; i--)
            {
                SetElement(obj, i + insertCount, GetElement(obj, i + deleteCount));
            }
        }
        for (var i = 0; i < insertCount; i++)
        {
            SetElement(obj, start + i, args[2 + i]);
        }

        AbstractOperations.Set(null, obj, "length", JsValue.Number(newLen));
        return JsValue.Object(removed);
    }

    private static JsValue Reverse(JsRealm realm, JsValue thisV)
    {
        var obj = ThisObject(realm, thisV);
        var len = ToLength(obj);
        for (var i = 0; i < len / 2; i++)
        {
            var j = len - i - 1;
            var a = GetElement(obj, i);
            var b = GetElement(obj, j);
            SetElement(obj, i, b);
            SetElement(obj, j, a);
        }
        return JsValue.Object(obj);
    }

    private static JsValue Sort(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var cmpV = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (!cmpV.IsUndefined && !AbstractOperations.IsCallable(cmpV))
        {
            throw new JsThrow(realm.NewTypeError("Array.prototype.sort: comparator must be a function"));
        }

        var len = ToLength(obj);
        var items = CollectIndexedValues(obj, len);
        StableSort(realm, items, cmpV);
        for (var i = 0; i < len; i++)
        {
            SetElement(obj, i, items[i].Value);
        }

        return JsValue.Object(obj);
    }

    private static int Compare(JsRealm realm, JsValue a, JsValue b, JsValue cmpFn)
    {
        // §23.1.3.30: undefined values sort to the end (regardless of comparator).
        var aIsU = a.IsUndefined; var bIsU = b.IsUndefined;
        if (aIsU && bIsU)
        {
            return 0;
        }

        if (aIsU)
        {
            return 1;
        }

        if (bIsU)
        {
            return -1;
        }

        if (!cmpFn.IsUndefined)
        {
            var r = AbstractOperations.Call(realm.ActiveVm, cmpFn, JsValue.Undefined, new[] { a, b });
            var n = JsValue.ToNumber(r);
            if (double.IsNaN(n))
            {
                return 0;
            }

            if (n < 0)
            {
                return -1;
            }

            if (n > 0)
            {
                return 1;
            }

            return 0;
        }
        return string.CompareOrdinal(JsValue.ToStringValue(a), JsValue.ToStringValue(b));
    }

    private static List<IndexedValue> CollectIndexedValues(JsObject obj, int len)
    {
        var items = new List<IndexedValue>(len);
        for (var i = 0; i < len; i++)
        {
            items.Add(new IndexedValue(GetElement(obj, i), i));
        }

        return items;
    }

    private static void StableSort(JsRealm realm, List<IndexedValue> items, JsValue cmpV)
    {
        items.Sort((a, b) =>
        {
            var c = Compare(realm, a.Value, b.Value, cmpV);
            return c != 0 ? c : a.Index.CompareTo(b.Index);
        });
    }

    private readonly struct IndexedValue
    {
        public readonly JsValue Value;
        public readonly int Index;

        public IndexedValue(JsValue value, int index)
        {
            Value = value;
            Index = index;
        }
    }

    private static JsValue Fill(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var len = ToLength(obj);
        var value = args.Length > 0 ? args[0] : JsValue.Undefined;
        var start = args.Length > 1 ? ClampStart(ToInteger(args[1]), len) : 0;
        var end = args.Length > 2 ? ClampStart(ToInteger(args[2]), len) : len;
        for (var i = start; i < end; i++)
        {
            SetElement(obj, i, value);
        }

        return JsValue.Object(obj);
    }

    private static JsValue CopyWithin(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var len = ToLength(obj);
        var target = args.Length > 0 ? ClampStart(ToInteger(args[0]), len) : 0;
        var start = args.Length > 1 ? ClampStart(ToInteger(args[1]), len) : 0;
        var end = args.Length > 2 ? ClampStart(ToInteger(args[2]), len) : len;
        var count = Math.Min(end - start, len - target);
        if (count <= 0)
        {
            return JsValue.Object(obj);
        }
        // Snapshot to avoid overlap issues.
        var snap = new JsValue[count];
        for (var i = 0; i < count; i++)
        {
            snap[i] = GetElement(obj, start + i);
        }

        for (var i = 0; i < count; i++)
        {
            SetElement(obj, target + i, snap[i]);
        }

        return JsValue.Object(obj);
    }

    // ============================================================
    //                       Non-mutators
    // ============================================================

    private static JsValue Concat(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var result = new JsArray(realm);
        if (JsArray.IsArray(JsValue.Object(obj), realm))
        {
            _ = AbstractOperations.Get(realm.ActiveVm, obj, "constructor");
        }

        AppendConcat(realm, result, JsValue.Object(obj));
        foreach (var a in args)
        {
            AppendConcat(realm, result, a);
        }

        return JsValue.Object(result);
    }

    private static void AppendConcat(JsRealm realm, JsArray target, JsValue v)
    {
        if (IsConcatSpreadable(realm, v))
        {
            var source = v.AsObject;
            var len = ToLength(source);
            for (var i = 0; i < len; i++)
            {
                target.Push(GetElement(source, i));
            }
        }
        else
        {
            target.Push(v);
        }
    }

    private static bool IsConcatSpreadable(JsRealm realm, JsValue value)
    {
        if (!value.IsObject)
        {
            return false;
        }

        var spreadable = AbstractOperations.Get(realm.ActiveVm, value.AsObject,
            JsPropertyKey.Symbol(SymbolCtor.IsConcatSpreadable));
        return spreadable.IsUndefined ? JsArray.IsArray(value, realm) : JsValue.ToBoolean(spreadable);
    }

    private static JsValue Slice(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var len = ToLength(obj);
        var start = args.Length > 0 ? ClampStart(ToInteger(args[0]), len) : 0;
        var end = args.Length > 1 && !args[1].IsUndefined ? ClampStart(ToInteger(args[1]), len) : len;
        var result = new JsArray(realm);
        for (var i = start; i < end; i++)
        {
            result.Push(GetElement(obj, i));
        }

        return JsValue.Object(result);
    }

    private static JsValue Join(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var sep = args.Length > 0 && !args[0].IsUndefined ? JsValue.ToStringValue(args[0]) : ",";
        var len = ToLength(obj);
        var sb = new System.Text.StringBuilder();
        var stack = s_joinStack ??= new HashSet<JsObject>();
        if (!stack.Add(obj))
        {
            return JsValue.String(string.Empty);
        }

        try
        {
            for (var i = 0; i < len; i++)
            {
                if (i > 0)
                {
                    sb.Append(sep);
                }

                var v = GetElement(obj, i);
                if (v.IsNullish)
                {
                    continue;
                }

                if (v.IsObject && stack.Contains(v.AsObject))
                {
                    continue;
                }

                sb.Append(AbstractOperations.ToStringJs(realm.ActiveVm, v));
            }
        }
        finally
        {
            stack.Remove(obj);
        }
        return JsValue.String(sb.ToString());
    }

    private static JsValue ToString(JsRealm realm, JsValue thisV)
    {
        var obj = ThisObject(realm, thisV);
        var join = AbstractOperations.Get(realm.ActiveVm, obj, "join");
        if (AbstractOperations.IsCallable(join))
        {
            return AbstractOperations.Call(realm.ActiveVm, join, JsValue.Object(obj), Array.Empty<JsValue>());
        }

        var objectToString = AbstractOperations.Get(realm.ActiveVm, realm.ObjectPrototype, "toString");
        return AbstractOperations.Call(realm.ActiveVm, objectToString, JsValue.Object(obj), Array.Empty<JsValue>());
    }

    private static JsValue IndexOf(JsRealm realm, JsValue thisV, JsValue[] args, bool fromEnd)
    {
        var obj = ThisObject(realm, thisV);
        var len = ToLength(obj);
        if (len == 0)
        {
            return JsValue.Number(-1);
        }

        var target = args.Length > 0 ? args[0] : JsValue.Undefined;
        int from;
        if (args.Length > 1)
        {
            var n = ToInteger(args[1]);
            if (fromEnd)
            {
                from = n >= 0 ? Math.Min(n, len - 1) : len + n;
            }
            else
            {
                from = n >= 0 ? n : Math.Max(0, len + n);
            }
        }
        else
        {
            from = fromEnd ? len - 1 : 0;
        }
        if (fromEnd)
        {
            for (var i = from; i >= 0; i--)
            {
                if (JsValue.StrictEquals(GetElement(obj, i), target))
                {
                    return JsValue.Number(i);
                }
            }
        }
        else
        {
            for (var i = from; i < len; i++)
            {
                if (JsValue.StrictEquals(GetElement(obj, i), target))
                {
                    return JsValue.Number(i);
                }
            }
        }
        return JsValue.Number(-1);
    }

    private static JsValue Includes(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var len = ToLength(obj);
        if (len == 0)
        {
            return JsValue.False;
        }

        var target = args.Length > 0 ? args[0] : JsValue.Undefined;
        var n = args.Length > 1 ? ToInteger(args[1]) : 0;
        var from = n >= 0 ? n : Math.Max(0, len + n);
        for (var i = from; i < len; i++)
        {
            if (AbstractOperations.SameValueZero(GetElement(obj, i), target))
            {
                return JsValue.True;
            }
        }

        return JsValue.False;
    }

    private static (JsValue fn, JsValue thisArg) ParseCallback(JsRealm realm, JsValue[] args, string methodName)
    {
        if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
        {
            throw new JsThrow(realm.NewTypeError($"Array.prototype.{methodName}: callback must be a function"));
        }

        return (args[0], args.Length > 1 ? args[1] : JsValue.Undefined);
    }

    private static JsValue ForEach(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var (fn, thisArg) = ParseCallback(realm, args, "forEach");
        var len = ToLength(obj);
        for (var i = 0; i < len; i++)
        {
            var v = GetElement(obj, i);
            AbstractOperations.Call(realm.ActiveVm, fn, thisArg, new[] { v, JsValue.Number(i), JsValue.Object(obj) });
        }
        return JsValue.Undefined;
    }

    private static JsValue Map(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var (fn, thisArg) = ParseCallback(realm, args, "map");
        var len = ToLength(obj);
        var result = new JsArray(realm);
        for (var i = 0; i < len; i++)
        {
            var v = GetElement(obj, i);
            var mapped = AbstractOperations.Call(realm.ActiveVm, fn, thisArg, new[] { v, JsValue.Number(i), JsValue.Object(obj) });
            result.Push(mapped);
        }
        return JsValue.Object(result);
    }

    private static JsValue Filter(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var (fn, thisArg) = ParseCallback(realm, args, "filter");
        var len = ToLength(obj);
        var result = new JsArray(realm);
        for (var i = 0; i < len; i++)
        {
            var v = GetElement(obj, i);
            var keep = AbstractOperations.Call(realm.ActiveVm, fn, thisArg, new[] { v, JsValue.Number(i), JsValue.Object(obj) });
            if (JsValue.ToBoolean(keep))
            {
                result.Push(v);
            }
        }
        return JsValue.Object(result);
    }

    private static JsValue Reduce(JsRealm realm, JsValue thisV, JsValue[] args, bool fromRight)
    {
        var obj = ThisObject(realm, thisV);
        if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
        {
            throw new JsThrow(realm.NewTypeError("Array.prototype.reduce: callback must be a function"));
        }

        var fn = args[0];
        var len = ToLength(obj);
        var hasInitial = args.Length > 1;
        JsValue acc;
        int i;
        if (hasInitial)
        {
            acc = args[1];
            i = fromRight ? len - 1 : 0;
        }
        else
        {
            if (len == 0)
            {
                throw new JsThrow(realm.NewTypeError("Reduce of empty array with no initial value"));
            }

            i = fromRight ? len - 1 : 0;
            acc = GetElement(obj, i);
            i += fromRight ? -1 : 1;
        }
        if (fromRight)
        {
            for (; i >= 0; i--)
            {
                acc = AbstractOperations.Call(realm.ActiveVm, fn, JsValue.Undefined,
                    new[] { acc, GetElement(obj, i), JsValue.Number(i), JsValue.Object(obj) });
            }
        }
        else
        {
            for (; i < len; i++)
            {
                acc = AbstractOperations.Call(realm.ActiveVm, fn, JsValue.Undefined,
                    new[] { acc, GetElement(obj, i), JsValue.Number(i), JsValue.Object(obj) });
            }
        }
        return acc;
    }

    private static JsValue Every(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var (fn, thisArg) = ParseCallback(realm, args, "every");
        var len = ToLength(obj);
        for (var i = 0; i < len; i++)
        {
            var v = GetElement(obj, i);
            var r = AbstractOperations.Call(realm.ActiveVm, fn, thisArg, new[] { v, JsValue.Number(i), JsValue.Object(obj) });
            if (!JsValue.ToBoolean(r))
            {
                return JsValue.False;
            }
        }
        return JsValue.True;
    }

    private static JsValue Some(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var (fn, thisArg) = ParseCallback(realm, args, "some");
        var len = ToLength(obj);
        for (var i = 0; i < len; i++)
        {
            var v = GetElement(obj, i);
            var r = AbstractOperations.Call(realm.ActiveVm, fn, thisArg, new[] { v, JsValue.Number(i), JsValue.Object(obj) });
            if (JsValue.ToBoolean(r))
            {
                return JsValue.True;
            }
        }
        return JsValue.False;
    }

    private static JsValue Find(JsRealm realm, JsValue thisV, JsValue[] args, bool fromEnd, bool indexOnly)
    {
        var obj = ThisObject(realm, thisV);
        var name = fromEnd ? (indexOnly ? "findLastIndex" : "findLast") : (indexOnly ? "findIndex" : "find");
        var (fn, thisArg) = ParseCallback(realm, args, name);
        var len = ToLength(obj);
        if (fromEnd)
        {
            for (var i = len - 1; i >= 0; i--)
            {
                var v = GetElement(obj, i);
                var r = AbstractOperations.Call(realm.ActiveVm, fn, thisArg, new[] { v, JsValue.Number(i), JsValue.Object(obj) });
                if (JsValue.ToBoolean(r))
                {
                    return indexOnly ? JsValue.Number(i) : v;
                }
            }
        }
        else
        {
            for (var i = 0; i < len; i++)
            {
                var v = GetElement(obj, i);
                var r = AbstractOperations.Call(realm.ActiveVm, fn, thisArg, new[] { v, JsValue.Number(i), JsValue.Object(obj) });
                if (JsValue.ToBoolean(r))
                {
                    return indexOnly ? JsValue.Number(i) : v;
                }
            }
        }
        return indexOnly ? JsValue.Number(-1) : JsValue.Undefined;
    }

    private static JsValue Flat(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var depth = 1d;
        if (args.Length > 0 && !args[0].IsUndefined)
        {
            depth = JsValue.ToNumber(args[0]);
        }

        var result = new JsArray(realm);
        FlattenInto(result, obj, depth);
        return JsValue.Object(result);
    }

    private static void FlattenInto(JsArray target, JsObject source, double depth)
    {
        var len = ToLength(source);
        for (var i = 0; i < len; i++)
        {
            var v = GetElement(source, i);
            if (depth > 0 && v.IsObject && v.AsObject is JsArray sub)
            {
                FlattenInto(target, sub, depth - 1);
            }
            else
            {
                target.Push(v);
            }
        }
    }

    private static JsValue FlatMap(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var (fn, thisArg) = ParseCallback(realm, args, "flatMap");
        var len = ToLength(obj);
        var result = new JsArray(realm);
        for (var i = 0; i < len; i++)
        {
            var v = GetElement(obj, i);
            var mapped = AbstractOperations.Call(realm.ActiveVm, fn, thisArg,
                new[] { v, JsValue.Number(i), JsValue.Object(obj) });
            if (mapped.IsObject && mapped.AsObject is JsArray sub)
            {
                for (var k = 0; k < sub.Length; k++)
                {
                    result.Push(sub[k]);
                }
            }
            else
            {
                result.Push(mapped);
            }
        }
        return JsValue.Object(result);
    }

    // ============================================================
    //                       Immutable variants (ES2023)
    // ============================================================

    private static JsValue ToReversed(JsRealm realm, JsValue thisV)
    {
        var obj = ThisObject(realm, thisV);
        var len = ToLength(obj);
        var result = new JsArray(realm);
        for (var i = len - 1; i >= 0; i--)
        {
            result.Push(GetElement(obj, i));
        }

        return JsValue.Object(result);
    }

    private static JsValue ToSorted(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var cmpV = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (!cmpV.IsUndefined && !AbstractOperations.IsCallable(cmpV))
        {
            throw new JsThrow(realm.NewTypeError("Array.prototype.toSorted: comparator must be a function"));
        }

        var len = ToLength(obj);
        var items = CollectIndexedValues(obj, len);
        StableSort(realm, items, cmpV);
        var result = new JsArray(realm);
        foreach (var item in items)
        {
            result.Push(item.Value);
        }

        return JsValue.Object(result);
    }

    private static JsValue ToSpliced(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var len = ToLength(obj);
        var start = args.Length > 0 ? ClampStart(ToInteger(args[0]), len) : 0;
        int deleteCount;
        if (args.Length < 1)
        {
            deleteCount = 0;
        }
        else if (args.Length < 2)
        {
            deleteCount = len - start;
        }
        else
        {
            deleteCount = Math.Max(0, Math.Min(ToInteger(args[1]), len - start));
        }

        var result = new JsArray(realm);
        for (var i = 0; i < start; i++)
        {
            result.Push(GetElement(obj, i));
        }

        for (var i = 2; i < args.Length; i++)
        {
            result.Push(args[i]);
        }

        for (var i = start + deleteCount; i < len; i++)
        {
            result.Push(GetElement(obj, i));
        }

        return JsValue.Object(result);
    }

    private static JsValue With(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var obj = ThisObject(realm, thisV);
        var len = ToLength(obj);
        if (args.Length < 1)
        {
            throw new JsThrow(realm.NewTypeError("Array.prototype.with: index required"));
        }

        var rel = ToInteger(args[0]);
        var idx = rel < 0 ? len + rel : rel;
        if (idx < 0 || idx >= len)
        {
            throw new JsThrow(realm.NewRangeError("Invalid index"));
        }

        var value = args.Length > 1 ? args[1] : JsValue.Undefined;
        var result = new JsArray(realm);
        for (var i = 0; i < len; i++)
        {
            result.Push(i == idx ? value : GetElement(obj, i));
        }

        return JsValue.Object(result);
    }

}
