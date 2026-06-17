// SPDX-License-Identifier: Apache-2.0
using AwesomeAssertions;
using Starling.Js.Parse;
using Starling.Js.Runtime;

namespace Starling.Js.Tests.Runtime;

/// <summary>
/// wp:M3-84 — guards the VM call-depth model. Stage A shrank the dispatch
/// loop's native frame (pure JS recursion died at ~24 frames before it);
/// Stage B took JS→JS calls off the native stack entirely (the barrier
/// trampoline), so pure JS recursion now runs to the logical MaxFrameDepth
/// cap (10,000) on any thread, and only native→JS re-entries (getters,
/// Proxy traps, …) burn native stack, bounded by MaxBarrierDepth. If either
/// regresses (a fat arm back in the dispatch loop, a call arm recursing
/// natively again), these assertions catch it.
/// </summary>
[TestClass]
public class JsRecursionDepthTests
{
    [TestMethod]
    public void Recursion_reaches_at_least_50_frames_on_the_default_test_thread()
    {
        // Pre-trampoline this probed the native-frame size (Stage A: 72 in
        // Debug). With Stage B it trivially passes — kept as a cheap canary.
        MaxDepth(64).Should().BeGreaterThanOrEqualTo(50);
    }

    [TestMethod]
    public void Recursion_reaches_at_least_200_frames_on_an_8mb_thread()
    {
        // Stage B: hits the logical MaxFrameDepth cap (10,000), thread size
        // no longer matters for pure JS→JS recursion.
        var depth = 0;
        var t = new Thread(() => depth = MaxDepth(2048), 8 * 1024 * 1024);
        t.Start();
        t.Join();
        depth.Should().BeGreaterThanOrEqualTo(200);
    }

    // ---- wp:M3-84 Stage B — barrier-trampoline acceptance ------------------

    [TestMethod]
    public void Recursion_5000_deep_succeeds_on_the_default_test_thread()
    {
        // Pure JS→JS recursion runs on heap CallFrames — 5,000 frames must
        // complete without a RangeError (pre-trampoline this died at ~24).
        Eval("function f(n){ return n<=0 ? 0 : 1+f(n-1); } f(5000);")
            .AsNumber.Should().Be(5000);
    }

    [TestMethod]
    public void Recursion_15000_deep_throws_catchable_RangeError_mentioning_call_stack()
    {
        // 15,000 frames exceeds MaxFrameDepth (10,000): a catchable RangeError
        // whose message names the call stack, never a process crash.
        var result = Eval(
            "function f(n){ return n<=0 ? 0 : 1+f(n-1); }" +
            "var msg = ''; var isRange = false;" +
            "try { f(15000); } catch (e) { msg = e.message; isRange = e instanceof RangeError; }" +
            "isRange && msg.indexOf('call stack') >= 0;");
        result.AsBool.Should().BeTrue();
    }

    [TestMethod]
    public void Native_reentrant_getter_recursion_throws_catchable_RangeError()
    {
        // getter → property read → getter… — every level is a native→JS
        // barrier re-entry, so the barrier cap (MaxBarrierDepth +
        // TryEnsureSufficientExecutionStack) must stop it with a catchable
        // RangeError while the process stays alive.
        var result = Eval(
            "var o = { get p() { return o.p; } };" +
            "var caught = false;" +
            "try { o.p; } catch (e) { caught = e instanceof RangeError; }" +
            "caught;");
        result.AsBool.Should().BeTrue();
    }

    [TestMethod]
    public void Recursion_3000_deep_inside_try_finally_runs_every_finalizer()
    {
        // Each level's finalizer must run exactly once on the way back —
        // finalizers are per-frame state and must survive the trampoline.
        var result = Eval(
            "var count = 0;" +
            "function f(n){ try { return n<=0 ? 0 : 1+f(n-1); } finally { count++; } }" +
            "var v = f(3000);" +
            "v === 3000 && count === 3001;");
        result.AsBool.Should().BeTrue();
    }

    [TestMethod]
    public void Recursion_3000_deep_through_spread_calls_succeeds()
    {
        // f(...args) compiles to the apply-call path — it must trampoline too.
        Eval("function f(n){ return n<=0 ? 0 : 1+f(...[n-1]); } f(3000);")
            .AsNumber.Should().Be(3000);
    }

    [TestMethod]
    public void Deep_unwind_from_15000_frames_runs_finalizers_in_passed_frames()
    {
        // The depth RangeError thrown at the cap must run each unwound
        // frame's finalizer (explicit unwinding walks the frame chain).
        var result = Eval(
            "var count = 0; var caught = false;" +
            "function f(n){ try { return n<=0 ? 0 : 1+f(n-1); } finally { count++; } }" +
            "try { f(15000); } catch (e) { caught = e instanceof RangeError; }" +
            "caught && count > 9000;");
        result.AsBool.Should().BeTrue();
    }

    [TestMethod]
    public void Webpack_style_module_graph_walk_2000_deep_succeeds()
    {
        // Reduced fixture for the bug that motivated wp:M3-84: x.com's
        // webpack runtime walks __webpack_require__ → factory →
        // __webpack_require__ in a deep chain of plain calls and method
        // calls. Pins the fix without needing the live site.
        var result = Eval(
            "var cache = {};" +
            "var factories = {};" +
            "for (var i = 0; i < 2000; i++) {" +
            "  (function (id) {" +
            "    factories[id] = function (req) {" +
            "      var exports = { id: id, next: 0 };" +
            "      if (id + 1 < 2000) exports.next = req.require(id + 1).id;" +
            "      return exports;" +
            "    };" +
            "  })(i);" +
            "}" +
            "var runtime = {" +
            "  require: function (id) {" +
            "    if (cache[id]) return cache[id];" +
            "    var mod = factories[id](runtime);" +
            "    cache[id] = mod;" +
            "    return mod;" +
            "  }" +
            "};" +
            "var root = runtime.require(0);" +
            "root.id === 0 && root.next === 1 && runtime.require(1999).id === 1999;");
        result.AsBool.Should().BeTrue();
    }

    [TestMethod]
    public void Construct_coercion_throw_on_barrier_pop_does_not_poison_the_array_pool()
    {
        // A derived ctor that completes without running super() throws the
        // coercion ReferenceError at pop. On a BARRIER pop (Reflect.construct,
        // new through bind/Proxy) that throw must propagate to the construct
        // site without the unwinder releasing the already-released frame —
        // a double pool return hands the same operand stack to two later
        // frames, and unrelated arithmetic afterwards goes wrong (h() was
        // NaN under the bug).
        var result = Eval(
            "class A {}" +
            "class B extends A { constructor() {} }" +
            "var caught = false;" +
            "try { Reflect.construct(B, []); } catch (e) { caught = e instanceof ReferenceError; }" +
            "function g(){ return 2; }" +
            "function h(){ return 1 + g(); }" +
            "caught && h() === 3;");
        result.AsBool.Should().BeTrue();
    }

    [TestMethod]
    public void Construct_coercion_throw_is_not_catchable_inside_the_completed_ctor_body()
    {
        // The coercion error belongs to the construct site, not the body —
        // a try/catch wrapping the ctor's return must never observe it.
        var result = Eval(
            "class A {}" +
            "class B extends A { constructor() { try { return; } catch (e) { globalThis.leaked = true; } } }" +
            "var atSite = false;" +
            "try { Reflect.construct(B, []); } catch (e) { atSite = e instanceof ReferenceError; }" +
            "atSite && !globalThis.leaked;");
        result.AsBool.Should().BeTrue();
    }

    [TestMethod]
    public void Native_reentrant_getter_recursion_reaches_a_minimum_barrier_depth()
    {
        // Pins the per-barrier native frame size. Pure JS recursion no longer
        // touches the native stack, so without this floor a fat inlined helper
        // in Dispatch could silently shrink usable barrier depth (getters,
        // Proxy traps, ToPrimitive all live on it). A fixed 8 MB thread keeps
        // the floor meaningful across Debug (fat JIT frames) and test hosts.
        var depth = 0.0;
        var t = new Thread(() => depth = Eval(
            "var count = 0;" +
            "var o = { get p() { count++; return o.p; } };" +
            "try { o.p; } catch (e) {}" +
            "count;").AsNumber, 8 * 1024 * 1024);
        t.Start();
        t.Join();
        depth.Should().BeGreaterThanOrEqualTo(200);
    }

    [TestMethod]
    public void Deep_recursion_allocates_a_bounded_operand_stack_per_frame()
    {
        // wp:M3-84 follow-up — frames rent a small operand stack
        // (InitialStackSlots) and grow on demand. With the old fixed
        // 1024-slot rent, 9,000 live frames cost ~25 KB of transient array
        // per frame (~250 MB total — the pool cannot hold that many arrays).
        // Grow-on-demand measures ~1.1 KB per frame in Debug. The 4 KB bound
        // leaves headroom for harness noise while catching any regression
        // back to big up-front rents.
        Eval("function w(n){ return n<=0 ? 0 : 1+w(n-1); } w(64);"); // warm JIT + pools
        var before = GC.GetAllocatedBytesForCurrentThread();
        Eval("function f(n){ return n<=0 ? 0 : 1+f(n-1); } f(9000);");
        var delta = GC.GetAllocatedBytesForCurrentThread() - before;
        (delta / 9001.0).Should().BeLessThan(4096);
    }

    private static JsValue Eval(string source)
    {
        var program = new JsParser(source).ParseProgram();
        var chunk = Starling.Js.Bytecode.JsCompiler.CompileForEval(program);
        return new JsVm(new JsRuntime()).Run(chunk);
    }

    /// <summary>Binary-search the largest n for which f(n) completes without
    /// the recursion RangeError.</summary>
    private static int MaxDepth(int hi)
    {
        var lo = 1;
        if (!Works(lo))
        {
            return 0;
        }

        while (lo < hi)
        {
            var mid = lo + (hi - lo + 1) / 2;
            if (Works(mid))
            {
                lo = mid;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return lo;
    }

    private static bool Works(int n)
    {
        try
        {
            var program = new JsParser(
                $"function f(n){{ return n<=0 ? 0 : 1+f(n-1); }} f({n});").ParseProgram();
            var chunk = Starling.Js.Bytecode.JsCompiler.CompileForEval(program);
            var v = new JsVm(new JsRuntime()).Run(chunk);
            return v.IsNumber && v.AsNumber == n;
        }
        catch (JsThrow)
        {
            return false;
        }
    }
}
