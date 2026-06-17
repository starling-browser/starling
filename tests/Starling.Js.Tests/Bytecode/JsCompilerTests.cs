using AwesomeAssertions;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;
namespace Starling.Js.Tests.Bytecode;

[TestClass]
public class JsCompilerTests
{
    [TestMethod]
    public void Empty_program_just_halts()
    {
        var c = Compile("");
        // No statements → just Halt.
        c.Code.Should().Equal((byte)Opcode.Halt);
    }

    [TestMethod]
    public void Numeric_literal_zero_uses_LoadZero()
    {
        // Expression statement: LoadZero, Pop, Halt.
        var c = Compile("0;");
        c.Code.Should().Equal(
            (byte)Opcode.LoadZero,
            (byte)Opcode.Pop,
            (byte)Opcode.Halt);
    }

    [TestMethod]
    public void Nonzero_numeric_literal_loads_from_constant_pool()
    {
        var c = Compile("42;");
        c.Constants.Should().Contain(42.0);
        // LoadConst(idx) Pop Halt
        c.Code[0].Should().Be((byte)Opcode.LoadConst);
        c.Code[3].Should().Be((byte)Opcode.Pop);
    }

    [TestMethod]
    public void String_literal_interned_in_pool()
    {
        var c = Compile("\"hello\";");
        c.Constants.Should().Contain("hello");
    }

    [TestMethod]
    public void Boolean_and_null_use_dedicated_opcodes()
    {
        var t = Compile("true;");
        t.Code[0].Should().Be((byte)Opcode.LoadTrue);

        var n = Compile("null;");
        n.Code[0].Should().Be((byte)Opcode.LoadNull);
    }

    [TestMethod]
    public void Script_top_var_declaration_emits_global_binding()
    {
        // gap:script-top-var-not-global — `var x = 1;` at script top creates
        // a property on the global object (§16.1.7 ScriptEvaluation →
        // CreateGlobalVarBinding), so the compiler emits the idempotent
        // DeclareGlobalVar + LoadConst + StoreGlobal sequence rather than
        // local-slot opcodes.
        var c = Compile("var x = 1;");
        var d = Disassembler.Disassemble(c);
        d.Should().Contain("DeclareGlobalVar")
            .And.Contain("LoadConst")
            .And.Contain("StoreGlobal");
    }

    [TestMethod]
    public void Var_declaration_inside_function_still_reserves_local()
    {
        // Regression: only script-top `var` becomes a global. Inside a
        // function body, vars stay as fast local slots (DeclareLocal +
        // StoreLocal).
        var d = DisassembleNestedFunction(Compile("function f(){ var x = 1; return x; }"));
        d.Should().Contain("DeclareLocal").And.Contain("StoreLocal");
    }

    [TestMethod]
    public void Script_top_var_read_resolves_to_LoadGlobal()
    {
        // gap:script-top-var-not-global — reading a script-top var name
        // goes through LoadGlobal rather than LoadLocal, since the binding
        // lives on the global object.
        var d = Disassembler.Disassemble(Compile("var x = 1; x;"));
        d.Should().Contain("LoadGlobal").And.Contain("\"x\"");
    }

    [TestMethod]
    public void Free_identifier_falls_through_to_LoadGlobal()
    {
        var d = Disassembler.Disassemble(Compile("foo;"));
        d.Should().Contain("LoadGlobal").And.Contain("\"foo\"");
    }

    [TestMethod]
    public void Binary_addition_compiles()
    {
        // 1 + 2; → LoadConst LoadConst Add Pop Halt
        var d = Disassembler.Disassemble(Compile("1 + 2;"));
        d.Should().Contain("Add");
    }

    [TestMethod]
    public void Comparison_operators()
    {
        Disassembler.Disassemble(Compile("a === b;")).Should().Contain("StrictEq");
        Disassembler.Disassemble(Compile("a < b;")).Should().Contain("Lt");
    }

    [TestMethod]
    public void Unary_minus()
    {
        Disassembler.Disassemble(Compile("-x;")).Should().Contain("Neg");
    }

    [TestMethod]
    public void Typeof_unary()
    {
        Disassembler.Disassemble(Compile("typeof x;")).Should().Contain("TypeOf");
    }

    [TestMethod]
    public void If_without_else_emits_one_forward_jump()
    {
        var d = Disassembler.Disassemble(Compile("if (a) { b(); }"));
        d.Should().Contain("JumpIfFalse");
    }

    [TestMethod]
    public void If_with_else_emits_both_jumps()
    {
        var d = Disassembler.Disassemble(Compile("if (a) b(); else c();"));
        d.Should().Contain("JumpIfFalse").And.Contain("Jump ");
    }

    [TestMethod]
    public void While_loop_has_backward_jump()
    {
        var d = Disassembler.Disassemble(Compile("while (a) b();"));
        d.Should().Contain("JumpIfFalse").And.Contain("Jump ");
    }

    [TestMethod]
    public void Logical_and_short_circuits_via_dup_jumpiffalse_pop()
    {
        // a && b → LoadGlobal a, Dup, JumpIfFalse end, Pop, LoadGlobal b, end:
        var d = Disassembler.Disassemble(Compile("a && b;"));
        d.Should().Contain("Dup").And.Contain("JumpIfFalse").And.Contain("Pop");
    }

    [TestMethod]
    public void Logical_or_short_circuits_via_dup_jumpiftrue_pop()
    {
        var d = Disassembler.Disassemble(Compile("a || b;"));
        d.Should().Contain("Dup").And.Contain("JumpIfTrue").And.Contain("Pop");
    }

    [TestMethod]
    public void Nullish_coalescing_uses_JumpIfNotNullish()
    {
        // a ?? b short-circuits to 'a' when a is NOT nullish, hence the
        // inverted polarity vs && and ||.
        var d = Disassembler.Disassemble(Compile("a ?? b;"));
        d.Should().Contain("Dup").And.Contain("JumpIfNotNullish");
    }

    [TestMethod]
    public void Member_access_compiles()
    {
        var d = Disassembler.Disassemble(Compile("a.b;"));
        d.Should().Contain("LoadProperty").And.Contain("\"b\"");
    }

    [TestMethod]
    public void Computed_member_access_compiles()
    {
        var d = Disassembler.Disassemble(Compile("a[0];"));
        d.Should().Contain("LoadComputed");
    }

    [TestMethod]
    public void Call_emits_callee_then_args()
    {
        var d = Disassembler.Disassemble(Compile("foo(1, 2);"));
        d.Should().Contain("Call 2");
    }

    [TestMethod]
    public void Conditional_ternary_compiles()
    {
        var d = Disassembler.Disassemble(Compile("a ? b : c;"));
        d.Should().Contain("JumpIfFalse").And.Contain("Jump ");
    }

    [TestMethod]
    public void Assignment_to_function_local()
    {
        var d = DisassembleNestedFunction(Compile("function f(){ var x = 0; x = 5; return x; }"));
        d.Should().Contain("StoreLocal");
    }

    [TestMethod]
    public void Compound_assignment_to_local_compiles_to_load_op_store()
    {
        var d = DisassembleNestedFunction(Compile("function f(){ var x = 1; x += 2; return x; }"));
        d.Should().Contain("LoadLocal")
            .And.Contain("Add")
            .And.Contain("StoreLocal");
    }

    [TestMethod]
    public void Postfix_increment_on_local_dups_before_mutation()
    {
        var d = DisassembleNestedFunction(Compile("function f(){ var x = 0; x++; return x; }"));
        d.Should().Contain("Dup").And.Contain("Add").And.Contain("StoreLocal");
    }

    [TestMethod]
    public void Postfix_increment_on_script_top_var_uses_global()
    {
        // gap:script-top-var-not-global — `x++` for a script-top var now
        // loads/stores through the global object.
        var d = Disassembler.Disassemble(Compile("var x = 0; x++;"));
        d.Should().Contain("LoadGlobal").And.Contain("Add").And.Contain("StoreGlobal");
    }

    [TestMethod]
    public void Sequence_expression_pops_intermediate()
    {
        var d = Disassembler.Disassemble(Compile("(a, b, c);"));
        // Two Pops (one per intermediate) plus the statement's Pop.
        d.Split('\n').Where(l => l.Contains("Pop")).Should().HaveCount(3);
    }

    [TestMethod]
    public void Return_at_top_level_is_a_syntax_error()
    {
        // §13.10.1 — a ReturnStatement outside any function body is an early
        // SyntaxError at script/module top level.
        Action top = () => Compile("return;");
        top.Should().Throw<JsParseException>();

        Action topVal = () => Compile("return 42;");
        topVal.Should().Throw<JsParseException>();

        // The same statements inside a function body compile fine.
        Action inFn = () => Compile("function f() { return; }");
        inFn.Should().NotThrow();

        Action inFnVal = () => Compile("function f() { return 42; }");
        inFnVal.Should().NotThrow();
    }

    [TestMethod]
    public void Block_scope_does_not_leak_locals_outward()
    {
        // After the block ends, a reference to 'x' should NOT resolve as
        // local. The outer scope only sees a global lookup.
        var d = Disassembler.Disassemble(Compile("{ var inner = 1; } outer;"));
        d.Should().Contain("LoadGlobal").And.Contain("\"outer\"");
    }

    [TestMethod]
    public void Throw_compiles()
    {
        var d = Disassembler.Disassemble(Compile("throw e;"));
        d.Should().Contain("Throw");
    }

    [TestMethod]
    public void String_constants_are_interned()
    {
        var c = Compile("'a'; 'a'; 'a';");
        // Three identical string literals should produce a single
        // constant-pool entry.
        c.Constants.Count(x => Equals(x, "a")).Should().Be(1);
    }

    [TestMethod]
    public void Disassembler_includes_constant_table_header()
    {
        var d = Disassembler.Disassemble(Compile("1 + 2;"));
        d.Should().Contain("# constants:")
            .And.Contain("# code:");
    }

    private static Chunk Compile(string src)
        => JsCompiler.Compile(new JsParser(src).ParseProgram());

    /// <summary>Find the first <see cref="JsFunction"/> template embedded in
    /// the constant pool of <paramref name="script"/> and disassemble its
    /// body. Used by tests that need to assert on the bytecode of a nested
    /// function (which the script-level disassembly only references by
    /// constant index).</summary>
    private static string DisassembleNestedFunction(Chunk script)
    {
        foreach (var c in script.Constants)
        {
            if (c is JsFunction fn)
            {
                return Disassembler.Disassemble(fn.Body);
            }
        }
        throw new System.InvalidOperationException("script chunk has no embedded function template");
    }
}
