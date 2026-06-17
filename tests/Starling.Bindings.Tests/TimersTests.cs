using AwesomeAssertions;
using Starling.Js.Bytecode;
using Starling.Js.Modules;
using Starling.Js.Parse;
using Starling.Js.Runtime;
using Starling.Loop;
namespace Starling.Bindings.Tests;

[TestClass]
public sealed class TimersTests
{
    [TestMethod]
    public void SetTimeout_returns_numeric_id_and_does_not_fire_before_delay()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, "globalThis.__fired = false; globalThis.__id = setTimeout(() => { __fired = true; }, 50);");

        runtime.GetGlobal("__id").Kind.Should().Be(JsValueKind.Number);
        runtime.GetGlobal("__id").AsNumber.Should().BeGreaterThan(0);

        loop.AdvanceBy(49);
        runtime.GetGlobal("__fired").AsBool.Should().BeFalse();

        loop.AdvanceBy(1); // cumulative 50
        runtime.GetGlobal("__fired").AsBool.Should().BeTrue();
    }

    [TestMethod]
    public void SetTimeout_zero_delay_fires_on_RunUntilIdle()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, "globalThis.__fired = false; setTimeout(() => { __fired = true; }, 0);");
        runtime.GetGlobal("__fired").AsBool.Should().BeFalse();

        loop.RunUntilIdle();
        runtime.GetGlobal("__fired").AsBool.Should().BeTrue();
    }

    [TestMethod]
    public void SetTimeout_forwards_extra_arguments()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, @"
            globalThis.__captured = null;
            setTimeout((a, b, c) => { globalThis.__captured = a + ':' + b + ':' + c; }, 10, 'a', 1, true);
        ");

        loop.AdvanceBy(10);
        runtime.GetGlobal("__captured").AsString.Should().Be("a:1:true");
    }

    [TestMethod]
    public void ClearTimeout_cancels_pending_handler()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, @"
            globalThis.__fired = false;
            var id = setTimeout(() => { __fired = true; }, 10);
            clearTimeout(id);
        ");

        loop.RunUntilIdle();
        runtime.GetGlobal("__fired").AsBool.Should().BeFalse();
    }

    [TestMethod]
    public void ClearTimeout_double_clear_is_noop()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, @"
            var id = setTimeout(() => {}, 10);
            clearTimeout(id);
            clearTimeout(id); // second clear must not throw
        ");

        loop.RunUntilIdle();
        // No exception is the assertion; sanity-check loop is empty.
        loop.PendingTimerCount.Should().Be(0);
    }

    [TestMethod]
    public void SetInterval_fires_repeatedly_and_clearInterval_stops_chain()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, @"
            globalThis.__count = 0;
            globalThis.__id = setInterval(() => { __count = __count + 1; }, 10);
        ");

        // WebEventLoop is deterministic: a single AdvanceBy(30) jumps clock
        // ahead before the first fire reschedules, so reschedules land at
        // now+delay (not original_due+delay) and only one fire happens. Step
        // the clock to model real-world wall-clock ticks.
        for (var i = 0; i < 3; i++)
        {
            loop.AdvanceBy(10);
        }

        runtime.GetGlobal("__count").AsNumber.Should().Be(3);

        Eval(runtime, "clearInterval(__id);");
        for (var i = 0; i < 3; i++)
        {
            loop.AdvanceBy(10);
        }

        runtime.GetGlobal("__count").AsNumber.Should().Be(3);
    }

    [TestMethod]
    public void Non_callable_handler_throws_TypeError()
    {
        var (runtime, _, _) = NewHost();

        Action act = () => Eval(runtime, "setTimeout('not a function', 0);");
        act.Should().Throw<JsThrow>();
    }

    [TestMethod]
    public void Negative_delay_clamps_to_zero()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, "globalThis.__fired = false; setTimeout(() => { __fired = true; }, -1000);");

        loop.AdvanceBy(0);
        runtime.GetGlobal("__fired").AsBool.Should().BeTrue();
    }

    [TestMethod]
    public void Timer_callback_drains_microtasks_for_promise_reactions()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, @"
            globalThis.__settled = false;
            setTimeout(() => {
                Promise.resolve(42).then(v => { globalThis.__settled = true; });
            }, 10);
        ");

        loop.AdvanceBy(10);
        runtime.GetGlobal("__settled").AsBool.Should().BeTrue();
    }

    [TestMethod]
    public void Timer_resolved_promise_resumes_async_function()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, @"
            globalThis.__afterAwait = false;
            async function boot() {
                await new Promise(resolve => setTimeout(resolve, 0));
                globalThis.__afterAwait = true;
            }
            boot();
        ");

        loop.AdvanceBy(0);
        runtime.GetGlobal("__afterAwait").AsBool.Should().BeTrue();
    }

    [TestMethod]
    public void Wrapped_setTimeout_resolved_promise_resumes_async_function()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, @"
            globalThis.__trace = '';
            var originalSetTimeout = globalThis.setTimeout;
            globalThis.setTimeout = function (callback, delay) {
                return originalSetTimeout(function () {
                    globalThis.__trace += 'fire;';
                    return callback();
                }, delay);
            };
            globalThis.__afterAwait = false;
            async function boot() {
                globalThis.__trace += 'start;';
                await new Promise(resolve => globalThis.setTimeout(resolve, 0));
                globalThis.__trace += 'after;';
                globalThis.__afterAwait = true;
            }
            boot();
        ");

        loop.AdvanceBy(0);
        runtime.GetGlobal("__afterAwait").AsBool.Should().BeTrue(runtime.GetGlobal("__trace").AsString);
    }

    [TestMethod]
    public void Dynamic_imported_async_function_resumes_after_timer()
    {
        var (runtime, loop, _) = NewHost();
        _ = new ModuleLoader(runtime, new MapModuleHost(new Dictionary<string, string>
        {
            ["runtime.js"] = """
                export const dotnet = {
                    async runMain() {
                        globalThis.__trace += 'run-start;';
                        await new Promise(resolve => globalThis.setTimeout(resolve, 0));
                        globalThis.__trace += 'run-after;';
                    }
                };
                """,
        }));

        Eval(runtime, @"
            globalThis.__trace = '';
            globalThis.__done = false;
            import('runtime.js').then(function (ns) {
                globalThis.__trace += 'imported;';
                ns.dotnet.runMain().then(function () {
                    globalThis.__trace += 'run-resolve;';
                    globalThis.__done = true;
                }, function (error) {
                    globalThis.__trace += 'run-reject:' + (error && error.message ? error.message : String(error)) + ';';
                });
            }, function (error) {
                globalThis.__trace += 'import-reject:' + (error && error.message ? error.message : String(error)) + ';';
            });
        ");

        loop.AdvanceBy(0);
        runtime.GetGlobal("__done").AsBool.Should().BeTrue(runtime.GetGlobal("__trace").AsString);
        runtime.GetGlobal("__trace").AsString.Should().Be("imported;run-start;run-after;run-resolve;");
    }

    [TestMethod]
    public void Async_try_finally_return_await_resumes_after_timer()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, @"
            globalThis.__trace = '';
            globalThis.__done = false;
            async function runMain() {
                try {
                    globalThis.__trace += 'push;';
                    return await (async function () {
                        globalThis.__trace += 'before-timer;';
                        await new Promise(resolve => globalThis.setTimeout(resolve, 0));
                        globalThis.__trace += 'after-timer;';
                        return 42;
                    })();
                } finally {
                    globalThis.__trace += 'pop;';
                }
            }
            runMain().then(function (value) {
                globalThis.__trace += 'resolve:' + value + ';';
                globalThis.__done = true;
            }, function (error) {
                globalThis.__trace += 'reject:' + (error && error.message ? error.message : String(error)) + ';';
            });
        ");

        loop.AdvanceBy(0);
        runtime.GetGlobal("__done").AsBool.Should().BeTrue(runtime.GetGlobal("__trace").AsString);
        runtime.GetGlobal("__trace").AsString.Should().Be("push;before-timer;after-timer;pop;resolve:42;");
    }

    [TestMethod]
    public void Async_try_finally_return_comma_await_resumes_after_timer()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, @"
            globalThis.__trace = '';
            globalThis.__done = false;
            function push() { globalThis.__trace += 'push;'; }
            function pop() { globalThis.__trace += 'pop;'; }
            async function nextStep() {
                globalThis.__trace += 'next;';
                return 42;
            }
            async function runMain() {
                try {
                    return push(),
                        await new Promise(resolve => globalThis.setTimeout(resolve, 0)),
                        await nextStep();
                } finally {
                    pop();
                }
            }
            runMain().then(function (value) {
                globalThis.__trace += 'resolve:' + value + ';';
                globalThis.__done = true;
            }, function (error) {
                globalThis.__trace += 'reject:' + (error && error.message ? error.message : String(error)) + ';';
            });
        ");

        loop.AdvanceBy(0);
        runtime.GetGlobal("__done").AsBool.Should().BeTrue(runtime.GetGlobal("__trace").AsString);
        runtime.GetGlobal("__trace").AsString.Should().Be("push;next;pop;resolve:42;");
    }

    [TestMethod]
    public void Handler_throw_is_routed_to_ConsoleSink_and_subsequent_timers_still_fire()
    {
        var (runtime, loop, errors) = NewHost();

        Eval(runtime, @"
            globalThis.__second = false;
            setTimeout(() => { throw new Error('boom'); }, 5);
            setTimeout(() => { globalThis.__second = true; }, 10);
        ");

        loop.AdvanceBy(10);
        errors.Should().NotBeEmpty();
        errors[0].Should().Contain("boom");
        runtime.GetGlobal("__second").AsBool.Should().BeTrue();
    }

    [TestMethod]
    public void SetInterval_handler_can_call_clearInterval_on_itself()
    {
        var (runtime, loop, _) = NewHost();

        Eval(runtime, @"
            globalThis.__count = 0;
            globalThis.__id = setInterval(() => {
                __count = __count + 1;
                if (__count >= 2) clearInterval(__id);
            }, 10);
        ");

        for (var i = 0; i < 5; i++)
        {
            loop.AdvanceBy(10);
        }

        runtime.GetGlobal("__count").AsNumber.Should().Be(2);
    }

    // ----- helpers ---------------------------------------------------------

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
        TimersBinding.Install(runtime, loop);
        return (runtime, loop, errors);
    }

    private static JsValue Eval(JsRuntime runtime, string source)
    {
        var program = new JsParser(source).ParseProgram();
        var chunk = JsCompiler.Compile(program);
        return new JsVm(runtime).Run(chunk);
    }

    private sealed class MapModuleHost(Dictionary<string, string> modules) : IModuleHost
    {
        public string? Resolve(string specifier, string? referrer) =>
            modules.ContainsKey(specifier) ? specifier : null;

        public string? FetchSource(string resolvedUrl) =>
            modules.TryGetValue(resolvedUrl, out var source) ? source : null;
    }
}
