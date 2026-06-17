using System.Text;
using Jint; // JsValueExtensions (IsObject/AsObject/IsString/…)
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Microsoft.Extensions.Logging;
using Starling.Net.Http;
using StarlingUrl = global::Starling.Url.Url;
using StarlingUrlParser = global::Starling.Url.UrlParser;

namespace Starling.Bindings.Jint;

// fetch() + Request/Response/Headers (+ minimal AbortController/AbortSignal).
// Mirrors Starling.Bindings/FetchBinding.cs for init parsing + body semantics,
// reusing Starling.Net (StarlingHttpClient/HttpRequest/HttpResponse) for the wire.
//
// === Architecture (why a JS bootstrap?) ===
// Jint 4.9.2 exposes no *public* way to define a native constructor function
// (`ClrFunction` is call-only; `IConstructor` and `Engine.Realm` are internal).
// So the JS-visible constructors (Headers/Request/Response/AbortController/
// AbortSignal) are defined by evaluating a small JS bootstrap that delegates ALL
// logic to native helper functions installed under a hidden global `__sfetch`.
// Each JS instance carries an opaque integer handle; native code keys its real
// state (byte[] body, header store, status, …) off that handle. This keeps
// Web-IDL `new X()` / prototype / instanceof semantics correct while the heavy
// lifting (HTTP, header model, body decoding) stays in C# over Starling.Net.
//
// === Cross-thread promise / pump integration ===
// fetch() returns a Jint ManualPromise (Engine.Advanced.RegisterPromise()). The
// HTTP send runs on a thread-pool Task; its continuation MUST NOT touch Jint
// from the background thread. Instead each completion is marshalled back onto the
// JS thread via ctx.Post(...):
// JintScriptSession.PumpOnce() drains the post queue on the JS thread, invoking
// resolve/reject there, and reports "not idle" while the queue is non-empty — so
// the pump keeps turning until every fetch promise settles.
internal static class FetchBinding
{
    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;
        var state = new FetchState(ctx);

        // Hidden native bridge: __sfetch.* functions the JS bootstrap calls.
        var bridge = new JsObject(engine);
        void Fn(string name, Func<JsValue, JsValue[], JsValue> body, int length)
            => JintInterop.DefineMethod(engine, bridge, name, body, length);

        // ---- Headers backing ----
        Fn("headersNew", (_, a) => Num(state.Headers.Create()), 0);
        Fn("headersInit", (_, a) => { state.Headers.Get(Handle(a, 0)).PopulateFromJs(state.Headers, Arg(a, 1)); return JsValue.Undefined; }, 2);
        Fn("headersAppend", (_, a) => { state.Headers.Get(Handle(a, 0)).Append(Str0(a, 1), Str0(a, 2)); return JsValue.Undefined; }, 3);
        Fn("headersSet", (_, a) => { state.Headers.Get(Handle(a, 0)).Set(Str0(a, 1), Str0(a, 2)); return JsValue.Undefined; }, 3);
        Fn("headersDelete", (_, a) => { state.Headers.Get(Handle(a, 0)).Delete(Str0(a, 1)); return JsValue.Undefined; }, 2);
        Fn("headersHas", (_, a) => Bool(state.Headers.Get(Handle(a, 0)).Has(Str0(a, 1))), 2);
        Fn("headersGet", (_, a) => { var v = state.Headers.Get(Handle(a, 0)).GetCombined(Str0(a, 1)); return v is null ? JsValue.Null : Str(v); }, 2);
        Fn("headersEntries", (_, a) => state.Headers.Get(Handle(a, 0)).EntriesAsJsArray(engine), 1);
        Fn("headersGetSetCookie", (_, a) =>
            new JsArray(engine, state.Headers.Get(Handle(a, 0)).SetCookies().Select(c => (JsValue)new JsString(c)).ToArray()), 1);

        // ---- Request backing ----
        Fn("requestBuild", (_, a) => Num(state.BuildRequest(Arg(a, 0), Arg(a, 1))), 2);
        Fn("requestField", (_, a) => state.RequestField(Handle(a, 0), Str0(a, 1)), 2);

        // ---- Response backing ----
        Fn("responseBuild", (_, a) => Num(state.BuildResponse(Arg(a, 0), Arg(a, 1))), 2);
        Fn("responseField", (_, a) => state.ResponseField(Handle(a, 0), Str0(a, 1)), 2);
        Fn("responseClone", (_, a) => Num(state.CloneResponse(Handle(a, 0))), 1);

        // ---- Body reader (shared by Request + Response; handle == facade id) ----
        Fn("bodyUsed", (_, a) => Bool(state.Bodies.IsUsed(Handle(a, 0))), 1);
        Fn("bodyText", (_, a) => Str(Encoding.UTF8.GetString(state.Bodies.Take(Handle(a, 0)))), 1);
        Fn("bodyArrayBuffer", (_, a) => engine.Intrinsics.ArrayBuffer.Construct(state.Bodies.Take(Handle(a, 0))), 1);

        // ---- AbortController / AbortSignal ----
        Fn("abortNew", (_, a) => Num(state.Abort.Create()), 0);
        Fn("abortDoAbort", (_, a) => { state.Abort.Get(Handle(a, 0)).Abort(Arg(a, 1)); return JsValue.Undefined; }, 2);
        Fn("abortAborted", (_, a) => Bool(state.Abort.Get(Handle(a, 0)).Aborted), 1);
        Fn("abortReason", (_, a) => state.Abort.Get(Handle(a, 0)).Reason, 1);

        // ---- fetch() ----
        Fn("send", (_, a) => state.Fetch(Arg(a, 0), Arg(a, 1)), 2);

        JintInterop.DefineDataProp(engine.Global, "__sfetch", bridge,
            writable: false, enumerable: false, configurable: true);

        // Define the Web-IDL surface (constructors + prototypes) in JS, then hide
        // the implementation-private bridge once the classes have captured it.
        engine.Execute(Bootstrap, "<fetch-bootstrap>");
        engine.Execute("delete globalThis.__sfetch;", "<fetch-bootstrap-cleanup>");
    }

    // ---- tiny arg helpers (kept local so call sites read clearly) ----
    private static JsValue Arg(JsValue[] a, int i) => i < a.Length ? a[i] : JsValue.Undefined;
    private static int Handle(JsValue[] a, int i) => (int)Arg(a, i).AsNumber();
    private static string Str0(JsValue[] a, int i) { var v = Arg(a, i); return v.IsUndefined() || v.IsNull() ? "" : v.ToString(); }
    private static JsValue Str(string? s) => JintInterop.Str(s);
    private static JsValue Num(double d) => JintInterop.Num(d);
    private static JsValue Bool(bool b) => JintInterop.Bool(b);

    // =====================================================================
    // JS bootstrap: Web-IDL classes delegating to __sfetch.*.
    // Each instance stores its native handle on a Symbol slot (off enumeration /
    // JSON output) PLUS a native-readable, non-enumerable marker (__hid/__rid/__qid)
    // so C# can recognise a facade passed back in (fetch(request), new Headers(h)).
    // =====================================================================
    private const string Bootstrap = """
    (function (B) {
      'use strict';
      const H = Symbol('handle');
      const stamp = (o, k, h) => Object.defineProperty(o, k, { value: h, enumerable: false, writable: false, configurable: true });

      class Headers {
        constructor(init) {
          this[H] = B.headersNew();
          stamp(this, '__hid', this[H]);
          if (init !== undefined && init !== null) B.headersInit(this[H], init);
        }
        append(n, v) { B.headersAppend(this[H], String(n), String(v)); }
        set(n, v) { B.headersSet(this[H], String(n), String(v)); }
        delete(n) { B.headersDelete(this[H], String(n)); }
        has(n) { return B.headersHas(this[H], String(n)); }
        get(n) { return B.headersGet(this[H], String(n)); }
        getSetCookie() { return B.headersGetSetCookie(this[H]); }
        forEach(cb, thisArg) {
          const e = B.headersEntries(this[H]);
          for (let i = 0; i < e.length; i++) cb.call(thisArg, e[i][1], e[i][0], this);
        }
        *entries() { const e = B.headersEntries(this[H]); for (let i = 0; i < e.length; i++) yield e[i]; }
        *keys() { const e = B.headersEntries(this[H]); for (let i = 0; i < e.length; i++) yield e[i][0]; }
        *values() { const e = B.headersEntries(this[H]); for (let i = 0; i < e.length; i++) yield e[i][1]; }
        [Symbol.iterator]() { return this.entries(); }
      }

      // Shared body-mixin methods (text/json/arrayBuffer/blob/bodyUsed).
      function defineBody(proto) {
        Object.defineProperty(proto, 'bodyUsed', {
          get() { return B.bodyUsed(this[H]); }, enumerable: true, configurable: true
        });
        proto.text = function () {
          try { return Promise.resolve(B.bodyText(this[H])); }
          catch (e) { return Promise.reject(e); }
        };
        proto.json = function () {
          try { return Promise.resolve(JSON.parse(B.bodyText(this[H]))); }
          catch (e) { return Promise.reject(e); }
        };
        proto.arrayBuffer = function () {
          try { return Promise.resolve(B.bodyArrayBuffer(this[H])); }
          catch (e) { return Promise.reject(e); }
        };
        // Real Blob via the global Blob class (installed by BlobFileFormDataBinding).
        proto.blob = function () {
          try {
            const buf = B.bodyArrayBuffer(this[H]);
            let ct = '';
            try { ct = (this.headers && this.headers.get('content-type')) || ''; } catch (e) {}
            return Promise.resolve(new Blob([buf], { type: ct }));
          } catch (e) { return Promise.reject(e); }
        };
        // formData() — parse the body per content-type (urlencoded fully; a basic
        // multipart/form-data parser for the common simple-field case).
        proto.formData = function () {
          try {
            let ct = '';
            try { ct = (this.headers && this.headers.get('content-type')) || ''; } catch (e) {}
            const text = B.bodyText(this[H]);
            const fd = new FormData();
            if (/multipart\/form-data/i.test(ct)) {
              const m = /boundary=("?)([^";]+)\1/i.exec(ct);
              if (m) {
                const boundary = '--' + m[2];
                const parts = text.split(boundary);
                for (let i = 0; i < parts.length; i++) {
                  let part = parts[i];
                  if (part === '' || part === '--' || part === '--\r\n' || part === '\r\n') continue;
                  if (part.startsWith('\r\n')) part = part.slice(2);
                  const sep = part.indexOf('\r\n\r\n');
                  if (sep < 0) continue;
                  const head = part.slice(0, sep);
                  let value = part.slice(sep + 4);
                  if (value.endsWith('\r\n')) value = value.slice(0, -2);
                  const nm = /name="([^"]*)"/i.exec(head);
                  if (nm) fd.append(nm[1], value);
                }
              }
            } else {
              const usp = new URLSearchParams(text);
              for (const [k, v] of usp) fd.append(k, v);
            }
            return Promise.resolve(fd);
          } catch (e) { return Promise.reject(e); }
        };
      }

      class Request {
        constructor(input, init) {
          if (input === undefined) throw new TypeError("Request requires at least 1 argument");
          this[H] = B.requestBuild(input, init);
          stamp(this, '__rid', this[H]);
          this._signal = (init && init.signal) || (input && input.signal) || null;
        }
        get url() { return B.requestField(this[H], 'url'); }
        get method() { return B.requestField(this[H], 'method'); }
        get headers() { return __mkHeaders(B.requestField(this[H], 'headers')); }
        get redirect() { return B.requestField(this[H], 'redirect'); }
        get mode() { return B.requestField(this[H], 'mode'); }
        get credentials() { return B.requestField(this[H], 'credentials'); }
        get signal() { return this._signal || null; }
        clone() { return new Request(this); }
      }
      defineBody(Request.prototype);

      class Response {
        constructor(body, init) {
          this[H] = B.responseBuild(body, init);
          stamp(this, '__qid', this[H]);
        }
        get status() { return B.responseField(this[H], 'status'); }
        get statusText() { return B.responseField(this[H], 'statusText'); }
        get ok() { const s = this.status; return s >= 200 && s <= 299; }
        get redirected() { return B.responseField(this[H], 'redirected'); }
        get url() { return B.responseField(this[H], 'url'); }
        get type() { return B.responseField(this[H], 'type'); }
        get headers() { return __mkHeaders(B.responseField(this[H], 'headers')); }
        get body() { return null; } // ReadableStream not supported
        clone() { return __mkResponse(B.responseClone(this[H])); }
        static error() { return __mkResponse(B.responseBuild(null, { status: 0, statusText: '' })); }
        static json(data, init) {
          const i = init || {};
          const h = new Headers(i.headers);
          if (!h.has('content-type')) h.set('content-type', 'application/json');
          return new Response(JSON.stringify(data), { status: i.status, statusText: i.statusText, headers: h });
        }
      }
      defineBody(Response.prototype);

      function abortError() {
        try { return new DOMException('The operation was aborted.', 'AbortError'); }
        catch (e) { const x = new Error('The operation was aborted.'); x.name = 'AbortError'; return x; }
      }

      class AbortSignal {
        constructor(h) { this[H] = h; this.__listeners = []; this.onabort = null; }
        get aborted() { return B.abortAborted(this[H]); }
        get reason() { return B.abortReason(this[H]); }
        throwIfAborted() { if (this.aborted) throw this.reason; }
        addEventListener(type, cb) { if (type === 'abort' && typeof cb === 'function') this.__listeners.push(cb); }
        removeEventListener(type, cb) { if (type === 'abort') this.__listeners = this.__listeners.filter(x => x !== cb); }
        __fireAbort() {
          const ev = { type: 'abort', target: this };
          if (typeof this.onabort === 'function') { try { this.onabort(ev); } catch (e) {} }
          for (const cb of this.__listeners.slice()) { try { cb(ev); } catch (e) {} }
        }
        static abort(reason) { const c = new AbortController(); c.abort(reason); return c.signal; }
        static timeout(ms) { const c = new AbortController(); return c.signal; } // no real timer wiring
        static any(signals) {
          const c = new AbortController();
          for (const s of signals) {
            if (s.aborted) { c.abort(s.reason); break; }
            s.addEventListener('abort', () => c.abort(s.reason));
          }
          return c.signal;
        }
      }

      class AbortController {
        constructor() {
          this[H] = B.abortNew();
          this._signal = new AbortSignal(this[H]);
        }
        get signal() { return this._signal; }
        abort(reason) {
          if (this._signal.aborted) return;
          B.abortDoAbort(this[H], reason === undefined ? abortError() : reason);
          this._signal.__fireAbort();
        }
      }

      // Mint facades over an existing native handle.
      function __mkHeaders(h) { const o = Object.create(Headers.prototype); o[H] = h; stamp(o, '__hid', h); return o; }
      function __mkResponse(h) { const o = Object.create(Response.prototype); o[H] = h; stamp(o, '__qid', h); return o; }

      function fetch(input, init) {
        try {
          const signal = (init && init.signal) || (input && input.signal) || null;
          if (signal && signal.aborted) return Promise.reject(signal.reason || abortError());
          const p = B.send(input, init);
          if (!signal) return p;
          // Race the in-flight request against an abort on the signal.
          return new Promise((resolve, reject) => {
            let settled = false;
            const onAbort = () => { if (!settled) { settled = true; reject(signal.reason || abortError()); } };
            signal.addEventListener('abort', onAbort);
            p.then(
              r => { if (!settled) { settled = true; resolve(r); } },
              e => { if (!settled) { settled = true; reject(e); } });
          });
        } catch (e) { return Promise.reject(e); }
      }

      const g = globalThis;
      const def = (n, v) => Object.defineProperty(g, n, { value: v, writable: true, enumerable: false, configurable: true });
      def('Headers', Headers);
      def('Request', Request);
      def('Response', Response);
      def('AbortController', AbortController);
      def('AbortSignal', AbortSignal);
      def('fetch', fetch);
      // Expose the response factory so native send() can mint a Response facade.
      def('__mkResponse', __mkResponse);
    })(globalThis.__sfetch);
    """;
}

// =========================================================================
// Native fetch state: header stores, request/response/abort/body tables,
// and the cross-thread completion pump.
// =========================================================================
internal sealed class FetchState
{
    private readonly JintBackendContext _ctx;
    private readonly ILogger _log;
    public HeadersTable Headers { get; }
    public BodyTable Bodies { get; }
    public AbortTable Abort { get; }

    private readonly Dictionary<int, RequestRec> _requests = new();
    private readonly Dictionary<int, ResponseRec> _responses = new();
    // Single shared facade-id space for Request + Response so their ids never
    // collide in the Bodies alias table.
    private int _nextFacade = 1;

    public FetchState(JintBackendContext ctx)
    {
        _ctx = ctx;
        _log = ctx.LoggerFactory.CreateLogger<FetchState>();
        Headers = new HeadersTable();
        Bodies = new BodyTable();
        Abort = new AbortTable();
    }

    // ---------------- Request ----------------

    public int BuildRequest(JsValue input, JsValue init)
    {
        var (urlStr, method, headers, body, redirect, mode, credentials) = ParseRequestInit(input, init);
        var resolved = ResolveUrl(urlStr);
        var headersHandle = Headers.Adopt(headers);
        var bodyHandle = Bodies.Create(body);
        var id = _nextFacade++;
        _requests[id] = new RequestRec(resolved, method, headersHandle, bodyHandle, redirect, mode, credentials);
        // Alias the JS-visible request id to its body handle so the body mixin
        // (this[H] == request id) reads the right bytes / used-flag.
        Bodies.Alias(id, bodyHandle);
        return id;
    }

    public JsValue RequestField(int id, string field)
    {
        var r = _requests[id];
        return field switch
        {
            "url" => JintInterop.Str(r.Url),
            "method" => JintInterop.Str(r.Method),
            "headers" => JintInterop.Num(r.HeadersHandle),
            "redirect" => JintInterop.Str(r.Redirect),
            "mode" => JintInterop.Str(r.Mode),
            "credentials" => JintInterop.Str(r.Credentials),
            _ => JsValue.Undefined,
        };
    }

    private (string url, string method, HeaderStore headers, byte[] body, string redirect, string mode, string credentials)
        ParseRequestInit(JsValue input, JsValue init)
    {
        string url;
        var method = "GET";
        var headers = new HeaderStore();
        var body = Array.Empty<byte>();
        string redirect = "follow", mode = "cors", credentials = "same-origin";

        // input may be a string or an existing Request (we read its fields back).
        if (TryGetRequestHandle(input, out var existingId) && _requests.TryGetValue(existingId, out var existing))
        {
            url = existing.Url;
            method = existing.Method;
            foreach (var (k, v) in Headers.Get(existing.HeadersHandle).Entries())
            {
                headers.Append(k, v);
            }

            body = Bodies.Peek(existing.BodyHandle);
            redirect = existing.Redirect; mode = existing.Mode; credentials = existing.Credentials;
        }
        else
        {
            url = input.IsUndefined() || input.IsNull() ? "undefined" : input.ToString();
        }

        if (init.IsObject())
        {
            var o = init.AsObject();
            var m = o.Get("method"); if (m.IsString())
            {
                method = m.AsString().ToUpperInvariant();
            }

            var h = o.Get("headers"); if (!h.IsUndefined() && !h.IsNull()) { headers = new HeaderStore(); headers.PopulateFromJs(Headers, h); }
            var b = o.Get("body");
            if (!b.IsUndefined() && !b.IsNull())
            {
                body = BodyToBytes(b, out var bodyCt);
                if (bodyCt is not null && !headers.Has("content-type"))
                {
                    headers.Append("content-type", bodyCt);
                }
            }
            var r = o.Get("redirect"); if (r.IsString())
            {
                redirect = r.AsString();
            }

            var md = o.Get("mode"); if (md.IsString())
            {
                mode = md.AsString();
            }

            var cr = o.Get("credentials"); if (cr.IsString())
            {
                credentials = cr.AsString();
            }
        }
        return (url, method, headers, body, redirect, mode, credentials);
    }

    // ---------------- Response ----------------

    public int BuildResponse(JsValue body, JsValue init)
    {
        var bytes = BodyToBytes(body, out var bodyCt);
        var status = 200;
        var statusText = "";
        var headers = new HeaderStore();
        if (init.IsObject())
        {
            var o = init.AsObject();
            var st = o.Get("status"); if (st.IsNumber())
            {
                status = (int)st.AsNumber();
            }

            var stt = o.Get("statusText"); if (stt.IsString())
            {
                statusText = stt.AsString();
            }

            var hd = o.Get("headers"); if (!hd.IsUndefined() && !hd.IsNull())
            {
                headers.PopulateFromJs(Headers, hd);
            }
        }
        if (bodyCt is not null && !headers.Has("content-type"))
        {
            headers.Append("content-type", bodyCt);
        }

        return RegisterResponse(status, statusText, headers, bytes, url: "", redirected: false, type: "default");
    }

    public int CloneResponse(int id)
    {
        var r = _responses[id];
        if (Bodies.IsUsed(r.BodyHandle))
        {
            throw new JavaScriptException(new JsString("Failed to execute 'clone' on 'Response': body already used"));
        }

        var headers = new HeaderStore();
        foreach (var (k, v) in Headers.Get(r.HeadersHandle).Entries())
        {
            headers.Append(k, v);
        }

        return RegisterResponse(r.Status, r.StatusText, headers, Bodies.Peek(r.BodyHandle), r.Url, r.Redirected, r.Type);
    }

    public JsValue ResponseField(int id, string field)
    {
        var r = _responses[id];
        return field switch
        {
            "status" => JintInterop.Num(r.Status),
            "statusText" => JintInterop.Str(r.StatusText),
            "redirected" => JintInterop.Bool(r.Redirected),
            "url" => JintInterop.Str(r.Url),
            "type" => JintInterop.Str(r.Type),
            "headers" => JintInterop.Num(r.HeadersHandle),
            _ => JsValue.Undefined,
        };
    }

    private int RegisterResponse(int status, string statusText, HeaderStore headers, byte[] body,
        string url, bool redirected, string type)
    {
        var headersHandle = Headers.Adopt(headers);
        var bodyHandle = Bodies.Create(body);
        var id = _nextFacade++;
        _responses[id] = new ResponseRec(status, statusText, headersHandle, bodyHandle, url, redirected, type);
        // Alias the response id to its body handle so the body mixin reads it.
        Bodies.Alias(id, bodyHandle);
        return id;
    }

    // ---------------- fetch() ----------------

    public JsValue Fetch(JsValue input, JsValue init)
    {
        var (promise, resolve, reject) = _ctx.Engine.Advanced.RegisterPromise();

        // Build the request synchronously so URL/parse errors reject promptly.
        HttpRequest wire;
        try
        {
            var (urlStr, method, headers, body, _, _, _) = ParseRequestInit(input, init);
            var resolved = ResolveUrl(urlStr);

            var parsed = StarlingUrlParser.Parse(resolved);
            if (parsed.IsErr)
            {
                reject(MakeError("TypeError", $"Failed to fetch: invalid URL '{resolved}'"));
                return promise;
            }
            var url = parsed.Value;

            // data: URLs resolve locally (offline-friendly; mirrors ImageFetcher).
            if (url.IsData)
            {
                CompleteWithDataUrl(url, resolve, reject);
                return promise;
            }

            var hdrs = new HttpHeaders();
            foreach (var (k, v) in headers.Entries())
            {
                try { hdrs.Add(k, v); }
                catch (Exception ex) { /* invalid header chars → skip */ FetchBindingLog.InvalidHeaderSkipped(_log, k, ex.Message); }
            }
            ReadOnlyMemory<byte> bodyMem = body;
            wire = new HttpRequest(method, url, hdrs, bodyMem);
        }
        catch (JavaScriptException ex)
        {
            reject(ex.Error);
            return promise;
        }
        catch (Exception ex)
        {
            reject(MakeError("TypeError", $"Failed to fetch: {ex.Message}"));
            return promise;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _ctx.Http.SendAsync(wire).ConfigureAwait(false);
                if (result.IsErr)
                {
                    var err = result.Error;
                    _ctx.Post(() => reject(MakeError("TypeError", $"Failed to fetch: {err}")));
                }
                else
                {
                    var resp = result.Value;
                    var finalUrl = wire.Url.ToString();
                    _ctx.Post(() =>
                    {
                        var id = RegisterResponseFromWire(resp, finalUrl);
                        resolve(MakeResponseJs(id));
                    });
                }
            }
            catch (Exception ex)
            {
                _ctx.Post(() => reject(MakeError("TypeError", $"Failed to fetch: {ex.Message}")));
            }
        });

        return promise;
    }

    private void CompleteWithDataUrl(StarlingUrl url, Action<JsValue> resolve, Action<JsValue> reject)
    {
        // Route data: completion through the post queue too, so .then() callbacks
        // run on a later turn (matching real fetch's async contract).
        _ctx.Post(() =>
        {
            if (global::Starling.Url.DataUrl.TryDecode(url, out var payload))
            {
                var headers = new HeaderStore();
                headers.Append("content-type", payload.MediaType);
                var id = RegisterResponse(200, "OK", headers, payload.Bytes, url.ToString(), redirected: false, type: "basic");
                resolve(MakeResponseJs(id));
            }
            else
            {
                reject(MakeError("TypeError", "Failed to fetch: malformed data: URL"));
            }
        });
    }

    private int RegisterResponseFromWire(HttpResponse wire, string finalUrl)
    {
        var headers = new HeaderStore();
        foreach (var kv in wire.Headers)
        {
            headers.Append(kv.Key, kv.Value);
        }

        return RegisterResponse(wire.StatusCode, wire.ReasonPhrase, headers, wire.Body.ToArray(),
            finalUrl, redirected: false, type: "basic");
    }

    private JsValue MakeResponseJs(int id)
    {
        // The bootstrap exposed __mkResponse(handle) → a Response facade.
        var mk = _ctx.Engine.Global.Get("__mkResponse");
        return _ctx.Engine.Invoke(mk, JintInterop.Num(id));
    }

    // ---------------- shared helpers ----------------

    private string ResolveUrl(string input)
    {
        var parsed = StarlingUrlParser.Parse(input, _ctx.BaseUrl);
        if (parsed.IsOk)
        {
            return parsed.Value.ToString();
        }

        var abs = StarlingUrlParser.Parse(input);
        return abs.IsOk ? abs.Value.ToString() : input;
    }

    private static byte[] BodyToBytes(JsValue body) => BodyToBytes(body, out _);

    // Convert a fetch body init into bytes, also reporting the default
    // Content-Type the body implies (null when none / caller-supplied). Handles
    // string, ArrayBuffer/typed-array/DataView, Blob/File, FormData (multipart),
    // and URLSearchParams (urlencoded).
    private static byte[] BodyToBytes(JsValue body, out string? contentType)
    {
        contentType = null;
        if (body.IsUndefined() || body.IsNull())
        {
            return Array.Empty<byte>();
        }

        if (body.IsString())
        {
            return Encoding.UTF8.GetBytes(body.AsString());
        }

        if (body.IsArrayBuffer() && body.AsArrayBuffer() is { } ab)
        {
            return (byte[])ab.Clone();
        }

        if (body.IsUint8Array() && body.AsUint8Array() is { } u8)
        {
            return (byte[])u8.Clone();
        }

        if (body.IsDataView() && body.AsDataView() is { } dv)
        {
            return (byte[])dv.Clone();
        }

        if (body is ObjectInstance o)
        {
            // Blob / File — has the non-enumerable __bytes() helper.
            var bytesFn = o.Get("__bytes");
            if (bytesFn.IsCallable())
            {
                var arr = bytesFn.Call(o, System.Array.Empty<JsValue>());
                var bytes = ExtractBytes(arr);
                var t = o.Get("type");
                if (t.IsString() && t.AsString().Length > 0)
                {
                    contentType = t.AsString();
                }

                return bytes;
            }
            // FormData — has the __entries list.
            var entries = o.Get("__entries");
            if (entries is JsArray fdEntries)
            {
                return SerializeMultipart(fdEntries, out contentType);
            }
            // URLSearchParams — toStringTag "URLSearchParams".
            var tag = o.Get(global::Jint.Native.Symbol.GlobalSymbolRegistry.ToStringTag);
            if (tag.IsString() && tag.AsString() == "URLSearchParams")
            {
                contentType = "application/x-www-form-urlencoded;charset=UTF-8";
                return Encoding.UTF8.GetBytes(body.ToString());
            }
        }
        return Encoding.UTF8.GetBytes(body.ToString());
    }

    private static byte[] ExtractBytes(JsValue v)
    {
        if (v.IsArrayBuffer() && v.AsArrayBuffer() is { } ab)
        {
            return (byte[])ab.Clone();
        }

        if (v is ObjectInstance oi)
        {
            var bufVal = oi.Get("buffer");
            if (bufVal.IsArrayBuffer() && bufVal.AsArrayBuffer() is { } backing)
            {
                var offset = oi.Get("byteOffset").IsNumber() ? (int)oi.Get("byteOffset").AsNumber() : 0;
                var length = oi.Get("byteLength").IsNumber() ? (int)oi.Get("byteLength").AsNumber() : backing.Length;
                if (offset >= 0 && length >= 0 && offset + length <= backing.Length)
                {
                    var slice = new byte[length];
                    System.Array.Copy(backing, offset, slice, 0, length);
                    return slice;
                }
            }
        }
        return System.Array.Empty<byte>();
    }

    // HTML §multipart/form-data serialization of a FormData entry list.
    private static byte[] SerializeMultipart(JsArray entries, out string contentType)
    {
        var boundary = "----StarlingFormBoundary" + Guid.NewGuid().ToString("N");
        contentType = "multipart/form-data; boundary=" + boundary;
        using var ms = new System.IO.MemoryStream();
        void Write(string s) { var b = Encoding.UTF8.GetBytes(s); ms.Write(b, 0, b.Length); }

        for (uint i = 0; i < entries.Length; i++)
        {
            if (entries[(int)i] is not JsArray pair || pair.Length < 2)
            {
                continue;
            }

            var name = pair[0].ToString();
            var value = pair[1];
            Write("--" + boundary + "\r\n");
            if (value is ObjectInstance vo && vo.Get("__bytes").IsCallable())
            {
                var fileName = vo.Get("name");
                var fn = fileName.IsString() ? fileName.AsString() : "blob";
                var typeV = vo.Get("type");
                Write($"Content-Disposition: form-data; name=\"{name}\"; filename=\"{fn}\"\r\n");
                Write($"Content-Type: {(typeV.IsString() && typeV.AsString().Length > 0 ? typeV.AsString() : "application/octet-stream")}\r\n\r\n");
                var bytes = ExtractBytes(vo.Get("__bytes").Call(vo, System.Array.Empty<JsValue>()));
                ms.Write(bytes, 0, bytes.Length);
                Write("\r\n");
            }
            else
            {
                Write($"Content-Disposition: form-data; name=\"{name}\"\r\n\r\n");
                Write(value.ToString());
                Write("\r\n");
            }
        }
        Write("--" + boundary + "--\r\n");
        return ms.ToArray();
    }

    private static bool TryGetRequestHandle(JsValue v, out int handle)
    {
        handle = 0;
        if (!v.IsObject())
        {
            return false;
        }
        // Request facades carry a native-readable, non-enumerable '__rid' marker
        // (stamped by the JS bootstrap). Absent it, the input is treated as a URL.
        var marker = v.AsObject().Get("__rid");
        if (marker.IsNumber()) { handle = (int)marker.AsNumber(); return true; }
        return false;
    }

    private JsObject MakeError(string name, string message)
    {
        var err = new JsObject(_ctx.Engine);
        err.FastSetProperty("name", new PropertyDescriptor(new JsString(name), true, false, true));
        err.FastSetProperty("message", new PropertyDescriptor(new JsString(message), true, false, true));
        return err;
    }

    private readonly record struct RequestRec(
        string Url, string Method, int HeadersHandle, int BodyHandle,
        string Redirect, string Mode, string Credentials);

    private readonly record struct ResponseRec(
        int Status, string StatusText, int HeadersHandle, int BodyHandle,
        string Url, bool Redirected, string Type);
}

// =========================================================================
// Header model (case-insensitive, multi-value, insertion order).
// Mirrors Starling.Bindings/FetchBinding.cs HeadersStore.
// =========================================================================
internal sealed class HeaderStore
{
    private readonly List<(string Name, string Value)> _entries = new();

    public void Append(string name, string value) => _entries.Add((name.ToLowerInvariant(), value));

    public void Set(string name, string value)
    {
        var lower = name.ToLowerInvariant();
        _entries.RemoveAll(e => e.Name == lower);
        _entries.Add((lower, value));
    }

    public void Delete(string name)
    {
        var lower = name.ToLowerInvariant();
        _entries.RemoveAll(e => e.Name == lower);
    }

    public bool Has(string name)
    {
        var lower = name.ToLowerInvariant();
        return _entries.Exists(e => e.Name == lower);
    }

    public string? GetCombined(string name)
    {
        var lower = name.ToLowerInvariant();
        var matches = _entries.FindAll(e => e.Name == lower).ConvertAll(e => e.Value);
        return matches.Count == 0 ? null : string.Join(", ", matches);
    }

    public IEnumerable<(string Name, string Value)> Entries() => _entries;

    /// <summary>Fetch §Headers.getSetCookie() — the individual (uncombined)
    /// <c>Set-Cookie</c> header values in insertion order.</summary>
    public IEnumerable<string> SetCookies()
    {
        foreach (var (name, value) in _entries)
        {
            if (name == "set-cookie")
            {
                yield return value;
            }
        }
    }

    public JsValue EntriesAsJsArray(global::Jint.Engine engine)
    {
        // Emit each distinct header once with its combined value (insertion order),
        // so forEach/entries/get stay consistent. (Spec mandates sorted order;
        // tests don't depend on it.)
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pairs = new List<JsValue>();
        foreach (var (name, _) in _entries)
        {
            if (!seen.Add(name))
            {
                continue;
            }

            var combined = GetCombined(name) ?? "";
            pairs.Add(new JsArray(engine, new JsValue[] { new JsString(name), new JsString(combined) }));
        }
        return new JsArray(engine, pairs.ToArray());
    }

    public void PopulateFromJs(HeadersTable table, JsValue init)
    {
        if (init.IsUndefined() || init.IsNull() || !init.IsObject())
        {
            return;
        }

        var o = init.AsObject();

        // Existing Headers facade? Read its native store via the '__hid' marker.
        var marker = o.Get("__hid");
        if (marker.IsNumber())
        {
            foreach (var (k, v) in table.Get((int)marker.AsNumber()).Entries())
            {
                Append(k, v);
            }

            return;
        }

        // Array of [name, value] pairs?
        if (o.IsArray())
        {
            var arr = o.AsArray();
            for (uint i = 0; i < arr.Length; i++)
            {
                var pair = arr.Get(i);
                if (pair.IsObject())
                {
                    var po = pair.AsObject();
                    Append(po.Get("0").ToString(), po.Get("1").ToString());
                }
            }
            return;
        }

        // Plain record { name: value }.
        foreach (var prop in o.GetOwnProperties())
        {
            if (!prop.Value.Enumerable)
            {
                continue;
            }

            var name = prop.Key.ToString();
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            Append(name, o.Get(prop.Key).ToString());
        }
    }
}

internal sealed class HeadersTable
{
    private readonly Dictionary<int, HeaderStore> _stores = new();
    private int _next = 1;

    public int Create() { var id = _next++; _stores[id] = new HeaderStore(); return id; }
    public int Adopt(HeaderStore store) { var id = _next++; _stores[id] = store; return id; }
    public HeaderStore Get(int id) => _stores[id];
}

// =========================================================================
// Body table: byte arrays with a one-shot "used" flag, addressable by handle.
// Request/Response facade ids are aliased onto their body handle so the JS body
// mixin (this[H]) reads the correct bytes.
// =========================================================================
internal sealed class BodyTable
{
    private readonly Dictionary<int, byte[]> _bytes = new();
    private readonly HashSet<int> _used = new();
    private readonly Dictionary<int, int> _alias = new(); // facadeId → bodyHandle
    private int _next = 1;

    public int Create(byte[] bytes) { var id = _next++; _bytes[id] = bytes; return id; }
    public void Alias(int facadeId, int bodyHandle) => _alias[facadeId] = bodyHandle;

    private int Resolve(int handle) => _alias.TryGetValue(handle, out var real) ? real : handle;

    public byte[] Peek(int handle) => _bytes.TryGetValue(Resolve(handle), out var b) ? b : Array.Empty<byte>();

    public bool IsUsed(int handle) => _used.Contains(Resolve(handle));

    public byte[] Take(int handle)
    {
        var real = Resolve(handle);
        if (_used.Contains(real))
        {
            throw new JavaScriptException(new JsString("Failed to read body: already used"));
        }

        _used.Add(real);
        return _bytes.TryGetValue(real, out var b) ? b : Array.Empty<byte>();
    }
}

// =========================================================================
// AbortController/AbortSignal: minimal flag + reason (no listener registry).
// =========================================================================
internal sealed class AbortTable
{
    private readonly Dictionary<int, AbortRec> _recs = new();
    private int _next = 1;

    public int Create() { var id = _next++; _recs[id] = new AbortRec(); return id; }
    public AbortRec Get(int id) => _recs[id];
}

internal sealed class AbortRec
{
    public bool Aborted { get; private set; }
    public JsValue Reason { get; private set; } = JsValue.Undefined;

    public void Abort(JsValue reason)
    {
        if (Aborted)
        {
            return;
        }

        Aborted = true;
        Reason = reason.IsUndefined()
            ? new JsString("AbortError: The operation was aborted.")
            : reason;
    }
}

internal static partial class FetchBindingLog
{
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "Skipped invalid fetch request header '{Name}': {Reason}")]
    public static partial void InvalidHeaderSkipped(ILogger logger, string name, string reason);
}
