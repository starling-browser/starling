using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Layout.Text;
using Starling.Spec;
namespace Starling.Layout.Tests;

/// <summary>
/// CSS 2.2 §10.8.1 — when the used <c>line-height</c> exceeds the font's
/// content area, the difference ("leading") is split half above and half below
/// the glyphs. The alphabetic baseline therefore sits at half-leading + ascent
/// from the top of the line box, vertically centring the text. A regression
/// here pinned text to the top of the line box, so pills, buttons, and search
/// fields (all inheriting <c>line-height: 1.6</c>) rendered visibly too high.
/// </summary>
[TestClass]
public sealed class LineHeightLeadingTests
{
    // DefaultTextMeasurer: ascent = 0.8em, content area (NormalLineHeight) = 1.2em.
    private const double Ascent = 0.8;
    private const double ContentArea = 1.2;

    private static BlockBox Layout(string html, Size viewport)
        => new LayoutEngine(new StyleEngine(), DefaultTextMeasurer.Instance)
            .LayoutDocument(HtmlParser.Parse(html), viewport);

    [TestMethod]
    [Spec("css2", "https://www.w3.org/TR/CSS21/visudet.html#leading", section: "10.8.1")]
    public void Extra_line_height_is_split_half_above_and_below_the_glyphs()
    {
        const double fontSize = 20;
        const double lineHeight = 40; // > content area (24) → 16px of leading

        var root = Layout(
            $"""<body style="margin:0"><p style="font-size:{fontSize}px; line-height:{lineHeight}px">hi</p></body>""",
            new Size(400, 300));

        var frag = FirstFragment(root);
        frag.Height.Should().BeApproximately(lineHeight, 0.5);

        // baseline = half-leading + ascent = (40 - 24)/2 + 16 = 24.
        var expectedBaseline = (lineHeight - ContentArea * fontSize) / 2 + Ascent * fontSize;
        frag.Baseline.Should().BeApproximately(expectedBaseline, 0.5);

        // The glyph box (ascent above baseline, descent below) is centred: the
        // gap above the ascent equals the gap below the descent.
        var descent = (ContentArea - Ascent) * fontSize;
        var gapAbove = frag.Baseline - Ascent * fontSize;
        var gapBelow = frag.Height - (frag.Baseline + descent);
        gapAbove.Should().BeApproximately(gapBelow, 0.5);
        gapAbove.Should().BeApproximately(8, 0.5);
    }

    [TestMethod]
    [Spec("css2", "https://www.w3.org/TR/CSS21/visudet.html#leading", section: "10.8.1")]
    public void Normal_line_height_keeps_the_baseline_at_the_ascent()
    {
        const double fontSize = 20;

        var root = Layout(
            $"""<body style="margin:0"><p style="font-size:{fontSize}px; line-height:normal">hi</p></body>""",
            new Size(400, 300));

        var frag = FirstFragment(root);
        // No extra leading → baseline stays at the ascent (0.8em).
        frag.Baseline.Should().BeApproximately(Ascent * fontSize, 0.5);
    }

    private static TextFragment FirstFragment(Box.Box root)
    {
        var tb = FindText(root)!;
        tb.Fragments.Should().NotBeEmpty();
        return tb.Fragments[0];
    }

    private static TextBox? FindText(Box.Box box)
    {
        if (box is TextBox { Fragments.Count: > 0 } tb)
        {
            return tb;
        }

        foreach (var c in box.Children)
        {
            if (FindText(c) is { } hit)
            {
                return hit;
            }
        }

        return null;
    }
}
