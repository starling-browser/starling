// SPDX-License-Identifier: Apache-2.0
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AsHtmlParser = AngleSharp.Html.Parser.HtmlParser;
using StarlingDom = Starling.Dom;

namespace Starling.Html.AngleSharp;

/// <summary>
/// An <see cref="IHtmlParserBackend"/> that parses with AngleSharp and copies the
/// resulting tree into a Starling <see cref="StarlingDom.Document"/>. AngleSharp
/// never escapes this project: it parses to its own DOM, and the walker below
/// rebuilds an ordinary Starling DOM through the public construction API
/// (<c>CreateElement</c> / <c>CreateElementNS</c>, <c>CreateTextNode</c>, etc.).
/// </summary>
/// <remarks>
/// The rebuilt tree is a normal Starling DOM. Nodes are built fully detached and
/// only the top-level nodes are appended to the Starling <c>Document</c>, exactly
/// like the Starling parser's output. So when the engine later walks the tree for
/// <c>NodeConnected</c> / script discovery, nothing here changes that walk.
/// </remarks>
public sealed class AngleSharpHtmlBackend : IHtmlParserBackend
{
    /// <inheritdoc/>
    public string Name => "anglesharp";

    /// <inheritdoc/>
    public StarlingDom.Document Parse(string html, bool scriptingEnabled)
    {
        // scriptingEnabled is intentionally ignored for tree shape: AngleSharp does
        // not run scripts by default, so the parsed tree shape is the same either
        // way. The engine still runs discovered scripts after the copy, just as it
        // does for the Starling parser's output.
        ArgumentNullException.ThrowIfNull(html);

        var source = new AsHtmlParser().ParseDocument(html);
        var doc = new StarlingDom.Document { IsHtml = true };

        // Locked decision 1: do NOT set Document.Mode/quirks. The copied document
        // stays at its default; nothing in CSS or layout reads compatMode here.

        // Walk every child of the AngleSharp document: doctype, the <html> root,
        // and any comments that sit before or after it.
        foreach (var child in source.ChildNodes)
        {
            var copied = CopyNode(child, doc);
            if (copied is not null)
            {
                doc.AppendChild(copied);
            }
        }

        return doc;
    }

    /// <inheritdoc/>
    public StarlingDom.DocumentFragment ParseFragment(string markup, StarlingDom.Element context,
        StarlingDom.Document ownerDocument)
    {
        ArgumentNullException.ThrowIfNull(markup);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(ownerDocument);

        // AngleSharp fragment parsing needs an AngleSharp IElement context that
        // carries the same tag name and namespace as the Starling context element.
        // Build a throwaway AngleSharp document and create that context element on
        // it, then run HtmlParser.ParseFragment(string, IElement) — the exact 1.4
        // fragment API. It returns an INodeList of the parsed nodes.
        var asDoc = new AsHtmlParser().ParseDocument(string.Empty);
        var contextQualified = context.Prefix is { Length: > 0 } p
            ? p + ":" + context.LocalName
            : context.LocalName;
        var asContext = asDoc.CreateElement(context.Namespace, contextQualified);

        var fragment = ownerDocument.CreateDocumentFragment();
        var nodes = new AsHtmlParser().ParseFragment(markup, asContext);
        foreach (var node in nodes)
        {
            var copied = CopyNode(node, ownerDocument);
            if (copied is not null)
            {
                fragment.AppendChild(copied);
            }
        }

        return fragment;
    }

    // Deep-copies a single AngleSharp node (and its subtree) into a detached
    // Starling node owned by <paramref name="owner"/>. Returns null for node kinds
    // the Starling DOM does not model.
    private static StarlingDom.Node? CopyNode(INode node, StarlingDom.Document owner)
    {
        switch (node.NodeType)
        {
            case NodeType.Element:
                return CopyElement((IElement)node, owner);

            case NodeType.Text:
                return owner.CreateTextNode(((IText)node).Data);

            case NodeType.Comment:
                return owner.CreateComment(((IComment)node).Data);

            case NodeType.CharacterData:
                // AngleSharp surfaces CDATA sections as ICharacterData with this type.
                return owner.CreateCDataSection(((ICharacterData)node).Data);

            case NodeType.ProcessingInstruction:
                var pi = (IProcessingInstruction)node;
                return owner.CreateProcessingInstruction(pi.Target, pi.Data);

            case NodeType.DocumentType:
                var dt = (IDocumentType)node;
                return owner.CreateDocumentType(dt.Name, dt.PublicIdentifier ?? string.Empty, dt.SystemIdentifier ?? string.Empty);

            default:
                // Document / DocumentFragment / Attribute and friends never appear
                // as walkable children here; skip anything the Starling DOM lacks.
                return null;
        }
    }

    private static StarlingDom.Element CopyElement(IElement source, StarlingDom.Document owner)
    {
        var element = CreateElement(source, owner);
        CopyAttributes(source, element);

        if (source is IHtmlTemplateElement template)
        {
            // <template> children live in a separate content fragment, not as normal
            // children. Copy AngleSharp's template.Content into the Starling
            // template element's own Content fragment.
            var content = ((StarlingDom.HtmlTemplateElement)element).Content;
            foreach (var child in template.Content.ChildNodes)
            {
                var copied = CopyNode(child, owner);
                if (copied is not null)
                {
                    content.AppendChild(copied);
                }
            }
            return element;
        }

        foreach (var child in source.ChildNodes)
        {
            var copied = CopyNode(child, owner);
            if (copied is not null)
            {
                element.AppendChild(copied);
            }
        }

        return element;
    }

    private static StarlingDom.Element CreateElement(IElement source, StarlingDom.Document owner)
    {
        var ns = source.NamespaceUri;

        // Plain HTML elements (HTML namespace, no prefix): use CreateElement with the
        // lowercase local name so TagName comes out lowercased, matching the Starling
        // parser. AngleSharp reports an uppercase TagName for HTML elements, so we
        // must pass LocalName, not TagName.
        if ((string.IsNullOrEmpty(ns) || ns == StarlingDom.Element.HtmlNamespace)
            && string.IsNullOrEmpty(source.Prefix))
        {
            return owner.CreateElement(source.LocalName);
        }

        // SVG, MathML, or any prefixed element: preserve case and prefix via the
        // namespace-aware factory.
        var qualified = source.Prefix is { Length: > 0 } p
            ? p + ":" + source.LocalName
            : source.LocalName;
        return owner.CreateElementNS(ns, qualified);
    }

    // The HTML parser's adjusted-attribute table (§13.2.6.5): the fixed prefixes
    // the tree builder assigns to foreign namespaced attributes. AngleSharp reports
    // these attributes with the namespace but an empty Prefix and a bare local Name
    // (e.g. xlink:href arrives as Name="href", Ns=xlink, Prefix=""), so we rebuild
    // the qualified name here to match the Starling parser's stored form.
    private static string? PrefixForNamespace(string? ns) => ns switch
    {
        "http://www.w3.org/1999/xlink" => "xlink",
        "http://www.w3.org/XML/1998/namespace" => "xml",
        "http://www.w3.org/2000/xmlns/" => "xmlns",
        _ => null,
    };

    private static void CopyAttributes(IElement source, StarlingDom.Element target)
    {
        foreach (var attr in source.Attributes)
        {
            if (string.IsNullOrEmpty(attr.NamespaceUri))
            {
                // Plain attribute (no namespace). SetAttribute lowercases the name,
                // matching the Starling parser for HTML attributes.
                target.SetAttribute(attr.Name, attr.Value);
                continue;
            }

            // Namespaced attribute (xlink:href, xml:lang, …): rebuild the qualified
            // name with its conventional prefix and keep the namespace via
            // SetAttributeNS. AngleSharp keeps the attribute's local-name case but
            // does not surface the prefix, so prefer its own Prefix when present and
            // fall back to the standard prefix for the namespace.
            var prefix = attr.Prefix is { Length: > 0 } pfx ? pfx : PrefixForNamespace(attr.NamespaceUri);
            var qualified = prefix is { Length: > 0 } p ? p + ":" + attr.LocalName : attr.Name;
            target.SetAttributeNS(attr.NamespaceUri, qualified, attr.Value);
        }
    }
}
