// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// A resolved font face a glyph run draws with. Phase 1 carries only identity;
/// real shaping and the glyph atlas land in Starling.Text (Phase 2). The
/// <see cref="Key"/> dedups faces so the atlas is built once per face.
/// </summary>
public sealed class FontResource
{
    public FontResource(string family, int weight, bool italic, long key)
    {
        Family = family;
        Weight = weight;
        Italic = italic;
        Key = key;
    }

    public string Family { get; }
    public int Weight { get; }
    public bool Italic { get; }

    /// <summary>Stable identity for the face, for example a hash of its bytes.</summary>
    public long Key { get; }
}
