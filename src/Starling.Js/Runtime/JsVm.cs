using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Js.Bytecode;
using Starling.Js.Intrinsics;
using Starling.RegExp;

namespace Starling.Js.Runtime;

/// <summary>
/// Stack-machine VM. Executes a <see cref="Chunk"/> against a
/// <see cref="JsRuntime"/> and returns the last-evaluated value.
/// </summary>
/// <remarks>
/// <para>
/// Executes user-defined functions, native host functions, constructors,
/// generators, async functions, modules, direct eval, and the core control
/// flow opcodes emitted by <see cref="JsCompiler"/>.
/// </para>
/// <para>
/// Throws <see cref="JsThrow"/> for uncaught script-level throws; the host
/// wraps it appropriately. Stack overflows surface as
/// <see cref="StackOverflowException"/>.
/// </para>
/// </remarks>
public sealed class JsVm
{
    private readonly JsRuntime _runtime;
    private readonly ILogger _log;
    /// <summary>Hard logical ceiling on one frame's operand stack. Reaching it
    /// throws the same "JS stack overflow" as the old fixed-size rent.</summary>
    private const int MaxStack = 1024;

    /// <summary>wp:M3-84 follow-up — initial operand-stack rent per frame.
    /// Frames used to rent <see cref="MaxStack"/> slots up front, which at
    /// <see cref="MaxFrameDepth"/> meant hundreds of MB of transient arrays
    /// and constant pool misses (the pool cannot hold 10k arrays of one
    /// size). Most frames touch only a handful of slots, so rent small and
    /// grow by doubling on demand (see <see cref="GrowStack"/>). 32 keeps
    /// typical expression nesting and call-argument marshaling in the first
    /// rent while costing ~1/32 of the old per-frame footprint.</summary>
    private const int InitialStackSlots = 32;

    /// <summary>wp:M3-84 Stage B — logical cap on the JS frame chain. A JS→JS
    /// call pushes a heap <see cref="CallFrame"/> (no native recursion), so
    /// this cap costs no native stack and an over-deep recursion surfaces as a
    /// catchable <c>RangeError</c>. The spec leaves the limit
    /// implementation-defined as long as recursion fails with a RangeError.</summary>
    private const int MaxFrameDepth = 10_000;

    /// <summary>wp:M3-84 Stage B — cap on nested native→JS re-entries
    /// (getters/setters, ToPrimitive, iterator next, Proxy traps, super-ctor
    /// through non-plain callees, cross-realm). Each barrier recurses on the
    /// native C# stack, so this cap pairs with
    /// <c>RuntimeHelpers.TryEnsureSufficientExecutionStack()</c> to surface a
    /// catchable RangeError before the thread stack overflows.</summary>
    private const int MaxBarrierDepth = 1_000;

    /// <summary>Head of this thread's JS frame chain. Thread-static is
    /// mandatory: other threads run VMs concurrently (the WASM invocation
    /// thread, tests) and must not share one chain. Multiple VMs on one thread
    /// interleave safely — every barrier saves and restores the head.</summary>
    [ThreadStatic] private static CallFrame? t_current;

    /// <summary>Number of live JS frames on this thread's chain (trampolined
    /// frames and barrier frames both count).</summary>
    [ThreadStatic] private static int t_frameDepth;

    /// <summary>Number of nested native→JS barrier entries on this thread.</summary>
    [ThreadStatic] private static int t_barrierDepth;

    // Exact-size pool of call-argument arrays. A JS call previously allocated a
    // fresh JsValue[argc] on every invocation — the hottest allocation in the
    // interpreter. args.Length is load-bearing (it is the JS argument count), so
    // this hands back arrays of the EXACT requested length, unlike ArrayPool's
    // ">= size" sizing. Rent and Return are balanced on one thread within a
    // single synchronous call; generator/async bodies clone their args because
    // those values may be held by a suspended continuation frame.
    // Arrays are cleared on return so the pool never pins JS objects.
    [ThreadStatic] private static Stack<JsValue[]>?[]? t_argPools;
    private const int MaxPooledArgc = 8;
    private const int ArgPoolDepth = 64;

    private static JsValue[] RentArgs(int n)
    {
        if (n == 0)
        {
            return System.Array.Empty<JsValue>();
        }

        if (n > MaxPooledArgc)
        {
            return new JsValue[n];
        }

        var pools = t_argPools ??= new Stack<JsValue[]>?[MaxPooledArgc + 1];
        var pool = pools[n];
        return pool is { Count: > 0 } ? pool.Pop() : new JsValue[n];
    }

    private static void ReturnArgs(JsValue[] args)
    {
        var n = args.Length;
        if (n == 0 || n > MaxPooledArgc)
        {
            return;
        }

        System.Array.Clear(args, 0, n);
        var pools = t_argPools ??= new Stack<JsValue[]>?[MaxPooledArgc + 1];
        var pool = pools[n] ??= new Stack<JsValue[]>();
        if (pool.Count < ArgPoolDepth)
        {
            pool.Push(args);
        }
    }

    private static string NullishLabel(JsValue value) => value.IsNull ? "null" : "undefined";

    /// <summary>The prototype that hosts the methods of a primitive value, or
    /// null if not a primitive with a wrapper prototype. Used to resolve a
    /// property on a primitive without allocating a wrapper object.</summary>
    private JsObject? PrimitivePrototype(JsValue v)
    {
        var r = _runtime.Realm;
        if (v.IsString)
        {
            return r.StringPrototype;
        }

        if (v.IsNumber)
        {
            return r.NumberPrototype;
        }

        if (v.IsBoolean)
        {
            return r.BooleanPrototype;
        }

        if (v.IsSymbol)
        {
            return r.SymbolPrototype;
        }

        if (v.IsBigInt)
        {
            return r.BigIntPrototype;
        }

        return null;
    }

    public JsVm(JsRuntime runtime, ILogger<JsVm>? log = null)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _log = log ?? NullLogger<JsVm>.Instance;
    }

    /// <summary>Per-opcode dispatch counts for this VM. Indexed by
    /// <see cref="Opcode"/> byte value. Incremented once per dispatched
    /// instruction inside <see cref="Dispatch"/>.</summary>
    public readonly long[] OpcodeCounts = new long[256];

    /// <summary>The realm this VM dispatches against.</summary>
    public JsRealm Realm => _runtime.Realm;

    /// <summary>The runtime that owns this VM (host bindings, console sink).</summary>
    public JsRuntime Runtime => _runtime;

    /// <summary>Run a chunk to completion. Returns the topmost value at Halt,
    /// or Undefined if the stack was empty.</summary>
    /// <remarks>
    /// Drains the realm's microtask queue (Promise reactions, thenable
    /// adoption jobs) before returning, matching what an HTML embedder
    /// would do at the bottom of a top-level task. Nested invocations
    /// (function call → JS body) go through <see cref="CallFunction"/> /
    /// <see cref="ConstructFunction"/> and intentionally do NOT drain — the
    /// drain belongs to the outermost frame only. When the host installs a
    /// microtask scheduler via <see cref="JsRuntime.SetMicrotaskScheduler"/>,
    /// the drain is a no-op (the host loop owns pumping).
    /// </remarks>
    public JsValue Run(Chunk chunk) =>
        // §16.1.7 / §9.4.1: a classic script's top-level `this` is the global
        // object (modules — evaluated via CallFunction, not this entry — keep
        // `this` = undefined). Real-world UMD wrappers do `}(this, factory)` and
        // read `this._` / `this.Backbone`, so top-level `this` MUST be global.
        RunBarrier(chunk, args: [], thisValue: JsValue.Object(_runtime.Realm.GlobalObject),
            upvalues: Array.Empty<JsValue>(), drainMicrotasks: true,
            currentFunction: null, newTarget: null);

    /// <summary>Re-entrant evaluation entry for the <c>eval</c> builtin and the
    /// <c>Function</c> constructor (§19.2.1 / §20.2.1). Runs a freshly compiled
    /// global-scope chunk against the current realm with <c>this</c> = the global
    /// object, WITHOUT draining microtasks (the outermost frame owns the drain).
    /// Returns the completion value the chunk left on the stack.</summary>
    public JsValue RunEval(Chunk chunk) =>
        RunBarrier(chunk, args: Array.Empty<JsValue>(),
            thisValue: JsValue.Object(_runtime.Realm.GlobalObject),
            upvalues: Array.Empty<JsValue>(), drainMicrotasks: false,
            currentFunction: null, newTarget: null);

    /// <summary>wp:M3-72 — run direct-eval'd code with <paramref name="evalScope"/>
    /// (the calling frame's live variable environment) installed so the eval
    /// body's caller-scope-aware opcodes resolve free identifiers to the
    /// caller's live bindings. <c>this</c> / <c>new.target</c> / the
    /// caller-as-currentFunction (for <c>super</c>) are inherited so the eval'd
    /// code's this / new.target / super resolve as in §19.2.1.1. Does not drain
    /// microtasks (the outermost frame owns the drain).</summary>
    internal JsValue RunDirectEval(Chunk chunk, EvalScope evalScope, JsFunction? caller,
        JsValue callerThis, JsObject? callerNewTarget, EvalVarStore? callerVarStore = null) =>
        RunBarrier(chunk, args: Array.Empty<JsValue>(),
            thisValue: callerThis,
            upvalues: Array.Empty<JsValue>(), drainMicrotasks: false,
            currentFunction: caller, newTarget: callerNewTarget,
            suspension: null, evalScope: evalScope,
            // wp:M3-73 — thread the caller frame's eval-introduced var store so
            // the eval body's DeclareEvalVar/StoreEvalVar write into it and its
            // own reads/writes of those names resolve through it.
            frameVarStore: callerVarStore);

    /// <summary>wp:M3-71/72 — §19.2.1.1 PerformEval (direct path). Parse + compile
    /// <paramref name="args"/>[0] inheriting the CALLER's lexical context, then
    /// run the resulting chunk on the caller's function so a SuperProperty
    /// resolves via <paramref name="caller"/>'s <c>[[HomeObject]]</c>,
    /// <c>new.target</c> sees <paramref name="callerNewTarget"/>, and <c>this</c>
    /// is <paramref name="callerThis"/>. A non-string argument is returned unchanged
    /// (§19.2.1 step 2).
    /// <para>wp:M3-72 — <paramref name="callerScope"/> is the caller frame's live
    /// variable environment (built from the call site's
    /// <see cref="Bytecode.EvalScopeDescriptor"/> + the live locals/upvalues). The
    /// eval body's free identifiers that name a caller binding read/write that
    /// live binding, and the §19.2.1.3 EvalDeclarationInstantiation early-error
    /// checks run against the caller's lexical bindings.</para>
    /// <para>wp:M3-73 — a NON-strict direct eval whose caller is a function injects
    /// the eval body's OWN top-level var/function declarations into the caller's
    /// variable environment so they persist after eval returns (§19.2.1.3
    /// non-global branch). Those new bindings live in the caller frame's
    /// <see cref="EvalVarStore"/> (<paramref name="callerVarStore"/>, created
    /// lazily here and propagated back to the caller frame via <c>ref</c>); a
    /// top-level var/function whose name IS an existing caller binding updates
    /// that live binding instead. A STRICT direct eval keeps its own
    /// var-environment (no injection — its top-level var/function bind globally),
    /// and a script-top (non-function) caller keeps the existing global-injection
    /// path.</para></summary>
    private JsValue PerformDirectEval(JsValue[] args, EvalScope callerScope,
        JsFunction? caller, JsValue callerThis, JsObject? callerNewTarget, bool callerStrict,
        bool inInitializer, ref EvalVarStore? callerVarStore)
    {
        var x = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (!x.IsString)
        {
            return x;
        }

        // §19.2.1.1 steps 4-6: derive the early-error context from the caller.
        // A [[HomeObject]] ⇒ the caller is a method (inMethod). A class
        // constructor (HomeObject + [[Construct]]) ⇒ inConstructor (derived-ctor
        // gating for SuperCall). new.target is available iff the caller is
        // function code (inFunction); arrow callers inherit their enclosing
        // function's, so a caller at all (non-null) means inFunction.
        var inFunction = caller is not null;
        var inMethod = caller?.HomeObject is not null;
        var inDerivedCtor = caller is { ConstructorKind: ClassConstructorKind.Derived };

        Chunk chunk;
        bool injectVars;
        try
        {
            var ctx = new Parse.JsParser.DirectEvalContext(
                CallerStrict: callerStrict, InFunction: inFunction,
                InMethod: inMethod, InDerivedConstructor: inDerivedCtor);
            var program = new Parse.JsParser(x.AsString).ParseProgram(ctx);

            // wp:M3-81 — §sec-performeval-rules-in-initializer Additional Early
            // Error Rules for Eval Inside Initializer: when this direct eval occurs
            // inside a class field/static initializer or a (non-arrow) function
            // parameter default, "ScriptBody : StatementList — It is a Syntax Error
            // if ContainsArguments of StatementList is true." Throw before the body
            // runs (a pre-execution early error), so no side effect is observed.
            if (inInitializer && Bytecode.CaptureAnalysis.ContainsArgumentsInEvalBody(program.Body))
            {
                throw new JsThrow(_runtime.Realm.NewSyntaxError(
                    "'arguments' is not allowed in an eval call inside an initializer"));
            }

            var evalIsStrict = callerStrict || program.Strict;

            // §19.2.1.3 EvalDeclarationInstantiation — a NON-strict direct eval
            // hoists its top-level var/function names into the caller's variable
            // environment, and must not hoist over a like-named LEXICAL binding
            // (let/const/class) anywhere in the caller's environment chain. A
            // strict direct eval gets its own fresh variable environment, so no
            // collision is possible. The caller-scope entries carry the caller's
            // lexical names flagged IsLexical.
            if (!evalIsStrict)
            {
                foreach (var vn in Parse.JsParser.EvalVarDeclaredNames(program))
                {
                    if (callerScope.TryGet(vn, out var entry) && entry.IsLexical)
                    {
                        throw new JsThrow(_runtime.Realm.NewSyntaxError(
                            "Identifier '" + vn + "' has already been declared"));
                    }
                }
            }

            // wp:M3-73 — inject the eval body's own top-level var/function
            // declarations into the CALLER's var-environment iff the eval is
            // non-strict AND the caller is a function (a function var-env, not the
            // global one). A strict eval keeps its own var-env; a script-top
            // caller keeps the existing global-injection behaviour.
            injectVars = !evalIsStrict && inFunction;

            var callerNames = new HashSet<string>(callerScope.Names, StringComparer.Ordinal);
            // §19.2.1.1 — thread the caller's PrivateEnvironment so eval'd code
            // resolves private names (`this.#m`) against the enclosing class.
            chunk = JsCompiler.CompileForDirectEval(program, "<eval>", callerNames, injectVars,
                caller?.Body.PrivateNameScope);
        }
        catch (Parse.JsParseException ex)
        {
            throw new JsThrow(_runtime.Realm.NewSyntaxError(ex.Message));
        }

        // wp:M3-73 — when injecting, ensure the caller frame has a var store and
        // run the eval body against it so its DeclareEvalVar/StoreEvalVar land in
        // the caller's environment and its own reads/writes resolve there.
        if (injectVars)
        {
            callerVarStore ??= new EvalVarStore();
        }

        // Run on the caller's function (so super / this / new.target resolve)
        // with the caller scope installed so free identifiers reach the caller's
        // live bindings, and the caller's eval-introduced var store threaded in.
        return RunDirectEval(chunk, callerScope, caller, callerThis, callerNewTarget,
            callerVarStore);
    }

    /// <summary>Invoke a JS function with an explicit <c>this</c> and args.
    /// Used by <see cref="AbstractOperations.Call"/>. B1b-2c: when the
    /// callee is async / generator / async-generator, this entry instead
    /// returns the corresponding wrapper without running the body —
    /// dispatch lands in <see cref="StartGeneratorBody"/> /
    /// <see cref="StartAsyncBody"/>.</summary>
    public JsValue CallFunction(JsFunction fn, JsValue thisValue, JsValue[] args)
    {
        // wp:M3-83 — §9.3.1 cross-realm execution (see CallFunctionForeignRealm).
        if (fn.Realm is { } fnRealm && !ReferenceEquals(fnRealm, _runtime.Realm)
            && fnRealm.OwnerRuntime is { } owner)
        {
            return CallFunctionForeignRealm(owner, fn, thisValue, args);
        }

        return CallFunctionLocal(fn, thisValue, args);
    }

    /// <summary>wp:M3-83 — §9.3.1 cross-realm execution. The function was
    /// created in a DIFFERENT realm than the one this VM runs (the
    /// $262.createRealm case: a foreign realm's function invoked from the host
    /// realm's VM), so dispatch through that realm's own VM with its realm
    /// published as the running execution context. PrepareForOrdinaryCall
    /// pushes the callee's [[Realm]] as the current realm; doing so makes the
    /// body resolve globals, allocate intrinsics, and throw errors in the
    /// function's realm. Kept out of <see cref="CallFunction"/> so the
    /// lambda's closure is allocated only on this cold path — inlined, the
    /// capture forces a display-class allocation on every call (37 MB of
    /// churn on an x.com load).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue CallFunctionForeignRealm(JsRuntime owner, JsFunction fn, JsValue thisValue, JsValue[] args)
        => owner.WithActiveVm(foreignVm =>
            ReferenceEquals(foreignVm, this) ? CallFunctionLocal(fn, thisValue, args)
                                             : foreignVm.CallFunction(fn, thisValue, args));

    private JsValue CallFunctionLocal(JsFunction fn, JsValue thisValue, JsValue[] args)
    {
        // §10.2.1.2 OrdinaryCallBindThis: a SLOPPY function called with a nullish
        // `this` binds the global object, not undefined. This makes the
        // ubiquitous global-detection idiom `(function(){return this})()` and
        // legacy libs (jQuery/Backbone/YUI feature-detection) work. A STRICT
        // function leaves `this` as the passed value (undefined/null). Arrows
        // capture `this` lexically and ignore this argument; class constructors
        // only run via [[Construct]] with a real `this`, so neither is affected.
        if (thisValue.IsNullish && fn.ConstructorKind == ClassConstructorKind.None
            && !fn.Body.IsStrict)
        {
            thisValue = JsValue.Object(_runtime.Realm.GlobalObject);
        }

        if (fn.Kind == JsFunctionKind.Generator)
        {
            return StartGeneratorBody(fn, thisValue, args);
        }

        if (fn.Kind == JsFunctionKind.Async)
        {
            return StartAsyncBody(fn, thisValue, args);
        }

        if (fn.Kind == JsFunctionKind.AsyncGenerator)
        {
            return StartAsyncGeneratorBody(fn, thisValue, args);
        }

        return RunBarrier(fn.Body, args, thisValue, fn.Upvalues, drainMicrotasks: false,
               currentFunction: fn, newTarget: null,
               // wp:M3-73 — inherit the eval-introduced var store this closure
               // captured at creation so its free identifiers resolve through the
               // vars a direct eval injected into the enclosing var-environment.
               frameVarStore: fn.CapturedEvalVarStore);
    }

    /// <summary>Construct a JS function (spec [[Construct]] for ordinary
    /// functions): allocate a fresh ordinary object inheriting from the
    /// constructor's <c>prototype</c> property, run the body with <c>this</c>
    /// bound to it, and return whichever object the body produced.</summary>
    public JsValue ConstructFunction(JsFunction fn, JsValue[] args, JsObject newTarget)
    {
        // wp:M3-83 — §9.3.2 cross-realm construct (see ConstructFunctionForeignRealm).
        if (fn.Realm is { } fnRealm && !ReferenceEquals(fnRealm, _runtime.Realm)
            && fnRealm.OwnerRuntime is { } owner)
        {
            return ConstructFunctionForeignRealm(owner, fn, args, newTarget);
        }

        return ConstructFunctionLocal(fn, args, newTarget);
    }

    /// <summary>wp:M3-83 — §9.3.2 cross-realm construct. Mirror
    /// <see cref="CallFunctionForeignRealm"/>: a foreign realm's constructor
    /// (a class produced by another realm's eval) must run with its realm
    /// active so its <c>this</c> instance, prototype, and any brand / error it
    /// throws come from the constructor's realm, not the caller's. Kept out of
    /// <see cref="ConstructFunction"/> so the lambda's closure is allocated
    /// only on this cold path.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue ConstructFunctionForeignRealm(JsRuntime owner, JsFunction fn, JsValue[] args, JsObject newTarget)
        => owner.WithActiveVm(foreignVm =>
            ReferenceEquals(foreignVm, this) ? ConstructFunctionLocal(fn, args, newTarget)
                                             : foreignVm.ConstructFunction(fn, args, newTarget));

    private JsValue ConstructFunctionLocal(JsFunction fn, JsValue[] args, JsObject newTarget)
    {
        // wp:M3-84 Stage B — the [[Construct]] return-value coercion (and the
        // derived-class DerivedThis rules) now run at frame pop (see
        // CoerceConstructReturn), keyed off the frame's Disposition. This
        // keeps the barrier path and the trampolined New/CallSuperCtor path
        // on one code path.
        var thisVal = ComputeConstructThis(fn, newTarget);
        return RunBarrier(fn.Body, args, thisVal, fn.Upvalues,
            drainMicrotasks: false, currentFunction: fn, newTarget: newTarget,
            frameVarStore: fn.CapturedEvalVarStore, // wp:M3-73
            disposition: FrameDisposition.Construct);
    }

    /// <summary>The <c>this</c> a [[Construct]] invocation binds at frame
    /// entry. A derived class constructor gets the uninitialized-this sentinel
    /// (§10.2.1.1: <c>this</c> is dead until <c>super(...)</c> runs;
    /// LoadThisChecked throws ReferenceError before then). Anything else gets
    /// a fresh ordinary object via OrdinaryCreateFromConstructor: prototype is
    /// newTarget.prototype if that is an object, else Object.prototype.</summary>
    private JsValue ComputeConstructThis(JsFunction fn, JsObject newTarget)
    {
        if (fn.ConstructorKind == ClassConstructorKind.Derived)
        {
            return JsValue.Object(_runtime.Realm.UninitializedThisSentinel);
        }

        var protoSlot = newTarget.Get("prototype");
        var proto = protoSlot.IsObject ? protoSlot.AsObject : _runtime.Realm.ObjectPrototype;
        return JsValue.Object(_runtime.Realm.NewObjectWithProto(proto));
    }

    /// <summary>DIAG: name of the most recent property/global load, used to
    /// enrich "not a function" errors with the callee identifier.</summary>
    private string? _lastLoadName;

    private static bool IsCallableValue(JsValue v)
        => v.IsObject && v.AsObject is JsNativeFunction or JsFunction or JsBoundFunction or JsProxy;

    /// <summary>wp:M3-72 — build the runtime <see cref="EvalScope"/> for a direct
    /// eval from the compile-time <see cref="Bytecode.EvalScopeDescriptor"/> and
    /// the caller frame's live storage (its <paramref name="locals"/> array and
    /// <paramref name="upvalues"/> cell table). Plain locals are referenced by
    /// (array, slot) so writes are observed by the caller after eval returns;
    /// captured locals/upvalues share the same <see cref="Cell"/> instance.</summary>
    private static EvalScope BuildEvalScope(Bytecode.EvalScopeDescriptor descriptor,
        JsValue[] locals, IReadOnlyList<JsValue> upvalues)
    {
        var entries = new List<EvalScope.Entry>(descriptor.Bindings.Count);
        foreach (var b in descriptor.Bindings)
        {
            switch (b.Kind)
            {
                case Bytecode.EvalScopeDescriptor.Kind.LocalSlot:
                    entries.Add(new EvalScope.Entry
                    {
                        Name = b.Name,
                        Locals = locals,
                        Slot = b.Index,
                        IsLexical = b.IsLexical,
                        IsConst = b.IsConst,
                    });
                    break;
                case Bytecode.EvalScopeDescriptor.Kind.LocalCell:
                    {
                        // A captured local holds a Cell in its slot; share it so
                        // writes from the eval'd code are live for the caller and
                        // any other closures over the same binding.
                        var slotVal = locals[b.Index];
                        if (slotVal.IsObject && slotVal.AsObject is Cell cell)
                        {
                            entries.Add(new EvalScope.Entry { Name = b.Name, Cell = cell, IsLexical = b.IsLexical, IsConst = b.IsConst });
                        }
                        else
                        {
                            entries.Add(new EvalScope.Entry { Name = b.Name, Locals = locals, Slot = b.Index, IsLexical = b.IsLexical, IsConst = b.IsConst });
                        }

                        break;
                    }
                case Bytecode.EvalScopeDescriptor.Kind.Upvalue:
                    {
                        if (b.Index < upvalues.Count && upvalues[b.Index].IsObject
                            && upvalues[b.Index].AsObject is Cell upCell)
                        {
                            entries.Add(new EvalScope.Entry { Name = b.Name, Cell = upCell, IsLexical = b.IsLexical, IsConst = b.IsConst });
                        }

                        break;
                    }
            }
        }
        return new EvalScope(entries);
    }

    /// <summary>
    /// wp:M3-84 Stage B — the native→JS barrier. Every native entry into JS
    /// (host Run/RunEval, AbstractOperations.Call/Construct via
    /// CallFunction/ConstructFunction, direct eval, generator/async resume,
    /// cross-realm dispatch) lands here: it pushes one barrier-marked
    /// <see cref="CallFrame"/> onto the thread's frame chain and runs
    /// <see cref="Dispatch"/> until that frame pops. JS→JS calls inside the
    /// dispatch loop push trampolined frames instead and never recurse
    /// natively — only barrier entries consume native stack, bounded by
    /// <see cref="MaxBarrierDepth"/> plus the execution-stack probe.
    /// </summary>
    private JsValue RunBarrier(Chunk chunk, JsValue[] args, JsValue thisValue,
        IReadOnlyList<JsValue> upvalues, bool drainMicrotasks,
        JsFunction? currentFunction, JsObject? newTarget,
        SuspendedFrame? suspension = null, EvalScope? evalScope = null,
        EvalVarStore? frameVarStore = null,
        FrameDisposition disposition = FrameDisposition.Call)
    {
        // Guard the native stack: each nested barrier recurses through here, so
        // cap the depth and surface a catchable RangeError instead of a fatal
        // StackOverflowException. The logical cap alone is unreliable: native
        // frames-per-barrier vary (calls routed through native intrinsics like
        // String.prototype.replace / Function.prototype.call burn several extra
        // native frames), so TryEnsureSufficientExecutionStack() probes the
        // actual remaining stack as well. The frame-depth cap also applies —
        // a barrier frame is a JS frame on the chain like any other.
        if (t_barrierDepth >= MaxBarrierDepth || t_frameDepth >= MaxFrameDepth ||
            !System.Runtime.CompilerServices.RuntimeHelpers.TryEnsureSufficientExecutionStack())
        {
            throw new JsThrow(_runtime.Realm.NewRangeError("Maximum call stack size exceeded"));
        }

        // Publish this VM on the realm so native intrinsics (JSON.parse
        // reviver, JSON.stringify replacer/toJSON, etc.) can dispatch JS
        // callables. Save/restore in case of reentry from a nested host
        // invocation chain. Trampolined JS→JS calls are same-VM/same-realm by
        // construction, so only barriers touch ActiveVm.
        var prevVm = _runtime.Realm.ActiveVm;
        _runtime.Realm.ActiveVm = this;
        var prevCurrent = t_current;
        var prevFrameDepth = t_frameDepth;
        t_barrierDepth++;
        try
        {
            var frame = CreateFrame(chunk, args, thisValue, upvalues, currentFunction,
                newTarget, suspension, evalScope, frameVarStore);
            frame.Caller = prevCurrent;
            frame.IsBarrier = true;
            frame.Disposition = disposition;
            t_current = frame;
            t_frameDepth++;
            // Coerce OUTSIDE Dispatch's catch: the construct coercion can
            // throw (derived ctor that never ran super), and that throw
            // belongs to this construct site — the completed body's own
            // try/catch must not see it, and the unwinder must not release
            // the popped frame again. Matches the old post-RunInner model.
            // Suspension exits are always Disposition.Call, so coercion is
            // the identity for them.
            var result = CoerceReturn(frame, Dispatch(frame));
            // Restore the chain head before draining: the barrier frame just
            // popped (its pooled arrays are released), so reaction jobs must
            // not see it as the current frame.
            t_current = prevCurrent;
            t_frameDepth = prevFrameDepth;
            // Drain microtasks while ActiveVm still points to this VM so
            // reaction jobs that dispatch JS handlers find a usable VM
            // (AbstractOperations.Call needs one for JsFunction). Only the
            // outermost (top-level Run) frame drains — nested calls do not.
            if (drainMicrotasks)
            {
                _runtime.DrainMicrotasks();
            }

            return result;
        }
        finally
        {
            t_barrierDepth--;
            t_frameDepth = prevFrameDepth;
            t_current = prevCurrent;
            _runtime.Realm.ActiveVm = prevVm;
        }
    }

    /// <summary>Build the heap <see cref="CallFrame"/> for one JS activation —
    /// shared by the barrier entry and the JS→JS trampoline push. NoInlining
    /// keeps its temporaries off the dispatch loop's native frame.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private CallFrame CreateFrame(Chunk chunk, JsValue[] args, JsValue thisValue,
        IReadOnlyList<JsValue> upvalues, JsFunction? currentFunction, JsObject? newTarget,
        SuspendedFrame? suspension, EvalScope? evalScope, EvalVarStore? frameVarStore)
    {
        var restored = suspension?.State;
        if (restored is not null)
        {
            chunk = restored.Chunk;
            upvalues = restored.Upvalues;
            currentFunction = restored.CurrentFunction;
            newTarget = restored.NewTarget;
            evalScope = restored.EvalScope;
            frameVarStore = restored.FrameVarStore;
        }

        // wp:M3-84 Stage A — per-call state lives on one heap CallFrame instead
        // of captured locals. The old nested local functions captured every
        // local into one large closure display, which made each native frame
        // 20-50 KB and tripped the native stack guard at ~26 nested JS calls.
        // Hot fields (Ip/Sp/MaxSp + the stack/code/constants/locals arrays) are
        // cached in C# locals below and flushed back to the frame at suspend
        // points; cold state reads off the frame.
        var frame = new CallFrame
        {
            Chunk = chunk,
            Code = chunk.Code,
            Constants = chunk.Constants,
            // Operand stack rented from the shared array pool. Rented small
            // (InitialStackSlots) and grown by GrowStack when a Push runs out
            // of room, up to the MaxStack logical ceiling. It is returned
            // via Finish at every normal exit; a suspended frame keeps its
            // (possibly grown) stack until completion. MaxSp tracks the
            // high-water slot so Finish clears only the touched region.
            Stack = restored?.Stack ?? ArrayPool<JsValue>.Shared.Rent(InitialStackSlots),
            Sp = restored?.Sp ?? 0,
            MaxSp = restored?.MaxSp ?? 0,
            // Locals rented from the same shared pool as the operand stack
            // (measured ~358 MB/page-load of JsValue[] churn on x.com). The
            // rented array is OVERSIZED: every consumer must bound itself by
            // chunk.LocalCount, never Locals.Length. Returned by ReleaseFrame /
            // the Halt arm (cleared up to LocalCount so the pool never pins JS
            // objects); a suspended frame keeps it until the body completes,
            // and a frame whose locals array escaped (mapped `arguments`,
            // direct-eval scope) skips the return — see LocalsEscaped.
            Locals = restored?.Locals ?? RentLocals(chunk.LocalCount),
            LocalsEscaped = restored?.LocalsEscaped ?? false,
            Upvalues = upvalues,
            Args = args,
            Ip = restored?.Ip ?? 0,
            ThisV = restored?.ThisValue ?? thisValue,
            // ES strict mode — whether this frame's code runs as strict mode
            // code. Drives strict StoreGlobal (assignment to undeclared global
            // throws) and strict Set/delete failures.
            FrameStrict = chunk.IsStrict,
            CurrentFunction = currentFunction,
            NewTarget = newTarget,
            EvalScope = evalScope,
            FrameVarStore = frameVarStore,
            Suspension = suspension,
            // §14.15 try-frame stack — owns the catch/finally targets that the
            // in-loop catch(JsThrow) and the Return opcode handler consult.
            // Lazily created by EnterTry: most functions contain no try/catch,
            // and the unconditional Stack<TryFrame> was ~78 MB of churn on an
            // x.com page load. Every reader treats null as empty.
            TryStack = restored?.TryStack,
            // §14.11 / §9.1.1.2 — object Environment Records installed by the
            // running `with` statements (innermost last). The with-aware opcodes
            // consult it for unqualified name resolution; lazily created so the
            // common (no-`with`) path allocates nothing. §10.2.1 — a function
            // whose body was compiled inside a `with` seeds its frame's
            // with-stack from the records captured at closure-creation time.
            WithStack = restored?.WithStack
                ?? (currentFunction?.CapturedWith is { Count: > 0 } cap
                    ? new List<JsObject>(cap)
                    : null),
            // wp:M3-81 — §sec-performeval-rules-in-initializer initializer
            // depth. A direct eval whose ScriptBody ContainsArguments is an
            // early SyntaxError while this is > 0. Seeded for class field /
            // static-block thunks (chunk.IsInitializer) and for an arrow
            // closure that inherited the initializer context lexically.
            // Parameter default regions toggle it at runtime via the
            // Enter/ExitInitializer opcodes.
            InitDepth = restored?.InitDepth
                ?? ((chunk.IsInitializer
                    || (chunk.IsArrow && currentFunction is { InInitializer: true })) ? 1 : 0),
        };
        if (restored is null)
        {
            // Bound by LocalCount, not Locals.Length: the rented array is
            // oversized, and slots past LocalCount are never read or cleared.
            for (var k = 0; k < args.Length && k < chunk.LocalCount; k++)
            {
                frame.Locals[k] = args[k];
            }

            // wp:M3-73 — a non-strict function whose body/params contain a
            // direct eval eagerly allocates its var store at frame entry so
            // closures it creates (incl. ones created in a parameter
            // initializer BEFORE the eval runs) snapshot the same store and
            // observe the bindings a later direct eval injects into this
            // function's variable environment. Any already-captured parent
            // store (frameVarStore, from JsFunction.CapturedEvalVarStore)
            // becomes this store's lookup parent so a free identifier resolves
            // own-env -> enclosing eval-env -> global.
            if (!frame.FrameStrict && chunk.HasDirectEval)
            {
                frame.FrameVarStore = new EvalVarStore { Parent = frameVarStore };
            }
        }
        return frame;
    }

    /// <summary>wp:M3-84 Stage B — the single dispatch loop. Runs the current
    /// frame's bytecode, switching frames in place on JS→JS call/return (the
    /// trampoline), and exits only when <paramref name="frame"/>'s barrier
    /// frame pops (return value), parks (generator/async suspension), or
    /// throws an unhandled <see cref="JsThrow"/> past the barrier.</summary>
    private JsValue Dispatch(CallFrame frame)
    {
        // Hot-field cache — touched per opcode. ip/sp/maxSp are flushed to the
        // frame before any operation that can suspend or push a callee frame
        // (the snapshot and the unwinder read the frame, not these locals).
        // Reloaded via LoadFrameCache on every frame switch.
        //
        // FRAME MATERIALIZATION INVARIANT: a frame on the chain is only read
        // through its fields at three points — trampoline push (the caller's
        // Ip/Sp/MaxSp are flushed at the push site, so they name the call
        // site), suspend (FlushAndSuspend writes everything the snapshot
        // needs), and the unwinder (which works on the CURRENT frame via
        // these cached locals, and on caller frames via their push-flushed
        // state). Native re-entry mid-opcode (a getter, ToPrimitive, a Proxy
        // trap) does NOT need a flush today because nothing walks the chain
        // while it runs: a barrier handles its own frames and rethrows
        // natively, and this dispatch's catch resumes from the live locals.
        // If a chain-walker is ever added that reads Ip/Sp of frames below a
        // barrier (a debugger, Error-construction stack capture, a sampling
        // profiler), every native re-entry site must flush first — without
        // that, symptoms are wrong stack lines, skipped finally blocks, or
        // corrupt eval scopes.
        var chunk = frame.Chunk;
        var stack = frame.Stack;
        var locals = frame.Locals;
        var code = frame.Code;
        var constants = frame.Constants;
        var ip = frame.Ip;
        var sp = frame.Sp;
        var maxSp = frame.MaxSp;

        // Host-driven abort (Stop button, navigation supersede). Checked at a
        // ~1-in-1024 cadence so the interpreter pays at most one cheap mask + a
        // very-rarely-taken branch per opcode. The token lives on the runtime so
        // every frame and barrier observes the same signal; the throw
        // unwinds through the C# stack and out of Run(), where the engine's
        // navigation catch picks it up. The check sits OUTSIDE the JsThrow
        // try/catch below so cancellation never masquerades as a script throw.
        const int AbortCheckMask = 0x3FF;
        var stepCount = 0;

        while (true)
        {
            if ((stepCount++ & AbortCheckMask) == 0)
            {
                _runtime.AbortToken.ThrowIfCancellationRequested();
            }

            JsThrow? rethrow = null;
            try
            {
                // Generator/async resume prelude — only when a resume
                // action is pending. The prelude pushes resume values or
                // re-parks the frame, so it works on the frame's authoritative
                // fields: flush the hot locals first, reload after.
                if (frame.Suspension is { } pendingSusp
                    && pendingSusp.ResumeAction != ContinuationResumeAction.None)
                {
                    frame.Ip = ip;
                    frame.Sp = sp;
                    frame.MaxSp = maxSp;
                    var parked = RunContinuationPrelude(frame, out var continuationResult);
                    ip = frame.Ip;
                    sp = frame.Sp;
                    maxSp = frame.MaxSp;
                    // The prelude pushes via PushFrame, which can grow (and
                    // replace) the frame's operand stack — reload the array too.
                    stack = frame.Stack;
                    if (parked)
                    {
                        return continuationResult;
                    }
                }

                var op = (Opcode)code[ip++];
                OpcodeCounts[(byte)op]++;
                switch (op)
                {
                    case Opcode.Halt:
                        {
                            // Read the completion value BEFORE the releases clear
                            // the pooled arrays.
                            var hv = sp > 0 ? stack[sp - 1] : JsValue.Undefined;
                            ReleaseLocals(frame);
                            return Finish(stack, maxSp, hv);
                        }
                    case Opcode.Nop: break;

                    // ----- Constants -----
                    case Opcode.LoadConst:
                        {
                            var idx = ReadU16(code, ref ip);
                            var c = constants[idx];
                            Push(frame, ref stack, ref sp, ref maxSp, c switch
                            {
                                double d => JsValue.Number(d),
                                string s => JsValue.String(s),
                                JsBigIntPlaceholder bi => JsValue.BigInt(bi.Value),
                                _ => JsValue.Undefined,
                            });
                            break;
                        }
                    case Opcode.LoadTrue: Push(frame, ref stack, ref sp, ref maxSp, JsValue.True); break;
                    case Opcode.LoadFalse: Push(frame, ref stack, ref sp, ref maxSp, JsValue.False); break;
                    case Opcode.LoadNull: Push(frame, ref stack, ref sp, ref maxSp, JsValue.Null); break;
                    case Opcode.LoadUndefined: Push(frame, ref stack, ref sp, ref maxSp, JsValue.Undefined); break;
                    case Opcode.LoadZero: Push(frame, ref stack, ref sp, ref maxSp, JsValue.Zero); break;

                    // ----- Locals -----
                    // Local-slot operands are u16 (see ChunkBuilder.EmitSlot):
                    // large minified bundles routinely declare >255 locals in one
                    // function, which a u8 operand would alias modulo 256.
                    case Opcode.DeclareLocal:
                        {
                            var slot = ReadU16(code, ref ip);
                            locals[slot] = JsValue.Undefined;
                            break;
                        }
                    case Opcode.LoadLocal: Push(frame, ref stack, ref sp, ref maxSp, locals[ReadU16(code, ref ip)]); break;
                    case Opcode.StoreLocal: locals[ReadU16(code, ref ip)] = Pop(stack, ref sp); break;

                    // ----- Lexical bindings / Temporal Dead Zone -----
                    // A let/const/class slot is seeded with the TDZ sentinel at
                    // scope entry; any read/write before the declaration's
                    // initializer runs throws ReferenceError (§§9.1.1.1.4 /
                    // 13.3.1.1). The plain DeclareLocal/StoreLocal opcodes are
                    // unchanged so var/param fast paths take no extra branch.
                    case Opcode.DeclareLocalTdz:
                        {
                            var slot = ReadU16(code, ref ip);
                            locals[slot] = JsValue.Object(_runtime.Realm.TdzSentinel);
                            break;
                        }
                    case Opcode.InitCellLocalTdz:
                        {
                            var slot = ReadU16(code, ref ip);
                            locals[slot] = JsValue.Object(
                                new Cell(JsValue.Object(_runtime.Realm.TdzSentinel)));
                            break;
                        }
                    case Opcode.LoadLocalChecked:
                        {
                            var v = locals[ReadU16(code, ref ip)];
                            if (v.IsObject && ReferenceEquals(v.AsObject, _runtime.Realm.TdzSentinel))
                            {
                                throw new JsThrow(_runtime.Realm.NewReferenceError(
                                    "Cannot access a lexical binding before initialization"));
                            }

                            Push(frame, ref stack, ref sp, ref maxSp, v);
                            break;
                        }
                    case Opcode.LoadCellLocalChecked:
                        {
                            var cell = (Cell)locals[ReadU16(code, ref ip)].AsObject;
                            var v = cell.Value;
                            if (v.IsObject && ReferenceEquals(v.AsObject, _runtime.Realm.TdzSentinel))
                            {
                                throw new JsThrow(_runtime.Realm.NewReferenceError(
                                    "Cannot access a lexical binding before initialization"));
                            }

                            Push(frame, ref stack, ref sp, ref maxSp, v);
                            break;
                        }
                    case Opcode.StoreCellLocalChecked:
                        {
                            var cell = (Cell)locals[ReadU16(code, ref ip)].AsObject;
                            if (cell.Value.IsObject
                                && ReferenceEquals(cell.Value.AsObject, _runtime.Realm.TdzSentinel))
                            {
                                throw new JsThrow(_runtime.Realm.NewReferenceError(
                                    "Cannot access a lexical binding before initialization"));
                            }

                            cell.Value = Pop(stack, ref sp);
                            break;
                        }
                    case Opcode.LoadUpvalueChecked:
                        {
                            var idx = ReadU16(code, ref ip);
                            var upV = frame.Upvalues[idx];
                            JsValue v;
                            if (upV.IsObject && upV.AsObject is Cell c)
                            {
                                v = c.Value;
                            }
                            else
                            {
                                v = upV;
                            }

                            if (v.IsObject && ReferenceEquals(v.AsObject, _runtime.Realm.TdzSentinel))
                            {
                                throw new JsThrow(_runtime.Realm.NewReferenceError(
                                    "Cannot access a lexical binding before initialization"));
                            }

                            Push(frame, ref stack, ref sp, ref maxSp, v);
                            break;
                        }
                    case Opcode.StoreUpvalueChecked:
                        {
                            var idx = ReadU16(code, ref ip);
                            var cell = (Cell)frame.Upvalues[idx].AsObject;
                            if (cell.Value.IsObject
                                && ReferenceEquals(cell.Value.AsObject, _runtime.Realm.TdzSentinel))
                            {
                                throw new JsThrow(_runtime.Realm.NewReferenceError(
                                    "Cannot access a lexical binding before initialization"));
                            }

                            cell.Value = Pop(stack, ref sp);
                            break;
                        }

                    // ----- Captured locals -----
                    case Opcode.InitCellLocal:
                        {
                            var slot = ReadU16(code, ref ip);
                            locals[slot] = JsValue.Object(new Cell(JsValue.Undefined));
                            break;
                        }
                    case Opcode.LoadCellLocal:
                        {
                            var slot = ReadU16(code, ref ip);
                            var cell = (Cell)locals[slot].AsObject;
                            Push(frame, ref stack, ref sp, ref maxSp, cell.Value);
                            break;
                        }
                    case Opcode.StoreCellLocal:
                        {
                            var slot = ReadU16(code, ref ip);
                            var cell = (Cell)locals[slot].AsObject;
                            cell.Value = Pop(stack, ref sp);
                            break;
                        }
                    case Opcode.PromoteParamCell:
                        {
                            var slot = ReadU16(code, ref ip);
                            locals[slot] = JsValue.Object(new Cell(locals[slot]));
                            break;
                        }
                    case Opcode.StoreUpvalue:
                        {
                            var idx = ReadU16(code, ref ip);
                            var cell = (Cell)frame.Upvalues[idx].AsObject;
                            cell.Value = Pop(stack, ref sp);
                            break;
                        }
                    case Opcode.LoadUpvalueCell:
                        {
                            var idx = ReadU16(code, ref ip);
                            Push(frame, ref stack, ref sp, ref maxSp, frame.Upvalues[idx]);
                            break;
                        }
                    // §14.7.4.4 CreatePerIterationEnvironment — read the cell in
                    // `slot`, allocate a fresh Cell with the same value, write
                    // it back. Closures formed in the upcoming iteration body
                    // capture the new cell; previous iterations' closures retain
                    // theirs, giving each iteration its own binding for `let` /
                    // `const` declared in a for-loop init.
                    case Opcode.RefreshLetBinding:
                        {
                            var slot = ReadU16(code, ref ip);
                            var oldCell = (Cell)locals[slot].AsObject;
                            locals[slot] = JsValue.Object(new Cell(oldCell.Value));
                            break;
                        }

                    // ----- Globals -----
                    // gap:opcode-fast-path-bypasses-accessors — route global
                    // reads/writes through AbstractOperations.Get/Set so accessor
                    // descriptors on the global object (e.g. `location` defined via
                    // Object.defineProperty with a getter) invoke their getter/setter
                    // instead of silently returning undefined / overwriting the slot.
                    case Opcode.LoadGlobal:
                        {
                            var idx = ReadU16(code, ref ip);
                            var name = (string)constants[idx]!;
                            _lastLoadName = name;
                            // wp:M3-73 — a free identifier resolves through this frame's
                            // eval-introduced var store (a var/function a direct eval
                            // injected) before the global object (spec order: local ->
                            // upvalue -> var-env -> global).
                            if (frame.FrameVarStore is not null && frame.FrameVarStore.TryGet(name, out var evCell0))
                            {
                                Push(frame, ref stack, ref sp, ref maxSp, evCell0.Value);
                                break;
                            }
                            var globalObj = _runtime.Realm.GlobalObject;
                            Push(frame, ref stack, ref sp, ref maxSp, AbstractOperations.Get(this, globalObj, name, JsValue.Object(globalObj)));
                            break;
                        }
                    case Opcode.LoadGlobalChecked:
                        {
                            // §6.2.5.5 GetValue — reading a free identifier that resolves
                            // to no binding is an unresolvable Reference and throws a
                            // ReferenceError. Emitted for all free-identifier reads
                            // (except the operand of typeof/delete). An embedder — e.g.
                            // the browser, for graceful degradation of host globals it
                            // hasn't implemented yet — can suppress the throw via the
                            // realm's ThrowOnUnresolvedGlobalRead flag / LenientGlobalNames
                            // allowlist (then the read yields undefined, the old behavior).
                            var idx = ReadU16(code, ref ip);
                            var name = (string)constants[idx]!;
                            _lastLoadName = name;
                            // wp:M3-73 — resolve through the eval-introduced var store
                            // before the global object (see LoadGlobal).
                            if (frame.FrameVarStore is not null && frame.FrameVarStore.TryGet(name, out var evCell1))
                            {
                                Push(frame, ref stack, ref sp, ref maxSp, evCell1.Value);
                                break;
                            }
                            var realm = _runtime.Realm;
                            var globalObj = realm.GlobalObject;
                            if (!globalObj.Has(name))
                            {
                                if (realm.ThrowOnUnresolvedGlobalRead && !realm.LenientGlobalNames.Contains(name))
                                {
                                    throw new JsThrow(realm.NewReferenceError(name + " is not defined"));
                                }

                                Push(frame, ref stack, ref sp, ref maxSp, JsValue.Undefined);
                                break;
                            }
                            Push(frame, ref stack, ref sp, ref maxSp, AbstractOperations.Get(this, globalObj, name, JsValue.Object(globalObj)));
                            break;
                        }
                    case Opcode.StoreGlobal:
                        {
                            var idx = ReadU16(code, ref ip);
                            var name = (string)constants[idx]!;
                            var value = Pop(stack, ref sp);
                            // wp:M3-73 — an assignment to a free identifier writes through
                            // this frame's eval-introduced var store when it owns the name
                            // (a var/function a direct eval injected), before the global
                            // object. This is how the eval body's own `x = 4` and the
                            // caller's post-eval writes hit the injected binding.
                            if (frame.FrameVarStore is not null && frame.FrameVarStore.TryGet(name, out var evCell2))
                            {
                                evCell2.Value = value;
                                break;
                            }
                            var globalObj = _runtime.Realm.GlobalObject;
                            // §9.1.1.4.16 / §13.15.2 — in strict code, assigning to an
                            // identifier that resolves to no existing binding is a
                            // ReferenceError (no implicit global creation). Walk the
                            // prototype chain since inherited accessors/props count.
                            if (frame.FrameStrict && !globalObj.Has(name))
                            {
                                throw new JsThrow(_runtime.Realm.NewReferenceError(name + " is not defined"));
                            }
                            // §10.1.9 — a strict assignment that the [[Set]] rejects
                            // (non-writable prop, accessor without setter) throws TypeError.
                            var ok = AbstractOperations.Set(this, globalObj, name, value, JsValue.Object(globalObj));
                            if (!ok && frame.FrameStrict)
                            {
                                throw new JsThrow(_runtime.Realm.NewTypeError(
                                    "Cannot assign to read-only property '" + name + "'"));
                            }

                            break;
                        }



                    // ----- Stack manipulation -----
                    case Opcode.Pop: sp--; break;
                    case Opcode.Dup: Push(frame, ref stack, ref sp, ref maxSp, stack[sp - 1]); break;
                    case Opcode.Dup2:
                        {
                            // (..., a, b) → (..., a, b, a, b)
                            var b = stack[sp - 1];
                            var a = stack[sp - 2];
                            Push(frame, ref stack, ref sp, ref maxSp, a);
                            Push(frame, ref stack, ref sp, ref maxSp, b);
                            break;
                        }
                    case Opcode.Swap:
                        {
                            var b = stack[sp - 1];
                            stack[sp - 1] = stack[sp - 2];
                            stack[sp - 2] = b;
                            break;
                        }

                    // ----- Arithmetic -----
                    case Opcode.Add:
                        {
                            var b = Pop(stack, ref sp);
                            var a = Pop(stack, ref sp);
                            Push(frame, ref stack, ref sp, ref maxSp, JsAdd(a, b));
                            break;
                        }
                    // Numeric/bitwise operators — executed out-of-line so
                    // their per-arm locals stay off this frame (see ExecArith;
                    // same reasoning as DispatchCold).
                    case Opcode.Sub:
                    case Opcode.Mul:
                    case Opcode.Div:
                    case Opcode.Mod:
                    case Opcode.Pow:
                    case Opcode.Neg:
                    case Opcode.UnaryPlus:
                    case Opcode.BitOr:
                    case Opcode.BitAnd:
                    case Opcode.BitXor:
                    case Opcode.BitNot:
                    case Opcode.Shl:
                    case Opcode.Shr:
                    case Opcode.Ushr:
                        ExecArith(op, frame, ref stack, ref sp, ref maxSp);
                        break;

                    // Comparison / typeof / instanceof / in — executed
                    // out-of-line so their per-arm locals stay off this frame
                    // (see ExecCompare; same reasoning as DispatchCold).
                    case Opcode.Eq:
                    case Opcode.NEq:
                    case Opcode.StrictEq:
                    case Opcode.StrictNEq:
                    case Opcode.Lt:
                    case Opcode.LtEq:
                    case Opcode.Gt:
                    case Opcode.GtEq:
                    case Opcode.TypeOf:
                    case Opcode.Instanceof:
                    case Opcode.In:
                        ExecCompare(op, frame, ref stack, ref sp, ref maxSp);
                        break;

                    case Opcode.Not: Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(!JsValue.ToBoolean(Pop(stack, ref sp)))); break;

                    // ----- Property access -----
                    case Opcode.LoadProperty:
                        {
                            var idx = ReadU16(code, ref ip);
                            var cacheId = ReadU16(code, ref ip);
                            var name = (string)constants[idx]!;
                            _lastLoadName = name;
                            var obj = Pop(stack, ref sp);
                            if (obj.IsObject)
                            {
                                var o = obj.AsObject;
                                var sh = o.Shape;
                                if (cacheId != 0xFFFF)
                                {
                                    var ic = chunk.Caches[cacheId];
                                    if (sh is not null && ReferenceEquals(sh, ic.Shape) && o.SupportsInlineCache)
                                    {
                                        // Own data property at a known slot.
                                        if (ic.Holder is null) { Push(frame, ref stack, ref sp, ref maxSp, o.ReadSlot(ic.Slot)); break; }
                                        // One-hop inherited data property. Same shape proves
                                        // no own shadow; same direct prototype proves the
                                        // same chain; unchanged epoch proves the holder
                                        // still has the property at that slot.
                                        if (ReferenceEquals(o.Prototype, ic.Holder) && ic.Epoch == JsObject.ProtoEpoch)
                                        {
                                            Push(frame, ref stack, ref sp, ref maxSp, ic.Holder.ReadSlot(ic.Slot));
                                            break;
                                        }
                                    }
                                    var result = AbstractOperations.Get(this, o, name);
                                    if (sh is not null && o.SupportsInlineCache)
                                    {
                                        if (sh.TryGet(name, out var ownHit))
                                        {
                                            chunk.Caches[cacheId] = new InlineCache { Shape = sh, Slot = ownHit.Slot };
                                        }
                                        else if (o.Prototype is { Shape: { } dsh } dp && dp.SupportsInlineCache
                                            && dsh.TryGet(name, out var pp))
                                        {
                                            // Property lives on the DIRECT prototype as fast
                                            // data (one hop). Multi-hop and accessor/dict
                                            // holders are left to the slow path.
                                            chunk.Caches[cacheId] = new InlineCache
                                            { Shape = sh, Slot = pp.Slot, Holder = dp, Epoch = JsObject.ProtoEpoch };
                                        }
                                    }
                                    Push(frame, ref stack, ref sp, ref maxSp, result);
                                    break;
                                }
                                Push(frame, ref stack, ref sp, ref maxSp, AbstractOperations.Get(this, o, name));
                                break;
                            }
                            if (!obj.IsNullish)
                            {
                                // String "length" is just the code-unit count — read it
                                // directly, no boxed String object. Every other primitive
                                // property lives on the prototype chain, reached without a
                                // wrapper: §6.2.5.5 runs [[Get]] with the primitive as the
                                // receiver. A string's only other exotic own keys are
                                // canonical indices, which arrive as computed access, never a
                                // named LoadProperty, so no box is ever needed here.
                                if (obj.IsString && name == "length")
                                {
                                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Number(obj.AsString.Length));
                                }
                                else
                                {
                                    var proto = PrimitivePrototype(obj);
                                    Push(frame, ref stack, ref sp, ref maxSp, proto is not null
                                        ? AbstractOperations.Get(this, proto, name, obj)
                                        : AbstractOperations.Get(this, AbstractOperations.ToObject(_runtime.Realm, obj), name, obj));
                                }
                            }
                            else
                            {
                                throw new JsThrow(_runtime.Realm.NewTypeError(
                                "Cannot read properties of " + NullishLabel(obj) + " (reading '" + name + "')"));
                            }

                            break;
                        }
                    case Opcode.StoreProperty:
                        {
                            var idx = ReadU16(code, ref ip);
                            var cacheId = ReadU16(code, ref ip);
                            var name = (string)constants[idx]!;
                            var value = Pop(stack, ref sp);
                            var obj = Pop(stack, ref sp);
                            if (obj.IsObject)
                            {
                                var o = obj.AsObject;
                                var sh = o.Shape;
                                if (cacheId != 0xFFFF && sh is not null && o.SupportsInlineCache)
                                {
                                    var ic = chunk.Caches[cacheId];
                                    if (ReferenceEquals(sh, ic.Shape))
                                    {
                                        // Write an existing own writable data slot — an own
                                        // writable data property shadows the chain, so this
                                        // is safe regardless of the prototype.
                                        if (ic.NextShape is null)
                                        {
                                            o.WriteSlot(ic.Slot, value);
                                            Push(frame, ref stack, ref sp, ref maxSp, value);
                                            break;
                                        }
                                        // Add a new property via the cached transition.
                                        // Valid only for the same direct prototype (same
                                        // chain) with an unchanged epoch (no inherited
                                        // setter / non-writable data appeared for this name).
                                        if (ReferenceEquals(o.Prototype, ic.Holder) && ic.Epoch == JsObject.ProtoEpoch)
                                        {
                                            o.FastAdd(ic.NextShape, ic.Slot, value);
                                            Push(frame, ref stack, ref sp, ref maxSp, value);
                                            break;
                                        }
                                    }
                                    // Miss: run the spec [[Set]], then cache an existing-slot
                                    // write or an add transition.
                                    var okIc = AbstractOperations.Set(this, o, name, value);
                                    if (!okIc)
                                    {
                                        if (frame.FrameStrict)
                                        {
                                            throw new JsThrow(_runtime.Realm.NewTypeError(
                                                "Cannot assign to read-only property '" + name + "'"));
                                        }
                                    }
                                    else
                                    {
                                        var after = o.Shape;
                                        if (after is not null && o.SupportsInlineCache
                                            && after.TryGet(name, out var p) && p.Writable)
                                        {
                                            chunk.Caches[cacheId] = ReferenceEquals(after, sh)
                                                ? new InlineCache { Shape = sh, Slot = p.Slot }
                                                : new InlineCache { Shape = sh, Slot = p.Slot, NextShape = after, Holder = o.Prototype, Epoch = JsObject.ProtoEpoch };
                                        }
                                    }
                                    Push(frame, ref stack, ref sp, ref maxSp, value);
                                    break;
                                }
                                var ok = AbstractOperations.Set(this, o, name, value);
                                // §10.1.9 / §13.15.2 — a strict assignment the [[Set]]
                                // rejects (non-writable data prop, accessor without a
                                // setter, or add to a non-extensible object) throws.
                                if (!ok && frame.FrameStrict)
                                {
                                    throw new JsThrow(_runtime.Realm.NewTypeError(
                                        "Cannot assign to read-only property '" + name + "'"));
                                }
                            }
                            else if (obj.IsNullish)
                            {
                                // §13.15.2 PutValue on a nullish base is always a TypeError.
                                throw new JsThrow(_runtime.Realm.NewTypeError(
                                    "Cannot set property '" + name + "' of " + NullishLabel(obj)));
                            }
                            Push(frame, ref stack, ref sp, ref maxSp, value);
                            break;
                        }
                    case Opcode.LoadComputed:
                        {
                            var key = Pop(stack, ref sp);
                            var obj = Pop(stack, ref sp);
                            var propertyKey = AbstractOperations.ToPropertyKey(this, key);
                            if (obj.IsObject)
                            {
                                Push(frame, ref stack, ref sp, ref maxSp, AbstractOperations.Get(this, obj.AsObject, propertyKey));
                            }
                            else if (!obj.IsNullish)
                            {
                                Push(frame, ref stack, ref sp, ref maxSp, AbstractOperations.Get(this, AbstractOperations.ToObject(_runtime.Realm, obj), propertyKey, obj));
                            }
                            else
                            {
                                throw new JsThrow(_runtime.Realm.NewTypeError(
                                "Cannot read properties of " + NullishLabel(obj) + " (reading '" + propertyKey + "')"));
                            }

                            break;
                        }
                    case Opcode.ResolveComputedKey:
                        {
                            // §13.15.2 / §13.3.3 — resolve a compound-assignment computed
                            // key once. Spec order: the base's coercibility is checked
                            // BEFORE ToPropertyKey, so `null[obj] *= …` throws TypeError
                            // without ever invoking the key's toString.
                            var rawKey = Pop(stack, ref sp);
                            var baseV = stack[sp - 1];
                            if (baseV.IsNullish)
                            {
                                throw new JsThrow(_runtime.Realm.NewTypeError(
                                    "Cannot read properties of " + (baseV.IsNull ? "null" : "undefined")
                                    + " (reading a computed property)"));
                            }

                            var resolved = AbstractOperations.ToPropertyKey(this, rawKey);
                            Push(frame, ref stack, ref sp, ref maxSp, resolved.IsSymbol ? JsValue.Symbol(resolved.AsSymbol)
                                                   : JsValue.String(resolved.AsString));
                            break;
                        }
                    case Opcode.StoreComputed:
                        {
                            var value = Pop(stack, ref sp);
                            var key = Pop(stack, ref sp);
                            var obj = Pop(stack, ref sp);
                            var pk = AbstractOperations.ToPropertyKey(this, key);
                            if (obj.IsNullish)
                            {
                                throw new JsThrow(_runtime.Realm.NewTypeError(
                                    "Cannot set property '" + pk + "' of " + NullishLabel(obj)));
                            }

                            if (obj.IsObject)
                            {
                                var ok = AbstractOperations.Set(this, obj.AsObject, pk, value);
                                if (!ok && frame.FrameStrict)
                                {
                                    throw new JsThrow(_runtime.Realm.NewTypeError(
                                        "Cannot assign to read-only property '" + pk + "'"));
                                }
                            }
                            Push(frame, ref stack, ref sp, ref maxSp, value);
                            break;
                        }

                    // wp:M3-81 — §sec-performeval-rules-in-initializer: open/close the
                    // initializer region bracketing a non-arrow function's parameter
                    // default prologue. While initDepth > 0, a direct eval whose
                    // ScriptBody ContainsArguments throws a SyntaxError.
                    case Opcode.EnterInitializer:
                        frame.InitDepth++;
                        break;
                    case Opcode.ExitInitializer:
                        frame.InitDepth--;
                        break;

                    // ----- Calls -----
                    // §10.2.1: plain Call binds this=Undefined (strict default);
                    // CallMethod takes a receiver and binds this=receiver, used
                    // by the compiler for obj.method() / obj[key]() syntax.
                    case Opcode.Call:
                        {
                            var argc = code[ip++];
                            var callArgs = RentArgs(argc);
                            for (var i = argc - 1; i >= 0; i--)
                            {
                                callArgs[i] = Pop(stack, ref sp);
                            }

                            var callee = Pop(stack, ref sp);
                            if (!IsCallableValue(callee))
                            {
                                throw new JsThrow(JsValue.String(AtPos(chunk, ip, $"not a function: {JsValue.ToStringValue(callee)} (callee hint: '{_lastLoadName}')")));
                            }
                            // wp:M3-84 Stage B — ordinary same-realm JsFunction:
                            // push a trampolined frame, no native recursion.
                            if (TryPushCall(callee, JsValue.Undefined, callArgs, frame, ip, sp, maxSp, out var pushed))
                            {
                                frame = pushed;
                                LoadFrameCache(frame, ref chunk, ref stack, ref locals, ref code, ref constants, ref ip, ref sp, ref maxSp);
                                break;
                            }
                            var callResult = AbstractOperations.Call(this, callee, JsValue.Undefined, callArgs);
                            ReturnArgs(callArgs);
                            Push(frame, ref stack, ref sp, ref maxSp, callResult);
                            break;
                        }
                    case Opcode.CallMethod:
                        {
                            var argc = code[ip++];
                            var callArgs = RentArgs(argc);
                            for (var i = argc - 1; i >= 0; i--)
                            {
                                callArgs[i] = Pop(stack, ref sp);
                            }

                            var callee = Pop(stack, ref sp);
                            var receiver = Pop(stack, ref sp);
                            if (!IsCallableValue(callee))
                            {
                                throw new JsThrow(JsValue.String(AtPos(chunk, ip, $"not a function: {JsValue.ToStringValue(callee)} (method hint: '{_lastLoadName}')")));
                            }

                            if (TryPushCall(callee, receiver, callArgs, frame, ip, sp, maxSp, out var pushed))
                            {
                                frame = pushed;
                                LoadFrameCache(frame, ref chunk, ref stack, ref locals, ref code, ref constants, ref ip, ref sp, ref maxSp);
                                break;
                            }
                            var methodResult = AbstractOperations.Call(this, callee, receiver, callArgs);
                            ReturnArgs(callArgs);
                            Push(frame, ref stack, ref sp, ref maxSp, methodResult);
                            break;
                        }

                    // LoadFunction — pull a pre-compiled JsFunction template
                    // out of the constant pool (empty upvalues) and wrap as
                    // an object value. Used only for non-capturing functions;
                    // capturing ones come through MakeClosure.
                    case Opcode.LoadFunction:
                        {
                            var idx = ReadU16(code, ref ip);
                            var template = (JsFunction)constants[idx]!;
                            // Per B2-2: every LoadFunction produces a fresh instance
                            // wired to realm.FunctionPrototype with its own
                            // `prototype`/`name`/`length` own properties. The template
                            // in the constant pool stays untouched.
                            var fn = JsFunction.CreateInstance(_runtime.Realm, template, Array.Empty<JsValue>());
                            // §14.11 / §10.2.1 — capture the active with-objects so the
                            // function body resolves free identifiers against them.
                            if (template.Body.CapturesWith && frame.WithStack is { Count: > 0 })
                            {
                                fn.CapturedWith = frame.WithStack.ToArray();
                            }
                            // wp:M3-64 — §14.2 / §13.2.5: an arrow inherits the enclosing
                            // method's [[HomeObject]] lexically so `super.x` inside it
                            // resolves against the enclosing method's home object.
                            if (template.Body.IsArrow && frame.CurrentFunction?.HomeObject is { } h1)
                            {
                                fn.HomeObject = h1;
                            }
                            // wp:M3-81 — §sec-performeval-rules-in-initializer: an arrow
                            // created while this frame is inside an initializer region (a
                            // parameter default or a field/static initializer) inherits the
                            // "inside-initializer" status lexically, so a deferred direct
                            // eval in its body still hits the ContainsArguments early error
                            // when the arrow is later invoked.
                            if (template.Body.IsArrow && frame.InitDepth > 0)
                            {
                                fn.InInitializer = true;
                            }
                            // wp:M3-73 — snapshot the creating frame's eval-introduced var
                            // store so this closure resolves free identifiers through the
                            // vars a direct eval injected into the enclosing function's
                            // variable environment (spec scope chain) before the global.
                            if (frame.FrameVarStore is not null)
                            {
                                fn.CapturedEvalVarStore = frame.FrameVarStore;
                            }

                            Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(fn));
                            break;
                        }

                    // MakeClosure — pop N captured values and wrap a fresh
                    // JsFunction over the template, with those values bound
                    // as snapshot upvalues. §10.2.1 (closure-of-environment),
                    // adapted to our snapshot-only semantics for M3-04c.
                    case Opcode.MakeClosure:
                        {
                            var idx = ReadU16(code, ref ip);
                            var nUpvalues = ReadU16(code, ref ip);
                            var template = (JsFunction)constants[idx]!;
                            var captured = new JsValue[nUpvalues];
                            for (var i = nUpvalues - 1; i >= 0; i--)
                            {
                                captured[i] = Pop(stack, ref sp);
                            }
                            // Per B2-2: closure also routes through CreateInstance so
                            // it inherits Function.prototype and gets a per-call
                            // `prototype` own-property.
                            var closure = JsFunction.CreateInstance(_runtime.Realm, template, captured);
                            // §14.11 / §10.2.1 — capture the active with-objects (see
                            // LoadFunction) so the closure body resolves free identifiers
                            // against the enclosing object Environment Records.
                            if (template.Body.CapturesWith && frame.WithStack is { Count: > 0 })
                            {
                                closure.CapturedWith = frame.WithStack.ToArray();
                            }
                            // wp:M3-64 — an arrow closure inherits the enclosing method's
                            // [[HomeObject]] lexically for `super.x` (see LoadFunction).
                            if (template.Body.IsArrow && frame.CurrentFunction?.HomeObject is { } h2)
                            {
                                closure.HomeObject = h2;
                            }
                            // wp:M3-81 — an arrow closure created inside an initializer
                            // region inherits the inside-initializer status (see
                            // LoadFunction) for the eval ContainsArguments early error.
                            if (template.Body.IsArrow && frame.InitDepth > 0)
                            {
                                closure.InInitializer = true;
                            }
                            // wp:M3-73 — snapshot the creating frame's eval-introduced var
                            // store (see LoadFunction).
                            if (frame.FrameVarStore is not null)
                            {
                                closure.CapturedEvalVarStore = frame.FrameVarStore;
                            }

                            Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(closure));
                            break;
                        }

                    case Opcode.LoadUpvalue:
                        {
                            // Every upvalue is a Cell, so
                            // dereference to push the current bound value. Use
                            // LoadUpvalueCell to push the raw cell (for further
                            // chained captures).
                            var idx = ReadU16(code, ref ip);
                            var upV = frame.Upvalues[idx];
                            if (upV.IsObject && upV.AsObject is Cell c)
                            {
                                Push(frame, ref stack, ref sp, ref maxSp, c.Value);
                            }
                            else
                            {
                                Push(frame, ref stack, ref sp, ref maxSp, upV); // legacy snapshot path — empty in practice
                            }

                            break;
                        }

                    case Opcode.LoadThis:
                        Push(frame, ref stack, ref sp, ref maxSp, frame.ThisV);
                        break;

                    case Opcode.NewObject:
                        Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(_runtime.Realm.NewOrdinaryObject()));
                        break;

                    case Opcode.NewArray:
                        Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(new JsArray(_runtime.Realm)));
                        break;



                    case Opcode.New:
                        {
                            var argc = code[ip++];
                            var newArgs = RentArgs(argc);
                            for (var i = argc - 1; i >= 0; i--)
                            {
                                newArgs[i] = Pop(stack, ref sp);
                            }

                            var ctor = Pop(stack, ref sp);
                            if (!ctor.IsObject)
                            {
                                throw new JsThrow(JsValue.String(AtPos(chunk, ip, $"not a constructor: {JsValue.ToStringValue(ctor)} (new hint: '{_lastLoadName}')")));
                            }

                            if (TryPushConstruct(ctor, newArgs, newTarget: null, frame, ip, sp, maxSp,
                                    FrameDisposition.Construct, out var pushed))
                            {
                                frame = pushed;
                                LoadFrameCache(frame, ref chunk, ref stack, ref locals, ref code, ref constants, ref ip, ref sp, ref maxSp);
                                break;
                            }
                            var newResult = AbstractOperations.Construct(this, ctor, newArgs);
                            ReturnArgs(newArgs);
                            Push(frame, ref stack, ref sp, ref maxSp, newResult);
                            break;
                        }

                    // ----- Control flow -----
                    case Opcode.Jump: { var d = ReadI32(code, ref ip); ip += d; break; }
                    case Opcode.JumpIfTrue:
                        {
                            var d = ReadI32(code, ref ip);
                            if (JsValue.ToBoolean(Pop(stack, ref sp)))
                            {
                                ip += d;
                            }

                            break;
                        }
                    case Opcode.JumpIfFalse:
                        {
                            var d = ReadI32(code, ref ip);
                            if (!JsValue.ToBoolean(Pop(stack, ref sp)))
                            {
                                ip += d;
                            }

                            break;
                        }
                    case Opcode.JumpIfNotNullish:
                        {
                            var d = ReadI32(code, ref ip);
                            if (!Pop(stack, ref sp).IsNullish)
                            {
                                ip += d;
                            }

                            break;
                        }

                    // ----- Returns -----
                    // §14.15: divert through any enclosing finalizer first.
                    // wp:M3-84 Stage B — a return pops the frame: release its
                    // pooled arrays, restore the caller, apply the disposition's
                    // return coercion, and deliver the value to the caller's
                    // operand stack. A barrier frame exits the dispatch loop
                    // returning the RAW value — RunBarrier coerces outside this
                    // try, so a coercion throw (derived ctor that never ran
                    // super) propagates natively to the construct site instead
                    // of being caught here, where the unwinder would release
                    // the already-released frame a second time and poison the
                    // array pool.
                    case Opcode.Return:
                        {
                            var rv = Pop(stack, ref sp);
                            if (DivertReturnThroughFinally(frame.TryStack, rv, ref ip))
                            {
                                break;
                            }

                            ReleaseFrame(frame, stack, maxSp);
                            if (frame.IsBarrier)
                            {
                                return rv;
                            }

                            var popped = frame;
                            frame = popped.Caller!;
                            t_current = frame;
                            LoadFrameCache(frame, ref chunk, ref stack, ref locals, ref code, ref constants, ref ip, ref sp, ref maxSp);
                            Push(frame, ref stack, ref sp, ref maxSp, CoerceReturn(popped, rv));
                            break;
                        }
                    case Opcode.ReturnUndefined:
                        {
                            if (DivertReturnThroughFinally(frame.TryStack, JsValue.Undefined, ref ip))
                            {
                                break;
                            }

                            ReleaseFrame(frame, stack, maxSp);
                            if (frame.IsBarrier)
                            {
                                return JsValue.Undefined;
                            }

                            var popped = frame;
                            frame = popped.Caller!;
                            t_current = frame;
                            LoadFrameCache(frame, ref chunk, ref stack, ref locals, ref code, ref constants, ref ip, ref sp, ref maxSp);
                            Push(frame, ref stack, ref sp, ref maxSp, CoerceReturn(popped, JsValue.Undefined));
                            break;
                        }

                    // ----- Throw -----
                    case Opcode.Throw: throw new JsThrow(Pop(stack, ref sp));


                    // ----- Try-frame management (gap:try-catch) -----
                    case Opcode.EnterTry:
                        {
                            var catchOff = ReadI32(code, ref ip);
                            var finOff = ReadI32(code, ref ip);
                            // First try-frame of this activation allocates the
                            // stack — most functions never get here.
                            (frame.TryStack ??= new Stack<TryFrame>()).Push(new TryFrame
                            {
                                CatchPc = catchOff == -1 ? -1 : ip + catchOff,
                                FinallyPc = finOff == -1 ? -1 : ip + finOff,
                                StackBase = sp,
                                Phase = TryPhase.TryBody,
                                Pending = PendingCompletion.None,
                                PendingValue = JsValue.Undefined,
                            });
                            break;
                        }
                    case Opcode.LeaveTry:
                        {
                            if (frame.TryStack is not { Count: > 0 } tryStack)
                            {
                                throw new InvalidOperationException("LeaveTry with empty try-frame stack");
                            }

                            var tf = tryStack.Peek();
                            if (tf.FinallyPc != -1 && tf.Phase != TryPhase.RunningFinally)
                            {
                                tf.Phase = TryPhase.RunningFinally;
                                tf.Pending = PendingCompletion.Normal;
                                tf.PendingValue = JsValue.Undefined;
                                tryStack.Pop(); tryStack.Push(tf);
                                ip = tf.FinallyPc;
                            }
                            else
                            {
                                tryStack.Pop();
                            }
                            break;
                        }
                    case Opcode.EndFinally:
                        {
                            if (frame.TryStack is not { Count: > 0 } tryStack)
                            {
                                throw new InvalidOperationException("EndFinally with empty try-frame stack");
                            }

                            var tf = tryStack.Pop();
                            switch (tf.Pending)
                            {
                                case PendingCompletion.Normal:
                                    break;
                                case PendingCompletion.Throw:
                                    throw new JsThrow(tf.PendingValue);
                                case PendingCompletion.Return:
                                    {
                                        // wp:M3-84 Stage B — a return completing its
                                        // finalizers pops the frame exactly like the
                                        // Return opcode above.
                                        var rv = tf.PendingValue;
                                        if (DivertReturnThroughFinally(frame.TryStack, rv, ref ip))
                                        {
                                            break;
                                        }

                                        ReleaseFrame(frame, stack, maxSp);
                                        if (frame.IsBarrier)
                                        {
                                            return rv;
                                        }

                                        var popped = frame;
                                        frame = popped.Caller!;
                                        t_current = frame;
                                        LoadFrameCache(frame, ref chunk, ref stack, ref locals, ref code, ref constants, ref ip, ref sp, ref maxSp);
                                        Push(frame, ref stack, ref sp, ref maxSp, CoerceReturn(popped, rv));
                                        break;
                                    }
                                case PendingCompletion.Break:
                                    {
                                        // wp:M3-15 — resume an in-flight break/continue: run
                                        // any further intervening finalizers, then jump to
                                        // the loop/switch site. The finalizer just executed
                                        // completed normally, so the saved completion still
                                        // governs control flow (§14.15.3).
                                        DivertBranchThroughFinally(
                                            frame.TryStack, tf.PendingTargetPc,
                                            tf.PendingUnwindRemaining, ref ip);
                                        break;
                                    }
                            }
                            break;
                        }
                    // wp:M3-15 — break/continue exiting a loop/switch across one or
                    // more enclosing finalizers. Operand: [u8 unwindCount][i16 target].
                    case Opcode.BranchThroughFinally:
                        {
                            int unwindCount = code[ip++];
                            var delta = ReadI32(code, ref ip);
                            var targetPc = ip + delta; // i16 measured from after the operand
                            DivertBranchThroughFinally(frame.TryStack, targetPc, unwindCount, ref ip);
                            break;
                        }


                    case Opcode.RequireObjectCoercible:
                        {
                            // §7.2.1 RequireObjectCoercible — object destructuring of a
                            // null/undefined value is a TypeError (the value stays on the
                            // stack for the following property loads).
                            var v = stack[sp - 1];
                            if (v.IsNullish)
                            {
                                throw new JsThrow(_runtime.Realm.NewTypeError("Cannot destructure null or undefined"));
                            }

                            break;
                        }




                    case Opcode.GetIterator:
                        {
                            var iterable = Pop(stack, ref sp);
                            var record = AbstractOperations.GetIterator(_runtime.Realm, this, iterable);
                            Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(new Starling.Js.Intrinsics.JsIteratorRecordHandle(record)));
                            break;
                        }

                    case Opcode.IteratorStep:
                        {
                            // Peek (don't pop) so the surrounding loop keeps the handle
                            // across iterations. The dispatch arm pushes either the
                            // iterator-result object (done=false) or undefined (done=true)
                            // as the loop sentinel.
                            var top = stack[sp - 1];
                            if (!top.IsObject || top.AsObject is not Starling.Js.Intrinsics.JsIteratorRecordHandle handle)
                            {
                                throw new InvalidOperationException("IteratorStep expects an iterator-record handle on the stack");
                            }

                            var step = AbstractOperations.IteratorStep(_runtime.Realm, this, ref handle.Record);
                            Push(frame, ref stack, ref sp, ref maxSp, step ?? JsValue.Undefined);
                            break;
                        }


                    case Opcode.IteratorBindNext:
                        {
                            // §8.5.3 IteratorBindingInitialization for a single array-
                            // pattern element. Peek the record (kept across elements).
                            // Once the record is Done, further elements bind undefined
                            // WITHOUT calling next() again.
                            var top = stack[sp - 1];
                            if (!top.IsObject || top.AsObject is not Starling.Js.Intrinsics.JsIteratorRecordHandle handle)
                            {
                                throw new InvalidOperationException("IteratorBindNext expects an iterator-record handle on the stack");
                            }

                            if (handle.Record.Done)
                            {
                                Push(frame, ref stack, ref sp, ref maxSp, JsValue.Undefined);
                            }
                            else
                            {
                                var step = AbstractOperations.IteratorStep(_runtime.Realm, this, ref handle.Record);
                                if (step is null)
                                {
                                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Undefined);
                                }
                                else
                                {
                                    // §8.5.3 — if IteratorValue (reading .value) throws,
                                    // the iterator is considered closed: mark Done so a
                                    // surrounding IteratorClose skips return().
                                    JsValue v;
                                    try { v = AbstractOperations.IteratorValue(this, step.Value); }
                                    catch { handle.Record = handle.Record with { Done = true }; throw; }
                                    Push(frame, ref stack, ref sp, ref maxSp, v);
                                }
                            }
                            break;
                        }








                    case Opcode.CallApply:
                        {
                            var argsArrV = Pop(stack, ref sp);
                            var callee = Pop(stack, ref sp);
                            var applyArgs = ExtractApplyArgs(argsArrV);
                            if (TryPushCall(callee, JsValue.Undefined, applyArgs, frame, ip, sp, maxSp, out var pushed))
                            {
                                frame = pushed;
                                LoadFrameCache(frame, ref chunk, ref stack, ref locals, ref code, ref constants, ref ip, ref sp, ref maxSp);
                                break;
                            }
                            var applyResult = AbstractOperations.Call(this, callee, JsValue.Undefined, applyArgs);
                            ReturnArgs(applyArgs);
                            Push(frame, ref stack, ref sp, ref maxSp, applyResult);
                            break;
                        }

                    case Opcode.CallApplyMethod:
                        {
                            var argsArrV = Pop(stack, ref sp);
                            var callee = Pop(stack, ref sp);
                            var receiver = Pop(stack, ref sp);
                            var applyArgs = ExtractApplyArgs(argsArrV);
                            if (TryPushCall(callee, receiver, applyArgs, frame, ip, sp, maxSp, out var pushed))
                            {
                                frame = pushed;
                                LoadFrameCache(frame, ref chunk, ref stack, ref locals, ref code, ref constants, ref ip, ref sp, ref maxSp);
                                break;
                            }
                            var applyResult = AbstractOperations.Call(this, callee, receiver, applyArgs);
                            ReturnArgs(applyArgs);
                            Push(frame, ref stack, ref sp, ref maxSp, applyResult);
                            break;
                        }

                    case Opcode.NewApply:
                        {
                            var argsArrV = Pop(stack, ref sp);
                            var ctor = Pop(stack, ref sp);
                            var applyArgs = ExtractApplyArgs(argsArrV);
                            if (TryPushConstruct(ctor, applyArgs, newTarget: null, frame, ip, sp, maxSp,
                                    FrameDisposition.Construct, out var pushed))
                            {
                                frame = pushed;
                                LoadFrameCache(frame, ref chunk, ref stack, ref locals, ref code, ref constants, ref ip, ref sp, ref maxSp);
                                break;
                            }
                            var applyResult = AbstractOperations.Construct(this, ctor, applyArgs);
                            ReturnArgs(applyArgs);
                            Push(frame, ref stack, ref sp, ref maxSp, applyResult);
                            break;
                        }



                    // ----- Classes (B1b-2a) -----
                    case Opcode.LoadThisChecked:
                        {
                            if (frame.ThisV.IsObject
                                && ReferenceEquals(frame.ThisV.AsObject, _runtime.Realm.UninitializedThisSentinel))
                            {
                                throw new JsThrow(_runtime.Realm.NewReferenceError(
                                    "Must call super constructor in derived class before accessing 'this'"));
                            }
                            Push(frame, ref stack, ref sp, ref maxSp, frame.ThisV);
                            break;
                        }
                    case Opcode.LoadNewTarget:
                        {
                            Push(frame, ref stack, ref sp, ref maxSp, frame.NewTarget is null ? JsValue.Undefined : JsValue.Object(frame.NewTarget));
                            break;
                        }
                    case Opcode.BindThis:
                        {
                            // wp:M3-84 Stage B — the derived-ctor bound-this lives
                            // on the frame (was a VM-wide side channel); the frame
                            // pop's construct coercion reads it.
                            frame.ThisV = Pop(stack, ref sp);
                            frame.DerivedThis = frame.ThisV;
                            break;
                        }

                    case Opcode.ToPropertyKey:
                        {
                            // wp:M3-04f — §7.1.19 ToPropertyKey; push the normalized key
                            // back as a Symbol value or a String value. Threads `this`
                            // VM so an object key's Symbol.toPrimitive is honored.
                            var key = AbstractOperations.ToPropertyKey(this, Pop(stack, ref sp));
                            Push(frame, ref stack, ref sp, ref maxSp, key.IsSymbol ? JsValue.Symbol(key.AsSymbol) : JsValue.String(key.AsString));
                            break;
                        }
                    // ----- B1b-2c — Suspend (yield / await) -----
                    case Opcode.Suspend:
                        {
                            var kind = code[ip++];
                            var yielded = Pop(stack, ref sp);
                            if (frame.Suspension is null)
                            {
                                // Outside a suspendable context — yield/await are
                                // syntax errors but we accept liberally; surface
                                // the misuse as a SyntaxError at runtime.
                                throw new JsThrow(_runtime.Realm.NewSyntaxError(
                                    kind == 1
                                        ? "await is only valid in async functions and async generators"
                                        : "yield is only valid in generator functions"));
                            }
                            if (kind == 0 && frame.CurrentFunction?.Kind == JsFunctionKind.AsyncGenerator)
                            {
                                // §27.6.3.8 AsyncGeneratorYield step 1: a plain `yield x`
                                // inside an async generator must `Await(x)` before the
                                // result is delivered to the pending request. The resume
                                // prelude turns the await fulfilment into the visible yield.
                                return FlushAndSuspend(frame, ip, sp, maxSp,
                                    ContinuationResumeAction.AsyncGeneratorYieldAwait,
                                    yielded,
                                    kind: 1);
                            }

                            return FlushAndSuspend(frame, ip, sp, maxSp,
                                ContinuationResumeAction.PushResume, yielded, kind);
                        }
                    case Opcode.PrologueEnd:
                        {
                            // §10.2.1.3 — the parameter-binding prologue has run
                            // synchronously. Hand off to the
                            // caller (Start{Generator,Async,AsyncGenerator}Body) so it
                            // can observe a prologue throw before producing the
                            // generator/promise. No value travels across this boundary;
                            // the body resumes here on the first real next()/drive.
                            // If suspension is null (defensive), it's a no-op.
                            if (frame.Suspension is not null)
                            {
                                return FlushAndSuspend(frame, ip, sp, maxSp,
                                    ContinuationResumeAction.IgnoreResume,
                                    JsValue.Undefined,
                                    kind: 0);
                            }

                            break;
                        }
                    case Opcode.YieldDelegate:
                        {
                            // Out-of-line for frame size; true means the inner
                            // iterator parked this frame — return without
                            // releasing the pooled stack (the snapshot owns it).
                            if (ExecYieldDelegate(frame, ref stack, ref ip, ref sp, ref maxSp, out var ydParked))
                            {
                                return ydParked;
                            }

                            break;
                        }

                    default:
                        // Cold opcode — dispatched out-of-line so its arm's
                        // locals don't enlarge this frame (see DispatchCold).
                        if (!DispatchCold(op, frame, ref stack, locals, ref ip, ref sp, ref maxSp))
                        {
                            throw new InvalidOperationException($"opcode {op} not implemented in VM");
                        }
                        // wp:M3-84 Stage B — CallSuperCtor (inside DispatchCold)
                        // may have pushed a trampolined callee frame; detect the
                        // switch and reload the hot cache.
                        if (!ReferenceEquals(t_current, frame))
                        {
                            frame = t_current!;
                            LoadFrameCache(frame, ref chunk, ref stack, ref locals, ref code, ref constants, ref ip, ref sp, ref maxSp);
                        }
                        break;
                }
            }
            catch (JsThrow ex)
            {
                // wp:M3-84 Stage B — explicit multi-frame unwind. Walk the
                // frame chain from the current frame toward the barrier: for
                // each frame, record its JS stack entry (same order the old
                // per-native-frame catch produced), try its try-stack (the
                // TryBody→catch / finally-as-PendingCompletion.Throw logic is
                // verbatim Stage A), and if unhandled release the frame's
                // pooled arrays and step to its caller. At the barrier frame
                // the JsThrow rethrows natively to the barrier's caller.
                while (true)
                {
                    CaptureJsStack(ex, chunk, ip, frame.CurrentFunction);
                    JsValue thrown = ex.Value;
                    bool handled = false;
                    // Null TryStack = no try ever entered in this frame.
                    var tryStack = frame.TryStack;
                    while (tryStack is not null && tryStack.Count > 0)
                    {
                        var tf = tryStack.Peek();
                        if (tf.Phase == TryPhase.TryBody && tf.CatchPc != -1)
                        {
                            // Route through Push: StackBase can equal the
                            // current array length (try entered with a full
                            // operand stack), in which case this push grows.
                            sp = tf.StackBase;
                            Push(frame, ref stack, ref sp, ref maxSp, thrown);
                            ip = tf.CatchPc;
                            tf.Phase = TryPhase.CatchBody;
                            tryStack.Pop(); tryStack.Push(tf);
                            handled = true;
                            break;
                        }
                        if (tf.Phase != TryPhase.RunningFinally && tf.FinallyPc != -1)
                        {
                            sp = tf.StackBase;
                            tf.Phase = TryPhase.RunningFinally;
                            tf.Pending = PendingCompletion.Throw;
                            tf.PendingValue = thrown;
                            tryStack.Pop(); tryStack.Push(tf);
                            ip = tf.FinallyPc;
                            handled = true;
                            break;
                        }
                        tryStack.Pop();
                    }
                    if (handled)
                    {
                        break;
                    }
                    // Unhandled in this frame — release it. A barrier frame
                    // rethrows to the native caller; a trampolined frame
                    // unwinds into its JS caller and keeps walking.
                    ReleaseFrame(frame, stack, maxSp);
                    if (frame.IsBarrier)
                    {
                        rethrow = ex;
                        break;
                    }
                    frame = frame.Caller!;
                    t_current = frame;
                    LoadFrameCache(frame, ref chunk, ref stack, ref locals, ref code, ref constants, ref ip, ref sp, ref maxSp);
                }
            }
            catch (JsReturnSentinel rs)
            {
                // Generator.return(v) injected at a suspension point —
                // walk enclosing try/finally frames as a Return completion
                // (mirrors DivertReturnThroughFinally for the synchronous
                // Return opcode). If nothing diverts it, exit the body
                // with rs.Value as the return value. The sentinel only fires
                // on a frame with a live Suspension, which is always this
                // dispatch's barrier frame (trampolined callees never suspend).
                if (!DivertReturnThroughFinally(frame.TryStack, rs.Value, ref ip))
                {
                    ReleaseFrame(frame, stack, maxSp);
                    return rs.Value;
                }
            }
            if (rethrow is not null)
            {
                throw rethrow;
            }
        }
    }


    /// <summary>wp:M3-84 Stage A — cold opcode arms moved out of the dispatch loop.
    /// RyuJIT gives every IL local in the dispatch method a distinct stack slot (the
    /// method is too large for slot sharing), so keeping these arms' ~280
    /// locals inline cost ~7 KB of native stack on every JS call frame. This
    /// method's frame is transient — it returns before any JS->JS recursion
    /// continues — so its size does not multiply with JS call depth.
    /// NoInlining is load-bearing: inlining would put the locals right back
    /// into the dispatch frame. Returns false when the opcode is not handled
    /// here (the dispatch loop then reports the unimplemented opcode).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool DispatchCold(Opcode op, CallFrame frame, ref JsValue[] stack, JsValue[] locals,
        ref int ip, ref int sp, ref int maxSp)
    {
        var chunk = frame.Chunk;
        var code = frame.Code;
        var constants = frame.Constants;
        switch (op)
        {
            // wp:M3-72 — direct-eval caller-scope read. Resolve a free
            // identifier (matching a caller binding name) against the live
            // caller frame, falling back to a checked global load on a miss.
            case Opcode.LoadEvalScope:
                {
                    var idx = ReadU16(code, ref ip);
                    var name = (string)constants[idx]!;
                    _lastLoadName = name;
                    if (frame.EvalScope is not null && frame.EvalScope.TryGet(name, out var entry))
                    {
                        var v = entry.Read();
                        // §13.3.1.1 — reading a caller lexical binding still in
                        // its TDZ throws ReferenceError.
                        if (v.IsObject && ReferenceEquals(v.AsObject, _runtime.Realm.TdzSentinel))
                        {
                            throw new JsThrow(_runtime.Realm.NewReferenceError(
                                "Cannot access '" + name + "' before initialization"));
                        }

                        Push(frame, ref stack, ref sp, ref maxSp, v);
                        break;
                    }
                    var realm = _runtime.Realm;
                    var globalObj = realm.GlobalObject;
                    if (!globalObj.Has(name))
                    {
                        if (realm.ThrowOnUnresolvedGlobalRead && !realm.LenientGlobalNames.Contains(name))
                        {
                            throw new JsThrow(realm.NewReferenceError(name + " is not defined"));
                        }

                        Push(frame, ref stack, ref sp, ref maxSp, JsValue.Undefined);
                        break;
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, AbstractOperations.Get(this, globalObj, name, JsValue.Object(globalObj)));
                    break;
                }
            // wp:M3-72 — direct-eval caller-scope write. Write through the
            // live caller binding, else fall back to a global store.
            case Opcode.StoreEvalScope:
                {
                    var idx = ReadU16(code, ref ip);
                    var name = (string)constants[idx]!;
                    var value = Pop(stack, ref sp);
                    if (frame.EvalScope is not null && frame.EvalScope.TryGet(name, out var entry))
                    {
                        if (entry.IsConst)
                        {
                            throw new JsThrow(_runtime.Realm.NewTypeError(
                                "Assignment to constant variable '" + name + "'"));
                        }

                        entry.Write(value);
                        break;
                    }
                    var globalObj = _runtime.Realm.GlobalObject;
                    if (frame.FrameStrict && !globalObj.Has(name))
                    {
                        throw new JsThrow(_runtime.Realm.NewReferenceError(name + " is not defined"));
                    }

                    var ok2 = AbstractOperations.Set(this, globalObj, name, value, JsValue.Object(globalObj));
                    if (!ok2 && frame.FrameStrict)
                    {
                        throw new JsThrow(_runtime.Realm.NewTypeError(
                            "Cannot assign to read-only property '" + name + "'"));
                    }

                    break;
                }
            // gap:script-top-var-not-global — idempotent CreateGlobalVarBinding
            // (§16.1.7 / §9.1.1.4.16). Skip if the global already has an own
            // property of this name (function-decl hoist may have installed
            // it first, or this is the second `var x` of a redeclaration);
            // otherwise install an own data property seeded with undefined.
            case Opcode.DeclareGlobalVar:
                {
                    var idx = ReadU16(code, ref ip);
                    var name = (string)constants[idx]!;
                    var globalObj = _runtime.Realm.GlobalObject;
                    if (!globalObj.HasOwn(name))
                    {
                        globalObj.DefineOwnProperty(name,
                            PropertyDescriptor.Data(JsValue.Undefined,
                                writable: true, enumerable: true, configurable: false));
                    }
                    break;
                }
            // wp:M3-73 — §19.2.1.3 EvalDeclarationInstantiation (non-global
            // branch). Idempotent pre-declaration of an eval-body top-level
            // var/function name into the CALLER frame's eval-introduced var
            // store (frameVarStore is the caller's store while running eval'd
            // code). Re-declaring an existing binding has no effect.
            case Opcode.DeclareEvalVar:
                {
                    var idx = ReadU16(code, ref ip);
                    var name = (string)constants[idx]!;
                    frame.FrameVarStore?.Declare(name);
                    break;
                }
            // wp:M3-73 — set an eval-introduced binding (created by
            // DeclareEvalVar): a var initializer's value or a hoisted
            // function declaration's function object.
            case Opcode.StoreEvalVar:
                {
                    var idx = ReadU16(code, ref ip);
                    var name = (string)constants[idx]!;
                    var value = Pop(stack, ref sp);
                    frame.FrameVarStore?.Set(name, value);
                    break;
                }
            // wp:M3-73 — `delete name` where the name may be an
            // eval-introduced binding. Such bindings are configurable
            // (§19.2.1.3), so remove it from the store and push true; if the
            // name isn't there this is the ordinary sloppy identifier-delete
            // no-op (still true). Deletes only at the store's OWN level — an
            // enclosing function's binding (parent store) is not in scope to
            // delete from here.
            case Opcode.DeleteEvalVar:
                {
                    var idx = ReadU16(code, ref ip);
                    var name = (string)constants[idx]!;
                    frame.FrameVarStore?.Delete(name);
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(true));
                    break;
                }
            // wp:M3-26 — object-literal accessor (getter/setter) shorthand.
            // Reuse the class-member accessor installer so paired get/set on
            // the same key share one descriptor. Object-literal accessors are
            // enumerable (§13.2.5), unlike class accessors.
            case Opcode.DefineGetter:
            case Opcode.DefineSetter:
                {
                    var idx = ReadU16(code, ref ip);
                    var name = (string)constants[idx]!;
                    var fnVal = Pop(stack, ref sp);
                    var obj = Pop(stack, ref sp);
                    InstallObjectAccessor(obj.AsObject, JsPropertyKey.String(name),
                        isGetter: op == Opcode.DefineGetter, (JsFunction)fnVal.AsObject);
                    Push(frame, ref stack, ref sp, ref maxSp, obj);
                    break;
                }
            case Opcode.DefineGetterComputed:
            case Opcode.DefineSetterComputed:
                {
                    var fnVal = Pop(stack, ref sp);
                    var key = Pop(stack, ref sp);
                    var obj = Pop(stack, ref sp);
                    InstallObjectAccessor(obj.AsObject, AbstractOperations.ToPropertyKey(this, key),
                        isGetter: op == Opcode.DefineGetterComputed, (JsFunction)fnVal.AsObject);
                    Push(frame, ref stack, ref sp, ref maxSp, obj);
                    break;
                }
            // wp:M3-26 — CreateDataPropertyOrThrow (§7.3.5): define an own
            // enumerable/writable/configurable data property, replacing any
            // existing accessor or data descriptor on the key.
            case Opcode.DefineDataProperty:
                {
                    var idx = ReadU16(code, ref ip);
                    var name = (string)constants[idx]!;
                    var value = Pop(stack, ref sp);
                    var obj = Pop(stack, ref sp);
                    obj.AsObject.DefineOwnProperty(name,
                        PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true));
                    Push(frame, ref stack, ref sp, ref maxSp, obj);
                    break;
                }
            case Opcode.DefineDataComputed:
                {
                    var value = Pop(stack, ref sp);
                    var key = Pop(stack, ref sp);
                    var obj = Pop(stack, ref sp);
                    obj.AsObject.DefineOwnProperty(AbstractOperations.ToPropertyKey(this, key),
                        PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true));
                    Push(frame, ref stack, ref sp, ref maxSp, obj);
                    break;
                }
            case Opcode.SetObjectPrototype:
                {
                    var value = Pop(stack, ref sp);
                    var obj = Pop(stack, ref sp);
                    if (value.IsObject)
                    {
                        obj.AsObject.SetPrototypeOf(value.AsObject);
                    }
                    else if (value.IsNull)
                    {
                        obj.AsObject.SetPrototypeOf(null);
                    }

                    Push(frame, ref stack, ref sp, ref maxSp, obj);
                    break;
                }
            // wp:M3-64 — §13.2.5 MakeMethod. Stack: [obj, fn]. Stamp the
            // method's [[HomeObject]] = the object literal being built so
            // `super.x` inside it resolves against the object's prototype.
            // Peek (do not pop) so the stack stays [obj, fn] for the Define
            // opcode that follows.
            case Opcode.SetHomeObject:
                {
                    var fnVal = stack[sp - 1];
                    var objVal = stack[sp - 2];
                    if (fnVal.IsObject && fnVal.AsObject is JsFunction methodFn
                        && objVal.IsObject)
                    {
                        methodFn.HomeObject = objVal.AsObject;
                    }
                    break;
                }
            // wp:M3-64 — computed-key variant: stack is [obj, key, fn].
            case Opcode.SetHomeObjectComputed:
                {
                    var fnVal = stack[sp - 1];
                    var objVal = stack[sp - 3];
                    if (fnVal.IsObject && fnVal.AsObject is JsFunction methodFn
                        && objVal.IsObject)
                    {
                        methodFn.HomeObject = objVal.AsObject;
                    }
                    break;
                }
            case Opcode.DirectEval:
                {
                    // wp:M3-71/72 — §19.2.1.1 PerformEval (direct path). The
                    // compiler emitted this for a bare-`eval` call resolving to
                    // the global slot, with a u16 EvalScopeDescriptor index of the
                    // calling function's variable environment followed by the u8
                    // argc. Confirm the callee is STILL the realm intrinsic; if it
                    // was reassigned (or is otherwise not the intrinsic), fall back
                    // to an ordinary indirect call with this=undefined.
                    var descIdx = ReadU16(code, ref ip);
                    var argc = code[ip++];
                    var callArgs = new JsValue[argc];
                    for (var i = argc - 1; i >= 0; i--)
                    {
                        callArgs[i] = Pop(stack, ref sp);
                    }

                    var callee = Pop(stack, ref sp);
                    var intrinsic = _runtime.Realm.EvalFunction;
                    if (intrinsic is not null && callee.IsObject
                        && ReferenceEquals(callee.AsObject, intrinsic))
                    {
                        // wp:M3-72 — pair the compile-time descriptor with the live
                        // frame storage so the eval'd code reads/writes the
                        // caller's actual bindings.
                        var descriptor = (Bytecode.EvalScopeDescriptor)constants[descIdx]!;
                        var callerScope = BuildEvalScope(descriptor, locals, frame.Upvalues);
                        // The eval scope references this frame's locals ARRAY by
                        // (array, slot). Today the scope dies when the eval barrier
                        // pops (nothing captures it), but keep this frame's locals
                        // out of the pool so any future holder of the scope can
                        // never read another frame's recycled slots.
                        frame.LocalsEscaped = true;
                        // wp:M3-73 — a non-strict direct eval whose caller is a
                        // function injects its own top-level var/function bindings
                        // into the caller frame's eval-introduced var store. Pass
                        // it by ref so PerformDirectEval can create it lazily and
                        // the rest of THIS frame then resolves those names too.
                        Push(frame, ref stack, ref sp, ref maxSp, PerformDirectEval(callArgs, callerScope, frame.CurrentFunction, frame.ThisV,
                            frame.NewTarget, frame.FrameStrict, inInitializer: frame.InitDepth > 0,
                            ref frame.FrameVarStore));
                        break;
                    }
                    if (!IsCallableValue(callee))
                    {
                        throw new JsThrow(JsValue.String(AtPos(chunk, ip, $"not a function: {JsValue.ToStringValue(callee)} (callee hint: 'eval')")));
                    }

                    Push(frame, ref stack, ref sp, ref maxSp, AbstractOperations.Call(this, callee, JsValue.Undefined, callArgs));
                    break;
                }
            case Opcode.LoadRegExp:
                {
                    var srcIdx = ReadU16(code, ref ip);
                    var flagsIdx = ReadU16(code, ref ip);
                    var regexCacheId = ReadU16(code, ref ip);
                    // Per-site cache: a regex literal compiles once and the matcher
                    // is reused across re-evaluations (e.g. in a loop), avoiding even
                    // the (source, flags) dictionary lookup on the hot path. A fresh
                    // JsRegExp wrapper is still created each time so each evaluation
                    // gets an independent `lastIndex` slot, as the spec requires.
                    var compiled = chunk.RegexLiterals[regexCacheId];
                    if (compiled is null)
                    {
                        var source = (string)constants[srcIdx]!;
                        var flagsStr = (string)constants[flagsIdx]!;
                        if (!RegexFlagParser.TryParse(flagsStr, out var flags, out var flagErr))
                        {
                            throw new JsThrow(_runtime.Realm.NewSyntaxError(flagErr!));
                        }

                        try
                        {
                            compiled = Starling.Js.Runtime.Regex.RegexBackendSelector.CompileCached(source, flags);
                        }
                        catch (RegexSyntaxException ex)
                        {
                            throw new JsThrow(_runtime.Realm.NewSyntaxError(
                                $"Invalid regular expression: /{source}/: {ex.Message}"));
                        }
                        chunk.RegexLiterals[regexCacheId] = compiled;
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(new JsRegExp(_runtime.Realm, compiled)));
                    break;
                }
            case Opcode.TemplateObject:
                {
                    var tmpl = (TemplateObjectTemplate)constants[ReadU16(code, ref ip)]!;
                    var cache = _runtime.Realm.TemplateObjectCache;
                    if (!cache.TryGetValue(tmpl, out var strings))
                    {
                        strings = BuildTemplateObject(tmpl);
                        cache[tmpl] = strings;
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(strings));
                    break;
                }
            case Opcode.ThrowConstAssignment:
                {
                    // §16.2.1.6.2 — an assignment to an immutable binding (a
                    // module's imported binding) is a runtime TypeError. Discard
                    // the would-be assigned value, then throw.
                    var nameIdx = ReadU16(code, ref ip);
                    var name = (string)constants[nameIdx]!;
                    Pop(stack, ref sp);
                    throw new JsThrow(_runtime.Realm.NewTypeError(
                        $"Assignment to constant variable '{name}'."));
                }
            case Opcode.DeleteProperty:
                {
                    var key = Pop(stack, ref sp);
                    var receiver = Pop(stack, ref sp);
                    if (!receiver.IsObject)
                    {
                        // §13.5.1: ToObject for primitives so we can delete keys
                        // on a wrapper — wrappers report success since no own
                        // properties exist matching the key. For null/undefined
                        // the spec throws TypeError.
                        if (receiver.IsNullish)
                        {
                            throw new JsThrow(_runtime.Realm.NewTypeError(
                                "Cannot convert undefined or null to object"));
                        }

                        var boxed = AbstractOperations.ToObject(_runtime.Realm, receiver);
                        Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(boxed.Delete(AbstractOperations.ToPropertyKey(this, key))));
                        break;
                    }
                    var delKey = AbstractOperations.ToPropertyKey(this, key);
                    var deleted = receiver.AsObject.Delete(delKey);
                    // §13.5.1.2 — in strict code, `delete` of a non-configurable
                    // own property is a TypeError (sloppy returns false instead).
                    if (!deleted && frame.FrameStrict)
                    {
                        throw new JsThrow(_runtime.Realm.NewTypeError(
                            "Cannot delete property '" + delKey + "'"));
                    }

                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(deleted));
                    break;
                }
            case Opcode.SetFunctionName:
                {
                    // §named-evaluation — stamp an inferred name onto an anonymous
                    // function/class produced as the value of a binding/assignment
                    // initializer. The compiler only emits this when the RHS is a
                    // syntactically anonymous function definition, so we just guard
                    // on the current `name` still being "" (so a named expression
                    // or a non-function value is never clobbered).
                    var nameIdx = ReadU16(code, ref ip);
                    StampInferredFunctionName(stack[sp - 1], (string)constants[nameIdx]!);
                    break;
                }
            case Opcode.SetFunctionNameComputed:
                {
                    var target = stack[sp - 1];
                    var keyValue = stack[sp - 2];
                    var key = keyValue.IsSymbol
                        ? JsPropertyKey.Symbol(keyValue.AsSymbol)
                        : JsPropertyKey.String(JsValue.ToStringValue(keyValue));
                    StampInferredFunctionName(target, FunctionNameFromPropertyKey(key));
                    break;
                }
            case Opcode.SpreadInto:
                {
                    var src = Pop(stack, ref sp);
                    var dst = Pop(stack, ref sp);
                    if (src.IsObject && dst.IsObject)
                    {
                        var srcObj = src.AsObject;
                        var dstObj = dst.AsObject;
                        // CopyDataProperties (§7.3.27) invokes getters on the source,
                        // not the data-only fast path. Mirror that here so accessor
                        // properties are spread by their getter's return value.
                        foreach (var key in srcObj.EnumerableKeys())
                        {
                            AbstractOperations.Set(this, dstObj, key,
                                AbstractOperations.Get(this, srcObj, key));
                        }

                        foreach (var key in srcObj.EnumerableSymbolKeys())
                        {
                            AbstractOperations.Set(this, dstObj, JsPropertyKey.Symbol(key),
                                AbstractOperations.Get(this, srcObj, JsPropertyKey.Symbol(key)));
                        }
                    }
                    break;
                }
            case Opcode.RestArray:
                {
                    var start = ReadU16(code, ref ip);
                    var src = Pop(stack, ref sp);
                    // B2-4: rest-array binding now produces a real JsArray.
                    var result = new JsArray(_runtime.Realm);
                    var srcObj = src.IsObject ? src.AsObject : (!src.IsNullish ? AbstractOperations.ToObject(_runtime.Realm, src) : null);
                    var len = 0;
                    if (srcObj is not null)
                    {
                        len = Math.Max(0, (int)Math.Truncate(JsValue.ToNumber(
                            AbstractOperations.Get(this, srcObj, "length"))));
                    }

                    if (srcObj is not null)
                    {
                        for (var i = start; i < len; i++)
                        {
                            result.Push(AbstractOperations.Get(this, srcObj,
                                i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                        }
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(result));
                    break;
                }
            case Opcode.IteratorClose:
                {
                    var handleV = Pop(stack, ref sp);
                    if (handleV.IsObject && handleV.AsObject is Starling.Js.Intrinsics.JsIteratorRecordHandle h)
                    {
                        if (!h.Record.Done)
                        {
                            AbstractOperations.IteratorClose(this, h.Record, isThrowing: false);
                        }
                    }
                    break;
                }
            case Opcode.IteratorRest:
                {
                    // §8.5.3 BindingRestElement — collect every remaining value
                    // into a fresh dense array, driving the iterator to Done.
                    var top = stack[sp - 1];
                    if (!top.IsObject || top.AsObject is not Starling.Js.Intrinsics.JsIteratorRecordHandle handle)
                    {
                        throw new InvalidOperationException("IteratorRest expects an iterator-record handle on the stack");
                    }

                    var rest = new JsArray(_runtime.Realm);
                    var n = 0;
                    while (!handle.Record.Done)
                    {
                        var step = AbstractOperations.IteratorStep(_runtime.Realm, this, ref handle.Record);
                        if (step is null)
                        {
                            break;
                        }

                        rest.Set(n.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            AbstractOperations.IteratorValue(this, step.Value));
                        n++;
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(rest));
                    break;
                }
            case Opcode.IteratorCloseForThrow:
                {
                    // §7.4.10 IteratorClose in a throwing completion: invoke
                    // return() but swallow any error it raises so the original
                    // (in-flight) throw is the one that propagates.
                    var handleV = Pop(stack, ref sp);
                    if (handleV.IsObject && handleV.AsObject is Starling.Js.Intrinsics.JsIteratorRecordHandle h
                        && !h.Record.Done)
                    {
                        AbstractOperations.IteratorClose(this, h.Record, isThrowing: true);
                    }
                    break;
                }
            case Opcode.IteratorCloseFinally:
                {
                    // §7.4.8 / §14.7.5.6 — the for-of synthetic finalizer. The
                    // for-of frame is on top of the try-stack with its pending
                    // completion set (LeaveTry/Divert pushed it back before
                    // jumping here). Close iff that completion is abrupt; on a
                    // Normal pending completion (ordinary body finish OR a
                    // `continue` to this loop) leave the iterator open so the
                    // next iteration re-steps it. Swallow return()-errors only
                    // for a pending Throw so the in-flight throw still wins.
                    var handleV = Pop(stack, ref sp);
                    var pending = frame.TryStack is { Count: > 0 } ts ? ts.Peek().Pending : PendingCompletion.Normal;
                    if (pending != PendingCompletion.Normal
                        && handleV.IsObject
                        && handleV.AsObject is Starling.Js.Intrinsics.JsIteratorRecordHandle hf
                        && !hf.Record.Done)
                    {
                        AbstractOperations.IteratorClose(
                            this, hf.Record, isThrowing: pending == PendingCompletion.Throw);
                    }
                    break;
                }
            case Opcode.GetAsyncIterator:
                {
                    // §7.4.2 GetIterator(obj, async). Resolve
                    // obj[@@asyncIterator]; if absent, fall back to the sync
                    // iterator wrapped as async (CreateAsyncFromSyncIterator).
                    var iterable = Pop(stack, ref sp);
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(GetAsyncIteratorHandle(iterable)));
                    break;
                }
            case Opcode.AsyncIteratorNext:
                {
                    // Peek the record handle (loop keeps it across iterations).
                    var top = stack[sp - 1];
                    if (!top.IsObject || top.AsObject is not Starling.Js.Intrinsics.JsIteratorRecordHandle handle)
                    {
                        throw new InvalidOperationException("AsyncIteratorNext expects an async-iterator-record handle");
                    }

                    var resultV = AbstractOperations.Call(this, handle.Record.NextMethod,
                        handle.Record.Iterator, Array.Empty<JsValue>());
                    if (handle.SyncWrapped)
                    {
                        // §27.1.4.2.1 CreateAsyncFromSyncIterator: await the
                        // sync result's `value` (it may itself be a thenable),
                        // then rebuild {value: awaited, done} so the loop's
                        // following await observes a fully-settled element.
                        Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(WrapSyncIteratorResult(resultV)));
                    }
                    else
                    {
                        // Async iterator: next() already returns a promise.
                        Push(frame, ref stack, ref sp, ref maxSp, resultV);
                    }
                    break;
                }
            case Opcode.AsyncIteratorClose:
                {
                    var handleV = Pop(stack, ref sp);
                    if (handleV.IsObject && handleV.AsObject is Starling.Js.Intrinsics.JsIteratorRecordHandle h
                        && !h.Record.Done)
                    {
                        var ret = AbstractOperations.GetMethod(this, h.Record.Iterator, "return");
                        if (!ret.IsUndefined && !ret.IsNull)
                        {
                            var rv = AbstractOperations.Call(this, ret, h.Record.Iterator,
                                Array.Empty<JsValue>());
                            // AsyncIteratorClose awaits the return result; the
                            // following Suspend(kind=1) does the await. For a
                            // sync-wrapped iterator the return value isn't a
                            // promise — resolve it so the await is uniform.
                            if (h.SyncWrapped)
                            {
                                var p = new JsPromise(_runtime.Realm.PromisePrototype);
                                PromiseCtor.Resolve(_runtime.Realm, p, rv);
                                Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(p));
                            }
                            else
                            {
                                Push(frame, ref stack, ref sp, ref maxSp, rv);
                            }
                            break;
                        }
                    }
                    // No return method (or already done) — push undefined so
                    // the unconditional await downstream is a no-op.
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Undefined);
                    break;
                }
            case Opcode.EnumerateKeys:
                {
                    // §14.7.5.10 ForIn/OfHeadEvaluation step 6: for-in
                    // snapshots own + inherited enumerable string keys at
                    // loop entry. Null/undefined silently skip the body
                    // (spec: return an empty iterator).
                    var src = Pop(stack, ref sp);
                    var snapshot = new JsArray(_runtime.Realm);
                    if (!src.IsNullish)
                    {
                        var obj = AbstractOperations.ToObject(_runtime.Realm, src);
                        var emitted = new HashSet<string>(StringComparer.Ordinal);
                        var shadowed = new HashSet<string>(StringComparer.Ordinal);
                        // §10.1.11.1 OrdinaryOwnPropertyKeys ordering: integer
                        // ("array index") keys first, in ascending numeric order,
                        // then the remaining string keys in insertion order.
                        // Integer keys are collected across the whole prototype
                        // chain (deduped) and sorted; string keys keep their
                        // per-level insertion order (own object first, then up
                        // the chain).
                        var intKeys = new SortedDictionary<uint, string>();
                        var strKeys = new List<string>();
                        var current = obj;
                        while (current is not null)
                        {
                            foreach (var k in current.EnumerableKeys())
                            {
                                if (shadowed.Contains(k))
                                {
                                    continue;
                                }

                                if (!emitted.Add(k))
                                {
                                    continue;
                                }

                                if (JsArray.IsArrayIndex(k, out var idx))
                                {
                                    intKeys[idx] = k;
                                }
                                else
                                {
                                    strKeys.Add(k);
                                }
                            }
                            // Any own key (enumerable or not) on this level
                            // shadows same-named keys further up the proto
                            // chain — per OrdinaryOwnPropertyKeys, all own
                            // names appear regardless of enumerability.
                            foreach (var k in current.Keys)
                            {
                                shadowed.Add(k);
                            }

                            current = current.Prototype;
                        }
                        foreach (var pair in intKeys)
                        {
                            snapshot.Push(JsValue.String(pair.Value));
                        }

                        foreach (var k in strKeys)
                        {
                            snapshot.Push(JsValue.String(k));
                        }
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(snapshot));
                    break;
                }
            case Opcode.SpreadIterable:
                {
                    // Stack: [target, iterable] -> [target] with target's
                    // dense backing extended by iterable's values.
                    var iterable = Pop(stack, ref sp);
                    var targetV = stack[sp - 1];
                    if (!targetV.IsObject || targetV.AsObject is not JsArray targetArr)
                    {
                        throw new InvalidOperationException("SpreadIterable target must be a JsArray");
                    }

                    var record = AbstractOperations.GetIterator(_runtime.Realm, this, iterable);
                    while (true)
                    {
                        var step = AbstractOperations.IteratorStep(_runtime.Realm, this, ref record);
                        if (step is null)
                        {
                            break;
                        }

                        targetArr.Push(AbstractOperations.IteratorValue(this, step.Value));
                    }
                    break;
                }
            case Opcode.RestObject:
                {
                    var excludedCount = ReadU16(code, ref ip);
                    var excluded = new HashSet<string>(StringComparer.Ordinal);
                    for (var i = 0; i < excludedCount; i++)
                    {
                        var key = AbstractOperations.ToPropertyKey(this, Pop(stack, ref sp));
                        if (!key.IsSymbol)
                        {
                            excluded.Add(key.AsString);
                        }
                    }
                    var src = Pop(stack, ref sp);
                    var result = _runtime.Realm.NewOrdinaryObject();
                    // CopyDataProperties (§7.3.27) — accessor getters on the
                    // source must be invoked, not bypassed by the data-only
                    // fast path. Route through AbstractOperations.Get.
                    if (src.IsObject)
                    {
                        var srcObj = src.AsObject;
                        foreach (var key in srcObj.EnumerableKeys())
                        {
                            if (!excluded.Contains(key))
                            {
                                AbstractOperations.Set(this, result, key,
                                    AbstractOperations.Get(this, srcObj, key));
                            }
                        }
                    }
                    else if (!src.IsNullish)
                    {
                        var srcObj = AbstractOperations.ToObject(_runtime.Realm, src);
                        foreach (var key in srcObj.EnumerableKeys())
                        {
                            if (!excluded.Contains(key))
                            {
                                AbstractOperations.Set(this, result, key,
                                    AbstractOperations.Get(this, srcObj, key));
                            }
                        }
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(result));
                    break;
                }
            case Opcode.LoadHomeObject:
                {
                    if (frame.CurrentFunction?.HomeObject is null)
                    {
                        throw new JsThrow(_runtime.Realm.NewSyntaxError(
                            "'super' keyword unexpected here"));
                    }

                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(frame.CurrentFunction.HomeObject));
                    break;
                }
            case Opcode.DynamicImport:
                {
                    // wp:M3-03c — §13.3.10 import(specifier). Hand the specifier
                    // value + this chunk's referrer URL to the active loader and
                    // push the resulting Promise. Every load/eval failure is a
                    // rejection inside ImportDynamic — the only synchronous throw
                    // is when no loader is wired into the realm.
                    // wp:M3-63 — the referrer is the ACTIVE script/module's source
                    // path (SourcePath), which is identical across all nested
                    // functions, NOT the running function's own chunk Name (which
                    // for a nested async/arrow/generator is its function name, not
                    // the script path — so a relative specifier would otherwise
                    // resolve against the cwd). Fall back to Name for the
                    // top-level chunk whose Name already is the path.
                    var spec = Pop(stack, ref sp);
                    var loader = _runtime.Realm.ModuleLoader
                        ?? throw new JsThrow(_runtime.Realm.NewTypeError(
                            "dynamic import() is not supported in this context (no module loader)"));
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(loader.ImportDynamic(spec, chunk.SourcePath ?? chunk.Name)));
                    break;
                }
            case Opcode.LoadImportMeta:
                {
                    // wp:M3-03c — §13.3.12 import.meta. The active module's
                    // resolved URL is its SourcePath (identical across nested
                    // functions); ask the loader for that module's lazily-built
                    // meta object. wp:M3-63 — use SourcePath so import.meta.url
                    // stays consistent inside nested functions; fall back to Name.
                    var loader = _runtime.Realm.ModuleLoader;
                    var meta = loader?.ResolveMetaForUrl(chunk.SourcePath ?? chunk.Name);
                    if (meta is null)
                    {
                        throw new JsThrow(_runtime.Realm.NewSyntaxError(
                            "import.meta is only valid inside a module"));
                    }

                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(meta));
                    break;
                }
            case Opcode.RestParam:
                {
                    // §10.2.11 — collect the rest parameter's arguments
                    // (args[start..argc)) into a real dense array. Works in
                    // arrows too: reads the frame's `args` directly rather than
                    // the (arrow-absent) `arguments` object.
                    var start = ReadU16(code, ref ip);
                    var rest = new JsArray(_runtime.Realm);
                    for (var i = start; i < frame.Args.Length; i++)
                    {
                        rest.Push(frame.Args[i]);
                    }

                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(rest));
                    break;
                }
            case Opcode.MakeArguments:
                {
                    // §10.4.4 — materialize the callee's `arguments` object from
                    // this frame's received args and bind it into `slot`. If the
                    // slot was pre-initialized to a Cell (because a nested arrow
                    // captures `arguments`), write through the cell so the
                    // closure observes the same object; otherwise store directly.
                    var slot = ReadU16(code, ref ip);
                    var argObj = JsValue.Object(_runtime.Realm.CreateArgumentsObject(frame.Args, frame.FrameStrict));
                    if (locals[slot].IsObject && locals[slot].AsObject is Cell cell)
                    {
                        cell.Value = argObj;
                    }
                    else
                    {
                        locals[slot] = argObj;
                    }

                    break;
                }
            case Opcode.MakeMappedArguments:
                {
                    // §10.4.4.6 — build the mapped arguments object, live-linking
                    // each parameter index to its local slot in THIS frame so
                    // arguments[i] ⇄ parameter i. Must run after parameter binding
                    // (including PromoteParamCell) so a captured parameter's slot
                    // already holds its Cell and the map shares it.
                    var slot = ReadU16(code, ref ip);
                    var paramCount = ReadU16(code, ref ip);
                    var slotForIndex = new int[paramCount];
                    for (var i = 0; i < paramCount; i++)
                    {
                        var ps = ReadU16(code, ref ip);
                        // §10.4.4.6 — index i is mapped only when its parameter is
                        // the last with that name (compiler marks shadowed dupes
                        // 0xFFFF) AND an argument was actually passed at i.
                        slotForIndex[i] = (ps == 0xFFFF || i >= frame.Args.Length) ? -1 : ps;
                    }
                    // The mapped arguments object holds the locals ARRAY by
                    // reference and the live link survives the frame's return —
                    // this frame's pooled locals must never go back to the pool.
                    frame.LocalsEscaped = true;
                    var argObj = JsValue.Object(_runtime.Realm.CreateMappedArgumentsObject(
                        frame.Args, locals, slotForIndex, frame.CurrentFunction));
                    if (locals[slot].IsObject && locals[slot].AsObject is Cell cell)
                    {
                        cell.Value = argObj;
                    }
                    else
                    {
                        locals[slot] = argObj;
                    }

                    break;
                }
            case Opcode.BindCallee:
                {
                    // wp:M3-21 — §15.2.5. Bind a named function expression's own
                    // name to the executing function instance, so the body can
                    // refer to itself. `currentFunction` IS the callee. If the
                    // slot was pre-initialized to a Cell (a nested closure
                    // captures the name) write through the cell so the closure
                    // observes the same binding; otherwise store directly.
                    var slot = ReadU16(code, ref ip);
                    var calleeVal = frame.CurrentFunction is null
                        ? JsValue.Undefined
                        : JsValue.Object(frame.CurrentFunction);
                    if (locals[slot].IsObject && locals[slot].AsObject is Cell cell)
                    {
                        cell.Value = calleeVal;
                    }
                    else
                    {
                        locals[slot] = calleeVal;
                    }

                    break;
                }
            // ----- with statement (§14.11 / §9.1.1.2) -----
            case Opcode.PushWith:
                {
                    // §14.11.2 — ToObject the head value and install it as an
                    // object Environment Record for the body.
                    var v = Pop(stack, ref sp);
                    var envObj = AbstractOperations.ToObject(_runtime.Realm, v);
                    (frame.WithStack ??= new List<JsObject>()).Add(envObj);
                    break;
                }
            case Opcode.PopWith:
                {
                    if (frame.WithStack is { Count: > 0 })
                    {
                        frame.WithStack.RemoveAt(frame.WithStack.Count - 1);
                    }

                    break;
                }
            case Opcode.WithLoadOrMiss:
                {
                    var name = (string)constants[ReadU16(code, ref ip)]!;
                    var miss = ReadI32(code, ref ip);
                    var obj = FindWithBinding(frame.WithStack, name);
                    if (obj is not null)
                    {
                        _lastLoadName = name;
                        Push(frame, ref stack, ref sp, ref maxSp, AbstractOperations.Get(this, obj, name, JsValue.Object(obj)));
                        ip += miss;
                    }
                    // miss: fall through to the static fallback the compiler emitted.
                    break;
                }
            case Opcode.WithLoadMethodOrMiss:
                {
                    var name = (string)constants[ReadU16(code, ref ip)]!;
                    var miss = ReadI32(code, ref ip);
                    var obj = FindWithBinding(frame.WithStack, name);
                    if (obj is not null)
                    {
                        // §9.1.1.2 WithBaseObject: the call's `this` is the
                        // binding object — push [withObj, fn] for CallMethod.
                        _lastLoadName = name;
                        Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(obj));
                        Push(frame, ref stack, ref sp, ref maxSp, AbstractOperations.Get(this, obj, name, JsValue.Object(obj)));
                        ip += miss;
                    }
                    break;
                }
            case Opcode.WithStoreOrMiss:
                {
                    var name = (string)constants[ReadU16(code, ref ip)]!;
                    var miss = ReadI32(code, ref ip);
                    var obj = FindWithBinding(frame.WithStack, name);
                    if (obj is not null)
                    {
                        var value = Pop(stack, ref sp);
                        var ok = AbstractOperations.Set(this, obj, name, value, JsValue.Object(obj));
                        if (!ok && frame.FrameStrict)
                        {
                            throw new JsThrow(_runtime.Realm.NewTypeError(
                                "Cannot assign to read-only property '" + name + "'"));
                        }

                        ip += miss;
                    }
                    // miss: leave the value on the stack for the static store.
                    break;
                }
            case Opcode.WithDeleteOrMiss:
                {
                    var name = (string)constants[ReadU16(code, ref ip)]!;
                    var miss = ReadI32(code, ref ip);
                    var obj = FindWithBinding(frame.WithStack, name);
                    if (obj is not null)
                    {
                        var ok = obj.Delete(name);
                        if (!ok && frame.FrameStrict)
                        {
                            throw new JsThrow(_runtime.Realm.NewTypeError(
                                "Cannot delete property '" + name + "'"));
                        }

                        Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(ok));
                        ip += miss;
                    }
                    break;
                }
            case Opcode.WithCompoundLoad:
                {
                    // §13.15.2 — resolve the compound-assignment LHS Reference base
                    // ONCE. On a with-binding hit, stash the base object so the
                    // paired WithCompoundStore writes to the SAME object even if
                    // the getter deletes the binding mid-evaluation.
                    var name = (string)constants[ReadU16(code, ref ip)]!;
                    var baseSlot = ReadU16(code, ref ip);
                    var miss = ReadI32(code, ref ip);
                    var obj = FindWithBinding(frame.WithStack, name);
                    if (obj is not null)
                    {
                        locals[baseSlot] = JsValue.Object(obj);
                        _lastLoadName = name;
                        Push(frame, ref stack, ref sp, ref maxSp, AbstractOperations.Get(this, obj, name, JsValue.Object(obj)));
                        ip += miss;
                    }
                    else
                    {
                        // No with-base: mark the slot and fall through to the
                        // statically-compiled fallback load.
                        locals[baseSlot] = JsValue.Undefined;
                    }
                    break;
                }
            case Opcode.WithCompoundStore:
                {
                    var name = (string)constants[ReadU16(code, ref ip)]!;
                    var baseSlot = ReadU16(code, ref ip);
                    var miss = ReadI32(code, ref ip);
                    var captured = locals[baseSlot];
                    if (captured.Kind == JsValueKind.Object)
                    {
                        // Write through the once-resolved Reference base. The
                        // result copy (Dup'd by the compiler) stays beneath.
                        var value = Pop(stack, ref sp);
                        var baseObj = captured.AsObject;
                        var ok = AbstractOperations.Set(this, baseObj, name, value, captured);
                        if (!ok && frame.FrameStrict)
                        {
                            throw new JsThrow(_runtime.Realm.NewTypeError(
                                "Cannot assign to read-only property '" + name + "'"));
                        }

                        ip += miss;
                    }
                    // miss (captured is undefined): leave the value on the stack
                    // for the static store fallback.
                    break;
                }
            case Opcode.CallSuperCtor:
                {
                    var argsArr = Pop(stack, ref sp);
                    var ctorArgs = ExtractApplyArgs(argsArr);
                    // The "super" is the [[Prototype]] of the home object's
                    // [[Prototype]]? Actually for a derived constructor,
                    // home object is the constructor's prototype object.
                    // The super-ctor is the [[Prototype]] of the *constructor*
                    // itself — and currentFunction IS the constructor here.
                    if (frame.CurrentFunction is null)
                    {
                        throw new JsThrow(_runtime.Realm.NewSyntaxError(
                            "'super(...)' may only be used inside a derived class constructor"));
                    }

                    var superCtor = frame.CurrentFunction.Prototype; // [[Prototype]] of the function
                    if (superCtor is null || !AbstractOperations.IsConstructor(JsValue.Object(superCtor)))
                    {
                        throw new JsThrow(_runtime.Realm.NewTypeError(
                            "Super constructor is not a constructor"));
                    }

                    var nt = frame.NewTarget ?? frame.CurrentFunction;
                    // wp:M3-84 Stage B — a plain same-realm parent constructor is
                    // trampolined like New. The dispatch loop detects the frame
                    // switch after DispatchCold returns (t_current changed) and
                    // reloads its hot cache; the refs must not be touched after
                    // the push, so return immediately.
                    if (TryPushConstruct(JsValue.Object(superCtor), ctorArgs, nt, frame,
                            ip, sp, maxSp, FrameDisposition.SuperCtor, out _))
                    {
                        return true;
                    }

                    var constructed = AbstractOperations.Construct(this,
                        JsValue.Object(superCtor), ctorArgs, nt);
                    Push(frame, ref stack, ref sp, ref maxSp, constructed);
                    break;
                }
            case Opcode.LoadSuperProperty:
                {
                    var idx = ReadU16(code, ref ip);
                    var name = (string)constants[idx]!;
                    if (frame.CurrentFunction?.HomeObject is null)
                    {
                        throw new JsThrow(_runtime.Realm.NewSyntaxError("'super' keyword unexpected here"));
                    }

                    var superProto = frame.CurrentFunction.HomeObject.Prototype;
                    if (superProto is null)
                    {
                        Push(frame, ref stack, ref sp, ref maxSp, JsValue.Undefined);
                        break;
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, AbstractOperations.Get(this, superProto, name, frame.ThisV));
                    break;
                }
            case Opcode.StoreSuperProperty:
                {
                    var idx = ReadU16(code, ref ip);
                    var name = (string)constants[idx]!;
                    var value = Pop(stack, ref sp);
                    if (frame.CurrentFunction?.HomeObject is null)
                    {
                        throw new JsThrow(_runtime.Realm.NewSyntaxError("'super' keyword unexpected here"));
                    }
                    // §13.3.4 / §9.1.1.4 PutValue with a Super Reference: the
                    // [[Set]] runs against the super base (GetPrototypeOf([[HomeObject]]))
                    // with the receiver = `this`. So a setter found on the super
                    // base runs with this=receiver; otherwise OrdinarySet creates
                    // the own data property on the receiver, not the prototype.
                    var superBase = frame.CurrentFunction.HomeObject.Prototype;
                    if (superBase is not null)
                    {
                        AbstractOperations.Set(this, superBase, name, value, frame.ThisV);
                    }

                    Push(frame, ref stack, ref sp, ref maxSp, value);
                    break;
                }
            case Opcode.LoadSuperComputed:
                {
                    // wp:M3-04h — super[expr] read. Like LoadSuperProperty but the
                    // key is taken from the stack and coerced via ToPropertyKey
                    // (§13.3.7.2 GetSuperBase + §13.3.4 MakeSuperPropertyReference).
                    var key = Pop(stack, ref sp);
                    if (frame.CurrentFunction?.HomeObject is null)
                    {
                        throw new JsThrow(_runtime.Realm.NewSyntaxError("'super' keyword unexpected here"));
                    }

                    var propertyKey = AbstractOperations.ToPropertyKey(this, key);
                    var superProto = frame.CurrentFunction.HomeObject.Prototype;
                    if (superProto is null)
                    {
                        Push(frame, ref stack, ref sp, ref maxSp, JsValue.Undefined);
                        break;
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, AbstractOperations.Get(this, superProto, propertyKey, frame.ThisV));
                    break;
                }
            case Opcode.StoreSuperComputed:
                {
                    // wp:M3-04h — super[expr] = v. Mirrors StoreSuperProperty:
                    // §13.3.4 PutValue with a Super Reference runs [[Set]] against
                    // the super base with the receiver = `this`. Key is coerced
                    // via ToPropertyKey.
                    var value = Pop(stack, ref sp);
                    var key = Pop(stack, ref sp);
                    if (frame.CurrentFunction?.HomeObject is null)
                    {
                        throw new JsThrow(_runtime.Realm.NewSyntaxError("'super' keyword unexpected here"));
                    }

                    var propertyKey = AbstractOperations.ToPropertyKey(this, key);
                    var superBase = frame.CurrentFunction.HomeObject.Prototype;
                    if (superBase is not null)
                    {
                        AbstractOperations.Set(this, superBase, propertyKey, value, frame.ThisV);
                    }

                    Push(frame, ref stack, ref sp, ref maxSp, value);
                    break;
                }
            case Opcode.PrivateGet:
                {
                    var idx = ReadU16(code, ref ip);
                    var name = (string)constants[idx]!;
                    var receiver = Pop(stack, ref sp);
                    // §13.3.4.2 PrivateGet → §7.3.x PrivateElementFind: a TypeError
                    // unless the receiver ITSELF carries the brand. The brand is a
                    // per-object set (never prototype-walked), so a wrong receiver —
                    // a subclass constructor for a static private member, a Proxy
                    // wrapping an instance, or any object before its brand is
                    // installed (derived class, pre-super()) — throws here.
                    if (!receiver.IsObject || !receiver.AsObject.HasPrivateBrand(name))
                    {
                        throw new JsThrow(_runtime.Realm.NewTypeError(
                            "Cannot read private member from an object whose class did not declare it"));
                    }
                    // §13.3.4.2 PrivateGet — a private accessor's getter must be
                    // invoked with `this` = receiver; a private method or field
                    // yields its value directly. Routing through the AO walks the
                    // chain (private methods/accessors live on the prototype) and
                    // invokes any getter; a §13.3.4 get on a set-only accessor is
                    // a TypeError.
                    var obj = receiver.AsObject;
                    var (getDesc, _) = FindPrivateDescriptor(obj, name);
                    if (getDesc is { IsAccessor: true } ga)
                    {
                        if (ga.Getter is null)
                        {
                            throw new JsThrow(_runtime.Realm.NewTypeError(
                                $"'{name}' was defined without a getter"));
                        }

                        Push(frame, ref stack, ref sp, ref maxSp, AbstractOperations.Call(this, JsValue.Object(ga.Getter), receiver, Array.Empty<JsValue>()));
                    }
                    else
                    {
                        Push(frame, ref stack, ref sp, ref maxSp, getDesc?.Value ?? obj.Get(name));
                    }
                    break;
                }
            case Opcode.PrivateSet:
                {
                    var idx = ReadU16(code, ref ip);
                    var name = (string)constants[idx]!;
                    var value = Pop(stack, ref sp);
                    var receiver = Pop(stack, ref sp);
                    // §13.3.4.3 PrivateSet → PrivateElementFind: a TypeError unless
                    // the receiver ITSELF carries the brand (per-object set, never
                    // prototype-walked) — see PrivateGet above.
                    if (!receiver.IsObject || !receiver.AsObject.HasPrivateBrand(name))
                    {
                        throw new JsThrow(_runtime.Realm.NewTypeError(
                            "Cannot write private member from an object whose class did not declare it"));
                    }
                    // §13.3.4.3 PrivateSet — invoke a private accessor's setter
                    // with `this` = receiver; writing a get-only accessor or a
                    // private method is a TypeError. A private field writes its
                    // own slot directly.
                    var sobj = receiver.AsObject;
                    var (setDesc, ownsField) = FindPrivateDescriptor(sobj, name);
                    if (setDesc is { IsAccessor: true } sa)
                    {
                        if (sa.Setter is null)
                        {
                            throw new JsThrow(_runtime.Realm.NewTypeError(
                                $"'{name}' was defined without a setter"));
                        }

                        AbstractOperations.Call(this, JsValue.Object(sa.Setter), receiver, new[] { value });
                    }
                    else if (setDesc is { IsAccessor: false } && !ownsField)
                    {
                        // A private *method* (data descriptor on the prototype) is
                        // not writable (§13.3.4.3 step "if entry.[[Kind]] is method").
                        throw new JsThrow(_runtime.Realm.NewTypeError(
                            $"private method '{name}' is not writable"));
                    }
                    else
                    {
                        sobj.Set(name, value);
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, value);
                    break;
                }
            case Opcode.DefinePrivateField:
                {
                    var idx = ReadU16(code, ref ip);
                    var name = (string)constants[idx]!;
                    var value = Pop(stack, ref sp);
                    var receiver = Pop(stack, ref sp);
                    if (!receiver.IsObject)
                    {
                        throw new JsThrow(_runtime.Realm.NewTypeError("Cannot define private field on non-object"));
                    }

                    if (receiver.AsObject.HasOwn(name))
                    {
                        throw new JsThrow(_runtime.Realm.NewTypeError(
                            "Cannot initialize the same private member twice on the same object"));
                    }

                    receiver.AsObject.DefineOwnProperty(name,
                        PropertyDescriptor.Data(value, writable: true, enumerable: false, configurable: false));
                    // §7.3.28 PrivateFieldAdd installs the private element onto
                    // the receiver's [[PrivateElements]] — i.e. the brand. The
                    // brand check (PrivateGet/PrivateSet/PrivateIn/call) consults
                    // this per-object set, not the prototype chain.
                    receiver.AsObject.AddPrivateBrand(name);
                    break;
                }
            case Opcode.PrivateIn:
                {
                    var idx = ReadU16(code, ref ip);
                    var name = (string)constants[idx]!;
                    var operand = Pop(stack, ref sp);
                    // §13.10.1 step 4 — a non-object right operand is a TypeError
                    // (not `false`).
                    if (!operand.IsObject)
                    {
                        throw new JsThrow(_runtime.Realm.NewTypeError(
                            "Cannot use 'in' operator to search for a private name in a non-object"));
                    }
                    // §13.10.1 / §7.3.x — `#x in obj` is true iff obj ITSELF carries
                    // the brand for #x (per-object set, never prototype-walked). A
                    // subclass constructor or a Proxy wrapping an instance does not
                    // carry the brand, so this yields false rather than true.
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(operand.AsObject.HasPrivateBrand(name)));
                    break;
                }
            case Opcode.LoadCallerArgs:
                {
                    var arr = new JsArray(_runtime.Realm);
                    foreach (var a in frame.Args)
                    {
                        arr.Push(a);
                    }

                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Object(arr));
                    break;
                }
            case Opcode.RunFieldInits:
                {
                    // §10.2.1.3 InitializeInstanceElements — for a derived class
                    // this runs only after super() returns, so the instance's
                    // private brands appear at exactly the spec-correct moment;
                    // private access before this point throws a TypeError.
                    // Install instance private method/accessor brands first: their
                    // bodies live on the shared prototype, but each instance must
                    // carry the brand in its own [[PrivateElements]] set.
                    if (frame.CurrentFunction?.InstancePrivateBrands is { } brands && frame.ThisV.IsObject)
                    {
                        var brandObj = frame.ThisV.AsObject;
                        foreach (var b in brands)
                        {
                            brandObj.AddPrivateBrand(b);
                        }
                    }
                    var inits = frame.CurrentFunction?.InstanceFieldInitializers;
                    if (inits is not null)
                    {
                        foreach (var init in inits)
                        {
                            var value = AbstractOperations.Call(
                                this, JsValue.Object(init.Thunk), frame.ThisV, Array.Empty<JsValue>());
                            // wp:M3-04f — computed-key instance fields: the thunk
                            // returns the initializer value; define the own data
                            // property under the key resolved at class-definition
                            // time (CreateDataPropertyOrThrow per §10.2.4.1 /
                            // §15.7.10). Non-computed thunks self-store and return
                            // undefined — nothing to do here.
                            if (init.ComputedKey is { } ck && frame.ThisV.IsObject)
                            {
                                frame.ThisV.AsObject.DefineOwnProperty(ck,
                                    PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true));
                            }
                        }
                    }
                    break;
                }
            case Opcode.BuildClass:
                {
                    var idx = ReadU16(code, ref ip);
                    var template = (Starling.Js.Bytecode.ClassTemplate)constants[idx]!;

                    // Stack layout (top → bottom):
                    //   [baseClass?]
                    //   [ctor-upvalue0, ctor-upvalue1, …]
                    //   [method0-upvalue0, …, methodK-upvalueN]
                    //   [field0-upvalue0, …, fieldK-upvalueN]
                    //   [staticBlock0-upvalue0, …, staticBlockK-upvalueN]
                    // We pop in reverse declaration order so each consumer
                    // sees its upvalues in the order it pushed them.
                    var staticBlocks = template.StaticBlocks;
                    var staticBlockUpvalues = new JsValue[staticBlocks.Count][];
                    for (var i = staticBlocks.Count - 1; i >= 0; i--)
                    {
                        var n = staticBlocks[i].UpvalueCount;
                        var ups = new JsValue[n];
                        for (var k = n - 1; k >= 0; k--)
                        {
                            ups[k] = Pop(stack, ref sp);
                        }

                        staticBlockUpvalues[i] = ups;
                    }
                    // wp:M3-04f — computed keys were pushed (already ToPropertyKey-
                    // coerced) below each member's upvalues, so pop upvalues first
                    // then the key. Keys default to undefined for non-computed
                    // members and are ignored there.
                    var fieldUpvalues = new JsValue[template.Fields.Count][];
                    var fieldComputedKeys = new JsValue[template.Fields.Count];
                    for (var i = template.Fields.Count - 1; i >= 0; i--)
                    {
                        var n = template.Fields[i].UpvalueCount;
                        var ups = new JsValue[n];
                        for (var k = n - 1; k >= 0; k--)
                        {
                            ups[k] = Pop(stack, ref sp);
                        }

                        fieldUpvalues[i] = ups;
                        fieldComputedKeys[i] = template.Fields[i].IsComputed ? Pop(stack, ref sp) : JsValue.Undefined;
                    }
                    var methodUpvalues = new JsValue[template.Methods.Count][];
                    var methodComputedKeys = new JsValue[template.Methods.Count];
                    for (var i = template.Methods.Count - 1; i >= 0; i--)
                    {
                        var n = template.Methods[i].UpvalueCount;
                        var ups = new JsValue[n];
                        for (var k = n - 1; k >= 0; k--)
                        {
                            ups[k] = Pop(stack, ref sp);
                        }

                        methodUpvalues[i] = ups;
                        methodComputedKeys[i] = template.Methods[i].IsComputed ? Pop(stack, ref sp) : JsValue.Undefined;
                    }
                    var ctorUps = new JsValue[template.ConstructorUpvalueCount];
                    for (var k = template.ConstructorUpvalueCount - 1; k >= 0; k--)
                    {
                        ctorUps[k] = Pop(stack, ref sp);
                    }

                    JsValue baseClassValue = JsValue.Undefined;
                    if (template.HasExtends)
                    {
                        baseClassValue = Pop(stack, ref sp);
                    }

                    // §15.7.14 — the inner class-name binding (a named
                    // class expression's `Inner` cell) must hold the
                    // constructor BEFORE static field initializers run, so
                    // `static x = new Inner()` resolves the class by name.
                    // The cell lives in this frame's captured locals; pass
                    // it through so BuildClassRuntime can set it at the
                    // right moment.
                    Cell? selfNameCell = null;
                    if (template.SelfNameSlot >= 0)
                    {
                        selfNameCell = (Cell)locals[template.SelfNameSlot].AsObject;
                    }

                    var classCtor = BuildClassRuntime(template, baseClassValue,
                        ctorUps, methodUpvalues, fieldUpvalues, staticBlockUpvalues,
                        methodComputedKeys, fieldComputedKeys, selfNameCell);
                    Push(frame, ref stack, ref sp, ref maxSp, classCtor);
                    break;
                }
            default:
                return false;
        }
        return true;
    }


    /// <summary>wp:M3-84 Stage A — numeric/bitwise operator arms, out-of-line
    /// for the same reason as <see cref="DispatchCold"/>: every IL local in
    /// the dispatch method costs a permanent native stack slot per barrier, and these
    /// arms hold ~40 of them. This frame is transient, so the temporaries no
    /// longer multiply with JS call depth. NoInlining is load-bearing.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ExecArith(Opcode op, CallFrame frame, ref JsValue[] stack, ref int sp, ref int maxSp)
    {
        switch (op)
        {
            case Opcode.Sub:
                {
                    var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); a = ToNumericOperand(a); b = ToNumericOperand(b);
                    if (a.IsBigInt || b.IsBigInt)
                    {
                        if (!(a.IsBigInt && b.IsBigInt))
                        {
                            throw BigIntOps.MixedTypeError(_runtime.Realm, "-");
                        }

                        Push(frame, ref stack, ref sp, ref maxSp, BigIntOps.Subtract(a.AsBigInt, b.AsBigInt));
                        break;
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Number(JsValue.ToNumber(a) - JsValue.ToNumber(b)));
                    break;
                }
            case Opcode.Mul:
                {
                    var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); a = ToNumericOperand(a); b = ToNumericOperand(b);
                    if (a.IsBigInt || b.IsBigInt)
                    {
                        if (!(a.IsBigInt && b.IsBigInt))
                        {
                            throw BigIntOps.MixedTypeError(_runtime.Realm, "*");
                        }

                        Push(frame, ref stack, ref sp, ref maxSp, BigIntOps.Multiply(a.AsBigInt, b.AsBigInt));
                        break;
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Number(JsValue.ToNumber(a) * JsValue.ToNumber(b)));
                    break;
                }
            case Opcode.Div:
                {
                    var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); a = ToNumericOperand(a); b = ToNumericOperand(b);
                    if (a.IsBigInt || b.IsBigInt)
                    {
                        if (!(a.IsBigInt && b.IsBigInt))
                        {
                            throw BigIntOps.MixedTypeError(_runtime.Realm, "/");
                        }

                        Push(frame, ref stack, ref sp, ref maxSp, BigIntOps.Divide(_runtime.Realm, a.AsBigInt, b.AsBigInt));
                        break;
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Number(JsValue.ToNumber(a) / JsValue.ToNumber(b)));
                    break;
                }
            case Opcode.Mod:
                {
                    var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); a = ToNumericOperand(a); b = ToNumericOperand(b);
                    if (a.IsBigInt || b.IsBigInt)
                    {
                        if (!(a.IsBigInt && b.IsBigInt))
                        {
                            throw BigIntOps.MixedTypeError(_runtime.Realm, "%");
                        }

                        Push(frame, ref stack, ref sp, ref maxSp, BigIntOps.Remainder(_runtime.Realm, a.AsBigInt, b.AsBigInt));
                        break;
                    }
                    var ad = JsValue.ToNumber(a); var bd = JsValue.ToNumber(b);
                    // §Number::remainder — truncated remainder (result carries the
                    // sign of the dividend), matching C `fmod`. The C# `%` operator
                    // on doubles implements exactly these IEEE semantics, including
                    // the NaN/Infinity/zero edge cases (`x % 0` → NaN, `x % ∞` → x,
                    // `∞ % y` → NaN), unlike the floored `a - floor(a/b)*b` form.
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Number(ad % bd));
                    break;
                }
            case Opcode.Pow:
                {
                    var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); a = ToNumericOperand(a); b = ToNumericOperand(b);
                    if (a.IsBigInt || b.IsBigInt)
                    {
                        if (!(a.IsBigInt && b.IsBigInt))
                        {
                            throw BigIntOps.MixedTypeError(_runtime.Realm, "**");
                        }

                        Push(frame, ref stack, ref sp, ref maxSp, BigIntOps.Pow(_runtime.Realm, a.AsBigInt, b.AsBigInt));
                        break;
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Number(Math.Pow(JsValue.ToNumber(a), JsValue.ToNumber(b))));
                    break;
                }
            case Opcode.Neg:
                {
                    var v = ToNumericOperand(Pop(stack, ref sp));
                    if (v.IsBigInt) { Push(frame, ref stack, ref sp, ref maxSp, BigIntOps.Negate(v.AsBigInt)); break; }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Number(-JsValue.ToNumber(v)));
                    break;
                }
            case Opcode.UnaryPlus:
                {
                    var v = ToNumericOperand(Pop(stack, ref sp));
                    // §13.5.4: unary + on a BigInt throws TypeError.
                    if (v.IsBigInt)
                    {
                        throw new JsThrow(_runtime.Realm.NewTypeError("Cannot convert a BigInt value to a number"));
                    }

                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Number(JsValue.ToNumber(v)));
                    break;
                }
            // ----- Bitwise (Number → Int32, or BigInt-only) -----
            case Opcode.BitOr:
                {
                    var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); a = ToNumericOperand(a); b = ToNumericOperand(b);
                    if (a.IsBigInt || b.IsBigInt)
                    {
                        if (!(a.IsBigInt && b.IsBigInt))
                        {
                            throw BigIntOps.MixedTypeError(_runtime.Realm, "|");
                        }

                        Push(frame, ref stack, ref sp, ref maxSp, BigIntOps.BitwiseOr(a.AsBigInt, b.AsBigInt));
                        break;
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Number(ToInt32(a) | ToInt32(b))); break;
                }
            case Opcode.BitAnd:
                {
                    var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); a = ToNumericOperand(a); b = ToNumericOperand(b);
                    if (a.IsBigInt || b.IsBigInt)
                    {
                        if (!(a.IsBigInt && b.IsBigInt))
                        {
                            throw BigIntOps.MixedTypeError(_runtime.Realm, "&");
                        }

                        Push(frame, ref stack, ref sp, ref maxSp, BigIntOps.BitwiseAnd(a.AsBigInt, b.AsBigInt));
                        break;
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Number(ToInt32(a) & ToInt32(b))); break;
                }
            case Opcode.BitXor:
                {
                    var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); a = ToNumericOperand(a); b = ToNumericOperand(b);
                    if (a.IsBigInt || b.IsBigInt)
                    {
                        if (!(a.IsBigInt && b.IsBigInt))
                        {
                            throw BigIntOps.MixedTypeError(_runtime.Realm, "^");
                        }

                        Push(frame, ref stack, ref sp, ref maxSp, BigIntOps.BitwiseXor(a.AsBigInt, b.AsBigInt));
                        break;
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Number(ToInt32(a) ^ ToInt32(b))); break;
                }
            case Opcode.BitNot:
                {
                    var v = ToNumericOperand(Pop(stack, ref sp));
                    if (v.IsBigInt) { Push(frame, ref stack, ref sp, ref maxSp, BigIntOps.BitwiseNot(v.AsBigInt)); break; }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Number(~ToInt32(v))); break;
                }
            case Opcode.Shl:
                {
                    var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); a = ToNumericOperand(a); b = ToNumericOperand(b);
                    if (a.IsBigInt || b.IsBigInt)
                    {
                        if (!(a.IsBigInt && b.IsBigInt))
                        {
                            throw BigIntOps.MixedTypeError(_runtime.Realm, "<<");
                        }

                        Push(frame, ref stack, ref sp, ref maxSp, BigIntOps.ShiftLeft(_runtime.Realm, a.AsBigInt, b.AsBigInt));
                        break;
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Number(ToInt32(a) << (ToInt32(b) & 31))); break;
                }
            case Opcode.Shr:
                {
                    var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); a = ToNumericOperand(a); b = ToNumericOperand(b);
                    if (a.IsBigInt || b.IsBigInt)
                    {
                        if (!(a.IsBigInt && b.IsBigInt))
                        {
                            throw BigIntOps.MixedTypeError(_runtime.Realm, ">>");
                        }

                        Push(frame, ref stack, ref sp, ref maxSp, BigIntOps.ShiftRight(_runtime.Realm, a.AsBigInt, b.AsBigInt));
                        break;
                    }
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Number(ToInt32(a) >> (ToInt32(b) & 31))); break;
                }
            case Opcode.Ushr:
                {
                    var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); a = ToNumericOperand(a); b = ToNumericOperand(b);
                    // §13.10.4 — BigInts have no unsigned right shift; throw TypeError.
                    if (a.IsBigInt || b.IsBigInt)
                    {
                        throw new JsThrow(_runtime.Realm.NewTypeError("BigInts have no unsigned right shift, use >> instead"));
                    }

                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Number((uint)ToInt32(a) >> (ToInt32(b) & 31))); break;
                }
            default:
                throw new InvalidOperationException($"opcode {op} is not an arithmetic opcode");
        }
    }

    /// <summary>wp:M3-84 Stage A — comparison / typeof / instanceof / in arms,
    /// out-of-line for frame size (see <see cref="ExecArith"/>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void ExecCompare(Opcode op, CallFrame frame, ref JsValue[] stack, ref int sp, ref int maxSp)
    {
        switch (op)
        {
            // ----- Comparison -----
            case Opcode.Eq: { var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(AbstractEquals(a, b))); break; }
            case Opcode.NEq: { var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(!AbstractEquals(a, b))); break; }
            case Opcode.StrictEq: { var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(JsValue.StrictEquals(a, b))); break; }
            case Opcode.StrictNEq: { var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(!JsValue.StrictEquals(a, b))); break; }
            case Opcode.Lt: { var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(RelationalLessThan(a, b, leftFirst: true) == true)); break; }
            case Opcode.LtEq:
                {
                    var b = Pop(stack, ref sp); var a = Pop(stack, ref sp);
                    var r = RelationalLessThan(b, a, leftFirst: false);
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(r == false));
                    break;
                }
            case Opcode.Gt: { var b = Pop(stack, ref sp); var a = Pop(stack, ref sp); Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(RelationalLessThan(b, a, leftFirst: false) == true)); break; }
            case Opcode.GtEq:
                {
                    var b = Pop(stack, ref sp); var a = Pop(stack, ref sp);
                    var r = RelationalLessThan(a, b, leftFirst: true);
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(r == false));
                    break;
                }
            case Opcode.TypeOf:
                {
                    var v = Pop(stack, ref sp);
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.String(v.Kind switch
                    {
                        JsValueKind.Undefined => "undefined",
                        JsValueKind.Null => "object",
                        JsValueKind.Boolean => "boolean",
                        JsValueKind.Number => "number",
                        JsValueKind.String => "string",
                        JsValueKind.Object => AbstractOperations.IsCallable(v) ? "function" : "object",
                        JsValueKind.BigInt => "bigint",
                        JsValueKind.Symbol => "symbol",
                        _ => "undefined",
                    }));
                    break;
                }
            // ----- Operator bundle (gap:instanceof / gap:in / gap:delete) -----
            case Opcode.Instanceof:
                {
                    var target = Pop(stack, ref sp);
                    var value = Pop(stack, ref sp);
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(InstanceofOperator(value, target)));
                    break;
                }
            case Opcode.In:
                {
                    var rhs = Pop(stack, ref sp);
                    var key = Pop(stack, ref sp);
                    if (!rhs.IsObject)
                    {
                        throw new JsThrow(_runtime.Realm.NewTypeError(
                            "Cannot use 'in' operator to search for '"
                            + JsValue.ToStringValue(key) + "' in "
                            + JsValue.ToStringValue(rhs)));
                    }

                    var pk = AbstractOperations.ToPropertyKey(this, key);
                    Push(frame, ref stack, ref sp, ref maxSp, JsValue.Boolean(AbstractOperations.HasProperty(rhs.AsObject, pk)));
                    break;
                }
            default:
                throw new InvalidOperationException($"opcode {op} is not a comparison opcode");
        }
    }

    /// <summary>wp:M3-84 Stage A — the YieldDelegate arm, out-of-line for frame
    /// size. Returns true when the inner iterator suspended this frame; the
    /// dispatch loop then returns <paramref name="result"/> WITHOUT releasing
    /// the pooled operand stack (the suspension snapshot owns it until the
    /// body completes).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool ExecYieldDelegate(CallFrame frame, ref JsValue[] stack, ref int ip, ref int sp, ref int maxSp, out JsValue result)
    {
        result = JsValue.Undefined;
        var code = frame.Code;
        var isAsync = code[ip++] != 0;
        var iterable = Pop(stack, ref sp);
        if (frame.Suspension is not { } ydSusp)
        {
            throw new JsThrow(_runtime.Realm.NewSyntaxError(
                "yield is only valid in generator functions"));
        }
        IteratorRecord record;
        bool syncWrapped = false;
        if (isAsync)
        {
            var handle = GetAsyncIteratorHandle(iterable);
            record = handle.Record;
            syncWrapped = handle.SyncWrapped;
        }
        else
        {
            record = AbstractOperations.GetIterator(_runtime.Realm, this, iterable);
        }
        ydSusp.YieldDelegate = new YieldDelegateContinuation
        {
            IsAsync = isAsync,
            SyncWrapped = syncWrapped,
            Record = record,
            InnerIterator = record.Iterator,
            NextMethod = record.NextMethod,
        };
        frame.Ip = ip;
        frame.Sp = sp;
        frame.MaxSp = maxSp;
        var step = RunYieldDelegateContinuation(frame, ydSusp.YieldDelegate);
        if (step.Suspended)
        {
            result = JsValue.Undefined;
            return true;
        }
        ydSusp.ClearContinuation();
        Push(frame, ref stack, ref sp, ref maxSp, step.Value);
        return false;
    }

    // ---- wp:M3-84 Stage A — de-closured dispatch-loop helpers --------------------
    // These were nested local functions inside the old RunInner. As locals-capturing
    // local functions they forced one large closure display per dispatch
    // activation (~20-50 KB of native frame), which capped pure JS->JS
    // recursion at ~26 native frames. They take explicit state instead, so
    // the dispatch loop keeps no closure at all.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Push(CallFrame frame, ref JsValue[] stack, ref int sp, ref int maxSp, JsValue v)
    {
        if (sp >= stack.Length)
        {
            GrowStack(frame, ref stack, sp, maxSp);
        }

        stack[sp++] = v;
        if (sp > maxSp)
        {
            maxSp = sp;
        }
    }

    /// <summary>Out-of-line grow path for <see cref="Push"/> /
    /// <see cref="PushFrame"/>. Rents double the current length (capped at
    /// <see cref="MaxStack"/> — at the cap this throws the same
    /// StackOverflowException the fixed-size stack did), copies the live
    /// region, and returns the old array to the pool with the same clearing
    /// <see cref="Finish"/> does. Writes the new array to BOTH the caller's
    /// cached local (via <paramref name="stack"/>) and
    /// <see cref="CallFrame.Stack"/> so frame switches, suspension snapshots,
    /// and the unwinder all see it. NoInlining is load-bearing: inlined into
    /// the dispatch loop this would regrow every JS frame's native cost
    /// (wp:M3-84 Stage A trap).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void GrowStack(CallFrame frame, ref JsValue[] stack, int sp, int maxSp)
    {
        if (stack.Length >= MaxStack)
        {
            throw new StackOverflowException("JS stack overflow");
        }

        var grown = ArrayPool<JsValue>.Shared.Rent(Math.Min(stack.Length * 2, MaxStack));
        System.Array.Copy(stack, grown, sp);
        Finish(stack, maxSp, JsValue.Undefined);
        frame.Stack = grown;
        stack = grown;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static JsValue Pop(JsValue[] stack, ref int sp) => stack[--sp];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadU16(byte[] code, ref int ip)
    {
        var v = BinaryPrimitives.ReadUInt16LittleEndian(code.AsSpan(ip, 2));
        ip += 2;
        return v;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ReadI32(byte[] code, ref int ip)
    {
        var v = BinaryPrimitives.ReadInt32LittleEndian(code.AsSpan(ip, 4));
        ip += 4;
        return v;
    }

    /// <summary>Return the rented operand stack to the pool, clearing only the
    /// slots the frame touched so the pool never pins JS objects. Returns its
    /// argument so call sites read <c>return Finish(stack, maxSp, value);</c> —
    /// the value is evaluated before the clear, so reading stack[sp-1] for it
    /// stays valid. Never called on the suspend path: a suspended frame's
    /// snapshot keeps the pooled array until the body completes.</summary>
    private static JsValue Finish(JsValue[] stack, int maxSp, JsValue result)
    {
        if (maxSp > 0)
        {
            System.Array.Clear(stack, 0, maxSp);
        }

        ArrayPool<JsValue>.Shared.Return(stack);
        return result;
    }

    /// <summary>Rent a frame's locals array from the shared pool (sized ≥
    /// <paramref name="localCount"/>, so OVERSIZED — consumers bound by
    /// <see cref="Chunk.LocalCount"/>, never by Length). No clear on rent:
    /// every pool return path (<see cref="Finish"/>, <see cref="ReleaseLocals"/>)
    /// clears the region it dirtied, and pool-fresh arrays are CLR-zeroed, so a
    /// rented array always reads as all-Undefined (default(JsValue)) exactly
    /// like the <c>new JsValue[]</c> it replaces.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static JsValue[] RentLocals(int localCount)
        => ArrayPool<JsValue>.Shared.Rent(Math.Max(localCount, 1));

    /// <summary>Return a popping frame's pooled locals array, clearing the
    /// addressed region (slots 0..LocalCount) so the pool never pins JS
    /// objects. Skipped when the array escaped the frame
    /// (<see cref="CallFrame.LocalsEscaped"/>: a mapped <c>arguments</c> object
    /// or a direct-eval <see cref="EvalScope"/> holds the array itself and
    /// reads it after the frame pops). Never called on the suspend path — a
    /// suspended frame's snapshot keeps its locals until the body completes.</summary>
    private static void ReleaseLocals(CallFrame frame)
    {
        if (frame.LocalsEscaped)
        {
            return;
        }

        var locals = frame.Locals;
        var n = frame.Chunk.LocalCount;
        if (n > 0)
        {
            System.Array.Clear(locals, 0, n);
        }

        ArrayPool<JsValue>.Shared.Return(locals);
    }

    // ---- wp:M3-84 Stage B — trampoline push/pop helpers ---------------------

    /// <summary>Reload the dispatch loop's hot-field cache from
    /// <paramref name="frame"/> after a frame switch (push, pop, or unwind).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void LoadFrameCache(CallFrame frame, ref Chunk chunk, ref JsValue[] stack,
        ref JsValue[] locals, ref byte[] code, ref IReadOnlyList<object?> constants,
        ref int ip, ref int sp, ref int maxSp)
    {
        chunk = frame.Chunk;
        stack = frame.Stack;
        locals = frame.Locals;
        code = frame.Code;
        constants = frame.Constants;
        ip = frame.Ip;
        sp = frame.Sp;
        maxSp = frame.MaxSp;
    }

    /// <summary>True when <paramref name="callee"/> is an ordinary same-realm
    /// plain <see cref="JsFunction"/> the dispatch loop may run on a
    /// trampolined frame. Native/bound/Proxy callables and foreign-realm
    /// functions stay on the AbstractOperations barrier path; generator /
    /// async / async-generator bodies start via StartGeneratorBody /
    /// StartAsyncBody and stay native calls.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsTrampolinable(JsValue callee, out JsFunction fn)
    {
        fn = null!;
        if (!callee.IsObject || callee.AsObject is not JsFunction f)
        {
            return false;
        }

        if (f.Kind != JsFunctionKind.Normal)
        {
            return false;
        }

        if (f.Realm is { } fnRealm && !ReferenceEquals(fnRealm, _runtime.Realm))
        {
            return false;
        }

        fn = f;
        return true;
    }

    /// <summary>Push a trampolined [[Call]] frame for an ordinary same-realm
    /// <see cref="JsFunction"/>. Returns false (without consuming anything)
    /// when the callee must take the barrier path instead. On true, the
    /// caller's hot fields are flushed and <paramref name="pushed"/> is the
    /// new current frame — the dispatch loop reloads its cache and continues.
    /// The callee frame owns <paramref name="args"/> and returns it to the
    /// arg pool when it pops.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryPushCall(JsValue callee, JsValue thisValue, JsValue[] args,
        CallFrame caller, int ip, int sp, int maxSp, out CallFrame pushed)
    {
        pushed = null!;
        if (!IsTrampolinable(callee, out var fn))
        {
            return false;
        }

        if (t_frameDepth >= MaxFrameDepth)
        {
            throw new JsThrow(_runtime.Realm.NewRangeError("Maximum call stack size exceeded"));
        }
        // §10.2.1.2 OrdinaryCallBindThis — same sloppy-this coercion as
        // CallFunctionLocal: a sloppy function called with nullish `this`
        // binds the global object.
        if (thisValue.IsNullish && fn.ConstructorKind == ClassConstructorKind.None
            && !fn.Body.IsStrict)
        {
            thisValue = JsValue.Object(_runtime.Realm.GlobalObject);
        }

        var callee_frame = CreateFrame(fn.Body, args, thisValue, fn.Upvalues, fn,
            newTarget: null, suspension: null, evalScope: null,
            frameVarStore: fn.CapturedEvalVarStore); // wp:M3-73
        caller.Ip = ip;
        caller.Sp = sp;
        caller.MaxSp = maxSp;
        callee_frame.Caller = caller;
        callee_frame.Disposition = FrameDisposition.Call;
        callee_frame.ReleaseArgsOnPop = true;
        t_current = callee_frame;
        t_frameDepth++;
        pushed = callee_frame;
        return true;
    }

    /// <summary>Push a trampolined [[Construct]] frame (New / NewApply /
    /// CallSuperCtor). Mirrors <see cref="TryPushCall"/>; the construct
    /// return coercion runs at pop via <see cref="CoerceReturn"/>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private bool TryPushConstruct(JsValue ctor, JsValue[] args, JsObject? newTarget,
        CallFrame caller, int ip, int sp, int maxSp, FrameDisposition disposition,
        out CallFrame pushed)
    {
        pushed = null!;
        if (!IsTrampolinable(ctor, out var fn))
        {
            return false;
        }

        if (t_frameDepth >= MaxFrameDepth)
        {
            throw new JsThrow(_runtime.Realm.NewRangeError("Maximum call stack size exceeded"));
        }

        newTarget ??= fn;
        var thisVal = ComputeConstructThis(fn, newTarget);
        var callee_frame = CreateFrame(fn.Body, args, thisVal, fn.Upvalues, fn,
            newTarget, suspension: null, evalScope: null,
            frameVarStore: fn.CapturedEvalVarStore); // wp:M3-73
        caller.Ip = ip;
        caller.Sp = sp;
        caller.MaxSp = maxSp;
        callee_frame.Caller = caller;
        callee_frame.Disposition = disposition;
        callee_frame.ReleaseArgsOnPop = true;
        t_current = callee_frame;
        t_frameDepth++;
        pushed = callee_frame;
        return true;
    }

    /// <summary>Release a popping frame's pooled resources — exactly the old
    /// exit path: return the operand stack and the locals array to the array
    /// pool (clearing the touched regions) and, for a trampolined frame,
    /// return its rented args array. Never called on the suspend path (the
    /// suspension snapshot keeps the pooled arrays until the body
    /// completes).</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ReleaseFrame(CallFrame frame, JsValue[] stack, int maxSp)
    {
        Finish(stack, maxSp, JsValue.Undefined);
        ReleaseLocals(frame);
        if (frame.ReleaseArgsOnPop)
        {
            ReturnArgs(frame.Args);
        }

        t_frameDepth--;
    }

    /// <summary>Apply the frame's return-value coercion at pop. A [[Call]]
    /// frame returns the value unchanged; construct dispositions defer to
    /// <see cref="CoerceConstructReturn"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private JsValue CoerceReturn(CallFrame frame, JsValue rv)
        => frame.Disposition == FrameDisposition.Call ? rv : CoerceConstructReturn(frame, rv);

    /// <summary>§10.2.1.4 / §10.2.1.1 — the [[Construct]] return coercion
    /// (shared by New, NewApply, super(...) and the ConstructFunction
    /// barrier). An object return wins; otherwise a derived-class constructor
    /// yields its super-bound <see cref="CallFrame.DerivedThis"/> (or throws
    /// if super never ran), and any other constructor yields its own
    /// <c>this</c>.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private JsValue CoerceConstructReturn(CallFrame frame, JsValue rv)
    {
        if (rv.IsObject)
        {
            return rv;
        }

        if (frame.CurrentFunction is { ConstructorKind: ClassConstructorKind.Derived })
        {
            return frame.DerivedThis ?? throw new JsThrow(_runtime.Realm.NewReferenceError(
                "Must call super constructor in derived class before returning from derived constructor"));
        }

        return frame.ThisV;
    }

    /// <summary>wp:M3-23 — append "(at line:col)" to a runtime-error message
    /// using the chunk's sparse position table. At a throw site `ip` has
    /// already advanced past the offending opcode's operands, so PositionAt
    /// finds the nearest preceding recorded entry (the opcode's start offset).
    /// No-ops gracefully (returns the bare message) when no position was
    /// recorded for that opcode.</summary>
    private static string AtPos(Chunk chunk, int ip, string message)
    {
        var pos = chunk.PositionAt(ip);
        return pos is { } p ? $"{message} (at {p.Line}:{p.Col})" : message;
    }

    private void CaptureJsStack(JsThrow ex, Chunk chunk, int ip, JsFunction? currentFunction)
    {
        var pos = chunk.PositionAt(ip);
        var source = chunk.SourcePath ?? chunk.Name ?? "<unknown>";
        var function = currentFunction?.Name;
        if (string.IsNullOrEmpty(function))
        {
            function = "<anonymous>";
        }

        ex.AddStackFrame(new JsStackFrame(function!, source, pos?.Line, pos?.Col));
        AttachGeneratedStack(ex);
    }

    /// <summary>§9.1.1.2.1 HasBinding — does object Environment Record
    /// <paramref name="obj"/> have a usable binding for <paramref name="name"/>?
    /// True when HasProperty(obj, name) and the name is not blocked by the
    /// object's @@unscopables list.</summary>
    private bool WithHasBinding(JsObject obj, string name)
    {
        if (!AbstractOperations.HasProperty(obj, name))
        {
            return false;
        }

        var unscopables = AbstractOperations.Get(this, obj, JsPropertyKey.Symbol(Intrinsics.SymbolCtor.Unscopables));
        if (unscopables.IsObject)
        {
            var blocked = AbstractOperations.Get(this, unscopables.AsObject, name);
            if (JsValue.ToBoolean(blocked))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Find the innermost with-object that provides a binding for
    /// <paramref name="name"/>, or null if none does (so the static fallback
    /// applies).</summary>
    private JsObject? FindWithBinding(List<JsObject>? withStack, string name)
    {
        if (withStack is null)
        {
            return null;
        }

        for (var i = withStack.Count - 1; i >= 0; i--)
        {
            if (WithHasBinding(withStack[i], name))
            {
                return withStack[i];
            }
        }

        return null;
    }

    /// <summary>Snapshot the frame for a generator/async suspension. The caller
    /// must have flushed the hot locals (Ip/Sp/MaxSp) to the frame first. The
    /// pooled Stack array is NOT returned to the pool here — the suspended
    /// continuation keeps it (and Locals) live until the body completes.</summary>
    private static ContinuationFrameState SnapshotFrame(CallFrame frame) => new()
    {
        Chunk = frame.Chunk,
        Stack = frame.Stack,
        Locals = frame.Locals,
        LocalsEscaped = frame.LocalsEscaped,
        Upvalues = frame.Upvalues,
        TryStack = frame.TryStack,
        CurrentFunction = frame.CurrentFunction,
        NewTarget = frame.NewTarget,
        EvalScope = frame.EvalScope,
        FrameVarStore = frame.FrameVarStore,
        WithStack = frame.WithStack,
        ThisValue = frame.ThisV,
        Ip = frame.Ip,
        Sp = frame.Sp,
        MaxSp = frame.MaxSp,
        InitDepth = frame.InitDepth,
    };

    private static JsValue SuspendCurrent(CallFrame frame, ContinuationResumeAction action, JsValue yielded, int kind)
    {
        if (frame.Suspension is not { } suspension)
        {
            throw new InvalidOperationException("Cannot suspend without a suspended frame");
        }

        suspension.Suspend(SnapshotFrame(frame), yielded, kind, action);
        return JsValue.Undefined;
    }

    /// <summary>Flush the hot locals to the frame, then park it. Keeps the
    /// dispatch loop's suspend sites single-expression returns.</summary>
    private static JsValue FlushAndSuspend(CallFrame frame, int ip, int sp, int maxSp,
        ContinuationResumeAction action, JsValue yielded, int kind)
    {
        frame.Ip = ip;
        frame.Sp = sp;
        frame.MaxSp = maxSp;
        return SuspendCurrent(frame, action, yielded, kind);
    }

    /// <summary>Push onto the frame's authoritative operand stack. Used by the
    /// resume prelude, which runs with the hot locals flushed to the frame;
    /// the dispatch loop reloads them right after.</summary>
    private static void PushFrame(CallFrame frame, JsValue v)
    {
        var stack = frame.Stack;
        var sp = frame.Sp;
        if (sp >= stack.Length)
        {
            GrowStack(frame, ref stack, sp, frame.MaxSp);
        }

        stack[sp++] = v;
        frame.Sp = sp;
        if (sp > frame.MaxSp)
        {
            frame.MaxSp = sp;
        }
    }

    private static void ApplyResumeToStack(CallFrame frame)
    {
        if (frame.Suspension is not { } suspension)
        {
            return;
        }

        var resume = suspension.ConsumeResume();
        suspension.ClearContinuation();
        switch (resume.Kind)
        {
            case ResumeCompletionKind.Throw:
                throw new JsThrow(resume.Value);
            case ResumeCompletionKind.Return:
                throw new JsReturnSentinel(resume.Value);
            default:
                PushFrame(frame, resume.Value);
                break;
        }
    }

    private YieldDelegateStep SuspendYieldDelegateAwait(CallFrame frame, YieldDelegateContinuation yd, JsValue value, bool processingReturn)
    {
        if (yd.SyncWrapped)
        {
            value = JsValue.Object(WrapSyncIteratorResult(value));
        }

        yd.Phase = YieldDelegatePhase.AwaitInnerResult;
        yd.ProcessingReturnResult = processingReturn;
        _ = SuspendCurrent(frame, ContinuationResumeAction.YieldDelegate, value, kind: 1);
        return YieldDelegateStep.Parked();
    }

    private static YieldDelegateStep SuspendYieldDelegateYield(CallFrame frame, YieldDelegateContinuation yd, JsValue value)
    {
        yd.Phase = YieldDelegatePhase.AfterOuterYield;
        _ = SuspendCurrent(frame, ContinuationResumeAction.YieldDelegate, value, kind: 0);
        return YieldDelegateStep.Parked();
    }

    private YieldDelegateStep ProcessYieldDelegateInnerResult(CallFrame frame, YieldDelegateContinuation yd, JsValue innerResult)
    {
        if (!innerResult.IsObject)
        {
            throw new JsThrow(_runtime.Realm.NewTypeError(
                yd.ProcessingReturnResult
                    ? "iterator.return() did not return an object"
                    : "iterator.next() did not return an object"));
        }

        var done = JsValue.ToBoolean(AbstractOperations.Get(this, innerResult.AsObject, "done"));
        var value = AbstractOperations.Get(this, innerResult.AsObject, "value");
        if (done)
        {
            if (yd.PendingOuterReturn)
            {
                yd.PendingOuterReturn = false;
                throw new JsReturnSentinel(value);
            }
            return YieldDelegateStep.Done(value);
        }

        return SuspendYieldDelegateYield(frame, yd, value);
    }

    private YieldDelegateStep RunYieldDelegateContinuation(CallFrame frame, YieldDelegateContinuation yd)
    {
        while (true)
        {
            if (yd.Phase == YieldDelegatePhase.AwaitInnerResult)
            {
                if (frame.Suspension is not { } awaitSusp)
                {
                    throw new InvalidOperationException("Yield delegate await without suspended frame");
                }

                var resume = awaitSusp.ConsumeResume();
                awaitSusp.ClearResumeAction();
                yd.Phase = YieldDelegatePhase.CallInner;
                if (resume.Kind == ResumeCompletionKind.Throw)
                {
                    throw new JsThrow(resume.Value);
                }

                if (resume.Kind == ResumeCompletionKind.Return)
                {
                    throw new JsReturnSentinel(resume.Value);
                }

                var step = ProcessYieldDelegateInnerResult(frame, yd, resume.Value);
                if (step.Suspended || step.Completed)
                {
                    return step;
                }

                continue;
            }

            if (yd.Phase == YieldDelegatePhase.AfterOuterYield)
            {
                if (frame.Suspension is not { } yieldSusp)
                {
                    throw new InvalidOperationException("Yield delegate resume without suspended frame");
                }

                var resume = yieldSusp.ConsumeResume();
                yieldSusp.ClearResumeAction();
                yd.Phase = YieldDelegatePhase.CallInner;
                yd.ProcessingReturnResult = false;
                switch (resume.Kind)
                {
                    case ResumeCompletionKind.Throw:
                        yd.Received = resume.Value;
                        yd.ReceivedKind = 1;
                        break;
                    case ResumeCompletionKind.Return:
                        yd.Received = resume.Value;
                        yd.ReceivedKind = 2;
                        yd.PendingOuterReturn = true;
                        break;
                    default:
                        yd.Received = resume.Value;
                        yd.ReceivedKind = 0;
                        break;
                }
            }

            JsValue innerResult;
            var processingReturn = false;
            if (yd.ReceivedKind == 0)
            {
                innerResult = AbstractOperations.Call(this, yd.NextMethod, yd.InnerIterator,
                    new[] { yd.Received });
            }
            else if (yd.ReceivedKind == 1)
            {
                var throwM = AbstractOperations.GetMethod(this, yd.InnerIterator, "throw");
                if (throwM.IsUndefined || throwM.IsNull)
                {
                    if (yd.IsAsync)
                    {
                        var ret = AbstractOperations.GetMethod(this, yd.InnerIterator, "return");
                        if (!ret.IsUndefined && !ret.IsNull)
                        {
                            _ = AbstractOperations.Call(this, ret, yd.InnerIterator, Array.Empty<JsValue>());
                        }
                    }
                    else
                    {
                        AbstractOperations.IteratorClose(this, yd.Record, isThrowing: true);
                    }
                    throw new JsThrow(_runtime.Realm.NewTypeError(
                        "Inner iterator does not have a 'throw' method"));
                }
                innerResult = AbstractOperations.Call(this, throwM, yd.InnerIterator,
                    new[] { yd.Received });
            }
            else
            {
                var retM = AbstractOperations.GetMethod(this, yd.InnerIterator, "return");
                if (retM.IsUndefined || retM.IsNull)
                {
                    throw new JsReturnSentinel(yd.Received);
                }

                yd.PendingOuterReturn = true;
                processingReturn = true;
                innerResult = AbstractOperations.Call(this, retM, yd.InnerIterator,
                    new[] { yd.Received });
            }

            yd.Received = JsValue.Undefined;
            yd.ReceivedKind = 0;
            yd.ProcessingReturnResult = processingReturn;

            if (yd.IsAsync)
            {
                return SuspendYieldDelegateAwait(frame, yd, innerResult, processingReturn);
            }

            var result = ProcessYieldDelegateInnerResult(frame, yd, innerResult);
            if (result.Suspended || result.Completed)
            {
                return result;
            }
        }
    }

    /// <summary>Generator/async resume prelude. Runs with the hot locals
    /// flushed to the frame (it pushes resume values or re-parks the frame).
    /// Returns true when the frame parked again and Dispatch must return
    /// <paramref name="result"/>.</summary>
    private bool RunContinuationPrelude(CallFrame frame, out JsValue result)
    {
        result = JsValue.Undefined;
        if (frame.Suspension is not { } suspension)
        {
            return false;
        }

        switch (suspension.ResumeAction)
        {
            case ContinuationResumeAction.IgnoreResume:
                _ = suspension.ConsumeResume();
                suspension.ClearContinuation();
                return false;
            case ContinuationResumeAction.PushResume:
                ApplyResumeToStack(frame);
                return false;
            case ContinuationResumeAction.AsyncGeneratorYieldAwait:
                {
                    var resume = suspension.ConsumeResume();
                    suspension.ClearContinuation();
                    if (resume.Kind == ResumeCompletionKind.Throw)
                    {
                        throw new JsThrow(resume.Value);
                    }

                    if (resume.Kind == ResumeCompletionKind.Return)
                    {
                        throw new JsReturnSentinel(resume.Value);
                    }

                    result = SuspendCurrent(frame, ContinuationResumeAction.PushResume, resume.Value, kind: 0);
                    return true;
                }
            case ContinuationResumeAction.YieldDelegate:
                if (suspension.YieldDelegate is null)
                {
                    return false;
                }

                try
                {
                    var step = RunYieldDelegateContinuation(frame, suspension.YieldDelegate);
                    if (step.Suspended)
                    {
                        result = JsValue.Undefined;
                        return true;
                    }
                    suspension.ClearContinuation();
                    PushFrame(frame, step.Value);
                    return false;
                }
                catch
                {
                    suspension.ClearContinuation();
                    throw;
                }
            default:
                return false;
        }
    }

    private void AttachGeneratedStack(JsThrow ex)
    {
        if (!ex.Value.IsObject)
        {
            return;
        }

        var error = ex.Value.AsObject;
        if (!IsErrorObject(error, _runtime.Realm))
        {
            return;
        }

        if (error.HasOwn("stack") && !ex.GeneratedStackAttached)
        {
            return;
        }

        error.DefineOwnProperty("stack",
            PropertyDescriptor.Data(JsValue.String(FormatJsStack(error, ex.StackFrames)),
                writable: true, enumerable: false, configurable: true));
        ex.GeneratedStackAttached = true;
    }

    private static bool IsErrorObject(JsObject obj, JsRealm realm)
    {
        for (var cur = obj; cur is not null; cur = cur.Prototype)
        {
            if (ReferenceEquals(cur, realm.ErrorPrototype))
            {
                return true;
            }
        }

        return false;
    }

    private static string FormatJsStack(JsObject error, List<JsStackFrame> frames)
    {
        var sb = new StringBuilder();
        AppendErrorHeader(sb, error);
        for (var i = 0; i < frames.Count; i++)
        {
            var frame = frames[i];
            sb.AppendLine();
            sb.Append("    at ");
            sb.Append(string.IsNullOrEmpty(frame.FunctionName) ? "<anonymous>" : frame.FunctionName);
            sb.Append(" (");
            sb.Append(string.IsNullOrEmpty(frame.SourceName) ? "<unknown>" : frame.SourceName);
            if (frame.Line is { } line)
            {
                sb.Append(':');
                sb.Append(line.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (frame.Column is { } column)
                {
                    sb.Append(':');
                    sb.Append(column.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            sb.Append(')');
        }
        return sb.ToString();
    }

    private static void AppendErrorHeader(StringBuilder sb, JsObject error)
    {
        var nameV = error.Get("name");
        var name = nameV.IsUndefined ? "Error" : JsValue.ToStringValue(nameV);
        var messageV = error.Get("message");
        var message = messageV.IsUndefined ? "" : JsValue.ToStringValue(messageV);

        if (name.Length == 0)
        {
            sb.Append(message);
        }
        else if (message.Length == 0)
        {
            sb.Append(name);
        }
        else
        {
            sb.Append(name).Append(": ").Append(message);
        }
    }

    /// <summary>§14.15 — divert a return through any enclosing finalizer.
    /// A null <paramref name="tryStack"/> is an empty one (lazily created by
    /// EnterTry): nothing to divert through.</summary>
    private static bool DivertReturnThroughFinally(Stack<TryFrame>? tryStack, JsValue value, ref int ip)
    {
        if (tryStack is null)
        {
            return false;
        }

        while (tryStack.Count > 0)
        {
            var frame = tryStack.Peek();
            if (frame.Phase != TryPhase.RunningFinally && frame.FinallyPc != -1)
            {
                frame.Phase = TryPhase.RunningFinally;
                frame.Pending = PendingCompletion.Return;
                frame.PendingValue = value;
                tryStack.Pop(); tryStack.Push(frame);
                ip = frame.FinallyPc;
                return true;
            }
            tryStack.Pop();
        }
        return false;
    }

    /// <summary>wp:M3-15 — drive a <c>break</c>/<c>continue</c> abrupt completion
    /// out of <paramref name="unwindCount"/> enclosing try-frames, running each
    /// frame's finalizer (innermost first) on the way to
    /// <paramref name="targetPc"/> (the loop/switch break/continue site).
    ///
    /// <para>Pops up to <paramref name="unwindCount"/> frames. A popped frame
    /// that is still in its try/catch phase and carries a finalizer suspends
    /// the unwind: its finalizer runs as a <see cref="PendingCompletion.Break"/>
    /// carrying the target PC and the remaining count, and <c>EndFinally</c>
    /// re-enters this helper for the rest. Frames without a finalizer (or
    /// already running one) are simply discarded. When all intervening
    /// finalizers have run, control jumps to <paramref name="targetPc"/>.</para>
    ///
    /// <para>Per §14.15.3 a finalizer that itself completes abruptly overrides
    /// the pending Break — that is handled naturally because the finalizer's
    /// own break/continue/return/throw opcodes re-drive the try-stack and never
    /// reach the <c>EndFinally</c> that would resume this one.</para></summary>
    private static void DivertBranchThroughFinally(
        Stack<TryFrame>? tryStack, int targetPc, int unwindCount, ref int ip)
    {
        while (unwindCount > 0 && tryStack is { Count: > 0 })
        {
            var frame = tryStack.Pop();
            unwindCount--;
            if (frame.Phase != TryPhase.RunningFinally && frame.FinallyPc != -1)
            {
                frame.Phase = TryPhase.RunningFinally;
                frame.Pending = PendingCompletion.Break;
                frame.PendingValue = JsValue.Undefined;
                frame.PendingTargetPc = targetPc;
                frame.PendingUnwindRemaining = unwindCount;
                tryStack.Push(frame);
                ip = frame.FinallyPc;
                return;
            }
        }
        // No (further) finalizer to run — jump straight to the loop/switch site.
        ip = targetPc;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Drain a JsArray (built by spread-into-array machinery) into
    /// the JsValue[] expected by <see cref="AbstractOperations.Call"/> and
    /// <see cref="AbstractOperations.Construct"/>.</summary>
    private static JsValue[] ExtractApplyArgs(JsValue argsArrV)
    {
        if (!argsArrV.IsObject || argsArrV.AsObject is not JsArray arr)
        {
            throw new InvalidOperationException("CallApply expects an Array of args on the stack");
        }

        var n = arr.Length;
        var dst = RentArgs(n);
        for (var i = 0; i < n; i++)
        {
            dst[i] = arr[i];
        }

        return dst;
    }

    /// <summary>JS '+': run ToPrimitive first; Symbols reject implicit string
    /// conversion per ECMA-262 §20.4, while explicit String(sym) is allowed by
    /// the String constructor. BigInt + BigInt is allowed; mixing BigInt with
    /// Number throws TypeError per §13.10.4.</summary>
    private JsValue JsAdd(JsValue a, JsValue b)
    {
        a = AbstractOperations.ToPrimitive(this, a);
        b = AbstractOperations.ToPrimitive(this, b);
        if (a.IsSymbol || b.IsSymbol)
        {
            throw new JsThrow(_runtime.Realm.NewTypeError("Cannot convert a Symbol value to a string"));
        }

        if (a.IsString || b.IsString)
        {
            return JsValue.String(JsValue.ToStringValue(a) + JsValue.ToStringValue(b));
        }

        if (a.IsBigInt || b.IsBigInt)
        {
            if (!(a.IsBigInt && b.IsBigInt))
            {
                throw BigIntOps.MixedTypeError(_runtime.Realm, "+");
            }

            return BigIntOps.Add(a.AsBigInt, b.AsBigInt);
        }
        return JsValue.Number(JsValue.ToNumber(a) + JsValue.ToNumber(b));
    }

    /// <summary>§13.10.2 InstanceofOperator. Consults
    /// <c>target[@@hasInstance]</c> first; if absent, falls back to
    /// §10.4.6.4 OrdinaryHasInstance which walks the prototype chain.
    /// Throws TypeError when the target is not callable.</summary>
    private bool InstanceofOperator(JsValue value, JsValue target)
    {
        if (!target.IsObject)
        {
            throw new JsThrow(_runtime.Realm.NewTypeError(
                "Right-hand side of 'instanceof' is not an object"));
        }

        var targetObj = target.AsObject;
        // §13.10.2 step 2: invoke the well-known method if defined anywhere
        // on the prototype chain.
        var hasInstance = AbstractOperations.Get(this, targetObj,
            JsPropertyKey.Symbol(Starling.Js.Intrinsics.SymbolCtor.HasInstance));
        if (!hasInstance.IsUndefined && !hasInstance.IsNull)
        {
            if (!AbstractOperations.IsCallable(hasInstance))
            {
                throw new JsThrow(_runtime.Realm.NewTypeError(
                    "Symbol.hasInstance method is not callable"));
            }

            var result = AbstractOperations.Call(this, hasInstance, target, new[] { value });
            return JsValue.ToBoolean(result);
        }
        // §10.4.6.4 OrdinaryHasInstance.
        if (!AbstractOperations.IsCallable(target))
        {
            throw new JsThrow(_runtime.Realm.NewTypeError(
                "Right-hand side of 'instanceof' is not callable"));
        }
        // Unwrap bound functions: instanceof checks against the bound target.
        var unwrapped = targetObj;
        while (unwrapped is JsBoundFunction bf)
        {
            unwrapped = bf.Target;
        }

        if (!value.IsObject)
        {
            return false;
        }

        var proto = AbstractOperations.Get(this, unwrapped, "prototype");
        if (!proto.IsObject)
        {
            throw new JsThrow(_runtime.Realm.NewTypeError(
                "Function has non-object prototype in instanceof check"));
        }

        var protoObj = proto.AsObject;
        for (var p = value.AsObject.Prototype; p is not null; p = p.Prototype)
        {
            if (ReferenceEquals(p, protoObj))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>§7.2.15 IsLooselyEqual with VM-aware object-to-primitive
    /// coercion. The value-only helper cannot call user JS methods.</summary>
    private bool AbstractEquals(JsValue a, JsValue b)
    {
        if (a.Kind == b.Kind)
        {
            return JsValue.StrictEquals(a, b);
        }

        if (a.IsNullish && b.IsNullish)
        {
            return true;
        }

        if (a.IsBoolean)
        {
            return AbstractEquals(JsValue.Number(a.AsBool ? 1 : 0), b);
        }

        if (b.IsBoolean)
        {
            return AbstractEquals(a, JsValue.Number(b.AsBool ? 1 : 0));
        }

        if (a.IsObject && IsPrimitiveComparableToObject(b))
        {
            return AbstractEquals(AbstractOperations.ToPrimitive(this, a), b);
        }

        if (b.IsObject && IsPrimitiveComparableToObject(a))
        {
            return AbstractEquals(a, AbstractOperations.ToPrimitive(this, b));
        }

        return JsValue.AbstractEquals(a, b);
    }

    private static bool IsPrimitiveComparableToObject(JsValue value)
        => value.IsString || value.IsNumber || value.IsBigInt || value.IsSymbol;

    /// <summary>§7.2.14 IsLessThan. Returns <c>null</c> for the spec's
    /// undefined result (NaN, invalid StringToBigInt), which makes <c>&lt;=</c>
    /// and <c>&gt;=</c> reject instead of becoming simple negations.</summary>
    private bool? RelationalLessThan(JsValue x, JsValue y, bool leftFirst)
    {
        JsValue px;
        JsValue py;
        if (leftFirst)
        {
            px = AbstractOperations.ToPrimitive(this, x, "number");
            py = AbstractOperations.ToPrimitive(this, y, "number");
        }
        else
        {
            py = AbstractOperations.ToPrimitive(this, y, "number");
            px = AbstractOperations.ToPrimitive(this, x, "number");
        }

        return LessThanPrimitives(px, py);
    }

    /// <summary>Primitive less-than after §7.2.14's ToPrimitive steps.
    /// Cross-type BigInt/Number compares numerically with care for non-integer
    /// doubles per §6.1.6.1.13.</summary>
    private bool? LessThanPrimitives(JsValue a, JsValue b)
    {
        if (a.IsString && b.IsString)
        {
            return string.CompareOrdinal(a.AsString, b.AsString) < 0;
        }

        if (a.IsBigInt && b.IsString)
        {
            if (!JsValue.TryStringToBigInt(b.AsString, out var rhs))
            {
                return null;
            }

            return a.AsBigInt < rhs;
        }
        if (a.IsString && b.IsBigInt)
        {
            if (!JsValue.TryStringToBigInt(a.AsString, out var lhs))
            {
                return null;
            }

            return lhs < b.AsBigInt;
        }

        a = AbstractOperations.ToNumeric(_runtime.Realm, a);
        b = AbstractOperations.ToNumeric(_runtime.Realm, b);
        if (a.IsBigInt && b.IsBigInt)
        {
            return BigIntOps.LessThan(a.AsBigInt, b.AsBigInt);
        }

        if (a.IsBigInt && b.IsNumber)
        {
            return BigIntLessThanNumber(a.AsBigInt, b.AsNumber);
        }

        if (a.IsNumber && b.IsBigInt)
        {
            return NumberLessThanBigInt(a.AsNumber, b.AsBigInt);
        }

        var ad = a.AsNumber;
        var bd = b.AsNumber;
        if (double.IsNaN(ad) || double.IsNaN(bd))
        {
            return null;
        }

        return ad < bd;
    }

    /// <summary>BigInt &lt; Number per §6.1.6.1.13. NaN → false; infinities
    /// compare sign-wise; finite non-integers compare against the BigInt by
    /// flooring the double on the BigInt's side.</summary>
    private static bool? BigIntLessThanNumber(System.Numerics.BigInteger a, double n)
    {
        if (double.IsNaN(n))
        {
            return null;
        }

        if (double.IsPositiveInfinity(n))
        {
            return true;
        }

        if (double.IsNegativeInfinity(n))
        {
            return false;
        }
        // Compare exactly when the double is an integer; otherwise compare to
        // floor(n) and decide by the fractional sign (n > floor(n) ⇒ a < n
        // iff a ≤ floor(n)).
        if (n == Math.Truncate(n))
        {
            return a < new System.Numerics.BigInteger(n);
        }

        var floor = new System.Numerics.BigInteger(Math.Floor(n));
        return a <= floor;
    }

    private static bool? NumberLessThanBigInt(double n, System.Numerics.BigInteger b)
    {
        if (double.IsNaN(n))
        {
            return null;
        }

        if (double.IsPositiveInfinity(n))
        {
            return false;
        }

        if (double.IsNegativeInfinity(n))
        {
            return true;
        }

        if (n == Math.Truncate(n))
        {
            return new System.Numerics.BigInteger(n) < b;
        }
        // n is not an integer: n < b iff ceil(n) ≤ b
        var ceil = new System.Numerics.BigInteger(Math.Ceiling(n));
        return ceil <= b;
    }

    private static int ToInt32(JsValue v)
    {
        var d = JsValue.ToNumber(v);
        if (double.IsNaN(d) || double.IsInfinity(d) || d == 0)
        {
            return 0;
        }

        var i = Math.Truncate(d);
        var mod = i - Math.Floor(i / 4294967296.0) * 4294967296.0;
        return unchecked((int)(uint)mod);
    }

    /// <summary>§13.6 ApplyStringOrNumericBinaryOperator step 1 (and ToNumeric
    /// §7.1.22 step 1): coerce an operand of a numeric/bitwise binary operator to
    /// its primitive via ToPrimitive(number) so an object operand (e.g.
    /// <c>new Number(7)</c>, <c>new String("2")</c>, <c>new Boolean(true)</c>)
    /// converts through its <c>valueOf</c>/<c>toString</c> rather than yielding
    /// NaN. Primitives are returned unchanged so the existing fast paths
    /// (<see cref="JsValue.ToNumber"/>, <see cref="ToInt32"/>, the BigInt
    /// branches) are untouched. The static <see cref="JsValue.ToNumber"/> cannot
    /// do this itself — it has no VM/realm to dispatch the user method. Used by
    /// the arithmetic (<c>- * / % **</c>) and bitwise (<c>| &amp; ^ &lt;&lt;
    /// &gt;&gt; &gt;&gt;&gt;</c>) ops, which compound assignment reuses, so an
    /// object operand coerces identically whether written as <c>a * b</c> or
    /// <c>a *= b</c>. (<c>+</c> already runs ToPrimitive in <see cref="JsAdd"/>.)</summary>
    private JsValue ToNumericOperand(JsValue v)
        => AbstractOperations.ToNumeric(_runtime.Realm, v);


    /// <summary>B1b-2a — build a class constructor at <c>BuildClass</c>-opcode
    /// dispatch time. Sets up the prototype chain, installs methods/static
    /// members, stamps the instance field initializer table on the
    /// constructor, and runs static initializers in declaration order.</summary>
    private JsValue BuildClassRuntime(
        Starling.Js.Bytecode.ClassTemplate template,
        JsValue baseClassValue,
        JsValue[] ctorUpvalues,
        JsValue[][] methodUpvalues,
        JsValue[][] fieldUpvalues,
        JsValue[][] staticBlockUpvalues,
        JsValue[] methodComputedKeys,
        JsValue[] fieldComputedKeys,
        Cell? selfNameCell = null)
    {
        var realm = _runtime.Realm;
        JsObject? parentCtor = null;
        JsObject? parentProto;
        if (template.HasExtends)
        {
            if (baseClassValue.IsNull)
            {
                // null prototype — class extends null gives a proto-less chain.
                parentProto = null;
                parentCtor = null;
            }
            else if (baseClassValue.IsObject && AbstractOperations.IsConstructor(baseClassValue))
            {
                parentCtor = baseClassValue.AsObject;
                var protoSlot = parentCtor.Get("prototype");
                if (protoSlot.IsObject)
                {
                    parentProto = protoSlot.AsObject;
                }
                else if (protoSlot.IsNull)
                {
                    parentProto = null;
                }
                else
                {
                    throw new JsThrow(realm.NewTypeError("Class extends value's prototype is not an object or null"));
                }
            }
            else
            {
                throw new JsThrow(realm.NewTypeError("Class extends value is not a constructor"));
            }
        }
        else
        {
            parentProto = realm.ObjectPrototype;
            parentCtor = null;
        }

        // Build the prototype object for instance methods.
        var protoObj = new JsObject(parentProto);

        // Build the constructor function instance.
        var ctorInstance = JsFunction.CreateInstance(realm, template.ConstructorTemplate, ctorUpvalues);
        // Override `prototype` to point at our prototype object, and link
        // constructor back. Spec defaults: writable=false, enumerable=false,
        // configurable=false (we use configurable=true to permit JSON-style
        // overrides in test harnesses; functionally equivalent for the
        // tests in B1b-2a).
        // Match the writable=true bit set by JsFunction.CreateInstance so the
        // §10.1.6.3 same-attributes check accepts the value swap; the class
        // prototype slot is logically non-writable per spec, but writability
        // is mostly observable via Object.defineProperty and we accept that
        // (small) divergence for now.
        ctorInstance.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(protoObj), writable: true, enumerable: false, configurable: false));
        protoObj.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctorInstance), writable: true, enumerable: false, configurable: true));
        // Name override — class name takes precedence over template name.
        if (!string.IsNullOrEmpty(template.Name))
        {
            ctorInstance.DefineOwnProperty("name",
                PropertyDescriptor.Data(JsValue.String(template.Name), writable: false, enumerable: false, configurable: true));
        }

        // Static inheritance: constructor's [[Prototype]] = parent ctor (or
        // Function.prototype for base classes — already wired by CreateInstance).
        if (parentCtor is not null)
        {
            ctorInstance.SetPrototypeOf(parentCtor);
        }

        ctorInstance.HomeObject = protoObj;

        // Install methods. Private instance methods/accessors live on the shared
        // prototype, but each instance must carry their brand in its own
        // [[PrivateElements]] set (collected here, applied at construction).
        // Static private members brand the constructor object directly.
        List<string>? instancePrivateBrands = null;
        for (var i = 0; i < template.Methods.Count; i++)
        {
            var m = template.Methods[i];
            var fnInstance = JsFunction.CreateInstance(realm, m.Template, methodUpvalues[i]);
            fnInstance.HomeObject = m.IsStatic ? ctorInstance : protoObj;
            var owner = m.IsStatic ? (JsObject)ctorInstance : protoObj;
            if (m.IsComputed)
            {
                // wp:M3-04f — coerced key value (Symbol or String) off the stack.
                var keyPk = AbstractOperations.ToPropertyKey(this, methodComputedKeys[i]);
                // §15.7.10 — a static class method/accessor may not be named
                // "prototype" (a computed key resolving to it → TypeError).
                if (m.IsStatic && keyPk.IsString && keyPk.AsString == "prototype")
                {
                    throw new JsThrow(realm.NewTypeError(
                        "Classes may not have a static property named 'prototype'"));
                }

                StampMethodName(fnInstance, keyPk, m.Kind);
                InstallMethodOrAccessor(owner, keyPk, m.Kind, fnInstance);
            }
            else
            {
                var keyForInstall = m.MangledPrivateKey ?? m.StaticKey!;
                InstallMethodOrAccessor(owner, keyForInstall, m.Kind, fnInstance);
                if (m.MangledPrivateKey is { } mangledMethod)
                {
                    if (m.IsStatic)
                    {
                        // §15.7.10 — only the constructor object carries the brand
                        // for a static private method/accessor.
                        ctorInstance.AddPrivateBrand(mangledMethod);
                    }
                    else
                    {
                        (instancePrivateBrands ??= new List<string>()).Add(mangledMethod);
                    }
                }
            }
        }
        if (instancePrivateBrands is not null)
        {
            ctorInstance.InstancePrivateBrands = instancePrivateBrands;
        }

        // §15.7.14 — for a class *declaration*, bind the class name to the
        // constructor BEFORE static elements run, so `static { … C … }` blocks
        // and `static p = C.q` field initializers can reference the class by
        // name. (Class expressions keep their inner name scoped to the body;
        // BindNameToGlobal is false for them, so this does not leak.)
        if (template.BindNameToGlobal && template.Name.Length > 0)
        {
            AbstractOperations.Set(this, realm.GlobalObject, template.Name,
                JsValue.Object(ctorInstance), JsValue.Object(realm.GlobalObject));
        }

        // §15.7.14 — for a named class expression, initialize the inner
        // class-name binding to the constructor here, BEFORE static field
        // initializers and static blocks run. Static elements (and instance
        // method/field closures) capture this same Cell as an upvalue, so the
        // store makes `new Inner()` / `Inner.x` resolve during static init.
        // The compiler also re-stores the value after BuildClass returns (for
        // closures formed later); both writes target the same cell, so this is
        // idempotent.
        if (selfNameCell is not null)
        {
            selfNameCell.Value = JsValue.Object(ctorInstance);
        }

        // Static fields + static blocks: run in interleaved declaration order
        // per ES2022. Field thunks and static-block thunks both invoked with
        // this = constructor.
        // Walk fields & static blocks in declaration order. For simplicity
        // here we run all static fields then all static blocks; the spec
        // interleaves them but the test suite for B1b-2a doesn't depend on
        // the interleaving across both kinds. Pin a follow-up if Google's
        // bundles depend on the spec ordering.
        var instanceFieldInits = new List<InstanceFieldInit>();
        for (var i = 0; i < template.Fields.Count; i++)
        {
            var f = template.Fields[i];
            // wp:M3-04f — computed key resolved at class-definition time.
            JsPropertyKey? computedKey = f.IsComputed
                ? AbstractOperations.ToPropertyKey(this, fieldComputedKeys[i])
                : (JsPropertyKey?)null;
            if (f.IsStatic)
            {
                if (computedKey is { } sck)
                {
                    // §15.7.10 — a static field may not be named "prototype".
                    if (sck.IsString && sck.AsString == "prototype")
                    {
                        throw new JsThrow(realm.NewTypeError(
                            "Classes may not have a static property named 'prototype'"));
                    }
                    // Static computed field: the thunk (when present) returns the
                    // initializer value; absent initializer ⇒ undefined.
                    var value = JsValue.Undefined;
                    if (f.InitializerTemplate is not null)
                    {
                        var initFnC = JsFunction.CreateInstance(realm, f.InitializerTemplate, fieldUpvalues[i]);
                        initFnC.HomeObject = ctorInstance;
                        value = AbstractOperations.Call(this, JsValue.Object(initFnC), JsValue.Object(ctorInstance), Array.Empty<JsValue>());
                    }
                    ctorInstance.DefineOwnProperty(sck,
                        PropertyDescriptor.Data(value, writable: true, enumerable: true, configurable: true));
                    continue;
                }
                if (f.InitializerTemplate is null)
                {
                    var key = f.MangledPrivateKey ?? f.StaticKey!;
                    if (f.MangledPrivateKey is not null)
                    {
                        ctorInstance.DefineOwnProperty(key,
                            PropertyDescriptor.Data(JsValue.Undefined, writable: true, enumerable: false, configurable: false));
                        // §15.7.10 — only the constructor object carries the brand
                        // for a static private field.
                        ctorInstance.AddPrivateBrand(key);
                    }
                    else
                    {
                        ctorInstance.DefineOwnProperty(key,
                            PropertyDescriptor.Data(JsValue.Undefined, writable: true, enumerable: true, configurable: true));
                    }
                    continue;
                }
                var initFn = JsFunction.CreateInstance(realm, f.InitializerTemplate, fieldUpvalues[i]);
                initFn.HomeObject = ctorInstance;
                AbstractOperations.Call(this, JsValue.Object(initFn), JsValue.Object(ctorInstance), Array.Empty<JsValue>());
            }
            else if (computedKey is { } ick)
            {
                // Instance computed field: defer the (already-coerced) key + the
                // initializer thunk to construction time. The thunk returns the
                // value; RunFieldInits defines the property under ick. An absent
                // initializer ⇒ a thunk returning undefined.
                var thunk = f.InitializerTemplate is not null
                    ? JsFunction.CreateInstance(realm, f.InitializerTemplate, fieldUpvalues[i])
                    : MakeUndefinedReturningThunk(realm);
                // wp:M3-71 — an instance field initializer has [[HomeObject]] =
                // the class prototype so `super.x` (incl. inside a direct eval in
                // the initializer) resolves against the parent prototype.
                thunk.HomeObject = protoObj;
                instanceFieldInits.Add(new InstanceFieldInit("", IsPrivate: false, thunk, ick));
            }
            else
            {
                // Instance field — collect for later use during construction.
                var thunkInit = f.InitializerTemplate;
                if (thunkInit is null)
                {
                    // No initializer — still need to define slot at construction time.
                    var nullThunk = MakeUndefinedFieldThunk(realm, f);
                    instanceFieldInits.Add(new InstanceFieldInit(
                        f.MangledPrivateKey ?? f.StaticKey!,
                        f.MangledPrivateKey is not null,
                        nullThunk));
                }
                else
                {
                    var initFn = JsFunction.CreateInstance(realm, thunkInit, fieldUpvalues[i]);
                    // wp:M3-71 — [[HomeObject]] = class prototype so `super.x`
                    // (incl. inside a direct eval in the initializer) resolves.
                    initFn.HomeObject = protoObj;
                    instanceFieldInits.Add(new InstanceFieldInit(
                        f.MangledPrivateKey ?? f.StaticKey!,
                        f.MangledPrivateKey is not null,
                        initFn));
                }
            }
        }
        if (instanceFieldInits.Count > 0)
        {
            ctorInstance.InstanceFieldInitializers = instanceFieldInits;
        }

        // Static blocks — run with this=constructor.
        for (var i = 0; i < template.StaticBlocks.Count; i++)
        {
            var sb = template.StaticBlocks[i];
            var sbFn = JsFunction.CreateInstance(realm, sb.Template, staticBlockUpvalues[i]);
            sbFn.HomeObject = ctorInstance;
            AbstractOperations.Call(this, JsValue.Object(sbFn), JsValue.Object(ctorInstance), Array.Empty<JsValue>());
        }

        return JsValue.Object(ctorInstance);
    }

    /// <summary>§13.2.8.4 — materialise a tagged template's frozen strings
    /// object: a cooked array carrying a frozen <c>.raw</c> sibling. Both arrays
    /// are integrity-frozen (own props non-writable/non-configurable, then
    /// non-extensible) so a tag function can't mutate the shared, cached object.</summary>
    private JsArray BuildTemplateObject(TemplateObjectTemplate tmpl)
    {
        var realm = _runtime.Realm;

        var rawItems = new JsValue[tmpl.Raw.Count];
        for (var i = 0; i < rawItems.Length; i++)
        {
            rawItems[i] = JsValue.String(tmpl.Raw[i]);
        }

        var rawArr = new JsArray(realm, rawItems);
        FreezeOwnProperties(rawArr);

        var cookedItems = new JsValue[tmpl.Cooked.Count];
        for (var i = 0; i < cookedItems.Length; i++)
        {
            cookedItems[i] = tmpl.Cooked[i] is { } s ? JsValue.String(s) : JsValue.Undefined;
        }

        var cooked = new JsArray(realm, cookedItems);
        cooked.DefineOwnProperty("raw",
            PropertyDescriptor.Data(JsValue.Object(rawArr), writable: false, enumerable: false, configurable: false));
        FreezeOwnProperties(cooked);

        return cooked;
    }

    private static void FreezeOwnProperties(JsObject obj)
    {
        foreach (var key in new List<JsPropertyKey>(obj.OwnPropertyKeys))
        {
            var d = obj.GetOwnPropertyDescriptor(key);
            if (d is null)
            {
                continue;
            }

            var desc = d.Value;
            obj.DefineOwnProperty(key, desc.IsAccessor
                ? PropertyDescriptor.Accessor(desc.Getter, desc.Setter, desc.Enumerable, configurable: false)
                : PropertyDescriptor.Data(desc.Value, writable: false, enumerable: desc.Enumerable, configurable: false));
        }
        obj.PreventExtensions();
    }

    /// <summary>Locate a private member's descriptor for a mangled name: a
    /// private field lives as an OWN property on the instance (defined via
    /// <see cref="Opcode.DefinePrivateField"/>); a private method/accessor lives
    /// on the class prototype (or the constructor, for statics). Returns the
    /// descriptor and whether it was found as an own property of
    /// <paramref name="obj"/> (i.e. a field, which the caller may write
    /// directly).</summary>
    private static (PropertyDescriptor? Desc, bool Own) FindPrivateDescriptor(JsObject obj, string name)
    {
        var own = obj.GetOwnPropertyDescriptor(name);
        if (own is not null)
        {
            return (own, true);
        }

        for (var o = obj.Prototype; o is not null; o = o.Prototype)
        {
            var d = o.GetOwnPropertyDescriptor(name);
            if (d is not null)
            {
                return (d, false);
            }
        }
        return (null, false);
    }

    private static void InstallMethodOrAccessor(JsObject owner, string key, Starling.Js.Bytecode.ClassMethodKind kind, JsFunction fn)
        => InstallMethodOrAccessor(owner, JsPropertyKey.String(key), kind, fn);

    /// <summary>wp:M3-04f — install a method/accessor under a runtime property
    /// key (String <em>or</em> Symbol), used for computed class members such
    /// as <c>[Symbol.iterator]()</c>. Mirrors the string-keyed path exactly,
    /// merging an existing accessor's complementary half so a paired
    /// <c>get [k]()/set [k]()</c> shares one descriptor.</summary>
    private static void InstallMethodOrAccessor(JsObject owner, JsPropertyKey key, Starling.Js.Bytecode.ClassMethodKind kind, JsFunction fn)
    {
        switch (kind)
        {
            case Starling.Js.Bytecode.ClassMethodKind.Method:
                owner.DefineOwnProperty(key,
                    PropertyDescriptor.Data(JsValue.Object(fn), writable: true, enumerable: false, configurable: true));
                break;
            case Starling.Js.Bytecode.ClassMethodKind.Get:
                {
                    var existing = owner.GetOwnPropertyDescriptor(key);
                    var setter = existing is { IsAccessor: true } existingDesc ? existingDesc.Setter : null;
                    owner.DefineOwnProperty(key, PropertyDescriptor.Accessor(fn, setter, enumerable: false, configurable: true));
                    break;
                }
            case Starling.Js.Bytecode.ClassMethodKind.Set:
                {
                    var existing = owner.GetOwnPropertyDescriptor(key);
                    var getter = existing is { IsAccessor: true } existingDesc ? existingDesc.Getter : null;
                    owner.DefineOwnProperty(key, PropertyDescriptor.Accessor(getter, fn, enumerable: false, configurable: true));
                    break;
                }
        }
    }

    /// <summary>wp:M3-26 — install an object-literal accessor (getter/setter).
    /// Mirrors <see cref="InstallMethodOrAccessor(JsObject, JsPropertyKey, Starling.Js.Bytecode.ClassMethodKind, JsFunction)"/>
    /// but marks the descriptor <em>enumerable</em> per §13.2.5 (object-literal
    /// accessors are enumerable; class accessors are not). Merges an existing
    /// accessor's complementary half so a paired <c>get x()/set x()</c> on the
    /// same key shares one descriptor, and stamps the §13.2.5.5 "name"
    /// ("get x"/"set x").</summary>
    private static void InstallObjectAccessor(JsObject owner, JsPropertyKey key, bool isGetter, JsFunction fn)
    {
        StampMethodName(fn, key, isGetter
            ? Starling.Js.Bytecode.ClassMethodKind.Get
            : Starling.Js.Bytecode.ClassMethodKind.Set);
        var existing = owner.GetOwnPropertyDescriptor(key);
        var prevAccessor = existing is { IsAccessor: true } d ? d : (PropertyDescriptor?)null;
        var getter = isGetter ? fn : prevAccessor?.Getter;
        var setter = isGetter ? prevAccessor?.Setter : fn;
        owner.DefineOwnProperty(key,
            PropertyDescriptor.Accessor(getter, setter, enumerable: true, configurable: true));
    }

    /// <summary>wp:M3-04f — stamp the §13.2.5.5 / §15.7.5 "name" own property
    /// for a computed-key method. String keys use the key text directly;
    /// Symbol keys use <c>[description]</c> (empty for unnamed Symbols).
    /// Getters/setters prefix "get "/"set ".</summary>
    private static void StampMethodName(JsFunction fn, JsPropertyKey key, Starling.Js.Bytecode.ClassMethodKind kind)
    {
        string baseName = FunctionNameFromPropertyKey(key);
        string prefix = kind switch
        {
            Starling.Js.Bytecode.ClassMethodKind.Get => "get ",
            Starling.Js.Bytecode.ClassMethodKind.Set => "set ",
            _ => "",
        };
        fn.DefineOwnProperty("name",
            PropertyDescriptor.Data(JsValue.String(prefix + baseName), writable: false, enumerable: false, configurable: true));
    }

    private static string FunctionNameFromPropertyKey(JsPropertyKey key)
        => key.IsSymbol
            ? (key.AsSymbol.Description is { } d ? "[" + d + "]" : "")
            : key.AsString;

    private static void StampInferredFunctionName(JsValue target, string name)
    {
        if (!target.IsObject || target.AsObject is not JsFunction fn)
        {
            return;
        }

        var cur = fn.GetOwnPropertyDescriptor("name");
        var isAnon = cur is null
            || (cur.Value.IsData && cur.Value.Value.IsString && cur.Value.Value.AsString.Length == 0);
        if (!isAnon)
        {
            return;
        }

        fn.DefineOwnProperty("name",
            PropertyDescriptor.Data(JsValue.String(name), writable: false, enumerable: false, configurable: true));
    }

    /// <summary>wp:M3-04f — a zero-arg thunk that simply returns
    /// <c>undefined</c>, used for an instance computed field with no
    /// initializer. The runtime then defines the own property under the
    /// pre-resolved computed key.</summary>
    private static JsFunction MakeUndefinedReturningThunk(JsRealm realm)
    {
        var b = new Starling.Js.Bytecode.ChunkBuilder();
        b.Emit(Starling.Js.Bytecode.Opcode.LoadUndefined);
        b.Emit(Starling.Js.Bytecode.Opcode.Return);
        var chunk = b.Build("#field-init-undef-computed");
        var tmpl = new JsFunction("", chunk, 0);
        return JsFunction.CreateInstance(realm, tmpl, Array.Empty<JsValue>());
    }

    private static JsFunction MakeUndefinedFieldThunk(JsRealm realm, Starling.Js.Bytecode.FieldEntry field)
    {
        // Synthesize a tiny chunk: `this.key = undefined;` (or DefinePrivateField).
        var b = new Starling.Js.Bytecode.ChunkBuilder();
        if (field.MangledPrivateKey is not null)
        {
            // [this, undefined, DefinePrivateField]
            b.Emit(Starling.Js.Bytecode.Opcode.LoadThis);
            b.Emit(Starling.Js.Bytecode.Opcode.LoadUndefined);
            b.EmitU16(Starling.Js.Bytecode.Opcode.DefinePrivateField, b.AddConstant(field.MangledPrivateKey));
        }
        else
        {
            b.Emit(Starling.Js.Bytecode.Opcode.LoadThis);
            b.Emit(Starling.Js.Bytecode.Opcode.LoadUndefined);
            b.EmitU16(Starling.Js.Bytecode.Opcode.StoreProperty, b.AddConstant(field.StaticKey!));
            b.Emit(Starling.Js.Bytecode.Opcode.Pop);
        }
        b.Emit(Starling.Js.Bytecode.Opcode.ReturnUndefined);
        var chunk = b.Build("#field-init-undef");
        var tmpl = new JsFunction("", chunk, 0);
        return JsFunction.CreateInstance(realm, tmpl, Array.Empty<JsValue>());
    }

    // =====================================================================
    //               B1b-2c — Generator / Async dispatch
    // =====================================================================

    /// <summary>Invoke a generator function — set up a JsGenerator wrapper
    /// whose heap-backed frame runs lazily on the first <c>.next()</c> call.</summary>
    internal JsValue StartGeneratorBody(JsFunction fn, JsValue thisValue, JsValue[] args)
    {
        var realm = _runtime.Realm;
        var frame = new SuspendedFrame(this);
        var gen = new JsGenerator(realm, frame);
        // Stamp own properties so duck-typing tests work.
        // Deep-copy: the caller's args array is pooled and reused after this
        // synchronous call returns, but the suspended frame keeps a reference to
        // its arguments across resumes.
        var argsCopy = args.Length == 0 ? args : (JsValue[])args.Clone();
        var thisCopy = thisValue;
        var fnCopy = fn;
        frame.Start(() =>
        {
            var rv = RunBarrier(fnCopy.Body, argsCopy, thisCopy, fnCopy.Upvalues,
                drainMicrotasks: false, currentFunction: fnCopy, newTarget: null,
                suspension: frame);
            if (!frame.Suspended)
            {
                frame.SetReturnValue(rv);
            }
        });
        // §15.5.2 EvaluateGeneratorBody — FunctionDeclarationInstantiation (the
        // parameter-binding prologue) runs synchronously here, BEFORE the
        // generator object is returned. RunPrologue drives the frame to the
        // PrologueEnd marker; a throw from param destructuring / defaults /
        // RequireObjectCoercible / iterator protocol propagates to the caller
        // now (no generator object is produced).
        if (fn.Body.HasPrologue)
        {
            RunPrologue(frame);
        }

        return JsValue.Object(gen);
    }

    /// <summary>§10.2.1.3 — drive the frame through the synchronous
    /// parameter-binding prologue. Resumes once (which runs everything up to the
    /// body's <see cref="Opcode.PrologueEnd"/> marker). If the prologue threw,
    /// re-raises it on the caller; if it ran to completion (an
    /// empty/return-only body with no marker — defensive) the throw still
    /// surfaces. On success the frame is parked at PrologueEnd, ready for the
    /// first real resume.</summary>
    private static void RunPrologue(SuspendedFrame frame)
    {
        frame.Resume(JsValue.Undefined);
        if (frame.Completed && frame.ThrewUncaught)
        {
            throw new JsThrow(frame.ReturnValue);
        }
    }

    /// <summary>Invoke an async function — set up an outer Promise and a
    /// heap-backed continuation frame. Returns the outer Promise immediately;
    /// the body settles it on completion or an unhandled throw.</summary>
    internal JsValue StartAsyncBody(JsFunction fn, JsValue thisValue, JsValue[] args)
    {
        var realm = _runtime.Realm;
        var outer = new JsPromise(realm.PromisePrototype);
        var frame = new SuspendedFrame(this);
        var state = new JsAsyncFunctionState(frame, outer);

        var fnCopy = fn;
        // Deep-copy: the caller's args array is pooled and reused after this
        // synchronous call returns, but the suspended frame keeps a reference to
        // its arguments across awaits.
        var argsCopy = args.Length == 0 ? args : (JsValue[])args.Clone();
        var thisCopy = thisValue;
        frame.Start(() =>
        {
            var rv = RunBarrier(fnCopy.Body, argsCopy, thisCopy, fnCopy.Upvalues,
                drainMicrotasks: false, currentFunction: fnCopy, newTarget: null,
                suspension: frame);
            if (!frame.Suspended)
            {
                frame.SetReturnValue(rv);
            }
        });

        // §27.7.5.2 AsyncFunctionStart — FunctionDeclarationInstantiation (the
        // parameter-binding prologue) runs synchronously at call time. Per
        // §27.7.5.1, an abrupt completion from the prologue REJECTS the returned
        // promise (it is NOT thrown synchronously — the function still returns a
        // promise). RunPrologueAsync runs the frame to the PrologueEnd marker;
        // if it threw, reject `outer` and skip driving the body. Synthetic async
        // bodies without a marker (top-level-await module wrappers) skip this.
        if (fn.Body.HasPrologue && RunPrologueAsync(state))
        {
            return JsValue.Object(outer);
        }

        // Drive the frame synchronously on this thread, riding each await
        // suspension via the microtask queue. The first Resume kicks off the
        // body; subsequent Resumes are wired by the await handler below.
        DriveAsync(state);
        return JsValue.Object(outer);
    }

    /// <summary>§27.7.5.1 — run the async body's parameter-binding prologue
    /// synchronously. Returns true if the prologue threw (in which case the
    /// outer promise has been rejected and the body must not be driven); false
    /// when the frame parked cleanly at <see cref="Opcode.PrologueEnd"/>.</summary>
    private bool RunPrologueAsync(JsAsyncFunctionState state)
    {
        var frame = state.Frame;
        frame.Resume(JsValue.Undefined);
        if (frame.Completed && frame.ThrewUncaught)
        {
            state.Settled = true;
            PromiseCtor.Reject(_runtime.Realm, state.OuterPromise, frame.ReturnValue);
            return true;
        }
        return false;
    }

    /// <summary>Run the async body forward until the next await or
    /// completion. If the body awaits a value, schedules a .then on the
    /// Promise.resolve(value) so the frame resumes after the awaited
    /// settlement.</summary>
    private void DriveAsync(JsAsyncFunctionState state)
    {
        var realm = _runtime.Realm;
        var frame = state.Frame;
        // Initial kick: pass Undefined as resume value. The frame starts
        // executing and either runs to completion or hits a Suspend.
        frame.Resume(JsValue.Undefined);
        if (frame.Completed)
        {
            SettleAsync(state);
            return;
        }
        // Hit an await — frame.YieldedValue holds the awaited value.
        ScheduleAwait(state);
    }

    private void ScheduleAwait(JsAsyncFunctionState state)
    {
        var realm = _runtime.Realm;
        var awaited = state.Frame.YieldedValue;
        // Wrap value in a Promise (Promise.resolve semantics).
        JsPromise inner;
        if (awaited.IsObject && awaited.AsObject is JsPromise existing)
        {
            inner = existing;
        }
        else
        {
            inner = new JsPromise(realm.PromisePrototype);
            Starling.Js.Intrinsics.PromiseCtor.Resolve(realm, inner, awaited);
        }

        var onFulfill = new JsNativeFunction("", (thisV, args) =>
        {
            var v = args.Length > 0 ? args[0] : JsValue.Undefined;
            state.Frame.Resume(v, withThrow: false);
            if (state.Frame.Completed)
            {
                SettleAsync(state);
            }
            else
            {
                ScheduleAwait(state);
            }

            return JsValue.Undefined;
        }, isConstructor: false);
        var onReject = new JsNativeFunction("", (thisV, args) =>
        {
            var r = args.Length > 0 ? args[0] : JsValue.Undefined;
            state.Frame.Resume(r, withThrow: true);
            if (state.Frame.Completed)
            {
                SettleAsync(state);
            }
            else
            {
                ScheduleAwait(state);
            }

            return JsValue.Undefined;
        }, isConstructor: false);

        // Call inner.then(onFulfill, onReject).
        var then = AbstractOperations.Get(this, inner, "then");
        AbstractOperations.Call(this, then, JsValue.Object(inner),
            new[] { JsValue.Object(onFulfill), JsValue.Object(onReject) });
    }

    private void SettleAsync(JsAsyncFunctionState state)
    {
        if (state.Settled)
        {
            return;
        }

        state.Settled = true;
        var realm = _runtime.Realm;
        if (state.Frame.ThrewUncaught)
        {
            Starling.Js.Intrinsics.PromiseCtor.Reject(realm, state.OuterPromise, state.Frame.ReturnValue);
        }
        else
        {
            Starling.Js.Intrinsics.PromiseCtor.Resolve(realm, state.OuterPromise, state.Frame.ReturnValue);
        }
    }

    /// <summary>wp:M3-04g — invoke an <c>async function*</c>. Sets up a
    /// <see cref="JsAsyncGenerator"/> whose heap-backed frame runs the body lazily
    /// on the first request. The body interleaves <c>yield</c> (kind 0) and
    /// <c>await</c> (kind 1) suspensions through the shared
    /// <see cref="SuspendedFrame"/>; the driver
    /// (<see cref="AsyncGeneratorDrainQueue"/>) tells them apart via
    /// <see cref="SuspendedFrame.SuspendKind"/>.</summary>
    internal JsValue StartAsyncGeneratorBody(JsFunction fn, JsValue thisValue, JsValue[] args)
    {
        var realm = _runtime.Realm;
        var frame = new SuspendedFrame(this);
        var gen = new JsAsyncGenerator(realm, frame);
        var fnCopy = fn;
        // Deep-copy: the caller's args array is pooled and reused after this
        // synchronous call returns, but the suspended frame keeps a reference to
        // its arguments across resumes.
        var argsCopy = args.Length == 0 ? args : (JsValue[])args.Clone();
        var thisCopy = thisValue;
        frame.Start(() =>
        {
            var rv = RunBarrier(fnCopy.Body, argsCopy, thisCopy, fnCopy.Upvalues,
                drainMicrotasks: false, currentFunction: fnCopy, newTarget: null,
                suspension: frame);
            if (!frame.Suspended)
            {
                frame.SetReturnValue(rv);
            }
        });
        // §27.4 EvaluateAsyncGeneratorBody — like sync generators, the parameter-
        // binding prologue runs synchronously at call time and a throw propagates
        // to the caller before the async-generator object is produced.
        if (fn.Body.HasPrologue)
        {
            RunPrologue(frame);
        }

        return JsValue.Object(gen);
    }

    /// <summary>wp:M3-04g — §7.4.2 GetIterator(obj, async) for
    /// <c>for await…of</c>. Resolves <c>obj[@@asyncIterator]</c>; when absent,
    /// builds the record from the sync <c>[Symbol.iterator]</c> and marks it
    /// sync-wrapped so the driver lifts each result into a Promise
    /// (CreateAsyncFromSyncIterator, §27.1.4.1).</summary>
    private Starling.Js.Intrinsics.JsIteratorRecordHandle GetAsyncIteratorHandle(JsValue iterable)
    {
        var realm = _runtime.Realm;
        if (iterable.IsNullish)
        {
            throw new JsThrow(realm.NewTypeError("value is not async iterable"));
        }

        var asyncMethod = AbstractOperations.GetMethod(this, iterable,
            Starling.Js.Intrinsics.SymbolCtor.AsyncIterator);
        if (!asyncMethod.IsUndefined && !asyncMethod.IsNull)
        {
            var iter = AbstractOperations.Call(this, asyncMethod, iterable, Array.Empty<JsValue>());
            if (!iter.IsObject)
            {
                throw new JsThrow(realm.NewTypeError("async iterator method did not return an object"));
            }

            var nextMethod = AbstractOperations.Get(this, iter.AsObject, "next");
            return new Starling.Js.Intrinsics.JsIteratorRecordHandle(
                new IteratorRecord(iter, nextMethod, Done: false));
        }

        // No @@asyncIterator — wrap the sync iterator.
        var syncRecord = AbstractOperations.GetIterator(realm, this, iterable);
        return new Starling.Js.Intrinsics.JsIteratorRecordHandle(syncRecord) { SyncWrapped = true };
    }

    /// <summary>wp:M3-04g — §27.1.4.2.1 AsyncFromSyncIterator next: produce a
    /// promise that resolves to <c>{value: await syncResult.value, done}</c>,
    /// so a sync iterable of promises iterated by <c>for await</c> unwraps each
    /// element.</summary>
    private JsPromise WrapSyncIteratorResult(JsValue syncResult)
    {
        var realm = _runtime.Realm;
        if (!syncResult.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("iterator.next() did not return an object"));
        }

        var done = JsValue.ToBoolean(AbstractOperations.Get(this, syncResult.AsObject, "done"));
        var value = AbstractOperations.Get(this, syncResult.AsObject, "value");

        // Promise.resolve(value).then(v => MakeResult(v, done)).
        var inner = new JsPromise(realm.PromisePrototype);
        PromiseCtor.Resolve(realm, inner, value);
        var onFulfill = new JsNativeFunction("", (thisV, args) =>
        {
            var v = args.Length > 0 ? args[0] : JsValue.Undefined;
            return IteratorIntrinsics.MakeResult(realm, v, done);
        }, isConstructor: false);
        var then = AbstractOperations.Get(this, inner, "then");
        var chained = AbstractOperations.Call(this, then, JsValue.Object(inner),
            new[] { JsValue.Object(onFulfill) });
        // `then` returns a Promise; hand it back for the loop to await.
        return chained.IsObject && chained.AsObject is JsPromise p
            ? p
            : throw new JsThrow(realm.NewTypeError("Promise.prototype.then did not return a promise"));
    }

    // ---- wp:M3-04g — async-generator request queue + driver ----------------

    /// <summary>§27.6.3.1 AsyncGeneratorEnqueue — allocate a request promise,
    /// queue the request, and (if the generator isn't already busy draining)
    /// start processing the queue. Returns the request's promise.</summary>
    internal JsValue AsyncGeneratorEnqueue(JsAsyncGenerator gen, AsyncGeneratorRequestKind kind, JsValue value)
    {
        var realm = _runtime.Realm;
        var cap = new JsPromise(realm.PromisePrototype);
        gen.Queue.Enqueue(new AsyncGeneratorRequest(kind, value, cap));
        AsyncGeneratorDrainQueue(gen);
        return JsValue.Object(cap);
    }

    /// <summary>§27.6.3.4 AsyncGeneratorDrainQueue — if the generator is idle,
    /// resume the body for the head request and ride its yield/await
    /// suspensions until it produces a result (or completes), settling the
    /// head request's promise.</summary>
    private void AsyncGeneratorDrainQueue(JsAsyncGenerator gen)
    {
        if (gen.Draining)
        {
            return;            // a resume is already in flight
        }

        if (gen.Queue.Count == 0)
        {
            return;    // nothing to do
        }

        var realm = _runtime.Realm;
        var req = gen.Queue.Peek();

        // Already-completed generator: short-circuit per §27.6.3.6
        // AsyncGeneratorResumeNext for a Done state.
        if (gen.Done)
        {
            gen.Queue.Dequeue();
            switch (req.Kind)
            {
                case AsyncGeneratorRequestKind.Throw:
                    PromiseCtor.Reject(realm, req.Capability, req.Value);
                    break;
                case AsyncGeneratorRequestKind.Return:
                    PromiseCtor.Resolve(realm, req.Capability,
                        IteratorIntrinsics.MakeResult(realm, req.Value, done: true));
                    break;
                default:
                    PromiseCtor.Resolve(realm, req.Capability,
                        IteratorIntrinsics.MakeResult(realm, JsValue.Undefined, done: true));
                    break;
            }
            // Keep draining any further requests against the done state.
            AsyncGeneratorDrainQueue(gen);
            return;
        }

        // Requests against a not-yet-started generator that aren't `next`
        // complete the generator without ever running the body (§27.6.3.6):
        //   .return(v) → {value:v, done:true}; .throw(e) → reject(e).
        if (!gen.Started && req.Kind != AsyncGeneratorRequestKind.Next)
        {
            gen.Done = true;
            gen.Queue.Dequeue();
            if (req.Kind == AsyncGeneratorRequestKind.Throw)
            {
                PromiseCtor.Reject(realm, req.Capability, req.Value);
            }
            else
            {
                PromiseCtor.Resolve(realm, req.Capability,
                    IteratorIntrinsics.MakeResult(realm, req.Value, done: true));
            }

            AsyncGeneratorDrainQueue(gen);
            return;
        }

        gen.Draining = true;
        gen.Started = true;
        // Resume the body for this request's completion kind.
        switch (req.Kind)
        {
            case AsyncGeneratorRequestKind.Throw:
                gen.Frame.Resume(req.Value, withThrow: true);
                break;
            case AsyncGeneratorRequestKind.Return:
                gen.Frame.Resume(req.Value, withReturn: true);
                break;
            default:
                gen.Frame.Resume(req.Value);
                break;
        }
        AsyncGeneratorAfterResume(gen);
    }

    /// <summary>Common post-resume handling: the body either completed, hit a
    /// <c>yield</c> (deliver a result to the head request) or hit an
    /// <c>await</c> (schedule a continuation that resumes the body once the
    /// awaited promise settles).</summary>
    private void AsyncGeneratorAfterResume(JsAsyncGenerator gen)
    {
        var realm = _runtime.Realm;
        var frame = gen.Frame;

        if (frame.Completed)
        {
            gen.Draining = false;
            gen.Done = true;
            var req = gen.Queue.Dequeue();
            if (frame.ThrewUncaught)
            {
                PromiseCtor.Reject(realm, req.Capability, frame.ReturnValue);
            }
            else
            {
                PromiseCtor.Resolve(realm, req.Capability,
                    IteratorIntrinsics.MakeResult(realm, frame.ReturnValue, done: true));
            }
            // Drive remaining requests against the now-done state.
            AsyncGeneratorDrainQueue(gen);
            return;
        }

        if (frame.SuspendKind == 1)
        {
            // Internal `await` — resume the body once the awaited value
            // settles, then re-enter this handler. Stays Draining; does NOT
            // deliver a result to the head request.
            AsyncGeneratorScheduleAwait(gen);
            return;
        }

        // Real `yield` — fulfil the head request with {value, done:false}.
        gen.Draining = false;
        var yielded = frame.YieldedValue;
        var head = gen.Queue.Dequeue();
        PromiseCtor.Resolve(realm, head.Capability,
            IteratorIntrinsics.MakeResult(realm, yielded, done: false));
        // Service the next queued request (if any).
        AsyncGeneratorDrainQueue(gen);
    }

    /// <summary>Wire the internal <c>await</c>: resolve the awaited value to a
    /// promise and resume the body when it settles (mirrors
    /// <see cref="ScheduleAwait"/> but feeds back into the async-generator
    /// driver).</summary>
    private void AsyncGeneratorScheduleAwait(JsAsyncGenerator gen)
    {
        var realm = _runtime.Realm;
        var awaited = gen.Frame.YieldedValue;
        JsPromise inner;
        if (awaited.IsObject && awaited.AsObject is JsPromise existing)
        {
            inner = existing;
        }
        else
        {
            inner = new JsPromise(realm.PromisePrototype);
            PromiseCtor.Resolve(realm, inner, awaited);
        }

        var onFulfill = new JsNativeFunction("", (thisV, args) =>
        {
            var v = args.Length > 0 ? args[0] : JsValue.Undefined;
            gen.Frame.Resume(v, withThrow: false);
            AsyncGeneratorAfterResume(gen);
            return JsValue.Undefined;
        }, isConstructor: false);
        var onReject = new JsNativeFunction("", (thisV, args) =>
        {
            var r = args.Length > 0 ? args[0] : JsValue.Undefined;
            gen.Frame.Resume(r, withThrow: true);
            AsyncGeneratorAfterResume(gen);
            return JsValue.Undefined;
        }, isConstructor: false);

        var then = AbstractOperations.Get(this, inner, "then");
        AbstractOperations.Call(this, then, JsValue.Object(inner),
            new[] { JsValue.Object(onFulfill), JsValue.Object(onReject) });
    }
}

internal readonly record struct JsStackFrame(string FunctionName, string SourceName, int? Line, int? Column);

/// <summary>Thrown by the VM when a script-level <c>throw</c> is uncaught.</summary>
#pragma warning disable RCS1194
public sealed class JsThrow : Exception
{
    public JsThrow(JsValue value)
        : base($"uncaught: {value}")
    {
        Value = value;
    }

    public JsValue Value { get; }
    internal List<JsStackFrame> StackFrames { get; } = [];
    internal bool GeneratedStackAttached { get; set; }

    internal void AddStackFrame(JsStackFrame frame)
    {
        if (StackFrames.Count > 0 && StackFrames[^1].Equals(frame))
        {
            return;
        }

        StackFrames.Add(frame);
    }
}

/// <summary>Internal sentinel raised inside a generator continuation when
/// the caller invokes <c>.return(v)</c> at a suspension point. Walks any
/// enclosing try/finally frames via the standard exception-handling path
/// (treated as a Return completion), and at the top of the generator body
/// produces a normal completion with <see cref="Value"/> as the return
/// value. Mirrors the synchronous-return path that
/// <c>DivertReturnThroughFinally</c> uses for the <c>Return</c> opcode.</summary>
internal sealed class JsReturnSentinel(JsValue value) : Exception("generator return sentinel")
{
    public JsValue Value { get; } = value;
}
#pragma warning restore RCS1194

/// <summary>Phase of a §14.15 try-frame in the VM dispatch loop.</summary>
internal enum TryPhase
{
    TryBody,
    CatchBody,
    RunningFinally,
}

/// <summary>Pending completion saved on a try-frame while its
/// finalizer runs; replayed by <c>EndFinally</c>.</summary>
internal enum PendingCompletion
{
    None,
    Normal,
    Throw,
    Return,
    /// <summary>wp:M3-15 — a <c>break</c>/<c>continue</c> exiting a loop or
    /// switch across this finalizer. <see cref="TryFrame.PendingTargetPc"/>
    /// holds the loop's break/continue PC and
    /// <see cref="TryFrame.PendingUnwindRemaining"/> the number of further
    /// try-frames to unwind before reaching it.</summary>
    Break,
}

/// <summary>§14.15 try-frame entry used by the VM dispatch loop.</summary>
internal struct TryFrame
{
    public int CatchPc;
    public int FinallyPc;
    public int StackBase;
    public TryPhase Phase;
    public PendingCompletion Pending;
    public JsValue PendingValue;
    /// <summary>wp:M3-15 — for a pending <see cref="PendingCompletion.Break"/>,
    /// the absolute PC of the loop/switch break/continue site to jump to once
    /// every intervening finalizer has run.</summary>
    public int PendingTargetPc;
    /// <summary>wp:M3-15 — for a pending <see cref="PendingCompletion.Break"/>,
    /// the number of additional enclosing try-frames still to unwind (and run
    /// finalizers for) before reaching the target.</summary>
    public int PendingUnwindRemaining;
}

/// <summary>wp:M3-84 Stage A — heap-resident per-call state for one
/// <see cref="JsVm"/> JS activation. One CallFrame is allocated per JS
/// call; everything that used to be a captured local of the dispatch loop lives here so
/// the native frame stays small (deep JS→JS recursion previously tripped the
/// native stack guard at ~26 frames). The hot fields (Ip/Sp/MaxSp plus the
/// array references) are cached in dispatch-loop locals and flushed back
/// before any operation that snapshots the frame (generator/async
/// suspension).</summary>
internal sealed class CallFrame
{
    public required Chunk Chunk;
    public required byte[] Code;
    public required IReadOnlyList<object?> Constants;
    /// <summary>Pooled operand stack. Returned to the pool at every normal
    /// exit; a suspended frame keeps it until the body completes.</summary>
    public required JsValue[] Stack;
    /// <summary>Pooled local-slot storage, rented OVERSIZED (≥
    /// <see cref="Bytecode.Chunk.LocalCount"/>) — never derive logic from its
    /// Length. Returned by <see cref="JsVm.ReleaseLocals"/> at frame pop
    /// unless <see cref="LocalsEscaped"/>; a suspended frame keeps it until
    /// the body completes.</summary>
    public required JsValue[] Locals;
    public required IReadOnlyList<JsValue> Upvalues;
    public required JsValue[] Args;
    /// <summary>§14.15 try-frame stack. Null until the first EnterTry of this
    /// activation (most functions have no try/catch); every reader treats
    /// null as empty.</summary>
    public required Stack<TryFrame>? TryStack;
    /// <summary>True when this frame's <see cref="Locals"/> ARRAY itself was
    /// handed out (a §10.4.4.6 mapped <c>arguments</c> object live-links it; a
    /// direct-eval <see cref="EvalScope"/> references it by (array, slot)) —
    /// the array may be read after the frame pops, so it must NOT go back to
    /// the pool. Survives suspension via
    /// <see cref="ContinuationFrameState.LocalsEscaped"/>.</summary>
    public bool LocalsEscaped;
    public JsFunction? CurrentFunction;
    public JsObject? NewTarget;
    public EvalScope? EvalScope;
    /// <summary>Mutable field (not a property): the DirectEval opcode threads
    /// it by ref so PerformDirectEval can create the store lazily.</summary>
    public EvalVarStore? FrameVarStore;
    public SuspendedFrame? Suspension;
    public List<JsObject>? WithStack;
    public JsValue ThisV;
    public bool FrameStrict;
    public int Ip;
    public int Sp;
    public int MaxSp;
    public int InitDepth;

    // ---- wp:M3-84 Stage B — frame-chain bookkeeping -------------------------

    /// <summary>The frame that pushed this one. For a trampolined JS→JS frame
    /// the pop/unwind paths step back through it; for a barrier frame it is
    /// the frame that was current when the native re-entry began (the chain
    /// stays walkable across barriers).</summary>
    public CallFrame? Caller;

    /// <summary>True for a frame pushed by <see cref="JsVm.RunBarrier"/> —
    /// popping it exits the dispatch loop back to the native caller, and an
    /// unhandled <see cref="JsThrow"/> rethrows natively here.</summary>
    public bool IsBarrier;

    /// <summary>True when this frame owns its <see cref="Args"/> array (a
    /// trampolined call rented it from the arg pool); the pop/unwind paths
    /// return it. Barrier frames never own their args — the native caller
    /// does.</summary>
    public bool ReleaseArgsOnPop;

    /// <summary>How this frame was entered — governs the return coercion at
    /// pop (see <see cref="JsVm.CoerceReturn"/>).</summary>
    public FrameDisposition Disposition;

    /// <summary>Derived-class constructor only: the <c>this</c> bound by
    /// <c>super(...)</c> (the BindThis opcode). Read by the construct return
    /// coercion when the body returns a non-object. Was the VM-wide
    /// <c>_currentDerivedThis</c> side channel before Stage B.</summary>
    public JsValue? DerivedThis;
}

/// <summary>wp:M3-84 Stage B — how a <see cref="CallFrame"/> was entered.
/// Governs the return-value coercion when the frame pops.
/// <see cref="SuperCtor"/> coerces exactly like <see cref="Construct"/> (a
/// super(...) call IS a [[Construct]] of the parent); it exists to keep the
/// frame's provenance visible.</summary>
internal enum FrameDisposition : byte
{
    Call,
    Construct,
    SuperCtor,
}

internal static partial class JsVmLog
{
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "async iterator close: GetMethod('return') threw; abandoning close")]
    public static partial void AsyncIteratorCloseGetMethodFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "async iterator close: invoking 'return' threw; abandoning close")]
    public static partial void AsyncIteratorCloseCallFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug,
        Message = "async iterator close: awaiting 'return' result rejected; original completion wins")]
    public static partial void AsyncIteratorCloseAwaitRejected(ILogger logger, Exception ex);
}
