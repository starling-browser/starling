using AwesomeAssertions;
using Starling.Common.Image;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Text;
using Starling.Layout.Tree;
using Starling.Paint.DisplayList;
namespace Starling.Paint.Tests;

/// <summary>
/// CSS Backgrounds 3 §3 — paint-side wiring of <c>background-image</c>,
/// <c>background-position</c> and <c>background-size</c>. The McMaster-style
/// sprite-sheet pattern <c>background-image: url(sprite.png);
/// background-position: -60px</c> on a 60×60 box must emit a
/// <see cref="DrawImage"/> whose source rect is the right slice of the
/// sprite.
/// </summary>
[TestClass]
public sealed class BackgroundImagePaintTests
{
    /// <summary>1320×60 stub: each of 22 sprite slots is 60×60.</summary>
    private static DecodedImage MakeSprite(int width = 1320, int height = 60)
        => DecodedImage.CreatePooled(width, height, span =>
        {
            // Distinct red channel value per 60px column so a wrong slice
            // would be visually distinguishable in golden tests.
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 4;
                    span[i] = (byte)((x / 60) * 11 % 256);
                    span[i + 1] = 0;
                    span[i + 2] = 0;
                    span[i + 3] = 255;
                }
            }
        });

    private sealed class StubResolver : IImageResolver
    {
        public string? Url { get; init; }
        public DecodedImage? Image { get; init; }

        public bool TryResolve(Starling.Dom.Element element, out ResolvedImage image)
        {
            image = default;
            return false;
        }

        public bool TryResolveUrl(string url, out DecodedImage image)
        {
            if (url == Url && Image is not null)
            {
                image = Image;
                return true;
            }
            image = null!;
            return false;
        }
    }

    private static (Starling.Layout.Box.BlockBox Root, Starling.Paint.DisplayList.DisplayList List, DecodedImage Sprite) Build(string html, IImageResolver resolver, Size viewport)
    {
        var document = HtmlParser.Parse(html);
        var style = new StyleEngine();
        var engine = new LayoutEngine(style, DefaultTextMeasurer.Instance, resolver);
        var root = engine.LayoutDocument(document, viewport);
        var list = new DisplayListBuilder().Build(root, styleOverride: null, images: resolver);
        return (root, list, (resolver as StubResolver)?.Image!);
    }

    [TestMethod]
    public void Background_image_with_negative_position_emits_sliced_draw_image()
    {
        using var sprite = MakeSprite();
        var resolver = new StubResolver { Url = "sprite.png", Image = sprite };

        // 60×60 box, sprite is 1320×60, bg-position picks the slice starting
        // at source x=60. Source rect should be (60, 0, 60, 60).
        var (_, list, _) = Build("""
            <body style="margin:0">
              <div style="width:60px; height:60px;
                          background-image: url(sprite.png);
                          background-position: -60px 0;
                          background-repeat: no-repeat"></div>
            </body>
            """, resolver, new Size(800, 600));

        var draws = list.Items.OfType<DrawImage>().ToList();
        draws.Should().NotBeEmpty();
        var draw = draws[0];

        draw.SourceRect.Should().NotBeNull();
        draw.SourceRect!.Value.X.Should().BeApproximately(60, 0.5);
        draw.SourceRect.Value.Y.Should().BeApproximately(0, 0.5);
        draw.SourceRect.Value.Width.Should().BeApproximately(60, 0.5);
        draw.SourceRect.Value.Height.Should().BeApproximately(60, 0.5);

        // Destination rect should fill the box exactly.
        draw.Bounds.Width.Should().BeApproximately(60, 0.5);
        draw.Bounds.Height.Should().BeApproximately(60, 0.5);
    }

    [TestMethod]
    public void Background_image_position_zero_paints_top_left_slice()
    {
        using var sprite = MakeSprite();
        var resolver = new StubResolver { Url = "sprite.png", Image = sprite };

        var (_, list, _) = Build("""
            <body style="margin:0">
              <div style="width:60px; height:60px;
                          background-image: url(sprite.png);
                          background-position: 0 0"></div>
            </body>
            """, resolver, new Size(800, 600));

        var draw = list.Items.OfType<DrawImage>().Single();
        draw.SourceRect!.Value.X.Should().BeApproximately(0, 0.5);
        draw.SourceRect.Value.Width.Should().BeApproximately(60, 0.5);
    }

    [TestMethod]
    public void No_background_image_emits_no_draw_image_for_plain_box()
    {
        using var sprite = MakeSprite();
        var resolver = new StubResolver { Url = "sprite.png", Image = sprite };

        var (_, list, _) = Build("""
            <body style="margin:0">
              <div style="width:60px; height:60px"></div>
            </body>
            """, resolver, new Size(800, 600));

        list.Items.OfType<DrawImage>().Should().BeEmpty();
    }

    [TestMethod]
    public void Unresolved_url_does_not_emit_draw_image()
    {
        // Stub returns false for any url — DisplayListBuilder must skip the
        // background-image emission rather than throwing.
        var resolver = new StubResolver { Url = null, Image = null };

        var (_, list, _) = Build("""
            <body style="margin:0">
              <div style="width:60px; height:60px;
                          background-image: url(missing.png)"></div>
            </body>
            """, resolver, new Size(800, 600));

        list.Items.OfType<DrawImage>().Should().BeEmpty();
    }
}
