using AwesomeAssertions;
using Starling.IdlGen.Emit;
using Starling.IdlGen.Mapping;
using Starling.IdlGen.Merging;
using Starling.IdlGen.Overrides;
using Starling.IdlGen.Parsing;

namespace Starling.IdlGen.Tests;

// Enforces the per-interface coverage gates in coverage-gates.json. The gates
// ratchet coverage: a drop below an interface's floor fails the build, and a new
// target interface must be given a gate. Raise a floor after a coverage gain to
// lock it in.
[TestClass]
public sealed class CoverageGateTests
{
    private static (IReadOnlyList<InterfaceCoverage> perInterface, CoverageGates gates) Load()
    {
        string root = FindRepoRoot();
        string idlDir = Path.Combine(root, "testdata", "webref", "idl");
        var model = IdlMerger.Merge(Directory.EnumerateFiles(idlDir, "*.idl")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => IdlParser.Parse(File.ReadAllText(p))));
        var overrides = OverrideSet.Load(Path.Combine(root, "tools", "Starling.IdlGen", "overrides", "overrides.json"));
        new BindingsEmitter(model, new ClrMap(), overrides).Emit(BindingsEmitter.CoreDomInterfaces, out var stats);
        var gates = CoverageGates.Load(CoverageGates.DefaultPath(root));
        return (stats.PerInterface, gates);
    }

    [TestMethod]
    public void Every_target_interface_meets_its_coverage_gate()
    {
        var (perInterface, gates) = Load();

        foreach (var iface in perInterface)
        {
            gates.MinPercent.Should().ContainKey(iface.Interface,
                $"every target interface needs a gate in coverage-gates.json; {iface.Interface} is missing one");
            int min = gates.MinPercent[iface.Interface];
            iface.CoveragePercent.Should().BeGreaterThanOrEqualTo(min,
                $"{iface.Interface} coverage {iface.CoveragePercent:F1}% dropped below its gate of {min}%. " +
                "Restore the coverage, or lower the gate with a reason.");
        }
    }

    [TestMethod]
    public void Every_target_interface_binds_to_a_clr_type_and_prototype()
    {
        // An interface that has no CLR type or no prototype slot produces no
        // per-interface stats, so a target that silently fails to bind would be
        // invisible. Assert every target appears.
        var (perInterface, _) = Load();
        var present = perInterface.Select(i => i.Interface).ToHashSet(StringComparer.Ordinal);

        foreach (string iface in BindingsEmitter.CoreDomInterfaces)
        {
            present.Should().Contain(iface,
                $"{iface} is a target interface but produced no coverage — it has no CLR type or no prototype slot");
        }
    }

    [TestMethod]
    public void No_gate_is_set_for_an_interface_that_is_not_targeted()
    {
        // A stale gate (for a dropped interface) would never be checked; flag it.
        var (_, gates) = Load();
        var targets = BindingsEmitter.CoreDomInterfaces.ToHashSet(StringComparer.Ordinal);

        foreach (string gated in gates.MinPercent.Keys)
        {
            targets.Should().Contain(gated,
                $"coverage-gates.json has a gate for {gated}, which is not a target interface");
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Starling.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
