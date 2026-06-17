using System.Runtime.CompilerServices;
using Starling.Dom;
using Starling.Js.Runtime;
using Starling.Net.Http.Cookies;
using Starling.Url;
using StarlingUrl = Starling.Url.Url;

namespace Starling.Bindings;

/// <summary>
/// B5-5 — HTML §7.5.4 / RFC 6265bis <c>document.cookie</c>. Routes the
/// JS-visible getter/setter through the shared <see cref="CookieJar"/> using
/// the document's current URL as the request URL, so the same storage backs
/// HTTP <c>Cookie</c> / <c>Set-Cookie</c> headers and script-driven access.
/// </summary>
/// <remarks>
/// <para><b>HttpOnly:</b> per spec, script-set cookies must not be allowed to
/// overwrite <c>HttpOnly</c> entries, and the getter must filter them out.
/// v1 honors filtering on read (the jar's per-cookie HttpOnly flag is set
/// from <c>Set-Cookie</c> headers and we omit those from the getter); the
/// write-path collision check is a follow-up since the jar doesn't yet have a
/// public "set from script" overload.</para>
/// <para><b>No jar:</b> if <see cref="WindowInstallOptions.CookieJar"/> is
/// null on install, <c>document.cookie</c> returns an empty string and the
/// setter is a no-op. This matches a sandboxed-origin behavior and avoids
/// breaking pages that read cookies defensively.</para>
/// </remarks>
public static class CookieBinding
{
    private static readonly ConditionalWeakTable<Document, CookieJar> DocToJar = new();

    /// <summary>Install the <c>cookie</c> accessor on the realm's Document
    /// prototype. Safe to call multiple times for the same realm — additional
    /// documents simply register their jar mapping.</summary>
    public static void Install(JsRuntime runtime, Document document, CookieJar? jar)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(document);

        var realm = runtime.Realm;
        if (realm.DocumentPrototype is null)
        {
            throw new InvalidOperationException("NodeBindings.Install must run before CookieBinding.Install");
        }

        if (jar is not null)
        {
            if (DocToJar.TryGetValue(document, out _))
            {
                DocToJar.Remove(document);
            }

            DocToJar.Add(document, jar);
        }

        if (realm.DocumentPrototype.HasOwn("cookie"))
        {
            return;
        }

        EventTargetBinding.DefineAccessor(realm, realm.DocumentPrototype, "cookie",
            (thisV, _) =>
            {
                var (j, url) = Resolve(thisV);
                if (j is null || url is null)
                {
                    return JsValue.String("");
                }

                return JsValue.String(j.BuildCookieHeader(url));
            },
            (thisV, args) =>
            {
                var raw = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                if (string.IsNullOrEmpty(raw))
                {
                    return JsValue.Undefined;
                }

                var (j, url) = Resolve(thisV);
                if (j is null || url is null)
                {
                    return JsValue.Undefined;
                }

                j.StoreFromHeaders(url, new[] { raw });
                return JsValue.Undefined;
            });
    }

    private static (CookieJar? Jar, StarlingUrl? Url) Resolve(JsValue thisV)
    {
        var doc = DomWrappers.UnwrapDocument(thisV);
        if (doc is null)
        {
            return (null, null);
        }

        if (!DocToJar.TryGetValue(doc, out var jar))
        {
            return (null, null);
        }

        var urlStr = WindowBinding.UrlForDocumentOrNull(doc);
        if (urlStr is null)
        {
            return (jar, null);
        }

        var parsed = UrlParser.Parse(urlStr);
        return parsed.IsOk ? (jar, parsed.Value) : (jar, null);
    }
}
