using System.Diagnostics;
using System.Globalization;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Microsoft.Extensions.Logging;
using Starling.Js.Hosting;

namespace Starling.Bindings.Jint;

/// <summary>
/// The full <c>console</c> surface for the Jint backend. Beyond log/info/warn/
/// error/debug/trace/dir/table, this adds the methods real sites call that used to
/// throw "not a function": <c>time</c>/<c>timeEnd</c>/<c>timeLog</c>,
/// <c>count</c>/<c>countReset</c>, <c>group</c>/<c>groupCollapsed</c>/<c>groupEnd</c>,
/// <c>assert</c>, and <c>clear</c>. Output is routed through a sink so the live
/// session and the bare unit-test context share one implementation.
/// </summary>
internal static class ConsoleBinding
{
    /// <summary>Install a console for a bare context (parity tests / no session),
    /// routing output to the context logger. Idempotent — skips if a console is
    /// already installed (e.g. by the session before InstallAll).</summary>
    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.Engine.Global.HasOwnProperty("console"))
        {
            return;
        }

        var log = ctx.Log;
        Install(ctx.Engine, (level, msg) =>
        {
            switch (level)
            {
                case ConsoleLevel.Error: log.LogError("{Message}", msg); break;
                case ConsoleLevel.Warn: log.LogWarning("{Message}", msg); break;
                default: log.LogInformation("{Message}", msg); break;
            }
        });
    }

    /// <summary>Build and install the full console on <paramref name="engine"/>,
    /// writing through <paramref name="sink"/>. Overwrites any existing console.</summary>
    public static void Install(global::Jint.Engine engine, Action<ConsoleLevel, string> sink)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(sink);

        var console = new JsObject(engine);
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var timers = new Dictionary<string, Stopwatch>(StringComparer.Ordinal);
        var groupDepth = 0;

        string Indent(string s) => groupDepth > 0 ? new string(' ', groupDepth * 2) + s : s;

        void Method(string name, ConsoleLevel level)
            => JintInterop.DefineMethod(engine, console, name, (_, args) =>
            {
                sink(level, Indent(Format(args)));
                return JsValue.Undefined;
            }, 0);

        Method("log", ConsoleLevel.Log);
        Method("info", ConsoleLevel.Info);
        Method("warn", ConsoleLevel.Warn);
        Method("error", ConsoleLevel.Error);
        Method("debug", ConsoleLevel.Debug);
        Method("trace", ConsoleLevel.Trace);
        Method("dir", ConsoleLevel.Dir);
        Method("table", ConsoleLevel.Table);

        JintInterop.DefineMethod(engine, console, "assert", (_, args) =>
        {
            var cond = args.Length > 0 && TypeConverter.ToBoolean(args[0]);
            if (!cond)
            {
                var rest = args.Length > 1 ? args[1..] : System.Array.Empty<JsValue>();
                var msg = rest.Length > 0 ? "Assertion failed: " + Format(rest) : "Assertion failed";
                sink(ConsoleLevel.Error, Indent(msg));
            }
            return JsValue.Undefined;
        }, 0);

        JintInterop.DefineMethod(engine, console, "count", (_, args) =>
        {
            var label = Label(args, "default");
            counts.TryGetValue(label, out var n);
            counts[label] = ++n;
            sink(ConsoleLevel.Info, Indent($"{label}: {n}"));
            return JsValue.Undefined;
        }, 0);
        JintInterop.DefineMethod(engine, console, "countReset", (_, args) =>
        {
            counts.Remove(Label(args, "default"));
            return JsValue.Undefined;
        }, 0);

        JintInterop.DefineMethod(engine, console, "time", (_, args) =>
        {
            timers[Label(args, "default")] = Stopwatch.StartNew();
            return JsValue.Undefined;
        }, 0);
        JintInterop.DefineMethod(engine, console, "timeLog", (_, args) =>
        {
            var label = Label(args, "default");
            if (timers.TryGetValue(label, out var sw))
            {
                sink(ConsoleLevel.Info, Indent($"{label}: {sw.Elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}ms"));
            }

            return JsValue.Undefined;
        }, 0);
        JintInterop.DefineMethod(engine, console, "timeEnd", (_, args) =>
        {
            var label = Label(args, "default");
            if (timers.Remove(label, out var sw))
            {
                sink(ConsoleLevel.Info, Indent($"{label}: {sw.Elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}ms"));
            }

            return JsValue.Undefined;
        }, 0);

        JintInterop.DefineMethod(engine, console, "group", (_, args) =>
        {
            if (args.Length > 0)
            {
                sink(ConsoleLevel.Log, Indent(Format(args)));
            }

            groupDepth++;
            return JsValue.Undefined;
        }, 0);
        JintInterop.DefineMethod(engine, console, "groupCollapsed", (_, args) =>
        {
            if (args.Length > 0)
            {
                sink(ConsoleLevel.Log, Indent(Format(args)));
            }

            groupDepth++;
            return JsValue.Undefined;
        }, 0);
        JintInterop.DefineMethod(engine, console, "groupEnd", (_, _) =>
        {
            if (groupDepth > 0)
            {
                groupDepth--;
            }

            return JsValue.Undefined;
        }, 0);

        JintInterop.DefineMethod(engine, console, "clear", (_, _) => JsValue.Undefined, 0);

        JintInterop.DefineDataProp(engine.Global, "console", console, writable: true, enumerable: false, configurable: true);
    }

    private static string Label(JsValue[] args, string fallback)
        => args.Length > 0 && !args[0].IsUndefined() ? TypeConverter.ToString(args[0]) : fallback;

    private static string Format(JsValue[] args)
    {
        if (args.Length == 0)
        {
            return string.Empty;
        }

        return string.Join(" ", args.Select(a => a.IsNull() ? "null" : a.ToString()));
    }
}
