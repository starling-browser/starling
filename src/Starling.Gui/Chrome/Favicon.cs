using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Starling.Gui.Theme;

namespace Starling.Gui.Chrome;

/// <summary>
/// Synthetic favicon — Avalonia port of Starling.Gui's Chrome/Favicon.cs.
/// Small rounded square tinted by a deterministic hash of the host with the
/// host's first letter in white. Stable across reloads.
/// </summary>
public static class Favicon
{
    public static Border Make(ThemeManager tm, string host, double size = 12)
    {
        var hue = HostHue(host);
        var bg = OklchToColor(0.65, 0.13, hue);

        var letter = new TextBlock
        {
            Text = InitialOf(host),
            FontFamily = new FontFamily(tm.MonoFont),
            FontSize = size * 0.62,
            FontWeight = FontWeight.Bold,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
        };

        return new Border
        {
            Width = size,
            Height = size,
            Background = new SolidColorBrush(bg),
            CornerRadius = new CornerRadius(4),
            Child = letter,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static string InitialOf(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return "?";
        }

        var h = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        return h.Length == 0 ? "?" : char.ToUpperInvariant(h[0]).ToString();
    }

    private static int HostHue(string? host)
    {
        var h = 0;
        if (host is null)
        {
            return 0;
        }

        foreach (var ch in host)
        {
            h = (h * 31 + ch) % 360;
        }

        return h;
    }

    /// <summary>OKLCH → sRGB via Björn Ottosson's OKLab matrices.</summary>
    private static Color OklchToColor(double l, double c, double hueDegrees)
    {
        var hr = hueDegrees * Math.PI / 180.0;
        var a = c * Math.Cos(hr);
        var b = c * Math.Sin(hr);

        var lp = l + 0.3963377774 * a + 0.2158037573 * b;
        var mp = l - 0.1055613458 * a - 0.0638541728 * b;
        var sp = l - 0.0894841775 * a - 1.2914855480 * b;

        var lc = lp * lp * lp;
        var mc = mp * mp * mp;
        var sc = sp * sp * sp;

        var r = 4.0767416621 * lc - 3.3077115913 * mc + 0.2309699292 * sc;
        var g = -1.2684380046 * lc + 2.6097574011 * mc - 0.3413193965 * sc;
        var bl = -0.0041960863 * lc - 0.7034186147 * mc + 1.7076147010 * sc;

        return Color.FromRgb(
            (byte)Math.Round(GammaEncode(r) * 255),
            (byte)Math.Round(GammaEncode(g) * 255),
            (byte)Math.Round(GammaEncode(bl) * 255));
    }

    private static double GammaEncode(double linear)
    {
        var v = linear <= 0.0031308
            ? linear * 12.92
            : 1.055 * Math.Pow(linear, 1.0 / 2.4) - 0.055;
        return Math.Clamp(v, 0.0, 1.0);
    }
}
