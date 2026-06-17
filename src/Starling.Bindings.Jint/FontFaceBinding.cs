using System.Runtime.CompilerServices;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Starling.Css;
using Starling.Css.FontFace;
using Starling.Css.FontLoading;
using Starling.Css.Parser;
using Starling.Dom;
using FontFaceModel = Starling.Css.FontLoading.FontFace;

namespace Starling.Bindings.Jint;

/// <summary>
/// CSS Font Loading 3 §3–§4 for the Jint backend, mirroring
/// <c>Starling.Bindings/FontFaceBinding.cs</c>: <c>document.fonts</c> (a
/// FontFaceSet) and the global <c>FontFace</c> constructor. The set is seeded per
/// document from <c>@font-face</c> rules in the document's &lt;style&gt; sheets
/// (each seeded face marked loaded). Backed by the engine-neutral
/// <see cref="FontFaceSet"/> / <see cref="FontFaceModel"/>.
/// </summary>
internal static class FontFaceBinding
{
    private static readonly ConditionalWeakTable<Document, FontFaceSet> SetPerDocument = new();
    private static readonly ConditionalWeakTable<ObjectInstance, FontFaceModel> ModelByJs = new();
    private static readonly ConditionalWeakTable<FontFaceModel, JsObject> JsByModel = new();

    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;

        InstallFontFaceConstructor(ctx);

        if (ctx.Wrappers.DocumentPrototype is { } docProto)
        {
            JintInterop.DefineAccessor(engine, docProto, "fonts", (t, _) =>
            {
                var doc = ctx.Wrappers.UnwrapDocument(t) ?? ctx.Document;
                return BuildFontFaceSet(ctx, GetOrBuildSet(doc));
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
                face.Load();
                set.Add(face);
            }
        }
        SetPerDocument.Add(doc, set);
        return set;
    }

    private static JsObject BuildFontFaceSet(JintBackendContext ctx, FontFaceSet set)
    {
        var engine = ctx.Engine;
        var obj = new JsObject(engine);

        JintInterop.DefineAccessor(engine, obj, "size", (_, _) => JintInterop.Num(set.Count));
        JintInterop.DefineAccessor(engine, obj, "status",
            (_, _) => JintInterop.Str(set.Status == FontFaceSetLoadStatus.Loading ? "loading" : "loaded"));
        JintInterop.DefineAccessor(engine, obj, "ready", (_, _) => ResolvedPromise(engine, obj));
        obj.FastSetProperty("onloading", new PropertyDescriptor(JsValue.Null, writable: true, enumerable: true, configurable: true));
        obj.FastSetProperty("onloadingdone", new PropertyDescriptor(JsValue.Null, writable: true, enumerable: true, configurable: true));

        JintInterop.DefineMethod(engine, obj, "add", (_, a) =>
        {
            if (a.Length > 0 && a[0].IsObject() && ModelByJs.TryGetValue(a[0].AsObject(), out var face))
            {
                set.Add(face);
            }

            return obj;
        }, 1);
        JintInterop.DefineMethod(engine, obj, "delete", (_, a) =>
            JintInterop.Bool(a.Length > 0 && a[0].IsObject() && ModelByJs.TryGetValue(a[0].AsObject(), out var face) && set.Delete(face)), 1);
        JintInterop.DefineMethod(engine, obj, "has", (_, a) =>
            JintInterop.Bool(a.Length > 0 && a[0].IsObject() && ModelByJs.TryGetValue(a[0].AsObject(), out var face) && set.Has(face)), 1);
        JintInterop.DefineMethod(engine, obj, "clear", (_, _) => { set.Clear(); return JsValue.Undefined; }, 0);
        JintInterop.DefineMethod(engine, obj, "check", (_, a) =>
        {
            if (a.Length == 0)
            {
                return JsBoolean.False;
            }

            var font = TypeConverter.ToString(a[0]);
            var text = a.Length > 1 && !a[1].IsNull() && !a[1].IsUndefined() ? TypeConverter.ToString(a[1]) : null;
            return JintInterop.Bool(set.Check(font, text));
        }, 1);
        JintInterop.DefineMethod(engine, obj, "load", (_, a) =>
        {
            var font = a.Length > 0 ? TypeConverter.ToString(a[0]) : "";
            var stripped = font.Replace("\"", string.Empty, StringComparison.Ordinal).Replace("'", string.Empty, StringComparison.Ordinal);
            var matched = new List<JsValue>();
            foreach (var face in set.Faces)
            {
                face.Load();
                if (stripped.Contains(face.Family, StringComparison.OrdinalIgnoreCase))
                {
                    matched.Add(BuildFontFace(ctx, face));
                }
            }
            return ResolvedPromise(engine, new JsArray(engine, matched.ToArray()));
        }, 1);
        JintInterop.DefineMethod(engine, obj, "forEach", (_, a) =>
        {
            if (a.Length == 0 || !a[0].IsCallable())
            {
                return JsValue.Undefined;
            }

            foreach (var face in set.Faces)
            {
                var js = BuildFontFace(ctx, face);
                a[0].Call(JsValue.Undefined, new JsValue[] { js, js, obj });
            }
            return JsValue.Undefined;
        }, 1);
        foreach (var m in new[] { "addEventListener", "removeEventListener" })
        {
            JintInterop.DefineMethod(engine, obj, m, (_, _) => JsValue.Undefined, 2);
        }

        return obj;
    }

    private static void InstallFontFaceConstructor(JintBackendContext ctx)
    {
        var engine = ctx.Engine;
        var ctor = new NativeConstructor(engine, "FontFace", 2, (a, _) =>
        {
            if (a.Length < 2)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "FontFace requires family and source arguments");
            }

            var family = TypeConverter.ToString(a[0]);
            var source = TypeConverter.ToString(a[1]);
            var desc = a.Length > 2 && a[2].IsObject() ? a[2].AsObject() : null;
            var face = new FontFaceModel(
                family: family,
                source: source,
                style: DescOr(desc, "style", "normal"),
                weight: DescOr(desc, "weight", "normal"),
                stretch: DescOr(desc, "stretch", "normal"),
                unicodeRange: DescOr(desc, "unicodeRange", "U+0-10FFFF"));
            return BuildFontFace(ctx, face);
        });
        JintInterop.DefineDataProp(engine.Global, "FontFace", ctor, writable: true, enumerable: false, configurable: true);
    }

    private static JsObject BuildFontFace(JintBackendContext ctx, FontFaceModel face)
    {
        if (JsByModel.TryGetValue(face, out var existing))
        {
            return existing;
        }

        var engine = ctx.Engine;
        var o = new JsObject(engine);
        JintInterop.DefineAccessor(engine, o, "family", (_, _) => JintInterop.Str(face.Family));
        JintInterop.DefineAccessor(engine, o, "style", (_, _) => JintInterop.Str(face.Style));
        JintInterop.DefineAccessor(engine, o, "weight", (_, _) => JintInterop.Str(face.Weight));
        JintInterop.DefineAccessor(engine, o, "stretch", (_, _) => JintInterop.Str(face.Stretch));
        JintInterop.DefineAccessor(engine, o, "unicodeRange", (_, _) => JintInterop.Str(face.UnicodeRange));
        JintInterop.DefineAccessor(engine, o, "status", (_, _) => JintInterop.Str(StatusText(face.Status)));
        JintInterop.DefineMethod(engine, o, "load", (_, _) => { face.Load(); return ResolvedPromise(engine, o); }, 0);
        JintInterop.DefineAccessor(engine, o, "loaded", (_, _) => ResolvedPromise(engine, o));
        ModelByJs.AddOrUpdate(o, face);
        JsByModel.AddOrUpdate(face, o);
        return o;
    }

    private static JsValue ResolvedPromise(global::Jint.Engine engine, JsValue value)
    {
        var (promise, resolve, _) = engine.Advanced.RegisterPromise();
        resolve(value);
        return promise;
    }

    private static string StatusText(FontFaceLoadStatus s) => s switch
    {
        FontFaceLoadStatus.Unloaded => "unloaded",
        FontFaceLoadStatus.Loading => "loading",
        FontFaceLoadStatus.Loaded => "loaded",
        FontFaceLoadStatus.Error => "error",
        _ => "unloaded",
    };

    private static string DescOr(ObjectInstance? desc, string name, string fallback)
    {
        if (desc is null)
        {
            return fallback;
        }

        var v = desc.Get(name);
        return v.IsNull() || v.IsUndefined() ? fallback : TypeConverter.ToString(v);
    }
}
