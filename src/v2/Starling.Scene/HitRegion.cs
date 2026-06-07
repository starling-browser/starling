// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>A rectangle that routes input to a hit id. Hit testing is decoupled from paint.</summary>
public readonly struct HitRegion
{
    public HitRegion(PxRect bounds, int hitId)
    {
        Bounds = bounds;
        HitId = hitId;
    }

    public PxRect Bounds { get; }
    public int HitId { get; }
}
