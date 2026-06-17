using Starling.Css.TypedOm;
using Starling.Js.Intrinsics;
using Starling.Js.Runtime;

namespace Starling.Bindings;

/// <summary>
/// The <c>window.CSS</c> namespace + <c>CSSStyleValue</c> global. Exposes the
/// Starling CSS Typed OM value model (CSS Typed OM 1) and the <c>@property</c>
/// registration API (CSS Properties and Values API 1) to scripts. Pure model
/// exposure: the numeric factories and <see cref="CssStyleValue.Parse"/> reuse
/// <c>Starling.Css.TypedOm</c>, and <c>registerProperty</c> applies the same
/// descriptor-validity rules as the <c>@property</c> at-rule parser.
/// </summary>
internal static class CssBinding
{
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var global = realm.GlobalObject;

        var css = new JsObject(realm.ObjectPrototype);

        // CSS Typed OM 1 §4.1 — numeric value factories. Each returns a
        // CSSUnitValue-shaped object {value, unit, toString()}.
        DefineUnitFactory(realm, css, "number", "number");
        DefineUnitFactory(realm, css, "px", "px");
        DefineUnitFactory(realm, css, "percent", "%");
        DefineUnitFactory(realm, css, "em", "em");
        DefineUnitFactory(realm, css, "rem", "rem");
        DefineUnitFactory(realm, css, "vw", "vw");
        DefineUnitFactory(realm, css, "vh", "vh");
        DefineUnitFactory(realm, css, "deg", "deg");
        DefineUnitFactory(realm, css, "s", "s");
        DefineUnitFactory(realm, css, "ms", "ms");

        // CSSOM §2.1 — CSS.escape(ident).
        EventTargetBinding.DefineMethod(realm, css, "escape", (_, args) =>
            JsValue.String(CssEscape(args.Length > 0 ? JsValue.ToStringValue(args[0]) : string.Empty)), length: 1);

        // CSS Properties and Values API 1 §3 — CSS.registerProperty(definition).
        // Validates the descriptor and rejects duplicates; a per-realm set
        // tracks names registered in this session.
        var registered = new HashSet<string>(StringComparer.Ordinal);
        EventTargetBinding.DefineMethod(realm, css, "registerProperty", (_, args) =>
        {
            if (args.Length < 1 || !args[0].IsObject)
            {
                throw new JsThrow(realm.NewTypeError("registerProperty requires a descriptor object"));
            }

            var def = args[0].AsObject;

            var name = GetString(def, "name");
            var syntax = GetString(def, "syntax") ?? "*";
            var inheritsVal = def.Get("inherits");
            var initial = GetString(def, "initialValue");

            if (name is null || !name.StartsWith("--", StringComparison.Ordinal))
            {
                throw new JsThrow(realm.NewTypeError("@property name must start with --"));
            }

            if (inheritsVal.IsUndefined)
            {
                throw new JsThrow(realm.NewTypeError("registerProperty requires an 'inherits' flag"));
            }

            var isUniversal = syntax.Trim() == "*";
            if (!isUniversal && string.IsNullOrEmpty(initial))
            {
                throw new JsThrow(realm.NewTypeError("initialValue is required for a non-universal syntax"));
            }

            if (!registered.Add(name))
            {
                throw new JsThrow(realm.NewTypeError($"property {name} is already registered"));
            }

            // Descriptor is valid (validity rules mirror the @property at-rule model).
            return JsValue.Undefined;
        }, length: 1);

        // CSSOM §6.5.1 — CSS.supports(property, value) and CSS.supports(conditionText).
        EventTargetBinding.DefineMethod(realm, css, "supports", (_, args) =>
        {
            string condition;
            if (args.Length >= 2 && !args[1].IsUndefined)
            {
                var prop = JsValue.ToStringValue(args[0]);
                var value = JsValue.ToStringValue(args[1]);
                // The 2-arg form is false for an empty value or one that smuggles in
                // a priority / extra declaration.
                if (value.Length == 0 || value.Contains('!') || value.Contains(';'))
                {
                    return JsValue.False;
                }

                condition = $"({prop}:{value})";
            }
            else if (args.Length >= 1)
            {
                condition = JsValue.ToStringValue(args[0]);
            }
            else
            {
                throw new JsThrow(realm.NewTypeError("CSS.supports requires an argument"));
            }
            return JsValue.Boolean(EvaluateSupports(condition));
        }, length: 1);

        // WebIDL §3.7.3 — a namespace object has an @@toStringTag of its name,
        // making Object.prototype.toString.call(CSS) yield "[object CSS]".
        css.DefineOwnProperty(SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("CSS"), writable: false, enumerable: false, configurable: true));

        global.DefineOwnProperty("CSS",
            PropertyDescriptor.Data(JsValue.Object(css), writable: true, enumerable: false, configurable: true));

        // CSS Typed OM 1 §3.2 — CSSStyleValue.parse(property, cssText).
        var styleValue = new JsObject(realm.ObjectPrototype);
        EventTargetBinding.DefineMethod(realm, styleValue, "parse", (_, args) =>
        {
            var prop = args.Length > 0 ? JsValue.ToStringValue(args[0]) : string.Empty;
            var text = args.Length > 1 ? JsValue.ToStringValue(args[1]) : string.Empty;
            return JsValue.Object(BuildStyleValue(realm, CssStyleValue.Parse(prop, text)));
        }, length: 2);
        global.DefineOwnProperty("CSSStyleValue",
            PropertyDescriptor.Data(JsValue.Object(styleValue), writable: true, enumerable: false, configurable: true));
    }

    /// <summary>Evaluate a CSS @supports condition by round-tripping it through the
    /// parser and the shared <see cref="Starling.Css.Cascade.SupportsEvaluator"/>.</summary>
    private static bool EvaluateSupports(string condition)
    {
        try
        {
            var sheet = Starling.Css.Parser.CssParser.ParseStyleSheet($"@supports {condition} {{}}");
            foreach (var rule in sheet.Rules)
            {
                if (rule is Starling.Css.Parser.AtRule at &&
                    string.Equals(at.Name, "supports", StringComparison.OrdinalIgnoreCase))
                {
                    return Starling.Css.Cascade.SupportsEvaluator.Evaluate(at.Prelude);
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Wrap a declared CSS value (property + cssText) as a CSSStyleValue
    /// object, reusing <see cref="CssStyleValue.Parse"/>. Used by the Typed OM
    /// style maps (<c>attributeStyleMap</c> / <c>computedStyleMap</c>).</summary>
    internal static JsObject WrapDeclaredValue(JsRealm realm, string property, string cssText)
        => BuildStyleValue(realm, CssStyleValue.Parse(property, cssText));

    private static void DefineUnitFactory(JsRealm realm, JsObject css, string name, string unit)
        => EventTargetBinding.DefineMethod(realm, css, name, (_, args) =>
            JsValue.Object(BuildUnitValue(realm, args.Length > 0 ? JsValue.ToNumber(args[0]) : 0, unit)), length: 1);

    private static JsObject BuildStyleValue(JsRealm realm, CssStyleValue value) => value switch
    {
        CssUnitValue u => BuildUnitValue(realm, u.Value, u.Unit),
        CssKeywordValue k => BuildKeywordValue(realm, k.Value),
        _ => BuildUnparsed(realm, value.ToString()),
    };

    private static JsObject BuildUnitValue(JsRealm realm, double value, string unit)
    {
        var o = new JsObject(realm.ObjectPrototype);
        o.DefineOwnProperty("value", PropertyDescriptor.Data(JsValue.Number(value), writable: true, enumerable: true, configurable: true));
        o.DefineOwnProperty("unit", PropertyDescriptor.Data(JsValue.String(unit), writable: true, enumerable: true, configurable: true));
        EventTargetBinding.DefineMethod(realm, o, "toString",
            (_, _) => JsValue.String(new CssUnitValue(value, unit).ToString()), length: 0);
        return o;
    }

    private static JsObject BuildKeywordValue(JsRealm realm, string keyword)
    {
        var o = new JsObject(realm.ObjectPrototype);
        o.DefineOwnProperty("value", PropertyDescriptor.Data(JsValue.String(keyword), writable: true, enumerable: true, configurable: true));
        EventTargetBinding.DefineMethod(realm, o, "toString", (_, _) => JsValue.String(keyword), length: 0);
        return o;
    }

    private static JsObject BuildUnparsed(JsRealm realm, string raw)
    {
        var o = new JsObject(realm.ObjectPrototype);
        EventTargetBinding.DefineMethod(realm, o, "toString", (_, _) => JsValue.String(raw), length: 0);
        return o;
    }

    private static string? GetString(JsObject obj, string name)
    {
        var v = obj.Get(name);
        return v.IsNullish ? null : JsValue.ToStringValue(v);
    }

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
