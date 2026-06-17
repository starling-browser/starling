using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Starling.Gui.Theme;

namespace Starling.Gui.Chrome;

/// <summary>
/// The URL bar — calm-modern redesign. A soft rounded well with a focus glow,
/// a color-segmented URL display (faint scheme + sep, full host, muted path),
/// an accent-tinted progress pill that appears during navigation, and a Find
/// affordance pinned to the right.
/// </summary>
public sealed class UrlBar : Border
{
    private readonly ThemeManager _tm;
    private readonly Grid _grid;
    private readonly TextBlock _schemeText;
    private readonly TextBlock _sepText;
    private readonly TextBlock _hostText;
    private readonly TextBlock _pathText;
    private readonly Panel _display;
    private readonly Border _focusRing;
    private Border? _progressPill;
    private TextBlock? _progressMs;
    private Ellipse? _progressSpinner;
    private DispatcherTimer? _progressTimer;
    private DateTime _progressStarted;

    private readonly Border _lockCell;
    private SiteSecurity? _security;
    private Popup? _securityPopup;
    private bool _detailsExpanded;

    /// <summary>The editable address field (hidden until focused).</summary>
    public TextBox Address { get; }

    public event EventHandler? FindClicked;
    public event EventHandler? LockClicked;
    public event EventHandler? Submitted;

    public UrlBar(ThemeManager tm, bool secure = true)
    {
        _tm = tm;
        var t = tm.Tokens;

        // Lock cell on the left. Its icon/colour and the popover it opens are
        // refreshed per navigation via SetSecurity.
        var lockIcon = Icons.Make(secure ? Icons.Lock : Icons.Shield,
            secure ? t.Accent : t.Muted, 13);
        _lockCell = new Border
        {
            Width = 38,
            Child = lockIcon,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        var lockCell = _lockCell;
        global::Avalonia.Automation.AutomationProperties.SetName(lockCell, secure ? "Secure connection" : "Connection not secure");
        ChromeKit.AttachClick(lockCell, () =>
        {
            LockClicked?.Invoke(this, EventArgs.Empty);
            ToggleSecurityPopover();
        });

        // Segmented URL display (visible by default)
        _schemeText = MonoSegment("https", t.Faint);
        _sepText = MonoSegment("://", t.Faint);
        _hostText = MonoSegment("", t.Text);
        _pathText = MonoSegment("", t.Muted);

        var displayRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 6, 0),
        };
        displayRow.Children.Add(_schemeText);
        displayRow.Children.Add(_sepText);
        displayRow.Children.Add(_hostText);
        displayRow.Children.Add(_pathText);

        // Editable TextBox (overlays the display when focused)
        Address = new TextBox
        {
            PlaceholderText = "Enter URL — e.g. example.com",
            FontFamily = new FontFamily(tm.MonoFont),
            FontSize = 13,
            Foreground = new SolidColorBrush(t.Text),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            IsVisible = false,
            CaretBrush = new SolidColorBrush(t.Accent),
        };
        Address.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                Submitted?.Invoke(this, EventArgs.Empty);
                CommitDisplayFromAddress();
                HideEditor();
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                HideEditor();
            }
        };
        Address.LostFocus += (_, _) =>
        {
            CommitDisplayFromAddress();
            HideEditor();
        };
        // Reflect external writes (MainWindow sets Address.Text after navigation).
        Address.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty && !Address.IsFocused)
            {
                SetDisplayUrl(Address.Text ?? string.Empty);
            }
        };

        // The display panel must always be hit-test visible (even when the
        // segments are empty), so clicking an empty URL bar focuses the editor.
        // A Panel with no background has zero hit area — wrap in a Border with
        // Background=Transparent.
        _display = new Panel { Background = new SolidColorBrush(Colors.Transparent) };
        _display.Children.Add(displayRow);
        _display.Children.Add(Address);
        // Only enter edit mode on the *first* click — once the TextBox is
        // focused, subsequent clicks must fall through so they position the
        // caret instead of re-selecting the entire URL.
        ChromeKit.AttachClick(_display, () =>
        {
            if (Address.IsFocused)
            {
                return;
            }

            ShowEditor();
        });

        // Find chip on the far right
        var findBtn = BuildFindChip(tm);

        _grid = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
        };
        _grid.Children.Add(lockCell); Grid.SetColumn(lockCell, 0);
        _grid.Children.Add(_display); Grid.SetColumn(_display, 1);
        _grid.Children.Add(findBtn); Grid.SetColumn(findBtn, 3);

        // The well itself — soft border, generous corner radius
        Background = new SolidColorBrush(t.Surface);
        BorderBrush = new SolidColorBrush(t.Hair);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(10);
        Height = 38;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Child = _grid;

        // The focus ring sits *behind* the bar as a 3px outer halo when focused.
        _focusRing = new Border
        {
            Background = new SolidColorBrush(t.AccentBg),
            CornerRadius = new CornerRadius(13),
            Margin = new Thickness(-3),
            IsVisible = false,
            ZIndex = -1,
            IsHitTestVisible = false,
        };

        Address.GotFocus += (_, _) =>
        {
            BorderBrush = new SolidColorBrush(t.Accent);
            _focusRing.IsVisible = true;
        };
        Address.LostFocus += (_, _) =>
        {
            BorderBrush = new SolidColorBrush(t.Hair);
            _focusRing.IsVisible = false;
        };

        ChromeKit.AttachHover(this,
            () => { if (!Address.IsFocused) { BorderBrush = new SolidColorBrush(t.Strong); } },
            () => { if (!Address.IsFocused) { BorderBrush = new SolidColorBrush(t.Hair); } });
    }

    /// <summary>The host the parent should attach the focus ring on, so the
    /// glow sits behind the URL bar in z-order.</summary>
    public Control FocusRing => _focusRing;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Avalonia keeps keyboard focus on a TextBox when the user clicks
        // non-focusable chrome (Borders, Panels). Listen at the top level so
        // we can hand focus away — that, in turn, fires LostFocus and the
        // existing handler collapses the editor back to the display segments.
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.AddHandler(InputElement.PointerPressedEvent, OnTopLevelPointerPressed,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (TopLevel.GetTopLevel(this) is { } top)
        {
            top.RemoveHandler(InputElement.PointerPressedEvent, OnTopLevelPointerPressed);
        }
    }

    private void OnTopLevelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!Address.IsFocused)
        {
            return;
        }

        if (e.Source is Visual v && IsDescendantOf(v, this))
        {
            return;
        }
        // Outside click — move keyboard focus to the top level so the
        // TextBox's LostFocus fires and the editor collapses.
        TopLevel.GetTopLevel(this)?.Focus();
    }

    private static bool IsDescendantOf(Visual? v, Visual root)
    {
        while (v is not null)
        {
            if (ReferenceEquals(v, root))
            {
                return true;
            }

            v = v.GetVisualParent();
        }
        return false;
    }

    private TextBlock MonoSegment(string text, Color color) => new()
    {
        Text = text,
        FontFamily = new FontFamily(_tm.MonoFont),
        FontSize = 13,
        Foreground = new SolidColorBrush(color),
        VerticalAlignment = VerticalAlignment.Center,
    };

    private Control BuildFindChip(ThemeManager tm)
    {
        var t = tm.Tokens;
        var icon = Icons.Make(Icons.Find, t.Muted, 13);
        var label = new TextBlock
        {
            Text = "Find",
            FontFamily = new FontFamily(tm.SansFont),
            FontSize = 12,
            Foreground = new SolidColorBrush(t.Muted),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var kbd = new Border
        {
            Background = new SolidColorBrush(t.Bg),
            BorderBrush = new SolidColorBrush(t.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 0, 5, 1),
            Child = new TextBlock
            {
                Text = "⌘F",
                FontFamily = new FontFamily(tm.MonoFont),
                FontSize = 10,
                Foreground = new SolidColorBrush(t.Faint),
            },
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { icon, label, kbd },
        };
        var divider = new Border
        {
            Width = 1,
            Background = new SolidColorBrush(t.Rule()),
            VerticalAlignment = VerticalAlignment.Stretch,
            Margin = new Thickness(0, 8),
        };
        var inner = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { divider, row },
        };
        var btn = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(2, 0, 14, 0),
            Child = inner,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        global::Avalonia.Automation.AutomationProperties.SetName(btn, "Find in page");

        ChromeKit.AttachClick(btn, () => FindClicked?.Invoke(this, EventArgs.Empty));
        ChromeKit.AttachHover(btn,
            () => label.Foreground = new SolidColorBrush(t.Text),
            () => label.Foreground = new SolidColorBrush(t.Muted));
        return btn;
    }

    private void ShowEditor()
    {
        Address.Text = ReconstructFullUrl();
        Address.IsVisible = true;
        // Hide the display row siblings so the editor takes the full width.
        if (_display.Children.Count > 0 && _display.Children[0] is StackPanel sp)
        {
            sp.IsVisible = false;
        }

        Address.Focus();
        Address.SelectAll();
    }

    private void HideEditor()
    {
        Address.IsVisible = false;
        if (_display.Children.Count > 0 && _display.Children[0] is StackPanel sp)
        {
            sp.IsVisible = true;
        }
    }

    private string ReconstructFullUrl()
    {
        var scheme = _schemeText.Text ?? "";
        var host = _hostText.Text ?? "";
        var path = _pathText.Text ?? "";
        if (string.IsNullOrEmpty(scheme) && string.IsNullOrEmpty(host))
        {
            return Address.Text ?? "";
        }

        return $"{scheme}://{host}{path}";
    }

    private void CommitDisplayFromAddress()
    {
        var raw = Address.Text ?? string.Empty;
        SetDisplayUrl(raw);
    }

    /// <summary>Parses a URL into scheme / host / path segments for display.</summary>
    public void SetDisplayUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            _schemeText.Text = "";
            _sepText.Text = "";
            _hostText.Text = "";
            _pathText.Text = "";
            return;
        }

        var schemeIdx = url.IndexOf("://", StringComparison.Ordinal);
        string scheme, hostAndPath;
        if (schemeIdx >= 0)
        {
            scheme = url[..schemeIdx];
            hostAndPath = url[(schemeIdx + 3)..];
        }
        else
        {
            scheme = "";
            hostAndPath = url;
        }

        var slashIdx = hostAndPath.IndexOf('/');
        string host, path;
        if (slashIdx >= 0)
        {
            host = hostAndPath[..slashIdx];
            path = hostAndPath[slashIdx..];
        }
        else
        {
            host = hostAndPath;
            path = "";
        }

        _schemeText.Text = scheme;
        _sepText.Text = string.IsNullOrEmpty(scheme) ? "" : "://";
        _hostText.Text = host;
        _pathText.Text = path;
    }

    /// <summary>Shows the accent-tinted progress pill and starts the live ms counter.</summary>
    public void ShowProgress()
    {
        EnsurePill();
        SetPillActive(true);

        _progressStarted = DateTime.UtcNow;
        _progressTimer?.Stop();
        _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _progressTimer.Tick += (_, _) =>
        {
            if (_progressMs is null)
            {
                return;
            }

            var ms = (int)(DateTime.UtcNow - _progressStarted).TotalMilliseconds;
            _progressMs.Text = ms < 1000 ? $"{ms} ms" : $"{ms / 1000.0:0.0} s";
        };
        _progressTimer.Start();
    }

    /// <summary>Freezes the progress pill at the final timing — pill stays
    /// visible but mutes; brightens on hover.</summary>
    public void HideProgress()
    {
        _progressTimer?.Stop();
        _progressTimer = null;
        SetPillActive(false);
    }

    /// <summary>Builds the pill once and parks it in column 2 of the grid.</summary>
    private void EnsurePill()
    {
        if (_progressPill is not null)
        {
            return;
        }

        var t = _tm.Tokens;

        _progressSpinner = new Ellipse
        {
            Width = 9,
            Height = 9,
            Stroke = new SolidColorBrush(t.Accent),
            StrokeThickness = 1.5,
            StrokeDashArray = [16, 6],
            VerticalAlignment = VerticalAlignment.Center,
        };
        _progressSpinner.RenderTransform = new RotateTransform(0);
        var anim = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(900),
            IterationCount = IterationCount.Infinite,
            Easing = new LinearEasing(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(RotateTransform.AngleProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(RotateTransform.AngleProperty, 360d) } },
            },
        };
        _ = anim.RunAsync(_progressSpinner);

        _progressMs = new TextBlock
        {
            Text = "—",
            FontFamily = new FontFamily(_tm.MonoFont),
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _progressSpinner, _progressMs },
        };

        _progressPill = new Border
        {
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(10, 3, 10, 3),
            Margin = new Thickness(0, 0, 6, 0),
            Child = row,
            VerticalAlignment = VerticalAlignment.Center,
        };
        global::Avalonia.Automation.AutomationProperties.SetName(_progressPill, "Navigation timing");

        // Hover brightens the muted resting state back to accent. During
        // active navigation the pill is already accent, so hover is a no-op.
        ChromeKit.AttachHover(_progressPill,
            () => { if (_progressTimer is null) { ApplyPillColors(active: true); } },
            () => { if (_progressTimer is null) { ApplyPillColors(active: false); } });

        _grid.Children.Add(_progressPill);
        Grid.SetColumn(_progressPill, 2);
    }

    private void SetPillActive(bool active)
    {
        if (_progressPill is null || _progressSpinner is null)
        {
            return;
        }

        _progressSpinner.IsVisible = active;
        ApplyPillColors(active);
    }

    private void ApplyPillColors(bool active)
    {
        if (_progressPill is null || _progressMs is null)
        {
            return;
        }

        var t = _tm.Tokens;
        if (active)
        {
            _progressPill.Background = new SolidColorBrush(t.AccentBg);
            _progressMs.Foreground = new SolidColorBrush(t.Accent);
        }
        else
        {
            // Muted resting state — sits in the bar but reads as background
            // detail until the user looks for it.
            _progressPill.Background = new SolidColorBrush(t.Hover);
            _progressMs.Foreground = new SolidColorBrush(t.Faint);
        }
    }

    // ---- Connection security (lock icon + popover) -------------------------

    /// <summary>
    /// Update the lock affordance for the page just loaded: swaps the lock/shield
    /// icon and tint, and refreshes the popover content if it's open. Pass null
    /// to reset to a neutral state (e.g. about:blank).
    /// </summary>
    public void SetSecurity(SiteSecurity? security)
    {
        _security = security;
        var t = _tm.Tokens;

        var (data, color, name) = security switch
        {
            { Secure: true } => (Icons.Lock, t.Accent, "Connection is secure"),
            { Encrypted: true } => (Icons.Lock, t.Warn, "Connection encrypted (certificate unverified)"),
            { Encrypted: false } => (Icons.Shield, t.Warn, "Connection not secure"),
            _ => (Icons.Shield, t.Muted, "Connection security"),
        };
        _lockCell.Child = Icons.Make(data, color, 13);
        global::Avalonia.Automation.AutomationProperties.SetName(_lockCell, name);

        if (_securityPopup is { IsOpen: true })
        {
            _securityPopup.Child = BuildSecurityCard();
        }
    }

    private void ToggleSecurityPopover()
    {
        _securityPopup ??= CreatePopup();
        if (_securityPopup.IsOpen)
        {
            _securityPopup.IsOpen = false;
            return;
        }
        _detailsExpanded = false;
        _securityPopup.Child = BuildSecurityCard();
        _securityPopup.IsOpen = true;
    }

    private Popup CreatePopup()
    {
        var popup = new Popup
        {
            PlacementTarget = _lockCell,
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            HorizontalOffset = 2,
            VerticalOffset = 8,
        };
        // Park it in the grid so it shares our visual tree; a Popup takes no
        // layout space, so the column assignment is cosmetic.
        _grid.Children.Add(popup);
        Grid.SetColumn(popup, 0);
        return popup;
    }

    /// <summary>Test hook: build the popover card for the current security state.</summary>
    internal Control BuildSecurityCardForTest() => BuildSecurityCard();

    private Control BuildSecurityCard()
    {
        var t = _tm.Tokens;
        var s = _security;

        var rows = new StackPanel { Orientation = Orientation.Vertical, Spacing = 10 };

        // Header: icon + title.
        var secure = s is { Secure: true };
        var encrypted = s is { Encrypted: true };
        var headColor = secure ? t.Ok : encrypted ? t.Warn : t.Muted;
        var title = secure ? "Connection is secure"
            : encrypted ? "Connection encrypted"
            : s is null ? "Connection security" : "Not secure";
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            Children =
            {
                Icons.Make(secure ? Icons.Lock : Icons.Shield, headColor, 16),
                ChromeKit.Sans(_tm, title, 13.5, t.Text),
            },
        };
        ((TextBlock)header.Children[1]).FontWeight = FontWeight.SemiBold;
        ((TextBlock)header.Children[1]).VerticalAlignment = VerticalAlignment.Center;
        rows.Children.Add(header);

        var subtitle = secure
            ? "Your connection to this site is encrypted and the certificate is valid."
            : encrypted
                ? "This site is encrypted but its certificate could not be summarised."
                : s is null
                    ? "Navigate to a site to see its connection details."
                    : "This site is served over plain HTTP, so it isn't encrypted.";
        rows.Children.Add(WrapText(subtitle, t.Muted));

        rows.Children.Add(Hairline(t));

        // Protocol row.
        var protocol = string.IsNullOrEmpty(s?.Protocol) ? "—" : s!.Protocol;
        rows.Children.Add(InfoRow("Protocol", protocol));

        // Certificate status + optional expandable details.
        if (s is { Certificate: true })
        {
            rows.Children.Add(StatusRow("Certificate", "Valid", t.Ok));
            rows.Children.Add(BuildCertDetails(s, t));
        }
        else if (encrypted)
        {
            rows.Children.Add(StatusRow("Certificate", "Unverified", t.Warn));
        }
        else
        {
            rows.Children.Add(StatusRow("Certificate", "None", t.Muted));
        }

        rows.Children.Add(Hairline(t));

        // Fail-closed policy note.
        rows.Children.Add(WrapText(
            "Starling rejects sites with invalid certificates outright — there is no "
            + "click-through past a certificate error.", t.Faint, 11.5));

        return new Border
        {
            Width = 320,
            Background = new SolidColorBrush(t.Panel),
            BorderBrush = new SolidColorBrush(t.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 14, 16, 14),
            Child = rows,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetX = 0,
                OffsetY = 8,
                Blur = 28,
                Spread = -6,
                Color = Color.FromArgb(90, 0, 0, 0),
            }),
        };
    }

    private Control BuildCertDetails(SiteSecurity s, ThemeTokens t)
    {
        var details = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 6,
            IsVisible = _detailsExpanded,
            Margin = new Thickness(0, 2, 0, 0),
        };
        details.Children.Add(InfoRow("Issued to", s.CertSubject ?? "—"));
        details.Children.Add(InfoRow("Issued by", s.CertIssuer ?? "—"));
        details.Children.Add(InfoRow("Valid from", FormatDate(s.CertNotBefore)));
        details.Children.Add(InfoRow("Valid to", FormatDate(s.CertNotAfter)));

        var chevron = Icons.Make(_detailsExpanded ? Icons.TriDown : Icons.TriRight, t.Muted, 12);
        var label = ChromeKit.Sans(_tm, _detailsExpanded ? "Hide details" : "Show details", 12, t.Accent);
        var toggle = new Border
        {
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(0, 2, 0, 0),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 5,
                Children = { chevron, label },
            },
        };
        ChromeKit.AttachClick(toggle, () =>
        {
            _detailsExpanded = !_detailsExpanded;
            details.IsVisible = _detailsExpanded;
            chevron.Data = Geometry.Parse(_detailsExpanded ? Icons.TriDown : Icons.TriRight);
            label.Text = _detailsExpanded ? "Hide details" : "Show details";
        });

        return new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 4,
            Children = { toggle, details },
        };
    }

    private Control InfoRow(string label, string value)
    {
        var t = _tm.Tokens;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*") };
        var l = ChromeKit.Sans(_tm, label, 12, t.Muted);
        l.VerticalAlignment = VerticalAlignment.Top;
        var v = new TextBlock
        {
            Text = value,
            FontFamily = new FontFamily(_tm.MonoFont),
            FontSize = 12,
            Foreground = new SolidColorBrush(t.Text),
            TextWrapping = TextWrapping.Wrap,
        };
        grid.Children.Add(l); Grid.SetColumn(l, 0);
        grid.Children.Add(v); Grid.SetColumn(v, 1);
        return grid;
    }

    private Control StatusRow(string label, string status, Color statusColor)
    {
        var t = _tm.Tokens;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*") };
        var l = ChromeKit.Sans(_tm, label, 12, t.Muted);
        var chip = new Border
        {
            Background = new SolidColorBrush(WithAlpha(statusColor, 34)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 1, 7, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = ChromeKit.Sans(_tm, status, 11.5, statusColor),
        };
        grid.Children.Add(l); Grid.SetColumn(l, 0);
        grid.Children.Add(chip); Grid.SetColumn(chip, 1);
        return grid;
    }

    private TextBlock WrapText(string text, Color color, double size = 12) => new()
    {
        Text = text,
        FontFamily = new FontFamily(_tm.SansFont),
        FontSize = size,
        Foreground = new SolidColorBrush(color),
        TextWrapping = TextWrapping.Wrap,
    };

    private static Border Hairline(ThemeTokens t) => new()
    {
        Height = 1,
        Background = new SolidColorBrush(t.Hair),
        Margin = new Thickness(0, 1, 0, 1),
    };

    private static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    private static string FormatDate(DateTimeOffset? d) =>
        d is { } dt ? dt.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture) : "—";
}

/// <summary>
/// Presentation view of a page's connection security for the lock popover.
/// Mapped from the engine's <c>LaidOutPage.Security</c> by the shell.
/// </summary>
public sealed record SiteSecurity(
    bool Encrypted,
    bool Secure,
    string Protocol,
    bool Certificate,
    string? CertSubject,
    string? CertIssuer,
    DateTimeOffset? CertNotBefore,
    DateTimeOffset? CertNotAfter);

internal static class TokensExt
{
    /// <summary>The hairline-rule color used inside the URL bar dividers.</summary>
    public static Color Rule(this ThemeTokens t) => t.Border;
}
