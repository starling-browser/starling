using Starling.Common.Diagnostics;
using Starling.Telemetry.Daemon.Ingestion;

namespace Starling.Telemetry.Daemon.Analysis;

// ── Report DTOs (serialized to JSON for REST + MCP) -----

internal sealed record SpanAggregate(
    string Source, string Name, int Count, int Errors,
    double TotalMs, double AvgMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs,
    double AvgCpuDuring, double MaxCpuDuring, double AvgWorkingSetMb);

internal sealed record FrameSample(long UnixMs, double FrameMs, double CpuDuring, double WorkingSetMb);

internal sealed record FrameReport(
    string Signal, double BudgetMs, int Count,
    double AvgMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs,
    int OverBudget, double OverBudgetRate, double EstimatedFps,
    IReadOnlyList<FrameSample> Slowest);

internal sealed record ResourceSnapshot(
    long? AtUnixMs, int Cores, double CpuUtilization,
    double WorkingSetMb, double ManagedHeapMb, double GcHeapMb,
    int Threads, long Gc0, long Gc1, long Gc2);

internal sealed record ResourceReport(
    ResourceSnapshot Latest, int SampleCount,
    double AvgCpu, double MaxCpu, double AvgWorkingSetMb, double MaxWorkingSetMb);

internal sealed record SpanCorrelation(
    string Name, int Count, double AvgDurationMs, double MaxDurationMs,
    double AvgCpuDuring, double MaxCpuDuring, double AvgWorkingSetMb, double MaxWorkingSetMb,
    string Interpretation);

internal sealed record OverviewReport(
    int WindowSeconds,
    long SpansIngested, long MetricsIngested, long LogsIngested,
    DateTime? FirstReceivedUtc, DateTime? LastReceivedUtc,
    FrameReport Frames, ResourceSnapshot Resources,
    IReadOnlyList<SpanAggregate> TopOffenders);

/// <summary>
/// Reads the daemon's ring buffers on demand and answers the two questions the
/// request asks: (1) which actions/spans are causing the lag — via per-span
/// duration aggregates and a frame-time report with budget overruns; and (2)
/// how those spans correlate with local CPU and memory — by joining each span's
/// time window to the process.* resource gauges sampled in the browser.
/// </summary>
internal sealed class TelemetryAnalyzer
{
    // The frame budget the overrun rate is measured against (60 fps). Overridable
    // via STARLING_DAEMON_FRAME_BUDGET_MS for higher-refresh targets.
    private readonly double _budgetMs;
    private readonly TelemetryIngestStore _store;

    public TelemetryAnalyzer(TelemetryIngestStore store)
    {
        _store = store;
        _budgetMs = double.TryParse(
            Environment.GetEnvironmentVariable("STARLING_DAEMON_FRAME_BUDGET_MS"),
            System.Globalization.CultureInfo.InvariantCulture, out var b) && b > 0 ? b : 1000.0 / 60; // 60fps
    }

    public OverviewReport Overview(TimeSpan window, int topLimit = 12)
    {
        var spans = _store.Activities.Snapshot();
        var metrics = _store.Metrics.Snapshot();
        var series = new ResourceSeries(metrics);
        var cutoff = DateTime.UtcNow - window;

        return new OverviewReport(
            (int)window.TotalSeconds,
            _store.SpansIngested, _store.MetricsIngested, _store.LogsIngested,
            _store.FirstReceivedUtc, _store.LastReceivedUtc,
            BuildFrames(spans, metrics, series, cutoff),
            series.Latest(),
            BuildTopOffenders(spans, series, cutoff, topLimit));
    }

    public IReadOnlyList<SpanAggregate> TopOffenders(TimeSpan window, int limit)
    {
        var spans = _store.Activities.Snapshot();
        var series = new ResourceSeries(_store.Metrics.Snapshot());
        return BuildTopOffenders(spans, series, DateTime.UtcNow - window, limit);
    }

    public FrameReport Frames(TimeSpan window)
    {
        var spans = _store.Activities.Snapshot();
        var metrics = _store.Metrics.Snapshot();
        return BuildFrames(spans, metrics, new ResourceSeries(metrics), DateTime.UtcNow - window);
    }

    public ResourceReport Resources(TimeSpan window)
    {
        var series = new ResourceSeries(_store.Metrics.Snapshot());
        return series.Report(DateTime.UtcNow - window);
    }

    public SpanCorrelation Correlate(string spanName, TimeSpan window)
    {
        var cutoff = DateTime.UtcNow - window;
        var series = new ResourceSeries(_store.Metrics.Snapshot());
        var matches = _store.Activities.Snapshot()
            .Where(s => s.StartUtc >= cutoff &&
                        string.Equals(s.OperationName, spanName, StringComparison.Ordinal))
            .ToArray();

        if (matches.Length == 0)
        {
            return new SpanCorrelation(spanName, 0, 0, 0, 0, 0, 0, 0,
                "No spans with this name in the window.");
        }

        var durations = matches.Select(m => m.Duration.TotalMilliseconds).ToArray();
        var cpus = new List<double>();
        var wss = new List<double>();
        foreach (var m in matches)
        {
            var startMs = Ms(m.StartUtc);
            var endMs = startMs + (long)m.Duration.TotalMilliseconds;
            var cpu = series.AverageCpu(startMs, endMs);
            var ws = series.AverageWorkingSetMb(startMs, endMs);
            if (!double.IsNaN(cpu))
            {
                cpus.Add(cpu);
            }

            if (!double.IsNaN(ws))
            {
                wss.Add(ws);
            }
        }

        var avgCpu = cpus.Count > 0 ? cpus.Average() : double.NaN;
        var maxCpu = cpus.Count > 0 ? cpus.Max() : double.NaN;
        return new SpanCorrelation(
            spanName, matches.Length,
            durations.Average(), durations.Max(),
            avgCpu, maxCpu,
            wss.Count > 0 ? wss.Average() : double.NaN,
            wss.Count > 0 ? wss.Max() : double.NaN,
            Interpret(durations.Average(), avgCpu));
    }

    // ── builders ────────────────────────────────────────────────────────────

    private List<SpanAggregate> BuildTopOffenders(
        ActivityRecord[] spans, ResourceSeries series, DateTime cutoff, int limit)
    {
        var groups = new Dictionary<(string, string), List<ActivityRecord>>();
        foreach (var s in spans)
        {
            if (s.StartUtc < cutoff)
            {
                continue;
            }

            var key = (s.Source, s.OperationName);
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
            }

            list.Add(s);
        }

        var result = new List<SpanAggregate>(groups.Count);
        foreach (var ((source, name), list) in groups)
        {
            var durations = list.Select(s => s.Duration.TotalMilliseconds).OrderBy(d => d).ToArray();
            var cpus = new List<double>();
            var wss = new List<double>();
            foreach (var s in list)
            {
                var startMs = Ms(s.StartUtc);
                var endMs = startMs + (long)s.Duration.TotalMilliseconds;
                var c = series.AverageCpu(startMs, endMs);
                var w = series.AverageWorkingSetMb(startMs, endMs);
                if (!double.IsNaN(c))
                {
                    cpus.Add(c);
                }

                if (!double.IsNaN(w))
                {
                    wss.Add(w);
                }
            }

            result.Add(new SpanAggregate(
                source, name, durations.Length,
                list.Count(s => s.Status == System.Diagnostics.ActivityStatusCode.Error),
                durations.Sum(), durations.Average(),
                Percentile(durations, 0.50), Percentile(durations, 0.95), Percentile(durations, 0.99),
                durations[^1],
                cpus.Count > 0 ? cpus.Average() : double.NaN,
                cpus.Count > 0 ? cpus.Max() : double.NaN,
                wss.Count > 0 ? wss.Average() : double.NaN));
        }

        // Rank by total time spent — the spans eating the most wall-clock are the
        // first place to look for the lag.
        return result.OrderByDescending(a => a.TotalMs).Take(limit).ToList();
    }

    private FrameReport BuildFrames(
        ActivityRecord[] spans, MeterRecord[] metrics, ResourceSeries series, DateTime cutoff)
    {
        // Prefer the explicit frame-time gauge; fall back to gui.frame/shell.frame
        // span durations if the host didn't emit the gauge.
        var signal = "gui.frame.time_ms";
        var samples = metrics
            .Where(m => m.TimestampUtc >= cutoff &&
                        string.Equals(m.InstrumentName, RenderMetrics.FrameTimeMs, StringComparison.Ordinal))
            .Select(m => (ms: Ms(m.TimestampUtc), val: m.Value))
            .ToList();

        if (samples.Count == 0)
        {
            signal = "span:gui.frame";
            samples = spans
                .Where(s => s.StartUtc >= cutoff &&
                            (s.OperationName is "gui.frame" or "shell.frame" or "shell.present.frame"))
                .Select(s => (ms: Ms(s.StartUtc), val: s.Duration.TotalMilliseconds))
                .ToList();
        }

        if (samples.Count == 0)
        {
            return new FrameReport(signal, _budgetMs, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);
        }

        var values = samples.Select(s => s.val).OrderBy(v => v).ToArray();
        var over = samples.Count(s => s.val > _budgetMs);
        var avg = values.Average();
        var slowest = samples
            .OrderByDescending(s => s.val)
            .Take(8)
            .Select(s => new FrameSample(
                s.ms, Round(s.val),
                Round(series.CpuAt(s.ms)),
                Round(series.WorkingSetMbAt(s.ms))))
            .ToList();

        return new FrameReport(
            signal, _budgetMs, samples.Count,
            Round(avg), Round(Percentile(values, 0.50)), Round(Percentile(values, 0.95)),
            Round(Percentile(values, 0.99)), Round(values[^1]),
            over, Round(samples.Count > 0 ? (double)over / samples.Count : 0),
            Round(avg > 0 ? Math.Min(1000.0 / avg, 1000.0) : 0),
            slowest);
    }

    private static string Interpret(double avgMs, double avgCpu)
    {
        if (double.IsNaN(avgCpu))
        {
            return $"avg {avgMs:F1}ms; no overlapping CPU samples to correlate.";
        }

        var cpuPct = avgCpu * 100;
        if (avgMs > 16 && cpuPct > 70)
        {
            return $"avg {avgMs:F1}ms while CPU ~{cpuPct:F0}% — CPU-bound; this span is a prime lag suspect.";
        }

        if (avgMs > 16 && cpuPct <= 70)
        {
            return $"avg {avgMs:F1}ms but CPU only ~{cpuPct:F0}% — likely blocked/waiting (GPU/IO/lock), not CPU work.";
        }

        return $"avg {avgMs:F1}ms, CPU ~{cpuPct:F0}% during — within budget.";
    }

    // ── numeric helpers ──────────────────────────────────────────────────────

    internal static double Percentile(double[] sortedAsc, double q)
    {
        if (sortedAsc.Length == 0)
        {
            return 0;
        }

        if (sortedAsc.Length == 1)
        {
            return sortedAsc[0];
        }

        var rank = q * (sortedAsc.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        if (lo == hi)
        {
            return sortedAsc[lo];
        }

        var frac = rank - lo;
        return sortedAsc[lo] + (sortedAsc[hi] - sortedAsc[lo]) * frac;
    }

    private static double Round(double v) => double.IsNaN(v) ? double.NaN : Math.Round(v, 2);

    internal static long Ms(DateTime utc)
        => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
