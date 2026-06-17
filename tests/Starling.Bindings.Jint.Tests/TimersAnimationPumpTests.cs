using AwesomeAssertions;
using Jint;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Html;
using Starling.Js.Hosting;
using Starling.Loop;

namespace Starling.Bindings.Jint.Tests;

/// <summary>
/// J3a — timers (setTimeout/setInterval/setImmediate), requestAnimationFrame, the
/// event-loop pump, and the cross-thread <c>Post</c> hook. Timer/rAF behaviour is
/// driven over a bare <see cref="JintBackendContext"/> (mirroring the other Jint
/// binding tests); the pump + Post drain is exercised both directly on the context
/// and end-to-end through a real <see cref="JintScriptSession"/>.
/// </summary>
[TestClass]
public sealed class TimersAnimationPumpTests
{
    // ---- timers over the loop ----

    [TestMethod]
    public void SetTimeout_fires_after_pump_loop()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        ctx.Engine.Execute("""
            globalThis.fired = false;
            setTimeout(() => { globalThis.fired = true; }, 25);
            """);

        // Not fired before the clock advances past the delay.
        ctx.Engine.Evaluate("fired").AsBoolean().Should().BeFalse();
        ctx.Loop.PendingTimerCount.Should().Be(1);

        PumpLoop(ctx, () => ctx.Engine.Evaluate("fired").AsBoolean());

        ctx.Engine.Evaluate("fired").AsBoolean().Should().BeTrue();
        ctx.Loop.PendingTimerCount.Should().Be(0);
    }

    [TestMethod]
    public void SetTimeout_forwards_extra_arguments()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        ctx.Engine.Execute("""
            globalThis.sum = 0;
            setTimeout((a, b) => { globalThis.sum = a + b; }, 0, 3, 4);
            """);
        PumpLoop(ctx, () => ctx.Engine.Evaluate("sum").AsNumber() != 0);

        ctx.Engine.Evaluate("sum").AsNumber().Should().Be(7);
    }

    [TestMethod]
    public void ClearTimeout_cancels_pending_timer()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        ctx.Engine.Execute("""
            globalThis.fired = false;
            const id = setTimeout(() => { globalThis.fired = true; }, 25);
            clearTimeout(id);
            """);

        ctx.Loop.PendingTimerCount.Should().Be(0);
        // Advance well past the would-be delay; nothing should fire.
        for (var i = 0; i < 5; i++)
        {
            ctx.Loop.AdvanceBy(50);
        }

        ctx.Engine.Evaluate("fired").AsBoolean().Should().BeFalse();
    }

    [TestMethod]
    public void SetInterval_repeats_then_clearInterval_stops_the_chain()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        ctx.Engine.Execute("""
            globalThis.count = 0;
            globalThis.id = setInterval(() => {
                globalThis.count++;
                if (globalThis.count >= 3) clearInterval(globalThis.id);
            }, 10);
            """);

        // Drive the loop until the interval cancels itself (or a safety bound).
        for (var i = 0; i < 50 && ctx.Loop.PendingTimerCount > 0; i++)
        {
            ctx.Loop.AdvanceBy(10);
        }

        ctx.Engine.Evaluate("count").AsNumber().Should().Be(3);
        ctx.Loop.PendingTimerCount.Should().Be(0, "clearInterval must stop the reschedule chain");
    }

    [TestMethod]
    public void SetImmediate_runs_on_the_next_pump()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        ctx.Engine.Execute("""
            globalThis.ran = false;
            setImmediate(() => { globalThis.ran = true; });
            """);

        ctx.Engine.Evaluate("ran").AsBoolean().Should().BeFalse();
        PumpLoop(ctx, () => ctx.Engine.Evaluate("ran").AsBoolean());
        ctx.Engine.Evaluate("ran").AsBoolean().Should().BeTrue();
    }

    // ---- requestAnimationFrame ----

    [TestMethod]
    public void RequestAnimationFrame_fires_with_a_timestamp()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        ctx.Engine.Execute("""
            globalThis.ts = -1;
            requestAnimationFrame((t) => { globalThis.ts = t; });
            """);

        ctx.Loop.PendingAnimationFrameCount.Should().Be(1);
        ctx.Engine.Evaluate("ts").AsNumber().Should().Be(-1);

        ctx.Loop.AdvanceBy(16); // one frame
        ctx.Engine.Advanced.ProcessTasks();

        ctx.Engine.Evaluate("ts").AsNumber().Should().BeGreaterThanOrEqualTo(0);
        ctx.Loop.PendingAnimationFrameCount.Should().Be(0);
    }

    [TestMethod]
    public void CancelAnimationFrame_prevents_the_callback()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        ctx.Engine.Execute("""
            globalThis.fired = false;
            const id = requestAnimationFrame(() => { globalThis.fired = true; });
            cancelAnimationFrame(id);
            """);

        ctx.Loop.AdvanceBy(16);
        ctx.Engine.Advanced.ProcessTasks();
        ctx.Engine.Evaluate("fired").AsBoolean().Should().BeFalse();
    }

    // ---- ordering: microtask (Promise) before macrotask (timer) ----

    [TestMethod]
    public void Promise_microtask_runs_before_a_zero_delay_timer()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        ctx.Engine.Execute("""
            globalThis.order = [];
            setTimeout(() => { globalThis.order.push('timeout'); }, 0);
            Promise.resolve().then(() => { globalThis.order.push('promise'); });
            """);

        // Drain microtasks first (promise), then advance the clock (timer).
        ctx.Engine.Advanced.ProcessTasks();
        PumpLoop(ctx, () => ctx.Engine.Evaluate("order.length").AsNumber() >= 2);

        ctx.Engine.Evaluate("order.join(',')").AsString().Should().Be("promise,timeout");
    }

    // ---- the cross-thread Post hook + bare-context drain ----

    [TestMethod]
    public void Post_default_queue_defers_and_DrainPosted_runs_on_the_calling_thread()
    {
        var ctx = NewContext();
        var drainThread = -1;
        var ranInline = true;

        // Post from a BACKGROUND thread; the action must NOT run there.
        var t = new Thread(() => ctx.Post(() =>
        {
            drainThread = Environment.CurrentManagedThreadId;
        }));
        t.Start();
        t.Join();

        // Nothing ran yet on any thread (the action sits in the queue).
        ctx.HasPosted.Should().BeTrue();

        var callerThread = Environment.CurrentManagedThreadId;
        ranInline = ctx.DrainPosted();
        ranInline.Should().BeTrue();
        drainThread.Should().Be(callerThread, "DrainPosted runs the action on the calling (JS) thread");
        ctx.HasPosted.Should().BeFalse();
    }

    // ---- the session pump: PumpOnce drains the Post queue + reports idleness ----

    [TestMethod]
    public void Session_PumpOnce_fires_timers_and_reports_idle_when_quiescent()
    {
        var logs = new List<string>();
        using var session = NewSession(logs);

        session.RunClassicScript("setTimeout(() => console.log('tick'), 30);", "<t>");

        // Pump until the timer fires; PumpOnce reports not-idle while pending.
        var iterations = PumpSession(session, () => logs.Contains("tick"));

        logs.Should().Contain("tick");
        iterations.Should().BeLessThan(100);

        // Once everything settled, PumpOnce must report idle.
        session.PumpOnce().Should().BeFalse("nothing is pending after the timer fired");
    }

    [TestMethod]
    public void Session_PumpOnce_delivers_a_posted_completion_on_the_js_thread()
    {
        var logs = new List<string>();
        using var session = NewSession(logs);

        // fetch() settles its completion through ctx.Post; PumpOnce must drain it
        // on the JS thread so the .then() reaction runs and logs.
        const string dataUrl = "data:text/plain;base64,aGk="; // "hi"
        session.RunClassicScript(
            $"fetch('{dataUrl}').then(r => r.text()).then(t => console.log('got:' + t));", "<f>");

        PumpSession(session, () => logs.Exists(l => l.StartsWith("got:")));

        logs.Should().Contain("got:hi");
    }

    // ---- helpers ----

    private static void PumpLoop(JintBackendContext ctx, System.Func<bool> done)
    {
        for (var i = 0; i < 100; i++)
        {
            ctx.Engine.Advanced.ProcessTasks();
            if (ctx.DrainPosted())
            {
                ctx.Engine.Advanced.ProcessTasks();
            }

            if (done())
            {
                return;
            }

            if (ctx.Loop.PendingTimerCount > 0 || ctx.Loop.PendingAnimationFrameCount > 0)
            {
                ctx.Loop.AdvanceBy(10);
            }
            else if (!ctx.HasPosted)
            {
                return;
            }
        }
    }

    // Drive a real session's pump until done or a safety bound; returns the
    // iteration count. Sleeps briefly between idle iterations so a background
    // HTTP/data task can land its posted completion.
    private static int PumpSession(JintScriptSession session, System.Func<bool> done)
    {
        for (var i = 0; i < 200; i++)
        {
            var notIdle = session.PumpOnce();
            if (done())
            {
                return i;
            }

            if (!notIdle)
            {
                Thread.Sleep(1);
            }
        }
        return 200;
    }

    private static JintBackendContext NewContext(string baseUrl = "https://example.test/")
    {
        var doc = HtmlParser.Parse("<!doctype html><html><body></body></html>");
        var url = global::Starling.Url.UrlParser.Parse(baseUrl).Value;
        var engine = new global::Jint.Engine(o => o.AllowClr());
        var http = new Starling.Net.StarlingHttpClient();
        return new JintBackendContext(
            engine: engine,
            document: doc,
            baseUrl: url,
            http: http,
            loggerFactory: NullLoggerFactory.Instance,
            loop: new WebEventLoop(),
            layoutHost: null,
            fetch: (_, _) => System.Threading.Tasks.Task.FromResult<string?>(null));
    }

    private static JintScriptSession NewSession(List<string> logs, string baseUrl = "https://example.test/")
    {
        var doc = HtmlParser.Parse("<!doctype html><html><body></body></html>");
        var url = global::Starling.Url.UrlParser.Parse(baseUrl).Value;
        var http = new Starling.Net.StarlingHttpClient();
        var options = new ScriptSessionOptions(
            Document: doc,
            BaseUrl: url,
            Fetcher: (_, _) => System.Threading.Tasks.Task.FromResult<string?>(null),
            Http: http,
            LayoutHost: null,
            LoggerFactory: NullLoggerFactory.Instance);
        var session = new JintScriptSession(options)
        {
            ConsoleSink = (_, msg) => logs.Add(msg),
        };
        return session;
    }
}
