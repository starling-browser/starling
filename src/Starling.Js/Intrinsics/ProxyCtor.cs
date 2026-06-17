using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// §28.2 The Proxy constructor and the <c>Proxy.revocable</c> factory. The
/// runtime <see cref="JsProxy"/> exotic owns trap dispatch; this installer
/// only wires the constructor + revocable() helper into the global scope.
/// </summary>
public static class ProxyCtor
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);

        // Spec §28.2.1.1: Proxy is callable only with `new`. Calling without
        // `new` throws TypeError; we approximate via an isConstructor=true
        // native function whose body assumes constructor calls.
        var ctor = new JsNativeFunction(realm, "Proxy", length: 2, (thisV, args) =>
        {
            var target = args.Length > 0 ? args[0] : JsValue.Undefined;
            var handler = args.Length > 1 ? args[1] : JsValue.Undefined;
            return JsValue.Object(MakeProxy(realm, target, handler));
        }, isConstructor: true);

        // Proxy.revocable(target, handler) → { proxy, revoke }
        var revocable = new JsNativeFunction(realm, "revocable", length: 2, (thisV, args) =>
        {
            var target = args.Length > 0 ? args[0] : JsValue.Undefined;
            var handler = args.Length > 1 ? args[1] : JsValue.Undefined;
            var proxy = MakeProxy(realm, target, handler);
            var revokeFn = new JsNativeFunction(realm, "", length: 0, (_, _) =>
            {
                proxy.Revoke();
                return JsValue.Undefined;
            }, isConstructor: false);
            var result = realm.NewOrdinaryObject();
            result.DefineOwnProperty("proxy",
                PropertyDescriptor.Data(JsValue.Object(proxy), writable: true, enumerable: true, configurable: true));
            result.DefineOwnProperty("revoke",
                PropertyDescriptor.Data(JsValue.Object(revokeFn), writable: true, enumerable: true, configurable: true));
            return JsValue.Object(result);
        }, isConstructor: false);

        ctor.DefineOwnProperty("revocable",
            PropertyDescriptor.BuiltinMethod(JsValue.Object(revocable)));

        realm.ProxyConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("Proxy",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    private static JsProxy MakeProxy(JsRealm realm, JsValue target, JsValue handler)
    {
        if (!target.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("Cannot create proxy with a non-object as target"));
        }

        if (!handler.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("Cannot create proxy with a non-object as handler"));
        }

        return new JsProxy(realm, target.AsObject, handler.AsObject);
    }
}
