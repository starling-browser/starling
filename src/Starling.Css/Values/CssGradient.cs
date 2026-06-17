namespace Starling.Css.Values;

/// <summary>
/// Typed value for the CSS Images 3 <c>&lt;gradient&gt;</c> functions
/// (<see href="https://www.w3.org/TR/css-images-3/#gradients">§3</see>).
/// <see cref="CssGradientParser"/> builds it from a generic
/// <see cref="CssFunctionValue"/>, the same way <see cref="CssTransformParser"/>
/// builds transforms. It carries what the paint backend needs to build a
/// gradient brush.
/// <para>
/// Covers <c>linear-gradient</c>, <c>radial-gradient</c>, and
/// <c>conic-gradient</c> plus their <c>repeating-</c> variants. Linear and
/// radial map to a gradient brush. Conic gradients have no brush, so the
/// paint backend rasterizes them per pixel into an offscreen layer.
/// </para>
/// <para>
/// For a conic gradient, <see cref="Line"/> holds the <c>from &lt;angle&gt;</c>
/// in <see cref="CssGradientLine.AngleDegrees"/> (clockwise from straight up,
/// default 0deg). <see cref="Position"/> holds the <c>at &lt;position&gt;</c>
/// center (default center). Color-stop positions are angles around the turn.
/// A percentage is a fraction of one full turn. An angle stop is normalized
/// to <c>deg / 360</c>.
/// </para>
/// <para>
/// <see cref="Interpolation"/> stores the CSS Color 4 <c>in &lt;colorspace&gt;</c>
/// choice. The conic per-pixel path honors it. The linear/radial brush path
/// pre-bakes the stops into sRGB instead. Null means the default. CSS Color 4
/// says the default is Oklab, but we use sRGB for compatibility.
/// </para>
/// </summary>
public sealed record CssGradient(
    CssGradientKind Kind,
    bool Repeating,
    IReadOnlyList<CssColorStop> Stops,
    CssGradientLine? Line = null,
    CssRadialShape Shape = CssRadialShape.Ellipse,
    CssRadialSize Size = CssRadialSize.FarthestCorner,
    CssGradientPosition? Position = null,
    GradientInterpolationMethod? Interpolation = null) : CssValue
{
    /// <summary>True when the paint backend can rasterize this gradient.
    /// All three kinds are paintable once they have at least one color
    /// stop.</summary>
    public bool IsPaintable => Stops.Count >= 1;
}

/// <summary>
/// CSS Color 4 §12.3 — the <c>in &lt;colorspace&gt;</c> prelude of a gradient.
/// Holds the color space to interpolate in. For polar spaces (oklch, hsl,
/// hwb, lch) it also holds the hue interpolation strategy.
/// </summary>
public sealed record GradientInterpolationMethod(
    GradientColorSpace ColorSpace,
    HueInterpolationMethod HueMethod = HueInterpolationMethod.Shorter);

/// <summary>
/// CSS Color 4 §12.3 — color spaces supported in gradient interpolation.
/// The conic per-pixel path honors all of these. The linear/radial brush
/// path pre-bakes the stops to sRGB no matter which space was asked for.
/// </summary>
public enum GradientColorSpace
{
    /// <summary>Premultiplied sRGB (default for CSS Images 3 gradients).</summary>
    Srgb,
    /// <summary>sRGB (straight alpha, linear interpolation in sRGB).</summary>
    SrgbLinear,
    /// <summary>Oklab (CSS Color 4 default for Level 4 gradients).</summary>
    Oklab,
    /// <summary>Oklch (polar Oklab).</summary>
    Oklch,
    /// <summary>HSL (hue-saturation-lightness).</summary>
    Hsl,
    /// <summary>HWB (hue-whiteness-blackness).</summary>
    Hwb,
    /// <summary>CIELAB (Lab).</summary>
    Lab,
    /// <summary>CIELCh (polar Lab).</summary>
    Lch,
    /// <summary>Display-P3.</summary>
    DisplayP3,
    /// <summary>A98-RGB (Adobe RGB).</summary>
    A98Rgb,
    /// <summary>ProPhoto RGB.</summary>
    ProphotoRgb,
    /// <summary>Rec 2020.</summary>
    Rec2020,
    /// <summary>XYZ with D50 white point.</summary>
    XyzD50,
    /// <summary>XYZ with D65 white point.</summary>
    XyzD65,
}

/// <summary>
/// CSS Color 4 §12.4 — hue interpolation strategy for polar color spaces
/// (oklch, hsl, hwb, lch). Controls how the hue angle wraps around the circle.
/// </summary>
public enum HueInterpolationMethod
{
    /// <summary>Take the shortest arc (the default). If the difference is exactly 180deg, go counter-clockwise.</summary>
    Shorter,
    /// <summary>Take the longer arc.</summary>
    Longer,
    /// <summary>Always increase the hue angle.</summary>
    Increasing,
    /// <summary>Always decrease the hue angle.</summary>
    Decreasing,
}

/// <summary>
/// CSS Images 4 §3.4 — a color-stop transition hint. A bare
/// <c>&lt;length-percentage&gt;</c> between two color stops that moves the
/// midpoint of the transition. The color at the hint position is the midpoint
/// between the surrounding stops. A power-curve skew makes the transition
/// speed up or slow down around it. The hint sits in the
/// <see cref="CssColorStop"/> list between real stops, marked by
/// <see cref="CssColorStop.IsHint"/>.
/// </summary>
public static class CssTransitionHint
{
    /// <summary>
    /// Creates a hint entry: a <see cref="CssColorStop"/> with
    /// <see cref="CssColorStop.IsHint"/> set, a transparent-black sentinel
    /// color, and the hint position in <see cref="CssColorStop.Position"/>.
    /// </summary>
    public static CssColorStop Create(CssGradientStopPosition position)
        => new(IsHint: true, Color: new CssColor(0, 0, 0, 0), Position: position);
}

/// <summary>One color stop: a color and an optional position. When
/// <see cref="Position"/> is null the stop is spread evenly at paint time per
/// CSS Images 3 §3.4.3. When <see cref="IsHint"/> is true this entry is a CSS
/// Images 4 transition hint. The backend applies a power-curve skew at the
/// hint position.</summary>
public sealed record CssColorStop(CssColor Color, CssGradientStopPosition? Position = null, bool IsHint = false);

public enum CssGradientKind
{
    Linear,
    Radial,
    Conic,
}

/// <summary>The direction of a linear gradient. Either an explicit
/// <c>&lt;angle&gt;</c> (clockwise from "to top", so 0deg points up) or a
/// <c>to &lt;side-or-corner&gt;</c> keyword pair.</summary>
public sealed record CssGradientLine
{
    /// <summary>Angle in degrees, clockwise from straight up (CSS convention:
    /// 0deg = "to top", 90deg = "to right"). Null when a side or corner is
    /// set.</summary>
    public double? AngleDegrees { get; init; }

    /// <summary>Horizontal side component for <c>to &lt;side-or-corner&gt;</c>.</summary>
    public CssGradientSideX SideX { get; init; } = CssGradientSideX.None;

    /// <summary>Vertical side component for <c>to &lt;side-or-corner&gt;</c>.</summary>
    public CssGradientSideY SideY { get; init; } = CssGradientSideY.None;

    public bool IsAngle => AngleDegrees is not null;

    public static CssGradientLine FromAngle(double degrees) => new() { AngleDegrees = degrees };

    public static CssGradientLine FromSide(CssGradientSideX x, CssGradientSideY y) => new() { SideX = x, SideY = y };

    /// <summary>Resolves the gradient line to an angle in degrees, clockwise
    /// from "to top". A <c>to &lt;side-or-corner&gt;</c> uses the box's aspect
    /// ratio per CSS Images 3 §3.1. For corners the angle points toward the
    /// corner.</summary>
    public double ToDegrees(double boxWidth, double boxHeight)
    {
        if (AngleDegrees is { } a)
        {
            return a;
        }

        // Pure sides.
        if (SideX == CssGradientSideX.None)
        {
            return SideY switch
            {
                CssGradientSideY.Top => 0,
                CssGradientSideY.Bottom => 180,
                _ => 180, // default `to bottom`
            };
        }
        if (SideY == CssGradientSideY.None)
        {
            return SideX switch
            {
                CssGradientSideX.Right => 90,
                CssGradientSideX.Left => 270,
                _ => 180,
            };
        }

        // Corner: the gradient line points so that the line is perpendicular to
        // the line joining the two opposite corners (CSS Images 3 §3.1). We
        // approximate using the box-diagonal angle, which is correct for the
        // common corner cases.
        var w = boxWidth <= 0 ? 1 : boxWidth;
        var h = boxHeight <= 0 ? 1 : boxHeight;
        var diag = Math.Atan2(w, h) * 180.0 / Math.PI; // angle of the corner direction from vertical
        return (SideX, SideY) switch
        {
            (CssGradientSideX.Right, CssGradientSideY.Top) => diag,
            (CssGradientSideX.Right, CssGradientSideY.Bottom) => 180 - diag,
            (CssGradientSideX.Left, CssGradientSideY.Bottom) => 180 + diag,
            (CssGradientSideX.Left, CssGradientSideY.Top) => 360 - diag,
            _ => 180,
        };
    }
}

public enum CssGradientSideX { None, Left, Right }

public enum CssGradientSideY { None, Top, Bottom }

/// <summary>A radial gradient's <c>at &lt;position&gt;</c> as fractions of the
/// box (0..1). Defaults to center.</summary>
public readonly record struct CssGradientPosition(double FractionX, double FractionY)
{
    public static CssGradientPosition Center { get; } = new(0.5, 0.5);
}

public enum CssRadialShape { Circle, Ellipse }

public enum CssRadialSize
{
    ClosestSide,
    ClosestCorner,
    FarthestSide,
    FarthestCorner,
}

/// <summary>A color-stop position: an absolute length (already resolved to
/// pixels) or a percentage of the gradient line. Only one is set.</summary>
public readonly record struct CssGradientStopPosition(double Value, bool IsPercent)
{
    /// <summary>Resolves to a fraction (0..1) of the gradient line length.</summary>
    public double ResolveFraction(double lineLengthPx)
        => IsPercent
            ? Value / 100.0
            : (lineLengthPx <= 0 ? 0 : Value / lineLengthPx);
}
