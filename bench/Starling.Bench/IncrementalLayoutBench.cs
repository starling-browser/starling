using BenchmarkDotNet.Attributes;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Dom;
using Starling.Html;
using Starling.Layout;
using Starling.Layout.Incremental;
using Starling.Layout.Text;

namespace Starling.Bench;

// Incremental layout vs. full rebuild on a synthetic page that scales with
// [Rows]. Each frame applies one small DOM change and re-lays-out. The baseline
// is the current behaviour — a full box-tree rebuild + block/inline pass — and
// the incremental variant reuses the retained tree, recomputing only the dirty
// subtree. The thesis the bench checks: incremental cost is ~O(change) while the
// full rebuild is ~O(tree), so the gap widens with [Rows].
//
// Both sides share one pre-built StyleEngine, so this isolates the layout pass
// (the engine path additionally avoids rebuilding the StyleEngine per frame —
// Phase 2f — which this bench deliberately does not charge to either side).
[MemoryDiagnoser]
public class IncrementalLayoutBench
{
    [Params(100, 1000)]
    public int Rows;

    private static readonly Size Viewport = new(1024, 768);
    private readonly ITextMeasurer _measurer = DefaultTextMeasurer.Instance;

    private Document _doc = null!;
    private StyleEngine _style = null!;
    private LayoutSession _session = null!;
    private Text _targetText = null!;
    private Element _targetEl = null!;
    private Element _scratchParent = null!;
    private Element _scratchChild = null!;
    private int _frame;

    [GlobalSetup]
    public void Setup()
    {
        _doc = HtmlParser.Parse(Fixtures.ListHtml(Rows));
        _doc.RecordLayoutMutations = true;
        _style = new StyleEngine();
        _style.AddStyleSheet(CssParser.ParseStyleSheet(Fixtures.SmallCss));
        _session = new LayoutSession(_style);
        _session.Layout(_doc, Viewport, _measurer, nowMs: null); // warm: full build + stamp

        var lastRow = _doc.GetElementById($"row-{Rows - 1}")!;
        _targetEl = lastRow;
        _targetText = FirstText(lastRow)!;
        _scratchParent = FirstElement(_doc.DocumentElement!, "main")!;
        _scratchChild = MakeRow(_doc, "scratch");
    }

    // ---- text change (the Animations-demo status-line case) ------------------

    [Benchmark(Baseline = true)]
    public double Full_TextChange()
    {
        _targetText.Data = NextLabel();
        _doc.DrainLayoutMutations(); // the full path ignores the batch; keep it from growing
        return new LayoutEngine(_style).LayoutDocument(_doc, Viewport).Frame.Height;
    }

    [Benchmark]
    public double Incremental_TextChange()
    {
        _targetText.Data = NextLabel();
        return _session.Layout(_doc, Viewport, _measurer, nowMs: null).Frame.Height;
    }

    // ---- layout-relevant attribute change ------------------------------------

    [Benchmark]
    public double Full_AttributeChange()
    {
        _targetEl.SetAttribute("style", NextPadding());
        _doc.DrainLayoutMutations();
        return new LayoutEngine(_style).LayoutDocument(_doc, Viewport).Frame.Height;
    }

    [Benchmark]
    public double Incremental_AttributeChange()
    {
        _targetEl.SetAttribute("style", NextPadding());
        return _session.Layout(_doc, Viewport, _measurer, nowMs: null).Frame.Height;
    }

    // ---- structural insert/remove (alternating, net-bounded) -----------------
    //
    // Per-child splice (§3a) + localized anonymous re-wrap (§3b): the changed
    // child is built/removed and the unchanged siblings' laid-out subtrees are
    // reused by identity, so build + layout + text-shaping is O(change). The
    // parent's subtree is still re-cascaded (to catch positional :nth-child-style
    // restyles soundly), so the residual cost is cascade-bound (~1/3 of a full
    // rebuild here), not the near-zero of a text/attr edit. :has()/:empty force a
    // full rebuild instead (a structural change can restyle outside the parent).

    [Benchmark]
    public double Full_StructuralToggle()
    {
        ToggleScratch();
        _doc.DrainLayoutMutations();
        return new LayoutEngine(_style).LayoutDocument(_doc, Viewport).Frame.Height;
    }

    [Benchmark]
    public double Incremental_StructuralToggle()
    {
        ToggleScratch();
        return _session.Layout(_doc, Viewport, _measurer, nowMs: null).Frame.Height;
    }

    // ---- a frame with no mutation (pure reuse-traversal cost) ----------------

    [Benchmark]
    public double Full_NoChange()
        => new LayoutEngine(_style).LayoutDocument(_doc, Viewport).Frame.Height;

    [Benchmark]
    public double Incremental_NoChange()
        => _session.Layout(_doc, Viewport, _measurer, nowMs: null).Frame.Height;

    // ---- helpers -------------------------------------------------------------

    private string NextLabel() => (_frame++ & 1) == 0 ? "Item alpha that wraps" : "Item bravo that wraps";
    private string NextPadding() => (_frame++ & 1) == 0 ? "padding:0px" : "padding:8px";

    private void ToggleScratch()
    {
        if (_scratchChild.ParentNode is not null)
        {
            _scratchParent.RemoveChild(_scratchChild);
        }
        else
        {
            _scratchParent.AppendChild(_scratchChild);
        }
    }

    private static Element MakeRow(Document doc, string id)
    {
        var section = doc.CreateElement("section");
        section.SetAttribute("id", id);
        var h3 = doc.CreateElement("h3");
        h3.AppendChild(doc.CreateTextNode("Scratch row"));
        var p = doc.CreateElement("p");
        p.AppendChild(doc.CreateTextNode("Scratch paragraph that wraps onto a line or two."));
        section.AppendChild(h3);
        section.AppendChild(p);
        return section;
    }

    private static Text? FirstText(Node node)
    {
        for (var c = node.FirstChild; c is not null; c = c.NextSibling)
        {
            if (c is Text t && !string.IsNullOrWhiteSpace(t.Data))
            {
                return t;
            }

            if (FirstText(c) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static Element? FirstElement(Element root, string localName)
    {
        if (string.Equals(root.LocalName, localName, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        for (var c = root.FirstChild; c is not null; c = c.NextSibling)
        {
            if (c is Element e && FirstElement(e, localName) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}

// The animations-demo CPU-spike shape: a flex-rooted page (body is a flex
// container) where a per-frame script rewrites one deep text node — the status
// line. Because the root is a flex item with an auto cross-size, every relayout
// runs an intrinsic-size measurement pass over the item; the fix lets clean
// sibling subtrees replay their cached measured height instead of re-measuring
// every text run. Full_TextChange (always re-measures the whole page) is the
// baseline; Incremental_TextChange should be far cheaper and ~flat in [Rows].
[MemoryDiagnoser]
public class IncrementalFlexRootBench
{
    [Params(50, 500)]
    public int Rows;

    private static readonly Size Viewport = new(1024, 768);
    private readonly ITextMeasurer _measurer = DefaultTextMeasurer.Instance;

    private Document _doc = null!;
    private StyleEngine _style = null!;
    private LayoutSession _session = null!;
    private Text _status = null!;
    private int _frame;

    [GlobalSetup]
    public void Setup()
    {
        _doc = HtmlParser.Parse(Fixtures.FlexRootedHtml(Rows));
        _doc.RecordLayoutMutations = true;
        _style = new StyleEngine();
        _style.AddStyleSheet(CssParser.ParseStyleSheet(Fixtures.FlexRootCss));
        _session = new LayoutSession(_style);
        _session.Layout(_doc, Viewport, _measurer, nowMs: null); // warm full build

        _status = (Text)_doc.GetElementById("status")!.FirstChild!;
    }

    [Benchmark(Baseline = true)]
    public double Full_TextChange()
    {
        _status.Data = NextLabel();
        _doc.DrainLayoutMutations();
        return new LayoutEngine(_style).LayoutDocument(_doc, Viewport).Frame.Height;
    }

    [Benchmark]
    public double Incremental_TextChange()
    {
        _status.Data = NextLabel();
        return _session.Layout(_doc, Viewport, _measurer, nowMs: null).Frame.Height;
    }

    private string NextLabel() => (_frame++ & 1) == 0 ? "running 16 ms" : "running 32 ms";
}

// The same comparison on a real-world page (the offline nginx.org snapshot the
// golden gate uses), so the synthetic scaling result is anchored to a page with
// genuine cascade + inline complexity.
[MemoryDiagnoser]
public class IncrementalLayoutNginxBench
{
    private static readonly Size Viewport = new(1024, 768);
    private readonly ITextMeasurer _measurer = DefaultTextMeasurer.Instance;

    private Document _doc = null!;
    private StyleEngine _style = null!;
    private LayoutSession _session = null!;
    private Text _targetText = null!;
    private int _frame;

    [GlobalSetup]
    public void Setup()
    {
        _doc = HtmlParser.Parse(File.ReadAllText(Fixtures.NginxHtmlPath));
        _doc.RecordLayoutMutations = true;
        _style = new StyleEngine();
        _style.AddStyleSheet(CssParser.ParseStyleSheet(File.ReadAllText(Fixtures.NginxCssPath)));
        _session = new LayoutSession(_style);
        _session.Layout(_doc, Viewport, _measurer, nowMs: null);
        // Target rendered body text — a node that actually became a text box, so
        // the incremental path reconciles it rather than falling back (head /
        // script / style text has no box and isn't in the layout map).
        var body = FirstElement(_doc.DocumentElement!, "body") ?? _doc.DocumentElement!;
        _targetText = FirstRenderedText(body)
            ?? throw new InvalidOperationException("no rendered text node found in the nginx fixture body");
    }

    [Benchmark(Baseline = true)]
    public double Full_TextChange()
    {
        _targetText.Data = (_frame++ & 1) == 0 ? "nginx alpha" : "nginx bravo";
        _doc.DrainLayoutMutations();
        return new LayoutEngine(_style).LayoutDocument(_doc, Viewport).Frame.Height;
    }

    [Benchmark]
    public double Incremental_TextChange()
    {
        _targetText.Data = (_frame++ & 1) == 0 ? "nginx alpha" : "nginx bravo";
        return _session.Layout(_doc, Viewport, _measurer, nowMs: null).Frame.Height;
    }

    // First non-whitespace text node that is actually rendered — descends only
    // through elements that produce boxes (skips script/style/title/etc.).
    private static Text? FirstRenderedText(Node node)
    {
        for (var c = node.FirstChild; c is not null; c = c.NextSibling)
        {
            if (c is Text t && !string.IsNullOrWhiteSpace(t.Data))
            {
                return t;
            }

            if (c is Element e && IsRendered(e) && FirstRenderedText(e) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private static bool IsRendered(Element e) => e.LocalName.ToLowerInvariant() switch
    {
        "script" or "style" or "title" or "head" or "meta" or "link"
            or "noscript" or "template" => false,
        _ => true,
    };

    private static Element? FirstElement(Element root, string localName)
    {
        if (string.Equals(root.LocalName, localName, StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        for (var c = root.FirstChild; c is not null; c = c.NextSibling)
        {
            if (c is Element e && FirstElement(e, localName) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
