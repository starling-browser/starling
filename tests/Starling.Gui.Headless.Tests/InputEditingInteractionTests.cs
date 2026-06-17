using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Engine;
using Starling.Gui.Controls;
using Starling.Gui.Theme;
using AvPoint = Avalonia.Point;
using EngineSize = SixLabors.ImageSharp.Size;

namespace Starling.Gui.Headless.Tests;

/// <summary>
/// End-to-end coverage for click-to-focus + type in a real <see cref="WebviewPanel"/>:
/// a click lands focus on a text <c>&lt;input&gt;</c> and subsequent keystrokes
/// mutate its value — the pipeline that was previously missing entirely (clicks
/// only drove links/selection; keys only drove the browser chrome).
/// </summary>
public class InputEditingInteractionTests
{
    [AvaloniaFact]
    public async Task Clicking_a_text_input_and_typing_updates_its_value()
    {
        var (window, panel, page, input) = await ShowPageWithInputAsync();

        ClickDoc(window, panel, CenterOf(page.Root, input)!.Value);
        page.Document.FocusedElement.Should().BeSameAs(input, "the click should focus the field");

        window.KeyTextInput("hello");

        input.InputValue.Should().Be("hello",
            "typing into a focused text input must append to its value");
    }

    [AvaloniaFact]
    public async Task Backspace_deletes_before_the_caret()
    {
        var (window, panel, page, input) = await ShowPageWithInputAsync();

        ClickDoc(window, panel, CenterOf(page.Root, input)!.Value);
        window.KeyTextInput("abc");
        window.KeyPressQwerty(PhysicalKey.Backspace, RawInputModifiers.None);

        input.InputValue.Should().Be("ab");
    }

    [AvaloniaFact]
    public async Task Clicking_outside_a_focused_input_blurs_it()
    {
        var (window, panel, page, input) = await ShowPageWithInputAsync();
        var center = CenterOf(page.Root, input)!.Value;

        ClickDoc(window, panel, center);
        page.Document.FocusedElement.Should().BeSameAs(input);

        // Click empty page area on the same row, well clear of the control.
        ClickDoc(window, panel, (400, center.Y));
        page.Document.FocusedElement.Should().BeNull("clicking off the field clears focus");
    }

    [AvaloniaFact]
    public async Task Clearing_a_focused_field_keeps_the_caret_at_the_origin()
    {
        // Reproduces the to-do submit bug: after the field's value is cleared
        // (the page's submit handler resets it) the empty field shows its
        // placeholder again, and the caret must snap back to the content origin
        // — not stay measured into the placeholder glyphs at the stale index,
        // which stranded it in the middle of "What needs doing?".
        var fixture = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"starling-caret-{Guid.NewGuid():N}.html");
        System.IO.File.WriteAllText(fixture,
            "<!doctype html><html><body><input type=\"text\" id=\"q\" placeholder=\"What needs doing?\"></body></html>");

        var engine = new StarlingEngine();
        var opts = new RenderOptions(new EngineSize(800, 600), FontSize: 16f);
        var result = await engine.LayoutPageAsync(
            "file://" + fixture.Replace('\\', '/'), opts, CancellationToken.None);
        result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");

        var page = result.Value;
        var input = page.Document.GetElementById("q")!;

        // Focus the empty field and re-lay-out so the focused box tree (with the
        // placeholder) is current — this is the state right after a submit clear.
        page.Document.FocusedElement = input;
        var laid = engine.RelayoutPage(page, opts)!;

        var atOrigin = ComputeCaretX(laid.Root, input, caretIndex: 0);
        var atStaleIndex = ComputeCaretX(laid.Root, input, caretIndex: 8);

        atOrigin.Should().NotBeNull("the focused empty field still has a caret");
        atStaleIndex.Should().BeApproximately(atOrigin!.Value, 0.5,
            "an empty field's caret stays at the content origin for any index — never inside the placeholder");
    }

    /// <summary>X of the caret rect <see cref="WebviewPanel"/> would draw for
    /// <paramref name="caretIndex"/> inside <paramref name="input"/>, via the
    /// private static <c>ComputeCaretRect</c>; null when no rect is produced.</summary>
    private static double? ComputeCaretX(
        Starling.Layout.Box.BlockBox root, Starling.Dom.Element input, int caretIndex)
    {
        var method = typeof(WebviewPanel).GetMethod(
            "ComputeCaretRect", BindingFlags.NonPublic | BindingFlags.Static)!;
        var rect = method.Invoke(null, new object?[] { root, input, caretIndex });
        return rect is null ? null : (double)rect.GetType().GetField("Item1")!.GetValue(rect)!;
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<(Window Window, WebviewPanel Panel, LaidOutPage Page, Starling.Dom.Element Input)>
        ShowPageWithInputAsync()
    {
        GpuTests.SkipUnlessAvailable();
        var (engine, page, input) = await BuildPageWithInputAsync();
        var panel = new WebviewPanel(
            new ThemeManager(), NullLoggerFactory.Instance, _ => { }, (_, _) => { },
            (p, vp) => engine.RelayoutPage(p, new RenderOptions(vp, FontSize: 16f)));

        var window = new Window { Content = panel, Width = 800, Height = 600 };
        window.Show();
        panel.ShowPage(page);
        window.CaptureRenderedFrame(); // force a measure/arrange so the canvas has bounds
        return (window, panel, page, input);
    }

    private static async Task<(StarlingEngine Engine, LaidOutPage Page, Starling.Dom.Element Input)>
        BuildPageWithInputAsync()
    {
        var fixture = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"starling-input-{Guid.NewGuid():N}.html");
        System.IO.File.WriteAllText(fixture,
            "<!doctype html><html><body><input type=\"text\" id=\"q\"></body></html>");

        var engine = new StarlingEngine();
        var result = await engine.LayoutPageAsync(
            "file://" + fixture.Replace('\\', '/'),
            new RenderOptions(new EngineSize(800, 600), FontSize: 16f),
            CancellationToken.None);
        result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");

        var page = result.Value;
        var input = page.Document.GetElementById("q")!;
        return (engine, page, input);
    }

    /// <summary>Presses and releases the left mouse button at a document-space
    /// point, translating it into the window's coordinate space (the page canvas
    /// may be offset within the scroll viewport — e.g. vertically centred when
    /// the page is shorter than the viewport).</summary>
    private static void ClickDoc(Window window, WebviewPanel panel, (double X, double Y) doc)
    {
        var canvas = (Control)typeof(WebviewPanel)
            .GetField("_pageCanvas", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(panel)!;
        var winPt = canvas.TranslatePoint(new AvPoint(doc.X, doc.Y), window)!.Value;
        window.MouseDown(winPt, MouseButton.Left);
        window.MouseUp(winPt, MouseButton.Left);
    }

    /// <summary>Absolute (document-space) centre of <paramref name="target"/>'s
    /// box, accumulating content-area offsets like the panel's hit-tester.</summary>
    private static (double X, double Y)? CenterOf(
        Starling.Layout.Box.Box box, Starling.Dom.Element target, double originX = 0, double originY = 0)
    {
        var fx = originX + box.Frame.X;
        var fy = originY + box.Frame.Y;
        if (ReferenceEquals(box.Element, target))
        {
            return (fx + box.Frame.Width / 2, fy + box.Frame.Height / 2);
        }

        if (box is Starling.Layout.Box.TextBox)
        {
            return null;
        }

        var cx = fx + box.Border.Left + box.Padding.Left;
        var cy = fy + box.Border.Top + box.Padding.Top;
        foreach (var child in box.Children)
        {
            if (CenterOf(child, target, cx, cy) is { } r)
            {
                return r;
            }
        }

        return null;
    }
}
