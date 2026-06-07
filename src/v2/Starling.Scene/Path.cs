// SPDX-License-Identifier: Apache-2.0
using System.Numerics;

namespace Starling.Scene;

/// <summary>A path segment kind. The point count per verb is fixed: MoveTo and LineTo take one point, QuadTo two, CubicTo three, Close none.</summary>
public enum PathVerb
{
    MoveTo,
    LineTo,
    QuadTo,
    CubicTo,
    Close,
}

/// <summary>
/// A vector path: a list of verbs and the points they consume. This is the one
/// geometry primitive in the v2 IR. A rectangle and a rounded rectangle are just
/// paths built by the helpers below, so the renderer has a single geometry path
/// to support and the IR maps cleanly onto Vello later.
/// </summary>
public sealed class Path
{
    public Path(IReadOnlyList<PathVerb> verbs, IReadOnlyList<Vector2> points)
    {
        Verbs = verbs;
        Points = points;
    }

    public IReadOnlyList<PathVerb> Verbs { get; }
    public IReadOnlyList<Vector2> Points { get; }

    /// <summary>Builds an axis-aligned rectangle path.</summary>
    public static Path Rect(PxRect rect)
    {
        var builder = new PathBuilder();
        builder.MoveTo(new Vector2(rect.X, rect.Y));
        builder.LineTo(new Vector2(rect.Right, rect.Y));
        builder.LineTo(new Vector2(rect.Right, rect.Bottom));
        builder.LineTo(new Vector2(rect.X, rect.Bottom));
        builder.Close();
        return builder.Build();
    }

    /// <summary>Builds a rounded rectangle path using cubic-bezier corners.</summary>
    public static Path RoundedRect(PxRect rect, float radius)
    {
        float r = Math.Min(radius, Math.Min(rect.Width, rect.Height) * 0.5f);
        if (r <= 0f)
        {
            return Rect(rect);
        }

        // Distance from a corner to the bezier control point for a circular arc.
        const float kappa = 0.5522847498f;
        float c = r * (1f - kappa);

        float x = rect.X, y = rect.Y, right = rect.Right, bottom = rect.Bottom;
        var builder = new PathBuilder();
        builder.MoveTo(new Vector2(x + r, y));
        builder.LineTo(new Vector2(right - r, y));
        builder.CubicTo(new Vector2(right - c, y), new Vector2(right, y + c), new Vector2(right, y + r));
        builder.LineTo(new Vector2(right, bottom - r));
        builder.CubicTo(new Vector2(right, bottom - c), new Vector2(right - c, bottom), new Vector2(right - r, bottom));
        builder.LineTo(new Vector2(x + r, bottom));
        builder.CubicTo(new Vector2(x + c, bottom), new Vector2(x, bottom - c), new Vector2(x, bottom - r));
        builder.LineTo(new Vector2(x, y + r));
        builder.CubicTo(new Vector2(x, y + c), new Vector2(x + c, y), new Vector2(x + r, y));
        builder.Close();
        return builder.Build();
    }
}
