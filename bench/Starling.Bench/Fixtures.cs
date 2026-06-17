namespace Starling.Bench;

internal static class Fixtures
{
    private static readonly Lazy<string> _repoRoot = new(LocateRepoRoot);

    public static string RepoRoot => _repoRoot.Value;

    public static string NginxHtmlPath => Path.Combine(RepoRoot, "testdata", "snapshots", "nginx.org", "index.html");
    public static string NginxCssPath => Path.Combine(RepoRoot, "testdata", "snapshots", "nginx.org", "css", "style_en.css");
    public static string GitHubSnapshotRoot => Path.Combine(RepoRoot, "testdata", "local-snapshots", "github.com");
    public static string GitHubHtmlPath => Path.Combine(GitHubSnapshotRoot, "index.html");
    public static string GitHubAssetsPath => Path.Combine(GitHubSnapshotRoot, "assets");
    public static bool GitHubSnapshotExists
        => File.Exists(GitHubHtmlPath) && Directory.Exists(GitHubAssetsPath);

    public static void RequireGitHubSnapshot()
    {
        if (!GitHubSnapshotExists)
        {
            throw new InvalidOperationException(
                "GitHub local snapshot missing. Run tools/snapshot-vendor/vendor-github-home.sh first.");
        }
    }

    private static string LocateRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null
            && !File.Exists(Path.Combine(dir.FullName, "Starling.slnx"))
            && !File.Exists(Path.Combine(dir.FullName, "Starling.sln")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate the Starling solution walking up from the bench binary.");
        }

        return dir.FullName;
    }

    /// <summary>
    /// A deep, mutation-friendly page: <paramref name="rows"/> sibling rows, each
    /// a small subtree (a heading + a paragraph) under a wrapper. Used by the
    /// incremental-layout bench to show that a localized edit costs O(change),
    /// not O(tree), as the tree grows. Row <c>i</c> carries <c>id="row-i"</c> so a
    /// bench can target a known node deep in the document.
    /// </summary>
    public static string ListHtml(int rows)
    {
        var sb = new System.Text.StringBuilder(rows * 96 + 64);
        sb.Append("<!doctype html><html><body><main>");
        for (var i = 0; i < rows; i++)
        {
            sb.Append("<section id=\"row-").Append(i).Append("\"><h3>Item ").Append(i)
              .Append("</h3><p>Row ").Append(i)
              .Append(" has a short paragraph of body text that wraps.</p></section>");
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    // A flex-rooted page (body is a flex container, so its single child is a
    // flex item) with many text-heavy sibling sections and one deep "status"
    // line. This mirrors the animations demo, whose CPU spike came from a
    // per-frame script writing a deep text node: the dirty path reaches the flex
    // root, whose auto cross-size measurement re-measured the whole subtree. The
    // status line carries id="status" for the per-frame text mutation.
    public static string FlexRootedHtml(int rows)
    {
        var sb = new System.Text.StringBuilder(rows * 96 + 128);
        sb.Append("<!doctype html><html><body><main id=\"page\"><header><h1>Showcase headline</h1>");
        sb.Append("<p>An intro paragraph that wraps across a couple of lines here.</p></header>");
        for (var i = 0; i < rows; i++)
        {
            sb.Append("<section id=\"row-").Append(i).Append("\"><h3>Item ").Append(i)
              .Append("</h3><p>Row ").Append(i)
              .Append(" has a short paragraph of body text that wraps.</p></section>");
        }
        sb.Append("<div class=\"status-wrap\"><p id=\"status\">idle 0 ms</p></div>");
        sb.Append("<footer>Rendered by the layout engine.</footer></main></body></html>");
        return sb.ToString();
    }

    public const string FlexRootCss = """
        body { display: flex; justify-content: center; margin: 0; }
        #page { width: 100%; max-width: 880px; }
        h1, h2, h3 { color: #111; }
        """;

    public const string TinyHtml = "<!doctype html><html><body><p>Hello, world.</p></body></html>";

    public const string SmallHtml = """
        <!doctype html>
        <html>
          <head><title>small</title></head>
          <body>
            <h1>Heading</h1>
            <p>One paragraph with <em>inline</em> markup and a <a href="/x">link</a>.</p>
            <ul><li>a</li><li>b</li><li>c</li></ul>
          </body>
        </html>
        """;

    public const string TinyCss = "body { color: #333; font-size: 16px; margin: 0; }";

    public const string SmallCss = """
        body { margin: 0; padding: 0; color: #222; font-family: system-ui, sans-serif; }
        h1, h2, h3 { color: #111; }
        h1 { font-size: 2em; }
        a { color: rgb(0, 102, 204); text-decoration: underline; }
        ul { padding-left: 1.5em; }
        li + li { margin-top: 0.25em; }
        p > em { font-style: italic; }
        .container { max-width: 960px; margin: 0 auto; padding: 1em; }
        """;

    // ---- Focused layout + raster fixtures (benchmark dashboard) ----
    // Each isolates one cost driver so a benchmark can attribute time to it.

    /// <summary>Many short paragraphs — text shaping and measurement dominate.</summary>
    public static string TextHeavyParagraphs(int paragraphs)
    {
        var sb = new System.Text.StringBuilder(paragraphs * 120 + 64);
        sb.Append("<!doctype html><html><body><main>");
        for (var i = 0; i < paragraphs; i++)
        {
            sb.Append("<p>Paragraph ").Append(i)
              .Append(" has several words of body text that the engine must shape and wrap across the available width of the line box.</p>");
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    /// <summary>Nested flex containers — exercises flex sizing depth.</summary>
    public static string NestedFlex(int depth)
    {
        var sb = new System.Text.StringBuilder(depth * 48 + 96);
        sb.Append("<!doctype html><html><body>");
        for (var i = 0; i < depth; i++)
        {
            sb.Append("<div class=\"f\">");
        }

        sb.Append("<span>leaf</span>");
        for (var i = 0; i < depth; i++)
        {
            sb.Append("</div>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    public const string NestedFlexCss = ".f { display: flex; flex-direction: row; padding: 2px; }";

    /// <summary>A grid of bordered boxes — border raster cost.</summary>
    public static string ManyBorders(int boxes)
    {
        var sb = new System.Text.StringBuilder(boxes * 28 + 96);
        sb.Append("<!doctype html><html><body>");
        for (var i = 0; i < boxes; i++)
        {
            sb.Append("<div class=\"b\"></div>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    public const string ManyBordersCss = """
        body { margin: 0; }
        .b { display: inline-block; width: 40px; height: 40px; margin: 2px;
             border: 3px solid #336699; background: #eef; }
        """;

    // ---- Animation fixtures (AnimationBench) ----
    // A page of identical boxes, each running one CSS @keyframes animation. The
    // bench ticks the clock and re-renders each frame; pair this HTML with one
    // of the two CSS variants to pick the per-frame cost shape.

    /// <summary>A row of <paramref name="boxes"/> animated boxes — each carries
    /// the <c>anim</c> class so the cascade starts a <c>@keyframes</c> animation
    /// on it. Pair with <see cref="TransformAnimCss"/> (composite-only) or
    /// <see cref="LayoutAnimCss"/> (layout-affecting).</summary>
    public static string AnimatedBoxesHtml(int boxes)
    {
        var sb = new System.Text.StringBuilder(boxes * 44 + 96);
        sb.Append("<!doctype html><html><body><main>");
        for (var i = 0; i < boxes; i++)
        {
            sb.Append("<div class=\"anim\" id=\"box-").Append(i).Append("\">Box ").Append(i).Append("</div>");
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    // transform + opacity animate → composite-only. The animated value never
    // changes box geometry, so incremental layout leaves the tree alone and only
    // the paint (display-list) stage re-runs each frame.
    public const string TransformAnimCss = """
        body { margin: 0; }
        .anim { width: 120px; height: 40px; background: #4080c0;
                animation: slide 1s linear infinite; }
        @keyframes slide {
          from { transform: translateX(0px); opacity: 1; }
          to   { transform: translateX(200px); opacity: 0.2; }
        }
        """;

    // width + margin animate → layout-affecting. Each frame marks the animated
    // elements dirty (AnimationEngine.HasLayoutAffectingProperty) and relays
    // them out, so the box-tree pass re-runs too.
    public const string LayoutAnimCss = """
        body { margin: 0; }
        .anim { width: 120px; height: 40px; background: #c08040;
                animation: grow 1s linear infinite; }
        @keyframes grow {
          from { width: 80px; margin-left: 0px; }
          to   { width: 320px; margin-left: 120px; }
        }
        """;

    /// <summary>Solid-color blocks — fill raster cost with no text or borders.</summary>
    public static string SolidBackgrounds(int boxes)
    {
        var sb = new System.Text.StringBuilder(boxes * 24 + 96);
        sb.Append("<!doctype html><html><body>");
        for (var i = 0; i < boxes; i++)
        {
            sb.Append("<div class=\"s\"></div>");
        }

        sb.Append("</body></html>");
        return sb.ToString();
    }

    public const string SolidBackgroundsCss = """
        body { margin: 0; }
        .s { display: inline-block; width: 48px; height: 48px; background: #4080c0; }
        """;

    // ---- Compositor fixtures (CompositorBench) ----
    // A grid of promoted "cards", each its own compositor layer. `transform`
    // forces a stacking context (CSS-Transforms-1 §5), so LayerTreeBuilder gives
    // every card a layer with a picture cache. That makes the warm-vs-cold cache
    // split meaningful: a warm frame re-blits each cached layer, a cold frame
    // re-rasterizes all of them through the backend.

    /// <summary>A grid of <paramref name="cards"/> promoted cards (each a layer
    /// via <c>transform</c>), every card carrying a heading + paragraph of text
    /// so a cold re-raster pays real text + fill cost per layer.</summary>
    public static string PromotedCards(int cards)
    {
        var sb = new System.Text.StringBuilder(cards * 96 + 96);
        sb.Append("<!doctype html><html><body><main>");
        for (var i = 0; i < cards; i++)
        {
            sb.Append("<div class=\"card\" id=\"card-").Append(i).Append("\"><h3>Card ").Append(i)
              .Append("</h3><p>Card ").Append(i)
              .Append(" body text that the layer must shape and fill when its cache is cold.</p></div>");
        }

        sb.Append("</main></body></html>");
        return sb.ToString();
    }

    public const string PromotedCardsCss = """
        body { margin: 0; }
        .card { display: inline-block; width: 200px; height: 120px; margin: 6px;
                padding: 8px; background: #eef; border: 1px solid #336699;
                transform: translateZ(0); }
        .card h3 { margin: 0 0 6px; font-size: 16px; color: #224; }
        .card p { margin: 0; font-size: 13px; color: #446; }
        """;
}
