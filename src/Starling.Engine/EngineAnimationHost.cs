using Starling.Bindings;
using Starling.Css.Animations;
using Starling.Css.Parser;
using Starling.Css.Values;
using Starling.Dom;

namespace Starling.Engine;

/// <summary>
/// Engine implementation of the Web Animations API host seam
/// (<see cref="IAnimationHost"/>). Translates the binding's neutral keyframe /
/// timing payloads into the CSS animation model and registers them directly in
/// the document's persistent <see cref="AnimationEngine"/> (held by the
/// document's <see cref="AnimationTimeline"/>). Because that engine outlives the
/// per-layout <see cref="Starling.Css.Cascade.StyleEngine"/>, a script animation
/// is added once and survives relayouts — no per-frame re-import. Playback
/// control and readback go through the live <see cref="AnimationInstance"/>
/// looked up by handle id.
/// </summary>
internal sealed class EngineAnimationHost : IAnimationHost
{
    private readonly AnimationEngine _engine;
    private readonly Func<double> _clock;
    private readonly Dictionary<int, AnimationInstance> _byId = new();
    private int _counter;
    private int _nextId = 1;

    public EngineAnimationHost(AnimationEngine engine, Func<double>? clock = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        // Default to the animation engine's own timeline: the live Engine
        // constructs this host without a clock, and a zero clock would pin
        // every element.animate() start at t=0 while sampling runs on the
        // GUI stopwatch — instantly "finishing" any animation issued late.
        _clock = clock ?? (() => engine.NowMs);
    }

    public double TimelineNow => _clock();

    public int Animate(Element element, IReadOnlyList<AnimationKeyframeSpec> keyframes, AnimationEffectTimingSpec timing)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(keyframes);
        ArgumentNullException.ThrowIfNull(timing);

        var rule = BuildKeyframesRule(keyframes);
        var decl = BuildDeclaration(rule.Name, timing);
        var inst = _engine.AddScriptAnimation(element, decl, rule, TimelineNow);
        var id = _nextId++;
        _byId[id] = inst;
        return id;
    }

    public void Play(int id) { if (_byId.TryGetValue(id, out var i))
        {
            i.ScriptPlay();
        }
    }
    public void Pause(int id) { if (_byId.TryGetValue(id, out var i))
        {
            i.ScriptPause();
        }
    }
    public void Cancel(int id) { if (_byId.TryGetValue(id, out var i))
        {
            i.ScriptCancel();
        }
    }
    public void Finish(int id) { if (_byId.TryGetValue(id, out var i))
        {
            i.ScriptFinish();
        }
    }

    public double CurrentTime(int id) => _byId.TryGetValue(id, out var i) ? i.ScriptCurrentTime() : 0;
    public void SetCurrentTime(int id, double ms) { if (_byId.TryGetValue(id, out var i))
        {
            i.ScriptSetCurrentTime(ms);
        }
    }

    public double PlaybackRate(int id) => _byId.TryGetValue(id, out var i) ? i.ScriptPlaybackRate : 1;
    public void SetPlaybackRate(int id, double rate) { if (_byId.TryGetValue(id, out var i))
        {
            i.ScriptSetPlaybackRate(rate);
        }
    }

    public void Observe(int id, Action onFinished, Action onCanceled)
    {
        if (_byId.TryGetValue(id, out var i))
        {
            i.SetScriptObservers(onFinished, onCanceled);
        }
    }

    public string PlayState(int id)
    {
        if (!_byId.TryGetValue(id, out var i))
        {
            return "idle";
        }

        if (i.IsCanceled)
        {
            return "idle";
        }

        if (i.IsPaused)
        {
            return "paused";
        }

        if (i.IsCompleted)
        {
            return "finished";
        }

        return "running";
    }

    // ----- translation -------------------------------------------------------

    private KeyframesRule BuildKeyframesRule(IReadOnlyList<AnimationKeyframeSpec> keyframes)
    {
        var name = $"__waapi_{++_counter}";
        var frames = new List<Keyframe>(keyframes.Count);
        foreach (var kf in keyframes)
        {
            var decls = new List<KeyframeDeclaration>(kf.Declarations.Count);
            foreach (var (prop, text) in kf.Declarations)
            {
                if (ParseValue($"{prop}: {text}") is { } parsed)
                {
                    decls.Add(new KeyframeDeclaration(prop.ToLowerInvariant(), parsed));
                }
            }
            frames.Add(new Keyframe(kf.Offset, decls));
        }
        // Keyframes must be offset-sorted for the sampler's bracketing.
        frames.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));
        return new KeyframesRule(name, frames);
    }

    /// <summary>Parse a single "<c>property: value</c>" declaration string into
    /// its <see cref="CssValue"/>, or null if it doesn't parse.</summary>
    private static CssValue? ParseValue(string declarationText)
    {
        try
        {
            var decls = new CssParser(declarationText).ParseDeclarationList();
            return decls.Count > 0 ? CssValueParser.Parse(decls[0].Value) : null;
        }
        catch { return null; }
    }

    private static AnimationDeclaration BuildDeclaration(string name, AnimationEffectTimingSpec t)
    {
        var easing = ParseValue($"easing: {t.Easing}") is { } e
            ? TimingFunction.FromCss(e)
            : TimingFunction.Linear;

        return new AnimationDeclaration(
            name,
            t.DurationMs,
            t.DelayMs,
            easing,
            double.IsNaN(t.Iterations) ? 1 : t.Iterations,
            MapDirection(t.Direction),
            MapFill(t.Fill),
            AnimationPlayState.Running);
    }

    private static AnimationDirection MapDirection(string s) => s switch
    {
        "reverse" => AnimationDirection.Reverse,
        "alternate" => AnimationDirection.Alternate,
        "alternate-reverse" => AnimationDirection.AlternateReverse,
        _ => AnimationDirection.Normal,
    };

    private static AnimationFillMode MapFill(string s) => s switch
    {
        "forwards" => AnimationFillMode.Forwards,
        "backwards" => AnimationFillMode.Backwards,
        "both" => AnimationFillMode.Both,
        _ => AnimationFillMode.None,
    };
}
