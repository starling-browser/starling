// SPDX-License-Identifier: Apache-2.0
using System.Numerics;

namespace Starling.Scene;

/// <summary>Builds a <see cref="Path"/> from verbs and points. Reusable for arbitrary geometry, not just rects.</summary>
public sealed class PathBuilder
{
    private readonly List<PathVerb> _verbs = [];
    private readonly List<Vector2> _points = [];

    public PathBuilder MoveTo(Vector2 point)
    {
        _verbs.Add(PathVerb.MoveTo);
        _points.Add(point);
        return this;
    }

    public PathBuilder LineTo(Vector2 point)
    {
        _verbs.Add(PathVerb.LineTo);
        _points.Add(point);
        return this;
    }

    public PathBuilder QuadTo(Vector2 control, Vector2 end)
    {
        _verbs.Add(PathVerb.QuadTo);
        _points.Add(control);
        _points.Add(end);
        return this;
    }

    public PathBuilder CubicTo(Vector2 control1, Vector2 control2, Vector2 end)
    {
        _verbs.Add(PathVerb.CubicTo);
        _points.Add(control1);
        _points.Add(control2);
        _points.Add(end);
        return this;
    }

    public PathBuilder Close()
    {
        _verbs.Add(PathVerb.Close);
        return this;
    }

    public Path Build() => new([.. _verbs], [.. _points]);
}
