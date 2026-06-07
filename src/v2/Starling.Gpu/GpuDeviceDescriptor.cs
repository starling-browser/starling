// SPDX-License-Identifier: Apache-2.0
namespace Starling.Gpu;

/// <summary>Describes a device to request from an adapter.</summary>
public readonly struct GpuDeviceDescriptor
{
    public GpuDeviceDescriptor(string? label = null) => Label = label;

    /// <summary>Optional debug label, surfaced to GPU debug tooling.</summary>
    public string? Label { get; }
}
