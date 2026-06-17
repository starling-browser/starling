using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Starling.Bench.Replay;

namespace Starling.Bench;

internal static class Program
{
    public static int Main(string[] args)
    {
        // Custom run-modes dispatched before the BenchmarkDotNet switcher: the
        // frame-replay harness and its baseline-compare tool.
        if (args.Length > 0 && args[0] == "replay")
        {
            return ReplayProgram.Run(args[1..]);
        }

        if (args.Length > 0 && args[0] == "compare")
        {
            return ReplayCompare.Run(args[1..]);
        }

        if (args.Length > 0 && args[0] == "report")
        {
            return ReplayReport.Run(args[1..]);
        }

        if (args.Length > 0 && args[0] == "animtrace")
        {
            return AnimationTraceProgram.Run(args[1..]);
        }

        if (args.Length > 0 && args[0] == "github-style-smoke")
        {
            return GitHubStyleSmoke.Run();
        }

        // Add tail-latency columns so the BenchmarkDotNet tables carry p95/p90,
        // not just mean/median — the percentiles that predict frame budget misses.
        var config = DefaultConfig.Instance
            .AddColumn(StatisticColumn.P95)
            .AddColumn(StatisticColumn.P90);
        BenchmarkSwitcher.FromTypes(BenchmarkTypes(args)).Run(args, config);
        return 0;
    }

    private static Type[] BenchmarkTypes(string[] args)
    {
        var includeGitHub = Fixtures.GitHubSnapshotExists || args.Any(arg =>
            arg.Contains("github", StringComparison.OrdinalIgnoreCase));

        return typeof(Program).Assembly.GetTypes()
            .Where(static type => type.GetMethods()
                .Any(static method => method.GetCustomAttributes(typeof(BenchmarkAttribute), inherit: false).Length > 0))
            .Where(type => includeGitHub || !type.Name.Contains("GitHub", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }
}
