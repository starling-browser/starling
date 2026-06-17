using System.Globalization;
using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// §27.2 The Promise intrinsic. Owns the Promise constructor, the
/// reaction/resolution machinery (§27.2.1.*), prototype methods
/// (<c>then</c>/<c>catch</c>/<c>finally</c>, §27.2.5.*) and the
/// constructor-level statics (<c>resolve</c>/<c>reject</c>/<c>all</c>/
/// <c>allSettled</c>/<c>any</c>/<c>race</c>/<c>withResolvers</c>,
/// §27.2.4.*).
/// </summary>
/// <remarks>
/// <para>
/// Reactions are scheduled onto <see cref="JsRealm.Microtasks"/>; the VM
/// drains the queue at the bottom of every top-level <c>Run</c>, so unit
/// tests see settlement without manual pumping. When a host installs a
/// scheduler via <see cref="JsRuntime.SetMicrotaskScheduler"/>, jobs are
/// handed off to the host loop instead.
/// </para>
/// <para>
/// Simplifications relative to the spec:
/// </para>
/// <list type="bullet">
///   <item><description>
///     Iterable-accepting statics (<c>all</c>, <c>allSettled</c>, <c>any</c>,
///     <c>race</c>) take array-likes (length + indexed access) rather than
///     general iterables.
///   </description></item>
///   <item><description>
///     <c>Promise.any</c>'s aggregate rejection is a real
///     <c>AggregateError</c> instance built via
///     <c>realm.NewAggregateError</c> with an own <c>errors</c> JsArray
///     of the rejection reasons.
///   </description></item>
///   <item><description>
///     Uncaught microtask exceptions surface through the realm's console
///     sink. <c>PromiseRejectionEvent</c> dispatch on <c>Window</c> is not wired
///     yet.
///   </description></item>
/// </list>
/// </remarks>
public static class PromiseCtor
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var proto = realm.PromisePrototype;

        // ----------------------------------------------------------- Constructor
        // §27.2.3.1 Promise(executor) — executor MUST be callable. The
        // executor runs synchronously, receiving the (resolve, reject)
        // pair built from this promise's capability. Any thrown value
        // becomes the rejection reason.
        var ctor = new JsNativeFunction("Promise", (newTarget, args) =>
        {
            // §27.2.3.1 step 1: Promise requires `new` (or a derived super()).
            if (!IntrinsicHelpers.IsConstructInvocation(newTarget))
            {
                throw new JsThrow(realm.NewTypeError("Constructor Promise requires 'new'"));
            }

            if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
            {
                throw new JsThrow(realm.NewTypeError("Promise resolver is not a function"));
            }

            var executor = args[0];

            // §27.2.3.1 step 3: OrdinaryCreateFromConstructor — prototype from
            // new.target so `class P extends Promise {}` produces a P-prototyped
            // promise carrying the [[PromiseState]] slots.
            var instProto = IntrinsicHelpers.NewTargetPrototype(realm.ActiveVm, newTarget, proto);
            var promise = new JsPromise(instProto);
            var (resolve, reject) = CreateResolvingFunctions(realm, promise);
            try
            {
                AbstractOperations.Call(realm.ActiveVm, executor, JsValue.Undefined,
                    new[] { resolve, reject });
            }
            catch (JsThrow ex)
            {
                // §27.2.3.1 step 12 — IfAbruptRejectPromise. Note: if the
                // executor already called resolve/reject, this is a no-op
                // (the resolving functions are one-shot).
                RejectPromise(realm, promise, ex.Value);
            }
            return JsValue.Object(promise);
        }, isConstructor: true);
        ctor.SetPrototypeOf(realm.FunctionPrototype);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));
        ctor.DefineOwnProperty("name",
            PropertyDescriptor.Data(JsValue.String("Promise"), writable: false, enumerable: false, configurable: true));
        ctor.DefineOwnProperty("length",
            PropertyDescriptor.Data(JsValue.Number(1), writable: false, enumerable: false, configurable: true));

        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
        proto.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("Promise"), writable: false, enumerable: false, configurable: true));

        // --------------------------------------------------------- Statics
        DefineMethod(realm, ctor, "resolve", (thisV, args) =>
            ResolveStatic(realm, args.Length > 0 ? args[0] : JsValue.Undefined), length: 1);
        DefineMethod(realm, ctor, "reject", (thisV, args) =>
            RejectStatic(realm, args.Length > 0 ? args[0] : JsValue.Undefined), length: 1);
        DefineMethod(realm, ctor, "all", (thisV, args) =>
            All(realm, args.Length > 0 ? args[0] : JsValue.Undefined), length: 1);
        DefineMethod(realm, ctor, "allSettled", (thisV, args) =>
            AllSettled(realm, args.Length > 0 ? args[0] : JsValue.Undefined), length: 1);
        DefineMethod(realm, ctor, "any", (thisV, args) =>
            Any(realm, args.Length > 0 ? args[0] : JsValue.Undefined), length: 1);
        DefineMethod(realm, ctor, "race", (thisV, args) =>
            Race(realm, args.Length > 0 ? args[0] : JsValue.Undefined), length: 1);
        DefineMethod(realm, ctor, "withResolvers", (thisV, args) => WithResolvers(realm), length: 0);

        // --------------------------------------------------------- Prototype
        DefineMethod(realm, proto, "then", (thisV, args) =>
        {
            var onFulfilled = args.Length > 0 ? args[0] : JsValue.Undefined;
            var onRejected = args.Length > 1 ? args[1] : JsValue.Undefined;
            return Then(realm, thisV, onFulfilled, onRejected);
        }, length: 2);

        DefineMethod(realm, proto, "catch", (thisV, args) =>
        {
            var onRejected = args.Length > 0 ? args[0] : JsValue.Undefined;
            return Then(realm, thisV, JsValue.Undefined, onRejected);
        }, length: 1);

        DefineMethod(realm, proto, "finally", (thisV, args) =>
        {
            var onFinally = args.Length > 0 ? args[0] : JsValue.Undefined;
            return Finally(realm, thisV, onFinally);
        }, length: 1);

        realm.PromiseConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("Promise",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    // ====================================================================
    //                       Resolving-function machinery
    // ====================================================================

    /// <summary>§27.2.1.3 CreateResolvingFunctions. Produces the
    /// (resolve, reject) pair handed to the executor. Both share a
    /// single <c>[[AlreadyResolved]]</c> flag — whichever runs first
    /// settles the promise; the other becomes a no-op.</summary>
    private static (JsValue Resolve, JsValue Reject) CreateResolvingFunctions(
        JsRealm realm, JsPromise promise)
    {
        var alreadyResolved = new bool[1]; // boxed flag shared by closures

        var resolve = new JsNativeFunction("", (thisV, args) =>
        {
            if (alreadyResolved[0])
            {
                return JsValue.Undefined;
            }

            alreadyResolved[0] = true;
            var value = args.Length > 0 ? args[0] : JsValue.Undefined;
            ResolvePromise(realm, promise, value);
            return JsValue.Undefined;
        }, isConstructor: false);

        var reject = new JsNativeFunction("", (thisV, args) =>
        {
            if (alreadyResolved[0])
            {
                return JsValue.Undefined;
            }

            alreadyResolved[0] = true;
            var reason = args.Length > 0 ? args[0] : JsValue.Undefined;
            RejectPromise(realm, promise, reason);
            return JsValue.Undefined;
        }, isConstructor: false);

        return (JsValue.Object(resolve), JsValue.Object(reject));
    }

    /// <summary>B1b-2c — public surface for async-function machinery.
    /// Settles <paramref name="promise"/> via the standard adoption path.</summary>
    public static void Resolve(JsRealm realm, JsPromise promise, JsValue resolution)
        => ResolvePromise(realm, promise, resolution);

    /// <summary>B1b-2c — public surface mirror of <see cref="Resolve"/>.</summary>
    public static void Reject(JsRealm realm, JsPromise promise, JsValue reason)
        => RejectPromise(realm, promise, reason);

    /// <summary>§27.2.1.4 Promise Resolve Functions. Adoption rules:
    /// resolving with the promise itself is a TypeError; resolving with a
    /// thenable schedules a microtask to follow it; otherwise fulfill
    /// directly.</summary>
    private static void ResolvePromise(JsRealm realm, JsPromise promise, JsValue resolution)
    {
        if (resolution.IsObject && ReferenceEquals(resolution.AsObject, promise))
        {
            RejectPromise(realm, promise, realm.NewTypeError("Chaining cycle detected for promise"));
            return;
        }

        if (!resolution.IsObject)
        {
            FulfillPromise(realm, promise, resolution);
            return;
        }

        // Thenable adoption — Promise instances take a shortcut (we don't
        // re-walk .then for own-realm promises in §27.2.7 terms, but
        // observationally this is fine because the chained-promise's
        // settlement still pumps via microtask).
        JsValue then;
        try
        {
            then = AbstractOperations.Get(realm.ActiveVm, resolution.AsObject, "then");
        }
        catch (JsThrow ex)
        {
            RejectPromise(realm, promise, ex.Value);
            return;
        }
        if (!AbstractOperations.IsCallable(then))
        {
            FulfillPromise(realm, promise, resolution);
            return;
        }

        // §27.2.2.1 NewPromiseResolveThenableJob — schedule the .then call
        // for the next microtask. Spec uses a job record; we capture
        // directly.
        realm.Microtasks.Enqueue(() =>
        {
            var (innerResolve, innerReject) = CreateResolvingFunctions(realm, promise);
            try
            {
                AbstractOperations.Call(realm.ActiveVm, then, resolution,
                    new[] { innerResolve, innerReject });
            }
            catch (JsThrow ex)
            {
                AbstractOperations.Call(realm.ActiveVm, innerReject, JsValue.Undefined,
                    new[] { ex.Value });
            }
        });
    }

    /// <summary>§27.2.1.5 FulfillPromise — transition state and flush
    /// fulfillment reactions to the microtask queue.</summary>
    private static void FulfillPromise(JsRealm realm, JsPromise promise, JsValue value)
    {
        if (!promise.Fulfill(value))
        {
            return;
        }

        var reactions = promise.FulfillReactions.ToArray();
        promise.FulfillReactions.Clear();
        promise.RejectReactions.Clear();
        foreach (var reaction in reactions)
        {
            EnqueueReactionJob(realm, reaction, value);
        }
    }

    /// <summary>§27.2.1.7 RejectPromise — symmetric to fulfill. Step 7:
    /// when the promise is not handled, HostPromiseRejectionTracker is told
    /// so the host can report the rejection if no handler arrives.</summary>
    private static void RejectPromise(JsRealm realm, JsPromise promise, JsValue reason)
    {
        if (!promise.Reject(reason))
        {
            return;
        }

        var reactions = promise.RejectReactions.ToArray();
        promise.FulfillReactions.Clear();
        promise.RejectReactions.Clear();
        if (!promise.IsHandled)
        {
            realm.OnUnhandledRejection?.Invoke(promise);
        }

        foreach (var reaction in reactions)
        {
            EnqueueReactionJob(realm, reaction, reason);
        }
    }

    /// <summary>§27.2.2.1 NewPromiseReactionJob — schedules a single
    /// reaction onto the microtask queue. The job invokes the handler
    /// (or applies pass-through identity/thrower when omitted) and
    /// settles the chained promise with the outcome.</summary>
    private static void EnqueueReactionJob(JsRealm realm, PromiseReaction reaction, JsValue argument)
    {
        realm.Microtasks.Enqueue(() =>
        {
            var cap = reaction.Capability;
            var handler = reaction.Handler;
            JsValue handlerResult;
            bool isThrow;

            if (!AbstractOperations.IsCallable(handler))
            {
                // Pass-through: fulfill-reaction with no handler → identity;
                // reject-reaction with no handler → re-throw.
                handlerResult = argument;
                isThrow = reaction.Type == PromiseReactionType.Reject;
            }
            else
            {
                try
                {
                    handlerResult = AbstractOperations.Call(realm.ActiveVm, handler,
                        JsValue.Undefined, new[] { argument });
                    isThrow = false;
                }
                catch (JsThrow ex)
                {
                    handlerResult = ex.Value;
                    isThrow = true;
                }
            }

            if (isThrow)
            {
                AbstractOperations.Call(realm.ActiveVm, cap.Reject, JsValue.Undefined, new[] { handlerResult });
            }
            else
            {
                AbstractOperations.Call(realm.ActiveVm, cap.Resolve, JsValue.Undefined, new[] { handlerResult });
            }
        });
    }

    // ====================================================================
    //                              .then
    // ====================================================================

    /// <summary>§27.2.5.4 Promise.prototype.then.</summary>
    private static JsValue Then(JsRealm realm, JsValue thisV, JsValue onFulfilled, JsValue onRejected)
    {
        if (!thisV.IsObject || thisV.AsObject is not JsPromise self)
        {
            throw new JsThrow(realm.NewTypeError("Promise.prototype.then called on non-Promise"));
        }

        var capability = NewPromiseCapability(realm);
        var fulfillHandler = AbstractOperations.IsCallable(onFulfilled) ? onFulfilled : JsValue.Undefined;
        var rejectHandler = AbstractOperations.IsCallable(onRejected) ? onRejected : JsValue.Undefined;

        var fulfillReaction = new PromiseReaction(capability, PromiseReactionType.Fulfill, fulfillHandler);
        var rejectReaction = new PromiseReaction(capability, PromiseReactionType.Reject, rejectHandler);

        switch (self.State)
        {
            case PromiseState.Pending:
                self.FulfillReactions.Add(fulfillReaction);
                self.RejectReactions.Add(rejectReaction);
                break;
            case PromiseState.Fulfilled:
                EnqueueReactionJob(realm, fulfillReaction, self.Result);
                break;
            case PromiseState.Rejected:
                // HostPromiseRejectionTracker(promise, "handle") — a late
                // handler retracts a queued unhandled-rejection report.
                if (!self.IsHandled)
                {
                    realm.OnRejectionHandled?.Invoke(self);
                }

                EnqueueReactionJob(realm, rejectReaction, self.Result);
                break;
        }

        // §27.2.5.4.1 PerformPromiseThen step 12 — the promise now has a
        // handler regardless of which callbacks were supplied (omitted ones
        // become pass-through identity/thrower on the chained promise).
        self.IsHandled = true;

        return JsValue.Object(capability.Promise);
    }

    // ====================================================================
    //                              .finally
    // ====================================================================

    /// <summary>§27.2.5.3 Promise.prototype.finally — wraps the user-supplied
    /// handler so it runs on both branches, then forwards the original
    /// value/reason unless the handler itself throws or returns a rejected
    /// thenable.</summary>
    private static JsValue Finally(JsRealm realm, JsValue thisV, JsValue onFinally)
    {
        if (!thisV.IsObject || thisV.AsObject is not JsPromise)
        {
            throw new JsThrow(realm.NewTypeError("Promise.prototype.finally called on non-Promise"));
        }

        if (!AbstractOperations.IsCallable(onFinally))
        {
            return Then(realm, thisV, onFinally, onFinally);
        }

        // Build the two thunks the spec uses. Each invokes onFinally, then
        // adopts a then-chained promise that forwards the original outcome.
        var thenFinally = new JsNativeFunction("", (selfThis, args) =>
        {
            var value = args.Length > 0 ? args[0] : JsValue.Undefined;
            var followed = AbstractOperations.Call(realm.ActiveVm, onFinally, JsValue.Undefined, Array.Empty<JsValue>());
            var followedPromise = AdoptAsPromise(realm, followed);
            // Forward the original value once the inner settlement completes.
            var forwarder = new JsNativeFunction("", (_, _) => value, isConstructor: false);
            return Then(realm, JsValue.Object(followedPromise), JsValue.Object(forwarder), JsValue.Undefined);
        }, isConstructor: false);

        var catchFinally = new JsNativeFunction("", (selfThis, args) =>
        {
            var reason = args.Length > 0 ? args[0] : JsValue.Undefined;
            var followed = AbstractOperations.Call(realm.ActiveVm, onFinally, JsValue.Undefined, Array.Empty<JsValue>());
            var followedPromise = AdoptAsPromise(realm, followed);
            // Rethrow the original reason once the inner settlement completes.
            var thrower = new JsNativeFunction("", (_, _) => throw new JsThrow(reason), isConstructor: false);
            return Then(realm, JsValue.Object(followedPromise), JsValue.Object(thrower), JsValue.Undefined);
        }, isConstructor: false);

        return Then(realm, thisV, JsValue.Object(thenFinally), JsValue.Object(catchFinally));
    }

    // ====================================================================
    //                              Statics
    // ====================================================================

    /// <summary>§27.2.4.7 Promise.resolve. Returns the input verbatim if it's
    /// already a <see cref="JsPromise"/>; otherwise builds a fresh promise
    /// and resolves it with the value (including thenable adoption).</summary>
    private static JsValue ResolveStatic(JsRealm realm, JsValue value)
    {
        if (value.IsObject && value.AsObject is JsPromise existing)
        {
            return JsValue.Object(existing);
        }

        var p = new JsPromise(realm.PromisePrototype);
        ResolvePromise(realm, p, value);
        return JsValue.Object(p);
    }

    /// <summary>§27.2.4.6 Promise.reject.</summary>
    private static JsValue RejectStatic(JsRealm realm, JsValue reason)
    {
        var p = new JsPromise(realm.PromisePrototype);
        RejectPromise(realm, p, reason);
        return JsValue.Object(p);
    }

    /// <summary>§27.2.4.1 Promise.all (array-like simplification). Resolves
    /// to an array-like of values in source order; rejects with the first
    /// rejection reason.</summary>
    private static JsValue All(JsRealm realm, JsValue iterable)
    {
        var capability = NewPromiseCapability(realm);
        if (!TryArrayLikeToList(realm, iterable, capability, out var items))
        {
            return JsValue.Object(capability.Promise);
        }

        if (items.Count == 0)
        {
            var emptyArr = MakeArrayLike(realm, Array.Empty<JsValue>());
            AbstractOperations.Call(realm.ActiveVm, capability.Resolve, JsValue.Undefined, new[] { emptyArr });
            return JsValue.Object(capability.Promise);
        }

        var results = new JsValue[items.Count];
        var remaining = new int[1] { items.Count };
        for (var i = 0; i < items.Count; i++)
        {
            var idx = i;
            results[idx] = JsValue.Undefined;
            var item = ResolveStatic(realm, items[idx]);
            var onFulfilled = new JsNativeFunction("", (_, args) =>
            {
                results[idx] = args.Length > 0 ? args[0] : JsValue.Undefined;
                if (--remaining[0] == 0)
                {
                    var arr = MakeArrayLike(realm, results);
                    AbstractOperations.Call(realm.ActiveVm, capability.Resolve, JsValue.Undefined, new[] { arr });
                }
                return JsValue.Undefined;
            }, isConstructor: false);
            Then(realm, item, JsValue.Object(onFulfilled), capability.Reject);
        }
        return JsValue.Object(capability.Promise);
    }

    /// <summary>§27.2.4.2 Promise.allSettled.</summary>
    private static JsValue AllSettled(JsRealm realm, JsValue iterable)
    {
        var capability = NewPromiseCapability(realm);
        if (!TryArrayLikeToList(realm, iterable, capability, out var items))
        {
            return JsValue.Object(capability.Promise);
        }

        if (items.Count == 0)
        {
            var emptyArr = MakeArrayLike(realm, Array.Empty<JsValue>());
            AbstractOperations.Call(realm.ActiveVm, capability.Resolve, JsValue.Undefined, new[] { emptyArr });
            return JsValue.Object(capability.Promise);
        }

        var results = new JsValue[items.Count];
        var remaining = new int[1] { items.Count };

        void TryFinalize()
        {
            if (--remaining[0] != 0)
            {
                return;
            }

            var arr = MakeArrayLike(realm, results);
            AbstractOperations.Call(realm.ActiveVm, capability.Resolve, JsValue.Undefined, new[] { arr });
        }

        for (var i = 0; i < items.Count; i++)
        {
            var idx = i;
            var item = ResolveStatic(realm, items[idx]);
            var onFulfilled = new JsNativeFunction("", (_, args) =>
            {
                var entry = realm.NewOrdinaryObject();
                entry.DefineOwnProperty("status",
                    PropertyDescriptor.Data(JsValue.String("fulfilled"), writable: true, enumerable: true, configurable: true));
                entry.DefineOwnProperty("value",
                    PropertyDescriptor.Data(args.Length > 0 ? args[0] : JsValue.Undefined, writable: true, enumerable: true, configurable: true));
                results[idx] = JsValue.Object(entry);
                TryFinalize();
                return JsValue.Undefined;
            }, isConstructor: false);
            var onRejected = new JsNativeFunction("", (_, args) =>
            {
                var entry = realm.NewOrdinaryObject();
                entry.DefineOwnProperty("status",
                    PropertyDescriptor.Data(JsValue.String("rejected"), writable: true, enumerable: true, configurable: true));
                entry.DefineOwnProperty("reason",
                    PropertyDescriptor.Data(args.Length > 0 ? args[0] : JsValue.Undefined, writable: true, enumerable: true, configurable: true));
                results[idx] = JsValue.Object(entry);
                TryFinalize();
                return JsValue.Undefined;
            }, isConstructor: false);
            Then(realm, item, JsValue.Object(onFulfilled), JsValue.Object(onRejected));
        }
        return JsValue.Object(capability.Promise);
    }

    /// <summary>§27.2.4.3 Promise.any. Resolves with the first fulfillment;
    /// if every input rejects, rejects with an <c>AggregateError</c> whose
    /// <c>errors</c> own property is a JsArray of the rejection reasons.</summary>
    private static JsValue Any(JsRealm realm, JsValue iterable)
    {
        var capability = NewPromiseCapability(realm);
        if (!TryArrayLikeToList(realm, iterable, capability, out var items))
        {
            return JsValue.Object(capability.Promise);
        }

        if (items.Count == 0)
        {
            // Empty iterable → immediate AggregateError, per §27.2.4.3.
            AbstractOperations.Call(realm.ActiveVm, capability.Reject, JsValue.Undefined,
                new[] { MakeAggregateError(realm, Array.Empty<JsValue>(), "All promises were rejected") });
            return JsValue.Object(capability.Promise);
        }

        var errors = new JsValue[items.Count];
        var remaining = new int[1] { items.Count };
        for (var i = 0; i < items.Count; i++)
        {
            var idx = i;
            errors[idx] = JsValue.Undefined;
            var item = ResolveStatic(realm, items[idx]);
            var onRejected = new JsNativeFunction("", (_, args) =>
            {
                errors[idx] = args.Length > 0 ? args[0] : JsValue.Undefined;
                if (--remaining[0] == 0)
                {
                    AbstractOperations.Call(realm.ActiveVm, capability.Reject, JsValue.Undefined,
                        new[] { MakeAggregateError(realm, errors, "All promises were rejected") });
                }
                return JsValue.Undefined;
            }, isConstructor: false);
            Then(realm, item, capability.Resolve, JsValue.Object(onRejected));
        }
        return JsValue.Object(capability.Promise);
    }

    /// <summary>§27.2.4.5 Promise.race.</summary>
    private static JsValue Race(JsRealm realm, JsValue iterable)
    {
        var capability = NewPromiseCapability(realm);
        if (!TryArrayLikeToList(realm, iterable, capability, out var items))
        {
            return JsValue.Object(capability.Promise);
        }

        for (var i = 0; i < items.Count; i++)
        {
            var item = ResolveStatic(realm, items[i]);
            Then(realm, item, capability.Resolve, capability.Reject);
        }
        // Empty race never settles, matching spec (§27.2.4.5 step 8).
        return JsValue.Object(capability.Promise);
    }

    /// <summary>§27.2.4.8 Promise.withResolvers — Stage 4 sugar that returns
    /// the capability fields as a plain object.</summary>
    private static JsValue WithResolvers(JsRealm realm)
    {
        var capability = NewPromiseCapability(realm);
        var obj = realm.NewOrdinaryObject();
        obj.DefineOwnProperty("promise",
            PropertyDescriptor.Data(JsValue.Object(capability.Promise), writable: true, enumerable: true, configurable: true));
        obj.DefineOwnProperty("resolve",
            PropertyDescriptor.Data(capability.Resolve, writable: true, enumerable: true, configurable: true));
        obj.DefineOwnProperty("reject",
            PropertyDescriptor.Data(capability.Reject, writable: true, enumerable: true, configurable: true));
        return JsValue.Object(obj);
    }

    // ====================================================================
    //                              Helpers
    // ====================================================================

    /// <summary>§27.2.1.5 NewPromiseCapability, specialized for the
    /// built-in Promise constructor.</summary>
    private static PromiseCapability NewPromiseCapability(JsRealm realm)
    {
        var promise = new JsPromise(realm.PromisePrototype);
        var (resolve, reject) = CreateResolvingFunctions(realm, promise);
        return new PromiseCapability(promise, resolve, reject);
    }

    /// <summary>If <paramref name="value"/> is already a JsPromise, return it;
    /// otherwise wrap it in a fresh promise resolved with the value (handles
    /// thenable adoption via <see cref="ResolvePromise"/>).</summary>
    private static JsPromise AdoptAsPromise(JsRealm realm, JsValue value)
    {
        if (value.IsObject && value.AsObject is JsPromise p)
        {
            return p;
        }

        var fresh = new JsPromise(realm.PromisePrototype);
        ResolvePromise(realm, fresh, value);
        return fresh;
    }

    /// <summary>Pull items out of an array-like (length + index access). Promise
    /// statics do not consume general iterables yet.</summary>
    private static bool TryArrayLikeToList(
        JsRealm realm,
        JsValue iterable,
        PromiseCapability capability,
        out List<JsValue> items)
    {
        try
        {
            items = ArrayLikeToList(realm, iterable);
            return true;
        }
        catch (JsThrow ex)
        {
            items = new List<JsValue>();
            AbstractOperations.Call(realm.ActiveVm, capability.Reject, JsValue.Undefined, new[] { ex.Value });
            return false;
        }
    }

    private static List<JsValue> ArrayLikeToList(JsRealm realm, JsValue iterable)
    {
        if (!iterable.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("Promise iterable must be an object"));
        }

        var obj = iterable.AsObject;
        var lengthV = obj.Get("length");
        if (!lengthV.IsNumber)
        {
            throw new JsThrow(realm.NewTypeError("Promise iterable has no length"));
        }

        var len = (int)lengthV.AsNumber;
        var items = new List<JsValue>(len);
        for (var i = 0; i < len; i++)
        {
            items.Add(obj.Get(i.ToString(CultureInfo.InvariantCulture)));
        }

        return items;
    }

    /// <summary>Build the array-like return object for <c>Promise.all</c> and
    /// <c>allSettled</c>. This is still a plain object with
    /// <c>Array.prototype</c>, not a <see cref="JsArray"/>.</summary>
    private static JsValue MakeArrayLike(JsRealm realm, JsValue[] items)
    {
        var arr = realm.NewObjectWithProto(realm.ArrayPrototype);
        for (var i = 0; i < items.Length; i++)
        {
            arr.DefineOwnProperty(i.ToString(CultureInfo.InvariantCulture),
                PropertyDescriptor.Data(items[i], writable: true, enumerable: true, configurable: true));
        }
        arr.DefineOwnProperty("length",
            PropertyDescriptor.Data(JsValue.Number(items.Length), writable: true, enumerable: false, configurable: false));
        return JsValue.Object(arr);
    }

    /// <summary>Build a real AggregateError instance via
    /// <c>realm.NewAggregateError</c> (B2-3) and attach the rejection reasons
    /// as an own <c>errors</c> array (B2-4 JsArray). Used by §27.2.4.3
    /// Promise.any when every input rejects.</summary>
    private static JsValue MakeAggregateError(JsRealm realm, IReadOnlyList<JsValue> errors, string message)
    {
        var errValue = realm.NewAggregateError(message);
        var errorsArray = new JsArray(realm, errors);
        errValue.AsObject.DefineOwnProperty("errors",
            PropertyDescriptor.Data(JsValue.Object(errorsArray), writable: true, enumerable: false, configurable: true));
        return errValue;
    }

    /// <summary>Install a builtin method descriptor (W=true, E=false, C=true)
    /// per §17 default attributes.</summary>
    private static void DefineMethod(JsRealm realm, JsObject target, string name,
        Func<JsValue, JsValue[], JsValue> body, int length)
    {
        var fn = new JsNativeFunction(name, body, isConstructor: false);
        // §10.3 — built-in functions inherit from %Function.prototype% so
        // call/apply/bind work on them (`Promise.resolve.bind(Promise)` is a
        // common bundle idiom).
        fn.SetPrototypeOf(realm.FunctionPrototype);
        fn.DefineOwnProperty("name",
            PropertyDescriptor.Data(JsValue.String(name), writable: false, enumerable: false, configurable: true));
        fn.DefineOwnProperty("length",
            PropertyDescriptor.Data(JsValue.Number(length), writable: false, enumerable: false, configurable: true));
        target.DefineOwnProperty(name, PropertyDescriptor.BuiltinMethod(JsValue.Object(fn)));
    }
}
