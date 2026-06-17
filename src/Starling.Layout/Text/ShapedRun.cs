using System.Runtime.InteropServices;

namespace Starling.Layout.Text;

/// <summary>
/// One shaped glyph in a run: a glyph id and its pen position relative to the
/// run origin (0,0).
/// <para>
/// <b>Pen positions are authoritative</b> and renderer-neutral — layout slices
/// and hit-tests on them. <see cref="GlyphId"/> is best-effort: it is the
/// shaping engine's glyph index, which is <i>not portable</i> across shaping
/// engines (the ImageSharp measurer cannot read OpenType ids from SixLabors'
/// public surface and carries the codepoint as a stand-in; a skrifa/HarfBuzz
/// backend emits its own ids). A non-ImageSharp paint backend therefore re-shapes
/// the run's source text with its own engine rather than trusting these ids —
/// the neutral text contract a backend relies on is the source text plus the
/// <c>FontSpec</c> carried on the draw item, with these positions for layout.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ShapedGlyph(uint GlyphId, float X, float Y);

/// <summary>
/// A pre-shaped text run. Produced by <see cref="ITextMeasurer.Shape"/> at
/// layout time and carried unchanged through the display list to the paint
/// backend. Layout consumes only glyph positions and total advance; concrete
/// implementations may carry renderer-specific data as well.
/// </summary>
public abstract class ShapedRun
{
    /// <summary>
    /// Initializes a shaped run from origin-relative glyph positions and an
    /// advance width.
    /// </summary>
    protected ShapedRun(ShapedGlyph[] glyphs, double advance)
    {
        Glyphs = glyphs;
        Advance = advance;
    }

    /// <summary>
    /// The shaped glyphs, with pen positions relative to (0, 0) along the
    /// run's baseline. Layout uses these to slice simple runs without
    /// re-entering the shaper.
    /// </summary>
    public ShapedGlyph[] Glyphs { get; }

    /// <summary>
    /// The post-run pen advance — the total width of the shaped text.
    /// </summary>
    public double Advance { get; }

    /// <summary>
    /// Whether the paint backend can reuse this run's pre-shaped data to draw at
    /// <paramref name="fontSize"/> without re-shaping. The backend asks the run
    /// instead of pattern-matching its concrete type, so a non-ImageSharp run
    /// (e.g. <see cref="GlyphShapedRun"/>) is handled without a down-cast. Default
    /// false — a run that carries no renderer-ready payload forces the slow path.
    /// </summary>
    public virtual bool CanReuseAtSize(double fontSize) => false;

    /// <summary>
    /// Carve a sub-run out of an already-shaped run. Used by inline layout to
    /// shape a whole text run once and then split the result per-word for the
    /// existing per-fragment data structures (hit-testing, alignment, line
    /// wrapping). Pen positions in the returned run start at 0, so the slice
    /// can drop into a <see cref="Box.TextFragment"/> exactly as if it had
    /// been shaped on its own.
    /// <para>
    /// Assumes a 1:1 glyph-to-character mapping. The caller is responsible for
    /// detecting non-1:1 cases (combining marks, surrogate pairs, ligatures,
    /// complex script reshaping) and falling back to a per-slice shape call —
    /// the simplest check is <c>Glyphs.Length == text.Length</c> on the source.
    /// </para>
    /// </summary>
    /// <param name="startGlyph">Inclusive start glyph index into <see cref="Glyphs"/>.</param>
    /// <param name="endGlyph">Exclusive end glyph index. <c>endGlyph == Glyphs.Length</c>
    /// means "to the end of the run"; the slice's advance then uses this run's
    /// <see cref="Advance"/> as the trailing pen-X.</param>
    public abstract ShapedRun Slice(int startGlyph, int endGlyph);

    /// <summary>
    /// Slices the backend-neutral glyph data shared by all shaped-run
    /// implementations.
    /// </summary>
    protected static (ShapedGlyph[] Glyphs, double Advance) SliceGlyphs(
        ShapedGlyph[] glyphs,
        double advance,
        int startGlyph,
        int endGlyph)
    {
        if (startGlyph < 0 || endGlyph > glyphs.Length || startGlyph > endGlyph)
        {
            throw new ArgumentOutOfRangeException(nameof(startGlyph));
        }

        if (startGlyph == endGlyph)
        {
            return (Array.Empty<ShapedGlyph>(), 0d);
        }

        var startX = glyphs[startGlyph].X;
        // The pen-X just past the slice's last glyph: either the next glyph's
        // origin (intermediate slice) or the whole run's total advance (slice
        // touches the run's tail). Both are computed without a re-shape.
        var endX = endGlyph < glyphs.Length ? glyphs[endGlyph].X : (float)advance;

        var sliced = new ShapedGlyph[endGlyph - startGlyph];
        for (var i = 0; i < sliced.Length; i++)
        {
            var g = glyphs[startGlyph + i];
            sliced[i] = new ShapedGlyph(g.GlyphId, g.X - startX, g.Y);
        }

        return (sliced, endX - startX);
    }
}

/// <summary>
/// Backend-neutral shaped run containing only glyph positions and advance.
/// </summary>
public sealed class GlyphShapedRun : ShapedRun
{
    /// <summary>
    /// Initializes a backend-neutral shaped run.
    /// </summary>
    public GlyphShapedRun(ShapedGlyph[] glyphs, double advance)
        : base(glyphs, advance)
    {
    }

    /// <summary>
    /// Carves a backend-neutral sub-run from a 1:1 glyph/character run.
    /// </summary>
    public override ShapedRun Slice(int startGlyph, int endGlyph)
    {
        var (glyphs, advance) = SliceGlyphs(Glyphs, Advance, startGlyph, endGlyph);
        return new GlyphShapedRun(glyphs, advance);
    }
}
