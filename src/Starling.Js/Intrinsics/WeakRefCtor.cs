using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>§26.1 The WeakRef constructor + prototype.</summary>
public static class WeakRefCtor
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.WeakRefPrototype;

        var ctor = new JsNativeFunction(realm, "WeakRef", length: 1,
            (newTarget, args) =>
            {
                if (!IntrinsicHelpers.IsConstructInvocation(newTarget))
                {
                    throw new JsThrow(realm.NewTypeError("Constructor WeakRef requires 'new'"));
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
            PropertyDescriptor.Data(JsValue.String("WeakRef"), writable: false, enumerable: false, configurable: true));

        // §26.1.3.2 deref()
        IntrinsicHelpers.DefineMethod(realm, proto, "deref", 0, (thisV, _) =>
        {
            var wr = ThisWeakRef(realm, thisV);
            if (!wr.TryGetTarget(out var target) || target is null)
            {
                return JsValue.Undefined;
            }
            // Spec: add to the kept-alive set so the target stays observable
            // for the rest of the current job. The runtime clears this set at
            // the end of the microtask drain.
            realm.KeptAlive.Add(target);
            return JsValue.Object(target);
        });

        realm.WeakRefConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("WeakRef",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    private static JsWeakRef Construct(JsRealm realm, JsObject instProto, JsValue[] args)
    {
        // §26.1.1.1: target must be an Object (not null, not a primitive,
        // not undefined).
        if (args.Length == 0 || !args[0].IsObject)
        {
            throw new JsThrow(realm.NewTypeError("WeakRef: target must be an object"));
        }

        var target = args[0].AsObject;
        // Pin in the kept-alive set on construction per §26.1.1.1 step 4.
        realm.KeptAlive.Add(target);
        var wr = new JsWeakRef(realm, target);
        if (!ReferenceEquals(instProto, realm.WeakRefPrototype))
        {
            wr.SetPrototypeOf(instProto);
        }

        return wr;
    }

    private static JsWeakRef ThisWeakRef(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is JsWeakRef wr)
        {
            return wr;
        }

        throw new JsThrow(realm.NewTypeError("WeakRef.prototype.deref called on incompatible receiver"));
    }
}
