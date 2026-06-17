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
/// End-to-end coverage that interaction in a real <see cref="WebviewPanel"/>
/// dispatches DOM events into a live page's JavaScript: typing into a scripted
/// <c>&lt;input&gt;</c> fires the page's <c>input</c> listener, which mutates the
/// DOM (the search-as-you-type path). Exercises the full chain — interactive
/// load retaining the realm, click-to-focus, keystroke → DOM event → JS handler.
/// </summary>
public class LiveScriptingInteractionTests
{
    [AvaloniaFact]
    public async Task Typing_into_a_scripted_input_fires_its_input_listener()
    {
        GpuTests.SkipUnlessAvailable();
        const string html = """
            <!doctype html><html><body>
              <input id='q' type='text'>
              <div id='out'>none</div>
              <script>
                var q = document.getElementById('q');
                q.addEventListener('input', function () {
                  document.getElementById('out').textContent = 'echo:' + q.value;
                });
              </script>
            </body></html>
            """;
        var (engine, page) = await LoadInteractiveAsync(html);
        var doc = page.Document;            // stable across relayouts
        var input = doc.GetElementById("q")!;

        using var panel = new WebviewPanel(
            new ThemeManager(), NullLoggerFactory.Instance, _ => { }, (_, _) => { },
            (p, vp) => engine.RelayoutPage(p, new RenderOptions(vp, FontSize: 16f)));
        var window = new Window { Content = panel, Width = 800, Height = 600 };
        window.Show();
        panel.ShowPage(page);
        panel.BindLiveScripting();
        window.CaptureRenderedFrame();

        page.Scripting.Should().NotBeNull("the interactively-loaded scripted page stays live");

        ClickDoc(window, panel, CenterOf(page.Root, input)!.Value);
        window.KeyTextInput("hi");

        // The page's input handler ran synchronously on each keystroke.
        doc.GetElementById("out")!.TextContent.Should().Be("echo:hi");
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<(StarlingEngine Engine, LaidOutPage Page)> LoadInteractiveAsync(string html)
    {
        var fixture = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"starling-livegui-{Guid.NewGuid():N}.html");
        System.IO.File.WriteAllText(fixture, html);

        var engine = new StarlingEngine();
        var result = await engine.LayoutPageAsync(
            "file://" + fixture.Replace('\\', '/'),
            new RenderOptions(new EngineSize(800, 600), FontSize: 16f),
            CancellationToken.None, onFirstPaint: _ => { });
        result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");
        return (engine, result.Value);
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
