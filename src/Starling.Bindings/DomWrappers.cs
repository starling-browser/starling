using System.Runtime.CompilerServices;
using Starling.Dom;
using Starling.Dom.Events;
using Starling.Js.Runtime;

namespace Starling.Bindings;

// AttrNode wrapping is handled through the Node cache since AttrNode extends Node.

/// <summary>
/// B5-1 — Lazy DOM-node ↔ JS-wrapper map. Each host node gets at most one JS
/// wrapper per realm, returned by every <see cref="Wrap"/> call. The wrapper's
/// prototype chain is wired so JS methods on Element/Document/Node/EventTarget
/// resolve via ordinary prototype walk.
/// </summary>
/// <remarks>
/// <para>Wrappers are stored in <see cref="ConditionalWeakTable{TKey, TValue}"/>
/// so they are released when the host node becomes unreachable. The cache is
/// keyed by the host node, not by the wrapper, so a single node observed
/// through two paths still resolves to the same JS object — preserving the
/// invariant <c>document.body === document.body</c>.</para>
/// </remarks>
public static class DomWrappers
{
    // Per-realm wrapper caches. Two realms (rare) get independent caches so a
    // wrapper from realm A isn't reused as the identity in realm B.
    private static readonly ConditionalWeakTable<JsRealm, ConditionalWeakTable<Node, JsObject>> NodeCachesPerRealm = new();

    /// <summary>Return the JS wrapper for <paramref name="target"/> on the
    /// supplied realm. Allocates a new wrapper the first time a node is
    /// observed; returns the cached wrapper on subsequent calls. Non-Node
    /// EventTargets (e.g. Window's <see cref="InMemoryEventTarget"/>) fall back
    /// to a transient plain JS object — currently unused outside synthetic
    /// scenarios.</summary>
    public static JsObject Wrap(JsRealm realm, EventTarget target)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(target);
        if (target is Node node)
        {
            return WrapNode(realm, node);
        }
        // Fallback: return a fresh wrapper bound to the host. Not cached.
        var proto = realm.EventTargetPrototype ?? realm.ObjectPrototype;
        var fresh = new JsObject(proto);
        EventTargetBinding.BindWrapper(fresh, target);
        return fresh;
    }

    private static JsObject WrapNode(JsRealm realm, Node node)
    {
        var cache = NodeCachesPerRealm.GetValue(realm, _ => new ConditionalWeakTable<Node, JsObject>());
        if (cache.TryGetValue(node, out var existing))
        {
            return existing;
        }

        // CharacterData subtypes (Text, Comment, CData, PI) get their own
        // prototype so `instanceof Text` / `instanceof Comment` work.
        var proto = node switch
        {
            Document { IsHtml: false } => realm.XmlDocumentPrototype ?? realm.DocumentPrototype ?? realm.ObjectPrototype,
            Document => realm.DocumentPrototype ?? realm.ObjectPrototype,
            Element => realm.ElementPrototype ?? realm.ObjectPrototype,
            AttrNode => realm.AttrPrototype ?? realm.NodePrototype ?? realm.ObjectPrototype,
            Text => realm.TextPrototype ?? realm.CharacterDataPrototype ?? realm.NodePrototype ?? realm.ObjectPrototype,
            Comment => realm.CommentPrototype ?? realm.CharacterDataPrototype ?? realm.NodePrototype ?? realm.ObjectPrototype,
            CData => realm.CharacterDataPrototype ?? realm.NodePrototype ?? realm.ObjectPrototype,
            ProcessingInstruction => realm.ProcessingInstructionPrototype ?? realm.CharacterDataPrototype ?? realm.NodePrototype ?? realm.ObjectPrototype,
            DocumentFragment => realm.DocumentFragmentPrototype ?? realm.NodePrototype ?? realm.ObjectPrototype,
            DocumentType => realm.DocumentTypePrototype ?? realm.NodePrototype ?? realm.ObjectPrototype,
            _ => realm.NodePrototype ?? realm.ObjectPrototype,
        };
        var wrapper = node is Document doc
            ? new JsDocumentWrapper(proto, doc, realm)
            : new JsObject(proto);
        EventTargetBinding.BindWrapper(wrapper, node);
        cache.Add(node, wrapper);
        return wrapper;
    }

    /// <summary>Wrap an <see cref="AttrNode"/> in a JS object bound to the
    /// Attr prototype. Uses the same node cache so identity is stable.</summary>
    public static JsObject WrapAttr(JsRealm realm, AttrNode attr) => WrapNode(realm, attr);

    /// <summary>Resolve the host <see cref="AttrNode"/> from a JS wrapper, or null.</summary>
    public static AttrNode? UnwrapAttr(JsValue v) => UnwrapNode(v) as AttrNode;

    /// <summary>Wrap a node with an explicit prototype (used by constructors that
    /// create detached nodes where the default prototype-selection heuristic would
    /// pick the wrong type — e.g. <c>new Text("…")</c> creates a Text with
    /// TextPrototype, not the generic NodePrototype).</summary>
    public static JsObject WrapWithProto(JsRealm realm, Node node, JsObject proto)
    {
        ArgumentNullException.ThrowIfNull(realm);
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(proto);
        var cache = NodeCachesPerRealm.GetValue(realm, _ => new ConditionalWeakTable<Node, JsObject>());
        if (cache.TryGetValue(node, out var existing))
        {
            return existing;
        }

        var wrapper = new JsObject(proto);
        EventTargetBinding.BindWrapper(wrapper, node);
        cache.Add(node, wrapper);
        return wrapper;
    }

    /// <summary>Resolve the host <see cref="Node"/> backing a JS wrapper, or
    /// null when the object is not a wrapped DOM node.</summary>
    public static Node? UnwrapNode(JsValue v) =>
        EventTargetBinding.ResolveHost(v) as Node;

    public static Element? UnwrapElement(JsValue v) => UnwrapNode(v) as Element;
    public static Document? UnwrapDocument(JsValue v) => UnwrapNode(v) as Document;

    /// <summary>Convenience for proto methods: returns null when <c>this</c> is
    /// not a wrapped node of the expected type.</summary>
    internal static T? UnwrapAs<T>(JsValue v) where T : class => EventTargetBinding.ResolveHost(v) as T;

    /// <summary>Build a plain JS object representing an <see cref="Attr"/> value.
    /// Attr is not a Node in DOM4+, so we create a simple bag with the relevant
    /// properties (name, localName, value, namespaceURI, prefix, ownerElement,
    /// specified) — sufficient for the WPT attribute accessor tests.</summary>
    public static JsObject WrapAttr(JsRealm realm, Attr attr, Element? ownerElement = null)
    {
        var o = new JsObject(realm.ObjectPrototype);
        // Compute localName and prefix from the qualified name (e.g. "xml:lang" → prefix="xml", localName="lang")
        var name = attr.Name;
        string? prefix = null;
        string localName = name;
        var colon = name.IndexOf(':');
        if (colon > 0) { prefix = name[..colon]; localName = name[(colon + 1)..]; }
        o.Set("name", JsValue.String(name));
        o.Set("localName", JsValue.String(localName));
        o.Set("value", JsValue.String(attr.Value ?? ""));
        o.Set("namespaceURI", attr.Namespace is null ? JsValue.Null : JsValue.String(attr.Namespace));
        o.Set("prefix", prefix is null ? JsValue.Null : JsValue.String(prefix));
        o.Set("specified", JsValue.True);
        if (ownerElement is not null)
        {
            o.Set("ownerElement", JsValue.Object(DomWrappers.Wrap(realm, ownerElement)));
        }
        else
        {
            o.Set("ownerElement", JsValue.Null);
        }

        return o;
    }
}

/// <summary>
/// Exotic JS object backing <c>element.attributes</c> (DOM §4.9 NamedNodeMap).
/// Inherits from <c>NamedNodeMap.prototype</c>; individual attribute names are
/// exposed as named properties returning the wrapped <see cref="AttrNode"/>.
/// Indexed properties (e.g. <c>el.attributes[0]</c>) also resolve to AttrNode wrappers.
/// Per-attribute named properties shadow only the name — methods like
/// <c>getNamedItem</c> are on the prototype and are NOT shadowed even if an
/// attribute has the same name.
/// </summary>
internal sealed class JsNamedNodeMapObject : JsObject
{
    private readonly JsRealm _realm;
    private readonly Element _element;

    public JsNamedNodeMapObject(JsRealm realm, Element element)
        : base(realm.NamedNodeMapPrototype ?? realm.ObjectPrototype)
    {
        _realm = realm;
        _element = element;
        // NOTE: "length" is NOT installed as an own property descriptor here.
        // Instead it is surfaced dynamically via GetOwnPropertyDescriptor so
        // Object.getOwnPropertyNames does NOT include "length" in its output
        // (matching WPT expectations for namednodemap-supported-property-names).
    }

    // ---- Helpers ------------------------------------------------------------

    public int Length => _element.Attributes.Count;

    public AttrNode? GetItem(int index)
        => index >= 0 && index < _element.Attributes.Count ? _element.Attributes[index] : null;

    public AttrNode? GetNamedItem(string name)
        => _element.Attributes.GetNamedItem(name);

    public AttrNode? GetNamedItemNS(string? ns, string localName)
        => _element.Attributes.GetNamedItemNS(ns, localName);

    public AttrNode? SetNamedItem(AttrNode attr)
        => _element.Attributes.SetNamedItem(attr);

    public AttrNode? SetNamedItemNS(AttrNode attr)
        => _element.Attributes.SetNamedItemNS(attr);

    public AttrNode? RemoveNamedItem(string name)
        => _element.Attributes.RemoveNamedItem(name);

    public AttrNode? RemoveNamedItemNS(string? ns, string localName)
        => _element.Attributes.RemoveNamedItemNS(ns, localName);

    // ---- Exotic property semantics (DOM §4.9.1 NamedNodeMap) ----------------
    //
    // The VM uses AbstractOperations.Get → GetOwnPropertyDescriptor, NOT the
    // virtual JsObject.Get(string) method. We must override GetOwnPropertyDescriptor
    // so that integer indices and attribute names resolve through the prototype
    // chain walk performed by AbstractOperations.Get.
    //
    // Priority order per spec:
    //   1. Own data/accessor properties (e.g. "length" accessor installed in ctor)
    //   2. Prototype methods (NamedNodeMap.prototype: item, getNamedItem, etc.)
    //      — the VM handles this automatically via prototype chain walk
    //   3. Indexed access: attributes[0], attributes[1], ...
    //   4. Named getter: attributes.id → the Attr node for "id"
    //      BUT only if the name is NOT already on the prototype (prototype methods win)
    //
    // We surface #3 and #4 via GetOwnPropertyDescriptor so the VM's chain walk finds them.

    public override PropertyDescriptor? GetOwnPropertyDescriptor(string name)
    {
        // Check explicitly installed own properties first (e.g. "length" accessor).
        var own = base.GetOwnPropertyDescriptor(name);
        if (own is not null)
        {
            return own;
        }

        // Indexed integer properties → attributes[i]
        if (uint.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var idx))
        {
            var indexed = GetItem((int)idx);
            if (indexed is not null)
            {
                return PropertyDescriptor.Data(JsValue.Object(DomWrappers.WrapAttr(_realm, indexed)),
                    writable: false, enumerable: true, configurable: true);
            }

            return null;
        }

        // Named getter (DOM §4.9.1): attribute name → wrapped AttrNode.
        // Prototype methods take priority — they are found by the VM walking the
        // prototype chain BEFORE reaching our own descriptor. So we can safely
        // return the attribute descriptor here; if the prototype has the same name
        // it wins because GetOwnPropertyDescriptor on the prototype returns non-null
        // and the chain walk stops there.
        //
        // Exception: we explicitly do NOT return descriptors for prototype method
        // names so that method identity holds (map.item === NamedNodeMap.prototype.item).
        // Check the prototype first.
        if (IsOnPrototype(name))
        {
            return null;
        }

        var attr = _element.Attributes.GetNamedItem(name);
        if (attr is not null)
        {
            return PropertyDescriptor.Data(JsValue.Object(DomWrappers.WrapAttr(_realm, attr)),
                writable: false, enumerable: true, configurable: true);
        }

        return null;
    }

    /// <summary>Returns true when <paramref name="name"/> is a property on our
    /// NamedNodeMap.prototype (so named-getter does not shadow it).</summary>
    private bool IsOnPrototype(string name)
    {
        for (var p = Prototype; p is not null; p = p.Prototype)
        {
            if (p.GetOwnPropertyDescriptor(name) is not null)
            {
                return true;
            }
        }

        return false;
    }

    public override bool Has(string name)
    {
        if (base.Has(name))
        {
            return true;
        }

        if (uint.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var idx))
        {
            return (int)idx < _element.Attributes.Count;
        }

        if (IsOnPrototype(name))
        {
            return true;
        }

        return _element.Attributes.GetNamedItem(name) is not null;
    }

    public override bool HasOwn(string name)
    {
        if (base.HasOwn(name))
        {
            return true;
        }

        if (uint.TryParse(name, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var idx))
        {
            return (int)idx < _element.Attributes.Count;
        }

        if (IsOnPrototype(name))
        {
            return false;
        }

        return _element.Attributes.GetNamedItem(name) is not null;
    }

    public override IEnumerable<string> EnumerableKeys()
    {
        // Indexed attribute positions
        for (var i = 0; i < _element.Attributes.Count; i++)
        {
            yield return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        // Named attribute keys (non-shadowed by prototype)
        foreach (var attr in _element.Attributes)
        {
            if (!IsOnPrototype(attr.Name))
            {
                yield return attr.Name;
            }
        }
        // Own non-attr enumerable keys (e.g. nothing extra currently)
        foreach (var k in base.EnumerableKeys())
        {
            yield return k;
        }
    }

    public override IEnumerable<Starling.Js.Runtime.JsPropertyKey> OwnPropertyKeys
    {
        get
        {
            for (var i = 0; i < _element.Attributes.Count; i++)
            {
                yield return Starling.Js.Runtime.JsPropertyKey.String(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            foreach (var attr in _element.Attributes)
            {
                if (!IsOnPrototype(attr.Name))
                {
                    yield return Starling.Js.Runtime.JsPropertyKey.String(attr.Name);
                }
            }

            foreach (var k in base.OwnPropertyKeys)
            {
                yield return k;
            }
        }
    }

    // Object.getOwnPropertyNames uses target.Keys (the _properties dict keys).
    // Override it to include our dynamic attribute indices + names.
    // NOTE: "length" is on the prototype, not on each instance, so it is NOT
    // returned here (matching the WPT expectation).
    public override IEnumerable<string> Keys
    {
        get
        {
            // Indexed keys
            for (var i = 0; i < _element.Attributes.Count; i++)
            {
                yield return i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            // Named attribute keys (if not shadowed by prototype methods)
            foreach (var attr in _element.Attributes)
            {
                if (!IsOnPrototype(attr.Name))
                {
                    yield return attr.Name;
                }
            }
            // Any explicitly installed own props (currently none for this exotic object)
            foreach (var k in base.Keys)
            {
                yield return k;
            }
        }
    }
}

/// <summary>
/// Exotic JS object backing a <c>Document</c> wrapper: a legacy platform object
/// with named properties (HTML §3.1.5). <c>document[name]</c> / <c>name in
/// document</c> resolve to the named <c>embed</c>/<c>form</c>/<c>iframe</c>/
/// <c>img</c>/<c>object</c> element(s) — a single iframe yields its content
/// window, a single other element yields the element, several yield an
/// HTMLCollection — but only when the name is not already an own/prototype
/// property (so document methods and attributes still win).
/// </summary>
internal sealed class JsDocumentWrapper : JsObject
{
    private readonly Document _doc;
    private readonly JsRealm _realm;

    public JsDocumentWrapper(JsObject proto, Document doc, JsRealm realm) : base(proto)
    {
        _doc = doc;
        _realm = realm;
    }

    private static bool NameAccessible(Element e) =>
        e.Namespace == Element.HtmlNamespace
        && e.LocalName is "embed" or "form" or "iframe" or "img" or "object"
        && !string.IsNullOrEmpty(e.GetAttribute("name"));

    // id grants a named property for object elements, and for img elements that
    // also carry a name attribute.
    private static bool IdAccessible(Element e) =>
        e.Namespace == Element.HtmlNamespace
        && !string.IsNullOrEmpty(e.GetAttribute("id"))
        && (e.LocalName == "object"
            || (e.LocalName == "img" && !string.IsNullOrEmpty(e.GetAttribute("name"))));

    private List<Element> NamedElements(string name)
    {
        var result = new List<Element>();
        if (string.IsNullOrEmpty(name))
        {
            return result;
        }

        foreach (var e in _doc.DescendantElements())
        {
            if ((NameAccessible(e) && e.GetAttribute("name") == name)
                || (IdAccessible(e) && e.GetAttribute("id") == name))
            {
                result.Add(e);
            }
        }

        return result;
    }

    private JsValue NamedValue(string lookupName, List<Element> matches)
    {
        if (matches.Count == 1)
        {
            var el = matches[0];
            if (el.LocalName == "iframe" && el.Namespace == Element.HtmlNamespace)
            {
                return JsValue.Object(IFrameBinding.EnsureContentWindow(_realm, IFrameBinding.EnsureContext(el)));
            }

            return JsValue.Object(DomWrappers.Wrap(_realm, el));
        }
        // Several matches → a live HTMLCollection re-evaluated on access, keyed by
        // the SAME name the property was looked up under — not a name/id guessed
        // off the first match, which could rebuild the collection for a different
        // key when an element's id and name differ.
        return NodeBindings.BuildHtmlCollection(_realm, () => NamedElements(lookupName));
    }

    private bool ShadowedByPrototype(string name)
    {
        for (var p = GetPrototypeOf(); p is not null; p = p.GetPrototypeOf())
        {
            if (p.HasOwn(name))
            {
                return true;
            }
        }

        return false;
    }

    // The VM resolves property reads and the `in` operator through
    // GetOwnPropertyDescriptor, so the named-property lookup lives here. A named
    // property is exposed only when it is not an own data property and not
    // shadowed by anything on the prototype chain (no [LegacyOverrideBuiltins]).
    public override PropertyDescriptor? GetOwnPropertyDescriptor(string name)
    {
        if (base.GetOwnPropertyDescriptor(name) is { } own)
        {
            return own;
        }

        if (ShadowedByPrototype(name))
        {
            return null;
        }

        var matches = NamedElements(name);
        if (matches.Count == 0)
        {
            return null;
        }

        return PropertyDescriptor.Data(NamedValue(name, matches), writable: true, enumerable: true, configurable: true);
    }

    public override JsValue Get(string name)
    {
        // An own property (expando) wins even when its value is undefined — decide
        // ownership via HasOwn, not the returned value, so `document.foo = undefined`
        // is not later overridden by a named element called "foo".
        if (base.HasOwn(name))
        {
            return base.Get(name);
        }

        if (!ShadowedByPrototype(name) && NamedElements(name) is { Count: > 0 } matches)
        {
            return NamedValue(name, matches);
        }

        return base.Get(name); // prototype / built-ins (getElementById, …) resolve here
    }

    public override bool HasOwn(string name)
    {
        if (base.HasOwn(name))
        {
            return true;
        }

        return !ShadowedByPrototype(name) && NamedElements(name).Count > 0;
    }

    // The supported property names (HTML §3.1.5): the name of each accessible
    // embed/form/iframe/img/object and the id of each accessible object / named
    // img, in tree order, de-duplicated and not shadowed by a prototype property.
    private IEnumerable<string> SupportedNames()
    {
        // Suppress names that already exist as an own (expando) property: those win
        // (GetOwnPropertyDescriptor returns the own property first), so emitting the
        // supported name too would put a duplicate in the own-key list.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var e in _doc.DescendantElements())
        {
            if (NameAccessible(e) && e.GetAttribute("name") is { Length: > 0 } n
                && !base.HasOwn(n) && !ShadowedByPrototype(n) && seen.Add(n))
            {
                yield return n;
            }

            if (IdAccessible(e) && e.GetAttribute("id") is { Length: > 0 } id
                && !base.HasOwn(id) && !ShadowedByPrototype(id) && seen.Add(id))
            {
                yield return id;
            }
        }
    }

    // Legacy platform objects enumerate supported property names BEFORE ordinary
    // own (expando) keys; Keys and OwnPropertyKeys must agree on that order.
    public override IEnumerable<string> Keys
    {
        get
        {
            foreach (var n in SupportedNames())
            {
                yield return n;
            }

            foreach (var k in base.Keys)
            {
                yield return k;
            }
        }
    }

    public override IEnumerable<Starling.Js.Runtime.JsPropertyKey> OwnPropertyKeys
    {
        get
        {
            foreach (var n in SupportedNames())
            {
                yield return Starling.Js.Runtime.JsPropertyKey.String(n);
            }

            foreach (var k in base.OwnPropertyKeys)
            {
                yield return k;
            }
        }
    }
}

/// <summary>
/// Exotic JS object backing <c>element.dataset</c> (HTML §2.7.3 DOMStringMap).
/// Property reads/writes delegate to the element's <c>data-*</c> attributes.
/// Name mapping: camelCase property ↔ "data-" + kebab-case attribute name.
/// </summary>
internal sealed class JsDatasetObject : JsObject
{
    private readonly Element _element;

    public JsDatasetObject(JsObject proto, Element element) : base(proto)
    {
        _element = element;
    }

    // camelCase → data-kebab-case
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

    // data-kebab-case → camelCase
    private static string AttrToProp(string attr)
    {
        // Strip "data-" prefix
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

    public override JsValue Get(string name)
    {
        var attr = _element.GetAttribute(PropToAttr(name));
        return attr is not null ? JsValue.String(attr) : base.Get(name);
    }

    public override void Set(string name, JsValue value)
    {
        _element.SetAttribute(PropToAttr(name), JsValue.ToStringValue(value));
    }

    public override bool DefineOwnProperty(string name, PropertyDescriptor desc)
    {
        if (desc.IsAccessor)
        {
            return base.DefineOwnProperty(name, desc);
        }

        _element.SetAttribute(PropToAttr(name), JsValue.ToStringValue(desc.Value));
        return true;
    }

    public override PropertyDescriptor? GetOwnPropertyDescriptor(string name)
    {
        var attr = _element.GetAttribute(PropToAttr(name));
        if (attr is not null)
        {
            return PropertyDescriptor.Data(JsValue.String(attr), writable: true, enumerable: true, configurable: true);
        }

        return base.GetOwnPropertyDescriptor(name);
    }

    public override bool Has(string name)
    {
        if (_element.HasAttribute(PropToAttr(name)))
        {
            return true;
        }

        return base.Has(name);
    }

    public override bool HasOwn(string name)
    {
        if (_element.HasAttribute(PropToAttr(name)))
        {
            return true;
        }

        return base.HasOwn(name);
    }

    public override bool Delete(string name)
    {
        if (_element.HasAttribute(PropToAttr(name)))
        {
            _element.RemoveAttribute(PropToAttr(name));
            return true;
        }
        return base.Delete(name);
    }

    public override IEnumerable<string> EnumerableKeys()
    {
        foreach (var attr in _element.Attributes)
        {
            if (attr.Name.StartsWith("data-", StringComparison.OrdinalIgnoreCase))
            {
                yield return AttrToProp(attr.Name);
            }
        }
    }

    public override IEnumerable<string> Keys =>
        EnumerableKeys().Concat(base.Keys);
}
