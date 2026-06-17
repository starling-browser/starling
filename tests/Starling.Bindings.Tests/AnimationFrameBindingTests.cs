using AwesomeAssertions;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;
using Starling.Loop;
namespace Starling.Bindings.Tests;

[TestClass]
public sealed class AnimationFrameBindingTests
{
    [TestMethod]
    public void Raf_fires_callback_with_frame_timestamp()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, """
            globalThis.__t = -1;
            globalThis.__id = requestAnimationFrame(t => { globalThis.__t = t; });
        """);

        runtime.GetGlobal("__id").Kind.Should().Be(JsValueKind.Number);
        runtime.GetGlobal("__t").AsNumber.Should().Be(-1);

        loop.RunFrame(16);
        runtime.GetGlobal("__t").AsNumber.Should().Be(16);
    }

    [TestMethod]
    public void CancelAnimationFrame_stops_callback()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, """
            globalThis.__fired = false;
            const id = requestAnimationFrame(() => { globalThis.__fired = true; });
            cancelAnimationFrame(id);
        """);

        loop.RunFrame(16);
        runtime.GetGlobal("__fired").AsBool.Should().BeFalse();
    }

    [TestMethod]
    public void Nested_raf_runs_on_next_frame()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, """
            globalThis.__count = 0;
            function f() {
              globalThis.__count = globalThis.__count + 1;
              requestAnimationFrame(f);
            }
            requestAnimationFrame(f);
        """);

        loop.RunFrame(16);
        runtime.GetGlobal("__count").AsNumber.Should().Be(1);

        loop.RunFrame(32);
        runtime.GetGlobal("__count").AsNumber.Should().Be(2);

        loop.RunFrame(48);
        runtime.GetGlobal("__count").AsNumber.Should().Be(3);
    }

    [TestMethod]
    public void Non_callable_throws_TypeError()
    {
        var (runtime, _, _) = NewHost();
        var act = () => Eval(runtime, "requestAnimationFrame(42);");
        act.Should().Throw<JsThrow>()
            .Which.Value.AsObject.Get("message").AsString.Should().Contain("callable");
    }

    [TestMethod]
    public void Errors_in_callback_route_to_console_and_dont_stop_loop()
    {
        var (runtime, loop, errors) = NewHost();

        Eval(runtime, """
            globalThis.__after = false;
            requestAnimationFrame(() => { throw new Error('boom'); });
            requestAnimationFrame(() => { globalThis.__after = true; });
        """);

        loop.RunFrame(16);
        errors.Should().ContainSingle(e => e.Contains("boom"));
        runtime.GetGlobal("__after").AsBool.Should().BeTrue();
    }

    private static (JsRuntime Runtime, WebEventLoop Loop, List<string> Errors) NewHost()
    {
        var runtime = new JsRuntime();
        var loop = new WebEventLoop();
        var errors = new List<string>();
        runtime.Realm.ConsoleSink = (level, msg) =>
        {
            if (level == ConsoleLevel.Error)
            {
                errors.Add(msg);
            }
        };
        AnimationFrameBinding.Install(runtime, loop);
        return (runtime, loop, errors);
    }

    private static JsValue Eval(JsRuntime runtime, string source)
    {
        var program = new JsParser(source).ParseProgram();
        var chunk = JsCompiler.Compile(program);
        return new JsVm(runtime).Run(chunk);
    }
}
