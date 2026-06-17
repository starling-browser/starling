using System.Globalization;
using Starling.Dom;
using Starling.Js.Runtime;

namespace Starling.Bindings;

/// <summary>
/// A live HTMLCollection (DOM §4.2.10) as a legacy platform object: integer
/// indices resolve to items, and supported property names (element ids, plus
/// the name attribute of HTML-namespace elements) resolve to the matching
/// element. Backed by a snapshot function so it reflects the current tree.
/// </summary>
internal sealed class HtmlCollectionObject : JsObject
{
    private readonly JsRealm _realm;
    private readonly Func<IReadOnlyList<Element>> _source;

    public HtmlCollectionObject(JsRealm realm, JsObject? prototype, Func<IReadOnlyList<Element>> source)
        : base(prototype)
    {
        _realm = realm;
        _source = source;
    }

    private IReadOnlyList<Element> Items => _source();

    public int Count => Items.Count;

    public JsValue Item(int index)
    {
        var items = Items;
        return index >= 0 && index < items.Count
            ? JsValue.Object(DomWrappers.Wrap(_realm, items[index])) : JsValue.Null;
    }

    public JsValue NamedItemValue(string name)
        => NamedItem(name) is { } e ? JsValue.Object(DomWrappers.Wrap(_realm, e)) : JsValue.Null;

    public IEnumerable<JsValue> Values()
    {
        foreach (var e in Items)
        {
            yield return JsValue.Object(DomWrappers.Wrap(_realm, e));
        }
    }

    // An array index is a canonical non-negative integer string (no leading
    // zeros, within range).
    private static bool TryIndex(string name, out int index)
    {
        index = 0;
        if (name.Length == 0)
        {
            return false;
        }

        if (name.Length > 1 && name[0] == '0')
        {
            return false;
        }

        if (!ulong.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var v))
        {
            return false;
        }
        // WebIDL "array index": an integer in [0, 2^32-2]. 2^32-1 (4294967295)
        // and above are NOT array indices — they fall through to named-property
        // lookup. A value beyond int range is a valid index but always past the
        // end of any real collection, so clamp it to int.MaxValue (=> undefined).
        if (v > 4294967294UL)
        {
            return false;
        }

        index = v > int.MaxValue ? int.MaxValue : (int)v;
        return true;
    }

    private Element? NamedItem(string name)
    {
        if (name.Length == 0)
        {
            return null;
        }

        var items = Items;
        foreach (var e in items)
        {
            if (e.GetAttribute("id") == name)
            {
                return e;
            }
        }

        foreach (var e in items)
        {
            if (e.Namespace == Element.HtmlNamespace && e.GetAttribute("name") == name)
            {
                return e;
            }
        }

        return null;
    }

    // The supported property names: each element's id, then the name attribute
    // of HTML-namespace elements, in tree order with no duplicates.
    private List<string> SupportedNames()
    {
        // HTML §2.7.2.1 "supported property names": process each element in tree
        // order, appending its id then (for HTML elements) its name — so the two
        // keys of one element stay adjacent — skipping duplicates.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (var e in Items)
        {
            var id = e.GetAttribute("id");
            if (!string.IsNullOrEmpty(id) && seen.Add(id))
            {
                names.Add(id);
            }

            if (e.Namespace == Element.HtmlNamespace)
            {
                var n = e.GetAttribute("name");
                if (!string.IsNullOrEmpty(n) && seen.Add(n))
                {
                    names.Add(n);
                }
            }
        }
        return names;
    }

    public override JsValue Get(string name)
    {
        var items = Items;
        if (TryIndex(name, out var index))
        {
            return index < items.Count ? JsValue.Object(DomWrappers.Wrap(_realm, items[index])) : JsValue.Undefined;
        }
        // A named element resolves only when the name is a visible named property
        // (not shadowed by an own expando or a prototype/built-in like item/length).
        if (IsVisibleNamedProperty(name) && NamedItem(name) is { } named)
        {
            return JsValue.Object(DomWrappers.Wrap(_realm, named));
        }

        return base.Get(name); // length lives on the prototype; expandos / built-ins resolve here
    }

    // WebIDL named-property visibility (HTMLCollection has no [LegacyOverrideBuiltins]):
    // a supported name is shadowed when an own expando OR any prototype property
    // shares the name. Uses base.HasOwn so the synthesized index/named properties
    // (which this override reports) don't make every name look "shadowed".
    private bool IsShadowed(string name)
    {
        if (base.HasOwn(name))
        {
            return true; // real own expando
        }

        for (var p = GetPrototypeOf(); p is not null; p = p.GetPrototypeOf())
        {
            if (p.HasOwn(name))
            {
                return true; // prototype / built-in (item, namedItem, length, …)
            }
        }

        return false;
    }

    private bool IsVisibleNamedProperty(string name)
        => !TryIndex(name, out _) && !IsShadowed(name) && NamedItem(name) is not null;

    public override bool HasOwn(string name)
    {
        if (TryIndex(name, out var index))
        {
            return index < Items.Count;
        }

        if (base.HasOwn(name))
        {
            return true;
        }

        return IsVisibleNamedProperty(name);
    }

    public override PropertyDescriptor? GetOwnPropertyDescriptor(string name)
    {
        var items = Items;
        // Indexed and named properties are read-only (HTMLCollection has no indexed
        // or named setter), so the descriptor is writable:false — matching the
        // Set/Delete/DefineOwnProperty overrides and making a strict-mode write
        // (e.g. coll[0] = x) fail per WebIDL rather than silently appear to succeed.
        if (TryIndex(name, out var index))
        {
            return index < items.Count
                ? PropertyDescriptor.Data(JsValue.Object(DomWrappers.Wrap(_realm, items[index])), writable: false, enumerable: true, configurable: true)
                : null;
        }

        if (base.GetOwnPropertyDescriptor(name) is { } own)
        {
            return own;
        }

        if (IsVisibleNamedProperty(name) && NamedItem(name) is { } named)
        {
            return PropertyDescriptor.Data(JsValue.Object(DomWrappers.Wrap(_realm, named)), writable: false, enumerable: true, configurable: true);
        }

        return null;
    }

    // The own string keys in spec order: array indices, then supported property
    // names, then any expando (ordinary) string keys. `Keys` drives
    // Object.getOwnPropertyNames; OwnPropertyKeys adds the expando symbols.
    public override IEnumerable<string> Keys
    {
        get
        {
            var count = Items.Count;
            for (var i = 0; i < count; i++)
            {
                yield return i.ToString(CultureInfo.InvariantCulture);
            }
            // WebIDL [[OwnPropertyKeys]]: after the indices come the supported
            // names that are NOT array indices and NOT shadowed by an own expando
            // or a prototype/built-in key — so the own-key list stays duplicate-free
            // and never collides with an index ("0"), "length", or "item".
            foreach (var n in SupportedNames())
            {
                if (!TryIndex(n, out _) && !IsShadowed(n))
                {
                    yield return n;
                }
            }

            foreach (var k in base.Keys)
            {
                yield return k; // expando properties set directly on the collection
            }
        }
    }

    public override IEnumerable<JsPropertyKey> OwnPropertyKeys
    {
        get
        {
            foreach (var k in Keys)
            {
                yield return JsPropertyKey.String(k);
            }

            foreach (var s in SymbolKeys)
            {
                yield return JsPropertyKey.Symbol(s);
            }
        }
    }

    // Legacy platform object: no indexed or named property setter, so a plain
    // assignment to an array index or a VISIBLE supported named property is ignored
    // (in loose mode; strict-mode throwing is handled by the VM's set path). A name
    // shadowed by the prototype (e.g. "item") is not a visible named property, so it
    // behaves as an ordinary expando. Any other key is an ordinary expando.
    public override void Set(string name, JsValue value)
    {
        if (TryIndex(name, out _))
        {
            return;
        }

        if (IsVisibleNamedProperty(name))
        {
            return;
        }

        base.Set(name, value);
    }

    // Legacy platform object [[Delete]] (WebIDL): an indexed property is never
    // deletable, and HTMLCollection (no [LegacyOverrideBuiltins]) also refuses to
    // delete a visible supported named property. Expando keys delete normally.
    public override bool Delete(string name)
    {
        if (TryIndex(name, out _))
        {
            return false;
        }

        if (IsVisibleNamedProperty(name))
        {
            return false;
        }

        return base.Delete(name);
    }

    // Legacy platform object [[DefineOwnProperty]] (WebIDL): HTMLCollection has no
    // indexed or named property setter, so defining over an array index, or over a
    // visible supported named property, fails; any other key is an ordinary expando.
    public override bool DefineOwnProperty(string name, PropertyDescriptor desc)
    {
        if (TryIndex(name, out _))
        {
            return false;
        }

        if (IsVisibleNamedProperty(name))
        {
            return false;
        }

        return base.DefineOwnProperty(name, desc);
    }
}
