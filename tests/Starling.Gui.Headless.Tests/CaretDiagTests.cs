using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Starling.Engine;
using Starling.Gui.Controls;
using Starling.Gui.Theme;
using AvPoint = Avalonia.Point;
using DomElement = Starling.Dom.Element;
using EngineSize = SixLabors.ImageSharp.Size;

namespace Starling.Gui.Headless.Tests;

public class CaretDiagTests
{
    private readonly ITestOutputHelper _out;
    public CaretDiagTests(ITestOutputHelper o) => _out = o;

    [AvaloniaFact]
    public async Task Caret_x_per_keystroke()
    {
        GpuTests.SkipUnlessAvailable();
        var url = TodoUrl();
        var engine = new StarlingEngine();
        var result = await engine.LayoutPageAsync(url,
            new RenderOptions(new EngineSize(800, 600), FontSize: 16f),
            CancellationToken.None, onFirstPaint: _ => { });
        Assert.True(result.IsOk, result.IsErr ? result.Error.Message : "");
        var page = result.Value;
        var doc = page.Document;
        var input = doc.GetElementById("new-todo")!;

        using var panel = new WebviewPanel(
            new ThemeManager(), NullLoggerFactory.Instance, _ => { }, (_, _) => { },
            (p, vp) => engine.RelayoutPage(p, new RenderOptions(vp, FontSize: 16f)));
        var window = new Window { Content = panel, Width = 800, Height = 600 };
        window.Show();
        panel.ShowPage(page);
        panel.BindLiveScripting();
        window.CaptureRenderedFrame();

        var lines = new List<string>();
        ClickDoc(window, panel, CenterOf(CurrentRoot(panel), input)!.Value);
        lines.Add($"after focus: caretX={CaretX(panel)}, idx={CaretIndex(panel)}, val='{input.InputValue}'");

        // Does the idle todo page spuriously report DOM mutations on each pump?
        var elapsed = 0L;
        for (var i = 0; i < 6; i++)
        {
            elapsed += 16;
            var mutated = page.Scripting!.PumpFrame(elapsed);
            lines.Add($"idle pump #{i}: mutated={mutated}");
        }

        var liveTick = typeof(WebviewPanel).GetMethod("LiveTick", BindingFlags.NonPublic | BindingFlags.Instance)!;
        foreach (var ch in "ab cd ef")
        {
            window.KeyTextInput(ch.ToString());
            lines.Add($"typed '{ch}': caretX={CaretX(panel)}, idx={CaretIndex(panel)}, val='{input.InputValue}'");
            liveTick.Invoke(panel, null);  // simulate the real GUI's 16ms live pump
            lines.Add($"   after pump: caretX={CaretX(panel)}, idx={CaretIndex(panel)}");
        }
        File.WriteAllLines("/tmp/caret-diag.txt", lines);
        foreach (var l in lines)
        {
            _out.WriteLine(l);
        }
    }

    private static double? CaretX(WebviewPanel panel)
    {
        var ov = typeof(WebviewPanel).GetField("_caretOverlay", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(panel) as Control;
        return ov is null ? null : Canvas.GetLeft(ov);
    }

    private static int CaretIndex(WebviewPanel panel)
        => (int)typeof(WebviewPanel).GetField("_caretIndex", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(panel)!;

    private static Starling.Layout.Box.BlockBox CurrentRoot(WebviewPanel panel)
        => ((LaidOutPage)typeof(WebviewPanel).GetField("_currentPage", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(panel)!).Root;

    private static string TodoUrl()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "testdata", "sites", "todo", "index.html")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        return "file://" + Path.Combine(dir!, "testdata", "sites", "todo", "index.html").Replace('\\', '/');
    }

    private static void ClickDoc(Window window, WebviewPanel panel, (double X, double Y) docPt)
    {
        var canvas = (Control)typeof(WebviewPanel).GetField("_pageCanvas", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(panel)!;
        var winPt = canvas.TranslatePoint(new AvPoint(docPt.X, docPt.Y), window)!.Value;
        window.MouseDown(winPt, MouseButton.Left);
        window.MouseUp(winPt, MouseButton.Left);
    }

    private static (double X, double Y)? CenterOf(Starling.Layout.Box.Box box, DomElement target, double ox = 0, double oy = 0)
    {
        var fx = ox + box.Frame.X;
        var fy = oy + box.Frame.Y;
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
