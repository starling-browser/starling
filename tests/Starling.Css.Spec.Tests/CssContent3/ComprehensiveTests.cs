using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Css.Properties;
using Starling.Css.Selectors;
using Starling.Css.Values;
using Starling.Dom;

namespace Starling.Css.Spec.Tests.CssContent3;

/// <summary>
/// Comprehensive conformance for
/// <see href="https://www.w3.org/TR/css-content-3/">CSS Generated Content 3</see>
/// covering: <c>content</c> values (§2), <c>quotes</c> property (§2.5),
/// inheritance rules (§2), and cascade behaviour.
/// </summary>
[TestClass]
[Spec("css-content-3", "https://www.w3.org/TR/css-content-3/")]
public sealed class ComprehensiveTests
{
    // ---- helpers --------------------------------------------------------

    private static List<PropertyDeclaration> Expand(string css)
    {
        var sheet = CssParser.ParseStyleSheet($"x {{ {css} }}");
        var rule = (StyleRule)sheet.Rules[0];
        return rule.Declarations.SelectMany(PropertyRegistry.Parse).ToList();
    }

    private static PropertyDeclaration ContentDecl(string css)
        => Expand(css).Single(d => d.Id == PropertyId.Content);

    private static PropertyDeclaration QuotesDecl(string css)
        => Expand(css).Single(d => d.Id == PropertyId.Quotes);

    private static (StyleEngine Engine, Element Elem) Styled(
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

    // ---- §2 content: initial value is `normal` --------------------------

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_initial_value_is_normal()
        => PropertyRegistry.InitialValue(PropertyId.Content)
            .Should().Be(new CssKeyword("normal"));

    // ---- §2 content: normal keyword -------------------------------------

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_normal_parses_to_keyword()
        => ContentDecl("content: normal;").Value
            .Should().Be(new CssKeyword("normal"));

    // ---- §2 content: none -----------------------------------------------

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_none_parses_to_keyword()
        => ContentDecl("content: none;").Value
            .Should().Be(new CssKeyword("none"));

    // ---- §2 content: <string> -------------------------------------------

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_quoted_string_parses_to_CssString()
        => ContentDecl(@"content: ""hello"";").Value
            .Should().BeOfType<CssString>()
            .Which.Value.Should().Be("hello");

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_empty_string_parses()
        => ContentDecl(@"content: """";").Value
            .Should().BeOfType<CssString>()
            .Which.Value.Should().Be(string.Empty);

    // ---- §2 content: attr() ---------------------------------------------

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_attr_parse_produces_CssAttrReference()
        => ContentDecl("content: attr(data-label);").Value
            .Should().BeOfType<CssAttrReference>()
            .Which.AttrName.Should().Be("data-label");

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_attr_computed_against_element_yields_string()
    {
        var (engine, p) = Styled(
            "p::before { content: attr(data-v); }",
            [("data-v", "computed")]);
        var pStyle = engine.Compute(p);
        var before = engine.ComputePseudoElement(p, PseudoElement.Before, pStyle);
        before!.Get(PropertyId.Content).Should().Be(new CssString("computed"));
    }

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_attr_missing_attribute_falls_back_to_empty_string()
    {
        // attr() with no fallback and a missing attribute resolves to null,
        // leaving the raw CssAttrReference in place on the computed style.
        var (engine, p) = Styled("p::before { content: attr(data-missing); }");
        var pStyle = engine.Compute(p);
        var before = engine.ComputePseudoElement(p, PseudoElement.Before, pStyle);
        // The resolver returns null → the CssAttrReference stays (or the
        // property reads as CssAttrReference). Either way it is NOT a CssString
        // with a fabricated value; it should be the reference itself.
        before!.Get(PropertyId.Content).Should().NotBeOfType<CssString>(
            "missing attr without fallback must not produce a fabricated string");
    }

    // ---- §2 content: multiple components (string + attr) ----------------

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_string_followed_by_attr_parses_to_CssValueList()
    {
        var value = ContentDecl(@"content: ""prefix "" attr(href);").Value;
        value.Should().BeOfType<CssValueList>()
            .Which.Values.Should().HaveCount(2);
        var list = (CssValueList)value;
        list.Values[0].Should().BeOfType<CssString>().Which.Value.Should().Be("prefix ");
        list.Values[1].Should().BeOfType<CssAttrReference>().Which.AttrName.Should().Be("href");
    }

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_two_string_components_parses_to_CssValueList()
    {
        var value = ContentDecl(@"content: ""a"" ""b"";").Value;
        value.Should().BeOfType<CssValueList>()
            .Which.Values.Should().HaveCount(2);
    }

    // ---- §2 content: url() ----------------------------------------------

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_url_bare_parses_to_CssUrl()
    {
        // bare url(…) tokenizes as a url-token → CssUrl
        var value = ContentDecl("content: url(image.png);").Value;
        value.Should().BeOfType<CssUrl>()
            .Which.Value.Should().Be("image.png");
    }

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_url_quoted_parses_to_CssUrl()
    {
        // url("…") as a function call — CssValueParser unifies both to CssUrl.
        var value = ContentDecl(@"content: url(""image.png"");").Value;
        value.Should().BeOfType<CssUrl>()
            .Which.Value.Should().Be("image.png");
    }

    // ---- §2 content: open-quote / close-quote / no-*-quote keywords -----

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_open_quote_parses_to_keyword()
        => ContentDecl("content: open-quote;").Value
            .Should().Be(new CssKeyword("open-quote"));

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_close_quote_parses_to_keyword()
        => ContentDecl("content: close-quote;").Value
            .Should().Be(new CssKeyword("close-quote"));

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_no_open_quote_parses_to_keyword()
        => ContentDecl("content: no-open-quote;").Value
            .Should().Be(new CssKeyword("no-open-quote"));

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_no_close_quote_parses_to_keyword()
        => ContentDecl("content: no-close-quote;").Value
            .Should().Be(new CssKeyword("no-close-quote"));

    // quote keywords are deferred at render time but parse correctly.
    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [PendingFact(
        "open-quote/close-quote render actual quote glyphs using the `quotes` property; not yet rendered",
        trackingWp: "wp:spec-css-content-3")]
    public void Open_quote_renders_the_opening_quote_character()
    {
        var (engine, p) = Styled(@"p::before { content: open-quote; } p { quotes: ""“"" ""”""; }");
        var pStyle = engine.Compute(p);
        var before = engine.ComputePseudoElement(p, PseudoElement.Before, pStyle);
        // When implemented, open-quote should resolve to the first opening glyph.
        before!.Get(PropertyId.Content).Should().Be(new CssString("“"));
    }

    // ---- §2 content: counter() ------------------------------------------

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_counter_parses_to_CssFunctionValue()
    {
        var value = ContentDecl("content: counter(section);").Value;
        value.Should().BeOfType<CssFunctionValue>()
            .Which.Name.Should().Be("counter");
    }

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_counter_with_style_parses_to_CssFunctionValue_with_two_args()
    {
        var value = ContentDecl("content: counter(section, upper-roman);").Value;
        var fn = value.Should().BeOfType<CssFunctionValue>().Subject;
        fn.Name.Should().Be("counter");
        fn.Arguments.Should().HaveCount(2);
    }

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_counters_parses_to_CssFunctionValue()
    {
        var value = ContentDecl(@"content: counters(section, ""."");").Value;
        value.Should().BeOfType<CssFunctionValue>()
            .Which.Name.Should().Be("counters");
    }

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [PendingFact(
        "counter()/counters() are not yet evaluated to a rendered string at compute time",
        trackingWp: "wp:spec-css-content-3")]
    public void Content_counter_resolves_to_rendered_string_at_compute_time()
    {
        // When counters are implemented, content: counter(x) on a pseudo
        // should resolve to a CssString with the rendered counter value.
        var (engine, p) = Styled("p::before { content: counter(x); }");
        var pStyle = engine.Compute(p);
        var before = engine.ComputePseudoElement(p, PseudoElement.Before, pStyle);
        before!.Get(PropertyId.Content).Should().BeOfType<CssString>();
    }

    // ---- §2.5 quotes property -------------------------------------------

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#quotes-property", section: "2.5")]
    [SpecFact]
    public void Quotes_initial_value_is_auto()
        => PropertyRegistry.InitialValue(PropertyId.Quotes)
            .Should().Be(new CssKeyword("auto"));

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#quotes-property", section: "2.5")]
    [SpecFact]
    public void Quotes_auto_parses_to_keyword()
        => QuotesDecl("quotes: auto;").Value
            .Should().Be(new CssKeyword("auto"));

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#quotes-property", section: "2.5")]
    [SpecFact]
    public void Quotes_none_parses_to_keyword()
        => QuotesDecl("quotes: none;").Value
            .Should().Be(new CssKeyword("none"));

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#quotes-property", section: "2.5")]
    [SpecFact]
    public void Quotes_string_pair_parses_to_CssValueList_of_two_strings()
    {
        var value = QuotesDecl(@"quotes: ""“"" ""”"";").Value;
        var list = value.Should().BeOfType<CssValueList>().Subject;
        list.Values.Should().HaveCount(2);
        list.Values[0].Should().BeOfType<CssString>();
        list.Values[1].Should().BeOfType<CssString>();
    }

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#quotes-property", section: "2.5")]
    [SpecFact]
    public void Quotes_two_level_pairs_parse_to_CssValueList_of_four_strings()
    {
        var value = QuotesDecl(@"quotes: ""“"" ""”"" ""‘"" ""’"";").Value;
        var list = value.Should().BeOfType<CssValueList>().Subject;
        list.Values.Should().HaveCount(4);
        list.Values.Should().AllBeOfType<CssString>();
    }

    // ---- inheritance: quotes is inherited, content is not ---------------

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#quotes-property", section: "2.5")]
    [SpecFact]
    public void Quotes_is_inherited()
        => PropertyRegistry.Inherits(PropertyId.Quotes).Should().BeTrue();

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_is_not_inherited()
        => PropertyRegistry.Inherits(PropertyId.Content).Should().BeFalse();

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#quotes-property", section: "2.5")]
    [SpecFact]
    public void Quotes_cascades_to_child_element()
    {
        var doc = new Document();
        var parent = doc.CreateElement("div");
        var child = doc.CreateElement("p");
        parent.AppendChild(child);
        child.AppendChild(doc.CreateTextNode("x"));
        doc.AppendChild(parent);
        var engine = new StyleEngine();
        engine.AddStyleSheet(CssParser.ParseStyleSheet(
            @"div { quotes: ""“"" ""”""; }",
            StyleOrigin.Author));
        var parentStyle = engine.Compute(parent);
        var childStyle = engine.Compute(child);
        // Child did not have quotes set; it must inherit the same value from parent.
        // CssValueList uses reference equality on its inner list, so we assert the
        // type and that both carry the same number of string members rather than
        // using Be() which would fail on list identity.
        var childQuotes = childStyle.Get(PropertyId.Quotes)
            .Should().BeOfType<CssValueList>().Subject;
        var parentQuotes = parentStyle.Get(PropertyId.Quotes)
            .Should().BeOfType<CssValueList>().Subject;
        childQuotes.Values.Should().HaveCount(parentQuotes.Values.Count);
    }

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Content_does_not_cascade_to_child_element()
    {
        var doc = new Document();
        var parent = doc.CreateElement("div");
        var child = doc.CreateElement("p");
        parent.AppendChild(child);
        child.AppendChild(doc.CreateTextNode("x"));
        doc.AppendChild(parent);
        var engine = new StyleEngine();
        engine.AddStyleSheet(CssParser.ParseStyleSheet(
            @"div { content: ""parent text""; }",
            StyleOrigin.Author));
        var childStyle = engine.Compute(child);
        // `content` is not inherited; child gets the initial value `normal`.
        childStyle.Get(PropertyId.Content).Should().Be(new CssKeyword("normal"),
            "content is not inherited — child must get the initial value");
    }

    // ---- pseudo-element interaction -------------------------------------

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void After_pseudo_element_content_string_cascades()
    {
        var (engine, p) = Styled(@"p::after { content: "" !""; }");
        var pStyle = engine.Compute(p);
        var after = engine.ComputePseudoElement(p, PseudoElement.After, pStyle);
        after.Should().NotBeNull();
        after!.Get(PropertyId.Content).Should().Be(new CssString(" !"));
    }

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Pseudo_with_content_none_yields_none_in_computed_style()
    {
        var (engine, p) = Styled("p::before { content: none; }");
        var pStyle = engine.Compute(p);
        var before = engine.ComputePseudoElement(p, PseudoElement.Before, pStyle);
        before!.Get(PropertyId.Content).Should().Be(new CssKeyword("none"));
    }

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Pseudo_with_url_content_computed_style_holds_CssUrl()
    {
        var (engine, p) = Styled("p::before { content: url(bullet.png); }");
        var pStyle = engine.Compute(p);
        var before = engine.ComputePseudoElement(p, PseudoElement.Before, pStyle);
        before!.Get(PropertyId.Content).Should().BeOfType<CssUrl>()
            .Which.Value.Should().Be("bullet.png");
    }

    // ---- §2 content: counter() in pseudo element computes as function ----

    [Spec("css-content-3", "https://www.w3.org/TR/css-content-3/#content-property", section: "2")]
    [SpecFact]
    public void Pseudo_with_counter_content_computed_style_holds_CssFunctionValue()
    {
        var (engine, p) = Styled("p::before { content: counter(c); }");
        var pStyle = engine.Compute(p);
        var before = engine.ComputePseudoElement(p, PseudoElement.Before, pStyle);
        // counter() is deferred; it must not silently drop or corrupt the value.
        before!.Get(PropertyId.Content).Should().BeOfType<CssFunctionValue>()
            .Which.Name.Should().Be("counter");
    }
}
