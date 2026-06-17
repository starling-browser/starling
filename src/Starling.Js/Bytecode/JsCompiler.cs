using Starling.Js.Ast;
using Starling.Js.Lex;
using Starling.Js.Runtime;

namespace Starling.Js.Bytecode;

/// <summary>
/// Walks a parsed JS <see cref="Program"/> and emits a <see cref="Chunk"/>
/// of bytecode for <see cref="JsVm"/> to execute.
/// </summary>
/// <remarks>
/// The compiler owns scope binding, Temporal Dead Zone checks, closures,
/// class bodies, modules, direct eval, control flow, and source-position
/// metadata. Runtime-only semantics stay in <see cref="JsVm"/> and the
/// intrinsic helpers.
/// </remarks>
public sealed partial class JsCompiler
{
    private readonly ChunkBuilder _b = new();
    private readonly List<Dictionary<string, int>> _scopes = [new()];

    /// <summary>TDZ — names in each <see cref="_scopes"/> frame that are
    /// lexical bindings (<c>let</c>/<c>const</c>/<c>class</c>) and therefore
    /// subject to the Temporal Dead Zone. Parallel to <see cref="_scopes"/>:
    /// index <c>i</c> here is the lexical-name set for scope frame <c>i</c>.
    /// Reads/writes resolved to a name in this set emit the TDZ-checking
    /// opcode variants; <c>var</c>/param/function bindings are absent and use
    /// the plain (no-check) opcodes. A name's presence is set when the binding
    /// is instantiated in the uninitialized state and cleared (removed) once
    /// the binding has provably been initialized in straight-line code, so a
    /// declaration's own initializer store and post-declaration reads skip the
    /// check.</summary>
    private readonly List<HashSet<string>> _lexicalScopes = [new(StringComparer.Ordinal)];

    /// <summary>§13.15.2 / §16.2.1.6.2 — names in each <see cref="_scopes"/>
    /// frame that are <c>const</c> bindings, so a write (plain or compound)
    /// after initialization is a runtime TypeError. Parallel to
    /// <see cref="_scopes"/>. Unlike <see cref="_lexicalScopes"/> (which is
    /// cleared once a binding is provably initialized so post-decl reads skip the
    /// TDZ check), const-ness is permanent — a const stays read-only for its
    /// whole lifetime — so entries here are NEVER removed. A store to a name in
    /// this set emits <see cref="Opcode.ThrowConstAssignment"/> instead of the
    /// real store, EXCEPT inside the binding's own initializer
    /// (<see cref="_inLexicalDeclInit"/>), which performs the one legal write.</summary>
    private readonly List<HashSet<string>> _constScopes = [new(StringComparer.Ordinal)];

    /// <summary>TDZ — true while emitting a <c>let</c>/<c>const</c>
    /// declaration's own initializer store (including destructuring leaves).
    /// In this window a store to an in-scope lexical binding is the
    /// initialization itself, so it must use the UNCHECKED store opcode (the
    /// slot legitimately still holds the TDZ sentinel). Outside this window,
    /// assignments to a lexical binding emit the checked store so a write
    /// before initialization throws ReferenceError.</summary>
    private bool _inLexicalDeclInit;

    /// <summary>True while compiling the body of an async generator. Read by
    /// <see cref="EmitYield"/> so a <c>yield*</c> in an async generator uses
    /// the async iteration protocol (<c>@@asyncIterator</c> with the sync
    /// fallback wrapper, §27.6.3.7) instead of the sync one — without this an
    /// async <c>yield* asyncIterable</c> wrongly throws "value is not
    /// iterable".</summary>
    private bool _currentIsAsyncGenerator;

    /// <summary>Stack of class private-name scopes — each frame maps the
    /// source-level name (e.g. <c>#x</c>) to its mangled own-property key
    /// for the active class. Resolution walks outer-to-inner so nested
    /// classes shadow correctly.</summary>
    private readonly Stack<Dictionary<string, string>> _privateScopes = new();

    /// <summary>Enclosing compiler — null for the script-top compiler. Used
    /// by lazy upvalue resolution to walk lexically-enclosing scopes when
    /// a free identifier inside a nested function isn't local here.</summary>
    private readonly JsCompiler? _parent;

    /// <summary>
    /// Description of a captured name as recorded in this function's
    /// upvalue table. <see cref="IsLocalCapture"/> distinguishes the two
    /// cases: when true, <see cref="Index"/> is a parent local slot;
    /// when false, it's an index into the parent's upvalue table (i.e.
    /// the capture chains through an intermediate function).
    /// </summary>
    private readonly record struct UpvalueRef(bool IsLocalCapture, int Index);

    /// <summary>Upvalue descriptors for the function this compiler is
    /// producing. Index in this list is the operand passed to
    /// <see cref="Opcode.LoadUpvalue"/> inside the body, and is the order
    /// in which the parent must push captured values before
    /// <see cref="Opcode.MakeClosure"/>.</summary>
    private readonly List<UpvalueRef> _upvalues = [];

    /// <summary>Name → index in <see cref="_upvalues"/>, so repeated reads
    /// of the same captured name reuse one upvalue slot.</summary>
    private readonly Dictionary<string, int> _upvalueByName = new(StringComparer.Ordinal);

    /// <summary>Names declared in this compiler's function that are referenced
    /// from inside one or more nested functions. Computed by
    /// <see cref="CaptureAnalysis.Compute"/> before any bytecode is emitted.
    /// Declaration sites use this to decide whether the slot needs
    /// <see cref="Starling.Js.Runtime.Cell"/> storage.</summary>
    private HashSet<string> _capturedNames = new(StringComparer.Ordinal);

    /// <summary>§10.2.1.1 / §13.2.5 — synthetic binding name for the captured
    /// lexical <c>this</c>. An arrow function resolves <c>this</c> to the
    /// nearest enclosing ordinary function's <c>this</c>; the enclosing function
    /// materializes it into a captured Cell under this name (illegal as a user
    /// identifier, so no collision) which the arrow reads as an upvalue.</summary>
    private const string LexicalThisName = "<this>";

    /// <summary>B7-followup-b — open-loop tracking for <c>break</c> /
    /// <c>continue</c>. Each entry holds the patch lists for jumps targeting
    /// the loop's continue / break sites; the loop body emits forward
    /// <see cref="Opcode.Jump"/> instructions and records the operand
    /// position so the loop's lowering pass can patch them once the
    /// continue/break PCs are known. <see cref="TryDepthAtEntry"/> records
    /// the try-frame depth at the loop's entry; a <c>break</c> /
    /// <c>continue</c> whose enclosing try-depth is greater than this would
    /// need to run intervening <c>finally</c> blocks (currently
    /// unsupported — the compiler throws).</summary>
    private sealed class LoopFrame
    {
        public List<int> BreakPatches { get; } = [];
        public List<int> ContinuePatches { get; } = [];
        public int TryDepthAtEntry { get; init; }
        /// <summary>Try-frame depth seen by a <c>continue</c> targeting this
        /// frame. For most loops this equals <see cref="TryDepthAtEntry"/>. A
        /// for-of/for-in wraps each iteration body in a synthetic
        /// <c>try { … } finally { IteratorClose }</c>: a <c>break</c> (or
        /// outward break/continue/return/throw) must run that finalizer so the
        /// iterator closes, but a <c>continue</c> to THIS loop must re-step
        /// WITHOUT closing (§14.7.5.6 LoopContinues). The two therefore see
        /// different depths — break uses <see cref="TryDepthAtEntry"/> (outer,
        /// so it crosses the synthetic finally), continue uses this (inner, so
        /// it stays within the protected region and just re-steps).
        /// Null means "same as <see cref="TryDepthAtEntry"/>" (the common case).</summary>
        public int? ContinueTryDepth { get; init; }
        /// <summary>True when this frame belongs to a switch statement rather
        /// than an iteration statement. A bare <c>continue</c> must skip switch
        /// frames and target the nearest enclosing iteration frame instead.</summary>
        public bool IsSwitch { get; init; }
        /// <summary>Optional label that directly wraps this statement; used by
        /// <c>break &lt;label&gt;</c> / <c>continue &lt;label&gt;</c> resolution.</summary>
        public string? Label { get; init; }
    }

    private readonly Stack<LoopFrame> _loops = new();

    /// <summary>B7-followup-b — depth of currently open try-frames in the
    /// emitted bytecode. Incremented on <see cref="Opcode.EnterTry"/>,
    /// decremented after the matching cleanup. Used by <c>break</c> /
    /// <c>continue</c> to detect the (currently-unsupported) cross-finally
    /// case.</summary>
    private int _tryDepth;

    /// <summary>§14.11 — number of <c>with</c> object Environment Records that
    /// lexically enclose the code currently being compiled in THIS function.
    /// While &gt; 0, every unqualified identifier reference (load / store /
    /// update / typeof / delete / call) routes through the with-aware opcodes
    /// (<see cref="Opcode.WithLoadOrMiss"/> etc.) so the with-object is consulted
    /// before the statically-resolved binding. A nested function body resets
    /// this to 0 (a <c>with</c> object never extends into a nested function's
    /// own free-identifier resolution unless it too is textually inside the
    /// <c>with</c>; that nesting is handled by the parent's upvalue capture).</summary>
    private int _withDepth;

    /// <summary>wp:M3-72 — when this is the top-level compiler for direct-eval
    /// source, the set of binding names visible in the calling function's
    /// variable environment. A free identifier in the eval'd code that would
    /// otherwise fall through to a global load/store and whose name is in this
    /// set instead routes through the caller-scope-aware opcodes
    /// (<see cref="Opcode.LoadEvalScope"/> / <see cref="Opcode.StoreEvalScope"/>)
    /// so it resolves to the caller's live binding. Null for ordinary
    /// script/module/function compilation (no direct-eval caller scope). Nested
    /// function bodies inside the eval'd code inherit it so their own free
    /// identifiers can also reach the caller's environment.</summary>
    private IReadOnlySet<string>? _callerScopeNames;

    /// <summary>wp:M3-73 — true when compiling a NON-strict direct eval whose
    /// caller is a function: the eval body's OWN top-level var/function
    /// declarations are injected into the caller frame's eval-introduced var
    /// store (via <see cref="Opcode.DeclareEvalVar"/> / <see cref="Opcode.StoreEvalVar"/>)
    /// — or, when the name is an existing caller binding, written through
    /// <see cref="Opcode.StoreEvalScope"/> — instead of binding on the global
    /// object. Only consulted at the eval body's TOP-LEVEL (script-top of the
    /// eval chunk); nested function bodies inside the eval'd code have their own
    /// real var-environments and never inject.</summary>
    private bool _evalInjectVars;

    /// <summary>True for a direct eval chunk: top-level let/const declarations
    /// are scoped to the eval body, not installed on the global object.</summary>
    private bool _directEvalLocalLexicals;

    /// <summary>True for strict direct eval: top-level var/function declarations
    /// are scoped to the eval body, not injected into the caller/global env.</summary>
    private bool _directEvalLocalVars;

    /// <summary>wp:M3-80 — the local slot of each positional formal parameter, in
    /// declaration order, recorded by <see cref="BindFunctionParameters"/> and
    /// consumed by <see cref="MaybeBindArguments"/> to build the §10.4.4.6 mapped
    /// arguments object's parameter map. Only meaningful for a simple parameter
    /// list (every formal a plain identifier); <see cref="_paramsAreSimple"/>
    /// gates its use. Null until parameters are bound.</summary>
    private int[]? _paramSlots;

    /// <summary>wp:M3-80 — true when this function's parameter list is "simple"
    /// per §15.1.3 (every formal a plain <c>Identifier</c>; no defaults, rest, or
    /// destructuring). A simple list in a non-strict function gets the mapped
    /// arguments object; anything else stays unmapped.</summary>
    private bool _paramsAreSimple;

    /// <summary>wp:M3-79 — statement completion value (§13–§14 + §13.2.13
    /// UpdateEmpty). When compiling eval / Program code (<see cref="CompileForEval"/>
    /// / <see cref="CompileForDirectEval"/>) this holds a reserved local slot that
    /// accumulates the running completion value: each statement with a NON-empty
    /// completion (an ExpressionStatement, or a construct that produces an explicit
    /// value) stores its value into it; statements with an EMPTY completion
    /// (EmptyStatement, var/let/const/function/class declarations, an empty Block)
    /// leave it untouched (UpdateEmpty keeps the prior value). Constructs that
    /// "create their own running value" (if / with / switch / loops / try) reset it
    /// to <c>undefined</c> at entry so their own completion overwrites the prior
    /// statement's value (e.g. <c>1; if (false) {}</c> → undefined). The slot's
    /// final value is what an <c>eval</c> returns. <c>null</c> for ordinary
    /// script / function-body compilation, which never observes completion values.</summary>
    private int? _completionSlot;

    public JsCompiler() : this(parent: null) { }

    private JsCompiler(JsCompiler? parent)
    {
        _parent = parent;
        // wp:M3-63 — inherit the enclosing script/module source path so every
        // nested function chunk (arrow / async / generator / method / ctor /
        // field-init / static-block) carries the SAME SourcePath as the
        // top-level chunk. Dynamic import() and import.meta.url then resolve
        // relative to the active script/module, not the running function's own
        // (path-less) chunk name. The top-level entry-points overwrite this
        // with the real script/module URL before emission.
        if (parent is not null)
        {
            _b.SourcePath = parent._b.SourcePath;
        }
        // wp:M3-72 — a nested function inside direct-eval'd code can still
        // reference the original caller's bindings (they are captured as
        // upvalues through the eval top-level chunk where they resolve to the
        // caller scope), so inherit the caller-scope name set down the chain.
        if (parent is not null)
        {
            _callerScopeNames = parent._callerScopeNames;
        }
    }

    /// <summary>§14.11 / §10.2.1 — configure this (child) compiler to inherit the
    /// enclosing <c>with</c> environment. When the parent was inside a
    /// <c>with</c> (<paramref name="enclosingWithDepth"/> &gt; 0) and this body
    /// is NOT strict (a strict body cannot reference a `with`, since `with` is
    /// sloppy-only — but a nested strict function still severs the dynamic
    /// scope), free identifiers route through the with-aware opcodes and the
    /// chunk is flagged so the VM seeds the callee frame's with-stack from the
    /// captured snapshot.</summary>
    private void ConfigureWithCapture(int enclosingWithDepth, bool bodyIsStrict)
    {
        if (enclosingWithDepth <= 0 || bodyIsStrict)
        {
            return;
        }

        _withDepth = enclosingWithDepth;
        _capturedWithBase = enclosingWithDepth;
        _b.CapturesWith = true;
    }

    /// <summary>§9.1.1.2 — the number of inherited (captured) <c>with</c>
    /// environments that sit OUTSIDE this function's own variable environment.
    /// A reference to one of this function's own bindings (param / var /
    /// function / lexical local) must NOT consult those captured with-objects,
    /// since they are lexically outer to the binding. Only a <c>with</c>
    /// statement of THIS function (which pushes its env INNER to the locals at
    /// runtime) may shadow a local — i.e. when <see cref="_withDepth"/> exceeds
    /// this base.</summary>
    private int _capturedWithBase;

    /// <summary>Should a reference to <paramref name="name"/> route through the
    /// with-aware opcodes? Free identifiers (no current-function local) consult
    /// every active with-object. A name that resolves to a current-function
    /// local is only shadowable by a <c>with</c> pushed inside this function
    /// (depth above the captured base), never by an inherited/captured one.</summary>
    private bool ShouldRouteWith(string name)
    {
        if (_withDepth <= 0)
        {
            return false;
        }

        if (TryResolveLocal(name, out _))
        {
            return _withDepth > _capturedWithBase;
        }

        return true;
    }

    /// <summary>Is this name marked for shared-cell storage in the current
    /// function?</summary>
    private bool IsNameCaptured(string name) => _capturedNames.Contains(name);

    /// <summary>Did the compiler box the local at this slot? Used by load and
    /// store emission sites.</summary>
    private bool IsSlotCaptured(int slot) => _b.IsCaptured(slot);

    /// <summary>TDZ — is <paramref name="name"/> a lexical binding
    /// (<c>let</c>/<c>const</c>/<c>class</c>) in some currently-open scope of
    /// THIS function? Walks innermost-out, mirroring
    /// <see cref="TryResolveLocal"/>, and stops at the first frame that
    /// declares the name as a local so an inner <c>var</c> shadowing the same
    /// name (only legal across function boundaries) doesn't misfire.</summary>
    private bool IsLexicalLocal(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].ContainsKey(name))
            {
                return _lexicalScopes[i].Contains(name);
            }
        }
        return false;
    }

    /// <summary>TDZ — mark <paramref name="name"/> as a lexical binding in the
    /// innermost scope frame (must already be reserved as a local there).</summary>
    private void MarkLexical(string name) => _lexicalScopes[^1].Add(name);

    /// <summary>§13.15.2 — mark <paramref name="name"/> as a <c>const</c> binding
    /// in the innermost scope frame so later writes throw TypeError.</summary>
    private void MarkConst(string name) => _constScopes[^1].Add(name);

    /// <summary>§13.15.2 — is <paramref name="name"/> a <c>const</c> binding in
    /// some currently-open scope of THIS function? Mirrors
    /// <see cref="IsLexicalLocal"/>: walk innermost-out and stop at the first
    /// frame that declares the name as a local, so an inner <c>var</c> shadowing
    /// the name (only legal across function boundaries) does not misreport.</summary>
    private bool IsConstLocal(string name)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].ContainsKey(name))
            {
                return _constScopes[i].Contains(name);
            }
        }
        return false;
    }

    /// <summary>Push a fresh lexical scope frame, keeping
    /// <see cref="_scopes"/> and <see cref="_lexicalScopes"/> aligned.</summary>
    private void PushScope()
    {
        _scopes.Add(new());
        _lexicalScopes.Add(new(StringComparer.Ordinal));
        _constScopes.Add(new(StringComparer.Ordinal));
    }

    /// <summary>Pop the innermost lexical scope frame.</summary>
    private void PopScope()
    {
        _scopes.RemoveAt(_scopes.Count - 1);
        _lexicalScopes.RemoveAt(_lexicalScopes.Count - 1);
        _constScopes.RemoveAt(_constScopes.Count - 1);
    }

    private bool IsConstLocalSlot(int slot)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            foreach (var binding in _scopes[i])
            {
                if (binding.Value == slot)
                {
                    return _constScopes[i].Contains(binding.Key);
                }
            }
        }

        return false;
    }

    private bool IsLexicalLocalSlot(int slot)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            foreach (var binding in _scopes[i])
            {
                if (binding.Value == slot)
                {
                    return _lexicalScopes[i].Contains(binding.Key);
                }
            }
        }

        return false;
    }

    private string? ModuleBindingNameForUpvalue(int upIdx)
    {
        if (_moduleBindingUpvalues is null)
        {
            return null;
        }

        foreach (var binding in _moduleBindingUpvalues)
        {
            if (binding.Value == upIdx)
            {
                return binding.Key;
            }
        }

        return null;
    }

    /// <summary>TDZ — is this resolved upvalue a lexical binding? This follows
    /// the upvalue descriptor chain rather than the source name, so a function
    /// local can shadow a same-named module lexical binding.</summary>
    private bool IsLexicalUpvalue(int upIdx)
    {
        if (ModuleBindingNameForUpvalue(upIdx) is { } moduleName)
        {
            return _moduleLexicalBindings is not null && _moduleLexicalBindings.Contains(moduleName);
        }

        if (upIdx < 0 || upIdx >= _upvalues.Count || _parent is null)
        {
            return false;
        }

        var upvalue = _upvalues[upIdx];
        return upvalue.IsLocalCapture
            ? _parent.IsLexicalLocalSlot(upvalue.Index)
            : _parent.IsLexicalUpvalue(upvalue.Index);
    }

    /// <summary>§13.15.2 / §16.2.1.6.2 — is this resolved upvalue immutable
    /// (<c>const</c> or import)? This follows the upvalue descriptor chain rather
    /// than the source name, so a function-local <c>let o</c> is not mistaken for
    /// a shadowed module <c>const o</c>.</summary>
    private bool IsImmutableUpvalue(int upIdx)
    {
        if (ModuleBindingNameForUpvalue(upIdx) is { } moduleName)
        {
            return _moduleImmutableBindings is not null && _moduleImmutableBindings.Contains(moduleName);
        }

        if (upIdx < 0 || upIdx >= _upvalues.Count || _parent is null)
        {
            return false;
        }

        var upvalue = _upvalues[upIdx];
        return upvalue.IsLocalCapture
            ? _parent.IsConstLocalSlot(upvalue.Index)
            : _parent.IsImmutableUpvalue(upvalue.Index);
    }

    /// <summary>gap:script-top-var-not-global — true when this compiler is
    /// emitting the top-level script chunk (i.e. not inside any function or
    /// arrow body). Script-top <c>var</c> / <c>let</c> / <c>const</c>
    /// declarations become properties on the global object per §16.1.7
    /// ScriptEvaluation, so they're addressed by name (LoadGlobal /
    /// StoreGlobal / DeclareGlobalVar) rather than allocated to local
    /// slots.</summary>
    private bool IsScriptTop => _parent is null;

    /// <summary>TDZ — true only in the OUTERMOST script lexical scope, where a
    /// top-level <c>let</c>/<c>const</c> binds on the global object (§16.1.7)
    /// rather than a local slot. Global-lexical TDZ is deferred, so these
    /// bindings are not TDZ-hoisted. A <c>let</c>/<c>const</c> inside any
    /// nested scope (block, switch, loop body, …) — even at script top — is a
    /// true block-scoped lexical binding and IS subject to TDZ.</summary>
    private bool IsGlobalLexicalScope => IsScriptTop && !_directEvalLocalLexicals && _scopes.Count == 1;

    public static Chunk Compile(Program program, string? name = "<script>")
    {
        var c = new JsCompiler();
        c._b.IsStrict = program.Strict;
        c._b.SourcePath = name; // wp:M3-63 — referrer for dynamic import()
        c.RunCaptureAnalysisForScript(program.Body);
        c.EmitProgram(program, keepLastExpression: false);
        return c._b.Build(name);
    }

    /// <summary>
    /// Compile in eval / REPL mode: if the last statement is an
    /// ExpressionStatement, leave its value on the stack so the caller of
    /// the VM can observe it. Used by <c>starling js</c> and by tests.
    /// </summary>
    public static Chunk CompileForEval(Program program, string? name = "<eval>")
    {
        var c = new JsCompiler();
        c._b.IsStrict = program.Strict;
        c._b.SourcePath = name; // wp:M3-63 — referrer for dynamic import()
        c.RunCaptureAnalysisForScript(program.Body);
        c.EmitProgram(program, keepLastExpression: true);
        return c._b.Build(name);
    }

    /// <summary>wp:M3-72 — compile direct-eval source. Identical to
    /// <see cref="CompileForEval"/> except that free identifiers whose name is in
    /// <paramref name="callerScopeNames"/> (the calling function's in-scope
    /// binding names) route through the caller-scope-aware load/store opcodes so
    /// they resolve to the caller's live binding (§19.2.1.1 PerformEval running
    /// the code in the caller's variable environment) instead of the global
    /// object. The eval body's OWN top-level var/lexical declarations still bind
    /// as usual (on the global object for non-strict eval — Starling's existing
    /// script-top behaviour — which the EvalDeclarationInstantiation conflict
    /// check in the caller guards against colliding with caller lexicals).
    /// <para>wp:M3-73 — when <paramref name="injectVars"/> is true (a non-strict
    /// direct eval whose caller is a function), the eval body's OWN top-level
    /// <c>var</c>/function declarations are instead injected into the caller's
    /// variable environment: a name that is already a caller binding writes
    /// through that live binding (<see cref="Opcode.StoreEvalScope"/>), and a
    /// brand-new name lands in the caller frame's eval-introduced var store
    /// (<see cref="Opcode.DeclareEvalVar"/> / <see cref="Opcode.StoreEvalVar"/>).
    /// The eval body's reads/writes of those new names resolve through that store
    /// at runtime (the global-fallback opcodes consult it first).</para></summary>
    public static Chunk CompileForDirectEval(Program program, string? name,
        IReadOnlySet<string> callerScopeNames, bool injectVars = false,
        IReadOnlyDictionary<string, string>? privateNameScope = null)
    {
        var c = new JsCompiler
        {
            _callerScopeNames = callerScopeNames,
            _evalInjectVars = injectVars,
            _directEvalLocalLexicals = true,
            _directEvalLocalVars = program.Strict
        };
        c._b.IsStrict = program.Strict;
        c._b.SourcePath = name;
        // §19.2.1.1 — a direct eval inherits the caller's PrivateEnvironment, so
        // eval'd code can resolve `this.#m` against the enclosing class's private
        // names. Seed the private-name scope from the caller's chunk.
        if (privateNameScope is { Count: > 0 })
        {
            c._privateScopes.Push(new Dictionary<string, string>(privateNameScope, StringComparer.Ordinal));
        }

        c._capturedNames = CaptureAnalysis.Compute(Array.Empty<Expression>(), program.Body);
        c.EmitProgram(program, keepLastExpression: true);
        return c._b.Build(name);
    }

    /// <summary>gap:script-top-var-not-global — script-top declarations
    /// (<c>var</c>, <c>let</c>, <c>const</c>, function-declarations, classes)
    /// all bind on the global object now, so nested functions resolve any
    /// free identifier through <see cref="Opcode.LoadGlobal"/> /
    /// <see cref="Opcode.StoreGlobal"/>. There are no script-top "locals"
    /// to capture, so the captured-name set is empty.</summary>
    private void RunCaptureAnalysisForScript(IReadOnlyList<Statement> body)
    {
        _ = body;
        _capturedNames = new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Populate <see cref="_capturedNames"/> for a function body. Run
    /// by the sub-compiler before any bytecode is emitted, so declaration sites
    /// can pick the right opcode.</summary>
    private void RunCaptureAnalysisForFunction(IReadOnlyList<Expression> parameters, IReadOnlyList<Statement> body)
    {
        _capturedNames = CaptureAnalysis.Compute(parameters, body);
        // §10.2.1.1 — a nested arrow that reads `this` captures this function's
        // `this` binding. Seed the synthetic name so the prologue boxes it.
        if (CaptureAnalysis.ReferencesThisInNestedArrow(parameters, body))
        {
            _capturedNames.Add(LexicalThisName);
        }
    }

    private void EmitProgram(Program p, bool keepLastExpression)
    {
        // wp:M3-79 — eval / Program code returns the §13–§14 statement completion
        // value (with §13.2.13 UpdateEmpty). Reserve a completion register,
        // initialise it to undefined, and accumulate each statement's completion
        // into it as the body is emitted (see _completionSlot). The final value is
        // loaded onto the stack at Halt so RunEval / Run returns it. Ordinary
        // (non-eval) script compilation leaves _completionSlot null and behaves as
        // before. Reserve the slot FIRST so it is stable across the whole body.
        if (keepLastExpression)
        {
            var cv = _b.ReserveLocal();
            _completionSlot = cv;
            _b.Emit(Opcode.LoadUndefined);
            _b.EmitSlot(Opcode.StoreLocal, cv);
        }

        // wp:M3-73 — §19.2.1.3 EvalDeclarationInstantiation (non-global branch):
        // before any eval-body code runs, idempotently pre-declare every
        // top-level var-declared name (and top-level function name) that is NOT
        // already an existing caller binding into the caller frame's
        // eval-introduced var store, so a `var x` is visible (as undefined) to
        // reads that precede its textual position and a re-`var` of an existing
        // store binding is a no-op. Names that ARE existing caller bindings are
        // skipped (the binding already exists; re-declaration has no effect).
        if (_evalInjectVars)
        {
            foreach (var vn in Parse.JsParser.EvalVarDeclaredNames(p))
            {
                if (_callerScopeNames is { } cs && cs.Contains(vn))
                {
                    continue;
                }

                _b.EmitU16(Opcode.DeclareEvalVar, _b.AddConstant(vn));
            }
        }
        // Captured top-level vars are pre-allocated as cells before function
        // hoisting, so a hoisted function body resolves a captured name to the
        // parent's local cell instead of falling through to LoadGlobal/StoreGlobal.
        PreallocateCapturedVarBindings(p.Body);
        if (_directEvalLocalLexicals)
        {
            HoistLexicalDeclarations(p.Body);
        }
        // Hoist FunctionDeclarations: compile bodies, allocate locals,
        // emit StoreLocal in declaration order so they're callable before
        // their textual position (matches §10.2.11 / §13.2.1 var hoisting
        // for function declarations).
        HoistFunctionDeclarations(p.Body);

        foreach (var s in p.Body)
        {
            if (s is FunctionDeclaration)
            {
                // Already hoisted; nothing to emit at the textual position.
                // A function declaration's completion is empty (§14.x), so it
                // leaves the completion register unchanged (UpdateEmpty).
                continue;
            }
            EmitStatement(s);
        }
        // wp:M3-79 — leave the accumulated completion value on the stack for the
        // VM's Halt to return as the eval / Program completion value.
        if (_completionSlot is int finalSlot)
        {
            _b.EmitSlot(Opcode.LoadLocal, finalSlot);
        }

        _b.Emit(Opcode.Halt);
    }

    private void HoistFunctionDeclarations(IReadOnlyList<Statement> body)
    {
        // At the script top there is no enclosing function, so hoisted
        // function-declarations install themselves as globals (mirroring
        // §10.2.11 host-defined global object behavior). Inside a function,
        // they bind a fresh local slot in the function's variable scope.
        var isScriptTop = IsScriptTop && !_directEvalLocalVars;

        // PASS 1 — register a slot / binding for EVERY function declaration in
        // this scope BEFORE compiling any body. Without this, a body that
        // forward-references a LATER-defined sibling (e.g. `function a(){return
        // b();} function b(){…}`) compiles that reference to a (undefined)
        // global instead of capturing the sibling's slot as an upvalue. (At
        // script top, declarations are late-bound globals so order is moot, but
        // registering uniformly is harmless.)
        var hoisted = new List<(FunctionDeclaration Fd, int? Slot)>();
        foreach (var s in body)
        {
            if (s is not FunctionDeclaration fd)
            {
                continue;
            }

            int? slot = null;
            if (!isScriptTop)
            {
                if (_scopes[^1].TryGetValue(fd.Name.Name, out var existing))
                {
                    slot = existing;
                }
                else
                {
                    var fresh = _b.ReserveLocal();
                    _scopes[^1][fd.Name.Name] = fresh;
                    if (IsNameCaptured(fd.Name.Name))
                    {
                        _b.MarkCaptured(fresh);
                    }

                    if (_b.IsCaptured(fresh))
                    {
                        _b.EmitSlot(Opcode.InitCellLocal, fresh);
                    }

                    slot = fresh;
                }
            }
            hoisted.Add((fd, slot));
        }

        // PASS 2 — compile each body + construct the closure + store. All
        // sibling names are registered now, so cross-references (forward and
        // backward) resolve to the right slot/upvalue.
        foreach (var (fd, slot) in hoisted)
        {
            // Compile the body in a fresh sub-compiler parented to this one so
            // the body can resolve free identifiers as upvalues captured here.
            var sub = new JsCompiler(parent: this);
            sub._b.IsStrict = fd.Strict;
            // §14.11 / §10.2.1 — a function declared lexically inside a `with`
            // inherits the enclosing object Environment Records.
            sub.ConfigureWithCapture(_withDepth, fd.Strict);
            sub.RunCaptureAnalysisForFunction(fd.Params, fd.Body.Body);
            sub.EmitFunctionBody(fd);
            var chunk = sub._b.Build(fd.Name.Name);

            // Push the function value (LoadFunction or upvalues + MakeClosure),
            // then store under the function's name (global at script top, else
            // the reserved local slot).
            EmitFunctionConstructor(fd.Name.Name, chunk,
                CountSimpleParams(fd.Params), sub._upvalues,
                ResolveFunctionKind(fd.Async, fd.Generator), fd.SourceText);
            if (isScriptTop && _evalInjectVars)
            {
                // wp:M3-73 — §19.2.1.3 (non-global branch). The function object is
                // on the stack; bind it into the caller's variable environment:
                // an existing caller binding is updated through its live storage,
                // a new name lands in the caller frame's eval-introduced var store.
                EmitEvalInjectedStore(fd.Name.Name);
            }
            else if (isScriptTop)
            {
                // §16.1.7 GlobalDeclarationInstantiation — a hoisted function
                // declaration creates a global binding before any code runs.
                // Declare it first so the StoreGlobal is a write to an existing
                // binding (otherwise strict mode would reject it as an
                // assignment to an undeclared global).
                var nameIdx = _b.AddConstant(fd.Name.Name);
                _b.EmitU16(Opcode.DeclareGlobalVar, nameIdx);
                _b.EmitU16(Opcode.StoreGlobal, nameIdx);
            }
            else
            {
                EmitStoreLocalSlot(slot!.Value);
            }
        }
    }

    /// <summary>wp:M3-73 — store the value on top of the stack into an eval-body
    /// top-level <c>var</c>/function binding being injected into the caller's
    /// variable environment. A name that is already an existing caller binding
    /// writes through its live storage (<see cref="Opcode.StoreEvalScope"/>);
    /// a brand-new name is set in the caller frame's eval-introduced var store
    /// (the binding was pre-declared by <see cref="Opcode.DeclareEvalVar"/>).
    /// Only valid while <see cref="_evalInjectVars"/> is set.</summary>
    private void EmitEvalInjectedStore(string name)
    {
        if (_callerScopeNames is { } cs && cs.Contains(name))
        {
            _b.EmitU16(Opcode.StoreEvalScope, _b.AddConstant(name));
        }
        else
        {
            _b.EmitU16(Opcode.StoreEvalVar, _b.AddConstant(name));
        }
    }

    /// <summary>Emit the correct store opcode for a local slot, accounting for
    /// whether the slot was promoted to a cell.</summary>
    private void EmitStoreLocalSlot(int slot)
    {
        if (_b.IsCaptured(slot))
        {
            _b.EmitSlot(Opcode.StoreCellLocal, slot);
        }
        else
        {
            _b.EmitSlot(Opcode.StoreLocal, slot);
        }
    }

    /// <summary>Emit the correct load opcode for a local slot, accounting for
    /// whether the slot was promoted to a cell.</summary>
    private void EmitLoadLocalSlot(int slot)
    {
        if (_b.IsCaptured(slot))
        {
            _b.EmitSlot(Opcode.LoadCellLocal, slot);
        }
        else
        {
            _b.EmitSlot(Opcode.LoadLocal, slot);
        }
    }

    /// <summary>wp:M3-79 — true when this compile is tracking statement completion
    /// values (eval / Program code). See <see cref="_completionSlot"/>.</summary>
    private bool TrackCompletion => _completionSlot is not null;

    /// <summary>wp:M3-79 — set the completion register to <c>undefined</c>. Emitted
    /// at the entry of constructs whose own completion overwrites the running value
    /// (if / with / switch / loops / try) so e.g. <c>1; if (false) {}</c> → undefined
    /// and a zero-iteration loop yields undefined. No-op when not tracking.</summary>
    private void EmitCompletionReset()
    {
        if (_completionSlot is not int slot)
        {
            return;
        }

        _b.Emit(Opcode.LoadUndefined);
        _b.EmitSlot(Opcode.StoreLocal, slot);
    }

    /// <summary>wp:M3-79 — store the value currently on top of the stack into the
    /// completion register (consuming it). Used for an ExpressionStatement's value.
    /// No-op (and leaves the value where it is) when not tracking.</summary>
    private void EmitCompletionStore()
    {
        if (_completionSlot is not int slot)
        {
            return;
        }

        _b.EmitSlot(Opcode.StoreLocal, slot);
    }

    /// <summary>Materialize a function as either a plain template
    /// reference (no upvalues — emits <see cref="Opcode.LoadFunction"/>)
    /// or as a closure-construction sequence: push each upvalue's
    /// snapshot, then emit <see cref="Opcode.MakeClosure"/>. Leaves the
    /// resulting function value on the top of the stack.</summary>
    private void EmitFunctionConstructor(
        string name, Chunk body, int arity, IReadOnlyList<UpvalueRef> upvalues)
        => EmitFunctionConstructor(name, body, arity, upvalues, Runtime.JsFunctionKind.Normal);

    private void EmitFunctionConstructor(
        string name, Chunk body, int arity, IReadOnlyList<UpvalueRef> upvalues,
        Runtime.JsFunctionKind kind, string? sourceText = null)
    {
        var fn = new Runtime.JsFunction(name, body, arity)
        {
            Kind = kind,
            SourceText = sourceText,
        };
        var fnIdx = _b.AddConstant(fn);

        if (upvalues.Count == 0)
        {
            _b.EmitU16(Opcode.LoadFunction, fnIdx);
            return;
        }

        if (upvalues.Count > 65535)
        {
            throw new NotSupportedException("more than 65535 captured variables not supported");
        }

        // Push the captured cell, not its value, so the new closure aliases the
        // same shared cell that the owning function reads and writes. Parent
        // local slots already hold the cell as a JsValue (allocated by
        // InitCellLocal / PromoteParamCell), so a plain LoadLocal pushes the
        // cell. Parent upvalues are dereferenced by the default LoadUpvalue, so
        // we need LoadUpvalueCell to push the cell intact.
        foreach (var u in upvalues)
        {
            if (u.IsLocalCapture)
            {
                // The parent's slot must already be a cell here (the
                // static CaptureAnalysis seeded the parent's
                // _capturedNames before any bytecode was emitted, and
                // every declaration site honored that). Plain LoadLocal
                // pushes the slot value — which is the cell.
                _b.EmitSlot(Opcode.LoadLocal, u.Index);
            }
            else
            {
                _b.EmitUpvalue(Opcode.LoadUpvalueCell, u.Index);
            }
        }
        _b.EmitU16(Opcode.MakeClosure, fnIdx);
        _b.EmitU16Raw(upvalues.Count);
    }

    private static int CountSimpleParams(IReadOnlyList<Expression> ps)
    {
        var n = 0;
        foreach (var p in ps)
        {
            if (p is SpreadElement)
            {
                break;
            }

            if (p is AssignmentExpression or AssignmentPattern)
            {
                break;
            }

            n++;
        }
        return n;
    }

    /// <summary>§named-evaluation (8.4.5 NamedEvaluation, 13.15.5.3 etc.) — an
    /// anonymous function definition (an unnamed function/generator/async
    /// expression, an arrow, or an unnamed class expression) used directly as
    /// the RHS of a binding/assignment initializer adopts the binding's name as
    /// its <c>name</c> own property. Returns true when <paramref name="rhs"/> is
    /// such an anonymous function definition.</summary>
    private static bool IsAnonymousFunctionDefinition(Expression rhs) => rhs switch
    {
        FunctionExpression fe => fe.Name is null,
        ArrowFunctionExpression => true,
        ClassExpression ce => ce.Name is null,
        _ => false,
    };

    /// <summary>§named-evaluation — emit <paramref name="rhs"/> and, if it is an
    /// anonymous function definition, stamp <paramref name="name"/> onto the
    /// resulting function/class. The leaves the value on the stack either way.</summary>
    private void EmitNamedEvaluation(Expression rhs, string name)
    {
        EmitExpression(rhs);
        if (IsAnonymousFunctionDefinition(rhs))
        {
            _b.EmitU16(Opcode.SetFunctionName, _b.AddConstant(name));
        }
    }

    /// <summary>Compile a function body. Parameters get the first N local
    /// slots; the body's own var declarations follow.</summary>
    private void EmitFunctionBody(FunctionDeclaration fd)
    {
        // Reserve a local slot per simple-identifier parameter so the
        // callee sees args in slots 0..N-1.
        // wp:M3-81 — a function declaration is never an arrow, so its parameter
        // default region is an initializer context for the eval ContainsArguments rule.
        BindFunctionParameters(fd.Params, markInitializer: true);
        // Captured `var` bindings must exist as locals before we compile any
        // nested function body that might resolve them as upvalues. Pre-allocate
        // slots for every captured var in this function's body, and emit
        // InitCellLocal so the slot already holds a Cell when an inner closure is
        // constructed.
        PreallocateCapturedVarBindings(fd.Body.Body);
        HoistVarDeclarations(fd.Body.Body);
        // Temporal Dead Zone — instantiate top-level let/const before we decide
        // whether to synthesize `arguments`, so a real lexical binding named
        // `arguments` wins over the implicit object.
        HoistLexicalDeclarations(fd.Body.Body);
        // Function declarations inside a function body are hoisted to the top
        // of the function per §13.2.1 and §14.1.18. Register lexical names first
        // so hoisted function bodies capture same-scope class/let/const names.
        HoistFunctionDeclarations(fd.Body.Body);
        // wp:M3-20 — synthesize the `arguments` object if the body reads it.
        MaybeBindArguments(fd.Params, fd.Body.Body);
        // §10.2.1.1 — box `this` if a nested arrow reads it.
        MaybeBindLexicalThis();
        // §10.2.1.3 — for generator/async/async-generator bodies the parameter-
        // binding prologue above must run synchronously at call time; mark the
        // boundary so the runtime can hand off here before the body runs lazily.
        EmitPrologueEndIfSuspendable(fd.Async, fd.Generator);
        _currentIsAsyncGenerator = fd.Async && fd.Generator;
        foreach (var inner in fd.Body.Body)
        {
            EmitStatement(inner);
        }
        // Implicit `return undefined` if the body didn't return.
        _b.Emit(Opcode.ReturnUndefined);
    }

    /// <summary>§10.2.1.3 / §15.5.2 / §27 — emit a <see cref="Opcode.PrologueEnd"/>
    /// marker for generator / async / async-generator bodies. The runtime runs
    /// everything before this marker (parameter destructuring + defaults +
    /// RequireObjectCoercible + iterator protocol + <c>arguments</c> + var/lexical
    /// hoisting) synchronously when the function is called, so a throw surfaces
    /// to the caller before the generator object / promise is produced. Ordinary
    /// functions get no marker (their bodies already run synchronously).</summary>
    private void EmitPrologueEndIfSuspendable(bool isAsync, bool isGenerator)
    {
        if (isAsync || isGenerator)
        {
            _b.Emit(Opcode.PrologueEnd);
        }
    }

    /// <summary>Walk this function's body and pre-allocate local slots for
    /// every <c>var</c>, <c>let</c>, and <c>const</c> binding whose name is
    /// captured by a nested function. Captured slots are also initialized to a
    /// Cell immediately, so a nested closure built during hoisting can capture
    /// the cell.
    /// </summary>
    /// <remarks>
    /// Non-captured locals stay unallocated here — they're declared at
    /// their textual site by <see cref="DeclarePatternBindings"/>, which
    /// keeps the fast path unchanged for the overwhelming majority of
    /// bindings.
    /// </remarks>
    private void PreallocateCapturedVarBindings(IReadOnlyList<Statement> body)
    {
        foreach (var s in body)
        {
            PreallocateCapturedInStatement(s);
        }
    }

    private void HoistVarDeclarations(IReadOnlyList<Statement> body)
    {
        foreach (var s in body)
        {
            HoistVarDeclarationsInStatement(s);
        }
    }

    private void HoistVarDeclarationsInStatement(Statement? s)
    {
        if (s is null)
        {
            return;
        }

        switch (s)
        {
            case VariableDeclaration vd:
                if (vd.Kind == "var")
                {
                    foreach (var d in vd.Declarations)
                    {
                        HoistVarPattern(d.Id);
                    }
                }

                return;
            case BlockStatement b:
                foreach (var inner in b.Body)
                {
                    HoistVarDeclarationsInStatement(inner);
                }

                return;
            case IfStatement i:
                HoistVarDeclarationsInStatement(i.Consequent);
                HoistVarDeclarationsInStatement(i.Alternate);
                return;
            case WhileStatement w: HoistVarDeclarationsInStatement(w.Body); return;
            case DoWhileStatement dw: HoistVarDeclarationsInStatement(dw.Body); return;
            case ForStatement f:
                if (f.Init is VariableDeclaration fvd && fvd.Kind == "var")
                {
                    foreach (var d in fvd.Declarations)
                    {
                        HoistVarPattern(d.Id);
                    }
                }

                HoistVarDeclarationsInStatement(f.Body);
                return;
            case ForInStatement fi:
                if (fi.Left is VariableDeclaration fivd && fivd.Kind == "var")
                {
                    foreach (var d in fivd.Declarations)
                    {
                        HoistVarPattern(d.Id);
                    }
                }

                HoistVarDeclarationsInStatement(fi.Body);
                return;
            case ForOfStatement fo:
                if (fo.Left is VariableDeclaration fovd && fovd.Kind == "var")
                {
                    foreach (var d in fovd.Declarations)
                    {
                        HoistVarPattern(d.Id);
                    }
                }

                HoistVarDeclarationsInStatement(fo.Body);
                return;
            case SwitchStatement sw:
                foreach (var c in sw.Cases)
                {
                    foreach (var inner in c.Consequent)
                    {
                        HoistVarDeclarationsInStatement(inner);
                    }
                }

                return;
            case TryStatement tr:
                HoistVarDeclarationsInStatement(tr.Block);
                if (tr.Handler is not null)
                {
                    foreach (var inner in tr.Handler.Body.Body)
                    {
                        HoistVarDeclarationsInStatement(inner);
                    }
                }

                if (tr.Finalizer is not null)
                {
                    HoistVarDeclarationsInStatement(tr.Finalizer);
                }

                return;
            case LabeledStatement ls: HoistVarDeclarationsInStatement(ls.Body); return;
            case WithStatement ws: HoistVarDeclarationsInStatement(ws.Body); return;
            case FunctionDeclaration:
            case ClassDeclaration:
                return;
        }
    }

    private void HoistVarPattern(Expression? pattern)
    {
        if (pattern is null)
        {
            return;
        }

        switch (pattern)
        {
            case Identifier id:
                if (_scopes[0].ContainsKey(id.Name))
                {
                    return;
                }

                var slot = _b.ReserveLocal();
                _scopes[0][id.Name] = slot;
                if (IsNameCaptured(id.Name))
                {
                    _b.MarkCaptured(slot);
                    _b.EmitSlot(Opcode.InitCellLocal, slot);
                }
                else
                {
                    _b.EmitSlot(Opcode.DeclareLocal, slot);
                }
                return;
            case AssignmentExpression { Op: JsTokenKind.Eq } a: HoistVarPattern(a.Target); return;
            case AssignmentPattern a: HoistVarPattern(a.Target); return;
            case ArrayPattern arr:
                foreach (var el in arr.Elements)
                {
                    switch (el)
                    {
                        case ArrayPatternBindingElement binding: HoistVarPattern(binding.Target); break;
                        case ArrayPatternRestElement rest: HoistVarPattern(rest.Target); break;
                    }
                }
                return;
            case ObjectPattern obj:
                foreach (var prop in obj.Properties)
                {
                    HoistVarPattern(prop.Target);
                }

                if (obj.Rest is not null)
                {
                    HoistVarPattern(obj.Rest.Argument);
                }

                return;
            case ArrayExpression arr:
                foreach (var el in arr.Elements)
                {
                    if (el is null)
                    {
                        continue;
                    }

                    HoistVarPattern(el is SpreadElement sp ? sp.Argument : el);
                }
                return;
            case ObjectExpression obj:
                foreach (var prop in obj.Properties)
                {
                    if (prop.Value is SpreadElement sp)
                    {
                        HoistVarPattern(sp.Argument);
                    }
                    else
                    {
                        HoistVarPattern(prop.Value);
                    }
                }
                return;
            case SpreadElement spread: HoistVarPattern(spread.Argument); return;
            case RestElement rest: HoistVarPattern(rest.Argument); return;
        }
    }

    private void PreallocateCapturedInStatement(Statement? s, bool inBlock = false)
    {
        if (s is null)
        {
            return;
        }

        switch (s)
        {
            case VariableDeclaration vd:
                {
                    // TDZ — a captured let/const reserves its function-lifetime cell
                    // in the uninitialized (sentinel) state so a read before the
                    // declaration's initializer throws ReferenceError even through a
                    // closure. `var` keeps the undefined-seeded cell.
                    var lexical = vd.Kind is "let" or "const";
                    // A captured let/const INSIDE a nested block is reserved at block
                    // entry instead (HoistLexicalName), in the block's own frame, so
                    // it gets its OWN cell rather than sharing — and clobbering — a
                    // same-named function-scoped binding's cell (function decl / var)
                    // hoisted into this function frame. `var` stays function-scoped
                    // and must still be preallocated here even inside a block.
                    if (lexical && inBlock)
                    {
                        return;
                    }

                    foreach (var d in vd.Declarations)
                    {
                        PreallocateCapturedInPattern(d.Id, lexical);
                    }

                    return;
                }
            // ClassDeclaration: block-scoped class TDZ is deferred; the class
            // name still binds on the global object (EmitClassDeclaration), so
            // it is not preallocated as a captured lexical cell here.
            case BlockStatement b: foreach (var x in b.Body) { PreallocateCapturedInStatement(x, inBlock: true); } return;
            case IfStatement i:
                PreallocateCapturedInStatement(i.Consequent);
                PreallocateCapturedInStatement(i.Alternate);
                return;
            case WhileStatement w: PreallocateCapturedInStatement(w.Body); return;
            case DoWhileStatement dw: PreallocateCapturedInStatement(dw.Body); return;
            // Loop heads: only `var` bindings are function-scoped and need a
            // function-frame cell here. A captured loop-head let/const gets its
            // per-iteration cell from the loop's own scope at emit time
            // (ReserveForOfLetSlots / the for-init declare path); preallocating
            // it into the function frame would plant a phantom TDZ-sentinel
            // binding that shadows any same-named outer variable for every
            // closure compiled later in the function — the sentinel is never
            // initialized, so those closures throw ReferenceError on a name
            // that should have resolved outward.
            case ForStatement f:
                if (f.Init is VariableDeclaration { Kind: "var" } fvd)
                {
                    foreach (var d in fvd.Declarations)
                    {
                        PreallocateCapturedInPattern(d.Id, lexical: false);
                    }
                }

                PreallocateCapturedInStatement(f.Body);
                return;
            case ForInStatement fi:
                if (fi.Left is VariableDeclaration { Kind: "var" } vdi)
                {
                    foreach (var d in vdi.Declarations)
                    {
                        PreallocateCapturedInPattern(d.Id, lexical: false);
                    }
                }

                PreallocateCapturedInStatement(fi.Body);
                return;
            case ForOfStatement fo:
                if (fo.Left is VariableDeclaration { Kind: "var" } vdo)
                {
                    foreach (var d in vdo.Declarations)
                    {
                        PreallocateCapturedInPattern(d.Id, lexical: false);
                    }
                }

                PreallocateCapturedInStatement(fo.Body);
                return;
            case SwitchStatement sw:
                foreach (var c in sw.Cases)
                {
                    foreach (var s2 in c.Consequent)
                    {
                        PreallocateCapturedInStatement(s2);
                    }
                }

                return;
            case TryStatement tr:
                PreallocateCapturedInStatement(tr.Block);
                if (tr.Handler is not null)
                {
                    foreach (var s2 in tr.Handler.Body.Body)
                    {
                        PreallocateCapturedInStatement(s2);
                    }
                }

                if (tr.Finalizer is not null)
                {
                    PreallocateCapturedInStatement(tr.Finalizer);
                }

                return;
            case LabeledStatement ls: PreallocateCapturedInStatement(ls.Body); return;
            case WithStatement ws: PreallocateCapturedInStatement(ws.Body); return;
        }
    }

    private void PreallocateCapturedInPattern(Expression? pattern, bool lexical = false)
    {
        if (pattern is null)
        {
            return;
        }

        switch (pattern)
        {
            case Identifier id:
                if (IsNameCaptured(id.Name) && !_scopes[^1].ContainsKey(id.Name))
                {
                    var slot = _b.ReserveLocal();
                    _scopes[^1][id.Name] = slot;
                    _b.MarkCaptured(slot);
                    if (lexical)
                    {
                        MarkLexical(id.Name);
                        _b.EmitSlot(Opcode.InitCellLocalTdz, slot);
                    }
                    else
                    {
                        _b.EmitSlot(Opcode.InitCellLocal, slot);
                    }
                }
                return;
            case AssignmentExpression a when a.Op == JsTokenKind.Eq: PreallocateCapturedInPattern(a.Target, lexical); return;
            case AssignmentPattern a: PreallocateCapturedInPattern(a.Target, lexical); return;
            case ArrayPattern arr:
                foreach (var el in arr.Elements)
                {
                    switch (el)
                    {
                        case ArrayPatternBindingElement binding: PreallocateCapturedInPattern(binding.Target, lexical); break;
                        case ArrayPatternRestElement rest: PreallocateCapturedInPattern(rest.Target, lexical); break;
                    }
                }
                return;
            case ObjectPattern obj:
                foreach (var prop in obj.Properties)
                {
                    PreallocateCapturedInPattern(prop.Target, lexical);
                }

                if (obj.Rest is not null)
                {
                    PreallocateCapturedInPattern(obj.Rest.Argument, lexical);
                }

                return;
            case ArrayExpression arr:
                foreach (var el in arr.Elements)
                {
                    if (el is null)
                    {
                        continue;
                    }

                    PreallocateCapturedInPattern(el is SpreadElement sp ? sp.Argument : el, lexical);
                }
                return;
            case ObjectExpression obj:
                foreach (var prop in obj.Properties)
                {
                    if (prop.Value is SpreadElement sp)
                    {
                        PreallocateCapturedInPattern(sp.Argument, lexical);
                    }
                    else
                    {
                        PreallocateCapturedInPattern(prop.Value, lexical);
                    }
                }
                return;
            case SpreadElement spread: PreallocateCapturedInPattern(spread.Argument, lexical); return;
            case RestElement rest: PreallocateCapturedInPattern(rest.Argument, lexical); return;
        }
    }

    /// <summary>TDZ — §14.2.3 BlockDeclarationInstantiation. At entry to a
    /// block (or function body / switch case-block), every <c>let</c>,
    /// <c>const</c>, and <c>class</c> declared at the IMMEDIATE level of that
    /// scope is instantiated in the uninitialized ("TDZ") state BEFORE any
    /// statement runs, so a read or write that textually precedes the
    /// declaration throws ReferenceError. This does NOT descend into nested
    /// blocks, loop bodies, or function bodies (those open their own scopes),
    /// and ignores <c>var</c>/function declarations (which hoist initialized).
    /// Captured lexical bindings are already instantiated to a TDZ cell at
    /// function entry by <see cref="PreallocateCapturedVarBindings"/>, so they
    /// are skipped here (their slot lives in the function scope, reachable via
    /// the normal innermost-out resolution).</summary>
    private void HoistLexicalDeclarations(IReadOnlyList<Statement> body)
    {
        foreach (var s in body)
        {
            switch (s)
            {
                case VariableDeclaration vd when vd.Kind is "let" or "const":
                    foreach (var d in vd.Declarations)
                    {
                        HoistLexicalPattern(d.Id);
                    }

                    break;
                case ClassDeclaration cd when !IsGlobalLexicalScope:
                    HoistLexicalName(cd.Name.Name);
                    break;
                    // Script-top ClassDeclaration still uses the existing global
                    // binding path in EmitClassDeclaration.
                    //
                    // A labeled declaration is only ever a (var-hoisted) function
                    // declaration per Annex B; lexical declarations cannot be
                    // labeled, so labels need no lexical hoisting.
            }
        }
    }

    /// <summary>§13.15.2 — walk a <c>const</c> declarator's binding pattern and
    /// <see cref="MarkConst"/> every bound name in the innermost scope frame, so
    /// later writes throw TypeError. Mirrors <see cref="HoistLexicalPattern"/>'s
    /// pattern traversal (identifier / array / object / rest / default).</summary>
    private void MarkConstNames(Expression pattern)
    {
        switch (pattern)
        {
            case Identifier id: MarkConst(id.Name); return;
            case AssignmentExpression { Op: JsTokenKind.Eq } a: MarkConstNames(a.Target); return;
            case AssignmentPattern a: MarkConstNames(a.Target); return;
            case ArrayPattern arr:
                foreach (var el in arr.Elements)
                {
                    switch (el)
                    {
                        case ArrayPatternBindingElement b: MarkConstNames(b.Target); break;
                        case ArrayPatternRestElement r: MarkConstNames(r.Target); break;
                    }
                }
                return;
            case ObjectPattern obj:
                foreach (var prop in obj.Properties)
                {
                    MarkConstNames(prop.Target);
                }

                if (obj.Rest is not null)
                {
                    MarkConstNames(obj.Rest.Argument);
                }

                return;
            case ArrayExpression arr:
                foreach (var el in arr.Elements)
                {
                    if (el is null)
                    {
                        continue;
                    }

                    MarkConstNames(el is SpreadElement sp ? sp.Argument : el);
                }
                return;
            case ObjectExpression obj:
                foreach (var prop in obj.Properties)
                {
                    MarkConstNames(prop.Value is SpreadElement sp ? sp.Argument : prop.Value);
                }

                return;
            case SpreadElement spread: MarkConstNames(spread.Argument); return;
            case RestElement rest: MarkConstNames(rest.Argument); return;
        }
    }

    private void HoistLexicalPattern(Expression pattern)
    {
        switch (pattern)
        {
            case Identifier id: HoistLexicalName(id.Name); return;
            case AssignmentExpression { Op: JsTokenKind.Eq } a: HoistLexicalPattern(a.Target); return;
            case AssignmentPattern a: HoistLexicalPattern(a.Target); return;
            case ArrayPattern arr:
                foreach (var el in arr.Elements)
                {
                    switch (el)
                    {
                        case ArrayPatternBindingElement b: HoistLexicalPattern(b.Target); break;
                        case ArrayPatternRestElement r: HoistLexicalPattern(r.Target); break;
                    }
                }
                return;
            case ObjectPattern obj:
                foreach (var prop in obj.Properties)
                {
                    HoistLexicalPattern(prop.Target);
                }

                if (obj.Rest is not null)
                {
                    HoistLexicalPattern(obj.Rest.Argument);
                }

                return;
            case ArrayExpression arr:
                foreach (var el in arr.Elements)
                {
                    if (el is null)
                    {
                        continue;
                    }

                    HoistLexicalPattern(el is SpreadElement sp ? sp.Argument : el);
                }
                return;
            case ObjectExpression obj:
                foreach (var prop in obj.Properties)
                {
                    HoistLexicalPattern(prop.Value is SpreadElement sp ? sp.Argument : prop.Value);
                }

                return;
            case SpreadElement spread: HoistLexicalPattern(spread.Argument); return;
            case RestElement rest: HoistLexicalPattern(rest.Argument); return;
        }
    }

    /// <summary>TDZ — instantiate one lexical binding name in the innermost
    /// scope in the uninitialized state. Captured names are already handled at
    /// function entry; non-captured names get a fresh block-local slot seeded
    /// with the TDZ sentinel via <see cref="Opcode.DeclareLocalTdz"/>.</summary>
    private void HoistLexicalName(string name)
    {
        // Global-lexical TDZ is deferred — a top-level script let/const binds on
        // the global object, so don't reserve a local slot for it here.
        if (IsGlobalLexicalScope)
        {
            return;
        }
        // Already instantiated in THIS frame: either a function top-level captured
        // lexical the pre-pass placed here (reuse its cell), or a redeclaration the
        // parser let through — keep the first slot.
        if (_scopes[^1].ContainsKey(name))
        {
            return;
        }
        // A captured lexical not yet in this frame is a BLOCK-scoped binding (the
        // pre-pass defers block lexicals and only seeds the function's top-level
        // ones). Reserve its OWN captured cell in this block's frame so it does
        // not share — and clobber — a same-named function-scoped binding's cell
        // (function declaration / var) resolved from an enclosing frame. This was
        // the github high-contrast-cookie "not a function: [object Object]" bug.
        if (IsNameCaptured(name))
        {
            var cellSlot = _b.ReserveLocal();
            _scopes[^1][name] = cellSlot;
            _b.MarkCaptured(cellSlot);
            MarkLexical(name);
            _b.EmitSlot(Opcode.InitCellLocalTdz, cellSlot);
            return;
        }
        var slot = _b.ReserveLocal();
        _scopes[^1][name] = slot;
        MarkLexical(name);
        _b.EmitSlot(Opcode.DeclareLocalTdz, slot);
    }

    // -----------------------------------------------------------------------
    // Statements
    // -----------------------------------------------------------------------

    private void EmitStatement(Statement s)
    {
        switch (s)
        {
            // EmptyStatement (`;`) has an EMPTY completion (§14.4): leave the
            // completion register untouched (UpdateEmpty keeps the prior value).
            case EmptyStatement: return;
            case ExpressionStatement es:
                EmitExpression(es.Expression);
                // wp:M3-79 — an ExpressionStatement's completion is its expression
                // value (§14.5). When tracking completion, store it into the
                // register (consuming it) rather than discarding it.
                if (TrackCompletion)
                {
                    EmitCompletionStore();
                }
                else
                {
                    _b.Emit(Opcode.Pop);
                }

                return;
            case BlockStatement bs:
                PushScope();
                HoistLexicalDeclarations(bs.Body);
                // §14.2.13 BlockDeclarationInstantiation — a FunctionDeclaration
                // inside a Block creates a block-scoped binding initialized to
                // the function value when the block is entered. Hoist before
                // walking the body so the textual position emits nothing and
                // same-block references resolve through the block scope.
                HoistFunctionDeclarations(bs.Body);
                foreach (var inner in bs.Body)
                {
                    EmitStatement(inner);
                }

                PopScope();
                return;
            case VariableDeclaration vd:
                EmitVarDecl(vd);
                return;
            case IfStatement i:
                // wp:M3-79 — §14.6: an IfStatement's completion is its taken
                // branch's value, or undefined when no branch is taken / the
                // branch is empty (both spec rules end in
                // "Return NormalCompletion(undefined)"). Reset the register to
                // undefined up front so `1; if (false) {}` → undefined; the taken
                // branch's ExpressionStatements then overwrite it.
                EmitCompletionReset();
                EmitExpression(i.Test);
                var jz = _b.EmitJump(Opcode.JumpIfFalse);
                EmitStatement(i.Consequent);
                if (i.Alternate is not null)
                {
                    var jend = _b.EmitJump(Opcode.Jump);
                    _b.PatchJump(jz);
                    EmitStatement(i.Alternate);
                    _b.PatchJump(jend);
                }
                else
                {
                    _b.PatchJump(jz);
                }
                return;
            case WhileStatement w:
                EmitWhile(w);
                return;
            case DoWhileStatement dw:
                EmitDoWhile(dw);
                return;
            case ForStatement f:
                EmitFor(f);
                return;
            case ForOfStatement fo:
                EmitForOf(fo);
                return;
            case ForInStatement fi:
                EmitForIn(fi);
                return;
            case BreakStatement bs:
                EmitBreakOrContinue(bs.Start, isBreak: true, label: bs.Label);
                return;
            case ContinueStatement cs:
                EmitBreakOrContinue(cs.Start, isBreak: false, label: cs.Label);
                return;
            case ReturnStatement r:
                if (r.Argument is null)
                {
                    _b.Emit(Opcode.ReturnUndefined);
                }
                else
                {
                    EmitExpression(r.Argument);
                    _b.Emit(Opcode.Return);
                }
                return;
            case ThrowStatement t:
                EmitExpression(t.Argument);
                RecordPos(t);
                _b.Emit(Opcode.Throw);
                return;
            case DebuggerStatement:
                _b.Emit(Opcode.Nop); // M3-05+ may emit a real debugger trap
                return;
            case FunctionDeclaration:
                // Hoisted at scope entry by HoistFunctionDeclarations;
                // textual position emits nothing.
                return;
            case ClassDeclaration cd:
                EmitClassDeclaration(cd);
                return;
            case TryStatement ts:
                EmitTry(ts);
                return;
            case SwitchStatement sw:
                EmitSwitch(sw, outerLabel: null);
                return;
            case LabeledStatement ls:
                EmitLabeledStatement(ls);
                return;
            case WithStatement ws:
                EmitWith(ws);
                return;
        }
        throw new NotSupportedException(
            $"compiler: statement kind '{s.GetType().Name}' not yet supported (see wp:M3-03 notes).");
    }

    /// <summary>
    /// §14.15 — emit <c>try</c>/<c>catch</c>/<c>finally</c>. Lowering relies on a per-VM
    /// try-frame stack: <see cref="Opcode.EnterTry"/> records both branch targets and
    /// the eval-stack height, the dispatch loop's outer C# <c>catch (JsThrow)</c>
    /// routes thrown values to the active frame's catch (or finally, if no handler),
    /// and <see cref="Opcode.LeaveTry"/> / <see cref="Opcode.EndFinally"/> drive the
    /// normal-completion / pending-completion flow.
    /// </summary>
    private void EmitTry(TryStatement ts)
    {
        var hasHandler = ts.Handler is not null;
        var hasFinalizer = ts.Finalizer is not null;
        // wp:M3-79 — §14.15: a try statement's completion is the (try-block, or
        // catch-block) value with §13.2.13 UpdateEmpty; a normally-completing
        // finally block's value is DISCARDED (`6; try { 7; } finally { 8; }` → 7).
        // Reset before the block so the try's own value overwrites the prior
        // statement's; the finally body's effect on the register is saved/restored
        // around it (see below).
        EmitCompletionReset();
        if (!hasHandler && !hasFinalizer)
        {
            EmitStatement(ts.Block);
            return;
        }

        _b.Emit(Opcode.EnterTry);
        var catchOperandPos = _b.Position;
        _b.EmitI32Raw(-1);
        var finallyOperandPos = _b.Position;
        _b.EmitI32Raw(-1);

        _tryDepth++;
        try
        {
            EmitTryBody(ts, catchOperandPos, finallyOperandPos);
        }
        finally
        {
            _tryDepth--;
        }
    }

    private void EmitTryBody(TryStatement ts, int catchOperandPos, int finallyOperandPos)
    {
        var hasHandler = ts.Handler is not null;
        var hasFinalizer = ts.Finalizer is not null;
        EmitStatement(ts.Block);
        _b.Emit(Opcode.LeaveTry);
        int jumpPastHandler = -1;
        if (hasHandler && !hasFinalizer)
        {
            jumpPastHandler = _b.EmitJump(Opcode.Jump);
        }

        if (hasHandler)
        {
            // Base is the byte after BOTH i32 operands (catch+finally = 8 bytes),
            // matching the VM, which reads both before computing ip + catchOff.
            var catchTargetDelta = _b.Position - (catchOperandPos + 8);
            _b.PatchI32(catchOperandPos, catchTargetDelta);

            PushScope();
            var handler = ts.Handler!;
            if (handler.Param is Identifier idParam)
            {
                var slot = _b.ReserveLocal();
                _scopes[^1][idParam.Name] = slot;
                // Catch bindings can be captured too.
                if (IsNameCaptured(idParam.Name))
                {
                    _b.MarkCaptured(slot);
                    _b.EmitSlot(Opcode.InitCellLocal, slot);
                    _b.EmitSlot(Opcode.StoreCellLocal, slot);
                }
                else
                {
                    _b.EmitSlot(Opcode.DeclareLocal, slot);
                    _b.EmitSlot(Opcode.StoreLocal, slot);
                }
            }
            else if (handler.Param is null)
            {
                _b.Emit(Opcode.Pop);
            }
            else
            {
                var srcSlot = _b.ReserveLocal();
                _b.EmitSlot(Opcode.StoreLocal, srcSlot);
                DeclarePatternBindings(handler.Param);
                EmitPatternFromLocal(handler.Param, srcSlot, isDeclaration: true);
            }
            // The catch block is its own lexical scope: TDZ-hoist its top-level
            // let/const into the scope opened above (the same pass the
            // BlockStatement case runs) before emitting the body, so a
            // `const`/`let` at the top of the catch resolves to a slot instead
            // of throwing "missing declared lexical".
            HoistLexicalDeclarations(handler.Body.Body);
            foreach (var inner in handler.Body.Body)
            {
                EmitStatement(inner);
            }

            PopScope();
            _b.Emit(Opcode.LeaveTry);
        }

        if (hasFinalizer)
        {
            var finallyTargetDelta = _b.Position - (finallyOperandPos + 4);
            _b.PatchI32(finallyOperandPos, finallyTargetDelta);

            // wp:M3-79 — a normally-completing finally block does NOT contribute
            // its value to the try statement's completion (§14.15.3 step 9 keeps
            // the saved (try/catch) result). When tracking completion, snapshot
            // the register before the finalizer body and restore it after, so the
            // finalizer's own ExpressionStatements don't clobber the try value. An
            // ABRUPT finalizer (e.g. `finally { break; }`) jumps out before the
            // restore, leaving the register at its finalizer-entry value, which is
            // what UpdateEmpty(abrupt-with-empty-value, V) preserves.
            if (_completionSlot is int cvSlot)
            {
                var saved = _b.ReserveLocal();
                _b.EmitSlot(Opcode.LoadLocal, cvSlot);
                _b.EmitSlot(Opcode.StoreLocal, saved);
                EmitStatement(ts.Finalizer!);
                _b.EmitSlot(Opcode.LoadLocal, saved);
                _b.EmitSlot(Opcode.StoreLocal, cvSlot);
            }
            else
            {
                EmitStatement(ts.Finalizer!);
            }
            _b.Emit(Opcode.EndFinally);
        }

        if (jumpPastHandler >= 0)
        {
            _b.PatchJump(jumpPastHandler);
        }
    }

    /// <summary>§14.11 — lower a <c>with</c> statement. Evaluate the object,
    /// install an object Environment Record (<see cref="Opcode.PushWith"/>),
    /// then run the body with the with-aware identifier path active. The body
    /// is wrapped in an implicit <c>finally { PopWith }</c> so the environment
    /// record is removed on every completion (normal, throw, break, continue,
    /// return) — reusing the existing try-frame finalizer machinery.</summary>
    private void EmitWith(WithStatement ws)
    {
        // §14.11.2 step 1–4: evaluate the head OUTSIDE the protected region so a
        // throw during evaluation / ToObject doesn't run the PopWith finalizer
        // (nothing has been pushed yet).
        // wp:M3-79 — §14.11.2: a with statement's completion is
        // UpdateEmpty(body, undefined), so its own value overwrites the prior
        // statement's (`1; with ({}) {}` → undefined). Reset before the body; the
        // body's ExpressionStatements then overwrite it. The synthetic
        // finally below only runs PopWith and never touches the register.
        EmitCompletionReset();
        EmitExpression(ws.Object);
        _b.Emit(Opcode.PushWith);

        // Synthetic try { body } finally { PopWith }.
        _b.Emit(Opcode.EnterTry);
        var catchOperandPos = _b.Position;
        _b.EmitI32Raw(-1);                  // no catch handler
        var finallyOperandPos = _b.Position;
        _b.EmitI32Raw(-1);

        _tryDepth++;
        _withDepth++;
        try
        {
            EmitStatement(ws.Body);
        }
        finally
        {
            _withDepth--;
            _tryDepth--;
        }
        _b.Emit(Opcode.LeaveTry);

        var finallyTargetDelta = _b.Position - (finallyOperandPos + 4);
        _b.PatchI32(finallyOperandPos, finallyTargetDelta);

        _b.Emit(Opcode.PopWith);
        _b.Emit(Opcode.EndFinally);
    }

    /// <summary>wp:M3-03e — emit a labeled statement. Strips the label and
    /// passes it to the inner statement so that a labeled switch or loop can
    /// register the label on its <see cref="LoopFrame"/>. After the inner
    /// statement finishes, any break patches targeting this label (if the inner
    /// statement was NOT a loop/switch that already consumed the label) are
    /// patched to the current PC.</summary>
    private void EmitLabeledStatement(LabeledStatement ls)
    {
        // If the inner statement is a loop or switch, let that emitter register
        // the label on the frame it pushes, so `break label` / `continue label`
        // can find it. Otherwise (e.g. `label: { block }`) we create a
        // synthetic switch-like break-only frame.
        switch (ls.Body)
        {
            case WhileStatement w:
                EmitWhile(w, ls.Label);
                return;
            case DoWhileStatement dw:
                EmitDoWhile(dw, ls.Label);
                return;
            case ForStatement f:
                EmitFor(f, ls.Label);
                return;
            case ForOfStatement fo:
                EmitForOf(fo, ls.Label);
                return;
            case ForInStatement fi:
                EmitForIn(fi, ls.Label);
                return;
            case SwitchStatement sw:
                EmitSwitch(sw, ls.Label);
                return;
            default:
                // Generic labeled block: push a break-only frame so that
                // `break label` inside the body can exit to past this statement.
                var frame = new LoopFrame { TryDepthAtEntry = _tryDepth, ContinueTryDepth = _tryDepth, IsSwitch = true, Label = ls.Label };
                _loops.Push(frame);
                EmitStatement(ls.Body);
                foreach (var p in frame.BreakPatches)
                {
                    _b.PatchJump(p);
                }

                _loops.Pop();
                return;
        }
    }

    /// <summary>wp:M3-03e — §14.12 SwitchStatement lowering.
    ///
    /// <para>Algorithm (correct per spec):</para>
    /// <list type="number">
    ///   <item>Evaluate the discriminant once into a temp local slot.</item>
    ///   <item>For each clause (in source order) that has a Test expression,
    ///     load the temp, evaluate the test, emit <see cref="Opcode.StrictEq"/>,
    ///     and jump-if-true to that clause's body label.</item>
    ///   <item>After all tests: jump to the default clause's body if one exists,
    ///     otherwise jump straight to the switch end.</item>
    ///   <item>Emit all clause bodies in source order (contiguous — fall-through
    ///     is free). Each body label is patched here. No implicit jump is emitted
    ///     between bodies; <c>break</c> inside a body was already converted to a
    ///     forward jump whose patch position is recorded on the <see cref="LoopFrame"/>.</item>
    ///   <item>Patch all break-jumps to the switch end.</item>
    /// </list>
    ///
    /// <para>One shared lexical scope is pushed for the whole switch body per
    /// §14.12.2 CaseBlock — <c>let</c>/<c>const</c> in any clause are scoped
    /// to the entire switch body.</para>
    /// </summary>
    private void EmitSwitch(SwitchStatement sw, string? outerLabel)
    {
        // §14.12.3 step 1: evaluate the discriminant once.
        EmitExpression(sw.Discriminant);
        var discSlot = _b.ReserveLocal();
        _b.EmitSlot(Opcode.StoreLocal, discSlot);

        // Open a single shared lexical scope for the entire switch body
        // (§14.12.2 CaseBlock — one LexicalEnvironment for all clauses).
        PushScope();

        // Push the loop frame (IsSwitch=true so that bare `continue` skips it).
        var frame = new LoopFrame { TryDepthAtEntry = _tryDepth, ContinueTryDepth = _tryDepth, IsSwitch = true, Label = outerLabel };
        _loops.Push(frame);

        // wp:M3-79 — §14.12.4 CaseBlockEvaluation "Let V = undefined" once; the
        // matched clause (and any fall-through clauses') ExpressionStatements
        // update it via UpdateEmpty, a no-match / empty-body switch yields
        // undefined. Reset before the comparison chain (which doesn't touch V).
        EmitCompletionReset();

        var allConsequent = sw.Cases.SelectMany(c => c.Consequent).ToList();
        // TDZ — §14.12.2: let/const declared in ANY clause are instantiated in
        // the uninitialized state across the whole switch's shared lexical
        // scope, so a read in an earlier clause (before the textual declaration
        // in a later clause) throws ReferenceError.
        HoistLexicalDeclarations(allConsequent);
        // Hoist any function declarations visible at the top of the switch body.
        HoistFunctionDeclarations(allConsequent);

        // Locate the default clause index (if any).
        int defaultIdx = -1;
        for (var i = 0; i < sw.Cases.Count; i++)
        {
            if (sw.Cases[i].Test is null) { defaultIdx = i; break; }
        }

        // -----------------------------------------------------------------------
        // Pass 1: emit the comparison chain.
        // For each non-default clause, load discriminant, evaluate Test,
        // strict-equal, jump-if-true to a placeholder; record the patch.
        // -----------------------------------------------------------------------
        var bodyPatchPositions = new int[sw.Cases.Count]; // patch-or-no-op
        for (var i = 0; i < sw.Cases.Count; i++)
        {
            var c = sw.Cases[i];
            if (c.Test is null) { bodyPatchPositions[i] = -1; continue; } // default — patched later

            // Load discriminant from temp slot, push test value, strict-equal.
            _b.EmitSlot(Opcode.LoadLocal, discSlot);
            EmitExpression(c.Test);
            _b.Emit(Opcode.StrictEq);
            bodyPatchPositions[i] = _b.EmitJump(Opcode.JumpIfTrue);
        }

        // After all tests failed: jump to default body (or switch end).
        int jDefault;
        if (defaultIdx >= 0)
        {
            jDefault = _b.EmitJump(Opcode.Jump); // will be patched to default body start
        }
        else
        {
            jDefault = _b.EmitJump(Opcode.Jump); // will be patched to switch end
        }

        // -----------------------------------------------------------------------
        // Pass 2: emit clause bodies in source order.
        // -----------------------------------------------------------------------
        for (var i = 0; i < sw.Cases.Count; i++)
        {
            var bodyStart = _b.Position;

            // Patch the comparison jump for this clause (if it has a Test).
            if (bodyPatchPositions[i] >= 0)
            {
                _b.PatchJump(bodyPatchPositions[i]);
            }

            // Patch the default-jump when we reach the default clause.
            if (i == defaultIdx)
            {
                _b.PatchJump(jDefault);
            }

            foreach (var stmt in sw.Cases[i].Consequent)
            {
                EmitStatement(stmt);
            }

            _ = bodyStart; // suppress unused-variable warning
        }

        // If jDefault was never patched as a body-start (no cases follow it,
        // or there is no default), it must point past all bodies — which is
        // exactly where we are now. But if defaultIdx >= 0, jDefault was already
        // patched above in the loop. If defaultIdx < 0, we need to patch it now.
        if (defaultIdx < 0)
        {
            _b.PatchJump(jDefault);
        }

        // Patch all break jumps to the current position (switch end).
        foreach (var p in frame.BreakPatches)
        {
            _b.PatchJump(p);
        }

        _loops.Pop();
        PopScope();
    }

    private void EmitVarDecl(VariableDeclaration vd)
    {
        // `var` is function-scoped (§14.3.2): its bindings live in the
        // function-variable scope, not the enclosing block, so a `var` declared
        // inside a block (and captured by a closure) shares the one binding.
        var functionScoped = vd.Kind == "var";
        // TDZ — `let`/`const` bindings (outside script top, which still routes
        // through the global object) were instantiated in the uninitialized
        // state at scope entry (HoistLexicalDeclarations / preallocate). Here
        // the declaration's initializer transitions the binding out of the TDZ,
        // so its store must be UNCHECKED (the slot legitimately still holds the
        // sentinel at this point). A bare `let x;` initializes to undefined.
        var lexical = (vd.Kind is "let" or "const") && !IsGlobalLexicalScope;
        // §13.15.2 — record const bindings so a post-initialization write (plain
        // `c = x` or compound `c op= x`) throws TypeError. The declarator's own
        // initializer below routes through EmitStoreLocalSlot / StoreBindingIdentifier
        // (with _inLexicalDeclInit), which never consult IsConstLocal, so this
        // marking does not block the one legal write.
        if (vd.Kind == "const" && lexical)
        {
            foreach (var d in vd.Declarations)
            {
                MarkConstNames(d.Id);
            }
        }

        foreach (var d in vd.Declarations)
        {
            if (lexical)
            {
                EmitLexicalDeclarator(d);
                continue;
            }
            // ECMA-262 §14.3.3 BindingPattern: declarations reserve all
            // binding names first, then initialize by walking the pattern.
            DeclarePatternBindings(d.Id, functionScoped);
            if (d.Init is not null)
            {
                if (d.Id is Identifier id)
                {
                    EmitNamedEvaluation(d.Init, id.Name);
                    // §14.11 — `var x = e` inside a `with` evaluates the
                    // initializer assignment in the running context, so the write
                    // consults the object Environment Record first (it may have an
                    // own `x` property), falling back to the hoisted var binding.
                    // A var hoisted to THIS function's scope is only shadowable by
                    // a `with` pushed in this function (ShouldRouteWith handles
                    // the captured-vs-local distinction).
                    if (ShouldRouteWith(id.Name))
                    {
                        EmitWithGuarded(Opcode.WithStoreOrMiss, id.Name, () =>
                        {
                            // wp:M3-73 — inject a top-level `var` initializer into
                            // the caller's var-environment (let/const keep their
                            // own script-top/global lexical binding).
                            if (_evalInjectVars && functionScoped)
                            {
                                EmitEvalInjectedStore(id.Name);
                            }
                            else if (IsScriptTop && !_directEvalLocalVars)
                            {
                                _b.EmitU16(Opcode.StoreGlobal, _b.AddConstant(id.Name));
                            }
                            else if (TryResolveLocal(id.Name, out var s))
                            {
                                EmitStoreLocalSlot(s);
                            }
                            else
                            {
                                _b.EmitU16(Opcode.StoreGlobal, _b.AddConstant(id.Name));
                            }
                        });
                    }
                    // wp:M3-73 — a non-strict direct eval whose caller is a
                    // function injects its top-level `var` initializer into the
                    // caller's var-environment (an existing caller binding's live
                    // storage, else the eval-introduced var store). let/const fall
                    // through to the global script-top lexical binding below.
                    else if (_evalInjectVars && functionScoped)
                    {
                        EmitEvalInjectedStore(id.Name);
                    }
                    // gap:script-top-var-not-global — at script top, the
                    // binding is a global property (not a local slot), so
                    // the initializer write routes through StoreGlobal.
                    else if (IsScriptTop && !_directEvalLocalVars)
                    {
                        _b.EmitU16(Opcode.StoreGlobal, _b.AddConstant(id.Name));
                    }
                    else
                    {
                        if (!TryResolveLocal(id.Name, out var slot))
                        {
                            throw new InvalidOperationException($"missing declared local '{id.Name}'");
                        }

                        EmitStoreLocalSlot(slot);
                    }
                }
                else
                {
                    var srcSlot = _b.ReserveLocal();
                    EmitExpression(d.Init);
                    _b.EmitSlot(Opcode.StoreLocal, srcSlot);
                    EmitPatternFromLocal(d.Id, srcSlot, isDeclaration: true);
                }
            }
        }
    }

    /// <summary>TDZ — emit the initializer for one <c>let</c>/<c>const</c>
    /// declarator whose binding(s) were already instantiated (in the TDZ) at
    /// scope entry. The store is unchecked because this IS the initialization;
    /// a bare <c>let x;</c> stores <c>undefined</c>. For a binding pattern, the
    /// per-leaf stores route through <see cref="StoreBindingIdentifier"/>,
    /// which emits the unchecked store for an in-scope lexical binding when
    /// invoked during a declaration (<see cref="_inLexicalDeclInit"/>).</summary>
    private void EmitLexicalDeclarator(VariableDeclarator d)
    {
        if (d.Id is Identifier id)
        {
            if (d.Init is not null)
            {
                EmitNamedEvaluation(d.Init, id.Name);
            }
            else
            {
                _b.Emit(Opcode.LoadUndefined);
            }

            if (!TryResolveLocal(id.Name, out var slot))
            {
                throw new InvalidOperationException($"missing declared lexical '{id.Name}'");
            }

            EmitStoreLocalSlot(slot); // unchecked — this is the initializer
            return;
        }
        // Destructuring let/const: evaluate the init into a temp, then bind
        // each leaf. Leaf stores happen inside a declaration initializer, so
        // they must be unchecked even though the leaf names are lexical.
        var srcSlot = _b.ReserveLocal();
        if (d.Init is not null)
        {
            EmitExpression(d.Init);
        }
        else
        {
            _b.Emit(Opcode.LoadUndefined);
        }

        _b.EmitSlot(Opcode.StoreLocal, srcSlot);
        var prev = _inLexicalDeclInit;
        _inLexicalDeclInit = true;
        EmitPatternFromLocal(d.Id, srcSlot, isDeclaration: true);
        _inLexicalDeclInit = prev;
    }

    /// <summary>§14.7.3 WhileStatement — emits a label-top / test / body /
    /// jump-back pattern, routing <c>break</c> and <c>continue</c> through
    /// the loop frame so nested jumps land at the right targets.</summary>
    private void EmitWhile(WhileStatement w, string? label = null)
    {
        // wp:M3-79 — §14.7.3: "Let V = undefined" once before the loop. Each
        // iteration's body value updates V (via the body's ExpressionStatements);
        // a zero-iteration loop yields undefined (`1; while (false) {}`), and on a
        // break/continue the running V is preserved (UpdateEmpty with the empty
        // break/continue value). Reset once here, before the loop entry.
        EmitCompletionReset();
        var loop = new LoopFrame { TryDepthAtEntry = _tryDepth, Label = label };
        _loops.Push(loop);
        var loopStart = _b.Position;
        EmitExpression(w.Test);
        var jzWhile = _b.EmitJump(Opcode.JumpIfFalse);
        EmitStatement(w.Body);
        // continue → loopStart.
        foreach (var p in loop.ContinuePatches)
        {
            PatchBackwardJump(p, loopStart);
        }

        var jBack = _b.EmitJump(Opcode.Jump);
        PatchBackwardJump(jBack, loopStart);
        _b.PatchJump(jzWhile);
        // break-target lands here.
        foreach (var p in loop.BreakPatches)
        {
            _b.PatchJump(p);
        }

        _loops.Pop();
    }

    /// <summary>§14.7.2 DoWhileStatement — body runs once before the test.
    /// <c>continue</c> jumps to the test; <c>break</c> jumps past the loop.</summary>
    private void EmitDoWhile(DoWhileStatement dw, string? label = null)
    {
        // wp:M3-79 — §14.7.2: "Let V = undefined" once before the loop (see
        // EmitWhile). `1; do {} while (false)` → undefined; `2; do { 3; } …` → 3.
        EmitCompletionReset();
        var loop = new LoopFrame { TryDepthAtEntry = _tryDepth, Label = label };
        _loops.Push(loop);
        var loopStart = _b.Position;
        EmitStatement(dw.Body);
        // continue → test.
        var testPos = _b.Position;
        foreach (var p in loop.ContinuePatches)
        {
            PatchBackwardJump(p, testPos);
        }

        EmitExpression(dw.Test);
        var jBack = _b.EmitJump(Opcode.JumpIfTrue);
        PatchBackwardJump(jBack, loopStart);
        // break-target.
        foreach (var p in loop.BreakPatches)
        {
            _b.PatchJump(p);
        }

        _loops.Pop();
    }

    /// <summary>§14.7.4 ForStatement — C-style <c>for (init; test; update) body</c>.
    /// <c>continue</c> jumps to the update; <c>break</c> jumps past the loop.
    /// Init may be a VariableDeclaration (var/let/const), an Expression
    /// (evaluated for side effects), or null. Test and update are both
    /// optional; an absent test is treated as truthy.</summary>
    /// <remarks>
    /// Per §14.7.4.4 ForBodyEvaluation, <c>for (let|const i = ...; ...; ...)</c>
    /// creates a fresh binding per iteration (CreatePerIterationEnvironment).
    /// The compiler allocates a Cell-backed local slot for each let/const
    /// binding declared in the init, emits the init normally, and then
    /// emits <see cref="Opcode.RefreshLetBinding"/> at the top of every
    /// iteration (after init, before test) so closures formed inside the
    /// iteration body capture the iteration's own cell. <c>for (var i = ...)</c>
    /// is unchanged: a single binding for the whole loop.
    /// </remarks>
    private void EmitFor(ForStatement f, string? label = null)
    {
        PushScope();

        // Per-iteration let/const bindings: route the init through a
        // Cell-backed local slot, then emit RefreshLetBinding at loopStart.
        List<int>? perIterSlots = null;
        if (f.Init is VariableDeclaration vdLet && (vdLet.Kind == "let" || vdLet.Kind == "const"))
        {
            perIterSlots = EmitForLetInit(vdLet);
        }
        else
        {
            switch (f.Init)
            {
                case null: break;
                case VariableDeclaration vd: EmitVarDecl(vd); break;
                case ExpressionStatement es:
                    EmitExpression(es.Expression);
                    _b.Emit(Opcode.Pop);
                    break;
                case Expression ie:
                    EmitExpression(ie);
                    _b.Emit(Opcode.Pop);
                    break;
                default:
                    throw new NotSupportedException(
                        $"for-loop init '{f.Init.GetType().Name}' not supported");
            }
        }

        // §14.7.4.4 step 2 — refresh once after init, before the first test.
        // The init's value is copied into a fresh cell so closures formed
        // in iteration 1's body see an iteration-local binding.
        if (perIterSlots is not null)
        {
            foreach (var slot in perIterSlots)
            {
                _b.EmitSlot(Opcode.RefreshLetBinding, slot);
            }
        }

        // wp:M3-79 — §14.7.4.4 ForBodyEvaluation step 1 "Let V = undefined" — after
        // init / the first per-iteration refresh, before the first test. A
        // zero-iteration for yields undefined; otherwise the last non-empty body
        // value (`for (var i=0;i<3;i++) i` → 2).
        EmitCompletionReset();

        var loop = new LoopFrame { TryDepthAtEntry = _tryDepth, Label = label };
        _loops.Push(loop);

        var loopStart = _b.Position;
        int jExit = -1;
        if (f.Test is not null)
        {
            EmitExpression(f.Test);
            jExit = _b.EmitJump(Opcode.JumpIfFalse);
        }
        EmitStatement(f.Body);
        // continue → update site (which also re-refreshes the bindings, so a
        // `continue` from inside the body still gets per-iteration semantics
        // for the next round).
        var updatePos = _b.Position;
        foreach (var p in loop.ContinuePatches)
        {
            PatchBackwardJump(p, updatePos);
        }
        // §14.7.4.4 step 3c — refresh after body, before update. The current
        // iteration's mutations land in this iteration's cell (captured by
        // any closures formed in the body); a fresh cell is allocated for
        // the next iteration with the post-body value, and the update
        // operates on that fresh cell.
        if (perIterSlots is not null)
        {
            foreach (var slot in perIterSlots)
            {
                _b.EmitSlot(Opcode.RefreshLetBinding, slot);
            }
        }
        if (f.Update is not null)
        {
            EmitExpression(f.Update);
            _b.Emit(Opcode.Pop);
        }
        var jBack = _b.EmitJump(Opcode.Jump);
        PatchBackwardJump(jBack, loopStart);
        if (jExit >= 0)
        {
            _b.PatchJump(jExit);
        }
        // break-target.
        foreach (var p in loop.BreakPatches)
        {
            _b.PatchJump(p);
        }

        _loops.Pop();
        PopScope();
    }

    /// <summary>Allocate Cell-backed local slots for each identifier declared
    /// in a <c>for (let|const ... ; ... ; ...)</c> init, emit the init
    /// expressions, and return the slot list so the caller can emit
    /// <see cref="Opcode.RefreshLetBinding"/> at the top of each iteration.
    /// Unlike <see cref="EmitVarDecl"/>, this path bypasses the
    /// script-top-as-global routing: spec requires <c>let</c> in a for-loop
    /// init to be lexically scoped to the loop, never a global. Each
    /// binding's slot is unconditionally marked captured (cell-backed) so
    /// the refresh opcode can swap it per iteration without checking.
    /// Destructuring patterns in the init currently fall back to the regular
    /// path (no per-iteration semantics for the destructured names — rare
    /// in practice, tracked as a follow-up).</summary>
    private List<int> EmitForLetInit(VariableDeclaration vd)
    {
        var slots = new List<int>(vd.Declarations.Count);
        foreach (var d in vd.Declarations)
        {
            if (d.Id is not Identifier id)
            {
                // Destructuring init: route through the regular pattern path
                // and skip per-iteration semantics for now.
                DeclarePatternBindings(d.Id);
                if (d.Init is not null)
                {
                    var srcSlot = _b.ReserveLocal();
                    EmitExpression(d.Init);
                    _b.EmitSlot(Opcode.StoreLocal, srcSlot);
                    EmitPatternFromLocal(d.Id, srcSlot, isDeclaration: true);
                }
                continue;
            }

            // Allocate a Cell-backed slot in the for-loop's scope. Even at
            // script-top the binding is loop-local (not a global), per spec.
            var slot = _b.ReserveLocal();
            _scopes[^1][id.Name] = slot;
            _b.MarkCaptured(slot);
            _b.EmitSlot(Opcode.InitCellLocal, slot);
            slots.Add(slot);

            if (d.Init is not null)
            {
                EmitExpression(d.Init);
                _b.EmitSlot(Opcode.StoreCellLocal, slot);
            }
        }
        return slots;
    }

    /// <summary>Per-iteration let/const binding for for-in / for-of: allocate
    /// a Cell-backed slot for each identifier, declare it in the loop's
    /// scope (overriding any preallocation from <see cref="DeclarePatternBindings"/>),
    /// and return the slot list. Identifier-only LHS — destructuring on the
    /// for-of LHS is bound by <see cref="EmitForOfBinding"/> directly into
    /// these slots via the regular pattern path.</summary>
    private List<int> ReserveForOfLetSlots(VariableDeclaration vd)
    {
        var slots = new List<int>(vd.Declarations.Count);
        foreach (var d in vd.Declarations)
        {
            if (d.Id is not Identifier id)
            {
                continue;
            }

            var slot = _b.ReserveLocal();
            _scopes[^1][id.Name] = slot;
            _b.MarkCaptured(slot);
            _b.EmitSlot(Opcode.InitCellLocal, slot);
            slots.Add(slot);
        }
        return slots;
    }

    /// <summary>
    /// §14.7.5 ForIn/OfBodyEvaluation — desugared to the spec iterator loop:
    /// <code>
    ///   handle = GetIterator(rhs)
    ///   loop:
    ///     step = IteratorStep(handle)   // pushed: iterator-result or undefined
    ///     if step === undefined goto done
    ///     value = step.value
    ///     &lt;bind value to LHS pattern/identifier&gt;
    ///     &lt;body&gt;
    ///     goto loop
    ///   done:
    ///     pop handle
    ///     IteratorClose(handle)         // no-op when record.Done is true
    /// </code>
    /// <c>break</c> jumps to the cleanup path so <c>IteratorClose</c> still
    /// fires (invoking the iterator's <c>return()</c> if defined, per
    /// §7.4.10). <c>continue</c> jumps to the next IteratorStep.
    /// </summary>
    private void EmitForOf(ForOfStatement fo, string? label = null)
    {
        // Open a fresh scope so loop-var bindings don't leak.
        PushScope();

        // Step 1: evaluate the iterable + materialise an iterator-record handle.
        // wp:M3-04g — `for await` resolves an async iterator; the per-iteration
        // result objects are obtained by awaiting iterator.next().
        EmitExpression(fo.Right);
        _b.Emit(fo.Await ? Opcode.GetAsyncIterator : Opcode.GetIterator);
        var handleSlot = _b.ReserveLocal();
        _b.EmitSlot(Opcode.StoreLocal, handleSlot);

        // Pre-declare any identifiers introduced by the LHS so the body sees
        // them as locals. For VariableDeclaration we declare each binding;
        // for a bare AssignmentTarget we leave the resolver to fall through
        // to the parent scope. Per §14.7.4.4, let/const bindings get per-
        // iteration semantics: allocate Cell-backed slots and refresh each
        // iteration so closures in the body capture an iteration-local cell.
        List<int>? perIterSlots = null;
        if (fo.Left is VariableDeclaration vd0)
        {
            if (vd0.Kind == "let" || vd0.Kind == "const")
            {
                perIterSlots = ReserveForOfLetSlots(vd0);
                // Destructuring patterns and any non-identifier LHS still
                // need the regular declare path; ReserveForOfLetSlots skips
                // those, so call DeclarePatternBindings as a fallback.
                foreach (var d in vd0.Declarations)
                {
                    if (d.Id is not Identifier)
                    {
                        DeclarePatternBindings(d.Id);
                    }
                }
            }
            else
            {
                // `var` loop-head bindings are function-scoped and were
                // already hoisted. Declaring them again in this synthetic loop
                // scope would shadow the hoisted binding, so assignment from
                // the pattern must resolve through StoreBindingIdentifier.
            }
        }

        if (fo.Await)
        {
            EmitForOfAwait(fo, handleSlot, perIterSlots, label);
            PopScope();
            return;
        }

        // §14.7.5.6 ForIn/OfBodyEvaluation — each iteration's body (the LHS
        // binding + the statement) runs inside a synthetic
        //   try { <bind value>; <body> } finally { IteratorClose-if-abrupt }
        // so that EVERY abrupt completion out of the body — break (to this loop
        // or an outer one), `continue label` to an outer loop, `return`,
        // `throw`, or an error thrown while binding the LHS — runs the
        // iterator's `return()` exactly once before control leaves. A plain
        // `continue` to THIS loop and a normal body completion are NOT abrupt:
        // they re-step the iterator without closing it. The finalizer
        // distinguishes the two via the try-frame's pending completion
        // (IteratorCloseFinally skips Normal). break/continue/return/throw
        // crossing the synthetic finally are handled by the existing
        // BranchThroughFinally / DivertReturnThroughFinally machinery.
        //
        // The loop frame's break uses the OUTER try-depth (so a break crosses
        // the synthetic finally → closes); continue uses the INNER depth (so a
        // continue stays inside the protected region and only re-steps).
        var loop = new LoopFrame
        {
            TryDepthAtEntry = _tryDepth,
            ContinueTryDepth = _tryDepth + 1,
            Label = label,
        };
        _loops.Push(loop);

        // wp:M3-79 — §14.7.5.6 ForIn/OfBodyEvaluation step 2 "Let V = undefined"
        // once before iterating; each iteration's body value updates it, a
        // zero-iteration loop yields undefined.
        EmitCompletionReset();

        var loopStart = _b.Position;
        // step = IteratorStep(handle); if done goto jExit. Done OUTSIDE the try
        // region: a normal exhaustion leaves the record Done and must NOT run
        // the iterator-close finalizer.
        _b.EmitSlot(Opcode.LoadLocal, handleSlot);
        _b.Emit(Opcode.IteratorStep);
        _b.Emit(Opcode.Dup);
        _b.Emit(Opcode.LoadUndefined);
        _b.Emit(Opcode.StrictEq);
        var jExit = _b.EmitJump(Opcode.JumpIfTrue);

        // §14.7.5.6 step 5.c — IteratorValue(nextResult). This runs OUTSIDE the
        // protected region: per §7.4.8 an error while reading `.value` does NOT
        // close the iterator (the abrupt next()/value result is treated as
        // already closing it), so the finalizer must not fire for it. Stash the
        // value in a temp so the protected region starts/ends stack-balanced.
        _b.EmitProperty(Opcode.LoadProperty, _b.AddConstant("value"));
        var valueSlot = _b.ReserveLocal();
        _b.EmitSlot(Opcode.StoreLocal, valueSlot);

        // ---- per-iteration protected region (binding + body) ----
        _b.Emit(Opcode.EnterTry);
        _b.EmitI32Raw(-1);                  // no catch handler
        var finallyOperandPos = _b.Position;
        _b.EmitI32Raw(-1);
        _tryDepth++;
        try
        {
            // CreatePerIterationEnvironment — refresh let/const bindings before
            // the iteration's value is stored into them.
            if (perIterSlots is not null)
            {
                foreach (var slot in perIterSlots)
                {
                    _b.EmitSlot(Opcode.RefreshLetBinding, slot);
                }
            }
            // Bind the stepped value to LHS. A throw here (LHS reference /
            // destructuring / PutValue error) propagates to the finally, which
            // closes the iterator (§14.7.5.6 step 5.i / body-put / body-dstr).
            _b.EmitSlot(Opcode.LoadLocal, valueSlot);
            EmitForOfBinding(fo.Left);
            EmitStatement(fo.Body);
        }
        finally
        {
            _tryDepth--;
        }
        // continue / normal body finish → LeaveTry, which routes through the
        // finalizer with a Normal pending completion (IteratorCloseFinally
        // skips the close). After EndFinally the Normal completion FALLS
        // THROUGH to the `goto loopStart` emitted just past the finalizer, so
        // the iterator re-steps. Abrupt completions (break/continue-label/
        // return/throw) instead re-route at EndFinally and never reach that
        // jump.
        var continueTarget = _b.Position;
        foreach (var p in loop.ContinuePatches)
        {
            PatchBackwardJump(p, continueTarget);
        }

        _b.Emit(Opcode.LeaveTry);

        // finalizer: close the iterator only when leaving abruptly.
        var finallyTargetDelta = _b.Position - (finallyOperandPos + 4);
        _b.PatchI32(finallyOperandPos, finallyTargetDelta);
        _b.EmitSlot(Opcode.LoadLocal, handleSlot);
        _b.Emit(Opcode.IteratorCloseFinally);
        _b.Emit(Opcode.EndFinally);

        // Normal completion resumes here (after EndFinally falls through):
        // re-step the iterator.
        var jBack = _b.EmitJump(Opcode.Jump);
        PatchBackwardJump(jBack, loopStart);

        // done: normal exhaustion. Pop the undefined sentinel left by
        // IteratorStep; the record is already Done, so no close is needed.
        _b.PatchJump(jExit);
        _b.Emit(Opcode.Pop);
        // break-target: a break to THIS loop crosses the synthetic finally
        // (BranchThroughFinally), runs the close, then lands here.
        foreach (var p in loop.BreakPatches)
        {
            _b.PatchJump(p);
        }

        _loops.Pop();
        PopScope();
    }

    /// <summary>wp:M3-04g — the <c>for await … of</c> form. Kept on the
    /// pre-existing IteratorStep/await structure (the synchronous synthetic
    /// try/finally would tangle with Suspend/await); abrupt-completion
    /// IteratorClose for the async loop is the prior behavior.</summary>
    private void EmitForOfAwait(ForOfStatement fo, int handleSlot, List<int>? perIterSlots, string? label)
    {
        var loop = new LoopFrame { TryDepthAtEntry = _tryDepth, Label = label };
        _loops.Push(loop);

        // wp:M3-79 — §14.7.5.6 "Let V = undefined" once before iterating.
        EmitCompletionReset();

        var loopStart = _b.Position;
        // step = await iterator.next(); if step.done goto done.
        _b.EmitSlot(Opcode.LoadLocal, handleSlot);
        _b.Emit(Opcode.AsyncIteratorNext);   // push the next()-promise
        _b.Emit(Opcode.Suspend);
        _b.EmitU8Raw(1);                     // await → result object on top
        _b.Emit(Opcode.Dup);
        _b.EmitProperty(Opcode.LoadProperty, _b.AddConstant("done"));
        var jExit = _b.EmitJump(Opcode.JumpIfTrue);

        if (perIterSlots is not null)
        {
            foreach (var slot in perIterSlots)
            {
                _b.EmitSlot(Opcode.RefreshLetBinding, slot);
            }
        }

        _b.EmitProperty(Opcode.LoadProperty, _b.AddConstant("value"));
        EmitForOfBinding(fo.Left);
        EmitStatement(fo.Body);
        foreach (var p in loop.ContinuePatches)
        {
            PatchBackwardJump(p, loopStart);
        }

        var jBack = _b.EmitJump(Opcode.Jump);
        PatchBackwardJump(jBack, loopStart);

        foreach (var p in loop.BreakPatches)
        {
            _b.PatchJump(p);
        }

        EmitForOfClose(handleSlot, isAwait: true);
        var jPastNormal = _b.EmitJump(Opcode.Jump);

        _b.PatchJump(jExit);
        _b.Emit(Opcode.Pop);

        _b.PatchJump(jPastNormal);
        _loops.Pop();
    }

    /// <summary>wp:M3-04g — emit the IteratorClose for a for-of cleanup path.
    /// Async loops await the close (AsyncIteratorClose, §7.4.11) and discard
    /// the awaited result.</summary>
    private void EmitForOfClose(int handleSlot, bool isAwait)
    {
        if (isAwait)
        {
            _b.EmitSlot(Opcode.LoadLocal, handleSlot);
            _b.Emit(Opcode.AsyncIteratorClose); // push return()-result (or undefined)
            _b.Emit(Opcode.Suspend);
            _b.EmitU8Raw(1);                    // await the close
            _b.Emit(Opcode.Pop);                // discard the awaited result
        }
        else
        {
            _b.EmitSlot(Opcode.LoadLocal, handleSlot);
            _b.Emit(Opcode.IteratorClose);
        }
    }

    /// <summary>§14.7.5 ForIn — iterate enumerable string keys of the
    /// right-hand side (own + inherited, dedup'd). The key set is snapshotted
    /// at loop entry per spec, so mutations during iteration don't appear.</summary>
    private void EmitForIn(ForInStatement fi, string? label = null)
    {
        PushScope();

        // Materialize the key snapshot. EnumerateKeys handles null/undefined
        // by yielding an empty array (spec: silently skip the loop body).
        // Keep the (coerced) source object too: §14.7.5.9 requires a key that
        // has been DELETED before it is reached to be skipped, so each step
        // re-checks that the property still exists.
        EmitExpression(fi.Right);
        _b.Emit(Opcode.Dup);
        _b.Emit(Opcode.EnumerateKeys);
        var keysSlot = _b.ReserveLocal();
        _b.EmitSlot(Opcode.StoreLocal, keysSlot);
        // [src] still on the stack — coerce nullish to undefined-safe and stash.
        var srcSlot = _b.ReserveLocal();
        _b.EmitSlot(Opcode.StoreLocal, srcSlot);

        // Iteration counter.
        var iSlot = _b.ReserveLocal();
        _b.Emit(Opcode.LoadZero);
        _b.EmitSlot(Opcode.StoreLocal, iSlot);

        // Pre-declare LHS bindings. Per §14.7.4.4 CreatePerIterationEnvironment,
        // let/const bindings get a fresh slot per iteration.
        List<int>? perIterSlots = null;
        if (fi.Left is VariableDeclaration vd0)
        {
            if (vd0.Kind == "let" || vd0.Kind == "const")
            {
                perIterSlots = ReserveForOfLetSlots(vd0);
                foreach (var d in vd0.Declarations)
                {
                    if (d.Id is not Identifier)
                    {
                        DeclarePatternBindings(d.Id);
                    }
                }
            }
            else
            {
                // `var` loop-head bindings are function-scoped and were
                // already hoisted. Do not shadow them in the synthetic loop
                // scope used by for-in lowering.
            }
        }

        var loop = new LoopFrame { TryDepthAtEntry = _tryDepth, Label = label };
        _loops.Push(loop);

        // wp:M3-79 — §14.7.5.6 "Let V = undefined" once before iterating; each
        // visited iteration's body value updates it, a zero-iteration (or
        // all-skipped) loop yields undefined.
        EmitCompletionReset();

        var loopStart = _b.Position;
        // if (i >= keys.length) break.
        _b.EmitSlot(Opcode.LoadLocal, iSlot);
        _b.EmitSlot(Opcode.LoadLocal, keysSlot);
        _b.EmitProperty(Opcode.LoadProperty, _b.AddConstant("length"));
        _b.Emit(Opcode.GtEq);
        var jExit = _b.EmitJump(Opcode.JumpIfTrue);

        // §14.7.5.9 — skip a key that has been deleted (or otherwise no longer
        // exists) since the snapshot was taken: `if (!(key in src)) continue;`.
        // The snapshot's keys are strings (own + inherited enumerable), so a
        // HasProperty check correctly skips deleted own keys while still
        // visiting inherited ones.
        _b.EmitSlot(Opcode.LoadLocal, keysSlot);
        _b.EmitSlot(Opcode.LoadLocal, iSlot);
        _b.Emit(Opcode.LoadComputed);       // [key]
        _b.EmitSlot(Opcode.LoadLocal, srcSlot); // [key, src]
        _b.Emit(Opcode.In);                 // [bool] — HasProperty(src, key)
        var jVisit = _b.EmitJump(Opcode.JumpIfTrue);
        // deleted → skip to the increment.
        var jSkip = _b.EmitJump(Opcode.Jump);
        _b.PatchJump(jVisit);

        // CreatePerIterationEnvironment — refresh let/const bindings before
        // the iteration's key is stored into them.
        if (perIterSlots is not null)
        {
            foreach (var slot in perIterSlots)
            {
                _b.EmitSlot(Opcode.RefreshLetBinding, slot);
            }
        }

        // key = keys[i].
        _b.EmitSlot(Opcode.LoadLocal, keysSlot);
        _b.EmitSlot(Opcode.LoadLocal, iSlot);
        _b.Emit(Opcode.LoadComputed);
        // Bind to LHS.
        EmitForOfBinding(fi.Left);
        // Body.
        EmitStatement(fi.Body);
        // continue → increment.
        var incPos = _b.Position;
        _b.PatchJump(jSkip);
        foreach (var p in loop.ContinuePatches)
        {
            PatchBackwardJump(p, incPos);
        }
        // i++ (use load/add/store; the Inc-update path is not exposed here).
        _b.EmitSlot(Opcode.LoadLocal, iSlot);
        _b.EmitU16(Opcode.LoadConst, _b.AddConstant((double)1));
        _b.Emit(Opcode.Add);
        _b.EmitSlot(Opcode.StoreLocal, iSlot);
        var jBack = _b.EmitJump(Opcode.Jump);
        PatchBackwardJump(jBack, loopStart);

        _b.PatchJump(jExit);
        foreach (var p in loop.BreakPatches)
        {
            _b.PatchJump(p);
        }

        _loops.Pop();
        PopScope();
    }

    /// <summary>B7-followup-b / wp:M3-03e — emit a forward <see cref="Opcode.Jump"/> for
    /// <c>break</c> or <c>continue</c> and record the patch position on the
    /// appropriate open loop frame.
    ///
    /// <para>Rules:</para>
    /// <list type="bullet">
    ///   <item>Bare <c>break</c> — targets the innermost frame (loop or switch).</item>
    ///   <item>Bare <c>continue</c> — skips switch frames; targets the innermost
    ///     iteration (non-switch) frame. Mirrors spec §14.9.3 where <c>continue</c>
    ///     is only valid inside an IterationStatement, not inside a SwitchStatement.</item>
    ///   <item>Labeled <c>break label</c> — targets any frame (loop or switch) whose
    ///     <see cref="LoopFrame.Label"/> matches.</item>
    ///   <item>Labeled <c>continue label</c> — targets any non-switch frame whose
    ///     label matches.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// wp:M3-15 — the cross-finally case (a break/continue whose target loop
    /// sits outside one or more enclosing try/finally blocks) emits
    /// <see cref="Opcode.BranchThroughFinally"/> so the intervening finalizers
    /// run first (innermost out), mirroring the VM's
    /// DivertReturnThroughFinally path for <c>return</c>.
    /// </remarks>
    private void EmitBreakOrContinue(Starling.Js.Lex.JsPosition where, bool isBreak, string? label)
    {
        // §14.9 / §14.11 — an unmatched break/continue (no enclosing iteration
        // or switch, or an unresolved label) is an early SyntaxError, not a host
        // failure; surface it as a parse error.
        if (_loops.Count == 0)
        {
            throw new Parse.JsParseException(
                $"Illegal {(isBreak ? "break" : "continue")} statement (must be inside a loop or switch)", where);
        }

        LoopFrame? targetFrame = null;

        if (label is not null)
        {
            // Labeled break/continue: walk the stack to find a frame whose
            // Label matches. For continue, the matching frame must not be a
            // switch (§14.9.3 ContinueStatement runtime semantics).
            foreach (var f in _loops)
            {
                if (f.Label != label)
                {
                    continue;
                }

                if (!isBreak && f.IsSwitch)
                {
                    throw new Parse.JsParseException(
                        $"Illegal 'continue {label}' — label '{label}' is a switch, not an iteration statement", where);
                }

                targetFrame = f;
                break;
            }
            if (targetFrame is null)
            {
                throw new Parse.JsParseException(
                    $"Label '{label}' not found for '{(isBreak ? "break" : "continue")}' statement", where);
            }
        }
        else if (!isBreak)
        {
            // Bare continue: skip switch frames, find the nearest iteration frame.
            foreach (var f in _loops)
            {
                if (!f.IsSwitch) { targetFrame = f; break; }
            }
            if (targetFrame is null)
            {
                throw new Parse.JsParseException(
                    "Illegal 'continue' — not inside an iteration statement", where);
            }
        }
        else
        {
            // Bare break: innermost frame (loop or switch).
            targetFrame = _loops.Peek();
        }

        // A `continue` to a for-of/for-in targets the iteration body's interior
        // (inside the synthetic IteratorClose finally), so it uses the frame's
        // ContinueTryDepth; a `break` (and every outward completion) uses the
        // outer TryDepthAtEntry so it crosses that finally and closes.
        var targetTryDepth = isBreak
            ? targetFrame.TryDepthAtEntry
            : (targetFrame.ContinueTryDepth ?? targetFrame.TryDepthAtEntry);
        var crossedTryFrames = _tryDepth - targetTryDepth;
        if (crossedTryFrames > 0)
        {
            // wp:M3-15 — the break/continue exits the loop/switch across one or
            // more enclosing try-frames. Per §14.15 every intervening finalizer
            // must run (innermost first) before control reaches the loop's
            // break/continue site. Emit BranchThroughFinally so the VM diverts
            // the abrupt completion through those finalizers, then jumps to the
            // (forward-patched) target. The i16 target slot is recorded on the
            // same break/continue patch list as a plain Jump, so the loop's
            // lowering pass patches it identically.
            if (crossedTryFrames > byte.MaxValue)
            {
                throw new NotSupportedException(
                    $"'{(isBreak ? "break" : "continue")}' crosses too many try/finally frames ({crossedTryFrames}) (compiler at {where.Line}:{where.Column}).");
            }

            _b.Emit(Opcode.BranchThroughFinally);
            _b.EmitU8Raw(crossedTryFrames);
            var finPatch = _b.Position;
            _b.EmitI32Raw(0); // i32 target placeholder, patched by the loop pass.
            (isBreak ? targetFrame.BreakPatches : targetFrame.ContinuePatches).Add(finPatch);
            return;
        }

        var patch = _b.EmitJump(Opcode.Jump);
        (isBreak ? targetFrame.BreakPatches : targetFrame.ContinuePatches).Add(patch);
    }

    private void EmitForOfBinding(AstNode left)
    {
        // Stack top: [value]. Consume it into LHS.
        switch (left)
        {
            case VariableDeclaration vd:
                if (vd.Declarations.Count != 1)
                {
                    throw new NotSupportedException("for…of binding requires exactly one declarator");
                }

                var d = vd.Declarations[0];
                if (d.Id is Identifier id)
                {
                    StoreBindingIdentifier(id.Name);
                }
                else
                {
                    EmitPatternFromStack(d.Id, isDeclaration: true);
                }
                return;
            case Identifier id2:
                StoreBindingIdentifier(id2.Name);
                return;
            case MemberExpression me:
                StoreMemberTarget(me);
                return;
            case Expression pattern:
                EmitPatternFromStack(pattern, isDeclaration: false);
                return;
            default:
                throw new NotSupportedException($"for…of LHS '{left.GetType().Name}' not supported");
        }
    }

    private void PatchBackwardJump(int operandPos, int target)
    {
        // Offset measured from the byte after the i32 operand (matches the VM).
        var jumpFrom = operandPos + 4;
        var delta = target - jumpFrom;
        _b.PatchI32(operandPos, delta);
    }

    // -----------------------------------------------------------------------
    // Expressions
    // -----------------------------------------------------------------------

    private void EmitExpression(Expression e)
    {
        switch (e)
        {
            case NumericLiteral n:
                if (n.Value == 0)
                {
                    _b.Emit(Opcode.LoadZero);
                }
                else
                {
                    var idx = _b.AddConstant(n.Value);
                    _b.EmitU16(Opcode.LoadConst, idx);
                }
                return;
            case StringLiteral s:
                _b.EmitU16(Opcode.LoadConst, _b.AddConstant(s.Value));
                return;
            case BooleanLiteral b:
                _b.Emit(b.Value ? Opcode.LoadTrue : Opcode.LoadFalse);
                return;
            case NullLiteral:
                _b.Emit(Opcode.LoadNull);
                return;
            case BigIntLiteral bi:
                _b.EmitU16(Opcode.LoadConst, _b.AddConstant(new JsBigIntPlaceholder(bi.Value)));
                return;
            case RegExpLiteral rx:
                // Source + flags live as string constants; the VM compiles
                // and wraps a fresh JsRegExp per execution so each evaluation
                // gets an independent `lastIndex` slot (§13.2.7.3).
                _b.EmitU16(Opcode.LoadRegExp, _b.AddConstant(rx.Source));
                _b.EmitU16Raw(_b.AddConstant(rx.Flags));
                _b.EmitU16Raw(_b.AllocateRegexCache());
                return;
            case Identifier id:
                EmitIdLoad(id);
                return;
            case ThisExpression:
                // §10.2.1.1 — inside an arrow, `this` is the enclosing ordinary
                // function's binding, captured as the synthetic <this> upvalue.
                if (_b.IsArrow && TryResolveUpvalue(LexicalThisName, out var thisUp))
                {
                    _b.EmitUpvalue(Opcode.LoadUpvalue, thisUp);
                    return;
                }
                _b.Emit(_classMethodDepth > 0 ? Opcode.LoadThisChecked : Opcode.LoadThis);
                return;
            case BinaryExpression bin:
                EmitExpression(bin.Left);
                EmitExpression(bin.Right);
                _b.Emit(BinaryOpToOpcode(bin.Op));
                return;
            case LogicalExpression log:
                EmitLogical(log);
                return;
            case UnaryExpression u:
                if (u.Op == JsTokenKind.Delete)
                {
                    EmitDelete(u);
                    return;
                }
                if (u.Op == JsTokenKind.Void)
                {
                    EmitExpression(u.Argument);
                    _b.Emit(Opcode.Pop);
                    _b.Emit(Opcode.LoadUndefined);
                    return;
                }
                // §13.5.3 — `typeof` on a bare identifier that resolves to no
                // binding yields "undefined" instead of throwing, so its read
                // uses the silent (unchecked) global load. Any other operand
                // shape evaluates normally and throws where the spec requires.
                if (u.Op == JsTokenKind.Typeof && u.Argument is Identifier typeofId)
                {
                    EmitIdLoad(typeofId, checkedGlobal: false);
                    _b.Emit(Opcode.TypeOf);
                    return;
                }
                EmitExpression(u.Argument);
                _b.Emit(UnaryOpToOpcode(u.Op));
                return;
            case UpdateExpression up:
                EmitUpdate(up);
                return;
            case AssignmentExpression a:
                EmitAssignment(a);
                return;
            case ConditionalExpression c:
                EmitExpression(c.Test);
                var jzCond = _b.EmitJump(Opcode.JumpIfFalse);
                EmitExpression(c.Consequent);
                var jend = _b.EmitJump(Opcode.Jump);
                _b.PatchJump(jzCond);
                EmitExpression(c.Alternate);
                _b.PatchJump(jend);
                return;
            case MemberExpression m:
                EmitMemberLoad(m);
                return;
            case CallExpression call:
                EmitCall(call);
                return;
            case NewExpression ne:
                EmitNew(ne);
                return;
            case ObjectExpression oe:
                EmitObjectLiteral(oe);
                return;
            case ArrayExpression ae:
                EmitArrayLiteral(ae);
                return;
            case FunctionExpression fe:
                EmitFunctionExpression(fe);
                return;
            case SequenceExpression seq:
                for (var i = 0; i < seq.Expressions.Count - 1; i++)
                {
                    EmitExpression(seq.Expressions[i]);
                    _b.Emit(Opcode.Pop);
                }
                EmitExpression(seq.Expressions[^1]);
                return;
            case TemplateLiteral tpl:
                EmitTemplateLiteral(tpl);
                return;
            case TaggedTemplateExpression tte:
                EmitTaggedTemplate(tte);
                return;
            case ArrowFunctionExpression arrow:
                EmitArrowFunction(arrow);
                return;
            case PrivateInExpression pin:
                {
                    // §13.10 `#x in obj` — resolve the private name to its mangled
                    // slot key (validates it is declared in an enclosing class),
                    // evaluate the operand, then run the brand check.
                    var mangled = ResolvePrivateName(pin.Name, pin.Start);
                    EmitExpression(pin.Object);
                    _b.EmitU16(Opcode.PrivateIn, _b.AddConstant(mangled));
                    return;
                }
            case YieldExpression yld:
                EmitYield(yld);
                return;
            case AwaitExpression aw:
                EmitAwait(aw);
                return;
            case ClassExpression cls:
                EmitClassExpression(cls);
                return;
            case SuperPropertyExpression sp:
                EmitSuperProperty(sp);
                return;
            case SuperCallExpression sc:
                EmitSuperCall(sc);
                return;
            case ImportCallExpression ic:
                // wp:M3-03c — `import(spec)`: evaluate the specifier, drop the
                // (forward-compat, currently ignored) options arg, and let the
                // runtime hand the string-coerced specifier + referrer to the
                // loader, pushing the resulting Promise.
                EmitExpression(ic.Specifier);
                if (ic.Options is not null)
                {
                    // Evaluate options for side-effect order, then discard.
                    EmitExpression(ic.Options);
                    _b.Emit(Opcode.Pop);
                }
                _b.Emit(Opcode.DynamicImport);
                return;
            case ImportMetaExpression:
                // wp:M3-03c — `import.meta`: push the running module's meta object.
                _b.Emit(Opcode.LoadImportMeta);
                return;
            case NewTargetExpression:
                // §13.3.12 — `new.target`: push the active frame's [[NewTarget]].
                _b.Emit(Opcode.LoadNewTarget);
                return;
            case PrivateNameExpression:
                throw new NotSupportedException(
                    "private-name reference used outside a member-expression context");
        }
        throw new NotSupportedException(
            $"compiler: expression kind '{e.GetType().Name}' not yet supported.");
    }

    /// <summary>
    /// Desugar template literal to left-fold string concat:
    /// `q0${e0}q1${e1}q2` → q0 + e0 + q1 + e1 + q2 (with explicit ToString
    /// coercion of substitution values implicit in JS '+'). Empty quasi
    /// segments are skipped.
    /// </summary>
    private void EmitTemplateLiteral(TemplateLiteral tpl)
    {
        // Always start with the first quasi (which can be "") to anchor as a string —
        // ensures arithmetic '+' on subsequent operands becomes string concat.
        // Untagged templates never have a null cooked segment (the parser rejects
        // invalid escapes here); coalesce defensively to keep the type non-null.
        _b.EmitU16(Opcode.LoadConst, _b.AddConstant(tpl.Quasis[0] ?? ""));
        for (var i = 0; i < tpl.Expressions.Count; i++)
        {
            EmitExpression(tpl.Expressions[i]);
            _b.Emit(Opcode.Add);
            if ((tpl.Quasis[i + 1] ?? "").Length > 0)
            {
                _b.EmitU16(Opcode.LoadConst, _b.AddConstant(tpl.Quasis[i + 1]!));
                _b.Emit(Opcode.Add);
            }
        }
    }

    /// <summary>
    /// §13.3.11 Tagged template — <c>tag`q0${e0}q1`</c> evaluates to
    /// <c>tag(strings, e0, …)</c> where <c>strings</c> is the frozen, call-site
    /// cached template object (cooked array + <c>.raw</c>). The tag's <c>this</c>
    /// binding follows ordinary call rules: a member-expression tag binds
    /// <c>this</c> to its base, anything else binds <c>this = undefined</c>.
    /// </summary>
    private void EmitTaggedTemplate(TaggedTemplateExpression tte)
    {
        var quasi = tte.Quasi;
        var argCount = 1 + quasi.Expressions.Count; // strings object + substitutions
        if (argCount > 255)
        {
            throw new NotSupportedException("more than 255 tagged-template args not supported");
        }

        // Emit the tag callee, mirroring EmitCall's this-binding: obj.tag`…` and
        // obj[k]`…` leave [receiver, fn] for CallMethod; a bare tag leaves [fn]
        // for Call (this = undefined).
        var method = tte.Tag is MemberExpression;
        if (tte.Tag is MemberExpression me)
        {
            EmitExpression(me.Object);              // [obj]
            _b.Emit(Opcode.Dup);                    // [obj, obj]
            if (me.Computed)
            {
                EmitExpression(me.Property);        // [obj, obj, key]
                RecordPos(me);
                _b.Emit(Opcode.LoadComputed);       // [obj, fn]
            }
            else if (me.Property is PrivateNameExpression pne)
            {
                var mangled = ResolvePrivateName(pne.Name, pne.Start);
                RecordPos(me);
                _b.EmitU16(Opcode.PrivateGet, _b.AddConstant(mangled)); // [obj, fn]
            }
            else
            {
                var nameIdx = _b.AddConstant(((Identifier)me.Property).Name);
                RecordPos(me);
                _b.EmitProperty(Opcode.LoadProperty, nameIdx); // [obj, fn]
            }
        }
        else
        {
            EmitExpression(tte.Tag);                // [fn]
        }

        // arg 0 — the (cached, frozen) template strings object.
        var cooked = new string?[quasi.Quasis.Count];
        for (var i = 0; i < cooked.Length; i++)
        {
            cooked[i] = quasi.Quasis[i];
        }

        var raw = new string[quasi.RawQuasis.Count];
        for (var i = 0; i < raw.Length; i++)
        {
            raw[i] = quasi.RawQuasis[i];
        }

        _b.EmitU16(Opcode.TemplateObject, _b.AddConstant(new TemplateObjectTemplate(cooked, raw)));

        // args 1.. — the substitution values, in source order.
        foreach (var sub in quasi.Expressions)
        {
            EmitExpression(sub);
        }

        RecordPos(tte);
        _b.Emit(method ? Opcode.CallMethod : Opcode.Call, (byte)argCount);
    }

    /// <summary>
    /// Emit an arrow function body. Arrows compile as ordinary function chunks,
    /// then closure analysis captures lexical <c>this</c> and the compiler marks
    /// the chunk so the VM does not create its own <c>arguments</c> binding.
    /// </summary>
    private void EmitArrowFunction(ArrowFunctionExpression arrow)
    {
        BlockStatement body = arrow.Body switch
        {
            BlockStatement b => b,
            Expression expr => new BlockStatement(
                [new ReturnStatement(expr, arrow.Start, arrow.End)],
                arrow.Start, arrow.End),
            _ => throw new InvalidOperationException("arrow body must be block or expression"),
        };
        var fe = BuildFunctionExpressionShim(null, arrow.Params, body, arrow.Start, arrow.End,
            isAsync: arrow.Async, isGenerator: arrow.Generator, strict: arrow.Strict,
            sourceText: arrow.SourceText);
        EmitFunctionExpression(fe, isArrow: true);
    }

    private static FunctionExpression BuildFunctionExpressionShim(
        Identifier? name, IReadOnlyList<Expression> @params, BlockStatement body,
        Starling.Js.Lex.JsPosition start, Starling.Js.Lex.JsPosition end,
        bool isAsync = false, bool isGenerator = false, bool strict = false, string? sourceText = null)
        => new(name, @params, body, Generator: isGenerator, start, end,
            Async: isAsync, Strict: strict, SourceText: sourceText);

    /// <summary>§14.11 — emit a with-aware opcode that on a runtime hit (an
    /// enclosing object Environment Record has the binding) jumps PAST the
    /// statically-compiled fallback that <paramref name="emitFallback"/> emits.
    /// On a miss the opcode falls through to that fallback. The opcode operand
    /// layout is <c>[u16 nameIdx][i16 missJump]</c>; we patch the jump to land
    /// after the fallback.</summary>
    private void EmitWithGuarded(Opcode op, string name, Action emitFallback)
    {
        _b.EmitU16(op, _b.AddConstant(name));
        var jumpPos = _b.Position;
        _b.EmitI32Raw(0); // i32 miss-jump placeholder
        emitFallback();
        var delta = _b.Position - (jumpPos + 4);
        _b.PatchI32(jumpPos, delta);
    }

    /// <summary>§13.15.2 — lower a compound assignment <c>name op= rhs</c> whose
    /// target routes through a <c>with</c>. The LHS Reference base is resolved
    /// ONCE (by <see cref="Opcode.WithCompoundLoad"/>) and reused for the write
    /// (by <see cref="Opcode.WithCompoundStore"/>) so a getter that deletes the
    /// binding mid-read does not redirect the store to the outer binding. The
    /// resolved base is parked in a reserved local for the duration of the op.
    /// On a with-miss both opcodes fall through to the ordinary static
    /// load/store fallback (so a name that does not live on any with-object
    /// behaves exactly as the non-with compound path).</summary>
    private void EmitWithCompoundAssignment(string name, AssignmentExpression a)
    {
        var nameIdx = _b.AddConstant(name);
        var baseSlot = _b.ReserveLocal();

        // WithCompoundLoad: hit → [oldVal] (base parked in baseSlot), jump past
        // the static fallback load; miss → fall through to the static load.
        _b.EmitU16(Opcode.WithCompoundLoad, nameIdx);
        _b.EmitU16Raw(baseSlot);
        var loadMissPos = _b.Position;
        _b.EmitI32Raw(0);
        EmitIdLoadStatic(name);                       // static fallback load
        _b.PatchI32(loadMissPos, _b.Position - (loadMissPos + 4));

        EmitExpression(a.Value);                      // [oldVal, rhs]
        _b.Emit(CompoundOpToBinaryOpcode(a.Op));      // [newVal]
        _b.Emit(Opcode.Dup);                          // [newVal, newVal] (result + store copy)

        // WithCompoundStore: hit → pop the top copy, Set on the parked base,
        // jump past the static fallback store (leaving the result copy); miss →
        // fall through to the static store, which consumes the top copy.
        _b.EmitU16(Opcode.WithCompoundStore, nameIdx);
        _b.EmitU16Raw(baseSlot);
        var storeMissPos = _b.Position;
        _b.EmitI32Raw(0);
        // The leading read already proved the binding exists (or fell back to a
        // static read), so the static store needs no TDZ check.
        EmitIdStoreStatic(name, needsTdzCheck: false); // static fallback store
        _b.PatchI32(storeMissPos, _b.Position - (storeMissPos + 4));
    }

    private void EmitIdLoad(Identifier id, bool checkedGlobal = true)
    {
        if (IdLoadMayThrow(id.Name, checkedGlobal))
        {
            RecordPos(id);
        }

        EmitIdLoad(id.Name, checkedGlobal);
    }

    private bool IdLoadMayThrow(string name, bool checkedGlobal)
    {
        if (ShouldRouteWith(name))
        {
            return true;
        }

        if (TryResolveLocal(name, out _))
        {
            return IsLexicalLocal(name);
        }

        if (TryResolveUpvalue(name, out var upIdx))
        {
            return IsLexicalUpvalue(upIdx);
        }

        if (_callerScopeNames is { } cs && cs.Contains(name))
        {
            return true;
        }

        return checkedGlobal;
    }

    private void EmitIdLoad(string name, bool checkedGlobal = true)
    {
        if (ShouldRouteWith(name))
        {
            EmitWithGuarded(Opcode.WithLoadOrMiss, name, () => EmitIdLoadStatic(name, checkedGlobal));
            return;
        }
        EmitIdLoadStatic(name, checkedGlobal);
    }

    /// <summary>Emit a store of the value on top of the stack into the binding
    /// <paramref name="name"/>. With-aware: inside a <c>with</c> body the store
    /// first attempts the object Environment Records, falling back to the static
    /// local/upvalue/global store. <paramref name="needsTdzCheck"/> selects the
    /// TDZ-checked store form for plain (non-compound) lexical writes.</summary>
    private void EmitIdStore(string name, bool needsTdzCheck)
    {
        if (ShouldRouteWith(name))
        {
            EmitWithGuarded(Opcode.WithStoreOrMiss, name,
                () => EmitIdStoreStatic(name, needsTdzCheck));
            return;
        }
        EmitIdStoreStatic(name, needsTdzCheck);
    }

    private void EmitIdStoreStatic(string name, bool needsTdzCheck)
    {
        if (TryResolveLocal(name, out var slot))
        {
            // §13.15.2 — a write to a const local (other than its own
            // initializer, which never routes through here) is a runtime
            // TypeError.
            if (IsConstLocal(name) && !_inLexicalDeclInit)
            {
                _b.EmitU16(Opcode.ThrowConstAssignment, _b.AddConstant(name));
                return;
            }
            if (needsTdzCheck && IsLexicalLocal(name))
            {
                if (IsSlotCaptured(slot))
                {
                    _b.EmitSlot(Opcode.StoreCellLocalChecked, slot);
                }
                else
                {
                    EmitTdzLocalStore(name, slot);
                }
            }
            else
            {
                EmitStoreLocalSlot(slot);
            }
        }
        else if (TryResolveUpvalue(name, out var upIdx))
        {
            // §16.2.1.6.2 — assignment to an immutable binding (import / const)
            // throws, except the binding's own initializer (_inLexicalDeclInit).
            if (IsImmutableUpvalue(upIdx) && !_inLexicalDeclInit)
            {
                _b.EmitU16(Opcode.ThrowConstAssignment, _b.AddConstant(name));
                return;
            }
            _b.EmitUpvalue(needsTdzCheck && IsLexicalUpvalue(upIdx)
                ? Opcode.StoreUpvalueChecked : Opcode.StoreUpvalue, upIdx);
        }
        else if (_callerScopeNames is { } cs && cs.Contains(name))
        {
            // wp:M3-72 — direct-eval write to a caller binding (live store).
            _b.EmitU16(Opcode.StoreEvalScope, _b.AddConstant(name));
        }
        else
        {
            _b.EmitU16(Opcode.StoreGlobal, _b.AddConstant(name));
        }
    }

    private void EmitIdLoadStatic(string name, bool checkedGlobal = true)
    {
        if (TryResolveLocal(name, out var slot))
        {
            // TDZ — reads of a lexical binding (let/const/class) check the
            // sentinel; var/param/function reads use the plain fast path.
            if (IsLexicalLocal(name))
            {
                _b.EmitSlot(IsSlotCaptured(slot)
                    ? Opcode.LoadCellLocalChecked : Opcode.LoadLocalChecked, slot);
            }
            else
            {
                EmitLoadLocalSlot(slot);
            }
            return;
        }
        if (TryResolveUpvalue(name, out var upIdx))
        {
            // Every upvalue is a Cell, so
            // LoadUpvalue dereferences it transparently. TDZ — a captured
            // lexical binding from an enclosing scope checks the sentinel.
            _b.EmitUpvalue(IsLexicalUpvalue(upIdx)
                ? Opcode.LoadUpvalueChecked : Opcode.LoadUpvalue, upIdx);
            return;
        }
        if (_callerScopeNames is { } cs && cs.Contains(name))
        {
            // wp:M3-72 — direct-eval read of a caller binding (live read; throws
            // ReferenceError for a TDZ-uninitialized caller lexical). On a miss
            // (the caller binding was deleted, which cannot happen for true
            // locals) the opcode falls back to a checked global load.
            _b.EmitU16(Opcode.LoadEvalScope, _b.AddConstant(name));
            return;
        }
        // §6.2.5.5 GetValue — a free identifier that resolves to no binding
        // (neither local/upvalue nor an own/inherited global property) is an
        // unresolvable Reference whose GetValue throws a ReferenceError. The
        // checked opcode performs that throw; the silent LoadGlobal (yields
        // undefined) is used only where the spec tolerates a missing global —
        // the operand of `typeof` (§13.5.3).
        _b.EmitU16(checkedGlobal ? Opcode.LoadGlobalChecked : Opcode.LoadGlobal,
            _b.AddConstant(name));
    }

    /// <summary>Emit a computed property-key expression (class/object member
    /// <c>[expr]</c>). Per §6.2.5.5 GetValue, evaluating the key reference
    /// must throw a ReferenceError when it is unresolvable, so a key that is a
    /// bare free identifier resolving to a missing global uses the checked
    /// global-load opcode instead of the silent <see cref="Opcode.LoadGlobal"/>
    /// (which returns <c>undefined</c>). All other key shapes evaluate through
    /// the ordinary expression path — their own sub-reads already throw where
    /// the spec requires (e.g. <c>[a.b]</c> reading a missing <c>a</c>).</summary>
    private void EmitComputedKey(Expression key)
    {
        if (key is Identifier id
            && !TryResolveLocal(id.Name, out _)
            && !TryResolveUpvalue(id.Name, out _))
        {
            _b.EmitU16(Opcode.LoadGlobalChecked, _b.AddConstant(id.Name));
            return;
        }
        EmitExpression(key);
    }

    private bool TryResolveLocal(string name, out int slot)
    {
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            if (_scopes[i].TryGetValue(name, out slot))
            {
                return true;
            }
        }

        slot = -1;
        return false;
    }

    /// <summary>
    /// Lazy upvalue resolution per §10.2.1.2. Walks the enclosing
    /// compiler chain: if a parent has <paramref name="name"/> as a
    /// local, record the capture as <c>IsLocalCapture=true</c> with the
    /// parent's slot index; if the parent has it transitively (i.e. it
    /// also captured the name), record <c>IsLocalCapture=false</c>
    /// pointing at the parent's upvalue index. Returns the index in
    /// this compiler's <see cref="_upvalues"/> table.
    /// </summary>
    private bool TryResolveUpvalue(string name, out int upIdx)
    {
        upIdx = -1;
        if (_parent is null)
        {
            return false;
        }

        // Already captured by this function — reuse the same slot so
        // multiple reads of the same name share one upvalue.
        if (_upvalueByName.TryGetValue(name, out upIdx))
        {
            return true;
        }

        if (_parent.TryResolveLocal(name, out var parentSlot))
        {
            upIdx = AddUpvalue(name, new UpvalueRef(IsLocalCapture: true, Index: parentSlot));
            return true;
        }
        if (_parent.TryResolveUpvalue(name, out var parentUp))
        {
            upIdx = AddUpvalue(name, new UpvalueRef(IsLocalCapture: false, Index: parentUp));
            return true;
        }
        return false;
    }

    private int AddUpvalue(string name, UpvalueRef u)
    {
        if (_upvalues.Count >= 65535)
        {
            throw new NotSupportedException("more than 65535 upvalues per function not supported");
        }

        var idx = _upvalues.Count;
        _upvalues.Add(u);
        _upvalueByName[name] = idx;
        return idx;
    }

    private void EmitLogical(LogicalExpression log)
    {
        EmitExpression(log.Left);
        // Duplicate so the short-circuit value remains on the stack.
        _b.Emit(Opcode.Dup);
        Opcode jmp = log.Op switch
        {
            JsTokenKind.AmpAmp => Opcode.JumpIfFalse,
            JsTokenKind.PipePipe => Opcode.JumpIfTrue,
            JsTokenKind.QuestionQuestion => Opcode.JumpIfNotNullish,
            _ => throw new NotSupportedException(log.Op.ToString()),
        };
        var jumpAddr = _b.EmitJump(jmp);
        _b.Emit(Opcode.Pop); // discard the duplicated left-hand value
        EmitExpression(log.Right);
        _b.PatchJump(jumpAddr);
    }

    private void EmitUpdate(UpdateExpression up)
    {
        if (up.Argument is Identifier id)
        {
            // §14.11 — inside a `with`, route the read and the write through the
            // with-aware load/store so an enclosing object Environment Record
            // is consulted first. Mirror the Dup ordering of the static arms:
            // postfix returns the (numeric-coerced) old value, prefix the new.
            if (ShouldRouteWith(id.Name))
            {
                EmitIdLoad(id.Name);                            // [old]
                _b.EmitU16(Opcode.LoadConst, _b.AddConstant(0.0));
                _b.Emit(Opcode.Add);                            // [ToNumber(old)]
                if (!up.Prefix)
                {
                    _b.Emit(Opcode.Dup);            // postfix: keep old
                }

                _b.EmitU16(Opcode.LoadConst, _b.AddConstant(1.0));
                _b.Emit(up.Op == JsTokenKind.PlusPlus ? Opcode.Add : Opcode.Sub);
                if (up.Prefix)
                {
                    _b.Emit(Opcode.Dup);             // prefix: keep new
                }

                EmitIdStore(id.Name, needsTdzCheck: false);
                return;
            }
            // Try local first, then upvalue, then global.
            if (TryResolveLocal(id.Name, out var slot))
            {
                // TDZ — the load checks the sentinel (throws if read before
                // init); the subsequent store can be unchecked since a
                // successful load proves the binding is initialized.
                var lexical = IsLexicalLocal(id.Name);
                if (lexical)
                {
                    _b.EmitSlot(IsSlotCaptured(slot)
                    ? Opcode.LoadCellLocalChecked : Opcode.LoadLocalChecked, slot);
                }
                else
                {
                    EmitLoadLocalSlot(slot);
                }

                if (!up.Prefix)
                {
                    _b.Emit(Opcode.Dup);
                }

                _b.EmitU16(Opcode.LoadConst, _b.AddConstant(1.0));
                _b.Emit(up.Op == JsTokenKind.PlusPlus ? Opcode.Add : Opcode.Sub);
                if (up.Prefix)
                {
                    _b.Emit(Opcode.Dup);
                }

                EmitStoreLocalSlot(slot);
                return;
            }
            if (TryResolveUpvalue(id.Name, out var upIdx))
            {
                _b.EmitUpvalue(IsLexicalUpvalue(upIdx)
                    ? Opcode.LoadUpvalueChecked : Opcode.LoadUpvalue, upIdx);
                if (!up.Prefix)
                {
                    _b.Emit(Opcode.Dup);
                }

                _b.EmitU16(Opcode.LoadConst, _b.AddConstant(1.0));
                _b.Emit(up.Op == JsTokenKind.PlusPlus ? Opcode.Add : Opcode.Sub);
                if (up.Prefix)
                {
                    _b.Emit(Opcode.Dup);
                }
                // §16.2.1.6.2 — `import++`/`--` writes to an immutable binding.
                if (IsImmutableUpvalue(upIdx))
                {
                    _b.EmitU16(Opcode.ThrowConstAssignment, _b.AddConstant(id.Name));
                }
                else
                {
                    _b.EmitUpvalue(Opcode.StoreUpvalue, upIdx);
                }

                return;
            }
            // gap:script-top-var-not-global — `x++` where `x` is a global
            // (e.g. a script-top `var`) does Load, ±1, Store through the
            // global object. The `Dup` ordering mirrors the local/upvalue
            // arms above so postfix returns the pre-update value and prefix
            // returns the post-update value.
            var nameIdx = _b.AddConstant(id.Name);
            // §13.4.4.1 — the initial read is GetValue, so an unresolvable free
            // identifier throws a ReferenceError (checked global load).
            _b.EmitU16(Opcode.LoadGlobalChecked, nameIdx);
            if (!up.Prefix)
            {
                _b.Emit(Opcode.Dup);
            }

            _b.EmitU16(Opcode.LoadConst, _b.AddConstant(1.0));
            _b.Emit(up.Op == JsTokenKind.PlusPlus ? Opcode.Add : Opcode.Sub);
            if (up.Prefix)
            {
                _b.Emit(Opcode.Dup);
            }

            _b.EmitU16(Opcode.StoreGlobal, nameIdx);
            return;
        }
        if (up.Argument is MemberExpression me)
        {
            // §13.4 Update Expressions — member-expression target.
            //
            // The receiver (and computed key) must be evaluated EXACTLY ONCE.
            // StoreProperty / StoreComputed already re-push the stored value
            // (same contract as compound assignment in EmitAssignment), so we
            // can recover oldNum from newVal without any temporary locals.
            //
            // Reject `super.x++` / `super[k]++` — these would require
            // PutValue on a super-reference, which needs separate super-update
            // opcodes (deferred; add them if the McMaster app needs it).
            if (me.Object is SuperPropertyExpression)
            {
                throw new NotSupportedException("update of super property not yet supported (wp:M3-15)");
            }

            var isIncrement = up.Op == JsTokenKind.PlusPlus;

            // §13.4 — a private member target (obj.#x++ / ++obj.#x / …) reads and
            // writes through PrivateGet/PrivateSet on a dup'd receiver, mirroring
            // the guarded compound-assignment path in EmitAssignment. Without this
            // guard the non-computed arm below would cast the PrivateNameExpression
            // property to Identifier and throw.
            if (!me.Computed && me.Property is PrivateNameExpression pne)
            {
                // PrivateSet pops [obj, newVal] and RE-PUSHES newVal (same contract
                // as StoreProperty), so the postfix arm recovers oldNum = newVal ∓ 1
                // without any temporary locals.
                //
                // Prefix  (++obj.#x):
                //   EmitObj, Dup, PrivateGet(#x), UnaryPlus,
                //   LoadConst(1), Add/Sub, PrivateSet(#x)             → [newVal]
                //
                // Postfix (obj.#x++):
                //   …same…, PrivateSet(#x), LoadConst(1), Sub/Add     → [oldNum]
                var mangled = ResolvePrivateName(pne.Name, pne.Start);
                var pneIdx = _b.AddConstant(mangled);
                EmitExpression(me.Object);                              // [obj]
                _b.Emit(Opcode.Dup);                                    // [obj, obj]
                _b.EmitU16(Opcode.PrivateGet, pneIdx);                  // [obj, oldVal]
                _b.Emit(Opcode.UnaryPlus);                              // [obj, oldNum]   ToNumber
                _b.EmitU16(Opcode.LoadConst, _b.AddConstant(1.0));      // [obj, oldNum, 1]
                _b.Emit(isIncrement ? Opcode.Add : Opcode.Sub);         // [obj, newVal]
                _b.EmitU16(Opcode.PrivateSet, pneIdx);                  // [newVal]
                if (!up.Prefix)
                {
                    // Postfix: result = oldNum = newVal ∓ 1 (reverse the ±1 step).
                    _b.EmitU16(Opcode.LoadConst, _b.AddConstant(1.0));  // [newVal, 1]
                    _b.Emit(isIncrement ? Opcode.Sub : Opcode.Add);     // [oldNum]
                }
                return;
            }

            if (!me.Computed)
            {
                // Non-computed: obj.name++ / obj.name-- / ++obj.name / --obj.name
                //
                // §13.4 mandates ToNumeric(GetValue(lhs)) before the ±1 step; we
                // use UnaryPlus (≡ ToNumber) so strings/booleans are coerced
                // correctly ("5" → 5, true → 1, etc.) before Add/Sub.
                //
                // StoreProperty pops [obj, newVal] and RE-PUSHES newVal, which
                // lets the postfix arm recover oldNum = newVal ∓ 1 without any
                // temporary locals.
                //
                // Prefix byte sequence  (++obj.name):
                //   EmitObj, Dup, LoadProperty(name), UnaryPlus,
                //   LoadConst(1), Add/Sub, StoreProperty(name)       → [newVal]
                //
                // Postfix byte sequence (obj.name++):
                //   EmitObj, Dup, LoadProperty(name), UnaryPlus,
                //   LoadConst(1), Add/Sub, StoreProperty(name),
                //   LoadConst(1), Sub/Add                             → [oldNum]
                var nameIdx = _b.AddConstant(((Identifier)me.Property).Name);
                EmitExpression(me.Object);                              // [obj]
                _b.Emit(Opcode.Dup);                                    // [obj, obj]
                _b.EmitProperty(Opcode.LoadProperty, nameIdx);               // [obj, oldVal]
                _b.Emit(Opcode.UnaryPlus);                              // [obj, oldNum]   ToNumber
                _b.EmitU16(Opcode.LoadConst, _b.AddConstant(1.0));      // [obj, oldNum, 1]
                _b.Emit(isIncrement ? Opcode.Add : Opcode.Sub);         // [obj, newVal]
                _b.EmitProperty(Opcode.StoreProperty, nameIdx);              // [newVal]
                if (!up.Prefix)
                {
                    // Postfix: result = oldNum = newVal ∓ 1 (reverse the ±1 step).
                    _b.EmitU16(Opcode.LoadConst, _b.AddConstant(1.0));  // [newVal, 1]
                    _b.Emit(isIncrement ? Opcode.Sub : Opcode.Add);     // [oldNum]
                }
                return;
            }
            else
            {
                // Computed: obj[key]++ / obj[key]-- / ++obj[key] / --obj[key]
                //
                // Both obj and key are evaluated exactly once via Dup2.
                // UnaryPlus coerces oldVal to number before the ±1 step.
                //
                // Prefix byte sequence  (++obj[key]):
                //   EmitObj, EmitKey, Dup2, LoadComputed, UnaryPlus,
                //   LoadConst(1), Add/Sub, StoreComputed              → [newVal]
                //
                // Postfix byte sequence (obj[key]++):
                //   EmitObj, EmitKey, Dup2, LoadComputed, UnaryPlus,
                //   LoadConst(1), Add/Sub, StoreComputed,
                //   LoadConst(1), Sub/Add                             → [oldNum]
                EmitExpression(me.Object);                              // [obj]
                EmitExpression(me.Property);                            // [obj, key]
                _b.Emit(Opcode.Dup2);                                   // [obj, key, obj, key]
                _b.Emit(Opcode.LoadComputed);                           // [obj, key, oldVal]
                _b.Emit(Opcode.UnaryPlus);                              // [obj, key, oldNum]  ToNumber
                _b.EmitU16(Opcode.LoadConst, _b.AddConstant(1.0));      // [obj, key, oldNum, 1]
                _b.Emit(isIncrement ? Opcode.Add : Opcode.Sub);         // [obj, key, newVal]
                _b.Emit(Opcode.StoreComputed);                          // [newVal]
                if (!up.Prefix)
                {
                    // Postfix: result = oldNum = newVal ∓ 1.
                    _b.EmitU16(Opcode.LoadConst, _b.AddConstant(1.0));  // [newVal, 1]
                    _b.Emit(isIncrement ? Opcode.Sub : Opcode.Add);     // [oldNum]
                }
                return;
            }
        }
        // §13.4.1 — the operand of a prefix/postfix ++/-- must be a valid simple
        // assignment target (an Identifier or MemberExpression). Anything else
        // (a call, literal, ImportCall, etc.) is an early SyntaxError, not a
        // host failure.
        throw new Parse.JsParseException(
            "invalid increment/decrement operand: not a valid assignment target", up.Start);
    }

    /// <summary>gap:delete — lower <c>delete expr</c> per §13.5.1. For
    /// member targets, evaluate the receiver + key and emit
    /// <see cref="Opcode.DeleteProperty"/>. For non-Reference targets
    /// (plain identifiers, literals, parenthesized expressions), the spec
    /// says evaluate-for-side-effects then return <c>true</c> (sloppy mode);
    /// our engine does not enforce strict mode globally so unqualified
    /// <c>delete x</c> is a no-op that yields <c>true</c>.</summary>
    private void EmitDelete(UnaryExpression u)
    {
        if (u.Argument is MemberExpression me)
        {
            // Reject `delete super.x` — §13.5.1.2 throws SyntaxError.
            if (me.Object is SuperPropertyExpression)
            {
                throw new Parse.JsParseException("delete of super property is a SyntaxError", u.Start);
            }
            // Private fields cannot be deleted (early error per §13.5.1).
            if (!me.Computed && me.Property is PrivateNameExpression)
            {
                throw new Parse.JsParseException("delete of a private field is a SyntaxError", u.Start);
            }

            EmitExpression(me.Object);
            if (me.Computed)
            {
                EmitExpression(me.Property);
            }
            else
            {
                _b.EmitU16(Opcode.LoadConst, _b.AddConstant(((Identifier)me.Property).Name));
            }

            _b.Emit(Opcode.DeleteProperty);
            return;
        }
        // Non-Reference: evaluate the operand for side effects, drop, push true.
        // (Identifier deletes are also routed here — they are reference-of-an-
        // environment-record per spec; sloppy mode returns true.)
        if (u.Argument is Identifier delId)
        {
            // §14.11 — inside a `with`, `delete name` deletes the property from
            // the enclosing object Environment Record when present; otherwise
            // the sloppy-mode identifier delete is a no-op that returns true.
            if (ShouldRouteWith(delId.Name))
            {
                EmitWithGuarded(Opcode.WithDeleteOrMiss, delId.Name,
                    () => _b.Emit(Opcode.LoadTrue));
                return;
            }
            // wp:M3-73 — in a non-strict direct eval (whose caller is a function),
            // `delete name` for a free identifier may target a binding the eval
            // introduced into the caller's var-environment. Those bindings are
            // configurable, so the delete actually removes it from the
            // eval-introduced var store (a later read then throws ReferenceError);
            // a name not in the store is the ordinary sloppy no-op (true).
            if (_evalInjectVars
                && !TryResolveLocal(delId.Name, out _)
                && !TryResolveUpvalue(delId.Name, out _))
            {
                _b.EmitU16(Opcode.DeleteEvalVar, _b.AddConstant(delId.Name));
                return;
            }
            _b.Emit(Opcode.LoadTrue);
            return;
        }
        EmitExpression(u.Argument);
        _b.Emit(Opcode.Pop);
        _b.Emit(Opcode.LoadTrue);
    }

    private void EmitAssignment(AssignmentExpression a)
    {
        if (IsPattern(a.Target))
        {
            // §13.15.1: a destructuring (array/object) assignment target only
            // pairs with the plain `=` operator — a compound operator such as
            // `[a] += x` is an early SyntaxError.
            if (a.Op != JsTokenKind.Eq)
            {
                throw new Parse.JsParseException("compound assignment with a destructuring target is a SyntaxError", a.Start);
            }
            // ECMA-262 §13.15 destructuring assignment evaluates the RHS once,
            // performs the pattern writes, and the whole expression returns the RHS.
            var rhsSlot = _b.ReserveLocal();
            EmitExpression(a.Value);
            _b.EmitSlot(Opcode.StoreLocal, rhsSlot);
            EmitPatternFromLocal(a.Target, rhsSlot, isDeclaration: false);
            _b.EmitSlot(Opcode.LoadLocal, rhsSlot);
            return;
        }
        // §13.15.2 — the three logical assignment operators short-circuit and
        // therefore have entirely different control flow from the arithmetic /
        // bitwise compound operators handled by the branches below. Intercept
        // them here, before the generic compound paths, for every target form.
        if (IsLogicalAssignOp(a.Op))
        {
            EmitLogicalAssignment(a);
            return;
        }
        if (a.Target is Identifier id)
        {
            // §13.15.2 — a compound assignment whose target routes through a
            // `with` must resolve the LHS Reference's base EXACTLY ONCE and
            // reuse it for both the read and the write. A self-deleting getter
            // can remove the binding from the with-object during the read; the
            // store must still land on that SAME object (PutValue(lref, v) uses
            // the initially-created Reference). The plain WithLoad/WithStore
            // pair re-resolves the name on the store side, which would miss the
            // (now-deleted) binding and write the outer binding instead. The
            // WithCompoundLoad/WithCompoundStore pair captures the base once.
            if (a.Op != JsTokenKind.Eq && ShouldRouteWith(id.Name))
            {
                EmitWithCompoundAssignment(id.Name, a);
                return;
            }
            if (a.Op != JsTokenKind.Eq)
            {
                // Compound: load + apply binary + store.
                EmitIdLoad(id.Name);
                EmitExpression(a.Value);
                _b.Emit(CompoundOpToBinaryOpcode(a.Op));
            }
            else
            {
                // §named-evaluation — `x = function(){}` names the function "x".
                EmitNamedEvaluation(a.Value, id.Name);
            }
            _b.Emit(Opcode.Dup); // assignment is an expression — leaves value on stack
            // Assigning to a captured upvalue must
            // route through the shared cell; assignment to a captured local
            // routes through StoreCellLocal.
            // TDZ — a write to a lexical binding before initialization throws.
            // For a compound assignment the preceding EmitIdLoad already checked
            // the sentinel, so only the plain `=` form needs the checked store.
            var needsTdzCheck = a.Op == JsTokenKind.Eq;
            EmitIdStore(id.Name, needsTdzCheck);
            return;
        }
        if (a.Target is SuperPropertyExpression sptarget)
        {
            // wp:M3-04h — super[expr] = v (and compound forms). Per spec
            // (§13.3.4 / PutValue on a super reference) the write targets the
            // receiver `this`, while the read for compound forms resolves
            // through the home object's prototype.
            if (!sptarget.Computed)
            {
                // super.name = v (and compound super.name op= v). Per spec the
                // write targets the receiver `this`; the compound read resolves
                // through the home object's prototype. LoadSuperProperty /
                // StoreSuperProperty carry the name as a constant operand.
                var name = ((Identifier)sptarget.Property).Name;
                if (a.Op != JsTokenKind.Eq)
                {
                    _b.EmitU16(Opcode.LoadSuperProperty, _b.AddConstant(name));  // [oldVal]
                    EmitExpression(a.Value);                                     // [oldVal, rhs]
                    _b.Emit(CompoundOpToBinaryOpcode(a.Op));                     // [newVal]
                    _b.EmitU16(Opcode.StoreSuperProperty, _b.AddConstant(name)); // [newVal]
                    return;
                }
                EmitExpression(a.Value);                                         // [value]
                _b.EmitU16(Opcode.StoreSuperProperty, _b.AddConstant(name));     // [value]
                return;
            }
            if (a.Op != JsTokenKind.Eq)
            {
                // Compound `super[k] op= v`: evaluate the key once, dup it for the
                // read, apply the op, then store back to `this`.
                EmitExpression(sptarget.Property);   // [key]
                _b.Emit(Opcode.Dup);                 // [key, key]
                _b.Emit(Opcode.LoadSuperComputed);   // [key, oldVal]
                EmitExpression(a.Value);             // [key, oldVal, rhs]
                _b.Emit(CompoundOpToBinaryOpcode(a.Op)); // [key, newVal]
                _b.Emit(Opcode.StoreSuperComputed);  // [newVal]
                return;
            }
            EmitExpression(sptarget.Property);        // [key]
            EmitExpression(a.Value);                  // [key, value]
            _b.Emit(Opcode.StoreSuperComputed);       // [value]
            return;
        }
        if (a.Target is MemberExpression me)
        {
            // Super-property assignment: super.x = v writes to `this`.
            if (me.Object is SuperPropertyExpression)
            {
                throw new NotSupportedException("super.x = v is not supported in B1b-2a");
            }
            // Private name assignment: this.#x = v (and compound this.#x op= v).
            if (!me.Computed && me.Property is PrivateNameExpression pne)
            {
                var mangled = ResolvePrivateName(pne.Name, pne.Start);
                var pneIdx = _b.AddConstant(mangled);
                if (a.Op != JsTokenKind.Eq)
                {
                    // §13.15.2 — evaluate the base ONCE: dup it for the read,
                    // PrivateGet the old value, apply the op, then PrivateSet
                    // through the same receiver (PrivateSet re-pushes the value).
                    EmitExpression(me.Object);                  // [obj]
                    _b.Emit(Opcode.Dup);                        // [obj, obj]
                    _b.EmitU16(Opcode.PrivateGet, pneIdx);      // [obj, oldVal]
                    EmitExpression(a.Value);                    // [obj, oldVal, rhs]
                    _b.Emit(CompoundOpToBinaryOpcode(a.Op));    // [obj, newVal]
                    _b.EmitU16(Opcode.PrivateSet, pneIdx);      // [newVal]
                    return;
                }
                EmitExpression(me.Object);
                EmitExpression(a.Value);
                _b.EmitU16(Opcode.PrivateSet, pneIdx);
                return;
            }
            // gap:compound-assign-property — for compound forms (`obj.x += v`,
            // `obj[k] *= v`, …) the spec (§13.15.2) evaluates the base
            // **once**, reads the property, applies the op, then writes
            // back to the same base/key. Previously we re-emitted the
            // member expression on the write side, which evaluated the
            // base (and any computed-key side effects) a second time and
            // could route the write to the wrong place when the base
            // expression was non-pure. Fix: dup the resolved base (+ key
            // for computed) before the read, then reuse those dup'd
            // values on the store side.
            if (a.Op != JsTokenKind.Eq)
            {
                EmitExpression(me.Object);
                if (me.Computed)
                {
                    EmitExpression(me.Property);          // base, rawKey
                    // §13.3.3 — resolve the key once (and reject a nullish base
                    // before ToPropertyKey runs), so a user toString/@@toPrimitive
                    // on the key fires exactly once across the read and the write.
                    _b.Emit(Opcode.ResolveComputedKey);   // base, key
                    _b.Emit(Opcode.Dup2);                 // base, key, base, key
                    _b.Emit(Opcode.LoadComputed);         // base, key, oldVal
                    EmitExpression(a.Value);              // base, key, oldVal, rhs
                    _b.Emit(CompoundOpToBinaryOpcode(a.Op)); // base, key, newVal
                    _b.Emit(Opcode.StoreComputed);        // newVal
                }
                else
                {
                    _b.Emit(Opcode.Dup);                  // base, base
                    var nameIdx = _b.AddConstant(((Identifier)me.Property).Name);
                    _b.EmitProperty(Opcode.LoadProperty, nameIdx); // base, oldVal
                    EmitExpression(a.Value);              // base, oldVal, rhs
                    _b.Emit(CompoundOpToBinaryOpcode(a.Op)); // base, newVal
                    _b.EmitProperty(Opcode.StoreProperty, nameIdx); // newVal
                }
                return;
            }
            // Plain `obj.x = v` / `obj[k] = v`.
            // StoreProperty / StoreComputed already re-push the assigned
            // value, so the expression's net result is one value on the
            // stack — no extra Dup needed (it would leak a value).
            EmitExpression(me.Object);
            if (me.Computed)
            {
                EmitExpression(me.Property);
            }

            EmitExpression(a.Value);
            if (me.Computed)
            {
                _b.Emit(Opcode.StoreComputed);
            }
            else
            {
                _b.EmitProperty(Opcode.StoreProperty, _b.AddConstant(((Identifier)me.Property).Name));
            }

            return;
        }
        // Array/object destructuring assignment targets are handled by the
        // IsPattern branch above; member and identifier targets by the branches
        // in between. Anything reaching here (e.g. `f() = x`, `1 = x`) is an
        // invalid assignment target — §13.15.1 makes these early SyntaxErrors.
        throw new Parse.JsParseException($"invalid assignment target '{a.Target.GetType().Name}'", a.Start);
    }

    /// <summary>
    /// Lower one of the three ES2021 logical assignment operators
    /// (<c>||=</c>, <c>&amp;&amp;=</c>, <c>??=</c>) per ECMA-262 §13.15.2.
    /// These are NOT <c>a = a op b</c>: they short-circuit. The shape for
    /// every target form is the same:
    /// <list type="number">
    ///   <item>Evaluate the target reference (object/key) EXACTLY ONCE.</item>
    ///   <item>Read the current value; Dup it and run the short-circuit jump.
    ///         If the jump is taken, no read of the RHS and no store happens,
    ///         and the current value is the result.</item>
    ///   <item>Otherwise Pop the current value, evaluate the RHS, and store
    ///         through the same reference, leaving the stored value as the
    ///         result (the whole expression yields the final target value).</item>
    /// </list>
    /// At the merge point the stack holds exactly one value (current value on
    /// the short-circuit path, stored value on the assign path).
    /// </summary>
    private void EmitLogicalAssignment(AssignmentExpression a)
    {
        var jmp = LogicalAssignShortCircuitJump(a.Op);

        if (a.Target is Identifier id)
        {
            // Resolve the binding once; the same kind (local / upvalue /
            // global) is used for both the read and the write so the
            // closure write-back path matches the plain `=` case.
            if (TryResolveLocal(id.Name, out var slot))
            {
                // TDZ — the initial read checks the sentinel; the store can stay
                // unchecked since a successful read proves initialization.
                if (IsLexicalLocal(id.Name))
                {
                    _b.EmitSlot(IsSlotCaptured(slot)
                    ? Opcode.LoadCellLocalChecked : Opcode.LoadLocalChecked, slot);
                }
                else
                {
                    EmitLoadLocalSlot(slot);       // [cur]
                }

                _b.Emit(Opcode.Dup);                // [cur, cur]
                var j = _b.EmitJump(jmp);           // pops one cur; [cur] if short-circuit
                _b.Emit(Opcode.Pop);                // assign path: drop cur → []
                EmitNamedEvaluation(a.Value, id.Name); // [rhs]
                _b.Emit(Opcode.Dup);                // [rhs, rhs] — keep result after store
                EmitStoreLocalSlot(slot);           // [rhs]
                _b.PatchJump(j);                    // merge: [cur] or [rhs]
                return;
            }
            if (TryResolveUpvalue(id.Name, out var upIdx))
            {
                _b.EmitUpvalue(IsLexicalUpvalue(upIdx)
                    ? Opcode.LoadUpvalueChecked : Opcode.LoadUpvalue, upIdx); // [cur]
                _b.Emit(Opcode.Dup);
                var j = _b.EmitJump(jmp);
                _b.Emit(Opcode.Pop);
                EmitNamedEvaluation(a.Value, id.Name);
                _b.Emit(Opcode.Dup);
                // §16.2.1.6.2 — `import ||= …` etc. writes to an immutable binding
                // (only on the assign path; the short-circuit path skips it).
                if (IsImmutableUpvalue(upIdx))
                {
                    _b.EmitU16(Opcode.ThrowConstAssignment, _b.AddConstant(id.Name));
                }
                else
                {
                    _b.EmitUpvalue(Opcode.StoreUpvalue, upIdx); // [rhs]
                }

                _b.PatchJump(j);
                return;
            }
            // Global / script-top var target.
            var nameIdx = _b.AddConstant(id.Name);
            // §13.15.2 — the leading read is GetValue, so an unresolvable free
            // identifier throws a ReferenceError (checked global load).
            _b.EmitU16(Opcode.LoadGlobalChecked, nameIdx);   // [cur]
            _b.Emit(Opcode.Dup);
            var jg = _b.EmitJump(jmp);
            _b.Emit(Opcode.Pop);
            EmitNamedEvaluation(a.Value, id.Name);
            _b.Emit(Opcode.Dup);
            _b.EmitU16(Opcode.StoreGlobal, nameIdx);  // [rhs]
            _b.PatchJump(jg);
            return;
        }

        if (a.Target is MemberExpression me)
        {
            // super.x ??= … would require PutValue on a super-reference; the
            // plain `=`/compound super paths above use dedicated opcodes and
            // netclaw doesn't need the logical form, so defer it explicitly.
            if (me.Object is SuperPropertyExpression)
            {
                throw new NotSupportedException("logical assignment to a super property is not supported");
            }
            // Private-name logical assignment (this.#x &&= / ||= / ??= …).
            // Same shape as the plain non-computed member path below, but the
            // read/write route through PrivateGet/PrivateSet on a dup'd receiver.
            if (!me.Computed && me.Property is PrivateNameExpression ppne)
            {
                var pmangled = ResolvePrivateName(ppne.Name, ppne.Start);
                var pmIdx = _b.AddConstant(pmangled);
                EmitExpression(me.Object);          // [obj]
                _b.Emit(Opcode.Dup);                // [obj, obj]
                _b.EmitU16(Opcode.PrivateGet, pmIdx); // [obj, cur]
                _b.Emit(Opcode.Dup);                // [obj, cur, cur]
                var jp = _b.EmitJump(jmp);          // pops one cur
                // Assign path: [obj, cur]. Drop cur, eval RHS, PrivateSet
                // (PrivateSet pops obj,rhs and RE-PUSHES rhs).
                _b.Emit(Opcode.Pop);                // [obj]
                EmitNamedEvaluation(a.Value, "#" + ppne.Name); // [obj, rhs]
                _b.EmitU16(Opcode.PrivateSet, pmIdx); // [rhs]
                var jpEnd = _b.EmitJump(Opcode.Jump);
                // Short-circuit path: [obj, cur]; drop the dup'd base from
                // underneath, leaving cur. Reorder via a temp local.
                _b.PatchJump(jp);                   // [obj, cur]
                var ptmp = _b.ReserveLocal();
                EmitStoreLocalSlot(ptmp);           // [obj]
                _b.Emit(Opcode.Pop);                // []
                EmitLoadLocalSlot(ptmp);            // [cur]
                _b.PatchJump(jpEnd);                // merge: [rhs] or [cur]
                return;
            }

            if (me.Computed)
            {
                // Evaluate base AND key exactly once, then keep both for a
                // possible store via Dup2 (same once-eval pattern as the
                // computed member-update lowering in EmitUpdate).
                EmitExpression(me.Object);          // [obj]
                EmitExpression(me.Property);        // [obj, key]
                _b.Emit(Opcode.ResolveComputedKey); // [obj, key]
                _b.Emit(Opcode.Dup2);               // [obj, key, obj, key]
                _b.Emit(Opcode.LoadComputed);       // [obj, key, cur]
                _b.Emit(Opcode.Dup);                // [obj, key, cur, cur]
                var j = _b.EmitJump(jmp);           // pops one cur
                // Assign path: [obj, key, cur]. Drop cur, eval RHS, store
                // (StoreComputed pops obj,key,rhs and RE-PUSHES rhs).
                _b.Emit(Opcode.Pop);                // [obj, key]
                EmitExpression(a.Value);            // [obj, key, rhs]
                if (IsAnonymousFunctionDefinition(a.Value))
                {
                    _b.Emit(Opcode.SetFunctionNameComputed); // [obj, key, rhs]
                }

                _b.Emit(Opcode.StoreComputed);      // [rhs]
                var jEnd = _b.EmitJump(Opcode.Jump);
                // Short-circuit path: stack is [obj, key, cur]; discard the
                // dup'd base + key, leaving cur as the result.
                _b.PatchJump(j);                    // [obj, key, cur]
                // Remove obj and key from underneath cur. We have no swap-N
                // opcode, so reorder via a temp local.
                var tmp = _b.ReserveLocal();
                EmitStoreLocalSlot(tmp);            // [obj, key]
                _b.Emit(Opcode.Pop);                // [obj]
                _b.Emit(Opcode.Pop);                // []
                EmitLoadLocalSlot(tmp);             // [cur]
                _b.PatchJump(jEnd);                 // merge: [rhs] or [cur]
                return;
            }
            else
            {
                var propName = ((Identifier)me.Property).Name;
                var nameIdx = _b.AddConstant(propName);
                EmitExpression(me.Object);          // [obj]
                _b.Emit(Opcode.Dup);                // [obj, obj]
                _b.EmitProperty(Opcode.LoadProperty, nameIdx); // [obj, cur]
                _b.Emit(Opcode.Dup);                // [obj, cur, cur]
                var j = _b.EmitJump(jmp);           // pops one cur
                // Assign path: [obj, cur]. Drop cur, eval RHS, store
                // (StoreProperty pops obj,rhs and RE-PUSHES rhs).
                _b.Emit(Opcode.Pop);                // [obj]
                EmitNamedEvaluation(a.Value, propName); // [obj, rhs]
                _b.EmitProperty(Opcode.StoreProperty, nameIdx); // [rhs]
                var jEnd = _b.EmitJump(Opcode.Jump);
                // Short-circuit path: stack is [obj, cur]; drop the dup'd base
                // from underneath, leaving cur. Use a temp to reorder.
                _b.PatchJump(j);                    // [obj, cur]
                var tmp = _b.ReserveLocal();
                EmitStoreLocalSlot(tmp);            // [obj]
                _b.Emit(Opcode.Pop);                // []
                EmitLoadLocalSlot(tmp);             // [cur]
                _b.PatchJump(jEnd);                 // merge: [rhs] or [cur]
                return;
            }
        }

        throw new Parse.JsParseException($"invalid logical-assignment target '{a.Target.GetType().Name}'", a.Start);
    }

    /// <summary>wp:M3-23 — record the AST node's source position against the
    /// NEXT opcode the builder will emit, so runtime throws from that opcode
    /// can report <c>(at line:col)</c>. Cheap: only called at the small set of
    /// throw-prone emit sites (calls / new / member loads).</summary>
    private void RecordPos(AstNode node)
        => _b.RecordPosition(node.Start.Line, node.Start.Column);

    // §13.3 OptionalChain — jump patches collected by every optional link of
    // the chain currently being compiled. The chain ROOT (the outermost
    // member/call node) creates the list and patches every jump to the
    // instruction right after the whole chain, so `a?.b.c` yields undefined
    // without reading `.c` when `a` is nullish. Null when no chain is active;
    // independent sub-chains (computed keys, call arguments) suspend it via
    // EmitNonChainSubexpression so they root themselves.
    private List<int>? _chainExitJumps;

    /// <summary>Does this member/call chain contain an optional (<c>?.</c>)
    /// link? Walks down member objects / call callees. A parenthesized
    /// sub-expression terminates the chain per §13.3 — but only when it is a
    /// LINK; parentheses around the whole chain don't change its root.</summary>
    private static bool HasOptionalLink(Expression e)
    {
        var first = true;
        while (true)
        {
            if (!first && e.IsParenthesized)
            {
                return false;
            }

            first = false;
            switch (e)
            {
                case MemberExpression me:
                    if (me.Optional)
                    {
                        return true;
                    }

                    e = me.Object;
                    break;
                case CallExpression ce:
                    if (ce.Optional)
                    {
                        return true;
                    }

                    e = ce.Callee;
                    break;
                default:
                    return false;
            }
        }
    }

    /// <summary>Emit an expression that is NOT a link of the active optional
    /// chain (a computed key or a call argument): the chain context is
    /// suspended so an optional chain inside it short-circuits to its own
    /// boundary, not the enclosing chain's exit.</summary>
    private void EmitNonChainSubexpression(Expression e)
    {
        var saved = _chainExitJumps;
        _chainExitJumps = null;
        try { EmitExpression(e); }
        finally { _chainExitJumps = saved; }
    }

    /// <summary>Route one optional link's "yield undefined" jump: into the
    /// active chain context (the root patches it past the whole chain), or
    /// patched locally when no chain is active (defensive fallback).</summary>
    private void RouteChainExitJump(int jump)
    {
        if (_chainExitJumps is not null)
        {
            _chainExitJumps.Add(jump);
        }
        else
        {
            _b.PatchJump(jump);
        }
    }

    private void EmitMemberLoad(MemberExpression m)
    {
        var rootsChain = _chainExitJumps is null && HasOptionalLink(m);
        if (rootsChain)
        {
            _chainExitJumps = new List<int>();
        }

        try
        {
            EmitMemberLoadCore(m);
        }
        finally
        {
            if (rootsChain)
            {
                foreach (var j in _chainExitJumps!)
                {
                    _b.PatchJump(j);
                }

                _chainExitJumps = null;
            }
        }
    }

    private void EmitMemberLoadCore(MemberExpression m)
    {
        // Private name: obj.#name (and the optional form obj?.#name).
        if (!m.Computed && m.Property is PrivateNameExpression pne)
        {
            var mangled = ResolvePrivateName(pne.Name, pne.Start);
            EmitExpression(m.Object);                 // [obj]
            if (m.Optional)
            {
                // §13.3 OptionalChain — a nullish base short-circuits the chain
                // to `undefined` without performing the private brand check.
                _b.Emit(Opcode.Dup);                  // [obj, obj]
                var notNullish = _b.EmitJump(Opcode.JumpIfNotNullish); // pops one
                // base is nullish: drop it and yield undefined.
                _b.Emit(Opcode.Pop);                  // []
                _b.Emit(Opcode.LoadUndefined);        // [undefined]
                RouteChainExitJump(_b.EmitJump(Opcode.Jump));
                _b.PatchJump(notNullish);             // [obj]
                RecordPos(m);
                _b.EmitU16(Opcode.PrivateGet, _b.AddConstant(mangled)); // [value]
                return;
            }
            RecordPos(m);
            _b.EmitU16(Opcode.PrivateGet, _b.AddConstant(mangled));
            return;
        }
        EmitExpression(m.Object);
        if (m.Optional)
        {
            _b.Emit(Opcode.Dup);                  // [obj, obj]
            var notNullish = _b.EmitJump(Opcode.JumpIfNotNullish); // pops one
            _b.Emit(Opcode.Pop);                  // []
            _b.Emit(Opcode.LoadUndefined);        // [undefined]
            RouteChainExitJump(_b.EmitJump(Opcode.Jump));
            _b.PatchJump(notNullish);             // [obj]
            if (m.Computed)
            {
                EmitNonChainSubexpression(m.Property);
                RecordPos(m);
                _b.Emit(Opcode.LoadComputed);
            }
            else
            {
                var optionalName = ((Identifier)m.Property).Name;
                RecordPos(m);
                _b.EmitProperty(Opcode.LoadProperty, _b.AddConstant(optionalName));
            }
            return;
        }
        if (m.Computed)
        {
            EmitNonChainSubexpression(m.Property);
            RecordPos(m);
            _b.Emit(Opcode.LoadComputed);
        }
        else
        {
            var name = ((Identifier)m.Property).Name;
            RecordPos(m);
            _b.EmitProperty(Opcode.LoadProperty, _b.AddConstant(name));
        }
    }

    private void EmitCall(CallExpression call)
    {
        var rootsChain = _chainExitJumps is null && HasOptionalLink(call);
        if (rootsChain)
        {
            _chainExitJumps = new List<int>();
        }

        try
        {
            EmitCallCore(call);
        }
        finally
        {
            if (rootsChain)
            {
                foreach (var j in _chainExitJumps!)
                {
                    _b.PatchJump(j);
                }

                _chainExitJumps = null;
            }
        }
    }

    private void EmitCallCore(CallExpression call)
    {
        if (call.Arguments.Count > 255)
        {
            throw new NotSupportedException("more than 255 call args not supported");
        }

        var hasSpread = false;
        foreach (var arg in call.Arguments)
        {
            if (arg is SpreadElement) { hasSpread = true; break; }
        }

        // super.method(args) — must bind this=current this.
        if (call.Callee is SuperPropertyExpression sp)
        {
            // Push this for receiver, then the resolved super method.
            _b.Emit(_classMethodDepth > 0 ? Opcode.LoadThisChecked : Opcode.LoadThis);  // [this]
            if (sp.Computed)
            {
                // wp:M3-04h — super[expr](args): evaluate the key and resolve via
                // LoadSuperComputed, leaving [this, fn] for the method dispatch.
                EmitExpression(sp.Property);                                            // [this, key]
                _b.Emit(Opcode.LoadSuperComputed);                                      // [this, fn]
            }
            else
            {
                var name = ((Identifier)sp.Property).Name;
                _b.EmitU16(Opcode.LoadSuperProperty, _b.AddConstant(name));             // [this, fn]
            }
            if (hasSpread)
            {
                EmitArgsAsArray(call.Arguments);
                RecordPos(call);
                _b.Emit(Opcode.CallApplyMethod);
                return;
            }
            foreach (var arg in call.Arguments)
            {
                EmitExpression(arg);
            }

            RecordPos(call);
            _b.Emit(Opcode.CallMethod, (byte)call.Arguments.Count);
            return;
        }

        // Method call form: obj.method() or obj[key]() must bind
        // this=obj inside the callee. Emit obj once, Dup it, load the
        // property, then args, then CallMethod which consumes
        // [receiver, callee, args...]. For plain calls, emit the callee
        // alone and route through Call (this=Undefined).
        if (call.Callee is MemberExpression me)
        {
            EmitExpression(me.Object);          // [obj]

            // §13.3 OptionalChain — `base?.method(args)`. A nullish base
            // short-circuits the WHOLE call to undefined: the property is not
            // loaded and the arguments are not evaluated. Without this the
            // method-call fast-path loaded `.method` off the nullish base
            // (undefined) and then issued CallMethod, throwing "not a function"
            // — e.g. Angular's `e.features?.forEach(...)`.
            if (me.Optional)
            {
                _b.Emit(Opcode.Dup);                                   // [obj, obj]
                var notNullish = _b.EmitJump(Opcode.JumpIfNotNullish); // pops one → [obj]
                _b.Emit(Opcode.Pop);                                   // [] (nullish base)
                _b.Emit(Opcode.LoadUndefined);                         // [undefined]
                RouteChainExitJump(_b.EmitJump(Opcode.Jump));          // skip the chain
                _b.PatchJump(notNullish);                              // [obj] (proceed)
            }

            _b.Emit(Opcode.Dup);                // [obj, obj]
            if (me.Computed)
            {
                EmitNonChainSubexpression(me.Property); // [obj, obj, key]
                RecordPos(me);
                _b.Emit(Opcode.LoadComputed);   // [obj, fn]
            }
            else if (me.Property is PrivateNameExpression pne)
            {
                var mangled = ResolvePrivateName(pne.Name, pne.Start);
                RecordPos(me);
                _b.EmitU16(Opcode.PrivateGet, _b.AddConstant(mangled));  // [obj, fn]
            }
            else
            {
                var nameIdx = _b.AddConstant(((Identifier)me.Property).Name);
                RecordPos(me);
                _b.EmitProperty(Opcode.LoadProperty, nameIdx);  // [obj, fn]
            }

            // §13.3 OptionalChain — `obj.method?.(args)`. The CALL is optional: a
            // nullish *method* short-circuits the whole call to undefined. `this`
            // still binds to obj when the method IS present (the method-call form
            // is preserved). This is distinct from me.Optional above, where a
            // nullish *base* short-circuits before the property is even loaded.
            if (call.Optional)
            {
                _b.Emit(Opcode.Dup);                                     // [obj, fn, fn]
                var fnNotNullish = _b.EmitJump(Opcode.JumpIfNotNullish); // pops one → [obj, fn]
                _b.Emit(Opcode.Pop);                                     // [obj]
                _b.Emit(Opcode.Pop);                                     // []
                _b.Emit(Opcode.LoadUndefined);                           // [undefined]
                RouteChainExitJump(_b.EmitJump(Opcode.Jump));            // skip the chain
                _b.PatchJump(fnNotNullish);                              // [obj, fn] (proceed)
            }

            if (hasSpread)
            {
                // Build args array first, then apply.
                EmitArgsAsArray(call.Arguments);
                RecordPos(call);
                _b.Emit(Opcode.CallApplyMethod);
                return;
            }
            foreach (var arg in call.Arguments)
            {
                EmitNonChainSubexpression(arg);
            }

            RecordPos(call);
            _b.Emit(Opcode.CallMethod, (byte)call.Arguments.Count);
            return;
        }

        // §14.11 / §9.1.1.2 — a bare-identifier call inside a `with` may resolve
        // through an object Environment Record. When it does, the call's `this`
        // binding is that binding object (WithBaseObject), not undefined. Emit a
        // method-style receiver: WithLoadMethodOrMiss pushes [withObj, fn] on a
        // hit (and jumps past the fallback); on a miss the fallback pushes
        // [undefined, fn], so CallMethod with an undefined receiver behaves like
        // a plain Call.
        if (!call.Optional && call.Callee is Identifier calleeId && ShouldRouteWith(calleeId.Name))
        {
            EmitWithGuarded(Opcode.WithLoadMethodOrMiss, calleeId.Name, () =>
            {
                _b.Emit(Opcode.LoadUndefined);   // [undefined]
                EmitIdLoadStatic(calleeId.Name); // [undefined, fn]
            });
            if (hasSpread)
            {
                EmitArgsAsArray(call.Arguments);
                RecordPos(call);
                _b.Emit(Opcode.CallApplyMethod);
                return;
            }
            foreach (var arg in call.Arguments)
            {
                EmitNonChainSubexpression(arg);
            }

            RecordPos(call);
            _b.Emit(Opcode.CallMethod, (byte)call.Arguments.Count);
            return;
        }

        // wp:M3-71 — §19.2.1.1 direct eval: the callee is a bare global-`eval`
        // IdentifierReference that does NOT resolve to a local/upvalue (so it is
        // the global slot) and is not subject to with-interception. Emit a
        // distinct DirectEval that hands the VM the current frame's lexical
        // context. The VM re-checks at runtime that the callee is still the realm
        // intrinsic; a reassigned global binding, `(0, x)(...)`, `window.x(...)`,
        // or a shadowed local binding never reaches here (or fails the runtime
        // check) and stays an ordinary indirect call.
        if (!call.Optional && !hasSpread
            && IsDirectEvalCallee(call.Callee, out var directEvalId)
            && !TryResolveLocal(directEvalId, out _)
            && !TryResolveUpvalue(directEvalId, out _)
            && !ShouldRouteWith(directEvalId))
        {
            // wp:M3-72 — snapshot the calling function's variable environment so
            // the VM can build the caller scope the eval'd code reads/writes and
            // run the §19.2.1.3 EvalDeclarationInstantiation early-error checks.
            var descIdx = _b.AddConstant(BuildEvalScopeDescriptor());
            // wp:M3-73 — this chunk lexically contains a direct eval, so a
            // non-strict frame built from it eagerly allocates a var store at
            // entry: var/function bindings the eval injects into this function's
            // variable environment are then visible to the rest of the frame AND
            // to closures it creates (which snapshot the store at creation).
            _b.HasDirectEval = true;
            EmitExpression(call.Callee);                  // [callee]
            foreach (var arg in call.Arguments)
            {
                EmitNonChainSubexpression(arg);
            }

            RecordPos(call);
            _b.EmitU16(Opcode.DirectEval, descIdx);
            _b.EmitU8Raw(call.Arguments.Count);
            return;
        }

        EmitExpression(call.Callee);

        // §13.3 OptionalChain — `callee?.(args)`. A nullish callee short-circuits
        // to undefined without evaluating the arguments. (A non-nullish callee
        // that isn't callable still throws "not a function", per spec.)
        if (call.Optional)
        {
            _b.Emit(Opcode.Dup);                                   // [callee, callee]
            var notNullish = _b.EmitJump(Opcode.JumpIfNotNullish); // pops one → [callee]
            _b.Emit(Opcode.Pop);                                   // [] (nullish callee)
            _b.Emit(Opcode.LoadUndefined);                         // [undefined]
            RouteChainExitJump(_b.EmitJump(Opcode.Jump));          // skip the chain
            _b.PatchJump(notNullish);                              // [callee] (proceed)
        }

        if (hasSpread)
        {
            EmitArgsAsArray(call.Arguments);
            RecordPos(call);
            _b.Emit(Opcode.CallApply);
            return;
        }
        foreach (var arg in call.Arguments)
        {
            EmitNonChainSubexpression(arg);
        }

        RecordPos(call);
        _b.Emit(Opcode.Call, (byte)call.Arguments.Count);
    }

    /// <summary>wp:M3-71 — is <paramref name="callee"/> a bare <c>eval</c>
    /// IdentifierReference (the only syntactic shape that can be a direct
    /// eval)? Returns the identifier name so the caller can verify it resolves
    /// to the global slot (not a local/upvalue).</summary>
    private static bool IsDirectEvalCallee(Expression callee, out string name)
    {
        if (callee is Identifier { Name: "eval" } id)
        {
            name = id.Name;
            return true;
        }
        name = string.Empty;
        return false;
    }

    /// <summary>wp:M3-72 — snapshot the calling function's currently-visible
    /// bindings (this function's open lexical scopes' locals, plus any names it
    /// has captured as upvalues from enclosing functions) into an
    /// <see cref="EvalScopeDescriptor"/>. The VM pairs each binding with the live
    /// frame storage at run time to build the EvalScope a direct eval reads.
    /// Innermost scope first so name shadowing resolves correctly.</summary>
    private EvalScopeDescriptor BuildEvalScopeDescriptor()
    {
        var bindings = new List<EvalScopeDescriptor.Binding>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // This function's own locals, innermost scope outward.
        for (var i = _scopes.Count - 1; i >= 0; i--)
        {
            foreach (var kv in _scopes[i])
            {
                if (!seen.Add(kv.Key))
                {
                    continue;
                }

                var slot = kv.Value;
                var captured = _b.IsCaptured(slot);
                var lexical = _lexicalScopes[i].Contains(kv.Key);
                bindings.Add(new EvalScopeDescriptor.Binding(
                    kv.Key,
                    captured ? EvalScopeDescriptor.Kind.LocalCell : EvalScopeDescriptor.Kind.LocalSlot,
                    slot, lexical, IsConstLocal(kv.Key)));
            }
        }
        // Names already captured from enclosing scopes (upvalues) — these live as
        // Cells in this frame's upvalue table.
        foreach (var kv in _upvalueByName)
        {
            if (!seen.Add(kv.Key))
            {
                continue;
            }

            bindings.Add(new EvalScopeDescriptor.Binding(
                kv.Key, EvalScopeDescriptor.Kind.Upvalue, kv.Value,
                IsLexicalUpvalue(kv.Value), IsImmutableUpvalue(kv.Value)));
        }
        return new EvalScopeDescriptor(bindings);
    }

    /// <summary>Materialise a possibly-spread argument list as a dense
    /// JsArray on the stack. Used by the CallApply / NewApply opcodes when
    /// at least one argument is a spread.</summary>
    private void EmitArgsAsArray(IReadOnlyList<Expression> args)
    {
        // Arguments are independent expressions, never links of an enclosing
        // optional chain — suspend any active chain context for the duration.
        var savedChain = _chainExitJumps;
        _chainExitJumps = null;
        try { EmitArgsAsArrayCore(args); }
        finally { _chainExitJumps = savedChain; }
    }

    private void EmitArgsAsArrayCore(IReadOnlyList<Expression> args)
    {
        _b.Emit(Opcode.NewArray);
        var pushMode = false;
        var nextIdx = 0;
        foreach (var arg in args)
        {
            if (arg is SpreadElement sp)
            {
                EmitExpression(sp.Argument);    // [arr, iterable]
                _b.Emit(Opcode.SpreadIterable); // [arr]
                pushMode = true;
                continue;
            }
            if (!pushMode)
            {
                _b.Emit(Opcode.Dup);            // [arr, arr]
                EmitExpression(arg);            // [arr, arr, v]
                _b.EmitProperty(Opcode.StoreProperty,
                    _b.AddConstant(nextIdx.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                _b.Emit(Opcode.Pop);            // [arr]
                nextIdx++;
            }
            else
            {
                _b.Emit(Opcode.Dup);            // [arr, arr]
                _b.Emit(Opcode.Dup);            // [arr, arr, arr]
                _b.EmitProperty(Opcode.LoadProperty, _b.AddConstant("length")); // [arr, arr, len]
                EmitExpression(arg);            // [arr, arr, len, v]
                _b.Emit(Opcode.StoreComputed);  // [arr, v]
                _b.Emit(Opcode.Pop);            // [arr]
            }
        }
    }

    private void EmitFunctionExpression(FunctionExpression fe, bool isArrow = false)
    {
        // Compile the body in a sub-compiler parented to this one so
        // free identifiers can be lazily resolved as upvalues captured
        // from this scope.
        var sub = new JsCompiler(parent: this);
        sub._b.IsStrict = fe.Strict;
        // wp:M3-64 — an arrow body inherits super/[[HomeObject]] lexically; mark
        // the chunk so the VM stamps the enclosing frame's home object onto the
        // arrow closure at creation time.
        sub._b.IsArrow = isArrow;
        // §14.11 / §10.2.1 — a function created lexically inside a `with` carries
        // the enclosing object Environment Records on its scope chain, so its
        // body resolves free identifiers with the with-objects consulted first.
        // (A strict body, e.g. one with its own "use strict", severs this since
        // `with` is sloppy-only; ConfigureWithCapture handles that.)
        sub.ConfigureWithCapture(_withDepth, fe.Strict);
        sub.RunCaptureAnalysisForFunction(fe.Params, fe.Body.Body);
        // wp:M3-81 — only a NON-arrow function's parameter default region is an
        // initializer context for the eval ContainsArguments rule; an arrow's own
        // parameter list is not (arrows have no own `arguments` binding).
        sub.BindFunctionParameters(fe.Params, markInitializer: !isArrow);
        // Pre-allocate captured vars as cell slots
        // BEFORE hoisting, so a hoisted inner function that writes an outer var
        // resolves it to the parent's cell slot rather than LoadGlobal/Store-
        // Global. Mirrors EmitProgram and the class-method body paths.
        sub.PreallocateCapturedVarBindings(fe.Body.Body);
        sub.HoistVarDeclarations(fe.Body.Body);
        // TDZ — instantiate top-level let/const of the body before compiling
        // hoisted function bodies, so those bodies capture same-scope class and
        // let/const names instead of resolving outer/global bindings.
        sub.HoistLexicalDeclarations(fe.Body.Body);
        // Function declarations nested in a function *expression* / arrow body
        // are function-scoped and hoisted to the top of the body (§14.1.18),
        // exactly as in EmitFunctionBody for function declarations. Without this
        // an inner `function f(){…}` inside an IIFE/expression is never bound, so
        // references to it compile to (undefined) globals — breaking all
        // IIFE/webpack/bundler code.
        sub.HoistFunctionDeclarations(fe.Body.Body);
        // wp:M3-20 — arrows have no own `arguments` (they inherit lexically),
        // so only ordinary function expressions synthesize one.
        if (!isArrow)
        {
            sub.MaybeBindArguments(fe.Params, fe.Body.Body);
        }
        // wp:M3-21 — §15.2.5. A NON-arrow named function *expression* binds its
        // own name inside the body to the function instance (for recursion /
        // self-reference). Run AFTER params + var/function bindings so a param
        // or body var/function of the same name shadows it (§10.2.11). Function
        // *declarations* bind in the enclosing scope (HoistFunctionDeclarations)
        // and must not double-bind here.
        if (!isArrow && fe.Name is not null)
        {
            sub.MaybeBindSelfName(fe.Name.Name);
        }
        // §10.2.1.1 — arrows have no own `this` binding; only an ordinary
        // function expression boxes `this` for a nested arrow that reads it.
        if (!isArrow)
        {
            sub.MaybeBindLexicalThis();
        }
        // §10.2.1.3 — synchronous parameter-binding prologue boundary for
        // generator / async (incl. async-arrow) bodies; see EmitFunctionBody.
        sub.EmitPrologueEndIfSuspendable(fe.Async, fe.Generator);
        sub._currentIsAsyncGenerator = fe.Async && fe.Generator;
        foreach (var s in fe.Body.Body)
        {
            sub.EmitStatement(s);
        }

        sub._b.Emit(Opcode.ReturnUndefined);
        // Per ES2024 §15.2 NamedEvaluation, anonymous FunctionExpression
        // produces `name === ""` (not "<anonymous>"); B2-2's Function intrinsic
        // surfaces this through the new-instance `name` slot.
        var name = fe.Name?.Name ?? "";
        var chunk = sub._b.Build(name);
        var kind = ResolveFunctionKind(fe.Async, fe.Generator);
        EmitFunctionConstructor(name, chunk, CountSimpleParams(fe.Params), sub._upvalues, kind, fe.SourceText);
    }

    /// <summary>B1b-2c — emit <c>yield expr</c> / <c>yield</c> / <c>yield* iter</c>.
    /// Pushes the value to yield, then emits a Suspend opcode (kind=0).
    /// On resume, the value passed to <c>.next(v)</c> is pushed back.</summary>
    private void EmitYield(YieldExpression yld)
    {
        if (yld.Delegate)
        {
            // yield* iter — push the iterable and hand off to the VM-level
            // YieldDelegate opcode, which implements §27.5.3.2 in full:
            // it forwards the outer generator's next/return/throw into the
            // inner iterator's matching method, exits when the inner says
            // done, and pushes the inner's value as the result of the
            // yield* expression.
            EmitExpression(yld.Argument!);
            _b.Emit(Opcode.YieldDelegate);
            // operand: 1 = async generator (use async iteration protocol,
            // §27.6.3.7), 0 = sync generator (§27.5.3.2).
            _b.EmitU8Raw((byte)(_currentIsAsyncGenerator ? 1 : 0));
            return;
        }

        // Simple yield: push the value to yield (or undefined), then Suspend.
        if (yld.Argument is not null)
        {
            EmitExpression(yld.Argument);
        }
        else
        {
            _b.Emit(Opcode.LoadUndefined);
        }

        _b.Emit(Opcode.Suspend);
        _b.EmitU8Raw(0); // kind = 0 (yield)
        // After resume, stack-top holds the value passed to .next(v); that
        // becomes the result of the yield expression.
    }

    /// <summary>B1b-2c — emit <c>await expr</c>. Pushes the awaited value,
    /// then Suspend with kind=1. On resume, the resolved value is on top
    /// (or a JsThrow propagates if the promise rejected).</summary>
    private void EmitAwait(AwaitExpression aw)
    {
        EmitExpression(aw.Argument);
        _b.Emit(Opcode.Suspend);
        _b.EmitU8Raw(1); // kind = 1 (await)
    }

    /// <summary>B1b-2c — map AST async/generator flags to runtime kind.</summary>
    private static Runtime.JsFunctionKind ResolveFunctionKind(bool isAsync, bool isGenerator)
    {
        if (isAsync && isGenerator)
        {
            return Runtime.JsFunctionKind.AsyncGenerator;
        }

        if (isAsync)
        {
            return Runtime.JsFunctionKind.Async;
        }

        if (isGenerator)
        {
            return Runtime.JsFunctionKind.Generator;
        }

        return Runtime.JsFunctionKind.Normal;
    }

    private void EmitObjectLiteral(ObjectExpression oe)
    {
        _b.Emit(Opcode.NewObject); // [obj]
        foreach (var prop in oe.Properties)
        {
            // Spread: copy enumerable own properties from the source onto obj.
            // The parser tags spreads with a SpreadElement value and an empty
            // sentinel identifier key.
            if (prop.Value is SpreadElement spread)
            {
                _b.Emit(Opcode.Dup);               // [obj, obj]
                EmitExpression(spread.Argument);   // [obj, obj, src]
                _b.Emit(Opcode.SpreadInto);        // [obj]
                continue;
            }
            // wp:M3-26 — accessor (getter/setter) shorthand. The value is the
            // accessor function; emit it then a Define{Getter,Setter} opcode
            // that installs an enumerable accessor descriptor (merging a paired
            // get/set on the same key). Reuses the runtime accessor machinery
            // the class compiler uses via InstallMethodOrAccessor.
            if (prop.Kind is MethodKind.Get or MethodKind.Set)
            {
                // The Define{Getter,Setter}* opcodes pop their inputs ABOVE the
                // object and push the object back, so the stack stays [obj] for
                // the next property — no Dup/Pop bracketing needed here.
                if (prop.Computed)
                {
                    EmitComputedKey(prop.Key);  // [obj, key]
                    _b.Emit(Opcode.ToPropertyKey); // [obj, key]
                    EmitExpression(prop.Value); // [obj, key, fn]
                    // wp:M3-64 — §13.2.5 MakeMethod: object-literal accessors get
                    // a [[HomeObject]] = the object so `super.x` resolves.
                    _b.Emit(Opcode.SetHomeObjectComputed); // [obj, key, fn]
                    _b.Emit(prop.Kind == MethodKind.Get
                        ? Opcode.DefineGetterComputed : Opcode.DefineSetterComputed); // [obj]
                }
                else
                {
                    var accIdx = prop.Key switch
                    {
                        Identifier id => _b.AddConstant(id.Name),
                        StringLiteral sl => _b.AddConstant(sl.Value),
                        NumericLiteral nl =>
                            _b.AddConstant(JsValue.ToStringValue(JsValue.Number(nl.Value))),
                        _ => throw new NotSupportedException(
                            $"accessor key kind '{prop.Key.GetType().Name}'"),
                    };
                    EmitExpression(prop.Value); // [obj, fn]
                    // wp:M3-64 — stamp [[HomeObject]] for the accessor.
                    _b.Emit(Opcode.SetHomeObject); // [obj, fn]
                    _b.EmitU16(prop.Kind == MethodKind.Get
                        ? Opcode.DefineGetter : Opcode.DefineSetter, accIdx); // [obj]
                }
                continue;
            }
            // wp:M3-26 — data property via CreateDataPropertyOrThrow (§13.2.5.5):
            // the DefineData* opcodes consume [obj, (key,) value] and push obj
            // back, defining a fresh own data property that OVERRIDES any prior
            // accessor on the key (so `{ get x(){…}, x: v }` ends as data `v`).
            if (prop.Computed)
            {
                EmitComputedKey(prop.Key);           // [obj, key]
                _b.Emit(Opcode.ToPropertyKey);       // [obj, key]
                EmitExpression(prop.Value);          // [obj, key, value]
                if (IsAnonymousFunctionDefinition(prop.Value))
                {
                    _b.Emit(Opcode.SetFunctionNameComputed); // [obj, key, value]
                }
                // wp:M3-64 — §13.2.5 MakeMethod: a concise method (`{ [k]() {} }`)
                // gets a [[HomeObject]] = the object so `super.x` resolves; a plain
                // data property (`{ [k]: fn }`) does NOT.
                if (prop.IsMethod)
                {
                    _b.Emit(Opcode.SetHomeObjectComputed); // [obj, key, value]
                }

                _b.Emit(Opcode.DefineDataComputed);  // [obj]
            }
            else
            {
                var propName = prop.Key switch
                {
                    Identifier id => id.Name,
                    StringLiteral sl => sl.Value,
                    NumericLiteral nl => JsValue.ToStringValue(JsValue.Number(nl.Value)),
                    _ => throw new NotSupportedException(
                        $"object key kind '{prop.Key.GetType().Name}'"),
                };
                if (IsObjectLiteralProtoSetter(prop, propName))
                {
                    EmitExpression(prop.Value);          // [obj, value]
                    _b.Emit(Opcode.SetObjectPrototype);  // [obj]
                    continue;
                }
                var nameIdx = _b.AddConstant(propName);
                // §named-evaluation — `{ x: function(){} }` names the function "x".
                EmitNamedEvaluation(prop.Value, propName);       // [obj, value]
                // wp:M3-64 — stamp [[HomeObject]] only for concise methods
                // (`{ foo() {} }`), not data properties (`{ foo: fn }`).
                if (prop.IsMethod)
                {
                    _b.Emit(Opcode.SetHomeObject); // [obj, value]
                }

                _b.EmitU16(Opcode.DefineDataProperty, nameIdx);  // [obj]
            }
        }
    }

    private static bool IsObjectLiteralProtoSetter(ObjectProperty prop, string propName)
        => propName == "__proto__"
            && !prop.Computed
            && !prop.Shorthand
            && !prop.IsMethod
            && prop.Kind == MethodKind.Method;

    private enum RestExclusionKind { Constant, Local }
    private readonly record struct RestExclusion(RestExclusionKind Kind, string? Name, int Slot);

    private static bool IsPattern(Expression e) => e switch
    {
        ArrayExpression => true,
        ObjectExpression => true,
        BindingPattern => true,
        AssignmentExpression { Op: JsTokenKind.Eq } a => IsPattern(a.Target),
        AssignmentPattern a => IsPattern(a.Target),
        _ => false,
    };

    /// <summary>wp:M3-20 — §10.4.4. If a non-arrow function body references the
    /// identifier <c>arguments</c> without binding it (no param/var named
    /// <c>arguments</c>), reserve a local for it and emit
    /// <see cref="Opcode.MakeArguments"/> so the VM materializes the arguments
    /// object at entry. Must run AFTER <see cref="BindFunctionParameters"/> and
    /// the captured-var pre-allocation so any explicit <c>arguments</c> binding
    /// already occupies the scope (in which case we do nothing — the user's
    /// binding wins, §10.2.11). When a nested arrow captures <c>arguments</c>,
    /// the slot is boxed into a Cell first so the closure shares the object.</summary>
    private void MaybeBindArguments(IReadOnlyList<Expression> parameters, IReadOnlyList<Statement> body)
    {
        // An explicit param/var/function named `arguments` shadows the implicit
        // one — the scope already owns the name, so don't synthesize.
        if (_scopes.Any(s => s.ContainsKey("arguments")))
        {
            return;
        }

        if (!CaptureAnalysis.ReferencesArguments(parameters, body))
        {
            return;
        }

        var slot = _b.ReserveLocal();
        _scopes[^1]["arguments"] = slot;
        if (IsNameCaptured("arguments"))
        {
            // A nested arrow reads `arguments` — back it with a Cell so the
            // closure and this frame observe one shared object.
            _b.MarkCaptured(slot);
            _b.EmitSlot(Opcode.InitCellLocal, slot);
        }

        // wp:M3-80 — §10.4.4.6 CreateMappedArgumentsObject. A non-strict function
        // with a simple parameter list (every formal a plain identifier) gets the
        // MAPPED arguments object: each index is live-linked to its parameter's
        // local slot. Strict mode or any non-simple formal keeps the unmapped form.
        if (!_b.IsStrict && _paramsAreSimple && _paramSlots is { } paramSlots)
        {
            // Last-wins per spec: when a name repeats (allowed in non-strict
            // simple lists), only the final index for that name is mapped.
            var lastIndexOfName = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < parameters.Count; i++)
            {
                lastIndexOfName[((Identifier)parameters[i]).Name] = i;
            }

            _b.EmitU16(Opcode.MakeMappedArguments, slot);
            _b.EmitU16Raw(parameters.Count);
            for (var i = 0; i < parameters.Count; i++)
            {
                var name = ((Identifier)parameters[i]).Name;
                // 0xFFFF marks an unmapped index (shadowed earlier duplicate).
                _b.EmitU16Raw(lastIndexOfName[name] == i ? paramSlots[i] : 0xFFFF);
            }
            return;
        }

        _b.EmitSlot(Opcode.MakeArguments, slot);
    }

    /// <summary>§10.2.1.1 — when a nested arrow reads <c>this</c>, materialize
    /// this (non-arrow) function's <c>this</c> into a captured Cell under the
    /// synthetic <see cref="LexicalThisName"/> binding so the arrow closure
    /// resolves it as an upvalue. Run in the prologue (after parameter binding)
    /// once <c>LoadThis</c> is valid. For a base/ordinary function the
    /// frame's <c>this</c> is already bound here; a derived constructor binds
    /// <c>this</c> only at <c>super()</c>, so its store is (re)emitted after
    /// <see cref="Opcode.BindThis"/> via <see cref="StoreLexicalThisCell"/>.</summary>
    private void MaybeBindLexicalThis()
    {
        if (!IsNameCaptured(LexicalThisName))
        {
            return;
        }

        var slot = _b.ReserveLocal();
        _scopes[^1][LexicalThisName] = slot;
        _b.MarkCaptured(slot);
        _b.EmitSlot(Opcode.InitCellLocal, slot);
        // Seed the cell with the current `this` (a no-op store of the TDZ-bound
        // value for a derived ctor, which re-stores after super()).
        _b.Emit(Opcode.LoadThis);
        _b.EmitSlot(Opcode.StoreCellLocal, slot);
    }

    /// <summary>Re-store the (now bound) <c>this</c> into the lexical-<c>this</c>
    /// Cell after a derived constructor's <c>super()</c> binds it. No-op when no
    /// nested arrow captured <c>this</c>.</summary>
    private void StoreLexicalThisCell()
    {
        if (!_scopes[^1].TryGetValue(LexicalThisName, out var slot))
        {
            return;
        }

        _b.Emit(Opcode.LoadThis);
        _b.EmitSlot(Opcode.StoreCellLocal, slot);
    }

    /// <summary>wp:M3-21 — §15.2.5 InstantiateOrdinaryFunctionExpression. Bind a
    /// named function <em>expression</em>'s own name inside its body to the
    /// executing function instance, so the body can refer to itself for
    /// recursion / self-reference (e.g. <c>var f = function g(){ return g; }</c>).
    /// Must run AFTER <see cref="BindFunctionParameters"/>, the captured-var
    /// pre-allocation, and function-declaration hoisting so any param or body
    /// <c>var</c>/function of the same name already owns the scope — in which
    /// case we do nothing (the user's binding shadows the self-name, §10.2.11).
    /// When a nested closure (incl. arrow) captures the name, the slot is boxed
    /// into a Cell first so the closure shares the binding. The VM's
    /// <see cref="Opcode.BindCallee"/> writes <c>currentFunction</c> into the
    /// slot at body entry.</summary>
    private void MaybeBindSelfName(string name)
    {
        // A param / var / function declaration of the same name shadows the
        // self-name — the scope already owns it, so don't synthesize.
        if (_scopes.Any(s => s.ContainsKey(name)))
        {
            return;
        }

        var slot = _b.ReserveLocal();
        _scopes[^1][name] = slot;
        if (IsNameCaptured(name))
        {
            // A nested closure references the self-name — back it with a Cell so
            // the closure and this frame observe the same binding.
            _b.MarkCaptured(slot);
            _b.EmitSlot(Opcode.InitCellLocal, slot);
        }
        _b.EmitSlot(Opcode.BindCallee, slot);
    }

    private void BindFunctionParameters(IReadOnlyList<Expression> parameters, bool markInitializer = false)
    {
        // wp:M3-81 — §sec-performeval-rules-in-initializer: a direct eval inside a
        // (non-arrow) function's parameter default initializer is subject to the
        // ContainsArguments early error. Bracket the parameter-binding prologue so
        // the VM's DirectEval applies the check while a default is being evaluated.
        // Arrows pass markInitializer=false because they have no own `arguments`
        // binding — UNLESS the arrow's own parameter list explicitly binds the name
        // `arguments` (an `arguments` formal parameter), in which case the rule
        // applies just as for an ordinary function with its own arguments binding.
        // Only emit the markers when a default actually exists, to keep the common
        // (no-default) parameter prologue byte-for-byte unchanged.
        var bracket = (markInitializer || ParamsBindArguments(parameters))
            && parameters.Any(HasParamDefault);
        if (bracket)
        {
            _b.Emit(Opcode.EnterInitializer);
        }

        var argSlots = new int[parameters.Count];
        Array.Fill(argSlots, -1);

        // wp:M3-80 — record the per-index parameter slots and whether the list is
        // simple (every formal a plain identifier) so MaybeBindArguments can build
        // the mapped arguments object's parameter map.
        _paramSlots = argSlots;
        _paramsAreSimple = parameters.All(p => p is Identifier);

        // VM argument copying fills local slots 0..argc-1, so reserve every
        // positional parameter slot before declaring destructured binding locals.
        for (var i = 0; i < parameters.Count; i++)
        {
            if (parameters[i] is SpreadElement)
            {
                break;
            }

            argSlots[i] = _b.ReserveLocal();
        }

        for (var i = 0; i < parameters.Count; i++)
        {
            var param = parameters[i];
            if (param is SpreadElement spread)
            {
                // §10.2.11 — the rest parameter binds an Array of every argument
                // from this positional index onward. Declare its binding(s),
                // gather args[i..argc) via RestParam, then bind the array to the
                // target (a plain identifier or a destructuring pattern). Rest
                // is always the last parameter, so `i` is the gather start.
                DeclarePatternBindings(spread.Argument);
                _b.EmitU16(Opcode.RestParam, i);
                EmitPatternFromStack(spread.Argument, isDeclaration: true);
                continue;
            }

            if (param is Identifier id)
            {
                _scopes[^1][id.Name] = argSlots[i];
                // If the parameter is captured by a
                // nested function, promote its argument value into a Cell so
                // writes from the nested function propagate back.
                if (IsNameCaptured(id.Name))
                {
                    _b.MarkCaptured(argSlots[i]);
                    _b.EmitSlot(Opcode.PromoteParamCell, argSlots[i]);
                }
                continue;
            }

            DeclarePatternBindings(param);
            EmitPatternFromLocal(param, argSlots[i], isDeclaration: true);
        }

        if (bracket)
        {
            _b.Emit(Opcode.ExitInitializer);
        }
    }

    /// <summary>wp:M3-81 — does any parameter in <paramref name="parameters"/>
    /// bind an identifier whose name is <c>arguments</c>? Lets an arrow function
    /// whose own parameter list explicitly introduces an <c>arguments</c> binding
    /// (e.g. <c>(p = …, arguments) => {}</c>) still trigger the
    /// eval-inside-initializer ContainsArguments early error — the arrow now HAS
    /// an own <c>arguments</c> binding (in its parameter scope) that a direct eval
    /// declaring <c>arguments</c> in a sibling default would collide with.</summary>
    private static bool ParamsBindArguments(IReadOnlyList<Expression> parameters)
    {
        foreach (var p in parameters)
        {
            if (ParamBindsArguments(p))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ParamBindsArguments(Expression? p)
    {
        switch (p)
        {
            case null: return false;
            case Identifier id: return id.Name == "arguments";
            case AssignmentExpression { Op: JsTokenKind.Eq } a: return ParamBindsArguments(a.Target);
            case AssignmentPattern a: return ParamBindsArguments(a.Target);
            case SpreadElement sp: return ParamBindsArguments(sp.Argument);
            case RestElement re: return ParamBindsArguments(re.Argument);
            case ArrayPattern arr:
                foreach (var el in arr.Elements)
                {
                    switch (el)
                    {
                        case ArrayPatternBindingElement b:
                            if (ParamBindsArguments(b.Target))
                            {
                                return true;
                            }

                            break;
                        case ArrayPatternRestElement r:
                            if (ParamBindsArguments(r.Target))
                            {
                                return true;
                            }

                            break;
                    }
                }
                return false;
            case ObjectPattern obj:
                foreach (var prop in obj.Properties)
                {
                    if (ParamBindsArguments(prop.Target))
                    {
                        return true;
                    }
                }

                return obj.Rest is not null && ParamBindsArguments(obj.Rest.Argument);
            default:
                return false;
        }
    }

    /// <summary>wp:M3-81 — does this formal parameter carry a default initializer,
    /// either directly (<c>x = …</c>) or nested inside a destructuring pattern
    /// (<c>[x = …]</c> / <c>{ x = … }</c> / <c>...[x = …]</c>)? Drives whether the
    /// parameter-binding prologue needs the EnterInitializer/ExitInitializer
    /// markers for the eval-inside-initializer ContainsArguments rule.</summary>
    private static bool HasParamDefault(Expression? p)
    {
        switch (p)
        {
            case null:
            case Identifier:
                return false;
            case AssignmentExpression { Op: JsTokenKind.Eq }:
            case AssignmentPattern:
                return true;
            case SpreadElement sp: return HasParamDefault(sp.Argument);
            case RestElement re: return HasParamDefault(re.Argument);
            case ArrayPattern arr:
                foreach (var el in arr.Elements)
                {
                    switch (el)
                    {
                        case ArrayPatternBindingElement b:
                            if (b.Default is not null || HasParamDefault(b.Target))
                            {
                                return true;
                            }

                            break;
                        case ArrayPatternRestElement r:
                            if (HasParamDefault(r.Target))
                            {
                                return true;
                            }

                            break;
                    }
                }
                return false;
            case ObjectPattern obj:
                foreach (var prop in obj.Properties)
                {
                    if (prop.Default is not null || HasParamDefault(prop.Target))
                    {
                        return true;
                    }
                }

                return obj.Rest is not null && HasParamDefault(obj.Rest.Argument);
            case ArrayExpression aex:
                foreach (var el in aex.Elements)
                {
                    if (HasParamDefault(el))
                    {
                        return true;
                    }
                }

                return false;
            case ObjectExpression oex:
                foreach (var prop in oex.Properties)
                {
                    if (HasParamDefault(prop.Value))
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    /// <summary>Reserve local slots / register scope bindings for every name in
    /// a binding pattern (declaration side), before the initializer is walked.</summary>
    /// <param name="pattern">The binding pattern (identifier, array/object
    /// pattern, or the cover-grammar expression forms reinterpreted as targets).</param>
    /// <param name="functionScoped">True for <c>var</c> bindings (§14.3.2):
    /// the name binds in the function-variable scope (<c>_scopes[0]</c>), not a
    /// block-local slot. A block-local slot would shadow the function-top cell
    /// that <see cref="PreallocateCapturedVarBindings"/> reserves for a captured
    /// <c>var</c>, stranding the closure's binding — the initializer would write
    /// the block-local slot while the closure reads the (never-initialized)
    /// cell, yielding <c>undefined</c>/<c>NaN</c>. <c>let</c>/<c>const</c> and
    /// catch/parameter bindings stay block-scoped (false).</param>
    private void DeclarePatternBindings(Expression pattern, bool functionScoped = false)
    {
        switch (pattern)
        {
            case Identifier id:
                // gap:script-top-var-not-global — at script top, a `var`
                // binding becomes an own data property on the global object
                // (§16.1.7 ScriptEvaluation → CreateGlobalVarBinding). Emit
                // the idempotent declare opcode so a redeclaration without
                // an initializer doesn't reset an existing value, and a
                // hoisted function-decl that already installed the name
                // keeps its function value.
                if (IsScriptTop && !_directEvalLocalVars)
                {
                    // wp:M3-73 — a non-strict direct eval whose caller is a
                    // function injects its top-level vars into the caller's
                    // var-environment, not the global object. The binding was
                    // already pre-declared into the caller's eval-introduced var
                    // store (EmitProgram), or already exists as a caller binding;
                    // either way nothing to declare here.
                    if (_evalInjectVars)
                    {
                        return;
                    }

                    _b.EmitU16(Opcode.DeclareGlobalVar, _b.AddConstant(id.Name));
                    return;
                }
                // `var` is function-scoped: bind in _scopes[0]. If it's already
                // there (e.g. preallocated as a captured-var cell, or a prior
                // var/param of the same name), reuse it — no shadowing block slot.
                var scope = functionScoped ? _scopes[0] : _scopes[^1];
                if (!scope.ContainsKey(id.Name))
                {
                    var slot = _b.ReserveLocal();
                    scope[id.Name] = slot;
                    // Captured bindings use a Cell.
                    if (IsNameCaptured(id.Name))
                    {
                        _b.MarkCaptured(slot);
                        _b.EmitSlot(Opcode.InitCellLocal, slot);
                    }
                    else
                    {
                        _b.EmitSlot(Opcode.DeclareLocal, slot);
                    }
                }
                return;
            case AssignmentExpression { Op: JsTokenKind.Eq } a:
                DeclarePatternBindings(a.Target, functionScoped);
                return;
            case AssignmentPattern a:
                DeclarePatternBindings(a.Target, functionScoped);
                return;
            case ArrayPattern arr:
                foreach (var element in arr.Elements)
                {
                    switch (element)
                    {
                        case ArrayPatternBindingElement binding:
                            DeclarePatternBindings(binding.Target, functionScoped);
                            break;
                        case ArrayPatternRestElement rest:
                            DeclarePatternBindings(rest.Target, functionScoped);
                            break;
                    }
                }
                return;
            case ObjectPattern obj:
                foreach (var prop in obj.Properties)
                {
                    DeclarePatternBindings(prop.Target, functionScoped);
                }

                if (obj.Rest is not null)
                {
                    DeclarePatternBindings(obj.Rest.Argument, functionScoped);
                }

                return;
            case ArrayExpression arr:
                foreach (var element in arr.Elements)
                {
                    if (element is null)
                    {
                        continue;
                    }

                    DeclarePatternBindings(element is SpreadElement spread ? spread.Argument : element, functionScoped);
                }
                return;
            case ObjectExpression obj:
                foreach (var prop in obj.Properties)
                {
                    if (prop.Value is SpreadElement spread)
                    {
                        DeclarePatternBindings(spread.Argument, functionScoped);
                    }
                    else
                    {
                        DeclarePatternBindings(prop.Value, functionScoped);
                    }
                }
                return;
            case SpreadElement spread:
                DeclarePatternBindings(spread.Argument, functionScoped);
                return;
        }
    }

    private void EmitPatternFromLocal(Expression pattern, int sourceSlot, bool isDeclaration)
    {
        _b.EmitSlot(Opcode.LoadLocal, sourceSlot);
        EmitPatternFromStack(pattern, isDeclaration);
    }

    /// <summary>
    /// Lower a destructuring binding pattern whose source value is already on the
    /// stack, identical to <see cref="EmitPatternFromStack"/> but used by the
    /// module compiler. The module path reserves every bound name as an upvalue
    /// cell (see <c>JsCompiler.Modules.cs</c>), so the leaf-identifier store in
    /// <see cref="StoreBindingIdentifier"/> resolves through <c>StoreUpvalue</c>
    /// automatically — no shadowing local is declared (no
    /// <c>DeclarePatternBindings</c> call), which keeps module live bindings
    /// intact. This is just a named seam so the module compiler does not depend
    /// on the private leaf helper directly.
    /// </summary>
    internal void EmitDestructuringFromStack(Expression pattern) =>
        EmitPatternFromStack(pattern, isDeclaration: true);

    private void EmitPatternFromStack(Expression pattern, bool isDeclaration)
    {
        switch (pattern)
        {
            case Identifier id:
                StoreBindingIdentifier(id.Name);
                return;
            case MemberExpression me:
                StoreMemberTarget(me);
                return;
            case AssignmentExpression { Op: JsTokenKind.Eq } a:
                EmitDefaultedPattern(a.Target, a.Value, isDeclaration);
                return;
            case AssignmentPattern a:
                EmitDefaultedPattern(a.Target, a.Default, isDeclaration);
                return;
            case ArrayPattern arr:
                EmitArrayPattern(arr, isDeclaration);
                return;
            case ObjectPattern obj:
                EmitObjectPattern(obj, isDeclaration);
                return;
            case ArrayExpression arr:
                EmitArrayPattern(arr, isDeclaration);
                return;
            case ObjectExpression obj:
                EmitObjectPattern(obj, isDeclaration);
                return;
            case SpreadElement spread:
                EmitPatternFromStack(spread.Argument, isDeclaration);
                return;
            case RestElement rest:
                EmitPatternFromStack(rest.Argument, isDeclaration);
                return;
            default:
                throw new Parse.JsParseException($"invalid destructuring target '{pattern.GetType().Name}'", pattern.Start);
        }
    }

    private void EmitDefaultedPattern(Expression target, Expression fallback, bool isDeclaration)
    {
        var valueSlot = _b.ReserveLocal();
        _b.EmitSlot(Opcode.StoreLocal, valueSlot);
        _b.EmitSlot(Opcode.LoadLocal, valueSlot);
        _b.Emit(Opcode.LoadUndefined);
        _b.Emit(Opcode.StrictEq);
        var skipDefault = _b.EmitJump(Opcode.JumpIfFalse);
        // §named-evaluation — `[x = function(){}]` / `{ x = () => {} }` /
        // `({ x = class {} } = obj)`: when the default applies to a simple
        // identifier target and is an anonymous function definition, the
        // function adopts the binding's name.
        if (target is Identifier tid)
        {
            EmitNamedEvaluation(fallback, tid.Name);
        }
        else
        {
            EmitExpression(fallback);
        }

        _b.EmitSlot(Opcode.StoreLocal, valueSlot);
        _b.PatchJump(skipDefault);
        EmitPatternFromLocal(target, valueSlot, isDeclaration);
    }

    private void StoreBindingIdentifier(string name)
    {
        // Captured-local writes route through the cell.
        if (TryResolveLocal(name, out var slot))
        {
            // §13.15.2 — a write to a const local outside its own initializer is
            // a runtime TypeError (e.g. a destructuring-assignment leaf or a
            // for-of/in re-bind onto a const name from an outer scope).
            if (IsConstLocal(name) && !_inLexicalDeclInit)
            {
                _b.EmitU16(Opcode.ThrowConstAssignment, _b.AddConstant(name));
                return;
            }
            // TDZ — a write to a lexical binding before initialization throws,
            // UNLESS this store is the declaration's own initializer (which is
            // what transitions the binding out of the TDZ).
            if (IsLexicalLocal(name) && !_inLexicalDeclInit)
            {
                if (IsSlotCaptured(slot))
                {
                    _b.EmitSlot(Opcode.StoreCellLocalChecked, slot);
                }
                else
                {
                    EmitTdzLocalStore(name, slot);
                }
            }
            else
            {
                EmitStoreLocalSlot(slot);
            }
        }
        else if (TryResolveUpvalue(name, out var upIdx))
        {
            // §16.2.1.6.2 — an assignment to an immutable binding (an import, or a
            // module `const`) is a runtime TypeError. The binding's own
            // initializer is exempt (a `const`/destructuring init runs with
            // _inLexicalDeclInit set); any other store is a user assignment.
            if (IsImmutableUpvalue(upIdx) && !_inLexicalDeclInit)
            {
                _b.EmitU16(Opcode.ThrowConstAssignment, _b.AddConstant(name));
                return;
            }
            _b.EmitUpvalue(
                IsLexicalUpvalue(upIdx) && !_inLexicalDeclInit
                    ? Opcode.StoreUpvalueChecked : Opcode.StoreUpvalue, upIdx);
        }
        else
        {
            _b.EmitU16(Opcode.StoreGlobal, _b.AddConstant(name));
        }
    }

    /// <summary>TDZ — emit a checked store to a NON-captured lexical local. A
    /// non-captured lexical lives in a plain slot (not a Cell), but the
    /// checked-store opcodes only operate on cells, so a write-before-init
    /// check for a plain-slot lexical would need a slot-read first. Such a
    /// write is exceedingly rare (it requires assigning to a block-local
    /// let/const before its declaration in straight-line code, which the
    /// parser usually rejects or which is a genuine TDZ violation). We model it
    /// by reading the slot through the checked load (which throws on the
    /// sentinel) for its side effect, discarding the loaded value, then storing
    /// — preserving evaluation order (value already on the stack below).</summary>
    private void EmitTdzLocalStore(string name, int slot)
    {
        _ = name;
        // Stack on entry: [value]. Verify the binding is initialized: a checked
        // load throws ReferenceError if the slot still holds the sentinel.
        _b.EmitSlot(Opcode.LoadLocalChecked, slot); // [value, current] (throws if TDZ)
        _b.Emit(Opcode.Pop);                        // [value]
        _b.EmitSlot(Opcode.StoreLocal, slot);       // []
    }

    private void StoreMemberTarget(MemberExpression me)
    {
        var valueSlot = _b.ReserveLocal();
        _b.EmitSlot(Opcode.StoreLocal, valueSlot);
        EmitExpression(me.Object);
        // §13.15.5.5 / §13.3.7 — a private member destructuring target stores
        // through PrivateSet (brand-checked), which pops [obj, value] and
        // re-pushes value; discard the re-pushed value to balance the stack.
        if (!me.Computed && me.Property is PrivateNameExpression pne)
        {
            var mangled = ResolvePrivateName(pne.Name, pne.Start);
            _b.EmitSlot(Opcode.LoadLocal, valueSlot);
            _b.EmitU16(Opcode.PrivateSet, _b.AddConstant(mangled));
            _b.Emit(Opcode.Pop);
            return;
        }
        if (me.Computed)
        {
            EmitExpression(me.Property);
        }

        _b.EmitSlot(Opcode.LoadLocal, valueSlot);
        if (me.Computed)
        {
            _b.Emit(Opcode.StoreComputed);
        }
        else
        {
            _b.EmitProperty(Opcode.StoreProperty, _b.AddConstant(((Identifier)me.Property).Name));
        }

        _b.Emit(Opcode.Pop);
    }

    /// <summary>One normalized array-pattern element: a hole (elision), a
    /// binding/assignment element (with optional default), or a rest element.</summary>
    private enum ArrayElemKind { Hole, Element, Rest }
    private readonly record struct ArrayElem(ArrayElemKind Kind, Expression? Target, Expression? Default);

    private void EmitArrayPattern(ArrayExpression arr, bool isDeclaration)
    {
        var elems = new List<ArrayElem>(arr.Elements.Count);
        foreach (var element in arr.Elements)
        {
            switch (element)
            {
                case null:
                    elems.Add(new ArrayElem(ArrayElemKind.Hole, null, null));
                    break;
                case SpreadElement spread:
                    elems.Add(new ArrayElem(ArrayElemKind.Rest, spread.Argument, null));
                    break;
                case AssignmentExpression { Op: JsTokenKind.Eq } a:
                    elems.Add(new ArrayElem(ArrayElemKind.Element, a.Target, a.Value));
                    break;
                case AssignmentPattern ap:
                    elems.Add(new ArrayElem(ArrayElemKind.Element, ap.Target, ap.Default));
                    break;
                default:
                    elems.Add(new ArrayElem(ArrayElemKind.Element, element, null));
                    break;
            }
        }
        EmitArrayPatternIter(elems, isDeclaration);
    }

    private void EmitArrayPattern(ArrayPattern arr, bool isDeclaration)
    {
        var elems = new List<ArrayElem>(arr.Elements.Count);
        foreach (var element in arr.Elements)
        {
            switch (element)
            {
                case ArrayPatternHole:
                    elems.Add(new ArrayElem(ArrayElemKind.Hole, null, null));
                    break;
                case ArrayPatternRestElement rest:
                    elems.Add(new ArrayElem(ArrayElemKind.Rest, rest.Target, null));
                    break;
                case ArrayPatternBindingElement binding:
                    elems.Add(new ArrayElem(ArrayElemKind.Element, binding.Target, binding.Default));
                    break;
            }
        }
        EmitArrayPatternIter(elems, isDeclaration);
    }

    /// <summary>§8.5.3 IteratorBindingInitialization / §13.15.5.3
    /// IteratorDestructuringAssignmentEvaluation — lower an array pattern to the
    /// iterator protocol: <c>GetIterator</c> (TypeError on a non-iterable RHS),
    /// step per element, then <c>IteratorClose</c> on completion. Closing fires
    /// the iterator's <c>return()</c> when the pattern consumed fewer elements
    /// than the iterator yields, or on an abrupt completion while binding (the
    /// element-binding work is wrapped in a try region that closes the iterator
    /// then rethrows). When the iterator is exhausted exactly (or a rest element
    /// drains it) the record is already Done, so the close is a no-op.</summary>
    private void EmitArrayPatternIter(List<ArrayElem> elems, bool isDeclaration)
    {
        // The RHS value is on the stack. GetIterator → handle in a local.
        _b.Emit(Opcode.GetIterator);
        var handleSlot = _b.ReserveLocal();
        _b.EmitSlot(Opcode.StoreLocal, handleSlot);

        // Wrap the element-binding work in a try region so an abrupt completion
        // (a default that throws, a nested pattern that throws, a non-object
        // iterator-result, etc.) still runs IteratorClose before propagating.
        _b.Emit(Opcode.EnterTry);
        var catchOperandPos = _b.Position;
        _b.EmitI32Raw(-1);              // catch offset (patched below)
        _b.EmitI32Raw(-1);              // finally offset (none → -1)

        _tryDepth++;
        try
        {
            foreach (var elem in elems)
            {
                switch (elem.Kind)
                {
                    case ArrayElemKind.Hole:
                        // Elision still advances the iterator.
                        _b.EmitSlot(Opcode.LoadLocal, handleSlot);
                        _b.Emit(Opcode.IteratorBindNext);
                        _b.Emit(Opcode.Pop);
                        break;
                    case ArrayElemKind.Rest:
                        _b.EmitSlot(Opcode.LoadLocal, handleSlot);
                        _b.Emit(Opcode.IteratorRest);
                        EmitPatternFromStack(elem.Target!, isDeclaration);
                        break;
                    case ArrayElemKind.Element:
                        // §13.15.5.5 AssignmentElement — for an assignment
                        // pattern (not a declaration) whose target is a member
                        // reference, the reference (`obj` and a computed key)
                        // must be evaluated BEFORE the iterator is stepped, so a
                        // throwing reference does not call next().
                        if (!isDeclaration && elem.Target is MemberExpression meTarget)
                        {
                            EmitOrderedMemberElement(meTarget, elem.Default, handleSlot);
                        }
                        else
                        {
                            _b.EmitSlot(Opcode.LoadLocal, handleSlot);
                            _b.Emit(Opcode.IteratorBindNext);   // [value]
                            if (elem.Default is null)
                            {
                                EmitPatternFromStack(elem.Target!, isDeclaration);
                            }
                            else
                            {
                                EmitDefaultedPattern(elem.Target!, elem.Default, isDeclaration);
                            }
                        }
                        break;
                }
            }
        }
        finally
        {
            _tryDepth--;
        }

        _b.Emit(Opcode.LeaveTry);
        // Normal completion: close the iterator if it is not already Done
        // (i.e. the pattern consumed fewer elements than the iterator yields).
        _b.EmitSlot(Opcode.LoadLocal, handleSlot);
        _b.Emit(Opcode.IteratorClose);
        var jPastCatch = _b.EmitJump(Opcode.Jump);

        // Catch: the thrown value is on the stack (sp reset to the try base).
        // Close the iterator (swallowing return()-errors so the original throw
        // wins) and rethrow.
        var catchDelta = _b.Position - (catchOperandPos + 8);
        _b.PatchI32(catchOperandPos, catchDelta);
        _b.EmitSlot(Opcode.LoadLocal, handleSlot);  // [thrown, handle]
        _b.Emit(Opcode.IteratorCloseForThrow);      // [thrown]
        _b.Emit(Opcode.Throw);                       // rethrow

        _b.PatchJump(jPastCatch);
    }

    /// <summary>§13.15.5.5 AssignmentElement with a member-expression target:
    /// evaluate the target reference (object, and the computed key) FIRST, then
    /// step the iterator, apply any default, and store — so a throwing reference
    /// expression aborts before <c>next()</c> is ever called.</summary>
    private void EmitOrderedMemberElement(MemberExpression me, Expression? fallback, int handleSlot)
    {
        // Pre-evaluate the reference into temp slots.
        var objSlot = _b.ReserveLocal();
        EmitExpression(me.Object);
        _b.EmitSlot(Opcode.StoreLocal, objSlot);
        var keySlot = -1;
        if (me.Computed)
        {
            keySlot = _b.ReserveLocal();
            EmitExpression(me.Property);
            _b.EmitSlot(Opcode.StoreLocal, keySlot);
        }

        // Step the iterator into a value temp.
        var valueSlot = _b.ReserveLocal();
        _b.EmitSlot(Opcode.LoadLocal, handleSlot);
        _b.Emit(Opcode.IteratorBindNext);   // [value]
        _b.EmitSlot(Opcode.StoreLocal, valueSlot);

        // Apply default when the stepped value is undefined.
        if (fallback is not null)
        {
            _b.EmitSlot(Opcode.LoadLocal, valueSlot);
            _b.Emit(Opcode.LoadUndefined);
            _b.Emit(Opcode.StrictEq);
            var skip = _b.EmitJump(Opcode.JumpIfFalse);
            EmitExpression(fallback);   // member targets get no name inference
            _b.EmitSlot(Opcode.StoreLocal, valueSlot);
            _b.PatchJump(skip);
        }

        // Store value into the pre-evaluated reference.
        _b.EmitSlot(Opcode.LoadLocal, objSlot);
        if (me.Computed)
        {
            _b.EmitSlot(Opcode.LoadLocal, keySlot);
        }

        _b.EmitSlot(Opcode.LoadLocal, valueSlot);
        // §13.3.7 — a private member target stores through PrivateSet (brand-
        // checked); it pops [obj, value] and re-pushes value, which we discard.
        if (!me.Computed && me.Property is PrivateNameExpression pne)
        {
            var mangled = ResolvePrivateName(pne.Name, pne.Start);
            _b.EmitU16(Opcode.PrivateSet, _b.AddConstant(mangled));
        }
        else if (me.Computed)
        {
            _b.Emit(Opcode.StoreComputed);
        }
        else
        {
            _b.EmitProperty(Opcode.StoreProperty, _b.AddConstant(((Identifier)me.Property).Name));
        }

        _b.Emit(Opcode.Pop);
    }

    private void EmitObjectPattern(ObjectExpression obj, bool isDeclaration)
    {
        // §13.15.5.2 / §8.5.2 step 1 — RequireObjectCoercible(value): object
        // destructuring of null/undefined is a TypeError.
        _b.Emit(Opcode.RequireObjectCoercible);
        var srcSlot = _b.ReserveLocal();
        _b.EmitSlot(Opcode.StoreLocal, srcSlot);
        var exclusions = new List<RestExclusion>();
        foreach (var prop in obj.Properties)
        {
            if (prop.Value is SpreadElement spread)
            {
                EmitObjectRest(srcSlot, exclusions, spread.Argument, isDeclaration);
                continue;
            }

            EmitObjectPatternPropertyLoad(srcSlot, prop.Key, prop.Computed, exclusions);
            EmitPatternFromStack(prop.Value, isDeclaration);
        }
    }

    private void EmitObjectPattern(ObjectPattern obj, bool isDeclaration)
    {
        // §8.5.2 step 1 — RequireObjectCoercible(value).
        _b.Emit(Opcode.RequireObjectCoercible);
        var srcSlot = _b.ReserveLocal();
        _b.EmitSlot(Opcode.StoreLocal, srcSlot);
        var exclusions = new List<RestExclusion>();
        foreach (var prop in obj.Properties)
        {
            EmitObjectPatternPropertyLoad(srcSlot, prop.Key, prop.Computed, exclusions);
            if (prop.Default is null)
            {
                EmitPatternFromStack(prop.Target, isDeclaration);
            }
            else
            {
                EmitDefaultedPattern(prop.Target, prop.Default, isDeclaration);
            }
        }
        if (obj.Rest is not null)
        {
            EmitObjectRest(srcSlot, exclusions, obj.Rest.Argument, isDeclaration);
        }
    }

    private void EmitObjectPatternPropertyLoad(int srcSlot, Expression key, bool computed, List<RestExclusion> exclusions)
    {
        if (computed)
        {
            var keySlot = _b.ReserveLocal();
            EmitExpression(key);
            _b.EmitSlot(Opcode.StoreLocal, keySlot);
            _b.EmitSlot(Opcode.LoadLocal, srcSlot);
            _b.EmitSlot(Opcode.LoadLocal, keySlot);
            _b.Emit(Opcode.LoadComputed);
            exclusions.Add(new RestExclusion(RestExclusionKind.Local, null, keySlot));
        }
        else
        {
            var name = PropertyName(key);
            _b.EmitSlot(Opcode.LoadLocal, srcSlot);
            _b.EmitProperty(Opcode.LoadProperty, _b.AddConstant(name));
            exclusions.Add(new RestExclusion(RestExclusionKind.Constant, name, -1));
        }
    }

    private void EmitObjectRest(int srcSlot, List<RestExclusion> exclusions, Expression target, bool isDeclaration)
    {
        _b.EmitSlot(Opcode.LoadLocal, srcSlot);
        foreach (var ex in exclusions)
        {
            if (ex.Kind == RestExclusionKind.Constant)
            {
                _b.EmitU16(Opcode.LoadConst, _b.AddConstant(ex.Name!));
            }
            else
            {
                _b.EmitSlot(Opcode.LoadLocal, ex.Slot);
            }
        }
        _b.EmitU16(Opcode.RestObject, exclusions.Count);
        EmitPatternFromStack(target, isDeclaration);
    }

    private void EmitArrayLiteral(ArrayExpression ae)
    {
        // B2-4: arrays are dense JsArray exotics, so the magic length slot is
        // derived from indexed slots. Literal element creation uses
        // CreateDataProperty semantics, not ordinary assignment, so inherited
        // non-writable prototype indexes cannot block a fresh array's elements.
        // B3-2: spread elements dispatch through SpreadIterable, which walks
        // @@iterator and appends directly.
        _b.Emit(Opcode.NewArray);
        // Track the next dense index that a plain element should land at.
        // Once a spread runs, subsequent plain elements use Array.prototype.push
        // semantics via a length-derived index — implemented here by reading
        // the current length back from the array. Track via a "pushMode"
        // flag: once true, every plain element goes through the same length+
        // index loop.
        var pushMode = false;
        for (var i = 0; i < ae.Elements.Count; i++)
        {
            var element = ae.Elements[i];
            if (element is null)
            {
                // Hole: skip but make sure length advances if it's the last hole.
                continue;
            }
            if (element is SpreadElement spread)
            {
                // SpreadIterable peeks the target — no Dup needed.
                EmitExpression(spread.Argument); // [arr, iterable]
                _b.Emit(Opcode.SpreadIterable); // [arr]
                pushMode = true;
                continue;
            }
            if (!pushMode)
            {
                _b.Emit(Opcode.Dup);
                EmitExpression(element);
                _b.EmitU16(Opcode.DefineDataProperty, _b.AddConstant(i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                _b.Emit(Opcode.Pop);
            }
            else
            {
                // Append via CreateDataProperty(arr, arr.length, value).
                _b.Emit(Opcode.Dup);            // [arr, arr]
                _b.Emit(Opcode.Dup);            // [arr, arr, arr]
                _b.EmitProperty(Opcode.LoadProperty, _b.AddConstant("length")); // [arr, arr, len]
                EmitExpression(element);        // [arr, arr, len, value]
                _b.Emit(Opcode.DefineDataComputed); // [arr, arr]
                _b.Emit(Opcode.Pop);            // [arr]
            }
        }
        // Pad trailing holes (null elements in the AST = elided slots) by
        // bumping length up via the magic property. JsArray.SetLength handles
        // grow-with-undefined. Skipped when pushMode is active since length
        // already tracks pushed values.
        if (!pushMode && ae.Elements.Count > 0 && ae.Elements[^1] is null)
        {
            _b.Emit(Opcode.Dup);
            _b.EmitU16(Opcode.LoadConst, _b.AddConstant((double)ae.Elements.Count));
            _b.EmitProperty(Opcode.StoreProperty, _b.AddConstant("length"));
            _b.Emit(Opcode.Pop);
        }
    }

    private static string PropertyName(Expression key) => key switch
    {
        Identifier id => id.Name,
        StringLiteral s => s.Value,
        NumericLiteral n => JsValue.ToStringValue(JsValue.Number(n.Value)),
        _ => throw new NotSupportedException($"object pattern key kind '{key.GetType().Name}'"),
    };

    private void EmitNew(NewExpression ne)
    {
        EmitExpression(ne.Callee);
        var hasSpread = false;
        foreach (var arg in ne.Arguments)
        {
            if (arg is SpreadElement) { hasSpread = true; break; }
        }

        if (hasSpread)
        {
            EmitArgsAsArray(ne.Arguments);
            RecordPos(ne);
            _b.Emit(Opcode.NewApply);
            return;
        }
        foreach (var arg in ne.Arguments)
        {
            EmitExpression(arg);
        }

        if (ne.Arguments.Count > 255)
        {
            throw new NotSupportedException("more than 255 new args not supported");
        }

        RecordPos(ne);
        _b.Emit(Opcode.New, (byte)ne.Arguments.Count);
    }

    private static Opcode BinaryOpToOpcode(JsTokenKind op) => op switch
    {
        JsTokenKind.Plus => Opcode.Add,
        JsTokenKind.Minus => Opcode.Sub,
        JsTokenKind.Star => Opcode.Mul,
        JsTokenKind.Slash => Opcode.Div,
        JsTokenKind.Percent => Opcode.Mod,
        JsTokenKind.StarStar => Opcode.Pow,
        JsTokenKind.Pipe => Opcode.BitOr,
        JsTokenKind.Amp => Opcode.BitAnd,
        JsTokenKind.Caret => Opcode.BitXor,
        JsTokenKind.LtLt => Opcode.Shl,
        JsTokenKind.GtGt => Opcode.Shr,
        JsTokenKind.GtGtGt => Opcode.Ushr,
        JsTokenKind.EqEq => Opcode.Eq,
        JsTokenKind.BangEq => Opcode.NEq,
        JsTokenKind.EqEqEq => Opcode.StrictEq,
        JsTokenKind.BangEqEq => Opcode.StrictNEq,
        JsTokenKind.Lt => Opcode.Lt,
        JsTokenKind.LtEq => Opcode.LtEq,
        JsTokenKind.Gt => Opcode.Gt,
        JsTokenKind.GtEq => Opcode.GtEq,
        JsTokenKind.Instanceof => Opcode.Instanceof,
        JsTokenKind.In => Opcode.In,
        _ => throw new NotSupportedException($"binary op '{op}'"),
    };

    private static Opcode UnaryOpToOpcode(JsTokenKind op) => op switch
    {
        JsTokenKind.Minus => Opcode.Neg,
        JsTokenKind.Plus => Opcode.UnaryPlus,
        JsTokenKind.Bang => Opcode.Not,
        JsTokenKind.Tilde => Opcode.BitNot,
        JsTokenKind.Typeof => Opcode.TypeOf,
        _ => throw new NotSupportedException($"unary op '{op}'"),
    };

    private static Opcode CompoundOpToBinaryOpcode(JsTokenKind op) => op switch
    {
        JsTokenKind.PlusEq => Opcode.Add,
        JsTokenKind.MinusEq => Opcode.Sub,
        JsTokenKind.StarEq => Opcode.Mul,
        JsTokenKind.SlashEq => Opcode.Div,
        JsTokenKind.PercentEq => Opcode.Mod,
        JsTokenKind.StarStarEq => Opcode.Pow,
        JsTokenKind.PipeEq => Opcode.BitOr,
        JsTokenKind.AmpEq => Opcode.BitAnd,
        JsTokenKind.CaretEq => Opcode.BitXor,
        JsTokenKind.LtLtEq => Opcode.Shl,
        JsTokenKind.GtGtEq => Opcode.Shr,
        JsTokenKind.GtGtGtEq => Opcode.Ushr,
        _ => throw new NotSupportedException($"compound op '{op}'"),
    };

    /// <summary>The three ES2021 logical assignment operators (§13.15.2).
    /// Unlike the arithmetic/bitwise compound ops these short-circuit, so
    /// they need a dedicated conditional-jump lowering in
    /// <see cref="EmitLogicalAssignment"/> rather than the binary-op path.</summary>
    private static bool IsLogicalAssignOp(JsTokenKind op) => op is JsTokenKind.PipePipeEq or JsTokenKind.AmpAmpEq or JsTokenKind.QuestionQuestionEq;

    /// <summary>Map a logical assignment operator to the conditional jump that
    /// detects its short-circuit case (when no assignment occurs and the
    /// current value is the result):
    /// <list type="bullet">
    ///   <item><c>||=</c> short-circuits when the current value is truthy →
    ///         <see cref="Opcode.JumpIfTrue"/>.</item>
    ///   <item><c>&amp;&amp;=</c> short-circuits when falsy →
    ///         <see cref="Opcode.JumpIfFalse"/>.</item>
    ///   <item><c>??=</c> short-circuits when non-nullish →
    ///         <see cref="Opcode.JumpIfNotNullish"/>.</item>
    /// </list>
    /// Each of these jumps POPS the test operand, so the lowering Dups the
    /// current value first (mirroring <see cref="EmitLogical"/>).</summary>
    private static Opcode LogicalAssignShortCircuitJump(JsTokenKind op) => op switch
    {
        JsTokenKind.PipePipeEq => Opcode.JumpIfTrue,
        JsTokenKind.AmpAmpEq => Opcode.JumpIfFalse,
        JsTokenKind.QuestionQuestionEq => Opcode.JumpIfNotNullish,
        _ => throw new NotSupportedException($"logical assign op '{op}'"),
    };
}
