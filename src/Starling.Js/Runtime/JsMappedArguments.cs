namespace Starling.Js.Runtime;

/// <summary>
/// §10.4.4 Arguments Exotic Object — the <em>mapped</em> form produced by
/// §10.4.4.6 CreateMappedArgumentsObject for a NON-strict function with a
/// simple parameter list (no defaults, no rest, no destructuring).
/// </summary>
/// <remarks>
/// <para>
/// Each integer index <c>i &lt; paramCount</c> that names a still-live formal
/// parameter is <em>mapped</em>: the spec's [[ParameterMap]] ties
/// <c>arguments[i]</c> to the i-th parameter's environment-record binding, so
/// reading <c>arguments[i]</c> yields the parameter's current value and writing
/// <c>arguments[i]</c> updates the parameter (and vice-versa). Starling models
/// the binding as a direct reference to the callee frame's <c>locals</c> array
/// plus the parameter's local-slot index; when the compiler boxed that slot into
/// a <see cref="Cell"/> (because a nested closure captures the parameter), the
/// map writes through the same cell so the closure, the parameter, and
/// <c>arguments[i]</c> all observe one shared binding (which keeps working after
/// the frame returns).
/// </para>
/// <para>
/// The mapping for an index is removed (the property becomes ordinary) when the
/// index is deleted, redefined as an accessor, or redefined as a non-writable
/// data property — see §10.4.4.2 [[DefineOwnProperty]] / §10.4.4.4 [[Delete]].
/// </para>
/// </remarks>
public sealed class JsMappedArguments : JsObject
{
    /// <summary>The callee frame's local-slot storage. A mapped index reads and
    /// writes the parameter through this array (possibly via a <see cref="Cell"/>
    /// in the slot). Held by reference so the live link survives frame return.</summary>
    private readonly JsValue[] _locals;

    /// <summary>For each mapped index, the local slot of its parameter; -1 once
    /// the index is unmapped (deleted / redefined as accessor / non-writable).
    /// Sparse: only indices in <c>0..paramCount-1</c> can ever be mapped.</summary>
    private readonly int[] _slotForIndex;

    internal JsMappedArguments(JsObject? prototype, JsValue[] locals, int[] slotForIndex)
        : base(prototype)
    {
        DisableInlineCache();
        _locals = locals;
        _slotForIndex = slotForIndex;
        IsArgumentsExotic = true;
    }

    /// <summary>True iff string key <paramref name="name"/> is a currently-mapped
    /// canonical array index (the spec's <c>HasOwnProperty(map, P)</c>).</summary>
    private bool IsMapped(string name)
        => TryIndex(name, out var idx) && idx >= 0 && idx < _slotForIndex.Length && _slotForIndex[idx] >= 0;

    /// <summary>Parse a canonical non-negative integer index from a property key,
    /// matching the spec's CanonicalNumericIndexString restricted to the small
    /// indices a parameter map can hold.</summary>
    private static bool TryIndex(string name, out int idx)
    {
        idx = -1;
        if (name.Length == 0)
        {
            return false;
        }
        // Reject leading zeros / signs / non-digits so "00", "+0", "1.0" are not
        // treated as array indices (their canonical strings differ).
        if (name.Length > 1 && name[0] == '0')
        {
            return false;
        }

        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] < '0' || name[i] > '9')
            {
                return false;
            }
        }

        return int.TryParse(name, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out idx);
    }

    /// <summary>Read the live parameter value bound to mapped index
    /// <paramref name="idx"/> (Get(map, P)).</summary>
    private JsValue GetMapped(int idx)
    {
        var slot = _slotForIndex[idx];
        var v = _locals[slot];
        return v.IsObject && v.AsObject is Cell cell ? cell.Value : v;
    }

    /// <summary>Write the parameter bound to mapped index <paramref name="idx"/>
    /// (Set(map, P, value)). Goes through the slot's <see cref="Cell"/> when the
    /// compiler promoted the parameter for closure capture.</summary>
    private void SetMapped(int idx, JsValue value)
    {
        var slot = _slotForIndex[idx];
        var cur = _locals[slot];
        if (cur.IsObject && cur.AsObject is Cell cell)
        {
            cell.Value = value;
        }
        else
        {
            _locals[slot] = value;
        }
    }

    /// <summary>Remove the mapping for an index (map.[[Delete]](P)); the stored
    /// ordinary property, if any, is left untouched.</summary>
    private void Unmap(int idx)
    {
        if (idx >= 0 && idx < _slotForIndex.Length)
        {
            _slotForIndex[idx] = -1;
        }
    }

    // ----- §10.4.4.1 [[GetOwnProperty]] -----
    public override PropertyDescriptor? GetOwnPropertyDescriptor(string name)
    {
        var desc = base.GetOwnPropertyDescriptor(name);
        if (desc is null)
        {
            return null;
        }
        // §10.4.4.1 step 4 — for a mapped index the reported value is the live
        // parameter value, overriding the (stale) stored slot.
        if (IsMapped(name) && TryIndex(name, out var idx))
        {
            return desc.Value.WithValue(GetMapped(idx));
        }

        return desc;
    }

    // ----- §10.4.4.3 [[Get]] (data-only fast path; accessors dispatch via AO) -----
    public override JsValue Get(string name)
    {
        if (IsMapped(name) && TryIndex(name, out var idx))
        {
            return GetMapped(idx);
        }

        return base.Get(name);
    }

    // ----- §10.4.4.4 [[Set]] (data-only fast path) -----
    public override void Set(string name, JsValue value)
    {
        // §10.4.4.4 step 4 — when an own writable data property is being updated
        // for a mapped index, push the value through to the bound parameter too.
        if (IsMapped(name) && TryIndex(name, out var idx))
        {
            var own = base.GetOwnPropertyDescriptor(name);
            if (own is { IsData: true, Writable: true })
            {
                SetMapped(idx, value);
                base.Set(name, value);
                return;
            }
        }
        base.Set(name, value);
    }

    // ----- §10.4.4.5 [[Delete]] -----
    public override bool Delete(string name)
    {
        var wasMapped = IsMapped(name);
        var result = base.Delete(name);
        if (result && wasMapped && TryIndex(name, out var idx))
        {
            Unmap(idx);
        }

        return result;
    }

    // ----- §10.4.4.2 [[DefineOwnProperty]] -----
    // The presence-less override (used by internal callers / object literals)
    // treats the descriptor as complete: any data define updates the value and
    // a non-writable data / accessor define unmaps the index.
    public override bool DefineOwnProperty(string name, PropertyDescriptor desc)
        => DefineOwnPropertyMapped(name, desc, DescriptorFields.Complete(desc));

    /// <summary>§10.4.4.2 with explicit field-presence info — the user-facing
    /// <c>Object.defineProperty</c> / <c>Reflect.defineProperty</c> path threads
    /// the originally-specified fields so the unmapping rules (which depend on
    /// whether [[Value]] / [[Writable]] were present) are spec-exact.</summary>
    internal bool DefineOwnPropertyMapped(string name, PropertyDescriptor desc, DescriptorFields present)
    {
        var isMapped = IsMapped(name);
        if (!isMapped || !TryIndex(name, out var idx))
        {
            // Unmapped indices, `length`, `callee`, etc. still need partial-field
            // merge so a probe like `{configurable:false}` does not clobber the
            // value/writable/enumerable a verifyProperty round-trip relies on.
            return DefinePartial(name, desc, present);
        }

        // §10.4.4.2 steps 4-5 — a non-writable data redefinition that omits a new
        // [[Value]] must keep the current mapped value; fold the live value in so
        // OrdinaryDefineOwnProperty validates and stores against it.
        var present2 = present;
        if (desc.IsData && !present.HasValue && present.HasWritable && !desc.Writable)
        {
            desc = desc.WithValue(GetMapped(idx));
            present2 = DescriptorFields.WithValuePresent(present);
        }

        var allowed = DefinePartial(name, desc, present2);
        if (!allowed)
        {
            return false;
        }

        // §10.4.4.2 step 8 — sync / unmap depending on the requested descriptor.
        if (desc.IsAccessor)
        {
            Unmap(idx);
        }
        else
        {
            if (present.HasValue)
            {
                SetMapped(idx, desc.Value);
            }

            if (present.HasWritable && !desc.Writable)
            {
                Unmap(idx);
            }
        }
        return true;
    }

    /// <summary>String-keyed wrapper around the base
    /// <see cref="JsObject.DefineOwnPropertyPartial"/>. Kept so the mapped path
    /// below reads cleanly; semantics are identical.</summary>
    private bool DefinePartial(string name, PropertyDescriptor desc, DescriptorFields present)
        => DefineOwnPropertyPartial(JsPropertyKey.String(name), desc, present);

    /// <summary>Define <paramref name="desc"/> (already resolved from
    /// <paramref name="descSource"/>) on <paramref name="target"/>, routing through
    /// the §10.4.4.2 mapped path when the target is a mapped arguments object so
    /// parameter mappings update / unmap correctly based on field presence. Used by
    /// the user-facing <c>Object.defineProperty</c> / <c>Object.defineProperties</c>
    /// / <c>Reflect.defineProperty</c> entry points.</summary>
    public static bool DefineFromUser(JsObject target, JsPropertyKey key,
        PropertyDescriptor desc, JsObject descSource)
    {
        if (target is JsMappedArguments ma && !key.IsSymbol)
        {
            return ma.DefineOwnPropertyMapped(key.AsString, desc, DescriptorFields.FromSource(descSource));
        }
        // §10.1.6 OrdinaryDefineOwnProperty with the field-presence info threaded
        // through from §6.2.5.6 ToPropertyDescriptor — preserves attributes the
        // caller did NOT specify (e.g. defineProperty(o,'a',{value:11}) keeps
        // [[Enumerable]] from the existing descriptor). Without this routing the
        // collapsed PropertyDescriptor's default flags clobber the prior state.
        return target.DefineOwnPropertyPartial(key, desc, DescriptorFields.FromSource(descSource));
    }
}

/// <summary>
/// Which fields a §6.2.5.6 ToPropertyDescriptor result actually specified.
/// Starling's resolved <see cref="PropertyDescriptor"/> collapses partial
/// descriptors to complete ones, losing the presence information that
/// §10.4.4.2 [[DefineOwnProperty]] needs to decide whether to update or remove a
/// parameter mapping. The user-facing define paths carry this alongside the
/// resolved descriptor so a mapped arguments object can apply the spec rules.
/// </summary>
public readonly struct DescriptorFields
{
    [System.Flags]
    private enum F : byte
    {
        None = 0,
        Value = 1 << 0,
        Writable = 1 << 1,
        Enumerable = 1 << 2,
        Configurable = 1 << 3,
        Get = 1 << 4,
        Set = 1 << 5,
    }

    private readonly F _flags;
    private DescriptorFields(F flags) => _flags = flags;

    public bool HasValue => (_flags & F.Value) != 0;
    public bool HasWritable => (_flags & F.Writable) != 0;
    public bool HasEnumerable => (_flags & F.Enumerable) != 0;
    public bool HasConfigurable => (_flags & F.Configurable) != 0;
    public bool HasGet => (_flags & F.Get) != 0;
    public bool HasSet => (_flags & F.Set) != 0;

    /// <summary>Copy with the [[Value]] field marked present — used by §10.4.4.2
    /// step 5 after folding the live mapped value into a non-writable redefine.</summary>
    public DescriptorFields WithValuePresent() => new(_flags | F.Value);
    internal static DescriptorFields WithValuePresent(DescriptorFields f) => f.WithValuePresent();

    public static DescriptorFields Build(bool value, bool writable, bool enumerable,
        bool configurable, bool get, bool set)
    {
        var f = F.None;
        if (value)
        {
            f |= F.Value;
        }

        if (writable)
        {
            f |= F.Writable;
        }

        if (enumerable)
        {
            f |= F.Enumerable;
        }

        if (configurable)
        {
            f |= F.Configurable;
        }

        if (get)
        {
            f |= F.Get;
        }

        if (set)
        {
            f |= F.Set;
        }

        return new DescriptorFields(f);
    }

    /// <summary>All fields appropriate to the descriptor's kind are present —
    /// used when the caller has no partial-presence information (internal
    /// defines and object/array literal property creation).</summary>
    public static DescriptorFields Complete(PropertyDescriptor d)
        => d.IsAccessor
            ? Build(value: false, writable: false, enumerable: true, configurable: true, get: true, set: true)
            : Build(value: true, writable: true, enumerable: true, configurable: true, get: false, set: false);

    /// <summary>§6.2.5.6 — read which fields a descriptor source object actually
    /// specified (own-or-inherited <c>Has</c>, matching ToPropertyDescriptor).</summary>
    public static DescriptorFields FromSource(JsObject descObj)
        => Build(
            value: descObj.Has("value"),
            writable: descObj.Has("writable"),
            enumerable: descObj.Has("enumerable"),
            configurable: descObj.Has("configurable"),
            get: descObj.Has("get"),
            set: descObj.Has("set"));
}
