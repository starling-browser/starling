namespace Starling.Dom.Events;

/// <summary>
/// Base DOM Event per [DOM §2.2](https://dom.spec.whatwg.org/#event).
/// </summary>
public class Event
{
    private IReadOnlyList<EventTarget> _composedPath = Array.Empty<EventTarget>();

    public Event(string type, EventInit init = default)
    {
        // Empty type is allowed: document.createEvent() produces an event whose
        // type is "" until legacy initEvent() sets it (DOM §2.9).
        ArgumentNullException.ThrowIfNull(type);
        Type = type;
        Bubbles = init.Bubbles;
        Cancelable = init.Cancelable;
        Composed = init.Composed;
        TimeStamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    public string Type { get; private set; }
    public EventTarget? Target { get; internal set; }
    public EventTarget? CurrentTarget { get; internal set; }
    public EventPhase EventPhase { get; internal set; } = EventPhase.None;
    public bool Bubbles { get; private set; }
    public bool Cancelable { get; private set; }
    public bool Composed { get; }

    /// <summary>True once a legacy <c>document.createEvent</c> + <c>initEvent</c>
    /// has initialized this event (or for constructor-built events, always).
    /// An uninitialized event must not be dispatched (DOM §2.9).</summary>
    public bool Initialized { get; internal set; } = true;

    /// <summary>DOM §2.9: mark this event as uninitialized. Called by the JS
    /// <c>document.createEvent()</c> binding so the event requires a
    /// subsequent <c>initEvent</c> call before it can be dispatched.</summary>
    public void MarkAsUninitialized() => Initialized = false;

    /// <summary>Legacy <c>Event.initEvent</c> (DOM §2.9). Re-initializes a
    /// not-yet-dispatched event; a no-op while dispatching. Sets type/bubbles/
    /// cancelable and clears the propagation + canceled flags.</summary>
    public void InitEvent(string type, bool bubbles, bool cancelable)
    {
        if (DispatchFlag)
        {
            return;
        }

        Initialized = true;
        Type = type;
        Bubbles = bubbles;
        Cancelable = cancelable;
        PropagationStopped = false;
        ImmediatePropagationStopped = false;
        DefaultPrevented = false;
    }
    public bool DefaultPrevented { get; internal set; }
    public bool IsTrusted { get; internal set; }
    public long TimeStamp { get; }

    public bool PropagationStopped { get; private set; }
    internal bool ImmediatePropagationStopped { get; private set; }
    internal bool DispatchFlag { get; set; }

    /// <summary>DOM §2.9 "in passive listener flag": set while a passive listener's
    /// callback runs so that <see cref="PreventDefault"/> becomes a no-op.</summary>
    internal bool InPassiveListener { get; set; }

    /// <summary>Public accessor for the dispatch flag — used by JS bindings
    /// to check whether this event is already being dispatched before calling
    /// <c>EventTarget.DispatchEvent</c> (which would throw internally).</summary>
    public bool IsBeingDispatched => DispatchFlag;

    public IReadOnlyList<EventTarget> ComposedPath => _composedPath;

    internal void SetComposedPath(IReadOnlyList<EventTarget> path) =>
        _composedPath = path;

    public void StopPropagation() => PropagationStopped = true;

    public void StopImmediatePropagation()
    {
        PropagationStopped = true;
        ImmediatePropagationStopped = true;
    }

    public void PreventDefault()
    {
        // DOM §2.9: preventDefault is a no-op inside a passive listener.
        if (Cancelable && !InPassiveListener)
        {
            DefaultPrevented = true;
        }
    }

    /// <summary>DOM §2.9 dispatch step: after dispatch, unset the stop-propagation
    /// and stop-immediate-propagation flags so the same instance can be dispatched
    /// again. The canceled flag (defaultPrevented) is intentionally preserved.</summary>
    internal void ClearPropagationFlags()
    {
        PropagationStopped = false;
        ImmediatePropagationStopped = false;
    }

    /// <summary>Reset transient flags so the same instance can be re-dispatched (test-only path).</summary>
    internal void ResetForDispatch()
    {
        Target = null;
        CurrentTarget = null;
        EventPhase = EventPhase.None;
        DefaultPrevented = false;
        PropagationStopped = false;
        ImmediatePropagationStopped = false;
        _composedPath = Array.Empty<EventTarget>();
    }
}

public readonly record struct EventInit(
    bool Bubbles = false,
    bool Cancelable = false,
    bool Composed = false);

public enum EventPhase : byte
{
    None = 0,
    CapturingPhase = 1,
    AtTarget = 2,
    BubblingPhase = 3,
}
