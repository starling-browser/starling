using System.Runtime.CompilerServices;
using Starling.Css;
using Starling.Css.FontFace;
using Starling.Css.Parser;
using Starling.Css.FontLoading;
using Starling.Dom;
using Starling.Js.Runtime;
using FontFaceModel = Starling.Css.FontLoading.FontFace;

namespace Starling.Bindings;

/// <summary>
/// CSS Font Loading 3 §3–§4 JS surface: <c>document.fonts</c> (a
/// <c>FontFaceSet</c>) plus the global <c>FontFace</c> constructor. The set is
/// seeded per-document from the <c>@font-face</c> rules in the document's
/// <c>&lt;style&gt;</c> sheets (each seeded face is marked loaded — the engine's
/// real async fetch happens during layout); scripts can also construct
/// <c>FontFace</c> objects and <c>add</c> them.
/// </summary>
internal static class FontFaceBinding
{
    private static readonly ConditionalWeakTable<Document, FontFaceSet> SetPerDocument = new();
    // Recover the model face from a JS FontFace wrapper (for add/delete/has),
    // and reuse one JS wrapper per model face (for iteration identity).
    private static readonly ConditionalWeakTable<JsObject, FontFaceModel> ModelByJs = new();
    private static readonly ConditionalWeakTable<FontFaceModel, JsObject> JsByModel = new();

    public static void Install(JsRealm realm, Document document)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(document);

        InstallFontFaceConstructor(realm);

        // document.fonts — built once per document, reflecting the live set.
        if (realm.DocumentPrototype is { } docProto)
        {
            EventTargetBinding.DefineAccessor(realm, docProto, "fonts", (thisV, _) =>
            {
                var doc = DomWrappers.UnwrapDocument(thisV) ?? document;
                return JsValue.Object(BuildFontFaceSet(realm, GetOrBuildSet(doc)));
            });
        }
    }

    private static FontFaceSet GetOrBuildSet(Document doc)
    {
        if (SetPerDocument.TryGetValue(doc, out var existing))
        {
            return existing;
        }

        var set = new FontFaceSet();
        foreach (var el in doc.DescendantElements())
        {
            if (!el.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var sheet = CssParser.ParseStyleSheet(el.TextContent ?? string.Empty, StyleOrigin.Author);
            foreach (var rule in FontFaceParser.ParseAll(sheet))
            {
                var face = FontFaceModel.FromRule(rule);
                // The engine loads declared @font-face sources during layout, so
                // a face declared by the page is treated as loaded for querying.
                face.Load();
                set.Add(face);
            }
        }
        SetPerDocument.Add(doc, set);
        return set;
    }

    private static JsObject BuildFontFaceSet(JsRealm realm, FontFaceSet set)
    {
        var obj = new JsObject(realm.ObjectPrototype);

        EventTargetBinding.DefineAccessor(realm, obj, "size", (_, _) => JsValue.Number(set.Count));
        EventTargetBinding.DefineAccessor(realm, obj, "status",
            (_, _) => JsValue.String(set.Status == FontFaceSetLoadStatus.Loading ? "loading" : "loaded"));
        // ready resolves once the set is no longer loading; our seeded faces are
        // already loaded, so a resolved promise is spec-correct here.
        EventTargetBinding.DefineAccessor(realm, obj, "ready",
            (_, _) => FetchBinding.ResolvedPromise(realm, JsValue.Object(obj)));
        obj.DefineOwnProperty("onloading", PropertyDescriptor.Data(JsValue.Null, writable: true, enumerable: true, configurable: true));
        obj.DefineOwnProperty("onloadingdone", PropertyDescriptor.Data(JsValue.Null, writable: true, enumerable: true, configurable: true));

        EventTargetBinding.DefineMethod(realm, obj, "add", (_, args) =>
        {
            if (args.Length > 0 && args[0].IsObject && ModelByJs.TryGetValue(args[0].AsObject, out var face))
            {
                set.Add(face);
            }

            return JsValue.Object(obj);
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, obj, "delete", (_, args) =>
            args.Length > 0 && args[0].IsObject && ModelByJs.TryGetValue(args[0].AsObject, out var face) && set.Delete(face)
                ? JsValue.True : JsValue.False, length: 1);

        EventTargetBinding.DefineMethod(realm, obj, "has", (_, args) =>
            args.Length > 0 && args[0].IsObject && ModelByJs.TryGetValue(args[0].AsObject, out var face) && set.Has(face)
                ? JsValue.True : JsValue.False, length: 1);

        EventTargetBinding.DefineMethod(realm, obj, "clear", (_, _) => { set.Clear(); return JsValue.Undefined; }, length: 0);

        EventTargetBinding.DefineMethod(realm, obj, "check", (_, args) =>
        {
            if (args.Length == 0)
            {
                return JsValue.False;
            }

            var font = JsValue.ToStringValue(args[0]);
            var text = args.Length > 1 && !args[1].IsNullish ? JsValue.ToStringValue(args[1]) : null;
            return set.Check(font, text) ? JsValue.True : JsValue.False;
        }, length: 1);

        // load(font, text?) → Promise<sequence<FontFace>>. Returns the matching
        // loaded faces; our seeded faces resolve synchronously.
        EventTargetBinding.DefineMethod(realm, obj, "load", (_, args) =>
        {
            var font = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            var matched = new List<JsValue>();
            var stripped = font.Replace("\"", string.Empty, StringComparison.Ordinal)
                               .Replace("'", string.Empty, StringComparison.Ordinal);
            foreach (var face in set.Faces)
            {
                face.Load();
                if (stripped.Contains(face.Family, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add(JsValue.Object(BuildFontFace(realm, face)));
                }
            }
            return FetchBinding.ResolvedPromise(realm, JsValue.Object(new JsArray(realm, matched)));
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, obj, "forEach", (_, args) =>
        {
            if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
            {
                return JsValue.Undefined;
            }

            foreach (var face in set.Faces)
            {
                var js = JsValue.Object(BuildFontFace(realm, face));
                AbstractOperations.Call(realm.ActiveVm, args[0], JsValue.Undefined, new[] { js, js, JsValue.Object(obj) });
            }
            return JsValue.Undefined;
        }, length: 1);

        // EventTarget surface — inert (no loading/loadingdone events fired yet).
        foreach (var m in new[] { "addEventListener", "removeEventListener" })
        {
            EventTargetBinding.DefineMethod(realm, obj, m, (_, _) => JsValue.Undefined, length: 2);
        }

        return obj;
    }

    private static void InstallFontFaceConstructor(JsRealm realm)
    {
        var ctor = new JsNativeFunction(realm, "FontFace", 2, (_, args) =>
        {
            if (args.Length < 2)
            {
                throw new JsThrow(realm.NewTypeError("FontFace requires family and source arguments"));
            }

            var family = JsValue.ToStringValue(args[0]);
            var source = JsValue.ToStringValue(args[1]);
            var desc = args.Length > 2 && args[2].IsObject ? args[2].AsObject : null;

            var face = new FontFaceModel(
                family: family,
                source: source,
                style: DescOr(desc, "style", "normal"),
                weight: DescOr(desc, "weight", "normal"),
                stretch: DescOr(desc, "stretch", "normal"),
                unicodeRange: DescOr(desc, "unicodeRange", "U+0-10FFFF"));
            return JsValue.Object(BuildFontFace(realm, face));
        }, isConstructor: true);

        realm.GlobalObject.DefineOwnProperty("FontFace",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    private static JsObject BuildFontFace(JsRealm realm, FontFaceModel face)
    {
        if (JsByModel.TryGetValue(face, out var existing))
        {
            return existing;
        }

        var o = new JsObject(realm.ObjectPrototype);
        EventTargetBinding.DefineAccessor(realm, o, "family", (_, _) => JsValue.String(face.Family));
        EventTargetBinding.DefineAccessor(realm, o, "style", (_, _) => JsValue.String(face.Style));
        EventTargetBinding.DefineAccessor(realm, o, "weight", (_, _) => JsValue.String(face.Weight));
        EventTargetBinding.DefineAccessor(realm, o, "stretch", (_, _) => JsValue.String(face.Stretch));
        EventTargetBinding.DefineAccessor(realm, o, "unicodeRange", (_, _) => JsValue.String(face.UnicodeRange));
        EventTargetBinding.DefineAccessor(realm, o, "status", (_, _) => JsValue.String(StatusText(face.Status)));
        EventTargetBinding.DefineMethod(realm, o, "load", (_, _) =>
        {
            face.Load();
            return FetchBinding.ResolvedPromise(realm, JsValue.Object(o));
        }, length: 0);
        EventTargetBinding.DefineAccessor(realm, o, "loaded",
            (_, _) => FetchBinding.ResolvedPromise(realm, JsValue.Object(o)));

        ModelByJs.AddOrUpdate(o, face);
        JsByModel.AddOrUpdate(face, o);
        return o;
    }

    private static string StatusText(FontFaceLoadStatus s) => s switch
    {
        FontFaceLoadStatus.Unloaded => "unloaded",
        FontFaceLoadStatus.Loading => "loading",
        FontFaceLoadStatus.Loaded => "loaded",
        FontFaceLoadStatus.Error => "error",
        _ => "unloaded",
    };

    private static string DescOr(JsObject? desc, string name, string fallback)
    {
        if (desc is null)
        {
            return fallback;
        }

        var v = desc.Get(name);
        return v.IsNullish ? fallback : JsValue.ToStringValue(v);
    }
}
