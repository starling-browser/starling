using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using Starling.Css;
using Starling.Css.Cascade;
using Starling.Css.Parser;
using Starling.Dom;
using Starling.Html;
using Starling.Layout;
using Starling.Paint;
using Starling.Paint.DisplayList;
using LayoutRect = Starling.Layout.Rect;

namespace Starling.Bench;

// Opt-in benchmark for a locally fetched github.com home page. Fetch the
// snapshot with tools/snapshot-vendor/vendor-github-home.sh before running this
// class. It keeps the live site's selector shape local and repeatable.
[MemoryDiagnoser]
public class GitHubStyleBench
{
    private const float DefaultFontSize = 16f;
    private static readonly Size Viewport = new(1280, 720);

    private string _html = string.Empty;
    private Document _doc = null!;
    private readonly List<string> _cssTexts = [];
    private readonly Dictionary<string, StyleSheet> _externalSheets = new(StringComparer.Ordinal);
    private readonly Dictionary<Element, StyleSheet> _inlineSheets = [];

    [GlobalSetup]
    public void Setup()
    {
        Fixtures.RequireGitHubSnapshot();

        _html = File.ReadAllText(Fixtures.GitHubHtmlPath);
        _doc = HtmlParser.Parse(_html);
        LoadExternalSheets(_doc);
        LoadInlineSheets(_doc);

        if (_cssTexts.Count == 0)
        {
            throw new InvalidOperationException(
                "GitHub local snapshot has no CSS. Re-run tools/snapshot-vendor/vendor-github-home.sh.");
        }
    }

    [Benchmark]
    public int ParseCss_GitHubHome()
    {
        var rules = 0;
        foreach (var css in _cssTexts)
        {
            rules += CssParser.ParseStyleSheet(css).Rules.Count;
        }

        return rules;
    }

    [Benchmark]
    public int BuildStyleEngine_GitHubHome()
    {
        var style = CreateStyleEngine(_doc, useCachedInlineSheets: true);
        return style.ReferencedAttributeNames.Count;
    }

    [Benchmark]
    public int PrecomputeTree_GitHubHome()
        => PrecomputeTree_GitHubHome(loggerFactory: null);

    public int PrecomputeTree_GitHubHome(ILoggerFactory? loggerFactory)
    {
        var style = CreateStyleEngine(_doc, useCachedInlineSheets: true, loggerFactory);
        var cache = new CascadeCache();
        style.PrecomputeTree(_doc.DocumentElement!, cache);
        return cache.Count;
    }

    [Benchmark]
    public double LayoutDocument_GitHubHome()
        => LayoutDocument_GitHubHome(loggerFactory: null);

    public double LayoutDocument_GitHubHome(ILoggerFactory? loggerFactory)
    {
        var style = CreateStyleEngine(_doc, useCachedInlineSheets: true, loggerFactory: null);
        var root = new LayoutEngine(style, loggerFactory: loggerFactory).LayoutDocument(_doc, Viewport);
        return root.Frame.Height;
    }

    [Benchmark]
    public int Render_GitHubHome_HtmlToDisplayList()
    {
        var doc = HtmlParser.Parse(_html);
        var style = CreateStyleEngine(doc, useCachedInlineSheets: false);
        var layout = new LayoutEngine(style).LayoutDocument(doc, Viewport);
        var displayList = new DisplayListBuilder().Build(layout);
        return displayList.Items.Count;
    }

    [Benchmark]
    public int Render_GitHubHome_GpuTextureCompositor()
    {
        var doc = HtmlParser.Parse(_html);
        var style = CreateStyleEngine(doc, useCachedInlineSheets: false);
        var layout = new LayoutEngine(style).LayoutDocument(doc, Viewport);
        using var renderer = new CompositedPageRenderer();
        using var bitmap = renderer.Render(
            layout,
            new LayoutRect(0, 0, Viewport.Width, Viewport.Height));
        return bitmap.Width;
    }

    private void LoadExternalSheets(Document doc)
    {
        foreach (var link in Elements(doc))
        {
            if (!IsStylesheetLink(link))
            {
                continue;
            }

            var href = link.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            var path = SnapshotPathFromHref(href);
            if (path is null || !File.Exists(path) || _externalSheets.ContainsKey(href))
            {
                continue;
            }

            var css = File.ReadAllText(path);
            _cssTexts.Add(css);
            _externalSheets[href] = CssParser.ParseStyleSheet(css);
        }
    }

    private void LoadInlineSheets(Document doc)
    {
        foreach (var element in Elements(doc))
        {
            if (!string.Equals(element.LocalName, "style", StringComparison.Ordinal))
            {
                continue;
            }

            var source = element.TextContent;
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            _cssTexts.Add(source);
            _inlineSheets[element] = CssParser.ParseStyleSheet(source);
        }
    }

    private StyleEngine CreateStyleEngine(Document doc, bool useCachedInlineSheets, ILoggerFactory? loggerFactory = null)
    {
        var style = new StyleEngine(loggerFactory: loggerFactory);
        style.MediaContext = style.MediaContext with
        {
            ViewportWidthPx = Viewport.Width,
            ViewportHeightPx = Viewport.Height,
        };
        style.AddStyleSheet(CssParser.ParseStyleSheet($"body {{ font-size: {DefaultFontSize}px; }}", StyleOrigin.User));
        AddAuthorSheets(doc, style, useCachedInlineSheets);
        return style;
    }

    private void AddAuthorSheets(Document doc, StyleEngine style, bool useCachedInlineSheets)
    {
        foreach (var element in Elements(doc))
        {
            if (string.Equals(element.LocalName, "style", StringComparison.Ordinal))
            {
                var source = element.TextContent;
                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                if (useCachedInlineSheets && _inlineSheets.TryGetValue(element, out var cached))
                {
                    style.AddStyleSheet(cached);
                }
                else
                {
                    style.AddStyleSheet(CssParser.ParseStyleSheet(source));
                }
            }
            else if (IsStylesheetLink(element))
            {
                var href = element.GetAttribute("href");
                if (href is not null && _externalSheets.TryGetValue(href, out var sheet))
                {
                    style.AddStyleSheet(sheet);
                }
            }
        }
    }

    private static bool IsStylesheetLink(Element element)
    {
        if (!string.Equals(element.LocalName, "link", StringComparison.Ordinal))
        {
            return false;
        }

        var rel = element.GetAttribute("rel");
        if (rel is null)
        {
            return false;
        }

        foreach (var token in rel.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token, "stylesheet", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string? SnapshotPathFromHref(string href)
    {
        var end = href.IndexOfAny(['?', '#']);
        if (end >= 0)
        {
            href = href[..end];
        }

        href = href.TrimStart('/');
        if (!href.StartsWith("assets/", StringComparison.Ordinal))
        {
            return null;
        }

        return Path.Combine(Fixtures.GitHubSnapshotRoot, href.Replace('/', Path.DirectorySeparatorChar));
    }

    private static IEnumerable<Element> Elements(Node root)
    {
        if (root is Element element)
        {
            yield return element;
        }

        foreach (var child in root.ChildNodes)
        {
            foreach (var nested in Elements(child))
            {
                yield return nested;
            }
        }
    }
}
