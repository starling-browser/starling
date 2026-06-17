using System.Globalization;
using System.Text.Json;

namespace Starling.Bench.Replay;

/// <summary>
/// The <c>compare</c> run-mode: load two replay result files and flag phases
/// where the candidate regressed past a threshold. Exits non-zero on any
/// regression so a continuous-integration job can gate on it. Refuses to compare
/// mismatched pages or scopes, so an incremental run is never measured against a
/// full one by accident.
/// </summary>
internal static class ReplayCompare
{
    public static int Run(string[] args)
    {
        var threshold = 0.10;
        var positional = new List<string>();
        try
        {
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] == "--threshold")
                {
                    threshold = double.Parse(args[++i], CultureInfo.InvariantCulture);
                }
                else
                {
                    positional.Add(args[i]);
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or IndexOutOfRangeException)
        {
            Console.Error.WriteLine("invalid --threshold value.");
            return 2;
        }

        if (positional.Count != 2)
        {
            Console.Error.WriteLine("usage: compare <baseline.json> <candidate.json> [--threshold 0.10]");
            return 2;
        }

        var baseline = Load(positional[0]);
        var candidate = Load(positional[1]);
        if (baseline is null || candidate is null)
        {
            return 2;
        }

        if (baseline.Page != candidate.Page || baseline.ScopeLabel != candidate.ScopeLabel)
        {
            Console.Error.WriteLine(
                $"refusing to compare mismatched runs: baseline {baseline.Page}/{baseline.ScopeLabel} "
                + $"vs candidate {candidate.Page}/{candidate.ScopeLabel}");
            return 2;
        }

        Console.WriteLine($"Compare {baseline.Page} ({baseline.ScopeLabel})   threshold +{threshold.ToString("P0", CultureInfo.InvariantCulture)}");
        Console.WriteLine($"{"phase",-13}{"base p95",11}{"cand p95",11}{"delta",10}  flag");

        var regressed = false;
        foreach (var key in new[] { "frame", "style_anim", "layout", "display_list", "raster" })
        {
            if (!baseline.Phases.TryGetValue(key, out var b))
            {
                continue;
            }

            if (!candidate.Phases.TryGetValue(key, out var c))
            {
                continue;
            }

            // Percentage thresholds with small absolute floors so a near-zero
            // phase (e.g. style_anim with no animations) does not flag on noise.
            var meanBad = c.MeanMs > b.MeanMs * (1 + threshold) && c.MeanMs - b.MeanMs > 0.05;
            var p95Bad = c.P95Ms > b.P95Ms * (1 + threshold) && c.P95Ms - b.P95Ms > 0.05;
            var allocBad = c.MeanAllocBytes > b.MeanAllocBytes * (1 + threshold) && c.MeanAllocBytes - b.MeanAllocBytes > 1024;
            var bad = meanBad || p95Bad || allocBad;
            regressed |= bad;

            var delta = b.P95Ms > 0 ? (c.P95Ms - b.P95Ms) / b.P95Ms : 0;
            Console.WriteLine(
                $"{key,-13}{Ms(b.P95Ms),11}{Ms(c.P95Ms),11}{delta.ToString("P1", CultureInfo.InvariantCulture),10}  "
                + (bad ? "REGRESSION" : "ok"));
        }

        Console.WriteLine();
        Console.WriteLine(regressed ? "REGRESSION detected." : "no regression.");
        return regressed ? 1 : 0;
    }

    private static ReplayResult? Load(string path)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"not found: {path}");
            return null;
        }
        var result = JsonSerializer.Deserialize(File.ReadAllText(path), ReplayJsonContext.Default.ReplayResult);
        if (result is null)
        {
            Console.Error.WriteLine($"could not parse: {path}");
        }

        return result;
    }

    private static string Ms(double v) => v.ToString("F3", CultureInfo.InvariantCulture);
}
