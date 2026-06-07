// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>
/// Device limits the renderer must respect. The producer clamps surface and
/// texture sizes to these before it asks the device to allocate, so a bad size
/// never reaches the native backend.
/// </summary>
public readonly struct GpuLimits
{
    public GpuLimits(int maxTextureDimension2D)
    {
        MaxTextureDimension2D = maxTextureDimension2D;
    }

    public int MaxTextureDimension2D { get; }
}
