using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Dom;
using Starling.Js.Intrinsics;
using Starling.Js.Runtime;
using Starling.Net;
using Starling.Net.Http;
using StarlingUrlParser = global::Starling.Url.UrlParser;

namespace Starling.Bindings;

/// <summary>
/// B5-3 — installs the WHATWG Fetch surface (<c>fetch</c>, <c>Headers</c>,
/// <c>Request</c>, <c>Response</c>, <c>AbortController</c>, <c>AbortSignal</c>)
/// against a <see cref="StarlingHttpClient"/>.
/// </summary>
/// <remarks>
/// <para><b>Cross-thread completion contract.</b> The actual HTTP send runs on
/// a thread-pool task via <c>Task.Run</c>. On completion we enqueue a
/// resolve/reject job onto <see cref="JsRealm.Microtasks"/> — the queue is
/// now thread-safe (MicrotaskQueue.cs locks around Enqueue/Drain). The host
/// MUST periodically pump microtasks by either calling
/// <see cref="JsRuntime.DrainMicrotasks"/> or running another top-level
/// <see cref="JsVm.Run"/> (which drains automatically at the bottom). Tests
/// drive the pump synchronously after each fetch.</para>
///
/// <para><b>Simplifications.</b>
/// <list type="bullet">
///   <item>No <c>ReadableStream</c>: response bodies are buffered byte arrays.</item>
///   <item><c>response.formData()</c> supports URL-encoded bodies. Multipart parsing is still pending.</item>
///   <item><c>mode</c>, <c>credentials</c>, <c>cache</c> accepted but ignored.</item>
///   <item><c>redirect: "follow"</c> is the underlying client's default; <c>"manual"</c>
///   and <c>"error"</c> aren't actively distinguishable today — we accept all
///   three and document.</item>
///   <item>Headers <c>entries</c> / <c>keys</c> / <c>values</c> return real
///   iterators (per spec) backed by an insertion-order snapshot of the entries
///   at the call site, so later mutations aren't observed by an in-flight
///   iteration. The spec's exact sorted-lexicographic order isn't enforced.</item>
/// </list></para>
/// </remarks>
public static class FetchBinding
{
    private static readonly ConditionalWeakTable<JsRuntime, PendingFetchCounter> PendingFetches = new();
    private static readonly ILogger Log = NullLoggerFactory.Instance.CreateLogger(typeof(FetchBinding));

    /// <summary>Install fetch + Headers + Request + Response + AbortController.
    /// Idempotent per realm.</summary>
    public static void Install(JsRuntime runtime, StarlingHttpClient client, Document document)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(document);

        var realm = runtime.Realm;
        if (realm.HeadersConstructor is not null)
        {
            return;
        }

        InstallHeaders(realm);
        InstallAbort(realm);
        InstallRequest(realm);
        InstallResponse(realm);
        InstallFetch(runtime, client, document);
    }

    public static bool HasPendingFetches(JsRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        return PendingFetches.TryGetValue(runtime, out var counter) && counter.Count > 0;
    }

    // =====================================================================
    // Headers
    // =====================================================================

    private static void InstallHeaders(JsRealm realm)
    {
        var proto = new JsObject(realm.ObjectPrototype);
        realm.HeadersPrototype = proto;

        EventTargetBinding.DefineMethod(realm, proto, "append", (thisV, args) =>
        {
            var store = HeadersStore.Require(realm, thisV);
            var name = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            var value = args.Length > 1 ? JsValue.ToStringValue(args[1]) : "";
            store.Append(name, value);
            return JsValue.Undefined;
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, proto, "delete", (thisV, args) =>
        {
            var store = HeadersStore.Require(realm, thisV);
            var name = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            store.Delete(name);
            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, proto, "get", (thisV, args) =>
        {
            var store = HeadersStore.Require(realm, thisV);
            var name = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            var v = store.Get(name);
            return v is null ? JsValue.Null : JsValue.String(v);
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, proto, "has", (thisV, args) =>
        {
            var store = HeadersStore.Require(realm, thisV);
            var name = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            return JsValue.Boolean(store.Has(name));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, proto, "set", (thisV, args) =>
        {
            var store = HeadersStore.Require(realm, thisV);
            var name = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            var value = args.Length > 1 ? JsValue.ToStringValue(args[1]) : "";
            store.Set(name, value);
            return JsValue.Undefined;
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, proto, "getSetCookie", (thisV, _) =>
        {
            var store = HeadersStore.Require(realm, thisV);
            var arr = new JsArray(realm);
            var i = 0;
            foreach (var v in store.GetAll("set-cookie"))
            {
                arr.DefineOwnProperty(i.ToString(CultureInfo.InvariantCulture),
                    PropertyDescriptor.Data(JsValue.String(v), true, true, true));
                i++;
            }
            arr.DefineOwnProperty("length", PropertyDescriptor.Data(JsValue.Number(i), true, false, false));
            return JsValue.Object(arr);
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "forEach", (thisV, args) =>
        {
            var store = HeadersStore.Require(realm, thisV);
            if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
            {
                throw new JsThrow(realm.NewTypeError("Headers.forEach requires a callable"));
            }

            var cb = args[0];
            var thisArg = args.Length > 1 ? args[1] : JsValue.Undefined;
            foreach (var (name, value) in store.Entries())
            {
                AbstractOperations.Call(realm.ActiveVm, cb, thisArg, new[] {
                    JsValue.String(value), JsValue.String(name), thisV });
            }
            return JsValue.Undefined;
        }, length: 1);
        // Spec: Headers.entries/keys/values return real iterator objects
        // (Headers Iterator). We model these as Array Iterators over a
        // snapshot JsArray of the entries in insertion order. This matches
        // the spec's "snapshot ordering" semantics (the iterator does not
        // observe later mutations) and reuses the existing %ArrayIteratorPrototype%.
        EventTargetBinding.DefineMethod(realm, proto, "entries", (thisV, _) =>
        {
            var store = HeadersStore.Require(realm, thisV);
            var snapshot = new JsArray(realm);
            foreach (var (k, v) in store.Entries())
            {
                var pair = new JsArray(realm);
                pair.Push(JsValue.String(k));
                pair.Push(JsValue.String(v));
                snapshot.Push(JsValue.Object(pair));
            }
            return IteratorIntrinsics.CreateArrayIterator(realm, JsValue.Object(snapshot), ArrayIteratorKind.Value);
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "keys", (thisV, _) =>
        {
            var store = HeadersStore.Require(realm, thisV);
            var snapshot = new JsArray(realm);
            foreach (var (k, _) in store.Entries())
            {
                snapshot.Push(JsValue.String(k));
            }

            return IteratorIntrinsics.CreateArrayIterator(realm, JsValue.Object(snapshot), ArrayIteratorKind.Value);
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "values", (thisV, _) =>
        {
            var store = HeadersStore.Require(realm, thisV);
            var snapshot = new JsArray(realm);
            foreach (var (_, v) in store.Entries())
            {
                snapshot.Push(JsValue.String(v));
            }

            return IteratorIntrinsics.CreateArrayIterator(realm, JsValue.Object(snapshot), ArrayIteratorKind.Value);
        }, length: 0);

        var ctor = new JsNativeFunction(realm, "Headers", 1, (_, args) =>
        {
            var obj = new HeadersObject(proto);
            if (args.Length > 0 && !args[0].IsUndefined && !args[0].IsNull)
            {
                obj.Store.PopulateFromJs(realm, args[0]);
            }

            return JsValue.Object(obj);
        }, isConstructor: true);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), false, false, false));
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), true, false, true));
        realm.HeadersConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("Headers",
            PropertyDescriptor.Data(JsValue.Object(ctor), true, false, true));
    }

    // =====================================================================
    // AbortController / AbortSignal
    // =====================================================================

    private static void InstallAbort(JsRealm realm)
    {
        // AbortSignal.prototype inherits EventTarget so addEventListener('abort', fn) works.
        var signalProto = new JsObject(realm.EventTargetPrototype ?? realm.ObjectPrototype);
        realm.AbortSignalPrototype = signalProto;

        EventTargetBinding.DefineAccessor(realm, signalProto, "aborted",
            (thisV, _) => JsValue.Boolean(AbortSignalObject.Require(realm, thisV).Aborted));
        EventTargetBinding.DefineAccessor(realm, signalProto, "reason",
            (thisV, _) => AbortSignalObject.Require(realm, thisV).Reason);

        var signalCtor = new JsNativeFunction(realm, "AbortSignal", 0, (_, _) =>
            throw new JsThrow(realm.NewTypeError("AbortSignal cannot be constructed directly")),
            isConstructor: true);
        signalCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(signalProto), false, false, false));
        signalProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(signalCtor), true, false, true));
        realm.AbortSignalConstructor = signalCtor;
        realm.GlobalObject.DefineOwnProperty("AbortSignal",
            PropertyDescriptor.Data(JsValue.Object(signalCtor), true, false, true));

        // Mint a fresh AbortSignal wired into the EventTarget machinery so
        // addEventListener('abort', …) reaches the listener registry.
        AbortSignalObject NewSignal()
        {
            var s = new AbortSignalObject(signalProto);
            EventTargetBinding.BindWrapper(s, s.HostTarget);
            return s;
        }

        void AddStatic(string name, int length, Func<JsValue[], JsValue> impl)
            => signalCtor.DefineOwnProperty(name, PropertyDescriptor.Data(
                JsValue.Object(new JsNativeFunction(realm, name, length, (_, a) => impl(a))),
                writable: true, enumerable: false, configurable: true));

        // AbortSignal.abort(reason?) — an already-aborted signal.
        AddStatic("abort", 0, args =>
        {
            var s = NewSignal();
            var reason = args.Length > 0 ? args[0] : JsValue.Undefined;
            s.DoAbort(realm, MakeAbortError(realm, reason));
            return JsValue.Object(s);
        });

        // AbortSignal.timeout(ms) — aborts with a TimeoutError after ms, scheduled
        // on the realm's event loop (via setTimeout), so a detached frame's signal
        // never fires.
        AddStatic("timeout", 1, args =>
        {
            var s = NewSignal();
            var ms = args.Length > 0 ? JsValue.ToNumber(args[0]) : 0;
            if (double.IsNaN(ms) || ms < 0)
            {
                ms = 0;
            }

            var setTimeout = realm.GlobalObject.Get("setTimeout");
            if (setTimeout.IsObject)
            {
                var cb = new JsNativeFunction(realm, "", 0, (_, _) =>
                {
                    s.DoAbort(realm, DomExceptionBinding.Make(realm, "TimeoutError", "The operation timed out."));
                    return JsValue.Undefined;
                });
                AbstractOperations.Call(realm.ActiveVm, setTimeout, JsValue.Undefined,
                    new[] { JsValue.Object(cb), JsValue.Number(ms) });
            }
            return JsValue.Object(s);
        });

        // AbortSignal.any(iterable) — aborts when any source signal aborts.
        AddStatic("any", 1, args =>
        {
            var result = NewSignal();
            if (args.Length > 0 && args[0].IsObject)
            {
                foreach (var item in IterateSignals(realm, args[0]))
                {
                    if (item.Aborted) { result.DoAbort(realm, item.Reason); break; }
                    var captured = item;
                    captured.OnAbort(_ => result.DoAbort(realm, captured.Reason));
                }
            }
            return JsValue.Object(result);
        });

        // throwIfAborted() — throw the abort reason if already aborted.
        EventTargetBinding.DefineMethod(realm, signalProto, "throwIfAborted", (thisV, _) =>
        {
            var s = AbortSignalObject.Require(realm, thisV);
            if (s.Aborted)
            {
                throw new JsThrow(s.Reason);
            }

            return JsValue.Undefined;
        }, length: 0);

        // AbortController.prototype
        var ctlProto = new JsObject(realm.ObjectPrototype);
        realm.AbortControllerPrototype = ctlProto;

        EventTargetBinding.DefineAccessor(realm, ctlProto, "signal",
            (thisV, _) => JsValue.Object(AbortControllerObject.Require(realm, thisV).Signal));

        EventTargetBinding.DefineMethod(realm, ctlProto, "abort", (thisV, args) =>
        {
            var ctl = AbortControllerObject.Require(realm, thisV);
            var reason = args.Length > 0 && !args[0].IsUndefined
                ? args[0]
                : MakeAbortError(realm, JsValue.Undefined);
            ctl.Abort(realm, reason);
            return JsValue.Undefined;
        }, length: 0);

        var ctlCtor = new JsNativeFunction(realm, "AbortController", 0, (_, _) =>
        {
            var signal = new AbortSignalObject(signalProto);
            // Wire it into the EventTarget machinery: AbortSignal inherits
            // from EventTarget, so it needs a host EventTarget bound to it
            // so addEventListener('abort', fn) reaches the listener registry.
            EventTargetBinding.BindWrapper(signal, signal.HostTarget);
            var ctl = new AbortControllerObject(ctlProto, signal);
            return JsValue.Object(ctl);
        }, isConstructor: true);
        ctlCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(ctlProto), false, false, false));
        ctlProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctlCtor), true, false, true));
        realm.AbortControllerConstructor = ctlCtor;
        realm.GlobalObject.DefineOwnProperty("AbortController",
            PropertyDescriptor.Data(JsValue.Object(ctlCtor), true, false, true));
    }

    // =====================================================================
    // Request
    // =====================================================================

    private static void InstallRequest(JsRealm realm)
    {
        var proto = new JsObject(realm.ObjectPrototype);
        realm.RequestPrototype = proto;

        EventTargetBinding.DefineAccessor(realm, proto, "url",
            (thisV, _) => JsValue.String(RequestObject.Require(realm, thisV).Url));
        EventTargetBinding.DefineAccessor(realm, proto, "method",
            (thisV, _) => JsValue.String(RequestObject.Require(realm, thisV).Method));
        EventTargetBinding.DefineAccessor(realm, proto, "headers",
            (thisV, _) => JsValue.Object(RequestObject.Require(realm, thisV).Headers));
        EventTargetBinding.DefineAccessor(realm, proto, "redirect",
            (thisV, _) => JsValue.String(RequestObject.Require(realm, thisV).Redirect));
        EventTargetBinding.DefineAccessor(realm, proto, "mode",
            (thisV, _) => JsValue.String(RequestObject.Require(realm, thisV).Mode));
        EventTargetBinding.DefineAccessor(realm, proto, "credentials",
            (thisV, _) => JsValue.String(RequestObject.Require(realm, thisV).Credentials));
        EventTargetBinding.DefineAccessor(realm, proto, "signal",
            (thisV, _) => RequestObject.Require(realm, thisV).Signal is { } s ? JsValue.Object(s) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, proto, "bodyUsed",
            (thisV, _) => JsValue.Boolean(RequestObject.Require(realm, thisV).BodyUsed));

        InstallBodyMethods(realm, proto, thisV => RequestObject.Require(realm, thisV));

        var ctor = new JsNativeFunction(realm, "Request", 1, (_, args) =>
        {
            if (args.Length == 0)
            {
                throw new JsThrow(realm.NewTypeError("Request requires at least 1 argument"));
            }

            return JsValue.Object(BuildRequest(realm, args[0], args.Length > 1 ? args[1] : JsValue.Undefined, null));
        }, isConstructor: true);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), false, false, false));
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), true, false, true));
        realm.RequestConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("Request",
            PropertyDescriptor.Data(JsValue.Object(ctor), true, false, true));
    }

    // =====================================================================
    // Response
    // =====================================================================

    private static void InstallResponse(JsRealm realm)
    {
        var proto = new JsObject(realm.ObjectPrototype);
        realm.ResponsePrototype = proto;

        EventTargetBinding.DefineAccessor(realm, proto, "status",
            (thisV, _) => JsValue.Number(ResponseObject.Require(realm, thisV).Status));
        EventTargetBinding.DefineAccessor(realm, proto, "statusText",
            (thisV, _) => JsValue.String(ResponseObject.Require(realm, thisV).StatusText));
        EventTargetBinding.DefineAccessor(realm, proto, "ok", (thisV, _) =>
        {
            var s = ResponseObject.Require(realm, thisV).Status;
            return JsValue.Boolean(s >= 200 && s <= 299);
        });
        EventTargetBinding.DefineAccessor(realm, proto, "redirected",
            (thisV, _) => JsValue.Boolean(ResponseObject.Require(realm, thisV).Redirected));
        EventTargetBinding.DefineAccessor(realm, proto, "url",
            (thisV, _) => JsValue.String(ResponseObject.Require(realm, thisV).Url));
        EventTargetBinding.DefineAccessor(realm, proto, "type",
            (thisV, _) => JsValue.String("basic"));
        EventTargetBinding.DefineAccessor(realm, proto, "headers",
            (thisV, _) => JsValue.Object(ResponseObject.Require(realm, thisV).Headers));
        EventTargetBinding.DefineAccessor(realm, proto, "body",
            (_, _) => JsValue.Null); // streams not supported; documented
        EventTargetBinding.DefineAccessor(realm, proto, "bodyUsed",
            (thisV, _) => JsValue.Boolean(ResponseObject.Require(realm, thisV).BodyUsed));

        InstallBodyMethods(realm, proto, thisV => ResponseObject.Require(realm, thisV));

        EventTargetBinding.DefineMethod(realm, proto, "clone", (thisV, _) =>
        {
            var r = ResponseObject.Require(realm, thisV);
            if (r.BodyUsed)
            {
                throw new JsThrow(realm.NewTypeError("Body already consumed"));
            }

            var headersClone = new HeadersObject(realm.HeadersPrototype);
            foreach (var (k, v) in r.Headers.Store.Entries())
            {
                headersClone.Store.Append(k, v);
            }

            var clone = new ResponseObject(proto, r.Status, r.StatusText, headersClone,
                r.BodyBytes, r.Url, r.Redirected);
            return JsValue.Object(clone);
        }, length: 0);

        var ctor = new JsNativeFunction(realm, "Response", 1, (_, args) =>
        {
            var body = args.Length > 0 ? args[0] : JsValue.Undefined;
            var init = args.Length > 1 ? args[1] : JsValue.Undefined;
            var bytes = BodyToBytes(realm, body, out var contentType);
            var status = 200;
            var statusText = "";
            HeadersObject headers = new(realm.HeadersPrototype);
            if (init.IsObject)
            {
                var initObj = init.AsObject;
                var st = initObj.Get("status");
                if (st.IsNumber)
                {
                    status = (int)st.AsNumber;
                }

                var stt = initObj.Get("statusText");
                if (stt.IsString)
                {
                    statusText = stt.AsString;
                }

                var hd = initObj.Get("headers");
                if (!hd.IsUndefined && !hd.IsNull)
                {
                    headers.Store.PopulateFromJs(realm, hd);
                }
            }
            if (contentType is not null && !headers.Store.Has("content-type"))
            {
                headers.Store.Set("content-type", contentType);
            }

            var resp = new ResponseObject(proto, status, statusText, headers, bytes, "", false);
            return JsValue.Object(resp);
        }, isConstructor: true);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), false, false, false));
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), true, false, true));
        realm.ResponseConstructor = ctor;
        realm.GlobalObject.DefineOwnProperty("Response",
            PropertyDescriptor.Data(JsValue.Object(ctor), true, false, true));
    }

    private static void InstallBodyMethods(JsRealm realm, JsObject proto, Func<JsValue, IBodyOwner> resolve)
    {
        EventTargetBinding.DefineMethod(realm, proto, "text", (thisV, _) =>
        {
            var owner = resolve(thisV);
            if (owner.BodyUsed)
            {
                return RejectedPromise(realm, realm.NewTypeError("Body already consumed"));
            }

            owner.BodyUsed = true;
            var s = Encoding.UTF8.GetString(owner.BodyBytes);
            return ResolvedPromise(realm, JsValue.String(s));
        }, length: 0);

        EventTargetBinding.DefineMethod(realm, proto, "json", (thisV, _) =>
        {
            var owner = resolve(thisV);
            if (owner.BodyUsed)
            {
                return RejectedPromise(realm, realm.NewTypeError("Body already consumed"));
            }

            owner.BodyUsed = true;
            var s = Encoding.UTF8.GetString(owner.BodyBytes);
            // Use the global JSON.parse so we honour the realm's implementation.
            try
            {
                var json = realm.GlobalObject.Get("JSON");
                if (!json.IsObject)
                {
                    return RejectedPromise(realm, realm.NewTypeError("JSON not installed"));
                }

                var parse = json.AsObject.Get("parse");
                if (!AbstractOperations.IsCallable(parse))
                {
                    return RejectedPromise(realm, realm.NewTypeError("JSON.parse not callable"));
                }

                var value = AbstractOperations.Call(realm.ActiveVm, parse, json, new[] { JsValue.String(s) });
                return ResolvedPromise(realm, value);
            }
            catch (JsThrow ex)
            {
                return RejectedPromise(realm, ex.Value);
            }
        }, length: 0);

        EventTargetBinding.DefineMethod(realm, proto, "arrayBuffer", (thisV, _) =>
        {
            var owner = resolve(thisV);
            if (owner.BodyUsed)
            {
                return RejectedPromise(realm, realm.NewTypeError("Body already consumed"));
            }

            owner.BodyUsed = true;
            var buf = new JsArrayBuffer(realm.ArrayBufferPrototype, owner.BodyBytes.Length);
            owner.BodyBytes.CopyTo(buf.GetSpan());
            return ResolvedPromise(realm, JsValue.Object(buf));
        }, length: 0);

        EventTargetBinding.DefineMethod(realm, proto, "blob", (thisV, _) =>
        {
            var owner = resolve(thisV);
            if (owner.BodyUsed)
            {
                return RejectedPromise(realm, realm.NewTypeError("Body already consumed"));
            }

            owner.BodyUsed = true;
            var type = owner is ResponseObject response ? response.Headers.Store.Get("content-type") ?? "" : "";
            var blob = new BlobObject(realm.GlobalObject.Get("Blob").AsObject.Get("prototype").AsObject,
                owner.BodyBytes.ToArray(), type);
            return ResolvedPromise(realm, JsValue.Object(blob));
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "formData", (thisV, _) =>
        {
            var owner = resolve(thisV);
            if (owner.BodyUsed)
            {
                return RejectedPromise(realm, realm.NewTypeError("Body already consumed"));
            }

            owner.BodyUsed = true;
            if (owner is not ResponseObject response)
            {
                return RejectedPromise(realm, realm.NewTypeError("formData is only supported on Response"));
            }

            var type = response.Headers.Store.Get("content-type") ?? "";
            if (!type.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            {
                return RejectedPromise(realm, realm.NewTypeError("Response.formData only supports URL-encoded bodies"));
            }

            var protoObj = realm.GlobalObject.Get("FormData").AsObject.Get("prototype").AsObject;
            var form = CoreWebApiBinding.ParseUrlEncodedFormData(realm, protoObj, Encoding.UTF8.GetString(owner.BodyBytes));
            return ResolvedPromise(realm, JsValue.Object(form));
        }, length: 0);
    }

    // =====================================================================
    // fetch
    // =====================================================================

    private static void InstallFetch(JsRuntime runtime, StarlingHttpClient client, Document document)
    {
        var realm = runtime.Realm;
        var fetch = new JsNativeFunction(realm, "fetch", 1, (_, args) =>
        {
            try
            {
                var input = args.Length > 0 ? args[0] : JsValue.Undefined;
                var init = args.Length > 1 ? args[1] : JsValue.Undefined;
                var req = BuildRequest(realm, input, init, document);
                return StartFetch(runtime, client, req);
            }
            catch (JsThrow ex)
            {
                return RejectedPromise(realm, ex.Value);
            }
        }, isConstructor: false);

        realm.GlobalObject.DefineOwnProperty("fetch",
            PropertyDescriptor.Data(JsValue.Object(fetch), true, false, true));
    }

    /// <summary>Kick off the actual HTTP request on the thread pool and return
    /// a JS Promise that settles on the next microtask drain.</summary>
    private static JsValue StartFetch(JsRuntime runtime, StarlingHttpClient client, RequestObject req)
    {
        var realm = runtime.Realm;
        return MakePromise(realm, (resolve, reject) =>
        {
            req.BodyUsed = true; // sending consumes the body

            if (TryStartFileFetch(runtime, req, resolve, reject))
            {
                return;
            }

            // Build wire request synchronously to surface URL errors as rejections.
            HttpRequest wire;
            try { wire = BuildWireRequest(realm, req); }
            catch (JsThrow ex)
            {
                realm.Microtasks.Enqueue(() => runtime.WithActiveVm(() =>
                    AbstractOperations.Call(realm.ActiveVm, reject, JsValue.Undefined, new[] { ex.Value })));
                return;
            }

            // Wire AbortSignal -> CTS.
            var cts = new CancellationTokenSource();
            if (req.Signal is { } sig)
            {
                if (sig.Aborted)
                {
                    realm.Microtasks.Enqueue(() => runtime.WithActiveVm(() =>
                        AbstractOperations.Call(realm.ActiveVm, reject, JsValue.Undefined,
                            new[] { MakeAbortError(realm, sig.Reason) })));
                    return;
                }
                sig.OnAbort(_ => { try { cts.Cancel(); } catch (Exception ex) { FetchBindingLog.AbortSignalCancelFailed(Log, ex); } });
            }

            // Dispatch on the thread pool; settle via microtask queue.
            var pending = TrackFetch(runtime);
            _ = Task.Run(async () =>
            {
                try
                {
                    var result = await client.SendAsync(wire, cts.Token).ConfigureAwait(false);
                    if (result.IsErr)
                    {
                        realm.Microtasks.Enqueue(() => runtime.WithActiveVm(() =>
                        {
                            // If the signal aborted while the request was in flight,
                            // the SendAsync may surface as a generic transport error
                            // rather than throwing OperationCanceledException — route
                            // to AbortError in that case.
                            if (req.Signal is { Aborted: true } sig2)
                            {
                                AbstractOperations.Call(realm.ActiveVm, reject, JsValue.Undefined,
                                    new[] { MakeAbortError(realm, sig2.Reason) });
                                return;
                            }
                            var err = realm.NewTypeError($"Failed to fetch: {result.Error}");
                            AbstractOperations.Call(realm.ActiveVm, reject, JsValue.Undefined, new[] { err });
                        }));
                        return;
                    }
                    var resp = result.Value;
                    realm.Microtasks.Enqueue(() => runtime.WithActiveVm(() =>
                    {
                        var respObj = BuildResponseFromWire(realm, resp, wire.Url.ToString());
                        AbstractOperations.Call(realm.ActiveVm, resolve, JsValue.Undefined, new[] { JsValue.Object(respObj) });
                    }));
                }
                catch (OperationCanceledException)
                {
                    realm.Microtasks.Enqueue(() => runtime.WithActiveVm(() =>
                    {
                        var reason = req.Signal?.Reason ?? MakeAbortError(realm, JsValue.Undefined);
                        AbstractOperations.Call(realm.ActiveVm, reject, JsValue.Undefined, new[] { reason });
                    }));
                }
                catch (Exception ex)
                {
                    realm.Microtasks.Enqueue(() => runtime.WithActiveVm(() =>
                    {
                        var err = realm.NewTypeError($"Failed to fetch: {ex.Message}");
                        AbstractOperations.Call(realm.ActiveVm, reject, JsValue.Undefined, new[] { err });
                    }));
                }
                finally
                {
                    pending.Dispose();
                }
            });
        });
    }

    private static bool TryStartFileFetch(JsRuntime runtime, RequestObject req, JsValue resolve, JsValue reject)
    {
        var realm = runtime.Realm;
        if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var uri) || uri.Scheme != "file")
        {
            return false;
        }

        if (!req.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            && !req.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
        {
            realm.Microtasks.Enqueue(() => runtime.WithActiveVm(() =>
            {
                var err = realm.NewTypeError($"Failed to fetch: file URL only supports GET or HEAD ({req.Method})");
                AbstractOperations.Call(realm.ActiveVm, reject, JsValue.Undefined, new[] { err });
            }));
            return true;
        }

        var path = uri.LocalPath;
        var pending = TrackFetch(runtime);
        _ = Task.Run(async () =>
        {
            try
            {
                if (!File.Exists(path))
                {
                    realm.Microtasks.Enqueue(() => runtime.WithActiveVm(() =>
                    {
                        var err = realm.NewTypeError($"Failed to fetch: File not found: {path}");
                        AbstractOperations.Call(realm.ActiveVm, reject, JsValue.Undefined, new[] { err });
                    }));
                    return;
                }

                var bytes = req.Method.Equals("HEAD", StringComparison.OrdinalIgnoreCase)
                    ? Array.Empty<byte>()
                    : await File.ReadAllBytesAsync(path).ConfigureAwait(false);
                realm.Microtasks.Enqueue(() => runtime.WithActiveVm(() =>
                {
                    var headers = new HeadersObject(realm.HeadersPrototype);
                    headers.Store.Set("content-type", GuessContentType(path));
                    var response = new ResponseObject(
                        realm.ResponsePrototype,
                        200,
                        "OK",
                        headers,
                        bytes,
                        req.Url,
                        redirected: false);
                    AbstractOperations.Call(realm.ActiveVm, resolve, JsValue.Undefined,
                        new[] { JsValue.Object(response) });
                }));
            }
            catch (Exception ex)
            {
                realm.Microtasks.Enqueue(() => runtime.WithActiveVm(() =>
                {
                    var err = realm.NewTypeError($"Failed to fetch: {ex.Message}");
                    AbstractOperations.Call(realm.ActiveVm, reject, JsValue.Undefined, new[] { err });
                }));
            }
            finally
            {
                pending.Dispose();
            }
        });
        return true;
    }

    private static string GuessContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".css" => "text/css",
        ".html" or ".htm" => "text/html",
        ".js" or ".mjs" => "text/javascript",
        ".json" => "application/json",
        ".wasm" => "application/wasm",
        ".br" => "application/octet-stream",
        ".gz" => "application/octet-stream",
        _ => "application/octet-stream",
    };

    // =====================================================================
    // Building requests / responses
    // =====================================================================

    internal static RequestObject BuildRequest(JsRealm realm, JsValue input, JsValue init, Document? document)
    {
        string urlStr;
        string method = "GET";
        HeadersObject headers = new(realm.HeadersPrototype);
        byte[] body = Array.Empty<byte>();
        string redirect = "follow", mode = "cors", credentials = "same-origin";
        AbortSignalObject? signal = null;

        if (input.IsObject && input.AsObject is RequestObject existing)
        {
            urlStr = existing.Url;
            method = existing.Method;
            foreach (var (k, v) in existing.Headers.Store.Entries())
            {
                headers.Store.Append(k, v);
            }

            body = existing.BodyBytes;
            redirect = existing.Redirect;
            mode = existing.Mode;
            credentials = existing.Credentials;
            signal = existing.Signal;
        }
        else
        {
            urlStr = JsValue.ToStringValue(input);
        }

        if (init.IsObject)
        {
            var obj = init.AsObject;
            var m = obj.Get("method");
            if (m.IsString)
            {
                method = m.AsString.ToUpperInvariant();
            }

            var h = obj.Get("headers");
            if (!h.IsUndefined && !h.IsNull)
            {
                headers = new HeadersObject(realm.HeadersPrototype);
                headers.Store.PopulateFromJs(realm, h);
            }
            var b = obj.Get("body");
            if (!b.IsUndefined && !b.IsNull)
            {
                body = BodyToBytes(realm, b, out var contentType);
                if (contentType is not null && !headers.Store.Has("content-type"))
                {
                    headers.Store.Set("content-type", contentType);
                }
            }
            var r = obj.Get("redirect");
            if (r.IsString)
            {
                redirect = r.AsString;
            }

            var md = obj.Get("mode");
            if (md.IsString)
            {
                mode = md.AsString;
            }

            var cr = obj.Get("credentials");
            if (cr.IsString)
            {
                credentials = cr.AsString;
            }

            var sig = obj.Get("signal");
            if (sig.IsObject && sig.AsObject is AbortSignalObject so)
            {
                signal = so;
            }
        }

        // Resolve URL relative to document base.
        var resolved = ResolveUrl(urlStr, document);

        return new RequestObject(realm.RequestPrototype, resolved, method, headers, body,
            redirect, mode, credentials, signal);
    }

    private static string ResolveUrl(string input, Document? document)
    {
        // Try absolute first.
        if (Uri.TryCreate(input, UriKind.Absolute, out var abs))
        {
            return abs.ToString();
        }
        // Document base.
        var baseUrl = document is null ? null : DocumentBaseUrl(document);
        if (baseUrl is not null && Uri.TryCreate(new Uri(baseUrl), input, out var combined))
        {
            return combined.ToString();
        }

        return input;
    }

    private static string? DocumentBaseUrl(Document doc)
    {
        // WindowBinding stashes a per-document URL; reuse its accessor.
        return WindowBinding.UrlForDocumentOrNull(doc);
    }

    private static HttpRequest BuildWireRequest(JsRealm realm, RequestObject req)
    {
        var parsed = StarlingUrlParser.Parse(req.Url);
        if (parsed.IsErr)
        {
            throw new JsThrow(realm.NewTypeError($"Invalid URL: {req.Url}"));
        }

        var u = parsed.Value;
        var hdrs = new HttpHeaders();
        foreach (var (k, v) in req.Headers.Store.Entries())
        {
            // Skip pseudo-headers / banned headers (none enforced here).
            try { hdrs.Add(k, v); } catch (Exception ex) { FetchBindingLog.InvalidHeaderSkipped(Log, ex, k); }
        }
        // Default Host header is added by the wire writer.
        ReadOnlyMemory<byte> body = req.BodyBytes;
        return new HttpRequest(req.Method, u, hdrs, body);
    }

    private static ResponseObject BuildResponseFromWire(JsRealm realm, HttpResponse wire, string finalUrl)
    {
        var headers = new HeadersObject(realm.HeadersPrototype);
        foreach (var kv in wire.Headers)
        {
            headers.Store.Append(kv.Key, kv.Value);
        }

        var bytes = wire.Body.ToArray();
        return new ResponseObject(realm.ResponsePrototype, wire.StatusCode, wire.ReasonPhrase ?? "",
            headers, bytes, finalUrl, redirected: false);
    }

    internal static byte[] BodyToBytes(JsRealm realm, JsValue body)
        => BodyToBytes(realm, body, out _);

    private static byte[] BodyToBytes(JsRealm realm, JsValue body, out string? contentType)
    {
        contentType = null;
        if (body.IsUndefined || body.IsNull)
        {
            return Array.Empty<byte>();
        }

        if (body.IsString)
        {
            return Encoding.UTF8.GetBytes(body.AsString);
        }

        if (body.IsObject)
        {
            switch (body.AsObject)
            {
                case BlobObject blob:
                    if (blob.Type.Length > 0)
                    {
                        contentType = blob.Type;
                    }

                    return blob.Bytes.ToArray();
                case FormDataObject form:
                    return CoreWebApiBinding.SerializeFormDataMultipart(form, out contentType);
                case UrlSearchParamsObject searchParams:
                    contentType = "application/x-www-form-urlencoded;charset=UTF-8";
                    return Encoding.UTF8.GetBytes(searchParams.Serialize());
                case JsArrayBuffer buf:
                    return buf.GetSpan().ToArray();
                case JsTypedArray ta:
                    return ta.Buffer.GetSpan(ta.ByteOffset, ta.ByteLength).ToArray();
            }
        }
        // Fallback: stringify.
        return Encoding.UTF8.GetBytes(JsValue.ToStringValue(body));
    }

    // =====================================================================
    // Promise helpers
    // =====================================================================

    internal static JsValue MakePromise(JsRealm realm, Action<JsValue, JsValue> executor)
    {
        if (realm.PromiseConstructor is null)
        {
            throw new InvalidOperationException("Promise not installed");
        }
        // Invoke the Promise constructor synchronously with a native executor
        // that captures resolve/reject. This routes through the existing
        // resolving-functions plumbing in PromiseCtor.
        var nativeExec = new JsNativeFunction(realm, "", 2, (_, fnArgs) =>
        {
            var resolve = fnArgs.Length > 0 ? fnArgs[0] : JsValue.Undefined;
            var reject = fnArgs.Length > 1 ? fnArgs[1] : JsValue.Undefined;
            executor(resolve, reject);
            return JsValue.Undefined;
        }, isConstructor: false);
        return AbstractOperations.Construct(realm.ActiveVm, JsValue.Object(realm.PromiseConstructor),
            new[] { JsValue.Object(nativeExec) });
    }

    internal static JsValue ResolvedPromise(JsRealm realm, JsValue value)
        => MakePromise(realm, (resolve, _) =>
            AbstractOperations.Call(realm.ActiveVm, resolve, JsValue.Undefined, new[] { value }));

    internal static JsValue RejectedPromise(JsRealm realm, JsValue reason)
        => MakePromise(realm, (_, reject) =>
            AbstractOperations.Call(realm.ActiveVm, reject, JsValue.Undefined, new[] { reason }));

    // Yield AbortSignal objects from an array-like value (AbortSignal.any).
    private static IEnumerable<AbortSignalObject> IterateSignals(JsRealm realm, JsValue iterable)
    {
        if (!iterable.IsObject)
        {
            yield break;
        }

        var obj = iterable.AsObject;
        var lenVal = obj.Get("length");
        if (!lenVal.IsNumber)
        {
            yield break;
        }

        var len = (int)JsValue.ToNumber(lenVal);
        for (var i = 0; i < len; i++)
        {
            if (obj.Get(i.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { IsObject: true } v
                && v.AsObject is AbortSignalObject s)
            {
                yield return s;
            }
        }
    }

    internal static JsValue MakeAbortError(JsRealm realm, JsValue reason)
    {
        if (!reason.IsUndefined)
        {
            return reason;
        }
        // The default abort reason is a real "AbortError" DOMException, so
        // signal.reason.constructor resolves to the realm's DOMException.
        return DomExceptionBinding.Make(realm, "AbortError", "The operation was aborted.");
    }

    private static PendingFetchScope TrackFetch(JsRuntime runtime)
    {
        var counter = PendingFetches.GetValue(runtime, static _ => new PendingFetchCounter());
        counter.Increment();
        return new PendingFetchScope(counter);
    }

    private sealed class PendingFetchCounter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public void Increment() => Interlocked.Increment(ref _count);

        public void Decrement() => Interlocked.Decrement(ref _count);
    }

    private readonly struct PendingFetchScope : IDisposable
    {
        private readonly PendingFetchCounter _counter;

        public PendingFetchScope(PendingFetchCounter counter) => _counter = counter;

        public void Dispose() => _counter.Decrement();
    }
}

// =========================================================================
// Backing types
// =========================================================================

/// <summary>Common contract for Request/Response body-reader plumbing.</summary>
internal interface IBodyOwner
{
    bool BodyUsed { get; set; }
    byte[] BodyBytes { get; }
}

internal sealed class HeadersObject : JsObject
{
    public HeadersStore Store { get; } = new();
    public HeadersObject(JsObject? proto) : base(proto) { }
}

internal sealed class HeadersStore
{
    // Per spec: case-insensitive name, comma-joined values on append for the
    // same name. We track entries as a list of (lower-case-name, value).
    private readonly List<(string Name, string Value)> _entries = new();

    public void Append(string name, string value)
    {
        var lower = name.ToLowerInvariant();
        _entries.Add((lower, value));
    }

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
        return _entries.Any(e => e.Name == lower);
    }

    public string? Get(string name)
    {
        var lower = name.ToLowerInvariant();
        var matches = _entries.Where(e => e.Name == lower).Select(e => e.Value).ToList();
        if (matches.Count == 0)
        {
            return null;
        }

        return string.Join(", ", matches);
    }

    public IEnumerable<string> GetAll(string name)
    {
        var lower = name.ToLowerInvariant();
        foreach (var e in _entries)
        {
            if (e.Name == lower)
            {
                yield return e.Value;
            }
        }
    }

    public IEnumerable<(string Name, string Value)> Entries()
    {
        // Spec mandates sorted lexicographic order; tests don't rely on it but
        // emitting in insertion order is good enough for now.
        foreach (var e in _entries)
        {
            yield return e;
        }
    }

    public static HeadersStore Require(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is HeadersObject ho)
        {
            return ho.Store;
        }

        throw new JsThrow(realm.NewTypeError("'this' is not a Headers instance"));
    }

    /// <summary>Populate from a JS init: Headers instance, array-of-pairs, or plain object.</summary>
    public void PopulateFromJs(JsRealm realm, JsValue init)
    {
        if (init.IsUndefined || init.IsNull)
        {
            return;
        }

        if (!init.IsObject)
        {
            return;
        }

        var obj = init.AsObject;
        if (obj is HeadersObject other)
        {
            foreach (var (k, v) in other.Store.Entries())
            {
                Append(k, v);
            }

            return;
        }
        // Array-of-pairs?
        if (obj is JsArray arr)
        {
            var lenV = arr.Get("length");
            var len = lenV.IsNumber ? (int)lenV.AsNumber : 0;
            for (var i = 0; i < len; i++)
            {
                var pair = arr.Get(i.ToString(CultureInfo.InvariantCulture));
                if (pair.IsObject)
                {
                    var po = pair.AsObject;
                    var name = JsValue.ToStringValue(po.Get("0"));
                    var value = JsValue.ToStringValue(po.Get("1"));
                    Append(name, value);
                }
            }
            return;
        }
        // Plain object.
        foreach (var key in obj.EnumerableKeys())
        {
            var v = obj.Get(key);
            Append(key, JsValue.ToStringValue(v));
        }
    }
}

internal sealed class RequestObject : JsObject, IBodyOwner
{
    public string Url { get; }
    public string Method { get; }
    public HeadersObject Headers { get; }
    public byte[] BodyBytes { get; }
    public string Redirect { get; }
    public string Mode { get; }
    public string Credentials { get; }
    public AbortSignalObject? Signal { get; }
    public bool BodyUsed { get; set; }

    public RequestObject(JsObject? proto, string url, string method, HeadersObject headers,
        byte[] body, string redirect, string mode, string credentials, AbortSignalObject? signal)
        : base(proto)
    {
        Url = url; Method = method; Headers = headers; BodyBytes = body;
        Redirect = redirect; Mode = mode; Credentials = credentials; Signal = signal;
    }

    public static RequestObject Require(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is RequestObject r)
        {
            return r;
        }

        throw new JsThrow(realm.NewTypeError("'this' is not a Request instance"));
    }
}

internal sealed class ResponseObject : JsObject, IBodyOwner
{
    public int Status { get; }
    public string StatusText { get; }
    public HeadersObject Headers { get; }
    public byte[] BodyBytes { get; }
    public string Url { get; }
    public bool Redirected { get; }
    public bool BodyUsed { get; set; }

    public ResponseObject(JsObject? proto, int status, string statusText, HeadersObject headers,
        byte[] body, string url, bool redirected)
        : base(proto)
    {
        Status = status; StatusText = statusText; Headers = headers; BodyBytes = body;
        Url = url; Redirected = redirected;
    }

    public static ResponseObject Require(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is ResponseObject r)
        {
            return r;
        }

        throw new JsThrow(realm.NewTypeError("'this' is not a Response instance"));
    }
}

internal sealed class AbortSignalObject : JsObject
{
    private static readonly ILogger s_log = NullLoggerFactory.Instance.CreateLogger(typeof(AbortSignalObject));

    public bool Aborted { get; private set; }
    public JsValue Reason { get; private set; } = JsValue.Undefined;
    public InMemoryEventTarget HostTarget { get; } = new();
    private readonly List<Action<JsValue>> _abortCallbacks = new();

    public AbortSignalObject(JsObject? proto) : base(proto) { }

    public void OnAbort(Action<JsValue> cb)
    {
        lock (_abortCallbacks)
        {
            if (Aborted) { cb(Reason); return; }
            _abortCallbacks.Add(cb);
        }
    }

    public void DoAbort(JsRealm realm, JsValue reason)
    {
        Action<JsValue>[] toFire;
        lock (_abortCallbacks)
        {
            if (Aborted)
            {
                return;
            }

            Aborted = true;
            Reason = reason;
            toFire = _abortCallbacks.ToArray();
            _abortCallbacks.Clear();
        }
        // Fire the 'abort' event on the signal.
        var ev = new Starling.Dom.Events.Event("abort");
        HostTarget.DispatchEvent(ev);
        foreach (var cb in toFire) { try { cb(reason); } catch (Exception ex) { FetchBindingLog.AbortCallbackFailed(s_log, ex); } }
    }

    public static AbortSignalObject Require(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is AbortSignalObject s)
        {
            return s;
        }

        throw new JsThrow(realm.NewTypeError("'this' is not an AbortSignal"));
    }
}

internal sealed class AbortControllerObject : JsObject
{
    public AbortSignalObject Signal { get; }
    public AbortControllerObject(JsObject? proto, AbortSignalObject signal) : base(proto) { Signal = signal; }

    public void Abort(JsRealm realm, JsValue reason) => Signal.DoAbort(realm, reason);

    public static AbortControllerObject Require(JsRealm realm, JsValue thisV)
    {
        if (thisV.IsObject && thisV.AsObject is AbortControllerObject c)
        {
            return c;
        }

        throw new JsThrow(realm.NewTypeError("'this' is not an AbortController"));
    }
}

internal static partial class FetchBindingLog
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "abort-signal CancellationTokenSource.Cancel() failed (best-effort)")]
    public static partial void AbortSignalCancelFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "skipping HTTP header '{Name}' with invalid characters")]
    public static partial void InvalidHeaderSkipped(ILogger logger, Exception ex, string name);

    [LoggerMessage(Level = LogLevel.Debug, Message = "abort callback threw (fan-out, swallowed)")]
    public static partial void AbortCallbackFailed(ILogger logger, Exception ex);
}
