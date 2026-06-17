using Starling.Js.Intrinsics;

namespace Starling.Js.Runtime;

/// <summary>
/// §10.4.6 Module Namespace Exotic Object — the object an <c>import * as ns</c>
/// binding (and a dynamic <c>import()</c> fulfilment) observes. It exposes the
/// exporting module's resolved exports as live, read-only data properties plus a
/// non-enumerable <c>@@toStringTag</c> of <c>"Module"</c>, and has a null
/// prototype.
/// </summary>
/// <remarks>
/// <para>The object is NOT an ordinary property bag: every internal method is
/// overridden to match §10.4.6 exactly. In particular it is non-extensible, has
/// no prototype, rejects every prototype/define/set/delete mutation, and reports
/// its own keys as the exported string names <em>sorted by code unit</em>
/// followed by <c>@@toStringTag</c> (§10.4.6.10 step 4).</para>
/// <para>Live bindings are realised by resolving each export name to the
/// <see cref="Cell"/> the exporting module writes (see
/// <c>ModuleLoader.GetOrBuildNamespace</c>). [[Get]] / [[GetOwnProperty]] read
/// the cell's current value on every access, so a later mutation of the
/// exporting binding is visible through the namespace.</para>
/// <para>Because the engine routes property reads/writes through
/// <c>AbstractOperations.Get</c> / <c>AbstractOperations.Set</c> (which dispatch
/// this exotic by type, the same way they dispatch <see cref="JsProxy"/>), the
/// [[Get]] live-value and [[Set]]-returns-false behaviour is observed
/// everywhere, not only through the virtual overrides.</para>
/// </remarks>
public sealed class JsModuleNamespace : JsObject
{
    /// <summary>Exported name → live backing cell, in no particular order. A
    /// data-property read returns <c>cell.Value</c> at access time.</summary>
    private readonly Dictionary<string, Cell> _exports;

    /// <summary>The exported names sorted by code unit (Array.prototype.sort
    /// default order), per §10.4.6.10 step 4. Cached so [[OwnPropertyKeys]] is a
    /// stable, ordered view.</summary>
    private readonly List<string> _sortedNames = new();

    /// <summary>§10.4.6 — a module namespace object has no [[Prototype]] and is
    /// not extensible from creation; build it that way. Internal because
    /// <see cref="Cell"/> (the live-binding box) is engine-internal.</summary>
    internal JsModuleNamespace(Dictionary<string, Cell> exports)
        : base(prototype: null)
    {
        DisableInlineCache();
        _exports = exports;
        RefreshExportNames();
    }

    internal void RefreshExportNames()
    {
        _sortedNames.Clear();
        _sortedNames.AddRange(_exports.Keys);
        _sortedNames.Sort(string.CompareOrdinal);
    }

    /// <summary>True iff <paramref name="name"/> is one of this module's resolved
    /// exported names.</summary>
    private bool IsExport(string name) => _exports.ContainsKey(name);

    // ==========================================================
    //                §10.4.6.1 [[GetPrototypeOf]]
    // ==========================================================
    public override JsObject? GetPrototypeOf() => null;

    // ==========================================================
    //                §10.4.6.2 [[SetPrototypeOf]] (V)
    // ==========================================================
    public override bool SetPrototypeOf(JsObject? proto) => proto is null;

    // ==========================================================
    //                §10.4.6.3 [[IsExtensible]]
    // ==========================================================
    public override bool Extensible => false;

    // ==========================================================
    //                §10.4.6.4 [[PreventExtensions]]
    // ==========================================================
    public override bool PreventExtensions() => true;

    // ==========================================================
    //                §10.4.6.5 [[GetOwnProperty]] (P)
    // ==========================================================
    public override PropertyDescriptor? GetOwnPropertyDescriptor(string name)
    {
        if (!_exports.TryGetValue(name, out var cell))
        {
            return null;
        }
        // Live data descriptor: value reflects the binding's current value.
        return PropertyDescriptor.Data(cell.Value, writable: true, enumerable: true, configurable: false);
    }

    public override PropertyDescriptor? GetOwnPropertyDescriptor(JsSymbol symbol)
    {
        // §10.4.6.5 step 1: a symbol key falls back to OrdinaryGetOwnProperty,
        // which for a namespace only has @@toStringTag.
        if (ReferenceEquals(symbol, SymbolCtor.ToStringTag))
        {
            return PropertyDescriptor.Data(JsValue.String("Module"),
                writable: false, enumerable: false, configurable: false);
        }

        return null;
    }

    // ==========================================================
    //                §10.4.6.6 [[DefineOwnProperty]] (P, Desc)
    // ==========================================================
    public override bool DefineOwnProperty(string name, PropertyDescriptor desc)
        => DefineMatches(GetOwnPropertyDescriptor(name), desc);

    public override bool DefineOwnProperty(JsSymbol symbol, PropertyDescriptor desc)
        => DefineMatches(GetOwnPropertyDescriptor(symbol), desc);

    /// <summary>§10.4.6.6 partial — only the fields the caller actually
    /// specified must match the current export's data descriptor. Any
    /// addition or specified-field mismatch is rejected (returns false).
    /// Routed through by <see cref="JsMappedArguments.DefineFromUser"/>.</summary>
    internal override bool DefineOwnPropertyPartial(JsPropertyKey key, PropertyDescriptor desc, DescriptorFields present)
    {
        var current = GetOwnPropertyDescriptor(key);
        if (current is not { } cur)
        {
            return false; // no such export → reject add
        }

        if (desc.IsAccessor)
        {
            return false; // current is always a data descriptor
        }

        if (present.HasConfigurable && desc.Configurable)
        {
            return false; // current is non-configurable
        }

        if (present.HasEnumerable && desc.Enumerable != cur.Enumerable)
        {
            return false;
        }

        if (present.HasWritable && desc.Writable != cur.Writable)
        {
            return false;
        }

        if (present.HasValue && !AbstractOperations.SameValue(desc.Value, cur.Value))
        {
            return false;
        }

        return true;
    }

    /// <summary>§10.4.6.6 — a define succeeds only when the proposed descriptor
    /// is exactly the current one (no attribute or value change). Any addition or
    /// modification is rejected (returns false).</summary>
    private static bool DefineMatches(PropertyDescriptor? current, PropertyDescriptor desc)
    {
        if (current is not { } cur)
        {
            return false; // adding a property → reject
        }

        if (desc.IsAccessor)
        {
            return false; // current is always a data descriptor
        }
        // Step 8: configurable/enumerable/writable/value must all match.
        if (desc.Configurable)
        {
            return false;        // current is non-configurable
        }

        if (desc.Enumerable != cur.Enumerable)
        {
            return false;
        }

        if (desc.Writable != cur.Writable)
        {
            return false;
        }

        return AbstractOperations.SameValue(desc.Value, cur.Value);
    }

    // ==========================================================
    //                §10.4.6.7 [[HasProperty]] (P)
    // ==========================================================
    public override bool Has(string name) => IsExport(name);

    public override bool Has(JsSymbol symbol)
        => ReferenceEquals(symbol, SymbolCtor.ToStringTag);

    public override bool HasOwn(string name) => IsExport(name);

    public override bool HasOwn(JsSymbol symbol)
        => ReferenceEquals(symbol, SymbolCtor.ToStringTag);

    // ==========================================================
    //                §10.4.6.8 [[Get]] (P, Receiver)
    // ==========================================================
    public override JsValue Get(string name)
        => _exports.TryGetValue(name, out var cell) ? cell.Value : JsValue.Undefined;

    public override JsValue Get(JsSymbol symbol)
        => ReferenceEquals(symbol, SymbolCtor.ToStringTag)
            ? JsValue.String("Module")
            : JsValue.Undefined;

    // ==========================================================
    //                §10.4.6.9 [[Set]] (P, V, Receiver)
    // ==========================================================
    // [[Set]] always returns false. The base Set is void; the boolean rejection
    // contract is enforced through AbstractOperations.Set (dispatched by type).
    public override void Set(string name, JsValue value) { /* §10.4.6.9 — no-op */ }
    public override void Set(JsSymbol symbol, JsValue value) { /* §10.4.6.9 — no-op */ }

    // ==========================================================
    //                §10.4.6.10 [[Delete]] (P)
    // ==========================================================
    public override bool Delete(string name) => !IsExport(name);

    public override bool Delete(JsSymbol symbol)
        => !ReferenceEquals(symbol, SymbolCtor.ToStringTag);

    // ==========================================================
    //                §10.4.6.11 [[OwnPropertyKeys]]
    // ==========================================================
    public override IEnumerable<JsPropertyKey> OwnPropertyKeys
    {
        get
        {
            foreach (var name in _sortedNames)
            {
                yield return JsPropertyKey.String(name);
            }

            yield return JsPropertyKey.Symbol(SymbolCtor.ToStringTag);
        }
    }

    public override IEnumerable<string> Keys => _sortedNames;

    /// <summary>The exported names are enumerable data properties;
    /// <c>@@toStringTag</c> is not enumerable, so it is excluded.</summary>
    public override IEnumerable<string> EnumerableKeys() => _sortedNames;
}
