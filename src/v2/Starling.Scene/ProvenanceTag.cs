// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// Records who produced a surface layer or region and what it is allowed to do.
/// Generated UI uses this for inspection, undo, and permission checks. A document
/// layer rendered from a real web page can leave this null.
/// </summary>
public readonly struct ProvenanceTag
{
    public ProvenanceTag(string source, string? actionId, PermissionScope scope)
    {
        Source = source;
        ActionId = actionId;
        Scope = scope;
    }

    /// <summary>The producer, for example an agent or tool name.</summary>
    public string Source { get; }

    /// <summary>The typed action this region maps to, if any.</summary>
    public string? ActionId { get; }

    public PermissionScope Scope { get; }
}
