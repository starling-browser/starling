using AwesomeAssertions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Starling.Html;
namespace Starling.Engine.Tests;

[TestClass]
public class EngineRenderTests
{
    [TestMethod]
    public async Task Render_writes_a_non_empty_png_for_hello_html()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-{Guid.NewGuid():N}.html");
        var output = Path.Combine(Path.GetTempPath(), $"starling-{Guid.NewGuid():N}.png");

        try
        {
            File.WriteAllText(fixture, "<!doctype html><body><p>Hello, world.</p></body>");
            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                "file://" + fixture.Replace('\\', '/'),
                new RenderOptions(new Size(400, 200), 28f),
                output);

            result.IsOk.Should().BeTrue($"Render should succeed; got: {(result.IsErr ? result.Error.Message : "")}");
            File.Exists(output).Should().BeTrue();
            new FileInfo(output).Length.Should().BeGreaterThan(100, "PNG header alone is ~50 bytes; real output is larger");
            result.Value.DisplayText.Should().Be("Hello, world.");
        }
        finally
        {
            if (File.Exists(fixture))
            {
                File.Delete(fixture);
            }

            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    [TestMethod]
    public async Task Render_fetches_and_paints_local_image_via_file_url()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"starling-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var imagePath = Path.Combine(dir, "swatch.png");
        var fixture = Path.Combine(dir, "page.html");
        var output = Path.Combine(dir, "out.png");

        try
        {
            // Write a 30x20 solid green PNG; the engine should decode it via
            // ImageSharp and paint those pixels into the output.
            using (var swatch = new Image<Rgba32>(30, 20))
            {
                swatch.ProcessPixelRows(rows =>
                {
                    for (var y = 0; y < rows.Height; y++)
                    {
                        var row = rows.GetRowSpan(y);
                        for (var x = 0; x < row.Length; x++)
                        {
                            row[x] = new Rgba32(0, 128, 0);
                        }
                    }
                });
                swatch.SaveAsPng(imagePath);
            }
            File.WriteAllText(fixture,
                "<!doctype html><body><p>before<img src=\"swatch.png\">after</p></body>");

            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                "file://" + fixture.Replace('\\', '/'),
                new RenderOptions(new Size(320, 180), 16f),
                output);

            result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
            using var rendered = Image.Load<Rgba32>(output);
            CountExact(rendered, new Rgba32(0, 128, 0)).Should().BeGreaterThanOrEqualTo(
                300, "the 30x20 green PNG (600 px) should land in the output");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task Render_returns_err_for_missing_file()
    {
        var engine = new StarlingEngine();
        var result = await engine.RenderAsync(
            "file:///definitely-not-there.html",
            RenderOptions.Default,
            Path.Combine(Path.GetTempPath(), "unused.png"));

        result.IsErr.Should().BeTrue();
        result.Error.Message.Should().Contain("File not found");
    }

    [TestMethod]
    public async Task Render_uses_document_style_layout_and_paint_pipeline()
    {
        var fixture = Path.Combine(Path.GetTempPath(), $"starling-{Guid.NewGuid():N}.html");
        var output = Path.Combine(Path.GetTempPath(), $"starling-{Guid.NewGuid():N}.png");

        try
        {
            File.WriteAllText(fixture, """
                <!doctype html>
                <html>
                  <head>
                    <style>
                      .hero { background-color: #008000; width: 120px; height: 40px; }
                    </style>
                  </head>
                  <body><div class="hero">Styled box</div></body>
                </html>
                """);

            var engine = new StarlingEngine();
            var result = await engine.RenderAsync(
                "file://" + fixture.Replace('\\', '/'),
                new RenderOptions(new Size(240, 140), 16f),
                output);

            result.IsOk.Should().BeTrue($"Render should succeed; got: {(result.IsErr ? result.Error.Message : "")}");
            using var image = Image.Load<Rgba32>(output);
            CountExact(image, new Rgba32(0, 128, 0)).Should().BeGreaterThan(1_000);
            result.Value.DisplayText.Should().Be("Styled box");
        }
        finally
        {
            if (File.Exists(fixture))
            {
                File.Delete(fixture);
            }

            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }

    [TestMethod]
    public void ExtractDisplayText_collapses_whitespace()
    {
        var doc = HtmlParser.Parse("<body>  Hello,   world. \n\t Next line. </body>");
        StarlingEngine.ExtractDisplayText(doc).Should().Be("Hello, world. Next line.");
    }

    private static int CountExact(Image<Rgba32> image, Rgba32 color)
    {
        var count = 0;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                foreach (var px in row)
                {
                    if (px.Equals(color))
                    {
                        count++;
                    }
                }
            }
        });
        return count;
    }
}
