using AwesomeAssertions;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;
namespace Starling.Js.Tests.Intrinsics;

/// <summary>
/// End-to-end coverage for the Promise intrinsic (B3-4). Each test spins up a
/// fresh <see cref="JsRuntime"/>, executes a script, and asserts on globals
/// written by promise callbacks. The top-level <see cref="JsVm.Run(Chunk)"/>
/// drains the microtask queue before returning, so settled-promise reactions
/// are observable without manual pumping.
/// </summary>
/// <remarks>
/// Iterable-statics (<c>all</c>, <c>allSettled</c>, <c>any</c>, <c>race</c>)
/// take array-likes — the iterator protocol arrives in B3-2.
/// <c>AggregateError</c> is observable only as a plain object with
/// <c>name='AggregateError'</c> + an <c>errors</c> array-like until the real
/// constructor lands with B2-3.
/// </remarks>
[TestClass]
public class PromiseTests
{
    [TestMethod]
    public void Promise_is_registered_on_global_with_prototype_slot()
    {
        var rt = new JsRuntime();
        var Promise = rt.GetGlobal("Promise");

        Promise.IsObject.Should().BeTrue();
        var proto = Promise.AsObject.Get("prototype");
        proto.AsObject.Should().BeSameAs(rt.Realm.PromisePrototype);
        rt.Realm.PromiseConstructor.Should().BeSameAs(Promise.AsObject);
    }

    [TestMethod]
    public void Promise_resolve_then_settles_after_drain()
    {
        var rt = Run(@"
            globalThis.result = 0;
            Promise.resolve(42).then(function(v) { globalThis.result = v; });
        ");
        rt.GetGlobal("result").AsNumber.Should().Be(42);
    }

    [TestMethod]
    public void Multiple_then_handlers_fire_in_registration_order()
    {
        var rt = Run(@"
            globalThis.order = '';
            var p = Promise.resolve(1);
            p.then(function() { globalThis.order = globalThis.order + 'a'; });
            p.then(function() { globalThis.order = globalThis.order + 'b'; });
            p.then(function() { globalThis.order = globalThis.order + 'c'; });
        ");
        rt.GetGlobal("order").AsString.Should().Be("abc");
    }

    [TestMethod]
    public void Promise_constructor_throws_on_non_callable_executor()
    {
        var ex = Assert.ThrowsExactly<JsThrow>(() =>
        {
            var rt = new JsRuntime();
            var program = new JsParser("new Promise(42);").ParseProgram();
            var chunk = JsCompiler.CompileForEval(program);
            new JsVm(rt).Run(chunk);
        });
        ex.Value.IsObject.Should().BeTrue();
        ex.Value.AsObject.Get("message").AsString.Should().Contain("Promise resolver");
    }

    [TestMethod]
    public void Promise_constructor_throws_without_executor()
    {
        var ex = Assert.ThrowsExactly<JsThrow>(() =>
        {
            var rt = new JsRuntime();
            var program = new JsParser("new Promise();").ParseProgram();
            var chunk = JsCompiler.CompileForEval(program);
            new JsVm(rt).Run(chunk);
        });

        ex.Value.IsObject.Should().BeTrue();
        ex.Value.AsObject.Get("message").AsString.Should().Contain("Promise resolver");
    }

    [TestMethod]
    public void Promise_constructor_accepts_callable_executor()
    {
        var rt = Run(@"
            globalThis.result = 0;
            new Promise(function(resolve, reject) { resolve(66); })
                .then(function(v) { globalThis.result = v; });
        ");

        rt.GetGlobal("result").AsNumber.Should().Be(66);
    }

    [TestMethod]
    public void Executor_throw_causes_rejection_with_thrown_value()
    {
        var rt = Run(@"
            globalThis.reason = null;
            new Promise(function(res, rej) { throw 'boom'; })
                .then(undefined, function(r) { globalThis.reason = r; });
        ");
        rt.GetGlobal("reason").AsString.Should().Be("boom");
    }

    [TestMethod]
    public void Then_chain_threads_values_through_each_step()
    {
        var rt = Run(@"
            globalThis.result = 0;
            Promise.resolve(1)
                .then(function(x) { return x + 1; })
                .then(function(x) { return x * 10; })
                .then(function(x) { globalThis.result = x; });
        ");
        rt.GetGlobal("result").AsNumber.Should().Be(20);
    }

    [TestMethod]
    public void Catch_recovers_a_rejected_chain_and_continues_fulfilled()
    {
        var rt = Run(@"
            globalThis.recovered = 0;
            globalThis.afterCatch = 0;
            Promise.reject('bad')
                .catch(function(r) { globalThis.recovered = 1; return 99; })
                .then(function(v) { globalThis.afterCatch = v; });
        ");
        rt.GetGlobal("recovered").AsNumber.Should().Be(1);
        rt.GetGlobal("afterCatch").AsNumber.Should().Be(99);
    }

    [TestMethod]
    public void Finally_runs_on_fulfillment_and_forwards_value()
    {
        var rt = Run(@"
            globalThis.ran = 0;
            globalThis.value = 0;
            Promise.resolve(7)
                .finally(function() { globalThis.ran = 1; })
                .then(function(v) { globalThis.value = v; });
        ");
        rt.GetGlobal("ran").AsNumber.Should().Be(1);
        rt.GetGlobal("value").AsNumber.Should().Be(7);
    }

    [TestMethod]
    public void Finally_runs_on_rejection_and_forwards_reason()
    {
        var rt = Run(@"
            globalThis.ran = 0;
            globalThis.reason = '';
            Promise.reject('nope')
                .finally(function() { globalThis.ran = 1; })
                .catch(function(r) { globalThis.reason = r; });
        ");
        rt.GetGlobal("ran").AsNumber.Should().Be(1);
        rt.GetGlobal("reason").AsString.Should().Be("nope");
    }

    [TestMethod]
    public void Finally_throw_rejects_the_outer_promise()
    {
        var rt = Run(@"
            globalThis.reason = '';
            Promise.resolve(1)
                .finally(function() { throw 'finally-bad'; })
                .catch(function(r) { globalThis.reason = r; });
        ");
        rt.GetGlobal("reason").AsString.Should().Be("finally-bad");
    }

    [TestMethod]
    public void Finally_returns_new_promise_and_non_callable_handler_forwards_value()
    {
        var rt = Run(@"
            globalThis.same = true;
            globalThis.value = 0;
            var p = Promise.resolve(2);
            var q = p.finally();
            globalThis.same = p === q;
            q.then(function(v) { globalThis.value = v; });
        ");

        rt.GetGlobal("same").AsBool.Should().BeFalse();
        rt.GetGlobal("value").AsNumber.Should().Be(2);
    }

    [TestMethod]
    public void Finally_chain_ignores_fulfilled_handler_return_value()
    {
        var rt = Run(@"
            globalThis.value = 0;
            Promise.resolve(2)
                .finally(function() { return 6; })
                .finally(function() { return 9; })
                .then(function(v) { globalThis.value = v; });
        ");

        rt.GetGlobal("value").AsNumber.Should().Be(2);
    }

    [TestMethod]
    public void Promise_all_resolves_to_array_of_values_in_order()
    {
        var rt = Run(@"
            globalThis.r = '';
            Promise.all([Promise.resolve(1), Promise.resolve(2), Promise.resolve(3)])
                .then(function(vs) { globalThis.r = vs[0] + ',' + vs[1] + ',' + vs[2] + ':' + vs.length; });
        ");
        rt.GetGlobal("r").AsString.Should().Be("1,2,3:3");
    }

    [TestMethod]
    public void Promise_all_rejects_on_first_rejection()
    {
        var rt = Run(@"
            globalThis.reason = '';
            Promise.all([Promise.resolve(1), Promise.reject('first-bad'), Promise.reject('second-bad')])
                .catch(function(r) { globalThis.reason = r; });
        ");
        rt.GetGlobal("reason").AsString.Should().Be("first-bad");
    }

    [TestMethod]
    public void Promise_all_with_non_promise_values_lifts_them()
    {
        var rt = Run(@"
            globalThis.r = '';
            Promise.all([1, 2, Promise.resolve(3)])
                .then(function(vs) { globalThis.r = vs[0] + ',' + vs[1] + ',' + vs[2]; });
        ");
        rt.GetGlobal("r").AsString.Should().Be("1,2,3");
    }

    [TestMethod]
    public void Promise_all_rejects_missing_or_non_iterable_input()
    {
        var rt = Run(@"
            globalThis.missing = '';
            globalThis.object = '';
            Promise.all().catch(function(e) { globalThis.missing = e.name; });
            Promise.all({}).catch(function(e) { globalThis.object = e.name; });
        ");

        rt.GetGlobal("missing").AsString.Should().Be("TypeError");
        rt.GetGlobal("object").AsString.Should().Be("TypeError");
    }

    [TestMethod]
    public void Promise_allSettled_reports_per_entry_status()
    {
        var rt = Run(@"
            globalThis.r = '';
            Promise.allSettled([Promise.resolve(1), Promise.reject('bad')])
                .then(function(rs) {
                    globalThis.r = rs[0].status + '=' + rs[0].value + '|' + rs[1].status + '=' + rs[1].reason;
                });
        ");
        rt.GetGlobal("r").AsString.Should().Be("fulfilled=1|rejected=bad");
    }

    [TestMethod]
    public void Promise_allSettled_rejects_missing_or_non_iterable_input()
    {
        var rt = Run(@"
            globalThis.missing = '';
            globalThis.object = '';
            Promise.allSettled().catch(function(e) { globalThis.missing = e.name; });
            Promise.allSettled({}).catch(function(e) { globalThis.object = e.name; });
        ");

        rt.GetGlobal("missing").AsString.Should().Be("TypeError");
        rt.GetGlobal("object").AsString.Should().Be("TypeError");
    }

    [TestMethod]
    public void Promise_race_settles_with_first_to_settle()
    {
        // Synchronously-settled promises run in registration order through
        // the microtask queue, so the first one wins.
        var rt = Run(@"
            globalThis.r = 0;
            Promise.race([Promise.resolve(5), Promise.resolve(99)])
                .then(function(v) { globalThis.r = v; });
        ");
        rt.GetGlobal("r").AsNumber.Should().Be(5);
    }

    [TestMethod]
    public void Promise_race_rejects_when_first_is_a_rejection()
    {
        var rt = Run(@"
            globalThis.reason = '';
            Promise.race([Promise.reject('first'), Promise.resolve(2)])
                .catch(function(r) { globalThis.reason = r; });
        ");
        rt.GetGlobal("reason").AsString.Should().Be("first");
    }

    [TestMethod]
    public void Promise_race_resolves_plain_values_and_pending_promises_in_order()
    {
        Run(@"
            globalThis.first = 0;
            Promise.race([12, 2, 3]).then(function(v) { globalThis.first = v; });
        ").GetGlobal("first").AsNumber.Should().Be(12);

        Run(@"
            globalThis.second = 0;
            Promise.race([Promise.resolve(2), 6, 3]).then(function(v) { globalThis.second = v; });
        ").GetGlobal("second").AsNumber.Should().Be(2);

        Run(@"
            globalThis.third = 0;
            Promise.race([new Promise(function() {}), Promise.resolve(55), 3])
                .then(function(v) { globalThis.third = v; });
        ").GetGlobal("third").AsNumber.Should().Be(55);
    }

    [TestMethod]
    public void Promise_race_rejects_missing_or_non_iterable_input()
    {
        var rt = Run(@"
            globalThis.missing = '';
            globalThis.object = '';
            Promise.race().catch(function(e) { globalThis.missing = e.name; });
            Promise.race({}).catch(function(e) { globalThis.object = e.name; });
        ");

        rt.GetGlobal("missing").AsString.Should().Be("TypeError");
        rt.GetGlobal("object").AsString.Should().Be("TypeError");
    }

    [TestMethod]
    public void Promise_race_rejects_when_first_settled_input_rejects_after_pending()
    {
        var rt = Run(@"
            globalThis.reason = '';
            Promise.race([new Promise(function() {}), Promise.reject('Could not connect'), 3])
                .catch(function(e) { globalThis.reason = e; });
        ");

        rt.GetGlobal("reason").AsString.Should().Be("Could not connect");
    }

    [TestMethod]
    public void Promise_any_returns_first_fulfillment()
    {
        var rt = Run(@"
            globalThis.r = 0;
            Promise.any([Promise.reject('a'), Promise.resolve(42), Promise.reject('c')])
                .then(function(v) { globalThis.r = v; });
        ");
        rt.GetGlobal("r").AsNumber.Should().Be(42);
    }

    [TestMethod]
    public void Promise_any_rejects_with_AggregateError_when_all_reject()
    {
        // Promise.any rejects with a real AggregateError instance built via
        // realm.NewAggregateError; the errors slot is a JsArray preserving
        // source order.
        var rt = Run(@"
            globalThis.name = '';
            globalThis.errs = '';
            Promise.any([Promise.reject('a'), Promise.reject('b')])
                .catch(function(e) {
                    globalThis.name = e.name;
                    globalThis.errs = e.errors[0] + ',' + e.errors[1] + ':' + e.errors.length;
                });
        ");
        rt.GetGlobal("name").AsString.Should().Be("AggregateError");
        rt.GetGlobal("errs").AsString.Should().Be("a,b:2");
    }

    [TestMethod]
    public void Promise_any_aggregate_error_is_real_AggregateError_instance()
    {
        var rt = Run(@"
            globalThis.isAgg = false;
            globalThis.isErr = false;
            globalThis.msg = '';
            Promise.any([Promise.reject(1), Promise.reject(2)])
                .catch(function(e) {
                    globalThis.isAgg = e instanceof AggregateError;
                    globalThis.isErr = e instanceof Error;
                    globalThis.msg = e.message;
                });
        ");
        rt.GetGlobal("isAgg").AsBool.Should().BeTrue();
        rt.GetGlobal("isErr").AsBool.Should().BeTrue();
        rt.GetGlobal("msg").AsString.Should().Be("All promises were rejected");
    }

    [TestMethod]
    public void Promise_any_aggregate_error_preserves_numeric_reasons()
    {
        var rt = Run(@"
            globalThis.e0 = 0;
            globalThis.e1 = 0;
            globalThis.len = 0;
            Promise.any([Promise.reject(1), Promise.reject(2)])
                .catch(function(e) {
                    globalThis.e0 = e.errors[0];
                    globalThis.e1 = e.errors[1];
                    globalThis.len = e.errors.length;
                });
        ");
        rt.GetGlobal("e0").AsNumber.Should().Be(1);
        rt.GetGlobal("e1").AsNumber.Should().Be(2);
        rt.GetGlobal("len").AsNumber.Should().Be(2);
    }

    [TestMethod]
    public void Promise_any_rejects_missing_or_non_iterable_input()
    {
        var rt = Run(@"
            globalThis.missing = '';
            globalThis.object = '';
            Promise.any().catch(function(e) { globalThis.missing = e.name; });
            Promise.any({}).catch(function(e) { globalThis.object = e.name; });
        ");

        rt.GetGlobal("missing").AsString.Should().Be("TypeError");
        rt.GetGlobal("object").AsString.Should().Be("TypeError");
    }

    [TestMethod]
    public void Promise_withResolvers_exposes_capability_fields()
    {
        var rt = Run(@"
            globalThis.r = 0;
            var d = Promise.withResolvers();
            d.promise.then(function(v) { globalThis.r = v; });
            d.resolve(123);
        ");
        rt.GetGlobal("r").AsNumber.Should().Be(123);
    }

    [TestMethod]
    public void Promise_withResolvers_reject_path_settles_correctly()
    {
        var rt = Run(@"
            globalThis.r = '';
            var d = Promise.withResolvers();
            d.promise.catch(function(e) { globalThis.r = e; });
            d.reject('nope');
        ");
        rt.GetGlobal("r").AsString.Should().Be("nope");
    }

    [TestMethod]
    public void Thenable_interop_via_Promise_resolve()
    {
        // §27.2.4.7: Promise.resolve adopts thenables — invokes .then with
        // the freshly-built resolving functions, then settles when called.
        var rt = Run(@"
            globalThis.r = 0;
            var thenable = { then: function(res, rej) { res(99); } };
            Promise.resolve(thenable).then(function(v) { globalThis.r = v; });
        ");
        rt.GetGlobal("r").AsNumber.Should().Be(99);
    }

    [TestMethod]
    public void Resolve_with_promise_returns_same_promise_identity()
    {
        // §27.2.4.7 step 1: Promise.resolve on an already-Promise returns it as-is.
        var rt = new JsRuntime();
        var program = new JsParser(@"
            var p = Promise.resolve(1);
            globalThis.same = (Promise.resolve(p) === p);
        ").ParseProgram();
        var chunk = JsCompiler.CompileForEval(program);
        new JsVm(rt).Run(chunk);
        rt.GetGlobal("same").AsBool.Should().BeTrue();
    }

    [TestMethod]
    public void Microtask_runs_after_current_sync_frame()
    {
        // Resolving the promise synchronously must NOT settle .then handlers
        // before the rest of the synchronous frame runs. We mark 'before'
        // before the drain happens.
        var rt = Run(@"
            globalThis.trace = '';
            Promise.resolve().then(function() { globalThis.trace = globalThis.trace + 'micro'; });
            globalThis.trace = globalThis.trace + 'sync';
        ");
        rt.GetGlobal("trace").AsString.Should().Be("syncmicro");
    }

    [TestMethod]
    public void Pending_promise_does_not_settle_without_resolve()
    {
        var rt = Run(@"
            globalThis.ran = 0;
            new Promise(function(res, rej) { /* never settled */ })
                .then(function() { globalThis.ran = 1; });
        ");
        rt.GetGlobal("ran").AsNumber.Should().Be(0);
    }

    [TestMethod]
    public void Resolving_with_self_rejects_with_TypeError()
    {
        var rt = Run(@"
            globalThis.reason = null;
            var d = Promise.withResolvers();
            d.resolve(d.promise);
            d.promise.catch(function(e) { globalThis.reason = e; });
        ");
        var reason = rt.GetGlobal("reason");
        reason.IsObject.Should().BeTrue();
        reason.AsObject.Get("message").AsString.Should().Contain("Chaining cycle");
    }

    [TestMethod]
    public void MicrotaskQueue_host_scheduler_takes_ownership()
    {
        // When a host scheduler is installed, in-process drain is a no-op
        // and jobs are handed off to the host. The host is responsible for
        // pumping them on its own event-loop tick. This test exercises the
        // hand-off — the host bridge in Starling.Loop wraps the JS-function
        // dispatch in a fresh VM scope, which lives outside this engine
        // assembly (see B5-2 once it lands).
        var rt = new JsRuntime();
        var captured = new List<Action>();
        rt.SetMicrotaskScheduler(captured.Add);
        rt.Realm.Microtasks.HasHostScheduler.Should().BeTrue();

        var program = new JsParser(@"
            globalThis.r = 0;
            Promise.resolve(7).then(function(v) { globalThis.r = v; });
        ").ParseProgram();
        var chunk = JsCompiler.CompileForEval(program);
        new JsVm(rt).Run(chunk);

        // Top-level Run's drain was a no-op (host owns the queue) and the
        // 'then' reaction never ran — so the global is still 0 and the host
        // is holding at least one queued job.
        rt.GetGlobal("r").AsNumber.Should().Be(0);
        captured.Should().HaveCountGreaterThan(0);

        // Reverting to the in-process drain lets us pump synchronously here
        // — but we still need an active VM scope so JS callbacks resolve. A
        // fresh `Run(empty)` reasserts ActiveVm and drains in one step.
        rt.SetMicrotaskScheduler(null);
        foreach (var job in captured)
        {
            rt.Realm.Microtasks.Enqueue(job);
        }

        captured.Clear();
        var empty = JsCompiler.CompileForEval(new JsParser("0;").ParseProgram());
        new JsVm(rt).Run(empty);
        rt.GetGlobal("r").AsNumber.Should().Be(7);
    }

    [TestMethod]
    public void Then_on_pending_promise_settles_after_external_resolve()
    {
        var rt = Run(@"
            globalThis.r = 0;
            var d = Promise.withResolvers();
            d.promise.then(function(v) { globalThis.r = v; });
            // settle in next sync line
            d.resolve(11);
        ");
        rt.GetGlobal("r").AsNumber.Should().Be(11);
    }

    // -------------------------------------------------------------- Helpers

    private static JsRuntime Run(string source)
    {
        var rt = new JsRuntime();
        var program = new JsParser(source).ParseProgram();
        var chunk = JsCompiler.CompileForEval(program);
        new JsVm(rt).Run(chunk);
        return rt;
    }
}
