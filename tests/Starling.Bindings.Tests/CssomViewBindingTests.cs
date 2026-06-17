using AwesomeAssertions;
using Starling.Dom;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;
using Starling.Spec;

namespace Starling.Bindings.Tests;

/// <summary>
/// JS-binding conformance for CSSOM View geometry on the Starling JS engine:
/// <c>getBoundingClientRect</c> returns a spec-shaped <c>DOMRect</c> (eight
/// numeric members + <c>toJSON</c>), <c>getClientRects</c> returns a list, the
/// box-metric accessors are exposed and numeric, and <c>matchMedia</c> returns
/// a MediaQueryList. With no layout host these read the spec-permitted zeros of
/// a never-laid-out document; the interface shape is the conformance surface.
/// </summary>
[TestClass]
public sealed class CssomViewBindingTests
{
    private static JsRuntime NewSession()
    {
        var doc = new Document();
        var html = doc.CreateElement("html");
        var body = doc.CreateElement("body");
        var div = doc.CreateElement("div");
        div.SetAttribute("id", "d");
        div.AppendChild(doc.CreateTextNode("x"));
        doc.AppendChild(html);
        html.AppendChild(body);
        body.AppendChild(div);
        var runtime = new JsRuntime();
        WindowBinding.Install(runtime, doc, new WindowInstallOptions());
        return runtime;
    }

    private static JsValue Eval(JsRuntime runtime, string source)
    {
        var program = new JsParser(source).ParseProgram();
        var chunk = JsCompiler.Compile(program);
        new JsVm(runtime).Run(chunk);
        return runtime.GetGlobal("result");
    }

    [Spec("cssom-view", "https://drafts.csswg.org/cssom-view/#dom-element-getboundingclientrect", section: "6")]
    [SpecFact]
    public void GetBoundingClientRect_returns_a_DOMRect_shape()
    {
        var rt = NewSession();
        Eval(rt, "result = (function(){var r=document.getElementById('d').getBoundingClientRect();" +
            "return ['x','y','width','height','top','right','bottom','left']" +
            ".every(function(k){return typeof r[k]==='number';});})();")
            .AsBool.Should().BeTrue();
        Eval(rt, "result = typeof document.getElementById('d').getBoundingClientRect().toJSON().width;")
            .AsString.Should().Be("number");
    }

    [Spec("cssom-view", "https://drafts.csswg.org/cssom-view/#dom-element-getclientrects", section: "6")]
    [SpecFact]
    public void GetClientRects_returns_a_list()
    {
        var rt = NewSession();
        Eval(rt, "result = typeof document.getElementById('d').getClientRects().length;")
            .AsString.Should().Be("number");
    }

    [Spec("cssom-view", "https://drafts.csswg.org/cssom-view/#dom-element-clientwidth", section: "7")]
    [SpecFact]
    public void Box_metric_accessors_are_exposed_and_numeric()
    {
        var rt = NewSession();
        Eval(rt, "result = (function(){var d=document.getElementById('d');" +
            "return ['clientWidth','clientHeight','offsetWidth','offsetHeight'," +
            "'scrollWidth','scrollHeight','scrollTop','scrollLeft']" +
            ".every(function(k){return typeof d[k]==='number';});})();")
            .AsBool.Should().BeTrue();
    }

    [Spec("cssom-view", "https://drafts.csswg.org/cssom-view/#dom-window-matchmedia", section: "4.2")]
    [SpecFact]
    public void MatchMedia_returns_a_MediaQueryList()
    {
        var rt = NewSession();
        Eval(rt, "result = typeof matchMedia('(min-width: 100px)').matches;").AsString.Should().Be("boolean");
        Eval(rt, "result = matchMedia('(min-width: 100px)').media;").AsString.Should().Be("(min-width: 100px)");
    }

    // ---- WP3: the scroll surface over a host with real scroll state --------
    //
    // Geometry (document space at scroll zero, all CSS px):
    //   root scroller: 800x600 viewport over an 800x2000 page
    //   #outer at (0,0):   100x100 port, 100x1000 scrollable overflow
    //   #inner at (0,300): 100x100 port, 300x1000 scrollable overflow
    //   #t     at (0,550): 80x10 — i.e. 250px below #inner's content origin
    private static (JsRuntime Runtime, FakeScrollHost Host) NewScrollSession()
    {
        var doc = new Document();
        var html = doc.CreateElement("html");
        var body = doc.CreateElement("body");
        var outer = doc.CreateElement("div");
        outer.SetAttribute("id", "outer");
        var inner = doc.CreateElement("div");
        inner.SetAttribute("id", "inner");
        var target = doc.CreateElement("p");
        target.SetAttribute("id", "t");
        doc.AppendChild(html);
        html.AppendChild(body);
        body.AppendChild(outer);
        outer.AppendChild(inner);
        inner.AppendChild(target);

        var host = new FakeScrollHost();
        host.Rects[outer] = new LayoutRect(0, 0, 100, 100);
        host.Rects[inner] = new LayoutRect(0, 300, 100, 100);
        host.Rects[target] = new LayoutRect(0, 550, 80, 10);
        host.Scrollers[outer] = new FakeScrollHost.Scroller { PortW = 100, PortH = 100, OverW = 100, OverH = 1000 };
        host.Scrollers[inner] = new FakeScrollHost.Scroller { PortW = 100, PortH = 100, OverW = 300, OverH = 1000 };
        host.Root = new FakeScrollHost.Scroller { PortW = 800, PortH = 600, OverW = 800, OverH = 2000 };

        var runtime = new JsRuntime();
        WindowBinding.Install(runtime, doc,
            new WindowInstallOptions(InnerWidth: 800, InnerHeight: 600, LayoutHost: host));
        return (runtime, host);
    }

    [Spec("cssom-view", "https://drafts.csswg.org/cssom-view/#dom-element-scrolltop", section: "7")]
    [SpecFact]
    public void ScrollTop_write_clamps_to_max_and_reads_back()
    {
        var (rt, _) = NewScrollSession();
        // max scrollTop = overflow 1000 - port 100 = 900.
        Eval(rt, "var d=document.getElementById('inner'); d.scrollTop = 9999; result = d.scrollTop;")
            .AsNumber.Should().Be(900);
        Eval(rt, "d.scrollTop = -5; result = d.scrollTop;").AsNumber.Should().Be(0);
        // CSSOM: non-finite values normalize to 0.
        Eval(rt, "d.scrollTop = 50; d.scrollTop = NaN; result = d.scrollTop;").AsNumber.Should().Be(0);
        // max scrollLeft = overflow 300 - port 100 = 200; the other axis holds.
        Eval(rt, "d.scrollTop = 40; d.scrollLeft = 9999; result = '' + d.scrollLeft + ',' + d.scrollTop;")
            .AsString.Should().Be("200,40");
    }

    [Spec("cssom-view", "https://drafts.csswg.org/cssom-view/#dom-element-scrolltop", section: "7")]
    [SpecFact]
    public void ScrollTop_write_on_a_non_scroller_is_a_no_op()
    {
        var (rt, _) = NewScrollSession();
        Eval(rt, "var t=document.getElementById('t'); t.scrollTop = 50; result = t.scrollTop;")
            .AsNumber.Should().Be(0);
    }

    [Spec("cssom-view", "https://drafts.csswg.org/cssom-view/#dom-element-scrollwidth", section: "7")]
    [SpecFact]
    public void ScrollWidth_and_scrollHeight_report_scrollable_overflow_not_offset_size()
    {
        var (rt, _) = NewScrollSession();
        // #inner's border box is 100x100, but its scrolling area is 300x1000.
        Eval(rt, "var d=document.getElementById('inner'); result = '' + d.scrollWidth + ',' + d.scrollHeight;")
            .AsString.Should().Be("300,1000");
        // clientWidth/clientHeight stay the scrollport (padding box).
        Eval(rt, "result = '' + d.clientWidth + ',' + d.clientHeight;").AsString.Should().Be("100,100");
        // A non-scroller falls back to its own box size (nothing overflows).
        Eval(rt, "var t=document.getElementById('t'); result = '' + t.scrollWidth + ',' + t.scrollHeight;")
            .AsString.Should().Be("80,10");
        // The root element reports the document scrolling area.
        Eval(rt, "var r=document.documentElement; result = '' + r.scrollWidth + ',' + r.scrollHeight;")
            .AsString.Should().Be("800,2000");
    }

    [Spec("cssom-view", "https://drafts.csswg.org/cssom-view/#dom-element-scrollto", section: "7")]
    [SpecFact]
    public void ScrollTo_and_scrollBy_move_the_element_and_smooth_is_instant()
    {
        var (rt, _) = NewScrollSession();
        // Options form with behavior:'smooth' — accepted, applied instantly.
        Eval(rt, "var d=document.getElementById('inner');" +
            "d.scrollTo({top: 150, behavior: 'smooth'}); result = d.scrollTop;")
            .AsNumber.Should().Be(150);
        // Two-arg form; omitted options members keep the current offset.
        Eval(rt, "d.scrollTo(30, 60); result = '' + d.scrollLeft + ',' + d.scrollTop;")
            .AsString.Should().Be("30,60");
        Eval(rt, "d.scrollTo({left: 10}); result = '' + d.scrollLeft + ',' + d.scrollTop;")
            .AsString.Should().Be("10,60");
        // scrollBy accumulates (and clamps).
        Eval(rt, "d.scrollBy(0, 50); d.scrollBy({top: 25, behavior: 'smooth'});" +
            "result = '' + d.scrollLeft + ',' + d.scrollTop;")
            .AsString.Should().Be("10,135");
        Eval(rt, "d.scrollBy(0, 99999); result = d.scrollTop;").AsNumber.Should().Be(900);
    }

    [Spec("cssom-view", "https://drafts.csswg.org/cssom-view/#dom-element-scrollintoview", section: "7")]
    [SpecFact]
    public void ScrollIntoView_walks_two_nested_scrollers_and_the_viewport()
    {
        var (rt, _) = NewScrollSession();
        // block:'start' (default): #inner scrolls #t's 250px content offset to
        // its top; #outer scrolls #t's adjusted position (550-250=300) to its
        // top; the viewport needs nothing (550-250-300=0 is already visible).
        Eval(rt, "document.getElementById('t').scrollIntoView();" +
            "result = '' + document.getElementById('inner').scrollTop" +
            " + ',' + document.getElementById('outer').scrollTop" +
            " + ',' + window.scrollY;")
            .AsString.Should().Be("250,300,0");
    }

    [Spec("cssom-view", "https://drafts.csswg.org/cssom-view/#dom-element-scrollintoview", section: "7")]
    [SpecFact]
    public void ScrollIntoView_honors_block_end_and_the_boolean_form()
    {
        var (rt, _) = NewScrollSession();
        // block:'end': #inner aligns #t's bottom to the scrollport bottom
        // (250+10-100=160); #outer then sees #t at 550-160=390 and aligns its
        // bottom too (390+10-100=300).
        Eval(rt, "document.getElementById('t').scrollIntoView({block: 'end', behavior: 'smooth'});" +
            "result = '' + document.getElementById('inner').scrollTop" +
            " + ',' + document.getElementById('outer').scrollTop;")
            .AsString.Should().Be("160,300");
        // scrollIntoView(false) is the legacy spelling of block:'end'.
        var (rt2, _) = NewScrollSession();
        Eval(rt2, "document.getElementById('t').scrollIntoView(false);" +
            "result = document.getElementById('inner').scrollTop;")
            .AsNumber.Should().Be(160);
    }

    [Spec("cssom-view", "https://drafts.csswg.org/cssom-view/#dom-window-scroll", section: "4")]
    [SpecFact]
    public void Window_scrollTo_scrollBy_write_and_scrollX_scrollY_read_the_root_entry()
    {
        var (rt, _) = NewScrollSession();
        Eval(rt, "window.scrollTo(0, 500); result = '' + window.scrollY + ',' + window.pageYOffset;")
            .AsString.Should().Be("500,500");
        // Clamps to the page extent: 2000 - 600 viewport = 1400.
        Eval(rt, "window.scrollBy(0, 5000); result = window.scrollY;").AsNumber.Should().Be(1400);
        // Options form, smooth treated as instant.
        Eval(rt, "window.scrollTo({top: 10, behavior: 'smooth'}); result = window.scrollY;")
            .AsNumber.Should().Be(10);
        Eval(rt, "result = '' + window.scrollX + ',' + window.pageXOffset;").AsString.Should().Be("0,0");
        // The root element's scrollTop is the viewport scroll (standards mode).
        Eval(rt, "document.documentElement.scrollTop = 300; result = window.scrollY;")
            .AsNumber.Should().Be(300);
        Eval(rt, "result = document.documentElement.scrollTop;").AsNumber.Should().Be(300);
    }

    /// <summary>ILayoutHost fake with real scroll-state semantics: per-element
    /// scrollers with clamp-on-write offsets, plus a root entry — the same
    /// contract BoxLayoutHost implements over the engine's ScrollStateStore.
    /// Rects are document-space at scroll zero, like the real host.</summary>
    private sealed class FakeScrollHost : ILayoutHost
    {
        public sealed class Scroller
        {
            public double X, Y, PortW, PortH, OverW, OverH;
        }

        public readonly Dictionary<Element, LayoutRect> Rects = new();
        public readonly Dictionary<Element, Scroller> Scrollers = new();
        public Scroller Root = new();

        public bool TryGetBoundingClientRect(Element element, out LayoutRect rect)
            => Rects.TryGetValue(element, out rect);

        public bool TryGetOffsetMetrics(Element element, out OffsetMetrics metrics)
        {
            if (Rects.TryGetValue(element, out var r))
            {
                metrics = new OffsetMetrics(r.Width, r.Height, r.Y, r.X, r.Width, r.Height);
                return true;
            }
            metrics = default;
            return false;
        }

        public string GetComputedProperty(Element element, string propertyName) => string.Empty;

        public bool MatchMedia(string query) => false;

        public bool TryGetScrollMetrics(Element element, out ScrollMetrics metrics)
        {
            if (Scrollers.TryGetValue(element, out var s))
            {
                metrics = new ScrollMetrics(s.X, s.Y, s.OverW, s.OverH, s.PortW, s.PortH);
                return true;
            }
            metrics = default;
            return false;
        }

        public ScrollMetrics GetRootScrollMetrics()
            => new(Root.X, Root.Y, Root.OverW, Root.OverH, Root.PortW, Root.PortH);

        public void SetScrollOffset(Element element, double x, double y)
        {
            if (Scrollers.TryGetValue(element, out var s))
            {
                Clamp(s, x, y);
            }
        }

        public void SetRootScrollOffset(double x, double y) => Clamp(Root, x, y);

        private static void Clamp(Scroller s, double x, double y)
        {
            s.X = Math.Clamp(x, 0, Math.Max(0, s.OverW - s.PortW));
            s.Y = Math.Clamp(y, 0, Math.Max(0, s.OverH - s.PortH));
        }
    }
}
