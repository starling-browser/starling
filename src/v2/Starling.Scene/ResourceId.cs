// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// A stable handle into a <see cref="RenderResourceTable"/>. The id is scoped by
/// resource kind: the render command that carries it knows whether it indexes an
/// image, a glyph run, or a font. This keeps the renderer and compositor free to
/// cache GPU resources by id without re-uploading identical content.
/// </summary>
public readonly struct ResourceId : IEquatable<ResourceId>
{
    public ResourceId(int value) => Value = value;

    public int Value { get; }

    public bool IsValid => Value >= 0;

    public static ResourceId Invalid => new(-1);

    public bool Equals(ResourceId other) => Value == other.Value;

    public override bool Equals(object? obj) => obj is ResourceId other && Equals(other);

    public override int GetHashCode() => Value;

    public static bool operator ==(ResourceId left, ResourceId right) => left.Equals(right);

    public static bool operator !=(ResourceId left, ResourceId right) => !left.Equals(right);
}
