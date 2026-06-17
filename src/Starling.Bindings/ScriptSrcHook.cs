using System.Runtime.CompilerServices;
using Starling.Dom;
using Starling.Js.Runtime;

namespace Starling.Bindings;

/// <summary>
/// Bridge for HTML §4.12.1 "prepare a script" triggered by a script element's
/// <c>src</c> being set from JavaScript. The engine owns the fetch+execute
/// pipeline, but the place that observes the mutation is the DOM binding layer
/// (<see cref="NodeBindings"/>'s <c>setAttribute</c> and the <c>.src</c> IDL
/// property). This class is the thin, realm-keyed seam between the two so the
/// bindings stay engine-agnostic.
/// </summary>
/// <remarks>
/// <para>When JS sets <c>src</c> on a <c>&lt;script&gt;</c> element that has not
/// yet started, real browsers treat it as a newly inserted external script and
/// run "prepare a script": fetch the URL, execute it, then fire <c>load</c>
/// (or <c>error</c>). The deferred-bundle loader pattern depends on exactly
/// this — a loader runs on <c>DOMContentLoaded</c> and copies a custom
/// <c>data-*</c> attribute onto <c>src</c> to kick the real download.</para>
/// <para>The hook is intentionally narrow: only a script element's <c>src</c>
/// flows through it. The engine registers a callback per realm in
/// <c>RunScriptsAsync</c>; with no callback registered (JS run outside the
/// engine pipeline) the mutation just lands as a plain attribute write.</para>
/// </remarks>
public static class ScriptSrcHook
{
    private static readonly ConditionalWeakTable<JsRealm, Box> ByRealm = new();

    private sealed class Box { public Action<Element>? Callback; }

    /// <summary>Register (or replace) the engine callback invoked when a script
    /// element's <c>src</c> is set from JS. Pass <c>null</c> to clear.</summary>
    public static void Register(JsRealm realm, Action<Element>? callback)
    {
        ArgumentNullException.ThrowIfNull(realm);
        var box = ByRealm.GetValue(realm, _ => new Box());
        box.Callback = callback;
    }

    /// <summary>Notify the engine that <paramref name="script"/> (a
    /// <c>&lt;script&gt;</c> element) just had its <c>src</c> set. No-op when no
    /// callback is registered for the realm.</summary>
    internal static void NotifySrcSet(JsRealm realm, Element script)
    {
        if (ByRealm.TryGetValue(realm, out var box) && box.Callback is { } cb)
        {
            cb(script);
        }
    }
}
