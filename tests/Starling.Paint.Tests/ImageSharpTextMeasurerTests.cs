using AwesomeAssertions;
using SixLabors.Fonts;
using Starling.Layout.Text;
namespace Starling.Paint.Tests;

/// <summary>
/// Locks down the width-measurement regression: when ImageSharp's measurer
/// only knew about bundled OpenSans, CSS like
/// <c>font-family: "Helvetica Neue", Helvetica, Arial, sans-serif</c>
/// measured at OpenSans advances. That fed back into layout's max-width
/// shrink-to-fit pass and changed line-break points, causing visible width
/// drift on pages like justinjackson.ca/words.html.
/// </summary>
[TestClass]
public sealed class ImageSharpTextMeasurerTests
{
    private const string Probe = "The quick brown fox jumps over the lazy dog";
    private const double FontSize = 16d;

    /// <summary>
    /// A run whose glyph count exceeds its character count (non-1:1) must slice
    /// without overrunning the character string. Before the clamp, slicing by
    /// glyph index ran <c>Text.Substring</c> past the string and threw.
    /// </summary>
    [TestMethod]
    public void Slice_clamps_text_substring_when_glyphs_exceed_chars()
    {
        var font = SystemFonts.Families.First().CreateFont(16f);
        var glyphs = new[]
        {
            new ShapedGlyph(1, 0, 0), new ShapedGlyph(2, 4, 0),
            new ShapedGlyph(3, 8, 0), new ShapedGlyph(4, 12, 0),
        };
        var run = new ImageSharpShapedRun("ab", font, new TextBlock("ab", new TextOptions(font)), glyphs, 16d);

        var act = () => run.Slice(0, 4);
        act.Should().NotThrow("glyph-index slicing must clamp to the character string for non-1:1 runs");
        run.Slice(0, 4).Glyphs.Length.Should().Be(4, "glyph slicing stays exact even when text slicing clamps");
    }

    /// <summary>
    /// Bold text must measure wider than Regular at the same font size for any
    /// system family that ships a real Bold face. If the measurer regressed
    /// to bundled-only OpenSans (no Bold face), Bold and Regular collapse to
    /// the same width.
    /// </summary>
    [TestMethod]
    public void Bold_advance_differs_from_regular_advance()
    {
        if (!(HostHasInstalledFontStyle(FontStyle.Bold))) { Assert.Inconclusive("no system Bold face on this host; can't differentiate Bold vs Regular"); return; }

        using var measurer = new ImageSharpTextMeasurer();
        var regular = measurer.MeasureWidth(Probe, FontSize,
            new FontSpec(["sans-serif"], bold: false, italic: false));
        var bold = measurer.MeasureWidth(Probe, FontSize,
            new FontSpec(["sans-serif"], bold: true, italic: false));

        regular.Should().BeGreaterThan(0);
        bold.Should().NotBeApproximately(regular, 0.5,
            "Bold should measure visibly different from Regular; identical advances indicate both resolved to the same face");
    }

    /// <summary>
    /// The system Helvetica/Arial/Segoe UI families have noticeably narrower
    /// glyphs than OpenSans, so resolving <c>sans-serif</c> against the
    /// system collection produces a different advance than the bundled
    /// OpenSans fallback. This is the regression's most direct fingerprint —
    /// if the measurer drops back to OpenSans-only the two measurements
    /// become identical.
    /// </summary>
    [TestMethod]
    public void System_sans_serif_measures_differently_than_bundled_open_sans()
    {
        if (!(HostHasInstalledSansSerif())) { Assert.Inconclusive("no system sans-serif on this host; nothing to compare against bundled OpenSans"); return; }

        using var measurer = new ImageSharpTextMeasurer();

        // "Open Sans" is the embedded family name; "sans-serif" is the CSS
        // generic that should expand to a system family on real hosts.
        var openSansSpec = new FontSpec(["Open Sans"], bold: false, italic: false);
        var sansSerifSpec = new FontSpec(["sans-serif"], bold: false, italic: false);

        var openSans = measurer.MeasureWidth(Probe, FontSize, openSansSpec);
        var sansSerif = measurer.MeasureWidth(Probe, FontSize, sansSerifSpec);

        sansSerif.Should().NotBeApproximately(openSans, 0.5,
            "CSS sans-serif must resolve to a system family on hosts that have one — the regression collapsed it back to bundled OpenSans");
    }

    [TestMethod]
    public void Empty_string_measures_zero()
    {
        using var measurer = new ImageSharpTextMeasurer();
        measurer.MeasureWidth("", FontSize, FontSpec.Default).Should().Be(0d);
    }

    [TestMethod]
    public void Line_height_is_positive_for_supported_specs()
    {
        using var measurer = new ImageSharpTextMeasurer();
        measurer.NormalLineHeight(FontSize, FontSpec.Default).Should().BeGreaterThan(0);
    }

    /// <summary>
    /// Regression: when ImageSharpTextMeasurer replaced SkiaTextMeasurer the
    /// per-instance shape cache from commit 499ce3d was dropped. Article-class
    /// pages with hundreds of repeats of common tokens then re-shaped each
    /// token via SixLabors.Fonts on every call. The cache should return the
    /// same <see cref="ShapedRun"/> instance for an identical (text, size,
    /// spec) call, proving the cache is engaged.
    /// </summary>
    [TestMethod]
    public void Shape_returns_same_instance_for_repeated_identical_calls()
    {
        using var measurer = new ImageSharpTextMeasurer();
        var first = measurer.Shape(Probe, FontSize, FontSpec.Default);
        var second = measurer.Shape(Probe, FontSize, FontSpec.Default);
        second.Should().BeSameAs(first,
            "the shape-result cache must hand the same ShapedRun back for an identical (text, size, spec) — otherwise repeated tokens on a page re-shape via SixLabors every call");
    }

    /// <summary>
    /// Regression: <c>ImageSharpTextMeasurer.Shape</c> originally returned
    /// <c>Glyphs = Array.Empty()</c> as a "let the paint backend re-shape"
    /// sentinel. That silently disabled the <c>canSlice</c> optimisation in
    /// <c>InlineLayout</c> (it requires <c>Glyphs.Length == text.Length</c>),
    /// forcing one shape per word instead of one shape per run. Verify the
    /// per-codepoint positions are populated and monotonic.
    /// </summary>
    [TestMethod]
    public void Shape_populates_per_codepoint_positions_for_ascii_text()
    {
        const string ascii = "hello world";
        using var measurer = new ImageSharpTextMeasurer();
        var shaped = measurer.Shape(ascii, FontSize, FontSpec.Default);

        shaped.Glyphs.Length.Should().Be(ascii.Length,
            "ASCII text shapes 1:1 to glyphs — the canSlice fast path in InlineLayout depends on this");
        shaped.Advance.Should().BeGreaterThan(0);
        shaped.Glyphs[0].X.Should().Be(0f, "the first glyph's pen position is at the run origin");
        for (var i = 1; i < shaped.Glyphs.Length; i++)
        {
            shaped.Glyphs[i].X.Should().BeGreaterThanOrEqualTo(shaped.Glyphs[i - 1].X,
                "pen positions accumulate monotonically along the baseline");
        }

        ((double)shaped.Glyphs[^1].X).Should().BeLessThanOrEqualTo(shaped.Advance,
            "the last glyph's pen X is before the run's total advance");
    }

    /// <summary>
    /// <c>MeasureWidth</c> must agree with <c>Shape().Advance</c> — both
    /// callers ought to share the same cache so common tokens hit cache
    /// regardless of which entry point InlineLayout uses. A mismatch would
    /// also indicate two parallel measurement codepaths drifting.
    /// </summary>
    [TestMethod]
    public void Measure_width_matches_shape_advance()
    {
        using var measurer = new ImageSharpTextMeasurer();
        var advance = measurer.MeasureWidth(Probe, FontSize, FontSpec.Default);
        var shaped = measurer.Shape(Probe, FontSize, FontSpec.Default);
        advance.Should().Be(shaped.Advance);
    }

    /// <summary>
    /// Cache key includes size and FontSpec — same text at a different size
    /// must shape independently, not collide with the smaller cache entry.
    /// </summary>
    [TestMethod]
    public void Shape_cache_distinguishes_size_and_spec()
    {
        using var measurer = new ImageSharpTextMeasurer();
        var small = measurer.Shape(Probe, FontSize, FontSpec.Default);
        var large = measurer.Shape(Probe, FontSize * 2, FontSpec.Default);
        large.Should().NotBeSameAs(small);
        large.Advance.Should().BeGreaterThan(small.Advance,
            "doubling font size must roughly double advance — collision in the cache would return the smaller run");
    }

    private static bool HostHasInstalledSansSerif()
    {
        try
        {
            var sys = SystemFonts.Collection;
            return sys.TryGet("Helvetica Neue", out _)
                || sys.TryGet("Helvetica", out _)
                || sys.TryGet("Arial", out _)
                || sys.TryGet("Segoe UI", out _)
                || sys.TryGet("DejaVu Sans", out _)
                || sys.TryGet("Liberation Sans", out _)
                || sys.TryGet("Inter", out _);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private static bool HostHasInstalledFontStyle(FontStyle style)
    {
        try
        {
            foreach (var family in SystemFonts.Collection.Families)
            {
                var styles = family.GetAvailableStyles().Span;
                for (var i = 0; i < styles.Length; i++)
                {
                    if (styles[i] == style)
                    {
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // System fonts unavailable on this host.
        }
        return false;
    }
}

