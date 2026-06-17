namespace Starling.Net.Http.H2.Hpack;

/// <summary>
/// HPACK dynamic table (RFC 7541 §2.3.2 / §4): a FIFO of recently seen header
/// fields that follows the static table in the index space. Newly inserted
/// entries get the lowest dynamic index (combined index 62); the table evicts
/// oldest-first to stay within its size bound.
/// </summary>
/// <remarks>
/// One direction of HPACK state. A decoder owns one of these to mirror the
/// peer encoder's table; entry size accounting (name octets + value octets +
/// 32) must match the encoder exactly or the shared index space drifts.
/// </remarks>
internal sealed class HpackDynamicTable
{
    private const int EntryOverhead = 32; // RFC 7541 §4.1

    // _entries[0] is the newest entry (dynamic index 1 / combined index 62).
    private readonly List<Entry> _entries = [];

    public HpackDynamicTable(int maxSize)
    {
        MaxSize = maxSize;
    }

    /// <summary>Current upper bound on <see cref="Size"/>, in octets.</summary>
    public int MaxSize { get; private set; }

    /// <summary>Sum of all entry sizes currently held.</summary>
    public int Size { get; private set; }

    /// <summary>Number of entries currently held.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Insert a header field at the front. Evicts oldest entries until it fits;
    /// if the entry alone exceeds <see cref="MaxSize"/> the table is emptied
    /// and nothing is inserted (RFC 7541 §4.4).
    /// </summary>
    public void Add(string name, string value, int nameOctets, int valueOctets)
    {
        var entrySize = nameOctets + valueOctets + EntryOverhead;
        EvictTo(MaxSize - entrySize);
        if (entrySize > MaxSize)
        {
            return; // doesn't fit even in an empty table
        }

        _entries.Insert(0, new Entry(name, value, entrySize));
        Size += entrySize;
    }

    /// <summary>
    /// Resize the table per a dynamic table size update (RFC 7541 §6.3),
    /// evicting oldest entries as needed to satisfy the new bound.
    /// </summary>
    public void Resize(int newMaxSize)
    {
        MaxSize = newMaxSize;
        EvictTo(MaxSize);
    }

    /// <summary>
    /// Resolve a 1-based dynamic index (1 == newest). Returns false if out of
    /// range.
    /// </summary>
    public bool TryGet(int dynamicIndex, out string name, out string value)
    {
        if (dynamicIndex < 1 || dynamicIndex > _entries.Count)
        {
            name = string.Empty;
            value = string.Empty;
            return false;
        }
        var e = _entries[dynamicIndex - 1];
        name = e.Name;
        value = e.Value;
        return true;
    }

    private void EvictTo(int target)
    {
        while (Size > target && _entries.Count > 0)
        {
            var last = _entries[^1];
            _entries.RemoveAt(_entries.Count - 1);
            Size -= last.Size;
        }
    }

    private readonly record struct Entry(string Name, string Value, int Size);
}
