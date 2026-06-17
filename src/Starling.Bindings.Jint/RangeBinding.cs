using System.Runtime.CompilerServices;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Starling.Dom;

namespace Starling.Bindings.Jint;

/// <summary>
/// DOM §4.6 Range + StaticRange for the Jint backend, mirroring
/// <c>Starling.Bindings/RangeBinding.cs</c>. Installs the <c>Range</c> constructor
/// + prototype, a <c>StaticRange</c> constructor, and <c>document.createRange()</c>.
/// Each host <see cref="DomRange"/> gets one JS wrapper per engine so
/// <c>range === range</c> holds across accessor reads.
/// </summary>
internal static class RangeBinding
{
    // Per-engine wrapper cache (engine is fixed per context, so a single table).
    private static readonly ConditionalWeakTable<global::Jint.Engine, ConditionalWeakTable<DomRange, JintRangeObject>> Caches = new();

    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;
        var docProto = ctx.Wrappers.DocumentPrototype;
        if (docProto is null)
        {
            return; // NodeBindings must run first.
        }

        if (engine.Global.HasOwnProperty("Range"))
        {
            return; // idempotent
        }

        var proto = new JsObject(engine);

        // §4.6 attributes
        JintInterop.DefineAccessor(engine, proto, "startContainer",
            (t, _) => Range(t) is { } r ? ctx.Wrappers.Wrap(r.StartContainer) : JsValue.Null);
        JintInterop.DefineAccessor(engine, proto, "startOffset",
            (t, _) => JintInterop.Num(Range(t)?.StartOffset ?? 0));
        JintInterop.DefineAccessor(engine, proto, "endContainer",
            (t, _) => Range(t) is { } r ? ctx.Wrappers.Wrap(r.EndContainer) : JsValue.Null);
        JintInterop.DefineAccessor(engine, proto, "endOffset",
            (t, _) => JintInterop.Num(Range(t)?.EndOffset ?? 0));
        JintInterop.DefineAccessor(engine, proto, "collapsed",
            (t, _) => JintInterop.Bool(Range(t)?.Collapsed ?? true));
        JintInterop.DefineAccessor(engine, proto, "commonAncestorContainer",
            (t, _) => Range(t) is { } r ? ctx.Wrappers.Wrap(r.CommonAncestorContainer) : JsValue.Null);

        // §4.6.3 set / collapse / select
        Method(engine, proto, "setStart", 2, (t, a) => { var r = Req(ctx, t, "setStart"); Guard(ctx, () => r.SetStart(Node(ctx, a, 0, "setStart"), Int(a, 1))); return JsValue.Undefined; });
        Method(engine, proto, "setEnd", 2, (t, a) => { var r = Req(ctx, t, "setEnd"); Guard(ctx, () => r.SetEnd(Node(ctx, a, 0, "setEnd"), Int(a, 1))); return JsValue.Undefined; });
        Method(engine, proto, "setStartBefore", 1, (t, a) => { var r = Req(ctx, t, "setStartBefore"); Guard(ctx, () => r.SetStartBefore(Node(ctx, a, 0, "setStartBefore"))); return JsValue.Undefined; });
        Method(engine, proto, "setStartAfter", 1, (t, a) => { var r = Req(ctx, t, "setStartAfter"); Guard(ctx, () => r.SetStartAfter(Node(ctx, a, 0, "setStartAfter"))); return JsValue.Undefined; });
        Method(engine, proto, "setEndBefore", 1, (t, a) => { var r = Req(ctx, t, "setEndBefore"); Guard(ctx, () => r.SetEndBefore(Node(ctx, a, 0, "setEndBefore"))); return JsValue.Undefined; });
        Method(engine, proto, "setEndAfter", 1, (t, a) => { var r = Req(ctx, t, "setEndAfter"); Guard(ctx, () => r.SetEndAfter(Node(ctx, a, 0, "setEndAfter"))); return JsValue.Undefined; });
        Method(engine, proto, "collapse", 0, (t, a) => { Req(ctx, t, "collapse").Collapse(a.Length > 0 && TypeConverter.ToBoolean(a[0])); return JsValue.Undefined; });
        Method(engine, proto, "selectNode", 1, (t, a) => { var r = Req(ctx, t, "selectNode"); Guard(ctx, () => r.SelectNode(Node(ctx, a, 0, "selectNode"))); return JsValue.Undefined; });
        Method(engine, proto, "selectNodeContents", 1, (t, a) => { var r = Req(ctx, t, "selectNodeContents"); Guard(ctx, () => r.SelectNodeContents(Node(ctx, a, 0, "selectNodeContents"))); return JsValue.Undefined; });

        // §4.6.4 comparison
        Method(engine, proto, "compareBoundaryPoints", 2, (t, a) =>
        {
            var r = Req(ctx, t, "compareBoundaryPoints");
            var how = (int)(ushort)Uint(a, 0);
            if (a.Length < 2 || Range(a[1]) is not { } other)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "compareBoundaryPoints: second argument must be a Range");
            }

            return GuardR(ctx, () => JintInterop.Num(r.CompareBoundaryPoints(how, other)));
        });
        Method(engine, proto, "comparePoint", 2, (t, a) => { var r = Req(ctx, t, "comparePoint"); var n = Node(ctx, a, 0, "comparePoint"); var o = Int(a, 1); return GuardR(ctx, () => JintInterop.Num(r.ComparePoint(n, o))); });
        Method(engine, proto, "isPointInRange", 2, (t, a) => { var r = Req(ctx, t, "isPointInRange"); var n = Node(ctx, a, 0, "isPointInRange"); var o = Int(a, 1); return GuardR(ctx, () => JintInterop.Bool(r.IsPointInRange(n, o))); });
        Method(engine, proto, "intersectsNode", 1, (t, a) => { var r = Req(ctx, t, "intersectsNode"); var n = Node(ctx, a, 0, "intersectsNode"); return GuardR(ctx, () => JintInterop.Bool(r.IntersectsNode(n))); });

        // §4.6.5 cloning
        Method(engine, proto, "cloneRange", 0, (t, _) => Wrap(ctx, Req(ctx, t, "cloneRange").CloneRange()));
        Method(engine, proto, "detach", 0, (t, _) => { Req(ctx, t, "detach"); return JsValue.Undefined; });

        // §4.6.6 stringify
        Method(engine, proto, "toString", 0, (t, _) => JintInterop.Str(Req(ctx, t, "toString").Stringify()));

        // §4.6.7 mutation
        Method(engine, proto, "deleteContents", 0, (t, _) => { Guard(ctx, () => Req(ctx, t, "deleteContents").DeleteContents()); return JsValue.Undefined; });
        Method(engine, proto, "cloneContents", 0, (t, _) => GuardR(ctx, () => ctx.Wrappers.Wrap(Req(ctx, t, "cloneContents").CloneContents())));
        Method(engine, proto, "extractContents", 0, (t, _) => GuardR(ctx, () => ctx.Wrappers.Wrap(Req(ctx, t, "extractContents").ExtractContents())));
        Method(engine, proto, "insertNode", 1, (t, a) => { var r = Req(ctx, t, "insertNode"); var n = Node(ctx, a, 0, "insertNode"); Guard(ctx, () => r.InsertNode(n)); return JsValue.Undefined; });
        Method(engine, proto, "surroundContents", 1, (t, a) => { var r = Req(ctx, t, "surroundContents"); var n = Node(ctx, a, 0, "surroundContents"); Guard(ctx, () => r.SurroundContents(n)); return JsValue.Undefined; });

        // §4.6 constants on ctor + prototype
        var consts = new (string Name, int Val)[] { ("START_TO_START", 0), ("START_TO_END", 1), ("END_TO_END", 2), ("END_TO_START", 3) };
        foreach (var (n, v) in consts)
        {
            proto.FastSetProperty(n, new PropertyDescriptor(JintInterop.Num(v), writable: false, enumerable: true, configurable: false));
        }

        // Range constructor — new Range() builds a collapsed range on the document.
        var ctor = new NativeConstructor(engine, "Range", 0, (_, _) =>
        {
            var doc = ctx.Document;
            return Wrap(ctx, new DomRange(doc)).AsObject();
        });
        ctor.DefineOwnProperty("prototype", new PropertyDescriptor(proto, writable: false, enumerable: false, configurable: false));
        proto.FastSetProperty("constructor", new PropertyDescriptor(ctor, writable: true, enumerable: false, configurable: true));
        foreach (var (n, v) in consts)
        {
            ctor.FastSetProperty(n, new PropertyDescriptor(JintInterop.Num(v), writable: false, enumerable: true, configurable: false));
        }

        JintInterop.DefineDataProp(engine.Global, "Range", ctor, writable: true, enumerable: false, configurable: true);

        // document.createRange()
        JintInterop.DefineMethod(engine, docProto, "createRange", (t, _) =>
        {
            var doc = ctx.Wrappers.UnwrapDocument(t) ?? ctx.Document;
            return Wrap(ctx, new DomRange(doc));
        }, 0);

        InstallStaticRange(ctx, proto);
    }

    private static void InstallStaticRange(JintBackendContext ctx, ObjectInstance rangeProto)
    {
        var engine = ctx.Engine;
        var staticProto = new JsObject(engine) { Prototype = rangeProto };

        var ctor = new NativeConstructor(engine, "StaticRange", 1, (args, _) =>
        {
            var init = args.Length > 0 && args[0].IsObject() ? args[0].AsObject() : null;
            if (init is null)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "StaticRange: argument must be an object");
            }

            var sc = ctx.Wrappers.UnwrapNode(init.Get("startContainer"));
            var ec = ctx.Wrappers.UnwrapNode(init.Get("endContainer"));
            if (sc is null || ec is null)
            {
                throw DomExceptionBinding.Throw(ctx, "InvalidNodeTypeError", "StaticRange: startContainer and endContainer must be nodes");
            }

            var so = (int)TypeConverter.ToNumber(init.Get("startOffset"));
            var eo = (int)TypeConverter.ToNumber(init.Get("endOffset"));
            var obj = new JsObject(engine) { Prototype = staticProto };
            obj.FastSetProperty("startContainer", new PropertyDescriptor(ctx.Wrappers.Wrap(sc), writable: false, enumerable: true, configurable: false));
            obj.FastSetProperty("startOffset", new PropertyDescriptor(JintInterop.Num(so), writable: false, enumerable: true, configurable: false));
            obj.FastSetProperty("endContainer", new PropertyDescriptor(ctx.Wrappers.Wrap(ec), writable: false, enumerable: true, configurable: false));
            obj.FastSetProperty("endOffset", new PropertyDescriptor(JintInterop.Num(eo), writable: false, enumerable: true, configurable: false));
            obj.FastSetProperty("collapsed", new PropertyDescriptor(JintInterop.Bool(ReferenceEquals(sc, ec) && so == eo), writable: false, enumerable: true, configurable: false));
            return obj;
        });
        ctor.DefineOwnProperty("prototype", new PropertyDescriptor(staticProto, writable: false, enumerable: false, configurable: false));
        staticProto.FastSetProperty("constructor", new PropertyDescriptor(ctor, writable: true, enumerable: false, configurable: true));
        JintInterop.DefineDataProp(engine.Global, "StaticRange", ctor, writable: true, enumerable: false, configurable: true);
    }

    // ---- wrapper identity ---------------------------------------------------

    internal static JsValue Wrap(JintBackendContext ctx, DomRange range)
    {
        var cache = Caches.GetValue(ctx.Engine, _ => new ConditionalWeakTable<DomRange, JintRangeObject>());
        if (cache.TryGetValue(range, out var existing))
        {
            return existing;
        }

        var proto = (ObjectInstance?)ctx.Engine.Global.Get("Range").AsObject().Get("prototype").AsObject();
        var wrapper = new JintRangeObject(ctx.Engine, proto, range);
        cache.Add(range, wrapper);
        return wrapper;
    }

    internal static DomRange? Range(JsValue v) => (v as JintRangeObject)?.HostRange;

    // ---- helpers ------------------------------------------------------------

    private static void Method(global::Jint.Engine e, ObjectInstance proto, string name, int len, Func<JsValue, JsValue[], JsValue> body)
        => JintInterop.DefineMethod(e, proto, name, body, len);

    private static DomRange Req(JintBackendContext ctx, JsValue t, string op)
        => Range(t) ?? throw new JavaScriptException(ctx.Engine.Intrinsics.TypeError, $"{op}: 'this' is not a Range");

    private static Node Node(JintBackendContext ctx, JsValue[] a, int i, string op)
        => (i < a.Length ? ctx.Wrappers.UnwrapNode(a[i]) : null)
            ?? throw new JavaScriptException(ctx.Engine.Intrinsics.TypeError, $"{op}: argument {i} must be a Node");

    private static int Int(JsValue[] a, int i)
    {
        if (i >= a.Length)
        {
            return 0;
        }

        var n = TypeConverter.ToNumber(a[i]);
        return double.IsNaN(n) || double.IsInfinity(n) ? 0 : (int)(uint)(long)n;
    }

    private static uint Uint(JsValue[] a, int i)
        => i < a.Length ? TypeConverter.ToUint32(a[i]) : 0u;

    private static void Guard(JintBackendContext ctx, Action action)
    {
        try { action(); }
        catch (DomRangeException ex) { throw DomExceptionBinding.Throw(ctx, ex.DomName, ex.Message); }
    }

    private static JsValue GuardR(JintBackendContext ctx, Func<JsValue> fn)
    {
        try { return fn(); }
        catch (DomRangeException ex) { throw DomExceptionBinding.Throw(ctx, ex.DomName, ex.Message); }
    }
}

/// <summary>JS wrapper carrying a host <see cref="DomRange"/>.</summary>
internal sealed class JintRangeObject : ObjectInstance
{
    public DomRange HostRange { get; }
    public JintRangeObject(global::Jint.Engine engine, ObjectInstance? proto, DomRange range) : base(engine)
    {
        HostRange = range;
        if (proto is not null)
        {
            Prototype = proto;
        }
    }
}
