using System.Runtime.CompilerServices;
using Starling.Dom;
using Starling.Dom.Events;
using Starling.Js.Runtime;
using Starling.Url;

namespace Starling.Bindings;

/// <summary>
/// B5-5 — HTML §7.7 <c>history</c> + <c>popstate</c>. Installs
/// <c>window.history</c> backed by a per-realm <see cref="SessionHistory"/>
/// entry list. <c>pushState</c>/<c>replaceState</c> mutate the entry list and
/// the JS-visible URL surface (<c>location.href</c> reads through the
/// history's current entry); <c>back</c>/<c>forward</c>/<c>go</c> fire a
/// <see cref="PopStateEvent"/> on the window.
/// </summary>
/// <remarks>
/// <para><b>Sync vs. async dispatch:</b> per HTML §7.4.3, <c>popstate</c> is
/// part of "traverse the history" which queues a task. v1 fires it
/// synchronously inside the navigating method — same single-threaded outcome
/// for code that doesn't race against pending tasks, and saves us threading a
/// task source through every History entry point. Revisit if a real-world
/// site depends on the queued-task ordering.</para>
/// <para><b>Cross-document navigation:</b> setting <c>location.href</c> still
/// no-ops (see <see cref="WindowBinding"/>); this binding only manages the
/// same-document history surface that SPAs depend on.</para>
/// <para><b>URL resolution:</b> the <c>url</c> argument is resolved against
/// the current document URL via <see cref="UrlParser.Parse(string)"/> if
/// absolute, otherwise treated as a relative reference that replaces the
/// path/query/fragment. A full WHATWG basic-URL-parser pass over relative
/// inputs is out of scope here; we accept absolute URLs and root-relative
/// forms like <c>/foo?q=1</c>.</para>
/// </remarks>
public static class HistoryBinding
{
    private static readonly ConditionalWeakTable<JsRealm, SessionHistory> RealmToHistory = new();
    private static readonly ConditionalWeakTable<JsRealm, JsObject> HistoryObjectCache = new();

    /// <summary>Install <c>window.history</c> on the realm. Idempotent.</summary>
    public static void Install(JsRuntime runtime, Document document, string initialUrl)
    {
        ArgumentNullException.ThrowIfNull(runtime);
        ArgumentNullException.ThrowIfNull(document);

        var realm = runtime.Realm;
        if (HistoryObjectCache.TryGetValue(realm, out _))
        {
            return;
        }

        var history = new SessionHistory(initialUrl ?? "about:blank");
        RealmToHistory.Add(realm, history);

        var historyObj = BuildHistoryObject(realm, history, document);
        HistoryObjectCache.Add(realm, historyObj);

        var global = realm.GlobalObject;
        global.DefineOwnProperty("history",
            PropertyDescriptor.Data(JsValue.Object(historyObj), writable: true, enumerable: true, configurable: true));
    }

    /// <summary>Resolve the per-realm session history, or null if
    /// <see cref="Install"/> was never called for this realm.</summary>
    internal static SessionHistory? HistoryForRealm(JsRealm realm)
        => RealmToHistory.TryGetValue(realm, out var h) ? h : null;

    private static JsObject BuildHistoryObject(JsRealm realm, SessionHistory history, Document document)
    {
        var obj = new JsObject(realm.ObjectPrototype);

        EventTargetBinding.DefineAccessor(realm, obj, "length",
            (_, _) => JsValue.Number(history.Length));
        EventTargetBinding.DefineAccessor(realm, obj, "state",
            (_, _) => history.CurrentState);
        EventTargetBinding.DefineAccessor(realm, obj, "scrollRestoration",
            (_, _) => JsValue.String(history.ScrollRestoration),
            (_, args) =>
            {
                var v = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                if (v == "auto" || v == "manual")
                {
                    history.ScrollRestoration = v;
                }

                return JsValue.Undefined;
            });

        EventTargetBinding.DefineMethod(realm, obj, "pushState",
            (_, args) => { Mutate(realm, history, document, args, replace: false); return JsValue.Undefined; },
            length: 2);
        EventTargetBinding.DefineMethod(realm, obj, "replaceState",
            (_, args) => { Mutate(realm, history, document, args, replace: true); return JsValue.Undefined; },
            length: 2);

        EventTargetBinding.DefineMethod(realm, obj, "back",
            (_, _) => { Traverse(realm, history, -1); return JsValue.Undefined; },
            length: 0);
        EventTargetBinding.DefineMethod(realm, obj, "forward",
            (_, _) => { Traverse(realm, history, +1); return JsValue.Undefined; },
            length: 0);
        EventTargetBinding.DefineMethod(realm, obj, "go", (_, args) =>
        {
            var delta = 0;
            if (args.Length > 0 && !args[0].IsUndefined)
            {
                var n = JsValue.ToNumber(args[0]);
                if (!double.IsNaN(n) && !double.IsInfinity(n))
                {
                    delta = (int)n;
                }
            }
            // Per spec, go(0) reloads. v1 has no cross-document reload path,
            // so we no-op for delta == 0 and document the gap.
            if (delta != 0)
            {
                Traverse(realm, history, delta);
            }

            return JsValue.Undefined;
        }, length: 0);

        return obj;
    }

    private static void Mutate(JsRealm realm, SessionHistory history, Document document, JsValue[] args, bool replace)
    {
        var state = args.Length > 0 ? args[0] : JsValue.Null;
        // args[1] is the unused "title" parameter — ignore per HTML §7.7.2.
        var urlArg = args.Length > 2 ? args[2] : JsValue.Undefined;
        var resolved = ResolveUrl(history.CurrentUrl, urlArg);
        history.Mutate(state, resolved, replace);
    }

    private static void Traverse(JsRealm realm, SessionHistory history, int delta)
    {
        if (!history.TryTraverse(delta, out var newState))
        {
            return;
        }

        var hostTarget = EventTargetBinding.ResolveHost(JsValue.Object(realm.GlobalObject));
        if (hostTarget is null)
        {
            return;
        }

        var ev = new PopStateEvent("popstate") { State = newState };
        hostTarget.DispatchEvent(ev);
    }

    /// <summary>Resolve a <c>pushState</c>/<c>replaceState</c> URL argument
    /// against the current entry. Accepts absolute URLs, root-relative paths
    /// (<c>/foo</c>), query-only (<c>?q=1</c>), and fragment-only (<c>#x</c>)
    /// references; falls back to the current URL on malformed input.</summary>
    internal static string ResolveUrl(string baseUrl, JsValue arg)
    {
        if (arg.IsUndefined || arg.IsNull)
        {
            return baseUrl;
        }

        var raw = JsValue.ToStringValue(arg);
        if (raw.Length == 0)
        {
            return baseUrl;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return raw;
        }

        // Only treat as absolute if it has an explicit scheme; otherwise it's a
        // relative reference. Uri.TryCreate(..., Absolute) will happily parse
        // "/foo?q=1" as a file:// URL on macOS, which is the opposite of what
        // the spec wants — same-document relative paths should resolve against
        // the document base.
        if (HasExplicitScheme(raw) && Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        try
        {
            return new Uri(baseUri, raw).ToString();
        }
        catch
        {
            return baseUrl;
        }
    }

    private static bool HasExplicitScheme(string raw)
    {
        var colon = raw.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }
        // A scheme is ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ) per RFC 3986.
        for (var i = 0; i < colon; i++)
        {
            var ch = raw[i];
            var ok = i == 0 ? char.IsAsciiLetter(ch)
                : char.IsAsciiLetterOrDigit(ch) || ch == '+' || ch == '-' || ch == '.';
            if (!ok)
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>Per-realm session history entry list. Mutable in-place by
/// pushState / replaceState; index advances via back/forward/go.</summary>
internal sealed class SessionHistory
{
    private readonly List<HistoryEntry> _entries = new();
    private int _index;

    public SessionHistory(string initialUrl)
    {
        _entries.Add(new HistoryEntry(JsValue.Null, initialUrl));
        _index = 0;
    }

    public int Length => _entries.Count;
    public JsValue CurrentState => _entries[_index].State;
    public string CurrentUrl => _entries[_index].Url;
    public string ScrollRestoration { get; set; } = "auto";

    public void Mutate(JsValue state, string url, bool replace)
    {
        if (replace)
        {
            _entries[_index] = new HistoryEntry(state, url);
            return;
        }

        if (_index + 1 < _entries.Count)
        {
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        }

        _entries.Add(new HistoryEntry(state, url));
        _index = _entries.Count - 1;
    }

    public bool TryTraverse(int delta, out JsValue newState)
    {
        var target = _index + delta;
        if (target < 0 || target >= _entries.Count) { newState = JsValue.Null; return false; }
        _index = target;
        newState = _entries[_index].State;
        return true;
    }

    private readonly record struct HistoryEntry(JsValue State, string Url);
}
