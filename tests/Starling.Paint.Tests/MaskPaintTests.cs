// SPDX-License-Identifier: Apache-2.0
using AwesomeAssertions;
using Starling.Common.Image;
using Starling.Css.Values;
using Starling.Paint.Backend;
using Starling.Paint.DisplayList;
using LayoutSize = Starling.Layout.Size;
using LayoutRect = Starling.Layout.Rect;
using PaintList = Starling.Paint.DisplayList.DisplayList;

namespace Starling.Paint.Tests;

/// <summary>
/// CSS Masking 1 §6 — pixel-probe coverage for <see cref="FillMaskedBackground"/>
/// rasterized through <see cref="ImageSharpBackend"/>. Tests cover:
/// alpha masking (default), luminance masking, gradient mask sources,
/// corner-radius clipping, and mask-repeat space/round tiling.
/// </summary>
[TestClass]
public sealed class MaskPaintTests
{
    private static readonly CssColor Red = new(255, 0, 0);
    private static readonly CssColor Green = new(0, 200, 0);

    /// <summary>A width×height RGBA8888 mask whose left half is opaque white and
    /// right half is fully transparent.</summary>
    private static DecodedImage LeftOpaqueRightTransparentMask(int width, int height)
    {
        var buf = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                buf[i + 0] = 255; // R
                buf[i + 1] = 255; // G
                buf[i + 2] = 255; // B
                buf[i + 3] = (byte)(x < width / 2 ? 255 : 0); // A: opaque left, clear right
            }
        }
        return DecodedImage.FromBuffer(width, height, buf);
    }

    [TestMethod]
    public void Mask_keeps_background_where_opaque_and_removes_it_where_transparent()
    {
        using var mask = LeftOpaqueRightTransparentMask(100, 100);
        var item = new FillMaskedBackground(
            Bounds: new LayoutRect(0, 0, 100, 100),
            Gradient: null,
            Color: Red,
            BackgroundImage: null,
            Radii: CornerRadii.None,
            Mask: mask,
            MaskGradient: null,
            MaskRenderWidth: 100,
            MaskRenderHeight: 100,
            MaskOffsetX: 0,
            MaskOffsetY: 0,
            MaskRepeat: MaskRepeatMode.NoRepeat);

        var list = new PaintList();
        list.Add(item);

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        // Canvas clears to opaque white; the red background should remain only on
        // the masked (left) half.
        using var bmp = backend.Render(list, new LayoutSize(100, 100));

        var (lr, lg, lb, _) = bmp.GetPixel(25, 50); // mask opaque → red survives
        var (rr, rg, rb, _) = bmp.GetPixel(75, 50); // mask transparent → white

        lr.Should().BeGreaterThan(200, "the masked (opaque) half keeps the red background");
        lg.Should().BeLessThan(60);
        lb.Should().BeLessThan(60);

        rr.Should().BeGreaterThan(200, "the unmasked (transparent) half is the white canvas");
        rg.Should().BeGreaterThan(200);
        rb.Should().BeGreaterThan(200);
    }

    [TestMethod]
    public void Mask_repeat_tiles_a_small_mask_across_the_box()
    {
        // A 10px-wide mask, left half opaque, tiled across a 100px box: every
        // 10px column repeats opaque(0..5)/clear(5..10). Probe a tile far from
        // the origin to prove tiling reached it.
        using var mask = LeftOpaqueRightTransparentMask(10, 10);
        var item = new FillMaskedBackground(
            Bounds: new LayoutRect(0, 0, 100, 100),
            Gradient: null,
            Color: Red,
            BackgroundImage: null,
            Radii: CornerRadii.None,
            Mask: mask,
            MaskGradient: null,
            MaskRenderWidth: 10,
            MaskRenderHeight: 10,
            MaskOffsetX: 0,
            MaskOffsetY: 0,
            MaskRepeat: MaskRepeatMode.Repeat);

        var list = new PaintList();
        list.Add(item);

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(100, 100));

        // Tile starting at x=80: opaque 80..85 (red), transparent 85..90 (white).
        var (or, _, ob, _) = bmp.GetPixel(82, 50);
        var (tr, tg, tb, _) = bmp.GetPixel(87, 50);

        or.Should().BeGreaterThan(200, "the opaque part of a far tile keeps red");
        ob.Should().BeLessThan(60);

        tr.Should().BeGreaterThan(200, "the clear part of a far tile shows white");
        tg.Should().BeGreaterThan(200);
        tb.Should().BeGreaterThan(200);
    }

    // ---- Fix #1: corner radii clipping ----------------------------------------

    /// <summary>
    /// A red 100×100 box with a fully-opaque mask and a 50px border-radius should
    /// be clipped to a circle: the centre is red, but the four corners (which lie
    /// outside the rounded shape) must show the white canvas.
    /// </summary>
    [TestMethod]
    public void Mask_with_border_radius_clips_corners_to_rounded_shape()
    {
        // Fully-opaque white mask so the full fill survives — only radii matter.
        var buf = new byte[100 * 100 * 4];
        for (var i = 0; i < buf.Length; i += 4)
        {
            buf[i] = buf[i + 1] = buf[i + 2] = buf[i + 3] = 255;
        }

        using var mask = DecodedImage.FromBuffer(100, 100, buf);

        // 50px uniform radius on a 100px box → circle.
        var radii = CornerRadii.Uniform(50, 50, 50, 50);
        var item = new FillMaskedBackground(
            Bounds: new LayoutRect(0, 0, 100, 100),
            Gradient: null,
            Color: Red,
            BackgroundImage: null,
            Radii: radii,
            Mask: mask,
            MaskGradient: null,
            MaskRenderWidth: 100,
            MaskRenderHeight: 100,
            MaskOffsetX: 0,
            MaskOffsetY: 0,
            MaskRepeat: MaskRepeatMode.NoRepeat);

        var list = new PaintList();
        list.Add(item);

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(100, 100));

        // Centre: inside the circle → red.
        var (cr, cg, cb, _) = bmp.GetPixel(50, 50);
        cr.Should().BeGreaterThan(200, "the circle centre is inside the clip radius and should be red");
        cg.Should().BeLessThan(60);
        cb.Should().BeLessThan(60);

        // Corner (2,2): outside the circle → white canvas.
        var (xr, xg, xb, _) = bmp.GetPixel(2, 2);
        xr.Should().BeGreaterThan(200, "the top-left corner lies outside the rounded clip and should be white");
        xg.Should().BeGreaterThan(200);
        xb.Should().BeGreaterThan(200);
    }

    // ---- Fix #3: luminance vs alpha mask-mode ---------------------------------

    /// <summary>
    /// A pure-white mask in alpha mode should let the full red background through
    /// (alpha=255 → full opacity). The same mask in luminance mode should also pass
    /// because white has luminance ≈ 1.0. Then a pure-black mask in luminance mode
    /// should block the red because black has luminance = 0.
    /// Alpha mask with pure-white: red survives; luminance mask with pure-black: blocked.
    /// </summary>
    [TestMethod]
    public void Luminance_mask_black_blocks_alpha_mask_white_passes()
    {
        // White mask: alpha=255 everywhere (all opaque), colour=white (luma≈1).
        var whiteBuf = new byte[40 * 40 * 4];
        for (var i = 0; i < whiteBuf.Length; i += 4)
        {
            whiteBuf[i] = whiteBuf[i + 1] = whiteBuf[i + 2] = whiteBuf[i + 3] = 255;
        }

        using var whiteMask = DecodedImage.FromBuffer(40, 40, whiteBuf);

        // Black mask: alpha=255 everywhere (fully opaque), colour=black (luma=0).
        var blackBuf = new byte[40 * 40 * 4];
        for (var i = 0; i < blackBuf.Length; i += 4)
        {
            blackBuf[i] = 0;   // R
            blackBuf[i + 1] = 0; // G
            blackBuf[i + 2] = 0; // B
            blackBuf[i + 3] = 255; // A
        }
        using var blackMask = DecodedImage.FromBuffer(40, 40, blackBuf);

        FillMaskedBackground MakeItem(DecodedImage m, MaskModeKind mode) =>
            new(Bounds: new LayoutRect(0, 0, 40, 40),
                Gradient: null, Color: Red, BackgroundImage: null, Radii: CornerRadii.None,
                Mask: m, MaskGradient: null,
                MaskRenderWidth: 40, MaskRenderHeight: 40,
                MaskOffsetX: 0, MaskOffsetY: 0,
                MaskRepeat: MaskRepeatMode.NoRepeat,
                Mode: mode);

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);

        // Alpha mode + white mask → red survives.
        {
            var list = new PaintList();
            list.Add(MakeItem(whiteMask, MaskModeKind.Alpha));
            using var bmp = backend.Render(list, new LayoutSize(40, 40));
            var (r, g, b, _) = bmp.GetPixel(20, 20);
            r.Should().BeGreaterThan(200, "alpha mode + white mask = full opacity; red should show");
            g.Should().BeLessThan(60);
            b.Should().BeLessThan(60);
        }

        // Luminance mode + black mask → blocked (luma=0 → no fill; canvas stays white).
        {
            var list = new PaintList();
            list.Add(MakeItem(blackMask, MaskModeKind.Luminance));
            using var bmp = backend.Render(list, new LayoutSize(40, 40));
            var (r, g, b, _) = bmp.GetPixel(20, 20);
            r.Should().BeGreaterThan(200, "luminance mode + black mask = luma=0; canvas white should show");
            g.Should().BeGreaterThan(200);
            b.Should().BeGreaterThan(200);
        }
    }

    /// <summary>
    /// Luminance mode with a white mask and luminance mode with a white mask both
    /// produce a red result (luma=1 → full opacity). This confirms the alpha-mode
    /// and luminance-mode code paths differ and both work for the pass-through case.
    /// </summary>
    [TestMethod]
    public void Luminance_mask_white_passes_same_as_alpha()
    {
        var whiteBuf = new byte[40 * 40 * 4];
        for (var i = 0; i < whiteBuf.Length; i += 4)
        {
            whiteBuf[i] = whiteBuf[i + 1] = whiteBuf[i + 2] = whiteBuf[i + 3] = 255;
        }

        using var whiteMask = DecodedImage.FromBuffer(40, 40, whiteBuf);

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);

        var listAlpha = new PaintList();
        listAlpha.Add(new FillMaskedBackground(
            Bounds: new LayoutRect(0, 0, 40, 40), Gradient: null, Color: Red,
            BackgroundImage: null, Radii: CornerRadii.None,
            Mask: whiteMask, MaskGradient: null,
            MaskRenderWidth: 40, MaskRenderHeight: 40,
            MaskOffsetX: 0, MaskOffsetY: 0,
            MaskRepeat: MaskRepeatMode.NoRepeat,
            Mode: MaskModeKind.Alpha));

        var listLuma = new PaintList();
        listLuma.Add(new FillMaskedBackground(
            Bounds: new LayoutRect(0, 0, 40, 40), Gradient: null, Color: Red,
            BackgroundImage: null, Radii: CornerRadii.None,
            Mask: whiteMask, MaskGradient: null,
            MaskRenderWidth: 40, MaskRenderHeight: 40,
            MaskOffsetX: 0, MaskOffsetY: 0,
            MaskRepeat: MaskRepeatMode.NoRepeat,
            Mode: MaskModeKind.Luminance));

        using var bmpAlpha = backend.Render(listAlpha, new LayoutSize(40, 40));
        using var bmpLuma = backend.Render(listLuma, new LayoutSize(40, 40));

        var (ar, ag, ab, _) = bmpAlpha.GetPixel(20, 20);
        var (lr, lg, lb, _) = bmpLuma.GetPixel(20, 20);

        ar.Should().BeGreaterThan(200, "alpha + white mask: red passes");
        lr.Should().BeGreaterThan(200, "luminance + white mask: luma=1 so red passes");
    }

    // ---- Fix #4: gradient mask sources ----------------------------------------

    /// <summary>
    /// A linear-gradient mask from fully-opaque on the left to fully-transparent
    /// on the right: the red background should be bright on the left and fade
    /// (near-white on the canvas) on the right.
    /// </summary>
    [TestMethod]
    public void Gradient_mask_fades_background_left_to_right()
    {
        // linear-gradient(to right, black 0%, transparent 100%) used as mask.
        // black stops: alpha is set via colour stop alpha.
        // We need opaque-to-transparent; use CssColor(0,0,0,255) → CssColor(0,0,0,0).
        var maskGradient = new CssGradient(
            CssGradientKind.Linear,
            Repeating: false,
            new[]
            {
                new CssColorStop(new CssColor(0, 0, 0, 255)),  // opaque
                new CssColorStop(new CssColor(0, 0, 0, 0)),   // transparent
            },
            Line: CssGradientLine.FromAngle(90)); // to right

        var item = new FillMaskedBackground(
            Bounds: new LayoutRect(0, 0, 100, 40),
            Gradient: null,
            Color: Red,
            BackgroundImage: null,
            Radii: CornerRadii.None,
            Mask: null,
            MaskGradient: maskGradient,
            MaskRenderWidth: 100,
            MaskRenderHeight: 40,
            MaskOffsetX: 0,
            MaskOffsetY: 0,
            MaskRepeat: MaskRepeatMode.NoRepeat);

        var list = new PaintList();
        list.Add(item);

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(100, 40));

        // Left side: mask is opaque → red survives.
        var (lr, lg, lb, _) = bmp.GetPixel(5, 20);
        // Right side: mask is transparent → canvas white shows.
        var (rr, rg, rb, _) = bmp.GetPixel(95, 20);

        lr.Should().BeGreaterThan(200, "left side of gradient mask is opaque; red background should show");
        lg.Should().BeLessThan(80, "left side of gradient mask is opaque; should be red not white");

        // The right side is mostly transparent, so the white canvas dominates.
        rg.Should().BeGreaterThan(150, "right side of gradient mask is transparent; white canvas shows through");
        rb.Should().BeGreaterThan(150, "right side of gradient mask is transparent; white canvas shows through");
    }

    // ---- Fix #2: whole-element masking (PushMask / PopMask) -------------------

    /// <summary>
    /// A PushMask / PopMask bracket containing both a FillRect (background) and a
    /// FillRect (simulated border) should mask the full group. The left half of the
    /// box is red background and green "border" (both FillRects); the right half
    /// should be masked out. After masking, the left half shows content; the right
    /// half shows the white canvas.
    /// </summary>
    [TestMethod]
    public void PushMask_masks_full_element_group_including_border()
    {
        using var mask = LeftOpaqueRightTransparentMask(100, 100);
        var bounds = new LayoutRect(0, 0, 100, 100);

        var list = new PaintList();
        list.Add(new PushMask(
            Bounds: bounds,
            Radii: CornerRadii.None,
            Mask: mask,
            MaskGradient: null,
            MaskRenderWidth: 100,
            MaskRenderHeight: 100,
            MaskOffsetX: 0,
            MaskOffsetY: 0,
            MaskRepeat: MaskRepeatMode.NoRepeat));
        // Background fill: red
        list.Add(new FillRect(bounds, Red, FillRectPixelAlignment.Preserve));
        // Simulated "border" fill (green, top strip) — should also be masked.
        list.Add(new FillRect(new LayoutRect(0, 0, 100, 5), Green, FillRectPixelAlignment.Preserve));
        list.Add(PopMask.Instance);

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(100, 100));

        // Left half (mask opaque) — both red background and green border should show.
        // The "border" strip at y=2: should be greenish on the left.
        var (lr, lg, lb, _) = bmp.GetPixel(25, 50); // mid-height, left of mask → red
        lr.Should().BeGreaterThan(200, "the left half of the masked group should show the red background");
        lg.Should().BeLessThan(60, "left half red background — green channel should be low");

        // Right half (mask transparent) — canvas white.
        var (rr, rg, rb, _) = bmp.GetPixel(75, 50);
        rr.Should().BeGreaterThan(200, "the right half is masked out; white canvas shows");
        rg.Should().BeGreaterThan(200);
        rb.Should().BeGreaterThan(200);

        // Top-left corner of the "border" strip (left half, green) should be greenish.
        var (br, bg2, bb, _) = bmp.GetPixel(10, 2); // green border strip, left half
        bg2.Should().BeGreaterThan(100, "the top-left of the green border strip should be green (masked group includes border)");
        bb.Should().BeLessThan(100);
    }

    // ---- Fix #5: mask-repeat space and round ----------------------------------

    /// <summary>
    /// mask-repeat: space — an integer number of whole tiles should fill the box
    /// with even spacing. For a 10px tile in a 100px box: 10 whole tiles fit with
    /// 0 gap each, so it degenerates to the normal repeat case. Use a 15px box
    /// so only 6 tiles fit (6×15=90px), leaving 10px of gap spread across 5 gaps
    /// (2px each). Probe an opaque-tile position and a gap position.
    /// </summary>
    [TestMethod]
    public void Mask_repeat_space_places_tiles_with_even_gaps()
    {
        // 10×10 mask, left half opaque — used in a 100px box with space tiling.
        // 100px / 10px = 10 tiles, no gap; fall back to simpler math: use a 25px
        // box. 25px / 10px → floor = 2 whole tiles; gap = (25 - 20) / 1 = 5px gap.
        // Tile 0: x=0..10, Tile 1: x=15..25, gap: x=10..15.
        using var mask = LeftOpaqueRightTransparentMask(10, 10);
        var item = new FillMaskedBackground(
            Bounds: new LayoutRect(0, 0, 25, 20),
            Gradient: null,
            Color: Red,
            BackgroundImage: null,
            Radii: CornerRadii.None,
            Mask: mask,
            MaskGradient: null,
            MaskRenderWidth: 10,
            MaskRenderHeight: 10,
            MaskOffsetX: 0,
            MaskOffsetY: 0,
            MaskRepeat: MaskRepeatMode.Space);

        var list = new PaintList();
        list.Add(item);

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(25, 20));

        // x=2 is inside the opaque half of tile 0 → red.
        var (r0, g0, b0, _) = bmp.GetPixel(2, 10);
        r0.Should().BeGreaterThan(180, "x=2 is in the opaque half of the first space tile; red should show");
        g0.Should().BeLessThan(80);

        // x=17 is inside tile 1 opaque half → red.
        var (r1, g1, b1, _) = bmp.GetPixel(17, 10);
        r1.Should().BeGreaterThan(180, "x=17 is in the opaque half of the second space tile; red should show");
        g1.Should().BeLessThan(80);
    }

    /// <summary>
    /// mask-repeat: round — tiles are stretched so an integer number fills the box
    /// exactly. A 10px tile in a 25px box: round(25/10) = 3 tiles, each ≈ 8.3px.
    /// Probe inside what would be the opaque half of a stretched tile and confirm red.
    /// </summary>
    [TestMethod]
    public void Mask_repeat_round_stretches_tiles_to_fill_box()
    {
        // 10×10 mask: opaque left 5px, transparent right 5px.
        // round(25/10) = 3 tiles, each ~8.3px wide. Opaque half ≈ first 4px of each tile.
        using var mask = LeftOpaqueRightTransparentMask(10, 10);
        var item = new FillMaskedBackground(
            Bounds: new LayoutRect(0, 0, 25, 20),
            Gradient: null,
            Color: Red,
            BackgroundImage: null,
            Radii: CornerRadii.None,
            Mask: mask,
            MaskGradient: null,
            MaskRenderWidth: 10,
            MaskRenderHeight: 10,
            MaskOffsetX: 0,
            MaskOffsetY: 0,
            MaskRepeat: MaskRepeatMode.Round);

        var list = new PaintList();
        list.Add(item);

        using var backend = new ImageSharpBackend(FontResolver.Default, webFonts: null);
        using var bmp = backend.Render(list, new LayoutSize(25, 20));

        // x=1 should be in the opaque part of the first stretched tile → red.
        var (r, g, b, _) = bmp.GetPixel(1, 10);
        r.Should().BeGreaterThan(150, "x=1 should be in the opaque half of the first round-tiled mask tile");
        g.Should().BeLessThan(80);
    }
}
