using System.Runtime.CompilerServices;
using Starling.Dom;
using Starling.Js.Runtime;

namespace Starling.Bindings;

/// <summary>
/// DOM §6 — NodeFilter, TreeWalker, NodeIterator JS bindings.
///
/// <para><b>NodeFilter (§6.1)</b>: exposed as a window-global constructor/
/// object with SHOW_* and FILTER_* constants. Filter callback is a raw function
/// (called directly) or an object with an <c>acceptNode</c> property (retrieved
/// fresh on every invocation per spec). Exceptions propagate to the caller.</para>
///
/// <para><b>TreeWalker (§6.2)</b>: created by <c>document.createTreeWalker</c>.
/// Exposes <c>root</c>, <c>whatToShow</c>, <c>filter</c>, <c>currentNode</c>
/// (read/write), <c>parentNode</c>, <c>firstChild</c>, <c>lastChild</c>,
/// <c>previousSibling</c>, <c>nextSibling</c>, <c>previousNode</c>,
/// <c>nextNode</c>. Algorithms ported literally from WHATWG DOM §6.2.2.</para>
///
/// <para><b>NodeIterator (§6.3)</b>: created by <c>document.createNodeIterator</c>.
/// Exposes <c>root</c>, <c>whatToShow</c>, <c>filter</c>, <c>referenceNode</c>,
/// <c>pointerBeforeReferenceNode</c>, <c>nextNode</c>, <c>previousNode</c>,
/// <c>detach</c> (no-op per spec). Active-flag guard prevents recursive filter
/// calls — throws InvalidStateError per §6.1. NodeIterator removal tracking
/// (DOM §6.3.3) requires a per-document registry of live iterators.</para>
/// </summary>
internal static class TraversalBinding
{
    // -----------------------------------------------------------------------
    //  Per-document live-iterator registry (for NodeIterator removal steps)
    // -----------------------------------------------------------------------

    /// <summary>Per-document set of live NodeIterators that need removal-step
    /// notifications when a node is removed from the document tree.</summary>
    private static readonly ConditionalWeakTable<Document, List<WeakReference<HostNodeIterator>>>
        LiveIterators = new();

    internal static void RegisterIterator(Document doc, HostNodeIterator iter)
    {
        var list = LiveIterators.GetValue(doc, _ => new List<WeakReference<HostNodeIterator>>());
        lock (list)
        {
            list.Add(new WeakReference<HostNodeIterator>(iter));
        }
    }

    /// <summary>DOM §6.3.3 — NodeIterator pre-removal steps. Called from
    /// <see cref="Node.RemoveChild"/> (and remove-from-parent) when a node is
    /// removed from a document so each live iterator can update its
    /// referenceNode / pointerBeforeReferenceNode.</summary>
    public static void NotifyNodeRemoval(Document doc, Node nodeBeingRemoved)
    {
        if (!LiveIterators.TryGetValue(doc, out var list))
        {
            return;
        }

        lock (list)
        {
            list.RemoveAll(wr => !wr.TryGetTarget(out _)); // prune dead refs
            foreach (var wr in list)
            {
                if (wr.TryGetTarget(out var iter))
                {
                    iter.NodeRemoved(nodeBeingRemoved);
                }
            }
        }
    }

    // -----------------------------------------------------------------------
    //  Install
    // -----------------------------------------------------------------------

    /// <summary>Install NodeFilter, TreeWalker, NodeIterator into the realm.
    /// Also wires <c>document.createTreeWalker</c> and
    /// <c>document.createNodeIterator</c> onto <paramref name="docProto"/>.</summary>
    public static void Install(JsRealm realm, JsObject docProto)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(docProto);

        // Wire §6.3.3 removal hook once (idempotent: if already subscribed this
        // is a no-op because += adds a delegate — but we use a null check then
        // assignment to avoid duplicate subscriptions across multiple realms).
        if (Node.NodeRemovedHook is null)
        {
            Node.NodeRemovedHook = NotifyNodeRemoval;
        }

        InstallNodeFilter(realm);
        InstallTreeWalker(realm);
        InstallNodeIterator(realm);

        // document.createTreeWalker(root, whatToShow?, filter?)
        EventTargetBinding.DefineMethod(realm, docProto, "createTreeWalker", (thisV, args) =>
        {
            var root = args.Length > 0 ? DomWrappers.UnwrapNode(args[0]) : null;
            if (root is null)
            {
                throw new JsThrow(realm.NewTypeError("createTreeWalker: root must be a Node"));
            }

            // whatToShow: undefined → 0xFFFFFFFF; null → 0; number → truncated
            var whatToShow = args.Length < 2 || args[1].IsUndefined
                ? 0xFFFF_FFFFu
                : args[1].IsNullish ? 0u : (uint)JsValue.ToNumber(args[1]);

            // filter: null/undefined → null; callable or object with acceptNode → kept
            var filterVal = args.Length < 3 || args[2].IsUndefined ? JsValue.Null : args[2];

            var walker = new HostTreeWalker(root, whatToShow, filterVal);
            return JsValue.Object(WrapTreeWalker(realm, walker));
        }, length: 1);

        // document.createNodeIterator(root, whatToShow?, filter?)
        EventTargetBinding.DefineMethod(realm, docProto, "createNodeIterator", (thisV, args) =>
        {
            var root = args.Length > 0 ? DomWrappers.UnwrapNode(args[0]) : null;
            if (root is null)
            {
                throw new JsThrow(realm.NewTypeError("createNodeIterator: root must be a Node"));
            }

            var whatToShow = args.Length < 2 || args[1].IsUndefined
                ? 0xFFFF_FFFFu
                : args[1].IsNullish ? 0u : (uint)JsValue.ToNumber(args[1]);

            var filterVal = args.Length < 3 || args[2].IsUndefined ? JsValue.Null : args[2];

            var iter = new HostNodeIterator(root, whatToShow, filterVal);
            // Register with the document so removal steps can update it.
            var doc = root.OwnerDocument ?? root as Document;
            if (doc is not null)
            {
                RegisterIterator(doc, iter);
            }

            return JsValue.Object(WrapNodeIterator(realm, iter));
        }, length: 1);
    }

    // -----------------------------------------------------------------------
    //  NodeFilter global object (§6.1)
    // -----------------------------------------------------------------------

    private static void InstallNodeFilter(JsRealm realm)
    {
        // NodeFilter is exposed as both a constructor and as an object with
        // constants directly on it. Tests do NodeFilter.SHOW_ELEMENT, also
        // use 'instanceof NodeFilter' is NOT a spec requirement — the tests
        // just do typeof NodeFilter and read constants.
        var nfObj = new JsObject(realm.ObjectPrototype);

        void DefConst(string name, uint val) =>
            nfObj.DefineOwnProperty(name,
                PropertyDescriptor.Data(JsValue.Number(val), writable: false, enumerable: true, configurable: false));

        // Filter result constants
        DefConst("FILTER_ACCEPT", 1);
        DefConst("FILTER_REJECT", 2);
        DefConst("FILTER_SKIP", 3);

        // whatToShow bitmask constants
        DefConst("SHOW_ALL", 0xFFFF_FFFF);
        DefConst("SHOW_ELEMENT", 0x1);
        DefConst("SHOW_ATTRIBUTE", 0x2);
        DefConst("SHOW_TEXT", 0x4);
        DefConst("SHOW_CDATA_SECTION", 0x8);
        DefConst("SHOW_ENTITY_REFERENCE", 0x10);
        DefConst("SHOW_ENTITY", 0x20);
        DefConst("SHOW_PROCESSING_INSTRUCTION", 0x40);
        DefConst("SHOW_COMMENT", 0x80);
        DefConst("SHOW_DOCUMENT", 0x100);
        DefConst("SHOW_DOCUMENT_TYPE", 0x200);
        DefConst("SHOW_DOCUMENT_FRAGMENT", 0x400);
        DefConst("SHOW_NOTATION", 0x800);

        // Also put the constants on the function so that code doing
        // `NodeFilter.SHOW_ELEMENT` resolves whether NodeFilter is a function
        // or object (both work in browsers). We expose it as a no-op function
        // so typeof NodeFilter === "function" (some tests check this).
        var nfCtor = new JsNativeFunction(realm, "NodeFilter", 0,
            (_, _) => JsValue.Undefined, isConstructor: false);

        // Copy all own constants to the function object too.
        foreach (var key in nfObj.Keys)
        {
            var val = nfObj.Get(key);
            nfCtor.DefineOwnProperty(key,
                PropertyDescriptor.Data(val, writable: false, enumerable: true, configurable: false));
        }

        realm.GlobalObject.DefineOwnProperty("NodeFilter",
            PropertyDescriptor.Data(JsValue.Object(nfCtor), writable: true, enumerable: false, configurable: true));
    }

    // -----------------------------------------------------------------------
    //  TreeWalker prototype + wrapper (§6.2)
    // -----------------------------------------------------------------------

    // Cache per-realm TreeWalker prototype.
    private static readonly ConditionalWeakTable<JsRealm, JsObject> TreeWalkerProtos = new();

    private static JsObject GetOrBuildTreeWalkerProto(JsRealm realm)
    {
        if (TreeWalkerProtos.TryGetValue(realm, out var proto))
        {
            return proto;
        }

        proto = new JsObject(realm.ObjectPrototype);

        // [Symbol.toStringTag] or just toString override → '[object TreeWalker]'
        EventTargetBinding.DefineMethod(realm, proto, "toString",
            (_, _) => JsValue.String("[object TreeWalker]"), length: 0);

        // readonly root
        EventTargetBinding.DefineAccessor(realm, proto, "root",
            (thisV, _) => GetWalker(thisV) is { } w
                ? JsValue.Object(DomWrappers.Wrap(realm, w.Root))
                : JsValue.Undefined);

        // readonly whatToShow
        EventTargetBinding.DefineAccessor(realm, proto, "whatToShow",
            (thisV, _) => GetWalker(thisV) is { } w ? JsValue.Number(w.WhatToShow) : JsValue.Number(0));

        // readonly filter — null when no filter; otherwise the original JS value
        EventTargetBinding.DefineAccessor(realm, proto, "filter",
            (thisV, _) => GetWalker(thisV) is { } w ? FilterToJs(w.Filter) : JsValue.Null);

        // read/write currentNode
        EventTargetBinding.DefineAccessor(realm, proto, "currentNode",
            (thisV, _) => GetWalker(thisV) is { } w
                ? JsValue.Object(DomWrappers.Wrap(realm, w.CurrentNode))
                : JsValue.Undefined,
            (thisV, args) =>
            {
                if (GetWalker(thisV) is not { } w)
                {
                    return JsValue.Undefined;
                }

                var node = args.Length > 0 ? DomWrappers.UnwrapNode(args[0]) : null;
                if (node is null)
                {
                    throw new JsThrow(realm.NewTypeError("TreeWalker.currentNode must be set to a Node"));
                }

                w.CurrentNode = node;
                return JsValue.Undefined;
            });

        // Traversal methods
        EventTargetBinding.DefineMethod(realm, proto, "parentNode",
            (thisV, _) => WalkerTraverse(realm, thisV, (w, vm) => w.ParentNode(vm)), length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "firstChild",
            (thisV, _) => WalkerTraverse(realm, thisV, (w, vm) => w.FirstChild(vm)), length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "lastChild",
            (thisV, _) => WalkerTraverse(realm, thisV, (w, vm) => w.LastChild(vm)), length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "previousSibling",
            (thisV, _) => WalkerTraverse(realm, thisV, (w, vm) => w.PreviousSibling(vm)), length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "nextSibling",
            (thisV, _) => WalkerTraverse(realm, thisV, (w, vm) => w.NextSibling(vm)), length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "previousNode",
            (thisV, _) => WalkerTraverse(realm, thisV, (w, vm) => w.PreviousNode(vm)), length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "nextNode",
            (thisV, _) => WalkerTraverse(realm, thisV, (w, vm) => w.NextNode(vm)), length: 0);

        TreeWalkerProtos.Add(realm, proto);
        return proto;
    }

    private static void InstallTreeWalker(JsRealm realm)
    {
        // Pre-build the prototype (also caches it).
        GetOrBuildTreeWalkerProto(realm);
    }

    private static JsTreeWalkerWrapper WrapTreeWalker(JsRealm realm, HostTreeWalker walker)
    {
        var proto = GetOrBuildTreeWalkerProto(realm);
        return new JsTreeWalkerWrapper(proto, walker);
    }

    private static HostTreeWalker? GetWalker(JsValue v)
        => v.IsObject && v.AsObject is JsTreeWalkerWrapper w ? w.Walker : null;

    private static JsValue WalkerTraverse(JsRealm realm, JsValue thisV,
        Func<HostTreeWalker, JsVm, Node?> method)
    {
        if (GetWalker(thisV) is not { } w)
        {
            return JsValue.Null;
        }

        var vm = GetOrCreateVm(realm);
        var result = method(w, vm);
        return result is null ? JsValue.Null : JsValue.Object(DomWrappers.Wrap(realm, result));
    }

    // -----------------------------------------------------------------------
    //  NodeIterator prototype + wrapper (§6.3)
    // -----------------------------------------------------------------------

    private static readonly ConditionalWeakTable<JsRealm, JsObject> NodeIteratorProtos = new();

    private static JsObject GetOrBuildNodeIteratorProto(JsRealm realm)
    {
        if (NodeIteratorProtos.TryGetValue(realm, out var proto))
        {
            return proto;
        }

        proto = new JsObject(realm.ObjectPrototype);

        EventTargetBinding.DefineMethod(realm, proto, "toString",
            (_, _) => JsValue.String("[object NodeIterator]"), length: 0);

        EventTargetBinding.DefineAccessor(realm, proto, "root",
            (thisV, _) => GetIter(thisV) is { } it
                ? JsValue.Object(DomWrappers.Wrap(realm, it.Root))
                : JsValue.Undefined);

        EventTargetBinding.DefineAccessor(realm, proto, "whatToShow",
            (thisV, _) => GetIter(thisV) is { } it ? JsValue.Number(it.WhatToShow) : JsValue.Number(0));

        EventTargetBinding.DefineAccessor(realm, proto, "filter",
            (thisV, _) => GetIter(thisV) is { } it ? FilterToJs(it.Filter) : JsValue.Null);

        // readonly referenceNode
        EventTargetBinding.DefineAccessor(realm, proto, "referenceNode",
            (thisV, _) => GetIter(thisV) is { } it
                ? JsValue.Object(DomWrappers.Wrap(realm, it.ReferenceNode))
                : JsValue.Undefined);

        // readonly pointerBeforeReferenceNode
        EventTargetBinding.DefineAccessor(realm, proto, "pointerBeforeReferenceNode",
            (thisV, _) => GetIter(thisV) is { } it
                ? JsValue.Boolean(it.PointerBeforeReferenceNode)
                : JsValue.Undefined);

        EventTargetBinding.DefineMethod(realm, proto, "nextNode",
            (thisV, _) => IterTraverse(realm, thisV, true), length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "previousNode",
            (thisV, _) => IterTraverse(realm, thisV, false), length: 0);

        // detach() — no-op per spec (DOM §6.3.1)
        EventTargetBinding.DefineMethod(realm, proto, "detach",
            (_, _) => JsValue.Undefined, length: 0);

        NodeIteratorProtos.Add(realm, proto);
        return proto;
    }

    private static void InstallNodeIterator(JsRealm realm)
    {
        GetOrBuildNodeIteratorProto(realm);
    }

    private static JsNodeIteratorWrapper WrapNodeIterator(JsRealm realm, HostNodeIterator iter)
    {
        var proto = GetOrBuildNodeIteratorProto(realm);
        return new JsNodeIteratorWrapper(proto, iter);
    }

    private static HostNodeIterator? GetIter(JsValue v)
        => v.IsObject && v.AsObject is JsNodeIteratorWrapper w ? w.Iterator : null;

    private static JsValue IterTraverse(JsRealm realm, JsValue thisV, bool next)
    {
        if (GetIter(thisV) is not { } it)
        {
            return JsValue.Null;
        }

        var vm = GetOrCreateVm(realm);
        var result = next ? it.NextNode(vm) : it.PreviousNode(vm);
        return result is null ? JsValue.Null : JsValue.Object(DomWrappers.Wrap(realm, result));
    }

    // -----------------------------------------------------------------------
    //  Shared helpers
    // -----------------------------------------------------------------------

    private static JsVm GetOrCreateVm(JsRealm realm)
        => realm.ActiveVm ?? new JsVm(WindowBinding.RuntimeForRealm(realm)
               ?? throw new InvalidOperationException("No runtime for realm"));

    private static JsValue FilterToJs(JsValue filter)
        => filter.IsNullish ? JsValue.Null : filter;

    /// <summary>DOM §6.1 "filter" steps — invoke a NodeFilter against a node.
    /// Returns FILTER_ACCEPT(1), FILTER_REJECT(2), or FILTER_SKIP(3).
    /// Throws InvalidStateError when the active-flag is set (recursive call).
    /// Propagates any exception thrown by the filter callback.</summary>
    internal static uint InvokeFilter(JsRealm realm, JsVm vm, JsValue filterVal, Node node, ref bool activeFlag)
    {
        if (filterVal.IsNullish)
        {
            return NodeFilter.Accept;
        }

        if (activeFlag)
        {
            throw DomExceptionBinding.Throw(realm, "InvalidStateError",
                "NodeFilter is already active (recursive filter call)");
        }

        activeFlag = true;
        try
        {
            JsValue result;
            var nodeJs = JsValue.Object(DomWrappers.Wrap(realm, node));

            if (AbstractOperations.IsCallable(filterVal))
            {
                // A raw function: call it as a function (thisValue = undefined per spec).
                result = AbstractOperations.Call(vm, filterVal, JsValue.Undefined, new[] { nodeJs });
            }
            else if (filterVal.IsObject)
            {
                // An object: Get the acceptNode property using the full spec Get
                // (which invokes getters and propagates their exceptions), then call it
                // with thisValue = the filter object (per spec §6.1). Get is fresh
                // every traversal step (spec requires this).
                var fn = AbstractOperations.Get(vm, filterVal.AsObject, "acceptNode", filterVal);
                if (!AbstractOperations.IsCallable(fn))
                {
                    throw new JsThrow(realm.NewTypeError(
                        "NodeFilter object must have a callable 'acceptNode' property"));
                }

                result = AbstractOperations.Call(vm, fn, filterVal, new[] { nodeJs });
            }
            else
            {
                return NodeFilter.Accept;
            }

            // DOM §6.1: return value converted via ToUint32. Values outside {1,2,3}
            // default to SKIP (browser-compatible; 0/false is treated as SKIP not REJECT).
            var n = (uint)JsValue.ToNumber(result);
            return n switch { 1 => NodeFilter.Accept, 2 => NodeFilter.Reject, 3 => NodeFilter.Skip, _ => NodeFilter.Skip };
        }
        finally
        {
            activeFlag = false;
        }
    }

    /// <summary>DOM §6 — apply whatToShow bitmask (§6.1 step 1) and then
    /// the optional filter callback. Returns FILTER_ACCEPT/REJECT/SKIP.</summary>
    internal static uint FilterNode(JsRealm realm, JsVm vm, Node node, uint whatToShow, JsValue filterVal, ref bool activeFlag)
    {
        // Bit position = nodeType - 1.
        var bit = 1u << ((int)NodeTypeOf(node) - 1);
        if ((whatToShow & bit) == 0)
        {
            return NodeFilter.Skip;
        }

        return filterVal.IsNullish
            ? NodeFilter.Accept
            : InvokeFilter(realm, vm, filterVal, node, ref activeFlag);
    }

    private static int NodeTypeOf(Node n) => n switch
    {
        Element => 1,
        // Attr (2) is a struct, not a Node in this DOM model
        Text => 3,
        CData => 4,
        ProcessingInstruction => 7,
        Comment => 8,
        Document => 9,
        DocumentType => 10,
        DocumentFragment => 11,
        _ => 0,
    };

    // -----------------------------------------------------------------------
    //  Node tree iteration helpers (pre-order; used by TreeWalker + NodeIterator)
    // -----------------------------------------------------------------------

    /// <summary>First node following <paramref name="node"/> in tree order
    /// (depth-first pre-order). null when none.</summary>
    internal static Node? NextNodeInTree(Node node)
    {
        if (node.FirstChild is not null)
        {
            return node.FirstChild;
        }

        for (var n = node; n is not null; n = n.ParentNode)
        {
            if (n.NextSibling is not null)
            {
                return n.NextSibling;
            }
        }
        return null;
    }

    /// <summary>First node preceding <paramref name="node"/> in tree order.
    /// null when none.</summary>
    internal static Node? PreviousNodeInTree(Node node)
    {
        if (node.PreviousSibling is not null)
        {
            var n = node.PreviousSibling;
            while (n.LastChild is not null)
            {
                n = n.LastChild;
            }

            return n;
        }
        return node.ParentNode;
    }

    internal static bool IsInclusiveDescendant(Node node, Node root)
    {
        for (var n = node; n is not null; n = n.ParentNode)
        {
            if (ReferenceEquals(n, root))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool IsInclusiveAncestor(Node ancestor, Node node)
        => IsInclusiveDescendant(node, ancestor);
}

// ============================================================================
//  JS wrapper types
// ============================================================================

internal sealed class JsTreeWalkerWrapper : JsObject
{
    public HostTreeWalker Walker { get; }
    public JsTreeWalkerWrapper(JsObject proto, HostTreeWalker walker) : base(proto) { Walker = walker; }
}

internal sealed class JsNodeIteratorWrapper : JsObject
{
    public HostNodeIterator Iterator { get; }
    public JsNodeIteratorWrapper(JsObject proto, HostNodeIterator iterator) : base(proto) { Iterator = iterator; }
}

// ============================================================================
//  Host TreeWalker (§6.2)
// ============================================================================

/// <summary>Host-side state for a TreeWalker. All traversal algorithms are
/// ported literally from WHATWG DOM §6.2.2.</summary>
internal sealed class HostTreeWalker
{
    public Node Root { get; }
    public uint WhatToShow { get; }
    public JsValue Filter { get; }
    public Node CurrentNode { get; set; }

    // Per §6.1: active flag prevents recursive filter calls.
    private bool _active;

    public HostTreeWalker(Node root, uint whatToShow, JsValue filter)
    {
        Root = root;
        WhatToShow = whatToShow;
        Filter = filter;
        CurrentNode = root;
    }

    // ----------------------------------------------------------------
    //  §6.2.2 — TreeWalker traverse algorithms
    // ----------------------------------------------------------------

    /// <summary>§6.2.2 — "traverse children" algorithm.
    /// <paramref name="first"/> true → firstChild, false → lastChild.</summary>
    private Node? TraverseChildren(JsRealm realm, JsVm vm, bool first)
    {
        var node = CurrentNode;
        node = first ? node.FirstChild : node.LastChild;
        while (node is not null)
        {
            var result = ApplyFilter(realm, vm, node);
            if (result == NodeFilter.Accept)
            {
                CurrentNode = node;
                return node;
            }
            if (result == NodeFilter.Skip)
            {
                var child = first ? node.FirstChild : node.LastChild;
                if (child is not null) { node = child; continue; }
            }
            // REJECT or SKIP with no children: back up
            while (node is not null)
            {
                var sibling = first ? node.NextSibling : node.PreviousSibling;
                if (sibling is not null) { node = sibling; break; }
                var parent = node.ParentNode;
                if (parent is null || ReferenceEquals(parent, Root) || ReferenceEquals(parent, CurrentNode))
                {
                    return null;
                }

                node = parent;
            }
        }
        return null;
    }

    /// <summary>§6.2.2 — "traverse siblings" algorithm.
    /// <paramref name="next"/> true → nextSibling, false → previousSibling.</summary>
    private Node? TraverseSiblings(JsRealm realm, JsVm vm, bool next)
    {
        var node = CurrentNode;
        if (ReferenceEquals(node, Root))
        {
            return null;
        }

        while (true)
        {
            var sibling = next ? node.NextSibling : node.PreviousSibling;
            while (sibling is not null)
            {
                node = sibling;
                var result = ApplyFilter(realm, vm, node);
                if (result == NodeFilter.Accept)
                {
                    CurrentNode = node;
                    return node;
                }
                sibling = (result != NodeFilter.Reject) ? (next ? node.FirstChild : node.LastChild) : null;
                if (sibling is null)
                {
                    sibling = next ? node.NextSibling : node.PreviousSibling;
                }
            }

            node = node.ParentNode!;
            if (node is null || ReferenceEquals(node, Root))
            {
                return null;
            }

            if (ApplyFilter(realm, vm, node) == NodeFilter.Accept)
            {
                return null;
            }
        }
    }

    // ----------------------------------------------------------------
    //  Public traversal entry points
    // ----------------------------------------------------------------

    public Node? ParentNode(JsVm vm)
    {
        // §6.2.2 traverse parent: start from currentNode, while not root walk up.
        var realm = GetRealm(vm);
        var node = CurrentNode;
        while (!ReferenceEquals(node, Root))
        {
            node = node.ParentNode!;
            if (node is null)
            {
                return null;
            }

            if (ApplyFilter(realm, vm, node) == NodeFilter.Accept)
            {
                CurrentNode = node;
                return node;
            }
        }
        return null;
    }

    public Node? FirstChild(JsVm vm) => TraverseChildren(GetRealm(vm), vm, true);
    public Node? LastChild(JsVm vm) => TraverseChildren(GetRealm(vm), vm, false);
    public Node? NextSibling(JsVm vm) => TraverseSiblings(GetRealm(vm), vm, true);
    public Node? PreviousSibling(JsVm vm) => TraverseSiblings(GetRealm(vm), vm, false);

    public Node? NextNode(JsVm vm)
    {
        var realm = GetRealm(vm);
        var node = CurrentNode;
        var result = NodeFilter.Accept;
        while (true)
        {
            while (result != NodeFilter.Reject && node.FirstChild is not null)
            {
                node = node.FirstChild;
                result = ApplyFilter(realm, vm, node);
                if (result == NodeFilter.Accept)
                {
                    CurrentNode = node;
                    return node;
                }
            }
            Node? next = null;
            for (var tmp = node; tmp is not null; tmp = tmp.ParentNode)
            {
                if (ReferenceEquals(tmp, Root))
                {
                    return null;
                }

                if (tmp.NextSibling is not null) { next = tmp.NextSibling; break; }
            }
            if (next is null)
            {
                return null;
            }

            node = next;
            result = ApplyFilter(realm, vm, node);
            if (result == NodeFilter.Accept)
            {
                CurrentNode = node;
                return node;
            }
        }
    }

    public Node? PreviousNode(JsVm vm)
    {
        var realm = GetRealm(vm);
        var node = CurrentNode;
        while (!ReferenceEquals(node, Root))
        {
            Node? sibling = node.PreviousSibling;
            while (sibling is not null)
            {
                node = sibling;
                var result = ApplyFilter(realm, vm, node);
                while (result != NodeFilter.Reject && node.LastChild is not null)
                {
                    node = node.LastChild;
                    result = ApplyFilter(realm, vm, node);
                }
                if (result == NodeFilter.Accept)
                {
                    CurrentNode = node;
                    return node;
                }
                sibling = node.PreviousSibling;
            }
            if (ReferenceEquals(node, Root))
            {
                return null;
            }

            node = node.ParentNode!;
            if (node is null)
            {
                return null;
            }

            if (ApplyFilter(realm, vm, node) == NodeFilter.Accept)
            {
                CurrentNode = node;
                return node;
            }
        }
        return null;
    }

    // ----------------------------------------------------------------
    //  Private helpers
    // ----------------------------------------------------------------

    private uint ApplyFilter(JsRealm realm, JsVm vm, Node node)
        => TraversalBinding.FilterNode(realm, vm, node, WhatToShow, Filter, ref _active);

    private static JsRealm GetRealm(JsVm vm) => vm.Realm;
}

// ============================================================================
//  Host NodeIterator (§6.3)
// ============================================================================

/// <summary>Host-side state for a NodeIterator. Algorithms from §6.3.2.</summary>
internal sealed class HostNodeIterator
{
    public Node Root { get; }
    public uint WhatToShow { get; }
    public JsValue Filter { get; }
    public Node ReferenceNode { get; private set; }
    public bool PointerBeforeReferenceNode { get; private set; }

    private bool _active;

    public HostNodeIterator(Node root, uint whatToShow, JsValue filter)
    {
        Root = root;
        WhatToShow = whatToShow;
        Filter = filter;
        ReferenceNode = root;
        PointerBeforeReferenceNode = true;
    }

    // ----------------------------------------------------------------
    //  §6.3.2 — NodeIterator traverse algorithm
    // ----------------------------------------------------------------

    private Node? Traverse(JsRealm realm, JsVm vm, bool next)
    {
        var node = ReferenceNode;
        var beforeNode = PointerBeforeReferenceNode;

        while (true)
        {
            if (next)
            {
                if (!beforeNode)
                {
                    node = TraversalBinding.NextNodeInTree(node);
                    if (node is null || !TraversalBinding.IsInclusiveDescendant(node, Root))
                    {
                        return null;
                    }
                }
                else
                {
                    beforeNode = false;
                }
            }
            else
            {
                if (beforeNode)
                {
                    node = TraversalBinding.PreviousNodeInTree(node);
                    if (node is null || !TraversalBinding.IsInclusiveDescendant(node, Root))
                    {
                        return null;
                    }
                }
                else
                {
                    beforeNode = true;
                }
            }

            var result = FilterNode(realm, vm, node!);
            if (result == NodeFilter.Accept)
            {
                ReferenceNode = node!;
                PointerBeforeReferenceNode = beforeNode;
                return node;
            }
        }
    }

    public Node? NextNode(JsVm vm) => Traverse(vm.Realm, vm, true);
    public Node? PreviousNode(JsVm vm) => Traverse(vm.Realm, vm, false);

    private uint FilterNode(JsRealm realm, JsVm vm, Node node)
        => TraversalBinding.FilterNode(realm, vm, node, WhatToShow, Filter, ref _active);

    // ----------------------------------------------------------------
    //  §6.3.3 — NodeIterator pre-removal steps
    // ----------------------------------------------------------------

    public void NodeRemoved(Node nodeBeingRemoved)
    {
        // §6.3.3: "If the node is root or is not an inclusive ancestor of
        // referenceNode, terminate these steps."
        if (ReferenceEquals(nodeBeingRemoved, Root))
        {
            return;
        }

        if (!TraversalBinding.IsInclusiveAncestor(nodeBeingRemoved, ReferenceNode))
        {
            return;
        }

        if (PointerBeforeReferenceNode)
        {
            // "If there is a node following the last inclusive descendant of
            // the node that is being removed …"
            var next = NextNodeDescendants(nodeBeingRemoved);
            if (next is not null && TraversalBinding.IsInclusiveDescendant(next, Root))
            {
                ReferenceNode = next;
                return;
            }
            // "Set the referenceNode attribute to the first node preceding the
            // node that is being removed and set the pointerBeforeReferenceNode
            // attribute to false."
            var prev = TraversalBinding.PreviousNodeInTree(nodeBeingRemoved);
            if (prev is not null)
            {
                ReferenceNode = prev;
                PointerBeforeReferenceNode = false;
            }
        }
        else
        {
            // "If the pointerBeforeReferenceNode attribute value is false, set
            // the referenceNode attribute to the first node preceding the node
            // that is being removed, and terminate these steps."
            var prev = TraversalBinding.PreviousNodeInTree(nodeBeingRemoved);
            if (prev is not null)
            {
                ReferenceNode = prev;
            }
        }
    }

    /// <summary>First node following the last inclusive descendant of
    /// <paramref name="node"/> (i.e. the node after the subtree rooted
    /// at <paramref name="node"/>).</summary>
    private static Node? NextNodeDescendants(Node node)
    {
        for (var n = node; n is not null; n = n.ParentNode)
        {
            if (n.NextSibling is not null)
            {
                return n.NextSibling;
            }
        }
        return null;
    }
}

/// <summary>Numeric constants for the NodeFilter callback return values.</summary>
internal static class NodeFilter
{
    public const uint Accept = 1;
    public const uint Reject = 2;
    public const uint Skip = 3;
}
