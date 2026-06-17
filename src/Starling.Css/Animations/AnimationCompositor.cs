using Starling.Css.Cascade;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Dom;

namespace Starling.Css.Animations;

/// <summary>
/// Stitches <see cref="AnimationEngine"/> + <see cref="TransitionEngine"/>
/// into the cascade. Given a statically-cascaded <see cref="ComputedStyle"/>
/// and the current monotonic clock, <see cref="Compose"/> feeds new
/// cascaded values to the underlying engines (driving animation start/stop
/// and transition triggering) and returns a <see cref="ComputedStyle"/> with
/// the engines' current samples overlaid on top.
/// <para>
/// Effective-value priority (CSS Animations 1 §3.2):
/// <c>transition &gt; animation &gt; static cascade</c>.
/// </para>
/// </summary>
public sealed class AnimationCompositor
{
    private readonly AnimationEngine _animations;
    private readonly TransitionEngine _transitions;

    // Per-element cache so re-cascades that produce the same animation list
    // don't restart playback.
    private readonly Dictionary<Element, IReadOnlyList<AnimationDeclaration>> _lastDecls = new();
    // Per-element snapshot of the most recently observed static cascade for
    // every property listed in transition-property. The TransitionEngine
    // also keeps a snapshot but its is keyed by "every property it's ever
    // seen change"; this one is scoped to actually-transitioned properties
    // so we don't drive zero-duration transitions for unrelated changes.
    private readonly Dictionary<Element, Dictionary<PropertyId, CssValue>> _snapshots = new();

    public AnimationCompositor(AnimationEngine animations, TransitionEngine transitions)
    {
        ArgumentNullException.ThrowIfNull(animations);
        ArgumentNullException.ThrowIfNull(transitions);
        _animations = animations;
        _transitions = transitions;
    }

    /// <summary>
    /// Sample the engines at <paramref name="nowMs"/> and return a
    /// <see cref="ComputedStyle"/> where any animation- or transition-driven
    /// property values overlay the static cascade. Returns
    /// <paramref name="staticStyle"/> unchanged when no animation /
    /// transition is in flight for <paramref name="element"/>.
    /// </summary>
    public ComputedStyle Compose(Element element, ComputedStyle staticStyle, double nowMs)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(staticStyle);

        // 1. Detect cascade-level animation declaration changes and feed
        //    the AnimationEngine so it starts/stops instances. Compare
        //    structurally so a re-cascade with the same shorthand doesn't
        //    reset playback.
        var decls = BuildDeclarations(staticStyle);
        if (!_lastDecls.TryGetValue(element, out var prior) || !DeclsEqual(prior, decls))
        {
            _animations.OnAnimationsCascaded(element, decls);
            _lastDecls[element] = decls;
        }

        // 2. Detect transitioned-property changes and feed the
        //    TransitionEngine.
        var transitioned = ParseTransitionProperty(staticStyle);
        if (transitioned.Count > 0)
        {
            if (!_snapshots.TryGetValue(element, out var snap))
            {
                _snapshots[element] = snap = new Dictionary<PropertyId, CssValue>();
            }

            foreach (var prop in transitioned)
            {
                if (!staticStyle.TryGet(prop, out var newVal))
                {
                    continue;
                }
                // Only feed the TransitionEngine when the *static* cascade
                // value actually changed since we last saw it. The engine's
                // own _lastEffective gets mutated by Tick (it stores the
                // sampled mid-tween value), so re-feeding the same static
                // value would look like a change and restart the
                // transition every frame.
                if (snap.TryGetValue(prop, out var lastStatic) && Equals(lastStatic, newVal))
                {
                    continue;
                }

                _transitions.OnComputedValueChanged(element, prop, newVal, p =>
                    staticStyle.TryGet(p, out var v) ? v : null);
                snap[prop] = newVal;
            }
        }

        // 3. Collect overrides — union of properties touched by an active
        //    animation and properties with an active transition. Transition
        //    wins (§3.2). GetEffective returns null when nothing is in
        //    flight, so the natural cascade value wins by default.
        Dictionary<PropertyId, CssValue>? overrides = null;

        foreach (var prop in _animations.ActiveProperties(element))
        {
            var animVal = _animations.GetEffective(element, prop);
            if (animVal is null)
            {
                continue;
            }

            (overrides ??= new Dictionary<PropertyId, CssValue>())[prop] = animVal;
        }

        foreach (var prop in _transitions.ActiveProperties(element))
        {
            var transVal = _transitions.GetEffective(element, prop);
            if (transVal is null)
            {
                continue;
            }

            (overrides ??= new Dictionary<PropertyId, CssValue>())[prop] = transVal;
        }

        return overrides is null ? staticStyle : staticStyle.WithOverrides(overrides);
    }

    /// <summary>
    /// Sampling-only compose: overlays the current animation + transition
    /// samples onto <paramref name="baseStyle"/> WITHOUT the declaration
    /// diffing and transition-trigger detection of
    /// <see cref="Compose"/>. For pure animation ticks — frames where no
    /// static style changed (any style-affecting mutation forces a relayout,
    /// and the relayout's box-tree build runs the full <see cref="Compose"/>
    /// with a fresh static cascade) — those steps are no-ops by definition,
    /// and skipping them lets the caller pass the box's last-relayout style
    /// as the base instead of re-running the whole ancestor cascade per
    /// element per frame. Feeding a BAKED (already-composed) style through
    /// the full <see cref="Compose"/> would poison the transition snapshots
    /// with mid-tween values; this entry point has no such hazard.
    /// </summary>
    public ComputedStyle Sample(Element element, ComputedStyle baseStyle)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(baseStyle);

        Dictionary<PropertyId, CssValue>? overrides = null;

        foreach (var prop in _animations.ActiveProperties(element))
        {
            var animVal = _animations.GetEffective(element, prop);
            if (animVal is null)
            {
                continue;
            }

            (overrides ??= new Dictionary<PropertyId, CssValue>())[prop] = animVal;
        }

        foreach (var prop in _transitions.ActiveProperties(element))
        {
            var transVal = _transitions.GetEffective(element, prop);
            if (transVal is null)
            {
                continue;
            }

            (overrides ??= new Dictionary<PropertyId, CssValue>())[prop] = transVal;
        }

        return overrides is null ? baseStyle : baseStyle.WithOverrides(overrides);
    }

    /// <summary>Forget all per-element compositor state for
    /// <paramref name="element"/>. Call on element detachment alongside
    /// <see cref="AnimationEngine.Forget"/> + <see cref="TransitionEngine.Forget"/>.</summary>
    public void Forget(Element element)
    {
        _lastDecls.Remove(element);
        _snapshots.Remove(element);
    }

    /// <summary>Reset all per-element state — primarily for tests.</summary>
    public void Reset()
    {
        _lastDecls.Clear();
        _snapshots.Clear();
    }

    /// <summary>
    /// Turn the cascaded Animation* longhand values into one
    /// <see cref="AnimationDeclaration"/> per layer. Parallel longhand lists
    /// shorter than <c>animation-name</c> cycle from the start
    /// (CSS Animations 1 §4.1). Layers with <c>animation-name: none</c> are
    /// skipped.
    /// </summary>
    public static IReadOnlyList<AnimationDeclaration> BuildDeclarations(ComputedStyle style)
    {
        var names = AsList(style.Get(PropertyId.AnimationName));
        if (names.Count == 0)
        {
            return Array.Empty<AnimationDeclaration>();
        }

        var durations = AsList(style.Get(PropertyId.AnimationDuration));
        var delays = AsList(style.Get(PropertyId.AnimationDelay));
        var timings = AsList(style.Get(PropertyId.AnimationTimingFunction));
        var iterations = AsList(style.Get(PropertyId.AnimationIterationCount));
        var directions = AsList(style.Get(PropertyId.AnimationDirection));
        var fills = AsList(style.Get(PropertyId.AnimationFillMode));
        var playStates = AsList(style.Get(PropertyId.AnimationPlayState));

        var result = new List<AnimationDeclaration>(names.Count);
        for (var i = 0; i < names.Count; i++)
        {
            var name = NameOf(names[i]);
            if (name is null or "none")
            {
                continue;
            }

            result.Add(new AnimationDeclaration(
                name,
                TimeMs(Pick(durations, i)),
                TimeMs(Pick(delays, i)),
                ParseTimingFunction(Pick(timings, i)),
                IterationCount(Pick(iterations, i)),
                Direction(Pick(directions, i)),
                FillMode(Pick(fills, i)),
                PlayState(Pick(playStates, i))));
        }
        return result;
    }

    private static IReadOnlyList<PropertyId> ParseTransitionProperty(ComputedStyle style)
    {
        if (!style.TryGet(PropertyId.TransitionProperty, out var raw))
        {
            return Array.Empty<PropertyId>();
        }

        var values = AsList(raw);
        if (values.Count == 0)
        {
            return Array.Empty<PropertyId>();
        }

        var result = new List<PropertyId>(values.Count);
        foreach (var v in values)
        {
            if (v is not CssKeyword k)
            {
                continue;
            }

            if (k.Name is "none" or "")
            {
                continue;
            }

            if (k.Name is "all" or "initial")
            {
                // "all" expands to every animatable property; we can't
                // enumerate that cheaply, so let the engine catch changes
                // lazily by snapshotting only the properties the static
                // style actually defines (most pages use specific names).
                continue;
            }
            if (PropertyRegistry.TryGetPropertyId(k.Name, out var id))
            {
                result.Add(id);
            }
        }
        return result;
    }

    private static bool DeclsEqual(IReadOnlyList<AnimationDeclaration> a, IReadOnlyList<AnimationDeclaration> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (var i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<CssValue> AsList(CssValue value)
        => value is CssValueList list
            ? list.Values.Where(v => v is not CssKeyword { Name: "" }).ToList()
            : new[] { value };

    private static CssValue Pick(IReadOnlyList<CssValue> list, int i)
        => list.Count == 0 ? new CssKeyword("initial") : list[i % list.Count];

    private static string? NameOf(CssValue v) => v switch
    {
        CssKeyword k => k.Name,
        CssString s => s.Value,
        _ => null,
    };

    private static double TimeMs(CssValue v) => v switch
    {
        CssTime t => t.InSeconds * 1000d,
        CssDimension d when d.Unit == "s" => d.Value * 1000d,
        CssDimension d when d.Unit == "ms" => d.Value,
        CssNumber n => n.Value,
        _ => 0d,
    };

    private static double IterationCount(CssValue v) => v switch
    {
        CssNumber n => n.Value,
        CssKeyword { Name: "infinite" } => double.PositiveInfinity,
        _ => 1d,
    };

    private static AnimationDirection Direction(CssValue v) => v switch
    {
        CssKeyword { Name: "reverse" } => AnimationDirection.Reverse,
        CssKeyword { Name: "alternate" } => AnimationDirection.Alternate,
        CssKeyword { Name: "alternate-reverse" } => AnimationDirection.AlternateReverse,
        _ => AnimationDirection.Normal,
    };

    private static AnimationFillMode FillMode(CssValue v) => v switch
    {
        CssKeyword { Name: "forwards" } => AnimationFillMode.Forwards,
        CssKeyword { Name: "backwards" } => AnimationFillMode.Backwards,
        CssKeyword { Name: "both" } => AnimationFillMode.Both,
        _ => AnimationFillMode.None,
    };

    private static AnimationPlayState PlayState(CssValue v) => v switch
    {
        CssKeyword { Name: "paused" } => AnimationPlayState.Paused,
        _ => AnimationPlayState.Running,
    };

    private static TimingFunction ParseTimingFunction(CssValue v) => TimingFunction.FromCss(v);
}
