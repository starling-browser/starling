using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// §28.1 The Reflect namespace object. Mirrors the proxy-intercepted internal
/// methods one-for-one — each call returns a value where the corresponding
/// regular operation would throw. Reflect itself is not callable and not a
/// constructor.
/// </summary>
public static class ReflectObj
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var reflect = realm.NewOrdinaryObject();

        DefineMethod(realm, reflect, "get", length: 2, (thisV, args) =>
        {
            var target = RequireObject(realm, args, 0, "get");
            var key = AbstractOperations.ToPropertyKey(args.Length > 1 ? args[1] : JsValue.Undefined);
            if (args.Length > 2)
            {
                return AbstractOperations.GetWithReceiver(realm.ActiveVm, target, key, args[2]);
            }

            return AbstractOperations.Get(realm.ActiveVm, target, key);
        });

        DefineMethod(realm, reflect, "set", length: 3, (thisV, args) =>
        {
            var target = RequireObject(realm, args, 0, "set");
            var key = AbstractOperations.ToPropertyKey(args.Length > 1 ? args[1] : JsValue.Undefined);
            var value = args.Length > 2 ? args[2] : JsValue.Undefined;
            if (args.Length > 3)
            {
                return JsValue.Boolean(AbstractOperations.SetWithReceiver(realm.ActiveVm, target, key, value, args[3]));
            }

            return JsValue.Boolean(AbstractOperations.Set(realm.ActiveVm, target, key, value));
        });

        DefineMethod(realm, reflect, "has", length: 2, (thisV, args) =>
        {
            var target = RequireObject(realm, args, 0, "has");
            var key = AbstractOperations.ToPropertyKey(args.Length > 1 ? args[1] : JsValue.Undefined);
            return JsValue.Boolean(target.Has(key));
        });

        DefineMethod(realm, reflect, "deleteProperty", length: 2, (thisV, args) =>
        {
            var target = RequireObject(realm, args, 0, "deleteProperty");
            var key = AbstractOperations.ToPropertyKey(args.Length > 1 ? args[1] : JsValue.Undefined);
            return JsValue.Boolean(target.Delete(key));
        });

        DefineMethod(realm, reflect, "getOwnPropertyDescriptor", length: 2, (thisV, args) =>
        {
            var target = RequireObject(realm, args, 0, "getOwnPropertyDescriptor");
            var key = AbstractOperations.ToPropertyKey(args.Length > 1 ? args[1] : JsValue.Undefined);
            var d = target.GetOwnPropertyDescriptor(key);
            if (d is null)
            {
                return JsValue.Undefined;
            }

            return FromPropertyDescriptor(realm, d.Value);
        });

        DefineMethod(realm, reflect, "defineProperty", length: 3, (thisV, args) =>
        {
            var target = RequireObject(realm, args, 0, "defineProperty");
            var key = AbstractOperations.ToPropertyKey(args.Length > 1 ? args[1] : JsValue.Undefined);
            var descObj = args.Length > 2 ? args[2] : JsValue.Undefined;
            var desc = ToPropertyDescriptor(realm, descObj);
            return JsValue.Boolean(
                Starling.Js.Runtime.JsMappedArguments.DefineFromUser(target, key, desc, descObj.AsObject));
        });

        DefineMethod(realm, reflect, "getPrototypeOf", length: 1, (thisV, args) =>
        {
            var target = RequireObject(realm, args, 0, "getPrototypeOf");
            var proto = target.GetPrototypeOf();
            return proto is null ? JsValue.Null : JsValue.Object(proto);
        });

        DefineMethod(realm, reflect, "setPrototypeOf", length: 2, (thisV, args) =>
        {
            var target = RequireObject(realm, args, 0, "setPrototypeOf");
            var protoV = args.Length > 1 ? args[1] : JsValue.Undefined;
            JsObject? proto;
            if (protoV.IsNull)
            {
                proto = null;
            }
            else if (protoV.IsObject)
            {
                proto = protoV.AsObject;
            }
            else
            {
                throw new JsThrow(realm.NewTypeError("Reflect.setPrototypeOf: prototype must be Object or null"));
            }

            return JsValue.Boolean(target.SetPrototypeOf(proto));
        });

        DefineMethod(realm, reflect, "isExtensible", length: 1, (thisV, args) =>
        {
            var target = RequireObject(realm, args, 0, "isExtensible");
            return JsValue.Boolean(target.Extensible);
        });

        DefineMethod(realm, reflect, "preventExtensions", length: 1, (thisV, args) =>
        {
            var target = RequireObject(realm, args, 0, "preventExtensions");
            return JsValue.Boolean(target.PreventExtensions());
        });

        DefineMethod(realm, reflect, "ownKeys", length: 1, (thisV, args) =>
        {
            var target = RequireObject(realm, args, 0, "ownKeys");
            var arr = new JsArray(realm);
            // §10.4.6.11 — a Module Namespace Exotic Object's [[OwnPropertyKeys]]
            // is authoritative (sorted export names then @@toStringTag); use it
            // verbatim rather than re-deriving the ordinary integer-first order.
            if (target is JsModuleNamespace)
            {
                foreach (var k in target.OwnPropertyKeys)
                {
                    arr.Push(k.IsSymbol ? JsValue.Symbol(k.AsSymbol) : JsValue.String(k.AsString));
                }

                return JsValue.Object(arr);
            }
            // Spec §10.1.11.1 OrdinaryOwnPropertyKeys order (integer indices
            // ascending, then string keys in creation order, then symbols) is
            // now produced directly by [[OwnPropertyKeys]].
            foreach (var k in target.OwnPropertyKeys)
            {
                arr.Push(k.IsSymbol ? JsValue.Symbol(k.AsSymbol) : JsValue.String(k.AsString));
            }

            return JsValue.Object(arr);
        });

        DefineMethod(realm, reflect, "apply", length: 3, (thisV, args) =>
        {
            if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
            {
                throw new JsThrow(realm.NewTypeError("Reflect.apply: target must be callable"));
            }

            var target = args[0];
            var thisArg = args.Length > 1 ? args[1] : JsValue.Undefined;
            var argList = args.Length > 2 ? args[2] : JsValue.Undefined;
            var argsArr = CreateListFromArrayLike(realm, argList);
            return AbstractOperations.Call(realm.ActiveVm, target, thisArg, argsArr);
        });

        DefineMethod(realm, reflect, "construct", length: 2, (thisV, args) =>
        {
            if (args.Length == 0 || !AbstractOperations.IsConstructor(args[0]))
            {
                throw new JsThrow(realm.NewTypeError("Reflect.construct: target must be a constructor"));
            }

            var target = args[0];
            var argList = args.Length > 1 ? args[1] : JsValue.Undefined;
            var argsArr = CreateListFromArrayLike(realm, argList);
            JsObject? newTarget = null;
            if (args.Length > 2)
            {
                if (!AbstractOperations.IsConstructor(args[2]))
                {
                    throw new JsThrow(realm.NewTypeError("Reflect.construct: newTarget must be a constructor"));
                }

                newTarget = args[2].AsObject;
            }
            return AbstractOperations.Construct(realm.ActiveVm, target, argsArr, newTarget);
        });

        // Reflect[@@toStringTag] = "Reflect"
        reflect.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("Reflect"), writable: false, enumerable: false, configurable: true));

        realm.ReflectObject = reflect;
        realm.GlobalObject.DefineOwnProperty("Reflect",
            PropertyDescriptor.Data(JsValue.Object(reflect), writable: true, enumerable: false, configurable: true));
    }

    // ====================================================================

    private static void DefineMethod(JsRealm realm, JsObject target, string name, int length,
        Func<JsValue, JsValue[], JsValue> body)
    {
        var fn = new JsNativeFunction(realm, name, length, body, isConstructor: false);
        target.DefineOwnProperty(name, PropertyDescriptor.BuiltinMethod(JsValue.Object(fn)));
    }

    private static JsObject RequireObject(JsRealm realm, JsValue[] args, int index, string opName)
    {
        if (args.Length <= index)
        {
            throw new JsThrow(realm.NewTypeError($"Reflect.{opName} called on non-object"));
        }

        var v = args[index];
        if (!v.IsObject)
        {
            throw new JsThrow(realm.NewTypeError($"Reflect.{opName} called on non-object"));
        }

        return v.AsObject;
    }

    private static JsValue FromPropertyDescriptor(JsRealm realm, PropertyDescriptor d)
    {
        var obj = realm.NewOrdinaryObject();
        if (d.IsAccessor)
        {
            obj.Set("get", d.Getter is null ? JsValue.Undefined : JsValue.Object(d.Getter));
            obj.Set("set", d.Setter is null ? JsValue.Undefined : JsValue.Object(d.Setter));
        }
        else
        {
            obj.Set("value", d.Value);
            obj.Set("writable", JsValue.Boolean(d.Writable));
        }
        obj.Set("enumerable", JsValue.Boolean(d.Enumerable));
        obj.Set("configurable", JsValue.Boolean(d.Configurable));
        return JsValue.Object(obj);
    }

    private static PropertyDescriptor ToPropertyDescriptor(JsRealm realm, JsValue input)
    {
        if (!input.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("Property descriptor must be an object"));
        }

        var obj = input.AsObject;

        var hasEnumerable = AbstractOperations.HasProperty(obj, "enumerable");
        var enumerable = hasEnumerable
            && JsValue.ToBoolean(AbstractOperations.Get(realm.ActiveVm, obj, "enumerable"));

        var hasConfigurable = AbstractOperations.HasProperty(obj, "configurable");
        var configurable = hasConfigurable
            && JsValue.ToBoolean(AbstractOperations.Get(realm.ActiveVm, obj, "configurable"));

        var hasValue = AbstractOperations.HasProperty(obj, "value");
        var value = hasValue
            ? AbstractOperations.Get(realm.ActiveVm, obj, "value")
            : JsValue.Undefined;

        var hasWritable = AbstractOperations.HasProperty(obj, "writable");
        var writable = hasWritable
            && JsValue.ToBoolean(AbstractOperations.Get(realm.ActiveVm, obj, "writable"));

        JsObject? getter = null;
        var hasGet = AbstractOperations.HasProperty(obj, "get");
        if (hasGet)
        {
            var g = AbstractOperations.Get(realm.ActiveVm, obj, "get");
            if (!g.IsUndefined)
            {
                if (!AbstractOperations.IsCallable(g))
                {
                    throw new JsThrow(realm.NewTypeError("Getter must be a function"));
                }

                getter = g.AsObject;
            }
        }

        JsObject? setter = null;
        var hasSet = AbstractOperations.HasProperty(obj, "set");
        if (hasSet)
        {
            var s = AbstractOperations.Get(realm.ActiveVm, obj, "set");
            if (!s.IsUndefined)
            {
                if (!AbstractOperations.IsCallable(s))
                {
                    throw new JsThrow(realm.NewTypeError("Setter must be a function"));
                }

                setter = s.AsObject;
            }
        }

        if ((hasValue || hasWritable) && (hasGet || hasSet))
        {
            throw new JsThrow(realm.NewTypeError(
                "Invalid property descriptor. Cannot both specify accessors and a value or writable attribute"));
        }

        if (hasGet || hasSet)
        {
            return PropertyDescriptor.Accessor(getter, setter, enumerable, configurable);
        }

        return PropertyDescriptor.Data(value, writable, enumerable, configurable);
    }

    private static JsValue[] CreateListFromArrayLike(JsRealm realm, JsValue v)
    {
        if (!v.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("CreateListFromArrayLike called on non-object"));
        }

        var obj = v.AsObject;
        var lenV = AbstractOperations.Get(realm.ActiveVm, obj, "length");
        var len = (int)JsValue.ToNumber(lenV);
        if (len < 0)
        {
            len = 0;
        }

        var result = new JsValue[len];
        for (var i = 0; i < len; i++)
        {
            result[i] = AbstractOperations.Get(realm.ActiveVm, obj,
                i.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        return result;
    }
}
