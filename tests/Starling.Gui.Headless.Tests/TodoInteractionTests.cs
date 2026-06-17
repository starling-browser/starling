using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Starling.Engine;
using Starling.Gui.Controls;
using Starling.Gui.Theme;
using AvPoint = Avalonia.Point;
using DomElement = Starling.Dom.Element;
using EngineSize = SixLabors.ImageSharp.Size;

namespace Starling.Gui.Headless.Tests;

/// <summary>
/// End-to-end coverage of the bundled <c>testdata/sites/todo</c> app driven
/// through a real <see cref="WebviewPanel"/>: focus the field, type a title,
/// then click the submit button. The form's <c>submit</c> handler must run and
/// append a list item — exercising the GUI click → implicit-form-submit path,
/// the live JS realm, and the <c>.value</c> binding reading the typed text.
/// </summary>
/// <remarks>
/// Pins the Jint backend, so it must run in isolation with
/// <c>STARLING_JS_ENGINE=jint</c> — the engine's JS-backend selector caches the
/// env var on first use, so another test reading it first would defeat the pin.
/// </remarks>
public class TodoInteractionTests
{
    [AvaloniaFact]
    public async Task Type_a_title_and_click_Add_appends_a_todo()
    {
        // The JS-backend selector caches the env var on first use, so this test
        // can only guarantee Jint when the whole test process is pinned to it.
        // Skip (rather than silently run on whatever backend is cached) otherwise.
        Assert.SkipUnless(
            Environment.GetEnvironmentVariable("STARLING_JS_ENGINE") == "jint",
            "Pins the Jint backend; run with STARLING_JS_ENGINE=jint (e.g. the Jint CI arm).");

        var (engine, page) = await LoadInteractiveAsync(TodoIndexUrl());
        var doc = page.Document;            // stable across relayouts
        var input = doc.GetElementById("new-todo")!;
        var list = doc.GetElementById("list")!;

        CountItems(list).Should().Be(0, "the list starts empty");

        using var panel = new WebviewPanel(
            new ThemeManager(), NullLoggerFactory.Instance, _ => { }, (_, _) => { },
            (p, vp) => engine.RelayoutPage(p, new RenderOptions(vp, FontSize: 16f)));
        var window = new Window { Content = panel, Width = 800, Height = 600 };
        window.Show();
        panel.ShowPage(page);
        panel.BindLiveScripting();
        window.CaptureRenderedFrame();

        page.Scripting.Should().NotBeNull("the interactively-loaded todo app stays live");

        // Focus the field and type a task title (locate it on the live page each
        // time — typing relays out and swaps the current LaidOutPage).
        ClickDoc(window, panel, CenterOf(panel, input)!.Value);
        window.KeyTextInput("Buy milk");

        // The typed text is the field's live value (the binding reads InputValue).
        input.InputValue.Should().Be("Buy milk");

        // Click the "Add" submit button → click → implicit form submit → handler.
        var add = FirstWithClass(doc, "composer__add")
                  ?? throw new InvalidOperationException("Add button not found");
        ClickDoc(window, panel, CenterOf(panel, add)!.Value);

        CountItems(list).Should().Be(1, "clicking Add submits the form and appends a todo");
        ItemTitles(list).Should().ContainSingle().Which.Should().Be("Buy milk");
        doc.GetElementById("status")!.TextContent.Should().Be("1 of 1 to go");
    }

    // ---------------------------------------------------------------- helpers

    private static int CountItems(DomElement list)
    {
        var n = 0;
        for (var c = list.FirstChild; c is not null; c = c.NextSibling)
        {
            if (c is DomElement { LocalName: "li" })
            {
                n++;
            }
        }

        return n;
    }

    private static List<string> ItemTitles(DomElement list)
    {
        var titles = new List<string>();
        for (var c = list.FirstChild; c is not null; c = c.NextSibling)
        {
            if (c is DomElement { LocalName: "li" } li
                && FirstWithClass(li, "item__title") is { } title)
            {
                titles.Add(title.TextContent);
            }
        }

        return titles;
    }

    private static DomElement? FirstWithClass(Starling.Dom.Node root, string cls)
    {
        foreach (var e in root.DescendantElements())
        {
            if (e.ClassList.Contains(cls))
            {
                return e;
            }
        }

        return null;
    }

    private static string TodoIndexUrl()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null
            && !File.Exists(Path.Combine(dir, "testdata", "sites", "todo", "index.html")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        if (dir is null)
        {
            throw new InvalidOperationException("testdata/sites/todo not found above " + AppContext.BaseDirectory);
        }

        var path = Path.Combine(dir, "testdata", "sites", "todo", "index.html");
        return "file://" + path.Replace('\\', '/');
    }

    private static async Task<(StarlingEngine Engine, LaidOutPage Page)> LoadInteractiveAsync(string url)
    {
        var engine = new StarlingEngine();
        var result = await engine.LayoutPageAsync(
            url,
            new RenderOptions(new EngineSize(800, 600), FontSize: 16f),
            CancellationToken.None, onFirstPaint: _ => { });
        result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
        return (engine, result.Value);
    }

    /// <summary>The live page's root box, read from the panel after any relayout.</summary>
    private static Starling.Layout.Box.BlockBox CurrentRoot(WebviewPanel panel)
    {
        var page = (LaidOutPage)typeof(WebviewPanel)
            .GetField("_currentPage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(panel)!;
        return page.Root;
    }

    private static void ClickDoc(Window window, WebviewPanel panel, (double X, double Y) doc)
    {
        var canvas = (Control)typeof(WebviewPanel)
            .GetField("_pageCanvas", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(panel)!;
        var winPt = canvas.TranslatePoint(new AvPoint(doc.X, doc.Y), window)!.Value;
        window.MouseDown(winPt, MouseButton.Left);
        window.MouseUp(winPt, MouseButton.Left);
    }

    private static (double X, double Y)? CenterOf(WebviewPanel panel, DomElement target)
        => CenterOf(CurrentRoot(panel), target);

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
