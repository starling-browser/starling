using Jint;
using Jint.Native;
using Jint.Native.Object;
using Starling.Css.TypedOm;

namespace Starling.Bindings.Jint;

/// <summary>
/// The <c>window.CSS</c> namespace + <c>CSSStyleValue</c> global. Exposes the
/// Starling CSS Typed OM value model (CSS Typed OM 1) and the <c>@property</c>
/// registration API (CSS Properties and Values API 1) to scripts. This is pure
/// model exposure: the numeric factories and <see cref="CssStyleValue.Parse"/>
/// reuse <c>Starling.Css.TypedOm</c>, and <c>registerProperty</c> reuses the
/// same descriptor-validity rules as the <c>@property</c> at-rule parser.
/// </summary>
internal static class CssBinding
{
    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;
        var global = engine.Global;

        var css = new JsObject(engine);

        // CSS Typed OM 1 §4.1 — numeric value factories. Each returns a
        // CSSUnitValue-shaped object {value, unit, toString()}.
        DefineUnitFactory(engine, css, "number", "number");
        DefineUnitFactory(engine, css, "px", "px");
        DefineUnitFactory(engine, css, "percent", "%");
        DefineUnitFactory(engine, css, "em", "em");
        DefineUnitFactory(engine, css, "rem", "rem");
        DefineUnitFactory(engine, css, "vw", "vw");
        DefineUnitFactory(engine, css, "vh", "vh");
        DefineUnitFactory(engine, css, "deg", "deg");
        DefineUnitFactory(engine, css, "s", "s");
        DefineUnitFactory(engine, css, "ms", "ms");

        // CSSOM §2.1 — CSS.escape(ident).
        JintInterop.DefineMethod(engine, css, "escape", (_, args) =>
            JintInterop.Str(CssEscape(args.Length > 0 ? args[0].ToString() : string.Empty)), 1);

        // CSS Properties and Values API 1 §3 — CSS.registerProperty(definition).
        // Validates the descriptor and rejects duplicates; a per-document set
        // tracks names registered in this session.
        var registered = new HashSet<string>(StringComparer.Ordinal);
        JintInterop.DefineMethod(engine, css, "registerProperty", (_, args) =>
        {
            if (args.Length < 1 || args[0] is not ObjectInstance def)
            {
                throw TypeErr(engine, "registerProperty requires a descriptor object");
            }

            var name = GetString(def, "name");
            var syntax = GetString(def, "syntax") ?? "*";
            var inheritsVal = def.Get("inherits");
            var initial = def.HasProperty("initialValue") ? GetString(def, "initialValue") : null;

            if (name is null || !name.StartsWith("--", StringComparison.Ordinal))
            {
                throw TypeErr(engine, "@property name must start with --");
            }

            if (inheritsVal.IsUndefined())
            {
                throw TypeErr(engine, "registerProperty requires an 'inherits' flag");
            }

            var isUniversal = syntax.Trim() == "*";
            if (!isUniversal && string.IsNullOrEmpty(initial))
            {
                throw TypeErr(engine, "initialValue is required for a non-universal syntax");
            }

            if (!registered.Add(name))
            {
                throw TypeErr(engine, $"property {name} is already registered");
            }

            // Descriptor is valid (validity rules mirror the @property at-rule model).
            return JsValue.Undefined;
        }, 1);

        // CSSOM §6 — CSS[Symbol.toStringTag] === "CSS".
        css.DefineOwnProperty(global::Jint.Native.Symbol.GlobalSymbolRegistry.ToStringTag,
            new global::Jint.Runtime.Descriptors.PropertyDescriptor(JintInterop.Str("CSS"),
                writable: false, enumerable: false, configurable: true));

        JintInterop.DefineDataProp(global, "CSS", css, writable: true, enumerable: false, configurable: true);

        // CSS Typed OM 1 §3.2 — CSSStyleValue.parse(property, cssText).
        var styleValue = new JsObject(engine);
        JintInterop.DefineMethod(engine, styleValue, "parse", (_, args) =>
        {
            var prop = args.Length > 0 ? args[0].ToString() : string.Empty;
            var text = args.Length > 1 ? args[1].ToString() : string.Empty;
            return BuildStyleValue(engine, CssStyleValue.Parse(prop, text));
        }, 2);
        JintInterop.DefineDataProp(global, "CSSStyleValue", styleValue, writable: true, enumerable: false, configurable: true);
    }

    private static void DefineUnitFactory(Engine engine, JsObject css, string name, string unit)
        => JintInterop.DefineMethod(engine, css, name, (_, args) =>
            BuildUnitValue(engine, args.Length > 0 ? args[0].AsNumber() : 0, unit), 1);

    private static JsObject BuildStyleValue(Engine engine, CssStyleValue value) => value switch
    {
        CssUnitValue u => BuildUnitValue(engine, u.Value, u.Unit),
        CssKeywordValue k => BuildKeywordValue(engine, k.Value),
        _ => BuildUnparsed(engine, value.ToString()),
    };

    private static JsObject BuildUnitValue(Engine engine, double value, string unit)
    {
        var o = new JsObject(engine);
        JintInterop.DefineDataProp(o, "value", JintInterop.Num(value));
        JintInterop.DefineDataProp(o, "unit", JintInterop.Str(unit));
        JintInterop.DefineMethod(engine, o, "toString", (_, _) =>
            JintInterop.Str(new CssUnitValue(value, unit).ToString()), 0);
        return o;
    }

    private static JsObject BuildKeywordValue(Engine engine, string keyword)
    {
        var o = new JsObject(engine);
        JintInterop.DefineDataProp(o, "value", JintInterop.Str(keyword));
        JintInterop.DefineMethod(engine, o, "toString", (_, _) => JintInterop.Str(keyword), 0);
        return o;
    }

    private static JsObject BuildUnparsed(Engine engine, string raw)
    {
        var o = new JsObject(engine);
        JintInterop.DefineMethod(engine, o, "toString", (_, _) => JintInterop.Str(raw), 0);
        return o;
    }

    private static string? GetString(ObjectInstance obj, string name)
    {
        var v = obj.Get(name);
        return v.IsUndefined() || v.IsNull() ? null : v.ToString();
    }

    private static global::Jint.Runtime.JavaScriptException TypeErr(Engine engine, string message)
        => new(engine.Intrinsics.TypeError, message);

    // CSSOM §2.1 serialize-an-identifier (the subset needed for CSS.escape):
    // escapes the NULL replacement, control/0x7F, leading digit, and any
    // non-ident code point with a backslash.
    private static string CssEscape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\0') { sb.Append('�'); continue; }
            if ((c <= 0x1F) || c == 0x7F || (i == 0 && char.IsAsciiDigit(c)))
            {
                sb.Append('\\').Append(((int)c).ToString("x", System.Globalization.CultureInfo.InvariantCulture)).Append(' ');
                continue;
            }
            if (char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_' || c > 0x7F)
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('\\').Append(c);
            }
        }
        return sb.ToString();
    }
}
