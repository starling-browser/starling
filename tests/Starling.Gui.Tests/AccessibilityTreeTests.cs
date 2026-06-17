using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Dom;
using Starling.Gui.Core.Accessibility;
using Starling.Html;
using Starling.Layout;

namespace Starling.Gui.Tests;

/// <summary>
/// Unit coverage for the managed accessibility tree
/// (<see cref="AccessibilityTreeBuilder"/>): roles, accessible names, values,
/// document-coordinate bounds, focus, and the flattening of non-semantic boxes.
/// Engine-agnostic — no shell or platform bridge involved.
/// </summary>
[TestClass]
public sealed class AccessibilityTreeTests
{
    private static (AccessibilityNode Tree, Document Doc) Build(string html)
    {
        var doc = HtmlParser.Parse(html);
        var root = new LayoutEngine(new StyleEngine()).LayoutDocument(doc, new Size(800, 600));
        return (AccessibilityTreeBuilder.Build(root, doc), doc);
    }

    private static IEnumerable<AccessibilityNode> Flatten(AccessibilityNode node)
    {
        yield return node;
        foreach (var c in node.Children)
        {
            foreach (var n in Flatten(c))
            {
                yield return n;
            }
        }
    }

    private static IEnumerable<AccessibilityNode> Nodes(string html)
        => Flatten(Build(html).Tree);

    [TestMethod]
    public void Root_is_a_document_node()
    {
        var (tree, _) = Build("<title>Hi</title><body><p>x</p></body>");
        tree.Role.Should().Be(AccessibilityRole.Document);
        tree.Name.Should().Be("Hi");
    }

    [TestMethod]
    public void Heading_has_role_name_and_level()
    {
        var h = Nodes("<body><h2>Section Title</h2></body>")
            .Single(n => n.Role == AccessibilityRole.Heading);
        h.Name.Should().Be("Section Title");
        h.HeadingLevel.Should().Be(2);
    }

    [TestMethod]
    public void Anchor_with_href_is_a_link_named_by_its_text()
    {
        var link = Nodes("<body><a href=\"/next\">Go to next page</a></body>")
            .Single(n => n.Role == AccessibilityRole.Link);
        link.Name.Should().Be("Go to next page");
    }

    [TestMethod]
    public void Anchor_without_href_is_not_a_link()
    {
        Nodes("<body><a>no href</a></body>")
            .Should().NotContain(n => n.Role == AccessibilityRole.Link);
    }

    [TestMethod]
    public void Text_input_is_a_textfield_with_value_and_associated_label()
    {
        var field = Nodes(
            "<body><label for=\"q\">Search the site</label>" +
            "<input id=\"q\" type=\"text\" value=\"shoes\"></body>")
            .Single(n => n.Role == AccessibilityRole.TextField);
        field.Name.Should().Be("Search the site");
        field.Value.Should().Be("shoes");
    }

    [TestMethod]
    public void Text_input_falls_back_to_placeholder_name()
    {
        var field = Nodes("<body><input type=\"text\" placeholder=\"Email address\"></body>")
            .Single(n => n.Role == AccessibilityRole.TextField);
        field.Name.Should().Be("Email address");
    }

    [TestMethod]
    public void Checkbox_role_and_checked_state()
    {
        var box = Nodes("<body><input type=\"checkbox\" checked></body>")
            .Single(n => n.Role == AccessibilityRole.CheckBox);
        box.Checked.Should().BeTrue();
    }

    [TestMethod]
    public void Image_role_named_by_alt_or_title()
    {
        // A real <img> needs a loaded source to lay out a box (the shell's
        // BrowserSession supplies an image resolver). role="img" exercises the
        // same Image-role naming path (alt → title) with a box that always lays
        // out, so the test does not depend on image decoding.
        var img = Nodes("<body><div role=\"img\" title=\"A red square\" style=\"width:10px;height:10px\"></div></body>")
            .Single(n => n.Role == AccessibilityRole.Image);
        img.Name.Should().Be("A red square");
    }

    [TestMethod]
    public void Aria_label_overrides_text()
    {
        var link = Nodes("<body><a href=\"/\" aria-label=\"Home\">x</a></body>")
            .Single(n => n.Role == AccessibilityRole.Link);
        link.Name.Should().Be("Home");
    }

    [TestMethod]
    public void Role_attribute_overrides_tag()
    {
        // A div with role=button is exposed as a button.
        Nodes("<body><div role=\"button\">Click</div></body>")
            .Should().Contain(n => n.Role == AccessibilityRole.Button && n.Name == "Click");
    }

    [TestMethod]
    public void Non_semantic_boxes_are_flattened()
    {
        // The heading is several presentational divs deep but attaches to the document.
        var (tree, _) = Build("<body><div><div><div><h1>Deep</h1></div></div></div></body>");
        tree.Children.Should().ContainSingle(n => n.Role == AccessibilityRole.Heading);
    }

    [TestMethod]
    public void Focused_element_is_marked()
    {
        var doc = HtmlParser.Parse("<body><input id=\"q\" type=\"text\"></body>");
        var root = new LayoutEngine(new StyleEngine()).LayoutDocument(doc, new Size(800, 600));
        doc.FocusedElement = doc.GetElementById("q");

        var tree = AccessibilityTreeBuilder.Build(root, doc);
        Flatten(tree).Single(n => n.Role == AccessibilityRole.TextField)
            .Focused.Should().BeTrue();
    }

    [TestMethod]
    public void Bounds_are_document_coordinates()
    {
        // A heading pushed down by a tall spacer must report a positive Y.
        var h = Nodes("<body style=\"margin:0\"><div style=\"height:200px\"></div><h1>Below</h1></body>")
            .Single(n => n.Role == AccessibilityRole.Heading);
        h.Bounds.Y.Should().BeGreaterThanOrEqualTo(200);
        h.Bounds.Width.Should().BeGreaterThan(0);
        h.Bounds.Height.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void Article_and_aside_are_landmarks()
    {
        var nodes = Nodes("<body><article><p>post</p></article><aside><p>side</p></aside></body>").ToList();
        nodes.Should().Contain(n => n.Role == AccessibilityRole.Article);
        nodes.Should().Contain(n => n.Role == AccessibilityRole.Complementary);
    }

    [TestMethod]
    public void Section_is_a_region_only_when_named()
    {
        Nodes("<body><section aria-label=\"Promotions\"><p>x</p></section></body>")
            .Should().ContainSingle(n => n.Role == AccessibilityRole.Region && n.Name == "Promotions");

        // An unnamed section is presentational and flattens away.
        Nodes("<body><section><p>x</p></section></body>")
            .Should().NotContain(n => n.Role == AccessibilityRole.Region);
    }

    [TestMethod]
    public void Form_is_a_landmark_only_when_named()
    {
        Nodes("<body><form aria-label=\"Login\"><input type=\"text\"></form></body>")
            .Should().ContainSingle(n => n.Role == AccessibilityRole.Form && n.Name == "Login");

        Nodes("<body><form><input type=\"text\"></form></body>")
            .Should().NotContain(n => n.Role == AccessibilityRole.Form);
    }

    [TestMethod]
    public void Select_is_a_combobox_with_its_value()
    {
        var combo = Nodes(
            "<body><label for=\"s\">Country</label>" +
            "<select id=\"s\"><option value=\"us\">US</option>" +
            "<option value=\"ca\" selected>Canada</option></select></body>")
            .Single(n => n.Role == AccessibilityRole.ComboBox);
        combo.Name.Should().Be("Country");
        combo.Value.Should().Be("ca");
    }

    [TestMethod]
    public void Aria_labelledby_resolves_referenced_text()
    {
        var field = Nodes(
            "<body><span id=\"lbl\">Username</span>" +
            "<input aria-labelledby=\"lbl\" type=\"text\"></body>")
            .Single(n => n.Role == AccessibilityRole.TextField);
        field.Name.Should().Be("Username");
    }

    [TestMethod]
    public void Aria_labelledby_joins_multiple_references_in_order()
    {
        var field = Nodes(
            "<body><span id=\"a\">Billing</span><span id=\"b\">address</span>" +
            "<input aria-labelledby=\"a b\" type=\"text\"></body>")
            .Single(n => n.Role == AccessibilityRole.TextField);
        field.Name.Should().Be("Billing address");
    }

    [TestMethod]
    public void Button_with_no_text_falls_back_to_title()
    {
        var btn = Nodes("<body><button title=\"Save changes\"></button></body>")
            .Single(n => n.Role == AccessibilityRole.Button);
        btn.Name.Should().Be("Save changes");
    }
}
