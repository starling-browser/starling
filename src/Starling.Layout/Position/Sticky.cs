namespace Starling.Layout.Position;

/// <summary>
/// Resolves <c>position: sticky</c> offsets without a scroll model.
/// <para>
/// True sticky positioning is "behaves like <c>relative</c> until the
/// element would scroll past a configured threshold within its nearest
/// scrolling ancestor, then it pins to that threshold." Starling's layout
/// engine has no scroll concept yet, so this implementation degrades to
/// <em>clamped-relative</em>: the natural in-flow frame is shifted only
/// when an inset (<c>top</c> / <c>right</c> / <c>bottom</c> / <c>left</c>)
/// is violated relative to the containing block's content-box edges, and
/// the resulting frame is clamped to stay inside that content rect.
/// </para>
/// <para>
/// The scroll-driven shift requires a "nearest scrolling ancestor" concept
/// plus a per-frame scroll position; both land in a later milestone.
/// </para>
/// </summary>
internal static class StickyLayout
{
    /// <summary>
    /// Compute the shifted frame for a sticky element.
    /// </summary>
    /// <param name="naturalFrame">
    /// The element's in-flow frame in its containing block's local
    /// (content-box) coordinate space — i.e. the same space
    /// <paramref name="containingBlockContentRect"/> is expressed in.
    /// </param>
    /// <param name="containingBlockContentRect">
    /// The containing block's content-box rect, in the same local space as
    /// <paramref name="naturalFrame"/>. For a sticky element whose parent
    /// is its containing block, this is <c>(0, 0, cbWidth, cbHeight)</c>.
    /// </param>
    /// <param name="props">Resolved positioning properties for the element.</param>
    public static Rect ResolveOffset(
        Rect naturalFrame,
        Rect containingBlockContentRect,
        PositionedProps props)
    {
        // Percentage basis: top/bottom against CB height, left/right against
        // CB width — matches the percentage basis used by `position: relative`.
        var cb = containingBlockContentRect;
        var topPx = props.Top.Resolve(cb.Height);
        var bottomPx = props.Bottom.Resolve(cb.Height);
        var leftPx = props.Left.Resolve(cb.Width);
        var rightPx = props.Right.Resolve(cb.Width);

        var rect = naturalFrame;

        // ---- Vertical -------------------------------------------------
        // `top` enforces a minimum distance from CB.Top.
        if (topPx is { } t)
        {
            var minTop = cb.Y + t;
            if (rect.Y < minTop)
            {
                rect = rect.Translate(0, minTop - rect.Y);
            }
        }
        // `bottom` enforces a maximum extent to CB.Bottom - bottom. If both
        // are set and the element is shorter than the resulting band, `top`
        // wins (already applied above) and this branch only pulls the box
        // back up when `top` would have pushed it past the bottom edge.
        if (bottomPx is { } b)
        {
            var maxBottom = cb.Bottom - b;
            if (rect.Bottom > maxBottom)
            {
                rect = rect.Translate(0, maxBottom - rect.Bottom);
            }
        }

        // ---- Horizontal (mirror of vertical) --------------------------
        if (leftPx is { } l)
        {
            var minLeft = cb.X + l;
            if (rect.X < minLeft)
            {
                rect = rect.Translate(minLeft - rect.X, 0);
            }
        }
        if (rightPx is { } r)
        {
            var maxRight = cb.Right - r;
            if (rect.Right > maxRight)
            {
                rect = rect.Translate(maxRight - rect.Right, 0);
            }
        }

        // ---- Clamp to containing-block content rect -------------------
        // Without a scroll model this rarely triggers, but it makes the
        // "can't escape the containing block" guarantee explicit. When the
        // element is *larger* than the CB on an axis, clamping keeps the
        // top/left edge anchored (the leading edge wins).
        rect = ClampWithin(rect, cb);
        return rect;
    }

    private static Rect ClampWithin(Rect rect, Rect cb)
    {
        double x = rect.X;
        double y = rect.Y;

        // Clamp horizontally.
        if (rect.Width <= cb.Width)
        {
            if (x < cb.X)
            {
                x = cb.X;
            }
            else if (x + rect.Width > cb.Right)
            {
                x = cb.Right - rect.Width;
            }
        }
        else
        {
            // Larger than CB: pin leading edge to CB.X.
            x = cb.X;
        }

        // Clamp vertically.
        if (rect.Height <= cb.Height)
        {
            if (y < cb.Y)
            {
                y = cb.Y;
            }
            else if (y + rect.Height > cb.Bottom)
            {
                y = cb.Bottom - rect.Height;
            }
        }
        else
        {
            y = cb.Y;
        }

        return new Rect(x, y, rect.Width, rect.Height);
    }
}
