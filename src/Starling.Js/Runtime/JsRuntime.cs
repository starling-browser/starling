using System.Diagnostics;
using Starling.Js.Intrinsics;

namespace Starling.Js.Runtime;

/// <summary>
/// Host-facing entry point for the Starling JS engine. Owns one
/// <see cref="JsRealm"/> and exposes helpers for host globals, native
/// functions, and microtask pumping.
/// </summary>
/// <remarks>
/// Browser page execution is driven through <c>IScriptSession</c> and
/// <c>WindowBinding</c>. Tests and low-level hosts can still use this type
/// directly with a custom host surface.
/// </remarks>
public sealed class JsRuntime
{
    // Shared ActivitySource with the rest of the engine subsystems
    // (StarlingTelemetry.SourceName = "Starling.Engine"). Telemetry listeners
    // pick spans up by name match — duplicating the constant here avoids a
    // Starling.Common reference from Starling.Js just for the name.
    private static readonly ActivitySource RuntimeActivitySource = new("Starling.Engine");

    public JsRealm Realm { get; }
    public JsObject Global => Realm.GlobalObject;
    internal JsRuntimeDiagnostics Diagnostics { get; } = new();

    /// <summary>When true, top-level uncaught throws are swallowed and logged
    /// rather than propagated to the host. Path-A behavior so the engine can
    /// run inline scripts that reference missing globals without aborting the
    /// page render.</summary>
    public bool IgnoreUncaught { get; set; }

    /// <summary>Cancellation token observed by the VM dispatch loop so a host's
    /// Stop signal can interrupt a long-running synchronous script
    /// (<c>while(true)</c> hangs, expensive layouts driven from JS, etc.) without
    /// waiting for it to return. Checked at a low frequency (every few thousand
    /// bytecode steps) so the overhead stays under one branch per opcode on the
    /// hot path. The VM throws <see cref="OperationCanceledException"/> when
    /// cancellation is observed; the engine's navigation catch unwraps it.</summary>
    public CancellationToken AbortToken { get; set; }

    /// <summary>Compatibility wrapper for <c>console.*</c> output. Prefer
    /// <see cref="JsRealm.ConsoleSink"/> for new host integrations.</summary>
    public Action<string, string> ConsoleSink
    {
        get => (level, message) => Realm.ConsoleSink(ParseLevel(level), message);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            Realm.ConsoleSink = (level, message) => value(LevelName(level), message);
        }
    }

    public JsRuntime()
    {
        // Realm + intrinsic install is the heavyweight part of JS startup —
        // every realm constructed (per page, per worker) walks the same
        // installer list. A single span makes the cost visible alongside
        // engine.fetch_html / engine.parse_html in the trace timeline.
        using var realmActivity = RuntimeActivitySource.StartActivity(
            "js.realm.init", ActivityKind.Internal);
        Realm = new JsRealm();
        // wp:M3-83 — back-reference so host re-entry into THIS realm (e.g. a
        // foreign $262.createRealm() realm whose eval/functions are invoked from
        // the host realm's VM) can recover this realm's own primary VM and
        // publish it as the running execution context. See JsRealm.OwnerRuntime.
        Realm.OwnerRuntime = this;
        ObjectCtor.Install(Realm);
        FunctionCtor.Install(Realm); // B2-2 — single-line region so the B3-4 merge stays trivial.
        ArrayCtor.Install(Realm);    // B2-4
        ErrorCtor.Install(Realm);
        StringCtor.Install(Realm);
        NumberCtor.Install(Realm);
        BooleanCtor.Install(Realm);
        SymbolCtor.Install(Realm);
        IteratorIntrinsics.Install(Realm); // B3-2 — depends on SymbolCtor for @@iterator.
        GeneratorIntrinsics.Install(Realm); // B1b-2c — depends on IteratorIntrinsics for %IteratorPrototype%.
        ((JsGlobalObject)Realm.GlobalObject).ReserveLazyGlobal("Map", () => MapCtor.Install(Realm));            // B3-3 — depends on B3-2 iterator protocol (lazy).
        ((JsGlobalObject)Realm.GlobalObject).ReserveLazyGlobal("Set", () => SetCtor.Install(Realm));
        ((JsGlobalObject)Realm.GlobalObject).ReserveLazyGlobal("WeakMap", () => WeakMapCtor.Install(Realm));
        ((JsGlobalObject)Realm.GlobalObject).ReserveLazyGlobal("WeakSet", () => WeakSetCtor.Install(Realm));
        ((JsGlobalObject)Realm.GlobalObject).ReserveLazyGlobal("WeakRef", () => WeakRefCtor.Install(Realm));                          // B4-6 (lazy)
        ((JsGlobalObject)Realm.GlobalObject).ReserveLazyGlobal("FinalizationRegistry", () => FinalizationRegistryCtor.Install(Realm, this)); // B4-6 — installer captures the runtime (lazy).
        Globals.Install(Realm);
        ((JsGlobalObject)Realm.GlobalObject).ReserveLazyGlobal("Math", () => MathObj.Install(Realm));
        ((JsGlobalObject)Realm.GlobalObject).ReserveLazyGlobal("JSON", () => JsonObj.Install(Realm));
        // ArrayBuffer / DataView / every typed array share ONE materialize thunk
        // (typed arrays need ArrayBuffer; DataView views a buffer). Reserving
        // every cluster name against the same delegate means touching any one
        // installs all three groups, and the cluster-sibling sweep in
        // JsGlobalObject.Materialize clears the rest from the registry.
        {
            var g = (JsGlobalObject)Realm.GlobalObject;
            Action installBuffers = () =>
            {
                ArrayBufferCtor.Install(Realm);
                DataViewCtor.Install(Realm);
                TypedArrayCtors.Install(Realm);
            };
            g.ReserveLazyGlobal("ArrayBuffer", installBuffers);
            g.ReserveLazyGlobal("DataView", installBuffers);
            g.ReserveLazyGlobal("Int8Array", installBuffers);
            g.ReserveLazyGlobal("Uint8Array", installBuffers);
            g.ReserveLazyGlobal("Uint8ClampedArray", installBuffers);
            g.ReserveLazyGlobal("Int16Array", installBuffers);
            g.ReserveLazyGlobal("Uint16Array", installBuffers);
            g.ReserveLazyGlobal("Int32Array", installBuffers);
            g.ReserveLazyGlobal("Uint32Array", installBuffers);
            g.ReserveLazyGlobal("Float32Array", installBuffers);
            g.ReserveLazyGlobal("Float64Array", installBuffers);
            g.ReserveLazyGlobal("BigInt64Array", installBuffers);
            g.ReserveLazyGlobal("BigUint64Array", installBuffers);
        }
        ConsoleObj.Install(Realm);
        PromiseCtor.Install(Realm); // B3-4 — depends on Object/Function/Error protos.
        RegExpCtor.Install(Realm);  // B4-1 — depends on Function/Error/Array protos.
        ((JsGlobalObject)Realm.GlobalObject).ReserveLazyGlobal("Date", () => DateCtor.Install(Realm)); // B4-2 — depends on Function.prototype only (lazy).
        ((JsGlobalObject)Realm.GlobalObject).ReserveLazyGlobal("Intl", () => IntlObj.Install(Realm)); // Intl-lite bundle compatibility surface (lazy).
        BigIntCtor.Install(Realm);  // B4-3 — depends on Function.prototype only.
        ((JsGlobalObject)Realm.GlobalObject).ReserveLazyGlobal("Proxy", () => ProxyCtor.Install(Realm));   // B4-4 — depends on Function.prototype only (lazy).
        ((JsGlobalObject)Realm.GlobalObject).ReserveLazyGlobal("Reflect", () => ReflectObj.Install(Realm)); // B4-4 — depends on Symbol (for @@toStringTag) (lazy).
        Global.Set("globalThis", JsValue.Object(Global));
        Global.Set("undefined", JsValue.Undefined);
        Global.Set("NaN", JsValue.NaN);
        Global.Set("Infinity", JsValue.Number(double.PositiveInfinity));

        // Route uncaught microtask exceptions through the console sink so
        // they don't crash the embedder. The full unhandledrejection event
        // pipe lands with B5-1.
        Realm.Microtasks.UncaughtHandler = ex =>
            Realm.ConsoleSink(ConsoleLevel.Error, $"Uncaught (in promise) {ex.Message}");

        // HostPromiseRejectionTracker (§27.2.1.9 / HTML §8.1.7.4): queue
        // rejections that have no handler; a late .then/.catch retracts the
        // entry; whatever is left when a microtask drain settles is reported
        // through the console sink. Without this, a page whose boot chain
        // dies inside a promise callback fails completely silently.
        Realm.OnUnhandledRejection = p => _pendingUnhandledRejections.Add(p);
        Realm.OnRejectionHandled = p => _pendingUnhandledRejections.Remove(p);
    }

    /// <summary>Rejected-without-handler promises awaiting the end-of-drain
    /// report. Entries retract on a late handler attach (the common
    /// reject-then-catch-in-the-same-turn pattern never reports).</summary>
    private readonly List<JsPromise> _pendingUnhandledRejections = new();

    /// <summary>Report (then forget) every still-unhandled rejected promise.
    /// Mirrors the browser behaviour of firing <c>unhandledrejection</c> at
    /// the end of the event-loop turn: the report includes the rejection
    /// value's <c>stack</c> when one exists, else its string form.</summary>
    private void ReportUnhandledRejections()
    {
        if (_pendingUnhandledRejections.Count == 0)
        {
            return;
        }
        // Swap-out before reporting: the sink may run JS-adjacent code; new
        // rejections recorded while reporting belong to the next flush.
        var batch = _pendingUnhandledRejections.ToArray();
        _pendingUnhandledRejections.Clear();
        foreach (var promise in batch)
        {
            if (promise.State != PromiseState.Rejected || promise.IsHandled)
            {
                continue;
            }

            Realm.ConsoleSink(ConsoleLevel.Error,
                $"Uncaught (in promise) {DescribeRejectionReason(promise.Result)}");
        }
    }

    /// <summary>Drain any queued microtasks against the realm. Called
    /// automatically at the bottom of every top-level
    /// <see cref="JsVm.Run(Starling.Js.Bytecode.Chunk)"/>; embedders running
    /// scripts via a custom path can invoke this explicitly. No-op when a
    /// host scheduler is installed — the host pumps its own loop.</summary>
    /// <remarks>
    /// B4-6: after the drain loop empties, we run a FinalizationRegistry
    /// cleanup pass against every live registry — collected targets schedule
    /// their cleanup callbacks via the microtask queue. We loop on the drain
    /// so callbacks enqueued by the cleanup pass also fire. Finally, the
    /// realm's WeakRef "kept alive" set is cleared so subsequent turns can
    /// observe target reclamation.
    /// </remarks>
    public void DrainMicrotasks()
    {
        Realm.Microtasks.DrainAll();
        // B4-6: cleanup pass + secondary drain to flush callbacks the pass
        // enqueued. Bounded — only run a cleanup pass once per outer drain
        // because per-turn cadence is sufficient for any observable contract.
        RunFinalizationCleanupPass();
        Realm.Microtasks.DrainAll();
        Realm.KeptAlive.Clear();
        ReportUnhandledRejections();
    }

    /// <summary>Best human-readable form of a rejection value: the error's
    /// <c>stack</c> when present (attached during throw unwinding), else
    /// "name: message" for error-shaped objects (a constructed-but-never-thrown
    /// Error has no stack), else the value's string form.</summary>
    private static string DescribeRejectionReason(JsValue reason)
    {
        if (reason.IsObject)
        {
            var obj = reason.AsObject;
            if (obj.Get("stack") is { IsString: true } stack)
            {
                return stack.AsString;
            }

            var name = obj.Get("name");
            var message = obj.Get("message");
            if (name.IsString || message.IsString)
            {
                var n = name.IsUndefined ? "Error" : JsValue.ToStringValue(name);
                var m = message.IsUndefined ? "" : JsValue.ToStringValue(message);
                return m.Length == 0 ? n : $"{n}: {m}";
            }
        }
        return JsValue.ToStringValue(reason);
    }

    /// <summary>Walk every live <see cref="JsFinalizationRegistry"/> attached
    /// to the realm and schedule cleanup callbacks for collected targets.
    /// Prunes dead weak handles from <see cref="JsRealm.FinalizationRegistries"/>
    /// in the same pass so the list doesn't grow without bound.</summary>
    private void RunFinalizationCleanupPass()
    {
        var registries = Realm.FinalizationRegistries;
        if (registries.Count == 0)
        {
            return;
        }

        for (var i = registries.Count - 1; i >= 0; i--)
        {
            if (!registries[i].TryGetTarget(out var fr))
            {
                registries.RemoveAt(i);
                continue;
            }
            fr.RunCleanupPass();
        }
    }

    /// <summary>Lazily-allocated primary VM used by <see cref="WithActiveVm(Action)"/>
    /// when there's no script-driven VM on the stack. Held privately so it
    /// stays an implementation detail of the helper.</summary>
    private JsVm? _primaryVm;

    private JsVm GetOrCreatePrimaryVm() => _primaryVm ??= new JsVm(this);

    /// <summary>wp:M3-83 — establish a running execution context for this
    /// realm if none is active, run <paramref name="body"/>, and return its
    /// result. When <see cref="JsRealm.ActiveVm"/> is already set (the realm is
    /// mid-execution) it is reused as-is; otherwise the realm's lazily-created
    /// primary VM is published as <see cref="JsRealm.ActiveVm"/> for the
    /// duration and restored on exit (even when the body throws). Used by the
    /// global <c>eval</c> path so a <em>foreign</em> realm's <c>eval</c>, invoked
    /// from the host realm's VM, has its own realm's context to run against
    /// (cross-realm execution, §9.6 / §19.2.1).</summary>
    internal JsValue WithActiveVm(Func<JsVm, JsValue> body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var previous = Realm.ActiveVm;
        var vm = previous ?? GetOrCreatePrimaryVm();
        Realm.ActiveVm = vm;
        try { return body(vm); }
        finally { Realm.ActiveVm = previous; }
    }

    /// <summary>
    /// Run <paramref name="body"/> with <see cref="JsRealm.ActiveVm"/> set, then
    /// drain the microtask queue. Use when re-entering JS from host code
    /// (timers, fetch completions, event dispatch) where there's no enclosing
    /// <see cref="JsVm.Run(Starling.Js.Bytecode.Chunk)"/> on the stack to
    /// publish the VM and drain reactions. Restores the previous
    /// <see cref="JsRealm.ActiveVm"/> on exit, even when the body throws.
    /// </summary>
    public void WithActiveVm(Action body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var previous = Realm.ActiveVm;
        Realm.ActiveVm = previous ?? GetOrCreatePrimaryVm();
        try
        {
            body();
            DrainMicrotasks();
        }
        finally
        {
            Realm.ActiveVm = previous;
        }
    }

    /// <summary>Register a host-side function as a global named <paramref name="name"/>.
    /// Uses the realm-aware <see cref="JsNativeFunction"/> ctor so the function
    /// inherits from <c>Function.prototype</c> (call/apply/bind chain). The
    /// host doesn't tell us the declared arity, so <c>length</c> is set to the
    /// ECMAScript-default of <c>0</c>; callers needing a specific arity should
    /// either redefine the property or use the full (thisValue, args)
    /// overload paired with a manual <c>length</c> stamp.</summary>
    public void RegisterGlobal(string name, Func<JsValue[], JsValue> body)
        => Global.Set(name, JsValue.Object(new JsNativeFunction(Realm, name, length: 0, (_, a) => body(a), isConstructor: false)));

    /// <summary>Register a host-side function with full (thisValue, args) signature.
    /// As with the simpler overload, <c>length</c> defaults to <c>0</c>; the
    /// caller can override by editing the function's own <c>length</c>
    /// property after registration.</summary>
    public void RegisterGlobal(string name, Func<JsValue, JsValue[], JsValue> body)
        => Global.Set(name, JsValue.Object(new JsNativeFunction(Realm, name, length: 0, body, isConstructor: false)));

    /// <summary>Set or replace a global value (variable or object).</summary>
    public void SetGlobal(string name, JsValue value) => Global.Set(name, value);

    /// <summary>Bridge a host's microtask scheduler (e.g. the renderer's
    /// <c>WebEventLoop</c>) into the realm. Pass <c>null</c> to revert to the
    /// internal drain. Thin convenience wrapper around
    /// <see cref="MicrotaskQueue.SetHostScheduler"/> so hosts don't need to
    /// reach through the realm.</summary>
    public void SetMicrotaskScheduler(Action<Action>? scheduler)
        => Realm.Microtasks.SetHostScheduler(scheduler);

    /// <summary>Look up a global. Returns Undefined if absent.</summary>
    public JsValue GetGlobal(string name) => Global.Get(name);

    private static string LevelName(ConsoleLevel level) => level switch
    {
        ConsoleLevel.Log => "log",
        ConsoleLevel.Info => "info",
        ConsoleLevel.Warn => "warn",
        ConsoleLevel.Error => "error",
        ConsoleLevel.Debug => "debug",
        ConsoleLevel.Dir => "dir",
        ConsoleLevel.Table => "table",
        ConsoleLevel.Trace => "trace",
        _ => "log",
    };

    private static ConsoleLevel ParseLevel(string level) => level switch
    {
        "info" => ConsoleLevel.Info,
        "warn" => ConsoleLevel.Warn,
        "error" => ConsoleLevel.Error,
        "debug" => ConsoleLevel.Debug,
        "dir" => ConsoleLevel.Dir,
        "table" => ConsoleLevel.Table,
        "trace" => ConsoleLevel.Trace,
        _ => ConsoleLevel.Log,
    };
}

internal sealed class JsRuntimeDiagnostics
{
    private int _continuationThreadStarts;

    internal int ContinuationThreadStarts => Volatile.Read(ref _continuationThreadStarts);

    internal void RecordContinuationThreadStart()
        => Interlocked.Increment(ref _continuationThreadStarts);
}
