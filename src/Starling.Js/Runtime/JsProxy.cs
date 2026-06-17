namespace Starling.Js.Runtime;

/// <summary>
/// §10.5 Proxy Exotic Object. Wraps a target object and a handler whose
/// configured traps intercept the corresponding internal-method calls. When
/// a trap is absent, the operation forwards to the target.
/// </summary>
/// <remarks>
/// <para>Revoked proxies (via <c>Proxy.revocable</c>'s revoke fn) null out
/// <see cref="Target"/> and <see cref="Handler"/>; every operation then throws
/// <c>TypeError</c>.</para>
/// <para>Per §10.5 every trap result must satisfy invariants vs. the target —
/// e.g. a non-configurable own property cannot be reported as absent. The
/// invariants are enforced inline so user trap bugs surface as TypeError
/// rather than silently corrupted state.</para>
/// </remarks>
public sealed class JsProxy : JsObject
{
    private readonly JsRealm _realm;
    internal JsObject? Target { get; private set; }
    internal JsObject? Handler { get; private set; }

    /// <summary>True if the proxy has been revoked. Every operation then
    /// throws TypeError.</summary>
    public bool IsRevoked => Target is null;

    /// <summary>True if the target was callable when the proxy was created.
    /// Stashed at construction because the target's exact type matters for the
    /// proxy's [[Call]] / [[Construct]] surface (per §10.5.13/§10.5.14).</summary>
    public bool TargetIsCallable { get; }

    /// <summary>True if the target was a constructor when the proxy was created.</summary>
    public bool TargetIsConstructor { get; }

    public JsProxy(JsRealm realm, JsObject target, JsObject handler)
        // Proxy inherits from realm.ProxyPrototype to match how Array/Promise
        // are wired; spec §10.5 leaves [[Prototype]] resolution to the trap
        // chain anyway via GetPrototypeOf.
        : base(realm?.ProxyPrototype)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(handler);
        DisableInlineCache();
        _realm = realm;
        Target = target;
        Handler = handler;
        TargetIsCallable = AbstractOperations.IsCallable(JsValue.Object(target));
        TargetIsConstructor = AbstractOperations.IsConstructor(JsValue.Object(target));
    }

    /// <summary>Revoke the proxy. After revocation every internal method
    /// throws TypeError per §10.5.</summary>
    public void Revoke()
    {
        Target = null;
        Handler = null;
    }

    private (JsObject Target, JsObject Handler) RequireLive(string op)
    {
        var t = Target;
        var h = Handler;
        if (t is null || h is null)
        {
            throw new JsThrow(_realm.NewTypeError($"Cannot perform '{op}' on a proxy that has been revoked"));
        }

        return (t, h);
    }

    private JsValue GetTrap(JsObject handler, string name)
    {
        var vm = _realm.ActiveVm;
        var trap = AbstractOperations.Get(vm, handler, name);
        if (trap.IsNullish)
        {
            return JsValue.Undefined;
        }

        if (!AbstractOperations.IsCallable(trap))
        {
            throw new JsThrow(_realm.NewTypeError($"Proxy handler.{name} must be callable"));
        }

        return trap;
    }

    // ==========================================================
    //                       §10.5.1 GetPrototypeOf
    // ==========================================================
    public override JsObject? GetPrototypeOf()
    {
        var (target, handler) = RequireLive("getPrototypeOf");
        var trap = GetTrap(handler, "getPrototypeOf");
        if (trap.IsUndefined)
        {
            return target.GetPrototypeOf();
        }

        var handlerProto = AbstractOperations.Call(_realm.ActiveVm, trap,
            JsValue.Object(handler), new[] { JsValue.Object(target) });
        if (!handlerProto.IsObject && !handlerProto.IsNull)
        {
            throw new JsThrow(_realm.NewTypeError("'getPrototypeOf' trap must return an Object or null"));
        }
        // Invariant: if target is non-extensible, returned proto must equal target's actual proto.
        if (!target.Extensible)
        {
            var targetProto = target.GetPrototypeOf();
            var handlerProtoObj = handlerProto.IsNull ? null : handlerProto.AsObject;
            if (!ReferenceEquals(targetProto, handlerProtoObj))
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'getPrototypeOf' trap returned different value for a non-extensible target"));
            }
        }
        return handlerProto.IsNull ? null : handlerProto.AsObject;
    }

    // ==========================================================
    //                       §10.5.2 SetPrototypeOf
    // ==========================================================
    public override bool SetPrototypeOf(JsObject? proto)
    {
        var (target, handler) = RequireLive("setPrototypeOf");
        var trap = GetTrap(handler, "setPrototypeOf");
        if (trap.IsUndefined)
        {
            return target.SetPrototypeOf(proto);
        }

        var protoArg = proto is null ? JsValue.Null : JsValue.Object(proto);
        var result = AbstractOperations.Call(_realm.ActiveVm, trap,
            JsValue.Object(handler), new[] { JsValue.Object(target), protoArg });
        var bool_ = JsValue.ToBoolean(result);
        if (!bool_)
        {
            return false;
        }
        // Invariant: non-extensible target must keep its prototype.
        if (!target.Extensible)
        {
            var targetProto = target.GetPrototypeOf();
            if (!ReferenceEquals(targetProto, proto))
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'setPrototypeOf' trap returned true on non-extensible target with different prototype"));
            }
        }
        return true;
    }

    // ==========================================================
    //                       §10.5.3 IsExtensible
    // ==========================================================
    public override bool Extensible
    {
        get
        {
            var (target, handler) = RequireLive("isExtensible");
            var trap = GetTrap(handler, "isExtensible");
            if (trap.IsUndefined)
            {
                return target.Extensible;
            }

            var result = AbstractOperations.Call(_realm.ActiveVm, trap,
                JsValue.Object(handler), new[] { JsValue.Object(target) });
            var b = JsValue.ToBoolean(result);
            // Invariant: result must match target's actual extensibility.
            if (b != target.Extensible)
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'isExtensible' trap result must match target extensibility"));
            }

            return b;
        }
    }

    // ==========================================================
    //                       §10.5.4 PreventExtensions
    // ==========================================================
    public override bool PreventExtensions()
    {
        var (target, handler) = RequireLive("preventExtensions");
        var trap = GetTrap(handler, "preventExtensions");
        if (trap.IsUndefined)
        {
            return target.PreventExtensions();
        }

        var result = AbstractOperations.Call(_realm.ActiveVm, trap,
            JsValue.Object(handler), new[] { JsValue.Object(target) });
        var b = JsValue.ToBoolean(result);
        if (!b)
        {
            return false;
        }
        // Invariant: if trap returns true, target must actually be non-extensible.
        if (target.Extensible)
        {
            throw new JsThrow(_realm.NewTypeError(
                "'preventExtensions' trap returned true but target is still extensible"));
        }

        return true;
    }

    // ==========================================================
    //                       §10.5.5 GetOwnProperty
    // ==========================================================
    public override PropertyDescriptor? GetOwnPropertyDescriptor(string name)
        => GetOwnPropertyDescriptorImpl(JsPropertyKey.String(name));

    public override PropertyDescriptor? GetOwnPropertyDescriptor(JsSymbol symbol)
        => GetOwnPropertyDescriptorImpl(JsPropertyKey.Symbol(symbol));

    private PropertyDescriptor? GetOwnPropertyDescriptorImpl(JsPropertyKey key)
    {
        var (target, handler) = RequireLive("getOwnPropertyDescriptor");
        var trap = GetTrap(handler, "getOwnPropertyDescriptor");
        if (trap.IsUndefined)
        {
            return target.GetOwnPropertyDescriptor(key);
        }

        var keyArg = key.IsSymbol ? JsValue.Symbol(key.AsSymbol) : JsValue.String(key.AsString);
        var trapResult = AbstractOperations.Call(_realm.ActiveVm, trap,
            JsValue.Object(handler), new[] { JsValue.Object(target), keyArg });
        if (!trapResult.IsObject && !trapResult.IsUndefined)
        {
            throw new JsThrow(_realm.NewTypeError(
                "'getOwnPropertyDescriptor' trap must return Object or undefined"));
        }

        var targetDesc = target.GetOwnPropertyDescriptor(key);
        if (trapResult.IsUndefined)
        {
            if (targetDesc is null)
            {
                return null;
            }
            // Invariant: cannot report a non-configurable target prop as absent.
            if (!targetDesc.Value.Configurable)
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'getOwnPropertyDescriptor' trap reported non-configurable property as absent"));
            }

            if (!target.Extensible)
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'getOwnPropertyDescriptor' trap reported existing property as absent on non-extensible target"));
            }

            return null;
        }
        // Build a host descriptor from the trap-returned JS object.
        var desc = ToPropertyDescriptor(trapResult);
        var extensibleTarget = target.Extensible;
        var present = DescriptorFields.Complete(desc);
        if (!IsCompatiblePropertyDescriptor(extensibleTarget, desc, present, targetDesc))
        {
            throw new JsThrow(_realm.NewTypeError(
                "'getOwnPropertyDescriptor' trap returned an incompatible descriptor"));
        }

        if (!desc.Configurable)
        {
            if (targetDesc is null)
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'getOwnPropertyDescriptor' trap reported a non-configurable descriptor for a missing target property"));
            }

            if (targetDesc.Value.Configurable)
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'getOwnPropertyDescriptor' trap reported a configurable target property as non-configurable"));
            }
        }
        return desc;
    }

    // ==========================================================
    //                       §10.5.6 DefineOwnProperty
    // ==========================================================
    public override bool DefineOwnProperty(string name, PropertyDescriptor desc)
        => DefineOwnPropertyImpl(JsPropertyKey.String(name), desc, DescriptorFields.Complete(desc));

    public override bool DefineOwnProperty(JsSymbol symbol, PropertyDescriptor desc)
        => DefineOwnPropertyImpl(JsPropertyKey.Symbol(symbol), desc, DescriptorFields.Complete(desc));

    /// <summary>Route the user-facing partial define through the proxy's
    /// <c>defineProperty</c> trap (the base partial path would bypass the trap
    /// by writing straight into the proxy object's own property bag). The trap
    /// receives the resolved descriptor; the partial-presence info is folded
    /// in by the caller's <see cref="JsMappedArguments.DefineFromUser"/>.</summary>
    internal override bool DefineOwnPropertyPartial(JsPropertyKey key, PropertyDescriptor desc, DescriptorFields present)
        => DefineOwnPropertyImpl(key, desc, present);

    private bool DefineOwnPropertyImpl(JsPropertyKey key, PropertyDescriptor desc, DescriptorFields present)
    {
        var (target, handler) = RequireLive("defineProperty");
        var trap = GetTrap(handler, "defineProperty");
        if (trap.IsUndefined)
        {
            return target.DefineOwnPropertyPartial(key, desc, present);
        }

        var keyArg = key.IsSymbol ? JsValue.Symbol(key.AsSymbol) : JsValue.String(key.AsString);
        var descArg = FromPropertyDescriptor(desc, present);
        var trapResult = AbstractOperations.Call(_realm.ActiveVm, trap,
            JsValue.Object(handler), new[] { JsValue.Object(target), keyArg, descArg });
        if (!JsValue.ToBoolean(trapResult))
        {
            return false;
        }
        // Invariants per §10.5.6.
        var targetDesc = target.GetOwnPropertyDescriptor(key);
        var extensibleTarget = target.Extensible;
        var settingConfigurableFalse = present.HasConfigurable && !desc.Configurable;
        if (targetDesc is null)
        {
            if (!extensibleTarget)
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'defineProperty' trap added a property to a non-extensible target"));
            }

            if (settingConfigurableFalse)
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'defineProperty' trap added a non-configurable property not present on the target"));
            }
        }
        else
        {
            if (!IsCompatiblePropertyDescriptor(extensibleTarget, desc, present, targetDesc.Value))
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'defineProperty' trap returned true for an incompatible property descriptor"));
            }

            if (settingConfigurableFalse && targetDesc.Value.Configurable)
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'defineProperty' trap reported a configurable target property as non-configurable"));
            }

            if (!targetDesc.Value.IsAccessor && !targetDesc.Value.Configurable && targetDesc.Value.Writable
                && !desc.IsAccessor && present.HasWritable && !desc.Writable)
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'defineProperty' trap made a non-configurable writable property non-writable"));
            }
        }
        return true;
    }

    // ==========================================================
    //                       §10.5.7 HasProperty
    // ==========================================================
    public override bool Has(string name) => HasImpl(JsPropertyKey.String(name));
    public override bool Has(JsSymbol symbol) => HasImpl(JsPropertyKey.Symbol(symbol));

    private bool HasImpl(JsPropertyKey key)
    {
        var (target, handler) = RequireLive("has");
        var trap = GetTrap(handler, "has");
        if (trap.IsUndefined)
        {
            return key.IsSymbol ? target.Has(key.AsSymbol) : target.Has(key.AsString);
        }
        var keyArg = key.IsSymbol ? JsValue.Symbol(key.AsSymbol) : JsValue.String(key.AsString);
        var result = AbstractOperations.Call(_realm.ActiveVm, trap,
            JsValue.Object(handler), new[] { JsValue.Object(target), keyArg });
        var b = JsValue.ToBoolean(result);
        if (!b)
        {
            // Invariant: cannot deny existence of non-configurable own prop.
            var targetDesc = target.GetOwnPropertyDescriptor(key);
            if (targetDesc is { } td)
            {
                if (!td.Configurable)
                {
                    throw new JsThrow(_realm.NewTypeError(
                        "'has' trap returned false on non-configurable own property"));
                }

                if (!target.Extensible)
                {
                    throw new JsThrow(_realm.NewTypeError(
                        "'has' trap returned false on own property of non-extensible target"));
                }
            }
        }
        return b;
    }

    // ==========================================================
    //                       §10.5.8 Get
    // ==========================================================
    public override JsValue Get(string name) => GetImpl(JsPropertyKey.String(name), JsValue.Object(this));
    public override JsValue Get(JsSymbol symbol) => GetImpl(JsPropertyKey.Symbol(symbol), JsValue.Object(this));

    internal JsValue GetWithReceiver(JsPropertyKey key, JsValue receiver) => GetImpl(key, receiver);

    private JsValue GetImpl(JsPropertyKey key, JsValue receiver)
    {
        var (target, handler) = RequireLive("get");
        var trap = GetTrap(handler, "get");
        if (trap.IsUndefined)
        {
            return AbstractOperations.Get(_realm.ActiveVm, target, key, receiver);
        }
        var keyArg = key.IsSymbol ? JsValue.Symbol(key.AsSymbol) : JsValue.String(key.AsString);
        var trapResult = AbstractOperations.Call(_realm.ActiveVm, trap,
            JsValue.Object(handler), new[] { JsValue.Object(target), keyArg, receiver });
        // Invariants per §10.5.8: non-configurable non-writable data must match.
        var targetDesc = target.GetOwnPropertyDescriptor(key);
        if (targetDesc is { } td && !td.Configurable)
        {
            if (!td.IsAccessor && !td.Writable
                && !AbstractOperations.SameValue(trapResult, td.Value))
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'get' trap returned a different value for non-writable non-configurable property"));
            }

            if (td.IsAccessor && td.Getter is null && !trapResult.IsUndefined)
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'get' trap returned non-undefined for accessor with undefined getter"));
            }
        }
        return trapResult;
    }

    // ==========================================================
    //                       §10.5.9 Set
    // ==========================================================
    public override void Set(string name, JsValue value)
        => SetImpl(JsPropertyKey.String(name), value, JsValue.Object(this));
    public override void Set(JsSymbol symbol, JsValue value)
        => SetImpl(JsPropertyKey.Symbol(symbol), value, JsValue.Object(this));

    internal bool SetWithReceiver(JsPropertyKey key, JsValue value, JsValue receiver)
        => SetImpl(key, value, receiver);

    private bool SetImpl(JsPropertyKey key, JsValue value, JsValue receiver)
    {
        var (target, handler) = RequireLive("set");
        var trap = GetTrap(handler, "set");
        if (trap.IsUndefined)
        {
            return AbstractOperations.Set(_realm.ActiveVm, target, key, value, receiver);
        }
        var keyArg = key.IsSymbol ? JsValue.Symbol(key.AsSymbol) : JsValue.String(key.AsString);
        var trapResult = AbstractOperations.Call(_realm.ActiveVm, trap,
            JsValue.Object(handler), new[] { JsValue.Object(target), keyArg, value, receiver });
        if (!JsValue.ToBoolean(trapResult))
        {
            return false;
        }
        // Invariants per §10.5.9.
        var targetDesc = target.GetOwnPropertyDescriptor(key);
        if (targetDesc is { } td && !td.Configurable)
        {
            if (!td.IsAccessor && !td.Writable
                && !AbstractOperations.SameValue(value, td.Value))
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'set' trap returned true for non-writable non-configurable property with different value"));
            }

            if (td.IsAccessor && td.Setter is null)
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'set' trap returned true for accessor with no setter"));
            }
        }
        return true;
    }

    // ==========================================================
    //                       §10.5.10 Delete
    // ==========================================================
    public override bool Delete(string name) => DeleteImpl(JsPropertyKey.String(name));
    public override bool Delete(JsSymbol symbol) => DeleteImpl(JsPropertyKey.Symbol(symbol));

    private bool DeleteImpl(JsPropertyKey key)
    {
        var (target, handler) = RequireLive("deleteProperty");
        var trap = GetTrap(handler, "deleteProperty");
        if (trap.IsUndefined)
        {
            return key.IsSymbol ? target.Delete(key.AsSymbol) : target.Delete(key.AsString);
        }
        var keyArg = key.IsSymbol ? JsValue.Symbol(key.AsSymbol) : JsValue.String(key.AsString);
        var result = AbstractOperations.Call(_realm.ActiveVm, trap,
            JsValue.Object(handler), new[] { JsValue.Object(target), keyArg });
        if (!JsValue.ToBoolean(result))
        {
            return false;
        }
        // Invariant: cannot report deletion of non-configurable own property.
        var targetDesc = target.GetOwnPropertyDescriptor(key);
        if (targetDesc is { } td)
        {
            if (!td.Configurable)
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'deleteProperty' trap deleted a non-configurable own property"));
            }

            if (!target.Extensible)
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'deleteProperty' trap deleted an own property on a non-extensible target"));
            }
        }
        return true;
    }

    // ==========================================================
    //                       §10.5.11 OwnPropertyKeys
    // ==========================================================
    public override IEnumerable<JsPropertyKey> OwnPropertyKeys => GetOwnPropertyKeys();

    public override IEnumerable<string> Keys
    {
        get
        {
            foreach (var k in GetOwnPropertyKeys())
            {
                if (k.IsString)
                {
                    yield return k.AsString;
                }
            }
        }
    }

    public override IEnumerable<string> EnumerableKeys()
    {
        // Match the spec: filter by GetOwnProperty + enumerable.
        foreach (var k in GetOwnPropertyKeys())
        {
            if (!k.IsString)
            {
                continue;
            }

            var d = GetOwnPropertyDescriptor(k.AsString);
            if (d is { } dv && dv.Enumerable)
            {
                yield return k.AsString;
            }
        }
    }

    private List<JsPropertyKey> GetOwnPropertyKeys()
    {
        var (target, handler) = RequireLive("ownKeys");
        var trap = GetTrap(handler, "ownKeys");
        if (trap.IsUndefined)
        {
            return new List<JsPropertyKey>(target.OwnPropertyKeys);
        }

        var result = AbstractOperations.Call(_realm.ActiveVm, trap,
            JsValue.Object(handler), new[] { JsValue.Object(target) });
        if (!result.IsObject)
        {
            throw new JsThrow(_realm.NewTypeError("'ownKeys' trap must return an Object"));
        }

        var arr = result.AsObject;
        // Build a list of property keys from the returned array-like.
        var lengthV = AbstractOperations.Get(_realm.ActiveVm, arr, "length");
        var length = (int)JsValue.ToNumber(lengthV);
        var keys = new List<JsPropertyKey>();
        var seenStrings = new HashSet<string>(StringComparer.Ordinal);
        var seenSymbols = new HashSet<JsSymbol>();
        for (var i = 0; i < length; i++)
        {
            var elem = AbstractOperations.Get(_realm.ActiveVm, arr,
                i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (elem.IsSymbol)
            {
                if (!seenSymbols.Add(elem.AsSymbol))
                {
                    throw new JsThrow(_realm.NewTypeError("'ownKeys' trap returned duplicate keys"));
                }

                keys.Add(JsPropertyKey.Symbol(elem.AsSymbol));
            }
            else if (elem.Kind == JsValueKind.String)
            {
                if (!seenStrings.Add(elem.AsString))
                {
                    throw new JsThrow(_realm.NewTypeError("'ownKeys' trap returned duplicate keys"));
                }

                keys.Add(JsPropertyKey.String(elem.AsString));
            }
            else
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'ownKeys' trap returned a non-string, non-symbol key"));
            }
        }
        // §10.5.11 invariants.
        var extensible = target.Extensible;
        var targetKeys = new List<JsPropertyKey>(target.OwnPropertyKeys);
        var nonConfigurableTargetKeys = new List<JsPropertyKey>();
        foreach (var tk in targetKeys)
        {
            var d = target.GetOwnPropertyDescriptor(tk);
            if (d is { } dv && !dv.Configurable)
            {
                nonConfigurableTargetKeys.Add(tk);
            }
        }
        // Every non-configurable own key on target must appear in trap result.
        foreach (var k in nonConfigurableTargetKeys)
        {
            if (!ContainsKey(keys, k))
            {
                throw new JsThrow(_realm.NewTypeError(
                    "'ownKeys' trap result missing a non-configurable own property of the target"));
            }
        }
        if (!extensible)
        {
            // Every target own key must appear AND no extra keys allowed.
            foreach (var k in targetKeys)
            {
                if (!ContainsKey(keys, k))
                {
                    throw new JsThrow(_realm.NewTypeError(
                        "'ownKeys' trap result missing an own property of a non-extensible target"));
                }
            }
            foreach (var k in keys)
            {
                if (!ContainsKey(targetKeys, k))
                {
                    throw new JsThrow(_realm.NewTypeError(
                        "'ownKeys' trap result added a key absent from a non-extensible target"));
                }
            }
        }
        return keys;
    }

    private static bool ContainsKey(List<JsPropertyKey> list, JsPropertyKey key)
    {
        foreach (var k in list)
        {
            if (k.Equals(key))
            {
                return true;
            }
        }

        return false;
    }

    // ==========================================================
    //         Descriptor marshalling helpers (proxy-local)
    // ==========================================================

    /// <summary>FromPropertyDescriptor (§6.2.5.5) — builds a JS object the
    /// trap can observe.</summary>
    private JsValue FromPropertyDescriptor(PropertyDescriptor d, DescriptorFields present)
    {
        var obj = _realm.NewOrdinaryObject();
        if (d.IsAccessor)
        {
            if (present.HasGet)
            {
                obj.Set("get", d.Getter is null ? JsValue.Undefined : JsValue.Object(d.Getter));
            }

            if (present.HasSet)
            {
                obj.Set("set", d.Setter is null ? JsValue.Undefined : JsValue.Object(d.Setter));
            }
        }
        else
        {
            if (present.HasValue)
            {
                obj.Set("value", d.Value);
            }

            if (present.HasWritable)
            {
                obj.Set("writable", JsValue.Boolean(d.Writable));
            }
        }
        if (present.HasEnumerable)
        {
            obj.Set("enumerable", JsValue.Boolean(d.Enumerable));
        }

        if (present.HasConfigurable)
        {
            obj.Set("configurable", JsValue.Boolean(d.Configurable));
        }

        return JsValue.Object(obj);
    }

    private static bool IsCompatiblePropertyDescriptor(bool extensible, PropertyDescriptor desc,
        DescriptorFields present, PropertyDescriptor? current)
    {
        if (current is null)
        {
            return extensible;
        }

        var cur = current.Value;
        if (cur.Configurable)
        {
            return true;
        }

        if (present.HasConfigurable && desc.Configurable)
        {
            return false;
        }

        if (present.HasEnumerable && desc.Enumerable != cur.Enumerable)
        {
            return false;
        }

        var descHasAccessorFields = present.HasGet || present.HasSet;
        var descHasDataFields = present.HasValue || present.HasWritable;
        if (!descHasAccessorFields && !descHasDataFields)
        {
            return true;
        }

        if (cur.IsAccessor)
        {
            if (descHasDataFields)
            {
                return false;
            }

            if (present.HasGet && !ReferenceEquals(desc.Getter, cur.Getter))
            {
                return false;
            }

            if (present.HasSet && !ReferenceEquals(desc.Setter, cur.Setter))
            {
                return false;
            }

            return true;
        }

        if (descHasAccessorFields)
        {
            return false;
        }

        if (cur.Writable)
        {
            return true;
        }

        if (present.HasWritable && desc.Writable)
        {
            return false;
        }

        if (present.HasValue && !AbstractOperations.SameValue(desc.Value, cur.Value))
        {
            return false;
        }

        return true;
    }

    /// <summary>ToPropertyDescriptor (§6.2.5.6).</summary>
    private PropertyDescriptor ToPropertyDescriptor(JsValue input)
    {
        if (!input.IsObject)
        {
            throw new JsThrow(_realm.NewTypeError("Property descriptor must be an object"));
        }

        var obj = input.AsObject;

        var hasEnumerable = AbstractOperations.HasProperty(obj, "enumerable");
        var enumerable = hasEnumerable
            && JsValue.ToBoolean(AbstractOperations.Get(_realm.ActiveVm, obj, "enumerable"));

        var hasConfigurable = AbstractOperations.HasProperty(obj, "configurable");
        var configurable = hasConfigurable
            && JsValue.ToBoolean(AbstractOperations.Get(_realm.ActiveVm, obj, "configurable"));

        var hasValue = AbstractOperations.HasProperty(obj, "value");
        var value = hasValue
            ? AbstractOperations.Get(_realm.ActiveVm, obj, "value")
            : JsValue.Undefined;

        var hasWritable = AbstractOperations.HasProperty(obj, "writable");
        var writable = hasWritable
            && JsValue.ToBoolean(AbstractOperations.Get(_realm.ActiveVm, obj, "writable"));

        JsObject? getter = null;
        var hasGet = AbstractOperations.HasProperty(obj, "get");
        if (hasGet)
        {
            var g = AbstractOperations.Get(_realm.ActiveVm, obj, "get");
            if (!g.IsUndefined)
            {
                if (!AbstractOperations.IsCallable(g))
                {
                    throw new JsThrow(_realm.NewTypeError("Getter must be a function"));
                }

                getter = g.AsObject;
            }
        }

        JsObject? setter = null;
        var hasSet = AbstractOperations.HasProperty(obj, "set");
        if (hasSet)
        {
            var s = AbstractOperations.Get(_realm.ActiveVm, obj, "set");
            if (!s.IsUndefined)
            {
                if (!AbstractOperations.IsCallable(s))
                {
                    throw new JsThrow(_realm.NewTypeError("Setter must be a function"));
                }

                setter = s.AsObject;
            }
        }

        if ((hasValue || hasWritable) && (hasGet || hasSet))
        {
            throw new JsThrow(_realm.NewTypeError(
                "Invalid property descriptor. Cannot both specify accessors and a value or writable attribute"));
        }

        if (hasGet || hasSet)
        {
            return PropertyDescriptor.Accessor(getter, setter, enumerable, configurable);
        }

        return PropertyDescriptor.Data(value, writable, enumerable, configurable);
    }

    // ==========================================================
    //                §10.5.13 [[Call]]
    // ==========================================================
    internal JsValue ProxyCall(JsValue thisArg, JsValue[] args)
    {
        var (target, handler) = RequireLive("apply");
        if (!TargetIsCallable)
        {
            throw new JsThrow(_realm.NewTypeError("Proxy target is not callable"));
        }

        var trap = GetTrap(handler, "apply");
        if (trap.IsUndefined)
        {
            return AbstractOperations.Call(_realm.ActiveVm, JsValue.Object(target), thisArg, args);
        }
        var argsArr = new JsArray(_realm);
        foreach (var a in args)
        {
            argsArr.Push(a);
        }

        return AbstractOperations.Call(_realm.ActiveVm, trap,
            JsValue.Object(handler),
            new[] { JsValue.Object(target), thisArg, JsValue.Object(argsArr) });
    }

    // ==========================================================
    //                §10.5.14 [[Construct]]
    // ==========================================================
    internal JsValue ProxyConstruct(JsValue[] args, JsObject newTarget)
    {
        var (target, handler) = RequireLive("construct");
        if (!TargetIsConstructor)
        {
            throw new JsThrow(_realm.NewTypeError("Proxy target is not a constructor"));
        }

        var trap = GetTrap(handler, "construct");
        if (trap.IsUndefined)
        {
            return AbstractOperations.Construct(_realm.ActiveVm, JsValue.Object(target), args, newTarget);
        }
        var argsArr = new JsArray(_realm);
        foreach (var a in args)
        {
            argsArr.Push(a);
        }

        var result = AbstractOperations.Call(_realm.ActiveVm, trap,
            JsValue.Object(handler),
            new[] { JsValue.Object(target), JsValue.Object(argsArr), JsValue.Object(newTarget) });
        // Invariant: result must be an object.
        if (!result.IsObject)
        {
            throw new JsThrow(_realm.NewTypeError("'construct' trap must return an object"));
        }

        return result;
    }
}
