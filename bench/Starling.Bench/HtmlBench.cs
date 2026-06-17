using BenchmarkDotNet.Attributes;
using Starling.Html;
using Starling.Html.Tokenizer;

namespace Starling.Bench;

// Anchored against the M1 exit (`html5lib` tokenizer 100% / tree-construction
// ≥ 95%) and the §C.5 zero-allocation budget for the tokenizer per character.
// `TokenizerOnly_*` separates the lex stage from tree construction so a
// regression in either is attributable.
[MemoryDiagnoser]
public class HtmlBench
{
    private string _nginxHtml = string.Empty;

    [GlobalSetup]
    public void Setup() => _nginxHtml = File.ReadAllText(Fixtures.NginxHtmlPath);

    [Benchmark]
    public int TokenizerOnly_Tiny() => TokenizeAndCount(Fixtures.TinyHtml);

    [Benchmark]
    public int TokenizerOnly_Small() => TokenizeAndCount(Fixtures.SmallHtml);

    [Benchmark]
    public int TokenizerOnly_NginxOrg() => TokenizeAndCount(_nginxHtml);

    [Benchmark]
    public int Parse_Tiny() => HtmlParser.Parse(Fixtures.TinyHtml).TextContent.Length;

    [Benchmark]
    public int Parse_Small() => HtmlParser.Parse(Fixtures.SmallHtml).TextContent.Length;

    [Benchmark(Baseline = true)]
    public int Parse_NginxOrg() => HtmlParser.Parse(_nginxHtml).TextContent.Length;

    private static int TokenizeAndCount(string source)
    {
        var tk = new HtmlTokenizer();
        tk.Feed(source);
        tk.EndOfInput();
        var count = 0;
        while (tk.ReadToken() is not null)
        {
            count++;
        }

        return count;
    }
}
