using AwesomeAssertions;
using Starling.Css.Animations;
using Starling.Dom;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;
using Starling.Spec;

namespace Starling.Bindings.Tests;

/// <summary>
/// Web Animations 1 §4 JS object-model conformance on the Starling JS engine:
/// <c>element.animate()</c> returns an <c>Animation</c> with the expected
/// control surface and an associated <c>KeyframeEffect</c>. Object-model tests
/// run without an animation host (inert playback); playback tests install a
/// fake <see cref="IAnimationHost"/> over the real
/// <see cref="AnimationEngine"/> with an injected clock so finishing is
/// observable tick by tick.
/// </summary>
[TestClass]
public sealed class WebAnimationsBindingTests
{
    private static (JsRuntime, Element) NewSession()
    {
        var doc = new Document();
        var html = doc.CreateElement("html");
        var body = doc.CreateElement("body");
        var div = doc.CreateElement("div");
        div.SetAttribute("id", "d");
        doc.AppendChild(html);
        html.AppendChild(body);
        body.AppendChild(div);
        var runtime = new JsRuntime();
        WindowBinding.Install(runtime, doc, new WindowInstallOptions());
        return (runtime, div);
    }

    private static JsValue Eval(JsRuntime runtime, string source)
    {
        var program = new JsParser(source).ParseProgram();
        var chunk = JsCompiler.Compile(program);
        new JsVm(runtime).Run(chunk);
        return runtime.GetGlobal("result");
    }

    [Spec("web-animations-1", "https://www.w3.org/TR/web-animations-1/#dom-animatable-animate", section: "4")]
    [SpecFact]
    public void Animate_returns_an_Animation_with_a_control_surface()
    {
        var (rt, _) = NewSession();
        const string make = "var a = document.getElementById('d').animate([{opacity:0},{opacity:1}], 500);";
        Eval(rt, make + "result = typeof a.play;").AsString.Should().Be("function");
        Eval(rt, make + "result = typeof a.pause;").AsString.Should().Be("function");
        Eval(rt, make + "result = typeof a.cancel;").AsString.Should().Be("function");
        Eval(rt, make + "result = typeof a.finish;").AsString.Should().Be("function");
        Eval(rt, make + "result = typeof a.currentTime;").AsString.Should().Be("number");
        Eval(rt, make + "result = typeof a.playState;").AsString.Should().Be("string");
    }

    [Spec("web-animations-1", "https://www.w3.org/TR/web-animations-1/#dom-animation-finished", section: "4")]
    [SpecFact]
    public void Animation_finished_is_a_thenable()
    {
        var (rt, _) = NewSession();
        Eval(rt, "result = typeof document.getElementById('d').animate([{opacity:0},{opacity:1}], 500).finished.then;")
            .AsString.Should().Be("function");
    }

    [Spec("web-animations-1", "https://www.w3.org/TR/web-animations-1/#dom-keyframeeffect-getkeyframes", section: "4")]
    [SpecFact]
    public void KeyframeEffect_exposes_keyframes_and_timing()
    {
        var (rt, _) = NewSession();
        const string make = "var e = document.getElementById('d').animate([{opacity:0},{opacity:1}], {duration:500, iterations:2, easing:'ease-in'}).effect;";
        Eval(rt, make + "result = e.getKeyframes().length;").AsNumber.Should().Be(2);
        Eval(rt, make + "result = e.getKeyframes()[0].offset;").AsNumber.Should().Be(0);
        Eval(rt, make + "result = e.getKeyframes()[1].offset;").AsNumber.Should().Be(1);
        Eval(rt, make + "result = e.getTiming().duration;").AsNumber.Should().Be(500);
        Eval(rt, make + "result = e.getTiming().iterations;").AsNumber.Should().Be(2);
        Eval(rt, make + "result = e.getTiming().easing;").AsString.Should().Be("ease-in");
        Eval(rt, make + "result = e.getComputedTiming().activeDuration;").AsNumber.Should().Be(1000);
    }

    // ===== playback tests: fake host over the real engine, injected clock ====

    private sealed class ClockAnimationHost : IAnimationHost
    {
        private readonly AnimationEngine _engine = new();
        private readonly Dictionary<int, AnimationInstance> _byId = new();
        private int _nextId = 1;

        public double Now { get; private set; }

        public double TimelineNow => Now;

        public int Animate(Element element, IReadOnlyList<AnimationKeyframeSpec> keyframes, AnimationEffectTimingSpec timing)
        {
            var decl = new AnimationDeclaration(
                "t", timing.DurationMs, timing.DelayMs, TimingFunction.Linear,
                double.IsNaN(timing.Iterations) ? 1 : timing.Iterations,
                AnimationDirection.Normal, AnimationFillMode.None, AnimationPlayState.Running);
            var inst = _engine.AddScriptAnimation(element, decl, new KeyframesRule("t", Array.Empty<Keyframe>()), Now);
            _byId[_nextId] = inst;
            return _nextId++;
        }

        public void Play(int id) => _byId[id].ScriptPlay();
        public void Pause(int id) => _byId[id].ScriptPause();
        public void Cancel(int id) => _byId[id].ScriptCancel();
        public void Finish(int id) => _byId[id].ScriptFinish();
        public double CurrentTime(int id) => _byId[id].ScriptCurrentTime();
        public void SetCurrentTime(int id, double ms) => _byId[id].ScriptSetCurrentTime(ms);

        public string PlayState(int id)
        {
            var i = _byId[id];
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

        public double PlaybackRate(int id) => _byId[id].ScriptPlaybackRate;
        public void SetPlaybackRate(int id, double rate) => _byId[id].ScriptSetPlaybackRate(rate);
        public void Observe(int id, Action onFinished, Action onCanceled)
            => _byId[id].SetScriptObservers(onFinished, onCanceled);

        /// <summary>Advance the injected clock and tick the engine once.</summary>
        public void Tick(double nowMs) { Now = nowMs; _engine.Tick(nowMs); }
    }

    private static (JsRuntime Runtime, ClockAnimationHost Host) NewHostedSession()
    {
        var doc = new Document();
        var html = doc.CreateElement("html");
        var body = doc.CreateElement("body");
        var div = doc.CreateElement("div");
        div.SetAttribute("id", "d");
        doc.AppendChild(html);
        html.AppendChild(body);
        body.AppendChild(div);
        var runtime = new JsRuntime();
        var host = new ClockAnimationHost();
        WindowBinding.Install(runtime, doc, new WindowInstallOptions(AnimationHost: host));
        return (runtime, host);
    }

    private static int TicksToFinish(JsRuntime rt, ClockAnimationHost host, double stepMs, int maxTicks)
    {
        for (var n = 1; n <= maxTicks; n++)
        {
            host.Tick(n * stepMs);
            if (Eval(rt, "result = a.playState;").AsString == "finished")
            {
                return n;
            }
        }
        return -1;
    }

    [Spec("web-animations-1", "https://www.w3.org/TR/web-animations-1/#dom-animation-playbackrate", section: "4")]
    [SpecFact]
    public void PlaybackRate_2_halves_ticks_to_finish()
    {
        var (rt1, h1) = NewHostedSession();
        Eval(rt1, "var a = document.getElementById('d').animate([{opacity:0},{opacity:1}], 1000); result = a.playbackRate;")
            .AsNumber.Should().Be(1);
        TicksToFinish(rt1, h1, stepMs: 100, maxTicks: 50).Should().Be(10);

        var (rt2, h2) = NewHostedSession();
        Eval(rt2, "var a = document.getElementById('d').animate([{opacity:0},{opacity:1}], 1000); a.playbackRate = 2; result = a.playbackRate;")
            .AsNumber.Should().Be(2);
        TicksToFinish(rt2, h2, stepMs: 100, maxTicks: 50).Should().Be(5);
    }

    [Spec("web-animations-1", "https://www.w3.org/TR/web-animations-1/#dom-animation-playbackrate", section: "4")]
    [SpecFact]
    public void Mid_flight_playbackRate_change_preserves_progress()
    {
        var (rt, host) = NewHostedSession();
        Eval(rt, "var a = document.getElementById('d').animate([{opacity:0},{opacity:1}], 1000);");
        host.Tick(400);
        Eval(rt, "result = a.currentTime;").AsNumber.Should().Be(400);
        Eval(rt, "a.playbackRate = 2; result = a.currentTime;").AsNumber.Should().Be(400);
        host.Tick(700); // 300ms of clock at rate 2 covers the remaining 600ms
        Eval(rt, "result = a.currentTime;").AsNumber.Should().Be(1000);
        Eval(rt, "result = a.playState;").AsString.Should().Be("finished");
    }

    [Spec("web-animations-1", "https://www.w3.org/TR/web-animations-1/#dom-animation-finished", section: "4")]
    [SpecFact]
    public void Finished_promise_is_pending_then_resolves_with_the_animation()
    {
        var (rt, host) = NewHostedSession();
        Eval(rt, "var a = document.getElementById('d').animate([{opacity:0},{opacity:1}], 200);" +
                 "var got = ''; a.finished.then(function(v){ got = v === a ? 'same' : 'different'; });");
        Eval(rt, "result = got;").AsString.Should().Be(""); // pending while running
        host.Tick(250);
        rt.DrainMicrotasks();
        Eval(rt, "result = got;").AsString.Should().Be("same");
    }

    [Spec("web-animations-1", "https://www.w3.org/TR/web-animations-1/#dom-animation-cancel", section: "4")]
    [SpecFact]
    public void Cancel_rejects_finished_with_AbortError_and_fires_oncancel()
    {
        var (rt, _) = NewHostedSession();
        Eval(rt, "var a = document.getElementById('d').animate([{opacity:0},{opacity:1}], 1000);" +
                 "var rejected = ''; var canceled = '';" +
                 "a.finished.catch(function(e){ rejected = e.name; });" +
                 "a.oncancel = function(ev){ canceled = ev.type; };" +
                 "a.cancel();");
        rt.DrainMicrotasks();
        Eval(rt, "result = rejected;").AsString.Should().Be("AbortError");
        Eval(rt, "result = canceled;").AsString.Should().Be("cancel");
        Eval(rt, "result = a.playState;").AsString.Should().Be("idle");
    }

    [Spec("web-animations-1", "https://www.w3.org/TR/web-animations-1/#dom-animation-cancel", section: "4")]
    [SpecFact]
    public void Cancel_without_a_host_still_rejects_and_fires_oncancel()
    {
        var (rt, _) = NewSession();
        Eval(rt, "var a = document.getElementById('d').animate([{opacity:0},{opacity:1}], 1000);" +
                 "var rejected = ''; var canceled = '';" +
                 "a.finished.catch(function(e){ rejected = e.name; });" +
                 "a.oncancel = function(ev){ canceled = ev.type; };" +
                 "a.cancel();");
        rt.DrainMicrotasks();
        Eval(rt, "result = rejected;").AsString.Should().Be("AbortError");
        Eval(rt, "result = canceled;").AsString.Should().Be("cancel");
    }

    [Spec("web-animations-1", "https://www.w3.org/TR/web-animations-1/#dom-animation-finish", section: "4")]
    [SpecFact]
    public void Finish_jumps_to_the_end_and_fires_onfinish()
    {
        var (rt, _) = NewHostedSession();
        Eval(rt, "var a = document.getElementById('d').animate([{opacity:0},{opacity:1}], 1000);" +
                 "var fin = ''; a.onfinish = function(ev){ fin = ev.type; };" +
                 "a.finish();");
        rt.DrainMicrotasks();
        Eval(rt, "result = fin;").AsString.Should().Be("finish");
        Eval(rt, "result = a.playState;").AsString.Should().Be("finished");
        Eval(rt, "result = a.currentTime;").AsNumber.Should().Be(1000);
    }

    [Spec("web-animations-1", "https://www.w3.org/TR/web-animations-1/#dom-animation-finish", section: "4")]
    [SpecFact]
    public void Finish_with_playbackRate_0_throws_InvalidStateError()
    {
        var (rt, _) = NewHostedSession();
        Eval(rt, "var a = document.getElementById('d').animate([{opacity:0},{opacity:1}], 1000);" +
                 "a.playbackRate = 0; var err = '';" +
                 "try { a.finish(); } catch (e) { err = e.name; } result = err;")
            .AsString.Should().Be("InvalidStateError");
    }

    [Spec("web-animations-1", "https://www.w3.org/TR/web-animations-1/#dom-animation-playbackrate", section: "4")]
    [SpecFact]
    public void Negative_playbackRate_throws_NotSupportedError()
    {
        var (rt, _) = NewSession();
        Eval(rt, "var a = document.getElementById('d').animate([{opacity:0},{opacity:1}], 1000);" +
                 "var err = ''; try { a.playbackRate = -1; } catch (e) { err = e.name; } result = err;")
            .AsString.Should().Be("NotSupportedError");
    }

    [Spec("web-animations-1", "https://www.w3.org/TR/web-animations-1/#dom-animatable-getanimations", section: "6")]
    [SpecFact]
    public void GetAnimations_lists_live_animations_and_drops_canceled_ones()
    {
        var (rt, _) = NewSession();
        Eval(rt, "var el = document.getElementById('d');" +
                 "var a1 = el.animate([{opacity:0},{opacity:1}], 1000);" +
                 "var a2 = el.animate([{opacity:1},{opacity:0}], 1000);" +
                 "result = el.getAnimations().length;").AsNumber.Should().Be(2);
        Eval(rt, "result = document.getAnimations().length;").AsNumber.Should().Be(2);
        Eval(rt, "a1.cancel(); result = el.getAnimations().length;").AsNumber.Should().Be(1);
        Eval(rt, "result = document.getAnimations().length;").AsNumber.Should().Be(1);
        Eval(rt, "result = el.getAnimations()[0] === a2 ? 'a2' : 'other';").AsString.Should().Be("a2");
    }
}
