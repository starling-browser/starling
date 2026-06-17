// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Starling.Bindings;
using Starling.Dom;
using Starling.Js.Bytecode;
using Starling.Js.Parse;
using Starling.Js.Runtime;

namespace Starling.BindingSurface.Tests;

// Runtime IDL harness over the surface manifest.
//
// This loads testdata/webref/core-dom-surface.json (the backend-neutral surface
// manifest written by the generator) and, for each required member, builds a
// live fixture object in the Starling JS engine and asserts the runtime exposes
// the member with the shape the manifest describes:
//   - the property lives on the correct prototype object,
//   - it is an accessor (getter) versus a method versus a data/value property,
//   - methods expose the required-argument count as their .length,
//   - the Node nodeType constants exist with the right value on both the
//     constructor and the prototype,
//   - the prototype chain of an instance follows IDL inheritance.
//
// Known gaps are tracked in testdata/webref/surface-expected-failures.json. The
// harness asserts all required members are present and correctly shaped EXCEPT
// the listed gaps, and it fails when a new gap appears or a listed gap turns out
// to be already fixed, so the gap list cannot rot.
//
// The harness runs over the Starling JS engine with the Starling DOM bindings
// installed through WindowBinding.Install, matching the path the existing
// IdlSurfaceManifestTests uses. The generated installers are a separate concern
// owned elsewhere, so they are not layered in here.
[TestClass]
public sealed class IdlRuntimeHarnessTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // IDL inheritance for the eleven target interfaces, taken from dom.idl.
    // Each entry maps an interface to the chain of prototype objects an instance
    // should walk through, ending at Object.prototype.
    private static readonly Dictionary<string, string[]> InheritanceChain =
        new(StringComparer.Ordinal)
        {
            ["EventTarget"] = ["EventTarget"],
            ["Node"] = ["Node", "EventTarget"],
            ["Document"] = ["Document", "Node", "EventTarget"],
            ["DocumentType"] = ["DocumentType", "Node", "EventTarget"],
            ["DocumentFragment"] = ["DocumentFragment", "Node", "EventTarget"],
            ["Element"] = ["Element", "Node", "EventTarget"],
            ["Attr"] = ["Attr", "Node", "EventTarget"],
            ["CharacterData"] = ["CharacterData", "Node", "EventTarget"],
            ["Text"] = ["Text", "CharacterData", "Node", "EventTarget"],
            ["ProcessingInstruction"] = ["ProcessingInstruction", "CharacterData", "Node", "EventTarget"],
            ["Comment"] = ["Comment", "CharacterData", "Node", "EventTarget"],
        };

    // A JavaScript expression that builds a live instance of each interface, used
    // both to find the prototype that owns a member and to walk the chain.
    //
    // Node, CharacterData, and EventTarget are abstract in this DOM, so they have
    // no direct instance. The fixture for those uses a concrete subtype, and the
    // FixtureLeaf table below records which concrete interface the instance really
    // is so the prototype-chain check expects the right leaf.
    private static readonly Dictionary<string, string> FixtureExpression =
        new(StringComparer.Ordinal)
        {
            ["EventTarget"] = "new EventTarget()",
            ["Node"] = "document.createElement('div')",
            ["Document"] = "document",
            ["DocumentType"] = "document.implementation.createDocumentType('html', '', '')",
            ["DocumentFragment"] = "document.createDocumentFragment()",
            ["Element"] = "document.createElement('div')",
            ["Attr"] = "(function(){ var e = document.createElement('div'); e.setAttribute('data-x', 'v'); return e.getAttributeNode('data-x'); })()",
            ["CharacterData"] = "document.createTextNode('x')",
            ["Text"] = "document.createTextNode('x')",
            ["ProcessingInstruction"] = "document.createProcessingInstruction('t', 'd')",
            ["Comment"] = "document.createComment('c')",
        };

    // The concrete leaf interface the fixture instance actually is. For most
    // interfaces this is the interface itself, but the abstract Node and
    // CharacterData fixtures use a concrete subtype, so the expected chain must
    // start at that subtype.
    private static readonly Dictionary<string, string> FixtureLeaf =
        new(StringComparer.Ordinal)
        {
            ["Node"] = "Element",
            ["CharacterData"] = "Text",
        };

    // The DOM nodeType constant values, from the DOM standard. The manifest marks
    // these as data constants but does not carry their numeric value, so the
    // expected value table lives here.
    private static readonly Dictionary<string, double> NodeConstantValues =
        new(StringComparer.Ordinal)
        {
            ["ELEMENT_NODE"] = 1,
            ["ATTRIBUTE_NODE"] = 2,
            ["TEXT_NODE"] = 3,
            ["CDATA_SECTION_NODE"] = 4,
            ["ENTITY_REFERENCE_NODE"] = 5,
            ["ENTITY_NODE"] = 6,
            ["PROCESSING_INSTRUCTION_NODE"] = 7,
            ["COMMENT_NODE"] = 8,
            ["DOCUMENT_NODE"] = 9,
            ["DOCUMENT_TYPE_NODE"] = 10,
            ["DOCUMENT_FRAGMENT_NODE"] = 11,
            ["NOTATION_NODE"] = 12,
            ["DOCUMENT_POSITION_DISCONNECTED"] = 1,
            ["DOCUMENT_POSITION_PRECEDING"] = 2,
            ["DOCUMENT_POSITION_FOLLOWING"] = 4,
            ["DOCUMENT_POSITION_CONTAINS"] = 8,
            ["DOCUMENT_POSITION_CONTAINED_BY"] = 16,
            ["DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC"] = 32,
        };

    [TestMethod]
    public void Required_members_match_manifest_shape_except_known_gaps()
    {
        var runtime = NewStarlingRuntime();
        var expected = ExpectedFailures();
        var seenGaps = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var member in RequiredMembers())
        {
            string key = MemberKey(member);
            string wanted = ExpectedSurface(member);
            string actual = ReadMemberSurface(runtime, member);
            bool matches = actual == wanted;

            if (expected.Members.TryGetValue(key, out var gap))
            {
                seenGaps.Add(key);
                // A listed gap that is already fixed must be removed from the
                // sidecar, so flag it loudly instead of silently passing.
                if (matches)
                {
                    failures.Add($"{key}: listed as an expected failure ({gap}) but the runtime now matches the manifest. Remove it from the sidecar.");
                }

                continue;
            }

            if (!matches)
            {
                failures.Add($"{key}: expected surface '{wanted}' but runtime reported '{actual}'. Add a sidecar entry with a reason if this gap is known.");
            }
        }

        ReportUnusedGaps(expected.Members.Keys, seenGaps, failures);
        failures.Should().BeEmpty(BuildReport(failures));
    }

    [TestMethod]
    public void Node_constants_have_the_right_value_on_constructor_and_prototype()
    {
        var runtime = NewStarlingRuntime();
        var failures = new List<string>();

        foreach (var member in AllMembers().Where(m => m.Kind == "constant"))
        {
            if (!NodeConstantValues.TryGetValue(member.Name, out double wantValue))
            {
                failures.Add($"{MemberKey(member)}: constant has no expected value in the harness table.");
                continue;
            }

            string actual = ReadConstantPlacement(runtime, member, wantValue);
            if (actual != "ok")
            {
                failures.Add($"{MemberKey(member)}: {actual}");
            }
        }

        failures.Should().BeEmpty(BuildReport(failures));
    }

    [TestMethod]
    public void Instance_prototype_chains_follow_idl_inheritance_except_known_gaps()
    {
        var runtime = NewStarlingRuntime();
        var expected = ExpectedFailures();
        var seenGaps = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<string>();

        foreach (var iface in InheritanceChain.Keys)
        {
            string leaf = FixtureLeaf.GetValueOrDefault(iface, iface);
            string wanted = string.Join(" -> ", InheritanceChain[leaf]) + " -> Object";
            string actual = ReadPrototypeChain(runtime, iface);
            bool matches = actual == wanted;

            if (expected.Chains.TryGetValue(iface, out var gap))
            {
                seenGaps.Add(iface);
                if (matches)
                {
                    failures.Add($"chain {iface}: listed as an expected failure ({gap}) but the runtime chain now matches. Remove it from the sidecar.");
                }

                continue;
            }

            if (!matches)
            {
                failures.Add($"chain {iface}: expected '{wanted}' but runtime reported '{actual}'. Add a sidecar chain entry with a reason if this gap is known.");
            }
        }

        foreach (var listed in expected.Chains.Keys)
        {
            if (!seenGaps.Contains(listed))
            {
                failures.Add($"chain {listed}: listed in the sidecar but the harness never checked it. Remove the stale entry.");
            }
        }

        failures.Should().BeEmpty(BuildReport(failures));
    }

    // ---- runtime helpers ----

    private static JsRuntime NewStarlingRuntime()
    {
        var doc = new Document();
        var html = doc.CreateElement("html");
        var body = doc.CreateElement("body");
        doc.AppendChild(html);
        html.AppendChild(body);

        var runtime = new JsRuntime();
        WindowBinding.Install(runtime, doc, new WindowInstallOptions());
        return runtime;
    }

    private static string Eval(JsRuntime runtime, string script)
    {
        string source = "result = " + script;
        var program = new JsParser(source).ParseProgram();
        var chunk = JsCompiler.Compile(program);
        new JsVm(runtime).Run(chunk);
        return JsValue.ToStringValue(runtime.GetGlobal("result"));
    }

    // Reads the descriptor shape of a member off the prototype that owns it. For
    // instance members the owning prototype is found by walking up from a live
    // fixture so the harness checks the member where it actually lives, not just
    // on the leaf interface prototype.
    private static string ReadMemberSurface(JsRuntime runtime, SurfaceMember member)
    {
        string fixture = member.Static
            ? "null"
            : FixtureExpression.GetValueOrDefault(member.Interface, "null");
        string iface = JsonSerializer.Serialize(member.Interface);
        string name = JsonSerializer.Serialize(member.Name);
        string isStatic = member.Static ? "true" : "false";

        return Eval(runtime, $$"""
            (function(){
              var ifaceName = {{iface}};
              var memberName = {{name}};
              var ctor = globalThis[ifaceName];
              if (typeof ctor !== 'function') return 'missing-interface';
              var target;
              if ({{isStatic}}) {
                target = ctor;
              } else {
                var inst = {{fixture}};
                if (inst === null || inst === undefined) return 'missing-fixture';
                target = ctor.prototype;
                if (!(inst instanceof ctor)) return 'fixture-not-instance';
              }
              if (!target) return 'missing-target';
              var desc = Object.getOwnPropertyDescriptor(target, memberName);
              if (!desc) return 'missing';
              var shape = typeof desc.value === 'function'
                ? 'method'
                : ((typeof desc.get === 'function' || typeof desc.set === 'function') ? 'accessor' : 'data');
              return [
                shape,
                desc.writable === true ? 'w' : 'r',
                desc.enumerable === true ? 'e' : 'ne',
                desc.configurable === true ? 'c' : 'nc',
                typeof desc.get === 'function' ? 'get' : 'noget',
                typeof desc.set === 'function' ? 'set' : 'noset',
                typeof desc.value === 'function' ? String(desc.value.length) : '-'
              ].join('|');
            })()
            """);
    }

    private static string ExpectedSurface(SurfaceMember member)
    {
        var descriptor = member.Descriptor;
        string writable = descriptor.Writable is true ? "w" : "r";
        string enumerable = descriptor.Enumerable ? "e" : "ne";
        string configurable = descriptor.Configurable ? "c" : "nc";
        string hasGetter = descriptor.Kind == "accessor" ? "get" : "noget";
        string hasSetter = descriptor.Kind == "accessor" && member.Readonly is false ? "set" : "noset";
        string length = descriptor.Kind == "method"
            ? (member.RequiredArguments ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "-";

        return string.Join('|', descriptor.Kind, writable, enumerable, configurable, hasGetter, hasSetter, length);
    }

    // Checks a constant exists with the wanted value on both the constructor and
    // the prototype, and that both descriptors are non-writable, enumerable, and
    // non-configurable per the manifest.
    private static string ReadConstantPlacement(JsRuntime runtime, SurfaceMember member, double wantValue)
    {
        string iface = JsonSerializer.Serialize(member.Interface);
        string name = JsonSerializer.Serialize(member.Name);
        string want = wantValue.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return Eval(runtime, $$"""
            (function(){
              var ctor = globalThis[{{iface}}];
              if (typeof ctor !== 'function') return 'missing-interface';
              var proto = ctor.prototype;
              function check(target, label){
                var d = Object.getOwnPropertyDescriptor(target, {{name}});
                if (!d) return label + ':missing';
                if (typeof d.value === 'undefined' || String(d.value) !== {{JsonSerializer.Serialize(want)}}) return label + ':value=' + String(d.value);
                if (d.writable !== false) return label + ':writable';
                if (d.enumerable !== true) return label + ':not-enumerable';
                if (d.configurable !== false) return label + ':configurable';
                return 'ok';
              }
              var c = check(ctor, 'ctor');
              if (c !== 'ok') return c;
              return check(proto, 'proto');
            })()
            """);
    }

    // Walks the prototype chain of a fixture instance and names each prototype by
    // matching it against each interface constructor's prototype, ending with
    // Object.prototype reported as 'Object'.
    private static string ReadPrototypeChain(JsRuntime runtime, string iface)
    {
        string fixture = FixtureExpression[iface];
        var names = InheritanceChain.Keys.ToArray();
        string namesJson = JsonSerializer.Serialize(names);

        return Eval(runtime, $$"""
            (function(){
              var names = {{namesJson}};
              var inst = {{fixture}};
              if (inst === null || inst === undefined) return 'missing-fixture';
              function nameOf(proto){
                if (proto === Object.prototype) return 'Object';
                for (var i = 0; i < names.length; i++) {
                  var c = globalThis[names[i]];
                  if (typeof c === 'function' && c.prototype === proto) return names[i];
                }
                return '?';
              }
              var out = [];
              var p = Object.getPrototypeOf(inst);
              var guard = 0;
              while (p !== null && guard < 20) {
                out.push(nameOf(p));
                if (p === Object.prototype) break;
                p = Object.getPrototypeOf(p);
                guard++;
              }
              return out.join(' -> ');
            })()
            """);
    }

    // ---- manifest and sidecar loading ----

    private static IReadOnlyList<SurfaceMember> AllMembers()
    {
        string path = Path.Combine(FindRepoRoot(), "testdata", "webref", "core-dom-surface.json");
        var manifest = JsonSerializer.Deserialize<SurfaceManifest>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("Surface manifest could not be read.");
        return manifest.Members;
    }

    private static IReadOnlyList<SurfaceMember> RequiredMembers() =>
        [.. AllMembers().Where(m => m.Required)];

    private static ExpectedFailureSet ExpectedFailures()
    {
        string path = Path.Combine(FindRepoRoot(), "testdata", "webref", "surface-expected-failures.json");
        var file = JsonSerializer.Deserialize<ExpectedFailureFile>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("Expected-failures sidecar could not be read.");

        var members = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in file.Members ?? [])
        {
            string key = $"{entry.Interface}.{entry.Name}";
            members[key] = string.IsNullOrWhiteSpace(entry.Reason) ? "no reason given" : entry.Reason!;
        }

        var chains = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in file.Chains ?? [])
        {
            chains[entry.Interface] = string.IsNullOrWhiteSpace(entry.Reason) ? "no reason given" : entry.Reason!;
        }

        return new ExpectedFailureSet(members, chains);
    }

    private static void ReportUnusedGaps(IEnumerable<string> listed, HashSet<string> seen, List<string> failures)
    {
        foreach (var key in listed)
        {
            if (!seen.Contains(key))
            {
                failures.Add($"{key}: listed in the sidecar but it is not a required member the harness checks. Remove the stale entry.");
            }
        }
    }

    private static string MemberKey(SurfaceMember member) => $"{member.Interface}.{member.Name}";

    private static string BuildReport(List<string> failures)
    {
        if (failures.Count == 0)
        {
            return "no failures";
        }

        var sb = new StringBuilder();
        sb.AppendLine($"{failures.Count} runtime IDL harness failure(s):");
        foreach (var f in failures)
        {
            sb.AppendLine("  - " + f);
        }

        return sb.ToString();
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

    private sealed record ExpectedFailureSet(
        IReadOnlyDictionary<string, string> Members,
        IReadOnlyDictionary<string, string> Chains);

    private sealed record ExpectedFailureFile(
        [property: JsonPropertyName("members")] IReadOnlyList<ExpectedFailureEntry>? Members,
        [property: JsonPropertyName("chains")] IReadOnlyList<ExpectedChainEntry>? Chains);

    private sealed record ExpectedFailureEntry(
        [property: JsonPropertyName("interface")] string Interface,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("check")] string? Check,
        [property: JsonPropertyName("reason")] string? Reason);

    private sealed record ExpectedChainEntry(
        [property: JsonPropertyName("interface")] string Interface,
        [property: JsonPropertyName("reason")] string? Reason);
}
