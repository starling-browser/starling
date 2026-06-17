using Starling.Js.Ast;
using Starling.Js.Lex;

namespace Starling.Js.Parse;

/// <summary>
/// Statement-level parsing — wp:M3-02b. Extends the expression parser
/// in <c>JsParser.cs</c> via partial class. Method names mirror
/// ES2024 §14 sub-sections.
/// </summary>
public ref partial struct JsParser
{
    /// <summary>wp:M3-71 — §19.2.1.1 — parse a DIRECT-eval program seeding the
    /// caller's lexical context: caller strictness, in-function-ness (so
    /// <c>new.target</c> is legal), in-method-ness (so a SuperProperty is legal),
    /// and derived-constructor-ness (so a SuperCall is legal). With these seeded,
    /// the §13.3 early errors fire (or don't) exactly as if the eval'd code were
    /// spliced into the caller. Indirect/global eval uses the parameterless
    /// <see cref="ParseProgram()"/> and inherits none of this.</summary>
    public Program ParseProgram(DirectEvalContext ctx)
    {
        if (ctx.CallerStrict)
        {
            _strict = true;
        }

        if (ctx.InFunction)
        {
            _functionDepth = 1;       // §13.3.12 — new.target legal
        }

        if (ctx.InMethod)
        {
            _superPropertyDepth = 1;    // §13.3.7.1 — SuperProperty legal
        }

        if (ctx.InDerivedConstructor)
        {
            _derivedConstructorDepth = 1; // SuperCall legal
        }

        return ParseProgram();
    }

    /// <summary>Parse a complete program — top-level statement list.</summary>
    public Program ParseProgram()
    {
        var start = _current.Start;
        var body = new List<Statement>();
        // §11.2.1 / §16.1.1 — scan the directive prologue first. A "use strict"
        // directive anywhere in the prologue makes the WHOLE script strict, so
        // strictness must be established before parsing the rest (early errors
        // in the body depend on it). The prologue is parsed twice-tolerant: we
        // collect the leading string-literal statements, set _strict if any is
        // "use strict", then continue parsing the body under that strictness.
        ScanDirectivePrologue(body, isProgram: true);
        while (!Check(JsTokenKind.EndOfFile))
        {
            body.Add(ParseProgramStatement());
        }
        var end = _current.End;
        // §16.1.1 — Script top-level lexical/var early errors.
        CheckScopeEarlyErrors(body, ScopeKind.TopLevel);
        // §13.2.5.1 — any object literal carrying a CoverInitializedName that was
        // never reinterpreted as a destructuring pattern was used as a value,
        // which is an early SyntaxError.
        CheckNoPendingCoverInit();
        return new Program(body, start, end, Strict: _strict);
    }

    /// <summary>§13.2.5.1 — throw if any object with a CoverInitializedName
    /// (`{ a = 1 }`) was used as a value rather than reinterpreted as a
    /// destructuring pattern.</summary>
    private void CheckNoPendingCoverInit()
    {
        foreach (var obj in _coverInitObjects)
        {
            throw new JsParseException(
                "shorthand property with initializer is only valid in a destructuring pattern",
                obj.Start);
        }
    }

    /// <summary>Parse a Module goal (§16.2.1.6). Module code is always strict
    /// (§11.2.2) and is an implicit <c>[+Await]</c> context at its top level
    /// (§16.2.1.6.2), so <c>await</c> is the AwaitExpression keyword and may not
    /// be a binding identifier in module global code. Routes through the same
    /// program body parser as a classic Script so import/export declarations stay
    /// at program scope, then applies the Module-specific early errors
    /// (§16.2.1.5 / §16.2.1.6) via <see cref="CheckModuleEarlyErrors"/>.</summary>
    public Program ParseModule()
    {
        _module = true;          // goal symbol is Module (persists through the parse)
        _strict = true;          // §11.2.2 — module code is always strict
        _moduleTopAwait = true;  // §16.2.1.6.2 — module top level is [+Await]
        var program = ParseProgram();
        CheckModuleEarlyErrors(program.Body);
        return program;
    }

    /// <summary>§11.2.1 — parse the directive prologue (leading
    /// ExpressionStatements that are bare StringLiterals), appending each
    /// parsed statement to <paramref name="into"/>. Sets <see cref="_strict"/>
    /// if a "use strict" directive is present. Stops at (and does not consume)
    /// the first non-directive statement.</summary>
    private void ScanDirectivePrologue(List<Statement> into, bool isProgram)
    {
        // §11.2.1 — if a "use strict" directive appears, any directive in the
        // prologue that contained a legacy octal escape is a SyntaxError, even
        // though that directive was lexed/parsed before strictness was known.
        JsPosition? sawOctalDirective = null;
        while (_current.Kind == JsTokenKind.StringLiteral)
        {
            // Capture the RAW lexeme + octal tag before parsing — only an
            // unescaped "use strict" / 'use strict' counts (§11.2.2).
            var lexeme = _current.Lexeme;
            var octal = _current.LegacyOctal;
            var octalPos = _current.Start;
            var stmt = isProgram ? ParseProgramStatement() : ParseStatement();
            into.Add(stmt);
            if (!IsDirective(stmt))
            {
                break; // a string used as part of a larger expression ends the prologue
            }

            if (octal && sawOctalDirective is null)
            {
                sawOctalDirective = octalPos;
            }

            if (IsUseStrictDirective(stmt, lexeme))
            {
                _strict = true;
                _prologueHadUseStrict = true;
                if (sawOctalDirective is { } p)
                {
                    throw new JsParseException(
                        "octal escape sequences are not allowed in a strict-mode directive prologue", p);
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Statement dispatch (§14.1)
    // -----------------------------------------------------------------------
    private Statement ParseStatement()
    {
        switch (_current.Kind)
        {
            case JsTokenKind.LBrace: return ParseBlock();
            case JsTokenKind.Semicolon: return ParseEmpty();
            case JsTokenKind.If: return ParseIf();
            case JsTokenKind.While: return ParseWhile();
            case JsTokenKind.Do: return ParseDoWhile();
            case JsTokenKind.For: return ParseFor();
            case JsTokenKind.Return: return ParseReturn();
            case JsTokenKind.Break: return ParseBreak();
            case JsTokenKind.Continue: return ParseContinue();
            case JsTokenKind.Throw: return ParseThrow();
            case JsTokenKind.Try: return ParseTry();
            case JsTokenKind.Switch: return ParseSwitch();
            case JsTokenKind.Debugger: return ParseDebugger();
            case JsTokenKind.Function: return ParseFunctionDeclaration();
            case JsTokenKind.Class: return ParseClassDeclarationWithExtendsTracking();
            case JsTokenKind.Var: return ParseVar("var");
            case JsTokenKind.Const: return ParseVar("const");
            case JsTokenKind.With: return ParseWith();
        }
        // 'let' is contextual; treat as variable decl when followed by an
        // identifier or pattern starter, else expression statement.
        if (_current.Kind == JsTokenKind.Identifier && _current.TextEquals("let")
            && IsLetDeclarationStart())
        {
            return ParseVar("let");
        }
        // B1b-2c — `async function` at statement level → async function decl.
        if (_current.Kind == JsTokenKind.Identifier && _current.TextEquals("async")
            && _lex.Peek().Kind == JsTokenKind.Function)
        {
            var asyncStart = _current.Start;
            Advance(); // async
            return ParseFunctionDeclaration(asyncStart, isAsync: true);
        }
        // §14.13 LabeledStatement — `identifier : Statement`
        if (_current.Kind == JsTokenKind.Identifier && _lex.Peek().Kind == JsTokenKind.Colon)
        {
            return ParseLabeledStatement();
        }
        return ParseExpressionStatement();
    }

    /// <summary>
    /// Decide whether a contextual 'let' Identifier at the head of a
    /// statement is starting a variable declaration or an expression
    /// statement. ES2024 §14.3.1 says <c>let</c> followed by <c>[</c>,
    /// <c>{</c>, or an Identifier (and not a reserved word) starts a
    /// LexicalDeclaration. We use the lexer's one-token peek to look
    /// past <c>let</c> without consuming it.
    /// </summary>
    private bool IsLetDeclarationStart()
    {
        var next = _lex.Peek();
        return next.Kind == JsTokenKind.Identifier
            || next.Kind == JsTokenKind.LBracket
            || next.Kind == JsTokenKind.LBrace;
    }

    // -----------------------------------------------------------------------
    // Trivial statements
    // -----------------------------------------------------------------------

    private BlockStatement ParseBlock()
    {
        var start = _current.Start;
        Expect(JsTokenKind.LBrace, "{ expected");
        // A block is a statement context, so the `for` header's [NoIn]
        // restriction does NOT propagate into it — this is what makes `in` legal
        // inside a function/arrow body that happens to sit in a for-initializer,
        // e.g. `for(!function(){ if("x" in a){} }(); …)`. Reset and restore.
        var savedNoIn = _disallowInDepth;
        _disallowInDepth = 0;
        try
        {
            var body = new List<Statement>();
            while (!Check(JsTokenKind.RBrace) && !Check(JsTokenKind.EndOfFile))
            {
                body.Add(ParseStatement());
            }

            var end = _current.End;
            Expect(JsTokenKind.RBrace, "expected '}' to close block");
            // §14.2.1 — Block early errors: no duplicate LexicallyDeclaredNames
            // and no lexical/var collision.
            CheckScopeEarlyErrors(body, ScopeKind.Block);
            return new BlockStatement(body, start, end);
        }
        finally
        {
            _disallowInDepth = savedNoIn;
        }
    }

    private EmptyStatement ParseEmpty()
    {
        var t = Advance();
        return new EmptyStatement(t.Start, t.End);
    }

    /// <summary>§14.11 / §14.11.1 — the <c>with</c> statement. A strict-mode
    /// <c>with</c> is the canonical strict early error (SyntaxError). The sloppy
    /// form parses to a <see cref="WithStatement"/>: the compiler lowers it to an
    /// object Environment Record pushed for the body so unqualified name lookups
    /// consult the object first (§9.1.1.2).</summary>
    private WithStatement ParseWith()
    {
        var start = _current.Start;
        if (_strict)
        {
            throw new JsParseException("'with' statements are not allowed in strict mode", start);
        }

        Advance(); // 'with'
        Expect(JsTokenKind.LParen, "( expected after 'with'");
        var obj = ParseExpressionNoEof();
        Expect(JsTokenKind.RParen, "expected ')' to close with-head");
        var body = ParseSubStatement();
        return new WithStatement(obj, body, start, body.End);
    }

    private ExpressionStatement ParseExpressionStatement()
    {
        var start = _current.Start;
        var expr = ParseExpressionNoEof();
        var end = _current.End;
        ConsumeSemicolonOrAsi();
        return new ExpressionStatement(expr, start, end);
    }

    private DebuggerStatement ParseDebugger()
    {
        var t = Advance();
        ConsumeSemicolonOrAsi();
        return new DebuggerStatement(t.Start, t.End);
    }

    // -----------------------------------------------------------------------
    // Control flow
    // -----------------------------------------------------------------------

    private IfStatement ParseIf()
    {
        var start = _current.Start;
        Advance(); // 'if'
        Expect(JsTokenKind.LParen, "( expected after 'if'");
        var test = ParseExpressionNoEof();
        Expect(JsTokenKind.RParen, "expected ')'");
        // Annex B.3.4 — a sloppy-mode FunctionDeclaration is permitted as the
        // body of an if (and its else).
        var cons = ParseSubStatement(allowSloppyFunction: true);
        Statement? alt = null;
        if (Match(JsTokenKind.Else))
        {
            alt = ParseSubStatement(allowSloppyFunction: true);
        }

        return new IfStatement(test, cons, alt, start, (alt ?? cons).End);
    }

    private WhileStatement ParseWhile()
    {
        var start = _current.Start;
        Advance();
        Expect(JsTokenKind.LParen, "( expected after 'while'");
        var test = ParseExpressionNoEof();
        Expect(JsTokenKind.RParen, "expected ')'");
        var body = ParseIterationBody();
        return new WhileStatement(test, body, start, body.End);
    }

    private DoWhileStatement ParseDoWhile()
    {
        var start = _current.Start;
        Advance(); // 'do'
        var body = ParseIterationBody();
        Expect(JsTokenKind.While, "expected 'while' after do-block");
        Expect(JsTokenKind.LParen, "( expected after 'while'");
        var test = ParseExpressionNoEof();
        Expect(JsTokenKind.RParen, "expected ')'");
        var end = _current.End;
        // ES2024 §12.10.1 rule 3 — the do-while-specific ASI rule: a semicolon
        // is ALWAYS inserted after the closing ')' of a do-while, even with no
        // line terminator and even when the next token is not '}' or EOF. So
        // consume an OPTIONAL ';' here and never error (handles minified code
        // like `do x();while(c)return 1`). Do NOT route through
        // ConsumeSemicolonOrAsi — its strict callers still need to reject this.
        Match(JsTokenKind.Semicolon);
        return new DoWhileStatement(body, test, start, end);
    }

    private Statement ParseFor()
    {
        var start = _current.Start;
        Advance();
        // wp:M3-04g — `for await (… of …)`. `await` is a contextual keyword
        // (an Identifier here); consume it and require an of-iteration form.
        bool isAwait = false;
        if (_current.Kind == JsTokenKind.Identifier && _current.TextEquals("await"))
        {
            isAwait = true;
            Advance();
        }
        Expect(JsTokenKind.LParen, "( expected after 'for'");

        // Initializer: VariableDeclaration | Expression | empty.
        AstNode? init = null;
        if (!Check(JsTokenKind.Semicolon))
        {
            if (_current.Kind == JsTokenKind.Var
                || _current.Kind == JsTokenKind.Const
                || (_current.Kind == JsTokenKind.Identifier && _current.TextEquals("let")
                    && IsLetDeclarationStart()))
            {
                var kind = _current.Kind == JsTokenKind.Var ? "var"
                         : _current.Kind == JsTokenKind.Const ? "const"
                         : "let";
                init = ParseVarHeadless(kind);
                // Detect for-in / for-of: after the single declarator, the
                // next token will be 'in' or contextual 'of'.
                if (init is VariableDeclaration vd && vd.Declarations.Count == 1
                    && vd.Declarations[0].Init is null)
                {
                    if (Check(JsTokenKind.In))
                    {
                        return FinishForIn(start, vd);
                    }

                    if (IsContextualOf())
                    {
                        return FinishForOf(start, vd, isAwait);
                    }
                }
            }
            else
            {
                if (Check(JsTokenKind.LBracket) || Check(JsTokenKind.LBrace))
                {
                    var cover = ParseLeftHandSide();
                    if (Check(JsTokenKind.In))
                    {
                        return FinishForIn(start, cover);
                    }

                    if (IsContextualOf())
                    {
                        return FinishForOf(start, cover, isAwait);
                    }

                    var exprInit = cover;
                    init = new ExpressionStatement(exprInit, exprInit.Start, exprInit.End);
                }
                else
                {
                    var expr = ParseExpressionNoIn();
                    if (Check(JsTokenKind.In))
                    {
                        return FinishForIn(start, expr);
                    }

                    if (IsContextualOf())
                    {
                        // §14.7.5 — a for-of LeftHandSide may not be the single
                        // token `async` (`for (async of …)` has a
                        // [lookahead ≠ async of] restriction to keep it distinct
                        // from `for await`). The bare `async` here is an error.
                        if (expr is Identifier { Name: "async" })
                        {
                            throw new JsParseException(
                                "'async' may not be the left-hand side of a for-of loop", expr.Start);
                        }

                        return FinishForOf(start, expr, isAwait);
                    }
                    init = new ExpressionStatement(expr, expr.Start, expr.End);
                }
            }
        }
        // wp:M3-04g — `for await` is only valid in the for-of form.
        if (isAwait)
        {
            throw new JsParseException("'for await' requires an 'of' iteration clause", _current.Start);
        }

        Expect(JsTokenKind.Semicolon, "expected ';' in for-loop init");

        Expression? test = null;
        if (!Check(JsTokenKind.Semicolon))
        {
            test = ParseExpressionNoEof();
        }

        Expect(JsTokenKind.Semicolon, "expected ';' in for-loop test");

        Expression? update = null;
        if (!Check(JsTokenKind.RParen))
        {
            update = ParseExpressionNoEof();
        }

        Expect(JsTokenKind.RParen, "expected ')' to close for-loop header");
        var body = ParseIterationBody();
        CheckForHeadLexicalVsBodyVar(init, body);
        return new ForStatement(init, test, update, body, start, body.End);
    }

    private bool IsContextualOf()
        => _current.Kind == JsTokenKind.Identifier && _current.TextEquals("of");

    private ForInStatement FinishForIn(JsPosition start, AstNode left)
    {
        if (left is Expression expr)
        {
            left = ReinterpretAssignmentTarget(expr);
            // §13.15.1 — a destructuring assignment target may not bind strict
            // `eval`/`arguments` (`for ({ eval } in …)` in strict mode).
            CheckAssignmentTarget((Expression)left, ((Expression)left).Start);
        }
        Advance(); // 'in'
        var right = ParseExpressionNoEof();
        Expect(JsTokenKind.RParen, "expected ')' after for-in head");
        var body = ParseIterationBody();
        CheckForHeadLexicalVsBodyVar(left, body);
        return new ForInStatement(left, right, body, start, body.End);
    }

    private ForOfStatement FinishForOf(JsPosition start, AstNode left, bool isAwait = false)
    {
        if (left is Expression expr)
        {
            left = ReinterpretAssignmentTarget(expr);
            // §13.15.1 — a destructuring assignment target may not bind strict
            // `eval`/`arguments` (`for ({ eval } of …)` in strict mode).
            CheckAssignmentTarget((Expression)left, ((Expression)left).Start);
        }
        // §14.7.5 — the contextual `of` keyword may not contain a Unicode escape
        // (`for (var x of [])` is a SyntaxError).
        if (_current.ContainsEscape)
        {
            throw new JsParseException("'of' keyword may not contain an escape sequence", _current.Start);
        }

        Advance(); // contextual 'of'
        // §14.7.5 — the for-of right-hand side is a single AssignmentExpression[+In]
        // (no comma sequence): `for (x of a, b)` is a SyntaxError. `in` is
        // re-enabled here regardless of any enclosing for-header [NoIn].
        var savedNoIn = _disallowInDepth;
        _disallowInDepth = 0;
        Expression right;
        try { right = ParseAssignment(); }
        finally { _disallowInDepth = savedNoIn; }
        Expect(JsTokenKind.RParen, "expected ')' after for-of head");
        var body = ParseIterationBody();
        CheckForHeadLexicalVsBodyVar(left, body);
        return new ForOfStatement(left, right, body, start, body.End, isAwait);
    }

    // -----------------------------------------------------------------------
    // Jump / control statements
    // -----------------------------------------------------------------------

    private ReturnStatement ParseReturn()
    {
        var start = _current.Start;
        // §13.10.1 — a ReturnStatement is only valid inside a function body. A
        // `return` at script/module top level (depth 0) is an early SyntaxError.
        if (_functionDepth == 0)
        {
            throw new JsParseException("'return' statement outside of a function", start);
        }

        Advance(); // 'return'
        Expression? arg = null;
        if (!_current.PrecededByLineTerminator
            && !Check(JsTokenKind.Semicolon)
            && !Check(JsTokenKind.RBrace)
            && !Check(JsTokenKind.EndOfFile))
        {
            arg = ParseExpressionNoEof();
        }
        var end = _current.End;
        ConsumeSemicolonOrAsi();
        return new ReturnStatement(arg, start, end);
    }

    private BreakStatement ParseBreak()
    {
        var start = _current.Start;
        Advance();
        string? label = null;
        if (!_current.PrecededByLineTerminator && Check(JsTokenKind.Identifier))
        {
            label = _current.Lexeme;
            Advance();
        }
        var end = _current.End;
        ConsumeSemicolonOrAsi();
        return new BreakStatement(label, start, end);
    }

    private ContinueStatement ParseContinue()
    {
        var start = _current.Start;
        Advance();
        string? label = null;
        if (!_current.PrecededByLineTerminator && Check(JsTokenKind.Identifier))
        {
            label = _current.Lexeme;
            Advance();
        }
        var end = _current.End;
        ConsumeSemicolonOrAsi();
        return new ContinueStatement(label, start, end);
    }

    private ThrowStatement ParseThrow()
    {
        var start = _current.Start;
        Advance();
        if (_current.PrecededByLineTerminator)
        {
            throw new JsParseException("illegal newline after 'throw'", start);
        }

        var arg = ParseExpressionNoEof();
        var end = _current.End;
        ConsumeSemicolonOrAsi();
        return new ThrowStatement(arg, start, end);
    }

    /// <summary>§14.15.1 — CatchParameter early errors: (a) its BoundNames must
    /// not contain duplicates (a CatchParameter BindingPattern is
    /// UniqueFormalParameters-like; `catch ([x, x])` is an error); (b) none of
    /// its BoundNames may also appear in the catch Block's LexicallyDeclaredNames
    /// (`catch (x) { let x; }`, `catch (e) { function e(){} }`).</summary>
    private void CheckCatchBindings(Expression param, BlockStatement body)
    {
        var names = new List<(string Name, JsPosition Pos)>();
        CollectPatternNames(param, names);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, pos) in names)
        {
            if (!seen.Add(name))
            {
                throw new JsParseException(
                    $"duplicate catch parameter name '{name}'", pos);
            }
        }

        // The catch Block's top-level LexicallyDeclaredNames: let/const/class and
        // any directly-nested FunctionDeclaration (which is lexical in a Block).
        var lexical = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stmt in body.Body)
        {
            switch (stmt)
            {
                case VariableDeclaration vd when vd.Kind is "let" or "const":
                    foreach (var n in BoundNamesOf(vd))
                    {
                        lexical.Add(n.Name);
                    }

                    break;
                case ClassDeclaration cd:
                    lexical.Add(cd.Name.Name);
                    break;
                case FunctionDeclaration fd:
                    lexical.Add(fd.Name.Name);
                    break;
                case LabeledStatement lab when Unlabel(lab) is FunctionDeclaration lfd:
                    lexical.Add(lfd.Name.Name);
                    break;
            }
        }
        foreach (var (name, pos) in names)
        {
            if (lexical.Contains(name))
            {
                throw new JsParseException(
                    $"catch parameter '{name}' is redeclared in the catch body", pos);
            }
        }
    }

    // -----------------------------------------------------------------------
    // try / catch / finally
    // -----------------------------------------------------------------------

    private TryStatement ParseTry()
    {
        var start = _current.Start;
        Advance();
        var block = ParseBlock();
        CatchClause? handler = null;
        if (Match(JsTokenKind.Catch))
        {
            var cstart = _current.Start;
            Expression? param = null;
            if (Match(JsTokenKind.LParen))
            {
                param = ParseBindingTarget();
                // §14.15.1 — a catch binding may not be `eval`/`arguments` in strict.
                CheckPatternBindingNames(param);
                Expect(JsTokenKind.RParen, "expected ')' after catch parameter");
            }
            var body = ParseBlock();
            if (param is not null)
            {
                CheckCatchBindings(param, body);
            }

            handler = new CatchClause(param, body, cstart, body.End);
        }
        BlockStatement? finalizer = null;
        if (Match(JsTokenKind.Finally))
        {
            finalizer = ParseBlock();
        }

        if (handler is null && finalizer is null)
        {
            throw new JsParseException("'try' requires 'catch' or 'finally'", start);
        }

        var end = (Statement?)finalizer ?? handler?.Body ?? block;
        return new TryStatement(block, handler, finalizer, start, end.End);
    }

    // -----------------------------------------------------------------------
    // labeled statement (§14.13)
    // -----------------------------------------------------------------------

    /// <summary>§14.13 LabeledStatement — <c>identifier : Statement</c>.
    /// The caller must have verified that the current token is an Identifier
    /// and the next token is a Colon before calling this method.</summary>
    private LabeledStatement ParseLabeledStatement()
    {
        var start = _current.Start;
        var label = _current.Lexeme;
        // §13.3.10.1 / §14.4 — `await` may not be a LabelIdentifier in an async
        // context, and `yield` may not be one in a generator (it arrives here as
        // an Identifier only outside a generator; the in-generator `yield:` form
        // is rejected earlier as a yield expression).
        if (_inAsync && label == "await")
        {
            throw new JsParseException(
                "'await' may not be used as a label in an async context", start);
        }

        Advance(); // identifier
        Advance(); // ':'
        // §14.13.1 — a LabelledStatement body is a LabelledItem, which forbids
        // lexical/class declarations; Annex B.3.2 permits a sloppy plain
        // function declaration — EXCEPT:
        //  - when this label chain is the body of an iteration statement
        //    (`for (;;) lbl: function f(){}`) — the extension does not apply, or
        //  - when the function is under MORE THAN ONE label
        //    (`a: b: function f(){}`) — only a single directly-applied label
        //    qualifies for the Annex B.3.2 LabelledFunctionDeclaration form.
        // So if this label's own body is itself a LabelledStatement, the inner
        // (nested) label may not introduce a sloppy function — set the forbid
        // flag for the nested parse. The flag is cleared by ParseSubStatement
        // once a non-label statement (a block, etc.) is entered.
        var bodyIsLabel = _current.Kind == JsTokenKind.Identifier
            && _lex.Peek().Kind == JsTokenKind.Colon;
        var allowFn = !_forbidLabelledFunction;
        var savedForbid = _forbidLabelledFunction;
        if (bodyIsLabel)
        {
            _forbidLabelledFunction = true;
        }

        Statement body;
        try { body = ParseSubStatement(allowSloppyFunction: allowFn); }
        finally { _forbidLabelledFunction = savedForbid; }
        return new LabeledStatement(label, body, start, body.End);
    }

    // -----------------------------------------------------------------------
    // switch
    // -----------------------------------------------------------------------

    private SwitchStatement ParseSwitch()
    {
        var start = _current.Start;
        Advance();
        Expect(JsTokenKind.LParen, "( expected after 'switch'");
        var disc = ParseExpressionNoEof();
        Expect(JsTokenKind.RParen, "expected ')'");
        Expect(JsTokenKind.LBrace, "expected '{' to open switch body");
        var cases = new List<SwitchCase>();
        while (!Check(JsTokenKind.RBrace) && !Check(JsTokenKind.EndOfFile))
        {
            var cstart = _current.Start;
            Expression? test;
            if (Match(JsTokenKind.Case))
            {
                test = ParseExpressionNoEof();
            }
            else
            {
                Expect(JsTokenKind.Default, "expected 'case' or 'default'");
                test = null;
            }
            Expect(JsTokenKind.Colon, "expected ':' after case label");
            var body = new List<Statement>();
            while (!Check(JsTokenKind.Case) && !Check(JsTokenKind.Default)
                && !Check(JsTokenKind.RBrace))
            {
                body.Add(ParseStatement());
            }
            var cend = body.Count > 0 ? body[^1].End : _current.End;
            cases.Add(new SwitchCase(test, body, cstart, cend));
        }
        var end = _current.End;
        Expect(JsTokenKind.RBrace, "expected '}' to close switch");
        // §14.12.1 — the whole CaseBlock is one lexical scope: concatenate
        // every clause's StatementList and apply the Block early errors.
        var caseBody = new List<Statement>();
        foreach (var c in cases)
        {
            caseBody.AddRange(c.Consequent);
        }

        CheckScopeEarlyErrors(caseBody, ScopeKind.Block);
        return new SwitchStatement(disc, cases, start, end);
    }

    // -----------------------------------------------------------------------
    // Declarations
    // -----------------------------------------------------------------------

    private VariableDeclaration ParseVar(string kind)
    {
        var start = _current.Start;
        Advance(); // var / let / const
        var decl = ParseVarBody(kind, start);
        // §14.3.1 — a `const` declaration outside a for-in/for-of head must
        // initialize every binding. (The for-head form is parsed via
        // ParseVarHeadless and never reaches here.)
        if (kind == "const")
        {
            foreach (var d in decl.Declarations)
            {
                if (d.Init is null)
                {
                    throw new JsParseException(
                        "missing initializer in const declaration", d.Start);
                }
            }
        }
        ConsumeSemicolonOrAsi();
        return decl;
    }

    /// <summary>Same as <see cref="ParseVar"/> but assumes the keyword has
    /// already been consumed (used by <c>for(var x=…)</c>).</summary>
    private VariableDeclaration ParseVarHeadless(string kind)
    {
        var start = _current.Start;
        Advance(); // var / let / const
        return ParseVarBody(kind, start);
    }

    private VariableDeclaration ParseVarBody(string kind, JsPosition start)
    {
        var decls = new List<VariableDeclarator>();
        while (true)
        {
            var dstart = _current.Start;
            var idNode = ParseBindingTarget();
            // §13.3.1.1 — `eval`/`arguments`/strict-reserved binding names error
            // in strict code (covers var/let/const, simple or destructured).
            CheckPatternBindingNames(idNode);
            // §14.3.1.1 — a LexicalDeclaration (let/const) may not bind the
            // name `let` (in any mode). `var let` is legal in sloppy code.
            if (kind is "let" or "const")
            {
                CheckLexicalBindingNotLet(idNode);
            }

            Expression? init = null;
            if (Match(JsTokenKind.Eq))
            {
                init = ParseAssignment();
            }

            decls.Add(new VariableDeclarator(idNode, init, dstart,
                (init ?? idNode).End));
            if (!Match(JsTokenKind.Comma))
            {
                break;
            }
        }
        var end = decls[^1].End;
        var result = new VariableDeclaration(kind, decls, start, end);
        // §14.3.1.1 — a single LexicalDeclaration's BoundNames must have no
        // duplicate entries (`let [x, x] = …`, `const a, a;`). `var` permits
        // repeats. (Var/let/const collisions across statements are handled by
        // the scope-level early-error pass.)
        if (kind is "let" or "const")
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in BoundNamesOf(result))
            {
                if (!seen.Add(n.Name))
                {
                    throw new JsParseException(
                        $"'{n.Name}' has already been declared", n.Pos);
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Function as an expression: <c>function [name](params) { body }</c>.
    /// The optional name is stored on the AST. The compiler binds it inside
    /// the function body for recursion and self-reference.
    /// </summary>
    private FunctionExpression ParseFunctionExpression()
        => ParseFunctionExpressionInner(_current.Start, isAsync: false);

    internal FunctionExpression ParseFunctionExpression(JsPosition start, bool isAsync)
        => ParseFunctionExpressionInner(isAsync ? start : _current.Start, isAsync);

    private FunctionExpression ParseFunctionExpressionInner(JsPosition start, bool isAsync)
    {
        Advance(); // 'function'
        var generator = Match(JsTokenKind.Star);
        var savedStrict = _strict;
        var (savedAsync, savedGen) = (_inAsync, _inGenerator);
        var savedModuleAwait = _moduleTopAwait;
        // §13.3.7.1 — an ordinary function has no [[HomeObject]], so a
        // SuperProperty is NOT legal in its body even when nested inside a
        // method. Reset the home-object scope (arrows inherit it — see ParseArrowBody).
        var savedSuper = _superPropertyDepth;
        _superPropertyDepth = 0;
        try
        {
            Identifier? fnName = null;
            if (Check(JsTokenKind.Identifier))
            {
                var tok = Advance();
                // §15.8.1 — an async FunctionExpression's BindingIdentifier is
                // [+Await], so it may not be `await` (`async function await(){}`,
                // `async function* await(){}`).
                if (isAsync && tok.TextEquals("await"))
                {
                    throw new JsParseException(
                        "'await' may not be used as the name of an async function", tok.Start);
                }

                fnName = new Identifier(tok.Lexeme, tok.Start, tok.End);
            }
            // §15.2.1 / §12.7.1 — a non-generator FunctionExpression's
            // BindingIdentifier uses the [~Yield] context regardless of the
            // enclosing generator scope, so `yield` is a valid name for it in
            // sloppy non-generator code.  A generator expression may NOT use
            // `yield` as its name (its BindingIdentifier is [+Yield]).  If the
            // body's "use strict" directive flips the function to strict mode,
            // CheckBindingIdentifier below will reject `yield` as a
            // FutureReservedWord.
            else if (!generator && Check(JsTokenKind.Yield))
            {
                var tok = Advance();
                fnName = new Identifier(tok.Lexeme, tok.Start, tok.End);
            }
            // §15 — a function establishes a fresh await/yield context for its
            // own parameters and body (an async/generator turns the keyword on;
            // an ordinary function turns it off, shadowing any enclosing one).
            // A function body is never the Module's [+Await] top level.
            _inAsync = isAsync;
            _inGenerator = generator;
            _moduleTopAwait = false;
            Expect(JsTokenKind.LParen, "( expected after function expression");
            var parameters = ParseParameterList();
            Expect(JsTokenKind.RParen, "expected ')'");
            var (body, strict) = ParseFunctionBody();
            // §15.2.1 — the function-name and parameter early errors use the
            // function's OWN strictness (the body may have flipped to strict).
            CheckUseStrictSimpleParams(parameters, start);
            if (strict && fnName is not null)
            {
                CheckBindingIdentifier(fnName.Name, fnName.Start);
            }

            ValidateParameters(parameters, strict);
            CheckParamsVsLexicalBody(parameters, body);
            return new FunctionExpression(fnName, parameters, body, generator, start, body.End,
                Async: isAsync, Strict: strict, SourceText: SourceSlice(start, body.End));
        }
        finally { _strict = savedStrict; (_inAsync, _inGenerator) = (savedAsync, savedGen); _moduleTopAwait = savedModuleAwait; _superPropertyDepth = savedSuper; }
    }

    private FunctionDeclaration ParseFunctionDeclaration()
        => ParseFunctionDeclarationInner(_current.Start, isAsync: false);

    internal FunctionDeclaration ParseFunctionDeclaration(JsPosition start, bool isAsync)
        => ParseFunctionDeclarationInner(isAsync ? start : _current.Start, isAsync);

    private FunctionDeclaration ParseFunctionDeclarationInner(JsPosition start, bool isAsync)
    {
        Advance(); // function
        var generator = Match(JsTokenKind.Star);
        var nameTok = Expect(JsTokenKind.Identifier, "function name expected");
        var name = new Identifier(nameTok.Lexeme, nameTok.Start, nameTok.End);
        var savedStrict = _strict;
        var (savedAsync, savedGen) = (_inAsync, _inGenerator);
        var savedModuleAwait = _moduleTopAwait;
        // §13.3.7.1 — an ordinary function has no [[HomeObject]]; reset the
        // home-object scope so a SuperProperty in its body is an early error.
        var savedSuper = _superPropertyDepth;
        _superPropertyDepth = 0;
        try
        {
            // §15 — fresh await/yield context for this function's params + body.
            // A function body is never the Module's [+Await] top level.
            _inAsync = isAsync;
            _inGenerator = generator;
            _moduleTopAwait = false;
            Expect(JsTokenKind.LParen, "( expected after function name");
            var parameters = ParseParameterList();
            Expect(JsTokenKind.RParen, "expected ')'");
            var (body, strict) = ParseFunctionBody();
            // §15.2.1 — name + parameter early errors use the function's own
            // strictness (a "use strict" body directive applies to both).
            CheckUseStrictSimpleParams(parameters, start);
            if (strict)
            {
                CheckBindingIdentifier(name.Name, name.Start);
            }

            ValidateParameters(parameters, strict);
            CheckParamsVsLexicalBody(parameters, body);
            return new FunctionDeclaration(name, parameters, body, generator, start, body.End,
                Async: isAsync, Strict: strict, SourceText: SourceSlice(start, body.End));
        }
        finally { _strict = savedStrict; (_inAsync, _inGenerator) = (savedAsync, savedGen); _moduleTopAwait = savedModuleAwait; _superPropertyDepth = savedSuper; }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private List<Expression> ParseParameterList()
    {
        var parameters = new List<Expression>();
        // §15.1.1 / §15.5.1 / §15.8.1 — mark that we are in a FormalParameters
        // list so a yield/await expression inside a default value is rejected.
        var savedInParams = _inFormalParameters;
        _inFormalParameters = true;
        try
        {
            while (!Check(JsTokenKind.RParen))
            {
                if (Check(JsTokenKind.Ellipsis))
                {
                    var sstart = _current.Start;
                    Advance();
                    var inner = ParseBindingTarget();
                    parameters.Add(new SpreadElement(inner, sstart, inner.End));
                    break;
                }
                parameters.Add(ParseParameter());
                if (!Match(JsTokenKind.Comma))
                {
                    break;
                }
            }
        }
        finally { _inFormalParameters = savedInParams; }
        return parameters;
    }

    /// <summary>
    /// Like <see cref="ParseExpression"/> but doesn't require EOF afterwards.
    /// Used inside statement contexts where a terminator is expected.
    /// </summary>
    private Expression ParseExpressionNoEof()
    {
        var expr = ParseAssignment();
        if (Match(JsTokenKind.Comma))
        {
            var parts = new List<Expression> { expr };
            do { parts.Add(ParseAssignment()); }
            while (Match(JsTokenKind.Comma));
            expr = new SequenceExpression(parts, parts[0].Start, parts[^1].End);
        }
        return expr;
    }

    private Expression ParseExpressionNoIn()
    {
        _disallowInDepth++;
        try
        {
            return ParseExpressionNoEof();
        }
        finally
        {
            _disallowInDepth--;
        }
    }

    /// <summary>
    /// Spec §11.9.1 ASI: a semicolon may be omitted before <c>}</c>, before
    /// EOF, or before a line terminator. Anything else after the statement
    /// is a syntax error.
    /// </summary>
    private void ConsumeSemicolonOrAsi()
    {
        if (Match(JsTokenKind.Semicolon))
        {
            return;
        }

        if (Check(JsTokenKind.EndOfFile) || Check(JsTokenKind.RBrace))
        {
            return;
        }

        if (_current.PrecededByLineTerminator)
        {
            return;
        }

        throw new JsParseException(
            $"expected ';' after statement (got {_current.Kind} '{_current.Lexeme}')",
            _current.Start);
    }
}
