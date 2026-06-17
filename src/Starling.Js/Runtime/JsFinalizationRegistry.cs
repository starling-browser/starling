namespace Starling.Js.Runtime;

/// <summary>
/// §26.2 FinalizationRegistry Objects — manages a list of weak target /
/// held-value pairs and invokes a cleanup callback after each target is
/// reclaimed.
/// </summary>
/// <remarks>
/// The cleanup pass is driven by <see cref="JsRuntime.DrainMicrotasks"/>:
/// after the microtask queue empties, the runtime walks every live
/// registry, identifies entries whose target's <see cref="WeakReference{T}"/>
/// has been collected, and schedules the callback via
/// <see cref="JsRealm.Microtasks"/>. This mirrors the spec's HostEnqueueFinalizationRegistryCleanupJob
/// hook (§26.2.1.1) at a coarser cadence — most code can't observe the
/// difference.
/// </remarks>
public sealed class JsFinalizationRegistry : JsObject
{
    private readonly JsRealm _realm;
    private readonly JsValue _cleanupCallback;
    private readonly List<Entry> _entries = new();

    public JsFinalizationRegistry(JsRealm realm, JsValue cleanupCallback)
        : base(realm.FinalizationRegistryPrototype)
    {
        ArgumentNullException.ThrowIfNull(realm);
        _realm = realm;
        _cleanupCallback = cleanupCallback;
    }

    /// <summary>§26.2.3.2 — record a weak entry.</summary>
    public void Register(JsObject target, JsValue heldValue, JsObject? unregisterToken)
    {
        _entries.Add(new Entry(
            new WeakReference<JsObject>(target),
            heldValue,
            unregisterToken is null ? null : new WeakReference<JsObject>(unregisterToken)));
    }

    /// <summary>§26.2.3.3 — remove every entry registered with the given
    /// token. Returns <c>true</c> when at least one entry was removed.</summary>
    public bool Unregister(JsObject token)
    {
        var removed = false;
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            if (e.Token is null)
            {
                continue;
            }

            if (!e.Token.TryGetTarget(out var t))
            {
                continue;
            }

            if (ReferenceEquals(t, token))
            {
                _entries.RemoveAt(i);
                removed = true;
            }
        }
        return removed;
    }

    /// <summary>
    /// Cleanup pass per §26.2.1.1 / §9.10.4. For each entry whose target has
    /// been reclaimed, enqueue the cleanup callback with the entry's held
    /// value and drop the entry. Reclaimed token references are also pruned
    /// opportunistically.
    /// </summary>
    public void RunCleanupPass()
    {
        if (_entries.Count == 0)
        {
            return;
        }

        var runtime = JsRuntimeHandle;
        for (var i = _entries.Count - 1; i >= 0; i--)
        {
            var e = _entries[i];
            if (e.Target.TryGetTarget(out _))
            {
                continue;
            }

            // Target reclaimed — schedule callback(heldValue) on the
            // microtask queue, then drop the entry.
            var held = e.HeldValue;
            var callback = _cleanupCallback;
            var realm = _realm;
            _realm.Microtasks.Enqueue(() =>
            {
                if (runtime is not null)
                {
                    runtime.WithActiveVm(() =>
                        AbstractOperations.Call(realm.ActiveVm, callback, JsValue.Undefined, new[] { held }));
                }
                else
                {
                    AbstractOperations.Call(realm.ActiveVm, callback, JsValue.Undefined, new[] { held });
                }
            });
            _entries.RemoveAt(i);
        }
    }

    /// <summary>Optional handle so the cleanup pass can use
    /// <see cref="JsRuntime.WithActiveVm(Action)"/>. The runtime sets this when it
    /// owns this registry.</summary>
    internal JsRuntime? JsRuntimeHandle { get; set; }

    private sealed record Entry(
        WeakReference<JsObject> Target,
        JsValue HeldValue,
        WeakReference<JsObject>? Token);
}
