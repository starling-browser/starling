// SPDX-License-Identifier: Apache-2.0
using Starling.Css.Values;

namespace Starling.Paint.Gradients;

/// <summary>
/// Renderer-neutral CSS Color 4 gradient color-space math (CSS Images 4 §3.1,
/// CSS Color 4 §12): convert sRGB stop colors into an interpolation space, blend,
/// and convert back. Pure <c>double</c> math with no rasterizer dependency, so
/// any paint backend (ImageSharp today, a GPU backend later) can interpolate
/// gradient stops identically on the CPU. Extracted verbatim from the ImageSharp
/// adapter so the two backends produce byte-identical gradient output.
/// </summary>
internal static class GradientInterpolation
{
    public static (double ch0, double ch1, double ch2) SrgbToSpace(double r, double g, double b, GradientColorSpace cs)
    {
        return cs switch
        {
            GradientColorSpace.Srgb => (r, g, b),
            GradientColorSpace.SrgbLinear => (SrgbToLinear01(r), SrgbToLinear01(g), SrgbToLinear01(b)),
            GradientColorSpace.Oklab or GradientColorSpace.Oklch => ToOklab(r, g, b),
            GradientColorSpace.Hsl or GradientColorSpace.Hwb => RgbToHsl(r, g, b),
            GradientColorSpace.Lab or GradientColorSpace.Lch => ToLab(r, g, b),
            // Display-P3, A98-RGB, ProPhoto, Rec2020, XYZ spaces: treat as sRGB
            // (same as the linear/radial brush fallback).
            // sRGB fallback — these wide-gamut spaces need a matrix transform that
            // is out of scope for this paint pass; interpolation happens in sRGB.
            _ => (r, g, b),
        };
    }

    /// <summary>
    /// Convert from an interpolation color space back to sRGB (0..1 each).
    /// </summary>
    public static (double r, double g, double b) SpaceToSrgb(double c0, double c1, double c2, GradientColorSpace cs)
    {
        return cs switch
        {
            GradientColorSpace.Srgb => (c0, c1, c2),
            GradientColorSpace.SrgbLinear => (LinearToSrgb01(c0), LinearToSrgb01(c1), LinearToSrgb01(c2)),
            GradientColorSpace.Oklab => OklabToSrgb(c0, c1, c2),
            GradientColorSpace.Oklch => OklabToSrgb(c0, c1 * Math.Cos(c2 * Math.PI / 180.0), c1 * Math.Sin(c2 * Math.PI / 180.0)),
            GradientColorSpace.Hsl => HslToRgb(c0, c1, c2),
            GradientColorSpace.Hwb => HwbToRgb(c0, c1, c2),
            GradientColorSpace.Lab => LabToSrgb(c0, c1, c2),
            GradientColorSpace.Lch => LabToSrgb(c0, c1 * Math.Cos(c2 * Math.PI / 180.0), c1 * Math.Sin(c2 * Math.PI / 180.0)),
            _ => (c0, c1, c2), // sRGB fallback for wide-gamut spaces
        };
    }

    public static bool IsPolarSpace(GradientColorSpace cs)
        => cs is GradientColorSpace.Oklch or GradientColorSpace.Hsl
            or GradientColorSpace.Hwb or GradientColorSpace.Lch;

    public static double InterpolateHue(double h0, double h1, double f, HueInterpolationMethod method)
    {
        // Normalize both angles to [0, 360).
        h0 = ((h0 % 360) + 360) % 360;
        h1 = ((h1 % 360) + 360) % 360;
        var diff = h1 - h0;

        switch (method)
        {
            case HueInterpolationMethod.Shorter:
                if (diff > 180)
                {
                    diff -= 360;
                }
                else if (diff < -180)
                {
                    diff += 360;
                }

                break;
            case HueInterpolationMethod.Longer:
                if (diff is > (-180) and < 0)
                {
                    diff += 360;
                }
                else if (diff is > 0 and < 180)
                {
                    diff -= 360;
                }

                break;
            case HueInterpolationMethod.Increasing:
                if (diff < 0)
                {
                    diff += 360;
                }

                break;
            case HueInterpolationMethod.Decreasing:
                if (diff > 0)
                {
                    diff -= 360;
                }

                break;
        }
        return h0 + diff * f;
    }

    // --- Oklab color space conversions ---

    /// <summary>
    /// sRGB → Oklab. CSS Color 4 §10.9 (Oklab is defined by Björn Ottosson;
    /// the CSS spec adopts it). The conversion goes sRGB → linear sRGB →
    /// XYZ D65 (via the sRGB-to-XYZ matrix) → Oklab LMS (via the M1 matrix) →
    /// Oklab (via cube root + M2). Returns (L, a, b) where L ∈ [0,1], a/b ∈ [-0.5,0.5].
    /// </summary>
    public static (double L, double a, double b) ToOklab(double r, double g, double bv)
    {
        // sRGB → linear sRGB.
        var rl = SrgbToLinear01(r);
        var gl = SrgbToLinear01(g);
        var bl = SrgbToLinear01(bv);

        // linear sRGB → XYZ D65 (IEC 61966-2-1 matrix, row-major):
        // [0.4124564, 0.3575761, 0.1804375]
        // [0.2126729, 0.7151522, 0.0721750]
        // [0.0193339, 0.1191920, 0.9503041]
        // Then XYZ → LMS via the Oklab M1 matrix. These two multiplied inline:
        var l = 0.4122214708 * rl + 0.5363325363 * gl + 0.0514459929 * bl;
        var m = 0.2119034982 * rl + 0.6806995451 * gl + 0.1073969566 * bl;
        var s = 0.0883024619 * rl + 0.2817188376 * gl + 0.6299787005 * bl;

        var lc = Math.Cbrt(Math.Max(0, l));
        var mc = Math.Cbrt(Math.Max(0, m));
        var sc = Math.Cbrt(Math.Max(0, s));

        return (
            0.2104542553 * lc + 0.7936177850 * mc - 0.0040720468 * sc,
            1.9779984951 * lc - 2.4285922050 * mc + 0.4505937099 * sc,
            0.0259040371 * lc + 0.7827717662 * mc - 0.8086757660 * sc);
    }

    public static (double r, double g, double b) OklabToSrgb(double L, double a, double bv)
    {
        var lc = L + 0.3963377774 * a + 0.2158037573 * bv;
        var mc = L - 0.1055613458 * a - 0.0638541728 * bv;
        var sc = L - 0.0894841775 * a - 1.2914855480 * bv;

        var l = lc * lc * lc;
        var m = mc * mc * mc;
        var s = sc * sc * sc;

        var rl = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        var gl = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        var bl = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;

        return (LinearToSrgb01(rl), LinearToSrgb01(gl), LinearToSrgb01(bl));
    }

    // --- HSL / HWB conversions ---

    public static (double h, double s, double l) RgbToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;
        double h = 0, s = 0;
        var d = max - min;
        if (d > 1e-10)
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r)
            {
                h = (g - b) / d + (g < b ? 6 : 0);
            }
            else if (max == g)
            {
                h = (b - r) / d + 2;
            }
            else
            {
                h = (r - g) / d + 4;
            }

            h *= 60;
        }
        return (h, s, l);
    }

    public static (double r, double g, double b) HslToRgb(double h, double s, double l)
    {
        static double Hue2Rgb(double p, double q, double t)
        {
            t = ((t % 1.0) + 1.0) % 1.0;
            if (t < 1.0 / 6)
            {
                return p + (q - p) * 6 * t;
            }

            if (t < 1.0 / 2)
            {
                return q;
            }

            if (t < 2.0 / 3)
            {
                return p + (q - p) * (2.0 / 3 - t) * 6;
            }

            return p;
        }
        if (s < 1e-10)
        {
            return (l, l, l);
        }

        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        var hN = h / 360.0;
        return (Hue2Rgb(p, q, hN + 1.0 / 3), Hue2Rgb(p, q, hN), Hue2Rgb(p, q, hN - 1.0 / 3));
    }

    public static (double r, double g, double b) HwbToRgb(double h, double w, double bk)
    {
        var wn = w + bk;
        if (wn > 1.0) { w /= wn; bk /= wn; }
        var (r, g, b) = HslToRgb(h, 1.0, 0.5); // pure hue
        r = r * (1 - w - bk) + w;
        g = g * (1 - w - bk) + w;
        b = b * (1 - w - bk) + w;
        return (r, g, b);
    }

    // --- CIELab conversions (D50 white point to match CSS spec) ---

    public static (double L, double a, double b) ToLab(double r, double g, double bv)
    {
        // sRGB → linear sRGB → XYZ D65 → XYZ D50 (Bradford) → Lab.
        var rl = SrgbToLinear01(r);
        var gl = SrgbToLinear01(g);
        var bl = SrgbToLinear01(bv);

        // sRGB (linear) → XYZ D65.
        var xd65 = 0.4124564 * rl + 0.3575761 * gl + 0.1804375 * bl;
        var yd65 = 0.2126729 * rl + 0.7151522 * gl + 0.0721750 * bl;
        var zd65 = 0.0193339 * rl + 0.1191920 * gl + 0.9503041 * bl;

        // XYZ D65 → XYZ D50 via Bradford:
        var x = 1.0478112 * xd65 + 0.0228866 * yd65 - 0.0501270 * zd65;
        var y = 0.0295424 * xd65 + 0.9904844 * yd65 - 0.0170491 * zd65;
        var z = -0.0092345 * xd65 + 0.0150436 * yd65 + 0.7521316 * zd65;

        // D50 white point: (0.3457/0.3585, 1, 0.2958/0.3585).
        static double F(double t)
        {
            const double d = 6.0 / 29;
            return t > d * d * d ? Math.Cbrt(t) : t / (3 * d * d) + 4.0 / 29;
        }
        var fx = F(x / 0.9642957);
        var fy = F(y / 1.0);
        var fz = F(z / 0.8251046);
        return (116 * fy - 16, 500 * (fx - fy), 200 * (fy - fz));
    }

    public static (double r, double g, double b) LabToSrgb(double L, double a, double bv)
    {
        var fy = (L + 16) / 116.0;
        var fx = a / 500.0 + fy;
        var fz = fy - bv / 200.0;

        static double F(double t)
        {
            const double d = 6.0 / 29;
            return t > d ? t * t * t : 3 * d * d * (t - 4.0 / 29);
        }
        var x = F(fx) * 0.9642957;
        var y = F(fy) * 1.0;
        var z = F(fz) * 0.8251046;

        // XYZ D50 → XYZ D65 via inverse Bradford:
        var xd65 = 0.9555766 * x - 0.0230393 * y + 0.0631636 * z;
        var yd65 = -0.0282895 * x + 1.0099416 * y + 0.0210077 * z;
        var zd65 = 0.0122982 * x - 0.0204830 * y + 1.3299098 * z;

        // XYZ D65 → linear sRGB:
        var rl = 3.2404542 * xd65 - 1.5371385 * yd65 - 0.4985314 * zd65;
        var gl = -0.9692660 * xd65 + 1.8760108 * yd65 + 0.0415560 * zd65;
        var bl = 0.0556434 * xd65 - 0.2040259 * yd65 + 1.0572252 * zd65;
        return (LinearToSrgb01(rl), LinearToSrgb01(gl), LinearToSrgb01(bl));
    }

    // --- sRGB ↔ linear sRGB ---

    public static double SrgbToLinear01(double c)
    {
        c = Math.Clamp(c, 0, 1);
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    public static double LinearToSrgb01(double c)
    {
        c = Math.Clamp(c, 0, 1);
        return c <= 0.0031308 ? c * 12.92 : 1.055 * Math.Pow(c, 1.0 / 2.4) - 0.055;
    }
}
