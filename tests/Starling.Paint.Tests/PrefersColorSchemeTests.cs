using AwesomeAssertions;
using Starling.Css.Media;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Html;
namespace Starling.Paint.Tests;

/// <summary>
/// The Painter accepts a <see cref="ColorScheme"/> and threads it into the
/// <c>StyleEngine.MediaContext</c> so author <c>@media (prefers-color-scheme:
/// …)</c> rules participate in the cascade. The interactive shell binds this
/// to its light/dark theme button.
/// </summary>
[TestClass]
public sealed class PrefersColorSchemeTests
{
    private const string Html =
        "<html><head><style>" +
        "  body { color: black; }" +
        "  @media (prefers-color-scheme: dark) { body { color: white; } }" +
        "</style></head><body>hi</body></html>";

    [TestMethod]
    public void Light_scheme_does_not_match_dark_media_rule()
    {
        var doc = HtmlParser.Parse(Html);
        var (_, style) = new Painter().LayoutDocumentWithStyle(
            doc, new Starling.Layout.Size(200, 100),
            defaultFontSize: 16f, colorScheme: ColorScheme.Light);

        style.Compute(Body(doc)).GetColor(PropertyId.Color)
            .Should().Be(new CssColor(0, 0, 0));
    }

    [TestMethod]
    public void Dark_scheme_applies_dark_media_rule()
    {
        var doc = HtmlParser.Parse(Html);
        var (_, style) = new Painter().LayoutDocumentWithStyle(
            doc, new Starling.Layout.Size(200, 100),
            defaultFontSize: 16f, colorScheme: ColorScheme.Dark);

        style.Compute(Body(doc)).GetColor(PropertyId.Color)
            .Should().Be(new CssColor(255, 255, 255));
    }

    private static Starling.Dom.Element Body(Starling.Dom.Document doc)
    {
        foreach (var b in doc.GetElementsByTagName("body"))
        {
            return b;
        }

        throw new InvalidOperationException("no <body>");
    }
}
