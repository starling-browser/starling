using System.Globalization;
using System.Runtime.CompilerServices;
using Jint;
using Jint.Native;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Starling.Css;
using Starling.Css.Cssom;
using Starling.Css.Parser;
using Starling.Dom;

namespace Starling.Bindings.Jint;

/// <summary>
/// CSSOM host objects for the Jint backend (CSSOM §6), mirroring
/// <c>Starling.Bindings/CssomBinding.cs</c>: <c>document.styleSheets</c> →
/// StyleSheetList → CSSStyleSheet → <c>cssRules</c> → CSSStyleRule
/// (<c>selectorText</c>/<c>style</c>/<c>cssText</c>), plus <c>element.sheet</c> on
/// &lt;style&gt; elements. Backed by a live <see cref="CssomStyleSheet"/> per
/// element, cached per document so CSSOM edits round-trip.
/// </summary>
internal static class CssomBinding
{
    private static readonly ConditionalWeakTable<Document, List<CssomStyleSheet>> SheetsPerDocument = new();

    private sealed class StyleElementSheet { public string Source = ""; public CssomStyleSheet? Sheet; }
    private static readonly ConditionalWeakTable<Element, StyleElementSheet> SheetPerStyleElement = new();

    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;
        var docProto = ctx.Wrappers.DocumentPrototype;
        var elProto = ctx.Wrappers.ElementPrototype;
        if (docProto is null || elProto is null)
        {
            return;
        }

        // document.styleSheets
        JintInterop.DefineAccessor(engine, docProto, "styleSheets", (t, _) =>
        {
            var doc = ctx.Wrappers.UnwrapDocument(t) ?? ctx.Document;
            return BuildStyleSheetList(ctx, GetOrBuildSheets(doc));
        });

        // element.sheet (CSSOM §6.5) — only <style> elements carry inline CSS.
        if (!elProto.HasOwnProperty("sheet"))
        {
            JintInterop.DefineAccessor(engine, elProto, "sheet", (t, _) => StyleElementSheetAccessor(ctx, t));
        }
    }

    private static JsValue StyleElementSheetAccessor(JintBackendContext ctx, JsValue thisV)
    {
        var el = ctx.Wrappers.UnwrapElement(thisV);
        if (el is null)
        {
            return JsValue.Null;
        }

        if (!el.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
        {
            return JsValue.Null;
        }

        var source = el.TextContent ?? string.Empty;
        var entry = SheetPerStyleElement.GetValue(el, static _ => new StyleElementSheet());
        if (entry.Sheet is null || !string.Equals(entry.Source, source, StringComparison.Ordinal))
        {
            var parsed = CssParser.ParseStyleSheet(source, StyleOrigin.Author);
            entry.Sheet = new CssomStyleSheet(parsed);
            entry.Source = source;
        }
        return BuildStyleSheet(ctx, entry.Sheet);
    }

    private static List<CssomStyleSheet> GetOrBuildSheets(Document doc)
    {
        if (SheetsPerDocument.TryGetValue(doc, out var existing))
        {
            return existing;
        }

        var list = new List<CssomStyleSheet>();
        foreach (var el in doc.DescendantElements())
        {
            if (el.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = CssParser.ParseStyleSheet(el.TextContent ?? string.Empty, StyleOrigin.Author);
                list.Add(new CssomStyleSheet(parsed));
            }
        }
        SheetsPerDocument.Add(doc, list);
        return list;
    }

    private static JsObject BuildStyleSheetList(JintBackendContext ctx, List<CssomStyleSheet> sheets)
    {
        var engine = ctx.Engine;
        var obj = new JsObject(engine);
        for (var i = 0; i < sheets.Count; i++)
        {
            obj.FastSetProperty(i.ToString(CultureInfo.InvariantCulture),
                new PropertyDescriptor(BuildStyleSheet(ctx, sheets[i]), writable: false, enumerable: true, configurable: true));
        }

        obj.FastSetProperty("length", new PropertyDescriptor(JintInterop.Num(sheets.Count), writable: false, enumerable: false, configurable: true));
        JintInterop.DefineMethod(engine, obj, "item", (_, a) =>
        {
            if (a.Length == 0)
            {
                return JsValue.Null;
            }

            var idx = (int)TypeConverter.ToNumber(a[0]);
            return idx >= 0 && idx < sheets.Count ? BuildStyleSheet(ctx, sheets[idx]) : JsValue.Null;
        }, 1);
        return obj;
    }

    private static JsObject BuildStyleSheet(JintBackendContext ctx, CssomStyleSheet sheet)
    {
        var engine = ctx.Engine;
        var obj = new JsObject(engine);
        JintInterop.DefineAccessor(engine, obj, "cssRules", (_, _) => BuildRuleList(ctx, sheet));
        JintInterop.DefineAccessor(engine, obj, "rules", (_, _) => BuildRuleList(ctx, sheet));
        JintInterop.DefineAccessor(engine, obj, "type", (_, _) => JintInterop.Str("text/css"));
        JintInterop.DefineAccessor(engine, obj, "href", (_, _) => JsValue.Null);
        JintInterop.DefineAccessor(engine, obj, "title", (_, _) => JsValue.Null);
        JintInterop.DefineAccessor(engine, obj, "disabled", (_, _) => JsBoolean.False);
        return obj;
    }

    private static JsObject BuildRuleList(JintBackendContext ctx, CssomStyleSheet sheet)
    {
        var engine = ctx.Engine;
        var obj = new JsObject(engine);
        var rules = sheet.Rules;
        for (var i = 0; i < rules.Count; i++)
        {
            var ruleObj = rules[i] is { } styleRule
                ? BuildStyleRule(ctx, styleRule)
                : BuildAtRulePlaceholder(ctx, sheet.AtRuleNameAt(i));
            obj.FastSetProperty(i.ToString(CultureInfo.InvariantCulture),
                new PropertyDescriptor(ruleObj, writable: false, enumerable: true, configurable: true));
        }
        obj.FastSetProperty("length", new PropertyDescriptor(JintInterop.Num(rules.Count), writable: false, enumerable: false, configurable: true));
        JintInterop.DefineMethod(engine, obj, "item", (_, a) =>
        {
            if (a.Length == 0)
            {
                return JsValue.Null;
            }

            var idx = (int)TypeConverter.ToNumber(a[0]);
            if (idx < 0 || idx >= rules.Count)
            {
                return JsValue.Null;
            }

            return rules[idx] is { } r ? BuildStyleRule(ctx, r) : BuildAtRulePlaceholder(ctx, sheet.AtRuleNameAt(idx));
        }, 1);
        return obj;
    }

    private static JsObject BuildAtRulePlaceholder(JintBackendContext ctx, string? atRuleName)
    {
        var engine = ctx.Engine;
        var obj = new JsObject(engine);
        var emptyBlock = new CssomDeclarationBlock();
        var type = AtRuleTypeConstant(atRuleName);
        JintInterop.DefineAccessor(engine, obj, "type", (_, _) => JintInterop.Num(type));
        JintInterop.DefineAccessor(engine, obj, "cssText", (_, _) => JintInterop.Str(""));
        JintInterop.DefineAccessor(engine, obj, "style", (_, _) => BuildDeclaration(ctx, emptyBlock));
        return obj;
    }

    private static int AtRuleTypeConstant(string? name) => name switch
    {
        "charset" => 2,
        "import" => 3,
        "media" => 4,
        "font-face" => 5,
        "page" => 6,
        "keyframes" or "-webkit-keyframes" => 7,
        "namespace" => 10,
        "counter-style" => 11,
        "supports" => 12,
        "font-feature-values" => 14,
        _ => 5,
    };

    private static JsObject BuildStyleRule(JintBackendContext ctx, CssomStyleRule rule)
    {
        var engine = ctx.Engine;
        var obj = new JsObject(engine);
        JintInterop.DefineAccessor(engine, obj, "selectorText",
            (_, _) => JintInterop.Str(rule.SelectorTextRaw),
            (_, a) => { if (a.Length > 0) { rule.TrySetSelectorText(TypeConverter.ToString(a[0])); } return JsValue.Undefined; });
        JintInterop.DefineAccessor(engine, obj, "style", (_, _) => BuildDeclaration(ctx, rule.Style));
        JintInterop.DefineAccessor(engine, obj, "cssText", (_, _) =>
        {
            var decls = rule.Style.CssText;
            return JintInterop.Str(decls.Length == 0
                ? rule.SelectorTextRaw + " { }"
                : rule.SelectorTextRaw + " { " + decls + " }");
        });
        JintInterop.DefineAccessor(engine, obj, "type", (_, _) => JintInterop.Num(1));
        return obj;
    }

    /// <summary>Build a live CSSStyleDeclaration over a declaration block.</summary>
    public static JsObject BuildDeclaration(JintBackendContext ctx, CssomDeclarationBlock block)
    {
        var engine = ctx.Engine;
        var obj = new JsObject(engine);

        JintInterop.DefineAccessor(engine, obj, "cssText",
            (_, _) => JintInterop.Str(block.CssText),
            (_, a) => { block.CssText = a.Length > 0 ? TypeConverter.ToString(a[0]) : ""; return JsValue.Undefined; });
        JintInterop.DefineAccessor(engine, obj, "length", (_, _) => JintInterop.Num(block.Count));

        JintInterop.DefineMethod(engine, obj, "getPropertyValue",
            (_, a) => JintInterop.Str(a.Length == 0 ? "" : block.GetPropertyValue(TypeConverter.ToString(a[0]))), 1);
        JintInterop.DefineMethod(engine, obj, "getPropertyPriority",
            (_, a) => JintInterop.Str(a.Length == 0 ? "" : block.GetPropertyPriority(TypeConverter.ToString(a[0]))), 1);
        JintInterop.DefineMethod(engine, obj, "setProperty", (_, a) =>
        {
            if (a.Length < 2)
            {
                return JsValue.Undefined;
            }

            var name = TypeConverter.ToString(a[0]);
            var value = TypeConverter.ToString(a[1]);
            var priority = a.Length > 2 && !a[2].IsNull() && !a[2].IsUndefined() ? TypeConverter.ToString(a[2]) : null;
            block.SetProperty(name, value, priority);
            return JsValue.Undefined;
        }, 2);
        JintInterop.DefineMethod(engine, obj, "removeProperty",
            (_, a) => JintInterop.Str(a.Length == 0 ? "" : block.RemoveProperty(TypeConverter.ToString(a[0]))), 1);
        JintInterop.DefineMethod(engine, obj, "item", (_, a) =>
            JintInterop.Str(a.Length == 0 ? "" : block.ItemName((int)TypeConverter.ToNumber(a[0]))), 1);

        // camelCase / kebab-case accessors over the declaration block.
        foreach (var kebab in NodeBindings.StylePropertyNames)
        {
            var k = kebab;
            var camel = NodeBindings.KebabToCamelPublic(k);
            JintInterop.DefineAccessor(engine, obj, k,
                (_, _) => JintInterop.Str(block.GetPropertyValue(k)),
                (_, a) => { block.SetProperty(k, a.Length > 0 ? TypeConverter.ToString(a[0]) : "", null); return JsValue.Undefined; });
            if (camel != k)
            {
                JintInterop.DefineAccessor(engine, obj, camel,
                    (_, _) => JintInterop.Str(block.GetPropertyValue(k)),
                    (_, a) => { block.SetProperty(k, a.Length > 0 ? TypeConverter.ToString(a[0]) : "", null); return JsValue.Undefined; });
            }
        }
        return obj;
    }
}
