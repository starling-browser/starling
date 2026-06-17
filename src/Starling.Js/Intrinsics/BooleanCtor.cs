using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>§21.2 Boolean Objects. Installs Boolean and Boolean.prototype.</summary>
public static class BooleanCtor
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.BooleanPrototype;

        JsNativeFunction? ctor = null;
        ctor = new JsNativeFunction(realm, "Boolean", length: 1, (newTarget, args) =>
        {
            var b = JsValue.ToBoolean(args.Length > 0 ? args[0] : JsValue.Undefined);
            // §21.3.1.1: called as a function → return the primitive; constructed
            // (new / derived super()) → return a wrapper whose [[Prototype]] is
            // new.target.prototype (so `class B extends Boolean {}` works).
            if (IntrinsicHelpers.IsConstructInvocation(newTarget))
            {
                var box = realm.BoxBoolean(JsValue.Boolean(b));
                var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
                if (!ReferenceEquals(instProto, proto))
                {
                    box.SetPrototypeOf(instProto);
                }

                return JsValue.Object(box);
            }
            return JsValue.Boolean(b);
        }, isConstructor: true);
        DefineData(ctor, "prototype", JsValue.Object(proto), false, false, false);

        // Bulk-install constructor + the two prototype methods by adopting one
        // precomputed shape. Same creation order (constructor, toString, valueOf),
        // so getOwnPropertyNames order is unchanged and the result is byte-identical.
        IntrinsicHelpers.BulkInstallBuiltins(realm, proto, new[]
        {
            new IntrinsicHelpers.BulkMember("constructor", 0, null, JsValue.Object(ctor)),
            new IntrinsicHelpers.BulkMember("toString", 0, (thisV, _) => JsValue.String(ThisBoolean(realm, thisV) ? "true" : "false")),
            new IntrinsicHelpers.BulkMember("valueOf", 0, (thisV, _) => JsValue.Boolean(ThisBoolean(realm, thisV))),
        });

        realm.BooleanConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("Boolean", PropertyDescriptor.Data(JsValue.Object(ctor), true, false, true));
    }

    private static bool ThisBoolean(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsBoolean)
        {
            return thisV.AsBool;
        }

        if (thisV.IsObject)
        {
            var slot = thisV.AsObject.GetOwnPropertyDescriptor("__primitiveValue");
            if (slot is { } d && d.Value.IsBoolean)
            {
                return d.Value.AsBool;
            }
        }
        throw new JsThrow(realm.NewTypeError("Boolean.prototype method called on incompatible receiver"));
    }

    private static void DefineData(JsObject target, string name, JsValue value, bool writable, bool enumerable, bool configurable)
        => target.DefineOwnProperty(name, PropertyDescriptor.Data(value, writable, enumerable, configurable));
}
