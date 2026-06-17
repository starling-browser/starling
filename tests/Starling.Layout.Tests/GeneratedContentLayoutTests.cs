using AwesomeAssertions;
using Starling.Css;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Html;
using Starling.Layout.Box;
using Starling.Spec;

namespace Starling.Layout.Tests;

[TestClass]
[Spec("css-content-3", "https://www.w3.org/TR/css-content-3/")]
[Spec("css-lists-3", "https://www.w3.org/TR/css-lists-3/")]
public sealed class GeneratedContentLayoutTests
{
    private static BlockBox Layout(string html, string? css = null)
    {
        var engine = new StyleEngine();
        if (css is not null)
        {
            engine.AddStyleSheet(CssParser.ParseStyleSheet(css, StyleOrigin.Author));
        }

        return new LayoutEngine(engine).LayoutDocument(HtmlParser.Parse(html), new Size(800, 600));
    }

    private static IEnumerable<TextBox> TextBoxes(Box.Box box)
    {
        if (box is TextBox tb) { yield return tb; yield break; }
        foreach (var child in box.Children)
        {
            foreach (var inner in TextBoxes(child))
            {
                yield return inner;
            }
        }
    }

    private static List<string> Fragments(Box.Box root)
        => TextBoxes(root).SelectMany(tb => tb.Fragments.Select(f => f.Text)).ToList();

    private static List<string> MarkerTexts(Box.Box root)
        => TextBoxes(root).Select(tb => tb.Text).ToList();

    [TestMethod]
    public void Before_content_yields_a_leading_fragment()
    {
        var root = Layout("<body><p>body</p></body>", "p::before { content: \"x\"; }");
        var texts = MarkerTexts(root);
        texts.Should().Contain("x");
        // The generated "x" should precede the element's own text.
        var frags = Fragments(root);
        var idxX = frags.FindIndex(t => t.Contains('x'));
        var idxBody = frags.FindIndex(t => t.Contains("body"));
        idxX.Should().BeGreaterThanOrEqualTo(0);
        idxBody.Should().BeGreaterThanOrEqualTo(0);
        idxX.Should().BeLessThan(idxBody);
    }

    [TestMethod]
    public void Content_none_suppresses_the_pseudo_box()
    {
        var root = Layout("<body><p>body</p></body>", "p::before { content: none; }");
        MarkerTexts(root).Should().NotContain(t => t == "none");
        // Only the element's own text exists; no generated fragment.
        Fragments(root).Should().NotContain(t => t.Contains("none"));
    }

    [TestMethod]
    public void After_content_yields_a_trailing_fragment()
    {
        var root = Layout("<body><p>hi</p></body>", "p::after { content: \"!\"; }");
        var frags = Fragments(root);
        frags.FindIndex(t => t.Contains('!')).Should()
            .BeGreaterThan(frags.FindIndex(t => t.Contains("hi")));
    }

    [TestMethod]
    public void Content_attr_reflects_the_attribute_value()
    {
        var root = Layout("<body><p data-x=\"PFX\">y</p></body>", "p::before { content: attr(data-x); }");
        MarkerTexts(root).Should().Contain("PFX");
    }

    [TestMethod]
    public void Ol_of_three_li_yields_decimal_markers()
    {
        var root = Layout("<body><ol><li>a</li><li>b</li><li>c</li></ol></body>");
        var markers = MarkerTexts(root);
        markers.Should().Contain("1. ");
        markers.Should().Contain("2. ");
        markers.Should().Contain("3. ");
    }

    [TestMethod]
    public void Ul_li_yields_a_disc_marker()
    {
        var root = Layout("<body><ul><li>a</li></ul></body>");
        MarkerTexts(root).Should().Contain(t => t.Contains(ListMarkerDisc));
    }

    [TestMethod]
    public void List_style_type_none_removes_markers()
    {
        var root = Layout("<body><ul><li>a</li></ul></body>", "li { list-style-type: none; }");
        MarkerTexts(root).Should().NotContain(t => t.Contains(ListMarkerDisc));
        MarkerTexts(root).Should().NotContain(t => t.StartsWith("1."));
    }

    [TestMethod]
    public void Upper_roman_list_numbers_with_roman_markers()
    {
        var root = Layout("<body><ol><li>a</li><li>b</li><li>c</li><li>d</li></ol></body>", "ol { list-style-type: upper-roman; }");
        var markers = MarkerTexts(root);
        markers.Should().Contain("I. ");
        markers.Should().Contain("IV. ");
    }

    [TestMethod]
    public void Ol_start_attribute_offsets_the_first_ordinal()
    {
        var root = Layout("<body><ol start=\"5\"><li>a</li><li>b</li></ol></body>");
        var markers = MarkerTexts(root);
        markers.Should().Contain("5. ");
        markers.Should().Contain("6. ");
    }

    // U+2022 BULLET — the disc glyph the marker generator emits.
    private const string ListMarkerDisc = "•";
}
