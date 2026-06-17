using AwesomeAssertions;

namespace Starling.Dom.Tests;

/// <summary>
/// Tests for the internal Document mutation hooks (AttributeMutated /
/// ChildListMutated) that MutationObserverBinding subscribes to. These exercise
/// two correctness details the hooks must get right:
/// <list type="bullet">
/// <item>childList records capture sibling positions at insertion time, even
/// when a re-entrant host hook (e.g. an injected &lt;script&gt;) mutates the tree
/// during connection.</item>
/// <item>attribute records carry the pre-change value on every mutation path,
/// including removals and attribute-node replacement (not just setAttribute).</item>
/// </list>
/// </summary>
[TestClass]
public sealed class MutationHookTests
{
    // --- childList ordering (record fires before re-entrant NodeConnected) ----

    [TestMethod]
    public void ChildList_record_captures_siblings_before_reentrant_connect()
    {
        var doc = new Document();
        var body = doc.CreateElement("body");
        doc.AppendChild(body);
        var a = doc.CreateElement("a");
        body.AppendChild(a);

        var records = new List<(Node Added, Node? Prev, Node? Next)>();
        doc.ChildListMutated = (_, added, _, prev, next) =>
        {
            if (added is { Count: > 0 })
            {
                records.Add((added[0], prev, next));
            }
        };

        // Simulate the engine's NodeConnected hook running an injected <script>
        // that re-enters and mutates the tree the moment `b` is connected.
        var b = doc.CreateElement("b");
        var reentered = false;
        doc.NodeConnected = node =>
        {
            if (reentered || !ReferenceEquals(node, b))
            {
                return;
            }

            reentered = true;
            body.AppendChild(doc.CreateElement("c")); // changes b.NextSibling: null -> <c>
        };

        body.AppendChild(b); // tree becomes <a><b>, then the hook appends <c>

        // The record for <b> must reflect its siblings at insertion (prev=<a>,
        // next=null) — NOT the post-re-entrancy state where next=<c>.
        var bRecord = records.Single(r => ReferenceEquals(r.Added, b));
        bRecord.Prev.Should().BeSameAs(a);
        bRecord.Next.Should().BeNull();
    }

    [TestMethod]
    public void Fragment_insertion_reports_single_record_with_all_added_nodes()
    {
        var doc = new Document();
        var body = doc.CreateElement("body");
        doc.AppendChild(body);

        // Build the fragment BEFORE subscribing, so only the body insertion is
        // recorded (appending into the fragment fires its own childList events).
        var frag = doc.CreateDocumentFragment();
        var a = doc.CreateElement("a");
        var b = doc.CreateElement("b");
        frag.AppendChild(a);
        frag.AppendChild(b);

        var records = new List<(IReadOnlyList<Node>? Added, IReadOnlyList<Node>? Removed)>();
        doc.ChildListMutated = (_, added, removed, _, _) => records.Add((added, removed));

        body.AppendChild(frag); // moves <a> and <b> in one logical insertion

        // DOM §4.3: a single childList record whose addedNodes is the whole set.
        records.Should().HaveCount(1);
        records[0].Added.Should().NotBeNull();
        records[0].Added!.Should().HaveCount(2);
        records[0].Added![0].Should().BeSameAs(a);
        records[0].Added![1].Should().BeSameAs(b);
        records[0].Removed.Should().BeNull();
    }

    // --- attribute oldValue on every mutation path ----------------------------

    [TestMethod]
    public void RemoveAttribute_reports_old_value()
    {
        var doc = new Document();
        var el = doc.CreateElement("div");
        el.SetAttribute("data-x", "hello");

        var captured = "UNSET";
        doc.AttributeMutated = (_, _, old) => captured = old ?? "<null>";
        el.RemoveAttribute("data-x");

        captured.Should().Be("hello");
    }

    [TestMethod]
    public void RemoveAttributeNS_reports_old_value()
    {
        var doc = new Document();
        var el = doc.CreateElement("div");
        el.SetAttributeNS("urn:x", "p:foo", "v1");

        var captured = "UNSET";
        doc.AttributeMutated = (_, _, old) => captured = old ?? "<null>";
        el.RemoveAttributeNS("urn:x", "foo");

        captured.Should().Be("v1");
    }

    [TestMethod]
    public void SetNamedItem_replacement_reports_old_value()
    {
        var doc = new Document();
        var el = doc.CreateElement("div");
        el.SetAttribute("data-x", "first");

        var captured = "UNSET";
        doc.AttributeMutated = (_, _, old) => captured = old ?? "<null>";
        el.Attributes.SetNamedItem(new AttrNode("data-x", "second"));

        captured.Should().Be("first");
    }

    [TestMethod]
    public void SetNamedItemNS_replacement_reports_old_value()
    {
        var doc = new Document();
        var el = doc.CreateElement("div");
        el.SetAttributeNS("urn:x", "p:foo", "v1");

        var captured = "UNSET";
        doc.AttributeMutated = (_, _, old) => captured = old ?? "<null>";
        el.Attributes.SetNamedItemNS(AttrNode.CreateNamespaced("p:foo", "urn:x", "v2"));

        captured.Should().Be("v1");
    }

    [TestMethod]
    public void Newly_added_attribute_reports_null_old_value()
    {
        var doc = new Document();
        var el = doc.CreateElement("div");

        var captured = "UNSET";
        doc.AttributeMutated = (_, _, old) => captured = old ?? "<null>";
        el.SetAttribute("data-x", "fresh");

        captured.Should().Be("<null>");
    }
}
