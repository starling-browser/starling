using AwesomeAssertions;
using Starling.IdlGen.Model;
using Starling.IdlGen.Parsing;

namespace Starling.IdlGen.Tests;

[TestClass]
public class IdlParserTests
{
    private static IdlDocument Parse(string src) => IdlParser.Parse(src);

    private static T Single<T>(string src) where T : IdlDefinition =>
        Parse(src).Definitions.OfType<T>().Single();

    [TestMethod]
    public void Interface_with_inheritance_and_members()
    {
        var iface = Single<IdlInterface>(
            "[Exposed=Window] interface Node : EventTarget { readonly attribute DOMString nodeName; };");

        iface.Name.Should().Be("Node");
        iface.Inherits.Should().Be("EventTarget");
        iface.ExtendedAttributes.Should().ContainSingle(a => a.Name == "Exposed");
        var attr = iface.Members.OfType<IdlAttribute>().Single();
        attr.Name.Should().Be("nodeName");
        attr.Readonly.Should().BeTrue();
        attr.Type.Name.Should().Be("DOMString");
    }

    [TestMethod]
    public void Operation_with_union_argument()
    {
        var iface = Single<IdlInterface>(
            "interface ParentNode { undefined append((Node or DOMString)... nodes); };");
        var op = iface.Members.OfType<IdlOperation>().Single();
        op.Name.Should().Be("append");
        op.ReturnType.Name.Should().Be("undefined");

        var arg = op.Arguments.Single();
        arg.Variadic.Should().BeTrue();
        arg.Type.IsUnion.Should().BeTrue();
        arg.Type.Union.Select(u => u.Name).Should().BeEquivalentTo("Node", "DOMString");
    }

    [TestMethod]
    public void Generic_and_nullable_types()
    {
        var iface = Single<IdlInterface>(
            "interface I { sequence<Node> kids(); readonly attribute Element? owner; record<DOMString, long> map(); };");
        var ops = iface.Members.OfType<IdlOperation>().ToList();

        var kids = ops.Single(o => o.Name == "kids");
        kids.ReturnType.Name.Should().Be("sequence");
        kids.ReturnType.TypeArgs.Single().Name.Should().Be("Node");

        var owner = iface.Members.OfType<IdlAttribute>().Single();
        owner.Type.Name.Should().Be("Element");
        owner.Type.Nullable.Should().BeTrue();

        var map = ops.Single(o => o.Name == "map");
        map.ReturnType.Name.Should().Be("record");
        map.ReturnType.TypeArgs.Select(t => t.Name).Should().BeEquivalentTo("DOMString", "long");
    }

    [TestMethod]
    public void Multiword_primitive_types()
    {
        var iface = Single<IdlInterface>(
            "interface I { undefined f(unsigned long long a, long long b, unsigned short c, unrestricted double d); };");
        var args = iface.Members.OfType<IdlOperation>().Single().Arguments;
        args.Select(a => a.Type.Name).Should().ContainInOrder(
            "unsigned long long", "long long", "unsigned short", "unrestricted double");
    }

    [TestMethod]
    public void Extended_attribute_forms()
    {
        var iface = Single<IdlInterface>(
            "[Exposed=(Window,Worker), LegacyFactoryFunction=Image(DOMString src)] interface HTMLImageElement {};");
        var exposed = iface.ExtendedAttributes.Single(a => a.Name == "Exposed");
        exposed.Kind.Should().Be(IdlExtAttrKind.IdentList);
        exposed.Identifiers.Should().BeEquivalentTo("Window", "Worker");

        var factory = iface.ExtendedAttributes.Single(a => a.Name == "LegacyFactoryFunction");
        factory.Kind.Should().Be(IdlExtAttrKind.NamedArgList);
        factory.Identifier.Should().Be("Image");
        factory.Arguments.Single().Name.Should().Be("src");
    }

    [TestMethod]
    public void Wildcard_exposed()
    {
        var iface = Single<IdlInterface>("[Exposed=*] interface Event {};");
        iface.ExtendedAttributes.Single().Kind.Should().Be(IdlExtAttrKind.Wildcard);
    }

    [TestMethod]
    public void Enum_values()
    {
        var e = Single<IdlEnum>("enum ShadowRootMode { \"open\", \"closed\", };");
        e.Values.Should().BeEquivalentTo("open", "closed");
    }

    [TestMethod]
    public void Dictionary_required_and_default()
    {
        var dict = Single<IdlDictionary>(
            "dictionary EventInit : Base { required boolean bubbles; boolean cancelable = false; };");
        dict.Inherits.Should().Be("Base");

        var bubbles = dict.Members.Single(m => m.Name == "bubbles");
        bubbles.Required.Should().BeTrue();

        var cancelable = dict.Members.Single(m => m.Name == "cancelable");
        cancelable.Required.Should().BeFalse();
        cancelable.Default!.Kind.Should().Be(IdlDefaultKind.Boolean);
        cancelable.Default.Value.Should().Be("false");
    }

    [TestMethod]
    public void Callback_function_and_interface()
    {
        var fn = Single<IdlCallback>(
            "callback MutationCallback = undefined (sequence<MutationRecord> mutations);");
        fn.ReturnType.Name.Should().Be("undefined");
        fn.Arguments.Single().Type.Name.Should().Be("sequence");

        var cb = Parse("callback interface EventListener { undefined handleEvent(Event e); };")
            .Definitions.OfType<IdlInterface>().Single();
        cb.Callback.Should().BeTrue();
        cb.Members.OfType<IdlOperation>().Single().Name.Should().Be("handleEvent");
    }

    [TestMethod]
    public void Typedef_union()
    {
        var td = Single<IdlTypedef>("typedef (DOMString or sequence<DOMString>) StringOrList;");
        td.Name.Should().Be("StringOrList");
        td.Type.IsUnion.Should().BeTrue();
    }

    [TestMethod]
    public void Includes_statement()
    {
        var inc = Single<IdlIncludes>("Document includes ParentNode;");
        inc.Name.Should().Be("Document");
        inc.Mixin.Should().Be("ParentNode");
    }

    [TestMethod]
    public void Partial_and_mixin()
    {
        Single<IdlInterface>("partial interface Window { readonly attribute long x; };")
            .Partial.Should().BeTrue();
        Single<IdlInterface>("interface mixin ParentNode { readonly attribute long y; };")
            .Mixin.Should().BeTrue();
    }

    [TestMethod]
    public void Special_operations()
    {
        var iface = Single<IdlInterface>(
            "interface NodeList { getter Node? item(unsigned long index); readonly attribute unsigned long length; };");
        var getter = iface.Members.OfType<IdlOperation>().Single();
        getter.Special.Should().Be(IdlSpecialKind.Getter);
        getter.Name.Should().Be("item");
    }

    [TestMethod]
    public void Anonymous_getter()
    {
        var iface = Single<IdlInterface>("interface I { getter DOMString (DOMString name); };");
        var getter = iface.Members.OfType<IdlOperation>().Single();
        getter.Special.Should().Be(IdlSpecialKind.Getter);
        getter.Name.Should().BeNull();
    }

    [TestMethod]
    public void Iterable_maplike_setlike()
    {
        Single<IdlInterface>("interface A { iterable<Node>; };")
            .Members.OfType<IdlIterable>().Single().KeyType.Should().BeNull();

        var pair = Single<IdlInterface>("interface B { iterable<DOMString, Node>; };")
            .Members.OfType<IdlIterable>().Single();
        pair.KeyType!.Name.Should().Be("DOMString");
        pair.ValueType.Name.Should().Be("Node");

        Single<IdlInterface>("interface C { readonly maplike<DOMString, long>; };")
            .Members.OfType<IdlMaplike>().Single().Readonly.Should().BeTrue();

        Single<IdlInterface>("interface D { setlike<long>; };")
            .Members.OfType<IdlSetlike>().Single().ValueType.Name.Should().Be("long");
    }

    [TestMethod]
    public void Constants_and_static_and_stringifier()
    {
        var iface = Single<IdlInterface>(
            "interface I { const unsigned short ELEMENT_NODE = 1; static DOMString make(); stringifier DOMString (); };");

        var c = iface.Members.OfType<IdlConstant>().Single();
        c.Name.Should().Be("ELEMENT_NODE");
        c.Value.Should().Be("1");

        iface.Members.OfType<IdlOperation>().Single(o => o.Name == "make").Static.Should().BeTrue();
        iface.Members.OfType<IdlOperation>().Single(o => o.Stringifier).Name.Should().BeNull();
    }

    [TestMethod]
    public void Optional_argument_with_default_dictionary()
    {
        var op = Single<IdlInterface>(
            "interface I { undefined f(optional EventInit init = {}); };")
            .Members.OfType<IdlOperation>().Single();
        var arg = op.Arguments.Single();
        arg.Optional.Should().BeTrue();
        arg.Default!.Kind.Should().Be(IdlDefaultKind.EmptyDictionary);
    }

    [TestMethod]
    public void Underscore_escaped_identifier()
    {
        // A leading underscore escapes a keyword-like identifier; it is stripped.
        Single<IdlInterface>("interface _interface { readonly attribute long x; };")
            .Name.Should().Be("interface");
    }

    [TestMethod]
    public void All_vendored_idl_parses()
    {
        // Smoke test: every vendored spec parses without error.
        string idlDir = Path.Combine(FindRepoRoot(), "testdata", "webref", "idl");
        foreach (string path in Directory.EnumerateFiles(idlDir, "*.idl"))
        {
            var act = () => IdlParser.Parse(File.ReadAllText(path));
            act.Should().NotThrow($"{Path.GetFileName(path)} should parse");
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
