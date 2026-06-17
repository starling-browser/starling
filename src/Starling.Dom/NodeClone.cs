namespace Starling.Dom;

/// <summary>
/// DOM §4.4.4 cloneNode primitives, lifted to <see cref="Starling.Dom"/> so
/// algorithms in this project (Range cloneContents / extractContents) can
/// duplicate subtrees without reaching into the binding layer.
/// </summary>
/// <remarks>
/// The JS-visible <c>cloneNode</c> in <c>Starling.Bindings</c> still owns the
/// wrapper-cache / prototype routing — that's where the JS shape lives.
/// What this class adds is the engine-internal clone walk for host-side
/// operations.
/// </remarks>
public static class NodeClone
{
    /// <summary>Shallow clone — copy node identity (tag/data/etc.) but not
    /// children. OwnerDocument is preserved.</summary>
    public static Node Shallow(Node source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var doc = source.OwnerDocument ?? (source as Document);
        return source switch
        {
            Element el when doc is not null => CloneElement(doc, el),
            Element el => new Element(el.TagName, el.Namespace),
            CData cd => doc?.CreateCDataSection(cd.Data) ?? new CData(cd.Data),
            Text t => doc?.CreateTextNode(t.Data) ?? new Text(t.Data),
            Comment c => doc?.CreateComment(c.Data) ?? new Comment(c.Data),
            ProcessingInstruction pi => doc?.CreateProcessingInstruction(pi.Target, pi.Data)
                ?? new ProcessingInstruction(pi.Target, pi.Data),
            DocumentFragment when doc is not null => doc.CreateDocumentFragment(),
            DocumentFragment => new DocumentFragment(),
            DocumentType dt => doc?.CreateDocumentType(dt.Name, dt.PublicId, dt.SystemId)
                ?? new DocumentType(dt.Name, dt.PublicId, dt.SystemId),
            _ => source, // unknown node types — pass through
        };
    }

    /// <summary>Deep clone — recursive subtree copy. Each descendant is
    /// itself cloned via <see cref="Shallow"/> and re-parented under the
    /// new top-level clone.</summary>
    public static Node Deep(Node source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var clone = Shallow(source);
        if (ReferenceEquals(clone, source))
        {
            return clone;
        }

        for (var c = source.FirstChild; c is not null; c = c.NextSibling)
        {
            clone.AppendChild(Deep(c));
        }

        return clone;
    }

    private static Element CloneElement(Document doc, Element el)
    {
        var clone = el.Prefix is not null || el.Namespace != Element.HtmlNamespace
            ? doc.CreateElementNS(el.Namespace, el.Prefix is null ? el.LocalName : el.Prefix + ":" + el.LocalName)
            : doc.CreateElement(el.LocalName);
        foreach (var attr in el.Attributes)
        {
            clone.Attributes.SetNamedItemNS(attr.Clone());
        }

        return clone;
    }
}
