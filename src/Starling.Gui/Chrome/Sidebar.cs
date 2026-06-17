using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Starling.Gui.Theme;

namespace Starling.Gui.Chrome;

/// <summary>Runtime build facts shown in the sidebar footer: the build's git
/// commit (short SHA) and which JS / render engines this process is running.</summary>
public sealed record BuildInfo(string? Commit, string? JsEngine, string? RenderEngine, string? HtmlParser);

/// <summary>
/// 232px vertical bookmark sidebar — calm-modern redesign. A small gradient
/// app-mark + "Starling" wordmark at top, a single Bookmarks section with
/// deterministic-color favicon tiles, and a footer column of build facts
/// (commit / JS engine / render engine).
/// </summary>
public sealed class Sidebar : Grid
{
    public const double WidthDip = 232;

    // Soft, calm tile colors — assigned deterministically per host. None of
    // these are eye-grabbing; they sit politely in the sidebar.
    private static readonly Color[] TileColors =
    [
        Color.FromRgb(0xE0, 0x7A, 0x55), // warm orange
        Color.FromRgb(0x4A, 0x8A, 0x78), // teal-green
        Color.FromRgb(0x6E, 0x7F, 0xC6), // periwinkle
        Color.FromRgb(0xD1, 0x8A, 0x3D), // gold
        Color.FromRgb(0x9D, 0x6F, 0xB5), // heather
        Color.FromRgb(0xC2, 0x5E, 0x7A), // rose
        Color.FromRgb(0x5A, 0x8A, 0x5A), // sage
    ];

    public Sidebar(
        ThemeManager tm,
        IReadOnlyList<TabInfo> bookmarks,
        string? activeId,
        Action<TabInfo>? onTabActivated = null,
        BuildInfo? build = null)
    {
        var t = tm.Tokens;

        Width = WidthDip;
        Background = new SolidColorBrush(t.Panel);
        RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto");

        var wordmark = BuildWordmark(tm);
        Children.Add(wordmark); SetRow(wordmark, 0);

        var sectionLabel = BuildSectionLabel(tm, "Bookmarks", bookmarks.Count);
        Children.Add(sectionLabel); SetRow(sectionLabel, 1);

        var list = BuildList(tm, bookmarks, activeId, onTabActivated);
        Children.Add(list); SetRow(list, 2);

        var footer = BuildFooter(tm, build);
        Children.Add(footer); SetRow(footer, 3);

        // Right-edge hairline rules the whole height
        var edge = new Rectangle
        {
            Fill = new SolidColorBrush(t.Rule()),
            Width = 1,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Children.Add(edge);
        SetRow(edge, 0); SetRowSpan(edge, 4);
    }

    private static Control BuildWordmark(ThemeManager tm)
    {
        var t = tm.Tokens;

        // Gradient app-mark — only spot of brand color in the chrome.
        var markBg = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(7),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(t.Accent, 0),
                    new GradientStop(t.Accent2, 1),
                },
            },
            Child = Icons.Make("M3 11 L7 5 L10 9 L13 4", Colors.White, 13, 1.6),
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = 2,
                OffsetX = 0,
                OffsetY = 1,
                Color = Color.FromArgb(0x4D, t.Accent.R, t.Accent.G, t.Accent.B),
            }),
        };

        var name = new TextBlock
        {
            Text = "Starling",
            FontFamily = new FontFamily(tm.SansFont),
            FontSize = 15.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(t.Text),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 9,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { markBg, name },
        };

        return new Border
        {
            // On macOS the native traffic lights overlap the sidebar's
            // top-left corner; bump the top padding so the wordmark sits
            // clear of them. Other platforms use the original 16px inset.
            Padding = new Thickness(18, OperatingSystem.IsMacOS() ? 36 : 16, 18, 16),
            Child = row,
        };
    }

    private static Control BuildSectionLabel(ThemeManager tm, string text, int count)
    {
        var t = tm.Tokens;
        var label = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily(tm.SansFont),
            FontSize = 11,
            FontWeight = FontWeight.Medium,
            Foreground = new SolidColorBrush(t.Muted),
        };
        var countLabel = new TextBlock
        {
            Text = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            FontFamily = new FontFamily(tm.MonoFont),
            FontSize = 10,
            Foreground = new SolidColorBrush(t.Faint),
        };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(label); Grid.SetColumn(label, 0);
        grid.Children.Add(countLabel); Grid.SetColumn(countLabel, 1);

        return new Border
        {
            Padding = new Thickness(22, 8, 22, 6),
            Child = grid,
        };
    }

    private static Control BuildList(
        ThemeManager tm,
        IReadOnlyList<TabInfo> bookmarks,
        string? activeId,
        Action<TabInfo>? onTabActivated)
    {
        var stack = new StackPanel { Spacing = 1, Margin = new Thickness(10, 0, 10, 8) };
        foreach (var tab in bookmarks)
        {
            stack.Children.Add(BuildRow(tm, tab, tab.Id == activeId, onTabActivated));
        }

        return new ScrollViewer
        {
            Content = stack,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        };
    }

    private static Control BuildRow(ThemeManager tm, TabInfo tab, bool active, Action<TabInfo>? onTabActivated)
    {
        var t = tm.Tokens;

        Control icon = tab.Loading ? Spinner(t) : FaviconTile(tm, tab.Host);

        var title = new TextBlock
        {
            Text = tab.Title,
            FontFamily = new FontFamily(tm.SansFont),
            FontSize = 13,
            FontWeight = active ? FontWeight.Medium : FontWeight.Normal,
            Foreground = new SolidColorBrush(active ? t.Text : t.Text2),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var grid = new Grid
        {
            ColumnSpacing = 11,
            VerticalAlignment = VerticalAlignment.Center,
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
        };
        grid.Children.Add(icon); Grid.SetColumn(icon, 0);
        grid.Children.Add(title); Grid.SetColumn(title, 1);

        if (tab.Audio)
        {
            var dot = ChromeKit.Dot(t.Accent);
            grid.Children.Add(dot); Grid.SetColumn(dot, 2);
        }

        var row = new Border
        {
            Padding = new Thickness(10, 8),
            Background = new SolidColorBrush(active ? t.Surface : Colors.Transparent),
            BorderBrush = new SolidColorBrush(active ? t.Hair : Colors.Transparent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            BoxShadow = active
                ? new BoxShadows(new BoxShadow
                {
                    Blur = 2,
                    OffsetX = 0,
                    OffsetY = 1,
                    Color = Color.FromArgb(0x14, 0, 0, 0),
                })
                : default,
            Child = grid,
        };
        global::Avalonia.Automation.AutomationProperties.SetName(row, $"Bookmark: {tab.Title}");

        if (!active)
        {
            ChromeKit.AttachHover(row,
                () => row.Background = new SolidColorBrush(t.Hover),
                () => row.Background = new SolidColorBrush(Colors.Transparent));
        }

        if (onTabActivated is not null && !string.IsNullOrWhiteSpace(tab.Url))
        {
            ChromeKit.AttachClick(row, () => onTabActivated(tab));
        }
        return row;
    }

    /// <summary>16×16 rounded-square tile with a single capital letter on a
    /// deterministic-from-host color background.</summary>
    private static Control FaviconTile(ThemeManager tm, string host)
    {
        var color = TileColorFor(host);
        var letter = LetterFor(host);
        return new Border
        {
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(color),
            Child = new TextBlock
            {
                Text = letter,
                FontFamily = new FontFamily(tm.SansFont),
                FontSize = 9,
                FontWeight = FontWeight.SemiBold,
                Foreground = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static Color TileColorFor(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return TileColors[0];
        }
        // Cheap deterministic hash — sum of bytes, mod palette length.
        var sum = 0;
        foreach (var ch in host)
        {
            sum += ch;
        }

        return TileColors[Math.Abs(sum) % TileColors.Length];
    }

    private static string LetterFor(string host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return "?";
        }

        var trimmed = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        if (string.IsNullOrEmpty(trimmed))
        {
            return "?";
        }

        return trimmed[..1].ToUpperInvariant();
    }

    /// <summary>1.5px accent ring stand-in for a loading spinner.</summary>
    private static Control Spinner(ThemeTokens t) => new Ellipse
    {
        Width = 16,
        Height = 16,
        Stroke = new SolidColorBrush(t.Accent),
        StrokeThickness = 1.5,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Footer is a small column of build facts: commit / JS engine / render
    // engine / HTML parser, each a faint label paired with a monospace value.
    private static Control BuildFooter(ThemeManager tm, BuildInfo? build)
    {
        var t = tm.Tokens;

        var rows = new StackPanel { Spacing = 3 };
        rows.Children.Add(FooterRow(tm, "commit", build?.Commit));
        rows.Children.Add(FooterRow(tm, "js", build?.JsEngine));
        rows.Children.Add(FooterRow(tm, "render", build?.RenderEngine));
        rows.Children.Add(FooterRow(tm, "html", build?.HtmlParser));

        var content = new Border
        {
            Padding = new Thickness(20, 12, 20, 14),
            Child = rows,
        };

        var wrap = new Grid();
        var hairline = new Rectangle
        {
            Fill = new SolidColorBrush(t.Rule()),
            Height = 1,
            VerticalAlignment = VerticalAlignment.Top,
        };
        wrap.Children.Add(hairline);
        wrap.Children.Add(content);
        return wrap;
    }

    private static Control FooterRow(ThemeManager tm, string label, string? value)
    {
        var t = tm.Tokens;
        var key = new TextBlock
        {
            Text = label,
            FontFamily = new FontFamily(tm.SansFont),
            FontSize = 10,
            Foreground = new SolidColorBrush(t.Faint),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var val = new TextBlock
        {
            Text = string.IsNullOrEmpty(value) ? "—" : value,
            FontFamily = new FontFamily(tm.MonoFont),
            FontSize = 10.5,
            Foreground = new SolidColorBrush(t.Muted),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        grid.Children.Add(key); SetColumn(key, 0);
        grid.Children.Add(val); SetColumn(val, 1);
        return grid;
    }
}
