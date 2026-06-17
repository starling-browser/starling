using System.Globalization;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Native.Symbol;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using Starling.Dom;

namespace Starling.Bindings.Jint;

/// <summary>
/// Live DOM collections for the Jint backend — NodeList, HTMLCollection, and
/// DOMTokenList — mirroring <c>Starling.Bindings/{NodeListObject,
/// HtmlCollectionObject,DomTokenListObject}.cs</c>. Before this, every
/// collection-returning member (<c>childNodes</c>, <c>children</c>,
/// <c>getElementsBy*</c>, <c>querySelectorAll</c>, <c>classList</c>) returned a
/// plain snapshot <c>Array</c>, so <c>item()</c>/<c>namedItem()</c>, named access
/// (<c>coll.id</c>), liveness, and <c>instanceof NodeList</c>/<c>HTMLCollection</c>
/// were all absent. This installs real interface prototypes + constructors and
/// the exotic objects the Node bindings hand back.
/// </summary>
internal static class CollectionsBinding
{
    public static void Install(JintBackendContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        if (ctx.Wrappers.NodeListPrototype is not null)
        {
            return; // idempotent
        }

        var engine = ctx.Engine;

        // ---- NodeList.prototype -------------------------------------------------
        var nodeListProto = new JsObject(engine);
        ctx.Wrappers.NodeListPrototype = nodeListProto;
        JintInterop.DefineAccessor(engine, nodeListProto, "length",
            (t, _) => JintInterop.Num(t is JintNodeListObject n ? n.Count : 0));
        JintInterop.DefineMethod(engine, nodeListProto, "item",
            (t, a) => t is JintNodeListObject n && a.Length > 0 ? n.Item((int)TypeConverter.ToNumber(a[0])) : JsValue.Null, 1);
        DefineIteration(engine, nodeListProto, t => (t as JintNodeListObject)?.ValuesArray(engine));
        WireInterface(engine, nodeListProto, "NodeList");

        // ---- HTMLCollection.prototype ------------------------------------------
        var htmlCollProto = new JsObject(engine);
        ctx.Wrappers.HtmlCollectionPrototype = htmlCollProto;
        JintInterop.DefineAccessor(engine, htmlCollProto, "length",
            (t, _) => JintInterop.Num(t is JintHtmlCollectionObject c ? c.Count : 0));
        JintInterop.DefineMethod(engine, htmlCollProto, "item",
            (t, a) => t is JintHtmlCollectionObject c && a.Length > 0 ? c.Item((int)TypeConverter.ToNumber(a[0])) : JsValue.Null, 1);
        JintInterop.DefineMethod(engine, htmlCollProto, "namedItem",
            (t, a) => t is JintHtmlCollectionObject c && a.Length > 0 ? c.NamedItemValue(TypeConverter.ToString(a[0])) : JsValue.Null, 1);
        DefineIterator(engine, htmlCollProto, t => (t as JintHtmlCollectionObject)?.ValuesArray(engine));
        WireInterface(engine, htmlCollProto, "HTMLCollection");

        // ---- DOMTokenList.prototype --------------------------------------------
        // The classList object sets its own per-element methods; the prototype
        // carries the iteration surface shared by every token list.
        var tokenListProto = new JsObject(engine);
        ctx.Wrappers.DomTokenListPrototype = tokenListProto;
        DefineIteration(engine, tokenListProto, t => (t as JintDomTokenListObject)?.ValuesArray(engine));
        WireInterface(engine, tokenListProto, "DOMTokenList");
    }

    // ---- factory helpers used by NodeBindings -------------------------------

    public static JintNodeListObject NodeList(JintBackendContext ctx, Func<IReadOnlyList<Node>> source)
        => new(ctx, ctx.Wrappers.NodeListPrototype, source);

    public static JintHtmlCollectionObject HtmlCollection(JintBackendContext ctx, Func<IReadOnlyList<Element>> source)
        => new(ctx, ctx.Wrappers.HtmlCollectionPrototype, source);

    // ---- shared iteration installers ----------------------------------------

    // NodeList: array-like iteration — values/keys/entries/forEach + @@iterator.
    private static void DefineIteration(global::Jint.Engine engine, ObjectInstance proto, Func<JsValue, JsArray?> snapshot)
    {
        DefineIterator(engine, proto, snapshot);
        JintInterop.DefineMethod(engine, proto, "keys", (t, _) =>
        {
            var arr = snapshot(t) ?? new JsArray(engine, System.Array.Empty<JsValue>());
            var keys = new JsValue[arr.Length];
            for (uint i = 0; i < arr.Length; i++)
            {
                keys[i] = JintInterop.Num(i);
            }

            return ArrayIterator(engine, new JsArray(engine, keys));
        }, 0);
        JintInterop.DefineMethod(engine, proto, "entries", (t, _) =>
        {
            var arr = snapshot(t) ?? new JsArray(engine, System.Array.Empty<JsValue>());
            var entries = new JsValue[arr.Length];
            for (uint i = 0; i < arr.Length; i++)
            {
                entries[i] = new JsArray(engine, new[] { JintInterop.Num(i), arr[(int)i] });
            }

            return ArrayIterator(engine, new JsArray(engine, entries));
        }, 0);
        JintInterop.DefineMethod(engine, proto, "forEach", (t, a) =>
        {
            var arr = snapshot(t) ?? new JsArray(engine, System.Array.Empty<JsValue>());
            if (a.Length == 0 || !a[0].IsCallable())
            {
                return JsValue.Undefined;
            }

            var cb = a[0];
            var thisArg = a.Length > 1 ? a[1] : JsValue.Undefined;
            for (uint i = 0; i < arr.Length; i++)
            {
                cb.Call(thisArg, new[] { arr[(int)i], JintInterop.Num(i), t });
            }

            return JsValue.Undefined;
        }, 1);
    }

    // Define `values` + `[Symbol.iterator]` (both yield the items), shared by all
    // three collection prototypes.
    private static void DefineIterator(global::Jint.Engine engine, ObjectInstance proto, Func<JsValue, JsArray?> snapshot)
    {
        JsValue Values(JsValue t, JsValue[] _)
            => ArrayIterator(engine, snapshot(t) ?? new JsArray(engine, System.Array.Empty<JsValue>()));
        JintInterop.DefineMethod(engine, proto, "values", Values, 0);
        var iterFn = new ClrFunction(engine, "[Symbol.iterator]", (t, args) => Values(t, args), 0, PropertyFlag.Configurable);
        proto.DefineOwnProperty(GlobalSymbolRegistry.Iterator,
            new PropertyDescriptor(iterFn, writable: true, enumerable: false, configurable: true));
    }

    // Build a real ES array iterator from a snapshot array (so .next()/for-of work).
    private static JsValue ArrayIterator(global::Jint.Engine engine, JsArray array)
    {
        var iterFn = array.Get(GlobalSymbolRegistry.Iterator);
        return iterFn.Call(array, System.Array.Empty<JsValue>());
    }

    // Wire an interface prototype to a constructible-illegal global ctor so
    // `coll instanceof NodeList` resolves and `NodeList.prototype` is reachable.
    private static void WireInterface(global::Jint.Engine engine, ObjectInstance proto, string name)
    {
        var ctor = new ClrFunction(engine, name,
            (_, _) => throw new JavaScriptException(engine.Intrinsics.TypeError, "Illegal constructor"), 0, PropertyFlag.Configurable);
        ctor.Set("prototype", proto);
        JintInterop.DefineDataProp(proto, "constructor", ctor, writable: true, enumerable: false, configurable: true);
        proto.DefineOwnProperty(GlobalSymbolRegistry.ToStringTag,
            new PropertyDescriptor(JintInterop.Str(name), writable: false, enumerable: false, configurable: true));
        JintInterop.DefineDataProp(engine.Global, name, ctor, writable: true, enumerable: false, configurable: true);
    }
}

/// <summary>Live NodeList (DOM §4.2.10.2): integer indices + <c>length</c> resolve
/// against a snapshot function. No named properties (unlike HTMLCollection).</summary>
internal sealed class JintNodeListObject : ObjectInstance
{
    private readonly JintBackendContext _ctx;
    private readonly Func<IReadOnlyList<Node>> _source;

    public JintNodeListObject(JintBackendContext ctx, ObjectInstance? prototype, Func<IReadOnlyList<Node>> source)
        : base(ctx.Engine)
    {
        _ctx = ctx;
        _source = source;
        if (prototype is not null)
        {
            Prototype = prototype;
        }
    }

    private IReadOnlyList<Node> Items => _source();
    public int Count => Items.Count;

    public JsValue Item(int index)
    {
        var items = Items;
        return index >= 0 && index < items.Count ? _ctx.Wrappers.Wrap(items[index]) : JsValue.Null;
    }

    public JsArray ValuesArray(global::Jint.Engine engine)
    {
        var items = Items;
        var values = new JsValue[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            values[i] = _ctx.Wrappers.Wrap(items[i]);
        }

        return new JsArray(engine, values);
    }

    public override JsValue Get(JsValue property, JsValue receiver)
    {
        if (property.IsString() && CollectionIndex.TryIndex(property.AsString(), out var i))
        {
            var items = Items;
            return i < items.Count ? _ctx.Wrappers.Wrap(items[i]) : JsValue.Undefined;
        }
        return base.Get(property, receiver);
    }

    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        if (property.IsString() && CollectionIndex.TryIndex(property.AsString(), out var i))
        {
            var items = Items;
            if (i < items.Count)
            {
                return new PropertyDescriptor(_ctx.Wrappers.Wrap(items[i]), writable: false, enumerable: true, configurable: true);
            }
        }
        return base.GetOwnProperty(property);
    }

    public override bool HasProperty(JsValue property)
    {
        if (property.IsString() && CollectionIndex.TryIndex(property.AsString(), out var i) && i < Items.Count)
        {
            return true;
        }

        return base.HasProperty(property);
    }

    public override List<JsValue> GetOwnPropertyKeys(Types types = Types.String | Types.Symbol)
    {
        var keys = new List<JsValue>();
        if ((types & Types.String) != 0)
        {
            for (var i = 0; i < Items.Count; i++)
            {
                keys.Add(JintInterop.Str(i.ToString(CultureInfo.InvariantCulture)));
            }
        }

        keys.AddRange(base.GetOwnPropertyKeys(types));
        return keys;
    }
}

/// <summary>Live HTMLCollection (DOM §4.2.10): integer indices + supported named
/// properties (element ids, and the <c>name</c> attribute of HTML-namespace
/// elements) resolve against a snapshot function.</summary>
internal sealed class JintHtmlCollectionObject : ObjectInstance
{
    private readonly JintBackendContext _ctx;
    private readonly Func<IReadOnlyList<Element>> _source;

    public JintHtmlCollectionObject(JintBackendContext ctx, ObjectInstance? prototype, Func<IReadOnlyList<Element>> source)
        : base(ctx.Engine)
    {
        _ctx = ctx;
        _source = source;
        if (prototype is not null)
        {
            Prototype = prototype;
        }
    }

    private IReadOnlyList<Element> Items => _source();
    public int Count => Items.Count;

    public JsValue Item(int index)
    {
        var items = Items;
        return index >= 0 && index < items.Count ? _ctx.Wrappers.Wrap(items[index]) : JsValue.Null;
    }

    public JsValue NamedItemValue(string name)
        => NamedItem(name) is { } e ? _ctx.Wrappers.Wrap(e) : JsValue.Null;

    public JsArray ValuesArray(global::Jint.Engine engine)
    {
        var items = Items;
        var values = new JsValue[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            values[i] = _ctx.Wrappers.Wrap(items[i]);
        }

        return new JsArray(engine, values);
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

    // A named property is shadowed by an own expando or a prototype/built-in
    // (item/namedItem/length/…), per WebIDL named-property visibility.
    private bool IsShadowed(JsValue name)
    {
        if (base.GetOwnProperty(name) != PropertyDescriptor.Undefined)
        {
            return true;
        }

        for (var p = Prototype; p is not null; p = p.Prototype)
        {
            if (p.GetOwnProperty(name) != PropertyDescriptor.Undefined)
            {
                return true;
            }
        }

        return false;
    }

    public override JsValue Get(JsValue property, JsValue receiver)
    {
        if (property.IsString())
        {
            var name = property.AsString();
            if (CollectionIndex.TryIndex(name, out var i))
            {
                var items = Items;
                return i < items.Count ? _ctx.Wrappers.Wrap(items[i]) : JsValue.Undefined;
            }
            if (!IsShadowed(property) && NamedItem(name) is { } named)
            {
                return _ctx.Wrappers.Wrap(named);
            }
        }
        return base.Get(property, receiver);
    }

    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        if (property.IsString())
        {
            var name = property.AsString();
            if (CollectionIndex.TryIndex(name, out var i))
            {
                var items = Items;
                if (i < items.Count)
                {
                    return new PropertyDescriptor(_ctx.Wrappers.Wrap(items[i]), writable: false, enumerable: true, configurable: true);
                }
            }
            else if (base.GetOwnProperty(property) == PropertyDescriptor.Undefined && NamedItem(name) is { } named)
            {
                return new PropertyDescriptor(_ctx.Wrappers.Wrap(named), writable: false, enumerable: true, configurable: true);
            }
        }
        return base.GetOwnProperty(property);
    }

    public override bool HasProperty(JsValue property)
    {
        if (property.IsString())
        {
            var name = property.AsString();
            if (CollectionIndex.TryIndex(name, out var i))
            {
                return i < Items.Count;
            }

            if (!IsShadowed(property) && NamedItem(name) is not null)
            {
                return true;
            }
        }
        return base.HasProperty(property);
    }

    public override List<JsValue> GetOwnPropertyKeys(Types types = Types.String | Types.Symbol)
    {
        var keys = new List<JsValue>();
        if ((types & Types.String) != 0)
        {
            for (var i = 0; i < Items.Count; i++)
            {
                keys.Add(JintInterop.Str(i.ToString(CultureInfo.InvariantCulture)));
            }
        }

        keys.AddRange(base.GetOwnPropertyKeys(types));
        return keys;
    }
}

/// <summary>Live DOMTokenList backing <c>element.classList</c> (DOM §7.1):
/// integer indices resolve to tokens against the element's live class list. The
/// instance carries the token-mutating methods (add/remove/toggle/…); the
/// iteration surface (values/keys/entries/forEach/@@iterator) is inherited from
/// %DOMTokenListPrototype%.</summary>
internal sealed class JintDomTokenListObject : ObjectInstance
{
    private readonly DomTokenList _tokens;

    public JintDomTokenListObject(JintBackendContext ctx, DomTokenList tokens) : base(ctx.Engine)
    {
        _tokens = tokens;
        if (ctx.Wrappers.DomTokenListPrototype is { } p)
        {
            Prototype = p;
        }
    }

    public JsArray ValuesArray(global::Jint.Engine engine)
    {
        var values = new JsValue[_tokens.Count];
        for (var i = 0; i < _tokens.Count; i++)
        {
            values[i] = JintInterop.Str(_tokens[i]);
        }

        return new JsArray(engine, values);
    }

    public override JsValue Get(JsValue property, JsValue receiver)
    {
        if (property.IsString() && CollectionIndex.TryIndex(property.AsString(), out var i))
        {
            return i < _tokens.Count ? JintInterop.Str(_tokens[i]) : JsValue.Undefined;
        }

        return base.Get(property, receiver);
    }

    public override PropertyDescriptor GetOwnProperty(JsValue property)
    {
        if (property.IsString() && CollectionIndex.TryIndex(property.AsString(), out var i) && i < _tokens.Count)
        {
            return new PropertyDescriptor(JintInterop.Str(_tokens[i]), writable: false, enumerable: true, configurable: true);
        }

        return base.GetOwnProperty(property);
    }

    public override bool HasProperty(JsValue property)
    {
        if (property.IsString() && CollectionIndex.TryIndex(property.AsString(), out var i) && i < _tokens.Count)
        {
            return true;
        }

        return base.HasProperty(property);
    }

    public override List<JsValue> GetOwnPropertyKeys(Types types = Types.String | Types.Symbol)
    {
        var keys = new List<JsValue>();
        if ((types & Types.String) != 0)
        {
            for (var i = 0; i < _tokens.Count; i++)
            {
                keys.Add(JintInterop.Str(i.ToString(CultureInfo.InvariantCulture)));
            }
        }

        keys.AddRange(base.GetOwnPropertyKeys(types));
        return keys;
    }
}

/// <summary>WebIDL "array index" parsing shared by the collection objects: a
/// canonical non-negative integer string in [0, 2^32-2], no leading zeros.</summary>
internal static class CollectionIndex
{
    public static bool TryIndex(string name, out int index)
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

        if (v > 4294967294UL)
        {
            return false;
        }

        index = v > int.MaxValue ? int.MaxValue : (int)v;
        return true;
    }
}
