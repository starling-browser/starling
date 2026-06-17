using System.Collections;

namespace Starling.Dom;

/// <summary>
/// DOM §4.9 NamedNodeMap — the live attribute-node collection of an Element.
/// Each entry is an <see cref="AttrNode"/>; the collection is keyed by
/// (namespace, local-name) for namespace-aware operations and by qualified name
/// for the non-namespaced HTML path.
/// </summary>
/// <remarks>
/// The internal store is a <see cref="List{AttrNode}"/> so random-access by
/// index (element.attributes[i]) is O(1). Lookup by name is O(n) — attribute
/// lists are almost always tiny so this is fine.
/// </remarks>
public sealed class NamedNodeMap : IReadOnlyList<AttrNode>
{
    private readonly Element _owner;
    private readonly List<AttrNode> _attributes = [];

    internal NamedNodeMap(Element owner)
    {
        _owner = owner;
    }

    public int Count => _attributes.Count;

    public AttrNode this[int index] => _attributes[index];

    // ---- Non-namespace-aware operations (HTML path). Attribute names are
    // matched case-insensitively per the HTML spec (content attributes on HTML
    // elements are ASCII case-insensitive). The stored Name is already
    // lower-cased by Element.SetAttribute.

    public AttrNode? GetNamedItem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (var attr in _attributes)
        {
            if (attr.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return attr;
            }
        }
        return null;
    }

    /// <summary>
    /// Add or replace an attribute. Returns the old AttrNode that was replaced,
    /// or null when the attribute was newly added. The old node's OwnerElement
    /// is cleared; the new node's OwnerElement is set to <c>_owner</c>.
    /// </summary>
    public AttrNode? SetNamedItem(AttrNode attr)
    {
        ArgumentNullException.ThrowIfNull(attr);
        for (var i = 0; i < _attributes.Count; i++)
        {
            if (_attributes[i].Name.Equals(attr.Name, StringComparison.OrdinalIgnoreCase))
            {
                var old = _attributes[i];
                if (ReferenceEquals(old, attr))
                {
                    return null; // no change
                }

                var oldValue = old.Value;
                old.OwnerElement = null;
                attr.OwnerElement = _owner;
                _attributes[i] = attr;
                _owner.OnAttributeMutated(attr.Name, oldValue);
                return old;
            }
        }
        attr.OwnerElement = _owner;
        _attributes.Add(attr);
        _owner.OnAttributeMutated(attr.Name);
        return null;
    }

    public AttrNode? RemoveNamedItem(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        for (var i = 0; i < _attributes.Count; i++)
        {
            if (_attributes[i].Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                var removed = _attributes[i];
                removed.OwnerElement = null;
                _attributes.RemoveAt(i);
                _owner.OnAttributeMutated(removed.Name, removed.Value);
                return removed;
            }
        }
        return null;
    }

    // ---- Namespace-aware operations (DOM §4.9). Attributes are keyed by
    // (namespace, local name); the qualified Name carries the prefix. Null and
    // "" namespaces are equivalent (no namespace).

    /// <summary>Local part of a qualified name ("xlink:href" → "href").</summary>
    public static string LocalNameOf(string qualifiedName)
    {
        var i = qualifiedName.IndexOf(':', StringComparison.Ordinal);
        return i >= 0 ? qualifiedName[(i + 1)..] : qualifiedName;
    }

    private static bool NsEq(string? a, string? b)
        => (string.IsNullOrEmpty(a) ? null : a) == (string.IsNullOrEmpty(b) ? null : b);

    public AttrNode? GetNamedItemNS(string? ns, string localName)
    {
        ArgumentNullException.ThrowIfNull(localName);
        foreach (var attr in _attributes)
        {
            if (NsEq(attr.Namespace, ns) && attr.LocalName.Equals(localName, StringComparison.Ordinal))
            {
                return attr;
            }
        }

        return null;
    }

    /// <summary>
    /// Namespace-aware set. Returns the old AttrNode (if replaced) or null (if new).
    /// </summary>
    public AttrNode? SetNamedItemNS(AttrNode attr)
    {
        ArgumentNullException.ThrowIfNull(attr);
        for (var i = 0; i < _attributes.Count; i++)
        {
            if (NsEq(_attributes[i].Namespace, attr.Namespace)
                && _attributes[i].LocalName.Equals(attr.LocalName, StringComparison.Ordinal))
            {
                var old = _attributes[i];
                if (ReferenceEquals(old, attr))
                {
                    return null;
                }

                var oldValue = old.Value;
                old.OwnerElement = null;
                attr.OwnerElement = _owner;
                _attributes[i] = attr;
                _owner.OnAttributeMutated(attr.Name, oldValue);
                return old;
            }
        }
        attr.OwnerElement = _owner;
        _attributes.Add(attr);
        _owner.OnAttributeMutated(attr.Name);
        return null;
    }

    public AttrNode? RemoveNamedItemNS(string? ns, string localName)
    {
        ArgumentNullException.ThrowIfNull(localName);
        for (var i = 0; i < _attributes.Count; i++)
        {
            if (NsEq(_attributes[i].Namespace, ns) && _attributes[i].LocalName.Equals(localName, StringComparison.Ordinal))
            {
                var removed = _attributes[i];
                removed.OwnerElement = null;
                _attributes.RemoveAt(i);
                _owner.OnAttributeMutated(removed.Name, removed.Value);
                return removed;
            }
        }
        return null;
    }

    // ---- Internal value sync -------------------------------------------------

    /// <summary>Called by AttrNode.Value setter to propagate a value change from
    /// the node back into the element's attribute store without re-triggering the
    /// round-trip.</summary>
    internal void SyncAttrValue(AttrNode attr)
    {
        // The AttrNode IS the backing store — _attributes holds the same reference,
        // so the value field is already updated. We just fire the mutation callback.
        _owner.OnAttributeMutated(attr.Name);
    }

    // ---- Compatibility bridge: iterate as (Name, Value) tuples --------------

    // Many internal consumers enumerate attributes as (Name, Value) pairs.
    // The AttrNode exposes both, so callers can still use `attr.Name` / `attr.Value`.

    public IEnumerator<AttrNode> GetEnumerator() => _attributes.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
