using Starling.Js.Runtime;
using Starling.Loop;

namespace Starling.Bindings;

/// <summary>
/// HTML §"run the animation frame callbacks" — installs
/// <c>requestAnimationFrame</c> and <c>cancelAnimationFrame</c> on the
/// realm's global, routing through a <see cref="WebEventLoop"/>.
/// </summary>
/// <remarks>
/// <para>Each frame, the loop snapshots the rAF queue and dispatches every
/// pending callback with the same <c>nowMs</c> timestamp (CSS Animations 1
/// §3.5). Callbacks scheduled *during* the drain land in the freshly-empty
/// queue and fire on the *next* frame, not the current one — same shape as
/// the spec's "list of animation frame callbacks" algorithm.</para>
///
/// <para>Errors thrown out of a callback are routed through
/// <see cref="JsRealm.ConsoleSink"/> at <see cref="ConsoleLevel.Error"/> so
/// the loop continues to fire remaining callbacks in the same frame —
/// matches the TimersBinding error-routing convention.</para>
/// </remarks>
public static class AnimationFrameBinding
{
    public static void Install(JsRuntime runtime, WebEventLoop loop)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(loop);

        var realm = runtime.Realm;

        var raf = new JsNativeFunction(realm, "requestAnimationFrame", 1, (_, args) =>
        {
            if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
            {
                throw new JsThrow(realm.NewTypeError("requestAnimationFrame argument is not callable"));
            }

            var handler = args[0];
            var id = loop.RequestAnimationFrame(timestamp =>
                InvokeCallback(runtime, handler, timestamp));
            return JsValue.Number(id);
        }, isConstructor: false);

        var caf = new JsNativeFunction(realm, "cancelAnimationFrame", 1, (_, args) =>
        {
            if (TryCoerceId(args, out var id))
            {
                loop.CancelAnimationFrame(id);
            }

            return JsValue.Undefined;
        }, isConstructor: false);

        DefineGlobal(realm, "requestAnimationFrame", raf);
        DefineGlobal(realm, "cancelAnimationFrame", caf);
    }

    private static void DefineGlobal(JsRealm realm, string name, JsNativeFunction fn)
    {
        realm.GlobalObject.DefineOwnProperty(name,
            PropertyDescriptor.Data(JsValue.Object(fn), writable: true, enumerable: false, configurable: true));
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

    private static void InvokeCallback(JsRuntime runtime, JsValue handler, double timestamp)
    {
        var realm = runtime.Realm;
        runtime.WithActiveVm(() =>
        {
            try
            {
                var args = new[] { JsValue.Number(timestamp) };
                if (handler.IsObject && handler.AsObject is JsFunction fn && realm.ActiveVm is { } vm)
                {
                    vm.CallFunction(fn, JsValue.Undefined, args);
                }
                else
                {
                    AbstractOperations.Call(realm.ActiveVm, handler, JsValue.Undefined, args);
                }
            }
            catch (JsThrow ex)
            {
                realm.ConsoleSink(ConsoleLevel.Error, $"Uncaught (in animation frame) {DescribeThrown(ex.Value)}");
            }
            catch (Exception ex)
            {
                realm.ConsoleSink(ConsoleLevel.Error, $"Uncaught (in animation frame) {ex.Message}");
            }
        });
    }

    private static string DescribeThrown(JsValue value)
    {
        if (value.IsObject)
        {
            var msg = value.AsObject.Get("message");
            if (!msg.IsUndefined)
            {
                return JsValue.ToStringValue(msg);
            }
        }
        return JsValue.ToStringValue(value);
    }
}
