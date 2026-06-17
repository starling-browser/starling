using System.Collections.Concurrent;
using SixLabors.Fonts;
using Starling.Layout.Text;

namespace Starling.Paint;

/// <summary>
/// <see cref="ITextMeasurer"/> backed by SixLabors.Fonts 3.x: real shaped glyph
/// advances and sized font metrics via <see cref="TextMeasurer"/>. Used by the
/// ImageSharp paint backend, which is now the only backend after the Skia shim
/// was removed.
/// <para>
/// Typefaces are loaded from the same embedded TTF/OTF resources
/// <see cref="FontResolver"/> uses (bundled OpenSans plus anything else
/// stamped into <c>Resources/Fonts/</c>). SixLabors.Fonts owns no native
/// resources, so disposal is bookkeeping only — but we still flag and guard
/// re-entry so misuse trips loudly instead of returning stale measurements.
/// </para>
/// </summary>
public sealed class ImageSharpTextMeasurer : ITextMeasurer, IDisposable
{
    private readonly FontResolver _fonts;
    private readonly FontFaceRegistry? _webFonts;
    private readonly FontCollection _collection;
    private readonly ConcurrentDictionary<FontCacheKey, Font> _fontCache = new();
    // Per-measurer shape cache, mirroring the cache the original
    // SkiaTextMeasurer carried (commit 499ce3d). SixLabors.Fonts applies
    // kerning and contextual shaping within the text passed to one Shape call;
    // separate Starling layout runs are shaped independently, so no adjustment
    // can cross those boundaries. With the font collection and text options
    // fixed for this measurer, the same (text, size, spec) produces the same
    // origin-relative glyph advances. Layout and paint treat the cached run as
    // read-only, so the same instance can back many TextFragments.
    // Article-class pages repeat common tokens ("the", " ", ",", digits)
    // hundreds of times; without the cache, those repeated runs and fallback
    // per-word shapes would re-enter SixLabors.Fonts each time.
    private readonly ConcurrentDictionary<ShapeCacheKey, ShapedRun> _shapeCache = new();
    private bool _disposed;

    private readonly record struct ShapeCacheKey(string Text, float Size, FontSpec Spec);

    public ImageSharpTextMeasurer(FontResolver? fonts = null, FontFaceRegistry? webFonts = null)
    {
        _fonts = fonts ?? FontResolver.Default;
        _webFonts = webFonts;
        _collection = ImageSharpFontLookup.LoadCollection(webFonts);
    }

    public double MeasureWidth(string text, double fontSize, FontSpec spec)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || fontSize <= 0)
        {
            return 0d;
        }

        return Shape(text, fontSize, spec).Advance;
    }

    /// <summary>
    /// Shape <paramref name="text"/> once and cache the result. Returns the
    /// per-glyph pen positions plus the total advance.
    /// <para>
    /// SixLabors.Fonts 3 does not expose OpenType glyph indices on its public
    /// <see cref="GlyphMetrics"/> surface (only the codepoint), so
    /// <see cref="ShapedGlyph.GlyphId"/> carries the codepoint as a stand-in.
    /// The ImageSharp paint backend re-renders by string and never reads the
    /// glyph id, so this substitution is safe; the positions are what matter,
    /// because <c>InlineLayout</c> uses them to slice a whole-run shape
    /// into per-word <c>TextFragment</c>s without re-shaping each word.
    /// </para>
    /// <para>
    /// When the metrics count matches <c>text.Length</c> (the ASCII / 1:1
    /// case) the slice optimisation in <c>InlineLayout</c> re-engages.
    /// For ligatures, surrogate pairs, or combining marks the counts differ
    /// and InlineLayout falls back to per-word shaping — still cached here
    /// by (word, size, spec), so the regression cost is bounded.
    /// </para>
    /// </summary>
    public ShapedRun Shape(string text, double fontSize, FontSpec spec)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length == 0 || fontSize <= 0)
        {
            return new GlyphShapedRun(Array.Empty<ShapedGlyph>(), 0d);
        }

        var size = (float)fontSize;
        var key = new ShapeCacheKey(text, size, spec);
        if (_shapeCache.TryGetValue(key, out var hit))
        {
            return hit;
        }

        var font = GetFont(size, spec);
        var options = new TextOptions(font);
        var textBlock = new TextBlock(text, options);
        var measured = textBlock.Measure(-1);
        var metrics = measured.GetGlyphMetrics().Span;

        var glyphs = new ShapedGlyph[metrics.Length];
        var pen = 0f;
        for (var i = 0; i < metrics.Length; i++)
        {
            var gm = metrics[i];
            // ImageSharp.Drawing's public API does not expose the OpenType
            // glyph index (GlyphMetrics.GlyphId is internal), so the codepoint
            // is carried as a stand-in. See ShapedGlyph for why this is fine:
            // glyph ids are not portable across shaping engines anyway.
            glyphs[i] = new ShapedGlyph((uint)gm.CodePoint.Value, pen, 0f);
            pen += gm.Advance.Width;
        }

        var shaped = new ImageSharpShapedRun(text, font, textBlock, glyphs, measured.Advance.Width);
        _shapeCache[key] = shaped;
        return shaped;
    }

    public double NormalLineHeight(double fontSize, FontSpec spec)
    {
        if (fontSize <= 0)
        {
            return 0;
        }

        var font = GetFont((float)fontSize, spec);
        var (ascent, descent, leading) = ScaledMetrics(font);
        return ascent + descent + leading;
    }

    public double Baseline(double fontSize, FontSpec spec)
    {
        if (fontSize <= 0)
        {
            return 0;
        }

        var font = GetFont((float)fontSize, spec);
        var (ascent, _, leading) = ScaledMetrics(font);
        return ascent + (leading / 2.0);
    }

    // SixLabors.Fonts exposes ascender/descender/line-gap in font design units
    // on HorizontalMetrics; scale by (fontSize / UnitsPerEm) to get CSS px.
    // Descender is signed (negative below baseline in OpenType) — abs it so
    // it composes additively with ascender.
    private static (double Ascent, double Descent, double Leading) ScaledMetrics(Font font)
    {
        var fm = font.FontMetrics;
        var h = fm.HorizontalMetrics;
        var scale = font.Size / fm.UnitsPerEm;
        var ascent = h.Ascender * scale;
        var descent = Math.Abs(h.Descender) * scale;
        var leading = h.LineGap * scale;
        return (ascent, descent, leading);
    }

    private Font GetFont(float size, FontSpec spec)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var key = new FontCacheKey(spec, size);
        return _fontCache.GetOrAdd(key, static (k, self) => self.CreateFont(k.Spec, k.Size), this);
    }

    private Font CreateFont(FontSpec spec, float size)
        => ImageSharpFontLookup.CreateFont(_collection, spec, size);

    private readonly record struct FontCacheKey(FontSpec Spec, float Size);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _fontCache.Clear();
        _shapeCache.Clear();
    }
}

/// <summary>
/// ImageSharp-backed shaped run that carries the prepared SixLabors text block
/// through layout into paint.
/// </summary>
internal sealed class ImageSharpShapedRun : ShapedRun
{
    /// <summary>
    /// Initializes an ImageSharp-backed shaped run.
    /// </summary>
    public ImageSharpShapedRun(string text, Font font, TextBlock textBlock, ShapedGlyph[] glyphs, double advance)
        : base(glyphs, advance)
    {
        Text = text;
        Font = font;
        TextBlock = textBlock;
    }

    /// <summary>
    /// The text represented by this shaped run.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// The concrete ImageSharp font used to prepare <see cref="TextBlock"/>.
    /// </summary>
    public Font Font { get; }

    /// <summary>
    /// The prepared SixLabors text block consumed by the ImageSharp renderer.
    /// </summary>
    public TextBlock TextBlock { get; }

    /// <summary>The cached TextBlock can be reused when the draw size matches the
    /// font this run was shaped with.</summary>
    public override bool CanReuseAtSize(double fontSize) => Font.Size == fontSize;

    /// <summary>
    /// Carves a backend-preserving sub-run. Glyph slicing is exact;
    /// <see cref="Text"/> slicing assumes a 1:1 glyph/character run (the caller's
    /// contract). The character-range substring is clamped to the string bounds
    /// so a non-1:1 run can never read out of range — best-effort text, never a crash.
    /// </summary>
    public override ShapedRun Slice(int startGlyph, int endGlyph)
    {
        var (glyphs, advance) = SliceGlyphs(Glyphs, Advance, startGlyph, endGlyph);
        var start = Math.Clamp(startGlyph, 0, Text.Length);
        var length = Math.Clamp(endGlyph - startGlyph, 0, Text.Length - start);
        var sliceText = Text.Substring(start, length);
        var textBlock = new TextBlock(sliceText, new TextOptions(Font));

        return new ImageSharpShapedRun(sliceText, Font, textBlock, glyphs, advance);
    }
}
