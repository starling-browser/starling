namespace Starling.Js.Runtime;

/// <summary>
/// §24.1 Map Objects — insertion-ordered associative storage keyed by
/// SameValueZero. Backing storage:
/// <list type="bullet">
///   <item><description><c>_entries</c> — append-only list of slots,
///     preserving insertion order. Deleted slots are tombstoned in place so
///     concurrent iterators don't skip neighbours.</description></item>
///   <item><description><c>_lookup</c> — index map keyed by SameValueZero;
///     points at the live slot in <c>_entries</c>.</description></item>
/// </list>
/// We compact (rebuild both structures) once tombstones make up more than
/// half the list and there are at least 64 of them. The amortised cost stays
/// O(1) per op while keeping pending iterators correct between rebuilds.
/// </summary>
public sealed class JsMap : JsObject
{
    private const int CompactMinHoles = 64;
    private const double CompactRatio = 0.5;

    private readonly List<Slot> _entries = new();
    private readonly Dictionary<JsValue, int> _lookup = new(SameValueZeroComparer.Instance);
    private int _holes;

    public JsMap(JsRealm realm) : base(realm.MapPrototype) { }

    /// <summary>Live entry count (excludes tombstones).</summary>
    public int Count => _entries.Count - _holes;

    /// <summary>Total slot count including tombstones — used by iterators to
    /// know where the high-water mark is. Iterators capture this lazily as
    /// they advance.</summary>
    internal int SlotCount => _entries.Count;

    internal bool TryGetSlot(int index, out JsValue key, out JsValue value)
    {
        if ((uint)index >= (uint)_entries.Count || !_entries[index].Alive)
        {
            key = JsValue.Undefined;
            value = JsValue.Undefined;
            return false;
        }
        key = _entries[index].Key;
        value = _entries[index].Value;
        return true;
    }

    public bool Has(JsValue key) => _lookup.ContainsKey(NormalizeKey(key));

    public JsValue Get(JsValue key)
    {
        return _lookup.TryGetValue(NormalizeKey(key), out var i)
            ? _entries[i].Value
            : JsValue.Undefined;
    }

    public void Set(JsValue key, JsValue value)
    {
        var k = NormalizeKey(key);
        if (_lookup.TryGetValue(k, out var i))
        {
            _entries[i] = new Slot(k, value, alive: true);
            return;
        }
        _lookup[k] = _entries.Count;
        _entries.Add(new Slot(k, value, alive: true));
    }

    public bool Delete(JsValue key)
    {
        var k = NormalizeKey(key);
        if (!_lookup.TryGetValue(k, out var i))
        {
            return false;
        }

        _entries[i] = new Slot(JsValue.Undefined, JsValue.Undefined, alive: false);
        _lookup.Remove(k);
        _holes++;
        MaybeCompact();
        return true;
    }

    public void Clear()
    {
        _entries.Clear();
        _lookup.Clear();
        _holes = 0;
    }

    /// <summary>Enumerate live slots in insertion order. Safe to mutate the
    /// map during enumeration — added entries become visible at their slot
    /// index, deleted entries are skipped.</summary>
    public IEnumerable<(JsValue Key, JsValue Value)> LiveEntries()
    {
        // Capture an index so newly-added entries are still seen.
        for (var i = 0; i < _entries.Count; i++)
        {
            var slot = _entries[i];
            if (slot.Alive)
            {
                yield return (slot.Key, slot.Value);
            }
        }
    }

    /// <summary>§7.2.11 normalizer — folds <c>-0</c> to <c>+0</c> so the
    /// dictionary stores a single canonical key per Map spec semantics.</summary>
    private static JsValue NormalizeKey(JsValue key)
    {
        if (key.Kind == JsValueKind.Number && key.AsNumber == 0.0)
        {
            return JsValue.Zero;
        }

        return key;
    }

    private void MaybeCompact()
    {
        if (_holes < CompactMinHoles)
        {
            return;
        }

        if ((double)_holes / Math.Max(1, _entries.Count) < CompactRatio)
        {
            return;
        }

        var live = new List<Slot>(_entries.Count - _holes);
        _lookup.Clear();
        for (var i = 0; i < _entries.Count; i++)
        {
            var slot = _entries[i];
            if (!slot.Alive)
            {
                continue;
            }

            _lookup[slot.Key] = live.Count;
            live.Add(slot);
        }
        _entries.Clear();
        _entries.AddRange(live);
        _holes = 0;
    }

    private readonly struct Slot
    {
        public readonly JsValue Key;
        public readonly JsValue Value;
        public readonly bool Alive;
        public Slot(JsValue k, JsValue v, bool alive) { Key = k; Value = v; Alive = alive; }
    }
}
