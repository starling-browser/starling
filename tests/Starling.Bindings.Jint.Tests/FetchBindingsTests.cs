using AwesomeAssertions;
using Jint;
using Jint.Native;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Html;
using Starling.Loop;

namespace Starling.Bindings.Jint.Tests;

/// <summary>
/// J3b — fetch / Request / Response / Headers / AbortController bindings.
///
/// The end-to-end fetch test uses a <c>data:</c> URL so it runs fully offline
/// (no network transport needed): <c>fetch(dataUrl).then(r =&gt; r.text())</c>
/// resolves through the same cross-thread completion pump real HTTP uses
/// (completion queue → re-arming loop timer → resolve on the JS thread). The
/// remaining tests assert the synchronous Web-IDL shape of the constructors.
/// </summary>
[TestClass]
public sealed class FetchBindingsTests
{
    [TestMethod]
    public void Fetch_data_url_resolves_with_response_text_and_shape()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        // data:text/plain content, base64-encoded "hello jint".
        const string dataUrl = "data:text/plain;base64,aGVsbG8gamludA==";
        ctx.Engine.Execute($$"""
            globalThis.__out = {};
            fetch("{{dataUrl}}").then(r => {
                __out.ok = r.ok;
                __out.status = r.status;
                __out.type = r.type;
                __out.ctype = r.headers.get('content-type');
                __out.isResponse = (r instanceof Response);
                return r.text();
            }).then(t => { __out.text = t; __out.done = true; })
              .catch(e => { __out.error = String(e && e.message || e); __out.done = true; });
            """);

        PumpUntil(ctx, () => IsDone(ctx));

        var error = GetProp(ctx, "error");
        error.IsUndefined().Should().BeTrue($"fetch should not reject (error: {(error.IsUndefined() ? "" : error.ToString())})");
        GetProp(ctx, "text").AsString().Should().Be("hello jint");
        GetProp(ctx, "ok").AsBoolean().Should().BeTrue();
        GetProp(ctx, "status").AsNumber().Should().Be(200);
        GetProp(ctx, "type").AsString().Should().Be("basic");
        GetProp(ctx, "ctype").AsString().Should().Contain("text/plain");
        GetProp(ctx, "isResponse").AsBoolean().Should().BeTrue();
    }

    [TestMethod]
    public void Fetch_data_url_json_parses()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        const string dataUrl = "data:application/json,%7B%22a%22%3A1%2C%22b%22%3A%22x%22%7D"; // {"a":1,"b":"x"}
        ctx.Engine.Execute($$"""
            globalThis.__out = {};
            fetch("{{dataUrl}}").then(r => r.json()).then(j => {
                __out.a = j.a; __out.b = j.b; __out.done = true;
            }).catch(e => { __out.error = String(e); __out.done = true; });
            """);

        PumpUntil(ctx, () => IsDone(ctx));

        GetProp(ctx, "error").IsUndefined().Should().BeTrue();
        GetProp(ctx, "a").AsNumber().Should().Be(1);
        GetProp(ctx, "b").AsString().Should().Be("x");
    }

    [TestMethod]
    public void Fetch_is_a_function_and_returns_a_promise()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        ctx.Engine.Evaluate("typeof fetch").AsString().Should().Be("function");
        ctx.Engine.Evaluate("fetch('data:,') instanceof Promise").AsBoolean().Should().BeTrue();
    }

    [TestMethod]
    public void Headers_constructor_is_case_insensitive_and_supports_crud()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        ctx.Engine.Execute("""
            globalThis.h = new Headers({ 'Content-Type': 'text/html' });
            h.append('X-Test', 'a');
            h.append('x-test', 'b');
            """);

        ctx.Engine.Evaluate("h.get('content-type')").AsString().Should().Be("text/html");
        ctx.Engine.Evaluate("h.has('CONTENT-TYPE')").AsBoolean().Should().BeTrue();
        ctx.Engine.Evaluate("h.get('x-test')").AsString().Should().Be("a, b"); // combined
        ctx.Engine.Evaluate("(h.delete('content-type'), h.has('content-type'))").AsBoolean().Should().BeFalse();
        ctx.Engine.Evaluate("(h.set('x-test', 'z'), h.get('x-test'))").AsString().Should().Be("z");
    }

    [TestMethod]
    public void Headers_iterates_via_forEach_and_entries()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        ctx.Engine.Execute("""
            const h = new Headers();
            h.append('a', '1');
            h.append('b', '2');
            globalThis.viaForEach = [];
            h.forEach((v, k) => viaForEach.push(k + '=' + v));
            globalThis.viaEntries = [...h.entries()].map(p => p[0] + '=' + p[1]);
            globalThis.viaKeys = [...h.keys()].join(',');
            """);

        ctx.Engine.Evaluate("viaForEach.join('|')").AsString().Should().Be("a=1|b=2");
        ctx.Engine.Evaluate("viaEntries.join('|')").AsString().Should().Be("a=1|b=2");
        ctx.Engine.Evaluate("viaKeys").AsString().Should().Be("a,b");
    }

    [TestMethod]
    public void Request_constructor_parses_init_and_resolves_relative_url()
    {
        var ctx = NewContext("https://example.com/dir/page.html");
        JintBindings.InstallAll(ctx);

        ctx.Engine.Execute("""
            globalThis.req = new Request('../api', { method: 'post', headers: { 'X-A': '1' } });
            """);

        ctx.Engine.Evaluate("req.method").AsString().Should().Be("POST");
        ctx.Engine.Evaluate("req.url").AsString().Should().Be("https://example.com/api");
        ctx.Engine.Evaluate("req.headers.get('x-a')").AsString().Should().Be("1");
        ctx.Engine.Evaluate("req instanceof Request").AsBoolean().Should().BeTrue();
    }

    [TestMethod]
    public void Response_constructor_exposes_status_ok_and_body_methods()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        ctx.Engine.Execute("""
            globalThis.r = new Response('body text', { status: 201, statusText: 'Created' });
            globalThis.__out = {};
            r.text().then(t => { __out.text = t; __out.done = true; });
            """);

        PumpUntil(ctx, () => IsDone(ctx));

        ctx.Engine.Evaluate("r.status").AsNumber().Should().Be(201);
        ctx.Engine.Evaluate("r.statusText").AsString().Should().Be("Created");
        ctx.Engine.Evaluate("r.ok").AsBoolean().Should().BeTrue();
        GetProp(ctx, "text").AsString().Should().Be("body text");
    }

    [TestMethod]
    public void Response_clone_yields_independent_readable_body()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        ctx.Engine.Execute("""
            const r = new Response('shared');
            const c = r.clone();
            globalThis.__out = {};
            Promise.all([r.text(), c.text()]).then(([a, b]) => {
                __out.a = a; __out.b = b; __out.done = true;
            });
            """);

        PumpUntil(ctx, () => IsDone(ctx));

        GetProp(ctx, "a").AsString().Should().Be("shared");
        GetProp(ctx, "b").AsString().Should().Be("shared");
    }

    [TestMethod]
    public void Body_used_flag_rejects_double_read()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        ctx.Engine.Execute("""
            const r = new Response('once');
            globalThis.__out = {};
            r.text().then(() => {
                __out.usedAfterFirst = r.bodyUsed;
                return r.text();
            }).then(() => { __out.secondResolved = true; __out.done = true; })
              .catch(e => { __out.secondRejected = true; __out.done = true; });
            """);

        PumpUntil(ctx, () => IsDone(ctx));

        GetProp(ctx, "usedAfterFirst").AsBoolean().Should().BeTrue();
        GetProp(ctx, "secondRejected").AsBoolean().Should().BeTrue();
    }

    [TestMethod]
    public void AbortController_signal_and_abort_set_aborted_flag()
    {
        var ctx = NewContext();
        JintBindings.InstallAll(ctx);

        ctx.Engine.Execute("""
            globalThis.c = new AbortController();
            globalThis.beforeAbort = c.signal.aborted;
            c.abort();
            globalThis.afterAbort = c.signal.aborted;
            globalThis.isSignal = (c.signal instanceof AbortSignal);
            """);

        ctx.Engine.Evaluate("beforeAbort").AsBoolean().Should().BeFalse();
        ctx.Engine.Evaluate("afterAbort").AsBoolean().Should().BeTrue();
        ctx.Engine.Evaluate("isSignal").AsBoolean().Should().BeTrue();
    }

    // ---- helpers ----

    private static JsValue GetProp(JintBackendContext ctx, string name)
        => ctx.Engine.Evaluate($"globalThis.__out.{name}");

    private static bool IsDone(JintBackendContext ctx)
    {
        var v = GetProp(ctx, "done");
        return v.IsBoolean() && v.AsBoolean();
    }

    /// <summary>Mirror JintScriptSession.PumpOnce: drain promise jobs + the posted
    /// completion queue (ctx.DrainPosted), then advance the loop while timers/rAF
    /// are pending, until <paramref name="done"/> or a safety bound is hit.</summary>
    private static void PumpUntil(JintBackendContext ctx, Func<bool> done)
    {
        for (var i = 0; i < 1000; i++)
        {
            ctx.Engine.Advanced.ProcessTasks();
            if (ctx.DrainPosted())
            {
                ctx.Engine.Advanced.ProcessTasks();
            }

            if (done())
            {
                return;
            }

            if (ctx.Loop.PendingTimerCount > 0 || ctx.Loop.PendingAnimationFrameCount > 0)
            {
                ctx.Loop.AdvanceBy(1);
            }
            else if (!ctx.HasPosted)
            {
                System.Threading.Thread.Sleep(1); // wait for the background HTTP/data task
            }
        }
        ctx.Engine.Advanced.ProcessTasks();
    }

    private static JintBackendContext NewContext(string baseUrl = "https://example.test/")
    {
        var doc = HtmlParser.Parse("<!doctype html><html><body></body></html>");
        var url = global::Starling.Url.UrlParser.Parse(baseUrl).Value;
        var engine = new global::Jint.Engine();
        var http = new Starling.Net.StarlingHttpClient();
        return new JintBackendContext(
            engine: engine,
            document: doc,
            baseUrl: url,
            http: http,
            loggerFactory: NullLoggerFactory.Instance,
            loop: new WebEventLoop(),
            layoutHost: null,
            fetch: (_, _) => System.Threading.Tasks.Task.FromResult<string?>(null));
    }
}
