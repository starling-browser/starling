using Starling.Js.Ast;
using Starling.Js.Lex;

namespace Starling.Js.Bytecode;

/// <summary>
/// Static analysis that decides which of a function's own locals must use
/// shared <see cref="Starling.Js.Runtime.Cell"/> storage rather than plain slot
/// storage.
/// </summary>
/// <remarks>
/// <para>
/// A local is "captured" iff some nested function (or class method / field
/// initializer / arrow / static block) inside the same function body
/// references the name as a free variable — i.e. the name isn't shadowed
/// by an inner declaration.
/// </para>
/// <para>
/// The analysis runs once per <see cref="JsCompiler"/> instance, before
/// any bytecode is emitted. Captured locals are recorded by name; the
/// compiler then consults the set at declaration sites (params + var/let
/// decls + function-decl hoists) and either marks the slot as a cell or
/// leaves it as a plain slot. Non-captured locals stay fast — no boxing.
/// </para>
/// </remarks>
internal static class CaptureAnalysis
{
    /// <summary>Given the parameters and statements of one function body
    /// (or the top-level script), return the set of identifier names that
    /// are referenced from inside any nested function or class member —
    /// after accounting for inner declarations that shadow the name.</summary>
    /// <remarks>
    /// The returned set is intentionally over-approximate in one direction
    /// only: names that are NOT in the set are guaranteed safe to store as
    /// plain slot values. Names IN the set may end up referenced by code
    /// that the static walker conservatively conflated; the runtime cost
    /// is a single extra indirection per access, never a correctness bug.
    /// </remarks>
    public static HashSet<string> Compute(
        IReadOnlyList<Expression> parameters,
        IReadOnlyList<Statement> body)
    {
        var captured = new HashSet<string>(StringComparer.Ordinal);
        // The current function's lexical scope owns its params + declared
        // names; we don't seed those into `captured` here — the caller
        // intersects with its actual declarations to find which slots to box.
        foreach (var s in body)
        {
            WalkStatementInOuter(s, captured);
        }
        // Parameter destructuring defaults can themselves contain nested
        // functions whose free names should capture this function's other
        // parameters; walk those too.
        foreach (var p in parameters)
        {
            WalkExpressionInOuter(p, captured);
        }

        return captured;
    }

    /// <summary>wp:M3-20 — does this (non-arrow) function body reference the
    /// identifier <c>arguments</c> in its own <c>arguments</c>-scope? True when
    /// the name appears free in the body or in a parameter default, including
    /// inside nested <em>arrow</em> functions (which inherit <c>arguments</c>
    /// lexically) — but NOT inside nested ordinary functions / class methods,
    /// which establish their own <c>arguments</c>. The compiler uses this to
    /// decide whether to materialize the arguments object (§10.4.4) for the
    /// function, avoiding the per-call allocation when it is never read.</summary>
    public static bool ReferencesArguments(
        IReadOnlyList<Expression> parameters,
        IReadOnlyList<Statement> body)
    {
        foreach (var p in parameters)
        {
            if (ArgRefExpr(p))
            {
                return true;
            }
        }

        foreach (var s in body)
        {
            if (ArgRefStmt(s))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>§15.7.1 ContainsArguments — true when <paramref name="e"/>
    /// references the identifier <c>arguments</c> free in its own scope
    /// (recursing through arrow functions but not ordinary function / class
    /// boundaries). Used to enforce the class-field-initializer early error.</summary>
    public static bool ContainsArguments(Expression? e) => ArgRefExpr(e);

    /// <summary>wp:M3-81 — §sec-performeval-rules-in-initializer: ContainsArguments
    /// of a StatementList (an eval ScriptBody). Returns true when the list contains
    /// ANY IdentifierReference OR BindingIdentifier whose StringValue is
    /// <c>arguments</c> — recursing through arrow functions (which inherit
    /// <c>arguments</c> lexically) but NOT through ordinary function / class-method
    /// boundaries (which establish their own <c>arguments</c>). Unlike
    /// <see cref="ReferencesArguments"/>, this also matches DECLARATIONS that bind
    /// the name (e.g. <c>var arguments</c>, <c>let arguments</c>,
    /// <c>function arguments() {}</c>, <c>class arguments {}</c>, catch
    /// <c>arguments</c>, destructuring <c>{ arguments }</c>) per the spec
    /// ContainsArguments static-semantics — which is exactly what is needed for the
    /// eval-inside-initializer early error in PerformEval.</summary>
    public static bool ContainsArgumentsInEvalBody(IReadOnlyList<Statement> statements)
    {
        foreach (var s in statements)
        {
            if (EvalCAStmt(s))
            {
                return true;
            }
        }

        return false;
    }

    private static bool EvalCAStmt(Statement? s)
    {
        if (s is null)
        {
            return false;
        }

        switch (s)
        {
            case BlockStatement b: return b.Body.Any(EvalCAStmt);
            case ExpressionStatement es: return EvalCAExpr(es.Expression);
            case ReturnStatement r: return EvalCAExpr(r.Argument);
            case ThrowStatement t: return EvalCAExpr(t.Argument);
            case IfStatement i:
                return EvalCAExpr(i.Test) || EvalCAStmt(i.Consequent) || EvalCAStmt(i.Alternate);
            case WhileStatement w: return EvalCAExpr(w.Test) || EvalCAStmt(w.Body);
            case DoWhileStatement dw: return EvalCAStmt(dw.Body) || EvalCAExpr(dw.Test);
            case ForStatement f:
                if (f.Init is Statement fis && EvalCAStmt(fis))
                {
                    return true;
                }

                if (f.Init is Expression fie && EvalCAExpr(fie))
                {
                    return true;
                }

                return EvalCAExpr(f.Test) || EvalCAExpr(f.Update) || EvalCAStmt(f.Body);
            case ForInStatement fi:
                if (fi.Left is Statement fil && EvalCAStmt(fil))
                {
                    return true;
                }

                if (fi.Left is Expression file && EvalCAExpr(file))
                {
                    return true;
                }

                return EvalCAExpr(fi.Right) || EvalCAStmt(fi.Body);
            case ForOfStatement fo:
                if (fo.Left is Statement fol && EvalCAStmt(fol))
                {
                    return true;
                }

                if (fo.Left is Expression foe && EvalCAExpr(foe))
                {
                    return true;
                }

                return EvalCAExpr(fo.Right) || EvalCAStmt(fo.Body);
            case SwitchStatement sw:
                if (EvalCAExpr(sw.Discriminant))
                {
                    return true;
                }

                foreach (var c in sw.Cases)
                {
                    if (EvalCAExpr(c.Test))
                    {
                        return true;
                    }

                    if (c.Consequent.Any(EvalCAStmt))
                    {
                        return true;
                    }
                }
                return false;
            case TryStatement tr:
                if (EvalCAStmt(tr.Block))
                {
                    return true;
                }

                if (tr.Handler is not null)
                {
                    // Catch binding: the catch parameter is a BindingIdentifier (or
                    // pattern containing one) — `catch (arguments)` counts.
                    if (tr.Handler.Param is not null && EvalCABindingTarget(tr.Handler.Param))
                    {
                        return true;
                    }

                    if (tr.Handler.Body.Body.Any(EvalCAStmt))
                    {
                        return true;
                    }
                }
                return tr.Finalizer is not null && EvalCAStmt(tr.Finalizer);
            case LabeledStatement ls: return EvalCAStmt(ls.Body);
            case WithStatement ws: return EvalCAExpr(ws.Object) || EvalCAStmt(ws.Body);
            case VariableDeclaration vd:
                // BindingIdentifiers in the declaration list count.
                foreach (var d in vd.Declarations)
                {
                    if (EvalCABindingTarget(d.Id))
                    {
                        return true;
                    }

                    if (EvalCAExpr(d.Init))
                    {
                        return true;
                    }
                }
                return false;
            // §15.7.1 — a nested ORDINARY function declaration establishes its own
            // `arguments`; do not descend into its parameters / body. The function
            // name BindingIdentifier itself, however, still counts.
            case FunctionDeclaration fd:
                return fd.Name.Name == "arguments";
            case ClassDeclaration cd:
                if (cd.Name.Name == "arguments")
                {
                    return true;
                }

                if (EvalCAExpr(cd.BaseClass))
                {
                    return true;
                }
                // Class body methods are non-arrow and have their own `arguments`,
                // so do NOT descend into method params/bodies. Class field
                // initializers run with no own `arguments` and arrow propagation,
                // but a class declaration sitting inside an eval body is itself a
                // BindingIdentifier site — recursing further is not required for
                // the early-error rule (and would be a spec edge case).
                return false;
            case EmptyStatement:
            case BreakStatement:
            case ContinueStatement:
            case DebuggerStatement:
                return false;
            default:
                return false;
        }
    }

    private static bool EvalCAExpr(Expression? e)
    {
        if (e is null)
        {
            return false;
        }

        switch (e)
        {
            // IdentifierReference at this position.
            case Identifier id: return id.Name == "arguments";
            case BinaryExpression bin: return EvalCAExpr(bin.Left) || EvalCAExpr(bin.Right);
            case LogicalExpression log: return EvalCAExpr(log.Left) || EvalCAExpr(log.Right);
            case UnaryExpression u: return EvalCAExpr(u.Argument);
            case UpdateExpression up: return EvalCAExpr(up.Argument);
            case AssignmentExpression a: return EvalCAExpr(a.Target) || EvalCAExpr(a.Value);
            case AssignmentPattern ap: return EvalCAExpr(ap.Target) || EvalCAExpr(ap.Default);
            case RestElement rest: return EvalCAExpr(rest.Argument);
            case ConditionalExpression c:
                return EvalCAExpr(c.Test) || EvalCAExpr(c.Consequent) || EvalCAExpr(c.Alternate);
            case MemberExpression m:
                // Non-computed property names are IdentifierName (not a reference),
                // so only the object (and computed property) participate.
                return EvalCAExpr(m.Object) || (m.Computed && EvalCAExpr(m.Property));
            case CallExpression call:
                return EvalCAExpr(call.Callee) || call.Arguments.Any(EvalCAExpr);
            case NewExpression ne:
                return EvalCAExpr(ne.Callee) || ne.Arguments.Any(EvalCAExpr);
            case ArrayExpression aex: return aex.Elements.Any(EvalCAExpr);
            case ObjectExpression oe:
                foreach (var prop in oe.Properties)
                {
                    // Only a COMPUTED key is an expression that might reference
                    // `arguments`; a non-computed key is a PropertyName, not a
                    // reference, so do not descend.
                    if (prop.Computed && EvalCAExpr(prop.Key))
                    {
                        return true;
                    }

                    if (EvalCAExpr(prop.Value))
                    {
                        return true;
                    }
                }
                return false;
            case SequenceExpression seq: return seq.Expressions.Any(EvalCAExpr);
            case TemplateLiteral tpl: return tpl.Expressions.Any(EvalCAExpr);
            case TaggedTemplateExpression tte: return EvalCAExpr(tte.Tag) || EvalCAExpr(tte.Quasi);
            case SpreadElement sp: return EvalCAExpr(sp.Argument);
            // Arrows inherit `arguments` lexically — descend through them. Their
            // params can also introduce BindingIdentifiers named `arguments`.
            case ArrowFunctionExpression arrow:
                foreach (var p in arrow.Params)
                {
                    if (EvalCABindingTarget(p) || EvalCAExpr(p))
                    {
                        return true;
                    }
                }

                return arrow.Body switch
                {
                    BlockStatement block => block.Body.Any(EvalCAStmt),
                    Expression expr => EvalCAExpr(expr),
                    _ => false,
                };
            // §15.7.1 — a nested ORDINARY function expression establishes its own
            // `arguments`; do not descend into its parameters / body. A NAMED
            // function expression's name binding (e.g. `function arguments() {}`
            // as an expression) is itself a BindingIdentifier site and counts.
            case FunctionExpression fe:
                return fe.Name is { Name: "arguments" };
            case ClassExpression cls:
                if (cls.Name is { Name: "arguments" })
                {
                    return true;
                }

                return EvalCAExpr(cls.BaseClass);
            // Binding patterns appearing in expression position (e.g. destructuring
            // assignment targets) — descend through both targets (which may be
            // BindingIdentifiers) and defaults.
            case ArrayPattern arrp:
                foreach (var el in arrp.Elements)
                {
                    switch (el)
                    {
                        case ArrayPatternBindingElement binding:
                            if (EvalCABindingTarget(binding.Target))
                            {
                                return true;
                            }

                            if (EvalCAExpr(binding.Target))
                            {
                                return true;
                            }

                            if (EvalCAExpr(binding.Default))
                            {
                                return true;
                            }

                            break;
                        case ArrayPatternRestElement r:
                            if (EvalCABindingTarget(r.Target))
                            {
                                return true;
                            }

                            if (EvalCAExpr(r.Target))
                            {
                                return true;
                            }

                            break;
                    }
                }
                return false;
            case ObjectPattern op:
                foreach (var prop in op.Properties)
                {
                    if (prop.Computed && EvalCAExpr(prop.Key))
                    {
                        return true;
                    }

                    if (EvalCABindingTarget(prop.Target))
                    {
                        return true;
                    }

                    if (EvalCAExpr(prop.Target))
                    {
                        return true;
                    }

                    if (EvalCAExpr(prop.Default))
                    {
                        return true;
                    }
                }
                if (op.Rest is not null)
                {
                    if (EvalCABindingTarget(op.Rest.Argument))
                    {
                        return true;
                    }

                    if (EvalCAExpr(op.Rest.Argument))
                    {
                        return true;
                    }
                }
                return false;
            case SuperPropertyExpression sp2: return sp2.Computed && EvalCAExpr(sp2.Property);
            case SuperCallExpression scx: return scx.Arguments.Any(EvalCAExpr);
            default: return false;
        }
    }

    // True when a binding pattern (declaration target) introduces a
    // BindingIdentifier named "arguments" anywhere within it.
    private static bool EvalCABindingTarget(Expression? p)
    {
        if (p is null)
        {
            return false;
        }

        switch (p)
        {
            case Identifier id: return id.Name == "arguments";
            case AssignmentPattern ap: return EvalCABindingTarget(ap.Target);
            case AssignmentExpression { Op: JsTokenKind.Eq } a: return EvalCABindingTarget(a.Target);
            case RestElement re: return EvalCABindingTarget(re.Argument);
            case SpreadElement sp: return EvalCABindingTarget(sp.Argument);
            case ArrayPattern arr:
                foreach (var el in arr.Elements)
                {
                    switch (el)
                    {
                        case ArrayPatternBindingElement b:
                            if (EvalCABindingTarget(b.Target))
                            {
                                return true;
                            }

                            break;
                        case ArrayPatternRestElement r:
                            if (EvalCABindingTarget(r.Target))
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
                    if (EvalCABindingTarget(prop.Target))
                    {
                        return true;
                    }
                }

                return obj.Rest is not null && EvalCABindingTarget(obj.Rest.Argument);
            default:
                return false;
        }
    }

    /// <summary>§10.2.1.1 / §13.2.5 — does any <em>nested arrow</em> in this
    /// (non-arrow) function's body reference <c>this</c>? Arrow functions have
    /// no own <c>this</c> binding; they resolve <c>this</c> lexically to the
    /// nearest enclosing ordinary function. When such a reference exists, the
    /// enclosing function must materialize its <c>this</c> into a captured Cell
    /// (under the synthetic name <c>&lt;this&gt;</c>) so the arrow closure can
    /// read it as an upvalue. Mirrors <see cref="ReferencesArguments"/> but for
    /// the <c>this</c> binding. Implemented as: does the body contain any arrow
    /// whose body (descending through further arrows, not ordinary functions)
    /// references <c>this</c>? A bare <c>this</c> in the ordinary body does not
    /// count — it uses <see cref="Opcode.LoadThis"/> directly.</summary>
    public static bool ReferencesThisInNestedArrow(
        IReadOnlyList<Expression> parameters,
        IReadOnlyList<Statement> body)
    {
        foreach (var p in parameters)
        {
            if (FindArrowThisExpr(p))
            {
                return true;
            }
        }

        foreach (var s in body)
        {
            if (FindArrowThisStmt(s))
            {
                return true;
            }
        }

        return false;
    }

    // Phase 1: locate a nested arrow (without counting the ordinary body's own
    // `this`). Reuses ArgRef*'s traversal shape but the leaf trigger is an arrow
    // whose body references `this`. Ordinary function bodies establish their own
    // `this`, so we do NOT descend into them.
    private static bool FindArrowThisStmt(Statement? s)
    {
        if (s is null)
        {
            return false;
        }

        switch (s)
        {
            case BlockStatement b: return b.Body.Any(FindArrowThisStmt);
            case ExpressionStatement es: return FindArrowThisExpr(es.Expression);
            case ReturnStatement r: return FindArrowThisExpr(r.Argument);
            case ThrowStatement t: return FindArrowThisExpr(t.Argument);
            case IfStatement i:
                return FindArrowThisExpr(i.Test) || FindArrowThisStmt(i.Consequent) || FindArrowThisStmt(i.Alternate);
            case WhileStatement w: return FindArrowThisExpr(w.Test) || FindArrowThisStmt(w.Body);
            case DoWhileStatement dw: return FindArrowThisStmt(dw.Body) || FindArrowThisExpr(dw.Test);
            case ForStatement f:
                if (f.Init is Statement fis && FindArrowThisStmt(fis))
                {
                    return true;
                }

                if (f.Init is Expression fie && FindArrowThisExpr(fie))
                {
                    return true;
                }

                return FindArrowThisExpr(f.Test) || FindArrowThisExpr(f.Update) || FindArrowThisStmt(f.Body);
            case ForInStatement fi:
                if (fi.Left is Statement fil && FindArrowThisStmt(fil))
                {
                    return true;
                }

                if (fi.Left is Expression file && FindArrowThisExpr(file))
                {
                    return true;
                }

                return FindArrowThisExpr(fi.Right) || FindArrowThisStmt(fi.Body);
            case ForOfStatement fo:
                if (fo.Left is Statement fol && FindArrowThisStmt(fol))
                {
                    return true;
                }

                if (fo.Left is Expression foe && FindArrowThisExpr(foe))
                {
                    return true;
                }

                return FindArrowThisExpr(fo.Right) || FindArrowThisStmt(fo.Body);
            case SwitchStatement sw:
                if (FindArrowThisExpr(sw.Discriminant))
                {
                    return true;
                }

                foreach (var c in sw.Cases)
                {
                    if (FindArrowThisExpr(c.Test))
                    {
                        return true;
                    }

                    if (c.Consequent.Any(FindArrowThisStmt))
                    {
                        return true;
                    }
                }
                return false;
            case TryStatement tr:
                if (FindArrowThisStmt(tr.Block))
                {
                    return true;
                }

                if (tr.Handler is not null && tr.Handler.Body.Body.Any(FindArrowThisStmt))
                {
                    return true;
                }

                return tr.Finalizer is not null && FindArrowThisStmt(tr.Finalizer);
            case LabeledStatement ls: return FindArrowThisStmt(ls.Body);
            case WithStatement ws: return FindArrowThisExpr(ws.Object) || FindArrowThisStmt(ws.Body);
            case VariableDeclaration vd:
                return vd.Declarations.Any(d => FindArrowThisExpr(d.Init));
            // Ordinary nested function/class declarations have their own `this`.
            case FunctionDeclaration: return false;
            case ClassDeclaration cd: return FindArrowThisExpr(cd.BaseClass);
            default: return false;
        }
    }

    private static bool FindArrowThisExpr(Expression? e)
    {
        if (e is null)
        {
            return false;
        }

        switch (e)
        {
            // Found an arrow: count if its body references `this` lexically.
            case ArrowFunctionExpression arrow:
                if (arrow.Params.Any(ThisRefExpr))
                {
                    return true;
                }

                return arrow.Body switch
                {
                    BlockStatement block => block.Body.Any(ThisRefStmt),
                    Expression expr => ThisRefExpr(expr),
                    _ => false,
                };
            case BinaryExpression bin: return FindArrowThisExpr(bin.Left) || FindArrowThisExpr(bin.Right);
            case LogicalExpression log: return FindArrowThisExpr(log.Left) || FindArrowThisExpr(log.Right);
            case UnaryExpression u: return FindArrowThisExpr(u.Argument);
            case UpdateExpression up: return FindArrowThisExpr(up.Argument);
            case AssignmentExpression a: return FindArrowThisExpr(a.Target) || FindArrowThisExpr(a.Value);
            case AssignmentPattern ap: return FindArrowThisExpr(ap.Target) || FindArrowThisExpr(ap.Default);
            case RestElement rest: return FindArrowThisExpr(rest.Argument);
            case ConditionalExpression c:
                return FindArrowThisExpr(c.Test) || FindArrowThisExpr(c.Consequent) || FindArrowThisExpr(c.Alternate);
            case MemberExpression m:
                return FindArrowThisExpr(m.Object) || (m.Computed && FindArrowThisExpr(m.Property));
            case CallExpression call:
                return FindArrowThisExpr(call.Callee) || call.Arguments.Any(FindArrowThisExpr);
            case NewExpression ne:
                return FindArrowThisExpr(ne.Callee) || ne.Arguments.Any(FindArrowThisExpr);
            case ArrayExpression aex: return aex.Elements.Any(FindArrowThisExpr);
            case ObjectExpression oe:
                foreach (var prop in oe.Properties)
                {
                    if (prop.Computed && FindArrowThisExpr(prop.Key))
                    {
                        return true;
                    }

                    if (FindArrowThisExpr(prop.Value))
                    {
                        return true;
                    }
                }
                return false;
            case SequenceExpression seq: return seq.Expressions.Any(FindArrowThisExpr);
            case TemplateLiteral tpl: return tpl.Expressions.Any(FindArrowThisExpr);
            case TaggedTemplateExpression tte: return FindArrowThisExpr(tte.Tag) || FindArrowThisExpr(tte.Quasi);
            case SpreadElement sp: return FindArrowThisExpr(sp.Argument);
            // Ordinary nested function expressions / classes have their own `this`.
            case FunctionExpression: return false;
            case ClassExpression cls: return FindArrowThisExpr(cls.BaseClass);
            case SuperPropertyExpression sp2: return sp2.Computed && FindArrowThisExpr(sp2.Property);
            case SuperCallExpression scx: return scx.Arguments.Any(FindArrowThisExpr);
            default: return false;
        }
    }

    // Phase 2: once inside an arrow, any `this` (descending through further
    // arrows, not ordinary functions) triggers. Reuses ArgRef*'s traversal but
    // with `this` (not the `arguments` identifier) as the leaf.
    private static bool ThisRefStmt(Statement? s)
    {
        if (s is null)
        {
            return false;
        }

        switch (s)
        {
            case BlockStatement b: return b.Body.Any(ThisRefStmt);
            case ExpressionStatement es: return ThisRefExpr(es.Expression);
            case ReturnStatement r: return ThisRefExpr(r.Argument);
            case ThrowStatement t: return ThisRefExpr(t.Argument);
            case IfStatement i:
                return ThisRefExpr(i.Test) || ThisRefStmt(i.Consequent) || ThisRefStmt(i.Alternate);
            case WhileStatement w: return ThisRefExpr(w.Test) || ThisRefStmt(w.Body);
            case DoWhileStatement dw: return ThisRefStmt(dw.Body) || ThisRefExpr(dw.Test);
            case ForStatement f:
                if (f.Init is Statement fis && ThisRefStmt(fis))
                {
                    return true;
                }

                if (f.Init is Expression fie && ThisRefExpr(fie))
                {
                    return true;
                }

                return ThisRefExpr(f.Test) || ThisRefExpr(f.Update) || ThisRefStmt(f.Body);
            case ForInStatement fi:
                if (fi.Left is Statement fil && ThisRefStmt(fil))
                {
                    return true;
                }

                if (fi.Left is Expression file && ThisRefExpr(file))
                {
                    return true;
                }

                return ThisRefExpr(fi.Right) || ThisRefStmt(fi.Body);
            case ForOfStatement fo:
                if (fo.Left is Statement fol && ThisRefStmt(fol))
                {
                    return true;
                }

                if (fo.Left is Expression foe && ThisRefExpr(foe))
                {
                    return true;
                }

                return ThisRefExpr(fo.Right) || ThisRefStmt(fo.Body);
            case SwitchStatement sw:
                if (ThisRefExpr(sw.Discriminant))
                {
                    return true;
                }

                foreach (var c in sw.Cases)
                {
                    if (ThisRefExpr(c.Test))
                    {
                        return true;
                    }

                    if (c.Consequent.Any(ThisRefStmt))
                    {
                        return true;
                    }
                }
                return false;
            case TryStatement tr:
                if (ThisRefStmt(tr.Block))
                {
                    return true;
                }

                if (tr.Handler is not null && tr.Handler.Body.Body.Any(ThisRefStmt))
                {
                    return true;
                }

                return tr.Finalizer is not null && ThisRefStmt(tr.Finalizer);
            case LabeledStatement ls: return ThisRefStmt(ls.Body);
            case WithStatement ws: return ThisRefExpr(ws.Object) || ThisRefStmt(ws.Body);
            case VariableDeclaration vd:
                return vd.Declarations.Any(d => ThisRefExpr(d.Init));
            // An ordinary nested function/class declaration has its own `this`.
            case FunctionDeclaration: return false;
            case ClassDeclaration cd: return ThisRefExpr(cd.BaseClass);
            default: return false;
        }
    }

    private static bool ThisRefExpr(Expression? e)
    {
        if (e is null)
        {
            return false;
        }

        switch (e)
        {
            case ThisExpression: return true;
            case BinaryExpression bin: return ThisRefExpr(bin.Left) || ThisRefExpr(bin.Right);
            case LogicalExpression log: return ThisRefExpr(log.Left) || ThisRefExpr(log.Right);
            case UnaryExpression u: return ThisRefExpr(u.Argument);
            case UpdateExpression up: return ThisRefExpr(up.Argument);
            case AssignmentExpression a: return ThisRefExpr(a.Target) || ThisRefExpr(a.Value);
            case AssignmentPattern ap: return ThisRefExpr(ap.Target) || ThisRefExpr(ap.Default);
            case RestElement rest: return ThisRefExpr(rest.Argument);
            case ConditionalExpression c:
                return ThisRefExpr(c.Test) || ThisRefExpr(c.Consequent) || ThisRefExpr(c.Alternate);
            case MemberExpression m:
                return ThisRefExpr(m.Object) || (m.Computed && ThisRefExpr(m.Property));
            case CallExpression call:
                return ThisRefExpr(call.Callee) || call.Arguments.Any(ThisRefExpr);
            case NewExpression ne:
                return ThisRefExpr(ne.Callee) || ne.Arguments.Any(ThisRefExpr);
            case ArrayExpression aex: return aex.Elements.Any(ThisRefExpr);
            case ObjectExpression oe:
                foreach (var prop in oe.Properties)
                {
                    if (prop.Computed && ThisRefExpr(prop.Key))
                    {
                        return true;
                    }

                    if (ThisRefExpr(prop.Value))
                    {
                        return true;
                    }
                }
                return false;
            case SequenceExpression seq: return seq.Expressions.Any(ThisRefExpr);
            case TemplateLiteral tpl: return tpl.Expressions.Any(ThisRefExpr);
            case TaggedTemplateExpression tte: return ThisRefExpr(tte.Tag) || ThisRefExpr(tte.Quasi);
            case SpreadElement sp: return ThisRefExpr(sp.Argument);
            // A nested arrow still inherits the same lexical `this`.
            case ArrowFunctionExpression arrow:
                if (arrow.Params.Any(ThisRefExpr))
                {
                    return true;
                }

                return arrow.Body switch
                {
                    BlockStatement block => block.Body.Any(ThisRefStmt),
                    Expression expr => ThisRefExpr(expr),
                    _ => false,
                };
            // Ordinary nested function expressions / classes have their own `this`.
            case FunctionExpression: return false;
            case ClassExpression cls: return ThisRefExpr(cls.BaseClass);
            // `super.x` / `super[k]` / `super(...)` implicitly read the lexical
            // `this`, so they also require the captured binding.
            case SuperPropertyExpression: return true;
            case SuperCallExpression: return true;
            default: return false;
        }
    }

    private static bool ArgRefStmt(Statement? s)
    {
        if (s is null)
        {
            return false;
        }

        switch (s)
        {
            case BlockStatement b: return b.Body.Any(ArgRefStmt);
            case ExpressionStatement es: return ArgRefExpr(es.Expression);
            case ReturnStatement r: return ArgRefExpr(r.Argument);
            case ThrowStatement t: return ArgRefExpr(t.Argument);
            case IfStatement i:
                return ArgRefExpr(i.Test) || ArgRefStmt(i.Consequent) || ArgRefStmt(i.Alternate);
            case WhileStatement w: return ArgRefExpr(w.Test) || ArgRefStmt(w.Body);
            case DoWhileStatement dw: return ArgRefStmt(dw.Body) || ArgRefExpr(dw.Test);
            case ForStatement f:
                if (f.Init is Statement fis && ArgRefStmt(fis))
                {
                    return true;
                }

                if (f.Init is Expression fie && ArgRefExpr(fie))
                {
                    return true;
                }

                return ArgRefExpr(f.Test) || ArgRefExpr(f.Update) || ArgRefStmt(f.Body);
            case ForInStatement fi:
                if (fi.Left is Statement fil && ArgRefStmt(fil))
                {
                    return true;
                }

                if (fi.Left is Expression file && ArgRefExpr(file))
                {
                    return true;
                }

                return ArgRefExpr(fi.Right) || ArgRefStmt(fi.Body);
            case ForOfStatement fo:
                if (fo.Left is Statement fol && ArgRefStmt(fol))
                {
                    return true;
                }

                if (fo.Left is Expression foe && ArgRefExpr(foe))
                {
                    return true;
                }

                return ArgRefExpr(fo.Right) || ArgRefStmt(fo.Body);
            case SwitchStatement sw:
                if (ArgRefExpr(sw.Discriminant))
                {
                    return true;
                }

                foreach (var c in sw.Cases)
                {
                    if (ArgRefExpr(c.Test))
                    {
                        return true;
                    }

                    if (c.Consequent.Any(ArgRefStmt))
                    {
                        return true;
                    }
                }
                return false;
            case TryStatement tr:
                if (ArgRefStmt(tr.Block))
                {
                    return true;
                }

                if (tr.Handler is not null && tr.Handler.Body.Body.Any(ArgRefStmt))
                {
                    return true;
                }

                return tr.Finalizer is not null && ArgRefStmt(tr.Finalizer);
            case LabeledStatement ls: return ArgRefStmt(ls.Body);
            case WithStatement ws: return ArgRefExpr(ws.Object) || ArgRefStmt(ws.Body);
            case VariableDeclaration vd:
                return vd.Declarations.Any(d => ArgRefExpr(d.Init));
            // A nested ordinary function / class declaration establishes its
            // own `arguments` — do not descend.
            case FunctionDeclaration: return false;
            case ClassDeclaration cd: return ArgRefExpr(cd.BaseClass);
            default: return false;
        }
    }

    private static bool ArgRefExpr(Expression? e)
    {
        if (e is null)
        {
            return false;
        }

        switch (e)
        {
            case Identifier id: return id.Name == "arguments";
            case BinaryExpression bin: return ArgRefExpr(bin.Left) || ArgRefExpr(bin.Right);
            case LogicalExpression log: return ArgRefExpr(log.Left) || ArgRefExpr(log.Right);
            case UnaryExpression u: return ArgRefExpr(u.Argument);
            case UpdateExpression up: return ArgRefExpr(up.Argument);
            case AssignmentExpression a: return ArgRefExpr(a.Target) || ArgRefExpr(a.Value);
            case AssignmentPattern ap: return ArgRefExpr(ap.Target) || ArgRefExpr(ap.Default);
            case RestElement rest: return ArgRefExpr(rest.Argument);
            case ConditionalExpression c:
                return ArgRefExpr(c.Test) || ArgRefExpr(c.Consequent) || ArgRefExpr(c.Alternate);
            case MemberExpression m:
                return ArgRefExpr(m.Object) || (m.Computed && ArgRefExpr(m.Property));
            case CallExpression call:
                return ArgRefExpr(call.Callee) || call.Arguments.Any(ArgRefExpr);
            case NewExpression ne:
                return ArgRefExpr(ne.Callee) || ne.Arguments.Any(ArgRefExpr);
            case ArrayExpression aex: return aex.Elements.Any(ArgRefExpr);
            case ObjectExpression oe:
                foreach (var prop in oe.Properties)
                {
                    if (prop.Computed && ArgRefExpr(prop.Key))
                    {
                        return true;
                    }

                    if (ArgRefExpr(prop.Value))
                    {
                        return true;
                    }
                }
                return false;
            case SequenceExpression seq: return seq.Expressions.Any(ArgRefExpr);
            case TemplateLiteral tpl: return tpl.Expressions.Any(ArgRefExpr);
            case TaggedTemplateExpression tte: return ArgRefExpr(tte.Tag) || ArgRefExpr(tte.Quasi);
            case SpreadElement sp: return ArgRefExpr(sp.Argument);
            // Arrows inherit the enclosing `arguments` — descend into them.
            case ArrowFunctionExpression arrow:
                if (arrow.Params.Any(ArgRefExpr))
                {
                    return true;
                }

                return arrow.Body switch
                {
                    BlockStatement block => block.Body.Any(ArgRefStmt),
                    Expression expr => ArgRefExpr(expr),
                    _ => false,
                };
            // Ordinary nested function expressions have their own `arguments`.
            case FunctionExpression: return false;
            case ClassExpression cls: return ArgRefExpr(cls.BaseClass);
            case ArrayPattern arrp:
                foreach (var el in arrp.Elements)
                {
                    switch (el)
                    {
                        case ArrayPatternBindingElement binding:
                            if (ArgRefExpr(binding.Target) || ArgRefExpr(binding.Default))
                            {
                                return true;
                            }

                            break;
                        case ArrayPatternRestElement r:
                            if (ArgRefExpr(r.Target))
                            {
                                return true;
                            }

                            break;
                    }
                }
                return false;
            case ObjectPattern op:
                foreach (var prop in op.Properties)
                {
                    if (prop.Computed && ArgRefExpr(prop.Key))
                    {
                        return true;
                    }

                    if (ArgRefExpr(prop.Target) || ArgRefExpr(prop.Default))
                    {
                        return true;
                    }
                }
                return op.Rest is not null && ArgRefExpr(op.Rest.Argument);
            case SuperPropertyExpression sp2: return sp2.Computed && ArgRefExpr(sp2.Property);
            case SuperCallExpression scx: return scx.Arguments.Any(ArgRefExpr);
            default: return false;
        }
    }

    // ----------------------------------------------------------------------
    // "Outer" walk: we are still in the function whose captures we're
    // computing. Whenever we hit a nested function/class member body, we
    // switch to InnerWalk, which tracks lexical scopes to subtract local
    // declarations from the free-name set.
    // ----------------------------------------------------------------------

    private static void WalkStatementInOuter(Statement? s, HashSet<string> captured)
    {
        if (s is null)
        {
            return;
        }

        switch (s)
        {
            case BlockStatement b: foreach (var x in b.Body) { WalkStatementInOuter(x, captured); } return;
            case ExpressionStatement es: WalkExpressionInOuter(es.Expression, captured); return;
            case ReturnStatement r: if (r.Argument is not null) { WalkExpressionInOuter(r.Argument, captured); } return;
            case ThrowStatement t: WalkExpressionInOuter(t.Argument, captured); return;
            case IfStatement i:
                WalkExpressionInOuter(i.Test, captured);
                WalkStatementInOuter(i.Consequent, captured);
                WalkStatementInOuter(i.Alternate, captured);
                return;
            case WhileStatement w:
                WalkExpressionInOuter(w.Test, captured);
                WalkStatementInOuter(w.Body, captured);
                return;
            case DoWhileStatement dw:
                WalkStatementInOuter(dw.Body, captured);
                WalkExpressionInOuter(dw.Test, captured);
                return;
            case ForStatement f:
                if (f.Init is Statement initS)
                {
                    WalkStatementInOuter(initS, captured);
                }
                else if (f.Init is Expression initE)
                {
                    WalkExpressionInOuter(initE, captured);
                }

                if (f.Test is not null)
                {
                    WalkExpressionInOuter(f.Test, captured);
                }

                if (f.Update is not null)
                {
                    WalkExpressionInOuter(f.Update, captured);
                }

                WalkStatementInOuter(f.Body, captured);
                return;
            case ForInStatement fi:
                if (fi.Left is Statement leftS)
                {
                    WalkStatementInOuter(leftS, captured);
                }
                else if (fi.Left is Expression leftE)
                {
                    WalkExpressionInOuter(leftE, captured);
                }

                WalkExpressionInOuter(fi.Right, captured);
                WalkStatementInOuter(fi.Body, captured);
                return;
            case ForOfStatement fo:
                if (fo.Left is Statement leftS2)
                {
                    WalkStatementInOuter(leftS2, captured);
                }
                else if (fo.Left is Expression leftE2)
                {
                    WalkExpressionInOuter(leftE2, captured);
                }

                WalkExpressionInOuter(fo.Right, captured);
                WalkStatementInOuter(fo.Body, captured);
                return;
            case SwitchStatement sw:
                WalkExpressionInOuter(sw.Discriminant, captured);
                foreach (var c in sw.Cases)
                {
                    if (c.Test is not null)
                    {
                        WalkExpressionInOuter(c.Test, captured);
                    }

                    foreach (var s2 in c.Consequent)
                    {
                        WalkStatementInOuter(s2, captured);
                    }
                }
                return;
            case TryStatement tr:
                WalkStatementInOuter(tr.Block, captured);
                if (tr.Handler is not null)
                {
                    // The catch-binding shadows for the catch body — but we're
                    // not in a nested function, so we don't need to remove it
                    // from the outer's captured set. Just walk the body.
                    foreach (var s2 in tr.Handler.Body.Body)
                    {
                        WalkStatementInOuter(s2, captured);
                    }
                }
                if (tr.Finalizer is not null)
                {
                    WalkStatementInOuter(tr.Finalizer, captured);
                }

                return;
            case LabeledStatement ls: WalkStatementInOuter(ls.Body, captured); return;
            case WithStatement ws:
                WalkExpressionInOuter(ws.Object, captured);
                WalkStatementInOuter(ws.Body, captured);
                return;
            case VariableDeclaration vd:
                foreach (var d in vd.Declarations)
                {
                    if (d.Init is not null)
                    {
                        WalkExpressionInOuter(d.Init, captured);
                    }
                }
                return;
            case FunctionDeclaration fd:
                // A nested function. Compute its free names (after subtracting
                // its own params + inner declarations) and union into captured.
                AddFreeNames(fd.Params, fd.Body.Body, captured);
                return;
            case ClassDeclaration cd:
                if (cd.BaseClass is not null)
                {
                    WalkExpressionInOuter(cd.BaseClass, captured);
                }

                AddClassBodyFreeNames(cd.Body, captured);
                return;
            case EmptyStatement:
            case BreakStatement:
            case ContinueStatement:
            case DebuggerStatement:
                return;
        }
    }

    private static void WalkExpressionInOuter(Expression? e, HashSet<string> captured)
    {
        if (e is null)
        {
            return;
        }

        switch (e)
        {
            case Identifier:
            case NumericLiteral:
            case StringLiteral:
            case BooleanLiteral:
            case NullLiteral:
            case BigIntLiteral:
            case RegExpLiteral:
            case ThisExpression:
            case PrivateNameExpression:
                return;
            case PrivateInExpression pin: WalkExpressionInOuter(pin.Object, captured); return;
            case BinaryExpression bin: WalkExpressionInOuter(bin.Left, captured); WalkExpressionInOuter(bin.Right, captured); return;
            case LogicalExpression log: WalkExpressionInOuter(log.Left, captured); WalkExpressionInOuter(log.Right, captured); return;
            case UnaryExpression u: WalkExpressionInOuter(u.Argument, captured); return;
            case UpdateExpression up: WalkExpressionInOuter(up.Argument, captured); return;
            case AwaitExpression aw: WalkExpressionInOuter(aw.Argument, captured); return;
            case YieldExpression yld: WalkExpressionInOuter(yld.Argument, captured); return;
            case AssignmentExpression a: WalkExpressionInOuter(a.Target, captured); WalkExpressionInOuter(a.Value, captured); return;
            case AssignmentPattern a: WalkExpressionInOuter(a.Target, captured); WalkExpressionInOuter(a.Default, captured); return;
            case RestElement rest: WalkExpressionInOuter(rest.Argument, captured); return;
            case ConditionalExpression c:
                WalkExpressionInOuter(c.Test, captured);
                WalkExpressionInOuter(c.Consequent, captured);
                WalkExpressionInOuter(c.Alternate, captured);
                return;
            case MemberExpression m: WalkExpressionInOuter(m.Object, captured); if (m.Computed) { WalkExpressionInOuter(m.Property, captured); } return;
            case CallExpression call:
                WalkExpressionInOuter(call.Callee, captured);
                foreach (var arg in call.Arguments)
                {
                    WalkExpressionInOuter(arg, captured);
                }

                return;
            case NewExpression ne:
                WalkExpressionInOuter(ne.Callee, captured);
                foreach (var arg in ne.Arguments)
                {
                    WalkExpressionInOuter(arg, captured);
                }

                return;
            case ArrayPattern ap:
                foreach (var el in ap.Elements)
                {
                    switch (el)
                    {
                        case ArrayPatternBindingElement binding:
                            WalkExpressionInOuter(binding.Target, captured);
                            WalkExpressionInOuter(binding.Default, captured);
                            break;
                        case ArrayPatternRestElement rest:
                            WalkExpressionInOuter(rest.Target, captured);
                            break;
                    }
                }
                return;
            case ObjectPattern op:
                foreach (var prop in op.Properties)
                {
                    if (prop.Computed)
                    {
                        WalkExpressionInOuter(prop.Key, captured);
                    }

                    WalkExpressionInOuter(prop.Target, captured);
                    WalkExpressionInOuter(prop.Default, captured);
                }
                if (op.Rest is not null)
                {
                    WalkExpressionInOuter(op.Rest.Argument, captured);
                }

                return;
            case ArrayExpression ae:
                foreach (var el in ae.Elements)
                {
                    WalkExpressionInOuter(el, captured);
                }

                return;
            case ObjectExpression oe:
                foreach (var prop in oe.Properties)
                {
                    if (prop.Computed)
                    {
                        WalkExpressionInOuter(prop.Key, captured);
                    }

                    WalkExpressionInOuter(prop.Value, captured);
                }
                return;
            case SequenceExpression seq:
                foreach (var x in seq.Expressions)
                {
                    WalkExpressionInOuter(x, captured);
                }

                return;
            case TemplateLiteral tpl:
                foreach (var x in tpl.Expressions)
                {
                    WalkExpressionInOuter(x, captured);
                }

                return;
            case TaggedTemplateExpression tte:
                WalkExpressionInOuter(tte.Tag, captured);
                WalkExpressionInOuter(tte.Quasi, captured);
                return;
            case SpreadElement sp: WalkExpressionInOuter(sp.Argument, captured); return;
            case FunctionExpression fe:
                AddFreeNames(fe.Params, fe.Body.Body, captured);
                return;
            case ArrowFunctionExpression arrow:
                {
                    // Arrow body is either an expression or a block.
                    if (arrow.Body is BlockStatement block)
                    {
                        AddFreeNames(arrow.Params, block.Body, captured);
                    }
                    else if (arrow.Body is Expression expr)
                    {
                        // Treat expr-body arrow as a single-return body.
                        var fakeBody = new[] { (Statement)new ReturnStatement(expr, arrow.Start, arrow.End) };
                        AddFreeNames(arrow.Params, fakeBody, captured);
                    }
                }
                return;
            case ClassExpression cls:
                if (cls.BaseClass is not null)
                {
                    WalkExpressionInOuter(cls.BaseClass, captured);
                }

                AddClassBodyFreeNames(cls.Body, captured);
                return;
            case SuperPropertyExpression sp2:
                if (sp2.Computed)
                {
                    WalkExpressionInOuter(sp2.Property, captured);
                }

                return;
            case SuperCallExpression sc:
                foreach (var arg in sc.Arguments)
                {
                    WalkExpressionInOuter(arg, captured);
                }

                return;
        }
    }

    // ----------------------------------------------------------------------
    // "Inner" walk: we're traversing a nested function's body. We push a
    // scope, register the function's own declared names, walk the body, and
    // every Identifier reference that isn't shadowed by a scope frame is a
    // candidate captured-name for the outer.
    // ----------------------------------------------------------------------

    private static void AddFreeNames(
        IReadOnlyList<Expression> parameters,
        IReadOnlyList<Statement> body,
        HashSet<string> outerCaptured)
    {
        var scopes = new Stack<HashSet<string>>();
        scopes.Push(new HashSet<string>(StringComparer.Ordinal));
        // Declare parameter bindings + hoisted function decls + var decls at
        // function-top scope. let/const get added per-block.
        foreach (var p in parameters)
        {
            AddBindingNames(p, scopes.Peek());
        }
        // Hoisted function declarations are bound at function-top scope per §14.1.18.
        foreach (var s in body)
        {
            if (s is FunctionDeclaration fd)
            {
                scopes.Peek().Add(fd.Name.Name);
            }
        }
        // var-hoisting: visit body and collect var names into function-top scope.
        foreach (var s in body)
        {
            CollectVarHoistedBindings(s, scopes.Peek());
        }

        // Walk inner body: any free identifier whose name isn't covered by
        // any active scope frame goes into outerCaptured.
        foreach (var s in body)
        {
            InnerStatement(s, scopes, outerCaptured);
        }

        foreach (var p in parameters)
        {
            // Walk default-value expressions for params (the param identifier
            // itself is bound; defaults can reference earlier params).
            InnerExpression(p, scopes, outerCaptured);
        }
        scopes.Pop();
    }

    private static void AddClassBodyFreeNames(ClassBody body, HashSet<string> outerCaptured)
    {
        if (body.Constructor is not null)
        {
            AddFreeNames(body.Constructor.Params, body.Constructor.Body.Body, outerCaptured);
        }

        foreach (var m in body.Methods)
        {
            AddFreeNames(m.Params, m.Body.Body, outerCaptured);
        }

        foreach (var f in body.Fields)
        {
            if (f.Computed)
            {
                WalkExpressionInOuter(f.Key, outerCaptured);
            }

            if (f.Initializer is not null)
            {
                // Field initializer runs as a one-arg closure (`this` is the
                // instance) — treat it as a nested function with no params.
                var fakeBody = new[] { (Statement)new ExpressionStatement(f.Initializer, f.Initializer.Start, f.Initializer.End) };
                AddFreeNames(Array.Empty<Expression>(), fakeBody, outerCaptured);
            }
        }
        foreach (var sb in body.StaticBlocks)
        {
            AddFreeNames(Array.Empty<Expression>(), sb.Body, outerCaptured);
        }
    }

    private static void CollectVarHoistedBindings(Statement? s, HashSet<string> scope)
    {
        if (s is null)
        {
            return;
        }

        switch (s)
        {
            case VariableDeclaration vd:
                // Per §14.3.2, `var` is function-scoped; `let`/`const` are
                // block-scoped. For the conservative purposes of capture
                // analysis we treat all decls as function-scoped here — this
                // can only over-shadow (mark a name as NOT captured when it
                // actually is in a stricter sense), so we restrict to "var".
                if (vd.Kind == "var")
                {
                    foreach (var d in vd.Declarations)
                    {
                        AddBindingNames(d.Id, scope);
                    }
                }

                return;
            case BlockStatement b: foreach (var x in b.Body) { CollectVarHoistedBindings(x, scope); } return;
            case IfStatement i:
                CollectVarHoistedBindings(i.Consequent, scope);
                CollectVarHoistedBindings(i.Alternate, scope);
                return;
            case WhileStatement w: CollectVarHoistedBindings(w.Body, scope); return;
            case DoWhileStatement dw: CollectVarHoistedBindings(dw.Body, scope); return;
            case ForStatement f:
                if (f.Init is VariableDeclaration fvd && fvd.Kind == "var")
                {
                    foreach (var d in fvd.Declarations)
                    {
                        AddBindingNames(d.Id, scope);
                    }
                }

                CollectVarHoistedBindings(f.Body, scope);
                return;
            case ForInStatement fi:
                if (fi.Left is VariableDeclaration vdi && vdi.Kind == "var")
                {
                    foreach (var d in vdi.Declarations)
                    {
                        AddBindingNames(d.Id, scope);
                    }
                }

                CollectVarHoistedBindings(fi.Body, scope);
                return;
            case ForOfStatement fo:
                if (fo.Left is VariableDeclaration vdo && vdo.Kind == "var")
                {
                    foreach (var d in vdo.Declarations)
                    {
                        AddBindingNames(d.Id, scope);
                    }
                }

                CollectVarHoistedBindings(fo.Body, scope);
                return;
            case SwitchStatement sw:
                foreach (var c in sw.Cases)
                {
                    foreach (var s2 in c.Consequent)
                    {
                        CollectVarHoistedBindings(s2, scope);
                    }
                }

                return;
            case TryStatement tr:
                CollectVarHoistedBindings(tr.Block, scope);
                if (tr.Handler is not null)
                {
                    foreach (var s2 in tr.Handler.Body.Body)
                    {
                        CollectVarHoistedBindings(s2, scope);
                    }
                }

                if (tr.Finalizer is not null)
                {
                    CollectVarHoistedBindings(tr.Finalizer, scope);
                }

                return;
            case LabeledStatement ls: CollectVarHoistedBindings(ls.Body, scope); return;
            // §14.11 — `var` declarations inside a `with` body still hoist to
            // the enclosing function/script variable scope.
            case WithStatement ws: CollectVarHoistedBindings(ws.Body, scope); return;
        }
    }

    private static void AddBindingNames(Expression? pattern, HashSet<string> scope)
    {
        if (pattern is null)
        {
            return;
        }

        switch (pattern)
        {
            case Identifier id: scope.Add(id.Name); return;
            case AssignmentExpression a when a.Op == JsTokenKind.Eq: AddBindingNames(a.Target, scope); return;
            case AssignmentPattern a: AddBindingNames(a.Target, scope); return;
            case ArrayPattern arr:
                foreach (var el in arr.Elements)
                {
                    switch (el)
                    {
                        case ArrayPatternBindingElement binding: AddBindingNames(binding.Target, scope); break;
                        case ArrayPatternRestElement rest: AddBindingNames(rest.Target, scope); break;
                    }
                }
                return;
            case ObjectPattern obj:
                foreach (var prop in obj.Properties)
                {
                    AddBindingNames(prop.Target, scope);
                }

                if (obj.Rest is not null)
                {
                    AddBindingNames(obj.Rest.Argument, scope);
                }

                return;
            case ArrayExpression arr:
                foreach (var el in arr.Elements)
                {
                    if (el is null)
                    {
                        continue;
                    }

                    AddBindingNames(el is SpreadElement sp ? sp.Argument : el, scope);
                }
                return;
            case ObjectExpression obj:
                foreach (var prop in obj.Properties)
                {
                    if (prop.Value is SpreadElement sp)
                    {
                        AddBindingNames(sp.Argument, scope);
                    }
                    else
                    {
                        AddBindingNames(prop.Value, scope);
                    }
                }
                return;
            case SpreadElement spread: AddBindingNames(spread.Argument, scope); return;
        }
    }

    private static void InnerStatement(Statement? s, Stack<HashSet<string>> scopes, HashSet<string> outerCaptured)
    {
        if (s is null)
        {
            return;
        }

        switch (s)
        {
            case BlockStatement b:
                scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                // Hoist let/const + function decls at this block scope.
                foreach (var s2 in b.Body)
                {
                    if (s2 is VariableDeclaration vd && (vd.Kind == "let" || vd.Kind == "const"))
                    {
                        foreach (var d in vd.Declarations)
                        {
                            AddBindingNames(d.Id, scopes.Peek());
                        }
                    }
                    else if (s2 is FunctionDeclaration fd2)
                    {
                        scopes.Peek().Add(fd2.Name.Name);
                    }
                    else if (s2 is ClassDeclaration cd2)
                    {
                        scopes.Peek().Add(cd2.Name.Name);
                    }
                }
                foreach (var x in b.Body)
                {
                    InnerStatement(x, scopes, outerCaptured);
                }

                scopes.Pop();
                return;
            case ExpressionStatement es: InnerExpression(es.Expression, scopes, outerCaptured); return;
            case ReturnStatement r: if (r.Argument is not null) { InnerExpression(r.Argument, scopes, outerCaptured); } return;
            case ThrowStatement t: InnerExpression(t.Argument, scopes, outerCaptured); return;
            case IfStatement i:
                InnerExpression(i.Test, scopes, outerCaptured);
                InnerStatement(i.Consequent, scopes, outerCaptured);
                InnerStatement(i.Alternate, scopes, outerCaptured);
                return;
            case WhileStatement w:
                InnerExpression(w.Test, scopes, outerCaptured);
                InnerStatement(w.Body, scopes, outerCaptured);
                return;
            case DoWhileStatement dw:
                InnerStatement(dw.Body, scopes, outerCaptured);
                InnerExpression(dw.Test, scopes, outerCaptured);
                return;
            case ForStatement f:
                scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                if (f.Init is VariableDeclaration fvd)
                {
                    foreach (var d in fvd.Declarations)
                    {
                        AddBindingNames(d.Id, scopes.Peek());
                    }

                    foreach (var d in fvd.Declarations)
                    {
                        if (d.Init is not null)
                        {
                            InnerExpression(d.Init, scopes, outerCaptured);
                        }
                    }
                }
                else if (f.Init is Expression initE)
                {
                    InnerExpression(initE, scopes, outerCaptured);
                }

                if (f.Test is not null)
                {
                    InnerExpression(f.Test, scopes, outerCaptured);
                }

                if (f.Update is not null)
                {
                    InnerExpression(f.Update, scopes, outerCaptured);
                }

                InnerStatement(f.Body, scopes, outerCaptured);
                scopes.Pop();
                return;
            case ForInStatement fi:
                scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                if (fi.Left is VariableDeclaration vdfi)
                {
                    foreach (var d in vdfi.Declarations)
                    {
                        AddBindingNames(d.Id, scopes.Peek());
                    }
                }
                else if (fi.Left is Expression leftE)
                {
                    InnerExpression(leftE, scopes, outerCaptured);
                }

                InnerExpression(fi.Right, scopes, outerCaptured);
                InnerStatement(fi.Body, scopes, outerCaptured);
                scopes.Pop();
                return;
            case ForOfStatement fo:
                scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                if (fo.Left is VariableDeclaration vdfo)
                {
                    foreach (var d in vdfo.Declarations)
                    {
                        AddBindingNames(d.Id, scopes.Peek());
                    }
                }
                else if (fo.Left is Expression leftE2)
                {
                    InnerExpression(leftE2, scopes, outerCaptured);
                }

                InnerExpression(fo.Right, scopes, outerCaptured);
                InnerStatement(fo.Body, scopes, outerCaptured);
                scopes.Pop();
                return;
            case SwitchStatement sw:
                InnerExpression(sw.Discriminant, scopes, outerCaptured);
                foreach (var c in sw.Cases)
                {
                    if (c.Test is not null)
                    {
                        InnerExpression(c.Test, scopes, outerCaptured);
                    }

                    foreach (var s2 in c.Consequent)
                    {
                        InnerStatement(s2, scopes, outerCaptured);
                    }
                }
                return;
            case TryStatement tr:
                InnerStatement(tr.Block, scopes, outerCaptured);
                if (tr.Handler is not null)
                {
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                    if (tr.Handler.Param is not null)
                    {
                        AddBindingNames(tr.Handler.Param, scopes.Peek());
                    }

                    foreach (var s2 in tr.Handler.Body.Body)
                    {
                        InnerStatement(s2, scopes, outerCaptured);
                    }

                    scopes.Pop();
                }
                if (tr.Finalizer is not null)
                {
                    InnerStatement(tr.Finalizer, scopes, outerCaptured);
                }

                return;
            case LabeledStatement ls: InnerStatement(ls.Body, scopes, outerCaptured); return;
            case WithStatement ws:
                InnerExpression(ws.Object, scopes, outerCaptured);
                InnerStatement(ws.Body, scopes, outerCaptured);
                return;
            case VariableDeclaration vd:
                // For let/const, declare into the topmost scope frame (block);
                // for var, into the function-top (already collected by
                // hoisting); either way the name is now in scope.
                foreach (var d in vd.Declarations)
                {
                    AddBindingNames(d.Id, scopes.Peek());
                    if (d.Init is not null)
                    {
                        InnerExpression(d.Init, scopes, outerCaptured);
                    }
                }
                return;
            case FunctionDeclaration fd:
                scopes.Peek().Add(fd.Name.Name);
                // The nested-nested function is yet another walk; treat it
                // as a free-name source against the SAME outerCaptured (any
                // identifier it references that isn't shadowed in scopes
                // or in its own internals belongs to the outermost owner).
                InnerNestedFunctionFreeNames(fd.Params, fd.Body.Body, scopes, outerCaptured);
                return;
            case ClassDeclaration cd:
                scopes.Peek().Add(cd.Name.Name);
                if (cd.BaseClass is not null)
                {
                    InnerExpression(cd.BaseClass, scopes, outerCaptured);
                }

                InnerClassBody(cd.Body, scopes, outerCaptured);
                return;
            case EmptyStatement:
            case BreakStatement:
            case ContinueStatement:
            case DebuggerStatement:
                return;
        }
    }

    private static void InnerExpression(Expression? e, Stack<HashSet<string>> scopes, HashSet<string> outerCaptured)
    {
        if (e is null)
        {
            return;
        }

        switch (e)
        {
            case Identifier id:
                if (!IsBoundIn(scopes, id.Name))
                {
                    outerCaptured.Add(id.Name);
                }

                return;
            case NumericLiteral:
            case StringLiteral:
            case BooleanLiteral:
            case NullLiteral:
            case BigIntLiteral:
            case RegExpLiteral:
            case ThisExpression:
            case PrivateNameExpression:
                return;
            case PrivateInExpression pin: InnerExpression(pin.Object, scopes, outerCaptured); return;
            case BinaryExpression bin: InnerExpression(bin.Left, scopes, outerCaptured); InnerExpression(bin.Right, scopes, outerCaptured); return;
            case LogicalExpression log: InnerExpression(log.Left, scopes, outerCaptured); InnerExpression(log.Right, scopes, outerCaptured); return;
            case UnaryExpression u: InnerExpression(u.Argument, scopes, outerCaptured); return;
            case UpdateExpression up: InnerExpression(up.Argument, scopes, outerCaptured); return;
            case AwaitExpression aw: InnerExpression(aw.Argument, scopes, outerCaptured); return;
            case YieldExpression yld: InnerExpression(yld.Argument, scopes, outerCaptured); return;
            case AssignmentExpression a: InnerExpression(a.Target, scopes, outerCaptured); InnerExpression(a.Value, scopes, outerCaptured); return;
            case ConditionalExpression c:
                InnerExpression(c.Test, scopes, outerCaptured);
                InnerExpression(c.Consequent, scopes, outerCaptured);
                InnerExpression(c.Alternate, scopes, outerCaptured);
                return;
            case MemberExpression m: InnerExpression(m.Object, scopes, outerCaptured); if (m.Computed) { InnerExpression(m.Property, scopes, outerCaptured); } return;
            case CallExpression call:
                InnerExpression(call.Callee, scopes, outerCaptured);
                foreach (var arg in call.Arguments)
                {
                    InnerExpression(arg, scopes, outerCaptured);
                }

                return;
            case NewExpression ne:
                InnerExpression(ne.Callee, scopes, outerCaptured);
                foreach (var arg in ne.Arguments)
                {
                    InnerExpression(arg, scopes, outerCaptured);
                }

                return;
            case ArrayPattern ap:
                foreach (var el in ap.Elements)
                {
                    switch (el)
                    {
                        case ArrayPatternBindingElement binding:
                            InnerExpression(binding.Target, scopes, outerCaptured);
                            InnerExpression(binding.Default, scopes, outerCaptured);
                            break;
                        case ArrayPatternRestElement rest:
                            InnerExpression(rest.Target, scopes, outerCaptured);
                            break;
                    }
                }
                return;
            case ObjectPattern op:
                foreach (var prop in op.Properties)
                {
                    if (prop.Computed)
                    {
                        InnerExpression(prop.Key, scopes, outerCaptured);
                    }

                    InnerExpression(prop.Target, scopes, outerCaptured);
                    InnerExpression(prop.Default, scopes, outerCaptured);
                }
                if (op.Rest is not null)
                {
                    InnerExpression(op.Rest.Argument, scopes, outerCaptured);
                }

                return;
            case AssignmentPattern ap:
                InnerExpression(ap.Target, scopes, outerCaptured);
                InnerExpression(ap.Default, scopes, outerCaptured);
                return;
            case RestElement rest:
                InnerExpression(rest.Argument, scopes, outerCaptured);
                return;
            case ArrayExpression ae:
                foreach (var el in ae.Elements)
                {
                    InnerExpression(el, scopes, outerCaptured);
                }

                return;
            case ObjectExpression oe:
                foreach (var prop in oe.Properties)
                {
                    if (prop.Computed)
                    {
                        InnerExpression(prop.Key, scopes, outerCaptured);
                    }

                    InnerExpression(prop.Value, scopes, outerCaptured);
                }
                return;
            case SequenceExpression seq:
                foreach (var x in seq.Expressions)
                {
                    InnerExpression(x, scopes, outerCaptured);
                }

                return;
            case TemplateLiteral tpl:
                foreach (var x in tpl.Expressions)
                {
                    InnerExpression(x, scopes, outerCaptured);
                }

                return;
            case TaggedTemplateExpression tte:
                InnerExpression(tte.Tag, scopes, outerCaptured);
                InnerExpression(tte.Quasi, scopes, outerCaptured);
                return;
            case SpreadElement sp: InnerExpression(sp.Argument, scopes, outerCaptured); return;
            case FunctionExpression fe:
                InnerNestedFunctionFreeNames(fe.Params, fe.Body.Body, scopes, outerCaptured);
                return;
            case ArrowFunctionExpression arrow:
                {
                    IReadOnlyList<Statement> body;
                    if (arrow.Body is BlockStatement block)
                    {
                        body = block.Body;
                    }
                    else if (arrow.Body is Expression expr)
                    {
                        body = new[] { (Statement)new ReturnStatement(expr, arrow.Start, arrow.End) };
                    }
                    else
                    {
                        body = Array.Empty<Statement>();
                    }

                    InnerNestedFunctionFreeNames(arrow.Params, body, scopes, outerCaptured);
                }
                return;
            case ClassExpression cls:
                if (cls.BaseClass is not null)
                {
                    InnerExpression(cls.BaseClass, scopes, outerCaptured);
                }

                InnerClassBody(cls.Body, scopes, outerCaptured);
                return;
            case SuperPropertyExpression sp2:
                if (sp2.Computed)
                {
                    InnerExpression(sp2.Property, scopes, outerCaptured);
                }

                return;
            case SuperCallExpression sc:
                foreach (var arg in sc.Arguments)
                {
                    InnerExpression(arg, scopes, outerCaptured);
                }

                return;
        }
    }

    /// <summary>A nested-nested function: push a fresh scope frame seeded
    /// with the nested function's own bindings (params + var/let hoists +
    /// inner function decls), then walk its body. Any free identifier still
    /// not resolved against any active scope frame escapes all the way up
    /// to <paramref name="outerCaptured"/>, which is the set we'll
    /// intersect with the outermost function's declarations.</summary>
    private static void InnerNestedFunctionFreeNames(
        IReadOnlyList<Expression> parameters,
        IReadOnlyList<Statement> body,
        Stack<HashSet<string>> scopes,
        HashSet<string> outerCaptured)
    {
        scopes.Push(new HashSet<string>(StringComparer.Ordinal));
        foreach (var p in parameters)
        {
            AddBindingNames(p, scopes.Peek());
        }

        foreach (var s in body)
        {
            if (s is FunctionDeclaration fd)
            {
                scopes.Peek().Add(fd.Name.Name);
            }
        }

        foreach (var s in body)
        {
            CollectVarHoistedBindings(s, scopes.Peek());
        }

        foreach (var s in body)
        {
            InnerStatement(s, scopes, outerCaptured);
        }

        foreach (var p in parameters)
        {
            InnerExpression(p, scopes, outerCaptured);
        }

        scopes.Pop();
    }

    private static void InnerClassBody(ClassBody body, Stack<HashSet<string>> scopes, HashSet<string> outerCaptured)
    {
        if (body.Constructor is not null)
        {
            InnerNestedFunctionFreeNames(body.Constructor.Params, body.Constructor.Body.Body, scopes, outerCaptured);
        }

        foreach (var m in body.Methods)
        {
            InnerNestedFunctionFreeNames(m.Params, m.Body.Body, scopes, outerCaptured);
        }

        foreach (var f in body.Fields)
        {
            if (f.Computed)
            {
                InnerExpression(f.Key, scopes, outerCaptured);
            }

            if (f.Initializer is not null)
            {
                var fakeBody = new[] { (Statement)new ExpressionStatement(f.Initializer, f.Initializer.Start, f.Initializer.End) };
                InnerNestedFunctionFreeNames(Array.Empty<Expression>(), fakeBody, scopes, outerCaptured);
            }
        }
        foreach (var sb in body.StaticBlocks)
        {
            InnerNestedFunctionFreeNames(Array.Empty<Expression>(), sb.Body, scopes, outerCaptured);
        }
    }

    private static bool IsBoundIn(Stack<HashSet<string>> scopes, string name)
    {
        foreach (var frame in scopes)
        {
            if (frame.Contains(name))
            {
                return true;
            }
        }

        return false;
    }
}
