// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// A node in a surface layer's accessibility tree. The scene model owns this so a
/// surface is inspectable and reachable by a screen reader without rendering it.
/// A node can link to a hit region by id, so the same geometry drives both pointer
/// and assistive-technology interaction.
/// </summary>
public sealed class AccessibilityNode
{
    private readonly List<AccessibilityNode> _children = [];

    public AccessibilityNode(AccessibilityRole role, string? name = null, string? value = null, PxRect bounds = default)
    {
        Role = role;
        Name = name;
        Value = value;
        Bounds = bounds;
    }

    public AccessibilityRole Role { get; }
    public string? Name { get; set; }
    public string? Value { get; set; }
    public PxRect Bounds { get; set; }

    /// <summary>The hit region this node maps to, if any. Matches a <see cref="HitRegion.HitId"/>.</summary>
    public int? HitId { get; set; }

    public IReadOnlyList<AccessibilityNode> Children => _children;

    /// <summary>Appends a child node and returns it.</summary>
    public AccessibilityNode AddChild(AccessibilityNode child)
    {
        _children.Add(child);
        return child;
    }
}
