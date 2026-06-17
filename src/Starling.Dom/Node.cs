using Starling.Dom.Events;

namespace Starling.Dom;

/// <summary>
/// Base DOM node. v1 keeps children as a simple doubly-linked list inside the
/// parent (O(1) Append/Remove, O(n) by-index lookup). See 05_DOM.md §Design choices.
/// </summary>
/// <remarks>
/// Inherits <see cref="EventTarget"/> so every node can have listeners attached
/// per [DOM §4.4](https://dom.spec.whatwg.org/#interface-node).
/// </remarks>
public abstract class Node : EventTarget
{
    /// <summary>DOM §6.3.3 — NodeIterator removal steps. Bindings layer
    /// subscribes here so live iterators can update their reference node when
    /// a node is removed from the tree. Called with (ownerDocument, removedNode)
    /// before the parent link is cleared. Null when no iterators are active.</summary>
    public static Action<Document, Node>? NodeRemovedHook;

    public abstract NodeKind Kind { get; }

    public Node? ParentNode { get; internal set; }
    public Node? PreviousSibling { get; internal set; }
    public Node? NextSibling { get; internal set; }
    public Node? FirstChild { get; internal set; }
    public Node? LastChild { get; internal set; }

    public virtual Document? OwnerDocument { get; internal set; }

    /// <summary>True when this node is reachable from its document root — i.e.
    /// part of the live, laid-out tree (DOM <c>isConnected</c>). A node created
    /// via <c>createElement</c> but not yet inserted has an owner document but is
    /// not connected; incremental layout only batches mutations on connected
    /// nodes, since detached-subtree edits are subsumed when the subtree is
    /// attached and reconciled.</summary>
    internal bool IsConnectedToDocument
    {
        get
        {
            for (Node? n = this; n is not null; n = n.ParentNode)
            {
                if (n is Document)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public virtual string NodeName => Kind.ToString();

    public virtual string? NodeValue
    {
        get => null;
        set { }
    }

    public Node AppendChild(Node child) => InsertBefore(child, null);

    /// <summary>
    /// WHATWG DOM §4.2.3 "pre-insert(node, child)": ensure pre-insertion validity,
    /// then insert. Throws <see cref="DomException"/> with the right error name on
    /// an invalid hierarchy. The generated <c>appendChild</c>/<c>insertBefore</c>
    /// bindings dispatch here so they stay thin; the HTML parser uses the
    /// lower-level <see cref="AppendChild"/>, which does not run these checks.
    /// </summary>
    public Node PreInsert(Node child, Node? referenceChild)
    {
        ArgumentNullException.ThrowIfNull(child);

        // The parent must be a node that can contain children.
        if (this is not (Document or Element or DocumentFragment))
        {
            throw DomException.Create("HierarchyRequestError",
                $"Node of type '{GetType().Name}' cannot have children");
        }

        // The child must not be an inclusive ancestor of the parent (cycle check).
        for (var p = (Node?)this; p is not null; p = p.ParentNode)
        {
            if (ReferenceEquals(p, child))
            {
                throw DomException.Create("HierarchyRequestError", "The new child element contains the parent");
            }
        }

        // A document cannot be inserted as a child of a non-document.
        if (child is Document && this is not Document)
        {
            throw DomException.Create("HierarchyRequestError", "Documents cannot be inserted as a child node");
        }

        // An Attr is not part of the normal node tree.
        if (child is AttrNode)
        {
            throw DomException.Create("HierarchyRequestError", "Cannot insert an Attr into a node tree.");
        }

        // The reference child, when given, must be a child of this node.
        if (referenceChild is not null && referenceChild.ParentNode != this)
        {
            throw DomException.Create("NotFoundError", "The reference child is not a child of this node.");
        }

        try
        {
            return InsertBefore(child, referenceChild);
        }
        catch (InvalidOperationException ex)
        {
            throw MapMutationException(ex);
        }
    }

    /// <summary>WHATWG DOM §4.2.3 "pre-remove(child)": throws NotFoundError when
    /// the child is not a child of this node, otherwise removes it. The generated
    /// <c>removeChild</c> binding dispatches here.</summary>
    public Node PreRemove(Node child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (!ReferenceEquals(child.ParentNode, this))
        {
            throw DomException.Create("NotFoundError", "The node to be removed is not a child of this node");
        }

        try
        {
            return RemoveChild(child);
        }
        catch (InvalidOperationException ex)
        {
            throw DomException.Create("NotFoundError", ex.Message ?? "");
        }
    }

    /// <summary>WHATWG DOM §4.2.3 "replace(child, node)": validates the hierarchy
    /// and that the old child belongs to this node, then replaces it. The
    /// generated <c>replaceChild</c> binding dispatches here.</summary>
    public Node PreReplace(Node newChild, Node oldChild)
    {
        ArgumentNullException.ThrowIfNull(newChild);
        ArgumentNullException.ThrowIfNull(oldChild);

        if (this is not (Document or Element or DocumentFragment))
        {
            throw DomException.Create("HierarchyRequestError", $"Node of type '{GetType().Name}' cannot have children");
        }

        for (var p = (Node?)this; p is not null; p = p.ParentNode)
        {
            if (ReferenceEquals(p, newChild))
            {
                throw DomException.Create("HierarchyRequestError", "The new child element contains the parent");
            }
        }

        if (newChild is AttrNode)
        {
            throw DomException.Create("HierarchyRequestError", "Cannot insert an Attr into a node tree.");
        }

        if (!ReferenceEquals(oldChild.ParentNode, this))
        {
            throw DomException.Create("NotFoundError", "The child to be replaced is not a child of this node");
        }

        try
        {
            return ReplaceChild(newChild, oldChild);
        }
        catch (InvalidOperationException ex)
        {
            throw MapMutationException(ex);
        }
    }

    /// <summary>WHATWG DOM §4.4 "contains": true when <paramref name="other"/> is
    /// an inclusive descendant of this node. The generated binding dispatches here.</summary>
    public bool Contains(Node? other)
    {
        for (var n = other; n is not null; n = n.ParentNode)
        {
            if (ReferenceEquals(n, this))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>WHATWG DOM §4.4 "isSameNode": identity comparison.</summary>
    public bool IsSameNode(Node? other) => ReferenceEquals(this, other);

    /// <summary>WHATWG DOM §4.4 "getRootNode": the topmost ancestor (the
    /// composed flag is not honored, matching the prior binding).</summary>
    public Node GetRootNode()
    {
        var root = this;
        while (root.ParentNode is { } parent)
        {
            root = parent;
        }

        return root;
    }

    /// <summary>WHATWG DOM §4.4 "isEqualNode": structural equality.</summary>
    public bool IsEqualNode(Node? other) => NodesEqual(this, other);

    /// <summary>WHATWG DOM §4.4 "normalize": merge adjacent Text nodes and drop
    /// empty ones, recursively.</summary>
    public void Normalize()
    {
        var child = FirstChild;
        while (child is not null)
        {
            var next = child.NextSibling;
            if (child is Text t)
            {
                if (string.IsNullOrEmpty(t.Data))
                {
                    RemoveChild(child);
                }
                else
                {
                    while (next is Text t2)
                    {
                        var following = t2.NextSibling;
                        t.Data += t2.Data;
                        RemoveChild(t2);
                        next = following;
                    }
                }
            }
            else
            {
                child.Normalize();
            }
            child = next;
        }
    }

    /// <summary>The element children of this node, in tree order.</summary>
    public IReadOnlyList<Element> ChildElements()
    {
        var list = new List<Element>();
        for (var n = FirstChild; n is not null; n = n.NextSibling)
        {
            if (n is Element e)
            {
                list.Add(e);
            }
        }

        return list;
    }

    /// <summary>The parent node when it is an element, else null.
    /// DOM §4.4 <c>parentElement</c>.</summary>
    public Element? ParentElement => ParentNode as Element;

    /// <summary>True when this node is connected to a document.
    /// DOM §4.4 <c>isConnected</c>.</summary>
    public bool IsConnected => IsConnectedToDocument;

    /// <summary>True when this node has any children.
    /// DOM §4.4 <c>hasChildNodes()</c>.</summary>
    public bool HasChildNodes() => FirstChild is not null;

    /// <summary>The first child that is an element, else null.
    /// DOM §4.2.6 ParentNode <c>firstElementChild</c>.</summary>
    public Element? FirstElementChild
    {
        get
        {
            for (var n = FirstChild; n is not null; n = n.NextSibling)
            {
                if (n is Element e)
                {
                    return e;
                }
            }

            return null;
        }
    }

    /// <summary>The last child that is an element, else null.
    /// DOM §4.2.6 ParentNode <c>lastElementChild</c>.</summary>
    public Element? LastElementChild
    {
        get
        {
            for (var n = LastChild; n is not null; n = n.PreviousSibling)
            {
                if (n is Element e)
                {
                    return e;
                }
            }

            return null;
        }
    }

    /// <summary>The number of element children.
    /// DOM §4.2.6 ParentNode <c>childElementCount</c>.</summary>
    public int ChildElementCount
    {
        get
        {
            int count = 0;
            for (var n = FirstChild; n is not null; n = n.NextSibling)
            {
                if (n is Element)
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>The next sibling that is an element, else null.
    /// DOM §4.2.8 NonDocumentTypeChildNode <c>nextElementSibling</c>.</summary>
    public Element? NextElementSibling
    {
        get
        {
            for (var n = NextSibling; n is not null; n = n.NextSibling)
            {
                if (n is Element e)
                {
                    return e;
                }
            }

            return null;
        }
    }

    /// <summary>The previous sibling that is an element, else null.
    /// DOM §4.2.8 NonDocumentTypeChildNode <c>previousElementSibling</c>.</summary>
    public Element? PreviousElementSibling
    {
        get
        {
            for (var n = PreviousSibling; n is not null; n = n.PreviousSibling)
            {
                if (n is Element e)
                {
                    return e;
                }
            }

            return null;
        }
    }

    /// <summary>WHATWG DOM §4.4 "compareDocumentPosition": the position bitmask.
    /// Cross-root order is implementation-defined but internally consistent.</summary>
    public ushort CompareDocumentPosition(Node other)
    {
        if (ReferenceEquals(this, other))
        {
            return 0;
        }

        if (!ReferenceEquals(GetRootNode(), other.GetRootNode()))
        {
            int selfHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
            int otherHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(other);
            int precedingOrFollowing = otherHash < selfHash || (otherHash == selfHash && GetHashCode() < other.GetHashCode()) ? 2 : 4;
            return (ushort)(1 | 32 | precedingOrFollowing);   // DISCONNECTED | IMPLEMENTATION_SPECIFIC | (PRECEDING|FOLLOWING)
        }

        int bits = 0;
        if (Contains(other))
        {
            bits |= 16;        // CONTAINED_BY
        }

        if (other.Contains(this))
        {
            bits |= 8;    // CONTAINS
        }

        bits |= IsBeforeInTreeOrder(other, this) ? 2 : 4;   // PRECEDING : FOLLOWING
        return (ushort)bits;
    }

    private static bool IsBeforeInTreeOrder(Node a, Node b)
    {
        static List<Node> Path(Node n)
        {
            var path = new List<Node>();
            for (var cur = (Node?)n; cur is not null; cur = cur.ParentNode)
            {
                path.Insert(0, cur);
            }

            return path;
        }

        var pa = Path(a);
        var pb = Path(b);
        int min = System.Math.Min(pa.Count, pb.Count);
        for (int i = 0; i < min; i++)
        {
            if (!ReferenceEquals(pa[i], pb[i]))
            {
                var parent = i > 0 ? pa[i - 1] : null;
                if (parent is null)
                {
                    return false;
                }

                for (var child = parent.FirstChild; child is not null; child = child.NextSibling)
                {
                    if (ReferenceEquals(child, pa[i]))
                    {
                        return true;
                    }

                    if (ReferenceEquals(child, pb[i]))
                    {
                        return false;
                    }
                }
                return false;
            }
        }
        return pa.Count < pb.Count;
    }

    private static bool NodesEqual(Node? a, Node? b)
    {
        if (a is null && b is null)
        {
            return true;
        }

        if (a is null || b is null)
        {
            return false;
        }

        if (a.Kind != b.Kind)
        {
            return false;
        }

        switch (a)
        {
            case Element ea when b is Element eb:
                if (ea.TagName != eb.TagName || ea.Namespace != eb.Namespace)
                {
                    return false;
                }

                var attrsA = ea.Attributes;
                var attrsB = eb.Attributes;
                if (attrsA.Count != attrsB.Count)
                {
                    return false;
                }

                for (var i = 0; i < attrsA.Count; i++)
                {
                    var atA = attrsA[i];
                    var localName = NamedNodeMap.LocalNameOf(atA.Name);
                    var atB = eb.GetAttributeNS(atA.Namespace, localName);
                    if (atB is null || atA.Value != atB)
                    {
                        return false;
                    }
                }
                break;
            case Text ta when b is Text tb:
                if (ta.Data != tb.Data)
                {
                    return false;
                }

                break;
            case Comment ca when b is Comment cb:
                if (ca.Data != cb.Data)
                {
                    return false;
                }

                break;
            case ProcessingInstruction pia when b is ProcessingInstruction pib:
                if (pia.Target != pib.Target || pia.Data != pib.Data)
                {
                    return false;
                }

                break;
            case DocumentType dta when b is DocumentType dtb:
                if (dta.Name != dtb.Name)
                {
                    return false;
                }

                break;
        }

        var ac = a.FirstChild;
        var bc = b.FirstChild;
        while (ac is not null && bc is not null)
        {
            if (!NodesEqual(ac, bc))
            {
                return false;
            }

            ac = ac.NextSibling;
            bc = bc.NextSibling;
        }
        return ac is null && bc is null;
    }

    // ParentNode / ChildNode insertion methods (WHATWG DOM §4.2.1). The node-or-
    // string coercion happens in the binding marshaller; these take the resolved
    // nodes. The generated append/prepend/before/after/replaceWith/replaceChildren
    // bindings dispatch here.

    /// <summary>ParentNode "append": insert each node after the last child.</summary>
    public void Append(IReadOnlyList<Node> nodes)
    {
        foreach (var n in nodes)
        {
            AppendChild(n);
        }
    }

    /// <summary>ParentNode "prepend": insert each node before the first child.</summary>
    public void Prepend(IReadOnlyList<Node> nodes)
    {
        var reference = FirstChild;
        foreach (var n in nodes)
        {
            InsertBefore(n, reference);
        }
    }

    /// <summary>ChildNode "before": insert each node before this node.</summary>
    public void Before(IReadOnlyList<Node> nodes)
    {
        if (ParentNode is not { } parent)
        {
            return;
        }

        foreach (var n in nodes)
        {
            parent.InsertBefore(n, this);
        }
    }

    /// <summary>ChildNode "after": insert each node after this node.</summary>
    public void After(IReadOnlyList<Node> nodes)
    {
        if (ParentNode is not { } parent)
        {
            return;
        }

        var reference = NextSibling;
        foreach (var n in nodes)
        {
            parent.InsertBefore(n, reference);
        }
    }

    /// <summary>ChildNode "replaceWith": replace this node with the given nodes.</summary>
    public void ReplaceWith(IReadOnlyList<Node> nodes)
    {
        if (ParentNode is not { } parent)
        {
            return;
        }

        var reference = NextSibling;
        RemoveFromParent();
        foreach (var n in nodes)
        {
            parent.InsertBefore(n, reference);
        }
    }

    /// <summary>ParentNode "replaceChildren": replace all children with the given nodes.</summary>
    public void ReplaceChildren(IReadOnlyList<Node> nodes)
    {
        while (FirstChild is { } c)
        {
            c.RemoveFromParent();
        }

        foreach (var n in nodes)
        {
            AppendChild(n);
        }
    }

    private static DomException MapMutationException(InvalidOperationException ex)
    {
        string msg = ex.Message ?? "";
        string name = msg.Contains("not found", StringComparison.OrdinalIgnoreCase)
                      || msg.Contains("not a child", StringComparison.OrdinalIgnoreCase)
            ? "NotFoundError" : "HierarchyRequestError";
        return DomException.Create(name, msg);
    }

    public Node InsertBefore(Node child, Node? referenceChild)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (referenceChild is not null && referenceChild.ParentNode != this)
        {
            throw new InvalidOperationException("The reference child is not a child of this node.");
        }

        if (child == referenceChild)
        {
            return child;
        }

        if (child == this)
        {
            throw new InvalidOperationException("A node cannot be its own child.");
        }

        for (var ancestor = this.ParentNode; ancestor is not null; ancestor = ancestor.ParentNode)
        {
            if (ancestor == child)
            {
                throw new InvalidOperationException("A node cannot be inserted into one of its descendants.");
            }
        }

        if (child is DocumentFragment fragment)
        {
            // DOM §4.2.3 "insert": all of the fragment's children move into this
            // node and are reported in a SINGLE childList record whose addedNodes
            // is the whole moved set. Snapshot first — linking unlinks each node
            // from the fragment as we go.
            var moved = new List<Node>();
            for (var n = fragment.FirstChild; n is not null; n = n.NextSibling)
            {
                moved.Add(n);
            }

            if (moved.Count == 0)
            {
                return fragment;
            }
            // DOM §4.2.3: empty the fragment with the suppress-observers flag set,
            // so the per-child removal from the fragment queues no record — the
            // single insertion record below covers the whole move.
            foreach (var node in moved)
            {
                LinkChild(node, referenceChild, suppressRemoveRecord: true);
            }
            // Capture the inserted range's siblings before any re-entrant
            // NotifyConnected can mutate the tree, then fire one record.
            var fragPrev = moved[0].PreviousSibling;
            var fragNext = moved[^1].NextSibling;
            (OwnerDocument ?? this as Document)?.ChildListMutated?.Invoke(
                this, moved, null, fragPrev, fragNext);
            foreach (var node in moved)
            {
                NotifyConnected(node);
            }

            return fragment;
        }

        LinkChild(child, referenceChild, suppressRemoveRecord: false);
        // Capture sibling positions and queue the childList MutationRecord BEFORE
        // NotifyConnected. NotifyConnected can run host hooks (e.g. execute an
        // injected <script>) that re-enter and mutate the tree, which would leave
        // child.PreviousSibling/NextSibling stale by the time the record is built.
        var insertedPrev = child.PreviousSibling;
        var insertedNext = child.NextSibling;
        (OwnerDocument ?? this as Document)?.ChildListMutated?.Invoke(
            this, new[] { child }, null, insertedPrev, insertedNext);
        NotifyConnected(child);
        return child;
    }

    /// <summary>Link a single child into the tree before <paramref name="referenceChild"/>
    /// (append when null): update sibling/parent links, owner document, live Ranges,
    /// and layout bookkeeping. Does NOT fire the childList MutationRecord or
    /// NotifyConnected — the InsertBefore caller does that once per logical insertion
    /// (so a DocumentFragment yields a single record covering all moved nodes).</summary>
    private void LinkChild(Node child, Node? referenceChild, bool suppressRemoveRecord)
    {
        child.RemoveFromParent(suppressRemoveRecord);
        child.ParentNode = this;
        child.SetOwnerDocumentRecursive(OwnerDocument ?? (this as Document));

        if (referenceChild is null)
        {
            if (LastChild is null)
            {
                FirstChild = child;
                LastChild = child;
            }
            else
            {
                LastChild.NextSibling = child;
                child.PreviousSibling = LastChild;
                LastChild = child;
            }
        }
        else
        {
            var previous = referenceChild.PreviousSibling;
            child.NextSibling = referenceChild;
            child.PreviousSibling = previous;
            referenceChild.PreviousSibling = child;
            if (previous is null)
            {
                FirstChild = child;
            }
            else
            {
                previous.NextSibling = child;
            }
        }

        // DOM §5.3.4 "insert": update live Range boundary points now that the
        // child is linked into the tree.
        DomRange.OnNodeInserted(this, DomRange.IndexOf(child), 1);

        var childAffectsLayout = !IsLayoutInvariantElement(child);
        OnTreeMutated(affectsLayout: childAffectsLayout);
        if (childAffectsLayout)
        {
            (OwnerDocument ?? this as Document)?.RecordLayoutMutation(this, LayoutChangeKind.ChildInserted);
        }
    }

    /// <summary>If <paramref name="inserted"/> is now connected to a document
    /// that has a host hook attached, raise the connection notification for the
    /// inserted node and every descendant. The engine uses this to discover
    /// <c>&lt;script&gt;</c> elements injected at runtime; pure-DOM callers pay
    /// only a null check.</summary>
    private void NotifyConnected(Node inserted)
    {
        var document = OwnerDocument ?? (this as Document);
        if (document?.NodeConnected is null)
        {
            return;
        }

        document.NotifyNodeConnected(inserted);
        foreach (var descendant in inserted.Descendants())
        {
            document.NotifyNodeConnected(descendant);
        }
    }

    public Node ReplaceChild(Node newChild, Node oldChild)
    {
        ArgumentNullException.ThrowIfNull(newChild);
        ArgumentNullException.ThrowIfNull(oldChild);
        if (oldChild.ParentNode != this)
        {
            throw new InvalidOperationException("The old child is not a child of this node.");
        }

        InsertBefore(newChild, oldChild);
        RemoveChild(oldChild);
        return oldChild;
    }

    public Node RemoveChild(Node child)
    {
        ArgumentNullException.ThrowIfNull(child);
        if (child.ParentNode != this)
        {
            throw new InvalidOperationException("The node is not a child of this node.");
        }

        child.RemoveFromParent();
        return child;
    }

    /// <summary>Detach this node from its parent. No-op if already orphaned.</summary>
    public void RemoveFromParent() => RemoveFromParent(suppressObservers: false);

    /// <summary>Detach this node from its parent. When <paramref name="suppressObservers"/>
    /// is set, no childList MutationRecord is queued for the removal — DOM §4.2.3
    /// "insert" sets the suppress-observers flag while emptying a DocumentFragment
    /// into its new parent, since that move is reported by the single insertion
    /// record instead. Live Ranges and NodeIterators still update either way.</summary>
    internal void RemoveFromParent(bool suppressObservers)
    {
        var parent = ParentNode;
        if (parent is null)
        {
            return;
        }

        // DOM §6.3.3: notify live NodeIterators before the parent link is cleared.
        if (NodeRemovedHook is { } hook)
        {
            var doc = OwnerDocument ?? (this is Document d ? d : null);
            if (doc is not null)
            {
                hook(doc, this);
            }
        }

        // DOM §5.3.4 "remove": update live Range boundary points before the
        // node is unlinked (needs the old parent and old index).
        DomRange.OnNodeRemoved(this, parent, DomRange.IndexOf(this));

        // Capture the old siblings for the childList MutationRecord before unlinking.
        var oldPrev = PreviousSibling;
        var oldNext = NextSibling;

        if (PreviousSibling is not null)
        {
            PreviousSibling.NextSibling = NextSibling;
        }
        else
        {
            parent.FirstChild = NextSibling;
        }

        if (NextSibling is not null)
        {
            NextSibling.PreviousSibling = PreviousSibling;
        }
        else
        {
            parent.LastChild = PreviousSibling;
        }

        ParentNode = null;
        PreviousSibling = null;
        NextSibling = null;
        // Cache before nulling refs above? We already read `this` after the
        // unhook, so call the lookup now while the type is known.
        var removedAffectsLayout = !IsLayoutInvariantElement(this);
        parent.OnTreeMutated(affectsLayout: removedAffectsLayout);
        if (removedAffectsLayout)
        {
            (parent.OwnerDocument ?? parent as Document)?.RecordLayoutMutation(parent, LayoutChangeKind.ChildRemoved);
        }

        if (!suppressObservers)
        {
            (parent.OwnerDocument ?? parent as Document)?.ChildListMutated?.Invoke(
                parent, null, new[] { this }, oldPrev, oldNext);
        }
    }

    public IEnumerable<Node> ChildNodes
    {
        get
        {
            for (var child = FirstChild; child is not null; child = child.NextSibling)
            {
                yield return child;
            }
        }
    }

    public IEnumerable<Node> Descendants()
    {
        // Iterative pre-order walk avoids unbounded recursion on hostile DOM depth.
        var stack = new Stack<Node>();
        for (var child = LastChild; child is not null; child = child.PreviousSibling)
        {
            stack.Push(child);
        }

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            for (var child = node.LastChild; child is not null; child = child.PreviousSibling)
            {
                stack.Push(child);
            }
        }
    }

    public IEnumerable<Element> DescendantElements() => Descendants().OfType<Element>();

    // ---- Namespace lookup (DOM §4.4 "locate a namespace / prefix").
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    public string? LookupNamespaceURI(string? prefix)
        => LocateNamespace(this, string.IsNullOrEmpty(prefix) ? null : prefix);

    public bool IsDefaultNamespace(string? @namespace)
        => LookupNamespaceURI(null) == (string.IsNullOrEmpty(@namespace) ? null : @namespace);

    public string? LookupPrefix(string? @namespace)
    {
        if (string.IsNullOrEmpty(@namespace))
        {
            return null;
        }

        return LocatePrefix(this is Document d ? d.DocumentElement : this as Element ?? ParentNode as Element, @namespace);
    }

    private static string? LocateNamespace(Node? node, string? prefix)
    {
        switch (node)
        {
            case Element el:
                if (!string.IsNullOrEmpty(el.Namespace) && el.Prefix == prefix)
                {
                    return el.Namespace;
                }

                foreach (var a in el.Attributes)
                {
                    if (a.Namespace != XmlnsNamespace)
                    {
                        continue;
                    }

                    var (pfx, local) = SplitName(a.Name);
                    if (pfx == "xmlns" && local == prefix)
                    {
                        return string.IsNullOrEmpty(a.Value) ? null : a.Value;
                    }

                    if (pfx is null && a.Name == "xmlns" && prefix is null)
                    {
                        return string.IsNullOrEmpty(a.Value) ? null : a.Value;
                    }
                }
                // Walk up to element ancestors only — stop at the document to avoid
                // documentElement<->Document recursion.
                return el.ParentNode is Element ep ? LocateNamespace(ep, prefix) : null;
            case Document doc:
                return doc.DocumentElement is { } de ? LocateNamespace(de, prefix) : null;
            default:
                return node?.ParentNode is Element p ? LocateNamespace(p, prefix) : null;
        }
    }

    private static string? LocatePrefix(Element? el, string ns)
    {
        for (var node = el; node is not null; node = node.ParentNode as Element)
        {
            if (node.Namespace == ns && node.Prefix is not null)
            {
                return node.Prefix;
            }

            foreach (var a in node.Attributes)
            {
                if (a.Namespace != XmlnsNamespace)
                {
                    continue;
                }

                var (pfx, local) = SplitName(a.Name);
                if (pfx == "xmlns" && a.Value == ns)
                {
                    return local;
                }
            }
        }
        return null;
    }

    private static (string? Prefix, string Local) SplitName(string name)
    {
        var i = name.IndexOf(':', StringComparison.Ordinal);
        return i >= 0 ? (name[..i], name[(i + 1)..]) : (null, name);
    }

    public virtual string TextContent
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            foreach (var node in Descendants())
            {
                if (node is Text text)
                {
                    sb.Append(text.Data);
                }
                else if (node is CData cdata)
                {
                    sb.Append(cdata.Data);
                }
            }
            return sb.ToString();
        }
        set
        {
            while (FirstChild is not null)
            {
                RemoveChild(FirstChild);
            }

            if (!string.IsNullOrEmpty(value))
            {
                var document = OwnerDocument ?? this as Document;
                AppendChild(document is null
                    ? new Text(value)
                    : document.CreateTextNode(value));
            }
        }
    }

    protected virtual void OnTreeMutated() => OnTreeMutated(affectsLayout: true);

    /// <summary>Variant that lets callers opt out of the layout-invalidation
    /// bump when the moved child is known not to participate in the box tree
    /// (e.g. <c>&lt;script&gt;</c>, <c>&lt;meta&gt;</c>, <c>&lt;link&gt;</c>).
    /// On real pages script-injected non-rendered elements drive most of the
    /// spurious mid-execution reflows; google.com's startup hits two forced
    /// prelayouts that this skip avoids when only such elements were appended
    /// between the reads.</summary>
    protected virtual void OnTreeMutated(bool affectsLayout)
    {
        if (OwnerDocument is { } document)
        {
            // Structural / text mutations always invalidate the mutation
            // version (observers, live collections, MutationObserver rely on
            // it). Layout invalidation is opt-out for non-rendered children.
            document.BumpMutationVersion();
            if (affectsLayout)
            {
                document.BumpLayoutInvalidationVersion();
            }
        }
        else
        {
            ParentNode?.OnTreeMutated(affectsLayout);
        }
    }

    /// <summary>An element that never produces a box and contributes no
    /// stylesheet rules: <c>script</c>, <c>meta</c>, <c>title</c>, <c>base</c>,
    /// <c>noscript</c>, <c>template</c>. Inserting or removing it cannot
    /// change any other element's layout, so we can skip the
    /// LayoutInvalidationVersion bump. <c>&lt;style&gt;</c> and
    /// <c>&lt;link&gt;</c> are excluded (they can add author CSS that
    /// recascades layout).</summary>
    internal static bool IsLayoutInvariantElement(Node n)
    {
        if (n is not Element e)
        {
            return false;
        }

        var name = e.LocalName;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        return name.Equals("script", StringComparison.OrdinalIgnoreCase)
            || name.Equals("meta", StringComparison.OrdinalIgnoreCase)
            || name.Equals("title", StringComparison.OrdinalIgnoreCase)
            || name.Equals("base", StringComparison.OrdinalIgnoreCase)
            || name.Equals("noscript", StringComparison.OrdinalIgnoreCase)
            || name.Equals("template", StringComparison.OrdinalIgnoreCase);
    }

    internal void SetOwnerDocumentRecursive(Document? document)
    {
        var stack = new Stack<Node>();
        stack.Push(this);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is not Document)
            {
                node.OwnerDocument = document;
            }

            for (var child = node.LastChild; child is not null; child = child.PreviousSibling)
            {
                stack.Push(child);
            }
        }
    }
}
