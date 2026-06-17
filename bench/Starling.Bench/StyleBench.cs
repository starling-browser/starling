using BenchmarkDotNet.Attributes;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Dom;
using Starling.Html;

namespace Starling.Bench;

// CSS cascade against the §C.5 "0 allocations per element (reuse ComputedStyle
// slots)" budget and the M1 cascade exit. `Compute_FullTree_*` exercises the
// PrecomputeTree path that the engine actually uses; `Compute_SingleElement_*`
// isolates per-element matching cost.
[MemoryDiagnoser]
public class StyleBench
{
    private Document _nginxDoc = null!;
    private StyleSheet _nginxSheet = null!;

    [GlobalSetup]
    public void Setup()
    {
        _nginxDoc = HtmlParser.Parse(File.ReadAllText(Fixtures.NginxHtmlPath));
        _nginxSheet = CssParser.ParseStyleSheet(File.ReadAllText(Fixtures.NginxCssPath));
    }

    [Benchmark]
    public int PrecomputeTree_NginxOrg()
    {
        var engine = new StyleEngine();
        engine.AddStyleSheet(_nginxSheet);
        var cache = new CascadeCache();
        engine.PrecomputeTree(_nginxDoc.DocumentElement!, cache);
        return cache.Count;
    }

    [Benchmark]
    public int Compute_SingleElement_NginxBody()
    {
        var engine = new StyleEngine();
        engine.AddStyleSheet(_nginxSheet);
        var body = FirstByTag(_nginxDoc, "body")!;
        return engine.Compute(body).GetType().GetHashCode();
    }

    private static Element? FirstByTag(Node root, string tag)
    {
        foreach (var child in root.ChildNodes)
        {
            if (child is Element e && string.Equals(e.LocalName, tag, StringComparison.OrdinalIgnoreCase))
            {
                return e;
            }

            var nested = FirstByTag(child, tag);
            if (nested is not null)
            {
                return nested;
            }
        }
        return null;
    }
}
