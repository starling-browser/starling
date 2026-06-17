using AwesomeAssertions;
using Starling.Css.Cascade;
using Starling.Html;
using Starling.Layout.Box;
namespace Starling.Layout.Tests;

/// <summary>
/// Verifies that legacy form controls (&lt;input&gt;, &lt;button&gt;) render as
/// visible, sized boxes — Google's homepage and similar legacy forms rely on
/// this. Don't check pixel-perfect dimensions; just that the controls are
/// non-degenerate and that user-supplied labels/values land in the box tree.
/// </summary>
[TestClass]
public sealed class FormControlLayoutTests
{
    private static LayoutEngine NewEngine() => new(new StyleEngine());

    private static BlockBox Layout(string html, Size viewport)
        => NewEngine().LayoutDocument(HtmlParser.Parse(html), viewport);

    [TestMethod]
    public void Text_input_with_no_attributes_has_visible_width_and_height()
    {
        var root = Layout("<body><input type=\"text\"></body>", new Size(800, 600));
        var input = FindBox(root, "input");
        input.Should().NotBeNull();
        // Default HTML size=20 → ~10 char-widths at 13px font ≈ 70+ px.
        input!.Frame.Width.Should().BeGreaterThan(40);
        input.Frame.Height.Should().BeGreaterThan(10);
    }

    [TestMethod]
    public void Larger_size_attribute_widens_the_input()
    {
        var rootDefault = Layout("<body><input type=\"text\"></body>", new Size(2000, 600));
        var rootBig = Layout("<body><input type=\"text\" size=\"40\"></body>", new Size(2000, 600));
        var defaultInput = FindBox(rootDefault, "input")!;
        var bigInput = FindBox(rootBig, "input")!;
        // size=40 should be visibly wider than the default size=20.
        bigInput.Frame.Width.Should().BeGreaterThan(defaultInput.Frame.Width * 1.5);
    }

    [TestMethod]
    public void Text_input_with_value_renders_the_value_as_content()
    {
        var root = Layout("<body><input type=\"text\" value=\"hello\"></body>", new Size(800, 600));
        var input = FindBox(root, "input")!;
        var text = FlattenTextBoxes(input).Select(tb => tb.Text).ToList();
        text.Should().Contain("hello");
    }

    [TestMethod]
    public void Text_input_with_placeholder_renders_the_placeholder_when_no_value()
    {
        var root = Layout(
            "<body><input type=\"text\" placeholder=\"search\"></body>",
            new Size(800, 600));
        var input = FindBox(root, "input")!;
        var text = FlattenTextBoxes(input).Select(tb => tb.Text).ToList();
        text.Should().Contain("search");
    }

    [TestMethod]
    public void Submit_input_with_value_renders_the_value_as_label()
    {
        var root = Layout("<body><input type=\"submit\" value=\"Go\"></body>", new Size(800, 600));
        var input = FindBox(root, "input")!;
        var text = FlattenTextBoxes(input).Select(tb => tb.Text).ToList();
        text.Should().Contain("Go");
        // The button itself must be visible.
        input.Frame.Width.Should().BeGreaterThan(0);
        input.Frame.Height.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void Button_element_renders_its_child_text_as_label()
    {
        var root = Layout("<body><button>Click</button></body>", new Size(800, 600));
        var button = FindBox(root, "button")!;
        var text = FlattenTextBoxes(button).Select(tb => tb.Text).ToList();
        text.Should().Contain("Click");
        button.Frame.Width.Should().BeGreaterThan(0);
        button.Frame.Height.Should().BeGreaterThan(0);
    }

    [TestMethod]
    public void Submit_input_without_value_renders_default_submit_label()
    {
        var root = Layout("<body><input type=\"submit\"></body>", new Size(800, 600));
        var input = FindBox(root, "input")!;
        var text = FlattenTextBoxes(input).Select(tb => tb.Text).ToList();
        // Default label per HTML — we emit "Submit" when no value attribute.
        text.Should().Contain("Submit");
    }

    [TestMethod]
    public void Googlelike_form_lays_out_all_three_controls_visibly()
    {
        // Mirrors the legacy Google homepage shape: one text input + two submits.
        const string html = """
            <body>
              <form>
                <input maxlength="2048" name="q" type="text">
                <input value="Google Search" name="btnK" type="submit">
                <input value="I'm Feeling Lucky" name="btnI" type="submit">
              </form>
            </body>
            """;
        var root = Layout(html, new Size(1024, 768));
        var inputs = AllBoxes(root).Where(b => b.Element?.LocalName == "input").ToList();
        inputs.Should().HaveCount(3);
        foreach (var input in inputs)
        {
            input.Frame.Width.Should().BeGreaterThan(0);
            input.Frame.Height.Should().BeGreaterThan(0);
        }

        var labels = inputs
            .SelectMany(FlattenTextBoxes)
            .Select(tb => tb.Text)
            .ToList();
        labels.Should().Contain("Google Search");
        labels.Should().Contain("I'm Feeling Lucky");
    }

    [TestMethod]
    public void Textarea_renders_live_value_or_placeholder()
    {
        var document = HtmlParser.Parse("<body><textarea id=\"message\" placeholder=\"Message\"></textarea></body>");
        var textarea = document.GetElementById("message")!;
        textarea.InputValue = "hello";

        var root = NewEngine().LayoutDocument(document, new Size(800, 600));
        var box = FindBox(root, "textarea")!;
        FlattenTextBoxes(box).Select(tb => tb.Text).Should().Contain("hello");
    }

    [TestMethod]
    public void Select_renders_the_selected_option_label()
    {
        var root = Layout(
            "<body><select><option value=\"ground\">Ground</option><option value=\"air\" selected>Air</option></select></body>",
            new Size(800, 600));
        var select = FindBox(root, "select")!;

        FlattenTextBoxes(select).Select(tb => tb.Text).Should().Contain("Air");
        FlattenTextBoxes(select).Select(tb => tb.Text).Should().NotContain("Ground");
    }

    // ---------------------------------------------------------------- helpers

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

    private static IEnumerable<Box.Box> AllBoxes(Box.Box root)
    {
        yield return root;
        foreach (var child in root.Children)
        {
            foreach (var b in AllBoxes(child))
            {
                yield return b;
            }
        }
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
