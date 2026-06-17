using System.Text;

namespace Starling.HtmlParserBench;

// Fixture loader for the parser comparison. Standalone (does not share
// Starling.Bench's Fixtures) because this project is deliberately scoped to
// Starling.Html + Starling.Dom + AngleSharp only.
internal static class Fixtures
{
    private static readonly Lazy<string> RepoRootLazy = new(LocateRepoRoot);

    public static string RepoRoot => RepoRootLazy.Value;

    // ~6.4 KB real-world page (committed snapshot).
    public static string NginxHtmlPath =>
        Path.Combine(RepoRoot, "testdata", "snapshots", "nginx.org", "index.html");

    // ~567 KB real-world page (committed). This is the headline "large page"
    // case the comparison exists to answer.
    public static string GitHubHtmlPath =>
        Path.Combine(RepoRoot, "testdata", "sites", "github", "index.html");

    public const string Tiny =
        "<!doctype html><html><body><p>Hello, world.</p></body></html>";

    // ~1 MB synthetic document: a deep, attribute-bearing tree of repeated
    // sections. Mirrors the 1 MB budget target in browser-plan/04_HTML_PARSING.md
    // that no committed real-world fixture reaches.
    public static string SyntheticLarge(int targetBytes)
    {
        var sb = new StringBuilder(targetBytes + 256);
        sb.Append("<!doctype html><html><head><title>synthetic</title></head><body><main>");
        var i = 0;
        while (sb.Length < targetBytes)
        {
            sb.Append("<section id=\"row-").Append(i).Append("\" class=\"item even-")
              .Append(i & 1)
              .Append("\"><h3>Item ").Append(i)
              .Append("</h3><p>Row ").Append(i)
              .Append(" has a short paragraph of body text with <a href=\"/x/")
              .Append(i)
              .Append("\">a link</a> and <em>inline</em> markup that the tree builder must nest.</p></section>");
            i++;
        }
        sb.Append("</main></body></html>");
        return sb.ToString();
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
            throw new InvalidOperationException(
                "Could not locate the Starling solution walking up from the bench binary.");
        }

        return dir.FullName;
    }
}
