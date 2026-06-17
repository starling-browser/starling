using Starling.Common.Diagnostics;

namespace Starling.Telemetry.Daemon.Analysis;

/// <summary>
/// Time-indexed view over the process.* resource gauges the browser samples
/// (CPU utilization, working set, managed/GC heap, thread + GC counts). Built
/// from a metric snapshot, it answers "what was CPU/RAM during this span's
/// window?" by averaging the samples inside [start,end] or, failing that, the
/// nearest sample within a tolerance. This is the join that ties a slow span to
/// local-machine resource pressure.
/// </summary>
internal sealed class ResourceSeries
{
    private const long ToleranceMs = 2000; // ~4 samples at the 500ms default cadence

    private readonly (long Ms, double V)[] _cpu;
    private readonly (long Ms, double V)[] _workingSet;
    private readonly (long Ms, double V)[] _managed;
    private readonly (long Ms, double V)[] _heap;
    private readonly (long Ms, double V)[] _threads;
    private readonly (long Ms, double V)[] _gc0;
    private readonly (long Ms, double V)[] _gc1;
    private readonly (long Ms, double V)[] _gc2;
    private readonly int _cores;

    public ResourceSeries(MeterRecord[] metrics)
    {
        _cpu = Pick(metrics, RenderMetrics.ProcessCpuUtilization);
        _workingSet = Pick(metrics, RenderMetrics.ProcessMemoryWorkingSet);
        _managed = Pick(metrics, RenderMetrics.ProcessMemoryManaged);
        _heap = Pick(metrics, RenderMetrics.ProcessHeapBytes);
        _threads = Pick(metrics, RenderMetrics.ProcessThreads);
        _gc0 = Pick(metrics, RenderMetrics.ProcessGcGen0);
        _gc1 = Pick(metrics, RenderMetrics.ProcessGcGen1);
        _gc2 = Pick(metrics, RenderMetrics.ProcessGcGen2);
        var cores = LastValue(Pick(metrics, RenderMetrics.ProcessCpuCores));
        _cores = double.IsNaN(cores) ? Environment.ProcessorCount : (int)cores;
    }

    public bool HasResourceData => _cpu.Length > 0 || _workingSet.Length > 0;

    public double CpuAt(long unixMs) => Nearest(_cpu, unixMs);
    public double WorkingSetMbAt(long unixMs) => ToMb(Nearest(_workingSet, unixMs));

    public double AverageCpu(long startMs, long endMs) => AvgInWindow(_cpu, startMs, endMs);
    public double AverageWorkingSetMb(long startMs, long endMs)
        => ToMb(AvgInWindow(_workingSet, startMs, endMs));

    public ResourceSnapshot Latest()
    {
        var atMs = _cpu.Length > 0 ? _cpu[^1].Ms
                 : _workingSet.Length > 0 ? _workingSet[^1].Ms
                 : (long?)null;
        return new ResourceSnapshot(
            atMs, _cores,
            Round(LastValue(_cpu)),
            Round(ToMb(LastValue(_workingSet))),
            Round(ToMb(LastValue(_managed))),
            Round(ToMb(LastValue(_heap))),
            (int)NanToZero(LastValue(_threads)),
            (long)NanToZero(LastValue(_gc0)),
            (long)NanToZero(LastValue(_gc1)),
            (long)NanToZero(LastValue(_gc2)));
    }

    public ResourceReport Report(DateTime cutoff)
    {
        var cutoffMs = TelemetryAnalyzer.Ms(cutoff);
        var cpu = _cpu.Where(s => s.Ms >= cutoffMs).Select(s => s.V).ToArray();
        var ws = _workingSet.Where(s => s.Ms >= cutoffMs).Select(s => ToMb(s.V)).ToArray();
        return new ResourceReport(
            Latest(),
            Math.Max(cpu.Length, ws.Length),
            cpu.Length > 0 ? Round(cpu.Average()) : double.NaN,
            cpu.Length > 0 ? Round(cpu.Max()) : double.NaN,
            ws.Length > 0 ? Round(ws.Average()) : double.NaN,
            ws.Length > 0 ? Round(ws.Max()) : double.NaN);
    }

    // ── internals ─────────────────────────────────────────────────────────────

    private static (long, double)[] Pick(MeterRecord[] metrics, string instrument)
        => metrics
            .Where(r => string.Equals(r.InstrumentName, instrument, StringComparison.Ordinal))
            .Select(r => (TelemetryAnalyzer.Ms(r.TimestampUtc), r.Value))
            .OrderBy(t => t.Item1)
            .ToArray();

    private static double LastValue((long Ms, double V)[] series)
        => series.Length == 0 ? double.NaN : series[^1].V;

    private static double AvgInWindow((long Ms, double V)[] series, long startMs, long endMs)
    {
        if (series.Length == 0)
        {
            return double.NaN;
        }

        double sum = 0;
        var n = 0;
        // Linear scan is fine: snapshots are bounded at 2000 entries.
        foreach (var (ms, v) in series)
        {
            if (ms < startMs)
            {
                continue;
            }

            if (ms > endMs)
            {
                break;
            }

            sum += v;
            n++;
        }
        if (n > 0)
        {
            return sum / n;
        }

        // No sample inside the window (common for sub-frame spans): fall back to
        // the nearest sample to the window midpoint within tolerance.
        return Nearest(series, startMs + (endMs - startMs) / 2);
    }

    private static double Nearest((long Ms, double V)[] series, long t)
    {
        if (series.Length == 0)
        {
            return double.NaN;
        }

        var lo = 0;
        var hi = series.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (series[mid].Ms < t)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        var best = series[lo];
        if (lo > 0 && Math.Abs(series[lo - 1].Ms - t) < Math.Abs(best.Ms - t))
        {
            best = series[lo - 1];
        }

        return Math.Abs(best.Ms - t) <= ToleranceMs ? best.V : double.NaN;
    }

    private static double ToMb(double bytes) => double.IsNaN(bytes) ? double.NaN : bytes / (1024.0 * 1024.0);
    private static double NanToZero(double v) => double.IsNaN(v) ? 0 : v;
    private static double Round(double v) => double.IsNaN(v) ? double.NaN : Math.Round(v, 2);
}
