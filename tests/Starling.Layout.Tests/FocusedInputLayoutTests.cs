using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
namespace Starling.Layout.Tests;

/// <summary>
/// Verifies the editable-input layout hooks: the live <c>Element.InputValue</c>
/// (typed text / scripted assignment) renders in place of the <c>value</c>
/// content attribute, the placeholder survives focus (it only clears once the
/// user types), and an empty text field keeps one line box of height instead of
/// collapsing — all prerequisites for click-to-type that doesn't jump the box.
/// </summary>
[TestClass]
public sealed class FocusedInputLayoutTests
{
    private static BlockBox Layout(Starling.Dom.Document doc, Size viewport)
        => new LayoutEngine(new StyleEngine()).LayoutDocument(doc, viewport);

    [TestMethod]
    public void Live_input_value_renders_over_the_value_attribute()
    {
        var doc = HtmlParser.Parse("<body><input type=\"text\" value=\"old\"></body>");
        var input = doc.GetElementsByTagName("input")[0];
        input.InputValue = "typed";

        var labels = Texts(FindBox(Layout(doc, new Size(800, 600)), "input")!);
        labels.Should().Contain("typed");
        labels.Should().NotContain("old");
    }

    [TestMethod]
    public void Focused_empty_input_keeps_its_placeholder()
    {
        // The placeholder clears only once the user types — focusing the field
        // must not drop it, or the box visibly changes the moment it's clicked.
        var doc = HtmlParser.Parse("<body><input type=\"text\" placeholder=\"search\"></body>");
        doc.FocusedElement = doc.GetElementsByTagName("input")[0];

        Texts(FindBox(Layout(doc, new Size(800, 600)), "input")!)
            .Should().Contain("search");
    }

    [TestMethod]
    public void Unfocused_empty_input_keeps_its_placeholder()
    {
        var doc = HtmlParser.Parse("<body><input type=\"text\" placeholder=\"search\"></body>");

        Texts(FindBox(Layout(doc, new Size(800, 600)), "input")!)
            .Should().Contain("search");
    }

    [TestMethod]
    public void Typed_value_clears_the_placeholder()
    {
        var doc = HtmlParser.Parse("<body><input type=\"text\" placeholder=\"search\"></body>");
        var input = doc.GetElementsByTagName("input")[0];
        doc.FocusedElement = input;
        input.InputValue = "hi";

        var labels = Texts(FindBox(Layout(doc, new Size(800, 600)), "input")!);
        labels.Should().Contain("hi");
        labels.Should().NotContain("search");
    }

    [TestMethod]
    public void Empty_text_input_keeps_its_line_height_on_focus()
    {
        // An empty text field (no value, no placeholder) reserves one line box,
        // so focusing it does not collapse the control to padding+border. The
        // box height must be the same focused and unfocused.
        const string html = "<body><input type=\"text\" style=\"font: 16px sans-serif; padding:0; border:0\"></body>";

        var unfocused = HtmlParser.Parse(html);
        var unfocusedHeight = FindBox(Layout(unfocused, new Size(800, 600)), "input")!.Frame.Height;

        var focused = HtmlParser.Parse(html);
        focused.FocusedElement = focused.GetElementsByTagName("input")[0];
        var focusedHeight = FindBox(Layout(focused, new Size(800, 600)), "input")!.Frame.Height;

        unfocusedHeight.Should().BeGreaterThan(0);
        focusedHeight.Should().BeApproximately(unfocusedHeight, 0.5);
    }

    // ---------------------------------------------------------------- helpers

    private static List<string> Texts(Box.Box box) => FlattenTextBoxes(box).Select(tb => tb.Text).ToList();

    private static Box.Box? FindBox(Box.Box root, string localName)
    {
        if (root.Element?.LocalName == localName)
        {
            return root;
        }

        foreach (var child in root.Children)
        {
            var hit = FindBox(child, localName);
            if (hit is not null)
            {
                return hit;
            }
        }
        return null;
    }

    private static IEnumerable<TextBox> FlattenTextBoxes(Box.Box box)
    {
        if (box is TextBox tb) { yield return tb; yield break; }
        foreach (var child in box.Children)
        {
            foreach (var inner in FlattenTextBoxes(child))
            {
                yield return inner;
            }
        }
    }
}
