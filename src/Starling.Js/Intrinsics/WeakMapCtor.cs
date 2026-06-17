using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>§24.3 The WeakMap constructor + prototype. Keys must be objects;
/// no iteration / no <c>size</c>.</summary>
public static class WeakMapCtor
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.WeakMapPrototype;

        var ctor = new JsNativeFunction(realm, "WeakMap", length: 0,
            (newTarget, args) =>
            {
                if (!IntrinsicHelpers.IsConstructInvocation(newTarget))
                {
                    throw new JsThrow(realm.NewTypeError("Constructor WeakMap requires 'new'"));
                }

                var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
                return JsValue.Object(Construct(realm, instProto, args));
            },
            isConstructor: true);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));

        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
        proto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("WeakMap"), writable: false, enumerable: false, configurable: true));

        IntrinsicHelpers.DefineMethod(realm, proto, "get", 1, (thisV, args) =>
        {
            var m = ThisWeakMap(realm, thisV);
            if (args.Length == 0 || !args[0].IsObject)
            {
                return JsValue.Undefined;
            }

            return m.Get(args[0].AsObject);
        });

        IntrinsicHelpers.DefineMethod(realm, proto, "set", 2, (thisV, args) =>
        {
            var m = ThisWeakMap(realm, thisV);
            if (args.Length == 0 || !args[0].IsObject)
            {
                throw new JsThrow(realm.NewTypeError("WeakMap key must be an object"));
            }

            m.Set(args[0].AsObject, args.Length > 1 ? args[1] : JsValue.Undefined);
            return thisV;
        });

        IntrinsicHelpers.DefineMethod(realm, proto, "has", 1, (thisV, args) =>
        {
            var m = ThisWeakMap(realm, thisV);
            if (args.Length == 0 || !args[0].IsObject)
            {
                return JsValue.False;
            }

            return JsValue.Boolean(m.Has(args[0].AsObject));
        });

        IntrinsicHelpers.DefineMethod(realm, proto, "delete", 1, (thisV, args) =>
        {
            var m = ThisWeakMap(realm, thisV);
            if (args.Length == 0 || !args[0].IsObject)
            {
                return JsValue.False;
            }

            return JsValue.Boolean(m.Delete(args[0].AsObject));
        });

        realm.WeakMapConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("WeakMap",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    private static JsWeakMap Construct(JsRealm realm, JsObject instProto, JsValue[] args)
    {
        var m = new JsWeakMap(realm);
        if (!ReferenceEquals(instProto, realm.WeakMapPrototype))
        {
            m.SetPrototypeOf(instProto);
        }

        if (args.Length == 0 || args[0].IsNullish)
        {
            return m;
        }

        var adder = AbstractOperations.Get(realm.ActiveVm, m, "set");
        if (!AbstractOperations.IsCallable(adder))
        {
            throw new JsThrow(realm.NewTypeError("WeakMap constructor set method is not callable"));
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
                throw new JsThrow(realm.NewTypeError("WeakMap iterable entry is not an object"));
            }
            try
            {
                var key = AbstractOperations.Get(realm.ActiveVm, entry.AsObject, "0");
                var value = AbstractOperations.Get(realm.ActiveVm, entry.AsObject, "1");
                AbstractOperations.Call(realm.ActiveVm, adder, JsValue.Object(m), new[] { key, value });
            }
            catch
            {
                AbstractOperations.IteratorClose(realm.ActiveVm, record, isThrowing: true);
                throw;
            }
        }
        return m;
    }

    private static JsWeakMap ThisWeakMap(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is JsWeakMap m)
        {
            return m;
        }

        throw new JsThrow(realm.NewTypeError("WeakMap.prototype method called on incompatible receiver"));
    }
}
