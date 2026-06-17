using System.Runtime.CompilerServices;

namespace Starling.Js.Runtime;

/// <summary>
/// ES2024 abstract operations not pinned to a single value (those live on
/// <see cref="JsValue"/>). VM-aware variants (Get/Set/Call/Construct) take an
/// optional <see cref="JsVm"/> so accessor invocation and JS-callable dispatch
/// can recurse into the interpreter. Native callers can pass <c>null</c> and
/// stay on the data-only fast path.
/// </summary>
public static class AbstractOperations
{
    /// <summary>§7.1.1 ToPrimitive — checks ECMA-262 §20.4's
    /// <c>@@toPrimitive</c> well-known Symbol first, then falls back to
    /// <c>toString</c>/<c>valueOf</c> for objects via the host bridge when
    /// available.</summary>
    public static JsValue ToPrimitive(JsValue input, string hint = "default")
        => ToPrimitive(null, input, hint);

    public static JsValue ToPrimitive(JsVm? vm, JsValue input, string hint = "default")
    {
        if (input.Kind != JsValueKind.Object)
        {
            return input;
        }

        var obj = input.AsObject;
        var exotic = Get(vm, obj, JsPropertyKey.Symbol(Starling.Js.Intrinsics.SymbolCtor.ToPrimitive));
        if (!exotic.IsUndefined && !exotic.IsNull)
        {
            if (!IsCallable(exotic))
            {
                throw new JsThrow(vm is not null
                    ? vm.Realm.NewTypeError("@@toPrimitive must be callable")
                    : JsValue.String("@@toPrimitive must be callable"));
            }

            var r = Call(vm, exotic, input, new[] { JsValue.String(hint) });
            if (r.Kind != JsValueKind.Object)
            {
                return r;
            }

            throw new JsThrow(vm is not null
                ? vm.Realm.NewTypeError("@@toPrimitive must return a primitive value")
                : JsValue.String("@@toPrimitive must return a primitive value"));
        }
        // §13.4.10 OrdinaryToPrimitive: try toString/valueOf in hint order.
        // Use Get (chain-walking) + Call so BOTH native (e.g.
        // Error.prototype.toString) and user-defined JS methods are honored —
        // calling nat.Body directly would have skipped JsFunction overrides.
        var first = hint == "string" ? "toString" : "valueOf";
        var second = hint == "string" ? "valueOf" : "toString";
        var m1 = Get(vm, obj, first);
        if (IsCallable(m1))
        {
            var r = Call(vm, m1, input, Array.Empty<JsValue>());
            if (r.Kind != JsValueKind.Object)
            {
                return r;
            }
        }
        var m2 = Get(vm, obj, second);
        if (IsCallable(m2))
        {
            var r = Call(vm, m2, input, Array.Empty<JsValue>());
            if (r.Kind != JsValueKind.Object)
            {
                return r;
            }
        }
        // §13.4.10 step 5: neither method returned a primitive.
        throw new JsThrow(vm is not null
            ? vm.Realm.NewTypeError("Cannot convert object to primitive value")
            : JsValue.String("Cannot convert object to primitive value"));
    }

    /// <summary>§7.1.17 ToString — spec-faithful string coercion. For objects
    /// it runs §7.1.1 ToPrimitive with the "string" hint (which invokes the
    /// object's <c>Symbol.toPrimitive</c> / <c>toString</c> / <c>valueOf</c>,
    /// e.g. <c>Error.prototype.toString</c>) before stringifying. Primitives
    /// short-circuit to <see cref="JsValue.ToStringValue"/>. Symbols throw a
    /// TypeError per step 2. A <see cref="JsVm"/> is needed so user-defined JS
    /// <c>toString</c>/<c>valueOf</c> methods can be dispatched.</summary>
    public static string ToStringJs(JsVm? vm, JsValue value)
    {
        if (value.IsSymbol)
        {
            throw new JsThrow(vm is not null
                ? vm.Realm.NewTypeError("Cannot convert a Symbol value to a string")
                : JsValue.String("Cannot convert a Symbol value to a string"));
        }

        if (value.Kind != JsValueKind.Object)
        {
            return JsValue.ToStringValue(value);
        }

        var prim = ToPrimitive(vm, value, "string");
        // After ToPrimitive an object can only remain if a custom @@toPrimitive
        // returned one; that path is a TypeError per ToPrimitive itself, so
        // prim is guaranteed primitive here. Symbols from ToPrimitive still
        // reject per step 2.
        if (prim.IsSymbol)
        {
            throw new JsThrow(vm is not null
                ? vm.Realm.NewTypeError("Cannot convert a Symbol value to a string")
                : JsValue.String("Cannot convert a Symbol value to a string"));
        }

        return JsValue.ToStringValue(prim);
    }

    /// <summary>§7.1.18 ToObject — boxes primitives to their wrapper. Throws
    /// for undefined/null per spec. Realm needed so we know which intrinsic
    /// prototype to box against.</summary>
    public static JsObject ToObject(JsRealm realm, JsValue value)
    {
        ArgumentNullException.ThrowIfNull(realm);
        return value.Kind switch
        {
            JsValueKind.Undefined => throw new JsThrow(realm.NewTypeError("Cannot convert undefined to object")),
            JsValueKind.Null => throw new JsThrow(realm.NewTypeError("Cannot convert null to object")),
            JsValueKind.Object => value.AsObject,
            JsValueKind.Boolean => realm.BoxBoolean(value),
            JsValueKind.Number => realm.BoxNumber(value),
            JsValueKind.String => realm.BoxString(value),
            JsValueKind.BigInt => realm.BoxBigInt(value),
            JsValueKind.Symbol => realm.BoxSymbol(value),
            _ => throw new InvalidOperationException($"unhandled kind {value.Kind}"),
        };
    }

    /// <summary>§7.1.13 ToBigInt — coerce to BigInt. Delegates to
    /// <see cref="BigIntOps.ToBigInt"/> so the parsing/range logic lives next
    /// to the rest of the BigInt operator suite.</summary>
    public static System.Numerics.BigInteger ToBigInt(JsRealm realm, JsValue value)
        => BigIntOps.ToBigInt(realm, value);

    /// <summary>§7.1.22 ToNumeric — converts the input to a BigInt-or-Number
    /// primitive. Used by the arithmetic operator boundary so the spec-faithful
    /// "no implicit Number↔BigInt coercion" rule has a single seam.</summary>
    public static JsValue ToNumeric(JsRealm realm, JsValue value)
    {
        // Thread the realm's active VM so an object operand's user-defined JS
        // valueOf/toString can be dispatched (else OrdinaryToPrimitive's Call
        // throws "VM required"). Covers compound-assignment / ++ / -- / unary.
        var prim = ToPrimitive(realm.ActiveVm, value, "number");
        if (prim.IsBigInt)
        {
            return prim;
        }

        if (prim.IsSymbol)
        {
            throw new JsThrow(realm.NewTypeError("Cannot convert a Symbol value to a number"));
        }

        return JsValue.Number(JsValue.ToNumber(prim));
    }

    /// <summary>§7.1.19 ToPropertyKey — Symbols are already property keys;
    /// every other primitive is stringified.</summary>
    public static JsPropertyKey ToPropertyKey(JsValue value)
        => ToPropertyKey(null, value);

    /// <summary>§7.1.19 ToPropertyKey with a VM so the §7.1.1 ToPrimitive step
    /// can dispatch a user/exotic <c>Symbol.toPrimitive</c> on an object key
    /// (used by computed class/object keys — wp:M3-04f).</summary>
    public static JsPropertyKey ToPropertyKey(JsVm? vm, JsValue value)
    {
        if (value.IsObject)
        {
            value = ToPrimitive(vm, value, "string");
        }

        return value.IsSymbol ? JsPropertyKey.Symbol(value.AsSymbol)
            : JsPropertyKey.String(value.Kind == JsValueKind.String ? value.AsString : JsValue.ToStringValue(value));
    }

    /// <summary>§7.2.3 IsCallable — true if the value is an object with a
    /// callable internal method ([[Call]]). For us: a JsFunction, a
    /// JsNativeFunction, a bound function, or a proxy whose target is callable.</summary>
    public static bool IsCallable(JsValue value)
    {
        if (!value.IsObject)
        {
            return false;
        }

        var obj = value.AsObject;
        if (obj is JsProxy proxy)
        {
            return !proxy.IsRevoked && proxy.TargetIsCallable;
        }

        return obj is JsFunction or JsNativeFunction or JsBoundFunction;
    }

    /// <summary>§7.2.4 IsConstructor.</summary>
    public static bool IsConstructor(JsValue value)
    {
        if (!value.IsObject)
        {
            return false;
        }

        return value.AsObject switch
        {
            JsFunction => true,
            JsNativeFunction nat => nat.IsConstructor,
            JsBoundFunction bf => IsConstructor(JsValue.Object(bf.Target)),
            JsProxy proxy => !proxy.IsRevoked && proxy.TargetIsConstructor,
            _ => false,
        };
    }

    /// <summary>§10.1.8 OrdinaryGet — chain-walking property lookup that
    /// invokes accessor getters via <paramref name="vm"/> when present.
    /// Passing a null VM falls back to the data-only path.</summary>
    public static JsValue Get(JsVm? vm, JsObject obj, string key, JsValue receiver = default)
        => Get(vm, obj, JsPropertyKey.String(key), receiver);

    public static JsValue Get(JsVm? vm, JsObject obj, JsPropertyKey key, JsValue receiver = default)
    {
        if (receiver.IsUndefined)
        {
            receiver = JsValue.Object(obj);
        }

        return GetCore(vm, obj, key, receiver);
    }

    public static JsValue GetWithReceiver(JsVm? vm, JsObject obj, JsPropertyKey key, JsValue receiver)
        => GetCore(vm, obj, key, receiver);

    private static JsValue GetCore(JsVm? vm, JsObject obj, JsPropertyKey key, JsValue receiver)
    {
        // §10.5.8: Proxy exotic objects route property reads through the [[Get]]
        // internal method (which consults the `get` trap). Done at the AO entry
        // so every call site picks it up — the VM and intrinsics all call here
        // rather than virtual JsObject.Get(string), so dispatching via virtual
        // override is not enough.
        if (obj is JsProxy proxy)
        {
            return proxy.GetWithReceiver(key, receiver);
        }
        // §10.4.6.8 — a Module Namespace Exotic Object's [[Get]] returns the live
        // value of the exported binding (or "Module" for @@toStringTag); it does
        // not walk a prototype chain (it has none). Dispatch by type here so every
        // call site picks it up, mirroring the Proxy handling above.
        if (obj is JsModuleNamespace ns)
        {
            return key.IsSymbol ? ns.Get(key.AsSymbol) : ns.Get(key.AsString);
        }
        // ECMA-262 §25.2.5: integer-indexed exotic element access is handled
        // by the typed-array object before ordinary descriptor lookup.
        if (obj is JsTypedArray ta && key.IsString && IsCanonicalArrayIndex(key.AsString))
        {
            return ta.Get(key.AsString);
        }

        for (var o = obj; o is not null; o = o.Prototype)
        {
            var desc = o.GetOwnPropertyDescriptor(key);
            if (desc is null)
            {
                continue;
            }

            var d = desc.Value;
            if (d.IsAccessor)
            {
                if (d.Getter is null)
                {
                    return JsValue.Undefined;
                }

                return Call(vm, JsValue.Object(d.Getter), receiver, Array.Empty<JsValue>());
            }
            return d.Value;
        }
        return JsValue.Undefined;
    }

    /// <summary>§7.3.12 HasProperty — chain-walking presence check. Routes
    /// through <see cref="JsProxy"/> when the target is a Proxy so the
    /// <c>has</c> trap fires.</summary>
    public static bool HasProperty(JsObject obj, JsPropertyKey key)
    {
        ArgumentNullException.ThrowIfNull(obj);
        // JsObject.Has already walks the prototype chain and JsProxy
        // overrides Has to dispatch the `has` trap.
        return obj.Has(key);
    }

    public static bool HasProperty(JsObject obj, string key)
        => HasProperty(obj, JsPropertyKey.String(key));

    /// <summary>§10.1.9 OrdinarySet — chain-walking write that respects
    /// accessor setters. Returns false if the write was rejected.</summary>
    public static bool Set(JsVm? vm, JsObject obj, string key, JsValue value, JsValue receiver = default)
        => Set(vm, obj, JsPropertyKey.String(key), value, receiver);

    public static bool Set(JsVm? vm, JsObject obj, JsPropertyKey key, JsValue value, JsValue receiver = default)
    {
        if (receiver.IsUndefined)
        {
            receiver = JsValue.Object(obj);
        }

        return SetCore(vm, obj, key, value, receiver);
    }

    public static bool SetWithReceiver(JsVm? vm, JsObject obj, JsPropertyKey key, JsValue value, JsValue receiver)
        => SetCore(vm, obj, key, value, receiver);

    private static bool SetCore(JsVm? vm, JsObject obj, JsPropertyKey key, JsValue value, JsValue receiver)
    {
        // §10.5.9: Proxy exotic objects route writes through the [[Set]] internal
        // method (which consults the `set` trap). See note on Get above.
        if (obj is JsProxy proxy)
        {
            return proxy.SetWithReceiver(key, value, receiver);
        }
        // §10.4.6.9 — a Module Namespace Exotic Object's [[Set]] always returns
        // false: a strict assignment throws (the caller surfaces it), a sloppy one
        // silently no-ops. Dispatch by type so the boolean rejection is observed
        // everywhere (the void JsObject.Set override cannot signal it).
        if (obj is JsModuleNamespace)
        {
            return false;
        }
        // ECMA-262 §25.2.5 integer-indexed exotic writes go to the backing
        // ArrayBuffer instead of creating ordinary own properties.
        if (obj is JsTypedArray ta && key.IsString && IsCanonicalArrayIndex(key.AsString))
        {
            if (int.TryParse(key.AsString, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var index)
                && index >= 0 && index < ta.Length)
            {
                ta.SetElement(index, value, vm?.Realm);
            }

            return true;
        }
        // Find existing descriptor anywhere on the chain.
        for (var o = obj; o is not null; o = o.Prototype)
        {
            var desc = o.GetOwnPropertyDescriptor(key);
            if (desc is null)
            {
                continue;
            }

            var d = desc.Value;
            if (d.IsAccessor)
            {
                if (d.Setter is null)
                {
                    return false;
                }

                Call(vm, JsValue.Object(d.Setter), receiver, new[] { value });
                return true;
            }
            if (!d.Writable)
            {
                return false;
            }
            // §10.1.9.2 OrdinarySetWithOwnDescriptor: a writable data property
            // is updated on the *receiver*, not on the object that owns the
            // inherited descriptor. When the receiver differs from `obj` (super
            // writes, Reflect.set, Proxy) the value lands on the receiver's own
            // slot; otherwise this is the ordinary same-object write.
            break;
        }
        // §10.1.9.2 steps 3-4 — the value is created/updated on the *receiver*.
        // For the common case (receiver === obj) this is identical to the old
        // behavior; for super[...] = v / Reflect.set the property is created on
        // the receiver rather than the prototype that was walked.
        if (!receiver.IsObject)
        {
            return false;
        }

        var target = receiver.AsObject;
        if (target.HasOwn(key))
        {
            var existing = target.GetOwnPropertyDescriptor(key);
            // A receiver own accessor / non-writable data property blocks the
            // create-data path per §10.1.9.2 (CreateDataProperty would fail).
            if (existing is { IsAccessor: true })
            {
                return false;
            }

            if (existing is { Writable: false })
            {
                return false;
            }

            target.Set(key, value);
            return true;
        }
        if (!target.Extensible)
        {
            return false;
        }

        return target.DefineOwnProperty(key, PropertyDescriptor.Data(value));
    }

    /// <summary>§7.3.14 Call — dispatch to native or JS callables. For JS
    /// functions, requires the VM. For native functions, the VM is optional.</summary>
    public static JsValue Call(JsVm? vm, JsValue callee, JsValue thisValue, JsValue[] args)
    {
        if (!callee.IsObject)
        {
            throw NotAFunction(vm, JsValue.ToStringValue(callee));
        }

        return callee.AsObject switch
        {
            JsNativeFunction nat => CallNative(nat, thisValue, args),
            JsFunction fn => vm is not null
                ? vm.CallFunction(fn, thisValue, args)
                : CallForeignRealm(fn, thisValue, args),
            JsBoundFunction bf => Call(vm, JsValue.Object(bf.Target), bf.BoundThis,
                ConcatBoundArgs(bf.BoundArgs, args)),
            JsProxy proxy => proxy.ProxyCall(thisValue, args),
            _ => throw NotAFunction(vm, callee.AsObject.ToString() ?? "object"),
        };
    }

    private static JsValue CallNative(JsNativeFunction nat, JsValue thisValue, JsValue[] args)
        => nat.Body(thisValue, args);

    /// <summary>wp:M3-83 — host invokes a foreign-realm JS function with no
    /// ambient VM (e.g. Function.prototype.call on a foreign function, where
    /// the install-site realm's ActiveVm is null because the host VM is the
    /// one on the stack). Recover via the callee's own realm: §9.3.1 [[Realm]]
    /// is the right context for the body, and its owner runtime publishes its
    /// primary VM as ActiveVm. Kept out of <see cref="Call"/> so the lambda's
    /// closure is allocated only on this cold path — inlined into Call, the
    /// capture forces a display-class allocation on every dispatch (measured
    /// 123 MB of churn on an x.com load).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JsValue CallForeignRealm(JsFunction fn, JsValue thisValue, JsValue[] args)
        => fn.Realm?.OwnerRuntime is { } owner
            ? owner.WithActiveVm(foreignVm => foreignVm.CallFunction(fn, thisValue, args))
            : throw new InvalidOperationException("VM required to call JS function");

    /// <summary>§7.3.14 Call step 2 — calling a non-callable is a TypeError.
    /// Uses the realm's TypeError when a VM is available so embedders observe a
    /// real <c>TypeError</c> (string fallback only for the realm-less path).</summary>
    private static JsThrow NotAFunction(JsVm? vm, string detail) =>
        vm is not null
            ? new JsThrow(vm.Realm.NewTypeError($"not a function: {detail}"))
            : new JsThrow(JsValue.String($"not a function: {detail}"));

    /// <summary>§7.3.15 Construct — analogous for <c>new</c>.</summary>
    public static JsValue Construct(JsVm? vm, JsValue ctor, JsValue[] args, JsObject? newTarget = null)
    {
        if (!IsConstructor(ctor))
        {
            throw new JsThrow(JsValue.String($"not a constructor: {JsValue.ToStringValue(ctor)}"));
        }

        newTarget ??= ctor.AsObject;
        return ctor.AsObject switch
        {
            JsNativeFunction nat => nat.Body(JsValue.Object(newTarget), args),
            JsFunction fn => vm is not null
                ? vm.ConstructFunction(fn, args, newTarget)
                : ConstructForeignRealm(fn, args, newTarget),
            JsBoundFunction bf => Construct(vm, JsValue.Object(bf.Target),
                ConcatBoundArgs(bf.BoundArgs, args), newTarget),
            JsProxy proxy => proxy.ProxyConstruct(args, newTarget),
            _ => throw new JsThrow(JsValue.String($"not a constructor: {ctor.AsObject}")),
        };
    }

    /// <summary>wp:M3-83 — mirror <see cref="CallForeignRealm"/> for
    /// [[Construct]]: a VM-less host construct of a foreign-realm function
    /// runs on that realm's own VM. Kept out of <see cref="Construct"/> so
    /// the lambda's closure is allocated only on this cold path.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JsValue ConstructForeignRealm(JsFunction fn, JsValue[] args, JsObject newTarget)
        => fn.Realm?.OwnerRuntime is { } owner
            ? owner.WithActiveVm(foreignVm => foreignVm.ConstructFunction(fn, args, newTarget))
            : throw new InvalidOperationException("VM required to construct JS function");

    /// <summary>§7.2.10 SameValue — like StrictEqual but +0 ≠ -0 and NaN = NaN.</summary>
    public static bool SameValue(JsValue a, JsValue b)
    {
        if (a.Kind != b.Kind)
        {
            return false;
        }

        if (a.Kind == JsValueKind.Number)
        {
            var na = a.AsNumber; var nb = b.AsNumber;
            if (double.IsNaN(na) && double.IsNaN(nb))
            {
                return true;
            }

            if (na == 0 && nb == 0)
            {
                return double.IsNegative(na) == double.IsNegative(nb);
            }

            return na == nb;
        }
        return JsValue.StrictEquals(a, b);
    }

    /// <summary>§7.2.11 SameValueZero — SameValue but +0 = -0. Used by Map/Set.</summary>
    public static bool SameValueZero(JsValue a, JsValue b)
    {
        if (a.Kind != b.Kind)
        {
            return false;
        }

        if (a.Kind == JsValueKind.Number)
        {
            var na = a.AsNumber; var nb = b.AsNumber;
            if (double.IsNaN(na) && double.IsNaN(nb))
            {
                return true;
            }

            return na == nb;
        }
        return JsValue.StrictEquals(a, b);
    }

    // ==========================================================
    //               §7.4 Iterator abstract operations (B3-2)
    // ==========================================================

    /// <summary>§7.4.1 GetIterator(obj, hint). Resolves
    /// <c>obj[@@iterator]</c>, invokes it with <paramref name="value"/> as
    /// <c>this</c>, and validates the result is an Object. The companion
    /// <c>NextMethod</c> is pre-resolved once so <see cref="IteratorNext"/>
    /// does not re-walk the prototype chain on every step.</summary>
    public static IteratorRecord GetIterator(JsRealm realm, JsVm? vm, JsValue value, string hint = "sync")
    {
        ArgumentNullException.ThrowIfNull(realm);
        if (hint != "sync")
        {
            throw new NotSupportedException("async iterator hint not supported yet");
        }
        // §7.3.11: for primitive receivers we need to walk the wrapper
        // prototype (e.g. String.prototype[@@iterator]). Box via ToObject;
        // the iterator method is called with the original primitive as `this`
        // (wrapper methods unbox as needed).
        JsValue method;
        if (value.IsObject)
        {
            method = GetMethod(vm, value, Starling.Js.Intrinsics.SymbolCtor.Iterator);
        }
        else if (value.IsNullish)
        {
            throw new JsThrow(realm.NewTypeError("value is not iterable"));
        }
        else
        {
            var boxed = ToObject(realm, value);
            method = GetMethod(vm, JsValue.Object(boxed), Starling.Js.Intrinsics.SymbolCtor.Iterator);
        }
        if (method.IsUndefined || method.IsNull)
        {
            throw new JsThrow(realm.NewTypeError("value is not iterable"));
        }

        var iter = Call(vm, method, value, Array.Empty<JsValue>());
        if (!iter.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("iterator method did not return an object"));
        }

        var nextMethod = Get(vm, iter.AsObject, "next");
        return new IteratorRecord(iter, nextMethod, Done: false);
    }

    /// <summary>§7.4.4 IteratorNext.</summary>
    public static JsValue IteratorNext(JsRealm realm, JsVm? vm, IteratorRecord record, JsValue? value = null)
    {
        var args = value is null ? Array.Empty<JsValue>() : new[] { value.Value };
        var result = Call(vm, record.NextMethod, record.Iterator, args);
        if (!result.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("iterator.next() did not return an object"));
        }

        return result;
    }

    /// <summary>§7.4.5 IteratorComplete.</summary>
    public static bool IteratorComplete(JsVm? vm, JsValue iteratorResult)
    {
        if (!iteratorResult.IsObject)
        {
            return true;
        }

        return JsValue.ToBoolean(Get(vm, iteratorResult.AsObject, "done"));
    }

    /// <summary>§7.4.6 IteratorValue.</summary>
    public static JsValue IteratorValue(JsVm? vm, JsValue iteratorResult)
    {
        if (!iteratorResult.IsObject)
        {
            return JsValue.Undefined;
        }

        return Get(vm, iteratorResult.AsObject, "value");
    }

    /// <summary>§7.4.7 IteratorStep. Returns the iterator-result object, or
    /// <c>null</c> when the iterator has signalled completion.</summary>
    public static JsValue? IteratorStep(JsRealm realm, JsVm? vm, ref IteratorRecord record)
    {
        // §8.5.3 / §7.4.8 — if reading the next result (calling next() or
        // accessing its `done` attribute) completes abruptly, the iterator is
        // considered closed: mark the record Done so a surrounding
        // IteratorClose does NOT then call return() on it (the abrupt next()
        // result already terminates the iterator). dstr/array-elision-iter-abpt
        // et al. depend on return() NOT being invoked in this case.
        JsValue result;
        bool done;
        try
        {
            result = IteratorNext(realm, vm, record);
            done = IteratorComplete(vm, result);
        }
        catch
        {
            record = record with { Done = true };
            throw;
        }
        if (done)
        {
            record = record with { Done = true };
            return null;
        }
        return result;
    }

    /// <summary>§7.4.10 IteratorClose. Invokes the iterator's <c>return</c>
    /// method if present. Used by <c>for…of</c> and destructuring on abrupt
    /// completion. When the wrapped completion is NOT itself throwing
    /// (<paramref name="isThrowing"/> false), a <c>return()</c> that throws
    /// propagates (step 8) and a <c>return()</c> whose result is not an Object
    /// is a TypeError (step 9); when the completion IS throwing, the original
    /// error wins and any close error is swallowed (step 7).</summary>
    public static void IteratorClose(JsVm? vm, IteratorRecord record, bool isThrowing = false)
    {
        if (!record.Iterator.IsObject)
        {
            return;
        }

        JsValue ret;
        try
        {
            ret = GetMethod(vm, record.Iterator, "return");
        }
        catch
        {
            // Resolving `return` (a Get + IsCallable check) may itself throw.
            if (isThrowing)
            {
                return;
            }

            throw;
        }
        if (ret.IsUndefined || ret.IsNull)
        {
            return;
        }

        JsValue innerResult;
        try
        {
            innerResult = Call(vm, ret, record.Iterator, Array.Empty<JsValue>());
        }
        catch
        {
            // §7.4.10 step 7/8 — swallow only when the inner completion is
            // already a throw; otherwise the return() error propagates.
            if (isThrowing)
            {
                return;
            }

            throw;
        }
        // §7.4.10 step 9 — only validated on a normal inner completion.
        if (!isThrowing && !innerResult.IsObject)
        {
            throw new JsThrow(vm is not null
                ? vm.Realm.NewTypeError("iterator return() result is not an object")
                : JsValue.String("iterator return() result is not an object"));
        }
    }

    /// <summary>§7.3.11 GetMethod — returns <see cref="JsValue.Undefined"/>
    /// when the resolved value is null/undefined; throws TypeError when the
    /// slot exists but isn't callable.</summary>
    public static JsValue GetMethod(JsVm? vm, JsValue value, JsPropertyKey key)
    {
        if (!value.IsObject)
        {
            // Primitives need to box to reach prototype methods (e.g. String[Symbol.iterator]).
            if (value.IsNullish)
            {
                return JsValue.Undefined;
            }
            // Caller is expected to ToObject before calling for primitives;
            // we don't have a realm reference, so just return undefined for
            // non-object primitives here.
            return JsValue.Undefined;
        }
        var v = Get(vm, value.AsObject, key);
        if (v.IsUndefined || v.IsNull)
        {
            return JsValue.Undefined;
        }

        if (!IsCallable(v))
        {
            throw vm is not null
                ? new JsThrow(vm.Realm.NewTypeError($"{key} is not a function"))
                : new JsThrow(JsValue.String("property is not callable"));
        }

        return v;
    }

    public static JsValue GetMethod(JsVm? vm, JsValue value, JsSymbol symbol)
        => GetMethod(vm, value, JsPropertyKey.Symbol(symbol));

    private static bool IsCanonicalArrayIndex(string key)
    {
        if (key.Length == 0 || key[0] == '-' || key.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        return int.TryParse(key, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    private static JsValue[] ConcatBoundArgs(IReadOnlyList<JsValue> bound, JsValue[] extra)
    {
        if (bound.Count == 0)
        {
            return extra;
        }

        var combined = new JsValue[bound.Count + extra.Length];
        for (var i = 0; i < bound.Count; i++)
        {
            combined[i] = bound[i];
        }

        Array.Copy(extra, 0, combined, bound.Count, extra.Length);
        return combined;
    }
}

/// <summary>§7.4.1 IteratorRecord — bundles the iterator object, the
/// pre-resolved <c>next</c> method, and the <c>done</c> flag. The
/// <c>NextMethod</c> is cached at <c>GetIterator</c> time per spec so the
/// per-step <c>iterator.next</c> property walk doesn't happen on every loop
/// iteration.</summary>
public readonly record struct IteratorRecord(JsValue Iterator, JsValue NextMethod, bool Done);
