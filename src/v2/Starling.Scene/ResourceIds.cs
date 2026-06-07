// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// Strongly typed handles into a <see cref="RenderResourceTable"/>. Each kind has
/// its own id type so a command can never reference an image where it meant a path.
/// The renderer and compositor cache GPU resources by these ids.
/// </summary>
public readonly struct PathId : IEquatable<PathId>
{
    public PathId(int value) => Value = value;
    public int Value { get; }
    public bool IsValid => Value >= 0;
    public static PathId Invalid => new(-1);
    public bool Equals(PathId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is PathId other && Equals(other);
    public override int GetHashCode() => Value;
    public static bool operator ==(PathId left, PathId right) => left.Equals(right);
    public static bool operator !=(PathId left, PathId right) => !left.Equals(right);
}

/// <summary>A handle to a brush (solid, linear gradient, or image) in the resource table.</summary>
public readonly struct BrushId : IEquatable<BrushId>
{
    public BrushId(int value) => Value = value;
    public int Value { get; }
    public bool IsValid => Value >= 0;
    public static BrushId Invalid => new(-1);
    public bool Equals(BrushId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is BrushId other && Equals(other);
    public override int GetHashCode() => Value;
    public static bool operator ==(BrushId left, BrushId right) => left.Equals(right);
    public static bool operator !=(BrushId left, BrushId right) => !left.Equals(right);
}

/// <summary>A handle to a decoded image in the resource table.</summary>
public readonly struct ImageId : IEquatable<ImageId>
{
    public ImageId(int value) => Value = value;
    public int Value { get; }
    public bool IsValid => Value >= 0;
    public static ImageId Invalid => new(-1);
    public bool Equals(ImageId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is ImageId other && Equals(other);
    public override int GetHashCode() => Value;
    public static bool operator ==(ImageId left, ImageId right) => left.Equals(right);
    public static bool operator !=(ImageId left, ImageId right) => !left.Equals(right);
}

/// <summary>A handle to a resolved font face in the resource table.</summary>
public readonly struct FontId : IEquatable<FontId>
{
    public FontId(int value) => Value = value;
    public int Value { get; }
    public bool IsValid => Value >= 0;
    public static FontId Invalid => new(-1);
    public bool Equals(FontId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is FontId other && Equals(other);
    public override int GetHashCode() => Value;
    public static bool operator ==(FontId left, FontId right) => left.Equals(right);
    public static bool operator !=(FontId left, FontId right) => !left.Equals(right);
}

/// <summary>A handle to a shaped glyph run in the resource table.</summary>
public readonly struct GlyphRunId : IEquatable<GlyphRunId>
{
    public GlyphRunId(int value) => Value = value;
    public int Value { get; }
    public bool IsValid => Value >= 0;
    public static GlyphRunId Invalid => new(-1);
    public bool Equals(GlyphRunId other) => Value == other.Value;
    public override bool Equals(object? obj) => obj is GlyphRunId other && Equals(other);
    public override int GetHashCode() => Value;
    public static bool operator ==(GlyphRunId left, GlyphRunId right) => left.Equals(right);
    public static bool operator !=(GlyphRunId left, GlyphRunId right) => !left.Equals(right);
}
