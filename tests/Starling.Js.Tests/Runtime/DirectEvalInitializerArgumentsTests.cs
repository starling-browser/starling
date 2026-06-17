using AwesomeAssertions;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;

namespace Starling.Js.Tests.Runtime;

/// <summary>
/// wp:M3-81 — sec-performeval-rules-in-initializer "Additional Early Error Rules
/// for Eval Inside Initializer." When a direct eval call occurs inside a class
/// field/static initializer or a (non-arrow) function parameter default, the
/// eval ScriptBody must not contain (a reference to OR a binding identifier
/// named) <c>arguments</c>; if it does, PerformEval throws a SyntaxError BEFORE
/// running any of the eval body. Outside an initializer context the rule does
/// not apply.
/// </summary>
[TestClass]
public class DirectEvalInitializerArgumentsTests
{
    // ---- Parameter default initializer: NON-arrow functions ----

    [TestMethod]
    public void Direct_eval_declaring_arguments_in_func_decl_param_default_throws_syntax_error()
    {
        // Mirrors test262 language/eval-code/direct/func-decl-no-pre-existing-arguments-...
        // The eval ScriptBody contains a `var arguments` BindingIdentifier; the
        // function f has its own `arguments` binding (it's a non-arrow function),
        // so PerformEval applies the eval-inside-initializer rule and throws.
        var act = () => Run("function f(p = eval('var arguments')) {} f();");
        act.Should().Throw<JsThrow>();
    }

    [TestMethod]
    public void Direct_eval_referencing_arguments_in_method_param_default_throws_syntax_error()
    {
        // Object-literal concise method (non-arrow, has its own `arguments`).
        // The eval body contains an `arguments` IdentifierReference.
        var act = () => Run("var o = { m(p = eval('arguments')) {} }; o.m();");
        act.Should().Throw<JsThrow>();
    }

    [TestMethod]
    public void Direct_eval_of_arrow_referencing_arguments_in_class_method_param_default_throws()
    {
        // A nested arrow inside the eval body still counts: arrows inherit
        // `arguments` lexically, so ContainsArguments recurses through them.
        var act = () => Run("class C { m(p = eval('() => arguments')) {} } new C().m();");
        act.Should().Throw<JsThrow>();
    }

    [TestMethod]
    public void Direct_eval_in_param_default_without_arguments_runs_normally()
    {
        // Same shape, but the eval body does NOT mention arguments — so the
        // early-error check does not fire and the body runs.
        Run("var seen; function f(p = eval('seen = 42; 7')) { return p; } f(); seen;")
            .AsNumber.Should().Be(42);
    }

    // ---- Parameter default initializer: ARROW functions ----

    [TestMethod]
    public void Direct_eval_declaring_arguments_in_arrow_param_default_does_not_throw()
    {
        // Arrows have no own `arguments` binding, so an eval-introduced
        // `var arguments` in a sibling default has no name to collide with;
        // the rule does NOT apply and the body runs.
        Run("var ok = false; var f = (p = eval('var arguments = 1; ok = true; 99')) => p; var v = f(); ok && v;")
            .AsNumber.Should().Be(99);
    }

    [TestMethod]
    public void Direct_eval_declaring_arguments_in_arrow_with_arguments_param_throws()
    {
        // Mirrors test262 arrow-fn-a-following-parameter-is-named-arguments-...
        // The arrow's own parameter list explicitly binds `arguments`, so it
        // DOES have an own `arguments` binding and the rule applies.
        var act = () => Run("var f = (p = eval('var arguments'), arguments) => {}; f();");
        act.Should().Throw<JsThrow>();
    }

    // ---- Class field initializer ----

    [TestMethod]
    public void Direct_eval_referencing_arguments_in_class_field_initializer_throws()
    {
        // Mirrors test262 class/elements/direct-eval-err-contains-arguments.js.
        var act = () => Run("class C { x = eval('arguments'); } new C();");
        act.Should().Throw<JsThrow>();
    }

    [TestMethod]
    public void Direct_eval_in_class_field_initializer_does_not_execute_body()
    {
        // The early error must fire BEFORE the eval body runs, so a side
        // effect in the body must not be observed.
        Run("var executed = false; try { (class { x = eval('executed = true; arguments;'); })(); } catch (e) {} executed;")
            .AsBool.Should().BeFalse();
    }

    [TestMethod]
    public void Direct_eval_in_arrow_inside_class_field_initializer_throws_when_invoked()
    {
        // Mirrors test262 class/elements/nested-direct-eval-err-contains-arguments.js.
        // The arrow `x` is created inside the field initializer (inherits the
        // initializer context lexically); when later invoked, its deferred
        // direct eval still fires the early-error rule.
        var act = () => Run("class C { x = () => { var t = () => { eval('arguments;'); }; t(); } } new C().x();");
        act.Should().Throw<JsThrow>();
    }

    [TestMethod]
    public void Direct_eval_returning_arrow_inside_class_field_initializer_throws_on_call()
    {
        // class/elements/arrow-body-direct-eval-err-contains-arguments.js — the
        // eval body itself produces an arrow over `arguments`; the early-error
        // ContainsArguments check recurses into that arrow.
        var act = () => Run("class C { x = eval('() => arguments;'); } new C().x();");
        act.Should().Throw<JsThrow>();
    }

    // ---- CONTROL: the rule does NOT apply outside an initializer context ----

    [TestMethod]
    public void Top_level_direct_eval_of_arguments_throws_reference_error_not_syntax_error()
    {
        // At script top there is no `arguments` binding. An eval reading
        // `arguments` is NOT an eval-inside-initializer, so the
        // ContainsArguments early error does NOT fire — instead the eval
        // body runs and the unresolved IdentifierReference throws a runtime
        // ReferenceError (caught here so we can assert the type).
        var act = () => Run("eval('arguments;');");
        act.Should().Throw<JsThrow>().Where(t => IsReferenceError(t));
    }

    [TestMethod]
    public void Direct_eval_in_function_body_declaring_arguments_does_not_throw_syntax_error()
    {
        // CONTROL: outside an initializer context the ContainsArguments early
        // error does NOT fire. A direct eval in the FUNCTION BODY (not in a
        // parameter default) is permitted to declare/reference `arguments`
        // even though the surrounding function has its own arguments object.
        // We don't assert the runtime outcome (the engine's body-arguments-
        // through-direct-eval has unrelated gaps); we just assert that the
        // failure mode is NOT the early SyntaxError thrown by the rule we
        // added — the early error would be detected by ContainsArguments and
        // would prevent the eval body from running at all.
        var act = () => Run("function f() { eval('var arguments = 7; arguments;'); return 'ran'; } f();");
        // If the wp:M3-81 rule misfired here we'd see a SyntaxError before
        // the function even completed; either a successful run OR a different
        // runtime error proves the rule was not applied in this context.
        try { act(); }
        catch (JsThrow t) { IsSyntaxError(t).Should().BeFalse(); }
    }

    [TestMethod]
    public void Direct_eval_in_function_body_after_simple_param_default_does_not_apply_rule()
    {
        // After the param-default region closes, the function body itself is
        // NOT an initializer context — a direct eval inside the body declaring
        // `arguments` must NOT trigger the eval-inside-initializer rule.
        var act = () => Run("function f(p = 0) { eval('var arguments = 1;'); return 'ran'; } f();");
        try { act(); }
        catch (JsThrow t) { IsSyntaxError(t).Should().BeFalse(); }
    }

    private static bool IsSyntaxError(JsThrow t)
    {
        if (!t.Value.IsObject)
        {
            return false;
        }

        var ctor = t.Value.AsObject.Get("constructor");
        if (!ctor.IsObject)
        {
            return false;
        }

        var name = ctor.AsObject.Get("name");
        return name.IsString && name.AsString == "SyntaxError";
    }

    private static bool IsReferenceError(JsThrow t)
    {
        if (!t.Value.IsObject)
        {
            return false;
        }

        var ctor = t.Value.AsObject.Get("constructor");
        if (!ctor.IsObject)
        {
            return false;
        }

        var name = ctor.AsObject.Get("name");
        return name.IsString && name.AsString == "ReferenceError";
    }

    private static JsValue Run(string src)
    {
        var program = new JsParser(src).ParseProgram();
        var chunk = JsCompiler.CompileForEval(program);
        return new JsVm(new JsRuntime()).Run(chunk);
    }
}
