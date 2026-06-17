using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// §24.1 The Map constructor + §24.1.3 Map.prototype + §24.1.5
/// %MapIteratorPrototype%.
/// </summary>
public static class MapCtor
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.MapPrototype;
        var iterProto = realm.MapIteratorPrototype;

        // §24.1.1.1 Map([iterable]) — constructor. §24.1.1.1 step 1: Map must be
        // invoked with `new` (or a derived super()); a plain call is a TypeError.
        var ctor = new JsNativeFunction(realm, "Map", length: 0,
            (newTarget, args) =>
            {
                if (!IntrinsicHelpers.IsConstructInvocation(newTarget))
                {
                    throw new JsThrow(realm.NewTypeError("Constructor Map requires 'new'"));
                }

                var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
                return JsValue.Object(Construct(realm, instProto, args));
            },
            isConstructor: true);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));

        // §24.1.2.2 Map[Symbol.species] — returns this. We model as a static
        // data accessor returning the ctor; matches V8/SpiderMonkey for the
        // null-derive case.
        var speciesGetter = new JsNativeFunction(realm, "get [Symbol.species]", 0,
            (thisV, _) => thisV, isConstructor: false);
        ctor.DefineOwnProperty(SymbolCtor.Species,
            PropertyDescriptor.Accessor(speciesGetter, null, enumerable: false, configurable: true));

        // -------- Prototype
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
        proto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("Map"), writable: false, enumerable: false, configurable: true));

        // size getter (§24.1.3.10).
        var sizeGetter = new JsNativeFunction(realm, "get size", 0,
            (thisV, _) => JsValue.Number(ThisMap(realm, thisV).Count), isConstructor: false);
        proto.DefineOwnProperty("size",
            PropertyDescriptor.Accessor(sizeGetter, null, enumerable: false, configurable: true));

        IntrinsicHelpers.DefineMethod(realm, proto, "get", 1, (thisV, args) =>
            ThisMap(realm, thisV).Get(args.Length > 0 ? args[0] : JsValue.Undefined));

        IntrinsicHelpers.DefineMethod(realm, proto, "set", 2, (thisV, args) =>
        {
            var m = ThisMap(realm, thisV);
            m.Set(args.Length > 0 ? args[0] : JsValue.Undefined,
                  args.Length > 1 ? args[1] : JsValue.Undefined);
            return thisV;
        });

        IntrinsicHelpers.DefineMethod(realm, proto, "has", 1, (thisV, args) =>
            JsValue.Boolean(ThisMap(realm, thisV).Has(args.Length > 0 ? args[0] : JsValue.Undefined)));

        IntrinsicHelpers.DefineMethod(realm, proto, "delete", 1, (thisV, args) =>
            JsValue.Boolean(ThisMap(realm, thisV).Delete(args.Length > 0 ? args[0] : JsValue.Undefined)));

        IntrinsicHelpers.DefineMethod(realm, proto, "clear", 0, (thisV, _) =>
        {
            ThisMap(realm, thisV).Clear();
            return JsValue.Undefined;
        });

        IntrinsicHelpers.DefineMethod(realm, proto, "forEach", 1, (thisV, args) => ForEach(realm, thisV, args));

        IntrinsicHelpers.DefineMethod(realm, proto, "keys", 0, (thisV, _) =>
            JsValue.Object(new JsMapIterator(realm, ThisMap(realm, thisV), MapIteratorKind.Key)));
        IntrinsicHelpers.DefineMethod(realm, proto, "values", 0, (thisV, _) =>
            JsValue.Object(new JsMapIterator(realm, ThisMap(realm, thisV), MapIteratorKind.Value)));
        var entries = IntrinsicHelpers.DefineMethod(realm, proto, "entries", 0, (thisV, _) =>
            JsValue.Object(new JsMapIterator(realm, ThisMap(realm, thisV), MapIteratorKind.KeyAndValue)));

        // §24.1.3.12 Map.prototype[@@iterator] is the same function as entries.
        proto.DefineOwnProperty(SymbolCtor.Iterator,
            PropertyDescriptor.BuiltinMethod(JsValue.Object(entries)));

        // -------- %MapIteratorPrototype%
        var iterNext = new JsNativeFunction(realm, "next", 0,
            (thisV, _) => MapIteratorNext(realm, thisV), isConstructor: false);
        iterNext.DefineOwnProperty("name",
            PropertyDescriptor.Data(JsValue.String("next"), writable: false, enumerable: false, configurable: true));
        iterProto.DefineOwnProperty("next",
            PropertyDescriptor.BuiltinMethod(JsValue.Object(iterNext)));
        iterProto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("Map Iterator"), writable: false, enumerable: false, configurable: true));

        realm.MapConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("Map",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    private static JsMap Construct(JsRealm realm, JsObject instProto, JsValue[] args)
    {
        var map = new JsMap(realm);
        if (!ReferenceEquals(instProto, realm.MapPrototype))
        {
            map.SetPrototypeOf(instProto);
        }

        if (args.Length == 0 || args[0].IsNullish)
        {
            return map;
        }

        var adder = AbstractOperations.Get(realm.ActiveVm, map, "set");
        if (!AbstractOperations.IsCallable(adder))
        {
            throw new JsThrow(realm.NewTypeError("Map constructor set method is not callable"));
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

            JsValue entry;
            try
            {
                entry = AbstractOperations.IteratorValue(realm.ActiveVm, next.Value);
            }
            catch
            {
                AbstractOperations.IteratorClose(realm.ActiveVm, record, isThrowing: true);
                throw;
            }
            if (!entry.IsObject)
            {
                AbstractOperations.IteratorClose(realm.ActiveVm, record, isThrowing: true);
                throw new JsThrow(realm.NewTypeError("Map iterable entry is not an object"));
            }
            try
            {
                var key = AbstractOperations.Get(realm.ActiveVm, entry.AsObject, "0");
                var value = AbstractOperations.Get(realm.ActiveVm, entry.AsObject, "1");
                AbstractOperations.Call(realm.ActiveVm, adder, JsValue.Object(map), new[] { key, value });
            }
            catch
            {
                AbstractOperations.IteratorClose(realm.ActiveVm, record, isThrowing: true);
                throw;
            }
        }
        return map;
    }

    private static JsMap ThisMap(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is JsMap m)
        {
            return m;
        }

        throw new JsThrow(realm.NewTypeError("Map.prototype method called on incompatible receiver"));
    }

    private static JsValue MapIteratorNext(JsRealm realm, JsValue thisV)
    {
        if (!thisV.IsObject || thisV.AsObject is not JsMapIterator it)
        {
            throw new JsThrow(realm.NewTypeError("Map Iterator.prototype.next called on incompatible receiver"));
        }

        return it.Next(realm);
    }

    private static JsValue ForEach(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var map = ThisMap(realm, thisV);
        if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
        {
            throw new JsThrow(realm.NewTypeError("Map.prototype.forEach requires a callback"));
        }

        var cb = args[0];
        var thisArg = args.Length > 1 ? args[1] : JsValue.Undefined;
        foreach (var (k, v) in map.LiveEntries())
        {
            AbstractOperations.Call(realm.ActiveVm, cb, thisArg, new[] { v, k, thisV });
        }
        return JsValue.Undefined;
    }
}
