using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Starling.Common.Diagnostics;

namespace Starling.Telemetry;

/// <summary>
/// Samples this process's CPU and memory use on a fixed cadence and records them
/// as gauges through <see cref="StarlingTelemetry"/> (which the OpenTelemetry sink
/// maps to OpenTelemetry Protocol metrics). These are the "local computer" signals the telemetry
/// daemon joins to span time-windows so a janky frame can be attributed to CPU
/// saturation, the garbage collector, or memory pressure rather than guessed at.
///
/// CPU utilization is computed as the process-CPU-time delta over the wall-clock
/// delta, normalised by logical core count, so the gauge reads 0..1 across the
/// whole machine (1.0 == every core pinned by this process). Sampling is
/// best-effort: a platform that denies a counter is logged at Trace rather than
/// crashing the host.
/// </summary>
public sealed class ProcessResourceSampler : IDisposable
{
    private readonly ILogger _log;
    private readonly Process _proc = Process.GetCurrentProcess();
    private readonly int _cores = Math.Max(1, Environment.ProcessorCount);
    private readonly Timer _timer;
    private TimeSpan _lastCpu;
    private long _lastTimestamp;
    private bool _disposed;

    /// <summary>
    /// Start sampling at <paramref name="interval"/> (default 500 ms — a balance
    /// between correlation resolution and overhead). The first tick establishes
    /// the CPU baseline; the gauge becomes meaningful from the second tick.
    /// </summary>
    public ProcessResourceSampler(ILogger<ProcessResourceSampler>? log = null, TimeSpan? interval = null)
    {
        _log = log ?? NullLogger<ProcessResourceSampler>.Instance;
        var period = interval ?? TimeSpan.FromMilliseconds(500);
        _lastCpu = SafeTotalProcessorTime();
        _lastTimestamp = Stopwatch.GetTimestamp();
        // Emit the constant core count once up front so consumers can de-normalise.
        StarlingTelemetry.Gauge(RenderMetrics.ProcessCpuCores, _cores);
        _timer = new Timer(static state => ((ProcessResourceSampler)state!).Sample(), this, period, period);
    }

    private void Sample()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _proc.Refresh();

            var now = Stopwatch.GetTimestamp();
            var wall = Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalMilliseconds;
            var cpu = SafeTotalProcessorTime();
            var cpuDeltaMs = (cpu - _lastCpu).TotalMilliseconds;
            _lastCpu = cpu;
            _lastTimestamp = now;

            // Fraction of total machine capacity: cpu time / (wall * cores).
            var util = wall > 0 ? Math.Clamp(cpuDeltaMs / (wall * _cores), 0, 1) : 0;
            StarlingTelemetry.Gauge(RenderMetrics.ProcessCpuUtilization, util);

            StarlingTelemetry.Gauge(RenderMetrics.ProcessMemoryWorkingSet, _proc.WorkingSet64);
            StarlingTelemetry.Gauge(RenderMetrics.ProcessMemoryManaged, GC.GetTotalMemory(forceFullCollection: false));
            StarlingTelemetry.Gauge(RenderMetrics.ProcessHeapBytes, GC.GetGCMemoryInfo().HeapSizeBytes);
            StarlingTelemetry.Gauge(RenderMetrics.ProcessGcGen0, GC.CollectionCount(0));
            StarlingTelemetry.Gauge(RenderMetrics.ProcessGcGen1, GC.CollectionCount(1));
            StarlingTelemetry.Gauge(RenderMetrics.ProcessGcGen2, GC.CollectionCount(2));

            try { StarlingTelemetry.Gauge(RenderMetrics.ProcessThreads, _proc.Threads.Count); }
            catch (Exception ex)
            {
                // Threads is platform-sensitive; skip if denied.
                ProcessResourceSamplerLog.ThreadCountUnavailable(_log, ex);
            }
        }
        catch (Exception ex)
        {
            // Sampling must never take the host down.
            ProcessResourceSamplerLog.SampleFailed(_log, ex);
        }
    }

    private TimeSpan SafeTotalProcessorTime()
    {
        try { return _proc.TotalProcessorTime; }
        catch (Exception ex)
        {
            ProcessResourceSamplerLog.CpuTimeUnavailable(_log, ex);
            return _lastCpu;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
        _proc.Dispose();
    }
}

internal static partial class ProcessResourceSamplerLog
{
    [LoggerMessage(Level = LogLevel.Trace, Message = "process thread count unavailable on this platform")]
    public static partial void ThreadCountUnavailable(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Trace, Message = "process resource sample failed")]
    public static partial void SampleFailed(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Trace, Message = "process CPU time unavailable; reusing last sample")]
    public static partial void CpuTimeUnavailable(ILogger logger, Exception ex);
}
