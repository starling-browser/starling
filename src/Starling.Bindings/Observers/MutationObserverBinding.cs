using System.Runtime.CompilerServices;
using Starling.Dom;
using Starling.Js.Runtime;

namespace Starling.Bindings.Observers;

/*
 * TODO (B5-4 partial impl): real mutation firing.
 *
 * The Starling.Dom layer (Node.OnTreeMutated / Element.OnAttributeMutated /
 * Text.OnTreeMutated) currently bumps a per-document mutation version, but
 * does NOT raise an observable event the bindings can subscribe to. Until a
 * delegate / IObservable / IMutationSink hook lands on Document (or
 * equivalent), this binding only exposes the JS surface — calling
 * `observe(...)` succeeds and `disconnect()` / `takeRecords()` work, but
 * no MutationRecord will ever be queued in response to a JS-side
 * appendChild / setAttribute / textContent write.
 *
 * The shape needed for the B5-4 follow-up:
 *   - Document raises (Node target, MutationKind kind, ...) events from
 *     OnTreeMutated / OnAttributeMutated paths.
 *   - This binding subscribes per-document and routes per registered
 *     observer based on its target/subtree/option filter.
 *   - Records accumulate in the per-observer queue; on the first record
 *     since last delivery, queue ONE microtask via runtime.WithActiveVm
 *     that drains the queue and invokes the callback.
 *
 * See browser-plan/05_DOM.md §"mutation observer hook" and
 * browser-plan/06_RENDER_PIPELINE.md for the broader integration.
 */

/// <summary>
/// B5-4 — installs the JS-visible <c>MutationObserver</c> constructor and
/// prototype.
/// </summary>
/// <remarks>
/// <para><b>Partial implementation:</b> the JS surface is fully wired —
/// <c>new MutationObserver(cb)</c>, <c>observe(target, options)</c>,
/// <c>disconnect()</c>, <c>takeRecords()</c> are all callable and validate
/// arguments per DOM §4.3. However, mutation records are <b>never produced</b>
/// because Starling.Dom does not yet expose a mutation-event hook the bindings
/// can subscribe to (see file-level TODO).</para>
///
/// <para>Once the DOM hook lands, records will be batched per observer and
/// delivered via a single microtask invocation that calls the callback as
/// <c>(records, observer)</c> on a freshly-pumped <see cref="JsRealm.ActiveVm"/>.</para>
/// </remarks>
public static class MutationObserverBinding
{
    // observer JS wrapper → host state (callback + list of (target, options) + pending records).
    internal static readonly ConditionalWeakTable<JsObject, MutationObserverState> States = new();
    // document → live observer states. Held as WeakReferences so an observer that
    // is disconnected and dropped by JS can be garbage-collected even while the
    // Document lives; dead entries are pruned under lock before each dispatch.
    internal static readonly ConditionalWeakTable<Document, List<WeakReference<MutationObserverState>>> DocStates = new();

    internal static void RegisterState(Document doc, MutationObserverState state)
    {
        var list = DocStates.GetValue(doc, static _ => new List<WeakReference<MutationObserverState>>());
        lock (list)
        {
            // Prune dead refs and bail if this state is already registered.
            for (var i = list.Count - 1; i >= 0; i--)
            {
                if (!list[i].TryGetTarget(out var existing))
                {
                    list.RemoveAt(i);
                }
                else if (ReferenceEquals(existing, state))
                {
                    return;
                }
            }
            list.Add(new WeakReference<MutationObserverState>(state));
        }
    }

    /// <summary>Snapshot the live observer states for a document, compacting dead
    /// WeakReferences out of the backing list under lock. Returns null when no
    /// (live) observers are registered.</summary>
    private static List<MutationObserverState>? LiveStates(Document doc)
    {
        if (!DocStates.TryGetValue(doc, out var list))
        {
            return null;
        }

        List<MutationObserverState>? live = null;
        lock (list)
        {
            var write = 0;
            for (var read = 0; read < list.Count; read++)
            {
                if (!list[read].TryGetTarget(out var s))
                {
                    continue; // dead — drop
                }

                list[write++] = list[read];                        // compact, preserving order
                (live ??= new List<MutationObserverState>()).Add(s);
            }
            if (write < list.Count)
            {
                list.RemoveRange(write, list.Count - write);
            }
        }
        return live;
    }

    private static void OnAttributeChanged(Document doc, Element el, string attrName, string? oldValue)
    {
        if (LiveStates(doc) is { } states)
        {
            foreach (var s in states)
            {
                s.MaybeQueueAttribute(el, attrName, oldValue);
            }
        }
    }

    private static void OnChildListChanged(Document doc, Node target, IReadOnlyList<Node>? added, IReadOnlyList<Node>? removed, Node? prev, Node? next)
    {
        if (LiveStates(doc) is { } states)
        {
            foreach (var s in states)
            {
                s.MaybeQueueChildList(target, added, removed, prev, next);
            }
        }
    }

    private static void OnCharacterDataChanged(Document doc, Node target, string oldValue)
    {
        if (LiveStates(doc) is { } states)
        {
            foreach (var s in states)
            {
                s.MaybeQueueCharacterData(target, oldValue);
            }
        }
    }

    public static void Install(JsRuntime runtime, Document document)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(document);
        var realm = runtime.Realm;
        // Subscribe the DOM attribute-mutation hook (idempotent — overwrites with
        // an equivalent closure when re-installed on the same document).
        document.AttributeMutated = (el, attr, old) => OnAttributeChanged(document, el, attr, old);
        document.ChildListMutated = (t, a, r, p, n) => OnChildListChanged(document, t, a, r, p, n);
        document.CharacterDataMutated = (t, old) => OnCharacterDataChanged(document, t, old);
        if (realm.MutationObserverConstructor is not null)
        {
            return; // idempotent
        }

        var proto = new JsObject(realm.ObjectPrototype);
        realm.MutationObserverPrototype = proto;

        // MutationRecord.prototype — bare placeholder so records have a stable [[Prototype]].
        realm.MutationRecordPrototype = new JsObject(realm.ObjectPrototype);

        EventTargetBinding.DefineMethod(realm, proto, "observe", (thisV, args) =>
        {
            var state = ResolveState(thisV)
                ?? throw new JsThrow(realm.NewTypeError("Illegal invocation: observe called on non-MutationObserver"));
            if (args.Length == 0 || !args[0].IsObject)
            {
                throw new JsThrow(realm.NewTypeError("MutationObserver.observe: target must be a Node"));
            }

            var target = DomWrappers.UnwrapNode(args[0]);
            if (target is null)
            {
                throw new JsThrow(realm.NewTypeError("MutationObserver.observe: target must be a Node"));
            }

            var opts = ParseOptions(realm, args.Length > 1 ? args[1] : JsValue.Undefined);
            state.AddOrReplaceObservation(target, opts);
            var doc = target as Document ?? target.OwnerDocument;
            if (doc is not null)
            {
                RegisterState(doc, state);
            }

            return JsValue.Undefined;
        }, length: 2);

        EventTargetBinding.DefineMethod(realm, proto, "disconnect", (thisV, _) =>
        {
            var state = ResolveState(thisV);
            state?.Disconnect();
            return JsValue.Undefined;
        }, length: 0);

        EventTargetBinding.DefineMethod(realm, proto, "takeRecords", (thisV, _) =>
        {
            var state = ResolveState(thisV);
            if (state is null)
            {
                return JsValue.Object(new JsArray(realm));
            }

            return JsValue.Object(state.DrainRecords(realm));
        }, length: 0);

        var ctor = new JsNativeFunction(realm, "MutationObserver", 1, (thisV, args) =>
        {
            if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
            {
                throw new JsThrow(realm.NewTypeError("MutationObserver requires a callback function"));
            }

            var inst = new JsObject(proto);
            States.Add(inst, new MutationObserverState(runtime, inst, args[0]));
            return JsValue.Object(inst);
        }, isConstructor: true);

        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));

        realm.MutationObserverConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("MutationObserver",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    private static MutationObserverState? ResolveState(JsValue thisV)
        => thisV.IsObject && States.TryGetValue(thisV.AsObject, out var s) ? s : null;

    private static MutationObserverInit ParseOptions(JsRealm realm, JsValue raw)
    {
        if (!raw.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("MutationObserver.observe: options must be an object"));
        }

        var o = raw.AsObject;
        var childList = JsValue.ToBoolean(o.Get("childList"));
        var attributes = JsValue.ToBoolean(o.Get("attributes"));
        var characterData = JsValue.ToBoolean(o.Get("characterData"));
        var subtree = JsValue.ToBoolean(o.Get("subtree"));
        var attributeOldValue = JsValue.ToBoolean(o.Get("attributeOldValue"));
        var characterDataOldValue = JsValue.ToBoolean(o.Get("characterDataOldValue"));

        List<string>? attributeFilter = null;
        var afRaw = o.Get("attributeFilter");
        if (!afRaw.IsUndefined && !afRaw.IsNull)
        {
            if (!afRaw.IsObject || afRaw.AsObject is not JsArray arr)
            {
                throw new JsThrow(realm.NewTypeError("MutationObserver.observe: attributeFilter must be a sequence of strings"));
            }

            attributeFilter = new List<string>(arr.Length);
            for (var i = 0; i < arr.Length; i++)
            {
                attributeFilter.Add(JsValue.ToStringValue(arr[i]));
            }
        }

        if (!childList && !attributes && !characterData)
        {
            // DOM §4.3.1: TypeError when none of childList/attributes/characterData is true.
            throw new JsThrow(realm.NewTypeError(
                "MutationObserver.observe: options must include at least one of childList, attributes, or characterData"));
        }

        return new MutationObserverInit(
            childList, attributes, characterData, subtree,
            attributeOldValue, characterDataOldValue, attributeFilter);
    }
}

internal readonly record struct MutationObserverInit(
    bool ChildList,
    bool Attributes,
    bool CharacterData,
    bool Subtree,
    bool AttributeOldValue,
    bool CharacterDataOldValue,
    IReadOnlyList<string>? AttributeFilter);

/// <summary>Per-observer host bookkeeping. Holds the callback identity, the
/// list of active observations, and a pending-records queue drained on
/// <c>takeRecords()</c> or microtask delivery.</summary>
internal sealed class MutationObserverState
{
    private readonly JsRuntime _runtime;
    private readonly JsObject _observerWrapper;
    private readonly JsValue _callback;
    private readonly List<(Node Target, MutationObserverInit Options)> _observations = new();
    private readonly List<JsObject> _pending = new();
    private bool _microtaskQueued;

    public MutationObserverState(JsRuntime runtime, JsObject observerWrapper, JsValue callback)
    {
        _runtime = runtime;
        _observerWrapper = observerWrapper;
        _callback = callback;
    }

    public void AddOrReplaceObservation(Node target, MutationObserverInit opts)
    {
        for (var i = 0; i < _observations.Count; i++)
        {
            if (ReferenceEquals(_observations[i].Target, target))
            {
                _observations[i] = (target, opts);
                return;
            }
        }
        _observations.Add((target, opts));
    }

    public void Disconnect()
    {
        _observations.Clear();
        _pending.Clear();
        _microtaskQueued = false;
    }

    /// <summary>DOM §4.3.4 — queue an attribute MutationRecord on this observer
    /// when one of its observations matches the mutated element (target, or an
    /// ancestor with subtree) and its options select this attribute.</summary>
    public void MaybeQueueAttribute(Element el, string attrName, string? oldValue)
    {
        foreach (var (target, opts) in _observations)
        {
            if (!opts.Attributes)
            {
                continue;
            }

            if (!Matches(target, el, opts.Subtree))
            {
                continue;
            }

            if (opts.AttributeFilter is { } f && !f.Contains(attrName))
            {
                continue;
            }

            var realm = _runtime.Realm;
            EnqueueRecord(BuildAttributeRecord(realm, el, attrName,
                opts.AttributeOldValue ? oldValue : null));
            return; // at most one record per observer per mutation
        }
    }

    /// <summary>DOM §4.3.4 — queue a childList MutationRecord when an observation
    /// with childList matches the mutated parent (target, or an ancestor with
    /// subtree).</summary>
    public void MaybeQueueChildList(Node target, IReadOnlyList<Node>? added, IReadOnlyList<Node>? removed, Node? prev, Node? next)
    {
        foreach (var (obsTarget, opts) in _observations)
        {
            if (!opts.ChildList)
            {
                continue;
            }

            if (!Matches(obsTarget, target, opts.Subtree))
            {
                continue;
            }

            EnqueueRecord(BuildChildListRecord(_runtime.Realm, target, added, removed, prev, next));
            return;
        }
    }

    private static JsObject BuildChildListRecord(JsRealm realm, Node target, IReadOnlyList<Node>? added, IReadOnlyList<Node>? removed, Node? prev, Node? next)
    {
        JsValue NodeList(IReadOnlyList<Node>? ns)
        {
            if (ns is null || ns.Count == 0)
            {
                return JsValue.Object(new JsArray(realm));
            }

            var items = new JsValue[ns.Count];
            for (var i = 0; i < ns.Count; i++)
            {
                items[i] = JsValue.Object(DomWrappers.Wrap(realm, ns[i]));
            }

            return JsValue.Object(new JsArray(realm, items));
        }
        JsValue OrNull(Node? n) => n is null ? JsValue.Null : JsValue.Object(DomWrappers.Wrap(realm, n));
        var r = new JsObject(realm.MutationRecordPrototype ?? realm.ObjectPrototype);
        void P(string k, JsValue v) => r.DefineOwnProperty(k,
            PropertyDescriptor.Data(v, writable: false, enumerable: true, configurable: true));
        P("type", JsValue.String("childList"));
        P("target", JsValue.Object(DomWrappers.Wrap(realm, target)));
        P("addedNodes", NodeList(added));
        P("removedNodes", NodeList(removed));
        P("previousSibling", OrNull(prev));
        P("nextSibling", OrNull(next));
        P("attributeName", JsValue.Null);
        P("attributeNamespace", JsValue.Null);
        P("oldValue", JsValue.Null);
        return r;
    }

    /// <summary>DOM §4.3.4 — queue a characterData MutationRecord when an
    /// observation with characterData matches the mutated node.</summary>
    public void MaybeQueueCharacterData(Node target, string oldValue)
    {
        foreach (var (obsTarget, opts) in _observations)
        {
            if (!opts.CharacterData)
            {
                continue;
            }

            if (!Matches(obsTarget, target, opts.Subtree))
            {
                continue;
            }

            var realm = _runtime.Realm;
            var r = new JsObject(realm.MutationRecordPrototype ?? realm.ObjectPrototype);
            void P(string k, JsValue v) => r.DefineOwnProperty(k,
                PropertyDescriptor.Data(v, writable: false, enumerable: true, configurable: true));
            P("type", JsValue.String("characterData"));
            P("target", JsValue.Object(DomWrappers.Wrap(realm, target)));
            P("oldValue", opts.CharacterDataOldValue ? JsValue.String(oldValue) : JsValue.Null);
            P("addedNodes", JsValue.Object(new JsArray(realm)));
            P("removedNodes", JsValue.Object(new JsArray(realm)));
            P("previousSibling", JsValue.Null);
            P("nextSibling", JsValue.Null);
            P("attributeName", JsValue.Null);
            P("attributeNamespace", JsValue.Null);
            EnqueueRecord(r);
            return;
        }
    }

    private static bool Matches(Node target, Node el, bool subtree)
    {
        if (ReferenceEquals(target, el))
        {
            return true;
        }

        if (!subtree)
        {
            return false;
        }

        for (var p = el.ParentNode; p is not null; p = p.ParentNode)
        {
            if (ReferenceEquals(p, target))
            {
                return true;
            }
        }

        return false;
    }

    private static JsObject BuildAttributeRecord(JsRealm realm, Element el, string attrName, string? oldValue)
    {
        var r = new JsObject(realm.MutationRecordPrototype ?? realm.ObjectPrototype);
        void P(string k, JsValue v) => r.DefineOwnProperty(k,
            PropertyDescriptor.Data(v, writable: false, enumerable: true, configurable: true));
        P("type", JsValue.String("attributes"));
        P("target", JsValue.Object(DomWrappers.Wrap(realm, el)));
        P("attributeName", JsValue.String(attrName));
        P("attributeNamespace", JsValue.Null);
        P("oldValue", oldValue is null ? JsValue.Null : JsValue.String(oldValue));
        P("addedNodes", JsValue.Object(new JsArray(realm)));
        P("removedNodes", JsValue.Object(new JsArray(realm)));
        P("previousSibling", JsValue.Null);
        P("nextSibling", JsValue.Null);
        return r;
    }

    public JsArray DrainRecords(JsRealm realm)
    {
        if (_pending.Count == 0)
        {
            return new JsArray(realm);
        }

        var items = new List<JsValue>(_pending.Count);
        foreach (var r in _pending)
        {
            items.Add(JsValue.Object(r));
        }

        _pending.Clear();
        return new JsArray(realm, items);
    }

    /// <summary>Reserved for the DOM-side mutation hook follow-up. Queues a
    /// single microtask delivery on the first record since the last drain.</summary>
    internal void EnqueueRecord(JsObject record)
    {
        _pending.Add(record);
        if (_microtaskQueued)
        {
            return;
        }

        _microtaskQueued = true;
        _runtime.Realm.Microtasks.Enqueue(() =>
        {
            _microtaskQueued = false;
            DeliverRecords();
        });
    }

    private void DeliverRecords()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var realm = _runtime.Realm;
        var records = DrainRecords(realm);
        _runtime.WithActiveVm(() =>
        {
            try
            {
                AbstractOperations.Call(realm.ActiveVm, _callback,
                    JsValue.Object(_observerWrapper),
                    new[] { JsValue.Object(records), JsValue.Object(_observerWrapper) });
            }
            catch (JsThrow ex)
            {
                realm.ConsoleSink(ConsoleLevel.Error, $"Uncaught (in MutationObserver) {JsValue.ToStringValue(ex.Value)}");
            }
            catch (Exception ex)
            {
                realm.ConsoleSink(ConsoleLevel.Error, $"Uncaught (in MutationObserver) {ex.Message}");
            }
        });
    }
}
