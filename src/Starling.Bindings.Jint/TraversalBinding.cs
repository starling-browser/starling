using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using Starling.Dom;

namespace Starling.Bindings.Jint;

/// <summary>
/// DOM §6 — NodeFilter, TreeWalker, NodeIterator for the Jint backend, mirroring
/// <c>Starling.Bindings/TraversalBinding.cs</c>.
///
/// <para><b>NodeFilter (§6.1)</b>: a global function/object carrying the
/// <c>SHOW_*</c> and <c>FILTER_*</c> constants. A filter is a raw function
/// (called directly) or an object with an <c>acceptNode</c> property (fetched
/// fresh every step). Exceptions propagate to the caller.</para>
///
/// <para><b>TreeWalker (§6.2)</b> / <b>NodeIterator (§6.3)</b>: created by
/// <c>document.createTreeWalker</c> / <c>document.createNodeIterator</c>. The
/// host-side walking algorithms are ported literally from WHATWG DOM. The active
/// flag guards against recursive filter calls (throws InvalidStateError).</para>
/// </summary>
internal static class TraversalBinding
{
    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;
        var docProto = ctx.Wrappers.DocumentPrototype;
        if (docProto is null)
        {
            return; // Node bindings must run first.
        }

        InstallNodeFilter(engine);
        var twProto = BuildTreeWalkerProto(ctx);
        var niProto = BuildNodeIteratorProto(ctx);

        // document.createTreeWalker(root, whatToShow?, filter?)
        JintInterop.DefineMethod(engine, docProto, "createTreeWalker", (_, args) =>
        {
            var root = args.Length > 0 ? ctx.Wrappers.UnwrapNode(args[0]) : null;
            if (root is null)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "createTreeWalker: root must be a Node");
            }

            var whatToShow = WhatToShow(args, 1);
            var filterVal = args.Length < 3 || args[2].IsUndefined() ? JsValue.Null : args[2];
            return new JintTreeWalkerObject(ctx, twProto, new HostTreeWalker(ctx, root, whatToShow, filterVal));
        }, 1);

        // document.createNodeIterator(root, whatToShow?, filter?)
        JintInterop.DefineMethod(engine, docProto, "createNodeIterator", (_, args) =>
        {
            var root = args.Length > 0 ? ctx.Wrappers.UnwrapNode(args[0]) : null;
            if (root is null)
            {
                throw new JavaScriptException(engine.Intrinsics.TypeError, "createNodeIterator: root must be a Node");
            }

            var whatToShow = WhatToShow(args, 1);
            var filterVal = args.Length < 3 || args[2].IsUndefined() ? JsValue.Null : args[2];
            return new JintNodeIteratorObject(ctx, niProto, new HostNodeIterator(ctx, root, whatToShow, filterVal));
        }, 1);
    }

    // whatToShow: undefined → 0xFFFFFFFF; null → 0; otherwise ToUint32.
    private static uint WhatToShow(JsValue[] args, int i)
    {
        if (args.Length <= i || args[i].IsUndefined())
        {
            return 0xFFFF_FFFFu;
        }

        if (args[i].IsNull())
        {
            return 0u;
        }

        return TypeConverter.ToUint32(args[i]);
    }

    // ---- NodeFilter global (§6.1) ------------------------------------------

    private static readonly (string Name, uint Val)[] NodeFilterConstants =
    {
        ("FILTER_ACCEPT", 1), ("FILTER_REJECT", 2), ("FILTER_SKIP", 3),
        ("SHOW_ALL", 0xFFFF_FFFF), ("SHOW_ELEMENT", 0x1), ("SHOW_ATTRIBUTE", 0x2),
        ("SHOW_TEXT", 0x4), ("SHOW_CDATA_SECTION", 0x8), ("SHOW_ENTITY_REFERENCE", 0x10),
        ("SHOW_ENTITY", 0x20), ("SHOW_PROCESSING_INSTRUCTION", 0x40), ("SHOW_COMMENT", 0x80),
        ("SHOW_DOCUMENT", 0x100), ("SHOW_DOCUMENT_TYPE", 0x200), ("SHOW_DOCUMENT_FRAGMENT", 0x400),
        ("SHOW_NOTATION", 0x800),
    };

    private static void InstallNodeFilter(global::Jint.Engine engine)
    {
        if (engine.Global.HasOwnProperty("NodeFilter"))
        {
            return;
        }
        // Exposed as a no-op function so `typeof NodeFilter === "function"`, with
        // all constants as own properties.
        var nf = new ClrFunction(engine, "NodeFilter", (_, _) => JsValue.Undefined, 0, PropertyFlag.Configurable);
        foreach (var (name, val) in NodeFilterConstants)
        {
            nf.FastSetProperty(name, new PropertyDescriptor(JintInterop.Num(val), writable: false, enumerable: true, configurable: false));
        }

        JintInterop.DefineDataProp(engine.Global, "NodeFilter", nf, writable: true, enumerable: false, configurable: true);
    }

    // ---- TreeWalker.prototype (§6.2) ---------------------------------------

    private static JsObject BuildTreeWalkerProto(JintBackendContext ctx)
    {
        var engine = ctx.Engine;
        var proto = new JsObject(engine);

        JintInterop.DefineMethod(engine, proto, "toString", (_, _) => JintInterop.Str("[object TreeWalker]"), 0);
        JintInterop.DefineAccessor(engine, proto, "root",
            (t, _) => Walker(t) is { } w ? ctx.Wrappers.Wrap(w.Root) : JsValue.Undefined);
        JintInterop.DefineAccessor(engine, proto, "whatToShow",
            (t, _) => JintInterop.Num(Walker(t)?.WhatToShow ?? 0));
        JintInterop.DefineAccessor(engine, proto, "filter",
            (t, _) => Walker(t) is { } w && !w.Filter.IsNull() && !w.Filter.IsUndefined() ? w.Filter : JsValue.Null);
        JintInterop.DefineAccessor(engine, proto, "currentNode",
            (t, _) => Walker(t) is { } w ? ctx.Wrappers.Wrap(w.CurrentNode) : JsValue.Undefined,
            (t, a) =>
            {
                if (Walker(t) is not { } w)
                {
                    return JsValue.Undefined;
                }

                var node = a.Length > 0 ? ctx.Wrappers.UnwrapNode(a[0]) : null;
                if (node is null)
                {
                    throw new JavaScriptException(engine.Intrinsics.TypeError, "TreeWalker.currentNode must be a Node");
                }

                w.CurrentNode = node;
                return JsValue.Undefined;
            });

        void Method(string name, Func<HostTreeWalker, Node?> m) =>
            JintInterop.DefineMethod(engine, proto, name,
                (t, _) => Walker(t) is { } w && m(w) is { } n ? ctx.Wrappers.Wrap(n) : JsValue.Null, 0);
        Method("parentNode", w => w.ParentNode());
        Method("firstChild", w => w.FirstChild());
        Method("lastChild", w => w.LastChild());
        Method("previousSibling", w => w.PreviousSibling());
        Method("nextSibling", w => w.NextSibling());
        Method("previousNode", w => w.PreviousNode());
        Method("nextNode", w => w.NextNode());

        WireInterface(engine, proto, "TreeWalker");
        return proto;
    }

    private static HostTreeWalker? Walker(JsValue v) => (v as JintTreeWalkerObject)?.Walker;

    // ---- NodeIterator.prototype (§6.3) -------------------------------------

    private static JsObject BuildNodeIteratorProto(JintBackendContext ctx)
    {
        var engine = ctx.Engine;
        var proto = new JsObject(engine);

        JintInterop.DefineMethod(engine, proto, "toString", (_, _) => JintInterop.Str("[object NodeIterator]"), 0);
        JintInterop.DefineAccessor(engine, proto, "root",
            (t, _) => Iter(t) is { } it ? ctx.Wrappers.Wrap(it.Root) : JsValue.Undefined);
        JintInterop.DefineAccessor(engine, proto, "whatToShow",
            (t, _) => JintInterop.Num(Iter(t)?.WhatToShow ?? 0));
        JintInterop.DefineAccessor(engine, proto, "filter",
            (t, _) => Iter(t) is { } it && !it.Filter.IsNull() && !it.Filter.IsUndefined() ? it.Filter : JsValue.Null);
        JintInterop.DefineAccessor(engine, proto, "referenceNode",
            (t, _) => Iter(t) is { } it ? ctx.Wrappers.Wrap(it.ReferenceNode) : JsValue.Undefined);
        JintInterop.DefineAccessor(engine, proto, "pointerBeforeReferenceNode",
            (t, _) => Iter(t) is { } it ? JintInterop.Bool(it.PointerBeforeReferenceNode) : JsValue.Undefined);
        JintInterop.DefineMethod(engine, proto, "nextNode",
            (t, _) => Iter(t) is { } it && it.NextNode() is { } n ? ctx.Wrappers.Wrap(n) : JsValue.Null, 0);
        JintInterop.DefineMethod(engine, proto, "previousNode",
            (t, _) => Iter(t) is { } it && it.PreviousNode() is { } n ? ctx.Wrappers.Wrap(n) : JsValue.Null, 0);
        JintInterop.DefineMethod(engine, proto, "detach", (_, _) => JsValue.Undefined, 0);

        WireInterface(engine, proto, "NodeIterator");
        return proto;
    }

    private static HostNodeIterator? Iter(JsValue v) => (v as JintNodeIteratorObject)?.Iterator;

    // Wire an interface prototype to an illegal-constructor global so
    // `x instanceof TreeWalker` resolves and `[object …]` toStringTag is set.
    private static void WireInterface(global::Jint.Engine engine, ObjectInstance proto, string name)
    {
        var ctor = new ClrFunction(engine, name,
            (_, _) => throw new JavaScriptException(engine.Intrinsics.TypeError, "Illegal constructor"), 0, PropertyFlag.Configurable);
        ctor.Set("prototype", proto);
        JintInterop.DefineDataProp(proto, "constructor", ctor, writable: true, enumerable: false, configurable: true);
        proto.DefineOwnProperty(global::Jint.Native.Symbol.GlobalSymbolRegistry.ToStringTag,
            new PropertyDescriptor(JintInterop.Str(name), writable: false, enumerable: false, configurable: true));
        JintInterop.DefineDataProp(engine.Global, name, ctor, writable: true, enumerable: false, configurable: true);
    }

    // ---- shared filter algorithm (§6.1) ------------------------------------

    /// <summary>DOM §6.1 "filter" — invoke a NodeFilter against a node, returning
    /// FILTER_ACCEPT(1)/REJECT(2)/SKIP(3). Throws InvalidStateError on a recursive
    /// call; propagates any callback exception.</summary>
    internal static uint InvokeFilter(JintBackendContext ctx, JsValue filterVal, Node node, ref bool active)
    {
        if (filterVal.IsNull() || filterVal.IsUndefined())
        {
            return Accept;
        }

        if (active)
        {
            throw DomExceptionBinding.Throw(ctx, "InvalidStateError", "NodeFilter is already active (recursive filter call)");
        }

        active = true;
        try
        {
            var nodeJs = ctx.Wrappers.Wrap(node);
            JsValue result;
            if (filterVal.IsCallable())
            {
                result = filterVal.Call(JsValue.Undefined, new[] { nodeJs });
            }
            else if (filterVal.IsObject())
            {
                var fn = filterVal.AsObject().Get("acceptNode");
                if (!fn.IsCallable())
                {
                    throw new JavaScriptException(ctx.Engine.Intrinsics.TypeError, "NodeFilter object must have a callable 'acceptNode'");
                }

                result = fn.Call(filterVal, new[] { nodeJs });
            }
            else
            {
                return Accept;
            }
            return TypeConverter.ToUint32(result) switch { 1 => Accept, 2 => Reject, 3 => Skip, _ => Skip };
        }
        finally
        {
            active = false;
        }
    }

    /// <summary>DOM §6 — whatToShow bitmask + filter callback.</summary>
    internal static uint FilterNode(JintBackendContext ctx, Node node, uint whatToShow, JsValue filterVal, ref bool active)
    {
        var bit = 1u << (NodeTypeOf(node) - 1);
        if ((whatToShow & bit) == 0)
        {
            return Skip;
        }

        return (filterVal.IsNull() || filterVal.IsUndefined())
            ? Accept
            : InvokeFilter(ctx, filterVal, node, ref active);
    }

    internal static int NodeTypeOf(Node n) => n switch
    {
        Element => 1,
        CData => 4,
        Text => 3,
        ProcessingInstruction => 7,
        Comment => 8,
        Document => 9,
        DocumentType => 10,
        DocumentFragment => 11,
        _ => 0,
    };

    // ---- tree iteration helpers --------------------------------------------

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

    internal const uint Accept = 1;
    internal const uint Reject = 2;
    internal const uint Skip = 3;
}

// ---- exotic wrapper objects -------------------------------------------------

internal sealed class JintTreeWalkerObject : ObjectInstance
{
    public HostTreeWalker Walker { get; }
    public JintTreeWalkerObject(JintBackendContext ctx, ObjectInstance proto, HostTreeWalker walker) : base(ctx.Engine)
    {
        Walker = walker;
        Prototype = proto;
    }
}

internal sealed class JintNodeIteratorObject : ObjectInstance
{
    public HostNodeIterator Iterator { get; }
    public JintNodeIteratorObject(JintBackendContext ctx, ObjectInstance proto, HostNodeIterator iter) : base(ctx.Engine)
    {
        Iterator = iter;
        Prototype = proto;
    }
}

// ---- host TreeWalker (§6.2) -------------------------------------------------

internal sealed class HostTreeWalker
{
    private readonly JintBackendContext _ctx;
    public Node Root { get; }
    public uint WhatToShow { get; }
    public JsValue Filter { get; }
    public Node CurrentNode { get; set; }
    private bool _active;

    public HostTreeWalker(JintBackendContext ctx, Node root, uint whatToShow, JsValue filter)
    {
        _ctx = ctx;
        Root = root;
        WhatToShow = whatToShow;
        Filter = filter;
        CurrentNode = root;
    }

    private uint ApplyFilter(Node node) => TraversalBinding.FilterNode(_ctx, node, WhatToShow, Filter, ref _active);

    private Node? TraverseChildren(bool first)
    {
        var node = first ? CurrentNode.FirstChild : CurrentNode.LastChild;
        while (node is not null)
        {
            var result = ApplyFilter(node);
            if (result == TraversalBinding.Accept) { CurrentNode = node; return node; }
            if (result == TraversalBinding.Skip)
            {
                var child = first ? node.FirstChild : node.LastChild;
                if (child is not null) { node = child; continue; }
            }
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

    private Node? TraverseSiblings(bool next)
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
                var result = ApplyFilter(node);
                if (result == TraversalBinding.Accept) { CurrentNode = node; return node; }
                sibling = (result != TraversalBinding.Reject) ? (next ? node.FirstChild : node.LastChild) : null;
                sibling ??= next ? node.NextSibling : node.PreviousSibling;
            }
            node = node.ParentNode!;
            if (node is null || ReferenceEquals(node, Root))
            {
                return null;
            }

            if (ApplyFilter(node) == TraversalBinding.Accept)
            {
                return null;
            }
        }
    }

    public Node? ParentNode()
    {
        var node = CurrentNode;
        while (!ReferenceEquals(node, Root))
        {
            node = node.ParentNode!;
            if (node is null)
            {
                return null;
            }

            if (ApplyFilter(node) == TraversalBinding.Accept) { CurrentNode = node; return node; }
        }
        return null;
    }

    public Node? FirstChild() => TraverseChildren(true);
    public Node? LastChild() => TraverseChildren(false);
    public Node? NextSibling() => TraverseSiblings(true);
    public Node? PreviousSibling() => TraverseSiblings(false);

    public Node? NextNode()
    {
        var node = CurrentNode;
        var result = TraversalBinding.Accept;
        while (true)
        {
            while (result != TraversalBinding.Reject && node.FirstChild is not null)
            {
                node = node.FirstChild;
                result = ApplyFilter(node);
                if (result == TraversalBinding.Accept) { CurrentNode = node; return node; }
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
            result = ApplyFilter(node);
            if (result == TraversalBinding.Accept) { CurrentNode = node; return node; }
        }
    }

    public Node? PreviousNode()
    {
        var node = CurrentNode;
        while (!ReferenceEquals(node, Root))
        {
            var sibling = node.PreviousSibling;
            while (sibling is not null)
            {
                node = sibling;
                var result = ApplyFilter(node);
                while (result != TraversalBinding.Reject && node.LastChild is not null)
                {
                    node = node.LastChild;
                    result = ApplyFilter(node);
                }
                if (result == TraversalBinding.Accept) { CurrentNode = node; return node; }
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

            if (ApplyFilter(node) == TraversalBinding.Accept) { CurrentNode = node; return node; }
        }
        return null;
    }
}

// ---- host NodeIterator (§6.3) -----------------------------------------------

internal sealed class HostNodeIterator
{
    private readonly JintBackendContext _ctx;
    public Node Root { get; }
    public uint WhatToShow { get; }
    public JsValue Filter { get; }
    public Node ReferenceNode { get; private set; }
    public bool PointerBeforeReferenceNode { get; private set; }
    private bool _active;

    public HostNodeIterator(JintBackendContext ctx, Node root, uint whatToShow, JsValue filter)
    {
        _ctx = ctx;
        Root = root;
        WhatToShow = whatToShow;
        Filter = filter;
        ReferenceNode = root;
        PointerBeforeReferenceNode = true;
    }

    private uint ApplyFilter(Node node) => TraversalBinding.FilterNode(_ctx, node, WhatToShow, Filter, ref _active);

    private Node? Traverse(bool next)
    {
        var node = ReferenceNode;
        var beforeNode = PointerBeforeReferenceNode;
        while (true)
        {
            if (next)
            {
                if (!beforeNode)
                {
                    var nx = TraversalBinding.NextNodeInTree(node);
                    if (nx is null || !TraversalBinding.IsInclusiveDescendant(nx, Root))
                    {
                        return null;
                    }

                    node = nx;
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
                    var pv = TraversalBinding.PreviousNodeInTree(node);
                    if (pv is null || !TraversalBinding.IsInclusiveDescendant(pv, Root))
                    {
                        return null;
                    }

                    node = pv;
                }
                else
                {
                    beforeNode = true;
                }
            }

            if (ApplyFilter(node) == TraversalBinding.Accept)
            {
                ReferenceNode = node;
                PointerBeforeReferenceNode = beforeNode;
                return node;
            }
        }
    }

    public Node? NextNode() => Traverse(true);
    public Node? PreviousNode() => Traverse(false);
}
