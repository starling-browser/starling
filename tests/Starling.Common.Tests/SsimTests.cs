using AwesomeAssertions;
using Starling.Common.Image;
namespace Starling.Common.Tests;

[TestClass]
public class SsimTests
{
    [TestMethod]
    public void Identical_images_score_one()
    {
        var (a, b) = MakePair(64, 64, fill: 128);
        Ssim.ComputeRgba(a, b, 64, 64).Should().BeApproximately(1.0, 1e-9);
    }

    [TestMethod]
    public void Wholly_different_images_score_below_threshold()
    {
        var (black, _) = MakePair(32, 32, fill: 0);
        var (white, _) = MakePair(32, 32, fill: 255);
        var score = Ssim.ComputeRgba(black, white, 32, 32);
        score.Should().BeLessThan(0.05);
    }

    [TestMethod]
    public void Mostly_matching_images_score_above_threshold()
    {
        // Two 128x128 images, identical except for one differing 4x4 patch
        // tucked into a single 8x8 window. With 256 windows total and one
        // dropping in score, the global mean SSIM stays comfortably above
        // 0.99.
        const int w = 128;
        const int h = 128;
        var a = new byte[w * h * 4];
        var b = new byte[w * h * 4];
        for (var i = 0; i < a.Length; i += 4)
        {
            a[i] = 128; a[i + 1] = 128; a[i + 2] = 128; a[i + 3] = 255;
            b[i] = 128; b[i + 1] = 128; b[i + 2] = 128; b[i + 3] = 255;
        }
        for (var y = 0; y < 4; y++)
        {
            for (var x = 0; x < 4; x++)
            {
                var i = (y * w + x) * 4;
                b[i] = 255; b[i + 1] = 255; b[i + 2] = 255;
            }
        }
        Ssim.ComputeRgba(a, b, w, h).Should().BeGreaterThan(0.99);
    }

    [TestMethod]
    public void Alpha_differences_are_ignored()
    {
        const int w = 16;
        const int h = 16;
        var (a, b) = MakePair(w, h, fill: 200);
        // Vary alpha aggressively; SSIM should still be perfect.
        for (var i = 3; i < b.Length; i += 4)
        {
            b[i] = (byte)(i & 0xFF);
        }

        Ssim.ComputeRgba(a, b, w, h).Should().BeApproximately(1.0, 1e-9);
    }

    [TestMethod]
    public void Throws_when_sizes_mismatch()
    {
        var (a, _) = MakePair(8, 8, fill: 0);
        var b = new byte[8 * 8 * 4 - 4];
        var act = () => Ssim.ComputeRgba(a, b, 8, 8);
        act.Should().Throw<ArgumentException>();
    }

    private static (byte[] A, byte[] B) MakePair(int w, int h, byte fill)
    {
        var a = new byte[w * h * 4];
        var b = new byte[w * h * 4];
        for (var i = 0; i < a.Length; i += 4)
        {
            a[i] = fill; a[i + 1] = fill; a[i + 2] = fill; a[i + 3] = 255;
            b[i] = fill; b[i + 1] = fill; b[i + 2] = fill; b[i + 3] = 255;
        }
        return (a, b);
    }
}
