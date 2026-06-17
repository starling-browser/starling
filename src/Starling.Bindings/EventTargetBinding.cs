using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Dom;
using Starling.Dom.Events;
using Starling.Js.Runtime;

namespace Starling.Bindings;

/// <summary>
/// B5-1 — Installs the JS-side <c>EventTarget</c> + <c>Event</c> bridge into a
/// realm. Populates <see cref="JsRealm.EventTargetPrototype"/>,
/// <see cref="JsRealm.EventPrototype"/>, and the matching constructor slots.
/// Listener registration on host <see cref="EventTarget"/>s is bridged via
/// a JS-listener → C#-delegate map keyed by the listener's JS object identity
/// so <c>removeEventListener(type, fn)</c> finds the same wrapper it added.
/// </summary>
/// <remarks>
/// <para>The listener wrapper looks up the realm's <see cref="JsRealm.ActiveVm"/>
/// at dispatch time. When the host fires events outside a JS-driven entry
/// (e.g. layout phase synthesizing a <c>resize</c>), a fresh <see cref="JsVm"/>
/// is created on demand. Exceptions thrown inside JS listeners are reported
/// through <see cref="JsRealm.ConsoleSink"/> and never propagate to the host.</para>
/// </remarks>
public static class EventTargetBinding
{
    private static readonly ILogger Log = NullLoggerFactory.Instance.CreateLogger(typeof(EventTargetBinding));

    // Lookup the host EventTarget that backs a JS wrapper.
    private static readonly ConditionalWeakTable<JsObject, EventTarget> WrapperToTarget = new();
    // Per host-EventTarget map of (type, capture) → (JS-listener → delegate).
    private static readonly ConditionalWeakTable<EventTarget, ListenerRegistry> Registries = new();
    // Per-realm legacy `window.event` slot: the event currently being dispatched.
    private static readonly ConditionalWeakTable<JsRealm, MutableJsValueSlot> CurrentEvent = new();
    // Host EventTargets that are the realm's Window object — needed to compute the
    // DOM "default passive value" (touch/wheel listeners on window/document/root/body).
    private static readonly ConditionalWeakTable<EventTarget, object> WindowTargets = new();
    // Host Event → its canonical JS wrapper, so a listener (and window.event) sees
    // the same Event object identity that was passed to dispatchEvent.
    private static readonly ConditionalWeakTable<Event, JsEventWrapper> EventWrappers = new();

    private static JsEventWrapper Track(JsEventWrapper wrapper)
    {
        EventWrappers.AddOrUpdate(wrapper.HostEvent, wrapper);
        return wrapper;
    }

    /// <summary>Mark a host <see cref="EventTarget"/> as the realm's Window object,
    /// so the default-passive-value computation can recognize it.</summary>
    public static void MarkWindowTarget(EventTarget host) => WindowTargets.AddOrUpdate(host, BoxedTrue);
    private static readonly object BoxedTrue = new();

    /// <summary>The legacy <c>window.event</c> value for this realm — the event
    /// being dispatched right now, or undefined when no dispatch is active.</summary>
    public static JsValue GetCurrentEvent(JsRealm realm)
        => CurrentEvent.TryGetValue(realm, out var slot) ? slot.Value : JsValue.Undefined;

    /// <summary>Install the EventTarget + Event constructors and prototypes onto the realm.</summary>
    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        if (realm.EventTargetPrototype is not null)
        {
            return; // idempotent
        }

        // ----- EventTarget.prototype
        var etProto = new JsObject(realm.ObjectPrototype);
        realm.EventTargetPrototype = etProto;

        DefineMethod(realm, etProto, "addEventListener", (thisV, args) => AddListener(realm, thisV, args), length: 2);
        DefineMethod(realm, etProto, "removeEventListener", (thisV, args) => RemoveListener(realm, thisV, args), length: 2);
        DefineMethod(realm, etProto, "dispatchEvent", (thisV, args) => DispatchEvent(realm, thisV, args), length: 1);

        var etCtor = new JsNativeFunction(realm, "EventTarget", 0, (thisV, args) =>
        {
            var inst = new JsObject(etProto);
            // A plain JS-created EventTarget gets a fresh host-side InMemoryEventTarget.
            var host = new InMemoryEventTarget();
            WrapperToTarget.Add(inst, host);
            return JsValue.Object(inst);
        }, isConstructor: true);
        etCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(etProto), writable: false, enumerable: false, configurable: false));
        etProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(etCtor), writable: true, enumerable: false, configurable: true));
        realm.EventTargetConstructor = etCtor;
        realm.GlobalObject.DefineOwnProperty("EventTarget",
            PropertyDescriptor.Data(JsValue.Object(etCtor), writable: true, enumerable: false, configurable: true));

        // ----- Event.prototype
        var evProto = new JsObject(realm.ObjectPrototype);
        realm.EventPrototype = evProto;

        DefineAccessor(realm, evProto, "type", (thisV, _) => HostEventOr(thisV, e => JsValue.String(e.Type), JsValue.String("")));
        DefineAccessor(realm, evProto, "target", (thisV, _) => HostEventOr(thisV, e =>
            e.Target is null ? JsValue.Null : JsValue.Object(DomWrappers.Wrap(realm, e.Target)), JsValue.Null));
        // DOM §2.2 — srcElement: legacy alias for target (returns null when unset).
        DefineAccessor(realm, evProto, "srcElement", (thisV, _) => HostEventOr(thisV, e =>
            e.Target is null ? JsValue.Null : JsValue.Object(DomWrappers.Wrap(realm, e.Target)), JsValue.Null));
        DefineAccessor(realm, evProto, "currentTarget", (thisV, _) => HostEventOr(thisV, e =>
            e.CurrentTarget is null ? JsValue.Null : JsValue.Object(DomWrappers.Wrap(realm, e.CurrentTarget)), JsValue.Null));
        DefineAccessor(realm, evProto, "bubbles", (thisV, _) => HostEventOr(thisV, e => JsValue.Boolean(e.Bubbles), JsValue.False));
        DefineAccessor(realm, evProto, "cancelable", (thisV, _) => HostEventOr(thisV, e => JsValue.Boolean(e.Cancelable), JsValue.False));
        DefineAccessor(realm, evProto, "composed", (thisV, _) => HostEventOr(thisV, e => JsValue.Boolean(e.Composed), JsValue.False));
        DefineAccessor(realm, evProto, "defaultPrevented", (thisV, _) => HostEventOr(thisV, e => JsValue.Boolean(e.DefaultPrevented), JsValue.False));
        DefineAccessor(realm, evProto, "eventPhase", (thisV, _) => HostEventOr(thisV, e => JsValue.Number((int)e.EventPhase), JsValue.Number(0)));
        DefineAccessor(realm, evProto, "timeStamp", (thisV, _) => HostEventOr(thisV, e => JsValue.Number(e.TimeStamp), JsValue.Number(0)));
        DefineAccessor(realm, evProto, "isTrusted", (thisV, _) => HostEventOr(thisV, e => JsValue.Boolean(e.IsTrusted), JsValue.False));
        // PopStateEvent.state surfaces on the shared Event prototype: any
        // non-popstate event returns undefined here, which matches the
        // observable behavior of real browsers where only PopStateEvent has
        // the slot.
        DefineAccessor(realm, evProto, "state",
            (thisV, _) => TryGetHostEvent(thisV, out var e) && e is PopStateEvent p && p.State is JsValue jv
                ? jv
                : JsValue.Undefined);

        DefineMethod(realm, evProto, "composedPath", (thisV, _) =>
        {
            if (!TryGetHostEvent(thisV, out var e) || e.ComposedPath.Count == 0)
            {
                return JsValue.Object(new JsArray(realm));
            }

            var items = new JsValue[e.ComposedPath.Count];
            for (var i = 0; i < e.ComposedPath.Count; i++)
            {
                items[i] = JsValue.Object(DomWrappers.Wrap(realm, e.ComposedPath[i]));
            }

            return JsValue.Object(new JsArray(realm, items));
        }, length: 0);

        DefineMethod(realm, evProto, "preventDefault", (thisV, _) =>
        {
            if (TryGetHostEvent(thisV, out var e))
            {
                e.PreventDefault();
            }

            return JsValue.Undefined;
        }, length: 0);
        DefineMethod(realm, evProto, "stopPropagation", (thisV, _) =>
        {
            if (TryGetHostEvent(thisV, out var e))
            {
                e.StopPropagation();
            }

            return JsValue.Undefined;
        }, length: 0);
        DefineMethod(realm, evProto, "stopImmediatePropagation", (thisV, _) =>
        {
            if (TryGetHostEvent(thisV, out var e))
            {
                e.StopImmediatePropagation();
            }

            return JsValue.Undefined;
        }, length: 0);
        // DOM §2.2 — cancelBubble: getter returns stopPropagation state;
        // setter of true calls stopPropagation (false is a no-op per spec).
        DefineAccessor(realm, evProto, "cancelBubble",
            (thisV, _) => TryGetHostEvent(thisV, out var e) ? JsValue.Boolean(e.PropagationStopped) : JsValue.False,
            (thisV, args) =>
            {
                if (args.Length > 0 && JsValue.ToBoolean(args[0]))
                {
                    if (TryGetHostEvent(thisV, out var e))
                    {
                        e.StopPropagation();
                    }
                }

                return JsValue.Undefined;
            });
        // DOM §2.2 — returnValue: legacy alias for !defaultPrevented (setter = preventDefault if false).
        DefineAccessor(realm, evProto, "returnValue",
            (thisV, _) => TryGetHostEvent(thisV, out var e) ? JsValue.Boolean(!e.DefaultPrevented) : JsValue.True,
            (thisV, args) =>
            {
                if (args.Length > 0 && !JsValue.ToBoolean(args[0]))
                {
                    if (TryGetHostEvent(thisV, out var e))
                    {
                        e.PreventDefault();
                    }
                }

                return JsValue.Undefined;
            });
        // Legacy Event.initEvent (DOM §2.9) — used with document.createEvent.
        // The `type` argument is mandatory per WebIDL: a missing first argument
        // is a TypeError.
        DefineMethod(realm, evProto, "initEvent", (thisV, args) =>
        {
            if (args.Length == 0)
            {
                throw new JsThrow(realm.NewTypeError("initEvent requires a type argument"));
            }

            if (TryGetHostEvent(thisV, out var e))
            {
                e.InitEvent(
                    JsValue.ToStringValue(args[0]),
                    args.Length > 1 && JsValue.ToBoolean(args[1]),
                    args.Length > 2 && JsValue.ToBoolean(args[2]));
            }

            return JsValue.Undefined;
        }, length: 3);

        var evCtor = new JsNativeFunction(realm, "Event", 1, (thisV, args) =>
        {
            if (args.Length == 0 || args[0].IsUndefined)
            {
                throw new JsThrow(realm.NewTypeError("Event constructor requires a type"));
            }

            var type = JsValue.ToStringValue(args[0]);
            var init = default(EventInit);
            if (args.Length > 1 && args[1].IsObject)
            {
                var initObj = args[1].AsObject;
                init = new EventInit(
                    Bubbles: JsValue.ToBoolean(initObj.Get("bubbles")),
                    Cancelable: JsValue.ToBoolean(initObj.Get("cancelable")),
                    Composed: JsValue.ToBoolean(initObj.Get("composed")));
            }
            var host = new Event(type, init);
            return JsValue.Object(Track(new JsEventWrapper(evProto, host)));
        }, isConstructor: true);
        evCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(evProto), writable: false, enumerable: false, configurable: false));
        evProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(evCtor), writable: true, enumerable: false, configurable: true));
        // DOM §2.2 — EventPhase constants on both the constructor and prototype.
        foreach (var (name, val) in new[] { ("NONE", 0), ("CAPTURING_PHASE", 1), ("AT_TARGET", 2), ("BUBBLING_PHASE", 3) })
        {
            var desc = PropertyDescriptor.Data(JsValue.Number(val), writable: false, enumerable: true, configurable: false);
            evCtor.DefineOwnProperty(name, desc);
            evProto.DefineOwnProperty(name, desc);
        }
        realm.EventConstructor = evCtor;
        realm.GlobalObject.DefineOwnProperty("Event",
            PropertyDescriptor.Data(JsValue.Object(evCtor), writable: true, enumerable: false, configurable: true));

        // ----- CustomEvent (inherits from Event)
        // DOM §2.2 CustomEvent — adds the `detail` property to Event. The host
        // CustomEvent class stores detail as object? but JS consumers expect it
        // as a JsValue. We stash the detail on the JsEventWrapper itself.
        var customEvProto = new JsObject(evProto);
        realm.CustomEventPrototype = customEvProto;
        DefineAccessor(realm, customEvProto, "detail", (thisV, _) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsCustomEventWrapper cw)
            {
                return cw.Detail;
            }

            return JsValue.Null;
        });
        // Legacy CustomEvent.initCustomEvent (DOM §2.3) — used with createEvent.
        // The `type` argument is mandatory per WebIDL.
        DefineMethod(realm, customEvProto, "initCustomEvent", (thisV, args) =>
        {
            if (args.Length == 0)
            {
                throw new JsThrow(realm.NewTypeError("initCustomEvent requires a type argument"));
            }

            if (TryGetHostEvent(thisV, out var e))
            {
                e.InitEvent(
                    JsValue.ToStringValue(args[0]),
                    args.Length > 1 && JsValue.ToBoolean(args[1]),
                    args.Length > 2 && JsValue.ToBoolean(args[2]));
                if (thisV.AsObject is JsCustomEventWrapper cw)
                {
                    cw.Detail = args.Length > 3 ? args[3] : JsValue.Null;
                }
            }
            return JsValue.Undefined;
        }, length: 4);

        var customEvCtor = new JsNativeFunction(realm, "CustomEvent", 1, (_, args) =>
        {
            if (args.Length == 0 || args[0].IsUndefined)
            {
                throw new JsThrow(realm.NewTypeError("CustomEvent constructor requires a type"));
            }

            var type = JsValue.ToStringValue(args[0]);
            var init = default(EventInit);
            var detail = JsValue.Null;
            if (args.Length > 1 && args[1].IsObject)
            {
                var initObj = args[1].AsObject;
                init = new EventInit(
                    Bubbles: JsValue.ToBoolean(initObj.Get("bubbles")),
                    Cancelable: JsValue.ToBoolean(initObj.Get("cancelable")),
                    Composed: JsValue.ToBoolean(initObj.Get("composed")));
                var d = initObj.Get("detail");
                if (!d.IsUndefined)
                {
                    detail = d;
                }
            }
            var host = new CustomEvent(type, init);
            CacheCustomEventDetail(host, detail);
            return JsValue.Object(Track(new JsCustomEventWrapper(customEvProto, host, detail)));
        }, isConstructor: true);
        customEvCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(customEvProto), writable: false, enumerable: false, configurable: false));
        customEvProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(customEvCtor), writable: true, enumerable: false, configurable: true));
        realm.GlobalObject.DefineOwnProperty("CustomEvent",
            PropertyDescriptor.Data(JsValue.Object(customEvCtor), writable: true, enumerable: false, configurable: true));

        // ----- UIEvent / MouseEvent / KeyboardEvent / FocusEvent (UIEvents).
        // Prototypes chain through Event.prototype; constructors read the init
        // dictionary into the host subtype. Stored on the realm so createEvent()
        // can return the correct prototype too.
        var uiEvProto = new JsObject(evProto);
        realm.UiEventPrototype = uiEvProto;
        DefineAccessor(realm, uiEvProto, "detail", (t, _) => TryGetHostEvent(t, out var e) && e is UiEvent u ? JsValue.Number(u.Detail) : JsValue.Number(0));
        DefineAccessor(realm, uiEvProto, "view", (t, _) => InitMember(t, "view", JsValue.Null));
        DefineSubtypeCtor(realm, uiEvProto, "UIEvent", (type, init) =>
            new UiEvent(type, ReadInit(init)) { Detail = (int)NumOf(init, "detail") });

        var mouseEvProto = new JsObject(uiEvProto);
        realm.MouseEventPrototype = mouseEvProto;
        DefineAccessor(realm, mouseEvProto, "clientX", (t, _) => MouseNum(t, m => m.ClientX));
        DefineAccessor(realm, mouseEvProto, "clientY", (t, _) => MouseNum(t, m => m.ClientY));
        DefineAccessor(realm, mouseEvProto, "x", (t, _) => MouseNum(t, m => m.ClientX));
        DefineAccessor(realm, mouseEvProto, "y", (t, _) => MouseNum(t, m => m.ClientY));
        DefineAccessor(realm, mouseEvProto, "screenX", (t, _) => MouseNum(t, m => m.ScreenX));
        DefineAccessor(realm, mouseEvProto, "screenY", (t, _) => MouseNum(t, m => m.ScreenY));
        DefineAccessor(realm, mouseEvProto, "pageX", (t, _) => MouseNum(t, m => m.ClientX));
        DefineAccessor(realm, mouseEvProto, "pageY", (t, _) => MouseNum(t, m => m.ClientY));
        DefineAccessor(realm, mouseEvProto, "button", (t, _) => MouseNum(t, m => m.Button));
        DefineAccessor(realm, mouseEvProto, "buttons", (t, _) => InitNum(t, "buttons"));
        DefineAccessor(realm, mouseEvProto, "ctrlKey", (t, _) => MouseBool(t, m => m.CtrlKey));
        DefineAccessor(realm, mouseEvProto, "shiftKey", (t, _) => MouseBool(t, m => m.ShiftKey));
        DefineAccessor(realm, mouseEvProto, "altKey", (t, _) => MouseBool(t, m => m.AltKey));
        DefineAccessor(realm, mouseEvProto, "metaKey", (t, _) => MouseBool(t, m => m.MetaKey));
        DefineAccessor(realm, mouseEvProto, "relatedTarget", (t, _) =>
            TryGetHostEvent(t, out var e) && e is MouseEvent m && m.RelatedTarget is { } rt ? JsValue.Object(DomWrappers.Wrap(realm, rt)) : JsValue.Null);
        DefineSubtypeCtor(realm, mouseEvProto, "MouseEvent", (type, init) => new MouseEvent(type, ReadInit(init))
        {
            ClientX = NumOf(init, "clientX"),
            ClientY = NumOf(init, "clientY"),
            ScreenX = NumOf(init, "screenX"),
            ScreenY = NumOf(init, "screenY"),
            Button = (short)NumOf(init, "button"),
            Detail = (int)NumOf(init, "detail"),
            CtrlKey = BoolOf(init, "ctrlKey"),
            ShiftKey = BoolOf(init, "shiftKey"),
            AltKey = BoolOf(init, "altKey"),
            MetaKey = BoolOf(init, "metaKey"),
            RelatedTarget = init.IsObject ? ResolveHost(init.AsObject.Get("relatedTarget")) : null,
        });

        var kbdEvProto = new JsObject(uiEvProto);
        realm.KeyboardEventPrototype = kbdEvProto;
        DefineAccessor(realm, kbdEvProto, "key", (t, _) => KbdStr(t, k => k.Key));
        DefineAccessor(realm, kbdEvProto, "code", (t, _) => KbdStr(t, k => k.Code));
        DefineAccessor(realm, kbdEvProto, "repeat", (t, _) => TryGetHostEvent(t, out var e) && e is KeyboardEvent k ? JsValue.Boolean(k.Repeat) : JsValue.False);
        DefineAccessor(realm, kbdEvProto, "location", (t, _) => InitNum(t, "location"));
        DefineAccessor(realm, kbdEvProto, "isComposing", (t, _) => InitBool(t, "isComposing"));
        DefineAccessor(realm, kbdEvProto, "charCode", (t, _) => InitNum(t, "charCode"));
        DefineAccessor(realm, kbdEvProto, "keyCode", (t, _) => InitNum(t, "keyCode"));
        DefineAccessor(realm, kbdEvProto, "which", (t, _) => InitNum(t, "which"));
        DefineAccessor(realm, kbdEvProto, "ctrlKey", (t, _) => KbdBool(t, k => k.CtrlKey));
        DefineAccessor(realm, kbdEvProto, "shiftKey", (t, _) => KbdBool(t, k => k.ShiftKey));
        DefineAccessor(realm, kbdEvProto, "altKey", (t, _) => KbdBool(t, k => k.AltKey));
        DefineAccessor(realm, kbdEvProto, "metaKey", (t, _) => KbdBool(t, k => k.MetaKey));
        DefineSubtypeCtor(realm, kbdEvProto, "KeyboardEvent", (type, init) => new KeyboardEvent(type, ReadInit(init))
        {
            Key = StrOf(init, "key"),
            Code = StrOf(init, "code"),
            Repeat = BoolOf(init, "repeat"),
            CtrlKey = BoolOf(init, "ctrlKey"),
            ShiftKey = BoolOf(init, "shiftKey"),
            AltKey = BoolOf(init, "altKey"),
            MetaKey = BoolOf(init, "metaKey"),
            Detail = (int)NumOf(init, "detail"),
        });

        var focusEvProto = new JsObject(uiEvProto);
        realm.FocusEventPrototype = focusEvProto;
        DefineAccessor(realm, focusEvProto, "relatedTarget", (t, _) =>
            TryGetHostEvent(t, out var e) && e is FocusEvent f && f.RelatedTarget is { } rt ? JsValue.Object(DomWrappers.Wrap(realm, rt)) : JsValue.Null);
        DefineSubtypeCtor(realm, focusEvProto, "FocusEvent", (type, init) => new FocusEvent(type, ReadInit(init))
        {
            Detail = (int)NumOf(init, "detail"),
            RelatedTarget = init.IsObject ? ResolveHost(init.AsObject.Get("relatedTarget")) : null,
        });

        // WheelEvent / InputEvent / CompositionEvent — minimal stubs that inherit
        // from UIEvent. No extra properties for now; these just need to be
        // constructable so `new WheelEvent('wheel')` doesn't throw.
        var wheelEvProto = new JsObject(mouseEvProto);
        DefineAccessor(realm, wheelEvProto, "deltaX", (t, _) => InitNum(t, "deltaX"));
        DefineAccessor(realm, wheelEvProto, "deltaY", (t, _) => InitNum(t, "deltaY"));
        DefineAccessor(realm, wheelEvProto, "deltaZ", (t, _) => InitNum(t, "deltaZ"));
        DefineAccessor(realm, wheelEvProto, "deltaMode", (t, _) => InitNum(t, "deltaMode"));
        DefineSubtypeCtor(realm, wheelEvProto, "WheelEvent", (type, init) => new MouseEvent(type, ReadInit(init))
        {
            ClientX = NumOf(init, "clientX"),
            ClientY = NumOf(init, "clientY"),
            ScreenX = NumOf(init, "screenX"),
            ScreenY = NumOf(init, "screenY"),
            Button = (short)NumOf(init, "button"),
            Detail = (int)NumOf(init, "detail"),
            CtrlKey = BoolOf(init, "ctrlKey"),
            ShiftKey = BoolOf(init, "shiftKey"),
            AltKey = BoolOf(init, "altKey"),
            MetaKey = BoolOf(init, "metaKey"),
            RelatedTarget = init.IsObject ? ResolveHost(init.AsObject.Get("relatedTarget")) : null,
        });

        // ----- AnimationEvent / TransitionEvent (CSS Animations 1 §5, CSS
        // Transitions 2 §events). Chain directly off Event.prototype — these
        // are not UIEvents. Host-dispatched events (engine-fired
        // animationstart / transitionend / ...) carry their values on the host
        // subtype; JS-constructed ones copy the init dictionary into the host
        // subtype so the same accessors serve both.
        var animationEvProto = new JsObject(evProto);
        realm.AnimationEventPrototype = animationEvProto;
        DefineAccessor(realm, animationEvProto, "animationName", (t, _) =>
            TryGetHostEvent(t, out var e) && e is Starling.Dom.Events.AnimationEvent a ? JsValue.String(a.AnimationName) : JsValue.String(""));
        DefineAccessor(realm, animationEvProto, "elapsedTime", (t, _) =>
            TryGetHostEvent(t, out var e) && e is Starling.Dom.Events.AnimationEvent a ? JsValue.Number(a.ElapsedTime) : JsValue.Number(0));
        DefineAccessor(realm, animationEvProto, "pseudoElement", (t, _) =>
            TryGetHostEvent(t, out var e) && e is Starling.Dom.Events.AnimationEvent a ? JsValue.String(a.PseudoElement) : JsValue.String(""));
        DefineSubtypeCtor(realm, animationEvProto, "AnimationEvent", (type, init) =>
            new Starling.Dom.Events.AnimationEvent(type, ReadInit(init))
            {
                AnimationName = StrOf(init, "animationName"),
                ElapsedTime = NumOf(init, "elapsedTime"),
                PseudoElement = StrOf(init, "pseudoElement"),
            });

        var transitionEvProto = new JsObject(evProto);
        realm.TransitionEventPrototype = transitionEvProto;
        DefineAccessor(realm, transitionEvProto, "propertyName", (t, _) =>
            TryGetHostEvent(t, out var e) && e is Starling.Dom.Events.TransitionEvent tr ? JsValue.String(tr.PropertyName) : JsValue.String(""));
        DefineAccessor(realm, transitionEvProto, "elapsedTime", (t, _) =>
            TryGetHostEvent(t, out var e) && e is Starling.Dom.Events.TransitionEvent tr ? JsValue.Number(tr.ElapsedTime) : JsValue.Number(0));
        DefineAccessor(realm, transitionEvProto, "pseudoElement", (t, _) =>
            TryGetHostEvent(t, out var e) && e is Starling.Dom.Events.TransitionEvent tr ? JsValue.String(tr.PseudoElement) : JsValue.String(""));
        DefineSubtypeCtor(realm, transitionEvProto, "TransitionEvent", (type, init) =>
            new Starling.Dom.Events.TransitionEvent(type, ReadInit(init))
            {
                PropertyName = StrOf(init, "propertyName"),
                ElapsedTime = NumOf(init, "elapsedTime"),
                PseudoElement = StrOf(init, "pseudoElement"),
            });

        var inputEvProto = new JsObject(uiEvProto);
        DefineSubtypeCtor(realm, inputEvProto, "InputEvent", (type, init) => new UiEvent(type, ReadInit(init)));

        var compositionEvProto = new JsObject(uiEvProto);
        DefineAccessor(realm, compositionEvProto, "data", (t, _) => JsValue.String(JsValue.ToStringValue(InitMember(t, "data", JsValue.String("")))));
        DefineSubtypeCtor(realm, compositionEvProto, "CompositionEvent", (type, init) => new UiEvent(type, ReadInit(init))
        {
            Detail = (int)NumOf(init, "detail"),
        });

        // HashChangeEvent / PopStateEvent / StorageEvent / MessageEvent — stubs
        // for tests that reference these constructors.
        foreach (var (name, proto) in new[]
        {
            ("HashChangeEvent", new JsObject(evProto)),
            ("MessageEvent", new JsObject(evProto)),
            ("StorageEvent", new JsObject(evProto)),
            ("BeforeUnloadEvent", new JsObject(evProto)),
            ("TextEvent", new JsObject(uiEvProto)),
            ("DragEvent", new JsObject(mouseEvProto)),
        })
        {
            var captured = proto;
            var capturedName = name;
            DefineSubtypeCtor(realm, captured, capturedName,
                (type, init) => new Event(type, ReadInit(init)));
        }

        // NodeList, HTMLCollection, DOMTokenList — expose interface constructor
        // objects on window so `instanceof NodeList` etc. can be checked.
        // These don't need real constructors since no WPT test creates instances
        // directly; they just need to exist as non-null objects.
        foreach (var ifaceName in new[] { "NodeList", "HTMLCollection", "DOMTokenList" })
        {
            var ifaceProto = new JsObject(realm.ObjectPrototype);
            var ifaceCtor = new JsNativeFunction(realm, ifaceName, 0, (_, _) =>
                throw new JsThrow(realm.NewTypeError("Illegal constructor")), isConstructor: false);
            ifaceCtor.DefineOwnProperty("prototype",
                PropertyDescriptor.Data(JsValue.Object(ifaceProto), writable: false, enumerable: false, configurable: false));
            ifaceProto.DefineOwnProperty("constructor",
                PropertyDescriptor.Data(JsValue.Object(ifaceCtor), writable: true, enumerable: false, configurable: true));
            realm.GlobalObject.DefineOwnProperty(ifaceName,
                PropertyDescriptor.Data(JsValue.Object(ifaceCtor), writable: true, enumerable: false, configurable: true));
        }
    }

    // ----- helpers for the UIEvents subtype constructors -----
    private static EventInit ReadInit(JsValue v) => v.IsObject
        ? new EventInit(JsValue.ToBoolean(v.AsObject.Get("bubbles")),
                        JsValue.ToBoolean(v.AsObject.Get("cancelable")),
                        JsValue.ToBoolean(v.AsObject.Get("composed")))
        : default;
    private static double NumOf(JsValue v, string k) => v.IsObject && !v.AsObject.Get(k).IsUndefined ? JsValue.ToNumber(v.AsObject.Get(k)) : 0;
    private static bool BoolOf(JsValue v, string k) => v.IsObject && JsValue.ToBoolean(v.AsObject.Get(k));
    private static string StrOf(JsValue v, string k) => v.IsObject && v.AsObject.Get(k).IsString ? v.AsObject.Get(k).AsString : "";
    /// <summary>Read a member of the init dictionary the event was constructed
    /// with. Returns <paramref name="fallback"/> when the wrapper carries no init
    /// dict or the member is undefined.</summary>
    private static JsValue InitMember(JsValue thisV, string key, JsValue fallback)
    {
        if (thisV.IsObject && thisV.AsObject is JsEventWrapper w && w.InitDict.IsObject)
        {
            var v = w.InitDict.AsObject.Get(key);
            if (!v.IsUndefined)
            {
                return v;
            }
        }
        return fallback;
    }
    private static JsValue InitNum(JsValue thisV, string key) => JsValue.Number(JsValue.ToNumber(InitMember(thisV, key, JsValue.Number(0))));
    private static JsValue InitBool(JsValue thisV, string key) => JsValue.Boolean(JsValue.ToBoolean(InitMember(thisV, key, JsValue.False)));

    private static JsValue MouseNum(JsValue t, Func<MouseEvent, double> f) => TryGetHostEvent(t, out var e) && e is MouseEvent m ? JsValue.Number(f(m)) : JsValue.Number(0);
    private static JsValue MouseBool(JsValue t, Func<MouseEvent, bool> f) => TryGetHostEvent(t, out var e) && e is MouseEvent m ? JsValue.Boolean(f(m)) : JsValue.False;
    private static JsValue KbdStr(JsValue t, Func<KeyboardEvent, string> f) => TryGetHostEvent(t, out var e) && e is KeyboardEvent k ? JsValue.String(f(k)) : JsValue.String("");
    private static JsValue KbdBool(JsValue t, Func<KeyboardEvent, bool> f) => TryGetHostEvent(t, out var e) && e is KeyboardEvent k ? JsValue.Boolean(f(k)) : JsValue.False;

    private static void DefineSubtypeCtor(JsRealm realm, JsObject proto, string name, Func<string, JsValue, Event> build)
    {
        var ctor = new JsNativeFunction(realm, name, 1, (_, args) =>
        {
            if (args.Length == 0 || args[0].IsUndefined)
            {
                throw new JsThrow(realm.NewTypeError($"{name} constructor requires a type"));
            }

            var init = args.Length > 1 ? args[1] : JsValue.Undefined;
            // UIEvents §3.5 — `view` must be a Window (a Window wrapper) or null.
            // A primitive like a Number is a TypeError per WebIDL interface coercion.
            if (init.IsObject)
            {
                var view = init.AsObject.Get("view");
                if (!view.IsUndefined && !view.IsNull && !view.IsObject)
                {
                    throw new JsThrow(realm.NewTypeError($"{name}: 'view' member is not a Window"));
                }
            }
            return JsValue.Object(Track(new JsEventWrapper(proto, build(JsValue.ToStringValue(args[0]), init), init)));
        }, isConstructor: true);
        ctor.DefineOwnProperty("prototype", PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));
        proto.DefineOwnProperty("constructor", PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
        realm.GlobalObject.DefineOwnProperty(name, PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    /// <summary>Legacy <c>document.createEvent(interface)</c> (DOM §2.9). Returns
    /// an uninitialized event of the requested legacy interface; the caller must
    /// call <c>initEvent</c>/<c>initCustomEvent</c> before dispatch. Throws a
    /// TypeError for an unrecognized interface (DOMException NotSupportedError is
    /// not yet modeled). Subtype-specific accessors aren't surfaced here; the
    /// legacy path is dominated by Event/CustomEvent + init + dispatch.</summary>
    public static JsValue CreateLegacyEvent(JsRealm realm, string interfaceName)
    {
        ArgumentNullException.ThrowIfNull(realm);
        switch ((interfaceName ?? "").ToLowerInvariant())
        {
            case "customevent":
                {
                    var ce = new CustomEvent("");
                    ce.MarkAsUninitialized();
                    return JsValue.Object(Track(new JsCustomEventWrapper(realm.CustomEventPrototype!, ce, JsValue.Null)));
                }
            case "event":
            case "events":
            case "htmlevents":
            case "svgevents":
            // Interfaces in the legacy createEvent table that Starling has no
            // dedicated subtype for yet map to a base Event. They are returned
            // uninitialized, so dispatching one before initEvent() throws
            // InvalidStateError per DOM §2.7.
            case "beforeunloadevent":
            case "compositionevent":
            case "textevent":
            case "hashchangeevent":
            case "messageevent":
            case "storageevent":
            case "dragevent":
            case "devicemotionevent":
            case "deviceorientationevent":
            case "touchevent":
            case "wheelevent":
            case "popstateevent":
                return WrapUninitEvent(realm, new Event(""));
            case "uievent":
            case "uievents":
                return WrapUninitEvent(realm, new UiEvent(""));
            case "mouseevent":
            case "mouseevents":
                return WrapUninitEvent(realm, new MouseEvent(""));
            case "keyboardevent":
            case "keyevents":
                return WrapUninitEvent(realm, new KeyboardEvent(""));
            case "focusevent":
                return WrapUninitEvent(realm, new FocusEvent(""));
            default:
                // DOM §4.5.1 — createEvent throws "NotSupportedError" (not a
                // TypeError) for an interface outside the legacy event table, so
                // assert_throws_dom("NOT_SUPPORTED_ERR") matches (code 9).
                throw DomExceptionBinding.Throw(realm, "NotSupportedError",
                    $"createEvent: unsupported interface '{interfaceName}'");
        }
    }

    private static JsValue WrapUninitEvent(JsRealm realm, Event host)
    {
        // DOM §2.9: events created via createEvent() are uninitialized until
        // initEvent() is called. Mark them so dispatchEvent can throw
        // InvalidStateError if dispatch is attempted before initialization.
        host.MarkAsUninitialized();
        var proto = host switch
        {
            MouseEvent => realm.MouseEventPrototype,
            KeyboardEvent => realm.KeyboardEventPrototype,
            FocusEvent => realm.FocusEventPrototype,
            UiEvent => realm.UiEventPrototype,
            _ => realm.EventPrototype,
        } ?? realm.EventPrototype!;
        return JsValue.Object(Track(new JsEventWrapper(proto, host)));
    }

    /// <summary>Associate an existing host <see cref="EventTarget"/> with a JS
    /// wrapper object so JS-side listener registrations route to the same
    /// host instance. Called by <see cref="DomWrappers"/> when a Node is
    /// wrapped for the first time, and by Window install.</summary>
    public static void BindWrapper(JsObject wrapper, EventTarget host)
    {
        if (WrapperToTarget.TryGetValue(wrapper, out _))
        {
            return;
        }

        WrapperToTarget.Add(wrapper, host);
    }

    /// <summary>Resolve the JS wrapper's underlying host EventTarget, or null.</summary>
    internal static EventTarget? ResolveHost(JsValue thisV) =>
        thisV.IsObject && WrapperToTarget.TryGetValue(thisV.AsObject, out var t) ? t : null;

    // ---- addEventListener / removeEventListener / dispatchEvent

    private static JsValue AddListener(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var host = ResolveHost(thisV);
        if (host is null || args.Length < 2)
        {
            return JsValue.Undefined;
        }

        var type = JsValue.ToStringValue(args[0]);
        // DOM §addEventListener: flatten the options first (so a `capture` getter
        // runs) even if the callback is null. The listener may be a callable
        // (a function) OR any object with a callable `handleEvent` method; a null
        // or non-object callback adds nothing.
        var (capture, once, passiveOpt) = ParseListenerOptions(args.Length > 2 ? args[2] : JsValue.Undefined);
        if (!args[1].IsObject)
        {
            return JsValue.Undefined;
        }

        var passive = passiveOpt ?? DefaultPassiveValue(host, type);
        var listenerObj = args[1].AsObject;

        var registry = Registries.GetValue(host, _ => new ListenerRegistry());
        var key = (type, capture);
        var byListener = registry.GetOrCreate(key);
        if (byListener.ContainsKey(listenerObj))
        {
            return JsValue.Undefined;
        }

        // Create a delegate that re-enters the JS engine on dispatch. When
        // `once` is set, the host auto-removes its own entry; we clean up the
        // JS-side identity map inside the wrapper so a subsequent
        // addEventListener with the same listener re-registers cleanly.
        EventListener wrapper = null!;
        wrapper = ev =>
        {
            if (once)
            {
                byListener.Remove(listenerObj);
            }

            InvokeJsListener(realm, listenerObj, ev);
        };
        byListener[listenerObj] = wrapper;
        host.AddEventListener(type, wrapper, new AddEventListenerOptions(capture, once, passive));
        return JsValue.Undefined;
    }

    private static JsValue RemoveListener(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var host = ResolveHost(thisV);
        if (host is null || args.Length < 2)
        {
            return JsValue.Undefined;
        }

        var type = JsValue.ToStringValue(args[0]);
        // Flatten options (running any `capture` getter) before bailing on a
        // null/non-object callback, matching addEventListener.
        var (capture, _, _) = ParseListenerOptions(args.Length > 2 ? args[2] : JsValue.Undefined);
        if (!args[1].IsObject)
        {
            return JsValue.Undefined;
        }

        var listenerObj = args[1].AsObject;

        if (!Registries.TryGetValue(host, out var registry))
        {
            return JsValue.Undefined;
        }

        var key = (type, capture);
        var byListener = registry.TryGet(key);
        if (byListener is null)
        {
            return JsValue.Undefined;
        }

        if (!byListener.TryGetValue(listenerObj, out var wrapper))
        {
            return JsValue.Undefined;
        }

        byListener.Remove(listenerObj);
        host.RemoveEventListener(type, wrapper, new RemoveEventListenerOptions(capture));
        return JsValue.Undefined;
    }

    /// <summary>Dispatch a host-built event on the EventTarget backing a JS
    /// wrapper (e.g. HTMLElement.click()'s synthetic MouseEvent). Runs the same
    /// host listeners — including JS-bridged ones — as dispatchEvent.</summary>
    public static bool DispatchHostEvent(JsValue target, Event ev)
    {
        var host = ResolveHost(target);
        return host is not null && host.DispatchEvent(ev);
    }

    private static JsValue DispatchEvent(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var host = ResolveHost(thisV);
        if (host is null)
        {
            return JsValue.False;
        }

        if (args.Length == 0 || !args[0].IsObject || args[0].AsObject is not JsEventWrapper wrapper)
        {
            throw new JsThrow(realm.NewTypeError("dispatchEvent requires an Event instance"));
        }

        var ev = wrapper.HostEvent;
        // DOM §2.7 step 1: "If event's dispatch flag is set … throw InvalidStateError"
        if (ev.IsBeingDispatched)
        {
            throw DomExceptionBinding.Throw(realm, "InvalidStateError",
                "The event is already being dispatched.");
        }
        // DOM §2.7 step 2: "If event's initialized flag is not set … throw InvalidStateError"
        if (!ev.Initialized)
        {
            throw DomExceptionBinding.Throw(realm, "InvalidStateError",
                "The event has not been initialized (call initEvent first).");
        }

        try
        {
            return JsValue.Boolean(host.DispatchEvent(ev));
        }
        catch (InvalidOperationException ex)
        {
            throw DomExceptionBinding.Throw(realm, "InvalidStateError", ex.Message);
        }
    }

    // ---- DispatchEvent overload helper (accepts CustomEvent wrapper too)
    // JsCustomEventWrapper inherits JsEventWrapper so the cast above covers it.

    // ---- Helpers

    /// <summary>Pump a JS listener call. Synthesizes a VM if none is active so
    /// host-driven dispatches still work.</summary>
    private static void InvokeJsListener(JsRealm realm, JsObject listener, Event ev)
    {
        var slot = CurrentEvent.GetValue(realm, _ => new MutableJsValueSlot());
        var prevEvent = slot.Value;
        try
        {
            var vm = realm.ActiveVm ?? new JsVm(GetRuntimeForRealm(realm));
            var jsEvent = JsValue.Object(WrapHostEvent(realm, ev));
            // Legacy `window.event` (HTML §window.event): the event being
            // dispatched is exposed on the global for the duration of the call.
            slot.Value = jsEvent;

            // DOM §2.7 invoke: if the listener is callable, call it with `this`
            // set to the current target. Otherwise it must be an object with a
            // `handleEvent` method, which is re-fetched on every dispatch and
            // called with `this` set to the listener object itself.
            if (AbstractOperations.IsCallable(JsValue.Object(listener)))
            {
                JsValue thisVal = ev.CurrentTarget is null
                    ? JsValue.Undefined
                    : JsValue.Object(DomWrappers.Wrap(realm, ev.CurrentTarget));
                AbstractOperations.Call(vm, JsValue.Object(listener), thisVal, new[] { jsEvent });
            }
            else
            {
                // Use the abstract Get so a `handleEvent` accessor getter actually
                // runs (and re-runs on every dispatch), per the DOM invoke step.
                var handle = AbstractOperations.Get(vm, listener, "handleEvent");
                if (AbstractOperations.IsCallable(handle))
                {
                    AbstractOperations.Call(vm, handle, JsValue.Object(listener), new[] { jsEvent });
                }
            }
        }
        catch (JsThrow jt)
        {
            ReportListenerError(realm, JsValue.ToStringValue(jt.Value), jt.Value);
        }
        catch (Exception ex)
        {
            ReportListenerError(realm, ex.Message, JsValue.Undefined);
        }
        finally
        {
            slot.Value = prevEvent;
        }
    }

    /// <summary>Report an uncaught error thrown by a JS event listener. HTML
    /// "report the exception": invoke the legacy <c>window.onerror</c> handler
    /// (message, source, lineno, colno, error) if one is installed, then always
    /// echo to the console. The boolean result of onerror (which can cancel) is
    /// ignored here since there is no default error UI to suppress.</summary>
    private static void ReportListenerError(JsRealm realm, string message, JsValue error)
    {
        var legacyMessage = "Uncaught " + message;
        try
        {
            var onerror = realm.GlobalObject.Get("onerror");
            if (AbstractOperations.IsCallable(onerror))
            {
                var vm = realm.ActiveVm ?? new JsVm(GetRuntimeForRealm(realm));
                AbstractOperations.Call(vm, onerror, JsValue.Object(realm.GlobalObject), new[]
                {
                    JsValue.String(legacyMessage),
                    JsValue.String(""),       // source URL — not tracked here
                    JsValue.Number(0),        // lineno
                    JsValue.Number(0),        // colno
                    error,                    // the thrown value (undefined for host errors)
                });
            }
        }
        catch (Exception ex)
        {
            // An error inside onerror itself must not escape the dispatch loop.
            EventTargetBindingLog.OnerrorHandlerThrew(Log, ex);
        }
        realm.ConsoleSink(ConsoleLevel.Error, $"Uncaught (in event listener) {message}");
    }

    /// <summary>Resolve the runtime that owns the given realm. <see cref="JsRuntime"/>
    /// does not expose itself on the realm directly, so we stash it on install.</summary>
    private static JsRuntime GetRuntimeForRealm(JsRealm realm)
        => WindowBinding.RuntimeForRealm(realm) ?? throw new InvalidOperationException(
            "Cannot synthesize a VM: realm has no associated JsRuntime. Was WindowBinding.Install ever called?");

    private static JsEventWrapper WrapHostEvent(JsRealm realm, Event ev)
    {
        // Return the canonical wrapper if this host event already has one (e.g.
        // a JS-constructed event passed to dispatchEvent) so listeners and
        // window.event observe the same object identity.
        if (EventWrappers.TryGetValue(ev, out var existing))
        {
            return existing;
        }

        // When the host event is a CustomEvent that originated from JS (and
        // was therefore a JsCustomEventWrapper), we need to preserve the JS
        // detail value. The listener wrapper for the JS-created custom event
        // IS itself the JsCustomEventWrapper stored in the runtime.  We walk
        // the realm's custom event registry to find it, but the simplest
        // approach is: wrap a fresh JsCustomEventWrapper when the host event
        // is a CustomEvent so the listener sees `e.detail`.
        if (ev is CustomEvent ce)
        {
            var ceProto = GetCustomEventProto(realm);
            // Try to locate the JS detail value that was stored when the event
            // was constructed. We stash it in CustomEventDetailCache.
            var detail = CustomEventDetailCache.TryGetValue(ce, out var box) ? box.Value : JsValue.Null;
            return Track(new JsCustomEventWrapper(ceProto ?? realm.EventPrototype ?? realm.ObjectPrototype, ce, detail));
        }
        // Combine the branch's canonical-identity Track() with the host-aware
        // prototype selection so a wrapped event both keeps a stable identity
        // across dispatch and gets the right subtype prototype.
        return Track(new JsEventWrapper(GetEventPrototypeForHost(realm, ev), ev));
    }

    // Cache: CustomEvent → JsValue detail. Populated when JS constructs new CustomEvent(...).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CustomEvent, JsValueBox>
        CustomEventDetailCache = new();

    internal static void CacheCustomEventDetail(CustomEvent ev, JsValue detail)
    {
        CustomEventDetailCache.GetValue(ev, _ => new JsValueBox(detail));
    }

    private static JsObject? GetCustomEventProto(JsRealm realm)
    {
        // The CustomEvent prototype is stored as the prototype of any
        // JsCustomEventWrapper in this realm — we retrieve it via the
        // global "CustomEvent" constructor's .prototype property.
        var customEvCtor = realm.GlobalObject.Get("CustomEvent");
        if (customEvCtor.IsObject)
        {
            var proto = customEvCtor.AsObject.Get("prototype");
            if (proto.IsObject)
            {
                return proto.AsObject;
            }
        }
        return null;
    }

    private static JsObject GetEventPrototypeForHost(JsRealm realm, Event ev) =>
        ev switch
        {
            MouseEvent => realm.MouseEventPrototype ?? realm.UiEventPrototype ?? realm.EventPrototype ?? realm.ObjectPrototype,
            KeyboardEvent => realm.KeyboardEventPrototype ?? realm.UiEventPrototype ?? realm.EventPrototype ?? realm.ObjectPrototype,
            FocusEvent => realm.FocusEventPrototype ?? realm.UiEventPrototype ?? realm.EventPrototype ?? realm.ObjectPrototype,
            UiEvent => realm.UiEventPrototype ?? realm.EventPrototype ?? realm.ObjectPrototype,
            Starling.Dom.Events.AnimationEvent => realm.AnimationEventPrototype ?? realm.EventPrototype ?? realm.ObjectPrototype,
            Starling.Dom.Events.TransitionEvent => realm.TransitionEventPrototype ?? realm.EventPrototype ?? realm.ObjectPrototype,
            _ => realm.EventPrototype ?? realm.ObjectPrototype,
        };

    internal static bool TryGetHostEvent(JsValue v, out Event ev)
    {
        if (v.IsObject && v.AsObject is JsEventWrapper w) { ev = w.HostEvent; return true; }
        ev = null!;
        return false;
    }

    private static JsValue HostEventOr(JsValue thisV, Func<Event, JsValue> read, JsValue fallback)
        => TryGetHostEvent(thisV, out var e) ? read(e) : fallback;

    // passive is null when the `passive` member was omitted/undefined, so the
    // caller can substitute the DOM default-passive value.
    private static (bool capture, bool once, bool? passive) ParseListenerOptions(JsValue opt)
    {
        // DOM §flatten options: an object form reads capture/once/passive; any
        // other value (boolean, number, string, …) is coerced to the capture
        // boolean (e.g. 2.3 → true, "" → false).
        if (opt.IsObject)
        {
            var o = opt.AsObject;
            var passiveVal = o.Get("passive");
            return (
                JsValue.ToBoolean(o.Get("capture")),
                JsValue.ToBoolean(o.Get("once")),
                passiveVal.IsUndefined ? null : JsValue.ToBoolean(passiveVal));
        }
        if (opt.IsUndefined || opt.IsNull)
        {
            return (false, false, null);
        }

        return (JsValue.ToBoolean(opt), false, null);
    }

    /// <summary>DOM "default passive value": touch/wheel listeners on the Window,
    /// the Document, the document element, or the body element are passive by
    /// default. Everything else defaults to non-passive.</summary>
    private static bool DefaultPassiveValue(EventTarget host, string type)
    {
        if (type is not ("touchstart" or "touchmove" or "wheel" or "mousewheel"))
        {
            return false;
        }

        if (WindowTargets.TryGetValue(host, out _))
        {
            return true;
        }

        if (host is Document)
        {
            return true;
        }

        if (host is Node node)
        {
            var doc = node.OwnerDocument;
            if (doc is not null && (ReferenceEquals(doc.DocumentElement, node) || ReferenceEquals(doc.Body, node)))
            {
                return true;
            }
        }
        return false;
    }

    internal static void DefineMethod(JsRealm realm, JsObject target, string name,
        Func<JsValue, JsValue[], JsValue> body, int length)
    {
        var fn = new JsNativeFunction(realm, name, length, body, isConstructor: false);
        target.DefineOwnProperty(name, PropertyDescriptor.BuiltinMethod(JsValue.Object(fn)));
    }

    internal static void DefineAccessor(JsRealm realm, JsObject target, string name,
        Func<JsValue, JsValue[], JsValue> getter,
        Func<JsValue, JsValue[], JsValue>? setter = null)
    {
        var get = new JsNativeFunction(realm, $"get {name}", 0, getter, isConstructor: false);
        JsObject? set = null;
        if (setter is not null)
        {
            set = new JsNativeFunction(realm, $"set {name}", 1, setter, isConstructor: false);
        }

        target.DefineOwnProperty(name,
            PropertyDescriptor.Accessor(get, set, enumerable: true, configurable: true));
    }

    // ---- HTML §8.1.8 — IDL event handler attributes (onload, onerror, …)

    /// <summary>Per-wrapper storage for IDL event handler values plus the set
    /// of event types whose bridging host listener is already registered. The
    /// bridge listener reads the CURRENT slot value at dispatch time (the spec's
    /// "event handler processing algorithm" semantics), so reassigning or
    /// clearing the property needs no listener churn.</summary>
    private sealed class EventHandlerSlots
    {
        public readonly Dictionary<string, JsValue> Values = new(StringComparer.Ordinal);
        public readonly HashSet<string> Registered = new(StringComparer.Ordinal);
    }

    private static readonly ConditionalWeakTable<JsObject, EventHandlerSlots> HandlerSlots = new();

    /// <summary>Define an <c>on&lt;type&gt;</c> event-handler accessor pair on a
    /// wrapper prototype (HTML §8.1.8.1). Setting a callable registers one
    /// lazy bridge listener on the backing host EventTarget; the bridge invokes
    /// whatever the slot holds when the event fires. Setting null (or any
    /// non-callable) deactivates the handler. Bundlers rely on this shape —
    /// webpack's chunk loader wires <c>script.onload</c>/<c>script.onerror</c>
    /// as properties, never via addEventListener.</summary>
    internal static void DefineEventHandlerAccessor(JsRealm realm, JsObject proto, string type)
    {
        DefineAccessor(realm, proto, "on" + type,
            getter: (thisV, _) =>
                thisV.IsObject
                && HandlerSlots.TryGetValue(thisV.AsObject, out var slots)
                && slots.Values.TryGetValue(type, out var v)
                    ? v
                    : JsValue.Null,
            setter: (thisV, args) =>
            {
                if (!thisV.IsObject)
                {
                    return JsValue.Undefined;
                }

                var wrapper = thisV.AsObject;
                var value = args.Length > 0 ? args[0] : JsValue.Undefined;
                var slots = HandlerSlots.GetValue(wrapper, _ => new EventHandlerSlots());

                if (!AbstractOperations.IsCallable(value))
                {
                    slots.Values.Remove(type);
                    return JsValue.Undefined;
                }

                slots.Values[type] = value;
                if (!slots.Registered.Add(type))
                {
                    return JsValue.Undefined;
                }

                var host = ResolveHost(thisV);
                if (host is null)
                {
                    // No backing host target — forget the registration so a
                    // later set retries once the wrapper is bound.
                    slots.Registered.Remove(type);
                    return JsValue.Undefined;
                }

                host.AddEventListener(type, ev =>
                {
                    if (HandlerSlots.TryGetValue(wrapper, out var s)
                        && s.Values.TryGetValue(type, out var h)
                        && h.IsObject)
                    {
                        InvokeJsListener(realm, h.AsObject, ev);
                    }
                }, new AddEventListenerOptions(Capture: false, Once: false, Passive: false));
                return JsValue.Undefined;
            });
    }

    /// <summary>The GlobalEventHandlers + common element-level event types that
    /// get <c>on*</c> accessors on the Element prototype. Order is cosmetic.</summary>
    internal static readonly string[] ElementEventHandlerTypes =
    {
        "abort", "animationend", "animationiteration", "animationstart",
        "auxclick", "beforeinput", "blur", "cancel", "canplay",
        "canplaythrough", "change", "click", "close", "contextmenu",
        "copy", "cut", "dblclick", "drag", "dragend", "dragenter",
        "dragleave", "dragover", "dragstart", "drop", "durationchange",
        "emptied", "ended", "error", "focus", "focusin", "focusout",
        "input", "invalid", "keydown", "keypress", "keyup", "load",
        "loadeddata", "loadedmetadata", "loadstart", "mousedown",
        "mouseenter", "mouseleave", "mousemove", "mouseout", "mouseover",
        "mouseup", "paste", "pause", "play", "playing", "pointercancel",
        "pointerdown", "pointerenter", "pointerleave", "pointermove",
        "pointerout", "pointerover", "pointerup", "progress", "ratechange",
        "reset", "resize", "scroll", "scrollend", "seeked", "seeking",
        "select", "selectionchange", "selectstart", "stalled", "submit",
        "suspend", "timeupdate", "toggle", "touchcancel", "touchend",
        "touchmove", "touchstart", "transitioncancel", "transitionend",
        "transitionrun", "transitionstart", "volumechange", "waiting",
        "wheel",
    };
}

/// <summary>JS wrapper for a host <see cref="Event"/>. Identity is the spec
/// [[HostEvent]] slot — accessor methods read directly off it.</summary>
internal class JsEventWrapper : JsObject
{
    public Event HostEvent { get; }
    /// <summary>The raw JS init dictionary the event was constructed with, kept so
    /// subtype accessors (view/buttons/deltaX/isComposing/location/data, …) that
    /// have no host-class slot can read their value back. Undefined when none was
    /// supplied (e.g. legacy createEvent or host-synthesized events).</summary>
    public JsValue InitDict { get; }
    public JsEventWrapper(JsObject proto, Event hostEvent) : base(proto)
    {
        HostEvent = hostEvent;
        InitDict = JsValue.Undefined;
    }
    public JsEventWrapper(JsObject proto, Event hostEvent, JsValue initDict) : base(proto)
    {
        HostEvent = hostEvent;
        InitDict = initDict;
    }
}

/// <summary>Wrapper for <c>CustomEvent</c> that carries a JS-value
/// <c>detail</c> slot alongside the host event object.</summary>
internal sealed class JsCustomEventWrapper : JsEventWrapper
{
    // Settable so legacy initCustomEvent(type, …, detail) can (re)assign it.
    public JsValue Detail { get; set; }
    public JsCustomEventWrapper(JsObject proto, CustomEvent hostEvent, JsValue detail)
        : base(proto, hostEvent) { Detail = detail; }
}

/// <summary>Per-target listener bookkeeping. Listeners are mapped from the
/// JS listener object to its host-side delegate so removeEventListener can
/// find the same wrapper to detach.</summary>
internal sealed class ListenerRegistry
{
    private readonly Dictionary<(string Type, bool Capture), Dictionary<JsObject, EventListener>> _byKey = new();

    public Dictionary<JsObject, EventListener> GetOrCreate((string Type, bool Capture) key)
    {
        if (!_byKey.TryGetValue(key, out var map))
        {
            map = new Dictionary<JsObject, EventListener>(ReferenceEqualityComparer.Instance);
            _byKey[key] = map;
        }
        return map;
    }

    public Dictionary<JsObject, EventListener>? TryGet((string Type, bool Capture) key)
        => _byKey.TryGetValue(key, out var map) ? map : null;
}

/// <summary>A bare host <see cref="EventTarget"/> used when JS constructs
/// <c>new EventTarget()</c> directly (i.e. not attached to a DOM node).</summary>
internal sealed class InMemoryEventTarget : EventTarget { }

/// <summary>Boxing wrapper so <see cref="JsValue"/> (a struct) can be stored
/// in a <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/>
/// (which requires reference-type values).</summary>
internal sealed class JsValueBox
{
    public JsValue Value { get; }
    public JsValueBox(JsValue v) { Value = v; }
}

/// <summary>A mutable single-value slot for a <see cref="JsValue"/>, used as the
/// per-realm legacy <c>window.event</c> holder.</summary>
internal sealed class MutableJsValueSlot
{
    public JsValue Value { get; set; } = JsValue.Undefined;
}

internal static partial class EventTargetBindingLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "window.onerror handler threw (must not escape the dispatch loop)")]
    public static partial void OnerrorHandlerThrew(ILogger logger, Exception ex);
}
