// SPDX-License-Identifier: Apache-2.0

using AwesomeAssertions;
using Starling.IdlGen.Emit;
using Starling.IdlGen.Merging;
using Starling.IdlGen.Overrides;
using Starling.IdlGen.Parsing;

namespace Starling.IdlGen.Tests;

[TestClass]
public sealed class SurfaceManifestEmitterTests
{
    [TestMethod]
    public void Generated_surface_manifest_matches_committed_baseline()
    {
        string baselinePath = Path.Combine(FindRepoRoot(), "testdata", "webref", "core-dom-surface.json");
        File.Exists(baselinePath).Should().BeTrue("the generated surface manifest must be committed");

        string expected = File.ReadAllText(baselinePath).ReplaceLineEndings("\n");
        string actual = EmitManifest().ReplaceLineEndings("\n");
        actual.Should().Be(expected);
    }

    [TestMethod]
    public void Surface_manifest_tracks_html_document_get_elements_by_name()
    {
        string manifest = EmitManifest();
        manifest.Should().Contain("\"interface\": \"Document\"");
        manifest.Should().Contain("\"name\": \"getElementsByName\"");
        manifest.Should().Contain("\"requiredArguments\": 1");
        manifest.Should().Contain("\"required\": true");
    }

    private static string EmitManifest()
    {
        string root = FindRepoRoot();
        string idlDir = Path.Combine(root, "testdata", "webref", "idl");
        var model = IdlMerger.Merge(Directory.EnumerateFiles(idlDir, "*.idl")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => IdlParser.Parse(File.ReadAllText(p))));
        var overrides = OverrideSet.Load(Path.Combine(root, "tools", "Starling.IdlGen", "overrides", "overrides.json"));
        return new SurfaceManifestEmitter(model, overrides).Emit(BindingsEmitter.CoreDomInterfaces);
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
