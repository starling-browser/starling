// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.PixelFormats;

namespace Starling.Paint.Svg;

/// <summary>
/// The resolved presentation state that cascades down the SVG element tree:
/// fill / stroke paints, stroke width, fill rule, and the three opacity
/// channels. A child clones its parent's state and overrides only the
/// properties it declares (presentation attributes, the <c>style="…"</c>
/// attribute, and matched <c>&lt;style&gt;</c> class rules).
/// </summary>
internal sealed class SvgStyle
{
    /// <summary>Fill paint, or null when <c>fill="none"</c>.</summary>
    public Color? Fill { get; set; } = Color.Black; // SVG default fill is black.

    /// <summary>
    /// Paint-server id from <c>fill="url(#id)"</c> (a <c>&lt;pattern&gt;</c> /
    /// gradient reference), or null for a plain color fill. When set it takes
    /// precedence over <see cref="Fill"/>.
    /// </summary>
    public string? FillRef { get; set; }

    /// <summary>
    /// Fallback color from <c>fill="url(#id) &lt;color&gt;"</c>, used when
    /// <see cref="FillRef"/> cannot be resolved.
    /// </summary>
    public Color? FillFallback { get; set; }

    /// <summary>Stroke paint, or null when there is no stroke.</summary>
    public Color? Stroke { get; set; }

    /// <summary>
    /// Paint-server id from <c>stroke="url(#id)"</c>, analogous to
    /// <see cref="FillRef"/> for the stroke paint.
    /// </summary>
    public string? StrokeRef { get; set; }

    public float StrokeWidth { get; set; } = 1f;

    public bool FillEvenOdd { get; set; }

    public float Opacity { get; set; } = 1f;
    public float FillOpacity { get; set; } = 1f;
    public float StrokeOpacity { get; set; } = 1f;

    /// <summary>
    /// Group-level opacity for <c>&lt;g opacity="…"&gt;</c>. Distinct from
    /// <see cref="Opacity"/> (which folds into each child's alpha directly).
    /// When rendering a group, a value less than 1 triggers offscreen
    /// compositing so overlapping children don't double-blend.
    /// </summary>
    public float GroupOpacity { get; set; } = 1f;

    /// <summary>
    /// Stroke dash array lengths. null or empty means solid stroke.
    /// Values are in user units; the pen builder scales them by stroke width.
    /// </summary>
    public float[]? StrokeDashArray { get; set; }

    /// <summary>Stroke dash offset (user units).</summary>
    public float StrokeDashOffset { get; set; }

    public LineCap StrokeLineCap { get; set; } = LineCap.Butt;
    public LineJoin StrokeLineJoin { get; set; } = LineJoin.Miter;
    public double StrokeMiterLimit { get; set; } = 4.0;

    /// <summary>The inherited <c>currentColor</c> (CSS <c>color</c>).</summary>
    public Color CurrentColor { get; set; } = Color.Black;

    /// <summary><c>display:none</c> removes the element and its subtree. Not
    /// inherited — it is resolved per element and reset on <see cref="Clone"/>.</summary>
    public bool DisplayNone { get; set; }

    /// <summary><c>visibility</c>: false hides this element's own geometry but,
    /// unlike <see cref="DisplayNone"/>, still lets a descendant re-show itself
    /// with <c>visibility:visible</c>. Inherited.</summary>
    public bool Visible { get; set; } = true;

    /// <summary><c>paint-order</c>: when true the stroke is painted before the
    /// fill (i.e. <c>paint-order:stroke</c>). Inherited.</summary>
    public bool PaintOrderStrokeFirst { get; set; }

    /// <summary><c>mix-blend-mode</c> for this element's paint, or
    /// <see cref="PixelColorBlendingMode.Normal"/>. Not inherited.</summary>
    public PixelColorBlendingMode BlendMode { get; set; } = PixelColorBlendingMode.Normal;

    /// <summary>Marker references (ids) for <c>marker-start/-mid/-end</c>, or
    /// null. Markers apply to path/line/polyline/polygon. Inherited.</summary>
    public string? MarkerStart { get; set; }
    public string? MarkerMid { get; set; }
    public string? MarkerEnd { get; set; }

    public SvgStyle Clone() => new()
    {
        Fill = Fill,
        FillRef = FillRef,
        FillFallback = FillFallback,
        Stroke = Stroke,
        StrokeRef = StrokeRef,
        StrokeWidth = StrokeWidth,
        FillEvenOdd = FillEvenOdd,
        Opacity = Opacity,
        FillOpacity = FillOpacity,
        StrokeOpacity = StrokeOpacity,
        GroupOpacity = GroupOpacity,
        StrokeDashArray = StrokeDashArray,
        StrokeDashOffset = StrokeDashOffset,
        StrokeLineCap = StrokeLineCap,
        StrokeLineJoin = StrokeLineJoin,
        StrokeMiterLimit = StrokeMiterLimit,
        CurrentColor = CurrentColor,
        // DisplayNone is deliberately NOT inherited (per-element).
        Visible = Visible,
        PaintOrderStrokeFirst = PaintOrderStrokeFirst,
        MarkerStart = MarkerStart,
        MarkerMid = MarkerMid,
        MarkerEnd = MarkerEnd,
    };

    /// <summary>
    /// Effective fill color folding fill-opacity and the group opacity into the
    /// alpha channel, or null when there is no fill paint or it is fully
    /// transparent.
    /// </summary>
    public Color? EffectiveFill()
        => Apply(Fill, FillOpacity * Opacity);

    /// <summary>Effective stroke color, or null when there is no stroke.</summary>
    public Color? EffectiveStroke()
        => Apply(Stroke, StrokeOpacity * Opacity);

    private static Color? Apply(Color? c, float alphaScale)
    {
        if (c is null)
        {
            return null;
        }

        var px = c.Value.ToPixel<SixLabors.ImageSharp.PixelFormats.Rgba32>();
        float a = px.A / 255f * Math.Clamp(alphaScale, 0f, 1f);
        if (a <= 0f)
        {
            return null;
        }

        px.A = (byte)Math.Clamp((int)Math.Round(a * 255f), 0, 255);
        return Color.FromPixel(px);
    }

    /// <summary>
    /// Apply one <c>property:value</c> declaration to this style. Unknown
    /// properties are ignored. Used for both presentation attributes and the
    /// parsed contents of <c>style="…"</c> / matched class rules.
    /// </summary>
    public void ApplyDeclaration(string property, string value)
    {
        value = value.Trim();
        switch (property.Trim().ToLowerInvariant())
        {
            case "fill":
                // `fill="url(#id)"` references a paint server (a <pattern> or
                // gradient). Capture the id and let DrawShape resolve it. An
                // optional trailing fallback color is parsed and stored.
                if (TryFuncIri(value, out var fillId, out var fillFallbackStr))
                {
                    FillRef = fillId;
                    if (fillFallbackStr is not null
                        && SvgColor.TryParse(fillFallbackStr, CurrentColor, out var fb, out var fbNone))
                    {
                        FillFallback = fbNone ? null : fb;
                    }
                    else
                    {
                        FillFallback = null;
                    }
                }
                else if (SvgColor.TryParse(value, CurrentColor, out var f, out var fNone))
                {
                    Fill = fNone ? null : f;
                    FillRef = null;
                    FillFallback = null;
                }
                break;
            case "stroke":
                if (TryFuncIri(value, out var strokeId, out _))
                {
                    StrokeRef = strokeId;
                }
                else if (SvgColor.TryParse(value, CurrentColor, out var s, out var sNone))
                {
                    Stroke = sNone ? null : s;
                    StrokeRef = null;
                }
                break;
            case "stroke-width":
                if (TryLength(value, out var sw))
                {
                    StrokeWidth = sw;
                }

                break;
            case "fill-rule":
                FillEvenOdd = value.Equals("evenodd", StringComparison.OrdinalIgnoreCase);
                break;
            case "opacity":
                // `opacity` on a <g> sets the group opacity for compositing.
                if (TryNumber(value, out var o))
                {
                    Opacity = Math.Clamp(o, 0f, 1f);
                    GroupOpacity = Opacity;
                }
                break;
            case "fill-opacity":
                if (TryNumber(value, out var fo))
                {
                    FillOpacity = Math.Clamp(fo, 0f, 1f);
                }

                break;
            case "stroke-opacity":
                if (TryNumber(value, out var so))
                {
                    StrokeOpacity = Math.Clamp(so, 0f, 1f);
                }

                break;
            case "color":
                if (SvgColor.TryParse(value, CurrentColor, out var cc, out var ccNone) && !ccNone)
                {
                    CurrentColor = cc;
                }

                break;
            case "stroke-dasharray":
                StrokeDashArray = ParseDashArray(value);
                break;
            case "stroke-dashoffset":
                if (TryLength(value, out var sdo))
                {
                    StrokeDashOffset = sdo;
                }

                break;
            case "stroke-linecap":
                StrokeLineCap = value.Trim().ToLowerInvariant() switch
                {
                    "round" => LineCap.Round,
                    "square" => LineCap.Square,
                    _ => LineCap.Butt,
                };
                break;
            case "stroke-linejoin":
                StrokeLineJoin = value.Trim().ToLowerInvariant() switch
                {
                    "round" => LineJoin.Round,
                    "bevel" => LineJoin.Bevel,
                    _ => LineJoin.Miter,
                };
                break;
            case "stroke-miterlimit":
                if (TryNumber(value, out var ml))
                {
                    StrokeMiterLimit = Math.Max(1.0, ml);
                }

                break;
            case "display":
                // Any 'none' hides the subtree; every other value (inline, block,
                // …) shows it. 'display' is not inherited.
                DisplayNone = value.Equals("none", StringComparison.OrdinalIgnoreCase);
                break;
            case "visibility":
                if (value.Equals("hidden", StringComparison.OrdinalIgnoreCase)
                    || value.Equals("collapse", StringComparison.OrdinalIgnoreCase))
                {
                    Visible = false;
                }
                else if (value.Equals("visible", StringComparison.OrdinalIgnoreCase))
                {
                    Visible = true;
                }

                break;
            case "marker-start":
                MarkerStart = MarkerRef(value);
                break;
            case "marker-mid":
                MarkerMid = MarkerRef(value);
                break;
            case "marker-end":
                MarkerEnd = MarkerRef(value);
                break;
            case "marker":
                MarkerStart = MarkerMid = MarkerEnd = MarkerRef(value);
                break;
            case "mix-blend-mode":
                BlendMode = value.Trim().ToLowerInvariant() switch
                {
                    "multiply" => PixelColorBlendingMode.Multiply,
                    "screen" => PixelColorBlendingMode.Screen,
                    "darken" => PixelColorBlendingMode.Darken,
                    "lighten" => PixelColorBlendingMode.Lighten,
                    "overlay" => PixelColorBlendingMode.Overlay,
                    "hard-light" => PixelColorBlendingMode.HardLight,
                    _ => PixelColorBlendingMode.Normal,
                };
                break;
            case "paint-order":
                // The first painted layer decides ordering for our fill/stroke
                // model: 'stroke …' strokes first, anything else fills first.
                PaintOrderStrokeFirst = value.TrimStart()
                    .StartsWith("stroke", StringComparison.OrdinalIgnoreCase);
                break;
        }
    }

    /// <summary>Parse a marker reference: <c>url(#id)</c> → id, anything else (incl. <c>none</c>) → null.</summary>
    private static string? MarkerRef(string value)
        => TryFuncIri(value, out var id, out _) ? id : null;

    private static bool TryNumber(string v, out float value)
        => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    /// <summary>
    /// Parse a CSS funciri <c>url(#id)</c> (optionally quoted) into its fragment
    /// id. A trailing fallback paint (e.g. <c>url(#p) red</c>) is captured in
    /// <paramref name="fallback"/>.
    /// </summary>
    private static bool TryFuncIri(string value, out string id, out string? fallback)
    {
        id = string.Empty;
        fallback = null;
        var v = value.TrimStart();
        if (!v.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int close = v.IndexOf(')');
        if (close < 0)
        {
            return false;
        }

        var inner = v[4..close].Trim().Trim('"', '\'').Trim();
        if (!inner.StartsWith('#'))
        {
            return false;
        }

        inner = inner[1..].Trim();
        if (inner.Length == 0)
        {
            return false;
        }

        id = inner;
        // Anything after the closing ')' is the optional fallback.
        var tail = v[(close + 1)..].Trim();
        if (tail.Length > 0)
        {
            fallback = tail;
        }

        return true;
    }

    /// <summary>
    /// Parse a length that may carry a <c>px</c> suffix (the only unit icons
    /// use for stroke width in practice); other units are ignored as 0-stripped
    /// numbers. Percentages are not supported here (rare for stroke-width).
    /// </summary>
    private static bool TryLength(string v, out float value)
    {
        v = v.Trim();
        if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            v = v[..^2].Trim();
        }

        return TryNumber(v, out value);
    }

    /// <summary>
    /// Parse a <c>stroke-dasharray</c> value: a list of lengths separated by
    /// commas or whitespace. Returns null for <c>"none"</c>.
    /// SVG 1.1 §11.4: an odd-length list is duplicated to even length.
    /// </summary>
    private static float[]? ParseDashArray(string v)
    {
        v = v.Trim();
        if (v.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = v.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        var vals = new List<float>(parts.Length);
        foreach (var p in parts)
        {
            var t = p.Trim();
            if (t.EndsWith('%'))
            {
                continue; // skip percentage dash lengths
            }
            // Strip trailing unit.
            int end = t.Length;
            while (end > 0 && char.IsLetter(t[end - 1]))
            {
                end--;
            }

            if (float.TryParse(t.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) && f >= 0)
            {
                vals.Add(f);
            }
        }
        if (vals.Count == 0)
        {
            return null;
        }
        // Duplicate odd-length list.
        if (vals.Count % 2 != 0)
        {
            int n = vals.Count;
            for (int i = 0; i < n; i++)
            {
                vals.Add(vals[i]);
            }
        }
        return vals.ToArray();
    }

    /// <summary>
    /// Parse a <c>style="a:b;c:d"</c> string into this style.
    /// </summary>
    public void ApplyStyleString(string? style)
    {
        if (string.IsNullOrWhiteSpace(style))
        {
            return;
        }

        foreach (var decl in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = decl.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            ApplyDeclaration(decl[..colon], decl[(colon + 1)..]);
        }
    }
}
