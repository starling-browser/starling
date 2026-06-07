// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// A stable identity for a surface layer across frames. The compositor pairs this
/// with <see cref="SurfaceLayer.ContentHash"/> to reuse resident textures and to
/// compute damage, so an unchanged layer is not re-rendered.
/// </summary>
public readonly struct LayerId : IEquatable<LayerId>
{
    public LayerId(int value) => Value = value;
    public int Value { get; }
    public bool Equals(LayerId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is LayerId other && Equals(other);
    public override int GetHashCode() => Value;
    public static bool operator ==(LayerId left, LayerId right) => left.Equals(right);
    public static bool operator !=(LayerId left, LayerId right) => !left.Equals(right);
}
