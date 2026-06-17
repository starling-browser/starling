using System.Net;
using System.Text;
using AwesomeAssertions;
using Starling.Dom;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;
using Starling.Net;
namespace Starling.Bindings.Tests;

/// <summary>
/// B5-3 fetch tests. Spins up a local <see cref="HttpListener"/> per test so
/// the live <see cref="StarlingHttpClient"/> path is exercised end-to-end.
/// </summary>
[TestClass]
public sealed class FetchTests
{
    [TestMethod]
    public async Task Fetch_text_resolves_with_body_string()
    {
        await using var server = await LocalServer.Start(ctx =>
        {
            using var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
            ctx.Response.StatusCode = 200;
            w.Write("hello");
        });
        var env = NewEnv(server.BaseUrl);
        Eval(env.Runtime, $@"
            globalThis.result = null;
            fetch('{server.BaseUrl}/foo').then(function(r) {{ return r.text(); }}).then(function(t) {{ globalThis.result = t; }});
        ");
        await PumpUntil(env.Runtime, () => env.Runtime.GetGlobal("result").IsString);
        env.Runtime.GetGlobal("result").AsString.Should().Be("hello");
    }

    [TestMethod]
    public async Task Fetch_file_url_resolves_with_buffer_and_content_type()
    {
        var path = Path.Combine(Path.GetTempPath(), $"starling_fetch_{Guid.NewGuid():N}.wasm");
        try
        {
            File.WriteAllBytes(path, [0x00, 0x61, 0x73, 0x6d]);
            var env = NewEnv(new Uri(path).AbsoluteUri);
            Eval(env.Runtime, $@"
                globalThis.status = null;
                globalThis.type = null;
                globalThis.length = null;
                fetch('{new Uri(path).AbsoluteUri}').then(function(r) {{
                    globalThis.status = r.status;
                    globalThis.type = r.headers.get('content-type');
                    return r.arrayBuffer();
                }}).then(function(buffer) {{
                    globalThis.length = buffer.byteLength;
                }});
            ");

            await PumpUntil(env.Runtime, () => env.Runtime.GetGlobal("length").IsNumber);

            env.Runtime.GetGlobal("status").AsNumber.Should().Be(200);
            env.Runtime.GetGlobal("type").AsString.Should().Be("application/wasm");
            env.Runtime.GetGlobal("length").AsNumber.Should().Be(4);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [TestMethod]
    public async Task Fetch_status_codes_round_trip()
    {
        await using var server = await LocalServer.Start(ctx =>
        {
            var path = ctx.Request.Url!.AbsolutePath;
            ctx.Response.StatusCode = path switch
            {
                "/404" => 404,
                "/500" => 500,
                _ => 200,
            };
        });
        var env = NewEnv(server.BaseUrl);
        Eval(env.Runtime, $@"
            globalThis.s1 = null; globalThis.s2 = null; globalThis.s3 = null;
            fetch('{server.BaseUrl}/ok').then(function(r) {{ globalThis.s1 = r.status; }});
            fetch('{server.BaseUrl}/404').then(function(r) {{ globalThis.s2 = r.status; }});
            fetch('{server.BaseUrl}/500').then(function(r) {{ globalThis.s3 = r.status; }});
        ");
        await PumpUntil(env.Runtime, () =>
            env.Runtime.GetGlobal("s1").IsNumber &&
            env.Runtime.GetGlobal("s2").IsNumber &&
            env.Runtime.GetGlobal("s3").IsNumber);
        env.Runtime.GetGlobal("s1").AsNumber.Should().Be(200);
        env.Runtime.GetGlobal("s2").AsNumber.Should().Be(404);
        env.Runtime.GetGlobal("s3").AsNumber.Should().Be(500);
    }

    [TestMethod]
    public async Task Fetch_json_parses_body()
    {
        await using var server = await LocalServer.Start(ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            using var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
            w.Write("{\"a\":1}");
        });
        var env = NewEnv(server.BaseUrl);
        Eval(env.Runtime, $@"
            globalThis.a = null;
            fetch('{server.BaseUrl}/x').then(function(r) {{ return r.json(); }}).then(function(o) {{ globalThis.a = o.a; }});
        ");
        await PumpUntil(env.Runtime, () => env.Runtime.GetGlobal("a").IsNumber);
        env.Runtime.GetGlobal("a").AsNumber.Should().Be(1);
    }

    [TestMethod]
    public async Task Fetch_response_header_can_be_read()
    {
        await using var server = await LocalServer.Start(ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.Headers["X-Custom"] = "yes";
        });
        var env = NewEnv(server.BaseUrl);
        Eval(env.Runtime, $@"
            globalThis.v = null;
            fetch('{server.BaseUrl}/h').then(function(r) {{ globalThis.v = r.headers.get('x-custom'); }});
        ");
        await PumpUntil(env.Runtime, () => env.Runtime.GetGlobal("v").IsString);
        env.Runtime.GetGlobal("v").AsString.Should().Be("yes");
    }

    [TestMethod]
    public async Task Fetch_post_sends_method_headers_and_body()
    {
        string? method = null;
        string? header = null;
        string? body = null;
        await using var server = await LocalServer.Start(ctx =>
        {
            method = ctx.Request.HttpMethod;
            header = ctx.Request.Headers["X-Custom"];
            using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            body = sr.ReadToEnd();
            ctx.Response.StatusCode = 204;
        });
        var env = NewEnv(server.BaseUrl);
        Eval(env.Runtime, $@"
            globalThis.done = false;
            fetch('{server.BaseUrl}/p', {{ method: 'POST', headers: {{ 'X-Custom': '1' }}, body: 'ping' }})
              .then(function(r) {{ globalThis.done = true; }});
        ");
        await PumpUntil(env.Runtime, () => env.Runtime.GetGlobal("done").AsBool);
        method.Should().Be("POST");
        header.Should().Be("1");
        body.Should().Be("ping");
    }

    [TestMethod]
    public async Task Fetch_url_search_params_body_sets_form_header()
    {
        string? contentType = null;
        string? body = null;
        await using var server = await LocalServer.Start(ctx =>
        {
            contentType = ctx.Request.Headers["Content-Type"];
            using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            body = sr.ReadToEnd();
            ctx.Response.StatusCode = 204;
        });
        var env = NewEnv(server.BaseUrl);
        Eval(env.Runtime, $@"
            globalThis.done = false;
            var params = new URLSearchParams({{ a: '1', b: 'two words' }});
            fetch('{server.BaseUrl}/p', {{ method: 'POST', body: params }})
              .then(function(r) {{ globalThis.done = true; }});
        ");
        await PumpUntil(env.Runtime, () => env.Runtime.GetGlobal("done").AsBool);
        contentType.Should().Be("application/x-www-form-urlencoded;charset=UTF-8");
        body.Should().Be("a=1&b=two+words");
    }

    [TestMethod]
    public async Task Fetch_form_data_body_sends_multipart_payload()
    {
        string? contentType = null;
        string? body = null;
        await using var server = await LocalServer.Start(ctx =>
        {
            contentType = ctx.Request.Headers["Content-Type"];
            using var sr = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            body = sr.ReadToEnd();
            ctx.Response.StatusCode = 204;
        });
        var env = NewEnv(server.BaseUrl);
        Eval(env.Runtime, $@"
            globalThis.done = false;
            var fd = new FormData();
            fd.append('name', 'value');
            fd.append('upload', new Blob(['abc'], {{ type: 'text/plain' }}), 'a.txt');
            fetch('{server.BaseUrl}/p', {{ method: 'POST', body: fd }})
              .then(function(r) {{ globalThis.done = true; }});
        ");
        await PumpUntil(env.Runtime, () => env.Runtime.GetGlobal("done").AsBool);
        contentType.Should().StartWith("multipart/form-data; boundary=----starling-formdata-");
        body.Should().Contain("name=\"name\"");
        body.Should().Contain("value");
        body.Should().Contain("filename=\"a.txt\"");
        body.Should().Contain("Content-Type: text/plain");
        body.Should().Contain("abc");
    }

    [TestMethod]
    public async Task Fetch_bad_url_rejects_with_TypeError()
    {
        var env = NewEnv("http://127.0.0.1:1/");
        Eval(env.Runtime, @"
            globalThis.err = null;
            fetch('http://127.0.0.1:1/nowhere').then(
                function(r) { globalThis.err = 'unexpected fulfillment'; },
                function(e) { globalThis.err = (e && e.message) ? e.message : String(e); }
            );
        ");
        await PumpUntil(env.Runtime, () => env.Runtime.GetGlobal("err").IsString, timeoutMs: 20000);
        env.Runtime.GetGlobal("err").AsString.Should().Contain("Failed to fetch");
    }

    [TestMethod]
    public async Task Response_body_can_only_be_read_once()
    {
        await using var server = await LocalServer.Start(ctx =>
        {
            using var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
            w.Write("hi");
        });
        var env = NewEnv(server.BaseUrl);
        Eval(env.Runtime, $@"
            globalThis.first = null; globalThis.secondMsg = null;
            fetch('{server.BaseUrl}/x').then(function(r) {{
                return r.text().then(function(t) {{
                    globalThis.first = t;
                    return r.text().then(
                        function(_) {{ globalThis.secondMsg = 'unexpected fulfillment'; }},
                        function(e) {{ globalThis.secondMsg = e.message; }}
                    );
                }});
            }});
        ");
        await PumpUntil(env.Runtime, () => env.Runtime.GetGlobal("secondMsg").IsString);
        env.Runtime.GetGlobal("first").AsString.Should().Be("hi");
        env.Runtime.GetGlobal("secondMsg").AsString.Should().Be("Body already consumed");
    }

    [TestMethod]
    public async Task Response_clone_permits_second_read()
    {
        await using var server = await LocalServer.Start(ctx =>
        {
            using var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
            w.Write("twice");
        });
        var env = NewEnv(server.BaseUrl);
        Eval(env.Runtime, $@"
            globalThis.a = null; globalThis.b = null;
            fetch('{server.BaseUrl}/x').then(function(r) {{
                var c = r.clone();
                return r.text().then(function(t1) {{
                    globalThis.a = t1;
                    return c.text().then(function(t2) {{ globalThis.b = t2; }});
                }});
            }});
        ");
        await PumpUntil(env.Runtime, () =>
            env.Runtime.GetGlobal("a").IsString && env.Runtime.GetGlobal("b").IsString);
        env.Runtime.GetGlobal("a").AsString.Should().Be("twice");
        env.Runtime.GetGlobal("b").AsString.Should().Be("twice");
    }

    [TestMethod]
    public async Task AbortController_aborts_in_flight_fetch()
    {
        // Server delays so the abort happens during the request.
        var hold = new TaskCompletionSource();
        await using var server = await LocalServer.Start(async ctx =>
        {
            await hold.Task.ConfigureAwait(false);
            ctx.Response.StatusCode = 200;
        });
        var env = NewEnv(server.BaseUrl);
        Eval(env.Runtime, $@"
            globalThis.errName = null;
            var ctl = new AbortController();
            fetch('{server.BaseUrl}/slow', {{ signal: ctl.signal }}).then(
                function(r) {{ globalThis.errName = 'unexpected fulfillment'; }},
                function(e) {{ globalThis.errName = (e && e.name) ? e.name : 'unknown'; }}
            );
            ctl.abort();
        ");
        // Let the abort flow.
        await PumpUntil(env.Runtime, () => env.Runtime.GetGlobal("errName").IsString, timeoutMs: 5000);
        hold.TrySetResult();
        env.Runtime.GetGlobal("errName").AsString.Should().Be("AbortError");
    }

    [TestMethod]
    public async Task Fetch_pending_counter_tracks_in_flight_request()
    {
        var hold = new TaskCompletionSource();
        await using var server = await LocalServer.Start(async ctx =>
        {
            await hold.Task.ConfigureAwait(false);
            using var w = new StreamWriter(ctx.Response.OutputStream, new UTF8Encoding(false));
            w.Write("done");
        });
        var env = NewEnv(server.BaseUrl);

        Eval(env.Runtime, $@"
            globalThis.result = null;
            fetch('{server.BaseUrl}/slow')
              .then(function(r) {{ return r.text(); }})
              .then(function(t) {{ globalThis.result = t; }});
        ");

        Starling.Bindings.FetchBinding.HasPendingFetches(env.Runtime).Should().BeTrue();
        hold.TrySetResult();

        await PumpUntil(env.Runtime, () => env.Runtime.GetGlobal("result").IsString);

        env.Runtime.GetGlobal("result").AsString.Should().Be("done");
        Starling.Bindings.FetchBinding.HasPendingFetches(env.Runtime).Should().BeFalse();
    }

    [TestMethod]
    public void Headers_constructor_round_trips()
    {
        var env = NewEnv("http://localhost/");
        Eval(env.Runtime, @"
            var h = new Headers({ 'Content-Type': 'text/html', 'X-Foo': 'a' });
            h.append('x-foo', 'b');
            globalThis.ct = h.get('content-type');
            globalThis.xf = h.get('x-foo');
            globalThis.hasFoo = h.has('X-Foo');
        ");
        env.Runtime.GetGlobal("ct").AsString.Should().Be("text/html");
        env.Runtime.GetGlobal("xf").AsString.Should().Be("a, b");
        env.Runtime.GetGlobal("hasFoo").AsBool.Should().BeTrue();
    }

    // -------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------

    internal static (JsRuntime Runtime, Document Document, StarlingHttpClient Client) NewEnv(string url)
    {
        var doc = new Document();
        doc.AppendChild(doc.CreateElement("html"));
        var runtime = new JsRuntime();
        var client = new StarlingHttpClient();
        WindowBinding.Install(runtime, doc, new WindowInstallOptions(
            DocumentUrl: url, HttpClient: client));
        return (runtime, doc, client);
    }

    internal static JsValue Eval(JsRuntime runtime, string source)
    {
        var program = new JsParser(source).ParseProgram();
        var chunk = JsCompiler.Compile(program);
        return new JsVm(runtime).Run(chunk);
    }

    internal static async Task PumpUntil(JsRuntime runtime, Func<bool> predicate, int timeoutMs = 10000)
    {
        // We need realm.ActiveVm published during the drain so Promise
        // reactions (JsFunction handlers) can dispatch. The easiest way is
        // to run a no-op chunk via JsVm.Run, which sets ActiveVm and drains
        // microtasks at the bottom. Same trick TimersBinding uses.
        var drainChunk = JsCompiler.Compile(new JsParser("").ParseProgram());
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!predicate() && sw.ElapsedMilliseconds < timeoutMs)
        {
            new JsVm(runtime).Run(drainChunk);
            if (predicate())
            {
                return;
            }

            await Task.Delay(20).ConfigureAwait(false);
        }
        new JsVm(runtime).Run(drainChunk);
    }
}

/// <summary>Minimal HttpListener-backed test server. Each request is dispatched
/// to the configured handler on a thread-pool task.</summary>
internal sealed class LocalServer : IAsyncDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public string BaseUrl { get; }

    private LocalServer(HttpListener listener, string baseUrl, Func<HttpListenerContext, Task> handler)
    {
        _listener = listener;
        BaseUrl = baseUrl;
        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync().ConfigureAwait(false); }
                catch { return; }
                _ = Task.Run(async () =>
                {
                    try { await handler(ctx).ConfigureAwait(false); }
                    catch { /* swallow */ }
                    finally
                    {
                        try { ctx.Response.OutputStream.Close(); } catch { }
                        try { ctx.Response.Close(); } catch { }
                    }
                });
            }
        });
    }

    public static Task<LocalServer> Start(Action<HttpListenerContext> handler)
        => Start(ctx => { handler(ctx); return Task.CompletedTask; });

    public static Task<LocalServer> Start(Func<HttpListenerContext, Task> handler)
    {
        // Bind to an ephemeral port.
        var port = FindFreePort();
        var prefix = $"http://127.0.0.1:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        return Task.FromResult(new LocalServer(listener, prefix.TrimEnd('/'), handler));
    }

    private static int FindFreePort()
    {
        var sock = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        sock.Start();
        var port = ((System.Net.IPEndPoint)sock.LocalEndpoint).Port;
        sock.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { await _loop.ConfigureAwait(false); } catch { }
    }
}
