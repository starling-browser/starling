using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Dom;
using Starling.Js.Runtime;
using Starling.Net;
using Starling.Net.Http;
using StarlingUrlParser = global::Starling.Url.UrlParser;

namespace Starling.Bindings;

/// <summary>
/// B5-3 — Legacy <c>XMLHttpRequest</c> backed by <see cref="StarlingHttpClient"/>.
/// Google's bundles still use it heavily, so it's a first-class citizen even
/// though it sits behind fetch.
/// </summary>
/// <remarks>
/// <para><b>State machine.</b> UNSENT(0) → OPENED(1) on <c>open()</c>;
/// → HEADERS_RECEIVED(2) → LOADING(3) → DONE(4) on completion (we don't
/// stream chunks, so HEADERS_RECEIVED/LOADING are dispatched together with
/// DONE on the microtask drain).</para>
///
/// <para><b>Threading.</b> Same contract as fetch: the actual request runs on
/// the thread pool; readystate transitions enqueue microtasks. The host must
/// pump (<see cref="JsRuntime.DrainMicrotasks"/> or another script Run).</para>
///
/// <para><b>Simplifications.</b> Synchronous XHR (<c>async === false</c>)
/// throws. <c>responseXML</c> always null. <c>responseType = 'document'</c>
/// resolves the same as <c>'text'</c>. <c>overrideMimeType</c> is accepted
/// and ignored. <c>upload</c> object not implemented.</para>
/// </remarks>
public static class XhrBinding
{
    private static readonly ILogger Log = NullLoggerFactory.Instance.CreateLogger(typeof(XhrBinding));

    public static void Install(JsRuntime runtime, StarlingHttpClient client, Document document)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(document);

        var realm = runtime.Realm;
        if (realm.XmlHttpRequestConstructor is not null)
        {
            return;
        }

        // XMLHttpRequest.prototype inherits from EventTarget so addEventListener works.
        var proto = new JsObject(realm.EventTargetPrototype ?? realm.ObjectPrototype);
        realm.XmlHttpRequestPrototype = proto;

        InstallStateConstants(proto);
        InstallProtoAccessors(realm, proto);
        InstallProtoMethods(runtime, client, document, proto);

        var ctor = new JsNativeFunction(realm, "XMLHttpRequest", 0, (_, _) =>
        {
            var xhr = new XhrObject(proto);
            EventTargetBinding.BindWrapper(xhr, xhr.HostTarget);
            return JsValue.Object(xhr);
        }, isConstructor: true);
        InstallStateConstants(ctor);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), false, false, false));
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), true, false, true));
        realm.XmlHttpRequestConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("XMLHttpRequest",
            PropertyDescriptor.Data(JsValue.Object(ctor), true, false, true));
    }

    private static void InstallStateConstants(JsObject target)
    {
        target.DefineOwnProperty("UNSENT", PropertyDescriptor.Data(JsValue.Number(0), false, true, false));
        target.DefineOwnProperty("OPENED", PropertyDescriptor.Data(JsValue.Number(1), false, true, false));
        target.DefineOwnProperty("HEADERS_RECEIVED", PropertyDescriptor.Data(JsValue.Number(2), false, true, false));
        target.DefineOwnProperty("LOADING", PropertyDescriptor.Data(JsValue.Number(3), false, true, false));
        target.DefineOwnProperty("DONE", PropertyDescriptor.Data(JsValue.Number(4), false, true, false));
    }

    private static void InstallProtoAccessors(JsRealm realm, JsObject proto)
    {
        EventTargetBinding.DefineAccessor(realm, proto, "readyState",
            (thisV, _) => JsValue.Number(XhrObject.Require(realm, thisV).ReadyState));
        EventTargetBinding.DefineAccessor(realm, proto, "status",
            (thisV, _) => JsValue.Number(XhrObject.Require(realm, thisV).Status));
        EventTargetBinding.DefineAccessor(realm, proto, "statusText",
            (thisV, _) => JsValue.String(XhrObject.Require(realm, thisV).StatusText));
        EventTargetBinding.DefineAccessor(realm, proto, "responseText",
            (thisV, _) => JsValue.String(XhrObject.Require(realm, thisV).ResponseText));
        EventTargetBinding.DefineAccessor(realm, proto, "responseXML",
            (_, _) => JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, proto, "responseURL",
            (thisV, _) => JsValue.String(XhrObject.Require(realm, thisV).ResponseUrl));
        EventTargetBinding.DefineAccessor(realm, proto, "response",
            (thisV, _) =>
            {
                var x = XhrObject.Require(realm, thisV);
                return x.ResponseType switch
                {
                    "json" => SafeJsonParse(realm, x.ResponseText),
                    "arraybuffer" => MakeArrayBuffer(realm, x.ResponseBytes),
                    _ => JsValue.String(x.ResponseText),
                };
            });
        EventTargetBinding.DefineAccessor(realm, proto, "responseType",
            (thisV, _) => JsValue.String(XhrObject.Require(realm, thisV).ResponseType),
            (thisV, args) =>
            {
                var x = XhrObject.Require(realm, thisV);
                x.ResponseType = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                return JsValue.Undefined;
            });
        // onreadystatechange / onload / onerror / onabort: writable own slots
        // that the dispatch code reads in addition to addEventListener.
        foreach (var ev in new[] { "onreadystatechange", "onload", "onerror", "onabort", "onloadstart", "onloadend", "onprogress" })
        {
            // Default as plain data property null.
            proto.DefineOwnProperty(ev,
                PropertyDescriptor.Data(JsValue.Null, true, true, true));
        }
    }

    private static void InstallProtoMethods(JsRuntime runtime, StarlingHttpClient client, Document document, JsObject proto)
    {
        var realm = runtime.Realm;
        EventTargetBinding.DefineMethod(realm, proto, "open", (thisV, args) =>
        {
            var x = XhrObject.Require(realm, thisV);
            if (args.Length < 2)
            {
                throw new JsThrow(realm.NewTypeError("XHR.open requires (method, url)"));
            }

            var method = JsValue.ToStringValue(args[0]).ToUpperInvariant();
            var url = JsValue.ToStringValue(args[1]);
            if (args.Length > 2 && !args[2].IsUndefined && !JsValue.ToBoolean(args[2]))
            {
                throw new JsThrow(realm.NewError(realm.ErrorPrototype,
                    "Synchronous XMLHttpRequest is not supported"));
            }

            x.Reset();
            x.Method = method;
            x.RequestUrl = ResolveUrl(url, document);
            x.ReadyState = 1; // OPENED
            FireReadyStateChange(realm, x);
            return JsValue.Undefined;
        }, length: 2);

        EventTargetBinding.DefineMethod(realm, proto, "setRequestHeader", (thisV, args) =>
        {
            var x = XhrObject.Require(realm, thisV);
            if (x.ReadyState != 1)
            {
                throw new JsThrow(realm.NewError(realm.ErrorPrototype, "setRequestHeader: state must be OPENED"));
            }

            var name = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            var value = args.Length > 1 ? JsValue.ToStringValue(args[1]) : "";
            x.RequestHeaders.Append(name, value);
            return JsValue.Undefined;
        }, length: 2);

        EventTargetBinding.DefineMethod(realm, proto, "send", (thisV, args) =>
        {
            var x = XhrObject.Require(realm, thisV);
            if (x.ReadyState != 1)
            {
                throw new JsThrow(realm.NewError(realm.ErrorPrototype, "XHR.send: state must be OPENED"));
            }

            var bodyV = args.Length > 0 ? args[0] : JsValue.Undefined;
            var bodyBytes = FetchBinding.BodyToBytes(realm, bodyV);
            SendImpl(runtime, client, x, bodyBytes);
            return JsValue.Undefined;
        }, length: 0);

        EventTargetBinding.DefineMethod(realm, proto, "abort", (thisV, _) =>
        {
            var x = XhrObject.Require(realm, thisV);
            try { x.Cts?.Cancel(); } catch (Exception ex) { XhrBindingLog.AbortCancelFailed(Log, ex); }
            x.Aborted = true;
            if (x.ReadyState != 0 && x.ReadyState != 4)
            {
                x.ReadyState = 4;
                FireReadyStateChange(realm, x);
                FireEvent(realm, x, "abort");
                FireEvent(realm, x, "loadend");
            }
            return JsValue.Undefined;
        }, length: 0);

        EventTargetBinding.DefineMethod(realm, proto, "getAllResponseHeaders", (thisV, _) =>
        {
            var x = XhrObject.Require(realm, thisV);
            var sb = new StringBuilder();
            foreach (var (k, v) in x.ResponseHeaders)
            {
                sb.Append(k).Append(": ").Append(v).Append("\r\n");
            }

            return JsValue.String(sb.ToString());
        }, length: 0);

        EventTargetBinding.DefineMethod(realm, proto, "getResponseHeader", (thisV, args) =>
        {
            var x = XhrObject.Require(realm, thisV);
            var name = args.Length > 0 ? JsValue.ToStringValue(args[0]).ToLowerInvariant() : "";
            foreach (var (k, v) in x.ResponseHeaders)
            {
                if (k.ToLowerInvariant() == name)
                {
                    return JsValue.String(v);
                }
            }

            return JsValue.Null;
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, proto, "overrideMimeType", (_, _) => JsValue.Undefined, length: 1);
    }

    private static void SendImpl(JsRuntime runtime, StarlingHttpClient client, XhrObject x, byte[] body)
    {
        var realm = runtime.Realm;
        var parsed = StarlingUrlParser.Parse(x.RequestUrl);
        if (parsed.IsErr)
        {
            x.ReadyState = 4;
            x.Status = 0;
            FireReadyStateChange(realm, x);
            FireEvent(realm, x, "error");
            FireEvent(realm, x, "loadend");
            return;
        }
        var hdrs = new HttpHeaders();
        foreach (var (k, v) in x.RequestHeaders.Entries())
        {
            try { hdrs.Add(k, v); } catch (Exception ex) { XhrBindingLog.InvalidHeaderSkipped(Log, ex, k); }
        }

        var wire = new HttpRequest(x.Method, parsed.Value, hdrs, body);

        x.Cts = new CancellationTokenSource();
        FireEvent(realm, x, "loadstart");

        // STARLING_DIAG_NET=1 — surface every XHR through the JS console so
        // page-level data flows (graphql calls and their status/size) are
        // observable from the browser MCP without instrumenting the page.
        var diagNet = Environment.GetEnvironmentVariable("STARLING_DIAG_NET") == "1";
        if (diagNet)
        {
            realm.ConsoleSink(ConsoleLevel.Info, $"[net:xhr] {x.Method} {x.RequestUrl}");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await client.SendAsync(wire, x.Cts.Token).ConfigureAwait(false);
                realm.Microtasks.Enqueue(() => runtime.WithActiveVm(() =>
                {
                    if (x.Aborted)
                    {
                        return;
                    }

                    if (result.IsErr)
                    {
                        if (diagNet)
                        {
                            realm.ConsoleSink(ConsoleLevel.Warn,
                                $"[net:xhr] ERR {x.Method} {x.RequestUrl}: {result.Error}");
                        }

                        x.ReadyState = 4;
                        x.Status = 0;
                        FireReadyStateChange(realm, x);
                        FireEvent(realm, x, "error");
                        FireEvent(realm, x, "loadend");
                        return;
                    }
                    var resp = result.Value;
                    if (diagNet)
                    {
                        realm.ConsoleSink(ConsoleLevel.Info,
                            $"[net:xhr] {resp.StatusCode} {x.Method} {x.RequestUrl} ({resp.Body.Length} bytes)");
                    }

                    x.Status = resp.StatusCode;
                    x.StatusText = resp.ReasonPhrase ?? "";
                    x.ResponseUrl = x.RequestUrl;
                    x.ResponseHeaders.Clear();
                    foreach (var kv in resp.Headers)
                    {
                        x.ResponseHeaders.Add((kv.Key, kv.Value));
                    }

                    x.ResponseBytes = resp.Body.ToArray();
                    x.ResponseText = Encoding.UTF8.GetString(x.ResponseBytes);
                    x.ReadyState = 2; FireReadyStateChange(realm, x);
                    x.ReadyState = 3; FireReadyStateChange(realm, x);
                    x.ReadyState = 4; FireReadyStateChange(realm, x);
                    FireEvent(realm, x, "load");
                    FireEvent(realm, x, "loadend");
                }));
            }
            catch (Exception)
            {
                realm.Microtasks.Enqueue(() => runtime.WithActiveVm(() =>
                {
                    if (x.Aborted)
                    {
                        return;
                    }

                    x.ReadyState = 4;
                    x.Status = 0;
                    FireReadyStateChange(realm, x);
                    FireEvent(realm, x, "error");
                    FireEvent(realm, x, "loadend");
                }));
            }
        });
    }

    private static void FireReadyStateChange(JsRealm realm, XhrObject x)
    {
        // Invoke onreadystatechange direct property, then dispatch event for
        // addEventListener listeners.
        var handler = x.Get("onreadystatechange");
        if (AbstractOperations.IsCallable(handler))
        {
            try { AbstractOperations.Call(realm.ActiveVm, handler, JsValue.Object(x), Array.Empty<JsValue>()); }
            catch (JsThrow ex) { realm.ConsoleSink(ConsoleLevel.Error, $"Uncaught (in XHR) {JsValue.ToStringValue(ex.Value)}"); }
        }
        FireEventOnly(realm, x, "readystatechange");
    }

    private static void FireEvent(JsRealm realm, XhrObject x, string type)
    {
        // Per-event on{type} handler.
        var handler = x.Get("on" + type);
        if (AbstractOperations.IsCallable(handler))
        {
            try { AbstractOperations.Call(realm.ActiveVm, handler, JsValue.Object(x), Array.Empty<JsValue>()); }
            catch (JsThrow ex) { realm.ConsoleSink(ConsoleLevel.Error, $"Uncaught (in XHR) {JsValue.ToStringValue(ex.Value)}"); }
        }
        FireEventOnly(realm, x, type);
    }

    private static void FireEventOnly(JsRealm realm, XhrObject x, string type)
    {
        var ev = new Starling.Dom.Events.Event(type);
        x.HostTarget.DispatchEvent(ev);
    }

    private static string ResolveUrl(string input, Document? document)
    {
        // WHATWG URL resolution — System.Uri.ToString() canonically UNESCAPES
        // the query (%7B/%22 → {/"), which corrupts percent-encoded JSON query
        // params (x.com's graphql `variables=` got 400s) and diverges from
        // every browser. The Starling parser preserves percent-escapes.
        var baseRaw = document is null ? null : WindowBinding.UrlForDocumentOrNull(document);
        global::Starling.Url.Url? baseUrl = null;
        if (baseRaw is not null)
        {
            var parsedBase = StarlingUrlParser.Parse(baseRaw);
            if (parsedBase.IsOk)
            {
                baseUrl = parsedBase.Value;
            }
        }
        var parsed = StarlingUrlParser.Parse(input, baseUrl);
        return parsed.IsOk ? parsed.Value.ToString() : input;
    }

    private static JsValue MakeArrayBuffer(JsRealm realm, byte[] bytes)
    {
        var buf = new JsArrayBuffer(realm.ArrayBufferPrototype, bytes.Length);
        bytes.CopyTo(buf.GetSpan());
        return JsValue.Object(buf);
    }

    private static JsValue SafeJsonParse(JsRealm realm, string text)
    {
        try
        {
            var json = realm.GlobalObject.Get("JSON");
            if (!json.IsObject)
            {
                return JsValue.Null;
            }

            var parse = json.AsObject.Get("parse");
            if (!AbstractOperations.IsCallable(parse))
            {
                return JsValue.Null;
            }

            return AbstractOperations.Call(realm.ActiveVm, parse, json, new[] { JsValue.String(text) });
        }
        catch (Exception ex) { XhrBindingLog.JsonParseFailed(Log, ex); return JsValue.Null; }
    }
}

internal sealed class XhrObject : JsObject
{
    public InMemoryEventTarget HostTarget { get; } = new();
    public string Method { get; set; } = "GET";
    public string RequestUrl { get; set; } = "";
    public HeadersStore RequestHeaders { get; set; } = new();
    public int ReadyState { get; set; }
    public int Status { get; set; }
    public string StatusText { get; set; } = "";
    public string ResponseText { get; set; } = "";
    public byte[] ResponseBytes { get; set; } = Array.Empty<byte>();
    public string ResponseUrl { get; set; } = "";
    public List<(string Name, string Value)> ResponseHeaders { get; } = new();
    public string ResponseType { get; set; } = "";
    public bool Aborted { get; set; }
    public CancellationTokenSource? Cts { get; set; }

    public XhrObject(JsObject? proto) : base(proto) { }

    public void Reset()
    {
        RequestHeaders = new HeadersStore();
        Status = 0; StatusText = ""; ResponseText = ""; ResponseBytes = Array.Empty<byte>();
        ResponseHeaders.Clear(); ResponseUrl = ""; Aborted = false;
    }

    public static XhrObject Require(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is XhrObject x)
        {
            return x;
        }

        throw new JsThrow(realm.NewTypeError("'this' is not an XMLHttpRequest"));
    }
}

internal static partial class XhrBindingLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "XHR abort: CancellationTokenSource.Cancel() failed (best-effort)")]
    public static partial void AbortCancelFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "skipping XHR request header '{Name}' with invalid characters")]
    public static partial void InvalidHeaderSkipped(ILogger logger, Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "XHR JSON response parse failed; returning null")]
    public static partial void JsonParseFailed(ILogger logger, Exception ex);
}
