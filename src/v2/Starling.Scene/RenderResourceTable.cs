// SPDX-License-Identifier: Apache-2.0
namespace Starling.Scene;

/// <summary>
/// The resource side table for one <see cref="RenderScene"/>. Path-first commands
/// reference paths, brushes, images, glyph runs, and fonts by typed id instead of
/// inlining their data, so the renderer and compositor can cache GPU resources
/// across frames. Images dedup by content hash and fonts by face key, so the same
/// content added twice returns the same id and uploads once.
/// </summary>
public sealed class RenderResourceTable
{
    private readonly List<Path> _paths = [];
    private readonly List<Brush> _brushes = [];
    private readonly List<ImageResource> _images = [];
    private readonly Dictionary<long, ImageId> _imageByHash = [];
    private readonly List<GlyphRun> _glyphRuns = [];
    private readonly List<FontResource> _fonts = [];
    private readonly Dictionary<long, FontId> _fontByKey = [];

    public int PathCount => _paths.Count;
    public int BrushCount => _brushes.Count;
    public int ImageCount => _images.Count;
    public int GlyphRunCount => _glyphRuns.Count;
    public int FontCount => _fonts.Count;

    public PathId AddPath(Path path)
    {
        var id = new PathId(_paths.Count);
        _paths.Add(path);
        return id;
    }

    public Path GetPath(PathId id) => _paths[id.Value];

    public BrushId AddBrush(Brush brush)
    {
        var id = new BrushId(_brushes.Count);
        _brushes.Add(brush);
        return id;
    }

    public Brush GetBrush(BrushId id) => _brushes[id.Value];

    /// <summary>Interns an image. Identical content (same hash) returns the existing id.</summary>
    public ImageId AddImage(ImageResource image)
    {
        if (_imageByHash.TryGetValue(image.ContentHash, out var existing))
        {
            return existing;
        }

        var id = new ImageId(_images.Count);
        _images.Add(image);
        _imageByHash[image.ContentHash] = id;
        return id;
    }

    public ImageResource GetImage(ImageId id) => _images[id.Value];

    /// <summary>Adds a glyph run. Glyph runs are not deduped: each draw is its own run.</summary>
    public GlyphRunId AddGlyphRun(GlyphRun run)
    {
        var id = new GlyphRunId(_glyphRuns.Count);
        _glyphRuns.Add(run);
        return id;
    }

    public GlyphRun GetGlyphRun(GlyphRunId id) => _glyphRuns[id.Value];

    /// <summary>Interns a font face. Identical faces (same key) return the existing id.</summary>
    public FontId AddFont(FontResource font)
    {
        if (_fontByKey.TryGetValue(font.Key, out var existing))
        {
            return existing;
        }

        var id = new FontId(_fonts.Count);
        _fonts.Add(font);
        _fontByKey[font.Key] = id;
        return id;
    }

    public FontResource GetFont(FontId id) => _fonts[id.Value];
}
