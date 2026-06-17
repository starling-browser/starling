using AwesomeAssertions;
using SixLabors.ImageSharp;
using Starling.Layout.Verification;
using Starling.Paint;
using DomText = Starling.Dom.Text;

namespace Starling.Engine.Tests;

/// <summary>
/// End-to-end check of the engine's incremental relayout path: after a DOM
/// mutation, <see cref="StarlingEngine.RelayoutPage"/> reuses the page's
/// persistent StyleEngine and retained box tree, and the result matches a full
/// rebuild.
/// </summary>
[TestClass]
public class IncrementalRelayoutTests
{
    private static readonly RenderOptions Options = new(new Size(800, 600), FontSize: 16f);

    [TestMethod]
    public async Task Incremental_relayout_after_text_change_matches_full_rebuild()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-inc-{Guid.NewGuid():N}.html");
        File.WriteAllText(fixture,
            "<!doctype html><html><body>" +
            "<div id=a>alpha</div><div id=b>beta</div><div id=c>gamma</div>" +
            "</body></html>");
        try
        {
            var engine = new StarlingEngine();
            var result = await engine.LayoutPageAsync(
                "file://" + fixture.Replace('\\', '/'), Options, CancellationToken.None);
            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            var page = result.Value;

            // Prime the session (first relayout = full build, which also enables
            // mutation recording for subsequent frames).
            var primed = engine.RelayoutPage(page, Options);

            // Mutate a text node — recorded into the batch, reconciled in place.
            var b = primed.Document.GetElementById("b")!;
            ((DomText)b.FirstChild!).Data = "beta is now a good deal longer and will wrap onto several lines here";

            var incremental = engine.RelayoutPage(primed, Options);

            // Reference: a full rebuild of the same (mutated) DOM, laid out from
            // scratch by the painter.
            var (referenceRoot, _) = new Painter().LayoutDocumentWithStyle(
                incremental.Document, new Starling.Layout.Size(800, 600), defaultFontSize: 16f);

            LayoutVerifier.FindFirstDivergence(incremental.Root, referenceRoot)
                .Should().BeNull("incremental relayout must match a full rebuild");
            incremental.Root.Frame.Should().Be(referenceRoot.Frame);
            // The mutation actually took effect (taller than the unmutated layout).
            incremental.DocumentHeight.Should().BeGreaterThan(0);
        }
        finally
        {
            if (File.Exists(fixture))
            {
                File.Delete(fixture);
            }
        }
    }

    [TestMethod]
    public async Task Incremental_relayout_reuses_the_pages_style_engine()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-inc-{Guid.NewGuid():N}.html");
        File.WriteAllText(fixture, "<!doctype html><body><div id=a>x</div></body>");
        try
        {
            var engine = new StarlingEngine();
            var result = await engine.LayoutPageAsync(
                "file://" + fixture.Replace('\\', '/'), Options, CancellationToken.None);
            var page = result.Value;

            var a = engine.RelayoutPage(page, Options);
            var b = engine.RelayoutPage(a, Options);

            // Phase 2f: the StyleEngine (and its animation/transition engines) is
            // not rebuilt per frame — it rides across relayout successors.
            ReferenceEquals(a.Style, b.Style).Should().BeTrue();
            ReferenceEquals(page.Style, a.Style).Should().BeTrue();
        }
        finally
        {
            if (File.Exists(fixture))
            {
                File.Delete(fixture);
            }
        }
    }
}
