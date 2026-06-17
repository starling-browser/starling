namespace Starling.Js.Runtime;

/// <summary>
/// The realm's global object, extended with a lazy-intrinsic registry so the
/// long-tail built-ins (Math, JSON, the typed-array cluster, Map/Set, Date,
/// Intl, Proxy, Reflect, …) can be installed on first access instead of during
/// <c>new JsRuntime()</c>. Eager install of every intrinsic dominates the fixed
/// startup cost of a realm, which hurts tiny scripts that never touch most of
/// them.
/// </summary>
/// <remarks>
/// <para><b>Placeholder + registry design.</b> When an intrinsic is deferred we
/// still define a real, non-enumerable, configurable, writable data property
/// for its global name up front (the <em>placeholder</em>, value
/// <c>undefined</c>) and record a <em>materialize thunk</em> in
/// <see cref="_lazy"/>. The placeholder reuses the base object's ordinary
/// string-keyed storage, so the name is present in the right creation-order
/// position with the right attributes — <c>'Map' in globalThis</c>,
/// <c>Object.getOwnPropertyNames(globalThis)</c> order, and the non-enumerable
/// flag are all byte-identical to an eager build. The placeholder's
/// <c>undefined</c> value is never observed: <see cref="Get(string)"/> and
/// <see cref="GetOwnPropertyDescriptor(string)"/> materialize first, replacing
/// the placeholder with the real intrinsic (which the installer defines with the
/// same attributes).</para>
/// <para><b>Observability.</b> <c>in</c> / <c>HasOwn</c> / key enumeration do
/// NOT materialize — the placeholder already makes the name present and ordered.
/// A user write or <c>defineProperty</c> or <c>delete</c> on a still-deferred
/// name drops it from the registry first, so overwriting or deleting before
/// first access wins and the real intrinsic is never installed over it.</para>
/// <para><b>Clusters.</b> Several names can share one thunk
/// (ArrayBuffer/DataView and every typed-array constructor — the typed arrays
/// need ArrayBuffer). Each cluster name is reserved pointing at the same thunk,
/// and materializing any one clears every cluster sibling from the registry so
/// the shared installer runs exactly once.</para>
/// </remarks>
public sealed class JsGlobalObject : JsObject
{
    /// <summary>Global name -> materialize thunk for not-yet-installed
    /// intrinsics. A name leaves this map the moment it is materialized,
    /// overwritten, redefined, or deleted. Cluster siblings share one thunk
    /// instance.</summary>
    private readonly Dictionary<string, Action> _lazy = new(StringComparer.Ordinal);

    public JsGlobalObject(JsObject? prototype) : base(prototype)
    {
        // The global object's property reads route through the lazy overrides
        // below, so it must never serve a cached slot directly — opt out of the
        // inline-cache fast paths exactly like other exotic objects.
        DisableInlineCache();
    }

    /// <summary>Defer an intrinsic's global <paramref name="name"/>: install a
    /// placeholder data property now (so the name is present, ordered, and
    /// non-enumerable like the real global) and record
    /// <paramref name="installer"/> to run on first access. For a cluster of
    /// names that share one installer, call this once per name passing the SAME
    /// <paramref name="installer"/> delegate; the installer must clear every
    /// cluster name from the registry (materializing any one materializes all).</summary>
    public void ReserveLazyGlobal(string name, Action installer)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(installer);
        // Install the placeholder FIRST. The overridden DefineOwnProperty drops
        // any deferred entry for the name, so recording _lazy afterward is the
        // correct order. Same attributes the real installer uses (W=true,
        // E=false, C=true), so descriptor shape and getOwnPropertyNames-vs-keys
        // behavior match before and after materialization.
        DefineOwnProperty(name,
            PropertyDescriptor.Data(JsValue.Undefined, writable: true, enumerable: false, configurable: true));
        _lazy[name] = installer;
    }

    /// <summary>Run the materialize thunk for <paramref name="name"/> if it is
    /// still deferred. Removes the name from the registry FIRST so the thunk's
    /// own DefineOwnProperty (which routes back through this object) does not
    /// re-enter, then runs the installer. The installer replaces the placeholder
    /// with the real intrinsic and populates its prototype. Idempotent.</summary>
    private void Materialize(string name)
    {
        if (!_lazy.TryGetValue(name, out var installer))
        {
            return;
        }
        // Clear this name AND any cluster siblings sharing the same thunk before
        // running, so the shared installer runs once and re-entry is impossible.
        RemoveLazyAndSiblings(name, installer);
        installer();
    }

    /// <summary>Drop <paramref name="name"/> and every other registry entry that
    /// points at the same <paramref name="installer"/> delegate (its cluster
    /// siblings).</summary>
    private void RemoveLazyAndSiblings(string name, Action installer)
    {
        _lazy.Remove(name);
        List<string>? siblings = null;
        foreach (var pair in _lazy)
        {
            if (ReferenceEquals(pair.Value, installer))
            {
                (siblings ??= new List<string>()).Add(pair.Key);
            }
        }

        if (siblings is not null)
        {
            foreach (var s in siblings)
            {
                _lazy.Remove(s);
            }
        }
    }

    /// <summary>Drop a single deferred name (no sibling sweep) — used when the
    /// user overwrites/redefines/deletes it before first access, which must
    /// suppress only that name's intrinsic, not its cluster.</summary>
    private void DropLazy(string name) => _lazy.Remove(name);

    // ----- Read paths: materialize on demand -----

    public override JsValue Get(string name)
    {
        if (_lazy.ContainsKey(name))
        {
            Materialize(name);
        }

        return base.Get(name);
    }

    public override PropertyDescriptor? GetOwnPropertyDescriptor(string name)
    {
        if (_lazy.ContainsKey(name))
        {
            Materialize(name);
        }

        return base.GetOwnPropertyDescriptor(name);
    }

    // ----- Presence + enumeration: placeholder already covers these, do NOT
    // materialize (would defeat the whole point). Inherited base behavior. -----

    // ----- Write paths: user override before first access wins -----

    public override void Set(string name, JsValue value)
    {
        // A direct write to a still-deferred name overwrites the placeholder and
        // suppresses the intrinsic (the user got there first).
        DropLazy(name);
        base.Set(name, value);
    }

    public override bool DefineOwnProperty(string name, PropertyDescriptor desc)
    {
        // ReserveLazyGlobal itself calls this to install the placeholder; at that
        // point the name is not yet in _lazy, so removing is a no-op. A later
        // user redefinition removes the deferred entry and applies the new
        // descriptor over the placeholder.
        DropLazy(name);
        return base.DefineOwnProperty(name, desc);
    }

    public override bool Delete(string name)
    {
        DropLazy(name);
        return base.Delete(name);
    }
}
