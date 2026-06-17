using AwesomeAssertions;
using Starling.Css.Animations;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Dom;

namespace Starling.Css.Spec.Tests.CssAnimations1;

/// <summary>
/// §4 conformance — engine sampling: given parsed keyframes, assert
/// interpolated values at t=0, mid-point, and end.
/// Spec: <see href="https://www.w3.org/TR/css-animations-1/#keyframes"/>
/// </summary>
[TestClass]
[Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/", section: "4")]
public sealed class EngineSamplingTests
{
    // ── Helpers ────────────────────────────────────────────────────────────

    private static KeyframesRule FadeRule()
        => new("fade", new[]
        {
            new Keyframe(0.0, new[] { new KeyframeDeclaration("opacity", new CssNumber(0)) }),
            new Keyframe(1.0, new[] { new KeyframeDeclaration("opacity", new CssNumber(1)) }),
        });

    private static KeyframesRule ThreeStopRule()
        => new("pulse", new[]
        {
            new Keyframe(0.0,  new[] { new KeyframeDeclaration("opacity", new CssNumber(0.2)) }),
            new Keyframe(0.5,  new[] { new KeyframeDeclaration("opacity", new CssNumber(1.0)) }),
            new Keyframe(1.0,  new[] { new KeyframeDeclaration("opacity", new CssNumber(0.2)) }),
        });

    private static AnimationDeclaration Decl(
        string name = "fade",
        double durationMs = 1000,
        double delayMs = 0,
        double iterationCount = 1,
        AnimationDirection direction = AnimationDirection.Normal,
        AnimationFillMode fillMode = AnimationFillMode.None,
        AnimationPlayState playState = AnimationPlayState.Running,
        TimingFunction? timing = null)
        => new(name, durationMs, delayMs, timing ?? TimingFunction.Linear,
               iterationCount, direction, fillMode, playState);

    private static double OpacityAt(AnimationEngine engine, Element el)
        => ((CssNumber)engine.GetEffective(el, PropertyId.Opacity)!).Value;

    // ── §4.1  basic linear interpolation ──────────────────────────────────

    // §4.1 — at t=0 (start of active period) the from-value is returned.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.1")]
    [SpecFact]
    public void Sample_at_start_returns_from_value()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 1000) });

        OpacityAt(engine, el).Should().BeApproximately(0, 1e-6);
    }

    // §4.1 — at t=mid the interpolated mid-value is returned.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.1")]
    [SpecFact]
    public void Sample_at_midpoint_returns_interpolated_value()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 1000) });

        engine.Tick(500);
        OpacityAt(engine, el).Should().BeApproximately(0.5, 1e-6);
    }

    // §4.1 — at t=end (still within active period) the to-value is returned.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.1")]
    [SpecFact]
    public void Sample_at_end_active_period_returns_to_value()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 1000, fillMode: AnimationFillMode.Forwards) });

        engine.Tick(1000);
        OpacityAt(engine, el).Should().BeApproximately(1, 1e-6);
    }

    // §4.1 — quarter-point interpolation (25% through).
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.1")]
    [SpecFact]
    public void Sample_at_quarter_returns_interpolated_value()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 1000) });

        engine.Tick(250);
        OpacityAt(engine, el).Should().BeApproximately(0.25, 1e-6);
    }

    // §4.1 — three-stop keyframe: sample between from and mid-point.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.1")]
    [SpecFact]
    public void Three_stop_sample_between_zero_and_mid()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(ThreeStopRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(name: "pulse", durationMs: 1000) });

        // At 25% (halfway between 0% and 50%): interpolate 0.2 → 1.0 at 0.5 → 0.6.
        engine.Tick(250);
        OpacityAt(engine, el).Should().BeApproximately(0.6, 1e-6);
    }

    // §4.1 — three-stop keyframe: sample exactly at mid-point.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.1")]
    [SpecFact]
    public void Three_stop_sample_at_mid_keyframe_returns_exact_value()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(ThreeStopRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(name: "pulse", durationMs: 1000) });

        engine.Tick(500);
        OpacityAt(engine, el).Should().BeApproximately(1.0, 1e-6);
    }

    // §4.1 — three-stop keyframe: sample between mid-point and end.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.1")]
    [SpecFact]
    public void Three_stop_sample_between_mid_and_end()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(ThreeStopRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(name: "pulse", durationMs: 1000) });

        // At 75% (halfway between 50% and 100%): interpolate 1.0 → 0.2 at 0.5 → 0.6.
        engine.Tick(750);
        OpacityAt(engine, el).Should().BeApproximately(0.6, 1e-6);
    }

    // ── §4.2  engine registration and lookup ───────────────────────────────

    // §4.2 — registering a keyframes rule makes it available by name.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.2")]
    [SpecFact]
    public void Engine_registers_keyframes_by_name()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        engine.HasKeyframes("fade").Should().BeTrue();
    }

    // §4.2 — re-registering with same name replaces previous rule.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.2")]
    [SpecFact]
    public void Re_registering_same_name_replaces_rule()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule()); // 0 → 1
        engine.RegisterKeyframes(new KeyframesRule("fade", new[]
        {
            new Keyframe(0.0, new[] { new KeyframeDeclaration("opacity", new CssNumber(1)) }),
            new Keyframe(1.0, new[] { new KeyframeDeclaration("opacity", new CssNumber(0)) }),
        }));
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 1000) });

        engine.Tick(500);
        // Second rule wins (1 → 0), so at 50% the value is 0.5 counting down.
        OpacityAt(engine, el).Should().BeApproximately(0.5, 1e-6);
    }

    // §4.2 — GetKeyframes returns the registered rule.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.2")]
    [SpecFact]
    public void GetKeyframes_returns_registered_rule()
    {
        var engine = new AnimationEngine();
        var rule = FadeRule();
        engine.RegisterKeyframes(rule);
        engine.GetKeyframes("fade").Should().BeSameAs(rule);
    }

    // §4.2 — GetKeyframes returns null for an unknown name.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.2")]
    [SpecFact]
    public void GetKeyframes_returns_null_for_unknown_name()
    {
        var engine = new AnimationEngine();
        engine.GetKeyframes("unknown").Should().BeNull();
    }

    // §4.2 — sampling with an unregistered name yields null.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.2")]
    [SpecFact]
    public void Sampling_with_unregistered_name_returns_null()
    {
        var engine = new AnimationEngine();
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(name: "missing", durationMs: 500) });
        engine.Tick(250);
        engine.GetEffective(el, PropertyId.Opacity).Should().BeNull();
    }

    // ── §4.3  fill-mode + delay interaction ───────────────────────────────

    // §4.3 — no fill: before active period → null.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.3")]
    [SpecFact]
    public void Before_active_period_without_fill_returns_null()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 100, delayMs: 200) });
        engine.Tick(100); // still in delay
        engine.GetEffective(el, PropertyId.Opacity).Should().BeNull();
    }

    // §4.3 — no fill: after active period → null.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.3")]
    [SpecFact]
    public void After_active_period_without_fill_returns_null()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 100) });
        engine.Tick(500); // well past end
        engine.GetEffective(el, PropertyId.Opacity).Should().BeNull();
    }

    // §4.3 — fill-mode forwards: holds final value after end.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.3")]
    [SpecFact]
    public void Fill_mode_forwards_holds_final_value_after_end()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 100, fillMode: AnimationFillMode.Forwards) });
        engine.Tick(500);
        OpacityAt(engine, el).Should().BeApproximately(1, 1e-6);
    }

    // §4.3 — fill-mode backwards: holds initial value during delay.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.3")]
    [SpecFact]
    public void Fill_mode_backwards_holds_initial_value_during_delay()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 100, delayMs: 100, fillMode: AnimationFillMode.Backwards) });
        engine.Tick(50); // still in delay
        OpacityAt(engine, el).Should().BeApproximately(0, 1e-6);
    }

    // §4.3 — fill-mode both: initial during delay AND final after end.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.3")]
    [SpecFact]
    public void Fill_mode_both_holds_initial_during_delay_and_final_after_end()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 100, delayMs: 50, fillMode: AnimationFillMode.Both) });

        engine.Tick(20); // delay
        OpacityAt(engine, el).Should().BeApproximately(0, 1e-6);

        engine.Tick(500); // past end
        OpacityAt(engine, el).Should().BeApproximately(1, 1e-6);
    }

    // ── §4.4  direction modes ─────────────────────────────────────────────

    // §4.4 — direction reverse: at t=0 the to-value is returned.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.4")]
    [SpecFact]
    public void Direction_reverse_at_start_returns_to_value()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 1000, direction: AnimationDirection.Reverse) });
        OpacityAt(engine, el).Should().BeApproximately(1, 1e-6);
    }

    // §4.4 — direction reverse: at mid-point returns 0.5.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.4")]
    [SpecFact]
    public void Direction_reverse_at_midpoint_returns_midvalue()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 1000, direction: AnimationDirection.Reverse) });
        engine.Tick(500);
        OpacityAt(engine, el).Should().BeApproximately(0.5, 1e-6);
    }

    // §4.4 — direction alternate: even iterations forward, odd reversed.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.4")]
    [SpecFact]
    public void Direction_alternate_odd_iteration_plays_in_reverse()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 100, iterationCount: 2, direction: AnimationDirection.Alternate) });

        // Iteration 0 (normal): at 50ms → 0.5.
        engine.Tick(50);
        OpacityAt(engine, el).Should().BeApproximately(0.5, 1e-6);

        // Iteration 1 (reverse): at 150ms (50ms into iter 1, 50% of 100ms reverse) → 0.5.
        engine.Tick(150);
        OpacityAt(engine, el).Should().BeApproximately(0.5, 1e-6);

        // Iteration 1 at 25ms → 75% reversed → 0.75.
        engine.Reset();
        engine.RegisterKeyframes(FadeRule());
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 100, iterationCount: 2, direction: AnimationDirection.Alternate) });
        engine.Tick(125); // 25ms into iter 1 → reverse at 0.25 → value 1 - 0.25 = 0.75
        OpacityAt(engine, el).Should().BeApproximately(0.75, 1e-6);
    }

    // §4.4 — direction alternate-reverse: starts in reverse (iter 0 is reversed).
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.4")]
    [SpecFact]
    public void Direction_alternate_reverse_starts_at_to_value()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 100, iterationCount: 2, direction: AnimationDirection.AlternateReverse) });

        // Iter 0 is reversed → at t=0 the to-value (1) is returned.
        OpacityAt(engine, el).Should().BeApproximately(1, 1e-6);
    }

    // ── §4.5  iteration count variations ──────────────────────────────────

    // §4.5 — infinite iteration count keeps sampling past one cycle.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.5")]
    [SpecFact]
    public void Infinite_iteration_count_keeps_sampling_after_one_cycle()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 100, iterationCount: double.PositiveInfinity) });

        engine.Tick(10050); // 100 complete iterations + 50ms → progress = 0.5
        OpacityAt(engine, el).Should().BeApproximately(0.5, 1e-6);
        engine.ActiveCount.Should().Be(1);
    }

    // §4.5 — iteration count 2 wraps back to 0 for second iteration.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.5")]
    [SpecFact]
    public void Iteration_count_two_wraps_second_iteration_to_start()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 100, iterationCount: 2) });

        // 10ms into second iteration → progress = 0.1.
        engine.Tick(110);
        OpacityAt(engine, el).Should().BeApproximately(0.1, 1e-6);
    }

    // ── §4.6  active property set ─────────────────────────────────────────

    // §4.6 — ActiveProperties enumerates the properties referenced by keyframes.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.6")]
    [SpecFact]
    public void Active_properties_reflects_keyframe_declarations()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 500) });

        engine.ActiveProperties(el).Should().Contain(PropertyId.Opacity);
    }

    // §4.6 — Forget removes all state for the element.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.6")]
    [SpecFact]
    public void Forget_clears_element_animation_state()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 500) });
        engine.Tick(250);
        engine.GetEffective(el, PropertyId.Opacity).Should().NotBeNull();

        engine.Forget(el);
        engine.GetEffective(el, PropertyId.Opacity).Should().BeNull();
        engine.ActiveCount.Should().Be(0);
    }

    // ── §4.7  per-keyframe timing function ────────────────────────────────

    // §4.7 — per-keyframe timing function (linear on from-frame, ease on default)
    // causes different interpolation than the animation-level default.
    // We use ease-in and compare that the mid-sample is NOT 0.5 (linear).
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.7")]
    [SpecFact]
    public void Per_keyframe_timing_overrides_animation_level_timing()
    {
        // Build rule manually with per-keyframe ease-in timing.
        var rule = new KeyframesRule("f", new[]
        {
            new Keyframe(0.0,
                new[] { new KeyframeDeclaration("opacity", new CssNumber(0)) },
                TimingFunction.EaseIn),
            new Keyframe(1.0,
                new[] { new KeyframeDeclaration("opacity", new CssNumber(1)) }),
        });

        var engine = new AnimationEngine();
        engine.RegisterKeyframes(rule);
        var el = new Element("div");
        // Use linear as animation-level timing so any deviation shows the per-keyframe one is active.
        engine.OnAnimationsCascaded(el, new[] { Decl(name: "f", durationMs: 1000, timing: TimingFunction.Linear) });

        engine.Tick(500);
        // ease-in at 0.5 is significantly less than 0.5 (slow start).
        var v = OpacityAt(engine, el);
        v.Should().BeLessThan(0.5);
    }

    // ── §4.8  parsed @keyframes + engine integration ───────────────────────

    // §4.8 — keyframes parsed from CSS source can drive the engine.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.8")]
    [SpecFact]
    public void Parsed_keyframes_drive_engine_sampling()
    {
        var sheet = new Starling.Css.Parser.CssParser(
            "@keyframes fade { from { opacity: 0 } to { opacity: 1 } }")
            .ParseStyleSheet();
        var rules = KeyframesParser.ParseAll(sheet).ToList();
        rules.Should().ContainSingle();

        var engine = new AnimationEngine();
        engine.RegisterKeyframes(rules[0]);
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(name: "fade", durationMs: 1000) });

        engine.Tick(750);
        OpacityAt(engine, el).Should().BeApproximately(0.75, 1e-6);
    }

    // §4.8 — multi-keyframe CSS source with 0%/50%/100% drives engine correctly.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.8")]
    [SpecFact]
    public void Parsed_three_stop_keyframes_drive_engine_sampling()
    {
        var sheet = new Starling.Css.Parser.CssParser(
            "@keyframes pulse { 0% { opacity: 0.2 } 50% { opacity: 1.0 } 100% { opacity: 0.2 } }")
            .ParseStyleSheet();
        var rules = KeyframesParser.ParseAll(sheet).ToList();
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(rules[0]);
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(name: "pulse", durationMs: 1000) });

        // At 25% (between 0% and 50%): 0.2 + 0.5 * (1.0 - 0.2) = 0.6.
        engine.Tick(250);
        OpacityAt(engine, el).Should().BeApproximately(0.6, 1e-6);
    }

    // §4.8 — multiple @keyframes parsed from one sheet; engine plays each by name.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.8")]
    [SpecFact]
    public void Multiple_parsed_rules_play_independently()
    {
        var sheet = new Starling.Css.Parser.CssParser(
            "@keyframes a { from { opacity: 0 } to { opacity: 1 } }" +
            "@keyframes b { from { opacity: 1 } to { opacity: 0 } }")
            .ParseStyleSheet();
        var rules = KeyframesParser.ParseAll(sheet).ToList();

        var engine = new AnimationEngine();
        foreach (var r in rules)
        {
            engine.RegisterKeyframes(r);
        }

        var elA = new Element("div");
        var elB = new Element("div");
        engine.OnAnimationsCascaded(elA, new[] { Decl(name: "a", durationMs: 1000) });
        engine.OnAnimationsCascaded(elB, new[] { Decl(name: "b", durationMs: 1000) });

        engine.Tick(500);
        OpacityAt(engine, elA).Should().BeApproximately(0.5, 1e-6);
        OpacityAt(engine, elB).Should().BeApproximately(0.5, 1e-6);

        engine.Tick(750);
        OpacityAt(engine, elA).Should().BeApproximately(0.75, 1e-6);
        OpacityAt(engine, elB).Should().BeApproximately(0.25, 1e-6);
    }

    // §4.8 — animation-name: none stops sampling (integration test).
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.8")]
    [SpecFact]
    public void Animation_name_none_stops_sampling()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(name: "none", durationMs: 500) });
        engine.Tick(250);
        engine.GetEffective(el, PropertyId.Opacity).Should().BeNull();
    }

    // §4.8 — zero duration jumps to end state.
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.8")]
    [SpecFact]
    public void Zero_duration_animation_jumps_to_end_state()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[] { Decl(durationMs: 0, fillMode: AnimationFillMode.Forwards) });
        engine.Tick(0);
        // Duration 0 → jump to progress=1 (normal direction) → opacity=1.
        OpacityAt(engine, el).Should().BeApproximately(1, 1e-6);
    }

    // §4.8 — zero duration reverse jumps to start (progress=0 for reverse).
    [Spec("css-animations-1", "https://www.w3.org/TR/css-animations-1/#keyframes", section: "4.8")]
    [SpecFact]
    public void Zero_duration_reverse_animation_jumps_to_start_state()
    {
        var engine = new AnimationEngine();
        engine.RegisterKeyframes(FadeRule());
        var el = new Element("div");
        engine.OnAnimationsCascaded(el, new[]
        {
            Decl(durationMs: 0, direction: AnimationDirection.Reverse, fillMode: AnimationFillMode.Forwards),
        });
        engine.Tick(0);
        // Duration 0 + reverse → jump to progress=0 → opacity=0.
        OpacityAt(engine, el).Should().BeApproximately(0, 1e-6);
    }
}
