using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Starling.Engine;

namespace Starling.Wpt.Tests;

public enum WptOutcome { Pass, Fail, Timeout, NotRun }

public sealed record WptSubtest(string Name, WptOutcome Outcome, string? Message);

/// <summary>
/// Outcome of running one WPT file. <see cref="HasResult"/> is false when the
/// page produced no testharness output at all (load error, hard timeout, or the
/// page wasn't a testharness test / testharness.js failed to run) — those are
/// reported but excluded from the pass-rate denominator, the way Test262 skips
/// out-of-scope files. When true, <see cref="Subtests"/> + <see cref="HarnessStatus"/>
/// hold the parsed results.
/// </summary>
public sealed record WptFileResult(
    string File, bool HasResult, int HarnessStatus, IReadOnlyList<WptSubtest> Subtests, string? Detail);

/// <summary>
/// Runs a single WPT testharness file: navigates the engine to it over the local
/// server, lets scripts run + the event loop drain, then reads the JSON results
/// our <c>testharnessreport.js</c> stashed on <c>&lt;html data-wpt-results&gt;</c>.
/// Each file runs on its own worker thread with a hard timeout (a runaway test
/// can spin the JS VM past any cooperative cancellation) and its own engine +
/// capturing diagnostics, so an abandoned thread can't corrupt the next file.
/// </summary>
public sealed class WptRunner
{
    private readonly string _baseUrl;
    private readonly int _timeoutMs;

    public WptRunner(string baseUrl, int timeoutMs)
    {
        _baseUrl = baseUrl;
        _timeoutMs = timeoutMs;
    }

    public WptFileResult RunFile(string rel)
    {
        WptFileResult? result = null;
        var worker = new Thread(() =>
        {
            try { result = RunCore(rel); }
            catch (Exception ex)
            {
                result = new WptFileResult(rel, false, -1, Array.Empty<WptSubtest>(),
                    "host:" + ex.GetType().Name + ":" + Truncate(ex.Message));
            }
        }, maxStackSize: 32 * 1024 * 1024)
        { IsBackground = true };

        worker.Start();
        // Real-time backstop: a little over the cooperative load timeout.
        if (!worker.Join(_timeoutMs + 5_000))
        {
            return new WptFileResult(rel, false, -2, Array.Empty<WptSubtest>(), "timeout (thread)");
        }

        return result!;
    }

    private WptFileResult RunCore(string rel)
    {
        var loggerFactory = new CapturingLoggerFactory();
        var engine = new StarlingEngine(loggerFactory: loggerFactory);
        var options = new RenderOptions(new SixLabors.ImageSharp.Size(800, 600), 16f);
        var url = _baseUrl + "/" + rel;

        using var cts = new CancellationTokenSource(_timeoutMs);
        Starling.Common.Result<LaidOutPage, RenderError> r;
        try
        {
            r = engine.LayoutPageAsync(url, options, cts.Token, onFirstPaint: _ => { })
                .GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return NoResult(rel, "timeout (load)", loggerFactory);
        }
        if (r.IsErr)
        {
            return NoResult(rel, "load:" + r.Error.Message, loggerFactory);
        }

        using var page = r.Value;
        var json = ReadResults(page);

        // Drive async tests: advance virtual time in 50 ms ticks until results
        // appear, the loop goes idle, or we exhaust the time budget. The report
        // script schedules a fallback testharness timeout() at 4 s of virtual
        // time, so a testharness file always completes by ~80 ticks; the idle
        // cutoff (above that) only stops pages with no testharness at all
        // (reftests), avoiding a busy-spin to the real-time budget.
        var sw = Stopwatch.StartNew();
        var idleStreak = 0;
        while (json is null && sw.ElapsedMilliseconds < _timeoutMs && page.Scripting is not null)
        {
            var didWork = page.Scripting.PumpFrame(50);
            json = ReadResults(page);
            idleStreak = didWork ? 0 : idleStreak + 1;
            if (idleStreak > 120)
            {
                break; // ~6 s virtual idle (past the 4 s fallback)
            }
        }

        if (json is null)
        {
            return NoResult(rel, "no-result", loggerFactory);
        }

        return Parse(rel, json);
    }

    private static string? ReadResults(LaidOutPage page)
    {
        var v = page.Document.DocumentElement?.GetAttribute("data-wpt-results");
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static WptFileResult NoResult(string rel, string detail, CapturingLoggerFactory rec)
        => new(rel, false, -1, Array.Empty<WptSubtest>(), rec.Tail() is { Length: > 0 } t ? detail + " | " + t : detail);

    private static WptFileResult Parse(string rel, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var hstatus = root.TryGetProperty("status", out var s) ? s.GetInt32() : 0;
            var subs = new List<WptSubtest>();
            if (root.TryGetProperty("tests", out var tests) && tests.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tests.EnumerateArray())
                {
                    var name = t.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "(unnamed)";
                    var st = t.TryGetProperty("status", out var ss) ? ss.GetInt32() : 1;
                    var msg = t.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                    subs.Add(new WptSubtest(name, MapSubtest(st), msg));
                }
            }
            var detail = hstatus != 0
                ? "harness-status:" + hstatus + (root.TryGetProperty("message", out var hm) && hm.ValueKind == JsonValueKind.String ? " " + Truncate(hm.GetString()!) : "")
                : null;
            return new WptFileResult(rel, HasResult: true, hstatus, subs, detail);
        }
        catch (JsonException ex)
        {
            return new WptFileResult(rel, false, -1, Array.Empty<WptSubtest>(), "result-parse:" + Truncate(ex.Message));
        }
    }

    private static WptOutcome MapSubtest(int status) => status switch
    {
        0 => WptOutcome.Pass,
        2 => WptOutcome.Timeout,
        3 => WptOutcome.NotRun,
        _ => WptOutcome.Fail, // 1 FAIL, 4 PRECONDITION_FAILED, anything else
    };

    private static string Truncate(string s) => s.Length <= 160 ? s : s[..160];

    /// <summary>Captures Warn/Error log entries (the engine routes page console
    /// output and script exceptions here) so a "no-result" file can report *why*
    /// — typically the unimplemented JS/DOM feature that broke testharness.js.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly List<string> _msgs = new();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

        public void AddProvider(ILoggerProvider provider) { }

        public void Dispose() { }

        public string Tail() => _msgs.Count == 0 ? "" : string.Join(" ;; ", _msgs);

        private void TryAdd(string m) { lock (_msgs) { if (_msgs.Count < 8) { _msgs.Add(Truncate(m)); } } }

        private sealed class CapturingLogger(CapturingLoggerFactory owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                if (logLevel < LogLevel.Warning)
                {
                    return;
                }

                var msg = exception is not null
                    ? $"[{logLevel}] {category}: {exception.GetType().Name}: {formatter(state, exception)}"
                    : $"[{logLevel}] {category}: {formatter(state, exception)}";
                owner.TryAdd(msg);
            }
        }
    }
}
