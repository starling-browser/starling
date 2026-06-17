using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Starling.Engine;
using Starling.Gui.Controls;
using Starling.Gui.Theme;
using DomElement = Starling.Dom.Element;
using EngineSize = SixLabors.ImageSharp.Size;

namespace Starling.Gui.Headless.Tests;

/// <summary>
/// Coverage for the synthetic-input surface the MCP server drives:
/// <see cref="WebviewPanel.ClickAt"/> / <see cref="WebviewPanel.TypeText"/> /
/// <see cref="WebviewPanel.MoveTo"/>. Exercised through a real
/// <see cref="WebviewPanel"/> at document-space coordinates (the same space
/// browser_screenshot captures). These are the paths browser_click /
/// browser_type / browser_move bottom out in.
/// </summary>
/// <remarks>
/// Uses self-contained file:// fixtures (never the shared testdata todo app): the
/// JS-backed sites persist to a process-wide localStorage, so two tests touching
/// the same app would contaminate each other's starting state.
/// </remarks>
public class ProgrammaticInputTests
{
    // A form whose submit handler appends the typed value as a list item, with no
    // persistence — so each test starts from a clean DOM.
    private const string FormFixture =
        "<!doctype html><html><body>"
        + "<form id=\"f\"><input id=\"q\" type=\"text\"><button type=\"submit\">go</button></form>"
        + "<ul id=\"out\"></ul>"
        + "<script>document.getElementById('f').addEventListener('submit', function(e){"
        + "  e.preventDefault();"
        + "  var li=document.createElement('li'); li.textContent=document.getElementById('q').value;"
        + "  document.getElementById('out').appendChild(li); });</script>"
        + "</body></html>";

    [AvaloniaFact]
    public async Task ClickAt_focuses_an_input_and_TypeText_sets_its_value()
    {
        // No JS needed: focus + value editing work on any backend.
        var (engine, page, input) = await LoadAsync(
            "<!doctype html><html><body><input type=\"text\" id=\"q\"></body></html>", "q");
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            var focus = Click(panel, CenterOf(CurrentRoot(panel), input)!.Value);
            focus.Ok.Should().BeTrue();
            page.Document.FocusedElement.Should().BeSameAs(input, "the click should focus the field");

            var typed = panel.TypeText("hello");
            typed.Ok.Should().BeTrue();
            input.InputValue.Should().Be("hello");
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public void TypeText_without_a_focused_field_reports_a_failure()
    {
        var engine = new StarlingEngine();
        using var panel = new WebviewPanel(
            new ThemeManager(), NullLoggerFactory.Instance, _ => { }, (_, _) => { },
            (p, vp) => engine.RelayoutPage(p, new RenderOptions(vp, FontSize: 16f)));

        var r = panel.TypeText("hello");
        r.Ok.Should().BeFalse();
        r.Detail.Should().Contain("no page");
    }

    [AvaloniaFact]
    public async Task ClickAt_and_TypeText_submit_a_form_via_the_Add_button()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("STARLING_JS_ENGINE") == "jint",
            "Pins the Jint backend; run with STARLING_JS_ENGINE=jint (e.g. the Jint CI arm).");

        var (engine, page) = await LoadInteractiveAsync(FormFixture);
        var doc = page.Document;
        var input = doc.GetElementById("q")!;
        var output = doc.GetElementById("out")!;
        var button = First(doc, "button")!;
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            panel.BindLiveScripting();
            page.Scripting.Should().NotBeNull("the interactively-loaded form stays live");

            // Locate elements on the live root each time — typing/submitting relays
            // out and swaps the current LaidOutPage.
            Click(panel, CenterOf(CurrentRoot(panel), input)!.Value);
            panel.TypeText("Buy milk").Detail.Should().Contain("Buy milk");
            input.InputValue.Should().Be("Buy milk", "typing writes through to the field's live value");

            Click(panel, CenterOf(CurrentRoot(panel), button)!.Value);

            CountChildren(output, "li").Should().Be(1, "clicking the submit button runs the form handler");
            output.FirstChild!.TextContent.Should().Be("Buy milk");
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public async Task TypeText_with_submit_presses_Enter_to_submit_the_form()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("STARLING_JS_ENGINE") == "jint",
            "Pins the Jint backend; run with STARLING_JS_ENGINE=jint (e.g. the Jint CI arm).");

        var (engine, page) = await LoadInteractiveAsync(FormFixture);
        var doc = page.Document;
        var input = doc.GetElementById("q")!;
        var output = doc.GetElementById("out")!;
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            panel.BindLiveScripting();

            Click(panel, CenterOf(CurrentRoot(panel), input)!.Value);
            var r = panel.TypeText("Walk dog", submit: true);

            r.Ok.Should().BeTrue();
            CountChildren(output, "li").Should().Be(1, "submit=true presses Enter, submitting the form");
            output.FirstChild!.TextContent.Should().Be("Walk dog");
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public async Task MoveTo_dispatches_DOM_mouseover_to_the_hovered_element()
    {
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("STARLING_JS_ENGINE") == "jint",
            "Pins the Jint backend; run with STARLING_JS_ENGINE=jint (e.g. the Jint CI arm).");

        var (engine, page) = await LoadInteractiveAsync(
            "<!doctype html><html><body>"
            + "<div id=\"target\" style=\"width:200px;height:80px\">hover me</div>"
            + "<script>document.getElementById('target')"
            + ".addEventListener('mouseover', function(){ this.setAttribute('data-hovered','yes'); });</script>"
            + "</body></html>");
        var target = page.Document.GetElementById("target")!;
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            panel.BindLiveScripting();
            target.GetAttribute("data-hovered").Should().BeNull("the handler hasn't fired yet");

            var r = Move(panel, CenterOf(CurrentRoot(panel), target)!.Value);
            r.Ok.Should().BeTrue();
            r.Detail.Should().Contain("moved over");
            target.GetAttribute("data-hovered").Should().Be("yes", "moving over the element fires mouseover");
        }
        finally { Teardown(window, panel); }
    }

    // ---- highlight / select / focus by CSS selector (browser_highlight/select/focus) ----

    [AvaloniaFact]
    public async Task HighlightElement_marks_matching_elements()
    {
        var (engine, page) = await LoadStaticAsync(
            "<!doctype html><html><body><p class=\"a\">one</p><p class=\"a\">two</p><p>three</p></body></html>");
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            var hit = panel.HighlightElement("p.a", "red");
            hit.Ok.Should().BeTrue();
            hit.Detail.Should().Contain("highlighted 2");

            var none = panel.HighlightElement("p.missing", null);
            none.Ok.Should().BeTrue();
            none.Detail.Should().Contain("no elements matched");
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public async Task SelectBySelector_selects_an_elements_text()
    {
        var (engine, page) = await LoadStaticAsync(
            "<!doctype html><html><body><p id=\"t\">hello world</p></body></html>");
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            var r = panel.SelectBySelector("#t");
            r.Ok.Should().BeTrue();
            r.Detail.Should().Contain("selected <p>");
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public async Task FocusBySelector_focuses_a_text_input()
    {
        var (engine, page, input) = await LoadAsync(
            "<!doctype html><html><body><input type=\"text\" id=\"q\"></body></html>", "q");
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            var r = panel.FocusBySelector("#q");
            r.Ok.Should().BeTrue();
            page.Document.FocusedElement.Should().BeSameAs(input, "focusing the field sets document focus");

            // Focus via selector should leave the field ready for browser_type.
            panel.TypeText("hi").Ok.Should().BeTrue();
            input.InputValue.Should().Be("hi");
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public async Task FocusBySelector_focuses_a_non_input_element()
    {
        var (engine, page, btn) = await LoadAsync(
            "<!doctype html><html><body><button id=\"b\">go</button></body></html>", "b");
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            var r = panel.FocusBySelector("#b");
            r.Ok.Should().BeTrue();
            page.Document.FocusedElement.Should().BeSameAs(btn);
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public async Task Selector_tools_reject_an_empty_selector()
    {
        var (engine, page) = await LoadStaticAsync("<!doctype html><html><body><p>x</p></body></html>");
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            panel.HighlightElement("", null).Ok.Should().BeFalse();
            panel.SelectBySelector("   ").Ok.Should().BeFalse();
            panel.FocusBySelector("").Ok.Should().BeFalse();
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public async Task ScrollBy_moves_the_outer_page_viewport_and_clamps()
    {
        var (engine, page) = await LoadStaticAsync(
            "<!doctype html><html><body><div style=\"height:2000px\">top</div></body></html>");
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            var first = panel.ScrollBy(0, 250);
            first.Ok.Should().BeTrue();
            ScrollOffsetY(panel).Should().Be(250);

            var clamped = panel.ScrollBy(0, 10_000);
            clamped.Ok.Should().BeTrue();
            ScrollOffsetY(panel).Should().BeLessThan(2000);
            clamped.Detail.Should().Contain("max");
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public async Task ClickBySelector_focuses_the_first_rendered_match()
    {
        var (engine, page, input) = await LoadAsync(
            "<!doctype html><html><body><input type=\"text\" id=\"q\"></body></html>", "q");
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            var r = panel.ClickBySelector("#q");

            r.Ok.Should().BeTrue();
            page.Document.FocusedElement.Should().BeSameAs(input);
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public async Task ScrollTo_supports_absolute_offsets_and_selector_targets()
    {
        var (engine, page) = await LoadStaticAsync(
            "<!doctype html><html><body>"
            + "<div style=\"height:1400px\">top</div><p id=\"target\">target</p>"
            + "<div style=\"height:800px\"></div>"
            + "</body></html>");
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            var absolute = panel.ScrollTo(null, 300, null, null);
            absolute.Ok.Should().BeTrue();
            ScrollOffsetY(panel).Should().Be(300);

            var selector = panel.ScrollTo(null, null, "#target", "center");
            selector.Ok.Should().BeTrue();
            selector.Detail.Should().Contain("#target");
            ScrollOffsetY(panel).Should().BeGreaterThan(300);
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public async Task PressKey_edits_focused_inputs_and_tabs_between_controls()
    {
        var (engine, page) = await LoadStaticAsync(
            "<!doctype html><html><body>"
            + "<input type=\"text\" id=\"first\"><input type=\"text\" id=\"second\">"
            + "</body></html>");
        var first = page.Document.GetElementById("first")!;
        var second = page.Document.GetElementById("second")!;
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            panel.FocusBySelector("#first").Ok.Should().BeTrue();
            panel.TypeText("hello").Ok.Should().BeTrue();

            panel.PressKey("Backspace", shift: false, ctrl: false, alt: false, meta: false).Ok.Should().BeTrue();
            first.InputValue.Should().Be("hell");

            panel.PressKey("Home", shift: false, ctrl: false, alt: false, meta: false).Ok.Should().BeTrue();
            panel.PressKey("Delete", shift: false, ctrl: false, alt: false, meta: false).Ok.Should().BeTrue();
            first.InputValue.Should().Be("ell");

            panel.PressKey("Tab", shift: false, ctrl: false, alt: false, meta: false).Ok.Should().BeTrue();
            page.Document.FocusedElement.Should().BeSameAs(second);
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public async Task PressKey_scrolls_page_navigation_keys_without_a_focused_field()
    {
        var (engine, page) = await LoadStaticAsync(
            "<!doctype html><html><body><div style=\"height:2200px\">top</div></body></html>");
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            panel.PressKey("PageDown", shift: false, ctrl: false, alt: false, meta: false).Ok.Should().BeTrue();
            ScrollOffsetY(panel).Should().BeGreaterThan(0);

            panel.PressKey("Home", shift: false, ctrl: false, alt: false, meta: false).Ok.Should().BeTrue();
            ScrollOffsetY(panel).Should().Be(0);

            panel.PressKey("End", shift: false, ctrl: false, alt: false, meta: false).Ok.Should().BeTrue();
            ScrollOffsetY(panel).Should().BeGreaterThan(0);
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public async Task QuerySelector_reports_bounds_text_and_html()
    {
        var (engine, page) = await LoadStaticAsync(
            "<!doctype html><html><body>"
            + "<p class=\"hit\">one</p><p class=\"hit\">two</p>"
            + "</body></html>");
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            var r = panel.QuerySelector("p.hit", includeText: true, includeHtml: true, limit: 10);

            r.Ok.Should().BeTrue();
            r.Detail.Should().Contain("matches: 2");
            r.Detail.Should().Contain("text=\"one\"");
            r.Detail.Should().Contain("<p");
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public async Task FindText_scrolls_to_the_matching_text_fragment()
    {
        var (engine, page) = await LoadStaticAsync(
            "<!doctype html><html><body>"
            + "<div style=\"height:1400px\">top</div><p>needle phrase</p>"
            + "<div style=\"height:800px\"></div>"
            + "</body></html>");
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            var r = panel.FindText("needle", "next");

            r.Ok.Should().BeTrue();
            r.Detail.Should().Contain("found 'needle'");
            ScrollOffsetY(panel).Should().BeGreaterThan(0);
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public async Task ClipboardAsync_reads_selection_and_pastes_given_text()
    {
        var (engine, page) = await LoadStaticAsync(
            "<!doctype html><html><body>"
            + "<p id=\"copy\">hello clipboard</p><input type=\"text\" id=\"q\">"
            + "</body></html>");
        var input = page.Document.GetElementById("q")!;
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            panel.SelectBySelector("#copy").Ok.Should().BeTrue();

            var selected = await panel.ClipboardAsync("readSelection", null);
            selected.Ok.Should().BeTrue();
            selected.Detail.Should().Be("hello clipboard");

            panel.FocusBySelector("#q").Ok.Should().BeTrue();
            var pasted = await panel.ClipboardAsync("paste", "pasted");
            pasted.Ok.Should().BeTrue();
            input.InputValue.Should().Be("pasted");
        }
        finally { Teardown(window, panel); }
    }

    [AvaloniaFact]
    public async Task CaptureViewportToPng_writes_the_current_viewport()
    {
        var (engine, page) = await LoadStaticAsync(
            "<!doctype html><html><body><p>viewport capture</p></body></html>");
        var path = Path.Combine(Path.GetTempPath(), $"starling-viewport-{Guid.NewGuid():N}.png");
        var (window, panel) = ShowPanel(engine, page);
        try
        {
            var r = panel.CaptureViewportToPng(path);

            r.Ok.Should().BeTrue();
            File.Exists(path).Should().BeTrue();
            File.ReadAllBytes(path).Take(4).Should().Equal(new byte[] { 137, 80, 78, 71 });
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            Teardown(window, panel);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static InputResult Click(WebviewPanel panel, (double X, double Y) p) => panel.ClickAt(p.X, p.Y);
    private static InputResult Move(WebviewPanel panel, (double X, double Y) p) => panel.MoveTo(p.X, p.Y);

    private static double ScrollOffsetY(WebviewPanel panel)
    {
        var scroll = (ScrollViewer)typeof(WebviewPanel)
            .GetField("_scroll", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(panel)!;
        return scroll.Offset.Y;
    }

    private static (Window Window, WebviewPanel Panel) ShowPanel(StarlingEngine engine, LaidOutPage page)
    {
        GpuTests.SkipUnlessAvailable();
        var panel = new WebviewPanel(
            new ThemeManager(), NullLoggerFactory.Instance, _ => { }, (_, _) => { },
            (p, vp) => engine.RelayoutPage(p, new RenderOptions(vp, FontSize: 16f)));
        var window = new Window { Content = panel, Width = 800, Height = 600 };
        window.Show();
        panel.ShowPage(page);
        window.CaptureRenderedFrame(); // force a measure/arrange so the canvas has bounds
        return (window, panel);
    }

    // Close the window before disposing the panel: the panel frees its page bitmap
    // on Dispose, so a window left shown would touch a disposed bitmap in the
    // session's teardown layout pass.
    private static void Teardown(Window window, WebviewPanel panel)
    {
        window.Close();
        panel.Dispose();
    }

    private static async Task<(StarlingEngine Engine, LaidOutPage Page, DomElement Element)> LoadAsync(
        string html, string id)
    {
        var (engine, page) = await LoadStaticAsync(html);
        return (engine, page, page.Document.GetElementById(id)!);
    }

    private static async Task<(StarlingEngine Engine, LaidOutPage Page)> LoadStaticAsync(string html)
    {
        var engine = new StarlingEngine();
        var result = await engine.LayoutPageAsync(
            WriteFixture(html), new RenderOptions(new EngineSize(800, 600), FontSize: 16f),
            CancellationToken.None);
        result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
        return (engine, result.Value);
    }

    private static async Task<(StarlingEngine Engine, LaidOutPage Page)> LoadInteractiveAsync(string html)
    {
        var engine = new StarlingEngine();
        var result = await engine.LayoutPageAsync(
            WriteFixture(html), new RenderOptions(new EngineSize(800, 600), FontSize: 16f),
            CancellationToken.None, onFirstPaint: _ => { });
        result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
        return (engine, result.Value);
    }

    private static string WriteFixture(string html)
    {
        var path = Path.Combine(Path.GetTempPath(), $"starling-input-{Guid.NewGuid():N}.html");
        File.WriteAllText(path, html);
        return "file://" + path.Replace('\\', '/');
    }

    private static Starling.Layout.Box.BlockBox CurrentRoot(WebviewPanel panel)
    {
        var page = (LaidOutPage)typeof(WebviewPanel)
            .GetField("_currentPage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(panel)!;
        return page.Root;
    }

    private static int CountChildren(DomElement parent, string localName)
    {
        var n = 0;
        for (var c = parent.FirstChild; c is not null; c = c.NextSibling)
        {
            if (c is DomElement e && e.LocalName == localName)
            {
                n++;
            }
        }

        return n;
    }

    private static DomElement? First(Starling.Dom.Node root, string localName)
    {
        foreach (var e in root.DescendantElements())
        {
            if (e.LocalName == localName)
            {
                return e;
            }
        }

        return null;
    }

    private static (double X, double Y)? CenterOf(
        Starling.Layout.Box.Box box, DomElement target, double originX = 0, double originY = 0)
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
