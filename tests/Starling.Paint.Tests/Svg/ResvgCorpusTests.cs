// SPDX-License-Identifier: Apache-2.0
using System.Reflection;
using AwesomeAssertions;
using Starling.Paint.Svg;
using Starling.Spec;

namespace Starling.Paint.Tests.Svg;

/// <summary>
/// Data-driven conformance corpus: one test case for every SVG in the vendored
/// resvg test suite (testdata/spec/resvg/tests, MIT — see its README). resvg's
/// suite is the de-facto reference corpus for static-SVG renderers, organised
/// into the SVG 1.1 chapters (structure, shapes, paint-servers, painting,
/// masking, text, filters). Starling decodes SVG as a static image, so each
/// resvg file maps to exactly one decode case here.
/// <para>
/// Because Starling's render won't match resvg's reference PNG pixel-for-pixel
/// (different text/filter/AA behaviour), the universal assertion across all
/// 1,679 files is a <b>robustness contract</b>: the decoder must turn every
/// real-world SVG into a valid, correctly-sized raster without throwing or
/// crashing. The 14 files that don't satisfy this today (4 XML-ENTITY, 10
/// unbounded-recursion) are excluded here and tracked one-for-one as
/// <c>[PendingFact]</c> gaps in the corpus gaps suite.
/// </para>
/// </summary>
[TestClass]
[Spec("svg11", SvgRaster.Spec11Url, section: "resvg-corpus")]
[SpecImplementedCategory]
public sealed class ResvgCorpusTests
{
    internal static readonly string CorpusRoot =
        Path.Combine(AppContext.BaseDirectory, "testdata", "spec", "resvg", "tests");

    /// <summary>
    /// Files excluded from the decode contract. Empty: every corpus file decodes
    /// to a valid raster. (The former exclusions — XML-ENTITY expansion and
    /// recursive <c>&lt;use&gt;</c>/<c>&lt;pattern&gt;</c> — are now handled by
    /// internal-DTD entity parsing plus a reference cycle/depth guard.)
    /// </summary>
    internal static readonly IReadOnlySet<string> KnownGaps = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Corpus-relative path with '/' separators (the resvg test id).</summary>
    internal static string RelId(string fullPath)
        => Path.GetRelativePath(CorpusRoot, fullPath).Replace(Path.DirectorySeparatorChar, '/');

    public static IEnumerable<object[]> Corpus()
    {
        if (!Directory.Exists(CorpusRoot))
        {
            throw new DirectoryNotFoundException(
                $"resvg corpus not found at '{CorpusRoot}'. Ensure testdata/spec/resvg is copied to output.");
        }

        foreach (var f in Directory.EnumerateFiles(CorpusRoot, "*.svg", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var id = RelId(f);
            if (!KnownGaps.Contains(id))
            {
                yield return new object[] { id };
            }
        }
    }

    public static string CaseName(MethodInfo method, object[] data)
        => $"{method.Name}({data[0]})";

    /// <summary>
    /// Every resvg corpus SVG must decode into a valid, tightly-packed RGBA
    /// raster of positive size. This is the static-SVG robustness contract —
    /// it catches any regression that makes the decoder throw, hang, or emit a
    /// malformed buffer on a real-world document.
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(Corpus), DynamicDataDisplayName = nameof(CaseName))]
    public void Decodes_to_valid_raster(string id)
    {
        var path = Path.Combine(CorpusRoot, id.Replace('/', Path.DirectorySeparatorChar));
        var svg = File.ReadAllText(path);

        using var img = SvgImageDecoder.DecodeText(svg);

        img.Should().NotBeNull($"'{id}' should decode to an image");
        img.Width.Should().BeGreaterThan(0, $"'{id}' should have a positive width");
        img.Height.Should().BeGreaterThan(0, $"'{id}' should have a positive height");
        img.Pixels.Length.Should().Be(img.Width * img.Height * 4,
            $"'{id}' must produce a tightly-packed RGBA8888 buffer");
    }
}
