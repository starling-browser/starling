using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// §20.1 The Object intrinsic. Populates <see cref="JsRealm.ObjectConstructor"/>
/// and <see cref="JsRealm.ObjectPrototype"/>, then registers <c>Object</c> on
/// the realm's global object.
/// </summary>
/// <remarks>
/// <para>
/// Array-returning methods (<c>keys</c>, <c>values</c>, <c>entries</c>,
/// <c>getOwnPropertyNames</c>, and <c>getOwnPropertySymbols</c>) return real
/// <see cref="JsArray"/> instances.
/// </para>
/// </remarks>
public static class ObjectCtor
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var objectProto = realm.ObjectPrototype;

        // ----------------------------------------------------------- Constructor
        // §20.1.1 The Object Constructor — called as Object(v) or new Object(v).
        JsNativeFunction? ctor = null;
        ctor = new JsNativeFunction("Object", (newTarget, args) =>
        {
            // §20.1.1.1 step 1: when constructed with a new.target that is NOT
            // the Object constructor itself (i.e. a subclass via super()),
            // OrdinaryCreateFromConstructor an object prototyped from new.target.
            // `class S extends Object {}; new S()` then yields an S-prototyped
            // instance regardless of any argument.
            if (newTarget.IsObject && AbstractOperations.IsConstructor(newTarget)
                && !ReferenceEquals(newTarget.AsObject, ctor))
            {
                var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, objectProto);
                return JsValue.Object(realm.NewObjectWithProto(instProto));
            }
            var value = args.Length > 0 ? args[0] : JsValue.Undefined;
            // When `value` is undefined or null → fresh ordinary object.
            if (value.IsNullish)
            {
                return JsValue.Object(realm.NewOrdinaryObject());
            }
            // Otherwise per §7.1.18 ToObject (objects pass through unchanged).
            return JsValue.Object(AbstractOperations.ToObject(realm, value));
        }, isConstructor: true);
        // Object.[[Prototype]] = Function.prototype
        ctor.SetPrototypeOf(realm.FunctionPrototype);
        // Object.prototype is a non-writable, non-enumerable, non-configurable slot
        // on the constructor (§20.1.2.1).
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(objectProto), writable: false, enumerable: false, configurable: false));
        ctor.DefineOwnProperty("name",
            PropertyDescriptor.Data(JsValue.String("Object"), writable: false, enumerable: false, configurable: true));
        ctor.DefineOwnProperty("length",
            PropertyDescriptor.Data(JsValue.Number(1), writable: false, enumerable: false, configurable: true));
        // -------------------------------------------------------- Static methods
        DefineMethod(realm, ctor, "assign", Assign, length: 2);
        DefineMethod(realm, ctor, "create", (thisV, args) => Create(realm, args), length: 2);
        DefineMethod(realm, ctor, "defineProperty", (thisV, args) => DefineProperty(realm, args), length: 3);
        DefineMethod(realm, ctor, "defineProperties", (thisV, args) => DefineProperties(realm, args), length: 2);
        DefineMethod(realm, ctor, "getOwnPropertyDescriptor", (thisV, args) => GetOwnPropertyDescriptor(realm, args), length: 2);
        DefineMethod(realm, ctor, "getOwnPropertyDescriptors", (thisV, args) => GetOwnPropertyDescriptors(realm, args), length: 1);
        DefineMethod(realm, ctor, "getOwnPropertyNames", (thisV, args) => GetOwnPropertyNames(realm, args), length: 1);
        DefineMethod(realm, ctor, "getOwnPropertySymbols", (thisV, args) => GetOwnPropertySymbols(realm, args), length: 1);
        DefineMethod(realm, ctor, "getPrototypeOf", (thisV, args) => GetPrototypeOf(realm, args), length: 1);
        DefineMethod(realm, ctor, "setPrototypeOf", (thisV, args) => SetPrototypeOf(realm, args), length: 2);
        DefineMethod(realm, ctor, "keys", (thisV, args) => Keys(realm, args), length: 1);
        DefineMethod(realm, ctor, "values", (thisV, args) => Values(realm, args), length: 1);
        DefineMethod(realm, ctor, "entries", (thisV, args) => Entries(realm, args), length: 1);
        DefineMethod(realm, ctor, "freeze", (thisV, args) => Freeze(args), length: 1);
        DefineMethod(realm, ctor, "isFrozen", (thisV, args) => IsFrozen(args), length: 1);
        DefineMethod(realm, ctor, "seal", (thisV, args) => Seal(args), length: 1);
        DefineMethod(realm, ctor, "isSealed", (thisV, args) => IsSealed(args), length: 1);
        DefineMethod(realm, ctor, "preventExtensions", (thisV, args) => PreventExtensions(args), length: 1);
        DefineMethod(realm, ctor, "isExtensible", (thisV, args) => IsExtensible(args), length: 1);
        DefineMethod(realm, ctor, "is", (thisV, args) => Is(args), length: 2);
        DefineMethod(realm, ctor, "fromEntries", (thisV, args) => FromEntries(realm, args), length: 1);
        DefineMethod(realm, ctor, "hasOwn", (thisV, args) => HasOwn(args), length: 2);

        // -------------------------------------------------------- Prototype methods
        // Bulk-install the constructor back-reference + the six prototype methods
        // by adopting one precomputed shape. Creation order (and thus
        // getOwnPropertyNames order) is exactly: constructor, hasOwnProperty,
        // isPrototypeOf, propertyIsEnumerable, toString, valueOf, toLocaleString —
        // unchanged from the prior sequential install. All are string-keyed builtin
        // data properties (W=true/E=false/C=true), so the result is byte-identical.
        IntrinsicHelpers.BulkInstallBuiltins(realm, objectProto, new[]
        {
            new IntrinsicHelpers.BulkMember("constructor", 0, null, JsValue.Object(ctor)),
            new IntrinsicHelpers.BulkMember("hasOwnProperty", 1, (thisV, args) => ProtoHasOwnProperty(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("isPrototypeOf", 1, (thisV, args) => ProtoIsPrototypeOf(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("propertyIsEnumerable", 1, (thisV, args) => ProtoPropertyIsEnumerable(realm, thisV, args)),
            new IntrinsicHelpers.BulkMember("toString", 0, (thisV, args) => ProtoToString(realm, thisV)),
            new IntrinsicHelpers.BulkMember("valueOf", 0, (thisV, args) => ProtoValueOf(realm, thisV)),
            new IntrinsicHelpers.BulkMember("toLocaleString", 0, (thisV, args) => ProtoToLocaleString(realm, thisV)),
        });

        realm.ObjectConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("Object",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    // ====================================================================
    //                            Helpers
    // ====================================================================

    /// <summary>Install a builtin method descriptor (W=true, E=false, C=true)
    /// per §17 default attributes for built-in functions. B2-2: routed through
    /// the realm-aware <see cref="JsNativeFunction"/> ctor so each method
    /// inherits from <c>Function.prototype</c>.</summary>
    private static void DefineMethod(JsRealm realm, JsObject target, string name,
        Func<JsValue, JsValue[], JsValue> body, int length)
    {
        var fn = new JsNativeFunction(realm, name, length, body, isConstructor: false);
        target.DefineOwnProperty(name, PropertyDescriptor.BuiltinMethod(JsValue.Object(fn)));
    }

    /// <summary>RequireObjectCoercible-then-ToObject. Mirrors what most spec
    /// statics do at entry.</summary>
    private static JsObject RequireObject(JsRealm realm, JsValue v)
    {
        if (v.IsNullish)
        {
            throw new JsThrow(realm.NewTypeError("Cannot convert undefined or null to object"));
        }

        if (!v.IsObject)
        {
            return AbstractOperations.ToObject(realm, v);
        }

        return v.AsObject;
    }

    /// <summary>Build a freshly-allocated dense array (B2-4) populated with
    /// the given elements. Used by <c>Object.keys</c>, <c>values</c>,
    /// <c>entries</c>, <c>getOwnPropertyNames</c>, and
    /// <c>getOwnPropertySymbols</c> so the returned values are real arrays
    /// (<c>Array.isArray</c> ⇒ true).</summary>
    private static JsValue MakeArrayLike(JsRealm realm, IReadOnlyList<JsValue> items)
    {
        var arr = new JsArray(realm);
        for (var i = 0; i < items.Count; i++)
        {
            arr.Push(items[i]);
        }

        return JsValue.Object(arr);
    }

    /// <summary>§6.2.5.5 FromPropertyDescriptor — build a plain JS object from
    /// a host descriptor (data → {value, writable, enumerable, configurable},
    /// accessor → {get, set, enumerable, configurable}).</summary>
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

    /// <summary>§6.2.5.6 ToPropertyDescriptor — read a JS object's hint
    /// fields and produce a host <see cref="PropertyDescriptor"/>. Throws
    /// TypeError on data+accessor mix per spec.</summary>
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

    // ====================================================================
    //                          Static implementations
    // ====================================================================

    /// <summary>§20.1.2.1 Object.assign — copy own enumerable string-keyed
    /// properties from each source to target.</summary>
    private static JsValue Assign(JsValue thisV, JsValue[] args)
    {
        if (args.Length == 0)
        {
            throw new JsThrow(JsValue.String("Object.assign requires a target"));
        }

        var target = args[0];
        if (!target.IsObject)
        {
            throw new JsThrow(JsValue.String("Object.assign target must be an object"));
        }

        var targetObj = target.AsObject;

        for (var i = 1; i < args.Length; i++)
        {
            var src = args[i];
            if (src.IsNullish)
            {
                continue;
            }

            if (!src.IsObject)
            {
                continue;
            }

            var srcObj = src.AsObject;
            foreach (var key in srcObj.EnumerableKeys())
            {
                var v = srcObj.Get(key);
                AbstractOperations.Set(vm: null, targetObj, key, v);
            }
            foreach (var key in srcObj.EnumerableSymbolKeys())
            {
                var v = srcObj.Get(key);
                AbstractOperations.Set(vm: null, targetObj, JsPropertyKey.Symbol(key), v);
            }
        }
        return target;
    }

    /// <summary>§20.1.2.2 Object.create — create a fresh object with the
    /// supplied prototype and optionally apply a descriptor map.</summary>
    private static JsValue Create(JsRealm realm, JsValue[] args)
    {
        if (args.Length == 0)
        {
            throw new JsThrow(realm.NewTypeError("Object.create: prototype must be Object or null"));
        }

        var proto = args[0];
        JsObject? protoObj;
        if (proto.IsNull)
        {
            protoObj = null;
        }
        else if (proto.IsObject)
        {
            protoObj = proto.AsObject;
        }
        else
        {
            throw new JsThrow(realm.NewTypeError("Object.create: prototype must be Object or null"));
        }

        var obj = realm.NewObjectWithProto(protoObj);
        if (args.Length >= 2 && !args[1].IsUndefined)
        {
            ApplyDescriptors(realm, obj, args[1]);
        }
        return JsValue.Object(obj);
    }

    /// <summary>§20.1.2.3 Object.defineProperties.</summary>
    private static JsValue DefineProperties(JsRealm realm, JsValue[] args)
    {
        if (args.Length == 0 || !args[0].IsObject)
        {
            throw new JsThrow(realm.NewTypeError("Object.defineProperties called on non-object"));
        }

        var target = args[0].AsObject;
        var props = args.Length > 1 ? args[1] : JsValue.Undefined;
        if (props.IsUndefined)
        {
            throw new JsThrow(realm.NewTypeError("Object.defineProperties: descriptors must be an object"));
        }

        ApplyDescriptors(realm, target, props);
        return args[0];
    }

    private static void ApplyDescriptors(JsRealm realm, JsObject target, JsValue props)
    {
        if (!props.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("Property descriptors must be an object"));
        }

        var p = props.AsObject;
        foreach (var key in p.EnumerableKeys())
        {
            var descVal = p.Get(key);
            var desc = ToPropertyDescriptor(realm, descVal);
            if (!Starling.Js.Runtime.JsMappedArguments.DefineFromUser(target, JsPropertyKey.String(key), desc, descVal.AsObject))
            {
                throw new JsThrow(realm.NewTypeError($"Cannot define property '{key}'"));
            }
        }
    }

    /// <summary>§20.1.2.4 Object.defineProperty.</summary>
    private static JsValue DefineProperty(JsRealm realm, JsValue[] args)
    {
        if (args.Length == 0 || !args[0].IsObject)
        {
            throw new JsThrow(realm.NewTypeError("Object.defineProperty called on non-object"));
        }

        if (args.Length < 2)
        {
            throw new JsThrow(realm.NewTypeError("Object.defineProperty requires a property key"));
        }

        if (args.Length < 3)
        {
            throw new JsThrow(realm.NewTypeError("Object.defineProperty requires a descriptor"));
        }

        var target = args[0].AsObject;
        var key = AbstractOperations.ToPropertyKey(args[1]);
        var desc = ToPropertyDescriptor(realm, args[2]);
        if (!Starling.Js.Runtime.JsMappedArguments.DefineFromUser(target, key, desc, args[2].AsObject))
        {
            throw new JsThrow(realm.NewTypeError($"Cannot redefine property '{key}'"));
        }

        return args[0];
    }

    /// <summary>§20.1.2.8 Object.getOwnPropertyDescriptor.</summary>
    private static JsValue GetOwnPropertyDescriptor(JsRealm realm, JsValue[] args)
    {
        var target = RequireObject(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var key = AbstractOperations.ToPropertyKey(args.Length > 1 ? args[1] : JsValue.Undefined);
        var d = target.GetOwnPropertyDescriptor(key);
        if (d is null)
        {
            return JsValue.Undefined;
        }

        return FromPropertyDescriptor(realm, d.Value);
    }

    /// <summary>§20.1.2.9 Object.getOwnPropertyDescriptors.</summary>
    private static JsValue GetOwnPropertyDescriptors(JsRealm realm, JsValue[] args)
    {
        var target = RequireObject(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var bag = realm.NewOrdinaryObject();
        foreach (var key in target.OwnPropertyKeys)
        {
            var d = target.GetOwnPropertyDescriptor(key);
            if (d is null)
            {
                continue;
            }

            bag.DefineOwnProperty(key,
                PropertyDescriptor.Data(FromPropertyDescriptor(realm, d.Value), writable: true, enumerable: true, configurable: true));
        }
        return JsValue.Object(bag);
    }

    /// <summary>§20.1.2.10 Object.getOwnPropertyNames.</summary>
    private static JsValue GetOwnPropertyNames(JsRealm realm, JsValue[] args)
    {
        var target = RequireObject(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var names = new List<JsValue>();
        foreach (var k in target.Keys)
        {
            names.Add(JsValue.String(k));
        }

        return MakeArrayLike(realm, names);
    }

    /// <summary>§20.1.2.11 Object.getOwnPropertySymbols.</summary>
    private static JsValue GetOwnPropertySymbols(JsRealm realm, JsValue[] args)
    {
        var target = RequireObject(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var symbols = new List<JsValue>();
        // §10.4.6 — a Module Namespace Exotic Object carries @@toStringTag as an
        // own symbol key surfaced through its [[OwnPropertyKeys]] (not the base
        // symbol-property bag, which it keeps empty), so iterate that.
        if (target is JsModuleNamespace)
        {
            foreach (var k in target.OwnPropertyKeys)
            {
                if (k.IsSymbol)
                {
                    symbols.Add(JsValue.Symbol(k.AsSymbol));
                }
            }

            return MakeArrayLike(realm, symbols);
        }
        foreach (var k in target.SymbolKeys)
        {
            symbols.Add(JsValue.Symbol(k));
        }

        return MakeArrayLike(realm, symbols);
    }

    /// <summary>§20.1.2.12 Object.getPrototypeOf. Goes through the virtual
    /// <see cref="JsObject.GetPrototypeOf"/> so proxies' getPrototypeOf trap fires.</summary>
    private static JsValue GetPrototypeOf(JsRealm realm, JsValue[] args)
    {
        var target = RequireObject(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var proto = target.GetPrototypeOf();
        return proto is null ? JsValue.Null : JsValue.Object(proto);
    }

    /// <summary>§20.1.2.22 Object.setPrototypeOf.</summary>
    private static JsValue SetPrototypeOf(JsRealm realm, JsValue[] args)
    {
        if (args.Length == 0 || args[0].IsNullish)
        {
            throw new JsThrow(realm.NewTypeError("Object.setPrototypeOf called on null or undefined"));
        }

        if (args.Length < 2)
        {
            throw new JsThrow(realm.NewTypeError("Object.setPrototypeOf requires a prototype argument"));
        }

        var proto = args[1];
        JsObject? protoObj;
        if (proto.IsNull)
        {
            protoObj = null;
        }
        else if (proto.IsObject)
        {
            protoObj = proto.AsObject;
        }
        else
        {
            throw new JsThrow(realm.NewTypeError("Object.setPrototypeOf: prototype must be Object or null"));
        }
        // If target is a primitive, ToObject would box it (and the box is
        // discarded). Spec: SetPrototypeOf on a non-object is a no-op return.
        if (!args[0].IsObject)
        {
            return args[0];
        }

        if (!args[0].AsObject.SetPrototypeOf(protoObj))
        {
            throw new JsThrow(realm.NewTypeError("Object.setPrototypeOf: cycle detected or non-extensible"));
        }

        return args[0];
    }

    /// <summary>§20.1.2.18 Object.keys.</summary>
    private static JsValue Keys(JsRealm realm, JsValue[] args)
    {
        var target = RequireObject(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var keys = new List<JsValue>();
        foreach (var k in target.EnumerableKeys())
        {
            keys.Add(JsValue.String(k));
        }

        return MakeArrayLike(realm, keys);
    }

    /// <summary>§20.1.2.23 Object.values.</summary>
    private static JsValue Values(JsRealm realm, JsValue[] args)
    {
        var target = RequireObject(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var values = new List<JsValue>();
        foreach (var k in target.EnumerableKeys())
        {
            values.Add(AbstractOperations.Get(vm: null, target, k));
        }

        return MakeArrayLike(realm, values);
    }

    /// <summary>§20.1.2.5 Object.entries.</summary>
    private static JsValue Entries(JsRealm realm, JsValue[] args)
    {
        var target = RequireObject(realm, args.Length > 0 ? args[0] : JsValue.Undefined);
        var entries = new List<JsValue>();
        foreach (var k in target.EnumerableKeys())
        {
            var v = AbstractOperations.Get(vm: null, target, k);
            entries.Add(MakeArrayLike(realm, new[] { JsValue.String(k), v }));
        }
        return MakeArrayLike(realm, entries);
    }

    /// <summary>§20.1.2.6 Object.freeze — set [[Writable]] and
    /// [[Configurable]] to false on every own property, then prevent
    /// extensions.</summary>
    private static JsValue Freeze(JsValue[] args)
    {
        var v = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (!v.IsObject)
        {
            return v;
        }

        var obj = v.AsObject;
        // Snapshot keys to avoid mutation-during-enumeration.
        var keys = new List<JsPropertyKey>(obj.OwnPropertyKeys);
        foreach (var key in keys)
        {
            var d = obj.GetOwnPropertyDescriptor(key);
            if (d is null)
            {
                continue;
            }

            var desc = d.Value;
            if (desc.IsAccessor)
            {
                obj.DefineOwnProperty(key, PropertyDescriptor.Accessor(desc.Getter, desc.Setter, desc.Enumerable, configurable: false));
            }
            else
            {
                obj.DefineOwnProperty(key, PropertyDescriptor.Data(desc.Value, writable: false, enumerable: desc.Enumerable, configurable: false));
            }
        }
        obj.PreventExtensions();
        return v;
    }

    /// <summary>§20.1.2.15 Object.isFrozen.</summary>
    private static JsValue IsFrozen(JsValue[] args)
    {
        var v = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (!v.IsObject)
        {
            return JsValue.True; // primitives are frozen by definition
        }

        var obj = v.AsObject;
        if (obj.Extensible)
        {
            return JsValue.False;
        }

        foreach (var key in obj.OwnPropertyKeys)
        {
            var d = obj.GetOwnPropertyDescriptor(key);
            if (d is null)
            {
                continue;
            }

            var desc = d.Value;
            if (desc.Configurable)
            {
                return JsValue.False;
            }

            if (!desc.IsAccessor && desc.Writable)
            {
                return JsValue.False;
            }
        }
        return JsValue.True;
    }

    /// <summary>§20.1.2.21 Object.seal — set [[Configurable]] to false; keep
    /// [[Writable]] alone; prevent extensions.</summary>
    private static JsValue Seal(JsValue[] args)
    {
        var v = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (!v.IsObject)
        {
            return v;
        }

        var obj = v.AsObject;
        var keys = new List<JsPropertyKey>(obj.OwnPropertyKeys);
        foreach (var key in keys)
        {
            var d = obj.GetOwnPropertyDescriptor(key);
            if (d is null)
            {
                continue;
            }

            var desc = d.Value;
            if (desc.IsAccessor)
            {
                obj.DefineOwnProperty(key, PropertyDescriptor.Accessor(desc.Getter, desc.Setter, desc.Enumerable, configurable: false));
            }
            else
            {
                obj.DefineOwnProperty(key, PropertyDescriptor.Data(desc.Value, writable: desc.Writable, enumerable: desc.Enumerable, configurable: false));
            }
        }
        obj.PreventExtensions();
        return v;
    }

    /// <summary>§20.1.2.16 Object.isSealed.</summary>
    private static JsValue IsSealed(JsValue[] args)
    {
        var v = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (!v.IsObject)
        {
            return JsValue.True;
        }

        var obj = v.AsObject;
        if (obj.Extensible)
        {
            return JsValue.False;
        }

        foreach (var key in obj.OwnPropertyKeys)
        {
            var d = obj.GetOwnPropertyDescriptor(key);
            if (d is null)
            {
                continue;
            }

            if (d.Value.Configurable)
            {
                return JsValue.False;
            }
        }
        return JsValue.True;
    }

    /// <summary>§20.1.2.19 Object.preventExtensions.</summary>
    private static JsValue PreventExtensions(JsValue[] args)
    {
        var v = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (!v.IsObject)
        {
            return v;
        }

        v.AsObject.PreventExtensions();
        return v;
    }

    /// <summary>§20.1.2.14 Object.isExtensible.</summary>
    private static JsValue IsExtensible(JsValue[] args)
    {
        var v = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (!v.IsObject)
        {
            return JsValue.False;
        }

        return JsValue.Boolean(v.AsObject.Extensible);
    }

    /// <summary>§20.1.2.13 Object.is.</summary>
    private static JsValue Is(JsValue[] args)
    {
        var a = args.Length > 0 ? args[0] : JsValue.Undefined;
        var b = args.Length > 1 ? args[1] : JsValue.Undefined;
        return JsValue.Boolean(AbstractOperations.SameValue(a, b));
    }

    /// <summary>§20.1.2.7 Object.fromEntries — consumes any iterable of
    /// <c>[key, value]</c> pairs via the iterator protocol (§7.4), so Maps,
    /// generators, and entries() iterators all work. Plain array-likes
    /// (length + indexed access) stay as a fallback for objects without
    /// <c>@@iterator</c>.</summary>
    private static JsValue FromEntries(JsRealm realm, JsValue[] args)
    {
        var srcV = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (!srcV.IsObject && !srcV.IsString)
        {
            throw new JsThrow(realm.NewTypeError("Object.fromEntries requires an iterable"));
        }

        var result = realm.NewOrdinaryObject();

        if (ArrayCtor.HasIteratorMethod(realm, srcV))
        {
            var record = AbstractOperations.GetIterator(realm, realm.ActiveVm, srcV);
            while (true)
            {
                var step = AbstractOperations.IteratorStep(realm, realm.ActiveVm, ref record);
                if (step is null)
                {
                    break;
                }

                var entryV = AbstractOperations.IteratorValue(realm.ActiveVm, step.Value);
                AddEntry(realm, result, entryV);
            }
            return JsValue.Object(result);
        }

        var src = srcV.AsObject;
        var lengthV = src.Get("length");
        if (!lengthV.IsNumber)
        {
            throw new JsThrow(realm.NewTypeError("Object.fromEntries: source is not iterable or array-like"));
        }

        var len = (int)lengthV.AsNumber;
        for (var i = 0; i < len; i++)
        {
            AddEntry(realm, result,
                src.Get(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }

        return JsValue.Object(result);
    }

    /// <summary>§20.1.2.7 steps 5.d–5.k (the adder closure): each entry must
    /// be an object; its <c>0</c>/<c>1</c> properties become the key/value of
    /// a new enumerable data property.</summary>
    private static void AddEntry(JsRealm realm, JsObject result, JsValue entryV)
    {
        if (!entryV.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("Object.fromEntries: entry must be an object"));
        }

        var entry = entryV.AsObject;
        var key = AbstractOperations.ToPropertyKey(entry.Get("0"));
        var val = entry.Get("1");
        result.DefineOwnProperty(key,
            PropertyDescriptor.Data(val, writable: true, enumerable: true, configurable: true));
    }

    /// <summary>§20.1.2.13 Object.hasOwn(obj, key).</summary>
    private static JsValue HasOwn(JsValue[] args)
    {
        if (args.Length == 0 || !args[0].IsObject)
        {
            return JsValue.False;
        }

        var key = AbstractOperations.ToPropertyKey(args.Length > 1 ? args[1] : JsValue.Undefined);
        return JsValue.Boolean(args[0].AsObject.HasOwn(key));
    }

    // ====================================================================
    //                       Prototype implementations
    // ====================================================================

    /// <summary>§20.1.3.2 Object.prototype.hasOwnProperty.</summary>
    private static JsValue ProtoHasOwnProperty(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var key = AbstractOperations.ToPropertyKey(args.Length > 0 ? args[0] : JsValue.Undefined);
        var obj = RequireObject(realm, thisV);
        return JsValue.Boolean(obj.HasOwn(key));
    }

    /// <summary>§20.1.3.3 Object.prototype.isPrototypeOf — walks <c>V</c>'s
    /// prototype chain looking for <c>this</c>.</summary>
    private static JsValue ProtoIsPrototypeOf(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        if (args.Length == 0 || !args[0].IsObject)
        {
            return JsValue.False;
        }

        var self = RequireObject(realm, thisV);
        for (var p = args[0].AsObject.Prototype; p is not null; p = p.Prototype)
        {
            if (ReferenceEquals(p, self))
            {
                return JsValue.True;
            }
        }
        return JsValue.False;
    }

    /// <summary>§20.1.3.4 Object.prototype.propertyIsEnumerable.</summary>
    private static JsValue ProtoPropertyIsEnumerable(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var key = AbstractOperations.ToPropertyKey(args.Length > 0 ? args[0] : JsValue.Undefined);
        var obj = RequireObject(realm, thisV);
        var d = obj.GetOwnPropertyDescriptor(key);
        return JsValue.Boolean(d is { } dv && dv.Enumerable);
    }

    /// <summary>§20.1.3.6 Object.prototype.toString. Spec-faithful: classifies
    /// the receiver's internal class to pick a default tag, then consults
    /// <c>@@toStringTag</c> on the (ToObject'd) receiver — if present and a
    /// string, it overrides the default tag.</summary>
    private static JsValue ProtoToString(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsUndefined)
        {
            return JsValue.String("[object Undefined]");
        }

        if (thisV.IsNull)
        {
            return JsValue.String("[object Null]");
        }

        var o = AbstractOperations.ToObject(realm, thisV);
        var defaultTag = DefaultToStringTag(realm, o);

        // Consult @@toStringTag on the receiver's prototype chain. Use the
        // AO-level Get so accessor descriptors (e.g. %TypedArray%.prototype's
        // toStringTag getter — §23.2.3.34) fire with the correct receiver.
        // Only string values override.
        var tag = defaultTag;
        var tagVal = AbstractOperations.Get(realm.ActiveVm, o, JsPropertyKey.Symbol(SymbolCtor.ToStringTag));
        if (tagVal.Kind == JsValueKind.String)
        {
            tag = tagVal.AsString;
        }

        return JsValue.String("[object " + tag + "]");
    }

    /// <summary>Pick the default built-in tag per §20.1.3.6 steps 4–14. We model
    /// the categories that have a corresponding host class, plus the Arguments
    /// exotic (step 5) via <see cref="JsObject.IsArgumentsExotic"/>.</summary>
    private static string DefaultToStringTag(JsRealm realm, JsObject o)
    {
        if (o is JsArray)
        {
            return "Array";
        }
        // §20.1.3.6 step 5 — [[ParameterMap]] ⇒ "Arguments". Must precede the
        // [[Call]] check (an arguments object isn't callable, but spec order).
        if (o.IsArgumentsExotic)
        {
            return "Arguments";
        }

        if (AbstractOperations.IsCallable(JsValue.Object(o)))
        {
            return "Function";
        }
        // Error: any object with ErrorPrototype somewhere in its chain.
        for (var p = o.Prototype; p is not null; p = p.Prototype)
        {
            if (ReferenceEquals(p, realm.ErrorPrototype))
            {
                return "Error";
            }
        }
        // Boxed primitives (String / Number / Boolean) — detect via prototype.
        for (var p = o.Prototype; p is not null; p = p.Prototype)
        {
            if (ReferenceEquals(p, realm.StringPrototype))
            {
                return "String";
            }

            if (ReferenceEquals(p, realm.NumberPrototype))
            {
                return "Number";
            }

            if (ReferenceEquals(p, realm.BooleanPrototype))
            {
                return "Boolean";
            }
        }
        if (o is JsDate)
        {
            return "Date";
        }

        if (o is JsRegExp)
        {
            return "RegExp";
        }

        return "Object";
    }

    /// <summary>§20.1.3.7 Object.prototype.valueOf — return <c>this</c> coerced
    /// to an object (primitives box).</summary>
    private static JsValue ProtoValueOf(JsRealm realm, JsValue thisV)
        => JsValue.Object(RequireObject(realm, thisV));

    /// <summary>§20.1.3.5 Object.prototype.toLocaleString — defaults to
    /// invoking <c>this.toString()</c>.</summary>
    private static JsValue ProtoToLocaleString(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject)
        {
            var ts = thisV.AsObject.Get("toString");
            if (ts.IsObject && ts.AsObject is JsNativeFunction nat)
            {
                return nat.Body(thisV, Array.Empty<JsValue>());
            }
        }
        return ProtoToString(realm, thisV);
    }
}
