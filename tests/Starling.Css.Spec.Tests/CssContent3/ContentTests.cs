using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Css.Properties;
using Starling.Css.Selectors;
using Starling.Css.Values;
using Starling.Dom;

namespace Starling.Css.Spec.Tests.CssContent3;

/// <summary>
/// Property + cascade conformance for
/// <see href="https://www.w3.org/TR/css-content-3/">CSS Generated Content 3</see>.
/// </summary>
[TestClass]
[Spec("css-content-3", "https://www.w3.org/TR/css-content-3/")]
public sealed class ContentTests
{
    private static List<PropertyDeclaration> Expand(string css)
    {
        var sheet = CssParser.ParseStyleSheet($"x {{ {css} }}");
        var rule = (StyleRule)sheet.Rules[0];
        return rule.Declarations.SelectMany(PropertyRegistry.Parse).ToList();
    }

    private static (StyleEngine Engine, Element Element) Styled(
        string css, (string Name, string Value)[]? attrs = null)
    {
        var doc = new Document();
        var p = doc.CreateElement("p");
        if (attrs is not null)
        {
            foreach (var (name, value) in attrs)
            {
                p.SetAttribute(name, value);
            }
        }

        p.AppendChild(doc.CreateTextNode("x"));
        doc.AppendChild(p);
        var engine = new StyleEngine();
        engine.AddStyleSheet(CssParser.ParseStyleSheet(css, StyleOrigin.Author));
        return (engine, p);
    }

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_string_parses_to_css_string()
        => Expand("content: \"\\00BB \";").Single(d => d.Id == PropertyId.Content).Value
            .Should().BeOfType<CssString>();

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_none_is_keyword()
        => Expand("content: none;").Single(d => d.Id == PropertyId.Content).Value
            .Should().Be(new CssKeyword("none"));

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Before_pseudo_element_gets_content_in_its_cascade()
    {
        var (engine, p) = Styled("p::before { content: \"» \"; }");
        var pStyle = engine.Compute(p);
        var before = engine.ComputePseudoElement(p, PseudoElement.Before, pStyle);
        before.Should().NotBeNull();
        before!.Get(PropertyId.Content).Should().Be(new CssString("» "));
    }

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Pseudo_without_rule_computes_to_normal_content()
    {
        var (engine, p) = Styled("p { color: red; }");
        var pStyle = engine.Compute(p);
        var before = engine.ComputePseudoElement(p, PseudoElement.Before, pStyle);
        // No ::before rule → content stays at its initial `normal` (renders nothing).
        before!.Get(PropertyId.Content).Should().Be(new CssKeyword("normal"));
    }

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Before_pseudo_attr_resolves_against_originating_element()
    {
        var (engine, p) = Styled("p::before { content: attr(data-x); }",
            [("data-x", "hello")]);
        var pStyle = engine.Compute(p);
        var before = engine.ComputePseudoElement(p, PseudoElement.Before, pStyle);
        before!.Get(PropertyId.Content).Should().Be(new CssString("hello"));
    }

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Pseudo_inherits_color_from_originating_element()
    {
        var (engine, p) = Styled("p { color: rgb(10, 20, 30); } p::before { content: \"y\"; }");
        var pStyle = engine.Compute(p);
        var before = engine.ComputePseudoElement(p, PseudoElement.Before, pStyle);
        // color is inherited; the pseudo inherits from its originating element.
        var color = before!.Get(PropertyId.Color).Should().BeOfType<Values.CssColor>().Subject;
        (color.R, color.G, color.B).Should().Be(((byte)10, (byte)20, (byte)30));
    }
}
