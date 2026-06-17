using AwesomeAssertions;
using Starling.Bindings.Observers;
using Starling.Dom;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;
namespace Starling.Bindings.Tests;

/// <summary>
/// B5-4 — IntersectionObserver tests. Covers the constructable / observable /
/// disconnectable JS surface, plus entry delivery off a layout snapshot: an
/// initial notification per observed target, threshold-gated
/// <c>isIntersecting</c>, and idempotent re-delivery.
/// </summary>
[TestClass]
public sealed class IntersectionObserverTests
{
    [TestMethod]
    public void Constructor_is_installed_globally_with_correct_name()
    {
        var (runtime, _) = BuildEnv();
        Eval(runtime, "result = typeof IntersectionObserver;")
            .AsString.Should().Be("function");
        Eval(runtime, "result = IntersectionObserver.name;")
            .AsString.Should().Be("IntersectionObserver");
    }

    [TestMethod]
    public void Constructor_requires_callable_argument()
    {
        var (runtime, _) = BuildEnv();
        Eval(runtime, """
            try { new IntersectionObserver(); result = 'no-throw'; }
            catch (e) { result = (e && e.constructor && e.constructor.name) || 'other'; }
        """).AsString.Should().Be("TypeError");

        Eval(runtime, """
            try { new IntersectionObserver(123); result = 'no-throw'; }
            catch (e) { result = (e && e.constructor && e.constructor.name) || 'other'; }
        """).AsString.Should().Be("TypeError");
    }

    [TestMethod]
    public void Constructor_accepts_callback_and_default_options()
    {
        var (runtime, _) = BuildEnv();
        Eval(runtime, """
            var io = new IntersectionObserver(function () {});
            result = io.constructor.name + '/' + io.rootMargin + '/' + io.thresholds.length;
        """).AsString.Should().Be("IntersectionObserver/0px 0px 0px 0px/1");
    }

    [TestMethod]
    public void Threshold_out_of_range_throws_range_error()
    {
        var (runtime, _) = BuildEnv();
        Eval(runtime, """
            try { new IntersectionObserver(function () {}, { threshold: 1.5 }); result = 'no-throw'; }
            catch (e) { result = (e && e.constructor && e.constructor.name) || 'other'; }
        """).AsString.Should().Be("RangeError");
    }

    [TestMethod]
    public void Observe_requires_element_target()
    {
        var (runtime, _) = BuildEnv();
        Eval(runtime, """
            var io = new IntersectionObserver(function () {});
            try { io.observe({}); result = 'no-throw'; }
            catch (e) { result = (e && e.constructor && e.constructor.name) || 'other'; }
        """).AsString.Should().Be("TypeError");
    }

    [TestMethod]
    public void Observe_and_unobserve_succeed()
    {
        var (runtime, _) = BuildEnv();
        Eval(runtime, """
            var io = new IntersectionObserver(function () {});
            var el = document.createElement('div');
            document.body.appendChild(el);
            io.observe(el);
            io.unobserve(el);
            io.disconnect();
            io.disconnect();
            result = 'ok';
        """).AsString.Should().Be("ok");
    }

    [TestMethod]
    public void Take_records_returns_empty_array()
    {
        var (runtime, _) = BuildEnv();
        Eval(runtime, """
            var io = new IntersectionObserver(function () {});
            var r = io.takeRecords();
            result = Array.isArray(r) && r.length === 0 ? 'ok' : 'fail';
        """).AsString.Should().Be("ok");
    }

    [TestMethod]
    public void Threshold_array_is_parsed_and_exposed()
    {
        var (runtime, _) = BuildEnv();
        Eval(runtime, """
            var io = new IntersectionObserver(function () {}, { threshold: [0, 0.5, 1] });
            result = io.thresholds.length + ':' + io.thresholds[1];
        """).AsString.Should().Be("3:0.5");
    }

    [TestMethod]
    public void Observe_delivers_intersecting_for_in_view_and_not_for_below_fold()
    {
        var (runtime, doc, a, b) = BuildLaidOutEnv();

        Eval(runtime, """
            globalThis.calls = [];
            var a = document.getElementById('a');
            var b = document.getElementById('b');
            var io = new IntersectionObserver(function (entries) {
                for (const e of entries)
                    calls.push((e.target === a ? 'a' : 'b') + ':' + e.isIntersecting);
            }, { threshold: 0 });
            io.observe(a);
            io.observe(b);
        """);
        // The settle pump delivers against the initial (unscrolled) viewport.
        IntersectionObserverBinding.RunPending(runtime);

        // a (y 100) is inside the 800-tall viewport; b (y 2000) is below it.
        Eval(runtime, "result = calls.slice().sort().join(',');")
            .AsString.Should().Be("a:true,b:false");
    }

    [TestMethod]
    public void Scrolling_the_viewport_reveals_a_below_fold_target()
    {
        var (runtime, doc, a, b) = BuildLaidOutEnv();

        Eval(runtime, """
            globalThis.calls = [];
            var a = document.getElementById('a');
            var b = document.getElementById('b');
            var io = new IntersectionObserver(function (entries) {
                for (const e of entries)
                    calls.push((e.target === a ? 'a' : 'b') + ':' + e.isIntersecting);
            }, { threshold: 0 });
            io.observe(a);
            io.observe(b);
        """);
        // Initial delivery against the unscrolled viewport (a in view, b below).
        IntersectionObserverBinding.RunPending(runtime);
        Eval(runtime, "calls.length = 0;");

        // Scroll so the viewport (0,1800,1000,800) covers b (y 2000) but not a (y 100).
        IntersectionObserverBinding.UpdateForDocument(doc, new LayoutRect(0, 1800, 1000, 800));
        Drain(runtime);

        Eval(runtime, "result = calls.slice().sort().join(',');")
            .AsString.Should().Be("a:false,b:true");
    }

    // ---- delivery off a layout snapshot ------------------------------------

    [TestMethod]
    public void Observe_delivers_initial_intersecting_entry_for_in_viewport_target()
    {
        var host = new FakeLayoutHost();
        host.Rects["t"] = new LayoutRect(10, 10, 40, 40); // fully inside 100x100
        var (runtime, _) = BuildEnv(host, 100, 100);

        IntersectionObserverBinding.HasPending(runtime).Should().BeFalse("no observer yet");
        SetupObserver(runtime, threshold: "0");
        IntersectionObserverBinding.HasPending(runtime).Should().BeTrue("an observed target awaits its initial report");

        IntersectionObserverBinding.RunPending(runtime).Should().BeTrue();

        // isIntersecting, ratio, target identity, observer identity.
        ReadLog(runtime).Should().Be("true,1.00,true,true");
    }

    [TestMethod]
    public void Below_viewport_target_reports_not_intersecting()
    {
        var host = new FakeLayoutHost();
        host.Rects["t"] = new LayoutRect(0, 200, 40, 40); // below a 100-tall viewport
        var (runtime, _) = BuildEnv(host, 100, 100);

        SetupObserver(runtime, threshold: "0");
        IntersectionObserverBinding.RunPending(runtime);

        ReadLog(runtime).Should().Be("false,0.00,true,true");
    }

    [TestMethod]
    public void Threshold_gates_is_intersecting()
    {
        // Target sits with only 25% of its area inside the viewport.
        var host = new FakeLayoutHost();
        host.Rects["t"] = new LayoutRect(0, 90, 40, 40); // 10px of 40px tall visible -> ratio 0.25
        var (runtime, _) = BuildEnv(host, 100, 100);

        SetupObserver(runtime, threshold: "0.5");
        IntersectionObserverBinding.RunPending(runtime);

        // 0.25 ratio does not meet the 0.5 threshold, so isIntersecting is false.
        ReadLog(runtime).Should().Be("false,0.25,true,true");
    }

    [TestMethod]
    public void Delivery_is_idempotent_until_state_changes()
    {
        var host = new FakeLayoutHost();
        host.Rects["t"] = new LayoutRect(10, 10, 40, 40);
        var (runtime, _) = BuildEnv(host, 100, 100);

        SetupObserver(runtime, threshold: "0");
        IntersectionObserverBinding.RunPending(runtime).Should().BeTrue();
        // Layout is unchanged, so there is nothing new to deliver.
        IntersectionObserverBinding.HasPending(runtime).Should().BeFalse();
        IntersectionObserverBinding.RunPending(runtime).Should().BeFalse();

        Eval(runtime, "result = String(window.__log.length);").AsString.Should().Be("1");
    }

    [TestMethod]
    public void Disconnect_before_delivery_suppresses_the_callback()
    {
        var host = new FakeLayoutHost();
        host.Rects["t"] = new LayoutRect(10, 10, 40, 40);
        var (runtime, _) = BuildEnv(host, 100, 100);

        SetupObserver(runtime, threshold: "0");
        Eval(runtime, "io.disconnect();");
        IntersectionObserverBinding.HasPending(runtime).Should().BeFalse();
        IntersectionObserverBinding.RunPending(runtime).Should().BeFalse();
        Eval(runtime, "result = String(window.__log.length);").AsString.Should().Be("0");
    }

    /// <summary>Creates <c>window.__log</c>, a target <c>#t</c> appended to the
    /// body, and an observer that records each entry as
    /// <c>isIntersecting,ratio,targetMatch,observerMatch</c>.</summary>
    private static void SetupObserver(JsRuntime runtime, string threshold)
    {
        Eval(runtime, $$"""
            window.__log = [];
            var el = document.createElement('div');
            el.id = 't';
            document.body.appendChild(el);
            var io = new IntersectionObserver(function (entries, obs) {
                for (var i = 0; i < entries.length; i++) {
                    var e = entries[i];
                    window.__log.push(
                        e.isIntersecting + ',' + e.intersectionRatio.toFixed(2)
                        + ',' + (e.target === el) + ',' + (obs === io));
                }
            }, { threshold: {{threshold}} });
            io.observe(el);
        """);
    }

    private static string ReadLog(JsRuntime runtime)
        => Eval(runtime, "result = window.__log.join('|');").AsString;

    private static (JsRuntime, Document) BuildEnv(
        ILayoutHost? layoutHost = null, double viewportWidth = 0, double viewportHeight = 0)
    {
        var doc = new Document();
        var html = doc.CreateElement("html");
        var body = doc.CreateElement("body");
        doc.AppendChild(html);
        html.AppendChild(body);
        var runtime = new JsRuntime();
        WindowBinding.Install(runtime, doc, new WindowInstallOptions(
            InnerWidth: viewportWidth, InnerHeight: viewportHeight, LayoutHost: layoutHost));
        return (runtime, doc);
    }

    // A document with two positioned targets and a fake layout host: 'a' sits in
    // the initial 1000x800 viewport, 'b' is 2000px down (below the fold).
    private static (JsRuntime, Document, Element a, Element b) BuildLaidOutEnv()
    {
        var doc = new Document();
        var html = doc.CreateElement("html");
        var body = doc.CreateElement("body");
        doc.AppendChild(html);
        html.AppendChild(body);
        var a = doc.CreateElement("div");
        a.SetAttribute("id", "a");
        var b = doc.CreateElement("div");
        b.SetAttribute("id", "b");
        body.AppendChild(a);
        body.AppendChild(b);

        var host = new FakeLayoutHost(new Dictionary<Element, LayoutRect>
        {
            [a] = new LayoutRect(0, 100, 200, 50),
            [b] = new LayoutRect(0, 2000, 200, 50),
        });
        var runtime = new JsRuntime();
        WindowBinding.Install(runtime, doc,
            new WindowInstallOptions(InnerWidth: 1000, InnerHeight: 800, LayoutHost: host));
        return (runtime, doc, a, b);
    }

    // Drains queued microtasks (observer delivery rides the realm microtask queue,
    // which WithActiveVm flushes on exit).
    private static void Drain(JsRuntime runtime) => runtime.WithActiveVm(() => { });

    private static JsValue Eval(JsRuntime runtime, string source)
    {
        var program = new JsParser(source).ParseProgram();
        var chunk = JsCompiler.Compile(program);
        new JsVm(runtime).Run(chunk);
        return runtime.GetGlobal("result");
    }

    /// <summary>Layout host backed by a fixed map of element → viewport rect.
    /// Entries can be seeded by Element (constructor) or by element id
    /// (<see cref="Rects"/>). Elements without an entry report "not laid out".</summary>
    private sealed class FakeLayoutHost : ILayoutHost
    {
        private readonly Dictionary<Element, LayoutRect> _byElement;
        public readonly Dictionary<string, LayoutRect> Rects = new();

        public FakeLayoutHost() => _byElement = new();
        public FakeLayoutHost(Dictionary<Element, LayoutRect> rects) => _byElement = rects;

        public bool TryGetBoundingClientRect(Element element, out LayoutRect rect)
        {
            if (_byElement.TryGetValue(element, out rect))
            {
                return true;
            }

            return Rects.TryGetValue(element.Id, out rect);
        }

        public bool TryGetOffsetMetrics(Element element, out OffsetMetrics metrics)
        {
            metrics = default;
            return false;
        }

        public string GetComputedProperty(Element element, string propertyName) => string.Empty;

        public bool MatchMedia(string query) => false;
    }
}
