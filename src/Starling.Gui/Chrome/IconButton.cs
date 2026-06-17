using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Starling.Gui.Theme;

namespace Starling.Gui.Chrome;

/// <summary>
/// A square icon button. Avalonia port of Starling.Gui's Chrome/IconButton.cs —
/// a Border with hover background, optional accent-tinted "on" state, and a
/// dimmed disabled state that also swallows taps.
/// </summary>
public sealed class IconButton : Border
{
    private readonly ThemeTokens _t;
    private readonly bool _on;
    private bool _enabled = true;

    public event EventHandler? Clicked;

    public IconButton(ThemeManager tm, string iconData, string label,
        bool isOn = false, double? size = null)
    {
        _t = tm.Tokens;
        var box = size ?? 34;
        _on = isOn;

        Width = box;
        Height = box;
        Background = new SolidColorBrush(isOn ? _t.AccentBg : Colors.Transparent);
        BorderThickness = new Thickness(0);
        CornerRadius = new CornerRadius(9);
        Child = Icons.Make(iconData, isOn ? _t.Accent : _t.Text2, 17);
        global::Avalonia.Automation.AutomationProperties.SetName(this, label);

        ChromeKit.AttachClick(this, () => { if (_enabled) { Clicked?.Invoke(this, EventArgs.Empty); } });
        ChromeKit.AttachHover(this,
            () => { if (_enabled && !_on) { Background = new SolidColorBrush(_t.Hover); } },
            () => { if (!_on) { Background = new SolidColorBrush(Colors.Transparent); } });
    }

    /// <summary>Dims the button and stops it raising <see cref="Clicked"/>.</summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        Opacity = enabled ? 1.0 : 0.35;
    }

    /// <summary>Test hook: raises <see cref="Clicked"/> as a tap would (honors the disabled state).</summary>
    internal void RaiseClickForTest()
    {
        if (_enabled)
        {
            Clicked?.Invoke(this, EventArgs.Empty);
        }
    }
}
