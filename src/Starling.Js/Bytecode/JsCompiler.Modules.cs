using Starling.Js.Ast;
using Starling.Js.Modules;
using Starling.Js.Runtime;

namespace Starling.Js.Bytecode;

/// <summary>
/// ES2024 §16.2 module-record compilation. Lowers a parsed module
/// <see cref="Program"/> into a <see cref="Chunk"/> plus the static
/// import/export metadata the linker needs.
/// </summary>
/// <remarks>
/// <para>
/// All module handling lives in this partial so the classic-script compiler in
/// <c>JsCompiler.cs</c> stays untouched (a sibling agent edits that file for
/// destructuring assignment). The two paths never overlap: classic scripts route
/// top-level bindings to the realm global; modules route every top-level binding
/// to a <see cref="Cell"/> upvalue supplied by the loader at instantiation.
/// </para>
/// <para>
/// <b>Live bindings.</b> Each top-level binding (local declaration <em>and</em>
/// imported name) maps to an upvalue slot. The loader allocates one
/// <see cref="Cell"/> per local binding and reuses the exporting module's cell
/// for an imported name, so reads/writes on either side observe the same
/// storage — the ECMAScript indirect-binding contract. <c>LoadUpvalue</c> /
/// <c>StoreUpvalue</c> (already supported by the VM) do the dereferencing.
/// </para>
/// <para>
/// <b>Top-level await.</b> A module whose body contains a top-level
/// <c>await</c> (an <see cref="AwaitExpression"/> — or a <c>for await</c> loop —
/// that is NOT nested inside a non-async function/method/arrow) evaluates
/// asynchronously. The compiler detects this
/// (<see cref="ModuleCompilation.HasTopLevelAwait"/>); the loader then builds the
/// body <see cref="Runtime.JsFunction"/> with <see cref="Runtime.JsFunctionKind.Async"/>,
/// so the VM's existing <c>StartAsyncBody</c>/<c>ScheduleAwait</c> machinery
/// suspends the body at each <c>await</c> and the body returns a Promise. Modules
/// with no top-level await keep <see cref="Runtime.JsFunctionKind.Normal"/> and
/// the synchronous fast path. Dynamic <c>import()</c> and <c>import.meta</c>
/// lower to dedicated opcodes handled by the module loader.
/// </para>
/// </remarks>
public sealed partial class JsCompiler
{
    /// <summary>Module top-level binding name → upvalue index. Populated before
    /// emission so <see cref="EmitIdLoad(string, bool)"/> / <see cref="StoreBindingIdentifier"/>
    /// resolve module bindings as upvalues (cells) rather than globals.</summary>
    private Dictionary<string, int>? _moduleBindingUpvalues;

    /// <summary>TDZ — module top-level binding names that are <em>lexical</em>
    /// (<c>let</c>/<c>const</c>/<c>class</c> and every imported binding). Reads
    /// and pre-init writes of these go through the TDZ-checked upvalue opcodes so
    /// access before initialization throws a ReferenceError (§16.2.1.6.2). A
    /// <c>var</c>/function binding is NOT lexical (var is initialized to
    /// <c>undefined</c>, functions are hoisted) and uses the plain opcodes.</summary>
    private HashSet<string>? _moduleLexicalBindings;

    /// <summary>Module top-level binding names that are <em>immutable</em> — i.e.
    /// imported bindings. An assignment to one is a runtime TypeError
    /// (§16.2.1.6.2: imported bindings are immutable), emitted as
    /// <see cref="Opcode.ThrowConstAssignment"/>.</summary>
    private HashSet<string>? _moduleImmutableBindings;

    /// <summary>Compile a module body. Returns the chunk plus the static
    /// import/export tables. The chunk expects one upvalue per entry in the
    /// returned binding-order list (locals first, then imports), supplied by the
    /// loader as <see cref="Cell"/>s at instantiation.</summary>
    public static ModuleCompilation CompileModule(Program program, string? name = "<module>")
    {
        // Parent a throwaway root so IsScriptTop is false (top-level bindings
        // become non-global) and the upvalue machinery is available.
        var root = new JsCompiler();
        var c = new JsCompiler(parent: root)
        {
            _moduleBindingUpvalues = new(StringComparer.Ordinal),
            _moduleLexicalBindings = new(StringComparer.Ordinal),
            _moduleImmutableBindings = new(StringComparer.Ordinal),
        };
        c._b.IsStrict = true; // §11.2.2 — module code is always strict
        c._b.SourcePath = name; // wp:M3-63 — referrer for dynamic import() + import.meta.url
        return c.EmitModule(program, name);
    }

    private ModuleCompilation EmitModule(Program program, string? name)
    {
        var imports = new List<ModuleImportEntry>();
        var exports = new List<ModuleExportEntry>();
        var requested = new List<string>();
        var bindingOrder = new List<ModuleBindingSlot>();

        CollectImportEntries(program.Body, imports, requested);
        CollectExportEntries(program.Body, exports, requested);

        // 1) Reserve an upvalue slot for every local top-level binding name…
        //    distinguishing lexical (let/const/class/default) from var/function
        //    so the former get TDZ-checked access and a sentinel-seeded cell, and
        //    const from the rest so an assignment to it is a TypeError.
        var localNames = new List<string>();
        var lexicalLocals = new HashSet<string>(StringComparer.Ordinal);
        var constLocals = new HashSet<string>(StringComparer.Ordinal);
        CollectLocalBindingNames(program.Body, localNames, lexicalLocals, constLocals);
        foreach (var local in localNames)
        {
            ReserveModuleBinding(local, isImport: false, bindingOrder,
                isLexical: lexicalLocals.Contains(local),
                isImmutable: constLocals.Contains(local));
        }

        // 2) …then one per imported local name (each resolves to the exporting
        //    module's cell at instantiation). Imported bindings are immutable and
        //    are TDZ-checked (they mirror the source module's lexical binding).
        foreach (var imp in imports)
        {
            ReserveModuleBinding(imp.LocalName, isImport: true, bindingOrder, imp,
                isLexical: true);
        }

        // Hoist module-top function declarations (they bind into the upvalue
        // cell so importers see them, and so they are callable before their
        // textual position).
        HoistModuleFunctionDeclarations(program.Body);

        foreach (var stmt in program.Body)
        {
            EmitModuleItem(stmt);
        }

        _b.Emit(Opcode.Halt);
        var chunk = _b.Build(name);

        // wp:M3-03b — does the module body use top-level await? Detection runs
        // over the AST (not bytecode) so it can distinguish a top-level await
        // from one nested in a non-async function. The loader uses this to build
        // the body as an Async-kind function.
        var hasTopLevelAwait = ModuleBodyHasTopLevelAwait(program.Body);

        return new ModuleCompilation(chunk, imports, exports, requested, bindingOrder, hasTopLevelAwait);
    }

    // -----------------------------------------------------------------------
    // wp:M3-03b — top-level await detection (ES2024 §16.2.1.6.2 "Module body
    // contains await"). An await is "top-level" when it appears in the module
    // body's own execution context — i.e. NOT inside a nested function, method,
    // accessor, or arrow (each of those introduces its own [[Async]] context).
    // A `for await (… of …)` loop counts as a top-level await. await inside a
    // nested async function is that function's await and is NOT top-level.
    // -----------------------------------------------------------------------

    /// <summary>True when the module body contains a top-level
    /// <c>await</c> / <c>for await</c> not nested in any function boundary.</summary>
    private static bool ModuleBodyHasTopLevelAwait(IReadOnlyList<Statement> body)
    {
        foreach (var s in body)
        {
            if (StatementHasTopLevelAwait(s))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StatementHasTopLevelAwait(Statement s)
    {
        switch (s)
        {
            // for await (… of …) — the loop itself is a top-level await.
            case ForOfStatement { Await: true }:
                return true;

            case BlockStatement b:
                return ModuleBodyHasTopLevelAwait(b.Body);
            case ExpressionStatement es:
                return ExprHasTopLevelAwait(es.Expression);
            case ReturnStatement rs:
                return rs.Argument is not null && ExprHasTopLevelAwait(rs.Argument);
            case ThrowStatement ts:
                return ExprHasTopLevelAwait(ts.Argument);
            case IfStatement ifs:
                return ExprHasTopLevelAwait(ifs.Test)
                    || StatementHasTopLevelAwait(ifs.Consequent)
                    || (ifs.Alternate is not null && StatementHasTopLevelAwait(ifs.Alternate));
            case WhileStatement ws:
                return ExprHasTopLevelAwait(ws.Test) || StatementHasTopLevelAwait(ws.Body);
            case DoWhileStatement dws:
                return StatementHasTopLevelAwait(dws.Body) || ExprHasTopLevelAwait(dws.Test);
            case ForStatement fs:
                return (fs.Init is Expression fi && ExprHasTopLevelAwait(fi))
                    || (fs.Init is Statement fis && StatementHasTopLevelAwait(fis))
                    || (fs.Test is not null && ExprHasTopLevelAwait(fs.Test))
                    || (fs.Update is not null && ExprHasTopLevelAwait(fs.Update))
                    || StatementHasTopLevelAwait(fs.Body);
            case ForInStatement fis2:
                return (fis2.Left is Expression le && ExprHasTopLevelAwait(le))
                    || (fis2.Left is Statement ls && StatementHasTopLevelAwait(ls))
                    || ExprHasTopLevelAwait(fis2.Right)
                    || StatementHasTopLevelAwait(fis2.Body);
            case ForOfStatement fos:
                return (fos.Left is Expression loe && ExprHasTopLevelAwait(loe))
                    || (fos.Left is Statement los && StatementHasTopLevelAwait(los))
                    || ExprHasTopLevelAwait(fos.Right)
                    || StatementHasTopLevelAwait(fos.Body);
            case SwitchStatement sw:
                if (ExprHasTopLevelAwait(sw.Discriminant))
                {
                    return true;
                }

                foreach (var c in sw.Cases)
                {
                    if (c.Test is not null && ExprHasTopLevelAwait(c.Test))
                    {
                        return true;
                    }

                    foreach (var cs in c.Consequent)
                    {
                        if (StatementHasTopLevelAwait(cs))
                        {
                            return true;
                        }
                    }
                }
                return false;
            case TryStatement tr:
                return StatementHasTopLevelAwait(tr.Block)
                    || (tr.Handler is not null && StatementHasTopLevelAwait(tr.Handler.Body))
                    || (tr.Finalizer is not null && StatementHasTopLevelAwait(tr.Finalizer));
            case LabeledStatement ls2:
                return StatementHasTopLevelAwait(ls2.Body);
            case WithStatement ws2:
                // `with` is sloppy-only (modules are strict) — included for
                // completeness so the traversal is total.
                return ExprHasTopLevelAwait(ws2.Object) || StatementHasTopLevelAwait(ws2.Body);
            case VariableDeclaration vd:
                foreach (var d in vd.Declarations)
                {
                    if (d.Init is not null && ExprHasTopLevelAwait(d.Init))
                    {
                        return true;
                    }
                }

                return false;

            // A class declaration's `extends` heritage clause and any computed
            // member keys are evaluated in the *enclosing* (module top-level)
            // context, so a top-level await there marks the module async. Method
            // bodies and field initializers introduce their own context and do
            // NOT count.
            case ClassDeclaration cd:
                return ClassHasTopLevelAwait(cd.BaseClass, cd.Body);

            // Module items: a declaration's initializer can hold a top-level
            // await (e.g. `export const x = await f();`).
            case ExportLocalDeclaration eld:
                return StatementHasTopLevelAwait(eld.Declaration);
            case ExportDefaultDeclaration edd:
                return edd.Declaration switch
                {
                    // `export default class extends (await x) {}` / computed keys.
                    ClassExpression ce => ClassHasTopLevelAwait(ce.BaseClass, ce.Body),
                    // `export default function …` introduces its own context.
                    FunctionExpression => false,
                    // `export default <AssignmentExpression>` is module top-level.
                    Expression dde => ExprHasTopLevelAwait(dde),
                    _ => false,
                };

            // FunctionDeclaration introduces its own context; its body is NOT
            // module top-level. import/export-spec lists,
            // empty/break/continue/debugger: no top-level await.
            default:
                return false;
        }
    }

    private static bool ExprHasTopLevelAwait(Expression e)
    {
        switch (e)
        {
            case AwaitExpression:
                return true;

            // Function boundaries — anything inside introduces a new execution
            // context, so awaits within are NOT module top-level.
            case FunctionExpression:
            case ArrowFunctionExpression:
                return false;

            // A class expression's `extends` heritage and computed member keys
            // are evaluated in the enclosing context — a top-level await there
            // is module top-level. Bodies/initializers are not (see helper).
            case ClassExpression ce:
                return ClassHasTopLevelAwait(ce.BaseClass, ce.Body);

            case UnaryExpression u: return ExprHasTopLevelAwait(u.Argument);
            case UpdateExpression up: return ExprHasTopLevelAwait(up.Argument);
            case BinaryExpression b: return ExprHasTopLevelAwait(b.Left) || ExprHasTopLevelAwait(b.Right);
            case LogicalExpression lg: return ExprHasTopLevelAwait(lg.Left) || ExprHasTopLevelAwait(lg.Right);
            case AssignmentExpression a: return ExprHasTopLevelAwait(a.Target) || ExprHasTopLevelAwait(a.Value);
            case ConditionalExpression c:
                return ExprHasTopLevelAwait(c.Test) || ExprHasTopLevelAwait(c.Consequent) || ExprHasTopLevelAwait(c.Alternate);
            case SequenceExpression seq:
                foreach (var x in seq.Expressions)
                {
                    if (ExprHasTopLevelAwait(x))
                    {
                        return true;
                    }
                }

                return false;
            case MemberExpression m:
                return ExprHasTopLevelAwait(m.Object) || (m.Computed && ExprHasTopLevelAwait(m.Property));
            case CallExpression call:
                if (ExprHasTopLevelAwait(call.Callee))
                {
                    return true;
                }

                foreach (var arg in call.Arguments)
                {
                    if (ExprHasTopLevelAwait(arg))
                    {
                        return true;
                    }
                }

                return false;
            case NewExpression ne:
                if (ExprHasTopLevelAwait(ne.Callee))
                {
                    return true;
                }

                foreach (var arg in ne.Arguments)
                {
                    if (ExprHasTopLevelAwait(arg))
                    {
                        return true;
                    }
                }

                return false;
            case SpreadElement sp: return ExprHasTopLevelAwait(sp.Argument);
            case ArrayExpression arr:
                foreach (var el in arr.Elements)
                {
                    if (el is not null && ExprHasTopLevelAwait(el))
                    {
                        return true;
                    }
                }

                return false;
            case ObjectExpression obj:
                foreach (var p in obj.Properties)
                {
                    if (p.Computed && ExprHasTopLevelAwait(p.Key))
                    {
                        return true;
                    }

                    if (ExprHasTopLevelAwait(p.Value))
                    {
                        return true;
                    }
                }
                return false;
            case TemplateLiteral tl:
                foreach (var x in tl.Expressions)
                {
                    if (ExprHasTopLevelAwait(x))
                    {
                        return true;
                    }
                }

                return false;
            case TaggedTemplateExpression tte:
                if (ExprHasTopLevelAwait(tte.Tag))
                {
                    return true;
                }

                foreach (var x in tte.Quasi.Expressions)
                {
                    if (ExprHasTopLevelAwait(x))
                    {
                        return true;
                    }
                }

                return false;
            case SuperPropertyExpression sup:
                return sup.Computed && ExprHasTopLevelAwait(sup.Property);
            case SuperCallExpression sc:
                foreach (var arg in sc.Arguments)
                {
                    if (ExprHasTopLevelAwait(arg))
                    {
                        return true;
                    }
                }

                return false;

            // Literals, identifiers, this, private-name refs, yield (not legal at
            // module top level): no nested top-level await.
            default:
                return false;
        }
    }

    /// <summary>True when a class's <c>extends</c> heritage clause or any computed
    /// member key contains a top-level <c>await</c>. Those sub-expressions are
    /// evaluated in the enclosing (module top-level) context per §15.7
    /// ClassDefinitionEvaluation, so an await there makes the module async. Method
    /// bodies and field initializers run in their own context and are excluded.</summary>
    private static bool ClassHasTopLevelAwait(Expression? baseClass, ClassBody body)
    {
        if (baseClass is not null && ExprHasTopLevelAwait(baseClass))
        {
            return true;
        }

        if (body.Constructor is { Computed: true } ctor && ExprHasTopLevelAwait(ctor.Key))
        {
            return true;
        }

        foreach (var m in body.Methods)
        {
            if (m.Computed && ExprHasTopLevelAwait(m.Key))
            {
                return true;
            }
        }

        foreach (var f in body.Fields)
        {
            if (f.Computed && ExprHasTopLevelAwait(f.Key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Register a module top-level binding name as an upvalue. The
    /// returned index is the slot the loader fills with the binding's
    /// <see cref="Cell"/>.</summary>
    private void ReserveModuleBinding(
        string name, bool isImport, List<ModuleBindingSlot> order,
        ModuleImportEntry? import = null, bool isLexical = false, bool isImmutable = false)
    {
        if (_moduleBindingUpvalues!.ContainsKey(name))
        {
            return; // first binding wins
        }

        var idx = order.Count;
        _moduleBindingUpvalues[name] = idx;
        _upvalues.Add(new UpvalueRef(IsLocalCapture: false, Index: idx));
        _upvalueByName[name] = idx;
        if (isLexical)
        {
            _moduleLexicalBindings!.Add(name);
        }
        // An imported binding (§16.2.1.6.2) and a local `const` are immutable: an
        // assignment to either is a runtime TypeError.
        if (isImport || isImmutable)
        {
            _moduleImmutableBindings!.Add(name);
        }
        // Only a local lexical (let/const/class) cell is seeded with the TDZ
        // sentinel by the loader; an imported binding aliases the source's cell
        // (which the source seeds), so it is lexical-for-reads but not sentinel-
        // seeded here.
        order.Add(new ModuleBindingSlot(name, isImport, import, IsLexical: isLexical && !isImport));
    }

    // -----------------------------------------------------------------------
    // Static metadata collection (ES2024 §16.2.1.3 / §16.2.1.4)
    // -----------------------------------------------------------------------

    private static void CollectImportEntries(
        IReadOnlyList<Statement> body, List<ModuleImportEntry> imports, List<string> requested)
    {
        foreach (var s in body)
        {
            if (s is not ImportDeclaration imp)
            {
                continue;
            }

            AddRequested(requested, imp.Source);
            foreach (var spec in imp.Specifiers)
            {
                switch (spec)
                {
                    case ImportDefaultSpecifier d:
                        imports.Add(new ModuleImportEntry(imp.Source, "default", d.Local.Name));
                        break;
                    case ImportNamespaceSpecifier ns:
                        imports.Add(new ModuleImportEntry(imp.Source, ModuleImportEntry.NamespaceImport, ns.Local.Name));
                        break;
                    case ImportNamedSpecifier named:
                        imports.Add(new ModuleImportEntry(imp.Source, ModuleExportName(named.Imported), named.Local.Name));
                        break;
                    case ImportSideEffectSpecifier:
                        break; // import "x"; — dependency only, no binding
                }
            }
        }
    }

    private static void CollectExportEntries(
        IReadOnlyList<Statement> body, List<ModuleExportEntry> exports, List<string> requested)
    {
        foreach (var s in body)
        {
            switch (s)
            {
                case ExportLocalDeclaration local:
                    foreach (var nameOfLocal in DeclaredNames(local.Declaration))
                    {
                        exports.Add(new ModuleExportEntry(nameOfLocal, null, null, nameOfLocal));
                    }

                    break;
                case ExportDefaultDeclaration:
                    exports.Add(new ModuleExportEntry("default", null, null, DefaultBindingName));
                    break;
                case ExportNamedDeclaration named:
                    if (named.Source is not null)
                    {
                        AddRequested(requested, named.Source);
                    }

                    foreach (var spec in named.Specifiers)
                    {
                        var exportName = ModuleExportName(spec.Exported);
                        if (named.Source is null)
                        {
                            exports.Add(new ModuleExportEntry(exportName, null, null, ModuleExportName(spec.Local)));
                        }
                        else
                        {
                            exports.Add(new ModuleExportEntry(exportName, named.Source, ModuleExportName(spec.Local), null));
                        }
                    }
                    break;
                case ExportAllDeclaration all:
                    AddRequested(requested, all.Source);
                    exports.Add(new ModuleExportEntry(
                        all.ExportedName?.Name, all.Source, ModuleExportEntry.StarReexport, null));
                    break;
            }
        }
    }

    /// <summary>Local binding name for the synthetic <c>default</c> export.</summary>
    internal const string DefaultBindingName = "*default*";

    private static void AddRequested(List<string> requested, string spec)
    {
        if (!requested.Contains(spec))
        {
            requested.Add(spec);
        }
    }

    private static string ModuleExportName(Expression e) => e switch
    {
        Identifier id => id.Name,
        StringLiteral s => s.Value,
        _ => throw new InvalidOperationException("unsupported module export name node"),
    };

    /// <summary>Names introduced by a local export declaration (var/let/const,
    /// function, class).</summary>
    private static IEnumerable<string> DeclaredNames(Statement declaration)
    {
        switch (declaration)
        {
            case VariableDeclaration vd:
                foreach (var d in vd.Declarations)
                {
                    foreach (var n in PatternNames(d.Id))
                    {
                        yield return n;
                    }
                }

                break;
            case FunctionDeclaration fd: yield return fd.Name.Name; break;
            case ClassDeclaration cd: yield return cd.Name.Name; break;
        }
    }

    private static IEnumerable<string> PatternNames(Expression pattern)
    {
        switch (pattern)
        {
            case Identifier id: yield return id.Name; break;
            case AssignmentPattern a:
                foreach (var n in PatternNames(a.Target))
                {
                    yield return n;
                }

                break;
            case ArrayPattern arr:
                foreach (var el in arr.Elements)
                {
                    var target = el switch
                    {
                        ArrayPatternBindingElement b => b.Target,
                        ArrayPatternRestElement r => r.Target,
                        _ => null,
                    };
                    if (target is null)
                    {
                        continue;
                    }

                    foreach (var n in PatternNames(target))
                    {
                        yield return n;
                    }
                }
                break;
            case ObjectPattern obj:
                foreach (var prop in obj.Properties)
                {
                    foreach (var n in PatternNames(prop.Target))
                    {
                        yield return n;
                    }
                }

                if (obj.Rest is not null)
                {
                    foreach (var n in PatternNames(obj.Rest.Argument))
                    {
                        yield return n;
                    }
                }

                break;
        }
    }

    /// <summary>Every top-level binding name a module declares locally
    /// (var/let/const/function/class, plus default-export bindings) — these get
    /// loader-allocated cells.</summary>
    private static void CollectLocalBindingNames(
        IReadOnlyList<Statement> body, List<string> names,
        HashSet<string> lexical, HashSet<string> immutableConst)
    {
        void Add(string n) { if (!names.Contains(n)) { names.Add(n); } }
        void AddLex(string n) { Add(n); lexical.Add(n); }
        void AddConst(string n) { AddLex(n); immutableConst.Add(n); }

        // A bare or exported declaration contributes its bound names; let/const/
        // class are lexical (TDZ), var/function are not; a `const` is also
        // immutable (a later assignment is a TypeError).
        void AddDecl(Statement decl)
        {
            switch (decl)
            {
                case VariableDeclaration vd:
                    var isConst = vd.Kind == "const";
                    var isLex = vd.Kind is "let" or "const";
                    foreach (var d in vd.Declarations)
                    {
                        foreach (var n in PatternNames(d.Id))
                        {
                            if (isConst)
                            {
                                AddConst(n);
                            }
                            else if (isLex)
                            {
                                AddLex(n);
                            }
                            else
                            {
                                Add(n);
                            }
                        }
                    }

                    break;
                case FunctionDeclaration fd: Add(fd.Name.Name); break;       // hoisted, not TDZ
                case ClassDeclaration cd: AddLex(cd.Name.Name); break;       // lexical (TDZ)
            }
        }

        foreach (var s in body)
        {
            switch (s)
            {
                case VariableDeclaration:
                case FunctionDeclaration:
                case ClassDeclaration:
                    AddDecl(s);
                    break;
                case ExportLocalDeclaration local:
                    AddDecl(local.Declaration);
                    break;
                // `export default` binds the synthetic *default* name; the
                // default-export binding is lexical/const-like (initialized once
                // when the body runs). Mark it lexical (TDZ) but NOT immutable —
                // the binding is only ever written by the body's own init, and an
                // anonymous default has no name to reassign, so const-immutability
                // adds nothing while risking the init store being rejected.
                case ExportDefaultDeclaration: AddLex(DefaultBindingName); break;
            }
        }
    }

    // -----------------------------------------------------------------------
    // Emission
    // -----------------------------------------------------------------------

    /// <summary>Module-mode function-declaration hoist: compile each top-level
    /// function body, build the closure, and store it into the function name's
    /// upvalue cell so it is callable before its textual site and visible to
    /// importers.</summary>
    private void HoistModuleFunctionDeclarations(IReadOnlyList<Statement> body)
    {
        foreach (var s in body)
        {
            var fd = s switch
            {
                FunctionDeclaration f => f,
                ExportLocalDeclaration { Declaration: FunctionDeclaration f } => f,
                _ => null,
            };
            if (fd is null)
            {
                continue;
            }

            EmitModuleFunctionValue(fd);
            StoreModuleBinding(fd.Name.Name);
        }
    }

    private void EmitModuleFunctionValue(FunctionDeclaration fd)
    {
        var sub = new JsCompiler(parent: this);
        sub._b.IsStrict = fd.Strict;
        sub.RunCaptureAnalysisForFunction(fd.Params, fd.Body.Body);
        sub.EmitFunctionBody(fd);
        var chunk = sub._b.Build(fd.Name.Name);
        EmitFunctionConstructor(fd.Name.Name, chunk,
            CountSimpleParams(fd.Params), sub._upvalues,
            ResolveFunctionKind(fd.Async, fd.Generator), fd.SourceText);
    }

    /// <summary>Emit a write of the top-of-stack value into a module top-level
    /// binding's upvalue cell.</summary>
    private void StoreModuleBinding(string name)
    {
        if (!_moduleBindingUpvalues!.TryGetValue(name, out var idx))
        {
            throw new InvalidOperationException($"module binding '{name}' not reserved");
        }

        _b.EmitUpvalue(Opcode.StoreUpvalue, idx);
    }

    private void EmitModuleItem(Statement s)
    {
        switch (s)
        {
            case ImportDeclaration:
                return; // bindings supplied as upvalues; nothing to emit
            case FunctionDeclaration:
                return; // already hoisted
            case ExportLocalDeclaration local:
                EmitExportLocal(local.Declaration);
                return;
            case ExportDefaultDeclaration def:
                EmitExportDefault(def);
                return;
            case ExportNamedDeclaration:
            case ExportAllDeclaration:
                // Pure re-export / specifier-only list: no runtime code; the
                // export tables already capture the binding wiring.
                return;
            case VariableDeclaration vd:
                // Module-top var/let/const (exported or not) route to upvalue
                // cells so every top-level binding is consistently a live cell.
                EmitModuleVarDecl(vd);
                return;
            case ClassDeclaration cd:
                EmitModuleClassDeclaration(cd.Name.Name, cd);
                return;
            default:
                EmitStatement(s);
                return;
        }
    }

    private void EmitExportLocal(Statement declaration)
    {
        switch (declaration)
        {
            case FunctionDeclaration:
                return; // hoisted by HoistModuleFunctionDeclarations
            case VariableDeclaration vd:
                EmitModuleVarDecl(vd);
                return;
            case ClassDeclaration cd:
                EmitModuleClassDeclaration(cd.Name.Name, cd);
                return;
            default:
                EmitStatement(declaration);
                return;
        }
    }

    private void EmitExportDefault(ExportDefaultDeclaration def)
    {
        switch (def.Declaration)
        {
            case FunctionExpression fe:
                EmitExpression(fe);
                break;
            case ClassExpression ce:
                EmitExpression(ce);
                break;
            case Expression expr:
                EmitExpression(expr);
                break;
            default:
                throw new InvalidOperationException("unsupported default export declaration");
        }
        StoreModuleBinding(DefaultBindingName);
    }

    /// <summary>Module-scope variable declaration — initializers write through
    /// the binding's upvalue cell instead of the global object.</summary>
    /// <remarks>
    /// Single-identifier targets store directly into the reserved upvalue cell.
    /// Destructuring binding patterns (<c>const { a, b } = obj</c>,
    /// <c>let [x, ...rest] = arr</c>, with nesting/defaults/rest) reuse the
    /// function-scope pattern lowering via <see cref="EmitDestructuringFromStack"/>:
    /// each bound name was already reserved as a module upvalue by
    /// <see cref="CollectLocalBindingNames"/> → <see cref="ReserveModuleBinding"/>,
    /// so the leaf store resolves through <c>StoreUpvalue</c> (the module's live
    /// cell) rather than a shadowing local — preserving live bindings even when
    /// the names are re-exported.
    /// </remarks>
    private void EmitModuleVarDecl(VariableDeclaration vd)
    {
        var isLexical = vd.Kind is "let" or "const";
        foreach (var d in vd.Declarations)
        {
            if (d.Init is null)
            {
                // `let x;` (no initializer) initializes the binding to `undefined`,
                // transitioning it out of the TDZ — otherwise its loader-seeded
                // sentinel cell would make every later read throw. A bare `var x;`
                // needs no store (its cell already holds `undefined`).
                if (isLexical && d.Id is Identifier lexId
                    && _moduleBindingUpvalues!.ContainsKey(lexId.Name))
                {
                    _b.Emit(Opcode.LoadUndefined);
                    StoreModuleBinding(lexId.Name);
                }
                continue;
            }
            if (d.Id is Identifier id && _moduleBindingUpvalues!.ContainsKey(id.Name))
            {
                EmitExpression(d.Init);
                StoreModuleBinding(id.Name);
            }
            else
            {
                // Destructuring binding pattern: push the source value and let
                // the shared pattern walker extract each name into its module
                // upvalue cell. No DeclarePatternBindings call → no shadowing
                // local, so live bindings stay intact (the subtlety the prior
                // implementation's NotSupportedException guarded against).
                EmitExpression(d.Init);
                // TDZ — a let/const destructuring init is the binding's own
                // initializer, so its leaf stores must be unchecked (they write
                // the sentinel-seeded cell to its first value); _inLexicalDeclInit
                // suppresses the StoreUpvalueChecked the leaves would otherwise use.
                var prevInit = _inLexicalDeclInit;
                if (isLexical)
                {
                    _inLexicalDeclInit = true;
                }

                try { EmitDestructuringFromStack(d.Id); }
                finally { _inLexicalDeclInit = prevInit; }
            }
        }
    }

    /// <summary>Module-scope class declaration — bind the class value into its
    /// upvalue cell (the classic path writes to the global object).</summary>
    private void EmitModuleClassDeclaration(string name, ClassDeclaration cd)
    {
        EmitClassValue(cd.Name, cd.BaseClass, cd.Body);
        StoreModuleBinding(name);
    }
}

/// <summary>Result of compiling one module: the body chunk plus the static
/// metadata the loader threads into a <see cref="ModuleRecord"/>.</summary>
/// <param name="Chunk">Compiled module body. Expects one upvalue cell per
/// <see cref="BindingOrder"/> entry, in order.</param>
/// <param name="Imports">Static import entries (§16.2.1.3).</param>
/// <param name="Exports">Static export entries (§16.2.1.4).</param>
/// <param name="RequestedModules">Distinct dependency specifiers, first-seen order.</param>
/// <param name="BindingOrder">Upvalue slot order: each entry says which binding
/// the slot at that index holds (local cell vs. imported cell).</param>
/// <param name="HasTopLevelAwait">wp:M3-03b — true when the module body uses a
/// top-level <c>await</c> (or <c>for await</c>) not nested in a function. The
/// loader builds the body as an <see cref="Runtime.JsFunctionKind.Async"/>
/// function so it suspends/returns a Promise; false keeps the synchronous path.</param>
public sealed record ModuleCompilation(
    Chunk Chunk,
    IReadOnlyList<ModuleImportEntry> Imports,
    IReadOnlyList<ModuleExportEntry> Exports,
    IReadOnlyList<string> RequestedModules,
    IReadOnlyList<ModuleBindingSlot> BindingOrder,
    bool HasTopLevelAwait = false);

/// <summary>One upvalue slot in a compiled module body. Locals get a fresh cell
/// from the loader; imports reuse the exporting module's cell, identified by
/// <see cref="Import"/>.</summary>
/// <param name="Name">Local binding name this slot backs.</param>
/// <param name="IsImport">True when the slot is an imported binding.</param>
/// <param name="Import">Import metadata when <see cref="IsImport"/>; null for locals.</param>
/// <param name="IsLexical">TDZ — true for a <em>local</em> lexical binding
/// (<c>let</c>/<c>const</c>/<c>class</c>/default) whose cell the loader must seed
/// with the TDZ sentinel so a read before the module body initializes it throws a
/// ReferenceError. False for <c>var</c>/function locals and for imports (imports
/// alias the source module's cell, which the source seeds).</param>
public sealed record ModuleBindingSlot(
    string Name, bool IsImport, ModuleImportEntry? Import, bool IsLexical = false);
