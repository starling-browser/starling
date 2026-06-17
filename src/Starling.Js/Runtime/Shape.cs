namespace Starling.Js.Runtime;

/// <summary>
/// A hidden class ("shape") for the fast-path storage of an ordinary object's
/// <em>string-keyed data properties</em>. Shapes form a transition tree rooted
/// at <see cref="Root"/>: adding a property transitions to a child shape that is
/// cached on the parent, so two objects that gain the same properties (same
/// keys, same order, same attributes) end up sharing the <em>same</em> Shape
/// instance. That shared identity is what makes an inline cache's
/// <c>ReferenceEquals(obj.Shape, cachedShape)</c> check meaningful across many
/// objects.
/// </summary>
/// <remarks>
/// <para>Scope is deliberately narrow. A Shape describes only string-keyed DATA
/// properties. Accessors, deletion, attribute redefinition, sealing/freezing,
/// and non-extensible adds all push the object into "dictionary mode"
/// (<c>JsObject</c> with a null shape), where the legacy dictionary storage runs
/// unchanged. Symbol-keyed properties never enter a shape. This keeps the fast
/// path small and the intricate spec validators untouched.</para>
/// <para>Each data property occupies exactly one slot in the object's slot
/// array; the slot index equals the property's position in creation order
/// (<see cref="AddedSlot"/>). Slot indices are stable across the transition
/// tree — a child never moves a parent's slots — so a cached (shape, slot) pair
/// stays valid for the life of that shape.</para>
/// <para><b>Thread-safety.</b> Shapes can be shared across host callbacks and
/// realms. The lazily-built transition cache and flattened lookup table are
/// guarded by <see cref="_gate"/>; the immutable per-shape data (parent, key,
/// flags, slot count) needs no lock. Inline-cache hot reads never touch a
/// Shape's mutable state — they only compare references and index the object's
/// slot array.</para>
/// </remarks>
internal sealed class Shape
{
    // Attribute bits — mirror PropertyDescriptor's layout for data properties.
    // (Accessor (bit 3) never appears in a shape; accessors force dictionary mode.)
    internal const byte Writable = 1 << 0;
    internal const byte Enumerable = 1 << 1;
    internal const byte Configurable = 1 << 2;
    internal const byte DefaultData = Writable | Enumerable | Configurable;

    /// <summary>The process-wide empty root shape. Every fresh ordinary object
    /// starts here.</summary>
    public static readonly Shape Root = new();

    private readonly Shape? _parent;
    private readonly string? _key;   // key added relative to _parent; null only for the root
    private readonly byte _flags;    // attributes of the added data property

    /// <summary>Number of slots an object of this shape uses — equivalently the
    /// number of own string data properties, equivalently the depth in the
    /// transition tree.</summary>
    public int SlotCount { get; }

    private readonly object _gate = new();
    private Dictionary<TransitionKey, Shape>? _transitions;   // lazy child cache
    private Dictionary<string, ShapeProp>? _table;            // lazy flattened lookup

    private Shape() { SlotCount = 0; }

    private Shape(Shape parent, string key, byte flags)
    {
        _parent = parent;
        _key = key;
        _flags = flags;
        SlotCount = parent.SlotCount + 1;
    }

    /// <summary>The slot the property added by this shape occupies. Equal to the
    /// parent's slot count, so slots fill densely 0,1,2,… in creation order.</summary>
    public int AddedSlot => SlotCount - 1;

    /// <summary>Return the child shape reached by adding string data property
    /// <paramref name="key"/> with the given attribute <paramref name="flags"/>,
    /// creating and caching it on first use. Shared, so the same add from the
    /// same parent always yields the same child.</summary>
    public Shape Transition(string key, byte flags)
    {
        var tk = new TransitionKey(key, flags);
        lock (_gate)
        {
            _transitions ??= new Dictionary<TransitionKey, Shape>();
            if (_transitions.TryGetValue(tk, out var existing))
            {
                return existing;
            }

            var child = new Shape(this, key, flags);
            _transitions[tk] = child;
            return child;
        }
    }

    // ── Flattening heuristics ────────────────────────────────────────────
    // The flattened table is only a lookup accelerator; the transition chain
    // itself is the source of truth. Flattening eagerly on first lookup was
    // the engine's single largest retained-memory cost (x.com at peak: 251 MB
    // of Dictionary<string,ShapeProp> entries — 39% of the managed heap —
    // across ~36k shapes), because every looked-up shape copied its parent's
    // whole table: a chain of N properties materialized N tables of sizes
    // 1..N. A shape now flattens only when it is BOTH deep and warm:
    //  - SlotCount <= FlattenSlotThreshold: never flatten. Walking <= 8
    //    parent links (one reference load + ordinal compare each) costs about
    //    the same as a single dictionary probe, and small shapes are the
    //    overwhelming majority.
    //  - Deeper shapes flatten once their lookup count reaches their own
    //    depth, min(SlotCount, FlattenLookupCap). Scaling the threshold with
    //    depth makes the table cost-proportional: a table of N entries is only
    //    built after ~N lookups' worth of chain-walk work, so a shape that is
    //    grown and read a handful of times per property (the React/spread
    //    pattern — each intermediate shape of a growing object sees ~2-4
    //    lookups, regardless of depth) never flattens, while a shape that
    //    keeps fielding lookups pays the one-time flatten and from then on
    //    behaves exactly like the old always-flattened code. The cap bounds
    //    the pre-flatten walk work for very wide objects (a 10k-slot shape
    //    flattens after 256 lookups, not 10k).
    private const int FlattenSlotThreshold = 8;
    private const int FlattenLookupCap = 256;

    /// <summary>Approximate lookup counter for the heat heuristic. Plain
    /// (no Interlocked/volatile) on purpose: under a race a lost increment
    /// merely delays flattening and a duplicate one triggers it early —
    /// <see cref="Flatten"/> itself is gated, so the table is built once.</summary>
    private int _lookups;

    /// <summary>Look up a string key in this shape. Returns false if absent.</summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public bool TryGet(string key, out ShapeProp prop)
    {
        var t = _table;
        if (t is not null)
        {
            return t.TryGetValue(key, out prop);
        }

        return TryGetSlow(key, out prop);
    }

    public bool Contains(string key)
    {
        var t = _table;
        if (t is not null)
        {
            return t.ContainsKey(key);
        }

        return TryGetSlow(key, out _);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private bool TryGetSlow(string key, out ShapeProp prop)
    {
        if (SlotCount > FlattenSlotThreshold)
        {
            var n = ++_lookups;
            if (n >= SlotCount || n >= FlattenLookupCap)
            {
                return Flatten().TryGetValue(key, out prop);
            }
        }

        // Chain walk — allocation-free. Each link holds the one key it added
        // (keys are unique along a chain: a Transition is only taken for a key
        // the shape does not already contain). A materialized ancestor table
        // answers for the entire chain above it, so deep-but-cold children of
        // a hot flattened shape stop after a few links.
        var s = this;
        while (s._key is not null)
        {
            if (string.Equals(s._key, key, System.StringComparison.Ordinal))
            {
                prop = new ShapeProp(s.AddedSlot, s._flags);
                return true;
            }
            s = s._parent!;
            var t = s._table;
            if (t is not null)
            {
                return t.TryGetValue(key, out prop);
            }
        }
        prop = default;
        return false;
    }

    /// <summary>Materialize this shape's flattened table. Copies from the
    /// nearest ancestor that already has one — never forcing ancestors to
    /// flatten — and chain-walks only the links in between.</summary>
    private Dictionary<string, ShapeProp> Flatten()
    {
        lock (_gate)
        {
            if (_table is not null)
            {
                return _table;
            }

            var anchor = _parent;
            while (anchor is not null && anchor._table is null)
            {
                anchor = anchor._parent;
            }

            var t = new Dictionary<string, ShapeProp>(SlotCount, StringComparer.Ordinal);
            if (anchor?._table is { } src)
            {
                foreach (var kv in src)
                {
                    t.Add(kv.Key, kv.Value);
                }
            }

            for (var s = this; !ReferenceEquals(s, anchor) && s._key is not null; s = s._parent!)
            {
                t.Add(s._key, new ShapeProp(s.AddedSlot, s._flags));
            }

            _table = t;
            return t;
        }
    }

    /// <summary>The own properties in creation (slot) order — element <c>i</c>
    /// describes slot <c>i</c>. One chain walk; never materializes the
    /// flattened table. For bulk exports (dictionary-mode migration).</summary>
    public ShapeProp[] OrderedProps()
    {
        if (SlotCount == 0)
        {
            return System.Array.Empty<ShapeProp>();
        }

        var props = new ShapeProp[SlotCount];
        var s = this;
        while (s._key is not null)
        {
            props[s.AddedSlot] = new ShapeProp(s.AddedSlot, s._flags);
            s = s._parent!;
        }
        return props;
    }

    /// <summary>The own string keys in creation (slot) order — the
    /// transition chain from root to here, oldest first.</summary>
    public IReadOnlyList<string> OrderedKeys()
    {
        if (SlotCount == 0)
        {
            return System.Array.Empty<string>();
        }

        var keys = new string[SlotCount];
        var s = this;
        while (s._key is not null)
        {
            keys[s.AddedSlot] = s._key;
            s = s._parent!;
        }
        return keys;
    }

    private readonly struct TransitionKey : System.IEquatable<TransitionKey>
    {
        private readonly string _key;
        private readonly byte _flags;
        public TransitionKey(string key, byte flags) { _key = key; _flags = flags; }
        public bool Equals(TransitionKey other)
            => _flags == other._flags && string.Equals(_key, other._key, System.StringComparison.Ordinal);
        public override bool Equals(object? obj) => obj is TransitionKey o && Equals(o);
        public override int GetHashCode()
            => System.HashCode.Combine(System.StringComparer.Ordinal.GetHashCode(_key), _flags);
    }
}

/// <summary>A property's location and attributes within a <see cref="Shape"/>:
/// its slot index plus the data-attribute flags.</summary>
internal readonly struct ShapeProp
{
    public readonly int Slot;
    public readonly byte Flags;
    public ShapeProp(int slot, byte flags) { Slot = slot; Flags = flags; }
    public bool Writable => (Flags & Shape.Writable) != 0;
    public bool Enumerable => (Flags & Shape.Enumerable) != 0;
    public bool Configurable => (Flags & Shape.Configurable) != 0;
}

/// <summary>
/// Per-call-site monomorphic inline cache for a property read (and, later,
/// write). Records the <see cref="Shape"/> an object had the last time this
/// bytecode site ran and the slot the property occupied. A cached read is valid
/// when <c>ReferenceEquals(obj.Shape, Shape)</c> — any structural change to an
/// object replaces its shape, so a stale entry simply misses and refills. A
/// null <see cref="Shape"/> is an empty (never-filled) cache.
/// </summary>
/// <remarks>Stored as a value type in <c>Chunk.Caches</c> (no per-entry
/// allocation). Continuation frames resume on the same thread, so cache reads
/// and refills from suspendable bodies are ordinary interpreter work.</remarks>
internal struct InlineCache
{
    /// <summary>Shape the receiver must have for this cache to hit (the read
    /// shape, or the write shape BEFORE an add). Null = empty.</summary>
    public Shape? Shape;
    /// <summary>Slot the property occupies: in <see cref="Holder"/> for a
    /// prototype read, in <see cref="NextShape"/> for a write that adds, else in
    /// <see cref="Shape"/> (own read / write to an existing slot).</summary>
    public int Slot;
    /// <summary>For a write that ADDS a new property: the shape to transition the
    /// object to. Null for a read cache or a write to an already-present slot.</summary>
    public Shape? NextShape;
    /// <summary>For a prototype read: the prototype object that holds the data
    /// property at <see cref="Slot"/>. Null for an own-property cache.</summary>
    public JsObject? Holder;
    /// <summary>Snapshot of <see cref="JsObject.ProtoEpoch"/> when this entry was
    /// filled. Checked on a hit for prototype reads and add transitions: an
    /// unchanged epoch proves no prototype anywhere gained or lost the name.</summary>
    public int Epoch;
}
