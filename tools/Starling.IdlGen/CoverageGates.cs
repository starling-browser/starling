using System.Text.Json;

namespace Starling.IdlGen;

// Per-interface minimum coverage percentages, read from coverage-gates.json. A
// gate fails the build (the CoverageGateTests test and the `coverage` command)
// when an interface's coverage drops below its floor, so coverage can only
// ratchet up. Raise a gate after a coverage gain locks it in.
public sealed class CoverageGates
{
    public IReadOnlyDictionary<string, int> MinPercent { get; }

    private CoverageGates(IReadOnlyDictionary<string, int> minPercent) => MinPercent = minPercent;

    public static string DefaultPath(string repoRoot) =>
        Path.Combine(repoRoot, "tools", "Starling.IdlGen", "coverage-gates.json");

    public static CoverageGates Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var gates = new Dictionary<string, int>(StringComparer.Ordinal);
        if (doc.RootElement.TryGetProperty("gates", out var g))
        {
            foreach (var p in g.EnumerateObject())
            {
                gates[p.Name] = p.Value.GetInt32();
            }
        }

        return new CoverageGates(gates);
    }
}
