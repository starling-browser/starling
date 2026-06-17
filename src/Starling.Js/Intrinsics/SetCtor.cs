using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// §24.2 The Set constructor + Set.prototype + %SetIteratorPrototype%.
/// Includes the ES2025 (Stage 4) set-theoretic methods: union, intersection,
/// difference, symmetricDifference, isSubsetOf, isSupersetOf, isDisjointFrom.
/// </summary>
/// <remarks>
/// <para>The set-method "other" argument is a <i>SetLike</i> per spec — an
/// object exposing <c>size</c> (number), <c>has</c> (callable), and
/// <c>keys</c> (callable returning an iterator). We probe those three slots
/// up front and surface a TypeError if any are missing/incorrect, then
/// dispatch into the matching algorithm.</para>
/// </remarks>
public static class SetCtor
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.SetPrototype;
        var iterProto = realm.SetIteratorPrototype;

        // §24.2.1.1 Set([iterable]). §24.2.1.1 step 1: requires `new`.
        var ctor = new JsNativeFunction(realm, "Set", length: 0,
            (newTarget, args) =>
            {
                if (!IntrinsicHelpers.IsConstructInvocation(newTarget))
                {
                    throw new JsThrow(realm.NewTypeError("Constructor Set requires 'new'"));
                }

                var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
                return JsValue.Object(Construct(realm, instProto, args));
            },
            isConstructor: true);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));

        var speciesGetter = new JsNativeFunction(realm, "get [Symbol.species]", 0,
            (thisV, _) => thisV, isConstructor: false);
        ctor.DefineOwnProperty(SymbolCtor.Species,
            PropertyDescriptor.Accessor(speciesGetter, null, enumerable: false, configurable: true));

        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
        proto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("Set"), writable: false, enumerable: false, configurable: true));

        var sizeGetter = new JsNativeFunction(realm, "get size", 0,
            (thisV, _) => JsValue.Number(ThisSet(realm, thisV).Count), isConstructor: false);
        proto.DefineOwnProperty("size",
            PropertyDescriptor.Accessor(sizeGetter, null, enumerable: false, configurable: true));

        IntrinsicHelpers.DefineMethod(realm, proto, "add", 1, (thisV, args) =>
        {
            ThisSet(realm, thisV).Add(args.Length > 0 ? args[0] : JsValue.Undefined);
            return thisV;
        });
        IntrinsicHelpers.DefineMethod(realm, proto, "has", 1, (thisV, args) =>
            JsValue.Boolean(ThisSet(realm, thisV).Has(args.Length > 0 ? args[0] : JsValue.Undefined)));
        IntrinsicHelpers.DefineMethod(realm, proto, "delete", 1, (thisV, args) =>
            JsValue.Boolean(ThisSet(realm, thisV).Delete(args.Length > 0 ? args[0] : JsValue.Undefined)));
        IntrinsicHelpers.DefineMethod(realm, proto, "clear", 0, (thisV, _) =>
        {
            ThisSet(realm, thisV).Clear();
            return JsValue.Undefined;
        });
        IntrinsicHelpers.DefineMethod(realm, proto, "forEach", 1, (thisV, args) => ForEach(realm, thisV, args));

        var values = IntrinsicHelpers.DefineMethod(realm, proto, "values", 0, (thisV, _) =>
            JsValue.Object(new JsSetIterator(realm, ThisSet(realm, thisV), SetIteratorKind.Value)));
        // §24.2.3.8: Set.prototype.keys is the same function as values.
        proto.DefineOwnProperty("keys",
            PropertyDescriptor.BuiltinMethod(JsValue.Object(values)));
        IntrinsicHelpers.DefineMethod(realm, proto, "entries", 0, (thisV, _) =>
            JsValue.Object(new JsSetIterator(realm, ThisSet(realm, thisV), SetIteratorKind.KeyAndValue)));
        // §24.2.3.11 [@@iterator] is the same function as values.
        proto.DefineOwnProperty(SymbolCtor.Iterator,
            PropertyDescriptor.BuiltinMethod(JsValue.Object(values)));

        // -------- ES2025 set-theoretic operations.
        IntrinsicHelpers.DefineMethod(realm, proto, "union", 1, (thisV, args) => Union(realm, thisV, args));
        IntrinsicHelpers.DefineMethod(realm, proto, "intersection", 1, (thisV, args) => Intersection(realm, thisV, args));
        IntrinsicHelpers.DefineMethod(realm, proto, "difference", 1, (thisV, args) => Difference(realm, thisV, args));
        IntrinsicHelpers.DefineMethod(realm, proto, "symmetricDifference", 1, (thisV, args) => SymmetricDifference(realm, thisV, args));
        IntrinsicHelpers.DefineMethod(realm, proto, "isSubsetOf", 1, (thisV, args) => IsSubsetOf(realm, thisV, args));
        IntrinsicHelpers.DefineMethod(realm, proto, "isSupersetOf", 1, (thisV, args) => IsSupersetOf(realm, thisV, args));
        IntrinsicHelpers.DefineMethod(realm, proto, "isDisjointFrom", 1, (thisV, args) => IsDisjointFrom(realm, thisV, args));

        // -------- %SetIteratorPrototype%
        var iterNext = new JsNativeFunction(realm, "next", 0,
            (thisV, _) => SetIteratorNext(realm, thisV), isConstructor: false);
        iterNext.DefineOwnProperty("name",
            PropertyDescriptor.Data(JsValue.String("next"), writable: false, enumerable: false, configurable: true));
        iterProto.DefineOwnProperty("next",
            PropertyDescriptor.BuiltinMethod(JsValue.Object(iterNext)));
        iterProto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("Set Iterator"), writable: false, enumerable: false, configurable: true));

        realm.SetConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("Set",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    private static JsSet Construct(JsRealm realm, JsObject instProto, JsValue[] args)
    {
        var set = new JsSet(realm);
        if (!ReferenceEquals(instProto, realm.SetPrototype))
        {
            set.SetPrototypeOf(instProto);
        }

        if (args.Length == 0 || args[0].IsNullish)
        {
            return set;
        }

        var adder = AbstractOperations.Get(realm.ActiveVm, set, "add");
        if (!AbstractOperations.IsCallable(adder))
        {
            throw new JsThrow(realm.NewTypeError("Set constructor add method is not callable"));
        }

        var iterable = args[0];
        var record = AbstractOperations.GetIterator(realm, realm.ActiveVm, iterable);
        while (true)
        {
            var next = AbstractOperations.IteratorStep(realm, realm.ActiveVm, ref record);
            if (next is null)
            {
                break;
            }

            JsValue value;
            try
            {
                value = AbstractOperations.IteratorValue(realm.ActiveVm, next.Value);
                AbstractOperations.Call(realm.ActiveVm, adder, JsValue.Object(set), new[] { value });
            }
            catch
            {
                AbstractOperations.IteratorClose(realm.ActiveVm, record, isThrowing: true);
                throw;
            }
        }
        return set;
    }

    private static JsSet ThisSet(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is JsSet s)
        {
            return s;
        }

        throw new JsThrow(realm.NewTypeError("Set.prototype method called on incompatible receiver"));
    }

    private static JsValue SetIteratorNext(JsRealm realm, JsValue thisV)
    {
        if (!thisV.IsObject || thisV.AsObject is not JsSetIterator it)
        {
            throw new JsThrow(realm.NewTypeError("Set Iterator.prototype.next called on incompatible receiver"));
        }

        return it.Next(realm);
    }

    private static JsValue ForEach(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var set = ThisSet(realm, thisV);
        if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
        {
            throw new JsThrow(realm.NewTypeError("Set.prototype.forEach requires a callback"));
        }

        var cb = args[0];
        var thisArg = args.Length > 1 ? args[1] : JsValue.Undefined;
        foreach (var v in set.LiveValues())
        {
            AbstractOperations.Call(realm.ActiveVm, cb, thisArg, new[] { v, v, thisV });
        }
        return JsValue.Undefined;
    }

    // ------------------------------------------------------------------
    //                         Set-theoretic algorithms
    // ------------------------------------------------------------------

    /// <summary>§24.2.1.2 GetSetRecord — resolves the SetLike contract.</summary>
    private readonly struct SetRecord
    {
        public readonly JsObject Other;
        public readonly double Size;
        public readonly JsValue HasFn;
        public readonly JsValue KeysFn;
        public SetRecord(JsObject other, double size, JsValue has, JsValue keys)
        {
            Other = other; Size = size; HasFn = has; KeysFn = keys;
        }
    }

    private static SetRecord GetSetRecord(JsRealm realm, JsValue other)
    {
        if (!other.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("set-like argument must be an object"));
        }

        var obj = other.AsObject;
        var rawSize = AbstractOperations.Get(realm.ActiveVm, obj, "size");
        var numSize = JsValue.ToNumber(rawSize);
        if (double.IsNaN(numSize))
        {
            throw new JsThrow(realm.NewTypeError("set-like.size must be a number"));
        }

        var size = numSize < 0 ? 0 : Math.Floor(numSize);
        var has = AbstractOperations.Get(realm.ActiveVm, obj, "has");
        if (!AbstractOperations.IsCallable(has))
        {
            throw new JsThrow(realm.NewTypeError("set-like.has must be callable"));
        }

        var keys = AbstractOperations.Get(realm.ActiveVm, obj, "keys");
        if (!AbstractOperations.IsCallable(keys))
        {
            throw new JsThrow(realm.NewTypeError("set-like.keys must be callable"));
        }

        return new SetRecord(obj, size, has, keys);
    }

    private static IteratorRecord OpenKeysIterator(JsRealm realm, SetRecord rec)
    {
        var iter = AbstractOperations.Call(realm.ActiveVm, rec.KeysFn, JsValue.Object(rec.Other), Array.Empty<JsValue>());
        if (!iter.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("set-like.keys() did not return an object"));
        }

        var next = AbstractOperations.Get(realm.ActiveVm, iter.AsObject, "next");
        if (!AbstractOperations.IsCallable(next))
        {
            throw new JsThrow(realm.NewTypeError("set-like.keys() iterator missing next"));
        }

        return new IteratorRecord(iter, next, Done: false);
    }

    private static JsValue Union(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var self = ThisSet(realm, thisV);
        var rec = GetSetRecord(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var result = new JsSet(realm);
        foreach (var v in self.LiveValues())
        {
            result.Add(v);
        }

        var record = OpenKeysIterator(realm, rec);
        while (true)
        {
            var next = AbstractOperations.IteratorStep(realm, realm.ActiveVm, ref record);
            if (next is null)
            {
                break;
            }

            result.Add(AbstractOperations.IteratorValue(realm.ActiveVm, next.Value));
        }
        return JsValue.Object(result);
    }

    private static JsValue Intersection(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var self = ThisSet(realm, thisV);
        var rec = GetSetRecord(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var result = new JsSet(realm);

        // Spec optimization: walk the smaller side. With size info we can
        // pick the receiver vs the other.
        if (self.Count <= rec.Size)
        {
            foreach (var v in self.LiveValues())
            {
                var inOther = AbstractOperations.Call(realm.ActiveVm, rec.HasFn, JsValue.Object(rec.Other), new[] { v });
                if (JsValue.ToBoolean(inOther))
                {
                    result.Add(v);
                }
            }
        }
        else
        {
            var record = OpenKeysIterator(realm, rec);
            while (true)
            {
                var next = AbstractOperations.IteratorStep(realm, realm.ActiveVm, ref record);
                if (next is null)
                {
                    break;
                }

                var v = AbstractOperations.IteratorValue(realm.ActiveVm, next.Value);
                if (self.Has(v))
                {
                    result.Add(v);
                }
            }
        }
        return JsValue.Object(result);
    }

    private static JsValue Difference(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var self = ThisSet(realm, thisV);
        var rec = GetSetRecord(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var result = new JsSet(realm);

        if (self.Count <= rec.Size)
        {
            foreach (var v in self.LiveValues())
            {
                var inOther = AbstractOperations.Call(realm.ActiveVm, rec.HasFn, JsValue.Object(rec.Other), new[] { v });
                if (!JsValue.ToBoolean(inOther))
                {
                    result.Add(v);
                }
            }
        }
        else
        {
            foreach (var v in self.LiveValues())
            {
                result.Add(v);
            }

            var record = OpenKeysIterator(realm, rec);
            while (true)
            {
                var next = AbstractOperations.IteratorStep(realm, realm.ActiveVm, ref record);
                if (next is null)
                {
                    break;
                }

                result.Delete(AbstractOperations.IteratorValue(realm.ActiveVm, next.Value));
            }
        }
        return JsValue.Object(result);
    }

    private static JsValue SymmetricDifference(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var self = ThisSet(realm, thisV);
        var rec = GetSetRecord(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var result = new JsSet(realm);
        foreach (var v in self.LiveValues())
        {
            result.Add(v);
        }

        var record = OpenKeysIterator(realm, rec);
        while (true)
        {
            var next = AbstractOperations.IteratorStep(realm, realm.ActiveVm, ref record);
            if (next is null)
            {
                break;
            }

            var v = AbstractOperations.IteratorValue(realm.ActiveVm, next.Value);
            if (result.Has(v))
            {
                result.Delete(v);
            }
            else
            {
                result.Add(v);
            }
        }
        return JsValue.Object(result);
    }

    private static JsValue IsSubsetOf(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var self = ThisSet(realm, thisV);
        var rec = GetSetRecord(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        if (self.Count > rec.Size)
        {
            return JsValue.False;
        }

        foreach (var v in self.LiveValues())
        {
            var inOther = AbstractOperations.Call(realm.ActiveVm, rec.HasFn, JsValue.Object(rec.Other), new[] { v });
            if (!JsValue.ToBoolean(inOther))
            {
                return JsValue.False;
            }
        }
        return JsValue.True;
    }

    private static JsValue IsSupersetOf(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var self = ThisSet(realm, thisV);
        var rec = GetSetRecord(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        if (self.Count < rec.Size)
        {
            return JsValue.False;
        }

        var record = OpenKeysIterator(realm, rec);
        while (true)
        {
            var next = AbstractOperations.IteratorStep(realm, realm.ActiveVm, ref record);
            if (next is null)
            {
                break;
            }

            var v = AbstractOperations.IteratorValue(realm.ActiveVm, next.Value);
            if (!self.Has(v))
            {
                AbstractOperations.IteratorClose(realm.ActiveVm, record);
                return JsValue.False;
            }
        }
        return JsValue.True;
    }

    private static JsValue IsDisjointFrom(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var self = ThisSet(realm, thisV);
        var rec = GetSetRecord(realm, args.Length > 0 ? args[0] : JsValue.Undefined);

        if (self.Count <= rec.Size)
        {
            foreach (var v in self.LiveValues())
            {
                var inOther = AbstractOperations.Call(realm.ActiveVm, rec.HasFn, JsValue.Object(rec.Other), new[] { v });
                if (JsValue.ToBoolean(inOther))
                {
                    return JsValue.False;
                }
            }
        }
        else
        {
            var record = OpenKeysIterator(realm, rec);
            while (true)
            {
                var next = AbstractOperations.IteratorStep(realm, realm.ActiveVm, ref record);
                if (next is null)
                {
                    break;
                }

                var v = AbstractOperations.IteratorValue(realm.ActiveVm, next.Value);
                if (self.Has(v))
                {
                    AbstractOperations.IteratorClose(realm.ActiveVm, record);
                    return JsValue.False;
                }
            }
        }
        return JsValue.True;
    }
}
