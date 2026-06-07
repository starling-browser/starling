// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// A typed action a provenance-tagged region can invoke. It names a tool, not an
/// arbitrary callback, so the runtime can validate and gate it. Mirrors the action
/// shape in a SurfaceSpec: a label, the tool to call, and whether it needs
/// confirmation before it runs.
/// </summary>
public readonly struct ActionRef
{
    public ActionRef(string tool, bool requiresConfirmation, string? label = null)
    {
        Tool = tool;
        RequiresConfirmation = requiresConfirmation;
        Label = label;
    }

    /// <summary>The tool the action invokes, for example "calendar.createEvent".</summary>
    public string Tool { get; }

    /// <summary>True when the runtime must confirm with the user before running the action.</summary>
    public bool RequiresConfirmation { get; }

    /// <summary>An optional human-facing label.</summary>
    public string? Label { get; }
}
