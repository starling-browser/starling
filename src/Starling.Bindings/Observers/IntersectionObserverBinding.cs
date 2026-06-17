using System.Runtime.CompilerServices;
using Starling.Dom;
using Starling.Js.Runtime;

namespace Starling.Bindings.Observers;

/*
 * IntersectionObserver — "run the update intersection observations steps"
 * (IO spec §3.2.2). Two entry points drive the same delivery engine:
 *
 *   - The host (interactive shell or headless renderer) calls
 *     UpdateForDocument(doc, viewport) after layout and on scroll. Each observed
 *     target's document-space border box is read from the ILayoutHost, intersected
 *     with the supplied viewport rect (scroll offset as origin, viewport size as
 *     extent), and — when the target crosses a configured threshold — an entry is
 *     queued and delivered to the callback.
 *
 *   - The backend pump calls HasPending/RunPending once per pump iteration so a
 *     target already in view fires during the navigation settle, before any host
 *     scroll update arrives (matching browsers, which always fire once after
 *     observe()). This uses the realm's current unscrolled viewport.
 *
 * Both paths track the last delivered threshold bucket per target, so a target is
 * reported exactly when its bucket changes — never twice for the same state, even
 * when the initial settle delivery and the first host update see the same layout.
 * observe() also schedules one initial update against the realm's viewport globals
 * (a context with no layout host falls back to "treat as on-screen", preserving
 * one-shot renders that gate content on the observer ever firing).
 */

/// <summary>
/// Installs the JS-visible <c>IntersectionObserver</c> constructor and prototype,
/// and drives intersection delivery both from host layout via
/// <see cref="UpdateForDocument"/> and from the backend pump via
/// <see cref="RunPending"/>. Geometry comes from the same snapshot that backs
/// <c>getBoundingClientRect</c>, so the target box, root box, and viewport agree
/// with what scripts measure directly.
/// </summary>
public static class IntersectionObserverBinding
{
    internal static readonly ConditionalWeakTable<JsObject, IntersectionObserverState> States = new();

    // Per-document registry of live observer states, so the host's
    // UpdateForDocument can iterate every observer rooted in a document without
    // holding the JS wrapper. Mirrors MutationObserverBinding.DocStates.
    internal static readonly ConditionalWeakTable<Document, List<WeakReference<IntersectionObserverState>>> DocStates = new();

    // Per-runtime registry so the backend pump can drive delivery without holding
    // a reference to every observer the page constructs. Keyed weakly on the
    // runtime; the list keeps observers alive for as long as the page can still
    // reach (and fire) them, and carries the live layout host + viewport size.
    private static readonly ConditionalWeakTable<JsRuntime, ObserverRegistry> Registries = new();

    internal sealed class ObserverRegistry
    {
        public ILayoutHost? LayoutHost;
        public double ViewportWidth;
        public double ViewportHeight;
        public readonly List<IntersectionObserverState> Live = new();
    }

    internal static void Register(Document doc, IntersectionObserverState state)
    {
        var list = DocStates.GetValue(doc, static _ => new List<WeakReference<IntersectionObserverState>>());
        foreach (var w in list)
        {
            if (w.TryGetTarget(out var s) && ReferenceEquals(s, state))
            {
                return;
            }
        }

        list.Add(new WeakReference<IntersectionObserverState>(state));
    }

    /// <summary>
    /// Runs the "update intersection observations" step for every observer in
    /// <paramref name="doc"/> against <paramref name="viewport"/> (the visible
    /// region in document CSS px — scroll offset as origin, viewport size as
    /// extent). Targets that crossed their threshold get a record delivered to
    /// the callback. Returns true when any record was delivered.
    /// </summary>
    public static bool UpdateForDocument(Document doc, LayoutRect viewport)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!DocStates.TryGetValue(doc, out var list))
        {
            return false;
        }

        var any = false;
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (!list[i].TryGetTarget(out var state)) { list.RemoveAt(i); continue; }
            any |= state.Update(viewport);
        }
        return any;
    }

    public static void Install(JsRuntime runtime, Document document)
        => Install(runtime, document, layoutHost: null, viewportWidth: 0, viewportHeight: 0);

    public static void Install(JsRuntime runtime, Document document,
        ILayoutHost? layoutHost, double viewportWidth, double viewportHeight)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(document);
        var realm = runtime.Realm;

        // The registry tracks the live viewport/host even across a re-install
        // (e.g. a relayout rebuilds the layout host); refresh it every call.
        var registry = Registries.GetOrCreateValue(runtime);
        registry.LayoutHost = layoutHost;
        registry.ViewportWidth = viewportWidth;
        registry.ViewportHeight = viewportHeight;

        if (realm.IntersectionObserverConstructor is not null)
        {
            return;
        }

        var proto = new JsObject(realm.ObjectPrototype);
        realm.IntersectionObserverPrototype = proto;
        realm.IntersectionObserverEntryPrototype = new JsObject(realm.ObjectPrototype);

        EventTargetBinding.DefineAccessor(realm, proto, "root", (thisV, _) =>
        {
            var s = ResolveState(thisV);
            return s?.Root is { } r ? JsValue.Object(DomWrappers.Wrap(realm, r)) : JsValue.Null;
        });
        EventTargetBinding.DefineAccessor(realm, proto, "rootMargin",
            (thisV, _) => JsValue.String(ResolveState(thisV)?.RootMargin ?? "0px 0px 0px 0px"));
        EventTargetBinding.DefineAccessor(realm, proto, "thresholds", (thisV, _) =>
        {
            var s = ResolveState(thisV);
            var items = new List<JsValue>();
            if (s is not null)
            {
                foreach (var t in s.Thresholds)
                {
                    items.Add(JsValue.Number(t));
                }
            }

            return JsValue.Object(new JsArray(realm, items));
        });

        EventTargetBinding.DefineMethod(realm, proto, "observe", (thisV, args) =>
        {
            var state = ResolveState(thisV)
                ?? throw new JsThrow(realm.NewTypeError("Illegal invocation: observe called on non-IntersectionObserver"));
            if (args.Length == 0 || DomWrappers.UnwrapElement(args[0]) is not { } el)
            {
                throw new JsThrow(realm.NewTypeError("IntersectionObserver.observe: target must be an Element"));
            }

            state.AddTarget(el);
            if (el.OwnerDocument is { } ownerDoc)
            {
                Register(ownerDoc, state);
            }
            // Delivery is driven by the host: the settle pump (RunPending) fires a
            // target already in view, and UpdateForDocument fires on scroll/layout.
            return JsValue.Undefined;
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, proto, "unobserve", (thisV, args) =>
        {
            var state = ResolveState(thisV);
            if (state is null || args.Length == 0)
            {
                return JsValue.Undefined;
            }

            if (DomWrappers.UnwrapElement(args[0]) is { } el)
            {
                state.RemoveTarget(el);
            }

            return JsValue.Undefined;
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, proto, "disconnect", (thisV, _) =>
        {
            ResolveState(thisV)?.Disconnect();
            return JsValue.Undefined;
        }, length: 0);

        EventTargetBinding.DefineMethod(realm, proto, "takeRecords", (thisV, _) =>
        {
            var s = ResolveState(thisV);
            return s is null ? JsValue.Object(new JsArray(realm)) : JsValue.Object(s.DrainRecords(realm));
        }, length: 0);

        var ctor = new JsNativeFunction(realm, "IntersectionObserver", 1, (thisV, args) =>
        {
            if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
            {
                throw new JsThrow(realm.NewTypeError("IntersectionObserver requires a callback function"));
            }

            var (root, rootMargin, thresholds) = ParseOptions(realm, args.Length > 1 ? args[1] : JsValue.Undefined);
            var inst = new JsObject(proto);
            var state = new IntersectionObserverState(runtime, inst, args[0], root, rootMargin, thresholds);
            States.Add(inst, state);
            registry.Live.Add(state);
            return JsValue.Object(inst);
        }, isConstructor: true);

        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));

        realm.IntersectionObserverConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("IntersectionObserver",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    /// <summary>True when at least one observed target has an undelivered
    /// intersection notification (an initial report, or a threshold-bucket
    /// change since the last delivery). The backend polls this to decide
    /// whether the page has settled.</summary>
    public static bool HasPending(JsRuntime runtime)
    {
        if (!Registries.TryGetValue(runtime, out var reg))
        {
            return false;
        }

        foreach (var obs in reg.Live)
        {
            if (obs.HasPending(reg))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Run the "update intersection observations" step for every live
    /// observer against the realm's current (unscrolled) viewport and invoke the
    /// callbacks that have queued entries. Returns true when any callback was
    /// invoked (so the backend keeps pumping). Safe to call when nothing is
    /// pending — it no-ops and returns false.</summary>
    public static bool RunPending(JsRuntime runtime)
    {
        if (!Registries.TryGetValue(runtime, out var reg))
        {
            return false;
        }

        var delivered = false;
        // Snapshot the list: a callback may construct/destroy observers.
        foreach (var obs in reg.Live.ToArray())
        {
            delivered |= obs.Deliver(reg);
        }

        return delivered;
    }

    private static IntersectionObserverState? ResolveState(JsValue thisV)
        => thisV.IsObject && States.TryGetValue(thisV.AsObject, out var s) ? s : null;

    private static (Element? root, string rootMargin, IReadOnlyList<double> thresholds) ParseOptions(JsRealm realm, JsValue raw)
    {
        Element? root = null;
        var rootMargin = "0px 0px 0px 0px";
        IReadOnlyList<double> thresholds = new[] { 0.0 };

        if (raw.IsUndefined || raw.IsNull)
        {
            return (root, rootMargin, thresholds);
        }

        if (!raw.IsObject)
        {
            throw new JsThrow(realm.NewTypeError("IntersectionObserver: options must be an object"));
        }

        var o = raw.AsObject;

        var rootV = o.Get("root");
        if (!rootV.IsUndefined && !rootV.IsNull)
        {
            if (DomWrappers.UnwrapElement(rootV) is { } el)
            {
                root = el;
            }
            // Document roots are spec-allowed too; we silently ignore non-Element roots for now.
        }

        var rm = o.Get("rootMargin");
        if (!rm.IsUndefined && !rm.IsNull)
        {
            rootMargin = JsValue.ToStringValue(rm);
        }

        var th = o.Get("threshold");
        if (!th.IsUndefined && !th.IsNull)
        {
            if (th.IsObject && th.AsObject is JsArray arr)
            {
                var list = new List<double>(arr.Length);
                for (var i = 0; i < arr.Length; i++)
                {
                    var n = JsValue.ToNumber(arr[i]);
                    if (double.IsNaN(n) || n < 0 || n > 1)
                    {
                        throw new JsThrow(realm.NewRangeError("IntersectionObserver: threshold values must be in [0, 1]"));
                    }

                    list.Add(n);
                }
                thresholds = list;
            }
            else
            {
                var n = JsValue.ToNumber(th);
                if (double.IsNaN(n) || n < 0 || n > 1)
                {
                    throw new JsThrow(realm.NewRangeError("IntersectionObserver: threshold values must be in [0, 1]"));
                }

                thresholds = new[] { n };
            }
        }

        return (root, rootMargin, thresholds);
    }
}

internal sealed class IntersectionObserverState
{
    private readonly JsRuntime _runtime;
    private readonly JsObject _observerWrapper;
    private readonly JsValue _callback;
    private readonly List<Element> _targets = new();
    // Last threshold bucket reported per target. -1 means "never reported", so
    // the first observation always fires (matching browser behavior). Shared by
    // the host-driven and pump-front delivery paths so neither double-reports.
    private readonly Dictionary<Element, int> _lastBucket = new(ReferenceEqualityComparer.Instance);
    private readonly List<JsObject> _pending = new();

    public Element? Root { get; }
    public string RootMargin { get; }
    public IReadOnlyList<double> Thresholds { get; }

    public IntersectionObserverState(JsRuntime runtime, JsObject observerWrapper, JsValue callback,
        Element? root, string rootMargin, IReadOnlyList<double> thresholds)
    {
        _runtime = runtime;
        _observerWrapper = observerWrapper;
        _callback = callback;
        Root = root;
        RootMargin = rootMargin;
        Thresholds = thresholds;
    }

    public void AddTarget(Element target)
    {
        foreach (var t in _targets)
        {
            if (ReferenceEquals(t, target))
            {
                return;
            }
        }

        _targets.Add(target);
        // A re-observed target gets a fresh initial notification.
        _lastBucket[target] = -1;
    }

    public void RemoveTarget(Element target)
    {
        for (var i = 0; i < _targets.Count; i++)
        {
            if (ReferenceEquals(_targets[i], target)) { _targets.RemoveAt(i); break; }
        }

        _lastBucket.Remove(target);
    }

    public void Disconnect()
    {
        _targets.Clear();
        _lastBucket.Clear();
        _pending.Clear();
    }

    /// <summary>Host-driven update (UpdateForDocument): recomputes intersection for
    /// every target against <paramref name="viewport"/> (document CSS px) and
    /// delivers a batch to the callback for any target whose threshold bucket
    /// changed. Returns true when the callback fired.</summary>
    public bool Update(LayoutRect viewport) => DeliverAgainst(viewport);

    /// <summary>Pump-front delivery (RunPending): recomputes against the realm's
    /// current unscrolled viewport from the registry and delivers any changes.</summary>
    public bool Deliver(IntersectionObserverBinding.ObserverRegistry reg)
        => DeliverAgainst(new LayoutRect(0, 0, reg.ViewportWidth, reg.ViewportHeight), reg.LayoutHost);

    /// <summary>True when any target's current bucket differs from the last one
    /// delivered, against the registry's unscrolled viewport.</summary>
    public bool HasPending(IntersectionObserverBinding.ObserverRegistry reg)
        => HasPendingAgainst(new LayoutRect(0, 0, reg.ViewportWidth, reg.ViewportHeight), reg.LayoutHost);

    private bool HasPendingAgainst(LayoutRect viewport, ILayoutHost? host)
    {
        host ??= WindowBinding.LayoutHostForRealm(_runtime.Realm);
        foreach (var target in _targets)
        {
            var bucket = ComputeBucket(host, viewport, target, out _, out _, out _, out _);
            if (!_lastBucket.TryGetValue(target, out var last) || last != bucket)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Compute current intersection for each target against
    /// <paramref name="viewport"/>, queue entries for any whose threshold bucket
    /// changed, and invoke the callback once with the batch. Returns true when the
    /// callback fired.</summary>
    private bool DeliverAgainst(LayoutRect viewport, ILayoutHost? host = null)
    {
        if (_targets.Count == 0)
        {
            return false;
        }

        host ??= WindowBinding.LayoutHostForRealm(_runtime.Realm);
        var rootRect = ResolveRoot(host, viewport);

        // _targets can be mutated by the callback (unobserve); iterate a copy.
        var snapshot = _targets.ToArray();
        var entries = new List<(Element Target, bool Intersecting, double Ratio, LayoutRect Bounds, LayoutRect Inter)>();
        foreach (var target in snapshot)
        {
            var bucket = ComputeBucket(host, viewport, target, out var intersecting, out var ratio, out var bounds, out var inter);
            if (_lastBucket.TryGetValue(target, out var last) && last == bucket)
            {
                continue;
            }

            _lastBucket[target] = bucket;
            entries.Add((target, intersecting, ratio, bounds, inter));
        }

        if (entries.Count == 0)
        {
            return false;
        }

        _runtime.WithActiveVm(() =>
        {
            var realm = _runtime.Realm;
            var jsEntries = new List<JsValue>(entries.Count);
            foreach (var e in entries)
            {
                jsEntries.Add(JsValue.Object(BuildEntry(realm, e.Target, e.Intersecting, e.Ratio, e.Bounds, e.Inter, rootRect)));
            }

            var arr = JsValue.Object(new JsArray(realm, jsEntries));
            try
            {
                var args = new[] { arr, JsValue.Object(_observerWrapper) };
                if (_callback.IsObject && _callback.AsObject is JsFunction fn && realm.ActiveVm is { } vm)
                {
                    vm.CallFunction(fn, JsValue.Object(_observerWrapper), args);
                }
                else
                {
                    AbstractOperations.Call(realm.ActiveVm, _callback, JsValue.Object(_observerWrapper), args);
                }
            }
            catch (JsThrow ex)
            {
                realm.ConsoleSink(ConsoleLevel.Error,
                    $"Uncaught (in IntersectionObserver callback) {DescribeThrown(ex.Value)}");
            }
            catch (Exception ex)
            {
                realm.ConsoleSink(ConsoleLevel.Error,
                    $"Uncaught (in IntersectionObserver callback) {ex.Message}");
            }
        });
        return true;
    }

    private LayoutRect ResolveRoot(ILayoutHost? host, LayoutRect viewport)
        => Root is { } r && host is not null && host.TryGetBoundingClientRect(r, out var rr)
            ? rr
            : viewport;

    /// <summary>Geometry + threshold bucket for a target against the root region.
    /// The bucket is the count of configured thresholds the current ratio meets;
    /// it changes whenever the target crosses a threshold, which is exactly when
    /// the spec queues a new entry. With no layout host at all (e.g. a non-laid-out
    /// test realm) the target is treated as fully on-screen so content gated on the
    /// observer firing still appears.</summary>
    private int ComputeBucket(ILayoutHost? host, LayoutRect viewport, Element target,
        out bool intersecting, out double ratio, out LayoutRect bounds, out LayoutRect inter)
    {
        bounds = default;
        inter = default;
        ratio = 0;
        intersecting = false;

        if (host is null)
        {
            // No layout available: treat the target as on-screen so one-shot
            // "render the whole page" contexts still fire the observer.
            ratio = 1.0;
            intersecting = true;
            return BucketFor(ratio);
        }
        if (!host.TryGetBoundingClientRect(target, out bounds))
        {
            return 0; // laid-out host, but this target has no box yet → not intersecting
        }

        var root = ResolveRoot(host, viewport);
        var ix = Math.Max(bounds.Left, root.Left);
        var iy = Math.Max(bounds.Top, root.Top);
        var ax = Math.Min(bounds.Right, root.Right);
        var ay = Math.Min(bounds.Bottom, root.Bottom);
        var iw = Math.Max(0, ax - ix);
        var ih = Math.Max(0, ay - iy);
        var interArea = iw * ih;
        inter = new LayoutRect(ix, iy, iw, ih);

        var targetArea = bounds.Width * bounds.Height;
        ratio = targetArea > 0 ? interArea / targetArea : (interArea > 0 ? 1 : 0);

        // A target "is intersecting" once it meets the smallest configured
        // threshold. A threshold of 0 means any non-zero overlap.
        var minThreshold = double.MaxValue;
        foreach (var t in Thresholds)
        {
            if (t < minThreshold)
            {
                minThreshold = t;
            }
        }

        if (minThreshold == double.MaxValue)
        {
            minThreshold = 0;
        }

        intersecting = interArea > 0 && ratio + 1e-9 >= minThreshold;

        return intersecting ? BucketFor(ratio) : 0;
    }

    private int BucketFor(double ratio)
    {
        var bucket = 0;
        foreach (var t in Thresholds)
        {
            if (ratio + 1e-9 >= t)
            {
                bucket++;
            }
        }

        return Math.Max(bucket, 1);
    }

    private JsObject BuildEntry(JsRealm realm, Element target, bool intersecting, double ratio,
        LayoutRect bounds, LayoutRect inter, LayoutRect root)
    {
        var entry = new JsObject(realm.IntersectionObserverEntryPrototype ?? realm.ObjectPrototype);
        Data(entry, "target", JsValue.Object(DomWrappers.Wrap(realm, target)));
        Data(entry, "isIntersecting", JsValue.Boolean(intersecting));
        Data(entry, "intersectionRatio", JsValue.Number(ratio));
        Data(entry, "boundingClientRect", JsValue.Object(BuildRect(realm, bounds)));
        Data(entry, "intersectionRect", JsValue.Object(BuildRect(realm, inter)));
        Data(entry, "rootBounds", JsValue.Object(BuildRect(realm, root)));
        Data(entry, "time", JsValue.Number(0));
        return entry;
    }

    private static JsObject BuildRect(JsRealm realm, LayoutRect r)
    {
        var o = new JsObject(realm.ObjectPrototype);
        Data(o, "x", JsValue.Number(r.X));
        Data(o, "y", JsValue.Number(r.Y));
        Data(o, "width", JsValue.Number(r.Width));
        Data(o, "height", JsValue.Number(r.Height));
        Data(o, "top", JsValue.Number(r.Top));
        Data(o, "right", JsValue.Number(r.Right));
        Data(o, "bottom", JsValue.Number(r.Bottom));
        Data(o, "left", JsValue.Number(r.Left));
        return o;
    }

    private static void Data(JsObject o, string name, JsValue value)
        => o.DefineOwnProperty(name,
            PropertyDescriptor.Data(value, writable: false, enumerable: true, configurable: false));

    private static string DescribeThrown(JsValue value)
        => value.IsObject ? JsValue.ToStringValue(value.AsObject.Get("message")) : JsValue.ToStringValue(value);

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
}
