using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Gui.Chrome;
using Starling.Gui.Theme;
using Starling.Telemetry;

namespace Starling.Gui.DevTools;

internal static partial class InternalsPanelLog
{
    [LoggerMessage(Level = LogLevel.Trace, Message = "internals activity pump stopped")]
    public static partial void ActivityPumpStopped(ILogger logger, OperationCanceledException ex);

    [LoggerMessage(Level = LogLevel.Trace, Message = "internals metrics pump stopped")]
    public static partial void MetricsPumpStopped(ILogger logger, OperationCanceledException ex);
}

/// <summary>
/// Live internals panel. Module chips count distinct activity sources and
/// operation areas. Metric cards list the last few <see cref="MeterRecord"/>
/// entries per instrument. Some cards stay placeholders until parser, JS engine,
/// and network counters are exposed.
/// </summary>
public sealed class InternalsPanel : Grid, IDisposable
{
    private const int MaxRecentMetrics = 12;

    private readonly ThemeManager _tm;
    private readonly TelemetryStream _stream;
    private readonly ILogger _log;
    private readonly StackPanel _chips;
    private readonly StackPanel _metricsCard;
    private readonly Dictionary<string, int> _opCounts = new(StringComparer.Ordinal);
    private readonly List<MeterRecord> _recentMetrics = new(MaxRecentMetrics);
    private readonly InMemoryActivitySink.Subscription _activitySub;
    private readonly InMemoryMeterSink.Subscription _meterSub;
    private readonly CancellationTokenSource _pumpCts = new();

    public InternalsPanel(ThemeManager tm, TelemetryStream stream,
        ILogger<InternalsPanel>? log = null)
    {
        _tm = tm;
        _stream = stream;
        _log = log ?? NullLogger<InternalsPanel>.Instance;
        var t = tm.Tokens;

        Background = new SolidColorBrush(t.Panel);
        RowDefinitions = new RowDefinitions("Auto,*");

        _chips = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(tm.Metrics.PadSm, 8),
        };
        Children.Add(_chips); SetRow(_chips, 0);

        _metricsCard = new StackPanel { Spacing = 2 };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        grid.Children.Add(BuildCard("Metrics (live)", _metricsCard)); SetColumn(_metricsCard.Parent as Control ?? _metricsCard, 0);
        grid.Children.Add(BuildPlaceholderCard("Parser tree", "TODO: hook Starling.Engine parser counter"));
        // Placeholder cards for counters that are not exposed yet.
        var stack = new StackPanel { Spacing = 8, Margin = new Thickness(tm.Metrics.PadSm) };
        stack.Children.Add(grid);
        stack.Children.Add(BuildGridRow(
            BuildPlaceholderCard("JS engine", "TODO: hook Starling.Js heap snapshot"),
            BuildPlaceholderCard("IPC channels", "TODO: hook Starling.Net IPC counter")));
        var scroll = new ScrollViewer
        {
            Content = stack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Children.Add(scroll); SetRow(scroll, 1);

        // Snapshot first.
        foreach (var a in stream.Activities.Snapshot())
        {
            BumpCount(a);
        }

        foreach (var m in stream.Metrics.Snapshot())
        {
            PushMetric(m);
        }

        RefreshChips();
        RefreshMetrics();

        _activitySub = stream.Activities.Subscribe();
        _meterSub = stream.Metrics.Subscribe();
        _ = PumpActivitiesAsync(_activitySub, _pumpCts.Token);
        _ = PumpMetricsAsync(_meterSub, _pumpCts.Token);
    }

    private async Task PumpActivitiesAsync(InMemoryActivitySink.Subscription sub, CancellationToken ct)
    {
        try
        {
            await foreach (var record in sub.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    BumpCount(record);
                    RefreshChips();
                });
            }
        }
        catch (OperationCanceledException ex) { InternalsPanelLog.ActivityPumpStopped(_log, ex); }
    }

    private async Task PumpMetricsAsync(InMemoryMeterSink.Subscription sub, CancellationToken ct)
    {
        try
        {
            await foreach (var record in sub.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PushMetric(record);
                    RefreshMetrics();
                });
            }
        }
        catch (OperationCanceledException ex) { InternalsPanelLog.MetricsPumpStopped(_log, ex); }
    }

    private void BumpCount(ActivityRecord record)
    {
        var area = AreaOf(record.OperationName);
        _opCounts[area] = _opCounts.GetValueOrDefault(area, 0) + 1;
    }

    private void PushMetric(MeterRecord record)
    {
        _recentMetrics.Add(record);
        if (_recentMetrics.Count > MaxRecentMetrics)
        {
            _recentMetrics.RemoveRange(0, _recentMetrics.Count - MaxRecentMetrics);
        }
    }

    private void RefreshChips()
    {
        _chips.Children.Clear();
        if (_opCounts.Count == 0)
        {
            _chips.Children.Add(ChromeKit.Mono(_tm, "(no activity yet)", _tm.Metrics.FsXs, _tm.Tokens.Faint));
            return;
        }
        foreach (var kv in _opCounts.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            _chips.Children.Add(Chip(kv.Key, kv.Value));
        }
    }

    private Control Chip(string area, int count)
    {
        var t = _tm.Tokens;
        var cat = CategoryFor(area);
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                ChromeKit.Dot(t[cat]),
                ChromeKit.Mono(_tm, area, _tm.Metrics.FsXs, t.Text2),
                ChromeKit.Mono(_tm, count.ToString(), _tm.Metrics.FsXs, t.Faint),
            },
        };
        return new Border
        {
            Background = new SolidColorBrush(t.Surface),
            BorderBrush = new SolidColorBrush(t.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(_tm.Metrics.RPill),
            Padding = new Thickness(8, 3),
            Child = row,
        };
    }

    private void RefreshMetrics()
    {
        _metricsCard.Children.Clear();
        if (_recentMetrics.Count == 0)
        {
            _metricsCard.Children.Add(ChromeKit.Mono(_tm, "(no measurements)", _tm.Metrics.FsXs, _tm.Tokens.Faint));
            return;
        }
        foreach (var m in _recentMetrics)
        {
            var line = $"{m.InstrumentName} {m.Value:0.##} {m.Unit}".TrimEnd();
            _metricsCard.Children.Add(ChromeKit.Mono(_tm, line, _tm.Metrics.FsSm, _tm.Tokens.Text));
        }
    }

    private Control BuildCard(string title, Control content)
    {
        var t = _tm.Tokens;
        var header = ChromeKit.Mono(_tm, title, _tm.Metrics.FsXs, t.Muted);
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(header);
        stack.Children.Add(content);
        return new Border
        {
            Background = new SolidColorBrush(t.Surface),
            BorderBrush = new SolidColorBrush(t.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(_tm.Metrics.RSm),
            Padding = new Thickness(10),
            Margin = new Thickness(4),
            Child = stack,
        };
    }

    private Control BuildPlaceholderCard(string title, string todo)
    {
        return BuildCard(title, ChromeKit.Mono(_tm, todo, _tm.Metrics.FsXs, _tm.Tokens.Faint));
    }

    private Control BuildGridRow(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        grid.Children.Add(left); SetColumn(left, 0);
        grid.Children.Add(right); SetColumn(right, 1);
        return grid;
    }

    private static string AreaOf(string operationName)
    {
        // StarlingTelemetry emits span names shaped like "area.operation" (see
        // StarlingTelemetry.Span). Take the prefix before the first dot.
        var dot = operationName.IndexOf('.');
        return dot > 0 ? operationName[..dot] : operationName;
    }

    private static Category CategoryFor(string area)
    {
        var lower = area.ToLowerInvariant();
        if (lower.Contains("paint"))
        {
            return Category.Paint;
        }

        if (lower.Contains("layout"))
        {
            return Category.Layout;
        }

        if (lower.Contains("css") || lower.Contains("style"))
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

        if (lower.Contains("net") || lower.Contains("http") || lower.Contains("gui"))
        {
            return Category.Net;
        }

        return Category.Idle;
    }

    public void Dispose()
    {
        _pumpCts.Cancel();
        _activitySub.Dispose();
        _meterSub.Dispose();
        _pumpCts.Dispose();
    }
}
