using AwesomeAssertions;
using Jint;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Html;
using Starling.Loop;

namespace Starling.Bindings.Jint.Tests;

/// <summary>
/// Tier 3 parity: btoa / atob / structuredClone + the full console surface.
/// </summary>
[TestClass]
public sealed class CoreWebApiBindingsTests
{
    [TestMethod]
    public void btoa_and_atob_roundtrip()
    {
        var e = NewSession();
        e.Evaluate("btoa('hello')").AsString().Should().Be("aGVsbG8=");
        e.Evaluate("atob('aGVsbG8=')").AsString().Should().Be("hello");
        e.Evaluate("atob(btoa('Round Trip!'))").AsString().Should().Be("Round Trip!");
    }

    [TestMethod]
    public void btoa_rejects_non_latin1()
    {
        var e = NewSession();
        e.Evaluate("(function(){ try { btoa('\\u2603'); return 'no'; } catch (x) { return x.name; } })()")
            .AsString().Should().Be("InvalidCharacterError");
    }

    [TestMethod]
    public void atob_rejects_bad_encoding()
    {
        var e = NewSession();
        e.Evaluate("(function(){ try { atob('a'); return 'no'; } catch (x) { return x.name; } })()")
            .AsString().Should().Be("InvalidCharacterError");
    }

    [TestMethod]
    public void structuredClone_deep_object()
    {
        var e = NewSession();
        var js = """
            (function(){
              var src = { a: 1, b: [2, 3, { c: 'x' }], d: true };
              var c = structuredClone(src);
              c.b[2].c = 'y';
              return (c !== src) + '|' + (c.b !== src.b) + '|' + src.b[2].c + '|' + c.b[2].c + '|' + c.a + '|' + c.d;
            })()
            """;
        e.Evaluate(js).AsString().Should().Be("true|true|x|y|1|true");
    }

    [TestMethod]
    public void structuredClone_handles_cycles_and_typed_arrays()
    {
        var e = NewSession();
        e.Evaluate("(function(){ var o={}; o.self=o; var c=structuredClone(o); return c.self===c; })()")
            .AsBoolean().Should().BeTrue();
        e.Evaluate("(function(){ var a=new Uint8Array([1,2,3]); var c=structuredClone(a); c[0]=9; return (c instanceof Uint8Array)+'|'+a[0]+'|'+c[0]+'|'+c.length; })()")
            .AsString().Should().Be("true|1|9|3");
    }

    [TestMethod]
    public void structuredClone_rejects_functions()
    {
        var e = NewSession();
        e.Evaluate("(function(){ try { structuredClone(function(){}); return 'no'; } catch (x) { return x.name; } })()")
            .AsString().Should().Be("DataCloneError");
    }

    [TestMethod]
    public void console_has_full_method_set()
    {
        var e = NewSession();
        foreach (var m in new[] { "log", "info", "warn", "error", "debug", "trace", "dir", "table",
                                   "assert", "count", "countReset", "time", "timeEnd", "timeLog",
                                   "group", "groupCollapsed", "groupEnd", "clear" })
        {
            e.Evaluate($"typeof console.{m}").AsString().Should().Be("function", $"console.{m}");
        }
        // calling them must not throw
        e.Evaluate("(function(){ console.group('g'); console.count('x'); console.count('x'); console.countReset('x'); " +
                   "console.time('t'); console.timeEnd('t'); console.assert(true); console.groupEnd(); console.clear(); return 'ok'; })()")
            .AsString().Should().Be("ok");
    }

    private static global::Jint.Engine NewSession()
    {
        var doc = HtmlParser.Parse("<!doctype html><html><body></body></html>");
        var baseUrl = global::Starling.Url.UrlParser.Parse("about:blank").Value;
        var engine = new global::Jint.Engine();
        var http = new Starling.Net.StarlingHttpClient();
        var ctx = new JintBackendContext(
            engine, doc, baseUrl, http, NullLoggerFactory.Instance,
            new WebEventLoop(), null,
            (_, _) => System.Threading.Tasks.Task.FromResult<string?>(null));
        JintBindings.InstallAll(ctx);
        return engine;
    }
}
