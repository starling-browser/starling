using System.Diagnostics;

namespace Starling.Bench.Replay;

/// <summary>
/// One phase's timing and allocation distribution over the measured frames. All
/// times are milliseconds. Built from raw per-frame <see cref="Stopwatch"/> ticks
/// so the hot loop never touches floating point, sorting, or LINQ.
/// </summary>
public readonly record struct PhaseStats(
    double MeanMs,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    double MaxMs,
    int DroppedOver16_67ms,
    int DroppedOver8_33ms,
    double MeanAllocBytes,
    long MaxAllocBytes)
{
    /// <summary>
    /// Build the distribution from one phase's per-frame samples.
    /// <paramref name="ticks"/> holds <see cref="Stopwatch"/> ticks per frame;
    /// <paramref name="allocBytes"/> holds the allocation delta per frame. The
    /// two arrays are parallel and the same length.
    /// </summary>
    public static PhaseStats From(long[] ticks, long[] allocBytes)
    {
        ArgumentNullException.ThrowIfNull(ticks);
        ArgumentNullException.ThrowIfNull(allocBytes);
        if (ticks.Length == 0)
        {
            return default;
        }

        var n = ticks.Length;
        var ms = new double[n];
        double sum = 0;
        var dropped60 = 0;
        var dropped120 = 0;
        for (var i = 0; i < n; i++)
        {
            var v = ticks[i] * 1000.0 / Stopwatch.Frequency;
            ms[i] = v;
            sum += v;
            if (v > 16.666_67)
            {
                dropped60++;
            }

            if (v > 8.333_33)
            {
                dropped120++;
            }
        }
        Array.Sort(ms);

        double allocSum = 0;
        long allocMax = 0;
        for (var i = 0; i < n; i++)
        {
            allocSum += allocBytes[i];
            if (allocBytes[i] > allocMax)
            {
                allocMax = allocBytes[i];
            }
        }

        return new PhaseStats(
            MeanMs: sum / n,
            P50Ms: Percentile(ms, 50),
            P95Ms: Percentile(ms, 95),
            P99Ms: Percentile(ms, 99),
            MaxMs: ms[n - 1],
            DroppedOver16_67ms: dropped60,
            DroppedOver8_33ms: dropped120,
            MeanAllocBytes: allocSum / n,
            MaxAllocBytes: allocMax);
    }

    // Linear-interpolated percentile over an ascending-sorted array.
    private static double Percentile(double[] sortedAsc, double p)
    {
        var n = sortedAsc.Length;
        if (n == 1)
        {
            return sortedAsc[0];
        }

        var rank = p / 100.0 * (n - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi)
        {
            return sortedAsc[lo];
        }

        return sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * (rank - lo);
    }
}
