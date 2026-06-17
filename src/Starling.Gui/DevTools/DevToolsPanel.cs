using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Starling.Gui.Chrome;
using Starling.Gui.Theme;
using Starling.Telemetry;

namespace Starling.Gui.DevTools;

/// <summary>
/// Docked DevTools shell. Tab strip over a switching body that hosts panels
/// wired to <see cref="TelemetryStream"/>.
/// </summary>
public sealed class DevToolsPanel : Grid, IDisposable
{
    private readonly ThemeManager _tm;
    private readonly TelemetryStream _stream;
    private readonly ContentControl _body;
    private DevToolsTab _active;
    private DevToolsDock _dock;
    private IDisposable? _activePanel;

    public event EventHandler? CloseRequested;

    /// <summary>Raised when a dock affordance is clicked; the host re-hosts the panel.</summary>
    public event EventHandler<DevToolsDock>? DockRequested;

    /// <summary>The currently selected tab — used to restore selection across theme rebuilds.</summary>
    public DevToolsTab ActiveTab => _active;

    public DevToolsPanel(ThemeManager tm, TelemetryStream stream,
        DevToolsTab active = DevToolsTab.Performance, DevToolsDock dock = DevToolsDock.Bottom)
    {
        _tm = tm;
        _stream = stream;
        _active = active;
        _dock = dock;
        var t = tm.Tokens;

        Background = new SolidColorBrush(t.Panel);
        RowDefinitions = new RowDefinitions("Auto,*");

        var strip = BuildTabStrip();
        Children.Add(strip); SetRow(strip, 0);

        _body = new ContentControl { Content = BuildBody(_active) };
        Children.Add(_body); SetRow(_body, 1);
    }

    private Control BuildTabStrip()
    {
        var t = _tm.Tokens;

        var tabs = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tabs.Children.Add(TabButton(DevToolsTab.Performance, Icons.Spark, "Performance"));
        tabs.Children.Add(TabButton(DevToolsTab.Console, Icons.Console, "Console"));
        tabs.Children.Add(TabButton(DevToolsTab.Internals, Icons.Cpu, "Internals"));
        tabs.Children.Add(TabButton(DevToolsTab.Inspect, Icons.Inspect, "Inspect", dim: true));
        tabs.Children.Add(TabButton(DevToolsTab.Network, Icons.Layers, "Net", dim: true));

        // Dock affordances: left / bottom / right re-host the panel within the
        // main window; detach pops it into a floating top-level window. The
        // active position is highlighted via IconButton's "on" state.
        var docks = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        docks.Children.Add(DockButton(DevToolsDock.Left, Icons.PanelL, "Dock to left"));
        docks.Children.Add(DockButton(DevToolsDock.Bottom, Icons.PanelB, "Dock to bottom"));
        docks.Children.Add(DockButton(DevToolsDock.Right, Icons.PanelR, "Dock to right"));
        docks.Children.Add(DockButton(DevToolsDock.Floating, Icons.Detach, "Undock into separate window"));

        var close = new IconButton(_tm, Icons.Close, "Close DevTools", size: 26);
        close.Clicked += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);

        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        right.Children.Add(docks);
        right.Children.Add(new Border
        {
            Width = 1,
            Margin = new Thickness(2, 8),
            Background = new SolidColorBrush(t.Border),
        });
        right.Children.Add(close);

        var grid = new Grid
        {
            Height = 34,
            Margin = new Thickness(_tm.Metrics.PadSm, 0),
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
        };
        grid.Children.Add(tabs); SetColumn(tabs, 0);
        grid.Children.Add(right); SetColumn(right, 1);

        return new Border
        {
            Background = new SolidColorBrush(t.Bg),
            BorderBrush = new SolidColorBrush(t.Border),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = grid,
        };
    }

    private IconButton DockButton(DevToolsDock dock, string iconData, string label)
    {
        var btn = new IconButton(_tm, iconData, label, isOn: _dock == dock, size: 26);
        btn.Clicked += (_, _) => DockRequested?.Invoke(this, dock);
        return btn;
    }

    private Control TabButton(DevToolsTab tab, string iconData, string label, bool dim = false)
    {
        var t = _tm.Tokens;
        var on = tab == _active;

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Icons.Make(iconData, on ? t.Accent : (dim ? t.Faint : t.Text2), 12),
                ChromeKit.Sans(_tm, label, _tm.Metrics.FsSm,
                    on ? t.Text : (dim ? t.Faint : t.Text2),
                    on ? FontWeight.Bold : FontWeight.Normal),
            },
        };

        var btn = new Border
        {
            Height = 26,
            Padding = new Thickness(12, 0),
            Background = new SolidColorBrush(on ? t.Panel : Colors.Transparent),
            CornerRadius = new CornerRadius(_tm.Metrics.RSm),
            Child = content,
        };
        global::Avalonia.Automation.AutomationProperties.SetName(btn, $"DevTools panel: {label}");

        ChromeKit.AttachClick(btn, () => SetActive(tab));
        return btn;
    }

    public void SetActive(DevToolsTab tab)
    {
        if (_active == tab)
        {
            return;
        }

        _active = tab;
        // Tear down the previous panel's subscription, swap the body, then
        // refresh the strip so the new tab reads as selected.
        _activePanel?.Dispose();
        _activePanel = null;
        _body.Content = BuildBody(_active);
        RebuildStrip();
    }

    /// <summary>
    /// Updates the highlighted dock affordance. The host calls this when it
    /// re-hosts the panel so the strip mirrors the live position; the body and
    /// its telemetry subscription are left untouched.
    /// </summary>
    public void SetDock(DevToolsDock dock)
    {
        if (_dock == dock)
        {
            return;
        }

        _dock = dock;
        RebuildStrip();
    }

    // The strip is always the first child / row 0. Replace it in place so the
    // body control (and the panel it hosts) survive a strip refresh.
    private void RebuildStrip()
    {
        Children.RemoveAt(0);
        var strip = BuildTabStrip();
        Children.Insert(0, strip);
        SetRow(strip, 0);
    }

    private Control BuildBody(DevToolsTab tab)
    {
        Control panel;
        switch (tab)
        {
            case DevToolsTab.Performance:
                var perf = new PerformancePanel(_tm, _stream);
                _activePanel = perf;
                panel = perf;
                break;
            case DevToolsTab.Console:
                var console = new ConsolePanel(_tm, _stream);
                _activePanel = console;
                panel = console;
                break;
            case DevToolsTab.Internals:
                var internals = new InternalsPanel(_tm, _stream);
                _activePanel = internals;
                panel = internals;
                break;
            default:
                panel = Placeholder(tab);
                break;
        }
        return panel;
    }

    private Control Placeholder(DevToolsTab tab) => new Border
    {
        Background = new SolidColorBrush(_tm.Tokens.Panel),
        Child = ChromeKit.Sans(_tm, $"{tab} — not in this design pass", _tm.Metrics.FsSm, _tm.Tokens.Faint),
    };

    public void Dispose()
    {
        _activePanel?.Dispose();
    }
}
