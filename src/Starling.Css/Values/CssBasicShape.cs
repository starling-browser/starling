// SPDX-License-Identifier: Apache-2.0
using System.Collections.Frozen;

namespace Starling.Css.Values;

// CSS Shapes 1 §4 / CSS Masking 1 §1 — basic-shape value types.
// Used by clip-path (and shape-outside, shape-inside in the future).

/// <summary>
/// Geometry-box keyword that can accompany a basic shape, or stand alone as
/// a clip-path value. CSS Masking 1 §1 / CSS Shapes 1 §5.
/// </summary>
public enum CssGeometryBox
{
    /// <summary>No geometry box specified; shape uses its own reference box.</summary>
    None,
    MarginBox,
    BorderBox,
    PaddingBox,
    ContentBox,
    FillBox,
    StrokeBox,
    ViewBox,
}

/// <summary>
/// CSS fill-rule (nonzero / evenodd) used by polygon() and SVG shapes.
/// CSS Shapes 1 §4.4.
/// </summary>
public enum CssFillRule { Nonzero, EvenOdd }

/// <summary>
/// A <c>&lt;length-percentage&gt;</c> value stored as either a px length or a
/// percentage, matching what the paint agent can receive before a containing
/// block is known. A <see cref="CssCalc"/> node is carried as-is via the
/// base class.
/// </summary>
public sealed record CssLengthPercentage
{
    private CssLengthPercentage() { }

    /// <summary>Wrap a resolved pixel length.</summary>
    public static CssLengthPercentage FromLength(CssLength l) => new() { Length = l };

    /// <summary>Wrap a percentage value (0–100).</summary>
    public static CssLengthPercentage FromPercentage(double pct) => new() { Percentage = pct, IsPercentage = true };

    /// <summary>Wrap a calc() node that was not fully resolved at parse time.</summary>
    public static CssLengthPercentage FromCalc(CssCalc c) => new() { Calc = c, IsCalc = true };

    public CssLength? Length { get; private init; }
    public bool IsPercentage { get; private init; }
    public double Percentage { get; private init; }
    public bool IsCalc { get; private init; }
    public CssCalc? Calc { get; private init; }

    public bool IsZero => Length is { Value: 0 } || (IsPercentage && Percentage == 0);
}

/// <summary>
/// A 2-D position used by <c>at &lt;position&gt;</c> in circle() / ellipse().
/// Stored as X/Y <see cref="CssLengthPercentage"/>; defaults to 50%/50% when
/// the <c>at</c> clause is absent.
/// </summary>
public sealed record CssShapePosition(CssLengthPercentage X, CssLengthPercentage Y)
{
    /// <summary>50% 50% — the default when <c>at &lt;position&gt;</c> is omitted.</summary>
    public static CssShapePosition Center { get; } =
        new(CssLengthPercentage.FromPercentage(50), CssLengthPercentage.FromPercentage(50));
}

/// <summary>
/// A border-radius pair (horizontal / vertical) used in the
/// <c>round &lt;border-radius&gt;</c> clause of <c>inset()</c>.
/// CSS Shapes 1 §4.1.
/// </summary>
public sealed record CssRadiusPair(CssLengthPercentage H, CssLengthPercentage V);

/// <summary>
/// A single vertex in a <c>polygon()</c> shape.
/// </summary>
public sealed record CssPolygonVertex(CssLengthPercentage X, CssLengthPercentage Y);

/// <summary>
/// Typed representation of a CSS basic-shape function.
/// CSS Shapes 1 §4 / CSS Masking 1.
/// <para>
/// The discriminated union is encoded as a sealed base class with one
/// derived record per shape kind, mirroring <see cref="CssGradient"/>'s
/// sealed record style. Callers switch on the concrete type.
/// </para>
/// </summary>
public abstract record CssBasicShape : CssValue;

/// <summary>
/// <c>circle( [&lt;shape-radius&gt;]? [at &lt;position&gt;]? )</c> — CSS Shapes 1 §4.2.
/// <para>
/// <see cref="Radius"/> is null when the radius is <c>closest-side</c> (the
/// default) or when the keyword <c>closest-side</c> / <c>farthest-side</c> is
/// explicit; <see cref="RadiusKeyword"/> carries the keyword in that case.
/// </para>
/// </summary>
public sealed record CssCircleShape(
    CssLengthPercentage? Radius,
    string? RadiusKeyword,
    CssShapePosition Position) : CssBasicShape;

/// <summary>
/// <c>ellipse( [&lt;shape-radius&gt;{2}]? [at &lt;position&gt;]? )</c> — CSS Shapes 1 §4.3.
/// Radii are null (closest-side default) when omitted.
/// </summary>
public sealed record CssEllipseShape(
    CssLengthPercentage? RadiusX,
    string? RadiusXKeyword,
    CssLengthPercentage? RadiusY,
    string? RadiusYKeyword,
    CssShapePosition Position) : CssBasicShape;

/// <summary>
/// <c>inset( &lt;lp&gt;{1,4} [round &lt;border-radius&gt;]? )</c> — CSS Shapes 1 §4.1.
/// The four offsets are top/right/bottom/left relative to the reference box.
/// The optional round clause carries up to four corner radii (pairs of H/V);
/// a null <see cref="Radii"/> means no rounding.
/// </summary>
public sealed record CssInsetShape(
    CssLengthPercentage Top,
    CssLengthPercentage Right,
    CssLengthPercentage Bottom,
    CssLengthPercentage Left,
    IReadOnlyList<CssRadiusPair>? Radii) : CssBasicShape;

/// <summary>
/// <c>polygon( [&lt;fill-rule&gt;,]? &lt;pair&gt;# )</c> — CSS Shapes 1 §4.4.
/// </summary>
public sealed record CssPolygonShape(
    CssFillRule FillRule,
    IReadOnlyList<CssPolygonVertex> Vertices) : CssBasicShape;

/// <summary>
/// A typed <c>clip-path</c> value that carries either a basic shape, a
/// geometry-box keyword, a combination of both, or a <c>url(#ref)</c>
/// reference to an SVG &lt;clipPath&gt; element.
/// CSS Masking 1 §7.
/// </summary>
public sealed record CssClipPath : CssValue
{
    private CssClipPath() { }

    /// <summary>None — clip-path does not apply.</summary>
    public static CssClipPath None { get; } = new() { IsNone = true };

    /// <summary>Shape only, no geometry-box override.</summary>
    public static CssClipPath FromShape(CssBasicShape shape)
        => new() { Shape = shape, GeometryBox = CssGeometryBox.None };

    /// <summary>Shape + geometry-box reference box.</summary>
    public static CssClipPath FromShapeAndBox(CssBasicShape shape, CssGeometryBox box)
        => new() { Shape = shape, GeometryBox = box };

    /// <summary>Geometry-box keyword alone (no explicit shape; the box itself is the clip).</summary>
    public static CssClipPath FromBox(CssGeometryBox box)
        => new() { GeometryBox = box };

    /// <summary>Fragment-id reference to an SVG &lt;clipPath&gt; element.</summary>
    public static CssClipPath FromUrl(string fragmentId)
        => new() { UrlFragmentId = fragmentId, IsUrl = true };

    public bool IsNone { get; private init; }
    public bool IsUrl { get; private init; }

    /// <summary>SVG &lt;clipPath&gt; fragment id (without the leading #), or null.</summary>
    public string? UrlFragmentId { get; private init; }

    /// <summary>Parsed basic shape, or null when this is a box-only or url() value.</summary>
    public CssBasicShape? Shape { get; private init; }

    /// <summary>Reference geometry box. <see cref="CssGeometryBox.None"/> means not set.</summary>
    public CssGeometryBox GeometryBox { get; private init; }
}

/// <summary>
/// Parser for CSS basic-shape functions and the <c>clip-path</c> property
/// (CSS Masking 1 §7 / CSS Shapes 1 §4).
/// </summary>
public static class CssBasicShapeParser
{
    // Build-once geometry-box keyword map (hot lookup during property parsing).
    private static readonly FrozenDictionary<string, CssGeometryBox> GeometryBoxKeywords =
        new Dictionary<string, CssGeometryBox>(StringComparer.OrdinalIgnoreCase)
        {
            ["margin-box"] = CssGeometryBox.MarginBox,
            ["border-box"] = CssGeometryBox.BorderBox,
            ["padding-box"] = CssGeometryBox.PaddingBox,
            ["content-box"] = CssGeometryBox.ContentBox,
            ["fill-box"] = CssGeometryBox.FillBox,
            ["stroke-box"] = CssGeometryBox.StrokeBox,
            ["view-box"] = CssGeometryBox.ViewBox,
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parse a <c>clip-path</c> property value from the already-parsed CSS value
    /// list. Returns null when the value is not a recognized clip-path value (the
    /// caller should then fall through to the default single-value path).
    /// </summary>
    public static CssClipPath? TryParseClipPath(IReadOnlyList<CssValue> values)
    {
        if (values.Count == 0)
        {
            return null;
        }

        // none
        if (values.Count == 1 && values[0] is CssKeyword { Name: "none" })
        {
            return CssClipPath.None;
        }

        // geometry-box keyword alone
        if (values.Count == 1 && values[0] is CssKeyword kw
            && GeometryBoxKeywords.TryGetValue(kw.Name, out var boxOnly))
        {
            return CssClipPath.FromBox(boxOnly);
        }

        // url(#frag) or url(frag)
        if (values.Count == 1 && values[0] is CssUrl url)
        {
            var frag = url.Value.TrimStart('#');
            return CssClipPath.FromUrl(frag);
        }

        // shape function [ geometry-box ]? | geometry-box shape-function
        CssBasicShape? shape = null;
        CssGeometryBox box = CssGeometryBox.None;

        foreach (var v in values)
        {
            if (v is CssFunctionValue fn)
            {
                shape ??= TryParseShapeFunction(fn);
            }
            else if (v is CssKeyword boxKw && GeometryBoxKeywords.TryGetValue(boxKw.Name, out var b))
            {
                box = b;
            }
        }

        if (shape is not null)
        {
            return box != CssGeometryBox.None
                ? CssClipPath.FromShapeAndBox(shape, box)
                : CssClipPath.FromShape(shape);
        }

        if (box != CssGeometryBox.None)
        {
            return CssClipPath.FromBox(box);
        }

        return null;
    }

    private static CssBasicShape? TryParseShapeFunction(CssFunctionValue fn)
        => fn.Name switch
        {
            "circle" => TryParseCircle(FlattenArgs(fn.Arguments)),
            "ellipse" => TryParseEllipse(FlattenArgs(fn.Arguments)),
            "inset" => TryParseInset(FlattenArgs(fn.Arguments)),
            "polygon" => TryParsePolygon(fn.Arguments), // polygon keeps comma groups
            _ => null,
        };

    /// <summary>
    /// Flatten a function's argument list. CssValueParser.ParseFunction groups
    /// each comma-delimited segment into one Parse() call, so a single-arg
    /// function like <c>inset(10px round 5px)</c> produces
    /// <c>[CssValueList([10px, round, 5px])]</c>. We need a flat list.
    /// </summary>
    private static List<CssValue> FlattenArgs(IReadOnlyList<CssValue> args)
    {
        var flat = new List<CssValue>(args.Count * 2);
        foreach (var v in args)
        {
            if (v is CssValueList list)
            {
                flat.AddRange(list.Values);
            }
            else
            {
                flat.Add(v);
            }
        }
        return flat;
    }

    // -----------------------------------------------------------------------
    // circle( <shape-radius>? [ at <position> ]? )
    // CSS Shapes 1 §4.2
    // -----------------------------------------------------------------------

    private static CssCircleShape? TryParseCircle(IReadOnlyList<CssValue> args)
    {
        CssLengthPercentage? radius = null;
        string? radiusKeyword = null;
        CssShapePosition pos = CssShapePosition.Center;

        var i = 0;
        var flat = args.Where(v => v is not CssKeyword { Name: "" }).ToList();

        // optional <shape-radius>
        if (i < flat.Count && flat[i] is not CssKeyword { Name: "at" })
        {
            if (TryParseShapeRadius(flat[i], out var r, out var rKw))
            {
                radius = r;
                radiusKeyword = rKw;
                i++;
            }
        }

        // optional at <position>
        if (i < flat.Count && flat[i] is CssKeyword { Name: "at" })
        {
            i++; // skip "at"
            pos = ParseShapePosition(flat, ref i);
        }

        return new CssCircleShape(radius, radiusKeyword, pos);
    }

    // -----------------------------------------------------------------------
    // ellipse( [ <shape-radius>{2} ]? [ at <position> ]? )
    // CSS Shapes 1 §4.3
    // -----------------------------------------------------------------------

    private static CssEllipseShape? TryParseEllipse(IReadOnlyList<CssValue> args)
    {
        CssLengthPercentage? rx = null, ry = null;
        string? rxKw = null, ryKw = null;
        CssShapePosition pos = CssShapePosition.Center;

        var i = 0;
        var flat = args.Where(v => v is not CssKeyword { Name: "" }).ToList();

        // optional first <shape-radius>
        if (i < flat.Count && flat[i] is not CssKeyword { Name: "at" })
        {
            if (TryParseShapeRadius(flat[i], out var r, out var rKw))
            {
                rx = r; rxKw = rKw; i++;
            }
        }

        // optional second <shape-radius>
        if (i < flat.Count && flat[i] is not CssKeyword { Name: "at" })
        {
            if (TryParseShapeRadius(flat[i], out var r, out var rKw))
            {
                ry = r; ryKw = rKw; i++;
            }
        }

        // optional at <position>
        if (i < flat.Count && flat[i] is CssKeyword { Name: "at" })
        {
            i++;
            pos = ParseShapePosition(flat, ref i);
        }

        return new CssEllipseShape(rx, rxKw, ry, ryKw, pos);
    }

    // -----------------------------------------------------------------------
    // inset( <length-percentage>{1,4} [ round <border-radius> ]? )
    // CSS Shapes 1 §4.1
    // -----------------------------------------------------------------------

    private static CssInsetShape? TryParseInset(IReadOnlyList<CssValue> args)
    {
        var flat = args.Where(v => v is not CssKeyword { Name: "" }).ToList();
        if (flat.Count == 0)
        {
            return null;
        }

        // Collect offsets up to 4 <length-percentage> values, stopping at "round".
        var offsets = new List<CssLengthPercentage>(4);
        var i = 0;
        while (i < flat.Count && offsets.Count < 4)
        {
            if (flat[i] is CssKeyword { Name: "round" })
            {
                break;
            }

            if (TryParseLengthPercentage(flat[i], out var lp))
            {
                offsets.Add(lp);
                i++;
            }
            else
            {
                break;
            }
        }

        if (offsets.Count == 0)
        {
            return null;
        }

        // Box model: 1→top=right=bottom=left, 2→top+bottom / right+left, etc.
        var top = offsets[0];
        var right = offsets.Count > 1 ? offsets[1] : offsets[0];
        var bottom = offsets.Count > 2 ? offsets[2] : offsets[0];
        var left = offsets.Count > 3 ? offsets[3] : right;

        // optional round <border-radius>
        IReadOnlyList<CssRadiusPair>? radii = null;
        if (i < flat.Count && flat[i] is CssKeyword { Name: "round" })
        {
            i++;
            radii = ParseBorderRadius(flat, ref i);
        }

        return new CssInsetShape(top, right, bottom, left, radii);
    }

    // -----------------------------------------------------------------------
    // polygon( [ <fill-rule>, ]? <pair># )
    // CSS Shapes 1 §4.4
    // -----------------------------------------------------------------------

    private static CssPolygonShape? TryParsePolygon(IReadOnlyList<CssValue> args)
    {
        // CssValueParser.ParseFunction splits on top-level commas so each
        // argument is either:
        //   • a CssValueList([x, y]) for a vertex pair like "50% 0%", or
        //   • a CssKeyword("nonzero"/"evenodd") for the fill-rule arg, or
        //   • a flat CssValue (e.g. when there is no whitespace between values).
        // We process argument groups one by one.
        if (args.Count == 0)
        {
            return null;
        }

        var fillRule = CssFillRule.Nonzero;
        var vertices = new List<CssPolygonVertex>();
        var skipFirst = false;

        // Check the first argument for a fill-rule keyword.
        var firstArg = args[0];
        if (firstArg is CssKeyword { Name: "nonzero" } or CssKeyword { Name: "evenodd" })
        {
            fillRule = ((CssKeyword)firstArg).Name == "evenodd"
                ? CssFillRule.EvenOdd
                : CssFillRule.Nonzero;
            skipFirst = true;
        }
        else if (firstArg is CssValueList fvl
            && fvl.Values.Count == 1
            && fvl.Values[0] is CssKeyword { Name: "nonzero" or "evenodd" } frKw)
        {
            fillRule = frKw.Name == "evenodd" ? CssFillRule.EvenOdd : CssFillRule.Nonzero;
            skipFirst = true;
        }

        for (var i = skipFirst ? 1 : 0; i < args.Count; i++)
        {
            var group = args[i];
            // Each group should be a CssValueList([x, y]) or pair of values.
            var groupValues = group is CssValueList vl ? vl.Values : (IReadOnlyList<CssValue>)[group];
            var flat = groupValues.Where(v => v is not CssKeyword { Name: "" }).ToList();
            if (flat.Count >= 2
                && TryParseLengthPercentage(flat[0], out var vx)
                && TryParseLengthPercentage(flat[1], out var vy))
            {
                vertices.Add(new CssPolygonVertex(vx, vy));
            }
        }

        if (vertices.Count == 0)
        {
            return null;
        }

        return new CssPolygonShape(fillRule, vertices);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static bool TryParseShapeRadius(
        CssValue v,
        out CssLengthPercentage? lengthPct,
        out string? keyword)
    {
        keyword = null;
        lengthPct = null;

        if (v is CssKeyword { Name: "closest-side" or "farthest-side" } kw)
        {
            keyword = kw.Name;
            return true;
        }

        if (TryParseLengthPercentage(v, out var lp))
        {
            lengthPct = lp;
            return true;
        }

        return false;
    }

    internal static bool TryParseLengthPercentage(CssValue v, out CssLengthPercentage result)
    {
        switch (v)
        {
            case CssLength l:
                result = CssLengthPercentage.FromLength(l);
                return true;
            case CssPercentage p:
                result = CssLengthPercentage.FromPercentage(p.Value);
                return true;
            case CssNumber { Value: 0 }:
                result = CssLengthPercentage.FromLength(CssLength.Zero);
                return true;
            case CssCalc c:
                result = CssLengthPercentage.FromCalc(c);
                return true;
        }

        result = null!;
        return false;
    }

    private static CssShapePosition ParseShapePosition(List<CssValue> flat, ref int i)
    {
        // <position> := [ center | left | right | top | bottom | <lp> ]{1,2}
        // We read up to 2 positional components. We store them as X then Y;
        // keyword mapping is approximate (center→50%, left→0%, right→100%,
        // top→0%, bottom→100%).
        CssLengthPercentage? x = null, y = null;

        while (i < flat.Count && (x is null || y is null))
        {
            var v = flat[i];
            if (v is CssKeyword kw)
            {
                var mapped = MapPositionKeyword(kw.Name);
                if (mapped is null)
                {
                    break;
                }
                // Assign to axis implied by the keyword or left-to-right order.
                if (kw.Name is "left" or "right")
                {
                    x ??= mapped;
                }
                else if (kw.Name is "top" or "bottom")
                {
                    y ??= mapped;
                }
                else // center
                {
                    if (x is null)
                    {
                        x = mapped;
                    }
                    else
                    {
                        y = mapped;
                    }
                }
                i++;
            }
            else if (TryParseLengthPercentage(v, out var lp))
            {
                if (x is null)
                {
                    x = lp;
                }
                else
                {
                    y = lp;
                }

                i++;
            }
            else
            {
                break;
            }
        }

        var cx = CssLengthPercentage.FromPercentage(50);
        return new CssShapePosition(x ?? cx, y ?? cx);
    }

    private static CssLengthPercentage? MapPositionKeyword(string name)
        => name switch
        {
            "center" => CssLengthPercentage.FromPercentage(50),
            "left" => CssLengthPercentage.FromPercentage(0),
            "right" => CssLengthPercentage.FromPercentage(100),
            "top" => CssLengthPercentage.FromPercentage(0),
            "bottom" => CssLengthPercentage.FromPercentage(100),
            _ => null,
        };

    /// <summary>
    /// Parse <c>&lt;border-radius&gt;</c> as up to 4 radius values after the
    /// <c>round</c> keyword in an <c>inset()</c>. The CSS border-radius grammar
    /// allows a slash to separate horizontal and vertical radii per corner; we
    /// accept both forms but normalize to a list of <see cref="CssRadiusPair"/>
    /// items (one per corner, top-left → top-right → bottom-right → bottom-left).
    /// </summary>
    private static IReadOnlyList<CssRadiusPair> ParseBorderRadius(List<CssValue> flat, ref int i)
    {
        // Collect all remaining length-percentage values (up to 8: 4 horizontal +
        // optional slash + 4 vertical). A CssKeyword "/" signals the horizontal/
        // vertical split.
        var hVals = new List<CssLengthPercentage>(4);
        var vVals = new List<CssLengthPercentage>(4);
        var sawSlash = false;

        while (i < flat.Count)
        {
            if (flat[i] is CssKeyword { Name: "/" }) { sawSlash = true; i++; continue; }
            if (TryParseLengthPercentage(flat[i], out var lp))
            {
                if (!sawSlash)
                {
                    hVals.Add(lp);
                }
                else
                {
                    vVals.Add(lp);
                }

                i++;
            }
            else
            {
                break;
            }
        }

        if (hVals.Count == 0)
        {
            return [];
        }

        // Expand box model shorthand (same 1/2/3/4 pattern as padding).
        var h = ExpandBox(hVals);
        var v = sawSlash && vVals.Count > 0 ? ExpandBox(vVals) : h;

        return
        [
            new CssRadiusPair(h[0], v[0]),
            new CssRadiusPair(h[1], v[1]),
            new CssRadiusPair(h[2], v[2]),
            new CssRadiusPair(h[3], v[3]),
        ];

        static CssLengthPercentage[] ExpandBox(List<CssLengthPercentage> vals) => vals.Count switch
        {
            1 => [vals[0], vals[0], vals[0], vals[0]],
            2 => [vals[0], vals[1], vals[0], vals[1]],
            3 => [vals[0], vals[1], vals[2], vals[1]],
            _ => [vals[0], vals[1], vals[2], vals[3]],
        };
    }
}
