// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using Starling.Css.Values;
using System.Numerics;
using System.Xml;
using System.Xml.Linq;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Starling.Common.Image;

namespace Starling.Paint.Svg;

/// <summary>
/// A pure-managed first-cut SVG rasterizer. Parses an SVG document and renders
/// the shapes real-world icons use into a backend-neutral
/// <see cref="DecodedImage"/> (straight RGBA8888, top-down, tightly packed),
/// matching the contract the OS-native raster decoders produce — so the engine
/// can treat a decoded SVG exactly like a decoded PNG.
/// </summary>
/// <remarks>
/// <para>
/// Rasterization runs on ImageSharp.Drawing 3 (already the engine's paint
/// backend), keeping this firmly inside the managed-first interop policy: no
/// native code, no new dependency. It lives in <c>Starling.Paint</c> — the only
/// engine project that references ImageSharp.Drawing — rather than in the native
/// interop seam <c>Starling.Codecs</c>, which references only
/// <c>Starling.Common</c> and must not take a draw dependency.
/// </para>
/// <para>
/// Supported: <c>&lt;svg&gt;</c> (width/height/viewBox), <c>&lt;g&gt;</c>,
/// <c>&lt;path&gt;</c>, <c>&lt;rect&gt;</c> (incl. distinct rx/ry),
/// <c>&lt;circle&gt;</c>, <c>&lt;ellipse&gt;</c>, <c>&lt;line&gt;</c>,
/// <c>&lt;polyline&gt;</c>, <c>&lt;polygon&gt;</c>, <c>&lt;use&gt;</c>,
/// <c>&lt;symbol&gt;</c>; fill/stroke/stroke-width/fill-rule/opacity via
/// presentation attributes, <c>style="…"</c>, and flat <c>&lt;style&gt;</c>
/// class rules; <c>transform</c>; hex/rgb()/hsl()/named/currentColor colors;
/// <c>linearGradient</c>/<c>radialGradient</c> paint servers;
/// stroke-dasharray/dashoffset/linecap/linejoin/miterlimit; group opacity
/// compositing. Unknown elements and attributes are ignored gracefully.
/// </para>
/// <para>
/// Deferred (out of scope): <c>&lt;text&gt;</c>, <c>&lt;filter&gt;</c>,
/// <c>&lt;image&gt;</c>, markers, <c>&lt;clipPath&gt;</c>,
/// <c>patternUnits="objectBoundingBox"</c>.
/// </para>
/// </remarks>
public static class SvgImageDecoder
{
    /// <summary>Default intrinsic size when neither width/height nor viewBox is present (CSS replaced-element default).</summary>
    private const int DefaultDimension = 150;

    /// <summary>Clamp the rasterized canvas so a hostile/huge viewBox can't OOM.</summary>
    private const int MaxDimension = 4096;

    /// <summary>
    /// Decode SVG <paramref name="utf8"/> bytes into a <see cref="DecodedImage"/>.
    /// <paramref name="currentColor"/> resolves <c>currentColor</c> references
    /// (defaults to opaque black). Throws <see cref="SvgDecodeException"/> on
    /// malformed XML or a document with no rasterizable root.
    /// </summary>
    public static DecodedImage Decode(ReadOnlySpan<byte> utf8, CssColor? currentColor = null)
        => DecodeText(DecodeText(utf8), currentColor);

    /// <summary>Decode from an already-decoded SVG source string.</summary>
    public static DecodedImage DecodeText(string svg, CssColor? currentColor = null)
    {
        var root = ParseRoot(svg);

        // --- intrinsic size + viewBox → user-space transform -----------------
        ViewBox? viewBox = ParseViewBox(Attr(root, "viewBox"));
        float? widthAttr = ParseLength(Attr(root, "width"));
        float? heightAttr = ParseLength(Attr(root, "height"));

        float intrinsicW = widthAttr ?? viewBox?.Width ?? DefaultDimension;
        float intrinsicH = heightAttr ?? viewBox?.Height ?? DefaultDimension;

        int pxW = Math.Clamp((int)MathF.Ceiling(intrinsicW <= 0 ? DefaultDimension : intrinsicW), 1, MaxDimension);
        int pxH = Math.Clamp((int)MathF.Ceiling(intrinsicH <= 0 ? DefaultDimension : intrinsicH), 1, MaxDimension);

        // Map the viewBox into the viewport. preserveAspectRatio defaults to
        // "xMidYMid meet": a uniform scale that fits, then centering.
        Matrix3x2 viewportTransform = Matrix3x2.Identity;
        if (viewBox is { } vb && vb.Width > 0 && vb.Height > 0)
        {
            float s = MathF.Min(pxW / vb.Width, pxH / vb.Height);
            float tx = -vb.MinX * s + (pxW - vb.Width * s) / 2f;
            float ty = -vb.MinY * s + (pxH - vb.Height * s) / 2f;
            viewportTransform = new Matrix3x2(s, 0, 0, s, tx, ty);
        }

        // --- collect <style> class rules -------------------------------------
        var sheet = new SvgStyleSheet();
        CollectStyles(root, sheet);

        // --- collect paint servers (<pattern>, <linearGradient>,
        // <radialGradient>) by id, and all elements with an id so <use>
        // can find them; the viewport size resolves percentage geometry --------
        var (paintServers, elementsById) = CollectPaintServers(root);
        var viewport = new Vector2(
            viewBox?.Width ?? intrinsicW,
            viewBox?.Height ?? intrinsicH);
        var renderCtx = new SvgRenderContext(sheet, paintServers, elementsById, viewport)
        {
            DeviceWidth = pxW,
            DeviceHeight = pxH,
        };

        var rootStyle = new SvgStyle { CurrentColor = ToImageSharpColor(currentColor) };

        // --- render via the ImageSharp.Drawing canvas ------------------------
        // The DrawingCanvas Save(options) REPLACES the current transform rather
        // than composing it, so we compose transforms ourselves and always pass
        // the full accumulated matrix down the tree (mirroring how
        // ImageSharpBackend threads its own transform stack).
        using var image = new Image<Rgba32>(pxW, pxH, new Rgba32(0, 0, 0, 0));
        image.Mutate(imageCtx => imageCtx.Paint(canvas =>
        {
            // Render the <svg> root as an element (not just its children) so its
            // own presentation attributes — `fill="none"`, `stroke`,
            // `stroke-width`, … — apply and inherit down the tree. Many icons
            // (e.g. stroke-only line icons with fill="none" stroke="currentColor"
            // on the root) otherwise fall back to the default black fill and
            // paint as a solid blob.
            RenderElement(canvas, root, rootStyle, viewportTransform, renderCtx);
        }));

        // Pattern tiles and gradient offscreen layers were sampled lazily
        // during the Mutate above; now that rasterization is complete they
        // can be released.
        foreach (var disposable in renderCtx.Pending)
        {
            disposable.Dispose();
        }

        var buffer = new byte[checked(pxW * pxH * 4)];
        image.CopyPixelDataTo(buffer);
        return DecodedImage.FromBuffer(pxW, pxH, buffer);
    }

    /// <summary>
    /// Resolve the neutral <see cref="CssColor"/> the engine supplies for the SVG
    /// <c>currentColor</c> keyword into the adapter's ImageSharp colour. Keeping
    /// this conversion inside the decoder means the public Decode API carries no
    /// SixLabors type — the engine never constructs an ImageSharp colour.
    /// </summary>
    private static Color ToImageSharpColor(CssColor? color)
    {
        if (color is not { } c)
        {
            return Color.Black;
        }

        var srgb = c.ToSrgb();
        return Color.FromPixel(new Rgba32(srgb.R, srgb.G, srgb.B, srgb.A));
    }

    private static XElement ParseRoot(string svg)
    {
        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                // Parse the internal DTD subset so in-document <!ENTITY> definitions
                // expand (SVGs use them for repeated markup/values). XmlResolver=null
                // still blocks fetching any external DTD/entity, and the entity
                // character cap guards against billion-laughs expansion bombs.
                DtdProcessing = DtdProcessing.Parse,
                XmlResolver = null,
                MaxCharactersFromEntities = 10_000_000,
                CheckCharacters = false,
            };
            using var reader = XmlReader.Create(new System.IO.StringReader(svg), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new SvgDecodeException($"SVG is not well-formed XML: {ex.Message}", ex);
        }

        var root = doc.Root;
        if (root is null || !root.Name.LocalName.Equals("svg", StringComparison.OrdinalIgnoreCase))
        {
            throw new SvgDecodeException("SVG document has no <svg> root element.");
        }

        return root;
    }

    private static void RenderChildren(
        DrawingCanvas canvas, XElement parent, SvgStyle parentStyle, Matrix3x2 transform, SvgRenderContext ctx)
    {
        foreach (var el in parent.Elements())
        {
            RenderElement(canvas, el, parentStyle, transform, ctx);
        }
    }

    private static void RenderElement(
        DrawingCanvas canvas, XElement el, SvgStyle parentStyle, Matrix3x2 parentTransform, SvgRenderContext ctx)
    {
        string name = el.Name.LocalName;

        // Skip non-rendered containers (but their <style> was already harvested).
        // <pattern> / <linearGradient> / <radialGradient> are paint servers:
        // their content is resolved only when a shape references them via
        // fill="url(#id)", never inline. <symbol> is rendered via <use>.
        if (name is "defs" or "style" or "title" or "desc" or "metadata" or "symbol"
            or "clipPath" or "mask" or "pattern" or "linearGradient" or "radialGradient" or "filter" or "marker")
        {
            return;
        }

        // Resolve cascaded style: clone parent, apply class rules, then
        // presentation attributes, then style="…" (highest priority).
        var style = parentStyle.Clone();
        if (!ctx.Sheet.IsEmpty)
        {
            ctx.Sheet.Apply(style, name, Attr(el, "class"));
        }

        ApplyPresentationAttributes(style, el);
        style.ApplyStyleString(Attr(el, "style"));

        // display:none removes the element and its whole subtree.
        if (style.DisplayNone)
        {
            return;
        }

        // Compose this element's local transform onto the inherited one. SVG
        // applies the local transform first to a point, so for row-vector
        // matrices (point * M) the composition is local * parent. transform-origin
        // (when present) pivots that local transform about the given point.
        var localTransform = ApplyTransformOrigin(SvgTransform.Parse(Attr(el, "transform")), el);
        var transform = localTransform * parentTransform;

        // clip-path="url(#id)": restrict this element and its subtree to the
        // referenced <clipPath> geometry (intersected with any enclosing clip).
        var savedClip = ctx.ClipDevice;
        var savedClipEvenOdd = ctx.ClipEvenOdd;
        PushClipPath(el, transform, ctx);
        try
        {
            // filter="url(#id)" / mask="url(#id)": render the element to an offscreen
            // layer, apply the effect, then composite. Skipped (rendered normally)
            // when the effect is unsupported or already mid-application.
            if (!ctx.ActiveEffects.Contains(el)
                && TryRenderWithEffects(canvas, el, style, transform, ctx))
            {
                return;
            }

            switch (name)
            {
                case "switch":
                    // Render only the first child whose conditional-processing
                    // attributes are all satisfied (SVG 1.1 §5.8).
                    foreach (var child in el.Elements())
                    {
                        if (SwitchChildMatches(child))
                        {
                            RenderElement(canvas, child, style, transform, ctx);
                            break;
                        }
                    }
                    break;
                case "svg" when el.Parent is not null:
                    // A nested <svg> establishes its own viewport: it offsets content
                    // by (x,y), optionally maps a viewBox, and clips to width×height.
                    RenderNestedSvg(canvas, el, style, transform, ctx);
                    break;
                case "g":
                case "svg":
                case "a":
                    // Group opacity compositing: a <g opacity=…> where opacity < 1
                    // must composite children into an offscreen layer first, then
                    // blend at the group opacity. Otherwise overlapping children
                    // double-blend through the inherited per-child Opacity.
                    // SVG 1.1 §14.6 / Compositing and Blending §7.
                    if (style.GroupOpacity < 1f - float.Epsilon && name == "g")
                    {
                        RenderGroupWithOpacity(canvas, el, style, transform, ctx);
                    }
                    else
                    {
                        RenderChildren(canvas, el, style, transform, ctx);
                    }

                    break;
                case "use":
                    RenderUse(canvas, el, style, transform, ctx);
                    break;
                case "text":
                    RenderText(canvas, el, style, transform, ctx);
                    break;
                case "path":
                    {
                        var p = SvgPathParser.Parse(Attr(el, "d"));
                        DrawShape(canvas, p, style, transform, ctx);
                        RenderMarkers(canvas, p, style, transform, ctx);
                        break;
                    }
                case "rect":
                    DrawShape(canvas, BuildRect(el, ctx.Viewport), style, transform, ctx);
                    break;
                case "circle":
                    DrawShape(canvas, BuildCircle(el, ctx.Viewport), style, transform, ctx);
                    break;
                case "ellipse":
                    DrawShape(canvas, BuildEllipse(el, ctx.Viewport), style, transform, ctx);
                    break;
                case "line":
                    {
                        var p = BuildLine(el, ctx.Viewport);
                        DrawShape(canvas, p, style, transform, ctx, strokeOnly: true);
                        RenderMarkers(canvas, p, style, transform, ctx);
                        break;
                    }
                case "polyline":
                    {
                        var p = BuildPoly(el, close: false);
                        DrawShape(canvas, p, style, transform, ctx);
                        RenderMarkers(canvas, p, style, transform, ctx);
                        break;
                    }
                case "polygon":
                    {
                        var p = BuildPoly(el, close: true);
                        DrawShape(canvas, p, style, transform, ctx);
                        RenderMarkers(canvas, p, style, transform, ctx);
                        break;
                    }
                default:
                    // Unknown element: ignore but descend (some wrappers, e.g.
                    // <switch>, hold renderable children).
                    RenderChildren(canvas, el, style, transform, ctx);
                    break;
            }
        }
        finally
        {
            ctx.ClipDevice = savedClip;
            ctx.ClipEvenOdd = savedClipEvenOdd;
        }
    }

    /// <summary>
    /// Render a <c>&lt;g opacity="…"&gt;</c> group into an offscreen layer,
    /// then blend the layer onto the canvas at the group opacity.
    /// SVG 1.1 §14.6 — group opacity forms an isolated compositing group.
    /// Uses <c>DrawingCanvas.SaveLayer</c> to open an isolated blend
    /// group and <c>DrawingCanvas.Restore</c> to composite it back.
    /// </summary>
    private static void RenderGroupWithOpacity(
        DrawingCanvas canvas, XElement el, SvgStyle style, Matrix3x2 transform, SvgRenderContext ctx)
    {
        float alpha = Math.Clamp(style.GroupOpacity, 0f, 1f);

        // Open an isolated compositing group; SaveLayer composites it at
        // BlendPercentage when Restore is called.
        canvas.SaveLayer(new SixLabors.ImageSharp.GraphicsOptions
        {
            BlendPercentage = alpha,
        });

        // Reset BOTH Opacity and GroupOpacity so children render fully opaque
        // inside the layer; the group opacity is applied at layer-flatten time
        // by Restore(), not by each child's own alpha multiplication.
        var childStyle = style.Clone();
        childStyle.Opacity = 1f;
        childStyle.GroupOpacity = 1f;

        RenderChildren(canvas, el, childStyle, transform, ctx);

        canvas.Restore();
    }

    /// <summary>
    /// Resolve a <c>&lt;use href="#id" x="…" y="…"/&gt;</c> reference and
    /// render the referenced element at the (x, y) offset. Handles both
    /// regular element references and <c>&lt;symbol&gt;</c> (renders its
    /// children). SVG 1.1 §5.6.
    /// </summary>
    private static void RenderUse(
        DrawingCanvas canvas, XElement useEl, SvgStyle style, Matrix3x2 transform, SvgRenderContext ctx)
    {
        // Guard against recursive <use> chains (self/indirect) overflowing the stack.
        if (ctx.RefDepth >= MaxRefDepth)
        {
            return;
        }

        // xlink:href and plain href are both valid in SVG 1.1/2.
        var href = Attr(useEl, "href") ?? Attr(useEl, "xlink:href");
        if (href is null || !href.StartsWith('#'))
        {
            return;
        }

        var refId = href[1..].Trim();
        if (refId.Length == 0)
        {
            return;
        }

        if (!ctx.ElementsById.TryGetValue(refId, out var refEl))
        {
            return;
        }

        float x = ParseLengthPct(Attr(useEl, "x"), ctx.Viewport.X) ?? 0;
        float y = ParseLengthPct(Attr(useEl, "y"), ctx.Viewport.Y) ?? 0;

        // Apply the (x,y) offset as an additional translation.
        var useTransform = Matrix3x2.CreateTranslation(x, y) * transform;

        // Cycle guard: skip if this target is already being expanded on the
        // current chain (also bounds branching self-references).
        if (!ctx.ActiveRefs.Add(refEl))
        {
            return;
        }

        ctx.RefDepth++;
        try
        {
            string refName = refEl.Name.LocalName;
            if (refName.Equals("symbol", StringComparison.OrdinalIgnoreCase))
            {
                // A <symbol> provides its own isolated viewport; render its children.
                RenderChildren(canvas, refEl, style, useTransform, ctx);
            }
            else
            {
                RenderElement(canvas, refEl, style, useTransform, ctx);
            }
        }
        finally
        {
            ctx.RefDepth--;
            ctx.ActiveRefs.Remove(refEl);
        }
    }

    /// <summary>
    /// A <c>&lt;switch&gt;</c> child is selected when all of its conditional
    /// processing attributes pass. We satisfy no SVG extensions, so a non-empty
    /// <c>requiredExtensions</c> fails; <c>systemLanguage</c> must list English
    /// (our assumed UA language); <c>requiredFeatures</c> is treated as available.
    /// </summary>
    private static bool SwitchChildMatches(XElement child)
    {
        var ext = Attr(child, "requiredExtensions");
        if (!string.IsNullOrWhiteSpace(ext))
        {
            return false;
        }

        var lang = Attr(child, "systemLanguage");
        if (lang is not null)
        {
            bool en = false;
            foreach (var tag in lang.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // Primary subtag match: "en", "en-US", … all satisfy "en".
                int dash = tag.IndexOf('-');
                var primary = dash < 0 ? tag : tag[..dash];
                if (primary.Equals("en", StringComparison.OrdinalIgnoreCase)) { en = true; break; }
            }
            if (!en)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Pivot a local transform about its <c>transform-origin</c>:
    /// <c>T(-o) · M · T(o)</c> so the element's own transform rotates/scales
    /// around the given point rather than the user-space origin. Only length
    /// origins (numbers / <c>px</c>) are honoured; keywords are ignored.
    /// </summary>
    private static Matrix3x2 ApplyTransformOrigin(Matrix3x2 local, XElement el)
    {
        var raw = ReadInlineProperty(el, "transform-origin");
        if (raw is null)
        {
            return local;
        }

        var parts = raw.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return local;
        }

        float? ox = ParseLength(parts[0]);
        float? oy = parts.Length > 1 ? ParseLength(parts[1]) : 0f;
        if (ox is null || oy is null)
        {
            return local;
        }

        return Matrix3x2.CreateTranslation(-ox.Value, -oy.Value)
             * local
             * Matrix3x2.CreateTranslation(ox.Value, oy.Value);
    }

    /// <summary>
    /// Read a CSS property from an element's inline <c>style="…"</c>, falling
    /// back to a presentation attribute of the same name. Returns null if absent.
    /// </summary>
    private static string? ReadInlineProperty(XElement el, string property)
    {
        var styleStr = Attr(el, "style");
        if (styleStr is not null)
        {
            foreach (var decl in styleStr.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int colon = decl.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                if (decl.AsSpan(0, colon).Trim().Equals(property, StringComparison.OrdinalIgnoreCase))
                {
                    return decl[(colon + 1)..].Trim();
                }
            }
        }
        return Attr(el, property);
    }

    /// <summary>Parse a <c>url(#id)</c> reference into its fragment id, or null.</summary>
    private static string? ParseUrlRef(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var v = value.TrimStart();
        if (!v.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        int close = v.IndexOf(')');
        if (close < 0)
        {
            return null;
        }

        var inner = v[4..close].Trim().Trim('"', '\'').Trim();
        if (!inner.StartsWith('#'))
        {
            return null;
        }

        inner = inner[1..].Trim();
        return inner.Length == 0 ? null : inner;
    }

    /// <summary>Resolve a shape/path element to its geometry in local user space.</summary>
    private static IPath? GetShapePath(XElement el, SvgRenderContext ctx) => el.Name.LocalName switch
    {
        "rect" => BuildRect(el, ctx.Viewport),
        "circle" => BuildCircle(el, ctx.Viewport),
        "ellipse" => BuildEllipse(el, ctx.Viewport),
        "path" => SvgPathParser.Parse(Attr(el, "d")),
        "polygon" => BuildPoly(el, close: true),
        "polyline" => BuildPoly(el, close: false),
        "line" => BuildLine(el, ctx.Viewport),
        _ => null,
    };

    /// <summary>
    /// Combine a <c>&lt;clipPath&gt;</c>'s child shapes into one geometry in the
    /// clip's local user space (clipPathUnits=userSpaceOnUse). <paramref name="evenOdd"/>
    /// reports whether any child requested <c>clip-rule:evenodd</c>.
    /// </summary>
    private static IPath? BuildClipGeometry(XElement clipEl, SvgRenderContext ctx, out bool evenOdd)
    {
        evenOdd = false;
        var paths = new List<IPath>();
        foreach (var child in clipEl.Elements())
        {
            var p = GetShapePath(child, ctx);
            if (p is null)
            {
                continue;
            }

            var childT = SvgTransform.Parse(Attr(child, "transform"));
            if (childT != Matrix3x2.Identity)
            {
                p = p.Transform(To4x4(childT));
            }

            var cr = ReadInlineProperty(child, "clip-rule");
            if (cr is not null && cr.Equals("evenodd", StringComparison.OrdinalIgnoreCase))
            {
                evenOdd = true;
            }

            paths.Add(p);
        }
        return paths.Count switch
        {
            0 => null,
            1 => paths[0],
            _ => new ComplexPolygon(paths),
        };
    }

    /// <summary>
    /// Resolve an element's <c>clip-path="url(#id)"</c> and set
    /// <see cref="SvgRenderContext.ClipDevice"/> to the clip region in device
    /// pixels, intersected with any enclosing clip (CSS Masking §6 / SVG 1.1
    /// §14.3). The caller restores the previous value when the subtree ends.
    /// </summary>
    private static void PushClipPath(XElement el, Matrix3x2 transform, SvgRenderContext ctx)
    {
        var id = ParseUrlRef(ReadInlineProperty(el, "clip-path"));
        if (id is null
            || !ctx.ElementsById.TryGetValue(id, out var clipEl)
            || !clipEl.Name.LocalName.Equals("clipPath", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var geom = BuildClipGeometry(clipEl, ctx, out bool evenOdd);
        if (geom is null)
        {
            return;
        }

        IPath device = geom.Transform(To4x4(transform));

        // Nested clip: intersect with the enclosing region.
        if (ctx.ClipDevice is { } outer)
        {
            device = device.Clip(new ShapeOptions { BooleanOperation = BooleanOperation.Intersection }, outer);
        }

        ctx.ClipDevice = device;
        ctx.ClipEvenOdd = evenOdd;
    }

    /// <summary>
    /// <c>canvas.Save</c> that also applies the active <see cref="SvgRenderContext.ClipDevice"/>
    /// region (an Intersection clip) when one is set, so every draw stays inside
    /// the current <c>clip-path</c>.
    /// </summary>
    private static void SaveClipped(DrawingCanvas canvas, DrawingOptions options, SvgStyle style, SvgRenderContext ctx)
    {
        // mix-blend-mode blends this element's paint with the backdrop.
        if (style.BlendMode != PixelColorBlendingMode.Normal)
        {
            options.GraphicsOptions.ColorBlendingMode = style.BlendMode;
        }

        if (ctx.ClipDevice is { } clip)
        {
            options.ShapeOptions.BooleanOperation = BooleanOperation.Intersection;
            if (ctx.ClipEvenOdd)
            {
                options.ShapeOptions.IntersectionRule = IntersectionRule.EvenOdd;
            }

            canvas.Save(options, clip);
        }
        else
        {
            canvas.Save(options);
        }
    }

    /// <summary>
    /// Render a nested <c>&lt;svg&gt;</c> viewport: offset content by its (x, y),
    /// map an optional <c>viewBox</c> (xMidYMid meet), and clip everything to its
    /// width×height rectangle (SVG 1.1 §7.9). The clip is restored by the caller.
    /// </summary>
    private static void RenderNestedSvg(
        DrawingCanvas canvas, XElement el, SvgStyle style, Matrix3x2 transform, SvgRenderContext ctx)
    {
        float x = ParseLengthPct(Attr(el, "x"), ctx.Viewport.X) ?? 0;
        float y = ParseLengthPct(Attr(el, "y"), ctx.Viewport.Y) ?? 0;
        float w = ParseLengthPct(Attr(el, "width"), ctx.Viewport.X) ?? ctx.Viewport.X;
        float h = ParseLengthPct(Attr(el, "height"), ctx.Viewport.Y) ?? ctx.Viewport.Y;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        // Clip to the viewport rectangle (device space), intersected with any
        // enclosing clip. The caller's finally restores ctx.ClipDevice.
        IPath device = new RectanglePolygon(x, y, w, h).Transform(To4x4(transform));
        if (ctx.ClipDevice is { } outer)
        {
            device = device.Clip(new ShapeOptions { BooleanOperation = BooleanOperation.Intersection }, outer);
        }

        ctx.ClipDevice = device;
        ctx.ClipEvenOdd = false;

        // Content transform: translate by (x, y), then map a viewBox if present.
        var inner = Matrix3x2.CreateTranslation(x, y) * transform;
        var vb = ParseViewBox(Attr(el, "viewBox"));
        if (vb is { } v && v.Width > 0 && v.Height > 0)
        {
            float s = MathF.Min(w / v.Width, h / v.Height);
            float tx = -v.MinX * s + (w - v.Width * s) / 2f;
            float ty = -v.MinY * s + (h - v.Height * s) / 2f;
            inner = new Matrix3x2(s, 0, 0, s, tx, ty) * inner;
        }

        RenderChildren(canvas, el, style, inner, ctx);
    }

    /// <summary>Device-space scale factor (x-axis length) of a transform.</summary>
    private static float DeviceScale(Matrix3x2 m)
    {
        float s = MathF.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
        return s <= 0 ? 1f : s;
    }

    /// <summary>
    /// If <paramref name="el"/> carries a supported <c>filter</c> and/or
    /// <c>mask</c>, render its raw content into an offscreen device-size layer,
    /// apply the effect(s), composite the result, and return true. Returns false
    /// (so the caller renders normally) when neither effect applies or the filter
    /// primitive is unsupported.
    /// </summary>
    private static bool TryRenderWithEffects(
        DrawingCanvas canvas, XElement el, SvgStyle style, Matrix3x2 transform, SvgRenderContext ctx)
    {
        XElement? filterEl = ResolveEffect(el, "filter", "filter", ctx);
        XElement? maskEl = ResolveEffect(el, "mask", "mask", ctx);
        // Recursion guard: skip an effect whose resource is already being applied
        // (a <mask>/<filter> that references itself through its own content).
        if (filterEl is not null && ctx.ActiveEffects.Contains(filterEl))
        {
            filterEl = null;
        }

        if (maskEl is not null && ctx.ActiveEffects.Contains(maskEl))
        {
            maskEl = null;
        }

        if (filterEl is null && maskEl is null)
        {
            return false;
        }

        if (ctx.DeviceWidth <= 0 || ctx.DeviceHeight <= 0)
        {
            return false;
        }

        // Render the element's raw content (effect suppressed) into a layer.
        var layer = new Image<Rgba32>(ctx.DeviceWidth, ctx.DeviceHeight, new Rgba32(0, 0, 0, 0));
        ctx.ActiveEffects.Add(el);
        try
        {
            layer.Mutate(c => c.Paint(lc => RenderElement(lc, el, style, transform, ctx)));
        }
        finally
        {
            ctx.ActiveEffects.Remove(el);
        }

        try
        {
            if (filterEl is not null)
            {
                ctx.ActiveEffects.Add(filterEl);
                Image<Rgba32>? filtered;
                try { filtered = ApplyFilter(layer, filterEl, el, transform, ctx); }
                finally { ctx.ActiveEffects.Remove(filterEl); }
                if (filtered is null)
                {
                    // Unsupported primitive — fall back to a normal (unfiltered) render.
                    layer.Dispose();
                    return false;
                }
                if (!ReferenceEquals(filtered, layer))
                {
                    layer.Dispose();
                    layer = filtered;
                }
            }

            if (maskEl is not null)
            {
                ctx.ActiveEffects.Add(maskEl);
                try { ApplyMask(layer, maskEl, style, transform, ctx); }
                finally { ctx.ActiveEffects.Remove(maskEl); }
            }
        }
        catch
        {
            // Any effect failure (e.g. a pathological filter value) falls back to
            // an unfiltered render rather than failing the whole decode.
            layer.Dispose();
            return false;
        }

        canvas.DrawImage(
            layer,
            new Rectangle(0, 0, ctx.DeviceWidth, ctx.DeviceHeight),
            new RectangleF(0, 0, ctx.DeviceWidth, ctx.DeviceHeight),
            KnownResamplers.NearestNeighbor);
        ctx.Pending.Add(layer);
        return true;
    }

    /// <summary>Resolve an <c>attr="url(#id)"</c> effect reference to its element of the expected name.</summary>
    private static XElement? ResolveEffect(XElement el, string attr, string expectedName, SvgRenderContext ctx)
    {
        var id = ParseUrlRef(ReadInlineProperty(el, attr));
        if (id is null || !ctx.ElementsById.TryGetValue(id, out var fe))
        {
            return null;
        }

        return fe.Name.LocalName.Equals(expectedName, StringComparison.OrdinalIgnoreCase) ? fe : null;
    }

    /// <summary>
    /// Apply the first supported primitive of a <c>&lt;filter&gt;</c> to a
    /// rendered layer. Supports a single <c>feGaussianBlur</c>, <c>feOffset</c>,
    /// or <c>feFlood</c>. Returns the result image (possibly the same instance
    /// blurred in place, possibly a new one), or null if no supported primitive
    /// is present so the caller renders the element unfiltered.
    /// </summary>
    private static Image<Rgba32>? ApplyFilter(
        Image<Rgba32> layer, XElement filterEl, XElement el, Matrix3x2 transform, SvgRenderContext ctx)
    {
        float scale = DeviceScale(transform);
        foreach (var prim in filterEl.Elements())
        {
            switch (prim.Name.LocalName)
            {
                case "feGaussianBlur":
                    {
                        var sd = (Attr(prim, "stdDeviation") ?? "0").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
                        float sigma = sd.Length > 0 && float.TryParse(sd[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
                        // Clamp to the layer extent: a blur wider than the image adds
                        // nothing visible and a huge kernel can throw or stall.
                        sigma = Math.Clamp(sigma * scale, 0f, Math.Max(layer.Width, layer.Height));
                        if (sigma > 0.01f)
                        {
                            try { layer.Mutate(x => x.GaussianBlur(sigma)); }
                            catch { /* degrade to the unblurred layer */ }
                        }
                        return layer;
                    }
                case "feOffset":
                    {
                        float dx = (ParseLength(Attr(prim, "dx")) ?? 0) * scale;
                        float dy = (ParseLength(Attr(prim, "dy")) ?? 0) * scale;
                        var shifted = new Image<Rgba32>(layer.Width, layer.Height, new Rgba32(0, 0, 0, 0));
                        shifted.Mutate(x => x.DrawImage(layer, new Point((int)MathF.Round(dx), (int)MathF.Round(dy)), 1f));
                        return shifted;
                    }
                case "feFlood":
                    {
                        var color = SvgColor.TryParse(Attr(prim, "flood-color") ?? "black", Color.Black, out var fc, out var none) && !none
                            ? fc : Color.Black;
                        if (float.TryParse(Attr(prim, "flood-opacity"), NumberStyles.Float, CultureInfo.InvariantCulture, out var fo))
                        {
                            color = ApplyAlpha(color, Math.Clamp(fo, 0f, 1f)) ?? color;
                        }

                        var region = FilterRegion(el, transform, ctx);
                        var flood = new Image<Rgba32>(layer.Width, layer.Height, new Rgba32(0, 0, 0, 0));
                        flood.Mutate(x => x.Paint(fc => fc.Fill(Brushes.Solid(color), new RectanglePolygon(region))));
                        return flood;
                    }
            }
        }
        return null;
    }

    /// <summary>Device-space rectangle covering an element's geometry (its filter region).</summary>
    private static RectangleF FilterRegion(XElement el, Matrix3x2 transform, SvgRenderContext ctx)
    {
        var p = GetShapePath(el, ctx);
        if (p is not null)
        {
            return p.Transform(To4x4(transform)).Bounds;
        }

        return new RectangleF(0, 0, ctx.DeviceWidth, ctx.DeviceHeight);
    }

    /// <summary>
    /// Apply a luminance <c>&lt;mask&gt;</c> to a rendered layer: render the mask
    /// content to its own layer, then multiply the element layer's alpha by the
    /// mask's luminance×alpha per pixel (SVG 1.1 §14.4).
    /// </summary>
    private static void ApplyMask(
        Image<Rgba32> layer, XElement maskEl, SvgStyle style, Matrix3x2 transform, SvgRenderContext ctx)
    {
        using var maskLayer = new Image<Rgba32>(layer.Width, layer.Height, new Rgba32(0, 0, 0, 0));
        var maskStyle = new SvgStyle { CurrentColor = style.CurrentColor };
        maskLayer.Mutate(c => c.Paint(mc =>
        {
            foreach (var child in maskEl.Elements())
            {
                RenderElement(mc, child, maskStyle, transform, ctx);
            }
        }));

        int w = layer.Width, h = layer.Height;
        var mk = new byte[w * h * 4];
        maskLayer.CopyPixelDataTo(mk);

        // Multiply the element layer's alpha by the mask's luminance×alpha, in place.
        layer.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < h; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (int x = 0; x < w; x++)
                {
                    int o = (y * w + x) * 4;
                    float lum = (0.2126f * mk[o] + 0.7152f * mk[o + 1] + 0.0722f * mk[o + 2]) / 255f;
                    float maskAlpha = lum * (mk[o + 3] / 255f);
                    ref var px = ref row[x];
                    px.A = (byte)Math.Clamp((int)MathF.Round(px.A * maskAlpha), 0, 255);
                }
            }
        });
    }

    private static readonly string[] FallbackFontFamilies =
        ["Arial", "Helvetica", "Liberation Sans", "DejaVu Sans", "Segoe UI"];

    /// <summary>Resolve a font for a text run: the element's font-family (first
    /// that the system has) at its font-size, else a common fallback.</summary>
    private static Font ResolveFont(XElement el, float size)
    {
        if (size <= 0)
        {
            size = 16f;
        }

        FontFamily family = default;
        bool found = false;
        var fam = ReadInlineProperty(el, "font-family");
        if (fam is not null)
        {
            foreach (var name in fam.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (SystemFonts.TryGet(name.Trim('\'', '"', ' '), out family)) { found = true; break; }
            }
        }
        if (!found)
        {
            foreach (var name in FallbackFontFamilies)
            {
                if (SystemFonts.TryGet(name, out family)) { found = true; break; }
            }
        }
        if (!found)
        {
            family = SystemFonts.Families.First();
        }

        return family.CreateFont(size, FontStyle.Regular);
    }

    /// <summary>
    /// Render a <c>&lt;text&gt;</c> element and its <c>&lt;tspan&gt;</c> runs.
    /// A minimal left-to-right layout: each run advances the pen by its measured
    /// width; <c>&lt;tspan&gt;</c> honours dx/dy, absolute x/y, fill and font-size.
    /// Best-effort — any failure leaves the text unpainted rather than throwing.
    /// </summary>
    private static void RenderText(
        DrawingCanvas canvas, XElement el, SvgStyle style, Matrix3x2 transform, SvgRenderContext ctx)
    {
        try
        {
            float penX = ParseLengthPct(Attr(el, "x"), ctx.Viewport.X) ?? 0;
            float penY = ParseLengthPct(Attr(el, "y"), ctx.Viewport.Y) ?? 0;
            float size = ParseLength(ReadInlineProperty(el, "font-size")) ?? 16f;

            foreach (var node in el.Nodes())
            {
                if (node is XText t)
                {
                    DrawTextRun(canvas, t.Value, el, style, size, ref penX, penY, transform, ctx);
                }
                else if (node is XElement child
                         && child.Name.LocalName.Equals("tspan", StringComparison.OrdinalIgnoreCase))
                {
                    var ts = style.Clone();
                    ApplyPresentationAttributes(ts, child);
                    ts.ApplyStyleString(Attr(child, "style"));
                    if (ts.DisplayNone)
                    {
                        continue;
                    }

                    penX += ParseLength(Attr(child, "dx")) ?? 0;
                    float runY = penY + (ParseLength(Attr(child, "dy")) ?? 0);
                    if (ParseLengthPct(Attr(child, "x"), ctx.Viewport.X) is { } ax)
                    {
                        penX = ax;
                    }

                    if (ParseLengthPct(Attr(child, "y"), ctx.Viewport.Y) is { } ay)
                    {
                        runY = ay;
                    }

                    float tsize = ParseLength(ReadInlineProperty(child, "font-size")) ?? size;

                    foreach (var tn in child.Nodes())
                    {
                        if (tn is XText tt)
                        {
                            DrawTextRun(canvas, tt.Value, child, ts, tsize, ref penX, runY, transform, ctx);
                        }
                    }
                }
            }
        }
        catch
        {
            // Text is best-effort; never let a font/layout failure break the raster.
        }
    }

    private static void DrawTextRun(
        DrawingCanvas canvas, string text, XElement el, SvgStyle style, float size,
        ref float penX, float penY, Matrix3x2 transform, SvgRenderContext ctx)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        // Collapse XML whitespace as SVG text does for a first cut.
        text = System.Text.RegularExpressions.Regex.Replace(text, "\\s+", " ");
        if (text.Length == 0 || (text == " "))
        {
            return;
        }

        if (!style.Visible)
        {
            return;
        }

        var font = ResolveFont(el, size);
        var color = style.EffectiveFill() ?? Color.Black;
        var options = new TextOptions(font);

        // SVG y is the baseline; ImageSharp draws from the top, so lift by the ascent.
        float ascent = font.FontMetrics.HorizontalMetrics.Ascender
                       / (float)font.FontMetrics.UnitsPerEm * font.Size;
        float width = TextMeasurer.MeasureAdvance(text, options).Width;

        canvas.Save(new DrawingOptions { Transform = To4x4(transform) });
        canvas.DrawText(new TextBlock(text, options), new PointF(penX, penY - ascent), -1, Brushes.Solid(color), null);
        canvas.Restore();

        penX += width;
    }

    /// <summary>
    /// Render <c>marker-start/-mid/-end</c> glyphs at a shape's vertices (SVG 1.1
    /// §11.6). Vertices come from the flattened path: the first point gets the
    /// start marker, the last the end marker, interior points the mid marker.
    /// Best-effort and bounded so a long path can't explode the marker count.
    /// </summary>
    private static void RenderMarkers(
        DrawingCanvas canvas, IPath? path, SvgStyle style, Matrix3x2 transform, SvgRenderContext ctx)
    {
        if (path is null
            || (style.MarkerStart is null && style.MarkerMid is null && style.MarkerEnd is null))
        {
            return;
        }

        try
        {
            var pts = new List<PointF>();
            foreach (var sub in path.Flatten())
            {
                var span = sub.Points.Span;
                for (int i = 0; i < span.Length; i++)
                {
                    pts.Add(span[i]);
                }
            }
            if (pts.Count == 0)
            {
                return;
            }

            // Only place interior (mid) markers for modest vertex counts so a
            // densely-flattened curve can't render thousands of glyphs.
            bool doMid = style.MarkerMid is not null && pts.Count <= 200;
            for (int i = 0; i < pts.Count; i++)
            {
                string? id = i == 0 ? style.MarkerStart
                    : i == pts.Count - 1 ? style.MarkerEnd
                    : (doMid ? style.MarkerMid : null);
                if (id is not null)
                {
                    RenderOneMarker(canvas, id, pts[i], style, transform, ctx);
                }
            }
        }
        catch
        {
            // Markers are best-effort; never break the raster.
        }
    }

    private static void RenderOneMarker(
        DrawingCanvas canvas, string markerId, PointF point, SvgStyle style, Matrix3x2 transform, SvgRenderContext ctx)
    {
        if (!ctx.ElementsById.TryGetValue(markerId, out var marker)
            || !marker.Name.LocalName.Equals("marker", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!ctx.ActiveRefs.Add(marker))
        {
            return; // marker cycle
        }

        try
        {
            float refX = ParseLength(Attr(marker, "refX")) ?? 0;
            float refY = ParseLength(Attr(marker, "refY")) ?? 0;
            // markerUnits: strokeWidth (default) scales the marker by the stroke width.
            var units = Attr(marker, "markerUnits") ?? "strokeWidth";
            float scale = units.Equals("userSpaceOnUse", StringComparison.OrdinalIgnoreCase)
                ? 1f
                : (style.StrokeWidth > 0 ? style.StrokeWidth : 1f);

            // contentPoint -> anchor at refX/refY -> scale -> place at the vertex -> element transform.
            var mt = Matrix3x2.CreateTranslation(-refX, -refY)
                   * Matrix3x2.CreateScale(scale)
                   * Matrix3x2.CreateTranslation(point.X, point.Y)
                   * transform;

            var markerStyle = new SvgStyle { CurrentColor = style.CurrentColor };
            foreach (var child in marker.Elements())
            {
                RenderElement(canvas, child, markerStyle, mt, ctx);
            }
        }
        finally
        {
            ctx.ActiveRefs.Remove(marker);
        }
    }

    private static void DrawShape(
        DrawingCanvas canvas, IPath? path, SvgStyle style, Matrix3x2 transform, SvgRenderContext ctx, bool strokeOnly = false)
    {
        if (path is null)
        {
            return;
        }

        // visibility:hidden suppresses this element's own geometry.
        if (!style.Visible)
        {
            return;
        }

        bool hasFillRef = style.FillRef is not null;

        void PaintFill()
        {
            if (!strokeOnly && hasFillRef)
            {
                var refId = style.FillRef!;
                if (ctx.PaintServers.TryGetValue(refId, out var server))
                {
                    string serverName = server.Name.LocalName;
                    if (serverName.Equals("pattern", StringComparison.OrdinalIgnoreCase))
                    {
                        FillWithPattern(canvas, path, server, style, transform, ctx);
                    }
                    else if (serverName.Equals("linearGradient", StringComparison.OrdinalIgnoreCase)
                          || serverName.Equals("radialGradient", StringComparison.OrdinalIgnoreCase))
                    {
                        FillWithGradient(canvas, path, server, style, transform, ctx);
                    }
                    // else: unsupported server type — check for fallback below
                }
                else if (style.FillFallback is { } fallback)
                {
                    // url(#missing) fallback: paint the fallback color.
                    var fc = ApplyAlpha(fallback, style.FillOpacity * style.Opacity);
                    if (fc is not null)
                    {
                        SaveClipped(canvas, new DrawingOptions
                        {
                            Transform = To4x4(transform),
                            ShapeOptions = new ShapeOptions
                            {
                                IntersectionRule = style.FillEvenOdd ? IntersectionRule.EvenOdd : IntersectionRule.NonZero,
                            },
                        }, style, ctx);
                        canvas.Fill(Brushes.Solid(fc.Value), path);
                        canvas.Restore();
                    }
                }
                // no fallback → paint nothing (consistent with original behaviour)
            }

            // When a fill reference is present (resolved above or not), it replaces
            // the solid fill — an unresolved reference with no fallback paints nothing.
            var fill = strokeOnly || hasFillRef ? null : style.EffectiveFill();
            if (fill is { } fc2)
            {
                SaveClipped(canvas, new DrawingOptions
                {
                    Transform = To4x4(transform),
                    ShapeOptions = new ShapeOptions
                    {
                        IntersectionRule = style.FillEvenOdd ? IntersectionRule.EvenOdd : IntersectionRule.NonZero,
                    },
                }, style, ctx);
                canvas.Fill(Brushes.Solid(fc2), path);
                canvas.Restore();
            }
        }

        void PaintStroke()
        {
            // Stroke: respect stroke ref too (gradient/pattern strokes).
            if (style.StrokeRef is not null)
            {
                var refId = style.StrokeRef!;
                if (ctx.PaintServers.TryGetValue(refId, out var server) && style.StrokeWidth > 0)
                {
                    string serverName = server.Name.LocalName;
                    if (serverName.Equals("linearGradient", StringComparison.OrdinalIgnoreCase)
                     || serverName.Equals("radialGradient", StringComparison.OrdinalIgnoreCase))
                    {
                        StrokeWithGradient(canvas, path, server, style, transform, ctx);
                    }
                }
            }
            else
            {
                var stroke = style.EffectiveStroke();
                if (stroke is { } sc && style.StrokeWidth > 0)
                {
                    var pen = BuildPen(sc, style);
                    SaveClipped(canvas, new DrawingOptions { Transform = To4x4(transform) }, style, ctx);
                    canvas.Draw(pen, path);
                    canvas.Restore();
                }
            }
        }

        // paint-order: default is fill then stroke; 'stroke' reverses it.
        if (style.PaintOrderStrokeFirst)
        {
            PaintStroke();
            PaintFill();
        }
        else
        {
            PaintFill();
            PaintStroke();
        }
    }

    /// <summary>
    /// Build a dashed pen from a solid color and SVG stroke style properties.
    /// Returns a <see cref="PatternPen"/> when a dash array is set, otherwise
    /// a plain <see cref="SolidPen"/>.
    /// </summary>
    private static Pen BuildPen(Color color, SvgStyle style)
    {
        var strokeOpts = new StrokeOptions
        {
            LineCap = style.StrokeLineCap,
            LineJoin = style.StrokeLineJoin,
            MiterLimit = style.StrokeMiterLimit,
        };

        // stroke-dasharray: encode the dash/gap sequence via PatternPen.
        // SVG dash values are in user units; PenOptions.StrokePattern stores
        // unit-relative ratios (pattern / strokeWidth). SVG 1.1 §11.4.
        if (style.StrokeDashArray is { Length: > 0 } dashes)
        {
            float w = style.StrokeWidth;
            if (w <= 0)
            {
                w = 1f;
            }
            // PenOptions(Color, width, pattern[]) — pattern values are absolute pixel lengths.
            float[] pattern = new float[dashes.Length];
            for (int i = 0; i < dashes.Length; i++)
            {
                pattern[i] = Math.Max(0.001f, dashes[i]); // avoid zero-length segments
            }

            var opts = new PenOptions(color, w, pattern) { StrokeOptions = strokeOpts };
            return new PatternPen(opts);
        }

        return new SolidPen(new PenOptions(color, style.StrokeWidth) { StrokeOptions = strokeOpts });
    }

    /// <summary>
    /// Paint <paramref name="path"/> with an SVG <c>&lt;pattern&gt;</c> paint
    /// server: rasterize one pattern tile, then tile it across the shape with an
    /// <see cref="ImageBrush"/> (which repeats the source image and is clipped to
    /// the fill path). Only the common <c>patternUnits="userSpaceOnUse"</c> form
    /// with a numeric tile <c>width</c>/<c>height</c> is supported; anything else
    /// leaves the fill unpainted rather than guessing.
    /// </summary>
    private static void FillWithPattern(
        DrawingCanvas canvas, IPath path, XElement pattern, SvgStyle style, Matrix3x2 transform, SvgRenderContext ctx)
    {
        // Guard against recursive <pattern> references overflowing the stack.
        if (ctx.RefDepth >= MaxRefDepth)
        {
            return;
        }

        // patternUnits: userSpaceOnUse uses the value as-is; objectBoundingBox
        // (the default) treats width/height/x/y as fractions of the shape's
        // bounding box. patternContentUnits is left at its userSpaceOnUse default.
        var units = Attr(pattern, "patternUnits") ?? "objectBoundingBox";
        bool obb = units.Equals("objectBoundingBox", StringComparison.OrdinalIgnoreCase);

        RectangleF bbox = obb ? GetPathBounds(path) : default;
        float? tileW = ParseLength(Attr(pattern, "width"));
        float? tileH = ParseLength(Attr(pattern, "height"));
        if (tileW is not > 0 || tileH is not > 0)
        {
            return;
        }

        if (obb)
        {
            tileW *= bbox.Width;
            tileH *= bbox.Height;
        }

        // Device scale of this shape (x-axis length of its transform). The tile
        // is rasterized at that resolution so it stays crisp when the shape is
        // scaled by the viewport transform.
        float scale = MathF.Sqrt(transform.M11 * transform.M11 + transform.M12 * transform.M12);
        if (scale <= 0)
        {
            scale = 1f;
        }

        int tilePxW = Math.Clamp((int)MathF.Ceiling(tileW.Value * scale), 1, MaxDimension);
        int tilePxH = Math.Clamp((int)MathF.Ceiling(tileH.Value * scale), 1, MaxDimension);

        float patternX = ParseLength(Attr(pattern, "x")) ?? 0;
        float patternY = ParseLength(Attr(pattern, "y")) ?? 0;
        if (obb)
        {
            patternX = bbox.X + patternX * bbox.Width;
            patternY = bbox.Y + patternY * bbox.Height;
        }

        // Map pattern content (userSpaceOnUse) into tile pixels: shift the tile
        // origin to (0,0), then scale to device resolution.
        var tileTransform =
            Matrix3x2.CreateTranslation(-patternX, -patternY) * Matrix3x2.CreateScale(scale);
        var contentStyle = new SvgStyle { CurrentColor = style.CurrentColor };

        // The brush samples this tile when the outer canvas timeline executes
        // (after this method returns), so it must outlive the call — stage it for
        // disposal once DecodeText finishes rendering.
        var tile = new Image<Rgba32>(tilePxW, tilePxH, new Rgba32(0, 0, 0, 0));
        ctx.Pending.Add(tile);

        // Cycle guard: a pattern whose content references itself is skipped.
        if (!ctx.ActiveRefs.Add(pattern))
        {
            return;
        }

        ctx.RefDepth++;
        try
        {
            tile.Mutate(c => c.Paint(tileCanvas =>
            {
                foreach (var child in pattern.Elements())
                {
                    RenderElement(tileCanvas, child, contentStyle, tileTransform, ctx);
                }
            }));
        }
        finally
        {
            ctx.RefDepth--;
            ctx.ActiveRefs.Remove(pattern);
        }

        // Fill the shape (mapped to device space) with the repeating tile. The
        // path itself clips the brush to the shape; ImageBrush wraps the tile to
        // cover the region.
        var devicePath = path.Transform(To4x4(transform));
        SaveClipped(canvas, new DrawingOptions
        {
            ShapeOptions = new ShapeOptions
            {
                IntersectionRule = style.FillEvenOdd ? IntersectionRule.EvenOdd : IntersectionRule.NonZero,
            },
        }, style, ctx);
        canvas.Fill(new ImageBrush<Rgba32>(tile), devicePath);
        canvas.Restore();
    }

    /// <summary>
    /// Paint <paramref name="path"/> with an SVG gradient paint server
    /// (<c>&lt;linearGradient&gt;</c> or <c>&lt;radialGradient&gt;</c>).
    /// Supports <c>gradientUnits</c> (objectBoundingBox default +
    /// userSpaceOnUse), <c>gradientTransform</c>, <c>spreadMethod</c>
    /// (pad/reflect/repeat), stop color/offset/opacity, and
    /// <c>xlink:href</c>/<c>href</c> stop inheritance. SVG 1.1 §13.
    /// </summary>
    private static void FillWithGradient(
        DrawingCanvas canvas, IPath path, XElement gradEl, SvgStyle style, Matrix3x2 transform, SvgRenderContext ctx)
    {
        var brush = BuildGradientBrush(gradEl, path, transform, ctx, forFill: true);
        if (brush is null)
        {
            return;
        }

        var devicePath = path.Transform(To4x4(transform));
        SaveClipped(canvas, new DrawingOptions
        {
            ShapeOptions = new ShapeOptions
            {
                IntersectionRule = style.FillEvenOdd ? IntersectionRule.EvenOdd : IntersectionRule.NonZero,
            },
        }, style, ctx);
        canvas.Fill(brush, devicePath);
        canvas.Restore();
    }

    /// <summary>Stroke <paramref name="path"/> with a gradient paint server.</summary>
    private static void StrokeWithGradient(
        DrawingCanvas canvas, IPath path, XElement gradEl, SvgStyle style, Matrix3x2 transform, SvgRenderContext ctx)
    {
        var brush = BuildGradientBrush(gradEl, path, transform, ctx, forFill: false);
        if (brush is null)
        {
            return;
        }

        // PenOptions(Brush, width, pattern) is the only brush-accepting ctor;
        // pass an empty pattern array for a solid gradient stroke.
        var opts = new PenOptions(brush, style.StrokeWidth, [])
        {
            StrokeOptions = new StrokeOptions
            {
                LineCap = style.StrokeLineCap,
                LineJoin = style.StrokeLineJoin,
                MiterLimit = style.StrokeMiterLimit,
            },
        };
        var pen = new SolidPen(opts);
        SaveClipped(canvas, new DrawingOptions { Transform = To4x4(transform) }, style, ctx);
        canvas.Draw(pen, path);
        canvas.Restore();
    }

    /// <summary>
    /// Build an ImageSharp gradient brush from a <c>&lt;linearGradient&gt;</c>
    /// or <c>&lt;radialGradient&gt;</c> element.
    /// </summary>
    private static Brush? BuildGradientBrush(
        XElement gradEl, IPath path, Matrix3x2 transform, SvgRenderContext ctx, bool forFill)
    {
        // Resolve stop inheritance: xlink:href/href can point to another gradient
        // whose stops are inherited when this element defines none of its own.
        var stopsEl = ResolveGradientRef(gradEl, ctx);
        var stops = CollectGradientStops(stopsEl);
        if (stops.Length < 1)
        {
            return null;
        }

        if (stops.Length == 1)
        {
            // Degenerate gradient: treat as solid.
            stops = [stops[0], stops[0]];
        }

        var repetition = ParseSpreadMethod(GAttr(gradEl, "spreadMethod", ctx));

        // gradientUnits: objectBoundingBox (default) measures coordinates
        // as fractions of the element's bounding box; userSpaceOnUse
        // measures in the current user coordinate system.
        var units = GAttr(gradEl, "gradientUnits", ctx) ?? "objectBoundingBox";
        bool obb = units.Equals("objectBoundingBox", StringComparison.OrdinalIgnoreCase);

        // gradientTransform: an additional user-space transform for the gradient
        // coordinate system. We compose it onto the element transform.
        var gradTransform = SvgTransform.Parse(GAttr(gradEl, "gradientTransform", ctx));

        // Compute the element bounding box in user space (before element transform).
        RectangleF bbox = GetPathBounds(path);

        string localName = gradEl.Name.LocalName;

        if (localName.Equals("linearGradient", StringComparison.OrdinalIgnoreCase))
        {
            // SVG 1.1 §13.4.2 defaults: x1=0% y1=0% x2=100% y2=0%
            float x1 = ParseGradCoord(GAttr(gradEl, "x1", ctx), obb, bbox.X, bbox.Width) ?? (obb ? bbox.X : 0);
            float y1 = ParseGradCoord(GAttr(gradEl, "y1", ctx), obb, bbox.Y, bbox.Height) ?? (obb ? bbox.Y : 0);
            float x2 = ParseGradCoord(GAttr(gradEl, "x2", ctx), obb, bbox.X, bbox.Width) ?? (obb ? bbox.X + bbox.Width : bbox.Width);
            float y2 = ParseGradCoord(GAttr(gradEl, "y2", ctx), obb, bbox.Y, bbox.Height) ?? (obb ? bbox.Y : 0);

            // Map the gradient vector into device space. The caller fills a
            // device-space path (path.Transform(transform)) but ImageSharp brushes
            // use absolute coordinates, so the brush must share that space:
            // apply gradientTransform first (the gradient's own coordinate map),
            // then the element CTM. Without this, any element under a group
            // transform (e.g. an Inkscape `scale(0.1,-0.1)` export) draws the gradient
            // line far outside the shape and the whole fill clamps to one stop.
            var toDevice = gradTransform * transform;
            var p1 = Vector2.Transform(new Vector2(x1, y1), toDevice);
            var p2 = Vector2.Transform(new Vector2(x2, y2), toDevice);
            x1 = p1.X; y1 = p1.Y; x2 = p2.X; y2 = p2.Y;

            if (MathF.Abs(x2 - x1) < 0.001f && MathF.Abs(y2 - y1) < 0.001f)
            {
                // Degenerate (zero-length) gradient: use last stop color.
                return Brushes.Solid(stops[^1].Color);
            }

            return new LinearGradientBrush(new PointF(x1, y1), new PointF(x2, y2), repetition, stops);
        }

        if (localName.Equals("radialGradient", StringComparison.OrdinalIgnoreCase))
        {
            // SVG 1.1 §13.4.3 defaults: cx=50% cy=50% r=50% fx=cx fy=cy
            float cx = ParseGradCoord(GAttr(gradEl, "cx", ctx), obb, bbox.X, bbox.Width) ?? (obb ? bbox.X + bbox.Width * 0.5f : bbox.X + bbox.Width * 0.5f);
            float cy = ParseGradCoord(GAttr(gradEl, "cy", ctx), obb, bbox.Y, bbox.Height) ?? (obb ? bbox.Y + bbox.Height * 0.5f : bbox.Y + bbox.Height * 0.5f);
            float r = ParseGradCoordAbs(GAttr(gradEl, "r", ctx), obb, MathF.Sqrt(bbox.Width * bbox.Width + bbox.Height * bbox.Height) * 0.5f)
                      ?? (obb ? MathF.Min(bbox.Width, bbox.Height) * 0.5f : MathF.Min(bbox.Width, bbox.Height) * 0.5f);

            // Map the gradient center/radius into device space (gradientTransform
            // then the element CTM) so the brush shares the device-space path's
            // coordinate system — see the linearGradient note above.
            var toDevice = gradTransform * transform;
            var pc = Vector2.Transform(new Vector2(cx, cy), toDevice);
            cx = pc.X; cy = pc.Y;
            // Scale r by the combined transform's scale factor.
            float rs = MathF.Sqrt(toDevice.M11 * toDevice.M11 + toDevice.M12 * toDevice.M12);
            if (rs > 0)
            {
                r *= rs;
            }

            if (r <= 0)
            {
                return Brushes.Solid(stops[^1].Color);
            }

            return new RadialGradientBrush(new PointF(cx, cy), r, repetition, stops);
        }

        return null;
    }

    /// <summary>
    /// Resolve a gradient's <c>xlink:href</c>/<c>href</c> stop-inheritance
    /// chain: if the given element has no <c>&lt;stop&gt;</c> children, follow
    /// the reference to find stops. Cycles are broken after 10 hops.
    /// </summary>
    private static XElement ResolveGradientRef(XElement gradEl, SvgRenderContext ctx)
    {
        int guard = 0;
        var current = gradEl;
        while (guard++ < 10)
        {
            bool hasStops = current.Elements()
                .Any(e => e.Name.LocalName.Equals("stop", StringComparison.OrdinalIgnoreCase));
            if (hasStops)
            {
                return current;
            }

            var href = Attr(current, "href") ?? Attr(current, "xlink:href");
            if (href is null || !href.StartsWith('#'))
            {
                break;
            }

            var refId = href[1..].Trim();
            if (refId.Length == 0 || !ctx.PaintServers.TryGetValue(refId, out var refEl))
            {
                break;
            }

            current = refEl;
        }
        return current;
    }

    /// <summary>
    /// Collect <c>&lt;stop&gt;</c> children from a gradient element into
    /// ImageSharp <see cref="ColorStop"/> values. Handles stop-color and
    /// stop-opacity (as presentation attributes and via style="…").
    /// </summary>
    /// <summary>
    /// Resolve a gradient attribute through the <c>xlink:href</c>/<c>href</c>
    /// inheritance chain: a referencing gradient inherits geometry attributes
    /// (gradientUnits, gradientTransform, spreadMethod, x1/y1/x2/y2, cx/cy/r/fx/fy)
    /// it does not set itself from the gradient it points at. SVG 1.1 §13.2.3.
    /// Cycles are broken after 10 hops.
    /// </summary>
    private static string? GAttr(XElement gradEl, string name, SvgRenderContext ctx)
    {
        int guard = 0;
        var current = gradEl;
        while (guard++ < 10)
        {
            var v = Attr(current, name);
            if (v is not null)
            {
                return v;
            }

            var href = Attr(current, "href") ?? Attr(current, "xlink:href");
            if (href is null || !href.StartsWith('#'))
            {
                break;
            }

            var refId = href[1..].Trim();
            if (refId.Length == 0 || !ctx.PaintServers.TryGetValue(refId, out var refEl))
            {
                break;
            }

            current = refEl;
        }
        return null;
    }

    private static ColorStop[] CollectGradientStops(XElement gradEl)
    {
        var result = new List<ColorStop>();
        float? prevOffset = null;
        foreach (var stopEl in gradEl.Elements()
                    .Where(e => e.Name.LocalName.Equals("stop", StringComparison.OrdinalIgnoreCase)))
        {
            // Offset: a number [0,1] or percentage.
            float offset = 0f;
            var offsetStr = Attr(stopEl, "offset");
            if (offsetStr is not null)
            {
                var trimmed = offsetStr.Trim();
                if (trimmed.EndsWith('%')
                    && float.TryParse(trimmed[..^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                {
                    offset = Math.Clamp(pct / 100f, 0f, 1f);
                }
                else if (float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
                {
                    offset = Math.Clamp(raw, 0f, 1f);
                }
            }
            // Offsets must be non-decreasing (SVG 1.1 §13.2.4).
            if (prevOffset.HasValue && offset < prevOffset.Value)
            {
                offset = prevOffset.Value;
            }

            prevOffset = offset;

            // stop-color / stop-opacity live in style="" or as presentation attrs.
            // Build a minimal style and apply both.
            Color stopColor = Color.Black;
            float stopOpacity = 1f;

            // Parse stop-color from style or attribute.
            string? stopColorStr = null;
            string? stopOpacityStr = null;
            var styleStr = Attr(stopEl, "style");
            if (styleStr is not null)
            {
                foreach (var decl in styleStr.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    int colon = decl.IndexOf(':');
                    if (colon <= 0)
                    {
                        continue;
                    }

                    var prop = decl[..colon].Trim();
                    var val = decl[(colon + 1)..].Trim();
                    if (prop.Equals("stop-color", StringComparison.OrdinalIgnoreCase))
                    {
                        stopColorStr = val;
                    }
                    else if (prop.Equals("stop-opacity", StringComparison.OrdinalIgnoreCase))
                    {
                        stopOpacityStr = val;
                    }
                }
            }
            stopColorStr ??= Attr(stopEl, "stop-color");
            stopOpacityStr ??= Attr(stopEl, "stop-opacity");

            if (stopColorStr is not null && SvgColor.TryParse(stopColorStr, Color.Black, out var sc, out var scNone))
            {
                stopColor = scNone ? Color.Transparent : sc;
            }

            if (stopOpacityStr is not null
                && float.TryParse(stopOpacityStr.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var so))
            {
                stopOpacity = Math.Clamp(so, 0f, 1f);
            }

            // Fold stop-opacity into the alpha channel.
            var px = stopColor.ToPixel<Rgba32>();
            px.A = (byte)Math.Clamp((int)Math.Round(px.A / 255f * stopOpacity * 255f), 0, 255);
            result.Add(new ColorStop(offset, Color.FromPixel(px)));
        }
        return result.ToArray();
    }

    /// <summary>
    /// Parse a gradient coordinate value. In <c>objectBoundingBox</c> mode,
    /// pure numbers and percentages are fractions mapped onto
    /// <c>origin + fraction * size</c>. In <c>userSpaceOnUse</c> mode the
    /// value is a plain user-unit length.
    /// </summary>
    private static float? ParseGradCoord(string? v, bool obb, float origin, float size)
    {
        if (string.IsNullOrWhiteSpace(v))
        {
            return null;
        }

        var t = v.Trim();
        if (t.EndsWith('%')
            && float.TryParse(t[..^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            float fraction = pct / 100f;
            return obb ? origin + fraction * size : fraction * size;
        }
        if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
        {
            return obb ? origin + raw * size : raw;
        }
        return null;
    }

    /// <summary>
    /// Like <see cref="ParseGradCoord"/> but for radii: in objectBoundingBox mode
    /// a pure number/percentage is a fraction of the <c>refLength</c> (not offset
    /// by an origin).
    /// </summary>
    private static float? ParseGradCoordAbs(string? v, bool obb, float refLength)
    {
        if (string.IsNullOrWhiteSpace(v))
        {
            return null;
        }

        var t = v.Trim();
        if (t.EndsWith('%')
            && float.TryParse(t[..^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            return obb ? pct / 100f * refLength : pct / 100f * refLength;
        }

        if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var raw))
        {
            return obb ? raw * refLength : raw;
        }

        return null;
    }

    private static GradientRepetitionMode ParseSpreadMethod(string? v)
    {
        if (v is null)
        {
            return GradientRepetitionMode.None;
        }

        return v.Trim().ToLowerInvariant() switch
        {
            "repeat" => GradientRepetitionMode.Repeat,
            "reflect" => GradientRepetitionMode.Reflect,
            _ => GradientRepetitionMode.None,    // "pad" (default) → None
        };
    }

    /// <summary>
    /// Compute the bounding box of a path in its own user coordinate system
    /// (before any element transform). Uses <see cref="IPath.Bounds"/> which
    /// returns the axis-aligned bounding rectangle.
    /// </summary>
    private static RectangleF GetPathBounds(IPath path)
    {
        var b = path.Bounds;
        // Ensure non-zero extents so gradient coordinates don't divide by zero.
        float w = b.Width <= 0 ? 1f : b.Width;
        float h = b.Height <= 0 ? 1f : b.Height;
        return new RectangleF(b.Left, b.Top, w, h);
    }

    // --- shape builders ------------------------------------------------------

    private static IPath? BuildRect(XElement el, Vector2 viewport)
    {
        // x/width resolve percentages against the viewport width, y/height
        // against its height (e.g. a full-bleed `<rect width='100%' height='100%'>`).
        float x = ParseLengthPct(Attr(el, "x"), viewport.X) ?? 0;
        float y = ParseLengthPct(Attr(el, "y"), viewport.Y) ?? 0;
        float w = ParseLengthPct(Attr(el, "width"), viewport.X) ?? 0;
        float h = ParseLengthPct(Attr(el, "height"), viewport.Y) ?? 0;
        if (w <= 0 || h <= 0)
        {
            return null;
        }

        float? rxRaw = ParseLength(Attr(el, "rx"));
        float? ryRaw = ParseLength(Attr(el, "ry"));
        float rx = MathF.Min(rxRaw ?? ryRaw ?? 0, w / 2f);
        float ry = MathF.Min(ryRaw ?? rxRaw ?? 0, h / 2f);

        if (rx > 0 || ry > 0)
        {
            // Ensure both radii are non-zero (SVG 1.1 §9.2: if one is given the
            // other defaults to it, already handled by the ?? above).
            if (rx <= 0)
            {
                rx = ry;
            }

            if (ry <= 0)
            {
                ry = rx;
            }

            return RoundedRectDistinct(x, y, w, h, rx, ry);
        }

        return new RectanglePolygon(x, y, w, h);
    }

    /// <summary>
    /// Trace a rounded rect with distinct horizontal (<paramref name="rx"/>) and
    /// vertical (<paramref name="ry"/>) corner radii as cubic Bézier arcs.
    /// k = 0.5523 approximates a 90° arc (SVG 1.1 §9.2).
    /// </summary>
    private static IPath RoundedRectDistinct(float x, float y, float w, float h, float rx, float ry)
    {
        const float k = 0.5522847498f;
        var pb = new PathBuilder();
        pb.MoveTo(new PointF(x + rx, y));
        pb.LineTo(new PointF(x + w - rx, y));
        pb.AddCubicBezier(new PointF(x + w - rx, y), new PointF(x + w - rx + rx * k, y), new PointF(x + w, y + ry - ry * k), new PointF(x + w, y + ry));
        pb.LineTo(new PointF(x + w, y + h - ry));
        pb.AddCubicBezier(new PointF(x + w, y + h - ry), new PointF(x + w, y + h - ry + ry * k), new PointF(x + w - rx + rx * k, y + h), new PointF(x + w - rx, y + h));
        pb.LineTo(new PointF(x + rx, y + h));
        pb.AddCubicBezier(new PointF(x + rx, y + h), new PointF(x + rx - rx * k, y + h), new PointF(x, y + h - ry + ry * k), new PointF(x, y + h - ry));
        pb.LineTo(new PointF(x, y + ry));
        pb.AddCubicBezier(new PointF(x, y + ry), new PointF(x, y + ry - ry * k), new PointF(x + rx - rx * k, y), new PointF(x + rx, y));
        pb.CloseFigure();
        return pb.Build();
    }

    private static EllipsePolygon? BuildCircle(XElement el, Vector2 viewport)
    {
        float cx = ParseLengthPct(Attr(el, "cx"), viewport.X) ?? 0;
        float cy = ParseLengthPct(Attr(el, "cy"), viewport.Y) ?? 0;
        // SVG 1.1 §10.4: r percentage resolved against sqrt((vw²+vh²)/2)
        float vpDiag = MathF.Sqrt((viewport.X * viewport.X + viewport.Y * viewport.Y) / 2f);
        float r = ParseLengthPct(Attr(el, "r"), vpDiag) ?? 0;
        // EllipsePolygon takes full width/height (diameters), so pass 2*r.
        return r <= 0 ? null : new EllipsePolygon(cx, cy, 2f * r, 2f * r);
    }

    private static EllipsePolygon? BuildEllipse(XElement el, Vector2 viewport)
    {
        float cx = ParseLengthPct(Attr(el, "cx"), viewport.X) ?? 0;
        float cy = ParseLengthPct(Attr(el, "cy"), viewport.Y) ?? 0;
        float rx = ParseLengthPct(Attr(el, "rx"), viewport.X) ?? 0;
        float ry = ParseLengthPct(Attr(el, "ry"), viewport.Y) ?? 0;
        // EllipsePolygon takes full width/height (diameters), so pass 2*rx, 2*ry.
        return rx <= 0 || ry <= 0 ? null : new EllipsePolygon(cx, cy, 2f * rx, 2f * ry);
    }

    private static IPath BuildLine(XElement el, Vector2 viewport)
    {
        float x1 = ParseLengthPct(Attr(el, "x1"), viewport.X) ?? 0;
        float y1 = ParseLengthPct(Attr(el, "y1"), viewport.Y) ?? 0;
        float x2 = ParseLengthPct(Attr(el, "x2"), viewport.X) ?? 0;
        float y2 = ParseLengthPct(Attr(el, "y2"), viewport.Y) ?? 0;
        var pb = new PathBuilder();
        pb.AddLine(new PointF(x1, y1), new PointF(x2, y2));
        return pb.Build();
    }

    private static IPath? BuildPoly(XElement el, bool close)
    {
        var pts = ParsePoints(Attr(el, "points"));
        if (pts.Count < 2)
        {
            return null;
        }

        var pb = new PathBuilder();
        pb.AddLines(pts);
        if (close)
        {
            pb.CloseFigure();
        }

        return pb.Build();
    }

    private static List<PointF> ParsePoints(string? s)
    {
        var list = new List<PointF>();
        if (string.IsNullOrWhiteSpace(s))
        {
            return list;
        }

        var nums = s.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i + 1 < nums.Length; i += 2)
        {
            if (float.TryParse(nums[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) &&
                float.TryParse(nums[i + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            {
                list.Add(new PointF(x, y));
            }
        }
        return list;
    }

    // --- style attribute plumbing -------------------------------------------

    private static void ApplyPresentationAttributes(SvgStyle style, XElement el)
    {
        // color must be resolved before fill/stroke so currentColor sees it.
        ApplyAttr(style, el, "color");
        ApplyAttr(style, el, "fill");
        ApplyAttr(style, el, "stroke");
        ApplyAttr(style, el, "stroke-width");
        ApplyAttr(style, el, "fill-rule");
        ApplyAttr(style, el, "opacity");
        ApplyAttr(style, el, "fill-opacity");
        ApplyAttr(style, el, "stroke-opacity");
        ApplyAttr(style, el, "stroke-dasharray");
        ApplyAttr(style, el, "stroke-dashoffset");
        ApplyAttr(style, el, "stroke-linecap");
        ApplyAttr(style, el, "stroke-linejoin");
        ApplyAttr(style, el, "stroke-miterlimit");
        ApplyAttr(style, el, "display");
        ApplyAttr(style, el, "visibility");
        ApplyAttr(style, el, "paint-order");
        ApplyAttr(style, el, "mix-blend-mode");
        ApplyAttr(style, el, "marker-start");
        ApplyAttr(style, el, "marker-mid");
        ApplyAttr(style, el, "marker-end");
        ApplyAttr(style, el, "marker");
    }

    private static void ApplyAttr(SvgStyle style, XElement el, string name)
    {
        var v = Attr(el, name);
        if (v is not null)
        {
            style.ApplyDeclaration(name, v);
        }
    }

    private static void CollectStyles(XElement el, SvgStyleSheet sheet)
    {
        foreach (var styleEl in el.Descendants().Where(e => e.Name.LocalName.Equals("style", StringComparison.OrdinalIgnoreCase)))
        {
            sheet.AddCss(styleEl.Value);
        }
    }

    // --- attribute / unit helpers -------------------------------------------

    /// <summary>Read an attribute by local name, namespace-agnostic.</summary>
    private static string? Attr(XElement el, string localName)
    {
        foreach (var a in el.Attributes())
        {
            if (a.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            {
                return a.Value;
            }
        }

        return null;
    }

    private static float? ParseLength(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
        {
            return null;
        }

        v = v.Trim();
        if (v.EndsWith('%'))
        {
            return null; // percentage lengths unsupported (first cut)
        }
        // Strip a trailing unit suffix (px/pt/etc.); user units == px here.
        int end = v.Length;
        while (end > 0 && char.IsLetter(v[end - 1]))
        {
            end--;
        }

        return float.TryParse(v.AsSpan(0, end), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : null;
    }

    /// <summary>
    /// Like <see cref="ParseLength"/> but resolves a percentage against
    /// <paramref name="basis"/> (e.g. a viewport extent), used for shape
    /// geometry such as <c>&lt;rect width='100%'&gt;</c>.
    /// </summary>
    private static float? ParseLengthPct(string? v, float basis)
    {
        if (string.IsNullOrWhiteSpace(v))
        {
            return null;
        }

        var t = v.Trim();
        if (t.EndsWith('%')
            && float.TryParse(t[..^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
        {
            return pct / 100f * basis;
        }

        return ParseLength(v);
    }

    /// <summary>
    /// Map every paint server (<c>&lt;pattern&gt;</c>, <c>&lt;linearGradient&gt;</c>,
    /// <c>&lt;radialGradient&gt;</c>) and every element with an <c>id</c> so
    /// <c>fill="url(#id)"</c> references and <c>&lt;use href="#id"&gt;</c>
    /// can find them. Scans the whole document, not just <c>&lt;defs&gt;</c>,
    /// since the id is what binds.
    /// </summary>
    private static (Dictionary<string, XElement> paintServers, Dictionary<string, XElement> elementsById)
        CollectPaintServers(XElement root)
    {
        var servers = new Dictionary<string, XElement>(StringComparer.Ordinal);
        var byId = new Dictionary<string, XElement>(StringComparer.Ordinal);

        foreach (var el in root.DescendantsAndSelf())
        {
            var id = Attr(el, "id");
            if (!string.IsNullOrEmpty(id))
            {
                byId.TryAdd(id, el);
            }

            var localName = el.Name.LocalName;
            if (localName.Equals("pattern", StringComparison.OrdinalIgnoreCase)
             || localName.Equals("linearGradient", StringComparison.OrdinalIgnoreCase)
             || localName.Equals("radialGradient", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(id))
                {
                    servers.TryAdd(id, el);
                }
            }
        }
        return (servers, byId);
    }

    private static ViewBox? ParseViewBox(string? v)
    {
        if (string.IsNullOrWhiteSpace(v))
        {
            return null;
        }

        var parts = v.Split([' ', '\t', '\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4)
        {
            return null;
        }

        if (float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var minX) &&
            float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var minY) &&
            float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) &&
            float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var h))
        {
            return new ViewBox(minX, minY, w, h);
        }

        return null;
    }

    /// <summary>
    /// Map a 2D affine <see cref="Matrix3x2"/> into the <see cref="Matrix4x4"/>
    /// the ImageSharp.Drawing canvas consumes (row 0/1 carry the linear part,
    /// row 3 the translation).
    /// </summary>
    private static Matrix4x4 To4x4(Matrix3x2 m) => new(
        m.M11, m.M12, 0f, 0f,
        m.M21, m.M22, 0f, 0f,
        0f, 0f, 1f, 0f,
        m.M31, m.M32, 0f, 1f);

    private static Color? ApplyAlpha(Color c, float alphaScale)
    {
        var px = c.ToPixel<Rgba32>();
        float a = px.A / 255f * Math.Clamp(alphaScale, 0f, 1f);
        if (a <= 0f)
        {
            return null;
        }

        px.A = (byte)Math.Clamp((int)Math.Round(a * 255f), 0, 255);
        return Color.FromPixel(px);
    }

    private static string DecodeText(ReadOnlySpan<byte> utf8)
    {
        // Strip a UTF-8 BOM if present, then decode. SVG is XML; the bytes we get
        // are virtually always UTF-8 in practice.
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF)
        {
            utf8 = utf8[3..];
        }

        return System.Text.Encoding.UTF8.GetString(utf8);
    }

    private readonly record struct ViewBox(float MinX, float MinY, float Width, float Height);

    /// <summary>
    /// Per-render context threaded down the element tree: the harvested
    /// <c>&lt;style&gt;</c> sheet, the paint-server registry (id → element),
    /// the id → element table for <c>&lt;use&gt;</c> resolution, and the
    /// user-space viewport size used to resolve percentage geometry.
    /// </summary>
    private sealed record SvgRenderContext(
        SvgStyleSheet Sheet,
        IReadOnlyDictionary<string, XElement> PaintServers,
        IReadOnlyDictionary<string, XElement> ElementsById,
        Vector2 Viewport)
    {
        /// <summary>Offscreen images (pattern tiles, group opacity layers) that must
        /// outlive the canvas timeline (the brush samples them lazily when the
        /// Paint scope unwinds), disposed after rendering completes.</summary>
        public List<IDisposable> Pending { get; } = [];

        /// <summary>Current <c>&lt;use&gt;</c>/<c>&lt;pattern&gt;</c> reference nesting
        /// depth. Resolution increments this and bails past <see cref="MaxRefDepth"/>
        /// so a self- or mutually-recursive reference can't overflow the stack
        /// (SVG 1.1 §5.6 / §13.3: such references are errors).</summary>
        public int RefDepth { get; set; }

        /// <summary>Referenced elements currently being expanded (by reference
        /// identity). A reference that targets an element already on this stack is
        /// a cycle and is skipped — this also bounds <i>branching</i> recursion
        /// (e.g. a group with two self-<c>&lt;use&gt;</c>s) that a depth limit
        /// alone would let blow up exponentially.</summary>
        public HashSet<XElement> ActiveRefs { get; } = [];

        /// <summary>The active <c>clip-path</c> region in device pixels, or null
        /// when nothing is clipped. Each draw intersects with it; nested clips
        /// intersect with the enclosing one.</summary>
        public IPath? ClipDevice { get; set; }

        /// <summary>Whether <see cref="ClipDevice"/> uses the even-odd fill rule
        /// (from <c>clip-rule:evenodd</c>).</summary>
        public bool ClipEvenOdd { get; set; }

        /// <summary>Output canvas size in device pixels — the size of offscreen
        /// layers used by <c>filter</c> and <c>mask</c>.</summary>
        public int DeviceWidth { get; init; }
        public int DeviceHeight { get; init; }

        /// <summary>Elements whose <c>filter</c>/<c>mask</c> is mid-application,
        /// so re-entering the same element renders its raw content (no infinite
        /// loop).</summary>
        public HashSet<XElement> ActiveEffects { get; } = [];
    }

    /// <summary>Maximum <c>&lt;use&gt;</c>/<c>&lt;pattern&gt;</c> reference nesting before
    /// resolution gives up, guarding against recursive references.</summary>
    private const int MaxRefDepth = 24;
}
