using AwesomeAssertions;
using Starling.IdlGen.Merging;
using Starling.IdlGen.Model;
using Starling.IdlGen.Parsing;

namespace Starling.IdlGen.Tests;

[TestClass]
public class IdlMergerTests
{
    private static WebIdlModel Merge(params string[] sources) =>
        IdlMerger.Merge(sources.Select(IdlParser.Parse));

    [TestMethod]
    public void Partials_merge_into_main_interface()
    {
        var model = Merge(
            "interface Document { readonly attribute DOMString url; };",
            "partial interface Document { readonly attribute DOMString cookie; };");

        var doc = model.Interfaces["Document"];
        doc.Members.OfType<IdlAttribute>().Select(a => a.Name)
            .Should().BeEquivalentTo("url", "cookie");
    }

    [TestMethod]
    public void Includes_copies_mixin_members()
    {
        var model = Merge(
            "interface Document {};",
            "interface mixin ParentNode { readonly attribute long childElementCount; };",
            "Document includes ParentNode;");

        model.Interfaces["Document"].Members.OfType<IdlAttribute>().Single()
            .Name.Should().Be("childElementCount");
        model.UnresolvedIncludes.Should().BeEmpty();
    }

    [TestMethod]
    public void Unresolved_include_is_reported()
    {
        var model = Merge(
            "interface Window {};",
            "Window includes GlobalEventHandlers;");
        model.UnresolvedIncludes.Should().ContainSingle().Which.Should().Contain("GlobalEventHandlers");
    }

    [TestMethod]
    public void Dictionary_partials_merge()
    {
        var model = Merge(
            "dictionary Init { boolean a = false; };",
            "partial dictionary Init { boolean b = true; };");
        model.Dictionaries["Init"].Members.Select(m => m.Name).Should().BeEquivalentTo("a", "b");
    }

    [TestMethod]
    public void Typedef_resolves_through_chain()
    {
        var model = Merge(
            "typedef DOMString A; typedef A B;",
            "interface I { undefined f(B x); };");

        var arg = model.Interfaces["I"].Members.OfType<IdlOperation>().Single().Arguments.Single();
        model.ResolveTypedef(arg.Type).Name.Should().Be("DOMString");
    }

    [TestMethod]
    public void Typedef_resolution_keeps_nullability()
    {
        var model = Merge("typedef long Coord;", "interface I { undefined f(Coord? x); };");
        var arg = model.Interfaces["I"].Members.OfType<IdlOperation>().Single().Arguments.Single();
        var resolved = model.ResolveTypedef(arg.Type);
        resolved.Name.Should().Be("long");
        resolved.Nullable.Should().BeTrue();
    }

    [TestMethod]
    public void Real_dom_idl_merges_with_known_external_includes_only()
    {
        // The vendored core DOM merges cleanly. Any unresolved include must be a
        // genuinely external mixin (defined in a spec we have not vendored), not
        // a parse or merge bug.
        string idlDir = Path.Combine(FindRepoRoot(), "testdata", "webref", "idl");
        var docs = Directory.EnumerateFiles(idlDir, "*.idl")
            .Select(p => IdlParser.Parse(File.ReadAllText(p)))
            .ToList();

        var model = IdlMerger.Merge(docs);

        model.Interfaces.Should().ContainKey("Node");
        model.Interfaces.Should().ContainKey("Element");
        model.Interfaces.Should().ContainKey("Document");

        // Node's tree mixins are in dom.idl, so these resolve.
        var element = model.Interfaces["Element"];
        element.Members.OfType<IdlOperation>().Select(o => o.Name)
            .Should().Contain("append");   // from ParentNode mixin via includes
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
