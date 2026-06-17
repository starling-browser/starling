using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Starling.Wpt.Tests;

/// <summary>
/// Runs the web-platform-tests conformance subset against the Starling engine
/// and reports a pass rate — the HTML/CSS/DOM analogue of <c>Test262Tests</c>.
/// The suite is NOT vendored; fetch it with <c>tools/fetch-wpt.sh</c> (pinned
/// SHA → testdata/wpt/suite/, gitignored). When the suite is absent the test is
/// inconclusive (skipped), so CI without it stays green.
///
/// Config via environment variables:
///   STARLING_WPT_DIRS        comma list of subdirs under the suite (default "dom,css,url")
///   STARLING_WPT_FILTER      case-insensitive path substring filter
///   STARLING_WPT_MAX         cap on number of files (0 = no cap)
///   STARLING_WPT_TIMEOUT_MS  per-file timeout (default 10000)
///   STARLING_WPT_FLOOR       minimum subtest pass rate to require, percent (default 0 = report only)
///
/// The pass rate is over *subtests* that produced a result. Files that produce
/// no testharness output at all (reftests, load errors, or testharness.js
/// failing to run) are reported as "no-result" and excluded from the denominator.
/// </summary>
[TestClass]
public class WptTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Conformance_pass_rate()
    {
        var suite = WptCorpus.LocateSuite();
        if (suite is null)
        {
            Assert.Inconclusive("wpt suite not found — run tools/fetch-wpt.sh (expects testdata/wpt/suite/resources/testharness.js).");
            return;
        }

        var cfg = WptCorpus.ReadConfig();
        var floor = double.TryParse(Environment.GetEnvironmentVariable("STARLING_WPT_FLOOR"), out var f) ? f : 0d;

        var files = WptCorpus.EnumerateTests(suite, cfg.Dirs, cfg.Filter, cfg.Max).ToList();

        using var server = new WptFileServer(suite);
        var runner = new WptRunner(server.BaseUrl, cfg.TimeoutMs);

        var resultsDir = Path.GetFullPath(Path.Combine(suite, "..", "results"));
        Directory.CreateDirectory(resultsDir);
        var progressPath = Path.Combine(resultsDir, "progress.txt");

        int pass = 0, fail = 0, timeout = 0, notrun = 0, harnessErr = 0, noResult = 0;
        var byCat = new SortedDictionary<string, (int pass, int total)>(StringComparer.Ordinal);
        var failSamples = new List<string>();
        var noResultSamples = new List<string>();
        var allFails = new List<string>();
        // Phase-0 classifier: bucket every non-passing subtest by root-cause
        // signature so causes.txt is the auto-generated, impact-ranked backlog.
        var causes = new Dictionary<string, (int count, string example)>(StringComparer.Ordinal);
        void Bump(string cause, string rel)
        {
            (int count, string example) cur = causes.TryGetValue(cause, out var c) ? c : (0, rel);
            causes[cause] = (cur.count + 1, cur.example);
        }

        var sw = Stopwatch.StartNew();
        var fileCount = 0;

        foreach (var rel in files)
        {
            fileCount++;
            // Incremental progress so an uncatchable crash leaves the culprit on disk.
            File.WriteAllText(progressPath, $"{fileCount}/{files.Count}: {rel}\n");

            var res = runner.RunFile(rel);
            var cat = WptCorpus.Category(rel);
            (int pass, int total) cur = byCat.TryGetValue(cat, out var c) ? c : (0, 0);

            if (!res.HasResult)
            {
                noResult++;
                Bump("no-result", rel);
                if (noResultSamples.Count < 40)
                {
                    noResultSamples.Add($"{rel} :: {res.Detail}");
                }
            }
            else if (res.Subtests.Count == 0)
            {
                // Harness ran but errored (or a single-page test with no subtests).
                if (res.HarnessStatus == 0) { pass++; cur = (cur.pass + 1, cur.total + 1); }
                else { fail++; harnessErr++; cur = (cur.pass, cur.total + 1); RecordFail(failSamples, allFails, rel, res.Detail); Bump("harness-error", rel); }
            }
            else
            {
                foreach (var t in res.Subtests)
                {
                    switch (t.Outcome)
                    {
                        case WptOutcome.Pass: pass++; cur = (cur.pass + 1, cur.total + 1); break;
                        case WptOutcome.Timeout: timeout++; cur = (cur.pass, cur.total + 1); RecordFail(failSamples, allFails, rel, $"[timeout] {t.Name}: {t.Message}"); Bump("timeout", rel); break;
                        case WptOutcome.NotRun: notrun++; cur = (cur.pass, cur.total + 1); Bump("notrun", rel); break;
                        default: fail++; cur = (cur.pass, cur.total + 1); RecordFail(failSamples, allFails, rel, $"{t.Name}: {t.Message}"); Bump(ClassifyCause(t.Message), rel); break;
                    }
                }
            }
            byCat[cat] = cur;
        }
        sw.Stop();

        var ran = pass + fail + timeout + notrun;
        var rate = ran == 0 ? 0d : 100d * pass / ran;

        var report = new StringBuilder();
        report.AppendLine($"WPT conformance — {DateTime.UtcNow:u}");
        report.AppendLine($"sha-pinned suite; dirs=[{string.Join(",", cfg.Dirs)}] filter={cfg.Filter ?? "(none)"} max={(cfg.Max == 0 ? "∞" : cfg.Max.ToString())}");
        report.AppendLine($"files={fileCount} subtests_run={ran} pass={pass} fail={fail} timeout={timeout} notrun={notrun} (harness-errors={harnessErr}) no-result-files={noResult}");
        report.AppendLine($"PASS RATE: {rate:F2}%  ({pass}/{ran})  in {sw.Elapsed.TotalSeconds:F1}s");
        report.AppendLine();
        report.AppendLine("By area (pass/total subtests):");
        foreach (var (cat, cc) in byCat)
        {
            report.AppendLine($"  {cc.pass,7}/{cc.total,-7} {100d * cc.pass / Math.Max(1, cc.total),6:F1}%  {cat}");
        }

        report.AppendLine();
        // Impact-ranked root-cause histogram (Phase 0). The full list goes to
        // causes.txt; the top slice is inlined for at-a-glance triage.
        var rankedCauses = causes.OrderByDescending(kv => kv.Value.count).ThenBy(kv => kv.Key, StringComparer.Ordinal).ToList();
        report.AppendLine($"Top failure causes (subtests; full list → causes.txt):");
        foreach (var kv in rankedCauses.Take(25))
        {
            report.AppendLine($"  {kv.Value.count,6}  {kv.Key,-34}  e.g. {kv.Value.example}");
        }

        report.AppendLine();
        report.AppendLine($"Failure samples ({failSamples.Count} shown):");
        foreach (var s in failSamples)
        {
            report.AppendLine("  " + s);
        }

        report.AppendLine();
        report.AppendLine($"No-result samples ({noResultSamples.Count} shown) — reftests, load errors, or testharness.js not running:");
        foreach (var s in noResultSamples)
        {
            report.AppendLine("  " + s);
        }

        var enc = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);
        var reportPath = Path.Combine(resultsDir, "summary.txt");
        File.WriteAllText(reportPath, report.ToString(), enc);
        File.WriteAllLines(Path.Combine(resultsDir, "failures.txt"), allFails, enc);
        File.WriteAllLines(Path.Combine(resultsDir, "causes.txt"),
            rankedCauses.Select(kv => $"{kv.Value.count}\t{kv.Key}\te.g. {kv.Value.example}"), enc);

        TestContext.WriteLine(report.ToString());
        TestContext.WriteLine("report: " + reportPath);

        if (floor > 0)
        {
            Assert.IsTrue(rate >= floor, $"WPT pass rate {rate:F2}% < floor {floor:F2}% — see {reportPath}");
        }
    }

    /// <summary>Map a failing subtest's message to a root-cause signature so
    /// causes that share a fix collapse into one ranked bucket. Mechanical gaps
    /// resolve to the specific missing API (e.g. <c>missing-method:createEvent</c>);
    /// semantic failures resolve to the assertion kind (<c>assert:assert_equals</c>).
    /// The "hint" the engine attaches to a "not a function" throw is a heuristic
    /// and sometimes misfires (length/e/alias/plural/undefined) — those buckets
    /// are noise, not real missing methods (see tasks/wpt/PLAN.md).</summary>
    private static string ClassifyCause(string? message)
    {
        var m = message ?? "";
        var mh = Regex.Match(m, @"method hint: '([^']+)'");
        if (mh.Success)
        {
            return "missing-method:" + mh.Groups[1].Value;
        }

        var nh = Regex.Match(m, @"new hint: '([^']+)'");
        if (nh.Success)
        {
            return "missing-ctor:" + nh.Groups[1].Value;
        }

        if (m.Contains("not a function", StringComparison.Ordinal))
        {
            return "missing-method:(unknown)";
        }

        if (m.Contains("not a constructor", StringComparison.Ordinal))
        {
            return "missing-ctor:(unknown)";
        }

        var am = Regex.Match(m, @"\bassert_[a-z_]+");
        if (am.Success)
        {
            return "assert:" + am.Value;
        }

        if (m.Contains("is not defined", StringComparison.Ordinal))
        {
            return "reference-error";
        }

        if (m.Contains("Test timed out", StringComparison.Ordinal))
        {
            return "timeout";
        }

        var trimmed = m.Trim();
        if (trimmed.Length == 0)
        {
            return "other:(empty)";
        }

        return "other:" + (trimmed.Length > 48 ? trimmed[..48] : trimmed);
    }

    private static void RecordFail(List<string> samples, List<string> all, string rel, string? detail)
    {
        var line = $"{rel} :: {detail}";
        if (samples.Count < 60)
        {
            samples.Add(line);
        }

        all.Add(line);
    }
}
