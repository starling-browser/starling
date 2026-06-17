using System.Diagnostics;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Starling.Gui.Chrome;
using Starling.Gui.Controls;
using Starling.Gui.Diagnostics;
using Starling.Gui.DevTools;
using Starling.Gui.Mcp;
using Starling.Gui.Theme;
using Starling.Common;
using Starling.Common.Diagnostics;
using Starling.Css.Media;
using Starling.Engine;
using Starling.Paint.Backend;
using Starling.Html;
using Starling.Telemetry;
using EngineSize = SixLabors.ImageSharp.Size;
using RenderOptions = Starling.Engine.RenderOptions;

namespace Starling.Gui;

internal static partial class MainWindowLog
{
    [LoggerMessage(Level = LogLevel.Information, Message = "first-paint: {ElapsedMs} ms ({OpLabel})")]
    public static partial void FirstPaint(ILogger logger, long elapsedMs, string opLabel);

    [LoggerMessage(Level = LogLevel.Information, Message = "show_page: {OpLabel} navElapsed={NavElapsedMs}ms showPage(+firstPresent)={ShowPageMs}ms")]
    public static partial void ShowPage(ILogger logger, string opLabel, long navElapsedMs, long showPageMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "resize relayout failed: {Message}")]
    public static partial void RelayoutFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "screenshot failed: {Message}")]
    public static partial void ScreenshotFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "inspect failed: {Message}")]
    public static partial void InspectFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "clipboard failed: {Message}")]
    public static partial void ClipboardFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "resize failed: {Message}")]
    public static partial void ResizeFailed(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{ToolName} failed: {Message}")]
    public static partial void InputToolFailed(ILogger logger, string toolName, string message);
}

/// <summary>
/// Composition root for the sidebar, toolbar, webview, status bar, DevTools,
/// and navigation flow.
/// </summary>
public sealed class MainWindow : Window, IBrowserController
{
    private static readonly IReadOnlyList<TabInfo> Bookmarks =
    [
        new TabInfo("b0s", "starlingbrowser.com", "Starling Browser",     Url: "https://starlingbrowser.com/"),
        new TabInfo("b0g", "localhost:8088",   "SVG Logo",                Url: "http://localhost:8088/svg-logo/"),
        new TabInfo("b0t", "localhost:8088",   "Todo",                    Url: "http://localhost:8088/todo/"),
        new TabInfo("b0n", "localhost:8088",   "Animations",              Url: "http://localhost:8088/animations/"),
        new TabInfo("b0a", "example.com",      "Example",                 Url: "https://example.com"),
        new TabInfo("b0b", "Todos",            "Todos",                   Url: "https://jsonplaceholder.typicode.com/todos"),
        new TabInfo("b0c", "netclaw.dev",      "netclaw.dev",             Url: "https://netclaw.dev/"),
        new TabInfo("b1", "google.com",       "Google",                  Url: "https://google.com"),
        new TabInfo("b2", "localhost:8088",   "Words",                   Url: "http://localhost:8088/words/"),
        new TabInfo("b3", "ladybird.org",     "Ladybird",                Url: "https://ladybird.org/"),
        new TabInfo("b4", "mcmaster.com",     "McMaster-Carr",           Url: "https://www.mcmaster.com/"),
        new TabInfo("b5", "github.com",     "GitHub",           Url: "https://github.com/"),
    ];

    // Window icon, loaded once from the bundled PNG (WindowIcon can't parse
    // .icns). Shared across all windows — the stream is consumed at construction.
    private static readonly WindowIcon AppIcon = new(
        Avalonia.Platform.AssetLoader.Open(
            new Uri("avares://Starling.Gui/Assets/icon_1024.png")));

    private readonly ThemeManager _tm;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _log;
    private readonly TelemetryStream _telemetry;
    private readonly BrowserSession _session;
    private readonly WebviewPanel _webview;
    private DevToolsPanel? _devtools;
    private GridSplitter? _devSplitter;
    private Window? _devWindow;
    private DevToolsDock _dock = DevToolsDock.Bottom;

    // Splitter hairline + default extent of the docked panel along its docking
    // axis (width for left/right, height for bottom).
    private const double SplitterThickness = 4;
    private const double DockExtent = 420;
    private const double DockExtentBottom = 320;

    // Chrome controls are rebuilt on every theme change (ChromeKit's static
    // builders bake colors at construction time, so a "theme flip plus a tree
    // rebuild" is the documented way to keep the UI in sync). None of these
    // fields are readonly for that reason.
    private IconButton _backButton = null!;
    private IconButton _forwardButton = null!;
    private IconButton _reloadButton = null!;
    private IconButton _stopButton = null!;
    private IconButton _themeButton = null!;
    private IconButton _devToolsButton = null!;
    private UrlBar _urlBar = null!;
    private StatusBar _statusBar = null!;
    private Border _toolbar = null!;
    private Sidebar _sidebar = null!;
    private Grid _contentStack = null!;

    private CancellationTokenSource? _navCts;
    private bool _busy;

    // The page reference currently handed to the webview. Progressive rendering
    // shows page1 at first paint then may return a successor page2; tracking the
    // last-shown reference lets the success branch skip a redundant re-show when
    // the deferred phase changed nothing and returned the same page1 instance
    // (re-showing it would dispose the live page out from under itself).
    private LaidOutPage? _lastShownPage;

    public MainWindow()
    {
        _tm = Program.Services.GetRequiredService<ThemeManager>();
        _loggerFactory = Program.Services.GetRequiredService<ILoggerFactory>();
        _log = _loggerFactory.CreateLogger<MainWindow>();
        _telemetry = Program.Services.GetRequiredService<TelemetryStream>();
        _session = Program.Services.GetRequiredService<BrowserSession>();
        _webview = new WebviewPanel(_tm, _loggerFactory, OnLinkActivated, OnWebviewStatus, RelayoutForResize,
            prepareAnimationFrame: _session.PrepareAnimationFrame,
            hasActiveAnimations: _session.HasActiveAnimations);

        Title = string.Empty;
        Icon = AppIcon;
        MinWidth = 1024;
        MinHeight = 720;
        Width = 1280;
        Height = 860;

        // Borderless chrome. macOS keeps Full system decorations so the native
        // traffic lights remain visible — ExtendClientAreaToDecorationsHint
        // pushes them on top of the toolbar (we reserve a left gutter for them
        // in BuildChrome). Windows/Linux drop system decorations entirely; the
        // user opted out of custom min/max/close buttons, and resize is handled
        // by the invisible grip overlay added below.
        ExtendClientAreaToDecorationsHint = true;
        WindowDecorations = OperatingSystem.IsMacOS()
            ? WindowDecorations.Full
            : WindowDecorations.None;

        BuildChrome(urlText: string.Empty, statusText: "Ready", devToolsTab: null);
        _tm.Changed += (_, _) => RebuildChromeForTheme();
    }

    private void BuildChrome(string urlText, string statusText, DevToolsTab? devToolsTab)
    {
        _backButton = new IconButton(_tm, Icons.Back, "Back");
        _backButton.Clicked += BackClicked;
        _forwardButton = new IconButton(_tm, Icons.Fwd, "Forward");
        _forwardButton.Clicked += ForwardClicked;
        _reloadButton = new IconButton(_tm, Icons.Reload, "Reload");
        _reloadButton.Clicked += ReloadClicked;
        _stopButton = new IconButton(_tm, Icons.Stop, "Stop") { IsVisible = _busy };
        _stopButton.Clicked += StopClicked;
        _themeButton = new IconButton(_tm, _tm.Theme == ThemeMode.Dark ? Icons.Sun : Icons.Moon, "Toggle theme");
        _themeButton.Clicked += (_, _) => ToggleTheme();
        _devToolsButton = new IconButton(_tm, Icons.Bug, "Toggle DevTools");
        _devToolsButton.Clicked += (_, _) => ToggleDevTools();
        _reloadButton.IsVisible = !_busy;

        _urlBar = new UrlBar(_tm);
        _urlBar.Address.Text = urlText;
        _urlBar.Submitted += async (_, _) => await NavigateAsync(_urlBar.Address.Text, ignoreEmpty: false);
        _urlBar.FindClicked += (_, _) => _webview.FocusFind();

        var reloadCell = new Panel { Children = { _reloadButton, _stopButton } };

        // Nav cluster on the left, with a small column gap that keeps the
        // three buttons visually grouped but not boxed.
        var navCluster = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        navCluster.Children.Add(_backButton);
        navCluster.Children.Add(_forwardButton);
        navCluster.Children.Add(reloadCell);

        var rightCluster = new StackPanel
        {
            Orientation = global::Avalonia.Layout.Orientation.Horizontal,
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
        };
        rightCluster.Children.Add(_devToolsButton);
        rightCluster.Children.Add(_themeButton);

        // Reserve ~72px on the left for macOS traffic lights so they don't
        // overlap the back/forward/reload cluster when the sidebar is
        // collapsed. Other platforms get the original 16px inset.
        var toolbarLeftInset = 16.0;
        var toolbarGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(toolbarLeftInset, 0, 16, 0),
            ColumnSpacing = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        toolbarGrid.Children.Add(navCluster); Grid.SetColumn(navCluster, 0);
        toolbarGrid.Children.Add(_urlBar); Grid.SetColumn(_urlBar, 1);
        toolbarGrid.Children.Add(rightCluster); Grid.SetColumn(rightCluster, 2);

        var t = _tm.Tokens;
        _toolbar = new Border
        {
            Child = toolbarGrid,
            Height = 58,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(t.Bg),
        };
        // Lets the OS treat the toolbar as the window's title bar — handles
        // click-and-drag to move and double-click-to-maximize. Interactive
        // children (buttons, the URL TextBox) still get input normally.
        WindowDecorationProperties.SetElementRole(_toolbar, WindowDecorationsElementRole.TitleBar);

        _statusBar = new StatusBar(_tm);
        _statusBar.SetHint(statusText);

        // Content stack: toolbar / [webview | optional devtools] / status bar.
        // The middle row hosts a Grid that starts as a single "*" cell holding
        // the webview; PlaceDevTools reconfigures it into a split column/row
        // layout (or pops the panel into a floating window) on demand.
        _contentStack = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        _contentStack.Children.Add(_toolbar); Grid.SetRow(_toolbar, 0);
        var middle = new Grid { ColumnDefinitions = new ColumnDefinitions("*") };
        middle.Children.Add(_webview); Grid.SetColumn(_webview, 0);
        _contentStack.Children.Add(middle); Grid.SetRow(middle, 1);
        _contentStack.Children.Add(_statusBar); Grid.SetRow(_statusBar, 2);

        _sidebar = new Sidebar(_tm, Bookmarks, activeId: null, OnSidebarTabActivated, build: GetBuildInfo());

        var rootGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        rootGrid.Children.Add(_sidebar); Grid.SetColumn(_sidebar, 0);
        rootGrid.Children.Add(_contentStack); Grid.SetColumn(_contentStack, 1);
        Background = new SolidColorBrush(t.Bg);
        Content = OperatingSystem.IsMacOS() ? (Control)rootGrid : WrapWithResizeGrips(rootGrid);

        if (devToolsTab is { } tab)
        {
            OpenDevTools(tab);
        }

        UpdateNavButtonStates();
    }

    // Avalonia drops native resize edges when SystemDecorations == None, so
    // on Windows/Linux we overlay an 8px transparent border around the
    // window content and tag each edge/corner with the appropriate
    // WindowDecorationsElementRole. The OS picks up the hit-testing role
    // and gives users back native resize behavior + cursors.
    private static Control WrapWithResizeGrips(Control inner)
    {
        const double grip = 8.0;
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions($"{grip},*,{grip}"),
            ColumnDefinitions = new ColumnDefinitions($"{grip},*,{grip}"),
        };

        static Border Strip(WindowDecorationsElementRole role)
        {
            var b = new Border { Background = Brushes.Transparent };
            WindowDecorationProperties.SetElementRole(b, role);
            return b;
        }

        var nw = Strip(WindowDecorationsElementRole.ResizeNW);
        var n = Strip(WindowDecorationsElementRole.ResizeN);
        var ne = Strip(WindowDecorationsElementRole.ResizeNE);
        var w = Strip(WindowDecorationsElementRole.ResizeW);
        var e = Strip(WindowDecorationsElementRole.ResizeE);
        var sw = Strip(WindowDecorationsElementRole.ResizeSW);
        var s = Strip(WindowDecorationsElementRole.ResizeS);
        var se = Strip(WindowDecorationsElementRole.ResizeSE);

        Grid.SetRow(inner, 1); Grid.SetColumn(inner, 1);
        Grid.SetRow(nw, 0); Grid.SetColumn(nw, 0);
        Grid.SetRow(n, 0); Grid.SetColumn(n, 1);
        Grid.SetRow(ne, 0); Grid.SetColumn(ne, 2);
        Grid.SetRow(w, 1); Grid.SetColumn(w, 0);
        Grid.SetRow(e, 1); Grid.SetColumn(e, 2);
        Grid.SetRow(sw, 2); Grid.SetColumn(sw, 0);
        Grid.SetRow(s, 2); Grid.SetColumn(s, 1);
        Grid.SetRow(se, 2); Grid.SetColumn(se, 2);

        grid.Children.Add(inner);
        grid.Children.Add(nw); grid.Children.Add(n); grid.Children.Add(ne);
        grid.Children.Add(w); grid.Children.Add(e);
        grid.Children.Add(sw); grid.Children.Add(s); grid.Children.Add(se);
        return grid;
    }

    private void RebuildChromeForTheme()
    {
        var urlText = _urlBar.Address.Text ?? string.Empty;
        var statusText = _statusBar.HintText;
        DevToolsTab? activeTab = _devtools?.ActiveTab;

        // Detach _webview from the old middle grid so it can be re-parented to
        // the new one — Avalonia controls reject being added to a second parent.
        if (_webview.Parent is Panel oldParent)
        {
            oldParent.Children.Remove(_webview);
        }

        if (_devtools is not null)
        {
            // The dock position lives on _dock (a field), so it carries across
            // the rebuild; OpenDevTools re-hosts at the same spot. The floating
            // window must be torn down here so we don't leak a second one.
            DestroyDevWindow();
            _devtools.Dispose();
            _devtools = null;
            _devSplitter = null;
        }

        BuildChrome(urlText, statusText, activeTab);
    }

    private void OpenDevTools(DevToolsTab tab)
    {
        _devtools = new DevToolsPanel(_tm, _telemetry, tab, _dock);
        _devtools.CloseRequested += (_, _) => CloseDevTools();
        _devtools.DockRequested += (_, dock) => SetDock(dock);
        PlaceDevTools();
    }

    // Switch the docking position of an already-open panel.
    private void SetDock(DevToolsDock dock)
    {
        if (_devtools is null || _dock == dock)
        {
            return;
        }

        _dock = dock;
        PlaceDevTools();
    }

    // (Re)host _devtools at _dock. Detaches it from whatever currently holds it
    // (the middle grid and/or a floating window), then lays it out afresh. The
    // webview is always re-seated as the primary cell of the middle grid.
    private void PlaceDevTools()
    {
        if (_devtools is null)
        {
            return;
        }

        var middle = (Grid)_contentStack.Children[1];

        // Clear the grid (removes webview + any splitter + a docked panel) and
        // close the floating window (detaching the panel from it) so _devtools
        // and _webview are parentless and free to re-host below.
        middle.Children.Clear();
        DestroyDevWindow();
        _devSplitter = null;
        _devtools.SetDock(_dock);

        if (_dock == DevToolsDock.Floating)
        {
            middle.ColumnDefinitions = new ColumnDefinitions("*");
            middle.RowDefinitions = new RowDefinitions("*");
            Place(middle, _webview);
            ShowDevWindow();
            return;
        }

        _devSplitter = new GridSplitter
        {
            ResizeDirection = _dock == DevToolsDock.Bottom
                ? GridResizeDirection.Rows
                : GridResizeDirection.Columns,
            Background = new SolidColorBrush(_tm.Tokens.Border),
        };

        switch (_dock)
        {
            case DevToolsDock.Right:
                _devSplitter.Width = SplitterThickness;
                middle.RowDefinitions = new RowDefinitions("*");
                middle.ColumnDefinitions = new ColumnDefinitions($"*,Auto,{DockExtent}");
                Place(middle, _webview, col: 0);
                Place(middle, _devSplitter, col: 1);
                Place(middle, _devtools, col: 2);
                break;
            case DevToolsDock.Left:
                _devSplitter.Width = SplitterThickness;
                middle.RowDefinitions = new RowDefinitions("*");
                middle.ColumnDefinitions = new ColumnDefinitions($"{DockExtent},Auto,*");
                Place(middle, _devtools, col: 0);
                Place(middle, _devSplitter, col: 1);
                Place(middle, _webview, col: 2);
                break;
            case DevToolsDock.Bottom:
                _devSplitter.Height = SplitterThickness;
                middle.ColumnDefinitions = new ColumnDefinitions("*");
                middle.RowDefinitions = new RowDefinitions($"*,Auto,{DockExtentBottom}");
                Place(middle, _webview, row: 0);
                Place(middle, _devSplitter, row: 1);
                Place(middle, _devtools, row: 2);
                break;
        }
    }

    // Add a child to the middle grid at an explicit cell. Both attached
    // properties are set every time because they persist on the control across
    // re-parents and a stale value would land it in the wrong cell after a
    // column<->row dock switch.
    private static void Place(Grid grid, Control child, int col = 0, int row = 0)
    {
        Grid.SetColumn(child, col);
        Grid.SetRow(child, row);
        grid.Children.Add(child);
    }

    private void ShowDevWindow()
    {
        if (_devtools is null)
        {
            return;
        }

        var t = _tm.Tokens;
        _devWindow = new Window
        {
            Title = "DevTools",
            Width = 720,
            Height = 480,
            MinWidth = 360,
            MinHeight = 240,
            Background = new SolidColorBrush(t.Panel),
            Content = _devtools,
        };
        // A user-initiated close of the floating window tears DevTools down
        // entirely. DestroyDevWindow nulls _devWindow before calling Close, so
        // programmatic teardown is a no-op here (avoids re-entrancy).
        _devWindow.Closed += (s, _) =>
        {
            if (!ReferenceEquals(_devWindow, s))
            {
                return;
            }

            _devWindow = null;
            CloseDevTools();
        };
        _devWindow.Show(this);
    }

    // Detach the panel from the floating window and close it, without disposing
    // the panel (the caller decides whether to re-host or dispose).
    private void DestroyDevWindow()
    {
        if (_devWindow is null)
        {
            return;
        }

        var w = _devWindow;
        _devWindow = null;
        w.Content = null;
        w.Close();
    }

    private void CloseDevTools()
    {
        if (_devtools is null)
        {
            return;
        }

        var middle = (Grid)_contentStack.Children[1];
        middle.Children.Remove(_devtools);
        if (_devSplitter is not null)
        {
            middle.Children.Remove(_devSplitter);
        }

        DestroyDevWindow();

        _devtools.Dispose();
        _devtools = null;
        _devSplitter = null;

        // Restore the webview as the sole occupant of the middle area.
        middle.ColumnDefinitions = new ColumnDefinitions("*");
        middle.RowDefinitions = new RowDefinitions("*");
        if (!middle.Children.Contains(_webview))
        {
            Place(middle, _webview);
        }
    }

    private async void ToggleTheme()
    {
        _tm.SetTheme(_tm.Theme == ThemeMode.Dark ? ThemeMode.Light : ThemeMode.Dark);
        // The cascade picks up prefers-color-scheme at style-engine
        // construction time, so flipping the theme has to re-run the load
        // for `@media (prefers-color-scheme: dark)` rules to apply.
        if (_session.History.Current is not null && !_busy)
        {
            await RunNavigation((ct, fp) => _session.ReloadInteractiveAsync(BuildOptions(), ct, fp), "Theme reload");
        }
    }

    private void ToggleDevTools()
    {
        if (_devtools is null)
        {
            OpenDevTools(DevToolsTab.Performance);
        }
        else
        {
            CloseDevTools();
        }
    }

    private async void BackClicked(object? s, EventArgs e)
    {
        if (!_session.History.CanGoBack || _busy)
        {
            return;
        }

        await RunNavigation((ct, fp) => _session.BackInteractiveAsync(BuildOptions(), ct, fp), "Back");
    }

    private async void ForwardClicked(object? s, EventArgs e)
    {
        if (!_session.History.CanGoForward || _busy)
        {
            return;
        }

        await RunNavigation((ct, fp) => _session.ForwardInteractiveAsync(BuildOptions(), ct, fp), "Forward");
    }

    private async void ReloadClicked(object? s, EventArgs e)
    {
        if (_session.History.Current is null || _busy)
        {
            return;
        }

        await RunNavigation((ct, fp) => _session.ReloadInteractiveAsync(BuildOptions(), ct, fp), "Reload");
    }

    private void StopClicked(object? s, EventArgs e) => _navCts?.Cancel();

    private async void OnLinkActivated(string resolvedUrl)
        => await NavigateAsync(resolvedUrl, ignoreEmpty: true);

    private void OnWebviewStatus(string text, bool isError) => _statusBar.SetHint(text, isError);

    // Build facts for the sidebar footer: the build's commit plus the JS engine,
    // render engine, and HTML parser this process actually selected (single
    // source of truth — the same selectors the engine/paint pipeline read).
    private static BuildInfo GetBuildInfo()
        => new(GetBuildLabel(), GetJsEngineLabel(), GetRenderBackendLabel(), GetHtmlParserLabel());

    private static string GetBuildLabel()
    {
        // Short build-sha label from assembly informational version. Falls
        // back to "" if none — the footer just renders a dash.
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? string.Empty;
        var plus = info.IndexOf('+');
        if (plus >= 0)
        {
            info = info[(plus + 1)..];
        }

        if (info.Length > 8)
        {
            info = info[..8];
        }

        return info;
    }

    // Canonical names matching the AppHost selection flags / README (--starling/
    // --jint, --imagesharp/--imagesharp-gpu).
    private static string GetJsEngineLabel() => JsEngineSelector.Selected switch
    {
        JsEngineKind.Jint => "jint",
        _ => "starling",
    };

    private static string GetRenderBackendLabel() => PaintBackendSelector.Selected switch
    {
        PaintBackendKind.ImageSharpWebGpu => "imagesharp-gpu",
        _ => "imagesharp",
    };

    // Matches the AppHost selection flags (--starling-html / --anglesharp-html).
    private static string GetHtmlParserLabel() => HtmlBackendSelector.Selected switch
    {
        HtmlBackendKind.AngleSharp => "anglesharp",
        _ => "starling",
    };

    private async void OnSidebarTabActivated(TabInfo tab)
    {
        if (string.IsNullOrWhiteSpace(tab.Url))
        {
            return;
        }

        await NavigateAsync(tab.Url, ignoreEmpty: true);
    }

    private async Task NavigateAsync(string? raw, bool ignoreEmpty)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            if (!ignoreEmpty)
            {
                _statusBar.SetHint("Enter a URL first.", isError: true);
            }

            return;
        }

        var url = UrlBarInputNormalizer.Normalize(trimmed);
        if (url is null)
        {
            _statusBar.SetHint($"Can't navigate: '{trimmed}' is not a URL.", isError: true);
            return;
        }
        if (_urlBar.Address.Text != url)
        {
            _urlBar.Address.Text = url;
        }

        await RunNavigation((ct, fp) => _session.NavigateInteractiveAsync(url, BuildOptions(), ct, fp), $"GET {url}");
    }

    private async Task RunNavigation(
        Func<CancellationToken, Action<LaidOutPage>, Task<Result<LaidOutPage, RenderError>>> navigate,
        string opLabel)
    {
        var previousCts = _navCts;
        var myCts = new CancellationTokenSource();
        _navCts = myCts;
        previousCts?.Cancel();

        BeginBusy(opLabel);
        var sw = Stopwatch.StartNew();

        // Each navigation must be its own trace. RunNavigation is async and
        // _diag.Span starts an Activity before the first await; that AsyncLocal
        // assignment leaks onto the UI thread's ambient context, but the matching
        // dispose runs in the awaited continuation and never restores the UI
        // thread's Current. Without this reset, every later navigation would
        // parent under the previous (finished) navigate span and the dashboard
        // would show one ever-growing trace instead of one trace per navigation.
        Activity.Current = null;
        using var navSpan = StarlingTelemetry.Span("gui", "navigate");
        var navigationActivity = Activity.Current;
        var firstPaintPosted = 0;

        // Progressive first paint: the engine invokes this from a background
        // continuation once render-blocking scripts have run, before deferred
        // scripts settle. Marshal to the UI thread and, if this navigation is
        // still current, show the page immediately so the user sees content
        // without waiting on async/analytics scripts.
        void OnFirstPaint(LaidOutPage page)
        {
            Interlocked.Exchange(ref firstPaintPosted, 1);
            PostUiWithoutActivity(() =>
            {
                if (!ReferenceEquals(_navCts, myCts))
                {
                    return;
                }

                using var _ = GuiActivityScope.Use(navigationActivity);
                // Wall time from navigation start to "page is visible." This is
                // the user-meaningful "loaded and rendered" moment, distinct from
                // the navigate-task's full-settle time (which includes deferred
                // scripts that run after first paint).
                MainWindowLog.FirstPaint(_log, sw.ElapsedMilliseconds, opLabel);
                ApplyShownPage(page, opLabel, sw.ElapsedMilliseconds);
            });
        }

        try
        {
            var result = await navigate(myCts.Token, OnFirstPaint);
            sw.Stop();
            if (!ReferenceEquals(_navCts, myCts))
            {
                return;
            }

            if (result.IsErr)
            {
                _statusBar.SetState(StatusState.Error);
                _statusBar.SetHint($"{opLabel} failed: {result.Error.Message}", isError: true);
            }
            else
            {
                // Avoid a redundant double-show: when the deferred phase changed
                // nothing it returns the very page already shown via OnFirstPaint.
                // Re-showing it would dispose the live page (ShowPage disposes the
                // outgoing page, which would be this same instance).
                if (!ReferenceEquals(result.Value, _lastShownPage))
                {
                    if (Volatile.Read(ref firstPaintPosted) != 0)
                    {
                        using var detached = GuiActivityScope.Detached();
                        using var settleShow = StarlingTelemetry.Span("gui", "settle_show");
                        ApplyShownPage(result.Value, opLabel, sw.ElapsedMilliseconds);
                    }
                    else
                    {
                        ApplyShownPage(result.Value, opLabel, sw.ElapsedMilliseconds);
                    }
                }

                // The page's live JS context is attached only once the load
                // settles. In the no-mutation case above we skip the re-show, so
                // bind the live event loop here too (idempotent when ShowPage
                // already bound it for the reflowed-successor case).
                _webview.BindLiveScripting();
            }
        }
        catch (OperationCanceledException) when (myCts.IsCancellationRequested)
        {
            if (ReferenceEquals(_navCts, myCts))
            {
                _statusBar.SetState(StatusState.Error);
                _statusBar.SetHint($"{opLabel} canceled.", isError: true);
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_navCts, myCts))
            {
                _statusBar.SetState(StatusState.Error);
                _statusBar.SetHint($"{opLabel} threw: {ex.Message}", isError: true);
            }
        }
        finally
        {
            if (ReferenceEquals(_navCts, myCts))
            {
                EndBusy();
                _navCts = null;
            }
            myCts.Dispose();
        }
    }

    private static void PostUiWithoutActivity(Action action)
    {
        using var detached = GuiActivityScope.Detached();
        if (ExecutionContext.IsFlowSuppressed())
        {
            Dispatcher.UIThread.Post(action);
            return;
        }

        var flow = ExecutionContext.SuppressFlow();
        try
        {
            Dispatcher.UIThread.Post(action);
        }
        finally
        {
            flow.Undo();
        }
    }

    /// <summary>
    /// Hand <paramref name="page"/> to the webview and sync the chrome (security
    /// lock, window title, URL bar, status info). Shared by progressive first
    /// paint and the final navigation result. Must run on the UI thread.
    /// </summary>
    private void ApplyShownPage(LaidOutPage page, string opLabel, long elapsedMs)
    {
        // DIAG (open-time investigation): ShowPage synchronously composites +
        // presents the first frame, so this times when pixels actually hit the
        // surface, and navElapsed says whether the UI thread got the page early
        // (first-paint post) or only after the deferred phase finished.
        var showSw = System.Diagnostics.Stopwatch.StartNew();
        _webview.ShowPage(page);
        MainWindowLog.ShowPage(_log, opLabel, elapsedMs, showSw.ElapsedMilliseconds);
        _lastShownPage = page;
        _urlBar.SetSecurity(MapSecurity(page.Security));
        Title = string.IsNullOrWhiteSpace(page.Title) ? string.Empty : page.Title;
        // Read the URL from the page itself rather than History.Current —
        // page.Url is the navigation's own target, immune to interleaving
        // from a parallel nav that might race the URL bar update.
        if (_urlBar.Address.Text != page.Url)
        {
            _urlBar.Address.Text = page.Url;
        }

        _statusBar.SetState(StatusState.Ready);
        _statusBar.SetHint($"{opLabel} → {elapsedMs} ms");
        _statusBar.SetInfo(
            view: $"{page.Viewport.Width}×{page.Viewport.Height}",
            doc: $"{(int)page.DocumentHeight} px",
            hist: HistoryLabel());
    }

    private void BeginBusy(string label)
    {
        _busy = true;
        _backButton.SetEnabled(false);
        _forwardButton.SetEnabled(false);
        _reloadButton.IsVisible = false;
        _stopButton.IsVisible = true;
        _statusBar.SetState(StatusState.Loading);
        _statusBar.SetHint($"{label}…");
        _urlBar.ShowProgress();
    }

    private void EndBusy()
    {
        _busy = false;
        _stopButton.IsVisible = false;
        _reloadButton.IsVisible = true;
        _urlBar.HideProgress();
        UpdateNavButtonStates();
    }

    private static SiteSecurity? MapSecurity(Starling.Net.Http.ConnectionSecurity? cs)
    {
        if (cs is null)
        {
            return null;
        }

        var cert = cs.Certificate;
        return new SiteSecurity(
            Encrypted: cs.IsEncrypted,
            Secure: cs.IsSecure,
            Protocol: cs.Protocol,
            Certificate: cert is not null,
            CertSubject: cert?.Subject,
            CertIssuer: cert?.Issuer,
            CertNotBefore: cert?.NotBefore,
            CertNotAfter: cert?.NotAfter);
    }

    private string HistoryLabel()
    {
        var h = _session.History;
        if (h.Count == 0 || h.Index < 0)
        {
            return "0 / 0";
        }

        return $"{h.Index + 1} / {h.Count}";
    }

    private void UpdateNavButtonStates()
    {
        _backButton.SetEnabled(_session.History.CanGoBack && !_busy);
        _forwardButton.SetEnabled(_session.History.CanGoForward && !_busy);
        _reloadButton.SetEnabled(_session.History.Current is not null && !_busy);
    }

    // Layout viewport tracks the live webview area so pages reflow to the
    // window size (and to sidebar/DevTools toggles), instead of a fixed size.
    private RenderOptions BuildOptions() => BuildOptionsFor(_webview.CurrentViewportSize());

    private RenderOptions BuildOptionsFor(EngineSize viewport) => new(viewport, FontSize: 16f)
    {
        PreferredColorScheme = _tm.Theme == ThemeMode.Dark ? ColorScheme.Dark : ColorScheme.Light,
    };

    // Re-layout callback handed to WebviewPanel: reflow the current page at the
    // new viewport reusing its document/resources (no network). Skipped while a
    // navigation is in flight — that load already picks up the latest size.
    private LaidOutPage? RelayoutForResize(LaidOutPage page, EngineSize viewport)
    {
        if (_busy)
        {
            return null;
        }

        try
        {
            var relaid = _session.RelayoutCurrent(page, BuildOptionsFor(viewport));
            // The webview will swap to `relaid`; keep _lastShownPage in sync so
            // tools that screenshot/inspect the current view (browser_screenshot,
            // browser_inspect, status-bar info) see the reflowed page rather than
            // the navigation-time one.
            _lastShownPage = relaid;
            _statusBar.SetInfo(
                view: $"{relaid.Viewport.Width}×{relaid.Viewport.Height}",
                doc: $"{(int)relaid.DocumentHeight} px",
                hist: HistoryLabel());
            return relaid;
        }
        catch (Exception ex)
        {
            MainWindowLog.RelayoutFailed(_log, ex.Message);
            return null;
        }
    }

    // ---- IBrowserController -------------------------------------------------
    // MCP tool calls land here, marshaled to the UI thread
    // by BrowserControlBridge.
    // Each method drives the same navigation flow as the toolbar buttons and
    // returns a BrowserControlResult so the agent sees the post-state.

    public async Task<BrowserControlResult> NavigateFromToolAsync(string url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return Snapshot(success: false, error: "browser_navigate requires a non-empty url.");
        }

        await NavigateAsync(url, ignoreEmpty: false);
        return Snapshot(success: true);
    }

    public async Task<BrowserControlResult> BackFromToolAsync(CancellationToken ct)
    {
        if (!_session.History.CanGoBack)
        {
            return Snapshot(success: false, error: "No back history.");
        }

        await RunNavigation((c, fp) => _session.BackInteractiveAsync(BuildOptions(), c, fp), "Back");
        return Snapshot(success: true);
    }

    public async Task<BrowserControlResult> ForwardFromToolAsync(CancellationToken ct)
    {
        if (!_session.History.CanGoForward)
        {
            return Snapshot(success: false, error: "No forward history.");
        }

        await RunNavigation((c, fp) => _session.ForwardInteractiveAsync(BuildOptions(), c, fp), "Forward");
        return Snapshot(success: true);
    }

    public async Task<BrowserControlResult> ReloadFromToolAsync(CancellationToken ct)
    {
        if (_session.History.Current is null)
        {
            return Snapshot(success: false, error: "Nothing to reload.");
        }

        await RunNavigation((c, fp) => _session.ReloadInteractiveAsync(BuildOptions(), c, fp), "Reload");
        return Snapshot(success: true);
    }

    private BrowserControlResult Snapshot(bool success, string? error = null, string? detail = null)
    {
        var url = _session.History.Current;
        var title = string.IsNullOrEmpty(Title) ? null : Title;
        return success
            ? BrowserControlResult.Success(url, title, _session.History.CanGoBack, _session.History.CanGoForward, _busy, detail)
            : BrowserControlResult.Failure(error ?? "Unknown error", url, title,
                _session.History.CanGoBack, _session.History.CanGoForward, _busy);
    }

    public Task<BrowserControlResult> ScreenshotFromToolAsync(string path, CancellationToken ct)
    {
        if (_lastShownPage is null)
        {
            return Task.FromResult(Snapshot(success: false, error: "No page is loaded to screenshot."));
        }

        try
        {
            var full = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? "starling-screenshot.png" : path);
            var outcome = _session.CaptureToPng(_lastShownPage, full, _webview.LiveElapsedMs, fullPage: true);
            return Task.FromResult(Snapshot(success: true,
                detail: $"wrote {outcome.OutputPath} ({outcome.Width}x{outcome.Height})"));
        }
        catch (Exception ex)
        {
            MainWindowLog.ScreenshotFailed(_log, ex.Message);
            return Task.FromResult(Snapshot(success: false, error: $"screenshot failed: {ex.Message}"));
        }
    }

    public Task<BrowserControlResult> ScreenshotViewportFromToolAsync(string path, CancellationToken ct)
        => InputTool("screenshot viewport", () => _webview.CaptureViewportToPng(path));

    public Task<BrowserControlResult> InspectFromToolAsync(bool includeHtml, string? logPath, CancellationToken ct)
    {
        try
        {
            var page = _lastShownPage;
            var url = _session.History.Current;

            var logs = _telemetry.Logs.Snapshot();
            var jsLogs = logs.Where(r => r.Category.Contains("engine.js", StringComparison.Ordinal)).ToArray();
            var errors = jsLogs.Count(r => r.Level >= LogLevel.Error);
            var warns = jsLogs.Count(r => r.Level == LogLevel.Warning);

            var html = page?.Document.DocumentElement is { } de ? HtmlSerializer.SerializeNode(de) : null;

            var sb = new StringBuilder();
            sb.Append("url: ").AppendLine(url ?? "(none)");
            sb.Append("title: ").AppendLine(string.IsNullOrEmpty(Title) ? "(none)" : Title);
            sb.Append("busy(loading): ").Append(_busy).AppendLine();
            sb.Append("liveScripting: ").Append(page?.Scripting is not null).AppendLine();
            sb.Append("documentHeightPx: ").Append(page is null ? 0 : (int)page.DocumentHeight).AppendLine();
            sb.Append("outerHtmlBytes: ").Append(html?.Length ?? 0).AppendLine();
            sb.Append("jsConsole: ").Append(jsLogs.Length).Append(" entries, ")
              .Append(errors).Append(" errors, ").Append(warns).AppendLine(" warnings");

            sb.AppendLine("--- recent JS console (warn+error, last 40) ---");
            foreach (var r in jsLogs.Where(r => r.Level >= LogLevel.Warning).TakeLast(40))
            {
                sb.Append('[').Append(r.Level).Append("] ").AppendLine(r.Message);
            }

            if (!string.IsNullOrWhiteSpace(logPath))
            {
                var full = Path.GetFullPath(logPath);
                var report = new StringBuilder();
                report.AppendLine(sb.ToString());
                report.AppendLine("=== ALL TELEMETRY LOGS ===");
                foreach (var r in logs)
                {
                    report.Append(r.TimestampUtc.ToString("HH:mm:ss.fff")).Append(" [").Append(r.Level)
                          .Append("] ").Append(r.Category).Append(": ").AppendLine(r.Message);
                }

                report.AppendLine().AppendLine("=== DOCUMENT outerHTML ===");
                report.AppendLine(html ?? "(no document)");
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllText(full, report.ToString());
                sb.Append("wrote full report to: ").AppendLine(full);
            }

            if (includeHtml && html is not null)
            {
                sb.AppendLine("--- outerHTML (truncated 100k) ---");
                sb.AppendLine(html.Length > 100_000 ? html[..100_000] + "\n…(truncated)" : html);
            }

            return Task.FromResult(Snapshot(success: true, detail: sb.ToString()));
        }
        catch (Exception ex)
        {
            MainWindowLog.InspectFailed(_log, ex.Message);
            return Task.FromResult(Snapshot(success: false, error: $"inspect failed: {ex.Message}"));
        }
    }

    public Task<BrowserControlResult> ConsoleFromToolAsync(string? minLevel, int limit, CancellationToken ct)
    {
        var floor = ParseLogLevel(minLevel);
        if (floor is null && !string.IsNullOrWhiteSpace(minLevel))
        {
            return Task.FromResult(Snapshot(success: false, error: $"Unknown log level '{minLevel}'."));
        }

        var capped = ClampToolLimit(limit, 100, 500);
        var logs = _telemetry.Logs.Snapshot()
            .Where(r => r.Category.Contains("engine.js", StringComparison.Ordinal))
            .Where(r => floor is null || r.Level >= floor)
            .TakeLast(capped)
            .ToArray();

        var sb = new StringBuilder();
        sb.Append("console entries: ").Append(logs.Length).AppendLine();
        foreach (var r in logs)
        {
            sb.Append(r.TimestampUtc.ToString("HH:mm:ss.fff")).Append(" [")
                .Append(r.Level).Append("] ").AppendLine(r.Message);
            if (!string.IsNullOrEmpty(r.Exception))
            {
                sb.AppendLine(r.Exception);
            }
        }
        return Task.FromResult(Snapshot(success: true, detail: sb.ToString()));
    }

    public Task<BrowserControlResult> NetworkFromToolAsync(int limit, CancellationToken ct)
    {
        var capped = ClampToolLimit(limit, 100, 500);
        var sb = new StringBuilder();
        var activities = _telemetry.Activities.Snapshot()
            .Where(IsNetworkActivity)
            .TakeLast(capped)
            .ToArray();
        sb.Append("network spans: ").Append(activities.Length).AppendLine();
        foreach (var a in activities)
        {
            sb.Append(a.StartUtc.ToString("HH:mm:ss.fff")).Append(' ')
                .Append(a.OperationName).Append(" durationMs=")
                .Append(a.Duration.TotalMilliseconds.ToString("0.#"));
            if (FindTag(a, "http.url") is { } url)
            {
                sb.Append(" url=").Append(url);
            }

            if (FindTag(a, "url") is { } url2)
            {
                sb.Append(" url=").Append(url2);
            }

            if (FindTag(a, "http.status_code") is { } status)
            {
                sb.Append(" status=").Append(status);
            }

            sb.AppendLine();
        }

        var logs = _telemetry.Logs.Snapshot()
            .Where(r => r.Message.Contains("fetch", StringComparison.OrdinalIgnoreCase)
                || r.Message.Contains("HTTP", StringComparison.OrdinalIgnoreCase)
                || r.Message.Contains("Network", StringComparison.OrdinalIgnoreCase))
            .TakeLast(capped)
            .ToArray();
        sb.Append("network logs: ").Append(logs.Length).AppendLine();
        foreach (var r in logs)
        {
            sb.Append(r.TimestampUtc.ToString("HH:mm:ss.fff")).Append(" [")
                .Append(r.Level).Append("] ").Append(r.Category).Append(": ")
                .AppendLine(r.Message);
        }

        return Task.FromResult(Snapshot(success: true, detail: sb.ToString()));
    }

    public Task<BrowserControlResult> ClickFromToolAsync(double x, double y, CancellationToken ct)
        => InputTool("click", () => _webview.ClickAt(x, y));

    public Task<BrowserControlResult> ClickSelectorFromToolAsync(string selector, CancellationToken ct)
        => InputTool("click selector", () => _webview.ClickBySelector(selector));

    public Task<BrowserControlResult> MoveMouseFromToolAsync(double x, double y, CancellationToken ct)
        => InputTool("move", () => _webview.MoveTo(x, y));

    public Task<BrowserControlResult> ScrollFromToolAsync(double deltaX, double deltaY, CancellationToken ct)
        => InputTool("scroll", () => _webview.ScrollBy(deltaX, deltaY));

    public Task<BrowserControlResult> ScrollToFromToolAsync(double? x, double? y, string? selector, string? position, CancellationToken ct)
        => InputTool("scroll to", () => _webview.ScrollTo(x, y, selector, position));

    public Task<BrowserControlResult> PressKeyFromToolAsync(string key, bool shift, bool ctrl, bool alt, bool meta, CancellationToken ct)
        => InputTool("press key", () => _webview.PressKey(key, shift, ctrl, alt, meta));

    public Task<BrowserControlResult> TypeTextFromToolAsync(string text, bool submit, CancellationToken ct)
        => InputTool("type", () => _webview.TypeText(text, submit));

    public async Task<BrowserControlResult> WaitFromToolAsync(string state, string? value, int timeoutMs, CancellationToken ct)
    {
        var mode = (state ?? string.Empty).Trim().ToLowerInvariant();
        if (mode.Length == 0)
        {
            return Snapshot(success: false, error: "browser_wait requires a state.");
        }

        if (mode is not ("load" or "idle" or "page" or "selector" or "text" or "url"))
        {
            return Snapshot(success: false, error: "browser_wait state must be load, idle, page, selector, text, or url.");
        }

        if ((mode is "selector" or "text" or "url") && string.IsNullOrWhiteSpace(value))
        {
            return Snapshot(success: false, error: $"browser_wait state '{mode}' requires a value.");
        }

        var capped = Math.Clamp(timeoutMs <= 0 ? 5000 : timeoutMs, 1, 60000);
        var sw = Stopwatch.StartNew();

        while (true)
        {
            if (WaitConditionMet(mode, value, out var detail))
            {
                return Snapshot(success: true, detail: detail);
            }

            if (sw.ElapsedMilliseconds >= capped)
            {
                return Snapshot(success: false, error: $"Timed out after {capped} ms waiting for {mode}.");
            }

            await Task.Delay(50, ct);
        }
    }

    public Task<BrowserControlResult> QueryFromToolAsync(string selector, bool includeText, bool includeHtml, int limit, CancellationToken ct)
        => InputTool("query", () => _webview.QuerySelector(selector, includeText, includeHtml, limit));

    public Task<BrowserControlResult> HighlightFromToolAsync(string selector, string? color, CancellationToken ct)
        => InputTool("highlight", () => _webview.HighlightElement(selector, color));

    public Task<BrowserControlResult> SelectElementFromToolAsync(string selector, CancellationToken ct)
        => InputTool("select", () => _webview.SelectBySelector(selector));

    public Task<BrowserControlResult> FocusElementFromToolAsync(string selector, CancellationToken ct)
        => InputTool("focus", () => _webview.FocusBySelector(selector));

    public Task<BrowserControlResult> FindFromToolAsync(string query, string direction, CancellationToken ct)
        => InputTool("find", () => _webview.FindText(query, direction));

    public async Task<BrowserControlResult> ClipboardFromToolAsync(string action, string? text, CancellationToken ct)
    {
        if (_lastShownPage is null)
        {
            return Snapshot(success: false, error: "No page is loaded for clipboard.");
        }

        try
        {
            var r = await _webview.ClipboardAsync(action, text);
            return r.Ok ? Snapshot(success: true, detail: r.Detail) : Snapshot(success: false, error: r.Detail);
        }
        catch (Exception ex)
        {
            MainWindowLog.ClipboardFailed(_log, ex.Message);
            return Snapshot(success: false, error: $"clipboard failed: {ex.Message}");
        }
    }

    public async Task<BrowserControlResult> BookmarksFromToolAsync(string? id, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            var bookmark = Bookmarks.FirstOrDefault(b => b.Id.Equals(id, StringComparison.Ordinal));
            if (bookmark is null)
            {
                return Snapshot(success: false, error: $"No bookmark with id '{id}'.");
            }

            if (string.IsNullOrWhiteSpace(bookmark.Url))
            {
                return Snapshot(success: false, error: $"Bookmark '{id}' has no URL.");
            }

            await NavigateAsync(bookmark.Url, ignoreEmpty: false);
            return Snapshot(success: true, detail: $"opened bookmark {id}: {bookmark.Title}");
        }

        var sb = new StringBuilder();
        foreach (var b in Bookmarks)
        {
            sb.Append(b.Id).Append(" | ").Append(b.Title).Append(" | ").AppendLine(b.Url ?? string.Empty);
        }

        return Snapshot(success: true, detail: sb.ToString());
    }

    public Task<BrowserControlResult> ComputedStyleFromToolAsync(string selector, CancellationToken ct)
        => InputTool("computed_style", () => _webview.InspectComputedStyle(selector));

    public Task<BrowserControlResult> ResizeFromToolAsync(double width, double height, CancellationToken ct)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return Task.FromResult(Snapshot(success: false,
                error: $"browser_resize requires positive finite width/height; got {width}x{height}."));
        }

        try
        {
            // Setting Width/Height has no effect while the window is maximized
            // or full-screen — drop back to Normal first so the assignment sticks
            // and the WebviewPanel sees a Bounds change that triggers reflow.
            if (WindowState != WindowState.Normal)
            {
                WindowState = WindowState.Normal;
            }

            var w = Math.Max(width, MinWidth);
            var h = Math.Max(height, MinHeight);
            Width = w;
            Height = h;
            return Task.FromResult(Snapshot(success: true, detail: $"resized to {w}x{h} DIPs"));
        }
        catch (Exception ex)
        {
            MainWindowLog.ResizeFailed(_log, ex.Message);
            return Task.FromResult(Snapshot(success: false, error: $"resize failed: {ex.Message}"));
        }
    }

    private bool WaitConditionMet(string mode, string? value, out string detail)
    {
        switch (mode)
        {
            case "load":
            case "idle":
                if (!_busy)
                {
                    detail = "browser is idle";
                    return true;
                }
                detail = "browser is busy";
                return false;

            case "page":
                if (_lastShownPage is not null)
                {
                    detail = "page is loaded";
                    return true;
                }
                detail = "no page loaded";
                return false;

            case "selector":
                if (string.IsNullOrWhiteSpace(value))
                {
                    detail = "selector wait requires value";
                    return false;
                }
                var count = _webview.CountSelectorMatches(value);
                detail = $"selector '{value}' matches {count}";
                return count > 0;

            case "text":
                if (string.IsNullOrEmpty(value))
                {
                    detail = "text wait requires value";
                    return false;
                }
                detail = $"text '{value}' present";
                return _webview.PageContainsText(value);

            case "url":
                if (string.IsNullOrEmpty(value))
                {
                    detail = "url wait requires value";
                    return false;
                }
                var url = _session.History.Current ?? string.Empty;
                detail = $"url is {url}";
                return url.Contains(value, StringComparison.OrdinalIgnoreCase);

            default:
                detail = $"unknown wait state '{mode}'";
                return false;
        }
    }

    private static int ClampToolLimit(int requested, int fallback, int max)
    {
        if (requested <= 0)
        {
            return fallback;
        }

        return Math.Min(requested, max);
    }

    private static LogLevel? ParseLogLevel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return Enum.TryParse<LogLevel>(text, ignoreCase: true, out var level) ? level : null;
    }

    private static bool IsNetworkActivity(ActivityRecord record)
        => record.OperationName.Contains("fetch", StringComparison.OrdinalIgnoreCase)
        || record.OperationName.Contains("http", StringComparison.OrdinalIgnoreCase)
        || FindTag(record, "http.url") is not null
        || FindTag(record, "http.status_code") is not null;

    private static string? FindTag(ActivityRecord record, string name)
    {
        foreach (var tag in record.Tags)
        {
            if (tag.Key.Equals(name, StringComparison.Ordinal))
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    // Shared shell for the synthetic-input tools: guards page presence, runs the
    // panel action on the (already UI-thread) call, and maps its InputResult onto
    // a navigation-state snapshot (effect string in detail, precondition failure
    // in error).
    private Task<BrowserControlResult> InputTool(string name, Func<Controls.InputResult> action)
    {
        if (_lastShownPage is null)
        {
            return Task.FromResult(Snapshot(success: false, error: $"No page is loaded to {name}."));
        }

        try
        {
            var r = action();
            return Task.FromResult(r.Ok
                ? Snapshot(success: true, detail: r.Detail)
                : Snapshot(success: false, error: r.Detail));
        }
        catch (Exception ex)
        {
            MainWindowLog.InputToolFailed(_log, name, ex.Message);
            return Task.FromResult(Snapshot(success: false, error: $"{name} failed: {ex.Message}"));
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _navCts?.Cancel();
        DestroyDevWindow();
        _webview.Dispose();
        _session.Dispose();
        base.OnClosed(e);
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        // Cmd on macOS, Ctrl elsewhere — Avalonia's KeyModifiers.Meta maps to
        // Command on macOS so we accept either to keep behaviour uniform.
        var primary = e.KeyModifiers.HasFlag(KeyModifiers.Meta)
                   || e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (primary && e.Key == Key.F)
        {
            e.Handled = true;
            _webview.FocusFind();
            return;
        }
        if (primary && e.Key == Key.C)
        {
            // Don't steal Cmd-C from a focused TextBox (URL bar, find entry).
            if (FocusManager?.GetFocusedElement() is TextBox) { base.OnKeyDown(e); return; }
            e.Handled = true;
            await _webview.CopySelectionAsync();
            return;
        }
        base.OnKeyDown(e);
    }
}
