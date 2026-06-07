// SPDX-License-Identifier: Apache-2.0
using System.Numerics;

namespace Starling.Scene;

/// <summary>
/// The hit regions for one surface layer. Input routing reads this, not the paint
/// commands, so a layer can be hit-tested without rasterizing. Regions are added
/// in paint order; <see cref="HitTest"/> returns the topmost (last added) hit.
/// </summary>
public sealed class HitRegionSet
{
    private readonly List<HitRegion> _regions = [];

    public IReadOnlyList<HitRegion> Regions => _regions;

    public void Add(PxRect bounds, int hitId) => _regions.Add(new HitRegion(bounds, hitId));

    /// <summary>Returns the hit id of the topmost region containing the point, or null on a miss.</summary>
    public int? HitTest(Vector2 point)
    {
        for (int i = _regions.Count - 1; i >= 0; i--)
        {
            if (_regions[i].Bounds.Contains(point))
            {
                return _regions[i].HitId;
            }
        }

        return null;
    }
}
