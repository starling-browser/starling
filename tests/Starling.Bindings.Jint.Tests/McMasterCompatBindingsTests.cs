using AwesomeAssertions;
using Jint;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Dom;
using Starling.Html;
using Starling.Loop;

namespace Starling.Bindings.Jint.Tests;

/// <summary>
/// Coverage for the Web-platform surface real pages (notably McMaster.com's
/// React app) depend on to boot: <c>document.implementation</c>/<c>location</c>,
/// the canvas text-measurement context, the window viewport/<c>screen</c>,
/// frame-tree globals, <c>performance.timing</c>, microtask/idle scheduling,
/// a firing <c>IntersectionObserver</c>, and the DOM/HTML/SVG/Event interface
/// globals named in <c>instanceof</c> / feature-detection checks.
/// </summary>
[TestClass]
public sealed class McMasterCompatBindingsTests
{
    private const string Html =
        "<!doctype html><html><head><title>Hi</title></head>" +
        "<body><div id='main'></div></body></html>";

    [TestMethod]
    public void Document_implementation_createHTMLDocument_returns_a_document()
    {
        var (engine, _) = NewSession(Html);
        engine.Evaluate("typeof document.implementation.createHTMLDocument").AsString().Should().Be("function");
        engine.Evaluate("document.implementation.createHTMLDocument('T').title").AsString().Should().Be("T");
        engine.Evaluate("document.implementation === document.implementation").AsBoolean().Should().BeTrue();
    }

    [TestMethod]
    public void Document_location_mirrors_window_location()
    {
        var (engine, _) = NewSession(Html, baseUrl: "https://example.com/a/b?q=1#h");
        engine.Evaluate("typeof document.location.href").AsString().Should().Be("string");
        engine.Evaluate("document.location.href").AsString().Should().Contain("example.com");
        engine.Evaluate("document.location === window.location").AsBoolean().Should().BeTrue();
        engine.Evaluate("document.defaultView === window").AsBoolean().Should().BeTrue();
    }

    [TestMethod]
    public void Canvas_getContext_2d_measures_text_with_positive_width()
    {
        var (engine, _) = NewSession(Html);
        engine.Evaluate(
            "var c = document.createElement('canvas'); var ctx = c.getContext('2d');" +
            "ctx.font = 'normal 20px Arial';");
        engine.Evaluate("typeof document.createElement('canvas').getContext('2d').measureText")
            .AsString().Should().Be("function");
        engine.Evaluate(
            "(function(){var ctx=document.createElement('canvas').getContext('2d');" +
            "ctx.font='20px Arial';return ctx.measureText('hello world').width;})()")
            .AsNumber().Should().BeGreaterThan(0);
        // Non-canvas elements return null from getContext (spec-correct).
        engine.Evaluate("document.createElement('div').getContext('2d')").IsNull().Should().BeTrue();
    }

    [TestMethod]
    public void Window_viewport_and_screen_reflect_the_supplied_size()
    {
        var (engine, _) = NewSession(Html, viewportWidth: 1280, viewportHeight: 900);
        engine.Evaluate("window.innerWidth").AsNumber().Should().Be(1280);
        engine.Evaluate("window.innerHeight").AsNumber().Should().Be(900);
        engine.Evaluate("screen.availHeight").AsNumber().Should().Be(900);
        engine.Evaluate("screen.width").AsNumber().Should().Be(1280);
    }

    [TestMethod]
    public void Frame_tree_globals_are_top_level()
    {
        var (engine, _) = NewSession(Html);
        engine.Evaluate("window.top === window").AsBoolean().Should().BeTrue();
        engine.Evaluate("window.parent === window").AsBoolean().Should().BeTrue();
        engine.Evaluate("window.frameElement").IsNull().Should().BeTrue();
    }

    [TestMethod]
    public void Performance_timing_exposes_numeric_milestones()
    {
        var (engine, _) = NewSession(Html);
        engine.Evaluate("typeof performance.timing.domInteractive").AsString().Should().Be("number");
        engine.Evaluate("typeof performance.timing.loadEventEnd").AsString().Should().Be("number");
        engine.Evaluate("performance.navigation.type").AsNumber().Should().Be(0);
        engine.Evaluate("Array.isArray(performance.getEntriesByType('resource'))").AsBoolean().Should().BeTrue();
    }

    [TestMethod]
    public void QueueMicrotask_runs_its_callback()
    {
        var (engine, _) = NewSession(Html);
        engine.Evaluate("var ran=false; queueMicrotask(function(){ ran=true; });");
        // queueMicrotask schedules on the event loop's microtask queue, which the
        // session pump drains each turn; drain it directly here.
        _lastCtx!.Loop.RunUntilIdle();
        engine.Evaluate("ran").AsBoolean().Should().BeTrue();
        engine.Evaluate("typeof requestIdleCallback").AsString().Should().Be("function");
    }

    [TestMethod]
    public void Interface_globals_exist_for_instanceof_and_feature_detection()
    {
        var (engine, _) = NewSession(Html);
        foreach (var name in new[]
        {
            "HTMLIFrameElement", "HTMLInputElement", "SVGAnimatedString", "HTMLCollection",
            "NodeList", "MouseEvent", "TouchEvent", "Blob", "FormData", "DOMException",
        })
        {
            engine.Evaluate($"typeof {name}").AsString().Should().Be("function", $"{name} should be a global constructor");
        }
        // The McMaster className helper does `n instanceof SVGAnimatedString`;
        // and React focus code does `el instanceof window.HTMLIFrameElement` —
        // neither must throw, and both are false for an HTML element's values.
        engine.Evaluate("('a' instanceof SVGAnimatedString)").AsBoolean().Should().BeFalse();
        engine.Evaluate("(document.body instanceof window.HTMLIFrameElement)").AsBoolean().Should().BeFalse();
    }

    [TestMethod]
    public void Constructed_subclass_events_are_instances_of_event()
    {
        var (engine, _) = NewSession(Html);
        engine.Evaluate("new MouseEvent('click') instanceof Event").AsBoolean().Should().BeTrue();
        engine.Evaluate("new MouseEvent('click').type").AsString().Should().Be("click");
    }

    [TestMethod]
    public void IntersectionObserver_delivers_an_intersecting_record()
    {
        var (engine, _) = NewSession(Html);
        // observe() must asynchronously report the target as intersecting so
        // lazy-rendered content (McMaster's React tiles) actually mounts.
        engine.Evaluate(
            "var hit=null;" +
            "var io=new IntersectionObserver(function(entries){ hit=entries[0]; });" +
            "io.observe(document.getElementById('main'));");
        // The notification is posted to a later turn; drain the post queue.
        for (var i = 0; i < 5 && engine.Evaluate("hit").IsNull(); i++)
        {
            _lastCtx!.DrainPosted();
        }

        engine.Evaluate("hit && hit.isIntersecting").AsBoolean().Should().BeTrue();
        engine.Evaluate("hit.intersectionRatio").AsNumber().Should().Be(1);
        engine.Evaluate("hit.target === document.getElementById('main')").AsBoolean().Should().BeTrue();
    }

    [TestMethod]
    public void TextEncoder_and_TextDecoder_are_global_constructors()
    {
        var (engine, _) = NewSession(Html);
        // Angular's main bundle does `new TextDecoder()` during module eval;
        // without the global it threw "ReferenceError: TextDecoder is not defined".
        engine.Evaluate("typeof TextEncoder").AsString().Should().Be("function");
        engine.Evaluate("typeof TextDecoder").AsString().Should().Be("function");
        engine.Evaluate("new TextEncoder().encoding").AsString().Should().Be("utf-8");
        engine.Evaluate("new TextDecoder().encoding").AsString().Should().Be("utf-8");
    }

    [TestMethod]
    public void TextEncoder_encodes_utf8_to_a_uint8array()
    {
        var (engine, _) = NewSession(Html);
        engine.Evaluate("new TextEncoder().encode('hi') instanceof Uint8Array").AsBoolean().Should().BeTrue();
        engine.Evaluate("Array.from(new TextEncoder().encode('hi')).join(',')").AsString().Should().Be("104,105");
        // Multi-byte: '€' is E2 82 AC in UTF-8.
        engine.Evaluate("Array.from(new TextEncoder().encode('€')).join(',')").AsString().Should().Be("226,130,172");
    }

    [TestMethod]
    public void TextDecoder_round_trips_utf8_bytes()
    {
        var (engine, _) = NewSession(Html);
        engine.Evaluate(
            "(function(){ var b = new TextEncoder().encode('héllo €');" +
            "return new TextDecoder().decode(b); })()")
            .AsString().Should().Be("héllo €");
        // Decode accepts the underlying ArrayBuffer too.
        engine.Evaluate(
            "(function(){ var b = new TextEncoder().encode('abc');" +
            "return new TextDecoder().decode(b.buffer); })()")
            .AsString().Should().Be("abc");
    }

    [TestMethod]
    public void URL_is_a_global_constructor_with_parsed_components()
    {
        var (engine, _) = NewSession(Html);
        // Angular constructs `new URL(...)` during boot; without the global it
        // threw "ReferenceError: URL is not defined".
        engine.Evaluate("typeof URL").AsString().Should().Be("function");
        engine.Evaluate("new URL('https://a.example.com:8443/p/q?x=1&y=2#frag').protocol")
            .AsString().Should().Be("https:");
        engine.Evaluate("new URL('https://a.example.com:8443/p/q?x=1#frag').host")
            .AsString().Should().Be("a.example.com:8443");
        engine.Evaluate("new URL('https://a.example.com/p/q?x=1#frag').pathname")
            .AsString().Should().Be("/p/q");
        engine.Evaluate("new URL('https://a.example.com/p?x=1#frag').search")
            .AsString().Should().Be("?x=1");
        engine.Evaluate("new URL('https://a.example.com/p#frag').hash")
            .AsString().Should().Be("#frag");
    }

    [TestMethod]
    public void URL_resolves_against_a_base()
    {
        var (engine, _) = NewSession(Html);
        engine.Evaluate("new URL('../c', 'https://h.example.com/a/b/').href")
            .AsString().Should().Be("https://h.example.com/a/c");
        engine.Evaluate("new URL('/abs', 'https://h.example.com/a/b').pathname")
            .AsString().Should().Be("/abs");
    }

    [TestMethod]
    public void URLSearchParams_reads_and_mutates()
    {
        var (engine, _) = NewSession(Html);
        engine.Evaluate("typeof URLSearchParams").AsString().Should().Be("function");
        engine.Evaluate("new URLSearchParams('a=1&b=2').get('b')").AsString().Should().Be("2");
        engine.Evaluate(
            "(function(){ var p = new URLSearchParams('a=1'); p.append('a','2');" +
            "return p.getAll('a').join(','); })()").AsString().Should().Be("1,2");
        engine.Evaluate(
            "(function(){ var u = new URL('https://h.example.com/?a=1');" +
            "u.searchParams.set('a','9'); u.searchParams.append('b','x');" +
            "return u.search; })()").AsString().Should().Be("?a=9&b=x");
    }

    [TestMethod]
    public void Named_class_expression_static_field_can_construct_via_inner_name()
    {
        // The exact angular.dev pattern that crashed the Starling engine:
        //   Un = class e extends Error { static IDLE = new e("IDLE"); }
        // Jint handles this natively; this guards the cross-engine contract.
        var (engine, _) = NewSession(Html);
        engine.Evaluate(@"
            globalThis.__C = class e extends Error {
              constructor(t){ super(t); }
              static IDLE = new e('IDLE');
            };
        ");
        engine.Evaluate("__C.IDLE instanceof __C").AsBoolean().Should().BeTrue();
        engine.Evaluate("__C.IDLE.message").AsString().Should().Be("IDLE");
    }

    private JintBackendContext? _lastCtx;

    private (global::Jint.Engine Engine, Document Doc) NewSession(
        string html, string baseUrl = "https://www.example.com/", int viewportWidth = 800, int viewportHeight = 600)
    {
        var doc = HtmlParser.Parse(html);
        var url = global::Starling.Url.UrlParser.Parse(baseUrl).Value;
        var engine = new global::Jint.Engine();
        var http = new Starling.Net.StarlingHttpClient();
        var ctx = new JintBackendContext(
            engine: engine,
            document: doc,
            baseUrl: url,
            http: http,
            loggerFactory: NullLoggerFactory.Instance,
            loop: new WebEventLoop(),
            layoutHost: null,
            fetch: (_, _) => System.Threading.Tasks.Task.FromResult<string?>(null))
        {
            ViewportWidth = viewportWidth,
            ViewportHeight = viewportHeight,
        };
        JintBindings.InstallAll(ctx);
        _lastCtx = ctx;
        return (engine, doc);
    }
}
