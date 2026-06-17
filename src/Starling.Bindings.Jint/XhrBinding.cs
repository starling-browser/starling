using System.Runtime.CompilerServices;
using System.Text;
using Jint;
using Jint.Native;
using Jint.Native.Function;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using Microsoft.Extensions.Logging;
using Starling.Net.Http;
using JsTypedArray = global::Jint.Native.JsTypedArray;
using StarlingUrl = global::Starling.Url.Url;
using StarlingUrlParser = global::Starling.Url.UrlParser;

namespace Starling.Bindings.Jint;

// XMLHttpRequest.
// Mirrors Starling.Bindings/XhrBinding.cs (state machine + header handling).
//
// State machine: UNSENT(0) → OPENED(1) on open(); → HEADERS_RECEIVED(2) →
// LOADING(3) → DONE(4) dispatched together on completion (we buffer the whole
// body, so the intermediate states fire back-to-back ahead of DONE, matching the
// Starling backend).
//
// Async completion / the session pump
// -----------------------------------
// The actual HTTP request runs on a thread-pool thread (ctx.Http.SendAsync). The
// completion MUST be marshalled back onto the JS thread before any handler runs
// (Jint.Engine is single-threaded). It is marshalled via ctx.Post(...): the background request posts its completion (the XHR
// state machine + event dispatch) onto the JS thread, where
// JintScriptSession.PumpOnce drains and runs it. PumpOnce reports "not idle"
// while the post queue is non-empty, so the pump keeps turning until the request
// settles. WebEventLoop is touched only on the JS thread; the only cross-thread
// hand-off is ctx.Post's internal thread-safe queue.
//
// EventTarget fallback: if ctx.Wrappers.EventTargetPrototype is present we
// chain XMLHttpRequest.prototype to it so addEventListener is inherited and
// `xhr instanceof EventTarget` holds. If not, we fall back to a
// local listener registry (addEventListener/removeEventListener defined in this
// file) plus the on* handler slots — same event names / handler shapes, so it is
// forward-compatible.
internal static class XhrBinding
{
    // Per-instance state, kept off-object so it never appears as an enumerable JS
    // property and never depends on CLR interop being enabled. Keyed by the XHR
    // wrapper ObjectInstance (unique per `new XMLHttpRequest()`); the weak table
    // lets a collected wrapper drop its state.
    private static readonly ConditionalWeakTable<ObjectInstance, XhrState> States = new();

    // Native init hook the JS constructor calls with `this`. Jint's ClrFunction
    // isn't constructable, so XMLHttpRequest is a real JS function (so `new`,
    // `instanceof` and `.name` all behave) that delegates instance setup here.
    private const string InitHook = "__starling_xhrInit__";

    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;

        // The native per-instance initializer (attaches XhrState to the new
        // object). Defined non-enumerably on the global so the JS shim can reach
        // it; harmless to leave installed.
        JintInterop.DefineDataProp(engine.Global, InitHook,
            new ClrFunction(engine, InitHook, (_, a) =>
            {
                if (a.Length > 0 && a[0] is ObjectInstance self)
                {
                    AttachState(self, new XhrState());
                }

                return JsValue.Undefined;
            }, 1),
            writable: false, enumerable: false, configurable: true);

        // Constructor as a real JS function; use ITS OWN .prototype object — Jint
        // binds `new`'s prototype at function-definition time, so replacing the
        // slot afterwards would be ignored.
        var ctor = (ObjectInstance)engine.Evaluate(
            $"(function XMLHttpRequest(){{ {InitHook}(this); }})");
        var proto = ctor.Get("prototype").AsObject();

        if (ctx.Wrappers.EventTargetPrototype is { } etp)
        {
            proto.Prototype = etp;
        }

        InstallStateConstants(proto);
        InstallStateConstants(ctor);
        InstallAccessors(ctx, proto);
        InstallMethods(ctx, proto);

        JintInterop.DefineDataProp(engine.Global, "XMLHttpRequest", ctor,
            writable: true, enumerable: false, configurable: true);
    }

    // ---- constants ----

    private static void InstallStateConstants(ObjectInstance target)
    {
        void K(string name, int value) =>
            target.FastSetProperty(name, new PropertyDescriptor(
                new JsNumber(value), writable: false, enumerable: true, configurable: false));
        K("UNSENT", 0);
        K("OPENED", 1);
        K("HEADERS_RECEIVED", 2);
        K("LOADING", 3);
        K("DONE", 4);
    }

    // ---- accessors ----

    private static void InstallAccessors(JintBackendContext ctx, ObjectInstance proto)
    {
        var engine = ctx.Engine;

        JintInterop.DefineAccessor(engine, proto, "readyState",
            (t, _) => new JsNumber(State(engine, t).ReadyState));
        JintInterop.DefineAccessor(engine, proto, "status",
            (t, _) => new JsNumber(State(engine, t).Status));
        JintInterop.DefineAccessor(engine, proto, "statusText",
            (t, _) => JintInterop.Str(State(engine, t).StatusText));
        JintInterop.DefineAccessor(engine, proto, "responseText",
            (t, _) => JintInterop.Str(State(engine, t).ResponseText));
        JintInterop.DefineAccessor(engine, proto, "responseXML",
            (_, _) => JsValue.Null);
        JintInterop.DefineAccessor(engine, proto, "responseURL",
            (t, _) => JintInterop.Str(State(engine, t).ResponseUrl));

        JintInterop.DefineAccessor(engine, proto, "response", (t, _) =>
        {
            var x = State(engine, t);
            return x.ResponseType switch
            {
                "json" => SafeJsonParse(engine, x.ResponseText),
                "arraybuffer" => engine.Intrinsics.ArrayBuffer.Construct((byte[])x.ResponseBytes.Clone()),
                _ => JintInterop.Str(x.ResponseText),
            };
        });

        JintInterop.DefineAccessor(engine, proto, "responseType",
            (t, _) => JintInterop.Str(State(engine, t).ResponseType),
            (t, args) =>
            {
                State(engine, t).ResponseType = args.Length > 0 ? args[0].ToString() : "";
                return JsValue.Undefined;
            });

        JintInterop.DefineAccessor(engine, proto, "timeout",
            (t, _) => new JsNumber(State(engine, t).Timeout),
            (t, args) =>
            {
                if (args.Length > 0)
                {
                    var v = TypeConverter.ToNumber(args[0]);
                    State(engine, t).Timeout = double.IsNaN(v) || v < 0 ? 0 : (int)v;
                }
                return JsValue.Undefined;
            });

        JintInterop.DefineAccessor(engine, proto, "withCredentials",
            (t, _) => JintInterop.Bool(State(engine, t).WithCredentials),
            (t, args) =>
            {
                State(engine, t).WithCredentials = args.Length > 0 && TypeConverter.ToBoolean(args[0]);
                return JsValue.Undefined;
            });

        // upload — minimal XMLHttpRequestUpload (an EventTarget-ish stub carrying
        // the same on* slots; no progress events are dispatched on it yet).
        JintInterop.DefineAccessor(engine, proto, "upload",
            (t, _) => State(engine, t).Upload ??= MakeUpload(ctx));

        // on* event-handler attributes: writable own slots read by the dispatch
        // path in addition to addEventListener listeners.
        foreach (var ev in new[]
                 {
                     "onreadystatechange", "onload", "onerror", "onabort",
                     "ontimeout", "onloadstart", "onloadend", "onprogress",
                 })
        {
            JintInterop.DefineDataProp(proto, ev, JsValue.Null,
                writable: true, enumerable: true, configurable: true);
        }
    }

    private static JsObject MakeUpload(JintBackendContext ctx)
    {
        var upload = new JsObject(ctx.Engine);
        if (ctx.Wrappers.EventTargetPrototype is { } etp)
        {
            upload.Prototype = etp;
        }

        foreach (var ev in new[] { "onloadstart", "onprogress", "onload", "onerror", "onabort", "ontimeout", "onloadend" })
        {
            JintInterop.DefineDataProp(upload, ev, JsValue.Null, writable: true, enumerable: true, configurable: true);
        }

        return upload;
    }

    // ---- methods ----

    private static void InstallMethods(JintBackendContext ctx, ObjectInstance proto)
    {
        var engine = ctx.Engine;

        JintInterop.DefineMethod(engine, proto, "open", (t, args) =>
        {
            var x = State(engine, t);
            if (args.Length < 2)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "XHR.open requires (method, url)");
            }

            var method = args[0].ToString().ToUpperInvariant();
            var url = args[1].ToString();
            // async defaults to true; synchronous XHR is unsupported.
            if (args.Length > 2 && !args[2].IsUndefined() && !TypeConverter.ToBoolean(args[2]))
            {
                throw new JavaScriptException(engine.Intrinsics.Error, "Synchronous XMLHttpRequest is not supported");
            }

            x.Reset();
            x.Method = method;
            x.RequestUrl = ResolveUrl(url, ctx.BaseUrl);
            x.ReadyState = 1; // OPENED
            FireReadyStateChange(ctx, t, x);
            return JsValue.Undefined;
        }, length: 2);

        JintInterop.DefineMethod(engine, proto, "setRequestHeader", (t, args) =>
        {
            var x = State(engine, t);
            if (x.ReadyState != 1)
            {
                throw new JavaScriptException(engine.Intrinsics.Error, "setRequestHeader: state must be OPENED");
            }

            var name = args.Length > 0 ? args[0].ToString() : "";
            var value = args.Length > 1 ? args[1].ToString() : "";
            x.RequestHeaders.Add((name, value));
            return JsValue.Undefined;
        }, length: 2);

        JintInterop.DefineMethod(engine, proto, "send", (t, args) =>
        {
            var x = State(engine, t);
            if (x.ReadyState != 1)
            {
                throw new JavaScriptException(engine.Intrinsics.Error, "XHR.send: state must be OPENED");
            }

            var body = args.Length > 0 ? BodyToBytes(args[0]) : Array.Empty<byte>();
            SendImpl(ctx, t, x, body);
            return JsValue.Undefined;
        }, length: 0);

        var ctxLog = ctx.LoggerFactory.CreateLogger(typeof(XhrBinding));
        JintInterop.DefineMethod(engine, proto, "abort", (t, _) =>
        {
            var x = State(engine, t);
            try { x.Cts?.Cancel(); }
            catch (Exception ex) { /* best-effort */ XhrBindingLog.AbortCancelFailed(ctxLog, ex.Message); }
            x.Aborted = true;
            if (x.ReadyState is not (0 or 4))
            {
                x.ReadyState = 4;
                FireReadyStateChange(ctx, t, x);
                FireEvent(ctx, t, x, "abort");
                FireEvent(ctx, t, x, "loadend");
            }
            return JsValue.Undefined;
        }, length: 0);

        JintInterop.DefineMethod(engine, proto, "getAllResponseHeaders", (t, _) =>
        {
            var x = State(engine, t);
            var sb = new StringBuilder();
            foreach (var (k, v) in x.ResponseHeaders)
            {
                sb.Append(k).Append(": ").Append(v).Append("\r\n");
            }

            return JintInterop.Str(sb.ToString());
        }, length: 0);

        JintInterop.DefineMethod(engine, proto, "getResponseHeader", (t, args) =>
        {
            var x = State(engine, t);
            var name = args.Length > 0 ? args[0].ToString() : "";
            foreach (var (k, v) in x.ResponseHeaders)
            {
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase))
                {
                    return JintInterop.Str(v);
                }
            }

            return JsValue.Null;
        }, length: 1);

        JintInterop.DefineMethod(engine, proto, "overrideMimeType",
            (_, _) => JsValue.Undefined, length: 1);

        // EventTarget surface. When the real EventTargetPrototype is chained,
        // these inherited methods exist already; we still define our own so the
        // local listener registry stays the source of truth for the XHR events the
        // completion path dispatches (that path doesn't go through the Starling.Dom
        // dispatcher, since the XHR object is not a DOM node).
        JintInterop.DefineMethod(engine, proto, "addEventListener", (t, args) =>
        {
            var x = State(engine, t);
            if (args.Length >= 2 && args[0].IsString() && args[1] is Function fn)
            {
                var type = args[0].ToString();
                if (!x.Listeners.TryGetValue(type, out var list))
                {
                    x.Listeners[type] = list = new List<Function>();
                }

                list.Add(fn);
            }
            return JsValue.Undefined;
        }, length: 2);

        JintInterop.DefineMethod(engine, proto, "removeEventListener", (t, args) =>
        {
            var x = State(engine, t);
            if (args.Length >= 2 && args[0].IsString() && args[1] is Function fn
                && x.Listeners.TryGetValue(args[0].ToString(), out var list))
            {
                list.RemoveAll(f => ReferenceEquals(f, fn));
            }

            return JsValue.Undefined;
        }, length: 2);

        JintInterop.DefineMethod(engine, proto, "dispatchEvent", (t, args) =>
        {
            var x = State(engine, t);
            if (args.Length > 0 && args[0] is ObjectInstance evt)
            {
                var type = evt.Get("type");
                if (type.IsString())
                {
                    DispatchToListeners(ctx, t, x, type.ToString(), evt);
                }
            }
            return JintInterop.Bool(true);
        }, length: 1);
    }

    // ---- request execution ----

    private static void SendImpl(JintBackendContext ctx, JsValue thisVal, XhrState x, byte[] body)
    {
        var parsed = StarlingUrlParser.Parse(x.RequestUrl);
        if (parsed.IsErr)
        {
            FailNow(ctx, thisVal, x);
            return;
        }
        var requestUrl = parsed.Value;

        x.Cts = new CancellationTokenSource();
        FireEvent(ctx, thisVal, x, "loadstart");

        // data: URLs resolve locally — the Starling HTTP client only speaks
        // http/https. This is also browser-correct (XHR supports data:) and gives
        // the test suite a fully-offline path. Posted (not run inline) so the
        // completion lands on a later pump turn, matching async XHR semantics.
        if (requestUrl.IsData)
        {
            var outcome = global::Starling.Url.DataUrl.TryDecode(requestUrl, out var payload)
                ? XhrOutcome.Success(200, "OK",
                    new List<(string, string)> { ("content-type", payload.MediaType) }, payload.Bytes)
                : XhrOutcome.Error();
            ctx.Post(() => Complete(ctx, thisVal, x, outcome));
            return;
        }

        var headers = new HttpHeaders();
        var sendLog = ctx.LoggerFactory.CreateLogger(typeof(XhrBinding));
        foreach (var (k, v) in x.RequestHeaders)
        {
            try { headers.Add(k, v); }
            catch (Exception ex) { /* skip invalid header names */ XhrBindingLog.InvalidHeaderSkipped(sendLog, k, ex.Message); }
        }

        var wire = new HttpRequest(x.Method, requestUrl, headers, body);

        var token = x.Cts.Token;
        _ = Task.Run(async () =>
        {
            XhrOutcome outcome;
            try
            {
                var result = await ctx.Http.SendAsync(wire, token).ConfigureAwait(false);
                if (result.IsErr)
                {
                    outcome = XhrOutcome.Error();
                }
                else
                {
                    var resp = result.Value;
                    var hdrs = new List<(string, string)>();
                    foreach (var kv in resp.Headers)
                    {
                        hdrs.Add((kv.Key, kv.Value));
                    }

                    outcome = XhrOutcome.Success(
                        resp.StatusCode, resp.ReasonPhrase ?? "", hdrs, resp.Body.ToArray());
                }
            }
            catch (Exception ex)
            {
                XhrBindingLog.SendFailed(ctx.LoggerFactory.CreateLogger(typeof(XhrBinding)), x.RequestUrl, ex.Message);
                outcome = XhrOutcome.Error();
            }
            // Marshal the completion onto the JS thread; PumpOnce drains and runs it.
            ctx.Post(() => Complete(ctx, thisVal, x, outcome));
        }, token);
    }

    private static void Complete(JintBackendContext ctx, JsValue thisVal, XhrState x, XhrOutcome outcome)
    {
        if (x.Aborted)
        {
            return;
        }

        if (!outcome.Ok)
        {
            FailNow(ctx, thisVal, x);
            return;
        }
        x.Status = outcome.Status;
        x.StatusText = outcome.StatusText;
        x.ResponseUrl = x.RequestUrl;
        x.ResponseHeaders.Clear();
        x.ResponseHeaders.AddRange(outcome.Headers);
        x.ResponseBytes = outcome.Body;
        x.ResponseText = Encoding.UTF8.GetString(outcome.Body);

        x.ReadyState = 2; FireReadyStateChange(ctx, thisVal, x);
        x.ReadyState = 3; FireReadyStateChange(ctx, thisVal, x);
        x.ReadyState = 4; FireReadyStateChange(ctx, thisVal, x);
        FireEvent(ctx, thisVal, x, "load");
        FireEvent(ctx, thisVal, x, "loadend");
    }

    private static void FailNow(JintBackendContext ctx, JsValue thisVal, XhrState x)
    {
        if (x.Aborted)
        {
            return;
        }

        x.ReadyState = 4;
        x.Status = 0;
        FireReadyStateChange(ctx, thisVal, x);
        FireEvent(ctx, thisVal, x, "error");
        FireEvent(ctx, thisVal, x, "loadend");
    }

    // ---- event dispatch ----

    private static void FireReadyStateChange(JintBackendContext ctx, JsValue thisVal, XhrState x)
    {
        InvokeHandler(ctx, thisVal, "onreadystatechange", "readystatechange");
        DispatchToListeners(ctx, thisVal, x, "readystatechange", MakeEvent(ctx, thisVal, "readystatechange"));
    }

    private static void FireEvent(JintBackendContext ctx, JsValue thisVal, XhrState x, string type)
    {
        InvokeHandler(ctx, thisVal, "on" + type, type);
        DispatchToListeners(ctx, thisVal, x, type, MakeEvent(ctx, thisVal, type));
    }

    private static void InvokeHandler(JintBackendContext ctx, JsValue thisVal, string slot, string type)
    {
        if (thisVal is not ObjectInstance self)
        {
            return;
        }

        if (self.Get(slot) is Function fn)
        {
            SafeCall(ctx, fn, self, MakeEvent(ctx, thisVal, type));
        }
    }

    private static void DispatchToListeners(JintBackendContext ctx, JsValue thisVal, XhrState x, string type, JsValue evt)
    {
        if (!x.Listeners.TryGetValue(type, out var list) || list.Count == 0)
        {
            return;
        }

        var self = thisVal as ObjectInstance;
        // Copy so a listener removing itself doesn't disturb iteration.
        foreach (var fn in list.ToArray())
        {
            SafeCall(ctx, fn, (JsValue?)self ?? JsValue.Undefined, evt);
        }
    }

    private static JsObject MakeEvent(JintBackendContext ctx, JsValue target, string type)
    {
        var ev = new JsObject(ctx.Engine);
        if (ctx.Wrappers.EventPrototype is { } ep)
        {
            ev.Prototype = ep;
        }

        JintInterop.DefineDataProp(ev, "type", JintInterop.Str(type), writable: false, enumerable: true, configurable: false);
        JintInterop.DefineDataProp(ev, "target", target, writable: false, enumerable: true, configurable: false);
        JintInterop.DefineDataProp(ev, "currentTarget", target, writable: false, enumerable: true, configurable: false);
        return ev;
    }

    private static void SafeCall(JintBackendContext ctx, Function fn, JsValue thisArg, JsValue arg)
    {
        var jsLog = ctx.LoggerFactory.CreateLogger("Starling.engine.js");
        try { ctx.Engine.Invoke(fn, thisArg, new[] { arg }); }
        catch (JavaScriptException ex)
        {
            XhrBindingLog.UncaughtInXhr(jsLog, JintInterop.DescribeError(ex.Error, ex.Message));
        }
    }

    // ---- helpers ----

    private static byte[] BodyToBytes(JsValue body)
    {
        if (body.IsNull() || body.IsUndefined())
        {
            return Array.Empty<byte>();
        }

        if (body.IsArrayBuffer() && body.AsArrayBuffer() is { } ab)
        {
            return (byte[])ab.Clone();
        }

        if (body is JsTypedArray)
        {
            return ExtractTypedArrayBytes(body);
        }
        // Strings (incl. URLSearchParams/FormData stringification) and everything
        // else go through UTF-8 of the JS string form.
        return Encoding.UTF8.GetBytes(body.ToString());
    }

    private static byte[] ExtractTypedArrayBytes(JsValue typed)
    {
        // A typed array exposes its underlying ArrayBuffer via `.buffer`; read the
        // bytes there. (byteOffset/byteLength slicing is uncommon for XHR bodies;
        // we send the whole backing buffer, matching common usage.)
        if (typed is ObjectInstance oi)
        {
            var buf = oi.Get("buffer");
            if (buf.IsArrayBuffer() && buf.AsArrayBuffer() is { } ab)
            {
                return (byte[])ab.Clone();
            }
        }
        return Encoding.UTF8.GetBytes(typed.ToString());
    }

    private static string ResolveUrl(string input, StarlingUrl baseUrl)
    {
        // Absolute first.
        var abs = StarlingUrlParser.Parse(input);
        if (abs.IsOk)
        {
            return abs.Value.ToString();
        }
        // Resolve relative against the document base URL.
        var rel = StarlingUrlParser.Parse(input, baseUrl);
        return rel.IsOk ? rel.Value.ToString() : input;
    }

    private static JsValue SafeJsonParse(global::Jint.Engine engine, string text)
    {
        try
        {
            if (engine.Global.Get("JSON") is not ObjectInstance jo)
            {
                return JsValue.Null;
            }

            if (jo.Get("parse") is not Function fn)
            {
                return JsValue.Null;
            }

            return engine.Invoke(fn, jo, new JsValue[] { new JsString(text) });
        }
        catch (Exception)
        {
            // JSON.parse threw (invalid JSON) — return null per XHR spec for responseType=json.
            return JsValue.Null;
        }
    }

    // ---- state plumbing ----

    private static void AttachState(ObjectInstance self, XhrState state) =>
        States.AddOrUpdate(self, state);

    private static XhrState State(global::Jint.Engine engine, JsValue thisVal)
    {
        if (thisVal is ObjectInstance oi && States.TryGetValue(oi, out var s))
        {
            return s;
        }

        throw new JavaScriptException(engine.Intrinsics.TypeError, "'this' is not an XMLHttpRequest");
    }
}

/// <summary>Per-instance XHR state, kept off the JS object in a side table.
/// Mirrors <c>Starling.Bindings.XhrObject</c>'s fields.</summary>
internal sealed class XhrState
{
    public string Method { get; set; } = "GET";
    public string RequestUrl { get; set; } = "";
    public List<(string Name, string Value)> RequestHeaders { get; } = new();
    public int ReadyState { get; set; }
    public int Status { get; set; }
    public string StatusText { get; set; } = "";
    public string ResponseText { get; set; } = "";
    public byte[] ResponseBytes { get; set; } = Array.Empty<byte>();
    public string ResponseUrl { get; set; } = "";
    public List<(string Name, string Value)> ResponseHeaders { get; } = new();
    public string ResponseType { get; set; } = "";
    public int Timeout { get; set; }
    public bool WithCredentials { get; set; }
    public bool Aborted { get; set; }
    public CancellationTokenSource? Cts { get; set; }
    public ObjectInstance? Upload { get; set; }

    // type → listeners (local fallback for the EventTarget surface; see file FLAG).
    public Dictionary<string, List<Function>> Listeners { get; } = new(StringComparer.Ordinal);

    public void Reset()
    {
        RequestHeaders.Clear();
        Status = 0; StatusText = ""; ResponseText = ""; ResponseBytes = Array.Empty<byte>();
        ResponseHeaders.Clear(); ResponseUrl = ""; Aborted = false;
    }
}

/// <summary>Snapshot of a completed (or failed) request, marshalled from the
/// thread-pool send back onto the JS thread.</summary>
internal readonly struct XhrOutcome
{
    public bool Ok { get; private init; }
    public int Status { get; private init; }
    public string StatusText { get; private init; }
    public List<(string Name, string Value)> Headers { get; private init; }
    public byte[] Body { get; private init; }

    public static XhrOutcome Success(int status, string statusText, List<(string, string)> headers, byte[] body) =>
        new() { Ok = true, Status = status, StatusText = statusText, Headers = headers, Body = body };

    public static XhrOutcome Error() =>
        new() { Ok = false, Headers = new List<(string, string)>(), Body = Array.Empty<byte>(), StatusText = "" };
}

internal static partial class XhrBindingLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "XHR abort CTS cancel failed: {Message}")]
    public static partial void AbortCancelFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped invalid XHR request header '{Name}': {Message}")]
    public static partial void InvalidHeaderSkipped(ILogger logger, string name, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "XHR send failed for {Url}: {Message}")]
    public static partial void SendFailed(ILogger logger, string url, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Uncaught (in XHR) {Detail}")]
    public static partial void UncaughtInXhr(ILogger logger, string detail);
}
