using System.Globalization;
using System.Numerics;

namespace Starling.Paint.Svg;

/// <summary>
/// Parses an SVG <c>transform</c> attribute into a <see cref="Matrix3x2"/>.
/// Supports the function list <c>translate</c>, <c>scale</c>, <c>rotate</c>
/// (optionally about a center), <c>matrix</c>, <c>skewX</c>, and <c>skewY</c>,
/// composed left-to-right (the SVG order).
/// </summary>
internal static class SvgTransform
{
    public static Matrix3x2 Parse(string? transform)
    {
        var m = Matrix3x2.Identity;
        if (string.IsNullOrWhiteSpace(transform))
        {
            return m;
        }

        int i = 0;
        while (i < transform.Length)
        {
            // function name
            while (i < transform.Length && !char.IsLetter(transform[i]))
            {
                i++;
            }

            int nameStart = i;
            while (i < transform.Length && char.IsLetter(transform[i]))
            {
                i++;
            }

            if (i >= transform.Length)
            {
                break;
            }

            var name = transform[nameStart..i];

            int open = transform.IndexOf('(', i);
            if (open < 0)
            {
                break;
            }

            int close = transform.IndexOf(')', open + 1);
            if (close < 0)
            {
                break;
            }

            var args = ParseNumbers(transform[(open + 1)..close]);
            i = close + 1;

            var t = Build(name, args);
            // SVG composes in document order: the leftmost function is applied
            // last to a point, i.e. result = T1 * T2 * ... . With row-vector
            // Matrix3x2 (point * M), that means m = current * t.
            m = t * m;
        }

        return m;
    }

    private static Matrix3x2 Build(string name, float[] a)
    {
        switch (name.ToLowerInvariant())
        {
            case "translate":
                return Matrix3x2.CreateTranslation(a.Length > 0 ? a[0] : 0, a.Length > 1 ? a[1] : 0);
            case "scale":
                return Matrix3x2.CreateScale(a.Length > 0 ? a[0] : 1, a.Length > 1 ? a[1] : (a.Length > 0 ? a[0] : 1));
            case "rotate":
                {
                    float deg = a.Length > 0 ? a[0] : 0;
                    float rad = deg * MathF.PI / 180f;
                    if (a.Length >= 3)
                    {
                        return Matrix3x2.CreateRotation(rad, new Vector2(a[1], a[2]));
                    }

                    return Matrix3x2.CreateRotation(rad);
                }
            case "matrix":
                if (a.Length >= 6)
                {
                    return new Matrix3x2(a[0], a[1], a[2], a[3], a[4], a[5]);
                }

                return Matrix3x2.Identity;
            case "skewx":
                {
                    float rad = (a.Length > 0 ? a[0] : 0) * MathF.PI / 180f;
                    return new Matrix3x2(1, 0, MathF.Tan(rad), 1, 0, 0);
                }
            case "skewy":
                {
                    float rad = (a.Length > 0 ? a[0] : 0) * MathF.PI / 180f;
                    return new Matrix3x2(1, MathF.Tan(rad), 0, 1, 0, 0);
                }
            default:
                return Matrix3x2.Identity;
        }
    }

    private static float[] ParseNumbers(string s)
    {
        var list = new List<float>();
        int i = 0;
        while (i < s.Length)
        {
            while (i < s.Length && (s[i] is ' ' or '\t' or '\r' or '\n' or ','))
            {
                i++;
            }

            if (i >= s.Length)
            {
                break;
            }

            int start = i;
            if (s[i] is '+' or '-')
            {
                i++;
            }

            bool dot = false, digit = false;
            while (i < s.Length)
            {
                char c = s[i];
                if (char.IsDigit(c)) { digit = true; i++; }
                else if (c == '.' && !dot) { dot = true; i++; }
                else if ((c is 'e' or 'E') && digit) { i++; if (i < s.Length && s[i] is '+' or '-')
                    {
                        i++;
                    }
                }
                else
                {
                    break;
                }
            }
            if (!digit) { i++; continue; }
            if (float.TryParse(s.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                list.Add(v);
            }
        }
        return list.ToArray();
    }
}
