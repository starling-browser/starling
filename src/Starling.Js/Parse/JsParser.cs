using Starling.Js.Ast;
using Starling.Js.Lex;

namespace Starling.Js.Parse;

/// <summary>
/// Recursive-descent ES2024 parser for scripts, modules, statements,
/// declarations, expressions, functions, classes, and early errors.
/// </summary>
/// <remarks>
/// Operator precedence mirrors ES2024 §13.16. Methods are named by
/// precedence level (e.g. <c>ParseAdditive</c>, <c>ParseMultiplicative</c>)
/// so cross-referencing the spec is line-of-sight. Right-associative
/// operators (assignment, exponentiation, conditional) recurse on the
/// right side, and left-associative ones loop.
/// </remarks>
public ref partial struct JsParser
{
    private JsLexer _lex;
    private JsToken _current;
    private int _disallowInDepth;

    /// <summary>§13.6 — identity set of expression nodes that were written
    /// inside parentheses (a grouping / sequence). Used to permit a grouped
    /// unary base of <c>**</c> (<c>(-1) ** 2</c>) while rejecting the bare form
    /// (<c>-1 ** 2</c>) as an early SyntaxError. Reference identity is enough:
    /// every grouped node is a distinct AST instance.</summary>
    private readonly HashSet<Expression> _parenthesized =
        new(ReferenceEqualityComparer.Instance);
    /// <summary>SpreadElements in an array literal that were immediately followed
    /// by a comma (`[...x,]` / `[...x, y]`). When such an array is reinterpreted
    /// as a destructuring AssignmentPattern, the AssignmentRestElement must be
    /// the last element with no trailing comma — so a trailing/intervening comma
    /// is an early SyntaxError (§13.15.5.1). The parser drops the trailing comma
    /// from the element list, so record it here by reference identity.</summary>
    private readonly HashSet<Expression> _spreadFollowedByComma =
        new(ReferenceEqualityComparer.Instance);
    /// <summary>ObjectExpressions that contain a CoverInitializedName — a
    /// shorthand property with an initializer (`{ a = 1 }`). This cover form is a
    /// SyntaxError unless the object is reinterpreted as an
    /// ObjectAssignmentPattern / ObjectBindingPattern (§13.2.5.1). Recorded by
    /// reference identity at parse time and cleared by the reinterpret pass; any
    /// object still present at the end of the parse was used as a value and is an
    /// early SyntaxError.</summary>
    private readonly HashSet<Expression> _coverInitObjects =
        new(ReferenceEqualityComparer.Instance);
    /// <summary>Nesting depth of ordinary (non-arrow) function bodies. Arrow
    /// functions do NOT have their own <c>new.target</c>; they inherit the
    /// enclosing function's, so they don't bump this. <c>new.target</c> is an
    /// early SyntaxError when this is 0 (top-level script / eval / module
    /// global code) — §13.3.12.</summary>
    private int _functionDepth;

    /// <summary>ES strict mode. True while the parser is inside a strict scope
    /// (a script/function whose directive prologue had <c>"use strict"</c>, code
    /// lexically nested in such a scope, or any class body — which is always
    /// strict per §15.7). Maintained as a save/restore stack discipline around
    /// every function/program/class scope. Drives the strict early-error checks
    /// (§§12, 13, 14, 15) and is stamped onto the AST so the compiler can thread
    /// strictness into the bytecode chunk.</summary>
    private bool _strict;

    /// <summary>True while parsing the body (and parameter list, for the
    /// relevant checks) of an async function/method/arrow. In this context
    /// <c>await</c> is always the AwaitExpression keyword and may NOT be used
    /// as a BindingIdentifier / IdentifierReference / LabelIdentifier
    /// (§13.3.10.1 / §15.8). Inherited by nested arrow functions (they have no
    /// async-ness of their own) but reset by nested non-arrow functions.
    /// Save/restore stack discipline around every function/method scope.</summary>
    private bool _inAsync;

    /// <summary>True while parsing the body of a generator function/method.
    /// In this context <c>yield</c> is always the YieldExpression keyword and
    /// may NOT be used as a BindingIdentifier (§14.4 / §15.5). Generator-ness
    /// is NOT inherited by nested arrows for parameter purposes; like
    /// <see cref="_inAsync"/> it is saved/restored at every function scope.</summary>
    private bool _inGenerator;

    /// <summary>True while parsing a FormalParameters / ArrowFormalParameters
    /// list (including default-value expressions). FormalParameters may not
    /// contain an <c>await</c> AwaitExpression (when <see cref="_inAsync"/>) nor
    /// a <c>yield</c> YieldExpression (when <see cref="_inGenerator"/>) —
    /// §15.1.1 / §15.5.1 / §15.8.1. Reset to false once the body is entered, and
    /// saved/restored across nested function/arrow scopes parsed inside a
    /// default value.</summary>
    private bool _inFormalParameters;

    /// <summary>True while parsing the (possibly label-prefixed) body of an
    /// iteration statement (<c>for</c>/<c>for-in</c>/<c>for-of</c>/<c>while</c>/
    /// <c>do-while</c>). In that position the Annex B.3.2 labelled
    /// FunctionDeclaration extension does NOT apply, so even in sloppy mode
    /// `for (;;) lbl: function f(){}` is an early SyntaxError (§14.13.1 /
    /// Annex B.3.2). Stays set through a chain of nested labels; cleared once a
    /// non-label statement is entered.</summary>
    private bool _forbidLabelledFunction;

    /// <summary>True while parsing a class static initialization block body.
    /// §15.7.1 — `await` is fully reserved there: not a valid
    /// BindingIdentifier/IdentifierReference NOR a usable AwaitExpression keyword
    /// (ContainsAwait of a ClassStaticBlockBody is a Syntax Error). Reset by
    /// nested function scopes parsed inside the block.</summary>
    private bool _inStaticBlock;

    /// <summary>True for the whole parse when the goal symbol is Module
    /// (<see cref="ParseModule"/>). Persistent (never saved/restored) so a nested
    /// non-async function inside a module still knows it is module code — where
    /// <c>await</c> is reserved and may never be the AwaitExpression keyword
    /// outside an async context, so <c>await UnaryExpression</c> in a non-async
    /// nested function is an early SyntaxError (§16.2.1.6.2), not the liberal
    /// "accept and defer to runtime" form used for classic scripts.</summary>
    private bool _module;
    private readonly Stack<HashSet<string>> _classPrivateScopes = new();
    private int _derivedConstructorDepth;
    private readonly Stack<Dictionary<string, PrivateDecl>> _privateDeclStack = new();
    private int _baseClassContextDepth;
    private bool _prologueHadUseStrict;
    private bool _lastBodyContainsUseStrict;

    /// <summary>True at the Module goal's <em>own</em> top level — i.e. the
    /// implicit <c>[+Await]</c> context of §16.2.1.6.2 where <c>await</c> is the
    /// AwaitExpression keyword. Distinct from <see cref="_inAsync"/> (which is the
    /// async-function context) so the two compose: await is the keyword when
    /// EITHER holds (<see cref="AwaitIsKeyword"/>). Reset to false at every
    /// function/arrow boundary (a non-async arrow/function at module top level is
    /// <c>[~Await]</c>: its <c>ConciseBody</c>/<c>FunctionBody</c> is not the
    /// module's await context), and restored afterwards.</summary>
    private bool _moduleTopAwait;

    /// <summary>§13.3.10.1 / §16.2.1.6.2 — <c>await</c> is the AwaitExpression
    /// keyword (never an identifier) when inside an async function context
    /// (<see cref="_inAsync"/>) OR at the Module goal's top level
    /// (<see cref="_moduleTopAwait"/>).</summary>
    private bool AwaitIsKeyword => _inAsync || _moduleTopAwait;

    /// <summary>wp:M3-71 — §13.3.7.1 — depth of enclosing method/accessor/
    /// constructor/class-field-initializer scopes, i.e. scopes that have a
    /// <c>[[HomeObject]]</c> so a SuperProperty (<c>super.x</c> / <c>super[x]</c>)
    /// is legal. A SuperProperty outside any such scope is an early SyntaxError
    /// (§13.3.7.1). For direct-eval code the seeding context (see
    /// <see cref="DirectEvalContext"/>) starts this at 1 when the caller is a
    /// method so a SuperProperty in the source parses, while an indirect/global
    /// eval keeps it at 0 and throws.</summary>
    private int _superPropertyDepth;

    /// <summary>wp:M3-71 — caller's lexical context threaded into a DIRECT eval's
    /// parse so §19.2.1.1 / §13.3.7.1 early errors fire (or don't) per the
    /// caller. Strictness, in-function-ness (for <c>new.target</c>), in-method-ness
    /// (for SuperProperty), and derived-constructor-ness (for SuperCall).</summary>
    public readonly record struct DirectEvalContext(
        bool CallerStrict, bool InFunction, bool InMethod, bool InDerivedConstructor);

    public JsParser(string source)
        : this(new JsLexer(source, ThrowingLexErrorSink.Instance)) { }

    public JsParser(JsLexer lex)
    {
        _lex = lex;
        _current = _lex.Next();
    }

    private string SourceSlice(JsPosition start, JsPosition end)
    {
        var startOffset = Math.Clamp(start.Offset, 0, _lex.Source.Length);
        var endOffset = Math.Clamp(end.Offset, startOffset, _lex.Source.Length);
        return _lex.Source[startOffset..endOffset].ToString();
    }

    /// <summary>A lexer error is an early <c>SyntaxError</c> per §12 — surface
    /// it as a <see cref="JsParseException"/> the moment the lexer reports it
    /// (malformed numeric/string/escape, unterminated literal, etc.). Without
    /// this the default sink silently drops them and the parser accepts the
    /// invalid token.</summary>
    private sealed class ThrowingLexErrorSink : IJsLexErrorSink
    {
        public static readonly ThrowingLexErrorSink Instance = new();
        public void Report(JsLexError code, JsPosition position, string message)
            => throw new JsParseException($"lexical error: {message}", position);
    }

    // -----------------------------------------------------------------------
    // Public entries
    // -----------------------------------------------------------------------

    /// <summary>
    /// Parse a full expression and require EOF immediately after. Throws
    /// <see cref="JsParseException"/> on syntax error.
    /// </summary>
    public Expression ParseExpression()
    {
        var expr = ParseAssignment();
        // Allow sequence at the top.
        if (Match(JsTokenKind.Comma))
        {
            var parts = new List<Expression> { expr };
            do { parts.Add(ParseAssignment()); }
            while (Match(JsTokenKind.Comma));
            expr = new SequenceExpression(parts, parts[0].Start, parts[^1].End);
        }
        Expect(JsTokenKind.EndOfFile, "expected end of input");
        CheckNoPendingCoverInit();
        return expr;
    }

    // -----------------------------------------------------------------------
    // Token plumbing
    // -----------------------------------------------------------------------

    private bool Check(JsTokenKind k) => _current.Kind == k;

    private bool Match(JsTokenKind k)
    {
        if (_current.Kind != k)
        {
            return false;
        }

        Advance();
        return true;
    }

    private JsToken Advance()
    {
        var prev = _current;
        _current = _lex.Next();
        return prev;
    }

    private JsToken Expect(JsTokenKind k, string what)
    {
        if (_current.Kind != k)
        {
            throw new JsParseException($"{what} (got {_current.Kind} '{_current.Lexeme}')", _current.Start);
        }

        return Advance();
    }

    // -----------------------------------------------------------------------
    // ES2024 §13.16 precedence ladder (low to high).
    // -----------------------------------------------------------------------

    // AssignmentExpression
    private Expression ParseAssignment()
    {
        // B1b-2c — `yield` / `yield expr` / `yield* expr`. §14.4 restricts the
        // YieldExpression to generator bodies; OUTSIDE a generator `yield` is
        // an ordinary IdentifierReference (sloppy) or a reserved word (strict —
        // caught downstream by the strict reserved-word checks). The lexer
        // always emits the `Yield` token, so only treat it as a yield expression
        // when we are actually inside a generator.
        if (_current.Kind == JsTokenKind.Yield && _inGenerator)
        {
            // §15.5.1 — a generator's FormalParameters may not contain a
            // YieldExpression (`*g(x = yield) {}`, `(x = yield) => {}` inside a
            // generator).
            if (_inFormalParameters)
            {
                throw new JsParseException(
                    "a YieldExpression may not appear in formal parameters", _current.Start);
            }

            var yieldTok = Advance();
            // §15.5 — `yield * AssignmentExpression` (delegate yield) has a
            // [no LineTerminator here] restriction before the `*`. A newline
            // makes this a bare `yield` (ASI), so the following `*` is NOT a
            // delegate marker (`yield \n * 1` is then an invalid `* 1`).
            var delegateYield = _current.Kind == JsTokenKind.Star
                && !_current.PrecededByLineTerminator
                && Match(JsTokenKind.Star);
            // No argument when followed by `,` `;` `)` `]` `}` `:` or EOF, or —
            // per the [no LineTerminator here] restriction on the operand — when
            // a LineTerminator separates `yield` from what follows.
            Expression? arg = null;
            if (!delegateYield && (_current.PrecededByLineTerminator
                || _current.Kind is JsTokenKind.Semicolon
                or JsTokenKind.Comma or JsTokenKind.RParen or JsTokenKind.RBracket
                or JsTokenKind.RBrace or JsTokenKind.Colon or JsTokenKind.EndOfFile))
            {
                return new YieldExpression(null, false, yieldTok.Start, yieldTok.End);
            }
            arg = ParseAssignment();
            return new YieldExpression(arg, delegateYield, yieldTok.Start, arg.End);
        }
        // B1b-2c — async arrow function. `async x => …` or `async (…) => …`.
        // §12.7.2 — the contextual `async` keyword may NOT contain a Unicode
        // escape (`async () => {}` is not an async arrow; it falls through
        // and is rejected as an ordinary expression).
        if (_current.Kind == JsTokenKind.Identifier && _current.TextEquals("async")
            && !_current.ContainsEscape)
        {
            var asyncPeek = _lex.Peek();
            // `async <ident> =>` — concise async arrow with single identifier param.
            if (asyncPeek.Kind == JsTokenKind.Identifier)
            {
                // We need a 3rd token, but Peek only gives one. Save+consume.
                var asyncTok = Advance();
                if (_current.Kind == JsTokenKind.Identifier && _lex.Peek().Kind == JsTokenKind.Arrow)
                {
                    var paramTok = Advance();
                    // §15.8 — `await` is forbidden as the BindingIdentifier param
                    // of an async arrow (`async await => …`).
                    if (paramTok.TextEquals("await"))
                    {
                        throw new JsParseException(
                            "'await' may not be used as a binding identifier in an async context", paramTok.Start);
                    }

                    var param = new Identifier(paramTok.Lexeme, paramTok.Start, paramTok.End);
                    Expect(JsTokenKind.Arrow, "expected '=>' in async arrow function");
                    return ParseArrowBody(new List<Expression> { param }, asyncTok.Start, async: true);
                }
                // Not an arrow — treat 'async' as identifier and reparse.
                // Fall through: we already consumed the 'async' token, so
                // produce an Identifier for it and let the rest of the
                // expression continue from _current.
                var ident = new Identifier("async", asyncTok.Start, asyncTok.End);
                return ContinueAssignment(ident);
            }
            // `async (` — could be `async (…) => …` or `async(arg)` call.
            // §12.7.2 — no LineTerminator is permitted between the `async`
            // keyword and the parameter list of an async arrow; with one,
            // `async` is an IdentifierReference (`async \n (foo) => {}` is then
            // an invalid `async(foo) => {}` and falls through to an error).
            if (asyncPeek.Kind == JsTokenKind.LParen && !asyncPeek.PrecededByLineTerminator)
            {
                // Best-effort: try parse params + arrow. If it fails, the
                // caller will have already consumed tokens — keep it simple
                // by checking the token *after* the matching ')'. We won't
                // implement full backtracking; instead, scan forward.
                if (LooksLikeAsyncArrow())
                {
                    var asyncTok = Advance(); // async
                    Advance(); // (
                    var ps = new List<Expression>();
                    // §15.8 / §15.9 — the FormalParameters of an async arrow are
                    // parsed [+Await], so `await` may not appear as a binding
                    // identifier anywhere in them (including default-value
                    // expressions and nested-arrow parameter positions). Set the
                    // async context for the duration of the param parse so
                    // ParseBindingTarget / ParseUnary reject `await` uses.
                    var savedAsyncParams = _inAsync;
                    _inAsync = true;
                    try
                    {
                        if (!Check(JsTokenKind.RParen))
                        {
                            while (true)
                            {
                                if (Check(JsTokenKind.Ellipsis))
                                {
                                    var sstart = _current.Start;
                                    Advance();
                                    var inner = ParseBindingTarget();
                                    ps.Add(new SpreadElement(inner, sstart, inner.End));
                                    break;
                                }
                                ps.Add(ParseParameter());
                                if (!Match(JsTokenKind.Comma))
                                {
                                    break;
                                }
                            }
                        }
                        Expect(JsTokenKind.RParen, "expected ')' in async arrow params");
                    }
                    finally { _inAsync = savedAsyncParams; }
                    Expect(JsTokenKind.Arrow, "expected '=>' after async arrow params");
                    return ParseArrowBody(ps, asyncTok.Start, async: true);
                }
            }
            // `async function …` — async function expression. Route it through
            // the call/member tail (ContinueAssignment) so an immediately-invoked
            // async function expression `async function(){…}(args)` attaches its
            // call instead of orphaning the '(' — returning the bare function
            // here skipped ParseCallAndMemberTail and broke github's async IIFEs.
            if (asyncPeek.Kind == JsTokenKind.Function)
            {
                var asyncTok = Advance(); // async
                return ContinueAssignment(ParseFunctionExpression(asyncTok.Start, isAsync: true));
            }
        }
        // Arrow function fast path: a bare identifier followed by '=>' is the
        // concise-param form `x => expr`.
        if (_current.Kind == JsTokenKind.Identifier && _lex.Peek().Kind == JsTokenKind.Arrow)
        {
            var paramTok = Advance();
            // §15.8 — in an async context (e.g. the param list of an enclosing
            // async arrow), `await` is the AwaitExpression keyword and may not be
            // a single-identifier arrow parameter (`await => {}`).
            if (_inAsync && paramTok.TextEquals("await"))
            {
                throw new JsParseException(
                    "'await' may not be used as a binding identifier in an async context", paramTok.Start);
            }

            var param = new Identifier(paramTok.Lexeme, paramTok.Start, paramTok.End);
            // §15.3 — no LineTerminator is permitted before the `=>` of an arrow
            // (the production has a [no LineTerminator here] restriction). With
            // one, ASI would have ended the statement (`x \n => {}` is an error).
            if (_current.PrecededByLineTerminator)
            {
                throw new JsParseException(
                    "no line terminator allowed before '=>' in an arrow function", _current.Start);
            }

            Expect(JsTokenKind.Arrow, "expected '=>' in arrow function");
            return ParseArrowBody(new List<Expression> { param }, paramTok.Start);
        }
        // Empty-param arrow: `() =>`. Two-token lookahead.
        if (_current.Kind == JsTokenKind.LParen && _lex.Peek().Kind == JsTokenKind.RParen)
        {
            var start = _current.Start;
            Advance(); Advance(); // consume `(` and `)`
            // §15.3 — no LineTerminator before the `=>` (`() \n => {}` is invalid).
            if (_current.PrecededByLineTerminator)
            {
                throw new JsParseException(
                    "no line terminator allowed before '=>' in an arrow function", _current.Start);
            }

            Expect(JsTokenKind.Arrow, "expected '=>' after '()' in arrow function");
            return ParseArrowBody(Array.Empty<Expression>(), start);
        }

        // Parenthesized-params arrow with a rest element, e.g. `(...a) => …` or
        // `(a, ...b) => …`. A leading `...` is not a valid grouping/sequence
        // expression, so we cannot rely on the cover-grammar `ParseConditional`
        // path below — it would throw on the `...` before the `=>` is seen.
        // Probe ahead (balanced parens then `=>`); if confirmed, parse the
        // parameter list directly with the same rest-aware logic the async
        // arrow path and function declarations use (rest → SpreadElement, the
        // node the compiler's rest-param handling already keys on). This also
        // covers the no-rest cases uniformly, but we only divert when an arrow
        // is certain so ordinary groupings/sequences are untouched.
        if (_current.Kind == JsTokenKind.LParen
            && _lex.LookaheadIsArrowFromParen(_current.End.Offset))
        {
            var start = _current.Start;
            Advance(); // consume `(`
            var ps = new List<Expression>();
            // §15.3.1 — ArrowFormalParameters may not contain a yield/await
            // expression (`(x = yield) => {}` inside a generator).
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
                        ps.Add(new SpreadElement(inner, sstart, inner.End));
                        break; // rest must be the last parameter
                    }
                    ps.Add(ParseParameter());
                    if (!Match(JsTokenKind.Comma))
                    {
                        break;
                    }
                }
                Expect(JsTokenKind.RParen, "expected ')' in arrow params");
            }
            finally { _inFormalParameters = savedInParams; }
            // §15.3 — no LineTerminator before the `=>`.
            if (_current.PrecededByLineTerminator)
            {
                throw new JsParseException(
                    "no line terminator allowed before '=>' in an arrow function", _current.Start);
            }

            Expect(JsTokenKind.Arrow, "expected '=>' after arrow params");
            return ParseArrowBody(ps, start);
        }

        var left = ParseConditional();
        // Parenthesized-params arrow form: ParseConditional() consumed the
        // `(...)` as either a grouping or a sequence. Either case maps cleanly
        // to ArrowFunctionExpression when followed by `=>`.
        if (_current.Kind == JsTokenKind.Arrow)
        {
            // §15.3 — no LineTerminator before the `=>`.
            if (_current.PrecededByLineTerminator)
            {
                throw new JsParseException(
                    "no line terminator allowed before '=>' in an arrow function", _current.Start);
            }

            Advance();
            var paramList = LiftArrowParams(left);
            return ParseArrowBody(paramList, left.Start);
        }
        if (IsAssignmentOp(_current.Kind))
        {
            var op = _current.Kind;
            var opPos = _current.Start;
            Advance();
            var right = ParseAssignment(); // right-associative
            // Only `=` reinterprets the LHS as a destructuring pattern, where a
            // CoverInitializedName (`{ a = 1 } = …`) is legal — clear any pending
            // cover-init error for the reinterpreted tree.
            var target = op == JsTokenKind.Eq ? ReinterpretAssignmentTarget(left) : left;
            // §13.15.1 / §13.5.1 — assignment to `eval`/`arguments` is a strict
            // SyntaxError.
            CheckAssignmentTarget(target, opPos);
            return new AssignmentExpression(op, target, right, target.Start, right.End);
        }
        return left;
    }

    private ArrowFunctionExpression ParseArrowBody(IReadOnlyList<Expression> @params, JsPosition start, bool async = false)
    {
        var savedStrict = _strict;
        var (savedAsync, savedGen) = (_inAsync, _inGenerator);
        var savedModuleAwait = _moduleTopAwait;
        try
        {
            // An async arrow body is an async context; a plain arrow inherits the
            // enclosing async-ness (so `await` works in an arrow nested in an
            // async fn). Arrows are never generators, but the body inherits the
            // enclosing generator context per §15.3 (yield refers outward).
            // An arrow body is never the Module's [+Await] top level (its
            // ConciseBody is [~Await] unless the arrow itself is async), so a
            // non-async arrow at module top level does NOT inherit module await.
            _inAsync = async || _inAsync;
            _moduleTopAwait = false;
            if (Check(JsTokenKind.LBrace))
            {
                var (block, strict) = ParseFunctionBody();
                // §15.3.1 — arrow parameter early errors use the arrow's own
                // strictness; arrow param lists are always checked for dups.
                CheckUseStrictSimpleParams(@params, start);
                ValidateParameters(@params, strict, forceDuplicateCheck: true);
                CheckParamsVsLexicalBody(@params, block);
                return new ArrowFunctionExpression(@params, block, IsExpression: false,
                    Async: async, start, block.End, Strict: strict,
                    SourceText: SourceSlice(start, block.End));
            }
            // The concise body is AssignmentExpression[+In]; a for-header [NoIn]
            // restriction never reaches an arrow body, so allow `in` again here.
            // A concise body has no directive prologue, so strictness is just
            // the inherited surrounding strictness.
            var savedNoIn = _disallowInDepth;
            _disallowInDepth = 0;
            // A concise body is not formal parameters; clear the restriction so
            // an enclosing param context (an arrow nested in another's default)
            // does not leak in.
            var savedInParams = _inFormalParameters;
            _inFormalParameters = false;
            Expression expr;
            try { expr = ParseAssignment(); }
            finally { _disallowInDepth = savedNoIn; _inFormalParameters = savedInParams; }
            ValidateParameters(@params, _strict, forceDuplicateCheck: true);
            return new ArrowFunctionExpression(@params, expr, IsExpression: true,
                Async: async, start, expr.End, Strict: _strict,
                SourceText: SourceSlice(start, expr.End));
        }
        finally { _strict = savedStrict; (_inAsync, _inGenerator) = (savedAsync, savedGen); _moduleTopAwait = savedModuleAwait; }
    }

    /// <summary>Continue ParseAssignment after consuming an unexpected lead
    /// token that turned out not to be the start of an async arrow.</summary>
    private Expression ContinueAssignment(Expression seed)
    {
        // Use the seed as the LHS for the rest of the expression. Because
        // the lead was a bare identifier, the remaining grammar is at most
        // a call / member / assignment / conditional. Reuse the existing
        // call/member tail + assignment-op handling.
        var left = ParseCallAndMemberTail(seed);
        if (_current.Kind == JsTokenKind.Arrow)
        {
            Advance();
            var paramList = LiftArrowParams(left);
            return ParseArrowBody(paramList, left.Start);
        }
        // Cycle through conditional / logical etc. Easiest: re-enter the
        // chain by walking from this LHS through the operator ladder. We
        // build the ladder by hand since we need to start mid-expression.
        return ContinueOperatorLadder(left);
    }

    /// <summary>Best-effort look-ahead: is the upcoming `async (...) ...` an
    /// arrow function? Scans matched parens then checks for `=>`.</summary>
    private bool LooksLikeAsyncArrow()
    {
        // Save lexer state via captured tokens. We need to scan forward over
        // the parenthesized chunk and peek at what follows. Since the lexer
        // is forward-only with one-token Peek, we instead snapshot using the
        // lexer's PushBack mechanism. Simpler: spawn a transient sub-lexer
        // from the lexer's checkpoint.
        return _lex.LookaheadIsAsyncArrow();
    }

    /// <summary>Continue the operator ladder mid-expression. Used when an
    /// ambiguous `async` lead token resolved to a plain identifier.</summary>
    private Expression ContinueOperatorLadder(Expression left)
    {
        // ParseConditional eats unary/binary/logical/conditional; we already
        // have the LHS of the LeftHandSide chain. Mirror ParseConditional by
        // walking precedence levels via parser helpers, but reuse the
        // assignment tail (for `=`, `+=`, etc.).
        // For simplicity, treat `left` as already a unary-or-higher and
        // resume from multiplicative. Most uses of async-as-identifier are
        // simple (e.g. `var async = 1`), so this short-circuits cleanly.
        // If a complex expression is parsed here, the engine still works
        // because expressions of identifiers are rare in async-keyword
        // ambiguity scenarios.
        if (IsAssignmentOp(_current.Kind))
        {
            var op = _current.Kind;
            Advance();
            var right = ParseAssignment();
            return new AssignmentExpression(op, left, right, left.Start, right.End);
        }
        return left;
    }

    /// <summary>Turn a parenthesized expression list (already parsed as a
    /// grouping or <see cref="SequenceExpression"/>) back into an arrow
    /// parameter list, reinterpreting cover literals as binding patterns.</summary>
    private List<Expression> LiftArrowParams(Expression expr)
    {
        var list = new List<Expression>();
        switch (expr)
        {
            case SequenceExpression seq:
                foreach (var e in seq.Expressions)
                {
                    list.Add(ReinterpretBindingParameter(e));
                }

                break;
            default:
                list.Add(ReinterpretBindingParameter(expr));
                break;
        }
        return list;
    }

    private static bool IsBindingPattern(Expression e) => e switch
    {
        Identifier => true,
        BindingPattern => true,
        AssignmentPattern { Target: var target } => IsBindingPattern(target),
        SpreadElement { Argument: var inner } => IsBindingPattern(inner),
        RestElement { Argument: var inner } => IsBindingPattern(inner),
        _ => false,
    };

    private static bool IsAssignmentOp(JsTokenKind k) => k is
        JsTokenKind.Eq or JsTokenKind.PlusEq or JsTokenKind.MinusEq
        or JsTokenKind.StarEq or JsTokenKind.SlashEq or JsTokenKind.PercentEq
        or JsTokenKind.StarStarEq or JsTokenKind.LtLtEq or JsTokenKind.GtGtEq
        or JsTokenKind.GtGtGtEq or JsTokenKind.AmpEq or JsTokenKind.PipeEq
        or JsTokenKind.CaretEq or JsTokenKind.AmpAmpEq or JsTokenKind.PipePipeEq
        or JsTokenKind.QuestionQuestionEq;

    // ConditionalExpression
    private Expression ParseConditional()
    {
        var test = ParseNullishCoalescing();
        if (Match(JsTokenKind.Question))
        {
            var cons = ParseAssignment();
            Expect(JsTokenKind.Colon, "expected ':' in conditional");
            var alt = ParseAssignment();
            return new ConditionalExpression(test, cons, alt, test.Start, alt.End);
        }
        return test;
    }

    // NullishCoalescing — left-associative.
    private Expression ParseNullishCoalescing()
    {
        var left = ParseLogicalOr();
        if (Check(JsTokenKind.QuestionQuestion))
        {
            // §12.6 — a CoalesceExpression may not immediately contain, nor be
            // immediately contained within, a LogicalAND/LogicalOR operation
            // (`a ?? b || c`, `a || b ?? c`, etc. must be parenthesized). The
            // head (left) must not be an unparenthesized `&&`/`||`.
            CheckCoalesceOperand(left);
            while (Check(JsTokenKind.QuestionQuestion))
            {
                var op = _current.Kind; Advance();
                var right = ParseLogicalOr();
                // The right operand (a BitwiseOR per the grammar) likewise must
                // not be an unparenthesized `&&`/`||`.
                CheckCoalesceOperand(right);
                left = new LogicalExpression(op, left, right, left.Start, right.End);
            }
        }
        return left;
    }

    /// <summary>§12.6 — reject a <c>&amp;&amp;</c>/<c>||</c> operand adjacent to a
    /// <c>??</c> unless it was parenthesized.</summary>
    private void CheckCoalesceOperand(Expression operand)
    {
        if (operand is LogicalExpression { Op: JsTokenKind.AmpAmp or JsTokenKind.PipePipe }
            && !_parenthesized.Contains(operand))
        {
            throw new JsParseException(
                "'??' cannot be mixed with '&&' or '||' without parentheses", operand.Start);
        }
    }

    private Expression ParseLogicalOr()
    {
        var left = ParseLogicalAnd();
        while (Check(JsTokenKind.PipePipe))
        {
            var op = _current.Kind; Advance();
            var right = ParseLogicalAnd();
            left = new LogicalExpression(op, left, right, left.Start, right.End);
        }
        return left;
    }

    private Expression ParseLogicalAnd()
    {
        var left = ParseBitwiseOr();
        while (Check(JsTokenKind.AmpAmp))
        {
            var op = _current.Kind; Advance();
            var right = ParseBitwiseOr();
            left = new LogicalExpression(op, left, right, left.Start, right.End);
        }
        return left;
    }

    private Expression ParseBitwiseOr()
    {
        var left = ParseBitwiseXor();
        while (_current.Kind == JsTokenKind.Pipe)
        {
            var t = Advance();
            var right = ParseBitwiseXor();
            left = new BinaryExpression(t.Kind, left, right, left.Start, right.End);
        }
        return left;
    }

    private Expression ParseBitwiseXor()
    {
        var left = ParseBitwiseAnd();
        while (_current.Kind == JsTokenKind.Caret)
        {
            var t = Advance();
            var right = ParseBitwiseAnd();
            left = new BinaryExpression(t.Kind, left, right, left.Start, right.End);
        }
        return left;
    }

    private Expression ParseBitwiseAnd()
    {
        var left = ParseEquality();
        while (_current.Kind == JsTokenKind.Amp)
        {
            var t = Advance();
            var right = ParseEquality();
            left = new BinaryExpression(t.Kind, left, right, left.Start, right.End);
        }
        return left;
    }

    private Expression ParseEquality()
    {
        var left = ParseRelational();
        while (_current.Kind is JsTokenKind.EqEq or JsTokenKind.BangEq
                                or JsTokenKind.EqEqEq or JsTokenKind.BangEqEq)
        {
            var op = _current.Kind; Advance();
            var right = ParseRelational();
            left = new BinaryExpression(op, left, right, left.Start, right.End);
        }
        return left;
    }

    private Expression ParseRelational()
    {
        // §13.10.1 RelationalExpression : PrivateIdentifier `in` ShiftExpression
        // — the ergonomic-brand-check form `#x in obj`. A PrivateIdentifier here
        // is ONLY legal as the left operand of `in`; any other use is an error.
        if (_current.Kind == JsTokenKind.PrivateIdentifier
            && _lex.Peek().Kind == JsTokenKind.In && _disallowInDepth == 0)
        {
            var pt = Advance();          // PrivateIdentifier
            var name = PrivateNameOf(pt);
            Advance();                   // 'in'
            var right = ParseShift();
            return new PrivateInExpression(name, right, pt.Start, right.End);
        }
        return ParseRelationalTail(ParseShift());
    }

    private Expression ParseRelationalTail(Expression left)
    {
        while (_current.Kind is JsTokenKind.Lt or JsTokenKind.Gt
                                or JsTokenKind.LtEq or JsTokenKind.GtEq
                                or JsTokenKind.Instanceof
               || (_current.Kind == JsTokenKind.In && _disallowInDepth == 0))
        {
            var op = _current.Kind; Advance();
            var right = ParseShift();
            left = new BinaryExpression(op, left, right, left.Start, right.End);
        }
        return left;
    }

    private Expression ParseShift()
    {
        var left = ParseAdditive();
        while (_current.Kind is JsTokenKind.LtLt or JsTokenKind.GtGt or JsTokenKind.GtGtGt)
        {
            var op = _current.Kind; Advance();
            var right = ParseAdditive();
            left = new BinaryExpression(op, left, right, left.Start, right.End);
        }
        return left;
    }

    private Expression ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (_current.Kind is JsTokenKind.Plus or JsTokenKind.Minus)
        {
            var op = _current.Kind; Advance();
            var right = ParseMultiplicative();
            left = new BinaryExpression(op, left, right, left.Start, right.End);
        }
        return left;
    }

    private Expression ParseMultiplicative()
    {
        var left = ParseExponentiation();
        while (_current.Kind is JsTokenKind.Star or JsTokenKind.Slash or JsTokenKind.Percent)
        {
            var op = _current.Kind; Advance();
            var right = ParseExponentiation();
            left = new BinaryExpression(op, left, right, left.Start, right.End);
        }
        return left;
    }

    // ** is RIGHT-associative per §13.6.
    private Expression ParseExponentiation()
    {
        var left = ParseUnary();
        if (Check(JsTokenKind.StarStar))
        {
            // §13.6 — ExponentiationExpression : UpdateExpression ** Exponentiation.
            // The base must be an UpdateExpression, NOT a UnaryExpression: a
            // prefix unary operator (`-`/`+`/`~`/`!`/`typeof`/`void`/`delete`) or
            // `await` directly before `**` is an early SyntaxError. Prefix/postfix
            // `++`/`--` ARE UpdateExpressions, so they remain valid bases.
            if (left is UnaryExpression or AwaitExpression && !_parenthesized.Contains(left))
            {
                throw new JsParseException(
                    "unary operator used immediately before '**' must be parenthesized", left.Start);
            }

            var op = _current.Kind; Advance();
            var right = ParseExponentiation();
            return new BinaryExpression(op, left, right, left.Start, right.End);
        }
        return left;
    }

    // Unary prefix: typeof, void, delete, !, ~, +, -, ++, --, await.
    private Expression ParseUnary()
    {
        switch (_current.Kind)
        {
            case JsTokenKind.Typeof:
            case JsTokenKind.Void:
            case JsTokenKind.Delete:
            case JsTokenKind.Bang:
            case JsTokenKind.Tilde:
            case JsTokenKind.Plus:
            case JsTokenKind.Minus:
                {
                    var t = Advance();
                    var arg = ParseUnary();
                    // §13.5.1.1 — in strict code `delete` of a bare/parenthesized
                    // unqualified identifier reference is a SyntaxError.
                    if (t.Kind == JsTokenKind.Delete && _strict && IsUnqualifiedReference(arg))
                    {
                        throw new JsParseException(
                            "'delete' of an unqualified identifier is not allowed in strict mode", t.Start);
                    }

                    return new UnaryExpression(t.Kind, arg, Prefix: true, t.Start, arg.End);
                }
            case JsTokenKind.PlusPlus:
            case JsTokenKind.MinusMinus:
                {
                    var t = Advance();
                    var arg = ParseUnary();
                    // §13.4.1 — ++/-- of `eval`/`arguments` is a strict SyntaxError.
                    CheckAssignmentTarget(arg, t.Start);
                    // §13.3.1.1 — an OptionalChain is not a valid assignment target,
                    // so `--a?.b` (prefix update on an optional chain) is an error.
                    if (IsOptionalChain(arg))
                    {
                        throw new JsParseException(
                            "optional chain is not a valid assignment target", arg.Start);
                    }

                    return new UpdateExpression(t.Kind, arg, Prefix: true, t.Start, arg.End);
                }
            case JsTokenKind.Identifier when _current.TextEquals("await") && !_current.ContainsEscape:
                {
                    // B1b-2c — await expression. Treat as a unary prefix in any
                    // context; the runtime errors if used outside an async fn.
                    // (Spec restricts to async contexts; we accept liberally.)
                    // Guard against "await" used as a plain identifier (e.g. as
                    // a property name or assignment target) — those flow through
                    // ParsePrimary which advances the token first.
                    // §13.3.10.1 / §16.2.1.6.2 — but in an await context (async
                    // function OR Module top level) `await` is always the keyword and
                    // may never be used as an identifier reference / assignment target,
                    // so the fall-through is only permitted outside an await context.
                    // An escaped `await` is never the keyword (handled by the
                    // ContainsEscape guard above → falls through to ParsePrimary,
                    // which yields a SyntaxError in await contexts).
                    var next = _lex.Peek().Kind;
                    if (!AwaitIsKeyword && (next == JsTokenKind.Eq || next == JsTokenKind.Comma
                        || next == JsTokenKind.Semicolon || next == JsTokenKind.RParen
                        || next == JsTokenKind.RBrace || next == JsTokenKind.RBracket
                        || next == JsTokenKind.Dot || next == JsTokenKind.Arrow
                        || next == JsTokenKind.LParen))
                    {
                        // `await` used as identifier (legacy). Fall through.
                        return ParseUpdate();
                    }
                    // §15.8.1 — an async function's FormalParameters may not contain
                    // an AwaitExpression (`async function*(x = await 1) {}`).
                    if (_inFormalParameters && _inAsync)
                    {
                        throw new JsParseException(
                            "an AwaitExpression may not appear in formal parameters", _current.Start);
                    }
                    // §15.7.1 — a class static initialization block may not contain
                    // an AwaitExpression (`static { await 0; }`).
                    if (_inStaticBlock)
                    {
                        throw new JsParseException(
                            "an AwaitExpression may not appear in a class static block", _current.Start);
                    }
                    // §16.2.1.6.2 — in module code, `await` is always reserved: an
                    // `await UnaryExpression` outside an await context (i.e. in a
                    // non-async function nested in the module) is an early SyntaxError,
                    // not the liberal "accept and defer to runtime" form classic
                    // scripts use.
                    if (_module && !AwaitIsKeyword)
                    {
                        throw new JsParseException(
                            "'await' is only valid in async functions and at the top level of a module",
                            _current.Start);
                    }

                    var t = Advance();
                    var arg = ParseUnary();
                    return new AwaitExpression(arg, t.Start, arg.End);
                }
        }
        return ParseUpdate();
    }

    // Postfix update: a++, a--. No line terminator allowed between operand
    // and the ++/-- (handled by checking PrecededByLineTerminator).
    private Expression ParseUpdate()
    {
        var arg = ParseLeftHandSide();
        if ((_current.Kind == JsTokenKind.PlusPlus || _current.Kind == JsTokenKind.MinusMinus)
            && !_current.PrecededByLineTerminator)
        {
            var t = Advance();
            // §13.4.1 — postfix ++/-- of `eval`/`arguments` is a strict SyntaxError.
            CheckAssignmentTarget(arg, t.Start);
            // §13.3.1.1 — an OptionalChain is not a valid assignment target,
            // so `a?.b++` (postfix update on an optional chain) is an error.
            if (IsOptionalChain(arg))
            {
                throw new JsParseException(
                    "optional chain is not a valid assignment target", arg.Start);
            }

            return new UpdateExpression(t.Kind, arg, Prefix: false, arg.Start, t.End);
        }
        return arg;
    }

    // LeftHandSide: NewExpression | CallExpression | OptionalExpression.
    private Expression ParseLeftHandSide()
    {
        Expression node;
        if (Check(JsTokenKind.New))
        {
            node = ParseNew();
        }
        else
        {
            node = ParsePrimary();
        }
        return ParseCallAndMemberTail(node);
    }

    private Expression ParseNew()
    {
        var start = _current.Start;
        Advance(); // 'new'
        // §13.3.12 NewTarget — `new.target` meta-property.
        if (Check(JsTokenKind.Dot))
        {
            Advance(); // '.'
            var meta = ExpectIdentifierName("expected 'target' after 'new.'");
            if (!meta.TextEquals("target"))
            {
                throw new JsParseException(
                    $"the only valid meta-property for new is 'new.target' (got 'new.{meta.Lexeme}')",
                    meta.Start);
            }
            // §13.3.12 — `new.target` is an early SyntaxError outside a function.
            if (_functionDepth == 0)
            {
                throw new JsParseException(
                    "'new.target' is only valid inside a function", start);
            }

            return new NewTargetExpression(start, meta.End);
        }
        // Callee can recurse for `new new X()` etc.
        Expression callee = Check(JsTokenKind.New)
            ? ParseNew()
            : ParseCallAndMemberTailNoCall(ParsePrimary());
        // §13.3 — ImportCall is a CallExpression, not a MemberExpression, so it
        // may never be the callee of a NewExpression (`new import(x)` is an early
        // SyntaxError). A leading `import.meta`/member tail off it is fine.
        if (callee is ImportCallExpression)
        {
            throw new JsParseException(
                "'import(...)' (ImportCall) may not be preceded by 'new'", start);
        }

        IReadOnlyList<Expression> args = [];
        var end = callee.End;
        if (Match(JsTokenKind.LParen))
        {
            args = ParseArgumentList();
            end = _current.Start;
            Expect(JsTokenKind.RParen, "expected ')' in new arguments");
        }
        return new NewExpression(callee, args, start, end);
    }

    private Expression ParseCallAndMemberTailNoCall(Expression node)
    {
        // For `new X.Y.Z` we want member accesses but not bare calls
        // (those bind to the surrounding `new`).
        while (true)
        {
            if (Match(JsTokenKind.Dot))
            {
                var prop = ExpectIdentifierName("expected property name after '.'");
                node = new MemberExpression(node,
                    new Identifier(prop.Lexeme, prop.Start, prop.End),
                    Computed: false, Optional: false, node.Start, prop.End);
            }
            else if (Match(JsTokenKind.LBracket))
            {
                var idx = ParseBracketedExpressionAllowingIn();
                var end = _current.Start;
                Expect(JsTokenKind.RBracket, "expected ']' after computed property");
                node = new MemberExpression(node, idx,
                    Computed: true, Optional: false, node.Start, end);
            }
            else
            {
                break;
            }
        }
        return node;
    }

    private Expression ParseCallAndMemberTail(Expression node)
    {
        while (true)
        {
            if (Match(JsTokenKind.Dot))
            {
                // §13.3.2 — private name access: obj.#privateName.
                if (_current.Kind == JsTokenKind.PrivateIdentifier)
                {
                    var pt = Advance();
                    node = new MemberExpression(node,
                        new PrivateNameExpression(PrivateNameOf(pt), pt.Start, pt.End),
                        Computed: false, Optional: false, node.Start, pt.End);
                    continue;
                }
                var prop = ExpectIdentifierName("expected property name after '.'");
                node = new MemberExpression(node,
                    new Identifier(prop.Lexeme, prop.Start, prop.End),
                    Computed: false, Optional: false, node.Start, prop.End);
            }
            else if (Match(JsTokenKind.LBracket))
            {
                var idx = ParseBracketedExpressionAllowingIn();
                var end = _current.Start;
                Expect(JsTokenKind.RBracket, "expected ']' after computed property");
                node = new MemberExpression(node, idx,
                    Computed: true, Optional: false, node.Start, end);
            }
            else if (Check(JsTokenKind.QuestionDot))
            {
                Advance();
                // ?. can be followed by .ident, [expr], (args).
                if (Match(JsTokenKind.LParen))
                {
                    var args = ParseArgumentList();
                    var end = _current.Start;
                    Expect(JsTokenKind.RParen, "expected ')' after optional call args");
                    node = new CallExpression(node, args, Optional: true, node.Start, end);
                }
                else if (Match(JsTokenKind.LBracket))
                {
                    var idx = ParseBracketedExpressionAllowingIn();
                    var end = _current.Start;
                    Expect(JsTokenKind.RBracket, "expected ']' after optional computed access");
                    node = new MemberExpression(node, idx,
                        Computed: true, Optional: true, node.Start, end);
                }
                else if (_current.Kind == JsTokenKind.PrivateIdentifier)
                {
                    // §13.3 — `obj?.#priv`: optional private-member access.
                    var pt = Advance();
                    node = new MemberExpression(node,
                        new PrivateNameExpression(PrivateNameOf(pt), pt.Start, pt.End),
                        Computed: false, Optional: true, node.Start, pt.End);
                }
                else
                {
                    var prop = ExpectIdentifierName("expected property name after '?.'");
                    node = new MemberExpression(node,
                        new Identifier(prop.Lexeme, prop.Start, prop.End),
                        Computed: false, Optional: true, node.Start, prop.End);
                }
            }
            else if (Match(JsTokenKind.LParen))
            {
                var args = ParseArgumentList();
                var end = _current.Start;
                Expect(JsTokenKind.RParen, "expected ')' after call args");
                node = new CallExpression(node, args, Optional: false, node.Start, end);
            }
            else if (Check(JsTokenKind.TemplateNoSubstitution) || Check(JsTokenKind.TemplateHead))
            {
                // §13.3.1.1 — `OptionalChain TemplateLiteral` is an early
                // SyntaxError: a tagged template whose tag is an optional chain
                // (`a?.b`...``) is forbidden (the `?.` and the template would
                // make `??.` ambiguous / the short-circuit semantics ill-defined).
                if (IsOptionalChain(node))
                {
                    throw new JsParseException(
                        "tagged template may not be applied to an optional chain", node.Start);
                }
                // §13.3.11 TaggedTemplate — `tag`...`` binds tighter than any
                // surrounding operator and may itself be re-tagged.
                var quasi = ParseTemplateLiteral();
                node = new TaggedTemplateExpression(node, quasi, node.Start, quasi.End);
            }
            else
            {
                break;
            }
        }
        return node;
    }

    /// <summary>True when <paramref name="expr"/> is (part of) an
    /// OptionalExpression — i.e. any link in its Member/Call chain carries the
    /// optional <c>?.</c> marker. Walks back through Member objects and Call
    /// callees (a parenthesized sub-expression breaks the chain and is therefore
    /// not in <see cref="_parenthesized"/>-tracked nodes here, matching §13.3).</summary>
    private bool IsOptionalChain(Expression expr)
    {
        while (true)
        {
            if (_parenthesized.Contains(expr))
            {
                return false;
            }

            switch (expr)
            {
                case MemberExpression me:
                    if (me.Optional)
                    {
                        return true;
                    }

                    expr = me.Object;
                    break;
                case CallExpression ce:
                    if (ce.Optional)
                    {
                        return true;
                    }

                    expr = ce.Callee;
                    break;
                default:
                    return false;
            }
        }
    }

    /// <summary>Parse an Expression that appears between square brackets (a
    /// computed member key / index). §13.3 uses <c>Expression</c>, which
    /// includes the comma/sequence operator (e.g. <c>a[b, c]</c>), so collect a
    /// SequenceExpression exactly as <see cref="ParseExpression"/> does. Such an
    /// expression is always <c>[+In]</c> per the grammar, so any active
    /// <c>for</c>-header [NoIn] restriction is suspended for its duration.</summary>
    private Expression ParseBracketedExpressionAllowingIn()
    {
        var savedNoIn = _disallowInDepth;
        _disallowInDepth = 0;
        try
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
        finally { _disallowInDepth = savedNoIn; }
    }

    private List<Expression> ParseArgumentList()
    {
        var args = new List<Expression>();
        if (Check(JsTokenKind.RParen))
        {
            return args;
        }

        while (true)
        {
            if (Check(JsTokenKind.Ellipsis))
            {
                var start = _current.Start;
                Advance();
                var inner = ParseAssignment();
                args.Add(new SpreadElement(inner, start, inner.End));
            }
            else
            {
                args.Add(ParseAssignment());
            }
            if (!Match(JsTokenKind.Comma))
            {
                break;
            }
            // §13.3.6 Arguments allows a single trailing comma:
            //   `f(a,)` / `f(a, b,)`. Stop the loop when the comma was trailing.
            if (Check(JsTokenKind.RParen))
            {
                break;
            }
        }
        return args;
    }

    // -----------------------------------------------------------------------
    // Primary expressions
    // -----------------------------------------------------------------------
    private Expression ParsePrimary()
    {
        // A `/` or `/=` token at the start of a primary expression must be the
        // opening of a regex literal — the lexer is intentionally
        // context-free for these punctuators (§11.6 division-vs-regex
        // ambiguity). Stuff the slash back into the lexer's lookahead slot
        // and ask it to rescan the position as a RegExp.
        if (_current.Kind == JsTokenKind.Slash || _current.Kind == JsTokenKind.SlashEq)
        {
            _lex.PushBack(_current);
            _current = _lex.ScanRegExp();
        }
        // `async function …` as a primary expression — reachable as a unary
        // operand (`!async function(){}`), a call callee, etc. ParseAssignment
        // already handles async arrows and a top-level async function
        // expression; mirroring it here makes the async function EXPRESSION
        // valid in every expression position. §15.8 — no LineTerminator is
        // permitted between the `async` keyword and `function`.
        if (_current.Kind == JsTokenKind.Identifier && _current.TextEquals("async")
            && !_current.ContainsEscape)
        {
            var asyncPeek = _lex.Peek();
            if (asyncPeek.Kind == JsTokenKind.Function && !asyncPeek.PrecededByLineTerminator)
            {
                var asyncTok = Advance(); // async
                return ParseFunctionExpression(asyncTok.Start, isAsync: true);
            }
        }
        var t = _current;
        switch (t.Kind)
        {
            case JsTokenKind.NumericLiteral:
                CheckLegacyOctalLiteral(t);
                Advance();
                return new NumericLiteral((double)t.Value!, t.Start, t.End);
            case JsTokenKind.BigIntLiteral:
                Advance();
                return new BigIntLiteral(ParseBigIntToken(t), t.Start, t.End);
            case JsTokenKind.StringLiteral:
                CheckLegacyOctalLiteral(t);
                Advance();
                return new StringLiteral((string)t.Value!, t.Start, t.End);
            case JsTokenKind.RegExpLiteral:
                {
                    Advance();
                    var (pattern, flags) = t.RegExpPatternAndFlags();
                    // §12.9.5 / §22.2.1 — a RegularExpressionLiteral whose pattern or
                    // flags fail to parse is an early SyntaxError (negative phase
                    // "parse"). Validate eagerly here by compiling through the same
                    // RegExp engine the runtime uses, converting any regex-level
                    // syntax failure into a parse error at the literal's position.
                    ValidateRegExpLiteral(pattern, flags, t.Start);
                    return new RegExpLiteral(pattern, flags, t.Start, t.End);
                }
            case JsTokenKind.BooleanLiteral:
                Advance();
                return new BooleanLiteral((bool)t.Value!, t.Start, t.End);
            case JsTokenKind.NullLiteral:
                Advance();
                return new NullLiteral(t.Start, t.End);
            case JsTokenKind.This:
                Advance();
                return new ThisExpression(t.Start, t.End);
            case JsTokenKind.Identifier:
                // §16.2.1.6.2 / §13.3.10.1 — `await` is reserved in module code
                // and in any async context: it may not be an IdentifierReference
                // there (e.g. `new await`, `void await`). The AwaitExpression form
                // is handled in ParseUnary; reaching here with `await` means it is
                // being used as a bare reference, which is illegal.
                if (t.TextEquals("await") && (_module || AwaitIsKeyword))
                {
                    throw new JsParseException(
                        "'await' may not be used as an identifier reference in a module or async context",
                        t.Start);
                }

                Advance();
                return new Identifier(t.Lexeme, t.Start, t.End);
            case JsTokenKind.Yield:
                // §12.7.1 — `yield` is a reserved word inside a generator body
                // (it can only appear as the YieldExpression handled by
                // ParseAssignment, never as an IdentifierReference reachable here
                // e.g. `void yield`) and in strict code generally. In sloppy
                // non-generator code it is a legal IdentifierReference.
                if (_inGenerator)
                {
                    throw new JsParseException(
                        "'yield' may not be used as an identifier reference in a generator", t.Start);
                }

                if (_strict)
                {
                    throw new JsParseException(
                        "'yield' is a reserved word and may not be used as an identifier in strict mode",
                        t.Start);
                }

                Advance();
                return new Identifier(t.Lexeme, t.Start, t.End);
            case JsTokenKind.LParen:
                Advance();
                var inner = ParseAssignment();
                // Allow comma to form sequence inside parens.
                if (Match(JsTokenKind.Comma))
                {
                    var parts = new List<Expression> { inner };
                    do { parts.Add(ParseAssignment()); }
                    while (Match(JsTokenKind.Comma));
                    Expect(JsTokenKind.RParen, "expected ')' to close grouping");
                    var seq = new SequenceExpression(parts, parts[0].Start, parts[^1].End);
                    _parenthesized.Add(seq);
                    seq.IsParenthesized = true;
                    return seq;
                }
                Expect(JsTokenKind.RParen, "expected ')' to close grouping");
                // §13.6 — record that this expression was parenthesized so a
                // grouped unary base of `**` (e.g. `(-1) ** 2`) is accepted while
                // a bare one (`-1 ** 2`) is rejected as an early error.
                _parenthesized.Add(inner);
                inner.IsParenthesized = true;
                return inner;
            case JsTokenKind.LBracket:
                return ParseArrayLiteral();
            case JsTokenKind.LBrace:
                return ParseObjectLiteral();
            case JsTokenKind.Function:
                return ParseFunctionExpression();
            case JsTokenKind.Class:
                return ParseClassExpression();
            case JsTokenKind.Super:
                return ParseSuperExpression();
            case JsTokenKind.TemplateNoSubstitution:
            case JsTokenKind.TemplateHead:
                // An untagged template literal must not contain an invalid escape.
                return RejectUncookableTemplate(ParseTemplateLiteral());
            case JsTokenKind.Import:
                // wp:M3-03c — `import(...)` (dynamic import call) and
                // `import.meta` (meta-property) are the only expression-context
                // forms of `import`. Static `import …` declarations are routed at
                // program scope by ParseProgramStatement and never reach here.
                return ParseImportExpression();
        }
        throw new JsParseException(
            $"unexpected token {t.Kind} '{t.Lexeme}'", t.Start);
    }

    /// <summary>§12.9.5 — eagerly validate a RegularExpressionLiteral's flags
    /// and pattern so that a malformed literal is reported as a parse-phase
    /// SyntaxError (matching test262 <c>negative: phase: parse</c>). Compiles
    /// through the runtime RegExp engine and translates any regex syntax failure
    /// into a <see cref="JsParseException"/> at the literal's position.</summary>
    private static void ValidateRegExpLiteral(string pattern, string flags, JsPosition start)
    {
        if (!RegExp.RegexFlagParser.TryParse(flags, out var f, out var flagErr))
        {
            throw new JsParseException(flagErr ?? "invalid regular expression flags", start);
        }

        try
        {
            RegExp.CompiledRegex.Compile(pattern, f);
        }
        catch (RegExp.RegexSyntaxException ex)
        {
            throw new JsParseException("invalid regular expression: " + ex.Message, start);
        }
    }

    private TemplateLiteral ParseTemplateLiteral()
    {
        var startTok = _current;
        var quasis = new List<string?>();
        var raws = new List<string>();
        var expressions = new List<Expression>();
        if (_current.Kind == JsTokenKind.TemplateNoSubstitution)
        {
            quasis.Add(CookedValue(_current));
            raws.Add(TemplateRaw(_current));
            var end = _current.End;
            Advance();
            return new TemplateLiteral(quasis, expressions, raws, startTok.Start, end);
        }
        // Head ... (expr ... Middle)* expr ... Tail
        quasis.Add(CookedValue(_current));
        raws.Add(TemplateRaw(_current));
        Advance();
        while (true)
        {
            expressions.Add(ParseAssignment());
            // The substitution must close on `}`; we consume it then re-enter
            // template-mode via the lexer's continuation entry point.
            if (_current.Kind != JsTokenKind.RBrace)
            {
                throw new JsParseException(
                    $"expected '}}' to close template substitution, got {_current.Kind}", _current.Start);
            }
            // Reset the parser's lookahead to the post-} character, then ask
            // the lexer for the next template segment instead of a normal token.
            _current = _lex.ScanTemplateContinuation();
            if (_current.Kind == JsTokenKind.TemplateTail)
            {
                quasis.Add(CookedValue(_current));
                raws.Add(TemplateRaw(_current));
                var endTok = _current;
                Advance();
                return new TemplateLiteral(quasis, expressions, raws, startTok.Start, endTok.End);
            }
            if (_current.Kind == JsTokenKind.TemplateMiddle)
            {
                quasis.Add(CookedValue(_current));
                raws.Add(TemplateRaw(_current));
                Advance();
                continue;
            }
            throw new JsParseException(
                $"expected template middle or tail, got {_current.Kind}", _current.Start);
        }
    }

    /// <summary>
    /// §12.9.6.1 Template Raw Value — the un-cooked source text of one template
    /// segment, used for a tagged template's <c>strings.raw</c>. The lexeme is
    /// the raw source slice including its closing delimiter (<c>`</c> for
    /// NoSubstitution/Tail, <c>${</c> for Head/Middle), so we strip that, then
    /// normalise <c>&lt;CR&gt;&lt;LF&gt;</c> and lone <c>&lt;CR&gt;</c> to <c>&lt;LF&gt;</c>.
    /// </summary>
    /// <summary>§12.9.6 — the cooked value of a template segment, or <c>null</c>
    /// when the segment held an invalid escape sequence (a NotEscapeSequence).
    /// A null cooked element is legal only in a tagged template (where it maps to
    /// <c>undefined</c>); an untagged literal with a null cooked element is a
    /// SyntaxError, rejected by <see cref="RejectUncookableTemplate"/>.</summary>
    private static string? CookedValue(in JsToken t)
        => t.InvalidEscape ? null : (string)t.Value!;

    /// <summary>Reject an untagged template literal that contains an invalid
    /// escape sequence (§12.9.6 — only tagged templates may carry an undefined
    /// cooked element).</summary>
    private static TemplateLiteral RejectUncookableTemplate(TemplateLiteral tpl)
    {
        for (var i = 0; i < tpl.Quasis.Count; i++)
        {
            if (tpl.Quasis[i] is null)
            {
                throw new JsParseException(
                    "invalid escape sequence in untagged template literal", tpl.Start);
            }
        }
        return tpl;
    }

    private static string TemplateRaw(in JsToken t)
    {
        var lex = t.LexemeSpan;
        var trim = t.Kind is JsTokenKind.TemplateHead or JsTokenKind.TemplateMiddle ? 2 : 1;
        var raw = trim <= lex.Length ? lex[..^trim] : lex;
        if (raw.IndexOf('\r') < 0)
        {
            return raw.ToString();
        }

        return raw.ToString().Replace("\r\n", "\n").Replace('\r', '\n');
    }

    private ArrayExpression ParseArrayLiteral()
    {
        var start = _current.Start;
        Expect(JsTokenKind.LBracket, "[ expected");
        // Array element list is `AssignmentExpression[+In]` — suspend any active
        // `for`-header [NoIn] restriction inside the brackets.
        var savedNoIn = _disallowInDepth;
        _disallowInDepth = 0;
        try
        {
            var elements = new List<Expression?>();
            while (!Check(JsTokenKind.RBracket))
            {
                if (Check(JsTokenKind.Comma))
                {
                    elements.Add(null); // elision
                    Advance();
                    continue;
                }
                SpreadElement? spread = null;
                if (Check(JsTokenKind.Ellipsis))
                {
                    var sstart = _current.Start;
                    Advance();
                    var inner = ParseAssignment();
                    spread = new SpreadElement(inner, sstart, inner.End);
                    elements.Add(spread);
                }
                else
                {
                    elements.Add(ParseAssignment());
                }
                if (!Match(JsTokenKind.Comma))
                {
                    break;
                }
                // Record a spread that was directly followed by a comma so the
                // destructuring reinterpret pass can reject `[...x,]` / `[...x, y]`.
                if (spread is not null)
                {
                    _spreadFollowedByComma.Add(spread);
                }
            }
            var end = _current.End;
            Expect(JsTokenKind.RBracket, "expected ']' to close array literal");
            return new ArrayExpression(elements, start, end);
        }
        finally { _disallowInDepth = savedNoIn; }
    }

    private ObjectExpression ParseObjectLiteral()
    {
        var start = _current.Start;
        Expect(JsTokenKind.LBrace, "{ expected");
        var props = new List<ObjectProperty>();
        bool sawProtoData = false;
        while (!Check(JsTokenKind.RBrace))
        {
            // Object spread: { ...other }
            if (Check(JsTokenKind.Ellipsis))
            {
                var sstart = _current.Start;
                Advance();
                var inner = ParseAssignment();
                var spreadKey = new Identifier("", sstart, sstart);  // sentinel for spread
                var spreadElem = new SpreadElement(inner, sstart, inner.End);
                props.Add(new ObjectProperty(spreadKey, spreadElem,
                    Shorthand: false, Computed: false, sstart, inner.End));
            }
            else
            {
                var prop = ParseObjectProperty();
                if (_lastPropertyWasProtoData)
                {
                    if (sawProtoData)
                    {
                        throw new JsParseException(
                            "duplicate __proto__ fields are not allowed in object literals",
                            prop.Start);
                    }

                    sawProtoData = true;
                }
                props.Add(prop);
            }
            if (!Match(JsTokenKind.Comma))
            {
                break;
            }
        }
        var end = _current.End;
        Expect(JsTokenKind.RBrace, "expected '}' to close object literal");
        var objExpr = new ObjectExpression(props, start, end);
        // §13.2.5.1 — a CoverInitializedName (`{ a = 1 }`) is only valid when the
        // object is later reinterpreted as a destructuring pattern. Record it so
        // an unreinterpreted (value) use is rejected at the end of the parse.
        foreach (var p in props)
        {
            if (p.Shorthand && p.Value is AssignmentExpression { Op: JsTokenKind.Eq })
            {
                _coverInitObjects.Add(objExpr);
                break;
            }
        }

        return objExpr;
    }

    /// <summary>Set by <see cref="ParseObjectProperty"/> when the just-parsed
    /// property was a <c>__proto__ : value</c> data property (B.3.1). Read and
    /// reset by <see cref="ParseObjectLiteral"/> to enforce the at-most-one rule.</summary>
    private bool _lastPropertyWasProtoData;

    private ObjectProperty ParseObjectProperty()
    {
        var start = _current.Start;
        _lastPropertyWasProtoData = false;

        // Generator / async / async-generator method shorthand (ES2024 §13.2.5):
        //   { *gen(){} }, { async m(){} }, { async *gen(){} }
        // `async` is contextual: it introduces an async method ONLY when
        // followed (on the same line) by a property-name start. Otherwise it is
        // an ordinary key — `{ async }`, `{ async: 1 }`, `{ async() {} }`.
        bool mGenerator = false, mAsync = false;
        if (_current.Kind == JsTokenKind.Identifier && !_current.ContainsEscape && _current.TextEquals("async"))
        {
            var peek = _lex.Peek();
            if (!peek.PrecededByLineTerminator && IsMethodNameStartAfterModifier(peek.Kind))
            {
                Advance(); // 'async'
                mAsync = true;
            }
        }
        if (Check(JsTokenKind.Star)) { Advance(); mGenerator = true; }
        if (mAsync || mGenerator)
        {
            var (mkey, mcomputed) = ParsePropertyKey();
            var (mparams, mbody, mend, mstrict) = ParseMethodTail(isAsync: mAsync, isGenerator: mGenerator);
            var mname = mkey is Identifier mki ? mki : null;
            var mfn = MakeFnExpression(mname, mparams, mbody, start, mend, mGenerator, mAsync, mstrict);
            return new ObjectProperty(mkey, mfn,
                Shorthand: false, Computed: mcomputed, start, mend, IsMethod: true);
        }

        // wp:M3-26 — accessor (getter/setter) shorthand: `{ get x(){…} }`,
        // `{ set x(v){…} }` (ECMA-262 §13.2.5). `get`/`set` are contextual:
        // they introduce an accessor ONLY when followed by a property name
        // (identifier / reserved word / string / number / computed `[`).
        // Otherwise they are an ordinary key: `{ get: 1 }` (data property
        // named "get") or `{ get(){} }` (method named "get"). Mirrors the
        // class-body disambiguation in ParseClassMember. An ESCAPED `get`/`set`
        // (`get x(){}`) is NOT the contextual accessor keyword (§12.7.2), so
        // it never introduces an accessor — `get`/`set` here must be unescaped.
        if (_current.Kind == JsTokenKind.Identifier && !_current.ContainsEscape
            && (_current.TextEquals("get") || _current.TextEquals("set")))
        {
            var peek = _lex.Peek();
            if (IsAccessorPropertyNameStart(peek.Kind))
            {
                var kind = _current.TextEquals("get") ? MethodKind.Get : MethodKind.Set;
                Advance(); // consume 'get' / 'set'
                var (akey, acomputed) = ParsePropertyKey();
                var (parameters, body, endPos, astrict) = ParseMethodTail();
                // §15.4.1 well-formedness: getter takes 0 params, setter exactly
                // one non-rest param.
                CheckAccessorParams(kind, parameters, start);
                var fn = MakeFnExpression(name: null, parameters, body, start, endPos, strict: astrict);
                return new ObjectProperty(akey, fn,
                    Shorthand: false, Computed: acomputed, start, endPos, kind, IsMethod: true);
            }
        }

        // Snapshot the key token so the shorthand forms below can tell a plain
        // IdentifierReference from a reserved word used as an IdentifierName
        // (e.g. `{ if }`): the latter is a legal property KEY but never a legal
        // shorthand VALUE / assignment-pattern target (§13.2.5.1 / §13.15.1).
        var keyToken = _current;
        var (key, computed) = ParsePropertyKey();

        // Method shorthand: { foo() { … }, [bar](x) { … } }
        if (Check(JsTokenKind.LParen))
        {
            var (parameters, body, endPos, mstrict) = ParseMethodTail();
            var methodName = key is Identifier ki ? ki : null;
            var method = MakeFnExpression(methodName, parameters, body, start, endPos, strict: mstrict);
            return new ObjectProperty(key, method,
                Shorthand: false, Computed: computed, start, endPos, IsMethod: true);
        }

        if (Match(JsTokenKind.Colon))
        {
            var value = ParseAssignment();
            // B.3.1 — a `__proto__ : value` data property (literal, non-computed
            // key) sets the prototype; more than one in an object literal is an
            // early SyntaxError. Flag this colon-form so the literal can dedup.
            _lastPropertyWasProtoData = !computed && IsProtoKey(key);
            return new ObjectProperty(key, value,
                Shorthand: false, Computed: computed, start, value.End);
        }
        // Shorthand binding default: { foo = expr }. This is only valid when
        // the object literal is later reinterpreted as a destructuring pattern
        // (ECMA-262 §13.15 / §14.3.3), but accepting the cover form here keeps
        // assignment and binding-pattern parsing single-pass.
        if (!computed && key is Identifier id && Match(JsTokenKind.Eq))
        {
            CheckShorthandIdentifier(keyToken);
            var fallback = ParseAssignment();
            var target = new AssignmentExpression(JsTokenKind.Eq, id, fallback, id.Start, fallback.End);
            return new ObjectProperty(key, target,
                Shorthand: true, Computed: false, start, fallback.End);
        }
        // Shorthand: { foo } where foo is an identifier.
        if (!computed && key is Identifier id2)
        {
            CheckShorthandIdentifier(keyToken);
            return new ObjectProperty(key, id2,
                Shorthand: true, Computed: false, start, key.End);
        }
        throw new JsParseException(
            $"expected ':' or '(' after object property key", _current.Start);
    }

    /// <summary>wp:M3-26 — parse a single object-literal property key:
    /// computed <c>[expr]</c>, string, numeric, identifier, or reserved word.
    /// Returns the key node and whether it was computed. Shared by the data /
    /// method / accessor property forms.</summary>
    /// <summary>True when a non-computed property key denotes the literal name
    /// <c>__proto__</c> — an IdentifierName or a StringLiteral. Numeric keys and
    /// computed keys never qualify (B.3.1).</summary>
    private static bool IsProtoKey(Expression key) => key switch
    {
        Identifier { Name: "__proto__" } => true,
        StringLiteral { Value: "__proto__" } => true,
        _ => false,
    };

    private (Expression Key, bool Computed) ParsePropertyKey()
    {
        if (Match(JsTokenKind.LBracket))
        {
            // A computed key is `[ AssignmentExpression[+In] ]` — `in` is always
            // allowed inside the brackets even within a `for` header [NoIn].
            var savedNoIn = _disallowInDepth;
            _disallowInDepth = 0;
            Expression k;
            try { k = ParseAssignment(); }
            finally { _disallowInDepth = savedNoIn; }
            Expect(JsTokenKind.RBracket, "expected ']' after computed key");
            return (k, true);
        }
        if (_current.Kind == JsTokenKind.StringLiteral)
        {
            var t = Advance();
            return (new StringLiteral((string)t.Value!, t.Start, t.End), false);
        }
        if (_current.Kind == JsTokenKind.NumericLiteral)
        {
            var t = Advance();
            return (new NumericLiteral((double)t.Value!, t.Start, t.End), false);
        }
        if (_current.Kind == JsTokenKind.Identifier
            || IsReservedNameAllowedAsPropertyName(_current.Kind))
        {
            var t = Advance();
            return (new Identifier(t.Lexeme, t.Start, t.End), false);
        }
        throw new JsParseException(
            $"expected property name, got {_current.Kind}", _current.Start);
    }

    /// <summary>wp:M3-26 — true when <paramref name="kind"/> can begin a
    /// property name following a contextual <c>get</c>/<c>set</c>, marking an
    /// accessor rather than a data property or method named "get"/"set".</summary>
    private bool IsAccessorPropertyNameStart(JsTokenKind kind)
        => kind == JsTokenKind.Identifier
        || kind == JsTokenKind.StringLiteral
        || kind == JsTokenKind.NumericLiteral
        || kind == JsTokenKind.LBracket
        || IsReservedNameAllowedAsPropertyName(kind);

    private static FunctionExpression MakeFnExpression(
        Identifier? name, IReadOnlyList<Expression> @params, BlockStatement body,
        JsPosition start, JsPosition end,
        bool generator = false, bool async = false, bool strict = false)
        => new(name, @params, body, generator, start, end, Async: async, Strict: strict);

    /// <summary>True when <paramref name="kind"/> can begin a method/property
    /// name immediately after an <c>async</c> or <c>*</c> method modifier
    /// (ES2024 §13.2.5 / §15.4) — identifier, reserved word, string, number,
    /// computed <c>[</c>, generator <c>*</c>, or a <c>#private</c> name.</summary>
    private bool IsMethodNameStartAfterModifier(JsTokenKind kind)
        => kind == JsTokenKind.Identifier
        || kind == JsTokenKind.StringLiteral
        || kind == JsTokenKind.NumericLiteral
        || kind == JsTokenKind.LBracket
        || kind == JsTokenKind.Star
        || kind == JsTokenKind.PrivateIdentifier
        || IsReservedNameAllowedAsPropertyName(kind);

    /// <summary>Parse the <c>(params) { body }</c> portion of an object-literal
    /// method shorthand, having already consumed the property key.
    /// Delegates to <see cref="ParseParameterList"/> so that a trailing
    /// <c>...rest</c> element is accepted (wp:M3-27).</summary>
    private (List<Expression> Params, BlockStatement Body, JsPosition End, bool Strict) ParseMethodTail(
        bool isAsync = false, bool isGenerator = false)
    {
        var savedStrict = _strict;
        var (savedAsync, savedGen) = (_inAsync, _inGenerator);
        var savedModuleAwait = _moduleTopAwait;
        // §13.3.7.1 — an object-literal method (incl. accessors) has a
        // [[HomeObject]], so a SuperProperty in its body is legal.
        var savedSuper = _superPropertyDepth;
        try
        {
            // §15 — the method's own async/generator modifiers establish the
            // await/yield context for its parameter list and body. A method body
            // is never the Module's [+Await] top level.
            _inAsync = isAsync;
            _inGenerator = isGenerator;
            _moduleTopAwait = false;
            _superPropertyDepth = savedSuper + 1;
            Expect(JsTokenKind.LParen, "expected '(' for method parameters");
            var parameters = ParseParameterList();
            Expect(JsTokenKind.RParen, "expected ')' after method parameters");
            var bodyStart = _current.Start;
            var (body, strict) = ParseFunctionBody();
            CheckUseStrictSimpleParams(parameters, bodyStart);
            // §15.4.1 — a MethodDefinition has UniqueFormalParameters: duplicate
            // parameter names are always an error (no Annex B sloppy exception).
            ValidateParameters(parameters, strict, forceDuplicateCheck: true);
            CheckParamsVsLexicalBody(parameters, body);
            return (parameters, body, body.End, strict);
        }
        finally
        {
            _strict = savedStrict;
            (_inAsync, _inGenerator) = (savedAsync, savedGen);
            _moduleTopAwait = savedModuleAwait;
            _superPropertyDepth = savedSuper;
        }
    }

    /// <summary>
    /// Consume any token that is a valid <c>IdentifierName</c> per ES §12.6 —
    /// i.e. a plain identifier or any reserved word/boolean/null literal.
    /// Used after <c>.</c> and <c>?.</c> in MemberExpression, where the
    /// grammar production is <c>MemberExpression . IdentifierName</c> (not
    /// <c>Identifier</c>), so reserved words like <c>catch</c>, <c>finally</c>,
    /// <c>default</c>, <c>class</c>, <c>with</c> are valid property names.
    /// </summary>
    private JsToken ExpectIdentifierName(string message)
    {
        if (_current.Kind == JsTokenKind.Identifier
            || IsReservedNameAllowedAsPropertyName(_current.Kind))
        {
            return Advance();
        }
        throw new JsParseException($"{message} (got {_current.Kind})", _current.Start);
    }

    private static bool IsReservedNameAllowedAsPropertyName(JsTokenKind k)
    {
        // Any reserved word is allowed as an object property name.
        return k is
            JsTokenKind.Break or JsTokenKind.Case or JsTokenKind.Catch or JsTokenKind.Class
            or JsTokenKind.Const or JsTokenKind.Continue or JsTokenKind.Debugger
            or JsTokenKind.Default or JsTokenKind.Delete or JsTokenKind.Do
            or JsTokenKind.Else or JsTokenKind.Enum or JsTokenKind.Export
            or JsTokenKind.Extends or JsTokenKind.Finally or JsTokenKind.For
            or JsTokenKind.Function or JsTokenKind.If or JsTokenKind.Import
            or JsTokenKind.In or JsTokenKind.Instanceof or JsTokenKind.New
            or JsTokenKind.Return or JsTokenKind.Super or JsTokenKind.Switch
            or JsTokenKind.This or JsTokenKind.Throw or JsTokenKind.Try
            or JsTokenKind.Typeof or JsTokenKind.Var or JsTokenKind.Void
            or JsTokenKind.While or JsTokenKind.With or JsTokenKind.Yield
            or JsTokenKind.BooleanLiteral or JsTokenKind.NullLiteral;
    }

    /// <summary>§13.2.5.1 / §13.15.1 — a shorthand property's value
    /// (<c>{ x }</c>) and an assignment-pattern shorthand target are an
    /// IdentifierReference, which must NOT be a ReservedWord. A reserved word
    /// is therefore a legal property KEY but an illegal shorthand. An escaped
    /// reserved word (<c>{ if }</c>) is likewise illegal — the escape
    /// does not make the word usable as an IdentifierReference (§12.7.2). The
    /// contextual keyword <c>yield</c> is permitted by the cover grammar here
    /// (sloppy-mode IdentifierReference); strict / generator contexts reject it
    /// elsewhere. Throws a SyntaxError when the snapshot key token is illegal.</summary>
    private void CheckShorthandIdentifier(JsToken keyToken)
    {
        // §13.2.5.1 / §12.7.1 — `yield` shorthand (`{ yield }`) is a valid
        // IdentifierReference only in sloppy non-generator code. Inside a
        // generator it is the YieldExpression keyword, and in strict code it is a
        // reserved word — so reject it as a shorthand there.
        if (keyToken.Kind == JsTokenKind.Yield && (_inGenerator || _strict))
        {
            throw new JsParseException(
                "'yield' may not be used as a shorthand property here", keyToken.Start);
        }
        // §13.2.5.1 / §13.3.10.1 — in an async context `await` is the
        // AwaitExpression keyword and is not a valid shorthand IdentifierReference
        // (`({ await })` inside an async function / class static block).
        if (keyToken.Kind == JsTokenKind.Identifier && keyToken.TextEquals("await") && _inAsync)
        {
            throw new JsParseException(
                "'await' may not be used as a shorthand property in an async context", keyToken.Start);
        }
        // A genuine reserved-keyword token (other than the contextually-allowed
        // `yield`) is never a valid IdentifierReference.
        if (keyToken.Kind != JsTokenKind.Identifier
            && keyToken.Kind != JsTokenKind.Yield
            && IsReservedNameAllowedAsPropertyName(keyToken.Kind))
        {
            throw new JsParseException(
                $"'{keyToken.Lexeme}' is a reserved word and cannot be used as a shorthand property", keyToken.Start);
        }
        // An escaped reserved word cannot serve as an IdentifierReference even
        // though it keeps its keyword kind for IdentifierName positions.
        if (keyToken.ContainsEscape && keyToken.Kind != JsTokenKind.Identifier)
        {
            throw new JsParseException(
                $"escaped reserved word '{keyToken.Lexeme}' cannot be used as a shorthand property", keyToken.Start);
        }
        // §12.7.2 / §13.2.5.1 — in strict code the strict FutureReservedWords
        // (`let`, `static`, `implements`, `interface`, `package`, `private`,
        // `protected`, `public`, `yield`) are reserved, so a shorthand using one
        // as an IdentifierReference / binding target is an illegal
        // IdentifierReference — whether escaped or not (`({ let })` /
        // `({ let })` under "use strict").
        if (_strict
            && keyToken.Kind == JsTokenKind.Identifier
            && IsStrictReservedWord(keyToken.Lexeme))
        {
            throw new JsParseException(
                $"'{keyToken.Lexeme}' is a reserved word and cannot be used as a shorthand property in strict mode", keyToken.Start);
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    /// <summary>B4-3 — decode a BigInt lexeme into a
    /// <see cref="System.Numerics.BigInteger"/>. The lexeme is the raw source
    /// slice, including any radix prefix and the trailing <c>n</c>.</summary>
    private static System.Numerics.BigInteger ParseBigIntToken(in JsToken token)
    {
        var lexeme = token.LexemeSpan;
        var digits = token.BigIntDigits;
        if (lexeme.Length > 2 && lexeme[0] == '0')
        {
            var p = lexeme[1];
            if (p == 'x' || p == 'X')
            {
                return System.Numerics.BigInteger.Parse("0" + digits,
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            if (p == 'b' || p == 'B')
            {
                return ParseRadixBigInt(digits, 2, token.Start);
            }

            if (p == 'o' || p == 'O')
            {
                return ParseRadixBigInt(digits, 8, token.Start);
            }
        }
        return System.Numerics.BigInteger.Parse(digits,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static System.Numerics.BigInteger ParseBigIntLexeme(string lexeme, JsPosition pos)
    {
        if (lexeme.Length > 0 && lexeme[^1] == 'n')
        {
            lexeme = lexeme[..^1];
        }
        // Numeric separators (`1_000n`, `0xA_Bn`) are validated for placement by
        // the lexer; strip them before BigInteger conversion. They never sit at
        // the prefix boundary, so the `0x`/`0b`/`0o` checks below stay correct.
        if (lexeme.IndexOf('_') >= 0)
        {
            lexeme = lexeme.Replace("_", "");
        }

        if (lexeme.Length > 2 && lexeme[0] == '0')
        {
            var p = lexeme[1];
            if (p == 'x' || p == 'X')
            {
                // Prefix '0' so BigInteger.Parse(HexNumber) treats the value
                // as non-negative — a leading hex digit ≥ 8 would otherwise
                // sign-extend (e.g. "FF" → -1).
                return System.Numerics.BigInteger.Parse("0" + lexeme[2..],
                    System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture);
            }

            if (p == 'b' || p == 'B')
            {
                return ParseRadixBigInt(lexeme[2..], 2, pos);
            }

            if (p == 'o' || p == 'O')
            {
                return ParseRadixBigInt(lexeme[2..], 8, pos);
            }
        }
        return System.Numerics.BigInteger.Parse(lexeme,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static System.Numerics.BigInteger ParseRadixBigInt(string digits, int radix, JsPosition pos)
    {
        var v = System.Numerics.BigInteger.Zero;
        foreach (var c in digits)
        {
            var d = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
            if (d < 0 || d >= radix)
            {
                throw new JsParseException($"invalid digit '{c}' in base-{radix} BigInt literal", pos);
            }

            v = v * radix + d;
        }
        return v;
    }

    private Expression ParseLeftAssoc(Func<Expression> next, JsTokenKind op)
    {
        var left = next();
        while (_current.Kind == op)
        {
            var t = Advance();
            var right = next();
            left = new BinaryExpression(t.Kind, left, right, left.Start, right.End);
        }
        return left;
    }
}

#pragma warning disable RCS1194 // Implement exception constructors — we
// surface only the single (message, position)
// form; the parser never throws bare instances.
public sealed class JsParseException(string message, JsPosition position)
    : Exception($"{message} (at {position})")
{
    public JsPosition Position { get; } = position;
}
#pragma warning restore RCS1194
