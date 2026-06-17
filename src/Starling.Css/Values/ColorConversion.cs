namespace Starling.Css.Values;

/// <summary>
/// Color space conversion math per CSS Color 4 §15. Implements the linkages
/// sRGB ↔ linear-sRGB ↔ XYZ(D65) ↔ XYZ(D50) ↔ Lab ↔ Lch, and
/// linear-sRGB ↔ Oklab ↔ Oklch. Other RGB spaces (Display P3, A98, Rec2020,
/// ProPhoto) route through XYZ. HSL and HWB route through sRGB.
/// </summary>
public static class ColorConversion
{
    public static (double R, double G, double B) ToSrgb(ColorSpace space, double c1, double c2, double c3)
    {
        var (x, y, z) = ToXyzD65(space, c1, c2, c3);
        return XyzD65ToSrgb(x, y, z);
    }

    public static (double X, double Y, double Z) ToXyzD65(ColorSpace space, double c1, double c2, double c3)
        => space switch
        {
            ColorSpace.Srgb => LinearSrgbToXyzD65(SrgbToLinear(c1), SrgbToLinear(c2), SrgbToLinear(c3)),
            ColorSpace.SrgbLinear => LinearSrgbToXyzD65(c1, c2, c3),
            ColorSpace.DisplayP3 => DisplayP3ToXyzD65(c1, c2, c3),
            ColorSpace.A98Rgb => A98RgbToXyzD65(c1, c2, c3),
            ColorSpace.Rec2020 => Rec2020ToXyzD65(c1, c2, c3),
            ColorSpace.ProphotoRgb => ProphotoToXyzD65(c1, c2, c3),
            ColorSpace.XyzD65 => (c1, c2, c3),
            ColorSpace.XyzD50 => XyzD50ToXyzD65(c1, c2, c3),
            ColorSpace.Lab => XyzD50ToXyzD65(LabToXyzD50(c1, c2, c3).X, LabToXyzD50(c1, c2, c3).Y, LabToXyzD50(c1, c2, c3).Z),
            ColorSpace.Lch => XyzD50ToXyzD65(LchToXyzD50(c1, c2, c3).X, LchToXyzD50(c1, c2, c3).Y, LchToXyzD50(c1, c2, c3).Z),
            ColorSpace.Oklab => OklabToXyzD65(c1, c2, c3),
            ColorSpace.Oklch => OklabToXyzD65(c1, c2 * Math.Cos(c3 * Math.PI / 180.0), c2 * Math.Sin(c3 * Math.PI / 180.0)),
            ColorSpace.Hsl => HslToSrgbThenXyz(c1, c2, c3),
            ColorSpace.Hwb => HwbToSrgbThenXyz(c1, c2, c3),
            _ => (0, 0, 0),
        };

    private static (double, double, double) HslToSrgbThenXyz(double h, double s, double l)
    {
        var (r, g, b) = HslToSrgb(h, s, l);
        return LinearSrgbToXyzD65(SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b));
    }

    private static (double, double, double) HwbToSrgbThenXyz(double h, double w, double bk)
    {
        var (r, g, b) = HwbToSrgb(h, w, bk);
        return LinearSrgbToXyzD65(SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b));
    }

    public static double SrgbToLinear(double c)
    {
        var abs = Math.Abs(c);
        var sign = c < 0 ? -1.0 : 1.0;
        return abs <= 0.04045 ? c / 12.92 : sign * Math.Pow((abs + 0.055) / 1.055, 2.4);
    }

    public static double LinearToSrgb(double c)
    {
        var abs = Math.Abs(c);
        var sign = c < 0 ? -1.0 : 1.0;
        return abs <= 0.0031308 ? c * 12.92 : sign * (1.055 * Math.Pow(abs, 1.0 / 2.4) - 0.055);
    }

    public static (double X, double Y, double Z) LinearSrgbToXyzD65(double r, double g, double b)
    {
        var x = 0.41239079926595934 * r + 0.357584339383878 * g + 0.1804807884018343 * b;
        var y = 0.21263900587151027 * r + 0.715168678767756 * g + 0.07219231536073371 * b;
        var z = 0.01933081871559182 * r + 0.11919477979462598 * g + 0.9505321522496607 * b;
        return (x, y, z);
    }

    public static (double R, double G, double B) XyzD65ToLinearSrgb(double x, double y, double z)
    {
        var r = 3.2409699419045226 * x - 1.537383177570094 * y - 0.4986107602930034 * z;
        var g = -0.9692436362808796 * x + 1.8759675015077202 * y + 0.04155505740717561 * z;
        var b = 0.05563007969699366 * x - 0.20397695888897652 * y + 1.0569715142428786 * z;
        return (r, g, b);
    }

    public static (double R, double G, double B) XyzD65ToSrgb(double x, double y, double z)
    {
        var (r, g, b) = XyzD65ToLinearSrgb(x, y, z);
        return (LinearToSrgb(r), LinearToSrgb(g), LinearToSrgb(b));
    }

    private static (double, double, double) DisplayP3ToXyzD65(double r, double g, double b)
    {
        var lr = SrgbToLinear(r);
        var lg = SrgbToLinear(g);
        var lb = SrgbToLinear(b);
        var x = 0.4865709486482162 * lr + 0.26566769316909306 * lg + 0.1982172852343625 * lb;
        var y = 0.2289745640697488 * lr + 0.6917385218365064 * lg + 0.079286914093745 * lb;
        var z = 0.0 * lr + 0.04511338185890264 * lg + 1.043944368900976 * lb;
        return (x, y, z);
    }

    private static (double, double, double) A98RgbToXyzD65(double r, double g, double b)
    {
        // gamma 2.19921875
        double Lin(double v) => Math.Sign(v) * Math.Pow(Math.Abs(v), 563.0 / 256.0);
        var lr = Lin(r); var lg = Lin(g); var lb = Lin(b);
        var x = 0.5766690429101305 * lr + 0.1855582379065463 * lg + 0.1882286462349947 * lb;
        var y = 0.297344975250536 * lr + 0.6273635662554661 * lg + 0.0752914584939979 * lb;
        var z = 0.0270313613864123 * lr + 0.0706888525358272 * lg + 0.9913375368376388 * lb;
        return (x, y, z);
    }

    private static (double, double, double) Rec2020ToXyzD65(double r, double g, double b)
    {
        const double alpha = 1.09929682680944;
        const double beta = 0.018053968510807;
        double Lin(double v)
        {
            var abs = Math.Abs(v);
            var sign = v < 0 ? -1.0 : 1.0;
            return abs < beta * 4.5 ? v / 4.5 : sign * Math.Pow((abs + alpha - 1) / alpha, 1.0 / 0.45);
        }
        var lr = Lin(r); var lg = Lin(g); var lb = Lin(b);
        var x = 0.6369580483012911 * lr + 0.14461690358620838 * lg + 0.16888097516247837 * lb;
        var y = 0.26270021201126703 * lr + 0.6779980715188708 * lg + 0.05930171646986196 * lb;
        var z = 0.0 * lr + 0.028072693049087428 * lg + 1.0609850577107572 * lb;
        return (x, y, z);
    }

    private static (double, double, double) ProphotoToXyzD65(double r, double g, double b)
    {
        double Lin(double v)
        {
            var abs = Math.Abs(v);
            var sign = v < 0 ? -1.0 : 1.0;
            return abs <= 16.0 / 512.0 ? v / 16.0 : sign * Math.Pow(abs, 1.8);
        }
        var lr = Lin(r); var lg = Lin(g); var lb = Lin(b);
        // ProPhoto is D50; convert to D65 via Bradford
        var x50 = 0.7977666449006423 * lr + 0.13518129740053308 * lg + 0.0313477341283922 * lb;
        var y50 = 0.2880748288194013 * lr + 0.711835234241873 * lg + 0.00008993693872564 * lb;
        var z50 = 0.0 * lr + 0.0 * lg + 0.8251046025104602 * lb;
        return XyzD50ToXyzD65(x50, y50, z50);
    }

    // Bradford D50 -> D65
    public static (double X, double Y, double Z) XyzD50ToXyzD65(double x, double y, double z)
    {
        var nx = 0.9554734527042182 * x + -0.023098536874261423 * y + 0.0632593086610217 * z;
        var ny = -0.028369706963208136 * x + 1.0099954580058226 * y + 0.021041398966943008 * z;
        var nz = 0.012314001688319899 * x + -0.020507696433477912 * y + 1.3303659366080753 * z;
        return (nx, ny, nz);
    }

    public static (double X, double Y, double Z) XyzD65ToXyzD50(double x, double y, double z)
    {
        var nx = 1.0479298208405488 * x + 0.022946793341019088 * y + -0.05019222954313557 * z;
        var ny = 0.029627815688159344 * x + 0.990434484573249 * y + -0.01707382502938514 * z;
        var nz = -0.009243058152591178 * x + 0.015055144896577792 * y + 0.7518742899580008 * z;
        return (nx, ny, nz);
    }

    public static (double X, double Y, double Z) LabToXyzD50(double L, double a, double b)
    {
        const double kappa = 24389.0 / 27.0;
        const double epsilon = 216.0 / 24389.0;
        // D50 white point
        const double Xn = 0.96422, Yn = 1.0, Zn = 0.82521;
        var fy = (L + 16.0) / 116.0;
        var fx = a / 500.0 + fy;
        var fz = fy - b / 200.0;
        double FInv(double f) => Math.Pow(f, 3) > epsilon ? Math.Pow(f, 3) : (116.0 * f - 16.0) / kappa;
        var xr = FInv(fx);
        var yr = L > kappa * epsilon ? Math.Pow(fy, 3) : L / kappa;
        var zr = FInv(fz);
        return (xr * Xn, yr * Yn, zr * Zn);
    }

    public static (double L, double a, double b) XyzD50ToLab(double x, double y, double z)
    {
        const double kappa = 24389.0 / 27.0;
        const double epsilon = 216.0 / 24389.0;
        const double Xn = 0.96422, Yn = 1.0, Zn = 0.82521;
        var xr = x / Xn; var yr = y / Yn; var zr = z / Zn;
        double F(double t) => t > epsilon ? Math.Cbrt(t) : (kappa * t + 16.0) / 116.0;
        var fx = F(xr); var fy = F(yr); var fz = F(zr);
        return (116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz));
    }

    public static (double X, double Y, double Z) LchToXyzD50(double L, double c, double h)
    {
        var rad = h * Math.PI / 180.0;
        var a = c * Math.Cos(rad);
        var b = c * Math.Sin(rad);
        return LabToXyzD50(L, a, b);
    }

    public static (double X, double Y, double Z) OklabToXyzD65(double L, double a, double b)
    {
        // Oklab → linear sRGB (per the original paper / Color 4 §10.4).
        var l_ = L + 0.3963377774 * a + 0.2158037573 * b;
        var m_ = L - 0.1055613458 * a - 0.0638541728 * b;
        var s_ = L - 0.0894841775 * a - 1.2914855480 * b;
        var l = l_ * l_ * l_;
        var m = m_ * m_ * m_;
        var s = s_ * s_ * s_;
        // LMS → XYZ D65
        var x = 1.2270138511 * l - 0.5577999807 * m + 0.281256149 * s;
        var y = -0.0405801784 * l + 1.1122568696 * m - 0.0716766787 * s;
        var z = -0.0763812845 * l - 0.4214819784 * m + 1.5861632204 * s;
        return (x, y, z);
    }

    public static (double L, double a, double b) XyzD65ToOklab(double x, double y, double z)
    {
        var l = 0.8190224432164319 * x + 0.3619062562801221 * y - 0.12887378261216414 * z;
        var m = 0.0329836671023158 * x + 0.9292868468965546 * y + 0.03614466816999844 * z;
        var s = 0.048177199566046255 * x + 0.26423952494422764 * y + 0.6335478258136937 * z;
        var l_ = Math.Cbrt(l); var m_ = Math.Cbrt(m); var s_ = Math.Cbrt(s);
        return (
            0.2104542553 * l_ + 0.7936177850 * m_ - 0.0040720468 * s_,
            1.9779984951 * l_ - 2.4285922050 * m_ + 0.4505937099 * s_,
            0.0259040371 * l_ + 0.7827717662 * m_ - 0.8086757660 * s_);
    }

    /// <summary>HSL with h in degrees, s/l in 0..1 → sRGB 0..1.</summary>
    public static (double R, double G, double B) HslToSrgb(double h, double s, double l)
    {
        h = ((h % 360.0) + 360.0) % 360.0;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double F(double n)
        {
            var k = (n + h / 30.0) % 12.0;
            var a = s * Math.Min(l, 1 - l);
            return l - a * Math.Max(-1.0, Math.Min(Math.Min(k - 3.0, 9.0 - k), 1.0));
        }
        return (F(0), F(8), F(4));
    }

    /// <summary>HWB with h in degrees, w/b in 0..1 → sRGB 0..1.</summary>
    public static (double R, double G, double B) HwbToSrgb(double h, double w, double black)
    {
        if (w + black >= 1)
        {
            var gray = w / (w + black);
            return (gray, gray, gray);
        }
        var (r, g, b) = HslToSrgb(h, 1.0, 0.5);
        r = r * (1 - w - black) + w;
        g = g * (1 - w - black) + w;
        b = b * (1 - w - black) + w;
        return (r, g, b);
    }

    public static (double H, double S, double L) SrgbToHsl(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;
        var d = max - min;
        if (d == 0)
        {
            return (0, 0, l);
        }

        var s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
        double h;
        if (max == r)
        {
            h = (g - b) / d + (g < b ? 6.0 : 0.0);
        }
        else if (max == g)
        {
            h = (b - r) / d + 2.0;
        }
        else
        {
            h = (r - g) / d + 4.0;
        }

        return (h * 60.0, s, l);
    }
}
