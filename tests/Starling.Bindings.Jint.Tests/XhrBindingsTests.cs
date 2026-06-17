using AwesomeAssertions;
using Jint;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Html;
using Starling.Loop;

namespace Starling.Bindings.Jint.Tests;

/// <summary>
/// J3c — XMLHttpRequest bindings. Offline-only: synchronous shape (constructor,
/// constants, open/setRequestHeader, readyState transitions) plus a full
/// end-to-end drive over a <c>data:</c> URL (which XHR resolves locally, so no
/// network is touched), settled through the simulated event-loop pump exactly
/// like the session's <c>PumpOnce</c> would.
/// </summary>
[TestClass]
public sealed class XhrBindingsTests
{
    [TestMethod]
    public void Constructor_and_constants_present()
    {
        var ctx = NewContext();
        XhrBinding.Install(ctx);

        ctx.Engine.Evaluate("typeof XMLHttpRequest").AsString().Should().Be("function");
        ctx.Engine.Evaluate("XMLHttpRequest.name").AsString().Should().Be("XMLHttpRequest");

        // Constants on both the constructor and instances (Web-IDL).
        ctx.Engine.Evaluate("XMLHttpRequest.UNSENT").AsNumber().Should().Be(0);
        ctx.Engine.Evaluate("XMLHttpRequest.OPENED").AsNumber().Should().Be(1);
        ctx.Engine.Evaluate("XMLHttpRequest.HEADERS_RECEIVED").AsNumber().Should().Be(2);
        ctx.Engine.Evaluate("XMLHttpRequest.LOADING").AsNumber().Should().Be(3);
        ctx.Engine.Evaluate("XMLHttpRequest.DONE").AsNumber().Should().Be(4);

        ctx.Engine.Evaluate("new XMLHttpRequest().DONE").AsNumber().Should().Be(4);
        ctx.Engine.Evaluate("new XMLHttpRequest() instanceof XMLHttpRequest").AsBoolean().Should().BeTrue();
    }

    [TestMethod]
    public void Initial_readyState_is_unsent_and_open_moves_to_opened()
    {
        var ctx = NewContext();
        XhrBinding.Install(ctx);

        ctx.Engine.Execute("var x = new XMLHttpRequest();");
        ctx.Engine.Evaluate("x.readyState").AsNumber().Should().Be(0); // UNSENT

        ctx.Engine.Execute("x.open('GET', 'data:,hello');");
        ctx.Engine.Evaluate("x.readyState").AsNumber().Should().Be(1); // OPENED
        ctx.Engine.Evaluate("x.status").AsNumber().Should().Be(0);
    }

    [TestMethod]
    public void Open_fires_readystatechange_to_opened()
    {
        var ctx = NewContext();
        XhrBinding.Install(ctx);

        ctx.Engine.Execute("""
            var states = [];
            var x = new XMLHttpRequest();
            x.onreadystatechange = function () { states.push(x.readyState); };
            x.open('GET', 'data:,hi');
        """);

        ctx.Engine.Evaluate("states.join(',')").AsString().Should().Be("1");
    }

    [TestMethod]
    public void SetRequestHeader_requires_opened_state()
    {
        var ctx = NewContext();
        XhrBinding.Install(ctx);

        ctx.Engine.Execute("var x = new XMLHttpRequest();");

        // Before open() → throws.
        var beforeOpen = ctx.Engine.Evaluate("""
            (function () {
                try { x.setRequestHeader('X-Test', '1'); return 'no-throw'; }
                catch (e) { return 'threw'; }
            })()
        """).AsString();
        beforeOpen.Should().Be("threw");

        // After open() → accepted.
        var afterOpen = ctx.Engine.Evaluate("""
            (function () {
                x.open('GET', 'data:,hi');
                try { x.setRequestHeader('X-Test', '1'); return 'ok'; }
                catch (e) { return 'threw'; }
            })()
        """).AsString();
        afterOpen.Should().Be("ok");
    }

    [TestMethod]
    public void Synchronous_xhr_is_rejected()
    {
        var ctx = NewContext();
        XhrBinding.Install(ctx);

        var result = ctx.Engine.Evaluate("""
            (function () {
                var x = new XMLHttpRequest();
                try { x.open('GET', 'data:,hi', false); return 'no-throw'; }
                catch (e) { return 'threw'; }
            })()
        """).AsString();
        result.Should().Be("threw");
    }

    [TestMethod]
    public void Send_drives_readyState_to_done_and_responseText_over_data_url()
    {
        var ctx = NewContext();
        XhrBinding.Install(ctx);

        ctx.Engine.Execute("""
            var states = [];
            var loaded = false;
            var x = new XMLHttpRequest();
            x.onreadystatechange = function () { states.push(x.readyState); };
            x.onload = function () { loaded = true; };
            x.open('GET', 'data:text/plain,Hello%20XHR');
            x.send();
        """);

        PumpUntilDone(ctx);

        ctx.Engine.Evaluate("x.readyState").AsNumber().Should().Be(4); // DONE
        ctx.Engine.Evaluate("x.status").AsNumber().Should().Be(200);
        ctx.Engine.Evaluate("x.responseText").AsString().Should().Be("Hello XHR");
        ctx.Engine.Evaluate("x.response").AsString().Should().Be("Hello XHR");
        ctx.Engine.Evaluate("loaded").AsBoolean().Should().BeTrue();
        // open()=1, then HEADERS_RECEIVED=2, LOADING=3, DONE=4 on completion.
        ctx.Engine.Evaluate("states.join(',')").AsString().Should().Be("1,2,3,4");
    }

    [TestMethod]
    public void AddEventListener_load_fires_on_completion()
    {
        var ctx = NewContext();
        XhrBinding.Install(ctx);

        ctx.Engine.Execute("""
            var hits = 0;
            var x = new XMLHttpRequest();
            x.addEventListener('load', function () { hits++; });
            x.open('GET', 'data:,payload');
            x.send();
        """);

        PumpUntilDone(ctx);

        ctx.Engine.Evaluate("hits").AsNumber().Should().Be(1);
        ctx.Engine.Evaluate("x.responseText").AsString().Should().Be("payload");
    }

    [TestMethod]
    public void Response_json_parses_the_body()
    {
        var ctx = NewContext();
        XhrBinding.Install(ctx);

        ctx.Engine.Execute("""
            var x = new XMLHttpRequest();
            x.responseType = 'json';
            x.open('GET', 'data:application/json,{"a":42}');
            x.send();
        """);

        PumpUntilDone(ctx);

        ctx.Engine.Evaluate("x.response.a").AsNumber().Should().Be(42);
    }

    [TestMethod]
    public void Base64_data_url_decodes_to_responseText()
    {
        var ctx = NewContext();
        XhrBinding.Install(ctx);

        // base64 of "ok!" = b2sh
        ctx.Engine.Execute("""
            var x = new XMLHttpRequest();
            x.open('GET', 'data:text/plain;base64,b2sh');
            x.send();
        """);

        PumpUntilDone(ctx);

        ctx.Engine.Evaluate("x.responseText").AsString().Should().Be("ok!");
    }

    [TestMethod]
    public void Abort_after_open_marks_done_and_fires_abort()
    {
        var ctx = NewContext();
        XhrBinding.Install(ctx);

        ctx.Engine.Execute("""
            var aborted = false;
            var x = new XMLHttpRequest();
            x.onabort = function () { aborted = true; };
            x.open('GET', 'data:,x');
            x.send();
            x.abort();
        """);

        ctx.Engine.Evaluate("aborted").AsBoolean().Should().BeTrue();
        ctx.Engine.Evaluate("x.readyState").AsNumber().Should().Be(4);
    }

    [TestMethod]
    public void Timeout_and_withCredentials_round_trip()
    {
        var ctx = NewContext();
        XhrBinding.Install(ctx);

        ctx.Engine.Execute("""
            var x = new XMLHttpRequest();
            x.timeout = 1500;
            x.withCredentials = true;
        """);

        ctx.Engine.Evaluate("x.timeout").AsNumber().Should().Be(1500);
        ctx.Engine.Evaluate("x.withCredentials").AsBoolean().Should().BeTrue();
    }

    // ---- helpers ----

    // Mirrors JintScriptSession.PumpOnce: drain promise jobs + the posted
    // completion queue, advance the loop while any timer work is pending. The XHR
    // completion settles through ctx.Post (drained here via DrainPosted).
    private static void PumpUntilDone(JintBackendContext ctx)
    {
        for (var i = 0; i < 50; i++)
        {
            ctx.Engine.Advanced.ProcessTasks();
            if (ctx.DrainPosted())
            {
                ctx.Engine.Advanced.ProcessTasks();
            }

            if (ctx.Loop.PendingTimerCount == 0 && ctx.Loop.PendingAnimationFrameCount == 0 && !ctx.HasPosted)
            {
                break;
            }

            if (ctx.Loop.PendingTimerCount > 0 || ctx.Loop.PendingAnimationFrameCount > 0)
            {
                ctx.Loop.AdvanceBy(50);
            }
        }
    }

    private static JintBackendContext NewContext()
    {
        var doc = HtmlParser.Parse("<!doctype html><html><body></body></html>");
        var baseUrl = global::Starling.Url.UrlParser.Parse("https://example.com/app/").Value;
        var engine = new global::Jint.Engine(o => o.AllowClr());
        var http = new Starling.Net.StarlingHttpClient();
        return new JintBackendContext(
            engine: engine,
            document: doc,
            baseUrl: baseUrl,
            http: http,
            loggerFactory: NullLoggerFactory.Instance,
            loop: new WebEventLoop(),
            layoutHost: null,
            fetch: (_, _) => System.Threading.Tasks.Task.FromResult<string?>(null));
    }
}
