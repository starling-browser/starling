using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>§26.2 The FinalizationRegistry constructor + prototype.</summary>
public static class FinalizationRegistryCtor
{
    public static void Install(JsRealm realm, JsRuntime? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.FinalizationRegistryPrototype;

        var ctor = new JsNativeFunction(realm, "FinalizationRegistry", length: 1,
            (newTarget, args) =>
            {
                if (!IntrinsicHelpers.IsConstructInvocation(newTarget))
                {
                    throw new JsThrow(realm.NewTypeError("Constructor FinalizationRegistry requires 'new'"));
                }

                var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
                return JsValue.Object(Construct(realm, runtime, instProto, args));
            },
            isConstructor: true);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));

        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
        proto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("FinalizationRegistry"), writable: false, enumerable: false, configurable: true));

        // §26.2.3.2 register(target, heldValue, unregisterToken?)
        IntrinsicHelpers.DefineMethod(realm, proto, "register", 2, (thisV, args) =>
        {
            var fr = ThisRegistry(realm, thisV);
            if (args.Length == 0 || !args[0].IsObject)
            {
                throw new JsThrow(realm.NewTypeError("FinalizationRegistry.register: target must be an object"));
            }

            var target = args[0].AsObject;
            var held = args.Length > 1 ? args[1] : JsValue.Undefined;
            // Spec: heldValue must not be the same as target.
            if (held.IsObject && ReferenceEquals(held.AsObject, target))
            {
                throw new JsThrow(realm.NewTypeError("FinalizationRegistry.register: heldValue must not be the target"));
            }

            JsObject? token = null;
            if (args.Length > 2)
            {
                var t = args[2];
                if (t.IsObject)
                {
                    token = t.AsObject;
                }
                else if (!t.IsUndefined)
                {
                    throw new JsThrow(realm.NewTypeError("FinalizationRegistry.register: unregisterToken must be an object or undefined"));
                }
            }
            fr.Register(target, held, token);
            return JsValue.Undefined;
        });

        // §26.2.3.3 unregister(unregisterToken)
        IntrinsicHelpers.DefineMethod(realm, proto, "unregister", 1, (thisV, args) =>
        {
            var fr = ThisRegistry(realm, thisV);
            if (args.Length == 0 || !args[0].IsObject)
            {
                throw new JsThrow(realm.NewTypeError("FinalizationRegistry.unregister: token must be an object"));
            }

            return JsValue.Boolean(fr.Unregister(args[0].AsObject));
        });

        realm.FinalizationRegistryConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("FinalizationRegistry",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    private static JsFinalizationRegistry Construct(JsRealm realm, JsRuntime? runtime, JsObject instProto, JsValue[] args)
    {
        // §26.2.1.1: cleanupCallback must be callable.
        if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
        {
            throw new JsThrow(realm.NewTypeError("FinalizationRegistry: cleanupCallback must be callable"));
        }

        var fr = new JsFinalizationRegistry(realm, args[0]);
        if (!ReferenceEquals(instProto, realm.FinalizationRegistryPrototype))
        {
            fr.SetPrototypeOf(instProto);
        }

        fr.JsRuntimeHandle = runtime;
        realm.FinalizationRegistries.Add(new WeakReference<JsFinalizationRegistry>(fr));
        return fr;
    }

    private static JsFinalizationRegistry ThisRegistry(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is JsFinalizationRegistry fr)
        {
            return fr;
        }

        throw new JsThrow(realm.NewTypeError("FinalizationRegistry.prototype method called on incompatible receiver"));
    }
}
