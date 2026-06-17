using System.Globalization;

namespace Starling.Js.Runtime;

/// <summary>
/// ES2024 §10.4.2 Array exotic object. Dense backing storage with a magic
/// <c>length</c> property that stays in lockstep with the indexed slots, plus
/// a fallback to the ordinary property bag for non-index/non-length keys.
/// </summary>
/// <remarks>
/// <para>The exotic dispatch lives in our overridden
/// GetOwnPropertyDescriptor / DefineOwnProperty / HasOwn / Delete / Keys /
/// OwnPropertyKeys / EnumerableKeys. We also override the virtual
/// <see cref="JsObject.Get(string)"/> / <see cref="JsObject.Set(string, JsValue)"/>
/// pair so intrinsic helpers reading <c>arr.length</c> or <c>arr[3]</c>
/// don't allocate a descriptor struct.</para>
/// </remarks>
public sealed class JsArray : JsObject
{
    private readonly List<JsValue> _items = new();

    public JsArray(JsRealm realm) : base(realm.ArrayPrototype) { DisableInlineCache(); }

    /// <summary>Create an empty array whose dense backing is pre-sized to
    /// <paramref name="capacity"/> slots, so a known number of subsequent
    /// <see cref="Push(JsValue)"/> calls grow the list without reallocating.
    /// Length stays 0 until items are added.</summary>
    public JsArray(JsRealm realm, int capacity) : base(realm.ArrayPrototype)
    {
        DisableInlineCache();
        if (capacity > 0)
        {
            _items.Capacity = capacity;
        }
    }

    public JsArray(JsRealm realm, IReadOnlyList<JsValue> items) : base(realm.ArrayPrototype)
    {
        DisableInlineCache();
        _items.AddRange(items);
    }

    /// <summary>Live element count. Driven by <see cref="_items"/>.Count; the
    /// "length" data slot is synthesized on the fly so we never store it twice.</summary>
    public int Length => _items.Count;

    /// <summary>Direct access to the dense slot (for native-side fast paths).
    /// Out-of-range reads return <see cref="JsValue.Undefined"/>.</summary>
    public JsValue this[int index]
    {
        get => (uint)index < (uint)_items.Count ? _items[index] : JsValue.Undefined;
        set => SetIndex(index, value);
    }

    /// <summary>Append without going through descriptor machinery.</summary>
    public void Push(JsValue value) => _items.Add(value);

    /// <summary>§7.2.2 IsArray. Proxies around arrays unwrap recursively and
    /// revoked proxies throw.</summary>
    public static bool IsArray(JsValue v, JsRealm? realm = null)
    {
        if (!v.IsObject)
        {
            return false;
        }

        var obj = v.AsObject;
        while (obj is JsProxy proxy)
        {
            if (proxy.Target is null)
            {
                throw new JsThrow(realm is not null
                    ? realm.NewTypeError("Cannot perform IsArray on a revoked proxy")
                    : JsValue.String("Cannot perform IsArray on a revoked proxy"));
            }

            obj = proxy.Target;
        }
        return obj is JsArray;
    }

    /// <summary>Canonical array index per §6.1.7: a String <c>P</c> such that
    /// <c>ToString(ToUint32(P)) === P</c> and value &lt; 2^32 - 1.</summary>
    public static bool IsArrayIndex(string key, out uint index)
    {
        index = 0;
        if (key.Length == 0)
        {
            return false;
        }
        // No leading zeros (except literal "0").
        if (key[0] == '0' && key.Length > 1)
        {
            return false;
        }
        // Digits-only check.
        for (var i = 0; i < key.Length; i++)
        {
            if (key[i] < '0' || key[i] > '9')
            {
                return false;
            }
        }
        if (!uint.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out index))
        {
            return false;
        }

        return index < uint.MaxValue;
    }

    public static string IndexToString(uint i) => i.ToString(CultureInfo.InvariantCulture);

    // ---------------- Overridden hot-path accessors ----------------

    public override JsValue Get(string name)
    {
        if (name == "length")
        {
            return JsValue.Number(_items.Count);
        }

        if (IsArrayIndex(name, out var idx) && idx < _items.Count)
        {
            return _items[(int)idx];
        }

        return base.Get(name);
    }

    public override void Set(string name, JsValue value)
    {
        if (name == "length")
        {
            SetLength(value);
            return;
        }
        if (IsArrayIndex(name, out var idx))
        {
            SetIndex((int)idx, value);
            return;
        }
        base.Set(name, value);
    }

    public override bool HasOwn(string name)
    {
        if (name == "length")
        {
            return true;
        }

        if (IsArrayIndex(name, out var idx) && idx < _items.Count)
        {
            return true;
        }

        return base.HasOwn(name);
    }

    public override PropertyDescriptor? GetOwnPropertyDescriptor(string name)
    {
        if (name == "length")
        {
            return PropertyDescriptor.Data(JsValue.Number(_items.Count), writable: true, enumerable: false, configurable: false);
        }

        if (IsArrayIndex(name, out var idx) && idx < _items.Count)
        {
            return PropertyDescriptor.Data(_items[(int)idx], writable: true, enumerable: true, configurable: true);
        }

        return base.GetOwnPropertyDescriptor(name);
    }

    public override bool DefineOwnProperty(string name, PropertyDescriptor desc)
    {
        if (name == "length")
        {
            if (desc.IsAccessor)
            {
                return false;
            }

            SetLength(desc.Value);
            return true;
        }
        if (IsArrayIndex(name, out var idx))
        {
            if (desc.IsAccessor)
            {
                // Accessor descriptors on indexed slots aren't supported by the dense backing.
                // Fall back to the property-bag path (spec actually allows mixing; we don't
                // for now). Document the simplification in tests if needed.
                return base.DefineOwnProperty(name, desc);
            }
            SetIndex((int)idx, desc.Value);
            return true;
        }
        return base.DefineOwnProperty(name, desc);
    }

    /// <summary>For an array-index or <c>length</c> string key the partial
    /// define must reach the dense backing (or the magic <c>length</c> setter),
    /// not the base property-bag — so we route it through the array's own
    /// [[DefineOwnProperty]] with a descriptor that merges the caller's
    /// specified fields onto the current slot's attributes. Non-index string
    /// keys and symbol keys defer to the base ordinary-object partial path.</summary>
    internal override bool DefineOwnPropertyPartial(JsPropertyKey key, PropertyDescriptor desc, DescriptorFields present)
    {
        if (!key.IsSymbol)
        {
            var s = key.AsString;
            if (s == "length" || IsArrayIndex(s, out _))
            {
                var cur = GetOwnPropertyDescriptor(s);
                var merged = MergeForExotic(cur, desc, present);
                return DefineOwnProperty(s, merged);
            }
        }
        return base.DefineOwnPropertyPartial(key, desc, present);
    }

    /// <summary>Fold the <paramref name="present"/> fields of <paramref name="desc"/>
    /// onto <paramref name="cur"/> (or onto default false/undefined for a
    /// fresh slot) to produce the resolved descriptor an exotic
    /// [[DefineOwnProperty]] expects. Mirrors the inheritance the base
    /// <see cref="JsObject.DefineOwnPropertyPartial"/> performs but returns
    /// the merged value instead of writing it.</summary>
    private static PropertyDescriptor MergeForExotic(PropertyDescriptor? cur, PropertyDescriptor desc, DescriptorFields present)
    {
        if (cur is null)
        {
            return desc.IsAccessor
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
        }
        var c = cur.Value;
        var enumerable = present.HasEnumerable ? desc.Enumerable : c.Enumerable;
        var configurable = present.HasConfigurable ? desc.Configurable : c.Configurable;
        if (desc.IsAccessor)
        {
            return PropertyDescriptor.Accessor(
                present.HasGet ? desc.Getter : (c.IsAccessor ? c.Getter : null),
                present.HasSet ? desc.Setter : (c.IsAccessor ? c.Setter : null),
                enumerable, configurable);
        }
        var writable = present.HasWritable ? desc.Writable : (!c.IsAccessor && c.Writable);
        var value = present.HasValue ? desc.Value : (c.IsAccessor ? JsValue.Undefined : c.Value);
        return PropertyDescriptor.Data(value, writable, enumerable, configurable);
    }

    public override bool Delete(string name)
    {
        if (name == "length")
        {
            return false; // non-configurable
        }

        if (IsArrayIndex(name, out var idx) && idx < _items.Count)
        {
            // Make the slot a hole (spec: delete leaves the slot absent but
            // doesn't shrink length). We model with Undefined since we don't
            // track sparse holes separately.
            _items[(int)idx] = JsValue.Undefined;
            return true;
        }
        return base.Delete(name);
    }

    public override IEnumerable<string> Keys
    {
        get
        {
            for (var i = 0; i < _items.Count; i++)
            {
                yield return IndexToString((uint)i);
            }

            foreach (var key in base.Keys)
            {
                yield return key;
            }
        }
    }

    public override IEnumerable<JsPropertyKey> OwnPropertyKeys
    {
        get
        {
            for (var i = 0; i < _items.Count; i++)
            {
                yield return JsPropertyKey.String(IndexToString((uint)i));
            }

            foreach (var key in base.OwnPropertyKeys)
            {
                yield return key;
            }
        }
    }

    public override IEnumerable<string> EnumerableKeys()
    {
        for (var i = 0; i < _items.Count; i++)
        {
            yield return IndexToString((uint)i);
        }

        foreach (var key in base.EnumerableKeys())
        {
            yield return key;
        }
    }

    // ---------------- Internals ----------------

    private void SetIndex(int index, JsValue value)
    {
        if (index < 0)
        {
            return;
        }

        if (index >= _items.Count)
        {
            // Grow with explicit Undefined holes (we don't track sparseness).
            while (_items.Count < index)
            {
                _items.Add(JsValue.Undefined);
            }

            _items.Add(value);
        }
        else
        {
            _items[index] = value;
        }
    }

    private void SetLength(JsValue value)
    {
        var n = JsValue.ToNumber(value);
        var nu = (uint)n;
        if (n != nu || double.IsNaN(n) || double.IsInfinity(n))
        {
            throw new JsThrow(JsValue.String("Invalid array length"));
        }

        var newLen = (int)nu;
        if (newLen < _items.Count)
        {
            // Shrink: remove indexed slots ≥ newLen. Also remove any own
            // string keys whose key is an integer-string ≥ newLen.
            _items.RemoveRange(newLen, _items.Count - newLen);
            // Walk base own keys for stragglers (paranoia for indices stored
            // there via mixed-mode DefineOwnProperty fallbacks).
            var stragglers = new List<string>();
            foreach (var k in base.Keys)
            {
                if (IsArrayIndex(k, out var i) && i >= newLen)
                {
                    stragglers.Add(k);
                }
            }

            foreach (var k in stragglers)
            {
                base.Delete(k);
            }
        }
        else
        {
            while (_items.Count < newLen)
            {
                _items.Add(JsValue.Undefined);
            }
        }
    }

    public override string ToString() => "[object Array]";
}
