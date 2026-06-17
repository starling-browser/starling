namespace Starling.Common.Image;

/// <summary>
/// Window-based image similarity score (the Structural Similarity Index, or
/// SSIM). Runs in pure managed code. It takes raw RGBA (or RGB) byte spans, so
/// it does not depend on any image library. Callers pack pixels into a byte
/// buffer first.
/// </summary>
/// <remarks>
/// <para>
/// Implements the original Wang, Bovik, Sheikh, and Simoncelli formula
/// (Image Quality Assessment: From Error Visibility to Structural Similarity,
/// IEEE TIP 13(4), 2004). For RGBA input the score is computed per color
/// channel (RGB only, alpha is ignored) over non-overlapping square windows.
/// The per-window scores are then averaged. The paper uses a window side of 8.
/// The stabilizing constants are C1 = (K1·L)² and C2 = (K2·L)², where
/// K1 = 0.01, K2 = 0.03, and L = 255.
/// </para>
/// <para>
/// This version is kept simple on purpose. No Gaussian weighting, no
/// multi-scale pyramid, no per-pixel sliding window. It still tracks the
/// reference closely enough to catch any visible regression in the layout and
/// paint pipeline, and it fits in one file you can read top to bottom. Tighten
/// it if a scenario needs it. This file is the only home for the score so the
/// behavior stays the same across all tests.
/// </para>
/// </remarks>
public static class Ssim
{
    /// <summary>Window side length, in pixels. 8 is the canonical value.</summary>
    private const int WindowSize = 8;

    /// <summary>K1 stabilizing constant for the luminance term.</summary>
    private const double K1 = 0.01;

    /// <summary>K2 stabilizing constant for the contrast and structure term.</summary>
    private const double K2 = 0.03;

    /// <summary>Maximum pixel value (8-bit channels).</summary>
    private const double L = 255.0;

    private const double C1 = K1 * L * (K1 * L);
    private const double C2 = K2 * L * (K2 * L);

    /// <summary>
    /// Computes the mean score across all non-overlapping windows of two
    /// equally sized RGBA buffers. Buffers must be row-major, 4 bytes per pixel.
    /// Channel order can be anything as long as both buffers use the same one,
    /// since only matching channels are compared. The alpha channel is ignored.
    /// </summary>
    /// <returns>
    /// A score in [-1, 1]. 1.0 means the images are pixel-identical. Two
    /// all-zero images score 1.0 by definition. If either dimension is smaller
    /// than <see cref="WindowSize"/>, the whole image is scored as a single
    /// window.
    /// </returns>
    public static double ComputeRgba(
        ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width/height must be positive.");
        }

        var expected = checked(width * height * 4);
        if (a.Length != expected)
        {
            throw new ArgumentException($"Buffer A length {a.Length} != expected {expected}.", nameof(a));
        }

        if (b.Length != expected)
        {
            throw new ArgumentException($"Buffer B length {b.Length} != expected {expected}.", nameof(b));
        }

        var window = Math.Min(WindowSize, Math.Min(width, height));

        var winsX = width / window;
        var winsY = height / window;
        if (winsX == 0 || winsY == 0)
        {
            // Image smaller than window in some dimension; score the
            // entire image as a single window.
            return WindowSsim(a, b, width, 0, 0, width, height);
        }

        double total = 0.0;
        int count = 0;
        for (var wy = 0; wy < winsY; wy++)
        {
            var y = wy * window;
            for (var wx = 0; wx < winsX; wx++)
            {
                var x = wx * window;
                total += WindowSsim(a, b, width, x, y, window, window);
                count++;
            }
        }
        return count == 0 ? 1.0 : total / count;
    }

    private static double WindowSsim(
        ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int stridePixels,
        int x0, int y0, int w, int h)
    {
        // Per-channel SSIM, averaged. (R, G, B; alpha skipped.)
        double sum = 0.0;
        for (var c = 0; c < 3; c++)
        {
            sum += ChannelSsim(a, b, stridePixels, x0, y0, w, h, c);
        }

        return sum / 3.0;
    }

    private static double ChannelSsim(
        ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int stridePixels,
        int x0, int y0, int w, int h, int channel)
    {
        var n = w * h;
        if (n <= 0)
        {
            return 1.0;
        }

        // Pass 1: means.
        double sumA = 0, sumB = 0;
        for (var dy = 0; dy < h; dy++)
        {
            var rowOffset = (y0 + dy) * stridePixels * 4;
            for (var dx = 0; dx < w; dx++)
            {
                var pi = rowOffset + (x0 + dx) * 4 + channel;
                sumA += a[pi];
                sumB += b[pi];
            }
        }
        var meanA = sumA / n;
        var meanB = sumB / n;

        // Pass 2: variances and covariance.
        double varA = 0, varB = 0, covAB = 0;
        for (var dy = 0; dy < h; dy++)
        {
            var rowOffset = (y0 + dy) * stridePixels * 4;
            for (var dx = 0; dx < w; dx++)
            {
                var pi = rowOffset + (x0 + dx) * 4 + channel;
                double da = a[pi] - meanA;
                double db = b[pi] - meanB;
                varA += da * da;
                varB += db * db;
                covAB += da * db;
            }
        }
        // Population (n) — biased estimator, as in the reference paper.
        varA /= n;
        varB /= n;
        covAB /= n;

        var numerator = (2 * meanA * meanB + C1) * (2 * covAB + C2);
        var denominator = (meanA * meanA + meanB * meanB + C1) * (varA + varB + C2);
        if (denominator == 0.0)
        {
            return 1.0;
        }

        return numerator / denominator;
    }
}
