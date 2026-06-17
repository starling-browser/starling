// SPDX-License-Identifier: Apache-2.0
using AwesomeAssertions;
using Starling.Dom;

namespace Starling.Html.AngleSharp.Tests;

/// <summary>
/// Differential and fidelity tests for <see cref="AngleSharpHtmlBackend"/>. The
/// equality tests parse well-formed markup (inside the common subset of both
/// parsers) through the Starling backend and the AngleSharp backend, then assert
/// the serialized Starling DOM matches. The fidelity tests use richer markup and
/// assert the AngleSharp-backed tree faithfully reflects AngleSharp's own tree.
/// </summary>
[TestClass]
public sealed class AngleSharpBackendTests
{
    private static readonly StarlingHtmlBackend Starling = new();
    private static readonly AngleSharpHtmlBackend Angle = new();

    // Serializes a full parsed document by walking its top-level children
    // (doctype + <html> + stray comments), matching the document shape both
    // backends produce.
    private static string SerializeDocument(Document doc)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var child in doc.ChildNodes)
        {
            sb.Append(HtmlSerializer.SerializeNode(child));
        }

        return sb.ToString();
    }

    private static void AssertSameDocument(string html)
    {
        var starling = SerializeDocument(Starling.Parse(html, scriptingEnabled: false));
        var angle = SerializeDocument(Angle.Parse(html, scriptingEnabled: false));
        angle.Should().Be(starling);
    }

    // ---- Differential equality (common subset) ----

    [TestMethod]
    public void Basic_document_matches()
        => AssertSameDocument("<html><head><title>Hi</title></head><body><p>Hello</p></body></html>");

    [TestMethod]
    public void Nested_elements_and_attributes_match()
        => AssertSameDocument(
            "<div id=\"a\" class=\"x y\"><span data-k=\"v\">t</span><a href=\"/u\">link</a></div>");

    [TestMethod]
    public void Comments_match()
        => AssertSameDocument("<div><!-- a comment -->text<!--another--></div>");

    [TestMethod]
    public void Doctype_matches()
        => AssertSameDocument("<!DOCTYPE html><html><head></head><body>hi</body></html>");

    [TestMethod]
    public void Svg_with_namespaced_attribute_matches()
        => AssertSameDocument("<svg><a xlink:href=\"x\"></a></svg>");

    // ---- Adapter fidelity (richer markup) ----

    [TestMethod]
    public void Svg_preserves_namespace_case_and_xlink_attribute()
    {
        var doc = Angle.Parse("<svg><linearGradient id=\"g\"><a xlink:href=\"x\"></a></linearGradient></svg>", false);

        var svg = doc.GetElementsByTagNameNS("http://www.w3.org/2000/svg", "svg").FirstOrDefault();
        svg.Should().NotBeNull();
        svg!.Namespace.Should().Be("http://www.w3.org/2000/svg");

        var gradient = doc.GetElementsByTagNameNS("http://www.w3.org/2000/svg", "linearGradient").FirstOrDefault();
        gradient.Should().NotBeNull("SVG element names keep their camelCase");
        gradient!.LocalName.Should().Be("linearGradient");

        var anchor = doc.GetElementsByTagNameNS("http://www.w3.org/2000/svg", "a").FirstOrDefault();
        anchor.Should().NotBeNull();
        anchor!.GetAttributeNS("http://www.w3.org/1999/xlink", "href").Should().Be("x");
    }

    [TestMethod]
    public void MathMl_element_lands_in_mathml_namespace()
    {
        var doc = Angle.Parse("<math><mi>x</mi></math>", false);

        var math = doc.GetElementsByTagNameNS("http://www.w3.org/1998/Math/MathML", "math").FirstOrDefault();
        math.Should().NotBeNull();
        var mi = doc.GetElementsByTagNameNS("http://www.w3.org/1998/Math/MathML", "mi").FirstOrDefault();
        mi.Should().NotBeNull();
        mi!.Namespace.Should().Be("http://www.w3.org/1998/Math/MathML");
    }

    [TestMethod]
    public void Html_element_names_and_attributes_are_lowercased()
    {
        var doc = Angle.Parse("<DIV ID=\"a\" Data-X=\"v\">t</DIV>", false);

        var div = doc.GetElementsByTagName("div").FirstOrDefault();
        div.Should().NotBeNull();
        div!.TagName.Should().Be("div");
        div.LocalName.Should().Be("div");
        div.GetAttribute("id").Should().Be("a");
        div.GetAttribute("data-x").Should().Be("v");
    }

    [TestMethod]
    public void Template_content_lands_in_content_fragment_not_children()
    {
        var doc = Angle.Parse("<template><p>inside</p></template>", false);

        var template = doc.GetElementsByTagName("template").FirstOrDefault();
        template.Should().NotBeNull();
        template.Should().BeOfType<HtmlTemplateElement>();

        var tmpl = (HtmlTemplateElement)template!;
        // Template children live in the content fragment, not as normal children.
        tmpl.ChildNodes.Should().BeEmpty();
        tmpl.Content.ChildNodes.Should().ContainSingle();
        var p = tmpl.Content.FirstChild as Element;
        p.Should().NotBeNull();
        p!.TagName.Should().Be("p");
        HtmlSerializer.SerializeChildren(tmpl.Content).Should().Be("<p>inside</p>");
    }

    [TestMethod]
    public void Doctype_node_is_copied_with_name()
    {
        var doc = Angle.Parse("<!DOCTYPE html><html><body>hi</body></html>", false);

        var doctype = doc.ChildNodes.OfType<DocumentType>().FirstOrDefault();
        doctype.Should().NotBeNull();
        doctype!.Name.Should().Be("html");
    }

    // ---- Fragment parsing (innerHTML-style) ----

    [TestMethod]
    public void Fragment_in_div_context_matches()
    {
        var owner = new Document();
        var context = owner.CreateElement("div");

        var starling = HtmlSerializer.SerializeChildren(
            Starling.ParseFragment("<p>hi <b>there</b></p>", context, owner));
        var angle = HtmlSerializer.SerializeChildren(
            Angle.ParseFragment("<p>hi <b>there</b></p>", context, owner));

        angle.Should().Be(starling);
    }

    [TestMethod]
    public void Fragment_in_table_row_context_reflects_anglesharp_tree()
    {
        var owner = new Document();
        var context = owner.CreateElement("tr");

        // A <tr> context coerces a bare <td> into the row. Assert the adapter
        // surfaces AngleSharp's parsed cell, owned by the right document.
        var fragment = Angle.ParseFragment("<td>cell</td>", context, owner);

        var td = fragment.ChildNodes.OfType<Element>().FirstOrDefault();
        td.Should().NotBeNull();
        td!.TagName.Should().Be("td");
        td.OwnerDocument.Should().BeSameAs(owner);
        HtmlSerializer.SerializeChildren(td).Should().Be("cell");
    }

    [TestMethod]
    public void Fragment_in_table_context_parses_rows()
    {
        var owner = new Document();
        var context = owner.CreateElement("table");

        var fragment = Angle.ParseFragment("<tr><td>a</td></tr>", context, owner);

        // AngleSharp wraps loose rows in a <tbody> per the table insertion mode.
        var tbody = fragment.ChildNodes.OfType<Element>().FirstOrDefault(e => e.TagName == "tbody");
        tbody.Should().NotBeNull("AngleSharp inserts an implied <tbody>");
        var td = tbody!.GetElementsByTagNameNS(Element.HtmlNamespace, "td").FirstOrDefault();
        td.Should().NotBeNull();
        HtmlSerializer.SerializeChildren(td!).Should().Be("a");
    }
}
