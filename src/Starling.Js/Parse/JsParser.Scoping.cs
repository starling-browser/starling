using Starling.Js.Ast;
using Starling.Js.Lex;

namespace Starling.Js.Parse;

/// <summary>
/// Lexical-scope early errors (ES2024 §14.2.1 Block, §14.12.1 CaseBlock,
/// §15.2.1 FunctionBody, §16.1.1 Script). After a statement-list scope is
/// parsed we verify its <c>LexicallyDeclaredNames</c> have no duplicates and
/// do not collide with that scope's <c>VarDeclaredNames</c>. These are the
/// classic <c>let x; let x;</c> / <c>let f; var f;</c> / two-function-decls
/// redeclaration <see cref="JsParseException"/>s.
/// </summary>
public ref partial struct JsParser
{
    /// <summary>Statement-list scope flavours. They differ only in whether a
    /// top-level FunctionDeclaration is lexically or var-scoped.</summary>
    private enum ScopeKind
    {
        /// <summary>Block / CaseBlock: top-level function declarations are
        /// lexically declared, so two with the same name (or one colliding with
        /// a let/const/class) is an early error.</summary>
        Block,
        /// <summary>FunctionBody / Script: top-level function declarations are
        /// var-scoped; duplicates among themselves are legal, but they collide
        /// with any top-level let/const/class of the same name.</summary>
        TopLevel,
    }

    /// <summary>Check the lexical/var early errors for a statement-list scope.</summary>
    private void CheckScopeEarlyErrors(IReadOnlyList<Statement> body, ScopeKind kind)
    {
        var lexical = new Dictionary<string, JsPosition>(StringComparer.Ordinal);
        var varNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stmt in body)
        {
            switch (stmt)
            {
                case VariableDeclaration vd when vd.Kind is "let" or "const":
                    foreach (var name in BoundNamesOf(vd))
                    {
                        AddLexical(lexical, name.Name, name.Pos);
                    }

                    break;
                case ClassDeclaration cd:
                    AddLexical(lexical, cd.Name.Name, cd.Name.Start);
                    break;
                case FunctionDeclaration fd:
                    if (kind == ScopeKind.Block)
                    {
                        AddLexical(lexical, fd.Name.Name, fd.Name.Start);
                    }
                    else
                    {
                        varNames.Add(fd.Name.Name); // var-scoped at function/script top level
                    }

                    break;
                case LabeledStatement lab when Unlabel(lab) is FunctionDeclaration lfd:
                    // A labelled function declaration is sloppy-only and is
                    // var-scoped for these purposes; treat like a plain function.
                    if (kind == ScopeKind.Block)
                    {
                        AddLexical(lexical, lfd.Name.Name, lfd.Name.Start);
                    }
                    else
                    {
                        varNames.Add(lfd.Name.Name);
                    }

                    break;
            }
        }

        // VarDeclaredNames across the whole scope (transitively, not crossing
        // function/class boundaries).
        foreach (var stmt in body)
        {
            CollectVarNames(stmt, varNames);
        }

        // Early error: a lexically-declared name also appears as a var name.
        foreach (var kv in lexical)
        {
            if (varNames.Contains(kv.Key))
            {
                throw new JsParseException(
                    $"'{kv.Key}' is already declared as a var binding", kv.Value);
            }
        }
    }

    /// <summary>§14.3.1.1 — the BoundNames of a <c>let</c>/<c>const</c>
    /// declaration may not contain <c>let</c>. Recurses through destructuring
    /// patterns.</summary>
    private static void CheckLexicalBindingNotLet(Expression target)
    {
        switch (target)
        {
            case Identifier { Name: "let" } id:
                throw new JsParseException(
                    "'let' is not a valid lexical binding name", id.Start);
            case AssignmentPattern ap:
                CheckLexicalBindingNotLet(ap.Target);
                break;
            case ArrayPattern arr:
                foreach (var el in arr.Elements)
                {
                    if (el is ArrayPatternBindingElement be)
                    {
                        CheckLexicalBindingNotLet(be.Target);
                    }
                    else if (el is ArrayPatternRestElement re)
                    {
                        CheckLexicalBindingNotLet(re.Target);
                    }
                }
                break;
            case ObjectPattern obj:
                foreach (var p in obj.Properties)
                {
                    CheckLexicalBindingNotLet(p.Target);
                }

                if (obj.Rest is not null)
                {
                    CheckLexicalBindingNotLet(obj.Rest.Argument);
                }

                break;
            case RestElement rest:
                CheckLexicalBindingNotLet(rest.Argument);
                break;
        }
    }

    private static Statement Unlabel(Statement s)
    {
        while (s is LabeledStatement lab)
        {
            s = lab.Body;
        }

        return s;
    }

    /// <summary>§14.7.5.1 — a <c>for</c>-loop whose head is a lexical
    /// declaration (<c>for (let/const … in/of …)</c> or
    /// <c>for (let/const …; …; …)</c>) is a SyntaxError if any of its
    /// BoundNames also appears in the VarDeclaredNames of the loop body
    /// (<c>for (let x in y) { var x; }</c>).</summary>
    private static void CheckForHeadLexicalVsBodyVar(AstNode? head, Statement body)
    {
        if (head is not VariableDeclaration vd || vd.Kind is not ("let" or "const"))
        {
            return;
        }

        var bodyVars = new HashSet<string>(StringComparer.Ordinal);
        CollectVarNames(body, bodyVars);
        if (bodyVars.Count == 0)
        {
            return;
        }

        foreach (var n in BoundNamesOf(vd))
        {
            if (bodyVars.Contains(n.Name))
            {
                throw new JsParseException(
                    $"'{n.Name}' is already declared as a var binding", n.Pos);
            }
        }
    }

    /// <summary>§15.2.1 / §15.3.1 etc. — a FormalParameter BindingIdentifier may
    /// not also be a LexicallyDeclaredName of the FunctionBody
    /// (<c>function f(x){ let x; }</c> is a SyntaxError). Only meaningful when
    /// the parameter list is not redeclarable as var; we conservatively check
    /// against the body's top-level let/const/class names.</summary>
    private void CheckParamsVsLexicalBody(IReadOnlyList<Expression> @params, BlockStatement body)
    {
        // Collect the body's top-level LexicallyDeclaredNames (let/const/class;
        // function declarations are var-scoped at the body top level and may
        // legally shadow a parameter, so they are excluded).
        HashSet<string>? lexical = null;
        foreach (var stmt in body.Body)
        {
            switch (stmt)
            {
                case VariableDeclaration vd when vd.Kind is "let" or "const":
                    foreach (var n in BoundNamesOf(vd))
                    {
                        (lexical ??= new(StringComparer.Ordinal)).Add(n.Name);
                    }

                    break;
                case ClassDeclaration cd:
                    (lexical ??= new(StringComparer.Ordinal)).Add(cd.Name.Name);
                    break;
            }
        }
        if (lexical is null || lexical.Count == 0)
        {
            return;
        }

        var paramNames = new List<(string Name, JsPosition Pos)>();
        foreach (var p in @params)
        {
            CollectPatternNames(p, paramNames);
        }

        foreach (var (name, pos) in paramNames)
        {
            if (lexical.Contains(name))
            {
                throw new JsParseException(
                    $"'{name}' is already declared as a parameter", pos);
            }
        }
    }

    /// <summary>Parse a Statement in a position that forbids declarations — the
    /// body of <c>if</c>/<c>while</c>/<c>do</c>/<c>for</c>/<c>with</c> and a
    /// label. Per the grammar these positions take a <em>Statement</em>, not a
    /// <em>StatementListItem</em>, so a <c>let</c>/<c>const</c>/<c>class</c>
    /// declaration is an early SyntaxError. A FunctionDeclaration is an error in
    /// strict code; in sloppy code Annex B.3.4 (if-body) / B.3.2 (label-body)
    /// permits a plain (non-generator, non-async) function declaration, gated by
    /// <paramref name="allowSloppyFunction"/>.</summary>
    private Statement ParseSubStatement(bool allowSloppyFunction = false)
    {
        // A lexical declaration head is never a valid Statement here.
        if (_current.Kind is JsTokenKind.Const
            || (_current.Kind == JsTokenKind.Identifier && _current.TextEquals("let")
                && IsLetDeclarationStart()))
        {
            throw new JsParseException(
                "lexical declaration cannot appear in a single-statement context",
                _current.Start);
        }

        if (_current.Kind == JsTokenKind.Class)
        {
            throw new JsParseException(
                "class declaration cannot appear in a single-statement context",
                _current.Start);
        }

        if (_current.Kind == JsTokenKind.Function)
        {
            if (_strict || !allowSloppyFunction)
            {
                throw new JsParseException(
                    "function declaration cannot appear in a single-statement context",
                    _current.Start);
            }
            // sloppy Annex B: a generator declaration is still forbidden here.
            if (_lex.Peek().Kind == JsTokenKind.Star)
            {
                throw new JsParseException(
                    "generator declaration cannot appear in a single-statement context",
                    _current.Start);
            }
        }
        // `async function …` declaration is never allowed as a sub-statement.
        if (_current.Kind == JsTokenKind.Identifier && _current.TextEquals("async")
            && _lex.Peek().Kind == JsTokenKind.Function)
        {
            throw new JsParseException(
                "async function declaration cannot appear in a single-statement context",
                _current.Start);
        }
        // The iteration-body labelled-function ban (set by ParseIterationBody)
        // only applies to a label chain DIRECTLY in the body position. Once we
        // enter any non-label statement (a block, etc.) the Annex B extension
        // applies normally again, so clear the flag here.
        var isLabel = _current.Kind == JsTokenKind.Identifier
            && _lex.Peek().Kind == JsTokenKind.Colon;
        if (!isLabel)
        {
            _forbidLabelledFunction = false;
        }

        return ParseStatement();
    }

    /// <summary>Parse the body of an iteration statement
    /// (<c>for</c>/<c>for-in</c>/<c>for-of</c>/<c>while</c>/<c>do-while</c>). Same
    /// as <see cref="ParseSubStatement"/> but additionally forbids a labelled
    /// FunctionDeclaration body (Annex B.3.2 does not extend to iteration
    /// bodies, so <c>for (;;) lbl: function f(){}</c> is always a SyntaxError).</summary>
    private Statement ParseIterationBody()
    {
        var saved = _forbidLabelledFunction;
        _forbidLabelledFunction = true;
        try { return ParseSubStatement(); }
        finally { _forbidLabelledFunction = saved; }
    }

    private static void AddLexical(Dictionary<string, JsPosition> lexical, string name, JsPosition pos)
    {
        if (!lexical.TryAdd(name, pos))
        {
            throw new JsParseException(
                $"'{name}' has already been declared", pos);
        }
    }

    /// <summary>VarDeclaredNames: walk into nested statements collecting `var`
    /// binding names. Stops at any new function/class boundary — those
    /// introduce their own scope.</summary>
    private static void CollectVarNames(Statement stmt, HashSet<string> into)
    {
        switch (stmt)
        {
            case VariableDeclaration vd when vd.Kind == "var":
                foreach (var n in BoundNamesOf(vd))
                {
                    into.Add(n.Name);
                }

                break;
            case BlockStatement block:
                foreach (var s in block.Body)
                {
                    CollectVarNames(s, into);
                }

                break;
            case IfStatement ifs:
                CollectVarNames(ifs.Consequent, into);
                if (ifs.Alternate is not null)
                {
                    CollectVarNames(ifs.Alternate, into);
                }

                break;
            case WhileStatement w:
                CollectVarNames(w.Body, into);
                break;
            case DoWhileStatement dw:
                CollectVarNames(dw.Body, into);
                break;
            case ForStatement f:
                if (f.Init is VariableDeclaration fvd && fvd.Kind == "var")
                {
                    foreach (var n in BoundNamesOf(fvd))
                    {
                        into.Add(n.Name);
                    }
                }

                CollectVarNames(f.Body, into);
                break;
            case ForInStatement fin:
                if (fin.Left is VariableDeclaration finvd && finvd.Kind == "var")
                {
                    foreach (var n in BoundNamesOf(finvd))
                    {
                        into.Add(n.Name);
                    }
                }

                CollectVarNames(fin.Body, into);
                break;
            case ForOfStatement fof:
                if (fof.Left is VariableDeclaration fofvd && fofvd.Kind == "var")
                {
                    foreach (var n in BoundNamesOf(fofvd))
                    {
                        into.Add(n.Name);
                    }
                }

                CollectVarNames(fof.Body, into);
                break;
            case SwitchStatement sw:
                foreach (var c in sw.Cases)
                {
                    foreach (var s in c.Consequent)
                    {
                        CollectVarNames(s, into);
                    }
                }

                break;
            case TryStatement t:
                foreach (var s in t.Block.Body)
                {
                    CollectVarNames(s, into);
                }

                if (t.Handler is not null)
                {
                    foreach (var s in t.Handler.Body.Body)
                    {
                        CollectVarNames(s, into);
                    }
                }

                if (t.Finalizer is not null)
                {
                    foreach (var s in t.Finalizer.Body)
                    {
                        CollectVarNames(s, into);
                    }
                }

                break;
            case LabeledStatement lab:
                CollectVarNames(lab.Body, into);
                break;
            case WithStatement ws:
                CollectVarNames(ws.Body, into);
                break;
                // FunctionDeclaration / ClassDeclaration: do NOT descend (own scope).
        }
    }

    /// <summary>wp:M3-72 — §19.2.1.3 EvalDeclarationInstantiation step 5/8: the
    /// VarDeclaredNames of an eval body — all <c>var</c> binding names plus all
    /// top-level (var-scoped) function-declaration names, gathered transitively
    /// without crossing nested function/class boundaries. A direct eval may not
    /// hoist any of these over a like-named lexical binding in the caller's
    /// environment chain.</summary>
    internal static IReadOnlyCollection<string> EvalVarDeclaredNames(Program program)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stmt in program.Body)
        {
            switch (stmt)
            {
                case FunctionDeclaration fd:
                    names.Add(fd.Name.Name);
                    break;
                case LabeledStatement lab when Unlabel(lab) is FunctionDeclaration lfd:
                    names.Add(lfd.Name.Name);
                    break;
            }
        }
        foreach (var stmt in program.Body)
        {
            CollectVarNames(stmt, names);
        }

        return names;
    }

    /// <summary>wp:M3-72 — the top-level lexically-declared names of an eval body
    /// (<c>let</c>/<c>const</c>/<c>class</c>). Used by the
    /// EvalDeclarationInstantiation collision check (a caller var hoisting into
    /// the eval's own var-env is unaffected, but the eval's own lexical names are
    /// the new lexical environment its body sees).</summary>
    internal static IReadOnlyCollection<string> EvalLexicallyDeclaredNames(Program program)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stmt in program.Body)
        {
            switch (stmt)
            {
                case VariableDeclaration vd when vd.Kind is "let" or "const":
                    foreach (var n in BoundNamesOf(vd))
                    {
                        names.Add(n.Name);
                    }

                    break;
                case ClassDeclaration cd:
                    names.Add(cd.Name.Name);
                    break;
            }
        }
        return names;
    }

    /// <summary>BoundNames of a VariableDeclaration — flattens destructuring
    /// patterns into their constituent identifier bindings.</summary>
    private static List<(string Name, JsPosition Pos)> BoundNamesOf(VariableDeclaration vd)
    {
        var result = new List<(string Name, JsPosition Pos)>();
        foreach (var d in vd.Declarations)
        {
            CollectPatternNames(d.Id, result);
        }

        return result;
    }

    private static void CollectPatternNames(Expression target, List<(string Name, JsPosition Pos)> into)
    {
        switch (target)
        {
            case Identifier id:
                into.Add((id.Name, id.Start));
                break;
            case ArrayPattern arr:
                foreach (var el in arr.Elements)
                {
                    switch (el)
                    {
                        case ArrayPatternBindingElement be:
                            CollectPatternNames(be.Target, into);
                            break;
                        case ArrayPatternRestElement re:
                            CollectPatternNames(re.Target, into);
                            break;
                    }
                }
                break;
            case ObjectPattern obj:
                foreach (var p in obj.Properties)
                {
                    CollectPatternNames(p.Target, into);
                }

                if (obj.Rest is not null)
                {
                    CollectPatternNames(obj.Rest.Argument, into);
                }

                break;
            case AssignmentPattern ap:
                CollectPatternNames(ap.Target, into);
                break;
            case RestElement rest:
                CollectPatternNames(rest.Argument, into);
                break;
        }
    }
}
