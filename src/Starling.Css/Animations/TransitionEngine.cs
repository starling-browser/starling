using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Dom;

namespace Starling.Css.Animations;

/// <summary>
/// Runtime side of CSS Transitions 1. Owns the active transition table
/// (per element + property) and produces a "current effective value" that
/// the cascade layers above the computed value while a transition is in
/// flight.
/// <para>
/// Lifecycle: the cascade calls <see cref="OnComputedValueChanged"/> after
/// recomputing styles for an element. If the property is listed in
/// <c>transition-property</c> (or covered by <c>all</c>) and the new value
/// differs from the previous effective value, a new <see cref="ActiveTransition"/>
/// replaces any prior one for that (element, property) tuple. The
/// engine-driver ticks <see cref="Tick"/> from its frame loop
/// (intended to plug into the event-loop's <c>requestAnimationFrame</c>
/// queue once that's wired up — see WP notes); each tick advances the
/// progress and removes completed transitions.
/// </para>
/// <para>
/// The engine is single-threaded and not thread-safe; pages own their own
/// cascade thread and transition-engine instance.
/// </para>
/// </summary>
public sealed class TransitionEngine
{
    private readonly Dictionary<(Element, PropertyId), ActiveTransition> _active
        = new(ActiveKeyComparer.Instance);
    // Previous effective values per (element, property) — needed to detect
    // changes and to derive the from-value when a new transition starts.
    // Kept separate from _active because the previous value is still
    // interesting even after a transition completes (the next change is
    // measured from the completed end value, not from the original start).
    private readonly Dictionary<(Element, PropertyId), CssValue> _lastEffective
        = new(ActiveKeyComparer.Instance);

    private double _nowMs;

    // Pending transition DOM-event facts (transitionrun / transitionstart /
    // transitionend / transitioncancel) recorded as transitions are created,
    // replaced and completed. The embedder drains these after the frame's
    // style pass and dispatches real DOM events at a safe point — never
    // synchronously from inside the style/compose path, because listeners
    // mutate the DOM.
    private readonly List<AnimationEventRecord> _pendingEvents = new();

    /// <summary>
    /// Snapshot of an active transition for a given (element, property).
    /// Immutable once created — a new value for the same key replaces the
    /// record rather than mutating it. (The single exception is
    /// <see cref="ActiveTransition.StartEventFired"/>, DOM-event bookkeeping
    /// that does not affect sampling.)
    /// </summary>
    public sealed record ActiveTransition(
        Element Element,
        PropertyId Property,
        CssValue From,
        CssValue To,
        double StartMs,
        double DurationMs,
        double DelayMs,
        TimingFunction TimingFunction)
    {
        public double EndMs => StartMs + DelayMs + DurationMs;

        /// <summary>True once the <c>transitionstart</c> DOM-event fact was
        /// queued (the playback head entered the active phase). Event
        /// bookkeeping only — sampling never reads it.</summary>
        internal bool StartEventFired;
    }

    /// <summary>
    /// Current monotonic clock in milliseconds — the engine's notion of
    /// "now". Tests advance this directly via <see cref="Tick"/>; the host
    /// is expected to feed it monotonic time (e.g.,
    /// <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/>-derived).
    /// </summary>
    public double NowMs => _nowMs;

    /// <summary>Number of in-flight transitions; primarily exposed for tests / diagnostics.</summary>
    public int ActiveCount => _active.Count;

    /// <summary>The elements with at least one in-flight transition. Incremental
    /// layout marks these dirty every frame — a transition changes their computed
    /// style off the clock, with no DOM mutation to record.</summary>
    public IEnumerable<Element> ActiveElements
    {
        get
        {
            foreach (var (element, _) in _active.Keys)
            {
                yield return element;
            }
        }
    }

    /// <summary>Adds every element with at least one in-flight transition to
    /// <paramref name="into"/>. The live compositor snapshots this once per
    /// frame so its per-box probes are set lookups instead of enumerator
    /// probes over the whole active table.</summary>
    public void CollectActiveElements(ISet<Element> into)
    {
        foreach (var (element, _) in _active.Keys)
        {
            into.Add(element);
        }
    }

    /// <summary>
    /// Called by the cascade after computing a property value for
    /// <paramref name="element"/>. If the property is transitionable and
    /// <paramref name="newValue"/> differs from the previously effective
    /// value, registers a new transition driven by the transition shorthand
    /// values on the same element (read from <paramref name="readProperty"/>).
    /// </summary>
    public void OnComputedValueChanged(
        Element element,
        PropertyId property,
        CssValue newValue,
        Func<PropertyId, CssValue?> readProperty)
    {
        var key = (element, property);
        if (!_lastEffective.TryGetValue(key, out var previous))
        {
            // First time we see this property for this element: prime the
            // table without firing a transition (transitions only fire on
            // *changes* per Transitions 1 §3 — the initial style is exempt).
            _lastEffective[key] = newValue;
            return;
        }

        if (Equals(previous, newValue))
        {
            return;
        }

        if (!Interpolator.IsAnimatable(property))
        {
            _lastEffective[key] = newValue;
            return;
        }

        if (!IsPropertyTransitioned(property, readProperty))
        {
            // No transition declared for this property — record the new
            // effective value and skip animating. An in-flight transition for
            // the property is canceled (CSS Transitions 2: it leaves the set
            // of running transitions early, so transitioncancel fires).
            _lastEffective[key] = newValue;
            if (_active.TryGetValue(key, out var dropped))
            {
                QueueEvent(AnimationEventKind.TransitionCancel, dropped);
                _active.Remove(key);
            }
            return;
        }

        var duration = ReadTimeMs(readProperty(PropertyId.TransitionDuration), 0);
        var delay = ReadTimeMs(readProperty(PropertyId.TransitionDelay), 0);
        var timing = TimingFunction.FromCss(readProperty(PropertyId.TransitionTimingFunction));

        if (duration <= 0 && delay <= 0)
        {
            // Zero-duration transitions are no-ops by spec (the new value
            // takes effect immediately) — short-circuit instead of pushing
            // a transition that would complete on the next tick anyway. An
            // in-flight transition snapping to the new value this way is
            // canceled, not completed.
            _lastEffective[key] = newValue;
            if (_active.TryGetValue(key, out var snapped))
            {
                QueueEvent(AnimationEventKind.TransitionCancel, snapped);
                _active.Remove(key);
            }
            return;
        }

        // The from-value is the current effective value at the time of the
        // change — if there's an in-flight transition we sample it first so
        // a transition-in-progress doesn't snap back to its original start.
        var fromValue = previous;
        if (_active.TryGetValue(key, out var prior))
        {
            // A transition is already heading to this exact target. The live host
            // re-runs the cascade every animation frame (e.g. re-sampling a hover
            // override), so OnComputedValueChanged fires with the same target
            // repeatedly. Restarting it each frame resets StartMs, so it never
            // settles — the hovered element flickers between its base and target
            // state. Leave the in-flight transition running; only a genuinely new
            // target (below) starts a fresh one (Transitions 1 §3 "reversing").
            if (Equals(prior.To, newValue))
            {
                return;
            }

            fromValue = Sample(prior, _nowMs);
            // The in-flight transition is replaced before it completed —
            // it fires transitioncancel, then the replacement fires its own
            // transitionrun below.
            QueueEvent(AnimationEventKind.TransitionCancel, prior);
        }

        var created = new ActiveTransition(
            Element: element,
            Property: property,
            From: fromValue,
            To: newValue,
            StartMs: _nowMs,
            DurationMs: duration,
            DelayMs: delay,
            TimingFunction: timing);
        _active[key] = created;
        // transitionrun fires when the transition is created — the start of
        // its delay phase (CSS Transitions 2 §events). transitionstart waits
        // for the active phase and is queued by Tick.
        QueueEvent(AnimationEventKind.TransitionRun, created);
        // _lastEffective stays at `previous` until the next Tick computes a
        // sample — that way the next OnComputedValueChanged still sees the
        // pre-transition value as the comparison baseline if the cascade
        // re-runs before a Tick.
    }

    /// <summary>
    /// Returns the current effective value for the (element, property)
    /// pair, or null if the engine has never seen this pair. The cascade
    /// calls this after running its compute step and substitutes the
    /// returned value into the final ComputedStyle.
    /// </summary>
    public CssValue? GetEffective(Element element, PropertyId property)
    {
        var key = (element, property);
        if (_active.TryGetValue(key, out var t))
        {
            return Sample(t, _nowMs);
        }

        return _lastEffective.TryGetValue(key, out var v) ? v : null;
    }

    /// <summary>
    /// Advance the engine clock to <paramref name="nowMs"/> and prune any
    /// transitions that finished at or before this tick. Returns the number
    /// of transitions completed during this tick — non-zero means the host
    /// should request a re-paint.
    /// </summary>
    public int Tick(double nowMs)
    {
        if (nowMs < _nowMs)
        {
            // Time going backwards is a programming error — clamp to avoid
            // negative progress and a corrupted sample.
            nowMs = _nowMs;
        }

        _nowMs = nowMs;
        if (_active.Count == 0)
        {
            return 0;
        }

        var completed = 0;
        List<(Element, PropertyId)>? toRemove = null;
        foreach (var (key, t) in _active)
        {
            var sample = Sample(t, _nowMs);
            _lastEffective[key] = sample;
            if (!t.StartEventFired && _nowMs >= t.StartMs + t.DelayMs)
            {
                // The playback head entered the active phase: transitionstart
                // (CSS Transitions 2 §events). A tick that jumps straight past
                // the end still queues start first, then end below — in order.
                t.StartEventFired = true;
                QueueEvent(AnimationEventKind.TransitionStart, t);
            }
            if (_nowMs >= t.EndMs)
            {
                completed++;
                QueueEvent(AnimationEventKind.TransitionEnd, t);
                (toRemove ??= []).Add(key);
            }
        }
        if (toRemove is not null)
        {
            foreach (var k in toRemove)
            {
                _active.Remove(k);
            }
        }

        return completed;
    }

    /// <summary>
    /// Enumerate the <see cref="PropertyId"/>s that currently have an
    /// in-flight transition for <paramref name="element"/>.
    /// </summary>
    public IEnumerable<PropertyId> ActiveProperties(Element element)
    {
        foreach (var key in _active.Keys)
        {
            if (ReferenceEquals(key.Item1, element))
            {
                yield return key.Item2;
            }
        }
    }

    /// <summary>
    /// True when at least one in-flight transition on <paramref name="element"/>
    /// targets a layout-affecting property, so incremental layout must recompute
    /// the element. False when every transitioned property is paint- or
    /// composite-only (transform, opacity, color, …) — those need only a repaint.
    /// Conservatively true when no recognised property is in flight.
    /// </summary>
    public bool HasLayoutAffectingProperty(Element element)
    {
        var any = false;
        foreach (var key in _active.Keys)
        {
            if (!ReferenceEquals(key.Item1, element))
            {
                continue;
            }

            any = true;
            if (PropertyRegistry.AffectsLayout(key.Item2))
            {
                return true;
            }
        }
        return !any;
    }

    /// <summary>
    /// Clear all active transitions and the effective-value table for
    /// <paramref name="element"/>. Called by the host when an element is
    /// detached so we don't leak Element references through the dictionary.
    /// </summary>
    public void Forget(Element element)
    {
        var keys = _active.Keys.Where(k => ReferenceEquals(k.Item1, element)).ToList();
        foreach (var k in keys)
        {
            // Destroyed before completing — transitioncancel, not transitionend.
            QueueEvent(AnimationEventKind.TransitionCancel, _active[k]);
            _active.Remove(k);
        }
        var keys2 = _lastEffective.Keys.Where(k => ReferenceEquals(k.Item1, element)).ToList();
        foreach (var k in keys2)
        {
            _lastEffective.Remove(k);
        }
    }

    /// <summary>Reset the engine to an empty state — primarily for tests.</summary>
    public void Reset()
    {
        _active.Clear();
        _lastEffective.Clear();
        _pendingEvents.Clear();
        _nowMs = 0;
    }

    /// <summary>True when the engine queued transition DOM-event facts not yet drained.</summary>
    public bool HasPendingEvents => _pendingEvents.Count > 0;

    /// <summary>Move all pending transition DOM-event facts into
    /// <paramref name="into"/> (appended in fire order) and clear the queue.
    /// The embedder calls this after the frame's style pass and dispatches the
    /// corresponding DOM events (see <see cref="AnimationEventDispatcher"/>).</summary>
    public void DrainPendingEvents(List<AnimationEventRecord> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        if (_pendingEvents.Count == 0)
        {
            return;
        }

        into.AddRange(_pendingEvents);
        _pendingEvents.Clear();
    }

    /// <summary>Queue one transition DOM-event fact for <paramref name="t"/>.
    /// elapsedTime follows CSS Transitions 2: seconds, excluding the delay
    /// phase — zero for run/start unless the delay is negative, the full
    /// duration for end, and the time spent in the active phase for cancel.</summary>
    private void QueueEvent(AnimationEventKind kind, ActiveTransition t)
    {
        var elapsedMs = kind switch
        {
            AnimationEventKind.TransitionRun or AnimationEventKind.TransitionStart
                => Math.Min(Math.Max(-t.DelayMs, 0), t.DurationMs),
            AnimationEventKind.TransitionEnd => t.DurationMs,
            _ => Math.Min(Math.Max(_nowMs - t.StartMs - t.DelayMs, 0), t.DurationMs),
        };
        _pendingEvents.Add(new AnimationEventRecord(
            t.Element, kind, PropertyName(t.Property), elapsedMs / 1000.0));
    }

    private static CssValue Sample(ActiveTransition t, double nowMs)
    {
        var elapsed = nowMs - t.StartMs - t.DelayMs;
        if (elapsed <= 0)
        {
            return t.From;
        }

        if (t.DurationMs <= 0 || elapsed >= t.DurationMs)
        {
            return t.To;
        }

        var linear = elapsed / t.DurationMs;
        var eased = t.TimingFunction.Evaluate(linear);
        return Interpolator.Interpolate(t.Property, t.From, t.To, eased);
    }

    private static bool IsPropertyTransitioned(PropertyId property, Func<PropertyId, CssValue?> readProperty)
    {
        var declared = readProperty(PropertyId.TransitionProperty);
        // `all` (the spec default) matches every animatable property; any
        // explicit keyword that case-insensitively names the property also
        // matches. `none` is the spec opt-out.
        switch (declared)
        {
            case CssKeyword k:
                var name = k.Name.ToLowerInvariant();
                if (name == "none")
                {
                    return false;
                }

                if (name == "all")
                {
                    return true;
                }

                return string.Equals(name, PropertyName(property), StringComparison.OrdinalIgnoreCase);
            case CssValueList list:
                // Comma-separated transition-property list (e.g.,
                // `transition-property: opacity, transform`). We currently
                // accept any list whose items include `all` or the property
                // name. Per-layer pairing with duration/timing-function is
                // not implemented yet.
                foreach (var v in list.Values)
                {
                    if (v is CssKeyword item)
                    {
                        var nm = item.Name.ToLowerInvariant();
                        if (nm == "all")
                        {
                            return true;
                        }

                        if (string.Equals(nm, PropertyName(property), StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                return false;
            default:
                return true;
        }
    }

    private static double ReadTimeMs(CssValue? value, double fallback)
    {
        return value switch
        {
            CssTime t => t.InSeconds * 1000.0,
            CssDimension d when d.Unit == "s" => d.Value * 1000.0,
            CssDimension d when d.Unit == "ms" => d.Value,
            CssNumber n => n.Value, // bare numbers are interpreted as ms — lenient
            _ => fallback,
        };
    }

    private static string PropertyName(PropertyId id)
    {
        // PascalCase → kebab-case. Mirrors the spec's property naming
        // without adding a dependency on the (non-existent) CssPropertyName
        // attribute we'd otherwise want. The transformation is mechanical
        // because all CSS property names already follow kebab-case modulo
        // the casing.
        var s = id.ToString();
        var sb = new System.Text.StringBuilder(s.Length + 4);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (i > 0 && char.IsUpper(c))
            {
                sb.Append('-');
            }

            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private sealed class ActiveKeyComparer : IEqualityComparer<(Element, PropertyId)>
    {
        public static readonly ActiveKeyComparer Instance = new();
        public bool Equals((Element, PropertyId) x, (Element, PropertyId) y)
            => ReferenceEquals(x.Item1, y.Item1) && x.Item2 == y.Item2;
        public int GetHashCode((Element, PropertyId) obj)
            => HashCode.Combine(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj.Item1), (int)obj.Item2);
    }
}
