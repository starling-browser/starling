using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Css.Properties;
using Starling.Css.Values;
using Starling.Dom;

namespace Starling.Css.Spec.Tests.CssContain2;

/// <summary>
/// Property + cascade conformance for
/// <see href="https://www.w3.org/TR/css-contain-2/">CSS Containment 2</see>.
/// </summary>
[TestClass]
[Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/")]
public sealed class ContainmentTests
{
    private static List<PropertyDeclaration> Expand(string css)
    {
        var sheet = CssParser.ParseStyleSheet($"x {{ {css} }}");
        var rule = (StyleRule)sheet.Rules[0];
        return rule.Declarations.SelectMany(PropertyRegistry.Parse).ToList();
    }

    private static CssValue Contain(string value)
        => Expand($"contain: {value};").Single(d => d.Id == PropertyId.Contain).Value;

    private static void ShouldBeKeywordList(CssValue value, params string[] keywords)
    {
        var list = value.Should().BeOfType<CssValueList>().Subject;
        list.Values.Should().HaveCount(keywords.Length);
        for (var i = 0; i < keywords.Length; i++)
        {
            list.Values[i].Should().Be(new CssKeyword(keywords[i]));
        }
    }

    // ---- contain: single keywords ----

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#contain-property", section: "3")]
    [SpecFact]
    public void Contain_none_parses()
        => Contain("none").Should().Be(new CssKeyword("none"));

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#contain-property", section: "3")]
    [SpecFact]
    public void Contain_strict_parses()
        => Contain("strict").Should().Be(new CssKeyword("strict"));

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#contain-property", section: "3")]
    [SpecFact]
    public void Contain_content_parses()
        => Contain("content").Should().Be(new CssKeyword("content"));

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#contain-property", section: "3")]
    [SpecFact]
    public void Contain_size_parses()
        => Contain("size").Should().Be(new CssKeyword("size"));

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#contain-property", section: "3")]
    [SpecFact]
    public void Contain_inline_size_parses()
        => Contain("inline-size").Should().Be(new CssKeyword("inline-size"));

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#contain-property", section: "3")]
    [SpecFact]
    public void Contain_layout_parses()
        => Contain("layout").Should().Be(new CssKeyword("layout"));

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#contain-property", section: "3")]
    [SpecFact]
    public void Contain_style_parses()
        => Contain("style").Should().Be(new CssKeyword("style"));

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#contain-property", section: "3")]
    [SpecFact]
    public void Contain_paint_parses()
        => Contain("paint").Should().Be(new CssKeyword("paint"));

    // ---- contain: keyword combinations ----

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#contain-property", section: "3")]
    [SpecFact]
    public void Contain_layout_paint_parses_as_list()
        => ShouldBeKeywordList(Contain("layout paint"), "layout", "paint");

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#contain-property", section: "3")]
    [SpecFact]
    public void Contain_size_layout_paint_parses_as_list()
        => ShouldBeKeywordList(Contain("size layout paint"), "size", "layout", "paint");

    // ---- contain: initial + inheritance ----

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#contain-property", section: "3")]
    [SpecFact]
    public void Contain_initial_is_none()
        => PropertyRegistry.InitialValue(PropertyId.Contain).Should().Be(new CssKeyword("none"));

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#contain-property", section: "3")]
    [SpecFact]
    public void Contain_is_not_inherited()
        => PropertyRegistry.Inherits(PropertyId.Contain).Should().BeFalse();

    // ---- content-visibility ----

    private static CssValue ContentVisibility(string value)
        => Expand($"content-visibility: {value};")
            .Single(d => d.Id == PropertyId.ContentVisibility).Value;

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#content-visibility", section: "4")]
    [SpecFact]
    public void Content_visibility_visible_parses()
        => ContentVisibility("visible").Should().Be(new CssKeyword("visible"));

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#content-visibility", section: "4")]
    [SpecFact]
    public void Content_visibility_auto_parses()
        => ContentVisibility("auto").Should().Be(new CssKeyword("auto"));

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#content-visibility", section: "4")]
    [SpecFact]
    public void Content_visibility_hidden_parses()
        => ContentVisibility("hidden").Should().Be(new CssKeyword("hidden"));

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#content-visibility", section: "4")]
    [SpecFact]
    public void Content_visibility_initial_is_visible()
        => PropertyRegistry.InitialValue(PropertyId.ContentVisibility).Should().Be(new CssKeyword("visible"));

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#content-visibility", section: "4")]
    [SpecFact]
    public void Content_visibility_is_not_inherited()
        => PropertyRegistry.Inherits(PropertyId.ContentVisibility).Should().BeFalse();

    // ---- container-type (lightly covered; belongs to contain-3) ----

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#contain-property", section: "3")]
    [SpecFact]
    public void Container_type_inline_size_parses()
        => Expand("container-type: inline-size;")
            .Single(d => d.Id == PropertyId.ContainerType).Value
            .Should().Be(new CssKeyword("inline-size"));

    // ---- cascade ----

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#contain-property", section: "3")]
    [SpecFact]
    public void Cascade_computes_authored_contain_on_element()
    {
        var doc = new Document();
        var div = doc.CreateElement("div");
        doc.AppendChild(div);

        var engine = new StyleEngine(includeUserAgentStyleSheet: false);
        engine.AddStyleSheet(CssParser.ParseStyleSheet("div { contain: layout paint; }"));

        var computed = engine.Compute(div).Get(PropertyId.Contain);
        ShouldBeKeywordList(computed, "layout", "paint");
    }

    [Spec("css-contain-2", "https://www.w3.org/TR/css-contain-2/#contain-property", section: "3")]
    [SpecFact]
    public void Cascade_uses_initial_contain_when_unset()
    {
        var doc = new Document();
        var div = doc.CreateElement("div");
        doc.AppendChild(div);

        var engine = new StyleEngine(includeUserAgentStyleSheet: false);
        engine.Compute(div).Get(PropertyId.Contain).Should().Be(new CssKeyword("none"));
    }
}
