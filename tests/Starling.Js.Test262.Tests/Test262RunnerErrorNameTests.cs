using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;

namespace Starling.Js.Test262.Tests;

/// <summary>
/// Pins how the runner classifies a thrown error against a negative test's
/// <c>type</c>. The harness's <c>Test262Error</c> is a plain function with no
/// <c>name</c> property, so a negative <c>type: Test262Error</c> can only be
/// matched by the thrown value's constructor name.
/// </summary>
[TestClass]
public class Test262RunnerErrorNameTests
{
    [TestMethod]
    public void Test262Error_instance_resolves_by_constructor_name()
    {
        var value = Eval("function Test262Error(m){ this.message = m || ''; } new Test262Error('boom');");
        Assert.AreEqual("Test262Error", Test262Runner.ErrorName(value));
    }

    [TestMethod]
    public void Builtin_error_resolves_by_name_property()
    {
        var value = Eval("new TypeError('x');");
        Assert.AreEqual("TypeError", Test262Runner.ErrorName(value));
    }

    [TestMethod]
    public void Name_property_wins_over_constructor_name()
    {
        // `name` is present, so it is used even though the constructor (Object)
        // has a different name.
        var value = Eval("({ name: 'Custom' });");
        Assert.AreEqual("Custom", Test262Runner.ErrorName(value));
    }

    [TestMethod]
    public void Thrown_primitive_has_no_error_name()
    {
        var value = Eval("'not an error';");
        Assert.IsNull(Test262Runner.ErrorName(value));
    }

    private static JsValue Eval(string source)
    {
        var chunk = JsCompiler.CompileForEval(new JsParser(source).ParseProgram());
        return new JsVm(new JsRuntime()).Run(chunk);
    }
}
