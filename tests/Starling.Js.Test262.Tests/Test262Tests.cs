using System.Diagnostics;
using System.Text;

namespace Starling.Js.Test262.Tests;

/// <summary>
/// Runs the tc39/test262 conformance corpus against the Starling JS engine and
/// reports a pass rate. The corpus is NOT vendored — fetch it first with
/// <c>tools/fetch-test262.sh</c> (pinned SHA → testdata/test262/, gitignored).
/// When the corpus is absent the test is inconclusive (skipped), so CI without
/// the corpus stays green.
///
/// Config via environment variables:
///   STARLING_TEST262_DIRS    comma list of subdirs under test/ (default "language,built-ins")
///   STARLING_TEST262_FILTER  case-insensitive path substring filter
///   STARLING_TEST262_MAX     cap on number of files (0 = no cap)
///   STARLING_TEST262_TIMEOUT_MS  per-scenario timeout (default 10000)
///   STARLING_TEST262_FLOOR   minimum pass rate to require, percent (default 0 = report only)
/// </summary>
[TestClass]
public class Test262Tests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Conformance_pass_rate()
    {
        var root = LocateCorpus();
        if (root is null)
        {
            Assert.Inconclusive("test262 corpus not found — run tools/fetch-test262.sh (expects testdata/test262/test).");
            return;
        }

        // Default scope is `language` (core ECMAScript semantics): it runs
        // cleanly and fast. `built-ins` is opt-in via STARLING_TEST262_DIRS —
        // a few of its tests allocate huge arrays and hit the timeout. Override
        // STARLING_TEST262_FLOOR too when you widen the scope.
        var dirs = (Environment.GetEnvironmentVariable("STARLING_TEST262_DIRS") ?? "language")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filter = Environment.GetEnvironmentVariable("STARLING_TEST262_FILTER");
        var max = int.TryParse(Environment.GetEnvironmentVariable("STARLING_TEST262_MAX"), out var m) ? m : 0;
        var timeout = int.TryParse(Environment.GetEnvironmentVariable("STARLING_TEST262_TIMEOUT_MS"), out var t) ? t : 5_000;
        // Ratchet floor for the default `language` scope (baseline 2026-05-21:
        // 37.77%). Raise as conformance improves; override for other scopes.
        var floor = double.TryParse(Environment.GetEnvironmentVariable("STARLING_TEST262_FLOOR"), out var f) ? f
            : (dirs.Length == 1 && dirs[0] == "language" ? 37d : 0d);

        var files = EnumerateTests(Path.Combine(root, "test"), dirs, filter, max);
        var runner = new Test262Runner(root, timeout);

        Directory.CreateDirectory(Path.Combine(root, "results"));
        var progressPath = Path.Combine(root, "results", "progress.txt");

        int pass = 0, fail = 0, timeoutN = 0, skip = 0;
        var byCat = new SortedDictionary<string, (int pass, int total)>(StringComparer.Ordinal);
        var failSamples = new List<string>();
        var allFails = new List<string>();
        var sw = Stopwatch.StartNew();
        var fileCount = 0;

        foreach (var file in files)
        {
            fileCount++;
            // Incremental progress so an uncatchable crash (e.g. native stack
            // overflow in the parser) leaves the offending file on disk.
            File.WriteAllText(progressPath, $"{fileCount}: {file}\n");
            foreach (var r in runner.RunFile(file))
            {
                var cat = Category(r.File);
                var cur = byCat.TryGetValue(cat, out var c) ? c : (0, 0);
                switch (r.Outcome)
                {
                    case Outcome.Pass: pass++; cur = (cur.Item1 + 1, cur.Item2 + 1); break;
                    case Outcome.Fail: fail++; cur = (cur.Item1, cur.Item2 + 1); RecordSample(failSamples, r); allFails.Add($"[{r.Mode}] {r.File} :: {r.Detail}"); break;
                    case Outcome.Timeout: timeoutN++; cur = (cur.Item1, cur.Item2 + 1); break;
                    case Outcome.Skip: skip++; break; // not counted in denominator
                }
                byCat[cat] = cur;
            }
        }
        sw.Stop();

        var ran = pass + fail + timeoutN;
        var rate = ran == 0 ? 0d : 100d * pass / ran;

        var report = new StringBuilder();
        report.AppendLine($"Test262 conformance — {DateTime.UtcNow:u}");
        report.AppendLine($"dirs=[{string.Join(",", dirs)}] filter={filter ?? "(none)"} max={(max == 0 ? "∞" : max.ToString())}");
        report.AppendLine($"files={fileCount} scenarios_run={ran} pass={pass} fail={fail} timeout={timeoutN} skip(module/io)={skip}");
        report.AppendLine($"PASS RATE: {rate:F2}%  ({pass}/{ran})  in {sw.Elapsed.TotalSeconds:F1}s");
        report.AppendLine();
        report.AppendLine("By category (pass/total):");
        foreach (var (cat, c) in byCat)
        {
            report.AppendLine($"  {c.pass,7}/{c.total,-7} {100d * c.pass / Math.Max(1, c.total),6:F1}%  {cat}");
        }

        report.AppendLine();
        report.AppendLine($"Failure samples ({failSamples.Count} shown):");
        foreach (var s in failSamples)
        {
            report.AppendLine("  " + s);
        }

        var resultsDir = Path.Combine(root, "results");
        Directory.CreateDirectory(resultsDir);
        var reportPath = Path.Combine(resultsDir, "summary.txt");
        // Failure details can contain lone surrogates copied out of a test's
        // source text; a strict encoder throws on those. Use a UTF-8 encoder
        // that replaces unencodable chars so the dump never crashes.
        var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        File.WriteAllText(reportPath, report.ToString(), enc);
        // Full failure dump (every failing scenario) for offline triage.
        File.WriteAllLines(Path.Combine(resultsDir, "failures.txt"), allFails, enc);

        TestContext.WriteLine(report.ToString());
        TestContext.WriteLine("report: " + reportPath);

        if (floor > 0)
        {
            Assert.IsTrue(rate >= floor, $"Test262 pass rate {rate:F2}% < floor {floor:F2}% — see {reportPath}");
        }
    }

    private static void RecordSample(List<string> samples, ScenarioResult r)
    {
        if (samples.Count < 60)
        {
            samples.Add($"[{r.Mode}] {r.File} :: {r.Detail}");
        }
    }

    private static string Category(string relFile) => Test262Corpus.Category(relFile);

    private static IEnumerable<string> EnumerateTests(string testDir, string[] dirs, string? filter, int max) =>
        Test262Corpus.EnumerateTests(testDir, dirs, filter, max);

    /// <summary>Walk up from the test binary to the repo and locate
    /// testdata/test262 (gitignored, fetched separately).</summary>
    private static string? LocateCorpus() => Test262Corpus.LocateCorpus();
}
