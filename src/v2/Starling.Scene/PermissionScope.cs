// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// What a provenance-tagged region is allowed to do. Generated UI surfaces carry
/// this so the runtime can gate actions before they run, instead of trusting
/// arbitrary callbacks. This is the seam that makes the surface graph
/// permission-aware rather than document-land.
/// </summary>
public enum PermissionScope
{
    None,
    ReadOnly,
    Interactive,
    Privileged,
}
