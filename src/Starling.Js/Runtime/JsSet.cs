namespace Starling.Js.Runtime;

/// <summary>
/// §24.2 Set Objects — insertion-ordered unique-value collection keyed by
/// SameValueZero. Tombstone strategy matches <see cref="JsMap"/>.
/// </summary>
public sealed class JsSet : JsObject
{
    private const int CompactMinHoles = 64;
    private const double CompactRatio = 0.5;

    private readonly List<Slot> _entries = new();
    private readonly Dictionary<JsValue, int> _lookup = new(SameValueZeroComparer.Instance);
    private int _holes;

    public JsSet(JsRealm realm) : base(realm.SetPrototype) { }

    public int Count => _entries.Count - _holes;
    internal int SlotCount => _entries.Count;

    internal bool TryGetSlot(int index, out JsValue value)
    {
        if ((uint)index >= (uint)_entries.Count || !_entries[index].Alive)
        {
            value = JsValue.Undefined;
            return false;
        }
        value = _entries[index].Value;
        return true;
    }

    public bool Has(JsValue value) => _lookup.ContainsKey(NormalizeKey(value));

    public void Add(JsValue value)
    {
        var k = NormalizeKey(value);
        if (_lookup.ContainsKey(k))
        {
            return;
        }

        _lookup[k] = _entries.Count;
        _entries.Add(new Slot(k, alive: true));
    }

    public bool Delete(JsValue value)
    {
        var k = NormalizeKey(value);
        if (!_lookup.TryGetValue(k, out var i))
        {
            return false;
        }

        _entries[i] = new Slot(JsValue.Undefined, alive: false);
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

    public IEnumerable<JsValue> LiveValues()
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var slot = _entries[i];
            if (slot.Alive)
            {
                yield return slot.Value;
            }
        }
    }

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

            _lookup[slot.Value] = live.Count;
            live.Add(slot);
        }
        _entries.Clear();
        _entries.AddRange(live);
        _holes = 0;
    }

    private readonly struct Slot
    {
        public readonly JsValue Value;
        public readonly bool Alive;
        public Slot(JsValue value, bool alive) { Value = value; Alive = alive; }
    }
}
