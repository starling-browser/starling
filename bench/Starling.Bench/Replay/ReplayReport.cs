using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Starling.Bench.Replay;

/// <summary>
/// The <c>report</c> run-mode: gather the latest frame-replay JSON results and the
/// BenchmarkDotNet GitHub-markdown artifacts, then render one human-friendly
/// <c>benchmarks.md</c> dashboard. Every number carries its scope label so a
/// reader always knows which phase it measures.
/// </summary>
internal static class ReplayReport
{
    public static int Run(string[] args)
    {
        var resultsRoot = Path.Combine(Fixtures.RepoRoot, "bench", "results");
        var bdnDir = Path.Combine(Fixtures.RepoRoot, "BenchmarkDotNet.Artifacts", "results");
        string? outPath = null;
        string? dateOverride = null;

        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--results": resultsRoot = args[++i]; break;
                    case "--bdn": bdnDir = args[++i]; break;
                    case "--out": outPath = args[++i]; break;
                    case "--date": dateOverride = args[++i]; break;
                    default:
                        Console.Error.WriteLine($"unknown option: {args[i]}");
                        Console.Error.WriteLine("usage: report [--results <dir>] [--bdn <dir>] [--date yyyy-MM-dd] [--out <path>]");
                        return 2;
                }
            }
        }
        catch (IndexOutOfRangeException)
        {
            Console.Error.WriteLine("missing option value.");
            return 2;
        }

        var dateDir = ResolveDateDir(resultsRoot, dateOverride);
        var replay = LoadReplayResults(dateDir);
        var bdn = LoadBdnTables(bdnDir);

        var md = Render(replay, bdn, dateDir, bdnDir);
        // Default to bench/benchmarks.md (a tracked, published file) rather than
        // bench/results/ (gitignored generated output).
        outPath ??= Path.Combine(Fixtures.RepoRoot, "bench", "benchmarks.md");
        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        File.WriteAllText(outPath, md);

        Console.WriteLine($"replay results: {replay.Count}   subsystem benches: {bdn.Count}");
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }

    // The newest yyyy-MM-dd folder under bench/results, unless one is named.
    private static string? ResolveDateDir(string resultsRoot, string? dateOverride)
    {
        if (dateOverride is not null)
        {
            return Path.Combine(resultsRoot, dateOverride);
        }

        if (!Directory.Exists(resultsRoot))
        {
            return null;
        }

        var dirs = Directory.GetDirectories(resultsRoot);
        Array.Sort(dirs, StringComparer.Ordinal); // yyyy-MM-dd sorts chronologically
        return dirs.Length > 0 ? dirs[^1] : null;
    }

    private static List<ReplayResult> LoadReplayResults(string? dateDir)
    {
        var list = new List<ReplayResult>();
        if (dateDir is null || !Directory.Exists(dateDir))
        {
            return list;
        }

        foreach (var file in Directory.GetFiles(dateDir, "*.json"))
        {
            try
            {
                var r = JsonSerializer.Deserialize(File.ReadAllText(file), ReplayJsonContext.Default.ReplayResult);
                if (r is not null)
                {
                    list.Add(r);
                }
            }
            catch (JsonException)
            {
                // Not a replay result file — skip it.
            }
        }
        // Group by page, then full before incremental, raster before no-raster.
        list.Sort((a, b) =>
        {
            var p = string.CompareOrdinal(a.Page, b.Page);
            if (p != 0)
            {
                return p;
            }

            var s = string.CompareOrdinal(a.ScopeLabel, b.ScopeLabel);
            if (s != 0)
            {
                return s;
            }

            return b.RasterEnabled.CompareTo(a.RasterEnabled);
        });
        return list;
    }

    // bench name -> its markdown table lines, from each *-report-github.md.
    private static List<(string Name, string Table)> LoadBdnTables(string bdnDir)
    {
        var list = new List<(string, string)>();
        if (!Directory.Exists(bdnDir))
        {
            return list;
        }

        foreach (var file in Directory.GetFiles(bdnDir, "*-report-github.md"))
        {
            var name = Path.GetFileNameWithoutExtension(file)
                .Replace("-report-github", "", StringComparison.Ordinal)
                .Replace("Starling.Bench.", "", StringComparison.Ordinal);
            var table = ExtractTable(File.ReadAllLines(file));
            // Skip stale or failed runs: a successful BenchmarkDotNet table always
            // carries a time unit. An all-NA table (a run that errored) has none.
            if (table.Length > 0 && HasMeasurements(table))
            {
                list.Add((name, table));
            }
            else if (table.Length > 0)
            {
                Console.WriteLine($"  skipped {name}: no successful measurements (stale or failed run).");
            }
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
        return list;
    }

    // A real BenchmarkDotNet result row reports a time unit; a failed all-NA
    // table has none.
    private static bool HasMeasurements(string table) =>
        table.Contains(" ms", StringComparison.Ordinal)
        || table.Contains(" us", StringComparison.Ordinal)
        || table.Contains(" μs", StringComparison.Ordinal)
        || table.Contains(" ns", StringComparison.Ordinal);

    // The markdown table is the run of lines starting with '|'.
    private static string ExtractTable(string[] lines)
    {
        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith('|'))
            {
                sb.AppendLine(line.TrimEnd());
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static string Render(
        List<ReplayResult> replay,
        List<(string Name, string Table)> bdn,
        string? dateDir,
        string bdnDir)
    {
        var sb = new StringBuilder(8192);
        var now = DateTime.UtcNow.ToString("u", CultureInfo.InvariantCulture);

        sb.AppendLine("# Starling benchmarks");
        sb.AppendLine();
        sb.AppendLine("<!-- Generated by `dotnet run --project bench/Starling.Bench -c Release -- report`. Do not edit by hand. -->");
        sb.AppendLine();
        sb.AppendLine($"_Generated {now}._");
        sb.AppendLine();
        sb.AppendLine("Every number says which phase it measures. A frame number is not a layout number, "
            + "and a layout number is not a raster number. For browser feel, p95 and p99 matter more than the mean.");
        sb.AppendLine();
        sb.AppendLine("**Scope labels:** `full-frame` = animation tick + layout + display-list + raster. "
            + "`layout` = box tree + text measure. `raster` = draw the paint list to pixels (pure-CPU backend). "
            + "`style` = the cascade. Frame budgets: 16.67 ms at 60 frames per second, 8.33 ms at 120.");
        sb.AppendLine();

        RenderReplaySection(sb, replay, dateDir);
        RenderSubsystemSection(sb, bdn, bdnDir);

        sb.AppendLine("## Layer 3 — Industry / browser benchmarks");
        sb.AppendLine();
        sb.AppendLine("Not run yet. Speedometer, MotionMark, and JetStream need a working interactive "
            + "JavaScript path. The Starling JS engine does not compile today, so these are blocked. "
            + "Revisit once it builds.");
        sb.AppendLine();

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("Regenerate with:");
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine("# end-to-end frame replay (writes the JSON this reads)");
        sb.AppendLine("dotnet run --project bench/Starling.Bench -c Release -- replay flex-status --frames 300 --full");
        sb.AppendLine("dotnet run --project bench/Starling.Bench -c Release -- replay flex-status --frames 300");
        sb.AppendLine("# subsystem benches (writes the BenchmarkDotNet artifacts this reads)");
        sb.AppendLine("dotnet run --project bench/Starling.Bench -c Release -- --filter \"*RasterBench*\"");
        sb.AppendLine("# then rebuild this page");
        sb.AppendLine("dotnet run --project bench/Starling.Bench -c Release -- report");
        sb.AppendLine("```");
        return sb.ToString();
    }

    private static void RenderReplaySection(StringBuilder sb, List<ReplayResult> replay, string? dateDir)
    {
        sb.AppendLine("## Layer 2 — End-to-end frame replay");
        sb.AppendLine();
        if (replay.Count == 0)
        {
            sb.AppendLine("_No replay results found. Run `replay <page>` first._");
            sb.AppendLine();
            return;
        }

        var dateLabel = dateDir is null ? "" : $" (from `{MakeRelative(dateDir)}`)";
        sb.AppendLine($"Each page is driven through N synthetic frames{dateLabel}. "
            + "The `full` row rebuilds the whole page every frame. The `incremental` row reuses the "
            + "retained box tree. Scope: full-frame.");
        sb.AppendLine();

        string? page = null;
        foreach (var r in replay)
        {
            if (r.Page != page)
            {
                page = r.Page;
                sb.AppendLine($"### {page}");
                sb.AppendLine();
                sb.AppendLine($"_{r.FrameCount} frames ({r.WarmupFrames} warmup), {Ms(r.FrameDeltaMs)} ms/frame, backend `{r.PaintBackend}`._");
                sb.AppendLine();
                sb.AppendLine("| Scope | frame p50 | frame p95 | frame p99 | drop&gt;16.7 | drop&gt;8.3 | alloc/frame | layout p95 | raster p95 | text meas/frame | shape cache hit |");
                sb.AppendLine("|---|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|");
            }

            var frame = r.Phases.GetValueOrDefault("frame");
            var layout = r.Phases.TryGetValue("layout", out var l) ? Ms(l.P95Ms) : "—";
            var raster = r.Phases.TryGetValue("raster", out var ra) ? Ms(ra.P95Ms) : "—";
            var scope = r.ScopeLabel + (r.RasterEnabled ? "" : " (no raster)");
            sb.AppendLine(
                $"| {scope} | {Ms(frame.P50Ms)} | **{Ms(frame.P95Ms)}** | {Ms(frame.P99Ms)} | "
                + $"{frame.DroppedOver16_67ms} | {frame.DroppedOver8_33ms} | {Bytes(frame.MeanAllocBytes)} | "
                + $"{layout} | {raster} | {F1(r.TextMeasure.MeanShapeCalls)} | {Pct(r.TextMeasure.ShapeCacheHitRate)} |");

            // Close each page block after its last row by peeking is awkward; a
            // trailing blank line per row is harmless and keeps tables separated.
            if (IsLastOfPage(replay, r))
            {
                sb.AppendLine();
            }
        }
    }

    private static bool IsLastOfPage(List<ReplayResult> all, ReplayResult r)
    {
        var idx = all.IndexOf(r);
        return idx == all.Count - 1 || all[idx + 1].Page != r.Page;
    }

    private static void RenderSubsystemSection(StringBuilder sb, List<(string Name, string Table)> bdn, string bdnDir)
    {
        sb.AppendLine("## Layer 1 — Subsystem benchmarks (BenchmarkDotNet)");
        sb.AppendLine();
        if (bdn.Count == 0)
        {
            sb.AppendLine($"_No BenchmarkDotNet artifacts found under `{MakeRelative(bdnDir)}`. "
                + "Run a bench (e.g. `--filter \"*RasterBench*\"`) first._");
            sb.AppendLine();
            return;
        }
        sb.AppendLine("Microbenchmarks per subsystem, straight from BenchmarkDotNet. "
            + "`PaintBench` builds the display list, `RasterBench` draws it — together they split "
            + "\"build the paint list\" from \"draw it to pixels\".");
        sb.AppendLine();
        foreach (var (name, table) in bdn)
        {
            sb.AppendLine($"### {name}");
            sb.AppendLine();
            sb.AppendLine(table);
            sb.AppendLine();
        }
    }

    private static string MakeRelative(string path)
    {
        var root = Fixtures.RepoRoot;
        return path.StartsWith(root, StringComparison.Ordinal)
            ? path[(root.Length + 1)..].Replace('\\', '/')
            : path;
    }

    private static string Ms(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static string F1(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
    private static string Pct(double v) => v.ToString("P0", CultureInfo.InvariantCulture);

    private static string Bytes(double v) => v switch
    {
        >= 1024 * 1024 => (v / (1024 * 1024)).ToString("F1", CultureInfo.InvariantCulture) + " MB",
        >= 1024 => (v / 1024).ToString("F1", CultureInfo.InvariantCulture) + " KB",
        _ => v.ToString("F0", CultureInfo.InvariantCulture) + " B",
    };
}
