using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Common.Diagnostics;

namespace Starling.Bench;

internal static class GitHubStyleSmoke
{
    public static int Run()
    {
        var bench = new GitHubStyleBench();
        bench.Setup();

        Measure("ParseCss_GitHubHome", bench.ParseCss_GitHubHome);
        Measure("BuildStyleEngine_GitHubHome", bench.BuildStyleEngine_GitHubHome);

        using var cascadeMetrics = new MetricRecorder();
        Measure("PrecomputeTree_GitHubHome", () => bench.PrecomputeTree_GitHubHome(NullLoggerFactory.Instance));
        cascadeMetrics.Print("css.");

        using var layoutSpans = new SpanRecorder();
        Measure("LayoutDocument_GitHubHome", () => bench.LayoutDocument_GitHubHome(NullLoggerFactory.Instance));
        layoutSpans.Print();

        Measure("Render_GitHubHome_HtmlToDisplayList", bench.Render_GitHubHome_HtmlToDisplayList);
        Measure("Render_GitHubHome_GpuTextureCompositor", bench.Render_GitHubHome_GpuTextureCompositor);
        return 0;
    }

    private static void Measure<T>(string name, Func<T> run)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();
        var sw = Stopwatch.StartNew();
        var result = run();
        sw.Stop();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Console.WriteLine($"{name}: {sw.ElapsedMilliseconds} ms, {allocated:N0} B, result={result}");
    }

    /// <summary>
    /// Listens on <see cref="StarlingTelemetry.Meter"/> and accumulates counter
    /// deltas emitted during the enclosed operation.
    /// </summary>
    private sealed class MetricRecorder : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ConcurrentDictionary<string, double> _counters = new(StringComparer.Ordinal);

        public MetricRecorder()
        {
            _listener.InstrumentPublished = (inst, lst) =>
            {
                if (inst.Meter.Name == StarlingTelemetry.SourceName)
                {
                    lst.EnableMeasurementEvents(inst);
                }
            };
            _listener.SetMeasurementEventCallback<double>((inst, m, _, _) => Add(inst.Name, m));
            _listener.SetMeasurementEventCallback<long>((inst, m, _, _) => Add(inst.Name, (double)m));
            _listener.Start();
        }

        private void Add(string name, double value)
            => _counters.AddOrUpdate(name, value, (_, current) => current + value);

        public void Print(string prefix)
        {
            foreach (var pair in _counters
                         .Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal))
                         .OrderBy(pair => pair.Key))
            {
                Console.WriteLine($"  counter {pair.Key}: {pair.Value:N0}");
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    /// <summary>
    /// Records span elapsed times from <see cref="StarlingTelemetry.Source"/>.
    /// </summary>
    private sealed class SpanRecorder : IDisposable
    {
        private readonly ActivityListener _listener;
        private readonly Dictionary<string, long> _spans = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Stopwatch> _active = new(StringComparer.Ordinal);

        public SpanRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = src => src.Name == StarlingTelemetry.SourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStarted = a => { lock (_active) { _active[a.Id ?? a.OperationName] = Stopwatch.StartNew(); } },
                ActivityStopped = a =>
                {
                    Stopwatch? sw;
                    lock (_active)
                    {
                        if (!_active.Remove(a.Id ?? a.OperationName, out sw))
                        {
                            return;
                        }
                    }
                    sw.Stop();
                    lock (_spans)
                    {
                        _spans.TryGetValue(a.OperationName, out var cur);
                        _spans[a.OperationName] = cur + sw.ElapsedMilliseconds;
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public void Print()
        {
            foreach (var pair in _spans.OrderByDescending(pair => pair.Value))
            {
                Console.WriteLine($"  span {pair.Key}: {pair.Value} ms");
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
