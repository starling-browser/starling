using AwesomeAssertions;
using Starling.Html;
using LayoutSize = Starling.Layout.Size;

namespace Starling.Paint.Tests;

[TestClass]
[TestCategory("GoldenImage")]
public sealed class M1StaticRenderingGoldenTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return [Case("hello paragraph", "<body><p>Hello, world.</p></body>", minNonWhite: 40)];
        yield return [Case("two paragraphs", "<body><p>First paragraph.</p><p>Second paragraph.</p></body>", minNonWhite: 80)];
        yield return [Case("heading plus paragraph", "<body><h1>Title</h1><p>Intro text.</p></body>", minNonWhite: 80)];
        yield return [Case("unordered list", "<body><ul><li>One</li><li>Two</li></ul></body>", minNonWhite: 40)];
        yield return [Case("nested block backgrounds", "<body><div style=\"background-color:#ff0000;width:90px;height:60px\"><div style=\"background-color:#0000ff;width:30px;height:20px\"></div></div></body>", requiredColors: [ColorCount.Red, ColorCount.Blue])];
        yield return [Case("style element background", "<head><style>.box{background-color:#008000;width:80px;height:40px}</style></head><body><div class=box>box</div></body>", requiredColors: [ColorCount.Green])];
        yield return [Case("inline style beats author style", "<head><style>.box{background-color:#ff0000;width:80px;height:40px}</style></head><body><div class=box style=\"background-color:#0000ff;width:80px;height:40px\">box</div></body>", requiredColors: [ColorCount.Blue])];
        yield return [Case("display none removes subtree", "<body><div style=\"display:none;background-color:#ff0000;width:80px;height:40px\">hidden</div><div style=\"background-color:#008000;width:80px;height:40px\">shown</div></body>", requiredColors: [ColorCount.Green], forbiddenColors: [ColorCount.Red])];
        yield return [Case("padding leaves colored panel", "<body><div style=\"background-color:#ff0000;padding:10px;width:80px;height:40px\">pad</div></body>", requiredColors: [ColorCount.Red])];
        yield return [Case("border shorthand paints border", "<body><div style=\"border:5px solid #0000ff;width:80px;height:40px\">border</div></body>", requiredColors: [ColorCount.Blue])];
        yield return [Case("explicit dimensions", "<body><div style=\"background-color:#008000;width:120px;height:25px\"></div></body>", requiredColors: [ColorCount.Green])];
        yield return [Case("wrapped text", "<body><p>one two three four five six seven eight nine ten eleven twelve</p></body>", width: 120, minNonWhite: 80)];
        yield return [Case("centered text", "<body><p style=\"text-align:center;width:180px\">centered line</p></body>", minNonWhite: 60)];
        yield return [Case("right aligned text", "<body><p style=\"text-align:right;width:180px\">right line</p></body>", minNonWhite: 50)];
        yield return [Case("font color", "<body><p style=\"color:#0000ff\">blue words</p></body>", minNonWhite: 30)];
        yield return [Case("large font", "<body><p style=\"font-size:28px\">large words</p></body>", minNonWhite: 100)];
        yield return [Case("block margin stack", "<body><div style=\"background-color:#ff0000;width:60px;height:20px;margin-bottom:20px\"></div><div style=\"background-color:#0000ff;width:60px;height:20px;margin-top:20px\"></div></body>", requiredColors: [ColorCount.Red, ColorCount.Blue])];
        yield return [Case("display contents keeps descendants", "<body><div style=\"display:contents\"><p style=\"background-color:#008000;width:80px;height:30px\">inner</p></div></body>", requiredColors: [ColorCount.Green])];
        yield return [Case("section article nesting", "<body><section style=\"background-color:#ff0000;width:100px;height:80px\"><article style=\"background-color:#0000ff;width:50px;height:30px\">article</article></section></body>", requiredColors: [ColorCount.Red, ColorCount.Blue])];
        yield return [Case("doctype document", "<!doctype html><html><head><title>x</title></head><body><main><h2>Static page</h2><p>rendered by Starling</p></main></body></html>", minNonWhite: 100)];
    }

    [TestMethod]
    [DynamicData(nameof(Cases))]
    public void Static_html_css_case_renders_expected_pixels(GoldenCase testCase)
    {
        var painter = new Painter();
        var document = HtmlParser.Parse(testCase.Html);

        using var image = painter.RenderDocument(document, new LayoutSize(testCase.Width, testCase.Height), defaultFontSize: 16f);

        image.Width.Should().Be(testCase.Width);
        image.Height.Should().Be(testCase.Height);
        BitmapPixels.CountNonWhite(image).Should().BeGreaterThanOrEqualTo(testCase.MinNonWhite, testCase.Name);

        foreach (var required in testCase.RequiredColors)
        {
            BitmapPixels.CountExact(image, required.R, required.G, required.B)
                .Should().BeGreaterThanOrEqualTo(required.MinCount, testCase.Name);
        }

        foreach (var forbidden in testCase.ForbiddenColors)
        {
            BitmapPixels.CountExact(image, forbidden.R, forbidden.G, forbidden.B)
                .Should().Be(0, testCase.Name);
        }
    }

    private static GoldenCase Case(
        string name,
        string html,
        int width = 320,
        int height = 180,
        int minNonWhite = 0,
        ColorCount[]? requiredColors = null,
        ColorCount[]? forbiddenColors = null)
        => new(name, html, width, height, minNonWhite, requiredColors ?? [], forbiddenColors ?? []);
}

public sealed record GoldenCase(
    string Name,
    string Html,
    int Width,
    int Height,
    int MinNonWhite,
    ColorCount[] RequiredColors,
    ColorCount[] ForbiddenColors)
{
    public override string ToString() => Name;
}

public sealed record ColorCount(byte R, byte G, byte B, int MinCount = 20)
{
    public static ColorCount Red { get; } = new(255, 0, 0, 100);
    public static ColorCount Green { get; } = new(0, 128, 0, 100);
    public static ColorCount Blue { get; } = new(0, 0, 255, 100);
}
