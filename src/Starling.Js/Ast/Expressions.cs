using Starling.Js.Lex;

namespace Starling.Js.Ast;

/// <summary>
/// Base for every JS expression node. ES2024 §13.
/// </summary>
public abstract record Expression(JsPosition Start, JsPosition End) : AstNode(Start, End);

// -----------------------------------------------------------------------
// Literals + identifier + this
// -----------------------------------------------------------------------

public sealed record NumericLiteral(double Value, JsPosition Start, JsPosition End)
    : Expression(Start, End);

/// <summary>ES2024 §13.2.4 — a numeric literal followed by <c>n</c>. The
/// parser parses the lexeme (decimal / 0x / 0b / 0o, with the trailing
/// <c>n</c> stripped) into a <see cref="System.Numerics.BigInteger"/> so the
/// compiler stamps the constant pool with the value directly.</summary>
public sealed record BigIntLiteral(System.Numerics.BigInteger Value, JsPosition Start, JsPosition End)
    : Expression(Start, End)
{
    /// <summary>Back-compat — decimal-string view of <see cref="Value"/>.</summary>
    public string Digits => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record StringLiteral(string Value, JsPosition Start, JsPosition End)
    : Expression(Start, End);

public sealed record BooleanLiteral(bool Value, JsPosition Start, JsPosition End)
    : Expression(Start, End);

public sealed record NullLiteral(JsPosition Start, JsPosition End)
    : Expression(Start, End);

/// <summary>
/// ES2024 §13.2.7 regex literal — <c>/pattern/flags</c>. Each evaluation
/// produces a fresh <c>RegExp</c> instance (per-instance <c>lastIndex</c>),
/// so the compiler emits a runtime construction op rather than a constant.
/// </summary>
public sealed record RegExpLiteral(string Source, string Flags, JsPosition Start, JsPosition End)
    : Expression(Start, End);

public sealed record Identifier(string Name, JsPosition Start, JsPosition End)
    : Expression(Start, End);

public sealed record ThisExpression(JsPosition Start, JsPosition End)
    : Expression(Start, End);

/// <summary>§13.3.12 NewTarget: the <c>new.target</c> meta-property. Evaluates
/// to the current execution context's [[NewTarget]] (the constructor invoked
/// via <c>new</c>, or <c>undefined</c> for an ordinary call).</summary>
public sealed record NewTargetExpression(JsPosition Start, JsPosition End)
    : Expression(Start, End);

// -----------------------------------------------------------------------
// Aggregate literals
// -----------------------------------------------------------------------

public sealed record ArrayExpression(
    IReadOnlyList<Expression?> Elements,  // null = elision (hole)
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

public sealed record ObjectExpression(
    IReadOnlyList<ObjectProperty> Properties,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

public sealed record ObjectProperty(
    Expression Key,         // Identifier, StringLiteral, or NumericLiteral
    Expression Value,
    bool Shorthand,         // { foo } instead of { foo: foo }
    bool Computed,          // { [key]: v }
    JsPosition Start, JsPosition End,
    // wp:M3-26 — accessor (getter/setter) shorthand in object literals
    // (ECMA-262 §13.2.5). MethodKind.Method = data/method property (default,
    // back-compat); Get/Set = accessor whose Value is the accessor function.
    MethodKind Kind = MethodKind.Method,
    // wp:M3-64 — true when this property is a MethodDefinition (a concise
    // method `{ foo() {} }`, getter, or setter) rather than a data property
    // whose value happens to be a function (`{ foo: function(){} }`). Only
    // MethodDefinitions get a [[HomeObject]] (the object being constructed)
    // so `super.x` inside them resolves against the object's prototype
    // (§13.2.5 / §15.4.4 MethodDefinitionEvaluation, MakeMethod).
    bool IsMethod = false)
    : AstNode(Start, End);

// -----------------------------------------------------------------------
// Operators
// -----------------------------------------------------------------------

public sealed record BinaryExpression(
    JsTokenKind Op, Expression Left, Expression Right,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

public sealed record LogicalExpression(
    JsTokenKind Op, Expression Left, Expression Right,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

public sealed record UnaryExpression(
    JsTokenKind Op, Expression Argument, bool Prefix,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

public sealed record UpdateExpression(
    JsTokenKind Op, Expression Argument, bool Prefix,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

public sealed record AssignmentExpression(
    JsTokenKind Op, Expression Target, Expression Value,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

public sealed record ConditionalExpression(
    Expression Test, Expression Consequent, Expression Alternate,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

public sealed record SequenceExpression(
    IReadOnlyList<Expression> Expressions,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

// -----------------------------------------------------------------------
// Access + calls
// -----------------------------------------------------------------------

public sealed record MemberExpression(
    Expression Object, Expression Property, bool Computed, bool Optional,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

public sealed record CallExpression(
    Expression Callee, IReadOnlyList<Expression> Arguments, bool Optional,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

public sealed record NewExpression(
    Expression Callee, IReadOnlyList<Expression> Arguments,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

public sealed record SpreadElement(
    Expression Argument, JsPosition Start, JsPosition End)
    : Expression(Start, End);

/// <summary>
/// <c>function () { ... }</c> as an expression. Name is optional. Named
/// function expressions bind their own name inside the body.
/// </summary>
public sealed record FunctionExpression(
    Identifier? Name,
    IReadOnlyList<Expression> Params, // Identifier or destructuring binding pattern (ES2024 §14.3.3)
    BlockStatement Body,
    bool Generator,
    JsPosition Start, JsPosition End,
    bool Async = false,
    // ES strict mode: true when this function's body parses as strict.
    bool Strict = false)
    : Expression(Start, End);

/// <summary>
/// B1b-2c — <c>yield expr</c> or <c>yield</c> or <c>yield* iter</c>. Only
/// legal inside a generator body; the compiler/VM error if seen elsewhere.
/// </summary>
public sealed record YieldExpression(
    Expression? Argument,
    bool Delegate, // yield*
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

/// <summary>
/// B1b-2c — <c>await expr</c>. Only legal inside an async function or
/// async generator body.
/// </summary>
public sealed record AwaitExpression(
    Expression Argument,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

/// <summary>
/// Arrow function <c>(x) =&gt; expr</c> or <c>(x) =&gt; { stmts }</c>. Unlike
/// <see cref="FunctionExpression"/>, an arrow body's <c>this</c> binding is
/// captured lexically from the enclosing scope. <see cref="IsExpression"/> is
/// true for concise-body forms (single expression after <c>=&gt;</c>); false for
/// block-body forms.
/// </summary>
public sealed record ArrowFunctionExpression(
    IReadOnlyList<Expression> Params, // Identifier or destructuring binding pattern (ES2024 §14.3.3)
    AstNode Body,                // BlockStatement or Expression
    bool IsExpression,           // true => Body is an Expression (concise body)
    bool Async,
    JsPosition Start, JsPosition End,
    bool Generator = false,
    // ES strict mode: true when this arrow's body parses as strict.
    bool Strict = false)
    : Expression(Start, End);

/// <summary>
/// Template literal <c>`pre ${a} mid ${b} tail`</c>. <see cref="Quasis"/> is
/// the cooked literal string segments; <see cref="Expressions"/> is the
/// substitution expressions. Always <c>Quasis.Count == Expressions.Count + 1</c>.
/// A cooked segment is <c>null</c> only for a tagged template that contains an
/// invalid escape sequence (§12.9.6) — there the cooked element is
/// <c>undefined</c> while <see cref="RawQuasis"/> still holds its raw source.
/// </summary>
public sealed record TemplateLiteral(
    IReadOnlyList<string?> Quasis,
    IReadOnlyList<Expression> Expressions,
    IReadOnlyList<string> RawQuasis,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

/// <summary>Tagged template — <c>tag`...`</c>.</summary>
public sealed record TaggedTemplateExpression(
    Expression Tag,
    TemplateLiteral Quasi,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

// -----------------------------------------------------------------------
// Classes (B1b-2a)
// -----------------------------------------------------------------------

/// <summary>
/// ES2024 §15.7 ClassBody — collected by the parser. Methods include both
/// regular methods and accessor (get/set) methods (their <see cref="MethodKind"/>
/// distinguishes). Fields are public/private instance or static field
/// declarations. <see cref="StaticBlocks"/> hold static-initialization
/// blocks (ES2022 §15.7.4).
/// </summary>
public sealed record ClassBody(
    MethodDefinition? Constructor,
    IReadOnlyList<MethodDefinition> Methods,
    IReadOnlyList<PropertyField> Fields,
    IReadOnlyList<BlockStatement> StaticBlocks,
    JsPosition Start, JsPosition End)
    : AstNode(Start, End);

/// <summary>
/// A method definition inside a class body. <see cref="Key"/> is an
/// <see cref="Identifier"/>, <see cref="StringLiteral"/>, <see cref="NumericLiteral"/>,
/// or <see cref="PrivateNameExpression"/>; if <see cref="Computed"/> is true,
/// it can be any expression.
/// </summary>
public sealed record MethodDefinition(
    Expression Key,
    MethodKind Kind,
    bool IsStatic,
    bool Computed,
    IReadOnlyList<Expression> Params,
    BlockStatement Body,
    JsPosition Start, JsPosition End,
    bool Generator = false,
    bool Async = false,
    // ES strict mode: class bodies are always strict (§15.7), so this is
    // always true for class methods/constructors.
    bool Strict = false)
    : AstNode(Start, End);

public enum MethodKind
{
    /// <summary>Regular method — <c>foo() { ... }</c>.</summary>
    Method,
    /// <summary>Constructor — <c>constructor() { ... }</c>.</summary>
    Constructor,
    /// <summary>Getter — <c>get name() { ... }</c>.</summary>
    Get,
    /// <summary>Setter — <c>set name(v) { ... }</c>.</summary>
    Set,
}

/// <summary>
/// A field declaration inside a class body — <c>name = expr;</c> or
/// <c>#name = expr;</c>, with or without <see cref="IsStatic"/>.
/// <see cref="Initializer"/> may be null for declarations without
/// initializers (the field still pins the slot but starts undefined).
/// </summary>
public sealed record PropertyField(
    Expression Key,
    bool IsStatic,
    bool Computed,
    Expression? Initializer,
    JsPosition Start, JsPosition End)
    : AstNode(Start, End);

/// <summary>
/// A <c>#name</c> reference — either a private-field declaration key,
/// a private-method key, or a private property access. The lexer emits
/// the leading <c>#</c> in <see cref="Name"/>.
/// </summary>
public sealed record PrivateNameExpression(
    string Name,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

/// <summary>
/// §13.10 ergonomic brand check — <c>#name in object</c>. Evaluates to a
/// boolean: whether <see cref="Object"/> carries the private element named
/// <see cref="Name"/> declared by the enclosing class. The left operand is a
/// bare PrivateIdentifier (not a member access on a receiver), so it is its own
/// AST node rather than a <see cref="BinaryExpression"/>.
/// </summary>
public sealed record PrivateInExpression(
    string Name, Expression Object,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

/// <summary>
/// <c>class [Name] [extends Base] { body }</c> as an expression.
/// <see cref="Name"/> is optional; when present it's bound inside the body
/// only (the rest of the program does not see it).
/// </summary>
public sealed record ClassExpression(
    Identifier? Name,
    Expression? BaseClass,
    ClassBody Body,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

/// <summary>
/// <c>super.prop</c> / <c>super[expr]</c> property access. The compiler
/// lowers this to <see cref="Starling.Js.Bytecode.Opcode.LoadSuperProperty"/>
/// or <see cref="Starling.Js.Bytecode.Opcode.StoreSuperProperty"/>.
/// </summary>
public sealed record SuperPropertyExpression(
    Expression Property,
    bool Computed,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);

/// <summary>
/// <c>super(...args)</c> call — only valid inside a derived constructor.
/// </summary>
public sealed record SuperCallExpression(
    IReadOnlyList<Expression> Arguments,
    JsPosition Start, JsPosition End)
    : Expression(Start, End);
