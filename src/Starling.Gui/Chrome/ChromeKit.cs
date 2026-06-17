using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Starling.Gui.Theme;

namespace Starling.Gui.Chrome;

/// <summary>
/// Shared Avalonia chrome atoms. All helpers read colours and metrics from the
/// supplied <see cref="ThemeManager"/> at build time. A theme flip plus a tree
/// rebuild keeps everything in sync.
/// </summary>
public static class ChromeKit
{
    /// <summary>The .pill utility — a rounded accent-tinted capsule.</summary>
    public static Border Pill(ThemeManager tm, params Control[] children)
    {
        var t = tm.Tokens;
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var c in children)
        {
            stack.Children.Add(c);
        }

        return new Border
        {
            Background = new SolidColorBrush(t.AccentBg),
            BorderBrush = new SolidColorBrush(t.AccentLine),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(tm.Metrics.RPill),
            Padding = new Thickness(10, 4),
            Child = stack,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    /// <summary>A 6px accent dot — the .pill .dot / audio-tab indicator.</summary>
    public static Border Dot(Color color, double size = 6) => new()
    {
        Width = size,
        Height = size,
        Background = new SolidColorBrush(color),
        CornerRadius = new CornerRadius(size / 2),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A 1px hairline divider — .hr / .vr.</summary>
    public static Border Hairline(ThemeManager tm, bool vertical = false) => new()
    {
        Background = new SolidColorBrush(tm.Tokens.Border),
        Width = vertical ? 1 : double.NaN,
        Height = vertical ? double.NaN : 1,
        HorizontalAlignment = vertical ? HorizontalAlignment.Center : HorizontalAlignment.Stretch,
        VerticalAlignment = vertical ? VerticalAlignment.Stretch : VerticalAlignment.Center,
    };

    /// <summary>A chrome (sans) label bound to the active type mode.</summary>
    public static TextBlock Sans(ThemeManager tm, string text, double fontSize, Color color,
        FontWeight weight = FontWeight.Normal) => new()
        {
            Text = text,
            FontFamily = string.IsNullOrEmpty(tm.ChromeFont) ? FontFamily.Default : new FontFamily(tm.ChromeFont),
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = new SolidColorBrush(color),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>A monospace label — timestamps, paths, sizes, code.</summary>
    public static TextBlock Mono(ThemeManager tm, string text, double fontSize, Color color,
        FontWeight weight = FontWeight.Normal) => new()
        {
            Text = text,
            FontFamily = new FontFamily(tm.MonoFont),
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = new SolidColorBrush(color),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>
    /// Wires pointer enter/exit on a control so hover styling can be applied.
    /// Uses Avalonia's PointerEntered / PointerExited events directly.
    /// </summary>
    public static void AttachHover(Control control, Action onEnter, Action onExit)
    {
        control.PointerEntered += (_, _) => onEnter();
        control.PointerExited += (_, _) => onExit();
    }

    /// <summary>
    /// Wires a left-button click to a control by listening on PointerReleased.
    /// </summary>
    public static void AttachClick(Control control, Action onClick)
    {
        control.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                onClick();
            }
        };
    }
}
