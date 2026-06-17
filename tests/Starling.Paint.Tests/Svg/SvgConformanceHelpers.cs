// SPDX-License-Identifier: Apache-2.0
using Starling.Css.Values;
using Starling.Common.Image;
using Starling.Paint.Svg;

namespace Starling.Paint.Tests.Svg;

/// <summary>
/// Shared raster-inspection helpers for the SVG conformance suite. The suite is
/// modelled on the resvg test suite (https://github.com/linebender/resvg-test-suite),
/// which organises static-SVG rendering tests into the SVG 1.1 chapters:
/// structure, shapes, paint-servers, painting, masking, text, filters. Each
/// chapter has its own test class here. Cases author their own SVG input and
/// assert on the rasterized pixels — Starling decodes SVG as a static image, so
/// the resvg "SVG in, raster out" model maps directly.
///
/// Tests for features Starling implements use <c>[SpecFact]</c>. Tests for
/// documented gaps (text, filters, clipping, masking, markers, …) carry a real
/// assertion body but use <c>[PendingFact]</c>, so they stay green today and
/// flip on by swapping the attribute once the feature lands.
/// </summary>
internal static class SvgRaster
{
    public const string Spec11Url = "https://www.w3.org/TR/SVG11/";

    public static DecodedImage Decode(string svg, CssColor? currentColor = null)
        => SvgImageDecoder.DecodeText(svg, currentColor);

    /// <summary>RGBA of the pixel at (x, y).</summary>
    public static (byte R, byte G, byte B, byte A) At(DecodedImage img, int x, int y)
    {
        var px = img.Pixels.Span;
        int i = (y * img.Width + x) * 4;
        return (px[i], px[i + 1], px[i + 2], px[i + 3]);
    }

    /// <summary>True when any pixel has a non-zero alpha.</summary>
    public static bool AnyOpaque(DecodedImage img)
    {
        var px = img.Pixels.Span;
        for (int i = 3; i < px.Length; i += 4)
        {
            if (px[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Count pixels matching <paramref name="pred"/>.</summary>
    public static int Count(DecodedImage img, Func<(byte R, byte G, byte B, byte A), bool> pred)
    {
        int n = 0;
        for (int y = 0; y < img.Height; y++)
        {
            for (int x = 0; x < img.Width; x++)
            {
                if (pred(At(img, x, y)))
                {
                    n++;
                }
            }
        }

        return n;
    }

    /// <summary>Count opaque pixels in a single row (used for dash/stroke coverage).</summary>
    public static int OpaqueInRow(DecodedImage img, int y)
    {
        int n = 0;
        for (int x = 0; x < img.Width; x++)
        {
            if (At(img, x, y).A > 40)
            {
                n++;
            }
        }

        return n;
    }

    public static CssColor Rgb(byte r, byte g, byte b, byte a = 255)
        => CssColor.FromSrgb(r / 255.0, g / 255.0, b / 255.0, a / 255.0);

    // Loose colour predicates, tolerant of anti-aliasing.
    public static bool IsRed((byte R, byte G, byte B, byte A) p) => p.A > 150 && p.R > 150 && p.G < 90 && p.B < 90;
    public static bool IsGreen((byte R, byte G, byte B, byte A) p) => p.A > 150 && p.G > 120 && p.R < 100 && p.B < 100;
    public static bool IsBlue((byte R, byte G, byte B, byte A) p) => p.A > 150 && p.B > 150 && p.R < 90 && p.G < 90;
}
