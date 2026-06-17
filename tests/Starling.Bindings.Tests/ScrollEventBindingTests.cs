using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Dom;
using Starling.Html;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;
using Starling.Layout;
using Starling.Layout.Scroll;

namespace Starling.Bindings.Tests;

/// <summary>
/// WP4 of browser-plan/scroll-model.md on the Starling JS engine: the
/// store-to-JS scroll event chain. Offset writes only flag the
/// <see cref="ScrollStateStore"/>; the frame pump drains the flagged set once
/// per frame and <see cref="ScrollEventDispatcher"/> dispatches the coalesced
/// <c>scroll</c> events on the host targets, where the bridged JS listeners
/// observe them. Mirrors the AnimationEventBindingTests harness; fixtures are
/// laid out for real so the store carries measured geometry.
/// </summary>
[TestClass]
public sealed class ScrollEventBindingTests
{
    private const string Fixture = """
        <body>
          <div id=s style="overflow:auto;width:200px;height:100px">
            <div style="width:600px;height:500px"></div>
          </div>
          <div style="height:2000px"></div>
        </body>
        """;

    private static (JsRuntime Runtime, Document Doc, ScrollStateStore Store) BuildEnv(string html = Fixture)
    {
        var doc = HtmlParser.Parse(html);
        var store = new ScrollStateStore();
        var engine = new LayoutEngine(new StyleEngine()) { ScrollState = store };
        engine.LayoutDocument(doc, new Size(800, 600));
        var runtime = new JsRuntime();
        WindowBinding.Install(runtime, doc);
        return (runtime, doc, store);
    }

    private static JsValue Eval(JsRuntime runtime, string source)
    {
        var program = new JsParser(source).ParseProgram();
        var chunk = JsCompiler.Compile(program);
        new JsVm(runtime).Run(chunk);
        return runtime.GetGlobal("result");
    }

    /// <summary>One frame-pump drain: the exact drain-then-dispatch sequence
    /// PageScripting.RunScrollSteps runs once per frame.</summary>
    private static int PumpScrollSteps(
        JsRuntime runtime, Document doc, ScrollStateStore store, List<Element> scratch)
    {
        if (!store.HasPendingEvents)
        {
            return 0;
        }

        store.DrainPendingEventTargets(scratch, out var documentScrolled);
        return ScrollEventDispatcher.Dispatch(runtime.Realm, doc, scratch, documentScrolled);
    }

    [TestMethod]
    public void N_writes_in_one_frame_dispatch_exactly_one_scroll_event()
    {
        var (runtime, doc, store) = BuildEnv();
        Eval(runtime, """
            globalThis.log = [];
            var s = document.getElementById('s');
            s.addEventListener('scroll', function (e) {
                log.push('scroll:' + e.bubbles + ':' + e.cancelable + ':'
                    + e.isTrusted + ':' + (e.target === s));
            });
            result = 'ready';
        """);

        var el = doc.GetElementById("s")!;
        var scratch = new List<Element>();
        store.Write(el, 0, 10);
        store.Write(el, 0, 20);
        store.Write(el, 0, 30);

        PumpScrollSteps(runtime, doc, store, scratch).Should().Be(1,
            "three writes in one frame coalesce to one scroll event");
        // Nothing re-flagged: the next frame's drain dispatches nothing.
        PumpScrollSteps(runtime, doc, store, scratch).Should().Be(0);

        Eval(runtime, "result = log.join('|');").AsString
            .Should().Be("scroll:false:false:true:true");
    }

    [TestMethod]
    public void Element_scroll_does_not_reach_ancestor_listeners()
    {
        var (runtime, doc, store) = BuildEnv();
        Eval(runtime, """
            globalThis.log = [];
            document.getElementById('s').addEventListener('scroll', function () {
                log.push('element');
            });
            document.body.addEventListener('scroll', function () { log.push('body'); });
            document.addEventListener('scroll', function () { log.push('document'); });
            window.addEventListener('scroll', function () { log.push('window'); });
            result = 'ready';
        """);

        store.Write(doc.GetElementById("s")!, 0, 40);
        PumpScrollSteps(runtime, doc, store, new List<Element>()).Should().Be(1);

        Eval(runtime, "result = log.join('|');").AsString
            .Should().Be("element", "scroll on an element scroller does not bubble");
    }

    [TestMethod]
    public void Document_scroll_fires_on_document_and_reaches_window()
    {
        var (runtime, doc, store) = BuildEnv();
        Eval(runtime, """
            globalThis.log = [];
            document.addEventListener('scroll', function (e) {
                log.push('document:' + e.bubbles + ':' + e.cancelable + ':'
                    + e.isTrusted + ':' + (e.target === document));
            });
            window.addEventListener('scroll', function (e) {
                log.push('window:' + e.isTrusted);
            });
            result = 'ready';
        """);

        // The 2000px body filler gives the root scroller real overflow.
        store.WriteRoot(0, 40);
        PumpScrollSteps(runtime, doc, store, new List<Element>());

        Eval(runtime, "result = log.join('|');").AsString
            .Should().Be("document:true:false:true:true|window:true");
    }

    [TestMethod]
    public void Onscroll_idl_attribute_receives_the_event()
    {
        var (runtime, doc, store) = BuildEnv();
        Eval(runtime, """
            globalThis.log = [];
            document.getElementById('s').onscroll = function (e) {
                log.push('onscroll:' + e.type);
            };
            result = 'ready';
        """);

        store.Write(doc.GetElementById("s")!, 0, 25);
        PumpScrollSteps(runtime, doc, store, new List<Element>());

        Eval(runtime, "result = log.join('|');").AsString.Should().Be("onscroll:scroll");
    }

    [TestMethod]
    public void Write_from_inside_a_scroll_listener_fires_next_frame_not_recursively()
    {
        var (runtime, doc, store) = BuildEnv();
        Eval(runtime, """
            globalThis.count = 0;
            document.getElementById('s').addEventListener('scroll', function () {
                count = count + 1;
            });
            result = 'ready';
        """);

        var el = doc.GetElementById("s")!;
        // Host-side stand-in for a JS scrollTop write (the WP3 setter routes to
        // the same store Write): the first scroll event writes the offset
        // again from inside its own dispatch.
        var rewrites = 0;
        el.AddEventListener("scroll", _ =>
        {
            if (rewrites++ == 0)
            {
                store.Write(el, 0, 99);
            }
        });

        var scratch = new List<Element>();
        store.Write(el, 0, 33);

        // Frame 1: exactly one event; the in-listener write re-flags the
        // element for the NEXT frame instead of recursing this drain.
        PumpScrollSteps(runtime, doc, store, scratch).Should().Be(1);
        Eval(runtime, "result = String(count);").AsString.Should().Be("1");
        store.HasPendingEvents.Should().BeTrue("the in-listener write lands in the next frame's drain");

        // Frame 2: the re-flag fires once more.
        PumpScrollSteps(runtime, doc, store, scratch).Should().Be(1);
        Eval(runtime, "result = String(count);").AsString.Should().Be("2");

        // Frame 3: bounded — no listener writes again, so nothing fires.
        PumpScrollSteps(runtime, doc, store, scratch).Should().Be(0);
        Eval(runtime, "result = String(count);").AsString.Should().Be("2");
    }
}
