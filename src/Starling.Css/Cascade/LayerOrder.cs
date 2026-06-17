namespace Starling.Css.Cascade;

// CSS Cascade 5 §6. A per-origin map of dotted layer paths in declaration order.
// `null`/empty path means "unlayered" — represented by index `UnlayeredIndex`.
public sealed class LayerOrder
{
    public const int UnlayeredIndex = -1;
    private readonly Dictionary<string, int> _paths = new(StringComparer.Ordinal);
    private int _nextIndex;

    public int RegisterLayer(string? dottedPath)
    {
        if (string.IsNullOrEmpty(dottedPath))
        {
            return UnlayeredIndex;
        }

        if (_paths.TryGetValue(dottedPath, out var existing))
        {
            return existing;
        }

        // Register each ancestor so `a.b` after `c` keeps `a` before `c` and `a.b` after `a`.
        var segments = dottedPath.Split('.');
        var path = string.Empty;
        var lastIndex = UnlayeredIndex;
        foreach (var seg in segments)
        {
            path = path.Length == 0 ? seg : path + "." + seg;
            if (!_paths.TryGetValue(path, out lastIndex))
            {
                lastIndex = _nextIndex++;
                _paths[path] = lastIndex;
            }
        }
        return lastIndex;
    }

    public int GetIndex(string? dottedPath)
    {
        if (string.IsNullOrEmpty(dottedPath))
        {
            return UnlayeredIndex;
        }

        return _paths.TryGetValue(dottedPath, out var idx) ? idx : UnlayeredIndex;
    }

    public IReadOnlyDictionary<string, int> AllLayers => _paths;

    // Comparator: returns negative if layer `a` is *weaker* (loses) than `b`,
    // positive if stronger, zero if equal. Unlayered is strongest among non-important;
    // for important the caller must invert.
    //
    // Layer ordering is a tree (CSS Cascade 5 §6.4.2): a layer's own (direct)
    // declarations are MORE important than any of its nested sub-layers. So when
    // one layer is an ancestor of the other, the ancestor's direct rules win —
    // this is NOT captured by the flat declaration-order index, which is only
    // correct for sibling / unrelated layers.
    public static int Compare(string? aPath, int aIndex, string? bPath, int bIndex)
    {
        if (aIndex == bIndex)
        {
            return 0;
        }
        // Unlayered (-1) wins over any layered rule.
        if (aIndex == UnlayeredIndex)
        {
            return 1;
        }

        if (bIndex == UnlayeredIndex)
        {
            return -1;
        }
        // Ancestor's direct rules beat its descendant sub-layers (§6.4.2).
        if (IsAncestor(aPath, bPath))
        {
            return 1;
        }

        if (IsAncestor(bPath, aPath))
        {
            return -1;
        }
        // Sibling / unrelated layers: later declared (higher index) wins.
        return aIndex.CompareTo(bIndex);
    }

    // True when `ancestor` is a proper layer-path ancestor of `descendant`
    // (e.g. "outer" is an ancestor of "outer.inner").
    private static bool IsAncestor(string? ancestor, string? descendant)
        => !string.IsNullOrEmpty(ancestor)
            && !string.IsNullOrEmpty(descendant)
            && descendant.Length > ancestor.Length
            && descendant.StartsWith(ancestor, StringComparison.Ordinal)
            && descendant[ancestor.Length] == '.';
}
