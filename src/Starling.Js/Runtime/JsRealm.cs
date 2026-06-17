namespace Starling.Js.Runtime;

/// <summary>
/// §9.3 Realm Record. Owns the global object plus every intrinsic prototype /
/// constructor used by the engine. Created once per <see cref="JsRuntime"/>;
/// each intrinsic family populates its slots during install
/// (<c>ObjectCtor.Install(realm)</c>, etc.).
/// </summary>
/// <remarks>
/// The constructor creates the prototype graph and host queues. Intrinsic
/// installers fill in constructors, prototype methods, and namespace objects in
/// dependency order during <see cref="JsRuntime"/> startup.
/// </remarks>
public sealed class JsRealm
{
    public JsObject GlobalObject { get; }

    /// <summary>Host hook for WHATWG Console Standard §2.3 printer output.</summary>
    public ConsoleSink ConsoleSink { get; set; } = DefaultConsoleSink;

    /// <summary>Optional host hook for <c>console.clear()</c>.</summary>
    public Action? ConsoleClear { get; set; }

    /// <summary>HostPromiseRejectionTracker(promise, "reject") — invoked when a
    /// promise is rejected with no reactions attached. The runtime queues the
    /// promise and reports it at the end of the next microtask drain unless
    /// <see cref="OnRejectionHandled"/> retracts it first.</summary>
    public Action<JsPromise>? OnUnhandledRejection { get; set; }

    /// <summary>HostPromiseRejectionTracker(promise, "handle") — invoked when a
    /// reaction is attached to an already-rejected, previously-unhandled
    /// promise (late <c>.catch</c>).</summary>
    public Action<JsPromise>? OnRejectionHandled { get; set; }

    internal Dictionary<string, System.Diagnostics.Stopwatch> ConsoleTimers { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, int> ConsoleCounts { get; } = new(StringComparer.Ordinal);
    internal int ConsoleGroupDepth { get; set; }
    internal Dictionary<string, JsSymbol> SymbolRegistry { get; } = new(StringComparer.Ordinal);

    // §13.2.8.4 — Tagged-template call-site cache. Keyed by the emitted
    // TemplateObjectTemplate's reference identity so every evaluation of one
    // tagged-template site hands back the same frozen strings object.
    internal Dictionary<object, JsObject> TemplateObjectCache { get; } =
        new(System.Collections.Generic.ReferenceEqualityComparer.Instance);

    // §9.3.1 — Intrinsic prototypes. Populated by each intrinsic's Install pass.
    public JsObject ObjectPrototype { get; }
    public JsObject FunctionPrototype { get; }

    // §9.3.2 — Intrinsic constructors. Each lands with its intrinsic install
    // pass (B2-*). Nullable until populated; consumers should null-check or
    // install order their dependencies.
    public JsObject? ObjectConstructor { get; internal set; }
    public JsObject? FunctionConstructor { get; internal set; }

    /// <summary>wp:M3-71 — the realm's <c>%eval%</c> intrinsic (the function
    /// installed as the global <c>eval</c>). The compiler emits a DirectEval
    /// opcode for a bare-<c>eval</c> call that resolves to the global slot; the
    /// VM uses this reference to confirm the callee is still the genuine
    /// intrinsic before applying §19.2.1.1 direct-eval semantics. A reassigned
    /// global <c>eval</c> (or any other callee) is an ordinary indirect call.</summary>
    public JsObject? EvalFunction { get; internal set; }
    public JsObject? ArrayConstructor { get; internal set; }
    public JsObject? StringConstructor { get; internal set; }
    public JsObject? NumberConstructor { get; internal set; }
    public JsObject? BooleanConstructor { get; internal set; }
    public JsObject? SymbolConstructor { get; internal set; }

    // B3-3 — Map/Set/WeakMap/WeakSet constructors. Populated by their
    // Install passes; nullable until then.
    public JsObject? MapConstructor { get; internal set; }
    public JsObject? SetConstructor { get; internal set; }
    public JsObject? WeakMapConstructor { get; internal set; }
    public JsObject? WeakSetConstructor { get; internal set; }

    // B4-6 — WeakRef / FinalizationRegistry constructors.
    public JsObject? WeakRefConstructor { get; internal set; }
    public JsObject? FinalizationRegistryConstructor { get; internal set; }

    /// <summary>§26.1 "kept alive" set — every successful
    /// <c>WeakRef.prototype.deref()</c> adds its target here so it survives
    /// for the rest of the current job. <see cref="JsRuntime.DrainMicrotasks"/>
    /// clears the set after each drain (a coarser cadence than the per-job
    /// clearing required by spec — unobservable in practice because JS can't
    /// force a GC between two <c>deref</c> calls inside the same job).</summary>
    public HashSet<JsObject> KeptAlive { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>Weak handles to every live <see cref="JsFinalizationRegistry"/>
    /// allocated against this realm. Walked at the end of every microtask
    /// drain to discover collected targets and schedule their cleanup
    /// callbacks. Weakly held so dropped registries can be reclaimed.</summary>
    public List<WeakReference<JsFinalizationRegistry>> FinalizationRegistries { get; } = new();

    // B3-4: Promise constructor + the host-agnostic microtask queue used by
    // its reaction jobs. The MicrotaskQueue is allocated unconditionally so
    // Promise install can schedule reactions even before a host scheduler
    // hooks in.
    public JsObject? PromiseConstructor { get; internal set; }

    // B4-1: RegExp constructor — populated by RegExpCtor.Install.
    public JsObject? RegExpConstructor { get; internal set; }

    // The built-in RegExp.prototype.exec and the global/unicode flag getters,
    // captured at install time. The @@match global fast path checks identity
    // against these to be sure the §22.2.7.1 RegExpExec delegation contract is
    // unobserved (no user-overridden exec / flag getters) before short-circuiting.
    public JsObject? RegExpBuiltinExec { get; internal set; }
    public JsObject? RegExpGlobalGetter { get; internal set; }
    public JsObject? RegExpUnicodeGetter { get; internal set; }
    /// <summary>The built-in <c>RegExp.prototype[@@replace]</c>. String.replace
    /// fast-paths to the engine's replace directly when a regex argument's
    /// @@replace still resolves to this (i.e. is not user-overridden).</summary>
    public JsObject? RegExpBuiltinSymbolReplace { get; internal set; }

    /// <summary>Protector cache for the @@replace fast-path guard. The structural
    /// <see cref="JsObject.ProtoEpoch"/> at which exec/global/unicode were last
    /// confirmed to be the builtins, and that result. <c>global</c>/<c>unicode</c>
    /// are accessors mutated only via defineProperty/delete (both bump the epoch),
    /// so while the epoch matches only a plain <c>exec</c> value reassignment can
    /// have slipped through — the guard re-probes that single property.</summary>
    internal int RegExpGuardCachedEpoch = int.MinValue;
    internal bool RegExpGuardCachedIntact;

    // B4-2: Date constructor — populated by DateCtor.Install.
    public JsObject? DateConstructor { get; internal set; }

    // B4-3: BigInt constructor — populated by BigIntCtor.Install.
    public JsObject? BigIntConstructor { get; internal set; }

    // B4-4: Proxy constructor — populated by ProxyCtor.Install. The prototype
    // slot (<see cref="ProxyPrototype"/>) is bootstrapped in this realm's
    // constructor; ProxyCtor only owns the callable + Proxy.revocable.
    public JsObject? ProxyConstructor { get; internal set; }

    // B4-4: Reflect namespace object — populated by ReflectObj.Install.
    public JsObject? ReflectObject { get; internal set; }

    /// <summary>HTML §8.1.5.1 microtask queue. Owns the in-process FIFO used
    /// by Promise reactions; the host may swap in a real loop scheduler via
    /// <see cref="JsRuntime.SetMicrotaskScheduler"/>.</summary>
    public MicrotaskQueue Microtasks { get; } = new();
    public JsObject ArrayPrototype { get; internal set; }
    public JsObject StringPrototype { get; internal set; }
    public JsObject NumberPrototype { get; internal set; }
    public JsObject BooleanPrototype { get; internal set; }
    public JsObject BigIntPrototype { get; internal set; }
    public JsObject SymbolPrototype { get; internal set; }
    public JsObject ErrorPrototype { get; internal set; }
    public JsObject TypeErrorPrototype { get; internal set; }
    public JsObject RangeErrorPrototype { get; internal set; }
    public JsObject ReferenceErrorPrototype { get; internal set; }
    public JsObject SyntaxErrorPrototype { get; internal set; }
    public JsObject UriErrorPrototype { get; internal set; }
    public JsObject EvalErrorPrototype { get; internal set; }
    public JsObject AggregateErrorPrototype { get; internal set; }
    public JsObject IteratorPrototype { get; internal set; }
    public JsObject ArrayIteratorPrototype { get; internal set; }
    public JsObject StringIteratorPrototype { get; internal set; }
    public JsObject MapPrototype { get; internal set; }
    public JsObject MapIteratorPrototype { get; internal set; }
    public JsObject SetPrototype { get; internal set; }
    public JsObject SetIteratorPrototype { get; internal set; }
    public JsObject WeakMapPrototype { get; internal set; }
    public JsObject WeakSetPrototype { get; internal set; }
    public JsObject WeakRefPrototype { get; internal set; }
    public JsObject FinalizationRegistryPrototype { get; internal set; }
    public JsObject PromisePrototype { get; internal set; }
    public JsObject RegExpPrototype { get; internal set; }
    public JsObject DatePrototype { get; internal set; }
    public JsObject ArrayBufferPrototype { get; internal set; }
    public JsObject DataViewPrototype { get; internal set; }
    public JsObject TypedArrayPrototype { get; internal set; }
    public JsObject ProxyPrototype { get; internal set; }
    public JsObject GeneratorPrototype { get; internal set; }
    public JsObject AsyncFunctionPrototype { get; internal set; }
    public JsObject AsyncGeneratorPrototype { get; internal set; }
    public JsObject AsyncIteratorPrototype { get; internal set; }

    // B5-1 — DOM/Web bindings. Populated by Starling.Bindings.WindowBinding.Install.
    // Chain: EventTargetPrototype → Object.prototype; NodePrototype → EventTargetPrototype;
    // ElementPrototype → NodePrototype; DocumentPrototype → NodePrototype;
    // WindowPrototype → EventTargetPrototype. Nullable because the bindings live
    // outside Starling.Js and may not be installed in every embedding.
    public JsObject? EventTargetPrototype { get; set; }
    public JsObject? NodePrototype { get; set; }
    public JsObject? CharacterDataPrototype { get; set; }
    public JsObject? TextPrototype { get; set; }
    public JsObject? CommentPrototype { get; set; }
    public JsObject? ProcessingInstructionPrototype { get; set; }
    public JsObject? DocumentFragmentPrototype { get; set; }
    public JsObject? DocumentTypePrototype { get; set; }
    public JsObject? ElementPrototype { get; set; }
    public JsObject? DocumentPrototype { get; set; }
    public JsObject? XmlDocumentPrototype { get; set; }
    public JsObject? EventPrototype { get; set; }
    public JsObject? CustomEventPrototype { get; set; }
    public JsObject? UiEventPrototype { get; set; }
    public JsObject? MouseEventPrototype { get; set; }
    public JsObject? KeyboardEventPrototype { get; set; }
    public JsObject? FocusEventPrototype { get; set; }
    public JsObject? AnimationEventPrototype { get; set; }
    public JsObject? TransitionEventPrototype { get; set; }
    public JsObject? DomExceptionPrototype { get; set; }
    public JsObject? WindowPrototype { get; set; }
    public JsObject? EventTargetConstructor { get; set; }
    public JsObject? EventConstructor { get; set; }
    public JsObject? NodeConstructor { get; set; }
    public JsObject? TextConstructor { get; set; }
    public JsObject? CommentConstructor { get; set; }
    public JsObject? DocumentFragmentConstructor { get; set; }
    public JsObject? DocumentTypeConstructor { get; set; }
    public JsObject? ElementConstructor { get; set; }
    public JsObject? DocumentConstructor { get; set; }
    // DOM §4.9 — Attr + NamedNodeMap (WPT-05).
    public JsObject? AttrPrototype { get; set; }
    public JsObject? AttrConstructor { get; set; }
    public JsObject? NamedNodeMapPrototype { get; set; }
    public JsObject? NamedNodeMapConstructor { get; set; }

    // B5-3 — Fetch / XHR web APIs. Populated by Starling.Bindings.FetchBinding +
    // XhrBinding. Constructor + prototype slots live together for the suite.
    public JsObject? HeadersPrototype { get; set; }
    public JsObject? HeadersConstructor { get; set; }
    public JsObject? RequestPrototype { get; set; }
    public JsObject? RequestConstructor { get; set; }
    public JsObject? ResponsePrototype { get; set; }
    public JsObject? ResponseConstructor { get; set; }
    public JsObject? AbortControllerPrototype { get; set; }
    public JsObject? AbortControllerConstructor { get; set; }
    public JsObject? AbortSignalPrototype { get; set; }
    public JsObject? AbortSignalConstructor { get; set; }
    public JsObject? XmlHttpRequestPrototype { get; set; }
    public JsObject? XmlHttpRequestConstructor { get; set; }

    // B5-4 — Observers (MutationObserver / IntersectionObserver / ResizeObserver
    // + MutationRecord / IntersectionObserverEntry / ResizeObserverEntry).
    // Populated by Starling.Bindings.Observers.* installs.
    public JsObject? MutationObserverPrototype { get; set; }
    public JsObject? MutationObserverConstructor { get; set; }
    public JsObject? MutationRecordPrototype { get; set; }
    public JsObject? IntersectionObserverPrototype { get; set; }
    public JsObject? IntersectionObserverConstructor { get; set; }
    public JsObject? IntersectionObserverEntryPrototype { get; set; }
    public JsObject? ResizeObserverPrototype { get; set; }
    public JsObject? ResizeObserverConstructor { get; set; }
    public JsObject? ResizeObserverEntryPrototype { get; set; }

    /// <summary>B1b-2a — singleton sentinel used as the <c>this</c> binding
    /// of a derived-class constructor frame before <c>super(...)</c> has
    /// run. Any <c>LoadThisChecked</c> dispatch that observes this value
    /// throws ReferenceError per ES2024 §10.2.1.1.</summary>
    public JsObject UninitializedThisSentinel { get; } = new();

    /// <summary>Temporal Dead Zone (TDZ) — singleton sentinel stored in a
    /// <c>let</c>/<c>const</c>/<c>class</c> lexical slot (or its backing
    /// <see cref="Cell"/>) between scope entry and the declaration's
    /// initializer running. Any read or write of the binding that observes
    /// this value throws a ReferenceError per ECMA-262 §§9.1.1.1.4 /
    /// 13.3.1.1: a lexical binding is instantiated uninitialized and must
    /// not be accessed before its initializer assigns it (or, for a bare
    /// <c>let x;</c>, before the declaration sets it to undefined).</summary>
    public JsObject TdzSentinel { get; } = new();

    /// <summary>§10.2.4.1 %ThrowTypeError% — the shared, frozen poison-pill
    /// function whose [[Call]] always throws a TypeError. Used as the
    /// <c>callee</c> getter/setter on a strict-mode <c>arguments</c> object.
    /// Created lazily once per realm.</summary>
    private JsNativeFunction? _throwTypeError;
    public JsNativeFunction ThrowTypeErrorIntrinsic =>
        _throwTypeError ??= new JsNativeFunction(this, "", length: 0,
            (_, _) => throw new JsThrow(NewTypeError(
                "'callee' may not be accessed on a strict mode arguments object")),
            isConstructor: false);

    /// <summary>The VM currently executing against this realm, if any. Set
    /// by <see cref="JsVm"/> on entry to <c>Run</c>. Native intrinsics that
    /// need to call back into JS (e.g. <c>JSON.parse</c>'s reviver,
    /// <c>JSON.stringify</c>'s replacer / <c>toJSON</c>) read this so they
    /// can pass a VM to <see cref="AbstractOperations.Call"/> for
    /// <see cref="JsFunction"/> dispatch.</summary>
    public JsVm? ActiveVm { get; internal set; }

    /// <summary>wp:M3-83 — the <see cref="JsRuntime"/> that owns this realm. Set
    /// once by <see cref="JsRuntime"/> at construction. Lets host code that
    /// re-enters JS against a realm with no running execution context (the
    /// classic case: a <em>foreign</em> realm from <c>$262.createRealm()</c>
    /// whose <c>global.eval</c> / functions are invoked while the host realm's
    /// VM is the one on the native stack) recover the realm's own primary VM and
    /// publish it as <see cref="ActiveVm"/> for the duration of that re-entry —
    /// so per-realm context-dependent ops (eval, global resolution) run against
    /// the correct realm. Null only for a bare <see cref="JsRealm"/> created
    /// outside a runtime (unit-test scaffolding); such realms still throw the
    /// "active execution context" TypeError, matching prior behavior.</summary>
    public JsRuntime? OwnerRuntime { get; internal set; }

    /// <summary>wp:M3-68 — §6.2.5.5: reading a bare free identifier that resolves
    /// to no binding throws a ReferenceError. Default <c>true</c> (spec-correct;
    /// what Test262 expects). An embedder can set this <c>false</c> so unresolved
    /// global reads yield <c>undefined</c> instead — used by the browser page
    /// realm to gracefully degrade host globals it hasn't implemented yet rather
    /// than crash a page. See also <see cref="LenientGlobalNames"/> for a
    /// per-name opt-out while the flag stays on.</summary>
    public bool ThrowOnUnresolvedGlobalRead { get; set; } = true;

    /// <summary>wp:M3-68 — names that stay lenient (read → <c>undefined</c>)
    /// even when <see cref="ThrowOnUnresolvedGlobalRead"/> is <c>true</c>: an
    /// allowlist of known host globals an embedder intends to provide. Empty by
    /// default (so Test262 / the default realm is fully strict).</summary>
    public HashSet<string> LenientGlobalNames { get; } = new(StringComparer.Ordinal);

    /// <summary>wp:M3-03c — the active module loader for this realm, if module
    /// evaluation is in flight. Set by <see cref="Modules.ModuleLoader"/> on
    /// construction so the VM can service dynamic <c>import()</c> and
    /// <c>import.meta</c> opcodes (which need specifier resolution + the module
    /// registry). Null in a pure classic-script realm with no loader; the VM's
    /// <c>DynamicImport</c> handler throws a TypeError in that case.</summary>
    public Modules.ModuleLoader? ModuleLoader { get; internal set; }

    public JsRealm()
    {
        // Bootstrap order matters: Object.prototype is the root; everything
        // else inherits from it. Function.prototype inherits from
        // Object.prototype and is itself callable (the empty function) — for
        // bare-bones bootstrap it's just an object.
        ObjectPrototype = new JsObject();
        FunctionPrototype = new JsObject(ObjectPrototype);

        // All other prototypes default to Object.prototype-inheriting empties.
        // Intrinsic install passes replace these with fully-populated objects.
        ArrayPrototype = new JsObject(ObjectPrototype);
        StringPrototype = new JsObject(ObjectPrototype);
        NumberPrototype = new JsObject(ObjectPrototype);
        BooleanPrototype = new JsObject(ObjectPrototype);
        BigIntPrototype = new JsObject(ObjectPrototype);
        SymbolPrototype = new JsObject(ObjectPrototype);
        ErrorPrototype = new JsObject(ObjectPrototype);
        TypeErrorPrototype = new JsObject(ErrorPrototype);
        RangeErrorPrototype = new JsObject(ErrorPrototype);
        ReferenceErrorPrototype = new JsObject(ErrorPrototype);
        SyntaxErrorPrototype = new JsObject(ErrorPrototype);
        UriErrorPrototype = new JsObject(ErrorPrototype);
        EvalErrorPrototype = new JsObject(ErrorPrototype);
        AggregateErrorPrototype = new JsObject(ErrorPrototype);
        IteratorPrototype = new JsObject(ObjectPrototype);
        ArrayIteratorPrototype = new JsObject(IteratorPrototype);
        StringIteratorPrototype = new JsObject(IteratorPrototype);
        MapPrototype = new JsObject(ObjectPrototype);
        MapIteratorPrototype = new JsObject(IteratorPrototype);
        SetPrototype = new JsObject(ObjectPrototype);
        SetIteratorPrototype = new JsObject(IteratorPrototype);
        WeakMapPrototype = new JsObject(ObjectPrototype);
        WeakSetPrototype = new JsObject(ObjectPrototype);
        WeakRefPrototype = new JsObject(ObjectPrototype);
        FinalizationRegistryPrototype = new JsObject(ObjectPrototype);
        PromisePrototype = new JsObject(ObjectPrototype);
        RegExpPrototype = new JsObject(ObjectPrototype);
        DatePrototype = new JsObject(ObjectPrototype);
        ArrayBufferPrototype = new JsObject(ObjectPrototype);
        DataViewPrototype = new JsObject(ObjectPrototype);
        TypedArrayPrototype = new JsObject(ObjectPrototype);
        ProxyPrototype = new JsObject(ObjectPrototype);
        GeneratorPrototype = new JsObject(IteratorPrototype);
        AsyncFunctionPrototype = new JsObject(FunctionPrototype);
        AsyncIteratorPrototype = new JsObject(ObjectPrototype);
        AsyncGeneratorPrototype = new JsObject(AsyncIteratorPrototype);

        GlobalObject = new JsGlobalObject(ObjectPrototype);
    }

    /// <summary>Allocate a fresh ordinary object inheriting from
    /// <see cref="ObjectPrototype"/>.</summary>
    public JsObject NewOrdinaryObject() => new JsObject(ObjectPrototype);

    /// <summary>Allocate a fresh ordinary object inheriting from <paramref name="proto"/>.</summary>
    public JsObject NewObjectWithProto(JsObject? proto) => new JsObject(proto);

    /// <summary>§10.4.4.6 CreateUnmappedArgumentsObject. Build the
    /// <c>arguments</c> object the running (non-arrow) function sees: an
    /// ordinary object inheriting from <see cref="ObjectPrototype"/>, carrying
    /// the supplied <paramref name="args"/> as configurable, writable,
    /// enumerable indexed data properties; a non-enumerable <c>length</c> data
    /// property; and <c>@@iterator</c> aliased to
    /// <c>Array.prototype[@@iterator]</c> so spreading / <c>for…of</c> /
    /// destructuring over <c>arguments</c> works. Starling builds the unmapped
    /// form (no parameter aliasing) — sufficient for sloppy-mode legacy code
    /// that reads <c>arguments.length</c> / <c>arguments[i]</c> and does
    /// <c>Array.prototype.slice.call(arguments)</c>.</summary>
    public JsObject CreateArgumentsObject(IReadOnlyList<JsValue> args)
        => CreateArgumentsObject(args, strict: false);

    /// <summary>§10.4.4.6 — as above, but when <paramref name="strict"/> install
    /// the §10.4.4.6 step-7 <c>callee</c> poison-pill accessor (a non-enumerable,
    /// non-configurable accessor whose get/set is %ThrowTypeError%).</summary>
    public JsObject CreateArgumentsObject(IReadOnlyList<JsValue> args, bool strict)
    {
        var obj = new JsObject(ObjectPrototype) { IsArgumentsExotic = true };
        for (var i = 0; i < args.Count; i++)
        {
            obj.DefineOwnProperty(
                i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PropertyDescriptor.Data(args[i], writable: true, enumerable: true, configurable: true));
        }

        obj.DefineOwnProperty("length",
            PropertyDescriptor.Data(JsValue.Number(args.Count), writable: true, enumerable: false, configurable: true));
        // @@iterator = %Array.prototype.values% so `[...arguments]` / `for…of`
        // work. Resolve it off Array.prototype if the Array intrinsic is wired.
        var arrayIter = ArrayPrototype.GetOwnPropertyDescriptor(Intrinsics.SymbolCtor.Iterator);
        if (arrayIter is { } iterDesc && iterDesc.Value.IsObject)
        {
            obj.DefineOwnProperty(Intrinsics.SymbolCtor.Iterator,
                PropertyDescriptor.BuiltinMethod(iterDesc.Value));
        }
        // §10.4.4.6 step 7 — strict-mode arguments objects expose a
        // non-configurable `callee` accessor that throws on get or set.
        if (strict)
        {
            var poison = JsValue.Object(ThrowTypeErrorIntrinsic);
            obj.DefineOwnProperty("callee",
                PropertyDescriptor.Accessor(poison.AsObject, poison.AsObject,
                    enumerable: false, configurable: false));
        }
        return obj;
    }

    /// <summary>§10.4.4.6 CreateMappedArgumentsObject — the mapped form for a
    /// non-strict, simple-parameter-list function. Indices <c>0..argc-1</c> are
    /// ordinary writable/enumerable/configurable data properties; those whose
    /// index <paramref name="slotForIndex"/> entry is non-negative are
    /// additionally live-linked to that parameter's local slot via
    /// <see cref="JsMappedArguments"/>. <paramref name="callee"/> is the executing
    /// function, exposed as a writable/non-enumerable/configurable <c>callee</c>
    /// (NOT the strict-mode poison pill). The <paramref name="locals"/> array is
    /// held by reference so the parameter link survives the frame's return.</summary>
    public JsObject CreateMappedArgumentsObject(
        IReadOnlyList<JsValue> args, JsValue[] locals, int[] slotForIndex, JsObject? callee)
    {
        var obj = new JsMappedArguments(ObjectPrototype, locals, slotForIndex);
        for (var i = 0; i < args.Count; i++)
        {
            obj.DefineOwnProperty(
                i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                PropertyDescriptor.Data(args[i], writable: true, enumerable: true, configurable: true));
        }

        obj.DefineOwnProperty("length",
            PropertyDescriptor.Data(JsValue.Number(args.Count), writable: true, enumerable: false, configurable: true));
        var arrayIter = ArrayPrototype.GetOwnPropertyDescriptor(Intrinsics.SymbolCtor.Iterator);
        if (arrayIter is { } iterDesc && iterDesc.Value.IsObject)
        {
            obj.DefineOwnProperty(Intrinsics.SymbolCtor.Iterator,
                PropertyDescriptor.BuiltinMethod(iterDesc.Value));
        }
        // §10.4.4.6 step 7.b — non-strict mapped arguments expose `callee` as the
        // function object itself (writable, non-enumerable, configurable).
        obj.DefineOwnProperty("callee",
            PropertyDescriptor.Data(
                callee is null ? JsValue.Undefined : JsValue.Object(callee),
                writable: true, enumerable: false, configurable: true));
        return obj;
    }

    /// <summary>Construct a generic Error-like object with a <c>message</c>
    /// data slot. Used by abstract operations to surface engine-level errors
    /// before the full Error intrinsic lands.</summary>
    public JsValue NewError(JsObject errorPrototype, string message)
    {
        var err = new JsObject(errorPrototype);
        err.DefineOwnProperty("message",
            PropertyDescriptor.Data(JsValue.String(message), writable: true, enumerable: false, configurable: true));
        return JsValue.Object(err);
    }

    public JsValue NewTypeError(string message) => NewError(TypeErrorPrototype, message);
    public JsValue NewRangeError(string message) => NewError(RangeErrorPrototype, message);
    public JsValue NewReferenceError(string message) => NewError(ReferenceErrorPrototype, message);
    public JsValue NewSyntaxError(string message) => NewError(SyntaxErrorPrototype, message);
    public JsValue NewUriError(string message) => NewError(UriErrorPrototype, message);
    public JsValue NewEvalError(string message) => NewError(EvalErrorPrototype, message);
    public JsValue NewAggregateError(string message) => NewError(AggregateErrorPrototype, message);

    /// <summary>Construct an Error-like object that also carries a <c>cause</c>
    /// own slot per §20.5.1.1. Used by host-side throws that want to surface a
    /// wrapped exception or other JS value.</summary>
    public JsValue NewError(JsObject errorPrototype, string message, JsValue cause)
    {
        var err = new JsObject(errorPrototype);
        err.DefineOwnProperty("message",
            PropertyDescriptor.Data(JsValue.String(message), writable: true, enumerable: false, configurable: true));
        err.DefineOwnProperty("cause",
            PropertyDescriptor.Data(cause, writable: true, enumerable: false, configurable: true));
        return JsValue.Object(err);
    }

    public JsValue NewTypeError(string message, JsValue cause) => NewError(TypeErrorPrototype, message, cause);
    public JsValue NewRangeError(string message, JsValue cause) => NewError(RangeErrorPrototype, message, cause);
    public JsValue NewReferenceError(string message, JsValue cause) => NewError(ReferenceErrorPrototype, message, cause);
    public JsValue NewSyntaxError(string message, JsValue cause) => NewError(SyntaxErrorPrototype, message, cause);
    public JsValue NewUriError(string message, JsValue cause) => NewError(UriErrorPrototype, message, cause);
    public JsValue NewEvalError(string message, JsValue cause) => NewError(EvalErrorPrototype, message, cause);

    /// <summary>§7.1.18 boxing for primitives. Placeholder: returns a wrapper
    /// object whose [[Prototype]] is the matching <c>*Prototype</c> and which
    /// stores the primitive in an internal slot under <c>__primitiveValue</c>
    /// until the typed wrapper subclasses land.</summary>
    internal JsObject BoxBoolean(JsValue v) => BoxPrimitive(BooleanPrototype, v);
    internal JsObject BoxNumber(JsValue v) => BoxPrimitive(NumberPrototype, v);
    internal JsObject BoxString(JsValue v)
    {
        if (v.IsString)
        {
            return new JsStringObject(StringPrototype, v.AsString);
        }

        return BoxPrimitive(StringPrototype, v);
    }
    internal JsObject BoxBigInt(JsValue v) => BoxPrimitive(BigIntPrototype, v);
    internal JsObject BoxSymbol(JsValue v) => BoxPrimitive(SymbolPrototype, v);

    private static JsObject BoxPrimitive(JsObject proto, JsValue value)
    {
        var box = new JsObject(proto);
        box.DefineOwnProperty("__primitiveValue",
            PropertyDescriptor.Data(value, writable: false, enumerable: false, configurable: false));
        return box;
    }

    private static void DefaultConsoleSink(ConsoleLevel level, string message)
    {
        var writer = level is ConsoleLevel.Warn or ConsoleLevel.Error ? Console.Error : Console.Out;
        writer.WriteLine(message);
    }
}
