using AwesomeAssertions;
using SixLabors.ImageSharp;
using Starling.Layout.Box;
namespace Starling.Engine.Tests;

/// <summary>
/// Smoke-tests the interactive layout path that powers the GUI: cascade + layout
/// without rasterization, returning a <see cref="LaidOutPage"/> with positions
/// callers can walk to emit native views, hit-test taps, and drive Cmd-F.
/// </summary>
[TestClass]
public class EngineLayoutPageTests
{
    [TestMethod]
    public async Task LayoutPageAsync_returns_box_tree_for_file_url()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-layout-{Guid.NewGuid():N}.html");
        try
        {
            File.WriteAllText(fixture,
                "<!doctype html><html><head><title>Smoke</title></head>" +
                "<body><h1>Heading</h1><p>First paragraph.</p><p>Second.</p></body></html>");

            var engine = new StarlingEngine();
            var result = await engine.LayoutPageAsync(
                "file://" + fixture.Replace('\\', '/'),
                new RenderOptions(new Size(800, 600), FontSize: 16f),
                CancellationToken.None);

            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            using var page = result.Value;

            page.Title.Should().Be("Smoke");
            page.Viewport.Width.Should().Be(800);
            page.Root.Frame.Width.Should().BeGreaterThan(0);
            page.DocumentHeight.Should().BeGreaterThan(0, "the page should occupy vertical space");
            page.Document.Should().NotBeNull();

            // Box tree should contain text fragments for both paragraphs in the laid-out result.
            var fragments = CollectTextFragments(page.Root).ToList();
            fragments.Should().Contain(f => f.Text.Contains("Heading", StringComparison.Ordinal));
            fragments.Should().Contain(f => f.Text.Contains("First", StringComparison.Ordinal));
            fragments.Should().Contain(f => f.Text.Contains("Second", StringComparison.Ordinal));
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
    public async Task LayoutPageAsync_disposes_idempotently()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-layout-{Guid.NewGuid():N}.html");
        try
        {
            File.WriteAllText(fixture, "<!doctype html><body><p>x</p></body>");
            var engine = new StarlingEngine();
            var result = await engine.LayoutPageAsync(
                "file://" + fixture.Replace('\\', '/'),
                new RenderOptions(new Size(200, 200)),
                CancellationToken.None);

            result.IsOk.Should().BeTrue();
            var page = result.Value;
            page.Dispose();
            page.Dispose(); // second dispose must be a no-op
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
    public async Task RelayoutPage_reflows_viewport_units_and_media_queries_to_new_size()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-relayout-{Guid.NewGuid():N}.html");
        try
        {
            // #vw is sized in viewport units; #mq flips width via a width media
            // query. Both depend on the layout viewport being threaded into the
            // cascade's MediaContext — the regression was that it was pinned to
            // the 1024×768 default, so neither responded to the real size.
            File.WriteAllText(fixture,
                "<!doctype html><html><head><style>" +
                "#vw { width: 50vw; height: 10px; }" +
                "#mq { width: 100px; height: 10px; }" +
                "@media (min-width: 1000px) { #mq { width: 600px; } }" +
                "</style></head><body><div id=vw></div><div id=mq></div></body></html>");

            var url = "file://" + fixture.Replace('\\', '/');
            var engine = new StarlingEngine();
            var result = await engine.LayoutPageAsync(
                url, new RenderOptions(new Size(800, 600), FontSize: 16f), CancellationToken.None);
            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");

            var page = result.Value;
            WidthOf(page.Root, "vw").Should().BeApproximately(400, 1, "50vw of an 800px viewport");
            WidthOf(page.Root, "mq").Should().BeApproximately(100, 1, "(min-width: 1000px) must not match at 800px");

            // Reflow to a wider viewport without re-fetching.
            var wide = engine.RelayoutPage(page, new RenderOptions(new Size(1200, 900), FontSize: 16f));
            page.Dispose();

            wide.Viewport.Width.Should().Be(1200);
            WidthOf(wide.Root, "vw").Should().BeApproximately(600, 1, "50vw of a 1200px viewport");
            WidthOf(wide.Root, "mq").Should().BeApproximately(600, 1, "(min-width: 1000px) now matches at 1200px");
            wide.Dispose();
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
    public async Task RelayoutPage_keeps_successor_usable_after_disposing_original()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-relayout-{Guid.NewGuid():N}.html");
        try
        {
            File.WriteAllText(fixture, "<!doctype html><body><p>resize me</p></body>");
            var url = "file://" + fixture.Replace('\\', '/');
            var engine = new StarlingEngine();
            var result = await engine.LayoutPageAsync(
                url, new RenderOptions(new Size(800, 600), FontSize: 16f), CancellationToken.None);
            result.IsOk.Should().BeTrue();

            var page = result.Value;
            var wide = engine.RelayoutPage(page, new RenderOptions(new Size(1200, 900), FontSize: 16f));

            // The successor reuses the original's document and resource resolvers;
            // disposing the original must not release them out from under it.
            page.Dispose();
            page.Dispose(); // idempotent

            wide.Root.Frame.Width.Should().BeGreaterThan(0);
            wide.Document.Should().NotBeNull();
            using (var bmp = engine.RenderFrame(wide, 0))
            {
                bmp.Should().NotBeNull("the successor's resolvers are still alive");
            }

            wide.Dispose();
        }
        finally
        {
            if (File.Exists(fixture))
            {
                File.Delete(fixture);
            }
        }
    }

    private static double WidthOf(Box root, string id)
    {
        foreach (var b in Walk(root))
        {
            if (string.Equals(b.Element?.GetAttribute("id"), id, StringComparison.Ordinal))
            {
                return b.Frame.Width;
            }
        }

        throw new InvalidOperationException($"no box found for #{id}");
    }

    private static IEnumerable<Box> Walk(Box box)
    {
        yield return box;
        foreach (var child in box.Children)
        {
            foreach (var d in Walk(child))
            {
                yield return d;
            }
        }
    }

    private static IEnumerable<(string Text, double Y)> CollectTextFragments(Box box)
    {
        if (box is TextBox tb)
        {
            foreach (var f in tb.Fragments)
            {
                yield return (f.Text, box.Frame.Y + f.Y);
            }
        }
        foreach (var child in box.Children)
        {
            foreach (var f in CollectTextFragments(child))
            {
                yield return f;
            }
        }
    }
}
