using System.Runtime.CompilerServices;
using Starling.Dom;
using Starling.Html;
using Starling.Html.TreeBuilder;
using Starling.Js.Intrinsics;
using Starling.Js.Runtime;

namespace Starling.Bindings;

/// <summary>
/// B5-1 — Installs the Node / Element / Document JS prototypes onto a realm.
/// EventTargetBinding.Install must have run first (these prototypes inherit
/// from EventTargetPrototype).
/// </summary>
/// <remarks>
/// <para><b>Selector grammar:</b> <c>querySelector</c> / <c>querySelectorAll</c>
/// / <c>matches</c> / <c>closest</c> delegate to the full <c>Starling.Css</c>
/// selector engine (<see cref="QuerySelectorEngine"/>), so compound selectors,
/// combinators, attribute selectors, <c>:nth-*</c>, and <c>:is/:where/:not</c>
/// all work. An unparseable selector throws a JS <c>SyntaxError</c>.</para>
/// <para><b>innerHTML / outerHTML / insertAdjacentHTML:</b> these go through the
/// real <c>Starling.Html</c> parser and serializer.
/// <see cref="HtmlTreeBuilder.ParseFragment"/> runs the HTML fragment parsing
/// algorithm (§13.4) against the element as context; <see cref="HtmlSerializer"/>
/// renders the tree back to markup (§13.3). Adding the <c>Starling.Html</c>
/// project reference introduced no cycle — nothing in that project's transitive
/// dependency graph (Common / Dom / Url) refers back to Bindings — so the simpler
/// direct-reference option was taken over a parser-capability seam.</para>
/// </remarks>
public static class NodeBindings
{
    // Per-realm CharacterData / Text / Comment / etc. prototypes, stored so
    // DomWrappers.WrapNode can route wrappers to the correct prototype chain.
    // The tuple holds (TextProto, CommentProto, CDataProto, DocFragProto, PIProto).
    private static readonly ConditionalWeakTable<JsRealm, CharDataProtos> CharDataProtosPerRealm = new();

    internal static JsObject? CharDataProtoFor(JsRealm realm, Node node)
    {
        if (!CharDataProtosPerRealm.TryGetValue(realm, out var p))
        {
            return null;
        }

        return node switch
        {
            Text => p.TextProto,
            CData => p.CDataProto,
            Comment => p.CommentProto,
            ProcessingInstruction => p.PIProto,
            DocumentFragment => p.DocFragProto,
            CharacterData => p.CharDataProto,
            _ => null,
        };
    }

    public static void Install(JsRealm realm)
    {
        ArgumentNullException.ThrowIfNull(realm);
        if (realm.NodePrototype is not null)
        {
            return; // idempotent
        }

        if (realm.EventTargetPrototype is null)
        {
            throw new InvalidOperationException("EventTargetBinding.Install must run before NodeBindings.Install");
        }

        InstallNode(realm);
        InstallAttr(realm);           // WPT-05: Attr extends Node
        InstallNamedNodeMap(realm);   // WPT-05: NamedNodeMap constructor
        InstallCharacterData(realm);  // WPT-03: CharacterData / Text / Comment / PI prototype hierarchy
        InstallElement(realm);
        InstallDocument(realm);

        // Generated bindings: emitted from Web IDL by tools/Starling.IdlGen. These
        // overwrite the mechanical members (simple attributes and methods) defined
        // above with code generated from the spec. Members that need custom
        // marshalling stay with the Starling binding and are on the generator's
        // skip list.
        // Behavioral equivalence is held by the binding + Web Platform Test suites.
        Generated.CoreDomBindingsGenerated.InstallAll(realm);

        // Re-layer behaviors the mechanical generator can't model on top of the
        // generated members it just installed.
        ReapplyScriptSrcAwareSetAttribute(realm);
    }

    /// <summary>The generated <c>setAttribute</c> does the mechanical attribute
    /// write (with the spec arity/exception marshalling). Real browsers also run
    /// HTML §4.12.1 "prepare a script" when <c>src</c> is set on a parser-inserted
    /// empty <c>&lt;script&gt;</c> (the deferred-bundle pattern) or an iframe, so
    /// re-define it to fire that hook after the write. Runs after the generated
    /// install so it wins on Element.prototype.</summary>
    private static void ReapplyScriptSrcAwareSetAttribute(JsRealm realm)
    {
        if (realm.ElementPrototype is not { } elProto)
        {
            return;
        }

        EventTargetBinding.DefineMethod(realm, elProto, "setAttribute", (thisV, args) =>
        {
            var receiver = IdlMarshal.Receiver<Element>(realm, thisV, "Element", "setAttribute");
            var name = IdlMarshal.RequireString(realm, args, 0, "setAttribute", 2);
            var value = IdlMarshal.RequireString(realm, args, 1, "setAttribute", 2);
            try { receiver.SetAttribute(name, value); }
            catch (Starling.Dom.DomException ex) { throw IdlMarshal.Translate(realm, ex); }
            MaybeTriggerScriptSrc(realm, receiver, name);
            return IdlMarshal.Void();
        }, length: 2);
    }

    // =====================================================================
    //                              Node
    // =====================================================================
    private static void InstallNode(JsRealm realm)
    {
        var nodeProto = new JsObject(realm.EventTargetPrototype);
        realm.NodePrototype = nodeProto;

        EventTargetBinding.DefineAccessor(realm, nodeProto, "nodeType",
            (thisV, _) => DomWrappers.UnwrapNode(thisV) is { } n ? JsValue.Number((int)NodeTypeFromKind(n.Kind)) : JsValue.Number(0));
        EventTargetBinding.DefineAccessor(realm, nodeProto, "nodeName",
            (thisV, _) => DomWrappers.UnwrapNode(thisV) is { } n ? JsValue.String(NormalizeNodeName(n)) : JsValue.String(""));
        EventTargetBinding.DefineAccessor(realm, nodeProto, "nodeValue",
            (thisV, _) => DomWrappers.UnwrapNode(thisV)?.NodeValue is { } s ? JsValue.String(s) : JsValue.Null,
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapNode(thisV) is { } n && args.Length > 0)
                {
                    n.NodeValue = args[0].IsNullish ? null : JsValue.ToStringValue(args[0]);
                }

                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, nodeProto, "textContent",
            (thisV, _) => DomWrappers.UnwrapNode(thisV) is { } n ? JsValue.String(n.TextContent) : JsValue.String(""),
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapNode(thisV) is { } n)
                {
                    n.TextContent = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                }

                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, nodeProto, "parentNode",
            (thisV, _) => DomWrappers.UnwrapNode(thisV)?.ParentNode is { } p
                ? JsValue.Object(DomWrappers.Wrap(realm, p)) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, nodeProto, "parentElement",
            (thisV, _) => DomWrappers.UnwrapNode(thisV)?.ParentNode is Element pe
                ? JsValue.Object(DomWrappers.Wrap(realm, pe)) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, nodeProto, "firstChild",
            (thisV, _) => DomWrappers.UnwrapNode(thisV)?.FirstChild is { } c
                ? JsValue.Object(DomWrappers.Wrap(realm, c)) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, nodeProto, "lastChild",
            (thisV, _) => DomWrappers.UnwrapNode(thisV)?.LastChild is { } c
                ? JsValue.Object(DomWrappers.Wrap(realm, c)) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, nodeProto, "nextSibling",
            (thisV, _) => DomWrappers.UnwrapNode(thisV)?.NextSibling is { } c
                ? JsValue.Object(DomWrappers.Wrap(realm, c)) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, nodeProto, "previousSibling",
            (thisV, _) => DomWrappers.UnwrapNode(thisV)?.PreviousSibling is { } c
                ? JsValue.Object(DomWrappers.Wrap(realm, c)) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, nodeProto, "childNodes", (thisV, _) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is not { } n)
            {
                return MakeArray(realm, Array.Empty<JsValue>());
            }

            var items = new List<JsValue>();
            foreach (var c in n.ChildNodes)
            {
                items.Add(JsValue.Object(DomWrappers.Wrap(realm, c)));
            }

            return MakeArray(realm, items);
        });
        EventTargetBinding.DefineAccessor(realm, nodeProto, "ownerDocument", (thisV, _) =>
        {
            var n = DomWrappers.UnwrapNode(thisV);
            if (n is Document)
            {
                return JsValue.Null; // documents have no owner
            }

            return n?.OwnerDocument is { } d ? JsValue.Object(DomWrappers.Wrap(realm, d)) : JsValue.Null;
        });
        EventTargetBinding.DefineAccessor(realm, nodeProto, "baseURI", (thisV, _) =>
        {
            var n = DomWrappers.UnwrapNode(thisV);
            var d = n as Document ?? n?.OwnerDocument;
            return d is { } ? JsValue.String(DocumentBaseUri(realm, d)) : JsValue.String("");
        });

        EventTargetBinding.DefineMethod(realm, nodeProto, "appendChild", (thisV, args) =>
        {
            var parent = DomWrappers.UnwrapNode(thisV);
            if (parent is null)
            {
                throw new JsThrow(realm.NewTypeError("appendChild called on non-Node"));
            }

            if (args.Length == 0 || !args[0].IsObject || DomWrappers.UnwrapNode(args[0]) is null)
            {
                throw new JsThrow(realm.NewTypeError("appendChild requires a Node argument"));
            }

            var child = DomWrappers.UnwrapNode(args[0])!;
            // DOM §4.4.3 — Attr nodes cannot be inserted into a normal node tree.
            if (child is AttrNode)
            {
                throw DomExceptionBinding.Throw(realm, "HierarchyRequestError", "Cannot insert an Attr into a node tree.");
            }

            ValidatePreInsert(realm, parent, child, null);
            try { parent.AppendChild(child); }
            catch (InvalidOperationException ex) { throw NodeMutationException(realm, ex, parent, child); }
            return args[0];
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, nodeProto, "removeChild", (thisV, args) =>
        {
            var parent = DomWrappers.UnwrapNode(thisV);
            if (parent is null)
            {
                throw new JsThrow(realm.NewTypeError("removeChild called on non-Node"));
            }

            if (args.Length == 0 || !args[0].IsObject || DomWrappers.UnwrapNode(args[0]) is null)
            {
                throw new JsThrow(realm.NewTypeError("removeChild requires a Node argument"));
            }

            var child = DomWrappers.UnwrapNode(args[0])!;
            if (!ReferenceEquals(child.ParentNode, parent))
            {
                throw DomExceptionBinding.Throw(realm, "NotFoundError", "The node to be removed is not a child of this node");
            }

            try { parent.RemoveChild(child); }
            catch (InvalidOperationException ex) { throw NodeMutationException(realm, ex, parent, child); }
            return args[0];
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, nodeProto, "insertBefore", (thisV, args) =>
        {
            var parent = DomWrappers.UnwrapNode(thisV);
            if (parent is null)
            {
                throw new JsThrow(realm.NewTypeError("insertBefore called on non-Node"));
            }

            if (args.Length == 0 || !args[0].IsObject || DomWrappers.UnwrapNode(args[0]) is null)
            {
                throw new JsThrow(realm.NewTypeError("insertBefore requires a Node argument"));
            }

            var child = DomWrappers.UnwrapNode(args[0])!;
            var refChild = args.Length > 1 && !args[1].IsNullish ? DomWrappers.UnwrapNode(args[1]) : null;
            // DOM §4.4.3 — Attr nodes cannot be inserted into a normal node tree.
            if (child is AttrNode)
            {
                throw DomExceptionBinding.Throw(realm, "HierarchyRequestError", "Cannot insert an Attr into a node tree.");
            }

            if (refChild is not null && !ReferenceEquals(refChild.ParentNode, parent))
            {
                throw DomExceptionBinding.Throw(realm, "NotFoundError", "The reference node is not a child of this node");
            }

            ValidatePreInsert(realm, parent, child, refChild);
            try { parent.InsertBefore(child, refChild); }
            catch (InvalidOperationException ex) { throw NodeMutationException(realm, ex, parent, child); }
            return args[0];
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, nodeProto, "replaceChild", (thisV, args) =>
        {
            var parent = DomWrappers.UnwrapNode(thisV);
            if (parent is null)
            {
                throw new JsThrow(realm.NewTypeError("replaceChild called on non-Node"));
            }

            if (args.Length < 2 || !args[0].IsObject || !args[1].IsObject
                || DomWrappers.UnwrapNode(args[0]) is null || DomWrappers.UnwrapNode(args[1]) is null)
            {
                throw new JsThrow(realm.NewTypeError("replaceChild requires two Node arguments"));
            }

            var newChild = DomWrappers.UnwrapNode(args[0])!;
            var oldChild = DomWrappers.UnwrapNode(args[1])!;
            // DOM §4.4.3 — Attr nodes cannot be inserted into a normal node tree.
            if (newChild is AttrNode)
            {
                throw DomExceptionBinding.Throw(realm, "HierarchyRequestError", "Cannot insert an Attr into a node tree.");
            }

            if (!ReferenceEquals(oldChild.ParentNode, parent))
            {
                throw DomExceptionBinding.Throw(realm, "NotFoundError", "The child to be replaced is not a child of this node");
            }

            try { parent.ReplaceChild(newChild, oldChild); }
            catch (InvalidOperationException ex) { throw NodeMutationException(realm, ex, parent, newChild); }
            return args[1];
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, nodeProto, "hasChildNodes",
            (thisV, _) => JsValue.Boolean(DomWrappers.UnwrapNode(thisV)?.FirstChild is not null), length: 0);
        // DOM §4.4.4 — cloneNode. Defined on Node so all node types can use it.
        EventTargetBinding.DefineMethod(realm, nodeProto, "cloneNode", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is not { } n)
            {
                return JsValue.Null;
            }

            var deep = args.Length > 0 && JsValue.ToBoolean(args[0]);
            var clone = CloneNode(realm, n, deep);
            return JsValue.Object(DomWrappers.Wrap(realm, clone));
        }, length: 0);
        // DOM §4.4 — normalize: merge adjacent text nodes, remove empty texts.
        EventTargetBinding.DefineMethod(realm, nodeProto, "normalize", (thisV, _) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is { } n)
            {
                NormalizeNode(n);
            }

            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, nodeProto, "contains", (thisV, args) =>
        {
            var self = DomWrappers.UnwrapNode(thisV);
            if (self is null || args.Length == 0)
            {
                return JsValue.False;
            }

            var other = DomWrappers.UnwrapNode(args[0]);
            if (other is null)
            {
                return JsValue.False;
            }

            for (var n = other; n is not null; n = n.ParentNode)
            {
                if (ReferenceEquals(n, self))
                {
                    return JsValue.True;
                }
            }

            return JsValue.False;
        }, length: 1);

        // ---- ChildNode + ParentNode mixins (DOM §4.2.1). Defined on Node.prototype
        // so Element/CharacterData/etc. inherit them; node-or-string args coerce to
        // Text nodes. Built on the host insert/remove primitives.
        EventTargetBinding.DefineMethod(realm, nodeProto, "before", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is { ParentNode: { } parent } node)
            {
                foreach (var n in CoerceNodes(realm, node, args))
                {
                    parent.InsertBefore(n, node);
                }
            }

            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, nodeProto, "after", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is { ParentNode: { } parent } node)
            {
                var refNode = node.NextSibling;
                foreach (var n in CoerceNodes(realm, node, args))
                {
                    parent.InsertBefore(n, refNode);
                }
            }
            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, nodeProto, "replaceWith", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is { ParentNode: { } parent } node)
            {
                var refNode = node.NextSibling;
                var nodes = CoerceNodes(realm, node, args);
                node.RemoveFromParent();
                foreach (var n in nodes)
                {
                    parent.InsertBefore(n, refNode);
                }
            }
            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, nodeProto, "remove", (thisV, _) =>
        {
            DomWrappers.UnwrapNode(thisV)?.RemoveFromParent();
            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, nodeProto, "append", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is { } node)
            {
                foreach (var n in CoerceNodes(realm, node, args))
                {
                    node.AppendChild(n);
                }
            }

            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, nodeProto, "prepend", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is { } node)
            {
                var refNode = node.FirstChild;
                foreach (var n in CoerceNodes(realm, node, args))
                {
                    node.InsertBefore(n, refNode);
                }
            }
            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, nodeProto, "replaceChildren", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is { } node)
            {
                var nodes = CoerceNodes(realm, node, args);
                while (node.FirstChild is { } c)
                {
                    c.RemoveFromParent();
                }

                foreach (var n in nodes)
                {
                    node.AppendChild(n);
                }
            }
            return JsValue.Undefined;
        }, length: 1);

        // Node.moveBefore(node, child) — atomic move within this parent (DOM
        // moveBefore proposal). Modeled as remove-then-insert; returns undefined.
        EventTargetBinding.DefineMethod(realm, nodeProto, "moveBefore", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is { } parent && args.Length > 0 && DomWrappers.UnwrapNode(args[0]) is { } node)
            {
                var refNode = args.Length > 1 ? DomWrappers.UnwrapNode(args[1]) : null;
                node.RemoveFromParent();
                parent.InsertBefore(node, refNode);
            }
            return JsValue.Undefined;
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, nodeProto, "lookupNamespaceURI", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is not { } n)
            {
                return JsValue.Null;
            }

            var r = n.LookupNamespaceURI(args.Length > 0 && !args[0].IsNullish ? JsValue.ToStringValue(args[0]) : null);
            return r is null ? JsValue.Null : JsValue.String(r);
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, nodeProto, "isDefaultNamespace", (thisV, args) =>
            DomWrappers.UnwrapNode(thisV) is { } n
                ? JsValue.Boolean(n.IsDefaultNamespace(args.Length > 0 && !args[0].IsNullish ? JsValue.ToStringValue(args[0]) : null))
                : JsValue.False, length: 1);
        EventTargetBinding.DefineMethod(realm, nodeProto, "lookupPrefix", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is not { } n)
            {
                return JsValue.Null;
            }

            var r = n.LookupPrefix(args.Length > 0 && !args[0].IsNullish ? JsValue.ToStringValue(args[0]) : null);
            return r is null ? JsValue.Null : JsValue.String(r);
        }, length: 1);

        // DOM §4.4 — getRootNode: climb parent chain to the topmost node (WPT-03).
        EventTargetBinding.DefineMethod(realm, nodeProto, "getRootNode", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is not { } n)
            {
                return JsValue.Null;
            }

            _ = args.Length > 0 && args[0].IsObject && JsValue.ToBoolean(args[0].AsObject.Get("composed"));
            var root = n;
            while (root.ParentNode is { } p)
            {
                root = p;
            }

            return JsValue.Object(DomWrappers.Wrap(realm, root));
        }, length: 0);

        // ---- CharacterData mixin (DOM §4.9) — exposed on Node.prototype so
        // Text/Comment/CData/ProcessingInstruction inherit them. Methods that
        // don't apply to non-CharacterData nodes simply return undefined.
        EventTargetBinding.DefineAccessor(realm, nodeProto, "data",
            (thisV, _) => DomWrappers.UnwrapNode(thisV) is CharacterData cd
                ? JsValue.String(cd.Data) : JsValue.Undefined,
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapNode(thisV) is CharacterData cd)
                {
                    cd.Data = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                }

                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, nodeProto, "length",
            (thisV, _) => DomWrappers.UnwrapNode(thisV) is CharacterData cd
                ? JsValue.Number(cd.Data.Length) : JsValue.Undefined);
        EventTargetBinding.DefineMethod(realm, nodeProto, "substringData", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is not CharacterData cd)
            {
                return JsValue.Undefined;
            }

            var offset = args.Length > 0 ? (int)JsValue.ToNumber(args[0]) : 0;
            var count = args.Length > 1 ? (int)JsValue.ToNumber(args[1]) : 0;
            offset = Math.Max(0, Math.Min(offset, cd.Data.Length));
            count = Math.Max(0, Math.Min(count, cd.Data.Length - offset));
            return JsValue.String(cd.Data.Substring(offset, count));
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, nodeProto, "appendData", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is CharacterData cd)
            {
                var s = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                var at = cd.Data.Length;
                cd.Data += s;
                Starling.Dom.DomRange.OnReplaceData(cd, at, 0, s.Length);
            }
            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, nodeProto, "insertData", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is not CharacterData cd)
            {
                return JsValue.Undefined;
            }

            var offset = args.Length > 0 ? (int)JsValue.ToNumber(args[0]) : 0;
            var s = args.Length > 1 ? JsValue.ToStringValue(args[1]) : "";
            offset = Math.Max(0, Math.Min(offset, cd.Data.Length));
            cd.Data = cd.Data[..offset] + s + cd.Data[offset..];
            Starling.Dom.DomRange.OnReplaceData(cd, offset, 0, s.Length);
            return JsValue.Undefined;
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, nodeProto, "deleteData", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is not CharacterData cd)
            {
                return JsValue.Undefined;
            }

            var offset = args.Length > 0 ? (int)JsValue.ToNumber(args[0]) : 0;
            var count = args.Length > 1 ? (int)JsValue.ToNumber(args[1]) : 0;
            offset = Math.Max(0, Math.Min(offset, cd.Data.Length));
            count = Math.Max(0, Math.Min(count, cd.Data.Length - offset));
            cd.Data = cd.Data[..offset] + cd.Data[(offset + count)..];
            Starling.Dom.DomRange.OnReplaceData(cd, offset, count, 0);
            return JsValue.Undefined;
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, nodeProto, "replaceData", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is not CharacterData cd)
            {
                return JsValue.Undefined;
            }

            if (args.Length < 3)
            {
                throw new JsThrow(realm.NewTypeError("replaceData requires 3 arguments"));
            }

            var offset = CdataOffset(args[0]);
            var count = CdataOffset(args[1]);
            var s = JsValue.ToStringValue(args[2]);
            var len = cd.Data.Length;
            if (offset > len)
            {
                throw DomExceptionBinding.Throw(realm, "IndexSizeError", $"Offset {offset} is outside the data length {len}");
            }

            var o = (int)offset;
            var realCount = (int)Math.Min(count, len - o);
            cd.Data = cd.Data[..o] + s + cd.Data[(o + realCount)..];
            Starling.Dom.DomRange.OnReplaceData(cd, o, realCount, s.Length);
            return JsValue.Undefined;
        }, length: 3);
        // Text.splitText(offset) — splits text node at offset. Live-Range
        // adjustment per DOM §5.3.4 "split a Text node": the second half
        // moves to a new node, so ranges whose container is the original
        // text with offset > splitOffset must re-target the new node.
        EventTargetBinding.DefineMethod(realm, nodeProto, "splitText", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is not Text text)
            {
                return JsValue.Null;
            }

            var offset = args.Length > 0 ? (int)JsValue.ToNumber(args[0]) : 0;
            offset = Math.Max(0, Math.Min(offset, text.Data.Length));
            var newText = (text.OwnerDocument ?? new Document()).CreateTextNode(text.Data[offset..]);
            text.Data = text.Data[..offset];
            text.ParentNode?.InsertBefore(newText, text.NextSibling);
            Starling.Dom.DomRange.OnSplitText(text, newText, offset);
            return JsValue.Object(DomWrappers.Wrap(realm, newText));
        }, length: 1);
        // Text.wholeText — concatenation of adjacent text nodes. Simple approximation.
        EventTargetBinding.DefineAccessor(realm, nodeProto, "wholeText",
            (thisV, _) => DomWrappers.UnwrapNode(thisV) is Text t ? JsValue.String(t.Data) : JsValue.Undefined);

        var nodeCtor = new JsNativeFunction(realm, "Node", 0, (_, _) =>
            throw new JsThrow(realm.NewTypeError("Illegal constructor")), isConstructor: false);
        nodeCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(nodeProto), writable: false, enumerable: false, configurable: false));
        nodeProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(nodeCtor), writable: true, enumerable: false, configurable: true));

        // DOM §4.4 nodeType constants — on both Node constructor and Node.prototype
        // so both `Node.ELEMENT_NODE` and `node.ELEMENT_NODE` resolve.
        foreach (var (name, value) in new[] {
            ("ELEMENT_NODE", 1), ("ATTRIBUTE_NODE", 2), ("TEXT_NODE", 3),
            ("CDATA_SECTION_NODE", 4), ("ENTITY_REFERENCE_NODE", 5), ("ENTITY_NODE", 6),
            ("PROCESSING_INSTRUCTION_NODE", 7), ("COMMENT_NODE", 8), ("DOCUMENT_NODE", 9),
            ("DOCUMENT_TYPE_NODE", 10), ("DOCUMENT_FRAGMENT_NODE", 11), ("NOTATION_NODE", 12) })
        {
            var v = JsValue.Number(value);
            nodeCtor.DefineOwnProperty(name, PropertyDescriptor.Data(v, writable: false, enumerable: true, configurable: false));
            nodeProto.DefineOwnProperty(name, PropertyDescriptor.Data(v, writable: false, enumerable: true, configurable: false));
        }

        // DOM §4.4.4 compareDocumentPosition bit-mask constants.
        foreach (var (name, value) in new[] {
            ("DOCUMENT_POSITION_DISCONNECTED", 1), ("DOCUMENT_POSITION_PRECEDING", 2),
            ("DOCUMENT_POSITION_FOLLOWING", 4), ("DOCUMENT_POSITION_CONTAINS", 8),
            ("DOCUMENT_POSITION_CONTAINED_BY", 16), ("DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC", 32) })
        {
            var v = JsValue.Number(value);
            nodeCtor.DefineOwnProperty(name, PropertyDescriptor.Data(v, writable: false, enumerable: true, configurable: false));
            nodeProto.DefineOwnProperty(name, PropertyDescriptor.Data(v, writable: false, enumerable: true, configurable: false));
        }

        // DOM §4.4.4 compareDocumentPosition(other) — returns a bitmask indicating
        // the relative position of `other` with respect to `this`.
        EventTargetBinding.DefineMethod(realm, nodeProto, "compareDocumentPosition", (thisV, args) =>
        {
            var self = DomWrappers.UnwrapNode(thisV);
            var other = args.Length > 0 ? DomWrappers.UnwrapNode(args[0]) : null;
            if (self is null || other is null)
            {
                return JsValue.Number(0);
            }

            if (ReferenceEquals(self, other))
            {
                return JsValue.Number(0);
            }
            // Find the roots; if different, return DISCONNECTED | IMPLEMENTATION_SPECIFIC.
            var rootSelf = NodeRoot(self);
            var rootOther = NodeRoot(other);
            if (!ReferenceEquals(rootSelf, rootOther))
            {
                // DOM §4.4.5: must also set PRECEDING or FOLLOWING consistently.
                // Use a stable implementation-defined order (hash code of the root, then
                // of the node itself) so the comparison is at least internally consistent.
                var selfHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(self);
                var otherHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(other);
                // If other "precedes" (by hash order), set PRECEDING; else FOLLOWING.
                var precOrFollow = (otherHash < selfHash || (otherHash == selfHash && self.GetHashCode() < other.GetHashCode())) ? 2 : 4;
                return JsValue.Number(1 | 32 | precOrFollow); // DISCONNECTED | IMPLEMENTATION_SPECIFIC | (PRECEDING|FOLLOWING)
            }
            // Check containment.
            var selfContainsOther = IsAncestor(self, other);
            var otherContainsSelf = IsAncestor(other, self);
            int bits = 0;
            if (selfContainsOther)
            {
                bits |= 16; // CONTAINED_BY (other is contained by self)
            }

            if (otherContainsSelf)
            {
                bits |= 8; // CONTAINS (self is contained by other, i.e. other contains self)
            }
            // Determine PRECEDING/FOLLOWING by pre-order walk.
            // "preceding" = other comes before self in tree order.
            if (IsBeforeInTreeOrder(other, self))
            {
                bits |= 2; // PRECEDING (other precedes self, so self is after other)
            }
            else
            {
                bits |= 4; // FOLLOWING (other follows self)
            }

            return JsValue.Number(bits);
        }, length: 1);

        // DOM §4.4.3 isSameNode(other) — same as ===.
        EventTargetBinding.DefineMethod(realm, nodeProto, "isSameNode", (thisV, args) =>
        {
            var self = DomWrappers.UnwrapNode(thisV);
            var other = args.Length > 0 ? DomWrappers.UnwrapNode(args[0]) : null;
            return JsValue.Boolean(self is not null && ReferenceEquals(self, other));
        }, length: 1);

        // DOM §4.4.3 isEqualNode(other) — structural equality.
        EventTargetBinding.DefineMethod(realm, nodeProto, "isEqualNode", (thisV, args) =>
        {
            var self = DomWrappers.UnwrapNode(thisV);
            var other = args.Length > 0 ? DomWrappers.UnwrapNode(args[0]) : null;
            return JsValue.Boolean(AreEqual(self, other));
        }, length: 1);

        realm.NodeConstructor = nodeCtor;
        realm.GlobalObject.DefineOwnProperty("Node",
            PropertyDescriptor.Data(JsValue.Object(nodeCtor), writable: true, enumerable: false, configurable: true));
    }

    // =====================================================================
    //                             Element
    // =====================================================================
    private static void InstallElement(JsRealm realm)
    {
        var elProto = new JsObject(realm.NodePrototype);
        realm.ElementPrototype = elProto;

        // HTML §8.1.8 — IDL `on*` event-handler attributes. One accessor pair
        // per GlobalEventHandlers member; the slot machinery lives in
        // EventTargetBinding.
        foreach (var type in EventTargetBinding.ElementEventHandlerTypes)
        {
            EventTargetBinding.DefineEventHandlerAccessor(realm, elProto, type);
        }

        EventTargetBinding.DefineAccessor(realm, elProto, "tagName",
            (thisV, _) =>
            {
                if (DomWrappers.UnwrapElement(thisV) is not { } e)
                {
                    return JsValue.String("");
                }
                // DOM §4.9 — tagName is ASCII-uppercased only for HTML-namespace
                // elements in an HTML document. SVG/MathML/other namespaces keep
                // their original case.
                return JsValue.String(e.Namespace == Element.HtmlNamespace ? e.TagName.ToUpperInvariant() : e.TagName);
            });
        EventTargetBinding.DefineAccessor(realm, elProto, "localName",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.String(e.LocalName) : JsValue.String(""));
        // DOM §4.9 — Element.prefix is the namespace prefix or null; namespaceURI
        // is the element's namespace or null. The data is on the Element model;
        // these accessors expose it (mirrors the Attr accessors below).
        EventTargetBinding.DefineAccessor(realm, elProto, "prefix",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { Prefix: { } p } ? JsValue.String(p) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, elProto, "namespaceURI",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e && !string.IsNullOrEmpty(e.Namespace) ? JsValue.String(e.Namespace) : JsValue.Null);

        // WPT-07: HTMLIFrameElement.contentDocument / contentWindow live on
        // ElementPrototype (same shape as HTMLInputElement.value, below) so
        // every wrapper goes through one accessor that branches on
        // LocalName. Non-iframe elements get null, matching real browsers'
        // IDL behavior.
        EventTargetBinding.DefineAccessor(realm, elProto, "contentDocument", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || !IFrameBinding.IsFrameElement(e))
            {
                return JsValue.Null;
            }

            var ctx = IFrameBinding.EnsureContext(e);
            return JsValue.Object(DomWrappers.Wrap(realm, ctx.Document));
        });
        EventTargetBinding.DefineAccessor(realm, elProto, "contentWindow", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || !IFrameBinding.IsFrameElement(e))
            {
                return JsValue.Null;
            }

            var ctx = IFrameBinding.EnsureContext(e);
            return JsValue.Object(IFrameBinding.EnsureContentWindow(realm, ctx));
        });
        // HTML §4.12.3 — HTMLTemplateElement.content returns the template's
        // content fragment. Non-template elements get null, matching the IDL.
        EventTargetBinding.DefineAccessor(realm, elProto, "content", (thisV, _) =>
            DomWrappers.UnwrapElement(thisV) is HtmlTemplateElement t
                ? JsValue.Object(DomWrappers.Wrap(realm, t.Content)) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, elProto, "id",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.String(e.Id) : JsValue.String(""),
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapElement(thisV) is { } e)
                {
                    e.Id = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                }

                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, elProto, "className",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.String(e.GetAttribute("class") ?? "") : JsValue.String(""),
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapElement(thisV) is { } e)
                {
                    e.SetAttribute("class", args.Length > 0 ? JsValue.ToStringValue(args[0]) : "");
                }

                return JsValue.Undefined;
            });
        // HTMLInputElement / HTMLTextAreaElement `value` IDL attribute. Reads
        // the live value (typed text or a prior assignment), falling back to the
        // `value` content attribute as the initial value. Writing updates the
        // live value so layout re-renders the field with the new text.
        EventTargetBinding.DefineAccessor(realm, elProto, "value",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e
                ? JsValue.String(HtmlFormControls.Value(e)) : JsValue.String(""),
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapElement(thisV) is { } e)
                {
                    HtmlFormControls.SetValue(e, args.Length > 0 ? JsValue.ToStringValue(args[0]) : "");
                }

                return JsValue.Undefined;
            });
        InstallFormControlAccessors(realm, elProto);
        // HTMLElement.focus() / .blur() — move the document focus. The shell
        // reads document.FocusedElement to drive the caret and :focus styling.
        EventTargetBinding.DefineMethod(realm, elProto, "focus", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is { } e && e.OwnerDocument is { } d)
            {
                d.FocusedElement = e;
            }

            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, elProto, "blur", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is { } e && e.OwnerDocument is { } d
                && ReferenceEquals(d.FocusedElement, e))
            {
                d.FocusedElement = null;
            }

            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineAccessor(realm, elProto, "innerHTML",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e
                ? JsValue.String(HtmlSerializer.SerializeChildren(e)) : JsValue.String(""),
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapElement(thisV) is { } e)
                {
                    var markup = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                    var fragment = ParseFragment(e, markup);
                    // Replace all existing children with the parsed nodes.
                    while (e.FirstChild is not null)
                    {
                        e.RemoveChild(e.FirstChild);
                    }

                    e.AppendChild(fragment);
                }
                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, elProto, "outerHTML",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e
                ? JsValue.String(HtmlSerializer.SerializeNode(e)) : JsValue.String(""),
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapElement(thisV) is not { } e)
                {
                    return JsValue.Undefined;
                }

                var parent = e.ParentNode;
                if (parent is null)
                {
                    throw new JsThrow(realm.NewTypeError("Cannot set outerHTML on an element with no parent"));
                }
                // Parse the markup in the context of the element's parent (or the
                // element itself if the parent is the document), then swap it in.
                var context = parent as Element ?? e;
                var markup = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                var fragment = ParseFragment(context, markup);
                parent.InsertBefore(fragment, e);
                parent.RemoveChild(e);
                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, elProto, "children", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e)
            {
                return MakeArray(realm, Array.Empty<JsValue>());
            }

            return BuildHtmlCollection(realm, () =>
            {
                var list = new List<Element>();
                for (var n = e.FirstChild; n is not null; n = n.NextSibling)
                {
                    if (n is Element child)
                    {
                        list.Add(child);
                    }
                }

                return list;
            });
        });
        EventTargetBinding.DefineAccessor(realm, elProto, "firstElementChild", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e)
            {
                return JsValue.Null;
            }

            for (var n = e.FirstChild; n is not null; n = n.NextSibling)
            {
                if (n is Element c)
                {
                    return JsValue.Object(DomWrappers.Wrap(realm, c));
                }
            }

            return JsValue.Null;
        });
        EventTargetBinding.DefineAccessor(realm, elProto, "lastElementChild", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e)
            {
                return JsValue.Null;
            }

            for (var n = e.LastChild; n is not null; n = n.PreviousSibling)
            {
                if (n is Element c)
                {
                    return JsValue.Object(DomWrappers.Wrap(realm, c));
                }
            }

            return JsValue.Null;
        });
        EventTargetBinding.DefineAccessor(realm, elProto, "nextElementSibling", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e)
            {
                return JsValue.Null;
            }

            for (var n = e.NextSibling; n is not null; n = n.NextSibling)
            {
                if (n is Element s)
                {
                    return JsValue.Object(DomWrappers.Wrap(realm, s));
                }
            }

            return JsValue.Null;
        });
        EventTargetBinding.DefineAccessor(realm, elProto, "previousElementSibling", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e)
            {
                return JsValue.Null;
            }

            for (var n = e.PreviousSibling; n is not null; n = n.PreviousSibling)
            {
                if (n is Element s)
                {
                    return JsValue.Object(DomWrappers.Wrap(realm, s));
                }
            }

            return JsValue.Null;
        });
        EventTargetBinding.DefineAccessor(realm, elProto, "childElementCount", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e)
            {
                return JsValue.Number(0);
            }

            var count = 0;
            for (var n = e.FirstChild; n is not null; n = n.NextSibling)
            {
                if (n is Element)
                {
                    count++;
                }
            }

            return JsValue.Number(count);
        });

        // ---- attributes (NamedNodeMap exotic JS object) --------------------
        // DOM §4.9 — element.attributes is a live NamedNodeMap. We build a
        // JsNamedNodeMapObject (exotic) cached on the element wrapper.
        EventTargetBinding.DefineAccessor(realm, elProto, "attributes", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e)
            {
                return JsValue.Null;
            }

            var wrapper = thisV.IsObject ? thisV.AsObject : null;
            if (wrapper is null)
            {
                return JsValue.Null;
            }

            const string cacheKey = "__attributes__";
            var cached = wrapper.Get(cacheKey);
            if (!cached.IsUndefined)
            {
                return cached;
            }

            var nmObj = new JsNamedNodeMapObject(realm, e);
            wrapper.Set(cacheKey, JsValue.Object(nmObj));
            return JsValue.Object(nmObj);
        });

        // ---- getAttributeNode / setAttributeNode / removeAttributeNode ----
        EventTargetBinding.DefineMethod(realm, elProto, "getAttributeNode", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length == 0)
            {
                return JsValue.Null;
            }

            var attr = e.Attributes.GetNamedItem(JsValue.ToStringValue(args[0]));
            return attr is null ? JsValue.Null : JsValue.Object(DomWrappers.WrapAttr(realm, attr));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, elProto, "getAttributeNodeNS", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length < 2)
            {
                return JsValue.Null;
            }

            var ns = args[0].IsNullish ? null : JsValue.ToStringValue(args[0]);
            var attr = e.Attributes.GetNamedItemNS(ns, JsValue.ToStringValue(args[1]));
            return attr is null ? JsValue.Null : JsValue.Object(DomWrappers.WrapAttr(realm, attr));
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, elProto, "setAttributeNode", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length == 0)
            {
                return JsValue.Null;
            }

            if (DomWrappers.UnwrapAttr(args[0]) is not { } attr)
            {
                throw new JsThrow(realm.NewTypeError("setAttributeNode requires an Attr argument"));
            }

            var old = e.Attributes.SetNamedItem(attr);
            return old is null ? JsValue.Null : JsValue.Object(DomWrappers.WrapAttr(realm, old));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, elProto, "setAttributeNodeNS", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length == 0)
            {
                return JsValue.Null;
            }

            if (DomWrappers.UnwrapAttr(args[0]) is not { } attr)
            {
                throw new JsThrow(realm.NewTypeError("setAttributeNodeNS requires an Attr argument"));
            }

            var old = e.Attributes.SetNamedItemNS(attr);
            return old is null ? JsValue.Null : JsValue.Object(DomWrappers.WrapAttr(realm, old));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, elProto, "removeAttributeNode", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length == 0)
            {
                return JsValue.Null;
            }

            if (DomWrappers.UnwrapAttr(args[0]) is not { } attr)
            {
                throw new JsThrow(realm.NewTypeError("removeAttributeNode requires an Attr argument"));
            }
            // Find by reference; if not found throw NotFoundError.
            var found = e.Attributes.GetNamedItem(attr.Name);
            if (found is null || !ReferenceEquals(found, attr))
            {
                throw DomExceptionBinding.Throw(realm, "NotFoundError", "The node was not found.");
            }

            e.Attributes.RemoveNamedItem(attr.Name);
            return JsValue.Object(DomWrappers.WrapAttr(realm, attr));
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, elProto, "getAttribute", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length == 0)
            {
                return JsValue.Null;
            }

            var v = e.GetAttribute(JsValue.ToStringValue(args[0]));
            return v is null ? JsValue.Null : JsValue.String(v);
        }, length: 1);
        // NOTE: `setAttribute` is emitted by the Web IDL generator (mechanical
        // attribute write + arity/exception marshalling). The HTML §4.12.1
        // "prepare a script" hook it can't know about is re-layered on top after
        // the generated installs run — see ReapplyScriptSrcAwareSetAttribute.

        // `script.src` IDL property — get/set the resolved-ish src attribute.
        // The setter mirrors setAttribute('src', …) and runs "prepare a script"
        // so `el.src = url` loads the same way `setAttribute` does. Defined on
        // Element.prototype but only meaningful for <script>; for non-script
        // elements it's a plain attribute round-trip.
        EventTargetBinding.DefineAccessor(realm, elProto, "src",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e
                ? JsValue.String(e.GetAttribute("src") ?? "")
                : JsValue.String(""),
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapElement(thisV) is { } e)
                {
                    var value = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                    e.SetAttribute("src", value);
                    MaybeTriggerScriptSrc(realm, e, "src");
                }
                return JsValue.Undefined;
            });
        // `script.async` IDL property. Reflects the boolean `async` content
        // attribute so `el.async = true` is observable by the engine's script
        // classification (HTML §4.12.1). A script-inserted external script
        // defaults to async; analytics snippets (ga.js/gtag) set this flag, so
        // reflecting it is what lets the engine defer them past first paint.
        EventTargetBinding.DefineAccessor(realm, elProto, "async",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e
                ? JsValue.Boolean(e.HasAttribute("async"))
                : JsValue.False,
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapElement(thisV) is { } e)
                {
                    if (args.Length > 0 && JsValue.ToBoolean(args[0]))
                    {
                        e.SetAttribute("async", "");
                    }
                    else
                    {
                        e.RemoveAttribute("async");
                    }
                }
                return JsValue.Undefined;
            });
        EventTargetBinding.DefineMethod(realm, elProto, "hasAttribute", (thisV, args) =>
            DomWrappers.UnwrapElement(thisV) is { } e && args.Length > 0
                ? JsValue.Boolean(e.HasAttribute(JsValue.ToStringValue(args[0])))
                : JsValue.False, length: 1);
        // DOM §4.9 hasAttributes — true when element has one or more attributes.
        EventTargetBinding.DefineMethod(realm, elProto, "hasAttributes", (thisV, _) =>
            JsValue.Boolean(DomWrappers.UnwrapElement(thisV) is { } e && e.Attributes.Count > 0), length: 0);
        // DOM §4.9 getAttributeNames — returns an array of qualified attribute names.
        EventTargetBinding.DefineMethod(realm, elProto, "getAttributeNames", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e)
            {
                return MakeArray(realm, Array.Empty<JsValue>());
            }

            var names = new List<JsValue>(e.Attributes.Count);
            foreach (var attr in e.Attributes)
            {
                names.Add(JsValue.String(attr.Name));
            }

            return MakeArray(realm, names);
        }, length: 0);
        // DOM §4.9 toggleAttribute(name[, force]) — toggles a boolean attribute.
        EventTargetBinding.DefineMethod(realm, elProto, "toggleAttribute", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length == 0)
            {
                return JsValue.False;
            }

            var name = JsValue.ToStringValue(args[0]);
            bool result;
            if (args.Length > 1 && !args[1].IsUndefined)
            {
                var force = JsValue.ToBoolean(args[1]);
                if (force) { e.SetAttribute(name, ""); result = true; }
                else { e.RemoveAttribute(name); result = false; }
            }
            else if (e.HasAttribute(name)) { e.RemoveAttribute(name); result = false; }
            else { e.SetAttribute(name, ""); result = true; }
            return JsValue.Boolean(result);
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, elProto, "removeAttribute", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is { } e && args.Length > 0)
            {
                e.RemoveAttribute(JsValue.ToStringValue(args[0]));
            }

            return JsValue.Undefined;
        }, length: 1);
        // ---- Namespace-aware attribute methods (DOM §4.9).
        EventTargetBinding.DefineMethod(realm, elProto, "getAttributeNS", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length < 2)
            {
                return JsValue.Null;
            }

            var v = e.GetAttributeNS(args[0].IsNullish ? null : JsValue.ToStringValue(args[0]), JsValue.ToStringValue(args[1]));
            return v is null ? JsValue.Null : JsValue.String(v);
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, elProto, "setAttributeNS", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is { } e && args.Length >= 3)
            {
                e.SetAttributeNS(args[0].IsNullish ? null : JsValue.ToStringValue(args[0]),
                    JsValue.ToStringValue(args[1]), JsValue.ToStringValue(args[2]));
            }

            return JsValue.Undefined;
        }, length: 3);
        EventTargetBinding.DefineMethod(realm, elProto, "hasAttributeNS", (thisV, args) =>
            DomWrappers.UnwrapElement(thisV) is { } e && args.Length >= 2
                ? JsValue.Boolean(e.HasAttributeNS(args[0].IsNullish ? null : JsValue.ToStringValue(args[0]), JsValue.ToStringValue(args[1])))
                : JsValue.False, length: 2);
        EventTargetBinding.DefineMethod(realm, elProto, "removeAttributeNS", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is { } e && args.Length >= 2)
            {
                e.RemoveAttributeNS(args[0].IsNullish ? null : JsValue.ToStringValue(args[0]), JsValue.ToStringValue(args[1]));
            }

            return JsValue.Undefined;
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, elProto, "getElementsByTagNameNS", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length < 2)
            {
                return MakeArray(realm, Array.Empty<JsValue>());
            }

            var ns = args[0].IsNullish ? null : JsValue.ToStringValue(args[0]);
            var local = JsValue.ToStringValue(args[1]);
            return BuildHtmlCollection(realm, () => e.GetElementsByTagNameNS(ns, local).ToList());
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, elProto, "appendChild", (thisV, args) =>
        {
            // Inherits from Node — re-stamped here so JS-level instanceof tests
            // see the property on Element.prototype too. Reuse Node impl via lookup.
            return CallInherited(realm, thisV, args, realm.NodePrototype!, "appendChild");
        }, length: 1);

        // HTMLCanvasElement.getContext — minimal "2d" context. Real raster
        // canvas isn't implemented, but libraries (e.g. @tanstack/table) call
        // canvas.getContext("2d") only to measure text for column sizing
        // (set ctx.font, read ctx.measureText(s).width). We return a context
        // object that supports that path with an approximate metric; other
        // context types and non-canvas elements return null (spec: getContext
        // is a HTMLCanvasElement member that returns null for unknown ids).
        EventTargetBinding.DefineMethod(realm, elProto, "getContext", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e
                || !string.Equals(e.LocalName, "canvas", StringComparison.OrdinalIgnoreCase))
            {
                return JsValue.Null;
            }

            var kind = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            if (kind != "2d")
            {
                return JsValue.Null;
            }

            return JsValue.Object(BuildCanvas2dContext(realm));
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, elProto, "querySelector", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length == 0)
            {
                return JsValue.Null;
            }

            var match = QuerySelectorEngine.First(e, JsValue.ToStringValue(args[0]), realm);
            return match is null ? JsValue.Null : JsValue.Object(DomWrappers.Wrap(realm, match));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, elProto, "querySelectorAll", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length == 0)
            {
                return MakeArray(realm, Array.Empty<JsValue>());
            }

            var items = new List<JsValue>();
            foreach (var m in QuerySelectorEngine.All(e, JsValue.ToStringValue(args[0]), realm))
            {
                items.Add(JsValue.Object(DomWrappers.Wrap(realm, m)));
            }

            return MakeArray(realm, items);
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, elProto, "matches", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length == 0)
            {
                return JsValue.False;
            }

            return JsValue.Boolean(QuerySelectorEngine.Matches(e, JsValue.ToStringValue(args[0]), realm));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, elProto, "closest", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length == 0)
            {
                return JsValue.Null;
            }

            var match = QuerySelectorEngine.Closest(e, JsValue.ToStringValue(args[0]), realm);
            return match is null ? JsValue.Null : JsValue.Object(DomWrappers.Wrap(realm, match));
        }, length: 1);
        // HTMLElement.click() — fire a synthetic click MouseEvent (HTML §click()).
        EventTargetBinding.DefineMethod(realm, elProto, "click", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not null)
            {
                EventTargetBinding.DispatchHostEvent(thisV,
                    new Starling.Dom.Events.MouseEvent("click",
                        new Starling.Dom.Events.EventInit(Bubbles: true, Cancelable: true, Composed: true)));
            }

            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, elProto, "getElementsByTagName", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length == 0)
            {
                return MakeArray(realm, Array.Empty<JsValue>());
            }

            var name = JsValue.ToStringValue(args[0]);
            return BuildHtmlCollection(realm, () =>
            {
                var list = new List<Element>();
                foreach (var d in e.DescendantElements())
                {
                    if (name == "*" || d.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(d);
                    }
                }

                return list;
            });
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, elProto, "getElementsByClassName", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length == 0)
            {
                return MakeArray(realm, Array.Empty<JsValue>());
            }

            var classes = JsValue.ToStringValue(args[0])
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return BuildHtmlCollection(realm, () =>
            {
                var list = new List<Element>();
                foreach (var d in e.DescendantElements())
                {
                    if (classes.Length > 0 && classes.All(d.ClassList.Contains))
                    {
                        list.Add(d);
                    }
                }

                return list;
            });
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, elProto, "remove", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is { } e)
            {
                e.RemoveFromParent();
            }

            return JsValue.Undefined;
        }, length: 0);
        // DOM §4.9 — Element.hasAttributes().
        EventTargetBinding.DefineMethod(realm, elProto, "hasAttributes", (thisV, _) =>
            JsValue.Boolean(DomWrappers.UnwrapElement(thisV) is { } e && e.Attributes.Count > 0), length: 0);
        // DOM §4.9 — Element.getAttributeNode(name) → Attr | null.
        EventTargetBinding.DefineMethod(realm, elProto, "getAttributeNode", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length == 0)
            {
                return JsValue.Null;
            }

            var name = JsValue.ToStringValue(args[0]);
            var attr = e.Attributes.GetNamedItem(name);
            return attr is null ? JsValue.Null : JsValue.Object(DomWrappers.WrapAttr(realm, attr));
        }, length: 1);
        // DOM §4.9 — Element.getAttributeNodeNS(ns, localName) → Attr | null.
        EventTargetBinding.DefineMethod(realm, elProto, "getAttributeNodeNS", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length < 2)
            {
                return JsValue.Null;
            }

            var ns = args[0].IsNullish ? null : JsValue.ToStringValue(args[0]);
            var localName = JsValue.ToStringValue(args[1]);
            var attr = e.Attributes.GetNamedItemNS(ns, localName);
            return attr is null ? JsValue.Null : JsValue.Object(DomWrappers.WrapAttr(realm, attr));
        }, length: 2);
        // DOM §4.9 — Element.toggleAttribute(qualifiedName[, force]).
        EventTargetBinding.DefineMethod(realm, elProto, "toggleAttribute", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e || args.Length == 0)
            {
                return JsValue.False;
            }

            var name = JsValue.ToStringValue(args[0]);
            if (!IsValidName(name))
            {
                throw DomExceptionBinding.Throw(realm, "InvalidCharacterError", $"'{name}' is not a valid attribute name");
            }

            var force = args.Length > 1 && !args[1].IsUndefined ? (bool?)JsValue.ToBoolean(args[1]) : null;
            var has = e.HasAttribute(name);
            if (force.HasValue)
            {
                if (force.Value) { if (!has) { e.SetAttribute(name, ""); } return JsValue.True; }
                else { if (has) { e.RemoveAttribute(name); } return JsValue.False; }
            }
            if (has) { e.RemoveAttribute(name); return JsValue.False; }
            e.SetAttribute(name, "");
            return JsValue.True;
        }, length: 1);
        // DOM §4.9 — Element.getAttributeNames().
        EventTargetBinding.DefineMethod(realm, elProto, "getAttributeNames", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e)
            {
                return MakeArray(realm, Array.Empty<JsValue>());
            }

            var items = new List<JsValue>();
            foreach (var attr in e.Attributes)
            {
                items.Add(JsValue.String(attr.Name));
            }

            return MakeArray(realm, items);
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, elProto, "insertAdjacentHTML", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e)
            {
                throw new JsThrow(realm.NewTypeError("insertAdjacentHTML called on a non-Element"));
            }

            if (args.Length < 2)
            {
                throw new JsThrow(realm.NewTypeError("insertAdjacentHTML requires (position, text)"));
            }

            var position = JsValue.ToStringValue(args[0]);
            var markup = JsValue.ToStringValue(args[1]);

            // §DOM-Parsing: beforebegin/afterend parse against the parent as
            // context; afterbegin/beforeend parse against the element itself.
            switch (position.ToLowerInvariant())
            {
                case "beforebegin":
                    {
                        var parent = e.ParentNode;
                        if (parent is null)
                        {
                            return JsValue.Undefined; // no-op per spec
                        }

                        var context = parent as Element ?? e;
                        parent.InsertBefore(ParseFragment(context, markup), e);
                        break;
                    }
                case "afterbegin":
                    e.InsertBefore(ParseFragment(e, markup), e.FirstChild);
                    break;
                case "beforeend":
                    e.AppendChild(ParseFragment(e, markup));
                    break;
                case "afterend":
                    {
                        var parent = e.ParentNode;
                        if (parent is null)
                        {
                            return JsValue.Undefined; // no-op per spec
                        }

                        var context = parent as Element ?? e;
                        parent.InsertBefore(ParseFragment(context, markup), e.NextSibling);
                        break;
                    }
                default:
                    throw new JsThrow(realm.NewTypeError(
                        $"insertAdjacentHTML: '{position}' is not a valid position"));
            }
            return JsValue.Undefined;
        }, length: 2);

        // DOM Living Standard — insertAdjacentElement(position, element).
        EventTargetBinding.DefineMethod(realm, elProto, "insertAdjacentElement", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e)
            {
                return JsValue.Null;
            }

            if (args.Length < 2)
            {
                return JsValue.Null;
            }

            var position = JsValue.ToStringValue(args[0]).ToLowerInvariant();
            var newEl = DomWrappers.UnwrapElement(args[1]);
            if (newEl is null)
            {
                return JsValue.Null;
            }

            switch (position)
            {
                case "beforebegin": if (e.ParentNode is { } pb) { pb.InsertBefore(newEl, e); } break;
                case "afterbegin": e.InsertBefore(newEl, e.FirstChild); break;
                case "beforeend": e.AppendChild(newEl); break;
                case "afterend": if (e.ParentNode is { } pa) { pa.InsertBefore(newEl, e.NextSibling); } break;
                default: return JsValue.Null;
            }
            return args[1];
        }, length: 2);
        // DOM Living Standard — insertAdjacentText(position, data).
        EventTargetBinding.DefineMethod(realm, elProto, "insertAdjacentText", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e)
            {
                return JsValue.Undefined;
            }

            if (args.Length < 2)
            {
                return JsValue.Undefined;
            }

            var position = JsValue.ToStringValue(args[0]).ToLowerInvariant();
            var data = JsValue.ToStringValue(args[1]);
            var doc = e.OwnerDocument ?? new Document();
            var textNode = doc.CreateTextNode(data);
            switch (position)
            {
                case "beforebegin": if (e.ParentNode is { } pb) { pb.InsertBefore(textNode, e); } break;
                case "afterbegin": e.InsertBefore(textNode, e.FirstChild); break;
                case "beforeend": e.AppendChild(textNode); break;
                case "afterend": if (e.ParentNode is { } pa) { pa.InsertBefore(textNode, e.NextSibling); } break;
            }
            return JsValue.Undefined;
        }, length: 2);
        // ---- cloneNode(deep?) -----------------------------------------------
        // DOM §4.4.4 — shallow clone copies tag + attributes; deep clone also
        // recursively copies all descendant nodes.
        EventTargetBinding.DefineMethod(realm, elProto, "cloneNode", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is not { } n)
            {
                return JsValue.Null;
            }

            var deep = args.Length > 0 && JsValue.ToBoolean(args[0]);
            var clone = CloneNode(realm, n, deep);
            return JsValue.Object(DomWrappers.Wrap(realm, clone));
        }, length: 0);

        // ---- before / after / prepend / append ------------------------------
        // DOM Living Standard — ParentNode / ChildNode mixins.
        // `prepend` / `append` insert at start/end of *this* (treating this as
        // the parent); `before` / `after` insert adjacent to *this* inside its
        // parent. String args become Text nodes.
        EventTargetBinding.DefineMethod(realm, elProto, "prepend", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is not { } parent)
            {
                return JsValue.Undefined;
            }

            var refChild = parent.FirstChild;
            foreach (var arg in args)
            {
                InsertAdjacentNode(realm, parent, arg, before: refChild);
            }

            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, elProto, "append", (thisV, args) =>
        {
            if (DomWrappers.UnwrapNode(thisV) is not { } parent)
            {
                return JsValue.Undefined;
            }

            foreach (var arg in args)
            {
                InsertAdjacentNode(realm, parent, arg, before: null);
            }

            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, elProto, "before", (thisV, args) =>
        {
            var self = DomWrappers.UnwrapNode(thisV);
            if (self?.ParentNode is not { } parent)
            {
                return JsValue.Undefined;
            }

            foreach (var arg in args)
            {
                InsertAdjacentNode(realm, parent, arg, before: self);
            }

            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, elProto, "after", (thisV, args) =>
        {
            var self = DomWrappers.UnwrapNode(thisV);
            if (self?.ParentNode is not { } parent)
            {
                return JsValue.Undefined;
            }

            var refChild = self.NextSibling;
            foreach (var arg in args)
            {
                InsertAdjacentNode(realm, parent, arg, before: refChild);
            }

            return JsValue.Undefined;
        }, length: 0);

        // ---- replaceWith(...nodes) ------------------------------------------
        // Replaces this node with one or more nodes / strings.
        EventTargetBinding.DefineMethod(realm, elProto, "replaceWith", (thisV, args) =>
        {
            var self = DomWrappers.UnwrapNode(thisV);
            if (self?.ParentNode is not { } parent)
            {
                return JsValue.Undefined;
            }

            var refChild = self.NextSibling;
            foreach (var arg in args)
            {
                InsertAdjacentNode(realm, parent, arg, before: refChild);
            }

            self.RemoveFromParent();
            return JsValue.Undefined;
        }, length: 0);

        // ---- innerText getter / setter -------------------------------------
        // HTML §6.1.6.1 — for our purposes the getter returns the rendered
        // text content (same as textContent minus non-text nodes) and the
        // setter sets textContent. The full spec requires layout awareness;
        // this approximation satisfies the common read-existing-text cases.
        EventTargetBinding.DefineAccessor(realm, elProto, "innerText",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.String(e.TextContent) : JsValue.String(""),
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapElement(thisV) is { } e)
                {
                    e.TextContent = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                }

                return JsValue.Undefined;
            });

        // ---- dataset -------------------------------------------------------
        // HTML §2.7.3 HTMLOrSVGElement.dataset — a DOMStringMap giving access
        // to data-* attributes. Property name `el.dataset.fooBar` maps to
        // attribute `data-foo-bar` via camelCase ↔ kebab-case conversion.
        EventTargetBinding.DefineAccessor(realm, elProto, "dataset", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e)
            {
                return JsValue.Null;
            }

            var wrapper = thisV.IsObject ? thisV.AsObject : null;
            if (wrapper is null)
            {
                return JsValue.Null;
            }

            const string cacheKey = "__dataset__";
            var cached = wrapper.Get(cacheKey);
            if (!cached.IsUndefined)
            {
                return cached;
            }

            var dsObj = BuildDataset(realm, e);
            wrapper.Set(cacheKey, JsValue.Object(dsObj));
            return JsValue.Object(dsObj);
        });

        // ---- classList ------------------------------------------------------
        // DOM §7.1 DOMTokenList, exposed via Element.classList. The C# backing
        // object (DomTokenList) already implements add/remove/toggle/contains.
        // Here we build a JS-visible DOMTokenList wrapper per element. The
        // wrapper is lazily created once and cached on the element wrapper so
        // `el.classList === el.classList` holds.
        EventTargetBinding.DefineAccessor(realm, elProto, "classList", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e)
            {
                return JsValue.Null;
            }
            // Cache the DOMTokenList wrapper on the JS element wrapper object
            // so identity is stable across multiple reads.
            var wrapper = thisV.IsObject ? thisV.AsObject : null;
            if (wrapper is null)
            {
                return JsValue.Null;
            }

            const string cacheKey = "__classList__";
            var cached = wrapper.Get(cacheKey);
            if (!cached.IsUndefined)
            {
                return cached;
            }

            var clObj = BuildDomTokenList(realm, e);
            wrapper.Set(cacheKey, JsValue.Object(clObj));
            return JsValue.Object(clObj);
        });

        // Per-interface reflected DOMTokenList attributes (HTML): relList (a,
        // area, link — and SVGAElement), iframe.sandbox, link.sizes,
        // output.htmlFor (reflects "for"). Gated by local name so unrelated
        // elements report undefined; the wrapper is cached for stable identity.
        void DefineTokenListAttr(string jsName, string attr, string ck, Func<Element, bool> applies)
        {
            EventTargetBinding.DefineAccessor(realm, elProto, jsName, (thisV, _) =>
            {
                if (DomWrappers.UnwrapElement(thisV) is not { } e || !applies(e)
                    || thisV.AsObject is not { } w)
                {
                    return JsValue.Undefined;
                }

                var hit = w.Get(ck);
                if (!hit.IsUndefined)
                {
                    return hit;
                }

                var o = JsValue.Object(BuildDomTokenList(realm, e, e.TokenListFor(attr), attr));
                w.Set(ck, o);
                return o;
            });
        }
        const string svgNs = "http://www.w3.org/2000/svg";
        // relList: HTML a/area/link, plus SVGAElement (svg <a>). Other namespaces
        // (MathML, custom, null) report undefined per the IDL.
        DefineTokenListAttr("relList", "rel", "__relList__", e =>
            (e.Namespace == Element.HtmlNamespace && e.LocalName is "a" or "area" or "link")
            || (e.Namespace == svgNs && e.LocalName == "a"));
        DefineTokenListAttr("sandbox", "sandbox", "__sandbox__",
            e => e.Namespace == Element.HtmlNamespace && e.LocalName == "iframe");
        DefineTokenListAttr("sizes", "sizes", "__sizes__",
            e => e.Namespace == Element.HtmlNamespace && e.LocalName == "link");
        DefineTokenListAttr("htmlFor", "for", "__htmlForList__",
            e => e.Namespace == Element.HtmlNamespace && e.LocalName == "output");

        // ---- style ----------------------------------------------------------
        // Returns a per-element inline-style CSSStyleDeclaration-shaped object.
        // The element stores inline styles as a flat string in the `style`
        // attribute; we parse it lazily and expose individual property
        // accessors plus cssText / getPropertyValue / setProperty.
        EventTargetBinding.DefineAccessor(realm, elProto, "style", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { } e)
            {
                return JsValue.Null;
            }

            var wrapper = thisV.IsObject ? thisV.AsObject : null;
            if (wrapper is null)
            {
                return JsValue.Null;
            }

            const string cacheKey = "__style__";
            var cached = wrapper.Get(cacheKey);
            if (!cached.IsUndefined)
            {
                return cached;
            }

            var styleObj = BuildInlineStyleDecl(realm, e);
            wrapper.Set(cacheKey, JsValue.Object(styleObj));
            return JsValue.Object(styleObj);
        });

        // CSSOM §6.5 — HTMLStyleElement.sheet / HTMLLinkElement.sheet returns the
        // associated CSSStyleSheet (or null for non-stylesheet elements).
        EventTargetBinding.DefineAccessor(realm, elProto, "sheet",
            (thisV, _) => CssomBinding.StyleElementSheetAccessor(realm, thisV));

        // ---- CSS Typed OM 1 §6: attributeStyleMap / computedStyleMap -------
        // A StylePropertyMap over the inline `style` attribute (mutable) and a
        // read-only StylePropertyMapReadOnly over the computed style (consults
        // the layout host). Values are CSSStyleValue objects (CssBinding).
        EventTargetBinding.DefineAccessor(realm, elProto, "attributeStyleMap", (thisV, _) =>
            DomWrappers.UnwrapElement(thisV) is { } e
                ? JsValue.Object(BuildInlineStyleMap(realm, e))
                : JsValue.Undefined);
        EventTargetBinding.DefineMethod(realm, elProto, "computedStyleMap", (thisV, _) =>
            DomWrappers.UnwrapElement(thisV) is { } e
                ? JsValue.Object(BuildComputedStyleMap(realm, e))
                : JsValue.Undefined, length: 0);

        // ---- Web Animations 1 §4: element.animate(keyframes, options) ------
        EventTargetBinding.DefineMethod(realm, elProto, "animate", (thisV, args) =>
            DomWrappers.UnwrapElement(thisV) is { } e
                ? WebAnimationsBinding.Animate(realm, e, args)
                : JsValue.Undefined, length: 2);
        // Web Animations 1 §6: element.getAnimations() — the element's live
        // script animations (declarative CSS animations not surfaced yet).
        EventTargetBinding.DefineMethod(realm, elProto, "getAnimations", (thisV, _) =>
            DomWrappers.UnwrapElement(thisV) is { } e
                ? WebAnimationsBinding.GetAnimations(realm, e)
                : JsValue.Object(new JsArray(realm, Array.Empty<JsValue>())), length: 0);

        // Layout-readback APIs — consult the realm's optional ILayoutHost
        // snapshot. With no host (e.g. JS run outside the engine pipeline)
        // they return spec-permitted zeros, matching a never-laid-out doc.
        EventTargetBinding.DefineMethod(realm, elProto, "getBoundingClientRect", (thisV, _) =>
        {
            var host = WindowBinding.LayoutHostForRealm(realm);
            if (DomWrappers.UnwrapElement(thisV) is { } e &&
                host is not null && host.TryGetBoundingClientRect(e, out var r))
            {
                return JsValue.Object(BuildDomRect(realm, r));
            }
            return JsValue.Object(BuildDomRect(realm, default));
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, elProto, "getClientRects", (thisV, _) =>
        {
            // Single-rect simplification — block boxes only return one
            // rect, which covers most layout-readback paths. Inline flows
            // that emit multiple line boxes would need the box tree walk.
            var host = WindowBinding.LayoutHostForRealm(realm);
            if (DomWrappers.UnwrapElement(thisV) is { } e &&
                host is not null && host.TryGetBoundingClientRect(e, out var r))
            {
                return MakeArray(realm, new[] { JsValue.Object(BuildDomRect(realm, r)) });
            }
            return MakeArray(realm, Array.Empty<JsValue>());
        }, length: 0);
        EventTargetBinding.DefineAccessor(realm, elProto, "offsetWidth",
            (thisV, _) => ReadOffsetMetric(realm, thisV, m => m.OffsetWidth));
        EventTargetBinding.DefineAccessor(realm, elProto, "offsetHeight",
            (thisV, _) => ReadOffsetMetric(realm, thisV, m => m.OffsetHeight));
        EventTargetBinding.DefineAccessor(realm, elProto, "offsetTop",
            (thisV, _) => ReadOffsetMetric(realm, thisV, m => m.OffsetTop));
        EventTargetBinding.DefineAccessor(realm, elProto, "offsetLeft",
            (thisV, _) => ReadOffsetMetric(realm, thisV, m => m.OffsetLeft));
        EventTargetBinding.DefineAccessor(realm, elProto, "clientWidth",
            (thisV, _) => ReadOffsetMetric(realm, thisV, m => m.ClientWidth));
        EventTargetBinding.DefineAccessor(realm, elProto, "clientHeight",
            (thisV, _) => ReadOffsetMetric(realm, thisV, m => m.ClientHeight));
        // CSSOM View §7 scrollWidth/scrollHeight — the element's scrolling
        // area, not the offset-size alias it used to be (see ReadScrollSize).
        EventTargetBinding.DefineAccessor(realm, elProto, "scrollWidth",
            (thisV, _) => ReadScrollSize(realm, thisV, vertical: false));
        EventTargetBinding.DefineAccessor(realm, elProto, "scrollHeight",
            (thisV, _) => ReadScrollSize(realm, thisV, vertical: true));
        // CSSOM View §7 scrollTop/scrollLeft. Reads come from the engine's
        // scroll store via ILayoutHost; writes flush layout if dirty, clamp,
        // store, and flag — never relayout (browser-plan/scroll-model.md).
        EventTargetBinding.DefineAccessor(realm, elProto, "scrollTop",
            (thisV, _) => ReadScrollOffset(realm, thisV, vertical: true),
            (thisV, args) => WriteScrollOffset(realm, thisV, args, vertical: true));
        EventTargetBinding.DefineAccessor(realm, elProto, "scrollLeft",
            (thisV, _) => ReadScrollOffset(realm, thisV, vertical: false),
            (thisV, args) => WriteScrollOffset(realm, thisV, args, vertical: false));
        // CSSOM View §7 — element scroll(), scrollTo(), scrollBy(),
        // scrollIntoView(). behavior:"smooth" is accepted and treated as
        // instant (smooth animation is a v1 non-goal).
        EventTargetBinding.DefineMethod(realm, elProto, "scroll",
            (thisV, args) => ScrollElementTo(realm, thisV, args, relative: false), length: 0);
        EventTargetBinding.DefineMethod(realm, elProto, "scrollTo",
            (thisV, args) => ScrollElementTo(realm, thisV, args, relative: false), length: 0);
        EventTargetBinding.DefineMethod(realm, elProto, "scrollBy",
            (thisV, args) => ScrollElementTo(realm, thisV, args, relative: true), length: 0);
        EventTargetBinding.DefineMethod(realm, elProto, "scrollIntoView",
            (thisV, args) => ScrollIntoView(realm, thisV, args), length: 0);

        var elCtor = new JsNativeFunction(realm, "Element", 0, (_, _) =>
            throw new JsThrow(realm.NewTypeError("Illegal constructor")), isConstructor: false);
        elCtor.SetPrototypeOf(realm.NodeConstructor);
        elCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(elProto), writable: false, enumerable: false, configurable: false));
        elProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(elCtor), writable: true, enumerable: false, configurable: true));
        realm.ElementConstructor = elCtor;
        realm.GlobalObject.DefineOwnProperty("Element",
            PropertyDescriptor.Data(JsValue.Object(elCtor), writable: true, enumerable: false, configurable: true));

        // HTMLElement is just a thin alias for now (inherits from Element).
        var htmlElProto = new JsObject(elProto);
        var htmlElCtor = new JsNativeFunction(realm, "HTMLElement", 0, (_, _) =>
            throw new JsThrow(realm.NewTypeError("Illegal constructor")), isConstructor: false);
        htmlElCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(htmlElProto), writable: false, enumerable: false, configurable: false));
        htmlElProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(htmlElCtor), writable: true, enumerable: false, configurable: true));
        DefineHtmlElementHasInstance(realm, htmlElCtor);
        realm.GlobalObject.DefineOwnProperty("HTMLElement",
            PropertyDescriptor.Data(JsValue.Object(htmlElCtor), writable: true, enumerable: false, configurable: true));

        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLButtonElement", "button");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLInputElement", "input");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLOptionElement", "option");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLSelectElement", "select");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLTextAreaElement", "textarea");
        // HTML §4 element interfaces — tag(s) per the WHATWG element-interface table.
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLAnchorElement", "a");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLAreaElement", "area");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLBaseElement", "base");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLQuoteElement", "blockquote", "q");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLBodyElement", "body");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLBRElement", "br");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLCanvasElement", "canvas");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLTableCaptionElement", "caption");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLTableColElement", "col", "colgroup");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLDataElement", "data");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLDataListElement", "datalist");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLModElement", "del", "ins");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLDetailsElement", "details");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLDialogElement", "dialog");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLDivElement", "div");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLDListElement", "dl");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLEmbedElement", "embed");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLFieldSetElement", "fieldset");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLFormElement", "form");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLHeadingElement", "h1", "h2", "h3", "h4", "h5", "h6");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLHeadElement", "head");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLHRElement", "hr");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLHtmlElement", "html");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLIFrameElement", "iframe");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLImageElement", "img");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLLabelElement", "label");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLLegendElement", "legend");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLLIElement", "li");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLLinkElement", "link");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLMapElement", "map");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLMenuElement", "menu");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLMetaElement", "meta");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLMeterElement", "meter");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLObjectElement", "object");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLOListElement", "ol");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLOptGroupElement", "optgroup");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLOutputElement", "output");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLParagraphElement", "p");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLParamElement", "param");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLPictureElement", "picture");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLPreElement", "pre");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLProgressElement", "progress");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLScriptElement", "script");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLSlotElement", "slot");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLSourceElement", "source");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLSpanElement", "span");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLStyleElement", "style");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLTableElement", "table");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLTableSectionElement", "tbody", "thead", "tfoot");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLTableCellElement", "td", "th");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLTemplateElement", "template");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLTableRowElement", "tr");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLTimeElement", "time");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLTitleElement", "title");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLTrackElement", "track");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLUListElement", "ul");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLMediaElement", "audio", "video");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLAudioElement", "audio");
        InstallHtmlElementConstructor(realm, htmlElProto, "HTMLVideoElement", "video");
    }

    private static void InstallHtmlElementConstructor(
        JsRealm realm,
        JsObject htmlElementPrototype,
        string interfaceName,
        params string[] localNames)
    {
        var proto = new JsObject(htmlElementPrototype);
        var ctor = new JsNativeFunction(realm, interfaceName, 0, (_, _) =>
            throw new JsThrow(realm.NewTypeError("Illegal constructor")), isConstructor: false);
        ctor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(proto), writable: false, enumerable: false, configurable: false));
        proto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
        DefineHtmlElementHasInstance(realm, ctor, localNames);
        realm.GlobalObject.DefineOwnProperty(interfaceName,
            PropertyDescriptor.Data(JsValue.Object(ctor), writable: true, enumerable: false, configurable: true));
    }

    private static void DefineHtmlElementHasInstance(JsRealm realm, JsObject ctor, params string[] localNames)
    {
        var hasInstance = new JsNativeFunction(realm, "Symbol.hasInstance", 1, (_, args) =>
        {
            var element = args.Length > 0 ? DomWrappers.UnwrapElement(args[0]) : null;
            if (element is null)
            {
                return JsValue.False;
            }

            if (localNames.Length == 0)
            {
                return JsValue.True;
            }

            foreach (var localName in localNames)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(element.LocalName, localName))
                {
                    return JsValue.True;
                }
            }
            return JsValue.False;
        }, isConstructor: false);
        ctor.DefineOwnProperty(SymbolCtor.HasInstance,
            PropertyDescriptor.Data(JsValue.Object(hasInstance), writable: false, enumerable: false, configurable: true));
    }

    // =====================================================================
    //                            Document
    // =====================================================================
    private static void InstallDocument(JsRealm realm)
    {
        var docProto = new JsObject(realm.NodePrototype);
        realm.DocumentPrototype = docProto;

        // Web Animations 1 §5.3: document.getAnimations() — all live script
        // animations in the document (declarative CSS animations not yet).
        EventTargetBinding.DefineMethod(realm, docProto, "getAnimations", (_, _) =>
            WebAnimationsBinding.GetAnimations(realm, null), length: 0);

        EventTargetBinding.DefineAccessor(realm, docProto, "documentElement", (thisV, _) =>
            DomWrappers.UnwrapDocument(thisV)?.DocumentElement is { } e
                ? JsValue.Object(DomWrappers.Wrap(realm, e)) : JsValue.Null);
        // DOM §4.5 document.doctype — the DocumentType child, or null.
        EventTargetBinding.DefineAccessor(realm, docProto, "doctype", (thisV, _) =>
            DomWrappers.UnwrapDocument(thisV)?.DocType is { } dt
                ? JsValue.Object(DomWrappers.Wrap(realm, dt)) : JsValue.Null);
        // HTML §3.1.5 document.body: the first child of the html element that is
        // a body or frameset element in the HTML namespace — where the html
        // element is the document element only when it is itself an HTML-namespace
        // <html>. A body/frameset that is the root, under a non-HTML <html>, or in
        // another namespace does not count.
        EventTargetBinding.DefineAccessor(realm, docProto, "body", (thisV, _) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d
                || d.DocumentElement is not { LocalName: "html", Namespace: Element.HtmlNamespace } htmlEl)
            {
                return JsValue.Null;
            }

            for (var c = htmlEl.FirstChild; c is not null; c = c.NextSibling)
            {
                if (c is Element { Namespace: Element.HtmlNamespace, LocalName: "body" or "frameset" } b)
                {
                    return JsValue.Object(DomWrappers.Wrap(realm, b));
                }
            }

            return JsValue.Null;
        },
        (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d)
            {
                return JsValue.Undefined;
            }

            var val = args.Length > 0 ? args[0] : JsValue.Undefined;
            // The IDL type is HTMLElement?: a non-element (string, plain object)
            // is a TypeError; an element that is not a body/frameset is a
            // HierarchyRequestError.
            if (!val.IsObject || DomWrappers.UnwrapElement(val) is not { } newEl)
            {
                throw new JsThrow(realm.NewTypeError("document.body must be an HTMLElement"));
            }

            if (newEl is not { Namespace: Element.HtmlNamespace, LocalName: "body" or "frameset" })
            {
                throw DomExceptionBinding.Throw(realm, "HierarchyRequestError",
                    "document.body must be a body or frameset element");
            }
            // Replace the current body/frameset if present; otherwise append to the
            // document element (HierarchyRequestError when there is none).
            Element? current = null;
            if (d.DocumentElement is { LocalName: "html", Namespace: Element.HtmlNamespace } htmlEl2)
            {
                for (var c = htmlEl2.FirstChild; c is not null; c = c.NextSibling)
                {
                    if (c is Element { Namespace: Element.HtmlNamespace, LocalName: "body" or "frameset" } b)
                    { current = b; break; }
                }
            }

            if (current is not null)
            {
                current.ParentNode!.ReplaceChild(newEl, current);
            }
            else if (d.DocumentElement is { } root)
            {
                root.AppendChild(newEl);
            }
            else
            {
                throw DomExceptionBinding.Throw(realm, "HierarchyRequestError",
                    "document.body cannot be set without a document element");
            }

            return JsValue.Undefined;
        });
        EventTargetBinding.DefineAccessor(realm, docProto, "head", (thisV, _) =>
            DomWrappers.UnwrapDocument(thisV)?.Head is { } h
                ? JsValue.Object(DomWrappers.Wrap(realm, h)) : JsValue.Null);
        // HTML document.activeElement — the focused element, or <body> when
        // nothing has focus (never null for a rendered document), matching the
        // spec's fallback so scripts that read activeElement.tagName don't throw.
        EventTargetBinding.DefineAccessor(realm, docProto, "activeElement", (thisV, _) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d)
            {
                return JsValue.Null;
            }

            var el = d.FocusedElement ?? d.Body;
            return el is { } ? JsValue.Object(DomWrappers.Wrap(realm, el)) : JsValue.Null;
        });
        EventTargetBinding.DefineAccessor(realm, docProto, "title", (thisV, _) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d)
            {
                return JsValue.String("");
            }
            // The title element is the first title (in tree order) — an SVG title
            // child of an SVG root, else any title element. Its value is the child
            // text content with ASCII whitespace stripped and collapsed.
            var title = FirstTitleElement(d);
            return JsValue.String(title is null ? "" : StripAndCollapseAsciiWhitespace(title.TextContent));
        },
        (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d)
            {
                return JsValue.Undefined;
            }

            var value = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            var existing = FirstTitleElement(d);
            if (existing is not null) { existing.TextContent = value; return JsValue.Undefined; }
            // No title yet: for an HTML document create one in the head (do nothing
            // if there is no head element). SVG roots are left to the SVG path.
            if (d.DocumentElement is { LocalName: "html", Namespace: Element.HtmlNamespace }
                && d.Head is { } head)
            {
                var t = d.CreateElement("title");
                t.TextContent = value;
                head.AppendChild(t);
            }
            return JsValue.Undefined;
        });
        EventTargetBinding.DefineAccessor(realm, docProto, "URL", (thisV, _) =>
            DomWrappers.UnwrapDocument(thisV) is { } d ? JsValue.String(WindowBinding.UrlFor(realm, d)) : JsValue.String(""));
        EventTargetBinding.DefineAccessor(realm, docProto, "documentURI", (thisV, _) =>
            DomWrappers.UnwrapDocument(thisV) is { } d ? JsValue.String(WindowBinding.UrlFor(realm, d)) : JsValue.String(""));
        EventTargetBinding.DefineAccessor(realm, docProto, "location", (thisV, _) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d)
            {
                return JsValue.Null;
            }
            // A document with no browsing context (createDocument/createHTMLDocument)
            // has a null location.
            if (!WindowBinding.DocumentHasBrowsingContext(realm, d))
            {
                return JsValue.Null;
            }

            return JsValue.Object(WindowBinding.LocationObjectFor(realm, d));
        });
        EventTargetBinding.DefineAccessor(realm, docProto, "defaultView", (thisV, _) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d)
            {
                return JsValue.Null;
            }
            // The realm's own document is associated with the realm's global.
            if (ReferenceEquals(DomWrappers.UnwrapDocument(realm.GlobalObject.Get("document")), d))
            {
                return JsValue.Object(realm.GlobalObject);
            }
            // An iframe's content document resolves to its contentWindow.
            if (IFrameBinding.WindowForDocument(realm, d) is { } w)
            {
                return JsValue.Object(w);
            }
            // createHTMLDocument / createDocument produce documents with no
            // browsing context, so their defaultView is null.
            return JsValue.Null;
        });
        EventTargetBinding.DefineAccessor(realm, docProto, "readyState", (thisV, _) =>
            JsValue.String("complete"));
        // DOM §4.5 — character encoding accessors. characterSet is canonical;
        // charset and inputEncoding are historical aliases. The runner decodes
        // every page as UTF-8.
        EventTargetBinding.DefineAccessor(realm, docProto, "characterSet", (_, _) => JsValue.String("UTF-8"));
        EventTargetBinding.DefineAccessor(realm, docProto, "charset", (_, _) => JsValue.String("UTF-8"));
        EventTargetBinding.DefineAccessor(realm, docProto, "inputEncoding", (_, _) => JsValue.String("UTF-8"));
        // DOM §4.5 — document.contentType: "text/html" for an HTML document,
        // "application/xml" for one made by createDocument / XML parsing.
        EventTargetBinding.DefineAccessor(realm, docProto, "contentType", (thisV, _) =>
        {
            var d = DomWrappers.UnwrapDocument(thisV);
            // An explicit ContentType (set by createDocument from the root
            // namespace) wins; otherwise fall back to the HTML/XML default.
            var ct = d?.ContentType ?? (d is { IsHtml: false } ? "application/xml" : "text/html");
            return JsValue.String(ct);
        });
        // DOM §4.4 — a Document node's textContent is null (overrides Node's
        // descendant-text concatenation); setting it is a no-op.
        EventTargetBinding.DefineAccessor(realm, docProto, "textContent",
            (_, _) => JsValue.Null, (_, _) => JsValue.Undefined);
        // DOM §4.5 — document.doctype: the DocumentType child of the document, or null.
        EventTargetBinding.DefineAccessor(realm, docProto, "doctype", (thisV, _) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d)
            {
                return JsValue.Null;
            }

            for (var c = d.FirstChild; c is not null; c = c.NextSibling)
            {
                if (c is DocumentType dt)
                {
                    return JsValue.Object(DomWrappers.Wrap(realm, dt));
                }
            }

            return JsValue.Null;
        });
        // HTML §8.4.1 — document.compatMode returns "CSS1Compat" when the
        // document is in no-quirks or limited-quirks mode. jQuery 1.x reads
        // this on boot to decide the scrollSize calculation branch.
        EventTargetBinding.DefineAccessor(realm, docProto, "compatMode", (thisV, _) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d)
            {
                return JsValue.String("CSS1Compat");
            }

            return JsValue.String(d.Mode == QuirksMode.Quirks ? "BackCompat" : "CSS1Compat");
        });
        // Page Visibility API §4 — document.hidden / visibilityState.
        // Starling has no background-tab concept; the document is always visible.
        EventTargetBinding.DefineAccessor(realm, docProto, "hidden",
            (_, _) => JsValue.False);
        EventTargetBinding.DefineAccessor(realm, docProto, "visibilityState",
            (_, _) => JsValue.String("visible"));
        // HTML §6.5 document.hasFocus(). Same single-document model: the page
        // is always the focused, foreground document.
        EventTargetBinding.DefineMethod(realm, docProto, "hasFocus",
            (_, _) => JsValue.True, length: 0);

        // CSSOM §6.1 — document.styleSheets returns a StyleSheetList over the
        // document's <style>/<link rel=stylesheet> elements in tree order.
        EventTargetBinding.DefineAccessor(realm, docProto, "styleSheets",
            (thisV, _) => CssomBinding.StyleSheetsAccessor(realm, thisV));

        // DOM §4.5 — document.implementation returns a DOMImplementation object.
        // One singleton per document is fine (cached on the wrapper with a hidden key).
        EventTargetBinding.DefineAccessor(realm, docProto, "implementation", (thisV, _) =>
        {
            var wrapper = thisV.IsObject ? thisV.AsObject : null;
            if (wrapper is null)
            {
                return JsValue.Null;
            }

            const string cacheKey = "__domImpl__";
            var cached = wrapper.Get(cacheKey);
            if (!cached.IsUndefined)
            {
                return cached;
            }

            var impl = BuildDomImplementation(realm, DomWrappers.UnwrapDocument(thisV));
            wrapper.Set(cacheKey, JsValue.Object(impl));
            return JsValue.Object(impl);
        });

        EventTargetBinding.DefineMethod(realm, docProto, "getElementById", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d || args.Length == 0)
            {
                return JsValue.Null;
            }

            var id = JsValue.ToStringValue(args[0]);
            // The empty string never matches an id (elements with no id attribute
            // would otherwise match spuriously).
            if (id.Length == 0)
            {
                return JsValue.Null;
            }

            return d.GetElementById(id) is { } e
                ? JsValue.Object(DomWrappers.Wrap(realm, e)) : JsValue.Null;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, docProto, "getElementsByTagName", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d || args.Length == 0)
            {
                return MakeArray(realm, Array.Empty<JsValue>());
            }

            var name = JsValue.ToStringValue(args[0]);
            return BuildHtmlCollection(realm, () => d.GetElementsByTagName(name));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, docProto, "getElementsByTagNameNS", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d || args.Length < 2)
            {
                return MakeArray(realm, Array.Empty<JsValue>());
            }

            var ns = args[0].IsNullish ? null : JsValue.ToStringValue(args[0]);
            var local = JsValue.ToStringValue(args[1]);
            return BuildHtmlCollection(realm, () => d.GetElementsByTagNameNS(ns, local));
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, docProto, "getElementsByClassName", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d || args.Length == 0)
            {
                return MakeArray(realm, Array.Empty<JsValue>());
            }

            var cls = JsValue.ToStringValue(args[0]);
            return BuildHtmlCollection(realm, () => d.GetElementsByClassName(cls));
        }, length: 1);
        // HTML §3.1.5 — document.getElementsByName(name): a live NodeList of all
        // elements (any namespace) whose `name` content attribute equals name, in
        // tree order.
        EventTargetBinding.DefineMethod(realm, docProto, "getElementsByName", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d || args.Length == 0)
            {
                return BuildNodeList(realm, static () => Array.Empty<Node>());
            }

            var name = JsValue.ToStringValue(args[0]);
            return BuildNodeList(realm,
                () => d.DescendantElements().Where(e => e.GetAttribute("name") == name).ToList<Node>());
        }, length: 1);
        // HTML §3.1.5 document named collections — each a live HTMLCollection of
        // HTML-namespace elements of a given kind (so namedItem / [name] work).
        void DefineDocCollection(string prop, Func<Element, bool> match)
            => EventTargetBinding.DefineAccessor(realm, docProto, prop, (thisV, _) =>
                DomWrappers.UnwrapDocument(thisV) is { } dc
                    ? BuildHtmlCollection(realm,
                        () => dc.DescendantElements()
                            .Where(e => e.Namespace == Element.HtmlNamespace && match(e)).ToList())
                    : MakeArray(realm, Array.Empty<JsValue>()));
        DefineDocCollection("images", e => e.LocalName == "img");
        DefineDocCollection("forms", e => e.LocalName == "form");
        DefineDocCollection("scripts", e => e.LocalName == "script");
        DefineDocCollection("embeds", e => e.LocalName == "embed");
        DefineDocCollection("plugins", e => e.LocalName == "embed");
        // links: a/area elements that have an href attribute.
        DefineDocCollection("links", e => e.LocalName is "a" or "area" && e.HasAttribute("href"));
        // anchors: a elements that have a name attribute.
        DefineDocCollection("anchors", e => e.LocalName == "a" && e.HasAttribute("name"));
        EventTargetBinding.DefineMethod(realm, docProto, "querySelector", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d || args.Length == 0)
            {
                return JsValue.Null;
            }

            var match = QuerySelectorEngine.First(d, JsValue.ToStringValue(args[0]), realm);
            return match is null ? JsValue.Null : JsValue.Object(DomWrappers.Wrap(realm, match));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, docProto, "querySelectorAll", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d || args.Length == 0)
            {
                return MakeArray(realm, Array.Empty<JsValue>());
            }

            var items = new List<JsValue>();
            foreach (var m in QuerySelectorEngine.All(d, JsValue.ToStringValue(args[0]), realm))
            {
                items.Add(JsValue.Object(DomWrappers.Wrap(realm, m)));
            }

            return MakeArray(realm, items);
        }, length: 1);
        // ---- DOM §4.9 createAttribute / createAttributeNS -----------------
        EventTargetBinding.DefineMethod(realm, docProto, "createAttribute", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d)
            {
                throw new JsThrow(realm.NewTypeError("createAttribute called on non-Document"));
            }
            // DOM Living Standard: null/undefined are coerced to string first.
            // The only invalid name is the empty string (after coercion).
            var name = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            if (name.Length == 0)
            {
                throw DomExceptionBinding.Throw(realm, "InvalidCharacterError", "The attribute name must not be empty.");
            }
            // HTML documents lower-case; XML documents preserve case.
            if (d.IsHtml)
            {
                name = name.ToLowerInvariant();
            }

            var attr = d.CreateAttribute(name);
            return JsValue.Object(DomWrappers.WrapAttr(realm, attr));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, docProto, "createAttributeNS", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d)
            {
                throw new JsThrow(realm.NewTypeError("createAttributeNS called on non-Document"));
            }

            if (args.Length < 2)
            {
                throw new JsThrow(realm.NewTypeError("createAttributeNS requires (namespace, qualifiedName)"));
            }

            var ns = args[0].IsNullish ? null : JsValue.ToStringValue(args[0]);
            var qname = args[1].IsNullish ? "" : JsValue.ToStringValue(args[1]);
            ValidateQualifiedName(ThrowRealmFor(realm, d), ns, qname); // cross-realm-aware throw
            var attr = d.CreateAttributeNS(ns, qname);
            return JsValue.Object(DomWrappers.WrapAttr(realm, attr));
        }, length: 2);

        EventTargetBinding.DefineMethod(realm, docProto, "createElement", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d || args.Length == 0)
            {
                throw new JsThrow(realm.NewTypeError("createElement requires a tag name"));
            }

            var name = JsValue.ToStringValue(args[0]);
            // DOM §4.5: an invalid Name throws InvalidCharacterError.
            if (!IsValidName(name))
            {
                throw DomExceptionBinding.Throw(ThrowRealmFor(realm, d), "InvalidCharacterError", $"'{name}' is not a valid element name");
            }
            // An HTML document lowercases the name; an XML document preserves its
            // case (and uses the null namespace).
            var el = d.IsHtml ? d.CreateElement(name) : d.CreateElementNS(null, name);
            return JsValue.Object(DomWrappers.Wrap(realm, el));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, docProto, "createElementNS", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d || args.Length < 2)
            {
                throw new JsThrow(realm.NewTypeError("createElementNS requires (namespace, qualifiedName)"));
            }

            var ns = args[0].IsNullish ? null : JsValue.ToStringValue(args[0]);
            // qualifiedName is a non-nullable DOMString: null -> "null".
            var qname = JsValue.ToStringValue(args[1]);
            // Validation errors must come from the target document's realm (its
            // DOMException), which differs from the caller's realm when `d` is a
            // cross-realm iframe contentDocument.
            ValidateQualifiedName(ThrowRealmFor(realm, d), ns, qname);
            return JsValue.Object(DomWrappers.Wrap(realm, d.CreateElementNS(ns, qname)));
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, docProto, "createEvent", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is null)
            {
                throw new JsThrow(realm.NewTypeError("createEvent called on non-Document"));
            }

            return EventTargetBinding.CreateLegacyEvent(realm, args.Length > 0 ? JsValue.ToStringValue(args[0]) : "");
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, docProto, "createTextNode", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d)
            {
                throw new JsThrow(realm.NewTypeError("createTextNode called on non-Document"));
            }

            var t = d.CreateTextNode(args.Length > 0 ? JsValue.ToStringValue(args[0]) : "");
            return JsValue.Object(DomWrappers.Wrap(realm, t));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, docProto, "createProcessingInstruction", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d || args.Length < 2)
            {
                throw new JsThrow(realm.NewTypeError("createProcessingInstruction requires (target, data)"));
            }

            return JsValue.Object(DomWrappers.Wrap(realm,
                d.CreateProcessingInstruction(JsValue.ToStringValue(args[0]), JsValue.ToStringValue(args[1]))));
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, docProto, "createComment", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d)
            {
                throw new JsThrow(realm.NewTypeError("createComment called on non-Document"));
            }

            var c = d.CreateComment(args.Length > 0 ? JsValue.ToStringValue(args[0]) : "");
            return JsValue.Object(DomWrappers.Wrap(realm, c));
        }, length: 1);
        // DOM §4.6 — createCDATASection(data). Only valid in XML documents per spec;
        // this binding accepts it unconditionally (browsers do the same in non-strict mode).
        EventTargetBinding.DefineMethod(realm, docProto, "createCDATASection", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d)
            {
                throw new JsThrow(realm.NewTypeError("createCDATASection called on non-Document"));
            }

            var data = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            if (data.Contains("]]>", StringComparison.Ordinal))
            {
                throw DomExceptionBinding.Throw(realm, "InvalidCharacterError", "CDATA section data must not contain ']]>'");
            }

            return JsValue.Object(DomWrappers.Wrap(realm, d.CreateCDataSection(data)));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, docProto, "createDocumentFragment", (thisV, _) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is not { } d)
            {
                throw new JsThrow(realm.NewTypeError("createDocumentFragment called on non-Document"));
            }

            return JsValue.Object(DomWrappers.Wrap(realm, d.CreateDocumentFragment()));
        }, length: 0);
        // DOM §4.5 — document.adoptNode(node): moves a node from its document into this one.
        EventTargetBinding.DefineMethod(realm, docProto, "adoptNode", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is null || args.Length == 0)
            {
                throw new JsThrow(realm.NewTypeError("adoptNode requires a Node argument"));
            }

            if (DomWrappers.UnwrapNode(args[0]) is null)
            {
                throw new JsThrow(realm.NewTypeError("adoptNode requires a Node argument"));
            }
            // Adopt: remove from current parent. The ownerDocument change would require
            // walking the subtree; as a simplification we just return the node.
            var node = DomWrappers.UnwrapNode(args[0])!;
            node.RemoveFromParent();
            return args[0];
        }, length: 1);
        // DOM §4.5 — document.importNode(node, deep?): clone into this document.
        EventTargetBinding.DefineMethod(realm, docProto, "importNode", (thisV, args) =>
        {
            if (DomWrappers.UnwrapDocument(thisV) is null || args.Length == 0)
            {
                throw new JsThrow(realm.NewTypeError("importNode requires a Node argument"));
            }

            if (DomWrappers.UnwrapNode(args[0]) is not { } src)
            {
                throw new JsThrow(realm.NewTypeError("importNode requires a Node argument"));
            }

            var deep = args.Length > 1 && JsValue.ToBoolean(args[1]);
            var clone = CloneNode(realm, src, deep);
            return JsValue.Object(DomWrappers.Wrap(realm, clone));
        }, length: 1);

        // DOM §6 — traversal: NodeFilter global + createTreeWalker + createNodeIterator.
        TraversalBinding.Install(realm, docProto);

        // DOM §4.5 — "new Document()" creates an XML document (no HTML semantics).
        // Not blocked in the spec; creating a Document via the constructor is valid.
        var docCtor = new JsNativeFunction(realm, "Document", 0, (_, _) =>
        {
            // DOM §4.5 — new Document() creates a new empty XML document.
            var doc = new Document();
            return JsValue.Object(DomWrappers.Wrap(realm, doc));
        }, isConstructor: true);
        docCtor.SetPrototypeOf(realm.NodeConstructor!);
        docCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(docProto), writable: false, enumerable: false, configurable: false));
        docProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(docCtor), writable: true, enumerable: false, configurable: true));
        realm.DocumentConstructor = docCtor;
        realm.GlobalObject.DefineOwnProperty("Document",
            PropertyDescriptor.Data(JsValue.Object(docCtor), writable: true, enumerable: false, configurable: true));

        // XMLDocument (DOM §4.5) — the interface of a document produced by
        // createDocument / DOMParser XML parsing. Its prototype inherits from
        // Document.prototype; a non-HTML Document wraps with it.
        var xmlDocProto = new JsObject(docProto);
        realm.XmlDocumentPrototype = xmlDocProto;
        var xmlDocCtor = new JsNativeFunction(realm, "XMLDocument", 0, (_, _) =>
            throw new JsThrow(realm.NewTypeError("Illegal constructor")), isConstructor: false);
        xmlDocCtor.SetPrototypeOf(docCtor);
        xmlDocCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(xmlDocProto), writable: false, enumerable: false, configurable: false));
        xmlDocProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(xmlDocCtor), writable: true, enumerable: false, configurable: true));
        realm.GlobalObject.DefineOwnProperty("XMLDocument",
            PropertyDescriptor.Data(JsValue.Object(xmlDocCtor), writable: true, enumerable: false, configurable: true));
        // HTMLDocument is an alias for Document (HTML §3.1).
        realm.GlobalObject.DefineOwnProperty("HTMLDocument",
            PropertyDescriptor.Data(JsValue.Object(docCtor), writable: true, enumerable: false, configurable: true));
        // DOMImplementation — expose as window.DOMImplementation (missing-ctor bucket).
        var domImplProto = new JsObject(realm.ObjectPrototype);
        var domImplCtor = new JsNativeFunction(realm, "DOMImplementation", 0, (_, _) =>
            throw new JsThrow(realm.NewTypeError("Illegal constructor")), isConstructor: false);
        domImplCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(domImplProto), writable: false, enumerable: false, configurable: false));
        realm.GlobalObject.DefineOwnProperty("DOMImplementation",
            PropertyDescriptor.Data(JsValue.Object(domImplCtor), writable: true, enumerable: false, configurable: true));
    }

    // ---- helpers ---------------------------------------------------------

    private static string DocumentBaseUri(JsRealm realm, Document doc)
    {
        var documentUrl = WindowBinding.UrlFor(realm, doc);
        foreach (var el in doc.GetElementsByTagName("base"))
        {
            var href = el.GetAttribute("href");
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            if (Uri.TryCreate(href, UriKind.Absolute, out var absolute))
            {
                return absolute.ToString();
            }

            if (Uri.TryCreate(documentUrl, UriKind.Absolute, out var baseUri)
                && Uri.TryCreate(baseUri, href, out var resolved))
            {
                return resolved.ToString();
            }

            break;
        }
        return documentUrl;
    }

    /// <summary>Build a DOMImplementation JS object for the given realm.
    /// DOM §4.5 — exposes <c>createHTMLDocument([title])</c>,
    /// <c>createDocumentFragment()</c>, and <c>hasFeature()</c> (always true).
    /// The returned document is wrapped with the realm's full Document prototype
    /// so all Document methods work on it.</summary>
    private static JsObject BuildDomImplementation(JsRealm realm, Document? ownerDoc)
    {
        var impl = new JsObject(realm.ObjectPrototype);

        // createHTMLDocument([title]) — DOM §4.5.1
        EventTargetBinding.DefineMethod(realm, impl, "createHTMLDocument", (_, args) =>
        {
            // title arg: if absent → null (no title el); if present (even "") → create title
            string? title = args.Length > 0 ? JsValue.ToStringValue(args[0]) : null;
            var doc = Document.CreateHtmlDocument(title);
            return JsValue.Object(DomWrappers.Wrap(realm, doc));
        }, length: 1);

        // createDocumentType(qualifiedName, publicId, systemId) — DOM §4.5.1
        EventTargetBinding.DefineMethod(realm, impl, "createDocumentType", (_, args) =>
        {
            var qname = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            var publicId = args.Length > 1 ? JsValue.ToStringValue(args[1]) : "";
            var systemId = args.Length > 2 ? JsValue.ToStringValue(args[2]) : "";
            // createDocumentType validation is legacy-loose (see the WPT cases):
            // leading digits, lone punctuation ({, @, '), and an empty prefix or
            // local part (":foo", "foo:", "prefix::local") are all accepted. A
            // name is rejected only when it contains a '>' or ASCII whitespace,
            // which would break the "<!DOCTYPE name>" serialization.
            if (qname.Any(c => c is '>' or ' ' or '\t' or '\n' or '\r' or '\f'))
            {
                throw DomExceptionBinding.Throw(realm, "InvalidCharacterError", $"'{qname}' is not a valid doctype name");
            }
            // The new doctype's node document is the implementation's document, so
            // its ownerDocument is non-null even before it is inserted into a tree.
            var dt = ownerDoc is { } d
                ? d.CreateDocumentType(qname, publicId, systemId)
                : new DocumentType(qname, publicId, systemId);
            return JsValue.Object(DomWrappers.Wrap(realm, dt));
        }, length: 3);

        // createDocument(namespace, qualifiedName, doctype) — DOM §4.5.1. Builds
        // an XML document; if a qualified name is given it becomes the document
        // element, and a passed doctype is inserted first.
        EventTargetBinding.DefineMethod(realm, impl, "createDocument", (_, args) =>
        {
            var ns = args.Length > 0 && !args[0].IsNullish ? JsValue.ToStringValue(args[0]) : null;
            // qualifiedName is [LegacyNullToEmptyString] DOMString: only an actual
            // null maps to "", while undefined (and an omitted argument) stringify
            // to "undefined" and therefore produce a <undefined> root element.
            var qnameVal = args.Length > 1 ? args[1] : JsValue.Undefined;
            var qname = qnameVal.IsNull ? "" : JsValue.ToStringValue(qnameVal);
            // DOM §4.5.1 — validate the qualified name (InvalidCharacterError /
            // NamespaceError) before building anything.
            if (qname.Length != 0)
            {
                ValidateQualifiedName(realm, ns, qname);
            }
            // DOM §4.5.1 step 7 — the content type is derived from the namespace:
            // the HTML namespace → application/xhtml+xml, SVG → image/svg+xml,
            // anything else (or none) → application/xml.
            var contentType = ns switch
            {
                "http://www.w3.org/1999/xhtml" => "application/xhtml+xml",
                "http://www.w3.org/2000/svg" => "image/svg+xml",
                _ => "application/xml",
            };
            var doc = new Document { IsHtml = false, ContentType = contentType }; // XML document — preserve name case
            if (args.Length > 2 && DomWrappers.UnwrapAs<DocumentType>(args[2]) is { } dt)
            {
                doc.AppendChild(dt);
            }

            if (!string.IsNullOrEmpty(qname))
            {
                doc.AppendChild(doc.CreateElementNS(ns, qname));
            }

            return JsValue.Object(DomWrappers.Wrap(realm, doc));
        }, length: 3);

        // createDocumentFragment() — convenience stub
        EventTargetBinding.DefineMethod(realm, impl, "createDocumentFragment", (_, _) =>
        {
            var doc = new Document();
            return JsValue.Object(DomWrappers.Wrap(realm, doc.CreateDocumentFragment()));
        }, length: 0);

        // hasFeature() — DOM §4.5 always returns true per spec
        EventTargetBinding.DefineMethod(realm, impl, "hasFeature",
            (_, _) => JsValue.True, length: 0);

        return impl;
    }

    /// <summary>Build a real <see cref="JsArray"/> snapshot of a DOM result list.
    /// <para>Spec note: <c>Element.children</c>, <c>Element.childNodes</c>,
    /// <c>Document.getElementsByTagName</c>, and
    /// <c>Document.getElementsByClassName</c> are spec'd as <em>live</em>
    /// collections (HTMLCollection / NodeList). We simplify to a snapshot
    /// <see cref="JsArray"/>; the live-collection plumbing is a separate
    /// follow-up. <c>Document.querySelectorAll</c> is spec'd as a static
    /// NodeList, so a snapshot array is spec-correct there.</para></summary>
    internal static JsValue MakeArray(JsRealm realm, IReadOnlyList<JsValue> items)
    {
        var arr = new JsArray(realm, items);
        return JsValue.Object(arr);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsRealm, JsObject> HtmlCollectionProtos = new();

    private static JsObject HtmlCollectionProto(JsRealm realm)
    {
        if (HtmlCollectionProtos.TryGetValue(realm, out var proto))
        {
            return proto;
        }

        proto = new JsObject(realm.ObjectPrototype);
        proto.DefineOwnProperty(Starling.Js.Intrinsics.SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("HTMLCollection"), writable: false, enumerable: false, configurable: true));
        // length is a read-only accessor on the prototype (per WebIDL it is an
        // interface attribute, NOT an own property of the instance), so
        // collection.hasOwnProperty("length") is false and it never appears in the
        // instance's own-key list.
        EventTargetBinding.DefineAccessor(realm, proto, "length",
            (thisV, _) => thisV.IsObject && thisV.AsObject is HtmlCollectionObject c
                ? JsValue.Number(c.Count) : JsValue.Number(0));
        EventTargetBinding.DefineMethod(realm, proto, "item", (thisV, args) =>
            thisV.IsObject && thisV.AsObject is HtmlCollectionObject c && args.Length > 0
                ? c.Item((int)JsValue.ToNumber(args[0])) : JsValue.Null, length: 1);
        EventTargetBinding.DefineMethod(realm, proto, "namedItem", (thisV, args) =>
            thisV.IsObject && thisV.AsObject is HtmlCollectionObject c && args.Length > 0
                ? c.NamedItemValue(JsValue.ToStringValue(args[0])) : JsValue.Null, length: 1);
        JsValue Iterate(JsValue thisV, JsValue[] _)
        {
            if (thisV.IsObject && thisV.AsObject is HtmlCollectionObject c)
            {
                return Starling.Js.Intrinsics.IteratorIntrinsics.CreateArrayIterator(
                    realm, MakeArray(realm, c.Values().ToList()), Starling.Js.Intrinsics.ArrayIteratorKind.Value);
            }

            return JsValue.Undefined;
        }
        // HTMLCollection is iterable but, unlike NodeList, its WebIDL has no
        // value-iterator declaration — so it exposes @@iterator only, with no
        // named values/keys/entries/forEach methods (the WPT iterator test
        // checks `"values" in collection` is false).
        var iterFn = new JsNativeFunction(realm, "values", 0, Iterate, isConstructor: false);
        proto.DefineOwnProperty(Starling.Js.Intrinsics.SymbolCtor.Iterator,
            PropertyDescriptor.Data(JsValue.Object(iterFn), writable: true, enumerable: false, configurable: true));
        HtmlCollectionProtos.Add(realm, proto);
        return proto;
    }

    /// <summary>A live HTMLCollection over the elements yielded by <paramref name="source"/>.</summary>
    internal static JsValue BuildHtmlCollection(JsRealm realm, Func<IReadOnlyList<Element>> source)
        => JsValue.Object(new HtmlCollectionObject(realm, HtmlCollectionProto(realm), source));

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<JsRealm, JsObject> NodeListProtos = new();

    private static JsObject NodeListProto(JsRealm realm)
    {
        if (NodeListProtos.TryGetValue(realm, out var proto))
        {
            return proto;
        }
        // Reuse the global NodeList.prototype (installed by EventTargetBinding) so
        // `list instanceof NodeList` holds, then augment it with the interface
        // members. NodeList has an iterable<Node> declaration, so it exposes a full
        // value-iterator surface (values/keys/entries/forEach + @@iterator) — unlike
        // HTMLCollection.
        proto = realm.GlobalObject.Get("NodeList") is { IsObject: true } ctor
                && ctor.AsObject.Get("prototype") is { IsObject: true } p
            ? p.AsObject
            : new JsObject(realm.ObjectPrototype);
        proto.DefineOwnProperty(Starling.Js.Intrinsics.SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("NodeList"), writable: false, enumerable: false, configurable: true));
        // length is a read-only accessor on the prototype (WebIDL interface
        // attribute, not an own property of the instance).
        EventTargetBinding.DefineAccessor(realm, proto, "length",
            (thisV, _) => thisV.IsObject && thisV.AsObject is NodeListObject c
                ? JsValue.Number(c.Count) : JsValue.Number(0));
        EventTargetBinding.DefineMethod(realm, proto, "item", (thisV, args) =>
            thisV.IsObject && thisV.AsObject is NodeListObject c && args.Length > 0
                ? c.Item((int)JsValue.ToNumber(args[0])) : JsValue.Null, length: 1);

        JsValue Iter(JsValue thisV, Starling.Js.Intrinsics.ArrayIteratorKind kind)
            => thisV.IsObject && thisV.AsObject is NodeListObject c
                ? Starling.Js.Intrinsics.IteratorIntrinsics.CreateArrayIterator(
                    realm, MakeArray(realm, c.Values().ToList()), kind)
                : JsValue.Undefined;
        EventTargetBinding.DefineMethod(realm, proto, "values",
            (t, _) => Iter(t, Starling.Js.Intrinsics.ArrayIteratorKind.Value), length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "keys",
            (t, _) => Iter(t, Starling.Js.Intrinsics.ArrayIteratorKind.Key), length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "entries",
            (t, _) => Iter(t, Starling.Js.Intrinsics.ArrayIteratorKind.KeyAndValue), length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "forEach", (thisV, args) =>
        {
            if (thisV.IsObject && thisV.AsObject is NodeListObject c
                && args.Length > 0 && AbstractOperations.IsCallable(args[0]))
            {
                var fn = args[0];
                var thisArg = args.Length > 1 ? args[1] : JsValue.Undefined;
                var snapshot = c.Values().ToList();
                for (var i = 0; i < snapshot.Count; i++)
                {
                    AbstractOperations.Call(realm.ActiveVm, fn, thisArg,
                        new[] { snapshot[i], JsValue.Number(i), thisV });
                }
            }
            return JsValue.Undefined;
        }, length: 1);
        // @@iterator === values (per the iterable<Node> declaration).
        proto.DefineOwnProperty(Starling.Js.Intrinsics.SymbolCtor.Iterator,
            PropertyDescriptor.Data(proto.Get("values"), writable: true, enumerable: false, configurable: true));

        NodeListProtos.Add(realm, proto);
        return proto;
    }

    /// <summary>A live NodeList over the nodes yielded by <paramref name="source"/>.</summary>
    internal static JsValue BuildNodeList(JsRealm realm, Func<IReadOnlyList<Node>> source)
        => JsValue.Object(new NodeListObject(realm, NodeListProto(realm), source));

    private static void InstallFormControlAccessors(JsRealm realm, JsObject proto)
    {
        EventTargetBinding.DefineAccessor(realm, proto, "name",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.String(e.GetAttribute("name") ?? "") : JsValue.String(""),
            (thisV, args) => { if (DomWrappers.UnwrapElement(thisV) is { } e) { e.SetAttribute("name", args.Length > 0 ? JsValue.ToStringValue(args[0]) : ""); } return JsValue.Undefined; });
        EventTargetBinding.DefineAccessor(realm, proto, "type",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.String(HtmlFormControls.InputType(e)) : JsValue.String(""),
            (thisV, args) => { if (DomWrappers.UnwrapElement(thisV) is { } e) { e.SetAttribute("type", args.Length > 0 ? JsValue.ToStringValue(args[0]) : ""); } return JsValue.Undefined; });
        EventTargetBinding.DefineAccessor(realm, proto, "required",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.Boolean(e.HasAttribute("required")) : JsValue.False,
            (thisV, args) => { if (DomWrappers.UnwrapElement(thisV) is { } e) { SetBoolAttr(e, "required", args.Length > 0 && JsValue.ToBoolean(args[0])); } return JsValue.Undefined; });
        EventTargetBinding.DefineAccessor(realm, proto, "disabled",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.Boolean(e.HasAttribute("disabled")) : JsValue.False,
            (thisV, args) => { if (DomWrappers.UnwrapElement(thisV) is { } e) { SetBoolAttr(e, "disabled", args.Length > 0 && JsValue.ToBoolean(args[0])); } return JsValue.Undefined; });
        EventTargetBinding.DefineAccessor(realm, proto, "readOnly",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.Boolean(e.HasAttribute("readonly")) : JsValue.False,
            (thisV, args) => { if (DomWrappers.UnwrapElement(thisV) is { } e) { SetBoolAttr(e, "readonly", args.Length > 0 && JsValue.ToBoolean(args[0])); } return JsValue.Undefined; });
        EventTargetBinding.DefineAccessor(realm, proto, "multiple",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.Boolean(e.HasAttribute("multiple")) : JsValue.False,
            (thisV, args) => { if (DomWrappers.UnwrapElement(thisV) is { } e) { SetBoolAttr(e, "multiple", args.Length > 0 && JsValue.ToBoolean(args[0])); } return JsValue.Undefined; });
        EventTargetBinding.DefineAccessor(realm, proto, "checked",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.Boolean(HtmlFormControls.Checked(e)) : JsValue.False,
            (thisV, args) => { if (DomWrappers.UnwrapElement(thisV) is { } e) { HtmlFormControls.SetChecked(e, args.Length > 0 && JsValue.ToBoolean(args[0])); } return JsValue.Undefined; });
        EventTargetBinding.DefineAccessor(realm, proto, "selected",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.Boolean(e.HasAttribute("selected")) : JsValue.False,
            (thisV, args) => { if (DomWrappers.UnwrapElement(thisV) is { } e) { SetBoolAttr(e, "selected", args.Length > 0 && JsValue.ToBoolean(args[0])); } return JsValue.Undefined; });
        EventTargetBinding.DefineAccessor(realm, proto, "form",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e && HtmlFormControls.FormOwner(e) is { } form
                ? JsValue.Object(DomWrappers.Wrap(realm, form)) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, proto, "selectionStart",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e && HtmlFormControls.IsTextControl(e) ? JsValue.Number(e.SelectionStart) : JsValue.Null,
            (thisV, args) => { if (DomWrappers.UnwrapElement(thisV) is { } e) { HtmlFormControls.SetSelectionRange(e, args.Length > 0 ? (int)JsValue.ToNumber(args[0]) : 0, e.SelectionEnd, e.SelectionDirection); } return JsValue.Undefined; });
        EventTargetBinding.DefineAccessor(realm, proto, "selectionEnd",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e && HtmlFormControls.IsTextControl(e) ? JsValue.Number(e.SelectionEnd) : JsValue.Null,
            (thisV, args) => { if (DomWrappers.UnwrapElement(thisV) is { } e) { HtmlFormControls.SetSelectionRange(e, e.SelectionStart, args.Length > 0 ? (int)JsValue.ToNumber(args[0]) : 0, e.SelectionDirection); } return JsValue.Undefined; });
        EventTargetBinding.DefineAccessor(realm, proto, "validity",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.Object(BuildValidityObject(realm, HtmlFormControls.Validity(e))) : JsValue.Undefined);
        EventTargetBinding.DefineAccessor(realm, proto, "willValidate",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.Boolean(HtmlFormControls.WillValidate(e)) : JsValue.False);
        EventTargetBinding.DefineAccessor(realm, proto, "validationMessage",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.String(HtmlFormControls.ValidationMessage(e)) : JsValue.String(""));
        EventTargetBinding.DefineAccessor(realm, proto, "selectedIndex",
            (thisV, _) => DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.Number(SelectedIndex(e)) : JsValue.Number(-1),
            (thisV, args) => { if (DomWrappers.UnwrapElement(thisV) is { } e) { SetSelectedIndex(e, args.Length > 0 ? (int)JsValue.ToNumber(args[0]) : -1); } return JsValue.Undefined; });
        EventTargetBinding.DefineMethod(realm, proto, "setSelectionRange", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is { } e)
            {
                HtmlFormControls.SetSelectionRange(e,
                    args.Length > 0 ? (int)JsValue.ToNumber(args[0]) : 0,
                    args.Length > 1 ? (int)JsValue.ToNumber(args[1]) : 0,
                    args.Length > 2 ? JsValue.ToStringValue(args[2]) : "none");
            }

            return JsValue.Undefined;
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, proto, "checkValidity", (thisV, _) =>
            DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.Boolean(HtmlFormControls.CheckValidity(e)) : JsValue.True, length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "reportValidity", (thisV, _) =>
            DomWrappers.UnwrapElement(thisV) is { } e ? JsValue.Boolean(HtmlFormControls.CheckValidity(e)) : JsValue.True, length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "setCustomValidity", (thisV, args) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is { } e)
            {
                e.CustomValidationMessage = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            }

            return JsValue.Undefined;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, proto, "serialize", (thisV, _) =>
            DomWrappers.UnwrapElement(thisV) is { LocalName: "form" } form ? JsValue.String(HtmlFormControls.UrlEncodedFormData(form)) : JsValue.String(""), length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "submit", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is { LocalName: "form" } form)
            {
                HtmlFormControls.RecordAutocompleteSubmission(form);
            }

            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "requestSubmit", (thisV, _) =>
        {
            if (DomWrappers.UnwrapElement(thisV) is not { LocalName: "form" } form)
            {
                return JsValue.Undefined;
            }

            if (!DispatchInvalidEvents(form))
            {
                return JsValue.Undefined;
            }

            var ev = new Starling.Dom.Events.Event("submit", new Starling.Dom.Events.EventInit(Bubbles: true, Cancelable: true));
            form.DispatchEvent(ev);
            if (!ev.DefaultPrevented)
            {
                HtmlFormControls.RecordAutocompleteSubmission(form);
            }

            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, proto, "autocompleteSuggestions", (thisV, _) =>
            DomWrappers.UnwrapElement(thisV) is { } e ? StringArray(realm, HtmlFormControls.AutocompleteSuggestions(e)) : MakeArray(realm, Array.Empty<JsValue>()), length: 0);
    }

    private static JsObject BuildValidityObject(JsRealm realm, FormValidityState validity)
    {
        var obj = new JsObject(realm.ObjectPrototype);
        obj.DefineOwnProperty("valueMissing", PropertyDescriptor.Data(JsValue.Boolean(validity.ValueMissing), writable: false, enumerable: true, configurable: true));
        obj.DefineOwnProperty("typeMismatch", PropertyDescriptor.Data(JsValue.Boolean(validity.TypeMismatch), writable: false, enumerable: true, configurable: true));
        obj.DefineOwnProperty("patternMismatch", PropertyDescriptor.Data(JsValue.Boolean(validity.PatternMismatch), writable: false, enumerable: true, configurable: true));
        obj.DefineOwnProperty("tooLong", PropertyDescriptor.Data(JsValue.Boolean(validity.TooLong), writable: false, enumerable: true, configurable: true));
        obj.DefineOwnProperty("tooShort", PropertyDescriptor.Data(JsValue.Boolean(validity.TooShort), writable: false, enumerable: true, configurable: true));
        obj.DefineOwnProperty("rangeUnderflow", PropertyDescriptor.Data(JsValue.Boolean(validity.RangeUnderflow), writable: false, enumerable: true, configurable: true));
        obj.DefineOwnProperty("rangeOverflow", PropertyDescriptor.Data(JsValue.Boolean(validity.RangeOverflow), writable: false, enumerable: true, configurable: true));
        obj.DefineOwnProperty("stepMismatch", PropertyDescriptor.Data(JsValue.Boolean(validity.StepMismatch), writable: false, enumerable: true, configurable: true));
        obj.DefineOwnProperty("badInput", PropertyDescriptor.Data(JsValue.Boolean(validity.BadInput), writable: false, enumerable: true, configurable: true));
        obj.DefineOwnProperty("customError", PropertyDescriptor.Data(JsValue.Boolean(validity.CustomError), writable: false, enumerable: true, configurable: true));
        obj.DefineOwnProperty("valid", PropertyDescriptor.Data(JsValue.Boolean(validity.Valid), writable: false, enumerable: true, configurable: true));
        return obj;
    }

    private static JsValue StringArray(JsRealm realm, IReadOnlyList<string> values)
    {
        var items = new JsValue[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            items[i] = JsValue.String(values[i]);
        }

        return MakeArray(realm, items);
    }

    private static int SelectedIndex(Element element)
    {
        if (element.LocalName != "select")
        {
            return -1;
        }

        var index = 0;
        var fallback = -1;
        foreach (var option in element.DescendantElements())
        {
            if (option.LocalName != "option")
            {
                continue;
            }

            if (fallback < 0)
            {
                fallback = index;
            }

            if (option.HasAttribute("selected"))
            {
                return index;
            }

            index++;
        }
        return fallback;
    }

    private static void SetSelectedIndex(Element element, int selectedIndex)
    {
        if (element.LocalName != "select")
        {
            return;
        }

        var index = 0;
        foreach (var option in element.DescendantElements())
        {
            if (option.LocalName != "option")
            {
                continue;
            }

            if (index == selectedIndex)
            {
                option.SetAttribute("selected", string.Empty);
            }
            else if (!element.HasAttribute("multiple"))
            {
                option.RemoveAttribute("selected");
            }

            index++;
        }
    }

    private static bool DispatchInvalidEvents(Element form)
    {
        var valid = true;
        foreach (var control in HtmlFormControls.FormControls(form))
        {
            if (HtmlFormControls.Validity(control).Valid)
            {
                continue;
            }

            valid = false;
            control.DispatchEvent(new Starling.Dom.Events.Event("invalid", new Starling.Dom.Events.EventInit(Cancelable: true)));
        }
        return valid;
    }

    private static void SetBoolAttr(Element element, string attr, bool value)
    {
        if (value)
        {
            element.SetAttribute(attr, string.Empty);
        }
        else
        {
            element.RemoveAttribute(attr);
        }
    }

    // ---- DOM Name / QName validation (DOM §1 "validate", §4.5). Approximates
    // the XML Name production: a NameStartChar (letter/_/:) followed by
    // NameChars (+digit/-/.). Sufficient for the WPT create*Element error cases.
    private const string XmlNamespace = "http://www.w3.org/XML/1998/namespace";
    private const string XmlnsNamespace = "http://www.w3.org/2000/xmlns/";

    /// <summary>Coerce ChildNode/ParentNode varargs to host nodes: a node arg
    /// passes through; anything else becomes a Text node (DOM §4.2.1).</summary>
    private static List<Node> CoerceNodes(JsRealm realm, Node context, JsValue[] args)
    {
        var doc = context.OwnerDocument ?? context as Document ?? new Document();
        var list = new List<Node>(args.Length);
        foreach (var a in args)
        {
            list.Add(DomWrappers.UnwrapNode(a) is { } n ? n : doc.CreateTextNode(JsValue.ToStringValue(a)));
        }

        return list;
    }

    // -------------------------------------------------------------------------
    // DOM tree helpers (used by compareDocumentPosition / isEqualNode)
    // -------------------------------------------------------------------------

    /// <summary>Walk up to the tree root of <paramref name="n"/> (no owner doc).</summary>
    private static Node NodeRoot(Node n)
    {
        while (n.ParentNode is { } p)
        {
            n = p;
        }

        return n;
    }

    /// <summary>True when <paramref name="ancestor"/> is an inclusive ancestor
    /// of <paramref name="descendant"/> (i.e. same node or a proper ancestor).</summary>
    private static bool IsAncestor(Node ancestor, Node descendant)
    {
        var cur = descendant;
        while (cur is not null)
        {
            if (ReferenceEquals(cur, ancestor))
            {
                return true;
            }

            cur = cur.ParentNode;
        }
        return false;
    }

    /// <summary>Returns true when <paramref name="a"/> precedes
    /// <paramref name="b"/> in tree pre-order (depth-first, left-to-right).
    /// Both nodes must share the same root.</summary>
    private static bool IsBeforeInTreeOrder(Node a, Node b)
    {
        // Collect ancestors-from-root for each node (including the node itself).
        static System.Collections.Generic.List<Node> Path(Node n)
        {
            var path = new System.Collections.Generic.List<Node>();
            for (var cur = n; cur is not null; cur = cur.ParentNode)
            {
                path.Insert(0, cur);
            }

            return path;
        }
        var pa = Path(a);
        var pb = Path(b);
        // Find the first divergence point.
        var min = Math.Min(pa.Count, pb.Count);
        for (var i = 0; i < min; i++)
        {
            if (!ReferenceEquals(pa[i], pb[i]))
            {
                // pa[i-1] is the common ancestor; find sibling index.
                var parent = i > 0 ? pa[i - 1] : null;
                if (parent is null)
                {
                    return false;
                }

                for (var child = parent.FirstChild; child is not null; child = child.NextSibling)
                {
                    if (ReferenceEquals(child, pa[i]))
                    {
                        return true;   // a's branch comes first
                    }

                    if (ReferenceEquals(child, pb[i]))
                    {
                        return false;  // b's branch comes first
                    }
                }
                return false;
            }
        }
        // One is a prefix of the other; the shorter path (ancestor) comes first.
        return pa.Count < pb.Count;
    }

    /// <summary>Structural (deep) equality per DOM §4.4.3 isEqualNode algorithm.</summary>
    private static bool AreEqual(Node? a, Node? b)
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
        // Check node-type-specific attributes.
        switch (a)
        {
            case Element ea when b is Element eb:
                if (ea.TagName != eb.TagName || ea.Namespace != eb.Namespace)
                {
                    return false;
                }
                // Compare attributes (order-independent).
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
            case Document:
                break; // Documents are equal iff children are equal (checked below).
            case DocumentType dta when b is DocumentType dtb:
                if (dta.Name != dtb.Name)
                {
                    return false;
                }

                break;
        }
        // Recurse into children.
        var ac = a.FirstChild;
        var bc = b.FirstChild;
        while (ac is not null && bc is not null)
        {
            if (!AreEqual(ac, bc))
            {
                return false;
            }

            ac = ac.NextSibling;
            bc = bc.NextSibling;
        }
        return ac is null && bc is null;
    }

    /// <summary>DOM §4.4 — structural equality between two nodes (isEqualNode).
    /// Two nodes are equal when they have the same type, the same qualifying data
    /// (tag/data/etc.), the same attributes, and the same children — recursively.</summary>
    private static bool NodesEqual(Node a, Node b)
    {
        if (a.Kind != b.Kind)
        {
            return false;
        }

        switch (a)
        {
            case DocumentType dta when b is DocumentType dtb:
                if (dta.Name != dtb.Name || dta.PublicId != dtb.PublicId || dta.SystemId != dtb.SystemId)
                {
                    return false;
                }

                break;
            case Element ea when b is Element eb:
                if (ea.Namespace != eb.Namespace || ea.LocalName != eb.LocalName)
                {
                    return false;
                }
                // Compare attributes (order-independent per spec).
                var attrsA = ea.Attributes.ToList();
                var attrsB = eb.Attributes.ToList();
                if (attrsA.Count != attrsB.Count)
                {
                    return false;
                }

                foreach (var attr in attrsA)
                {
                    var matchIdx = attrsB.FindIndex(x => x.Name == attr.Name && x.Namespace == attr.Namespace);
                    if (matchIdx < 0 || attrsB[matchIdx].Value != attr.Value)
                    {
                        return false;
                    }
                }
                break;
            case ProcessingInstruction pia when b is ProcessingInstruction pib:
                if (pia.Target != pib.Target || pia.Data != pib.Data)
                {
                    return false;
                }

                break;
            case CharacterData cda when b is CharacterData cdb:
                if (cda.Data != cdb.Data)
                {
                    return false;
                }

                break;
        }
        // Recursively compare children.
        var ca = a.FirstChild;
        var cb = b.FirstChild;
        while (ca is not null && cb is not null)
        {
            if (!NodesEqual(ca, cb))
            {
                return false;
            }

            ca = ca.NextSibling;
            cb = cb.NextSibling;
        }
        return ca is null && cb is null;
    }

    // =====================================================================
    //                          CharacterData
    // =====================================================================
    /// <summary>WebIDL <c>unsigned long</c> conversion for CharacterData offsets
    /// (ToUint32): NaN/Infinity become 0, negatives wrap modulo 2^32. So
    /// -0x100000000 + 2 becomes 2 (in bounds), -1 becomes 4294967295 (clamped).</summary>
    private static long CdataOffset(JsValue v)
    {
        var d = JsValue.ToNumber(v);
        if (double.IsNaN(d) || double.IsInfinity(d))
        {
            return 0;
        }

        return unchecked((uint)(long)Math.Truncate(d));
    }

    private static void InstallCharacterData(JsRealm realm)
    {
        // CharacterData prototype inherits from Node.prototype.
        var cdProto = new JsObject(realm.NodePrototype!);
        realm.CharacterDataPrototype = cdProto;

        // CharacterData.data setter is spec-equivalent to replaceData(0, length, value)
        // — so live Ranges adjust the same way.
        EventTargetBinding.DefineAccessor(realm, cdProto, "data",
            (thisV, _) => DomWrappers.UnwrapAs<CharacterData>(thisV) is { } cd ? JsValue.String(cd.Data) : JsValue.String(""),
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapAs<CharacterData>(thisV) is { } cd)
                {
                    var s = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                    var oldLen = cd.Data.Length;
                    cd.Data = s;
                    Starling.Dom.DomRange.OnReplaceData(cd, 0, oldLen, s.Length);
                }
                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, cdProto, "length",
            (thisV, _) => DomWrappers.UnwrapAs<CharacterData>(thisV) is { } cd ? JsValue.Number(cd.Data.Length) : JsValue.Number(0));
        EventTargetBinding.DefineAccessor(realm, cdProto, "nodeValue",
            (thisV, _) => DomWrappers.UnwrapAs<CharacterData>(thisV) is { } cd ? JsValue.String(cd.Data) : JsValue.Null,
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapAs<CharacterData>(thisV) is { } cd)
                {
                    var s = args.Length > 0 && !args[0].IsNullish ? JsValue.ToStringValue(args[0]) : "";
                    var oldLen = cd.Data.Length;
                    cd.Data = s;
                    Starling.Dom.DomRange.OnReplaceData(cd, 0, oldLen, s.Length);
                }
                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, cdProto, "textContent",
            (thisV, _) => DomWrappers.UnwrapAs<CharacterData>(thisV) is { } cd ? JsValue.String(cd.Data) : JsValue.String(""),
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapAs<CharacterData>(thisV) is { } cd)
                {
                    var s = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                    var oldLen = cd.Data.Length;
                    cd.Data = s;
                    Starling.Dom.DomRange.OnReplaceData(cd, 0, oldLen, s.Length);
                }
                return JsValue.Undefined;
            });

        // DOM §4.8 CharacterData.substringData(offset, count) → string.
        EventTargetBinding.DefineMethod(realm, cdProto, "substringData", (thisV, args) =>
        {
            var cd = DomWrappers.UnwrapAs<CharacterData>(thisV);
            if (cd is null)
            {
                return JsValue.String("");
            }

            if (args.Length < 2)
            {
                throw new JsThrow(realm.NewTypeError("substringData requires 2 arguments"));
            }

            var offset = CdataOffset(args[0]);
            var count = CdataOffset(args[1]);
            var len = cd.Data.Length;
            if (offset > len)
            {
                throw DomExceptionBinding.Throw(realm, "IndexSizeError", $"Offset {offset} is outside the data length {len}");
            }

            var o = (int)offset;
            var realCount = (int)Math.Min(count, len - o);
            return JsValue.String(cd.Data.Substring(o, realCount));
        }, length: 2);

        // DOM §4.8 CharacterData.appendData(data). Routed through the
        // replace-data primitive so live Ranges adjust per §5.3.4.
        EventTargetBinding.DefineMethod(realm, cdProto, "appendData", (thisV, args) =>
        {
            if (DomWrappers.UnwrapAs<CharacterData>(thisV) is { } cd)
            {
                if (args.Length < 1)
                {
                    throw new JsThrow(realm.NewTypeError("appendData requires 1 argument"));
                }

                var data = JsValue.ToStringValue(args[0]);
                var pos = cd.Data.Length;
                cd.Data += data;
                Starling.Dom.DomRange.OnReplaceData(cd, pos, 0, data.Length);
            }
            return JsValue.Undefined;
        }, length: 1);

        // DOM §4.8 CharacterData.insertData(offset, data).
        EventTargetBinding.DefineMethod(realm, cdProto, "insertData", (thisV, args) =>
        {
            var cd = DomWrappers.UnwrapAs<CharacterData>(thisV);
            if (cd is null)
            {
                return JsValue.Undefined;
            }

            if (args.Length < 2)
            {
                throw new JsThrow(realm.NewTypeError("insertData requires 2 arguments"));
            }

            var offset = CdataOffset(args[0]);
            var data = JsValue.ToStringValue(args[1]);
            var len = cd.Data.Length;
            if (offset > len)
            {
                throw DomExceptionBinding.Throw(realm, "IndexSizeError", $"Offset {offset} is outside the data length {len}");
            }

            var o = (int)offset;
            cd.Data = cd.Data[..o] + data + cd.Data[o..];
            Starling.Dom.DomRange.OnReplaceData(cd, o, 0, data.Length);
            return JsValue.Undefined;
        }, length: 2);

        // DOM §4.8 CharacterData.deleteData(offset, count).
        EventTargetBinding.DefineMethod(realm, cdProto, "deleteData", (thisV, args) =>
        {
            var cd = DomWrappers.UnwrapAs<CharacterData>(thisV);
            if (cd is null)
            {
                return JsValue.Undefined;
            }

            if (args.Length < 2)
            {
                throw new JsThrow(realm.NewTypeError("deleteData requires 2 arguments"));
            }

            var offset = CdataOffset(args[0]);
            var count = CdataOffset(args[1]);
            var len = cd.Data.Length;
            if (offset > len)
            {
                throw DomExceptionBinding.Throw(realm, "IndexSizeError", $"Offset {offset} is outside the data length {len}");
            }

            var o = (int)offset;
            var realCount = (int)Math.Min(count, len - o);
            cd.Data = cd.Data[..o] + cd.Data[(o + realCount)..];
            Starling.Dom.DomRange.OnReplaceData(cd, o, realCount, 0);
            return JsValue.Undefined;
        }, length: 2);

        // DOM §4.8 CharacterData.replaceData(offset, count, data).
        EventTargetBinding.DefineMethod(realm, cdProto, "replaceData", (thisV, args) =>
        {
            var cd = DomWrappers.UnwrapAs<CharacterData>(thisV);
            if (cd is null)
            {
                return JsValue.Undefined;
            }

            if (args.Length < 3)
            {
                throw new JsThrow(realm.NewTypeError("replaceData requires 3 arguments"));
            }

            var offset = CdataOffset(args[0]);
            var count = CdataOffset(args[1]);
            var data = JsValue.ToStringValue(args[2]);
            var len = cd.Data.Length;
            if (offset > len)
            {
                throw DomExceptionBinding.Throw(realm, "IndexSizeError", $"Offset {offset} is outside the data length {len}");
            }

            var o = (int)offset;
            var realCount = (int)Math.Min(count, len - o);
            cd.Data = cd.Data[..o] + data + cd.Data[(o + realCount)..];
            Starling.Dom.DomRange.OnReplaceData(cd, o, realCount, data.Length);
            return JsValue.Undefined;
        }, length: 3);

        // CharacterData interface constructor (illegal per spec — abstract type).
        var cdCtor = new JsNativeFunction(realm, "CharacterData", 0, (_, _) =>
            throw new JsThrow(realm.NewTypeError("Illegal constructor")), isConstructor: false);
        cdCtor.SetPrototypeOf(realm.NodeConstructor!);
        cdCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(cdProto), writable: false, enumerable: false, configurable: false));
        cdProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(cdCtor), writable: true, enumerable: false, configurable: true));
        realm.GlobalObject.DefineOwnProperty("CharacterData",
            PropertyDescriptor.Data(JsValue.Object(cdCtor), writable: true, enumerable: false, configurable: true));

        // Text prototype inherits from CharacterData.prototype.
        var textProto = new JsObject(cdProto);
        realm.TextPrototype = textProto;
        // Text.splitText(offset) — splits a text node at offset.
        EventTargetBinding.DefineMethod(realm, textProto, "splitText", (thisV, args) =>
        {
            var text = DomWrappers.UnwrapAs<Text>(thisV);
            if (text is null)
            {
                throw new JsThrow(realm.NewTypeError("splitText called on non-Text node"));
            }

            var offset = args.Length > 0 ? (int)JsValue.ToNumber(args[0]) : 0;
            var len = text.Data.Length;
            if (offset < 0 || offset > len)
            {
                throw DomExceptionBinding.Throw(realm, "IndexSizeError", $"Offset {offset} is outside the text length {len}");
            }

            var newText = new Text(text.Data[offset..]);
            text.Data = text.Data[..offset];
            if (text.ParentNode is { } parent)
            {
                parent.InsertBefore(newText, text.NextSibling);
            }

            return JsValue.Object(DomWrappers.Wrap(realm, newText));
        }, length: 1);
        // Text.wholeText — concatenates text node and adjacent text siblings.
        EventTargetBinding.DefineAccessor(realm, textProto, "wholeText", (thisV, _) =>
        {
            if (DomWrappers.UnwrapAs<Text>(thisV) is not { } t)
            {
                return JsValue.String("");
            }

            var sb = new System.Text.StringBuilder();
            // Walk backwards to find start
            Node cur = t;
            while (cur.PreviousSibling is Text prev)
            {
                cur = prev;
            }
            // Concatenate
            while (cur is Text txt) { sb.Append(txt.Data); cur = cur.NextSibling!; if (cur is null)
                {
                    break;
                }
            }
            return JsValue.String(sb.ToString());
        });

        var textCtor = new JsNativeFunction(realm, "Text", 1, (_, args) =>
        {
            var data = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            // Create a detached text node (ownerDocument is null).
            var doc = realm.DocumentPrototype is not null
                ? new Document()
                : new Document();
            return JsValue.Object(DomWrappers.WrapWithProto(realm, new Text(data), textProto));
        }, isConstructor: true);
        textCtor.SetPrototypeOf(cdCtor);
        textCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(textProto), writable: false, enumerable: false, configurable: false));
        textProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(textCtor), writable: true, enumerable: false, configurable: true));
        realm.TextConstructor = textCtor;
        realm.GlobalObject.DefineOwnProperty("Text",
            PropertyDescriptor.Data(JsValue.Object(textCtor), writable: true, enumerable: false, configurable: true));

        // Comment prototype inherits from CharacterData.prototype.
        var commentProto = new JsObject(cdProto);
        realm.CommentPrototype = commentProto;
        var commentCtor = new JsNativeFunction(realm, "Comment", 1, (_, args) =>
        {
            var data = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            return JsValue.Object(DomWrappers.WrapWithProto(realm, new Comment(data), commentProto));
        }, isConstructor: true);
        commentCtor.SetPrototypeOf(cdCtor);
        commentCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(commentProto), writable: false, enumerable: false, configurable: false));
        commentProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(commentCtor), writable: true, enumerable: false, configurable: true));
        realm.CommentConstructor = commentCtor;
        realm.GlobalObject.DefineOwnProperty("Comment",
            PropertyDescriptor.Data(JsValue.Object(commentCtor), writable: true, enumerable: false, configurable: true));

        // ProcessingInstruction prototype inherits from CharacterData.prototype.
        var piProto = new JsObject(cdProto);
        realm.ProcessingInstructionPrototype = piProto;
        EventTargetBinding.DefineAccessor(realm, piProto, "target",
            (thisV, _) => DomWrappers.UnwrapAs<ProcessingInstruction>(thisV) is { } pi ? JsValue.String(pi.Target) : JsValue.String(""));
        var piCtor = new JsNativeFunction(realm, "ProcessingInstruction", 0, (_, _) =>
            throw new JsThrow(realm.NewTypeError("Illegal constructor")), isConstructor: false);
        piCtor.SetPrototypeOf(cdCtor);
        piCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(piProto), writable: false, enumerable: false, configurable: false));
        piProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(piCtor), writable: true, enumerable: false, configurable: true));
        realm.GlobalObject.DefineOwnProperty("ProcessingInstruction",
            PropertyDescriptor.Data(JsValue.Object(piCtor), writable: true, enumerable: false, configurable: true));

        // DocumentFragment inherits from Node.prototype.
        var dfProto = new JsObject(realm.NodePrototype!);
        realm.DocumentFragmentPrototype = dfProto;
        EventTargetBinding.DefineMethod(realm, dfProto, "querySelector", (thisV, args) =>
        {
            if (DomWrappers.UnwrapAs<DocumentFragment>(thisV) is not { } df || args.Length == 0)
            {
                return JsValue.Null;
            }

            var match = QuerySelectorEngine.First(df, JsValue.ToStringValue(args[0]), realm);
            return match is null ? JsValue.Null : JsValue.Object(DomWrappers.Wrap(realm, match));
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, dfProto, "querySelectorAll", (thisV, args) =>
        {
            if (DomWrappers.UnwrapAs<DocumentFragment>(thisV) is not { } df || args.Length == 0)
            {
                return MakeArray(realm, Array.Empty<JsValue>());
            }

            var items = new List<JsValue>();
            foreach (var m in QuerySelectorEngine.All(df, JsValue.ToStringValue(args[0]), realm))
            {
                items.Add(JsValue.Object(DomWrappers.Wrap(realm, m)));
            }

            return MakeArray(realm, items);
        }, length: 1);
        var dfCtor = new JsNativeFunction(realm, "DocumentFragment", 0, (_, _) =>
        {
            // DOM §4.7 — the DocumentFragment constructor creates a detached fragment.
            var doc = new Document();
            return JsValue.Object(DomWrappers.WrapWithProto(realm, doc.CreateDocumentFragment(), dfProto));
        }, isConstructor: true);
        dfCtor.SetPrototypeOf(realm.NodeConstructor!);
        dfCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(dfProto), writable: false, enumerable: false, configurable: false));
        dfProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(dfCtor), writable: true, enumerable: false, configurable: true));
        realm.DocumentFragmentConstructor = dfCtor;
        realm.GlobalObject.DefineOwnProperty("DocumentFragment",
            PropertyDescriptor.Data(JsValue.Object(dfCtor), writable: true, enumerable: false, configurable: true));

        // DocumentType inherits from Node.prototype.
        var dtProto = new JsObject(realm.NodePrototype!);
        realm.DocumentTypePrototype = dtProto;
        EventTargetBinding.DefineAccessor(realm, dtProto, "name",
            (thisV, _) => DomWrappers.UnwrapAs<DocumentType>(thisV) is { } dt ? JsValue.String(dt.Name) : JsValue.String(""));
        EventTargetBinding.DefineAccessor(realm, dtProto, "publicId",
            (thisV, _) => DomWrappers.UnwrapAs<DocumentType>(thisV) is { } dt ? JsValue.String(dt.PublicId) : JsValue.String(""));
        EventTargetBinding.DefineAccessor(realm, dtProto, "systemId",
            (thisV, _) => DomWrappers.UnwrapAs<DocumentType>(thisV) is { } dt ? JsValue.String(dt.SystemId) : JsValue.String(""));
        var dtCtor = new JsNativeFunction(realm, "DocumentType", 0, (_, _) =>
            throw new JsThrow(realm.NewTypeError("Illegal constructor")), isConstructor: false);
        dtCtor.SetPrototypeOf(realm.NodeConstructor!);
        dtCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(dtProto), writable: false, enumerable: false, configurable: false));
        dtProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(dtCtor), writable: true, enumerable: false, configurable: true));
        realm.DocumentTypeConstructor = dtCtor;
        realm.GlobalObject.DefineOwnProperty("DocumentType",
            PropertyDescriptor.Data(JsValue.Object(dtCtor), writable: true, enumerable: false, configurable: true));
    }

    /// <summary>DOM §4.4 — pre-insert validation. Throws HierarchyRequestError for:
    /// (1) parent is not a valid container type (Document, Element, DocumentFragment),
    /// (2) child is an ancestor of parent, (3) child is a Document in a non-Document parent,
    /// (4) inserting a node type that is not allowed in the parent type.</summary>
    private static void ValidatePreInsert(JsRealm realm, Node parent, Node child, Node? refChild)
    {
        // Parent must be able to have children.
        if (parent is not (Document or Element or DocumentFragment))
        {
            throw DomExceptionBinding.Throw(realm, "HierarchyRequestError",
                $"Node of type '{parent.GetType().Name}' cannot have children");
        }
        // Child must not be an ancestor of parent (cycle check).
        for (var p = parent; p is not null; p = p.ParentNode)
        {
            if (ReferenceEquals(p, child))
            {
                throw DomExceptionBinding.Throw(realm, "HierarchyRequestError",
                    "The new child element contains the parent");
            }
        }
        // A document cannot be inserted as a child (unless it's already there somehow).
        if (child is Document && parent is not Document)
        {
            throw DomExceptionBinding.Throw(realm, "HierarchyRequestError",
                "Documents cannot be inserted as a child node");
        }
    }

    /// <summary>Convert an <see cref="InvalidOperationException"/> from a host
    /// tree mutation into the correct <see cref="JsThrow"/> DOMException.</summary>
    private static JsThrow NodeMutationException(JsRealm realm, InvalidOperationException ex, Node parent, Node child)
    {
        // Map common host-side error messages to DOM error names.
        var msg = ex.Message ?? "";
        if (msg.Contains("ancestor", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("hierarchy", StringComparison.OrdinalIgnoreCase))
        {
            return DomExceptionBinding.Throw(realm, "HierarchyRequestError", msg);
        }

        if (msg.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            msg.Contains("not a child", StringComparison.OrdinalIgnoreCase))
        {
            return DomExceptionBinding.Throw(realm, "NotFoundError", msg);
        }
        // Default: HierarchyRequestError
        return DomExceptionBinding.Throw(realm, "HierarchyRequestError", msg);
    }

    // Browsers (and the WPT name-validity tests) accept any non-ASCII code point
    // anywhere in an element/attribute name — including code points outside the
    // strict XML NameStartChar ranges (U+037E, U+FFFF, lone combining marks as a
    // local-name start). Only ASCII is checked against the Name production; every
    // code point >= U+0080 is permitted as both a start and a continuation char.
    private static bool IsNameStart(char c) => c >= 0x80 || char.IsAsciiLetter(c) || c == '_' || c == ':';
    private static bool IsNameChar(char c) =>
        IsNameStart(c) || char.IsAsciiDigit(c) || c is '-' or '.' or '·'
        // Browsers also accept these two ASCII punctuation marks mid-name
        // (e.g. "f}oo", "f<oo" parse as valid element names), so the WPT
        // name-validity cases expect them allowed as a continuation char.
        || c is '}' or '<'
        // XML NameChar also allows combining marks (e.g. U+0BC6) and non-ASCII
        // digits, which browsers accept in element names.
        || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.NonSpacingMark
            or System.Globalization.UnicodeCategory.SpacingCombiningMark
            or System.Globalization.UnicodeCategory.EnclosingMark
            or System.Globalization.UnicodeCategory.DecimalDigitNumber;

    /// <summary>The realm whose DOMException a DOM method should throw when it
    /// operates on <paramref name="doc"/>: the document's own (iframe) realm when
    /// it has a nested browsing context, else the caller's realm. WebIDL requires
    /// the error to be an instance of the target document's <c>DOMException</c>.</summary>
    private static JsRealm ThrowRealmFor(JsRealm realm, Document doc)
        => IFrameBinding.RealmForDocument(realm, doc) ?? realm;

    /// <summary>The document's title element (HTML §document.title): when the
    /// document element is an SVG <c>svg</c>, the first SVG <c>title</c> child of
    /// it; otherwise the first <c>title</c> element in tree order.</summary>
    private static Element? FirstTitleElement(Document d)
    {
        const string svgNs = "http://www.w3.org/2000/svg";
        if (d.DocumentElement is { LocalName: "svg", Namespace: svgNs } svgRoot)
        {
            for (var c = svgRoot.FirstChild; c is not null; c = c.NextSibling)
            {
                if (c is Element { LocalName: "title", Namespace: svgNs } st)
                {
                    return st;
                }
            }

            return null;
        }
        foreach (var n in d.Descendants())
        {
            if (n is Element { LocalName: "title" } t)
            {
                return t;
            }
        }

        return null;
    }

    /// <summary>Infra "strip and collapse ASCII whitespace": remove leading and
    /// trailing ASCII whitespace and replace any internal run of ASCII whitespace
    /// (tab/LF/FF/CR/space only — U+000B and other Unicode spaces are kept) with a
    /// single U+0020.</summary>
    private static string StripAndCollapseAsciiWhitespace(string s)
    {
        static bool IsAsciiWs(char c) => c is '\t' or '\n' or '\f' or '\r' or ' ';
        var sb = new System.Text.StringBuilder(s.Length);
        var pendingSpace = false;
        var started = false;
        foreach (var c in s)
        {
            if (IsAsciiWs(c)) { pendingSpace = started; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
            started = true;
        }
        return sb.ToString();
    }

    private static bool IsValidName(string name)
    {
        if (string.IsNullOrEmpty(name) || !IsNameStart(name[0]))
        {
            return false;
        }

        for (var i = 1; i < name.Length; i++)
        {
            if (!IsNameChar(name[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>DOM "validate and extract": the qualified name must match the
    /// Name production (colons allowed); the prefix is everything before the
    /// first colon. So "f:o:o" is a valid Name with prefix "f" (then the
    /// namespace check decides), while ";foo"/"f}oo" are not Names at all.</summary>
    private static bool IsValidQName(string qname, out string? prefix)
    {
        prefix = null;
        if (string.IsNullOrEmpty(qname))
        {
            return false;
        }

        var colon = qname.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return IsValidName(qname);    // unprefixed — must be a full NCName (NameStart first)
        }
        // An empty prefix (":local") or empty local part ("prefix:") is invalid.
        if (colon == 0 || colon == qname.Length - 1)
        {
            return false;
        }
        // Match browser leniency exactly (see the WPT name-validity cases): the
        // prefix is an Nmtoken — every char is a NameChar but a leading digit is
        // tolerated ("0:a" is valid) — while the local part must be a real NCName
        // whose first char is a NameStartChar ("a:0" is invalid). A colon inside
        // the local part is left to the namespace check ("f:o:o" → NamespaceError),
        // so the local is validated as NameStart followed by NameChars.
        var prefixPart = qname[..colon];
        var localPart = qname[(colon + 1)..];
        foreach (var c in prefixPart)
        {
            if (!IsNameChar(c))
            {
                return false;
            }
        }

        if (!IsNameStart(localPart[0]))
        {
            return false;
        }

        for (var i = 1; i < localPart.Length; i++)
        {
            if (!IsNameChar(localPart[i]))
            {
                return false;
            }
        }

        prefix = prefixPart;              // everything before the FIRST colon
        return true;
    }

    /// <summary>DOM §4.5 "validate and extract" for createElementNS/setAttributeNS.
    /// Throws InvalidCharacterError for a malformed qualified name and
    /// NamespaceError for prefix/namespace mismatches.</summary>
    private static void ValidateQualifiedName(JsRealm realm, string? ns, string qname)
    {
        if (!IsValidQName(qname, out var prefix))
        {
            throw DomExceptionBinding.Throw(realm, "InvalidCharacterError", $"'{qname}' is not a valid qualified name");
        }

        var nsOrNull = string.IsNullOrEmpty(ns) ? null : ns;
        if (prefix is not null && nsOrNull is null)
        {
            throw DomExceptionBinding.Throw(realm, "NamespaceError", "a prefix requires a non-null namespace");
        }

        if (prefix == "xml" && nsOrNull != XmlNamespace)
        {
            throw DomExceptionBinding.Throw(realm, "NamespaceError", "the 'xml' prefix requires the XML namespace");
        }

        var isXmlns = qname == "xmlns" || prefix == "xmlns";
        if (isXmlns != (nsOrNull == XmlnsNamespace))
        {
            throw DomExceptionBinding.Throw(realm, "NamespaceError", "the xmlns name/namespace must match");
        }
    }

    /// <summary>Run the HTML fragment parsing algorithm for <paramref name="markup"/>
    /// using <paramref name="context"/> as the parsing context element, returning a
    /// <see cref="DocumentFragment"/> whose nodes share the context element's
    /// document. Falls back to a throwaway <see cref="Document"/> for detached
    /// elements that have no owner document yet.</summary>
    private static DocumentFragment ParseFragment(Element context, string markup)
    {
        var ownerDocument = context.OwnerDocument ?? new Document();
        return HtmlParsing.Backend.ParseFragment(markup, context, ownerDocument);
    }

    /// <summary>Lookup a method on a parent prototype and call it bound to
    /// <paramref name="thisV"/>. Used when a subclass re-stamps a method that
    /// has the same body as its parent's (purely cosmetic).</summary>
    private static JsValue CallInherited(JsRealm realm, JsValue thisV, JsValue[] args,
        JsObject parentProto, string name)
    {
        var fn = parentProto.Get(name);
        return AbstractOperations.Call(realm.ActiveVm, fn, thisV, args);
    }

    /// <summary>Build a plain DOMRect-shaped JS object from a snapshot rect.
    /// Spec calls for a real <c>DOMRect</c> prototype; the bag here is
    /// duck-compatible for the read-only paths every test we care about
    /// touches (<c>r.width</c>, <c>r.top</c>, etc.).</summary>
    internal static JsObject BuildDomRect(JsRealm realm, LayoutRect rect)
    {
        var o = new JsObject(realm.ObjectPrototype);
        o.Set("x", JsValue.Number(rect.X));
        o.Set("y", JsValue.Number(rect.Y));
        o.Set("width", JsValue.Number(rect.Width));
        o.Set("height", JsValue.Number(rect.Height));
        o.Set("top", JsValue.Number(rect.Top));
        o.Set("right", JsValue.Number(rect.Right));
        o.Set("bottom", JsValue.Number(rect.Bottom));
        o.Set("left", JsValue.Number(rect.Left));
        // CSSOM View §6 — DOMRectReadOnly.toJSON() serializes the members.
        EventTargetBinding.DefineMethod(realm, o, "toJSON", (_, _) =>
        {
            var j = new JsObject(realm.ObjectPrototype);
            j.Set("x", JsValue.Number(rect.X));
            j.Set("y", JsValue.Number(rect.Y));
            j.Set("width", JsValue.Number(rect.Width));
            j.Set("height", JsValue.Number(rect.Height));
            j.Set("top", JsValue.Number(rect.Top));
            j.Set("right", JsValue.Number(rect.Right));
            j.Set("bottom", JsValue.Number(rect.Bottom));
            j.Set("left", JsValue.Number(rect.Left));
            return JsValue.Object(j);
        }, length: 0);
        return o;
    }

    /// <summary>A minimal CanvasRenderingContext2D supporting the text-metrics
    /// path (<c>font</c> + <c>measureText</c>) plus no-op drawing methods so
    /// code that grabs a 2d context doesn't crash. <c>measureText</c> width is
    /// an approximation: average glyph advance ≈ 0.5em of the font's px size,
    /// which is enough for column auto-sizing not to throw or collapse.</summary>
    private static JsObject BuildCanvas2dContext(JsRealm realm)
    {
        var ctx = new JsObject(realm.ObjectPrototype);
        ctx.DefineOwnProperty("font",
            PropertyDescriptor.Data(JsValue.String("10px sans-serif"),
                writable: true, enumerable: true, configurable: true));

        EventTargetBinding.DefineMethod(realm, ctx, "measureText", (thisV, args) =>
        {
            var text = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
            var fontPx = 10.0;
            if (thisV.IsObject && thisV.AsObject.Get("font") is { IsString: true } f)
            {
                fontPx = ParseFontPx(f.AsString) ?? 10.0;
            }

            var metrics = new JsObject(realm.ObjectPrototype);
            metrics.Set("width", JsValue.Number(text.Length * fontPx * 0.5));
            return JsValue.Object(metrics);
        }, length: 1);

        // No-op drawing surface: enough for feature-detection / incidental calls.
        foreach (var noop in new[]
        {
            "save", "restore", "beginPath", "closePath", "moveTo", "lineTo",
            "fill", "stroke", "fillRect", "clearRect", "strokeRect", "rect",
            "scale", "rotate", "translate", "setTransform", "fillText", "strokeText",
            "drawImage", "arc", "clip",
        })
        {
            EventTargetBinding.DefineMethod(realm, ctx, noop, (_, _) => JsValue.Undefined, length: 0);
        }

        return ctx;
    }

    /// <summary>Pull the px size out of a CSS <c>font</c> shorthand
    /// (e.g. <c>"normal 12px Arial"</c> → 12). Returns null if absent.</summary>
    private static double? ParseFontPx(string font)
    {
        var idx = font.IndexOf("px", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0)
        {
            return null;
        }

        var start = idx;
        while (start > 0 && (char.IsDigit(font[start - 1]) || font[start - 1] == '.'))
        {
            start--;
        }

        return double.TryParse(font.AsSpan(start, idx - start),
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static JsValue ReadOffsetMetric(JsRealm realm, JsValue thisV, Func<OffsetMetrics, double> select)
    {
        var host = WindowBinding.LayoutHostForRealm(realm);
        if (DomWrappers.UnwrapElement(thisV) is { } e &&
            host is not null && host.TryGetOffsetMetrics(e, out var m))
        {
            return JsValue.Number(select(m));
        }
        return JsValue.Number(0);
    }

    // ---- CSSOM View §7 scroll surface (browser-plan/scroll-model.md WP3) ----

    /// <summary>CSSOM-View scroll-into-view alignment keywords.</summary>
    private enum ScrollAlign { Start, Center, End, Nearest }

    /// <summary>True when <paramref name="e"/> is the document's root element
    /// — CSSOM View §7 routes its scrollTop/scrollLeft/scrollWidth/
    /// scrollHeight/scrollTo to the viewport (standards mode; the quirks-mode
    /// body special case is not modelled).</summary>
    private static bool IsDocumentScroller(Element e)
        => e.OwnerDocument is { } doc && ReferenceEquals(doc.DocumentElement, e);

    /// <summary>CSSOM View §7 dom-element-scrolltop / dom-element-scrollleft,
    /// read side: the root element reads the viewport scroll position, a
    /// scroll container reads its store entry, everything else reads 0.</summary>
    private static JsValue ReadScrollOffset(JsRealm realm, JsValue thisV, bool vertical)
    {
        var host = WindowBinding.LayoutHostForRealm(realm);
        if (host is null || DomWrappers.UnwrapElement(thisV) is not { } e)
        {
            return JsValue.Number(0);
        }

        if (IsDocumentScroller(e))
        {
            var r = host.GetRootScrollMetrics();
            return JsValue.Number(vertical ? r.ScrollTop : r.ScrollLeft);
        }
        if (host.TryGetScrollMetrics(e, out var m))
        {
            return JsValue.Number(vertical ? m.ScrollTop : m.ScrollLeft);
        }

        return JsValue.Number(0);
    }

    /// <summary>CSSOM View §7 dom-element-scrolltop / dom-element-scrollleft,
    /// write side: normalize non-finite values to 0, keep the other axis,
    /// and hand the pair to the host (which flushes layout if dirty, clamps,
    /// stores, and flags — no relayout). A non-scroller write terminates
    /// without effect, per the spec's "has no scrolling box" step.</summary>
    private static JsValue WriteScrollOffset(JsRealm realm, JsValue thisV, JsValue[] args, bool vertical)
    {
        var host = WindowBinding.LayoutHostForRealm(realm);
        if (host is null || DomWrappers.UnwrapElement(thisV) is not { } e)
        {
            return JsValue.Undefined;
        }

        var v = args.Length > 0 ? JsValue.ToNumber(args[0]) : 0;
        if (!double.IsFinite(v))
        {
            v = 0; // CSSOM: normalize non-finite values
        }

        if (IsDocumentScroller(e))
        {
            var r = host.GetRootScrollMetrics();
            host.SetRootScrollOffset(vertical ? r.ScrollLeft : v, vertical ? v : r.ScrollTop);
        }
        else if (host.TryGetScrollMetrics(e, out var m))
        {
            host.SetScrollOffset(e, vertical ? m.ScrollLeft : v, vertical ? v : m.ScrollTop);
        }
        return JsValue.Undefined;
    }

    /// <summary>CSSOM View §7 dom-element-scrollwidth / dom-element-scrollheight:
    /// 1-2. no document / inactive document → the no-host zero fallback below;
    /// 4-5. the root element (standards mode) returns
    ///      max(viewport scrolling area, viewport size) — exactly what the
    ///      store's root entry records;
    /// 6.   no associated box → 0 (both host lookups fail);
    /// 7.   otherwise the width/height of the element's scrolling area. For a
    ///      measured scroll container that is the stored scrollable overflow.
    ///      For a non-scroller no overflow is recorded, so the box's own
    ///      offset size stands in — the spec's max(content, padding box)
    ///      collapses to the box itself when nothing overflows, and the
    ///      offset size only over-reports by the border, preserving the
    ///      pre-store behavior for non-scrollers.</summary>
    private static JsValue ReadScrollSize(JsRealm realm, JsValue thisV, bool vertical)
    {
        var host = WindowBinding.LayoutHostForRealm(realm);
        if (host is null || DomWrappers.UnwrapElement(thisV) is not { } e)
        {
            return JsValue.Number(0);
        }

        if (IsDocumentScroller(e))
        {
            var r = host.GetRootScrollMetrics();
            var rootSize = vertical ? r.ScrollHeight : r.ScrollWidth;
            // All-zero root metrics mean no store behind the host (legacy
            // snapshot) — fall through to the offset alias below.
            if (rootSize > 0)
            {
                return JsValue.Number(rootSize);
            }
        }
        else if (host.TryGetScrollMetrics(e, out var m))
        {
            return JsValue.Number(vertical ? m.ScrollHeight : m.ScrollWidth);
        }
        if (host.TryGetOffsetMetrics(e, out var om))
        {
            return JsValue.Number(vertical ? om.OffsetHeight : om.OffsetWidth);
        }

        return JsValue.Number(0);
    }

    /// <summary>Shared argument parsing for <c>scroll()</c>/<c>scrollTo()</c>/
    /// <c>scrollBy()</c> on elements and on the window. Accepts the
    /// <c>(x, y)</c> overload and the <c>ScrollToOptions</c> dictionary
    /// (<c>left</c>/<c>top</c>/<c>behavior</c>); <c>behavior</c> is accepted
    /// and ignored — "smooth" is treated as instant (v1 non-goal). Omitted
    /// members keep the current offset (absolute) or contribute a zero delta
    /// (relative). Non-finite values normalize to 0 per CSSOM.</summary>
    internal static void ParseScrollArgs(JsValue[] args, bool relative, double currentX, double currentY,
        out double x, out double y)
    {
        double? left = null, top = null;
        if (args.Length >= 2 && !args[0].IsObject)
        {
            left = JsValue.ToNumber(args[0]);
            top = JsValue.ToNumber(args[1]);
        }
        else if (args.Length >= 1 && args[0].IsObject)
        {
            var o = args[0].AsObject;
            var l = o.Get("left");
            if (!l.IsUndefined)
            {
                left = JsValue.ToNumber(l);
            }

            var t = o.Get("top");
            if (!t.IsUndefined)
            {
                top = JsValue.ToNumber(t);
            }
        }
        var lx = left.GetValueOrDefault();
        var ty = top.GetValueOrDefault();
        if (!double.IsFinite(lx))
        {
            lx = 0;
        }

        if (!double.IsFinite(ty))
        {
            ty = 0;
        }

        if (relative)
        {
            x = currentX + lx;
            y = currentY + ty;
        }
        else
        {
            x = left.HasValue ? lx : currentX;
            y = top.HasValue ? ty : currentY;
        }
    }

    /// <summary>CSSOM View §7 element scroll()/scrollTo()/scrollBy(). The
    /// root element scrolls the viewport; a non-scroller terminates without
    /// effect (the spec's "no associated scrolling box" step).</summary>
    private static JsValue ScrollElementTo(JsRealm realm, JsValue thisV, JsValue[] args, bool relative)
    {
        var host = WindowBinding.LayoutHostForRealm(realm);
        if (host is null || DomWrappers.UnwrapElement(thisV) is not { } e)
        {
            return JsValue.Undefined;
        }

        if (IsDocumentScroller(e))
        {
            var r = host.GetRootScrollMetrics();
            ParseScrollArgs(args, relative, r.ScrollLeft, r.ScrollTop, out var rx, out var ry);
            host.SetRootScrollOffset(rx, ry);
            return JsValue.Undefined;
        }
        if (!host.TryGetScrollMetrics(e, out var m))
        {
            return JsValue.Undefined;
        }

        ParseScrollArgs(args, relative, m.ScrollLeft, m.ScrollTop, out var x, out var y);
        host.SetScrollOffset(e, x, y);
        return JsValue.Undefined;
    }

    /// <summary>CSSOM View §7 dom-element-scrollintoview. Defaults are
    /// block:"start", inline:"nearest"; the boolean form maps true → block
    /// "start", false → block "end" (both inline "nearest"). behavior is
    /// accepted and treated as instant. Resolves the target rect, then walks
    /// each scrolling ancestor outward — innermost first, then the viewport —
    /// adjusting each offset just enough for the requested alignment.</summary>
    private static JsValue ScrollIntoView(JsRealm realm, JsValue thisV, JsValue[] args)
    {
        var host = WindowBinding.LayoutHostForRealm(realm);
        if (host is null || DomWrappers.UnwrapElement(thisV) is not { } e)
        {
            return JsValue.Undefined;
        }

        var blockAlign = ScrollAlign.Start;
        var inlineAlign = ScrollAlign.Nearest;
        if (args.Length > 0)
        {
            if (args[0].IsObject)
            {
                var o = args[0].AsObject;
                blockAlign = ParseScrollAlign(o.Get("block"), ScrollAlign.Start);
                inlineAlign = ParseScrollAlign(o.Get("inline"), ScrollAlign.Nearest);
            }
            else if (!JsValue.ToBoolean(args[0]))
            {
                blockAlign = ScrollAlign.End;
            }
        }

        if (!host.TryGetBoundingClientRect(e, out var target))
        {
            return JsValue.Undefined;
        }

        // Host rects are document-space at scroll zero, so the target's
        // *visual* position at each level is its layout rect minus the
        // (clamped, read-back) offsets of the scrollers already adjusted
        // further in. Walk inside-out so each level sees the inner result.
        double accX = 0, accY = 0;
        for (var p = e.ParentElement; p is not null; p = p.ParentElement)
        {
            if (IsDocumentScroller(p))
            {
                break; // the viewport leg below owns the root
            }

            if (!host.TryGetScrollMetrics(p, out var m))
            {
                continue;
            }

            if (!host.TryGetBoundingClientRect(p, out var box))
            {
                continue;
            }
            // Target in the scroller's scroll-origin (padding-box) space.
            var elemX = target.X - accX - (box.X + m.ClientLeft);
            var elemY = target.Y - accY - (box.Y + m.ClientTop);
            var nx = ResolveScrollAlign(inlineAlign, m.ScrollLeft, elemX, target.Width, m.ClientWidth);
            var ny = ResolveScrollAlign(blockAlign, m.ScrollTop, elemY, target.Height, m.ClientHeight);
            host.SetScrollOffset(p, nx, ny);
            if (host.TryGetScrollMetrics(p, out var after))
            {
                accX += after.ScrollLeft;
                accY += after.ScrollTop;
            }
        }

        // The document scroller (viewport) is the outermost scrolling box;
        // its scroll origin is the document origin.
        var root = host.GetRootScrollMetrics();
        if (root.ClientWidth > 0 || root.ClientHeight > 0)
        {
            var vx = target.X - accX;
            var vy = target.Y - accY;
            var nx = ResolveScrollAlign(inlineAlign, root.ScrollLeft, vx, target.Width, root.ClientWidth);
            var ny = ResolveScrollAlign(blockAlign, root.ScrollTop, vy, target.Height, root.ClientHeight);
            host.SetRootScrollOffset(nx, ny);
        }
        return JsValue.Undefined;
    }

    private static ScrollAlign ParseScrollAlign(JsValue v, ScrollAlign fallback)
    {
        if (v.IsUndefined)
        {
            return fallback;
        }

        var s = JsValue.ToStringValue(v);
        // WebIDL would throw on an unknown enum value; the binding surface is
        // lenient and keeps the default instead.
        if (s.Equals("start", StringComparison.Ordinal))
        {
            return ScrollAlign.Start;
        }

        if (s.Equals("center", StringComparison.Ordinal))
        {
            return ScrollAlign.Center;
        }

        if (s.Equals("end", StringComparison.Ordinal))
        {
            return ScrollAlign.End;
        }

        if (s.Equals("nearest", StringComparison.Ordinal))
        {
            return ScrollAlign.Nearest;
        }

        return fallback;
    }

    /// <summary>One axis of CSSOM View §12 "determine the scroll-into-view
    /// position": the new scroll offset that aligns an element spanning
    /// [<paramref name="pos"/>, <paramref name="pos"/>+<paramref name="size"/>]
    /// (scroll-origin space) inside a scrollport of <paramref name="port"/>,
    /// given the <paramref name="current"/> offset (used by "nearest").</summary>
    private static double ResolveScrollAlign(ScrollAlign align, double current, double pos, double size, double port)
    {
        switch (align)
        {
            case ScrollAlign.Start: return pos;
            case ScrollAlign.End: return pos + size - port;
            case ScrollAlign.Center: return pos + (size - port) / 2;
            default:
                // nearest: fully visible (or spanning the whole port) keeps
                // the current offset; otherwise align the closer edge, with
                // the spec's larger-than-port edge flip.
                var startOut = pos < current;
                var endOut = pos + size > current + port;
                if (startOut == endOut)
                {
                    return current;
                }

                if ((startOut && size <= port) || (endOut && size > port))
                {
                    return pos;
                }

                return pos + size - port;
        }
    }

    /// <summary>If <paramref name="e"/> is a <c>&lt;script&gt;</c> or
    /// <c>&lt;iframe&gt;</c> and the mutated attribute is <c>src</c> with a
    /// non-empty value, notify the engine / iframe loader. For scripts this
    /// runs HTML §4.12.1 "prepare a script"; for iframes it triggers
    /// browsing-context load (WPT-07).</summary>
    private static void MaybeTriggerScriptSrc(JsRealm realm, Element e, string attrName)
    {
        if (!attrName.Equals("src", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var src = e.GetAttribute("src");
        if (string.IsNullOrWhiteSpace(src))
        {
            return;
        }

        if (e.LocalName.Equals("script", StringComparison.OrdinalIgnoreCase))
        {
            ScriptSrcHook.NotifySrcSet(realm, e);
        }
        else if (IFrameBinding.IsFrameElement(e))
        {
            IFrameBinding.OnSrcSet(realm, e);
        }
    }

    private static int NodeTypeFromKind(NodeKind kind) => (int)kind;

    // Internal so the generated nodeName override binding can reuse it.
    internal static string NormalizeNodeName(Node n) => n switch
    {
        // Like tagName: HTML-namespace elements are ASCII-uppercased, others keep case.
        Element e => e.Namespace == Element.HtmlNamespace ? e.TagName.ToUpperInvariant() : e.TagName,
        Document => "#document",
        Text => "#text",
        Comment => "#comment",
        DocumentFragment => "#document-fragment",
        _ => n.NodeName,
    };

    // ---- DOM Living Standard helpers ----------------------------------------

    /// <summary>DOM §4.4.4 cloneNode implementation. Shallow copies tag+attributes
    /// for Elements; deep additionally clones the entire subtree.</summary>
    private static Node CloneNode(JsRealm realm, Node source, bool deep)
    {
        var doc = source.OwnerDocument ?? (source as Document);
        Node clone = source switch
        {
            Element el when doc is not null => CloneElement(doc, el),
            Element el => new Element(el.TagName, el.Namespace),
            Text t => doc?.CreateTextNode(t.Data) ?? new Text(t.Data),
            Comment c => doc?.CreateComment(c.Data) ?? new Comment(c.Data),
            DocumentFragment when doc is not null => doc.CreateDocumentFragment(),
            DocumentFragment => new DocumentFragment(),
            _ => source, // Documents and other node types: return as-is (simplified)
        };

        if (deep && clone != source)
        {
            for (var child = source.FirstChild; child is not null; child = child.NextSibling)
            {
                var childClone = CloneNode(realm, child, deep: true);
                clone.AppendChild(childClone);
            }
        }
        return clone;
    }

    private static Element CloneElement(Document doc, Element el)
    {
        // Preserve case + prefix for namespaced elements; HTML elements keep the
        // lowercasing path.
        var clone = el.Prefix is not null || el.Namespace != Element.HtmlNamespace
            ? doc.CreateElementNS(el.Namespace, el.Prefix is null ? el.LocalName : el.Prefix + ":" + el.LocalName)
            : doc.CreateElement(el.LocalName);
        // Copy attributes, preserving any namespace/qualified name.
        // Clone the AttrNode so the clone element gets its own independent nodes.
        foreach (var attr in el.Attributes)
        {
            clone.Attributes.SetNamedItemNS(attr.Clone());
        }

        return clone;
    }

    /// <summary>Helper for before/after/prepend/append: inserts <paramref name="arg"/>
    /// (a JS Node wrapper or a string) before <paramref name="before"/> in
    /// <paramref name="parent"/>. A null <paramref name="before"/> means
    /// append at the end.</summary>
    private static void InsertAdjacentNode(JsRealm realm, Node parent, JsValue arg, Node? before)
    {
        Node child;
        if (arg.IsString)
        {
            var doc = parent.OwnerDocument ?? (parent as Document);
            child = doc?.CreateTextNode(JsValue.ToStringValue(arg)) ?? new Text(JsValue.ToStringValue(arg));
        }
        else if (DomWrappers.UnwrapNode(arg) is { } n)
        {
            child = n;
        }
        else
        {
            // Non-node, non-string: coerce to string, make text node.
            var s = JsValue.ToStringValue(arg);
            var doc = parent.OwnerDocument ?? (parent as Document);
            child = doc?.CreateTextNode(s) ?? new Text(s);
        }
        parent.InsertBefore(child, before);
    }

    /// <summary>DOM §4.2.6 normalize: removes empty Text nodes and merges
    /// adjacent Text sibling nodes. Operates on the node's subtree.</summary>
    private static void NormalizeNode(Node n)
    {
        var child = n.FirstChild;
        while (child is not null)
        {
            var next = child.NextSibling;
            if (child is Text t)
            {
                if (string.IsNullOrEmpty(t.Data))
                {
                    n.RemoveChild(child);
                }
                else
                {
                    // Merge subsequent adjacent text nodes.
                    while (next is Text t2)
                    {
                        var following = t2.NextSibling;
                        t.Data += t2.Data;
                        n.RemoveChild(t2);
                        next = following;
                    }
                }
            }
            else
            {
                NormalizeNode(child);
            }
            child = next;
        }
    }

    /// <summary>DOM §7.1 token validation for DOMTokenList add/remove/toggle/replace:
    /// an empty token throws SyntaxError, a token with ASCII whitespace throws
    /// InvalidCharacterError.</summary>
    private static void ValidateDomToken(JsRealm realm, string token)
    {
        if (token.Length == 0)
        {
            throw DomExceptionBinding.Throw(realm, "SyntaxError", "The token provided must not be empty.");
        }

        foreach (var c in token)
        {
            if (c == '\t' || c == '\n' || c == '\f' || c == '\r' || c == ' ')
            {
                throw DomExceptionBinding.Throw(realm, "InvalidCharacterError",
                    "The token provided contains HTML space characters, which are not valid in tokens.");
            }
        }
    }

    /// <summary>Build a DOMTokenList JS object wrapping the element's classList.
    /// Spec: DOM §7.1. Methods: add, remove, toggle, contains, replace, item,
    /// forEach, keys, values, entries. Properties: length, value.</summary>
    internal static DomTokenListObject BuildDomTokenList(JsRealm realm, Element element)
        => BuildDomTokenList(realm, element, element.ClassList, "class");

    private static DomTokenListObject BuildDomTokenList(JsRealm realm, Element element, DomTokenList cl, string attrName)
    {
        var obj = new DomTokenListObject(realm.ObjectPrototype, cl);

        // Object.prototype.toString.call(classList) === "[object DOMTokenList]".
        obj.DefineOwnProperty(Starling.Js.Intrinsics.SymbolCtor.ToStringTag,
            PropertyDescriptor.Data(JsValue.String("DOMTokenList"), writable: false, enumerable: false, configurable: true));

        EventTargetBinding.DefineAccessor(realm, obj, "length",
            (_, _) => JsValue.Number(cl.Count));
        EventTargetBinding.DefineAccessor(realm, obj, "value",
            (_, _) => JsValue.String(element.GetAttribute(attrName) ?? ""),
            (_, args) =>
            {
                element.SetAttribute(attrName, args.Length > 0 ? JsValue.ToStringValue(args[0]) : "");
                return JsValue.Undefined;
            });

        EventTargetBinding.DefineMethod(realm, obj, "contains", (_, args) =>
        {
            if (args.Length == 0)
            {
                return JsValue.False;
            }

            try { return JsValue.Boolean(cl.Contains(JsValue.ToStringValue(args[0]))); }
            catch { return JsValue.False; }
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, obj, "add", (_, args) =>
        {
            // DOM §7.1 — validate every token first (empty -> SyntaxError,
            // whitespace -> InvalidCharacterError), then add them all.
            var tokens = new string[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                tokens[i] = JsValue.ToStringValue(args[i]);
                ValidateDomToken(realm, tokens[i]);
            }
            foreach (var t in tokens)
            {
                cl.Add(t);
            }

            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, obj, "remove", (_, args) =>
        {
            var tokens = new string[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                tokens[i] = JsValue.ToStringValue(args[i]);
                ValidateDomToken(realm, tokens[i]);
            }
            foreach (var t in tokens)
            {
                cl.Remove(t);
            }

            return JsValue.Undefined;
        }, length: 0);
        EventTargetBinding.DefineMethod(realm, obj, "toggle", (_, args) =>
        {
            if (args.Length == 0)
            {
                return JsValue.False;
            }

            var token = JsValue.ToStringValue(args[0]);
            ValidateDomToken(realm, token);
            bool result;
            if (args.Length > 1 && !args[1].IsUndefined)
            {
                var force = JsValue.ToBoolean(args[1]);
                if (force) { cl.Add(token); result = true; }
                else { cl.Remove(token); result = false; }
            }
            else
            {
                if (cl.Contains(token)) { cl.Remove(token); result = false; }
                else { cl.Add(token); result = true; }
            }
            return JsValue.Boolean(result);
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, obj, "replace", (_, args) =>
        {
            if (args.Length < 2)
            {
                throw new JsThrow(realm.NewTypeError("replace requires 2 arguments"));
            }

            var oldToken = JsValue.ToStringValue(args[0]);
            var newToken = JsValue.ToStringValue(args[1]);
            ValidateDomToken(realm, oldToken);
            ValidateDomToken(realm, newToken);
            // Single attribute write so replace() yields exactly one mutation.
            return JsValue.Boolean(cl.Replace(oldToken, newToken));
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, obj, "item", (_, args) =>
        {
            if (args.Length == 0)
            {
                return JsValue.Null;
            }

            var idx = (int)JsValue.ToNumber(args[0]);
            return idx >= 0 && idx < cl.Count ? JsValue.String(cl[idx]) : JsValue.Null;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, obj, "forEach", (_, args) =>
        {
            if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
            {
                return JsValue.Undefined;
            }

            var fn = args[0];
            var thisArg = args.Length > 1 ? args[1] : JsValue.Undefined;
            for (var i = 0; i < cl.Count; i++)
            {
                var token = JsValue.String(cl[i]);
                AbstractOperations.Call(realm.ActiveVm, fn, thisArg, new[] { token, JsValue.Number(i), JsValue.Object(obj) });
            }
            return JsValue.Undefined;
        }, length: 1);
        // keys/values/entries are spec'd as iterators (not Arrays — the WPT
        // "must not be Array" assertions check this), so build a snapshot array
        // of the current tokens and hand back a real array iterator over it.
        JsValue TokenArray()
        {
            var items = new List<JsValue>(cl.Count);
            for (var i = 0; i < cl.Count; i++)
            {
                items.Add(JsValue.String(cl[i]));
            }

            return MakeArray(realm, items);
        }
        EventTargetBinding.DefineMethod(realm, obj, "keys", (_, _) =>
            Starling.Js.Intrinsics.IteratorIntrinsics.CreateArrayIterator(
                realm, TokenArray(), Starling.Js.Intrinsics.ArrayIteratorKind.Key), length: 0);
        JsValue ValuesIterator(JsValue _t, JsValue[] _a) =>
            Starling.Js.Intrinsics.IteratorIntrinsics.CreateArrayIterator(
                realm, TokenArray(), Starling.Js.Intrinsics.ArrayIteratorKind.Value);
        EventTargetBinding.DefineMethod(realm, obj, "values", ValuesIterator, length: 0);
        EventTargetBinding.DefineMethod(realm, obj, "entries", (_, _) =>
            Starling.Js.Intrinsics.IteratorIntrinsics.CreateArrayIterator(
                realm, TokenArray(), Starling.Js.Intrinsics.ArrayIteratorKind.KeyAndValue), length: 0);
        // DOMTokenList is iterable: @@iterator is the same function object as
        // values() (spec'd as an alias for a setlike/indexed iterable).
        obj.DefineOwnProperty(Starling.Js.Intrinsics.SymbolCtor.Iterator,
            PropertyDescriptor.Data(obj.Get("values"), writable: true, enumerable: false, configurable: true));
        EventTargetBinding.DefineMethod(realm, obj, "toString",
            (_, _) => JsValue.String(element.GetAttribute(attrName) ?? ""), length: 0);

        return obj;
    }

    /// <summary>Build an inline-style CSSStyleDeclaration-shaped object for
    /// an element's style attribute. Property reads/writes parse/serialize
    /// the element's `style` attribute as a flat CSS property bag.
    /// Spec: CSSOM §6.1 CSSStyleDeclaration with cssText / getPropertyValue /
    /// setProperty / removeProperty plus camelCase shorthand accessors.</summary>
    private static JsObject BuildInlineStyleDecl(JsRealm realm, Element element)
    {
        // Parse current style attribute into a dict; serialize back on mutation.
        var obj = new JsObject(realm.ObjectPrototype);

        EventTargetBinding.DefineAccessor(realm, obj, "cssText",
            (_, _) => JsValue.String(element.GetAttribute("style") ?? ""),
            (_, args) =>
            {
                var v = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                if (string.IsNullOrWhiteSpace(v))
                {
                    element.RemoveAttribute("style");
                }
                else
                {
                    element.SetAttribute("style", v);
                }

                return JsValue.Undefined;
            });

        EventTargetBinding.DefineMethod(realm, obj, "getPropertyValue", (_, args) =>
        {
            if (args.Length == 0)
            {
                return JsValue.String("");
            }

            var prop = JsValue.ToStringValue(args[0]).Trim().ToLowerInvariant();
            return JsValue.String(ParseInlineStyleProp(element, prop));
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, obj, "setProperty", (_, args) =>
        {
            if (args.Length < 2)
            {
                return JsValue.Undefined;
            }

            var prop = JsValue.ToStringValue(args[0]).Trim().ToLowerInvariant();
            var value = JsValue.ToStringValue(args[1]).Trim();
            WriteInlineStyleProp(element, prop, value);
            return JsValue.Undefined;
        }, length: 2);

        EventTargetBinding.DefineMethod(realm, obj, "removeProperty", (_, args) =>
        {
            if (args.Length == 0)
            {
                return JsValue.String("");
            }

            var prop = JsValue.ToStringValue(args[0]).Trim().ToLowerInvariant();
            var old = ParseInlineStyleProp(element, prop);
            WriteInlineStyleProp(element, prop, null);
            return JsValue.String(old);
        }, length: 1);

        // Camel-case / kebab-case accessors for the most-used CSS properties.
        // The bundle uses: display, position, backgroundClip, filter, left, top,
        // right, cssText, zoom. We expose all CommonComputedStyleProps plus more.
        foreach (var kebab in AllInlineStyleProperties)
        {
            var capturedKebab = kebab;
            var camel = KebabToCamel(kebab);
            // Kebab accessor
            EventTargetBinding.DefineAccessor(realm, obj, capturedKebab,
                (_, _) => JsValue.String(ParseInlineStyleProp(element, capturedKebab)),
                (_, a) => { WriteInlineStyleProp(element, capturedKebab, a.Length > 0 ? JsValue.ToStringValue(a[0]) : ""); return JsValue.Undefined; });
            // CamelCase accessor (only if different from kebab)
            if (camel != capturedKebab)
            {
                EventTargetBinding.DefineAccessor(realm, obj, camel,
                    (_, _) => JsValue.String(ParseInlineStyleProp(element, capturedKebab)),
                    (_, a) => { WriteInlineStyleProp(element, capturedKebab, a.Length > 0 ? JsValue.ToStringValue(a[0]) : ""); return JsValue.Undefined; });
            }
        }

        return obj;
    }

    /// <summary>CSS Typed OM 1 §6.3 — a mutable <c>StylePropertyMap</c> over the
    /// element's inline <c>style</c> attribute. <c>get</c>/<c>getAll</c> return
    /// CSSStyleValue objects (via <see cref="CssBinding"/>); <c>set</c>/<c>append</c>
    /// accept a CSSStyleValue or a string and serialize back into the attribute.</summary>
    private static JsObject BuildInlineStyleMap(JsRealm realm, Element element)
    {
        var map = new JsObject(realm.ObjectPrototype);

        EventTargetBinding.DefineMethod(realm, map, "get", (_, args) =>
        {
            if (args.Length == 0)
            {
                return JsValue.Undefined;
            }

            var prop = JsValue.ToStringValue(args[0]).Trim().ToLowerInvariant();
            var text = ParseInlineStyleProp(element, prop);
            return string.IsNullOrEmpty(text) ? JsValue.Undefined : JsValue.Object(CssBinding.WrapDeclaredValue(realm, prop, text));
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, map, "getAll", (_, args) =>
        {
            if (args.Length == 0)
            {
                return MakeArray(realm, Array.Empty<JsValue>());
            }

            var prop = JsValue.ToStringValue(args[0]).Trim().ToLowerInvariant();
            var text = ParseInlineStyleProp(element, prop);
            return string.IsNullOrEmpty(text)
                ? MakeArray(realm, Array.Empty<JsValue>())
                : MakeArray(realm, new[] { JsValue.Object(CssBinding.WrapDeclaredValue(realm, prop, text)) });
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, map, "has", (_, args) =>
            args.Length > 0 && !string.IsNullOrEmpty(ParseInlineStyleProp(element, JsValue.ToStringValue(args[0]).Trim().ToLowerInvariant()))
                ? JsValue.True : JsValue.False, length: 1);

        EventTargetBinding.DefineMethod(realm, map, "set", (_, args) =>
        {
            if (args.Length < 2)
            {
                return JsValue.Undefined;
            }

            var prop = JsValue.ToStringValue(args[0]).Trim().ToLowerInvariant();
            WriteInlineStyleProp(element, prop, CoerceCssText(realm, args[1]));
            return JsValue.Undefined;
        }, length: 2);

        EventTargetBinding.DefineMethod(realm, map, "append", (_, args) =>
        {
            if (args.Length < 2)
            {
                return JsValue.Undefined;
            }

            var prop = JsValue.ToStringValue(args[0]).Trim().ToLowerInvariant();
            var add = CoerceCssText(realm, args[1]);
            var existing = ParseInlineStyleProp(element, prop);
            WriteInlineStyleProp(element, prop, string.IsNullOrEmpty(existing) ? add : existing + ", " + add);
            return JsValue.Undefined;
        }, length: 2);

        EventTargetBinding.DefineMethod(realm, map, "delete", (_, args) =>
        {
            if (args.Length > 0)
            {
                WriteInlineStyleProp(element, JsValue.ToStringValue(args[0]).Trim().ToLowerInvariant(), null);
            }

            return JsValue.Undefined;
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, map, "clear", (_, _) =>
        {
            element.RemoveAttribute("style");
            return JsValue.Undefined;
        }, length: 0);

        EventTargetBinding.DefineAccessor(realm, map, "size",
            (_, _) => JsValue.Number(InlineStyleEntries(element).Count));

        EventTargetBinding.DefineMethod(realm, map, "forEach", (_, args) =>
        {
            if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
            {
                return JsValue.Undefined;
            }

            foreach (var (name, value) in InlineStyleEntries(element))
            {
                AbstractOperations.Call(realm.ActiveVm, args[0], JsValue.Undefined,
                    new[] { JsValue.Object(CssBinding.WrapDeclaredValue(realm, name, value)), JsValue.String(name), JsValue.Object(map) });
            }

            return JsValue.Undefined;
        }, length: 1);

        return map;
    }

    /// <summary>CSS Typed OM 1 §6.2 — a read-only <c>StylePropertyMapReadOnly</c>
    /// over the element's computed style, backed by the layout host. Enumeration
    /// spans the known property set, filtered to properties with a resolved value.</summary>
    private static JsObject BuildComputedStyleMap(JsRealm realm, Element element)
    {
        var map = new JsObject(realm.ObjectPrototype);
        var host = WindowBinding.LayoutHostForRealm(realm);

        string Resolve(string prop) => host?.GetComputedProperty(element, prop) ?? "";

        EventTargetBinding.DefineMethod(realm, map, "get", (_, args) =>
        {
            if (args.Length == 0)
            {
                return JsValue.Undefined;
            }

            var prop = JsValue.ToStringValue(args[0]).Trim().ToLowerInvariant();
            var text = Resolve(prop);
            return string.IsNullOrEmpty(text) ? JsValue.Undefined : JsValue.Object(CssBinding.WrapDeclaredValue(realm, prop, text));
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, map, "getAll", (_, args) =>
        {
            if (args.Length == 0)
            {
                return MakeArray(realm, Array.Empty<JsValue>());
            }

            var prop = JsValue.ToStringValue(args[0]).Trim().ToLowerInvariant();
            var text = Resolve(prop);
            return string.IsNullOrEmpty(text)
                ? MakeArray(realm, Array.Empty<JsValue>())
                : MakeArray(realm, new[] { JsValue.Object(CssBinding.WrapDeclaredValue(realm, prop, text)) });
        }, length: 1);

        EventTargetBinding.DefineMethod(realm, map, "has", (_, args) =>
            args.Length > 0 && !string.IsNullOrEmpty(Resolve(JsValue.ToStringValue(args[0]).Trim().ToLowerInvariant()))
                ? JsValue.True : JsValue.False, length: 1);

        EventTargetBinding.DefineAccessor(realm, map, "size", (_, _) =>
        {
            var n = 0;
            foreach (var prop in InlineStylePropertyNames)
            {
                if (!string.IsNullOrEmpty(Resolve(prop)))
                {
                    n++;
                }
            }

            return JsValue.Number(n);
        });

        EventTargetBinding.DefineMethod(realm, map, "forEach", (_, args) =>
        {
            if (args.Length == 0 || !AbstractOperations.IsCallable(args[0]))
            {
                return JsValue.Undefined;
            }

            foreach (var prop in InlineStylePropertyNames)
            {
                var text = Resolve(prop);
                if (string.IsNullOrEmpty(text))
                {
                    continue;
                }

                AbstractOperations.Call(realm.ActiveVm, args[0], JsValue.Undefined,
                    new[] { JsValue.Object(CssBinding.WrapDeclaredValue(realm, prop, text)), JsValue.String(prop), JsValue.Object(map) });
            }
            return JsValue.Undefined;
        }, length: 1);

        return map;
    }

    /// <summary>Coerce a JS argument (CSSStyleValue object or string/number) to
    /// CSS text for writing into a declaration. Objects are serialized via their
    /// <c>toString</c> method (CSSStyleValue defines one); primitives use ToString.</summary>
    private static string CoerceCssText(JsRealm realm, JsValue v)
    {
        if (v.IsObject)
        {
            var ts = v.AsObject.Get("toString");
            if (AbstractOperations.IsCallable(ts))
            {
                return JsValue.ToStringValue(AbstractOperations.Call(realm.ActiveVm, ts, v, Array.Empty<JsValue>())).Trim();
            }
        }
        return JsValue.ToStringValue(v).Trim();
    }

    /// <summary>Parse the element's inline <c>style</c> attribute into ordered
    /// (kebab-name, value) pairs. Used by the Typed OM style map.</summary>
    private static List<(string Name, string Value)> InlineStyleEntries(Element element)
    {
        var list = new List<(string, string)>();
        var styleAttr = element.GetAttribute("style");
        if (string.IsNullOrEmpty(styleAttr))
        {
            return list;
        }

        foreach (var decl in styleAttr.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = decl.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            list.Add((decl[..colon].Trim().ToLowerInvariant(), decl[(colon + 1)..].Trim()));
        }
        return list;
    }

    /// <summary>Build a DOMStringMap for the element's data-* attributes.
    /// Spec: HTML §2.7.3. Property names use camelCase; attribute names use
    /// kebab-case prefixed with "data-".</summary>
    private static JsDatasetObject BuildDataset(JsRealm realm, Element element)
    {
        // Exotic object: property reads/writes delegate to data-* attributes.
        return new JsDatasetObject(realm.ObjectPrototype, element);
    }

    /// <summary>Read a single property from the element's inline style attribute.</summary>
    private static string ParseInlineStyleProp(Element element, string kebabProp)
    {
        var styleAttr = element.GetAttribute("style");
        if (string.IsNullOrEmpty(styleAttr))
        {
            return "";
        }

        foreach (var decl in styleAttr.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = decl.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var name = decl[..colon].Trim().ToLowerInvariant();
            if (name == kebabProp)
            {
                return decl[(colon + 1)..].Trim();
            }
        }
        return "";
    }

    /// <summary>Set (or remove when <paramref name="value"/> is null/"") a
    /// single property in the element's inline style attribute.</summary>
    private static void WriteInlineStyleProp(Element element, string kebabProp, string? value)
    {
        var styleAttr = element.GetAttribute("style") ?? "";
        var pairs = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Parse existing.
        foreach (var decl in styleAttr.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = decl.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            pairs[decl[..colon].Trim().ToLowerInvariant()] = decl[(colon + 1)..].Trim();
        }
        // Mutate.
        if (string.IsNullOrEmpty(value))
        {
            pairs.Remove(kebabProp);
        }
        else
        {
            pairs[kebabProp] = value;
        }
        // Serialize.
        if (pairs.Count == 0)
        {
            element.RemoveAttribute("style");
        }
        else
        {
            element.SetAttribute("style", string.Join("; ", pairs.Select(p => $"{p.Key}: {p.Value}")));
        }
    }

    private static string KebabToCamel(string kebab)
    {
        if (kebab.IndexOf('-') < 0)
        {
            return kebab;
        }

        var sb = new System.Text.StringBuilder(kebab.Length);
        var upper = false;
        foreach (var c in kebab)
        {
            if (c == '-') { upper = true; continue; }
            sb.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        return sb.ToString();
    }

    /// <summary>The kebab-case CSS property names exposed as camelCase/kebab
    /// accessors on a CSSStyleDeclaration. Shared with <see cref="CssomBinding"/>.</summary>
    internal static IReadOnlyList<string> InlineStylePropertyNames => InlineStyleProperties;

    /// <summary>Public kebab→camel converter shared with <see cref="CssomBinding"/>.</summary>
    internal static string KebabToCamelPublic(string kebab) => KebabToCamel(kebab);

    // =====================================================================
    //                         Attr (WPT-05 DOM §4.9)
    // =====================================================================
    private static void InstallAttr(JsRealm realm)
    {
        // Attr.prototype inherits from Node.prototype (Attr is a Node subtype).
        var attrProto = new JsObject(realm.NodePrototype);
        realm.AttrPrototype = attrProto;

        // Attr interface properties — all read-only getters except value.
        EventTargetBinding.DefineAccessor(realm, attrProto, "name",
            (thisV, _) => DomWrappers.UnwrapAttr(thisV) is { } a ? JsValue.String(a.Name) : JsValue.String(""));
        EventTargetBinding.DefineAccessor(realm, attrProto, "localName",
            (thisV, _) => DomWrappers.UnwrapAttr(thisV) is { } a ? JsValue.String(a.LocalName) : JsValue.String(""));
        EventTargetBinding.DefineAccessor(realm, attrProto, "namespaceURI",
            (thisV, _) => DomWrappers.UnwrapAttr(thisV) is { } a && a.Namespace is { } ns
                ? JsValue.String(ns) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, attrProto, "prefix",
            (thisV, _) => DomWrappers.UnwrapAttr(thisV) is { } a && a.Prefix is { } p
                ? JsValue.String(p) : JsValue.Null);
        EventTargetBinding.DefineAccessor(realm, attrProto, "value",
            (thisV, _) => DomWrappers.UnwrapAttr(thisV) is { } a ? JsValue.String(a.Value) : JsValue.String(""),
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapAttr(thisV) is { } a)
                {
                    a.Value = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                }

                return JsValue.Undefined;
            });
        // DOM §4.9 ownerElement
        EventTargetBinding.DefineAccessor(realm, attrProto, "ownerElement",
            (thisV, _) => DomWrappers.UnwrapAttr(thisV) is { } a && a.OwnerElement is { } el
                ? JsValue.Object(DomWrappers.Wrap(realm, el)) : JsValue.Null);
        // specified is always true per DOM spec (compat)
        EventTargetBinding.DefineAccessor(realm, attrProto, "specified",
            (thisV, _) => JsValue.True);

        // Attr constructor — illegal to call directly.
        var attrCtor = new JsNativeFunction(realm, "Attr", 0, (_, _) =>
            throw new JsThrow(realm.NewTypeError("Illegal constructor")), isConstructor: false);
        attrCtor.SetPrototypeOf(realm.NodeConstructor!);
        attrCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(attrProto), writable: false, enumerable: false, configurable: false));
        attrProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(attrCtor), writable: true, enumerable: false, configurable: true));
        realm.AttrConstructor = attrCtor;
        realm.GlobalObject.DefineOwnProperty("Attr",
            PropertyDescriptor.Data(JsValue.Object(attrCtor), writable: true, enumerable: false, configurable: true));
    }

    // =====================================================================
    //                      NamedNodeMap (WPT-05 DOM §4.9)
    // =====================================================================
    private static void InstallNamedNodeMap(JsRealm realm)
    {
        // NamedNodeMap.prototype — just the prototype object. Individual
        // element.attributes instances are JsNamedNodeMapObject (exotic) that
        // inherit from this prototype so method identity holds:
        //   map.item === NamedNodeMap.prototype.item
        var nmProto = new JsObject(realm.ObjectPrototype);
        realm.NamedNodeMapPrototype = nmProto;

        // length is on the prototype so it is NOT an own property of individual
        // element.attributes objects (matching WPT expectations for
        // namednodemap-supported-property-names.html).
        EventTargetBinding.DefineAccessor(realm, nmProto, "length", (thisV, _) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsNamedNodeMapObject nm)
            {
                return JsValue.Number(nm.Length);
            }

            return JsValue.Number(0);
        });

        // All methods are defined on the prototype; they re-dispatch to the
        // backing NamedNodeMap via the exotic object's element reference.
        EventTargetBinding.DefineMethod(realm, nmProto, "item", (thisV, args) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsNamedNodeMapObject nm)
            {
                if (args.Length == 0)
                {
                    return JsValue.Null;
                }

                var idx = (int)JsValue.ToNumber(args[0]);
                var attr = nm.GetItem(idx);
                return attr is null ? JsValue.Null : JsValue.Object(DomWrappers.WrapAttr(realm, attr));
            }
            return JsValue.Null;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, nmProto, "getNamedItem", (thisV, args) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsNamedNodeMapObject nm && args.Length > 0)
            {
                var attr = nm.GetNamedItem(JsValue.ToStringValue(args[0]));
                return attr is null ? JsValue.Null : JsValue.Object(DomWrappers.WrapAttr(realm, attr));
            }
            return JsValue.Null;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, nmProto, "getNamedItemNS", (thisV, args) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsNamedNodeMapObject nm && args.Length >= 2)
            {
                var ns = args[0].IsNullish ? null : JsValue.ToStringValue(args[0]);
                var attr = nm.GetNamedItemNS(ns, JsValue.ToStringValue(args[1]));
                return attr is null ? JsValue.Null : JsValue.Object(DomWrappers.WrapAttr(realm, attr));
            }
            return JsValue.Null;
        }, length: 2);
        EventTargetBinding.DefineMethod(realm, nmProto, "setNamedItem", (thisV, args) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsNamedNodeMapObject nm && args.Length > 0)
            {
                if (DomWrappers.UnwrapAttr(args[0]) is not { } attr)
                {
                    throw new JsThrow(realm.NewTypeError("setNamedItem requires an Attr argument"));
                }

                var old = nm.SetNamedItem(attr);
                return old is null ? JsValue.Null : JsValue.Object(DomWrappers.WrapAttr(realm, old));
            }
            return JsValue.Null;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, nmProto, "setNamedItemNS", (thisV, args) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsNamedNodeMapObject nm && args.Length > 0)
            {
                if (DomWrappers.UnwrapAttr(args[0]) is not { } attr)
                {
                    throw new JsThrow(realm.NewTypeError("setNamedItemNS requires an Attr argument"));
                }

                var old = nm.SetNamedItemNS(attr);
                return old is null ? JsValue.Null : JsValue.Object(DomWrappers.WrapAttr(realm, old));
            }
            return JsValue.Null;
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, nmProto, "removeNamedItem", (thisV, args) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsNamedNodeMapObject nm && args.Length > 0)
            {
                var old = nm.RemoveNamedItem(JsValue.ToStringValue(args[0]));
                if (old is null)
                {
                    throw DomExceptionBinding.Throw(realm, "NotFoundError", "The attribute was not found.");
                }

                return JsValue.Object(DomWrappers.WrapAttr(realm, old));
            }
            throw DomExceptionBinding.Throw(realm, "NotFoundError", "The attribute was not found.");
        }, length: 1);
        EventTargetBinding.DefineMethod(realm, nmProto, "removeNamedItemNS", (thisV, args) =>
        {
            if (thisV.IsObject && thisV.AsObject is JsNamedNodeMapObject nm && args.Length >= 2)
            {
                var ns = args[0].IsNullish ? null : JsValue.ToStringValue(args[0]);
                var old = nm.RemoveNamedItemNS(ns, JsValue.ToStringValue(args[1]));
                if (old is null)
                {
                    throw DomExceptionBinding.Throw(realm, "NotFoundError", "The attribute was not found.");
                }

                return JsValue.Object(DomWrappers.WrapAttr(realm, old));
            }
            throw DomExceptionBinding.Throw(realm, "NotFoundError", "The attribute was not found.");
        }, length: 2);

        // NamedNodeMap constructor — illegal to call directly.
        var nmCtor = new JsNativeFunction(realm, "NamedNodeMap", 0, (_, _) =>
            throw new JsThrow(realm.NewTypeError("Illegal constructor")), isConstructor: false);
        nmCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(nmProto), writable: false, enumerable: false, configurable: false));
        nmProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(nmCtor), writable: true, enumerable: false, configurable: true));
        realm.NamedNodeMapConstructor = nmCtor;
        realm.GlobalObject.DefineOwnProperty("NamedNodeMap",
            PropertyDescriptor.Data(JsValue.Object(nmCtor), writable: true, enumerable: false, configurable: true));
    }

    // Comprehensive list of CSS properties exposed via element.style.X
    private static readonly string[] InlineStyleProperties =
    [
        "display", "visibility", "opacity", "position", "top", "right", "bottom", "left",
        "z-index", "width", "height", "min-width", "min-height", "max-width", "max-height",
        "margin", "margin-top", "margin-right", "margin-bottom", "margin-left",
        "padding", "padding-top", "padding-right", "padding-bottom", "padding-left",
        "border", "border-width", "border-style", "border-color",
        "border-top", "border-right", "border-bottom", "border-left",
        "border-radius", "box-shadow", "outline",
        "background", "background-color", "background-image", "background-clip",
        "color", "font-size", "font-family", "font-weight", "font-style", "line-height",
        "text-align", "text-decoration", "text-transform", "white-space",
        "flex", "flex-direction", "flex-wrap", "justify-content", "align-items", "align-self",
        "gap", "row-gap", "column-gap",
        "overflow", "overflow-x", "overflow-y",
        "cursor", "pointer-events",
        "transform", "transition", "animation",
        "float", "clear",
        "filter", "backdrop-filter",
        "zoom",
        "content", "list-style", "list-style-type",
    ];

    // The seed list above plus every property the CSS engine knows, so
    // el.style.<anyProp> reflects per CSSOM §6.3 (camel-cased IDL attributes).
    private static readonly string[] AllInlineStyleProperties =
        InlineStyleProperties
            .Concat(Starling.Css.Properties.PropertyRegistry.All.Select(Starling.Css.Properties.PropertyRegistry.Name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    // =====================================================================
    //  CharacterData / Text / Comment / DocumentFragment interfaces
    //  DOM §4.10–4.11: expose constructors so `instanceof Text`,
    //  `instanceof Comment`, `instanceof DocumentFragment` work in JS.
    // =====================================================================
    private static void InstallCharacterDataInterfaces(JsRealm realm)
    {
        var nodeProto = realm.NodePrototype!;

        // CharacterData.prototype (§4.10) — inherits from Node.
        var cdProto = new JsObject(nodeProto);
        EventTargetBinding.DefineAccessor(realm, cdProto, "data",
            (thisV, _) => DomWrappers.UnwrapAs<CharacterData>(thisV) is { } cd
                ? JsValue.String(cd.Data) : JsValue.String(""),
            (thisV, args) =>
            {
                if (DomWrappers.UnwrapAs<CharacterData>(thisV) is { } cd)
                {
                    cd.Data = args.Length > 0 ? JsValue.ToStringValue(args[0]) : "";
                }

                return JsValue.Undefined;
            });
        EventTargetBinding.DefineAccessor(realm, cdProto, "length",
            (thisV, _) => DomWrappers.UnwrapAs<CharacterData>(thisV) is { } cd
                ? JsValue.Number(cd.Data.Length) : JsValue.Number(0));
        var cdCtor = MakeIllegalCtor(realm, "CharacterData");
        cdCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(cdProto), writable: false, enumerable: false, configurable: false));
        cdProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(cdCtor), writable: true, enumerable: false, configurable: true));
        realm.GlobalObject.DefineOwnProperty("CharacterData",
            PropertyDescriptor.Data(JsValue.Object(cdCtor), writable: true, enumerable: false, configurable: true));

        // Text.prototype — inherits from CharacterData.
        var textProto = new JsObject(cdProto);
        EventTargetBinding.DefineAccessor(realm, textProto, "wholeText",
            (thisV, _) => DomWrappers.UnwrapAs<Text>(thisV) is { } t
                ? JsValue.String(t.Data) : JsValue.String(""));
        var textCtor = MakeIllegalCtor(realm, "Text");
        textCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(textProto), writable: false, enumerable: false, configurable: false));
        textProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(textCtor), writable: true, enumerable: false, configurable: true));
        realm.GlobalObject.DefineOwnProperty("Text",
            PropertyDescriptor.Data(JsValue.Object(textCtor), writable: true, enumerable: false, configurable: true));

        // Comment.prototype — inherits from CharacterData.
        var commentProto = new JsObject(cdProto);
        var commentCtor = MakeIllegalCtor(realm, "Comment");
        commentCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(commentProto), writable: false, enumerable: false, configurable: false));
        commentProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(commentCtor), writable: true, enumerable: false, configurable: true));
        realm.GlobalObject.DefineOwnProperty("Comment",
            PropertyDescriptor.Data(JsValue.Object(commentCtor), writable: true, enumerable: false, configurable: true));

        // DocumentFragment.prototype — inherits from Node.
        var dfProto = new JsObject(nodeProto);
        var dfCtor = MakeIllegalCtor(realm, "DocumentFragment");
        dfCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(dfProto), writable: false, enumerable: false, configurable: false));
        dfProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(dfCtor), writable: true, enumerable: false, configurable: true));
        realm.GlobalObject.DefineOwnProperty("DocumentFragment",
            PropertyDescriptor.Data(JsValue.Object(dfCtor), writable: true, enumerable: false, configurable: true));

        // ProcessingInstruction.prototype — inherits from CharacterData.
        var piProto = new JsObject(cdProto);
        EventTargetBinding.DefineAccessor(realm, piProto, "target",
            (thisV, _) => DomWrappers.UnwrapAs<ProcessingInstruction>(thisV) is { } pi
                ? JsValue.String(pi.Target) : JsValue.String(""));
        var piCtor = MakeIllegalCtor(realm, "ProcessingInstruction");
        piCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(piProto), writable: false, enumerable: false, configurable: false));
        piProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(piCtor), writable: true, enumerable: false, configurable: true));
        realm.GlobalObject.DefineOwnProperty("ProcessingInstruction",
            PropertyDescriptor.Data(JsValue.Object(piCtor), writable: true, enumerable: false, configurable: true));

        // CDATASection.prototype — inherits from Text.
        var cdataProto = new JsObject(textProto);
        var cdataCtor = MakeIllegalCtor(realm, "CDATASection");
        cdataCtor.DefineOwnProperty("prototype",
            PropertyDescriptor.Data(JsValue.Object(cdataProto), writable: false, enumerable: false, configurable: false));
        cdataProto.DefineOwnProperty("constructor",
            PropertyDescriptor.Data(JsValue.Object(cdataCtor), writable: true, enumerable: false, configurable: true));
        realm.GlobalObject.DefineOwnProperty("CDATASection",
            PropertyDescriptor.Data(JsValue.Object(cdataCtor), writable: true, enumerable: false, configurable: true));

        // Register the prototypes so DomWrappers.WrapNode can use them.
        CharDataProtosPerRealm.Add(realm, new CharDataProtos(
            CharDataProto: cdProto,
            TextProto: textProto,
            CommentProto: commentProto,
            CDataProto: cdataProto,
            DocFragProto: dfProto,
            PIProto: piProto));
    }

    private static JsNativeFunction MakeIllegalCtor(JsRealm realm, string name) =>
        new JsNativeFunction(realm, name, 0,
            (_, _) => throw new JsThrow(realm.NewTypeError("Illegal constructor")),
            isConstructor: false);
}

internal sealed record CharDataProtos(
    JsObject CharDataProto,
    JsObject TextProto,
    JsObject CommentProto,
    JsObject CDataProto,
    JsObject DocFragProto,
    JsObject PIProto);
