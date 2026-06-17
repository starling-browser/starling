using System.Diagnostics;
using System.Globalization;
using System.Text;
using Starling.Js.Runtime;

namespace Starling.Js.Intrinsics;

/// <summary>
/// WHATWG Console Standard. Installs the global <c>console</c> namespace object.
/// </summary>
public static class ConsoleObj
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var console = new JsObject(realm.ObjectPrototype);

        // WHATWG Console Standard: log/info/warn/error/debug all take rest args
        // only, so spec length === 0.
        IntrinsicHelpers.DefineMethod(realm, console, "log", 0, (thisV, args) => { Write(realm, ConsoleLevel.Log, args); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "info", 0, (thisV, args) => { Write(realm, ConsoleLevel.Info, args); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "warn", 0, (thisV, args) => { Write(realm, ConsoleLevel.Warn, args); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "error", 0, (thisV, args) => { Write(realm, ConsoleLevel.Error, args); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "debug", 0, (thisV, args) => { Write(realm, ConsoleLevel.Debug, args); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "dir", 1, (thisV, args) => { Dir(realm, args); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "table", 1, (thisV, args) => { Table(realm, args); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "time", 0, (thisV, args) => { Time(realm, args); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "timeEnd", 0, (thisV, args) => { TimeEnd(realm, args); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "count", 0, (thisV, args) => { Count(realm, args); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "countReset", 0, (thisV, args) => { CountReset(realm, args); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "group", 0, (thisV, args) => { Group(realm, args); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "groupCollapsed", 0, (thisV, args) => { Group(realm, args); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "groupEnd", 0, (thisV, args) => { GroupEnd(realm); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "trace", 0, (thisV, args) => { Trace(realm, args); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "assert", 1, (thisV, args) => { Assert(realm, args); return JsValue.Undefined; });
        IntrinsicHelpers.DefineMethod(realm, console, "clear", 0, (thisV, args) => { realm.ConsoleClear?.Invoke(); return JsValue.Undefined; });

        realm.GlobalObject.DefineOwnProperty("console",
            PropertyDescriptor.Data(JsValue.Object(console), writable: true, enumerable: false, configurable: true));
    }

    // Console Standard §2.1 logging: shared formatting + host sink dispatch.
    private static void Write(JsRealm realm, ConsoleLevel level, JsValue[] args)
        => Emit(realm, level, FormatArgs(args));

    private static void Emit(JsRealm realm, ConsoleLevel level, string message)
    {
        var indent = realm.ConsoleGroupDepth <= 0 ? string.Empty : new string(' ', realm.ConsoleGroupDepth * 2);
        realm.ConsoleSink(level, indent + message);
    }

    private static void Dir(JsRealm realm, JsValue[] args)
    {
        var value = args.Length > 0 ? args[0] : JsValue.Undefined;
        Emit(realm, ConsoleLevel.Dir, FormatValue(value, objectLiteralString: true, new HashSet<JsObject>()));
    }

    private static void Table(JsRealm realm, JsValue[] args)
    {
        var value = args.Length > 0 ? args[0] : JsValue.Undefined;
        Emit(realm, ConsoleLevel.Table, FormatTable(value));
    }

    private static void Time(JsRealm realm, JsValue[] args)
    {
        var label = Label(args);
        realm.ConsoleTimers[label] = Stopwatch.StartNew();
    }

    private static void TimeEnd(JsRealm realm, JsValue[] args)
    {
        var label = Label(args);
        if (!realm.ConsoleTimers.Remove(label, out var sw))
        {
            Emit(realm, ConsoleLevel.Warn, $"{label}: no such label");
            return;
        }

        Emit(realm, ConsoleLevel.Info,
            $"{label}: {sw.Elapsed.TotalMilliseconds.ToString("0.###", CultureInfo.InvariantCulture)}ms");
    }

    private static void Count(JsRealm realm, JsValue[] args)
    {
        var label = Label(args);
        realm.ConsoleCounts.TryGetValue(label, out var count);
        count++;
        realm.ConsoleCounts[label] = count;
        Emit(realm, ConsoleLevel.Info, $"{label}: {count.ToString(CultureInfo.InvariantCulture)}");
    }

    private static void CountReset(JsRealm realm, JsValue[] args)
    {
        realm.ConsoleCounts[Label(args)] = 0;
    }

    private static void Group(JsRealm realm, JsValue[] args)
    {
        if (args.Length > 0)
        {
            Emit(realm, ConsoleLevel.Log, FormatArgs(args));
        }

        realm.ConsoleGroupDepth++;
    }

    private static void GroupEnd(JsRealm realm)
    {
        if (realm.ConsoleGroupDepth > 0)
        {
            realm.ConsoleGroupDepth--;
        }
    }

    private static void Trace(JsRealm realm, JsValue[] args)
    {
        var label = FormatArgs(args);
        var prefix = string.IsNullOrEmpty(label) ? "Trace" : label;
        Emit(realm, ConsoleLevel.Trace, prefix + Environment.NewLine + "  at <js>");
    }

    private static void Assert(JsRealm realm, JsValue[] args)
    {
        var condition = args.Length > 0 ? args[0] : JsValue.Undefined;
        if (JsValue.ToBoolean(condition))
        {
            return;
        }

        var rest = args.Length <= 1 ? Array.Empty<JsValue>() : args[1..];
        var suffix = rest.Length == 0 ? string.Empty : " " + FormatArgs(rest);
        Emit(realm, ConsoleLevel.Error, "Assertion failed:" + suffix);
    }

    // Console Standard §2.2.1 Formatter: consume % substitutions from first string.
    private static string FormatArgs(JsValue[] args)
    {
        if (args.Length == 0)
        {
            return string.Empty;
        }

        if (!args[0].IsString)
        {
            return JoinArgs(args, 0);
        }

        var fmt = args[0].AsString;
        var sb = new StringBuilder();
        var argIndex = 1;
        for (var i = 0; i < fmt.Length; i++)
        {
            var ch = fmt[i];
            if (ch != '%' || i + 1 >= fmt.Length)
            {
                sb.Append(ch);
                continue;
            }

            var spec = fmt[++i];
            if (spec == '%')
            {
                sb.Append('%');
                continue;
            }
            if (argIndex >= args.Length)
            {
                sb.Append('%').Append(spec);
                continue;
            }

            var arg = args[argIndex++];
            sb.Append(spec switch
            {
                's' => JsValue.ToStringValue(arg),
                'd' => FormatNumber(JsValue.ToNumber(arg), integer: false),
                'i' => FormatNumber(JsValue.ToNumber(arg), integer: true),
                'f' => FormatNumber(JsValue.ToNumber(arg), integer: false),
                'o' or 'O' or 'j' => FormatValue(arg, objectLiteralString: true, new HashSet<JsObject>()),
                _ => "%" + spec + FormatValue(arg, objectLiteralString: false, new HashSet<JsObject>()),
            });
        }

        if (argIndex < args.Length)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(JoinArgs(args, argIndex));
        }
        return sb.ToString();
    }

    private static string JoinArgs(JsValue[] args, int start)
    {
        var sb = new StringBuilder();
        for (var i = start; i < args.Length; i++)
        {
            if (i > start)
            {
                sb.Append(' ');
            }

            sb.Append(FormatValue(args[i], objectLiteralString: false, new HashSet<JsObject>()));
        }
        return sb.ToString();
    }

    private static string FormatValue(JsValue value, bool objectLiteralString, HashSet<JsObject> seen)
    {
        if (!value.IsObject)
        {
            if (objectLiteralString && value.IsString)
            {
                return Quote(value.AsString);
            }

            return JsValue.ToStringValue(value);
        }

        var obj = value.AsObject;
        if (obj is JsNativeFunction fn)
        {
            return fn.ToString();
        }

        if (!seen.Add(obj))
        {
            return "[Circular]";
        }

        var parts = new List<string>();
        foreach (var key in obj.EnumerableKeys())
        {
            parts.Add(key + ": " + FormatValue(obj.Get(key), objectLiteralString: true, seen));
        }

        seen.Remove(obj);
        return "{ " + string.Join(", ", parts) + " }";
    }

    private static string FormatTable(JsValue value)
    {
        if (!value.IsObject)
        {
            return FormatValue(value, objectLiteralString: false, new HashSet<JsObject>());
        }

        var rows = new List<(string Key, string Value)>();
        foreach (var key in value.AsObject.EnumerableKeys())
        {
            rows.Add((key, FormatValue(value.AsObject.Get(key), objectLiteralString: true, new HashSet<JsObject>())));
        }

        if (rows.Count == 0)
        {
            return "(empty)";
        }

        var keyWidth = Math.Max("(index)".Length, rows.Max(r => r.Key.Length));
        var valueWidth = Math.Max("Value".Length, rows.Max(r => r.Value.Length));
        var sb = new StringBuilder();
        sb.Append("(index)".PadRight(keyWidth)).Append(" | ").Append("Value".PadRight(valueWidth));
        foreach (var row in rows)
        {
            sb.AppendLine().Append(row.Key.PadRight(keyWidth)).Append(" | ").Append(row.Value.PadRight(valueWidth));
        }

        return sb.ToString();
    }

    private static string FormatNumber(double value, bool integer)
    {
        if (integer && !double.IsNaN(value) && !double.IsInfinity(value))
        {
            value = Math.Truncate(value);
        }

        return JsValue.ToStringValue(JsValue.Number(value));
    }

    private static string Label(JsValue[] args)
        => args.Length == 0 ? "default" : JsValue.ToStringValue(args[0]);

    private static string Quote(string value)
        => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
}
