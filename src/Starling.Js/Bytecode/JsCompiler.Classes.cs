using Starling.Js.Ast;
using Starling.Js.Lex;
using Starling.Js.Runtime;

namespace Starling.Js.Bytecode;

/// <summary>
/// B1b-2a — class declaration / expression / <c>super</c> / private
/// <c>#field</c> compilation. Lowers each class to a single
/// <see cref="Opcode.BuildClass"/> instruction whose
/// <see cref="ClassTemplate"/> describes the class shape; method bodies
/// run as ordinary JsFunctions with their
/// <see cref="JsFunction.HomeObject"/> stamped by the runtime helper.
/// </summary>
public sealed partial class JsCompiler
{
    /// <summary>True while emitting bytecode for a class method body — the
    /// compiler must then emit <see cref="Opcode.LoadThisChecked"/> for
    /// <c>this</c> references so derived-constructor uninitialized state
    /// surfaces as ReferenceError.</summary>
    private int _classMethodDepth;

    private void EmitClassDeclaration(ClassDeclaration cd)
    {
        if (!IsGlobalLexicalScope && TryResolveLocal(cd.Name.Name, out var localSlot))
        {
            // Function/block class declarations are lexical. Keep their binding
            // local so a nested IIFE class cannot resolve to a same-named outer
            // function declaration.
            EmitClassValue(
                cd.Name,
                cd.BaseClass,
                cd.Body,
                bindNameToGlobal: false,
                selfNameSlotOverride: IsSlotCaptured(localSlot) ? localSlot : -1);
            _b.Emit(Opcode.Dup);
            EmitStoreLocalSlot(localSlot);
            _b.Emit(Opcode.Pop);
            return;
        }

        // §15.7 — script-top ClassDeclaration currently uses the global write
        // path so static initializers and method bodies that read the class name
        // resolve through LoadGlobal, preserving the existing class suite.
        // bindNameToGlobal: bind the name before static elements run so a
        // `static { … C … }` block or `static p = C.q` field sees the class.
        //
        // TDZ note: script-top global-lexical TDZ work is still deferred. The
        // global write path below remains the single source of truth there.
        EmitClassValue(cd.Name, cd.BaseClass, cd.Body, bindNameToGlobal: true);
        _b.Emit(Opcode.Dup); // assignment is an expression — leave value on stack briefly
        // Declare the global binding first so the store is not rejected as an
        // assignment to an undeclared global in strict mode (a top-level class
        // declaration introduces a binding before its initializer runs).
        var classNameIdx = _b.AddConstant(cd.Name.Name);
        _b.EmitU16(Opcode.DeclareGlobalVar, classNameIdx);
        _b.EmitU16(Opcode.StoreGlobal, classNameIdx);
        _b.Emit(Opcode.Pop);
    }

    private void EmitClassExpression(ClassExpression ce)
    {
        EmitClassValue(ce.Name, ce.BaseClass, ce.Body);
    }

    private void EmitClassValue(Identifier? name, Expression? baseExpr, ClassBody body,
        bool bindNameToGlobal = false, int selfNameSlotOverride = -1)
    {
        // Evaluate the base class first (if any) so it's on the stack before
        // method upvalues.
        if (baseExpr is not null)
        {
            EmitExpression(baseExpr);
        }

        var classId = ClassTemplate.NextClassId();

        // §15.7.14 ClassDefinitionEvaluation — a class expression with a name
        // creates an inner immutable binding of that name, scoped to the class
        // body and initialized to the constructor once built, so methods/the
        // constructor can refer to the class itself (e.g.
        // `class C extends Array { constructor(){ super(); Object.setProtoOf(this, C.prototype); } }`).
        // The declaration path instead binds the name to a global (handled by
        // the caller), so only do this for expressions whose name a nested
        // member actually captures. Without it `C` inside the body resolves to
        // an undefined free global. We back the binding with a captured Cell so
        // the method/ctor closures observe it as an upvalue.
        // At script top-level the captured-name set is empty (top-level names
        // are globals), so also bind when there is no parent compiler — the
        // class methods are nested functions that capture this script frame.
        var selfNameSlot = selfNameSlotOverride;
        var pushedSelfNameScope = false;
        if (selfNameSlot < 0 && !bindNameToGlobal && name is not null
            && (IsNameCaptured(name.Name) || _parent is null))
        {
            selfNameSlot = _b.ReserveLocal();
            _b.MarkCaptured(selfNameSlot);
            _b.EmitSlot(Opcode.InitCellLocal, selfNameSlot);
            PushScope();
            pushedSelfNameScope = true;
            _scopes[^1][name.Name] = selfNameSlot;
            _lexicalScopes[^1].Add(name.Name);
        }

        // Push a private-name scope so method bodies inside this class can
        // resolve `#x` to its mangled own-property name.
        var privScope = new Dictionary<string, string>(StringComparer.Ordinal);
        _privateScopes.Push(privScope);
        try
        {
            // Collect every declared private name first so forward references
            // inside earlier method bodies resolve.
            CollectPrivateNames(body, classId, privScope);

            // Compile the constructor template (synthesizing one if absent).
            var ctorTemplate = CompileConstructorTemplate(name, baseExpr is not null, body, classId, out var ctorUpvalues);
            // Push ctor upvalues.
            EmitUpvaluePushes(ctorUpvalues);

            // Compile each method template.
            var methodEntries = new List<MethodEntry>();
            for (var i = 0; i < body.Methods.Count; i++)
            {
                var md = body.Methods[i];
                var (entry, methodUps) = CompileMethodTemplate(md, classId);
                methodEntries.Add(entry);
                // wp:M3-04f — for a computed key, evaluate + ToPropertyKey the
                // key expression *now* (source order) and leave the coerced key
                // on the stack below this method's upvalues so BuildClass pops
                // upvalues first, then the key.
                if (md.Computed)
                {
                    EmitComputedKey(md.Key);
                    _b.Emit(Opcode.ToPropertyKey);
                }
                EmitUpvaluePushes(methodUps);
            }

            // Compile each field initializer (instance + static).
            var fieldEntries = new List<FieldEntry>();
            for (var i = 0; i < body.Fields.Count; i++)
            {
                var f = body.Fields[i];
                var (entry, fieldUps) = CompileFieldEntry(f, classId);
                fieldEntries.Add(entry);
                // wp:M3-04f — computed field key: evaluate + ToPropertyKey now
                // (source order), leaving the coerced key below this field's
                // upvalues for BuildClass to consume.
                if (f.Computed)
                {
                    EmitComputedKey(f.Key);
                    _b.Emit(Opcode.ToPropertyKey);
                }
                EmitUpvaluePushes(fieldUps);
            }

            // Compile each static block.
            var staticBlockEntries = new List<StaticBlockEntry>();
            for (var i = 0; i < body.StaticBlocks.Count; i++)
            {
                var (entry, sbUps) = CompileStaticBlockEntry(body.StaticBlocks[i]);
                staticBlockEntries.Add(entry);
                EmitUpvaluePushes(sbUps);
            }

            var template = new ClassTemplate(
                name?.Name ?? "",
                ctorTemplate,
                ctorUpvalues.Count,
                hasExtends: baseExpr is not null,
                methodEntries,
                fieldEntries,
                staticBlockEntries,
                classId,
                bindNameToGlobal,
                selfNameSlot);
            _b.EmitU16(Opcode.BuildClass, _b.AddConstant(template));

            // Initialize the inner class-name binding to the freshly-built
            // class. BuildClass leaves the class on the stack (the expression's
            // value); Dup so the store consumes a copy and the value remains.
            if (selfNameSlot >= 0)
            {
                _b.Emit(Opcode.Dup);
                _b.EmitSlot(Opcode.StoreCellLocal, selfNameSlot);
            }
        }
        finally
        {
            _privateScopes.Pop();
            if (pushedSelfNameScope)
            {
                PopScope();
            }
        }
    }

    private static void CollectPrivateNames(ClassBody body, int classId, Dictionary<string, string> scope)
    {
        if (body.Constructor is not null)
        {
            AddIfPrivate(body.Constructor.Key, classId, scope);
        }

        foreach (var m in body.Methods)
        {
            AddIfPrivate(m.Key, classId, scope);
        }

        foreach (var f in body.Fields)
        {
            AddIfPrivate(f.Key, classId, scope);
        }

        static void AddIfPrivate(Expression key, int classId, Dictionary<string, string> scope)
        {
            if (key is PrivateNameExpression pne)
            {
                scope[pne.Name] = ClassTemplate.MangledPrivateName(classId, pne.Name);
            }
        }
    }

    private JsFunction CompileConstructorTemplate(
        Identifier? className, bool hasExtends, ClassBody body, int classId,
        out IReadOnlyList<UpvalueRef> upvalues)
    {
        var sub = new JsCompiler(parent: this);
        sub._b.IsStrict = true; // §15.7 — class constructor bodies are strict
        sub._privateScopes.Push(_privateScopes.Peek());
        sub._classMethodDepth = 1;
        MethodDefinition? userCtor = body.Constructor;
        IReadOnlyList<Expression> parameters;
        BlockStatement bodyBlock;
        var synthesizedDerived = false;
        if (userCtor is null)
        {
            // Synthesize a default constructor.
            if (hasExtends)
            {
                // Special-cased emission below — forwards all caller args via
                // Opcode.LoadCallerArgs to avoid needing rest-param support.
                synthesizedDerived = true;
                parameters = Array.Empty<Expression>();
                var pos = new JsPosition(0, 1, 1);
                bodyBlock = new BlockStatement(Array.Empty<Statement>(), pos, pos);
            }
            else
            {
                var pos = new JsPosition(0, 1, 1);
                parameters = Array.Empty<Expression>();
                bodyBlock = new BlockStatement(Array.Empty<Statement>(), pos, pos);
            }
        }
        else
        {
            parameters = userCtor.Params;
            bodyBlock = userCtor.Body;
        }

        // wp:M3-04c2 — run the same mutated-capture → Cell promotion the plain
        // function-body pipeline runs (JsCompiler.EmitFunctionBody). Capture
        // analysis must precede BindFunctionParameters so a captured + mutated
        // constructor parameter is promoted to a Cell (PromoteParamCell).
        sub.RunCaptureAnalysisForFunction(parameters, bodyBlock.Body);
        // wp:M3-81 — a class constructor is a non-arrow function; its parameter
        // default region is an initializer context for the eval ContainsArguments rule.
        sub.BindFunctionParameters(parameters, markInitializer: true);

        if (synthesizedDerived)
        {
            // super(...arguments) — push args array, CallSuperCtor, BindThis,
            // RunFieldInits. Mirrors EmitSuperCall's emission sequence.
            sub._b.Emit(Opcode.LoadCallerArgs);
            sub._b.Emit(Opcode.CallSuperCtor);
            sub._b.Emit(Opcode.Dup);
            sub._b.Emit(Opcode.BindThis);
            sub._b.Emit(Opcode.RunFieldInits);
            sub._b.Emit(Opcode.Pop);
        }
        else
        {
            // Prologue:
            //   Base class:    RunFieldInits before user body
            //   Derived class: deferred — emitted right after super(...)
            if (!hasExtends)
            {
                sub._b.Emit(Opcode.RunFieldInits);
            }

            // wp:M3-04c2 — pre-allocate Cell slots for captured var/let/const
            // and hoist nested function declarations, exactly like
            // EmitFunctionBody, so closures formed in the constructor body
            // capture a shared cell instead of a raw value.
            sub.PreallocateCapturedVarBindings(bodyBlock.Body);
            sub.HoistVarDeclarations(bodyBlock.Body);
            sub.HoistLexicalDeclarations(bodyBlock.Body); // TDZ
            sub.HoistFunctionDeclarations(bodyBlock.Body);
            // wp:M3-20 — class constructors are non-arrow functions; give them
            // an `arguments` object when the body reads it.
            sub.MaybeBindArguments(parameters, bodyBlock.Body);
            // §10.2.1.1 — box `this` for a nested arrow. For a derived ctor the
            // cell starts undefined and EmitSuperCall re-stores it once super()
            // binds `this` (StoreLexicalThisCell).
            sub.MaybeBindLexicalThis();
            foreach (var s in bodyBlock.Body)
            {
                sub.EmitStatement(s);
            }
        }

        sub._b.Emit(Opcode.ReturnUndefined);
        // §16.1.7 — stamp the in-scope private names so a direct eval in the
        // constructor body can resolve them. Captured before popping the scope.
        sub._b.PrivateNameScope = sub.FlattenPrivateScopes();
        sub._privateScopes.Pop();

        var arity = CountSimpleParams(parameters);
        var ctorName = className?.Name ?? "";
        var chunk = sub._b.Build(ctorName);
        var template = new JsFunction(ctorName, chunk, arity)
        {
            ConstructorKind = hasExtends ? ClassConstructorKind.Derived : ClassConstructorKind.Base,
        };
        upvalues = sub._upvalues;
        return template;
    }

    private (MethodEntry Entry, IReadOnlyList<UpvalueRef> Upvalues) CompileMethodTemplate(
        MethodDefinition md, int classId)
    {
        var sub = new JsCompiler(parent: this);
        sub._b.IsStrict = true; // §15.7 — class method/accessor bodies are strict
        sub._privateScopes.Push(_privateScopes.Peek());
        sub._classMethodDepth = 1;
        // wp:M3-04c2 — method/accessor bodies use the same mutated-capture →
        // Cell pipeline as plain functions (JsCompiler.EmitFunctionBody).
        // Capture analysis must precede BindFunctionParameters so a captured +
        // mutated method parameter is promoted to a Cell (PromoteParamCell).
        sub.RunCaptureAnalysisForFunction(md.Params, md.Body.Body);
        // wp:M3-81 — a class method/accessor is a non-arrow function; its parameter
        // default region is an initializer context for the eval ContainsArguments rule.
        sub.BindFunctionParameters(md.Params, markInitializer: true);
        sub.PreallocateCapturedVarBindings(md.Body.Body);
        sub.HoistVarDeclarations(md.Body.Body);
        sub.HoistLexicalDeclarations(md.Body.Body); // TDZ
        sub.HoistFunctionDeclarations(md.Body.Body);
        // wp:M3-20 — methods/accessors are non-arrow functions; synthesize an
        // `arguments` object when the body reads it.
        sub.MaybeBindArguments(md.Params, md.Body.Body);
        // §10.2.1.1 — box `this` for a nested arrow that reads it (the common
        // `method(){ ... () => this.#x ... }` private-access case).
        sub.MaybeBindLexicalThis();
        // §10.2.1.3 — synchronous parameter-binding prologue boundary for
        // generator / async / async-generator methods; see EmitFunctionBody.
        sub.EmitPrologueEndIfSuspendable(md.Async, md.Generator);
        sub._currentIsAsyncGenerator = md.Async && md.Generator;
        foreach (var s in md.Body.Body)
        {
            sub.EmitStatement(s);
        }

        sub._b.Emit(Opcode.ReturnUndefined);
        // §16.1.7 — stamp in-scope private names for a direct eval in the body.
        sub._b.PrivateNameScope = sub.FlattenPrivateScopes();
        sub._privateScopes.Pop();

        // wp:M3-04f — computed keys have no statically-known name; the
        // coerced key is supplied at BuildClass time. Use "" as the function
        // name (the runtime stamps the real name when it knows the key).
        string keyName = md.Computed ? "" : md.Key switch
        {
            Identifier id => id.Name,
            StringLiteral sl => sl.Value,
            NumericLiteral nl => JsValue.ToStringValue(JsValue.Number(nl.Value)),
            PrivateNameExpression pne => pne.Name,
            _ => throw new NotSupportedException($"method key kind '{md.Key.GetType().Name}'"),
        };
        var arity = CountSimpleParams(md.Params);
        var chunk = sub._b.Build(keyName);
        var template = new JsFunction(keyName, chunk, arity)
        {
            // Generator / async / async-generator methods run through the same
            // Suspend-based machinery as standalone functions; the runtime
            // wrapper is selected by JsFunction.Kind (copied in CreateInstance).
            Kind = ResolveFunctionKind(md.Async, md.Generator),
            SourceText = md.SourceText,
        };
        var kind = md.Kind switch
        {
            MethodKind.Get => ClassMethodKind.Get,
            MethodKind.Set => ClassMethodKind.Set,
            _ => ClassMethodKind.Method,
        };
        string? staticKey = null;
        string? mangled = null;
        if (md.Computed)
        {
            // Both keys stay null; BuildClass reads the coerced key off the stack.
        }
        else if (md.Key is PrivateNameExpression pne2)
        {
            mangled = ClassTemplate.MangledPrivateName(classId, pne2.Name);
        }
        else
        {
            staticKey = keyName;
        }
        var entry = new MethodEntry(staticKey, mangled, kind, md.IsStatic, template, sub._upvalues.Count, md.Computed);
        return (entry, sub._upvalues);
    }

    private (FieldEntry Entry, IReadOnlyList<UpvalueRef> Upvalues) CompileFieldEntry(
        PropertyField field, int classId)
    {
        string? staticKey = null;
        string? mangled = null;
        if (field.Computed)
        {
            // wp:M3-04f — computed key resolved at BuildClass time; both keys
            // stay null.
        }
        else if (field.Key is PrivateNameExpression pne)
        {
            mangled = ClassTemplate.MangledPrivateName(classId, pne.Name);
        }
        else
        {
            staticKey = field.Key switch
            {
                Identifier id => id.Name,
                StringLiteral sl => sl.Value,
                NumericLiteral nl => JsValue.ToStringValue(JsValue.Number(nl.Value)),
                _ => throw new NotSupportedException($"field key kind '{field.Key.GetType().Name}'"),
            };
        }

        if (field.Initializer is null)
        {
            return (new FieldEntry(staticKey, mangled, field.IsStatic, null, 0, field.Computed), Array.Empty<UpvalueRef>());
        }

        // §15.7.1 Static Semantics: Early Errors — "It is a Syntax Error if
        // ContainsArguments of Initializer is true." A class field initializer
        // may not reference `arguments` (the binding does not extend into a
        // field's initializer); ContainsArguments recurses through arrow
        // functions but stops at ordinary function boundaries — exactly what
        // ArgRefExpr computes. (The PropertyName is excluded, so a computed key
        // referencing `arguments` is unaffected.)
        if (CaptureAnalysis.ContainsArguments(field.Initializer))
        {
            throw new Parse.JsParseException(
                "'arguments' is not allowed in a class field initializer", field.Initializer.Start);
        }

        // Compile the initializer thunk.
        //   Static / non-computed keys: `this.<key> = <init>` (or
        //   DefinePrivateField for private fields) — the key is baked in.
        //   Computed keys (wp:M3-04f): just `return <init>` — the runtime owns
        //   the (already-coerced) key and performs the define.
        var sub = new JsCompiler(parent: this);
        sub._b.IsStrict = true; // §15.7 — field initializers are strict
        // wp:M3-81 — §sec-performeval-rules-in-initializer: a direct eval inside a
        // class field initializer is subject to the ContainsArguments early error.
        // Mark the thunk so the VM seeds the initializer depth at frame entry.
        sub._b.IsInitializer = true;
        sub._privateScopes.Push(_privateScopes.Peek());
        sub._classMethodDepth = 1;
        // wp:M3-04c2 — the field-init thunk is a single expression, but run the
        // same capture analysis the function-body pipeline runs so any nested
        // closure inside the initializer that mutates a captured binding is
        // treated identically to plain functions. (The thunk has no params /
        // statement-level declarations, so no preallocate/hoist is required.)
        sub.RunCaptureAnalysisForFunction(
            Array.Empty<Expression>(),
            new[] { (Statement)new ExpressionStatement(field.Initializer, field.Initializer.Start, field.Initializer.End) });
        // §10.2.1.1 — box `this` (the instance / constructor) for a nested arrow
        // inside the initializer (e.g. `#f = () => this.#g`).
        sub.MaybeBindLexicalThis();
        if (field.Computed)
        {
            sub.EmitExpression(field.Initializer);
            sub._b.Emit(Opcode.Return);
        }
        else
        {
            sub._b.Emit(Opcode.LoadThis);
            sub.EmitExpression(field.Initializer);
            if (mangled is not null)
            {
                sub._b.EmitU16(Opcode.DefinePrivateField, sub._b.AddConstant(mangled));
            }
            else
            {
                sub._b.EmitProperty(Opcode.StoreProperty, sub._b.AddConstant(staticKey!));
                sub._b.Emit(Opcode.Pop);
            }
            sub._b.Emit(Opcode.ReturnUndefined);
        }
        // §16.1.7 — stamp in-scope private names for a direct eval in the init.
        sub._b.PrivateNameScope = sub.FlattenPrivateScopes();
        sub._privateScopes.Pop();
        var chunk = sub._b.Build($"#field-init:{(staticKey ?? mangled ?? "[computed]")}");
        var initTemplate = new JsFunction("", chunk, 0);
        return (new FieldEntry(staticKey, mangled, field.IsStatic, initTemplate, sub._upvalues.Count, field.Computed), sub._upvalues);
    }

    private (StaticBlockEntry Entry, IReadOnlyList<UpvalueRef> Upvalues) CompileStaticBlockEntry(BlockStatement block)
    {
        var sub = new JsCompiler(parent: this);
        sub._b.IsStrict = true; // §15.7 — static blocks are strict
        // wp:M3-81 — §sec-performeval-rules-in-initializer: a direct eval inside a
        // static initializer block is subject to the ContainsArguments early error.
        sub._b.IsInitializer = true;
        sub._privateScopes.Push(_privateScopes.Peek());
        sub._classMethodDepth = 1;
        // wp:M3-04c2 — static-block bodies run their own statement list and so
        // need the same mutated-capture → Cell promotion + function hoisting as
        // plain function bodies.
        sub.RunCaptureAnalysisForFunction(Array.Empty<Expression>(), block.Body);
        sub.PreallocateCapturedVarBindings(block.Body);
        sub.HoistVarDeclarations(block.Body);
        sub.HoistLexicalDeclarations(block.Body); // TDZ
        sub.HoistFunctionDeclarations(block.Body);
        // §10.2.1.1 — box `this` (the constructor) for a nested arrow.
        sub.MaybeBindLexicalThis();
        foreach (var s in block.Body)
        {
            sub.EmitStatement(s);
        }

        sub._b.Emit(Opcode.ReturnUndefined);
        // §16.1.7 — stamp in-scope private names for a direct eval in the block.
        sub._b.PrivateNameScope = sub.FlattenPrivateScopes();
        sub._privateScopes.Pop();
        var chunk = sub._b.Build("#static-block");
        var tmpl = new JsFunction("", chunk, 0);
        return (new StaticBlockEntry(tmpl, sub._upvalues.Count), sub._upvalues);
    }

    private void EmitUpvaluePushes(IReadOnlyList<UpvalueRef> upvalues)
    {
        foreach (var u in upvalues)
        {
            if (u.IsLocalCapture)
            {
                _b.EmitSlot(Opcode.LoadLocal, u.Index);
            }
            // Class elements can re-capture a binding that the enclosing chunk
            // already sees as an upvalue. Pass the cell, not its current value.
            else
            {
                _b.EmitUpvalue(Opcode.LoadUpvalueCell, u.Index);
            }
        }
    }

    // -------------------------------------------------------------- super.x
    private void EmitSuperProperty(SuperPropertyExpression sp)
    {
        if (sp.Computed)
        {
            // wp:M3-04h — super[expr]: evaluate the key, then resolve through the
            // home object's prototype with `this` as the receiver at runtime.
            EmitExpression(sp.Property);
            _b.Emit(Opcode.LoadSuperComputed);
            return;
        }
        var name = ((Identifier)sp.Property).Name;
        _b.EmitU16(Opcode.LoadSuperProperty, _b.AddConstant(name));
    }

    private void EmitSuperCall(SuperCallExpression sc)
    {
        // Marshal arguments as a single array and dispatch via CallSuperCtor.
        EmitArgsAsArray(sc.Arguments);
        _b.Emit(Opcode.CallSuperCtor);
        // Stack: [constructed]. Bind as this.
        _b.Emit(Opcode.Dup);
        _b.Emit(Opcode.BindThis);
        // §10.2.1.1 — `this` is now bound; refresh the lexical-this Cell so any
        // arrow created after super() observes the constructed instance.
        StoreLexicalThisCell();
        // Run instance field initializers immediately after super() per spec.
        _b.Emit(Opcode.RunFieldInits);
        // The value of `super(...)` as an expression is the constructed
        // object — leave it on the stack so an enclosing ExpressionStatement
        // can pop it.
    }

    /// <summary>§16.1.7 — flatten every private-name scope visible at this point
    /// (this compiler's <c>_privateScopes</c> plus the enclosing-compiler chain)
    /// into a single name→mangled map. Stamped onto a method/ctor/field/static
    /// body's chunk so a direct <c>eval</c> in it can resolve the enclosing
    /// class's private names. Returns null when no private names are in scope.
    /// Inner scopes override outer ones (added first, then not overwritten).</summary>
    private Dictionary<string, string>? FlattenPrivateScopes()
    {
        Dictionary<string, string>? flat = null;
        void Merge(Dictionary<string, string> scope)
        {
            foreach (var kv in scope)
            {
                flat ??= new Dictionary<string, string>(StringComparer.Ordinal);
                flat.TryAdd(kv.Key, kv.Value); // inner (visited first) wins
            }
        }
        foreach (var scope in _privateScopes)
        {
            Merge(scope);
        }

        for (var p = _parent; p is not null; p = p._parent)
        {
            foreach (var scope in p._privateScopes)
            {
                Merge(scope);
            }
        }

        return flat;
    }

    /// <summary>Resolve a private name (<c>#name</c>) to its mangled own
    /// property key. A reference to a private name not declared in any
    /// enclosing class scope is an early SyntaxError (§15.7.1
    /// AllPrivateIdentifiersValid), so this throws <see cref="Parse.JsParseException"/>.</summary>
    internal string ResolvePrivateName(string privateName, JsPosition at)
    {
        foreach (var scope in _privateScopes)
        {
            if (scope.TryGetValue(privateName, out var mangled))
            {
                return mangled;
            }
        }
        for (var p = _parent; p is not null; p = p._parent)
        {
            foreach (var scope in p._privateScopes)
            {
                if (scope.TryGetValue(privateName, out var mangled))
                {
                    return mangled;
                }
            }
        }
        throw new Parse.JsParseException(
            $"private name '{privateName}' is not declared in any enclosing class scope", at);
    }
}
