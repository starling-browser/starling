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

internal static partial class ConsolePanelLog
{
    [LoggerMessage(Level = LogLevel.Trace, Message = "console log pump stopped")]
    public static partial void PumpStopped(ILogger logger, OperationCanceledException ex);
}

/// <summary>
/// Live console panel wired to <see cref="InMemoryLogSink"/> via
/// <see cref="TelemetryStream.Logs"/>. Renders the recent snapshot on open,
/// then appends entries as the sink fan-outs them — marshaled to the UI
/// thread by <see cref="Dispatcher.UIThread"/>.
/// </summary>
public sealed class ConsolePanel : Grid, IDisposable
{
    private const int MaxRows = 500;

    private readonly ThemeManager _tm;
    private readonly TelemetryStream _stream;
    private readonly ILogger _log;
    private readonly StackPanel _rows;
    private readonly TextBlock _countLabel;
    private readonly InMemoryLogSink.Subscription _subscription;
    private readonly CancellationTokenSource _pumpCts = new();
    private LogLevel? _filter;
    private int _emitted;

    public ConsolePanel(ThemeManager tm, TelemetryStream stream,
        ILogger<ConsolePanel>? log = null)
    {
        _tm = tm;
        _stream = stream;
        _log = log ?? NullLogger<ConsolePanel>.Instance;
        var t = tm.Tokens;

        Background = new SolidColorBrush(t.Panel);
        RowDefinitions = new RowDefinitions("Auto,*");

        var toolbar = BuildToolbar(out _countLabel);
        Children.Add(toolbar); SetRow(toolbar, 0);

        _rows = new StackPanel();
        var scroll = new ScrollViewer
        {
            Content = _rows,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Children.Add(scroll); SetRow(scroll, 1);

        // Snapshot first so the panel shows recent activity on open; then live.
        foreach (var record in stream.Logs.Snapshot())
        {
            AppendRow(record);
        }

        UpdateCount();

        _subscription = stream.Logs.Subscribe();
        _ = PumpAsync(_subscription, _pumpCts.Token);
    }

    private async Task PumpAsync(InMemoryLogSink.Subscription sub, CancellationToken ct)
    {
        try
        {
            await foreach (var record in sub.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    AppendRow(record);
                    UpdateCount();
                });
            }
        }
        catch (OperationCanceledException ex) { ConsolePanelLog.PumpStopped(_log, ex); }
    }

    private void AppendRow(LogRecord record)
    {
        if (_filter is { } f && record.Level != f)
        {
            return;
        }

        _rows.Children.Add(BuildRow(record));
        while (_rows.Children.Count > MaxRows)
        {
            _rows.Children.RemoveAt(0);
        }
    }

    private Control BuildRow(LogRecord r)
    {
        var t = _tm.Tokens;
        var fsSm = _tm.Metrics.FsSm;
        var fsXs = _tm.Metrics.FsXs;

        var rowBg = r.Level switch
        {
            LogLevel.Error or LogLevel.Critical => new SolidColorBrush(t.Err, 0.06),
            LogLevel.Warning => new SolidColorBrush(t.Warn, 0.05),
            _ => (IBrush)new SolidColorBrush(Colors.Transparent),
        };

        var grid = new Grid
        {
            ColumnSpacing = 10,
            Margin = new Thickness(0),
            ColumnDefinitions = new ColumnDefinitions("76,72,140,*"),
            Background = rowBg,
        };

        var time = ChromeKit.Mono(_tm, r.TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff"), fsSm, t.Faint);
        grid.Children.Add(time); SetColumn(time, 0);

        var level = ChromeKit.Mono(_tm, r.Level.ToString().ToLowerInvariant(), fsSm, LevelColor(t, r.Level));
        grid.Children.Add(level); SetColumn(level, 1);

        var category = ChromeKit.Mono(_tm, ShortCategory(r.Category), fsXs, t[CategoryFor(r.Category)]);
        grid.Children.Add(category); SetColumn(category, 2);

        var message = ChromeKit.Mono(_tm, r.Message, fsSm, t.Text);
        message.TextTrimming = TextTrimming.None;
        message.TextWrapping = TextWrapping.Wrap;
        grid.Children.Add(message); SetColumn(message, 3);

        return new Border
        {
            Padding = new Thickness(12, 4),
            Child = grid,
            BorderBrush = new SolidColorBrush(t.Border),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private Control BuildToolbar(out TextBlock countLabel)
    {
        var t = _tm.Tokens;
        var pills = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pills.Children.Add(FilterPill(null, "all", t.Text));
        pills.Children.Add(FilterPill(LogLevel.Error, "error", t.Err));
        pills.Children.Add(FilterPill(LogLevel.Warning, "warn", t.Warn));
        pills.Children.Add(FilterPill(LogLevel.Information, "info", t.Muted));
        pills.Children.Add(FilterPill(LogLevel.Debug, "debug", t.CatCss));

        countLabel = ChromeKit.Mono(_tm, "", _tm.Metrics.FsXs, t.Faint);

        var grid = new Grid
        {
            Height = 36,
            Margin = new Thickness(_tm.Metrics.PadSm, 0),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        grid.Children.Add(pills); SetColumn(pills, 0);
        grid.Children.Add(countLabel); SetColumn(countLabel, 1);

        return new Border
        {
            Background = new SolidColorBrush(t.Bg),
            BorderBrush = new SolidColorBrush(t.Border),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        };
    }

    private Control FilterPill(LogLevel? level, string label, Color dotColor)
    {
        var t = _tm.Tokens;
        var active = _filter == level;
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                ChromeKit.Dot(dotColor),
                ChromeKit.Mono(_tm, label, _tm.Metrics.FsXs, t.Text2),
            },
        };
        var pill = new Border
        {
            Background = new SolidColorBrush(active ? t.Surface : Colors.Transparent),
            BorderBrush = new SolidColorBrush(active ? t.Border : Colors.Transparent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(_tm.Metrics.RPill),
            Padding = new Thickness(8, 3),
            Child = content,
        };
        ChromeKit.AttachClick(pill, () =>
        {
            _filter = level;
            RebuildRows();
        });
        return pill;
    }

    private void RebuildRows()
    {
        _rows.Children.Clear();
        foreach (var record in _stream.Logs.Snapshot())
        {
            if (_filter is { } f && record.Level != f)
            {
                continue;
            }

            _rows.Children.Add(BuildRow(record));
        }
        UpdateCount();
    }

    private void UpdateCount()
    {
        _emitted = _rows.Children.Count;
        _countLabel.Text = $"{_emitted} entries";
    }

    private static Color LevelColor(ThemeTokens t, LogLevel level) => level switch
    {
        LogLevel.Critical or LogLevel.Error => t.Err,
        LogLevel.Warning => t.Warn,
        LogLevel.Information => t.Muted,
        LogLevel.Debug or LogLevel.Trace => t.CatCss,
        _ => t.Text2,
    };

    private static string ShortCategory(string fullCategory)
    {
        // Trim "Starling." prefix and tail-truncate to ~16 chars so the column
        // stays narrow. Names like "Starling.paint" → "paint".
        var s = fullCategory.StartsWith("Starling.", StringComparison.Ordinal)
            ? fullCategory["Starling.".Length..]
            : fullCategory;
        return s.Length <= 16 ? s : s[..14] + "..";
    }

    private static Category CategoryFor(string fullCategory)
    {
        // Best-effort mapping from logger category → flame-row category color.
        // Mirrors the activity-name lookup in PerformancePanel; logger names
        // emit by area like "Starling.paint", "Starling.layout", etc.
        var lower = fullCategory.ToLowerInvariant();
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

        if (lower.Contains("net") || lower.Contains("http"))
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
