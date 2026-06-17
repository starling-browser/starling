using AwesomeAssertions;
using System.Text.RegularExpressions;
using Starling.IdlGen.Emit;
using Starling.IdlGen.Mapping;
using Starling.IdlGen.Merging;
using Starling.IdlGen.Overrides;
using Starling.IdlGen.Parsing;

namespace Starling.IdlGen.Tests;

[TestClass]
public class BindingsEmitterTests
{
    private static string EmitCoreDom()
    {
        string root = FindRepoRoot();
        string idlDir = Path.Combine(root, "testdata", "webref", "idl");
        var model = IdlMerger.Merge(Directory.EnumerateFiles(idlDir, "*.idl")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => IdlParser.Parse(File.ReadAllText(p))));
        var overrides = OverrideSet.Load(Path.Combine(root, "tools", "Starling.IdlGen", "overrides", "overrides.json"));
        return new BindingsEmitter(model, new ClrMap(), overrides).Emit(BindingsEmitter.CoreDomInterfaces, out _);
    }

    [TestMethod]
    public void Generated_file_matches_committed_baseline()
    {
        // The committed generated file is the golden baseline. Re-running the
        // emitter must reproduce it exactly. If this fails, run
        //   dotnet run --project tools/Starling.IdlGen -- emit
        // and review the diff before committing.
        string baselinePath = Path.Combine(FindRepoRoot(),
            "src", "Starling.Bindings", "Generated", "CoreDomBindings.g.cs");
        File.Exists(baselinePath).Should().BeTrue("the generated baseline must be committed");

        string expected = File.ReadAllText(baselinePath).ReplaceLineEndings("\n");
        string actual = EmitCoreDom().ReplaceLineEndings("\n");
        actual.Should().Be(expected);
    }

    [TestMethod]
    public void Emit_is_deterministic()
    {
        EmitCoreDom().Should().Be(EmitCoreDom());
    }

    [TestMethod]
    public void Generated_accessors_use_the_real_engine_api()
    {
        string code = EmitCoreDom();
        code.Should().Contain("EventTargetBinding.DefineAccessor(realm, proto, \"nodeValue\"");
        code.Should().Contain("DomWrappers.UnwrapAs<Element>(thisV)");
        code.Should().Contain("JsValue.String(v)");
        // A writable attribute emits a setter.
        code.Should().Contain("h.Id = JsValue.ToStringValue");
        // Override layer: tagName and nodeName use custom getter code, not the
        // mechanical mapping.
        code.Should().Contain("NodeBindings.NormalizeNodeName");
        code.Should().Contain("ToUpperInvariant");
    }

    [TestMethod]
    public void Generated_operations_route_through_IdlMarshal()
    {
        string code = EmitCoreDom();
        var methods = Regex.Matches(
            code,
            "EventTargetBinding\\.DefineMethod\\(realm, proto, \\\"(?<name>[^\\\"]+)\\\", \\(thisV, args\\) =>\\n        \\{\\n(?<body>.*?)\\n        \\}, length:",
            RegexOptions.Singleline);

        methods.Count.Should().BeGreaterThan(0);
        foreach (Match method in methods)
        {
            string name = method.Groups["name"].Value;
            string body = method.Groups["body"].Value;
            body.Should().Contain("IdlMarshal.", $"{name} must use the shared Web IDL marshalling path");
        }
    }

    [TestMethod]
    public void Generated_unsigned_long_operations_use_the_uint_marshaller()
    {
        string code = EmitCoreDom();
        // CharacterData.substringData(unsigned long, unsigned long) is a mechanical
        // binding: both args route through the unsigned long marshaller.
        code.Should().Contain("IdlMarshal.RequireUnsignedLong(realm, args, 0, \"substringData\", 2)");
        code.Should().Contain("IdlMarshal.RequireUnsignedLong(realm, args, 1, \"substringData\", 2)");
        // The mechanical path translates a DOM-raised exception, like the dispatch
        // path, so IndexSizeError reaches JS as a DOMException.
        code.Should().Contain("catch (Starling.Dom.DomException ex) { throw IdlMarshal.Translate(realm, ex); }");
    }

    [TestMethod]
    public void Nullable_string_operation_argument_marshals_as_nullable_string()
    {
        string code = EmitCoreDom();
        // Node.lookupPrefix(DOMString? namespace) is mechanical: the CLR
        // Node.LookupPrefix(string?) takes a nullable string. A nullable DOMString?
        // argument routes through RequireNullableString so a JS null becomes a C#
        // null rather than the string "null", while staying a required argument.
        code.Should().Contain("IdlMarshal.RequireNullableString(realm, args, 0, \"lookupPrefix\", 1)");
        code.Should().Contain("IdlMarshal.RequireNullableString(realm, args, 0, \"lookupNamespaceURI\", 1)");
        code.Should().Contain("IdlMarshal.RequireNullableString(realm, args, 0, \"isDefaultNamespace\", 1)");
        // A required (non-nullable) DOMString argument still uses RequireString.
        code.Should().Contain("IdlMarshal.RequireString(realm, args, 0, \"appendData\", 1)");
    }

    [TestMethod]
    public void Generated_install_all_calls_every_target_interface()
    {
        string code = EmitCoreDom();
        code.Should().Contain("internal static void InstallAll(JsRealm realm)");
        foreach (string iface in BindingsEmitter.CoreDomInterfaces)
        {
            code.Should().Contain($"Install{iface}(realm);");
        }
    }

    [TestMethod]
    public void Runtime_uses_generated_install_all()
    {
        string sourcePath = Path.Combine(FindRepoRoot(), "src", "Starling.Bindings", "NodeBindings.cs");
        string source = File.ReadAllText(sourcePath);

        source.Should().Contain("Generated.CoreDomBindingsGenerated.InstallAll(realm);");
        source.Should().NotContain("Generated.CoreDomBindingsGenerated.InstallNode(realm);");
    }

    [TestMethod]
    public void Add_layer_injects_verbatim_binding_code()
    {
        string idlDir = Path.Combine(FindRepoRoot(), "testdata", "webref", "idl");
        var model = IdlMerger.Merge(Directory.EnumerateFiles(idlDir, "*.idl")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => IdlParser.Parse(File.ReadAllText(p))));
        var overrides = OverrideSet.Parse(
            """{ "add": { "Node": ["EventTargetBinding.DefineMethod(realm, proto, \"__hostExtra\", (thisV, _) => JsValue.Null, length: 0);"] } }""");

        string code = new BindingsEmitter(model, new ClrMap(), overrides).Emit(BindingsEmitter.CoreDomInterfaces, out _);

        code.Should().Contain("DefineMethod(realm, proto, \"__hostExtra\"");
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
