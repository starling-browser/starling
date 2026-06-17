using System.Runtime.CompilerServices;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Microsoft.Extensions.Logging;
using Starling.Dom;

namespace Starling.Bindings.Jint;

/// <summary>
/// DOM §4.3 MutationObserver for the Jint backend — a real implementation that
/// queues MutationRecords in response to DOM mutations, mirroring
/// <c>Starling.Bindings/Observers/MutationObserverBinding.cs</c>. Subscribes to the
/// document's internal mutation hooks (attribute / childList / characterData),
/// validates <c>observe()</c> options, batches records per observer, and delivers
/// them on a microtask; <c>takeRecords()</c> drains synchronously.
/// </summary>
internal static class MutationObserverBinding
{
    private static readonly ConditionalWeakTable<ObjectInstance, MutationObserverState> States = new();
    private static readonly ConditionalWeakTable<Document, List<WeakReference<MutationObserverState>>> DocStates = new();

    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;
        if (engine.Global.HasOwnProperty("MutationObserver"))
        {
            return;
        }

        var doc = ctx.Document;
        doc.AttributeMutated = (el, attr, old) => Dispatch(doc, s => s.MaybeQueueAttribute(el, attr, old));
        doc.ChildListMutated = (t, a, r, p, n) => Dispatch(doc, s => s.MaybeQueueChildList(t, a, r, p, n));
        doc.CharacterDataMutated = (t, old) => Dispatch(doc, s => s.MaybeQueueCharacterData(t, old));

        var proto = new JsObject(engine);

        JintInterop.DefineMethod(engine, proto, "observe", (thisV, args) =>
        {
            var state = Resolve(thisV) ?? throw new JavaScriptException(engine.Intrinsics.TypeError,
                "Illegal invocation: observe called on non-MutationObserver");
            if (args.Length == 0 || ctx.Wrappers.UnwrapNode(args[0]) is not { } target)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "MutationObserver.observe: target must be a Node");
            }

            var opts = ParseOptions(ctx, args.Length > 1 ? args[1] : JsValue.Undefined);
            state.AddOrReplaceObservation(target, opts);
            RegisterState(doc, state);
            return JsValue.Undefined;
        }, 2);
        JintInterop.DefineMethod(engine, proto, "disconnect", (thisV, _) =>
        {
            Resolve(thisV)?.Disconnect();
            return JsValue.Undefined;
        }, 0);
        JintInterop.DefineMethod(engine, proto, "takeRecords", (thisV, _) =>
            Resolve(thisV)?.DrainRecords(engine) ?? new JsArray(engine, System.Array.Empty<JsValue>()), 0);

        var ctor = new NativeConstructor(engine, "MutationObserver", 1, (args, _) =>
        {
            if (args.Length == 0 || !args[0].IsCallable())
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "MutationObserver: callback is not a function");
            }

            var inst = new JsObject(engine) { Prototype = proto };
            States.Add(inst, new MutationObserverState(ctx, inst, args[0]));
            return inst;
        });
        ctor.DefineOwnProperty("prototype", new PropertyDescriptor(proto, writable: false, enumerable: false, configurable: false));
        proto.FastSetProperty("constructor", new PropertyDescriptor(ctor, writable: true, enumerable: false, configurable: true));
        JintInterop.DefineDataProp(engine.Global, "MutationObserver", ctor, writable: true, enumerable: false, configurable: true);

        // MutationRecord — illegal-constructor global so `instanceof MutationRecord` exists.
        if (!engine.Global.HasOwnProperty("MutationRecord"))
        {
            var recordProto = new JsObject(engine);
            ctx.Wrappers.MutationRecordPrototype = recordProto;
            var recCtor = new NativeConstructor(engine, "MutationRecord", 0, (_, _) =>
                throw new JavaScriptException(engine.Intrinsics.TypeError, "Illegal constructor"));
            recCtor.DefineOwnProperty("prototype", new PropertyDescriptor(recordProto, writable: false, enumerable: false, configurable: false));
            recordProto.FastSetProperty("constructor", new PropertyDescriptor(recCtor, writable: true, enumerable: false, configurable: true));
            JintInterop.DefineDataProp(engine.Global, "MutationRecord", recCtor, writable: true, enumerable: false, configurable: true);
        }
    }

    private static MutationObserverState? Resolve(JsValue thisV)
        => thisV is ObjectInstance oi && States.TryGetValue(oi, out var s) ? s : null;

    private static void RegisterState(Document doc, MutationObserverState state)
    {
        var list = DocStates.GetValue(doc, static _ => new List<WeakReference<MutationObserverState>>());
        foreach (var wr in list)
        {
            if (wr.TryGetTarget(out var existing) && ReferenceEquals(existing, state))
            {
                return;
            }
        }

        list.Add(new WeakReference<MutationObserverState>(state));
    }

    private static void Dispatch(Document doc, Action<MutationObserverState> action)
    {
        if (!DocStates.TryGetValue(doc, out var list))
        {
            return;
        }

        list.RemoveAll(wr => !wr.TryGetTarget(out _));
        foreach (var wr in list)
        {
            if (wr.TryGetTarget(out var s))
            {
                action(s);
            }
        }
    }

    private static MutationObserverInit ParseOptions(JintBackendContext ctx, JsValue raw)
    {
        if (raw is not ObjectInstance o)
        {
            throw new JavaScriptException(ctx.Engine.Intrinsics.TypeError, "MutationObserver.observe: options must be an object");
        }

        bool B(string k) => TypeConverter.ToBoolean(o.Get(k));
        var childList = B("childList");
        var subtree = B("subtree");
        var attributeOldValue = o.HasProperty("attributeOldValue") && B("attributeOldValue");
        var characterDataOldValue = o.HasProperty("characterDataOldValue") && B("characterDataOldValue");

        HashSet<string>? attributeFilter = null;
        var filterVal = o.Get("attributeFilter");
        if (!filterVal.IsUndefined() && !filterVal.IsNull())
        {
            if (filterVal is not JsArray fa)
            {
                throw new JavaScriptException(ctx.Engine.Intrinsics.TypeError, "MutationObserver.observe: attributeFilter must be a sequence of strings");
            }

            attributeFilter = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < fa.Length; i++)
            {
                attributeFilter.Add(TypeConverter.ToString(fa[i]));
            }
        }

        // attributes defaults true if attributeOldValue/attributeFilter present.
        var attributes = o.HasProperty("attributes") ? B("attributes") : (attributeOldValue || attributeFilter is not null);
        var characterData = o.HasProperty("characterData") ? B("characterData") : characterDataOldValue;

        if (!childList && !attributes && !characterData)
        {
            throw new JavaScriptException(ctx.Engine.Intrinsics.TypeError,
                "MutationObserver.observe: options must include at least one of childList, attributes, or characterData");
        }

        return new MutationObserverInit(childList, attributes, characterData, subtree, attributeOldValue, characterDataOldValue, attributeFilter);
    }
}

internal readonly record struct MutationObserverInit(
    bool ChildList, bool Attributes, bool CharacterData, bool Subtree,
    bool AttributeOldValue, bool CharacterDataOldValue, HashSet<string>? AttributeFilter);

/// <summary>Per-observer host bookkeeping: callback, active observations, and a
/// pending-records queue drained on takeRecords() or microtask delivery.</summary>
internal sealed class MutationObserverState
{
    private readonly JintBackendContext _ctx;
    private readonly ObjectInstance _wrapper;
    private readonly JsValue _callback;
    private readonly List<(Node Target, MutationObserverInit Options)> _observations = new();
    private readonly List<JsObject> _pending = new();
    private bool _microtaskQueued;

    public MutationObserverState(JintBackendContext ctx, ObjectInstance wrapper, JsValue callback)
    {
        _ctx = ctx;
        _wrapper = wrapper;
        _callback = callback;
    }

    public void AddOrReplaceObservation(Node target, MutationObserverInit opts)
    {
        for (var i = 0; i < _observations.Count; i++)
        {
            if (ReferenceEquals(_observations[i].Target, target)) { _observations[i] = (target, opts); return; }
        }

        _observations.Add((target, opts));
    }

    public void Disconnect()
    {
        _observations.Clear();
        _pending.Clear();
        _microtaskQueued = false;
    }

    public void MaybeQueueAttribute(Element el, string attrName, string? oldValue)
    {
        foreach (var (target, opts) in _observations)
        {
            if (!opts.Attributes || !Matches(target, el, opts.Subtree))
            {
                continue;
            }

            if (opts.AttributeFilter is { } f && !f.Contains(attrName))
            {
                continue;
            }

            Enqueue(BuildAttributeRecord(el, attrName, opts.AttributeOldValue ? oldValue : null));
            return;
        }
    }

    public void MaybeQueueChildList(Node target, IReadOnlyList<Node>? added, IReadOnlyList<Node>? removed, Node? prev, Node? next)
    {
        foreach (var (obsTarget, opts) in _observations)
        {
            if (!opts.ChildList || !Matches(obsTarget, target, opts.Subtree))
            {
                continue;
            }

            Enqueue(BuildChildListRecord(target, added, removed, prev, next));
            return;
        }
    }

    public void MaybeQueueCharacterData(Node target, string oldValue)
    {
        foreach (var (obsTarget, opts) in _observations)
        {
            if (!opts.CharacterData || !Matches(obsTarget, target, opts.Subtree))
            {
                continue;
            }

            var r = NewRecord("characterData", target);
            Prop(r, "oldValue", opts.CharacterDataOldValue ? JintInterop.Str(oldValue) : JsValue.Null);
            Enqueue(r);
            return;
        }
    }

    public JsArray DrainRecords(global::Jint.Engine engine)
    {
        if (_pending.Count == 0)
        {
            return new JsArray(engine, System.Array.Empty<JsValue>());
        }

        var items = _pending.ToArray<JsValue>();
        _pending.Clear();
        return new JsArray(engine, items);
    }

    private static bool Matches(Node target, Node node, bool subtree)
    {
        if (ReferenceEquals(target, node))
        {
            return true;
        }

        if (!subtree)
        {
            return false;
        }

        for (var p = node.ParentNode; p is not null; p = p.ParentNode)
        {
            if (ReferenceEquals(p, target))
            {
                return true;
            }
        }

        return false;
    }

    private JsObject NewRecord(string type, Node target)
    {
        var r = new JsObject(_ctx.Engine) { Prototype = _ctx.Wrappers.MutationRecordPrototype };
        Prop(r, "type", JintInterop.Str(type));
        Prop(r, "target", _ctx.Wrappers.Wrap(target));
        Prop(r, "addedNodes", new JsArray(_ctx.Engine, System.Array.Empty<JsValue>()));
        Prop(r, "removedNodes", new JsArray(_ctx.Engine, System.Array.Empty<JsValue>()));
        Prop(r, "previousSibling", JsValue.Null);
        Prop(r, "nextSibling", JsValue.Null);
        Prop(r, "attributeName", JsValue.Null);
        Prop(r, "attributeNamespace", JsValue.Null);
        Prop(r, "oldValue", JsValue.Null);
        return r;
    }

    private JsObject BuildAttributeRecord(Element el, string attrName, string? oldValue)
    {
        var r = NewRecord("attributes", el);
        Prop(r, "attributeName", JintInterop.Str(attrName));
        Prop(r, "oldValue", oldValue is null ? JsValue.Null : JintInterop.Str(oldValue));
        return r;
    }

    private JsObject BuildChildListRecord(Node target, IReadOnlyList<Node>? added, IReadOnlyList<Node>? removed, Node? prev, Node? next)
    {
        var r = NewRecord("childList", target);
        Prop(r, "addedNodes", NodeArray(added));
        Prop(r, "removedNodes", NodeArray(removed));
        Prop(r, "previousSibling", prev is null ? JsValue.Null : _ctx.Wrappers.Wrap(prev));
        Prop(r, "nextSibling", next is null ? JsValue.Null : _ctx.Wrappers.Wrap(next));
        return r;
    }

    private JsArray NodeArray(IReadOnlyList<Node>? ns)
    {
        if (ns is null || ns.Count == 0)
        {
            return new JsArray(_ctx.Engine, System.Array.Empty<JsValue>());
        }

        var items = new JsValue[ns.Count];
        for (var i = 0; i < ns.Count; i++)
        {
            items[i] = _ctx.Wrappers.Wrap(ns[i]);
        }

        return new JsArray(_ctx.Engine, items);
    }

    private void Prop(JsObject o, string k, JsValue v)
        => o.FastSetProperty(k, new PropertyDescriptor(v, writable: false, enumerable: true, configurable: true));

    private void Enqueue(JsObject record)
    {
        _pending.Add(record);
        if (_microtaskQueued)
        {
            return;
        }

        _microtaskQueued = true;
        _ctx.Post(() => { _microtaskQueued = false; Deliver(); });
    }

    private void Deliver()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var records = DrainRecords(_ctx.Engine);
        try
        {
            _callback.Call(_wrapper, new JsValue[] { records, _wrapper });
            _ctx.Engine.Advanced.ProcessTasks();
        }
        catch (JavaScriptException ex)
        {
            _ctx.LoggerFactory.CreateLogger("Starling.engine.js")
                .LogWarning("Uncaught (in MutationObserver) {Detail}", JintInterop.DescribeError(ex.Error, ex.Message));
        }
        catch (Exception ex)
        {
            _ctx.LoggerFactory.CreateLogger("Starling.engine.js")
                .LogWarning("Uncaught (in MutationObserver) {Detail}", ex.Message);
        }
    }
}
