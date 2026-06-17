using AwesomeAssertions;
using Starling.IdlGen.Emit;
using Starling.IdlGen.Mapping;
using Starling.IdlGen.Merging;
using Starling.IdlGen.Parsing;

namespace Starling.IdlGen.Tests;

[TestClass]
public class UnionEmitterTests
{
    private static string EmitUnions(out int count)
    {
        string idlDir = Path.Combine(FindRepoRoot(), "testdata", "webref", "idl");
        var model = IdlMerger.Merge(Directory.EnumerateFiles(idlDir, "*.idl")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => IdlParser.Parse(File.ReadAllText(p))));
        return new UnionEmitter(model, new TypeMapper(model), new ClrMap())
            .Emit(BindingsEmitter.CoreDomInterfaces, out count);
    }

    [TestMethod]
    public void Emits_node_or_string_union()
    {
        string code = EmitUnions(out _);
        // (Node or DOMString) from the ParentNode / ChildNode mixins.
        code.Should().Contain("public union NodeOrString(Node, string);");
    }

    [TestMethod]
    public void Skips_unions_whose_case_types_do_not_exist_yet()
    {
        // A union over an interface with no Starling DOM CLR type (TrustedType)
        // cannot compile, so it is left out. Unions over dictionaries, enums, and
        // callbacks DO compile now that those types are generated.
        string code = EmitUnions(out _);
        code.Should().NotContain("TrustedType");
    }

    [TestMethod]
    public void Emits_union_over_dictionary_once_dictionaries_exist()
    {
        string code = EmitUnions(out _);
        code.Should().Contain("public union AddEventListenerOptionsOrBoolean(AddEventListenerOptions, bool);");
    }

    [TestMethod]
    public void Emit_is_deterministic()
    {
        EmitUnions(out int a).Should().Be(EmitUnions(out int b));
        a.Should().Be(b);
    }

    [TestMethod]
    public void Matches_committed_baseline()
    {
        string baseline = Path.Combine(FindRepoRoot(),
            "src", "Starling.Bindings", "Generated", "Unions.g.cs");
        File.Exists(baseline).Should().BeTrue();
        EmitUnions(out _).ReplaceLineEndings("\n")
            .Should().Be(File.ReadAllText(baseline).ReplaceLineEndings("\n"));
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
