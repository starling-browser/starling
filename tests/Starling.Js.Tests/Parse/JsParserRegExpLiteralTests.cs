using AwesomeAssertions;
using Starling.Js.Ast;
using Starling.Js.Lex;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;
namespace Starling.Js.Tests.Parse;

/// <summary>
/// B4-1-followup-a: regex literal syntax wired through the parser. The lexer
/// already emits RegExpLiteral tokens (B1a); these tests verify that the
/// parser consumes them as primary expressions and that the compiler + VM
/// produce fresh JsRegExp instances per evaluation.
/// </summary>
[TestClass]
public class JsParserRegExpLiteralTests
{
    // ----- Parser: AST shape ----------------------------------------------

    [TestMethod]
    public void Bare_regex_literal_parses()
    {
        var rx = Parse("/foo/").Should().BeOfType<RegExpLiteral>().Subject;
        rx.Source.Should().Be("foo");
        rx.Flags.Should().Be(string.Empty);
    }

    [TestMethod]
    public void Regex_literal_with_flags_parses()
    {
        var rx = Parse("/foo/gi").Should().BeOfType<RegExpLiteral>().Subject;
        rx.Source.Should().Be("foo");
        rx.Flags.Should().Be("gi");
    }

    [TestMethod]
    public void Escaped_slash_inside_regex_does_not_terminate_it()
    {
        var rx = Parse(@"/a\/b/").Should().BeOfType<RegExpLiteral>().Subject;
        rx.Source.Should().Be(@"a\/b");
    }

    [TestMethod]
    public void Empty_regex_non_capturing_alternation_parses()
    {
        // `/(?:)/` is the canonical way to write an empty regex in source
        // because `//` is a single-line comment per ES2024 13.4.
        var rx = Parse("/(?:)/").Should().BeOfType<RegExpLiteral>().Subject;
        rx.Source.Should().Be("(?:)");
    }

    [TestMethod]
    public void Division_with_numeric_left_operand_is_not_regex()
    {
        // After a NumericLiteral the parser is in multiplicative position,
        // so `/` is division (lexer emits Slash and ParsePrimary is not
        // called for it).
        var bin = Parse("1 / 2 / 3").Should().BeOfType<BinaryExpression>().Subject;
        bin.Op.Should().Be(JsTokenKind.Slash);
    }

    // ----- Runtime: semantics ---------------------------------------------

    [TestMethod]
    public void Digit_global_regex_matches_via_test()
        => Eval(@"/\d+/g.test(""abc 123"");").AsBool.Should().BeTrue();

    [TestMethod]
    public void Case_insensitive_flag_works()
        => Eval(@"/foo/i.test(""FOO"");").AsBool.Should().BeTrue();

    [TestMethod]
    public void Exec_returns_capture_groups()
        => Eval(@"/(\d+)-(\d+)/.exec(""12-34"")[1];").AsString.Should().Be("12");

    [TestMethod]
    public void Global_regex_advances_last_index_across_calls()
    {
        var src = @"
            var re = /a/g;
            var before = re.lastIndex;
            re.exec(""aaa"");
            var after = re.lastIndex;
            [before, after];
        ";
        var arr = Eval(src);
        arr.IsObject.Should().BeTrue();
        var jsArr = (JsArray)arr.AsObject;
        jsArr.Get("0").AsNumber.Should().Be(0);
        jsArr.Get("1").AsNumber.Should().Be(1);
    }

    [TestMethod]
    public void Each_evaluation_of_regex_literal_yields_fresh_instance()
    {
        // Per 13.2.7.3 every evaluation of a RegExp literal creates a fresh
        // RegExp object, so a function that returns the lastIndex of its
        // literal regex must see a fresh 0 on every call.
        var src = @"
            function f() {
                var r = /x/g;
                r.exec(""xxx"");
                return r.lastIndex;
            }
            function g() {
                return /x/g.lastIndex;
            }
            [f(), g(), g()];
        ";
        var arr = Eval(src);
        var jsArr = (JsArray)arr.AsObject;
        jsArr.Get("0").AsNumber.Should().Be(1); // after exec
        jsArr.Get("1").AsNumber.Should().Be(0); // fresh
        jsArr.Get("2").AsNumber.Should().Be(0); // fresh again
    }

    // ----- Position contexts ----------------------------------------------

    [TestMethod]
    public void Regex_as_var_initializer()
        => Eval(@"var x = /a/; x.test(""a"");").AsBool.Should().BeTrue();

    [TestMethod]
    public void Regex_as_return_value()
        => Eval(@"function f() { return /a/; } f().test(""a"");").AsBool.Should().BeTrue();

    [TestMethod]
    public void Regex_as_call_argument()
        => Eval(@"""abc"".match(/b/)[0];").AsString.Should().Be("b");

    [TestMethod]
    public void Regex_inside_array_literal()
        => Eval(@"[/a/, /b/].length;").AsNumber.Should().Be(2);

    [TestMethod]
    public void Regex_inside_object_literal()
        => Eval(@"({re: /x/}).re.test(""x"");").AsBool.Should().BeTrue();

    [TestMethod]
    public void Regex_after_return_keyword()
        => Eval(@"function f() { return /xyz/; } f().source;").AsString.Should().Be("xyz");

    [TestMethod]
    public void Regex_after_if_paren()
    {
        // `if (a) /foo/.test(...);` — regex appears at the start of the
        // expression-statement that the `if`'s body resolves to.
        var src = @"
            var hit = false;
            if (true) hit = /foo/.test(""xfoox"");
            hit;
        ";
        Eval(src).AsBool.Should().BeTrue();
    }

    // ----- Disambiguation -------------------------------------------------

    [TestMethod]
    public void Chained_division_evaluates_left_to_right()
    {
        // `1 / 2 / 3` must remain division (== 1/6), NOT a regex literal
        // straddling the slashes.
        Eval("var x = 1 / 2 / 3; x;").AsNumber.Should().BeApproximately(1.0 / 6.0, 1e-12);
    }

    // ----- Errors ---------------------------------------------------------

    [TestMethod]
    public void Invalid_regex_is_an_early_parse_error()
    {
        // §12.9.5 — a RegularExpressionLiteral whose pattern fails to parse
        // (here the unbalanced `(`) is an early SyntaxError surfaced at PARSE
        // time, not deferred to the runtime LoadRegExp compile step.
        var act = () => Parse("/(/.test(\"\");");
        act.Should().Throw<JsParseException>();
    }

    // ----- Helpers --------------------------------------------------------

    private static Expression Parse(string src) => new JsParser(src).ParseExpression();

    private static JsValue Eval(string src)
    {
        var program = new JsParser(src).ParseProgram();
        var chunk = JsCompiler.CompileForEval(program);
        return new JsVm(new JsRuntime()).Run(chunk);
    }
}
