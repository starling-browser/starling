using AwesomeAssertions;
using Starling.Js.Ast;
using Starling.Js.Lex;
using Starling.Js.Parse;
namespace Starling.Js.Tests.Parse;

[TestClass]
public class JsParserStatementTests
{
    // ----- Programs --------------------------------------------------------

    [TestMethod]
    public void Empty_program()
    {
        var p = ParseProgram("");
        p.Body.Should().BeEmpty();
    }

    [TestMethod]
    public void Single_expression_statement()
    {
        var p = ParseProgram("42;");
        p.Body.Should().HaveCount(1);
        p.Body[0].Should().BeOfType<ExpressionStatement>()
            .Which.Expression.Should().BeOfType<NumericLiteral>();
    }

    [TestMethod]
    public void Asi_omits_trailing_semicolon()
    {
        var p = ParseProgram("foo()\nbar()");
        p.Body.Should().HaveCount(2);
    }

    // ----- Variable declarations ------------------------------------------

    [TestMethod]
    public void Var_declaration_single()
    {
        var p = ParseProgram("var x = 1;");
        var v = p.Body[0].Should().BeOfType<VariableDeclaration>().Subject;
        v.Kind.Should().Be("var");
        v.Declarations.Should().ContainSingle();
        ((Identifier)v.Declarations[0].Id).Name.Should().Be("x");
    }

    [TestMethod]
    public void Let_declaration_multi()
    {
        var p = ParseProgram("let a = 1, b = 2, c;");
        var v = p.Body[0].Should().BeOfType<VariableDeclaration>().Subject;
        v.Kind.Should().Be("let");
        v.Declarations.Should().HaveCount(3);
        v.Declarations[2].Init.Should().BeNull();
    }

    [TestMethod]
    public void Const_declaration()
    {
        var p = ParseProgram("const PI = 3.14;");
        ((VariableDeclaration)p.Body[0]).Kind.Should().Be("const");
    }

    [TestMethod]
    public void Let_as_identifier_at_statement_start()
    {
        // `let + 1` is a valid expression statement where 'let' is just
        // an identifier. We disambiguate via lookahead.
        var p = ParseProgram("let + 1;");
        p.Body[0].Should().BeOfType<ExpressionStatement>()
            .Which.Expression.Should().BeOfType<BinaryExpression>()
            .Which.Op.Should().Be(JsTokenKind.Plus);
    }

    // ----- Blocks / if / while / do ---------------------------------------

    [TestMethod]
    public void Block_statement()
    {
        var p = ParseProgram("{ a; b; c; }");
        var b = p.Body[0].Should().BeOfType<BlockStatement>().Subject;
        b.Body.Should().HaveCount(3);
    }

    [TestMethod]
    public void If_with_else()
    {
        var p = ParseProgram("if (a) b(); else c();");
        var ifs = p.Body[0].Should().BeOfType<IfStatement>().Subject;
        ifs.Alternate.Should().NotBeNull();
    }

    [TestMethod]
    public void If_no_else()
    {
        var ifs = ParseProgram("if (a) b();").Body[0]
            .Should().BeOfType<IfStatement>().Subject;
        ifs.Alternate.Should().BeNull();
    }

    [TestMethod]
    public void While_loop()
    {
        ParseProgram("while (i < 10) i++;").Body[0]
            .Should().BeOfType<WhileStatement>();
    }

    [TestMethod]
    public void Do_while_loop()
    {
        var d = ParseProgram("do { i--; } while (i > 0);").Body[0]
            .Should().BeOfType<DoWhileStatement>().Subject;
        d.Body.Should().BeOfType<BlockStatement>();
    }

    // ----- For loops -------------------------------------------------------

    [TestMethod]
    public void For_c_style()
    {
        var f = ParseProgram("for (let i = 0; i < 10; i++) f(i);").Body[0]
            .Should().BeOfType<ForStatement>().Subject;
        f.Init.Should().BeOfType<VariableDeclaration>();
        f.Test.Should().NotBeNull();
        f.Update.Should().NotBeNull();
    }

    [TestMethod]
    public void For_in()
    {
        var f = ParseProgram("for (const k in obj) g(k);").Body[0]
            .Should().BeOfType<ForInStatement>().Subject;
        f.Left.Should().BeOfType<VariableDeclaration>();
    }

    [TestMethod]
    public void For_of()
    {
        var f = ParseProgram("for (const k of arr) g(k);").Body[0]
            .Should().BeOfType<ForOfStatement>().Subject;
    }

    [TestMethod]
    public void For_with_empty_init_and_test()
    {
        var f = ParseProgram("for (;;) {}").Body[0]
            .Should().BeOfType<ForStatement>().Subject;
        f.Init.Should().BeNull();
        f.Test.Should().BeNull();
        f.Update.Should().BeNull();
    }

    // ----- Functions -------------------------------------------------------

    [TestMethod]
    public void Function_declaration()
    {
        var p = ParseProgram("function add(a, b) { return a + b; }");
        var fn = p.Body[0].Should().BeOfType<FunctionDeclaration>().Subject;
        fn.Name.Name.Should().Be("add");
        fn.Params.Should().HaveCount(2);
        fn.Body.Body[0].Should().BeOfType<ReturnStatement>();
    }

    [TestMethod]
    public void Function_with_rest_parameter()
    {
        var fn = ParseProgram("function f(a, ...rest) {}").Body[0]
            .Should().BeOfType<FunctionDeclaration>().Subject;
        fn.Params.Should().HaveCount(2);
        fn.Params[1].Should().BeOfType<SpreadElement>();
    }

    [TestMethod]
    public void Generator_function_marked()
    {
        var fn = ParseProgram("function* gen() {}").Body[0]
            .Should().BeOfType<FunctionDeclaration>().Subject;
        fn.Generator.Should().BeTrue();
    }

    // ----- Return / break / continue / throw ------------------------------

    [TestMethod]
    public void Bare_return()
    {
        var fn = (FunctionDeclaration)ParseProgram("function f() { return; }").Body[0];
        ((ReturnStatement)fn.Body.Body[0]).Argument.Should().BeNull();
    }

    [TestMethod]
    public void Return_with_value()
    {
        var fn = (FunctionDeclaration)ParseProgram("function f() { return 1 + 2; }").Body[0];
        ((ReturnStatement)fn.Body.Body[0]).Argument.Should().BeOfType<BinaryExpression>();
    }

    [TestMethod]
    public void Break_with_label()
    {
        var p = ParseProgram("break outer;");
        ((BreakStatement)p.Body[0]).Label.Should().Be("outer");
    }

    [TestMethod]
    public void Continue_bare()
    {
        ((ContinueStatement)ParseProgram("continue;").Body[0])
            .Label.Should().BeNull();
    }

    [TestMethod]
    public void Throw_statement()
    {
        ((ThrowStatement)ParseProgram("throw new Error('boom');").Body[0])
            .Argument.Should().BeOfType<NewExpression>();
    }

    // ----- try / catch / finally ------------------------------------------

    [TestMethod]
    public void Try_catch_finally_all_present()
    {
        var t = ParseProgram("try { a(); } catch (e) { log(e); } finally { cleanup(); }")
            .Body[0].Should().BeOfType<TryStatement>().Subject;
        t.Handler.Should().NotBeNull();
        t.Finalizer.Should().NotBeNull();
    }

    [TestMethod]
    public void Try_finally_only()
    {
        var t = ParseProgram("try { a(); } finally { cleanup(); }")
            .Body[0].Should().BeOfType<TryStatement>().Subject;
        t.Handler.Should().BeNull();
        t.Finalizer.Should().NotBeNull();
    }

    [TestMethod]
    public void Bare_catch_without_param()
    {
        var t = ParseProgram("try {} catch { handle(); }")
            .Body[0].Should().BeOfType<TryStatement>().Subject;
        t.Handler.Should().NotBeNull();
        t.Handler!.Param.Should().BeNull();
    }

    // ----- switch ---------------------------------------------------------

    [TestMethod]
    public void Switch_with_cases_and_default()
    {
        var s = ParseProgram(@"switch (x) {
            case 1: a(); break;
            case 2: b(); break;
            default: c();
        }").Body[0].Should().BeOfType<SwitchStatement>().Subject;
        s.Cases.Should().HaveCount(3);
        s.Cases[2].Test.Should().BeNull(); // default
    }

    // ----- ASI edge cases --------------------------------------------------

    [TestMethod]
    public void Return_newline_inserts_semicolon()
    {
        // `return\nfoo` should ASI to `return; foo;` per §11.9.1.
        var fn = (FunctionDeclaration)ParseProgram("function f() { return\nfoo; }").Body[0];
        ((ReturnStatement)fn.Body.Body[0]).Argument.Should().BeNull();
        fn.Body.Body[1].Should().BeOfType<ExpressionStatement>();
    }

    [TestMethod]
    public void Missing_semicolon_without_newline_throws()
    {
        var act = () => ParseProgram("a b");
        act.Should().Throw<JsParseException>();
    }

    // ----- Misc -----------------------------------------------------------

    [TestMethod]
    public void Empty_statement_via_lone_semicolon()
        => ParseProgram(";;;").Body.Should().HaveCount(3);

    [TestMethod]
    public void Debugger_statement()
        => ParseProgram("debugger;").Body[0].Should().BeOfType<DebuggerStatement>();

    [TestMethod]
    public void Nested_program_round_trip()
    {
        var src = @"
            function fizzbuzz(n) {
                for (let i = 1; i <= n; i++) {
                    if (i % 15 === 0) print('FizzBuzz');
                    else if (i % 3 === 0) print('Fizz');
                    else if (i % 5 === 0) print('Buzz');
                    else print(i);
                }
            }
            fizzbuzz(20);
        ";
        var p = ParseProgram(src);
        p.Body.Should().HaveCount(2);
        p.Body[0].Should().BeOfType<FunctionDeclaration>();
        p.Body[1].Should().BeOfType<ExpressionStatement>()
            .Which.Expression.Should().BeOfType<CallExpression>();
    }

    private static Program ParseProgram(string src) => new JsParser(src).ParseProgram();
}
