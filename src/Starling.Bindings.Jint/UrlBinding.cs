// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using Jint;
using Jint.Native;
using Jint.Runtime;
using StarlingUrl = global::Starling.Url.Url;
using StarlingUrlParser = global::Starling.Url.UrlParser;

namespace Starling.Bindings.Jint;

/// <summary>
/// The WHATWG <c>URL</c> and <c>URLSearchParams</c> constructors on the Jint
/// backend. Mirrors the surface in
/// <c>Starling.Bindings/CoreWebApiBinding.cs</c> (the Starling-engine impl),
/// which is itself backed by <see cref="System.Uri"/> / <see cref="System.UriBuilder"/>.
/// </summary>
/// <remarks>
/// <para>
/// Angular's runtime constructs <c>new URL(...)</c> during boot. Without the
/// global the main bundle throws <c>ReferenceError: URL is not defined</c> and
/// the app never hydrates.
/// </para>
/// <para>
/// The C# bridge does the parsing (so URL semantics live in one place) and the
/// JS bootstrap defines the Web-IDL classes. <c>URLSearchParams</c> is a small
/// pure-JS multimap; <c>url.searchParams</c> stays in sync because every
/// mutation re-parses through the bridge.
/// </para>
/// </remarks>
internal static class UrlBinding
{
    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;

        var bridge = new JsObject(engine);

        void Fn(string name, Func<JsValue, JsValue[], JsValue> body, int len) =>
            JintInterop.DefineMethod(engine, bridge, name, body, len);

        // parse(input, base?) -> { href, origin, protocol, host, hostname,
        // port, pathname, search, hash } or throws a TypeError.
        Fn("parse", (_, a) =>
        {
            var input = Str(a, 0);
            var hasBase = a.Length > 1 && !a[1].IsUndefined() && !a[1].IsNull();
            var baseUrl = hasBase ? a[1].ToString() : null;
            var uri = Parse(engine, input, baseUrl);
            return Components(engine, uri);
        }, 2);

        // reparse(href, which, value) — apply one component change by rebuilding the
        // WHATWG URL record, then return the new components. The single entry point
        // keeps URL-mutation semantics in C# (and WHATWG-correct).
        Fn("withComponent", (_, a) =>
        {
            var href = Str(a, 0);
            var which = Str(a, 1);
            var value = Str(a, 2);
            var url = Parse(engine, href, null);
            url = which switch
            {
                "protocol" => url with { Scheme = value.TrimEnd(':').ToLowerInvariant() },
                "hostname" => url with { Host = value.Length == 0 ? url.Host : value },
                "host" => ApplyHost(url, value),
                "port" => ApplyPort(url, value),
                "pathname" => url with { Path = value.Length == 0 || value[0] == '/' || !url.IsSpecial ? value : "/" + value },
                "search" => url with { Query = NormalizeQuery(value) },
                "hash" => url with { Fragment = NormalizeFragment(value) },
                _ => url,
            };
            return Components(engine, url);
        }, 3);

        JintInterop.DefineDataProp(engine.Global, "__surl", bridge,
            writable: false, enumerable: false, configurable: true);

        engine.Execute(Bootstrap, "<url-bootstrap>");
        engine.Execute("delete globalThis.__surl;", "<url-bootstrap-cleanup>");
    }

    private static string Str(JsValue[] a, int i) =>
        i < a.Length && !a[i].IsUndefined() && !a[i].IsNull() ? a[i].ToString() : "";

    private static StarlingUrl Parse(Engine engine, string input, string? baseUrl)
    {
        var baseParsed = baseUrl is null ? (StarlingUrl?)null
            : (StarlingUrlParser.Parse(baseUrl) is { IsOk: true } br ? br.Value : null);
        var result = baseParsed is null ? StarlingUrlParser.Parse(input) : StarlingUrlParser.Parse(input, baseParsed);
        if (result.IsOk)
        {
            return result.Value;
        }

        throw new JavaScriptException(engine.Intrinsics.TypeError, "Invalid URL");
    }

    private static StarlingUrl ApplyHost(StarlingUrl url, string value)
    {
        if (value.Length == 0)
        {
            return url;
        }

        var hp = value.Split(':', 2);
        var host = hp[0];
        int? port = url.Port;
        if (hp.Length > 1 && int.TryParse(hp[1], NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= 65535)
        {
            port = p == url.DefaultPort ? null : p;
        }

        return url with { Host = host, Port = port };
    }

    private static StarlingUrl ApplyPort(StarlingUrl url, string value)
    {
        if (value.Length == 0)
        {
            return url with { Port = null };
        }

        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var p) && p is >= 0 and <= 65535)
        {
            return url with { Port = p == url.DefaultPort ? null : p };
        }

        return url;
    }

    private static string? NormalizeQuery(string value)
    {
        var v = value.StartsWith('?') ? value[1..] : value;
        return v.Length == 0 ? null : v;
    }

    private static string? NormalizeFragment(string value)
    {
        var v = value.StartsWith('#') ? value[1..] : value;
        return v.Length == 0 ? null : v;
    }

    private static JsObject Components(Engine engine, StarlingUrl url)
    {
        var hostname = url.Host ?? "";
        var port = url.Port?.ToString(CultureInfo.InvariantCulture) ?? "";
        var host = port.Length == 0 ? hostname : hostname + ":" + port;
        var search = url.Query is { Length: > 0 } q ? "?" + q : "";
        var hash = url.Fragment is { Length: > 0 } f ? "#" + f : "";
        // Tuple origin for http(s)/ws(s)/ftp; opaque "null" otherwise (file/data/blob/…).
        var tupleOrigin = url.Scheme is "http" or "https" or "ws" or "wss" or "ftp";
        var origin = tupleOrigin ? $"{url.Scheme}://{host}" : "null";

        var o = new JsObject(engine);
        void Set(string k, string v) =>
            JintInterop.DefineDataProp(o, k, new JsString(v), writable: true, enumerable: true, configurable: true);
        Set("href", url.ToString());
        Set("origin", origin);
        Set("protocol", url.Scheme + ":");
        Set("host", host);
        Set("hostname", hostname);
        Set("port", port);
        Set("pathname", url.Path);
        Set("search", search);
        Set("hash", hash);
        return o;
    }

    // §6 (URLSearchParams) + §4 (URL) — Web-IDL classes over __surl.
    private const string Bootstrap = """
    (function (B) {
      'use strict';

      function parseQuery(init) {
        const list = [];
        if (init === undefined || init === null || init === '') return list;
        let s = String(init);
        if (s[0] === '?') s = s.slice(1);
        if (s === '') return list;
        for (const pair of s.split('&')) {
          if (pair === '') continue;
          const eq = pair.indexOf('=');
          const k = eq < 0 ? pair : pair.slice(0, eq);
          const v = eq < 0 ? '' : pair.slice(eq + 1);
          list.push([decodeURIComponent(k.replace(/\+/g, ' ')), decodeURIComponent(v.replace(/\+/g, ' '))]);
        }
        return list;
      }

      const enc = s => encodeURIComponent(s).replace(/%20/g, '+');

      class URLSearchParams {
        #list;
        #onchange;
        constructor(init) {
          if (init && typeof init === 'object' && typeof init[Symbol.iterator] === 'function') {
            this.#list = [];
            for (const e of init) this.#list.push([String(e[0]), String(e[1])]);
          } else if (init && typeof init === 'object') {
            this.#list = Object.keys(init).map(k => [k, String(init[k])]);
          } else {
            this.#list = parseQuery(init);
          }
          this.#onchange = null;
        }
        // Internal: URL wires a callback so query edits flow back to the URL.
        __bind(cb, list) { this.#onchange = cb; if (list) this.#list = list; }
        #notify() { if (this.#onchange) this.#onchange(this.toString()); }
        append(n, v) { this.#list.push([String(n), String(v)]); this.#notify(); }
        delete(n) { n = String(n); this.#list = this.#list.filter(e => e[0] !== n); this.#notify(); }
        get(n) { n = String(n); for (const e of this.#list) if (e[0] === n) return e[1]; return null; }
        getAll(n) { n = String(n); return this.#list.filter(e => e[0] === n).map(e => e[1]); }
        has(n) { n = String(n); return this.#list.some(e => e[0] === n); }
        set(n, v) {
          n = String(n); v = String(v);
          let done = false;
          this.#list = this.#list.filter(e => {
            if (e[0] !== n) return true;
            if (!done) { e[1] = v; done = true; return true; }
            return false;
          });
          if (!done) this.#list.push([n, v]);
          this.#notify();
        }
        sort() { this.#list.sort((a, b) => a[0] < b[0] ? -1 : a[0] > b[0] ? 1 : 0); this.#notify(); }
        forEach(cb, thisArg) { for (const e of this.#list) cb.call(thisArg, e[1], e[0], this); }
        *entries() { for (const e of this.#list) yield [e[0], e[1]]; }
        *keys() { for (const e of this.#list) yield e[0]; }
        *values() { for (const e of this.#list) yield e[1]; }
        [Symbol.iterator]() { return this.entries(); }
        get size() { return this.#list.length; }
        toString() { return this.#list.map(e => enc(e[0]) + '=' + enc(e[1])).join('&'); }
      }

      class URL {
        #c;
        #sp;
        constructor(input, base) {
          if (input === undefined) throw new TypeError('URL requires an input');
          this.#c = B.parse(String(input), base);
          this.#wireSearchParams();
        }
        #wireSearchParams() {
          this.#sp = new URLSearchParams(this.#c.search);
          const self = this;
          this.#sp.__bind(function (q) {
            self.#c = B.withComponent(self.#c.href, 'search', q.length ? '?' + q : '');
          });
        }
        get href() { return this.#c.href; }
        set href(v) { this.#c = B.parse(String(v), undefined); this.#wireSearchParams(); }
        get origin() { return this.#c.origin; }
        get protocol() { return this.#c.protocol; }
        set protocol(v) { this.#c = B.withComponent(this.#c.href, 'protocol', String(v)); }
        get host() { return this.#c.host; }
        set host(v) { this.#c = B.withComponent(this.#c.href, 'host', String(v)); }
        get hostname() { return this.#c.hostname; }
        set hostname(v) { this.#c = B.withComponent(this.#c.href, 'hostname', String(v)); }
        get port() { return this.#c.port; }
        set port(v) { this.#c = B.withComponent(this.#c.href, 'port', String(v)); }
        get pathname() { return this.#c.pathname; }
        set pathname(v) { this.#c = B.withComponent(this.#c.href, 'pathname', String(v)); }
        get search() { return this.#c.search; }
        set search(v) { this.#c = B.withComponent(this.#c.href, 'search', String(v)); this.#wireSearchParams(); }
        get hash() { return this.#c.hash; }
        set hash(v) { this.#c = B.withComponent(this.#c.href, 'hash', String(v)); }
        get searchParams() { return this.#sp; }
        toString() { return this.#c.href; }
        toJSON() { return this.#c.href; }
      }

      const g = globalThis;
      const def = (name, value) => Object.defineProperty(g, name, {
        value, writable: true, enumerable: false, configurable: true
      });
      def('URL', URL);
      def('URLSearchParams', URLSearchParams);
    })(globalThis.__surl);
    """;
}
