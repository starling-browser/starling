using System.Globalization;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;

namespace Starling.Paint.Svg;

/// <summary>
/// Parses an SVG <c>path</c> <c>d</c> attribute into an ImageSharp
/// <see cref="IPath"/> in user space. Supports the full first-cut command set:
/// <c>M m L l H h V v C c S s Q q T t A a Z z</c> (absolute + relative).
/// </summary>
/// <remarks>
/// Elliptical arcs (<c>A</c>/<c>a</c>) are converted to cubic Béziers using the
/// SVG-implementation-notes endpoint→center parameterization (F.6), then split
/// into ≤90° segments. Degenerate arcs (zero radius, coincident endpoints) fall
/// back to a straight line, as the spec requires.
/// </remarks>
internal static class SvgPathParser
{
    /// <summary>
    /// Build an <see cref="IPath"/> from a path <c>d</c> string. Returns
    /// <c>null</c> when the data is empty or yields no drawable geometry.
    /// </summary>
    public static IPath? Parse(string? d)
    {
        if (string.IsNullOrWhiteSpace(d))
        {
            return null;
        }

        var tok = new PathTokenizer(d);
        var pb = new PathBuilder();

        Vector2 current = Vector2.Zero;     // current point
        Vector2 start = Vector2.Zero;       // subpath start (for Z)
        Vector2 lastCubicCtrl = Vector2.Zero;
        Vector2 lastQuadCtrl = Vector2.Zero;
        char lastCmd = '\0';
        bool open = false;                  // a figure is being built
        bool any = false;

        char cmd = '\0';
        while (tok.TryReadCommand(ref cmd))
        {
            bool rel = char.IsLower(cmd);
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                    {
                        // First pair is a moveto; subsequent pairs are implicit linetos.
                        if (!tok.TryReadFloat(out var x) || !tok.TryReadFloat(out var y))
                        {
                            return Done(pb, any);
                        }

                        var p = rel ? current + new Vector2(x, y) : new Vector2(x, y);
                        if (open)
                        {
                            pb.StartFigure();
                        }

                        pb.MoveTo(ToPoint(p));
                        current = start = p;
                        open = true;
                        lastCmd = 'M';
                        // implicit linetos
                        while (tok.TryReadFloat(out var lx))
                        {
                            if (!tok.TryReadFloat(out var ly))
                            {
                                break;
                            }

                            var lp = rel ? current + new Vector2(lx, ly) : new Vector2(lx, ly);
                            pb.LineTo(ToPoint(lp));
                            current = lp; any = true; lastCmd = 'L';
                        }
                        break;
                    }
                case 'L':
                    {
                        while (tok.TryReadFloat(out var x))
                        {
                            if (!tok.TryReadFloat(out var y))
                            {
                                break;
                            }

                            var p = rel ? current + new Vector2(x, y) : new Vector2(x, y);
                            pb.LineTo(ToPoint(p));
                            current = p; any = true;
                        }
                        lastCmd = 'L';
                        break;
                    }
                case 'H':
                    {
                        while (tok.TryReadFloat(out var x))
                        {
                            var p = new Vector2(rel ? current.X + x : x, current.Y);
                            pb.LineTo(ToPoint(p));
                            current = p; any = true;
                        }
                        lastCmd = 'H';
                        break;
                    }
                case 'V':
                    {
                        while (tok.TryReadFloat(out var y))
                        {
                            var p = new Vector2(current.X, rel ? current.Y + y : y);
                            pb.LineTo(ToPoint(p));
                            current = p; any = true;
                        }
                        lastCmd = 'V';
                        break;
                    }
                case 'C':
                    {
                        while (tok.TryReadFloat(out var x1))
                        {
                            if (!tok.TryReadFloat(out var y1) ||
                                !tok.TryReadFloat(out var x2) || !tok.TryReadFloat(out var y2) ||
                                !tok.TryReadFloat(out var x) || !tok.TryReadFloat(out var y))
                            {
                                break;
                            }

                            var c1 = rel ? current + new Vector2(x1, y1) : new Vector2(x1, y1);
                            var c2 = rel ? current + new Vector2(x2, y2) : new Vector2(x2, y2);
                            var p = rel ? current + new Vector2(x, y) : new Vector2(x, y);
                            pb.AddCubicBezier(ToPoint(current), ToPoint(c1), ToPoint(c2), ToPoint(p));
                            lastCubicCtrl = c2; current = p; any = true;
                        }
                        lastCmd = 'C';
                        break;
                    }
                case 'S':
                    {
                        while (tok.TryReadFloat(out var x2))
                        {
                            if (!tok.TryReadFloat(out var y2) ||
                                !tok.TryReadFloat(out var x) || !tok.TryReadFloat(out var y))
                            {
                                break;
                            }
                            // Reflect previous cubic control point if last was C/S.
                            var c1 = (lastCmd is 'C' or 'S')
                                ? current + (current - lastCubicCtrl)
                                : current;
                            var c2 = rel ? current + new Vector2(x2, y2) : new Vector2(x2, y2);
                            var p = rel ? current + new Vector2(x, y) : new Vector2(x, y);
                            pb.AddCubicBezier(ToPoint(current), ToPoint(c1), ToPoint(c2), ToPoint(p));
                            lastCubicCtrl = c2; current = p; any = true; lastCmd = 'S';
                        }
                        lastCmd = 'S';
                        break;
                    }
                case 'Q':
                    {
                        while (tok.TryReadFloat(out var x1))
                        {
                            if (!tok.TryReadFloat(out var y1) ||
                                !tok.TryReadFloat(out var x) || !tok.TryReadFloat(out var y))
                            {
                                break;
                            }

                            var c = rel ? current + new Vector2(x1, y1) : new Vector2(x1, y1);
                            var p = rel ? current + new Vector2(x, y) : new Vector2(x, y);
                            pb.AddQuadraticBezier(ToPoint(current), ToPoint(c), ToPoint(p));
                            lastQuadCtrl = c; current = p; any = true;
                        }
                        lastCmd = 'Q';
                        break;
                    }
                case 'T':
                    {
                        while (tok.TryReadFloat(out var x))
                        {
                            if (!tok.TryReadFloat(out var y))
                            {
                                break;
                            }

                            var c = (lastCmd is 'Q' or 'T')
                                ? current + (current - lastQuadCtrl)
                                : current;
                            var p = rel ? current + new Vector2(x, y) : new Vector2(x, y);
                            pb.AddQuadraticBezier(ToPoint(current), ToPoint(c), ToPoint(p));
                            lastQuadCtrl = c; current = p; any = true; lastCmd = 'T';
                        }
                        lastCmd = 'T';
                        break;
                    }
                case 'A':
                    {
                        while (tok.TryReadFloat(out var rx))
                        {
                            if (!tok.TryReadFloat(out var ry) ||
                                !tok.TryReadFloat(out var xRot) ||
                                !tok.TryReadFlag(out var largeArc) ||
                                !tok.TryReadFlag(out var sweep) ||
                                !tok.TryReadFloat(out var x) || !tok.TryReadFloat(out var y))
                            {
                                break;
                            }

                            var p = rel ? current + new Vector2(x, y) : new Vector2(x, y);
                            AppendArc(pb, current, p, rx, ry, xRot, largeArc, sweep);
                            current = p; any = true;
                        }
                        lastCmd = 'A';
                        break;
                    }
                case 'Z':
                    {
                        if (open)
                        {
                            pb.CloseFigure();
                            current = start;
                            open = false;
                            any = true;
                        }
                        lastCmd = 'Z';
                        break;
                    }
                default:
                    // Unknown command: stop parsing gracefully.
                    return Done(pb, any);
            }
        }

        return Done(pb, any);
    }

    private static IPath? Done(PathBuilder pb, bool any)
        => any ? pb.Build() : null;

    private static PointF ToPoint(Vector2 v) => new(v.X, v.Y);

    /// <summary>
    /// Convert one SVG elliptical-arc segment to cubic Béziers and append them.
    /// Endpoint → center parameterization per SVG 1.1 Appendix F.6.5.
    /// </summary>
    private static void AppendArc(
        PathBuilder pb, Vector2 from, Vector2 to,
        float rx, float ry, float xAxisRotationDeg, bool largeArc, bool sweep)
    {
        // Out-of-range radii: treat coincident endpoints / zero radius as a line.
        if (from == to)
        {
            return;
        }

        rx = Math.Abs(rx);
        ry = Math.Abs(ry);
        if (rx == 0f || ry == 0f)
        {
            pb.AddLine(ToPoint(from), ToPoint(to));
            return;
        }

        double phi = xAxisRotationDeg * Math.PI / 180.0;
        double cosPhi = Math.Cos(phi), sinPhi = Math.Sin(phi);

        // Step 1: compute (x1', y1').
        double dx2 = (from.X - to.X) / 2.0;
        double dy2 = (from.Y - to.Y) / 2.0;
        double x1p = cosPhi * dx2 + sinPhi * dy2;
        double y1p = -sinPhi * dx2 + cosPhi * dy2;

        double rxs = rx * rx, rys = ry * ry;
        double x1ps = x1p * x1p, y1ps = y1p * y1p;

        // Correct out-of-range radii (F.6.6).
        double lambda = x1ps / rxs + y1ps / rys;
        if (lambda > 1.0)
        {
            double s = Math.Sqrt(lambda);
            rx = (float)(s * rx); ry = (float)(s * ry);
            rxs = (double)rx * rx; rys = (double)ry * ry;
        }

        // Step 2: compute (cx', cy').
        double num = rxs * rys - rxs * y1ps - rys * x1ps;
        double den = rxs * y1ps + rys * x1ps;
        double factor = den == 0 ? 0 : Math.Sqrt(Math.Max(0.0, num / den));
        if (largeArc == sweep)
        {
            factor = -factor;
        }

        double cxp = factor * (rx * y1p / ry);
        double cyp = factor * -(ry * x1p / rx);

        // Step 3: compute (cx, cy) from (cx', cy').
        double cx = cosPhi * cxp - sinPhi * cyp + (from.X + to.X) / 2.0;
        double cy = sinPhi * cxp + cosPhi * cyp + (from.Y + to.Y) / 2.0;

        // Step 4: compute theta1 and deltaTheta.
        double ux = (x1p - cxp) / rx, uy = (y1p - cyp) / ry;
        double vx = (-x1p - cxp) / rx, vy = (-y1p - cyp) / ry;

        double theta1 = Angle(1, 0, ux, uy);
        double deltaTheta = Angle(ux, uy, vx, vy);

        if (!sweep && deltaTheta > 0)
        {
            deltaTheta -= 2 * Math.PI;
        }
        else if (sweep && deltaTheta < 0)
        {
            deltaTheta += 2 * Math.PI;
        }

        // Split into ≤90° segments and emit a cubic for each.
        int segments = (int)Math.Ceiling(Math.Abs(deltaTheta) / (Math.PI / 2.0));
        if (segments == 0)
        {
            segments = 1;
        }

        double delta = deltaTheta / segments;
        double t = (4.0 / 3.0) * Math.Tan(delta / 4.0);

        double theta = theta1;
        var cur = from;
        for (int i = 0; i < segments; i++)
        {
            double thetaNext = theta + delta;

            double cosT1 = Math.Cos(theta), sinT1 = Math.Sin(theta);
            double cosT2 = Math.Cos(thetaNext), sinT2 = Math.Sin(thetaNext);

            // Endpoint of this segment in ellipse space, then rotated/translated.
            var e2 = EllipsePoint(cx, cy, rx, ry, cosPhi, sinPhi, cosT2, sinT2);

            // Control points (derivative-based).
            var d1 = EllipseDerivative(rx, ry, cosPhi, sinPhi, cosT1, sinT1);
            var d2 = EllipseDerivative(rx, ry, cosPhi, sinPhi, cosT2, sinT2);

            var c1 = new Vector2((float)(cur.X + t * d1.X), (float)(cur.Y + t * d1.Y));
            var c2 = new Vector2((float)(e2.X - t * d2.X), (float)(e2.Y - t * d2.Y));

            pb.AddCubicBezier(ToPoint(cur), ToPoint(c1), ToPoint(c2), ToPoint(e2));

            cur = e2;
            theta = thetaNext;
        }
    }

    private static Vector2 EllipsePoint(
        double cx, double cy, double rx, double ry,
        double cosPhi, double sinPhi, double cosT, double sinT)
    {
        double x = cosPhi * rx * cosT - sinPhi * ry * sinT + cx;
        double y = sinPhi * rx * cosT + cosPhi * ry * sinT + cy;
        return new Vector2((float)x, (float)y);
    }

    private static Vector2 EllipseDerivative(
        double rx, double ry, double cosPhi, double sinPhi, double cosT, double sinT)
    {
        double x = -cosPhi * rx * sinT - sinPhi * ry * cosT;
        double y = -sinPhi * rx * sinT + cosPhi * ry * cosT;
        return new Vector2((float)x, (float)y);
    }

    private static double Angle(double ux, double uy, double vx, double vy)
    {
        double dot = ux * vx + uy * vy;
        double len = Math.Sqrt((ux * ux + uy * uy) * (vx * vx + vy * vy));
        double cos = len == 0 ? 0 : Math.Clamp(dot / len, -1.0, 1.0);
        double ang = Math.Acos(cos);
        if (ux * vy - uy * vx < 0)
        {
            ang = -ang;
        }

        return ang;
    }

    /// <summary>
    /// Lexer over path-data: yields commands and numbers, treating commas and
    /// whitespace as separators. Numbers may run together (e.g. "1.5.5" =
    /// 1.5, 0.5; "1-2" = 1, -2) per the SVG grammar.
    /// </summary>
    private struct PathTokenizer
    {
        private readonly string _s;
        private int _i;

        public PathTokenizer(string s) { _s = s; _i = 0; }

        public bool TryReadCommand(ref char cmd)
        {
            SkipSeparators();
            if (_i >= _s.Length)
            {
                return false;
            }

            char c = _s[_i];
            // A command is a single ASCII letter (M/L/H/V/C/S/Q/T/A/Z, any
            // case). 'e'/'E' never start a command — they only appear inside
            // exponent notation, which TryReadFloat consumes. The per-command
            // inner loops already handle implicit repetition of coordinate
            // sets, so the outer loop only ever needs to read an explicit
            // letter here.
            if (char.IsAsciiLetter(c))
            {
                cmd = c;
                _i++;
                return true;
            }
            return false;
        }

        public bool TryReadFloat(out float value)
        {
            value = 0;
            SkipSeparators();
            int startTok = _i;
            if (_i >= _s.Length)
            {
                return false;
            }

            int start = _i;
            // optional sign
            if (_s[_i] is '+' or '-')
            {
                _i++;
            }

            bool sawDigit = false, sawDot = false;
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (char.IsDigit(c)) { sawDigit = true; _i++; }
                else if (c == '.' && !sawDot) { sawDot = true; _i++; }
                else if ((c is 'e' or 'E') && sawDigit)
                {
                    _i++;
                    if (_i < _s.Length && _s[_i] is '+' or '-')
                    {
                        _i++;
                    }
                }
                else
                {
                    break;
                }
            }
            if (!sawDigit) { _i = startTok; return false; }

            var span = _s.AsSpan(start, _i - start);
            if (!float.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                _i = startTok;
                return false;
            }
            return true;
        }

        /// <summary>Arc flags are a single '0' or '1' with no sign/decimal.</summary>
        public bool TryReadFlag(out bool value)
        {
            value = false;
            SkipSeparators();
            if (_i >= _s.Length)
            {
                return false;
            }

            char c = _s[_i];
            if (c == '0') { _i++; value = false; return true; }
            if (c == '1') { _i++; value = true; return true; }
            return false;
        }

        private void SkipSeparators()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c is ' ' or '\t' or '\r' or '\n' or ',')
                {
                    _i++;
                }
                else
                {
                    break;
                }
            }
        }
    }
}
