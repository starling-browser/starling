using AwesomeAssertions;
using SixLabors.Fonts;
using Starling.Css.FontFace;
using Starling.Layout.Text;
namespace Starling.Paint.Tests;

/// <summary>
/// Locks down the regression where the ImageSharp backend held only the
/// bundled OpenSans-Regular in its <see cref="FontCollection"/>, so
/// <c>font-family: "Helvetica Neue", Helvetica, Arial, sans-serif</c> resolved
/// to OpenSans Regular even on Bold/Italic requests. <see cref="ImageSharpFontLookup"/>
/// now loads system fonts and expands CSS generics so Bold/Italic faces
/// resolve to real fonts on macOS (and other supported platforms).
/// </summary>
[TestClass]
public sealed class ImageSharpFontLookupTests
{
    private const float Size = 16f;

    /// <summary>Bundled OpenSans-Regular guarantees the collection is never empty.</summary>
    [TestMethod]
    public void Collection_loads_at_least_the_bundled_family()
    {
        var collection = ImageSharpFontLookup.LoadCollection();
        var families = collection.Families.ToList();
        families.Should().NotBeEmpty(
            "the bundled OpenSans-Regular.ttf is embedded in Starling.Paint.dll and is the final fallback");
        families.Should().Contain(f => f.Name.Contains("Open Sans", StringComparison.OrdinalIgnoreCase),
            "OpenSans is the deterministic fallback face the engine ships with");
    }

    /// <summary>
    /// Skip on platforms where the host has no installed sans-serif (sandboxed
    /// CI without any system fonts). The probe consults
    /// <see cref="SystemFonts"/> directly so a regression that prevents the
    /// lookup from consulting system fonts fails the test rather than
    /// skipping it.
    /// </summary>
    [TestMethod]
    public void Generic_sans_serif_resolves_to_a_system_family_when_available()
    {
        if (!(HostHasInstalledSansSerif())) { Assert.Inconclusive("no major system sans-serif installed on this host; nothing to assert"); return; }

        var collection = ImageSharpFontLookup.LoadCollection();
        var spec = new FontSpec(["sans-serif"], bold: false, italic: false);
        var font = ImageSharpFontLookup.CreateFont(collection, spec, Size);

        // sans-serif must expand to a non-OpenSans system family when one is
        // present. Resolving to OpenSans on a host that has Helvetica/Arial
        // is the regression this test guards against.
        font.Family.Name.Should().NotContain("Open Sans",
            "sans-serif should expand to a system family on a host that has one — the regression collapsed it back to bundled OpenSans");
    }

    /// <summary>
    /// The regression: h1 elements rendered as OpenSans Regular because no
    /// Bold face was registered. Verifies that a Bold request lands on a
    /// real Bold face (whichever family resolves on this host).
    /// </summary>
    [TestMethod]
    public void Bold_request_resolves_to_a_bold_face_when_one_exists()
    {
        if (!(HostHasInstalledFontStyle(FontStyle.Bold))) { Assert.Inconclusive("no system family with a Bold face on this host; can't verify Bold selection"); return; }

        var collection = ImageSharpFontLookup.LoadCollection();
        var spec = new FontSpec(["sans-serif"], bold: true, italic: false);
        var font = ImageSharpFontLookup.CreateFont(collection, spec, Size);

        font.IsBold.Should().BeTrue(
            "a CSS Bold spec must round-trip to a real Bold face — synthesised bold doesn't fix the regression");
    }

    /// <summary>Same regression, italic variant.</summary>
    [TestMethod]
    public void Italic_request_resolves_to_an_italic_face_when_one_exists()
    {
        if (!(HostHasInstalledFontStyle(FontStyle.Italic))) { Assert.Inconclusive("no system family with an Italic face on this host; can't verify Italic selection"); return; }

        var collection = ImageSharpFontLookup.LoadCollection();
        var spec = new FontSpec(["sans-serif"], bold: false, italic: true);
        var font = ImageSharpFontLookup.CreateFont(collection, spec, Size);

        font.IsItalic.Should().BeTrue(
            "a CSS Italic spec must round-trip to a real Italic face");
    }

    /// <summary>
    /// Unknown family names are expected to fall through to the bundled
    /// OpenSans rather than throwing — keeps obscure or platform-specific
    /// families from blowing up the renderer.
    /// </summary>
    [TestMethod]
    public void Unknown_family_falls_back_to_bundled_open_sans()
    {
        var collection = ImageSharpFontLookup.LoadCollection();
        var spec = new FontSpec(["This Family Definitely Does Not Exist 12345"], bold: false, italic: false);

        var font = ImageSharpFontLookup.CreateFont(collection, spec, Size);

        font.Family.Name.Should().Contain("Open Sans",
            "the bundled OpenSans is the documented terminal fallback");
    }

    /// <summary>
    /// Two specs that differ only in <see cref="FontSpec.Bold"/> must produce
    /// fonts with different advance widths — proof that the lookup is
    /// actually pulling a different face, not synthesising weight via stroke
    /// width. This is the failure mode the regression bisects on: without the
    /// fix, both renders measured to the same width because both resolved to
    /// OpenSans Regular.
    /// </summary>
    [TestMethod]
    public void Bold_and_regular_measure_different_widths()
    {
        if (!(HostHasInstalledFontStyle(FontStyle.Bold))) { Assert.Inconclusive("no system Bold face on this host; can't compare Regular vs Bold metrics"); return; }

        var collection = ImageSharpFontLookup.LoadCollection();
        var regularSpec = new FontSpec(["sans-serif"], bold: false, italic: false);
        var boldSpec = new FontSpec(["sans-serif"], bold: true, italic: false);

        const string Probe = "The quick brown fox jumps over the lazy dog";
        var regularFont = ImageSharpFontLookup.CreateFont(collection, regularSpec, Size);
        var boldFont = ImageSharpFontLookup.CreateFont(collection, boldSpec, Size);

        var regularWidth = TextMeasurer.MeasureAdvance(Probe, new TextOptions(regularFont)).Width;
        var boldWidth = TextMeasurer.MeasureAdvance(Probe, new TextOptions(boldFont)).Width;

        boldWidth.Should().NotBe(regularWidth,
            "bold and regular faces must produce different advance widths; identical means we fell back to the same Regular face");
    }

    /// <summary>
    /// Probes the host's installed font collection directly (independent of
    /// <see cref="ImageSharpFontLookup"/>), so a regression that ignores
    /// system fonts fails the dependent test rather than skipping it.
    /// </summary>
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
    /// <summary>
    /// Locks down the <c>FontFaceRegistry</c> → <c>FontCollection</c> wiring:
    /// font bytes registered through the per-document registry must appear in
    /// the collection returned by <see cref="ImageSharpFontLookup.LoadCollection(FontFaceRegistry?)"/>.
    /// Re-uses the bundled OpenSans bytes as a known-good font — the
    /// assertion is on the registry round-trip, not on which family ends up
    /// in the collection (that depends on what the font's own name table
    /// reports, which SixLabors.Fonts uses verbatim during
    /// <c>FontCollection.Add</c>).
    /// </summary>
    [TestMethod]
    public void Registered_web_font_bytes_appear_in_loaded_collection()
    {
        var asm = typeof(ImageSharpFontLookup).Assembly;
        var fontResource = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("OpenSans-Regular.ttf", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(fontResource)!;
        var fontBytes = new byte[stream.Length];
        stream.ReadExactly(fontBytes);

        using var registry = new FontFaceRegistry();
        registry.TryAdd("WebProbe", bold: false, italic: false, fontBytes).Should().BeTrue(
            "the bundled OpenSans is valid font data and should register cleanly");

        var collection = ImageSharpFontLookup.LoadCollection(registry);
        collection.TryGet("Open Sans", out _).Should().BeTrue(
            "the registry-fed font identifies itself as 'Open Sans' via its name table, so LoadCollection must surface it");
    }

    /// <summary>
    /// Regression (netclaw.dev terminal rendered as .notdef tofu): Google Fonts
    /// splits a family into one <c>@font-face</c> per script subset, each with a
    /// <c>unicode-range</c> but the same internal family name. SixLabors' font
    /// collection is name-keyed and unicode-range-blind, so folding in a subset
    /// that can't serve Basic Latin lets it shadow the Latin face and ASCII text
    /// rasterises as tofu. <see cref="FontFaceRegistry.AddTo"/> must drop such a
    /// face so the cascade's font-family fallback (e.g. → <c>monospace</c>) takes
    /// over instead. (Bytes here are full OpenSans; the registry's
    /// <c>unicode-range</c> metadata — straight from the CSS — is what marks the
    /// face as a non-Latin subset.)
    /// </summary>
    [TestMethod]
    public void AddTo_drops_webfont_subset_that_excludes_basic_latin()
    {
        var bytes = BundledOpenSansBytes();
        using var registry = new FontFaceRegistry();
        var cyrillicOnly = new UnicodeRangeSet([(0x0400, 0x04FF)]);
        registry.TryAdd("Probe", bold: false, italic: false, bytes, cyrillicOnly)
            .Should().BeTrue("the bytes are valid OpenSans; the subset face registers");

        var collection = new FontCollection();
        registry.AddTo(collection);

        collection.Families.Should().BeEmpty(
            "a subset face that can't serve Basic Latin must not be folded into the paint collection — otherwise it shadows the Latin face and ASCII renders as tofu");
    }

    /// <summary>
    /// The complement of <see cref="AddTo_drops_webfont_subset_that_excludes_basic_latin"/>:
    /// the Latin-covering subset is the representative face and must be folded in.
    /// </summary>
    [TestMethod]
    public void AddTo_keeps_webfont_subset_that_covers_basic_latin()
    {
        var bytes = BundledOpenSansBytes();
        using var registry = new FontFaceRegistry();
        var latin = new UnicodeRangeSet([(0x0000, 0x00FF)]);
        registry.TryAdd("Probe", bold: false, italic: false, bytes, latin).Should().BeTrue();

        var collection = new FontCollection();
        registry.AddTo(collection);

        collection.Families.Should().Contain(
            f => f.Name.Contains("Open Sans", StringComparison.OrdinalIgnoreCase),
            "the Latin-covering subset must be folded in so ASCII text resolves to a real face");
    }

    /// <summary>
    /// Mirrors Google Fonts' document order — non-Latin subsets first, the Latin
    /// subset last. The Latin representative must still win regardless of order,
    /// and ASCII must shape to real glyphs (not .notdef).
    /// </summary>
    [TestMethod]
    public void AddTo_prefers_latin_face_when_a_non_latin_subset_is_registered_first()
    {
        var bytes = BundledOpenSansBytes();
        using var registry = new FontFaceRegistry();
        registry.TryAdd("Probe", false, false, bytes, new UnicodeRangeSet([(0x0400, 0x04FF)]));
        registry.TryAdd("Probe", false, false, bytes, new UnicodeRangeSet([(0x0000, 0x00FF)]));

        var collection = new FontCollection();
        registry.AddTo(collection);

        collection.Families.Should().Contain(
            f => f.Name.Contains("Open Sans", StringComparison.OrdinalIgnoreCase));
        var font = ImageSharpFontLookup.CreateFont(collection, new FontSpec(["Open Sans"], false, false), Size);
        TextMeasurer.MeasureAdvance("netclaw init", new TextOptions(font)).Width
            .Should().BeGreaterThan(0, "ASCII must shape to real glyphs once the Latin face is the representative");
    }

    private static byte[] BundledOpenSansBytes()
    {
        var asm = typeof(ImageSharpFontLookup).Assembly;
        var res = asm.GetManifestResourceNames()
            .First(n => n.EndsWith("OpenSans-Regular.ttf", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(res)!;
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>
    /// Invalid downloaded font data must fail during registration so the
    /// <c>@font-face</c> loader can keep trying later <c>src</c> entries.
    /// </summary>
    [TestMethod]
    public void Registry_rejects_unreadable_font_bytes()
    {
        using var registry = new FontFaceRegistry();

        registry.TryAdd("BrokenWebFont", bold: false, italic: false, [0x77, 0x4F, 0x46, 0x46])
            .Should().BeFalse("unreadable font data must not claim a CSS src slot");
    }

    /// <summary>
    /// A null registry must behave identically to the legacy parameterless
    /// loader — important because tests, the standalone text measurer, and
    /// any caller without document context all pass <c>null</c>.
    /// </summary>
    [TestMethod]
    public void Null_registry_matches_no_registry_overload()
    {
        var withNull = ImageSharpFontLookup.LoadCollection(webFonts: null);
        var bare = ImageSharpFontLookup.LoadCollection();

        withNull.Families.Select(f => f.Name).OrderBy(n => n)
            .Should().Equal(bare.Families.Select(f => f.Name).OrderBy(n => n),
                "passing a null registry must not change which families load");
    }
}

