using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>§24.4 The WeakSet constructor + prototype.</summary>
public static class WeakSetCtor
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.WeakSetPrototype;

        var ctor = new JsNativeFunction(realm, "WeakSet", length: 0,
            (newTarget, args) =>
            {
                if (!IntrinsicHelpers.IsConstructInvocation(newTarget))
                {
                    throw new JsThrow(realm.NewTypeError("Constructor WeakSet requires 'new'"));
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
            PropertyDescriptor.Data(JsValue.String("WeakSet"), writable: false, enumerable: false, configurable: true));

        IntrinsicHelpers.DefineMethod(realm, proto, "add", 1, (thisV, args) =>
        {
            var s = ThisWeakSet(realm, thisV);
            if (args.Length == 0 || !args[0].IsObject)
            {
                throw new JsThrow(realm.NewTypeError("WeakSet value must be an object"));
            }

            s.Add(args[0].AsObject);
            return thisV;
        });

        IntrinsicHelpers.DefineMethod(realm, proto, "has", 1, (thisV, args) =>
        {
            var s = ThisWeakSet(realm, thisV);
            if (args.Length == 0 || !args[0].IsObject)
            {
                return JsValue.False;
            }

            return JsValue.Boolean(s.Has(args[0].AsObject));
        });

        IntrinsicHelpers.DefineMethod(realm, proto, "delete", 1, (thisV, args) =>
        {
            var s = ThisWeakSet(realm, thisV);
            if (args.Length == 0 || !args[0].IsObject)
            {
                return JsValue.False;
            }

            return JsValue.Boolean(s.Delete(args[0].AsObject));
        });

        realm.WeakSetConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("WeakSet",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    private static JsWeakSet Construct(JsRealm realm, JsObject instProto, JsValue[] args)
    {
        var s = new JsWeakSet(realm);
        if (!ReferenceEquals(instProto, realm.WeakSetPrototype))
        {
            s.SetPrototypeOf(instProto);
        }

        if (args.Length == 0 || args[0].IsNullish)
        {
            return s;
        }

        var adder = AbstractOperations.Get(realm.ActiveVm, s, "add");
        if (!AbstractOperations.IsCallable(adder))
        {
            throw new JsThrow(realm.NewTypeError("WeakSet constructor add method is not callable"));
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

            try
            {
                var value = AbstractOperations.IteratorValue(realm.ActiveVm, next.Value);
                AbstractOperations.Call(realm.ActiveVm, adder, JsValue.Object(s), new[] { value });
            }
            catch
            {
                AbstractOperations.IteratorClose(realm.ActiveVm, record, isThrowing: true);
                throw;
            }
        }
        return s;
    }

    private static JsWeakSet ThisWeakSet(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is JsWeakSet s)
        {
            return s;
        }

        throw new JsThrow(realm.NewTypeError("WeakSet.prototype method called on incompatible receiver"));
    }
}
