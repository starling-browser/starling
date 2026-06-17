using System.Runtime.CompilerServices;
using Starling.Dom;
using Starling.Js.Runtime;

namespace Starling.Bindings.Observers;

/*
 * TODO (B5-4 partial impl): real resize firing.
 *
 * ResizeObserver fires entries at the "gather active resize observations"
 * render step (Resize Observer spec §3.4). Requires:
 *   - A layout result the bindings can read for the target's content-box /
 *     border-box / device-pixel-content-box dimensions.
 *   - A render-step hook in the event loop that walks each registered
 *     (observer, target), detects a dimension change vs the previously
 *     reported size, and accumulates entries.
 *
 * For B5-4 the JS surface is wired but no entry is ever delivered. Once
 * layout + render-step hooks land, build entries via
 * ObserverRecords.BuildResizeEntry and deliver through runtime.WithActiveVm.
 */

/// <summary>
/// B5-4 — installs the JS-visible <c>ResizeObserver</c> constructor and
/// prototype. <b>Partial implementation:</b> JS surface only — entries are
/// never produced (see file-level TODO).
/// </summary>
public static class ResizeObserverBinding
{
    internal static readonly ConditionalWeakTable<JsObject, ResizeObserverState> States = new();

    public static void Install(JsRuntime runtime, Document document)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(document);
        var realm = runtime.Realm;
        if (realm.ResizeObserverConstructor is not null)
        {
            return;
        }

        var proto = new JsObject(realm.ObjectPrototype);
        realm.ResizeObserverPrototype = proto;
        realm.ResizeObserverEntryPrototype = new JsObject(realm.ObjectPrototype);

        EventTargetBinding.DefineMethod(realm, proto, "observe", (thisV, args) =>
        {
            var state = ResolveState(thisV)
                ?? throw new JsThrow(realm.NewTypeError("Illegal invocation: observe called on non-ResizeObserver"));
            if (args.Length == 0 || DomWrappers.UnwrapElement(args[0]) is not { } el)
            {
                throw new JsThrow(realm.NewTypeError("ResizeObserver.observe: target must be an Element"));
            }

            var box = "content-box";
            if (args.Length > 1 && args[1].IsObject)
            {
                var b = args[1].AsObject.Get("box");
                if (!b.IsUndefined)
                {
                    var s = JsValue.ToStringValue(b);
                    if (s is "content-box" or "border-box" or "device-pixel-content-box")
                    {
                        box = s;
                    }
                    else
                    {
                        throw new JsThrow(realm.NewTypeError(
                            "ResizeObserver.observe: invalid box option (must be content-box, border-box, or device-pixel-content-box)"));
                    }
                }
            }
            state.AddTarget(el, box);
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

        var ctor = new JsNativeFunction(realm, "ResizeObserver", 1, (thisV, args) =>
        {
            if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
            {
                throw new JsThrow(realm.NewTypeError("ResizeObserver requires a callback function"));
            }

            var inst = new JsObject(proto);
            States.Add(inst, new ResizeObserverState(runtime, inst, args[0]));
            return JsValue.Object(inst);
        }, isConstructor: true);

        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));

        realm.ResizeObserverConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("ResizeObserver",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    private static ResizeObserverState? ResolveState(JsValue thisV)
        => thisV.IsObject && States.TryGetValue(thisV.AsObject, out var s) ? s : null;
}

internal sealed class ResizeObserverState
{
    private readonly JsRuntime _runtime;
    private readonly JsObject _observerWrapper;
    private readonly JsValue _callback;
    private readonly List<(Element Target, string Box)> _targets = new();

    public ResizeObserverState(JsRuntime runtime, JsObject observerWrapper, JsValue callback)
    {
        _runtime = runtime;
        _observerWrapper = observerWrapper;
        _callback = callback;
    }

    public void AddTarget(Element target, string box)
    {
        for (var i = 0; i < _targets.Count; i++)
        {
            if (ReferenceEquals(_targets[i].Target, target))
            {
                _targets[i] = (target, box);
                return;
            }
        }
        _targets.Add((target, box));
    }

    public void RemoveTarget(Element target)
    {
        for (var i = 0; i < _targets.Count; i++)
        {
            if (ReferenceEquals(_targets[i].Target, target)) { _targets.RemoveAt(i); return; }
        }
    }

    public void Disconnect() => _targets.Clear();
}
