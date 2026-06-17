namespace Starling.Layout.Compositor;

/// <summary>One box that was tagged as a layer candidate plus its hints.</summary>
public readonly record struct LayerCandidate(Box.Box Box, LayerHint Hints);

/// <summary>
/// Read-only diagnostics walker over a laid-out box tree. Used by future
/// compositor work and by tests to inspect which boxes carry
/// <see cref="LayerHint"/> bits.
/// </summary>
public static class LayerCandidates
{
    /// <summary>
    /// Depth-first pre-order enumeration of every box in the subtree rooted at
    /// <paramref name="root"/> whose <see cref="Box.Box.Hints"/> is not
    /// <see cref="LayerHint.None"/>.
    /// </summary>
    public static IEnumerable<LayerCandidate> EnumerateLayerCandidates(Box.Box root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Hints != LayerHint.None)
        {
            yield return new LayerCandidate(root, root.Hints);
        }

        foreach (var child in root.Children)
        {
            foreach (var candidate in EnumerateLayerCandidates(child))
            {
                yield return candidate;
            }
        }
    }
}
