namespace Starling.Wpt.Tests;

/// <summary>
/// Web-platform-tests corpus discovery, enumeration, and env-var config — the
/// WPT analogue of <c>Test262Corpus</c>. The suite is NOT vendored; fetch it
/// with <c>tools/fetch-wpt.sh</c> (pinned SHA → testdata/wpt/suite/, gitignored).
/// </summary>
internal static class WptCorpus
{
    /// <summary>Env-var config the runner reads.</summary>
    public readonly record struct Config(string[] Dirs, string? Filter, int Max, int TimeoutMs);

    /// <summary>Read STARLING_WPT_* (DIRS/FILTER/MAX/TIMEOUT_MS). Defaults cover
    /// the subset the fetch script vendors by default.</summary>
    public static Config ReadConfig()
    {
        var dirs = (Environment.GetEnvironmentVariable("STARLING_WPT_DIRS") ?? "dom,css,url")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filter = Environment.GetEnvironmentVariable("STARLING_WPT_FILTER");
        var max = int.TryParse(Environment.GetEnvironmentVariable("STARLING_WPT_MAX"), out var m) ? m : 0;
        var timeout = int.TryParse(Environment.GetEnvironmentVariable("STARLING_WPT_TIMEOUT_MS"), out var t) ? t : 10_000;
        return new Config(dirs, filter, max, timeout);
    }

    /// <summary>Enumerate testharness test files (relative URL paths, '/'-joined)
    /// under each requested subdir. Skips helper trees and non-testharness file
    /// shapes (manual tests, reftest references, .js sources that need wrapper
    /// generation we don't do yet).</summary>
    public static IEnumerable<string> EnumerateTests(string suiteDir, string[] dirs, string? filter, int max)
    {
        var skip = LoadSkipList(suiteDir);
        var count = 0;
        foreach (var sub in dirs)
        {
            var path = Path.Combine(suiteDir, sub);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*.html", SearchOption.AllDirectories)
                         .OrderBy(p => p, StringComparer.Ordinal))
            {
                var rel = Path.GetRelativePath(suiteDir, file).Replace('\\', '/');
                if (!IsCandidateTest(rel))
                {
                    continue;
                }

                if (filter is not null && rel.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (skip.Any(s => rel.Contains(s, StringComparison.Ordinal)))
                {
                    continue;
                }

                yield return rel;
                if (max > 0 && ++count >= max)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>Curated skip-list at <c>testdata/wpt/skip.txt</c> (committed, one
    /// path substring per line, '#' comments). Holds files that crash the test
    /// host uncatchably — e.g. a native stack overflow in the parser/layout that
    /// .NET can't trap, which would otherwise abort the whole run. The Test262
    /// harness keeps an equivalent list; entries are triage backlog, not a
    /// permanent exclusion.</summary>
    private static string[] LoadSkipList(string suiteDir)
    {
        var path = Path.GetFullPath(Path.Combine(suiteDir, "..", "skip.txt"));
        if (!File.Exists(path))
        {
            return Array.Empty<string>();
        }

        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();
    }

    /// <summary>True for files that plausibly run under testharness.js. Helper
    /// directories and reftest/manual shapes are excluded so they don't pollute
    /// the denominator (a reftest produces no testharness output → "no result").</summary>
    private static bool IsCandidateTest(string rel)
    {
        if (rel.Contains("/resources/", StringComparison.Ordinal)
            || rel.Contains("/support/", StringComparison.Ordinal)
            || rel.Contains("/common/", StringComparison.Ordinal)
            || rel.StartsWith("common/", StringComparison.Ordinal)
            || rel.StartsWith("resources/", StringComparison.Ordinal))
        {
            return false;
        }

        var name = rel.AsSpan(rel.LastIndexOf('/') + 1);
        if (name.EndsWith("-manual.html", StringComparison.Ordinal))
        {
            return false;
        }

        if (name.EndsWith("-ref.html", StringComparison.Ordinal))
        {
            return false;   // reftest reference
        }

        if (name.EndsWith("-notref.html", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    /// <summary>Two leading path segments, e.g. "dom/nodes", for the per-area
    /// breakdown in the summary.</summary>
    public static string Category(string rel)
    {
        var parts = rel.Split('/');
        return parts.Length > 1 ? parts[0] + "/" + parts[1] : parts[0];
    }

    /// <summary>Walk up from the test binary to locate testdata/wpt/suite (the
    /// gitignored checkout). Confirmed by the presence of resources/testharness.js.</summary>
    public static string? LocateSuite()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "testdata", "wpt", "suite");
            if (File.Exists(Path.Combine(candidate, "resources", "testharness.js")))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }
}
