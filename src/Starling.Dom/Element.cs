namespace Starling.Dom;

/// <summary>
/// Generic element. HTML-specific subclasses are deferred; this core element
/// provides attributes and tree behavior for parser/layout consumers.
/// </summary>
public class Element : Node
{
    public const string HtmlNamespace = "http://www.w3.org/1999/xhtml";
    private string? _inputValue;
    private string _customValidationMessage = string.Empty;

    public Element(string tagName, string? @namespace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tagName);
        LocalName = tagName.ToLowerInvariant();
        Namespace = @namespace ?? HtmlNamespace;
        TagName = LocalName;
        Attributes = new NamedNodeMap(this);
        ClassList = new DomTokenList(
            () => GetAttribute("class") ?? string.Empty,
            value => SetAttribute("class", value));
    }

    // Namespace-aware construction (createElementNS / createDocument): unlike the
    // HTML ctor above it preserves the qualified name's case and splits the
    // prefix, so prefix/localName/tagName and namespace lookups are correct.
    private Element(string qualifiedName, string localName, string? prefix, string @namespace)
    {
        ArgumentException.ThrowIfNullOrEmpty(qualifiedName);
        TagName = qualifiedName;
        LocalName = localName;
        Prefix = prefix;
        Namespace = @namespace;
        Attributes = new NamedNodeMap(this);
        ClassList = new DomTokenList(
            () => GetAttribute("class") ?? string.Empty,
            value => SetAttribute("class", value));
    }

    /// <summary>DOM §4.5 createElementNS construction: parse <paramref name="qualifiedName"/>
    /// into prefix + local name (case preserved). A null/empty namespace maps to
    /// the HTML namespace (this engine models Namespace as non-null).</summary>
    public static Element CreateNamespaced(string? @namespace, string qualifiedName)
    {
        ArgumentNullException.ThrowIfNull(qualifiedName);
        var i = qualifiedName.IndexOf(':', StringComparison.Ordinal);
        var prefix = i >= 0 ? qualifiedName[..i] : null;
        var local = i >= 0 ? qualifiedName[(i + 1)..] : qualifiedName;
        // A null/empty namespace stays the empty string (a null namespace per
        // DOM), not the HTML namespace — createElementNS(null, …) must report
        // namespaceURI === null.
        return new Element(qualifiedName, local, prefix, @namespace ?? "");
    }

    public override NodeKind Kind => NodeKind.Element;

    public override string NodeName => TagName;

    public string LocalName { get; }

    public string? Prefix { get; init; }

    public string Namespace { get; }

    /// <summary>Lower-cased tag name used by HTML parser and layout tests.</summary>
    public string TagName { get; }

    public NamedNodeMap Attributes { get; }

    public string Id
    {
        get => GetAttribute("id") ?? string.Empty;
        set => SetAttribute("id", value);
    }

    /// <summary>Reflects the <c>class</c> attribute. DOM §4.9 <c>className</c>.</summary>
    public string ClassName
    {
        get => GetAttribute("class") ?? string.Empty;
        set => SetAttribute("class", value);
    }

    public DomTokenList ClassList { get; }

    /// <summary>A live <see cref="DomTokenList"/> over an arbitrary reflected
    /// attribute (e.g. <c>rel</c> for <c>relList</c>, <c>sandbox</c>, <c>sizes</c>,
    /// <c>for</c> for an output's <c>htmlFor</c>). Reads/writes the attribute, so
    /// it stays in sync with direct attribute mutations.</summary>
    public DomTokenList TokenListFor(string attributeName)
    {
        ArgumentException.ThrowIfNullOrEmpty(attributeName);
        return new DomTokenList(
            () => GetAttribute(attributeName) ?? string.Empty,
            value => SetAttribute(attributeName, value));
    }

    /// <summary>
    /// The live IDL value of a form control (<c>&lt;input&gt;</c> /
    /// <c>&lt;textarea&gt;</c>) — the text the user has typed or that a script
    /// assigned via <c>element.value = …</c>. Null until the field is first
    /// edited; while null the <c>value</c> content attribute supplies the
    /// initial value. Layout (the synthesized label text) and the JS
    /// <c>.value</c> accessor read this in preference to the attribute so typed
    /// text and scripted assignments are reflected. Non-form elements ignore it.
    /// </summary>
    public string? InputValue
    {
        get => _inputValue;
        set
        {
            if (string.Equals(_inputValue, value, StringComparison.Ordinal))
            {
                return;
            }

            _inputValue = value;
            OnTreeMutated();
            // The synthesized label box reads InputValue, but unlike an attribute
            // write this is not a DOM mutation, so the incremental layout session
            // would drain an empty batch and reuse the stale input box (the typed
            // text never repaints). Record a layout mutation so the reconciler
            // rebuilds this element's box subtree — or falls back to a full
            // rebuild — and the new value reaches the box tree.
            OwnerDocument?.RecordLayoutMutation(this, LayoutChangeKind.LayoutRelevantAttr);
        }
    }

    public int SelectionStart { get; set; }

    public int SelectionEnd { get; set; }

    public string SelectionDirection { get; set; } = "none";

    public string CustomValidationMessage
    {
        get => _customValidationMessage;
        set => _customValidationMessage = value ?? string.Empty;
    }

    public string? GetAttribute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Attributes.GetNamedItem(name)?.Value;
    }

    public void SetAttribute(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        var lname = name.ToLowerInvariant();
        var existing = Attributes.GetNamedItem(lname);
        if (existing is not null)
        {
            existing.Value = value; // mutate in-place so AttrNode identity is preserved
        }
        else
        {
            Attributes.SetNamedItem(new AttrNode(lname, value));
        }
    }

    public bool HasAttribute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return Attributes.GetNamedItem(name) is not null;
    }

    public void RemoveAttribute(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        Attributes.RemoveNamedItem(name);
    }

    // ---- Namespace-aware attributes (DOM §4.9). setAttributeNS preserves the
    // qualified name's case (unlike HTML setAttribute, which lower-cases).

    public string? GetAttributeNS(string? @namespace, string localName)
    {
        ArgumentNullException.ThrowIfNull(localName);
        return Attributes.GetNamedItemNS(@namespace, localName)?.Value;
    }

    public void SetAttributeNS(string? @namespace, string qualifiedName, string value)
    {
        ArgumentNullException.ThrowIfNull(qualifiedName);
        ArgumentNullException.ThrowIfNull(value);
        var ns = string.IsNullOrEmpty(@namespace) ? null : @namespace;
        var existing = Attributes.GetNamedItemNS(ns, NamedNodeMap.LocalNameOf(qualifiedName));
        if (existing is not null)
        {
            existing.Value = value; // mutate in-place
        }
        else
        {
            Attributes.SetNamedItemNS(AttrNode.CreateNamespaced(qualifiedName, ns, value));
        }
    }

    public bool HasAttributeNS(string? @namespace, string localName)
    {
        ArgumentNullException.ThrowIfNull(localName);
        return Attributes.GetNamedItemNS(@namespace, localName) is not null;
    }

    public void RemoveAttributeNS(string? @namespace, string localName)
    {
        ArgumentNullException.ThrowIfNull(localName);
        Attributes.RemoveNamedItemNS(@namespace, localName);
    }

    /// <summary>DOM §4.9 getElementsByTagName over this element's descendants
    /// (live). "*" matches any local name.</summary>
    public IReadOnlyList<Element> GetElementsByTagName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var list = new List<Element>();
        foreach (var d in DescendantElements())
        {
            if (name == "*" || d.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(d);
            }
        }

        return list;
    }

    /// <summary>DOM §4.9 getElementsByClassName over this element's descendants.
    /// The caller re-evaluates for liveness.</summary>
    public IReadOnlyList<Element> GetElementsByClassName(string names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var classes = names.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var list = new List<Element>();
        if (classes.Length == 0)
        {
            return list;
        }

        foreach (var d in DescendantElements())
        {
            if (classes.All(d.ClassList.Contains))
            {
                list.Add(d);
            }
        }

        return list;
    }

    /// <summary>DOM §4.9 "hasAttributes".</summary>
    public bool HasAttributes() => Attributes.Count > 0;

    /// <summary>DOM §4.9 "getAttributeNames": the qualified attribute names.</summary>
    public IReadOnlyList<string> GetAttributeNames()
    {
        var names = new List<string>(Attributes.Count);
        foreach (var attr in Attributes)
        {
            names.Add(attr.Name);
        }

        return names;
    }

    /// <summary>DOM §4.9 "toggleAttribute(name, force?)": adds or removes a
    /// boolean attribute, returning whether it is now present.</summary>
    public bool ToggleAttribute(string name, bool? force)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (force is { } f)
        {
            if (f) { SetAttribute(name, ""); return true; }
            RemoveAttribute(name);
            return false;
        }
        if (HasAttribute(name)) { RemoveAttribute(name); return false; }
        SetAttribute(name, "");
        return true;
    }

    /// <summary>DOM §4.9 getElementsByTagNameNS over this element's descendants.
    /// "*" matches any namespace / any local name.</summary>
    public IEnumerable<Element> GetElementsByTagNameNS(string? @namespace, string localName)
    {
        ArgumentNullException.ThrowIfNull(localName);
        var anyNs = @namespace == "*";
        var anyLocal = localName == "*";
        var ns = string.IsNullOrEmpty(@namespace) ? null : @namespace;
        foreach (var d in DescendantElements())
        {
            if (!anyNs && !string.Equals(d.Namespace, ns, StringComparison.Ordinal))
            {
                continue;
            }

            if (!anyLocal && !d.LocalName.Equals(localName, StringComparison.Ordinal))
            {
                continue;
            }

            yield return d;
        }
    }

    public override string ToString() => $"<{TagName}>";

    internal void OnAttributeMutated(string attrName, string? oldValue = null)
    {
        if (OwnerDocument is { } d)
        {
            // Notify mutation observers (DOM §4.3) with the pre-change value.
            d.AttributeMutated?.Invoke(this, attrName, oldValue);
            d.NoteAttributeMutation(attrName);
            // Always advance MutationVersion — observers, PumpFrame, and live
            // collections rely on "any DOM change advances this counter". But
            // LayoutInvalidationVersion only advances for attributes that the
            // built-in cascade actually depends on. See
            // Document.IsLayoutRelevantAttribute for the trade-off.
            d.BumpMutationVersion();
            // Selector-aware (plan §7): relevant if the static heuristic says so,
            // OR some active stylesheet selects on this attribute (so author CSS
            // keyed on a data-*/aria-* attribute still invalidates on a script write).
            if (d.IsAttributeLayoutRelevant(attrName))
            {
                d.BumpLayoutInvalidationVersion();
                d.RecordLayoutMutation(this, LayoutChangeKind.LayoutRelevantAttr);
            }
            return;
        }
        // Detached element — fall back to the parent walk in OnTreeMutated.
        OnTreeMutated();
    }

    /// <summary>Called by <see cref="AttrNode.Value"/> setter to propagate a
    /// value change on an attached attribute node back into any dependent state
    /// (mutation observers, style engine invalidation, etc.). Currently just
    /// bumps the mutation version via OnAttributeMutated.</summary>
    internal void SyncAttrNodeValue(AttrNode attr, string newValue, string? oldValue = null)
    {
        // The AttrNode._value field is already updated by the caller.
        // Fire the same mutation hook that setAttribute fires so all observers
        // (cascade invalidation, mutation records) are notified.
        OnAttributeMutated(attr.Name, oldValue);
    }
}
