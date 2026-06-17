using Starling.Css.Values;
using Starling.Layout;

namespace Starling.Paint.Backend;

/// <summary>
/// A caller-owned vector scene drawn over the page on the GPU surface path.
/// Commands are in scene-local coordinates; <see cref="SurfaceOverlayLayer"/>
/// maps the scene into page space.
/// </summary>
public sealed class SurfaceOverlayScene
{
    public SurfaceOverlayScene(
        double width,
        double height,
        long geometryHash,
        IReadOnlyList<SurfaceOverlayCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        Width = width;
        Height = height;
        GeometryHash = geometryHash;
        Commands = commands.ToArray();
    }

    public double Width { get; }

    public double Height { get; }

    public long GeometryHash { get; }

    public IReadOnlyList<SurfaceOverlayCommand> Commands { get; }

    public static SurfaceOverlayScene Create(
        double width,
        double height,
        long geometryHash,
        Action<SurfaceOverlaySceneBuilder> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var builder = new SurfaceOverlaySceneBuilder();
        build(builder);
        return new SurfaceOverlayScene(width, height, geometryHash, builder.Commands);
    }
}

/// <summary>
/// Places an overlay scene in page space. Callers may pass an arbitrary transform,
/// or use the rectangle constructor for the common map-to-box case.
/// </summary>
public readonly record struct SurfaceOverlayLayer(
    SurfaceOverlayScene Scene,
    Matrix2D SceneToPage,
    float Opacity = 1f,
    Rect? ClipPage = null)
{
    public SurfaceOverlayLayer(
        double x,
        double y,
        double w,
        double h,
        SurfaceOverlayScene scene,
        float opacity = 1f,
        Rect? clipPage = null)
        : this(
            scene,
            Matrix2D.Translate(x, y).Multiply(Matrix2D.Scale(w / scene.Width, h / scene.Height)),
            opacity,
            clipPage)
    {
    }
}

public sealed class SurfaceOverlaySceneBuilder
{
    private readonly List<SurfaceOverlayCommand> _commands = [];

    public IReadOnlyList<SurfaceOverlayCommand> Commands => _commands;

    public void FillEllipse(double cx, double cy, double rx, double ry, CssColor color)
        => _commands.Add(new SurfaceOverlayFillEllipse(cx, cy, rx, ry, color));

    public void FillPolygon(IEnumerable<SurfaceOverlayPoint> points, CssColor color)
        => _commands.Add(new SurfaceOverlayFillPolygon(points.ToArray(), color));

    public void FillPath(SurfaceOverlayPath path, CssColor color)
        => _commands.Add(new SurfaceOverlayFillPath(path, color));

    public void StrokePath(
        SurfaceOverlayPath path,
        CssColor color,
        double width,
        SurfaceOverlayLineCap cap = SurfaceOverlayLineCap.Butt,
        SurfaceOverlayLineJoin join = SurfaceOverlayLineJoin.Miter)
        => _commands.Add(new SurfaceOverlayStrokePath(path, color, width, cap, join));

    public void FillInstances(SurfaceOverlayPrimitive primitive, IEnumerable<SurfaceOverlayInstance> instances)
        => _commands.Add(new SurfaceOverlayFillInstances(primitive, instances.ToArray()));
}

public abstract record SurfaceOverlayCommand;

public sealed record SurfaceOverlayFillEllipse(
    double Cx,
    double Cy,
    double Rx,
    double Ry,
    CssColor Color) : SurfaceOverlayCommand;

public sealed record SurfaceOverlayFillPolygon(
    IReadOnlyList<SurfaceOverlayPoint> Points,
    CssColor Color) : SurfaceOverlayCommand;

public sealed record SurfaceOverlayFillPath(
    SurfaceOverlayPath Path,
    CssColor Color) : SurfaceOverlayCommand;

public sealed record SurfaceOverlayStrokePath(
    SurfaceOverlayPath Path,
    CssColor Color,
    double Width,
    SurfaceOverlayLineCap Cap,
    SurfaceOverlayLineJoin Join) : SurfaceOverlayCommand;

public sealed record SurfaceOverlayFillInstances(
    SurfaceOverlayPrimitive Primitive,
    IReadOnlyList<SurfaceOverlayInstance> Instances) : SurfaceOverlayCommand;

public readonly record struct SurfaceOverlayPoint(double X, double Y);

public readonly record struct SurfaceOverlayInstance(
    double X,
    double Y,
    double W,
    double H,
    CssColor Color,
    double RotationRadians = 0,
    float Opacity = 1f);

public enum SurfaceOverlayPrimitive
{
    Rectangle,
    Circle,
    Triangle,
    Diamond,
    Star,
}

public enum SurfaceOverlayLineCap
{
    Butt,
    Square,
    Round,
}

public enum SurfaceOverlayLineJoin
{
    Miter,
    Bevel,
    Round,
}

public sealed class SurfaceOverlayPath
{
    private SurfaceOverlayPath(IReadOnlyList<SurfaceOverlayPathCommand> commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        Commands = commands.ToArray();
    }

    public IReadOnlyList<SurfaceOverlayPathCommand> Commands { get; }

    public static SurfaceOverlayPath Create(Action<SurfaceOverlayPathBuilder> build)
    {
        ArgumentNullException.ThrowIfNull(build);
        var builder = new SurfaceOverlayPathBuilder();
        build(builder);
        return new SurfaceOverlayPath(builder.Commands);
    }
}

public sealed class SurfaceOverlayPathBuilder
{
    private readonly List<SurfaceOverlayPathCommand> _commands = [];

    public IReadOnlyList<SurfaceOverlayPathCommand> Commands => _commands;

    public void MoveTo(double x, double y) => _commands.Add(new SurfaceOverlayMoveTo(x, y));

    public void LineTo(double x, double y) => _commands.Add(new SurfaceOverlayLineTo(x, y));

    public void QuadraticCurveTo(double cx, double cy, double x, double y)
        => _commands.Add(new SurfaceOverlayQuadraticCurveTo(cx, cy, x, y));

    public void CubicCurveTo(double c1x, double c1y, double c2x, double c2y, double x, double y)
        => _commands.Add(new SurfaceOverlayCubicCurveTo(c1x, c1y, c2x, c2y, x, y));

    public void Close() => _commands.Add(new SurfaceOverlayClosePath());
}

public abstract record SurfaceOverlayPathCommand;

public sealed record SurfaceOverlayMoveTo(double X, double Y) : SurfaceOverlayPathCommand;

public sealed record SurfaceOverlayLineTo(double X, double Y) : SurfaceOverlayPathCommand;

public sealed record SurfaceOverlayQuadraticCurveTo(double Cx, double Cy, double X, double Y) : SurfaceOverlayPathCommand;

public sealed record SurfaceOverlayCubicCurveTo(
    double C1x,
    double C1y,
    double C2x,
    double C2y,
    double X,
    double Y) : SurfaceOverlayPathCommand;

public sealed record SurfaceOverlayClosePath : SurfaceOverlayPathCommand;
