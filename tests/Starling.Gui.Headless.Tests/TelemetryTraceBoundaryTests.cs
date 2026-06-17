// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Common.Diagnostics;
using Starling.Engine;
using Starling.Gui.Controls;
using Starling.Gui.Theme;
using EngineSize = SixLabors.ImageSharp.Size;

namespace Starling.Gui.Headless.Tests;

public class TelemetryTraceBoundaryTests
{
    private static readonly MethodInfo LiveTick =
        typeof(WebviewPanel).GetMethod("LiveTick", BindingFlags.NonPublic | BindingFlags.Instance)!;

    [AvaloniaFact]
    public async Task Live_tick_does_not_parent_under_a_leaked_navigation_activity()
    {
        GpuTests.SkipUnlessAvailable();
        var url = WritePage("""
            <body><div id="box">hello</div><script>window.__ready = 1;</script></body>
            """);

        var engine = new StarlingEngine();
        var result = await engine.LayoutPageAsync(
            url, new RenderOptions(new EngineSize(800, 600), FontSize: 16f),
            CancellationToken.None, onFirstPaint: _ => { });
        result.IsOk.Should().BeTrue(result.IsErr ? result.Error.Message : "");

        // Capture spans from StarlingTelemetry.Source via an ActivityListener.
        var spans = new List<SpanRecord>();
        using var al = new ActivityListener
        {
            ShouldListenTo = src => src.Name == StarlingTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStarted = a =>
            {
                lock (spans)
                {
                    spans.Add(new SpanRecord(a.OperationName, a.Parent?.DisplayName));
                }
            },
        };
        ActivitySource.AddActivityListener(al);

        using var panel = new WebviewPanel(
            new ThemeManager(), NullLoggerFactory.Instance, _ => { }, (_, _) => { },
            (page, viewport) => engine.RelayoutPage(page, new RenderOptions(viewport, FontSize: 16f)));
        var window = new Window { Content = panel, Width = 800, Height = 600 };
        window.Show();
        panel.ShowPage(result.Value);
        window.CaptureRenderedFrame();

        // Clear spans accumulated during ShowPage/initial render.
        lock (spans)
        {
            spans.Clear();
        }

        var leakedNavigate = new Activity("gui.navigate").Start();
        try
        {
            LiveTick.Invoke(panel, null);
            SpanRecord tick;
            lock (spans)
            {
                tick = spans.Should().ContainSingle(s => s.Name == "gui.live.tick").Subject;
            }

            tick.ParentName.Should().BeNull("dispatcher-driven frames are independent after first paint");
            Activity.Current.Should().BeSameAs(leakedNavigate);
        }
        finally
        {
            leakedNavigate.Stop();
            window.Close();
        }
    }

    private static string WritePage(string html)
    {
        var path = Path.Combine(Path.GetTempPath(), $"starling-trace-boundary-{Guid.NewGuid():N}.html");
        File.WriteAllText(path, html);
        return "file://" + path.Replace('\\', '/');
    }

    private readonly record struct SpanRecord(string Name, string? ParentName);
}
