using System.Diagnostics;
using System.Text;
using Starling.Dom;
using Starling.Html.TreeBuilder;

namespace Starling.Html.Tests.TreeBuilder;

/// <summary>
/// html5lib-tests <c>tree-construction</c> conformance runner. Mirrors the
/// Test262 pattern (one aggregating method, on-disk report, ratchet floor)
/// because per-block <c>[DynamicData]</c> would generate ~1700 MSTest cases
/// and slow discovery to no useful end. To investigate a single block, set
/// <c>STARLING_TREEBUILD_FILTER</c> to a substring of <c>file.dat#index</c>.
///
/// Config via environment variables:
///   STARLING_TREEBUILD_FILTER  case-insensitive substring of "file.dat#N"
///   STARLING_TREEBUILD_FLOOR   minimum pass rate to require, percent
///   STARLING_TREEBUILD_VERBOSE 1 ⇒ dump expected vs actual for every failure
/// </summary>
[TestClass]
public sealed class Html5LibTreeConstructionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void Tree_construction_pass_rate()
    {
        var root = LocateCorpus();
        Assert.IsNotNull(root, "tree-construction corpus not copied to output — check csproj Content glob.");

        var filter = Environment.GetEnvironmentVariable("STARLING_TREEBUILD_FILTER");
        // Ratchet floor (same pattern as Test262 — bake in the current baseline
        // and raise it as conformance improves). Baseline 2026-06-16: 99.04%
        // (1760 / 1777) after the full §13.2.6 rewrite (all insertion modes,
        // adoption agency, foreign content + CDATA, customizable <select>). The
        // remaining ~17 require a JS engine (document.write/getElementById) or are
        // niche customizable-<select> mirroring / fragment-foreign edges.
        var floor = double.TryParse(Environment.GetEnvironmentVariable("STARLING_TREEBUILD_FLOOR"), out var f) ? f : 99d;
        var verbose = Environment.GetEnvironmentVariable("STARLING_TREEBUILD_VERBOSE") == "1";

        var cases = Enumerate(root!, filter).ToList();
        Assert.IsTrue(cases.Count > 0, "no fixtures discovered — corpus missing or filter mismatch.");

        var byFile = new SortedDictionary<string, FileStats>(StringComparer.Ordinal);
        var failSamples = new List<string>();
        var crashSamples = new List<string>();
        var allFails = new List<string>();
        var sw = Stopwatch.StartNew();
        int pass = 0, fail = 0, crash = 0;

        foreach (var c in cases)
        {
            var file = Path.GetFileName(c.SourceFile);
            var cur = byFile.TryGetValue(file, out var v) ? v : new FileStats();

            try
            {
                var actual = RunCase(c);
                var expected = c.Document;
                if (string.Equals(actual, expected, StringComparison.Ordinal))
                {
                    pass++;
                    cur.Pass++;
                    cur.Total++;
                }
                else
                {
                    fail++;
                    cur.Total++;
                    RecordFailure(failSamples, allFails, c, expected, actual, verbose);
                }
            }
            catch (Exception ex)
            {
                crash++;
                fail++;
                cur.Crash++;
                cur.Total++;
                var line = $"[CRASH] {c.Id}: {ex.GetType().Name}: {Truncate(ex.Message, 200)}";
                if (crashSamples.Count < 25)
                {
                    crashSamples.Add(line);
                }

                allFails.Add(line);
            }

            byFile[file] = cur;
        }
        sw.Stop();

        var total = pass + fail;
        var rate = total == 0 ? 0d : 100d * pass / total;

        var report = new StringBuilder();
        report.AppendLine($"html5lib tree-construction — {DateTime.UtcNow:u}");
        report.AppendLine($"filter={filter ?? "(none)"} cases={total} pass={pass} fail={fail} crash={crash}  in {sw.Elapsed.TotalSeconds:F1}s");
        report.AppendLine($"PASS RATE: {rate:F2}%  ({pass}/{total})");
        report.AppendLine();
        report.AppendLine("By fixture file (pass/total, * = had crashes):");
        foreach (var (file, c) in byFile)
        {
            var rateF = c.Total == 0 ? 0d : 100d * c.Pass / c.Total;
            var mark = c.Crash > 0 ? " *" : "";
            report.AppendLine($"  {c.Pass,5}/{c.Total,-5} {rateF,6:F1}%  {file}{mark}");
        }
        if (crashSamples.Count > 0)
        {
            report.AppendLine();
            report.AppendLine($"Crash samples ({crashSamples.Count} of {crash}):");
            foreach (var s in crashSamples)
            {
                report.AppendLine("  " + s);
            }
        }
        report.AppendLine();
        report.AppendLine($"Failure samples ({failSamples.Count} of {fail - crash}):");
        foreach (var s in failSamples)
        {
            report.AppendLine("  " + s);
        }

        TryWriteSidecar(report.ToString(), allFails);
        TestContext.WriteLine(report.ToString());

        if (floor > 0)
        {
            Assert.IsTrue(rate >= floor, $"tree-construction pass rate {rate:F2}% < floor {floor:F2}%");
        }
    }

    // ----- run one fixture --------------------------------------------------

    private static string RunCase(Html5LibCase c)
    {
        // scripting flag default: the corpus runs in both modes when neither
        // #script-on nor #script-off is set, but it never asks for both modes
        // *simultaneously*, so a single pass in "scripting disabled" matches
        // the html5lib conformance default (see HtmlParser.Parse remarks).
        var scripting = c.ScriptingEnabled ?? false;

        if (c.DocumentFragment is { Length: > 0 } ctxRaw)
        {
            var (ns, localName) = ParseFragmentContext(ctxRaw);
            var ownerDoc = new Document();
            var ctx = Element.CreateNamespaced(ns, localName);
            // HtmlTreeBuilder.ParseFragment doesn't take a scriptingEnabled flag
            // yet; once it does, thread `scripting` through here. For now the
            // default false matches the html5lib harness.
            _ = scripting;
            var fragment = HtmlTreeBuilder.ParseFragment(c.Data, ctx, ownerDoc);
            return Html5LibTreeSerializer.SerializeFragment(fragment);
        }
        else
        {
            var document = HtmlParser.Parse(c.Data, scriptingEnabled: scripting);
            return Html5LibTreeSerializer.Serialize(document);
        }
    }

    private static (string? ns, string localName) ParseFragmentContext(string raw)
    {
        if (raw.StartsWith("svg ", StringComparison.Ordinal))
        {
            return ("http://www.w3.org/2000/svg", raw[4..]);
        }

        if (raw.StartsWith("math ", StringComparison.Ordinal))
        {
            return ("http://www.w3.org/1998/Math/MathML", raw[5..]);
        }
        // A bare context name is an HTML element — give it the HTML namespace so
        // the context matches what a real innerHTML caller passes (Element built
        // via Document.CreateElement is HTML-namespaced). Without this the
        // CreateNamespaced(null, …) path would leave Namespace = "".
        return (Element.HtmlNamespace, raw);
    }

    // ----- enumeration ------------------------------------------------------

    private static IEnumerable<Html5LibCase> Enumerate(string root, string? filter)
    {
        // .dat files at the top level plus the scripted/ subdir.
        var paths = Directory.EnumerateFiles(root, "*.dat", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal);
        foreach (var path in paths)
        {
            foreach (var c in Html5LibDatFile.Read(path))
            {
                if (filter is not null && !c.Id.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                yield return c;
            }
        }
    }

    private static string? LocateCorpus()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "testdata", "spec",
            "html5lib-tests", "tree-construction");
        return Directory.Exists(dir) ? dir : null;
    }

    // ----- diagnostics ------------------------------------------------------

    private static void RecordFailure(List<string> samples, List<string> all,
        Html5LibCase c, string expected, string actual, bool verbose)
    {
        var firstDiff = FirstDifferingLine(expected, actual);
        var line = $"[DIFF ] {c.Id}: {firstDiff}";
        if (samples.Count < 25)
        {
            samples.Add(line);
        }

        if (verbose)
        {
            all.Add(line);
            all.Add("    expected:");
            foreach (var s in expected.Split('\n'))
            {
                all.Add("      " + s);
            }

            all.Add("    actual:");
            foreach (var s in actual.Split('\n'))
            {
                all.Add("      " + s);
            }
        }
        else
        {
            all.Add(line);
        }
    }

    private static string FirstDifferingLine(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (var i = 0; i < Math.Max(e.Length, a.Length); i++)
        {
            var es = i < e.Length ? e[i] : "(none)";
            var aas = i < a.Length ? a[i] : "(none)";
            if (!string.Equals(es, aas, StringComparison.Ordinal))
            {
                return $"line {i + 1}: expected {Truncate(es, 80)} | actual {Truncate(aas, 80)}";
            }
        }
        return "(documents differ but no line diff — trailing newline?)";
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private sealed class FileStats
    {
        public int Pass;
        public int Total;
        public int Crash;
    }

    private static void TryWriteSidecar(string report, List<string> failures)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "results", "tree-construction");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "summary.txt"), report);
            File.WriteAllLines(Path.Combine(dir, "failures.txt"), failures);
        }
        catch
        {
            // Best-effort — sandboxes / read-only test bins shouldn't fail the run.
        }
    }
}
