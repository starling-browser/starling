using AwesomeAssertions;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
namespace Starling.Js.Tests;

/// <summary>
/// Goal harness: the Starling JS engine should parse and compile github.com's
/// real script bundles. The bundles are a captured snapshot under
/// testdata/sites/github/assets/ (see that dir's README), so this runs offline
/// and fast — no live browser or network. github serves these as classic
/// scripts, so this mirrors the engine's classic-script path
/// (JsParser.ParseProgram -> JsCompiler.Compile). Red until the goal is met; the
/// failure message lists exactly which bundles still fail and why, and a full
/// report is written to the temp dir.
/// </summary>
[TestClass]
public class GithubCorpusTests
{
    private static string CorpusDir()
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

        return Path.Combine(dir.FullName, "testdata", "sites", "github", "assets");
    }

    [TestMethod]
    public void All_github_js_bundles_parse_and_compile()
    {
        var files = Directory.GetFiles(CorpusDir(), "*.js").OrderBy(f => f, StringComparer.Ordinal).ToArray();
        files.Should().NotBeEmpty("the github JS corpus must be present under testdata/sites/github/assets/");

        var failures = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            var source = File.ReadAllText(file);
            try
            {
                var program = new JsParser(source).ParseProgram();
                JsCompiler.Compile(program, name);
            }
            catch (Exception ex)
            {
                var msg = ex.Message.Split('\n')[0];
                failures.Add($"{name}: {ex.GetType().Name}: {msg}");
            }
        }

        var ok = files.Length - failures.Count;
        var report = $"github JS corpus: {ok}/{files.Length} parse+compile OK, {failures.Count} failing"
            + (failures.Count == 0 ? "" : "\n  " + string.Join("\n  ", failures));
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "github_corpus_report.txt"), report);

        failures.Should().BeEmpty(report);
    }
}
