namespace Starling.Dom;

/// <summary>
/// DOM §4.9 Attr — an attribute as a Node. Carries name, localName,
/// prefix, namespaceURI, and a mutable value. When attached to an element
/// (<see cref="OwnerElement"/> is non-null) writing <see cref="Value"/> also
/// updates the element's attribute storage so the two stay in sync.
/// </summary>
/// <remarks>
/// The "Attr is a Node" rule was reintroduced in the WHATWG DOM Living Standard;
/// Attr nodes are NOT children of their owner element — they live in the element's
/// <see cref="NamedNodeMap"/> attribute list and cannot be inserted into the
/// normal child-node tree (insertBefore / appendChild must throw HierarchyRequestError
/// for Attr arguments — enforced at the binding layer).
/// </remarks>
public sealed class AttrNode : Node
{
    private string _value;

    // ---- Construction -------------------------------------------------------

    /// <summary>Create a detached attribute with no namespace.
    /// The caller is responsible for lower-casing the name for HTML attributes.
    /// For this non-namespaced path the full name IS the local name — no prefix
    /// splitting is applied (per WHATWG DOM createAttribute semantics).</summary>
    public AttrNode(string name, string value = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        Name = name;
        // createAttribute: the localName is the entire name (no prefix split).
        // Colon is not treated as a namespace separator in this path.
        LocalName = name;
        Prefix = null;
        Namespace = null;
        _value = value;
    }

    // Private full constructor — used by CreateNamespaced and Clone.
    private AttrNode(string name, string? @namespace, string value)
    {
        Name = name;
        LocalName = LocalNameOf(name);
        Prefix = PrefixOf(name);
        Namespace = @namespace;
        _value = value;
    }

    /// <summary>Create a namespace-aware attribute (createAttributeNS path).
    /// Uses a named factory instead of a public constructor to avoid overload
    /// ambiguity between <c>string value</c> and <c>string? namespace</c>.</summary>
    internal static AttrNode CreateNamespaced(string qualifiedName, string? @namespace, string value = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(qualifiedName);
        var ns = string.IsNullOrEmpty(@namespace) ? null : @namespace;
        return new AttrNode(qualifiedName, ns, value);
    }

    // ---- DOM §4.9 interface -------------------------------------------------

    public override NodeKind Kind => NodeKind.Attribute;

    /// <summary>The qualified name: "prefix:localName", or just "localName" when there is no prefix.</summary>
    public string Name { get; }

    public string LocalName { get; }

    /// <summary>The prefix part of the qualified name, or null when the name has no colon.</summary>
    public string? Prefix { get; }

    /// <summary>The namespace URI, or null when the attribute has no namespace.</summary>
    public string? Namespace { get; }

    /// <summary>DOM §4.9 — specified is always true for Attr nodes.</summary>
    public bool Specified => true;

    /// <summary>The element this attribute is attached to, or null when detached.</summary>
    public Element? OwnerElement { get; internal set; }

    /// <summary>
    /// The attribute value. Setting this on an attached attribute propagates the
    /// change into the owner element's attribute storage (NamedNodeMap) so both
    /// the Attr node and the element's attribute table stay in sync.
    /// </summary>
    public string Value
    {
        get => _value;
        set
        {
            var newValue = value ?? "";
            if (_value == newValue)
            {
                return;
            }

            var oldValue = _value;
            _value = newValue;
            // Propagate to the element's attribute storage so getAttribute()
            // and the NamedNodeMap are consistent.
            OwnerElement?.SyncAttrNodeValue(this, newValue, oldValue);
        }
    }

    // ---- Node overrides -----------------------------------------------------

    public override string NodeName => Name;

    public override string? NodeValue
    {
        get => _value;
        set => Value = value ?? "";
    }

    public override string TextContent
    {
        get => _value;
        set => Value = value ?? "";
    }

    // ---- Helpers ------------------------------------------------------------

    private static string LocalNameOf(string qualifiedName)
    {
        var i = qualifiedName.IndexOf(':', StringComparison.Ordinal);
        return i >= 0 ? qualifiedName[(i + 1)..] : qualifiedName;
    }

    private static string? PrefixOf(string qualifiedName)
    {
        var i = qualifiedName.IndexOf(':', StringComparison.Ordinal);
        return i >= 0 ? qualifiedName[..i] : null;
    }

    /// <summary>Create a detached copy of this Attr node (for cloneNode / SetNamedItemNS copying).</summary>
    public AttrNode Clone() => new AttrNode(Name, Namespace, _value);
}
