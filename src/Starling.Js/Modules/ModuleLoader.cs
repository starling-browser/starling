using Starling.Js.Bytecode;
using Starling.Js.Intrinsics;
using Starling.Js.Parse;
using Starling.Js.Runtime;

namespace Starling.Js.Modules;

/// <summary>
/// ES2024 §16.2.1 module loader: resolves specifiers, fetches + compiles source
/// text, links the dependency graph depth-first (with cycle handling), and
/// evaluates modules in dependency order.
/// </summary>
/// <remarks>
/// <para>
/// The loader is host-agnostic: a <see cref="IModuleHost"/> supplies specifier
/// resolution and source fetching, so the engine can plug in its file:// + http
/// fetch mechanism while tests use an in-memory map.
/// </para>
/// <para>
/// Live bindings are realised by sharing <see cref="Cell"/>s between modules
/// (see <see cref="ModuleRecord"/>). Linking allocates one cell per local
/// binding of each module; an imported name reuses the exporting module's cell.
/// </para>
/// <para>
/// <b>Top-level await.</b> A module whose body uses top-level
/// <c>await</c> (or that imports such a module) evaluates asynchronously: its
/// body is an <see cref="JsFunctionKind.Async"/> function that suspends on each
/// <c>await</c> and returns a Promise. <see cref="Evaluate"/> waits for a
/// module's async dependencies before running its body and propagates rejections
/// as <see cref="ModuleRecord.EvaluationError"/>. <see cref="LoadAndEvaluate"/>
/// drives <see cref="MicrotaskQueue.DrainAll"/> to quiescence so the root settles
/// synchronously from the host's perspective. Non-TLA graphs keep the
/// synchronous fast path unchanged. <see cref="EvaluateToPromise"/> is the
/// reusable "evaluate to a Promise" entry that dynamic <c>import()</c>
/// builds on. <c>import.meta</c> is populated by the loader on demand.
/// </para>
/// </remarks>
public sealed class ModuleLoader
{
    private readonly JsRuntime _runtime;
    private readonly IModuleHost _host;
    private readonly Dictionary<string, ModuleRecord> _registry = new(StringComparer.Ordinal);

    public ModuleLoader(JsRuntime runtime, IModuleHost host)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        // wp:M3-03c — publish ourselves on the realm so the VM's DynamicImport /
        // LoadImportMeta opcodes can reach the loader (specifier resolution +
        // module registry) without threading a reference through every frame.
        _runtime.Realm.ModuleLoader = this;
    }

    /// <summary>Resolve, load, link and evaluate the module at
    /// <paramref name="specifier"/> (resolved relative to
    /// <paramref name="referrer"/>). Returns the evaluated record. Re-entrant:
    /// an already-evaluated module is returned from the registry without
    /// re-running.</summary>
    public ModuleRecord LoadAndEvaluate(string specifier, string? referrer = null)
    {
        var record = LoadGraph(specifier, referrer);
        Link(record);

        // wp:M3-03b — kick off evaluation (returns a Promise for a TLA graph,
        // null for a fully synchronous graph), then drain the microtask queue to
        // quiescence so an async root settles synchronously from the host's
        // point of view. A synchronous graph completes inside Evaluate and the
        // drain is a near no-op (it still runs any reactions the body queued).
        EvaluateToPromise(record);
        _runtime.Realm.Microtasks.DrainAll();

        // Surface a top-level-await rejection / synchronous throw as the module
        // graph's evaluation error (matching the synchronous contract).
        if (record.EvaluationError is not null)
        {
            throw record.EvaluationError;
        }

        return record;
    }

    /// <summary>wp:M3-03b — reusable "evaluate this module to a Promise" entry.
    /// Links must have completed. Returns a <see cref="JsPromise"/> that settles
    /// when the module's whole subtree (dependencies + own body) has finished
    /// evaluating; rejection carries the evaluation error. Dynamic <c>import()</c>
    /// (wp:M3-03c) calls this to obtain the promise it resolves to the module's
    /// namespace. The returned promise is never null — a synchronous graph yields
    /// an already-settled promise.</summary>
    internal JsPromise EvaluateToPromise(ModuleRecord record)
    {
        var realm = _runtime.Realm;
        var promise = Evaluate(record);
        if (promise is not null)
        {
            return promise;
        }

        // Fully synchronous subtree: produce an already-settled promise so
        // callers (import()) get a uniform shape.
        var settled = new JsPromise(realm.PromisePrototype);
        if (record.EvaluationError is not null)
        {
            PromiseCtor.Reject(realm, settled, record.EvaluationError.Value);
        }
        else
        {
            PromiseCtor.Resolve(realm, settled, JsValue.Undefined);
        }

        return settled;
    }

    // -----------------------------------------------------------------------
    // wp:M3-03c — dynamic import() + import.meta
    // -----------------------------------------------------------------------

    /// <summary>wp:M3-03c — §13.3.10.2 HostLoadImportedModule + §16.2.1.8
    /// ContinueDynamicImport. Resolve <paramref name="specifierValue"/> (string-
    /// coerced) relative to <paramref name="referrer"/>, load + link the graph,
    /// evaluate it (waiting
    /// for any top-level await to settle), and return a <see cref="JsPromise"/>
    /// that fulfils with the module's namespace object or rejects with the first
    /// resolve/fetch/link/evaluation error. Never throws synchronously — every
    /// failure path becomes a rejected promise, matching the spec's
    /// "import() always returns a promise" contract.</summary>
    internal JsPromise ImportDynamic(JsValue specifierValue, string? referrer)
    {
        var realm = _runtime.Realm;
        var result = new JsPromise(realm.PromisePrototype);

        ModuleRecord record;
        JsPromise evalPromise;
        try
        {
            // §13.3.10.1 step 5: ToString(specifier). A throw here (e.g. a
            // Symbol specifier) becomes a rejection, not a synchronous throw.
            var specifier = JsValue.ToStringValue(
                specifierValue.IsObject
                    ? AbstractOperations.ToPrimitive(realm.ActiveVm, specifierValue, "string")
                    : specifierValue);
            record = LoadGraph(specifier, referrer);
            Link(record);
            evalPromise = EvaluateToPromise(record);
        }
        catch (JsThrow ex)
        {
            // Synchronous resolve/fetch/link/sync-eval failure → rejected promise.
            PromiseCtor.Reject(realm, result, ex.Value);
            return result;
        }

        // Chain on the evaluation promise: fulfil with the namespace once the
        // subtree (incl. any top-level await) settles; reject with its error.
        var vm = realm.ActiveVm ?? new JsVm(_runtime);
        var onFulfill = new JsNativeFunction("", (_, _) =>
        {
            try
            {
                PromiseCtor.Resolve(realm, result, JsValue.Object(GetOrBuildNamespace(record)));
            }
            catch (JsThrow ex)
            {
                PromiseCtor.Reject(realm, result, ex.Value);
            }
            return JsValue.Undefined;
        }, isConstructor: false);
        var onReject = new JsNativeFunction("", (_, args) =>
        {
            PromiseCtor.Reject(realm, result, args.Length > 0 ? args[0] : JsValue.Undefined);
            return JsValue.Undefined;
        }, isConstructor: false);
        var then = AbstractOperations.Get(vm, evalPromise, "then");
        AbstractOperations.Call(vm, then, JsValue.Object(evalPromise),
            new[] { JsValue.Object(onFulfill), JsValue.Object(onReject) });
        return result;
    }

    /// <summary>wp:M3-03c — return the running module's <c>import.meta</c> object
    /// given its resolved URL (the chunk name the VM carries). Looks the record
    /// up in the registry; returns null when no module is registered under that
    /// URL (i.e. the code is a classic script, where <c>import.meta</c> is a
    /// SyntaxError — the VM surfaces that).</summary>
    internal JsObject? ResolveMetaForUrl(string? url)
    {
        if (url is null)
        {
            return null;
        }

        return _registry.TryGetValue(url, out var record) ? GetOrBuildMeta(record) : null;
    }

    /// <summary>Lazily build (and cache) a module's <c>import.meta</c> object.
    /// §16.2.1.9 HostGetImportMetaProperties supplies at least <c>url</c> (the
    /// module's resolved URL). The object inherits from <c>Object.prototype</c>
    /// per the spec's note that import.meta is an ordinary extensible object.</summary>
    private JsObject GetOrBuildMeta(ModuleRecord record)
    {
        if (record.Meta is not null)
        {
            return record.Meta;
        }

        var meta = _runtime.Realm.NewOrdinaryObject();
        meta.Set("url", JsValue.String(record.Url));
        record.Meta = meta;
        return meta;
    }

    // -----------------------------------------------------------------------
    // Phase 1 — load + parse + compile the whole graph (§16.2.1.5.1 step graph)
    // -----------------------------------------------------------------------

    private ModuleRecord LoadGraph(string specifier, string? referrer)
    {
        var url = ResolveOrThrow(specifier, referrer);
        if (_registry.TryGetValue(url, out var existing))
        {
            return existing;
        }

        var source = _host.FetchSource(url)
            ?? throw new JsThrow(_runtime.Realm.NewError(
                _runtime.Realm.TypeErrorPrototype, $"Failed to fetch module: {url}"));

        // §16.2.1.6 — parse under the Module goal: module code is strict and an
        // implicit [+Await] context at top level, and the Module-specific early
        // errors (duplicate exports, escaped reserved words in import/export,
        // unresolvable exports, top-level new.target/super, …) run here.
        var program = new JsParser(source).ParseModule();
        var compiled = JsCompiler.CompileModule(program, url);

        var record = new ModuleRecord(
            url, compiled.Chunk, compiled.Imports, compiled.Exports, compiled.RequestedModules)
        {
            HasTopLevelAwait = compiled.HasTopLevelAwait,
        };
        // Stash the binding order on the record via a side table so Link can
        // wire upvalue cells in slot order.
        _bindingOrders[record] = compiled.BindingOrder;
        _registry[url] = record;

        // Recurse into dependencies (depth-first) so the whole graph is present
        // before linking. Cycles terminate via the registry check above.
        foreach (var dep in record.RequestedModules)
        {
            LoadGraph(dep, url);
        }

        return record;
    }

    private readonly Dictionary<ModuleRecord, IReadOnlyList<ModuleBindingSlot>> _bindingOrders = new();

    // -----------------------------------------------------------------------
    // Phase 2 — link (§16.2.1.5.2 Link): allocate cells, resolve imports,
    // instantiate closures. Depth-first with cycle handling via [[Status]].
    // -----------------------------------------------------------------------

    private void Link(ModuleRecord record)
    {
        if (record.Status is ModuleStatus.Linked or ModuleStatus.Evaluating or ModuleStatus.Evaluated)
        {
            return;
        }

        if (record.Status == ModuleStatus.Linking)
        {
            return; // already on the stack (cycle) — its cells exist; defer wiring is done below
        }

        record.Status = ModuleStatus.Linking;

        // Allocate one live-binding cell per local declaration so importers can
        // alias them even while a cycle is mid-link.
        EnsureLocalCells(record);

        // Link dependencies first (depth-first) so their export cells exist.
        foreach (var dep in record.RequestedModules)
        {
            Link(Resolve(dep, record.Url));
        }

        // Build the upvalue table for the body in BindingOrder slot order.
        var order = _bindingOrders[record];
        var upvalues = new JsValue[order.Count];
        for (var i = 0; i < order.Count; i++)
        {
            var slot = order[i];
            Cell cell = slot.IsImport
                ? ResolveImportCell(record, slot.Import!)
                : record.LocalBindings[slot.Name];
            upvalues[i] = JsValue.Object(cell);
        }

        record.Instance = new JsFunction(record.Url, record.Body, arityDeclared: 0, upvalues)
        {
            // wp:M3-03b — a top-level-await module body is, runtime-wise, an async
            // function body: invoking it routes through StartAsyncBody, suspends
            // at each await, and returns a Promise. Non-TLA modules stay Normal
            // and keep the synchronous evaluation contract.
            Kind = record.HasTopLevelAwait ? JsFunctionKind.Async : JsFunctionKind.Normal,
        };

        // §16.2.1.5.2 Link step 9 — after wiring, every name the module exports
        // must resolve unambiguously (ResolveExport ≠ null / ≠ "ambiguous").
        // This catches re-export errors (`export { x } from` a module that does
        // not export x, a `default` requested through `export *`, and circular
        // indirect re-exports) at link time as a SyntaxError, BEFORE evaluation.
        ValidateModuleExports(record);

        record.Status = ModuleStatus.Linked;
    }

    /// <summary>§16.2.1.5.2 Link — validate that each name this module exports
    /// (excluding pure local declarations, which trivially resolve) resolves to a
    /// binding. An unresolvable re-export is a link-time SyntaxError.</summary>
    private void ValidateModuleExports(ModuleRecord record)
    {
        foreach (var e in record.ExportEntries)
        {
            // Only indirect (`export { x } from`) and named-star
            // (`export * as ns`) entries can fail to resolve; a local export or a
            // bare `export *` either has a cell or contributes no single name.
            if (e.IsLocal)
            {
                continue;
            }

            if (e.IsStar && e.ExportName is null)
            {
                continue; // bare `export *` — per-name checked lazily
            }

            // Indirect re-export: the imported name must resolve in the target.
            if (!e.IsStar && e.ImportName is not null && e.ModuleRequest is not null)
            {
                var dep = Resolve(e.ModuleRequest, record.Url);
                var cell = ResolveExportCell(dep, e.ImportName, new HashSet<string>(StringComparer.Ordinal));
                if (cell is null)
                {
                    throw new JsThrow(_runtime.Realm.NewSyntaxError(
                        $"The requested module '{e.ModuleRequest}' does not provide an export named '{e.ImportName}'"));
                }
            }
        }
    }

    /// <summary>§16.2.1.5.3-style ResolveExport, reduced to cell resolution: find
    /// the <see cref="Cell"/> that backs <paramref name="entry"/>'s imported name
    /// in the exporting module (following indirect/star re-exports).</summary>
    private Cell ResolveImportCell(ModuleRecord importer, ModuleImportEntry entry)
    {
        var target = Resolve(entry.ModuleRequest, importer.Url);
        // Ensure the target's cells exist (it may be later in link order under a
        // cycle); allocating here is idempotent with EnsureLocalCells.
        EnsureLocalCells(target);

        if (entry.IsNamespace)
        {
            // Namespace import: back the local name with a cell holding the
            // module namespace object (built lazily, after the target links).
            var ns = GetOrBuildNamespace(target);
            return new Cell(JsValue.Object(ns));
        }

        var cell = ResolveExportCell(target, entry.ImportName, new HashSet<string>(StringComparer.Ordinal));
        return cell ?? throw new JsThrow(_runtime.Realm.NewSyntaxError(
            $"The requested module '{entry.ModuleRequest}' does not provide an export named '{entry.ImportName}'"));
    }

    /// <summary>Resolve the cell backing exported <paramref name="exportName"/> in
    /// <paramref name="module"/>, following indirect and star re-exports.</summary>
    private Cell? ResolveExportCell(ModuleRecord module, string exportName, HashSet<string> seen)
    {
        if (!seen.Add(module.Url + "\0" + exportName))
        {
            return null; // cycle in re-exports
        }

        // 1) Local export (export const/function/class, export { x }).
        foreach (var e in module.ExportEntries)
        {
            if (e.IsLocal && e.ExportName == exportName && e.LocalName is not null)
            {
                EnsureLocalCells(module);
                if (module.LocalBindings.TryGetValue(e.LocalName, out var local))
                {
                    return local;
                }
            }
        }

        // 2) Indirect re-export (export { x } from "..."; export { x as y } from).
        foreach (var e in module.ExportEntries)
        {
            if (e.IsStar && e.ExportName == exportName && e.ModuleRequest is not null)
            {
                var dep = Resolve(e.ModuleRequest, module.Url);
                var ns = GetOrBuildNamespace(dep);
                return new Cell(JsValue.Object(ns));
            }

            if (!e.IsLocal && !e.IsStar && e.ExportName == exportName && e.ImportName is not null)
            {
                var dep = Resolve(e.ModuleRequest!, module.Url);
                var c = ResolveExportCell(dep, e.ImportName, seen);
                if (c is not null)
                {
                    return c;
                }
            }
        }

        // 3) Star re-export (export * from "..."). Search each star target for
        //    the name (named star re-exports — export * as ns — bind a namespace
        //    and are handled as a local export above once built). §16.2.1.5.3 —
        //    `export *` never re-exports the `default` name, so a request for
        //    `default` must NOT be satisfied through a star edge.
        if (exportName != "default")
        {
            foreach (var e in module.ExportEntries)
            {
                if (!e.IsLocal && e.IsStar && e.ExportName is null)
                {
                    var dep = Resolve(e.ModuleRequest!, module.Url);
                    var c = ResolveExportCell(dep, exportName, seen);
                    if (c is not null)
                    {
                        return c;
                    }
                }
            }
        }

        return null;
    }

    // -----------------------------------------------------------------------
    // Phase 3 — evaluate (§16.2.1.5.4): depth-first, idempotent, cycle-safe.
    // -----------------------------------------------------------------------

    /// <summary>§16.2.1.5.4 Evaluate, extended for async (top-level-await)
    /// modules (wp:M3-03b) and async <em>cycles</em> (wp:M3-03d). Returns
    /// <c>null</c> when the module's whole subtree evaluated synchronously (no
    /// async settling needed — the historical contract for non-TLA graphs).
    /// Returns a <see cref="JsPromise"/> when the module (or a dependency, or its
    /// strongly connected component) is async: that promise settles once the
    /// body — and, for an async cycle, every member of the component — has
    /// finished, and is stored on <see cref="ModuleRecord.EvaluationPromise"/> so
    /// the result is idempotent and importers can chain on it.</summary>
    /// <remarks>
    /// This is the public-facing idempotent entry. The real depth-first walk
    /// lives in <see cref="InnerEvaluate"/>, a pragmatic Tarjan SCC pass that
    /// groups cycle members so an async cycle settles jointly.
    /// </remarks>
    private JsPromise? Evaluate(ModuleRecord record)
    {
        if (record.Status is ModuleStatus.Evaluating or ModuleStatus.Evaluated)
        {
            // Re-entrant (idempotent or cycle back-edge). For a synchronous
            // module that already errored, preserve the throwing contract.
            if (record.EvaluationError is not null && record.EvaluationPromise is null)
            {
                throw record.EvaluationError;
            }

            return record.EvaluationPromise;
        }

        // Drive the SCC-aware depth-first evaluation. A fresh stack per
        // top-level Evaluate call; the DFS counter only needs to be monotonic
        // within one walk.
        InnerEvaluate(record, new List<ModuleRecord>());
        return record.EvaluationPromise;
    }

    /// <summary>Monotonic discovery counter for the evaluation DFS (§16.2.1.5.4
    /// <c>index</c>). Reset implicitly per top-level <see cref="Evaluate"/> walk
    /// is unnecessary — it only has to be strictly increasing within a walk.</summary>
    private int _dfsCounter;

    /// <summary>Transient per-module collection of <em>forward</em> async
    /// dependency promises discovered during the DFS: promises of already-settled-
    /// or-settling dependency SCCs that this module must wait on before its body
    /// runs. (Back-edges into the current SCC are NOT forward deps — they are the
    /// cycle itself and settle jointly.)</summary>
    private readonly Dictionary<ModuleRecord, List<JsPromise>> _forwardDeps = new();

    /// <summary>§16.2.1.5.4 InnerModuleEvaluation — the SCC-aware DFS. Visits a
    /// module, recurses into dependencies maintaining Tarjan low-link indices,
    /// and when the module is the root of a strongly connected component
    /// (<c>DfsAncestorIndex == DfsIndex</c>) pops the whole component and hands
    /// it to <see cref="FinishScc"/> for synchronous or joint-async settlement.
    /// </summary>
    private void InnerEvaluate(ModuleRecord module, List<ModuleRecord> stack)
    {
        module.Status = ModuleStatus.Evaluating;
        module.DfsIndex = module.DfsAncestorIndex = _dfsCounter++;
        module.OnEvalStack = true;
        stack.Add(module);
        _forwardDeps[module] = new List<JsPromise>();

        foreach (var depSpec in module.RequestedModules)
        {
            var dep = Resolve(depSpec, module.Url);
            if (dep.Status is ModuleStatus.Linked or ModuleStatus.Linking)
            {
                // Tree edge — not yet visited in evaluation. Recurse, then take
                // the low-link of the subtree.
                try
                {
                    InnerEvaluate(dep, stack);
                }
                catch (JsThrow ex)
                {
                    // A synchronous dependency threw during its DFS. Mirror the
                    // historical synchronous contract: adopt the error and rethrow
                    // so an outer synchronous caller surfaces it immediately.
                    module.EvaluationError = ex;
                    module.Status = ModuleStatus.Evaluated;
                    module.OnEvalStack = false;
                    _forwardDeps.Remove(module);
                    throw;
                }

                if (dep.OnEvalStack)
                {
                    // The dependency is still on the stack: it belongs to the same
                    // (not-yet-closed) strongly connected component as us. Merge
                    // low-links so we share an SCC root and settle jointly.
                    module.DfsAncestorIndex = Math.Min(module.DfsAncestorIndex, dep.DfsAncestorIndex);
                }
                else
                {
                    // The dependency's own SCC closed during the recursion — it is
                    // a fully separate (forward) dependency component. If it is
                    // still settling asynchronously, wait on its promise; if it
                    // errored synchronously, propagate now.
                    CollectForwardDep(module, dep);
                }
            }
            else if (dep.OnEvalStack)
            {
                // Back-edge into a module still on the DFS stack: a cycle. Pull
                // its discovery index into our low-link so we (and any enclosing
                // members) share an SCC root. The body does NOT run here — the
                // SCC root settles every member jointly (wp:M3-03d). This is the
                // crux of the fix: previously a back-edge into an unfinished
                // async module returned partial bindings; now it participates in
                // the component's joint (possibly async) settlement instead.
                module.DfsAncestorIndex = Math.Min(module.DfsAncestorIndex, dep.DfsIndex);
            }
            else
            {
                // Forward / cross edge to an already-finished component (visited in
                // an earlier branch of this DFS, or in a prior Evaluate call).
                CollectForwardDep(module, dep);
            }
        }

        // SCC root? Pop the component (everything pushed at or after this module).
        if (module.DfsAncestorIndex == module.DfsIndex)
        {
            var members = new List<ModuleRecord>();
            ModuleRecord popped;
            do
            {
                popped = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                popped.OnEvalStack = false;
                members.Add(popped); // pop order == DFS post-order (deepest first)
            }
            while (!ReferenceEquals(popped, module));

            FinishScc(members);
        }
    }

    /// <summary>Record a dependency on a <em>closed</em> component (one that is no
    /// longer on the DFS stack) for <paramref name="module"/>. A component still
    /// settling asynchronously contributes its settlement promise to
    /// <paramref name="module"/>'s forward-dependency wait set; a component that
    /// errored synchronously is propagated immediately (mirroring the historical
    /// synchronous contract).</summary>
    private void CollectForwardDep(ModuleRecord module, ModuleRecord dep)
    {
        if (dep.EvaluationError is not null && dep.EvaluationPromise is null)
        {
            module.EvaluationError = dep.EvaluationError;
            module.Status = ModuleStatus.Evaluated;
            module.OnEvalStack = false;
            _forwardDeps.Remove(module);
            throw dep.EvaluationError;
        }
        if (dep.EvaluationPromise is not null)
        {
            var list = _forwardDeps[module];
            if (!list.Contains(dep.EvaluationPromise))
            {
                list.Add(dep.EvaluationPromise);
            }
        }
    }

    /// <summary>Settle one strongly connected component. A component with no
    /// top-level-await member and no forward async dependency runs synchronously
    /// in DFS post-order — the historical fast path for synchronous graphs and
    /// synchronous cycles (partial-binding semantics preserved). A component that
    /// contains a top-level-await member (or depends on an async component) is an
    /// <em>async</em> component: every member shares one settlement promise and
    /// its bodies run only after the forward async dependencies settle, with the
    /// async (TLA) members completing before the synchronous members so the latter
    /// observe settled exports rather than in-flight bindings (wp:M3-03d).</summary>
    private void FinishScc(List<ModuleRecord> members)
    {
        // Gather the component's forward async dependency promises (dedup across
        // members; a back-edge inside the component never produced one).
        var forwardDeps = new List<JsPromise>();
        var anyTla = false;
        foreach (var m in members)
        {
            if (m.HasTopLevelAwait)
            {
                anyTla = true;
            }

            foreach (var p in _forwardDeps[m])
            {
                if (!forwardDeps.Contains(p))
                {
                    forwardDeps.Add(p);
                }
            }

            _forwardDeps.Remove(m);
        }

        var isAsync = anyTla || forwardDeps.Count > 0;
        if (!isAsync)
        {
            // Synchronous component: run every member's body in DFS post-order.
            // For a single module this is exactly the old synchronous fast path;
            // for a synchronous cycle this preserves the (spec-correct) partial-
            // binding semantics, since members run in the same order as before.
            foreach (var m in members)
            {
                try
                {
                    var vm = _runtime.Realm.ActiveVm ?? new JsVm(_runtime);
                    vm.CallFunction(m.Instance!, JsValue.Undefined, Array.Empty<JsValue>());
                }
                catch (JsThrow ex)
                {
                    m.EvaluationError = ex;
                    m.Status = ModuleStatus.Evaluated;
                    throw;
                }
                m.Status = ModuleStatus.Evaluated;
            }
            return;
        }

        // Async component: one shared settlement promise for the whole SCC so a
        // dependent chaining on any member waits for the entire cycle to settle.
        var sccPromise = new JsPromise(_runtime.Realm.PromisePrototype);
        foreach (var m in members)
        {
            m.AsyncEvaluation = true;
            m.EvaluationPromise = sccPromise;
            m.Status = ModuleStatus.Evaluated; // kicked off; settles via microtasks
        }

        // Run async (TLA) members first, then synchronous members, so a sync
        // member that imports an awaited export observes the settled value rather
        // than a partial binding. Within each tier we keep DFS post-order.
        var runOrder = new List<ModuleRecord>();
        foreach (var m in members)
        {
            if (m.HasTopLevelAwait)
            {
                runOrder.Add(m);
            }
        }

        foreach (var m in members)
        {
            if (!m.HasTopLevelAwait)
            {
                runOrder.Add(m);
            }
        }

        WhenAll(forwardDeps,
            onFulfilled: () => RunSccBodies(members, runOrder, 0, sccPromise),
            onRejected: reason =>
            {
                // A forward async dependency errored: the whole component errors;
                // no member body runs (§16.2.1.5.4: a module errors if a
                // dependency errors).
                FailScc(members, reason, sccPromise);
            });
    }

    /// <summary>Run an async component's member bodies one at a time in
    /// <paramref name="runOrder"/>, awaiting each top-level-await body before
    /// starting the next, then settle the shared <paramref name="sccPromise"/>.
    /// Sequential execution (rather than a parallel join) guarantees the awaited
    /// exports of earlier members are visible to later members of the same cycle.</summary>
    private void RunSccBodies(List<ModuleRecord> members, List<ModuleRecord> runOrder, int index, JsPromise sccPromise)
    {
        if (index >= runOrder.Count)
        {
            PromiseCtor.Resolve(_runtime.Realm, sccPromise, JsValue.Undefined);
            return;
        }

        var realm = _runtime.Realm;
        var vm = realm.ActiveVm ?? new JsVm(_runtime);
        var current = runOrder[index];
        JsValue result;
        try
        {
            result = vm.CallFunction(current.Instance!, JsValue.Undefined, Array.Empty<JsValue>());
        }
        catch (JsThrow ex)
        {
            // A synchronous member body threw after the cycle's async work began.
            FailScc(members, ex.Value, sccPromise);
            return;
        }

        // A TLA member's body returned its own Promise — await it before moving
        // on to the next member. A synchronous member returned a plain value.
        if (result.IsObject && result.AsObject is JsPromise bodyPromise)
        {
            var onFulfill = new JsNativeFunction("", (_, _) =>
            {
                RunSccBodies(members, runOrder, index + 1, sccPromise);
                return JsValue.Undefined;
            }, isConstructor: false);
            var onReject = new JsNativeFunction("", (_, args) =>
            {
                FailScc(members, args.Length > 0 ? args[0] : JsValue.Undefined, sccPromise);
                return JsValue.Undefined;
            }, isConstructor: false);
            var then = AbstractOperations.Get(vm, bodyPromise, "then");
            AbstractOperations.Call(vm, then, JsValue.Object(bodyPromise),
                new[] { JsValue.Object(onFulfill), JsValue.Object(onReject) });
        }
        else
        {
            RunSccBodies(members, runOrder, index + 1, sccPromise);
        }
    }

    /// <summary>Record an evaluation failure across an entire async component:
    /// adopt <paramref name="reason"/> as every member's evaluation error (so
    /// <see cref="LoadAndEvaluate"/> rethrows it for any root in the cycle) and
    /// reject the shared settlement promise so all dependents see the error.</summary>
    private void FailScc(List<ModuleRecord> members, JsValue reason, JsPromise sccPromise)
    {
        var thrown = new JsThrow(reason);
        foreach (var m in members)
        {
            m.EvaluationError ??= thrown;
        }

        PromiseCtor.Reject(_runtime.Realm, sccPromise, reason);
    }

    /// <summary>Promise.all-style join over module evaluation promises. Invokes
    /// <paramref name="onFulfilled"/> once every promise has fulfilled, or
    /// <paramref name="onRejected"/> with the first rejection reason (short-circuit:
    /// later settlements are ignored). Empty list → fulfil synchronously. Built on
    /// the async-function-style <c>.then</c> wiring already used by the VM, so it
    /// rides the same microtask queue the loader drains.</summary>
    private void WhenAll(List<JsPromise> promises, Action onFulfilled, Action<JsValue> onRejected)
    {
        if (promises.Count == 0) { onFulfilled(); return; }

        var vm = _runtime.Realm.ActiveVm ?? new JsVm(_runtime);
        var remaining = promises.Count;
        var done = false;

        foreach (var p in promises)
        {
            var onFulfill = new JsNativeFunction("", (_, _) =>
            {
                if (!done && --remaining == 0) { done = true; onFulfilled(); }
                return JsValue.Undefined;
            }, isConstructor: false);
            var onReject = new JsNativeFunction("", (_, args) =>
            {
                if (!done) { done = true; onRejected(args.Length > 0 ? args[0] : JsValue.Undefined); }
                return JsValue.Undefined;
            }, isConstructor: false);
            var then = AbstractOperations.Get(vm, p, "then");
            AbstractOperations.Call(vm, then, JsValue.Object(p),
                new[] { JsValue.Object(onFulfill), JsValue.Object(onReject) });
        }
    }

    // -----------------------------------------------------------------------
    // Module Namespace Exotic Object (§10.4.6) — live-binding accessor view.
    // -----------------------------------------------------------------------

    /// <summary>Build (or return cached) the §10.4.6 Module Namespace Exotic
    /// Object for a module. Each resolved exported name is wired to the live
    /// backing <see cref="Cell"/> the exporting module writes, so reads through
    /// the namespace reflect later mutations of the binding. The returned object
    /// is a <see cref="JsModuleNamespace"/> whose internal methods implement the
    /// exotic behaviour (null prototype, non-extensible, read-only/immutable
    /// keys, code-unit-sorted [[OwnPropertyKeys]], <c>@@toStringTag</c> of
    /// <c>"Module"</c>).</summary>
    public JsObject GetOrBuildNamespace(ModuleRecord module)
    {
        if (module.Namespace is not null)
        {
            return module.Namespace;
        }

        // §10.4.6 / §16.2.1.10 ModuleNamespaceCreate: the [[Exports]] list is the
        // module's resolved export names (export * expanded, default excluded
        // from re-exported stars), each bound to its live cell. A name that fails
        // to resolve to a cell is dropped (it is not a usable binding).
        var exports = new Dictionary<string, Cell>(StringComparer.Ordinal);
        var ns = new JsModuleNamespace(exports);
        module.Namespace = ns;

        foreach (var name in ExportedNames(module, new HashSet<string>(StringComparer.Ordinal)))
        {
            var cell = ResolveExportCell(module, name, new HashSet<string>(StringComparer.Ordinal));
            if (cell is not null)
            {
                exports[name] = cell;
            }
        }

        ns.RefreshExportNames();
        return ns;
    }

    /// <summary>Collect every name exported by a module, expanding
    /// <c>export *</c> re-exports (excluding <c>default</c>, per spec).</summary>
    private IEnumerable<string> ExportedNames(ModuleRecord module, HashSet<string> visited)
    {
        if (!visited.Add(module.Url))
        {
            yield break;
        }

        var names = new List<string>();
        void Add(string n) { if (!names.Contains(n)) { names.Add(n); } }

        foreach (var e in module.ExportEntries)
        {
            if (e.ExportName is not null && !e.IsStar)
            {
                Add(e.ExportName);
            }
            else if (e.IsStar && e.ExportName is not null)
            {
                Add(e.ExportName); // export * as ns
            }
        }
        foreach (var e in module.ExportEntries)
        {
            if (e.IsStar && e.ExportName is null)
            {
                var dep = Resolve(e.ModuleRequest!, module.Url);
                foreach (var n in ExportedNames(dep, visited))
                {
                    if (n != "default")
                    {
                        Add(n);
                    }
                }
            }
        }
        foreach (var n in names)
        {
            yield return n;
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>Allocate a live-binding <see cref="Cell"/> for every local
    /// (non-imported) top-level binding of a module. Idempotent.</summary>
    private void EnsureLocalCells(ModuleRecord record)
    {
        foreach (var slot in _bindingOrders[record])
        {
            if (!slot.IsImport)
            {
                // §16.2.1.6.2 / §9.4.2 — a local lexical binding (let/const/class/
                // default) starts in the Temporal Dead Zone: its cell holds the TDZ
                // sentinel until the module body's initializer writes it, so a read
                // before then throws a ReferenceError (the body emits TDZ-checked
                // reads for these). A var/function binding starts as `undefined`
                // (var is pre-initialized; functions are hoisted into the cell).
                var initial = slot.IsLexical
                    ? JsValue.Object(_runtime.Realm.TdzSentinel)
                    : JsValue.Undefined;
                record.LocalBindings.TryAdd(slot.Name, new Cell(initial));
            }
        }
    }

    private ModuleRecord Resolve(string specifier, string referrer)
    {
        var url = ResolveOrThrow(specifier, referrer);
        return _registry.TryGetValue(url, out var rec)
            ? rec
            : throw new JsThrow(_runtime.Realm.NewError(
                _runtime.Realm.TypeErrorPrototype, $"Module not loaded: {url}"));
    }

    private string ResolveOrThrow(string specifier, string? referrer) =>
        _host.Resolve(specifier, referrer)
        ?? throw new JsThrow(_runtime.Realm.NewError(
            _runtime.Realm.TypeErrorPrototype, $"Failed to resolve module specifier: {specifier}"));
}

/// <summary>Host hook for specifier resolution and source fetching. Lets the
/// engine plug its file:// + http fetch path in while tests use an in-memory
/// map.</summary>
public interface IModuleHost
{
    /// <summary>Resolve <paramref name="specifier"/> relative to
    /// <paramref name="referrer"/> (the absolute URL of the importing module, or
    /// null for the entry module) into an absolute module-map key. Return null on
    /// failure.</summary>
    string? Resolve(string specifier, string? referrer);

    /// <summary>Fetch the source text for an already-resolved module URL. Return
    /// null on failure.</summary>
    string? FetchSource(string resolvedUrl);
}
