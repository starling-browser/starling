using System.Diagnostics;
using System.Text;

namespace Starling.Js.Test262.Tests;

/// <summary>
/// Runs the tc39/test262 conformance corpus against <b>Jint</b> (the temporary
/// compatibility-crutch engine) and reports a pass rate — an INFORMATIONAL
/// compat-delta baseline (package J5b) that quantifies how much web-compat Jint
/// buys over the in-house Starling.Js engine on the identical corpus.
///
/// It reuses the SAME corpus discovery, frontmatter parsing, skip-feature set,
/// and env-var config as <see cref="Test262Tests"/>, so STARLING_TEST262_DIRS /
/// FILTER / MAX / TIMEOUT_MS all apply here too. The corpus is NOT vendored —
/// fetch it with <c>tools/fetch-test262.sh</c>. When the corpus is absent the
/// test is inconclusive (skipped), so CI without the corpus stays green.
///
/// This is report-only: STARLING_TEST262_FLOOR defaults to 0 (no hard-fail) so
/// this never becomes a CI gate. Set the env var to enforce a Jint floor locally.
/// Runnable independently of the Starling.Js test so one can get both engines'
/// numbers from the same corpus.
/// </summary>
[TestClass]
public class JintTest262Tests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Jint_conformance_pass_rate()
    {
        var root = Test262Corpus.LocateCorpus();
        if (root is null)
        {
            Assert.Inconclusive("test262 corpus not found — run tools/fetch-test262.sh (expects testdata/test262/test).");
            return;
        }

        var cfg = Test262Corpus.ReadConfig();
        // INFORMATIONAL: default floor is 0 (report-only). Only the explicit env
        // var enforces a minimum — Jint conformance is a baseline, not a gate.
        var floor = double.TryParse(Environment.GetEnvironmentVariable("STARLING_TEST262_FLOOR"), out var f) ? f : 0d;

        var files = Test262Corpus.EnumerateTests(Path.Combine(root, "test"), cfg.Dirs, cfg.Filter, cfg.Max).ToList();
        var runner = new JintTest262Runner(root, cfg.TimeoutMs);

        var resultsDir = Path.Combine(root, "results");
        Directory.CreateDirectory(resultsDir);

        int pass = 0, fail = 0, timeoutN = 0, skip = 0;
        var byCat = new SortedDictionary<string, (int pass, int total)>(StringComparer.Ordinal);
        var failSamples = new List<string>();
        var allFails = new List<string>();
        var sw = Stopwatch.StartNew();
        var fileCount = 0;

        foreach (var file in files)
        {
            fileCount++;
            foreach (var r in runner.RunFile(file))
            {
                var cat = Test262Corpus.Category(r.File);
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
        report.AppendLine($"Test262 conformance (Jint) — {DateTime.UtcNow:u}");
        report.AppendLine($"dirs=[{string.Join(",", cfg.Dirs)}] filter={cfg.Filter ?? "(none)"} max={(cfg.Max == 0 ? "∞" : cfg.Max.ToString())}");
        report.AppendLine($"files={fileCount} scenarios_run={ran} pass={pass} fail={fail} timeout={timeoutN} skip(out-of-scope)={skip}");
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

        var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        var reportPath = Path.Combine(resultsDir, "jint-summary.txt");
        File.WriteAllText(reportPath, report.ToString(), enc);
        File.WriteAllLines(Path.Combine(resultsDir, "jint-failures.txt"), allFails, enc);

        TestContext.WriteLine(report.ToString());
        TestContext.WriteLine($"COMPAT-DELTA (Jint, dirs=[{string.Join(",", cfg.Dirs)}]): {rate:F2}% ({pass}/{ran})");

        // One-line side-by-side IF the Starling.Js runner has already written its
        // summary against the same corpus (results/summary.txt). A full Starling.Js
        // language run is ~12 min, so we never trigger it inline — we just reuse a
        // previously-written number when present (zero added cost). Run
        // Conformance_pass_rate first to populate it.
        var starlingRate = TryReadStarlingRate(resultsDir);
        if (starlingRate is { } sr)
        {
            TestContext.WriteLine($"SIDE-BY-SIDE (dirs=[{string.Join(",", cfg.Dirs)}]): Starling.Js {sr:F2}%  |  Jint {rate:F2}%  (delta +{rate - sr:F2}pts)");
        }
        else
        {
            TestContext.WriteLine("SIDE-BY-SIDE: Starling.Js number unavailable (run Conformance_pass_rate against the same corpus first).");
        }

        TestContext.WriteLine("report: " + reportPath);

        // INFORMATIONAL: only enforce a floor when one is explicitly requested.
        if (floor > 0)
        {
            Assert.IsTrue(rate >= floor, $"Jint Test262 pass rate {rate:F2}% < floor {floor:F2}% — see {reportPath}");
        }
    }

    private static void RecordSample(List<string> samples, ScenarioResult r)
    {
        if (samples.Count < 60)
        {
            samples.Add($"[{r.Mode}] {r.File} :: {r.Detail}");
        }
    }

    /// <summary>Pull the Starling.Js pass rate out of the previously-written
    /// <c>results/summary.txt</c> (the line "PASS RATE: NN.NN% …"), if present, so
    /// the Jint run can print a side-by-side without re-running the in-house engine.
    /// Returns null when the file is absent or unparseable.</summary>
    private static double? TryReadStarlingRate(string resultsDir)
    {
        var path = Path.Combine(resultsDir, "summary.txt");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var idx = line.IndexOf("PASS RATE:", StringComparison.Ordinal);
                if (idx < 0)
                {
                    continue;
                }

                var pct = line.IndexOf('%', idx);
                if (pct < 0)
                {
                    continue;
                }

                var num = line.Substring(idx + "PASS RATE:".Length, pct - idx - "PASS RATE:".Length).Trim();
                if (double.TryParse(num, System.Globalization.CultureInfo.InvariantCulture, out var v))
                {
                    return v;
                }
            }
        }
        catch { /* best-effort: side-by-side is a nicety, never load-bearing */ }
        return null;
    }
}
