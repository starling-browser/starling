namespace Starling.Js.Runtime;

/// <summary>
/// Ordinary JS object per §10.1. Property-bag with descriptors and a prototype
/// chain. Subclasses override behavior for arrays, functions, proxies, etc.
/// </summary>
/// <remarks>
/// <para>String and Symbol property keys live in separate namespaces per
/// ECMA-262 §6.1.7. Indexed-integer fast paths land with <c>JsArray</c>'s dense
/// storage subclass.</para>
/// <para><b>Storage modes.</b> String-keyed properties use one of two backings.
/// In <em>fast mode</em> (the default for a fresh object) the object carries a
/// shared <see cref="Shape"/> describing its data properties plus a flat
/// <c>_slots</c> array holding their values — this is what inline caches read.
/// The object falls to <em>dictionary mode</em> (<c>_shape == null</c>, using
/// the legacy <c>_properties</c> dictionary) the moment it does something a
/// shape cannot model exactly: defining an accessor, deleting a property,
/// redefining attributes, or becoming non-extensible. Dictionary mode runs the
/// original validator/ordering code unchanged, so every observable matches the
/// pre-shape engine. Symbol-keyed properties always use a dictionary.</para>
/// </remarks>
public class JsObject
{
    // ----- String-keyed storage -----
    // Fast mode: _shape != null, values in _slots. Dictionary mode: _shape ==
    // null, descriptors in _properties (+ _stringKeyOrder for creation order).
    private Shape? _shape = Shape.Root;
    private JsValue[] _slots = System.Array.Empty<JsValue>();

    private Dictionary<string, PropertyDescriptor>? _properties;

    /// <summary>String own-key creation order for dictionary mode. The
    /// <see cref="Dictionary{TKey,TValue}"/> backing does NOT preserve
    /// chronological order across delete+reinsert (a removed slot is recycled by
    /// the next add), but §10.1.11.1 requires "ascending chronological order of
    /// property creation". We keep this authoritative ordered list in lockstep
    /// with <see cref="_properties"/> via <see cref="PutString"/> /
    /// <see cref="RemoveString"/> so a re-added key lands at the end. In fast
    /// mode the <see cref="Shape"/>'s transition chain supplies creation order
    /// instead.</summary>
    private List<string>? _stringKeyOrder;

    // ----- Symbol-keyed storage (always a dictionary; lazily allocated) -----
    private Dictionary<JsSymbol, PropertyDescriptor>? _symbolProperties;

    /// <summary>The object's fast-mode hidden class, or <c>null</c> in
    /// dictionary mode. Read by the inline cache to validate a cached slot.</summary>
    internal Shape? Shape => _shape;

    /// <summary>Read a fast-mode data slot by index. The caller (inline cache)
    /// must have validated <see cref="Shape"/> identity first.</summary>
    internal JsValue ReadSlot(int slot) => _slots[slot];

    /// <summary>Overwrite a fast-mode data slot. The caller (write inline cache)
    /// must have validated <see cref="Shape"/> identity, which guarantees the
    /// slot exists and is writable (writability is part of shape identity).</summary>
    internal void WriteSlot(int slot, JsValue value) => _slots[slot] = value;

    /// <summary>Add a new data property by transitioning straight to a cached
    /// child shape (the write inline cache's add path). The caller must have
    /// validated that the current <see cref="Shape"/> is <paramref name="next"/>'s
    /// parent; a fast-mode object is always extensible (non-extensible objects
    /// are migrated to dictionary mode), so no extensibility check is needed.</summary>
    internal void FastAdd(Shape next, int slot, JsValue value)
    {
        if (_slots.Length < next.SlotCount)
        {
            var cap = _slots.Length == 0 ? 4 : _slots.Length * 2;
            if (cap < next.SlotCount)
            {
                cap = next.SlotCount;
            }

            System.Array.Resize(ref _slots, cap);
        }
        _slots[slot] = value;
        _shape = next;
    }

    /// <summary>Install a precomputed fast-mode shape and a matching slot array
    /// directly, bypassing the per-property <see cref="AddFastProperty"/> path.
    /// Used by intrinsic bootstrap to build native functions and prototypes from a
    /// shape built once via <see cref="Shape.Transition"/> plus a pre-filled value
    /// array, instead of N sequential <see cref="DefineOwnProperty(string, PropertyDescriptor)"/>
    /// calls. The result is byte-identical to the incremental path: same shared
    /// Shape identity (so inline caches / migration see exactly what they expect),
    /// same slot values, same attributes. The object must still be in its initial
    /// fast state (root shape, no slots, no dictionary) — this is a one-shot
    /// adopt at construction time.</summary>
    internal void AdoptShape(Shape shape, JsValue[] slots)
    {
        _shape = shape;
        _slots = slots;
    }

    private bool _supportsInlineCache = true;

    /// <summary>Whether this object is the [[Prototype]] of some other object.
    /// Set when an object is created with it as prototype (or relinked to it).
    /// Only mutations to such objects bump <see cref="ProtoEpoch"/>, so the
    /// overwhelmingly common case — adding properties to a leaf object — never
    /// disturbs prototype-chain inline caches.</summary>
    private bool _isUsedAsPrototype;

    /// <summary>Global prototype-mutation epoch. Bumped whenever an object that
    /// is used as a prototype changes its string-key structure (add / delete /
    /// redefine / migrate to dictionary mode) or is relinked. A proto-chain read
    /// cache and an add-transition write cache snapshot this and re-check it on a
    /// hit: an unchanged epoch proves no prototype anywhere gained or lost the
    /// cached name, so the cached holder slot / add transition stays valid. The
    /// engine runs JS on one thread at a time (cooperative generator handoff), so
    /// this plain counter needs no synchronization.</summary>
    internal static int ProtoEpoch;

    private void BumpEpochIfPrototype()
    {
        if (_isUsedAsPrototype)
        {
            ProtoEpoch++;
        }
    }

    /// <summary>True iff this object stores its string data properties in the
    /// shape/slots backing, so the inline cache may read a slot directly. Exotic
    /// subclasses that override property access (arrays, proxies, string
    /// objects, mapped arguments, typed arrays, module namespaces) disable it so
    /// the cache always takes their slow path — important because such an exotic
    /// can share a Shape with a plain object (e.g. a mapped <c>arguments</c>
    /// object and <c>{0:…,1:…}</c>), and its overridden <c>Get</c> may return
    /// something other than the raw slot (a live binding, a trap result).</summary>
    internal bool SupportsInlineCache => _supportsInlineCache;

    /// <summary>Opt this object out of inline-cache fast paths. Called from an
    /// exotic subclass constructor.</summary>
    private protected void DisableInlineCache() => _supportsInlineCache = false;

    /// <summary>Insert or update a string-keyed descriptor in dictionary mode,
    /// maintaining <see cref="_stringKeyOrder"/>. A brand-new key is appended
    /// (correct creation order even after a prior delete); an existing key keeps
    /// its position.</summary>
    private void PutString(string name, PropertyDescriptor desc)
    {
        if (!_properties!.ContainsKey(name))
        {
            _stringKeyOrder!.Add(name);
        }

        _properties[name] = desc;
        BumpEpochIfPrototype();
    }

    /// <summary>Remove a string-keyed descriptor, keeping
    /// <see cref="_stringKeyOrder"/> in sync.</summary>
    private bool RemoveString(string name)
    {
        if (!_properties!.Remove(name))
        {
            return false;
        }

        _stringKeyOrder!.Remove(name);
        BumpEpochIfPrototype();
        return true;
    }

    /// <summary>Attribute flags (Shape.Writable/Enumerable/Configurable) for a
    /// data descriptor.</summary>
    private static byte DataFlags(in PropertyDescriptor desc)
    {
        byte f = 0;
        if (desc.Writable)
        {
            f |= Shape.Writable;
        }

        if (desc.Enumerable)
        {
            f |= Shape.Enumerable;
        }

        if (desc.Configurable)
        {
            f |= Shape.Configurable;
        }

        return f;
    }

    /// <summary>Append a new string data property in fast mode: transition to
    /// the child shape and store the value in its slot.</summary>
    private void AddFastProperty(string name, JsValue value, byte flags)
    {
        var next = _shape!.Transition(name, flags);
        if (_slots.Length < next.SlotCount)
        {
            var cap = _slots.Length == 0 ? 4 : _slots.Length * 2;
            if (cap < next.SlotCount)
            {
                cap = next.SlotCount;
            }

            System.Array.Resize(ref _slots, cap);
        }
        _slots[next.AddedSlot] = value;
        _shape = next;
        BumpEpochIfPrototype();
    }

    /// <summary>Collapse fast-mode shape/slots into the dictionary backing. Used
    /// before any operation a shape cannot model (accessor, delete, attribute
    /// redefinition, non-extensible churn). Idempotent.</summary>
    private void MigrateToDictionary()
    {
        if (_shape is null)
        {
            return;
        }

        var shape = _shape;
        var keys = shape.OrderedKeys();
        var shapeProps = shape.OrderedProps(); // slot-ordered, single chain walk — no flattened table
        var props = new Dictionary<string, PropertyDescriptor>(keys.Count, StringComparer.Ordinal);
        var order = new List<string>(keys.Count);
        for (var i = 0; i < keys.Count; i++) // creation (slot) order
        {
            var key = keys[i];
            var p = shapeProps[i];
            props[key] = PropertyDescriptor.Data(_slots[p.Slot], p.Writable, p.Enumerable, p.Configurable);
            order.Add(key);
        }
        _properties = props;
        _stringKeyOrder = order;
        _shape = null;
        _slots = System.Array.Empty<JsValue>();
        // A prototype leaving fast mode invalidates any proto-read cache that
        // referenced one of its slots.
        BumpEpochIfPrototype();
    }

    /// <summary>The [[Prototype]] internal slot. Mutate via
    /// <see cref="SetPrototypeOf"/> so subclasses can override.</summary>
    public JsObject? Prototype { get; private set; }

    /// <summary>The [[Extensible]] internal slot. When false, new own properties
    /// are rejected. Virtual so exotic objects (notably <see cref="JsProxy"/>)
    /// can route through the <c>isExtensible</c> trap.</summary>
    public virtual bool Extensible => _extensible;

    /// <summary>Backing field for <see cref="Extensible"/>; mutated only by
    /// <see cref="PreventExtensions"/> on ordinary objects.</summary>
    private bool _extensible = true;

    /// <summary>Models the spec's <c>[[ParameterMap]]</c> internal slot: true for
    /// an <c>arguments</c> exotic object. Read by §20.1.3.6 step 5 so
    /// <c>Object.prototype.toString</c> reports <c>"[object Arguments]"</c>
    /// (which legacy feature-detection — e.g. underscore.js <c>isArguments</c> —
    /// relies on instead of poking the poisoned <c>callee</c> accessor).</summary>
    public bool IsArgumentsExotic { get; internal set; }

    /// <summary>Models the spec's per-object [[PrivateElements]] brand set: the
    /// set of mangled private-name keys this object <em>itself</em> carries.
    /// A private field adds its mangled key when defined; instance private
    /// methods/accessors add their brands when the instance is initialized
    /// (post-<c>super()</c> for derived classes); static private members brand
    /// the constructor object only. The brand check for <c>obj.#x</c> /
    /// <c>obj.#x = v</c> / <c>obj.#m()</c> / <c>#x in obj</c> consults THIS set
    /// directly (never walking the prototype chain), so a subclass constructor
    /// or a Proxy — which do not carry the brand — correctly throw a TypeError
    /// per §13.3.4 PrivateGet/PrivateSet/PrivateElementFind. Null until the
    /// first brand is added, to keep ordinary objects allocation-free.</summary>
    private HashSet<string>? _privateBrands;

    /// <summary>True iff this object itself carries the brand for the given
    /// mangled private-name key (see <see cref="_privateBrands"/>). Does NOT
    /// walk the prototype chain — a derived/other-receiver constructor or a
    /// Proxy lacks the brand and must throw a TypeError on private access.</summary>
    public bool HasPrivateBrand(string mangledName)
        => _privateBrands is not null && _privateBrands.Contains(mangledName);

    /// <summary>Install the brand for a mangled private-name key on this object
    /// (idempotent). Called when a private field is defined and when an
    /// instance is initialized with private methods/accessors, or to brand a
    /// constructor with its static private members.</summary>
    public void AddPrivateBrand(string mangledName)
        => (_privateBrands ??= new HashSet<string>(StringComparer.Ordinal)).Add(mangledName);

    public JsObject() { }

    public JsObject(JsObject? prototype)
    {
        Prototype = prototype;
        if (prototype is not null)
        {
            prototype._isUsedAsPrototype = true;
        }
    }

    /// <summary>§10.1.2 [[SetPrototypeOf]]. Returns true on success.</summary>
    public virtual bool SetPrototypeOf(JsObject? proto)
    {
        if (ReferenceEquals(proto, Prototype))
        {
            return true; // §10.1.2 — same prototype is a no-op success
        }

        if (!Extensible)
        {
            return false;
        }
        // Cycle check: walking up the new chain must not lead back to this.
        for (var p = proto; p is not null; p = p.Prototype)
        {
            if (ReferenceEquals(p, this))
            {
                return false;
            }
        }
        // A relink changes the chain for any object whose prototype is this one,
        // and migrating to dictionary mode drops this object's own shape so its
        // own caches miss. Both keep inline caches correct across __proto__ swaps.
        if (_shape is not null)
        {
            MigrateToDictionary();
        }

        BumpEpochIfPrototype();
        if (proto is not null)
        {
            proto._isUsedAsPrototype = true;
        }

        Prototype = proto;
        return true;
    }

    /// <summary>§10.1.1 [[GetPrototypeOf]]. Virtual hook so
    /// <see cref="JsProxy"/> can route through the <c>getPrototypeOf</c> trap;
    /// ordinary objects return the <see cref="Prototype"/> slot directly.</summary>
    public virtual JsObject? GetPrototypeOf() => Prototype;

    /// <summary>§10.1.3 [[PreventExtensions]]. Virtual so <see cref="JsProxy"/>
    /// can route through the <c>preventExtensions</c> trap. Migrates to
    /// dictionary mode so the invariant "fast-mode object is extensible" holds —
    /// the write inline cache's add path relies on it to skip an extensibility
    /// check.</summary>
    public virtual bool PreventExtensions()
    {
        if (_shape is not null)
        {
            MigrateToDictionary();
        }

        _extensible = false;
        return true;
    }

    /// <summary>Own string-keyed lookup over both storage modes. Returns true if
    /// the property exists (mirroring the legacy chain-walk's "stop here"
    /// semantics for accessors, which read back as Undefined).</summary>
    private bool TryGetOwnRaw(string name, out JsValue value)
    {
        if (_shape is not null)
        {
            if (_shape.TryGet(name, out var p)) { value = _slots[p.Slot]; return true; }
            value = default;
            return false;
        }
        if (_properties is not null && _properties.TryGetValue(name, out var d))
        {
            value = d.IsAccessor ? JsValue.Undefined : d.Value;
            return true;
        }
        value = default;
        return false;
    }

    /// <summary>Spec [[Get]] simplified to data-only resolution: walks the
    /// prototype chain and returns the data slot's value, or Undefined.
    /// Accessor invocation lives in <c>AbstractOperations.Get</c> which has the
    /// VM in scope to dispatch the getter.</summary>
    public virtual JsValue Get(string name)
    {
        for (var o = this; o is not null; o = o.Prototype)
        {
            if (o.TryGetOwnRaw(name, out var v))
            {
                return v;
            }
        }

        return JsValue.Undefined;
    }

    public virtual JsValue Get(JsSymbol symbol)
    {
        for (var o = this; o is not null; o = o.Prototype)
        {
            if (o._symbolProperties is not null && o._symbolProperties.TryGetValue(symbol, out var desc))
            {
                return desc.IsAccessor ? JsValue.Undefined : desc.Value;
            }
        }
        return JsValue.Undefined;
    }

    public JsValue Get(JsPropertyKey key) => key.IsSymbol ? Get(key.AsSymbol) : Get(key.AsString);

    /// <summary>Spec [[Set]] simplified: own-data-property fast path for the
    /// vast majority of writes. Accessor + cross-chain writes go through
    /// <c>AbstractOperations.Set</c> (VM-aware).</summary>
    public virtual void Set(string name, JsValue value)
    {
        if (_shape is not null)
        {
            if (_shape.TryGet(name, out var p))
            {
                if (!p.Writable)
                {
                    return;
                }

                _slots[p.Slot] = value;
                return;
            }
            if (!Extensible)
            {
                return;
            }

            AddFastProperty(name, value, Shape.DefaultData);
            return;
        }
        if (_properties!.TryGetValue(name, out var desc))
        {
            if (desc.IsAccessor)
            {
                return; // accessor path — caller should use AbstractOperations.Set
            }

            if (!desc.Writable)
            {
                return;
            }

            _properties[name] = desc.WithValue(value); // existing key — order unchanged
            return;
        }
        if (!Extensible)
        {
            return;
        }

        PutString(name, PropertyDescriptor.Data(value));
    }

    public virtual void Set(JsSymbol symbol, JsValue value)
    {
        if (_symbolProperties is not null && _symbolProperties.TryGetValue(symbol, out var desc))
        {
            if (desc.IsAccessor)
            {
                return;
            }

            if (!desc.Writable)
            {
                return;
            }

            _symbolProperties[symbol] = desc.WithValue(value);
            return;
        }
        if (!Extensible)
        {
            return;
        }

        (_symbolProperties ??= new Dictionary<JsSymbol, PropertyDescriptor>())[symbol] = PropertyDescriptor.Data(value);
    }

    public void Set(JsPropertyKey key, JsValue value)
    {
        if (key.IsSymbol)
        {
            Set(key.AsSymbol, value);
        }
        else
        {
            Set(key.AsString, value);
        }
    }

    /// <summary>Spec [[HasProperty]] — walks the prototype chain. Virtual so
    /// <see cref="JsProxy"/> can route through the <c>has</c> trap. Uses
    /// <see cref="HasOwn(string)"/> so exotic subclasses (JsArray indices,
    /// JsTypedArray elements) participate in the walk.</summary>
    public virtual bool Has(string name)
    {
        for (var o = this; o is not null; o = o.Prototype)
        {
            if (o.HasOwn(name))
            {
                return true;
            }
        }

        return false;
    }

    public virtual bool Has(JsSymbol symbol)
    {
        for (var o = this; o is not null; o = o.Prototype)
        {
            if (o.HasOwn(symbol))
            {
                return true;
            }
        }

        return false;
    }

    public bool Has(JsPropertyKey key) => key.IsSymbol ? Has(key.AsSymbol) : Has(key.AsString);

    /// <summary>§10.1.5 [[GetOwnProperty]] — own slot only, no chain walk.</summary>
    public virtual bool HasOwn(string name)
        => _shape is not null ? _shape.Contains(name) : _properties!.ContainsKey(name);
    public virtual bool HasOwn(JsSymbol symbol)
        => _symbolProperties is not null && _symbolProperties.ContainsKey(symbol);
    public bool HasOwn(JsPropertyKey key) => key.IsSymbol ? HasOwn(key.AsSymbol) : HasOwn(key.AsString);

    /// <summary>Returns the own descriptor, or <c>null</c> if no own property.</summary>
    public virtual PropertyDescriptor? GetOwnPropertyDescriptor(string name)
    {
        if (_shape is not null)
        {
            return _shape.TryGet(name, out var p)
                ? PropertyDescriptor.Data(_slots[p.Slot], p.Writable, p.Enumerable, p.Configurable)
                : null;
        }

        return _properties!.TryGetValue(name, out var d) ? d : null;
    }
    public virtual PropertyDescriptor? GetOwnPropertyDescriptor(JsSymbol symbol)
        => _symbolProperties is not null && _symbolProperties.TryGetValue(symbol, out var d) ? d : null;
    public PropertyDescriptor? GetOwnPropertyDescriptor(JsPropertyKey key)
        => key.IsSymbol ? GetOwnPropertyDescriptor(key.AsSymbol) : GetOwnPropertyDescriptor(key.AsString);

    /// <summary>§10.1.6 [[DefineOwnProperty]] (simplified validator). Returns
    /// false when the operation is rejected (e.g. non-configurable conflict
    /// or non-extensible object).</summary>
    public virtual bool DefineOwnProperty(string name, PropertyDescriptor desc)
    {
        // Fast path: defining a plain data property on a shape-mode object.
        if (_shape is not null && !desc.IsAccessor)
        {
            if (_shape.TryGet(name, out var p))
            {
                var existing = PropertyDescriptor.Data(_slots[p.Slot], p.Writable, p.Enumerable, p.Configurable);
                if (!ValidateRedefine(existing, desc))
                {
                    return false;
                }

                if (DataFlags(desc) == p.Flags)
                {
                    _slots[p.Slot] = desc.Value; // attributes unchanged — just replace value
                    return true;
                }
                // Attribute change: fall to dictionary and apply there.
                MigrateToDictionary();
                PutString(name, desc);
                return true;
            }
            if (!Extensible)
            {
                return false;
            }

            AddFastProperty(name, desc.Value, DataFlags(desc));
            return true;
        }

        // Accessor, or already in dictionary mode: use the dictionary path.
        if (_shape is not null)
        {
            MigrateToDictionary();
        }

        if (_properties!.TryGetValue(name, out var ex))
        {
            if (!ValidateRedefine(ex, desc))
            {
                return false;
            }
        }
        else if (!Extensible)
        {
            return false;
        }

        PutString(name, desc);
        return true;
    }

    public virtual bool DefineOwnProperty(JsSymbol symbol, PropertyDescriptor desc)
        => DefineOwnPropertyCore(_symbolProperties ??= new Dictionary<JsSymbol, PropertyDescriptor>(), symbol, desc);

    public bool DefineOwnProperty(JsPropertyKey key, PropertyDescriptor desc)
        => key.IsSymbol ? DefineOwnProperty(key.AsSymbol, desc) : DefineOwnProperty(key.AsString, desc);

    /// <summary>Write an own string-keyed property descriptor with NO validation
    /// — the caller has already performed §10.1.6.3 ValidateAndApplyPropertyDescriptor.
    /// Used by §10.4.4.2 mapped-arguments [[DefineOwnProperty]], whose partial-field
    /// merge cannot be expressed through the simplified <see cref="DefineOwnProperty(string, PropertyDescriptor)"/>
    /// validator (which rejects a non-configurable data property's legal
    /// writable:true→false transition).</summary>
    internal void ForceDefineOwnProperty(string name, PropertyDescriptor desc)
    {
        if (_shape is not null)
        {
            MigrateToDictionary();
        }

        PutString(name, desc);
    }

    /// <summary>Symbol-keyed counterpart of <see cref="ForceDefineOwnProperty(string, PropertyDescriptor)"/>
    /// — bypasses §10.1.6.3 validation. Used by the partial-merge path so a
    /// non-configurable data property's legal writable:true→false transition
    /// can be applied to a symbol-keyed slot too.</summary>
    internal void ForceDefineOwnProperty(JsSymbol symbol, PropertyDescriptor desc)
        => (_symbolProperties ??= new Dictionary<JsSymbol, PropertyDescriptor>())[symbol] = desc;

    /// <summary>§10.1.6.3 ValidateAndApplyPropertyDescriptor restricted to what a
    /// partial <c>Object.defineProperty</c> / <c>Reflect.defineProperty</c>
    /// redefinition needs: only the fields the caller actually specified
    /// (<paramref name="present"/>) are applied; the rest are inherited from the
    /// existing own descriptor (or defaulted to false / undefined for a fresh
    /// property). Returns false when the change is rejected by a
    /// non-configurable conflict or non-extensible add. Symbol- and
    /// string-keyed slots share one implementation.</summary>
    internal virtual bool DefineOwnPropertyPartial(JsPropertyKey key, PropertyDescriptor desc, DescriptorFields present)
    {
        // The partial-merge logic operates through GetOwnPropertyDescriptor /
        // ForceDefineOwnProperty; for string keys, move to dictionary mode first
        // so its intricate non-configurable validation runs against the legacy
        // backing exactly as before.
        if (key.IsString && _shape is not null)
        {
            MigrateToDictionary();
        }

        var existing = GetOwnPropertyDescriptor(key);
        if (existing is null)
        {
            if (!Extensible)
            {
                return false;
            }

            var fresh = desc.IsAccessor
                ? PropertyDescriptor.Accessor(
                    present.HasGet ? desc.Getter : null,
                    present.HasSet ? desc.Setter : null,
                    present.HasEnumerable && desc.Enumerable,
                    present.HasConfigurable && desc.Configurable)
                : PropertyDescriptor.Data(
                    present.HasValue ? desc.Value : JsValue.Undefined,
                    present.HasWritable && desc.Writable,
                    present.HasEnumerable && desc.Enumerable,
                    present.HasConfigurable && desc.Configurable);
            if (key.IsSymbol)
            {
                ForceDefineOwnProperty(key.AsSymbol, fresh);
            }
            else
            {
                ForceDefineOwnProperty(key.AsString, fresh);
            }

            return true;
        }

        var cur = existing.Value;
        var changingKind = desc.IsAccessor != cur.IsAccessor;
        var enumerable = present.HasEnumerable ? desc.Enumerable : cur.Enumerable;
        var configurable = present.HasConfigurable ? desc.Configurable : cur.Configurable;

        if (!cur.Configurable)
        {
            if (configurable)
            {
                return false;
            }

            if (present.HasEnumerable && desc.Enumerable != cur.Enumerable)
            {
                return false;
            }

            if (changingKind)
            {
                return false;
            }

            if (!cur.IsAccessor)
            {
                if (!cur.Writable)
                {
                    if (present.HasWritable && desc.Writable)
                    {
                        return false;
                    }

                    if (present.HasValue && !desc.Value.Equals(cur.Value))
                    {
                        return false;
                    }
                }
            }
            else
            {
                if (present.HasGet && !ReferenceEquals(desc.Getter, cur.Getter))
                {
                    return false;
                }

                if (present.HasSet && !ReferenceEquals(desc.Setter, cur.Setter))
                {
                    return false;
                }
            }
        }

        PropertyDescriptor merged;
        if (desc.IsAccessor)
        {
            merged = PropertyDescriptor.Accessor(
                present.HasGet ? desc.Getter : (cur.IsAccessor ? cur.Getter : null),
                present.HasSet ? desc.Setter : (cur.IsAccessor ? cur.Setter : null),
                enumerable, configurable);
        }
        else
        {
            var writable = present.HasWritable ? desc.Writable : (!cur.IsAccessor && cur.Writable);
            var value = present.HasValue ? desc.Value : (cur.IsAccessor ? JsValue.Undefined : cur.Value);
            merged = PropertyDescriptor.Data(value, writable, enumerable, configurable);
        }
        if (key.IsSymbol)
        {
            ForceDefineOwnProperty(key.AsSymbol, merged);
        }
        else
        {
            ForceDefineOwnProperty(key.AsString, merged);
        }

        return true;
    }

    private bool DefineOwnPropertyCore<TKey>(Dictionary<TKey, PropertyDescriptor> table, TKey key, PropertyDescriptor desc)
        where TKey : notnull
    {
        if (table.TryGetValue(key, out var existing))
        {
            if (!ValidateRedefine(existing, desc))
            {
                return false;
            }
        }
        else if (!Extensible)
        {
            return false;
        }
        table[key] = desc;
        return true;
    }

    /// <summary>Subset of §10.1.6.3 ValidateAndApplyPropertyDescriptor's
    /// non-configurable conflict checks — false rejects the redefine. Pulled
    /// out so the string-keyed <see cref="DefineOwnProperty(string, PropertyDescriptor)"/>
    /// can share the rules with the symbol-keyed
    /// <see cref="DefineOwnPropertyCore"/> path.</summary>
    private static bool ValidateRedefine(PropertyDescriptor existing, PropertyDescriptor desc)
    {
        if (existing.Configurable)
        {
            return true;
        }

        if (existing.IsAccessor != desc.IsAccessor)
        {
            return false;
        }

        if (existing.Configurable != desc.Configurable)
        {
            return false;
        }

        if (existing.Enumerable != desc.Enumerable)
        {
            return false;
        }

        if (!existing.IsAccessor && existing.Writable != desc.Writable)
        {
            return false;
        }

        return true;
    }

    /// <summary>§10.1.10 [[Delete]] — returns true on success, false if the
    /// property exists and is non-configurable.</summary>
    public virtual bool Delete(string name)
    {
        if (_shape is not null)
        {
            if (!_shape.Contains(name))
            {
                return true; // absent — nothing to delete
            }

            MigrateToDictionary();
        }
        if (!_properties!.TryGetValue(name, out var desc))
        {
            return true;
        }

        if (!desc.Configurable)
        {
            return false;
        }

        return RemoveString(name);
    }

    public virtual bool Delete(JsSymbol symbol)
    {
        if (_symbolProperties is null || !_symbolProperties.TryGetValue(symbol, out var desc))
        {
            return true;
        }

        if (!desc.Configurable)
        {
            return false;
        }

        return _symbolProperties.Remove(symbol);
    }

    public bool Delete(JsPropertyKey key) => key.IsSymbol ? Delete(key.AsSymbol) : Delete(key.AsString);

    /// <summary>§10.1.11.1 OrdinaryOwnPropertyKeys ordering for the string
    /// keys of this object's property bag: every <em>array-index</em> key
    /// (canonical numeric string in [0, 2^32-1) — the
    /// <see cref="JsArray.IsArrayIndex"/> test) first, in ascending numeric
    /// order, then every remaining String key in property-creation order.
    /// Creation order comes from the <see cref="Shape"/> transition chain in
    /// fast mode, or <see cref="_stringKeyOrder"/> in dictionary mode. Non-index
    /// numeric-looking keys ("1e+55", "-1", "4294967295") stay in the string
    /// bucket per spec.</summary>
    private IEnumerable<string> OrderedStringKeys()
    {
        IReadOnlyList<string> creation = _shape is not null ? _shape.OrderedKeys() : _stringKeyOrder!;

        // Fast path: no array-index keys → emit in chronological creation order.
        List<uint>? indices = null;
        foreach (var key in creation)
        {
            if (JsArray.IsArrayIndex(key, out var idx))
            {
                (indices ??= new List<uint>()).Add(idx);
            }
        }
        if (indices is null)
        {
            foreach (var key in creation)
            {
                yield return key;
            }

            yield break;
        }
        indices.Sort();
        foreach (var idx in indices)
        {
            yield return JsArray.IndexToString(idx);
        }

        foreach (var key in creation)
        {
            if (!JsArray.IsArrayIndex(key, out _))
            {
                yield return key;
            }
        }
    }

    /// <summary>All own string keys in §10.1.11.1 order (integer indices
    /// ascending, then other strings in creation order).</summary>
    public virtual IEnumerable<string> Keys => OrderedStringKeys();
    public IEnumerable<JsSymbol> SymbolKeys
        => _symbolProperties is not null ? _symbolProperties.Keys : System.Linq.Enumerable.Empty<JsSymbol>();
    public virtual IEnumerable<JsPropertyKey> OwnPropertyKeys
    {
        get
        {
            foreach (var key in OrderedStringKeys())
            {
                yield return JsPropertyKey.String(key);
            }

            if (_symbolProperties is not null)
            {
                foreach (var key in _symbolProperties.Keys)
                {
                    yield return JsPropertyKey.Symbol(key);
                }
            }
        }
    }

    /// <summary>Own keys filtered to enumerable data properties, in
    /// §10.1.11.1 order — used by <c>Object.keys</c> and friends.</summary>
    public virtual IEnumerable<string> EnumerableKeys()
    {
        foreach (var key in OrderedStringKeys())
        {
            if (IsEnumerableOwnString(key))
            {
                yield return key;
            }
        }
    }

    private bool IsEnumerableOwnString(string key)
        => _shape is not null
            ? (_shape.TryGet(key, out var p) && p.Enumerable)
            : (_properties!.TryGetValue(key, out var d) && d.Enumerable);

    public IEnumerable<JsSymbol> EnumerableSymbolKeys()
    {
        if (_symbolProperties is null)
        {
            yield break;
        }

        foreach (var pair in _symbolProperties)
        {
            if (pair.Value.Enumerable)
            {
                yield return pair.Key;
            }
        }
    }

    public override string ToString() => "[object Object]";
}

/// <summary>
/// A native (host-side) function callable from JS. The body receives the
/// resolved <c>this</c> value and an argument array; returns a
/// <see cref="JsValue"/>. <see cref="IsConstructor"/> opts into being callable
/// via <c>new</c> (defaults to true so intrinsic constructors like
/// <c>new Array</c> work).
/// </summary>
/// <remarks>
/// <para><b>Prototype chain (B2-2):</b> when constructed with a non-null
/// <c>realm</c>, the function's <c>[[Prototype]]</c> is set to
/// <see cref="JsRealm.FunctionPrototype"/> so it inherits <c>call</c>,
/// <c>apply</c>, <c>bind</c>, and <c>toString</c>. Pass the realm in
/// whenever the call site has one in scope (every <c>Install(realm)</c>
/// intrinsic does). Legacy call sites that pass no realm produce a
/// function whose prototype chain is empty; that's tolerated for now but
/// will silently break <c>fn.call(...)</c> against the resulting instance,
/// so prefer the realm-aware overload for new code.</para>
/// </remarks>
public sealed class JsNativeFunction : JsObject
{
    public string Name { get; }
    public Func<JsValue, JsValue[], JsValue> Body { get; }
    public bool IsConstructor { get; }

    /// <summary>Declared <c>length</c> (declared positional arity). Mirrored
    /// as an own non-enumerable property when set via the realm-aware
    /// constructor; intrinsics that build descriptors by hand may also stamp
    /// it directly via DefineOwnProperty.</summary>
    public int Length { get; }

    /// <summary>Construct with a (thisValue, args) callable.</summary>
    public JsNativeFunction(string name, Func<JsValue, JsValue[], JsValue> body, bool isConstructor = true)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Body = body ?? throw new ArgumentNullException(nameof(body));
        IsConstructor = isConstructor;
        Length = 0;
    }

    /// <summary>The shared fast-mode shape every native function adopts:
    /// <c>Root → {name} → {name,length}</c>, both properties W=false/E=false/C=true
    /// (§17 builtin function defaults). Built once via <see cref="Shape.Transition"/>
    /// so its identity is exactly the shape an incremental two-property install
    /// would have produced — inline caches and dictionary migration see no
    /// difference. <c>name</c> lands in slot 0, <c>length</c> in slot 1.</summary>
    internal static readonly Shape NameLengthShape =
        Shape.Root
            .Transition("name", Shape.Configurable)
            .Transition("length", Shape.Configurable);

    /// <summary>Realm-aware constructor — wires
    /// <c>[[Prototype]] = realm.FunctionPrototype</c> and stamps
    /// own <c>name</c> + <c>length</c> data properties (W=false, E=false,
    /// C=true per §17 builtin function defaults). Prefer this overload when
    /// the realm is in scope.</summary>
    public JsNativeFunction(JsRealm realm, string name, int length, Func<JsValue, JsValue[], JsValue> body, bool isConstructor = false)
        : base(realm?.FunctionPrototype)
    {
        ArgumentNullException.ThrowIfNull(realm);
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Body = body ?? throw new ArgumentNullException(nameof(body));
        IsConstructor = isConstructor;
        Length = length;
        // Adopt the shared name/length shape with a pre-filled 2-slot array
        // instead of two DefineOwnProperty transitions. Byte-identical result,
        // no per-property shape transition or table flatten at bootstrap.
        AdoptShape(NameLengthShape, new[] { JsValue.String(name), JsValue.Number(length) });
    }

    /// <summary>Convenience overload for legacy (args-only) host functions.</summary>
    public JsNativeFunction(string name, Func<JsValue[], JsValue> body)
        : this(name, (_, a) => body(a), isConstructor: false) { }

    public override string ToString() => $"function {Name}() {{ [native code] }}";
}
