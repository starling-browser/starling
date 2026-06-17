namespace Starling.Css.Values;

/// <summary>
/// Resolved value of the CSS <c>transform</c> property — a list of 2D
/// <see cref="CssTransformFunction"/>s applied in source order. Per CSS
/// Transforms 1 §3, the functions compose left-to-right: the leftmost
/// function maps the box's local coordinate system first.
/// <para>
/// 3D transforms (<c>matrix3d</c>, <c>rotate3d</c>, <c>translate3d</c>,
/// <c>perspective</c>) are out of scope here — they require a 4×4 stack
/// and a stacking-context flatten step; this type is 2D-only.
/// </para>
/// </summary>
public sealed record CssTransform(IReadOnlyList<CssTransformFunction> Functions) : CssValue
{
    public static CssTransform None { get; } = new([]);

    public bool IsNone => Functions.Count == 0;

    /// <summary>
    /// Compose the function list into a single <see cref="Matrix2D"/>.
    /// <paramref name="referenceWidth"/>/<paramref name="referenceHeight"/> are
    /// the box used to resolve percentage translations (translate% is
    /// relative to the reference box, per Transforms 1 §6.1).
    /// </summary>
    public Matrix2D ToMatrix(double referenceWidth, double referenceHeight)
    {
        var result = Matrix2D.Identity;
        foreach (var fn in Functions)
        {
            result = result.Multiply(fn.ToMatrix(referenceWidth, referenceHeight));
        }

        return result;
    }
}

/// <summary>Base for one entry in a <c>transform</c> function list.</summary>
public abstract record CssTransformFunction
{
    public abstract Matrix2D ToMatrix(double referenceWidth, double referenceHeight);
}

/// <summary>A length in either absolute units (px) or as a percentage of a reference dimension.</summary>
public readonly record struct CssLengthOrPercent(double Value, bool IsPercent)
{
    public double Resolve(double reference)
        => IsPercent ? Value / 100d * reference : Value;
}

public sealed record CssTranslate(CssLengthOrPercent X, CssLengthOrPercent Y) : CssTransformFunction
{
    public override Matrix2D ToMatrix(double rw, double rh)
        => Matrix2D.Translate(X.Resolve(rw), Y.Resolve(rh));
}

public sealed record CssScale(double X, double Y) : CssTransformFunction
{
    public override Matrix2D ToMatrix(double rw, double rh) => Matrix2D.Scale(X, Y);
}

public sealed record CssRotate(double AngleRadians) : CssTransformFunction
{
    public override Matrix2D ToMatrix(double rw, double rh) => Matrix2D.Rotate(AngleRadians);
}

public sealed record CssSkew(double XRadians, double YRadians) : CssTransformFunction
{
    public override Matrix2D ToMatrix(double rw, double rh) => Matrix2D.Skew(XRadians, YRadians);
}

public sealed record CssMatrix(Matrix2D Value) : CssTransformFunction
{
    public override Matrix2D ToMatrix(double rw, double rh) => Value;
}
