using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using Microsoft.Extensions.Logging;
using Starling.Css.Selectors;
using Starling.Dom;
using Starling.Html;

namespace Starling.Bindings.Jint;

// Node / Element / Document prototypes + methods for the Jint backend.
//
// Mirrors the semantics (property names, accessor/operation shape, selector
// grammar, innerHTML parsing) of Starling.Bindings/NodeBindings.cs,
// DomWrappers.cs, and QuerySelectorEngine.cs — but over the Jint value model
// and the JintInterop/JintDomWrapper helpers, not the Starling JS engine types.
//
// Edits are confined to this file (plus the Starling.Html project reference in
// the csproj, required for the HTML fragment parser/serializer). Prototype slots
// are set on ctx.Wrappers so JintDomWrapper.GetOrCreate mints wrappers whose
// prototype is the most-derived installed slot for the node's type.
internal static class NodeBindings
{
    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        var engine = ctx.Engine;

        // ---- prototype chain -------------------------------------------------
        // Node => EventTarget if installed => Object.
        // Element => Node, CharacterData => Node, Text/Comment/CDATA/PI =>
        // CharacterData, DocumentFragment/DocumentType/Attr => Node, Document =>
        // Node. Each slot is registered on ctx.Wrappers so JintDomWrapper.
        // SelectPrototype routes a wrapped node to its most-derived prototype —
        // without this, Text/Comment nodes wrap against the bare Node prototype
        // and `.data`/`.length` (defined on CharacterData.prototype) are
        // unreachable, and `instanceof Text`/`Comment`/… is always false.
        var nodeProto = NewProto(engine, ctx.Wrappers.EventTargetPrototype);
        ctx.Wrappers.NodePrototype = nodeProto;

        var charDataProto = NewProto(engine, nodeProto);
        ctx.Wrappers.CharacterDataPrototype = charDataProto;
        var textProto = NewProto(engine, charDataProto);
        ctx.Wrappers.TextPrototype = textProto;
        var commentProto = NewProto(engine, charDataProto);
        ctx.Wrappers.CommentPrototype = commentProto;
        var cdataProto = NewProto(engine, textProto); // CDATASection extends Text
        ctx.Wrappers.CDataPrototype = cdataProto;
        var piProto = NewProto(engine, charDataProto);
        ctx.Wrappers.ProcessingInstructionPrototype = piProto;

        var elementProto = NewProto(engine, nodeProto);
        ctx.Wrappers.ElementPrototype = elementProto;

        var documentProto = NewProto(engine, nodeProto);
        ctx.Wrappers.DocumentPrototype = documentProto;

        var fragmentProto = NewProto(engine, nodeProto);
        ctx.Wrappers.DocumentFragmentPrototype = fragmentProto;
        var docTypeProto = NewProto(engine, nodeProto);
        ctx.Wrappers.DocumentTypePrototype = docTypeProto;
        // DOM §4.7 DocumentType — name / publicId / systemId.
        Accessor(ctx, docTypeProto, "name",
            (t, _) => ctx.Wrappers.Unwrap(t) is DocumentType d ? JintInterop.Str(d.Name) : JintInterop.Str(""));
        Accessor(ctx, docTypeProto, "publicId",
            (t, _) => ctx.Wrappers.Unwrap(t) is DocumentType d ? JintInterop.Str(d.PublicId) : JintInterop.Str(""));
        Accessor(ctx, docTypeProto, "systemId",
            (t, _) => ctx.Wrappers.Unwrap(t) is DocumentType d ? JintInterop.Str(d.SystemId) : JintInterop.Str(""));
        var attrProto = NewProto(engine, nodeProto);
        ctx.Wrappers.AttrPrototype = attrProto;

        InstallNode(ctx, nodeProto);
        InstallCharacterData(ctx, charDataProto);
        InstallAttr(ctx, attrProto);
        InstallElement(ctx, elementProto);
        InstallDocument(ctx, documentProto);

        InstallConstructors(ctx, nodeProto, elementProto, documentProto, charDataProto, textProto,
            commentProto, cdataProto, piProto, fragmentProto, docTypeProto, attrProto);

        // Expose `document` on the global so scripts can reach the DOM.
        JintInterop.DefineDataProp(engine.Global, "document",
            ctx.Wrappers.Wrap(ctx.Document), writable: true, enumerable: false, configurable: true);
    }

    // =====================================================================
    //                              Node
    // =====================================================================
    private static void InstallNode(JintBackendContext ctx, ObjectInstance proto)
    {
        var engine = ctx.Engine;
        var w = ctx.Wrappers;

        Accessor(ctx, proto, "nodeType",
            (t, _) => w.UnwrapNode(t) is { } n ? JintInterop.Num((int)n.Kind) : JintInterop.Num(0));
        Accessor(ctx, proto, "nodeName",
            (t, _) => w.UnwrapNode(t) is { } n ? JintInterop.Str(NodeName(n)) : JintInterop.Str(""));
        Accessor(ctx, proto, "nodeValue",
            (t, _) => w.UnwrapNode(t)?.NodeValue is { } s ? JintInterop.Str(s) : JsValue.Null,
            (t, args) =>
            {
                if (w.UnwrapNode(t) is { } n)
                {
                    n.NodeValue = Arg(args, 0).IsNull() || Arg(args, 0).IsUndefined() ? null : Str(Arg(args, 0));
                }

                return JsValue.Undefined;
            });
        Accessor(ctx, proto, "textContent",
            (t, _) => w.UnwrapNode(t) is { } n ? JintInterop.Str(n.TextContent) : JintInterop.Str(""),
            (t, args) =>
            {
                if (w.UnwrapNode(t) is { } n)
                {
                    n.TextContent = args.Length > 0 ? Str(args[0]) : "";
                }

                return JsValue.Undefined;
            });
        Accessor(ctx, proto, "parentNode",
            (t, _) => w.UnwrapNode(t)?.ParentNode is { } p ? w.Wrap(p) : JsValue.Null);
        Accessor(ctx, proto, "parentElement",
            (t, _) => w.UnwrapNode(t)?.ParentNode is Element pe ? w.Wrap(pe) : JsValue.Null);
        Accessor(ctx, proto, "firstChild",
            (t, _) => w.UnwrapNode(t)?.FirstChild is { } c ? w.Wrap(c) : JsValue.Null);
        Accessor(ctx, proto, "lastChild",
            (t, _) => w.UnwrapNode(t)?.LastChild is { } c ? w.Wrap(c) : JsValue.Null);
        Accessor(ctx, proto, "nextSibling",
            (t, _) => w.UnwrapNode(t)?.NextSibling is { } c ? w.Wrap(c) : JsValue.Null);
        Accessor(ctx, proto, "previousSibling",
            (t, _) => w.UnwrapNode(t)?.PreviousSibling is { } c ? w.Wrap(c) : JsValue.Null);
        Accessor(ctx, proto, "childNodes", (t, _) =>
        {
            if (w.UnwrapNode(t) is not { } n)
            {
                return EmptyArray(engine);
            }

            return CollectionsBinding.NodeList(ctx, () => n.ChildNodes is IReadOnlyList<Node> list ? list : n.ChildNodes.ToList());
        });
        Accessor(ctx, proto, "ownerDocument", (t, _) =>
        {
            var n = w.UnwrapNode(t);
            if (n is Document)
            {
                return JsValue.Null; // documents have no owner
            }

            return n?.OwnerDocument is { } d ? w.Wrap(d) : JsValue.Null;
        });
        Accessor(ctx, proto, "isConnected",
            (t, _) => JintInterop.Bool(IsConnected(w.UnwrapNode(t))));

        Method(ctx, proto, "appendChild", (t, args) =>
        {
            var parent = w.UnwrapNode(t);
            var child = w.UnwrapNode(Arg(args, 0));
            if (parent is null || child is null)
            {
                throw TypeError(engine, "appendChild requires a Node argument");
            }

            parent.AppendChild(child);
            return args[0];
        }, 1);
        Method(ctx, proto, "removeChild", (t, args) =>
        {
            var parent = w.UnwrapNode(t);
            var child = w.UnwrapNode(Arg(args, 0));
            if (parent is null || child is null)
            {
                throw TypeError(engine, "removeChild requires a Node argument");
            }

            parent.RemoveChild(child);
            return args[0];
        }, 1);
        Method(ctx, proto, "insertBefore", (t, args) =>
        {
            var parent = w.UnwrapNode(t);
            var child = w.UnwrapNode(Arg(args, 0));
            var refChild = w.UnwrapNode(Arg(args, 1));
            if (parent is null || child is null)
            {
                throw TypeError(engine, "insertBefore requires a Node argument");
            }

            parent.InsertBefore(child, refChild);
            return args[0];
        }, 2);
        Method(ctx, proto, "replaceChild", (t, args) =>
        {
            var parent = w.UnwrapNode(t);
            var newChild = w.UnwrapNode(Arg(args, 0));
            var oldChild = w.UnwrapNode(Arg(args, 1));
            if (parent is null || newChild is null || oldChild is null)
            {
                throw TypeError(engine, "replaceChild requires two Node arguments");
            }

            parent.ReplaceChild(newChild, oldChild);
            return args[1];
        }, 2);
        Method(ctx, proto, "hasChildNodes",
            (t, _) => JintInterop.Bool(w.UnwrapNode(t)?.FirstChild is not null), 0);
        Method(ctx, proto, "cloneNode", (t, args) =>
        {
            if (w.UnwrapNode(t) is not { } n)
            {
                return JsValue.Null;
            }

            var deep = args.Length > 0 && Bool(args[0]);
            return w.Wrap(CloneNode(n, deep));
        }, 0);
        Method(ctx, proto, "normalize", (t, _) =>
        {
            if (w.UnwrapNode(t) is { } n)
            {
                NormalizeNode(n);
            }

            return JsValue.Undefined;
        }, 0);
        Method(ctx, proto, "contains", (t, args) =>
        {
            var self = w.UnwrapNode(t);
            var other = w.UnwrapNode(Arg(args, 0));
            if (self is null || other is null)
            {
                return JsBoolean.False;
            }

            for (var n = other; n is not null; n = n.ParentNode)
            {
                if (ReferenceEquals(n, self))
                {
                    return JsBoolean.True;
                }
            }

            return JsBoolean.False;
        }, 1);

        // DOM §4.4 getRootNode — topmost node up the parent chain.
        Method(ctx, proto, "getRootNode", (t, _) =>
        {
            if (w.UnwrapNode(t) is not { } n)
            {
                return JsValue.Null;
            }

            var root = n;
            while (root.ParentNode is { } p)
            {
                root = p;
            }

            return w.Wrap(root);
        }, 0);
        // DOM §4.4 baseURI — the document base URL.
        Accessor(ctx, proto, "baseURI", (t, _) =>
        {
            var n = w.UnwrapNode(t);
            var d = n as Document ?? n?.OwnerDocument;
            return d is null ? JintInterop.Str("") : JintInterop.Str(WindowBinding.UrlFor(ctx));
        });
        // DOM §4.4 namespace lookups.
        Method(ctx, proto, "lookupNamespaceURI", (t, args) =>
        {
            if (w.UnwrapNode(t) is not { } n)
            {
                return JsValue.Null;
            }

            var prefix = args.Length > 0 && !args[0].IsNull() && !args[0].IsUndefined() ? Str(args[0]) : null;
            var r = n.LookupNamespaceURI(prefix);
            return r is null ? JsValue.Null : JintInterop.Str(r);
        }, 1);
        Method(ctx, proto, "lookupPrefix", (t, args) =>
        {
            if (w.UnwrapNode(t) is not { } n)
            {
                return JsValue.Null;
            }

            var ns = args.Length > 0 && !args[0].IsNull() && !args[0].IsUndefined() ? Str(args[0]) : null;
            var r = n.LookupPrefix(ns);
            return r is null ? JsValue.Null : JintInterop.Str(r);
        }, 1);
        Method(ctx, proto, "isDefaultNamespace", (t, args) =>
        {
            if (w.UnwrapNode(t) is not { } n)
            {
                return JsBoolean.False;
            }

            var ns = args.Length > 0 && !args[0].IsNull() && !args[0].IsUndefined() ? Str(args[0]) : null;
            return JintInterop.Bool(n.IsDefaultNamespace(ns));
        }, 1);
        // DOM §4.4.3 isSameNode — identity (=== ).
        Method(ctx, proto, "isSameNode", (t, args) =>
            JintInterop.Bool(w.UnwrapNode(t) is { } s && ReferenceEquals(s, w.UnwrapNode(Arg(args, 0)))), 1);
        // DOM §4.4.3 isEqualNode — structural equality.
        Method(ctx, proto, "isEqualNode", (t, args) =>
            JintInterop.Bool(AreEqual(w.UnwrapNode(t), w.UnwrapNode(Arg(args, 0)))), 1);
        // DOM §4.4.4 compareDocumentPosition — relative position bitmask.
        Method(ctx, proto, "compareDocumentPosition", (t, args) =>
        {
            var self = w.UnwrapNode(t);
            var other = w.UnwrapNode(Arg(args, 0));
            if (self is null || other is null || ReferenceEquals(self, other))
            {
                return JintInterop.Num(0);
            }

            if (!ReferenceEquals(NodeRoot(self), NodeRoot(other)))
            {
                var selfHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(self);
                var otherHash = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(other);
                var precOrFollow = (otherHash < selfHash || (otherHash == selfHash && self.GetHashCode() < other.GetHashCode())) ? 2 : 4;
                return JintInterop.Num(1 | 32 | precOrFollow); // DISCONNECTED | IMPLEMENTATION_SPECIFIC | (PRECEDING|FOLLOWING)
            }
            var bits = 0;
            if (IsAncestor(self, other))
            {
                bits |= 16; // CONTAINED_BY
            }

            if (IsAncestor(other, self))
            {
                bits |= 8;  // CONTAINS
            }

            bits |= IsBeforeInTreeOrder(other, self) ? 2 : 4; // PRECEDING : FOLLOWING
            return JintInterop.Num(bits);
        }, 1);

        // Node type constants (Node.ELEMENT_NODE, etc.) on the prototype.
        DefineNodeConstants(proto);
    }

    // ---- DOM tree helpers (compareDocumentPosition / isEqualNode) ------------

    private static Node NodeRoot(Node n)
    {
        while (n.ParentNode is { } p)
        {
            n = p;
        }

        return n;
    }

    // A namespace argument: null/undefined → null, otherwise the string.
    private static string? NsArg(JsValue[] args, int i)
        => i < args.Length && !args[i].IsNull() && !args[i].IsUndefined() ? Str(args[i]) : null;

    // DOM §4.5.1 DOMImplementation — createHTMLDocument / createDocumentType /
    // createDocument / hasFeature (legacy, always true).
    private static JsObject BuildDomImplementation(JintBackendContext ctx, Document? ownerDoc)
    {
        var engine = ctx.Engine;
        var w = ctx.Wrappers;
        var impl = new JsObject(engine);

        Method(ctx, impl, "hasFeature", (_, _) => JsBoolean.True, 0);
        Method(ctx, impl, "createHTMLDocument", (_, args) =>
        {
            string? title = args.Length > 0 && !args[0].IsUndefined() ? Str(args[0]) : null;
            return w.Wrap(Document.CreateHtmlDocument(title));
        }, 1);
        Method(ctx, impl, "createDocumentType", (_, args) =>
        {
            var qname = args.Length > 0 ? Str(args[0]) : "";
            var publicId = args.Length > 1 ? Str(args[1]) : "";
            var systemId = args.Length > 2 ? Str(args[2]) : "";
            if (qname.Any(c => c is '>' or ' ' or '\t' or '\n' or '\r' or '\f'))
            {
                throw DomExceptionBinding.Throw(ctx, "InvalidCharacterError", $"'{qname}' is not a valid doctype name");
            }

            var dt = ownerDoc is { } d ? d.CreateDocumentType(qname, publicId, systemId) : new DocumentType(qname, publicId, systemId);
            return w.Wrap(dt);
        }, 3);
        Method(ctx, impl, "createDocument", (_, args) =>
        {
            var ns = NsArg(args, 0);
            var qnameVal = args.Length > 1 ? args[1] : JsValue.Undefined;
            var qname = qnameVal.IsNull() ? "" : Str(qnameVal);
            var contentType = ns switch
            {
                "http://www.w3.org/1999/xhtml" => "application/xhtml+xml",
                "http://www.w3.org/2000/svg" => "image/svg+xml",
                _ => "application/xml",
            };
            var doc = new Document { IsHtml = false, ContentType = contentType };
            if (args.Length > 2 && w.Unwrap(args[2]) is DocumentType dt)
            {
                doc.AppendChild(dt);
            }

            if (!string.IsNullOrEmpty(qname))
            {
                doc.AppendChild(doc.CreateElementNS(ns, qname));
            }

            return w.Wrap(doc);
        }, 3);

        return impl;
    }

    // DOM — insert `node` relative to element `e` per a position keyword.
    private static bool InsertAdjacent(JintBackendContext ctx, Element e, string position, Node node)
    {
        switch (position.ToLowerInvariant())
        {
            case "beforebegin":
                if (e.ParentNode is { } pb) { pb.InsertBefore(node, e); return true; }
                return false;
            case "afterbegin":
                e.InsertBefore(node, e.FirstChild); return true;
            case "beforeend":
                e.AppendChild(node); return true;
            case "afterend":
                if (e.ParentNode is { } pa) { pa.InsertBefore(node, e.NextSibling); return true; }
                return false;
            default:
                throw DomExceptionBinding.Throw(ctx, "SyntaxError", $"The value provided ('{position}') is not one of 'beforebegin', 'afterbegin', 'beforeend', or 'afterend'.");
        }
    }

    private static bool IsAncestor(Node ancestor, Node descendant)
    {
        for (var cur = descendant; cur is not null; cur = cur.ParentNode)
        {
            if (ReferenceEquals(cur, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBeforeInTreeOrder(Node a, Node b)
    {
        static List<Node> Path(Node n)
        {
            var path = new List<Node>();
            for (var cur = n; cur is not null; cur = cur.ParentNode)
            {
                path.Insert(0, cur);
            }

            return path;
        }
        var pa = Path(a);
        var pb = Path(b);
        var min = Math.Min(pa.Count, pb.Count);
        for (var i = 0; i < min; i++)
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
                    var atB = eb.GetAttributeNS(atA.Namespace, NamedNodeMap.LocalNameOf(atA.Name));
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
            if (!AreEqual(ac, bc))
            {
                return false;
            }

            ac = ac.NextSibling;
            bc = bc.NextSibling;
        }
        return ac is null && bc is null;
    }

    // =====================================================================
    //                          CharacterData / Text
    // =====================================================================
    private static void InstallCharacterData(JintBackendContext ctx, ObjectInstance proto)
    {
        var w = ctx.Wrappers;
        Accessor(ctx, proto, "data",
            (t, _) => w.Unwrap(t) is CharacterData c ? JintInterop.Str(c.Data) : JintInterop.Str(""),
            (t, args) =>
            {
                if (w.Unwrap(t) is CharacterData c)
                {
                    c.Data = args.Length > 0 ? Str(args[0]) : "";
                }

                return JsValue.Undefined;
            });
        Accessor(ctx, proto, "length",
            (t, _) => w.Unwrap(t) is CharacterData c ? JintInterop.Num(c.Data.Length) : JintInterop.Num(0));

        // DOM §4.10 CharacterData mutation methods (string ops over .Data).
        Method(ctx, proto, "substringData", (t, args) =>
        {
            if (w.Unwrap(t) is not CharacterData c)
            {
                return JintInterop.Str("");
            }

            var data = c.Data;
            var offset = args.Length > 0 ? (int)global::Jint.Runtime.TypeConverter.ToNumber(args[0]) : 0;
            var count = args.Length > 1 ? (int)global::Jint.Runtime.TypeConverter.ToNumber(args[1]) : 0;
            if (offset < 0 || offset > data.Length)
            {
                throw DomExceptionBinding.Throw(ctx, "IndexSizeError", "offset is out of bounds");
            }

            count = Math.Min(count, data.Length - offset);
            return JintInterop.Str(count <= 0 ? "" : data.Substring(offset, count));
        }, 2);
        Method(ctx, proto, "appendData", (t, args) =>
        {
            if (w.Unwrap(t) is CharacterData c)
            {
                c.Data += args.Length > 0 ? Str(args[0]) : "";
            }

            return JsValue.Undefined;
        }, 1);
        Method(ctx, proto, "insertData", (t, args) =>
        {
            if (w.Unwrap(t) is not CharacterData c)
            {
                return JsValue.Undefined;
            }

            var offset = args.Length > 0 ? (int)global::Jint.Runtime.TypeConverter.ToNumber(args[0]) : 0;
            if (offset < 0 || offset > c.Data.Length)
            {
                throw DomExceptionBinding.Throw(ctx, "IndexSizeError", "offset is out of bounds");
            }

            c.Data = c.Data.Insert(offset, args.Length > 1 ? Str(args[1]) : "");
            return JsValue.Undefined;
        }, 2);
        Method(ctx, proto, "deleteData", (t, args) =>
        {
            if (w.Unwrap(t) is not CharacterData c)
            {
                return JsValue.Undefined;
            }

            var offset = args.Length > 0 ? (int)global::Jint.Runtime.TypeConverter.ToNumber(args[0]) : 0;
            var count = args.Length > 1 ? (int)global::Jint.Runtime.TypeConverter.ToNumber(args[1]) : 0;
            if (offset < 0 || offset > c.Data.Length)
            {
                throw DomExceptionBinding.Throw(ctx, "IndexSizeError", "offset is out of bounds");
            }

            count = Math.Min(count, c.Data.Length - offset);
            if (count > 0)
            {
                c.Data = c.Data.Remove(offset, count);
            }

            return JsValue.Undefined;
        }, 2);
        Method(ctx, proto, "replaceData", (t, args) =>
        {
            if (w.Unwrap(t) is not CharacterData c)
            {
                return JsValue.Undefined;
            }

            var offset = args.Length > 0 ? (int)global::Jint.Runtime.TypeConverter.ToNumber(args[0]) : 0;
            var count = args.Length > 1 ? (int)global::Jint.Runtime.TypeConverter.ToNumber(args[1]) : 0;
            var data = args.Length > 2 ? Str(args[2]) : "";
            if (offset < 0 || offset > c.Data.Length)
            {
                throw DomExceptionBinding.Throw(ctx, "IndexSizeError", "offset is out of bounds");
            }

            count = Math.Min(count, c.Data.Length - offset);
            c.Data = c.Data.Remove(offset, count).Insert(offset, data);
            return JsValue.Undefined;
        }, 3);

        // DOM §4.11 Text — splitText / wholeText (only meaningful on Text nodes).
        Method(ctx, proto, "splitText", (t, args) =>
        {
            if (w.Unwrap(t) is not Text txt)
            {
                return JsValue.Null;
            }

            var offset = args.Length > 0 ? (int)global::Jint.Runtime.TypeConverter.ToNumber(args[0]) : 0;
            if (offset < 0 || offset > txt.Data.Length)
            {
                throw DomExceptionBinding.Throw(ctx, "IndexSizeError", "offset is out of bounds");
            }

            var rest = txt.Data[offset..];
            txt.Data = txt.Data[..offset];
            var newNode = (txt.OwnerDocument ?? ctx.Document).CreateTextNode(rest);
            if (txt.ParentNode is { } parent)
            {
                parent.InsertBefore(newNode, txt.NextSibling);
            }

            return w.Wrap(newNode);
        }, 1);
        Accessor(ctx, proto, "wholeText", (t, _) =>
        {
            if (w.Unwrap(t) is not Text txt)
            {
                return JintInterop.Str("");
            }
            // Concatenate the contiguous run of Text siblings containing this node.
            var first = txt;
            while (first.PreviousSibling is Text p)
            {
                first = p;
            }

            var sb = new System.Text.StringBuilder();
            for (Node? n = first; n is Text tn; n = n.NextSibling)
            {
                sb.Append(tn.Data);
            }

            return JintInterop.Str(sb.ToString());
        });
    }

    // =====================================================================
    //                               Attr
    // =====================================================================
    // DOM §4.9 Attr — minimal read surface so a wrapped AttrNode exposes its
    // identity (name/value/ownerElement) and `instanceof Attr` resolves. Full
    // Attr-node integration (element.attributes as a NamedNodeMap, setAttributeNode,
    // …) is tracked separately as a Tier 2 family port.
    private static void InstallAttr(JintBackendContext ctx, ObjectInstance proto)
    {
        var w = ctx.Wrappers;
        Accessor(ctx, proto, "name",
            (t, _) => w.Unwrap(t) is AttrNode a ? JintInterop.Str(a.Name) : JintInterop.Str(""));
        Accessor(ctx, proto, "localName",
            (t, _) => w.Unwrap(t) is AttrNode a ? JintInterop.Str(a.LocalName) : JintInterop.Str(""));
        Accessor(ctx, proto, "prefix",
            (t, _) => w.Unwrap(t) is AttrNode { Prefix: { } p } ? JintInterop.Str(p) : JsValue.Null);
        Accessor(ctx, proto, "namespaceURI",
            (t, _) => w.Unwrap(t) is AttrNode { Namespace: { } ns } ? JintInterop.Str(ns) : JsValue.Null);
        Accessor(ctx, proto, "specified",
            (t, _) => JintInterop.Bool(w.Unwrap(t) is AttrNode));
        Accessor(ctx, proto, "ownerElement",
            (t, _) => w.Unwrap(t) is AttrNode { OwnerElement: { } e } ? w.Wrap(e) : JsValue.Null);
        Accessor(ctx, proto, "value",
            (t, _) => w.Unwrap(t) is AttrNode a ? JintInterop.Str(a.Value) : JintInterop.Str(""),
            (t, args) =>
            {
                if (w.Unwrap(t) is AttrNode a)
                {
                    a.Value = args.Length > 0 ? Str(args[0]) : "";
                }

                return JsValue.Undefined;
            });
    }

    // =====================================================================
    //                             Element
    // =====================================================================
    private static void InstallElement(JintBackendContext ctx, ObjectInstance proto)
    {
        var engine = ctx.Engine;
        var w = ctx.Wrappers;

        // tagName: HTML-namespace elements upper-case; SVG/MathML/other namespaces
        // preserve the original case (DOM §4.9).
        Accessor(ctx, proto, "tagName",
            (t, _) => w.UnwrapElement(t) is { } e
                ? JintInterop.Str(e.Namespace == Element.HtmlNamespace ? e.TagName.ToUpperInvariant() : e.TagName)
                : JintInterop.Str(""));
        Accessor(ctx, proto, "localName",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Str(e.LocalName) : JintInterop.Str(""));
        Accessor(ctx, proto, "namespaceURI",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Str(e.Namespace) : JsValue.Null);
        // HTML §4.12.3 — HTMLTemplateElement.content. Non-template elements get
        // null, matching the IDL.
        Accessor(ctx, proto, "content",
            (t, _) => w.UnwrapElement(t) is HtmlTemplateElement tpl ? w.Wrap(tpl.Content) : JsValue.Null);
        Accessor(ctx, proto, "id",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Str(e.Id) : JintInterop.Str(""),
            (t, args) =>
            {
                if (w.UnwrapElement(t) is { } e)
                {
                    e.Id = args.Length > 0 ? Str(args[0]) : "";
                }

                return JsValue.Undefined;
            });
        Accessor(ctx, proto, "className",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Str(e.GetAttribute("class") ?? "") : JintInterop.Str(""),
            (t, args) =>
            {
                if (w.UnwrapElement(t) is { } e)
                {
                    e.SetAttribute("class", args.Length > 0 ? Str(args[0]) : "");
                }

                return JsValue.Undefined;
            });
        // `value` IDL attribute of form controls. Real browsers expose it on
        // HTMLInputElement/HTMLTextAreaElement/... prototypes, not on every
        // Element; we share one element prototype, so we gate on the tag and
        // return undefined for non-controls (keeps `'value' in el` / `el.value
        // !== undefined` feature tests honest). Backed by the `value` attribute
        // — the same source BoxTreeBuilder paints input text from — so a JS
        // `el.value = x` round-trips and shows up in layout. Without this,
        // `input.value` was undefined and `input.value.toLowerCase()` threw,
        // which is what aborted McMaster's search-box init and blanked the page.
        Accessor(ctx, proto, "value",
            (t, _) =>
            {
                if (w.UnwrapElement(t) is not { } e || !IsFormControl(e))
                {
                    return JsValue.Undefined;
                }

                return JintInterop.Str(HtmlFormControls.Value(e));
            },
            (t, args) =>
            {
                if (w.UnwrapElement(t) is { } e && IsFormControl(e))
                {
                    var v = args.Length > 0 ? Str(args[0]) : "";
                    HtmlFormControls.SetValue(e, v);
                }
                return JsValue.Undefined;
            });
        InstallFormControlAccessors(ctx, proto);
        // focus()/blur() drive Document.FocusedElement — the same focus model the
        // GUI caret and `:focus` styling read, and what `document.activeElement`
        // reports. A field that scripts re-focus after a form submit (the classic
        // todo pattern) thus keeps the GUI caret.
        Method(ctx, proto, "focus", (t, _) =>
        {
            if (w.UnwrapElement(t) is { OwnerDocument: { } d } e)
            {
                d.FocusedElement = e;
            }

            return JsValue.Undefined;
        }, 0);
        Method(ctx, proto, "blur", (t, _) =>
        {
            if (w.UnwrapElement(t) is { OwnerDocument: { } d } e
                && ReferenceEquals(d.FocusedElement, e))
            {
                d.FocusedElement = null;
            }

            return JsValue.Undefined;
        }, 0);

        Accessor(ctx, proto, "innerHTML",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Str(HtmlSerializer.SerializeChildren(e)) : JintInterop.Str(""),
            (t, args) =>
            {
                if (w.UnwrapElement(t) is { } e)
                {
                    var markup = args.Length > 0 ? Str(args[0]) : "";
                    var fragment = ParseFragment(e, markup);
                    while (e.FirstChild is not null)
                    {
                        e.RemoveChild(e.FirstChild);
                    }

                    e.AppendChild(fragment);
                }
                return JsValue.Undefined;
            });
        Accessor(ctx, proto, "outerHTML",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Str(HtmlSerializer.SerializeNode(e)) : JintInterop.Str(""),
            (t, args) =>
            {
                if (w.UnwrapElement(t) is not { } e)
                {
                    return JsValue.Undefined;
                }

                var parent = e.ParentNode
                    ?? throw TypeError(engine, "Cannot set outerHTML on an element with no parent");
                var context = parent as Element ?? e;
                var markup = args.Length > 0 ? Str(args[0]) : "";
                var fragment = ParseFragment(context, markup);
                parent.InsertBefore(fragment, e);
                parent.RemoveChild(e);
                return JsValue.Undefined;
            });
        Accessor(ctx, proto, "innerText",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Str(e.TextContent) : JintInterop.Str(""),
            (t, args) =>
            {
                if (w.UnwrapElement(t) is { } e)
                {
                    e.TextContent = args.Length > 0 ? Str(args[0]) : "";
                }

                return JsValue.Undefined;
            });

        Accessor(ctx, proto, "children", (t, _) =>
        {
            if (w.UnwrapElement(t) is not { } e)
            {
                return EmptyArray(engine);
            }

            return CollectionsBinding.HtmlCollection(ctx, () => ChildElements(e));
        });
        Accessor(ctx, proto, "firstElementChild", (t, _) =>
        {
            if (w.UnwrapElement(t) is not { } e)
            {
                return JsValue.Null;
            }

            for (var n = e.FirstChild; n is not null; n = n.NextSibling)
            {
                if (n is Element c)
                {
                    return w.Wrap(c);
                }
            }

            return JsValue.Null;
        });
        Accessor(ctx, proto, "lastElementChild", (t, _) =>
        {
            if (w.UnwrapElement(t) is not { } e)
            {
                return JsValue.Null;
            }

            for (var n = e.LastChild; n is not null; n = n.PreviousSibling)
            {
                if (n is Element c)
                {
                    return w.Wrap(c);
                }
            }

            return JsValue.Null;
        });
        Accessor(ctx, proto, "nextElementSibling", (t, _) =>
        {
            if (w.UnwrapElement(t) is not { } e)
            {
                return JsValue.Null;
            }

            for (var n = e.NextSibling; n is not null; n = n.NextSibling)
            {
                if (n is Element s)
                {
                    return w.Wrap(s);
                }
            }

            return JsValue.Null;
        });
        Accessor(ctx, proto, "previousElementSibling", (t, _) =>
        {
            if (w.UnwrapElement(t) is not { } e)
            {
                return JsValue.Null;
            }

            for (var n = e.PreviousSibling; n is not null; n = n.PreviousSibling)
            {
                if (n is Element s)
                {
                    return w.Wrap(s);
                }
            }

            return JsValue.Null;
        });
        Accessor(ctx, proto, "childElementCount", (t, _) =>
        {
            if (w.UnwrapElement(t) is not { } e)
            {
                return JintInterop.Num(0);
            }

            var count = 0;
            for (var n = e.FirstChild; n is not null; n = n.NextSibling)
            {
                if (n is Element)
                {
                    count++;
                }
            }

            return JintInterop.Num(count);
        });

        Method(ctx, proto, "getAttribute", (t, args) =>
        {
            if (w.UnwrapElement(t) is not { } e || args.Length == 0)
            {
                return JsValue.Null;
            }

            var v = e.GetAttribute(Str(args[0]));
            return v is null ? JsValue.Null : JintInterop.Str(v);
        }, 1);
        Method(ctx, proto, "setAttribute", (t, args) =>
        {
            if (w.UnwrapElement(t) is { } e && args.Length >= 2)
            {
                var name = Str(args[0]);
                e.SetAttribute(name, Str(args[1]));
                // HTML §4.12.1: setting `src` on a not-yet-started <script> from
                // JS runs "prepare a script" (the deferred-bundle loader pattern,
                // e.g. a parser-created empty <script> that later gets a src as a
                // newly-fetched external script).
                MaybeTriggerScriptSrc(ctx, e, name);
            }
            return JsValue.Undefined;
        }, 2);

        // `script.src` IDL property — get/set the src attribute. The setter
        // mirrors setAttribute('src', …) and runs "prepare a script" so
        // `el.src = url` loads the same way `setAttribute` does. Defined on
        // Element.prototype but only meaningful for <script>; for non-script
        // elements it's a plain attribute round-trip.
        Accessor(ctx, proto, "src",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Str(e.GetAttribute("src") ?? "") : JintInterop.Str(""),
            (t, args) =>
            {
                if (w.UnwrapElement(t) is { } e)
                {
                    e.SetAttribute("src", args.Length > 0 ? Str(args[0]) : "");
                    MaybeTriggerScriptSrc(ctx, e, "src");
                }
                return JsValue.Undefined;
            });
        // `script.async` IDL property. Reflects the boolean `async` content
        // attribute so `el.async = true` is observable by the engine's script
        // classification (HTML §4.12.1). A script-inserted external script
        // defaults to async; analytics snippets (ga.js/gtag) set this flag, so
        // reflecting it is what lets the engine defer them past first paint.
        Accessor(ctx, proto, "async",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Bool(e.HasAttribute("async")) : JsBoolean.False,
            (t, args) =>
            {
                if (w.UnwrapElement(t) is { } e)
                {
                    if (args.Length > 0 && Bool(args[0]))
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
        Method(ctx, proto, "hasAttribute", (t, args) =>
            w.UnwrapElement(t) is { } e && args.Length > 0
                ? JintInterop.Bool(e.HasAttribute(Str(args[0])))
                : JsBoolean.False, 1);
        Method(ctx, proto, "removeAttribute", (t, args) =>
        {
            if (w.UnwrapElement(t) is { } e && args.Length > 0)
            {
                e.RemoveAttribute(Str(args[0]));
            }

            return JsValue.Undefined;
        }, 1);
        Method(ctx, proto, "getAttributeNames", (t, _) =>
        {
            if (w.UnwrapElement(t) is not { } e)
            {
                return EmptyArray(engine);
            }

            var items = new List<JsValue>();
            foreach (var a in e.Attributes)
            {
                items.Add(JintInterop.Str(a.Name));
            }

            return Array(engine, items);
        }, 0);
        Method(ctx, proto, "toggleAttribute", (t, args) =>
        {
            if (w.UnwrapElement(t) is not { } e || args.Length == 0)
            {
                return JsBoolean.False;
            }

            var name = Str(args[0]);
            bool result;
            if (args.Length > 1 && !args[1].IsUndefined())
            {
                var force = Bool(args[1]);
                if (force) { e.SetAttribute(name, ""); result = true; }
                else { e.RemoveAttribute(name); result = false; }
            }
            else if (e.HasAttribute(name)) { e.RemoveAttribute(name); result = false; }
            else { e.SetAttribute(name, ""); result = true; }
            return JintInterop.Bool(result);
        }, 1);

        // attributes — live NamedNodeMap (DOM §4.9). Backed by AttrBinding so
        // indexed/named access yields real wrapped Attr nodes.
        Accessor(ctx, proto, "attributes", (t, _) =>
            w.UnwrapElement(t) is { } e ? AttrBinding.WrapAttributes(ctx, e) : EmptyArray(engine));

        // DOM §4.9 hasAttributes — true when the element has ≥1 attribute.
        Method(ctx, proto, "hasAttributes",
            (t, _) => JintInterop.Bool(w.UnwrapElement(t) is { } e && e.Attributes.Count > 0), 0);
        // HTMLElement.click() — fire a synthetic click MouseEvent.
        Method(ctx, proto, "click", (t, _) =>
        {
            if (w.UnwrapElement(t) is { } e)
            {
                e.DispatchEvent(new Starling.Dom.Events.MouseEvent("click",
                    new Starling.Dom.Events.EventInit(Bubbles: true, Cancelable: true)));
            }

            return JsValue.Undefined;
        }, 0);
        // CSS Typed OM 1 §6 — element.attributeStyleMap (mutable, over inline style)
        // and element.computedStyleMap() (read-only, over computed style).
        Accessor(ctx, proto, "attributeStyleMap",
            (t, _) => w.UnwrapElement(t) is { } e ? BuildInlineStyleMap(ctx, e) : JsValue.Null);
        Method(ctx, proto, "computedStyleMap",
            (t, _) => w.UnwrapElement(t) is { } e ? BuildComputedStyleMap(ctx, e) : JsValue.Null, 0);

        // DOM §4.9 Element.prefix.
        Accessor(ctx, proto, "prefix",
            (t, _) => w.UnwrapElement(t) is { Prefix: { } p } ? JintInterop.Str(p) : JsValue.Null);
        // DOM §4.9 namespaced attribute methods.
        Method(ctx, proto, "getAttributeNS", (t, args) =>
        {
            if (w.UnwrapElement(t) is not { } e || args.Length < 2)
            {
                return JsValue.Null;
            }

            var v = e.GetAttributeNS(NsArg(args, 0), Str(args[1]));
            return v is null ? JsValue.Null : JintInterop.Str(v);
        }, 2);
        Method(ctx, proto, "setAttributeNS", (t, args) =>
        {
            if (w.UnwrapElement(t) is { } e && args.Length >= 3)
            {
                e.SetAttributeNS(NsArg(args, 0), Str(args[1]), Str(args[2]));
            }

            return JsValue.Undefined;
        }, 3);
        Method(ctx, proto, "hasAttributeNS", (t, args) =>
            JintInterop.Bool(w.UnwrapElement(t) is { } e && args.Length >= 2 && e.HasAttributeNS(NsArg(args, 0), Str(args[1]))), 2);
        Method(ctx, proto, "removeAttributeNS", (t, args) =>
        {
            if (w.UnwrapElement(t) is { } e && args.Length >= 2)
            {
                e.RemoveAttributeNS(NsArg(args, 0), Str(args[1]));
            }

            return JsValue.Undefined;
        }, 2);
        Method(ctx, proto, "getElementsByTagNameNS", (t, args) =>
        {
            if (w.UnwrapElement(t) is not { } e || args.Length < 2)
            {
                return EmptyArray(engine);
            }

            var ns = NsArg(args, 0);
            var local = Str(args[1]);
            return CollectionsBinding.HtmlCollection(ctx, () => e.GetElementsByTagNameNS(ns, local).ToList());
        }, 2);

        // DOM — insertAdjacentElement(position, element) / insertAdjacentText(position, data).
        Method(ctx, proto, "insertAdjacentElement", (t, args) =>
        {
            if (w.UnwrapElement(t) is not { } e || args.Length < 2)
            {
                return JsValue.Null;
            }

            if (w.UnwrapNode(args[1]) is not { } node)
            {
                throw TypeError(engine, "insertAdjacentElement: argument 2 must be a Node");
            }

            return InsertAdjacent(ctx, e, Str(args[0]), node) ? w.Wrap(node) : JsValue.Null;
        }, 2);
        Method(ctx, proto, "insertAdjacentText", (t, args) =>
        {
            if (w.UnwrapElement(t) is not { } e || args.Length < 2)
            {
                return JsValue.Undefined;
            }

            var node = (e.OwnerDocument ?? ctx.Document).CreateTextNode(Str(args[1]));
            InsertAdjacent(ctx, e, Str(args[0]), node);
            return JsValue.Undefined;
        }, 2);

        // Selector engine — querySelector / querySelectorAll / matches / closest.
        Method(ctx, proto, "querySelector", (t, args) =>
        {
            if (w.UnwrapElement(t) is not { } e || args.Length == 0)
            {
                return JsValue.Null;
            }

            var match = SelectorFirst(engine, e, Str(args[0]));
            return match is null ? JsValue.Null : w.Wrap(match);
        }, 1);
        Method(ctx, proto, "querySelectorAll", (t, args) =>
        {
            if (w.UnwrapElement(t) is not { } e || args.Length == 0)
            {
                return EmptyArray(engine);
            }
            // querySelectorAll returns a STATIC NodeList (snapshot at call time).
            var snapshot = SelectorAll(engine, e, Str(args[0]));
            return CollectionsBinding.NodeList(ctx, () => snapshot);
        }, 1);
        Method(ctx, proto, "matches", (t, args) =>
        {
            if (w.UnwrapElement(t) is not { } e || args.Length == 0)
            {
                return JsBoolean.False;
            }

            return JintInterop.Bool(SelectorMatches(engine, e, Str(args[0])));
        }, 1);
        Method(ctx, proto, "closest", (t, args) =>
        {
            if (w.UnwrapElement(t) is not { } e || args.Length == 0)
            {
                return JsValue.Null;
            }

            var match = SelectorClosest(engine, e, Str(args[0]));
            return match is null ? JsValue.Null : w.Wrap(match);
        }, 1);
        Method(ctx, proto, "getElementsByTagName", (t, args) =>
        {
            if (w.UnwrapElement(t) is not { } e || args.Length == 0)
            {
                return EmptyArray(engine);
            }

            var name = Str(args[0]);
            return CollectionsBinding.HtmlCollection(ctx, () => DescendantsByTag(e, name));
        }, 1);
        Method(ctx, proto, "getElementsByClassName", (t, args) =>
        {
            if (w.UnwrapElement(t) is not { } e || args.Length == 0)
            {
                return EmptyArray(engine);
            }

            var classes = Str(args[0]).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return CollectionsBinding.HtmlCollection(ctx, () => DescendantsByClass(e, classes));
        }, 1);

        // ChildNode / ParentNode mixins.
        Method(ctx, proto, "remove", (t, _) =>
        {
            if (w.UnwrapNode(t) is { } n)
            {
                n.RemoveFromParent();
            }

            return JsValue.Undefined;
        }, 0);
        Method(ctx, proto, "prepend", (t, args) =>
        {
            if (w.UnwrapNode(t) is not { } parent)
            {
                return JsValue.Undefined;
            }

            var refChild = parent.FirstChild;
            foreach (var a in args)
            {
                InsertAdjacent(w, parent, a, refChild);
            }

            return JsValue.Undefined;
        }, 0);
        Method(ctx, proto, "append", (t, args) =>
        {
            if (w.UnwrapNode(t) is not { } parent)
            {
                return JsValue.Undefined;
            }

            foreach (var a in args)
            {
                InsertAdjacent(w, parent, a, null);
            }

            return JsValue.Undefined;
        }, 0);
        Method(ctx, proto, "before", (t, args) =>
        {
            var self = w.UnwrapNode(t);
            if (self?.ParentNode is not { } parent)
            {
                return JsValue.Undefined;
            }

            foreach (var a in args)
            {
                InsertAdjacent(w, parent, a, self);
            }

            return JsValue.Undefined;
        }, 0);
        Method(ctx, proto, "after", (t, args) =>
        {
            var self = w.UnwrapNode(t);
            if (self?.ParentNode is not { } parent)
            {
                return JsValue.Undefined;
            }

            var refChild = self.NextSibling;
            foreach (var a in args)
            {
                InsertAdjacent(w, parent, a, refChild);
            }

            return JsValue.Undefined;
        }, 0);
        Method(ctx, proto, "replaceWith", (t, args) =>
        {
            var self = w.UnwrapNode(t);
            if (self?.ParentNode is not { } parent)
            {
                return JsValue.Undefined;
            }

            var refChild = self.NextSibling;
            foreach (var a in args)
            {
                InsertAdjacent(w, parent, a, refChild);
            }

            self.RemoveFromParent();
            return JsValue.Undefined;
        }, 0);
        Method(ctx, proto, "replaceChildren", (t, args) =>
        {
            if (w.UnwrapNode(t) is not { } parent)
            {
                return JsValue.Undefined;
            }

            while (parent.FirstChild is not null)
            {
                parent.RemoveChild(parent.FirstChild);
            }

            foreach (var a in args)
            {
                InsertAdjacent(w, parent, a, null);
            }

            return JsValue.Undefined;
        }, 0);
        Method(ctx, proto, "insertAdjacentHTML", (t, args) =>
        {
            if (w.UnwrapElement(t) is not { } e)
            {
                throw TypeError(engine, "insertAdjacentHTML called on a non-Element");
            }

            if (args.Length < 2)
            {
                throw TypeError(engine, "insertAdjacentHTML requires (position, text)");
            }

            var position = Str(args[0]);
            var markup = Str(args[1]);
            switch (position.ToLowerInvariant())
            {
                case "beforebegin":
                    {
                        if (e.ParentNode is not { } parent)
                        {
                            return JsValue.Undefined;
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
                        if (e.ParentNode is not { } parent)
                        {
                            return JsValue.Undefined;
                        }

                        var context = parent as Element ?? e;
                        parent.InsertBefore(ParseFragment(context, markup), e.NextSibling);
                        break;
                    }
                default:
                    throw TypeError(engine, $"insertAdjacentHTML: '{position}' is not a valid position");
            }
            return JsValue.Undefined;
        }, 2);

        // classList / dataset / style — lazily built + cached on the wrapper.
        Accessor(ctx, proto, "classList", (t, _) =>
        {
            if (w.UnwrapElement(t) is not { } e || t is not ObjectInstance wrapper)
            {
                return JsValue.Null;
            }

            return Cached(wrapper, "__classList__", () => BuildDomTokenList(ctx, e));
        });
        Accessor(ctx, proto, "dataset", (t, _) =>
        {
            if (w.UnwrapElement(t) is not { } e || t is not ObjectInstance wrapper)
            {
                return JsValue.Null;
            }

            return Cached(wrapper, "__dataset__", () => BuildDataset(ctx, e));
        });
        Accessor(ctx, proto, "style", (t, _) =>
        {
            if (w.UnwrapElement(t) is not { } e || t is not ObjectInstance wrapper)
            {
                return JsValue.Null;
            }

            return Cached(wrapper, "__style__", () => BuildStyle(ctx, e));
        });

        // Layout-readback APIs — route through the session's optional
        // ILayoutHost,
        // exactly as the Starling backend's NodeBindings does. The host's
        // TryGetBoundingClientRect / TryGetOffsetMetrics trigger the engine's
        // lazy pre-script layout and increment its diagnostics counters. With no
        // host (bare unit-test contexts) every read returns spec-permitted zeros,
        // matching a never-laid-out document.
        Method(ctx, proto, "getBoundingClientRect", (t, _) =>
        {
            if (w.UnwrapElement(t) is { } e && LayoutHost(ctx) is { } host &&
                host.TryGetBoundingClientRect(e, out var r))
            {
                return BuildDomRect(engine, r);
            }
            return BuildDomRect(engine, default);
        }, 0);
        Method(ctx, proto, "getClientRects", (t, _) =>
        {
            // Single-rect simplification — block boxes only return one rect,
            // which covers every layout-readback path the tests touch. Inline
            // flows that emit multiple line boxes would need the box-tree walk.
            if (w.UnwrapElement(t) is { } e && LayoutHost(ctx) is { } host &&
                host.TryGetBoundingClientRect(e, out var r))
            {
                return Array(engine, new JsValue[] { BuildDomRect(engine, r) });
            }
            return EmptyArray(engine);
        }, 0);
        Accessor(ctx, proto, "offsetWidth", (t, _) => ReadOffsetMetric(ctx, t, m => m.OffsetWidth));
        Accessor(ctx, proto, "offsetHeight", (t, _) => ReadOffsetMetric(ctx, t, m => m.OffsetHeight));
        Accessor(ctx, proto, "offsetTop", (t, _) => ReadOffsetMetric(ctx, t, m => m.OffsetTop));
        Accessor(ctx, proto, "offsetLeft", (t, _) => ReadOffsetMetric(ctx, t, m => m.OffsetLeft));
        Accessor(ctx, proto, "clientWidth", (t, _) => ReadOffsetMetric(ctx, t, m => m.ClientWidth));
        Accessor(ctx, proto, "clientHeight", (t, _) => ReadOffsetMetric(ctx, t, m => m.ClientHeight));
        // scrollWidth/Height mirror the border-box metrics until content-overflow
        // is wired (matches the Starling backend).
        Accessor(ctx, proto, "scrollWidth", (t, _) => ReadOffsetMetric(ctx, t, m => m.OffsetWidth));
        Accessor(ctx, proto, "scrollHeight", (t, _) => ReadOffsetMetric(ctx, t, m => m.OffsetHeight));
        // scrollTop/Left stay 0 (no scrolling is wired through this surface yet);
        // the setters are accepted no-ops so assignment doesn't throw.
        Accessor(ctx, proto, "scrollTop", (_, _) => JintInterop.Num(0), (_, _) => JsValue.Undefined);
        Accessor(ctx, proto, "scrollLeft", (_, _) => JintInterop.Num(0), (_, _) => JsValue.Undefined);

        // HTMLCanvasElement.getContext — canvas-only. We have no real raster
        // surface behind the JS seam, but real pages (e.g. McMaster) use a
        // throwaway 2D context purely to measure text widths for layout sizing.
        // Return a stub 2D context (cached per canvas) with a settable `font`
        // and a `measureText` that estimates width from the font size; all the
        // drawing operations are accepted no-ops. Non-canvas elements and
        // unsupported context types return null (spec-correct).
        Method(ctx, proto, "getContext", (t, args) =>
        {
            if (w.UnwrapElement(t) is not { } e ||
                !e.LocalName.Equals("canvas", StringComparison.OrdinalIgnoreCase))
            {
                return JsValue.Null;
            }

            var type = args.Length > 0 ? Str(args[0]) : "";
            if (!type.Equals("2d", StringComparison.OrdinalIgnoreCase))
            {
                return JsValue.Null;
            }

            if (t is not ObjectInstance wrapper)
            {
                return JsValue.Null;
            }

            return Cached(wrapper, "__ctx2d__", () => BuildCanvas2dContext(engine, wrapper));
        }, 1);
    }

    /// <summary>Build a stub <c>CanvasRenderingContext2D</c>. There is no raster
    /// surface behind the JS seam, so drawing ops are no-ops; the one method that
    /// must return a meaningful value is <c>measureText</c>, which pages use to
    /// size text. We estimate the advance width from the px size parsed out of
    /// the current <c>font</c> (≈0.5em per character — a serviceable mean for
    /// proportional fonts). <paramref name="canvas"/> is exposed via the
    /// <c>canvas</c> back-reference per spec.</summary>
    private static JsObject BuildCanvas2dContext(global::Jint.Engine engine, ObjectInstance canvas)
    {
        var c2d = new JsObject(engine);
        JintInterop.DefineDataProp(c2d, "canvas", canvas, writable: false, enumerable: true, configurable: true);
        // Settable state the measurement path reads/writes.
        foreach (var (name, def) in new (string, string)[]
        {
            ("font", "10px sans-serif"), ("textAlign", "start"), ("textBaseline", "alphabetic"),
            ("fillStyle", "#000000"), ("strokeStyle", "#000000"), ("lineCap", "butt"), ("lineJoin", "miter"),
        })
        {
            JintInterop.DefineDataProp(c2d, name, JintInterop.Str(def), writable: true, enumerable: true, configurable: true);
        }
        JintInterop.DefineDataProp(c2d, "lineWidth", JintInterop.Num(1), writable: true, enumerable: true, configurable: true);
        JintInterop.DefineDataProp(c2d, "globalAlpha", JintInterop.Num(1), writable: true, enumerable: true, configurable: true);

        JintInterop.DefineMethod(engine, c2d, "measureText", (self, a) =>
        {
            var text = a.Length > 0 ? Str(a[0]) : "";
            var px = 10.0;
            if (self is ObjectInstance o && o.Get("font") is { } f && !f.IsUndefined())
            {
                px = ParseFontPx(f.ToString());
            }

            var width = text.Length * px * 0.5;
            var metrics = new JsObject(engine);
            JintInterop.DefineDataProp(metrics, "width", JintInterop.Num(width));
            // A couple of TextMetrics fields some scripts read; conservative values.
            JintInterop.DefineDataProp(metrics, "actualBoundingBoxAscent", JintInterop.Num(px * 0.8));
            JintInterop.DefineDataProp(metrics, "actualBoundingBoxDescent", JintInterop.Num(px * 0.2));
            return metrics;
        }, 1);

        // Drawing / state ops — accepted no-ops so canvas code never throws.
        foreach (var name in new[]
        {
            "fillRect", "clearRect", "strokeRect", "fillText", "strokeText", "beginPath", "closePath",
            "moveTo", "lineTo", "rect", "arc", "fill", "stroke", "save", "restore", "scale", "rotate",
            "translate", "transform", "setTransform", "drawImage", "clip", "setLineDash",
        })
        {
            JintInterop.DefineMethod(engine, c2d, name, (_, _) => JsValue.Undefined, 0);
        }
        return c2d;
    }

    /// <summary>Pull the pixel size out of a CSS shorthand <c>font</c> string
    /// (e.g. <c>"normal 12px Arial"</c> → 12). Defaults to 10 when absent.</summary>
    private static double ParseFontPx(string font)
    {
        var idx = font.IndexOf("px", StringComparison.OrdinalIgnoreCase);
        if (idx <= 0)
        {
            return 10.0;
        }

        var start = idx;
        while (start > 0 && (char.IsDigit(font[start - 1]) || font[start - 1] == '.'))
        {
            start--;
        }

        return double.TryParse(font.AsSpan(start, idx - start),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var px)
            ? px : 10.0;
    }

    // =====================================================================
    //                            Document
    // =====================================================================
    private static void InstallDocument(JintBackendContext ctx, ObjectInstance proto)
    {
        var engine = ctx.Engine;
        var w = ctx.Wrappers;

        Accessor(ctx, proto, "documentElement",
            (t, _) => w.UnwrapDocument(t)?.DocumentElement is { } e ? w.Wrap(e) : JsValue.Null);
        Accessor(ctx, proto, "body",
            (t, _) =>
            {
                if (w.UnwrapDocument(t) is not { } d
                    || d.DocumentElement is not { LocalName: "html", Namespace: Element.HtmlNamespace } htmlEl)
                {
                    return JsValue.Null;
                }

                for (var c = htmlEl.FirstChild; c is not null; c = c.NextSibling)
                {
                    if (c is Element { Namespace: Element.HtmlNamespace, LocalName: "body" or "frameset" } b)
                    {
                        return w.Wrap(b);
                    }
                }

                return JsValue.Null;
            },
            (t, args) =>
            {
                if (w.UnwrapDocument(t) is not { } d)
                {
                    return JsValue.Undefined;
                }

                var val = Arg(args, 0);
                if (!val.IsObject() || w.UnwrapElement(val) is not { } newEl)
                {
                    throw TypeError(engine, "document.body must be an HTMLElement");
                }

                if (newEl is not { Namespace: Element.HtmlNamespace, LocalName: "body" or "frameset" })
                {
                    throw DomExceptionBinding.Throw(ctx, "HierarchyRequestError",
                        "document.body must be a body or frameset element");
                }

                Element? current = null;
                if (d.DocumentElement is { LocalName: "html", Namespace: Element.HtmlNamespace } htmlEl)
                {
                    for (var c = htmlEl.FirstChild; c is not null; c = c.NextSibling)
                    {
                        if (c is Element { Namespace: Element.HtmlNamespace, LocalName: "body" or "frameset" } b)
                        {
                            current = b;
                            break;
                        }
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
                    throw DomExceptionBinding.Throw(ctx, "HierarchyRequestError",
                        "document.body cannot be set without a document element");
                }

                return JsValue.Undefined;
            });
        Accessor(ctx, proto, "head",
            (t, _) => w.UnwrapDocument(t)?.Head is { } h ? w.Wrap(h) : JsValue.Null);
        Accessor(ctx, proto, "title",
            (t, _) =>
            {
                if (w.UnwrapDocument(t) is not { } d)
                {
                    return JintInterop.Str("");
                }

                foreach (var n in d.Descendants())
                {
                    if (n is Element { LocalName: "title" } titleEl)
                    {
                        return JintInterop.Str(titleEl.TextContent.Trim());
                    }
                }

                return JintInterop.Str("");
            },
            (t, args) =>
            {
                if (w.UnwrapDocument(t) is not { } d)
                {
                    return JsValue.Undefined;
                }

                var value = args.Length > 0 ? Str(args[0]) : "";
                // Set the text of the first <title>, creating one in <head> if absent.
                Element? titleEl = null;
                foreach (var n in d.Descendants())
                {
                    if (n is Element { LocalName: "title" } found) { titleEl = found; break; }
                }

                if (titleEl is null)
                {
                    titleEl = d.CreateElement("title");
                    (d.Head ?? d.DocumentElement)?.AppendChild(titleEl);
                }
                titleEl.TextContent = value;
                return JsValue.Undefined;
            });
        // The focused element, falling back to <body> per the HTML spec so
        // `document.activeElement.tagName` never throws on an unfocused page.
        Accessor(ctx, proto, "activeElement", (t, _) =>
        {
            if (w.UnwrapDocument(t) is not { } d)
            {
                return JsValue.Null;
            }

            var el = d.FocusedElement ?? (Element?)d.Body;
            return el is null ? JsValue.Null : w.Wrap(el);
        });
        Accessor(ctx, proto, "readyState", (_, _) => JintInterop.Str("complete"));
        Accessor(ctx, proto, "compatMode",
            (t, _) => w.UnwrapDocument(t) is { } d
                ? JintInterop.Str(d.Mode == QuirksMode.Quirks ? "BackCompat" : "CSS1Compat")
                : JintInterop.Str("CSS1Compat"));
        Accessor(ctx, proto, "hidden", (_, _) => JsBoolean.False);
        Accessor(ctx, proto, "visibilityState", (_, _) => JintInterop.Str("visible"));

        // DOM §4.5 — document.implementation returns a DOMImplementation object.
        // Cached per-document on the wrapper (hidden slot) so identity is stable.
        Accessor(ctx, proto, "implementation", (t, _) =>
        {
            if (t is not ObjectInstance wrapper)
            {
                return JsValue.Null;
            }

            return Cached(wrapper, "__domImpl__", () => BuildDomImplementation(ctx));
        });
        Accessor(ctx, proto, "characterSet", (_, _) => JintInterop.Str("UTF-8"));
        Accessor(ctx, proto, "contentType", (_, _) => JintInterop.Str("text/html"));

        // document.location / URL / documentURI — mirror the Starling backend.
        // document.location shares the same object as window.location.
        Accessor(ctx, proto, "location",
            (t, _) => w.UnwrapDocument(t) is null ? JsValue.Null : WindowBinding.LocationObjectFor(ctx));
        Accessor(ctx, proto, "URL",
            (t, _) => w.UnwrapDocument(t) is null ? JintInterop.Str("") : JintInterop.Str(WindowBinding.UrlFor(ctx)));
        Accessor(ctx, proto, "documentURI",
            (t, _) => w.UnwrapDocument(t) is null ? JintInterop.Str("") : JintInterop.Str(WindowBinding.UrlFor(ctx)));
        Accessor(ctx, proto, "defaultView",
            (t, _) => w.UnwrapDocument(t) is null ? JsValue.Null : engine.Global);
        // DOM §4.5 document.doctype — the DocumentType child, or null.
        Accessor(ctx, proto, "doctype",
            (t, _) => w.UnwrapDocument(t) is { DocType: { } dt } ? w.Wrap(dt) : JsValue.Null);

        // DOM §4.5 — node factories + namespace + import/adopt.
        Method(ctx, proto, "createCDATASection", (t, args) =>
        {
            if (w.UnwrapDocument(t) is not { } d)
            {
                throw TypeError(engine, "createCDATASection called on non-Document");
            }

            var data = args.Length > 0 ? Str(args[0]) : "";
            if (data.Contains("]]>", StringComparison.Ordinal))
            {
                throw DomExceptionBinding.Throw(ctx, "InvalidCharacterError", "CDATA section data must not contain ']]>'");
            }

            return w.Wrap(d.CreateCDataSection(data));
        }, 1);
        Method(ctx, proto, "createProcessingInstruction", (t, args) =>
        {
            if (w.UnwrapDocument(t) is not { } d)
            {
                throw TypeError(engine, "createProcessingInstruction called on non-Document");
            }

            var target = args.Length > 0 ? Str(args[0]) : "";
            var data = args.Length > 1 ? Str(args[1]) : "";
            if (data.Contains("?>", StringComparison.Ordinal))
            {
                throw DomExceptionBinding.Throw(ctx, "InvalidCharacterError", "PI data must not contain '?>'");
            }

            return w.Wrap(d.CreateProcessingInstruction(target, data));
        }, 2);
        Method(ctx, proto, "createAttributeNS", (t, args) =>
        {
            if (w.UnwrapDocument(t) is not { } d)
            {
                throw TypeError(engine, "createAttributeNS called on non-Document");
            }

            var ns = NsArg(args, 0);
            var qn = args.Length > 1 ? Str(args[1]) : "";
            if (qn.Length == 0)
            {
                throw DomExceptionBinding.Throw(ctx, "InvalidCharacterError", "createAttributeNS: empty name");
            }

            return w.Wrap(d.CreateAttributeNS(ns, qn));
        }, 2);
        Method(ctx, proto, "adoptNode", (t, args) =>
        {
            if (w.UnwrapDocument(t) is null || args.Length == 0 || w.UnwrapNode(args[0]) is not { } node)
            {
                throw TypeError(engine, "adoptNode requires a Node argument");
            }

            node.RemoveFromParent();
            return args[0];
        }, 1);
        Method(ctx, proto, "importNode", (t, args) =>
        {
            if (w.UnwrapDocument(t) is null || args.Length == 0 || w.UnwrapNode(args[0]) is not { } src)
            {
                throw TypeError(engine, "importNode requires a Node argument");
            }

            var deep = args.Length > 1 && Bool(args[1]);
            return w.Wrap(CloneNode(src, deep));
        }, 1);
        Method(ctx, proto, "getElementsByTagNameNS", (t, args) =>
        {
            if (w.UnwrapDocument(t) is not { } d || args.Length < 2)
            {
                return EmptyArray(engine);
            }

            var ns = NsArg(args, 0);
            var local = Str(args[1]);
            return CollectionsBinding.HtmlCollection(ctx, () => d.GetElementsByTagNameNS(ns, local).ToList());
        }, 2);
        // DOM §4.5 document.implementation — DOMImplementation (cached per wrapper).
        Accessor(ctx, proto, "implementation", (t, _) =>
        {
            if (t is not ObjectInstance wrapper)
            {
                return JsValue.Null;
            }

            const string cacheKey = "__domImpl__";
            var cached = wrapper.Get(cacheKey);
            if (!cached.IsUndefined())
            {
                return cached;
            }

            var impl = BuildDomImplementation(ctx, w.UnwrapDocument(t));
            wrapper.Set(cacheKey, impl);
            return impl;
        });

        Method(ctx, proto, "getElementById", (t, args) =>
        {
            if (w.UnwrapDocument(t) is not { } d || args.Length == 0)
            {
                return JsValue.Null;
            }

            return d.GetElementById(Str(args[0])) is { } e ? w.Wrap(e) : JsValue.Null;
        }, 1);
        Method(ctx, proto, "getElementsByTagName", (t, args) =>
        {
            if (w.UnwrapDocument(t) is not { } d || args.Length == 0)
            {
                return EmptyArray(engine);
            }

            var name = Str(args[0]);
            return CollectionsBinding.HtmlCollection(ctx, () => d.GetElementsByTagName(name).ToList());
        }, 1);
        Method(ctx, proto, "getElementsByClassName", (t, args) =>
        {
            if (w.UnwrapDocument(t) is not { } d || args.Length == 0)
            {
                return EmptyArray(engine);
            }

            var name = Str(args[0]);
            return CollectionsBinding.HtmlCollection(ctx, () => d.GetElementsByClassName(name).ToList());
        }, 1);
        Method(ctx, proto, "getElementsByName", (t, args) =>
        {
            if (w.UnwrapDocument(t) is not { } d || args.Length == 0)
            {
                return CollectionsBinding.NodeList(ctx, () => new List<Node>());
            }

            var name = Str(args[0]);
            return CollectionsBinding.NodeList(ctx, () => DescendantsByName(d, name));
        }, 1);
        Method(ctx, proto, "querySelector", (t, args) =>
        {
            if (w.UnwrapDocument(t) is not { } d || args.Length == 0)
            {
                return JsValue.Null;
            }

            var match = SelectorFirst(engine, d, Str(args[0]));
            return match is null ? JsValue.Null : w.Wrap(match);
        }, 1);
        Method(ctx, proto, "querySelectorAll", (t, args) =>
        {
            if (w.UnwrapDocument(t) is not { } d || args.Length == 0)
            {
                return EmptyArray(engine);
            }

            var snapshot = SelectorAll(engine, d, Str(args[0]));
            return CollectionsBinding.NodeList(ctx, () => snapshot);
        }, 1);
        Method(ctx, proto, "createElement", (t, args) =>
        {
            if (w.UnwrapDocument(t) is not { } d || args.Length == 0)
            {
                throw TypeError(engine, "createElement requires a tag name");
            }

            return w.Wrap(d.CreateElement(Str(args[0])));
        }, 1);
        Method(ctx, proto, "createElementNS", (t, args) =>
        {
            if (w.UnwrapDocument(t) is not { } d || args.Length < 2)
            {
                throw TypeError(engine, "createElementNS requires (namespace, qualifiedName)");
            }

            var ns = Arg(args, 0).IsNull() ? null : Str(args[0]);
            return w.Wrap(d.CreateElement(Str(args[1]), ns));
        }, 2);
        Method(ctx, proto, "createTextNode", (t, args) =>
        {
            if (w.UnwrapDocument(t) is not { } d)
            {
                throw TypeError(engine, "createTextNode called on non-Document");
            }

            return w.Wrap(d.CreateTextNode(args.Length > 0 ? Str(args[0]) : ""));
        }, 1);
        Method(ctx, proto, "createComment", (t, args) =>
        {
            if (w.UnwrapDocument(t) is not { } d)
            {
                throw TypeError(engine, "createComment called on non-Document");
            }

            return w.Wrap(d.CreateComment(args.Length > 0 ? Str(args[0]) : ""));
        }, 1);
        Method(ctx, proto, "createDocumentFragment", (t, _) =>
        {
            if (w.UnwrapDocument(t) is not { } d)
            {
                throw TypeError(engine, "createDocumentFragment called on non-Document");
            }

            return w.Wrap(d.CreateDocumentFragment());
        }, 0);
        // document.createEvent(interface) — a real uninitialized host event of the
        // requested legacy interface (dispatchable; instanceof resolves). Unknown
        // interfaces throw NotSupportedError. See EventTargetBinding.CreateLegacyEvent.
        Method(ctx, proto, "createEvent", (_, a) =>
            EventTargetBinding.CreateLegacyEvent(ctx, a.Length > 0 ? Str(a[0]) : ""), 1);
    }

    // =====================================================================
    //                          Constructors
    // =====================================================================
    // Expose Node/Element/Document/CharacterData/Text/HTMLElement on the global
    // with the right .prototype + .constructor wiring so `instanceof`,
    // `Node.ELEMENT_NODE`, and feature-detection (`'querySelector' in
    // Element.prototype`) work. Calling them throws (illegal constructor).
    private static void InstallConstructors(
        JintBackendContext ctx, ObjectInstance nodeProto, ObjectInstance elementProto,
        ObjectInstance documentProto, ObjectInstance charDataProto, ObjectInstance textProto,
        ObjectInstance commentProto, ObjectInstance cdataProto, ObjectInstance piProto,
        ObjectInstance fragmentProto, ObjectInstance docTypeProto, ObjectInstance attrProto)
    {
        var engine = ctx.Engine;

        var nodeCtor = MakeCtor(engine, "Node", nodeProto);
        DefineNodeConstants(nodeCtor); // also on the constructor (Node.ELEMENT_NODE)
        var elementCtor = MakeCtor(engine, "Element", elementProto);
        var documentCtor = MakeCtor(engine, "Document", documentProto);
        var charDataCtor = MakeCtor(engine, "CharacterData", charDataProto);
        var textCtor = MakeCtor(engine, "Text", textProto);

        // HTMLElement is a thin subclass of Element (so `el instanceof HTMLElement`
        // and library prototype patches resolve). Element wrappers use
        // ElementPrototype directly; HTMLElement.prototype sits between.
        var htmlElementProto = NewProto(engine, elementProto);
        var htmlElementCtor = MakeCtor(engine, "HTMLElement", htmlElementProto);

        Global(engine, "Node", nodeCtor);
        Global(engine, "Element", elementCtor);
        Global(engine, "Document", documentCtor);
        Global(engine, "CharacterData", charDataCtor);
        Global(engine, "Text", textCtor);
        Global(engine, "HTMLElement", htmlElementCtor);

        // CharacterData subtypes + the other Node families, each with a real
        // prototype chained per the DOM hierarchy so `instanceof` resolves and
        // the inherited accessors are reachable on a wrapped node.
        Global(engine, "Comment", MakeCtor(engine, "Comment", commentProto));
        Global(engine, "CDATASection", MakeCtor(engine, "CDATASection", cdataProto));
        Global(engine, "ProcessingInstruction", MakeCtor(engine, "ProcessingInstruction", piProto));
        Global(engine, "DocumentFragment", MakeCtor(engine, "DocumentFragment", fragmentProto));
        Global(engine, "DocumentType", MakeCtor(engine, "DocumentType", docTypeProto));
        Global(engine, "Attr", MakeCtor(engine, "Attr", attrProto));

        // HTMLDocument is a legacy alias of Document; share its prototype so
        // `document instanceof HTMLDocument` resolves.
        Global(engine, "HTMLDocument", MakeCtor(engine, "HTMLDocument", documentProto));

        // Marker constructors for DOM interfaces real pages name in
        // `instanceof` / `typeof X !== 'undefined'` feature tests. We model the
        // collections as plain snapshot arrays (so instanceof is false), but the
        // global names must at least exist so the references don't throw
        // ReferenceError. Non-element interfaces get a plain prototype; element
        // subclasses chain off Element.prototype.
        foreach (var name in new[]
        {
            // NamedNodeMap is installed as a real interface by AttrBinding.
            "DOMStringMap",
            "CSSStyleDeclaration", "DOMRect", "DOMRectReadOnly",
            // File API + misc interfaces used in `instanceof` / feature tests.
            // Blob/File are real classes installed by BlobFileFormDataBinding.
            "FileList", "FileReader", "DataTransfer",
            "ShadowRoot", "MediaQueryList",
            // SVG interfaces named in `instanceof` checks. Notably SVGAnimatedString:
            // an SVG element's `className` is an SVGAnimatedString (not a string), so
            // className helpers branch on `n instanceof SVGAnimatedString`. Our
            // elements use string classNames, so the test is correctly false — the
            // global just has to exist so the reference doesn't throw.
            "SVGAnimatedString", "SVGElement", "SVGSVGElement", "SVGAnimatedLength",
        })
        {
            Global(engine, name, MakeCtor(engine, name, NewProto(engine, null)));
        }
        InstallFormDataConstructor(ctx);
        InstallImageConstructor(ctx);

        foreach (var name in new[]
        {
            "HTMLInputElement", "HTMLAnchorElement", "HTMLImageElement", "HTMLFormElement",
            "HTMLButtonElement", "HTMLSelectElement", "HTMLTextAreaElement", "HTMLScriptElement",
            "HTMLCanvasElement", "HTMLDivElement", "HTMLSpanElement", "HTMLUListElement",
            "HTMLLIElement", "HTMLParagraphElement", "HTMLHeadingElement", "HTMLTableElement",
            // React's focus management does `node instanceof window.HTMLIFrameElement`
            // while walking the active-element chain — a missing global throws.
            "HTMLIFrameElement", "HTMLLabelElement", "HTMLOptionElement", "HTMLBodyElement",
            "HTMLHtmlElement", "HTMLStyleElement", "HTMLLinkElement", "HTMLMetaElement",
            "HTMLPreElement", "HTMLBRElement", "HTMLHRElement", "HTMLTableRowElement",
            "HTMLTableCellElement", "HTMLTableSectionElement", "HTMLFieldSetElement",
            "HTMLLegendElement", "HTMLPictureElement", "HTMLSourceElement", "HTMLVideoElement",
            "HTMLAudioElement", "HTMLMediaElement", "HTMLOptGroupElement", "HTMLDataListElement",
        })
        {
            Global(engine, name, MakeCtor(engine, name, NewProto(engine, htmlElementProto)));
        }
    }

    // =====================================================================
    //                      classList / dataset / style
    // =====================================================================
    private static JintDomTokenListObject BuildDomTokenList(JintBackendContext ctx, Element element)
    {
        var engine = ctx.Engine;
        var cl = element.ClassList;
        // Exotic backing object: integer indices resolve to tokens and the
        // iteration surface (values/keys/entries/forEach/@@iterator) is inherited
        // from %DOMTokenListPrototype%; the token-mutating methods below are own
        // properties.
        var obj = new JintDomTokenListObject(ctx, cl);
        var log = ctx.LoggerFactory.CreateLogger(typeof(NodeBindings));

        Accessor(ctx, obj, "length", (_, _) => JintInterop.Num(cl.Count));
        Accessor(ctx, obj, "value",
            (_, _) => JintInterop.Str(element.GetAttribute("class") ?? ""),
            (_, args) => { element.SetAttribute("class", args.Length > 0 ? Str(args[0]) : ""); return JsValue.Undefined; });

        Method(ctx, obj, "contains", (_, args) =>
        {
            if (args.Length == 0)
            {
                return JsBoolean.False;
            }

            try { return JintInterop.Bool(cl.Contains(Str(args[0]))); }
            catch (Exception ex) { NodeBindingsLog.ClassListInvalidToken(log, "contains", ex.Message); return JsBoolean.False; }
        }, 1);
        Method(ctx, obj, "add", (_, args) =>
        {
            foreach (var a in args)
            {
                try { cl.Add(Str(a)); }
                catch (Exception ex) { /* invalid token */ NodeBindingsLog.ClassListInvalidToken(log, "add", ex.Message); }
            }
            return JsValue.Undefined;
        }, 0);
        Method(ctx, obj, "remove", (_, args) =>
        {
            foreach (var a in args)
            {
                try { cl.Remove(Str(a)); }
                catch (Exception ex) { /* invalid token */ NodeBindingsLog.ClassListInvalidToken(log, "remove", ex.Message); }
            }
            return JsValue.Undefined;
        }, 0);
        Method(ctx, obj, "toggle", (_, args) =>
        {
            if (args.Length == 0)
            {
                return JsBoolean.False;
            }

            var token = Str(args[0]);
            bool result;
            if (args.Length > 1 && !args[1].IsUndefined())
            {
                if (Bool(args[1]))
                {
                    try { cl.Add(token); }
                    catch (Exception ex) { /* invalid */ NodeBindingsLog.ClassListInvalidToken(log, "toggle-add", ex.Message); }
                    result = true;
                }
                else
                {
                    try { cl.Remove(token); }
                    catch (Exception ex) { /* invalid */ NodeBindingsLog.ClassListInvalidToken(log, "toggle-remove", ex.Message); }
                    result = false;
                }
            }
            else if (cl.Contains(token))
            {
                try { cl.Remove(token); }
                catch (Exception ex) { /* invalid */ NodeBindingsLog.ClassListInvalidToken(log, "toggle-remove", ex.Message); }
                result = false;
            }
            else
            {
                try { cl.Add(token); }
                catch (Exception ex) { /* invalid */ NodeBindingsLog.ClassListInvalidToken(log, "toggle-add", ex.Message); }
                result = true;
            }
            return JintInterop.Bool(result);
        }, 1);
        Method(ctx, obj, "replace", (_, args) =>
        {
            if (args.Length < 2)
            {
                return JsBoolean.False;
            }

            var oldT = Str(args[0]);
            var newT = Str(args[1]);
            try
            {
                if (!cl.Contains(oldT))
                {
                    return JsBoolean.False;
                }

                cl.Remove(oldT);
                cl.Add(newT);
                return JsBoolean.True;
            }
            catch (Exception ex)
            {
                NodeBindingsLog.ClassListInvalidToken(log, "replace", ex.Message);
                return JsBoolean.False;
            }
        }, 2);
        Method(ctx, obj, "item", (_, args) =>
        {
            if (args.Length == 0)
            {
                return JsValue.Null;
            }

            var idx = (int)Num(args[0]);
            return idx >= 0 && idx < cl.Count ? JintInterop.Str(cl[idx]) : JsValue.Null;
        }, 1);
        Method(ctx, obj, "toString", (_, _) => JintInterop.Str(element.GetAttribute("class") ?? ""), 0);
        return obj;
    }

    private static JintDatasetObject BuildDataset(JintBackendContext ctx, Element element)
        => new(ctx.Engine, element);

    private static void InstallFormControlAccessors(JintBackendContext ctx, ObjectInstance proto)
    {
        var engine = ctx.Engine;
        var w = ctx.Wrappers;

        Accessor(ctx, proto, "name",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Str(e.GetAttribute("name") ?? "") : JintInterop.Str(""),
            (t, args) => { if (w.UnwrapElement(t) is { } e) { e.SetAttribute("name", args.Length > 0 ? Str(args[0]) : ""); } return JsValue.Undefined; });
        Accessor(ctx, proto, "required",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Bool(e.HasAttribute("required")) : JsBoolean.False,
            (t, args) => { if (w.UnwrapElement(t) is { } e) { SetBoolAttr(e, "required", args.Length > 0 && Bool(args[0])); } return JsValue.Undefined; });
        Accessor(ctx, proto, "disabled",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Bool(e.HasAttribute("disabled")) : JsBoolean.False,
            (t, args) => { if (w.UnwrapElement(t) is { } e) { SetBoolAttr(e, "disabled", args.Length > 0 && Bool(args[0])); } return JsValue.Undefined; });
        Accessor(ctx, proto, "readOnly",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Bool(e.HasAttribute("readonly")) : JsBoolean.False,
            (t, args) => { if (w.UnwrapElement(t) is { } e) { SetBoolAttr(e, "readonly", args.Length > 0 && Bool(args[0])); } return JsValue.Undefined; });
        Accessor(ctx, proto, "multiple",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Bool(e.HasAttribute("multiple")) : JsBoolean.False,
            (t, args) => { if (w.UnwrapElement(t) is { } e) { SetBoolAttr(e, "multiple", args.Length > 0 && Bool(args[0])); } return JsValue.Undefined; });
        Accessor(ctx, proto, "checked",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Bool(HtmlFormControls.Checked(e)) : JsBoolean.False,
            (t, args) => { if (w.UnwrapElement(t) is { } e) { HtmlFormControls.SetChecked(e, args.Length > 0 && Bool(args[0])); } return JsValue.Undefined; });
        Accessor(ctx, proto, "selected",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Bool(e.HasAttribute("selected")) : JsBoolean.False,
            (t, args) => { if (w.UnwrapElement(t) is { } e) { SetBoolAttr(e, "selected", args.Length > 0 && Bool(args[0])); } return JsValue.Undefined; });
        Accessor(ctx, proto, "selectionStart",
            (t, _) => w.UnwrapElement(t) is { } e && HtmlFormControls.IsTextControl(e) ? JintInterop.Num(e.SelectionStart) : JsValue.Null,
            (t, args) => { if (w.UnwrapElement(t) is { } e) { HtmlFormControls.SetSelectionRange(e, (int)Num(Arg(args, 0)), e.SelectionEnd, e.SelectionDirection); } return JsValue.Undefined; });
        Accessor(ctx, proto, "selectionEnd",
            (t, _) => w.UnwrapElement(t) is { } e && HtmlFormControls.IsTextControl(e) ? JintInterop.Num(e.SelectionEnd) : JsValue.Null,
            (t, args) => { if (w.UnwrapElement(t) is { } e) { HtmlFormControls.SetSelectionRange(e, e.SelectionStart, (int)Num(Arg(args, 0)), e.SelectionDirection); } return JsValue.Undefined; });
        Accessor(ctx, proto, "selectionDirection",
            (t, _) => w.UnwrapElement(t) is { } e && HtmlFormControls.IsTextControl(e) ? JintInterop.Str(e.SelectionDirection) : JsValue.Null);
        Accessor(ctx, proto, "validity",
            (t, _) => w.UnwrapElement(t) is { } e ? BuildValidityObject(engine, HtmlFormControls.Validity(e)) : JsValue.Undefined);
        Accessor(ctx, proto, "willValidate",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Bool(HtmlFormControls.WillValidate(e)) : JsBoolean.False);
        Accessor(ctx, proto, "validationMessage",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Str(HtmlFormControls.ValidationMessage(e)) : JintInterop.Str(""));
        Accessor(ctx, proto, "selectedIndex",
            (t, _) => w.UnwrapElement(t) is { } e ? JintInterop.Num(SelectedIndex(e)) : JintInterop.Num(-1),
            (t, args) => { if (w.UnwrapElement(t) is { } e) { SetSelectedIndex(e, (int)Num(Arg(args, 0))); } return JsValue.Undefined; });
        Method(ctx, proto, "setSelectionRange", (t, args) =>
        {
            if (w.UnwrapElement(t) is { } e)
            {
                HtmlFormControls.SetSelectionRange(e, (int)Num(Arg(args, 0)), (int)Num(Arg(args, 1)),
                    args.Length > 2 ? Str(args[2]) : "none");
            }

            return JsValue.Undefined;
        }, 2);
        Method(ctx, proto, "checkValidity", (t, _) =>
            w.UnwrapElement(t) is { } e ? JintInterop.Bool(HtmlFormControls.CheckValidity(e)) : JsBoolean.True, 0);
        Method(ctx, proto, "reportValidity", (t, _) =>
            w.UnwrapElement(t) is { } e ? JintInterop.Bool(HtmlFormControls.CheckValidity(e)) : JsBoolean.True, 0);
        Method(ctx, proto, "setCustomValidity", (t, args) =>
        {
            if (w.UnwrapElement(t) is { } e)
            {
                e.CustomValidationMessage = args.Length > 0 ? Str(args[0]) : "";
            }

            return JsValue.Undefined;
        }, 1);
        Method(ctx, proto, "submit", (t, _) =>
        {
            if (w.UnwrapElement(t) is { LocalName: "form" } form)
            {
                HtmlFormControls.RecordAutocompleteSubmission(form);
            }

            return JsValue.Undefined;
        }, 0);
        Method(ctx, proto, "requestSubmit", (t, _) =>
        {
            if (w.UnwrapElement(t) is not { LocalName: "form" } form)
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
        }, 0);
        Method(ctx, proto, "autocompleteSuggestions", (t, _) =>
            w.UnwrapElement(t) is { } e ? StringArray(engine, HtmlFormControls.AutocompleteSuggestions(e)) : EmptyArray(engine), 0);
    }

    private static JsObject BuildStyle(JintBackendContext ctx, Element element)
    {
        var engine = ctx.Engine;
        var obj = new JsObject(engine);

        Accessor(ctx, obj, "cssText",
            (_, _) => JintInterop.Str(element.GetAttribute("style") ?? ""),
            (_, args) =>
            {
                var v = args.Length > 0 ? Str(args[0]) : "";
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
        Method(ctx, obj, "getPropertyValue", (_, args) =>
            args.Length == 0 ? JintInterop.Str("")
                : JintInterop.Str(ReadStyleProp(element, Str(args[0]).Trim().ToLowerInvariant())), 1);
        Method(ctx, obj, "setProperty", (_, args) =>
        {
            if (args.Length >= 2)
            {
                WriteStyleProp(element, Str(args[0]).Trim().ToLowerInvariant(), Str(args[1]).Trim());
            }

            return JsValue.Undefined;
        }, 2);
        Method(ctx, obj, "removeProperty", (_, args) =>
        {
            if (args.Length == 0)
            {
                return JintInterop.Str("");
            }

            var prop = Str(args[0]).Trim().ToLowerInvariant();
            var old = ReadStyleProp(element, prop);
            WriteStyleProp(element, prop, null);
            return JintInterop.Str(old);
        }, 1);

        foreach (var kebab in AllStyleProperties)
        {
            var k = kebab;
            var camel = KebabToCamel(k);
            Accessor(ctx, obj, k,
                (_, _) => JintInterop.Str(ReadStyleProp(element, k)),
                (_, a) => { WriteStyleProp(element, k, a.Length > 0 ? Str(a[0]) : ""); return JsValue.Undefined; });
            if (camel != k)
            {
                Accessor(ctx, obj, camel,
                    (_, _) => JintInterop.Str(ReadStyleProp(element, k)),
                    (_, a) => { WriteStyleProp(element, k, a.Length > 0 ? Str(a[0]) : ""); return JsValue.Undefined; });
            }
        }
        return obj;
    }

    // =====================================================================
    //                          selector engine
    // =====================================================================
    // Mirrors QuerySelectorEngine.cs: delegate to the full Starling.Css selector
    // parser + matcher so the full grammar works; invalid selectors throw JS
    // SyntaxError.
    private static Element? SelectorFirst(global::Jint.Engine engine, Node root, string selector)
    {
        var list = ParseSelector(engine, selector);
        var ctx = SelectorContext(root);
        foreach (var e in root.DescendantElements())
        {
            if (SelectorMatcher.Matches(list, e, ctx))
            {
                return e;
            }
        }

        return null;
    }

    private static List<Element> SelectorAll(global::Jint.Engine engine, Node root, string selector)
    {
        var list = ParseSelector(engine, selector);
        var ctx = SelectorContext(root);
        var result = new List<Element>();
        foreach (var e in root.DescendantElements())
        {
            if (SelectorMatcher.Matches(list, e, ctx))
            {
                result.Add(e);
            }
        }

        return result;
    }

    private static bool SelectorMatches(global::Jint.Engine engine, Element element, string selector)
        => SelectorMatcher.Matches(ParseSelector(engine, selector), element, SelectorContext(element));

    private static Element? SelectorClosest(global::Jint.Engine engine, Element element, string selector)
    {
        var list = ParseSelector(engine, selector);
        var ctx = SelectorContext(element);
        for (Node? n = element; n is not null; n = n.ParentNode)
        {
            if (n is Element e && SelectorMatcher.Matches(list, e, ctx))
            {
                return e;
            }
        }

        return null;
    }

    private static SelectorMatchContext SelectorContext(Node root)
        => root is Element scope
            ? new SelectorMatchContext { ScopeElement = scope }
            : SelectorMatchContext.Default;

    private static SelectorList ParseSelector(global::Jint.Engine engine, string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            throw SyntaxError(engine, "The selector is empty.");
        }

        SelectorList list;
        try { list = SelectorParser.ParseSelectorList(raw); }
        catch (FormatException ex) { throw SyntaxError(engine, $"'{raw}' is not a valid selector: {ex.Message}"); }
        if (list.Selectors.Count == 0)
        {
            throw SyntaxError(engine, $"'{raw}' is not a valid selector.");
        }

        return list;
    }

    // =====================================================================
    //                         DOM tree helpers
    // =====================================================================
    private static DocumentFragment ParseFragment(Element context, string markup)
    {
        var ownerDocument = context.OwnerDocument ?? new Document();
        return HtmlParsing.Backend.ParseFragment(markup, context, ownerDocument);
    }

    // ---- live-collection snapshot helpers (re-walked on every read) ----------
    private static List<Element> ChildElements(Element e)
    {
        var items = new List<Element>();
        for (var n = e.FirstChild; n is not null; n = n.NextSibling)
        {
            if (n is Element c)
            {
                items.Add(c);
            }
        }

        return items;
    }

    private static List<Element> DescendantsByTag(Element e, string name)
    {
        var items = new List<Element>();
        foreach (var d in e.DescendantElements())
        {
            if (name == "*" || d.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                items.Add(d);
            }
        }

        return items;
    }

    private static List<Element> DescendantsByClass(Element e, string[] classes)
    {
        var items = new List<Element>();
        if (classes.Length == 0)
        {
            return items;
        }

        foreach (var d in e.DescendantElements())
        {
            if (classes.All(d.ClassList.Contains))
            {
                items.Add(d);
            }
        }

        return items;
    }

    private static List<Node> DescendantsByName(Document d, string name)
    {
        var items = new List<Node>();
        foreach (var e in d.DescendantElements())
        {
            if (e.GetAttribute("name") == name)
            {
                items.Add(e);
            }
        }

        return items;
    }

    private static void InsertAdjacent(JintDomWrapper w, Node parent, JsValue arg, Node? before)
    {
        Node child;
        if (arg.IsString())
        {
            var doc = parent.OwnerDocument ?? parent as Document;
            var s = arg.AsString();
            child = doc?.CreateTextNode(s) ?? new Text(s);
        }
        else if (w.UnwrapNode(arg) is { } n)
        {
            child = n;
        }
        else
        {
            var s = Str(arg);
            var doc = parent.OwnerDocument ?? parent as Document;
            child = doc?.CreateTextNode(s) ?? new Text(s);
        }
        parent.InsertBefore(child, before);
    }

    private static Node CloneNode(Node source, bool deep)
    {
        var doc = source.OwnerDocument ?? source as Document;
        Node clone = source switch
        {
            Element el when doc is not null => CloneElement(doc, el),
            Element el => CloneElementDetached(el),
            Text txt => doc?.CreateTextNode(txt.Data) ?? new Text(txt.Data),
            Comment c => doc?.CreateComment(c.Data) ?? new Comment(c.Data),
            DocumentFragment when doc is not null => doc.CreateDocumentFragment(),
            DocumentFragment => new DocumentFragment(),
            _ => source,
        };
        if (deep && !ReferenceEquals(clone, source))
        {
            for (var child = source.FirstChild; child is not null; child = child.NextSibling)
            {
                clone.AppendChild(CloneNode(child, deep: true));
            }
        }

        return clone;
    }

    private static Element CloneElement(Document doc, Element el)
    {
        var clone = doc.CreateElement(el.TagName, el.Namespace);
        foreach (var attr in el.Attributes)
        {
            clone.SetAttribute(attr.Name, attr.Value);
        }

        return clone;
    }

    private static Element CloneElementDetached(Element el)
    {
        var clone = new Element(el.TagName, el.Namespace);
        foreach (var attr in el.Attributes)
        {
            clone.SetAttribute(attr.Name, attr.Value);
        }

        return clone;
    }

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

    private static bool IsConnected(Node? n)
    {
        for (var cur = n; cur is not null; cur = cur.ParentNode)
        {
            if (cur is Document)
            {
                return true;
            }
        }

        return false;
    }

    private static string NodeName(Node n) => n switch
    {
        Element e => e.TagName.ToUpperInvariant(),
        Document => "#document",
        Text => "#text",
        Comment => "#comment",
        DocumentFragment => "#document-fragment",
        _ => n.NodeName,
    };

    // ---- inline style attribute parse/serialize -------------------------
    // ---- CSS Typed OM 1 §6 — StylePropertyMap[ReadOnly] ---------------------

    // Parse a declared value into a CSSStyleValue via the global CSSStyleValue.parse;
    // falls back to the raw string if parse is unavailable/throws.
    private static JsValue ParseCssValue(JintBackendContext ctx, string prop, string text)
    {
        var parse = ctx.Engine.Global.Get("CSSStyleValue");
        if (parse is ObjectInstance pc && pc.Get("parse").IsCallable())
        {
            try { return pc.Get("parse").Call(pc, new JsValue[] { JintInterop.Str(prop), JintInterop.Str(text) }); }
            catch { /* fall through to string */ }
        }
        return JintInterop.Str(text);
    }

    private static List<(string Name, string Value)> InlineStyleEntries(Element element)
    {
        var list = new List<(string, string)>();
        var style = element.GetAttribute("style");
        if (string.IsNullOrEmpty(style))
        {
            return list;
        }

        foreach (var decl in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
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

    private static JsObject BuildInlineStyleMap(JintBackendContext ctx, Element element)
    {
        var engine = ctx.Engine;
        var map = new JsObject(engine);
        string Prop(JsValue[] a) => a.Length > 0 ? Str(a[0]).Trim().ToLowerInvariant() : "";

        Method(ctx, map, "get", (_, a) =>
        {
            if (a.Length == 0)
            {
                return JsValue.Undefined;
            }

            var text = ReadStyleProp(element, Prop(a));
            return string.IsNullOrEmpty(text) ? JsValue.Undefined : ParseCssValue(ctx, Prop(a), text);
        }, 1);
        Method(ctx, map, "getAll", (_, a) =>
        {
            if (a.Length == 0)
            {
                return EmptyArray(engine);
            }

            var text = ReadStyleProp(element, Prop(a));
            return string.IsNullOrEmpty(text) ? EmptyArray(engine) : new JsArray(engine, new[] { ParseCssValue(ctx, Prop(a), text) });
        }, 1);
        Method(ctx, map, "has", (_, a) => JintInterop.Bool(a.Length > 0 && !string.IsNullOrEmpty(ReadStyleProp(element, Prop(a)))), 1);
        Method(ctx, map, "set", (_, a) => { if (a.Length >= 2) { WriteStyleProp(element, Prop(a), CoerceCss(a[1])); } return JsValue.Undefined; }, 2);
        Method(ctx, map, "append", (_, a) =>
        {
            if (a.Length >= 2)
            {
                var existing = ReadStyleProp(element, Prop(a));
                var add = CoerceCss(a[1]);
                WriteStyleProp(element, Prop(a), string.IsNullOrEmpty(existing) ? add : existing + ", " + add);
            }
            return JsValue.Undefined;
        }, 2);
        Method(ctx, map, "delete", (_, a) => { if (a.Length > 0) { WriteStyleProp(element, Prop(a), null); } return JsValue.Undefined; }, 1);
        Method(ctx, map, "clear", (_, _) => { element.RemoveAttribute("style"); return JsValue.Undefined; }, 0);
        Accessor(ctx, map, "size", (_, _) => JintInterop.Num(InlineStyleEntries(element).Count));
        Method(ctx, map, "forEach", (_, a) =>
        {
            if (a.Length == 0 || !a[0].IsCallable())
            {
                return JsValue.Undefined;
            }

            foreach (var (name, value) in InlineStyleEntries(element))
            {
                a[0].Call(JsValue.Undefined, new[] { ParseCssValue(ctx, name, value), JintInterop.Str(name), map });
            }

            return JsValue.Undefined;
        }, 1);
        return map;
    }

    private static JsObject BuildComputedStyleMap(JintBackendContext ctx, Element element)
    {
        var engine = ctx.Engine;
        var host = ctx.LayoutHost;
        var map = new JsObject(engine);
        string Prop(JsValue[] a) => a.Length > 0 ? Str(a[0]).Trim().ToLowerInvariant() : "";
        string Resolve(string p) => host?.GetComputedProperty(element, p) ?? "";

        Method(ctx, map, "get", (_, a) =>
        {
            if (a.Length == 0)
            {
                return JsValue.Undefined;
            }

            var v = Resolve(Prop(a));
            return string.IsNullOrEmpty(v) ? JsValue.Undefined : ParseCssValue(ctx, Prop(a), v);
        }, 1);
        Method(ctx, map, "getAll", (_, a) =>
        {
            if (a.Length == 0)
            {
                return EmptyArray(engine);
            }

            var v = Resolve(Prop(a));
            return string.IsNullOrEmpty(v) ? EmptyArray(engine) : new JsArray(engine, new[] { ParseCssValue(ctx, Prop(a), v) });
        }, 1);
        Method(ctx, map, "has", (_, a) => JintInterop.Bool(a.Length > 0 && !string.IsNullOrEmpty(Resolve(Prop(a)))), 1);
        Accessor(ctx, map, "size", (_, _) => JintInterop.Num(ComputedEntries(host, element).Count));
        Method(ctx, map, "forEach", (_, a) =>
        {
            if (a.Length == 0 || !a[0].IsCallable())
            {
                return JsValue.Undefined;
            }

            foreach (var (name, value) in ComputedEntries(host, element))
            {
                a[0].Call(JsValue.Undefined, new[] { ParseCssValue(ctx, name, value), JintInterop.Str(name), map });
            }

            return JsValue.Undefined;
        }, 1);
        return map;
    }

    private static List<(string Name, string Value)> ComputedEntries(global::Starling.Bindings.ILayoutHost? host, Element element)
    {
        var list = new List<(string, string)>();
        if (host is null)
        {
            return list;
        }

        foreach (var prop in StyleProperties)
        {
            var v = host.GetComputedProperty(element, prop);
            if (!string.IsNullOrEmpty(v))
            {
                list.Add((prop, v));
            }
        }
        return list;
    }

    // Coerce a CSSStyleValue (or string) argument to its CSS text.
    private static string CoerceCss(JsValue v) => v.IsString() ? v.AsString() : v.ToString();

    private static string ReadStyleProp(Element element, string kebabProp)
    {
        var style = element.GetAttribute("style");
        if (string.IsNullOrEmpty(style))
        {
            return "";
        }

        foreach (var decl in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = decl.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            if (decl[..colon].Trim().Equals(kebabProp, StringComparison.OrdinalIgnoreCase))
            {
                return decl[(colon + 1)..].Trim();
            }
        }
        return "";
    }

    private static void WriteStyleProp(Element element, string kebabProp, string? value)
    {
        var style = element.GetAttribute("style") ?? "";
        var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var decl in style.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var colon = decl.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            pairs[decl[..colon].Trim().ToLowerInvariant()] = decl[(colon + 1)..].Trim();
        }
        if (string.IsNullOrEmpty(value))
        {
            pairs.Remove(kebabProp);
        }
        else
        {
            pairs[kebabProp] = value;
        }

        if (pairs.Count == 0)
        {
            element.RemoveAttribute("style");
        }
        else
        {
            element.SetAttribute("style", string.Join("; ", pairs.Select(p => $"{p.Key}: {p.Value}")));
        }
    }

    /// <summary>The inline-style property names exposed as camel/kebab accessors,
    /// shared with <see cref="CssomBinding"/> for CSSOM declaration blocks.</summary>
    internal static IReadOnlyList<string> StylePropertyNames => StyleProperties;

    /// <summary>Public kebab→camel conversion, shared with CSSOM.</summary>
    internal static string KebabToCamelPublic(string kebab) => KebabToCamel(kebab);

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

    // =====================================================================
    //                         Jint plumbing helpers
    // =====================================================================
    private static JsObject NewProto(global::Jint.Engine engine, ObjectInstance? parent)
    {
        var proto = new JsObject(engine);
        if (parent is not null)
        {
            proto.Prototype = parent;
        }

        return proto;
    }

    private static void Accessor(JintBackendContext ctx, ObjectInstance proto, string name,
        Func<JsValue, JsValue[], JsValue> getter, Func<JsValue, JsValue[], JsValue>? setter = null)
        => JintInterop.DefineAccessor(ctx.Engine, proto, name, getter, setter);

    private static void Method(JintBackendContext ctx, ObjectInstance proto, string name,
        Func<JsValue, JsValue[], JsValue> body, int length)
        => JintInterop.DefineMethod(ctx.Engine, proto, name, body, length);

    private static JsValue Cached(ObjectInstance wrapper, string key, Func<ObjectInstance> build)
    {
        var existing = wrapper.Get(key);
        if (!existing.IsUndefined())
        {
            return existing;
        }

        var obj = build();
        // Hidden cache slot: non-enumerable so it doesn't leak into for-in.
        JintInterop.DefineDataProp(wrapper, key, obj, writable: true, enumerable: false, configurable: true);
        return obj;
    }

    /// <summary>Build a DOMImplementation JS object (DOM §4.5). Exposes
    /// <c>createHTMLDocument([title])</c>, <c>createDocumentFragment()</c>, and
    /// <c>hasFeature()</c> (always true). The returned document is wrapped with
    /// the Document prototype so all Document methods work on it. Mirrors the
    /// Starling backend's <c>NodeBindings.BuildDomImplementation</c>.</summary>
    private static JsObject BuildDomImplementation(JintBackendContext ctx)
    {
        var engine = ctx.Engine;
        var w = ctx.Wrappers;
        var impl = new JsObject(engine);

        JintInterop.DefineMethod(engine, impl, "createHTMLDocument", (_, args) =>
        {
            // title arg: absent → null (no <title>); present (even "") → create one.
            string? title = args.Length > 0 && !args[0].IsUndefined() ? Str(args[0]) : null;
            return w.Wrap(Document.CreateHtmlDocument(title));
        }, 1);

        JintInterop.DefineMethod(engine, impl, "createDocumentFragment",
            (_, _) => w.Wrap(new Document().CreateDocumentFragment()), 0);

        JintInterop.DefineMethod(engine, impl, "hasFeature", (_, _) => JsBoolean.True, 0);

        return impl;
    }

    private static JsArray Array(global::Jint.Engine engine, IReadOnlyList<JsValue> items)
        => new(engine, items as JsValue[] ?? items.ToArray());

    private static JsArray EmptyArray(global::Jint.Engine engine) => new(engine, System.Array.Empty<JsValue>());

    // =====================================================================
    //                          layout readback
    // =====================================================================
    /// <summary>The session's layout host, or null when none was supplied (bare
    /// unit-test contexts).</summary>
    private static ILayoutHost? LayoutHost(JintBackendContext ctx) => ctx.LayoutHost;

    /// <summary>If <paramref name="e"/> is a <c>&lt;script&gt;</c> and the mutated
    /// attribute is <c>src</c> with a non-empty value, notify the session via
    /// <see cref="JintBackendContext.OnScriptSrcSet"/> so it can run
    /// HTML §4.12.1 "prepare a script" (fetch + execute + fire load/error).
    /// Scoped strictly to script-element <c>src</c>; every other attribute write
    /// is untouched. No-op when no session installed the hook (bare context).</summary>
    private static void MaybeTriggerScriptSrc(JintBackendContext ctx, Element e, string attrName)
    {
        if (!attrName.Equals("src", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!e.LocalName.Equals("script", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(e.GetAttribute("src")))
        {
            return;
        }

        ctx.OnScriptSrcSet?.Invoke(e);
    }

    private static JsValue ReadOffsetMetric(JintBackendContext ctx, JsValue thisV, Func<OffsetMetrics, double> select)
    {
        if (ctx.Wrappers.UnwrapElement(thisV) is { } e && LayoutHost(ctx) is { } host &&
            host.TryGetOffsetMetrics(e, out var m))
        {
            return JintInterop.Num(select(m));
        }
        return JintInterop.Num(0);
    }

    /// <summary>Build a DOMRect-shaped JS object from a snapshot rect. A duck-
    /// compatible bag (x/y/width/height/top/right/bottom/left + toJSON) covering
    /// the read-only paths every layout-readback test touches.</summary>
    private static JsObject BuildDomRect(global::Jint.Engine engine, LayoutRect rect)
    {
        var o = new JsObject(engine);
        JintInterop.DefineDataProp(o, "x", JintInterop.Num(rect.X));
        JintInterop.DefineDataProp(o, "y", JintInterop.Num(rect.Y));
        JintInterop.DefineDataProp(o, "width", JintInterop.Num(rect.Width));
        JintInterop.DefineDataProp(o, "height", JintInterop.Num(rect.Height));
        JintInterop.DefineDataProp(o, "top", JintInterop.Num(rect.Top));
        JintInterop.DefineDataProp(o, "right", JintInterop.Num(rect.Right));
        JintInterop.DefineDataProp(o, "bottom", JintInterop.Num(rect.Bottom));
        JintInterop.DefineDataProp(o, "left", JintInterop.Num(rect.Left));
        JintInterop.DefineMethod(engine, o, "toJSON", (self, _) =>
        {
            var json = new JsObject(engine);
            if (self is ObjectInstance src)
            {
                foreach (var k in new[] { "x", "y", "width", "height", "top", "right", "bottom", "left" })
                {
                    JintInterop.DefineDataProp(json, k, src.Get(k));
                }
            }

            return json;
        }, 0);
        return o;
    }

    private static JsObject BuildValidityObject(global::Jint.Engine engine, FormValidityState validity)
    {
        var o = new JsObject(engine);
        JintInterop.DefineDataProp(o, "valueMissing", JintInterop.Bool(validity.ValueMissing), writable: false);
        JintInterop.DefineDataProp(o, "typeMismatch", JintInterop.Bool(validity.TypeMismatch), writable: false);
        JintInterop.DefineDataProp(o, "patternMismatch", JintInterop.Bool(validity.PatternMismatch), writable: false);
        JintInterop.DefineDataProp(o, "tooLong", JintInterop.Bool(validity.TooLong), writable: false);
        JintInterop.DefineDataProp(o, "tooShort", JintInterop.Bool(validity.TooShort), writable: false);
        JintInterop.DefineDataProp(o, "rangeUnderflow", JintInterop.Bool(validity.RangeUnderflow), writable: false);
        JintInterop.DefineDataProp(o, "rangeOverflow", JintInterop.Bool(validity.RangeOverflow), writable: false);
        JintInterop.DefineDataProp(o, "stepMismatch", JintInterop.Bool(validity.StepMismatch), writable: false);
        JintInterop.DefineDataProp(o, "badInput", JintInterop.Bool(validity.BadInput), writable: false);
        JintInterop.DefineDataProp(o, "customError", JintInterop.Bool(validity.CustomError), writable: false);
        JintInterop.DefineDataProp(o, "valid", JintInterop.Bool(validity.Valid), writable: false);
        return o;
    }

    private static JsArray StringArray(global::Jint.Engine engine, IReadOnlyList<string> values)
    {
        var items = new JsValue[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            items[i] = JintInterop.Str(values[i]);
        }

        return new JsArray(engine, items);
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

    private const string FormDataEntriesHook = "__starlingFormDataEntries";

    private static void InstallFormDataConstructor(JintBackendContext ctx)
    {
        var engine = ctx.Engine;
        JintInterop.DefineDataProp(engine.Global, FormDataEntriesHook,
            new ClrFunction(engine, FormDataEntriesHook, (_, args) =>
            {
                if (args.Length == 0 || ctx.Wrappers.UnwrapElement(args[0]) is not { LocalName: "form" } form)
                {
                    return new JsArray(engine, System.Array.Empty<JsValue>());
                }

                var entries = HtmlFormControls.ConstructEntryList(form);
                var pairs = new JsValue[entries.Count];
                for (var i = 0; i < entries.Count; i++)
                {
                    pairs[i] = new JsArray(engine, new[] { JintInterop.Str(entries[i].Name), JintInterop.Str(entries[i].Value) });
                }

                return new JsArray(engine, pairs);
            }, 1),
            writable: false, enumerable: false, configurable: true);
        // The complete FormData class (append/set/delete/getAll/has/forEach/keys/
        // values/iterator + Blob/File values) is installed by
        // BlobFileFormDataBinding; it reuses the __starlingFormDataEntries hook above
        // for `new FormData(formElement)`.
    }

    // HTML §4.8.5 — `new Image(width?, height?)` constructs a detached
    // HTMLImageElement (an <img> not yet in any document). Pages use it to
    // preload sprites and to feature-test image support; a missing `Image`
    // global throws ReferenceError and aborts the surrounding init (McMaster's
    // Shell_InitContent_Loaded). Jint's ClrFunction isn't constructable, so we
    // install a real JS function whose body returns a freshly wrapped <img>:
    // a constructor that returns an object yields that object from `new`.
    private const string ImageHook = "__starlingCreateImage";

    private static void InstallImageConstructor(JintBackendContext ctx)
    {
        var engine = ctx.Engine;
        JintInterop.DefineDataProp(engine.Global, ImageHook,
            new ClrFunction(engine, ImageHook, (_, a) =>
            {
                var img = ctx.Document.CreateElement("img");
                if (a.Length > 0 && !a[0].IsUndefined())
                {
                    img.SetAttribute("width", ((int)Num(a[0])).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                if (a.Length > 1 && !a[1].IsUndefined())
                {
                    img.SetAttribute("height", ((int)Num(a[1])).ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                return ctx.Wrappers.Wrap(img);
            }, 2),
            writable: false, enumerable: false, configurable: true);

        var ctor = (ObjectInstance)engine.Evaluate(
            $"(function Image(width, height){{ return {ImageHook}(width, height); }})");
        Global(engine, "Image", ctor);
    }

    private static ClrFunction MakeCtor(global::Jint.Engine engine, string name, ObjectInstance proto)
    {
        var ctor = new ClrFunction(engine, name,
            (_, _) => throw TypeError(engine, "Illegal constructor"), 0, PropertyFlag.Configurable);
        // ClrFunction shadows a data `prototype` set via FastSetProperty, so the
        // OrdinaryHasInstance path reads `undefined`; assigning via Set installs a
        // readable own slot so `node instanceof Element` resolves correctly.
        ctor.Set("prototype", proto);
        JintInterop.DefineDataProp(proto, "constructor", ctor, writable: true, enumerable: false, configurable: true);
        return ctor;
    }

    private static void Global(global::Jint.Engine engine, string name, JsValue value)
        => JintInterop.DefineDataProp(engine.Global, name, value, writable: true, enumerable: false, configurable: true);

    // Elements that carry a `value` IDL attribute (HTML §4.10). `select` is
    // included even though its real value reflects the selected option — for a
    // statically-rendered page reflecting the value attribute is close enough
    // and, crucially, never undefined.
    private static bool IsFormControl(Element e) => e.LocalName.ToLowerInvariant() switch
    {
        "input" or "textarea" or "select" or "option" or "button"
            or "li" or "meter" or "progress" or "param" or "data" => true,
        _ => false,
    };

    private static bool IsTextArea(Element e)
        => string.Equals(e.LocalName, "textarea", StringComparison.OrdinalIgnoreCase);

    private static void DefineNodeConstants(ObjectInstance target)
    {
        void C(string n, int v) => JintInterop.DefineDataProp(target, n, JintInterop.Num(v),
            writable: false, enumerable: true, configurable: false);
        C("ELEMENT_NODE", 1);
        C("ATTRIBUTE_NODE", 2);
        C("TEXT_NODE", 3);
        C("CDATA_SECTION_NODE", 4);
        C("PROCESSING_INSTRUCTION_NODE", 7);
        C("COMMENT_NODE", 8);
        C("DOCUMENT_NODE", 9);
        C("DOCUMENT_TYPE_NODE", 10);
        C("DOCUMENT_FRAGMENT_NODE", 11);
        // DOM §4.4.4 compareDocumentPosition bitmask constants.
        C("DOCUMENT_POSITION_DISCONNECTED", 1);
        C("DOCUMENT_POSITION_PRECEDING", 2);
        C("DOCUMENT_POSITION_FOLLOWING", 4);
        C("DOCUMENT_POSITION_CONTAINS", 8);
        C("DOCUMENT_POSITION_CONTAINED_BY", 16);
        C("DOCUMENT_POSITION_IMPLEMENTATION_SPECIFIC", 32);
    }

    private static JavaScriptException TypeError(global::Jint.Engine engine, string message)
        => new(engine.Intrinsics.TypeError.Construct(message));

    private static JavaScriptException SyntaxError(global::Jint.Engine engine, string message)
        => new(engine.Construct("SyntaxError", new JsValue[] { JintInterop.Str(message) }));

    // ---- value coercion -------------------------------------------------
    private static JsValue Arg(JsValue[] args, int i) => i < args.Length ? args[i] : JsValue.Undefined;
    private static string Str(JsValue v) => TypeConverter.ToString(v);
    private static bool Bool(JsValue v) => TypeConverter.ToBoolean(v);
    private static double Num(JsValue v) => TypeConverter.ToNumber(v);

    // Comprehensive list of CSS properties exposed via element.style.X (mirrors
    // the Starling.Js binding's set so JS that touches inline styles works).
    private static readonly string[] StyleProperties =
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

    // element.style exposes every registered CSS property (curated common names ∪
    // the full PropertyRegistry) so uncommon longhands read "" instead of undefined,
    // matching the canonical backend's AllInlineStyleProperties.
    private static readonly string[] AllStyleProperties =
        StyleProperties
            .Concat(Starling.Css.Properties.PropertyRegistry.All.Select(Starling.Css.Properties.PropertyRegistry.Name))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

/// <summary>
/// Exotic JS object backing <c>element.dataset</c> (HTML 2.7.3 DOMStringMap),
/// mirroring <c>Starling.Bindings.JsDatasetObject</c> but over Jint's
/// <see cref="ObjectInstance"/> property model. Property reads/writes delegate to
/// the element's <c>data-*</c> attributes; camelCase property maps to <c>data-</c>
/// + kebab-case. (<see cref="JsObject"/> is sealed, so this derives from the
/// overridable <see cref="ObjectInstance"/> base.)
/// </summary>
internal sealed class JintDatasetObject : ObjectInstance
{
    private readonly Element _element;

    public JintDatasetObject(global::Jint.Engine engine, Element element) : base(engine)
        => _element = element;

    private static string PropToAttr(string name)
    {
        var sb = new System.Text.StringBuilder("data-");
        foreach (var c in name)
        {
            if (char.IsUpper(c)) { sb.Append('-'); sb.Append(char.ToLowerInvariant(c)); }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string AttrToProp(string attr)
    {
        if (!attr.StartsWith("data-", StringComparison.OrdinalIgnoreCase))
        {
            return attr;
        }

        var kebab = attr[5..];
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

    public override JsValue Get(JsValue property, JsValue receiver)
    {
        if (property.IsString())
        {
            var attr = _element.GetAttribute(PropToAttr(property.AsString()));
            if (attr is not null)
            {
                return JintInterop.Str(attr);
            }
        }
        return base.Get(property, receiver);
    }

    public override bool Set(JsValue property, JsValue value, JsValue receiver)
    {
        if (property.IsString())
        {
            _element.SetAttribute(PropToAttr(property.AsString()), TypeConverter.ToString(value));
            return true;
        }
        return base.Set(property, value, receiver);
    }

    public override bool HasProperty(JsValue property)
    {
        if (property.IsString() && _element.HasAttribute(PropToAttr(property.AsString())))
        {
            return true;
        }

        return base.HasProperty(property);
    }

    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        if (property.IsString())
        {
            var attr = _element.GetAttribute(PropToAttr(property.AsString()));
            if (attr is not null)
            {
                return new PropertyDescriptor(JintInterop.Str(attr), writable: true, enumerable: true, configurable: true);
            }
        }
        return base.GetOwnProperty(property);
    }

    public override bool Delete(JsValue property)
    {
        if (property.IsString())
        {
            var attr = PropToAttr(property.AsString());
            if (_element.HasAttribute(attr)) { _element.RemoveAttribute(attr); return true; }
        }
        return base.Delete(property);
    }

    public override List<JsValue> GetOwnPropertyKeys(Types types = Types.String | Types.Symbol)
    {
        var keys = new List<JsValue>();
        if ((types & Types.String) != 0)
        {
            foreach (var a in _element.Attributes)
            {
                if (a.Name.StartsWith("data-", StringComparison.OrdinalIgnoreCase))
                {
                    keys.Add(JintInterop.Str(AttrToProp(a.Name)));
                }
            }
        }

        keys.AddRange(base.GetOwnPropertyKeys(types));
        return keys;
    }
}

internal static partial class NodeBindingsLog
{
    [LoggerMessage(Level = LogLevel.Debug,
        Message = "classList.{Operation} rejected invalid token: {Reason}")]
    public static partial void ClassListInvalidToken(ILogger logger, string operation, string reason);
}
