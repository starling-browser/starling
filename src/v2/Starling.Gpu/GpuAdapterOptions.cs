// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>Options for requesting an adapter from an instance.</summary>
public readonly struct GpuAdapterOptions
{
    public GpuAdapterOptions(GpuPowerPreference power) => Power = power;

    public GpuPowerPreference Power { get; }
}
