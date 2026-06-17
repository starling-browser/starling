using Jint.Native;
using Jint.Runtime;
using Microsoft.Extensions.Logging;

namespace Starling.Bindings.Jint;

/// <summary>
/// HTML §"run the animation frame callbacks" for the Jint backend — installs
/// <c>requestAnimationFrame</c> / <c>cancelAnimationFrame</c> on the global,
/// routing through <see cref="JintBackendContext.Loop"/> so rAF shares the
/// simulated clock with the timers and a rAF-bootstrapped page settles on the
/// same <see cref="JintScriptSession.PumpOnce"/>.
/// </summary>
/// <remarks>
/// Mirrors <c>Starling.Bindings/AnimationFrameBinding.cs</c>: each frame the loop
/// snapshots the rAF queue and dispatches every pending callback with the same
/// <c>DOMHighResTimeStamp</c> (CSS Animations 1 §3.5); callbacks scheduled
/// <i>during</i> the drain land on the <i>next</i> frame. Errors out of a
/// callback errors are logged so the loop keeps firing the remaining callbacks in the frame.
/// </remarks>
internal static class AnimationFrameBinding
{
    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;
        var loop = ctx.Loop;

        JintInterop.DefineMethod(engine, engine.Global, "requestAnimationFrame", (_, args) =>
        {
            if (args.Length == 0 || args[0] is not global::Jint.Native.Function.Function)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError,
                    "requestAnimationFrame argument is not callable");
            }

            var handler = args[0];
            var id = loop.RequestAnimationFrame(timestamp => InvokeCallback(ctx, handler, timestamp));
            return JintInterop.Num(id);
        }, 1);

        JintInterop.DefineMethod(engine, engine.Global, "cancelAnimationFrame", (_, args) =>
        {
            if (TryCoerceId(args, out var id))
            {
                loop.CancelAnimationFrame(id);
            }

            return JsValue.Undefined;
        }, 1);
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

    private static void InvokeCallback(JintBackendContext ctx, JsValue handler, double timestamp)
    {
        var jsLog = ctx.LoggerFactory.CreateLogger("Starling.engine.js");
        try
        {
            ctx.Engine.Invoke(handler, JsValue.Undefined, new JsValue[] { JintInterop.Num(timestamp) });
            ctx.Engine.Advanced.ProcessTasks();
        }
        catch (JavaScriptException ex)
        {
            AnimationFrameBindingLog.UncaughtInAnimationFrame(jsLog,
                JintInterop.DescribeError(ex.Error, ex.Message));
        }
        catch (Exception ex)
        {
            AnimationFrameBindingLog.UncaughtInAnimationFrame(jsLog, ex.Message);
        }
    }
}

internal static partial class AnimationFrameBindingLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Uncaught (in animation frame) {Detail}")]
    public static partial void UncaughtInAnimationFrame(ILogger logger, string detail);
}
