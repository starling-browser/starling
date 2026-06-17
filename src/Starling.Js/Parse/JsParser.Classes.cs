using Starling.Js.Ast;
using Starling.Js.Lex;

namespace Starling.Js.Parse;

/// <summary>
/// Class declaration + expression parsing — B1b-2a. Class bodies are
/// implicitly strict-mode per ES2024 §15.7; the engine does not yet enforce
/// strict-only restrictions (e.g. <c>arguments.caller</c>), tracked as a
/// known divergence in <c>tasks/M3/google-com-handoff.md</c>.
/// </summary>
public ref partial struct JsParser
{
    /// <summary>Set while parsing inside a class body so private-name
    /// references can be validated as declared. Each frame represents one
    /// (possibly nested) class scope.</summary>

    /// <summary>§12.7.2 — the canonical (escape-decoded) name of a
    /// PrivateIdentifier token. The lexer stores the decoded <c>#name</c> in the
    /// token's <see cref="JsToken.Value"/>; the raw source slice (which may
    /// contain <c>\u</c> escapes) lives in <see cref="JsToken.Lexeme"/>. Private
    /// names are compared by their decoded text, so <c>#\u{6F}</c> and <c>#o</c>
    /// denote the same name.</summary>
    private static string PrivateNameOf(JsToken t) => t.Value as string ?? t.Lexeme;

    /// <summary>Tracks whether the parser is currently inside a derived
    /// class's constructor body so <c>super(...)</c> calls can be
    /// distinguished from <c>super.x()</c> member calls.</summary>

    /// <summary>Parse a class declaration statement. The keyword
    /// <c>class</c> is the current token.</summary>
    private ClassDeclaration ParseClassDeclarationWithExtendsTracking()
    {
        var start = _current.Start;
        Advance(); // 'class'
        var nameTok = Expect(JsTokenKind.Identifier, "expected class name");
        var name = new Identifier(nameTok.Lexeme, nameTok.Start, nameTok.End);
        // §15.7.1 — a class definition is strict code, so its name binding may
        // not be `eval`/`arguments` or a strict reserved word, regardless of
        // the surrounding scope's strictness.
        CheckClassBindingName(name);
        var (baseClass, body, end) = ParseClassTail(start);
        return new ClassDeclaration(name, baseClass, body, start, end);
    }

    /// <summary>Parse a class expression. The keyword <c>class</c> is the
    /// current token. The optional name binding is visible only inside the
    /// body per §15.7.5 (ClassExpression Static Semantics: BoundNames).</summary>
    private ClassExpression ParseClassExpression()
    {
        var start = _current.Start;
        Advance(); // 'class'
        Identifier? name = null;
        if (_current.Kind == JsTokenKind.Identifier)
        {
            var t = Advance();
            name = new Identifier(t.Lexeme, t.Start, t.End);
            CheckClassBindingName(name);
        }
        var (baseClass, body, end) = ParseClassTail(start);
        return new ClassExpression(name, baseClass, body, start, end);
    }

    private (Expression? BaseClass, ClassBody Body, JsPosition End) ParseClassTail(JsPosition start)
    {
        Expression? baseClass = null;
        if (Match(JsTokenKind.Extends))
        {
            baseClass = ParseLeftHandSide();
        }
        if (baseClass is not null)
        {
            _baseClassContextDepth++;
        }

        try
        {
            var body = ParseClassBody();
            return (baseClass, body, body.End);
        }
        finally
        {
            if (baseClass is not null)
            {
                _baseClassContextDepth--;
            }
        }
    }

    private ClassBody ParseClassBody()
    {
        var start = _current.Start;
        Expect(JsTokenKind.LBrace, "expected '{' to open class body");

        // §15.7 — all code in a class body (methods, constructor, field
        // initializers, static blocks) is strict mode code, regardless of the
        // surrounding scope. Save/restore around the whole body.
        var savedStrict = _strict;
        _strict = true;

        // Pre-scan declared private names so any forward reference inside
        // an earlier method body resolves correctly.
        var privateScope = new HashSet<string>(StringComparer.Ordinal);
        _classPrivateScopes.Push(privateScope);

        // §15.7.1 — PrivateBoundNames duplicate check. Track each declared
        // private name's prior placements so a duplicate is rejected unless it
        // is exactly one getter + one setter of matching static-ness.
        var privateDecls = new Dictionary<string, PrivateDecl>(StringComparer.Ordinal);
        _privateDeclStack.Push(privateDecls);

        MethodDefinition? ctor = null;
        var methods = new List<MethodDefinition>();
        var fields = new List<PropertyField>();
        var staticBlocks = new List<BlockStatement>();
        try
        {
            // First-pass private-name pre-scan via a lookahead token by checkpointing.
            // Simpler: walk + collect lazily as we parse, but allow any usage to
            // succeed; we validate at compile time. Doing one-pass parse here.
            while (!Check(JsTokenKind.RBrace) && !Check(JsTokenKind.EndOfFile))
            {
                if (Match(JsTokenKind.Semicolon))
                {
                    continue; // empty class element
                }

                ParseClassMember(ctor, methods, fields, staticBlocks, privateScope, out var newCtor);
                if (newCtor is not null)
                {
                    ctor = newCtor;
                }
            }
            var end = _current.End;
            Expect(JsTokenKind.RBrace, "expected '}' to close class body");
            return new ClassBody(ctor, methods, fields, staticBlocks, start, end);
        }
        finally
        {
            _privateDeclStack.Pop();
            _classPrivateScopes.Pop();
            _strict = savedStrict;
        }
    }

    /// <summary>§15.7.1 — accumulated placements of one private name in a class
    /// body, used to enforce the PrivateBoundNames duplicate rule.</summary>
    private struct PrivateDecl
    {
        public bool HasGet, HasSet, HasOther;
        public bool GetStatic, SetStatic;
    }


    /// <summary>§15.7.1 ClassElementName early errors. Rejects a
    /// <c>static</c> element named <c>"prototype"</c> (string or identifier key),
    /// and the private name <c>#constructor</c> in any placement. A non-static
    /// method named <c>prototype</c> is permitted, so it is not rejected here;
    /// computed keys are checked at runtime, not statically.</summary>
    private static void CheckClassElementName(Expression key, bool computed, bool isStatic, bool isField, MethodKind kind, JsPosition pos)
    {
        _ = kind;
        if (key is PrivateNameExpression { Name: "#constructor" })
        {
            throw new JsParseException(
                "'#constructor' is a reserved class private name", pos);
        }
        // A computed key is checked at runtime, not statically.
        if (computed)
        {
            return;
        }

        var name = key switch
        {
            Identifier id => id.Name,
            StringLiteral sl => sl.Value,
            _ => null,
        };
        // §15.7.1 — a class FieldDefinition may not be named "constructor"
        // (whether static or not, identifier or string key). A static field may
        // additionally not be named "prototype".
        if (isField && name == "constructor")
        {
            throw new JsParseException(
                "a class field may not be named 'constructor'", pos);
        }

        if (isStatic && name == "prototype")
        {
            throw new JsParseException(
                "a static class element may not be named 'prototype'", pos);
        }
    }

    /// <summary>§15.7.1 — record a private element declaration and reject an
    /// illegal duplicate. The only legal repeat is a getter + setter sharing the
    /// same name AND the same static placement; every other repeat (method+
    /// method, field+anything, get+get, get+method, static-mismatched get/set,
    /// …) is a SyntaxError.</summary>
    private void RecordPrivateDeclaration(string name, MethodKind kind, bool isField, bool isStatic, JsPosition pos)
    {
        var decls = _privateDeclStack.Peek();
        decls.TryGetValue(name, out var d);

        bool dup;
        if (isField || kind is MethodKind.Method)
        {
            // A field or ordinary/generator/async method must be the SOLE
            // declaration of this name.
            dup = d.HasGet || d.HasSet || d.HasOther;
            d.HasOther = true;
        }
        else if (kind == MethodKind.Get)
        {
            dup = d.HasGet || d.HasOther || (d.HasSet && d.SetStatic != isStatic);
            d.HasGet = true; d.GetStatic = isStatic;
        }
        else // Set
        {
            dup = d.HasSet || d.HasOther || (d.HasGet && d.GetStatic != isStatic);
            d.HasSet = true; d.SetStatic = isStatic;
        }

        if (dup)
        {
            throw new JsParseException(
                $"duplicate private name '{name}' in class body", pos);
        }

        decls[name] = d;
    }

    private void ParseClassMember(
        MethodDefinition? existingCtor,
        List<MethodDefinition> methods,
        List<PropertyField> fields,
        List<BlockStatement> staticBlocks,
        HashSet<string> privateScope,
        out MethodDefinition? newCtor)
    {
        newCtor = null;
        var memberStart = _current.Start;
        // Detect `static` prefix — contextual identifier. `static() {}` is a
        // method named "static"; `static {` is a static block; `static name`
        // is a static member.
        bool isStatic = false;
        if (_current.Kind == JsTokenKind.Identifier && !_current.ContainsEscape && _current.TextEquals("static"))
        {
            var peek = _lex.Peek();
            // `static` followed by `(` or `=` or `;` is a regular member named "static".
            if (peek.Kind != JsTokenKind.LParen
                && peek.Kind != JsTokenKind.Eq
                && peek.Kind != JsTokenKind.Semicolon
                && peek.Kind != JsTokenKind.RBrace
                && peek.Kind != JsTokenKind.Comma)
            {
                Advance(); // consume 'static'
                isStatic = true;
                // Static initialization block: `static { ... }`
                if (Check(JsTokenKind.LBrace))
                {
                    // §15.7.1 — a ClassStaticBlockBody is evaluated with Await
                    // enabled, so `await` may not be used as a BindingIdentifier
                    // or IdentifierReference inside it; it also defines its own
                    // (non-generator) scope, and a `return` inside it is a
                    // SyntaxError (it is not a function body). Save/restore the
                    // surrounding context; reset _functionDepth so `return` is
                    // rejected even inside an enclosing function.
                    var (sbAsync, sbGen) = (_inAsync, _inGenerator);
                    var sbDepth = _functionDepth;
                    var sbStatic = _inStaticBlock;
                    _inAsync = true;
                    _inGenerator = false;
                    _functionDepth = 0;
                    _inStaticBlock = true;
                    try
                    {
                        var block = ParseBlock();
                        staticBlocks.Add(block);
                    }
                    finally
                    {
                        (_inAsync, _inGenerator) = (sbAsync, sbGen);
                        _functionDepth = sbDepth;
                        _inStaticBlock = sbStatic;
                    }
                    return;
                }
            }
        }

        // Detect async / generator method modifiers (ES2024 §15.4):
        //   *gen(){}, async m(){}, async *gen(){}, static async *gen(){}
        // `async` is contextual: a method modifier only when followed (same
        // line) by a method-name start; otherwise it is a member named "async".
        bool isGenerator = false, isAsync = false;
        if (_current.Kind == JsTokenKind.Identifier && !_current.ContainsEscape && _current.TextEquals("async"))
        {
            var peek = _lex.Peek();
            if (!peek.PrecededByLineTerminator && IsMethodNameStartAfterModifier(peek.Kind))
            {
                Advance(); // 'async'
                isAsync = true;
            }
        }
        if (Check(JsTokenKind.Star)) { Advance(); isGenerator = true; }

        // Detect get/set accessor — contextual. Accessors never combine with
        // async/* (those are distinct MethodDefinition productions).
        MethodKind methodKind = MethodKind.Method;
        if (!isAsync && !isGenerator
            && _current.Kind == JsTokenKind.Identifier && !_current.ContainsEscape
            && (_current.TextEquals("get") || _current.TextEquals("set")))
        {
            var peek = _lex.Peek();
            // `get name(...)` is an accessor; `get(...)` is a method named "get".
            if (peek.Kind != JsTokenKind.LParen
                && peek.Kind != JsTokenKind.Eq
                && peek.Kind != JsTokenKind.Semicolon
                && peek.Kind != JsTokenKind.RBrace
                && peek.Kind != JsTokenKind.Comma)
            {
                methodKind = _current.TextEquals("get") ? MethodKind.Get : MethodKind.Set;
                Advance();
            }
        }

        // Parse the key.
        var (key, computed, isPrivate) = ParseClassElementKey(privateScope);

        // Field declaration: `name [= init];` or `name;` (no parens follow).
        // A field is never async/generator (those require a method body).
        if (methodKind == MethodKind.Method && !isAsync && !isGenerator
            && !Check(JsTokenKind.LParen))
        {
            // §15.7.1 early errors for a field name.
            CheckClassElementName(key, computed, isStatic, isField: true, MethodKind.Method, memberStart);
            if (isPrivate && key is PrivateNameExpression pf)
            {
                RecordPrivateDeclaration(pf.Name, MethodKind.Method, isField: true, isStatic, memberStart);
            }

            Expression? init = null;
            if (Match(JsTokenKind.Eq))
            {
                // §15.7 — a field Initializer is [~Await][~Yield]: `await` there
                // is not the keyword. Also §13.3.7.1 — a field initializer has a
                // [[HomeObject]] (the class prototype / constructor), so a
                // SuperProperty inside it (incl. inside a direct eval) is legal.
                var savedFieldModuleAwait = _moduleTopAwait;
                var savedFieldSuper = _superPropertyDepth;
                _moduleTopAwait = false;
                _superPropertyDepth = savedFieldSuper + 1;
                try { init = ParseAssignment(); }
                finally { _moduleTopAwait = savedFieldModuleAwait; _superPropertyDepth = savedFieldSuper; }
            }
            var fieldEnd = _current.End;
            ConsumeSemicolonOrAsi();
            fields.Add(new PropertyField(key, isStatic, computed, init, memberStart, fieldEnd));
            return;
        }

        // Method form. Constructor disambiguation by name.
        bool isCtor = !isStatic
            && methodKind == MethodKind.Method
            && !isAsync && !isGenerator
            && !computed
            && !isPrivate
            && key is Identifier { Name: "constructor" };
        if (isCtor)
        {
            methodKind = MethodKind.Constructor;
        }
        // A generator/async method named "constructor" (non-static) is a
        // SyntaxError (§15.7.1 Early Errors).
        if (!isStatic && (isAsync || isGenerator) && !computed && !isPrivate
            && key is Identifier { Name: "constructor" })
        {
            throw new JsParseException(
                "class constructor may not be a generator or async method", memberStart);
        }
        // §15.7.1 — a non-static accessor named "constructor" is a SyntaxError
        // (`get constructor`/`set constructor`). A non-computed, non-private
        // method named "constructor" is the constructor itself (isCtor above).
        if (!isStatic && methodKind is MethodKind.Get or MethodKind.Set
            && !computed && !isPrivate && key is Identifier { Name: "constructor" })
        {
            throw new JsParseException(
                "class constructor may not be an accessor", memberStart);
        }

        // §15.7.1 early errors for the (non-constructor) method element name:
        // `static prototype`, `#constructor`, etc.
        if (!isCtor)
        {
            CheckClassElementName(key, computed, isStatic, isField: false, methodKind, memberStart);
            if (isPrivate && key is PrivateNameExpression pm)
            {
                RecordPrivateDeclaration(pm.Name, methodKind, isField: false, isStatic, memberStart);
            }
        }

        // Track derived-constructor scope so `super(...)` is allowed only
        // when this is a derived class's constructor.
        var enteredDerivedCtor = false;
        if (isCtor && _baseClassContextDepth > 0)
        {
            _derivedConstructorDepth++;
            enteredDerivedCtor = true;
        }

        IReadOnlyList<Expression> parameters;
        BlockStatement body;
        JsPosition endPos;
        var (savedAsync, savedGen) = (_inAsync, _inGenerator);
        var savedModuleAwait = _moduleTopAwait;
        // §13.3.7.1 — a class method / accessor / constructor has a
        // [[HomeObject]], so a SuperProperty in its body is legal.
        var savedSuper = _superPropertyDepth;
        try
        {
            // §15 — a method establishes its own await/yield context based on its
            // async/generator modifiers, regardless of any enclosing context. A
            // method body is never the Module's [+Await] top level.
            _inAsync = isAsync;
            _inGenerator = isGenerator;
            _moduleTopAwait = false;
            _superPropertyDepth = savedSuper + 1;
            Expect(JsTokenKind.LParen, "expected '(' in method definition");
            parameters = ParseParameterList();
            Expect(JsTokenKind.RParen, "expected ')' after method parameters");
            // §15.4.1 — accessor arity: a getter takes no parameters; a setter
            // takes exactly one non-rest parameter (no default, no rest, no
            // destructuring rest).
            CheckAccessorParams(methodKind, parameters, memberStart);
            // Class bodies are always strict (§15.7), so _strict is already
            // true here; ParseFunctionBody preserves it (no prologue can clear
            // it). Validate params under strict semantics.
            (body, _) = ParseFunctionBody();
            CheckUseStrictSimpleParams(parameters, memberStart);
            ValidateParameters(parameters, strict: true);
            CheckParamsVsLexicalBody(parameters, body);
            endPos = body.End;
        }
        finally
        {
            if (enteredDerivedCtor)
            {
                _derivedConstructorDepth--;
            }

            (_inAsync, _inGenerator) = (savedAsync, savedGen);
            _moduleTopAwait = savedModuleAwait;
            _superPropertyDepth = savedSuper;
        }

        var method = new MethodDefinition(key, methodKind, isStatic, computed,
            parameters, body, memberStart, endPos,
            Generator: isGenerator, Async: isAsync, Strict: true,
            SourceText: SourceSlice(memberStart, endPos));
        if (isCtor)
        {
            if (existingCtor is not null)
            {
                throw new JsParseException("a class may only have one constructor", memberStart);
            }

            newCtor = method;
        }
        else
        {
            methods.Add(method);
        }
    }

    /// <summary>§15.4.1 — validate accessor arity. A getter
    /// (<c>MethodKind.Get</c>) must declare no parameters; a setter
    /// (<c>MethodKind.Set</c>) must declare exactly one parameter that is a
    /// single non-rest BindingElement (<c>PropertySetParameterList</c> is one
    /// <c>FormalParameter</c>, so no rest element).</summary>
    private void CheckAccessorParams(MethodKind kind, IReadOnlyList<Expression> @params, JsPosition pos)
    {
        if (kind == MethodKind.Get)
        {
            if (@params.Count != 0)
            {
                throw new JsParseException("a getter must have no parameters", pos);
            }
        }
        else if (kind == MethodKind.Set)
        {
            if (@params.Count != 1)
            {
                throw new JsParseException("a setter must have exactly one parameter", pos);
            }

            if (@params[0] is SpreadElement)
            {
                throw new JsParseException("a setter parameter may not be a rest element", pos);
            }
        }
    }

    /// <summary>Parse a class element key: identifier, string, numeric,
    /// computed <c>[expr]</c>, or <c>#privateName</c>.</summary>
    private (Expression Key, bool Computed, bool IsPrivate) ParseClassElementKey(HashSet<string> privateScope)
    {
        if (Match(JsTokenKind.LBracket))
        {
            // A computed key is `[ AssignmentExpression[+In] ]` — `in` is always
            // allowed inside the brackets even within a `for` header [NoIn].
            var savedNoIn = _disallowInDepth;
            _disallowInDepth = 0;
            Expression expr;
            try { expr = ParseAssignment(); }
            finally { _disallowInDepth = savedNoIn; }
            Expect(JsTokenKind.RBracket, "expected ']' after computed key");
            return (expr, true, false);
        }
        if (_current.Kind == JsTokenKind.PrivateIdentifier)
        {
            var t = Advance();
            // §12.7.2 — a PrivateName's IdentifierName may use \u escapes; the
            // canonical name is the DECODED text (token Value), so `#\u{6F}` and
            // `#o` denote the same private name. Track the decoded name in the
            // class's private-name scope so a later `this.#o` resolves.
            var name = PrivateNameOf(t);
            privateScope.Add(name);
            return (new PrivateNameExpression(name, t.Start, t.End), false, true);
        }
        if (_current.Kind == JsTokenKind.StringLiteral)
        {
            var t = Advance();
            return (new StringLiteral((string)t.Value!, t.Start, t.End), false, false);
        }
        if (_current.Kind == JsTokenKind.NumericLiteral)
        {
            var t = Advance();
            return (new NumericLiteral((double)t.Value!, t.Start, t.End), false, false);
        }
        if (_current.Kind == JsTokenKind.Identifier
            || IsReservedNameAllowedAsPropertyName(_current.Kind))
        {
            var t = Advance();
            return (new Identifier(t.Lexeme, t.Start, t.End), false, false);
        }
        throw new JsParseException(
            $"expected class member name, got {_current.Kind}", _current.Start);
    }

    /// <summary>Depth counter set by the parser while it walks a class
    /// declaration / expression that has an <c>extends</c> clause. Used
    /// to enable <c>super(...)</c> parsing inside that scope.</summary>

    /// <summary>Parse a <c>super</c> primary — must be followed by <c>.x</c>,
    /// <c>[expr]</c>, or <c>(args)</c>.</summary>
    private Expression ParseSuperExpression()
    {
        var start = _current.Start;
        Advance(); // 'super'
        if (Match(JsTokenKind.Dot))
        {
            // §13.3.7.1 — a SuperProperty is only legal inside a method /
            // accessor / constructor / class-field initializer (a scope with a
            // [[HomeObject]]). Outside one — e.g. global code, an ordinary
            // function, or indirect/global eval — it is an early SyntaxError.
            if (_superPropertyDepth == 0)
            {
                throw new JsParseException("'super' keyword unexpected here", start);
            }
            // super.x or super.#x — private super access is rare but valid.
            if (_current.Kind == JsTokenKind.PrivateIdentifier)
            {
                throw new JsParseException("private member access on 'super' is not supported", _current.Start);
            }
            var prop = ExpectIdentifierName("expected property name after 'super.'");
            return new SuperPropertyExpression(
                new Identifier(prop.Lexeme, prop.Start, prop.End),
                Computed: false, start, prop.End);
        }
        if (Match(JsTokenKind.LBracket))
        {
            // §13.3.7.1 — same gate as the dotted form (see above).
            if (_superPropertyDepth == 0)
            {
                throw new JsParseException("'super' keyword unexpected here", start);
            }

            var expr = ParseAssignment();
            var end = _current.End;
            Expect(JsTokenKind.RBracket, "expected ']' after computed super key");
            return new SuperPropertyExpression(expr, Computed: true, start, end);
        }
        if (Check(JsTokenKind.LParen))
        {
            if (_derivedConstructorDepth == 0)
            {
                throw new JsParseException(
                    "'super(...)' is only allowed inside a derived class constructor", start);
            }

            Advance(); // (
            var args = ParseArgumentList();
            var end = _current.End;
            Expect(JsTokenKind.RParen, "expected ')' after super call args");
            return new SuperCallExpression(args, start, end);
        }
        throw new JsParseException(
            "'super' must be followed by '.', '[', or '('", start);
    }
}
