using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Dom;

namespace Starling.Css.Animations;

/// <summary>
/// CSS Animations 1 runtime — drives `@keyframes`-based property animation
/// (the long-running, declaratively defined kind, as opposed to
/// <c>transition</c>'s implicit-on-change kind handled by
/// <see cref="TransitionEngine"/>).
/// <para>
/// The cascade calls <see cref="OnAnimationsCascaded"/> after computing
/// the element's <c>animation-*</c> properties. The engine diffs the new
/// declaration set against the previously-active animations for the
/// element: new names start, dropped names stop, and existing names
/// update their parameters in place (per CSS Animations 1 §6 — only the
/// timing properties refresh, not the playback position). The frame
/// driver calls <see cref="Tick"/> each animation frame; the cascade
/// reads sampled property values back via <see cref="GetEffective"/>.
/// </para>
/// </summary>
public sealed class AnimationEngine
{
    private readonly Dictionary<Element, List<AnimationInstance>> _active
        = new(ReferenceEqualityComparer.Instance);
    // Script-driven (Web Animations API) animations. Kept separate from the
    // cascade-managed _active set so OnAnimationsCascaded — which rebuilds
    // _active from the cascaded animation-name list — never wipes a
    // programmatically created animation. Sampled / ticked alongside _active.
    private readonly Dictionary<Element, List<ScriptAnimation>> _scriptActive
        = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, KeyframesRule> _keyframes
        = new(StringComparer.Ordinal);
    private double _nowMs;
    // Pending animation DOM-event facts (animationstart / animationiteration /
    // animationend) recorded by Tick. The embedder drains these after the
    // frame's style pass and dispatches real DOM events at a safe point —
    // never synchronously from inside the style/compose path, because
    // listeners mutate the DOM.
    private readonly List<AnimationEventRecord> _pendingEvents = new();

    private sealed class ScriptAnimation(AnimationInstance instance, KeyframesRule rule)
    {
        public AnimationInstance Instance { get; } = instance;
        public KeyframesRule Rule { get; } = rule;
    }

    /// <summary>Current monotonic clock — exposed for diagnostics and tests.</summary>
    public double NowMs => _nowMs;

    /// <summary>True when any animation (cascade or script) is registered.</summary>
    public bool HasActive => _active.Count > 0 || _scriptActive.Count > 0;

    /// <summary>True when any animation is still advancing — playing, not yet
    /// completed, not paused/cancelled. The live loop repaints while this holds
    /// and stops (leaving the final frame) once everything settles, so a finished
    /// animation doesn't pin the frame loop on forever.</summary>
    public bool HasInFlight
    {
        get
        {
            foreach (var list in _active.Values)
            {
                foreach (var inst in list)
                {
                    if (inst.IsInFlight)
                    {
                        return true;
                    }
                }
            }

            foreach (var list in _scriptActive.Values)
            {
                foreach (var s in list)
                {
                    if (s.Instance.IsInFlight)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }

    /// <summary>Total in-flight animation instances across all elements; for tests.</summary>
    public int ActiveCount
    {
        get
        {
            var n = 0;
            foreach (var list in _active.Values)
            {
                n += list.Count;
            }

            return n;
        }
    }

    /// <summary>The elements with at least one in-flight animation. Incremental
    /// layout marks these dirty every frame: an animation changes their computed
    /// style off the animation clock, with no DOM mutation to record.</summary>
    public IEnumerable<Element> ActiveElements => _active.Keys;

    /// <summary>Adds every element with at least one registered animation —
    /// cascade-driven AND script-driven (Web Animations API) — to
    /// <paramref name="into"/>. The live compositor snapshots this once per
    /// frame so its per-box probes are set lookups instead of enumerator
    /// probes. Unlike <see cref="ActiveElements"/> (which feeds incremental
    /// layout and intentionally excludes script animations), this covers both
    /// tables, matching what <see cref="ActiveProperties"/> reports.</summary>
    public void CollectActiveElements(ISet<Element> into)
    {
        foreach (var el in _active.Keys)
        {
            into.Add(el);
        }

        foreach (var el in _scriptActive.Keys)
        {
            into.Add(el);
        }
    }

    /// <summary>
    /// Register (or replace) a parsed <c>@keyframes</c> rule by name. The
    /// most recent registration wins per spec — later <c>@keyframes</c> in
    /// the cascade replace earlier ones with the same name.
    /// </summary>
    public void RegisterKeyframes(KeyframesRule rule)
        => _keyframes[rule.Name] = rule;

    /// <summary>Clear all registered keyframes (e.g. between stylesheet swaps).</summary>
    public void ClearKeyframes() => _keyframes.Clear();

    /// <summary>True if a <c>@keyframes</c> rule with the given name is
    /// currently registered. Mostly useful for tests + diagnostics.</summary>
    public bool HasKeyframes(string name) => _keyframes.ContainsKey(name);

    /// <summary>Look up a registered <c>@keyframes</c> rule by name; returns
    /// <c>null</c> when no rule by that name exists.</summary>
    public KeyframesRule? GetKeyframes(string name)
        => _keyframes.TryGetValue(name, out var rule) ? rule : null;

    /// <summary>
    /// Register a script-created animation (Web Animations API
    /// <c>element.animate</c>) and return its <see cref="AnimationInstance"/>
    /// for playback control. Unlike cascade animations these carry their own
    /// keyframes and are not diffed by <see cref="OnAnimationsCascaded"/>, so a
    /// re-cascade of the element does not disturb them.
    /// </summary>
    public AnimationInstance AddScriptAnimation(Element element, AnimationDeclaration decl, KeyframesRule rule)
        => AddScriptAnimation(element, decl, rule, _nowMs);

    /// <summary>
    /// As <see cref="AddScriptAnimation(Element, AnimationDeclaration, KeyframesRule)"/>
    /// but pins the instance's start to <paramref name="startMs"/> on the
    /// engine clock's timeline. Used to re-import a persisted script animation
    /// into a freshly-built engine so its playback head lands where the page's
    /// timeline says it should (the <see cref="AnimationEngine"/> is rebuilt per
    /// layout pass; script animations live in a stable per-document store).
    /// </summary>
    public AnimationInstance AddScriptAnimation(Element element, AnimationDeclaration decl, KeyframesRule rule, double startMs)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(rule);
        var inst = new AnimationInstance(element, decl, _nowMs);
        inst.SetStart(startMs);
        if (!_scriptActive.TryGetValue(element, out var list))
        {
            _scriptActive[element] = list = new List<ScriptAnimation>();
        }

        list.Add(new ScriptAnimation(inst, rule));
        return inst;
    }

    /// <summary>Remove a script animation (cancel cleanup). No-op if unknown.</summary>
    public void RemoveScriptAnimation(AnimationInstance instance)
    {
        foreach (var (el, list) in _scriptActive)
        {
            if (list.RemoveAll(s => ReferenceEquals(s.Instance, instance)) > 0)
            {
                if (list.Count == 0)
                {
                    _scriptActive.Remove(el);
                }

                return;
            }
        }
    }

    /// <summary>
    /// Diff the element's previously-active animation list against the
    /// newly cascaded <c>animation-name</c> list and start / update / stop
    /// instances accordingly. Pass <c>null</c> or an empty list to stop all
    /// animations on the element.
    /// </summary>
    public void OnAnimationsCascaded(
        Element element,
        IReadOnlyList<AnimationDeclaration>? declarations)
    {
        if (declarations is null || declarations.Count == 0)
        {
            _active.Remove(element);
            return;
        }

        if (!_active.TryGetValue(element, out var list))
        {
            list = new List<AnimationInstance>();
            _active[element] = list;
        }

        // Match-by-name across the existing list so a re-cascade that
        // changes only the duration / direction doesn't reset playback.
        // Per spec, a re-cascade that includes the same name on the same
        // element keeps the instance ticking from its current StartMs.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rebuilt = new List<AnimationInstance>(declarations.Count);
        foreach (var d in declarations)
        {
            if (string.IsNullOrEmpty(d.Name) || d.Name == "none")
            {
                continue;
            }

            if (!seen.Add(d.Name))
            {
                continue; // duplicates use the first occurrence
            }

            var existing = list.FirstOrDefault(a => a.Name == d.Name);
            if (existing is not null)
            {
                existing.Update(d);
                rebuilt.Add(existing);
            }
            else
            {
                rebuilt.Add(new AnimationInstance(element, d, _nowMs));
            }
        }
        if (rebuilt.Count == 0)
        {
            _active.Remove(element);
        }
        else
        {
            _active[element] = rebuilt;
        }
    }

    /// <summary>Returns the current sampled value for <paramref name="property"/>, or null if no animation affects it.</summary>
    public CssValue? GetEffective(Element element, PropertyId property)
    {
        // Later-listed animations override earlier ones for the same
        // property, per CSS Animations 1 §6. Cascade animations are sampled
        // first; script animations (WAAPI) overlay on top, matching the
        // implementation-defined ordering where explicit animations win.
        CssValue? winning = null;
        if (_active.TryGetValue(element, out var list))
        {
            foreach (var inst in list)
            {
                if (!_keyframes.TryGetValue(inst.Name, out var rule))
                {
                    continue;
                }

                var sample = inst.Sample(property, rule, _nowMs);
                if (sample is not null)
                {
                    winning = sample;
                }
            }
        }

        if (_scriptActive.TryGetValue(element, out var slist))
        {
            foreach (var s in slist)
            {
                var sample = s.Instance.Sample(property, s.Rule, _nowMs);
                if (sample is not null)
                {
                    winning = sample;
                }
            }
        }

        return winning;
    }

    /// <summary>
    /// Enumerate the distinct <see cref="PropertyId"/>s currently targeted by
    /// any active animation on <paramref name="element"/>. Returns an empty
    /// sequence if the element has no active animations or none of their
    /// keyframes touch a recognised property.
    /// </summary>
    public IEnumerable<PropertyId> ActiveProperties(Element element)
    {
        var seen = new HashSet<PropertyId>();
        if (_active.TryGetValue(element, out var list))
        {
            foreach (var inst in list)
            {
                if (!_keyframes.TryGetValue(inst.Name, out var rule))
                {
                    continue;
                }

                foreach (var frame in rule.Frames)
                {
                    foreach (var decl in frame.Declarations)
                    {
                        if (PropertyRegistry.TryGetPropertyId(decl.Property, out var id) && seen.Add(id))
                        {
                            yield return id;
                        }
                    }
                }
            }
        }

        if (_scriptActive.TryGetValue(element, out var slist))
        {
            foreach (var s in slist)
            {
                foreach (var frame in s.Rule.Frames)
                {
                    foreach (var decl in frame.Declarations)
                    {
                        if (PropertyRegistry.TryGetPropertyId(decl.Property, out var id) && seen.Add(id))
                        {
                            yield return id;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// True when at least one in-flight animation on <paramref name="element"/>
    /// targets a layout-affecting property (width, margin, font-size, …), so
    /// incremental layout must recompute the element's box. False when every
    /// animated property is paint- or composite-only (transform, opacity, color,
    /// …) — those need only a repaint, not a relayout. Conservatively returns
    /// true when no recognised animated property is found, so an unknown
    /// animation still gets a (correct) relayout.
    /// </summary>
    public bool HasLayoutAffectingProperty(Element element)
    {
        var any = false;
        foreach (var id in ActiveProperties(element))
        {
            any = true;
            if (PropertyRegistry.AffectsLayout(id))
            {
                return true;
            }
        }
        return !any;
    }

    /// <summary>Advance the engine clock. Returns the number of animations that
    /// completed (entered a non-replaying terminal state) during this tick.</summary>
    public int Tick(double nowMs)
    {
        if (nowMs < _nowMs)
        {
            nowMs = _nowMs; // clamp on time-going-backwards
        }

        _nowMs = nowMs;
        var completed = 0;
        foreach (var list in _active.Values)
        {
            foreach (var inst in list)
            {
                if (inst.NoteTick(nowMs))
                {
                    completed++;
                }
                // Lifecycle DOM events fire only for animations that actually
                // run — an animation-name without a registered @keyframes rule
                // creates no animation (CSS Animations 1 §3).
                if (_keyframes.ContainsKey(inst.Name))
                {
                    inst.CollectLifecycleEvents(nowMs, _pendingEvents);
                }
            }
        }

        foreach (var list in _scriptActive.Values)
        {
            foreach (var s in list)
            {
                if (s.Instance.NoteTick(nowMs))
                {
                    completed++;
                }
            }
        }

        return completed;
    }

    /// <summary>True when Tick queued animation DOM-event facts not yet drained.</summary>
    public bool HasPendingEvents => _pendingEvents.Count > 0;

    /// <summary>Move all pending animation DOM-event facts into
    /// <paramref name="into"/> (appended in fire order) and clear the queue.
    /// The embedder calls this after the frame's style pass and dispatches the
    /// corresponding DOM events through the bindings layer.</summary>
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

    /// <summary>Forget all animation state for <paramref name="element"/> (detached element cleanup).</summary>
    public void Forget(Element element)
    {
        _active.Remove(element);
        _scriptActive.Remove(element);
    }

    /// <summary>Reset to empty state — for tests.</summary>
    public void Reset()
    {
        _active.Clear();
        _scriptActive.Clear();
        _keyframes.Clear();
        _pendingEvents.Clear();
        _nowMs = 0;
    }
}

/// <summary>
/// Snapshot of the <c>animation-*</c> cascade for a single layer
/// (one entry in a comma-separated animation shorthand). The engine binds
/// one of these to a matching <see cref="KeyframesRule"/>.
/// </summary>
public sealed record AnimationDeclaration(
    string Name,
    double DurationMs,
    double DelayMs,
    TimingFunction TimingFunction,
    double IterationCount,
    AnimationDirection Direction,
    AnimationFillMode FillMode,
    AnimationPlayState PlayState);

public enum AnimationDirection { Normal, Reverse, Alternate, AlternateReverse }
public enum AnimationFillMode { None, Forwards, Backwards, Both }
public enum AnimationPlayState { Running, Paused }

/// <summary>
/// Mutable, per-element-per-animation playback state. Holds enough
/// timing context to sample the right keyframe pair at any clock value.
/// </summary>
public sealed class AnimationInstance
{
    private AnimationDeclaration _decl;
    private double _startMs;
    private bool _completed;
    // When paused, we accumulate the elapsed time the playback head reached
    // at pause time; on resume, StartMs is shifted forward by the pause
    // duration so Sample() reads the same offset.
    private double _pausedAtElapsedMs = -1;
    // Web Animations API control flags. Script pause uses the same frozen-elapsed
    // mechanism as declarative pause but is driven explicitly (not via the
    // cascaded play-state); cancel removes the animation from sampling.
    private bool _scriptPaused;
    private bool _canceled;
    // Web Animations API playbackRate. Local time advances at
    // (clock - start) * rate, so the whole timeline (delay included) scales.
    // 0 freezes the playback head (via _pausedAtElapsedMs); negative rates are
    // clamped to 0 — reverse playback is not implemented and the JS binding
    // rejects negatives before they reach the engine.
    private double _playbackRate = 1;
    // Script-animation observers (Web Animations binding): fired once on the
    // transition to finished (natural NoteTick completion or ScriptFinish) and
    // once on the transition to canceled. May be invoked from the engine tick
    // thread — observers must only hand off (e.g. enqueue a microtask), never
    // run JS synchronously.
    private Action? _onScriptFinished;
    private Action? _onScriptCanceled;

    public AnimationInstance(Element element, AnimationDeclaration decl, double nowMs)
    {
        Element = element;
        _decl = decl;
        _startMs = nowMs;
        _nowSeenMs = nowMs;
    }

    public Element Element { get; }
    public string Name => _decl.Name;
    public double StartMs => _startMs;

    /// <summary>Pin the playback start to an explicit clock value (used when
    /// re-importing a persisted script animation into a freshly-built engine).</summary>
    internal void SetStart(double startMs) => _startMs = startMs;

    /// <summary>Update timing parameters without resetting playback (re-cascade case).</summary>
    public void Update(AnimationDeclaration decl)
    {
        _decl = decl;
        if (decl.PlayState == AnimationPlayState.Paused && _pausedAtElapsedMs < 0)
        {
            // Newly paused: freeze the elapsed offset.
            _pausedAtElapsedMs = Math.Max(0, _nowSeenMs - _startMs - _decl.DelayMs);
        }
        else if (decl.PlayState == AnimationPlayState.Running && _pausedAtElapsedMs >= 0)
        {
            // Resumed: shift start so the next sample reads the same elapsed.
            _startMs = _nowSeenMs - _decl.DelayMs - _pausedAtElapsedMs;
            _pausedAtElapsedMs = -1;
        }
    }

    private double _nowSeenMs;

    /// <summary>
    /// Called by the engine each tick so the instance can update its
    /// internal "last seen clock" (needed by pause logic on later Updates)
    /// and notice when it's reached the end. Returns true on the tick the
    /// instance transitions to its terminal state.
    /// </summary>
    public bool NoteTick(double nowMs)
    {
        _nowSeenMs = nowMs;
        if (_canceled || _scriptPaused)
        {
            return false;
        }

        if (_completed)
        {
            return false;
        }

        if (_decl.PlayState == AnimationPlayState.Paused)
        {
            return false;
        }

        if (_pausedAtElapsedMs >= 0 || _playbackRate <= 0)
        {
            return false; // frozen head (pause / rate 0)
        }

        if (double.IsInfinity(_decl.IterationCount) || _decl.IterationCount <= 0)
        {
            return false;
        }

        var total = _decl.DelayMs + _decl.DurationMs * _decl.IterationCount;
        if ((nowMs - _startMs) * _playbackRate >= total)
        {
            _completed = true;
            _onScriptFinished?.Invoke();
            return true;
        }
        return false;
    }

    // ---- CSS animation DOM events (CSS Animations 1 §5) --------------------
    // Additive event-fact tracking; never touches the playback math. Follows
    // the events-on-sample model from CSS Animations 2: each tick compares the
    // playback head against the boundaries not yet reported and queues at most
    // one animationstart / animationiteration / animationend fact each.
    private bool _eventStartFired;
    private bool _eventEndFired;
    private double _eventIterationsFired; // iteration boundaries already reported

    /// <summary>
    /// Append the animation DOM-event facts implied by the playback head at
    /// <paramref name="nowMs"/> to <paramref name="sink"/>. Called from
    /// <see cref="AnimationEngine.Tick"/> for cascade-managed instances only —
    /// script (Web Animations API) animations surface finish/cancel on the
    /// Animation object and never fire the CSS animation events. elapsedTime
    /// values follow the spec: seconds, excluding the delay phase.
    /// </summary>
    internal void CollectLifecycleEvents(double nowMs, List<AnimationEventRecord> sink)
    {
        if (_canceled || _scriptPaused || _decl.PlayState == AnimationPlayState.Paused)
        {
            return;
        }
        // An iteration count of 0 (or negative) never reaches a terminal state
        // in NoteTick; keep event behavior consistent and stay silent too.
        if (_decl.IterationCount <= 0)
        {
            return;
        }

        var elapsed = nowMs - _startMs - _decl.DelayMs;
        if (elapsed < 0)
        {
            return; // still inside the delay phase — nothing fires
        }

        var duration = Math.Max(0, _decl.DurationMs);
        var iterations = _decl.IterationCount;
        var activeMs = double.IsInfinity(iterations) ? double.PositiveInfinity : duration * iterations;

        if (!_eventStartFired)
        {
            _eventStartFired = true;
            // §5: elapsedTime = min(max(-delay, 0), active duration) — zero
            // unless the delay is negative (the head starts mid-animation).
            var startElapsed = Math.Min(Math.Max(-_decl.DelayMs, 0), activeMs);
            sink.Add(new AnimationEventRecord(Element, AnimationEventKind.AnimationStart, _decl.Name, startElapsed / 1000.0));
        }

        // A tick at/past the end of the active interval reports animationend
        // only: the final iteration boundary IS the end, and animationiteration
        // never fires when an animationend fires at the same sample (CSS
        // Animations 2). The gate also stops a late iteration report on the
        // tick after the end fired.
        var ended = !double.IsInfinity(iterations) && elapsed >= activeMs;

        if (duration > 0 && !ended)
        {
            // Iteration boundaries crossed so far, excluding the one that ends
            // the animation (that one is animationend, not animationiteration).
            // At most one event per tick — when a slow frame skips several
            // boundaries, report only the latest (CSS Animations 2 sampling).
            var boundaries = Math.Floor(elapsed / duration);
            if (!double.IsInfinity(iterations))
            {
                boundaries = Math.Min(boundaries, Math.Ceiling(iterations) - 1);
            }

            if (_eventIterationsFired < boundaries)
            {
                _eventIterationsFired = boundaries;
                sink.Add(new AnimationEventRecord(Element, AnimationEventKind.AnimationIteration, _decl.Name, boundaries * duration / 1000.0));
            }
        }

        if (!_eventEndFired && ended)
        {
            _eventEndFired = true;
            sink.Add(new AnimationEventRecord(Element, AnimationEventKind.AnimationEnd, _decl.Name, activeMs / 1000.0));
        }
    }

    // ---- Web Animations API playback control (element.animate) -------------

    /// <summary>True once <see cref="ScriptCancel"/> was called.</summary>
    public bool IsCanceled => _canceled;

    /// <summary>True while <see cref="ScriptPause"/> holds the playback head
    /// (Web Animations API <c>paused</c> state).</summary>
    public bool IsPaused => _scriptPaused;

    /// <summary>True once playback has reached its terminal state.</summary>
    public bool IsCompleted => _completed;

    /// <summary>True while the animation is still advancing: playing (not
    /// paused), not completed, not cancelled. An infinite-iteration animation is
    /// always in flight.</summary>
    public bool IsInFlight =>
        !_completed && !_canceled && !_scriptPaused && _pausedAtElapsedMs < 0
        && _playbackRate > 0 && _decl.PlayState != AnimationPlayState.Paused;

    /// <summary>Total active time (after delay) in ms — finite iterations only;
    /// infinite iteration counts report a single iteration's duration.</summary>
    private double ActiveDurationMs =>
        _decl.DurationMs * (double.IsInfinity(_decl.IterationCount) ? 1 : Math.Max(0, _decl.IterationCount));

    /// <summary>Current active-time (after delay) of the playback head, in ms.
    /// Reads the frozen head when one is set (pause or playbackRate 0).</summary>
    private double CurrentActiveElapsedMs()
        => _pausedAtElapsedMs >= 0
            ? Math.Max(0, _pausedAtElapsedMs)
            : Math.Max(0, (_nowSeenMs - _startMs) * _playbackRate - _decl.DelayMs);

    /// <summary>Animation.currentTime — elapsed active time in ms.</summary>
    public double ScriptCurrentTime() => CurrentActiveElapsedMs();

    /// <summary>Set Animation.currentTime by shifting the start so the playback
    /// head lands at <paramref name="ms"/> active-time.</summary>
    public void ScriptSetCurrentTime(double ms)
    {
        if (_playbackRate > 0)
        {
            _startMs = _nowSeenMs - (_decl.DelayMs + ms) / _playbackRate;
        }

        if (_pausedAtElapsedMs >= 0 || _playbackRate <= 0)
        {
            _pausedAtElapsedMs = ms;
        }

        _completed = false;
        _canceled = false;
    }

    /// <summary>Animation.pause() — freeze the playback head.</summary>
    public void ScriptPause()
    {
        if (_scriptPaused)
        {
            return;
        }

        _pausedAtElapsedMs = CurrentActiveElapsedMs();
        _scriptPaused = true;
    }

    /// <summary>Animation.play() — resume from the frozen head (or restart after cancel).</summary>
    public void ScriptPlay()
    {
        _canceled = false;
        _scriptPaused = false;
        if (_pausedAtElapsedMs >= 0 && _playbackRate > 0)
        {
            // Resume: shift start so (clock - start) * rate lands the active
            // head back on the frozen offset. Rate 0 stays frozen.
            _startMs = _nowSeenMs - (_decl.DelayMs + _pausedAtElapsedMs) / _playbackRate;
            _pausedAtElapsedMs = -1;
        }
        _completed = false;
    }

    /// <summary>Animation.playbackRate (default 1).</summary>
    public double ScriptPlaybackRate => _playbackRate;

    /// <summary>Set Animation.playbackRate, preserving the playback head across
    /// the change (the same start-shift trick the pause/resume path uses).
    /// Rate 0 freezes the head in place. Negative or NaN rates clamp to 0 —
    /// reverse playback is not implemented; the JS binding throws before
    /// calling here.</summary>
    public void ScriptSetPlaybackRate(double rate)
    {
        if (double.IsNaN(rate) || rate < 0)
        {
            rate = 0;
        }

        if (rate == _playbackRate)
        {
            return;
        }

        var elapsed = CurrentActiveElapsedMs();
        _playbackRate = rate;
        if (_scriptPaused || rate <= 0)
        {
            _pausedAtElapsedMs = elapsed;
            return;
        }
        _startMs = _nowSeenMs - (_decl.DelayMs + elapsed) / rate;
        _pausedAtElapsedMs = -1;
    }

    /// <summary>Register script-animation observers, fired once on the
    /// transition to finished (natural completion or <see cref="ScriptFinish"/>)
    /// and once on the transition to canceled. Invoked from whatever thread
    /// drives the tick — observers must only hand off (enqueue a microtask),
    /// never re-enter JS synchronously.</summary>
    public void SetScriptObservers(Action? onFinished, Action? onCanceled)
    {
        _onScriptFinished = onFinished;
        _onScriptCanceled = onCanceled;
    }

    /// <summary>Animation.cancel() — remove all effects (Sample returns null).</summary>
    public void ScriptCancel()
    {
        if (_canceled)
        {
            return;
        }

        _canceled = true;
        _onScriptCanceled?.Invoke();
    }

    /// <summary>Animation.finish() — jump the playback head to the end.</summary>
    public void ScriptFinish()
    {
        _scriptPaused = false;
        if (_playbackRate > 0)
        {
            _pausedAtElapsedMs = -1;
            _startMs = _nowSeenMs - (_decl.DelayMs + ActiveDurationMs) / _playbackRate;
        }
        else
        {
            // Rate 0 can't express "the end" as a start shift — park the head.
            _pausedAtElapsedMs = ActiveDurationMs;
        }
        var wasCompleted = _completed;
        _completed = true;
        if (!wasCompleted)
        {
            _onScriptFinished?.Invoke();
        }
    }

    /// <summary>
    /// Sample the property at <paramref name="nowMs"/> against the given
    /// rule. Returns null if the property is not animated by any of the
    /// rule's keyframes, the animation is in its before-active window with
    /// fill-mode that doesn't backfill, or after-active without
    /// forwards-fill.
    /// </summary>
    public CssValue? Sample(PropertyId property, KeyframesRule rule, double nowMs)
    {
        _nowSeenMs = nowMs;
        if (_canceled)
        {
            return null;
        }

        // Compute "iteration progress" p ∈ [0, 1] for this clock value,
        // honouring delay, playback rate, iteration count, direction, and fill
        // mode. A frozen head (pause or playbackRate 0) holds its active time.
        bool beforeActive;
        double elapsed;
        if (_pausedAtElapsedMs >= 0)
        {
            elapsed = _pausedAtElapsedMs;
            beforeActive = elapsed < 0;
        }
        else
        {
            var local = (nowMs - _startMs) * _playbackRate;
            beforeActive = local < _decl.DelayMs;
            elapsed = local - _decl.DelayMs;
        }

        var duration = _decl.DurationMs;
        if (duration <= 0)
        {
            // Spec §3.1: a duration of 0 makes the animation immediately
            // jump to its end state. We surface the keyframe at offset 1
            // (or 0, depending on direction) without further interpolation.
            return SampleAtProgress(property, rule, _decl.Direction is AnimationDirection.Reverse or AnimationDirection.AlternateReverse ? 0 : 1);
        }

        double progress;
        var iterations = _decl.IterationCount;
        var afterActive = !double.IsInfinity(iterations) && elapsed >= duration * iterations;
        if (beforeActive)
        {
            // Backfill: at progress=0 only if backwards/both fill mode is set.
            if (_decl.FillMode is AnimationFillMode.Backwards or AnimationFillMode.Both)
            {
                progress = StartProgress();
            }
            else
            {
                return null;
            }
        }
        else if (afterActive)
        {
            if (_decl.FillMode is AnimationFillMode.Forwards or AnimationFillMode.Both)
            {
                progress = EndProgress();
            }
            else
            {
                return null;
            }
        }
        else
        {
            var iter = elapsed / duration;
            var iterIndex = (int)Math.Floor(iter);
            var iterFraction = iter - iterIndex;
            // Direction handling: in alternate modes, odd iterations play
            // in reverse. AlternateReverse starts in reverse on iter 0.
            var reverse = _decl.Direction switch
            {
                AnimationDirection.Normal => false,
                AnimationDirection.Reverse => true,
                AnimationDirection.Alternate => (iterIndex & 1) == 1,
                AnimationDirection.AlternateReverse => (iterIndex & 1) == 0,
                _ => false,
            };
            progress = reverse ? 1.0 - iterFraction : iterFraction;
        }

        return SampleAtProgress(property, rule, progress);
    }

    private double StartProgress() => _decl.Direction is AnimationDirection.Reverse or AnimationDirection.AlternateReverse ? 1 : 0;
    private double EndProgress()
    {
        // When the final iteration of an `alternate` animation runs in
        // reverse, the "end progress" is 0; otherwise 1.
        if (double.IsInfinity(_decl.IterationCount))
        {
            return 1;
        }

        var lastIterIndex = (int)Math.Floor(Math.Max(0, _decl.IterationCount - 1));
        var endsReversed = _decl.Direction switch
        {
            AnimationDirection.Reverse => true,
            AnimationDirection.Alternate => (lastIterIndex & 1) == 1,
            AnimationDirection.AlternateReverse => (lastIterIndex & 1) == 0,
            _ => false,
        };
        return endsReversed ? 0 : 1;
    }

    private CssValue? SampleAtProgress(PropertyId property, KeyframesRule rule, double progress)
    {
        // Bracket by the raw iteration progress. CSS Animations 1 §7.1 says
        // the per-keyframe timing function (and, as a fallback, the
        // animation-level function) applies to the segment that starts at
        // the "before" keyframe — so we shouldn't pre-ease the iteration
        // progress before bracketing.
        progress = Math.Clamp(progress, 0, 1);

        // Find the bracketing keyframes for this property. Frames are
        // already sorted by offset (KeyframesParser stable-sorts on parse).
        // We need the property-bearing frames only — a keyframe without
        // the property contributes nothing to the segment for it.
        var propertyName = PropertyName(property);
        KeyframeDeclaration? before = null;
        double beforeOffset = 0;
        TimingFunction? beforeSegmentTiming = null;
        KeyframeDeclaration? after = null;
        double afterOffset = 1;
        foreach (var frame in rule.Frames)
        {
            var decl = FindDecl(frame.Declarations, propertyName);
            if (decl is null)
            {
                continue;
            }

            if (frame.Offset <= progress)
            {
                before = decl;
                beforeOffset = frame.Offset;
                beforeSegmentTiming = frame.SegmentTimingFunction;
            }
            if (frame.Offset >= progress) { after = decl; afterOffset = frame.Offset; break; }
        }
        if (before is null && after is null)
        {
            return null;
        }

        if (before is null) { before = after; beforeOffset = afterOffset; }
        if (after is null) { after = before; afterOffset = beforeOffset; }

        var span = afterOffset - beforeOffset;
        if (span <= 1e-9)
        {
            return before!.Value;
        }

        var segmentP = (progress - beforeOffset) / span;

        // §7.1: per-keyframe timing function on the *before* keyframe wins;
        // otherwise fall back to the animation-level function.
        var timing = beforeSegmentTiming ?? _decl.TimingFunction;
        segmentP = timing.Evaluate(Math.Clamp(segmentP, 0, 1));

        return Interpolator.Interpolate(property, before!.Value, after!.Value, segmentP);
    }

    private static KeyframeDeclaration? FindDecl(IReadOnlyList<KeyframeDeclaration> decls, string property)
    {
        for (var i = 0; i < decls.Count; i++)
        {
            if (string.Equals(decls[i].Property, property, StringComparison.OrdinalIgnoreCase))
            {
                return decls[i];
            }
        }

        return null;
    }

    private static string PropertyName(PropertyId id)
    {
        // PascalCase → kebab-case (same transformation TransitionEngine uses).
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
}
