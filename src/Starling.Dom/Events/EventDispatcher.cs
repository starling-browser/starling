using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Starling.Dom.Events;

/// <summary>
/// Implements [DOM §2.9 Dispatching events](https://dom.spec.whatwg.org/#dispatching-events).
/// </summary>
internal static class EventDispatcher
{
    public static bool Dispatch(EventTarget target, Event @event)
    {
        @event.DispatchFlag = true;
        try
        {
            @event.Target = target;

            // Build the event path: target up to root. For Node targets, walk ParentNode.
            // path[0] is the target. DOM §2.9 invokes the target's capture-flagged
            // listeners during the capture pass and its non-capture listeners during
            // the bubble pass, both at the AT_TARGET phase — so capture listeners on
            // the target fire before its bubble listeners regardless of registration
            // order.
            var path = BuildPath(target);
            @event.SetComposedPath(path);

            // 1) Capture pass: root → target (inclusive). Ancestors run with the
            //    CAPTURING_PHASE; the target runs at AT_TARGET (capture listeners).
            for (var i = path.Count - 1; i >= 0; i--)
            {
                if (@event.PropagationStopped)
                {
                    break;
                }

                @event.EventPhase = i == 0 ? EventPhase.AtTarget : EventPhase.CapturingPhase;
                InvokeListeners(path[i], @event, capture: true);
            }

            // 2) Bubble pass: target → root. The target runs at AT_TARGET (its
            //    non-capture listeners); ancestors run at BUBBLING_PHASE, but only
            //    when the event bubbles.
            for (var i = 0; i < path.Count; i++)
            {
                if (@event.PropagationStopped)
                {
                    break;
                }

                if (i > 0 && !@event.Bubbles)
                {
                    break;
                }

                @event.EventPhase = i == 0 ? EventPhase.AtTarget : EventPhase.BubblingPhase;
                InvokeListeners(path[i], @event, capture: false);
            }
        }
        finally
        {
            // DOM §2.9 dispatch: reset eventPhase/currentTarget, clear the dispatch
            // flag, and unset the stop-propagation flags so the same event instance
            // can be dispatched again (canceled flag is preserved).
            @event.EventPhase = EventPhase.None;
            @event.CurrentTarget = null;
            @event.DispatchFlag = false;
            @event.ClearPropagationFlags();
        }

        return !@event.DefaultPrevented;
    }

    private static List<EventTarget> BuildPath(EventTarget target)
    {
        var path = new List<EventTarget> { target };
        if (target is Node node)
        {
            for (var ancestor = node.ParentNode; ancestor is not null; ancestor = ancestor.ParentNode)
            {
                path.Add(ancestor);
            }
        }
        return path;
    }

    /// <summary>
    /// Invoke listeners on <paramref name="target"/> matching the event type.
    /// <paramref name="capture"/>: true → capture-only, false → bubble-only, null → both (at-target phase).
    /// </summary>
    private static void InvokeListeners(EventTarget target, Event @event, bool? capture)
    {
        @event.CurrentTarget = target;
        // Snapshot to allow add/remove during dispatch (spec: only listeners present at start are invoked).
        var listeners = target.ListenersSnapshot();
        var needsCompact = false;

        foreach (var entry in listeners)
        {
            if (entry.Removed)
            {
                continue;
            }

            if (entry.Type != @event.Type)
            {
                continue;
            }

            if (capture is not null && entry.Capture != capture.Value)
            {
                continue;
            }

            if (entry.Once)
            {
                entry.Removed = true;
                needsCompact = true;
            }

            // DOM §2.9 inner invoke: set the in-passive-listener flag while a
            // passive listener runs so its preventDefault() is a no-op.
            @event.InPassiveListener = entry.Passive;
            try
            {
                entry.Listener(@event);
            }
            catch (Exception ex)
            {
                // Swallow listener exceptions per spec (browsers report to console, we drop on the floor in v1).
                EventDispatcherLog.ListenerException(NullLogger.Instance, ex, @event.Type);
            }
            finally
            {
                @event.InPassiveListener = false;
            }

            if (@event.ImmediatePropagationStopped)
            {
                break;
            }
        }

        if (needsCompact)
        {
            target.CompactListeners();
        }
    }
}

internal static partial class EventDispatcherLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "Event listener threw for event type '{EventType}'")]
    public static partial void ListenerException(ILogger logger, Exception ex, string eventType);
}
