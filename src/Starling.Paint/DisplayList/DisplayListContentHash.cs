using Starling.Css.Values;
using Starling.Layout;

namespace Starling.Paint.DisplayList;

/// <summary>
/// Computes a stable 64-bit FNV-1a hash over a display-list slice's
/// visually-meaningful content — bounds, colors, text + font identity, image
/// refs, gradients, and the transforms/clips inside the slice. This is the
/// per-layer picture-cache key (plan LTF-02): two consecutive composites whose
/// only difference is the layer's composite-time transform/opacity (suppressed
/// from the slice, applied at composite) hash identically and serve from cache,
/// while a content change (color/text/size) produces a different hash and
/// re-rasters exactly that layer.
/// </summary>
/// <remarks>
/// 64-bit width keeps a stale-pixel collision astronomically unlikely. The walk
/// folds the exact field values that drive the raster, so a false HIT would
/// require two genuinely different slices to collide — not merely two equal
/// slices to hash equal (which they always do, the property we rely on). Any
/// field omission only ever risks a false MISS (a needless re-raster, safe),
/// never a false HIT, so unknown future item kinds fold their discriminator and
/// degrade to re-rastering rather than reusing stale pixels.
/// </remarks>
internal static class DisplayListContentHash
{
    // Use the standard constants for the  64-bit FNV-1a hash.
    private const ulong FnvOffset = 14695981039346656037UL; // initial hash value / offset basis
    private const ulong FnvPrime = 1099511628211UL;

    public static long Compute(DisplayList list)
    {
        var h = FnvOffset;
        var items = list.Items;
        for (var i = 0; i < items.Count; i++)
        {
            HashItem(ref h, items[i]);
        }

        return unchecked((long)h);
    }

    /// <summary>
    /// Hash of a filter chain alone. Folded into a layer's content hash when the
    /// chain is carried on the <c>CompositorLayer</c> instead of a slice
    /// PushFilter bracket, so a filter change (an animating blur radius, a hover
    /// brightness) still re-rasters the layer.
    /// </summary>
    public static long ComputeFilters(IReadOnlyList<FilterFunction> filters)
    {
        var h = FnvOffset;
        HashFilters(ref h, filters);
        return unchecked((long)h);
    }

    /// <summary>FNV-1a fold of <paramref name="b"/> into <paramref name="a"/> —
    /// order-sensitive combination of two content hashes.</summary>
    public static long Combine(long a, long b)
    {
        var h = unchecked((ulong)a);
        HashLong(ref h, b);
        return unchecked((long)h);
    }

    // Antialiasing/edge bleed safety (page px): a glyph or fill whose AABB ends
    // exactly on a tile seam can still touch the adjacent tile's pixels, so the
    // per-tile membership test grows each item's bounds by this much. Over-
    // inclusion only ever costs a needless re-raster (a false MISS), never a
    // stale tile (a false HIT) — the same safety contract Compute relies on.
    private const double EdgeMargin = 2.0;

    /// <summary>
    /// Walks a slice once into per-item effective AABBs (page coords, with the
    /// enclosing transform applied) so a per-tile content hash can be taken
    /// cheaply for many tiles without re-walking. Bracket items (Push/Pop
    /// Transform/Clip) and items with no computable bounds are marked "always
    /// fold" — they contribute to every tile's hash, so any change to them
    /// conservatively invalidates the whole layer.
    /// </summary>
    public static PreparedSlice Prepare(DisplayList list)
    {
        var items = list.Items;
        var entries = new List<Entry>(items.Count);
        var transform = Matrix2D.Identity;
        var stack = new Stack<Matrix2D>();
        stack.Push(Matrix2D.Identity);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            switch (item)
            {
                case PushTransform p:
                    entries.Add(new Entry(item, alwaysFold: true, default));
                    transform = transform.Multiply(p.Matrix);
                    stack.Push(transform);
                    continue;
                case PopTransform:
                    entries.Add(new Entry(item, alwaysFold: true, default));
                    if (stack.Count > 1)
                    {
                        stack.Pop();
                    }

                    transform = stack.Peek();
                    continue;
                case PushClip:
                case PopClip:
                    entries.Add(new Entry(item, alwaysFold: true, default));
                    continue;
                // Filter brackets affect every pixel the group paints (a blur
                // mixes neighbouring tiles), so fold them into every tile's
                // hash — a chain change re-rasters the whole layer.
                case PushFilter:
                case PopFilter:
                    entries.Add(new Entry(item, alwaysFold: true, default));
                    continue;
            }

            if (DisplayItemBounds.TryGet(item, out var local))
            {
                var aabb = TransformedAabb(Inflate(local, EdgeMargin + ExtraMargin(item)), transform);
                entries.Add(new Entry(item, alwaysFold: false, aabb));
            }
            else
            {
                // Unknown bounds → fold everywhere (conservative).
                entries.Add(new Entry(item, alwaysFold: true, default));
            }
        }

        return new PreparedSlice(entries);
    }

    internal readonly struct Entry(DisplayItem item, bool alwaysFold, Rect aabb)
    {
        public DisplayItem Item { get; } = item;
        public bool AlwaysFold { get; } = alwaysFold;
        public Rect Aabb { get; } = aabb;
    }

    /// <summary>
    /// A slice pre-walked by <see cref="Prepare"/>. <see cref="HashForTile"/>
    /// returns the content hash of just the items that can paint into a given
    /// page-space tile rect (plus all bracket items), so a change confined to one
    /// region only changes the hash of the tiles it overlaps — the per-tile cache
    /// then re-rasters those tiles and reuses the rest.
    /// </summary>
    public sealed class PreparedSlice
    {
        private readonly List<Entry> _entries;
        internal PreparedSlice(List<Entry> entries) => _entries = entries;

        public long HashForTile(Rect tilePage)
        {
            var h = FnvOffset;
            foreach (var e in _entries)
            {
                if (e.AlwaysFold || Intersects(e.Aabb, tilePage))
                {
                    HashItem(ref h, e.Item);
                }
            }

            return unchecked((long)h);
        }
    }

    // Per-item extra coverage (page px) on top of EdgeMargin, where the painted
    // pixels extend beyond DisplayItemBounds.TryGet's box. Symmetric inflation —
    // over-covering only costs a needless re-raster, never a stale tile:
    //  • text: TryGet's box is anchored at the line-box top, but glyphs descend
    //    ~0.8·FontSize below its bottom edge → grow by FontSize.
    //  • text shadow: TryGet grows by 1·Blur but the raster's Gaussian tail reaches
    //    ~3·Blur, plus the same descender gap → FontSize + 2·Blur.
    //  • box shadow: TryGet uses Spread+Blur; the raster's 3σ tail (σ=Blur/2) reaches
    //    Spread + 1.5·Blur → grow by ~Blur.
    //  • strokes: TryGet returns the centre-line box, but a centred pen paints
    //    Width/2 outside it.
    private static double ExtraMargin(DisplayItem item) => item switch
    {
        DrawText t => t.FontSize,
        DrawTextDecoration d => d.FontSize,
        DrawTextShadow s => s.FontSize + s.Blur * 2,
        DrawBoxShadow s => s.Blur,
        StrokeRect s => s.Width / 2,
        StrokeRoundedRect s => s.Width / 2,
        // Anti-aliased dash/dot/arc edges can touch the border-box boundary.
        DrawBorderSides b => Math.Max(Math.Max(b.TopWidth, b.BottomWidth), Math.Max(b.LeftWidth, b.RightWidth)) / 2,
        _ => 0,
    };

    private static Rect Inflate(Rect r, double m)
        => new(r.X - m, r.Y - m, r.Width + 2 * m, r.Height + 2 * m);

    private static bool Intersects(Rect a, Rect b)
        => a.X < b.Right && b.X < a.Right && a.Y < b.Bottom && b.Y < a.Bottom;

    private static Rect TransformedAabb(Rect r, Matrix2D m)
    {
        if (m.IsIdentity)
        {
            return r;
        }

        var (x0, y0) = m.Transform(r.X, r.Y);
        var (x1, y1) = m.Transform(r.X + r.Width, r.Y);
        var (x2, y2) = m.Transform(r.X + r.Width, r.Y + r.Height);
        var (x3, y3) = m.Transform(r.X, r.Y + r.Height);
        var minX = Math.Min(Math.Min(x0, x1), Math.Min(x2, x3));
        var minY = Math.Min(Math.Min(y0, y1), Math.Min(y2, y3));
        var maxX = Math.Max(Math.Max(x0, x1), Math.Max(x2, x3));
        var maxY = Math.Max(Math.Max(y0, y1), Math.Max(y2, y3));
        return new Rect(minX, minY, maxX - minX, maxY - minY);
    }

    private static void HashItem(ref ulong h, DisplayItem item)
    {
        switch (item)
        {
            case FillRect f:
                Tag(ref h, 1);
                HashRect(ref h, f.Bounds);
                HashColor(ref h, f.Color);
                HashInt(ref h, (int)f.PixelAlignment);
                break;
            case StrokeRect s:
                Tag(ref h, 2);
                HashRect(ref h, s.Bounds);
                HashColor(ref h, s.Color);
                HashDouble(ref h, s.Width);
                break;
            case DrawText t:
                Tag(ref h, 3);
                HashString(ref h, t.Text);
                HashDouble(ref h, t.X);
                HashDouble(ref h, t.Y);
                HashDouble(ref h, t.FontSize);
                HashColor(ref h, t.Color);
                HashFonts(ref h, t.FontFamilies);
                HashBool(ref h, t.Bold);
                HashBool(ref h, t.Italic);
                HashDouble(ref h, t.Shaped?.Advance ?? 0d);
                break;
            case DrawImage i:
                Tag(ref h, 4);
                HashRect(ref h, i.Bounds);
                // Reference identity of the decoded pixels: a re-decode yields a
                // fresh object → different hash → correct re-raster. Same decoded
                // image reused → same hash → cache hit.
                HashInt(ref h, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(i.Source));
                if (i.SourceRect is { } sr) { HashRect(ref h, sr); }
                break;
            case PushTransform p:
                Tag(ref h, 5);
                HashMatrix(ref h, p.Matrix);
                break;
            case PopTransform:
                Tag(ref h, 6);
                break;
            case PushClip pc:
                Tag(ref h, 7);
                HashRect(ref h, pc.Bounds);
                break;
            case PopClip:
                Tag(ref h, 8);
                break;
            case FillGradient g:
                Tag(ref h, 9);
                HashRect(ref h, g.Bounds);
                // Gradient values are rebuilt fresh each frame, so a structural
                // change makes a new object that hashes differently; an unchanged
                // gradient on a rebuilt list simply re-rasters (safe).
                HashInt(ref h, g.Gradient.GetHashCode());
                break;
            case FillRoundedRect rf:
                Tag(ref h, 10);
                HashRect(ref h, rf.Bounds);
                HashRadii(ref h, rf.Radii);
                HashColor(ref h, rf.Color);
                break;
            case StrokeRoundedRect rs:
                Tag(ref h, 11);
                HashRect(ref h, rs.Bounds);
                HashRadii(ref h, rs.Radii);
                HashColor(ref h, rs.Color);
                HashDouble(ref h, rs.Width);
                break;
            case DrawBoxShadow sh:
                Tag(ref h, 12);
                HashRect(ref h, sh.Bounds);
                HashRadii(ref h, sh.Radii);
                HashDouble(ref h, sh.OffsetX);
                HashDouble(ref h, sh.OffsetY);
                HashDouble(ref h, sh.Blur);
                HashDouble(ref h, sh.Spread);
                HashColor(ref h, sh.Color);
                HashBool(ref h, sh.Inset);
                break;
            case DrawTextDecoration d:
                Tag(ref h, 13);
                HashDouble(ref h, d.X);
                HashDouble(ref h, d.Width);
                HashDouble(ref h, d.BaselineY);
                HashDouble(ref h, d.FontSize);
                HashColor(ref h, d.Color);
                HashInt(ref h, (int)d.Lines);
                HashInt(ref h, (int)d.Style);
                HashDouble(ref h, d.Thickness);
                HashDouble(ref h, d.UnderlineOffset);
                HashFonts(ref h, d.FontFamilies);
                HashBool(ref h, d.Bold);
                HashBool(ref h, d.Italic);
                break;
            case DrawBorderSides bs:
                Tag(ref h, 15);
                HashRect(ref h, bs.Bounds);
                HashRadii(ref h, bs.Radii);
                HashDouble(ref h, bs.TopWidth);
                HashDouble(ref h, bs.RightWidth);
                HashDouble(ref h, bs.BottomWidth);
                HashDouble(ref h, bs.LeftWidth);
                HashColor(ref h, bs.TopColor);
                HashColor(ref h, bs.RightColor);
                HashColor(ref h, bs.BottomColor);
                HashColor(ref h, bs.LeftColor);
                HashInt(ref h, (int)bs.TopStyle);
                HashInt(ref h, (int)bs.RightStyle);
                HashInt(ref h, (int)bs.BottomStyle);
                HashInt(ref h, (int)bs.LeftStyle);
                break;
            case DrawTextShadow s:
                Tag(ref h, 14);
                HashString(ref h, s.Text);
                HashDouble(ref h, s.X);
                HashDouble(ref h, s.Y);
                HashDouble(ref h, s.FontSize);
                HashColor(ref h, s.Color);
                HashDouble(ref h, s.OffsetX);
                HashDouble(ref h, s.OffsetY);
                HashDouble(ref h, s.Blur);
                HashFonts(ref h, s.FontFamilies);
                HashBool(ref h, s.Bold);
                HashBool(ref h, s.Italic);
                break;
            case StrokeSegments seg:
                Tag(ref h, 16);
                HashDouble(ref h, seg.X0);
                HashDouble(ref h, seg.Y0);
                HashDouble(ref h, seg.X1);
                HashDouble(ref h, seg.Y1);
                HashDouble(ref h, seg.X2);
                HashDouble(ref h, seg.Y2);
                HashColor(ref h, seg.Color);
                HashDouble(ref h, seg.Width);
                break;
            case PushFilter pf:
                Tag(ref h, 17);
                HashRect(ref h, pf.Bounds);
                HashFilters(ref h, pf.Filters);
                break;
            case PopFilter:
                Tag(ref h, 18);
                break;
            case DrawBackdropFilter bf:
                Tag(ref h, 19);
                HashRect(ref h, bf.Bounds);
                HashRadii(ref h, bf.Radii);
                HashFilters(ref h, bf.Filters);
                break;
            default:
                // Unknown item kind: fold a distinct discriminator so its mere
                // presence changes the hash. Worst case is a needless re-raster.
                Tag(ref h, 255);
                break;
        }
    }

    private static void Tag(ref ulong h, byte discriminator) => HashByte(ref h, discriminator);

    private static void HashByte(ref ulong h, byte b)
    {
        h ^= b;
        h *= FnvPrime;
    }

    private static void HashInt(ref ulong h, int v)
    {
        HashByte(ref h, (byte)v);
        HashByte(ref h, (byte)(v >> 8));
        HashByte(ref h, (byte)(v >> 16));
        HashByte(ref h, (byte)(v >> 24));
    }

    private static void HashLong(ref ulong h, long v)
    {
        for (var i = 0; i < 8; i++)
        {
            HashByte(ref h, (byte)(v >> (i * 8)));
        }
    }

    private static void HashDouble(ref ulong h, double d)
        => HashLong(ref h, BitConverter.DoubleToInt64Bits(d));

    private static void HashBool(ref ulong h, bool b) => HashByte(ref h, b ? (byte)1 : (byte)0);

    private static void HashString(ref ulong h, string s)
    {
        HashInt(ref h, s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            HashByte(ref h, (byte)c);
            HashByte(ref h, (byte)(c >> 8));
        }
    }

    private static void HashFonts(ref ulong h, IReadOnlyList<string> families)
    {
        HashInt(ref h, families.Count);
        for (var i = 0; i < families.Count; i++)
        {
            HashString(ref h, families[i]);
        }
    }

    private static void HashFilters(ref ulong h, IReadOnlyList<FilterFunction> filters)
    {
        HashInt(ref h, filters.Count);
        for (var i = 0; i < filters.Count; i++)
        {
            var f = filters[i];
            HashInt(ref h, (int)f.Kind);
            HashDouble(ref h, f.Amount);
            HashDouble(ref h, f.OffsetX);
            HashDouble(ref h, f.OffsetY);
            HashColor(ref h, f.Color ?? CssColor.Black);
        }
    }

    private static void HashRect(ref ulong h, Rect r)
    {
        HashDouble(ref h, r.X);
        HashDouble(ref h, r.Y);
        HashDouble(ref h, r.Width);
        HashDouble(ref h, r.Height);
    }

    private static void HashColor(ref ulong h, CssColor c)
    {
        HashByte(ref h, c.R);
        HashByte(ref h, c.G);
        HashByte(ref h, c.B);
        HashByte(ref h, c.A);
    }

    private static void HashMatrix(ref ulong h, Matrix2D m)
    {
        HashDouble(ref h, m.A);
        HashDouble(ref h, m.B);
        HashDouble(ref h, m.C);
        HashDouble(ref h, m.D);
        HashDouble(ref h, m.E);
        HashDouble(ref h, m.F);
    }

    private static void HashRadii(ref ulong h, CornerRadii radii)
    {
        HashDouble(ref h, radii.TopLeftX);
        HashDouble(ref h, radii.TopLeftY);
        HashDouble(ref h, radii.TopRightX);
        HashDouble(ref h, radii.TopRightY);
        HashDouble(ref h, radii.BottomRightX);
        HashDouble(ref h, radii.BottomRightY);
        HashDouble(ref h, radii.BottomLeftX);
        HashDouble(ref h, radii.BottomLeftY);
    }
}
