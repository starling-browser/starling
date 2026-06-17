using BenchmarkDotNet.Attributes;
using Starling.Common.Image;

namespace Starling.Bench;

// SSIM is the gate behind the M2 "rendered PNG matches expected within
// SSIM 0.99" exit and the snapshot/live golden tests. The pure-managed
// implementation in `Starling.Common.Image.Ssim` is on every CI run that
// compares images, so its cost on a viewport-sized buffer matters.
[MemoryDiagnoser]
public class SsimBench
{
    private const int Width = 1024;
    private const int Height = 768;

    private byte[] _a = null!;
    private byte[] _identical = null!;
    private byte[] _noisy = null!;

    [GlobalSetup]
    public void Setup()
    {
        var size = Width * Height * 4;
        _a = new byte[size];
        _identical = new byte[size];
        _noisy = new byte[size];
        var rng = new Random(0xC0DE);
        rng.NextBytes(_a);
        Array.Copy(_a, _identical, size);
        Array.Copy(_a, _noisy, size);
        // Inject 1% noise so the SSIM scoring path runs meaningfully.
        for (var i = 0; i < size; i += 100)
        {
            _noisy[i] = (byte)(_a[i] ^ 0x40);
        }
    }

    [Benchmark]
    public double Identical_Viewport() => Ssim.ComputeRgba(_a, _identical, Width, Height);

    [Benchmark(Baseline = true)]
    public double Noisy_Viewport() => Ssim.ComputeRgba(_a, _noisy, Width, Height);
}
