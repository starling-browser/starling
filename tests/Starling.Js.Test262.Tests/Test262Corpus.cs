namespace Starling.Js.Test262.Tests;

/// <summary>
/// Shared Test262 corpus discovery + enumeration + the env-var config block,
/// used by both the Starling.Js runner (<see cref="Test262Tests"/>) and the Jint
/// runner (<see cref="JintTest262Tests"/>) so the two engines measure against the
/// identical file set with identical configuration. Factored out of
/// <see cref="Test262Tests"/> verbatim — the Starling.Js path is unchanged.
/// </summary>
internal static class Test262Corpus
{
    /// <summary>The shared env-var configuration the two runners read.</summary>
    public readonly record struct Config(string[] Dirs, string? Filter, int Max, int TimeoutMs);

    /// <summary>Read the STARLING_TEST262_* env vars (DIRS/FILTER/MAX/TIMEOUT_MS).
    /// FLOOR is read separately by each runner since the two have different
    /// defaults (Starling.Js ratchets a baseline; Jint is report-only).</summary>
    public static Config ReadConfig()
    {
        var dirs = (Environment.GetEnvironmentVariable("STARLING_TEST262_DIRS") ?? "language")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filter = Environment.GetEnvironmentVariable("STARLING_TEST262_FILTER");
        var max = int.TryParse(Environment.GetEnvironmentVariable("STARLING_TEST262_MAX"), out var m) ? m : 0;
        var timeout = int.TryParse(Environment.GetEnvironmentVariable("STARLING_TEST262_TIMEOUT_MS"), out var t) ? t : 5_000;
        return new Config(dirs, filter, max, timeout);
    }

    public static IEnumerable<string> EnumerateTests(string testDir, string[] dirs, string? filter, int max)
    {
        var count = 0;
        foreach (var sub in dirs)
        {
            var path = Path.Combine(testDir, sub);
            if (!Directory.Exists(path))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*.js", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
            {
                // _FIXTURE.js files are imported by module tests, never run directly.
                if (file.EndsWith("_FIXTURE.js", StringComparison.Ordinal))
                {
                    continue;
                }

                if (filter is not null && file.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                yield return file;
                if (max > 0 && ++count >= max)
                {
                    yield break;
                }
            }
        }
    }

    public static string Category(string relFile)
    {
        // relFile like "test/language/expressions/addition/foo.js" → "language/expressions"
        var parts = relFile.Replace('\\', '/').Split('/');
        // strip leading "test"
        var i = parts.Length > 0 && parts[0] == "test" ? 1 : 0;
        return parts.Length > i + 1 ? parts[i] + "/" + parts[i + 1] : (parts.Length > i ? parts[i] : relFile);
    }

    /// <summary>Walk up from the test binary to the repo and locate
    /// testdata/test262 (gitignored, fetched separately).</summary>
    public static string? LocateCorpus()
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 12 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "testdata", "test262");
            if (Directory.Exists(Path.Combine(candidate, "test")))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }
        return null;
    }
}
