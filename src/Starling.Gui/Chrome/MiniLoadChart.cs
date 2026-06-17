using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Starling.Gui.Theme;

namespace Starling.Gui.Chrome;

/// <summary>
/// The mini load chart that lives inside the URL bar during a page load —
/// Avalonia port of Starling.Gui's Chrome/MiniLoadChart.cs. A compact stacked
/// waterfall with a live wall-clock cursor and a total-ms readout.
/// </summary>
public static class MiniLoadChart
{
    private const double TrackWidth = 140;

    public static Border Make(
        ThemeManager tm, IReadOnlyList<TimingBar> phases, double totalMs,
        double cursorFraction = 0.78, Action? onClick = null)
    {
        var t = tm.Tokens;

        var track = new FlameRow
        {
            Bars = phases,
            Total = totalMs <= 0 ? 1 : totalMs,
            Tokens = t,
            CornerRadius = 2,
            BarOpacity = 0.92,
            ShowCursor = true,
            CursorFraction = cursorFraction,
            Width = TrackWidth,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var msLabel = new TextBlock
        {
            Text = $"{totalMs:0}ms",
            FontFamily = new FontFamily(tm.MonoFont),
            FontSize = 10,
            Foreground = new SolidColorBrush(t.Muted),
            Width = 38,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                ChromeKit.Mono(tm, "●", 10, t.Accent),
                track,
                msLabel,
            },
        };

        var border = new Border
        {
            Background = new SolidColorBrush(t.Surface),
            BorderBrush = new SolidColorBrush(t.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(tm.Metrics.RSm),
            Padding = new Thickness(8, 0),
            Height = 22,
            Child = row,
            VerticalAlignment = VerticalAlignment.Center,
        };
        global::Avalonia.Automation.AutomationProperties.SetName(border, $"Page load · {totalMs:0}ms · open Performance");

        if (onClick is not null)
        {
            ChromeKit.AttachClick(border, onClick);
        }

        return border;
    }
}
