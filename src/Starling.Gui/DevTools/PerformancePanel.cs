using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Gui.Chrome;
using Starling.Gui.Theme;
using Starling.Telemetry;

namespace Starling.Gui.DevTools;

internal static partial class PerformancePanelLog
{
    [LoggerMessage(Level = LogLevel.Trace, Message = "performance span pump stopped")]
    public static partial void PumpStopped(ILogger logger, OperationCanceledException ex);
}

/// <summary>
/// Live performance panel wired to <see cref="TelemetryStream.Activities"/>.
/// Renders captured spans as flame rows grouped by activity source.
/// </summary>
public sealed class PerformancePanel : Grid, IDisposable
{
    private const int MaxRows = 32;

    private readonly ThemeManager _tm;
    private readonly TelemetryStream _stream;
    private readonly ILogger _log;
    private readonly StackPanel _flameRows;
    private readonly TextBlock _summary;
    private readonly InMemoryActivitySink.Subscription _subscription;
    private readonly CancellationTokenSource _pumpCts = new();

    private readonly List<ActivityRecord> _recent = new(MaxRows);

    public PerformancePanel(ThemeManager tm, TelemetryStream stream,
        ILogger<PerformancePanel>? log = null)
    {
        _tm = tm;
        _stream = stream;
        _log = log ?? NullLogger<PerformancePanel>.Instance;
        var t = tm.Tokens;

        Background = new SolidColorBrush(t.Panel);
        RowDefinitions = new RowDefinitions("Auto,*");

        _summary = ChromeKit.Mono(tm, "", tm.Metrics.FsXs, t.Muted);
        var header = new Border
        {
            Padding = new Thickness(tm.Metrics.PadSm, 8),
            BorderBrush = new SolidColorBrush(t.Border),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = _summary,
        };
        Children.Add(header); SetRow(header, 0);

        _flameRows = new StackPanel { Spacing = 2, Margin = new Thickness(tm.Metrics.PadSm, 4) };
        var scroll = new ScrollViewer
        {
            Content = _flameRows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Children.Add(scroll); SetRow(scroll, 1);

        // Snapshot existing activities, then live-pump.
        foreach (var record in stream.Activities.Snapshot())
        {
            Push(record);
        }

        Refresh();

        _subscription = stream.Activities.Subscribe();
        _ = PumpAsync(_subscription, _pumpCts.Token);
    }

    private async Task PumpAsync(InMemoryActivitySink.Subscription sub, CancellationToken ct)
    {
        try
        {
            await foreach (var record in sub.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Push(record);
                    Refresh();
                });
            }
        }
        catch (OperationCanceledException ex) { PerformancePanelLog.PumpStopped(_log, ex); }
    }

    private void Push(ActivityRecord record)
    {
        _recent.Add(record);
        if (_recent.Count > MaxRows)
        {
            _recent.RemoveRange(0, _recent.Count - MaxRows);
        }
    }

    private void Refresh()
    {
        _flameRows.Children.Clear();
        if (_recent.Count == 0)
        {
            _summary.Text = "(no spans captured yet — navigate to populate)";
            return;
        }

        var earliest = _recent.Min(r => r.StartUtc);
        // Scale the timeline to the captured span extent (with a 1 ms floor to
        // avoid divide-by-zero) so bars fill the row width instead of clustering
        // at the left under a fixed, over-long timeline.
        var totalMs = Math.Max(1.0, _recent.Max(r => (r.StartUtc + r.Duration - earliest).TotalMilliseconds));
        _summary.Text = $"{_recent.Count} spans across {totalMs:0} ms · earliest {earliest:HH:mm:ss}";

        // One row per activity source so each backend/operation kind reads as
        // its own lane. Activities in a single source draw left→right by start
        // time as TimingBar entries.
        foreach (var group in _recent.GroupBy(r => r.Source).OrderBy(g => g.Key))
        {
            var bars = group
                .OrderBy(r => r.StartUtc)
                .Select(r => new TimingBar(
                    T: (r.StartUtc - earliest).TotalMilliseconds,
                    D: Math.Max(0.1, r.Duration.TotalMilliseconds),
                    Cat: CategoryFor(r.OperationName),
                    Label: r.OperationName))
                .ToList();

            _flameRows.Children.Add(BuildRow(group.Key, bars, totalMs));
        }
    }

    private Control BuildRow(string source, IReadOnlyList<TimingBar> bars, double totalMs)
    {
        var t = _tm.Tokens;
        var label = ChromeKit.Mono(_tm, source, _tm.Metrics.FsXs, t.Muted);
        label.Width = 160;
        label.TextTrimming = TextTrimming.CharacterEllipsis;

        var flame = new FlameRow
        {
            Bars = bars,
            Total = totalMs,
            Tokens = t,
            ShowLabels = true,
            MonoFontFamily = _tm.MonoFont,
            CornerRadius = 2,
            BarOpacity = 0.95,
            Height = 18,
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(label); SetColumn(label, 0);
        row.Children.Add(flame); SetColumn(flame, 1);
        return row;
    }

    private static Category CategoryFor(string operationName)
    {
        var lower = operationName.ToLowerInvariant();
        if (lower.Contains("paint") || lower.Contains("raster") || lower.Contains("render"))
        {
            return Category.Paint;
        }

        if (lower.Contains("layout") || lower.Contains("display_list"))
        {
            return Category.Layout;
        }

        if (lower.Contains("css") || lower.Contains("style") || lower.Contains("cascade"))
        {
            return Category.Css;
        }

        if (lower.Contains("js"))
        {
            return Category.Js;
        }

        if (lower.Contains("html") || lower.Contains("parse"))
        {
            return Category.Html;
        }

        if (lower.Contains("gc"))
        {
            return Category.Gc;
        }

        if (lower.Contains("net") || lower.Contains("http") || lower.Contains("navigate"))
        {
            return Category.Net;
        }

        return Category.Idle;
    }

    public void Dispose()
    {
        _pumpCts.Cancel();
        _subscription.Dispose();
        _pumpCts.Dispose();
    }
}
