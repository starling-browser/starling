namespace Starling.Gui.Controls;

/// <summary>
/// A set whose members expire a fixed number of frames after they were last
/// noted — the compositor-promotion hysteresis window. Mirrors the semantics
/// of <see cref="Starling.Dom.Document.DecayRecentMutations"/>: noting a
/// member resets its countdown, <see cref="Decay"/> ages every member by one
/// rendered frame, and a member whose countdown reaches zero drops out.
/// Used to keep an element promoted to its own compositor layer for a few
/// frames after its last animation/transition sample, so a transition end
/// does not immediately re-key the base layer's slice (issue #82).
/// </summary>
internal sealed class FrameCountdownSet<T> where T : notnull
{
    private readonly Dictionary<T, int> _entries = new();
    private readonly int _frames;
    private List<T>? _scratch;

    public FrameCountdownSet(int frames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(frames, 1);
        _frames = frames;
    }

    public int Count => _entries.Count;

    public bool Contains(T item) => _entries.Count > 0 && _entries.ContainsKey(item);

    /// <summary>Adds <paramref name="item"/>, or resets its countdown if it is
    /// already a member.</summary>
    public void Note(T item) => _entries[item] = _frames;

    /// <summary>Ages every member by one rendered frame, dropping members
    /// whose window has elapsed.</summary>
    public void Decay()
    {
        if (_entries.Count == 0)
        {
            return;
        }

        var keys = _scratch ??= new List<T>();
        keys.Clear();
        keys.AddRange(_entries.Keys);
        foreach (var item in keys)
        {
            var ttl = _entries[item] - 1;
            if (ttl <= 0)
            {
                _entries.Remove(item);
            }
            else
            {
                _entries[item] = ttl;
            }
        }
    }

    /// <summary>Adds every current member to <paramref name="into"/>.</summary>
    public void CopyTo(ISet<T> into)
    {
        foreach (var item in _entries.Keys)
        {
            into.Add(item);
        }
    }

    public void Clear() => _entries.Clear();
}
