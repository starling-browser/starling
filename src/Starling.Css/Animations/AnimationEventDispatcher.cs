using Starling.Dom.Events;

namespace Starling.Css.Animations;

/// <summary>
/// Turns the engines' queued <see cref="AnimationEventRecord"/> facts into
/// real DOM events and dispatches them on their target elements. The embedder
/// calls <see cref="DispatchPending"/> once per frame, after ticking the
/// engines (see <c>StarlingEngine.PrepareAnimationFrame</c>) — never from
/// inside the style/compose pass, because listeners mutate the DOM. JS
/// listeners run too: the bindings bridge every JS listener onto the host
/// <see cref="Starling.Dom.Events.EventTarget"/>, so a host-side dispatch is
/// the same path <c>dispatchEvent</c> takes.
/// </summary>
public static class AnimationEventDispatcher
{
    /// <summary>
    /// Drain both engines' pending DOM-event facts and dispatch them in queue
    /// order (animation events first, then transition events). Records are
    /// drained into a local list before any dispatch so listeners that start
    /// or cancel more animations queue facts for the next frame instead of
    /// invalidating this drain. Returns the number of events dispatched —
    /// zero, with no allocation, on the common no-event frame.
    /// </summary>
    public static int DispatchPending(AnimationEngine animations, TransitionEngine transitions)
    {
        ArgumentNullException.ThrowIfNull(animations);
        ArgumentNullException.ThrowIfNull(transitions);
        if (!animations.HasPendingEvents && !transitions.HasPendingEvents)
        {
            return 0;
        }

        var records = new List<AnimationEventRecord>();
        animations.DrainPendingEvents(records);
        transitions.DrainPendingEvents(records);
        for (var i = 0; i < records.Count; i++)
        {
            Dispatch(records[i]);
        }

        return records.Count;
    }

    private static void Dispatch(in AnimationEventRecord rec)
    {
        // CSS Animations 1 §5 / CSS Transitions 2 §events: all of these
        // bubble and none are cancelable.
        var init = new EventInit(Bubbles: true, Cancelable: false, Composed: false);
        Event ev = rec.Kind switch
        {
            AnimationEventKind.AnimationStart => new AnimationEvent("animationstart", init)
            { AnimationName = rec.Name, ElapsedTime = rec.ElapsedSeconds },
            AnimationEventKind.AnimationIteration => new AnimationEvent("animationiteration", init)
            { AnimationName = rec.Name, ElapsedTime = rec.ElapsedSeconds },
            AnimationEventKind.AnimationEnd => new AnimationEvent("animationend", init)
            { AnimationName = rec.Name, ElapsedTime = rec.ElapsedSeconds },
            AnimationEventKind.TransitionRun => new TransitionEvent("transitionrun", init)
            { PropertyName = rec.Name, ElapsedTime = rec.ElapsedSeconds },
            AnimationEventKind.TransitionStart => new TransitionEvent("transitionstart", init)
            { PropertyName = rec.Name, ElapsedTime = rec.ElapsedSeconds },
            AnimationEventKind.TransitionEnd => new TransitionEvent("transitionend", init)
            { PropertyName = rec.Name, ElapsedTime = rec.ElapsedSeconds },
            _ => new TransitionEvent("transitioncancel", init)
            { PropertyName = rec.Name, ElapsedTime = rec.ElapsedSeconds },
        };
        ev.IsTrusted = true; // engine-generated, not script-synthesized
        rec.Element.DispatchEvent(ev);
    }
}
