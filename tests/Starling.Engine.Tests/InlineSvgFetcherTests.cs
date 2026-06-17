using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Css.Values;
using Starling.Dom;
using Starling.Html;

namespace Starling.Engine.Tests;

/// <summary>
/// Inline <c>&lt;svg&gt;</c> rendering: the fetcher serializes a parsed SVG
/// subtree and rasterizes it through the managed decoder, resolving
/// <c>currentColor</c> against the element's computed color. This is what makes
/// inline icons (e.g. the netclaw "Search docs" magnifying glass) draw instead
/// of vanishing.
/// </summary>
[TestClass]
public sealed class InlineSvgFetcherTests
{
    private static Element FirstSvg(string html)
        => HtmlParser.Parse(html).GetElementsByTagName("svg").First();

    [TestMethod]
    public void Serializer_round_trips_tags_attributes_and_escaping()
    {
        var svg = FirstSvg(
            """<body><svg viewBox="0 0 24 24" stroke="currentColor"><circle cx="11" cy="11" r="8"></circle><path d="m21 21-4.35-4.35"></path></svg></body>""");

        var xml = InlineSvgSerializer.Serialize(svg);

        xml.Should().StartWith("<svg");
        xml.Should().Contain("viewbox=\"0 0 24 24\"").And.Contain("stroke=\"currentColor\"");
        xml.Should().Contain("<circle").And.Contain("cx=\"11\"").And.Contain("r=\"8\"");
        xml.Should().Contain("<path").And.Contain("d=\"m21 21-4.35-4.35\"");
        // Well-formed enough for the decoder's XML parser to consume.
        var act = () => System.Xml.Linq.XDocument.Parse(xml);
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Inline_svg_resolves_to_a_raster_in_currentColor()
    {
        var svg = FirstSvg(
            """<body><svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><circle cx="12" cy="12" r="8"></circle></svg></body>""");

        using var fetcher = new ImageFetcher(
            NullLoggerFactory.Instance,
            () => throw new InvalidOperationException("inline svg must not hit the network"));

        var ok = fetcher.TryResolveInlineSvg(svg, new CssColor(0, 128, 255), out var resolved);

        ok.Should().BeTrue();
        resolved.Width.Should().Be(24);
        resolved.Height.Should().Be(24);

        // A stroke ring is painted in the supplied blue (currentColor), and the
        // disc center stays transparent (fill="none").
        var px = resolved.Source.Pixels.Span;
        int blue = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] > 0 && px[i + 2] > 150 && px[i] < 80)
            {
                blue++;
            }
        }

        blue.Should().BeGreaterThan(0);

        int center = (12 * resolved.Source.Width + 12) * 4;
        px[center + 3].Should().Be(0);
    }

    [TestMethod]
    public void Inline_svg_decode_is_cached_per_element()
    {
        var svg = FirstSvg(
            """<body><svg width="16" height="16" viewBox="0 0 16 16"><rect width="16" height="16" fill="red"></rect></svg></body>""");

        using var fetcher = new ImageFetcher(NullLoggerFactory.Instance, () => throw new InvalidOperationException());

        fetcher.TryResolveInlineSvg(svg, CssColor.Black, out var a).Should().BeTrue();
        fetcher.TryResolveInlineSvg(svg, CssColor.Black, out var b).Should().BeTrue();
        ReferenceEquals(a.Source, b.Source).Should().BeTrue("a second resolve returns the cached raster");
    }
}
