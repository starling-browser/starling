using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Starling.Paint.Svg;

/// <summary>
/// Parses SVG/CSS color values into ImageSharp <see cref="Color"/>s. Supports
/// the subset real-world icons use: <c>#rgb</c> / <c>#rrggbb</c> hex,
/// <c>rgb()</c> / <c>rgba()</c> functional notation, the CSS named colors,
/// <c>currentColor</c> (resolved against a supplied current color), and
/// <c>none</c> / <c>transparent</c>.
/// </summary>
/// <remarks>
/// Supports hex, <c>rgb()</c>/<c>rgba()</c>, <c>hsl()</c>/<c>hsla()</c>,
/// the CSS named colors, <c>currentColor</c>, and <c>none</c>/<c>transparent</c>.
/// Modern <c>color()</c> spaces and system colors are not supported.
/// </remarks>
internal static class SvgColor
{
    /// <summary>
    /// Try to parse <paramref name="value"/> into a paint.
    /// <paramref name="isNone"/> is set when the value is <c>none</c> (no paint
    /// should be applied); in that case <paramref name="color"/> is undefined.
    /// </summary>
    public static bool TryParse(string? value, Color currentColor, out Color color, out bool isNone)
    {
        color = default;
        isNone = false;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var s = value.Trim();

        if (s.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            isNone = true;
            return true;
        }
        if (s.Equals("transparent", StringComparison.OrdinalIgnoreCase))
        {
            color = Color.Transparent;
            return true;
        }
        if (s.Equals("currentColor", StringComparison.OrdinalIgnoreCase))
        {
            color = currentColor;
            return true;
        }

        if (s.StartsWith('#'))
        {
            return TryParseHex(s, out color);
        }

        if (s.StartsWith("hsl", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseHslFunc(s, out color);
        }

        if (s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseRgbFunc(s, out color);
        }

        return TryParseNamed(s, out color);
    }

    private static bool TryParseHex(string s, out Color color)
    {
        color = default;
        var hex = s[1..];
        // Strip an alpha-bearing 4/8-digit form down too — support #rgb, #rgba,
        // #rrggbb, #rrggbbaa.
        byte r, g, b, a = 255;
        switch (hex.Length)
        {
            case 3:
                if (!TryNibble(hex[0], out r) || !TryNibble(hex[1], out g) || !TryNibble(hex[2], out b))
                {
                    return false;
                }

                r = (byte)(r * 17); g = (byte)(g * 17); b = (byte)(b * 17);
                break;
            case 4:
                if (!TryNibble(hex[0], out r) || !TryNibble(hex[1], out g) ||
                    !TryNibble(hex[2], out b) || !TryNibble(hex[3], out a))
                {
                    return false;
                }

                r = (byte)(r * 17); g = (byte)(g * 17); b = (byte)(b * 17); a = (byte)(a * 17);
                break;
            case 6:
                if (!TryByte(hex, 0, out r) || !TryByte(hex, 2, out g) || !TryByte(hex, 4, out b))
                {
                    return false;
                }

                break;
            case 8:
                if (!TryByte(hex, 0, out r) || !TryByte(hex, 2, out g) ||
                    !TryByte(hex, 4, out b) || !TryByte(hex, 6, out a))
                {
                    return false;
                }

                break;
            default:
                return false;
        }
        color = Color.FromPixel(new Rgba32(r, g, b, a));
        return true;
    }

    private static bool TryParseRgbFunc(string s, out Color color)
    {
        color = default;
        int open = s.IndexOf('(');
        int close = s.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            return false;
        }

        var inner = s.Substring(open + 1, close - open - 1);
        // Accept comma- or space-separated components, optional "/" before alpha.
        var parts = inner.Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        if (!TryChannel(parts[0], out byte r) ||
            !TryChannel(parts[1], out byte g) ||
            !TryChannel(parts[2], out byte b))
        {
            return false;
        }

        byte a = 255;
        if (parts.Length == 4 && !TryAlpha(parts[3], out a))
        {
            return false;
        }

        color = Color.FromPixel(new Rgba32(r, g, b, a));
        return true;
    }

    private static bool TryChannel(string p, out byte value)
    {
        value = 0;
        if (p.EndsWith('%'))
        {
            if (!float.TryParse(p[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                return false;
            }

            value = (byte)Math.Clamp((int)Math.Round(pct / 100f * 255f), 0, 255);
            return true;
        }
        if (!float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return false;
        }

        value = (byte)Math.Clamp((int)Math.Round(v), 0, 255);
        return true;
    }

    private static bool TryAlpha(string p, out byte value)
    {
        value = 255;
        if (p.EndsWith('%'))
        {
            if (!float.TryParse(p[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
            {
                return false;
            }

            value = (byte)Math.Clamp((int)Math.Round(pct / 100f * 255f), 0, 255);
            return true;
        }
        if (!float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return false;
        }

        value = (byte)Math.Clamp((int)Math.Round(v * 255f), 0, 255);
        return true;
    }

    private static bool TryNibble(char c, out byte value)
    {
        value = 0;
        if (c is >= '0' and <= '9') { value = (byte)(c - '0'); return true; }
        if (c is >= 'a' and <= 'f') { value = (byte)(c - 'a' + 10); return true; }
        if (c is >= 'A' and <= 'F') { value = (byte)(c - 'A' + 10); return true; }
        return false;
    }

    private static bool TryByte(string hex, int offset, out byte value)
    {
        value = 0;
        if (!TryNibble(hex[offset], out var hi) || !TryNibble(hex[offset + 1], out var lo))
        {
            return false;
        }

        value = (byte)((hi << 4) | lo);
        return true;
    }

    /// <summary>
    /// Parse <c>hsl(h, s%, l%)</c> / <c>hsla(h, s%, l%, a)</c> into an RGB color.
    /// Hue is in degrees; saturation and lightness are percentages.
    /// CSS Color 4 §4.2.
    /// </summary>
    private static bool TryParseHslFunc(string s, out Color color)
    {
        color = default;
        int open = s.IndexOf('(');
        int close = s.LastIndexOf(')');
        if (open < 0 || close < open)
        {
            return false;
        }

        var inner = s.Substring(open + 1, close - open - 1);
        // Accept comma- or space-separated components, optional "/" before alpha.
        var parts = inner.Split([',', ' ', '/'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 4)
        {
            return false;
        }

        // Hue: degrees (no unit required, but deg suffix is allowed).
        var hStr = parts[0].Trim();
        if (hStr.EndsWith("deg", StringComparison.OrdinalIgnoreCase))
        {
            hStr = hStr[..^3].Trim();
        }

        if (!float.TryParse(hStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var hDeg))
        {
            return false;
        }

        // Saturation and lightness as percentages.
        var sStr = parts[1].Trim().TrimEnd('%');
        var lStr = parts[2].Trim().TrimEnd('%');
        if (!float.TryParse(sStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var sPct) ||
            !float.TryParse(lStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var lPct))
        {
            return false;
        }

        byte a = 255;
        if (parts.Length == 4 && !TryAlpha(parts[3], out a))
        {
            return false;
        }

        var (r, g, b) = HslToRgb(hDeg, sPct / 100f, lPct / 100f);
        color = Color.FromPixel(new Rgba32(r, g, b, a));
        return true;
    }

    /// <summary>
    /// Convert HSL to RGB. CSS Color 4 §4.2 algorithm.
    /// H is in degrees (any value, normalized mod 360), S and L are [0,1].
    /// </summary>
    private static (byte R, byte G, byte B) HslToRgb(float h, float s, float l)
    {
        s = Math.Clamp(s, 0f, 1f);
        l = Math.Clamp(l, 0f, 1f);

        if (s == 0f)
        {
            var grey = (byte)Math.Clamp((int)Math.Round(l * 255f), 0, 255);
            return (grey, grey, grey);
        }

        float q = l < 0.5f ? l * (1f + s) : l + s - l * s;
        float p = 2f * l - q;

        h = ((h % 360f) + 360f) % 360f; // normalise to [0, 360)
        float hNorm = h / 360f;

        float r = HueToRgb(p, q, hNorm + 1f / 3f);
        float g = HueToRgb(p, q, hNorm);
        float b = HueToRgb(p, q, hNorm - 1f / 3f);

        return (
            (byte)Math.Clamp((int)Math.Round(r * 255f), 0, 255),
            (byte)Math.Clamp((int)Math.Round(g * 255f), 0, 255),
            (byte)Math.Clamp((int)Math.Round(b * 255f), 0, 255)
        );
    }

    private static float HueToRgb(float p, float q, float t)
    {
        if (t < 0f)
        {
            t += 1f;
        }

        if (t > 1f)
        {
            t -= 1f;
        }

        if (t < 1f / 6f)
        {
            return p + (q - p) * 6f * t;
        }

        if (t < 1f / 2f)
        {
            return q;
        }

        if (t < 2f / 3f)
        {
            return p + (q - p) * (2f / 3f - t) * 6f;
        }

        return p;
    }

    private static bool TryParseNamed(string s, out Color color)
        // ImageSharp's Color.TryParse resolves the CSS/SVG named colors
        // (e.g. "red", "cornflowerblue", "darkslategray") as well as hex.
        => Color.TryParse(s, out color);
}
