using Jint.Native;
using Jint.Runtime;
using Microsoft.Extensions.Logging;

namespace Starling.Bindings.Jint;

/// <summary>
/// HTML §8.7 timer surface for the Jint backend — installs <c>setTimeout</c>,
/// <c>setInterval</c>, <c>clearTimeout</c>, <c>clearInterval</c>,
/// <c>setImmediate</c>, and <c>clearImmediate</c> as global functions backed by
/// <see cref="JintBackendContext.Loop"/> (the <c>WebEventLoop</c> the session
/// advances in <see cref="JintScriptSession.PumpOnce"/>).
/// </summary>
/// <remarks>
/// Mirrors <c>Starling.Bindings/TimersBinding.cs</c>:
/// <list type="bullet">
/// <item><b>One-shot loop primitive.</b> <see cref="Starling.Loop.WebEventLoop.SetTimeout"/>
/// fires once; intervals chain themselves by enqueuing a fresh loop timer after
/// each fire while the JS-visible id stays stable. An internal interval map
/// tracks the most recent loop-side id so <c>clearInterval</c> can stop the
/// chain.</item>
/// <item><b>Extra args.</b> <c>setTimeout(fn, delay, a, b, …)</c> forwards the
/// trailing arguments to the callback.</item>
/// <item><b>Microtasks.</b> After each callback runs we drain Jint promise jobs
/// (<c>engine.Advanced.ProcessTasks()</c>) so reactions queued from inside the
/// timer settle before control returns to the loop.</item>
/// <item><b>Error routing.</b> A throwing handler is reported to
/// logged at warn level (mirroring the console error route the Starling backend uses);
/// the loop keeps running so later timers still fire.</item>
/// </list>
/// <c>setImmediate</c>/<c>clearImmediate</c> are a Node-flavoured convenience
/// scheduled as 0ms timers on the same loop (HTML has no setImmediate, but real
/// pages and bundles probe for it). They share the timer id space.
/// </remarks>
internal static class TimersBinding
{
    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;
        var loop = ctx.Loop;
        var intervals = new Dictionary<int, IntervalState>();

        // ---- setTimeout(handler, delay?, ...args) --------------------------
        JintInterop.DefineMethod(engine, engine.Global, "setTimeout", (_, args) =>
        {
            var (handler, delay, fwd) = ParseTimerArgs(engine, args);
            var id = loop.SetTimeout(() => InvokeHandler(ctx, handler, fwd), delay);
            return JintInterop.Num(id);
        }, 1);

        // ---- setInterval(handler, delay?, ...args) -------------------------
        JintInterop.DefineMethod(engine, engine.Global, "setInterval", (_, args) =>
        {
            var (handler, delay, fwd) = ParseTimerArgs(engine, args);

            // The id JS sees must remain stable across reschedules: schedule the
            // first timer, key the interval state by that id, and update the
            // current loop-side id on each reschedule so clearInterval can stop
            // the chain.
            int originalId = 0;
            Action? fire = null;
            fire = () =>
            {
                // Reschedule first so a clearInterval inside the handler can
                // cancel us before the next fire.
                if (intervals.TryGetValue(originalId, out var state))
                {
                    var nextId = loop.SetTimeout(fire!, state.Delay);
                    intervals[originalId] = state with { CurrentTimerId = nextId };
                }
                InvokeHandler(ctx, handler, fwd);
            };

            originalId = loop.SetTimeout(fire, delay);
            intervals[originalId] = new IntervalState(delay) { CurrentTimerId = originalId };
            return JintInterop.Num(originalId);
        }, 1);

        // ---- clearTimeout(id) / clearInterval(id) -------------------------
        // Per spec, the two share an id space; clearing either against an
        // interval id stops the chain.
        JsValue Clear(JsValue[] args)
        {
            if (TryCoerceId(args, out var id))
            {
                if (intervals.TryGetValue(id, out var state))
                {
                    loop.ClearTimeout(state.CurrentTimerId);
                    intervals.Remove(id);
                }
                else
                {
                    loop.ClearTimeout(id);
                }
            }
            return JsValue.Undefined;
        }

        JintInterop.DefineMethod(engine, engine.Global, "clearTimeout", (_, args) => Clear(args), 1);
        JintInterop.DefineMethod(engine, engine.Global, "clearInterval", (_, args) => Clear(args), 1);

        // ---- setImmediate(handler, ...args) / clearImmediate(id) ----------
        JintInterop.DefineMethod(engine, engine.Global, "setImmediate", (_, args) =>
        {
            var handler = args.Length > 0 ? args[0] : JsValue.Undefined;
            if (!IsCallable(handler))
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "setImmediate handler is not callable");
            }

            var fwd = Forwarded(args, 1);
            var id = loop.SetTimeout(() => InvokeHandler(ctx, handler, fwd), 0);
            return JintInterop.Num(id);
        }, 1);

        JintInterop.DefineMethod(engine, engine.Global, "clearImmediate", (_, args) =>
        {
            if (TryCoerceId(args, out var id))
            {
                loop.ClearTimeout(id);
            }

            return JsValue.Undefined;
        }, 1);

        // ---- queueMicrotask(callback) -------------------------------------
        // HTML §8.6. Runs the callback as a microtask (before the next task).
        // Modern frameworks use it directly as a scheduler primitive (e.g.
        // React's "tick" scheduler: `type==="tick" ? queueMicrotask : …`), so a
        // missing global silently breaks deferred rendering rather than throwing.
        JintInterop.DefineMethod(engine, engine.Global, "queueMicrotask", (_, args) =>
        {
            var handler = args.Length > 0 ? args[0] : JsValue.Undefined;
            if (!IsCallable(handler))
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "queueMicrotask handler is not callable");
            }

            loop.QueueMicrotask(() => InvokeHandler(ctx, handler, Array.Empty<JsValue>()));
            return JsValue.Undefined;
        }, 1);

        // ---- requestIdleCallback / cancelIdleCallback ---------------------
        // No real idle periods in a one-shot render, so we schedule on the timer
        // loop and hand the callback an IdleDeadline that always reports time
        // remaining (didTimeout:false). Shares the timer id space.
        JintInterop.DefineMethod(engine, engine.Global, "requestIdleCallback", (_, args) =>
        {
            var handler = args.Length > 0 ? args[0] : JsValue.Undefined;
            if (!IsCallable(handler))
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "requestIdleCallback handler is not callable");
            }

            var deadline = new JsObject(engine);
            JintInterop.DefineDataProp(deadline, "didTimeout", JsBoolean.False);
            JintInterop.DefineMethod(engine, deadline, "timeRemaining", (_, _) => JintInterop.Num(50), 0);
            var id = loop.SetTimeout(() => InvokeHandler(ctx, handler, new JsValue[] { deadline }), 1);
            return JintInterop.Num(id);
        }, 1);

        JintInterop.DefineMethod(engine, engine.Global, "cancelIdleCallback", (_, args) =>
        {
            if (TryCoerceId(args, out var id))
            {
                loop.ClearTimeout(id);
            }

            return JsValue.Undefined;
        }, 1);
    }

    private static (JsValue Handler, int Delay, JsValue[] Args) ParseTimerArgs(global::Jint.Engine engine, JsValue[] args)
    {
        var handler = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (!IsCallable(handler))
        {
            throw new JavaScriptException(engine.Intrinsics.TypeError, "Timer handler is not callable");
        }

        var delay = 0;
        if (args.Length > 1)
        {
            var d = TypeConverter.ToNumber(args[1]);
            if (double.IsNaN(d) || d < 0)
            {
                delay = 0;
            }
            else if (d > int.MaxValue)
            {
                delay = int.MaxValue;
            }
            else
            {
                delay = (int)d;
            }
        }

        return (handler, delay, Forwarded(args, 2));
    }

    private static JsValue[] Forwarded(JsValue[] args, int from)
    {
        if (args.Length <= from)
        {
            return Array.Empty<JsValue>();
        }

        var forwarded = new JsValue[args.Length - from];
        Array.Copy(args, from, forwarded, 0, forwarded.Length);
        return forwarded;
    }

    private static bool TryCoerceId(JsValue[] args, out int id)
    {
        id = 0;
        if (args.Length == 0)
        {
            return false;
        }

        var n = TypeConverter.ToNumber(args[0]);
        if (double.IsNaN(n) || double.IsInfinity(n))
        {
            return false;
        }

        if (n < int.MinValue || n > int.MaxValue)
        {
            return false;
        }

        id = (int)n;
        return true;
    }

    private static bool IsCallable(JsValue v) => v is global::Jint.Native.Function.Function;

    private static void InvokeHandler(JintBackendContext ctx, JsValue handler, JsValue[] args)
    {
        var jsLog = ctx.LoggerFactory.CreateLogger("Starling.engine.js");
        try
        {
            ctx.Engine.Invoke(handler, JsValue.Undefined, args);
            ctx.Engine.Advanced.ProcessTasks();
        }
        catch (JavaScriptException ex)
        {
            TimersBindingLog.UncaughtInTimer(jsLog, JintInterop.DescribeError(ex.Error, ex.Message));
        }
        catch (Exception ex)
        {
            TimersBindingLog.UncaughtInTimer(jsLog, ex.Message);
        }
    }

    private readonly record struct IntervalState(int Delay)
    {
        public int CurrentTimerId { get; init; }
    }
}

internal static partial class TimersBindingLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Uncaught (in timer) {Detail}")]
    public static partial void UncaughtInTimer(ILogger logger, string detail);
}
