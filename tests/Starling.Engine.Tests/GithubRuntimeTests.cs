using System.Text;
using AwesomeAssertions;
using Starling.Bindings;
using Starling.Html;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;
using Starling.Net;
namespace Starling.Engine.Tests;

/// <summary>
/// Offline runtime harness for the github.com snapshot (testdata/sites/github):
/// parse the real homepage into the Starling DOM, install the window/DOM
/// bindings (the Starling JS engine, not Jint), then execute every classic
/// script in document order and capture parse/compile failures separately from
/// runtime throws + console.error. Runs the RUNTIME layer (execution + bindings)
/// on real github code without the live browser.
///
/// <para>The assertion guards the bounded, proven claim: every github bundle
/// PARSES and COMPILES in the engine. Runtime throws are DOCUMENTED (written to
/// RUNTIME_REPORT.txt) rather than asserted — full runtime support for a SPA
/// the size of github is an open-ended capability (event loop, dynamic module
/// imports, the full DOM/Web-API surface) that this harness is built to drive
/// incrementally. Offline, network/dynamic-import driven code paths do not run,
/// so this only exercises eval-time execution.</para>
/// </summary>
[TestClass]
public sealed class GithubRuntimeTests
{
    private static string GithubDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Starling.slnx")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate Starling.slnx walking up from the test binary.");
        }

        return Path.Combine(dir.FullName, "testdata", "sites", "github");
    }

    [TestMethod]
    public void Github_classic_scripts_parse_compile_and_eval()
    {
        var ghDir = GithubDir();
        var doc = HtmlParser.Parse(File.ReadAllText(Path.Combine(ghDir, "index.html")), scriptingEnabled: true);

        var runtime = new JsRuntime();
        var consoleErrors = new List<string>();
        runtime.ConsoleSink = (level, message) => { if (level == "error") { consoleErrors.Add(message); } };

        using var http = new StarlingHttpClient();
        WindowBinding.Install(runtime, doc, new WindowInstallOptions(
            DocumentUrl: "https://github.com/", HttpClient: http));

        var parseCompileFailures = new List<string>();
        var runtimeThrows = new List<string>();
        var ran = 0;

        foreach (var script in doc.GetElementsByTagName("script"))
        {
            var type = script.GetAttribute("type");
            if (type is not (null or "" or "application/javascript" or "text/javascript"))
            {
                continue;
            }

            var src = script.GetAttribute("src");
            var name = src ?? "<inline>";
            string source;
            if (src is null)
            {
                source = script.TextContent ?? "";
            }
            else
            {
                var local = Path.Combine(ghDir, src);
                if (!File.Exists(local))
                {
                    continue;
                }

                source = File.ReadAllText(local);
            }
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            Chunk chunk;
            try { chunk = JsCompiler.Compile(new JsParser(source).ParseProgram(), name); }
            catch (Exception ex) { parseCompileFailures.Add($"{name}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); continue; }

            ran++;
            try { new JsVm(runtime).Run(chunk); }
            catch (Exception ex) { runtimeThrows.Add($"{name}: {ex.GetType().Name}: {ex.Message.Split('\n')[0]}"); }
        }

        try { runtime.DrainMicrotasks(); } catch (Exception ex) { runtimeThrows.Add($"<drain>: {ex.Message.Split('\n')[0]}"); }

        var report = new StringBuilder();
        report.AppendLine($"github runtime harness: {ran} scripts evaluated");
        report.AppendLine($"  parse/compile failures: {parseCompileFailures.Count}");
        report.AppendLine($"  eval-time throws: {runtimeThrows.Count}");
        report.AppendLine($"  console.error: {consoleErrors.Count}");
        foreach (var f in parseCompileFailures)
        {
            report.AppendLine("  PARSE/COMPILE " + f);
        }

        foreach (var t in runtimeThrows)
        {
            report.AppendLine("  THROW " + t);
        }

        foreach (var e in consoleErrors.Take(40))
        {
            report.AppendLine("  console.error " + e);
        }

        File.WriteAllText(Path.Combine(ghDir, "RUNTIME_REPORT.txt"), report.ToString());

        // Bounded, proven guard: the engine parses + compiles 100% of github's
        // bundles. (Runtime throws are documented in RUNTIME_REPORT.txt; full
        // runtime support is the open-ended goal this harness drives.)
        parseCompileFailures.Should().BeEmpty(report.ToString());
    }
}
