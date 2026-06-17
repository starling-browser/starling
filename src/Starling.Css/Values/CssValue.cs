using System.Globalization;

namespace Starling.Css.Values;

public abstract record CssValue;

public sealed record CssKeyword(string Name) : CssValue
{
    public override string ToString() => Name;
}

public sealed record CssNumber(double Value) : CssValue
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed record CssPercentage(double Value) : CssValue
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture) + "%";
}

public sealed record CssLength(double Value, CssLengthUnit Unit) : CssValue
{
    public static CssLength Zero { get; } = new(0, CssLengthUnit.Px);

    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture) + Unit.ToCssText();
}

public enum CssLengthUnit
{
    // Absolute (CSS Values 4 §6.2)
    Px,
    Pt,
    Pc,
    In,
    Cm,
    Mm,
    Q,

    // Font-relative (CSS Values 4 §6.1)
    Em,
    Rem,
    Ch,
    Ex,
    Cap,
    Ic,
    Lh,
    Rlh,

    // Viewport-relative — default (CSS Values 4 §6.1.4)
    Vh,
    Vw,
    Vmin,
    Vmax,
    Vi,
    Vb,

    // Viewport-relative — small
    Svh,
    Svw,
    Svmin,
    Svmax,

    // Viewport-relative — large
    Lvh,
    Lvw,
    Lvmin,
    Lvmax,

    // Viewport-relative — dynamic
    Dvh,
    Dvw,
    Dvmin,
    Dvmax,

    // Container query units (CSS Containment 3)
    Cqw,
    Cqh,
    Cqi,
    Cqb,
    Cqmin,
    Cqmax,
}

public sealed record CssDimension(double Value, string Unit) : CssValue;

/// <summary>
/// Time dimension (s, ms) — CSS Values 4 §8.2.
/// </summary>
public sealed record CssTime(double Value, CssTimeUnit Unit) : CssValue
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture) + (Unit == CssTimeUnit.Seconds ? "s" : "ms");

    public double InSeconds => Unit == CssTimeUnit.Seconds ? Value : Value / 1000d;
}

public enum CssTimeUnit { Seconds, Milliseconds }

/// <summary>
/// Angle dimension (deg, grad, rad, turn) — CSS Values 4 §8.1.
/// </summary>
public sealed record CssAngle(double Value, CssAngleUnit Unit) : CssValue
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture) + Unit switch
    {
        CssAngleUnit.Degrees => "deg",
        CssAngleUnit.Gradians => "grad",
        CssAngleUnit.Radians => "rad",
        CssAngleUnit.Turns => "turn",
        _ => "deg",
    };

    public double InDegrees => Unit switch
    {
        CssAngleUnit.Degrees => Value,
        CssAngleUnit.Gradians => Value * 0.9,
        CssAngleUnit.Radians => Value * 180.0 / Math.PI,
        CssAngleUnit.Turns => Value * 360.0,
        _ => Value,
    };

    public double InRadians => InDegrees * Math.PI / 180.0;
}

public enum CssAngleUnit { Degrees, Gradians, Radians, Turns }

/// <summary>
/// Frequency dimension (hz, khz) — CSS Values 4 §8.3.
/// </summary>
public sealed record CssFrequency(double Value, CssFrequencyUnit Unit) : CssValue
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture) + (Unit == CssFrequencyUnit.Hertz ? "hz" : "khz");

    public double InHertz => Unit == CssFrequencyUnit.Hertz ? Value : Value * 1000d;
}

public enum CssFrequencyUnit { Hertz, Kilohertz }

/// <summary>
/// Resolution dimension (dpi, dpcm, dppx, x) — CSS Values 4 §8.4.
/// </summary>
public sealed record CssResolution(double Value, CssResolutionUnit Unit) : CssValue
{
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture) + Unit switch
    {
        CssResolutionUnit.Dpi => "dpi",
        CssResolutionUnit.Dpcm => "dpcm",
        CssResolutionUnit.Dppx => "dppx",
        _ => "dppx",
    };

    public double InDppx => Unit switch
    {
        CssResolutionUnit.Dpi => Value / 96.0,
        CssResolutionUnit.Dpcm => Value * 2.54 / 96.0,
        CssResolutionUnit.Dppx => Value,
        _ => Value,
    };
}

public enum CssResolutionUnit { Dpi, Dpcm, Dppx }

/// <summary>
/// CSS Color value. Stores either an 8-bit sRGB representation (legacy R/G/B/A
/// for backwards compatibility) plus an optional source <see cref="ColorSpace"/>
/// with float component data. <see cref="ToSrgb"/> returns an 8-bit sRGB color
/// suitable for paint. Use <see cref="FromComponents"/> to construct from a
/// non-sRGB space.
/// </summary>
public sealed record CssColor(byte R, byte G, byte B, byte A = 255) : CssValue
{
    public static CssColor Transparent { get; } = new(0, 0, 0, 0);

    public static CssColor Black { get; } = new(0, 0, 0);

    /// <summary>Color space the original value was authored in.</summary>
    public ColorSpace Space { get; init; } = ColorSpace.Srgb;

    /// <summary>Native-range component values (NaN means "none" per Color 4 §4.4).</summary>
    public double C1 { get; init; }

    public double C2 { get; init; }

    public double C3 { get; init; }

    /// <summary>Alpha 0..1. NaN means "none".</summary>
    public double AlphaF { get; init; } = 1.0;

    /// <summary>Returns true if the color was specified using a non-sRGB space and
    /// retains its full-precision components.</summary>
    public bool HasWideGamutData => Space != ColorSpace.Srgb || double.IsNaN(C1) || double.IsNaN(C2) || double.IsNaN(C3) || double.IsNaN(AlphaF);

    public override string ToString()
        => A == 255
            ? $"rgb({R}, {G}, {B})"
            : $"rgba({R}, {G}, {B}, {(A / 255d).ToString("0.###", CultureInfo.InvariantCulture)})";

    /// <summary>Construct from float sRGB components in 0..1, with metadata.
    /// NaN components are preserved (representing CSS "none").</summary>
    public static CssColor FromSrgb(double r, double g, double b, double a = 1.0)
    {
        var (br, bg, bb, ba) = ClampToBytes(r, g, b, a);
        return new CssColor(br, bg, bb, ba)
        {
            Space = ColorSpace.Srgb,
            C1 = r,
            C2 = g,
            C3 = b,
            AlphaF = a,
        };
    }

    /// <summary>Construct from native-space components, preserving them while
    /// also computing an 8-bit sRGB fallback for direct paint use. Out-of-gamut
    /// colors are mapped via <see cref="GamutMapper"/> (Color 4 §13.1 chroma
    /// reduction in Oklch).</summary>
    public static CssColor FromComponents(ColorSpace space, double c1, double c2, double c3, double alpha = 1.0)
    {
        // For "none" components (NaN), treat as 0 for conversion purposes per Color 4 §4.4.
        var s1 = double.IsNaN(c1) ? 0 : c1;
        var s2 = double.IsNaN(c2) ? 0 : c2;
        var s3 = double.IsNaN(c3) ? 0 : c3;
        var sa = double.IsNaN(alpha) ? 0 : alpha;
        var (r, g, b) = GamutMapper.MapToSrgb(space, s1, s2, s3);
        var (br, bg, bb, ba) = ClampToBytes(r, g, b, sa);
        return new CssColor(br, bg, bb, ba)
        {
            Space = space,
            C1 = c1,
            C2 = c2,
            C3 = c3,
            AlphaF = alpha,
        };
    }

    /// <summary>Return an 8-bit sRGB representation, performing Color 4 §13.1
    /// chroma-reduction gamut mapping for wide-gamut authored colors.</summary>
    public CssColor ToSrgb()
    {
        if (!HasWideGamutData)
        {
            return new CssColor(R, G, B, A);
        }

        var s1 = double.IsNaN(C1) ? 0 : C1;
        var s2 = double.IsNaN(C2) ? 0 : C2;
        var s3 = double.IsNaN(C3) ? 0 : C3;
        var sa = double.IsNaN(AlphaF) ? 0 : AlphaF;
        var (r, g, b) = GamutMapper.MapToSrgb(Space, s1, s2, s3);
        var (br, bg, bb, ba) = ClampToBytes(r, g, b, sa);
        return new CssColor(br, bg, bb, ba) { Space = ColorSpace.Srgb };
    }

    private static (byte, byte, byte, byte) ClampToBytes(double r, double g, double b, double a)
    {
        static byte Bite(double v) => double.IsNaN(v) ? (byte)0 : (byte)Math.Round(Math.Clamp(v, 0.0, 1.0) * 255.0);
        return (Bite(r), Bite(g), Bite(b), Bite(a));
    }
}

/// <summary>
/// Color spaces recognized by CSS Color 4 / 5. The space determines how the
/// component values C1/C2/C3 are interpreted on <see cref="CssColor"/>.
/// </summary>
public enum ColorSpace
{
    Srgb,
    SrgbLinear,
    DisplayP3,
    A98Rgb,
    Rec2020,
    ProphotoRgb,
    XyzD50,
    XyzD65,
    Lab,
    Lch,
    Oklab,
    Oklch,
    Hsl,
    Hwb,
}

public sealed record CssString(string Value) : CssValue;

public sealed record CssUrl(string Value) : CssValue;

public sealed record CssFunctionValue(string Name, IReadOnlyList<CssValue> Arguments) : CssValue;

public sealed record CssValueList(IReadOnlyList<CssValue> Values) : CssValue;

/// <summary>
/// A single <c>text-shadow</c> layer (CSS Text Decoration 3 §5):
/// <c>&lt;color&gt;? &amp;&amp; [&lt;length&gt;{2,3}]</c> — an X/Y offset, an optional
/// non-negative blur radius (defaults to 0), and an optional color (defaults to
/// <c>currentColor</c>, signalled by <see cref="Color"/> being <c>null</c>).
/// Offsets and blur are stored in CSS px (resolved at parse time when authored
/// in absolute units; em/percent are left at face value for the painter to
/// approximate).
/// </summary>
public sealed record CssTextShadowLayer(double OffsetX, double OffsetY, double Blur, CssColor? Color);

/// <summary>
/// A typed <c>text-shadow</c> value — an ordered list of shadow layers painted
/// back-to-front (first layer on top), per CSS Text Decoration 3 §5. The keyword
/// <c>none</c> parses to an empty layer list.
/// </summary>
public sealed record CssTextShadow(IReadOnlyList<CssTextShadowLayer> Layers) : CssValue
{
    public static CssTextShadow None { get; } = new([]);
}

public sealed record CssVarReference(string Name, CssValue? Fallback) : CssValue;

/// <summary>
/// CSS Variables L1 §3.7 pending-substitution value. When a shorthand declaration
/// uses <c>var()</c> the shorthand cannot be expanded at parse time, since the
/// var() may resolve to any combination of components. Instead every longhand the
/// shorthand maps to is set to a pending-substitution value carrying the raw
/// shorthand components; at computed-value time the var() references are
/// substituted and the shorthand is re-expanded, then the appropriate longhand
/// value is extracted.
/// </summary>
public sealed record CssPendingSubstitution(
    string Shorthand,
    IReadOnlyList<CssValue> Values,
    Properties.PropertyId Longhand) : CssValue;

/// <summary>
/// env(name, fallback?) per CSS Environment Variables. Resolution requires
/// platform integration; this value preserves the syntax for later evaluation.
/// </summary>
public sealed record CssEnvReference(string Name, CssValue? Fallback) : CssValue;

/// <summary>
/// attr(name [type-or-unit] [, fallback]) per CSS Values 4 §11.2. Resolution
/// happens when the value is attached to an element with attributes.
/// </summary>
public sealed record CssAttrReference(string AttrName, string? TypeOrUnit, CssValue? Fallback) : CssValue
{
    public CssValue? Resolve(Func<string, string?> lookup) => AttrResolver.Resolve(this, lookup);
}

/// <summary>
/// A symbolic calc()/min()/max()/clamp()/round()/mod()/rem()/trig/exp/etc.
/// expression that could not be fully reduced at parse time. The
/// <see cref="Expression"/> is the simplified tree; call <c>Resolve(...)</c>
/// to finalize once viewport, em, percentage bases are known.
/// </summary>
public sealed record CssCalc(CalcNode Expression) : CssValue
{
    public override string ToString() => "calc(" + Expression + ")";

    public CssValue Resolve(CssResolutionContext ctx) => CssCalcResolver.Resolve(this, ctx);
}

/// <summary>Base of the calc() expression tree (CSS Values 4 §10).</summary>
public abstract record CalcNode
{
    public abstract NumericType Type { get; }
}

public sealed record CalcNumber(double Value) : CalcNode
{
    public override NumericType Type => NumericType.Number;
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}

public sealed record CalcLength(double Value, CssLengthUnit Unit) : CalcNode
{
    public override NumericType Type => NumericType.Length;
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture) + Unit.ToCssText();
}

public sealed record CalcPercentage(double Value) : CalcNode
{
    public override NumericType Type => NumericType.Percentage;
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture) + "%";
}

public sealed record CalcAngle(double Value, CssAngleUnit Unit) : CalcNode
{
    public override NumericType Type => NumericType.Angle;
    public override string ToString() => new CssAngle(Value, Unit).ToString();
}

public sealed record CalcTime(double Value, CssTimeUnit Unit) : CalcNode
{
    public override NumericType Type => NumericType.Time;
    public override string ToString() => new CssTime(Value, Unit).ToString();
}

public sealed record CalcFrequency(double Value, CssFrequencyUnit Unit) : CalcNode
{
    public override NumericType Type => NumericType.Frequency;
    public override string ToString() => new CssFrequency(Value, Unit).ToString();
}

public sealed record CalcResolution(double Value, CssResolutionUnit Unit) : CalcNode
{
    public override NumericType Type => NumericType.Resolution;
    public override string ToString() => new CssResolution(Value, Unit).ToString();
}

public enum CalcOperator { Add, Subtract, Multiply, Divide }

public sealed record CalcBinary(CalcOperator Op, CalcNode Left, CalcNode Right, NumericType ResultType) : CalcNode
{
    public override NumericType Type => ResultType;
    public override string ToString()
    {
        var op = Op switch
        {
            CalcOperator.Add => " + ",
            CalcOperator.Subtract => " - ",
            CalcOperator.Multiply => " * ",
            CalcOperator.Divide => " / ",
            _ => " ? ",
        };
        return "(" + Left + op + Right + ")";
    }
}

public sealed record CalcNegate(CalcNode Operand) : CalcNode
{
    public override NumericType Type => Operand.Type;
    public override string ToString() => "(-" + Operand + ")";
}

public sealed record CalcFunction(string Name, IReadOnlyList<CalcNode> Arguments, NumericType ResultType) : CalcNode
{
    public override NumericType Type => ResultType;
    public override string ToString()
        => Name + "(" + string.Join(", ", Arguments.Select(a => a.ToString())) + ")";
}

/// <summary>
/// Per Values 4 §10.2 — a coarse numeric type system for calc() result typing.
/// LengthPercentage represents a calc tree that mixes lengths and percentages
/// and therefore can only be resolved against a length percentage basis.
/// </summary>
public enum NumericType
{
    Number,
    Length,
    Percentage,
    LengthPercentage,
    Angle,
    Time,
    Frequency,
    Resolution,
    Flex,
    Unknown,
}

public static class CssLengthUnitExtensions
{
    public static string ToCssText(this CssLengthUnit unit)
        => unit switch
        {
            CssLengthUnit.Px => "px",
            CssLengthUnit.Em => "em",
            CssLengthUnit.Rem => "rem",
            CssLengthUnit.Vh => "vh",
            CssLengthUnit.Vw => "vw",
            CssLengthUnit.Vmin => "vmin",
            CssLengthUnit.Vmax => "vmax",
            CssLengthUnit.Vi => "vi",
            CssLengthUnit.Vb => "vb",
            CssLengthUnit.Pt => "pt",
            CssLengthUnit.Pc => "pc",
            CssLengthUnit.In => "in",
            CssLengthUnit.Cm => "cm",
            CssLengthUnit.Mm => "mm",
            CssLengthUnit.Ch => "ch",
            CssLengthUnit.Ex => "ex",
            CssLengthUnit.Cap => "cap",
            CssLengthUnit.Ic => "ic",
            CssLengthUnit.Lh => "lh",
            CssLengthUnit.Rlh => "rlh",
            CssLengthUnit.Q => "q",
            CssLengthUnit.Svh => "svh",
            CssLengthUnit.Svw => "svw",
            CssLengthUnit.Svmin => "svmin",
            CssLengthUnit.Svmax => "svmax",
            CssLengthUnit.Lvh => "lvh",
            CssLengthUnit.Lvw => "lvw",
            CssLengthUnit.Lvmin => "lvmin",
            CssLengthUnit.Lvmax => "lvmax",
            CssLengthUnit.Dvh => "dvh",
            CssLengthUnit.Dvw => "dvw",
            CssLengthUnit.Dvmin => "dvmin",
            CssLengthUnit.Dvmax => "dvmax",
            CssLengthUnit.Cqw => "cqw",
            CssLengthUnit.Cqh => "cqh",
            CssLengthUnit.Cqi => "cqi",
            CssLengthUnit.Cqb => "cqb",
            CssLengthUnit.Cqmin => "cqmin",
            CssLengthUnit.Cqmax => "cqmax",
            _ => unit.ToString().ToLowerInvariant(),
        };

    /// <summary>True if the unit can be converted to px without external context
    /// (font-size, viewport, container).</summary>
    public static bool IsAbsolute(this CssLengthUnit unit)
        => unit is CssLengthUnit.Px or CssLengthUnit.Pt or CssLengthUnit.Pc
            or CssLengthUnit.In or CssLengthUnit.Cm or CssLengthUnit.Mm or CssLengthUnit.Q;

    /// <summary>Convert an absolute-unit length value to px. Throws for relative units.</summary>
    public static double AbsoluteToPx(this CssLengthUnit unit, double value)
        => unit switch
        {
            CssLengthUnit.Px => value,
            CssLengthUnit.Pt => value * 4d / 3d,
            CssLengthUnit.Pc => value * 16d,
            CssLengthUnit.In => value * 96d,
            CssLengthUnit.Cm => value * 96d / 2.54d,
            CssLengthUnit.Mm => value * 96d / 25.4d,
            CssLengthUnit.Q => value * 96d / 101.6d,
            _ => throw new InvalidOperationException($"Unit {unit} is not absolute."),
        };
}
