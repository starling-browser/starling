using Starling.Js.Runtime;
using Starling.Loop;

namespace Starling.Bindings;

/// <summary>
/// HTML §8.7 timer surface — installs <c>setTimeout</c>, <c>setInterval</c>,
/// <c>clearTimeout</c>, and <c>clearInterval</c> as global functions backed by
/// a <see cref="WebEventLoop"/>.
/// </summary>
/// <remarks>
/// <para><b>One-shot semantics.</b> <see cref="WebEventLoop.SetTimeout"/> is a
/// one-shot scheduler. Intervals chain themselves by enqueuing a fresh timer
/// after each fire; the original id returned to JS stays stable, and an
/// internal <see cref="IntervalState"/> map tracks the most recent loop-side
/// timer id so <c>clearInterval</c> can stop the chain.</para>
///
/// <para><b>Microtasks.</b> After each timer callback runs we drain
/// microtasks via <see cref="JsRuntime.DrainMicrotasks"/> so promise
/// reactions queued from inside the timer settle before control returns to
/// the loop — the same pattern <see cref="JsVm.Run"/> uses at the bottom of
/// every top-level script execution.</para>
///
/// <para><b>Error routing.</b> If a timer handler throws, the throw is
/// routed through <see cref="JsRealm.ConsoleSink"/> at
/// <see cref="ConsoleLevel.Error"/> rather than propagated to the host. The
/// loop continues running so subsequent timers still fire.</para>
///
/// <para><b>Divergence from WHATWG.</b> Per spec, a non-callable
/// <c>handler</c> is coerced to a string and evaluated; we throw
/// <see cref="JsThrow"/> with a TypeError to match Node-flavored semantics.
/// Document this divergence in the page-init handoff once we encounter
/// real-world reliance on the string-eval form.</para>
/// </remarks>
public static class TimersBinding
{
    public static void Install(JsRuntime runtime, WebEventLoop loop)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(loop);

        var realm = runtime.Realm;
        var intervals = new Dictionary<int, IntervalState>();

        // ---- setTimeout(handler, delay?, ...args) --------------------------
        var setTimeout = new JsNativeFunction(realm, "setTimeout", 1, (_, args) =>
        {
            var (handler, delay, fwdArgs) = ParseTimerArgs(realm, args);
            var id = loop.SetTimeout(() => InvokeHandler(runtime, handler, fwdArgs), delay);
            return JsValue.Number(id);
        }, isConstructor: false);

        // ---- setInterval(handler, delay?, ...args) -------------------------
        var setInterval = new JsNativeFunction(realm, "setInterval", 1, (_, args) =>
        {
            var (handler, delay, fwdArgs) = ParseTimerArgs(realm, args);

            // We need the original id JS sees to remain stable across reschedules.
            // Schedule the first timer, then key the state by that id and update
            // the CurrentTimerId on each rescheduling so clearInterval can stop
            // the chain.
            int originalId = 0;
            Action? fire = null;
            fire = () =>
            {
                // Reschedule first so a clearInterval inside the handler can
                // cancel us. Update CurrentTimerId atomically before invoking.
                if (intervals.TryGetValue(originalId, out var state))
                {
                    var nextId = loop.SetTimeout(fire!, state.Delay);
                    intervals[originalId] = state with { CurrentTimerId = nextId };
                }
                InvokeHandler(runtime, handler, fwdArgs);
            };

            originalId = loop.SetTimeout(fire, delay);
            intervals[originalId] = new IntervalState(originalId, delay, handler, fwdArgs);
            return JsValue.Number(originalId);
        }, isConstructor: false);

        // ---- clearTimeout(id) ---------------------------------------------
        var clearTimeout = new JsNativeFunction(realm, "clearTimeout", 1, (_, args) =>
        {
            if (TryCoerceId(args, out var id))
            {
                // A clearTimeout against an interval id should also stop the chain.
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
        }, isConstructor: false);

        // ---- clearInterval(id) --------------------------------------------
        var clearInterval = new JsNativeFunction(realm, "clearInterval", 1, (_, args) =>
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
                    // Per spec, clearInterval and clearTimeout share an id space;
                    // also forward to the loop in case a non-interval timer was
                    // mistakenly cleared via clearInterval.
                    loop.ClearTimeout(id);
                }
            }
            return JsValue.Undefined;
        }, isConstructor: false);

        // ---- queueMicrotask(callback) — HTML §8.5 ---------------------------
        // Enqueues directly on the realm's microtask queue (not the timer
        // wheel): the callback runs at the current drain, before any timer.
        var queueMicrotask = new JsNativeFunction(realm, "queueMicrotask", 1, (_, args) =>
        {
            var cb = args.Length > 0 ? args[0] : JsValue.Undefined;
            if (!AbstractOperations.IsCallable(cb))
            {
                throw new JsThrow(realm.NewTypeError("queueMicrotask callback is not callable"));
            }

            realm.Microtasks.Enqueue(() => InvokeHandler(runtime, cb, Array.Empty<JsValue>()));
            return JsValue.Undefined;
        }, isConstructor: false);

        DefineGlobal(realm, "setTimeout", setTimeout);
        DefineGlobal(realm, "setInterval", setInterval);
        DefineGlobal(realm, "clearTimeout", clearTimeout);
        DefineGlobal(realm, "clearInterval", clearInterval);
        DefineGlobal(realm, "queueMicrotask", queueMicrotask);
    }

    private static void DefineGlobal(JsRealm realm, string name, JsNativeFunction fn)
    {
        realm.GlobalObject.DefineOwnProperty(name,
            PropertyDescriptor.Data(JsValue.Object(fn), writable: true, enumerable: false, configurable: true));
    }

    private static (JsValue Handler, int Delay, JsValue[] Args) ParseTimerArgs(JsRealm realm, JsValue[] args)
    {
        var handler = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (!AbstractOperations.IsCallable(handler))
        {
            throw new JsThrow(realm.NewTypeError("Timer handler is not callable"));
        }

        var delay = 0;
        if (args.Length > 1)
        {
            var d = JsValue.ToNumber(args[1]);
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

        JsValue[] forwarded;
        if (args.Length > 2)
        {
            forwarded = new JsValue[args.Length - 2];
            Array.Copy(args, 2, forwarded, 0, forwarded.Length);
        }
        else
        {
            forwarded = Array.Empty<JsValue>();
        }

        return (handler, delay, forwarded);
    }

    private static bool TryCoerceId(JsValue[] args, out int id)
    {
        id = 0;
        if (args.Length == 0)
        {
            return false;
        }

        var n = JsValue.ToNumber(args[0]);
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

    private static void InvokeHandler(JsRuntime runtime, JsValue handler, JsValue[] args)
    {
        var realm = runtime.Realm;
        // WithActiveVm publishes realm.ActiveVm for the duration of the body
        // (and the trailing microtask drain), so Promise reactions and other
        // intrinsics that depend on ActiveVm can dispatch JS callables.
        runtime.WithActiveVm(() =>
        {
            try
            {
                InvokeViaVm(realm.ActiveVm, handler, args);
            }
            catch (JsThrow ex)
            {
                realm.ConsoleSink(ConsoleLevel.Error, $"Uncaught (in timer) {DescribeThrown(ex.Value)}");
            }
            catch (Exception ex)
            {
                realm.ConsoleSink(ConsoleLevel.Error, $"Uncaught (in timer) {ex.Message}");
            }
        });
    }

    /// <summary>Best-effort string form of a thrown JS value for the console
    /// sink. Mirrors how `Uncaught (in promise)` formats reasons today —
    /// prefer the object's <c>message</c> property when present, fall back
    /// to ToString.</summary>
    private static string DescribeThrown(JsValue value)
    {
        if (value.IsObject)
        {
            // Prefer the full stack (message + frames) when the throw carried
            // one — a bare message is useless for minified-bundle failures.
            var stack = value.AsObject.Get("stack");
            if (stack.IsString)
            {
                return stack.AsString;
            }

            var msg = value.AsObject.Get("message");
            if (!msg.IsUndefined)
            {
                return JsValue.ToStringValue(msg);
            }
        }
        return JsValue.ToStringValue(value);
    }

    /// <summary>Dispatch through the VM when possible so JsRealm.ActiveVm is
    /// republished for the duration of the call (Promise reactions, etc. need
    /// it). For JsFunction handlers we call <see cref="JsVm.CallFunction"/>
    /// directly — that path enters Run, which publishes ActiveVm. For native
    /// or bound functions we fall through to AbstractOperations.Call.</summary>
    private static void InvokeViaVm(JsVm? vm, JsValue handler, JsValue[] args)
    {
        if (vm is not null && handler.IsObject && handler.AsObject is JsFunction fn)
        {
            vm.CallFunction(fn, JsValue.Undefined, args);
            return;
        }
        AbstractOperations.Call(vm, handler, JsValue.Undefined, args);
    }

    private readonly record struct IntervalState(int OriginalId, int Delay, JsValue Handler, JsValue[] Args)
    {
        public int CurrentTimerId { get; init; } = OriginalId;
    }
}
