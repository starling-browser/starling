using System.Runtime.CompilerServices;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Microsoft.Extensions.Logging;
using Starling.Dom;
using Starling.Dom.Events;
using DomEvent = Starling.Dom.Events.Event;
using DomCustomEvent = Starling.Dom.Events.CustomEvent;

// EventTarget tests drive the internal Install + helpers directly (the public
// entry point JintBindings.InstallAll pulls in every other binding family, which the
// EventTarget tests do not need).
[assembly: InternalsVisibleTo("Starling.Bindings.Jint.Tests")]

namespace Starling.Bindings.Jint;

// EventTarget + Event dispatch on the Jint backend.
//
// Mirrors Starling.Bindings/EventTargetBinding.cs but over the Jint engine and
// the Jint wrapper registry. Reuses the real Starling DOM event model
// (EventTarget.AddEventListener / DispatchEvent and the spec dispatcher in
// EventDispatcher) for capturing/bubbling/once/stop semantics — we only bridge
// JS callbacks to host EventListener delegates and wrap native Events for
// delivery. Runs before NodeBindings in InstallAll so Node.prototype can chain
// to EventTarget.prototype.
internal static class EventTargetBinding
{
    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.Wrappers.EventTargetPrototype is not null)
        {
            return; // idempotent
        }

        var engine = ctx.Engine;
        var state = EventState.For(ctx);

        // ----- EventTarget.prototype + constructor ----------------------------
        var etProto = new JsObject(engine);
        ctx.Wrappers.EventTargetPrototype = etProto;

        JintInterop.DefineMethod(engine, etProto, "addEventListener",
            (thisV, args) => AddListener(ctx, state, thisV, args), length: 2);
        JintInterop.DefineMethod(engine, etProto, "removeEventListener",
            (thisV, args) => RemoveListener(ctx, state, thisV, args), length: 2);
        JintInterop.DefineMethod(engine, etProto, "dispatchEvent",
            (thisV, args) => DispatchEvent(ctx, state, thisV, args), length: 1);

        var etCtor = new NativeConstructor(engine, "EventTarget", 0, (args, _) =>
        {
            // `new EventTarget()` mints a bare host target with no DOM identity.
            var host = new InMemoryEventTarget();
            var wrapper = ctx.Wrappers.GetOrCreate(host); // prototype = EventTargetPrototype
            return wrapper;
        });
        WireConstructor(etCtor, etProto, length: 0);
        DefineGlobal(engine, "EventTarget", etCtor);

        // ----- Event.prototype + constructor ----------------------------------
        var evProto = new JsObject(engine); // prototype defaults to %Object.prototype%
        ctx.Wrappers.EventPrototype = evProto;
        InstallEventAccessors(ctx, state, evProto);

        DefineConstant(engine, evProto, "NONE", (int)EventPhase.None);
        DefineConstant(engine, evProto, "CAPTURING_PHASE", (int)EventPhase.CapturingPhase);
        DefineConstant(engine, evProto, "AT_TARGET", (int)EventPhase.AtTarget);
        DefineConstant(engine, evProto, "BUBBLING_PHASE", (int)EventPhase.BubblingPhase);

        var evCtor = new NativeConstructor(engine, "Event", 1, (args, _) =>
        {
            var (type, init) = ParseEventArgs(engine, args, "Event");
            var host = new DomEvent(type, init);
            return state.WrapNativeEvent(host, evProto);
        });
        WireConstructor(evCtor, evProto, length: 1);
        // Mirror the constructor's phase constants per Web-IDL.
        DefineConstant(engine, evCtor, "NONE", (int)EventPhase.None);
        DefineConstant(engine, evCtor, "CAPTURING_PHASE", (int)EventPhase.CapturingPhase);
        DefineConstant(engine, evCtor, "AT_TARGET", (int)EventPhase.AtTarget);
        DefineConstant(engine, evCtor, "BUBBLING_PHASE", (int)EventPhase.BubblingPhase);
        DefineGlobal(engine, "Event", evCtor);

        // ----- CustomEvent.prototype + constructor (inherits Event) -----------
        var customProto = new JsObject(engine) { Prototype = evProto };
        JintInterop.DefineAccessor(engine, customProto, "detail", (thisV, _) =>
            thisV is ObjectInstance oi && state.TryGetDetail(oi, out var d) ? d : JsValue.Null);
        // Legacy CustomEvent.initCustomEvent (DOM §2.3) — used with createEvent.
        JintInterop.DefineMethod(engine, customProto, "initCustomEvent", (thisV, a) =>
        {
            if (a.Length == 0)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError,
                    "Failed to execute 'initCustomEvent' on 'CustomEvent': 1 argument required, but only 0 present.");
            }

            if (TryGetNative(state, thisV, out var e))
            {
                e.InitEvent(a[0].ToString(),
                    a.Length > 1 && TypeConverter.ToBoolean(a[1]),
                    a.Length > 2 && TypeConverter.ToBoolean(a[2]));
            }

            if (thisV is ObjectInstance oi && a.Length > 3)
            {
                state.SetDetail(oi, a[3]);
            }

            return JsValue.Undefined;
        }, length: 4);

        var customCtor = new NativeConstructor(engine, "CustomEvent", 1, (args, _) =>
        {
            var (type, init) = ParseEventArgs(engine, args, "CustomEvent");
            var detail = JsValue.Null;
            if (args.Length > 1 && args[1] is ObjectInstance initObj)
            {
                var d = initObj.Get("detail");
                if (!d.IsUndefined())
                {
                    detail = d;
                }
            }
            var host = new DomCustomEvent(type, init);
            var wrapper = state.WrapNativeEvent(host, customProto);
            state.SetDetail(wrapper, detail);
            return wrapper;
        });
        WireConstructor(customCtor, customProto, length: 1);
        // Web-IDL interface inheritance: CustomEvent extends Event, so the
        // CustomEvent constructor's [[Prototype]] is the Event constructor (this
        // is what makes `CustomEvent.AT_TARGET` etc. resolve through the chain).
        customCtor.Prototype = evCtor;
        DefineGlobal(engine, "CustomEvent", customCtor);

        // ----- Event subclass interfaces (UIEvent, MouseEvent, …) -------------
        // Real pages and frameworks both construct these (`new MouseEvent(...)`)
        // and branch on them (`e instanceof MouseEvent`). Each is a constructible
        // Event subclass whose prototype chains off Event.prototype (so phase
        // constants + base accessors resolve) and whose constructor chains off
        // Event (Web-IDL inheritance). The constructor builds the real host
        // subtype and reads the init dictionary into it, and the init dict is kept
        // on the wrapper so members with no host slot (buttons, deltaX, …) read
        // back. Mirrors Starling.Bindings/EventTargetBinding.cs.
        InstallEventSubtypes(ctx, state, engine, evProto, evCtor);

        // Legacy window.event (HTML) — the event currently being dispatched.
        JintInterop.DefineAccessor(engine, engine.Global, "event", (_, _) => state.CurrentEvent);
    }

    // ---- Event subclass prototypes + constructors ----------------------------

    private static void InstallEventSubtypes(
        JintBackendContext ctx, EventState state, global::Jint.Engine engine,
        ObjectInstance evProto, NativeConstructor evCtor)
    {
        // Build a constructible subtype: prototype chains off `parentProto`; the
        // constructor builds a host event from (type, init) and chains off Event.
        ObjectInstance Sub(ObjectInstance parentProto, string name, Func<string, JsValue, DomEvent> build)
        {
            var subProto = new JsObject(engine) { Prototype = parentProto };
            var subCtor = new NativeConstructor(engine, name, 1, (args, _) =>
            {
                if (args.Length == 0 || args[0].IsUndefined())
                {
                    throw new JavaScriptException(engine.Intrinsics.TypeError,
                        $"Failed to construct '{name}': 1 argument required, but only 0 present.");
                }

                var type = args[0].ToString();
                var init = args.Length > 1 ? args[1] : JsValue.Undefined;
                // UIEvents §3.5 — `view` must be a Window or null; a primitive is a TypeError.
                if (init is ObjectInstance io)
                {
                    var view = io.Get("view");
                    if (!view.IsUndefined() && !view.IsNull() && view is not ObjectInstance)
                    {
                        throw new JavaScriptException(engine.Intrinsics.TypeError,
                            $"Failed to construct '{name}': member view is not a Window.");
                    }
                }
                return state.WrapNativeEvent(build(type, init), subProto, init);
            });
            WireConstructor(subCtor, subProto, length: 1);
            subCtor.Prototype = evCtor;
            DefineGlobal(engine, name, subCtor);
            return subProto;
        }

        EventTarget? Related(JsValue init) => init is ObjectInstance io
            ? ctx.Wrappers.Unwrap(io.Get("relatedTarget")) as EventTarget : null;

        // ---- UIEvent
        var uiProto = Sub(evProto, "UIEvent",
            (type, init) => new UiEvent(type, ReadInit(init)) { Detail = (int)NumOf(init, "detail") });
        state.UiEventProto = uiProto;
        JintInterop.DefineAccessor(engine, uiProto, "detail",
            (t, _) => Native(state, t) is UiEvent u ? JintInterop.Num(u.Detail) : JintInterop.Num(0));
        JintInterop.DefineAccessor(engine, uiProto, "view",
            (t, _) => InitMember(state, t, "view", JsValue.Null));

        // ---- MouseEvent (extends UIEvent)
        var mouseProto = Sub(uiProto, "MouseEvent", (type, init) => new MouseEvent(type, ReadInit(init))
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
            RelatedTarget = Related(init),
        });
        state.MouseEventProto = mouseProto;
        MouseAccessor(engine, state, mouseProto, "clientX", m => m.ClientX);
        MouseAccessor(engine, state, mouseProto, "clientY", m => m.ClientY);
        MouseAccessor(engine, state, mouseProto, "x", m => m.ClientX);
        MouseAccessor(engine, state, mouseProto, "y", m => m.ClientY);
        MouseAccessor(engine, state, mouseProto, "pageX", m => m.ClientX);
        MouseAccessor(engine, state, mouseProto, "pageY", m => m.ClientY);
        MouseAccessor(engine, state, mouseProto, "screenX", m => m.ScreenX);
        MouseAccessor(engine, state, mouseProto, "screenY", m => m.ScreenY);
        MouseAccessor(engine, state, mouseProto, "button", m => m.Button);
        JintInterop.DefineAccessor(engine, mouseProto, "buttons", (t, _) => InitNum(state, t, "buttons"));
        MouseBoolAccessor(engine, state, mouseProto, "ctrlKey", m => m.CtrlKey);
        MouseBoolAccessor(engine, state, mouseProto, "shiftKey", m => m.ShiftKey);
        MouseBoolAccessor(engine, state, mouseProto, "altKey", m => m.AltKey);
        MouseBoolAccessor(engine, state, mouseProto, "metaKey", m => m.MetaKey);
        JintInterop.DefineAccessor(engine, mouseProto, "relatedTarget",
            (t, _) => Native(state, t) is MouseEvent { RelatedTarget: { } rt } ? ctx.Wrappers.Wrap(rt) : JsValue.Null);

        // ---- KeyboardEvent (extends UIEvent)
        var kbdProto = Sub(uiProto, "KeyboardEvent", (type, init) => new KeyboardEvent(type, ReadInit(init))
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
        state.KeyboardEventProto = kbdProto;
        JintInterop.DefineAccessor(engine, kbdProto, "key", (t, _) => Native(state, t) is KeyboardEvent k ? JintInterop.Str(k.Key) : JintInterop.Str(""));
        JintInterop.DefineAccessor(engine, kbdProto, "code", (t, _) => Native(state, t) is KeyboardEvent k ? JintInterop.Str(k.Code) : JintInterop.Str(""));
        JintInterop.DefineAccessor(engine, kbdProto, "repeat", (t, _) => JintInterop.Bool(Native(state, t) is KeyboardEvent { Repeat: true }));
        JintInterop.DefineAccessor(engine, kbdProto, "location", (t, _) => InitNum(state, t, "location"));
        JintInterop.DefineAccessor(engine, kbdProto, "isComposing", (t, _) => InitBool(state, t, "isComposing"));
        JintInterop.DefineAccessor(engine, kbdProto, "charCode", (t, _) => InitNum(state, t, "charCode"));
        JintInterop.DefineAccessor(engine, kbdProto, "keyCode", (t, _) => InitNum(state, t, "keyCode"));
        JintInterop.DefineAccessor(engine, kbdProto, "which", (t, _) => InitNum(state, t, "which"));
        KbdBoolAccessor(engine, state, kbdProto, "ctrlKey", k => k.CtrlKey);
        KbdBoolAccessor(engine, state, kbdProto, "shiftKey", k => k.ShiftKey);
        KbdBoolAccessor(engine, state, kbdProto, "altKey", k => k.AltKey);
        KbdBoolAccessor(engine, state, kbdProto, "metaKey", k => k.MetaKey);

        // ---- FocusEvent (extends UIEvent)
        var focusProto = Sub(uiProto, "FocusEvent", (type, init) => new FocusEvent(type, ReadInit(init))
        {
            Detail = (int)NumOf(init, "detail"),
            RelatedTarget = Related(init),
        });
        state.FocusEventProto = focusProto;
        JintInterop.DefineAccessor(engine, focusProto, "relatedTarget",
            (t, _) => Native(state, t) is FocusEvent { RelatedTarget: { } rt } ? ctx.Wrappers.Wrap(rt) : JsValue.Null);

        // ---- WheelEvent (extends MouseEvent) — host MouseEvent + init deltas
        var wheelProto = Sub(mouseProto, "WheelEvent", (type, init) => new MouseEvent(type, ReadInit(init))
        {
            ClientX = NumOf(init, "clientX"),
            ClientY = NumOf(init, "clientY"),
            ScreenX = NumOf(init, "screenX"),
            ScreenY = NumOf(init, "screenY"),
            Button = (short)NumOf(init, "button"),
            Detail = (int)NumOf(init, "detail"),
            RelatedTarget = Related(init),
        });
        JintInterop.DefineAccessor(engine, wheelProto, "deltaX", (t, _) => InitNum(state, t, "deltaX"));
        JintInterop.DefineAccessor(engine, wheelProto, "deltaY", (t, _) => InitNum(state, t, "deltaY"));
        JintInterop.DefineAccessor(engine, wheelProto, "deltaZ", (t, _) => InitNum(state, t, "deltaZ"));
        JintInterop.DefineAccessor(engine, wheelProto, "deltaMode", (t, _) => InitNum(state, t, "deltaMode"));

        // ---- CompositionEvent (extends UIEvent) — data payload
        var compProto = Sub(uiProto, "CompositionEvent",
            (type, init) => new UiEvent(type, ReadInit(init)) { Detail = (int)NumOf(init, "detail") });
        JintInterop.DefineAccessor(engine, compProto, "data",
            (t, _) => JintInterop.Str(TypeConverter.ToString(InitMember(state, t, "data", JintInterop.Str("")))));

        // ---- InputEvent (extends UIEvent)
        Sub(uiProto, "InputEvent", (type, init) => new UiEvent(type, ReadInit(init)));

        // ---- Pointer/Drag extend MouseEvent; Touch extends UIEvent — host subtypes.
        Sub(mouseProto, "PointerEvent", (type, init) => new MouseEvent(type, ReadInit(init)));
        Sub(mouseProto, "DragEvent", (type, init) => new MouseEvent(type, ReadInit(init)));
        Sub(uiProto, "TouchEvent", (type, init) => new UiEvent(type, ReadInit(init)));

        // ---- Remaining base-Event subclasses — constructible, chain off Event.
        foreach (var name in new[]
        {
            "ClipboardEvent", "AnimationEvent", "TransitionEvent", "PopStateEvent",
            "HashChangeEvent", "MessageEvent", "ProgressEvent", "ErrorEvent",
        })
        {
            Sub(evProto, name, (type, init) => new DomEvent(type, ReadInit(init)));
        }
    }

    // ---- subtype accessor + init-dict helpers --------------------------------

    private static DomEvent? Native(EventState state, JsValue thisV)
        => thisV is ObjectInstance oi && state.TryGetNativeEvent(oi, out var e) ? e : null;

    private static JsValue InitMember(EventState state, JsValue thisV, string key, JsValue fallback)
    {
        if (thisV is ObjectInstance oi && state.TryGetInitDict(oi, out var init) && init is ObjectInstance io)
        {
            var v = io.Get(key);
            if (!v.IsUndefined())
            {
                return v;
            }
        }
        return fallback;
    }

    private static JsValue InitNum(EventState state, JsValue thisV, string key)
        => JintInterop.Num(TypeConverter.ToNumber(InitMember(state, thisV, key, JintInterop.Num(0))));
    private static JsValue InitBool(EventState state, JsValue thisV, string key)
        => JintInterop.Bool(TypeConverter.ToBoolean(InitMember(state, thisV, key, JsBoolean.False)));

    private static void MouseAccessor(global::Jint.Engine engine, EventState state, ObjectInstance proto, string name, Func<MouseEvent, double> read)
        => JintInterop.DefineAccessor(engine, proto, name, (t, _) => Native(state, t) is MouseEvent m ? JintInterop.Num(read(m)) : JintInterop.Num(0));
    private static void MouseBoolAccessor(global::Jint.Engine engine, EventState state, ObjectInstance proto, string name, Func<MouseEvent, bool> read)
        => JintInterop.DefineAccessor(engine, proto, name, (t, _) => JintInterop.Bool(Native(state, t) is MouseEvent m && read(m)));
    private static void KbdBoolAccessor(global::Jint.Engine engine, EventState state, ObjectInstance proto, string name, Func<KeyboardEvent, bool> read)
        => JintInterop.DefineAccessor(engine, proto, name, (t, _) => JintInterop.Bool(Native(state, t) is KeyboardEvent k && read(k)));

    private static EventInit ReadInit(JsValue v) => v is ObjectInstance o
        ? new EventInit(
            Bubbles: TypeConverter.ToBoolean(o.Get("bubbles")),
            Cancelable: TypeConverter.ToBoolean(o.Get("cancelable")),
            Composed: TypeConverter.ToBoolean(o.Get("composed")))
        : default;
    private static double NumOf(JsValue v, string k) => v is ObjectInstance o && !o.Get(k).IsUndefined() ? TypeConverter.ToNumber(o.Get(k)) : 0;
    private static bool BoolOf(JsValue v, string k) => v is ObjectInstance o && TypeConverter.ToBoolean(o.Get(k));
    private static string StrOf(JsValue v, string k) => v is ObjectInstance o && o.Get(k).IsString() ? o.Get(k).AsString() : "";

    // ---- addEventListener / removeEventListener / dispatchEvent --------------

    private static JsValue AddListener(JintBackendContext ctx, EventState state, JsValue thisV, JsValue[] args)
    {
        var host = ResolveHost(ctx, thisV);
        if (host is null)
        {
            return JsValue.Undefined;
        }

        if (args.Length < 1)
        {
            return JsValue.Undefined;
        }

        var type = TypeArg(args);

        // listener may be null/undefined (spec: no-op), a callable, or an object
        // with a `handleEvent` method (the EventListener callback-interface form).
        var listenerArg = args.Length > 1 ? args[1] : JsValue.Undefined;
        if (listenerArg.IsNull() || listenerArg.IsUndefined())
        {
            return JsValue.Undefined;
        }

        if (listenerArg is not ObjectInstance listenerObj)
        {
            return JsValue.Undefined;
        }

        if (!IsValidListener(listenerObj))
        {
            return JsValue.Undefined;
        }

        var (capture, once, passive) = ParseListenerOptions(args.Length > 2 ? args[2] : JsValue.Undefined);

        var registry = state.RegistryFor(host);
        var key = new ListenerKey(type, capture);
        if (registry.Contains(key, listenerObj))
        {
            return JsValue.Undefined; // dedup per spec
        }

        EventListener wrapper = null!;
        wrapper = ev =>
        {
            // `once` removal happens host-side, but mirror it in our identity
            // map so re-adding the same listener registers cleanly.
            if (once)
            {
                registry.Remove(key, listenerObj);
            }

            InvokeJsListener(ctx, state, listenerObj, ev);
        };
        registry.Add(key, listenerObj, wrapper);
        host.AddEventListener(type, wrapper, new AddEventListenerOptions(capture, once, passive));
        return JsValue.Undefined;
    }

    private static JsValue RemoveListener(JintBackendContext ctx, EventState state, JsValue thisV, JsValue[] args)
    {
        var host = ResolveHost(ctx, thisV);
        if (host is null)
        {
            return JsValue.Undefined;
        }

        if (args.Length < 2 || args[1] is not ObjectInstance listenerObj)
        {
            return JsValue.Undefined;
        }

        var type = TypeArg(args);
        var (capture, _, _) = ParseListenerOptions(args.Length > 2 ? args[2] : JsValue.Undefined);

        var registry = state.TryGetRegistry(host);
        if (registry is null)
        {
            return JsValue.Undefined;
        }

        var key = new ListenerKey(type, capture);
        if (!registry.TryTake(key, listenerObj, out var wrapper))
        {
            return JsValue.Undefined;
        }

        host.RemoveEventListener(type, wrapper, new RemoveEventListenerOptions(capture));
        return JsValue.Undefined;
    }

    private static JsValue DispatchEvent(JintBackendContext ctx, EventState state, JsValue thisV, JsValue[] args)
    {
        var host = ResolveHost(ctx, thisV);
        if (host is null)
        {
            return JintInterop.Bool(false);
        }

        if (args.Length == 0 || args[0] is not ObjectInstance evObj || !state.TryGetNativeEvent(evObj, out var native))
        {
            throw new JavaScriptException(ctx.Engine.Intrinsics.TypeError,
                "Failed to execute 'dispatchEvent': parameter 1 is not of type 'Event'.");
        }
        // DOM §2.10 — an event that is mid-dispatch or uninitialized must not be dispatched.
        if (native.IsBeingDispatched)
        {
            throw DomExceptionBinding.Throw(ctx, "InvalidStateError", "The event is already being dispatched.");
        }

        if (!native.Initialized)
        {
            throw DomExceptionBinding.Throw(ctx, "InvalidStateError", "The event has not been initialized.");
        }

        var notCanceled = host.DispatchEvent(native);
        return JintInterop.Bool(notCanceled);
    }

    // ---- Event prototype accessors ------------------------------------------

    private static void InstallEventAccessors(JintBackendContext ctx, EventState state, ObjectInstance evProto)
    {
        var engine = ctx.Engine;

        void Accessor(string name, Func<DomEvent, JsValue> read, JsValue fallback)
            => JintInterop.DefineAccessor(engine, evProto, name,
                (thisV, _) => TryGetNative(state, thisV, out var e) ? read(e) : fallback);

        Accessor("type", e => JintInterop.Str(e.Type), JintInterop.Str(""));
        Accessor("target", e => ctx.Wrappers.Wrap(e.Target), JsValue.Null);
        Accessor("srcElement", e => ctx.Wrappers.Wrap(e.Target), JsValue.Null);
        Accessor("currentTarget", e => ctx.Wrappers.Wrap(e.CurrentTarget), JsValue.Null);
        Accessor("eventPhase", e => JintInterop.Num((int)e.EventPhase), JintInterop.Num(0));
        Accessor("bubbles", e => JintInterop.Bool(e.Bubbles), JsValue.Undefined);
        Accessor("cancelable", e => JintInterop.Bool(e.Cancelable), JsValue.Undefined);
        Accessor("composed", e => JintInterop.Bool(e.Composed), JsValue.Undefined);
        Accessor("defaultPrevented", e => JintInterop.Bool(e.DefaultPrevented), JintInterop.Bool(false));
        Accessor("timeStamp", e => JintInterop.Num(e.TimeStamp), JintInterop.Num(0));
        Accessor("isTrusted", e => JintInterop.Bool(e.IsTrusted), JintInterop.Bool(false));
        Accessor("state",
            e => e is PopStateEvent { State: JsValue state } ? state : JsValue.Undefined,
            JsValue.Undefined);

        JintInterop.DefineMethod(engine, evProto, "preventDefault", (thisV, _) =>
        {
            if (TryGetNative(state, thisV, out var e))
            {
                e.PreventDefault();
            }

            return JsValue.Undefined;
        }, length: 0);
        JintInterop.DefineMethod(engine, evProto, "stopPropagation", (thisV, _) =>
        {
            if (TryGetNative(state, thisV, out var e))
            {
                e.StopPropagation();
            }

            return JsValue.Undefined;
        }, length: 0);
        JintInterop.DefineMethod(engine, evProto, "stopImmediatePropagation", (thisV, _) =>
        {
            if (TryGetNative(state, thisV, out var e))
            {
                e.StopImmediatePropagation();
            }

            return JsValue.Undefined;
        }, length: 0);
        JintInterop.DefineMethod(engine, evProto, "composedPath", (thisV, _) =>
        {
            if (!TryGetNative(state, thisV, out var e))
            {
                return new JsArray(engine, Array.Empty<JsValue>());
            }

            return ComposedPath(ctx, e);
        }, length: 0);

        // Legacy Event.initEvent (DOM §2.9) — used with document.createEvent. The
        // `type` argument is mandatory per WebIDL.
        JintInterop.DefineMethod(engine, evProto, "initEvent", (thisV, a) =>
        {
            if (a.Length == 0)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError,
                    "Failed to execute 'initEvent' on 'Event': 1 argument required, but only 0 present.");
            }

            if (TryGetNative(state, thisV, out var e))
            {
                e.InitEvent(a[0].ToString(),
                    a.Length > 1 && TypeConverter.ToBoolean(a[1]),
                    a.Length > 2 && TypeConverter.ToBoolean(a[2]));
            }

            return JsValue.Undefined;
        }, length: 3);

        // Legacy cancelBubble (alias of stopPropagation when set truthy) and
        // returnValue (false ⇒ preventDefault).
        JintInterop.DefineAccessor(engine, evProto, "cancelBubble",
            (thisV, _) => TryGetNative(state, thisV, out var e) ? JintInterop.Bool(e.PropagationStopped) : JsBoolean.False,
            (thisV, a) => { if (a.Length > 0 && TypeConverter.ToBoolean(a[0]) && TryGetNative(state, thisV, out var e)) { e.StopPropagation(); } return JsValue.Undefined; });
        JintInterop.DefineAccessor(engine, evProto, "returnValue",
            (thisV, _) => TryGetNative(state, thisV, out var e) ? JintInterop.Bool(!e.DefaultPrevented) : JsBoolean.True,
            (thisV, a) => { if (a.Length > 0 && !TypeConverter.ToBoolean(a[0]) && TryGetNative(state, thisV, out var e)) { e.PreventDefault(); } return JsValue.Undefined; });
    }

    /// <summary>Legacy <c>document.createEvent(interface)</c> (DOM §2.9). Returns an
    /// uninitialized event of the requested legacy interface, wrapped against its
    /// subtype prototype so it is dispatchable and <c>instanceof</c> resolves. The
    /// caller must call <c>initEvent</c>/<c>initCustomEvent</c> before dispatch.
    /// Throws <c>NotSupportedError</c> for an unrecognized interface.</summary>
    public static JsValue CreateLegacyEvent(JintBackendContext ctx, string interfaceName)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var state = EventState.For(ctx);
        var evProto = ctx.Wrappers.EventPrototype!;
        var key = interfaceName.ToLowerInvariant();
        // A createEvent() event is uninitialized until initEvent/initCustomEvent.
        DomEvent Mk(DomEvent ev) { ev.MarkAsUninitialized(); return ev; }
        return key switch
        {
            "event" or "events" or "htmlevents" => state.WrapNativeEvent(Mk(new DomEvent("", default)), evProto),
            "customevent" => state.WrapNativeEvent(Mk(new DomCustomEvent("", default)), evProto),
            "uievent" or "uievents" => state.WrapNativeEvent(Mk(new UiEvent("", default)), state.UiEventProto ?? evProto),
            "mouseevent" or "mouseevents" => state.WrapNativeEvent(Mk(new MouseEvent("", default)), state.MouseEventProto ?? evProto),
            "keyboardevent" or "keyevents" => state.WrapNativeEvent(Mk(new KeyboardEvent("", default)), state.KeyboardEventProto ?? evProto),
            _ => throw DomExceptionBinding.Throw(ctx, "NotSupportedError",
                $"Failed to execute 'createEvent' on 'Document': The provided event type ('{interfaceName}') is invalid."),
        };
    }

    /// <summary>composedPath() — DOM §2.10. The native dispatcher does not retain
    /// the event path, so we recompute it from the current/target node's ancestor
    /// chain. While dispatching this is exact (target up to root); after dispatch
    /// (when currentTarget is null) it returns the empty array, matching browsers.</summary>
    private static JsArray ComposedPath(JintBackendContext ctx, DomEvent e)
    {
        var anchor = e.CurrentTarget ?? (e.EventPhase != EventPhase.None ? e.Target : null);
        if (anchor is null)
        {
            return new JsArray(ctx.Engine, Array.Empty<JsValue>());
        }

        var path = new List<JsValue> { ctx.Wrappers.Wrap(anchor) };
        if (anchor is Node node)
        {
            for (var p = node.ParentNode; p is not null; p = p.ParentNode)
            {
                path.Add(ctx.Wrappers.Wrap(p));
            }
        }

        return new JsArray(ctx.Engine, path.ToArray());
    }

    // ---- listener invocation -------------------------------------------------

    private static void InvokeJsListener(JintBackendContext ctx, EventState state, ObjectInstance listener, DomEvent ev)
    {
        var jsLog = ctx.LoggerFactory.CreateLogger("Starling.engine.js");
        try
        {
            // Wrap against the host event's own subtype prototype, so a listener
            // for a host-fired `click`/`keydown` sees `instanceof MouseEvent` and
            // the subtype accessors (clientX, key, …), not a bare Event.
            var jsEvent = state.WrapNativeEvent(ev, state.GetEventPrototypeForHost(ev));
            JsValue thisVal = ev.CurrentTarget is null ? JsValue.Undefined : ctx.Wrappers.Wrap(ev.CurrentTarget);

            JsValue callback = listener;
            // EventListener callback-interface: an object with handleEvent is
            // invoked as listener.handleEvent(event) with `this` = the listener.
            if (!listener.IsCallable())
            {
                var handle = listener.Get("handleEvent");
                if (!handle.IsCallable())
                {
                    return;
                }

                callback = handle;
                thisVal = listener;
            }

            var previousEvent = state.CurrentEvent;
            state.CurrentEvent = jsEvent;
            try { callback.Call(thisVal, new[] { jsEvent }); }
            finally { state.CurrentEvent = previousEvent; }
        }
        catch (JavaScriptException ex)
        {
            // HTML §"report the exception": run window.onerror first; a truthy
            // return cancels the default (logged) report.
            if (!ReportException(ctx, ex.Error, JintInterop.DescribeError(ex.Error, ex.Message)))
            {
                EventTargetBindingLog.UncaughtInEventListener(jsLog,
                    JintInterop.DescribeError(ex.Error, ex.Message));
            }
        }
        catch (Exception ex)
        {
            EventTargetBindingLog.UncaughtInEventListener(jsLog, ex.Message);
        }
    }

    /// <summary>HTML §"report the exception" — invoke the legacy <c>window.onerror</c>
    /// handler (message, source, lineno, colno, error). Returns true when the
    /// handler ran and returned a truthy value (cancelling the default report).
    /// An error thrown by the handler itself is swallowed (no recursion).</summary>
    public static bool ReportException(JintBackendContext ctx, JsValue error, string message)
    {
        var onerror = ctx.Engine.Global.Get("onerror");
        if (!onerror.IsCallable())
        {
            return false;
        }

        try
        {
            var result = onerror.Call(ctx.Engine.Global, new[]
            {
                JintInterop.Str(message),
                JintInterop.Str(ctx.BaseUrl.ToString()),
                JintInterop.Num(0),
                JintInterop.Num(0),
                error,
            });
            return TypeConverter.ToBoolean(result);
        }
        catch (JavaScriptException) { return false; }
    }

    // ---- helpers -------------------------------------------------------------

    private static EventTarget? ResolveHost(JintBackendContext ctx, JsValue thisV)
        => ctx.Wrappers.Unwrap(thisV) as EventTarget;

    private static string TypeArg(JsValue[] args)
        => args.Length == 0 || args[0].IsUndefined() ? "undefined" : args[0].ToString();

    private static bool IsValidListener(ObjectInstance listener)
        => listener.IsCallable() || listener.Get("handleEvent").IsCallable();

    private static bool TryGetNative(EventState state, JsValue thisV, out DomEvent ev)
    {
        if (thisV is ObjectInstance oi && state.TryGetNativeEvent(oi, out ev!))
        {
            return true;
        }

        ev = null!;
        return false;
    }

    private static (string Type, EventInit Init) ParseEventArgs(global::Jint.Engine engine, JsValue[] args, string ctorName)
    {
        if (args.Length == 0 || args[0].IsUndefined())
        {
            throw new JavaScriptException(engine.Intrinsics.TypeError,
                $"Failed to construct '{ctorName}': 1 argument required, but only 0 present.");
        }

        var type = args[0].ToString();
        var init = default(EventInit);
        if (args.Length > 1 && args[1] is ObjectInstance o)
        {
            init = new EventInit(
                Bubbles: TypeConverter.ToBoolean(o.Get("bubbles")),
                Cancelable: TypeConverter.ToBoolean(o.Get("cancelable")),
                Composed: TypeConverter.ToBoolean(o.Get("composed")));
        }

        return (type, init);
    }

    private static (bool Capture, bool Once, bool Passive) ParseListenerOptions(JsValue opt)
    {
        if (opt.IsUndefined() || opt.IsNull())
        {
            return (false, false, false);
        }

        if (opt.IsBoolean())
        {
            return (opt.AsBoolean(), false, false);
        }

        if (opt is ObjectInstance o)
        {
            return (
                TypeConverter.ToBoolean(o.Get("capture")),
                TypeConverter.ToBoolean(o.Get("once")),
                TypeConverter.ToBoolean(o.Get("passive")));
        }

        return (false, false, false);
    }

    /// <summary>Wire a Web-IDL interface object: ctor.prototype (non-writable,
    /// non-enumerable, non-configurable), proto.constructor (writable,
    /// non-enumerable, configurable), and ctor.length.</summary>
    private static void WireConstructor(NativeConstructor ctor, ObjectInstance proto, int length)
    {
        ctor.DefineOwnProperty("prototype",
            new PropertyDescriptor(proto, writable: false, enumerable: false, configurable: false));
        ctor.DefineOwnProperty("length",
            new PropertyDescriptor(JintInterop.Num(length), writable: false, enumerable: false, configurable: true));
        proto.FastSetProperty("constructor",
            new PropertyDescriptor(ctor, writable: true, enumerable: false, configurable: true));
    }

    private static void DefineGlobal(global::Jint.Engine engine, string name, JsValue value)
        => JintInterop.DefineDataProp(engine.Global, name, value,
            writable: true, enumerable: false, configurable: true);

    private static void DefineConstant(global::Jint.Engine engine, ObjectInstance target, string name, int value)
        => target.FastSetProperty(name,
            new PropertyDescriptor(JintInterop.Num(value), writable: false, enumerable: true, configurable: false));
}

/// <summary>A native, constructible Web-IDL interface object. Jint's
/// <c>ClrFunction</c> is callable but not a constructor, so EventTarget/Event/
/// CustomEvent need this thin <see cref="Constructor"/> subclass. Calling it
/// without <c>new</c> throws TypeError (Web-IDL "Illegal constructor").</summary>
internal sealed class NativeConstructor : Constructor
{
    private readonly Func<JsValue[], JsValue, ObjectInstance> _construct;

    public NativeConstructor(global::Jint.Engine engine, string name, int length,
        Func<JsValue[], JsValue, ObjectInstance> construct)
        : base(engine, name)
    {
        _construct = construct;
        _ = length; // length is defined explicitly by WireConstructor for Web-IDL flags.
    }

    public override ObjectInstance Construct(JsValue[] arguments, JsValue newTarget)
        => _construct(arguments, newTarget);
}

/// <summary>Per-session event bridge state: native↔JS event wrapper identity,
/// per-target listener registries, and CustomEvent detail boxes. One instance is
/// stashed on the engine via a side table keyed by the context.</summary>
internal sealed class EventState
{
    // One EventState per JintBackendContext. The context isn't engine-rooted in a
    // way we can hang state off, so key a static weak table on the context.
    private static readonly ConditionalWeakTable<JintBackendContext, EventState> Instances = new();

    public static EventState For(JintBackendContext ctx)
        => Instances.GetValue(ctx, static c => new EventState(c));

    private readonly JintBackendContext _ctx;

    // native Event ↔ JS wrapper (so the same JS object is seen across the whole
    // propagation, and JS-constructed events keep their detail).
    private readonly ConditionalWeakTable<DomEvent, ObjectInstance> _eventToWrapper = new();
    private readonly ConditionalWeakTable<ObjectInstance, DomEvent> _wrapperToEvent = new();
    // CustomEvent JS-value detail (JsValue is a struct → box for the table).
    private readonly ConditionalWeakTable<ObjectInstance, JsValueBox> _detail = new();
    // The raw JS init dictionary a subtype event was constructed with, so subtype
    // accessors with no host-class slot (buttons, deltaX, location, …) read back.
    private readonly ConditionalWeakTable<ObjectInstance, JsValueBox> _initDict = new();
    // per host EventTarget listener bookkeeping.
    private readonly ConditionalWeakTable<EventTarget, ListenerRegistry> _registries = new();

    private EventState(JintBackendContext ctx) => _ctx = ctx;

    /// <summary>Legacy <c>window.event</c> — the event whose listener is currently
    /// running, or undefined when none. Set/restored around each listener call.</summary>
    public JsValue CurrentEvent { get; set; } = JsValue.Undefined;

    // Subtype prototype slots, set during Install. GetEventPrototypeForHost reads
    // these so a host-fired MouseEvent/KeyboardEvent/… wraps against its own
    // prototype (not the bare Event prototype) — fixing `instanceof MouseEvent`
    // and `event.clientX` for real `click`/`keydown` listeners.
    public ObjectInstance? UiEventProto { get; set; }
    public ObjectInstance? MouseEventProto { get; set; }
    public ObjectInstance? KeyboardEventProto { get; set; }
    public ObjectInstance? FocusEventProto { get; set; }

    /// <summary>The most-derived installed prototype for a host event subtype,
    /// falling back up the chain to %EventPrototype%.</summary>
    public ObjectInstance GetEventPrototypeForHost(DomEvent ev) => ev switch
    {
        MouseEvent => MouseEventProto ?? UiEventProto ?? _ctx.Wrappers.EventPrototype!,
        KeyboardEvent => KeyboardEventProto ?? UiEventProto ?? _ctx.Wrappers.EventPrototype!,
        FocusEvent => FocusEventProto ?? UiEventProto ?? _ctx.Wrappers.EventPrototype!,
        UiEvent => UiEventProto ?? _ctx.Wrappers.EventPrototype!,
        _ => _ctx.Wrappers.EventPrototype!,
    };

    /// <summary>Get the existing JS wrapper for a native event, or mint one with
    /// the given prototype. For native events fired by the DOM (no JS wrapper yet)
    /// this builds the Event facade the listener sees.</summary>
    public ObjectInstance WrapNativeEvent(DomEvent native, ObjectInstance defaultProto)
        => WrapNativeEvent(native, defaultProto, JsValue.Undefined);

    /// <summary>As <see cref="WrapNativeEvent(DomEvent, ObjectInstance)"/>, but also
    /// records the JS init dictionary the event was constructed with so subtype
    /// accessors can read members that have no host-class slot.</summary>
    public ObjectInstance WrapNativeEvent(DomEvent native, ObjectInstance defaultProto, JsValue initDict)
    {
        if (_eventToWrapper.TryGetValue(native, out var existing))
        {
            return existing;
        }

        // A CustomEvent fired by the DOM (not via our JS constructor) should still
        // expose `detail`, so prefer the CustomEvent prototype for those.
        var proto = native is DomCustomEvent && defaultProto == _ctx.Wrappers.EventPrototype
            ? ResolveCustomEventProto() ?? defaultProto
            : defaultProto;

        var wrapper = new JsObject(_ctx.Engine) { Prototype = proto };
        _eventToWrapper.Add(native, wrapper);
        _wrapperToEvent.Add(wrapper, native);
        if (!initDict.IsUndefined())
        {
            _initDict.AddOrUpdate(wrapper, new JsValueBox(initDict));
        }

        // Surface the host CustomEvent.Detail when it carries a JsValue (e.g. an
        // event constructed by C# / a non-JS CustomEvent).
        if (native is DomCustomEvent ce && ce.Detail is JsValue hostDetail && !_detail.TryGetValue(wrapper, out _))
        {
            SetDetail(wrapper, hostDetail);
        }

        return wrapper;
    }

    public bool TryGetNativeEvent(ObjectInstance wrapper, out DomEvent native)
        => _wrapperToEvent.TryGetValue(wrapper, out native!);

    public bool TryGetInitDict(ObjectInstance wrapper, out JsValue initDict)
    {
        if (_initDict.TryGetValue(wrapper, out var box)) { initDict = box.Value; return true; }
        initDict = JsValue.Undefined;
        return false;
    }

    public void SetDetail(ObjectInstance wrapper, JsValue detail)
    {
        _detail.Remove(wrapper);
        _detail.Add(wrapper, new JsValueBox(detail));
    }

    public bool TryGetDetail(ObjectInstance wrapper, out JsValue detail)
    {
        if (_detail.TryGetValue(wrapper, out var box)) { detail = box.Value; return true; }
        detail = JsValue.Null;
        return false;
    }

    public ListenerRegistry RegistryFor(EventTarget host)
        => _registries.GetValue(host, static _ => new ListenerRegistry());

    public ListenerRegistry? TryGetRegistry(EventTarget host)
        => _registries.TryGetValue(host, out var r) ? r : null;

    private ObjectInstance? ResolveCustomEventProto()
    {
        var ctor = _ctx.Engine.Global.Get("CustomEvent");
        if (ctor is ObjectInstance c && c.Get("prototype") is ObjectInstance p)
        {
            return p;
        }

        return null;
    }
}

internal readonly record struct ListenerKey(string Type, bool Capture);

/// <summary>Per-target map of (type, capture) → (JS listener identity → host
/// delegate), so removeEventListener finds the same delegate addEventListener
/// registered. Keyed by reference identity of the JS listener object.</summary>
internal sealed class ListenerRegistry
{
    private readonly Dictionary<ListenerKey, Dictionary<ObjectInstance, EventListener>> _byKey = new();

    private Dictionary<ObjectInstance, EventListener> Bucket(ListenerKey key)
    {
        if (!_byKey.TryGetValue(key, out var map))
        {
            map = new Dictionary<ObjectInstance, EventListener>(ReferenceEqualityComparer.Instance);
            _byKey[key] = map;
        }
        return map;
    }

    public bool Contains(ListenerKey key, ObjectInstance listener)
        => _byKey.TryGetValue(key, out var map) && map.ContainsKey(listener);

    public void Add(ListenerKey key, ObjectInstance listener, EventListener wrapper)
        => Bucket(key)[listener] = wrapper;

    public void Remove(ListenerKey key, ObjectInstance listener)
    {
        if (_byKey.TryGetValue(key, out var map))
        {
            map.Remove(listener);
        }
    }

    public bool TryTake(ListenerKey key, ObjectInstance listener, out EventListener wrapper)
    {
        wrapper = null!;
        if (!_byKey.TryGetValue(key, out var map))
        {
            return false;
        }

        if (!map.TryGetValue(listener, out wrapper!))
        {
            return false;
        }

        map.Remove(listener);
        return true;
    }
}

/// <summary>A host EventTarget for JS <c>new EventTarget()</c> with no DOM
/// identity. Mirrors the Starling backend's InMemoryEventTarget.</summary>
internal sealed class InMemoryEventTarget : EventTarget { }

/// <summary>Boxes a <see cref="JsValue"/> (a struct) so it can live in a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> (reference values only).</summary>
internal sealed class JsValueBox
{
    public JsValue Value { get; }
    public JsValueBox(JsValue value) => Value = value;
}

internal static partial class EventTargetBindingLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "Uncaught (in event listener) {Detail}")]
    public static partial void UncaughtInEventListener(ILogger logger, string detail);
}
