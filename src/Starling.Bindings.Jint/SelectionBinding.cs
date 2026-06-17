using System.Runtime.CompilerServices;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Starling.Dom;

namespace Starling.Bindings.Jint;

/// <summary>
/// Selection API for the Jint backend, mirroring
/// <c>Starling.Bindings/SelectionBinding.cs</c>. Installs the global
/// <c>Selection</c> constructor + prototype and wires
/// <c>window.getSelection()</c> / <c>document.getSelection()</c> to the single
/// per-document Selection. Boundary points derive from a live Range, so mutating
/// the Range reflects in <c>anchorNode</c>/<c>focusOffset</c>/etc.
/// </summary>
internal static class SelectionBinding
{
    // Per-document host Selection (one per document).
    private static readonly ConditionalWeakTable<Document, DomSelection> HostSelections = new();
    // Per-engine wrapper cache (Document → wrapper) for stable identity.
    private static readonly ConditionalWeakTable<global::Jint.Engine, ConditionalWeakTable<Document, JintSelectionObject>> Caches = new();
    // Per-engine Selection prototype.
    private static readonly ConditionalWeakTable<global::Jint.Engine, ObjectInstance> Protos = new();

    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;
        var docProto = ctx.Wrappers.DocumentPrototype;
        if (docProto is null)
        {
            return; // NodeBindings must run first.
        }

        if (engine.Global.HasOwnProperty("Selection"))
        {
            return; // idempotent
        }

        var proto = new JsObject(engine);
        Protos.AddOrUpdate(engine, proto);

        // ---- attributes
        JintInterop.DefineAccessor(engine, proto, "anchorNode",
            (t, _) => Sel(t) is { AnchorNode: { } n } ? ctx.Wrappers.Wrap(n) : JsValue.Null);
        JintInterop.DefineAccessor(engine, proto, "anchorOffset",
            (t, _) => JintInterop.Num(Sel(t)?.AnchorOffset ?? 0));
        JintInterop.DefineAccessor(engine, proto, "focusNode",
            (t, _) => Sel(t) is { FocusNode: { } n } ? ctx.Wrappers.Wrap(n) : JsValue.Null);
        JintInterop.DefineAccessor(engine, proto, "focusOffset",
            (t, _) => JintInterop.Num(Sel(t)?.FocusOffset ?? 0));
        JintInterop.DefineAccessor(engine, proto, "isCollapsed",
            (t, _) => JintInterop.Bool(Sel(t)?.IsCollapsed ?? true));
        JintInterop.DefineAccessor(engine, proto, "rangeCount",
            (t, _) => JintInterop.Num(Sel(t)?.RangeCount ?? 0));
        JintInterop.DefineAccessor(engine, proto, "type",
            (t, _) => JintInterop.Str(Sel(t)?.Type ?? "None"));
        JintInterop.DefineAccessor(engine, proto, "direction",
            (t, _) => JintInterop.Str(Sel(t) is { } s ? s.Direction switch
            {
                SelectionDirection.Forwards => "forward",
                SelectionDirection.Backwards => "backward",
                _ => "none",
            } : "none"));

        // ---- methods
        JintInterop.DefineMethod(engine, proto, "getRangeAt", (t, a) =>
        {
            var s = Req(ctx, t, "getRangeAt");
            var idx = a.Length > 0 ? (int)TypeConverter.ToNumber(a[0]) : 0;
            var r = Guard(ctx, () => s.GetRangeAt(idx));
            return RangeBinding.Wrap(ctx, r);
        }, 1);
        JintInterop.DefineMethod(engine, proto, "addRange", (t, a) =>
        {
            var s = Req(ctx, t, "addRange");
            if (a.Length < 1 || RangeBinding.Range(a[0]) is not { } r)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "addRange: argument must be a Range");
            }

            s.AddRange(r);
            return JsValue.Undefined;
        }, 1);
        JintInterop.DefineMethod(engine, proto, "removeRange", (t, a) =>
        {
            var s = Req(ctx, t, "removeRange");
            if (a.Length < 1 || RangeBinding.Range(a[0]) is not { } r)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "removeRange: argument must be a Range");
            }

            GuardV(ctx, () => s.RemoveRange(r));
            return JsValue.Undefined;
        }, 1);
        JintInterop.DefineMethod(engine, proto, "removeAllRanges", (t, _) => { Req(ctx, t, "removeAllRanges").RemoveAllRanges(); return JsValue.Undefined; }, 0);
        JintInterop.DefineMethod(engine, proto, "empty", (t, _) => { Req(ctx, t, "empty").RemoveAllRanges(); return JsValue.Undefined; }, 0);
        JintInterop.DefineMethod(engine, proto, "collapse", (t, a) => { Collapse(ctx, t, a, "collapse"); return JsValue.Undefined; }, 1);
        JintInterop.DefineMethod(engine, proto, "setPosition", (t, a) => { Collapse(ctx, t, a, "setPosition"); return JsValue.Undefined; }, 1);
        JintInterop.DefineMethod(engine, proto, "collapseToStart", (t, _) => { var s = Req(ctx, t, "collapseToStart"); GuardV(ctx, () => s.CollapseToStart()); return JsValue.Undefined; }, 0);
        JintInterop.DefineMethod(engine, proto, "collapseToEnd", (t, _) => { var s = Req(ctx, t, "collapseToEnd"); GuardV(ctx, () => s.CollapseToEnd()); return JsValue.Undefined; }, 0);
        JintInterop.DefineMethod(engine, proto, "extend", (t, a) =>
        {
            var s = Req(ctx, t, "extend");
            if (a.Length < 1 || ctx.Wrappers.UnwrapNode(a[0]) is not { } node)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "extend: argument 0 must be a Node");
            }

            var offset = a.Length > 1 ? (int)TypeConverter.ToNumber(a[1]) : 0;
            GuardV(ctx, () => s.Extend(node, offset));
            return JsValue.Undefined;
        }, 1);
        JintInterop.DefineMethod(engine, proto, "setBaseAndExtent", (t, a) =>
        {
            var s = Req(ctx, t, "setBaseAndExtent");
            if (a.Length < 4)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "setBaseAndExtent: requires 4 arguments");
            }

            var aNode = ctx.Wrappers.UnwrapNode(a[0]) ?? throw new JavaScriptException(engine.Intrinsics.TypeError, "setBaseAndExtent: anchorNode must be a Node");
            var aOff = (int)TypeConverter.ToNumber(a[1]);
            var fNode = ctx.Wrappers.UnwrapNode(a[2]) ?? throw new JavaScriptException(engine.Intrinsics.TypeError, "setBaseAndExtent: focusNode must be a Node");
            var fOff = (int)TypeConverter.ToNumber(a[3]);
            GuardV(ctx, () => s.SetBaseAndExtent(aNode, aOff, fNode, fOff));
            return JsValue.Undefined;
        }, 4);
        JintInterop.DefineMethod(engine, proto, "selectAllChildren", (t, a) =>
        {
            var s = Req(ctx, t, "selectAllChildren");
            if (a.Length < 1 || ctx.Wrappers.UnwrapNode(a[0]) is not { } node)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "selectAllChildren: argument must be a Node");
            }

            GuardV(ctx, () => s.SelectAllChildren(node));
            return JsValue.Undefined;
        }, 1);
        JintInterop.DefineMethod(engine, proto, "containsNode", (t, a) =>
        {
            var s = Req(ctx, t, "containsNode");
            if (a.Length < 1 || ctx.Wrappers.UnwrapNode(a[0]) is not { } node)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "containsNode: argument 0 must be a Node");
            }

            var allowPartial = a.Length > 1 && TypeConverter.ToBoolean(a[1]);
            return JintInterop.Bool(s.ContainsNode(node, allowPartial));
        }, 1);
        JintInterop.DefineMethod(engine, proto, "deleteFromDocument", (t, _) => { Req(ctx, t, "deleteFromDocument").DeleteFromDocument(); return JsValue.Undefined; }, 0);
        JintInterop.DefineMethod(engine, proto, "toString", (t, _) => JintInterop.Str(Sel(t)?.Stringify() ?? string.Empty), 0);
        JintInterop.DefineMethod(engine, proto, "modify", (_, _) => JsValue.Undefined, 3);
        JintInterop.DefineMethod(engine, proto, "getComposedRanges", (_, _) => new JsArray(engine, System.Array.Empty<JsValue>()), 0);

        // ---- Selection constructor (not user-constructible)
        var ctor = new NativeConstructor(engine, "Selection", 0, (_, _) =>
            throw new JavaScriptException(engine.Intrinsics.TypeError, "Selection is not user-constructible"));
        ctor.DefineOwnProperty("prototype", new PropertyDescriptor(proto, writable: false, enumerable: false, configurable: false));
        proto.FastSetProperty("constructor", new PropertyDescriptor(ctor, writable: true, enumerable: false, configurable: true));
        JintInterop.DefineDataProp(engine.Global, "Selection", ctor, writable: true, enumerable: false, configurable: true);

        // ---- window.getSelection()
        JintInterop.DefineMethod(engine, engine.Global, "getSelection", (_, _) => WrapSelection(ctx, ctx.Document), 0);

        // ---- document.getSelection() — same instance for the realm document.
        JintInterop.DefineMethod(engine, docProto, "getSelection", (t, _) =>
        {
            if (ctx.Wrappers.UnwrapDocument(t) is not { } doc)
            {
                return JsValue.Null;
            }

            if (!ReferenceEquals(doc, ctx.Document))
            {
                return JsValue.Null;
            }

            return WrapSelection(ctx, doc);
        }, 0);
    }

    private static void Collapse(JintBackendContext ctx, JsValue t, JsValue[] a, string op)
    {
        var s = Req(ctx, t, op);
        if (a.Length == 0)
        {
            throw new JavaScriptException(ctx.Engine.Intrinsics.TypeError, $"{op}: requires a Node or null");
        }

        Node? node = a[0].IsNull() ? null : ctx.Wrappers.UnwrapNode(a[0]);
        if (!a[0].IsNull() && node is null)
        {
            throw new JavaScriptException(ctx.Engine.Intrinsics.TypeError, $"{op}: argument 0 must be a Node or null");
        }

        var offset = a.Length > 1 ? (int)TypeConverter.ToNumber(a[1]) : 0;
        GuardV(ctx, () => s.Collapse(node, offset));
    }

    private static JintSelectionObject WrapSelection(JintBackendContext ctx, Document document)
    {
        var cache = Caches.GetValue(ctx.Engine, _ => new ConditionalWeakTable<Document, JintSelectionObject>());
        if (cache.TryGetValue(document, out var existing))
        {
            return existing;
        }

        var host = HostSelections.GetValue(document, d => new DomSelection(d));
        var proto = Protos.TryGetValue(ctx.Engine, out var p) ? p : null;
        var wrapper = new JintSelectionObject(ctx.Engine, proto, host);
        cache.Add(document, wrapper);
        return wrapper;
    }

    private static DomSelection? Sel(JsValue v) => (v as JintSelectionObject)?.Host;

    private static DomSelection Req(JintBackendContext ctx, JsValue t, string op)
        => Sel(t) ?? throw new JavaScriptException(ctx.Engine.Intrinsics.TypeError, $"{op}: 'this' is not a Selection");

    private static T Guard<T>(JintBackendContext ctx, Func<T> fn)
    {
        try { return fn(); }
        catch (DomRangeException ex) { throw DomExceptionBinding.Throw(ctx, ex.DomName, ex.Message); }
    }

    private static void GuardV(JintBackendContext ctx, Action fn)
    {
        try { fn(); }
        catch (DomRangeException ex) { throw DomExceptionBinding.Throw(ctx, ex.DomName, ex.Message); }
    }
}

/// <summary>JS wrapper carrying a host <see cref="DomSelection"/>.</summary>
internal sealed class JintSelectionObject : ObjectInstance
{
    public DomSelection Host { get; }
    public JintSelectionObject(global::Jint.Engine engine, ObjectInstance? proto, DomSelection host) : base(engine)
    {
        Host = host;
        if (proto is not null)
        {
            Prototype = proto;
        }
    }
}
