using Starling.Js.Ast;
using Starling.Js.Lex;

namespace Starling.Js.Parse;

/// <summary>
/// ES strict-mode support for the parser: directive-prologue detection and the
/// strict early-error checks (§§12.1, 13.5.1, 13.4.1, 14.3.1, 14.7.5.1, 15.5,
/// 15.7, B.1.2). Strictness is tracked by <see cref="JsParser._strict"/> with a
/// stack discipline; the per-scope helpers here save and restore it.
/// </summary>
public ref partial struct JsParser
{
    /// <summary>§11.2.2 — true when <paramref name="s"/> is a "use strict"
    /// directive: an ExpressionStatement consisting of a single StringLiteral
    /// whose RAW source slice is exactly <c>"use strict"</c> or
    /// <c>'use strict'</c> (no escapes, no continuations).</summary>
    private static bool IsUseStrictDirective(Statement s, string rawLexeme)
        => s is ExpressionStatement { Expression: StringLiteral }
        && (rawLexeme == "\"use strict\"" || rawLexeme == "'use strict'");

    /// <summary>True when a parsed statement could still be part of the
    /// directive prologue — i.e. it is an ExpressionStatement wrapping a bare
    /// StringLiteral. Once a non-string-literal statement appears, the prologue
    /// ends (§11.2.1 / §16.1.1).</summary>
    private static bool IsDirective(Statement s)
        => s is ExpressionStatement { Expression: StringLiteral };

    // -----------------------------------------------------------------------
    // Strict early errors (§§12, 13, 14). Each is a no-op unless _strict.
    // -----------------------------------------------------------------------

    /// <summary>The strict-mode FutureReservedWords (§12.7.2) that may not be
    /// used as a BindingIdentifier when the scope is strict.</summary>
    private static bool IsStrictReservedWord(string name) => name is
        "implements" or "interface" or "let" or "package" or "private"
        or "protected" or "public" or "static" or "yield";

    /// <summary>§13.1.1 / §14.3.1 — a BindingIdentifier may not be
    /// <c>eval</c> or <c>arguments</c> in strict code, nor a strict
    /// FutureReservedWord. Applies to function/generator names, formal
    /// parameters, var/let/const binding names, class names, and catch
    /// parameters. No-op when not strict.</summary>
    private void CheckBindingIdentifier(string name, JsPosition pos)
    {
        if (!_strict)
        {
            return;
        }

        if (name is "eval" or "arguments")
        {
            throw new JsParseException(
                $"'{name}' may not be used as a binding identifier in strict mode", pos);
        }

        if (IsStrictReservedWord(name))
        {
            throw new JsParseException(
                $"'{name}' is a reserved word and may not be used as a binding identifier in strict mode", pos);
        }
    }

    /// <summary>§12.9.3 / B.1.2 — in strict code a legacy octal integer
    /// literal (<c>0123</c>), a NonOctalDecimalInteger (<c>08</c>/<c>09</c>),
    /// or a string with a legacy octal / <c>\8</c>/<c>\9</c> escape is a
    /// SyntaxError. The lexer tags these tokens; we raise the error here when
    /// the scope is strict. No-op otherwise (legacy sloppy semantics).</summary>
    private void CheckLegacyOctalLiteral(JsToken t)
    {
        if (_strict && t.LegacyOctal)
        {
            throw new JsParseException(
                "octal literals and octal escape sequences are not allowed in strict mode", t.Start);
        }
    }

    /// <summary>§13.5.1.1 — true when an expression is a bare (possibly
    /// parenthesized) Identifier reference, which <c>delete</c> may not target
    /// in strict code. Member accesses (<c>delete a.b</c>) are fine. Grouping
    /// parentheses are transparent here because <c>delete (x)</c> still deletes
    /// the reference <c>x</c>.</summary>
    private static bool IsUnqualifiedReference(Expression e) => e is Identifier;

    /// <summary>§15.7.1 — a class binding name is always checked under strict
    /// rules (class definitions are strict code), independent of the surrounding
    /// scope. <c>eval</c>/<c>arguments</c> and strict reserved words are errors.</summary>
    private void CheckClassBindingName(Identifier name)
    {
        if (name.Name is "eval" or "arguments")
        {
            throw new JsParseException(
                $"'{name.Name}' may not be used as a class name", name.Start);
        }

        if (IsStrictReservedWord(name.Name))
        {
            throw new JsParseException(
                $"'{name.Name}' is a reserved word and may not be used as a class name", name.Start);
        }
        // §15.7.1 — in an async context (e.g. a class static initialization
        // block) `await` is the AwaitExpression keyword and may not be a class
        // BindingIdentifier (`static { class await {} }`).
        if (_inAsync && name.Name == "await")
        {
            throw new JsParseException(
                "'await' may not be used as a class name in an async context", name.Start);
        }
    }

    /// <summary>§13.3.1.1 / §14.3.1 — recursively check every BindingIdentifier
    /// in a binding target (identifier or destructuring pattern) against the
    /// strict binding-name rules. No-op when not strict. Used for
    /// <c>var</c>/<c>let</c>/<c>const</c> and catch-clause binding names.</summary>
    private void CheckPatternBindingNames(Expression target)
    {
        if (!_strict)
        {
            return;
        }

        switch (target)
        {
            case Identifier id:
                CheckBindingIdentifier(id.Name, id.Start);
                break;
            case AssignmentPattern ap:
                CheckPatternBindingNames(ap.Target);
                break;
            case ArrayPattern arr:
                foreach (var el in arr.Elements)
                {
                    if (el is ArrayPatternBindingElement be)
                    {
                        CheckPatternBindingNames(be.Target);
                    }
                    else if (el is ArrayPatternRestElement re)
                    {
                        CheckPatternBindingNames(re.Target);
                    }
                }
                break;
            case ObjectPattern obj:
                foreach (var prop in obj.Properties)
                {
                    CheckPatternBindingNames(prop.Target);
                }

                if (obj.Rest is not null)
                {
                    CheckPatternBindingNames(obj.Rest.Argument);
                }

                break;
            case RestElement rest:
                CheckPatternBindingNames(rest.Argument);
                break;
        }
    }

    /// <summary>§13.5.1 / §13.4.1 — in strict code an assignment or
    /// increment/decrement may not target the identifier <c>eval</c> or
    /// <c>arguments</c>. No-op when not strict.</summary>
    private void CheckAssignmentTarget(Expression target, JsPosition pos)
    {
        if (!_strict)
        {
            return;
        }

        switch (target)
        {
            case Identifier { Name: "eval" or "arguments" } id:
                throw new JsParseException(
                    $"'{id.Name}' may not be assigned in strict mode", pos);
            // §13.15.1 — assignment-pattern targets are SimpleAssignmentTargets
            // too, so a strict `eval`/`arguments` anywhere inside the pattern
            // (`{ eval } = …`, `[arguments] = …`, `{ x = 0, eval } = …`) is the
            // same early SyntaxError. Recurse over the reinterpreted pattern.
            case AssignmentPattern ap:
                CheckAssignmentTarget(ap.Target, ap.Target.Start);
                break;
            case ArrayPattern arr:
                foreach (var el in arr.Elements)
                {
                    if (el is ArrayPatternBindingElement be)
                    {
                        CheckAssignmentTarget(be.Target, be.Target.Start);
                    }
                    else if (el is ArrayPatternRestElement re)
                    {
                        CheckAssignmentTarget(re.Target, re.Target.Start);
                    }
                }
                break;
            case ObjectPattern obj:
                foreach (var prop in obj.Properties)
                {
                    CheckAssignmentTarget(prop.Target, prop.Target.Start);
                }

                if (obj.Rest is not null)
                {
                    CheckAssignmentTarget(obj.Rest.Argument, obj.Rest.Argument.Start);
                }

                break;
        }
    }

    /// <summary>§15.2.1 / §15.3.1 / §14.1.2 — validate a function's formal
    /// parameters once the function's effective strictness is known. Checks:
    /// (a) no BindingIdentifier is <c>eval</c>/<c>arguments</c> or a strict
    /// FutureReservedWord when <paramref name="strict"/>; (b) no duplicate
    /// parameter names when <paramref name="strict"/> OR the list is
    /// non-simple (any default/rest/destructuring element). A simple sloppy
    /// parameter list still permits duplicates (§B.3.1 legacy).</summary>
    private void ValidateParameters(IReadOnlyList<Expression> @params, bool strict,
        bool forceDuplicateCheck = false)
    {
        var simple = AreSimpleParams(@params);
        // §15.3.1 — ArrowFormalParameters always forbid duplicate bindings, even
        // a simple sloppy list (no Annex B legacy exception for arrows).
        var forbidDuplicates = strict || !simple || forceDuplicateCheck;
        HashSet<string>? seen = forbidDuplicates ? new HashSet<string>(StringComparer.Ordinal) : null;
        foreach (var p in @params)
        {
            ValidateParameterElement(p, strict, forbidDuplicates, seen);
        }
    }

    private void ValidateParameterElement(Expression p, bool strict, bool forbidDuplicates, HashSet<string>? seen)
    {
        switch (p)
        {
            case Identifier id:
                if (strict)
                {
                    CheckBindingIdentifier(id.Name, id.Start);
                }

                if (forbidDuplicates && seen is not null && !seen.Add(id.Name))
                {
                    throw new JsParseException(
                        $"duplicate parameter name '{id.Name}'", id.Start);
                }

                break;
            case AssignmentPattern ap:
                ValidateParameterElement(ap.Target, strict, forbidDuplicates, seen);
                break;
            case SpreadElement sp:
                ValidateParameterElement(sp.Argument, strict, forbidDuplicates, seen);
                break;
            case RestElement re:
                ValidateParameterElement(re.Argument, strict, forbidDuplicates, seen);
                break;
            case ArrayPattern arr:
                foreach (var el in arr.Elements)
                {
                    ValidateArrayPatternElement(el, strict, forbidDuplicates, seen);
                }

                break;
            case ObjectPattern obj:
                foreach (var prop in obj.Properties)
                {
                    ValidateParameterElement(prop.Target, strict, forbidDuplicates, seen);
                }

                if (obj.Rest is not null)
                {
                    ValidateParameterElement(obj.Rest, strict, forbidDuplicates, seen);
                }

                break;
        }
    }

    private void ValidateArrayPatternElement(ArrayPatternElement el, bool strict, bool forbidDuplicates, HashSet<string>? seen)
    {
        switch (el)
        {
            case ArrayPatternBindingElement be:
                ValidateParameterElement(be.Target, strict, forbidDuplicates, seen);
                break;
            case ArrayPatternRestElement re:
                ValidateParameterElement(re.Target, strict, forbidDuplicates, seen);
                break;
        }
    }

    /// <summary>§15.1.3 IsSimpleParameterList — true when every element is a
    /// plain BindingIdentifier (no defaults, rest, or destructuring). A
    /// non-simple list forbids duplicate names even in sloppy mode.</summary>
    private static bool AreSimpleParams(IReadOnlyList<Expression> @params)
    {
        foreach (var p in @params)
        {
            if (p is not Identifier)
            {
                return false;
            }
        }

        return true;
    }

    // -----------------------------------------------------------------------
    // Function-body parse path with directive-prologue strictness (§15.2.1).
    // -----------------------------------------------------------------------

    /// <summary>Parse a <c>{ … }</c> function body, establishing the function's
    /// own strictness from its directive prologue (§11.2.1). The body inherits
    /// the surrounding <see cref="_strict"/> and ORs in any <c>"use strict"</c>
    /// directive. Returns the parsed block and the body's effective strictness.
    /// The caller is responsible for saving/restoring <see cref="_strict"/>
    /// around its whole parse (including the parameter list, whose early errors
    /// use the resulting strictness per §15.2.1 — the directive prologue's
    /// effect applies to the entire function definition).</summary>
    /// <summary>§15.2.1 — true when the most recently parsed FunctionBody's
    /// directive prologue literally contained a <c>"use strict"</c> directive
    /// (ContainsUseStrict). Read by callers right after
    /// <see cref="ParseFunctionBody"/> to enforce the simple-parameter-list
    /// rule, independent of any inherited strictness.</summary>

    private (BlockStatement Body, bool Strict) ParseFunctionBody()
    {
        var start = _current.Start;
        Expect(JsTokenKind.LBrace, "{ expected");
        var savedNoIn = _disallowInDepth;
        _disallowInDepth = 0;
        var savedPrologueUseStrict = _prologueHadUseStrict;
        _prologueHadUseStrict = false;
        // The body is no longer formal parameters (a function nested in a param
        // default value has its own body, where yield/await are unrestricted).
        var savedInParams = _inFormalParameters;
        _inFormalParameters = false;
        // A nested function/method body establishes its own await rules, so the
        // enclosing static-block `await` reservation does not apply inside it.
        var savedInStatic = _inStaticBlock;
        _inStaticBlock = false;
        // An ordinary function body provides a `new.target` binding (§13.3.12).
        _functionDepth++;
        try
        {
            var body = new List<Statement>();
            // The directive prologue can flip this body to strict.
            ScanDirectivePrologue(body, isProgram: false);
            while (!Check(JsTokenKind.RBrace) && !Check(JsTokenKind.EndOfFile))
            {
                body.Add(ParseStatement());
            }

            var end = _current.End;
            Expect(JsTokenKind.RBrace, "expected '}' to close block");
            // §15.2.1 — the FunctionBody's own lexical/var early errors.
            CheckScopeEarlyErrors(body, ScopeKind.TopLevel);
            _lastBodyContainsUseStrict = _prologueHadUseStrict;
            return (new BlockStatement(body, start, end), _strict);
        }
        finally
        {
            _disallowInDepth = savedNoIn;
            _functionDepth--;
            _prologueHadUseStrict = savedPrologueUseStrict;
            _inFormalParameters = savedInParams;
            _inStaticBlock = savedInStatic;
        }
    }

    /// <summary>§15.2.1 / §15.3.1 / §15.5.1 / §15.7.1 — a function whose body
    /// has a <c>"use strict"</c> directive may not have a non-simple parameter
    /// list. Call right after <see cref="ParseFunctionBody"/> (which sets
    /// <see cref="_lastBodyContainsUseStrict"/>).</summary>
    private void CheckUseStrictSimpleParams(IReadOnlyList<Expression> @params, JsPosition pos)
    {
        if (_lastBodyContainsUseStrict && !AreSimpleParams(@params))
        {
            throw new JsParseException(
                "a function with a non-simple parameter list may not declare \"use strict\"", pos);
        }
    }
}
