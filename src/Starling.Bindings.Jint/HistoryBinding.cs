using System.Runtime.CompilerServices;
using Jint;
using Jint.Native;
using Starling.Dom.Events;

namespace Starling.Bindings.Jint;

/// <summary>
/// HTML §7.7 <c>window.history</c> + <c>popstate</c> on the Jint backend.
/// Mirrors <c>Starling.Bindings/HistoryBinding.cs</c>: per-session
/// <see cref="SessionHistory"/> with <c>pushState</c>/<c>replaceState</c>
/// mutating an entry list, <c>back</c>/<c>forward</c>/<c>go</c> traversing it
/// and dispatching a synthetic <c>popstate</c> Event on the window host.
/// </summary>
/// <remarks>
/// Sync dispatch (no task queueing) and same-document only — matches the
/// Starling backend's v1 behavior. <c>location.href</c> reads UrlFor which
/// consults the history's current entry via <see cref="CurrentUrlFor"/>.
/// </remarks>
internal static class HistoryBinding
{
    private static readonly ConditionalWeakTable<JintBackendContext, SessionHistory> Histories = new();

    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;

        var initialUrl = ctx.BaseUrl?.ToString() ?? "about:blank";
        var history = new SessionHistory(initialUrl);
        if (!Histories.TryAdd(ctx, history))
        {
            // already installed for this ctx — keep the existing history.
            Histories.TryGetValue(ctx, out history!);
        }

        var historyObj = new JsObject(engine);

        JintInterop.DefineAccessor(engine, historyObj, "length",
            (_, _) => JintInterop.Num(history!.Length));
        JintInterop.DefineAccessor(engine, historyObj, "state",
            (_, _) => history!.CurrentState);
        JintInterop.DefineAccessor(engine, historyObj, "scrollRestoration",
            (_, _) => JintInterop.Str(history!.ScrollRestoration),
            (_, args) =>
            {
                var v = args.Length > 0 ? args[0].ToString() : "";
                if (v is "auto" or "manual")
                {
                    history!.ScrollRestoration = v;
                }

                return JsValue.Undefined;
            });

        JintInterop.DefineMethod(engine, historyObj, "pushState",
            (_, args) => { Mutate(history!, args, replace: false); return JsValue.Undefined; }, length: 2);
        JintInterop.DefineMethod(engine, historyObj, "replaceState",
            (_, args) => { Mutate(history!, args, replace: true); return JsValue.Undefined; }, length: 2);

        JintInterop.DefineMethod(engine, historyObj, "back",
            (_, _) => { Traverse(ctx, history!, -1); return JsValue.Undefined; }, length: 0);
        JintInterop.DefineMethod(engine, historyObj, "forward",
            (_, _) => { Traverse(ctx, history!, +1); return JsValue.Undefined; }, length: 0);
        JintInterop.DefineMethod(engine, historyObj, "go", (_, args) =>
        {
            var delta = 0;
            if (args.Length > 0 && !args[0].IsUndefined())
            {
                var n = global::Jint.Runtime.TypeConverter.ToNumber(args[0]);
                if (!double.IsNaN(n) && !double.IsInfinity(n))
                {
                    delta = (int)n;
                }
            }
            // go(0) is reload per spec; cross-document reload is not wired.
            if (delta != 0)
            {
                Traverse(ctx, history!, delta);
            }

            return JsValue.Undefined;
        }, length: 0);

        JintInterop.DefineDataProp(engine.Global, "history", historyObj,
            writable: true, enumerable: true, configurable: true);
    }

    /// <summary>Used by WindowBinding.UrlFor to reflect pushState into
    /// <c>location.href</c>. Returns <c>null</c> when no history has been
    /// installed for this context.</summary>
    internal static string? CurrentUrlFor(JintBackendContext ctx)
        => Histories.TryGetValue(ctx, out var h) ? h.CurrentUrl : null;

    internal static bool NavigateSameDocument(JintBackendContext ctx, string target, bool replace)
    {
        if (!Histories.TryGetValue(ctx, out var history))
        {
            return false;
        }

        var oldUrl = history.CurrentUrl;
        var newUrl = ResolveUrl(oldUrl, string.IsNullOrEmpty(target) ? JsValue.Undefined : new JsString(target));
        if (!IsSameDocumentUrl(oldUrl, newUrl))
        {
            return false;
        }

        if (string.Equals(oldUrl, newUrl, StringComparison.Ordinal))
        {
            return true;
        }

        var oldHash = ParsedFragment(oldUrl);
        var newHash = ParsedFragment(newUrl);
        history.Mutate(JsValue.Null, newUrl, replace);
        if (!string.Equals(oldHash, newHash, StringComparison.Ordinal)
            && ctx.Wrappers.Unwrap(ctx.Engine.Global) is EventTarget windowHost)
        {
            windowHost.DispatchEvent(new Event("hashchange"));
        }
        return true;
    }

    private static void Mutate(SessionHistory history, JsValue[] args, bool replace)
    {
        var state = args.Length > 0 ? args[0] : JsValue.Null;
        // args[1] is the unused "title" parameter — ignore per HTML §7.7.2.
        var urlArg = args.Length > 2 ? args[2] : JsValue.Undefined;
        var resolved = ResolveUrl(history.CurrentUrl, urlArg);
        history.Mutate(state, resolved, replace);
    }

    private static void Traverse(JintBackendContext ctx, SessionHistory history, int delta)
    {
        if (!history.TryTraverse(delta, out var newState))
        {
            return;
        }

        if (ctx.Wrappers.Unwrap(ctx.Engine.Global) is not EventTarget windowHost)
        {
            return;
        }

        windowHost.DispatchEvent(new PopStateEvent("popstate") { State = newState });
    }

    private static string ResolveUrl(string baseUrl, JsValue arg)
    {
        if (arg.IsUndefined() || arg.IsNull())
        {
            return baseUrl;
        }

        var raw = arg.ToString();
        if (raw.Length == 0)
        {
            return baseUrl;
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            return raw;
        }

        if (HasExplicitScheme(raw) && Uri.TryCreate(raw, UriKind.Absolute, out var absolute))
        {
            return absolute.ToString();
        }

        try { return new Uri(baseUri, raw).ToString(); }
        catch { return baseUrl; }
    }

    private static bool HasExplicitScheme(string raw)
    {
        var colon = raw.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

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

    private static bool IsSameDocumentUrl(string oldUrl, string newUrl)
    {
        if (!Uri.TryCreate(oldUrl, UriKind.Absolute, out var oldUri)
            || !Uri.TryCreate(newUrl, UriKind.Absolute, out var newUri))
        {
            return false;
        }

        return string.Equals(oldUri.Scheme, newUri.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(oldUri.Authority, newUri.Authority, StringComparison.OrdinalIgnoreCase)
            && string.Equals(oldUri.AbsolutePath, newUri.AbsolutePath, StringComparison.Ordinal)
            && string.Equals(oldUri.Query, newUri.Query, StringComparison.Ordinal);
    }

    private static string ParsedFragment(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Fragment : "";
}

internal sealed class SessionHistory
{
    private readonly List<Entry> _entries = new();
    private int _index;

    public SessionHistory(string initialUrl)
    {
        _entries.Add(new Entry(JsValue.Null, initialUrl));
        _index = 0;
    }

    public int Length => _entries.Count;
    public JsValue CurrentState => _entries[_index].State;
    public string CurrentUrl => _entries[_index].Url;
    public string ScrollRestoration { get; set; } = "auto";

    public void Mutate(JsValue state, string url, bool replace)
    {
        if (replace) { _entries[_index] = new Entry(state, url); return; }
        if (_index + 1 < _entries.Count)
        {
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);
        }

        _entries.Add(new Entry(state, url));
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

    private readonly record struct Entry(JsValue State, string Url);
}
