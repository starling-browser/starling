namespace Starling.Layout.Block;

/// <summary>
/// CSS 2.1 §9.5 float bookkeeping for a single block formatting context.
/// Tracks active <c>float: left</c> and <c>float: right</c> boxes by their
/// outer rect (margin box) so subsequent floats know where to wrap and
/// <c>clear</c> on subsequent in-flow content can push past them.
/// </summary>
internal sealed class FloatContext
{
    private readonly double _containerWidth;
    private readonly List<Rect> _left = new();
    private readonly List<Rect> _right = new();

    public FloatContext(double containerWidth)
    {
        _containerWidth = containerWidth;
    }

    /// <summary>
    /// The lowest x value at which a horizontal line drawn at <paramref name="y"/>
    /// would not intersect any active left float — i.e. the x at which a left-
    /// aligned line of content (or a wrapping left float) would start.
    /// </summary>
    public double LeftEdgeAt(double y)
    {
        var edge = 0d;
        foreach (var f in _left)
        {
            if (y >= f.Y && y < f.Bottom)
            {
                edge = Math.Max(edge, f.Right);
            }
        }

        return edge;
    }

    /// <summary>The right counterpart to <see cref="LeftEdgeAt"/>.</summary>
    public double RightEdgeAt(double y)
    {
        var edge = _containerWidth;
        foreach (var f in _right)
        {
            if (y >= f.Y && y < f.Bottom)
            {
                edge = Math.Min(edge, f.X);
            }
        }

        return edge;
    }

    public double AvailableAt(double y) => Math.Max(0, RightEdgeAt(y) - LeftEdgeAt(y));

    /// <summary>
    /// Place a left-floated box of size <paramref name="width"/> ×
    /// <paramref name="height"/> at the earliest y ≥ <paramref name="startY"/>
    /// where it fits between the active floats. Returns the chosen outer-rect.
    /// </summary>
    public Rect PlaceLeft(double startY, double width, double height)
    {
        var y = NextFitY(startY, width);
        var x = LeftEdgeAt(y);
        var rect = new Rect(x, y, width, height);
        _left.Add(rect);
        return rect;
    }

    public Rect PlaceRight(double startY, double width, double height)
    {
        var y = NextFitY(startY, width);
        var x = RightEdgeAt(y) - width;
        var rect = new Rect(x, y, width, height);
        _right.Add(rect);
        return rect;
    }

    /// <summary>
    /// CSS 2.1 §9.5.2 — return the y past the bottom of every active float of
    /// the requested side(s). Used to satisfy <c>clear</c> on subsequent
    /// in-flow blocks.
    /// </summary>
    public double ClearY(string side)
    {
        var y = 0d;
        if (side == "left" || side == "both")
        {
            foreach (var f in _left)
            {
                y = Math.Max(y, f.Bottom);
            }
        }

        if (side == "right" || side == "both")
        {
            foreach (var f in _right)
            {
                y = Math.Max(y, f.Bottom);
            }
        }

        return y;
    }

    /// <summary>
    /// Maximum bottom edge of any active float — used by the float-containing
    /// BFC to grow its content height to enclose its floats (§10.6.7).
    /// </summary>
    public double MaxFloatBottom()
    {
        var y = 0d;
        foreach (var f in _left)
        {
            y = Math.Max(y, f.Bottom);
        }

        foreach (var f in _right)
        {
            y = Math.Max(y, f.Bottom);
        }

        return y;
    }

    private double NextFitY(double startY, double width)
    {
        var y = startY;
        // Walk down through float-band boundaries until a horizontal segment of
        // `width` fits between the current left + right edges.
        var safety = 0;
        while (AvailableAt(y) + 0.5 < width)
        {
            var next = double.PositiveInfinity;
            foreach (var f in _left)
            {
                if (f.Bottom > y && f.Bottom < next)
                {
                    next = f.Bottom;
                }
            }

            foreach (var f in _right)
            {
                if (f.Bottom > y && f.Bottom < next)
                {
                    next = f.Bottom;
                }
            }

            if (double.IsPositiveInfinity(next))
            {
                break;
            }

            y = next;
            if (++safety > 10_000)
            {
                break;
            }
        }
        return y;
    }
}
