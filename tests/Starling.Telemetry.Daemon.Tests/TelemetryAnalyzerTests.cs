using System.Diagnostics;
using AwesomeAssertions;
using Starling.Common.Diagnostics;
using Starling.Telemetry.Daemon.Analysis;
using Starling.Telemetry.Daemon.Ingestion;

namespace Starling.Telemetry.Daemon.Tests;

[TestClass]
public sealed class TelemetryAnalyzerTests
{
    private static readonly TimeSpan Wide = TimeSpan.FromSeconds(3600);

    [TestMethod]
    public void TopOffenders_RanksByTotalTime_WithStats()
    {
        var store = new TelemetryIngestStore();
        var now = DateTime.UtcNow;
        store.IngestSpans([
            Span("paint.gpu.readback", now, 20),
            Span("paint.gpu.readback", now, 20),
            Span("paint.gpu.readback", now, 20),
            Span("layout.relayout", now, 5),
            Span("layout.relayout", now, 5),
        ]);

        var offenders = new TelemetryAnalyzer(store).TopOffenders(Wide, 10);

        offenders[0].Name.Should().Be("paint.gpu.readback");
        offenders[0].Count.Should().Be(3);
        offenders[0].TotalMs.Should().BeApproximately(60, 0.01);
        offenders[0].AvgMs.Should().BeApproximately(20, 0.01);
        offenders[1].Name.Should().Be("layout.relayout");
        offenders[1].TotalMs.Should().BeApproximately(10, 0.01);
    }

    [TestMethod]
    public void Frames_ComputesOverrunRateAndPercentiles()
    {
        var store = new TelemetryIngestStore();
        var now = DateTime.UtcNow;
        var samples = new List<MeterRecord>();
        for (var i = 0; i < 5; i++)
        {
            samples.Add(Gauge(RenderMetrics.FrameTimeMs, now, 10));
        }

        for (var i = 0; i < 5; i++)
        {
            samples.Add(Gauge(RenderMetrics.FrameTimeMs, now, 40));
        }

        store.IngestMetrics(samples);

        var frames = new TelemetryAnalyzer(store).Frames(Wide);

        frames.Count.Should().Be(10);
        frames.OverBudget.Should().Be(5);
        frames.OverBudgetRate.Should().BeApproximately(0.5, 0.001);
        frames.P99Ms.Should().BeApproximately(40, 0.5);
        frames.Signal.Should().Be("gui.frame.time_ms");
    }

    [TestMethod]
    public void Correlate_JoinsCpuSampleInsideSpanWindow()
    {
        var store = new TelemetryIngestStore();
        var start = DateTime.UtcNow.AddSeconds(-1);
        store.IngestSpans([Span("paint.gpu.readback", start, 40)]);
        // A CPU sample landing inside the span's [start, start+40ms] window.
        store.IngestMetrics([Gauge(RenderMetrics.ProcessCpuUtilization, start.AddMilliseconds(20), 0.95)]);

        var c = new TelemetryAnalyzer(store).Correlate("paint.gpu.readback", Wide);

        c.Count.Should().Be(1);
        c.AvgCpuDuring.Should().BeApproximately(0.95, 0.0001);
        c.AvgDurationMs.Should().BeApproximately(40, 0.5);
        c.Interpretation.Should().Contain("CPU");
    }

    [TestMethod]
    public void Correlate_NearestSampleWithinTolerance_WhenNoneInsideWindow()
    {
        var store = new TelemetryIngestStore();
        var start = DateTime.UtcNow.AddSeconds(-1);
        // 2ms span — no sample lands inside it; nearest sample is 300ms away (< 2s tolerance).
        store.IngestSpans([Span("paint.composite", start, 2)]);
        store.IngestMetrics([Gauge(RenderMetrics.ProcessCpuUtilization, start.AddMilliseconds(300), 0.4)]);

        var c = new TelemetryAnalyzer(store).Correlate("paint.composite", Wide);

        c.AvgCpuDuring.Should().BeApproximately(0.4, 0.0001);
    }

    [TestMethod]
    public void Overview_ReportsIngestCountsAndLatestResources()
    {
        var store = new TelemetryIngestStore();
        var now = DateTime.UtcNow;
        store.IngestSpans([Span("gui.render", now, 8)]);
        store.IngestMetrics([
            Gauge(RenderMetrics.ProcessCpuUtilization, now, 0.5),
            Gauge(RenderMetrics.ProcessMemoryWorkingSet, now, 1_073_741_824), // 1 GiB
        ]);

        var o = new TelemetryAnalyzer(store).Overview(Wide);

        o.SpansIngested.Should().Be(1);
        o.MetricsIngested.Should().Be(2);
        o.Resources.CpuUtilization.Should().BeApproximately(0.5, 0.0001);
        o.Resources.WorkingSetMb.Should().BeApproximately(1024, 0.5);
    }

    private static ActivityRecord Span(string name, DateTime start, double ms) => new(
        start, TimeSpan.FromMilliseconds(ms), "Starling.Engine", name,
        "trace", "span", null, ActivityStatusCode.Unset,
        Array.Empty<KeyValuePair<string, object?>>());

    private static MeterRecord Gauge(string instrument, DateTime ts, double value) => new(
        ts, "Starling.Engine", instrument, string.Empty, value,
        Array.Empty<KeyValuePair<string, object?>>());
}
