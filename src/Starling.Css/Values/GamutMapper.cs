namespace Starling.Css.Values;

/// <summary>
/// CSS Color 4 §13.1 — gamut mapping by chroma reduction in Oklch.
/// Binary-searches the largest Oklch chroma such that the clipped sRGB result
/// is within the JND (just-noticeable-difference) in Oklab from the
/// reduced-chroma target.
/// </summary>
public static class GamutMapper
{
    private const double JustNoticeableDifference = 0.02;
    private const double Epsilon = 0.0001;

    /// <summary>Map an arbitrary-space color to in-gamut linear sRGB components 0..1.</summary>
    public static (double R, double G, double B) MapToSrgb(ColorSpace sourceSpace, double c1, double c2, double c3)
    {
        // Skip work if the source is already in sRGB and obviously in gamut.
        if (sourceSpace == ColorSpace.Srgb && InUnitRange(c1, c2, c3))
        {
            return (c1, c2, c3);
        }

        var (x, y, z) = ColorConversion.ToXyzD65(sourceSpace, c1, c2, c3);
        var (r, g, b) = ColorConversion.XyzD65ToSrgb(x, y, z);
        if (InUnitRange(r, g, b))
        {
            return (r, g, b);
        }

        // Convert origin to Oklch for chroma reduction.
        var (oL, oA, oB) = ColorConversion.XyzD65ToOklab(x, y, z);
        var oC = Math.Sqrt(oA * oA + oB * oB);
        var oH = Math.Atan2(oB, oA);

        // Lightness rails per spec.
        if (oL >= 1.0)
        {
            return (1, 1, 1);
        }

        if (oL <= 0.0)
        {
            return (0, 0, 0);
        }

        var min = 0.0;
        var max = oC;
        var minInGamut = true;
        var clipped = ClipFromOklch(oL, oC, oH);

        while (max - min > Epsilon)
        {
            var chroma = (min + max) / 2.0;
            var current = FromOklch(oL, chroma, oH);
            if (minInGamut && InUnitRange(current.R, current.G, current.B))
            {
                min = chroma;
                continue;
            }
            clipped = (
                Math.Clamp(current.R, 0, 1),
                Math.Clamp(current.G, 0, 1),
                Math.Clamp(current.B, 0, 1));
            var e = DeltaEOk(clipped, oL, chroma, oH);
            if (e < JustNoticeableDifference)
            {
                if (JustNoticeableDifference - e < Epsilon)
                {
                    return clipped;
                }

                minInGamut = false;
                min = chroma;
            }
            else
            {
                max = chroma;
            }
        }
        return clipped;
    }

    private static (double R, double G, double B) FromOklch(double L, double C, double hRad)
    {
        var a = C * Math.Cos(hRad);
        var b = C * Math.Sin(hRad);
        var (x, y, z) = ColorConversion.OklabToXyzD65(L, a, b);
        return ColorConversion.XyzD65ToSrgb(x, y, z);
    }

    private static (double R, double G, double B) ClipFromOklch(double L, double C, double hRad)
    {
        var (r, g, b) = FromOklch(L, C, hRad);
        return (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
    }

    private static double DeltaEOk((double R, double G, double B) clipped, double targetL, double targetC, double targetH)
    {
        var (xc, yc, zc) = ColorConversion.LinearSrgbToXyzD65(
            ColorConversion.SrgbToLinear(clipped.R),
            ColorConversion.SrgbToLinear(clipped.G),
            ColorConversion.SrgbToLinear(clipped.B));
        var (lc, ac, bc) = ColorConversion.XyzD65ToOklab(xc, yc, zc);
        var ta = targetC * Math.Cos(targetH);
        var tb = targetC * Math.Sin(targetH);
        var dL = lc - targetL;
        var dA = ac - ta;
        var dB = bc - tb;
        return Math.Sqrt(dL * dL + dA * dA + dB * dB);
    }

    private static bool InUnitRange(double a, double b, double c)
        => a >= 0.0 - Epsilon && a <= 1.0 + Epsilon
        && b >= 0.0 - Epsilon && b <= 1.0 + Epsilon
        && c >= 0.0 - Epsilon && c <= 1.0 + Epsilon;
}
